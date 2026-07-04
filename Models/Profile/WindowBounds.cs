namespace FujinTerm.Models.Profile;

// Persisted size + screen position for one top-level window. Lives in
// CharacterProfile.WindowBounds keyed by a stable window id ("main",
// "backscroll", etc.).
//
// All values are device-independent pixels. X / Y are the window's Position in
// screen coordinates. If the saved position would land off-screen on the
// current monitor layout the window manager clamps it on Show; we don't
// second-guess.
public sealed class WindowBounds
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}
