namespace FujinTerm.Models.Profile;

/// <summary>
/// Persisted layout of the Session Stats window's panels — the user's chosen
/// top-to-bottom order and which panels they've hidden. One layout per character
/// profile, stored on <see cref="CharacterProfile.SessionStatsLayout"/> and
/// applied by <see cref="Services.SessionStatsLayoutStore"/> when the window opens.
/// </summary>
/// <remarks>
/// Panels are identified by stable string ids (see
/// <see cref="Services.SessionStatsLayoutStore.DefaultOrder"/>) rather than an
/// enum so a saved profile survives panels being added or removed: unknown ids
/// in <see cref="Order"/> / <see cref="Hidden"/> are ignored on load and panels
/// the save predates are appended in their default position. <c>null</c> on
/// either list means "nothing customised" — the window falls back to the default
/// order with every panel visible.
/// </remarks>
public sealed class SessionStatsLayout
{
    /// <summary>Panel ids in the user's chosen top-to-bottom order.</summary>
    public List<string>? Order { get; set; }

    /// <summary>Panel ids the user has toggled hidden via the context menu.</summary>
    public List<string>? Hidden { get; set; }
}
