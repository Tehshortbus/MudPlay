using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// A party member's death in our room ("<Name> is dead.") asks one `par`; the first
// reply read afterwards settles it (user, 2026-10-17). Not listed: the roster drops
// them. Listed [Invited]: a leader uninvites them once combat clears. Every line goes
// through ServerFeed and the router, and every send is what the managers put on their
// wire senders.
public sealed class PartyDeathRosterCleanupTests
{
    private sealed class Harness : IDisposable
    {
        private readonly ServerFeed _server;
        public MessageRouter Router { get; } = new();
        public PartyState Party { get; } = new();
        public PartyManager Manager { get; }
        public PartyPoller Poller { get; }
        public MovementCoordinator Coord { get; } = new();
        public PartyDeathRosterCleanup Cleanup { get; }
        public AutoPartyManager AutoParty { get; }
        public PlayerDatabase Players { get; } = new(TestProfiles.Service(), () => null);
        // The poller and auto-party timers: armed, never fired.
        public ManualTimers Timers { get; } = new();
        public LogService Log { get; } = new();
        public List<string> Wire { get; } = new();
        public DateTime Clock { get; set; } = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

        public Harness(bool parOnTimer = false)
        {
            DefaultPatterns.Seed(Router);
            _server = new ServerFeed(Router);
            Manager = new PartyManager(Router, Party) { LocalCharacterName = "MudPlay" };
            Manager.AttachLineExtractor(_server.Lines);
            Manager.SetWireSender(b => Wire.Add(Encoding.Latin1.GetString(b)));
            // The poller's timers are armed but never fired here: with the par timer
            // on, its countdown is what counts.
            ChatRouter chat = new(Router);
            Poller = new PartyPoller(chat, Party, Manager, schedule: Timers.Arm, now: () => Clock);
            Poller.HealthNagEnabled = false;
            // With the par timer on, its countdown starts here, from the test's clock.
            Poller.ApplyParSettings(new PartySettings { ParPollOnTimer = parOnTimer, ParPollFrequencySec = 10 });
            Poller.SetWireSender(b => Wire.Add(Encoding.Latin1.GetString(b)));
            Cleanup = new PartyDeathRosterCleanup(Router, Party, Manager, Coord, Poller, Log,
                now: () => new DateTimeOffset(Clock));
            // Wired as AppServices wires it.
            AutoParty = new AutoPartyManager(Router, Players, Party, schedule: Timers.Arm, now: () => Clock)
            {
                UninvitedForDeath = given => Cleanup.UninvitedForDeath(given),
            };
            AutoParty.SetWireSender(b => Wire.Add(Encoding.Latin1.GetString(b)));
            AutoParty.SetRoomRoster(new RoomEntityClassifier(Router, new MonsterMessageStore(), Players));
        }

        public void Line(string text) => _server.Line(text);

        // A `par` reply: the follow line when we follow, the header, the member rows,
        // our own row last, and the prompt that ends it.
        public void ParReply(string? following, params string[] rows)
        {
            if (following is not null) Line($"You are following {following}.");
            Line("The following people are in your travel party:");
            foreach (string row in rows) Line(row);
            Line(Row("MudPlay"));
            Line("[HP=100/MA=50]:");
        }

        public static string Row(string name) => $"  {name,-30} {"(Warrior)",-12}          [H:100%]  - Frontrank";
        public static string Invited(string name) => $"  {name,-30} {"(Warrior)",-12} [Invited]";

        public int Pars => Wire.Count(w => w == "par\r");
        public IEnumerable<string> Uninvites => Wire.Where(w => w.StartsWith("uninvite", StringComparison.Ordinal));
        public bool OnRoster(string given) => Party.Members.Any(m => m.Name.StartsWith(given, StringComparison.Ordinal));

        public void Dispose()
        {
            AutoParty.Dispose();
            Cleanup.Dispose();
            Poller.Dispose();
            Manager.Dispose();
        }
    }

    // We lead Raijin (and Bob): both follow us.
    private static Harness Leading(bool parOnTimer = false)
    {
        Harness h = new(parOnTimer);
        h.Line("Raijin started to follow you.");
        h.Line("Bob started to follow you.");
        h.Line("Cid started to follow you.");
        return h;
    }

