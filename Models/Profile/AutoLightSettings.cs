namespace FujinTerm.Models.Profile;

// Per-character "Auto-Light" settings — drives the auto-light provisioning
// engine: how much lit time to carry into a dark route, which light source to
// buy / ready, and when a dwindling supply should trigger a resupply run.
// Stored as the "AutoLight" entry in CharacterProfile.Settings.
//
// Gated by the AutoActionDefaults.AutoLight master flag (Settings → General /
// toolbar toggle) — these knobs only shape behaviour once auto-light is on.
// The engine reads them live through a Func<AutoLightSettings> delegate, so
// edits take effect without a reconnect.
public sealed class AutoLightSettings
{
    // Hours of lit time to keep on hand before committing to a dark route.
    // The engine divides this by a light's burn time to size the buy
    // (e.g. 6 h ÷ a lantern's 2 h = 3 lanterns). 0 disables provisioning —
    // the engine still readies a single light on demand but won't stock up.
    // Default 6.
    public int CarryHours { get; set; } = 6;

    // Minutes of remaining burn time at which the engine paths out to a shop,
    // restocks to CarryHours, and returns — before the light runs dry
    // mid-route. 0 disables the reorder run. Default 60.
    public int ReorderThresholdMinutes { get; set; } = 60;

    // Game name of the light source to buy and ready. null / empty means
    // auto-pick: the engine chooses a light whose projected strength covers the
    // route's darkest room (see Game.Light.RouteLightScanner). A named light the
    // active set doesn't carry falls back to auto-pick.
    public string? PreferredLightName { get; set; }
}
