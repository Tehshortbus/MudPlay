using FujinTerm.Game.Conditions;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.D — <see cref="ConditionTracker"/> matches lines against the
/// active <see cref="MessageStore"/> records' AppliedMessage /
/// AppliedEndsWith pairs and surfaces the aggregated
/// <see cref="MessageFlags"/>.
/// </summary>
public sealed class ConditionTrackerTests
{
    private sealed class Harness : IDisposable
    {
        public LogService Log { get; } = new();
        public MessageStore Messages { get; } = new();
        public ConditionTracker Tracker { get; }
        public List<MessageRecord> Applied { get; } = new();
        public List<MessageRecord> Ended { get; } = new();

        public Harness()
        {
            Tracker = new ConditionTracker(Messages, Log);
            Tracker.ConditionApplied += Applied.Add;
            Tracker.ConditionEnded   += Ended.Add;
        }

        public void Feed(string text)
        {
            // The tracker subscribes to LineExtractor in real life;
            // tests reflect into the private OnLine via a public
            // helper would be overkill. Instead, attach a fake
            // extractor and dispatch through it.
            // Simpler: keep the private OnLine internal-via-friend
            // by exposing FeedTestLine. Use the same pattern other
            // engines have.
            var emitted = new LineExtractor.EmittedLine(
                text, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false);
            typeof(ConditionTracker)
                .GetMethod("OnLine",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .Invoke(Tracker, new object[] { emitted });
        }

        public void Dispose() => Tracker.Dispose();
    }

    private static MessageRecord MakeRecord(
        string name,
        MessageFlags flags,
        string applied,
        string endsWith,
        MessageAction action = MessageAction.Ignore)
    {
        return new MessageRecord(
            Id: MessageRecord.ComputeId(name, "", "", "", applied, endsWith),
            Name: name,
            Action: action,
            Flags: flags,
            RawFlagsHex: (ushort)flags,
            Response: string.Empty,
            CasterMessage: string.Empty,
            TargetMessage: string.Empty,
            WitnessMessage: string.Empty,
            AppliedMessage: applied,
            AppliedEndsWith: endsWith);
    }

    // ----- applied / ends pair ---------------------------------------

    [Fact]
    public void AppliedMessage_SetsActiveFlags()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("Poison",
            MessageFlags.Poisoned | MessageFlags.LosingHp,
            applied: "You have been poisoned!",
            endsWith: "The poison wears off."));

        h.Feed("You have been poisoned!");

        Assert.True(h.Tracker.IsPoisoned);
        Assert.True(h.Tracker.IsLosingHp);
        Assert.Single(h.Applied);
    }

    [Fact]
    public void EndsWith_ClearsActiveFlags()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("Poison",
            MessageFlags.Poisoned,
            applied: "You have been poisoned!",
            endsWith: "The poison wears off."));

        h.Feed("You have been poisoned!");
        Assert.True(h.Tracker.IsPoisoned);

        h.Feed("The poison wears off.");
        Assert.False(h.Tracker.IsPoisoned);
        Assert.Single(h.Ended);
    }

    [Fact]
    public void MultipleConditions_OrFlags()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("Poison",
            MessageFlags.Poisoned, "poisoned!", "poison wears off."));
        h.Messages.Messages.Add(MakeRecord("Blind",
            MessageFlags.Blinded,  "blinded!",  "vision returns."));

        h.Feed("You have been poisoned!");
        h.Feed("You have been blinded!");

        Assert.True(h.Tracker.IsPoisoned);
        Assert.True(h.Tracker.IsBlinded);
        Assert.Equal(MessageFlags.Poisoned | MessageFlags.Blinded,
                     h.Tracker.ActiveFlags);
    }

    [Fact]
    public void EndsOneCondition_KeepsOthers()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("Poison",
            MessageFlags.Poisoned, "poisoned!", "poison wears off."));
        h.Messages.Messages.Add(MakeRecord("Blind",
            MessageFlags.Blinded,  "blinded!",  "vision returns."));

        h.Feed("You have been poisoned!");
        h.Feed("You have been blinded!");

        h.Feed("Your vision returns.");

        Assert.True(h.Tracker.IsPoisoned);
        Assert.False(h.Tracker.IsBlinded);
    }

    // ----- idempotency -----------------------------------------------

    [Fact]
    public void SameAppliedTwice_NoDuplicateEvent()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("Poison",
            MessageFlags.Poisoned, "poisoned!", "poison wears off."));

        h.Feed("You have been poisoned!");
        h.Feed("You have been poisoned!");

        Assert.Single(h.Applied);
    }

    [Fact]
    public void EndsWithBeforeApplied_NoOp()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("Poison",
            MessageFlags.Poisoned, "poisoned!", "poison wears off."));

        h.Feed("The poison wears off.");

        Assert.False(h.Tracker.IsPoisoned);
        Assert.Empty(h.Ended);
    }

    // ----- index rebuild on store changes ----------------------------

    [Fact]
    public void IndexRebuilds_OnStoreAdd()
    {
        using Harness h = new();
        h.Feed("You have been poisoned!");     // no record yet → no fire
        Assert.False(h.Tracker.IsPoisoned);

        h.Messages.Messages.Add(MakeRecord("Poison",
            MessageFlags.Poisoned, "poisoned!", "poison wears off."));
        h.Feed("You have been poisoned!");      // now matches

        Assert.True(h.Tracker.IsPoisoned);
    }

    [Fact]
    public void IndexRebuilds_DropsStaleActiveOnStoreReplace()
    {
        using Harness h = new();
        MessageRecord poison = MakeRecord("Poison",
            MessageFlags.Poisoned, "poisoned!", "poison wears off.");
        h.Messages.Messages.Add(poison);

        h.Feed("You have been poisoned!");
        Assert.True(h.Tracker.IsPoisoned);

        h.Messages.Messages.Clear();    // user deleted the record
        Assert.False(h.Tracker.IsPoisoned);
        Assert.Equal(MessageFlags.None, h.Tracker.ActiveFlags);
    }

    // ----- manual clear ----------------------------------------------

    [Fact]
    public void ClearAll_Resets()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("Poison",
            MessageFlags.Poisoned, "poisoned!", "poison wears off."));

        h.Feed("You have been poisoned!");
        Assert.True(h.Tracker.IsPoisoned);

        h.Tracker.ClearAll();
        Assert.False(h.Tracker.IsPoisoned);
        Assert.Equal(MessageFlags.None, h.Tracker.ActiveFlags);
    }
}
