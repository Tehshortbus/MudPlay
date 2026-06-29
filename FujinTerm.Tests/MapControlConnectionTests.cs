using FujinTerm.Controls;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Covers the adjacent / gap-bridge / stub decision in
/// <see cref="MapControl.ClassifyConnection"/>. Gap-bridges replace the
/// old "stub into empty space" for exits whose two rooms are connected
/// but couldn't be placed on grid-adjacent cells (non-tiling diagonals in
/// hand-built forest + sewer maps, e.g. map-1 sewers 603↔604 and
/// Darkwood Forest 3178↔3179).
/// </summary>
public sealed class MapControlConnectionTests
{
    private const int Cap = 4;

    [Fact]
    public void UnplacedTarget_IsStub()
    {
        // Dropped / blacklisted target — actual is meaningless, nothing
        // to connect to, so a half-stub is all we can draw.
        var kind = MapControl.ClassifyConnection(
            targetPlaced: false, source: (0, 0), expected: (1, 0), actual: (0, 0), Cap);
        Assert.Equal(MapControl.ConnectionKind.Stub, kind);
    }

    [Fact]
    public void TargetOnExpectedCell_IsAdjacent()
    {
        var kind = MapControl.ClassifyConnection(
            targetPlaced: true, source: (0, 0), expected: (1, 0), actual: (1, 0), Cap);
        Assert.Equal(MapControl.ConnectionKind.Adjacent, kind);
    }

    [Fact]
    public void TargetOffByOneCell_IsBridge()
    {
        // The sewers 603→604 case: exit is W (expected (-1,0) relative)
        // but the partner landed one row down — connected, not adjacent.
        var kind = MapControl.ClassifyConnection(
            targetPlaced: true, source: (0, 0), expected: (-1, 0), actual: (-1, 1), Cap);
        Assert.Equal(MapControl.ConnectionKind.Bridge, kind);
    }

    [Fact]
    public void TargetWithinCap_IsBridge()
    {
        var kind = MapControl.ClassifyConnection(
            targetPlaced: true, source: (0, 0), expected: (1, 1), actual: (4, 3), Cap);
        Assert.Equal(MapControl.ConnectionKind.Bridge, kind);   // Chebyshev 4 == cap
    }

    [Fact]
    public void TargetBeyondCap_FallsBackToStub()
    {
        // A line across the whole map would cross other rooms — stub
        // instead so the render stays legible.
        var kind = MapControl.ClassifyConnection(
            targetPlaced: true, source: (0, 0), expected: (1, 0), actual: (7, 2), Cap);
        Assert.Equal(MapControl.ConnectionKind.Stub, kind);     // Chebyshev 7 > cap
    }
}
