using System.Text;
using System.Threading;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Light;
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
// Light on the way home. The reroute is the sole controller of the walker for the
// whole errand — the reactive light-shop router is suppressed while IsRerouting, so
// it can't hijack the detour walk-tos (two controllers on one walker is what froze
// the client). To still cover a dark return leg, this manager runs the light
// provisioning itself as an extra phase: after offloading at the bank it scans the
// bank -> origin leg (AutoLightProvisioner.PlanRouteBuy), and if that leg runs dark
// with no carried light to cover it, detours through the fewest-added-steps light
// shop (AutoLightShopRouter.TrySelectShop) — bank -> shop -> buy -> origin — before
// resuming the loop. The whole light detour rides the AutoLight master toggle
// (PlanRouteBuy returns null when it's off), so no separate opt-in. A shop it can't
// reach or a buy that doesn't land in the buy window falls through to the plain
// return walk — a missing light never strands the reroute.
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
    private readonly TimeSpan _buyTimeout;
    private readonly Timer _buyTimer;
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
    // Carried count of the buy item before the buys were sent — a rise past this
    // means a fresh copy landed (BuyingLight resumes the walk home).
    private int _lightBaseCount;

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
        TimeSpan? buyTimeout = null)
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
        _buyTimeout = buyTimeout ?? TimeSpan.FromSeconds(8);
        _buyTimer = new Timer(_ => _post(OnBuyTimeout), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _depositSyncTimer = new Timer(_ => _post(OnDepositSyncTimeout), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _log = log;

        _cash.AutoDepositRequested += OnAutoDepositRequested;
        _walker.Event += OnWalkEvent;
        _tracker.StateChanged += OnRoomEntered;
    }

    // True while a reroute owns the walker (bank/stash detour and the walk back).
    // The reactive shop/drop routers gate their own detours on this: a reroute
    // stops the loop (LoopState.Idle) and drives its own WalkTo legs, so those
    // routers' engineWalkActive check would otherwise see plain walk-tos and
    // hijack the detour — two controllers driving one walker, which spins into a
    // re-announce/BFS loop that locks the UI. Suppressing them here keeps the
    // reroute single-controller.
    public bool IsRerouting => _busy;

    // One-line reroute status for the bug report — the current phase, plus the
    // light being bought while a return-leg light detour is in flight. "idle" when
    // no reroute is running. Diagnoses a walker stuck mid-errand (the original
    // freeze had no capture of which reroute phase owned the walker).
    public string RerouteStatus =>
        _phase == DepositPhase.Idle
            ? "idle"
            : _lightBuy is { } buy
                ? $"{_phase} (light: {buy.LightName} x{buy.Count})"
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
        if (_busy) return;

        // Snapshot BEFORE stopping anything — Stop() clears the engine's
        // run-state, so the resume target must be captured first. Only a
        // Loop or Auto-Lair qualifies; a one-shot walk-to (or an idle
        // stack) has nothing to detour-and-resume.
        ResumeTarget resume = SnapshotRunningEngine();
        if (resume.Kind == ResumeKind.None)
        {
            _log?.Debug(LogCategory, "gate fired but no loop / auto-lair running — ignoring");
            _cash.NotifyAutoDepositAborted();
            return;
        }

        CashSettings cash = _readCash();
        if (!RoomKey.TryParseWire(cash.BankRoomKey, out RoomKey destination))
        {
            _log?.Warn(LogCategory, $"gate fired but BankRoomKey '{cash.BankRoomKey}' is unparseable — ignoring");
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
            _cash.NotifyAutoDepositAborted();
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
        // runs without a competing command stream or asserted gate. Guarded:
        // halting an Approaching loop stops the walker, whose Stopped event must
        // not read as a user abort of the reroute we're mid-launch of.
        _drivingWalker = true;
        try { StopRunningEngine("auto-deposit reroute"); }
        finally { _drivingWalker = false; }

        _phase = DepositPhase.WalkingToDestination;
        if (!RerouteWalkTo(destination))
        {
            _log?.Warn(LogCategory, $"can't reach {destination} — resuming");
            _cash.NotifyAutoDepositAborted();
            Resume();
        }
    }

    // Every reroute-initiated WalkTo goes through here so the Stopped event that
    // WalkTo raises when it supersedes an in-flight walk is attributed to the
    // reroute, not to a user halt (see _drivingWalker).
    private bool RerouteWalkTo(RoomKey destination)
    {
        _drivingWalker = true;
        try { return _walker.WalkTo(destination); }
        finally { _drivingWalker = false; }
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
            GoIdle();
            _cash.NotifyAutoDepositAborted();
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
        _depositSyncTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
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
    // origin) before resuming; otherwise walk straight back. The light detour rides
    // the AutoLight master toggle (PlanRouteBuy returns null when it's off).
    private void BeginReturnLeg()
    {
        if (TryPlanLightDetour(out RoomKey shop, out AutoLightBuyRequest buy))
        {
            _lightBuy = buy;
            _phase = DepositPhase.WalkingToLightShop;
            _log?.Info(LogCategory,
                $"return leg runs dark — detouring to light shop {shop} for '{buy.LightName}' x{buy.Count}");
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
        if (_walker.TryComputeRouteKeys(_destination, _origin) is not { } route) return false;
        if (_provisioner.PlanRouteBuy(route) is not { } req) return false;
        if (!_lightShop.TrySelectShop(_destination, _origin, req.ItemId, out shop))
        {
            _log?.Info(LogCategory,
                $"return leg dark but no reachable shop stocks '{req.LightName}' — returning without light");
            return false;
        }
        buy = req;
        return true;
    }

    // At the light shop: buy the whole carry batch (one `buy <name>` per copy still
    // short of the target) and arm the buy window. Movement resumes only once a
    // fresh copy lands (OnInventoryChanged) or the window elapses (OnBuyTimeout),
    // so the run of buys isn't cut short.
    private void BeginBuyingLight()
    {
        if (_lightBuy is not { } buy)
        {
            WalkBackToOrigin();
            return;
        }
        _lightBaseCount = _carriedCount(buy.ItemId);
        int toBuy = Math.Max(1, buy.Count - _lightBaseCount);
        _phase = DepositPhase.BuyingLight;
        _log?.Info(LogCategory, $"at light shop — buying '{buy.LightName}' x{toBuy}");
        for (int i = 0; i < toBuy; i++) Send($"buy {buy.LightName}");
        _buyTimer.Change(_buyTimeout, Timeout.InfiniteTimeSpan);
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
        if (_phase != DepositPhase.BuyingLight) return;
        if (_lightBuy is not { } buy) return;
        if (_carriedCount(buy.ItemId) <= _lightBaseCount) return;
        _log?.Info(LogCategory, $"'{buy.LightName}' acquired — returning to origin");
        WalkBackToOrigin();
    }

    // Buy window elapsed. If no fresh copy landed the buy didn't take (no gold, out
    // of stock) — head home anyway rather than stalling the reroute. Posted onto the
    // UI thread from the timer.
    private void OnBuyTimeout()
    {
        if (_phase != DepositPhase.BuyingLight) return;
        if (_lightBuy is { } buy && _carriedCount(buy.ItemId) > _lightBaseCount) return; // race: handled
        _log?.Info(LogCategory, "light buy did not complete in time — returning to origin");
        WalkBackToOrigin();
    }

    private void WalkBackToOrigin()
    {
        _buyTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _lightBuy = null;
        _phase = DepositPhase.WalkingBackToOrigin;
        if (!RerouteWalkTo(_origin))
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
        // Decrement the on-hand tracker now — before BeginReturnLeg plans the trip
        // home — so a toll on the return route is weighed against post-deposit
        // wealth. Waiting for the server's "You deposit …" echo would let the
        // route commit through a toll we just banked past (bug: return leg
        // routed on the stale pre-deposit purse and stranded at the toll).
        _noteAutoDeposit(depositValue);
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
                // ResumeAfterDetour, not Start: the loop's first-waypoint reset
                // (session stats + party @reset) already fired at the user's
                // original Start. This bank detour is a continuation, so it must
                // not re-fire that reset.
                if (r.Loop is { } loop) _loopRunner.ResumeAfterDetour(loop);
                break;
        }
    }

    private void GoIdle()
    {
        _buyTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _depositSyncTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _lightBuy = null;
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
        _buyTimer.Dispose();
        _depositSyncTimer.Dispose();
    }

    private enum DepositPhase
    {
        Idle,
        WalkingToDestination,
        AwaitingInventoryForDeposit,
        WalkingToLightShop,
        BuyingLight,
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
