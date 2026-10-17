using System;
using System.ComponentModel;
using System.Linq;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// Watches the live character posture and auto-swaps the trigger-purposed gear
// sets at the moments the Equipment Manager arms. It is a pure subscriber — it
// reads PlayerState (position) and the HealthManager's recovery gates, and never
// writes any observed field, so it sits outside the single-writer ownership
// model. When a moment fires it resolves the matching enabled EquipmentSet
// through the live EquipmentSettings and hands its id to
// EquipmentManager.ApplyBySetId.
//
// Signal mapping:
//   - Pre-rest HP / Mana: position goes to Resting; the held recovery gate
//     (HP vs MA) disambiguates which set is wanted. Meditating is a mana
//     recovery, so it maps to Pre-rest Mana.
//   - Default: only when the character is DONE recovering (stands up out of a
//     rest with neither rest gate still held) AND a pre-rest swap set is enabled
//     (so we're actually swapping back from one), or when a loop / Auto-Lair run
//     begins (OnLoopStarted). Combat entry does NOT swap to Default by default — a
//     fight that interrupts a rest keeps its pre-rest loadout and reverts only once
//     recovered, per the user's rule (report paradigm-20260826-132742). The one
//     opt-out is EquipmentSettings.SwapToDefaultOnCombat (Equipment Manager's "Don't
//     swap to default upon entering combat" UNchecked): then a rest-interrupting
//     fight swaps to Default on entry (OnCombatChanged) and swaps back to the
//     pre-rest set on room-clear if still gated. The remaining Default path —
//     re-wearing on death-pile recovery — lives in the recovery engine, gated by
//     its own setting.
//
// The Backstab set isn't fired here: the combat engine applies its armor on the
// pre-move hook before the sneak (CombatManager.PrepBackstabForMove →
// EquipmentManager.ApplyBackstabArmor). The decision halves (ClassifyRest,
// ResolveTarget) are pure; the subscription plumbing wires them to the
// live events.
//
// A fired moment is held until the worn loadout is known (InventoryManager
// has parsed at least one full 'i' dump). The apply engine diffs the desired
// set against the live worn set, and an empty worn set reads as "nothing worn"
// rather than "unknown", so firing before the first dump would emit a wear for
// every set item — including one already worn (the game answers "You do not
// have X left unequipped."). Manual applies (the Workshop button,
// @equip <set>) carry explicit intent and aren't gated here.
public sealed class AutoEquipCoordinator : IDisposable
{
    private readonly PlayerState _player;
    private readonly Func<EquipmentSettings> _readEquipment;
    private readonly Func<bool> _hpGateAsserted;
    private readonly Func<bool> _maGateAsserted;
    private readonly Func<string, EquipResult> _applyBySetId;
    private readonly Func<bool> _wornLoadoutKnown;
    private readonly Func<bool> _isAutoEnabled;
    // Room predicates + a live "is the nav engine moving?" probe for the movement /
    // bossing sets. Null (unwired) means "never" — the new sets stay inert.
    private readonly Func<Game.Map.RoomKey, bool>? _isBossRoom;
    private readonly Func<Game.Map.RoomKey, bool>? _isLair;
    private readonly Func<bool>? _isMoving;
    private readonly LogService? _log;
    private readonly Func<DateTimeOffset> _now;

    private PlayerPosition _lastPosition;

    // True while the "While Moving" set is the active auto loadout (worn because the
    // nav engine is travelling). Cleared when we swap away for combat / a boss room /
    // arrival, so the combat-entry and arrival handlers know whether a movement-gear
    // revert is owed.
    private bool _inMovementSet;

    // True while we've swapped to the Default set because we stepped into a game-data
    // lair with "swap to default before lairs" on. Distinct from _inMovementSet so the
    // movement hook (OnMovementStarted, fired on the loop's own resume after the swap
    // gate clears) doesn't immediately yank us back into the While Moving set mid-lair —
    // the thrash that left the character fighting lairs in speed footwear (report
    // paradigm-20260915-124359). Cleared when we step out into a non-lair room (swap
    // back to the movement set), when the run stops, or when the option is turned off.
    // A run of adjacent lairs keeps it set so the gear doesn't flap between them.
    private bool _inLairDefault;

    // Last room the tracker confirmed us into, so combat-entry and the movement
    // handlers can tell whether the current room is a boss room (gear precedence).
    private Game.Map.RoomKey? _currentRoom;

    // An item-cast buff (ItemCastSequencer) temporarily borrows an equip slot: it
    // removes the worn gear, wields the cast item, uses it, then re-equips the gear
    // itself. That transient swap — and the rest it breaks — can trip a posture
    // auto-equip fire that ALSO restores the slot, double-sending the wear
    // (report paradigm-20260815-130733: an "eq griffon shield" restore followed by a
    // redundant "wear griffon shield" the game rejects). Hold auto-equip fires briefly
    // after an item-cast swap so the sequencer's own restore owns the slot — the same
    // "leave the slot to whoever's swapping it" courtesy _combatOwnsWeaponSlot gives
    // the combat engine.
    private DateTimeOffset _lastItemCastSwapAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan ItemCastSwapSuppressWindow = TimeSpan.FromSeconds(4);

    // Recovery-complete swaps back to the Default set in-room (OnRecoveryComplete),
    // and the stand-up that immediately follows would fire a SECOND, redundant
    // Default swap (report paradigm-20260827-125103: the gear swapper double-swapping
    // — a Default apply, then another ~1s later as the walker stepped out). Stamp
    // when a Default actually applies and suppress the stand-up Default within a short
    // window of it, so recovery→Default swaps exactly once. The stand-up path still
    // fires when nothing recently swapped Default (a stand that OnRecoveryComplete
    // didn't cover — e.g. recovery gated off in combat).
    private DateTimeOffset _lastDefaultAppliedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan DefaultReapplySuppressWindow = TimeSpan.FromSeconds(4);

    // Set true after we swap to Default because a fight interrupted a rest (only when
    // EquipmentSettings.SwapToDefaultOnCombat is on). On room-clear it tells us to swap
    // back to the pre-rest set if the rest still isn't done. Reset when the box is off
    // so toggling it mid-fight can't strand the flag.
    private bool _swappedToDefaultForCombat;

