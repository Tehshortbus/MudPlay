using System.Text;
using MudPlay.Game;
using MudPlay.Game.Conditions;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Outbound ailment-sync (AilmentSyncEngine): on a local VERBOSE ailment
// (blind / confuse / disease / held), announce a BARE '.@blind' on say (MegaMUD
// parity — no 'on'/'off' suffix) so other clients mirror our state; the four
// curable ailments also @wait the leader. On CLEAR the engine says NOTHING (no
// 'off' token) — the receiver clears the chip via a witnessed cure / spell-data
// duration / par P drop / @status reconcile — it only @ok's the leader to release
// the wait. POISON is NOT announced (an observer reads it from the par P flag) but
// still telepaths its @wait. The say only fires when in a party AND no cure spell
// is configured for that ailment; each Ignore<X> is the single per-ailment gate
// that suppresses BOTH the say AND the @wait. Held telepaths @wait like the curable
// four (plus its '.@held' say), balanced by @ok on clear; it has no Ignore gate.
public sealed class AilmentSyncEngineTests
{
    private sealed class Harness : IDisposable
    {
        public MessageStore Messages { get; } = new();
        public ConditionTracker Tracker { get; }
        // The game's output, fed the way the client receives it.
        public ServerFeed Server { get; } = new();
        public PartyState Party { get; } = new();
        public PartyRestSync Rest { get; }
        public AilmentSyncEngine Engine { get; }
        public SpellsSettings Spells { get; set; } = new();

        // Ailment flags the player has a cure spell configured for.
        public MessageFlags CureConfigured { get; set; } = MessageFlags.None;

        // Say-channel wire (engine's own sender).
        public List<string> Say { get; } = new();

        // Telepath wire (@wait / @ok via PartyRestSync).
        public List<string> Telepath { get; } = new();

        public Harness()
        {
            Tracker = new ConditionTracker(Messages, null);
            Tracker.AttachLineExtractor(Server.Lines);
            Rest = new PartyRestSync(Party);
            Rest.SetWireSender(b => Telepath.Add(Encoding.Latin1.GetString(b)));
            Engine = new AilmentSyncEngine(
                Tracker, Rest, () => Spells,
                isInParty: () => Party.IsInParty,
                hasCureConfigured: f => (CureConfigured & f) != MessageFlags.None,
                log: null);
            Engine.SetWireSender(b => Say.Add(Encoding.Latin1.GetString(b)));

            // Follower in a party so @wait / @ok can fire.
            Party.IsInParty = true;
            Party.LeaderName = "Leader";
        }

        public void Feed(string text) => Server.Line(text);

        public void Dispose()
        {
            Engine.Dispose();
            Rest.Dispose();
            Tracker.Dispose();
        }
    }

    private static MessageRecord Ailment(string name, MessageFlags flags, string applied, string ends) =>
        new(
            Id: MessageRecord.ComputeId(name, "", "", "", applied, ends),
            Name: name,
            Flags: flags,
            RawFlagsHex: (ushort)flags,
            CasterMessage: string.Empty,
            TargetMessage: string.Empty,
            WitnessMessage: string.Empty,
            AppliedMessage: applied,
            AppliedEndsWith: ends);

    private static void SeedAll(Harness h)
    {
        h.Messages.Messages.Add(Ailment("Poison",  MessageFlags.Poisoned, "poisoned!", "poison wears off."));
        h.Messages.Messages.Add(Ailment("Blind",   MessageFlags.Blinded,  "blinded!",  "vision returns."));
        h.Messages.Messages.Add(Ailment("Confuse", MessageFlags.Confused, "confused!", "head clears."));
        h.Messages.Messages.Add(Ailment("Disease", MessageFlags.Diseased, "diseased!", "disease fades."));
        h.Messages.Messages.Add(Ailment("Held",    MessageFlags.MovementPrevented, "cannot move!", "can move again."));
    }

    [Fact]
    public void Poisoned_WaitsButDoesNotSay()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You have been poisoned!");

