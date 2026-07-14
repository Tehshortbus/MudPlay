using System.Reflection;
using FujinTerm.Game;
using FujinTerm.Game.Conditions;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="SelfHeldResponder"/> — our own knockdown / held (MovementPrevented)
/// lights the self party chip and asserts the HeldGate so the loop holds instead
/// of hammering the server with moves that bonk "flat on your back"; both release
/// on "You get back on your feet.". There is no opt-out — a knockdown always holds.
/// </summary>
public sealed class SelfHeldResponderTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, System.Array.Empty<CellAttributes>(), DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private static MessageRecord Knockdown() => new(
        Id: MessageRecord.ComputeId("knockdown", "", "", "", "You are flat on your back!", "You get back on your feet."),
        Name: "knockdown",
        Action: MessageAction.Ignore,
        Flags: MessageFlags.MovementPrevented,
        RawFlagsHex: (ushort)MessageFlags.MovementPrevented,
        Response: string.Empty,
        CasterMessage: string.Empty,
        TargetMessage: string.Empty,
        WitnessMessage: string.Empty,
        AppliedMessage: "You are flat on your back!",
        AppliedEndsWith: "You get back on your feet.");

    private sealed class Harness : IDisposable
    {
        public LogService Log { get; } = new();
        public MessageStore Messages { get; } = new();
        public ConditionTracker Conditions { get; }
        public MessageRouter Router { get; } = new();
        public PartyManager Party { get; }
        public MovementCoordinator Coordinator { get; }
        public SelfHeldResponder Responder { get; }

        public Harness()
        {
            Conditions = new ConditionTracker(Messages, Log);
            Messages.Messages.Add(Knockdown());
            DefaultPatterns.Seed(Router);
            PartyState partyState = new();
            Party = new PartyManager(Router, partyState) { LocalCharacterName = "Forged" };
            Coordinator = new MovementCoordinator(Log);
            Responder = new SelfHeldResponder(Conditions, Party, Coordinator, Log);
        }

        public void FormParty() => Router.Dispatch(Line("Helper started to follow you."));

        public void Knock()   => Feed("You are flat on your back!");
        public void Recover() => Feed("You get back on your feet.");

        public bool GateHeld =>
            Coordinator.AssertedGates.Contains(MovementCoordinator.HeldGate);

        public bool SelfChip
        {
            get
            {
                foreach (PartyMember m in Party.State.Members)
                    if (m.IsSelf) return m.Held;
                return false;
            }
        }

        private void Feed(string text)
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
    public void Knockdown_LightsChipAndHoldsGate()
    {
        using Harness h = new();
        h.FormParty();

        h.Knock();

        Assert.True(h.SelfChip);
        Assert.True(h.GateHeld);
    }

    [Fact]
    public void GetBackOnFeet_DropsChipAndGate()
    {
        using Harness h = new();
        h.FormParty();
        h.Knock();
        Assert.True(h.GateHeld);

        h.Recover();

        Assert.False(h.SelfChip);
        Assert.False(h.GateHeld);
    }

    [Fact]
    public void Solo_NoSelfRow_StillHoldsGate()
    {
        using Harness h = new();
        // No party — the chip write is a harmless no-op, but the local hold must
        // still apply so a solo player doesn't walk while knocked down.
        h.Knock();

        Assert.True(h.GateHeld);
        Assert.False(h.SelfChip);
    }

    [Fact]
    public void RepeatedKnockdownLines_HoldOnce()
    {
        using Harness h = new();
        h.FormParty();
        h.Knock();
        h.Knock();   // "flat on your back!" repeats as a move-refusal while down

        Assert.True(h.GateHeld);
        Assert.Single(h.Coordinator.AssertedGates);   // no duplicate assert
    }

    [Fact]
    public void DisposeWhileHeld_ReleasesGate()
    {
        Harness h = new();
        h.FormParty();
        h.Knock();
        Assert.True(h.GateHeld);

        h.Responder.Dispose();

        Assert.False(h.GateHeld);
        h.Party.Dispose();
        h.Conditions.Dispose();
    }
}
