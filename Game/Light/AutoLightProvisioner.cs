using FujinTerm.Game.Inventory;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Light;

// The active auto-light engine. On each freshly-planned route (announced by
// AutoWalkManager.SetRouteAnnouncer) it scans the path for its darkest room, asks
// the pure AutoLightPlanner what to do, and — while the AutoLight master toggle is
// on — readies a carried light that clears the dark by sending use <light>
// (removing any different light that's currently lit first with rem <old>).
// Provisioning a light the pack doesn't hold (the planner's Buy verdict) is a shop
// detour handed to AutoLightShopRouter; when no router is wired it's logged and
// left for the reactive AutoLightManager need-poster to surface meanwhile.
//
// Gating: every action — readying now, buying later — sits behind the single
// AutoLight master toggle. There is deliberately no separate opt-in; a player who
// doesn't want the client touching their light simply leaves AutoLight off.
//
// The planner's "already covers" guard means a route re-announced mid-run (a loop
// crosses the same rooms each lap) is a no-op once a covering light is lit, so
// this engine doesn't re-issue use on every hop. A readied light lands in the
// snapshot only on the next i dump, though, so a local pending latch bridges the
// gap between sending use and the dump confirming it — a second walk-start on the
// same stale snapshot won't double-send, and a newer dump that lands without our
// light retires the latch so a failed send retries.
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
                ReadyLight(plan.LightName!, readiedName, snap.LastUpdated, plan.Reason);
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
        RetireReorderLatch(snap.ReadiedLight);
        if (snap.ReadiedLight is null) return;

        IReadOnlyList<LightItem> catalogue = _catalogue();
        AutoLightPlan plan = AutoLightPlanner.Plan(
            RouteLightScan.Empty, _wornIllu(), snap.ReadiedLight,
            CarriedLights(snap.CarriedItems, catalogue), catalogue, _settings());

        if (plan.Action == AutoLightAction.Reorder)
            RequestReorder(plan, snap.ReadiedLight, catalogue);
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
            return false;
        }

        _log?.Info(LogCategory, $"provision requested: {reason}");
        _provision(new AutoLightBuyRequest(itemId, name, Math.Max(1, count)));
        return true;
    }

    // The carried-but-unworn tokens from the last `i` dump that name a light in
    // the active catalogue. Tokens are bare item names (the readied light and
    // worn gear are split out upstream), so a trimmed case-insensitive match
    // against the catalogue is enough.
    private static IReadOnlyList<LightItem> CarriedLights(
        IReadOnlyList<string> carried, IReadOnlyList<LightItem> catalogue)
    {
        List<LightItem>? lights = null;
        foreach (string token in carried)
        {
            string trimmed = token.Trim();
            foreach (LightItem light in catalogue)
                if (string.Equals(light.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    (lights ??= new List<LightItem>()).Add(light);
                    break;
                }
        }
        return (IReadOnlyList<LightItem>?)lights ?? Array.Empty<LightItem>();
    }
}
