using System.Text.Json;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// CombatSettings defaults + JSON round-trip
// + supporting enums + CombatSpellSlot.
public sealed class CombatSettingsTests
{
    [Fact]
    public void Defaults_AreConservative()
    {
        CombatSettings dto = new();

        // Safety: nothing fires until the user opts in via
        // GeneralSettings.AutoMode.AutoCombat.
        Assert.False(dto.DoBackstab);
        Assert.Equal(PoliteMode.Off, dto.PoliteMode);

        // Sensible defaults that mirror MajorMUD conventions.
        Assert.Equal("a", dto.NormalAttackCommand);
        Assert.Equal("a", dto.AlternateAttackCommand);

        // Action order defaults to SpellsFirst — the previously hard-coded
        // spell-before-swing order.
        Assert.Equal(CombatActionOrder.SpellsFirst, dto.ActionOrder);
        Assert.Equal(TargetOrder.Normal, dto.TargetOrder);

        // Round-cycle fields (only consulted when ActionOrder is CustomRoundCycle)
        // default to a plain 1/1 alternation opening on physical.
        Assert.Equal(1, dto.CycleRoundsPhysical);
        Assert.Equal(1, dto.CycleRoundsSpell);
        Assert.False(dto.CycleStartOnSpell);

        // Target Priority defaults to following our own game data; no party
        // mirroring and no named member until the user opts in.
        Assert.Equal(TargetPriority.Default, dto.TargetPriority);
        Assert.Null(dto.TargetPriorityMemberName);

        Assert.Equal(AttackTiming.Default, dto.AttackTiming);
        Assert.True(dto.SkipBackstabIfMultiAttack);
        Assert.False(dto.RunIfBackstabFails);

        Assert.Equal(0,  dto.MinMonstersInRoom);
        Assert.Equal(20, dto.MaxMonstersInRoom);
        Assert.Equal(2,  dto.RunDistance);

        // Flee defaults (graduated from the Other tab): retrace the way we
        // came (Backward) and break combat before the first flee move.
        Assert.Equal(RunDirection.Backward, dto.RunDirection);
        Assert.True(dto.BreakBeforeFleeing);

        Assert.Equal(ThresholdMode.Percentage, dto.SpellManaThresholdMode);

        // Five spell slots constructed but empty.
        Assert.NotNull(dto.MultiAttackSpell);
        Assert.Null(dto.MultiAttackSpell.SpellName);
        Assert.NotNull(dto.AreaDebuffSpell);
        Assert.NotNull(dto.SingleTargetDebuffSpell);
        Assert.NotNull(dto.NormalAttackSpell);
        Assert.NotNull(dto.AlternateAttackSpell);

        Assert.False(dto.ShowCombatRoundTotals);
    }

    [Fact]
    public void JsonRoundTrip_PreservesScalars()
    {
        CombatSettings dto = new()
        {
            NormalAttackCommand        = "attack",
            AlternateAttackCommand     = "swing",
            ActionOrder                = CombatActionOrder.CustomRoundCycle,
            CycleRoundsPhysical        = 2,
            CycleRoundsSpell           = 0,
            CycleStartOnSpell          = true,
            NormalWeapon               = "long sword",
            NormalOffHand              = "kite shield",
            AlternateWeapon            = "two-handed axe",
            AlternateOffHand           = null,
            BackstabWeapon             = "main gauche",
            BackstabOffHand            = "buckler",
            DoBackstab                 = true,
            SkipBackstabIfMultiAttack  = false,
            RunIfBackstabFails         = true,
            TargetOrder                = TargetOrder.Reverse,
            TargetPriority             = TargetPriority.FollowMember,
            TargetPriorityMemberName   = "Healer",
            AttackTiming               = AttackTiming.AttackAfter,
            AttackAfterPlayerName      = "Tank",
            PoliteMode                 = PoliteMode.SkipRoom,
            MinMonstersInRoom          = 1,
            MaxMonstersInRoom          = 6,
            RunDistance                = 5,
            RunDirection               = RunDirection.Forward,
            BreakBeforeFleeing         = false,
            SpellManaThresholdMode     = ThresholdMode.Absolute,
            ShowCombatRoundTotals      = true,
        };

        string json = JsonSerializer.Serialize(dto, JsonStore.Options);
        CombatSettings? round = JsonSerializer.Deserialize<CombatSettings>(json, JsonStore.Options);

        Assert.NotNull(round);
        Assert.Equal(dto.NormalAttackCommand,       round!.NormalAttackCommand);
        Assert.Equal(dto.AlternateAttackCommand,    round.AlternateAttackCommand);
        Assert.Equal(dto.ActionOrder,               round.ActionOrder);
        Assert.Equal(dto.CycleRoundsPhysical,        round.CycleRoundsPhysical);
        Assert.Equal(dto.CycleRoundsSpell,           round.CycleRoundsSpell);
        Assert.Equal(dto.CycleStartOnSpell,          round.CycleStartOnSpell);
        Assert.Equal(dto.NormalWeapon,              round.NormalWeapon);
        Assert.Equal(dto.NormalOffHand,             round.NormalOffHand);
        Assert.Equal(dto.AlternateWeapon,           round.AlternateWeapon);
        Assert.Null(round.AlternateOffHand);
        Assert.Equal(dto.BackstabWeapon,            round.BackstabWeapon);
        Assert.Equal(dto.BackstabOffHand,           round.BackstabOffHand);
        Assert.Equal(dto.DoBackstab,                round.DoBackstab);
        Assert.Equal(dto.SkipBackstabIfMultiAttack, round.SkipBackstabIfMultiAttack);
        Assert.Equal(dto.RunIfBackstabFails,        round.RunIfBackstabFails);
        Assert.Equal(dto.TargetOrder,               round.TargetOrder);
        Assert.Equal(dto.TargetPriority,            round.TargetPriority);
        Assert.Equal(dto.TargetPriorityMemberName,  round.TargetPriorityMemberName);
        Assert.Equal(dto.AttackTiming,              round.AttackTiming);
        Assert.Equal(dto.AttackAfterPlayerName,     round.AttackAfterPlayerName);
        Assert.Equal(dto.PoliteMode,                round.PoliteMode);
        Assert.Equal(dto.MinMonstersInRoom,         round.MinMonstersInRoom);
        Assert.Equal(dto.MaxMonstersInRoom,         round.MaxMonstersInRoom);
        Assert.Equal(dto.RunDistance,               round.RunDistance);
        Assert.Equal(dto.RunDirection,              round.RunDirection);
        Assert.Equal(dto.BreakBeforeFleeing,        round.BreakBeforeFleeing);
        Assert.Equal(dto.SpellManaThresholdMode,    round.SpellManaThresholdMode);
        Assert.Equal(dto.ShowCombatRoundTotals,     round.ShowCombatRoundTotals);
    }

