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
/// The wealth tracker is demand-driven: unlike <see cref="PartyLevelTracker"/>
/// (which keeps levels warm on every roster change), it holds no party-state
/// subscription and fires an <c>@wealth</c> probe only when <c>MinWealth</c> is
/// asked — which <see cref="MovementFilter"/> does solely while BFS evaluates a
/// <c>(Toll: N)</c> exit. Wired to a real probe with a captured reply-window and a
/// mutable clock so freshness / debounce are exercised deterministically.
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

        public bool Enabled = true;
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
                isEnabled: () => Enabled,
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
    public void MinWealth_FeatureDisabled_ReturnsNull()
    {
        var h = new Harness { SelfWealth = 5000, Enabled = false };
        h.AddMember("Bob");
        h.Lead();
        h.Tracker.Record("Bob", 3000);

        Assert.Null(h.Tracker.MinWealth());
        Assert.Equal(0, h.Probes);   // gated off before the demand-poll
    }

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
        Assert.Equal(0, h.Probes);            // no point probing when we can't gate anyway
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

    // ----- Demand-poll: fires from MinWealth, debounced -----------------------

    [Fact]
    public void MinWealth_FiresProbe_OnDemand()
    {
        var h = new Harness { SelfWealth = 5000 };
        h.AddMember("Bob");
        h.Lead();

        Assert.Equal(0, h.Probes);   // no probe until a toll is actually evaluated
        _ = h.Tracker.MinWealth();

        Assert.Equal(1, h.Probes);
        Assert.Equal("/Bob @wealth\r", h.Sent(0));
    }

    [Fact]
    public void MinWealth_Debounced_WithinWindow()
    {
        var h = new Harness { SelfWealth = 5000 };
        h.AddMember("Bob");
        h.Lead();

        _ = h.Tracker.MinWealth();
        _ = h.Tracker.MinWealth();   // same window — no second round-trip

        Assert.Equal(1, h.Probes);
    }

    [Fact]
    public void MinWealth_Refires_AfterWindow()
    {
        var h = new Harness { SelfWealth = 5000 };
        h.AddMember("Bob");
        h.Lead();

        _ = h.Tracker.MinWealth();
        Assert.Equal(1, h.Probes);

        h.Advance(h.Tracker.FreshnessWindow + TimeSpan.FromSeconds(1));
        _ = h.Tracker.MinWealth();

        Assert.Equal(2, h.Probes);   // window elapsed → re-poll
    }

    [Fact]
    public void Probe_DoesNotFire_OnRosterChange()
    {
        var h = new Harness { SelfWealth = 5000 };
        h.AddMember("Bob");
        h.Lead();
        h.AddMember("Al");   // roster grows — no subscription, so no probe

        Assert.Equal(0, h.Probes);   // demand-driven only; nothing polled yet
    }

    // ----- End-to-end: reply lands via Record, reflected next MinWealth -------

    [Fact]
    public void ProbeReply_RecordsWealth_ReflectedInMinWealth()
    {
        var h = new Harness { SelfWealth = 5000 };
        h.AddMember("Bob");
        h.Lead();

        Assert.Equal(0, h.Tracker.MinWealth());   // fires probe; Bob unknown → 0
        Assert.Equal(1, h.Probes);

        h.Reply("Bob", Harness.Coins(3000));      // Bob answers @wealth

        // Same freshness window → debounced (no new probe), reads Bob's fresh reading.
        Assert.Equal(3000, h.Tracker.MinWealth());
        Assert.Equal(1, h.Probes);
    }
}
