using FujinTerm.Game.Map;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pure fold of leader + member level estimates into the party's
/// most-constraining (Low, High) window. Drives
/// <see cref="Services.MovementFilter.IsExitBlocked"/>'s party branch,
/// which routes a following party around gates a member can't clear.
/// </summary>
public sealed class PartyLevelBoundsTests
{
    private static PartyLevelEstimate Exact(int level) => new(level, null);
    private static PartyLevelEstimate Title(int min, int max) => new(null, (min, max));
    private static readonly PartyLevelEstimate Unknown = new(null, null);

    [Fact]
    public void NoSelf_NoMembers_ReturnsNull()
    {
        Assert.Null(PartyLevelBounds.Compute(selfLevel: null, System.Array.Empty<PartyLevelEstimate>()));
    }

    [Fact]
    public void SelfOnly_WindowIsSelfExact()
    {
        Assert.Equal((40, 40), PartyLevelBounds.Compute(40, System.Array.Empty<PartyLevelEstimate>()));
    }

    [Fact]
    public void SelfNonPositive_Skipped()
    {
        // A zero/negative self level carries no information — skip it.
        Assert.Null(PartyLevelBounds.Compute(0, System.Array.Empty<PartyLevelEstimate>()));
    }

    [Fact]
    public void ExactMembers_FoldToMinLowMaxHigh()
    {
        var r = PartyLevelBounds.Compute(40, new[] { Exact(30), Exact(50) });
        Assert.Equal((30, 50), r);   // low = min(40,30,50), high = max(40,30,50)
    }

    [Fact]
    public void TitleMember_UsesBandFloorAndCap()
    {
        // Leader 40, one title-only member banded 10..14 → widen both ends.
        var r = PartyLevelBounds.Compute(40, new[] { Title(10, 14) });
        Assert.Equal((10, 40), r);
    }

    [Fact]
    public void ExactPreferredOverTitle_OnSameEstimate()
    {
        // Exact present → title range ignored for that member.
        var r = PartyLevelBounds.Compute(null, new[] { new PartyLevelEstimate(25, (1, 99)) });
        Assert.Equal((25, 25), r);
    }

    [Fact]
    public void UnknownMembers_Skipped_NotOverBlocking()
    {
        // A member we know nothing about must not widen the window — else
        // we'd route around gates we've no reason to avoid.
        var r = PartyLevelBounds.Compute(40, new[] { Unknown, Exact(38) });
        Assert.Equal((38, 40), r);
    }

    [Fact]
    public void AllUnknown_NoSelf_ReturnsNull()
    {
        // Nothing known about anyone → null, so the filter falls back to
        // self-only evaluation instead of the party branch.
        Assert.Null(PartyLevelBounds.Compute(null, new[] { Unknown, Unknown }));
    }

    [Fact]
    public void MixedExactAndTitle_TakesTheExtremes()
    {
        // low from the title floor (5), high from the exact leader (60).
        var r = PartyLevelBounds.Compute(60, new[] { Title(5, 9), Exact(45) });
        Assert.Equal((5, 60), r);
    }
}
