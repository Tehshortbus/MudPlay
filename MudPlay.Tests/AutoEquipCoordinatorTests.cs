using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// AutoEquipCoordinator: the pure decision halves (ClassifyRest, which trigger a
// posture change implies; ResolveTarget, which set a moment applies), and the
// wiring that drives them — position and combat changes, movement start/stop,
// room changes, held sets and the re-apply once items come back — through the
// gear burst the equipment manager sends. The hazard-room cases (the HazardRig
// section) pin how EquipmentManager dresses a slot a worn counter holds.
public sealed class AutoEquipCoordinatorTests
{
    // What the equipment manager put on the wire, through the sender the client binds.
    private readonly List<byte[]> _sent = new();

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

    // ===== master gate (Auto-All kill-switch) =====

    [Fact]
    public void Fire_Suppressed_WhenAutoDisabled_ThenApplies_OnceReEnabled()
    {
        var player = new PlayerState();
        EquipmentSettings cfg = Config(SetFor(EquipTriggerType.Default, enabled: true, "default-set"));
        var applied = new System.Collections.Generic.List<string>();
        bool autoEnabled = false;

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => autoEnabled);

        // Kill-switch engaged: a loop start must NOT auto-swap gear.
        coord.OnLoopStarted();
        Assert.Empty(applied);

