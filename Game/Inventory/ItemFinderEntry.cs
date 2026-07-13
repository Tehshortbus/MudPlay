using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using FujinTerm.Game;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Inventory;

// One equippable item projected into the searchable columns the Item Finder
// lists — slot, type, the wear requirements (level / strength), the item's weight,
// the worn-stat totals (HP / mana / regens / damage / accuracy / crits / hit-magic /
// backstab / AC / DR / illumination), and — for weapons, when a character swing
// context is supplied — the modelled 10-round swing average. The numbers come from the same aggregation
// Character Info uses (CharacterCalculator.AggregateItemRow), so a row's
// AC/DR/bonuses match what the item grants once worn. Row is retained so the
// finder's class / level / alignment filter can defer to ItemEquipFilter.CanEquip.
//
// Build the whole catalog with BuildCatalog — it enumerates the active set's
// Items table once, keeps the rows that resolve to an EquipmentSlot, and returns
// them sorted by slot then name (the finder's default order). Two facts the
// aggregation summary doesn't surface — the Abil-135 min-level wear gate and
// Abil-116 backstab capability — are read in a single extra Abil pass per item.
public sealed record ItemFinderEntry
{
    // Items.ItemType codes the catalog keys on (weapons surface their base damage;
    // armour surfaces its tier). Other equippable types (jewellery, lights) have neither.
    private const int ArmourItemType = 0;
    private const int WeaponItemType = 1;

    // Abil-N codes whose facts the worn-stat summary doesn't expose.
    private const int MinLevelAbil = 135;   // AbilVal holds the level gate.
    private const int BackstabAbil = 116;   // presence ⇒ the weapon can backstab.

    // Item name — the catalog's secondary sort key and the grid's Name column.
    public required string Name { get; init; }

    // The slot the item occupies; the catalog's primary grouping / sort key.
    public required EquipmentSlot Slot { get; init; }

    // Short slot label for the grid (e.g. "Off-Hand", "Finger (1)").
    public required string SlotLabel { get; init; }

    // Numeric slot rank (EquipmentSlot order) for slot-column sorting.
    public int SlotOrder => (int)Slot;

    // Weapon-type label ("1H Sharp" …), or null when the item isn't a weapon.
    public string? WeaponTypeLabel { get; init; }

    // Armour-type label ("Platemail" …), or null when the item isn't armour.
    public string? ArmourTypeLabel { get; init; }

    // Weapon-type code, or -1 when the item isn't a weapon.
    public int WeaponType { get; init; }

    // Armour-type code, or -1 when the item isn't armour.
    public int ArmourType { get; init; }

    // The combined type label shown in the grid — weapon type, else armour type.
    public string TypeLabel => WeaponTypeLabel ?? ArmourTypeLabel ?? string.Empty;

    // Minimum character level to wear it (Abil-135), 0 when ungated.
    public int LevelReq { get; init; }

    // Strength the item requires (weapon StrReq), 0 when none.
    public int StrReq { get; init; }

    // Item weight (Items."Encum"), the encumbrance it adds to the carry load.
    public int Encum { get; init; }

    // Weapon base minimum damage, 0 for non-weapons.
    public int MinDmg { get; init; }

    // Weapon base maximum damage, 0 for non-weapons.
    public int MaxDmg { get; init; }

    // Accuracy the item grants (base Accy + the Abil-22/105/106 sum).
    public int Accuracy { get; init; }

    // Critical-hit bonus (Abil-58).
    public int Crits { get; init; }

    // Hit-magic level (Abil-28/142 sum).
    public int HitMagic { get; init; }

    // True when the item carries a backstab-accuracy ability (Abil-116).
    public bool CanBackstab { get; init; }

    // Backstab accuracy bonus (Abil-116).
    public int BsAccuracy { get; init; }

    // Backstab minimum-damage bonus (Abil-117).
    public int BsMin { get; init; }

    // Backstab maximum-damage bonus (Abil-118).
    public int BsMax { get; init; }

    // Total armour class (base ArmourClass/10 + Abil-2/10).
    public double Ac { get; init; }

    // Total damage resist (base DamageResist/10 + Abil-7/10).
    public double Dr { get; init; }

    // Max-HP bonus (Abil-88).
    public int Hp { get; init; }

    // HP-regen percent bonus (Abil-123).
    public int HpRegen { get; init; }

