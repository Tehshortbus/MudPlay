using Avalonia.Threading;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// Sell detours. An item flagged "Make detours to sell this item" (with Auto-sell)
// that's carried above its "Detour to sell if above" count turns a running walk-to,
// loop or Auto-Lair aside to a shop that trades it: stop the engine, walk to the
// shop, let AutoSellManager sell there on arrival, then carry on — the walk-to
// resumes to its destination, a loop walks back to its nearest room and a lair to
// where it left off, and restarts (DetourResume, shared with the auto-deposit reroute).
//
// Shop choice: the shops the user ticked "Sell here" on the item (any that trade it
// when none are), and among those the one adding the fewest steps to the trip
// (PathItemShopRouter.TrySelectShop). No detour when the running engine reaches such
// a shop on its own — the walk ends there, the loop passes through it, or it's a
// marked lair room — since Auto-sell sells in passing. After one shop, a still-due
// item whose shops weren't tried yet sends the detour on to the next before heading
// back. A shop that refuses an item, or can't be reached, isn't tried for it again
// this session. One that just didn't sell it — Auto-sell had nothing to queue there,
// or no sale reply came — waits UnsoldRetry before it's tried again: that's no proof
// the shop won't buy it (report paradigm-20260929-060520 wrote a shop off for the
// session and ignored every later pickup).
//
// A bank run joins in. When an auto-deposit comes due while anything Auto-sell would
// sell is carried — detour count or no, "Make detours" ticked or not — and a shop for
// it stands within the Cash setting's step count of the bank, the sale goes first and the bank run
// takes over from the shop, so one trip does both and the sale's coins are banked
// with the rest (user, 2026-10-05; report paradigm-20261005-214728 walked a dagger
// and a shuriken to the bank and back unsold).
//
// Single controller: it only starts when nothing else owns movement (the caller's
// blocked probe covers combat, party following and the other errand engines) and
// stands down if movement is stopped externally mid-detour. A walk sends its next
// step the moment a room confirms, so a detour that comes due mid-step holds the
// engine at the room it's entering (SellDetourGate) and takes over from there. UI
// thread only.
public sealed class SellDetourManager : IDisposable
{
    public const string LogCategory = "SellDetour";

    // One carried item that can take a sell detour: how many are carried, the keep
    // floor Auto-sell stops at, the count above which a detour goes (null: the item
    // makes no detours of its own, and is only sold in passing or ahead of a bank
    // run), and the shop rooms it may use (already narrowed to the user's picks).
    public sealed record Candidate(int Number, string Name, int Carried, int KeepFloor, int? DetourAbove,
        IReadOnlyList<RoomKey> Shops)
    {
        public bool Due => DetourAbove is { } above && Carried > Math.Max(KeepFloor, above);

        // Auto-sell would sell some of it at a shop, whatever the detour count says.
        public bool Sellable => Carried > KeepFloor;
    }

    private enum Phase { Idle, WalkingToShop, Selling, WalkingBack }

    private static readonly TimeSpan UnsoldRetry = TimeSpan.FromMinutes(10);

    private readonly Func<IReadOnlyList<Candidate>> _candidates;
    private readonly Func<RoomKey, RoomKey, int?> _distance;
    private readonly RoomTracker _tracker;
    private readonly AutoWalkManager _walker;
    private readonly LoopRunner _loops;
    private readonly AutoLairManager _lair;
    private readonly AutoSellManager _sell;
    private readonly MovementCoordinator _coordinator;
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _blocked;
    // A shop this many steps or fewer from the bank is near enough to add to a bank
    // run; 0 adds none (CashSettings.SellOnBankRunWithinSteps).
    private readonly Func<int> _nearBankSteps;
    private readonly LogService? _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<RoomKey, Loop, RoomKey?> _nearestLoopRoom;
    private readonly Action<Action> _post;
    private readonly Game.Light.ErrandLightStop? _lightStop;