    // Debounce for the travelling→Default combat swap. Paradigm flickers
    // *Combat Off* / *Combat Engaged* rapidly, and each engage while in the
    // While-Moving set would yank us into Default and back on every move — the
    // gear thrash of report paradigm-20260914-154019 (astral slippers on/off).
    // When a scheduler is supplied the swap is deferred by a short settle; a
    // *Combat Off* within it cancels the swap (it was a flicker), a sustained
    // fight lets it fire. Null scheduler → swap immediately (a caller that
    // doesn't wire a settle), so the debounce is opt-in via wiring.
    private readonly Func<Action, IDisposable>? _scheduleCombatGearSwap;
    private IDisposable? _pendingMovingCombatSwap;

    // Hand movement (EquipmentSettings.WhileMovingOnManualMoves). _navIdle says no nav
    // engine is running at all — the only time a move counts as typed by hand (an
    // engine's own steps pass the same outbound observer). A typed move has no
    // arrival, so the set comes off after the user-set idle delay with no further
    // typed move; _scheduleAfter arms that revert (null = never reverts).
    private readonly Func<bool>? _navIdle;
    private readonly Func<TimeSpan, Action, IDisposable>? _scheduleAfter;
    // Whether a rest the health engine sent just finished (see OnPositionChanged).
    private readonly Func<bool>? _restJustEnded;
    private bool _inManualMovementSet;
    private IDisposable? _manualIdleRevert;

    // Whether the trip we're on leads to another boss room (EquipmentSettings
    // .KeepBossingBetweenBosses). Null (unwired) means "no".
    // The argument is the boss room just left (it doesn't count as "another").
    private readonly Func<Game.Map.RoomKey?, BossTravel>? _bossTravel;
    private Game.Map.RoomKey? _keptBossingFrom;

    // True while the Bossing set is being kept on between boss rooms. Cleared when
    // another set takes over, the trip turns out not to lead to a boss, or it ends.
    private bool _keptBossing;

    // The set the user picked from the Equip menu and wants left on: every automatic
    // swap is held until they deselect it. Session-only.
    public EquipTriggerType? HeldSet { get; private set; }
    public event Action? HeldSetChanged;

    public bool IsKeepingBossing => _keptBossing;

    // A rest typed by hand gets its pre-rest gear only after the sit, and wearing
    // gear stands a resting character up (GAME_MECHANICS "Gear swaps interrupt
    // resting, not meditating"). So once that gear has gone out, `rest` is sent
    // again, and the stand the wear causes isn't taken for the end of the rest.
    private readonly Action? _resumeRest;
    private readonly Func<bool>? _healthOwnsRest;
    private DateTimeOffset? _reRestOwedSince;
    private DateTimeOffset _lastReRestAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan ReRestWindow = TimeSpan.FromSeconds(5);

    public bool ReRestOwed => _reRestOwedSince is not null;

    public AutoEquipCoordinator(
        PlayerState player,
        Func<EquipmentSettings> readEquipment,
        Func<bool> hpGateAsserted,
        Func<bool> maGateAsserted,
        Func<string, EquipResult> applyBySetId,
        Func<bool> wornLoadoutKnown,
        Func<bool> isAutoEnabled,
        LogService? log = null,
        Func<DateTimeOffset>? now = null,
        Func<Game.Map.RoomKey, bool>? isBossRoom = null,
        Func<Game.Map.RoomKey, bool>? isLair = null,
        Func<bool>? isMoving = null,
        Func<Action, IDisposable>? scheduleCombatGearSwap = null,
        Func<bool>? navIdle = null,
        Func<TimeSpan, Action, IDisposable>? scheduleAfter = null,
        Func<bool>? restJustEnded = null,
        Func<Game.Map.RoomKey?, BossTravel>? bossTravel = null,
        Action? resumeRest = null,
        Func<bool>? healthOwnsRest = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(readEquipment);
        ArgumentNullException.ThrowIfNull(hpGateAsserted);
        ArgumentNullException.ThrowIfNull(maGateAsserted);
        ArgumentNullException.ThrowIfNull(applyBySetId);
        ArgumentNullException.ThrowIfNull(wornLoadoutKnown);
        ArgumentNullException.ThrowIfNull(isAutoEnabled);
        _player = player;
        _readEquipment = readEquipment;
        _hpGateAsserted = hpGateAsserted;
        _maGateAsserted = maGateAsserted;
        _applyBySetId = applyBySetId;
        _wornLoadoutKnown = wornLoadoutKnown;
        _isAutoEnabled = isAutoEnabled;
        _isBossRoom = isBossRoom;
        _isLair = isLair;
        _isMoving = isMoving;
        _scheduleCombatGearSwap = scheduleCombatGearSwap;
        _restJustEnded = restJustEnded;
        _navIdle = navIdle;
        _scheduleAfter = scheduleAfter;
        _bossTravel = bossTravel;
        _resumeRest = resumeRest;
        _healthOwnsRest = healthOwnsRest;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);

