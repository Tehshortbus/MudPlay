namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character "Display" settings — terminal font size and scrollback
/// ring depth. Stored under the <c>"Display"</c> key in
/// <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// Live-applies <see cref="FontSize"/> to the terminal canvas via
/// <see cref="Services.DisplayConfig"/>. <see cref="ScrollbackLines"/>
/// applies on next launch — the <see cref="Terminal.ScrollbackBuffer"/>
/// is allocated once at startup; in-place resize would need to copy /
/// drop rows and is intentionally deferred.
/// </remarks>
public sealed class DisplaySettings
{
    /// <summary>Terminal canvas font size in points.</summary>
    public double FontSize { get; set; } = 16.0;

    /// <summary>How many scrolled-off rows the backscroll ring retains.</summary>
    public int ScrollbackLines { get; set; } = 10_000;
}