    private Phase _phase = Phase.Idle;
    private DetourResume _resume;
    private RoomKey _origin;
    private RoomKey _returnTo;
    private RoomKey _shop;
    private bool _drivingWalker;
    private readonly HashSet<RoomKey> _visited = new();
    // (item, shop) pairs the shop refused or we couldn't reach — not tried again this
    // session — and pairs that sold nothing, with when they may be tried again.
    private readonly HashSet<(int Item, RoomKey Shop)> _refused = new();
    private readonly Dictionary<(int Item, RoomKey Shop), DateTimeOffset> _retryAfter = new();
    private Dictionary<int, int> _carriedAtShop = new();
    private bool _gateHeld;
    private bool _disposed;
    // The bank this detour goes ahead of (SellAheadOfBankRun). The deposit gate fires
    // once per crossing and already has, so a detour that ends anywhere but in that
    // bank run says so through BankRunNotTaken or the gate stays latched.
    private RoomKey? _bankRun;
    // Why the last Evaluate with a due item didn't detour — logged on change, shown in
    // the bug report, so "it never detoured" names its cause.
    private string? _lastDecline;

    public SellDetourManager(
        Func<IReadOnlyList<Candidate>> candidates,
        Func<RoomKey, RoomKey, int?> distance,
        RoomTracker tracker,
        AutoWalkManager walker,
        LoopRunner loops,
        AutoLairManager lair,
        AutoSellManager sell,
        MovementCoordinator coordinator,
        Func<bool> isEnabled,
        Func<bool> blocked,
        Func<RoomKey, Loop, RoomKey?> nearestLoopRoom,
        Func<int> nearBankSteps,
        LogService? log = null,
        Func<DateTimeOffset>? clock = null,
        Action<Action>? post = null,
        // Auto-Light's stop for a leg that runs dark (and the walk on after it):
        // unwired, the legs walk as they are.
        Game.Light.ErrandLightStop? lightStop = null)
    {
        _lightStop = lightStop;
        _coordinator = coordinator;
        _candidates = candidates;
        _distance = distance;
        _tracker = tracker;
        _walker = walker;
        _loops = loops;
        _lair = lair;
        _sell = sell;
        _isEnabled = isEnabled;
        _blocked = blocked;
        _nearBankSteps = nearBankSteps;
        _log = log;
        _now = clock ?? (static () => DateTimeOffset.Now);
        _nearestLoopRoom = nearestLoopRoom;
        _post = post ?? (static action => Dispatcher.UIThread.Post(action));
        _walker.Event += OnWalkEvent;
        _sell.Finished += OnSellFinished;
        _sell.ItemRefused += OnItemRefused;
    }

    public bool IsDetouring => _phase != Phase.Idle;

    // Raised when a detour starts, turns for home (IsReturning), or ends (IsDetouring
    // flips).
    public event Action? DetouringChanged;

    // Sold up and walking back to where the run picks up.
    public bool IsReturning => _phase == Phase.WalkingBack;

    // Offered the way back once everything's sold: true when a bank run took it over
    // (AutoDepositManager.TakeOverFromDetour) because the sale left a deposit due.
    public Func<DetourResume, RoomKey, bool>? HandOffToBank { get; set; }

    // Raised when a detour that went ahead of a bank run ends without the bank run
    // taking over (CashManager.NotifyAutoDepositAborted re-arms the deposit gate).
    public Action? BankRunNotTaken { get; set; }

    // The engine this detour will pick back up (meaningful while it runs).
    public DetourResume ResumePlan => _resume;

