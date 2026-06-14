using System.Collections.Generic;
using System.IO;
using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Combat;
using FujinTerm.Game.Map;
using FujinTerm.Game.Remote;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 6.1 — leader-side <see cref="PartyComebackManager"/>. A stranded
/// follower's <c>@comeback</c> stops the running movement engine, walks to
/// recover them, re-invites, awaits the follow confirmation, then resumes.
/// Headless: the graph + tracker are driven directly, the wire is a no-op
/// sink, and recovery is exercised via the synchronous "already at
/// destination" Finished path (the walker has no completion event we can
/// pump, and DispatcherTimer ticks aren't pumped in the xUnit harness).
/// </summary>
public sealed class PartyComebackManagerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _root;

    public PartyComebackManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-comeback-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1 ↔ 1/2 ↔ 1/3 linear strip; 1/3 carries a lair tag so the timer
    // store resolves against it. 1/1 + 1/3 are the two markers Auto-Lair
    // needs; 1/1 doubles as the start position.
    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "[1-1-1][1]Group(lair): 1/3", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private const string LairsJson = """
        [ { "GroupIndex": "1-1-1", "AvgDelay": 5 } ]
        """;

    private sealed class Harness : IDisposable
    {
        public required MessageRouter Router { get; init; }
        public required PartyState PartyState { get; init; }
        public required PlayerDatabase Players { get; init; }
        public required PartyManager Party { get; init; }
        public required RemoteCommandManager Engine { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required LoopRunner Loop { get; init; }
        public required AutoLairManager Lair { get; init; }
        public required LairTimerStore Timers { get; init; }
        public required PartyComebackManager Comeback { get; init; }

        public string LastReply => Encoding.Latin1.GetString(Engine.LastSentForTests[^1]);

        public void Dispose()
        {
            Comeback.Dispose();
            Lair.Dispose();
            Timers.Dispose();
            Party.Dispose();
            Engine.Dispose();
        }
    }

    private Harness NewHarness()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "Lairs.json"), LairsJson);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState partyState = new();
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(chat, partyState, players);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord);
        walker.SetWireSender(_ => { });
        LoopRunner loop = new(tracker, coord, graph: graph, bfs: bfs, walker: walker);
        LairTimerStore timers = new(cache, graph, tracker);
        AutoLairManager lair = new(walker, tracker, graph, bfs, timers);
        MonsterMessageStore monsters = new();
        RoomEntityClassifier classifier = new(router, monsters, players, tracker);

        PartyManager party = new(router, partyState);
        PartyComebackManager comeback =
            new(engine, party, tracker, classifier, walker, loop, lair);

        return new Harness
        {
            Router = router,
            PartyState = partyState,
            Players = players,
            Party = party,
            Engine = engine,
            Tracker = tracker,
            Walker = walker,
            Loop = loop,
            Lair = lair,
            Timers = timers,
            Comeback = comeback,
        };
    }

    private static ChatLogEntry Telepath(string sender, string msg) =>
        new(Now, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    /// <summary>Seat the sender as an active party member so the engine's
    /// party-whitelist gate lets the (tier <c>None</c>) <c>@comeback</c>
    /// through.</summary>
    private static void SeatFollower(Harness h, string name)
    {
        h.PartyState.Members.Add(new PartyMember { Name = name });
        h.PartyState.IsInParty = true;
    }

    /// <summary>Grant <paramref name="name"/> the <c>RequestInvite</c>
    /// flag so the per-player gate authorises their <c>@forget</c> (it's
    /// not a party-whitelist command — a left-behind follower needs the
    /// explicit grant).</summary>
    private static void GrantForget(Harness h, string name)
    {
        h.Players.RecordObservation(name, @class: null, race: null, alignment: null,
            title: null, gang: null, role: null, nowUtc: Now);
        h.Players.EditCustomization(name,
            new Models.GameData.PlayerCustomization(RemoteControls: Models.GameData.PlayerRemoteControls.RequestInvite));
    }

    /// <summary>Mark 1/1 + 1/3, locate at 1/1, start Auto-Lair — the
    /// resumable "running engine" for these tests.</summary>
    private static void StartLair(Harness h)
    {
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Lair.Mark(new RoomKey(1, 1));
        h.Lair.Mark(new RoomKey(1, 3));
        Assert.True(h.Lair.Start());
        Assert.True(h.Lair.IsActive);
    }

    // ----- idle / busy guards ----------------------------------------

    [Fact]
    public void Idle_NoEngineRunning_RepliesCantImIdle()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");

        h.Engine.DispatchForTests(Telepath("Tank", "@comeback"));

        Assert.Contains("I can't I'm idle", h.LastReply);
        Assert.Equal(WalkState.Idle, h.Walker.State);
    }

    [Fact]
    public void SecondComeback_WhileBusy_RepliesAlreadyInProgress()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        StartLair(h);

        // First @comeback to a non-current room → walker starts walking,
        // stays busy (no synchronous completion).
        h.Engine.DispatchForTests(Telepath("Tank", "@comeback 1/3"));
        Assert.Equal(WalkState.Walking, h.Walker.State);

        h.Engine.DispatchForTests(Telepath("Tank", "@comeback 1/3"));
        Assert.Contains("already in progress", h.LastReply);
    }

    // ----- left-behind eligibility (leader-side authorisation) -------

    [Fact]
    public void LeftBehindFollower_NotActiveMember_StillAuthorised()
    {
        using Harness h = NewHarness();

        // Tank joins then gets left behind (self-departs / disconnects).
        // The server drops them from our party, so they're NOT an active
        // member — but the grace-window stamp makes them recoverable.
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Tank stops following you."));
        Assert.DoesNotContain(h.PartyState.Members,
            m => m.Name.Equals("Tank", StringComparison.OrdinalIgnoreCase));

        StartLair(h);
        h.Engine.DispatchForTests(Telepath("Tank", "@comeback 1/3"));

        // Authorised via WasRecentlyPartied → recovery walk begins.
        Assert.Contains("coming back to 1/3", h.LastReply);
        Assert.False(h.Lair.IsActive);
        Assert.Equal(WalkState.Walking, h.Walker.State);
    }

    [Fact]
    public void DeliberatelyUninvited_Comeback_IsDenied()
    {
        using Harness h = NewHarness();

        // WE uninvited Tank — "removed from your followers" does NOT stamp
        // the grace window, so the @comeback is rejected: recovery never
        // runs and the engine keeps looping.
        h.Router.Dispatch(Line("Tank started to follow you."));
        h.Router.Dispatch(Line("Tank has been removed from your followers."));

        StartLair(h);
        h.Engine.DispatchForTests(Telepath("Tank", "@comeback 1/3"));

        // Denial signal is the engine staying alive — the walker is already
        // Walking from AutoLair's own move, independent of @comeback, so
        // Lair.IsActive (not WalkState) is what distinguishes deny from allow.
        Assert.True(h.Lair.IsActive);
    }

    [Fact]
    public void Stranger_NeverPartied_Comeback_IsDenied()
    {
        using Harness h = NewHarness();
        StartLair(h);

        h.Engine.DispatchForTests(Telepath("Stranger", "@comeback 1/3"));

        // See DeliberatelyUninvited_Comeback_IsDenied — Lair.IsActive is the
        // denial signal; the walker is Walking from AutoLair regardless.
        Assert.True(h.Lair.IsActive);
    }

    // ----- explicit-room recovery ------------------------------------

    [Fact]
    public void ExplicitRoom_StopsEngine_AndWalksThere()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        StartLair(h);

        h.Engine.DispatchForTests(Telepath("Tank", "@comeback 1/3"));

        Assert.False(h.Lair.IsActive);                       // engine stopped
        Assert.Equal(WalkState.Walking, h.Walker.State);     // recovery walk owns the wire
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
        Assert.Contains("coming back to 1/3", h.LastReply);
    }

    [Fact]
    public void ExplicitRoom_AlreadyThere_ReInvitesFollower()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        StartLair(h);

        // Auto-Lair's first move left the tracker Pending, so @comeback's
        // WalkTo(1/1) defers (raises Started, not Finished). The command
        // first stops Auto-Lair, so settling the tracker at 1/1 now drives
        // the walker's deferred dispatch into WalkToImmediate, where
        // source==dest raises Finished synchronously → re-invite leg.
        h.Engine.DispatchForTests(Telepath("Tank", "@comeback 1/1"));
        h.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.Contains("invite Tank\r",
            Encoding.Latin1.GetString(h.Party.LastSentForTests[^1]));
        Assert.Contains("re-inviting Tank", h.LastReply);
        // Still busy — awaiting the follow confirmation, engine not yet resumed.
        Assert.False(h.Lair.IsActive);
    }

    [Fact]
    public void FollowConfirmed_ResumesCapturedEngine()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        StartLair(h);
        h.Engine.DispatchForTests(Telepath("Tank", "@comeback 1/1"));
        h.Tracker.SetLocated(new RoomKey(1, 1));   // settle → Finished → re-invite
        Assert.False(h.Lair.IsActive);

        // "Tank started to follow you." → PartyManager raises
        // MemberFollowConfirmed → comeback resumes the captured lair.
        h.Router.Dispatch(Line("Tank started to follow you."));

        Assert.True(h.Lair.IsActive);
        Assert.Contains("resuming", h.LastReply);
    }

    // ----- backtrack path --------------------------------------------

    [Fact]
    public void BareComeback_NoPathHistory_GoesIdle()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        StartLair(h);

        // Only the current room is in history → nothing to backtrack along.
        h.Engine.DispatchForTests(Telepath("Tank", "@comeback"));

        Assert.Contains("no path history", h.LastReply);
        Assert.False(h.Lair.IsActive);   // stopped during snapshot, stays stopped
        Assert.Equal(WalkState.Idle, h.Walker.State);
    }

    // ----- @forget (follower cancels their own pickup) ---------------

    [Fact]
    public void Forget_FromRecoveringMember_UninvitesAndResumes()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        GrantForget(h, "Tank");
        StartLair(h);

        // Pickup in flight (walking to 1/3), then Tank calls it off.
        h.Engine.DispatchForTests(Telepath("Tank", "@comeback 1/3"));
        Assert.Equal(WalkState.Walking, h.Walker.State);

        h.Engine.DispatchForTests(Telepath("Tank", "@forget"));

        Assert.Contains("uninvite Tank\r",
            Encoding.Latin1.GetString(h.Party.LastSentForTests[^1]));
        Assert.Contains("forgetting Tank", h.LastReply);
        Assert.True(h.Lair.IsActive);   // prior engine resumed
    }

    [Fact]
    public void Forget_WhileAwaitingFollow_UninvitesAndResumes()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        GrantForget(h, "Tank");
        StartLair(h);

        // Drive to the AwaitingFollow phase (re-invite sent, waiting).
        h.Engine.DispatchForTests(Telepath("Tank", "@comeback 1/1"));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.False(h.Lair.IsActive);

        h.Engine.DispatchForTests(Telepath("Tank", "@forget"));

        Assert.Contains("uninvite Tank\r",
            Encoding.Latin1.GetString(h.Party.LastSentForTests[^1]));
        Assert.True(h.Lair.IsActive);   // resumed instead of hanging on follow
    }

    [Fact]
    public void Forget_WhenNotBusy_RepliesNothingToForget()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        GrantForget(h, "Tank");
        StartLair(h);

        h.Engine.DispatchForTests(Telepath("Tank", "@forget"));

        Assert.Contains("nothing to forget", h.LastReply);
        Assert.True(h.Lair.IsActive);   // engine untouched
    }

    [Fact]
    public void Forget_FromDifferentMember_RepliesNothingToForget()
    {
        using Harness h = NewHarness();
        SeatFollower(h, "Tank");
        SeatFollower(h, "Mage");
        GrantForget(h, "Mage");
        StartLair(h);

        // Recovering Tank; Mage's @forget must not abandon Tank's pickup.
        h.Engine.DispatchForTests(Telepath("Tank", "@comeback 1/3"));
        Assert.Equal(WalkState.Walking, h.Walker.State);

        h.Engine.DispatchForTests(Telepath("Mage", "@forget"));

        Assert.Contains("nothing to forget", h.LastReply);
        Assert.False(h.Lair.IsActive);                    // Tank's pickup still in flight
        Assert.Equal(WalkState.Walking, h.Walker.State);
    }

    // ----- registration shape ----------------------------------------

    [Fact]
    public void Dispose_UnregistersHandler()
    {
        Harness h = NewHarness();
        Assert.True(h.Engine.HandlerCount > 0);
        h.Comeback.Dispose();
        Assert.Equal(0, h.Engine.HandlerCount);

        h.Lair.Dispose();
        h.Timers.Dispose();
        h.Party.Dispose();
        h.Engine.Dispose();
    }
}
