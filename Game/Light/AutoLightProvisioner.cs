using FujinTerm.Game.Inventory;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Light;

// The active auto-light engine, split across a predictive PROVISIONING path and a
// reactive READYING path:
//
//  • Provisioning (predictive) — on each freshly-planned route (announced by
//    AutoWalkManager.SetRouteAnnouncer) it scans the path for its darkest room and
//    asks the pure AutoLightPlanner what's needed. A Buy / Reorder verdict (a light
//    the pack lacks, or a dwindling readied charge) becomes a shop detour handed to
//    AutoLightShopRouter. This keeps the pack HOLDING a covering light ahead of
//    time; it deliberately never `use`s one predictively.
//  • Readying (reactive) — a light is `use`d only when the game itself reports we
//    can't see (OnDarkRoomObserved, driven by the "very dark" / "pitch black"
//    lines), and `rem`d again the moment we step into a room seeable on worn gear
//    alone (OnRoomEntered). The server's can't-see line is the single source of
//    truth for WHEN a light is lit: we light a room exactly when the server says
//    it's unseeable and put the light away otherwise, so a route predictor that
//    wrongly guesses a lit room is dark can no longer over-light it.
//
// Gating: every action sits behind the single AutoLight master toggle. There is
// deliberately no separate opt-in; a player who doesn't want the client touching
// their light simply leaves AutoLight off.
//
// A readied light lands in the snapshot only on the next i dump, so a local pending
// latch bridges the gap between sending use and the dump confirming it — a repeated
// can't-see line on the same stale snapshot won't double-send, and a newer dump
// that lands without our light retires the latch so a failed send retries.
public sealed class AutoLightProvisioner
{
    // LogService category — [AutoLight] rows per ready / deferral.
    public const string LogCategory = "AutoLight";

    private readonly Func<bool> _isEnabled;
    private readonly Func<InventorySnapshot> _snapshot;
    private readonly Func<IReadOnlyList<LightItem>> _catalogue;
    private readonly Func<RoomKey, Room?> _resolveRoom;
    private readonly Func<int> _wornIllu;
    private readonly Func<AutoLightSettings> _settings;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    // Hand-off for the Buy verdict: the shop-detour router. Null until wired,
    // in which case a Buy is logged and left for the reactive need-poster.
    private Action<AutoLightBuyRequest>? _provision;

    // The light we last asked to ready, and the snapshot it was decided against.
    // Guards a re-send of `use` before the readied light shows up in a later `i`
    // dump; retired once a newer dump lands (confirming it or not).
    private string? _pendingReadyName;
    private DateTimeOffset _pendingSnapshotTime;

    // The readied light instance we last fired a reorder for. A reorder is
    // requested at most once per instance — the readied charge only refreshes on
    // an `i` dump, so an unlatched reorder would re-detour on every dump while the
    // light sits below the threshold, over-buying. Retired when the readied light
    // is replaced/refreshed (name change, charge climbs, or it's gone), which is
    // the only trustworthy signal a fresh light got lit — carried-spare charge is
    // unknowable until it's `use`d, so we never key the latch off the pack.
    private ReadiedLight? _reorderRequestedFor;

    // The name of the light THIS engine reactively `use`d for a dark room. Set in
    // ReadyLight, cleared when we `rem` it on entering a seeable room (OnRoomEntered)
    // or when it burns out (OnReadiedLightExpired). Only auto-readied lights are
    // rem'd — a light the player readied by hand is theirs to manage, so we never
    // put it away. Null when we hold no auto-readied light.
    private string? _autoReadiedName;

    // Set when "flickers and goes out" reports the readied light burned out. The
    // snapshot's ReadiedLight only clears on the next `i` dump, so until then it
    // lies about a light that no longer exists — this flag lets the dark-room path
    // treat the currently-lit strength as 0 so a carried spare re-readies instead of
    // being judged "already lit by something stronger". Cleared on the next dump
    // (ground truth) or when we ready a fresh light.
    private bool _readiedLightBurnedOut;

