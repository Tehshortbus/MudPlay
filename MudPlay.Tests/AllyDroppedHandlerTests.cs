using System.Text;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Coverage for AllyDroppedHandler — the party reaction to another
// member dropping to the ground. Pins the recognition logic (live member,
// recently-partied follower, and the off-roster former-leader gap the bug report
// exposed), the aid / re-invite / recovery decisions, that aid fires without a
// party-heal loadout (a non-healer still rescues), and the
// MovementCoordinator.AllyDownGate lifecycle.
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
        public List<byte[]> ManagerWire { get; } = new();

        public bool Enabled { get; set; } = true;
        public int DeathFloor { get; set; } = -10;   // worst case: 10 ticks = 300 s
        public DateTime Clock { get; set; } = new(2026, 7, 5, 19, 32, 0, DateTimeKind.Utc);
        // The handler's poll timer.
        public ManualTimers Timers { get; } = new();

        public Harness(string selfName = "Raijin")
        {
            DefaultPatterns.Seed(Router);
            Manager = new PartyManager(Router, Party) { LocalCharacterName = selfName };
            Manager.SetWireSender(ManagerWire.Add);
            Chat = new ChatRouter(Router);
            Handler = new AllyDroppedHandler(
                Router, Party, Manager, Chat, Coordinator,
                readParty: () => Cfg,
                isEnabled: () => Enabled,
                log: null,
                readDeathFloor: () => DeathFloor,
                schedule: Timers.Arm,
                now: () => Clock);
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
            foreach (byte[] b in ManagerWire)
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

    // The master switch ends a rescue under way (its aid and poll would go on by
    // themselves) but keeps who was down, and the rescue starts again when the
    // switch is back on if they are still owed one.
    [Fact]
    public void MasterSwitch_RescueIsEnded_ThenStartedAgainForAnAllyStillDown()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "MudPlay WuzHere" });
        h.Drop("MudPlay");

        h.Handler.HoldForMasterSwitch();
        h.Enabled = false;
        Assert.False(h.GateAsserted);
        Assert.DoesNotContain("MudPlay", h.Handler.DownedGivenNames);
        h.Wire.Clear();

        h.Enabled = true;
        h.Handler.ResumeOwedRescues();

        Assert.True(h.GateAsserted);
        Assert.True(h.WireHas("aid MudPlay\r"));
        Assert.Contains("MudPlay", h.Handler.DownedGivenNames);
    }

    // One who dropped while the switch was off is owed a rescue too.
    [Fact]
    public void MasterSwitch_AllyWhoDroppedWhileOff_IsRescuedWhenItIsBackOn()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "MudPlay WuzHere" });
        h.Handler.HoldForMasterSwitch();
        h.Enabled = false;

        h.Drop("MudPlay");
        Assert.Empty(h.Wire);

        h.Enabled = true;
        h.Handler.ResumeOwedRescues();
        Assert.True(h.WireHas("aid MudPlay\r"));
    }

    [Fact]
    public void MasterSwitch_AllyWhoDiedMeanwhile_IsNotRescued()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "MudPlay WuzHere" });
        h.Drop("MudPlay");
        h.Handler.HoldForMasterSwitch();
        h.Enabled = false;
        new ServerFeed(h.Router).Line("MudPlay just left the Realm.");
        h.Wire.Clear();

        h.Enabled = true;
        h.Handler.ResumeOwedRescues();

        Assert.Empty(h.Wire);
    }

    [Fact]
    public void RosterMemberDrops_AidsAndHoldsMovement()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "MudPlay WuzHere" });

        h.Drop("MudPlay");

        Assert.True(h.GateAsserted);
        Assert.True(h.WireHas("aid MudPlay\r"));
        Assert.Contains("MudPlay", h.Handler.DownedGivenNames);
    }

    [Fact]
    public void FormerLeaderDrops_OffRoster_StillRecognised()
    {
        // The reported gap: the LEADER dropped, and a leader disconnect dissolves
        // the party — so by the time "MudPlay drops to the ground!" arrives MudPlay is
        // off our roster AND absent from PartyManager's disconnect grace map (a
        // follower never stamps its own leader there). The handler's own recent-
        // leader memory must still recognise the drop.
        using Harness h = new();
        h.Party.LeaderName = "MudPlay WuzHere";  // handler notes the live leader
        h.Party.LeaderName = null;             // dissolution — snapshot recent leader

        h.Drop("MudPlay");

        Assert.True(h.GateAsserted);
        Assert.True(h.WireHas("aid MudPlay\r"));
    }

    [Fact]
    public void FormerLeaderDrops_PastGraceWindow_NotRecognised()
    {
        using Harness h = new();
        h.Party.LeaderName = "MudPlay WuzHere";
        h.Party.LeaderName = null;
        h.Clock = h.Clock.AddSeconds(120); // well past RecentLeaderGrace

        h.Drop("MudPlay");

        Assert.False(h.GateAsserted);
        Assert.False(h.WireHas("aid MudPlay\r"));
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
        h.Party.Members.Add(new PartyMember { Name = "MudPlay WuzHere" });

        h.Drop("MudPlay");

        Assert.False(h.GateAsserted);
        Assert.False(h.WireHas("aid MudPlay\r"));
    }

    [Fact]
    public void NoPartyHealConfigured_StillAidsAndHolds()
    {
        // A non-healer (no party-heal spell — e.g. a Mystic whose only heal is a
        // self-power) must still aid a dropped ally: `aid` is universal and starts
        // their climb back above 0 HP on its own. The reported case.
        using Harness h = new();
        h.Cfg.MinorPartyHealSpell = null;
        h.Cfg.MajorPartyHealSpell = null;
        h.Party.Members.Add(new PartyMember { Name = "MudPlay WuzHere" });

        h.Drop("MudPlay");

        Assert.True(h.GateAsserted);
        Assert.True(h.WireHas("aid MudPlay\r"));
        Assert.Contains("MudPlay", h.Handler.DownedGivenNames);
    }

    // Aid only stops the bleeding — they can't accept an invite until their HP is
    // positive, so the leader re-invites when a @health reply shows them up.
    [Fact]
    public void WhenLeading_ReInvitesOnceTheyAreUp_NotOnAid()
    {
        using Harness h = new();
        h.Party.SelfIsLeader = true;
        h.Party.Members.Add(new PartyMember { Name = "MudPlay WuzHere" });
        h.Drop("MudPlay");

        h.Aid("MudPlay");
        Assert.False(h.ManagerSent("invite MudPlay\r"));

        ChatLines.Hear(h.Router, ChatChannel.TelepathIncoming, "MudPlay", "{HP=1/200}");
        Assert.True(h.ManagerSent("invite MudPlay\r"));
    }

    [Fact]
    public void WhenFollowing_DoesNotReInvite()
    {
        // A follower's leader re-invites THEM, not the other way round.
        using Harness h = new();
        h.Party.SelfIsLeader = false;
        h.Party.LeaderName = "MudPlay WuzHere";
        h.Party.LeaderName = null;
        h.Drop("MudPlay");
        h.Aid("MudPlay");

        ChatLines.Hear(h.Router, ChatChannel.TelepathIncoming, "MudPlay", "{HP=1/200}");

        Assert.False(h.ManagerSent("invite MudPlay\r"));
    }

    [Fact]
    public void AidedAlly_ExposedToDownedProvider()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "MudPlay WuzHere" });
        h.Drop("MudPlay");
        // Un-aided (still mortally wounded) — not yet healable, so not exposed.
        Assert.Empty(h.Handler.AidedDownedGivenNames());

        h.Aid("MudPlay");

        Assert.Contains("MudPlay", h.Handler.AidedDownedGivenNames());
    }

    [Fact]
    public void HealthReplyAtFullHp_ReleasesHold()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "MudPlay WuzHere" });
        h.Drop("MudPlay");
        h.Aid("MudPlay");
        Assert.True(h.GateAsserted);

        ChatLines.Hear(h.Router, ChatChannel.TelepathIncoming, "MudPlay", "{HP=200/200,MA=100/100}");

        Assert.False(h.GateAsserted);
        Assert.DoesNotContain("MudPlay", h.Handler.DownedGivenNames);
    }

    [Fact]
    public void HealthReplyStillCritical_KeepsHold()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "MudPlay WuzHere" });
        h.Drop("MudPlay");
        h.Aid("MudPlay");

        // Aided but still well below the 70% recovery bar — keep waiting.
        ChatLines.Hear(h.Router, ChatChannel.TelepathIncoming, "MudPlay", "{HP=20/200}");

        Assert.True(h.GateAsserted);
        Assert.Contains("MudPlay", h.Handler.DownedGivenNames);
    }

    [Fact]
    public void RejoiningRoster_ReleasesHold()
    {
        using Harness h = new();
        h.Party.LeaderName = "MudPlay WuzHere";
        h.Party.LeaderName = null;
        h.Drop("MudPlay");
        Assert.True(h.GateAsserted);

        // Leader recovered and re-invited us — MudPlay reappears on the roster.
        h.Party.Members.Add(new PartyMember { Name = "MudPlay WuzHere" });

        Assert.False(h.GateAsserted);
        Assert.DoesNotContain("MudPlay", h.Handler.DownedGivenNames);
    }

    [Fact]
    public void InvitedPlaceholder_DoesNotReleaseHold()
    {
        // Re-inviting a still-mortally-wounded ally adds an [Invited] placeholder
        // row — but they can't accept while down. That bare invite is NOT a rejoin;
        // the rescue must stay live so we keep polling + heal-targeting them.
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "MudPlay WuzHere" });
        h.Drop("MudPlay");
        h.Aid("MudPlay");
        Assert.True(h.GateAsserted);

        h.Party.Members.Add(new PartyMember { Name = "MudPlay WuzHere", IsInvited = true });

        Assert.True(h.GateAsserted);
        Assert.Contains("MudPlay", h.Handler.DownedGivenNames);
        Assert.Contains("MudPlay", h.Handler.AidedDownedGivenNames());
    }

    [Fact]
    public void Drop_AsksTheirHealthOnce()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "MudPlay WuzHere" });

        h.Drop("MudPlay");

        Assert.True(h.WireHas("/MudPlay @health\r"));
    }

    // With their HP unknown, the rescue waits the longest the climb can take: from
    // the deepest living HP (floor + 1) to 1 HP at a tick each — 10 ticks for a −10
    // floor — and only then starts asking. It holds the whole time; a fixed short
    // timeout used to walk off while they were still down.
    [Fact]
    public void UnknownHp_HoldsForTheWorstCaseClimb_ThenChecks()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "MudPlay WuzHere" });
        h.Drop("MudPlay");
        h.Aid("MudPlay");
        h.Wire.Clear();

        h.Clock = h.Clock.AddSeconds(299);
        h.Timers.RunAll();
        Assert.False(h.WireHas("/MudPlay @health\r"));    // can't be up yet
        Assert.True(h.GateAsserted);

        h.Clock = h.Clock.AddSeconds(1);
        h.Timers.RunAll();
        Assert.True(h.WireHas("/MudPlay @health\r"));
        Assert.True(h.GateAsserted);
    }

    [Fact]
    public void NegativeHpReply_TimesTheCheckExactly()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "MudPlay WuzHere" });
        h.Drop("MudPlay");
        h.Aid("MudPlay");

        ChatLines.Hear(h.Router, ChatChannel.TelepathIncoming, "MudPlay", "{HP=-3/200}");   // 4 ticks to 1 HP
        h.Wire.Clear();

        h.Clock = h.Clock.AddSeconds(119);
        h.Timers.RunAll();
        Assert.False(h.WireHas("/MudPlay @health\r"));

        h.Clock = h.Clock.AddSeconds(1);
        h.Timers.RunAll();
        Assert.True(h.WireHas("/MudPlay @health\r"));
    }

    [Fact]
    public void NeverStands_ReleasesAfterTheGrace()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "MudPlay WuzHere" });
        h.Drop("MudPlay");
        h.Aid("MudPlay");

        h.Clock = h.Clock.AddSeconds(300 + 59);
        h.Timers.RunAll();
        Assert.True(h.GateAsserted);

        h.Clock = h.Clock.AddSeconds(1);
        h.Timers.RunAll();
        Assert.False(h.GateAsserted);
        Assert.DoesNotContain("MudPlay", h.Handler.DownedGivenNames);
    }
}
