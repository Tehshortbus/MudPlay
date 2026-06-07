using System.Text.Json;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.0a sub-A — <see cref="CombatSettings"/> defaults + JSON round-trip
/// + supporting enums + <see cref="CombatSpellSlot"/>.
/// </summary>
public sealed class CombatSettingsTests
{
    [Fact]
    public void Defaults_AreConservative()
    {
        CombatSettings dto = new();

        // Safety: nothing fires until the user opts in.
        Assert.False(dto.MasterAutoAttackEnabled);
        Assert.False(dto.DoBackstab);
        Assert.Equal(PoliteMode.Off, dto.PoliteMode);

        // Sensible defaults that mirror MajorMUD conventions.
        Assert.Equal("a", dto.NormalAttackCommand);
        Assert.Equal(TargetOrder.Normal, dto.TargetOrder);
        Assert.Equal(AttackTiming.Default, dto.AttackTiming);
        Assert.True(dto.SkipBackstabIfMultiAttack);
        Assert.False(dto.RunIfBackstabFails);

        Assert.Equal(0,  dto.MinMonstersInRoom);
        Assert.Equal(20, dto.MaxMonstersInRoom);
        Assert.Equal(3,  dto.RunDistance);

        Assert.Equal(1, dto.NoEffectFailureThreshold);

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
    public void AttackTiming_HasAllFourModes()
    {
        // Locks the enum names — the engine in PR 9.A keys off these exact
        // identifiers, and the JSON storage uses them as serialised
        // strings. Renames here must be matched in CombatManager.
        Assert.Equal(AttackTiming.Default,          (AttackTiming)0);
        Assert.Equal(AttackTiming.AttackLastParty,  (AttackTiming)1);
        Assert.Equal(AttackTiming.AttackLastRoom,   (AttackTiming)2);
        Assert.Equal(AttackTiming.AttackAfter,      (AttackTiming)3);
    }

    [Fact]
    public void PoliteMode_HasAllFourModes()
    {
        Assert.Equal(PoliteMode.Off,             (PoliteMode)0);
        Assert.Equal(PoliteMode.WaitForOthers,   (PoliteMode)1);
        Assert.Equal(PoliteMode.SkipRoom,        (PoliteMode)2);
        Assert.Equal(PoliteMode.AttackDifferent, (PoliteMode)3);
    }

    [Fact]
    public void JsonRoundTrip_PreservesScalars()
    {
        CombatSettings dto = new()
        {
            MasterAutoAttackEnabled    = true,
            NormalAttackCommand        = "attack",
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
            AttackTiming               = AttackTiming.AttackAfter,
            AttackAfterPlayerName      = "Tank",
            PoliteMode                 = PoliteMode.SkipRoom,
            MinMonstersInRoom          = 1,
            MaxMonstersInRoom          = 6,
            RunDistance                = 5,
            NoEffectFailureThreshold   = 3,
            SpellManaThresholdMode     = ThresholdMode.Absolute,
            ShowCombatRoundTotals      = true,
        };

        string json = JsonSerializer.Serialize(dto);
        CombatSettings? round = JsonSerializer.Deserialize<CombatSettings>(json);

        Assert.NotNull(round);
        Assert.Equal(dto.MasterAutoAttackEnabled,   round!.MasterAutoAttackEnabled);
        Assert.Equal(dto.NormalAttackCommand,       round.NormalAttackCommand);
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
        Assert.Equal(dto.AttackTiming,              round.AttackTiming);
        Assert.Equal(dto.AttackAfterPlayerName,     round.AttackAfterPlayerName);
        Assert.Equal(dto.PoliteMode,                round.PoliteMode);
        Assert.Equal(dto.MinMonstersInRoom,         round.MinMonstersInRoom);
        Assert.Equal(dto.MaxMonstersInRoom,         round.MaxMonstersInRoom);
        Assert.Equal(dto.RunDistance,               round.RunDistance);
        Assert.Equal(dto.NoEffectFailureThreshold,  round.NoEffectFailureThreshold);
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
            NormalAttackSpell = new CombatSpellSlot
            {
                SpellName       = "fireball",
                MinManaPerCast  = 30,
            },
        };

        string json = JsonSerializer.Serialize(dto);
        CombatSettings? round = JsonSerializer.Deserialize<CombatSettings>(json);

        Assert.NotNull(round);
        Assert.Equal("star",   round!.MultiAttackSpell.SpellName);
        Assert.Equal(3,        round.MultiAttackSpell.MinEnemies);
        Assert.Equal(4,        round.MultiAttackSpell.MaxCastsPerRoom);
        Assert.Equal(25,       round.MultiAttackSpell.MinManaPerCast);

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
        Assert.Equal(0, slot.MaxCastsPerRoom);
        Assert.Equal(0, slot.MinManaPerCast);
    }
}
