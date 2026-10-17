using System.Collections.Generic;
using System.Linq;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;
using MudPlay.ViewModels.GameData.Tables;
using Xunit;

namespace MudPlay.Tests;

// Covers the two pure helpers behind the Monsters tab's new columns:
// the lair-tag → monster-id match (for the "Spawns In" map/room list)
// and the synthesised "Accuracy" column.
public sealed class MonstersSectionViewModelTests
{
    // ----- LairNamesMonster ------------------------------------------
    //
    // v1.11p lair tags are "(Max N): id,id,…,[group-index]". The ids are
    // the spawn monsters; the bracketed suffix is the group key.

    [Theory]
    [InlineData(1)]    // first in list
    [InlineData(109)]  // middle
    [InlineData(10)]   // last before the bracket
    public void LairNamesMonster_FindsListedIds(int id)
    {
        Assert.True(MonsterMdbInfoBuilder.LairNamesMonster(
            "(Max 1): 1,2,3,4,5,7,109,6,10,[6-0-5-1]", id));
    }

    [Fact]
    public void LairNamesMonster_RejectsUnlistedId()
    {
        Assert.False(MonsterMdbInfoBuilder.LairNamesMonster(
            "(Max 1): 1,2,3,[6-0-5-1]", 99));
    }

    [Fact]
    public void LairNamesMonster_DoesNotMatchGroupIndexDigits()
    {
        // The "[6-0-5-1]" suffix must not be parsed as monster ids — a
        // tag of ids {1,2} should not report monster 6 just because the
        // group key contains a 6.
        Assert.False(MonsterMdbInfoBuilder.LairNamesMonster(
            "(Max 1): 1,2,[6-0-5-1]", 6));
    }

