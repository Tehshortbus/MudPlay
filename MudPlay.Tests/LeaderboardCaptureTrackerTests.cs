using System.Text;
using MudPlay.Game.Leaderboard;
using MudPlay.Services;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// LeaderboardCaptureTracker: the state machine that stitches a live "top N" block
// off LineExtractor.LineEmitted and hands the finished snapshot to the per-BBS
// store. Drives the tracker through the client's input path: the game's rows fed
// through a terminal and the line extractor the tracker is attached to.
public sealed class LeaderboardCaptureTrackerTests
{
    private static readonly DateTimeOffset T = new(2026, 7, 20, 20, 3, 25, TimeSpan.Zero);
    private const int NameCol = 5, ClassCol = 27, GuildCol = 38, ExpCol = 58;

    private static string BuildHeader()
    {
        var sb = new StringBuilder();
        Place(sb, "Rank", 0);
        Place(sb, "Name", NameCol);
        Place(sb, "Class", ClassCol);
        Place(sb, "Gang/Guild", GuildCol);
        Place(sb, "Experience", ExpCol);
        return sb.ToString();
    }

    private static string Row(int rank, string name, string cls, string guild, string exp)
    {
        var sb = new StringBuilder();
        sb.Append((rank.ToString() + ".").PadLeft(4));
        Place(sb, name, NameCol);
        Place(sb, cls, ClassCol);
        Place(sb, guild, GuildCol);
        Place(sb, exp, ExpCol);
        return sb.ToString();
    }

    private static void Place(StringBuilder sb, string value, int start)
    {
        while (sb.Length < start) sb.Append(' ');
        sb.Append(value);
    }

    private static (LeaderboardCaptureTracker tracker, LeaderboardSnapshotStore store, ServerFeed feed) Setup()
    {
        var store = new LeaderboardSnapshotStore(); // no BBS pinned → in-memory, Persist no-ops
        var tracker = new LeaderboardCaptureTracker(store);
        ServerFeed feed = new(clock: () => T);
        tracker.AttachLineExtractor(feed.Lines);
        return (tracker, store, feed);
    }

    [Fact]
    public void CapturesBlock_TerminatedByPrompt_WithRequestedCount()
    {
        var (_, store, feed) = Setup();

        feed.Line("top 100");                 // echoed request → RequestedCount
        feed.Line("Top Heroes of the Realm"); // ignored while idle
        feed.Line(BuildHeader());             // begins collecting
        feed.Line("=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-"); // underline before rows — skipped
        feed.Line(Row(1, "Salty Exp", "Bard", "Droppin' Loads", "1529225118"));
        feed.Line(Row(2, "Shooting Loads", "Ranger", "what happen", "1522027759"));
        feed.Line("[HP=91/KAI=10]:"); // terminates block

        Assert.Single(store.Snapshots);
        LeaderboardSnapshot snap = store.Snapshots[0];
        Assert.Equal(100, snap.RequestedCount);
        Assert.Equal(2, snap.Entries.Count);
        Assert.Equal("Salty Exp", snap.Entries[0].Name);
        Assert.Equal("Shooting Loads", snap.Entries[1].Name);
        Assert.Equal(T, snap.CapturedAtUtc);
    }

    [Fact]
    public void CapturesBlock_TerminatedByNonRowLine()
    {
        var (_, store, feed) = Setup();

        feed.Line("top 50");
        feed.Line(BuildHeader());
        feed.Line(Row(1, "Salty Exp", "Bard", "Droppin' Loads", "1529225118"));
        feed.Line("General Store"); // non-row content ends the block once rows are in

        Assert.Single(store.Snapshots);
        Assert.Equal(50, store.Snapshots[0].RequestedCount);
        Assert.Single(store.Snapshots[0].Entries);
    }

    [Fact]
    public void TruncatedRequest_MarksSnapshotComplete()
    {
        var (_, store, feed) = Setup();

        // Asked for 200, realm returned 1 → whole pool shown (IsComplete).
        feed.Line("top 200");
        feed.Line(BuildHeader());
        feed.Line(Row(1, "Salty Exp", "Bard", "Droppin' Loads", "1529225118"));
        feed.Line("[HP=91/KAI=10]:");

        Assert.Single(store.Snapshots);
        Assert.Equal(200, store.Snapshots[0].RequestedCount);
        Assert.True(store.Snapshots[0].IsComplete);
    }

    [Fact]
    public void NoHeader_NothingCaptured()
    {
        var (_, store, feed) = Setup();

        feed.Line("You say, 'top of the morning'");
        feed.Line("[HP=91/KAI=10]:");

        Assert.Empty(store.Snapshots);
    }
}
