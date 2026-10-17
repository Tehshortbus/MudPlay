using System.Text;
using System.Threading;
using MudPlay.Game.Inventory;
using MudPlay.Game.Light;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Cash;

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
// wealth above the keep-on-hand floor. A key that resolves to neither — a
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
// Light on the way home. The reroute is the sole controller of the walker for the
// whole errand — the reactive light-shop router is suppressed while IsRerouting, so
// it can't hijack the detour walk-tos (two controllers on one walker is what froze
// the client). To still cover a dark return leg, this manager runs the light
// provisioning itself as an extra phase: after offloading at the bank it scans the
// bank -> origin leg (AutoLightProvisioner.PlanRouteBuy), and if that leg runs dark
// with no carried light to cover it, detours through the light shop nearest the
// bank (AutoLightShopRouter.TrySelectShop) — bank -> shop -> buy -> origin — before
// resuming the loop. When the leg is covered but the pack holds less than Carry
// hours of light (AutoLightProvisioner.PlanStockUp), the same stop is made if a
// shop selling the light adds at most AutoLightProvisioner.StockUpMaxAddedSteps
// (AutoLightShopRouter.TrySelectNearbyShop). The whole light detour rides the
// AutoLight master toggle (both advisors return null when it's off), so no separate
// opt-in. A shop it can't reach or a buy that doesn't land in the buy window falls
// through to the plain return walk — a missing light never strands the reroute.
//
// Light restock trip. The same reroute, with the shop as its only stop
// (StartLightRestock): a loop's lit light dwindling under Reorder-at, or a pack
// short of light after a training trip that ended near a shop. Engine stopped,
// shop -> buy -> back to the loop's nearest room, engine resumed. A deposit that
// comes due meanwhile is re-armed to fire once the trip is over.
//
// Everything runs on the UI thread (AutoWalkManager.Event and
// CashManager.AutoDepositRequested both fire there), so no marshalling is needed —
// except the buy-window timer, whose callback is posted back onto the UI thread.
// Single-flight: a second gate crossing while a reroute is in progress is ignored
// (the CashManager single-fire guard already suppresses re-fires until both gates
// fall back below threshold).
public sealed class AutoDepositManager : IDisposable
{
    private const string LogCategory = "AutoDeposit";

    // On arrival at a bank the deposit amount is re-read from a fresh `i` rather
    // than the carried snapshot: a silent wealth change en route — a toll charge,
    // which no pattern observes, so the snapshot never decrements for it — would
    // otherwise deposit the stale pre-toll figure, and the game rejects a `dep`
    // for more coin than is on hand (no partial), leaving the deposit un-done
    // while the engine walks on. Requesting `i` and deferring the `dep` until the
    // fresh dump lands also serialises the deposit ahead of the return leg, so the
    // walk home can't queue moves between the `i` and the `dep`. Bounds the wait so
    // a lost `i` reply can't wedge the engine at the bank.
    private static readonly TimeSpan DepositSyncTimeout = TimeSpan.FromSeconds(4);

    private readonly CashManager _cash;
    private readonly Func<CashSettings> _readCash;
    private readonly Func<InventorySnapshot> _getSnapshot;
    // Applies the deposited copper to the on-hand tracker the instant `dep` is
    // sent, so the return-leg router sees post-deposit wealth without waiting on
    // the server echo (InventoryManager.NoteAutoDeposit).
    private readonly Action<long> _noteAutoDeposit;
    private readonly Func<RoomKey, bool> _isBankRoom;
    private readonly ProfileService _profile;
    private readonly RoomTracker _tracker;
    private readonly AutoWalkManager _walker;
    private readonly LoopRunner _loopRunner;
    private readonly AutoLairManager _autoLair;
    private readonly StashRoomManager _stash;
    private readonly AutoLightProvisioner _provisioner;
    private readonly AutoLightShopRouter _lightShop;
    private readonly Func<int, int> _carriedCount;
    private readonly Action<Action> _post;
    // True while we're a party follower (in a party, not leading). Gates the
    // opt-in follower pass-through stash. Unbound (tests) ⇒ never a follower.
    private readonly Func<bool> _isFollower;
    // The light buy at a shop the reroute stops at.
    private readonly LightShopBuy _lightBuyer;
    // A loop's rooms by its waypoints (unbound: none known).
    private readonly Func<Loop, IReadOnlyList<RoomKey>> _loopRoomsOf;
    // Bounds the wait for the fresh `i` on bank arrival (see DepositSyncTimeout).
    // Fires on a threadpool thread, so its callback is posted back onto the UI
    // thread — same marshalling as the buy timer.
    private readonly Timer _depositSyncTimer;
    private readonly LogService? _log;

    private Action<byte[]>? _wireSender;
    private bool _disposed;
    private bool _busy;

    // Set while the reroute is itself driving the walker (the initial engine stop
    // + each WalkTo). WalkTo synchronously raises a Stopped event when it
    // supersedes an in-flight walk, and stopping an Approaching loop halts the
    // walker the same way — that churn is ours, not a user abort. OnWalkEvent
    // treats a Stopped seen while this is clear as a genuine external halt
    // (toolbar / nav / death / remote) and tears the reroute down.
    private bool _drivingWalker;