    // One-line status for the bug report.
    public string Status
    {
        get
        {
            string phase = _phase switch
            {
                Phase.Idle => "idle",
                Phase.WalkingBack => $"walking back to {_returnTo} (resume {_resume.Kind})",
                _ => $"{_phase} at/to {_shop} (resume {_resume.Kind})",
            };
            if (_bankRun is { } bank) phase += $", ahead of the bank run to {bank}";
            string refused = _refused.Count == 0 ? "none"
                : string.Join(", ", _refused.Select(r => $"item #{r.Item} at {r.Shop}"));
            DateTimeOffset now = _now();
            List<string> waiting = _retryAfter.Where(r => r.Value > now)
                .Select(r => $"item #{r.Key.Item} at {r.Key.Shop} until {r.Value:HH:mm:ss}").ToList();
            return $"{phase}; last decline: {_lastDecline ?? "(none)"}; shops that refused or can't be reached: {refused}; "
                + $"sold nothing lately: {(waiting.Count == 0 ? "none" : string.Join(", ", waiting))}";
        }
    }

    // Re-check whether a detour is due. Driven by the heartbeat and inventory changes;
    // a no-op unless a walk / loop / lair runs, the room is settled and nothing else
    // owns movement.
    public void Evaluate()
    {
        if (_phase != Phase.Idle) return;
        if (!_isEnabled() || _blocked() || _tracker.State.CurrentRoom is not { } here) { ReleaseStop("not now"); return; }
        DetourResume resume = DetourResume.Snapshot(_walker, _loops, _lair, includeWalk: true);
        if (resume.Kind == DetourResumeKind.None) { ReleaseStop("nothing running"); return; }

        RoomKey cur = here.Key;
        RoomKey back = resume.WalkDestination ?? cur;
        List<string> skipped = new();
        List<RoomKey> shops = ShopsToVisit(c => c.Due, cur, resume, skipped);
        if (shops.Count == 0)
        {
            if (skipped.Count > 0) Decline(string.Join("; ", skipped));
            ReleaseStop("nothing to sell");
            return;
        }
        if (!PathItemShopRouter.TrySelectShop(shops.Distinct().ToList(), cur, back, _distance, out RoomKey shop))
        {
            Decline($"no route from {cur} to {string.Join(", ", shops.Distinct())} and back");
            ReleaseStop("no reachable shop");
            return;
        }
        _lastDecline = null;

        // Mid-step: hold the engine at the room it's entering and take over there.
        if (_tracker.State.Confidence != RoomConfidence.Confirmed)
        {
            if (!_gateHeld)
            {
                _gateHeld = true;
                _coordinator.AssertGate(MovementCoordinator.SellDetourGate, nameof(SellDetourManager),
                    "stopping at the next room to detour and sell");
            }
            return;
        }

        _resume = resume;
        _origin = cur;
        _visited.Clear();
        _lightStop?.BeginRun();
        _log?.Info(LogCategory, $"detouring from {cur} to sell at {shop} (resume {resume.Kind}{(resume.WalkDestination is { } d ? $" to {d}" : "")})");
        _drivingWalker = true;
        try { resume.Stop(_walker, _loops, _lair, "sell detour"); }
        finally { _drivingWalker = false; }
        ReleaseStop("detour started");
        GoToShop(shop);
    }

    // The shops worth turning aside to for the carried items `wanted` picks: each
    // one's usable shops, unless the running engine stands in one of them anyway.
    // Why an item was left out goes in skipped.
    private List<RoomKey> ShopsToVisit(Func<Candidate, bool> wanted, RoomKey cur, DetourResume resume, List<string> skipped)
    {
        List<RoomKey> shops = new();
        foreach (Candidate c in _candidates())
        {
            if (!wanted(c)) continue;
            if (c.Shops.Count == 0) { skipped.Add($"{c.Name}: no shop that trades it among your picks"); continue; }
            List<RoomKey> usable = c.Shops.Where(s => Usable(c.Number, s)).ToList();
            if (usable.Count == 0) { skipped.Add($"{c.Name}: {WhyNoShop(c)}"); continue; }
            // The engine reaches one of its shops anyway: Auto-sell sells in passing.
            RoomKey? passing = usable.Cast<RoomKey?>()
                .FirstOrDefault(s => s!.Value.Equals(cur) || ReachedAnyway(s.Value, resume));
            if (passing is not null)
            {
                skipped.Add($"{c.Name}: the {resume.Kind} reaches {passing} itself");
                continue;
            }
            shops.AddRange(usable);
        }
        return shops;
    }

