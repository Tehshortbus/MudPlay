namespace FujinTerm.Models.Profile;

// Persisted layout of the Session Stats window's panels — the user's chosen
// top-to-bottom order and which panels they've hidden. One layout per character
// profile, stored on CharacterProfile.SessionStatsLayout and applied by
// Services.SessionStatsLayoutStore when the window opens.
//
// Panels are identified by stable string ids (see
// Services.SessionStatsLayoutStore.DefaultOrder) rather than an enum so a saved
// profile survives panels being added or removed: unknown ids in Order / Hidden
// are ignored on load and panels the save predates are appended in their
// default position. null on either list means "nothing customised" — the window
// falls back to the default order with every panel visible.
public sealed class SessionStatsLayout
{
    // Panel ids in the user's chosen top-to-bottom order.
    public List<string>? Order { get; set; }

    // Panel ids the user has toggled hidden via the context menu.
    public List<string>? Hidden { get; set; }
}
