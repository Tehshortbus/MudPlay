using System;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Pins DefaultTaskRunner's reconnect / party-hold decision: the runner holds
// for the party-reform window ONLY when a game entry is a reconnect that
// followed a party session. The heavy loop / lair start paths (DispatcherTimer,
// UI-posted walker moves) are smoke-tested via dotnet run; here we drive the
// connect / prompt / party / disconnect latch sequence and assert
// PendingPartyRebuildHold — the exact input to the wait-vs-immediate branch.
public sealed class DefaultTaskRunnerTests
{
    private static readonly byte[] PromptBytes = Encoding.Latin1.GetBytes("[HP=100]: ");

    private sealed class Harness : IDisposable
    {
        public required WirePromptScanner Prompt { get; init; }
        public required PartyState PartyState { get; init; }
        public required ProfileService Profile { get; init; }
        public required DefaultTaskRunner Runner { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required IDisposable[] Owned { get; init; }

        // First in-game prompt of the connection — the "we're in MajorMUD" signal.
        public void EnterGame() => Prompt.Append(PromptBytes);

        // PartyManager owns IsInParty in the app; the test simulates the join by
        // writing the observable directly (the single-writer IL scan only guards
        // the MudPlay assembly, not this test).
        public void JoinParty() => PartyState.IsInParty = true;

        public void Dispose()
        {
            Runner.Dispose();
            foreach (IDisposable d in Owned)
            {
                try { d.Dispose(); }
                catch { /* best-effort teardown */ }
            }
        }
    }

    private static Harness Build()
    {
        GameDataCache cache = new();
        RoomGraphManager graph = new(cache);
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord);
        walker.SetWireSender(_ => { });
        LairTimerStore timers = new(cache, graph, tracker);
        AutoLairManager autoLair = new(walker, tracker, graph, bfs, timers);
        LoopManager loops = new(bfs, graph);
        LairManager lairs = new();
        WirePromptScanner prompt = new();
        LoopRunner loopRunner = new(tracker, coord, prompt);
        PartyState partyState = new();
        MessageRouter router = new();
        DefaultPatterns.Seed(router);  // PartyManager subscribes to catalog pattern ids on construction.
        PartyManager party = new(router, partyState);
        ProfileService profile = TestProfiles.Service();

        DefaultTaskRunner runner = new(
            prompt, tracker, profile, loops, lairs, loopRunner, autoLair, partyState, party);

        return new Harness
        {
            Prompt = prompt,
            PartyState = partyState,
            Profile = profile,
            Runner = runner,
            Tracker = tracker,
            Owned = new IDisposable[] { autoLair, timers },
        };
    }

    [Fact]
    public void FirstConnect_NeverArmsPartyHold()
    {
        using Harness h = Build();
        h.Runner.NotifyConnected();
        h.EnterGame();
        h.JoinParty();  // in a party, but this is the FIRST connect — nothing to reform after.
        Assert.False(h.Runner.PendingPartyRebuildHold);
    }

    [Fact]
    public void SoloReconnect_DoesNotArmPartyHold()
    {
        using Harness h = Build();
        // A solo in-game session, then a drop and redial.
        h.Runner.NotifyConnected();
        h.EnterGame();
        h.Runner.NotifyDisconnected();

        h.Runner.NotifyConnected();
        Assert.False(h.Runner.PendingPartyRebuildHold);
    }

    [Fact]
    public void PartyReconnect_ArmsPartyHold()
    {
        using Harness h = Build();
        h.Runner.NotifyConnected();
        h.EnterGame();
        h.JoinParty();
        h.Runner.NotifyDisconnected();

        h.Runner.NotifyConnected();
        Assert.True(h.Runner.PendingPartyRebuildHold);
    }

    [Fact]
    public void FailedConnect_NeverInGame_DoesNotArmPartyHold()
    {
        using Harness h = Build();
        // TCP connect that never reached the in-game prompt (no EnterGame), then drop.
        h.Runner.NotifyConnected();
        h.Runner.NotifyDisconnected();

        h.Runner.NotifyConnected();
        Assert.False(h.Runner.PendingPartyRebuildHold);
    }

    // A death worked out on the way back in (a hang-up penalty that killed) stops
    // the engines that are running; a task still waiting for the room to be known,
    // or out the party-rebuild window, would start after that stop and walk a
    // stripped character out of the temple. It is called off for the connection.
    [Fact]
    public void ADeathWorkedOutOnEntry_CallsTheTaskOffForThisConnection()
    {
        using Harness h = Build();
        h.Profile.LoadDefaultProfile();
        h.Tracker.Hydrate(h.Profile.Current!);
        h.Runner.NotifyConnected();
        h.EnterGame();   // the room isn't known yet: the task is still to start
        Assert.False(h.Runner.StoodDownForDeath);

        h.Tracker.NoteUnwitnessedDeath(new MudPlay.Game.Recovery.UnwitnessedDeath(
            null, DateTimeOffset.UtcNow, 6, "Killed by the hang-up penalty.", [], [], null));

        Assert.True(h.Runner.StoodDownForDeath);
        Assert.False(h.Runner.IsHoldingForParty);

        // The next connection starts clean.
        h.Runner.NotifyDisconnected();
        h.Runner.NotifyConnected();
        Assert.False(h.Runner.StoodDownForDeath);
    }

    // Off the link there is no task to call off.
    [Fact]
    public void ADeathWorkedOutWhileNotConnected_ChangesNothing()
    {
        using Harness h = Build();
        h.Profile.LoadDefaultProfile();
        h.Tracker.Hydrate(h.Profile.Current!);

        h.Tracker.NoteUnwitnessedDeath(new MudPlay.Game.Recovery.UnwitnessedDeath(
            null, DateTimeOffset.UtcNow, 6, "Killed by the hang-up penalty.", [], [], null));

        Assert.False(h.Runner.StoodDownForDeath);
    }

    [Fact]
    public void PartyDisbandedBeforeDrop_StillArmsHold()
    {
        using Harness h = Build();
        h.Runner.NotifyConnected();
        h.EnterGame();
        h.JoinParty();
        h.PartyState.IsInParty = false;  // party fell apart before the disconnect
        h.Runner.NotifyDisconnected();

        // Sticky: we HAD a party this session, so the next entry still holds.
        h.Runner.NotifyConnected();
        Assert.True(h.Runner.PendingPartyRebuildHold);
    }

    [Fact]
    public void ProfileReload_ClearsReconnectLatches()
    {
        using Harness h = Build();
        h.Runner.NotifyConnected();
        h.EnterGame();
        h.JoinParty();
        h.Runner.NotifyDisconnected();
        Assert.True(h.Runner.PendingPartyRebuildHold);

        // Switching characters wipes the reconnect memory — a fresh profile
        // never inherits the previous one's party-session state.
        h.Profile.LoadDefaultProfile();
        Assert.False(h.Runner.PendingPartyRebuildHold);
    }

    [Fact]
    public void Dispose_UnsubscribesPromptObserved()
    {
        using Harness h = Build();
        h.Runner.NotifyConnected();
        h.Runner.Dispose();

        // After Dispose the prompt no longer flips the in-game latch, so a
        // subsequent disconnect can't record a reconnect.
        h.EnterGame();
        h.Runner.NotifyDisconnected();
        h.Runner.NotifyConnected();
        Assert.False(h.Runner.PendingPartyRebuildHold);
    }
}