    [Fact]
    public void LeaderStock_DeadMemberNotListed_ParAsked_AndRosterDropsThem()
    {
        using Harness h = Leading();
        h.Line("Raijin is dead.");
        Assert.Equal(1, h.Pars);

        h.ParReply(null, Harness.Row("Bob"), Harness.Row("Cid"));   // Stock: no row for the dead

        Assert.False(h.OnRoster("Raijin"));
        Assert.True(h.OnRoster("Bob"));
        Assert.Empty(h.Uninvites);
        Assert.Contains("no longer lists Raijin", h.Cleanup.LastDeathSummary);
        Assert.Empty(h.Cleanup.AwaitingPar);
        Assert.Empty(h.Cleanup.AwaitingUninvite);
    }

    [Fact]
    public void LeaderParadigm_DeadMemberListedInvited_IsUninvited()
    {
        using Harness h = Leading();
        h.Line("Raijin is dead.");
        h.ParReply(null, Harness.Invited("Raijin"), Harness.Row("Bob"), Harness.Row("Cid"));

        Assert.Equal(["uninvite Raijin\r"], h.Uninvites);
    }

    // Hands-on play (no movement engine here): the death line, then [Invited], then
    // the uninvite once combat clears.
    [Fact]
    public void Uninvite_WaitsForCombatToClear()
    {
        using Harness h = Leading();
        h.Coord.AssertGate(MovementCoordinator.CombatGate);
        h.Line("Raijin is dead.");
        h.ParReply(null, Harness.Invited("Raijin"), Harness.Row("Bob"), Harness.Row("Cid"));
        Assert.Empty(h.Uninvites);
        Assert.Contains("once the room is clear", h.Cleanup.LastDeathSummary);

        h.Coord.ClearGate(MovementCoordinator.CombatGate);
        Assert.Equal(["uninvite Raijin\r"], h.Uninvites);

        // Further gate churn sends nothing more.
        h.Coord.AssertGate(MovementCoordinator.CombatGate);
        h.Coord.ClearGate(MovementCoordinator.CombatGate);
        Assert.Single(h.Uninvites);
    }

    [Fact]
    public void Follower_DeadMemberNotListed_IsDropped_AndListedInvited_IsLeft()
    {
        using Harness h = new();
        h.Line("You are now following Boss.");
        h.ParReply("Boss", Harness.Row("Boss"), Harness.Row("Raijin"), Harness.Row("Bob"));
        Assert.True(h.OnRoster("Raijin"));

        h.Line("Raijin is dead.");
        Assert.Equal(1, h.Pars);
        h.ParReply("Boss", Harness.Row("Boss"), Harness.Row("Bob"));
        Assert.False(h.OnRoster("Raijin"));

        h.Clock += TimeSpan.FromSeconds(10);
        h.Line("Bob is dead.");
        Assert.Equal(2, h.Pars);
        h.ParReply("Boss", Harness.Row("Boss"), Harness.Invited("Bob"));
        Assert.Empty(h.Uninvites);   // only the leader can uninvite
        Assert.Contains("only the leader can uninvite", h.Cleanup.LastDeathSummary);
    }

    // The game ends our follow on the leader's death with its own line.
    [Fact]
    public void Follower_LeaderDies_NoParIsAsked()
    {
        using Harness h = new();
        h.Line("You are now following Boss.");
        h.ParReply("Boss", Harness.Row("Boss"), Harness.Row("Raijin"));

        h.Line("Boss is dead.");

        Assert.Equal(0, h.Pars);
        Assert.Contains("our leader", h.Cleanup.LastDeathSummary);
    }

    [Theory]
    [InlineData("Stranger is dead.")]                                  // not ours
    [InlineData("The roc hatchling lets out a squeal, and is dead.")]  // a monster
    [InlineData("Raijin has died.")]                                   // no game line
    public void ADeathThatIsNotOurMembers_AsksNothing(string line)
    {
        using Harness h = Leading();
        h.Line(line);
        Assert.Equal(0, h.Pars);
        Assert.Null(h.Cleanup.LastDeathSummary);
    }

    // A pending recruit was invited from the start, never active: not ours to act on.
    [Fact]
    public void PendingRecruitDies_AsksNothing_AndIsNotUninvited()
    {
        using Harness h = Leading();
        h.Line("You have invited Newbie to follow you.");
        h.Line("Newbie is dead.");
        h.ParReply(null, Harness.Row("Raijin"), Harness.Invited("Newbie"));

        Assert.Equal(0, h.Pars);
        Assert.Empty(h.Uninvites);
    }

