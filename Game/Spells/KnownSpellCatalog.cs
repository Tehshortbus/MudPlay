using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using FujinTerm.Game.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Spells;

// Builds the per-class learnable-spell list from the active game-data set's
// Spells + Classes tables. The eligibility filter applies MajorMUD's own
// spell-learnability rules, so the Spell Book shows exactly the spells the game
// would let the character know.
//
// Class identity flows in as the stat-reported class name; we resolve it to the
// Classes.Number (and its MageryType / MageryLVL) before filtering.
//
// Two realm-global flags default to false here: Kai-autolearn disable (Kai
// classes auto-learn unless the realm disables it; maps to the game-data
// Info.DisableKai flag, wired in a follow-up) and GreaterMUD mode. The data-set
// version is assumed >= 1.7 (modern MajorMUD / MegaMUD data), so the
// explicit-class-token check always applies.
public sealed class KnownSpellCatalog
{
    // Magery enum: None=0, Mage=1, Priest=2, Druid=3, Bard=4, Kai=5. Class
    // MageryType values are the same enum, so they compare directly against
    // Spells.Magery.
    private const int MageryNone = 0;
    private const int MageryKai = 5;

    private readonly GameDataCache _cache;

    // Realm-global learnability flags. Not yet wired to game data
    // (Info.DisableKai → DisableKaiAutolearn is a planned follow-up); both
    // default false, matching a stock realm. Non-const so the branches
    // that read them don't trip unreachable-code analysis.
    private readonly bool _disableKaiAutolearn = false;
    private readonly bool _greaterMud = false;

    public KnownSpellCatalog(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    // Resolve a stat-reported class name (e.g. "Mystic") to its
    // Classes.Number. Case-insensitive, trimmed. Returns null when the active
    // set has no class by that name.
    public int? ResolveClassNumber(string className)
    {
        if (string.IsNullOrWhiteSpace(className)) return null;
        JsonDocument? doc = _cache.GetRawTable("Classes");
        if (doc is null) return null;

        string target = className.Trim();
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            string? name = ReadString(row, "Name");
            if (name is not null && string.Equals(name.Trim(), target, StringComparison.OrdinalIgnoreCase))
                return ReadInt(row, "Number");
        }
        return null;
    }

    // Reverse of ResolveClassNumber: resolve a Classes.Number to its display
    // Name. Returns null when the active set has no class with that number.
    // Used by the party-bless picker to compare a slot's stored class numbers
    // against a PartyMember.Class name.
    public string? ResolveClassName(int classNumber)
    {
        if (classNumber < 1) return null;
        JsonDocument? doc = _cache.GetRawTable("Classes");
        if (doc is null) return null;

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
            if (ReadInt(row, "Number") == classNumber)
                return ReadString(row, "Name");
        return null;
    }

