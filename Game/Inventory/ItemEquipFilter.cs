using System;
using System.Text.Json;
using FujinTerm.Game.Calculators;
using FujinTerm.Services;

namespace FujinTerm.Game.Inventory;

// Decides whether a character can equip an Items row given their level, class,
// and alignment — the gate behind the Equipment Manager's per-slot item search
// (a Druid shouldn't see a Mage-only staff; a Saint shouldn't see an evil-only
// blade; a Mystic shouldn't see a longsword; a level-3 character shouldn't see a
// level-40 helm). The slot match itself stays with EquipmentSlotMap; this owns
// the "can this character use it" predicate.
//
// The order matters: level / alignment first (off the item's Abil-0..19 codes),
// then — only when the class is known — anti-magic, then the class allow-list,
// then weapon / armour type gating. A class grant (an item Abil-59 ClassOk for
// this class, or a ClassRest-0..9 entry naming it) bypasses the type gating
// entirely; anti-magic (the class's own Abil-51, e.g. Witchunter) blocks a
// magical item (Abil-28) even when class-granted.
//
// All three player dimensions degrade gracefully: an unknown level / class /
// alignment (the character hasn't been stat'd or who'd yet) skips that filter
// rather than hiding gear. Restriction codes are the MajorMUD item ability codes
// (135 MinLevel, 136 MaxLevel, 59 ClassOk, 28 Magical, 97 GoodOnly …
// 113 NotNeutral) read off the item's Abil-N / AbilVal-N pairs.
public static class ItemEquipFilter
{
    private const int ItemAbilSlots = 20;
    private const int ClassRestSlots = 10;
    private const int ClassAbilSlots = 10;

    private const int MinLevelCode = 135;
    private const int MaxLevelCode = 136;
    private const int ClassOkCode = 59;
    private const int MagicalCode = 28;
    private const int GoodOnlyCode = 97;
    private const int EvilOnlyCode = 98;
    private const int NotGoodCode = 110;
    private const int NotEvilCode = 111;
    private const int NeutralOnlyCode = 112;
    private const int NotNeutralCode = 113;

    // Classes.Abil-N code that marks an anti-magic class (e.g. Witchunter).
    private const int AntiMagicClassAbil = 51;

    // Items.ItemType codes the weapon / armour type gates key on.
    private const int ArmourItemType = 0;
    private const int WeaponItemType = 1;

    // Items.Worn code for an off-hand (shield) slot — barred to one-hand-weapon
    // classes unless the item is class-granted.
    private const int ShieldWornCode = 12;

    // The only two weapons a Staff (WeaponType 9) class may hold without a grant.
    private const int StaffDaggerNumber = 68;
    private const int StaffQuarterstaffNumber = 100;

    private enum ClassRestResult { None, Granted, Blocked }

    // Collapse a MajorMUD who alignment title to an AlignmentBucket, or null when
    // the word is unknown / unseen so the caller skips alignment filtering.
    // Matches the canonical ladder (Saint / Lawful / Good → Good; Neutral →
    // Neutral; Seedy / Outlaw / Criminal / Villain / Fiend → Evil),
    // case-insensitively.
    public static AlignmentBucket? BucketForWord(string? whoWord)
    {
        if (string.IsNullOrWhiteSpace(whoWord)) return null;
        return whoWord.Trim().ToLowerInvariant() switch
        {
            "saint" or "lawful" or "good" => AlignmentBucket.Good,
            "neutral" => AlignmentBucket.Neutral,
            "seedy" or "outlaw" or "criminal" or "villain" or "fiend" => AlignmentBucket.Evil,
            _ => null,
        };
    }

    // Resolve a class name (as PlayerStats.Class carries it) to the
    // ClassEquipProfile the predicate needs — its Classes.Number, WeaponType,
    // ArmourType, and anti-magic flag. Returns Unknown when the name is blank or
    // unmatched, which disables all class-dependent gating.
    public static ClassEquipProfile ResolveClassProfile(GameDataCache cache, string? className)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (string.IsNullOrWhiteSpace(className)) return ClassEquipProfile.Unknown;
        if (cache.FindRowByName("Classes", className.Trim()) is not JsonElement r)
            return ClassEquipProfile.Unknown;

        int number = GetInt(r, "Number");
        if (number <= 0) return ClassEquipProfile.Unknown;