    // The light buy the return leg needs, remembered across the shop detour
    // (WalkingToLightShop -> BuyingLight). Null outside a light detour.
    private AutoLightBuyRequest? _lightBuy;

    private DepositPhase _phase = DepositPhase.Idle;

    // For a bug report: the bank trip in progress, or null.
    public string? DescribePending() =>
        _phase == DepositPhase.Idle && !_busy ? null : $"phase {_phase}" + (_busy ? ", busy" : string.Empty);
    private DetourResume _resume;
    private RoomKey _destination;
    private RoomKey _origin;
    private readonly Func<RoomKey, Loop, RoomKey?> _nearestLoopRoom;
    private bool _destinationIsStash;
    // The reroute is an event's trip (StartEventTrip): it ends at the bank / stash.
    private bool _eventTrip;
    // The reroute is a light restock trip (StartLightRestock): no bank, a shop only.
    private bool _lightTrip;
    // The rooms of the loop the reroute will resume: what the light stock-up on the
    // way back asks about the darkness ahead.
    private IReadOnlyList<RoomKey> _loopRooms = Array.Empty<RoomKey>();

    public AutoDepositManager(
        CashManager cash,
        Func<CashSettings> readCash,
        Func<InventorySnapshot> getSnapshot,
        Action<long> noteAutoDeposit,
        Func<RoomKey, bool> isBankRoom,
        ProfileService profile,
        RoomTracker tracker,
        AutoWalkManager walker,
        LoopRunner loopRunner,
        AutoLairManager autoLair,
        StashRoomManager stash,
        AutoLightProvisioner provisioner,
        AutoLightShopRouter lightShop,
        Func<int, int> carriedCount,
        Action<Action> post,
        LogService? log = null,
        TimeSpan? buyTimeout = null,
        Func<bool>? isFollower = null,
        Func<RoomKey, Loop, RoomKey?>? nearestLoopRoom = null,
        Func<Loop, IReadOnlyList<RoomKey>>? loopRooms = null)
    {
        ArgumentNullException.ThrowIfNull(cash);
        ArgumentNullException.ThrowIfNull(readCash);
        ArgumentNullException.ThrowIfNull(getSnapshot);
        ArgumentNullException.ThrowIfNull(noteAutoDeposit);
        ArgumentNullException.ThrowIfNull(isBankRoom);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(autoLair);
        ArgumentNullException.ThrowIfNull(stash);
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(lightShop);
        ArgumentNullException.ThrowIfNull(carriedCount);
        ArgumentNullException.ThrowIfNull(post);
        _cash = cash;
        _readCash = readCash;
        _getSnapshot = getSnapshot;
        _noteAutoDeposit = noteAutoDeposit;
        _isBankRoom = isBankRoom;
        _profile = profile;
        _tracker = tracker;
        _walker = walker;
        _loopRunner = loopRunner;
        _autoLair = autoLair;
        _stash = stash;
        _provisioner = provisioner;
        _lightShop = lightShop;
        _carriedCount = carriedCount;
        _post = post;
        _isFollower = isFollower ?? (static () => false);
        _nearestLoopRoom = nearestLoopRoom ?? (static (_, _) => null);
        _lightBuyer = new LightShopBuy(carriedCount, LightShopBuy.PostedTimer(post), buyTimeout, log, LogCategory);
        _loopRoomsOf = loopRooms ?? (static _ => Array.Empty<RoomKey>());
        _depositSyncTimer = new Timer(_ => _post(OnDepositSyncTimeout), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _log = log;

        _cash.AutoDepositRequested += OnAutoDepositRequested;
        _walker.Event += OnWalkEvent;
        // The pass-through stash is NOT subscribed to RoomTracker.StateChanged
        // here: it must run BEFORE the loop's next-move step so the `hide` reaches
        // the wire ahead of the move (otherwise the coins get hidden in the next
        // room — report paradigm-20260819-054200). AppServices drives OnRoomEntered
        // from its early StateChanged handler, registered ahead of LoopRunner's.
    }

    // True while a reroute owns the walker (bank/stash detour and the walk back).
    // The reactive shop/drop routers gate their own detours on this: a reroute
    // stops the loop (LoopState.Idle) and drives its own WalkTo legs, so those
    // routers' engineWalkActive check would otherwise see plain walk-tos and
    // hijack the detour — two controllers driving one walker, which spins into a
    // re-announce/BFS loop that locks the UI. Suppressing them here keeps the
    // reroute single-controller.
    public bool IsRerouting => _busy;

    // Raised when a reroute or event trip starts, turns for home (IsReturning), or
    // ends (IsRerouting flips). May fire off the UI thread, from the buy /
    // deposit-sync timers.
    public event Action? ReroutingChanged;

    // The deposit / stash is done and the reroute is on its way back (a light-shop
    // stop on the way home included). A light restock trip is returning once bought.
    public bool IsReturning => _busy && (_lightTrip
        ? _phase == DepositPhase.WalkingBackToOrigin
        : _phase is DepositPhase.WalkingToLightShop or DepositPhase.BuyingLight or DepositPhase.WalkingBackToOrigin);

    // The reroute is a light restock trip rather than a bank run.
    public bool IsLightTrip => _busy && _lightTrip;

    // The engine this detour will pick back up (meaningful while it runs).
    public DetourResume ResumePlan => _resume;

    // Offered a bank run as it comes due, with the engine to resume and the bank:
    // true when a sell detour went first (SellDetourManager.SellAheadOfBankRun) and
    // will hand the run back from the shop through TakeOverFromDetour.
    public Func<DetourResume, RoomKey, bool>? SellFirst { get; set; }

    // One-line reroute status for the bug report — the current phase, plus the
    // light being bought while a return-leg light detour is in flight. "idle" when
    // no reroute is running. Diagnoses a walker stuck mid-errand (the original
    // freeze had no capture of which reroute phase owned the walker).
    public string RerouteStatus =>
        _phase == DepositPhase.Idle
            ? "idle"
            : _lightBuy is { } buy
                ? $"{_phase} ({(_lightTrip ? "light restock trip" : "light")}: {buy.LightName} x{buy.Count})"
                : _phase.ToString();

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
        if (_busy)
        {
            // A light restock trip has the walker. Re-arm the deposit's single-fire
            // latch (with CashManager's retry hold) so a later inventory change asks
            // again; one that comes while the trip still runs lands here and is
            // re-armed again, so the deposit goes out after the trip rather than
            // waiting for wealth to fall under the threshold and climb back.
            if (_lightTrip) _cash.NotifyAutoDepositAborted();
            return;
        }

        // Snapshot BEFORE stopping anything — Stop() clears the engine's
        // run-state, so the resume target must be captured first. Only a
        // Loop or Auto-Lair qualifies; a one-shot walk-to (or an idle
        // stack) has nothing to detour-and-resume.
        DetourResume resume = SnapshotRunningEngine();
        if (resume.Kind == DetourResumeKind.None)
        {
            _log?.Debug(LogCategory, "gate fired but no loop / auto-lair running — ignoring");
            _cash.NotifyAutoDepositAborted();
            return;
        }

        if (!TryResolveDestination(out RoomKey destination, out bool destinationIsStash))
        {
            _cash.NotifyAutoDepositAborted();
            return;
        }

        // Capture where we left off so we can walk back and restart the
        // engine at the same spot. No known room → nowhere to return to,
        // so don't strand the engine mid-circuit.
        if (_tracker.State.CurrentRoom is not { } current)
        {
            _log?.Warn(LogCategory, "gate fired but current room is unknown — can't reroute");
            _cash.NotifyAutoDepositAborted();
            return;
        }

        // Pass-through: if the stash destination already sits on the active
        // route, don't spend a dedicated detour — OnRoomEntered stashes it
        // when the engine walks through on its own. Banks always detour;
        // off-route stash rooms still detour.
        if (destinationIsStash && IsOnActiveRoute(destination, resume))
        {
            _log?.Info(LogCategory,
                $"stash room {destination} on the active {resume.Kind} route — "
                + "no detour, stashing on pass-through");
            return;
        }

        // Something to sell and a shop for it near the bank: the sale goes first and
        // hands the run back from the shop, so one trip does both and the sale's coin
        // is banked with the rest. The single-fire guard stays latched meanwhile; the
        // detour re-arms it if it never reaches the hand-over.
        if (!destinationIsStash && SellFirst?.Invoke(resume, destination) == true)
        {
            _log?.Info(LogCategory, $"auto-deposit wealth={wealthValue} dest={destination} follows a sale near the bank");
            return;
        }

        _busy = true;
        ReroutingChanged?.Invoke();
        _resume = resume;
        _destination = destination;
        _origin = current.Key;
        _destinationIsStash = destinationIsStash;
        _log?.Info(LogCategory,
            $"auto-deposit reroute wealth={wealthValue} dest={destination} " +
            $"kind={(_destinationIsStash ? "stash" : "bank")} resume={resume.Kind} origin={_origin}");

        // Stop the running engine so the detour walk owns the wire and
        // runs without a competing command stream or asserted gate. Guarded:
        // halting an Approaching loop stops the walker, whose Stopped event must
        // not read as a user abort of the reroute we're mid-launch of.
        _loopRooms = LoopRoomsOf(resume);
        _drivingWalker = true;
        try { _resume.Stop(_walker, _loopRunner, _autoLair, "auto-deposit reroute"); }
        finally { _drivingWalker = false; }

        _phase = DepositPhase.WalkingToDestination;
        if (!RerouteWalkTo(destination))
        {
            _log?.Warn(LogCategory, _walker.AvoidBlockingRouteTo(destination) is { } blocked
                ? $"can't reach {destination} — route blocked by your avoid in room {blocked.Map}/{blocked.Room}; skipping deposit and resuming"
                : $"can't reach {destination} — resuming");
            _cash.NotifyAutoDepositAborted();
            Resume();
        }
    }