    // Offered a bank run as it comes due (AutoDepositManager.SellFirst). True when
    // this detour went first: it stopped the engine, is walking to a shop near the
    // bank, and hands the run on from there through HandOffToBank. False — the bank
    // run goes as it always did — when nothing carried sells near the bank.
    public bool SellAheadOfBankRun(DetourResume resume, RoomKey bank)
    {
        if (_phase != Phase.Idle || !_isEnabled() || HandOffToBank is null) return false;
        if (_tracker.State.CurrentRoom is not { } here) return false;
        RoomKey cur = here.Key;
        List<string> skipped = new();
        List<RoomKey> shops = ShopsToVisit(c => c.Sellable, cur, resume, skipped)
            .Where(s => NearBank(s, bank)).Distinct().ToList();
        if (!PathItemShopRouter.TrySelectShop(shops, cur, bank, _distance, out RoomKey shop))
        {
            if (skipped.Count > 0)
                _log?.Debug(LogCategory, $"bank run to {bank}: nothing to sell on the way — {string.Join("; ", skipped)}");
            return false;
        }

        ReleaseStop("going ahead of a bank run");
        _lastDecline = null;
        _resume = resume;
        _origin = cur;
        _bankRun = bank;
        _visited.Clear();
        _lightStop?.BeginRun();
        _log?.Info(LogCategory, $"a bank run to {bank} is due — selling at {shop} on the way (from {cur}, resume {resume.Kind})");
        _drivingWalker = true;
        try { resume.Stop(_walker, _loops, _lair, "sell detour ahead of a bank run"); }
        finally { _drivingWalker = false; }
        GoToShop(shop);
        return true;
    }

    private bool NearBank(RoomKey shop, RoomKey bank) =>
        _distance(shop, bank) is { } steps && steps <= _nearBankSteps();

    private bool Usable(int item, RoomKey shop)
        => !_refused.Contains((item, shop))
           && !(_retryAfter.TryGetValue((item, shop), out DateTimeOffset after) && after > _now());

    private string WhyNoShop(Candidate c)
    {
        List<DateTimeOffset> waits = c.Shops
            .Where(s => !_refused.Contains((c.Number, s)))
            .Select(s => _retryAfter[(c.Number, s)])
            .ToList();
        return waits.Count == 0
            ? "its shops refused it or can't be reached"
            : $"its shops sold none lately; next try after {waits.Min():HH:mm:ss}";
    }

    private void Decline(string why)
    {
        if (why == _lastDecline) return;
        _lastDecline = why;
        _log?.Info(LogCategory, $"due to sell but not detouring — {why}");
    }

    // Reset States: stand down without resuming.
    public void Cancel()
    {
        _lightStop?.Cancel();
        ReleaseStop("cancelled");
        if (_phase != Phase.Idle) GoIdle("cancelled");
    }

    private void ReleaseStop(string why)
    {
        if (!_gateHeld) return;
        _gateHeld = false;
        _coordinator.ClearGate(MovementCoordinator.SellDetourGate, nameof(SellDetourManager), why);
    }

    // Whether the running engine will stand in this shop room without a detour.
    private bool ReachedAnyway(RoomKey shop, DetourResume resume) => resume.Kind switch
    {
        DetourResumeKind.Walk => resume.WalkDestination is { } d && d.Equals(shop),
        DetourResumeKind.Lair => _lair.IsMarked(shop),
        DetourResumeKind.Loop => _loops.ResolveLoopRoomKeys().Contains(shop),
        _ => false,
    };

