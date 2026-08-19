using MudPlay.Game.Conditions;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

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
        public List<MessageRecord> ActionFailed { get; } = new();

        public Harness()
        {
            Tracker = new ConditionTracker(Messages, Log);
            Tracker.ConditionApplied += Applied.Add;
            Tracker.ConditionEnded   += Ended.Add;
            Tracker.ActionFailed     += ActionFailed.Add;
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
        string endsWith)
    {
        return new MessageRecord(
            Id: MessageRecord.ComputeId(name, "", "", "", applied, endsWith),
            Name: name,
            Flags: flags,
            RawFlagsHex: (ushort)flags,
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

    [Theory]
    [InlineData("You feel lucky! (411s)")]     // seconds readout (from report 232454)
    [InlineData("You feel lucky! (6m 51s)")]    // longer buff, minutes+seconds form
    public void StatReadout_WithRemainingTime_DoesNotApply(string readout)
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("Bless",
            MessageFlags.None,
            applied: "You feel lucky!",
            endsWith: "You no longer feel lucky!"));

        // A `stat` status readout of an already-up buff (trailing "(Ns)") is NOT a fresh
        // cast — its shared effect text can't identify which buff is up, and matching it
        // falsely latched buffs on login and suppressed the real cast's confirm.
        h.Feed(readout);
        Assert.Empty(h.Applied);
        Assert.False(h.Tracker.IsActive(h.Messages.Messages[0]));

        // The bare effect line — a genuine fresh cast, no parenthetical — still applies.
        h.Feed("You feel lucky!");
        Assert.Single(h.Applied);
        Assert.True(h.Tracker.IsActive(h.Messages.Messages[0]));
    }

    // ----- LastActionFailed (confusion fumble) ------------------------

    [Fact]
    public void Fumble_FiresActionFailed()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("fumble",
            MessageFlags.Confused | MessageFlags.LastActionFailed,
            applied: "You fumble in confusion",
            endsWith: "The effects of confusion wear off"));

        h.Feed("You fumble in confusion!");

        Assert.Single(h.ActionFailed);
        Assert.Equal("fumble", h.ActionFailed[0].Name);
    }

    [Fact]
    public void Fumble_FiresEveryLine_NotJustFirst()
    {
        // Confusion fumbles command after command while it lasts; the condition
        // is "applied" only once, but the retry must ride every fumble line.
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("fumble",
            MessageFlags.Confused | MessageFlags.LastActionFailed,
            applied: "You fumble in confusion",
            endsWith: "The effects of confusion wear off"));

        h.Feed("You fumble in confusion!");
        h.Feed("You fumble in confusion!");
        h.Feed("You fumble in confusion!");

        Assert.Equal(3, h.ActionFailed.Count);   // per-line, not deduped
        Assert.Single(h.Applied);                // condition applied only once
    }

    [Fact]
    public void NonActionFailedCondition_DoesNotFireActionFailed()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("Poison",
            MessageFlags.Poisoned,
            applied: "You have been poisoned!",
            endsWith: "The poison wears off."));

        h.Feed("You have been poisoned!");

        Assert.Empty(h.ActionFailed);
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
    public void SharedAppliedLine_WearOff_FiresEndedForPrimaryOnly()
    {
        // Two DISTINCT buffs that share an applied line ("You feel protected!") but
        // carry their OWN wear-off lines (the mageshield / holy-armour family). When
        // one wears off, ConditionEnded must fire for THAT record only, not the
        // sibling — its sole consumer is the self-buff recast timers, which anchor on
        // each buff's own cast code, so a shared line must not clear a different buff
        // we cast. The sibling is still dropped from _active (it was co-latched on the
        // shared applied line), so flags stay honest.
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("mageshield", MessageFlags.None,
            applied: "You feel protected!", endsWith: "Your mageshield shimmers and fades."));
        h.Messages.Messages.Add(MakeRecord("holy armour", MessageFlags.None,
            applied: "You feel protected!", endsWith: "Your holy armour fades."));

        h.Feed("You feel protected!");        // both latch on the shared applied line
        Assert.Equal(2, h.Applied.Count);
        h.Ended.Clear();

        h.Feed("Your holy armour fades.");    // only holy armour's OWN wear-off fires

        Assert.Single(h.Ended);
        Assert.Equal("holy armour", h.Ended[0].Name);
    }

    [Fact]
    public void ConfusionWearOff_ClearsAllConfusedSources_IncludingCoLatchedFumble()
    {
        // A monster confuse with its own wear-off, plus the generic "you fumble in
        // confusion" (which also carries Confused so it can flag a confuse whose
        // set-line we missed), both latch. When the monster confuse wears off, the
        // fumble-held confusion must clear too — otherwise the flag (and the nav
        // pause it drives) stays stuck, because fumble's own generic wear-off never
        // fired. The two records do NOT share an applied line, so the alias-group
        // clear alone wouldn't reach fumble.
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("high-pitched scream",
            MessageFlags.Confused,
            applied: "You feel confused!",
            endsWith: "The effects of the death dog's shriek wear off!"));
        h.Messages.Messages.Add(MakeRecord("fumble",
            MessageFlags.Confused | MessageFlags.LastActionFailed,
            applied: "You fumble in confusion",
            endsWith: "The effects of confusion wear off"));

        h.Feed("You feel confused!");         // spell latches Confused
        h.Feed("You fumble in confusion!");   // fumble co-latches Confused
        Assert.True(h.Tracker.IsConfused);

        h.Feed("The effects of the death dog's shriek wear off!");   // spell wears off

        Assert.False(h.Tracker.IsConfused);   // both Confused sources cleared
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

    [Fact]
    public void MultipleRecordsSameLine_CollapsesLog_ButFiresEachEvent()
    {
        using Harness h = new();
        // Three catalogue records that all emit the same effect text — models the
        // bless spell plus bless-proc items all reporting "You feel lucky!".
        h.Messages.Messages.Add(MakeRecord("bless",
            MessageFlags.None, "You feel lucky", "The effects of bless wear off"));
        h.Messages.Messages.Add(MakeRecord("black warhammer",
            MessageFlags.None, "You feel lucky", "The effects of bless wear off"));
        h.Messages.Messages.Add(MakeRecord("dark blessing",
            MessageFlags.None, "You feel lucky", "The blessing fades"));

        h.Feed("You feel lucky!");

        // Every record still fires ConditionApplied — CastingDirector needs to see
        // them all to pick out the one that maps to a cast code.
        Assert.Equal(3, h.Applied.Count);

        // But the program log collapses to a single applied row, not one per record.
        int appliedRows = h.Log.Snapshot()
            .Count(e => e.Source == ConditionTracker.LogCategory
                        && e.Message.Contains("condition applied"));
        Assert.Equal(1, appliedRows);
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
