using System;
using System.Collections.Generic;
using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Remote;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// The wealth tracker is demand-driven with a two-entry split: unlike
/// <see cref="PartyLevelTracker"/> (which keeps levels warm on every roster
/// change), it holds no party-state subscription. <c>MinWealth</c> is a pure
/// cache read (MovementFilter calls it per toll exit during BFS, so it must not
/// touch the wire — BFS explores off-path toll edges). <c>Probe</c> is the
/// route-scoped trigger fired from MovementFilter.WarmForRoute only when the
/// planned route actually crosses a toll. Wired to a real probe with a captured
/// reply-window and a mutable clock so freshness / debounce are exercised
/// deterministically.
/// </summary>
public sealed class PartyWealthTrackerTests
{
    private sealed class Harness
    {
        public readonly MessageRouter Router;
        public readonly ChatRouter Chat;
        public readonly PartyState State = new();
        public readonly PartyBroadcaster Broadcaster;
        public readonly List<byte[]> Wire = new();
        public readonly List<Action> Windows = new();
        public readonly PartyWealthProbe Probe;
        public readonly PartyWealthTracker Tracker;

        public long? SelfWealth;
        public DateTime Now = DateTime.UnixEpoch;

        public Harness()
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            Chat = new ChatRouter(Router);
            Broadcaster = new PartyBroadcaster(State);
            Broadcaster.SetWireSender(Wire.Add);
            Probe = new PartyWealthProbe(
                Broadcaster, Chat, State,
                armWindow: Windows.Add,
                recordWealth: (n, c) => Tracker!.Record(n, c),   // assigned just below; live by first reply
                log: null);
            Tracker = new PartyWealthTracker(
                State, Probe,
                selfWealth: () => SelfWealth,
                post: a => a(),          // fire the probe synchronously in-test
                clock: () => Now,
                log: null);
        }

        // One armed reply-window == one probe that actually broadcast.
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

        public string Sent(int i) => Encoding.Latin1.GetString(Wire[i]);

        public void Advance(TimeSpan by) => Now += by;