    public AutoLightProvisioner(
        Func<bool> isEnabled,
        Func<InventorySnapshot> snapshot,
        Func<IReadOnlyList<LightItem>> catalogue,
        Func<RoomKey, Room?> resolveRoom,
        Func<int> wornIllu,
        Func<AutoLightSettings> settings,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(resolveRoom);
        ArgumentNullException.ThrowIfNull(wornIllu);
        ArgumentNullException.ThrowIfNull(settings);
        _isEnabled = isEnabled;
        _snapshot = snapshot;
        _catalogue = catalogue;
        _resolveRoom = resolveRoom;
        _wornIllu = wornIllu;
        _settings = settings;
        _log = log;
    }

    // Bind the wire-sender — the gate-wrapped engine pipeline from
    // MainWindowViewModel.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Bind the shop-detour hand-off — AutoLightShopRouter.OnBuyRequested. Until
    // bound, a Buy verdict is logged and deferred to the reactive need-poster.
    public void SetProvisioner(Action<AutoLightBuyRequest> provision) => _provision = provision;

    // Test seam — bytes the engine asked to write to the wire.
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    // Route-planned handler bound to AutoWalkManager.SetRouteAnnouncer. Scans the
    // route, runs the planner, and readies a covering light when one is called
    // for. A no-op unless the AutoLight master toggle is on.
    public void OnRoutePlanned(IReadOnlyList<RoomKey> route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!_isEnabled()) return;

        InventorySnapshot snap = _snapshot();
        string? readiedName = snap.ReadiedLight?.Name;

        // Retire the pending `use` once the world moves on: either the dump now
        // shows our light lit (confirmed) or a newer dump landed without it (the
        // send didn't take — allow a retry). Until then re-issuing it is skipped.
        if (_pendingReadyName is not null
            && (string.Equals(readiedName, _pendingReadyName, StringComparison.OrdinalIgnoreCase)
                || snap.LastUpdated > _pendingSnapshotTime))
            _pendingReadyName = null;

        RetireReorderLatch(snap.ReadiedLight);

        int wornIllu = _wornIllu();
        IReadOnlyList<LightItem> catalogue = _catalogue();
        RouteLightScan scan = RouteLightScanner.Scan(route, _resolveRoom, wornIllu);
        AutoLightPlan plan = AutoLightPlanner.Plan(
            scan, wornIllu, snap.ReadiedLight,
            CarriedLights(snap.CarriedItems, catalogue), catalogue, _settings());