    [Fact]
    public void LairNamesMonster_DoesNotMatchMaxCount()
    {
        // "(Max 3)" — the 3 is the cap, before the colon, not a monster.
        Assert.False(MonsterMdbInfoBuilder.LairNamesMonster(
            "(Max 3): 1,2,[6-0-5-1]", 3));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\0")]                                 // MDB no-lair sentinel
    [InlineData("[6-1-1][5]Group(lair): 1/602")]            // group-pointer shape, no inline ids
    public void LairNamesMonster_EmptyOrNonListShapes_NoMatch(string? tag)
    {
        Assert.False(MonsterMdbInfoBuilder.LairNamesMonster(tag, 1));
    }

    // ----- ComputeAttackAccuracy -------------------------------------

    [Fact]
    public void ComputeAccuracy_SingleAttack()
    {
        // Slot 0 is the only real physical attack (AttType 1); later slots
        // are empty (AttType 0) even though they carry stale AttAcc values.
        var el = Parse("""
            { "AttType-0": 1, "Att%-0": 100, "AttAcc-0": 10,
              "AttType-1": 0, "Att%-1": 20,  "AttAcc-1": 70 }
            """);
        Assert.Equal("10", MonstersSectionViewModel.ComputeAttackAccuracy(el));
    }

    [Fact]
    public void ComputeAccuracy_ShowsMajorityThenMax()
    {
        // Majority = the highest-chance physical attack's accuracy (att1, 55%
        // true); Max = the highest accuracy across physical attacks (att2, 90).
        var el = Parse("""
            { "AttType-0": 1, "Att%-0": 100, "AttAcc-0": 10, "AttTrue%-0": 20,
              "AttType-1": 1, "Att%-1": 100, "AttAcc-1": 40, "AttTrue%-1": 55,
              "AttType-2": 3, "Att%-2": 100, "AttAcc-2": 90, "AttTrue%-2": 25 }
            """);
        Assert.Equal("40/90", MonstersSectionViewModel.ComputeAttackAccuracy(el));
    }

    [Fact]
    public void ComputeAccuracy_SkipsZeroChanceAndSpellSlots()
    {
        // Slot 0 zero-chance (skipped), slot 1 is a spell attack (AttType 2,
        // AttAcc holds a spell id, not accuracy — skipped), slot 2 real.
        var el = Parse("""
            { "AttType-0": 1, "Att%-0": 0,   "AttAcc-0": 5,
              "AttType-1": 2, "Att%-1": 50,  "AttAcc-1": 999,
              "AttType-2": 3, "Att%-2": 50,  "AttAcc-2": 42 }
            """);
        Assert.Equal("42", MonstersSectionViewModel.ComputeAttackAccuracy(el));
    }

    [Fact]
    public void ComputeAccuracy_NoPhysicalAttack_IsBlank()
    {
        // Spell-only mob (AttType 2) — no physical attack, so blank. The AttAcc-0
        // slot holds a spell id (not an accuracy) and must not be shown.
        var el = Parse("""
            { "AttType-0": 2, "Att%-0": 100, "AttAcc-0": 5811 }
            """);
        Assert.Null(MonstersSectionViewModel.ComputeAttackAccuracy(el));
    }

    // ----- SpellSummons ----------------------------------------------

    [Fact]
    public void SpellSummons_ExplicitAbilityValue_Matches()
    {
        // Summon ability (code 12) names monster 609 directly.
        var el = Parse("""{ "Abil-0": 12, "AbilVal-0": 609, "MinBase": 0 }""");
        Assert.True(MonsterMdbInfoBuilder.SpellSummons(el, 609));
        Assert.False(MonsterMdbInfoBuilder.SpellSummons(el, 1));
    }

    [Fact]
    public void SpellSummons_FallsBackToMinBase_WhenNoAbilityTarget()
    {
        // "raptor summon" shape: summon ability value is 0, so the
        // summoned monster is the spell's MinBase (509 = tetraraptor).
        var el = Parse("""{ "Abil-0": 12, "AbilVal-0": 0, "MinBase": 509 }""");
        Assert.True(MonsterMdbInfoBuilder.SpellSummons(el, 509));
    }

    [Fact]
    public void SpellSummons_AbilityTargetPresent_DoesNotUseMinBase()
    {
        // "summon silver skull" shape: MinBase is 1 (giant rat) but the
        // summon ability points elsewhere (239) — giant rat must NOT match.
        var el = Parse("""{ "Abil-0": 12, "AbilVal-0": 239, "MinBase": 1 }""");
        Assert.False(MonsterMdbInfoBuilder.SpellSummons(el, 1));
        Assert.True(MonsterMdbInfoBuilder.SpellSummons(el, 239));
    }

    [Fact]
    public void SpellSummons_NonSummonSpell_NeverMatches()
    {
        // A damage spell (no Abil 12) whose MinBase happens to equal the
        // monster number must not be treated as a summon.
        var el = Parse("""{ "Abil-0": 1, "AbilVal-0": 0, "MinBase": 509 }""");
        Assert.False(MonsterMdbInfoBuilder.SpellSummons(el, 509));
    }

    [Fact]
    public void SpellSummons_RejectsNonPositiveMonster()
    {
        var el = Parse("""{ "Abil-0": 12, "AbilVal-0": 0, "MinBase": 0 }""");
        Assert.False(MonsterMdbInfoBuilder.SpellSummons(el, 0));
    }

    // ----- SummonContext ---------------------------------------------

    private static readonly System.Collections.Generic.HashSet<int> Ids = new() { 90, 215 };

    [Fact]
    public void SummonContext_DeathSpell()
    {
        var el = Parse("""{ "DeathSpell": 215 }""");
        Assert.Equal("death", MonsterMdbInfoBuilder.SummonContext(el, Ids));
    }

    [Fact]
    public void SummonContext_CreateSpell_IsOnSpawn()
    {
        var el = Parse("""{ "CreateSpell": 90 }""");
        Assert.Equal("on spawn", MonsterMdbInfoBuilder.SummonContext(el, Ids));
    }

    [Theory]
    [InlineData("""{ "AttType-0": 2, "AttAcc-0": 215 }""")]   // spell-attack
    [InlineData("""{ "AttHitSpell-1": 215 }""")]               // hit-spell
    [InlineData("""{ "MidSpell-2": 90 }""")]                    // between-rounds
    public void SummonContext_CombatPaths(string json)
        => Assert.Equal("combat", MonsterMdbInfoBuilder.SummonContext(Parse(json), Ids));

    [Fact]
    public void SummonContext_PhysicalAttackAccuracy_NotMistakenForSpell()
    {
        // AttType 1 (physical) — AttAcc is an ACCURACY value, not a spell
        // id, so a coincidental match (215) must NOT count as a summon.
        var el = Parse("""{ "AttType-0": 1, "AttAcc-0": 215 }""");
        Assert.Null(MonsterMdbInfoBuilder.SummonContext(el, Ids));
    }

    [Fact]
    public void SummonContext_MultipleContexts_Ordered()
    {
        // Hydra shape: casts in combat AND on spawn.
        var el = Parse("""{ "MidSpell-0": 90, "CreateSpell": 90 }""");
        Assert.Equal("combat, on spawn", MonsterMdbInfoBuilder.SummonContext(el, Ids));
    }

    [Fact]
    public void SummonContext_NoSummon_ReturnsNull()
    {
        var el = Parse("""{ "DeathSpell": 7, "MidSpell-0": 8 }""");
        Assert.Null(MonsterMdbInfoBuilder.SummonContext(el, Ids));
    }

    // ----- SummonTargets (forward) -----------------------------------

    [Fact]
    public void SummonTargets_ExplicitValueWins()
        => Assert.Equal(new[] { 239 },
            MonsterMdbInfoBuilder.SummonTargets(Parse("""{ "Abil-0": 12, "AbilVal-0": 239, "MinBase": 1 }""")));

    [Fact]
    public void SummonTargets_FallsBackToMinBase()
        => Assert.Equal(new[] { 509 },
            MonsterMdbInfoBuilder.SummonTargets(Parse("""{ "Abil-0": 12, "AbilVal-0": 0, "MinBase": 509 }""")));

    [Fact]
    public void SummonTargets_NonSummon_Empty()
        => Assert.Empty(
            MonsterMdbInfoBuilder.SummonTargets(Parse("""{ "Abil-0": 1, "MinBase": 509 }""")));

    // ----- BuildOutgoingSummonLabels ---------------------------------

    private static List<string> Labels(string monsterJson)
        => MonsterMdbInfoBuilder.BuildOutgoingSummonLabels(
            Parse(monsterJson),
            // spell 481 -> bandit(#700); 363 -> silvery skull(#239); else nothing
            spellId => spellId switch { 481 => new[] { 700 }, 363 => new[] { 239 }, _ => System.Array.Empty<int>() },
            num => num switch { 700 => "bandit", 239 => "silvery skull", _ => $"#{num}" })
           .Select(s => s.Label).ToList();

    [Fact]
    public void Outgoing_CarriesSummonedMonsterNumber()
    {
        // The number is what the Summons link opens — it must survive alongside the label.
        List<OutgoingSummon> result = MonsterMdbInfoBuilder.BuildOutgoingSummonLabels(
            Parse("""{ "CreateSpell": 481 }"""),
            spellId => spellId == 481 ? new[] { 700 } : System.Array.Empty<int>(),
            num => num == 700 ? "bandit" : $"#{num}");
        OutgoingSummon s = Assert.Single(result);
        Assert.Equal(700, s.MonsterNumber);
        Assert.Equal("bandit", s.Name);
        Assert.Equal("on spawn", s.Context);
        Assert.Equal("bandit (on spawn)", s.Label);
    }

    [Fact]
    public void Outgoing_CreateSpell_IsOnSpawn_NoChance()
    {
        // Leo the Quick shape: CreateSpell 481 → bandit, on spawn.
        Assert.Equal(new[] { "bandit (on spawn)" }, Labels("""{ "CreateSpell": 481 }"""));
    }

    [Fact]
    public void Outgoing_MidSpell_UsesDeltaChance()
    {
        // night-hag shape: between-rounds summon with a % chance (delta of
        // the cumulative MidSpell% threshold).
        Assert.Equal(new[] { "silvery skull (between rounds, 30%)" },
            Labels("""{ "MidSpell-0": 363, "MidSpell%-0": 30 }"""));
    }

    [Fact]
    public void Outgoing_SpellAttack_UsesAttChance()
    {
        Assert.Equal(new[] { "bandit (combat, 60%)" },
            Labels("""{ "AttType-0": 2, "AttAcc-0": 481, "Att%-0": 60 }"""));
    }

    [Fact]
    public void Outgoing_NonSummonSlots_ProduceNothing()
    {
        Assert.Empty(Labels("""{ "CreateSpell": 7, "MidSpell-0": 8, "MidSpell%-0": 50 }"""));
    }

    private static GameRow Parse(string json) => TestRows.Row(json);
}
