using System.Text;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Cash;

/// <summary>
/// Phase 9 PR 9.E follow-up — auto-deposit reroute. When
/// <see cref="CashManager.AutoDepositRequested"/> fires (a wealth /
/// coin-count gate crossed with a bank / stash location configured),
/// this manager detours the running movement engine to the configured
/// location, offloads the excess coin, walks back to where it left off,
/// and restarts the captured engine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Loop / Auto-Lair only.</b> A reroute only makes sense for an
/// indefinitely-running engine the user wants to keep alive across the
/// detour. A one-shot walk-to has a single fixed destination and no
/// "resume" semantics, so an active <see cref="AutoWalkManager"/> (or an
/// idle stack) is left alone — the gate fired but nothing reroutes.
/// </para>
/// <para>
/// <b>Stop-and-restart, not gate-pause</b> — same reasoning as
/// <see cref="Remote.PartyComebackManager"/>: a
/// <see cref="MovementCoordinator"/> gate would block the detour walk
/// itself. We snapshot the running engine, <c>Stop()</c> it, run the
/// detour gate-clean, then re-<c>Start</c> it.
/// </para>
/// <para>
/// <b>Bank vs. stash.</b> The configured
/// <see cref="CashSettings.BankRoomKey"/> is classified against the
/// character's <see cref="CharacterProfile.StashRooms"/>: a match is a
/// stash room (on arrival, <see cref="StashRoomManager.NoteRoomEntered"/>
/// auto-fires the per-currency <c>hide</c> commands — this manager only
/// has to walk there), anything else is a bank (this manager sends a
/// single <c>dep &lt;value&gt;</c> for the held wealth above the
/// keep-on-hand floors).
/// </para>
/// <para>
/// Everything runs on the UI thread (<see cref="AutoWalkManager.Event"/>
/// and the <see cref="CashManager.AutoDepositRequested"/> event both fire
/// there), so no marshalling is needed. Single-flight: a second gate
/// crossing while a reroute is in progress is ignored (the
/// <see cref="CashManager"/> single-fire guard already suppresses
/// re-fires until both gates fall back below threshold).
/// </para>
/// </remarks>
public sealed class AutoDepositManager : IDisposable
{
    private const string LogCategory = "AutoDeposit";

    private readonly CashManager _cash;
    private readonly Func<CashSettings> _readCash;
    private readonly Func<InventorySnapshot> _getSnapshot;
    private readonly ProfileService _profile;
    private readonly RoomTracker _tracker;
    private readonly AutoWalkManager _walker;
    private readonly LoopRunner _loopRunner;
    private readonly AutoLairManager _autoLair;
    private readonly LogService? _log;

    private Action<byte[]>? _wireSender;
    private bool _disposed;
    private bool _busy;
    private DepositPhase _phase = DepositPhase.Idle;
    private ResumeTarget _resume;
    private RoomKey _destination;
    private RoomKey _origin;
    private bool _destinationIsStash;

    public AutoDepositManager(
        CashManager cash,
        Func<CashSettings> readCash,
        Func<InventorySnapshot> getSnapshot,
        ProfileService profile,
        RoomTracker tracker,
        AutoWalkManager walker,
        LoopRunner loopRunner,
        AutoLairManager autoLair,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cash);
        ArgumentNullException.ThrowIfNull(readCash);
        ArgumentNullException.ThrowIfNull(getSnapshot);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(autoLair);
        _cash = cash;
        _readCash = readCash;
        _getSnapshot = getSnapshot;
        _profile = profile;
        _tracker = tracker;
        _walker = walker;
        _loopRunner = loopRunner;
        _autoLair = autoLair;
        _log = log;

