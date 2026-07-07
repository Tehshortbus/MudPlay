using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Coverage for <see cref="AllyDroppedHandler"/> — the party reaction to another
/// member dropping to the ground. Pins the recognition logic (live member,
/// recently-partied follower, and the off-roster former-leader gap the bug report
/// exposed), the aid / re-invite / recovery decisions, that aid fires without a
/// party-heal loadout (a non-healer still rescues), and the
/// <see cref="MovementCoordinator.AllyDownGate"/> lifecycle.
/// </summary>
public sealed class AllyDroppedHandlerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public PartyState Party { get; } = new();
        public PartyManager Manager { get; }
        public ChatRouter Chat { get; }
        public MovementCoordinator Coordinator { get; } = new();
        public PartySettings Cfg { get; } = new() { MinorPartyHealSpell = "mihe" };
        public AllyDroppedHandler Handler { get; }
        public List<byte[]> Wire { get; } = new();

        public bool Enabled { get; set; } = true;
        public DateTime Clock { get; set; } = new(2026, 7, 5, 19, 32, 0, DateTimeKind.Utc);

        public Harness(string selfName = "Raijin")
        {
            DefaultPatterns.Seed(Router);
            Manager = new PartyManager(Router, Party) { LocalCharacterName = selfName };
            Chat = new ChatRouter(Router);
            Handler = new AllyDroppedHandler(
                Router, Party, Manager, Chat, Coordinator,
                readParty: () => Cfg,
                isEnabled: () => Enabled,
                useTimer: false,
                log: null)
            {
                NowProvider = () => Clock,
            };
            Handler.SetWireSender(Wire.Add);
        }

        public void Drop(string name) => Router.Dispatch(Line($"{name} drops to the ground!"));
        public void Aid(string name) => Router.Dispatch(Line($"You have aided {name}, his wounds are now healing."));

        public bool GateAsserted =>
            Coordinator.AssertedGates.Contains(MovementCoordinator.AllyDownGate);

        public bool WireHas(string command)
        {
            string needle = command;
            foreach (byte[] b in Wire)
                if (Encoding.Latin1.GetString(b) == needle) return true;
            return false;
        }

        public bool ManagerSent(string command)
        {
            foreach (byte[] b in Manager.LastSentForTests)
                if (Encoding.Latin1.GetString(b) == command) return true;
            return false;
        }

        private static LineExtractor.EmittedLine Line(string text) =>
            new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

        public void Dispose()
        {
            Handler.Dispose();
            Chat.Dispose();
            Manager.Dispose();
        }
    }

    [Fact]
    public void RosterMemberDrops_AidsAndHoldsMovement()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "Fujin WuzHere" });

        h.Drop("Fujin");

        Assert.True(h.GateAsserted);
        Assert.True(h.WireHas("aid Fujin\r"));
        Assert.True(h.Handler.IsTrackingForTests("Fujin"));
    }

    [Fact]
    public void FormerLeaderDrops_OffRoster_StillRecognised()
    {
        // The reported gap: the LEADER dropped, and a leader disconnect dissolves
        // the party — so by the time "Fujin drops to the ground!" arrives Fujin is
        // off our roster AND absent from PartyManager's disconnect grace map (a
        // follower never stamps its own leader there). The handler's own recent-
        // leader memory must still recognise the drop.
        using Harness h = new();
        h.Party.LeaderName = "Fujin WuzHere";  // handler notes the live leader
        h.Party.LeaderName = null;             // dissolution — snapshot recent leader

        h.Drop("Fujin");

        Assert.True(h.GateAsserted);
        Assert.True(h.WireHas("aid Fujin\r"));
    }

    [Fact]
    public void FormerLeaderDrops_PastGraceWindow_NotRecognised()
    {
        using Harness h = new();
        h.Party.LeaderName = "Fujin WuzHere";
        h.Party.LeaderName = null;
        h.Clock = h.Clock.AddSeconds(120); // well past RecentLeaderGrace

        h.Drop("Fujin");

        Assert.False(h.GateAsserted);
        Assert.False(h.WireHas("aid Fujin\r"));
    }

    [Fact]
    public void SelfDrop_Ignored()
    {
        // The dropper sees the line with their OWN name; PlayerDroppedGate owns
        // the self case, so this handler must not react.
        using Harness h = new(selfName: "Raijin");

        h.Drop("Raijin");

        Assert.False(h.GateAsserted);
        Assert.False(h.WireHas("aid Raijin\r"));
    }

    [Fact]
    public void StrangerDrop_Ignored()
    {
        using Harness h = new();

        h.Drop("Nobody");

        Assert.False(h.GateAsserted);
        Assert.False(h.WireHas("aid Nobody\r"));
    }

    [Fact]
    public void Disabled_NoReaction()
    {
        using Harness h = new() { Enabled = false };
        h.Party.Members.Add(new PartyMember { Name = "Fujin WuzHere" });

        h.Drop("Fujin");

        Assert.False(h.GateAsserted);
        Assert.False(h.WireHas("aid Fujin\r"));
    }

    [Fact]
    public void NoPartyHealConfigured_StillAidsAndHolds()
    {
        // A non-healer (no party-heal spell — e.g. a Mystic whose only heal is a
        // self-power) must still aid a dropped ally: `aid` is universal and lifts
        // them back above 0 HP on its own. The reported case.
        using Harness h = new();
        h.Cfg.MinorPartyHealSpell = null;
        h.Cfg.MajorPartyHealSpell = null;
        h.Party.Members.Add(new PartyMember { Name = "Fujin WuzHere" });

        h.Drop("Fujin");

        Assert.True(h.GateAsserted);
        Assert.True(h.WireHas("aid Fujin\r"));
        Assert.True(h.Handler.IsTrackingForTests("Fujin"));
    }

    [Fact]
    public void AidConfirmation_WhenLeading_ReInvites()
    {
        using Harness h = new();
        h.Party.SelfIsLeader = true;
        h.Party.Members.Add(new PartyMember { Name = "Fujin WuzHere" });
        h.Drop("Fujin");

        h.Aid("Fujin");

        Assert.True(h.ManagerSent("invite Fujin\r"));
    }

    [Fact]
    public void AidConfirmation_WhenFollowing_DoesNotReInvite()
    {
        // A follower's leader re-invites THEM, not the other way round.
        using Harness h = new();
        h.Party.SelfIsLeader = false;
        h.Party.LeaderName = "Fujin WuzHere";
        h.Party.LeaderName = null;
        h.Drop("Fujin");

        h.Aid("Fujin");

        Assert.False(h.ManagerSent("invite Fujin\r"));
    }

    [Fact]
    public void AidedAlly_ExposedToDownedProvider()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "Fujin WuzHere" });
        h.Drop("Fujin");
        // Un-aided (still mortally wounded) — not yet healable, so not exposed.
        Assert.Empty(h.Handler.AidedDownedGivenNames());

        h.Aid("Fujin");

        Assert.Contains("Fujin", h.Handler.AidedDownedGivenNames());
    }

    [Fact]
    public void HealthReplyAtFullHp_ReleasesHold()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "Fujin WuzHere" });
        h.Drop("Fujin");
        h.Aid("Fujin");
        Assert.True(h.GateAsserted);

        h.Handler.NoteHealthReplyForTests("Fujin", "{HP=200/200,MA=100/100}");

        Assert.False(h.GateAsserted);
        Assert.False(h.Handler.IsTrackingForTests("Fujin"));
    }

    [Fact]
    public void HealthReplyStillCritical_KeepsHold()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "Fujin WuzHere" });
        h.Drop("Fujin");
        h.Aid("Fujin");

        // Aided but still well below the 70% recovery bar — keep waiting.
        h.Handler.NoteHealthReplyForTests("Fujin", "{HP=20/200}");

        Assert.True(h.GateAsserted);
        Assert.True(h.Handler.IsTrackingForTests("Fujin"));
    }

    [Fact]
    public void RejoiningRoster_ReleasesHold()
    {
        using Harness h = new();
        h.Party.LeaderName = "Fujin WuzHere";
        h.Party.LeaderName = null;
        h.Drop("Fujin");
        Assert.True(h.GateAsserted);

        // Leader recovered and re-invited us — Fujin reappears on the roster.
        h.Party.Members.Add(new PartyMember { Name = "Fujin WuzHere" });

        Assert.False(h.GateAsserted);
        Assert.False(h.Handler.IsTrackingForTests("Fujin"));
    }

    [Fact]
    public void RescueTimeout_ReleasesHold()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "Fujin WuzHere" });
        h.Drop("Fujin");
        Assert.True(h.GateAsserted);

        h.Clock = h.Clock.AddSeconds(121); // past RescueTimeout
        h.Handler.TickPollForTests();

        Assert.False(h.GateAsserted);
        Assert.False(h.Handler.IsTrackingForTests("Fujin"));
    }

    [Fact]
    public void AidedAlly_PollsHealthOnTick()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "Fujin WuzHere" });
        h.Drop("Fujin");
        h.Aid("Fujin");
        h.Wire.Clear();

        h.Clock = h.Clock.AddSeconds(6); // past PollCadence
        h.Handler.TickPollForTests();

        Assert.True(h.WireHas("/Fujin @health\r"));
    }

    [Fact]
    public void UnaidedAlly_NotPolled()
    {
        // A still-mortally-wounded ally can't answer a telepath, so we don't poll
        // until our aid lands.
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "Fujin WuzHere" });
        h.Drop("Fujin");
        h.Wire.Clear();

        h.Clock = h.Clock.AddSeconds(6);
        h.Handler.TickPollForTests();

        Assert.False(h.WireHas("/Fujin @health\r"));
    }
}
