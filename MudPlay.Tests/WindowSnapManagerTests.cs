using System.Collections.Generic;
using Avalonia;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The pure snap geometry behind WindowSnapManager — edge-snap search, flush-adjacency,
// and the cluster BFS. The live window plumbing (PositionChanged, cluster drag) is
// Avalonia UI and isn't unit-tested per the project rule.
public sealed class WindowSnapManagerTests
{
    private const int Threshold = WindowSnapManager.SnapThreshold; // 12
    // The gap within which two edges count as touching, for these tests.
    private const int Tol = 4;

    [Fact]
    public void ComputeSnap_RightEdgeNearLeftEdge_SnapsXFlush()
    {
        PixelRect c = new(0, 0, 100, 100);
        PixelRect v = new(110, 0, 100, 100);   // 10px gap, vertical overlap

        (int axis, int shift) = WindowSnapManager.ComputeSnap(c, new[] { v }, Threshold);

        Assert.Equal(1, axis);      // adjust X
        Assert.Equal(10, shift);    // move right so c.Right meets v.Left
    }

    // A full-height main window's bottom is the taskbar's top: snapping a panel
    // below it put the panel behind the taskbar, where it could hardly be grabbed.
    [Fact]
    public void ComputeSnap_NeverPutsTheTitleBarOffTheWorkingArea()
    {
        PixelRect work = new(0, 0, 1920, 1040);
        PixelRect main = new(0, 0, 900, 1040);
        PixelRect nav = new(100, 1030, 600, 400);   // top 10px above main's bottom

        (int axis, _) = WindowSnapManager.ComputeSnap(nav, new[] { main }, Threshold, new[] { work });

        Assert.Equal(0, axis);
    }

    [Fact]
    public void ComputeSnap_AnEdgeInsideTheWorkingArea_StillSnaps()
    {
        PixelRect work = new(0, 0, 1920, 1040);
        PixelRect main = new(0, 0, 900, 600);
        PixelRect nav = new(100, 608, 600, 400);

        (int axis, int shift) = WindowSnapManager.ComputeSnap(nav, new[] { main }, Threshold, new[] { work });

        Assert.Equal(2, axis);
        Assert.Equal(-8, shift);
    }

    [Fact]
    public void ComputeSnap_BottomEdgeNearTopEdge_SnapsY()
    {
        PixelRect c = new(0, 0, 100, 100);
        PixelRect v = new(0, 110, 100, 100);   // 10px below, horizontal overlap

        (int axis, int shift) = WindowSnapManager.ComputeSnap(c, new[] { v }, Threshold);

        Assert.Equal(2, axis);      // adjust Y
        Assert.Equal(10, shift);
    }

    [Fact]
    public void ComputeSnap_BeyondThreshold_DoesNotSnap()
    {
        PixelRect c = new(0, 0, 100, 100);
        PixelRect v = new(120, 0, 100, 100);   // 20px gap > 12

        Assert.Equal((0, 0), WindowSnapManager.ComputeSnap(c, new[] { v }, Threshold));
    }

    [Fact]
    public void ComputeSnap_NoPerpendicularOverlap_DoesNotSnap()
    {
        // Diagonally offset — near on X but no vertical overlap, so no adjacency snap.
        PixelRect c = new(0, 0, 100, 100);
        PixelRect v = new(110, 200, 100, 100);

        Assert.Equal((0, 0), WindowSnapManager.ComputeSnap(c, new[] { v }, Threshold));
    }

    [Fact]
    public void ComputeSnap_PicksTheClosestEdge()
    {
        PixelRect c = new(0, 0, 100, 100);
        PixelRect far = new(108, 0, 100, 100);    // 8px
        PixelRect near = new(0, 103, 100, 100);   // 3px below

        (int axis, int shift) = WindowSnapManager.ComputeSnap(c, new[] { far, near }, Threshold);

        Assert.Equal(2, axis);      // the 3px vertical snap beats the 8px horizontal one
        Assert.Equal(3, shift);
    }

    [Fact]
    public void Adjacent_FlushEdges_True()
        => Assert.True(WindowSnapManager.Adjacent(
            new PixelRect(0, 0, 100, 100), new PixelRect(100, 0, 100, 100), Tol));

    [Fact]
    public void Adjacent_Overlapping_False()
        => Assert.False(WindowSnapManager.Adjacent(
            new PixelRect(0, 0, 100, 100), new PixelRect(50, 0, 100, 100), Tol));

    [Fact]
    public void Adjacent_TouchingWithoutPerpendicularOverlap_False()
        => Assert.False(WindowSnapManager.Adjacent(
            new PixelRect(0, 0, 100, 100), new PixelRect(100, 200, 100, 100), Tol));

    [Fact]
    public void ConnectedFrom_TransitiveChain_ReachesAll_ExcludesDetached()
    {
        Dictionary<string, PixelRect> rects = new()
        {
            ["main"] = new(0, 0, 100, 100),
            ["a"]    = new(100, 0, 100, 100),   // right of main
            ["b"]    = new(200, 0, 100, 100),   // right of a
            ["c"]    = new(600, 600, 100, 100), // detached
        };

        HashSet<string> cluster = WindowSnapManager.ConnectedFrom("main", rects, Tol);

        Assert.Contains("main", cluster);
        Assert.Contains("a", cluster);
        Assert.Contains("b", cluster);   // transitive through a
        Assert.DoesNotContain("c", cluster);
    }
}
