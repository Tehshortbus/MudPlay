using System.IO;
using System.Linq;
using MudPlay.Game.Combat;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins MonsterCatalog — the typed, parsed-once view of the Monsters table.
// Covers raw-field fidelity (no silent slot truncation),
// the byte-boolean Undead quirk, the pre-resolved elemental resist/Magical/
// SpellImmu/Dodge/NonLiving fields (must match MonsterResistIndex /
// MonsterMagicIndex / MonsterLifeIndex exactly, since those stay authoritative
// until a later consolidation), the mid-spell cumulative-to-delta decode, and
// the new spell-cast elemental rollup.
public sealed class MonsterCatalogTests : IDisposable
{
    private readonly string _root;

    public MonsterCatalogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-monster-catalog-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 501 fireball — flat 12–40 damage (code 1), 500 energy: the per-round getter
    //   would double this, so it pins that a monster's single cast does NOT.
    // 502 icebolt — level-scaled 4+2L / 8+2L damage, 250 energy (per-round 4×).
    // 503 turn undead — no damage ability (a pure effect spell → 0 damage).
    // 504 plague — poison, no damage ability.
    private const string Spells = """
        [
          { "Number": 501, "Name": "fireball",   "AttType": 1, "MinBase": 12, "MaxBase": 40,
            "EnergyCost": 500, "Abil-0": 1, "AbilVal-0": 0 },
          { "Number": 502, "Name": "icebolt",     "AttType": 0,
            "MinBase": 4, "MinInc": 2, "MinIncLVLs": 1, "MaxBase": 8, "MaxInc": 2, "MaxIncLVLs": 1,
            "EnergyCost": 250, "Abil-0": 1, "AbilVal-0": 0 },
          { "Number": 503, "Name": "turn undead", "AttType": 4 },
          { "Number": 504, "Name": "plague",      "AttType": 6, "Abil-0": 19, "AbilVal-0": 0 }
        ]
        """;

    // #1 "giant rat" — plain scalar fields, one physical attack slot, one drop.
    // #2 "acid slime" — Undead stored as the MDB's 255 (True), a spell-attack
    //    slot (Type 2, Accuracy holds spell 501 fireball) and a mid-spell slot
    //    (502 icebolt) so CastsElements should read {Fire, Cold}; also carries
    //    Resist-Fire 50, Magical 3, SpellImmu 5, Dodge 12, NonLiving.
    // #3 "giant rat" (again) — a second record sharing #1's name, so a duplicate
    //    display name across two numbers is exercised in the parsed set.
    // #4 "ghost" — casts only a Normal (4) attack spell (turn undead) and a
    //    Poison (6) mid-spell (plague) — CastsElements should read {Poison}
    //    only (Normal excluded), and an unresolved spell number (999) in a
    //    third attack slot must not blow up or add anything.
    private const string Monsters = """
        [
          { "Number": 1, "Name": "giant rat", "Type": 0, "Align": 3, "Undead": 0,
            "EXP": 10, "ExpMulti": 1, "RegenTime": 1.5, "HP": 20, "ArmourClass": 2,
            "AttType-0": 1, "AttName-0": "bites you", "Att%-0": 100, "AttTrue%-0": 100,
            "AttMin-0": 1, "AttMax-0": 3, "AttAcc-0": 40, "AttEnergy-0": 500,
            "DropItem-0": 900, "DropItem%-0": 25 },
          { "Number": 2, "Name": "acid slime", "Undead": 255,
            "EXP": 65000, "ExpMulti": 40,
            "AttType-0": 2, "Att%-0": 100, "AttTrue%-0": 100, "AttAcc-0": 501, "AttMax-0": 5, "AttMin-0": 80,
            "MidSpell-0": 502, "MidSpell%-0": 30, "MidSpellLVL-0": 4,
            "AttType-1": 0,
            "Abil-0": 5,   "AbilVal-0": 50,
            "Abil-1": 28,  "AbilVal-1": 3,
            "Abil-2": 139, "AbilVal-2": 5,
            "Abil-3": 34,  "AbilVal-3": 12,
            "Abil-4": 109, "AbilVal-4": 0 },
          { "Number": 3, "Name": "giant rat", "HP": 5 },
          { "Number": 4, "Name": "ghost",
            "AttType-0": 2, "Att%-0": 100, "AttAcc-0": 503,
            "AttType-1": 2, "Att%-1": 50,  "AttAcc-1": 999,
            "MidSpell-0": 504, "MidSpell%-0": 20 }
        ]
        """;