        _lastPosition = player.Position;
        _player.PropertyChanged += OnPlayerChanged;
    }

    // Note that an item-cast buff swap just began (ItemCastSequencer): its
    // equip / use / restore dance owns whatever slot it borrowed, so auto-equip
    // fires are held for a short window (see _lastItemCastSwapAt) rather than
    // double-restoring the slot.
    public void NoteItemCastSwap() => _lastItemCastSwapAt = _now();

    // ----- pure decision logic ---------------------------------------------

    // The pre-rest trigger a transition into `to` implies, or null when the new
    // posture isn't a rest / meditate. A held HP gate marks an HP rest;
    // otherwise a held MA gate marks a mana rest; a plain rest with neither gate
    // still known defaults to HP (the common case). Meditation is always a mana
    // recovery.
    internal static EquipTriggerType? ClassifyRest(PlayerPosition to, bool hpGate, bool maGate) => to switch
    {
        PlayerPosition.Meditating => EquipTriggerType.PreRestMana,
        PlayerPosition.Resting when maGate && !hpGate => EquipTriggerType.PreRestMana,
        PlayerPosition.Resting => EquipTriggerType.PreRestHp,
        _ => null,
    };

    // The set id a type moment should apply given the live config, or null when
    // it shouldn't fire — no set exists for the type, the set is disabled, or it
    // has no stable id.
    internal static string? ResolveTarget(EquipmentSettings cfg, EquipTriggerType type)
    {
        EquipmentSet? set = cfg.Sets.FirstOrDefault(s => s.Trigger == type);
        if (set is not { Enabled: true }) return null;
        return string.IsNullOrEmpty(set.Id) ? null : set.Id;
    }

    // ----- signal subscription (UI plumbing) ------------------------------

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerState.InCombat))
        {
            OnCombatChanged(_player.InCombat);
            return;
        }
        if (e.PropertyName != nameof(PlayerState.Position)) return;
        OnPositionChanged(_lastPosition, _player.Position);
        _lastPosition = _player.Position;
    }

    // Opt-in (Equipment Manager's "Don't swap to default upon entering combat"
    // UNchecked → SwapToDefaultOnCombat true): a fight that interrupts a rest is
    // fought in the Default combat loadout instead of the pre-rest set. On combat
    // entry, if we're recovering in a pre-rest set, swap to Default for the fight; on
    // room-clear, if the rest still isn't satisfied, swap back to the pre-rest set the
    // held gate wants (HealthManager re-issues the rest itself). With the box checked
    // (the default) this is inert — the character keeps its pre-rest loadout through the
    // fight and reverts to Default only once recovered, the long-standing rule.
    private void OnCombatChanged(bool inCombat)
    {
        if (inCombat) ForgetReRest();
        // A *Combat Off* cancels a deferred travelling→Default swap — the engage
        // that armed it was flicker, so we stay in the movement set.
        if (!inCombat) CancelPendingMovingCombatSwap();
        // A set deselected mid-fight goes back to Default now that it's over.
        if (!inCombat) SettleReleasedSet();

        if (inCombat)
        {
            // In a boss room the fight is fought in the Bossing set — it wins over the
            // movement / rest swaps. The pre-step swap usually already wore it; this is
            // the idempotent backstop for a boss room entered without a planned step.
            if (CurrentRoomIsBoss() && EnabledSet(EquipTriggerType.Bossing) is not null)
            {
                CancelPendingMovingCombatSwap();
                _inMovementSet = false;
                Fire(EquipTriggerType.Bossing);
                return;
            }
            // A hostile appeared while we were travelling in the movement set — swap to
            // Default and let combat engage. Independent of SwapToDefaultOnCombat, which
            // governs only the rest-interrupt case below. Deferred behind a settle when
            // a scheduler is wired, so Paradigm's rapid combat-state flicker doesn't
            // thrash the movement / Default gear on every brief engage.
            if (_inMovementSet && _pendingMovingCombatSwap is null)
            {
                if (_scheduleCombatGearSwap is null)
                {
                    FireMovingCombatSwap();
                    return;
                }
                _pendingMovingCombatSwap = _scheduleCombatGearSwap(() =>
                {
                    _pendingMovingCombatSwap = null;
                    // Still fighting and still in the movement set → a sustained fight,
                    // gear up. Combat cleared or the posture moved on → the settle
                    // outlived a flicker, do nothing.
                    if (_player.InCombat && _inMovementSet) FireMovingCombatSwap();
                });
                return;
            }
        }

        if (!_readEquipment().SwapToDefaultOnCombat)
        {
            // Toggling the box off mid-fight must not strand the restore flag.
            _swappedToDefaultForCombat = false;
            return;
        }

        if (inCombat)
        {
            // Only when a rest actually swapped us into a pre-rest set: we're recovering
            // (a rest gate is held) AND a pre-rest swap set is enabled. Otherwise there's
            // nothing to swap away from — and nothing to restore on clear.
            if (_swappedToDefaultForCombat) return;
            if (!(_hpGateAsserted() || _maGateAsserted())) return;
            if (!UsingRestingSwapSets()) return;
            _swappedToDefaultForCombat = true;
            _log?.Info(EquipmentManager.LogCategory,
                "combat interrupted a rest — swapping to Default for the fight "
                + "(swap-to-default-on-combat enabled)");
            Fire(EquipTriggerType.Default);
        }
        else
        {
            if (!_swappedToDefaultForCombat) return;
            _swappedToDefaultForCombat = false;
            // Recovered during the fight → gates clear → stay in Default. Still gated →
            // swap back to the set the held gate wants so the resumed rest wears it (HP
            // gate wins when both are held, mirroring ClassifyRest's plain-rest default).
            EquipTriggerType? restType =
                _hpGateAsserted() ? EquipTriggerType.PreRestHp
                : _maGateAsserted() ? EquipTriggerType.PreRestMana
                : (EquipTriggerType?)null;
            if (restType is { } rt)
            {
                _log?.Info(EquipmentManager.LogCategory,
                    $"room cleared, still rest-gated — swapping back to '{rt}' to resume the rest");
                Fire(rt);
            }
        }
    }

    private void FireMovingCombatSwap()
    {
        _inMovementSet = false;
        _log?.Info(EquipmentManager.LogCategory,
            "hostiles recognized while moving — swapping to Default for the fight");
        Fire(EquipTriggerType.Default);
    }

    private void CancelPendingMovingCombatSwap()
    {
        _pendingMovingCombatSwap?.Dispose();
        _pendingMovingCombatSwap = null;
    }

    // A loop or Auto-Lair run just began — swap to the Default (baseline) set.
    // Wired from AppServices to LoopRunner's ReachedFirstWaypoint and AutoLair's
    // ActiveChanged(true). Unconditional (subject only to Fire's own gates): a run
    // starting means we're moving out under normal combat gear, whatever we were
    // wearing while idle / resting beforehand.
    public void OnLoopStarted()
    {
        // With a movement set configured (enabled + non-empty), the movement-state
        // hook (OnMovementStarted) wears it once the run is actually travelling — so
        // don't pre-empt with a Default swap here. Without one, keep the long-standing
        // Default-at-loop-start behavior.
        if (MovementSetActive()) return;
        Fire(EquipTriggerType.Default);
    }

    // ----- movement / boss / lair sets (pushed from AppServices) ----------

    // The nav engine started (or resumed) travelling and we're neither fighting nor
    // resting — wear the movement set. No-op unless a movement set is enabled + has
    // items, we're not already in it, and the current room isn't a boss room (Bossing
    // owns gear there). Idempotent: re-entry while already in the set does nothing.
    public void OnMovementStarted()
    {
        // A nav engine takes over from hand movement — its own stop owns the revert now.
        EndManualMovement();
        if (_inMovementSet) return;
        // The Bossing set is staying on for the trip to the next boss.
        if (_keptBossing)
        {
            if (TravelLeadsToBoss()) return;
            _keptBossing = false;
        }
        // We deliberately swapped to Default for a lair (swap-before-lairs) and this is
        // the loop resuming the step once the swap gate cleared — re-wearing the movement
        // set here would instantly undo the lair loadout (report paradigm-20260915-124359).
        // The pre-move handler swaps back on the way into a non-lair room. Self-heals: if
        // the option is turned off mid-run the latch drops and the movement set resumes.
        if (_inLairDefault && _readEquipment().SwapToDefaultBeforeLairs) return;
        _inLairDefault = false;
        if (_player.InCombat) return;
        // Hold the pre-rest set only while we're actually SITTING (a rest posture) — not
        // on a lingering rest-gate flag. A rest gate stays asserted until the pool tops
        // off at rest-target, but the loop can resume travelling before then (e.g. the HP
        // rest finished and it moved on while mana is still below its target). Gating on
        // the gate FLAG left the character travelling — and fighting — in weak pre-rest
        // gear for rooms on end (report paradigm-20260916-104923). Once we're up and
        // moving, wear the movement set; the pre-rest set resumes only if we sit again.
        if (IsRestPosture(_player.Position)) return;
        if (CurrentRoomIsBoss()) return;
        if (MovementSetActive())
        {
            _inMovementSet = true;
            _log?.Info(EquipmentManager.LogCategory, "nav engine moving — wearing the While Moving set");
            Fire(EquipTriggerType.WhileMoving);
            return;
        }
        // No While-Moving set configured — travel in Default. Without this, a rest /
        // combat swap-back (e.g. the "still rest-gated → Pre-rest" swap when combat
        // clears) is never corrected on the resume, so the loop travels — and fights —
        // in weak pre-rest gear (report paradigm-20260916-104923: While-Moving disabled,
        // walked 4 rooms into a monster room in Pre-rest). Fire is diff-based, so this
        // no-ops when we're already in Default.
        Fire(EquipTriggerType.Default);
    }

    // A move just went out (OutboundMovementObserver.MoveSent). With the opt-in on and
    // no nav engine running, it was typed by hand: wear the While Moving set under the
    // same rules the engine path uses (not fighting / resting / in a boss room), and
    // (re)arm the idle revert. An engine's own steps land here too and are ignored —
    // OnMovementStarted / Stopped own that gear.
    public void OnMoveSent()
    {
        ForgetReRest();   // a typed move stands us up on purpose
        EquipmentSettings cfg = _readEquipment();
        if (!cfg.WhileMovingOnManualMoves) { EndManualMovement(); return; }
        if (_navIdle?.Invoke() != true) return;
        if (!MovementSetActive()) return;
        if (_player.InCombat || IsRestPosture(_player.Position) || CurrentRoomIsBoss()) return;

        ArmManualIdleRevert(TimeSpan.FromSeconds(Math.Max(1, cfg.WhileMovingManualIdleSeconds)));
        if (_inMovementSet) return;   // already wearing it (this burst, or a fight ended mid-burst)
        _inMovementSet = true;
        _inManualMovementSet = true;
        _log?.Info(EquipmentManager.LogCategory, "moving by hand — wearing the While Moving set");
        Fire(EquipTriggerType.WhileMoving);
    }

    private void ArmManualIdleRevert(TimeSpan delay)
    {
        _manualIdleRevert?.Dispose();
        _manualIdleRevert = _scheduleAfter?.Invoke(delay, OnManualMovementIdle);
    }

    // No typed move for the idle delay: take the hand-movement set off — unless a fight,
    // a rest or a boss room has claimed the gear since (their own handlers own it), or a
    // nav engine started (OnMovementStarted already handed over).
    private void OnManualMovementIdle()
    {
        _manualIdleRevert = null;
        if (!_inManualMovementSet) return;
        _inManualMovementSet = false;
        if (!_inMovementSet) return;
        if (_navIdle?.Invoke() != true) return;
        _inMovementSet = false;
        if (_player.InCombat || _hpGateAsserted() || _maGateAsserted() || CurrentRoomIsBoss()) return;
        _log?.Info(EquipmentManager.LogCategory,
            $"no move typed for {Math.Max(1, _readEquipment().WhileMovingManualIdleSeconds)}s — reverting the While Moving set to Default");
        Fire(EquipTriggerType.Default);
    }

    private void EndManualMovement()
    {
        _manualIdleRevert?.Dispose();
        _manualIdleRevert = null;
        _inManualMovementSet = false;
    }

    // The nav engine went fully idle — a walk-to reached its destination, or a loop /
    // Auto-Lair run stopped. Revert the movement gear to Default. A pause for combat or
    // rest is NOT idle (the run stays live), so those keep their own gear; only a true
    // stop reverts.
    public void OnMovementStopped()
    {
        // A run that ended in (or at the edge of) a lair is already in the Default set —
        // just clear the lair-travel latch; there's nothing to revert.
        if (_inLairDefault)
        {
            _inLairDefault = false;
            return;
        }
        // The trip the Bossing set was kept on for ended somewhere that isn't a boss room.
        if (_keptBossing)
        {
            _keptBossing = false;
            if (_player.InCombat || _hpGateAsserted() || _maGateAsserted() || CurrentRoomIsBoss()) return;
            _log?.Info(EquipmentManager.LogCategory, "movement stopped short of a boss room — reverting the Bossing set to Default");
            Fire(EquipTriggerType.Default);
            return;
        }
        if (!_inMovementSet) return;
        _inMovementSet = false;
        if (_player.InCombat || _hpGateAsserted() || _maGateAsserted() || CurrentRoomIsBoss()) return;
        _log?.Info(EquipmentManager.LogCategory, "movement stopped — reverting the While Moving set to Default");
        Fire(EquipTriggerType.Default);
    }

    // The tracked room changed (a confirmed transition). Drives the Bossing set:
    // entering a boss room wears it, leaving one reverts — to the movement set if we're
    // still travelling, otherwise Default (the user's "back to default on leaving").
    public void OnRoomChanged(Game.Map.RoomKey? previous, Game.Map.RoomKey current)
    {
        _currentRoom = current;
        bool curBoss = _isBossRoom is { } cb && cb(current);
        bool prevBoss = _isBossRoom is { } pb && previous is { } pr && pb(pr);
        if (curBoss)
        {
            // Backstop for a boss room entered without a pre-step swap (the pre-move
            // hook usually wore Bossing already; ApplySet no-ops if it's on).
            _keptBossing = false;
            if (EnabledSet(EquipTriggerType.Bossing) is not null)
            {
                _inMovementSet = false;
                Fire(EquipTriggerType.Bossing);
            }
            return;
        }
        // Only revert on leaving if a Bossing set is actually configured. Without one we
        // never wore boss gear on entry (every boss-entry path gates on EnabledSet), so
        // there's nothing to undo — and the game-data Bosses table tags plenty of mundane
        // rooms (a town room with a named NPC). A character with no Bossing set was being
        // yanked out of its While Moving gear into Default every time it stepped out of
        // such a room mid-walk (report paradigm-20260915-061646 / -061734).
        if (prevBoss && EnabledSet(EquipTriggerType.Bossing) is not null)
        {
            // Opt-in: on the way to another boss the Bossing set stays on. A follower's
            // answer comes from its leader a moment later (Asking), so it's kept
            // meanwhile and OnBossTravelResolved reverts it if the answer is no.
            if (_readEquipment().KeepBossingBetweenBosses && HeldSet is null
                && _bossTravel?.Invoke(previous) is { } travel and not BossTravel.No)
            {
                _inMovementSet = false;
                _keptBossing = true;
                _keptBossingFrom = previous;
                _log?.Info(EquipmentManager.LogCategory, travel == BossTravel.Yes
                    ? "left the boss room heading for another boss — keeping the Bossing set on"
                    : "left the boss room — keeping the Bossing set on while the leader says where it's going");
                return;
            }
            RevertFromBossing("left the boss room — reverting to Default");
        }
    }

    // Stepping out of boss gear always reverts to Default FIRST (clears the boss
    // loadout), then re-layers the movement set if we're still travelling.
    private void RevertFromBossing(string why)
    {
        _keptBossing = false;
        _inMovementSet = false;
        _log?.Info(EquipmentManager.LogCategory, why);
        Fire(EquipTriggerType.Default);
        if (MovementSetActive() && (_isMoving?.Invoke() ?? false) && !_player.InCombat)
        {
            _inMovementSet = true;
            _log?.Info(EquipmentManager.LogCategory, "still travelling — back to the While Moving set");
            Fire(EquipTriggerType.WhileMoving);
        }
    }

    private bool TravelLeadsToBoss() => _bossTravel?.Invoke(_keptBossingFrom) is { } t and not BossTravel.No;

    // The leader answered (or didn't) where it's going. Not to a boss: the Bossing
    // set we kept on comes off, unless a fight, a rest or a boss room owns the gear.
    public void OnBossTravelResolved(bool headingToBoss)
    {
        if (!_keptBossing || headingToBoss) return;
        if (CurrentRoomIsBoss()) { _keptBossing = false; return; }
        if (_player.InCombat || _hpGateAsserted() || _maGateAsserted())
        {
            // Their own handlers swap the gear; just stop keeping it.
            _keptBossing = false;
            return;
        }
        RevertFromBossing("the trip isn't to a boss room — reverting the Bossing set to Default");
    }

    // ----- a set held from the Equip menu ---------------------------------

    // The user picked a set from the Equip menu: leave it on. Every automatic swap is
    // held (Fire) until ReleaseHeldSet.
    public void HoldSet(EquipTriggerType type)
    {
        if (type == EquipTriggerType.Default || HeldSet == type) return;
        HeldSet = type;
        _releasedSet = null;
        CancelPendingMovingCombatSwap();
        EndManualMovement();
        _inMovementSet = false;
        _inLairDefault = false;
        _keptBossing = false;
        _log?.Info(EquipmentManager.LogCategory, $"'{type}' held from the Equip menu — automatic gear swaps are off until it's deselected");
        HeldSetChanged?.Invoke();
    }

    // The user deselected the held set: automatic swaps resume. The set comes off for
    // Default, then whatever automation wants right now goes on over it (user,
    // 2026-10-03). Two exceptions: mid-fight the swap waits until combat clears, and
    // when the released set is itself what automation wants (a pre-rest set while
    // resting, say) it stays on for automation to use and revert in its own time.
    // quiet just ends the hold: a profile change, or another equip asked for by hand.
    public void ReleaseHeldSet(bool quiet = false)
    {
        if (HeldSet is not { } was) return;
        HeldSet = null;
        _log?.Info(EquipmentManager.LogCategory, $"'{was}' deselected — automatic gear swaps resume");
        HeldSetChanged?.Invoke();
        if (quiet) { _releasedSet = null; return; }
        _releasedSet = was;
        if (_player.InCombat)
        {
            _log?.Info(EquipmentManager.LogCategory, "still fighting — going back to Default once combat clears");
            return;
        }
        SettleReleasedSet();
    }

    // The set just deselected, until the swap back to Default it owes has happened.
    private EquipTriggerType? _releasedSet;
    public EquipTriggerType? ReleasedSetAwaitingDefault => _releasedSet;

    private void SettleReleasedSet()
    {
        if (_releasedSet is not { } released) return;
        _releasedSet = null;
        EquipTriggerType wanted = WantedNow();
        if (wanted == released)
        {
            AdoptReleasedSet(released);
            return;
        }
        Fire(EquipTriggerType.Default);
        if (wanted == EquipTriggerType.Default) return;
        if (wanted == EquipTriggerType.WhileMoving) _inMovementSet = true;
        Fire(wanted);
    }

    private void AdoptReleasedSet(EquipTriggerType released)
    {
        if (released == EquipTriggerType.WhileMoving) _inMovementSet = true;
        _log?.Info(EquipmentManager.LogCategory,
            $"'{released}' is what automation wants right now — leaving it on for it to use");
    }

    // The set automation would be wearing at this moment.
    private EquipTriggerType WantedNow()
    {
        if (CurrentRoomIsBoss() && EnabledSet(EquipTriggerType.Bossing) is not null)
            return EquipTriggerType.Bossing;
        if (_player.InCombat) return EquipTriggerType.Default;
        bool hp = _hpGateAsserted(), ma = _maGateAsserted();
        bool sitting = IsRestPosture(_player.Position);
        // A held rest gate means a rest is about to be (or being) taken.
        if ((sitting || hp || ma)
            && ClassifyRest(sitting ? _player.Position : PlayerPosition.Resting, hp, ma) is { } rest
            && ResolveTarget(_readEquipment(), rest) is not null)
            return rest;
        if (MovementSetActive() && (_isMoving?.Invoke() ?? false)) return EquipTriggerType.WhileMoving;
        return EquipTriggerType.Default;
    }

    // ----- gear that came back to the pack --------------------------------

    // Pieces were taken off the character by something that was no gear swap and
    // are back in the pack: a hang-up penalty dropped them and they were picked up
    // again (HangupItemRecheck). There is no recovery rule of its own for that
    // (user, 2026-10-09): the set that was on is applied again, and the equipment
    // manager works out what to wear under the rules every automatic apply has.
    //   lastSetId — EquipmentManager.CurrentSetId, the set last applied. It is kept
    //               in memory only, so it is known after a reconnect and not after
    //               the client restarts; then the set automation would have on
    //               right now stands in for it.
    //   returned  — the item names that came back. With none of them in the set no
    //               equip is sent: a torch back in the pack is no gear change, and
    //               a piece of some other set goes on when that set next does.
    // The name of the set an apply went out for, else null.
    public string? ReapplySetAfterItemsReturned(string? lastSetId, IReadOnlyCollection<string> returned)
    {
        ArgumentNullException.ThrowIfNull(returned);
        if (returned.Count == 0) return null;
        if (!_isAutoEnabled())
        {
            _log?.Info(EquipmentManager.LogCategory, "gear came back to the pack, but Auto-All is off — no set applied");
            return null;
        }

        EquipmentSettings cfg = _readEquipment();
        // The set that was on was put there by automation or by hand, so its
        // Enabled switch isn't asked again; a stand-in is automation's own pick,
        // and takes only a set automation may equip.
        EquipmentSet? set = string.IsNullOrEmpty(lastSetId)
            ? null
            : cfg.Sets.FirstOrDefault(s => string.Equals(s.Id, lastSetId, StringComparison.Ordinal));
        bool standIn = set is null;
        if (standIn && ResolveTarget(cfg, WantedNow()) is { } wantedId)
            set = cfg.Sets.FirstOrDefault(s => string.Equals(s.Id, wantedId, StringComparison.Ordinal));
        if (set is null || string.IsNullOrEmpty(set.Id))
        {
            _log?.Info(EquipmentManager.LogCategory, "gear came back to the pack, but no gear set is on or enabled — nothing applied");
            return null;
        }

        var back = new HashSet<string>(returned, StringComparer.OrdinalIgnoreCase);
        if (!set.Slots.Any(e => e.ItemName?.Trim() is { Length: > 0 } item && back.Contains(item)))
        {
            _log?.Info(EquipmentManager.LogCategory,
                $"gear came back to the pack, none of it part of gear set '{set.Name}' — no equip sent");
            return null;
        }
        if (!_wornLoadoutKnown())
        {
            _log?.Info(EquipmentManager.LogCategory,
                $"gear came back to the pack, but the worn loadout isn't known — gear set '{set.Name}' not applied");
            return null;
        }

        EquipResult result = _applyBySetId(set.Id);
        _log?.Info(EquipmentManager.LogCategory,
            $"gear came back to the pack — gear set '{set.Name}' "
            + (standIn ? "(what automation wants on now; the set last applied isn't known) " : "(the set last applied) ")
            + (result == EquipResult.Applied ? "applied again" : $"not applied again ({result})"));
        return result == EquipResult.Applied ? set.Name : null;
    }

    // About to send a step INTO `next`. Swap BEFORE the wire move so we land already
    // geared — the swap's wear/eq commands queue ahead of the move on the serialized
    // wire (same ordering the pre-move backstab prep relies on). A boss room wears the
    // Bossing set; a known lair wears Default when the movement set's "swap before
    // lairs" option is on and we're currently travelling in the movement set.
    public void OnAboutToEnterRoom(Game.Map.RoomKey next)
    {
        if (_player.InCombat) return;
        if (_isBossRoom is { } boss && boss(next))
        {
            if (EnabledSet(EquipTriggerType.Bossing) is not null)
            {
                _keptBossing = false;
                _inMovementSet = false;
                _log?.Info(EquipmentManager.LogCategory, "about to enter a boss room — wearing the Bossing set before the step");
                Fire(EquipTriggerType.Bossing);
            }
            return;
        }
        // The route changed under a kept Bossing set and no longer ends at a boss.
        if (_keptBossing && !TravelLeadsToBoss())
            RevertFromBossing("no longer heading for a boss room — reverting the Bossing set to Default");
        if (_keptBossing) return;
        // Lair gear swap (opt-in "swap to default before lairs"): wear the Default set in
        // game-data lair rooms and the While Moving set in the transit rooms between them.
        // Both directions fire on the pre-move hook so we land already geared for the room
        // we're stepping into. A run of adjacent lairs stays in Default (no flap); a gap
        // re-wears the movement set for the walk and swaps back to Default at the next lair
        // (reports paradigm-20260915-124359 / -130624).
        if (_readEquipment().SwapToDefaultBeforeLairs
            && MovementSetActive()
            && _isLair is { } lair)
        {
            bool nextIsLair = lair(next);
            if (nextIsLair && _inMovementSet)
            {
                _inMovementSet = false;
                _inLairDefault = true;
                _log?.Info(EquipmentManager.LogCategory, "about to enter a lair — swapping to Default before the step");
                Fire(EquipTriggerType.Default);
            }
            else if (!nextIsLair && _inLairDefault)
            {
                _inLairDefault = false;
                _inMovementSet = true;
                _log?.Info(EquipmentManager.LogCategory, "leaving the lair — back to the While Moving set before the step");
                Fire(EquipTriggerType.WhileMoving);
            }
        }
    }

    // The enabled, non-empty set for a trigger, or null. An empty set (no slots) is
    // treated as absent so it can't shadow a fallback — e.g. an empty movement set must
    // not suppress the loop-start Default.
    private EquipmentSet? EnabledSet(EquipTriggerType type)
    {
        EquipmentSet? set = _readEquipment().Sets.FirstOrDefault(s => s.Trigger == type);
        return set is { Enabled: true } && set.Slots.Count > 0 ? set : null;
    }

    // Whether the movement set takes over travel gear (enabled + has items).
    private bool MovementSetActive() => EnabledSet(EquipTriggerType.WhileMoving) is not null;

    private bool CurrentRoomIsBoss() =>
        _isBossRoom is { } probe && _currentRoom is { } r && probe(r);

    // Recovery just topped off to rest-max (a held rest gate cleared), fired from
    // HealthManager while the character is STILL resting in the room — before the
    // loop's deferred step-out. Swapping back to Default here (rather than on the
    // later stand, which IS the move) lets the swap complete in-room; the paced
    // apply holds the loop via the gear-swap movement gate, so we step out already
    // in Default gear instead of streaming the wears into the next room mid-combat
    // (report paradigm-20260826-140341). Gated on using rest swap sets (else there's
    // nothing to revert) and skipped in combat — a fight that interrupted recovery
    // is fought in the current loadout, per the rule.
    public void OnRecoveryComplete()
    {
        if (_player.InCombat || !UsingRestingSwapSets()) return;
        Fire(EquipTriggerType.Default);
    }

    // A swap just finished streaming and left a pre-rest set as the loadout. The
    // revert to Default is owed only when the rest that set was for is already over:
    // its recovery-complete revert ran before the gear was on, and would leave the
    // character walking on in rest gear (report paradigm-20260903-111227). "No rest
    // gate held" alone doesn't say that — a rest or meditate typed by hand, or a
    // top-off while the party waits, never had one, and reverting there re-wore the
    // Default piece in every slot the pre-rest set leaves alone, over whatever had
    // been put on by hand (report paradigm-20261008-202210). Such a set comes off at
    // the stand instead.
    public void OnPreRestSetStreamed()
    {
        if (_reRestOwedSince is { } owedSince)
        {
            _reRestOwedSince = null;
            if (!_player.InCombat && _now() - owedSince <= ReRestWindow)
            {
                _lastReRestAt = _now();
                _log?.Info(EquipmentManager.LogCategory,
                    "the rest gear's wear stood a hand-typed rest up — resting again");
                _resumeRest!();
                return;
            }
        }
        if (_hpGateAsserted() || _maGateAsserted()) return;
        if (_restJustEnded?.Invoke() != true) return;
        _log?.Info(EquipmentManager.LogCategory,
            "the rest finished while its gear was going on — reverting to Default");
        OnRecoveryComplete();
    }

    private void OnPositionChanged(PlayerPosition from, PlayerPosition to)
    {
        if (from == to) return;
        if (ClassifyRest(to, _hpGateAsserted(), _maGateAsserted()) is { } restType)
        {
            // Never swap into a pre-rest set while fighting. You can't rest in combat, so
            // a sit/rest-posture mid-fight is transient — swapping to weak pre-rest gear
            // there gets the character ravaged, and it thrashes against the
            // swap-to-Default-on-combat restore (report paradigm-20260916-104923). Combat
            // gear holds until OnCombatChanged restores the rest set once combat clears.
            if (_player.InCombat) return;
            // The server confirmed the sit after the rest it began had already
            // finished — a regen tick topped the pool off on the very next prompt, so
            // the gates cleared before "You are now meditating" arrived. Wearing the
            // pre-rest set now would walk the next room in it and swap it all back
            // there, under attack (report paradigm-20260929-115648).
            if (!(_hpGateAsserted() || _maGateAsserted()) && _restJustEnded?.Invoke() == true)
            {
                _log?.Info(EquipmentManager.LogCategory,
                    $"sat down after the rest had already finished — not swapping into '{restType}'");
                return;
            }
            // Sitting to rest leaves the movement set — clear the latch so the next
            // OnMovementStarted re-applies travel gear on the resume (otherwise the
            // idempotency guard thinks we're still travelling and never re-wears it).
            _inMovementSet = false;
            bool ungated = !(_hpGateAsserted() || _maGateAsserted());
            if (Fire(restType) && ungated)
            {
                _log?.Info(EquipmentManager.LogCategory,
                    $"sat down with no rest gate held — '{restType}' stays on until the stand");
                // One re-rest per sit: the sit it brings finds the gear already on.
                if (to == PlayerPosition.Resting && _resumeRest is not null
                    && _healthOwnsRest?.Invoke() != true
                    && _now() - _lastReRestAt > ReRestWindow)
                    _reRestOwedSince = _now();
            }
        }
        else if (to == PlayerPosition.Standing && IsRestPosture(from))
        {
            // Only revert to Default when we're actually swapping BACK from a
            // pre-rest set — i.e. a pre-rest swap set is enabled. Without one the
            // rest never left Default (or the user hand-equipped some other set),
            // so a Default fire here would clobber that loadout for nothing.
            if (!UsingRestingSwapSets()) return;
            // The rest gear's own wear stood a hand-typed rest up, and `rest` is (or
            // is about to be) sent again: the rest isn't over.
            if ((_reRestOwedSince is { } owed && _now() - owed <= ReRestWindow)
                || _now() - _lastReRestAt <= ReRestWindow)
            {
                _log?.Debug(EquipmentManager.LogCategory,
                    "auto-equip 'Default' held: the rest gear's wear stood us up and the rest is being resumed");
                return;
            }
            // Standing up mid-recovery is transient — a between-round cast, a loot
            // grab, or the pre-rest swap's own wear breaks rest, and the
            // HealthManager immediately re-issues it. Reverting to Default here
            // starts a swap→stand→swap thrash that re-arms the pre-rest set every
            // cycle and never lets the character actually recover (report
            // paradigm-20260826-132742). Only fall back to Default once recovery is
            // done — neither rest gate is still held, i.e. the pool has reached
            // rest-max (the gates clear at rest-target; see HealthManager).
            if (_hpGateAsserted() || _maGateAsserted())
            {
                _log?.Debug(EquipmentManager.LogCategory,
                    "auto-equip 'Default' held: still recovering (rest gate asserted) — "
                    + "not reverting the pre-rest set until the pool reaches rest-max");
                return;
            }
            // OnRecoveryComplete already swapped back to Default in-room, just before
            // this stand; don't fire a second, redundant Default swap on the stand-up
            // (report paradigm-20260827-125103). Only fires here when nothing recently
            // reverted Default — a stand OnRecoveryComplete didn't cover.
            if (_now() - _lastDefaultAppliedAt < DefaultReapplySuppressWindow)
            {
                _log?.Debug(EquipmentManager.LogCategory,
                    "auto-equip 'Default' held: recovery already reverted to Default in-room "
                    + "(suppressing the redundant stand-up swap)");
                return;
            }
            Fire(EquipTriggerType.Default);   // recovered — back to baseline
        }
    }

    // True when a pre-rest swap set (HP or Mana) is enabled — i.e. resting swaps
    // the loadout, so finishing a rest should swap it back. Read live off the
    // config so enabling / disabling a set takes effect without a restart.
    private bool UsingRestingSwapSets()
    {
        EquipmentSettings cfg = _readEquipment();
        return cfg.Sets.Any(s => s.Enabled
            && (s.Trigger == EquipTriggerType.PreRestHp || s.Trigger == EquipTriggerType.PreRestMana));
    }

    private void ForgetReRest()
    {
        _reRestOwedSince = null;
        _lastReRestAt = DateTimeOffset.MinValue;
    }

    private static bool IsRestPosture(PlayerPosition p) =>
        p is PlayerPosition.Resting or PlayerPosition.Meditating;

    private bool Fire(EquipTriggerType type)
    {
        // Respect the automation master switch — with the Auto-All kill-switch
        // engaged the user has silenced every engine, so a posture transition or
        // loop start must not auto-swap gear. Explicit applies (Workshop "Apply
        // Now", "Equip All", @equip <set>) don't flow through here, so they still
        // work with the kill-switch on.
        if (!_isAutoEnabled()) return false;
        if (HeldSet is { } held)
        {
            _log?.Debug(EquipmentManager.LogCategory,
                $"auto-equip '{type}' held: '{held}' was picked from the Equip menu and stays on until deselected");
            return false;
        }
        // An item-cast buff swap just borrowed an equip slot and restores it itself
        // (see _lastItemCastSwapAt) — often the very rest-break that swap caused is
        // what fired this. Hold so the sequencer's own restore isn't doubled.
        if (_now() - _lastItemCastSwapAt < ItemCastSwapSuppressWindow)
        {
            _log?.Debug(EquipmentManager.LogCategory,
                $"auto-equip '{type}' held: item-cast swap in progress (its own restore owns the slot)");
            return false;
        }
        // Automation wants a set before a deselected one has been swapped off (it was
        // released mid-fight). The same set stays on for it; any other goes on over
        // Default, as it would have if the release had settled first.
        if (_releasedSet is { } released)
        {
            _releasedSet = null;
            if (type == released)
            {
                AdoptReleasedSet(released);
                return false;   // nothing was sent, so nothing for a caller to wait on
            }
            if (type != EquipTriggerType.Default) Fire(EquipTriggerType.Default);
        }
        if (ResolveTarget(_readEquipment(), type) is not { } setId) return false;
        // Hold the fire until a full 'i' has established the worn set. Diffing a
        // set against an empty (never-parsed) loadout treats every item as unworn
        // and emits redundant `wear`s — e.g. re-wielding the weapon already held.
        if (!_wornLoadoutKnown())
        {
            _log?.Debug(EquipmentManager.LogCategory,
                $"auto-equip '{type}' held: worn loadout unknown (no inventory dump yet)");
            return false;
        }
        if (_applyBySetId(setId) != EquipResult.Applied) return false;
        // Stamp a real Default swap so the stand-up that follows a recovery-
        // complete revert doesn't fire a second one (report -125103).
        if (type == EquipTriggerType.Default) _lastDefaultAppliedAt = _now();
        // Another set went on (a rest on the way, say): the Bossing set isn't kept any more.
        if (type != EquipTriggerType.Bossing) _keptBossing = false;
        _log?.Info(EquipmentManager.LogCategory, $"auto-equip '{type}' applied its set");
        return true;
    }

    // The health engine is about to send `rest`: put on the pre-rest set the held
    // gates call for first, since a wear after the sit breaks the rest and resting
    // again restarts its timer. True when a swap started — the rest waits for it.
    public bool WearRestGearBeforeResting()
    {
        if (_player.InCombat) return false;
        if (ClassifyRest(PlayerPosition.Resting, _hpGateAsserted(), _maGateAsserted()) is not { } type)
            return false;
        _inMovementSet = false;
        return Fire(type);
    }

    public void Dispose()
    {
        CancelPendingMovingCombatSwap();
        EndManualMovement();
        _player.PropertyChanged -= OnPlayerChanged;
    }
}
