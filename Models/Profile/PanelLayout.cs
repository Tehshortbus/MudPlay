namespace FujinTerm.Models.Profile;

/// <summary>
/// Visibility / docking state for a single floating panel. Persisted as part
/// of <see cref="CharacterProfile.PanelLayouts"/>.
/// </summary>
public enum PanelState
{
    /// <summary>Panel is not visible — neither docked nor floating.</summary>
    Hidden,

    /// <summary>Panel content lives inside the main window's dock container.</summary>
    Docked,

    /// <summary>Panel content lives in a separate top-level <c>Window</c> owned by the main window.</summary>
    Floating,
}

/// <summary>
/// Persisted bounds + state for a single panel managed by
/// <see cref="Services.FloatingPanelHost"/>. One layout per panel per
/// character profile.
/// </summary>
/// <remarks>
/// X / Y / Width / Height apply when the panel is <see cref="PanelState.Floating"/>.
/// Z is the front-to-back ordering hint among floating panels (lowest = back).
/// All fields default to 0; the host treats 0-sized floats as "auto" and asks
/// the WM to place the window.
/// </remarks>
public sealed class PanelLayout
{
    public PanelState State { get; set; } = PanelState.Docked;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public int Z { get; set; }
}
