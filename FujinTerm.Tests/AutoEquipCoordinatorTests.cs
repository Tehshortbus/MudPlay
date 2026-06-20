using System.Linq;
using FujinTerm.Game;
using FujinTerm.Game.Inventory;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the pure decision halves of <see cref="AutoEquipCoordinator"/> — which
/// trigger a posture change implies (<see cref="AutoEquipCoordinator.ClassifyRest"/>)
/// and which set (if any) a moment should apply given the live config
/// (<see cref="AutoEquipCoordinator.ResolveTarget"/>). The subscription plumbing is
/// UI glue smoke-tested via the live app.
/// </summary>
public sealed class AutoEquipCoordinatorTests
{
    // ===== ClassifyRest =====

    [Fact]
    public void ClassifyRest_Meditating_IsPreRestManaRegardlessOfGates()
    {
        Assert.Equal(EquipTriggerType.PreRestMana,
            AutoEquipCoordinator.ClassifyRest(PlayerPosition.Meditating, hpGate: true, maGate: true));
    }

    [Fact]
    public void ClassifyRest_RestingWithHpGate_IsPreRestHp()
    {
        Assert.Equal(EquipTriggerType.PreRestHp,
            AutoEquipCoordinator.ClassifyRest(PlayerPosition.Resting, hpGate: true, maGate: false));
    }

    [Fact]
    public void ClassifyRest_RestingWithOnlyMaGate_IsPreRestMana()
    {
        Assert.Equal(EquipTriggerType.PreRestMana,
            AutoEquipCoordinator.ClassifyRest(PlayerPosition.Resting, hpGate: false, maGate: true));
    }

    [Fact]
    public void ClassifyRest_RestingWithBothGates_PrefersHp()
    {
        Assert.Equal(EquipTriggerType.PreRestHp,
            AutoEquipCoordinator.ClassifyRest(PlayerPosition.Resting, hpGate: true, maGate: true));
    }

    [Fact]
    public void ClassifyRest_RestingWithNoGate_DefaultsToHp()
    {
        Assert.Equal(EquipTriggerType.PreRestHp,
            AutoEquipCoordinator.ClassifyRest(PlayerPosition.Resting, hpGate: false, maGate: false));
    }

    [Fact]
    public void ClassifyRest_Standing_IsNull()
    {
        Assert.Null(AutoEquipCoordinator.ClassifyRest(PlayerPosition.Standing, hpGate: true, maGate: true));
    }

    // ===== ResolveTarget =====

    private static EquipmentSettings Config(params EquipmentSet[] sets)
        => new() { Sets = sets.ToList() };

    private static EquipmentSet SetFor(EquipTriggerType trigger, bool enabled, string id)
        => new() { Trigger = trigger, Enabled = enabled, Id = id };

    [Fact]
    public void ResolveTarget_NoSetForType_ReturnsNull()
    {
        EquipmentSettings cfg = Config(SetFor(EquipTriggerType.Backstab, enabled: true, "set-1"));

        Assert.Null(AutoEquipCoordinator.ResolveTarget(cfg, EquipTriggerType.Default));
    }

    [Fact]
    public void ResolveTarget_SetDisabled_ReturnsNull()
    {
        EquipmentSettings cfg = Config(SetFor(EquipTriggerType.Default, enabled: false, "set-1"));

        Assert.Null(AutoEquipCoordinator.ResolveTarget(cfg, EquipTriggerType.Default));
    }

    [Fact]
    public void ResolveTarget_EnabledButNoId_ReturnsNull()
    {
        EquipmentSettings cfg = Config(SetFor(EquipTriggerType.Default, enabled: true, ""));

        Assert.Null(AutoEquipCoordinator.ResolveTarget(cfg, EquipTriggerType.Default));
    }

    [Fact]
    public void ResolveTarget_EnabledWithId_ReturnsSetId()
    {
        EquipmentSettings cfg = Config(SetFor(EquipTriggerType.Default, enabled: true, "set-42"));

        Assert.Equal("set-42", AutoEquipCoordinator.ResolveTarget(cfg, EquipTriggerType.Default));
    }

    [Fact]
    public void ResolveTarget_PicksTheSetForTheRequestedTrigger()
    {
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "hp-set"),
            SetFor(EquipTriggerType.PreRestMana, enabled: true, "mana-set"));

        Assert.Equal("mana-set", AutoEquipCoordinator.ResolveTarget(cfg, EquipTriggerType.PreRestMana));
    }
}