        // Poison is NOT announced verbosely — an observer reads it from the par
        // `P` flag. It still telepaths its @wait to the leader.
        Assert.Empty(h.Say);
        Assert.Equal($"/Leader @wait {PartyRestSync.ConditionNote}\r", Assert.Single(h.Telepath));
    }

    [Theory]
    [InlineData("blinded!",  ".@blind\r")]
    [InlineData("confused!", ".@confused\r")]
    [InlineData("diseased!", ".@diseased\r")]
    public void EachAilment_UsesItsSayToken(string applied, string expected)
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You are " + applied);

        Assert.Equal(expected, Assert.Single(h.Say));
    }

    [Fact]
    public void Ignore_SuppressesBothSayAndWait()
    {
        // Ignore<X> is the single per-ailment gate: it suppresses BOTH the say
        // announce AND the @wait telepath (one "I don't care about this" toggle).
        using Harness h = new();
        SeedAll(h);
        h.Spells = new SpellsSettings { IgnoreBlindness = true };

        h.Feed("You have been blinded!");

        Assert.Empty(h.Say);
        Assert.Empty(h.Telepath);
    }

    [Fact]
    public void Cleared_AfterWait_SendsOk()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You have been poisoned!");
        h.Feed("The poison wears off.");

        // @wait then @ok on the telepath channel.
        Assert.Equal(new[] { $"/Leader @wait {PartyRestSync.ConditionNote}\r", "/Leader @ok\r" }, h.Telepath);
        // Poison is not announced on say (observers read the par `P` flag) — so
        // nothing lands on the say wire on apply or clear.
        Assert.Empty(h.Say);
    }

    [Fact]
    public void Cleared_SendsBareApplyOnly_NoOffOnSay()
    {
        // Blindness (verbose): the bare apply token is the ONLY say that ever goes
        // out — there's no '.@blind off' on clear (MegaMUD parity). A receiver clears
        // the chip via cure / duration / @status / @ok instead. The @wait/@ok telepath
        // still balances on the leader channel.
        using Harness h = new();
        SeedAll(h);

        h.Feed("You have been blinded!");
        h.Feed("Your vision returns.");

        Assert.Equal(new[] { ".@blind\r" }, h.Say);   // apply only, no off
        Assert.Equal(new[] { $"/Leader @wait {PartyRestSync.BlindNote}\r", "/Leader @ok\r" }, h.Telepath);
    }

    [Fact]
    public void TwoAilments_OneWait_OkOnlyWhenBothClear()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You have been poisoned!");
        h.Feed("You have been blinded!");
        // Two ailments, but a single @wait holds the leader.
        Assert.Single(h.Telepath);

        h.Feed("The poison wears off.");
        Assert.Single(h.Telepath);   // blind still holds

        h.Feed("Your vision returns.");
        Assert.Equal(new[] { $"/Leader @wait {PartyRestSync.ConditionNote}\r", "/Leader @ok\r" }, h.Telepath);
    }

    [Fact]
    public void IgnoredAilment_DoesNotHoldOrTriggerOk()
    {
        using Harness h = new();
        SeedAll(h);
        h.Spells = new SpellsSettings { IgnorePoison = true };   // push through poison

        h.Feed("You have been poisoned!");  // ignored → no @wait reason placed
        h.Feed("You have been blinded!");    // not ignored → @wait
        Assert.Equal($"/Leader @wait {PartyRestSync.BlindNote}\r", Assert.Single(h.Telepath));

        h.Feed("The poison wears off.");      // ignored poison clears → must NOT emit @ok
        Assert.Equal($"/Leader @wait {PartyRestSync.BlindNote}\r", Assert.Single(h.Telepath));

        h.Feed("Your vision returns.");       // last NON-ignored clears → @ok now
        Assert.Equal(new[] { $"/Leader @wait {PartyRestSync.BlindNote}\r", "/Leader @ok\r" }, h.Telepath);
    }

    [Fact]
    public void Held_AnnouncesSay_AndTelepathsWait()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You cannot move!");

        // Held broadcasts a bare '.@held' on set (MegaMUD parity — no 'on'/'off'
        // suffix) AND telepaths @wait to the leader, like the curable four: the say
        // lights the member's chip on the receiver, the @wait pauses the leader.
        Assert.Equal(".@held\r", Assert.Single(h.Say));
        Assert.Equal($"/Leader @wait {PartyRestSync.HeldNote}\r", Assert.Single(h.Telepath));
    }

    [Fact]
    public void Held_Cleared_SendsNoSayOff_AndWaitOkTelepaths()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You cannot move!");
        h.Feed("You can move again.");

        // No '.@held off' on clear — MegaMUD sends no 'off' token. The bare '.@held'
        // apply is the only say that ever goes out; observers clear the HELD badge via
        // a witnessed cure, the spell-data duration timing out, or a @status reconcile
        // (reports paradigm-20260820-122200 / -153540). On the leader channel the
        // @wait (apply) is balanced by the @ok (clear).
        Assert.Equal(new[] { ".@held\r" }, h.Say);
        Assert.Equal(new[] { $"/Leader @wait {PartyRestSync.HeldNote}\r", "/Leader @ok\r" }, h.Telepath);
    }

    [Fact]
    public void HeldThenPoisoned_OkOnlyWhenBothClear()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You cannot move!");       // held: .@held say + @wait (0→1 transition)
        Assert.Equal($"/Leader @wait {PartyRestSync.HeldNote}\r", Assert.Single(h.Telepath));

        h.Feed("You have been poisoned!"); // poison: no say (par-driven); @wait already held (1→2, no new telepath)
        Assert.Equal($"/Leader @wait {PartyRestSync.HeldNote}\r", Assert.Single(h.Telepath));

        h.Feed("You can move again.");      // Held clears; poison still holds → no @ok yet
        Assert.Equal($"/Leader @wait {PartyRestSync.HeldNote}\r", Assert.Single(h.Telepath));

        h.Feed("The poison wears off.");    // last reason clears → @ok
        Assert.Equal(new[] { $"/Leader @wait {PartyRestSync.HeldNote}\r", "/Leader @ok\r" }, h.Telepath);
    }

    [Fact]
    public void NotInParty_SuppressesSay()
    {
        using Harness h = new();
        SeedAll(h);
        h.Party.IsInParty = false;

        h.Feed("You have been poisoned!");

        // Out of a party there's no one to tell — no say, and CanSignal also
        // blocks the @wait on the wire.
        Assert.Empty(h.Say);
        Assert.Empty(h.Telepath);
    }

    [Fact]
    public void CureConfigured_SuppressesSay_ButWaitStillFires()
    {
        using Harness h = new();
        SeedAll(h);
        h.CureConfigured = MessageFlags.Poisoned;   // we self-cure poison

        h.Feed("You have been poisoned!");

        // We can clear it ourselves, so no broadcast — but the @wait still
        // pauses the leader while we cast (the cure gate is say-only).
        Assert.Empty(h.Say);
        Assert.Equal($"/Leader @wait {PartyRestSync.ConditionNote}\r", Assert.Single(h.Telepath));
    }

    [Fact]
    public void CureHoldsConfigured_SuppressesHeldSay_ButWaitStillFires()
    {
        using Harness h = new();
        SeedAll(h);
        h.CureConfigured = MessageFlags.MovementPrevented;  // we self-cure holds

        h.Feed("You cannot move!");
        h.Feed("You can move again.");

        // Self-cure: the '.@held' say is suppressed (the cure gate is say-only), but
        // the @wait still pauses the leader while we cast — same as the curable four —
        // balanced by the @ok on clear.
        Assert.Empty(h.Say);
        Assert.Equal(new[] { $"/Leader @wait {PartyRestSync.HeldNote}\r", "/Leader @ok\r" }, h.Telepath);
    }

    [Fact]
    public void NoSayWireSender_TheAnnounceStillPlacesTheWait()
    {
        // Engine with no say sender bound: blindness is announced on say, and with
        // nowhere to say it the announce path still runs on to the @wait.
        MessageStore messages = new();
        ConditionTracker tracker = new(messages, null);
        ServerFeed feed = new();
        tracker.AttachLineExtractor(feed.Lines);
        PartyState party = new();
        PartyRestSync rest = new(party);
        List<string> telepath = new();
        rest.SetWireSender(b => telepath.Add(Encoding.Latin1.GetString(b)));
        party.IsInParty = true;
        party.LeaderName = "Leader";
        using AilmentSyncEngine engine = new(
            tracker, rest, () => new SpellsSettings(),
            isInParty: () => party.IsInParty,
            hasCureConfigured: _ => false,
            log: null);
        messages.Messages.Add(Ailment("Blind", MessageFlags.Blinded, "blinded!", "vision returns."));

        feed.Line("You have been blinded!");

        Assert.Equal($"/Leader @wait {PartyRestSync.BlindNote}\r", Assert.Single(telepath));
        tracker.Dispose();
        rest.Dispose();
    }

    // ===== Mid-affliction Ignore-toggle reconcile (ReevaluateWaits) =====
    //
    // OnConditionsChanged latches the @wait decision at onset. Toggling an
    // Ignore<X> gate while still afflicted must reconcile the standing wait —
    // the live report was "flip IgnorePoison on while poisoned and the party
    // never resumes". SpellsSectionViewModel.Apply calls ReevaluateWaits after
    // pushing the new settings.

    [Fact]
    public void ReevaluateWaits_TogglingIgnoreOnMidPoison_ReleasesWait()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You have been poisoned!");
        Assert.Equal($"/Leader @wait {PartyRestSync.ConditionNote}\r", Assert.Single(h.Telepath));

        // Flip IgnorePoison ON while still poisoned. Without the reconcile the
        // already-telepathed @wait stands and the leader is stuck.
        h.Spells.IgnorePoison = true;
        h.Engine.ReevaluateWaits();

        Assert.Equal(new[] { $"/Leader @wait {PartyRestSync.ConditionNote}\r", "/Leader @ok\r" }, h.Telepath);
    }

    [Fact]
    public void ReevaluateWaits_TogglingIgnoreOffMidPoison_PlacesWait()
    {
        using Harness h = new();
        SeedAll(h);
        h.Spells = new SpellsSettings { IgnorePoison = true };

        h.Feed("You have been poisoned!");
        Assert.Empty(h.Telepath);   // ignored at onset — no @wait
        Assert.Empty(h.Say);        // poison is never said

        // Flip IgnorePoison OFF while still poisoned → (re)place the wait.
        h.Spells.IgnorePoison = false;
        h.Engine.ReevaluateWaits();

        Assert.Equal($"/Leader @wait {PartyRestSync.ConditionNote}\r", Assert.Single(h.Telepath));
    }

    [Fact]
    public void ReevaluateWaits_NoSettingChange_IsNoOpOnWire()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You have been poisoned!");
        Assert.Single(h.Telepath);   // the onset @wait

        // Reconciling without toggling anything must not double-@wait or @ok —
        // PartyRestSync dedupes the standing reason.
        h.Engine.ReevaluateWaits();

        Assert.Single(h.Telepath);
    }

    // ===== Following a leader while still afflicted =====
    //
    // A held follower drops out of the party when the leader moves on. When the
    // leader comes back and we follow again, the say and the @wait are given again
    // so the party can cure us (report paradigm-20261014-214037).

    // Left behind: out of the party, as PartyManager leaves it.
    private static void LeaveParty(Harness h)
    {
        h.Party.IsInParty = false;
        h.Party.LeaderName = null;
    }

    // "You are now following X." — the state PartyManager.OnYouFollowing sets, then
    // its FollowingLeader event.
    private static void Follow(Harness h, string leader)
    {
        h.Party.IsInParty = true;
        h.Party.LeaderName = leader;
        h.Party.SelfIsLeader = false;
        h.Engine.NoteFollowingLeader(leader);
    }

    [Fact]
    public void Held_RejoiningTheLeader_SaysHeldAndWaitsAgain()
    {
        using Harness h = new();
        SeedAll(h);
        h.Feed("You cannot move!");
        LeaveParty(h);
        h.Say.Clear();
        h.Telepath.Clear();

        Follow(h, "Leader");

        Assert.Equal(".@held\r", Assert.Single(h.Say));
        Assert.Equal($"/Leader @wait {PartyRestSync.HeldNote}\r", Assert.Single(h.Telepath));
        Assert.NotNull(h.Engine.LastFollowAnnounce);
    }

    // Held and poisoned: the @wait carries the hold's note, the reason we stand there.
    [Fact]
    public void HeldAndPoisoned_FollowingAgain_WaitsWithTheHoldsNote()
    {
        using Harness h = new();
        SeedAll(h);
        h.Feed("You have been poisoned!");
        h.Feed("You cannot move!");
        LeaveParty(h);
        h.Telepath.Clear();

        Follow(h, "Leader");

        Assert.Equal($"/Leader @wait {PartyRestSync.HeldNote}\r", Assert.Single(h.Telepath));
    }

    [Fact]
    public void HoldClearedBeforeRejoining_SendsNothing()
    {
        using Harness h = new();
        SeedAll(h);
        h.Feed("You cannot move!");
        LeaveParty(h);
        h.Feed("You can move again.");
        h.Say.Clear();
        h.Telepath.Clear();

        Follow(h, "Leader");

        Assert.Empty(h.Say);
        Assert.Empty(h.Telepath);
        Assert.Null(h.Engine.LastFollowAnnounce);
    }

    [Fact]
    public void Held_WithOwnCureConfigured_RejoiningWaitsButDoesNotSay()
    {
        using Harness h = new();
        SeedAll(h);
        h.CureConfigured = MessageFlags.MovementPrevented;
        h.Feed("You cannot move!");
        LeaveParty(h);
        h.Telepath.Clear();

        Follow(h, "Leader");

        Assert.Empty(h.Say);
        Assert.Equal($"/Leader @wait {PartyRestSync.HeldNote}\r", Assert.Single(h.Telepath));
    }

    [Fact]
    public void PoisonedAndBlind_Rejoining_SaysBlindOnly_OneWait()
    {
        using Harness h = new();
        SeedAll(h);
        h.Feed("You have been poisoned!");
        h.Feed("You have been blinded!");
        LeaveParty(h);
        h.Say.Clear();
        h.Telepath.Clear();

        Follow(h, "Leader");

        // Poison is never said (par carries it); one @wait with the first reason's note.
        Assert.Equal(".@blind\r", Assert.Single(h.Say));
        Assert.Equal($"/Leader @wait {PartyRestSync.ConditionNote}\r", Assert.Single(h.Telepath));
    }

    [Fact]
    public void IgnoredAilment_Rejoining_SendsNothing()
    {
        using Harness h = new();
        SeedAll(h);
        h.Spells = new SpellsSettings { IgnoreBlindness = true };
        h.Feed("You have been blinded!");
        LeaveParty(h);

        Follow(h, "Leader");

        Assert.Empty(h.Say);
        Assert.Empty(h.Telepath);
    }

    [Fact]
    public void MasterSwitchOff_Rejoining_SendsNothing()
    {
        using Harness h = new();
        SeedAll(h);
        h.Feed("You cannot move!");
        LeaveParty(h);
        h.Say.Clear();
        h.Telepath.Clear();
        h.Engine.MasterSwitchOff = () => true;
        h.Rest.MasterSwitchOff = () => true;

        Follow(h, "Leader");

        Assert.Empty(h.Say);
        Assert.Empty(h.Telepath);
    }

    [Fact]
    public void Held_FollowingANewLeader_TellsThem()
    {
        using Harness h = new();
        SeedAll(h);
        h.Feed("You cannot move!");
        h.Say.Clear();
        h.Telepath.Clear();

        Follow(h, "Other");

        Assert.Equal(".@held\r", Assert.Single(h.Say));
        Assert.Equal($"/Other @wait {PartyRestSync.HeldNote}\r", Assert.Single(h.Telepath));
    }
}