    private MonsterCatalog NewCatalog()
    {
        string set = "alpha";
        Directory.CreateDirectory(Path.Combine(_root, set));
        File.WriteAllText(Path.Combine(_root, set, "Monsters.json"), Monsters);
        File.WriteAllText(Path.Combine(_root, set, "Spells.json"), Spells);
        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        return new MonsterCatalog(cache);
    }

    [Fact]
    public void Get_ParsesScalarFields()
    {
        MonsterCatalogEntry rat = NewCatalog().Get(1)!;
        Assert.Equal("giant rat", rat.Name);
        Assert.Equal(10, rat.Exp);
        Assert.Equal(1.5, rat.RegenTime);
        Assert.Equal(20, rat.Hp);
        Assert.Equal(2, rat.ArmourClass);
    }

    // True experience is EXP × ExpMulti (default 1) — the raw EXP alone
    // undercounts a multiplier monster (aged earth dragon read 65000, not 2.6M).
    [Fact]
    public void EffectiveExp_MultipliesExpByExpMulti()
    {
        MonsterCatalog catalog = NewCatalog();
        Assert.Equal(10, catalog.Get(1)!.EffectiveExp);               // 10 × 1
        Assert.Equal(2_600_000, catalog.Get(2)!.EffectiveExp);        // 65000 × 40
        Assert.Equal(0, catalog.Get(3)!.EffectiveExp);                // no EXP / no multiplier → 0
    }

    [Fact]
    public void Get_UnknownNumber_ReturnsNull()
        => Assert.Null(NewCatalog().Get(12345));

    // The MDB stores Boolean True as -1, which arrives from mdb-json as 255 —
    // the same trap MonsterLifeIndex documents. Must read as undead, not "not 1".
    [Fact]
    public void Get_UndeadByteBoolean_ReadsTrueFrom255()
        => Assert.True(NewCatalog().Get(2)!.Undead);

    [Fact]
    public void Get_UndeadZero_ReadsFalse()
        => Assert.False(NewCatalog().Get(1)!.Undead);

    [Fact]
    public void Get_PhysicalAttackSlot_ParsesAllFields()
    {
        MonsterAttackSlot slot = Assert.Single(NewCatalog().Get(1)!.Attacks);
        Assert.Equal("bites you", slot.Name);
        Assert.Equal(1, slot.Type);
        Assert.Equal(1, slot.MinDamage);
        Assert.Equal(3, slot.MaxDamage);
        Assert.Equal(40, slot.Accuracy);
        Assert.Equal(500, slot.Energy);
    }

    [Fact]
    public void Get_UnusedAttackSlots_AreExcluded()
    {
        // #2 only has slot 0 populated (Type 2) and an explicit Type-1 = 0.
        MonsterAttackSlot slot = Assert.Single(NewCatalog().Get(2)!.Attacks);
        Assert.Equal(2, slot.Type);
    }

    [Fact]
    public void Get_DropSlot_ParsesItemAndPercent()
    {
        MonsterDropSlot drop = Assert.Single(NewCatalog().Get(1)!.Drops);
        Assert.Equal(900, drop.ItemId);
        Assert.Equal(25, drop.Percent);
    }

    // MidSpell%-N is a cumulative threshold; the catalog must resolve the
    // per-slot delta, mirroring MonsterMdbInfoBuilder's existing decode.
    [Fact]
    public void Get_MidSpell_ResolvesCumulativeToDelta()
    {
        MonsterMidSpellSlot mid = Assert.Single(NewCatalog().Get(2)!.MidSpells);
        Assert.Equal(502, mid.SpellId);
        Assert.Equal(30, mid.Percent);   // single slot: delta == threshold
        Assert.Equal(4, mid.Level);
    }

    // A spell attack resolves its single-cast damage (linked spell scaled to the
    // slot's cast level) WITHOUT the player per-round energy fold: #2's fireball
    // slot casts at level 5 for a flat 12–40 (500-energy would double per round).
    [Fact]
    public void Get_SpellAttackSlot_ResolvesSingleCastDamage()
    {
        MonsterAttackSlot slot = Assert.Single(NewCatalog().Get(2)!.Attacks);
        Assert.Equal(2, slot.Type);
        Assert.Equal(12, slot.SpellDmgMin);
        Assert.Equal(40, slot.SpellDmgMax);
    }

    // The mid-spell scales to its own cast level (icebolt at level 4 → 4+2·4=12,
    // 8+2·4=16), again single cast (250-energy would 4× per round).
    [Fact]
    public void Get_MidSpell_ResolvesScaledSingleCastDamage()
    {
        MonsterMidSpellSlot mid = Assert.Single(NewCatalog().Get(2)!.MidSpells);
        Assert.Equal(12, mid.DmgMin);
        Assert.Equal(16, mid.DmgMax);
    }

