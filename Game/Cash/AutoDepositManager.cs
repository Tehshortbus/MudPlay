using System.Text;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Cash;

// Auto-deposit reroute. When CashManager.AutoDepositRequested fires (a wealth /
// coin-count gate crossed with a bank / stash location configured), this manager
// detours the running movement engine to the configured location, offloads the
// excess coin, walks back to where it left off, and restarts the captured
// engine.
//
// Loop / Auto-Lair only. A reroute only makes sense for an indefinitely-running
// engine the user wants to keep alive across the detour. A one-shot walk-to has
// a single fixed destination and no "resume" semantics, so an active
// AutoWalkManager (or an idle stack) is left alone — the gate fired but nothing
// reroutes.
//
// Stop-and-restart, not gate-pause — same reasoning as PartyComebackManager: a
// MovementCoordinator gate would block the detour walk itself. We snapshot the
// running engine, Stop() it, run the detour gate-clean, then re-Start it.
//
// Bank vs. stash. The configured BankRoomKey is classified against the
// character's StashRooms: a match is a stash room (on arrival, this manager calls
// StashRoomManager.ExecuteStash to fire the per-currency `hide` commands),
// otherwise it must validate as a real bank (Shops ShopType == 7 via
// BankCatalog) before this manager sends a single `dep <value>` for the held
// wealth above the keep-on-hand floors. A key that resolves to neither — a
// stale / orphaned BankRoomKey the Settings picker shows as unset — is a no-op:
// honouring it would detour to a phantom bank and, on a tolled route, probe the
// party's @wealth for a deposit that can never land.
//
// Pass-through vs. detour. A dedicated detour is only worth spending on a store
// the running engine won't reach on its own. When the configured stash
// destination already sits on the active route — a resolved loop circuit room
// (LoopRunner.ResolveLoopRoomKeys) or a marked Auto-Lair room
// (AutoLairManager.IsMarked) — the gate crossing is a no-op: this manager
// subscribes to RoomTracker.StateChanged and stashes in passing every time the
// character naturally walks through a marked stash room while a loop / lair runs.
// Banks always detour (a bank is never a route waypoint), and an off-route stash
// room still detours. A purely manual walk with no engine running never triggers
// a pass-through stash.
//
// Everything runs on the UI thread (AutoWalkManager.Event and
// CashManager.AutoDepositRequested both fire there), so no marshalling is needed.
// Single-flight: a second gate crossing while a reroute is in progress is ignored
// (the CashManager single-fire guard already suppresses re-fires until both gates
// fall back below threshold).
public sealed class AutoDepositManager : IDisposable
{
    private const string LogCategory = "AutoDeposit";

    private readonly CashManager _cash;
    private readonly Func<CashSettings> _readCash;
    private readonly Func<InventorySnapshot> _getSnapshot;
    private readonly Func<RoomKey, bool> _isBankRoom;
    private readonly ProfileService _profile;
    private readonly RoomTracker _tracker;
    private readonly AutoWalkManager _walker;
    private readonly LoopRunner _loopRunner;
    private readonly AutoLairManager _autoLair;
    private readonly StashRoomManager _stash;
    private readonly LogService? _log;

    private Action<byte[]>? _wireSender;
    private bool _disposed;
    private bool _busy;

    // Fires when a bank `dep` is dispatched on arrival, carrying the deposited
    // copper value. Lets the Session Stats tracker count bank-deposited wealth
    // alongside stash-room hides.
    public event Action<long>? Deposited;
    private DepositPhase _phase = DepositPhase.Idle;
    private ResumeTarget _resume;
    private RoomKey _destination;
    private RoomKey _origin;
    private bool _destinationIsStash;

    public AutoDepositManager(
        CashManager cash,
        Func<CashSettings> readCash,
        Func<InventorySnapshot> getSnapshot,
        Func<RoomKey, bool> isBankRoom,
        ProfileService profile,
        RoomTracker tracker,
        AutoWalkManager walker,
        LoopRunner loopRunner,
        AutoLairManager autoLair,
        StashRoomManager stash,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cash);
        ArgumentNullException.ThrowIfNull(readCash);
        ArgumentNullException.ThrowIfNull(getSnapshot);
        ArgumentNullException.ThrowIfNull(isBankRoom);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(autoLair);
        ArgumentNullException.ThrowIfNull(stash);
        _cash = cash;
        _readCash = readCash;
        _getSnapshot = getSnapshot;
        _isBankRoom = isBankRoom;
        _profile = profile;
        _tracker = tracker;
        _walker = walker;
        _loopRunner = loopRunner;
        _autoLair = autoLair;
        _stash = stash;
        _log = log;