    // Returns the class's magery type (the Magery enum value) and emits its
    // magery level. A class number with no matching row resolves to None / 0.
    public int ResolveClassMagery(int classNumber, out int mageryLvl)
    {
        mageryLvl = 0;
        if (classNumber < 1) return MageryNone;
        JsonDocument? doc = _cache.GetRawTable("Classes");
        if (doc is null) return MageryNone;

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (ReadInt(row, "Number") != classNumber) continue;
            mageryLvl = ReadInt(row, "MageryLVL");
            return ReadInt(row, "MageryType");
        }
        return MageryNone;
    }

    // Every spell classNumber can learn, gated to level (pass 0 for the full
    // list ignoring the level requirement). Sorted by ReqLevel then Name. Empty
    // for classes with no magery (Warriors, Thieves, etc.).
    public IReadOnlyList<KnownSpell> Query(int classNumber, int level, int charAlign = 0)
    {
        List<KnownSpell> results = new();
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return results;

        int classMagery = ResolveClassMagery(classNumber, out int classMageryLvl);
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!IsUsable(row, classNumber, classMagery, classMageryLvl, level, charAlign, andLearnable: true))
                continue;
            results.Add(ToKnownSpell(row));
        }

        results.Sort(static (a, b) =>
        {
            int byLevel = a.ReqLevel.CompareTo(b.ReqLevel);
            return byLevel != 0 ? byLevel : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return results;
    }

    // Look up a single learnable spell by its Short cast-code for the given
    // class. Returns null when no usable spell matches. level 0 ignores the
    // level gate.
    public KnownSpell? GetByShort(string shortCode, int classNumber, int level = 0, int charAlign = 0)
    {
        if (string.IsNullOrWhiteSpace(shortCode)) return null;
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return null;

        string target = shortCode.Trim();
        int classMagery = ResolveClassMagery(classNumber, out int classMageryLvl);
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            string? s = ReadString(row, "Short");
            if (s is null || !string.Equals(s.Trim(), target, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsUsable(row, classNumber, classMagery, classMageryLvl, level, charAlign, andLearnable: true))
                continue;
            return ToKnownSpell(row);
        }
        return null;
    }

    // Look up a single learnable spell by its full Name for the given class.
    // The spells / pow list rows and the learn-scroll line ("…learn the spell
    // harm.") both report the spell's Name rather than its Short code, so the
    // obtained-signal path resolves through here. Returns null when no usable
    // spell matches. level 0 ignores the level gate.
    public KnownSpell? GetByName(string name, int classNumber, int level = 0, int charAlign = 0)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return null;

        string target = name.Trim();
        int classMagery = ResolveClassMagery(classNumber, out int classMageryLvl);
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            string? n = ReadString(row, "Name");
            if (n is null || !string.Equals(n.Trim(), target, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsUsable(row, classNumber, classMagery, classMageryLvl, level, charAlign, andLearnable: true))
                continue;
            return ToKnownSpell(row);
        }
        return null;
    }

    // Resolve a Spells.Number to its full Name across the entire Spells table
    // (no class / magery / learnable filter). Used to render the RemovesSpell
    // (Abil 122) target by name, which can reference any spell, not just the
    // active class's learnable list. Returns null when no row has that number.
    public string? GetSpellNameByNumber(int spellNumber)
    {
        if (spellNumber < 1) return null;
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return null;

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (ReadInt(row, "Number") != spellNumber) continue;
            return ReadString(row, "Name");
        }
        return null;
    }

    // Resolve a Spells.Number to its SpellFormulaInput across the entire Spells
    // table (no class / magery / learnable filter). Used by the Game Data Items
    // pane to render a weapon's use-cast / proc spell effect without depending
    // on a class spell book. Returns null when no row has that number.
    public SpellFormulaInput? GetFormulaByNumber(int spellNumber)
    {
        if (spellNumber < 1) return null;
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return null;

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (ReadInt(row, "Number") != spellNumber) continue;
            return ToFormula(row);
        }
        return null;
    }

    // Build the reverse index from a TBInfo textblock number to every spell
    // that textblock casts, parsed from each spell row's denormalised
    // "Casted By" column (e.g. "Textblock #2910, Textblock #2911"). The Spell
    // Book uses it to expand an Abil-148 (TextBlock) reference into the real
    // effect(s) the textblock applies — duration + stat bonuses for transform
    // spells like "form of the dragon" — instead of an opaque record number.
    // General across data sets: any spell whose "Casted By" names a textblock
    // is linked, with no per-spell special-casing. Empty when the active set
    // has no Spells table.
    public IReadOnlyDictionary<int, IReadOnlyList<KnownSpell>> BuildCastByTextblockIndex()
    {
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return EmptyCastIndex;

        Dictionary<int, List<KnownSpell>> map = new();
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            string? castedBy = ReadString(row, "Casted By");
            if (castedBy is null) continue;

            KnownSpell? projected = null;
            foreach (Match m in CastedByTextblock.Matches(castedBy))
            {
                if (!int.TryParse(m.Groups[1].Value, out int tb) || tb <= 0) continue;
                projected ??= ToKnownSpell(row);
                if (!map.TryGetValue(tb, out List<KnownSpell>? list))
                    map[tb] = list = new List<KnownSpell>();
                list.Add(projected.Value);
            }
        }

        Dictionary<int, IReadOnlyList<KnownSpell>> result = new(map.Count);
        foreach (KeyValuePair<int, List<KnownSpell>> kv in map) result[kv.Key] = kv.Value;
        return result;
    }

    private static readonly IReadOnlyDictionary<int, IReadOnlyList<KnownSpell>> EmptyCastIndex
        = new Dictionary<int, IReadOnlyList<KnownSpell>>();

    // MajorMUD ability code for a cast-on-use spell ("CastsSp"): the slot's
    // AbilVal is the Spells.Number the item casts when used.
    private const int CastsSpAbilityCode = 43;

    // Items carry 20 ability slots (Abil-0..19) vs the 10 on Spells / Classes.
    private const int ItemAbilSlots = 20;

    // Items denormalise their class restriction into 10 ClassRest-N slots,
    // each a Classes.Number that may use the item.
    private const int ItemClassRestSlots = 10;

    // A spellbook cast-on-use item must be equippable in one of the player's
    // equipment slots (MegaMUD's Hold panel). Weapons (ItemType 1) ready into the
    // Weapon slot; everything else is gated on a real Worn slot. Worn 0 (Nowhere)
    // and 1 (Everywhere) aren't equip slots, so non-equippable "use" items —
    // potions, food, containers/chests, scrolls, projectiles, and room Sign items
    // like the dark plated warchest — are excluded: you drink/eat/open/read those,
    // you don't ready them to cast a buff. Worn 2..19 spans Head…Face plus
    // Off-Hand and the generic Worn slot.
    private const int WeaponItemType = 1;
    private const int FirstWornSlot = 2;
    private const int LastWornSlot = 19;

    // Every cast-on-use item the class can use — an Items row that both
    // (a) is usable by classNumber via its ClassRest-0..9 restriction (no
    // entries => universal) and (b) carries a CastsSp ability (code 43) naming
    // a spell. Each result resolves the cast Spells.Name and the item's
    // UseCount charges (0 = unlimited). Sorted by item name then spell name.
    // Empty when no class is set, the set has no Items table, or nothing
    // matches.
    public IReadOnlyList<ClassCastItem> GetClassCastItems(int classNumber)
    {
        List<ClassCastItem> results = new();
        if (classNumber < 1) return results;

        JsonDocument? items = _cache.GetRawTable("Items");
        if (items is null) return results;

        Dictionary<int, (string Name, int ManaCost)>? spellInfo = null; // built lazily on first cast item
        foreach (JsonElement row in items.RootElement.EnumerateArray())
        {
            if (!ItemUsableByClass(row, classNumber)) continue;
            if (!IsEquippableCastItem(row)) continue;

            // First CastsSp (code 43) slot wins — an item casts a single use-spell.
            int spellNumber = 0;
            for (int i = 0; i < ItemAbilSlots; i++)
            {
                if (ReadInt(row, $"Abil-{i}") != CastsSpAbilityCode) continue;
                spellNumber = ReadInt(row, $"AbilVal-{i}");
                break;
            }
            if (spellNumber <= 0) continue;

            string itemName = ReadString(row, "Name")?.Trim() ?? string.Empty;
            if (itemName.Length == 0) continue;

            spellInfo ??= BuildSpellInfoMap();
            (string spellName, int manaCost) = spellInfo.TryGetValue(spellNumber, out (string Name, int ManaCost) info)
                ? info
                : (string.Empty, 0);

            results.Add(new ClassCastItem(
                ItemNumber: ReadInt(row, "Number"),
                ItemName: itemName,
                SpellNumber: spellNumber,
                SpellName: spellName,
                ManaCost: manaCost,
                UseCount: ReadInt(row, "UseCount"),
                IsTwoHanded: LookupEnums.IsTwoHandedWeaponType(ReadInt(row, "WeaponType")),
                ClassRestricted: ItemClassRestricted(row, classNumber)));
        }

        results.Sort(static (a, b) =>
        {
            int byItem = string.Compare(a.ItemName, b.ItemName, StringComparison.OrdinalIgnoreCase);
            return byItem != 0 ? byItem : string.Compare(a.SpellName, b.SpellName, StringComparison.OrdinalIgnoreCase);
        });
        return results;
    }

    // Item class-usability: an item with no non-zero ClassRest-N slot is
    // universal (usable by every class); otherwise it's restricted to the
    // listed class numbers.
    private static bool ItemUsableByClass(JsonElement row, int classNumber)
    {
        bool anyRestriction = false;
        for (int i = 0; i < ItemClassRestSlots; i++)
        {
            int c = ReadInt(row, $"ClassRest-{i}");
            if (c == 0) continue;
            anyRestriction = true;
            if (c == classNumber) return true;
        }
        return !anyRestriction;
    }

    // True when the item names classNumber in an explicit ClassRest slot — a
    // class-specific cast source, as opposed to a universal item any class can
    // use. Distinct from ItemUsableByClass (which also passes universal items):
    // the Spell Book surfaces only the class-specific ones so a caster isn't
    // buried under every generic wand / scroll in the game.
    private static bool ItemClassRestricted(JsonElement row, int classNumber)
    {
        for (int i = 0; i < ItemClassRestSlots; i++)
            if (ReadInt(row, $"ClassRest-{i}") == classNumber) return true;
        return false;
    }

    // True when an item can be equipped in one of the player's equipment slots,
    // the precondition for using it to cast: a weapon (readied into the Weapon
    // slot) or an item worn in a real body / Off-Hand / Worn slot. Excludes
    // non-equippable "use" items (potions, food, containers, scrolls,
    // projectiles, room Signs) that can't be readied to cast a buff.
    private static bool IsEquippableCastItem(JsonElement row)
    {
        if (ReadInt(row, "ItemType") == WeaponItemType) return true;
        int worn = ReadInt(row, "Worn");
        return worn is >= FirstWornSlot and <= LastWornSlot;
    }

    // One-pass Spells.Number → (Name, ManaCost) map for resolving cast-item
    // spells (display name + whether using the item draws mana) without a
    // per-item table scan.
    private Dictionary<int, (string Name, int ManaCost)> BuildSpellInfoMap()
    {
        Dictionary<int, (string Name, int ManaCost)> map = new();
        JsonDocument? spells = _cache.GetRawTable("Spells");
        if (spells is null) return map;
        foreach (JsonElement row in spells.RootElement.EnumerateArray())
        {
            int number = ReadInt(row, "Number");
            if (number <= 0) continue;
            string name = ReadString(row, "Name")?.Trim() ?? string.Empty;
            map[number] = (name, ReadInt(row, "ManaCost"));
        }
        return map;
    }

    // "Casted By" lists each source as "Textblock #<number>" (comma-joined).
    // \d+ captures the full number so "#291" never partial-matches "#2910".
    private static readonly Regex CastedByTextblock =
        new(@"Textblock\s*#(\d+)", RegexOptions.IgnoreCase);

    // MajorMUD's spell-usability rule: can classNumber use this spell at level
    // / charAlign, and (when andLearnable) is it actually learnable. classMagery
    // / classMageryLvl are the pre-resolved magery type / level for
    // classNumber.
    private bool IsUsable(
        JsonElement row, int classNumber, int classMagery, int classMageryLvl,
        int level, int charAlign, bool andLearnable)
    {
        if (classNumber < 1) return true;       // no class → treat as usable
        if (level < 0) level = 0;
        if (charAlign < 0) charAlign = 0;

        int spellMagery = ReadInt(row, "Magery");
        int spellMageryLvl = ReadInt(row, "MageryLVL");
        int reqLevel = ReadInt(row, "ReqLevel");
        int learnable = ReadInt(row, "Learnable");
        string? learnedFrom = ReadString(row, "Learned From");
        string? classes = ReadString(row, "Classes");

        // Learnable gate: a spell is learnable when Learnable==1, OR it has a
        // LearnedFrom source (length >= 5), OR it's a Kai spell with autolearn
        // enabled and a real level requirement.
        if (andLearnable)
        {
            bool notLearnable = learnable == 0
                && (learnedFrom?.Length ?? 0) < 5
                && (spellMagery != MageryKai
                    || (spellMagery == MageryKai && (_disableKaiAutolearn || reqLevel < 1)));
            if (notLearnable) return false;
        }

        // Magery check (skipped entirely when the spell has no magery).
        if (spellMagery != 0)
        {
            if (classMagery == MageryNone) return false;
            // The magery-mismatch exception requires Spells.Magery==0, which is
            // impossible inside this branch — so a mismatch is always
            // disqualifying.
            if (classMagery != spellMagery) return false;
            if (classMageryLvl > 0 && classMageryLvl < spellMageryLvl) return false;
            if (classMagery != MageryKai && learnable == 0) return false;
            if (classMagery == MageryKai && _disableKaiAutolearn && learnable == 0) return false;
        }

        // Explicit class-token restriction (data version assumed >= 1.7). A
        // non-"(*)" Classes field limits the spell to the listed class numbers.
        if (classes is not null && classes.Length > 2 && classes != "(*)")
        {
            if (!classes.Contains($"({classNumber})", StringComparison.OrdinalIgnoreCase)) return false;
        }

        if (level > 0 && level < reqLevel) return false;

        // Alignment-restriction abilities (only evaluated when a character
        // alignment is supplied, or for Kai under GreaterMUD).
        if (charAlign > 0 || (classMagery == MageryKai && _greaterMud))
        {
            for (int x = 0; x < 10; x++)
            {
                int code = ReadInt(row, $"Abil-{x}");
                switch (code)
                {
                    case 97 or 98 or 112: // requires good / evil / neutral
                        if (charAlign == 1 && code != 97) return false;
                        if (charAlign == 2 && code != 112) return false;
                        if (charAlign == 3 && code != 98) return false;
                        break;
                    case 110 or 111 or 113: // requires NOT good / evil / neutral
                        if (charAlign == 1 && code == 110) return false;
                        if (charAlign == 2 && code == 113) return false;
                        if (charAlign == 3 && code == 111) return false;
                        break;
                }
            }
        }

        return true;
    }

    // Project a Spells row into a KnownSpell + its formula inputs.
    private static KnownSpell ToKnownSpell(JsonElement row)
        => new(
            Number: ReadInt(row, "Number"),
            Short: ReadString(row, "Short") ?? string.Empty,
            Name: ReadString(row, "Name") ?? string.Empty,
            Magery: ReadInt(row, "Magery"),
            MageryLvl: ReadInt(row, "MageryLVL"),
            ReqLevel: ReadInt(row, "ReqLevel"),
            Targets: ReadInt(row, "Targets"),
            Formula: ToFormula(row));

    private static SpellFormulaInput ToFormula(JsonElement row)
    {
        List<SpellAbility> abilities = new();
        for (int x = 0; x < 10; x++)
        {
            int code = ReadInt(row, $"Abil-{x}");
            if (code == 0) continue; // empty slot — the calculator ignores code 0 anyway
            abilities.Add(new SpellAbility(code, ReadInt(row, $"AbilVal-{x}")));
        }

        return new SpellFormulaInput
        {
            Number = ReadInt(row, "Number"),
            MinBase = ReadInt(row, "MinBase"),
            MinInc = ReadInt(row, "MinInc"),
            MinIncLVLs = ReadInt(row, "MinIncLVLs"),
            MaxBase = ReadInt(row, "MaxBase"),
            MaxInc = ReadInt(row, "MaxInc"),
            MaxIncLVLs = ReadInt(row, "MaxIncLVLs"),
            Dur = ReadInt(row, "Dur"),
            DurInc = ReadInt(row, "DurInc"),
            DurIncLVLs = ReadInt(row, "DurIncLVLs"),
            Cap = ReadInt(row, "Cap"),
            ReqLevel = ReadInt(row, "ReqLevel"),
            EnergyCost = ReadInt(row, "EnergyCost"),
            ManaCost = ReadInt(row, "ManaCost"),
            Abilities = abilities,
        };
    }

    // ----- NUL-aware JSON readers ---------------------------------------
    // The MdbImporter writes a literal NUL char ("\0") for empty Jet text
    // columns, so a plain GetString can hand back "\0" rather than null.
    // These mirror the same sentinel handling SpellCoverageAuditor uses.

    private static string? ReadString(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out JsonElement el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        string? raw = el.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (char c in raw)
            if (c != '\0' && !char.IsWhiteSpace(c)) return raw;
        return null;
    }

    private static int ReadInt(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out JsonElement el)) return 0;
        if (el.ValueKind != JsonValueKind.Number) return 0;
        return el.TryGetInt32(out int n) ? n : 0;
    }
}
