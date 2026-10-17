using MudPlay.Game;
using MudPlay.Game.Conditions;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// SelfHeldResponder — our own knockdown / held (MovementPrevented)
// lights the self party chip and asserts the HeldGate so the loop holds instead
// of hammering the server with moves that bonk "flat on your back"; both release
// on "You get back on your feet.". There is no opt-out — a knockdown always holds.
public sealed class SelfHeldResponderTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, System.Array.Empty<CellAttributes>(), DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private static MessageRecord Knockdown() => new(
        Id: MessageRecord.ComputeId("knockdown", "", "", "", "You are flat on your back!", "You get back on your feet."),
        Name: "knockdown",
        Flags: MessageFlags.MovementPrevented,
        RawFlagsHex: (ushort)MessageFlags.MovementPrevented,
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
        // The game's output, fed the way the client receives it.
        public ServerFeed Server { get; } = new();
        public MessageRouter Router { get; } = new();
        public PartyManager Party { get; }
        public MovementCoordinator Coordinator { get; }
        public SelfHeldResponder Responder { get; }

        public Harness()
        {
            Conditions = new ConditionTracker(Messages, Log);
            Conditions.AttachLineExtractor(Server.Lines);
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

        private void Feed(string text) => Server.Line(text);

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
    public void Death_ClearsHeldGateAndChip()
    {
        // Regression (report paradigm-20260809-114444): a knockdown latched
        // MovementPrevented, then the player DIED before "You get back on your
        // feet." arrived, so the wear-off never came. AppServices wires
        // Conditions.ClearAll("death") to the death event; that resets the
        // observation log, whose ActiveFlags edge must drop the self chip and
        // release the HeldGate — otherwise the walker sits "Paused by: Held"
        // forever while the character is free to move in-game.
        using Harness h = new();
        h.FormParty();
        h.Knock();
        Assert.True(h.GateHeld);

        h.Conditions.ClearAll("death");

        Assert.False(h.SelfChip);
        Assert.False(h.GateHeld);
    }

    [Fact]
    public void Solo_SelfRow_HoldsGateAndLightsChip()
    {
        using Harness h = new();
        // Solo now keeps a lone self row (PartyWindow self-display): the local hold
        // applies so a solo player doesn't walk while knocked down, AND the self
        // row's Held chip lights.
        h.Knock();

        Assert.True(h.GateHeld);
        Assert.True(h.SelfChip);
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