    [Fact]
    public void JsonRoundTrip_PreservesSpellSlots()
    {
        CombatSettings dto = new()
        {
            MultiAttackSpell = new CombatSpellSlot
            {
                SpellName       = "star",
                MinEnemies      = 3,
                MaxCastsPerRoom = 4,
                MinManaPerCast  = 25,
            },
            MultiAttack2Enabled = true,
            MultiAttack2Spell = new CombatSpellSlot
            {
                SpellName       = "blad",
                MaxCastsPerRoom = 2,
                MinManaPerCast  = 10,
            },
            NormalAttackSpell = new CombatSpellSlot
            {
                SpellName       = "fireball",
                MinManaPerCast  = 30,
            },
        };

        string json = JsonSerializer.Serialize(dto, JsonStore.Options);
        CombatSettings? round = JsonSerializer.Deserialize<CombatSettings>(json, JsonStore.Options);

        Assert.NotNull(round);
        Assert.Equal("star",   round!.MultiAttackSpell.SpellName);
        Assert.Equal(3,        round.MultiAttackSpell.MinEnemies);
        Assert.Equal(4,        round.MultiAttackSpell.MaxCastsPerRoom);
        Assert.Equal(25,       round.MultiAttackSpell.MinManaPerCast);

        Assert.True(round.MultiAttack2Enabled);
        Assert.Equal("blad",   round.MultiAttack2Spell.SpellName);
        Assert.Equal(2,        round.MultiAttack2Spell.MaxCastsPerRoom);
        Assert.Equal(10,       round.MultiAttack2Spell.MinManaPerCast);

        Assert.Equal("fireball", round.NormalAttackSpell.SpellName);
        Assert.Equal(0,          round.NormalAttackSpell.MinEnemies);   // default
        Assert.Equal(30,         round.NormalAttackSpell.MinManaPerCast);

        // Untouched slots survive as fresh defaults rather than null.
        Assert.NotNull(round.AreaDebuffSpell);
        Assert.Null(round.AreaDebuffSpell.SpellName);
    }

    [Fact]
    public void CombatSpellSlot_Defaults()
    {
        CombatSpellSlot slot = new();
        Assert.Null(slot.SpellName);
        Assert.Equal(0, slot.MinEnemies);
        Assert.Null(slot.MaxCastsPerRoom);   // blank = no per-room cast cap
        Assert.Equal(0, slot.MinManaPerCast);
    }

    // The round-totals row boxes on settings saved before they existed follow "Show
    // combat round totals", so an update neither hides a table that was on nor turns
    // one on.
    [Theory]
    [InlineData("""{"ShowCombatRoundTotals":true}""", true)]
    [InlineData("""{"ShowCombatRoundTotals":true,"ShowCombatRoundTotalsSelfOnly":true}""", true)]
    [InlineData("""{"ShowCombatRoundTotals":false}""", false)]
    [InlineData("""{}""", false)]
    public void OlderSettings_RowsFollowTheTableSwitch(string json, bool rows)
    {
        CombatSettings c = JsonSerializer.Deserialize<CombatSettings>(json, JsonStore.Options)!;
        Assert.Equal(rows, c.ShowsRoundTotalsRow(c.ShowCombatRoundTotalsSelf));
        Assert.Equal(rows, c.ShowsRoundTotalsRow(c.ShowCombatRoundTotalsMonsters));
    }

    [Fact]
    public void SavedBoxes_AreKeptAsSaved()
    {
        CombatSettings c = JsonSerializer.Deserialize<CombatSettings>(
            """{"ShowCombatRoundTotals":true,"ShowCombatRoundTotalsSelf":true,"ShowCombatRoundTotalsParty":false}""")!;
        Assert.True(c.ShowsRoundTotalsRow(c.ShowCombatRoundTotalsSelf));
        Assert.False(c.ShowsRoundTotalsRow(c.ShowCombatRoundTotalsParty));
    }
}
