using FujinTerm.Game;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="PlayerDroppedGate"/> — HP falling to or below 0 (mortally
/// wounded) holds the <see cref="EngineSendGate"/>, asserts the
/// MortallyWoundedGate, and clears the stale party roster; all three release on
/// recovery.
/// </summary>
public sealed class PlayerDroppedGateTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private sealed class Harness : IDisposable
    {
        public PlayerState State { get; } = new();
        public EngineSendGate Gate { get; } = new();
        public LogService Log { get; } = new();
        public MovementCoordinator Coordinator { get; }
        public MessageRouter Router { get; } = new();
        public PartyManager Party { get; }
        public PlayerDroppedGate Dropped { get; }
        public int ChangeEvents { get; private set; }

        public Harness()
        {
            Coordinator = new MovementCoordinator(Log);
            DefaultPatterns.Seed(Router);
            PartyState partyState = new();
            Party = new PartyManager(Router, partyState);
            Dropped = new PlayerDroppedGate(State, Gate, Coordinator, Party, Log);
            Dropped.MortallyWoundedChanged += () => ChangeEvents++;
        }

        // PromptParser's write order: values first, HasPromptData last.
        public void SetPrompt(int hp, int maxHp)
        {
            State.Hp = hp;
            State.MaxHp = maxHp;
            State.HasPromptData = true;
        }

        public bool MoveGateHeld =>
            Coordinator.AssertedGates.Contains(MovementCoordinator.MortallyWoundedGate);

        public void Dispose()
        {
            Dropped.Dispose();
            Party.Dispose();
        }
    }

    [Fact]
    public void NoPromptData_NotDropped()
    {
        using Harness h = new();
        // HasPromptData=false even though Hp defaults to 0 — pre-connect state
        // must not read as mortally wounded.
        h.State.Hp = 0;
        h.State.MaxHp = 200;
        Assert.False(h.Dropped.IsMortallyWounded);
        Assert.False(h.Gate.IsLocked);
        Assert.False(h.MoveGateHeld);
    }

    [Fact]
    public void ZeroOnZeroPrompt_NotDropped()
    {
        using Harness h = new();
        // MaxHp still 0 when HasPromptData flips — a producer race, not a real
        // drop. Guarded by MaxHp > 0.
        h.State.HasPromptData = true;
        Assert.False(h.Dropped.IsMortallyWounded);
        Assert.False(h.Gate.IsLocked);
    }

    [Fact]
    public void HpAtZero_HoldsGateAndAssertsMovement()
    {
        using Harness h = new();
        h.SetPrompt(hp: 0, maxHp: 200);

        Assert.True(h.Dropped.IsMortallyWounded);
        Assert.True(h.Gate.IsLocked);
        Assert.True(h.MoveGateHeld);
        Assert.Equal(1, h.ChangeEvents);
    }

    [Fact]
    public void HpNegative_HoldsGate()
    {
        using Harness h = new();
        h.SetPrompt(hp: -10, maxHp: 200);   // bleeding out below 0

        Assert.True(h.Dropped.IsMortallyWounded);
        Assert.True(h.Gate.IsLocked);
        Assert.True(h.MoveGateHeld);
    }

    [Fact]
    public void HealthyHp_DoesNotHold()
    {
        using Harness h = new();
        h.SetPrompt(hp: 150, maxHp: 200);

        Assert.False(h.Dropped.IsMortallyWounded);
        Assert.False(h.Gate.IsLocked);
        Assert.False(h.MoveGateHeld);
    }

    [Fact]
    public void Recovery_ReleasesEverything()
    {
        using Harness h = new();
        h.SetPrompt(hp: 0, maxHp: 200);
        Assert.True(h.Dropped.IsMortallyWounded);

        h.State.Hp = 40;   // aided + healed back above 0

        Assert.False(h.Dropped.IsMortallyWounded);
        Assert.False(h.Gate.IsLocked);
        Assert.False(h.MoveGateHeld);
        Assert.Equal(2, h.ChangeEvents);   // drop + recover
    }

    [Fact]
    public void Drop_ClearsPartyRoster()
    {
        using Harness h = new();
        // Form a party: we lead, Helper follows.
        h.Party.LocalCharacterName = "Forged";
        h.Router.Dispatch(Line("Helper started to follow you."));
        Assert.True(h.Party.State.IsInParty);
        Assert.NotEmpty(h.Party.State.Members);

        // Drop — game-side removal from the party. Roster must go stale-clear so
        // a follower/leader gate doesn't hold movement on a party we've left.
        h.SetPrompt(hp: 0, maxHp: 200);

        Assert.False(h.Party.State.IsInParty);
        Assert.Empty(h.Party.State.Members);
        Assert.Null(h.Party.State.LeaderName);
        Assert.False(h.Party.State.SelfIsLeader);
    }

    [Fact]
    public void Recovery_DoesNotRestorePartyRoster()
    {
        // Recovery re-confirms membership only from a real follow / par signal —
        // a revived character needs a re-invite from the leader to rejoin, so the
        // gate must NOT resurrect the wiped roster.
        using Harness h = new();
        h.Party.LocalCharacterName = "Forged";
        h.Router.Dispatch(Line("Helper started to follow you."));

        h.SetPrompt(hp: 0, maxHp: 200);
        h.State.Hp = 40;   // recovered

        Assert.False(h.Party.State.IsInParty);
        Assert.Empty(h.Party.State.Members);
    }

    [Fact]
    public void ComposesWithSuicidePasswordHold()
    {
        using Harness h = new();
        // A suicide-password flow already holds the gate when we drop.
        h.Gate.Hold("SuicidePassword");
        h.SetPrompt(hp: 0, maxHp: 200);
        Assert.True(h.Gate.IsLocked);

        // Recovering releases our own hold but must NOT unlock the still-active
        // suicide hold.
        h.State.Hp = 40;
        Assert.True(h.Gate.IsLocked);

        h.Gate.Release("SuicidePassword");
        Assert.False(h.Gate.IsLocked);
    }

    [Fact]
    public void RepeatedDropTicks_FireOnce()
    {
        using Harness h = new();
        h.SetPrompt(hp: 0, maxHp: 200);
        h.State.Hp = -5;    // still dropped, deeper
        h.State.Hp = -10;   // still dropped

        Assert.Equal(1, h.ChangeEvents);   // single edge, no re-fire
        Assert.True(h.Gate.IsLocked);
    }

    [Fact]
    public void DisposeWhileDropped_ReleasesHoldAndGate()
    {
        Harness h = new();
        h.SetPrompt(hp: 0, maxHp: 200);
        Assert.True(h.Gate.IsLocked);
        Assert.True(h.MoveGateHeld);

        h.Dropped.Dispose();

        Assert.False(h.Gate.IsLocked);
        Assert.False(h.MoveGateHeld);
        h.Party.Dispose();
    }
}
