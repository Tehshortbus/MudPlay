using FujinTerm.Game.Inventory;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Light;

/// <summary>
/// The active auto-light engine. On each freshly-planned route (announced by
/// <see cref="AutoWalkManager.SetRouteAnnouncer"/>) it scans the path for its
/// darkest room, asks the pure <see cref="AutoLightPlanner"/> what to do, and —
/// while the AutoLight master toggle is on — readies a carried light that clears
/// the dark by sending <c>use &lt;light&gt;</c> (removing any different light
/// that's currently lit first with <c>rem &lt;old&gt;</c>). Provisioning a light
/// the pack doesn't hold (the planner's <see cref="AutoLightAction.Buy"/>
/// verdict) is a shop detour that lands in a follow-up slice; here it's logged
/// and left for the reactive <see cref="AutoLightManager"/> need-poster to
/// surface meanwhile.
/// </summary>
/// <remarks>
/// <para>
/// Gating: <em>every</em> action — readying now, buying later — sits behind the
/// single AutoLight master toggle. There is deliberately no separate opt-in; a
/// player who doesn't want the client touching their light simply leaves
/// AutoLight off.
/// </para>
/// <para>
/// The planner's "already covers" guard means a route re-announced mid-run (a
/// loop crosses the same rooms each lap) is a no-op once a covering light is lit,
/// so this engine doesn't re-issue <c>use</c> on every hop. A readied light lands
/// in the snapshot only on the next <c>i</c> dump, though, so a local pending
/// latch bridges the gap between sending <c>use</c> and the dump confirming it —
/// a second walk-start on the same stale snapshot won't double-send, and a newer
/// dump that lands without our light retires the latch so a failed send retries.
/// </para>
/// </remarks>
public sealed class AutoLightProvisioner
{
    /// <summary>LogService category — <c>[AutoLight]</c> rows per ready / deferral.</summary>
    public const string LogCategory = "AutoLight";

    private readonly Func<bool> _isEnabled;
    private readonly Func<InventorySnapshot> _snapshot;
    private readonly Func<IReadOnlyList<LightItem>> _catalogue;
    private readonly Func<RoomKey, Room?> _resolveRoom;
    private readonly Func<int> _wornIllu;
    private readonly Func<AutoLightSettings> _settings;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    // The light we last asked to ready, and the snapshot it was decided against.
    // Guards a re-send of `use` before the readied light shows up in a later `i`
    // dump; retired once a newer dump lands (confirming it or not).
    private string? _pendingReadyName;
    private DateTimeOffset _pendingSnapshotTime;

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

    /// <summary>Bind the wire-sender — the gate-wrapped engine pipeline from
    /// <c>MainWindowViewModel</c>.</summary>
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    /// <summary>Test seam — bytes the engine asked to write to the wire.</summary>
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    /// <summary>
    /// Route-planned handler bound to
    /// <see cref="AutoWalkManager.SetRouteAnnouncer"/>. Scans the route, runs the
    /// planner, and readies a covering light when one is called for. A no-op
    /// unless the AutoLight master toggle is on.
    /// </summary>
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
                // Provisioning detour lands in a follow-up slice; the reactive
                // AutoLightManager still posts a LightSource need meanwhile.
                _log?.Debug(LogCategory, $"buy deferred: {plan.Reason}");
                break;

            case AutoLightAction.None:
            default:
                break;
        }
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