    // One `par` on its way answers every death seen before its reply.
    [Fact]
    public void ParOnItsWay_IsNotSentAgain()
    {
        using Harness h = Leading();
        h.Line("Raijin is dead.");
        h.Line("Bob is dead.");
        Assert.Equal(1, h.Pars);
        Assert.Contains("already sent", h.Cleanup.LastDeathSummary);

        h.ParReply(null, Harness.Row("Cid"));
        Assert.False(h.OnRoster("Raijin"));
        Assert.False(h.OnRoster("Bob"));

        // Answered, a later death asks again.
        h.Line("Cid is dead.");
        Assert.Equal(2, h.Pars);
    }

    // An unanswered `par` stops counting as on its way once its wait is over.
    [Fact]
    public void UnansweredPar_StopsHoldingRequestsBack()
    {
        using Harness h = Leading();
        h.Line("Raijin is dead.");
        h.Clock += PartyPoller.ParReplyWait;
        h.Line("Bob is dead.");
        Assert.Equal(2, h.Pars);
    }

    // With the timer on, its next `par` due within the window answers the death.
    [Fact]
    public void TimerParDueSoon_NoneIsSent()
    {
        using Harness h = Leading(parOnTimer: true);   // every 10 s, counting from now
        h.Line("Raijin is dead.");                    // its next is 10 s away: one is sent
        Assert.Equal(1, h.Pars);
        h.ParReply(null, Harness.Row("Bob"), Harness.Row("Cid"));

        h.Clock += TimeSpan.FromSeconds(6);
        h.Line("Bob is dead.");                       // that `par` restarted it: 4 s away
        Assert.Equal(1, h.Pars);
        Assert.Contains("timer's next", h.Cleanup.LastDeathSummary);

        h.ParReply(null, Harness.Row("Cid"));          // the timer's `par` answers it
        Assert.False(h.OnRoster("Bob"));
    }

    // A new cadence starts the countdown over: the old one's next `par` was 4 s away,
    // the new one's is 8 s away, so a death asks its own.
    [Fact]
    public void CadenceChanged_CountdownStartsOver()
    {
        using Harness h = Leading(parOnTimer: true);   // every 10 s, counting from now
        h.Clock += TimeSpan.FromSeconds(6);
        h.Poller.ApplyParSettings(new PartySettings { ParPollOnTimer = true, ParPollFrequencySec = 8 });

        h.Line("Raijin is dead.");
        Assert.Equal(1, h.Pars);
    }

    // A disconnect stops the timer and drops the `par` on its way: back in the game,
    // neither is counted on, and a death asks again.
    [Fact]
    public void AfterAReconnect_NoParFromBeforeItIsCountedOn()
    {
        using Harness h = Leading(parOnTimer: true);
        h.Line("Raijin is dead.");
        Assert.Equal(1, h.Pars);                      // unanswered when the link drops

        h.Poller.NotifyDisconnected();
        h.Clock += TimeSpan.FromSeconds(1);
        h.Line("Bob is dead.");                       // out of the game: nothing can go out
        Assert.Equal(1, h.Pars);
        Assert.Contains("no `par` could be sent", h.Cleanup.LastDeathSummary);

        h.Poller.NotifyEnteredRealm();                // the timer starts again from now
        h.Clock += TimeSpan.FromSeconds(1);
        h.Line("Cid is dead.");
        Assert.Equal(2, h.Pars);
    }

    // The master switch holds the `par`; the next reply read still settles the death.
    [Fact]
    public void MasterSwitchOff_HoldsThePar_TheNextReplySettlesIt()
    {
        using Harness h = Leading();
        h.Poller.MasterSwitchOff = () => true;
        h.Line("Raijin is dead.");
        Assert.Equal(0, h.Pars);
        Assert.Contains("no `par` could be sent", h.Cleanup.LastDeathSummary);

        h.ParReply(null, Harness.Row("Bob"), Harness.Row("Cid"));   // the user's own `par`
        Assert.False(h.OnRoster("Raijin"));
        Assert.Empty(h.Cleanup.AwaitingPar);
    }

    // The death line carries the given name; par carries the family name too.
    [Fact]
    public void GivenNameMatch_AcrossDeathLineAndParForms()
    {
        using Harness h = new();
        h.Line("Raijin started to follow you.");
        h.ParReply(null, Harness.Row("Raijin WuzHere"));

        h.Line("Raijin is dead.");
        h.ParReply(null, Harness.Invited("Raijin WuzHere"));

        Assert.Equal(["uninvite Raijin\r"], h.Uninvites);
    }