    private void GoToShop(RoomKey shop)
    {
        _shop = shop;
        _visited.Add(shop);
        // Counted on the way out: by the time the walk to the shop finishes, Auto-sell
        // may already have sold there.
        _carriedAtShop = _candidates().ToDictionary(c => c.Number, c => c.Carried);
        _phase = Phase.WalkingToShop;
        DetouringChanged?.Invoke();
        if (!DriveWalk(shop))
        {
            // No route for want of a toll or fare is "not now", not "never": the
            // purse, or the party's, can cover it on a later detour, so the shop is
            // passed over this time and not written off.
            if (_walker.LastUnpaidFailure == shop)
                _log?.Info(LogCategory, $"can't pay the way to {shop} just now — passing it over this time");
            else
            {
                _log?.Warn(LogCategory, $"can't reach {shop} — carrying on");
                foreach (Candidate c in _candidates())
                    if (c.Shops.Contains(shop)) _refused.Add((c.Number, shop));
            }
            Next();
        }
    }

    // A leg's walk, by a light shop first when the leg, or the walk on from a shop
    // to the bank run or back, runs dark (ErrandLightStop).
    private bool DriveWalk(RoomKey to)
    {
        IReadOnlyList<RoomKey> ahead = _phase == Phase.WalkingBack
            ? Array.Empty<RoomKey>()
            : [_bankRun ?? _resume.WalkDestination ?? _origin];
        _drivingWalker = true;
        try { return _lightStop?.Walk(to, WalkTo, LightStopTakenOver, ahead) ?? WalkTo(to); }
        finally { _drivingWalker = false; }
    }

    private bool WalkTo(RoomKey to) => _walker.WalkTo(to, planThroughAcquirableGates: true);

    private void LightStopTakenOver(string detail)
    {
        if (_phase is Phase.WalkingToShop or Phase.WalkingBack) GoIdle($"movement stopped externally ({detail})");
    }

    private void OnWalkEvent(WalkEvent e)
    {
        if (_lightStop?.OnWalkEvent(e) == true) return;
        if (_phase is Phase.Idle or Phase.Selling) return;
        if (e.Kind == WalkEventKind.Stopped)
        {
            if (_drivingWalker) return;
            GoIdle($"movement stopped externally ({e.Detail})");
            return;
        }
        if (_phase == Phase.WalkingToShop)
        {
            if (e.Kind == WalkEventKind.Finished) AfterTheLine(Phase.WalkingToShop, ArrivedAtShop);
            else if (e.Kind == WalkEventKind.Failed)
            {
                _log?.Warn(LogCategory, $"path to {_shop} failed — carrying on");
                Next();
            }
        }
        else if (_phase == Phase.WalkingBack && e.Kind is WalkEventKind.Finished or WalkEventKind.Failed)
        {
            Resume();
        }
    }

    // Auto-sell's Selling gate holds the walker in the shop, so the walk's Finished and
    // Auto-sell's own Finished both fire inside the dispatch of the last `You sold`
    // line — before the inventory has taken that line in. Judge the shop once the line
    // is done, or a sale reads as nothing sold (report paradigm-20260930-182949).
    private void AfterTheLine(Phase expected, Action then) =>
        _post(() => { if (_phase == expected) then(); });

    // Auto-sell already queued on arrival (RoomTracker's room change runs ahead of the
    // walker's Finished); wait for it, or move on when there was nothing it would sell.
    private void ArrivedAtShop()
    {
        if (_sell.IsSelling)
        {
            _phase = Phase.Selling;
            _log?.Info(LogCategory, $"selling at {_shop}");
            return;
        }
        NoteUnsold("Auto-sell had nothing to sell here");
        Next();
    }

    private void OnSellFinished()
    {
        if (_phase != Phase.Selling) return;
        AfterTheLine(Phase.Selling, () =>
        {
            NoteUnsold("the sale didn't go through");
            Next();
        });
    }

    private void OnItemRefused(int item)
    {
        if (_phase != Phase.Selling) return;
        _refused.Add((item, _shop));
        _log?.Info(LogCategory, $"{_shop} refuses item #{item} — not trying it there again this session");
    }

