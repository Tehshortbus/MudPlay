namespace FujinTerm.Models.Profile;

/// <summary>
/// Persisted size + screen position for one top-level window.
/// Lives in <see cref="CharacterProfile.WindowBounds"/> keyed by a
/// stable window id ("main", "backscroll", etc.).
/// </summary>
/// <remarks>
/// All values are device-independent pixels. <see cref="X"/> / <see cref="Y"/>
/// are the window's <c>Position</c> in screen coordinates. If the saved
/// position would land off-screen on the current monitor layout the
/// window manager clamps it on Show; we don't second-guess.
/// </remarks>
public sealed class WindowBounds
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}