    // Max-mana bonus (Abil-69).
    public int Mana { get; init; }

    // Mana-regen percent bonus (Abil-145).
    public int ManaRegen { get; init; }

    // Illumination the item radiates when worn (Abil-13 + 14 sum) — a mining helm's
    // +75, a glow-ring's few points. 0 for gear that emits no light.
    public int Illuminate { get; init; }

    // Core attribute bonuses the item grants when worn (Abil 46/44/45/48/47/49).
    public int Strength { get; init; }
    public int Intellect { get; init; }
    public int Willpower { get; init; }
    public int Agility { get; init; }
    public int Health { get; init; }
    public int Charm { get; init; }

    // Flat damage bonuses — the low-end add (Abil 1) and the high-end add (Abil 4).
    public int MinDamageBonus { get; init; }
    public int MaxDamageBonus { get; init; }

    // Spell-damage bonus (Abil 165).
    public int SpellDamage { get; init; }

    // Carry-capacity bonus (Abil 96) — distinct from the item's own weight (Encum).
    public int EncumBonus { get; init; }

    // Thief / caster skill bonuses (Abil 27 / 70 / 67 / 40+179 / 37+180).
    public int Stealth { get; init; }
    public int Spellcasting { get; init; }
    public int Quickness { get; init; }
    public int Traps { get; init; }
    public int Picklocks { get; init; }

    // Alignment protection (Abil 24 / 25).
    public int ProtEvil { get; init; }
    public int ProtGood { get; init; }

    // Elemental / shadow resistances (Abil 3 / 5 / 65 / 66 / 147 / 9).
    public int ColdResist { get; init; }
    public int FireResist { get; init; }
    public int StoneResist { get; init; }
    public int LightningResist { get; init; }
    public int WaterResist { get; init; }
    public int ShadowResist { get; init; }

    // Mean swings per round over a 10-round simulation for the live character
    // wielding this weapon (energy carried forward each round). 0 for non-weapons
    // and when the finder is opened without a usable character context.
    public double AvgSwings { get; init; }

    // True for the bare-handed attack rows (Punch / Kick / Jumpkick) the catalog
    // synthesises under a martial-arts attack type — these aren't real items, so
    // they carry no slot / type / equip data and bypass the item filters.
    public bool IsSynthetic { get; init; }

    // The backing Items row — kept so the finder's character filter can call
    // ItemEquipFilter.CanEquip against the live class / level / alignment. Valid
    // for the lifetime of the cached Items JsonDocument.
    public required JsonElement Row { get; init; }

    // The item's MDB record number — the key the Game Data Browser selects on
    // when the finder double-clicks through to the item's record.
    public int Number => GetInt(Row, "Number");

    // ----- grid display (blank-on-zero so the dense grid stays readable) -----

    // Weapon damage as "min-max", blank for non-weapons.
    public string DamageText => MinDmg != 0 || MaxDmg != 0 ? $"{MinDmg}-{MaxDmg}" : string.Empty;
    public string LevelReqText => Plain(LevelReq);
    public string StrReqText => Plain(StrReq);
    public string EncumText => Plain(Encum);
    public string AccuracyText => Signed(Accuracy);
    public string CritsText => Signed(Crits);
    public string HitMagicText => Signed(HitMagic);
    public string BsAccuracyText => Signed(BsAccuracy);
    public string BsDamageText => BsMin != 0 || BsMax != 0 ? $"{BsMin}-{BsMax}" : string.Empty;
    public string AcText => Decimal(Ac);
    public string DrText => Decimal(Dr);
    public string HpText => Signed(Hp);
    public string HpRegenText => Signed(HpRegen);
    public string ManaText => Signed(Mana);
    public string ManaRegenText => Signed(ManaRegen);
    public string IlluminateText => Signed(Illuminate);
    public string StrengthText => Signed(Strength);
    public string IntellectText => Signed(Intellect);
    public string WillpowerText => Signed(Willpower);
    public string AgilityText => Signed(Agility);
    public string HealthText => Signed(Health);
    public string CharmText => Signed(Charm);
    public string MinDamageBonusText => Signed(MinDamageBonus);
    public string MaxDamageBonusText => Signed(MaxDamageBonus);
    public string SpellDamageText => Signed(SpellDamage);
    public string EncumBonusText => Signed(EncumBonus);
    public string StealthText => Signed(Stealth);
    public string SpellcastingText => Signed(Spellcasting);
    public string QuicknessText => Signed(Quickness);
    public string TrapsText => Signed(Traps);
    public string PicklocksText => Signed(Picklocks);
    public string ProtEvilText => Signed(ProtEvil);
    public string ProtGoodText => Signed(ProtGood);
    public string ColdResistText => Signed(ColdResist);
    public string FireResistText => Signed(FireResist);
    public string StoneResistText => Signed(StoneResist);
    public string LightningResistText => Signed(LightningResist);
    public string WaterResistText => Signed(WaterResist);
    public string ShadowResistText => Signed(ShadowResist);
    public string AvgSwingsText => AvgSwings > 0 ? AvgSwings.ToString("0.0", CultureInfo.InvariantCulture) : string.Empty;