    // A physical slot carries no spell damage, and a spell that deals no direct
    // damage (turn undead) or is unresolved (999) resolves to 0 — no bogus range.
    [Fact]
    public void Get_NonDamageAndPhysicalSlots_CarryNoSpellDamage()
    {
        MonsterAttackSlot physical = Assert.Single(NewCatalog().Get(1)!.Attacks);
        Assert.Equal(0, physical.SpellDmgMax);

        MonsterCatalogEntry ghost = NewCatalog().Get(4)!;
        foreach (MonsterAttackSlot a in ghost.Attacks) Assert.Equal(0, a.SpellDmgMax);
    }

    [Fact]
    public void Get_ElementalResist_MatchesMonsterResistIndexShape()
    {
        MonsterCatalogEntry slime = NewCatalog().Get(2)!;
        Assert.Equal(50, slime.ElementalResists[5]);   // Resist-Fire
        Assert.Single(slime.ElementalResists);
    }

    [Fact]
    public void Get_MagicalSpellImmuDodgeNonLiving_AllResolved()
    {
        MonsterCatalogEntry slime = NewCatalog().Get(2)!;
        Assert.Equal(3, slime.Magical);
        Assert.Equal(5, slime.SpellImmunity);
        Assert.Equal(12, slime.Dodge);
        Assert.True(slime.NonLiving);
    }

    [Fact]
    public void Get_NoAbilities_AllResolvedFieldsAreZeroOrFalse()
    {
        MonsterCatalogEntry rat = NewCatalog().Get(1)!;
        Assert.Empty(rat.ElementalResists);
        Assert.Equal(0, rat.Magical);
        Assert.Equal(0, rat.SpellImmunity);
        Assert.Equal(0, rat.Dodge);
        Assert.False(rat.NonLiving);
    }

    // #2 casts fireball (spell 501, AttType 1 = Fire) via its spell-attack slot
    // and icebolt (502, AttType 0 = Cold) via its mid-spell slot.
    [Fact]
    public void Get_CastsElements_RollsUpAttackAndMidSpellElements()
    {
        MonsterCatalogEntry slime = NewCatalog().Get(2)!;
        Assert.Equal(new[] { "Fire", "Cold" }, slime.CastsElements.OrderBy(s => s == "Fire" ? 0 : 1));
    }

    // #4 casts turn undead (503, AttType 4 = Normal — excluded, not "elemental"),
    // plague (504, AttType 6 = Poison — included), and an unresolved spell
    // number (999) in its second attack slot, which must be silently skipped.
    [Fact]
    public void Get_CastsElements_ExcludesNormalIncludesPoisonSkipsUnresolved()
    {
        MonsterCatalogEntry ghost = NewCatalog().Get(4)!;
        Assert.Equal(new[] { "Poison" }, ghost.CastsElements);
    }

    [Fact]
    public void Get_NoSpellAttacksOrMidSpells_CastsElementsIsEmpty()
        => Assert.Empty(NewCatalog().Get(1)!.CastsElements);

    [Fact]
    public void PhysicalAccuracy_SinglePhysicalSlot_MajorityEqualsMax()
    {
        (int Majority, int Max)? acc = NewCatalog().Get(1)!.PhysicalAccuracy;
        Assert.Equal((40, 40), acc);
    }

    [Fact]
    public void PhysicalAccuracy_SpellOnlyMonster_IsNull()
        => Assert.Null(NewCatalog().Get(2)!.PhysicalAccuracy);

    [Fact]
    public void PrimaryPhysicalAvgDamage_SinglePhysicalSlot_AveragesMinAndMax()
        => Assert.Equal(2, NewCatalog().Get(1)!.PrimaryPhysicalAvgDamage);

    [Fact]
    public void PrimaryPhysicalAvgDamage_SpellOnlyMonster_IsZero()
        => Assert.Equal(0, NewCatalog().Get(2)!.PrimaryPhysicalAvgDamage);

    [Fact]
    public void All_ReturnsEveryParsedMonster()
        => Assert.Equal(4, NewCatalog().All.Count);

    [Fact]
    public void Get_ActiveSetSwitch_RebuildsAgainstNewSet()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Monsters.json"), Monsters);
        File.WriteAllText(Path.Combine(_root, "alpha", "Spells.json"), Spells);
        Directory.CreateDirectory(Path.Combine(_root, "beta"));
        File.WriteAllText(Path.Combine(_root, "beta", "Monsters.json"),
            """[ { "Number": 1, "Name": "other rat" } ]""");

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        MonsterCatalog catalog = new(cache);
        Assert.Equal("giant rat", catalog.Get(1)!.Name);

        cache.SwitchSet("beta");
        Assert.Equal("other rat", catalog.Get(1)!.Name);
    }
}
