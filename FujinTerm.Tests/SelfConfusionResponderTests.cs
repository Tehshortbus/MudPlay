using System.Reflection;
using FujinTerm.Game;
using FujinTerm.Game.Conditions;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="SelfConfusionResponder"/> — the local side of our own confusion
/// (the leader / solo case whose @wait is eaten). On confusion onset it lights
/// the self party chip and, unless Ignore Confusion is set, asserts the
/// ConfusionGate; both release on clear. <see cref="SelfConfusionResponder.Reevaluate"/>
/// reconciles a mid-confusion Ignore Confusion toggle.
/// </summary>
public sealed class SelfConfusionResponderTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, System.Array.Empty<CellAttributes>(), DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private static MessageRecord Confusion() => new(
        Id: MessageRecord.ComputeId("Confusion", "", "", "", "You are confused!", "You feel less confused."),
        Name: "Confusion",
        Action: MessageAction.Ignore,
        Flags: MessageFlags.Confused,
        RawFlagsHex: (ushort)MessageFlags.Confused,
        Response: string.Empty,
        CasterMessage: string.Empty,
        TargetMessage: string.Empty,
        WitnessMessage: string.Empty,
        AppliedMessage: "You are confused!",
        AppliedEndsWith: "You feel less confused.");

    private sealed class Harness : IDisposable
    {
        public LogService Log { get; } = new();
        public MessageStore Messages { get; } = new();
        public ConditionTracker Conditions { get; }
        public MessageRouter Router { get; } = new();
        public PartyManager Party { get; }
        public MovementCoordinator Coordinator { get; }
        public SelfConfusionResponder Responder { get; }

        // Mutable so a test can flip Ignore Confusion between calls.
        public bool IgnoreConfusion;

        public Harness()
        {
            Conditions = new ConditionTracker(Messages, Log);
            Messages.Messages.Add(Confusion());
            DefaultPatterns.Seed(Router);
            PartyState partyState = new();
            Party = new PartyManager(Router, partyState) { LocalCharacterName = "Forged" };
            Coordinator = new MovementCoordinator(Log);
            Responder = new SelfConfusionResponder(
                Conditions, Party, Coordinator,
                readSpells: () => new SpellsSettings { IgnoreConfusion = IgnoreConfusion },
                log: Log);
        }

        // Form a real party so a self row exists for the chip to land on:
        // Helper follows us, which adds both Helper and our self row.
        public void FormParty() => Router.Dispatch(Line("Helper started to follow you."));

        public void Confuse()   => Feed("You are confused!");
        public void Unconfuse() => Feed("You feel less confused.");

        public bool GateHeld =>
            Coordinator.AssertedGates.Contains(MovementCoordinator.ConfusionGate);

        public bool SelfChip
        {
            get
            {
                foreach (PartyMember m in Party.State.Members)
                    if (m.IsSelf) return m.Confused;
                return false;
            }
        }

        public void Feed(string text)
        {
            var emitted = Line(text);
            typeof(ConditionTracker)
                .GetMethod("OnLine", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Conditions, new object[] { emitted });
        }

        public void Dispose()
        {
            Responder.Dispose();
            Party.Dispose();
            Conditions.Dispose();
        }
    }

    [Fact]
    public void Confusion_LightsChipAndHoldsGate()
    {
        using Harness h = new();
        h.FormParty();

        h.Confuse();

        Assert.True(h.SelfChip);
        Assert.True(h.GateHeld);
    }

    [Fact]
    public void Clear_DropsChipAndGate()
    {
        using Harness h = new();
        h.FormParty();
        h.Confuse();
        Assert.True(h.GateHeld);

        h.Unconfuse();

        Assert.False(h.SelfChip);
        Assert.False(h.GateHeld);
    }

    [Fact]
    public void IgnoreConfusion_ChipStillLit_ButNoGate()
    {
        using Harness h = new();
        h.FormParty();
        h.IgnoreConfusion = true;

        h.Confuse();

        // The chip reflects the fact we're confused regardless of the setting;
        // only the movement hold is suppressed.
        Assert.True(h.SelfChip);
        Assert.False(h.GateHeld);
    }

    [Fact]
    public void Solo_NoSelfRow_StillHoldsGate()
    {
        using Harness h = new();
        // No FormParty — LocalCharacterName is set but there's no member row.
        // The chip write is a harmless no-op; the local hold must still apply.
        h.Confuse();

        Assert.True(h.GateHeld);
        Assert.False(h.SelfChip);   // no self row to carry a chip
    }

    [Fact]
    public void Reevaluate_TurningIgnoreOn_LiftsHoldMidConfusion()
    {
        using Harness h = new();
        h.FormParty();
        h.Confuse();
        Assert.True(h.GateHeld);

        // User checks Ignore Confusion while still confused.
        h.IgnoreConfusion = true;
        h.Responder.Reevaluate();

        Assert.False(h.GateHeld);
        Assert.True(h.SelfChip);    // chip untouched by Reevaluate
    }

    [Fact]
    public void Reevaluate_TurningIgnoreOff_PlacesHoldMidConfusion()
    {
        using Harness h = new();
        h.FormParty();
        h.IgnoreConfusion = true;
        h.Confuse();
        Assert.False(h.GateHeld);

        // User unchecks Ignore Confusion while still confused.
        h.IgnoreConfusion = false;
        h.Responder.Reevaluate();

        Assert.True(h.GateHeld);
    }

    [Fact]
    public void Reevaluate_NotConfused_NoOp()
    {
        using Harness h = new();
        h.FormParty();

        h.IgnoreConfusion = false;
        h.Responder.Reevaluate();

        Assert.False(h.GateHeld);
    }

    // A second confusion source that shares the generic applied line ("You are
    // confused!") but carries its own specific wear-off — the shape that once
    // stuck the reported nav pause. The group-clear now ends every record sharing
    // the applied line when any of them wears off, so this record no longer
    // strands the flag when the generic "confusion wears off" fires.
    private static MessageRecord HypnoticHands() => new(
        Id: MessageRecord.ComputeId("HypnoticHands", "", "", "", "You are confused!", "The effect of hypnotic hands wears off."),
        Name: "hypnotic hands",
        Action: MessageAction.WaitForEnd,
        Flags: MessageFlags.Confused,
        RawFlagsHex: (ushort)MessageFlags.Confused,
        Response: string.Empty,
        CasterMessage: string.Empty,
        TargetMessage: string.Empty,
        WitnessMessage: string.Empty,
        AppliedMessage: "You are confused!",
        AppliedEndsWith: "The effect of hypnotic hands wears off.");

    [Fact]
    public void SharedAppliedLine_GenericWearOff_ClearsWholeGroup()
    {
        using Harness h = new();
        // Both records latch on the shared "You are confused!" applied line.
        h.Messages.Messages.Add(HypnoticHands());
        h.FormParty();

        h.Confuse();
        Assert.True(h.GateHeld);

        // The generic wear-off matches only the generic record's own end text,
        // but the shared applied line makes the pair one effect — the group sweep
        // clears hypnotic hands too, so the flag (and the nav pause) drops.
        h.Unconfuse();
        Assert.False(h.GateHeld);
        Assert.False(h.SelfChip);
    }

    [Fact]
    public void SharedAppliedLine_SpecificWearOff_ClearsWholeGroup()
    {
        using Harness h = new();
        h.Messages.Messages.Add(HypnoticHands());
        h.FormParty();

        h.Confuse();
        Assert.True(h.GateHeld);

        // The other direction: hypnotic hands' own specific wear-off ends the
        // generic sibling latched on the same applied line, too.
        h.Feed("The effect of hypnotic hands wears off.");
        Assert.False(h.GateHeld);
        Assert.False(h.SelfChip);
    }

    [Fact]
    public void ClearAll_DropsGroupChipAndGate()
    {
        using Harness h = new();
        // Reset States path: ClearAll drops every active record regardless of
        // wear-off text, cascading gate release + chip clear via the ActiveFlags
        // edge — the manual escape hatch when no wear-off line ever arrives.
        h.Messages.Messages.Add(HypnoticHands());
        h.FormParty();

        h.Confuse();
        Assert.True(h.GateHeld);

        h.Conditions.ClearAll();
        Assert.False(h.GateHeld);
        Assert.False(h.SelfChip);
    }

    [Fact]
    public void DisposeWhileConfused_ReleasesGate()
    {
        Harness h = new();
        h.FormParty();
        h.Confuse();
        Assert.True(h.GateHeld);

        h.Responder.Dispose();

        Assert.False(h.GateHeld);
        h.Party.Dispose();
        h.Conditions.Dispose();
    }
}
