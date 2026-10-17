using System;
using System.Collections.Generic;
using MudPlay.Game;
using MudPlay.Game.GameData;
using MudPlay.Game.Remote;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// The tracker folds each party member's known level (exact, else title band)
// into the (Low, High) window MovementFilter reads, and tops up a
// member whose level is unknown or not from the current day via
// WarmStaleLevels when a route crosses a level gate. The on-partying
// @level refresh lives in PartyProbeManager, not here — so the tracker no longer
// auto-probes on roster change. Wired to a real probe + PlayerDatabase
// so the reply→persist→read loop is exercised end to end.
public sealed class PartyLevelTrackerTests
{
    private sealed class Harness
    {
        public readonly MessageRouter Router;
        public readonly ChatRouter Chat;
        public readonly PartyState State = new();
        public readonly PartyBroadcaster Broadcaster;
        public readonly PlayerDatabase Players = new(new ProfileService(), () => null);
        public readonly List<byte[]> Wire = new();
        public readonly List<Action> Windows = new();
        public readonly PartyLevelProbe Probe;
        public readonly PartyLevelTracker Tracker;

        public int? SelfLevel;
        public DateTime Now = DateTime.UnixEpoch;

        public Harness()
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            Chat = new ChatRouter(Router);
            Broadcaster = new PartyBroadcaster(State);
            Broadcaster.SetWireSender(Wire.Add);
            Probe = new PartyLevelProbe(
                Broadcaster, Chat, State,
                armWindow: Windows.Add,
                recordLevel: (n, l) => Players.RecordLevel(n, l, Now),
                log: null);
            Tracker = new PartyLevelTracker(
                State, Probe, Players,
                selfLevel: () => SelfLevel,
                clock: () => Now,
                log: null);
        }

        // One armed reply-window == one probe that actually broadcast; the
        // probe only arms a window when it has members to ask.
        public int Probes => Windows.Count;

        public PartyMember AddMember(string name, bool self = false, bool invited = false)
        {
            PartyMember m = new() { Name = name, IsSelf = self, IsInvited = invited };
            State.Members.Add(m);
            return m;
        }

        public void Lead()
        {
            State.IsInParty = true;
            State.SelfIsLeader = true;
        }

        public void Reply(string sender, string message)
        {
            string line = $"{sender} telepaths: {message}";
            Router.Dispatch(new Terminal.LineExtractor.EmittedLine(
                line, new Terminal.CellAttributes[line.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));
        }

        public void Say(string line) =>
            Router.Dispatch(new Terminal.LineExtractor.EmittedLine(
                line, new Terminal.CellAttributes[line.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));

        public static string Level(int lvl) => $"Level {lvl}, 1,234 exp, 500 to next level";
    }

    // ----- Bounds gating --------------------------------------------------

    [Fact]
    public void Bounds_NotInParty_ReturnsNull()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Bob");
        h.State.SelfIsLeader = true;   // leader flag set but not in a party
        h.Players.RecordLevel("Bob", 30, DateTime.UnixEpoch);

        Assert.Null(h.Tracker.Bounds());
    }

    [Fact]
    public void Bounds_NotLeading_ReturnsNull()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Bob");
        h.State.IsInParty = true;   // in a party but following, not leading
        h.Players.RecordLevel("Bob", 30, DateTime.UnixEpoch);