    // A death no reply ever answers is given up on after the window.
    [Fact]
    public void ADeathNoReplyAnswers_StopsAwaitingThePar()
    {
        using Harness h = Leading();
        h.Poller.MasterSwitchOff = () => true;
        h.Line("Raijin is dead.");
        Assert.Contains("Raijin", h.Cleanup.AwaitingPar);

        h.Clock += h.Cleanup.CleanupWindow + TimeSpan.FromSeconds(1);
        h.Coord.AssertGate(MovementCoordinator.CombatGate);   // any gate change looks again
        Assert.Empty(h.Cleanup.AwaitingPar);
        h.Coord.ClearGate(MovementCoordinator.CombatGate);
        Assert.Empty(h.Cleanup.AwaitingUninvite);
    }

    // Invited again while the fight goes on: the uninvite would undo that invite.
    [Fact]
    public void InvitedAgainBeforeCombatClears_IsNotUninvited()
    {
        using Harness h = Leading();
        h.Coord.AssertGate(MovementCoordinator.CombatGate);
        h.Line("Raijin is dead.");
        h.ParReply(null, Harness.Invited("Raijin"), Harness.Row("Bob"), Harness.Row("Cid"));

        h.Line("You have invited Raijin to follow you.");   // the user's, or AutoParty's
        h.Coord.ClearGate(MovementCoordinator.CombatGate);

        Assert.Empty(h.Uninvites);
        Assert.Empty(h.Cleanup.AwaitingUninvite);
        Assert.Contains("invited again", h.Cleanup.LastDeathSummary);
    }

    // Invited again with the room clear, before any `par` reply: the invite's own
    // [Invited] row must not set the uninvite off.
    [Fact]
    public void InvitedAgainWithTheRoomClear_IsNotUninvited()
    {
        using Harness h = Leading();
        h.Line("Raijin is dead.");
        h.Line("You have invited Raijin to follow you.");
        h.ParReply(null, Harness.Invited("Raijin"), Harness.Row("Bob"), Harness.Row("Cid"));

        Assert.Empty(h.Uninvites);
    }

    // Following again before combat clears: back in the party, not the corpse's slot.
    [Fact]
    public void FollowingAgainBeforeCombatClears_IsNotUninvited()
    {
        using Harness h = Leading();
        h.Coord.AssertGate(MovementCoordinator.CombatGate);
        h.Line("Raijin is dead.");
        h.ParReply(null, Harness.Invited("Raijin"), Harness.Row("Bob"), Harness.Row("Cid"));

        h.Line("Raijin started to follow you.");
        h.Coord.ClearGate(MovementCoordinator.CombatGate);

        Assert.Empty(h.Uninvites);
        Assert.Contains("following again", h.Cleanup.LastDeathSummary);
    }

    // A teleport that splits the party shows the members [Invited] with no death
    // line (user, 2026-10-17): that is never a reason to uninvite or drop anyone.
    [Fact]
    public void TeleportSplit_InvitedWithNoDeathLine_SendsNothingAndKeepsTheMember()
    {
        using Harness h = Leading();
        h.ParReply(null, Harness.Invited("Raijin"), Harness.Row("Bob"), Harness.Row("Cid"));

        Assert.Equal(0, h.Pars);
        Assert.Empty(h.Uninvites);
        Assert.True(h.OnRoster("Raijin"));
        Assert.Null(h.Cleanup.LastDeathSummary);
    }

    // Another member's death in that split doesn't reach the one who wasn't seen to die.
    [Fact]
    public void TeleportSplit_ADeathOfAnother_LeavesTheInvitedMemberAlone()
    {
        using Harness h = Leading();
        h.Line("Bob is dead.");
        h.ParReply(null, Harness.Invited("Raijin"), Harness.Invited("Bob"), Harness.Row("Cid"));

        Assert.Equal(["uninvite Bob\r"], h.Uninvites);
        Assert.True(h.OnRoster("Raijin"));
    }