        // Re-enabled: a fresh loop start now applies the Default set.
        autoEnabled = true;
        coord.OnLoopStarted();
        Assert.Equal(new[] { "default-set" }, applied);
    }

    // ===== item-cast swap suppression =====

    // An item-cast buff temporarily borrows an equip slot and restores it itself;
    // an auto-equip fire it triggers (e.g. the rest it breaks) within the window must
    // be HELD so the restore isn't doubled (report paradigm-20260815-130733).
    [Fact]
    public void Fire_HeldBrieflyAfterItemCastSwap_ThenResumes()
    {
        var player = new PlayerState();
        EquipmentSettings cfg = Config(SetFor(EquipTriggerType.Default, enabled: true, "default-set"));
        var applied = new List<string>();
        DateTimeOffset clock = DateTimeOffset.UnixEpoch;

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true,
            log: null,
            now: () => clock);

        // Baseline: a loop start fires the Default set.
        coord.OnLoopStarted();
        Assert.Equal(new[] { "default-set" }, applied);

        // Item-cast swap just began — a fire inside the window is held.
        applied.Clear();
        coord.NoteItemCastSwap();
        coord.OnLoopStarted();
        Assert.Empty(applied);

        // Past the window, a fresh fire applies again.
        applied.Clear();
        clock += TimeSpan.FromSeconds(5);
        coord.OnLoopStarted();
        Assert.Equal(new[] { "default-set" }, applied);
    }

    // ===== recovery-gate hold on stand-up (report paradigm-20260826-132742) =====

    // While a rest-if-below cycle is active (a recovery gate still held), standing
    // up is transient — the HealthManager re-rests. Reverting to Default here would
    // thrash Default↔pre-rest every cycle, so the fall-back is held until recovery
    // completes (the gates clear at rest-max).
    [Fact]
    public void StandFromRest_WhileRecoveryGateHeld_HoldsDefault_ThenFiresOnceCleared()
    {
        var player = new PlayerState { Position = PlayerPosition.Standing };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "hp-set"));
        var applied = new List<string>();
        bool hpGate = true;

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => hpGate,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        // Rest-if-below fires: swap to the HP set.
        player.Position = PlayerPosition.Resting;
        Assert.Equal(new[] { "hp-set" }, applied);

        // A between-round cast / loot grab breaks rest → Standing, but the gate is
        // still held (still below rest-max): must NOT revert to Default.
        applied.Clear();
        player.Position = PlayerPosition.Standing;
        Assert.Empty(applied);

        // Recovery completes: pool hit rest-max, the gate clears; the next stand-up
        // now swaps back to the Default set.
        player.Position = PlayerPosition.Resting;   // HealthManager re-rests (still gated)
        hpGate = false;
        applied.Clear();
        player.Position = PlayerPosition.Standing;
        Assert.Equal(new[] { "default-set" }, applied);
    }

    // A rest typed by hand gets its gear after the sit, and that wear stands the
    // character up: rest again once the gear is on, and don't take the stand for
    // the end of the rest (report paradigm-20261015-202943).
    [Fact]
    public void HandTypedRest_TheGearsWearStandsUsUp_RestsAgain_InsteadOfRevertingToDefault()
    {
        var player = new PlayerState { Position = PlayerPosition.Standing };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "hp-set"));
        var applied = new List<string>();
        string? worn = null;
        int reRests = 0;
        DateTimeOffset t = DateTimeOffset.UnixEpoch;

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => false,
            applyBySetId: id =>
            {
                if (worn == id) return EquipResult.NoChange;
                worn = id;
                applied.Add(id);
                return EquipResult.Applied;
            },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true,
            now: () => t,
            resumeRest: () => reRests++);

        player.Position = PlayerPosition.Resting;          // typed `rest`
        Assert.Equal(new[] { "hp-set" }, applied);
        Assert.True(coord.ReRestOwed);

        t += TimeSpan.FromMilliseconds(50);
        player.Position = PlayerPosition.Standing;         // the wear stood us up
        coord.OnPreRestSetStreamed();                      // the set has gone out
        Assert.Equal(1, reRests);
        Assert.Equal(new[] { "hp-set" }, applied);         // no Default revert

        player.Position = PlayerPosition.Resting;          // the re-sent rest lands
        Assert.Equal(1, reRests);
        Assert.False(coord.ReRestOwed);

        t += TimeSpan.FromSeconds(30);
        player.Position = PlayerPosition.Standing;         // a real stand still reverts
        Assert.Equal(new[] { "hp-set", "default-set" }, applied);
    }

    [Fact]
    public void HandTypedRest_ThenAMoveTyped_IsAStand_NotAReRest()
    {
        var player = new PlayerState { Position = PlayerPosition.Standing };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "hp-set"));
        var applied = new List<string>();
        int reRests = 0;

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true,
            resumeRest: () => reRests++);

        player.Position = PlayerPosition.Resting;
        coord.OnMoveSent();
        player.Position = PlayerPosition.Standing;
        coord.OnPreRestSetStreamed();

        Assert.Equal(0, reRests);
        Assert.Equal(new[] { "hp-set", "default-set" }, applied);
    }

    // By default (SwapToDefaultOnCombat off) combat entry does NOT swap to Default —
    // a fight interrupting a rest leaves the pre-rest loadout on until recovery
    // completes (the long-standing rule).
    [Fact]
    public void CombatEntry_DoesNotFireDefault_WhenSwapOptionOff()
    {
        var player = new PlayerState { Position = PlayerPosition.Standing };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "hp-set"));
        cfg.SwapToDefaultOnCombat = false;   // the default
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => true,      // rest-gated, so only the flag keeps it inert
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        player.InCombat = true;
        Assert.Empty(applied);
    }

    // ===== opt-in: swap to Default on a rest-interrupting fight (and back after) =====

    // Flag on + rest-gated + a pre-rest swap set enabled: combat entry swaps to Default
    // for the fight, and room-clear (still gated) swaps back to the pre-rest set the held
    // gate wants so the resumed rest wears it.
    [Fact]
    public void SwapOnCombat_Enabled_HpGated_FiresDefaultOnEntry_ThenHpSetOnClear()
    {
        var player = new PlayerState { Position = PlayerPosition.Resting };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "hp-set"));
        cfg.SwapToDefaultOnCombat = true;
        var applied = new List<string>();
        bool hpGate = true;

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => hpGate,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        player.InCombat = true;
        Assert.Equal(new[] { "default-set" }, applied);

        // Room clears, still below rest-max (gate held) → swap back to the HP rest set.
        applied.Clear();
        player.InCombat = false;
        Assert.Equal(new[] { "hp-set" }, applied);
    }

    // A mana rest interrupted: swap-back targets the Pre-rest Mana set.
    [Fact]
    public void SwapOnCombat_Enabled_MaGated_SwapsBackToManaSetOnClear()
    {
        var player = new PlayerState { Position = PlayerPosition.Meditating };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestMana, enabled: true, "mana-set"));
        cfg.SwapToDefaultOnCombat = true;
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => true,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        player.InCombat = true;
        Assert.Equal(new[] { "default-set" }, applied);

        applied.Clear();
        player.InCombat = false;
        Assert.Equal(new[] { "mana-set" }, applied);
    }

    // Recovered DURING the fight (gates cleared) → room-clear stays in Default, no
    // swap back to a rest set.
    [Fact]
    public void SwapOnCombat_Enabled_RecoveredDuringFight_StaysInDefault()
    {
        var player = new PlayerState { Position = PlayerPosition.Resting };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "hp-set"));
        cfg.SwapToDefaultOnCombat = true;
        var applied = new List<string>();
        bool hpGate = true;

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => hpGate,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        player.InCombat = true;
        Assert.Equal(new[] { "default-set" }, applied);

        // Healed past rest-max mid-fight — the gate cleared. Room clear keeps Default.
        applied.Clear();
        hpGate = false;
        player.InCombat = false;
        Assert.Empty(applied);
    }

    // A rest posture flickering mid-fight must NOT swap into a pre-rest set — you can't
    // rest in combat, and wearing weak rest gear gets you ravaged (report
    // paradigm-20260916-104923). Combat gear holds; the swap-back happens on combat clear.
    [Fact]
    public void RestPostureDuringCombat_DoesNotSwapToPreRestSet()
    {
        var player = new PlayerState { Position = PlayerPosition.Standing };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "hp-set"));
        cfg.SwapToDefaultOnCombat = true;
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => true,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        player.InCombat = true;
        applied.Clear();

        // Sit mid-fight (HealthManager re-sits, a pre-rest wear breaks, etc.) → NO pre-rest swap.
        player.Position = PlayerPosition.Resting;
        Assert.Empty(applied);

        // Control: the same sit out of combat DOES fire the pre-rest swap.
        player.InCombat = false;
        applied.Clear();
        player.Position = PlayerPosition.Standing;
        player.Position = PlayerPosition.Resting;
        Assert.Equal(new[] { "hp-set" }, applied);
    }

    // Flag on but NOT rest-gated (a plain fight, not interrupting a rest) → no swap.
    [Fact]
    public void SwapOnCombat_Enabled_NotRestGated_DoesNothing()
    {
        var player = new PlayerState { Position = PlayerPosition.Standing };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "hp-set"));
        cfg.SwapToDefaultOnCombat = true;
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,   // not recovering
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        player.InCombat = true;
        Assert.Empty(applied);
        player.InCombat = false;
        Assert.Empty(applied);
    }

    // Flag on + rest-gated but NO pre-rest swap set enabled → nothing swapped us out
    // of Default, so there's nothing to swap for the fight.
    [Fact]
    public void SwapOnCombat_Enabled_NoRestSetEnabled_DoesNothing()
    {
        var player = new PlayerState { Position = PlayerPosition.Resting };
        EquipmentSettings cfg = Config(SetFor(EquipTriggerType.Default, enabled: true, "default-set"));
        cfg.SwapToDefaultOnCombat = true;
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => true,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        player.InCombat = true;
        Assert.Empty(applied);
    }

    // No pre-rest swap set enabled ⇒ resting never left Default (or the user
    // hand-equipped another set), so finishing a rest must NOT fire Default.
    [Fact]
    public void StandFromRest_NotUsingRestSwapSets_DoesNotFireDefault()
    {
        var player = new PlayerState { Position = PlayerPosition.Standing };
        EquipmentSettings cfg = Config(SetFor(EquipTriggerType.Default, enabled: true, "default-set"));
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,   // fully recovered
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        player.Position = PlayerPosition.Resting;   // no pre-rest set to fire
        player.Position = PlayerPosition.Standing;
        Assert.Empty(applied);
    }

    // Recovery topping off to rest-max swaps back to Default (fired while still
    // resting, before the loop steps out) when a pre-rest set is in use and we're
    // not in combat.
    [Fact]
    public void OnRecoveryComplete_UsingRestSets_NotInCombat_FiresDefault()
    {
        var player = new PlayerState { InCombat = false };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "hp-set"));
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        coord.OnRecoveryComplete();
        Assert.Equal(new[] { "default-set" }, applied);
    }

    [Fact]
    public void OnRecoveryComplete_InCombat_DoesNotFire()
    {
        var player = new PlayerState { InCombat = true };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "hp-set"));
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        coord.OnRecoveryComplete();
        Assert.Empty(applied);
    }

    [Fact]
    public void OnRecoveryComplete_NotUsingRestSets_DoesNotFire()
    {
        var player = new PlayerState { InCombat = false };
        EquipmentSettings cfg = Config(SetFor(EquipTriggerType.Default, enabled: true, "default-set"));
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        coord.OnRecoveryComplete();
        Assert.Empty(applied);
    }

    // A loop / Auto-Lair run beginning swaps to the Default set.
    [Fact]
    public void OnLoopStarted_FiresDefault()
    {
        var player = new PlayerState();
        EquipmentSettings cfg = Config(SetFor(EquipTriggerType.Default, enabled: true, "default-set"));
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        coord.OnLoopStarted();
        Assert.Equal(new[] { "default-set" }, applied);
    }

    // Recovery-complete reverts to Default in-room; the stand-up that immediately
    // follows must NOT fire a second, redundant Default swap (report
    // paradigm-20260827-125103 — the gear swapper double-swapping).
    [Fact]
    public void RecoveryComplete_ThenStandUp_FiresDefaultOnce_NotTwice()
    {
        var player = new PlayerState { Position = PlayerPosition.Meditating, InCombat = false };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestMana, enabled: true, "mana-set"));
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => false,   // recovery done (gates cleared)
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        // Recovery tops off while still meditating in-room → Default swap.
        coord.OnRecoveryComplete();
        Assert.Equal(new[] { "default-set" }, applied);

        // The walker resumes and the character stands up; the stand-up Default is
        // suppressed as redundant (recovery already reverted to Default in-room).
        applied.Clear();
        player.Position = PlayerPosition.Standing;
        Assert.Empty(applied);
    }

    // ===== While Moving / Bossing sets =====

    private static EquipmentSet SetWith(EquipTriggerType trigger, bool enabled, string id)
        => new()
        {
            Trigger = trigger, Enabled = enabled, Id = id,
            Slots = { new EquipmentSlotEntry { Slot = EquipmentSlot.Head, ItemName = "hat" } },
        };

    private static AutoEquipCoordinator Coord(
        PlayerState player, EquipmentSettings cfg, List<string> applied,
        Func<Game.Map.RoomKey, bool>? isBossRoom = null,
        Func<Game.Map.RoomKey, bool>? isLair = null,
        Func<bool>? isMoving = null)
        => new(player, () => cfg, () => false, () => false,
            id => { applied.Add(id); return EquipResult.Applied; },
            () => true, () => true, log: null, now: null,
            isBossRoom: isBossRoom, isLair: isLair, isMoving: isMoving);

    // ----- hand movement (report paradigm-20260924-113530) -----------------

    // Build a coordinator with the hand-movement opt-in wiring: an engine-idle probe and
    // a manual scheduler, so a test fires the idle revert itself.
    private static AutoEquipCoordinator ManualCoord(
        PlayerState player, EquipmentSettings cfg, List<string> applied,
        Func<bool> navIdle, List<(TimeSpan Delay, Action Fire)> scheduled)
        => new(player, () => cfg, () => false, () => false,
            id => { applied.Add(id); return EquipResult.Applied; },
            () => true, () => true, log: null, now: null,
            navIdle: navIdle,
            scheduleAfter: (delay, fire) =>
            {
                var entry = (delay, fire);
                scheduled.Add(entry);
                return new ActionDisposable(() => scheduled.Remove(entry));
            });

    private sealed class ActionDisposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    [Fact]
    public void ManualMove_OptIn_WearsMovementSet_AndRevertsAfterTheIdleDelay()
    {
        var player = new PlayerState();
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetWith(EquipTriggerType.WhileMoving, enabled: true, "move-set"));
        cfg.WhileMovingOnManualMoves = true;
        cfg.WhileMovingManualIdleSeconds = 7;
        var applied = new List<string>();
        var scheduled = new List<(TimeSpan Delay, Action Fire)>();
        using AutoEquipCoordinator coord = ManualCoord(player, cfg, applied, () => true, scheduled);

        coord.OnMoveSent();
        coord.OnMoveSent();                               // a second typed step only re-arms
        Assert.Equal(new[] { "move-set" }, applied);
        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(scheduled).Delay);

        scheduled[0].Fire();                              // no typed move for 7s
        Assert.Equal(new[] { "move-set", "default-set" }, applied);
    }

    [Fact]
    public void ManualMove_OptOff_OrAnEngineRunning_DoesNothing()
    {
        var player = new PlayerState();
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetWith(EquipTriggerType.WhileMoving, enabled: true, "move-set"));
        var applied = new List<string>();
        var scheduled = new List<(TimeSpan Delay, Action Fire)>();
        bool idle = true;
        using AutoEquipCoordinator coord = ManualCoord(player, cfg, applied, () => idle, scheduled);

        coord.OnMoveSent();                               // opt-in off
        Assert.Empty(applied);

        cfg.WhileMovingOnManualMoves = true;
        idle = false;                                     // a loop / walk owns the gear
        coord.OnMoveSent();
        Assert.Empty(applied);
        Assert.Empty(scheduled);
    }

    [Fact]
    public void ManualMove_FightTakesTheGear_IdleRevertStandsDown()
    {
        var player = new PlayerState();
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetWith(EquipTriggerType.WhileMoving, enabled: true, "move-set"));
        cfg.WhileMovingOnManualMoves = true;
        var applied = new List<string>();
        var scheduled = new List<(TimeSpan Delay, Action Fire)>();
        using AutoEquipCoordinator coord = ManualCoord(player, cfg, applied, () => true, scheduled);

        coord.OnMoveSent();
        player.InCombat = true;                           // hostiles → Default for the fight
        Assert.Equal(new[] { "move-set", "default-set" }, applied);

        scheduled[0].Fire();                              // the idle revert has nothing left to do
        Assert.Equal(new[] { "move-set", "default-set" }, applied);
    }

    [Fact]
    public void Moving_WearsMovementSet_ThenCombatSwapsToDefaultAndEngages()
    {
        var player = new PlayerState { InCombat = false };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetWith(EquipTriggerType.WhileMoving, enabled: true, "move-set"));
        var applied = new List<string>();
        using AutoEquipCoordinator coord = Coord(player, cfg, applied);

        coord.OnMovementStarted();
        Assert.Equal(new[] { "move-set" }, applied);

        // Hostile recognized while moving → swap to Default (independent of the
        // rest-only SwapToDefaultOnCombat flag, which stays false here).
        applied.Clear();
        player.InCombat = true;
        Assert.Equal(new[] { "default-set" }, applied);
    }

    private sealed class CancelHandle(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    [Fact]
    public void MovingCombatSwap_Debounced_FlickerCancels_SustainedFires()
    {
        // With a settle scheduler wired, the travelling→Default combat swap is
        // deferred: a *Combat Off* within the window cancels it (Paradigm's combat
        // flicker), a fight that outlasts it still gears up. Report
        // paradigm-20260914-154019 (astral slippers thrash).
        var player = new PlayerState { InCombat = false };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetWith(EquipTriggerType.WhileMoving, enabled: true, "move-set"));
        var applied = new List<string>();
        Action? pending = null;
        bool cancelled = false;
        using AutoEquipCoordinator coord = new(
            player, () => cfg, () => false, () => false,
            id => { applied.Add(id); return EquipResult.Applied; },
            () => true, () => true, log: null, now: null,
            scheduleCombatGearSwap: cb =>
            {
                pending = cb; cancelled = false;
                return new CancelHandle(() => { cancelled = true; pending = null; });
            });

        coord.OnMovementStarted();
        Assert.Equal(new[] { "move-set" }, applied);
        applied.Clear();

        // Flicker: engage arms the settle (no immediate swap); a *Combat Off*
        // before it fires cancels it and we stay in the movement set.
        player.InCombat = true;
        Assert.NotNull(pending);
        Assert.Empty(applied);
        player.InCombat = false;
        Assert.True(cancelled);
        Assert.Empty(applied);

        // Sustained: engage arms the settle, it elapses while still in combat → swap.
        player.InCombat = true;
        Assert.NotNull(pending);
        pending!.Invoke();
        Assert.Equal(new[] { "default-set" }, applied);
    }

    [Fact]
    public void MovementStopped_RevertsToDefault()
    {
        var player = new PlayerState();
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetWith(EquipTriggerType.WhileMoving, enabled: true, "move-set"));
        var applied = new List<string>();
        using AutoEquipCoordinator coord = Coord(player, cfg, applied);

        coord.OnMovementStarted();
        applied.Clear();
        coord.OnMovementStopped();   // walk-to arrived / run stopped
        Assert.Equal(new[] { "default-set" }, applied);
    }

    // Report paradigm-20260916-104923: an HP rest finished and the loop resumed moving
    // while the MANA gate still lingered (pool below rest-target). The movement-gear swap
    // must fire — standing + travelling wears the movement set even with a lingering rest
    // gate; only an actual rest posture holds the pre-rest gear.
    [Fact]
    public void MovementStarted_RestGateLingeringButStanding_WearsMovementSet()
    {
        var player = new PlayerState { Position = PlayerPosition.Standing };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetWith(EquipTriggerType.WhileMoving, enabled: true, "move-set"));
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player, readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => true,        // mana rest still lingering below target
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        coord.OnMovementStarted();
        Assert.Equal(new[] { "move-set" }, applied);
    }

    // The core of report paradigm-20260916-104923: the user has NO While-Moving set
    // (disabled). After a combat-clear swap-back left Pre-rest gear on, resuming the loop
    // used to no-op (OnMovementStarted returns without a movement set) — travelling and
    // fighting in Pre-rest. It must revert to Default so travel gear is worn.
    [Fact]
    public void MovementStarted_NoMovementSet_RevertsToDefault()
    {
        var player = new PlayerState { Position = PlayerPosition.Standing };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetWith(EquipTriggerType.PreRestMana, enabled: true, "mana-set"),
            SetWith(EquipTriggerType.WhileMoving, enabled: false, "move-set"));   // disabled
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player, readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        coord.OnMovementStarted();
        Assert.Equal(new[] { "default-set" }, applied);
    }

    [Fact]
    public void MovementStarted_WhileSittingAtRest_HoldsPreRestGear()
    {
        // The other half: actually SITTING (rest posture) still holds — no movement swap.
        var player = new PlayerState { Position = PlayerPosition.Resting };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetWith(EquipTriggerType.WhileMoving, enabled: true, "move-set"));
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player, readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => true,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        coord.OnMovementStarted();
        Assert.Empty(applied);
    }

    [Fact]
    public void EmptyMovementSet_DoesNotSuppressLoopStartDefault()
    {
        var player = new PlayerState();
        // WhileMoving enabled but with NO slots — must not shadow the loop-start Default.
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.WhileMoving, enabled: true, "move-set"));
        var applied = new List<string>();
        using AutoEquipCoordinator coord = Coord(player, cfg, applied);

        coord.OnLoopStarted();
        Assert.Equal(new[] { "default-set" }, applied);
    }

    [Fact]
    public void BossRoom_WearsBossingBeforeEntry_RevertsToDefaultOnLeaving()
    {
        var player = new PlayerState();
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetWith(EquipTriggerType.Bossing, enabled: true, "boss-set"));
        var applied = new List<string>();
        var boss = new Game.Map.RoomKey(1, 500);
        var plain = new Game.Map.RoomKey(1, 501);
        using AutoEquipCoordinator coord = Coord(player, cfg, applied,
            isBossRoom: k => k == boss, isMoving: () => false);

        // Pre-step into the boss room wears the Bossing set.
        coord.OnAboutToEnterRoom(boss);
        Assert.Equal(new[] { "boss-set" }, applied);

        // Leaving the boss room (not moving) reverts to Default.
        applied.Clear();
        coord.OnRoomChanged(previous: boss, current: plain);
        Assert.Equal(new[] { "default-set" }, applied);
    }

    [Fact]
    public void LeavingBossRoomWhileMoving_RevertsToDefaultThenMovementSet()
    {
        var player = new PlayerState();
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetWith(EquipTriggerType.WhileMoving, enabled: true, "move-set"),
            SetWith(EquipTriggerType.Bossing, enabled: true, "boss-set"));
        var applied = new List<string>();
        var boss = new Game.Map.RoomKey(1, 500);
        var plain = new Game.Map.RoomKey(1, 501);
        bool moving = true;
        using AutoEquipCoordinator coord = Coord(player, cfg, applied,
            isBossRoom: k => k == boss, isMoving: () => moving);

        // Step out of the boss room while still travelling: Default FIRST, then the
        // movement set re-layers (in that order).
        coord.OnRoomChanged(previous: boss, current: plain);
        Assert.Equal(new[] { "default-set", "move-set" }, applied);
    }

    [Fact]
    public void AboutToEnterLair_SwapsToDefaultOnlyWhenOptOnAndMoving()
    {
        var player = new PlayerState();
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetWith(EquipTriggerType.WhileMoving, enabled: true, "move-set"));
        cfg.SwapToDefaultBeforeLairs = true;
        var applied = new List<string>();
        var lair = new Game.Map.RoomKey(2, 42);
        using AutoEquipCoordinator coord = Coord(player, cfg, applied,
            isBossRoom: _ => false, isLair: k => k == lair);

        // Not yet in the movement set → no pre-lair swap (nothing to swap away from).
        coord.OnAboutToEnterRoom(lair);
        Assert.Empty(applied);

        // Now travelling in the movement set → the pre-lair step swaps to Default.
        coord.OnMovementStarted();          // wears move-set
        applied.Clear();
        coord.OnAboutToEnterRoom(lair);
        Assert.Equal(new[] { "default-set" }, applied);
    }

    [Fact]
    public void AboutToEnterLair_OptOff_DoesNotSwap()
    {
        var player = new PlayerState();
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetWith(EquipTriggerType.WhileMoving, enabled: true, "move-set"));
        cfg.SwapToDefaultBeforeLairs = false;   // enter in movement gear, swap on monsters
        var applied = new List<string>();
        var lair = new Game.Map.RoomKey(2, 42);
        using AutoEquipCoordinator coord = Coord(player, cfg, applied,
            isBossRoom: _ => false, isLair: k => k == lair);

        coord.OnMovementStarted();
        applied.Clear();
        coord.OnAboutToEnterRoom(lair);
        Assert.Empty(applied);   // stays in the movement set
    }

    [Fact]
    public void LeavingBossTaggedRoom_NoBossingSet_DoesNotRevertGear()
    {
        // The game-data Bosses table tags a room (e.g. a town room with a named NPC),
        // but the character has NO Bossing set. Stepping out must not yank the While
        // Moving gear into Default (report paradigm-20260915-061646 / -061734).
        var player = new PlayerState();
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetWith(EquipTriggerType.WhileMoving, enabled: true, "move-set"));
        var applied = new List<string>();
        var boss = new Game.Map.RoomKey(1, 500);
        var plain = new Game.Map.RoomKey(1, 501);
        using AutoEquipCoordinator coord = Coord(player, cfg, applied,
            isBossRoom: k => k == boss, isMoving: () => true);

        coord.OnRoomChanged(previous: boss, current: plain);
        Assert.Empty(applied);   // no Bossing set → nothing to revert
    }

    [Fact]
    public void LairThenTransit_SwapsToDefaultThenBackToMovementSet()
    {
        // Into a lair → Default footwear; out of the lair into a transit room → back to
        // the movement footwear, both on the pre-move hook (report paradigm-20260915-124359).
        var player = new PlayerState();
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetWith(EquipTriggerType.WhileMoving, enabled: true, "move-set"));
        cfg.SwapToDefaultBeforeLairs = true;
        var applied = new List<string>();
        var lair = new Game.Map.RoomKey(16, 242);
        var transit = new Game.Map.RoomKey(16, 240);
        using AutoEquipCoordinator coord = Coord(player, cfg, applied,
            isBossRoom: _ => false, isLair: k => k == lair, isMoving: () => true);

        coord.OnMovementStarted();               // wears move-set
        applied.Clear();

        coord.OnAboutToEnterRoom(lair);          // into a lair → Default
        Assert.Equal(new[] { "default-set" }, applied);
        applied.Clear();

        coord.OnAboutToEnterRoom(transit);       // out of the lair → back to move-set
        Assert.Equal(new[] { "move-set" }, applied);
    }

    [Fact]
    public void LairSwap_MovementResume_DoesNotUndoDefault()
    {
        // After the pre-lair swap to Default, the loop resume (OnMovementStarted, fired
        // when the gear-swap gate clears) must NOT re-wear the movement set — the thrash
        // that kept the character in speed footwear inside lairs (report
        // paradigm-20260915-124359).
        var player = new PlayerState();
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetWith(EquipTriggerType.WhileMoving, enabled: true, "move-set"));
        cfg.SwapToDefaultBeforeLairs = true;
        var applied = new List<string>();
        var lair = new Game.Map.RoomKey(16, 242);
        using AutoEquipCoordinator coord = Coord(player, cfg, applied,
            isBossRoom: _ => false, isLair: k => k == lair, isMoving: () => true);

        coord.OnMovementStarted();               // wears move-set
        coord.OnAboutToEnterRoom(lair);          // into a lair → Default
        applied.Clear();

        coord.OnMovementStarted();               // loop resume after the swap gate cleared
        Assert.Empty(applied);                   // stays in Default, not re-wearing move-set
    }

    [Fact]
    public void ConsecutiveLairs_NoSwapBackBetweenThem()
    {
        // Two adjacent lair rooms (no transit gap): once in Default we stay there rather
        // than flapping move-set/Default between them (report paradigm-20260915-124359).
        var player = new PlayerState();
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetWith(EquipTriggerType.WhileMoving, enabled: true, "move-set"));
        cfg.SwapToDefaultBeforeLairs = true;
        var applied = new List<string>();
        var lairA = new Game.Map.RoomKey(16, 242);
        var lairB = new Game.Map.RoomKey(16, 243);
        using AutoEquipCoordinator coord = Coord(player, cfg, applied,
            isBossRoom: _ => false, isLair: k => k == lairA || k == lairB, isMoving: () => true);

        coord.OnMovementStarted();
        coord.OnAboutToEnterRoom(lairA);         // move-set → Default
        applied.Clear();

        coord.OnAboutToEnterRoom(lairB);         // adjacent lair → stay in Default
        Assert.Empty(applied);
    }

    // ===== a sit confirmed after the rest already finished =====

    // The rest's regen tick topped mana off on the next prompt, so the gates cleared
    // before the server confirmed "You are now meditating": no pre-rest swap, or the
    // walk would carry the rest gear into the next fight (report
    // paradigm-20260929-115648). A sit with no rest just ended — typed by hand —
    // still wears the set.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SitAfterRestFinished_SkipsThePreRestSwap(bool restJustEnded, bool expectSwap)
    {
        var player = new PlayerState { Position = PlayerPosition.Standing };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestMana, enabled: true, "mana-set"));
        var applied = new System.Collections.Generic.List<string>();

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true,
            restJustEnded: () => restJustEnded);

        player.Position = PlayerPosition.Meditating;

        Assert.Equal(expectSwap, applied.Contains("mana-set"));
    }

    // ===== a pre-rest set worn with no rest behind it =====

    // Meditating by hand above every rest floor wears Pre-rest Mana and nothing else.
    // The Default set used to be applied in the same instant (no rest gate held read as
    // "the rest is over"), and against a worn list the pre-rest swap hadn't reached yet
    // it re-wore the Default piece in each slot the pre-rest set leaves alone — taking
    // off a neck piece put on by hand (report paradigm-20261008-202210).
    [Fact]
    public void MeditateByHand_WearsOnlyThePreRestSet_LeavingItsUnsetSlotsAlone()
    {
        var player = new PlayerState { Position = PlayerPosition.Standing };
        EquipmentSettings cfg = Config(
            new EquipmentSet
            {
                Trigger = EquipTriggerType.Default, Enabled = true, Id = "default-set", Name = "Default",
                Slots = { new(EquipmentSlot.Head, "turban"), new(EquipmentSlot.Neck, "stone") },
            },
            new EquipmentSet
            {
                Trigger = EquipTriggerType.PreRestMana, Enabled = true, Id = "mana-set", Name = "Pre-rest Mana",
                Slots = { new(EquipmentSlot.Head, "wizard hat") },
            });
        InventorySnapshot worn = InventorySnapshot.Empty with
        {
            EquippedItems = new[] { new EquippedItem("turban", "Head"), new EquippedItem("feather", "Neck") },
            CarriedItems = new[] { "wizard hat", "stone" },
            LastUpdated = DateTimeOffset.UtcNow,
        };
        var equipment = new EquipmentManager(
            readEquipment: () => cfg,
            getSnapshot: () => worn,
            readCombat: () => new CombatSettings(),
            writeCombat: _ => { });
        equipment.SetWireSender(_sent.Add);
        AutoEquipCoordinator? hooked = null;
        // The swap-finished hook as the app wires it.
        equipment.ApplyingChanged += applying =>
        {
            if (!applying && equipment.CurrentSetId == "mana-set") hooked!.OnPreRestSetStreamed();
        };
        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => false,
            applyBySetId: equipment.ApplyBySetId,
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true,
            restJustEnded: () => false);
        hooked = coord;

        player.Position = PlayerPosition.Meditating;

        Assert.Equal(new[] { "wear wizard hat" },
            _sent.Select(b => System.Text.Encoding.Latin1.GetString(b).TrimEnd('\r')));
        Assert.Equal("mana-set", equipment.CurrentSetId);
    }

    // ===== a gear swap never strips the room's protection =====

    private (EquipmentManager Equipment, EquipmentSettings Config) HazardRig(
        IReadOnlyCollection<string> wornCounters, params EquippedItem[] worn)
    {
        EquipmentSettings cfg = Config(
            new EquipmentSet
            {
                Trigger = EquipTriggerType.Default, Enabled = true, Id = "default-set", Name = "Default",
                Slots =
                {
                    new(EquipmentSlot.Head, "turban"), new(EquipmentSlot.Neck, "stone"),
                    new(EquipmentSlot.Finger1, "gold ring"), new(EquipmentSlot.Finger2, "pearl ring"),
                },
            });
        InventorySnapshot snap = InventorySnapshot.Empty with
        {
            EquippedItems = worn,
            CarriedItems = new[] { "turban", "stone", "gold ring", "pearl ring" },
            LastUpdated = DateTimeOffset.UtcNow,
        };
        var equipment = new EquipmentManager(
            readEquipment: () => cfg,
            getSnapshot: () => snap,
            readCombat: () => new CombatSettings(),
            writeCombat: _ => { });
        equipment.SetRoomProtectionProbe(() => wornCounters);
        equipment.SetWireSender(_sent.Add);
        return (equipment, cfg);
    }

    private string[] Sent() =>
        _sent.Select(b => System.Text.Encoding.Latin1.GetString(b).TrimEnd('\r')).ToArray();

    // In a lava room the phoenix feather on the neck is what stops the burning. The
    // Default set names another neck piece; applying it there dresses every other
    // slot and leaves the feather where it is.
    [Fact]
    public void ApplyingASet_InAHazardRoom_LeavesTheWornCounterOn()
    {
        (EquipmentManager equipment, _) = HazardRig(new[] { "phoenix feather" },
            new EquippedItem("wizard hat", "Head"), new EquippedItem("phoenix feather", "Neck"));

        equipment.ApplyBySetId("default-set");

        string[] sent = Sent();
        Assert.Contains("wear turban", sent);
        Assert.DoesNotContain("wear stone", sent);
        Assert.DoesNotContain(sent, c => c.Contains("phoenix feather"));
    }

    // Out of the hazard the same apply dresses the neck as the set says.
    [Fact]
    public void ApplyingASet_AwayFromAnyHazard_DressesTheSlotAsUsual()
    {
        (EquipmentManager equipment, _) = HazardRig(Array.Empty<string>(),
            new EquippedItem("wizard hat", "Head"), new EquippedItem("phoenix feather", "Neck"));

        equipment.ApplyBySetId("default-set");

        Assert.Contains("wear stone", Sent());
    }

    // A counter that is only carried isn't protecting anything, so nothing is held for it.
    [Fact]
    public void ACounterThatIsNotWorn_HoldsNoSlot()
    {
        (EquipmentManager equipment, _) = HazardRig(new[] { "phoenix feather" },
            new EquippedItem("wizard hat", "Head"));

        equipment.ApplyBySetId("default-set");

        Assert.Contains("wear stone", Sent());
    }

    // A counter on one of a pair (a ring, a wristband) holds both: putting a ring on
    // a full pair evicts whichever one the realm picks, and that could be the counter.
    [Fact]
    public void ACounterOnAPairedSlot_HoldsBothOfThePair()
    {
        (EquipmentManager equipment, _) = HazardRig(new[] { "ring of cooling" },
            new EquippedItem("wizard hat", "Head"), new EquippedItem("ring of cooling", "Finger"),
            new EquippedItem("iron ring", "Finger"));

        equipment.ApplyBySetId("default-set");

        string[] sent = Sent();
        Assert.Contains("wear turban", sent);
        Assert.DoesNotContain(sent, c => c.Contains("ring"));
    }

    // The swap-finished revert is for a rest that ended while its gear was going on:
    // it needs a rest that really just finished, and no gate still held.
    [Theory]
    [InlineData(false, false, false)]   // no rest behind the set — typed by hand
    [InlineData(true, false, true)]     // the rest finished under the swap
    [InlineData(true, true, false)]     // still recovering
    public void PreRestSetStreamed_RevertsToDefault_OnlyAfterAFinishedRest(
        bool restJustEnded, bool gateHeld, bool expectDefault)
    {
        var player = new PlayerState { Position = PlayerPosition.Meditating };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestMana, enabled: true, "mana-set"));
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => gateHeld,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true,
            restJustEnded: () => restJustEnded);

        coord.OnPreRestSetStreamed();

        Assert.Equal(expectDefault, applied.Contains("default-set"));
    }

    // A pre-rest set worn for a sit with no gate behind it comes off at the stand.
    [Fact]
    public void MeditateByHand_ThenStand_GoesBackToDefault()
    {
        var player = new PlayerState { Position = PlayerPosition.Standing };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestMana, enabled: true, "mana-set"));
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true,
            restJustEnded: () => false);

        player.Position = PlayerPosition.Meditating;
        coord.OnPreRestSetStreamed();
        Assert.Equal(new[] { "mana-set" }, applied);

        player.Position = PlayerPosition.Standing;
        Assert.Equal(new[] { "mana-set", "default-set" }, applied);
    }

    // Before `rest` goes out, the pre-rest set the held gates call for goes on; a
    // set already worn (NoChange) or a fight means no swap and no wait.
    [Fact]
    public void WearRestGearBeforeResting_FiresTheGatedSet_OnlyWhenItSwaps()
    {
        var player = new PlayerState { Position = PlayerPosition.Standing };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "hp-set"),
            SetFor(EquipTriggerType.PreRestMana, enabled: true, "mana-set"));
        var applied = new System.Collections.Generic.List<string>();
        EquipResult result = EquipResult.Applied;

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => true,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return result; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        Assert.True(coord.WearRestGearBeforeResting());
        Assert.Equal(new[] { "hp-set" }, applied);

        result = EquipResult.NoChange;               // already wearing it
        Assert.False(coord.WearRestGearBeforeResting());

        result = EquipResult.Applied;
        player.InCombat = true;
        Assert.False(coord.WearRestGearBeforeResting());
    }

    // ===== Bossing set kept on between bosses =====

    private static AutoEquipCoordinator BossCoord(
        PlayerState player, EquipmentSettings cfg, List<string> applied,
        Game.Map.RoomKey bossA, Game.Map.RoomKey bossB, Func<BossTravel> travel, Func<bool> isMoving)
        => new(player, () => cfg, () => false, () => false,
            id => { applied.Add(id); return EquipResult.Applied; },
            () => true, () => true, log: null, now: null,
            isBossRoom: k => k == bossA || k == bossB, isMoving: isMoving,
            bossTravel: _ => travel());

    private static EquipmentSettings BossConfig(bool keep)
    {
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetWith(EquipTriggerType.WhileMoving, enabled: true, "move-set"),
            SetWith(EquipTriggerType.Bossing, enabled: true, "boss-set"));
        cfg.KeepBossingBetweenBosses = keep;
        return cfg;
    }

    [Fact]
    public void KeepBossing_HeadingToAnotherBoss_StaysOnForTheTrip()
    {
        var player = new PlayerState();
        var applied = new List<string>();
        var bossA = new Game.Map.RoomKey(1, 500);
        var bossB = new Game.Map.RoomKey(1, 600);
        var road = new Game.Map.RoomKey(1, 501);
        using AutoEquipCoordinator coord = BossCoord(player, BossConfig(keep: true), applied,
            bossA, bossB, () => BossTravel.Yes, () => true);

        coord.OnRoomChanged(previous: bossA, current: road);
        coord.OnMovementStarted();                       // the walk resuming mustn't swap either
        coord.OnAboutToEnterRoom(new Game.Map.RoomKey(1, 502));

        Assert.Empty(applied);
        Assert.True(coord.IsKeepingBossing);

        coord.OnAboutToEnterRoom(bossB);                 // arriving: already in it
        Assert.False(coord.IsKeepingBossing);
    }

    [Fact]
    public void KeepBossing_NotHeadingToABoss_RevertsAsBefore()
    {
        var player = new PlayerState();
        var applied = new List<string>();
        var bossA = new Game.Map.RoomKey(1, 500);
        using AutoEquipCoordinator coord = BossCoord(player, BossConfig(keep: true), applied,
            bossA, new Game.Map.RoomKey(1, 600), () => BossTravel.No, () => true);

        coord.OnRoomChanged(previous: bossA, current: new Game.Map.RoomKey(1, 501));

        Assert.Equal(new[] { "default-set", "move-set" }, applied);
        Assert.False(coord.IsKeepingBossing);
    }

    [Fact]
    public void KeepBossing_OptionOff_RevertsEvenWhenHeadingToABoss()
    {
        var player = new PlayerState();
        var applied = new List<string>();
        var bossA = new Game.Map.RoomKey(1, 500);
        using AutoEquipCoordinator coord = BossCoord(player, BossConfig(keep: false), applied,
            bossA, new Game.Map.RoomKey(1, 600), () => BossTravel.Yes, () => true);

        coord.OnRoomChanged(previous: bossA, current: new Game.Map.RoomKey(1, 501));

        Assert.Equal(new[] { "default-set", "move-set" }, applied);
    }

    // A follower keeps the set on while its leader is asked, and takes it off when the
    // answer is that the party isn't going to a boss.
    [Fact]
    public void KeepBossing_Follower_KeepsWhileAsking_RevertsOnANo()
    {
        var player = new PlayerState();
        var applied = new List<string>();
        var bossA = new Game.Map.RoomKey(1, 500);
        using AutoEquipCoordinator coord = BossCoord(player, BossConfig(keep: true), applied,
            bossA, new Game.Map.RoomKey(1, 600), () => BossTravel.Asking, () => false);

        coord.OnRoomChanged(previous: bossA, current: new Game.Map.RoomKey(1, 501));
        Assert.Empty(applied);
        Assert.True(coord.IsKeepingBossing);

        coord.OnBossTravelResolved(headingToBoss: true);
        Assert.Empty(applied);

        coord.OnBossTravelResolved(headingToBoss: false);
        Assert.Equal(new[] { "default-set" }, applied);
        Assert.False(coord.IsKeepingBossing);
    }

    [Fact]
    public void KeepBossing_RouteChangesAwayFromTheBoss_RevertsAtTheNextStep()
    {
        var player = new PlayerState();
        var applied = new List<string>();
        var bossA = new Game.Map.RoomKey(1, 500);
        BossTravel travel = BossTravel.Yes;
        using AutoEquipCoordinator coord = BossCoord(player, BossConfig(keep: true), applied,
            bossA, new Game.Map.RoomKey(1, 600), () => travel, () => true);

        coord.OnRoomChanged(previous: bossA, current: new Game.Map.RoomKey(1, 501));
        travel = BossTravel.No;                          // a new destination was picked
        coord.OnAboutToEnterRoom(new Game.Map.RoomKey(1, 502));

        Assert.Equal(new[] { "default-set", "move-set" }, applied);
    }

    [Fact]
    public void KeepBossing_TripEndsShortOfABoss_GoesBackToDefault()
    {
        var player = new PlayerState();
        var applied = new List<string>();
        var bossA = new Game.Map.RoomKey(1, 500);
        using AutoEquipCoordinator coord = BossCoord(player, BossConfig(keep: true), applied,
            bossA, new Game.Map.RoomKey(1, 600), () => BossTravel.Yes, () => true);

        coord.OnRoomChanged(previous: bossA, current: new Game.Map.RoomKey(1, 501));
        coord.OnMovementStopped();

        Assert.Equal(new[] { "default-set" }, applied);
    }

    // ===== A set held from the Equip menu =====

    [Fact]
    public void HeldSet_BlocksEveryAutomaticSwap_UntilReleased()
    {
        var player = new PlayerState();
        EquipmentSettings cfg = BossConfig(keep: false);
        var applied = new List<string>();
        var boss = new Game.Map.RoomKey(1, 500);
        bool moving = true;
        using AutoEquipCoordinator coord = Coord(player, cfg, applied,
            isBossRoom: k => k == boss, isMoving: () => moving);
        int changes = 0;
        coord.HeldSetChanged += () => changes++;

        coord.HoldSet(EquipTriggerType.Backstab);
        coord.OnMovementStarted();
        coord.OnAboutToEnterRoom(boss);
        coord.OnLoopStarted();
        Assert.Empty(applied);
        Assert.Equal(EquipTriggerType.Backstab, coord.HeldSet);

        // Deselected while travelling: off to Default, then the set the run wants.
        coord.ReleaseHeldSet();
        Assert.Null(coord.HeldSet);
        Assert.Equal(new[] { "default-set", "move-set" }, applied);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void HeldSet_ReleasedStandingStill_GoesBackToDefault()
    {
        var player = new PlayerState();
        var applied = new List<string>();
        using AutoEquipCoordinator coord = Coord(player, BossConfig(keep: false), applied,
            isBossRoom: _ => false, isMoving: () => false);

        coord.HoldSet(EquipTriggerType.Bossing);
        coord.ReleaseHeldSet();

        Assert.Equal(new[] { "default-set" }, applied);
    }

    [Fact]
    public void HeldSet_DefaultIsNeverHeld_AndAQuietReleaseSwapsNothing()
    {
        var player = new PlayerState();
        var applied = new List<string>();
        using AutoEquipCoordinator coord = Coord(player, BossConfig(keep: false), applied,
            isBossRoom: _ => false, isMoving: () => false);

        coord.HoldSet(EquipTriggerType.Default);
        Assert.Null(coord.HeldSet);

        coord.HoldSet(EquipTriggerType.PreRestHp);
        coord.ReleaseHeldSet(quiet: true);
        Assert.Null(coord.HeldSet);
        Assert.Empty(applied);
    }

    // Deselected mid-fight: the gear stays put until combat clears, then Default.
    [Fact]
    public void HeldSet_ReleasedInCombat_GoesBackToDefaultOnceTheFightIsOver()
    {
        var player = new PlayerState { InCombat = true };
        var applied = new List<string>();
        using AutoEquipCoordinator coord = Coord(player, BossConfig(keep: false), applied,
            isBossRoom: _ => false, isMoving: () => false);

        coord.HoldSet(EquipTriggerType.Bossing);
        coord.ReleaseHeldSet();
        Assert.Empty(applied);

        player.InCombat = false;
        Assert.Equal(new[] { "default-set" }, applied);
    }

    // The set just deselected is the one automation wants (resting in the pre-rest
    // set): it stays on, and comes off the usual way when the rest is done.
    [Fact]
    public void HeldSet_ReleasedWhileItIsWhatAutomationWants_StaysOn()
    {
        var player = new PlayerState { Position = PlayerPosition.Resting };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "rest-set"));
        var applied = new List<string>();
        using AutoEquipCoordinator coord = Coord(player, cfg, applied,
            isBossRoom: _ => false, isMoving: () => false);

        coord.HoldSet(EquipTriggerType.PreRestHp);
        coord.ReleaseHeldSet();
        Assert.Empty(applied);

        player.Position = PlayerPosition.Standing;       // rested: back to Default as usual
        Assert.Equal(new[] { "default-set" }, applied);
    }

    // Deselected, then a rest in a different set: Default first, then the rest set.
    [Fact]
    public void HeldSet_ReleasedIntoARest_GoesThroughDefaultToTheRestSet()
    {
        var player = new PlayerState { Position = PlayerPosition.Resting };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "rest-set"),
            SetWith(EquipTriggerType.Bossing, enabled: true, "boss-set"));
        var applied = new List<string>();
        using AutoEquipCoordinator coord = Coord(player, cfg, applied,
            isBossRoom: _ => false, isMoving: () => false);

        coord.HoldSet(EquipTriggerType.Bossing);
        coord.ReleaseHeldSet();

        Assert.Equal(new[] { "default-set", "rest-set" }, applied);
    }

    // Deselected mid-fight, with the rest gate up when the fight clears: the same set
    // stays on, a different one goes through Default to the rest set.
    [Fact]
    public void HeldSet_ReleasedInCombat_RestGateUpAtTheClear_KeepsOrGoesThroughDefaultToTheRestSet()
    {
        var player = new PlayerState { InCombat = true };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "rest-set"),
            SetWith(EquipTriggerType.Bossing, enabled: true, "boss-set"));
        var applied = new List<string>();
        bool hpGate = false;
        using AutoEquipCoordinator coord = new(player, () => cfg, () => hpGate, () => false,
            id => { applied.Add(id); return EquipResult.Applied; }, () => true, () => true);

        // Same set: it stays on and nothing is sent.
        coord.HoldSet(EquipTriggerType.PreRestHp);
        coord.ReleaseHeldSet();
        hpGate = true;
        player.InCombat = false;
        Assert.Empty(applied);

        // A different set: Default, then the rest set.
        player.InCombat = true;
        hpGate = false;
        coord.HoldSet(EquipTriggerType.Bossing);
        coord.ReleaseHeldSet();
        hpGate = true;
        player.InCombat = false;
        Assert.Equal(new[] { "default-set", "rest-set" }, applied);
    }

    // Deselected mid-fight, and the fight carries into a boss room before it clears:
    // automation asks for the Bossing set while the deferred Default is still owed.
    // The same set stays on; any other goes on over Default.
    [Fact]
    public void HeldSet_ReleasedInCombat_ThenABossRoomBeforeTheClear()
    {
        var player = new PlayerState { InCombat = true };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "rest-set"),
            SetWith(EquipTriggerType.Bossing, enabled: true, "boss-set"));
        var applied = new List<string>();
        var boss = new Game.Map.RoomKey(1, 500);
        var plain = new Game.Map.RoomKey(1, 501);
        using AutoEquipCoordinator coord = Coord(player, cfg, applied, isBossRoom: k => k == boss);

        coord.HoldSet(EquipTriggerType.Bossing);
        coord.ReleaseHeldSet();
        Assert.Equal(EquipTriggerType.Bossing, coord.ReleasedSetAwaitingDefault);
        coord.OnRoomChanged(previous: plain, current: boss);
        Assert.Empty(applied);
        Assert.Null(coord.ReleasedSetAwaitingDefault);

        coord.HoldSet(EquipTriggerType.PreRestHp);
        coord.ReleaseHeldSet();
        coord.OnRoomChanged(previous: plain, current: boss);
        Assert.Equal(new[] { "default-set", "boss-set" }, applied);
        Assert.Null(coord.ReleasedSetAwaitingDefault);
    }

    // ===== gear that came back to the pack =====
    // A hang-up penalty dropped pieces and they were picked up again. No recovery
    // rule of its own: the set that was on is applied again (user, 2026-10-09).

    private static EquipmentSet Wearing(EquipTriggerType trigger, bool enabled, string id, params string[] items)
    {
        EquipmentSet set = SetFor(trigger, enabled, id);
        set.Name = id;
        EquipmentSlot slot = EquipmentSlot.Head;
        foreach (string item in items) set.Slots.Add(new EquipmentSlotEntry(slot++, item));
        return set;
    }

    private static AutoEquipCoordinator ReturnHarness(
        EquipmentSettings cfg, List<string> applied, PlayerState? player = null,
        Func<bool>? autoEnabled = null, Func<bool>? hpGate = null, Func<bool>? wornKnown = null,
        EquipResult result = EquipResult.Applied) =>
        new(player ?? new PlayerState(), () => cfg, hpGate ?? (() => false), () => false,
            id => { applied.Add(id); return result; }, wornKnown ?? (() => true), autoEnabled ?? (() => true));

    [Fact]
    public void ItemsReturned_TheSetLastApplied_IsAppliedAgain()
    {
        EquipmentSettings cfg = Config(
            Wearing(EquipTriggerType.Default, enabled: true, "default-set", "plate mail"),
            Wearing(EquipTriggerType.WhileMoving, enabled: true, "moving-set", "astral slippers", "plate mail"));
        var applied = new List<string>();
        using AutoEquipCoordinator coord = ReturnHarness(cfg, applied);

        string? set = coord.ReapplySetAfterItemsReturned("moving-set", new[] { "torch", "Astral Slippers" });

        Assert.Equal("moving-set", set);
        Assert.Equal(new[] { "moving-set" }, applied);
    }

    // A set put on by hand is on whatever its automation switch says.
    [Fact]
    public void ItemsReturned_TheSetLastApplied_IsAppliedAgain_EvenWhenAutomationMayNotEquipIt()
    {
        EquipmentSettings cfg = Config(
            Wearing(EquipTriggerType.Default, enabled: true, "default-set", "plate mail"),
            Wearing(EquipTriggerType.Backstab, enabled: false, "backstab-set", "black hood"));
        var applied = new List<string>();
        using AutoEquipCoordinator coord = ReturnHarness(cfg, applied);

        Assert.Equal("backstab-set", coord.ReapplySetAfterItemsReturned("backstab-set", new[] { "black hood" }));
    }

    // A torch back in the pack is no gear change, and a piece of some other set
    // goes on when that set next does.
    [Fact]
    public void ItemsReturned_NoneOfThemInTheSet_SendsNoEquip()
    {
        EquipmentSettings cfg = Config(
            Wearing(EquipTriggerType.Default, enabled: true, "default-set", "plate mail"),
            Wearing(EquipTriggerType.PreRestHp, enabled: true, "rest-set", "ring of regeneration"));
        var applied = new List<string>();
        using AutoEquipCoordinator coord = ReturnHarness(cfg, applied);

        Assert.Null(coord.ReapplySetAfterItemsReturned("default-set", new[] { "torch", "ring of regeneration" }));
        Assert.Null(coord.ReapplySetAfterItemsReturned("default-set", Array.Empty<string>()));
        Assert.Empty(applied);
    }

    // The set last applied is kept in memory only. After the client restarts it
    // isn't known, and the set automation would have on right now stands in.
    [Fact]
    public void ItemsReturned_LastSetNotKnown_TheSetAutomationWantsNowStandsIn()
    {
        EquipmentSettings cfg = Config(
            Wearing(EquipTriggerType.Default, enabled: true, "default-set", "plate mail"),
            Wearing(EquipTriggerType.PreRestHp, enabled: true, "rest-set", "ring of regeneration"));
        var applied = new List<string>();
        bool hpGate = false;
        using AutoEquipCoordinator coord = ReturnHarness(cfg, applied, hpGate: () => hpGate);

        Assert.Equal("default-set", coord.ReapplySetAfterItemsReturned(null, new[] { "plate mail" }));

        // Below the rest threshold the rest set is what automation wants: the
        // Default piece waits for Default to come round again.
        hpGate = true;
        Assert.Null(coord.ReapplySetAfterItemsReturned("a set this profile no longer has", new[] { "plate mail" }));
        Assert.Equal("rest-set", coord.ReapplySetAfterItemsReturned(null, new[] { "ring of regeneration" }));
        Assert.Equal(new[] { "default-set", "rest-set" }, applied);
    }

    // A stand-in is automation's own pick, so only a set automation may equip.
    [Fact]
    public void ItemsReturned_LastSetNotKnown_ADisabledSetIsNotAStandIn()
    {
        EquipmentSettings cfg = Config(Wearing(EquipTriggerType.Default, enabled: false, "default-set", "plate mail"));
        var applied = new List<string>();
        using AutoEquipCoordinator coord = ReturnHarness(cfg, applied);

        Assert.Null(coord.ReapplySetAfterItemsReturned(null, new[] { "plate mail" }));
        Assert.Empty(applied);
    }

    [Fact]
    public void ItemsReturned_FollowsAutoAll_AndTheKnownLoadout_AndTheEquipmentManagersAnswer()
    {
        EquipmentSettings cfg = Config(Wearing(EquipTriggerType.Default, enabled: true, "default-set", "plate mail"));
        string[] back = { "plate mail" };

        var applied = new List<string>();
        using (AutoEquipCoordinator off = ReturnHarness(cfg, applied, autoEnabled: () => false))
            Assert.Null(off.ReapplySetAfterItemsReturned("default-set", back));
        using (AutoEquipCoordinator unknown = ReturnHarness(cfg, applied, wornKnown: () => false))
            Assert.Null(unknown.ReapplySetAfterItemsReturned("default-set", back));
        Assert.Empty(applied);

        // Asked, and the equipment manager held it (a swap already streaming).
        using AutoEquipCoordinator busy = ReturnHarness(cfg, applied, result: EquipResult.Busy);
        Assert.Null(busy.ReapplySetAfterItemsReturned("default-set", back));
        Assert.Equal(new[] { "default-set" }, applied);
    }
}