    private static string Plain(int v) => v != 0 ? v.ToString(CultureInfo.InvariantCulture) : string.Empty;
    private static string Signed(int v) => v != 0 ? v.ToString("+0;-0", CultureInfo.InvariantCulture) : string.Empty;
    private static string Decimal(double v) => v != 0 ? v.ToString("0.#", CultureInfo.InvariantCulture) : string.Empty;

    // Project every equippable item in the active set's Items table into a
    // catalog, sorted by slot then name. Items with no resolvable slot (non-equip
    // types, Worn 0) are skipped, as are items the realm doesn't actually put in
    // play (see IsObtainable) and worn-but-consumable types that only map to a slot
    // by coincidence (lights, potions, containers, signs, keys — see the ItemType
    // gate below). Empty when no Items table is loaded. When a swing context is
    // supplied, each weapon carries its modelled 10-round swing average for the
    // given attack type; a martial-arts attack type also appends a single
    // bare-handed attack row (Punch / Kick / Jumpkick), since those don't use a
    // weapon.
    public static IReadOnlyList<ItemFinderEntry> BuildCatalog(
        GameDataCache cache, SwingContext? swing = null, MudAttackType attackType = MudAttackType.Normal)
    {
        ArgumentNullException.ThrowIfNull(cache);
        JsonDocument? doc = cache.GetRawTable("Items");
        if (doc is null) return Array.Empty<ItemFinderEntry>();

        bool martialArts = attackType is MudAttackType.Punch or MudAttackType.Kick or MudAttackType.Jumpkick;

        var list = new List<ItemFinderEntry>();
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            string? name = GetString(row, "Name");
            if (string.IsNullOrEmpty(name)) continue;
            if (!IsObtainable(row)) continue;
            if (EquipmentSlotMap.SlotForItem(row) is not { } slot) continue;

            int itemType = GetInt(row, "ItemType");
            bool isWeapon = itemType == WeaponItemType;
            bool isArmour = itemType == ArmourItemType;

            // Only permanently-equippable gear belongs in the finder — armour
            // (incl. jewellery) and weapons. Lights, potions, containers, signs,
            // projectiles and keys can resolve to a worn slot via their Worn code
            // (a torch reads as a wrist item, a key-amulet as a neck item) but are
            // consumable / limited-use, not real equipment, so they're dropped even
            // though a slot matched.
            if (!isWeapon && !isArmour) continue;

            // The slot tag picks weapon-base vs off-hand vs generic-worn handling
            // inside the aggregation — the same tag InventoryManager emits.
            string slotTag = isWeapon ? "Weapon Hand"
                : slot == EquipmentSlot.OffHand ? "Off-Hand"
                : "Worn";

            EquipmentStatSummary t = CharacterCalculator.AggregateItemRow(row, name, slotTag).Totals;
            (int levelReq, bool backstab) = ScanAbilFacts(row);

            int weaponType = isWeapon ? GetInt(row, "WeaponType") : -1;
            int armourType = isArmour ? GetInt(row, "ArmourType") : -1;

            int strReq = GetInt(row, "StrReq");
            // A martial-arts attack doesn't swing the weapon, so real items carry no
            // swing count under those types — only the appended bare-handed row does.
            double avgSwings = isWeapon && !martialArts && swing is { } ctx
                ? ctx.AvgSwingsFor(GetInt(row, "Speed"), strReq, attackType)
                : 0;

            list.Add(new ItemFinderEntry
            {
                Name = name,
                Slot = slot,
                SlotLabel = EquipmentSlotMap.FamilyLabel(slot),
                WeaponTypeLabel = isWeapon
                    ? LookupEnums.FormatWeaponType(weaponType.ToString(CultureInfo.InvariantCulture))
                    : null,
                ArmourTypeLabel = isArmour
                    ? LookupEnums.FormatArmourType(armourType.ToString(CultureInfo.InvariantCulture))
                    : null,
                WeaponType = weaponType,
                ArmourType = armourType,
                LevelReq = levelReq,
                StrReq = strReq,
                Encum = GetInt(row, "Encum"),
                MinDmg = isWeapon ? t.WeaponMin : 0,
                MaxDmg = isWeapon ? t.WeaponMax : 0,
                Accuracy = t.TotalWornAccy + t.PlusAccuracy,
                Crits = t.PlusCrits,
                HitMagic = t.PlusHitMagic,
                CanBackstab = backstab,
                BsAccuracy = t.PlusBSAccuracy,
                BsMin = t.PlusBSMin,
                BsMax = t.PlusBSMax,
                Ac = t.PlusAC,
                Dr = t.PlusDR,
                Hp = t.PlusMaxHp,
                HpRegen = t.HpRegenPercent,
                Mana = t.PlusMaxMana,
                ManaRegen = t.MpRegenPercent,
                Illuminate = t.PlusIlluminate,
                Strength = t.PlusStrength,
                Intellect = t.PlusIntellect,
                Willpower = t.PlusWillpower,
                Agility = t.PlusAgility,
                Health = t.PlusHealth,
                Charm = t.PlusCharm,
                MinDamageBonus = t.PlusMinDamage,
                MaxDamageBonus = t.PlusMaxDamage,
                SpellDamage = t.SpellDamageBonus,
                EncumBonus = t.PlusEncumbrance,
                Stealth = t.PlusStealth,
                Spellcasting = t.PlusSpellcasting,
                Quickness = t.PlusQuickness,
                Traps = t.PlusTraps,
                Picklocks = t.PlusPicklocks,
                ProtEvil = t.PlusProtEvil,
                ProtGood = t.PlusProtGood,
                ColdResist = t.PlusColdResist,
                FireResist = t.PlusFireResist,
                StoneResist = t.PlusStoneResist,
                LightningResist = t.PlusLightningResist,
                WaterResist = t.PlusWaterResist,
                ShadowResist = t.PlusShadowResist,
                AvgSwings = avgSwings,
                Row = row,
            });
        }

