using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Conditions;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Outbound ailment-sync (<see cref="AilmentSyncEngine"/>): on a local
/// curable ailment, announce <c>.@poisoned</c> etc. on say (so other
/// FujinTerm clients mirror our state) and <c>@wait</c> the leader; on
/// clear, <c>@ok</c> the leader. The say only fires when in a party AND no
/// cure spell is configured for that ailment; DoNotAnnounce* further gates
/// the say, Ignore* gates the @wait — independently. Held announces
/// <c>.@held</c> but never telepaths <c>@wait</c> (its pause rides the say).
/// </summary>
public sealed class AilmentSyncEngineTests
{
    private sealed class Harness : IDisposable
    {
        public MessageStore Messages { get; } = new();
        public ConditionTracker Tracker { get; }
        public PartyState Party { get; } = new();
        public PartyRestSync Rest { get; }
        public AilmentSyncEngine Engine { get; }
        public SpellsSettings Spells { get; set; } = new();

        /// <summary>Ailment flags the player has a cure spell configured for.</summary>
        public MessageFlags CureConfigured { get; set; } = MessageFlags.None;

        /// <summary>Say-channel wire (engine's own sender).</summary>
        public List<string> Say { get; } = new();

        /// <summary>Telepath wire (@wait / @ok via PartyRestSync).</summary>
        public List<string> Telepath { get; } = new();

        public Harness()
        {
            Tracker = new ConditionTracker(Messages, null);
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

        public void Feed(string text)
        {
            var emitted = new LineExtractor.EmittedLine(
                text, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false);
            typeof(ConditionTracker)
                .GetMethod("OnLine",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .Invoke(Tracker, new object[] { emitted });
        }

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
            Action: MessageAction.Ignore,
            Flags: flags,
            RawFlagsHex: (ushort)flags,
            Response: string.Empty,
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
    public void Poisoned_AnnouncesSayAndWaits()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You have been poisoned!");

        Assert.Equal(".@poisoned\r", Assert.Single(h.Say));
        Assert.Equal("/Leader @wait\r", Assert.Single(h.Telepath));
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
    public void DoNotAnnounce_SuppressesSay_ButWaitStillFires()
    {
        using Harness h = new();
        SeedAll(h);
        h.Spells = new SpellsSettings { DoNotAnnouncePoison = true };

        h.Feed("You have been poisoned!");

        Assert.Empty(h.Say);
        Assert.Equal("/Leader @wait\r", Assert.Single(h.Telepath));
    }

    [Fact]
    public void Ignore_SuppressesWait_ButSayStillFires()
    {
        using Harness h = new();
        SeedAll(h);
        h.Spells = new SpellsSettings { IgnorePoison = true };

        h.Feed("You have been poisoned!");

        Assert.Equal(".@poisoned\r", Assert.Single(h.Say));
        Assert.Empty(h.Telepath);
    }

    [Fact]
    public void Cleared_AfterWait_SendsOk()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You have been poisoned!");
        h.Feed("The poison wears off.");

        // @wait then @ok; no clear-side say announce.
        Assert.Equal(new[] { "/Leader @wait\r", "/Leader @ok\r" }, h.Telepath);
        Assert.Single(h.Say);   // only the apply-side announce
    }

    [Fact]
    public void Cleared_WhenWaitWasIgnored_NoOk()
    {
        using Harness h = new();
        SeedAll(h);
        h.Spells = new SpellsSettings { IgnorePoison = true };

        h.Feed("You have been poisoned!");
        h.Feed("The poison wears off.");

        // Never @waited (ignored), so nothing to @ok.
        Assert.Empty(h.Telepath);
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
        Assert.Equal(new[] { "/Leader @wait\r", "/Leader @ok\r" }, h.Telepath);
    }

    [Fact]
    public void Held_AnnouncesSay_ButNeverTelepathsWait()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You cannot move!");

        // .@held doubles as the "cure my hold" identifier; the leader-pause
        // is driven by that say on the receiving side, so NO @wait telepath.
        Assert.Equal(".@held\r", Assert.Single(h.Say));
        Assert.Empty(h.Telepath);
    }

    [Fact]
    public void Held_Cleared_SendsOk()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You cannot move!");
        h.Feed("You can move again.");

        // The silent Held reason still balances: @ok releases the say-driven
        // pause on clear, even though no @wait was ever telepathed.
        Assert.Equal("/Leader @ok\r", Assert.Single(h.Telepath));
    }

    [Fact]
    public void HeldThenPoisoned_OkOnlyWhenBothClear()
    {
        using Harness h = new();
        SeedAll(h);

        h.Feed("You cannot move!");       // silent Held reason, .@held say
        h.Feed("You have been poisoned!"); // poison: say + (suppressed) @wait — leader already paused via @held
        Assert.Empty(h.Telepath);          // no @wait telepath yet (Held holds the 0→1 slot silently)

        h.Feed("You can move again.");      // Held clears; poison still holds
        Assert.Empty(h.Telepath);

        h.Feed("The poison wears off.");    // last reason clears → @ok
        Assert.Equal("/Leader @ok\r", Assert.Single(h.Telepath));
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
        Assert.Equal("/Leader @wait\r", Assert.Single(h.Telepath));
    }

    [Fact]
    public void CureHoldsConfigured_SuppressesHeldSay_AndNoOk()
    {
        using Harness h = new();
        SeedAll(h);
        h.CureConfigured = MessageFlags.MovementPrevented;  // we self-cure holds

        h.Feed("You cannot move!");
        h.Feed("You can move again.");

        // Self-cure: nothing announced, and because the Held reason is only
        // registered when we announce, there's no @ok either.
        Assert.Empty(h.Say);
        Assert.Empty(h.Telepath);
    }

    [Fact]
    public void NoSayWireSender_DoesNotThrow()
    {
        // Engine with no say sender bound — the announce path must no-op
        // silently rather than NRE.
        MessageStore messages = new();
        ConditionTracker tracker = new(messages, null);
        PartyState party = new();
        PartyRestSync rest = new(party);
        party.IsInParty = true;
        party.LeaderName = "Leader";
        using AilmentSyncEngine engine = new(
            tracker, rest, () => new SpellsSettings(),
            isInParty: () => party.IsInParty,
            hasCureConfigured: _ => false,
            log: null);
        messages.Messages.Add(Ailment("Poison", MessageFlags.Poisoned, "poisoned!", "poison wears off."));

        var emitted = new LineExtractor.EmittedLine(
            "You have been poisoned!", Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false);
        typeof(ConditionTracker)
            .GetMethod("OnLine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(tracker, new object[] { emitted });

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
        Assert.Equal("/Leader @wait\r", Assert.Single(h.Telepath));

        // Flip IgnorePoison ON while still poisoned. Without the reconcile the
        // already-telepathed @wait stands and the leader is stuck.
        h.Spells.IgnorePoison = true;
        h.Engine.ReevaluateWaits();

        Assert.Equal(new[] { "/Leader @wait\r", "/Leader @ok\r" }, h.Telepath);
    }

    [Fact]
    public void ReevaluateWaits_TogglingIgnoreOffMidPoison_PlacesWait()
    {
        using Harness h = new();
        SeedAll(h);
        h.Spells = new SpellsSettings { IgnorePoison = true };

        h.Feed("You have been poisoned!");
        Assert.Empty(h.Telepath);   // ignored at onset — say fired, no @wait

        // Flip IgnorePoison OFF while still poisoned → (re)place the wait.
        h.Spells.IgnorePoison = false;
        h.Engine.ReevaluateWaits();

        Assert.Equal("/Leader @wait\r", Assert.Single(h.Telepath));
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
}
