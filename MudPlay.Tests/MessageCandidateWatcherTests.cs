using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

public sealed class MessageCandidateWatcherTests
{
    private sealed class Harness
    {
        public LogService Log { get; } = new();
        public MessageRouter Router { get; } = new();
        public MessageStore Messages { get; } = new();
        public MessageCandidateStore Candidates { get; } = new();
        public MessageCandidateWatcher Watcher { get; }

        // Mutable so a test can point the watcher at a known room before feeding.
        public RoomKey? Room { get; set; }

        public Harness()
        {
            Watcher = new MessageCandidateWatcher(
                Router, Messages, Candidates, currentRoom: () => Room, log: Log);
        }

        // The watcher subscribes to LineExtractor in real life; tests reflect into
        // the private OnLine directly instead of standing up a fake extractor —
        // same pattern ConditionTrackerTests uses for the identical shape.
        public void Feed(string text, DateTimeOffset? when = null)
        {
            var emitted = new LineExtractor.EmittedLine(
                text, Array.Empty<CellAttributes>(),
                when ?? DateTimeOffset.UtcNow, IsPromptLine: false);
            typeof(MessageCandidateWatcher)
                .GetMethod("OnLine",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .Invoke(Watcher, new object[] { emitted });
        }
    }

    private static MessageRecord MakeRecord(string casterMessage) => new(
        Id: MessageRecord.ComputeId("Test", casterMessage, "", "", "", ""),
        Name: "Test",
        Flags: MessageFlags.None,
        RawFlagsHex: 0,
        CasterMessage: casterMessage,
        TargetMessage: string.Empty,
        WitnessMessage: string.Empty,
        AppliedMessage: string.Empty,
        AppliedEndsWith: string.Empty);

