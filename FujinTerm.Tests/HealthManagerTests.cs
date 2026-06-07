using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Health;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.B — <see cref="HealthManager"/> threshold-driven gate
/// assertions + rest / stand pacing + InCombat respect + pre-/post-rest
/// chained commands.
/// </summary>
public sealed class HealthManagerTests
{
    private sealed class Harness : IDisposable
    {
        public PlayerState State { get; } = new();
        public LogService Log { get; } = new();
        public MovementCoordinator Coordinator { get; }
        public HealthManager Health { get; }
        public List<byte[]> Sent { get; } = new();
        public HealthSettings Settings { get; set; } = new();

        public Harness(HealthSettings? settings = null)
        {
            Settings = settings ?? new HealthSettings();
            Coordinator = new MovementCoordinator(Log);
            Health = new HealthManager(State, Coordinator,
                readSettings: () => Settings, log: Log);
            Health.SetWireSender(b => Sent.Add(b));
        }

        /// <summary>
        /// Mirror <see cref="PromptParser"/>'s write order: values
        /// first, HasPromptData last. Anything else and the engine
        /// sees Hp=0 with HasPromptData=true and asserts spuriously
        /// — same race a sloppy producer would hit in production.
        /// </summary>
        public void SetPrompt(int hp, int maxHp, int ma = 0, int maxMa = 0)
        {
            State.Hp = hp;
            State.MaxHp = maxHp;
            State.Ma = ma;
            State.MaxMa = maxMa;
            State.HasPromptData = true;
        }

        public bool HealthGateHeld =>
            Coordinator.AssertedGates.Contains(MovementCoordinator.HealthRecoveryGate);
        public bool ManaGateHeld =>
            Coordinator.AssertedGates.Contains(MovementCoordinator.ManaRecoveryGate);

        public string LastSent => Sent.Count == 0
            ? string.Empty
            : Encoding.Latin1.GetString(Sent[^1]).TrimEnd('\r');

        public List<string> SentLines =>
            Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

        public void Dispose() => Health.Dispose();
    }

    // ----- no prompt data yet → engine dormant -----------------------

    [Fact]
    public void NoPromptData_DoesNothing()
    {
        using Harness h = new();
        // Default: HasPromptData=false, Hp=0, MaxHp=0 — must not assert.
        h.Health.Evaluate();
        Assert.False(h.HealthGateHeld);
        Assert.False(h.ManaGateHeld);
        Assert.Empty(h.Sent);
    }

    // ----- HP gate transitions ---------------------------------------

    [Fact]
    public void HpBelowTrigger_AssertsHealthRecovery()
    {
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 100;            // 50% — at default 60% trigger

        Assert.True(h.HealthGateHeld);
    }

    [Fact]
    public void HpAboveTrigger_DoesNotAssert()
    {
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 180;            // 90% > 60%

        Assert.False(h.HealthGateHeld);
    }

    [Fact]
    public void HpRecoversToTarget_ClearsGate()
    {
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        Assert.True(h.HealthGateHeld);

        h.State.Hp = 190;            // 95% target hit
        Assert.False(h.HealthGateHeld);
    }

    [Fact]
    public void HpRecoversPartially_GateStaysAsserted()
    {
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        Assert.True(h.HealthGateHeld);

        h.State.Hp = 100;            // 50% — past trigger but below 95% target
        Assert.True(h.HealthGateHeld);
    }

    // ----- absolute threshold mode -----------------------------------

    [Fact]
    public void AbsoluteMode_TriggerExactValue()
    {
        HealthSettings s = new()
        {
            HpThresholdMode = ThresholdMode.Absolute,
            RestIfBelowHp   = 60,
            RestMaxHp       = 195,
        };
        using Harness h = new(s);
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 60;             // exactly at 60 absolute → trigger

        Assert.True(h.HealthGateHeld);
    }

    // ----- rest / stand pacing ---------------------------------------

    [Fact]
    public void GateAsserted_OutOfCombat_SendsRest()
    {
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;

        Assert.True(h.HealthGateHeld);
        Assert.Contains("rest", h.SentLines);
        Assert.True(h.Health.RestInFlight);
    }