        // A loaded @wealth reply carrying coins, as InventoryQueryHandler emits it.
        public static string Coins(long copper) => $"1 gold (= {copper:N0} copper)";
    }

    // ----- Gating: MinWealth stands down --------------------------------------

    [Fact]
    public void MinWealth_NotInParty_ReturnsNull()
    {
        var h = new Harness { SelfWealth = 5000 };
        h.AddMember("Bob");
        h.State.SelfIsLeader = true;   // leader flag set but not in a party
        h.Tracker.Record("Bob", 3000);

        Assert.Null(h.Tracker.MinWealth());
    }

    [Fact]
    public void MinWealth_NotLeading_ReturnsNull()
    {
        var h = new Harness { SelfWealth = 5000 };
        h.AddMember("Bob");
        h.State.IsInParty = true;   // following, not leading
        h.Tracker.Record("Bob", 3000);

        Assert.Null(h.Tracker.MinWealth());
    }

    [Fact]
    public void MinWealth_SelfWealthUnknown_ReturnsNull()
    {
        var h = new Harness { SelfWealth = null };   // our own wallet not parsed
        h.AddMember("Bob");
        h.Lead();
        h.Tracker.Record("Bob", 3000);

        Assert.Null(h.Tracker.MinWealth());   // unknown wallet never refuses a walk
        Assert.Equal(0, h.Probes);            // MinWealth never probes (pure cache read)
    }

    // ----- Computation: min over self + fresh follower readings ---------------

    [Fact]
    public void MinWealth_FoldsSelfAndFollowerReadings()
    {
        var h = new Harness { SelfWealth = 5000 };
        h.AddMember("Bob");
        h.AddMember("Al");
        h.Lead();
        h.Tracker.Record("Bob", 3000);
        h.Tracker.Record("Al", 8000);

        Assert.Equal(3000, h.Tracker.MinWealth());   // poorest of {5000, 3000, 8000}
    }

    [Fact]
    public void MinWealth_SkipsSelfMemberRow()
    {
        var h = new Harness { SelfWealth = 5000 };
        h.AddMember("Me", self: true);   // self row has no reading; must not gate
        h.AddMember("Bob");
        h.Lead();
        h.Tracker.Record("Bob", 3000);

        Assert.Equal(3000, h.Tracker.MinWealth());   // self wealth comes from the seam, not a reading
    }

    [Fact]
    public void MinWealth_MissingFollower_ReturnsZero()
    {
        var h = new Harness { SelfWealth = 5000 };
        h.AddMember("Bob");   // never reported wealth
        h.Lead();

        Assert.Equal(0, h.Tracker.MinWealth());   // unknown follower gates any positive toll
    }

    [Fact]
    public void MinWealth_StaleFollower_ReturnsZero()
    {
        var h = new Harness { SelfWealth = 5000 };
        h.AddMember("Bob");
        h.Lead();
        h.Tracker.Record("Bob", 3000);
        Assert.Equal(3000, h.Tracker.MinWealth());   // fresh → folded in

        h.Advance(h.Tracker.FreshnessWindow + TimeSpan.FromSeconds(1));
        Assert.Equal(0, h.Tracker.MinWealth());      // gone stale → treated as absent
    }

    // ----- MinWealth is a pure cache read — never touches the wire ------------

    [Fact]
    public void MinWealth_NeverProbes()
    {
        var h = new Harness { SelfWealth = 5000 };
        h.AddMember("Bob");
        h.Lead();

        // Repeated reads, across the freshness window, must never fire an
        // @wealth — BFS calls MinWealth per off-path toll edge and the wire
        // must stay quiet unless the route-scoped Probe decides otherwise.
        _ = h.Tracker.MinWealth();
        h.Advance(h.Tracker.FreshnessWindow + TimeSpan.FromSeconds(1));
        _ = h.Tracker.MinWealth();

        Assert.Equal(0, h.Probes);
    }

    // ----- Probe: route-scoped, debounced -------------------------------------

    [Fact]
    public void Probe_FiresRoundTrip()
    {
        var h = new Harness { SelfWealth = 5000 };
        h.AddMember("Bob");
        h.Lead();

        Assert.Equal(0, h.Probes);   // nothing until the walker warms the route
        h.Tracker.Probe();

        Assert.Equal(1, h.Probes);
        Assert.Equal("/Bob @wealth\r", h.Sent(0));
    }

    [Fact]
    public void Probe_Debounced_WithinWindow()
    {
        var h = new Harness { SelfWealth = 5000 };
        h.AddMember("Bob");
        h.Lead();

        h.Tracker.Probe();
        h.Tracker.Probe();   // same window (e.g. a multi-toll loop expansion) — one round-trip

        Assert.Equal(1, h.Probes);
    }

    [Fact]
    public void Probe_Refires_AfterWindow()
    {
        var h = new Harness { SelfWealth = 5000 };
        h.AddMember("Bob");
        h.Lead();

        h.Tracker.Probe();
        Assert.Equal(1, h.Probes);

        h.Advance(h.Tracker.FreshnessWindow + TimeSpan.FromSeconds(1));
        h.Tracker.Probe();

        Assert.Equal(2, h.Probes);   // window elapsed → re-poll
    }

    [Fact]
    public void Probe_DoesNotFire_OnRosterChange()
    {
        var h = new Harness { SelfWealth = 5000 };
        h.AddMember("Bob");
        h.Lead();
        h.AddMember("Al");   // roster grows — no subscription, so no probe

        Assert.Equal(0, h.Probes);   // route-scoped only; nothing polled yet
    }

    // ----- End-to-end: reply lands via Record, reflected next MinWealth -------

    [Fact]
    public void ProbeReply_RecordsWealth_ReflectedInMinWealth()
    {
        var h = new Harness { SelfWealth = 5000 };
        h.AddMember("Bob");
        h.Lead();

        Assert.Equal(0, h.Tracker.MinWealth());   // Bob unknown → 0 (no probe from the read)
        h.Tracker.Probe();                        // walker warms the route
        Assert.Equal(1, h.Probes);

        h.Reply("Bob", Harness.Coins(3000));      // Bob answers @wealth

        Assert.Equal(3000, h.Tracker.MinWealth()); // reads Bob's fresh reading
        Assert.Equal(1, h.Probes);                 // still just the one round-trip
    }
}