    [Fact]
    public void KnownMessageLine_DoesNotCreateCandidate()
    {
        Harness h = new();
        h.Messages.Messages.Add(MakeRecord("You feel a surge of power!"));

        h.Feed("You feel a surge of power!");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void ConfuseFumbleLine_DoesNotCreateCandidate()
    {
        // A recognized fumble line reaches the app via a predicate, not a router
        // pattern — the watcher must still exclude it (indexed from the record's
        // ConfuseFumbleLine slot), or it'd be falsely staged as unrecognized.
        Harness h = new();
        h.Messages.Messages.Add(new MessageRecord(
            Id: MessageRecord.ComputeId("Convulsions", "", "", "", "", ""),
            Name: "Convulsions",
            Flags: MessageFlags.Confused,
            RawFlagsHex: 0,
            CasterMessage: string.Empty,
            TargetMessage: string.Empty,
            WitnessMessage: string.Empty,
            AppliedMessage: string.Empty,
            AppliedEndsWith: string.Empty,
            Links: null,
            ConfuseFumbleLine: "You look around stupidly."));

        h.Feed("You look around stupidly.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void RouterMatchedLine_DoesNotCreateCandidate()
    {
        Harness h = new();
        h.Router.RegisterPattern(new PrefixPattern("test.gossip", "*GOSSIP* "));

        h.Feed("*GOSSIP* Forged: hello");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void GenuinelyNewLine_CreatesCandidate_AndWarnsOnce()
    {
        Harness h = new();
        int warnCount = 0;
        h.Log.EntryAdded += e => { if (e.Severity == LogSeverity.Warn) warnCount++; };

        h.Feed("A shimmering aura surrounds you!");

        Assert.Single(h.Candidates.Candidates);
        Assert.Equal(1, h.Candidates.Candidates[0].Occurrences);
        Assert.Equal(1, warnCount);
    }

    [Fact]
    public void NewLine_TagsCandidateWithCurrentRoom()
    {
        Harness h = new();
        h.Room = new RoomKey(12, 3456);

        h.Feed("A shimmering aura surrounds you!");

        MessageCandidateRecord c = Assert.Single(h.Candidates.Candidates);
        Assert.Equal(12, c.Map);
        Assert.Equal(3456, c.Room);
    }

    [Fact]
    public void NewLine_WithoutKnownRoom_LeavesLocationNull()
    {
        Harness h = new();   // Room stays null

        h.Feed("A shimmering aura surrounds you!");

        MessageCandidateRecord c = Assert.Single(h.Candidates.Candidates);
        Assert.Null(c.Map);
        Assert.Null(c.Room);
    }

    [Fact]
    public void RepeatedLine_BumpsOccurrences_WarnsOnlyOnce()
    {
        Harness h = new();
        int warnCount = 0;
        h.Log.EntryAdded += e => { if (e.Severity == LogSeverity.Warn) warnCount++; };

        h.Feed("A shimmering aura surrounds you!");
        h.Feed("A shimmering aura surrounds you!");
        h.Feed("A shimmering aura surrounds you!");

        Assert.Single(h.Candidates.Candidates);
        Assert.Equal(3, h.Candidates.Candidates[0].Occurrences);
        Assert.Equal(1, warnCount);
    }

    [Fact]
    public void SimulateCapture_StagesAFreshCandidateEachCall()
    {
        Harness h = new();

        string first = h.Watcher.SimulateCapture();
        string second = h.Watcher.SimulateCapture();

        Assert.NotEqual(first, second);   // varies per call → distinct candidates
        Assert.Equal(2, h.Candidates.Candidates.Count);
        Assert.Contains(h.Candidates.Candidates, c => c.RawText == first);
    }

    [Fact]
    public void SimulateCapture_RespectsDisabledGate()
    {
        Harness h = new();
        h.Watcher.Enabled = false;

        h.Watcher.SimulateCapture();

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void DismissedLine_IsIgnoredOnRecurrence()
    {
        Harness h = new();
        h.Feed("A shimmering aura surrounds you!");         // stages candidate (occ 1)
        MessageCandidateRecord c = Assert.Single(h.Candidates.Candidates);
        h.Candidates.Dismiss(c.Id);

        int warnAfter = 0;
        h.Log.EntryAdded += e => { if (e.Severity == LogSeverity.Warn) warnAfter++; };
        h.Feed("A shimmering aura surrounds you!");         // recurrence of a dismissed line

        Assert.Single(h.Candidates.Candidates);             // no duplicate
        Assert.Equal(1, h.Candidates.Candidates[0].Occurrences);  // not bumped
        Assert.Equal(0, warnAfter);                         // no re-alert
    }

    [Fact]
    public void DisabledWatcher_NeverCreatesCandidates()
    {
        Harness h = new();
        h.Watcher.Enabled = false;

        h.Feed("Whatever this is, it should be ignored.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void BurstOfDistinctUnrecognizedLines_CapsAtBurstLimit()
    {
        // BurstCap = 6, BurstWindow = 1500ms: 10 distinct never-seen lines
        // arriving within the window should stage only the first 6.
        Harness h = new();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        for (int i = 0; i < 10; i++)
            h.Feed($"Distinct never-seen line #{i}", t0.AddMilliseconds(i * 50));

        Assert.Equal(6, h.Candidates.Candidates.Count);
    }

    [Fact]
    public void BurstAcrossTwoWindows_BothGroupsStageNormally()
    {
        // Two separate bursts of 5 (under the cap), well apart in time, should
        // each stage in full — the window resets rather than accumulating
        // across the gap.
        Harness h = new();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        for (int i = 0; i < 5; i++)
            h.Feed($"Group A line #{i}", t0.AddMilliseconds(i * 50));

        DateTimeOffset t1 = t0.AddSeconds(2);   // past the 1500ms burst window
        for (int i = 0; i < 5; i++)
            h.Feed($"Group B line #{i}", t1.AddMilliseconds(i * 50));

        Assert.Equal(10, h.Candidates.Candidates.Count);
    }
}