        switch (plan.Action)
        {
            case AutoLightAction.Ready:
                // Predictive readying is deliberately suppressed. A light is lit
                // only when the server reports we can't see (OnDarkRoomObserved),
                // never ahead of a route whose darkness prediction can
                // false-positive in a room that renders fine (the over-lit town
                // report). The Buy / Reorder verdicts below still run — they only
                // provision the pack, they don't `use` anything.
                _log?.Debug(LogCategory,
                    $"route-dark ready suppressed (reactive-only policy): {plan.Reason}");
                break;

            case AutoLightAction.Buy:
                RequestProvision(plan.LightName!, plan.BuyCount, catalogue, plan.Reason);
                break;

            case AutoLightAction.Reorder:
                RequestReorder(plan, snap.ReadiedLight, catalogue);
                break;

            case AutoLightAction.None:
            default:
                break;
        }
    }

    // Advisor for an orchestrated errand (the auto-deposit reroute owns the walk):
    // given a route the character is about to traverse, decide whether it must buy a
    // light to cover it and, if so, what to buy. Unlike OnRoutePlanned this sends
    // nothing on the wire and ignores the ready / reorder branches — the caller
    // drives its own shop detour and buy. Returns null when no buy is warranted
    // (route lit, provisioning off, covered by a carried / lit light, or the light
    // has no catalogue id). Null too unless the AutoLight master toggle is on, so
    // the whole errand rides that one gate exactly like the reactive path.
    public AutoLightBuyRequest? PlanRouteBuy(IReadOnlyList<RoomKey> route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!_isEnabled()) return null;

        InventorySnapshot snap = _snapshot();
        int wornIllu = _wornIllu();
        IReadOnlyList<LightItem> catalogue = _catalogue();
        RouteLightScan scan = RouteLightScanner.Scan(route, _resolveRoom, wornIllu);
        AutoLightPlan plan = AutoLightPlanner.Plan(
            scan, wornIllu, snap.ReadiedLight,
            CarriedLights(snap.CarriedItems, catalogue), catalogue, _settings());

        return plan.Action == AutoLightAction.Buy
            ? ResolveBuy(plan.LightName!, plan.BuyCount, catalogue)
            : null;
    }

    // Reorder poll bound to InventoryManager.Changed. An i dump is the only moment
    // the readied light's charge refreshes, so this is where a dwindling supply is
    // caught: it re-runs the planner against an empty route (the reorder verdict is
    // route-independent) and hands a resulting restock to the shop-detour router —
    // at most once per readied-light instance. A no-op unless the AutoLight master
    // toggle is on.
    public void OnInventoryChanged()
    {
        if (!_isEnabled()) return;

        InventorySnapshot snap = _snapshot();
        // A fresh `i` dump is ground truth for what's readied, so the burned-out
        // bridge flag has served its purpose — retire it here so it can't linger
        // and skew a later dark-room strength comparison.
        _readiedLightBurnedOut = false;
        RetireReorderLatch(snap.ReadiedLight);
        if (snap.ReadiedLight is null) return;

        IReadOnlyList<LightItem> catalogue = _catalogue();
        AutoLightPlan plan = AutoLightPlanner.Plan(
            RouteLightScan.Empty, _wornIllu(), snap.ReadiedLight,
            CarriedLights(snap.CarriedItems, catalogue), catalogue, _settings());

        if (plan.Action == AutoLightAction.Reorder)
            RequestReorder(plan, snap.ReadiedLight, catalogue);
    }

    // Reactive handler for the "Your <light> flickers and goes out." line. The
    // readied light is gone, but the snapshot won't reflect that until the next `i`
    // dump. We don't ready here: if the room stays seeable no re-ready is wanted,
    // and if it's dark the game re-emits its "can't see" line right after (the
    // dark-room path then readies a carried spare). What we MUST do is drop both
    // latches keyed on the now-gone light: _pendingReadyName so a re-ready of the
    // same light name isn't blocked as "still in flight" (the stuck-blind report),
    // and _autoReadiedName so OnRoomEntered doesn't later `rem` a light that no
    // longer exists. A no-op unless the AutoLight master toggle is on.
    public void OnReadiedLightExpired()
    {
        if (!_isEnabled()) return;
        _readiedLightBurnedOut = true;
        _pendingReadyName = null;
        _autoReadiedName = null;
        _log?.Info(LogCategory, "readied light burned out — cleared latches for re-ready");
    }

    // Reactive handler for a live "can't see" room-light line — the server is
    // telling us this room is unseeable right now, which is the authoritative
    // signal to light it. We ready the best carried light UNLESS what's already lit
    // is at least as strong (readying a weaker torch over a lit lantern only
    // downgrades — the room is simply darker than anything we carry). A burned-out
    // light counts as strength 0 despite the stale snapshot, so a spare relights.
    // The in-flight pending latch (ReadyLight's own guard) stops a repeated dark
    // line on a stale snapshot from double-`use`ing. Buying a light we don't carry
    // stays with the reactive need-poster / get path; this only readies what's
    // already in the pack.
    public void OnDarkRoomObserved()
    {
        if (!_isEnabled()) return;

        InventorySnapshot snap = _snapshot();

        // Retire a stale pending `use` so a failed send can retry: the dump now
        // shows our light lit (confirmed) or a newer dump landed without it (the
        // send didn't take).
        if (_pendingReadyName is not null
            && (string.Equals(snap.ReadiedLight?.Name, _pendingReadyName, StringComparison.OrdinalIgnoreCase)
                || snap.LastUpdated > _pendingSnapshotTime))
            _pendingReadyName = null;

        IReadOnlyList<LightItem> catalogue = _catalogue();
        IReadOnlyList<LightItem> carried = CarriedLights(snap.CarriedItems, catalogue);
        if (carried.Count == 0) return;   // nothing carried — leave buying to the need-poster

        LightItem? pick = PreferredCarried(carried, _settings().PreferredLightName)
            ?? StrongestCarried(carried);
        if (pick is not { } chosen) return;

        // What's effectively lit right now: 0 if nothing readied or the readied
        // light just burned out (stale snapshot), else its catalogue strength.
        int litStrength = !_readiedLightBurnedOut && snap.ReadiedLight is { } lit
            ? StrengthOf(lit.Name, catalogue)
            : 0;
        if (chosen.Strength <= litStrength) return;   // nothing carried beats what's lit

        // Swap only when a genuinely-lit different light is being replaced; a
        // burned-out light needs no `rem` (it's already gone).
        string? swapFrom = _readiedLightBurnedOut ? null : snap.ReadiedLight?.Name;
        ReadyLight(chosen.Name, swapFrom, snap.LastUpdated,
            "dark room observed: ready carried light");
    }

    // Reactive handler for stepping into a room the graph knows the light level of.
    // Enforces the other half of the policy — "only use a light in rooms we can't
    // see": if THIS engine has a light readied and the room is seeable on worn gear
    // ALONE (the readied light excluded from _wornIllu()), put the light away with
    // `rem`. Guarded upstream by !RoomTracker.IsInDarkRoom so a dark room the game
    // hasn't rendered never reaches here — we never `rem` in the dark. A light the
    // player readied by hand is left untouched (_autoReadiedName is null for it).
    // Fail-safe: a room the graph can't resolve never fires this (the caller only
    // passes a known room), so an unmapped room keeps the light lit.
    public void OnRoomEntered(Room room)
    {
        ArgumentNullException.ThrowIfNull(room);
        if (!_isEnabled()) return;
        if (_autoReadiedName is not { } lit) return;
        if (LightModel.IlluGapToSee(_wornIllu(), room.Light) != 0) return;

        _wire.Send($"rem {lit}");
        _autoReadiedName = null;
        _pendingReadyName = null;
        _log?.Info(LogCategory, $"removed auto-readied {lit} — room seeable without it");
    }

    // The preferred light if it's in the pack, else null. Kept local to the
    // reactive path — the route planner does its own preferred/coverage pick.
    private static LightItem? PreferredCarried(IReadOnlyList<LightItem> carried, string? preferredName)
    {
        if (string.IsNullOrWhiteSpace(preferredName)) return null;
        foreach (LightItem l in carried)
            if (string.Equals(l.Name, preferredName.Trim(), StringComparison.OrdinalIgnoreCase))
                return l;
        return null;
    }

    private static LightItem? StrongestCarried(IReadOnlyList<LightItem> carried)
    {
        LightItem? best = null;
        foreach (LightItem l in carried)
            if (best is not { } b || l.Strength > b.Strength) best = l;
        return best;
    }

    // The catalogue strength of a readied light by its parsed name, or 0 when the
    // name isn't a catalogue light (unknown / user-curated). Used to compare what's
    // lit against a carried candidate on the reactive dark path.
    private static int StrengthOf(string name, IReadOnlyList<LightItem> catalogue)
    {
        foreach (LightItem l in catalogue)
            if (string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
                return l.Strength;
        return 0;
    }

    // Fire a reorder detour once per readied-light instance. The route-dark Buy
    // path relies on the router's own detour/suppression de-dup, but a reorder
    // re-fires on every `i` dump while the light stays below threshold, so it
    // needs this latch on top. Only latch when the hand-off actually took.
    private void RequestReorder(
        AutoLightPlan plan, ReadiedLight? current, IReadOnlyList<LightItem> catalogue)
    {
        if (_reorderRequestedFor is not null) return;
        if (RequestProvision(plan.LightName!, plan.BuyCount, catalogue, plan.Reason))
            _reorderRequestedFor = current;
    }

    // Retire the reorder latch once the light we reordered for is gone or a fresh
    // one is lit. Charge drains monotonically for a given physical light, so a
    // climb in Readied (or a name change / a null) is the signal a different light
    // took its place — at which point a new reorder may fire when it too dwindles.
    private void RetireReorderLatch(ReadiedLight? current)
    {
        if (_reorderRequestedFor is not { } prev) return;
        if (current is not { } cur
            || !string.Equals(cur.Name, prev.Name, StringComparison.OrdinalIgnoreCase)
            || cur.Readied > prev.Readied)
            _reorderRequestedFor = null;
    }

    private void ReadyLight(string name, string? readiedName, DateTimeOffset snapTime, string reason)
    {
        // The pending `use` is still in flight (snapshot hasn't caught up) — don't
        // fire the same one again on this stale snapshot.
        if (_pendingReadyName is not null
            && string.Equals(name, _pendingReadyName, StringComparison.OrdinalIgnoreCase))
            return;

        // Swap: a different light is lit — put it away before lighting the new
        // one (MajorMUD readies with `use`, unreadies with `rem`).
        if (readiedName is not null
            && !string.Equals(readiedName, name, StringComparison.OrdinalIgnoreCase))
            _wire.Send($"rem {readiedName}");

        _wire.Send($"use {name}");
        _pendingReadyName = name;
        _pendingSnapshotTime = snapTime;
        // Track what we lit so OnRoomEntered can `rem` it once we reach a room that
        // renders without it — the reactive-only "light only what we can't see" rule.
        _autoReadiedName = name;
        // A fresh light is now lit/pending, so the burned-out bridge no longer holds.
        _readiedLightBurnedOut = false;
        _log?.Info(LogCategory, $"readied {name} ({reason})");
    }

    // Hand a Buy / Reorder verdict to the shop-detour router, resolving the
    // light's MDB id from the catalogue (the router's shop / carried-count lookups
    // key on id). Returns whether the hand-off actually fired — the reorder latch
    // keys off that so a dropped request (no router wired / unknown id) doesn't
    // wedge the latch shut. Until a router is wired, the reactive AutoLightManager
    // still posts a LightSource need, so a Buy just logs here.
    private bool RequestProvision(
        string name, int count, IReadOnlyList<LightItem> catalogue, string reason)
    {
        if (_provision is null)
        {
            _log?.Debug(LogCategory, $"buy deferred (no provisioner): {reason}");
            return false;
        }
        if (ResolveBuy(name, count, catalogue) is not { } req) return false;

        _log?.Info(LogCategory, $"provision requested: {reason}");
        _provision(req);
        return true;
    }

    // Resolve a light name + carry count into a buy request, looking up the light's
    // MDB id from the catalogue (shop / carried-count lookups key on id). Null when
    // the name isn't a catalogue light (no id to buy against).
    private AutoLightBuyRequest? ResolveBuy(string name, int count, IReadOnlyList<LightItem> catalogue)
    {
        int itemId = 0;
        foreach (LightItem light in catalogue)
            if (string.Equals(light.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                itemId = light.Number;
                break;
            }
        if (itemId <= 0)
        {
            _log?.Debug(LogCategory, $"buy skipped: no catalogue id for '{name}'");
            return null;
        }
        return new AutoLightBuyRequest(itemId, name, Math.Max(1, count));
    }

    // The carried-but-unworn tokens from the last `i` dump that name a light in
    // the active catalogue. A stack keeps its leading count in the dump ("5
    // torch"), but the catalogue keys on the bare name, so the count is stripped
    // before a case-insensitive match — otherwise a pack of torches reads as
    // zero carried lights and the engine never readies (or, worse, buys more).
    private static IReadOnlyList<LightItem> CarriedLights(
        IReadOnlyList<string> carried, IReadOnlyList<LightItem> catalogue)
    {
        List<LightItem>? lights = null;
        foreach (string token in carried)
        {
            string trimmed = StripLeadingCount(token.Trim());
            foreach (LightItem light in catalogue)
                if (string.Equals(light.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    (lights ??= new List<LightItem>()).Add(light);
                    break;
                }
        }
        return (IReadOnlyList<LightItem>?)lights ?? Array.Empty<LightItem>();
    }

    // Drop a stack's leading "<n> " count ("5 torch" -> "torch"). Item names
    // never start with a digit, so a leading digit-run followed by a space is
    // always a count; anything else is returned untouched.
    private static string StripLeadingCount(string token)
    {
        int i = 0;
        while (i < token.Length && char.IsDigit(token[i])) i++;
        if (i == 0 || i >= token.Length || token[i] != ' ') return token;
        return token[(i + 1)..].TrimStart();
    }
}
