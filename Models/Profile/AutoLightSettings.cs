namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character "Auto-Light" settings — drives the auto-light provisioning
/// engine: how much lit time to carry into a dark route, which light source to
/// buy / ready, and when a dwindling supply should trigger a resupply run.
/// Stored as the <c>"AutoLight"</c> entry in
/// <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// Gated by the existing <see cref="AutoActionDefaults.AutoLight"/> master flag
/// (Settings → General / toolbar toggle) — these knobs only shape behaviour
/// once auto-light is on. The engine reads them live through a
/// <c>Func&lt;AutoLightSettings&gt;</c> delegate, like the other Phase 9
/// engines, so edits take effect without a reconnect.
/// </remarks>
public sealed class AutoLightSettings
{
    /// <summary>
    /// Hours of lit time to keep on hand before committing to a dark route.
    /// The engine divides this by a light's burn time to size the buy
    /// (e.g. 6 h ÷ a lantern's 2 h = 3 lanterns). <c>0</c> disables
    /// provisioning — the engine still readies a single light on demand but
    /// won't stock up. Default 6.
    /// </summary>
    public int CarryHours { get; set; } = 6;

    /// <summary>
    /// Minutes of remaining burn time at which the engine paths out to a shop,
    /// restocks to <see cref="CarryHours"/>, and returns — before the light
    /// runs dry mid-route. <c>0</c> disables the reorder run. Default 60.
    /// </summary>
    public int ReorderThresholdMinutes { get; set; } = 60;

    /// <summary>
    /// Game name of the light source to buy and ready. <c>null</c> / empty means
    /// auto-pick: the engine chooses a light whose projected strength covers the
    /// route's darkest room (see <see cref="Game.Light.RouteLightScanner"/>).
    /// A named light the active set doesn't carry falls back to auto-pick.
    /// </summary>
    public string? PreferredLightName { get; set; }
}
