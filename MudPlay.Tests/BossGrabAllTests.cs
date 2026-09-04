using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The Grab-All-on-boss-death command builder + the BossDef flag's persistence
// round-trip (Clone carries it; MatchesSeed breaks so an overlay-only toggle saves).
public sealed class BossGrabAllTests
{
    private static IReadOnlyList<MonsterDropSlot> Drops(params int[] ids)
        => ids.Select(id => new MonsterDropSlot(id, 50)).ToList();

    [Fact]
    public void NullDrops_YieldNoCommands()
        => Assert.Empty(BossGrabAllCommands.Build(null, _ => "x"));

    [Fact]
    public void OneGetPerDistinctItem_ByName_InSlotOrder()
    {
        IReadOnlyList<string> cmds = BossGrabAllCommands.Build(
            Drops(10, 20), id => id == 10 ? "gold crown" : "ruby ring");
        Assert.Equal(new[] { "get gold crown", "get ruby ring" }, cmds);
    }

    [Fact]
    public void DedupesRepeatedItemIds()
    {
        IReadOnlyList<string> cmds = BossGrabAllCommands.Build(
            Drops(10, 10, 20), id => id == 10 ? "crown" : "ring");
        Assert.Equal(new[] { "get crown", "get ring" }, cmds);
    }

    [Fact]
    public void SkipsZeroIdsAndUnresolvableNames()
    {
        // id 0 is an empty drop slot; id 30 has no name in the active set.
        IReadOnlyList<string> cmds = BossGrabAllCommands.Build(
            Drops(0, 10, 30), id => id == 10 ? "crown" : null);
        Assert.Equal(new[] { "get crown" }, cmds);
    }

    [Fact]
    public void GrabAll_ClonesThrough_AndTogglesSeedMatch()
    {
        var seed = new BossDef { Name = "Foo" };            // GrabAll defaults false
        BossDef edited = seed.Clone();
        edited.GrabAll = true;

        Assert.True(edited.Clone().GrabAll);                // Clone carries the flag
        Assert.False(edited.MatchesSeed(seed));             // differs from seed → overlay persists

        edited.GrabAll = false;
        Assert.True(edited.MatchesSeed(seed));              // back to default → dropped from overlay
    }

    [Theory]
    // hasMonsterNumber, isMonster, isItem  → expected kind
    [InlineData(true, false, false, BossGrabKind.Monster)]   // number alone = monster
    [InlineData(false, true, false, BossGrabKind.Monster)]   // monster name
    [InlineData(true, false, true, BossGrabKind.Monster)]    // monster wins over item
    [InlineData(false, true, true, BossGrabKind.Monster)]    // monster wins over item
    [InlineData(false, false, true, BossGrabKind.Item)]      // item only
    [InlineData(false, false, false, BossGrabKind.None)]     // unresolvable (Iceforge)
    public void ClassifyKind_PrioritisesMonster_ThenItem_ElseNone(
        bool hasNumber, bool isMonster, bool isItem, BossGrabKind expected)
        => Assert.Equal(expected, BossGrabAllCommands.ClassifyKind(hasNumber, isMonster, isItem));

    [Fact]
    public void NameCandidates_DropsLeadingArticle()
    {
        Assert.Equal(new[] { "the bogwood box", "bogwood box" },
            BossGrabClassifier.NameCandidates("the bogwood box"));
        Assert.Equal(new[] { "Pastor Landor's box" },
            BossGrabClassifier.NameCandidates("  Pastor Landor's box  "));   // trimmed, no article
        Assert.Empty(BossGrabClassifier.NameCandidates("   "));
    }
}