    // A detour that stopped the loop (the sell detour) is done and a deposit is due:
    // go to the bank from here rather than walk back first, then return to the loop
    // as any bank run does (user, 2026-09-30). False — the caller walks back itself —
    // when no deposit is due, nothing can be resumed, or there's no destination.
    public bool TakeOverFromDetour(DetourResume resume, RoomKey origin)
    {
        if (_busy || resume.Kind is not (DetourResumeKind.Loop or DetourResumeKind.Lair)) return false;
        if (!_cash.IsAutoDepositDue()) return false;
        if (!TryResolveDestination(out RoomKey destination, out bool destinationIsStash)) return false;

        _cash.NoteAutoDepositStarted();
        _busy = true;
        ReroutingChanged?.Invoke();
        _resume = resume;
        _loopRooms = LoopRoomsOf(resume);
        _destination = destination;
        _origin = origin;
        _destinationIsStash = destinationIsStash;
        _log?.Info(LogCategory,
            $"auto-deposit taking over from a detour: {(destinationIsStash ? "stash" : "bank")} {destination} "
            + $"before returning (resume={resume.Kind} origin={origin})");
        _phase = DepositPhase.WalkingToDestination;
        if (!RerouteWalkTo(destination))
        {
            _log?.Warn(LogCategory, $"can't reach {destination} — resuming");
            _cash.NotifyAutoDepositAborted();
            Resume();
        }
        return true;
    }