    // An item still carried at its arrival count sold nothing here: wait a while before
    // coming back to this shop for it.
    private void NoteUnsold(string why)
    {
        foreach (Candidate c in _candidates())
        {
            if (!c.Shops.Contains(_shop) || _refused.Contains((c.Number, _shop))) continue;
            if (!_carriedAtShop.TryGetValue(c.Number, out int before) || c.Carried < before) continue;
            DateTimeOffset after = _now() + UnsoldRetry;
            _retryAfter[(c.Number, _shop)] = after;
            _log?.Info(LogCategory, $"{_shop} sold no {c.Name} (carried {before} on arrival, {c.Carried} now; {why}) — next try there after {after:HH:mm:ss}");
        }
    }

    // Another shop for a still-due item, or head back.
    private void Next()
    {
        RoomKey? here = _tracker.State.CurrentRoom?.Key;
        // A loop picks up from whichever of its rooms is nearest the shop, not back
        // where the detour began (user, 2026-09-30; report paradigm-20260930-182854).
        _returnTo = _resume.WalkDestination
            ?? (here is { } from && _resume.Loop is { } loop ? _nearestLoopRoom(from, loop) : null)
            ?? _origin;
        if (here is { } cur)
        {
            // Ahead of a bank run the count doesn't matter and the next stop is the bank.
            List<RoomKey> shops = _candidates()
                .Where(c => _bankRun is null ? c.Due : c.Sellable)
                .SelectMany(c => c.Shops.Where(s => !_visited.Contains(s) && Usable(c.Number, s)))
                .Where(s => _bankRun is not { } bank || NearBank(s, bank))
                .Distinct().ToList();
            if (shops.Count > 0
                && PathItemShopRouter.TrySelectShop(shops, cur, _bankRun ?? _returnTo, _distance, out RoomKey next))
            {
                _log?.Info(LogCategory, $"on to {next} for what's left");
                GoToShop(next);
                return;
            }
        }

        // A deposit the sale made due goes to the bank from here (user, 2026-09-30).
        if (HandOffToBank?.Invoke(_resume, _origin) == true)
        {
            _bankRun = null;
            _phase = Phase.Idle;
            DetouringChanged?.Invoke();
            _log?.Info(LogCategory, "sold — a bank run is due, so the bank run takes the way back");
            return;
        }
        DropBankRun("the bank run didn't take over at the shop");

        // A walk-to just resumes toward its destination; a loop walks back to its
        // nearest room, a lair to where it was.
        if (_resume.Kind == DetourResumeKind.Walk || here?.Equals(_returnTo) == true)
        {
            Resume();
            return;
        }
        _phase = Phase.WalkingBack;
        DetouringChanged?.Invoke();
        _log?.Info(LogCategory, _returnTo.Equals(_origin)
            ? $"walking back to {_returnTo}"
            : $"walking back to {_returnTo}, the loop's nearest room (left it at {_origin})");
        if (!DriveWalk(_returnTo)) Resume();
    }

    private void Resume()
    {
        DetourResume r = _resume;
        _lightStop?.Cancel();
        _phase = Phase.Idle;
        DetouringChanged?.Invoke();
        _log?.Info(LogCategory, $"detour done — resuming {r.Kind}");
        _drivingWalker = true;
        try { r.Resume(_walker, _loops, _lair); }
        finally { _drivingWalker = false; }
    }

    private void GoIdle(string why)
    {
        _log?.Info(LogCategory, $"detour abandoned — {why}");
        _lightStop?.Cancel();
        _phase = Phase.Idle;
        DetouringChanged?.Invoke();
        DropBankRun("the detour ahead of it was abandoned");
    }

    private void DropBankRun(string why)
    {
        if (_bankRun is not { } bank) return;
        _bankRun = null;
        _log?.Info(LogCategory, $"the bank run to {bank} isn't going from here — {why}");
        BankRunNotTaken?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _walker.Event -= OnWalkEvent;
        _sell.Finished -= OnSellFinished;
        _sell.ItemRefused -= OnItemRefused;
        ReleaseStop("disposed");
    }
}