        bool antiMagic = false;
        for (int i = 0; i < ClassAbilSlots; i++)
        {
            if (GetInt(r, $"Abil-{i}") == AntiMagicClassAbil) { antiMagic = true; break; }
        }
        return new ClassEquipProfile(number, GetInt(r, "WeaponType"), GetInt(r, "ArmourType"), antiMagic);
    }

    // True when a character of the given level / class / alignment can equip
    // itemRow. A non-positive level, an unknown class profile, or a null
    // alignment bucket disables that dimension's filter.
    public static bool CanEquip(JsonElement itemRow, int level, ClassEquipProfile cls, AlignmentBucket? alignment)
    {
        if (itemRow.ValueKind != JsonValueKind.Object) return false;

        int minLevel = 0, maxLevel = 0;
        bool goodOnly = false, evilOnly = false, neutralOnly = false;
        bool notGood = false, notEvil = false, notNeutral = false;
        bool classOk = false;   // Abil-59 grant for this class — bypasses type gating.
        bool magical = false;   // Abil-28 — blocked for anti-magic classes.

        for (int i = 0; i < ItemAbilSlots; i++)
        {
            int code = GetInt(itemRow, $"Abil-{i}");
            if (code == 0) continue;
            switch (code)
            {
                case MinLevelCode: minLevel = GetInt(itemRow, $"AbilVal-{i}"); break;
                case MaxLevelCode: maxLevel = GetInt(itemRow, $"AbilVal-{i}"); break;
                case ClassOkCode:
                    int grant = GetInt(itemRow, $"AbilVal-{i}");
                    if (grant > 0 && cls.ClassNumber > 0 && grant == cls.ClassNumber) classOk = true;
                    break;
                case MagicalCode: magical = true; break;
                case GoodOnlyCode: goodOnly = true; break;
                case EvilOnlyCode: evilOnly = true; break;
                case NeutralOnlyCode: neutralOnly = true; break;
                case NotGoodCode: notGood = true; break;
                case NotEvilCode: notEvil = true; break;
                case NotNeutralCode: notNeutral = true; break;
            }
        }

        if (!PassesLevel(level, minLevel, maxLevel)) return false;
        if (!PassesAlignment(alignment, goodOnly, evilOnly, neutralOnly, notGood, notEvil, notNeutral))
            return false;

        // No class known ⇒ class / weapon / armour gating is skipped entirely.
        if (cls.ClassNumber <= 0) return true;

        // Anti-magic blocks a magical item even when the class is otherwise granted.
        if (cls.AntiMagic && magical) return false;

        // ClassRest is an allow-list. A match grants (bypassing type gating); entries
        // present with no match hard-block; no entries leaves type gating to decide.
        // Skipped when Abil-59 already granted.
        if (!classOk)
        {
            switch (EvalClassRest(itemRow, cls.ClassNumber))
            {
                case ClassRestResult.Blocked: return false;
                case ClassRestResult.Granted: classOk = true; break;
            }
        }

        // A class grant bypasses weapon / armour type gating entirely.
        if (classOk) return true;

        return PassesItemType(itemRow, cls);
    }

    // 135/136 are the MinLevel/MaxLevel codes — the wear band. A 0 bound (or
    // unknown player level) means "no constraint on that end".
    private static bool PassesLevel(int level, int minLevel, int maxLevel)
    {
        if (level <= 0) return true;
        if (minLevel > 0 && level < minLevel) return false;
        if (maxLevel > 0 && level > maxLevel) return false;
        return true;
    }

    private static bool PassesAlignment(
        AlignmentBucket? bucket,
        bool goodOnly, bool evilOnly, bool neutralOnly,
        bool notGood, bool notEvil, bool notNeutral)
    {
        if (bucket is not AlignmentBucket b) return true;
        if (goodOnly && b != AlignmentBucket.Good) return false;
        if (evilOnly && b != AlignmentBucket.Evil) return false;
        if (neutralOnly && b != AlignmentBucket.Neutral) return false;
        if (notGood && b == AlignmentBucket.Good) return false;
        if (notEvil && b == AlignmentBucket.Evil) return false;
        if (notNeutral && b == AlignmentBucket.Neutral) return false;
        return true;
    }

    // ClassRest-N is the allow-list of class ids. A match (or an item Abil-59 ClassOk,
    // handled by the caller) grants the item and bypasses type gating; entries present
    // with no match block; an empty list is neutral (type gating decides).
    private static ClassRestResult EvalClassRest(JsonElement itemRow, int classNumber)
    {
        bool anyRestriction = false;
        for (int i = 0; i < ClassRestSlots; i++)
        {
            int allowed = GetInt(itemRow, $"ClassRest-{i}");
            if (allowed == 0) continue;
            anyRestriction = true;
            if (allowed == classNumber) return ClassRestResult.Granted;
        }
        return anyRestriction ? ClassRestResult.Blocked : ClassRestResult.None;
    }

    // Reached only for a known class that the item neither grants nor restricts-in.
    // Armour and weapon rows gate by the class's tier; every other ItemType (worn
    // jewellery, etc.) has no type gate.
    private static bool PassesItemType(JsonElement itemRow, ClassEquipProfile cls) =>
        GetInt(itemRow, "ItemType") switch
        {
            ArmourItemType => PassesArmour(itemRow, cls),
            WeaponItemType => PassesWeapon(itemRow, cls),
            _ => true,
        };

    // Class ArmourType is a tier ceiling: usable only when it meets the item's tier.
    // An off-hand shield (Worn 12) is additionally barred to one-hand-weapon classes
    // (WeaponType 0/2/4/9) — only reached when the item isn't class-granted.
    private static bool PassesArmour(JsonElement itemRow, ClassEquipProfile cls)
    {
        if (cls.ArmourType < GetInt(itemRow, "ArmourType")) return false;
        if (GetInt(itemRow, "Worn") == ShieldWornCode && cls.WeaponType is 0 or 2 or 4 or 9)
            return false;
        return true;
    }

    // Class WeaponType selects which item WeaponType families are usable.
    private static bool PassesWeapon(JsonElement itemRow, ClassEquipProfile cls)
    {
        int wt = GetInt(itemRow, "WeaponType");
        return cls.WeaponType switch
        {
            0 => wt == 0,                // 1H Blunt
            1 => wt == 1,                // 2H Blunt
            2 => wt == 2,                // 1H Sharp
            3 => wt == 3,                // 2H Sharp
            4 => wt is 0 or 2,           // Any 1H
            5 => wt is 1 or 3,           // Any 2H
            6 => wt >= 2,                // Any Sharp
            7 => wt <= 1,                // Any Blunt
            8 => true,                   // All Weapons
            9 => IsStaffWeapon(itemRow), // Staff — only dagger / quarterstaff
            _ => true,
        };
    }

    private static bool IsStaffWeapon(JsonElement itemRow) =>
        GetInt(itemRow, "Number") is StaffDaggerNumber or StaffQuarterstaffNumber;

    private static int GetInt(JsonElement row, string property)
    {
        if (row.ValueKind != JsonValueKind.Object) return 0;
        if (!row.TryGetProperty(property, out JsonElement el)) return 0;
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v) ? v : 0;
    }
}