    // The game's echo of the cleanup's uninvite isn't the user turning the player
    // away: AutoParty still invites them when seen. The echo is the same on both
    // realms (user, 2026-10-17).
    [Theory]
    [InlineData(true)]    // Stock
    [InlineData(false)]   // Paradigm
    public void OwnUninvite_DoesNotSuppressAutoInvite(bool stock)
    {
        using Harness h = Leading();
        h.Manager.OnStock = () => stock;
        h.Players.RecordObservation("Raijin", null, null, null, null, null, null, h.Clock);
        h.Players.EditCustomization("Raijin", new PlayerCustomization(InviteToPartyIfSeen: true, JoinPartyIfInvited: false));
        h.Line("Raijin is dead.");
        h.ParReply(null, Harness.Invited("Raijin"), Harness.Row("Bob"), Harness.Row("Cid"));
        Assert.Equal(["uninvite Raijin\r"], h.Uninvites);
        h.Line("Raijin has been removed from your followers.");

        h.Clock += TimeSpan.FromMinutes(5);
        h.Line("Also here: Raijin.");

        Assert.Contains("invite Raijin\r", h.Wire);
    }

    // One echo answers the client's uninvite; the user's own uninvite of the same
    // player moments later is the user's, and still blocks auto-invite.
    [Fact]
    public void UsersUninvite_RightAfterTheClients_StillSuppresses()
    {
        using Harness h = Leading();
        h.Players.RecordObservation("Raijin", null, null, null, null, null, null, h.Clock);
        h.Players.EditCustomization("Raijin", new PlayerCustomization(InviteToPartyIfSeen: true, JoinPartyIfInvited: false));
        h.Line("Raijin is dead.");
        h.ParReply(null, Harness.Invited("Raijin"), Harness.Row("Bob"), Harness.Row("Cid"));
        h.Line("Raijin has been removed from your followers.");   // the client's

        h.Clock += TimeSpan.FromSeconds(10);
        h.Line("Raijin has been removed from your followers.");   // the user's
        h.Clock += TimeSpan.FromMinutes(5);
        h.Line("Also here: Raijin.");

        Assert.DoesNotContain("invite Raijin\r", h.Wire);
    }

    // Following again while the `par` is still held back: back in the party, so
    // a later [Invited] slot (a teleport split) isn't uninvited for the death.
    [Fact]
    public void FollowingAgainWhileTheParIsHeld_IsNotUninvited()
    {
        using Harness h = Leading();
        h.Poller.MasterSwitchOff = () => true;
        h.Line("Raijin is dead.");
        h.Line("Raijin started to follow you.");
        Assert.Empty(h.Cleanup.AwaitingUninvite);

        h.ParReply(null, Harness.Invited("Raijin"), Harness.Row("Bob"), Harness.Row("Cid"));
        Assert.Empty(h.Uninvites);
    }

    // The user's own uninvite still suppresses, as before.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UsersUninvite_StillSuppressesAutoInvite(bool stock)
    {
        using Harness h = Leading();
        h.Manager.OnStock = () => stock;
        h.Players.RecordObservation("Raijin", null, null, null, null, null, null, h.Clock);
        h.Players.EditCustomization("Raijin", new PlayerCustomization(InviteToPartyIfSeen: true, JoinPartyIfInvited: false));
        h.Line("Raijin has been removed from your followers.");

        h.Clock += TimeSpan.FromMinutes(5);
        h.Line("Also here: Raijin.");

        Assert.DoesNotContain("invite Raijin\r", h.Wire);
    }

    // A death no reply settles inside the window is dropped, not acted on late.
    [Fact]
    public void ExpiredDeath_IsNotActedOn()
    {
        using Harness h = Leading();
        h.Line("Raijin is dead.");
        h.Clock += h.Cleanup.CleanupWindow + TimeSpan.FromSeconds(1);
        h.ParReply(null, Harness.Invited("Raijin"), Harness.Row("Bob"), Harness.Row("Cid"));

        Assert.Empty(h.Uninvites);
    }

    [Fact]
    public void Dispose_StopsReactingToDeath()
    {
        using Harness h = Leading();
        h.Cleanup.Dispose();
        h.Line("Raijin is dead.");
        Assert.Equal(0, h.Pars);
    }

    [Fact]
    public void Decisions_AreLoggedAtInfo()
    {
        using Harness h = Leading();
        List<LogEntry> rows = new();
        h.Log.EntryAdded += rows.Add;
        h.Line("Raijin is dead.");
        h.ParReply(null, Harness.Row("Bob"), Harness.Row("Cid"));

        Assert.Contains(rows, r => r.Source == PartyDeathRosterCleanup.LogCategory
            && r.Severity == LogSeverity.Info && r.Message.Contains("`par` sent", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Source == PartyDeathRosterCleanup.LogCategory
            && r.Severity == LogSeverity.Info && r.Message.Contains("no longer lists Raijin", StringComparison.Ordinal));
    }
}
