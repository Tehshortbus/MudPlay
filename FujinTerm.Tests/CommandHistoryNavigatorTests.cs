using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the Up / Down recall cursor: Previous walks older, Next walks back
/// toward the newest, the in-progress line is stashed on the first Up and
/// restored when Down passes the newest entry, ends clamp, and Reset
/// returns to the live line.
/// </summary>
public sealed class CommandHistoryNavigatorTests
{
    private static (CommandHistory hist, CommandHistoryNavigator nav) Setup(params string[] cmds)
    {
        CommandHistory h = new();
        foreach (string c in cmds) h.Record(c);
        return (h, new CommandHistoryNavigator(h));
    }

    [Fact]
    public void Previous_OnEmptyHistory_ReturnsNull()
    {
        var (_, nav) = Setup();
        Assert.Null(nav.Previous("typing"));
    }

    [Fact]
    public void Next_WhenNotBrowsing_ReturnsNull()
    {
        var (_, nav) = Setup("a", "b");
        Assert.Null(nav.Next());
    }

    [Fact]
    public void Previous_StepsFromNewestToOldest_ThenClamps()
    {
        var (_, nav) = Setup("one", "two", "three");
        Assert.Equal("three", nav.Previous(""));   // newest first
        Assert.Equal("two", nav.Previous(""));
        Assert.Equal("one", nav.Previous(""));     // oldest
        Assert.Null(nav.Previous(""));             // clamp at oldest
    }

    [Fact]
    public void Next_WalksBackTowardNewest_ThenRestoresStash()
    {
        var (_, nav) = Setup("one", "two", "three");
        nav.Previous("draft");   // -> three  (stashes "draft")
        nav.Previous("draft");   // -> two
        nav.Previous("draft");   // -> one
        Assert.Equal("two", nav.Next());
        Assert.Equal("three", nav.Next());
        Assert.Equal("draft", nav.Next());   // past newest -> stashed live line
        Assert.Null(nav.Next());             // back on the live line
    }

    [Fact]
    public void DownImmediatelyAfterFirstUp_RestoresInProgressLine()
    {
        // Type "hel", Up to peek the newest, Down to get "hel" back.
        var (_, nav) = Setup("look", "north");
        Assert.Equal("north", nav.Previous("hel"));
        Assert.Equal("hel", nav.Next());
    }

    [Fact]
    public void Reset_DropsBackToLiveLine_AndRestashesOnNextUp()
    {
        var (_, nav) = Setup("one", "two");
        nav.Previous("first");      // -> two (stash "first")
        nav.Reset();
        Assert.Null(nav.Next());    // no longer browsing
        // Next Up re-stashes the current text afresh.
        Assert.Equal("two", nav.Previous("second"));
        Assert.Equal("second", nav.Next());
    }

    [Fact]
    public void HistoryChangingMidBrowse_StaysSafe()
    {
        // Browse to the oldest slot, then more commands land under the
        // cursor: navigation must stay in range and walk forward cleanly
        // rather than throw.
        CommandHistory h = new();
        h.Record("a");
        CommandHistoryNavigator nav = new(h);
        nav.Previous("");          // -> "a" (oldest slot)
        h.Record("b");
        h.Record("c");
        Assert.Null(nav.Previous(""));         // nothing older than the parked slot
        Assert.Equal("b", nav.Next());         // steps forward through the longer list
        Assert.Equal("c", nav.Next());
    }
}