    [Fact]
    public void GateAsserted_InCombat_DoesNotRest()
    {
        using Harness h = new();
        h.State.InCombat = true;     // first so it doesn't trigger evaluate before threshold
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;

        Assert.True(h.HealthGateHeld);
        Assert.DoesNotContain("rest", h.SentLines);
        Assert.False(h.Health.RestInFlight);
    }

    [Fact]
    public void GateAssertedInCombat_CombatEnds_ThenRest()
    {
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.InCombat = true;
        h.State.Hp = 50;
        Assert.True(h.HealthGateHeld);
        Assert.False(h.Health.RestInFlight);

        h.State.InCombat = false;    // combat just ended
        Assert.True(h.Health.RestInFlight);
        Assert.Contains("rest", h.SentLines);
    }

    [Fact]
    public void Recovery_SendsStand_ClearsInFlight()
    {
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        Assert.True(h.Health.RestInFlight);

        h.State.Hp = 195;             // past 95% target
        Assert.False(h.HealthGateHeld);
        Assert.Contains("stand", h.SentLines);
        Assert.False(h.Health.RestInFlight);
    }

    [Fact]
    public void RestSentOnce_NoSpamming()
    {
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;

        // Drop further → no extra rest emit while in flight.
        h.State.Hp = 30;
        int restCount = h.SentLines.Count(l => l == "rest");
        Assert.Equal(1, restCount);
    }

    // ----- pre / post commands ---------------------------------------

    [Fact]
    public void PreRestCommand_SentBeforeRest()
    {
        HealthSettings s = new() { PreRestCommand = "peer" };
        using Harness h = new(s);
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;

        int peerIdx = h.SentLines.IndexOf("peer");
        int restIdx = h.SentLines.IndexOf("rest");
        Assert.True(peerIdx >= 0);
        Assert.True(restIdx >= 0);
        Assert.True(peerIdx < restIdx);
    }

    [Fact]
    public void PreRestCommand_Chained_SplitsOnSemicolon()
    {
        HealthSettings s = new() { PreRestCommand = "peer;look" };
        using Harness h = new(s);
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;

        Assert.Contains("peer", h.SentLines);
        Assert.Contains("look", h.SentLines);
        // Both pre-rest commands precede rest.
        int peer = h.SentLines.IndexOf("peer");
        int look = h.SentLines.IndexOf("look");
        int rest = h.SentLines.IndexOf("rest");
        Assert.True(peer < rest);
        Assert.True(look < rest);
    }

    [Fact]
    public void PreRestCommand_Chained_SplitsOnCaretM()
    {
        HealthSettings s = new() { PreRestCommand = "peer^Mlook" };
        using Harness h = new(s);
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;

        Assert.Contains("peer", h.SentLines);
        Assert.Contains("look", h.SentLines);
    }

    [Fact]
    public void PostRestCommand_SentAfterStand()
    {
        HealthSettings s = new() { PostRestCommand = "look;exits" };
        using Harness h = new(s);
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        h.State.Hp = 195;

        int stand  = h.SentLines.IndexOf("stand");
        int look   = h.SentLines.LastIndexOf("look");
        int exits  = h.SentLines.LastIndexOf("exits");
        Assert.True(stand >= 0);
        Assert.True(look  > stand);
        Assert.True(exits > stand);
    }

    // ----- MA gate (independent of HP) -------------------------------

    [Fact]
    public void MaBelowTrigger_AssertsManaRecovery()
    {
        using Harness h = new();
        h.State.MaxMa = 100;
        h.State.HasPromptData = true;
        h.State.Ma = 20;             // 20% < 30% trigger

        Assert.True(h.ManaGateHeld);
    }

    [Fact]
    public void MaxMaZero_NoSpuriousAssert()
    {
        // Non-caster classes — MaxMa stays 0 forever. The threshold
        // computation must not spuriously assert.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.MaxMa = 0;
        h.State.HasPromptData = true;
        h.State.Ma = 0;

        Assert.False(h.ManaGateHeld);
    }

    [Fact]
    public void BothPoolsLow_BothGatesHeld_OneRest()
    {
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.MaxMa = 100;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        h.State.Ma = 20;

        Assert.True(h.HealthGateHeld);
        Assert.True(h.ManaGateHeld);
        Assert.Equal(1, h.SentLines.Count(l => l == "rest"));
    }

