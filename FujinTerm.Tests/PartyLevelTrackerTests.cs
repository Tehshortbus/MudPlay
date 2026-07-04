using System;
using System.Collections.Generic;
using FujinTerm.Game;
using FujinTerm.Game.GameData;
using FujinTerm.Game.Remote;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// The tracker keeps party level bounds warm for path planning: it fires
/// an <c>@level</c> probe on roster change (debounced) and folds each
/// member's known level (exact, else title band) into the (Low, High)
/// window <see cref="MovementFilter"/> reads. Wired to a real probe +
/// <see cref="PlayerDatabase"/> so the probe→persist→read loop is exercised
/// end to end.
/// </summary>
public sealed class PartyLevelTrackerTests
{
    private sealed class Harness
    {
        public readonly MessageRouter Router;
        public readonly ChatRouter Chat;
        public readonly PartyState State = new();
        public readonly PartyBroadcaster Broadcaster;
        public readonly PlayerDatabase Players = new();
        public readonly List<byte[]> Wire = new();
        public readonly List<Action> Windows = new();
        public readonly PartyLevelProbe Probe;
        public readonly PartyLevelTracker Tracker;

        public bool Enabled = true;
        public int? SelfLevel;

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
                recordLevel: (n, l) => Players.RecordLevel(n, l, DateTime.UnixEpoch),
                log: null);
            Tracker = new PartyLevelTracker(
                State, Probe, Players,
                selfLevel: () => SelfLevel,
                isEnabled: () => Enabled,
                log: null);
        }

        // One armed reply-window == one probe that actually broadcast; the
        // probe only arms a window when it has members to ask.
        public int Probes => Windows.Count;

        public void AddMember(string name, bool self = false)
            => State.Members.Add(new PartyMember { Name = name, IsSelf = self });

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

        public static string Level(int lvl) => $"Level {lvl}, 1,234 exp, 500 to next level";
    }

    // ----- Bounds gating --------------------------------------------------

    [Fact]
    public void Bounds_FeatureDisabled_ReturnsNull()
    {
        var h = new Harness { SelfLevel = 40, Enabled = false };
        h.AddMember("Bob");
        h.Lead();
        h.Players.RecordLevel("Bob", 30, DateTime.UnixEpoch);

        Assert.Null(h.Tracker.Bounds());
    }

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

    // ----- Probe firing + debounce ---------------------------------------

    [Fact]
    public void Probe_FiresOnRosterChange_WhenLeading()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.Lead();

        Assert.Equal(1, h.Probes);
        Assert.NotEmpty(h.Wire);   // /Bob @level went out
    }

    [Fact]
    public void Probe_DoesNotFire_WhenDisabled()
    {
        var h = new Harness { Enabled = false };
        h.AddMember("Bob");
        h.Lead();

        Assert.Equal(0, h.Probes);
        Assert.Empty(h.Wire);
    }

    [Fact]
    public void Probe_DoesNotFire_WhenNotLeading()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.State.IsInParty = true;   // following, not leading

        Assert.Equal(0, h.Probes);
    }

    [Fact]
    public void Probe_Refires_WhenRosterGrows()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.Lead();
        Assert.Equal(1, h.Probes);

        h.AddMember("Al");          // roster signature changes
        Assert.Equal(2, h.Probes);
    }

    [Fact]
    public void Probe_NotRefired_WhenSelfMemberAdded()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.Lead();
        Assert.Equal(1, h.Probes);

        h.AddMember("Me", self: true);   // self excluded from the signature
        Assert.Equal(1, h.Probes);       // no re-probe
    }

    // ----- End-to-end: probe reply persists and shows up in Bounds -------

    [Fact]
    public void ProbeReply_PersistsLevel_ReflectedInBounds()
    {
        var h = new Harness { SelfLevel = 40 };
        h.AddMember("Bob");
        h.Lead();
        Assert.Equal(1, h.Probes);          // probe went out on join
        Assert.Null(h.Players.Find("Bob")?.Level);

        h.Reply("Bob", Harness.Level(18));   // Bob answers @level

        Assert.Equal(18, h.Players.Find("Bob")!.Level);   // persisted via the probe seam
        Assert.Equal((18, 40), h.Tracker.Bounds());        // and folded into the window
    }
}