    // The Settings → Cash bank / stash room, when it's a real destination.
    // Destination-validity gate: a persisted BankRoomKey can go stale — the active
    // game-data set changed, or a room was un-marked as a stash — leaving a key that
    // resolves to neither a bank (Shops ShopType == 7) nor a marked stash room. The
    // Settings → Cash picker shows its placeholder in that case (no valid
    // selection), but the raw key survives on disk. Honouring it would detour to a
    // non-bank room and, when the route crosses a toll, probe the party's @wealth
    // for a deposit that can never land.
    private bool TryResolveDestination(out RoomKey destination, out bool isStash)
    {
        CashSettings cash = _readCash();
        isStash = false;
        if (!RoomKey.TryParseWire(cash.BankRoomKey, out destination))
        {
            _log?.Warn(LogCategory, $"BankRoomKey '{cash.BankRoomKey}' is unparseable — no deposit destination");
            return false;
        }
        isStash = IsStashRoom(destination);
        if (isStash || _isBankRoom(destination)) return true;
        _log?.Info(LogCategory,
            $"BankRoomKey '{cash.BankRoomKey}' is neither a bank nor a marked stash "
            + "room in the active set — no deposit destination");
        return false;
    }

    // ----- Event trip ------------------------------------------------

    // An event's bank / stash trip: walk to the Settings → Cash bank or stash room,
    // deposit or stash there, and stop — the event's Then step decides what comes
    // next, so there's no walk back and nothing to resume. The caller has already
    // stopped any running engine. False when there's no destination, no known room,
    // no route, or a reroute is already running; otherwise EventTripEnded reports
    // how it ended.
    public bool StartEventTrip()
    {
        if (_busy) return false;
        if (!TryResolveDestination(out RoomKey destination, out bool isStash)) return false;
        if (_tracker.State.CurrentRoom is not { } current)
        {
            _log?.Warn(LogCategory, "event trip: current room is unknown — can't go");
            return false;
        }
        _busy = true;
        ReroutingChanged?.Invoke();
        _eventTrip = true;
        _resume = default;
        _destination = destination;
        _origin = current.Key;
        _destinationIsStash = isStash;
        _phase = DepositPhase.WalkingToDestination;
        _log?.Info(LogCategory, $"event trip to {(isStash ? "stash" : "bank")} {destination}");
        if (RerouteWalkTo(destination)) return true;
        _log?.Warn(LogCategory, $"event trip: can't reach {destination}");
        GoIdle();
        return false;
    }

    // ----- Light restock trip ---------------------------------------

