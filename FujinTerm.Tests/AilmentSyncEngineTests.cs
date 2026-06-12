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
/// clear, <c>@ok</c> the leader. DoNotAnnounce* gates the say,
/// Ignore* gates the @wait — independently.
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
        public OtherSettings Other { get; set; } = new();

        /// <summary>Say-channel wire (engine's own sender).</summary>
        public List<string> Say { get; } = new();

        /// <summary>Telepath wire (@wait / @ok via PartyRestSync).</summary>
        public List<string> Telepath { get; } = new();

        public Harness()
        {
            Tracker = new ConditionTracker(Messages, null);
            Rest = new PartyRestSync(Party);
            Rest.SetWireSender(b => Telepath.Add(Encoding.Latin1.GetString(b)));
            Engine = new AilmentSyncEngine(Tracker, Rest, () => Other, null);
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
        h.Other = new OtherSettings { DoNotAnnouncePoison = true };

        h.Feed("You have been poisoned!");

        Assert.Empty(h.Say);
        Assert.Equal("/Leader @wait\r", Assert.Single(h.Telepath));
    }

    [Fact]
    public void Ignore_SuppressesWait_ButSayStillFires()
    {
        using Harness h = new();
        SeedAll(h);
        h.Other = new OtherSettings { IgnorePoison = true };

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
        h.Other = new OtherSettings { IgnorePoison = true };

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
    public void NoSayWireSender_DoesNotThrow()
    {
        // Engine with no say sender bound — the announce path must no-op
        // silently rather than NRE.
        MessageStore messages = new();
        ConditionTracker tracker = new(messages, null);
        PartyState party = new();
        PartyRestSync rest = new(party);
        using AilmentSyncEngine engine = new(tracker, rest, () => new OtherSettings(), null);
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
}