    [Fact]
    public void BothPoolsLow_OnlyHpRecovers_StaysResting()
    {
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.MaxMa = 100;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        h.State.Ma = 20;

        h.State.Hp = 195;            // HP topped, MA still low
        Assert.False(h.HealthGateHeld);
        Assert.True(h.ManaGateHeld);
        // No stand emit yet — MA gate still holds.
        Assert.DoesNotContain("stand", h.SentLines);
        Assert.True(h.Health.RestInFlight);
    }

    [Fact]
    public void BothPoolsRecover_StandsAndClears()
    {
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.MaxMa = 100;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        h.State.Ma = 20;

        h.State.Hp = 195;
        h.State.Ma = 95;

        Assert.False(h.HealthGateHeld);
        Assert.False(h.ManaGateHeld);
        Assert.Contains("stand", h.SentLines);
        Assert.False(h.Health.RestInFlight);
    }

    // ----- run-if-below (flee) ---------------------------------------

    [Fact]
    public void HpBelowRunTrigger_InCombat_SendsFleeOnce()
    {
        // Default RunIfBelowHp=20% — set HP to 30 against MaxHp=200
        // (15%) while in combat.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;

        Assert.Contains("flee", h.SentLines);
        Assert.True(h.Health.FledThisCombat);

        // Drop further → no spam.
        h.State.Hp = 25;
        Assert.Equal(1, h.SentLines.Count(l => l == "flee"));
    }

    [Fact]
    public void HpBelowRunTrigger_OutOfCombat_DoesNotFlee()
    {
        // Out of combat, low HP just enters the normal rest cycle.
        // `flee` is combat-specific.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 30;            // 15% — below run threshold

        Assert.DoesNotContain("flee", h.SentLines);
        Assert.False(h.Health.FledThisCombat);
    }

    [Fact]
    public void FledThisCombat_ResetsWhenCombatEnds()
    {
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;
        Assert.True(h.Health.FledThisCombat);

        h.State.InCombat = false;
        Assert.False(h.Health.FledThisCombat);
    }

    [Fact]
    public void FledThisCombat_AllowsFleeOnNextCombat()
    {
        // Flee in fight #1, combat ends, flee should re-arm for #2.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;
        Assert.Equal(1, h.SentLines.Count(l => l == "flee"));

        h.State.InCombat = false;
        h.State.Hp = 100;            // recovered enough to be ~50%
        h.State.InCombat = true;     // fight #2 begins
        h.State.Hp = 25;             // low again
        Assert.Equal(2, h.SentLines.Count(l => l == "flee"));
    }

    [Fact]
    public void MaBelowRunTrigger_InCombat_SendsFlee()
    {
        // Caster low on MA mid-combat — flee too.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.MaxMa = 100;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 150;            // healthy HP
        h.State.Ma = 5;              // below default MA run-trigger (10%)

        Assert.Contains("flee", h.SentLines);
    }

    [Fact]
    public void NonCasterMaxMaZero_NoFleeFromMa()
    {
        // Non-caster — MA is 0/0 forever; must not flee from MA path.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.MaxMa = 0;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 150;
        h.State.Ma = 0;

        Assert.DoesNotContain("flee", h.SentLines);
    }

    [Fact]
    public void FleeAndRest_BothHappen_SamePass()
    {
        // HP crosses run-trigger and rest-trigger together (e.g. 30/200
        // = 15% < both). We flee in combat; once combat ends, the rest
        // cycle takes over.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;
        Assert.Contains("flee", h.SentLines);
        Assert.DoesNotContain("rest", h.SentLines);   // still in combat — no rest

        h.State.InCombat = false;
        Assert.Contains("rest", h.SentLines);
    }

    // ----- gate-history captures asserter --------------------------

    [Fact]
    public void GateHistoryRecordsAsserterAndReason()
    {
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;

        GateTransitionEntry? hpAssert = h.Coordinator.History
            .FirstOrDefault(e => e.Gate == MovementCoordinator.HealthRecoveryGate && e.Asserted);
        Assert.NotNull(hpAssert);
        Assert.Equal(HealthManager.AsserterName, hpAssert!.Value.Asserter);
        Assert.Contains("HP", hpAssert.Value.Reason);
    }
}