    // Stop the running loop / Auto-Lair, walk to a shop selling buy's light, buy up
    // to buy.Count and walk back to the loop's nearest room (or where it was left),
    // then resume the engine. With maxAddedSteps the trip is made only when the shop
    // adds at most that many steps to the walk back (a stock-up in passing); without,
    // the nearest shop there and back is taken (a reorder the light can't do
    // without).
    public LightRestockOutcome StartLightRestock(AutoLightBuyRequest buy, int? maxAddedSteps, string why)
    {
        if (_busy) return LightRestockOutcome.Busy;
        if (_carriedCount(buy.ItemId) >= buy.Count)
        {
            _log?.Info(LogCategory, $"light restock ({why}): already carrying {buy.Count} '{buy.LightName}' — no trip");
            return LightRestockOutcome.NotPossible;
        }
        DetourResume resume = SnapshotRunningEngine();
        if (resume.Kind == DetourResumeKind.None || _tracker.State.CurrentRoom is not { } current)
            return LightRestockOutcome.NotPossible;

        _resume = resume;
        _origin = current.Key;
        RoomKey back = ReturnRoom(current.Key);
        RoomKey shop;
        bool found = maxAddedSteps is { } max
            ? _lightShop.TrySelectNearbyShop(current.Key, back, buy.ItemId, max, out shop)
            : _lightShop.TrySelectShop(current.Key, back, buy.ItemId, out shop);
        if (!found)
        {
            _resume = default;
            _log?.Info(LogCategory, maxAddedSteps is { } m
                ? $"light restock ({why}): no shop selling '{buy.LightName}' within {m} added steps"
                : $"light restock ({why}): no reachable shop sells '{buy.LightName}'");
            return LightRestockOutcome.NotPossible;
        }

        _busy = true;
        _lightTrip = true;
        _lightBuy = buy;
        _loopRooms = LoopRoomsOf(resume);
        _destination = shop;
        _destinationIsStash = false;
        ReroutingChanged?.Invoke();
        _log?.Info(LogCategory,
            $"light restock trip ({why}): '{buy.LightName}' up to x{buy.Count} at {shop}, resume={resume.Kind} origin={_origin}");

        _drivingWalker = true;
        try { _resume.Stop(_walker, _loopRunner, _autoLair, "light restock trip"); }
        finally { _drivingWalker = false; }

        _phase = DepositPhase.WalkingToLightShop;
        if (RerouteWalkTo(shop)) return LightRestockOutcome.Started;
        _log?.Warn(LogCategory, $"can't reach light shop {shop} — resuming");
        Resume();
        return LightRestockOutcome.NotPossible;
    }

    // How an event trip ended: deposited / stashed, gave up (no route, a leg
    // failed), or stopped from outside (a user or remote halt, Reset States).
    public event Action<EventTripOutcome>? EventTripEnded;

    private void EndEventTrip(EventTripOutcome outcome)
    {
        GoIdle();
        EventTripEnded?.Invoke(outcome);
    }

    // Every reroute-initiated WalkTo goes through here so the Stopped event that
    // WalkTo raises when it supersedes an in-flight walk is attributed to the
    // reroute, not to a user halt (see _drivingWalker).
    private bool RerouteWalkTo(RoomKey destination)
    {
        _drivingWalker = true;
        // planThroughAcquirableGates: walking OUT of a gated area (hidden exits,
        // key-doors, summon-drop keys, multi-action gates) is trivial, but walking
        // back IN needs the same acquirable-gate planning a user GOTO uses. Without
        // it the return leg plans over the plain graph, which has no route back into
        // e.g. the dark-elf city, so the walker can't get home and the engine strands
        // at the bank, never resuming the loop (report paradigm-20260913-022254).
        // armItemAcquisition stays on (WalkTo's default) so the crossing actions fire.
        try { return _walker.WalkTo(destination, planThroughAcquirableGates: true); }
        finally { _drivingWalker = false; }
    }

    // Pass-through stash trigger. Fires when the character walks into a marked
    // stash room while a loop / lair is running — stashes in passing, no detour.
    // Suppressed while a reroute is in flight (_busy, where the arrival handler
    // owns the stash) and when no resumable engine is active (a purely manual walk
    // never stashes).
    // Driven by AppServices' early RoomTracker.StateChanged handler (registered
    // ahead of LoopRunner) so the stash's `hide` precedes the loop's next move.
    internal void OnRoomEntered(RoomTransition t)
    {
        if (_busy) return;
        if (t.NewRoom is not { } room) return;
        if (t.PreviousRoom is { } prev && prev.Key.Equals(room.Key)) return;
        if (!IsStashRoom(room.Key)) return;
        if (SnapshotRunningEngine().Kind == DetourResumeKind.None)
        {
            // No local engine drives us. A party follower dragged through their own
            // stash room by the leader still stashes when opted in — their loop /
            // auto-lair is held by the leader-drag gate, so the running-engine check
            // never passes for them.
            if (!(_readCash().StashAsFollower && _isFollower())) return;
            _log?.Info(LogCategory, $"passed through stash room {room.Key} as a follower — stashing");
        }
        else
        {
            _log?.Info(LogCategory, $"passed through stash room {room.Key} during automation — stashing");
        }
        _stash.ExecuteStash(room.Key);
    }

