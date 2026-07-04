namespace FujinTerm.Models.Profile;

// Visibility / docking state for a single floating panel. Persisted as part of
// CharacterProfile.PanelLayouts.
public enum PanelState
{
    // Panel is not visible — neither docked nor floating.
    Hidden,

    // Panel content lives inside the main window's dock container.
    Docked,

    // Panel content lives in a separate top-level Window owned by the main window.
    Floating,
}

// Persisted bounds + state for a single panel managed by
// Services.FloatingPanelHost. One layout per panel per character profile.
//
// X / Y / Width / Height apply when the panel is Floating. Z is the
// front-to-back ordering hint among floating panels (lowest = back). All fields
// default to 0; the host treats 0-sized floats as "auto" and asks the WM to
// place the window.
public sealed class PanelLayout
{
    public PanelState State { get; set; } = PanelState.Docked;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public int Z { get; set; }
}