        _cash.AutoDepositRequested += OnAutoDepositRequested;
        _walker.Event += OnWalkEvent;
        _tracker.StateChanged += OnRoomEntered;
    }

    // Bind the wire sender — typically the gate-wrapped engine pipeline from
    // MainWindowViewModel. The bank `dep` command travels this path; the stash
    // path uses StashRoomManager's own sender.
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

        // Destination-validity gate. A persisted BankRoomKey can go stale — the
        // active game-data set changed, or a room was un-marked as a stash —
        // leaving a key that resolves to neither a bank (Shops ShopType == 7)
        // nor a marked stash room. The Settings → Cash picker shows its
        // placeholder in that case (no valid selection), but the raw key
        // survives on disk. Honouring it would detour to a non-bank room and,
        // when the route crosses a toll, probe the party's @wealth for a
        // deposit that can never land. No valid destination → no-op.
        if (!destinationIsStash && !_isBankRoom(destination))
        {
            _log?.Info(LogCategory,
                $"BankRoomKey '{cash.BankRoomKey}' is neither a bank nor a marked stash "
                + "room in the active set — no deposit destination, ignoring");
            return;
        }

        // Pass-through: if the stash destination already sits on the active
        // route, don't spend a dedicated detour — OnRoomEntered stashes it
        // when the engine walks through on its own. Banks always detour;
        // off-route stash rooms still detour.
        if (destinationIsStash && IsOnActiveRoute(destination, resume, current.Key))
        {
            _log?.Info(LogCategory,
                $"stash room {destination} on the active {resume.Kind} route — "
                + "no detour, stashing on pass-through");
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

    // Pass-through stash trigger. Fires when the character walks into a marked
    // stash room while a loop / lair is running — stashes in passing, no detour.
    // Suppressed while a reroute is in flight (_busy, where the arrival handler
    // owns the stash) and when no resumable engine is active (a purely manual walk
    // never stashes).
    private void OnRoomEntered(RoomTransition t)
    {
        if (_busy) return;
        if (t.NewRoom is not { } room) return;
        if (t.PreviousRoom is { } prev && prev.Key.Equals(room.Key)) return;
        if (!IsStashRoom(room.Key)) return;
        if (SnapshotRunningEngine().Kind == ResumeKind.None) return;
        _log?.Info(LogCategory, $"passed through stash room {room.Key} during automation — stashing");
        _stash.ExecuteStash(room.Key);
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
            // Drive the per-currency `hide` explicitly — StashRoomManager
            // is invoked, not autonomous, so it only fires here on an
            // auto-deposit reroute (never on a manual walk-through).
            _log?.Info(LogCategory, "arrived at stash room — executing stash");
            _stash.ExecuteStash(_destination);
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

    // Send a single `dep <value>` for the held wealth above the per-currency
    // keep-on-hand floors. Reads the snapshot fresh at deposit time (holdings may
    // have shifted during the walk), so the amount reflects what's actually on
    // hand at the bank.
    private void DepositAtBank()
    {
        CashSettings cash = _readCash();
        CurrencyHoldings held = _getSnapshot().Currency;
        long keepValue = cash.KeepOnHandCopper();
        long depositValue = held.TotalCopperValue - keepValue;
        if (depositValue <= 0)
        {
            _log?.Info(LogCategory,
                $"nothing to deposit (wealth={held.TotalCopperValue} <= keep={keepValue})");
            return;
        }
        _log?.Info(LogCategory, $"depositing {depositValue} (wealth={held.TotalCopperValue} keep={keepValue})");
        Send($"dep {depositValue}");
        Deposited?.Invoke(depositValue);
    }

    private bool IsStashRoom(RoomKey room)
    {
        if (_profile.Current?.StashRooms is not { } stashes) return false;
        foreach (RoomRef r in stashes)
            if (r.Map == room.Map && r.Room == room.Room) return true;
        return false;
    }

    // Whether room is one the running engine will reach on its own — a resolved
    // loop-circuit room, or a marked Auto-Lair room. Such a room needs no detour:
    // the pass-through handler stashes it when the engine walks through.
    private bool IsOnActiveRoute(RoomKey room, ResumeTarget resume, RoomKey current)
    {
        switch (resume.Kind)
        {
            case ResumeKind.Lair:
                // The lair engine roams among its marked rooms, so a marked
                // stash room is guaranteed to be revisited.
                return _autoLair.IsMarked(room);
            case ResumeKind.Loop:
                // The loop re-walks its resolved circuit each lap; membership
                // means a guaranteed per-lap pass.
                foreach (RoomKey k in _loopRunner.ResolveLoopRoomKeys(current))
                    if (k.Equals(room)) return true;
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
        _tracker.StateChanged -= OnRoomEntered;
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