    private void OnWalkEvent(WalkEvent e)
    {
        if (!_busy) return;

        // The user manually stopped movement mid-reroute (toolbar / nav Stop),
        // or a death / remote halt did — all of which stop the walker, raising
        // Stopped. Tear the reroute down here: otherwise _busy stays set and a
        // later walker Finished (from whatever the user starts next) fires
        // Resume(), resurrecting the pre-detour loop and superseding the new run
        // (the reported "built a new loop, it walked back to the old one").
        // Re-arm the deposit guard too, so a stop mid-reroute can't leave the
        // single-fire latch wedged for the rest of the session. _drivingWalker
        // filters out the synchronous Stopped the reroute's own WalkTo /
        // engine-stop churn raises.
        if (e.Kind == WalkEventKind.Stopped)
        {
            if (_drivingWalker) return;
            _log?.Info(LogCategory, $"reroute aborted — movement stopped externally ({e.Detail})");
            bool eventTrip = _eventTrip;
            bool lightTrip = _lightTrip;
            GoIdle();
            if (!lightTrip) _cash.NotifyAutoDepositAborted();
            if (eventTrip) EventTripEnded?.Invoke(EventTripOutcome.Stopped);
            return;
        }

        switch (_phase)
        {
            case DepositPhase.WalkingToDestination:
                if (e.Kind == WalkEventKind.Finished) OnArrivedAtDestination();
                else if (e.Kind == WalkEventKind.Failed)
                {
                    _log?.Warn(LogCategory, "detour path failed — resuming");
                    _cash.NotifyAutoDepositAborted();
                    Resume();
                }
                break;
            case DepositPhase.WalkingToLightShop:
                if (e.Kind == WalkEventKind.Finished) BeginBuyingLight();
                else if (e.Kind == WalkEventKind.Failed)
                {
                    // Shop unreachable — the light stays unbought; don't strand
                    // the reroute, just head home (dark leg pushed through
                    // reactively, same as the trip out).
                    _log?.Warn(LogCategory, "light-shop path failed — returning to origin without light");
                    WalkBackToOrigin();
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
            BeginReturnLeg();
            return;
        }

        // Bank: re-read holdings with a fresh `i` before computing the deposit
        // (see the DepositSyncTimeout note) and defer the `dep` + return leg until
        // the dump lands (OnInventoryChanged) or the timeout elapses.
        _log?.Info(LogCategory, "arrived at bank — requesting fresh inventory before deposit");
        _phase = DepositPhase.AwaitingInventoryForDeposit;
        Send("i");
        _depositSyncTimer.Change(DepositSyncTimeout, Timeout.InfiniteTimeSpan);
    }

    // Fresh inventory landed while a bank deposit was waiting on the pre-deposit
    // re-read — commit the deposit against the now-current holdings, then start the
    // return leg.
    private void CompleteBankDeposit()
    {
        // Re-entrancy guard. DepositAtBank's _noteAutoDeposit drives
        // InventoryManager.Changed SYNCHRONOUSLY, which re-enters OnInventoryChanged
        // mid-complete — were the phase still AwaitingInventoryForDeposit that re-entry
        // would run the whole deposit + return leg a second time (report
        // paradigm-20260913-022254: a "nothing to deposit" second pass and a doubled
        // return walk). Leave the awaiting phase BEFORE depositing so the re-entrant
        // call early-returns; BeginReturnLeg then sets the real leg (it only diverges
        // to the light-shop leg when the trip home runs dark).
        if (_phase != DepositPhase.AwaitingInventoryForDeposit) return;
        _depositSyncTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _phase = DepositPhase.WalkingBackToOrigin;
        DepositAtBank();
        BeginReturnLeg();
    }

    // The fresh `i` reply never arrived within DepositSyncTimeout — deposit from
    // the last-known snapshot (no worse than depositing without the re-read)
    // rather than wedge the engine at the bank. Posted onto the UI thread from the
    // timer.
    private void OnDepositSyncTimeout()
    {
        if (_phase != DepositPhase.AwaitingInventoryForDeposit) return;
        _log?.Warn(LogCategory, "no fresh inventory within timeout — depositing from last-known holdings");
        CompleteBankDeposit();
    }

    // Decide the trip home. If the bank -> origin leg runs dark and no carried light
    // covers it, detour through the nearest light shop (bank -> shop -> buy ->
    // origin) before resuming; else stock up at a light shop close to the way back
    // when the pack is short; otherwise walk straight back. The light detour rides
    // the AutoLight master toggle (both advisors return null when it's off).
    private void BeginReturnLeg()
    {
        if (_eventTrip)
        {
            _log?.Info(LogCategory, "event trip done");
            EndEventTrip(EventTripOutcome.Done);
            return;
        }
        if (TryPlanLightDetour(out RoomKey shop, out AutoLightBuyRequest buy))
        {
            _lightBuy = buy;
            _phase = DepositPhase.WalkingToLightShop;
            ReroutingChanged?.Invoke();
            _log?.Info(LogCategory,
                $"detouring to light shop {shop} on the way back for '{buy.LightName}' x{buy.Count}");
            if (!RerouteWalkTo(shop))
            {
                _log?.Warn(LogCategory, $"can't reach light shop {shop} — returning to origin without light");
                WalkBackToOrigin();
            }
            return;
        }

        WalkBackToOrigin();
    }

    // Whether the return leg needs a light we must shop for, and where to buy it.
    // Scans the bank -> origin leg we're about to walk: no route (origin
    // unreachable from here) leaves it to the plain return walk to surface;
    // PlanRouteBuy null means lit / covered / AutoLight off; a Buy verdict with no
    // reachable stocking shop returns false (head home, let the reactive need
    // surface). Only a full Buy + reachable shop arms the detour.
    private bool TryPlanLightDetour(out RoomKey shop, out AutoLightBuyRequest buy)
    {
        shop = default;
        buy = default;
        RoomKey back = ReturnRoom(_destination);
        if (_walker.TryComputeRouteKeys(_destination, back) is not { } route) return false;
        if (_provisioner.PlanRouteBuy(route) is not { } req)
            return TryPlanLightStockUp(back, route, out shop, out buy);
        // The light is bought in town before the walk back sets out (user,
        // 2026-10-17): the shop nearest the bank, there and back, not the one adding
        // the fewest steps, which can lie along the dark way home or at its end.
        if (!_lightShop.TrySelectShop(_destination, _destination, req.ItemId, out shop))
        {
            _log?.Info(LogCategory,
                $"return leg dark but no reachable shop stocks '{req.LightName}' — returning without light");
            return false;
        }
        buy = req;
        return true;
    }

    // The leg home is covered, but the pack is short of Carry hours of light: stop
    // at a light shop when one lies within the stock-up allowance of the way back.
    // The loop the trip returns to is what decides whether light is wanted at all.
    private bool TryPlanLightStockUp(RoomKey back, IReadOnlyList<RoomKey> legHome, out RoomKey shop, out AutoLightBuyRequest buy)
    {
        shop = default;
        buy = default;
        IReadOnlyList<RoomKey> ahead = legHome.Concat(_loopRooms).ToList();
        if (_provisioner.PlanStockUp(ahead) is not { } req) return false;
        if (!_lightShop.TrySelectNearbyShop(_destination, back, req.ItemId, AutoLightProvisioner.StockUpMaxAddedSteps, out shop))
        {
            _log?.Info(LogCategory,
                $"no shop selling '{req.LightName}' within {AutoLightProvisioner.StockUpMaxAddedSteps} added steps of the way back — no stock-up");
            return false;
        }
        _log?.Info(LogCategory, $"stocking up on '{req.LightName}' at {shop} on the way back");
        buy = req;
        return true;
    }

    // At the light shop: buy the whole carry batch (LightShopBuy). The walk home
    // starts once a fresh copy lands or the buy window passes, so the run of buys
    // isn't cut short.
    private void BeginBuyingLight()
    {
        if (_lightBuy is not { } buy)
        {
            WalkBackToOrigin();
            return;
        }
        _phase = DepositPhase.BuyingLight;
        _lightBuyer.Begin(buy, Send, bought =>
        {
            if (_phase != DepositPhase.BuyingLight) return;
            _log?.Info(LogCategory, bought
                ? $"'{buy.LightName}' acquired — returning to origin"
                : "light buy did not complete in time — returning to origin");
            WalkBackToOrigin();
        });
    }

    // Inventory-change poll (wired to InventoryManager.Changed). Drives two
    // deferred phases: the pre-deposit re-read at the bank (any change commits the
    // deposit against fresh holdings), and the light buy (the first fresh copy
    // resumes the walk home — one copy covers the dark leg even while the rest of
    // the batch is still buying). A no-op outside those phases.
    public void OnInventoryChanged()
    {
        if (_phase == DepositPhase.AwaitingInventoryForDeposit)
        {
            CompleteBankDeposit();
            return;
        }
        if (_phase == DepositPhase.BuyingLight) _lightBuyer.OnInventoryChanged();
    }

    private void WalkBackToOrigin()
    {
        _lightBuyer.Cancel();
        _lightBuy = null;
        RoomKey? here = _tracker.State.CurrentRoom?.Key;
        RoomKey back = here is { } from ? ReturnRoom(from) : _origin;
        if (here?.Equals(back) == true)
        {
            Resume();
            return;
        }
        _phase = DepositPhase.WalkingBackToOrigin;
        ReroutingChanged?.Invoke();
        if (!back.Equals(_origin))
            _log?.Info(LogCategory, $"walking back to {back}, the loop's nearest room (left it at {_origin})");
        if (!RerouteWalkTo(back))
        {
            _log?.Warn(LogCategory, $"can't return to {back} — resuming from here");
            Resume();
        }
    }

    // Where the trip heads home to from `from`: a loop picks up at whichever of its
    // rooms is nearest, as a sell detour does (user, 2026-09-30); anything else goes
    // back to where the trip began.
    private IReadOnlyList<RoomKey> LoopRoomsOf(DetourResume resume) =>
        resume.Loop is { } loop ? _loopRoomsOf(loop) : Array.Empty<RoomKey>();

    private RoomKey ReturnRoom(RoomKey from) =>
        _resume.Kind == DetourResumeKind.Loop && _resume.Loop is { } loop
            ? _nearestLoopRoom(from, loop) ?? _origin
            : _origin;

    // Send a single `dep <value>` for the held wealth above the raw keep-on-hand
    // floor. Reads the snapshot fresh at deposit time (holdings may have shifted
    // during the walk), so the amount reflects what's actually on hand at the
    // bank.
    private void DepositAtBank()
    {
        CashSettings cash = _readCash();
        CurrencyHoldings held = _getSnapshot().Currency;
        // Keep-on-hand is an amount of a chosen denomination (e.g. 1 runic) —
        // convert to copper against the ratio ladder before comparing to wealth.
        long keepValue = cash.KeepOnHandWealth * CurrencyHoldings.CopperUnit(cash.KeepOnHandDenomination);
        long depositValue = held.TotalCopperValue - keepValue;
        if (depositValue <= 0)
        {
            _log?.Info(LogCategory,
                $"nothing to deposit (wealth={held.TotalCopperValue} <= keep={keepValue})");
            return;
        }
        _log?.Info(LogCategory, $"depositing {depositValue} (wealth={held.TotalCopperValue} keep={keepValue})");
        Send($"dep {depositValue}");
        // Decrement the on-hand tracker now — before BeginReturnLeg plans the trip
        // home — so a toll on the return route is weighed against post-deposit
        // wealth. Waiting for the server's "You deposit …" echo would let the
        // route commit through a toll we just banked past (bug: return leg
        // routed on the stale pre-deposit purse and stranded at the toll).
        _noteAutoDeposit(depositValue);
    }

    private bool IsStashRoom(RoomKey room)
    {
        if (_profile.Current?.StashRooms is not { } stashes) return false;
        foreach (RoomRef r in stashes)
            if (r.Map == room.Map && r.Room == room.Room) return true;
        return false;
    }

    // True when the character is standing in a marked stash room with a loop /
    // lair running — the pass-through-stash context. AppServices wires this into
    // the cash + item auto-collect engines so a search here can't re-expose and
    // re-grab the pile the stash just hid (report paradigm-20260819-121516).
    public bool IsPassingThroughStashRoom()
        => _tracker.State.CurrentRoom is { } room
           && IsStashRoom(room.Key)
           && SnapshotRunningEngine().Kind != DetourResumeKind.None;

    // Whether room is one the running engine will reach on its own — a resolved
    // loop-circuit room, or a marked Auto-Lair room. Such a room needs no detour:
    // the pass-through handler stashes it when the engine walks through.
    private bool IsOnActiveRoute(RoomKey room, DetourResume resume)
    {
        switch (resume.Kind)
        {
            case DetourResumeKind.Lair:
                // The lair engine roams among its marked rooms, so a marked
                // stash room is guaranteed to be revisited.
                return _autoLair.IsMarked(room);
            case DetourResumeKind.Loop:
                // The loop re-walks its resolved circuit each lap; membership
                // means a guaranteed per-lap pass.
                foreach (RoomKey k in _loopRunner.ResolveLoopRoomKeys())
                    if (k.Equals(room)) return true;
                return false;
            default:
                return false;
        }
    }

    // ----- engine snapshot / stop / resume ---------------------------

    // A bare walk-to is intentionally NOT a resume target: auto-deposit only
    // reroutes looping / auto-lairing, per the Settings → Cash contract.
    private DetourResume SnapshotRunningEngine() =>
        DetourResume.Snapshot(_walker, _loopRunner, _autoLair, includeWalk: false);

    private void Resume()
    {
        // Re-arm the single-fire deposit guard on every reroute that completes and
        // returns through here — not just the explicit abort paths. A reroute can
        // reach the bank, attempt the deposit and STILL leave wealth above the
        // threshold (a partial deposit, a KeepOnHandWealth floor, or a deposit that
        // didn't land), then walk back and Resume() with no abort signal. Without
        // re-arming, the guard clears only when wealth falls below threshold —
        // which never happens — so auto-deposit wedges for the session and the
        // character loops forever, full and never depositing (report
        // paradigm-20260730-125716). A genuine success (wealth now below threshold)
        // re-arms naturally on the next inventory line, which also clears the
        // retry cooldown this sets, so the extra call is harmless there. A light
        // restock trip deposited nothing and holds no deposit latch.
        if (!_lightTrip) _cash.NotifyAutoDepositAborted();

        if (_eventTrip)
        {
            EndEventTrip(EventTripOutcome.Failed);
            return;
        }
        DetourResume r = _resume;
        GoIdle();
        r.Resume(_walker, _loopRunner, _autoLair);
    }

    // Reset States: stand down without resuming the walk it was part of.
    public void Cancel()
    {
        if (!_busy) return;
        if (_eventTrip) EndEventTrip(EventTripOutcome.Stopped);
        else GoIdle();
    }

    private void GoIdle()
    {
        _lightBuyer.Cancel();
        _depositSyncTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _lightBuy = null;
        _busy = false;
        _eventTrip = false;
        _lightTrip = false;
        _loopRooms = Array.Empty<RoomKey>();
        _phase = DepositPhase.Idle;
        ReroutingChanged?.Invoke();
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
        // OnRoomEntered is driven by AppServices (not a direct subscription).
        _lightBuyer.Dispose();
        _depositSyncTimer.Dispose();
    }

    public enum EventTripOutcome { Done, Failed, Stopped }

    private enum DepositPhase
    {
        Idle,
        WalkingToDestination,
        AwaitingInventoryForDeposit,
        WalkingToLightShop,
        BuyingLight,
        WalkingBackToOrigin,
    }
}