        Assert.Null(h.Tracker.Bounds());
    }

    // ----- Bounds computation --------------------------------------------

    [Fact]
    public void Bounds_FoldsSelfAndExactMemberLevels()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Bob");
        h.AddMember("Al");
        h.Lead();
        h.Players.RecordLevel("Bob", 30, DateTime.UnixEpoch);
        h.Players.RecordLevel("Al", 55, DateTime.UnixEpoch);

        Assert.Equal((30, 55), h.Tracker.Bounds());   // low=min(40,30,55), high=max(...)
    }

    [Fact]
    public void Bounds_UsesTitleBand_WhenNoExactLevel()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Bob");
        h.Lead();
        // A who-observation gives Bob a title but no exact level → title band.
        h.Players.RecordObservation("Bob", null, null, null, "Apprentice", null, null, DateTime.UnixEpoch);

        (int MinLevel, int MaxLevel)? band = ClassTitleTable.LookupLevelRange("Apprentice");
        Assert.NotNull(band);
        int expLow = Math.Min(40, band!.Value.MinLevel);
        int expHigh = Math.Max(40, band.Value.MaxLevel);
        Assert.Equal((expLow, expHigh), h.Tracker.Bounds());
    }

    [Fact]
    public void Bounds_ExactPreferredOverTitle()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Bob");
        h.Lead();
        h.Players.RecordObservation("Bob", null, null, null, "Apprentice", null, null, DateTime.UnixEpoch);
        h.Players.RecordLevel("Bob", 12, DateTime.UnixEpoch);   // exact wins over the band

        Assert.Equal((12, 40), h.Tracker.Bounds());
    }

    [Fact]
    public void Bounds_SkipsSelfMember_NotDoubleCounted()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Me", self: true);
        h.AddMember("Bob");
        h.Lead();
        h.Players.RecordLevel("Bob", 30, DateTime.UnixEpoch);
        // A stray level recorded for the self row must not widen the window.
        h.Players.RecordLevel("Me", 99, DateTime.UnixEpoch);

        Assert.Equal((30, 40), h.Tracker.Bounds());
    }

    [Fact]
    public void Bounds_SkipsUnknownMember_FallsToSelfOnly()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Ghost");   // never observed → no record
        h.Lead();

        Assert.Equal((40, 40), h.Tracker.Bounds());   // only self is known
    }

    // The tracker no longer auto-probes on roster change (that moved to
    // PartyProbeManager) — so no test here asserts a probe fires on join. Bounds
    // + the demand-driven WarmStaleLevels are what remain.

    // ----- Invited members are excluded from bounds + warm ---------------

    [Fact]
    public void Bounds_SkipsInvitedMember()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Tristian", invited: true);
        h.Lead();
        // Even if a level was somehow cached, an invited row must not widen bounds.
        h.Players.RecordLevel("Tristian", 12, DateTime.UnixEpoch);

        Assert.Equal((40, 40), h.Tracker.Bounds());   // only self is counted
    }

    [Fact]
    public void WarmStaleLevels_SkipsInvitedMember()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Tristian", invited: true);   // unknown level, but invited
        h.Lead();

        int before = h.Probes;
        h.Tracker.WarmStaleLevels();
        Assert.Equal(before, h.Probes);   // invited members don't count as stale
    }

    // ----- End-to-end: probe reply persists and shows up in Bounds -------

    [Fact]
    public void LevelReply_PersistsLevel_ReflectedInBounds()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Bob");
        h.Lead();
        Assert.Null(h.Players.Find("Bob")?.Level);

        // The probe's parser records ANY @level reply (auto-probe, the once-a-day
        // PartyProbeManager send, or a manual /Bob @level) — not just one it kicked
        // off — so a reply alone persists and folds into the window.
        h.Reply("Bob", Harness.Level(18));

        Assert.Equal(18, h.Players.Find("Bob")!.Level);   // persisted via the probe seam
        Assert.Equal((18, 40), h.Tracker.Bounds());        // and folded into the window
    }

    [Fact]
    public void LevelReply_BraceWrapped_StillRecords()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Bob");
        h.Lead();

        // A reply from another MudPlay client is brace-wrapped by SendReply.
        h.Reply("Bob", "{" + Harness.Level(22) + "}");

        Assert.Equal(22, h.Players.Find("Bob")!.Level);
    }

    // MegaMUD answers @level as "Level: N  Needed: N  Will level in: T" — its level
    // has to record too, or a MegaMUD party member's level is never known.
    [Fact]
    public void LevelReply_MegaMudShape_Records()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Bob");
        h.Lead();

        h.Reply("Bob", "{Level: 22  Needed: 1,000  Will level in: ?}");

        Assert.Equal(22, h.Players.Find("Bob")!.Level);
    }

    // A level-58 member with exp banked for 59 is asked @level: MegaMUD counts the
    // Needed figure to L60 and still says Level: 58. That is their level, recorded
    // and handed to the Party window as stated, with the level its figure counts toward.
    [Fact]
    public void LevelReply_MegaMudBankedShape_RecordsTheStatedLevel()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Bob");
        h.Lead();
        var seen = new List<(int Level, int Hint)>();
        h.Probe.ProgressObserved += (_, level, _, _, _, toLevel) => seen.Add((level, toLevel));

        h.Reply("Bob", "{Level: 58  Needed: 216,526,237 (L60)  Will level in: +2 in 2-3 days}");

        Assert.Equal(58, h.Players.Find("Bob")!.Level);
        Assert.Equal([(58, 60)], seen);
    }

    // An @exp reply states no level: its (L<n>) tag rides along as the level the
    // figure counts toward, and is never recorded as the member's level.
    [Fact]
    public void ExpReply_CarriesTheTagAsAHint_NotALevel()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Bob");
        h.Lead();
        var seen = new List<(int Level, int Hint)>();
        h.Probe.ProgressObserved += (_, level, _, _, _, toLevel) => seen.Add((level, toLevel));

        h.Reply("Bob", "{Made: 0  Needed: 216,526,237 (L60)  Rate: unknown}");

        Assert.Equal([(0, 60)], seen);
        Assert.Null(h.Players.Find("Bob"));
    }

    [Theory]
    [InlineData("{Made: 359  Needed: 1,077 (L2, +0.38 lvls)  Rate: 1,816/hr  Will level in: 35m}", 2, 1077L, "35m")]
    [InlineData("{Made: 0  Needed: 1,750 (L2, +0.18 lvls)  Rate: unknown}", 2, 1750L, null)]
    [InlineData("{Made: 0  Needed: 216,526,237 (L60)  Rate: unknown}", 60, 216526237L, null)]
    [InlineData("{Made: 0  Needed: 1,000  Rate: ? k/hr  Will level in: ?}", 0, 1000L, null)]
    public void TryParseExpReply_ReadsBothClients(string reply, int toLevel, long needed, string? eta)
    {
        Assert.True(Game.Remote.PartyLevelProbe.TryParseExpReply(reply, out int l, out long n, out string? e));
        Assert.Equal(toLevel, l);
        Assert.Equal(needed, n);
        Assert.Equal(eta, e);
    }

    // `.@level` asks over say; the reply is a directed say back, and still records.
    [Fact]
    public void LevelReply_DirectedSay_Records()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Bob");
        h.Lead();

        h.Say("Bob says (to you) \"{Level 23, 673 exp, 1,077 to next level}\"");

        Assert.Equal(23, h.Players.Find("Bob")!.Level);
    }

    [Theory]
    [InlineData("{Level 12, 1,234 exp, 500 to next level}", 12, 500L, null, 0)]
    [InlineData("Level 12, 1,234 exp, exp-to-next unknown (type exp)", 12, null, null, 0)]
    [InlineData("{Level: 1  Needed: 1,000  Will level in: ?}", 1, 1000L, null, 0)]
    [InlineData("{Level: 30  Needed: 2,345,678  Will level in: 1 hour}", 30, 2345678L, "1 hour", 0)]
    [InlineData("{Level: 58  Needed: 216,526,237 (L60)  Will level in: +2 in 2-3 days}", 58, 216526237L, "+2 in 2-3 days", 60)]
    [InlineData("{Level: 58  Needed: 208,954,024 (L60)  Will level in: ?}", 58, 208954024L, null, 60)]
    [InlineData("{Level: 58  Needed: 208,954,024 (L60)}", 58, 208954024L, null, 60)]
    public void TryParseLevelReply_ReadsBothShapes(string reply, int level, long? needed, string? eta, int toLevel)
    {
        Assert.True(Game.Remote.PartyLevelProbe.TryParseLevelReply(reply, out int l, out long? n, out string? e, out _, out int tl));
        Assert.Equal(level, l);
        Assert.Equal(needed, n);
        Assert.Equal(eta, e);
        Assert.Equal(toLevel, tl);
    }

    // ----- Route-scoped freshness warm (WarmStaleLevels) -----------------

    [Fact]
    public void WarmStaleLevels_UnknownMember_Probes()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Bob");   // never answered @level → unknown
        h.Lead();

        int before = h.Probes;
        h.Tracker.WarmStaleLevels();
        Assert.Equal(before + 1, h.Probes);   // unknown member → fresh probe
    }

    [Fact]
    public void WarmStaleLevels_FreshExact_NoProbe()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Bob");
        h.Lead();
        h.Players.RecordLevel("Bob", 30, h.Now);   // stamped LevelAt = now

        int before = h.Probes;
        h.Tracker.WarmStaleLevels();
        Assert.Equal(before, h.Probes);   // everyone fresh → nothing to warm
    }

    [Fact]
    public void WarmStaleLevels_PriorDayExact_Reprobes()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Bob");
        h.Lead();
        h.Players.RecordLevel("Bob", 30, h.Now);   // recorded at UnixEpoch

        h.Now = DateTime.UnixEpoch + TimeSpan.FromHours(25);   // a later calendar day
        int before = h.Probes;
        h.Tracker.WarmStaleLevels();
        Assert.Equal(before + 1, h.Probes);   // reading not from today → re-probe
    }

    [Fact]
    public void WarmStaleLevels_Debounced_OneRoundTrip()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Bob");   // unknown
        h.Lead();

        int before = h.Probes;
        h.Tracker.WarmStaleLevels();
        h.Tracker.WarmStaleLevels();   // second call inside the debounce window
        Assert.Equal(before + 1, h.Probes);   // at most one round-trip per plan
    }

    [Fact]
    public void WarmStaleLevels_NotLeading_NoProbe()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Bob");
        h.State.IsInParty = true;   // following, not leading

        h.Tracker.WarmStaleLevels();
        Assert.Equal(0, h.Probes);
    }
}
