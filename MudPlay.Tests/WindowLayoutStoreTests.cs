using Avalonia;
using Avalonia.Controls;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The pure half of WindowLayoutStore: which geometry is worth remembering.
// The live plumbing (Opened / Closing / Closed, the restore onto a real
// monitor layout) is Avalonia UI and isn't unit-tested per the project rule —
// see WindowSnapManagerTests.
public sealed class WindowLayoutStoreTests
{
    [Fact]
    public void BoundsToPersist_NormalWindow_RecordsPositionAndSize()
    {
        WindowBounds? b = WindowLayoutStore.BoundsToPersist(
            WindowState.Normal, new PixelPoint(1413, 220), 725, 580);

        Assert.NotNull(b);
        Assert.Equal(1413, b!.X);
        Assert.Equal(220, b.Y);
        Assert.Equal(725, b.Width);
        Assert.Equal(580, b.Height);
        Assert.False(b.Maximized);
    }

    [Fact]
    public void BoundsToPersist_Minimized_KeepsTheLastGoodBounds()
    {
        // Windows parks minimized windows at ~(-32000,-32000); capturing that
        // would overwrite "where I left it" with a position the restore then
        // judges off-screen and re-anchors. Report paradigm-20260827-081318.
        Assert.Null(WindowLayoutStore.BoundsToPersist(
            WindowState.Minimized, new PixelPoint(-32000, -32000), 725, 580));
    }

    [Fact]
    public void BoundsToPersist_CollapsingSize_IsRejected()
    {
        // Transient measurements during teardown, not the user's layout.
        Assert.Null(WindowLayoutStore.BoundsToPersist(
            WindowState.Normal, new PixelPoint(100, 100), 0, 0));
        Assert.Null(WindowLayoutStore.BoundsToPersist(
            WindowState.Normal, new PixelPoint(100, 100), 79, 580));
        Assert.Null(WindowLayoutStore.BoundsToPersist(
            WindowState.Normal, new PixelPoint(100, 100), 725, 59));
    }

    // A content-sized window's unset Width / Height is NaN; stored, it made every
    // later profile save throw (report Crash-20261031-145330).
    [Theory]
    [InlineData(double.NaN, 580)]
    [InlineData(725, double.NaN)]
    [InlineData(double.PositiveInfinity, 580)]
    public void BoundsToPersist_ASizeThatIsNoNumber_IsRejected(double width, double height)
    {
        Assert.Null(WindowLayoutStore.BoundsToPersist(
            WindowState.Normal, new PixelPoint(100, 100), width, height));
    }

    [Fact]
    public void BoundsToPersist_Maximized_KeepsTheNormalSizeAndFlagsIt()
    {
        // The window reports its maximized frame; the size to restore it TO is
        // the one already stored, written by an earlier capture taken Normal.
        // Recording 1920x1080 as the normal size left the window screen-sized
        // after un-maximizing.
        WindowBounds previous = new()
        {
            X = 1413, Y = 220, Width = 725, Height = 580, Maximized = false,
        };

        WindowBounds? b = WindowLayoutStore.BoundsToPersist(
            WindowState.Maximized, new PixelPoint(-8, -8), 1936, 1096, previous);

        Assert.NotNull(b);
        Assert.True(b!.Maximized);
        Assert.Equal(1413, b.X);
        Assert.Equal(220, b.Y);
        Assert.Equal(725, b.Width);
        Assert.Equal(580, b.Height);
    }

    [Fact]
    public void BoundsToPersist_MaximizedWithNothingRemembered_FallsBackToTheFrame()
    {
        // Maximized before this window was ever captured Normal: the maximized
        // frame is all there is, and the next Normal capture replaces it.
        WindowBounds? b = WindowLayoutStore.BoundsToPersist(
            WindowState.Maximized, new PixelPoint(0, 0), 1920, 1080);

        Assert.NotNull(b);
        Assert.True(b!.Maximized);
        Assert.Equal(1920, b.Width);
        Assert.Equal(1080, b.Height);
    }

    [Fact]
    public void BoundsToPersist_BackToNormal_OverwritesTheStoredGeometry()
    {
        // Un-maximize, resize, quit: the live bounds win over what was kept
        // through the maximized spell, and the flag clears.
        WindowBounds previous = new()
        {
            X = 1413, Y = 220, Width = 725, Height = 580, Maximized = true,
        };

        WindowBounds? b = WindowLayoutStore.BoundsToPersist(
            WindowState.Normal, new PixelPoint(300, 120), 900, 640, previous);

        Assert.NotNull(b);
        Assert.False(b!.Maximized);
        Assert.Equal(300, b.X);
        Assert.Equal(900, b.Width);
    }

    [Fact]
    public void BoundsToPersist_MinimizedFromMaximized_StillKeepsEverything()
    {
        // Minimize wins over the maximized arm: nothing is captured, so the
        // stored geometry AND the maximized flag both survive a Win+D.
        Assert.Null(WindowLayoutStore.BoundsToPersist(
            WindowState.Minimized, new PixelPoint(-32000, -32000), 1920, 1080,
            new WindowBounds { X = 10, Y = 10, Width = 725, Height = 580, Maximized = true }));
    }
}