        _cash.AutoDepositRequested += OnAutoDepositRequested;
        _walker.Event += OnWalkEvent;
    }

    /// <summary>Bind the wire sender — typically the gate-wrapped engine
    /// pipeline from <c>MainWindowViewModel</c>. The bank <c>dep</c>
    /// command travels this path; the stash path uses
    /// <see cref="StashRoomManager"/>'s own sender.</summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    private void OnAutoDepositRequested(long wealthValue)
    {
        if (_busy) return;

        // Snapshot BEFORE stopping anything — Stop() clears the engine's
        // run-state, so the resume target must be captured first. Only a
        // Loop or Auto-Lair qualifies; a one-shot walk-to (or an idle
        // stack) has nothing to detour-and-resume.
        ResumeTarget resume = SnapshotRunningEngine();
        if (resume.Kind == ResumeKind.None)
        {
            _log?.Debug(LogCategory, "gate fired but no loop / auto-lair running — ignoring");
            return;
        }

        CashSettings cash = _readCash();
        if (!RoomKey.TryParseWire(cash.BankRoomKey, out RoomKey destination))
        {
            _log?.Warn(LogCategory, $"gate fired but BankRoomKey '{cash.BankRoomKey}' is unparseable — ignoring");
            return;
        }

        // Capture where we left off so we can walk back and restart the
        // engine at the same spot. No known room → nowhere to return to,
        // so don't strand the engine mid-circuit.
        if (_tracker.State.CurrentRoom is not { } current)
        {
            _log?.Warn(LogCategory, "gate fired but current room is unknown — can't reroute");
            return;
        }

        bool destinationIsStash = IsStashRoom(destination);

        // Stash-on-path skip: when the configured stash room already sits
        // on the running engine's path (a loop circuit room, or a marked
        // Auto-Lair room), don't detour — the engine passes through it on
        // its own and StashRoomManager fires the per-coin `hide` on arrival.
        // Banks are exempt: the `dep` only goes out on a deliberate detour,
        // never passively on arrival, so a bank always reroutes even when
        // it's on-path.
        if (destinationIsStash && IsDestinationOnEnginePath(destination, resume))
        {
            _log?.Info(LogCategory,
                $"stash {destination} is on the running {resume.Kind} path — " +
                "skipping detour; StashRoomManager handles the hide on the next pass");
            return;
        }

        _busy = true;
        _resume = resume;
        _destination = destination;
        _origin = current.Key;
        _destinationIsStash = destinationIsStash;
        _log?.Info(LogCategory,
            $"auto-deposit reroute wealth={wealthValue} dest={destination} " +
            $"kind={(_destinationIsStash ? "stash" : "bank")} resume={resume.Kind} origin={_origin}");

        // Stop the running engine so the detour walk owns the wire and
        // runs without a competing command stream or asserted gate.
        StopRunningEngine("auto-deposit reroute");

        _phase = DepositPhase.WalkingToDestination;
        if (!_walker.WalkTo(destination))
        {
            _log?.Warn(LogCategory, $"can't reach {destination} — resuming");
            Resume();
        }
    }

    private void OnWalkEvent(WalkEvent e)
    {
        if (!_busy) return;
        switch (_phase)
        {
            case DepositPhase.WalkingToDestination:
                if (e.Kind == WalkEventKind.Finished) OnArrivedAtDestination();
                else if (e.Kind == WalkEventKind.Failed)
                {
                    _log?.Warn(LogCategory, "detour path failed — resuming");
                    Resume();
                }
                break;
            case DepositPhase.WalkingBackToOrigin:
                if (e.Kind == WalkEventKind.Finished) Resume();
                else if (e.Kind == WalkEventKind.Failed)
                {
                    // Couldn't get back to the exact origin; resume the
                    // engine anyway — Loop / Auto-Lair re-approach their
                    // own waypoints from wherever we ended up.
                    _log?.Warn(LogCategory, "return path failed — resuming engine from here");
                    Resume();
                }
                break;
        }
    }

    private void OnArrivedAtDestination()
    {
        if (_destinationIsStash)
        {
            // StashRoomManager fires `hide N <coin>` off RoomTracker's
            // arrival StateChanged — nothing to send here, just head back.
            _log?.Info(LogCategory, "arrived at stash room — StashRoomManager handles the hide");
        }
        else
        {
            DepositAtBank();
        }

        _phase = DepositPhase.WalkingBackToOrigin;
        if (!_walker.WalkTo(_origin))
        {
            _log?.Warn(LogCategory, $"can't return to {_origin} — resuming from here");
            Resume();
        }
    }

    /// <summary>
    /// Send a single <c>dep &lt;value&gt;</c> for the held wealth above
    /// the per-currency keep-on-hand floors. Reads the snapshot fresh at
    /// deposit time (holdings may have shifted during the walk), so the
    /// amount reflects what's actually on hand at the bank.
    /// </summary>
    private void DepositAtBank()
    {
        CashSettings cash = _readCash();
        CurrencyHoldings held = _getSnapshot().Currency;
        long keepValue = KeepValueInCopper(cash);
        long depositValue = held.TotalCopperValue - keepValue;
        if (depositValue <= 0)
        {
            _log?.Info(LogCategory,
                $"nothing to deposit (wealth={held.TotalCopperValue} <= keep={keepValue})");
            return;
        }
        _log?.Info(LogCategory, $"depositing {depositValue} (wealth={held.TotalCopperValue} keep={keepValue})");
        Send($"dep {depositValue}");
    }

    /// <summary>Total copper value of the per-currency keep-on-hand
    /// floors — the slice of wealth we leave on the character.</summary>
    private static long KeepValueInCopper(CashSettings c) =>
        c.KeepCopperOnHand
        + c.KeepSilverOnHand * 10
        + c.KeepGoldOnHand * 100
        + c.KeepPlatinumOnHand * 10000
        + c.KeepRunicOnHand * 1000000;

    private bool IsStashRoom(RoomKey room)
    {
        if (_profile.Current?.StashRooms is not { } stashes) return false;
        foreach (RoomRef r in stashes)
            if (r.Map == room.Map && r.Room == room.Room) return true;
        return false;
    }

    /// <summary>
    /// True when <paramref name="destination"/> is a guaranteed per-pass
    /// visit on the running engine's path. A <see cref="ResumeKind.Loop"/>
    /// expands its full BFS circuit (every circuit room is hit each lap);
    /// a <see cref="ResumeKind.Lair"/> only guarantees its <em>marked</em>
    /// rooms — the scheduler's transit rooms between lairs are dynamic and
    /// not reliably revisited, so they don't count as on-path. The
    /// asymmetry is deliberate: skipping a detour when the engine won't
    /// actually pass through would strand the coin forever, so we only skip
    /// on a certain visit.
    /// </summary>
    private bool IsDestinationOnEnginePath(RoomKey destination, ResumeTarget resume)
    {
        switch (resume.Kind)
        {
            case ResumeKind.Loop:
                RoomKey? source = _loopRunner.CircleStartRoom
                    ?? _tracker.State.CurrentRoom?.Key;
                if (source is not { } from) return false;
                foreach (RoomKey k in _loopRunner.ResolveLoopRoomKeys(from))
                    if (k.Equals(destination)) return true;
                return false;
            case ResumeKind.Lair:
                foreach (RoomKey k in _autoLair.Marked)
                    if (k.Equals(destination)) return true;
                return false;
            default:
                return false;
        }
    }

    // ----- engine snapshot / stop / resume ---------------------------

    private ResumeTarget SnapshotRunningEngine()
    {
        // Priority Lair -> Loop: Auto-Lair drives the walker, so the
        // topmost active engine is the real activity. A bare walk-to is
        // intentionally NOT a resume target (auto-deposit only reroutes
        // looping / auto-lairing, per the Settings → Cash contract).
        if (_autoLair.IsActive)
            return new ResumeTarget(ResumeKind.Lair, null);
        if (_loopRunner.State is not LoopState.Idle && _loopRunner.CurrentLoop is { } loop)
            return new ResumeTarget(ResumeKind.Loop, loop);
        return new ResumeTarget(ResumeKind.None, null);
    }

    private void StopRunningEngine(string reason)
    {
        if (_autoLair.IsActive) _autoLair.Stop(reason);
        if (_loopRunner.State is not LoopState.Idle) _loopRunner.Stop(reason);
    }

    private void Resume()
    {
        ResumeTarget r = _resume;
        GoIdle();
        switch (r.Kind)
        {
            case ResumeKind.Lair:
                _autoLair.Start();
                break;
            case ResumeKind.Loop:
                if (r.Loop is { } loop) _loopRunner.Start(loop);
                break;
        }
    }

    private void GoIdle()
    {
        _busy = false;
        _phase = DepositPhase.Idle;
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cash.AutoDepositRequested -= OnAutoDepositRequested;
        _walker.Event -= OnWalkEvent;
    }

    private enum DepositPhase
    {
        Idle,
        WalkingToDestination,
        WalkingBackToOrigin,
    }

    private enum ResumeKind
    {
        None,
        Loop,
        Lair,
    }

    private readonly record struct ResumeTarget(ResumeKind Kind, Loop? Loop);
}