        // A martial-arts attack doesn't swing a weapon, so no real item carries a
        // swing count under it — instead append one bare-handed attack row (Punch /
        // Kick / Jumpkick) modelling that attack's own swing rate. Only with a usable
        // character context; without one there's no rate to show, so nothing is added.
        if (martialArts && swing is { IsUsable: true } maCtx)
            list.Add(BuildMartialArtsRow(attackType, maCtx));

        list.Sort(static (a, b) =>
        {
            int c = a.SlotOrder.CompareTo(b.SlotOrder);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    // One Abil-0..19 pass for the two facts the worn-stat summary leaves out: the
    // min-level wear gate (135, value = level) and backstab capability (116 present).
    private static (int LevelReq, bool Backstab) ScanAbilFacts(JsonElement row)
    {
        int levelReq = 0;
        bool backstab = false;
        for (int i = 0; i < 20; i++)
        {
            int code = GetInt(row, $"Abil-{i}");
            if (code == 0) continue;
            if (code == MinLevelAbil) levelReq = GetInt(row, $"AbilVal-{i}");
            else if (code == BackstabAbil) backstab = true;
        }
        return (levelReq, backstab);
    }

    // Synthesise the single bare-handed attack row for a martial-arts attack type.
    // It's not a real item — no slot, damage, or equip data — just the attack's name
    // and its modelled swing rate, so IsSynthetic lets it bypass every item filter and
    // the double-click-to-record jump. Sorts among weapons (Slot = Weapon) by name.
    private static ItemFinderEntry BuildMartialArtsRow(MudAttackType attackType, SwingContext ctx) => new()
    {
        Name = attackType switch
        {
            MudAttackType.Punch => "Punch",
            MudAttackType.Kick => "Kick",
            MudAttackType.Jumpkick => "Jumpkick",
            _ => attackType.ToString(),
        },
        Slot = EquipmentSlot.Weapon,
        SlotLabel = string.Empty,
        WeaponTypeLabel = "Martial Arts",
        WeaponType = -1,
        ArmourType = -1,
        AvgSwings = ctx.AvgSwingsForMartialArts(attackType),
        IsSynthetic = true,
        Row = default,
    };

    // The MDB conversion stamps "In Game" = 1 on every item the realm actually
    // spawns, sells, drops, or gates behind a quest, and 0 on sysop-only,
    // unimplemented, or duplicate test rows (e.g. "bow of silver", the "large
    // rock" placeholders, "longsword1..5") — even ones a shop merely references
    // as no-generate / buy-only. Those can't be acquired in play, so the finder
    // omits them. The check is defensive: a set predating the flag (property
    // absent, or non-numeric) is treated as obtainable rather than blanked out.
    private static bool IsObtainable(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) return true;
        if (!row.TryGetProperty("In Game", out JsonElement el)) return true;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int v)) return true;
        return v != 0;
    }

    private static int GetInt(JsonElement row, string property)
    {
        if (row.ValueKind != JsonValueKind.Object) return 0;
        if (!row.TryGetProperty(property, out JsonElement el)) return 0;
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v) ? v : 0;
    }

    private static string? GetString(JsonElement row, string property)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        if (!row.TryGetProperty(property, out JsonElement el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    // Live-character inputs the finder needs to model each weapon's swing rate —
    // everything CombatCalculator.CalcSwings consumes except the per-weapon speed
    // and strength requirement, which vary per row. Built once when the finder
    // opens (player stats + class combat level + encumbrance); left null when no
    // character is loaded, in which case the Swings column stays blank.
    public readonly record struct SwingContext(
        int CombatLevel, int Level, int Agility, int Strength,
        int CurrentEncum, int MaxEncum, RealmType Realm)
    {
        // Swing math only makes sense once the character has a real level and a
        // resolved class combat level; below that the model divides toward garbage.
        public bool IsUsable => Level > 0 && CombatLevel > 0;

        // Mean swings per round across the 10-round energy-carry simulation for a
        // weapon of the given speed / strength requirement under a physical attack
        // type, or 0 when unmodellable. Bash doubles the per-swing energy (halving the
        // rate); Smash locks the round to exactly one swing regardless of speed; every
        // other type (Normal / Attack) uses the plain energy-carry model.
        public double AvgSwingsFor(int weaponSpeed, int weaponStrReq,
                                   MudAttackType attackType = MudAttackType.Normal)
        {
            if (!IsUsable || weaponSpeed <= 0) return 0;
            if (attackType == MudAttackType.Smash) return 1; // energy locked to 1000 ⇒ one swing
            SwingCalcResult res = CombatCalculator.CalcSwings(
                CombatLevel, Level, weaponSpeed, Agility, Strength, weaponStrReq,
                CurrentEncum, MaxEncum,
                isBashing: attackType == MudAttackType.Bash, realmType: Realm);
            return AverageSwings(res);
        }

        // Mean swings per round for a bare-handed martial-arts attack, whose fixed
        // attack speed replaces a weapon's and which carries no strength requirement.
        // Speeds are MajorMUD's: Punch 1150, Kick 1400. Jumpkick differs by realm —
        // Stock is faster (1900) than the highest-version Paradigm/GreaterMUD build
        // (2800); those are the two values the app models (older 2900 builds aside).
        public double AvgSwingsForMartialArts(MudAttackType attackType)
        {
            int speed = attackType switch
            {
                MudAttackType.Punch => 1150,
                MudAttackType.Kick => 1400,
                MudAttackType.Jumpkick => Realm == RealmType.ParaMud ? 2800 : 1900,
                _ => 0,
            };
            if (speed <= 0 || !IsUsable) return 0;
            SwingCalcResult res = CombatCalculator.CalcSwings(
                CombatLevel, Level, speed, Agility, Strength, weaponStrReq: 0,
                CurrentEncum, MaxEncum, realmType: Realm);
            return AverageSwings(res);
        }

        private static double AverageSwings(SwingCalcResult res)
        {
            int[] rounds = res.SwingsPerRound;
            if (rounds.Length == 0) return 0;
            int total = 0;
            foreach (int s in rounds) total += s;
            return (double)total / rounds.Length;
        }
    }
}
