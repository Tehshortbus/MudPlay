using System.Text;
using MudPlay.Game;
using MudPlay.Game.Health;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// HealthManager threshold-driven gate
// assertions + rest / stand pacing + InCombat respect + pre-/post-rest
// chained commands.
public sealed class HealthManagerTests
{
    // Reacted to the run trigger this fight and waiting for it to end, as the bug
    // report's wait list tells it.
    private static bool Fled(HealthManager health) =>
        health.DescribeWaits().Contains("fled this combat (waits for it to end)");

    private sealed class Harness : IDisposable
    {
        public PlayerState State { get; } = new();
        public LogService Log { get; } = new();
        public MovementCoordinator Coordinator { get; }
        public HealthManager Health { get; }
        public List<byte[]> Sent { get; } = new();

        // A confirmed arrival in a room the test hasn't been in before.
        private int _rooms;
        public void Moved() => Health.NoteRoomChanged(new RoomKey(900, ++_rooms));
        public HealthSettings Settings { get; set; } = new();
        public bool AutoHealRestEnabled { get; set; } = true;

        // The Auto-Rest switch on its own; the engine switch above stays on.
        public bool RestEnabled { get; set; } = true;

        // The master switch. Off, both probes above read false, as the app's do,
        // and each hang-up it holds back is noted here.
        public bool MasterSwitchOff { get; set; }
        public List<string?> HeldByMasterSwitch { get; } = new();

        // Char-tier General settings. Default instance has
        // AllowHangupInAllOffMode=false, so the all-off carve-out stays
        // dormant unless a test opts in.
        public Models.Profile.GeneralSettings General { get; set; } = new();

        // User-configured hangup command (Settings → BBS →
        // Game-menu commands). Default =x matches the default
        // value shipped on BbsProfile.GameExitCommand. Set to
        // null to test the "not configured" branch.
        public string? HangupCommand { get; set; } = "=x";

        // What the wimpy-goto escape delegate returns — true = it
        // dispatched a jump (so the hangup is skipped), false = couldn't (falls
        // back to hangup). Default false.
        public bool WimpyFireResult { get; set; }

        // The location the wimpy-goto delegate was last invoked with,
        // or null if never called this run.
        public string? WimpyFiredWith { get; private set; }

        // When true, HealthManager's rest-out branch skips —
        // mirrors CombatStateTracker.HasEngageableHostiles in app code.
        // Defaults false (room clear) so existing tests don't need to
        // touch it.
        public bool HostilesPresent { get; set; }

        // When true, a gear-set swap is streaming its wear/rem commands —
        // HealthManager holds its rest re-issue so it doesn't thrash the swap.
        // Mirrors EquipmentManager.IsApplyingSet. Defaults false.
        public bool EquipmentApplying { get; set; }

        // Live Auto-Combat toggle for the engage-to-clear-a-rest-blocker
        // override. Defaults TRUE (combat on) so existing tests never trigger the
        // force-clear — it only fires with Auto-Combat OFF.
        public bool AutoCombatEnabled { get; set; } = true;

        // Increments each time HealthManager pokes CombatManager to engage a
        // rest-blocker. AppServices wires this to CombatManager.RequestRestClearEngage.
        public int RestClearEngageCount { get; private set; }

        // Hostile-in-room signal gating the emergency hangup —
        // mirrors CombatStateTracker.HasHostileMonster (auto-attack
        // independent). Defaults true so the existing hangup tests, which
        // predate the gate, keep firing exactly as before.
        public bool HostileInRoom { get; set; } = true;

        // Per-BBS negative-HP death floor (BbsProfile.PlayerDiesAtHp).
        // Default -25 matches the seeded value. The emergency hangup fires
        // anywhere in the bleeding-out window down to — but not past — this.
        public int DeathFloor { get; set; } = -25;

        // Simulates a loop's "do not rest in this room" flag — when true,
        // HealthManager suppresses the rest hold (the loop would advance instead of
        // resting). Defaults false so existing tests rest normally. AppServices
        // wires this to the running loop's current-room DoNotRest waypoint.
        public bool SkipRestHere { get; set; }

        // A loop's "rest up here" flags for the room we're in.
        public (bool Hp, bool Mana) RestHere { get; set; }

        // Local character poisoned — AppServices wires this to
        // ConditionTracker.IsPoisoned. Poison prevents a rest from taking.
        public bool Poisoned { get; set; }

        // The hangup-intent signal wired into the emergency-hangup
        // path. Tests peek it (non-consuming) to assert an intentional drop was
        // flagged, so the reactive-reconnect path stands down.
        public HangupSignal Hangup { get; } = new();

        // ShadowRest predicates (AppServices wires these to the class
        // ability, StealthManager.IsStealthed, and !PartyState.IsInParty). All
        // default to the "engaged" side so a test only sets the one it's probing;
        // the UtilizeShadowRest setting still has to be on for it to fire.
        public bool ShadowRestClass { get; set; } = true;
        public bool Stealthed { get; set; } = true;
        public bool Solo { get; set; } = true;

        // Increments each time HealthManager fires the ShadowRest resume
        // callback (recovery topped off to rest-max). AppServices wires this to
        // CombatManager.ResumeAfterShadowRest.
        public int ShadowRestResumeCount { get; private set; }

        // Increments each time the emergency hangup asks the client to
        // close the carrier after sending the exit command. AppServices wires
        // this to MainWindowViewModel.RequestHangupDisconnect.
        public int HangupDisconnectCount { get; private set; }

        // Controllable clock for the reconfirm-hold timeout backstop.
        // Advance it to simulate the window elapsing with no room re-display.
        public DateTimeOffset Clock = DateTimeOffset.UtcNow;

        // When true, a deferred HealthManager reaction (ConfirmHpGate /
        // ConfirmMaGate — see _post) is queued into Posted instead of running
        // inline, so a test can simulate the tick between a gate's initial assert
        // and its one-tick re-check. Default false keeps every existing test's
        // gate-assert synchronous (immediately confirmed on unchanged state).
        public bool DeferPost { get; set; }
        public Queue<Action> Posted { get; } = new();
        public void DrainPost() { while (Posted.Count > 0) Posted.Dequeue()(); }

        // Delayed re-evaluations HealthManager asked for (the party @ok settle).
        // SettleOk moves the clock past the window and runs them.
        public List<Action> Scheduled { get; } = new();
        public void SettleOk()
        {
            Clock += TimeSpan.FromSeconds(1);
            List<Action> due = new(Scheduled);
            Scheduled.Clear();
            foreach (Action a in due) a();
        }

        // Char-tier Party settings — drives the @panic send gate
        // (UsePanicWhileLeading) read by HealthManager. Default instance has
        // both panic flags off.
        public PartySettings Party { get; set; } = new();

        // Whether HealthManager believes we're leading a party — the
        // @panic broadcast only fires while leading. AppServices wires this to
        // PartyState.IsInParty && PartyState.SelfIsLeader. Default false.
        public bool SelfIsLeader { get; set; }

        public Harness(HealthSettings? settings = null)
        {
            Settings = settings ?? new HealthSettings();
            Coordinator = new MovementCoordinator(Log);
            Health = new HealthManager(State, Coordinator,
                readSettings: () => Settings,
                isEnabled: () => AutoHealRestEnabled && !MasterSwitchOff,
                readHangupCommand: () => HangupCommand ?? string.Empty,
                getActiveMovementEngine: null,
                getLastSentDirection: null,
                readCombatSettings: null,
                readGeneralSettings: () => General,
                hasEngageableHostiles: () => HostilesPresent,
                readDeathFloor: () => DeathFloor,
                log: Log,
                hangupSignal: Hangup,
                hasHostileInRoom: () => HostileInRoom,
                post: a => { if (DeferPost) Posted.Enqueue(a); else a(); },
                now: () => Clock,
                readPartySettings: () => Party,
                selfIsPartyLeader: () => SelfIsLeader);
            Health.SetWireSender(b => Sent.Add(b));
            Health.SetHangupDisconnect(() => HangupDisconnectCount++);
            Health.SetWimpyGoto(name => { WimpyFiredWith = name; return WimpyFireResult; });
            Health.SetShadowRest(
                shadowRestClass: () => ShadowRestClass,
                isStealthed: () => Stealthed,
                isSolo: () => Solo,
                onRecovered: () => ShadowRestResumeCount++);
            Health.SetRestEnabledGate(() => RestEnabled && !MasterSwitchOff);
            Health.SetMasterSwitch(() => MasterSwitchOff, what => HeldByMasterSwitch.Add(what));
            Health.SetDoNotRestSelector(() => SkipRestHere);
            Health.SetRestHereSelector(() => RestHere);
            Health.SetEquipmentApplyingProbe(() => EquipmentApplying);
            Health.SetScheduler((_, a) => Scheduled.Add(a));
            Health.SetPartyRoleSync(
                isPartyFollower: () => false,
                requestPartyWait: () => { },
                requestPartyOk: () => { },
                isSelfPoisoned: () => Poisoned);
            Health.SetRestClearEngage(
                isAutoCombatEnabled: () => AutoCombatEnabled,
                requestEngage: () => RestClearEngageCount++);
        }

        // Mirror PromptParser's write order: values
        // first, HasPromptData last. Anything else and the engine
        // sees Hp=0 with HasPromptData=true and asserts spuriously
        // — same race a sloppy producer would hit in production.
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

    [Fact]
    public void DisabledMidRest_ReleasesHealthGate()
    {
        // The reported strand: HP below the rest trigger holds the recovery
        // gate, pausing the walker to rest. Toggling Auto-Heal/Rest off must
        // release that gate at once — the view-model calls Evaluate on the
        // flip — so a queued walk-to resumes instead of the character sitting
        // idle resting until HP happens to climb back to target.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;                 // below 60% trigger → gate held, resting
        Assert.True(h.HealthGateHeld);

        h.AutoHealRestEnabled = false;   // user flips Auto-Heal/Rest off
        h.Health.Evaluate();             // VM re-evaluates the engine on the flip
        Assert.False(h.HealthGateHeld);  // gate released → walker resumes
    }

    // ----- Auto-Rest off, engine on (Auto-Heal only) ------------------

    [Fact]
    public void RestOff_LowHp_RaisesNoHold_AndSendsNoRest()
    {
        using Harness h = new() { RestEnabled = false };
        h.SetPrompt(hp: 50, maxHp: 200);   // well under the 60% rest trigger
        Assert.False(h.HealthGateHeld);
        Assert.DoesNotContain("rest", h.SentLines);
    }

    [Fact]
    public void RestSwitchedOffMidRest_ReleasesTheHold_AndBackOnRestsAgain()
    {
        using Harness h = new();
        h.SetPrompt(hp: 50, maxHp: 200);
        Assert.True(h.HealthGateHeld);

        h.RestEnabled = false;
        h.Health.Evaluate();               // the view-model re-evaluates on the flip
        Assert.False(h.HealthGateHeld);

        h.RestEnabled = true;
        h.Health.Evaluate();
        Assert.True(h.HealthGateHeld);
    }

    [Fact]
    public void RestOff_RestUpHereRoom_DoesNotRest()
    {
        using Harness h = new() { RestEnabled = false, RestHere = (true, true) };
        h.SetPrompt(hp: 150, maxHp: 200);  // above the trigger, under rest-max
        Assert.False(h.HealthGateHeld);
        Assert.False(h.Health.HoldForRestHere());
    }

    [Fact]
    public void RestOff_NoLowHpHangup_UntilAutoRestIsBackOn()
    {
        // The low-HP hang-up sits behind Auto-Rest (user, 2026-10-09): healing by
        // spell alone no longer carries it.
        using Harness h = new() { RestEnabled = false };
        h.SetPrompt(hp: 5, maxHp: 200);
        Assert.DoesNotContain("=x", h.SentLines);

        h.RestEnabled = true;
        h.Health.Evaluate();
        Assert.Equal(1, h.SentLines.Count(l => l == "=x"));
    }

    // ----- absolute threshold mode -----------------------------------

    [Fact]
    public void AbsoluteMode_AtTriggerValue_DoesNotAssert()
    {
        // "Rest if below 60" is strictly below — exactly 60 must NOT trigger.
        HealthSettings s = new()
        {
            HpThresholdMode = ThresholdMode.Absolute,
            RestIfBelowHp   = 60,
            RestMaxHp       = 195,
        };
        using Harness h = new(s);
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 60;             // exactly at 60 absolute → not below

        Assert.False(h.HealthGateHeld);
    }

    [Fact]
    public void AbsoluteMode_BelowTriggerValue_Asserts()
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
        h.State.Hp = 59;             // strictly below 60 → trigger

        Assert.True(h.HealthGateHeld);
    }

    // A rest typed by hand above the rest trigger asserts no gate, but it is still a
    // rest under way until HP reaches rest-max — the in-place re-sneak reads this.
    [Fact]
    public void RestingShortOfRestMax_TracksPostureAndRestMax()
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
        h.State.Hp = 150;

        Assert.False(h.Health.RestingShortOfRestMax);     // standing

        h.State.Position = PlayerPosition.Resting;
        Assert.False(h.HealthGateHeld);
        Assert.True(h.Health.RestingShortOfRestMax);

        h.State.Hp = 195;
        Assert.False(h.Health.RestingShortOfRestMax);     // topped off
    }

    [Fact]
    public void ManaTriggerZero_AtZeroMana_DoesNotAssert()
    {
        // The reported repro: a level-2 mystic with 1 max KAI and rest-if-
        // below 0 spends the KAI → MA 0. With strict-below, 0 is not below
        // the 0 trigger, so no spurious mana-rest pause.
        HealthSettings s = new()
        {
            MaThresholdMode = ThresholdMode.Absolute,
            RestIfBelowMa   = 0,
        };
        using Harness h = new(s);
        h.State.MaxMa = 1;
        h.State.HasPromptData = true;
        h.State.Ma = 0;

        Assert.False(h.ManaGateHeld);
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

    // A second `rest` restarts the game's count toward the next rest tick, so a rest
    // already under way (typed by hand, or left running while the engine was off) is
    // taken over, not re-sent.
    [Fact]
    public void AlreadyResting_TakesTheRestOver_WithoutSendingRestAgain()
    {
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Position = PlayerPosition.Resting;   // the player typed `rest` themselves
        h.State.Hp = 50;

        Assert.True(h.HealthGateHeld);
        Assert.DoesNotContain("rest", h.SentLines);
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

    // ----- momentary breach: recovers before the rest command commits -------
    // Regression for paradigm-20260912-093819: PromptScanner parses prompt
    // digits ahead of the rest of a wire read, so a spell/attack that drops a
    // pool below its trigger can be followed, within the same burst, by a
    // regen tick that brings it back up. The gate still asserts immediately
    // (the walker still pauses on the spot, unchanged), but the actual
    // rest/meditate SEND waits one dispatch tick (ConfirmHpGate / ConfirmMaGate)
    // to re-check the trigger before committing. These tests drive that tick
    // explicitly via Harness.DeferPost + DrainPost.

    [Fact]
    public void MaGateAsserted_RecoversAboveTriggerBeforeConfirm_NeverSendsRest()
    {
        HealthSettings s = new()
        {
            MaThresholdMode = ThresholdMode.Absolute,
            RestIfBelowMa = 197,
            RestMaxMa = 350,
            UseMeditateAbility = true,
        };
        using Harness h = new(s) { DeferPost = true };

        h.SetPrompt(hp: 362, maxHp: 362, ma: 190, maxMa: 394);   // strictly below 197 — gate asserts
        Assert.True(h.ManaGateHeld);
        Assert.Empty(h.SentLines);            // send withheld pending confirm
        Assert.False(h.Health.RestInFlight);

        h.SetPrompt(hp: 362, maxHp: 362, ma: 245, maxMa: 394);   // regen tick lands before confirm runs — above trigger
        Assert.False(h.ManaGateHeld);         // cleared immediately — recovered above trigger, confirm not even needed
        Assert.Empty(h.SentLines);

        h.DrainPost();                        // the deferred re-check runs — no-op, gate's already gone

        Assert.False(h.ManaGateHeld);         // retracted — the breach was momentary
        Assert.Empty(h.SentLines);            // meditate was never sent
        Assert.False(h.Health.RestInFlight);
    }

    [Fact]
    public void MaGateAsserted_StillBelowTriggerAtConfirm_SendsMeditateAfterConfirm()
    {
        HealthSettings s = new()
        {
            MaThresholdMode = ThresholdMode.Absolute,
            RestIfBelowMa = 197,
            RestMaxMa = 350,
            UseMeditateAbility = true,
        };
        using Harness h = new(s) { DeferPost = true };

        h.SetPrompt(hp: 362, maxHp: 362, ma: 150, maxMa: 394);   // genuinely low — well below trigger
        Assert.True(h.ManaGateHeld);
        Assert.Empty(h.SentLines);            // still withheld pending confirm

        h.DrainPost();                        // confirm runs — still below trigger

        Assert.True(h.ManaGateHeld);
        Assert.Contains("meditate", h.SentLines);
        Assert.True(h.Health.RestInFlight);
    }

    // The engine-disabled branch resets every other in-flight latch, so the gate
    // CONFIRMATIONS have to drop there too. They're only ever cleared alongside an
    // asserted gate, so one left standing across the toggle can never be retracted
    // afterwards — it feeds the send path with no gate asserted, sitting the
    // character down at full mana and doing it again on every rest completion.
    [Fact]
    public void AutoHealToggledOffWhileConfirmed_ThenBackOnAtFullMa_NeverSendsMeditate()
    {
        HealthSettings s = new()
        {
            MaThresholdMode = ThresholdMode.Absolute,
            RestIfBelowMa = 197,
            RestMaxMa = 350,
            UseMeditateAbility = true,
        };
        using Harness h = new(s) { DeferPost = true };

        h.SetPrompt(hp: 362, maxHp: 362, ma: 150, maxMa: 394);   // genuine breach
        h.DrainPost();                                           // confirmed → meditate goes out
        Assert.Contains("meditate", h.SentLines);

        h.AutoHealRestEnabled = false;                           // user flips Auto-Heal / Rest off
        h.Health.Evaluate();
        h.Sent.Clear();

        h.SetPrompt(hp: 362, maxHp: 362, ma: 394, maxMa: 394);   // tops off while the engine is off
        h.AutoHealRestEnabled = true;                            // and back on
        h.Health.Evaluate();
        h.DrainPost();

        Assert.False(h.ManaGateHeld);
        Assert.Empty(h.SentLines);
        Assert.False(h.Health.RestInFlight);
    }

    [Fact]
    public void HpGateAsserted_RecoversAboveTriggerBeforeConfirm_NeverSendsRest()
    {
        HealthSettings s = new()
        {
            HpThresholdMode = ThresholdMode.Absolute,
            RestIfBelowHp = 100,
            RestMaxHp = 195,
        };
        using Harness h = new(s) { DeferPost = true };

        h.SetPrompt(hp: 90, maxHp: 200);      // below 100 — gate asserts
        Assert.True(h.HealthGateHeld);
        Assert.Empty(h.SentLines);

        h.SetPrompt(hp: 150, maxHp: 200);     // heal/regen lands before confirm — above trigger
        h.DrainPost();

        Assert.False(h.HealthGateHeld);
        Assert.Empty(h.SentLines);
        Assert.False(h.Health.RestInFlight);
    }

    // ----- confirmed mid-combat breach that fully recovers before combat ends ---
    // Regression for paradigm-20260912-103110: the one-tick confirm above only
    // catches a same-burst regen recovery. A genuine breach confirmed mid-fight
    // can still fully self-resolve over several more rounds of regen, well
    // before the fight itself ends — holding the confirmed gate out for the
    // full rest-target (instead of the ordinary trigger) meant combat ending
    // committed to a rest/meditate the pool wasn't actually low for anymore.

    [Fact]
    public void MaGateConfirmed_RecoversAboveTriggerWhileStillInCombat_NoRestWhenCombatEnds()
    {
        HealthSettings s = new()
        {
            MaThresholdMode = ThresholdMode.Absolute,
            RestIfBelowMa = 197,
            RestMaxMa = 335,
            UseMeditateAbility = true,
        };
        using Harness h = new(s) { DeferPost = true };
        h.State.InCombat = true;

        h.SetPrompt(hp: 362, maxHp: 362, ma: 150, maxMa: 394);   // genuine breach, still fighting
        Assert.True(h.ManaGateHeld);
        h.DrainPost();                                           // confirm runs — still below trigger
        Assert.True(h.ManaGateHeld);
        Assert.Empty(h.SentLines);                                // withheld — still in combat

        h.SetPrompt(hp: 362, maxHp: 362, ma: 229, maxMa: 394);   // regen climbs it back past 197, fight still on
        Assert.False(h.ManaGateHeld);                             // cleared at the trigger, not held for target
        Assert.Empty(h.SentLines);

        h.State.InCombat = false;                                 // combat ends
        Assert.Empty(h.SentLines);                                // never rests — it wasn't low anymore
        Assert.False(h.Health.RestInFlight);
    }

    [Fact]
    public void HpGateConfirmed_RecoversAboveTriggerWhileStillInCombat_NoRestWhenCombatEnds()
    {
        HealthSettings s = new()
        {
            HpThresholdMode = ThresholdMode.Absolute,
            RestIfBelowHp = 100,
            RestMaxHp = 195,
        };
        using Harness h = new(s) { DeferPost = true };
        h.State.InCombat = true;

        h.SetPrompt(hp: 80, maxHp: 200);       // genuine breach, still fighting
        Assert.True(h.HealthGateHeld);
        h.DrainPost();                          // confirm runs — still below trigger
        Assert.True(h.HealthGateHeld);
        Assert.Empty(h.SentLines);

        h.SetPrompt(hp: 150, maxHp: 200);      // heals back past 100, fight still on
        Assert.False(h.HealthGateHeld);
        Assert.Empty(h.SentLines);

        h.State.InCombat = false;               // combat ends
        Assert.Empty(h.SentLines);
        Assert.False(h.Health.RestInFlight);
    }

    // report paradigm-20260921-114318: a Pre-rest gear set that adds max HP inflates
    // live HP, so the rest "completes" at the Default-anchored target — then reverting
    // to Default strips that pool and drops HP back below the trigger, an endless
    // gear-swap/rest thrash. The resting clear floor must hold until DEFAULT-equivalent
    // HP reaches the target, so HP stays >= trigger once the boosting gear comes off.
    [Fact]
    public void RestClearFloor_HeldByPreRestPoolBoost_UntilDefaultEquivalentHitsTarget()
    {
        HealthSettings s = new()
        {
            HpThresholdMode = ThresholdMode.Percentage,
            RestIfBelowHp = 40,   // trigger = 40% of the Default basis
            RestMaxHp = 50,       // target  = 50% of the Default basis
        };
        using Harness h = new(s);
        // Default-set basis 1000 (trigger 400, target 500); a Pre-rest +100-HP ring is
        // worn, so live max is 1100 (boost 100). Real max well above.
        h.Health.SetRestPoolMaxProviders(
            defaultSetMaxHp: () => 1000, defaultSetMaxMa: () => 0,
            realMaxHp: () => 2000, realMaxMa: () => 0);

        h.SetPrompt(hp: 390, maxHp: 1100);      // below trigger 400 → rest
        Assert.Contains("rest", h.SentLines);
        Assert.True(h.Health.RestInFlight);

        // Live HP 590 clears the Default target 500 — but only because the +100 ring is
        // on; default-equivalent is 490, still under target. Must NOT finish the rest
        // (pre-fix it cleared here, then the revert dropped HP to 490 and re-asserted).
        h.SetPrompt(hp: 590, maxHp: 1100);
        Assert.True(h.HealthGateHeld);

        // Live HP 600 → default-equivalent 500 = target. Finish; reverting to Default
        // leaves HP 500, safely above the 400 trigger — no re-assert, no thrash.
        h.SetPrompt(hp: 600, maxHp: 1100);
        Assert.False(h.HealthGateHeld);
    }

    // ----- combat-end / same-burst recovery race (successor to -103110) ---------
    // Regression for paradigm-20260912-123108: -103110's fix (above) closes the
    // gap where recovery lands as its OWN Evaluate tick while still in combat.
    // But InCombat and the recovered Hp/Ma can update in the SAME wire burst as
    // combat itself ending, as two separate PropertyChanged events fired back to
    // back — and if InCombat's fires first, THIS Evaluate call sees the gate
    // still confirmed against the stale, about-to-be-overwritten low reading and
    // would otherwise commit the send right then. The combat-end edge now resets
    // a confirmed gate and re-defers through the same one-tick ConfirmHpGate /
    // ConfirmMaGate check the initial breach uses, so a same-burst recovery that
    // lands moments after InCombat flips false still retracts the gate instead
    // of sitting down for a pool that isn't actually low anymore.

    [Fact]
    public void MaGateConfirmed_CombatEndsBeforeSameBurstRecoveryApplies_NeverSendsRest()
    {
        HealthSettings s = new()
        {
            MaThresholdMode = ThresholdMode.Absolute,
            RestIfBelowMa = 197,
            RestMaxMa = 335,
            UseMeditateAbility = true,
        };
        using Harness h = new(s) { DeferPost = true };
        h.State.InCombat = true;

        h.SetPrompt(hp: 362, maxHp: 362, ma: 191, maxMa: 394);   // genuine breach mid-fight
        Assert.True(h.ManaGateHeld);
        h.DrainPost();                                            // confirm runs — still below trigger
        Assert.True(h.ManaGateHeld);
        Assert.Empty(h.SentLines);

        // Same wire burst as the kill landing: InCombat flips false BEFORE the
        // accompanying regen tick updates Ma, so Ma is still the stale 191 here.
        h.State.InCombat = false;
        Assert.Empty(h.SentLines);            // send withheld — reconfirming instead of trusting the stale value
        Assert.False(h.Health.RestInFlight);

        h.SetPrompt(hp: 362, maxHp: 362, ma: 239, maxMa: 394);   // the burst's regen tick lands now
        h.DrainPost();                                            // deferred reconfirm runs

        Assert.False(h.ManaGateHeld);
        Assert.Empty(h.SentLines);            // meditate never sent
        Assert.False(h.Health.RestInFlight);
    }

    [Fact]
    public void MaGateConfirmed_CombatEndsWithGenuineBreachStillLow_SendsMeditateAfterReconfirm()
    {
        HealthSettings s = new()
        {
            MaThresholdMode = ThresholdMode.Absolute,
            RestIfBelowMa = 197,
            RestMaxMa = 335,
            UseMeditateAbility = true,
        };
        using Harness h = new(s) { DeferPost = true };
        h.State.InCombat = true;

        h.SetPrompt(hp: 362, maxHp: 362, ma: 150, maxMa: 394);   // genuine breach, well below trigger
        h.DrainPost();                                            // confirm runs — still below trigger
        Assert.True(h.ManaGateHeld);
        Assert.Empty(h.SentLines);

        h.State.InCombat = false;              // combat ends — pool never recovered
        Assert.Empty(h.SentLines);              // withheld one more tick for the combat-end reconfirm
        h.DrainPost();                          // reconfirm runs — still genuinely below trigger

        Assert.True(h.ManaGateHeld);
        Assert.Contains("meditate", h.SentLines);
        Assert.True(h.Health.RestInFlight);
    }

    [Fact]
    public void HpGateConfirmed_CombatEndsBeforeSameBurstRecoveryApplies_NeverSendsRest()
    {
        HealthSettings s = new()
        {
            HpThresholdMode = ThresholdMode.Absolute,
            RestIfBelowHp = 100,
            RestMaxHp = 195,
        };
        using Harness h = new(s) { DeferPost = true };
        h.State.InCombat = true;

        h.SetPrompt(hp: 80, maxHp: 200);       // genuine breach, still fighting
        h.DrainPost();                          // confirm runs — still below trigger
        Assert.True(h.HealthGateHeld);
        Assert.Empty(h.SentLines);

        // Same wire burst: InCombat flips false before the heal/regen line updates Hp.
        h.State.InCombat = false;
        Assert.Empty(h.SentLines);
        Assert.False(h.Health.RestInFlight);

        h.SetPrompt(hp: 150, maxHp: 200);      // the burst's heal lands now
        h.DrainPost();

        Assert.False(h.HealthGateHeld);
        Assert.Empty(h.SentLines);
        Assert.False(h.Health.RestInFlight);
    }

    [Fact]
    public void PoisonCleared_ReRests_AfterUnconfirmedLatch()
    {
        // Repro (report paradigm-20260817-092945): below the rest floor + poisoned, a rest
        // is sent but never confirms (poison keeps Position=Standing), so _restInFlight
        // latches and the two-step interruption latch can't clear it (it needs a confirmed
        // Resting first). Once poison wears off the stale latch blocked the re-send and the
        // character stood below the floor forever. The poison falling edge must drop it.
        using Harness h = new();
        h.Poisoned = true;
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;                 // 25% — gate asserts, rest sent (never confirmed)

        Assert.True(h.HealthGateHeld);
        Assert.True(h.Health.RestInFlight);
        Assert.Equal(1, h.SentLines.Count(x => x == "rest"));   // one send, then latched

        // Still poisoned, HP ticks — no re-send (latched).
        h.State.Hp = 52;
        h.State.Hp = 51;
        Assert.Equal(1, h.SentLines.Count(x => x == "rest"));

        // Poison wears off; the next regen tick fires Evaluate.
        h.Poisoned = false;
        h.State.Hp = 53;

        // Stale latch dropped → a fresh rest re-sent now that resting will take.
        Assert.Equal(2, h.SentLines.Count(x => x == "rest"));
        Assert.True(h.Health.RestInFlight);
    }

    [Fact]
    public void RestRefusedSick_HoldsTheReSend_ThenRetries()
    {
        // "You are too sick to rest!" with no poison on record (the par screen hasn't
        // shown it yet): the latch drops, the re-send waits, then retries.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        Assert.Equal(1, h.SentLines.Count(x => x == "rest"));

        h.Health.NoteRestRefusedSick();
        Assert.False(h.Health.RestInFlight);
        h.State.Hp = 51;
        Assert.Equal(1, h.SentLines.Count(x => x == "rest"));   // held

        h.Clock += TimeSpan.FromSeconds(16);
        h.State.Hp = 52;
        Assert.Equal(2, h.SentLines.Count(x => x == "rest"));   // retried
    }

    [Fact]
    public void RestRefusedSick_WhilePoisoned_WaitsForThePoisonToClear()
    {
        using Harness h = new();
        h.Poisoned = true;
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        h.Health.NoteRestRefusedSick();

        h.Clock += TimeSpan.FromSeconds(60);
        h.State.Hp = 51;
        Assert.Equal(1, h.SentLines.Count(x => x == "rest"));   // still poisoned: no retry

        h.Poisoned = false;
        h.State.Hp = 52;
        Assert.Equal(2, h.SentLines.Count(x => x == "rest"));
    }

    [Fact]
    public void MeditateNotNeeded_ManaCountsAsFull()
    {
        // "Meditation will not help at this time.": the game only says it with mana
        // full, so that's rested whatever our max says — no rest, no re-sent meditate.
        using Harness h = new();
        h.Settings.UseMeditateAbility = true;
        h.SetPrompt(hp: 200, maxHp: 200, ma: 5, maxMa: 100);
        Assert.Equal("meditate", h.LastSent);

        h.Health.NoteMeditateNotNeeded();
        h.State.Ma = 6;

        Assert.False(h.ManaGateHeld);
        Assert.Equal("meditate", h.LastSent);
    }

    // A later, higher reading raises the game's full mark, so dropping back below it
    // is a real deficit again.
    [Fact]
    public void MeditateNotNeeded_HigherReadingRaisesTheFullMark()
    {
        using Harness h = new();
        h.Settings.UseMeditateAbility = true;
        h.SetPrompt(hp: 200, maxHp: 200, ma: 5, maxMa: 100);
        h.Health.NoteMeditateNotNeeded();
        h.State.Ma = 100;
        h.State.Ma = 20;

        Assert.True(h.ManaGateHeld);
    }

    [Fact]
    public void GateAsserted_HostilesInRoom_DoesNotRest()
    {
        // User direction: "if a room has hostiles it will break resting
        // every combat round preventing you from resting, so you need
        // to clear the room and then rest". Block the rest-out branch
        // while CombatStateTracker says a hostile is here.
        using Harness h = new() { HostilesPresent = true };
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;

        Assert.True(h.HealthGateHeld);
        Assert.DoesNotContain("rest", h.SentLines);
        Assert.False(h.Health.RestInFlight);
    }

    [Fact]
    public void GateAsserted_RoomCleared_ThenRest()
    {
        // We took damage while a hostile is alive — gate held but rest
        // blocked. When CombatManager kills it (HasEngageableHostiles
        // flips false), the next Evaluate tick fires rest.
        using Harness h = new() { HostilesPresent = true };
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        Assert.True(h.HealthGateHeld);
        Assert.False(h.Health.RestInFlight);

        h.HostilesPresent = false;   // mob died → room cleared
        h.Health.Evaluate();          // CombatStateTracker would call this via the
                                       // standard property-changed plumbing; tests
                                       // drive it explicitly.

        Assert.True(h.Health.RestInFlight);
        Assert.Contains("rest", h.SentLines);
    }

    [Fact]
    public void RestClear_ArmsOffInCombat_WhenRosterStale_CombatOff()
    {
        // Chased into a rest mid-walk with combat off: a monster is HITTING us (InCombat)
        // but the last room view hasn't listed it (HostilesPresent false — a stale roster).
        // InCombat blocks the rest send and a stale roster would block the clear — the
        // deadlock. The clear must arm off InCombat and poke the engage so we fight the
        // blocker instead of sitting (RequestRestClearEngage refreshes the roster to find it).
        using Harness h = new() { AutoCombatEnabled = false, HostilesPresent = false };
        h.State.InCombat = true;      // combat lines keep us in combat (being attacked)
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;              // below the rest trigger, above the 20% flee trigger

        Assert.True(h.Health.ForceClearForRest);
        Assert.True(h.RestClearEngageCount > 0);
    }

    [Fact]
    public void RestClear_DoesNotArm_NoBlocker_CombatOff()
    {
        // Combat off, rest due, but nothing is attacking us — no roster hostile AND not
        // InCombat. The clear must NOT arm (there's nothing to fight); we just rest.
        using Harness h = new() { AutoCombatEnabled = false, HostilesPresent = false };
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;

        Assert.False(h.Health.ForceClearForRest);
        Assert.Equal(0, h.RestClearEngageCount);
    }

    [Fact]
    public void EquipmentApplying_HoldsRest_ThenRestsWhenSwapDone()
    {
        // A pre-rest gear swap streams paced wear/rem, each of which stands the
        // character; without a hold the rest engine re-fires `rest` between every
        // command — the rest/stand thrash of report paradigm-20260825-103537. Hold
        // the rest while the swap is in flight, then rest once it finishes.
        using Harness h = new() { EquipmentApplying = true };
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        Assert.True(h.HealthGateHeld);
        Assert.DoesNotContain("rest", h.SentLines);   // held during the swap
        Assert.False(h.Health.RestInFlight);

        h.EquipmentApplying = false;   // swap finished — character standing with new gear
        h.Health.Evaluate();

        Assert.Contains("rest", h.SentLines);          // now rests, one command
        Assert.True(h.Health.RestInFlight);
    }

    // A wear after the sit breaks the rest and resting again restarts its timer, so
    // the rest gear goes on first and `rest` follows once it's worn (user,
    // 2026-09-29).
    [Fact]
    public void RestGear_GoesOnBeforeTheRest()
    {
        using Harness h = new();
        int swaps = 0;
        bool worn = false;
        h.Health.SetRestGearFirst(() =>
        {
            if (worn) return false;          // already on — nothing to swap
            swaps++;
            h.EquipmentApplying = true;
            return true;
        });
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;

        Assert.True(h.HealthGateHeld);
        Assert.Equal(1, swaps);
        Assert.DoesNotContain("rest", h.SentLines);   // held for the swap
        Assert.False(h.Health.RestInFlight);

        worn = true;
        h.EquipmentApplying = false;                  // the swap streamed
        h.Health.Evaluate();

        Assert.Contains("rest", h.SentLines);
        Assert.True(h.Health.RestInFlight);
        Assert.Equal(1, swaps);
    }

    [Fact]
    public void ForceClear_ThenHostileReconfirmed_DoesNotRest()
    {
        // The idle-stall watchdog force-cleared combat while a monster was still in
        // the room (its attacks weren't recognized for a beat). InCombat + the room
        // model read "empty" in the flicker, so a plain rest-out would fire. The hold
        // must block it until the resync re-display re-confirms — then the still-here
        // monster blocks the rest (paradigm-20260814-225055).
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.Health.NoteCombatForceCleared();   // watchdog optimistic clear → hold armed
        h.State.Hp = 50;                      // breach → gate held, but rest is HELD

        Assert.True(h.HealthGateHeld);
        Assert.DoesNotContain("rest", h.SentLines);
        Assert.False(h.Health.RestInFlight);

        h.HostilesPresent = true;                 // resync re-display: monster still here
        h.Health.NoteRoomEntitiesReconfirmed();   // release hold + re-evaluate

        Assert.DoesNotContain("rest", h.SentLines);   // hostiles present → still no rest
        Assert.False(h.Health.RestInFlight);
    }

    [Fact]
    public void ForceClear_ThenEmptyReconfirmed_Rests()
    {
        // Same force-clear, but the resync re-display confirms the room is genuinely
        // empty (a real stale-roster clear) — the held rest is released and fires.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.Health.NoteCombatForceCleared();
        h.State.Hp = 50;                      // held, not rested

        Assert.False(h.Health.RestInFlight);
        Assert.DoesNotContain("rest", h.SentLines);

        h.Health.NoteRoomEntitiesReconfirmed();   // room empty (HostilesPresent stays false)

        Assert.True(h.Health.RestInFlight);
        Assert.Contains("rest", h.SentLines);
    }

    [Fact]
    public void ForceClear_EmptyStaticRoom_NeverReconfirms_TimeoutReleasesHoldAndRests()
    {
        // reports paradigm-20260818-050950 / -092532: an empty, static room emits no
        // "Also here:" line and a stationary character triggers no room change, so the
        // reconfirm hold never clears and auto-rest is stuck off — below the threshold,
        // out of combat, yet never resting. The timeout backstop releases the hold so a
        // genuinely clear room rests without waiting on a re-display that never comes.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.Health.NoteCombatForceCleared();   // hold armed at the current clock
        h.State.Hp = 50;                      // breach → held, no rest yet

        Assert.False(h.Health.RestInFlight);
        Assert.DoesNotContain("rest", h.SentLines);

        // No reconfirm ever arrives (empty static room, no hostiles). Time passes beyond
        // the backstop; the next Evaluate tick releases the hold and rests.
        h.Clock += TimeSpan.FromSeconds(5);
        h.Health.Evaluate();

        Assert.True(h.Health.RestInFlight);
        Assert.Contains("rest", h.SentLines);
    }

    [Fact]
    public void ForceClear_TimeoutReleases_RestsPastStaleHostileLatch()
    {
        // paradigm-20260827-082222: the idle-stall "room empty" force-clear does NOT
        // clear the combat tracker's hostile latch (an empty room emits no occupant
        // line to re-derive it), so it stays stuck true from the last hostile-filled
        // room. A stationary character held below the trigger then sat forever — its
        // meditate/rest blocked by the stale hostiles guard while it passively
        // regenerated ("meditating state but not Medding / no gear swap"). After the
        // reconfirm-timeout with no re-display, the room is safe: the held rest fires
        // past the stale latch.
        using Harness h = new() { HostilesPresent = true };  // stale latch, room really empty
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.Health.NoteCombatForceCleared();   // idle-stall "room empty" → hold armed
        h.State.Hp = 50;                      // breach → held (stale hostile blocks the rest)

        Assert.False(h.Health.RestInFlight);
        Assert.DoesNotContain("rest", h.SentLines);

        // Past the backstop with no re-display: release the hold AND bypass the stale
        // hostile latch so the genuinely-clear room rests.
        h.Clock += TimeSpan.FromSeconds(5);
        h.Health.Evaluate();

        Assert.True(h.Health.RestInFlight);
        Assert.Contains("rest", h.SentLines);
    }

    [Fact]
    public void ForceClear_ThenRoomChange_ReleasesHoldAndRests()
    {
        // Dark-room force-clear sends no resync CR, so no EntitiesObserved arrives;
        // a move re-observes the room and releases the hold instead.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.Health.NoteCombatForceCleared();
        h.State.Hp = 50;                      // held
        Assert.False(h.Health.RestInFlight);

        h.Moved();           // a move re-observes
        h.Health.Evaluate();

        Assert.True(h.Health.RestInFlight);
        Assert.Contains("rest", h.SentLines);
    }

    [Fact]
    public void RestingAndHostileArrives_DoesNotReSpamRest()
    {
        // While resting, a new hostile walks in. The rest gets broken
        // server-side; our latch drops via room-change or InCombat flip.
        // Until CombatManager clears the new mob, we must NOT spam
        // another rest.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        Assert.Contains("rest", h.SentLines);
        int firstRestCount = h.SentLines.Count(l => l == "rest");

        // Mob arrived → CombatStateTracker flips HasEngageableHostiles
        // true; our latch is still _restInFlight=true until rest breaks.
        h.HostilesPresent = true;
        // Server breaks rest because we took damage / position changed —
        // simulate the position flip + Evaluate tick.
        h.State.Position = PlayerPosition.Standing;
        h.Health.Evaluate();

        // No second rest while hostile is here.
        int afterHostileRestCount = h.SentLines.Count(l => l == "rest");
        Assert.Equal(firstRestCount, afterHostileRestCount);
    }

    // ----- ShadowRest (Paradigm): rest with a hostile in the room -----

    [Fact]
    public void ShadowRest_HostilesPresent_StillRests()
    {
        // Solo, stealthed, ShadowRest-capable class, opted in: the game keeps
        // us un-attacked while stealthed, so the hostiles guard is relaxed and
        // the gated rest goes out even with a mob in the room.
        HealthSettings s = new() { UtilizeShadowRest = true };
        using Harness h = new(s) { HostilesPresent = true };
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;

        Assert.True(h.HealthGateHeld);
        Assert.Contains("rest", h.SentLines);
        Assert.True(h.Health.RestInFlight);
        Assert.True(h.Health.ShadowRestHolding);
    }

    [Fact]
    public void ShadowRest_SettingOff_HostilesBlockRest()
    {
        // Same capable/stealthed/solo character, but UtilizeShadowRest off
        // (default) → shadowRest inactive, the hostiles guard applies as normal.
        using Harness h = new() { HostilesPresent = true };
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;

        Assert.True(h.HealthGateHeld);
        Assert.DoesNotContain("rest", h.SentLines);
        Assert.False(h.Health.RestInFlight);
        Assert.False(h.Health.ShadowRestHolding);
    }

    [Fact]
    public void ShadowRest_NonShadowRestClass_HostilesBlockRest()
    {
        // Opted in and stealthed/solo, but the class lacks ability 1103 →
        // ShadowRest never engages, hostiles still block the rest.
        HealthSettings s = new() { UtilizeShadowRest = true };
        using Harness h = new(s) { HostilesPresent = true, ShadowRestClass = false };
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;

        Assert.DoesNotContain("rest", h.SentLines);
        Assert.False(h.Health.RestInFlight);
        Assert.False(h.Health.ShadowRestHolding);
    }

    [Fact]
    public void ShadowRest_NotStealthed_HostilesBlockRest()
    {
        // ShadowRest needs the stealth cover — standing in the open, the game
        // will happily attack us, so we must not rest into a mob.
        HealthSettings s = new() { UtilizeShadowRest = true };
        using Harness h = new(s) { HostilesPresent = true, Stealthed = false };
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;

        Assert.DoesNotContain("rest", h.SentLines);
        Assert.False(h.Health.RestInFlight);
        Assert.False(h.Health.ShadowRestHolding);
    }

    [Fact]
    public void ShadowRest_InParty_HostilesBlockRest()
    {
        // ShadowRest is a solo behavior — resting hidden un-targets party heals,
        // so in a party the guard stays in force.
        HealthSettings s = new() { UtilizeShadowRest = true };
        using Harness h = new(s) { HostilesPresent = true, Solo = false };
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;

        Assert.DoesNotContain("rest", h.SentLines);
        Assert.False(h.Health.RestInFlight);
        Assert.False(h.Health.ShadowRestHolding);
    }

    [Fact]
    public void ShadowRest_RecoveryToRestMax_FiresResumeOnce()
    {
        // Option A: rest until rest-max, then the held gate clears and the
        // falling edge fires the resume callback exactly once so CombatManager
        // re-runs the room and opens with the still-armed backstab.
        HealthSettings s = new() { UtilizeShadowRest = true };
        using Harness h = new(s) { HostilesPresent = true };
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;              // gate held, resting, ShadowRest holding
        Assert.True(h.Health.ShadowRestHolding);
        Assert.Equal(0, h.ShadowRestResumeCount);

        h.State.Hp = 190;             // 95% rest-max → gate clears
        Assert.False(h.Health.ShadowRestHolding);
        Assert.Equal(1, h.ShadowRestResumeCount);

        // Idempotent — a further tick past rest-max doesn't re-fire.
        h.State.Hp = 200;
        Assert.Equal(1, h.ShadowRestResumeCount);
    }

    // A buff cast mid-rest ends the sneak for a moment: that isn't recovery, so the
    // resume (which re-runs the room for a fight) waits for the rest gate to clear
    // (report paradigm-20260930-192045).
    [Fact]
    public void ShadowRest_SneakLapsesMidRest_ResumesOnlyAtRestMax()
    {
        HealthSettings s = new() { UtilizeShadowRest = true };
        using Harness h = new(s) { HostilesPresent = true };
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        Assert.True(h.Health.ShadowRestHolding);

        h.Stealthed = false;          // our own cast ends the sneak
        h.State.Hp = 52;
        Assert.Equal(0, h.ShadowRestResumeCount);

        h.Stealthed = true;           // re-sneaked, still resting
        h.State.Hp = 60;
        Assert.Equal(0, h.ShadowRestResumeCount);

        h.State.Hp = 190;             // rest-max
        Assert.Equal(1, h.ShadowRestResumeCount);
    }

    // Mid-fight the client still reads stale-stealthed; ShadowRest must not hold combat
    // there just because HP dipped under the rest trigger (report
    // paradigm-20260930-192645).
    [Fact]
    public void ShadowRest_InAFight_DoesNotHold()
    {
        HealthSettings s = new() { UtilizeShadowRest = true };
        using Harness h = new(s) { HostilesPresent = true };
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.InCombat = true;
        h.State.Hp = 50;
        Assert.False(h.Health.ShadowRestHolding);
    }

    [Fact]
    public void ShadowRest_Inactive_NoResumeOnRecovery()
    {
        // Without ShadowRest engaged, normal recovery to target must never fire
        // the resume callback — it's exclusive to the stealthed rest cycle.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        Assert.False(h.Health.ShadowRestHolding);

        h.State.Hp = 190;
        Assert.Equal(0, h.ShadowRestResumeCount);
    }

    [Fact]
    public void Recovery_DoesNotSendStand_JustClearsInFlight()
    {
        // "stand" isn't a valid MajorMUD command. We clear the gate +
        // the in-flight latch; the walker resuming (because the gate
        // cleared) issues a move which the server auto-stands on.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        Assert.True(h.Health.RestInFlight);

        h.State.Hp = 195;             // past 95% target
        Assert.False(h.HealthGateHeld);
        Assert.DoesNotContain("stand", h.SentLines);
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
    public void PostRestCommand_SentOnRecovery()
    {
        HealthSettings s = new() { PostRestCommand = "look;exits" };
        using Harness h = new(s);
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        int restIdx = h.SentLines.IndexOf("rest");
        h.State.Hp = 195;

        // No "stand"; post-rest commands fire after the recovery flip.
        Assert.DoesNotContain("stand", h.SentLines);
        int look   = h.SentLines.LastIndexOf("look");
        int exits  = h.SentLines.LastIndexOf("exits");
        Assert.True(look  > restIdx);
        Assert.True(exits > restIdx);
    }

    // ----- NoteRoomChanged drops the in-flight latch -------------------

    [Fact]
    public void NoteRoomChanged_DropsRestInFlight()
    {
        // Server-side resting state auto-clears on move. Our latch
        // must follow — otherwise the next threshold breach would
        // see _restInFlight==true and skip the `rest` emit.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        Assert.True(h.Health.RestInFlight);

        h.Moved();
        Assert.False(h.Health.RestInFlight);
    }

    [Fact]
    public void NoteRoomChanged_NoLatch_NoOp()
    {
        // Calling NoteRoomChanged with nothing in flight is a no-op.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 180;    // above trigger — no rest started
        Assert.False(h.Health.RestInFlight);

        h.Moved();
        Assert.False(h.Health.RestInFlight);
    }

    [Fact]
    public void NoteRoomChanged_GateStillAsserted_NextBreachReFiresRest()
    {
        // We rested, walker tugged us into a new room mid-recovery
        // (HP still below target → gate still held). Latch dropped
        // by NoteRoomChanged. Next Evaluate (any state change) AND
        // out-of-combat AND gate still held → rest is re-sent.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        Assert.True(h.HealthGateHeld);
        int restCount1 = h.SentLines.Count(l => l == "rest");
        Assert.Equal(1, restCount1);

        h.Moved();
        Assert.False(h.Health.RestInFlight);
        Assert.True(h.HealthGateHeld);     // still need recovery

        // Drive any state change to re-evaluate (in real use the next
        // HP/MA tick from PromptParser would do this naturally).
        h.State.Hp = 60;
        Assert.Equal(2, h.SentLines.Count(l => l == "rest"));
    }

    // ----- MA gate (independent of HP) -------------------------------

    [Fact]
    public void MaBelowTrigger_AssertsManaRecovery()
    {
        using Harness h = new();
        // Live HP — a mortally-wounded character (Hp <= 0) bails before any
        // recovery, mana included, so isolate the MA gate with healthy HP.
        h.State.MaxHp = 200;
        h.State.Hp = 200;
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
    public void BothPoolsRecover_ClearsGates_NoStand()
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
        Assert.DoesNotContain("stand", h.SentLines);
        Assert.False(h.Health.RestInFlight);
    }

    // ----- run-if-below (flee) ---------------------------------------

    // ----- engage-to-clear a rest-blocker (Auto-Combat OFF) ----------
    // report paradigm-20260901-093301: a hostile blocking a needed rest with
    // Auto-Combat off deadlocks (can't rest, won't fight, HP above the flee
    // trigger so won't run). Defaults: RestIfBelowHp=60, RunIfBelowHp=20,
    // RestIfBelowMa=30. MaxHp/MaxMa=200.

    [Fact]
    public void ForceClearForRest_ArmsAndPokes_WhenHostileBlocksHpRest_CombatOff()
    {
        using Harness h = new();
        h.AutoCombatEnabled = false;   // combat off
        h.HostilesPresent = true;      // an enemy blocks rest
        h.SetPrompt(hp: 80, maxHp: 200);   // 40% — below rest(60), above run(20)
        h.Health.Evaluate();

        Assert.True(h.Health.ForceClearForRest);
        Assert.Equal(1, h.RestClearEngageCount);   // poked CombatManager to engage
    }

    [Fact]
    public void ForceClearForRest_ArmsForBlockedManaRest_CombatOff()
    {
        using Harness h = new();
        h.AutoCombatEnabled = false;
        h.HostilesPresent = true;
        // HP healthy (90%), but mana below its rest trigger (20% < 30%) — a mana
        // rest is due and the hostile blocks it, so still engage-to-clear.
        h.SetPrompt(hp: 180, maxHp: 200, ma: 40, maxMa: 200);
        h.Health.Evaluate();

        Assert.True(h.Health.ForceClearForRest);
    }

    [Fact]
    public void ForceClearForRest_DoesNotArm_WhenAutoCombatOn()
    {
        using Harness h = new();
        h.AutoCombatEnabled = true;    // engine already fights it
        h.HostilesPresent = true;
        h.SetPrompt(hp: 80, maxHp: 200);
        h.Health.Evaluate();

        Assert.False(h.Health.ForceClearForRest);
        Assert.Equal(0, h.RestClearEngageCount);
    }

    [Fact]
    public void ForceClearForRest_DoesNotArm_InFleeZone()
    {
        using Harness h = new();
        h.AutoCombatEnabled = false;
        h.HostilesPresent = true;
        h.State.InCombat = true;
        h.SetPrompt(hp: 30, maxHp: 200);   // 15% — below run(20): flee, don't engage
        h.Health.Evaluate();

        Assert.False(h.Health.ForceClearForRest);
        Assert.True(Fled(h.Health));   // the flee path took over instead
    }

    [Fact]
    public void ForceClearForRest_DoesNotArm_WithNoHostile()
    {
        using Harness h = new();
        h.AutoCombatEnabled = false;
        h.HostilesPresent = false;     // room clear — just rest
        h.SetPrompt(hp: 80, maxHp: 200);
        h.Health.Evaluate();

        Assert.False(h.Health.ForceClearForRest);
    }

    [Fact]
    public void ForceClearForRest_DoesNotArm_WhenHealthy()
    {
        using Harness h = new();
        h.AutoCombatEnabled = false;
        h.HostilesPresent = true;
        h.SetPrompt(hp: 180, maxHp: 200);   // 90% — no rest due; walk past
        h.Health.Evaluate();

        Assert.False(h.Health.ForceClearForRest);
    }

    [Fact]
    public void HpBelowRunTrigger_OutOfCombat_DoesNotLatchFlee()
    {
        // Out of combat, low HP just enters the normal rest cycle.
        // Run-threshold detection is combat-specific.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 30;            // 15% — below run threshold

        Assert.False(Fled(h.Health));
    }

    [Fact]
    public void FledThisCombat_ResetsWhenCombatEnds()
    {
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;
        Assert.True(Fled(h.Health));

        h.State.InCombat = false;
        Assert.False(Fled(h.Health));
    }

    [Fact]
    public void FledThisCombat_ReArmsOnNextCombat()
    {
        // Latch flips in fight #1, clears when combat ends, re-arms
        // for fight #2.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;
        Assert.True(Fled(h.Health));

        h.State.InCombat = false;
        h.State.Hp = 100;
        Assert.False(Fled(h.Health));

        h.State.InCombat = true;
        h.State.Hp = 25;
        Assert.True(Fled(h.Health));
    }

    [Fact]
    public void MaBelowRunTrigger_LatchesFlee_EvenWithHealthyHp()
    {
        // RunIfBelowMa is wired into the flee trigger: a caster whose pool
        // drops below the run threshold flees even at full HP — out of mana
        // means it can't cast, so standing there just gets it killed.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.MaxMa = 100;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 150;      // healthy HP
        h.State.Ma = 5;        // below the default 10% run trigger

        Assert.True(Fled(h.Health));
    }

    [Fact]
    public void NonCasterMaxMaZero_NoFledFromMa()
    {
        // Non-caster — MA is 0/0 forever; must not latch from MA path.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.MaxMa = 0;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 150;
        h.State.Ma = 0;

        Assert.False(Fled(h.Health));
    }

    [Fact]
    public void RunIfBelowMaZero_DisablesMaFlee_EvenAtZeroMana()
    {
        // A run-trigger of 0 means "never flee on this pool". Without the
        // disable guard the MA branch (MaxMa>0 && Ma<=0) would fire the moment
        // mana bottomed out at 0 — an errant flee that walks the character off
        // its loop path. 0 must switch the MA flee off entirely.
        using Harness h = new(new HealthSettings { RunIfBelowMa = 0 });
        h.State.MaxHp = 200;
        h.State.MaxMa = 100;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 150;      // healthy HP
        h.State.Ma = 0;        // fully out of mana

        Assert.False(Fled(h.Health));
    }

    [Fact]
    public void RunIfBelowHpZero_DisablesHpFlee()
    {
        // Same disable rule on the HP pool: 0 = never flee on HP.
        using Harness h = new(new HealthSettings { RunIfBelowHp = 0 });
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;       // 15% — below the default 20% run trigger, but flee is off

        Assert.False(Fled(h.Health));
    }

    [Fact]
    public void RunLatchAndRest_BothHappen_AfterCombatEnds()
    {
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;
        Assert.True(Fled(h.Health));
        Assert.DoesNotContain("rest", h.SentLines);

        h.State.InCombat = false;
        Assert.Contains("rest", h.SentLines);
    }

    // ----- follower flee-substitute: @heal instead of running -------

    [Fact]
    public void Follower_LowHpInCombat_RequestsHealInsteadOfFlee()
    {
        // A party follower at the run trigger must NOT flee (that strands them
        // from the party) — it broadcasts @heal via the wired callback instead.
        using Harness h = new();
        int healRequested = 0;
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            requestPartyHeal: () => healRequested++);
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;                 // 15% — below default 20% run trigger

        Assert.Equal(1, healRequested);
        Assert.True(Fled(h.Health));

        // Single-shot per combat — dropping further doesn't re-request.
        h.State.Hp = 25;
        Assert.Equal(1, healRequested);
    }

    [Fact]
    public void LeaderOrSolo_LowHpInCombat_DoesNotRequestHeal()
    {
        // Not a follower (leader / solo) — the heal callback never fires; the
        // flee path runs instead (a no-op here since no movement engine is
        // wired, i.e. "idle", so nothing reaches the wire).
        using Harness h = new();
        int healRequested = 0;
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => false,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            requestPartyHeal: () => healRequested++);
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;

        Assert.Equal(0, healRequested);
        Assert.True(Fled(h.Health));
    }

    // ----- party @wait / @ok recovery ceiling (report 222618) -------

    [Fact]
    public void Follower_PartyOk_HeldUntilFullRestMax_NotTriggerPlusOne()
    {
        // A follower's movement gate clears at trigger+1 so it can keep pace,
        // but the party @ok must not release there — doing so told the leader
        // to resume while we were still nearly depleted, flapping @wait/@ok.
        using Harness h = new();          // percentage mode: MA trigger 30 %, rest-max 95 %
        int waits = 0, oks = 0;
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => waits++,
            requestPartyOk: () => oks++);

        h.SetPrompt(hp: 100, maxHp: 100, ma: 100, maxMa: 100);  // start rested
        Assert.Equal(0, waits);
        Assert.Equal(0, oks);

        h.State.Ma = 20;                  // below the 30 % MA rest floor
        Assert.True(h.ManaGateHeld);
        Assert.Equal(1, waits);
        Assert.Equal(0, oks);

        h.State.Ma = 31;                  // trigger+1: movement gate releases...
        Assert.False(h.ManaGateHeld);
        Assert.Equal(0, oks);             // ...but the leader is NOT told to resume yet

        h.State.Ma = 95;                  // full rest-max ceiling reached
        Assert.Equal(0, oks);             // held a moment to be sure it stays there
        h.SettleOk();
        Assert.Equal(1, oks);             // @ok fires exactly once, now that we're rested
        Assert.Equal(1, waits);           // and @wait never re-fired mid-recovery
    }

    // Health is one wait reason for both pools, so HealthManager says which pool
    // tripped; HP is named when both are short.
    [Fact]
    public void Follower_PartyWaitNote_NamesThePoolThatTripped()
    {
        using Harness h = new();          // percentage mode: trigger 30 %, rest-max 95 %
        List<string?> notes = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => notes.Add(h.Health.PartyWaitNote),
            requestPartyOk: () => { });

        h.SetPrompt(hp: 100, maxHp: 100, ma: 100, maxMa: 100);   // rested
        Assert.Empty(notes);
        Assert.Null(h.Health.PartyWaitNote);

        h.State.Ma = 20;                                          // mana alone
        Assert.Equal(new string?[] { PartyRestSync.ManaNote }, notes);

        h.State.Ma = 95;
        h.SettleOk();

        h.State.Hp = 20;                                          // HP alone
        Assert.Equal(new string?[] { PartyRestSync.ManaNote, PartyRestSync.HpNote }, notes);

        h.State.Ma = 20;                                          // both: HP is named
        Assert.Equal(PartyRestSync.HpNote, h.Health.PartyWaitNote);
    }

    // Report paradigm-20260929-233636: HP bounced 199→203→199 in one burst with the
    // floor at 201 and rest-max at 203. The blip at rest-max must not send @ok between
    // two @waits; the @ok goes out only once the pools hold at rest-max.
    [Fact]
    public void Follower_PartyOk_BlipToRestMax_DoesNotFlap()
    {
        using Harness h = new();
        h.Settings.RestIfBelowHp = 79;
        h.Settings.RestMaxHp = 80;
        int waits = 0, oks = 0;
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => waits++,
            requestPartyOk: () => oks++);
        h.SetPrompt(hp: 254, maxHp: 254);

        h.State.Hp = 199;                 // under the floor → @wait
        h.State.Hp = 200;
        h.State.Hp = 203;                 // rest-max, for one prompt
        h.State.Hp = 199;                 // back under
        h.SettleOk();                     // the settle check finds it still low
        Assert.Equal(1, waits);
        Assert.Equal(0, oks);

        h.State.Hp = 240;                 // healed, and it holds
        Assert.Equal(0, oks);
        h.SettleOk();
        Assert.Equal(1, oks);
        Assert.Equal(1, waits);
    }

    // Report paradigm-20260928-223148: a Pre-rest Mana set put our believed max at 448,
    // but the game refused the meditate at 423 ("Meditation will not help at this
    // time.") — full by its count. That's rested: the mana gate clears and @ok goes out.
    [Fact]
    public void Follower_MeditateNotNeeded_BelowBelievedMax_SendsOk()
    {
        using Harness h = new();          // percentage mode: MA trigger 30 %, rest-max 95 %
        int oks = 0;
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => oks++);

        h.SetPrompt(hp: 100, maxHp: 100, ma: 100, maxMa: 100);
        h.State.Ma = 20;                  // drop below the floor → @wait
        h.State.MaxMa = 200;              // gear swap: believed max jumps
        h.Clock += TimeSpan.FromSeconds(30);   // past the gear-swap max settle
        h.State.Ma = 100;                 // the game's real full
        Assert.Equal(0, oks);

        h.Health.NoteMeditateNotNeeded();
        h.State.Hp = 99;                  // any prompt re-evaluates
        h.State.Hp = 100;
        h.SettleOk();

        Assert.False(h.ManaGateHeld);
        Assert.Equal(1, oks);
    }

    // A rested follower's @ok waits until the Pre-rest set is off again (user,
    // 2026-09-28): Default gear back on, pools re-checked, then @ok.
    [Fact]
    public void Follower_PartyOk_WaitsForTheDefaultGear()
    {
        using Harness h = new();
        int oks = 0;
        bool preRestOn = true;
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => oks++);
        h.Health.SetPartyOkHold(() => preRestOn);

        h.SetPrompt(hp: 100, maxHp: 100, ma: 100, maxMa: 100);
        h.State.Ma = 20;                  // @wait
        h.State.Ma = 95;                  // rested — but the Pre-rest set is still on
        h.SettleOk();
        Assert.Equal(0, oks);
        Assert.True(h.Health.IsPartyOkHeldForGear);

        preRestOn = false;                // Default back on
        h.Health.Evaluate();
        Assert.Equal(1, oks);
    }

    // ----- Multi-step flee + auto-resume ----------------------------

    // Fake engine for testing the flee dispatch — captures
    // every call instead of touching real walker plumbing.
    private sealed class FakeFleeEngine : Game.Map.IRecoverableEngine
    {
        public string Name => "FakeWalker";
        public List<Game.Map.Direction> SentBacktrackMoves { get; } = new();
        public string? PausedReason { get; private set; }
        public Game.Map.RoomKey? ResumedAtRoom { get; private set; }
        public Game.Map.Direction? NextPlanned { get; set; }
        public Game.Map.RoomKey? JourneyOrigin { get; set; }

        // The engine's forward-planned route — the Forward flee walks up to
        // RunDistance of these.
        public List<Game.Map.Direction> PlannedForward { get; } = new();

        public Dictionary<Game.Map.RoomKey, Game.Map.Direction> PlannedFrom { get; } = new();
        public Game.Map.Direction? PlannedDirectionFrom(Game.Map.RoomKey room) =>
            PlannedFrom.TryGetValue(room, out Game.Map.Direction d) ? d : null;

        public Game.Map.Direction? PeekNextPlannedDirection() => NextPlanned;
        public IReadOnlyList<Game.Map.Direction> PeekPlannedDirections(int count) =>
            PlannedForward.Take(count).ToList();
        public void SendBacktrackMove(Game.Map.Direction d) => SentBacktrackMoves.Add(d);
        public void PauseForRecovery(string reason) => PausedReason = reason;
        public void ResumeAfterRecovery(Game.Map.RoomKey k) => ResumedAtRoom = k;
        public void AbortFromRecoveryFailure(string _) { }

        // Whether the resume after a flee said to carry on from where it landed.
        public bool? CarriedOn { get; private set; }
        public void ResumeAfterFlee(Game.Map.RoomKey k, bool carryOnFromHere = false)
        {
            CarriedOn = carryOnFromHere;
            ResumedAtRoom = k;
        }
    }

    private sealed class FleeHarness : IDisposable
    {
        public PlayerState State { get; } = new();
        public LogService Log { get; } = new();
        public MovementCoordinator Coordinator { get; }
        public HealthManager Health { get; }
        public List<byte[]> Sent { get; } = new();
        public HealthSettings HealthSettings { get; set; } = new();
        public Models.Profile.CombatSettings Combat { get; set; } = new();
        public FakeFleeEngine? Engine { get; set; } = new();
        public Game.Map.Direction? LastSent { get; set; } = Game.Map.Direction.N;
        public bool HostilesPresent { get; set; }
        public bool HostileInRoom { get; set; } = true;

        // Auto-Heal or Auto-Rest on: the health engine runs.
        public bool Enabled { get; set; } = true;

        // When true, the flee's deferred reaction is queued (into Posted) instead
        // of running inline — lets a test simulate the room clearing between the
        // flee-trigger and the deferred commit (the killing-blow race). Default
        // false keeps the synchronous flee for the existing flee tests.
        public bool DeferFlee { get; set; }
        public Queue<Action> Posted { get; } = new();
        public void DrainPost() { while (Posted.Count > 0) Posted.Dequeue()(); }

        // Reverse-path selector for the Backward flee. Null (the default) exercises
        // the fallback (invert the last sent direction); a test can set it to a
        // fixed BFS route to drive the multi-direction reverse trail.
        public Func<Game.Map.RoomKey, Game.Map.RoomKey,
            IReadOnlyList<Game.Map.Direction>?>? ReversePath { get; set; }

        public FleeHarness()
        {
            Coordinator = new MovementCoordinator(Log);
            Health = new HealthManager(State, Coordinator,
                readSettings: () => HealthSettings,
                isEnabled: () => Enabled,
                readHangupCommand: () => string.Empty,
                getActiveMovementEngine: () => Engine,
                getLastSentDirection: () => LastSent,
                readCombatSettings: () => Combat,
                readGeneralSettings: null,
                hasEngageableHostiles: () => HostilesPresent,
                log: Log,
                hasHostileInRoom: () => HostileInRoom,
                findReversePath: (from, to) => ReversePath?.Invoke(from, to),
                post: a => { if (DeferFlee) Posted.Enqueue(a); else a(); },
                now: () => Clock ?? DateTimeOffset.UtcNow);
            Health.SetWireSender(b => Sent.Add(b));
            Health.SetScheduler((delay, action) => Scheduled.Add((delay, action)));
        }

        // A fixed clock for the tests that wait something out; null runs on real time.
        public DateTimeOffset? Clock { get; set; }
        public List<(TimeSpan Delay, Action Action)> Scheduled { get; } = new();

        public List<string> SentLines =>
            Sent.Select(b => System.Text.Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

        public void Dispose() => Health.Dispose();
    }

    [Fact]
    public void Flee_NoActiveEngine_NoBacktrack()
    {
        // Per user direction: "if you aren't running a movement
        // engine, the flee-if-below wouldn't fire".
        using FleeHarness h = new() { Engine = null };
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;

        Assert.True(Fled(h.Health));
        Assert.DoesNotContain("break", h.SentLines);
    }

    [Fact]
    public void Flee_KillingBlowEmptiesRoom_StandsDown_NoBacktrack()
    {
        // The round that dropped HP into flee territory also killed the last
        // monster; the death registers a tick AFTER the prompt that fires the
        // flee. The deferred flee must re-check and stand down — stay and rest,
        // not run from an empty room (report stock-20260730-160706).
        using FleeHarness h = new() { DeferFlee = true };
        h.Combat.RunDirection = Models.Profile.RunDirection.Backward;
        h.Combat.RunDistance = 1;
        h.LastSent = Game.Map.Direction.N;
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.HostileInRoom = true;

        h.State.Hp = 30;                              // 15% → below the run trigger; flee QUEUED
        Assert.Empty(h.Engine!.SentBacktrackMoves);   // not fired yet — deferred a tick

        // The round settles: the last monster died, room clears, InCombat flips
        // false and no hostile remains.
        h.HostileInRoom = false;
        h.State.InCombat = false;
        h.DrainPost();                                // run the queued flee reaction

        Assert.Empty(h.Engine!.SentBacktrackMoves);   // stood down — stayed to rest
    }

    [Fact]
    public void Flee_HostileSurvivesTheSettle_StillFlees()
    {
        // A genuine low-HP flee with a monster still alive after the settle: the
        // deferred reaction proceeds and flees.
        using FleeHarness h = new() { DeferFlee = true };
        h.Combat.RunDirection = Models.Profile.RunDirection.Backward;
        h.Combat.RunDistance = 1;
        h.LastSent = Game.Map.Direction.N;
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.HostileInRoom = true;

        h.State.Hp = 30;                              // flee queued
        h.DrainPost();                                // still in combat + hostile → flees

        Assert.NotEmpty(h.Engine!.SentBacktrackMoves);
    }

    [Fact]
    public void Flee_BackwardMode_NoMap_InvertsLastSentDirection()
    {
        // Fallback path: no reverse-path selector result and no JourneyOrigin
        // (unmapped area), so the Backward flee inverts the last sent direction
        // for a single conservative step back into the room we came from.
        using FleeHarness h = new();
        h.Combat.RunDirection = Models.Profile.RunDirection.Backward;
        h.Combat.BreakBeforeFleeing = true;
        h.Combat.RunDistance = 1;
        h.LastSent = Game.Map.Direction.N;

        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;

        Assert.Single(h.Engine!.SentBacktrackMoves);
        Assert.Equal(Game.Map.Direction.S, h.Engine.SentBacktrackMoves[0]);
        Assert.Contains("break", h.SentLines);
    }

    [Fact]
    public void Flee_ForwardMode_WalksEnginePlannedTrail()
    {
        // "Go backwards if running" off — the flee follows the engine's own
        // planned route toward its destination, capped at RunDistance, one move
        // per room arrival (NOT one direction repeated).
        using FleeHarness h = new();
        h.Combat.RunDirection = Models.Profile.RunDirection.Forward;
        h.Combat.BreakBeforeFleeing = false;
        h.Combat.RunDistance = 2;
        h.Engine!.PlannedForward.AddRange(new[]
        {
            Game.Map.Direction.E, Game.Map.Direction.N, Game.Map.Direction.E,
        });

        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;

        Assert.Single(h.Engine.SentBacktrackMoves);
        Assert.Equal(Game.Map.Direction.E, h.Engine.SentBacktrackMoves[0]);
        Assert.DoesNotContain("break", h.SentLines);

        // Next room arrival advances to the second planned move.
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 1));
        Assert.Equal(2, h.Engine.SentBacktrackMoves.Count);
        Assert.Equal(Game.Map.Direction.N, h.Engine.SentBacktrackMoves[1]);

        // Capped at RunDistance=2 — the third planned move is never sent.
        h.HostileInRoom = false;   // landed clear of the fight — nothing to re-flee from
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 2));
        Assert.Equal(2, h.Engine.SentBacktrackMoves.Count);
    }

    [Fact]
    public void Flee_MultiStep_WalksReverseBfsTrail_OnePerRoomChange()
    {
        // Backward flee (default) with a mapped reverse trail: BFS from the
        // current room back to the engine's JourneyOrigin yields S,W,U and
        // RunDistance caps at 3 — first step on trigger, the rest one per
        // NoteRoomChanged, in path order (NOT one sustained direction).
        using FleeHarness h = new();
        h.Combat.RunDirection = Models.Profile.RunDirection.Backward;
        h.Combat.BreakBeforeFleeing = false;
        h.Combat.RunDistance = 3;
        h.Engine!.JourneyOrigin = new Game.Map.RoomKey(1, 0);
        h.ReversePath = (_, _) => new[]
        {
            Game.Map.Direction.S, Game.Map.Direction.W, Game.Map.Direction.U,
        };

        // Establish the current room so the flee has a BFS source.
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 50));

        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;
        Assert.Single(h.Engine.SentBacktrackMoves);
        Assert.Equal(Game.Map.Direction.S, h.Engine.SentBacktrackMoves[0]);

        // Each room arrival advances one more step, following the trail.
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 100));
        Assert.Equal(2, h.Engine.SentBacktrackMoves.Count);
        Assert.Equal(Game.Map.Direction.W, h.Engine.SentBacktrackMoves[1]);

        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 101));
        Assert.Equal(3, h.Engine.SentBacktrackMoves.Count);
        Assert.Equal(Game.Map.Direction.U, h.Engine.SentBacktrackMoves[2]);

        h.HostileInRoom = false;   // landed clear of the fight — nothing to re-flee from
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 102));
        Assert.Equal(3, h.Engine.SentBacktrackMoves.Count);    // stopped
    }

    // Report paradigm-20260926-221012: a two-room loop, standing on its origin after
    // one flee — no trail "back" to the origin, so fall back to the room we came from.
    [Fact]
    public void Flee_Backward_AtLoopOrigin_RetreatsToThePreviousRoom()
    {
        using FleeHarness h = new();
        h.Combat.RunDirection = Models.Profile.RunDirection.Backward;
        h.Combat.BreakBeforeFleeing = false;
        h.Combat.RunDistance = 2;
        h.Engine!.JourneyOrigin = new Game.Map.RoomKey(1, 2150);
        h.Health.PreviousRoom = () => new Game.Map.RoomKey(1, 2152);
        h.ReversePath = (_, to) => to.Room == 2152 ? new[] { Game.Map.Direction.N } : null;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 2150));

        h.State.MaxHp = 33;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 5;

        Assert.Equal(new[] { Game.Map.Direction.N }, h.Engine.SentBacktrackMoves);
    }

    // Same report: the flee landed on a monster and fought it at run-trigger HP.
    // One run per fight: a further HP drop in the same room sends no second step.
    [Fact]
    public void Flee_FurtherHpDropInTheSameRoom_SendsNoSecondStep()
    {
        using FleeHarness h = new();
        h.Combat.RunDirection = Models.Profile.RunDirection.Backward;
        h.Combat.BreakBeforeFleeing = false;
        h.Combat.RunDistance = 1;
        h.LastSent = Game.Map.Direction.N;
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;
        Assert.Single(h.Engine!.SentBacktrackMoves);

        h.State.Hp = 25;

        Assert.Single(h.Engine.SentBacktrackMoves);
    }

    [Fact]
    public void Flee_LandsOnAHostile_StillUnderTrigger_FleesAgain()
    {
        using FleeHarness h = new();
        h.Combat.RunDirection = Models.Profile.RunDirection.Backward;
        h.Combat.BreakBeforeFleeing = false;
        h.Combat.RunDistance = 1;
        h.Engine!.JourneyOrigin = new Game.Map.RoomKey(1, 0);
        h.ReversePath = (_, _) => new[] { Game.Map.Direction.S };
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 50));
        h.HostileInRoom = true;
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;
        Assert.Single(h.Engine.SentBacktrackMoves);

        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 51));   // landed — a rat is here

        Assert.Equal(2, h.Engine.SentBacktrackMoves.Count);
    }

    // A walk 1/10 → E → 1/11 → … ; behind its start 1/10 runs W to 1/9 and on W to
    // 1/8, and N of 1/10 is 1/12. Reverse paths lead back to the start (1/10).
    private static FleeHarness WalkStartFlee()
    {
        FleeHarness h = new();
        h.Combat.RunDirection = Models.Profile.RunDirection.Backward;
        h.Combat.BreakBeforeFleeing = false;
        h.Combat.RunDistance = 1;
        h.Engine!.JourneyOrigin = new Game.Map.RoomKey(1, 10);
        h.Engine.PlannedFrom[new Game.Map.RoomKey(1, 10)] = Game.Map.Direction.E;
        Dictionary<int, Dictionary<Game.Map.Direction, int>> map = new()
        {
            [8] = new() { [Game.Map.Direction.E] = 9 },
            [9] = new() { [Game.Map.Direction.E] = 10, [Game.Map.Direction.W] = 8 },
            [10] = new() { [Game.Map.Direction.E] = 11, [Game.Map.Direction.W] = 9, [Game.Map.Direction.N] = 12 },
            [11] = new() { [Game.Map.Direction.W] = 10 },
            [12] = new() { [Game.Map.Direction.S] = 10 },
        };
        h.Health.RoomExits = k => map.TryGetValue(k.Room, out var e)
            ? e.ToDictionary(x => x.Key, x => new Game.Map.RoomKey(1, x.Value)) : null;
        h.ReversePath = (from, _) => from.Room switch
        {
            11 => new[] { Game.Map.Direction.W },
            9 => new[] { Game.Map.Direction.E },
            8 => new[] { Game.Map.Direction.E, Game.Map.Direction.E },
            _ => null,
        };
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        return h;
    }

    // At the walk's start there's no trail left: run the opposite way to the walk,
    // past the boss room to the north (report paradigm-20260930-192727).
    [Fact]
    public void Flee_AtTheWalkStart_RunsOppositeThePlan_AvoidingABossRoom()
    {
        using FleeHarness h = WalkStartFlee();
        h.Health.RoomRisk = k => (k.Room == 12, 0);
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 10));
        h.State.InCombat = true;
        h.State.Hp = 30;

        Assert.Equal(new[] { Game.Map.Direction.W }, h.Engine!.SentBacktrackMoves);
    }

    // A bigger lair the opposite way loses to a quiet room (user, 2026-09-30).
    [Fact]
    public void Flee_AtTheWalkStart_SteersAroundABigLair()
    {
        using FleeHarness h = WalkStartFlee();
        h.Health.RoomRisk = k => (false, k.Room == 9 ? 5 : 0);
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 10));
        h.State.InCombat = true;
        h.State.Hp = 30;

        Assert.Equal(new[] { Game.Map.Direction.N }, h.Engine!.SentBacktrackMoves);
    }

    // The report's bounce: a hostile followed each one-room leg, and the next leg went
    // straight back. Still under the trigger, a follower keeps us running away —
    // never back into the room the last leg fled.
    [Fact]
    public void KeepRunning_HostileFollows_NeverDoublesBack()
    {
        using FleeHarness h = WalkStartFlee();
        h.Health.RoomRisk = k => (k.Room == 12, 0);
        h.HostileInRoom = true;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 11));
        h.State.InCombat = true;
        h.State.Hp = 30;
        Assert.Equal(new[] { Game.Map.Direction.W }, h.Engine!.SentBacktrackMoves);   // back to the start

        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 10));
        Assert.True(h.Health.KeepRunning());                                           // it followed us
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 9));
        Assert.True(h.Health.KeepRunning());
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 8));

        // 1/11 → 1/10 → 1/9 → 1/8, each leg away; at the dead end the only way out is
        // back, so it stands and fights rather than run into what followed it.
        Assert.Equal(new[] { Game.Map.Direction.W, Game.Map.Direction.W, Game.Map.Direction.W },
            h.Engine.SentBacktrackMoves);
        Assert.False(h.Health.KeepRunning());
    }

    // Report paradigm-20261004-201232: the trail back was refused ("There is no exit
    // in that direction!"), and still under the run trigger the next leg planned the
    // same step — 23 times into the same wall until the character died. A way out the
    // game refuses is not sent again: the run takes another, the plan's own way
    // included, and with every way refused stands and fights.
    [Fact]
    public void Flee_AWayOutTheGameRefuses_IsNeverSentAgain()
    {
        using FleeHarness h = WalkStartFlee();
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 10));
        h.State.InCombat = true;
        h.State.Hp = 30;
        Assert.Equal(new[] { Game.Map.Direction.W }, h.Engine!.SentBacktrackMoves);

        h.Health.NoteMoveBlocked();
        Assert.Equal(new[] { Game.Map.Direction.W, Game.Map.Direction.N }, h.Engine.SentBacktrackMoves);

        h.Health.NoteMoveBlocked();
        Assert.Equal(new[] { Game.Map.Direction.W, Game.Map.Direction.N, Game.Map.Direction.E },
            h.Engine.SentBacktrackMoves);

        h.Health.NoteMoveBlocked();
        Assert.Equal(3, h.Engine.SentBacktrackMoves.Count);
        Assert.False(h.Health.KeepRunning());
    }

    // The same report's shape: the refused step was the trail back itself.
    [Fact]
    public void Flee_TheTrailBackRefused_RunsAnotherWayInsteadOfRepeatingIt()
    {
        using FleeHarness h = WalkStartFlee();
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 9));
        h.State.InCombat = true;
        h.State.Hp = 30;
        Assert.Equal(new[] { Game.Map.Direction.E }, h.Engine!.SentBacktrackMoves);   // the trail back

        h.Health.NoteMoveBlocked();

        Assert.Equal(new[] { Game.Map.Direction.E, Game.Map.Direction.W }, h.Engine.SentBacktrackMoves);
    }

    // A run that ended leaves nothing shut: the next run may try that way again (a
    // door closed then can be open now).
    [Fact]
    public void Flee_RefusedWaysOut_AreForgottenWhenTheRunEnds()
    {
        using FleeHarness h = WalkStartFlee();
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 9));
        h.State.InCombat = true;
        h.State.Hp = 30;
        h.Health.NoteMoveBlocked();                                   // E refused, W sent

        // Ending the run re-reads the room: still hurt with the monster here, a new
        // run starts, and its trail back is E again.
        h.Health.CancelFlee();

        Assert.Equal(new[] { Game.Map.Direction.E, Game.Map.Direction.W, Game.Map.Direction.E },
            h.Engine!.SentBacktrackMoves);
    }

    // Crystal Lake's teleporting rooms are closed to every route; a flee builds its own
    // retreat, so it is told which rooms they are. WalkStartFlee's map: 8 - 9 - 10 - 11,
    // with 12 north of 10.
    [Fact]
    public void Flee_Backward_StopsShortOfARoomClosedToRoutes()
    {
        using FleeHarness h = WalkStartFlee();
        h.Combat.RunDistance = 2;
        h.Health.IsClosedToRoutes = k => k.Room == 10;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 8));
        h.State.InCombat = true;
        h.State.Hp = 30;
        Assert.Equal(new[] { Game.Map.Direction.E }, h.Engine!.SentBacktrackMoves);   // 9, and not on into 10

        h.HostileInRoom = false;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 9));
        Assert.Single(h.Engine.SentBacktrackMoves);
    }

    [Fact]
    public void Flee_Backward_TrailBackClosed_TakesAnotherOpenExit()
    {
        using FleeHarness h = WalkStartFlee();
        h.Health.IsClosedToRoutes = k => k.Room == 10;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 9));
        h.State.InCombat = true;
        h.State.Hp = 30;

        Assert.Equal(new[] { Game.Map.Direction.W }, h.Engine!.SentBacktrackMoves);
    }

    [Fact]
    public void Flee_AWayOutLeadingIntoAClosedRoom_IsNeverTheAwayFromThePlanPick()
    {
        using FleeHarness h = WalkStartFlee();
        h.Health.IsClosedToRoutes = k => k.Room == 9;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 10));
        h.State.InCombat = true;
        h.State.Hp = 30;

        // West, opposite the plan, would be the pick but for 9; north is open.
        Assert.Equal(new[] { Game.Map.Direction.N }, h.Engine!.SentBacktrackMoves);
    }

    [Fact]
    public void Flee_Backward_EveryWayClosed_StartsNoRun()
    {
        using FleeHarness h = WalkStartFlee();
        h.Health.IsClosedToRoutes = k => k.Room == 10;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 11));
        h.State.InCombat = true;
        h.State.Hp = 30;

        Assert.Empty(h.Engine!.SentBacktrackMoves);
        Assert.False(h.Health.IsFleeInFlight);
    }

    [Fact]
    public void Flee_Forward_StopsShortOfARoomClosedToRoutes()
    {
        using FleeHarness h = new();
        h.Combat.RunDirection = Models.Profile.RunDirection.Forward;
        h.Combat.BreakBeforeFleeing = false;
        h.Combat.RunDistance = 3;
        h.Engine!.PlannedForward.AddRange(new[]
        {
            Game.Map.Direction.E, Game.Map.Direction.E, Game.Map.Direction.E,
        });
        h.Health.RoomExits = k => k.Room < 4
            ? new Dictionary<Game.Map.Direction, Game.Map.RoomKey>
                { [Game.Map.Direction.E] = new Game.Map.RoomKey(1, k.Room + 1) }
            : null;
        h.Health.IsClosedToRoutes = k => k.Room == 3;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 1));
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;
        Assert.Single(h.Engine.SentBacktrackMoves);

        h.HostileInRoom = false;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 2));
        Assert.Single(h.Engine.SentBacktrackMoves);   // 1 → 2, and not on into 3
    }

    [Fact]
    public void Flee_Forward_NextRoomClosed_StartsNoRun()
    {
        using FleeHarness h = new();
        h.Combat.RunDirection = Models.Profile.RunDirection.Forward;
        h.Combat.BreakBeforeFleeing = false;
        h.Combat.RunDistance = 2;
        h.Engine!.PlannedForward.Add(Game.Map.Direction.E);
        h.Health.RoomExits = k => new Dictionary<Game.Map.Direction, Game.Map.RoomKey>
            { [Game.Map.Direction.E] = new Game.Map.RoomKey(1, k.Room + 1) };
        h.Health.IsClosedToRoutes = k => k.Room == 2;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 1));
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;

        Assert.Empty(h.Engine.SentBacktrackMoves);
    }

    // Standing in a closed room already, the first step out is the character's to take.
    [Fact]
    public void Flee_FromInsideAClosedRoom_StillLeaves()
    {
        using FleeHarness h = WalkStartFlee();
        h.Health.IsClosedToRoutes = k => k.Room == 9;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 9));
        h.State.InCombat = true;
        h.State.Hp = 30;

        Assert.Equal(new[] { Game.Map.Direction.E }, h.Engine!.SentBacktrackMoves);
    }

    [Fact]
    public void Flee_Backward_WithoutTheProbe_RunsEveryRoomBack()
    {
        using FleeHarness h = WalkStartFlee();
        h.Combat.RunDistance = 2;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 8));
        h.State.InCombat = true;
        h.State.Hp = 30;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 9));

        Assert.Equal(new[] { Game.Map.Direction.E, Game.Map.Direction.E }, h.Engine!.SentBacktrackMoves);
    }

    private static FleeHarness HitAndRunFlee()
    {
        FleeHarness h = new();
        h.Combat.RunDirection = Models.Profile.RunDirection.Backward;
        h.Combat.BreakBeforeFleeing = false;
        h.Combat.RunDistance = 1;
        h.Combat.HitAndRunMaxRuns = 3;
        h.Engine!.JourneyOrigin = new Game.Map.RoomKey(1, 0);
        h.ReversePath = (_, _) => new[] { Game.Map.Direction.S };
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 50));
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 200;
        return h;
    }

    // A hit-and-run retreat (healthy HP) resumes the loop only once it lands, so it
    // walks back in sneaking for the next backstab.
    [Fact]
    public void HitAndRun_BackstabLandedWithSomethingStanding_RetreatsThenResumes()
    {
        using FleeHarness h = HitAndRunFlee();

        h.Health.BackstabLanded(runNow: true);
        Assert.Equal(new[] { Game.Map.Direction.S }, h.Engine!.SentBacktrackMoves);
        h.State.Hp = 199;                                          // an Evaluate mid-retreat
        Assert.Null(h.Engine.ResumedAtRoom);                       // …doesn't resume early

        h.HostileInRoom = false;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 49));     // landed
        Assert.Equal(new Game.Map.RoomKey(1, 49), h.Engine.ResumedAtRoom);
    }

    // Report paradigm-20260927-003231: the backstab killed its target (*Combat Off*),
    // a rat walked in and we ran — but "break" went out with nothing to break.
    [Fact]
    public void Flee_NotEngaged_SkipsTheBreak()
    {
        using FleeHarness h = HitAndRunFlee();
        h.Combat.BreakBeforeFleeing = true;
        h.Health.IsServerEngaged = () => false;

        Assert.True(h.Health.RunInsteadOfFight("walk-in"));

        Assert.DoesNotContain("break", h.SentLines);
        Assert.Single(h.Engine!.SentBacktrackMoves);
    }

    [Fact]
    public void Flee_Engaged_SendsTheBreak()
    {
        using FleeHarness h = HitAndRunFlee();
        h.Combat.BreakBeforeFleeing = true;
        h.Health.IsServerEngaged = () => true;

        Assert.True(h.Health.RunInsteadOfFight("survivor"));

        Assert.Contains("break", h.SentLines);
    }

    // Report paradigm-20260927-010239: with the flee's move still unconfirmed, a
    // re-display swung the tracker back to the room we were leaving; that was taken as
    // the flee landing, the run looked over, and the next engage swung at the monster.
    [Fact]
    public void Flee_SwingBackToTheRoomWeLeft_IsNotALanding()
    {
        using FleeHarness h = HitAndRunFlee();
        h.Combat.HitAndRunMaxRuns = 1;

        h.Health.BackstabLanded(runNow: true);                      // run from 1/50
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 50));      // a re-display swings back to it

        Assert.Null(h.Engine!.ResumedAtRoom);                       // not landed
        Assert.True(h.Health.RunInsteadOfFight("the lunge"));        // still running — no swing
    }

    [Fact]
    public void Flee_ArrivalWhileAMoveIsPending_IsNotALanding()
    {
        using FleeHarness h = HitAndRunFlee();
        bool pending = false;
        h.Health.IsMovePending = () => pending;

        h.Health.BackstabLanded(runNow: true);
        pending = true;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 49));      // not confirmed yet
        Assert.Null(h.Engine!.ResumedAtRoom);

        pending = false;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 48));      // confirmed arrival
        Assert.Equal(new Game.Map.RoomKey(1, 48), h.Engine.ResumedAtRoom);
    }

    // Report paradigm-20260927-011624: a failed-backstab flee (healthy HP) "completed"
    // before its move landed. Every flee now resumes only after landing.
    [Fact]
    public void BackstabFailFlee_ResumesOnlyAfterLanding()
    {
        using FleeHarness h = HitAndRunFlee();

        h.Health.RunFromBackstabFailure();
        h.State.Hp = 199;                                           // an Evaluate in flight
        Assert.Null(h.Engine!.ResumedAtRoom);
        Assert.True(h.Health.IsFleeInFlight);

        h.HostileInRoom = false;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 49));
        Assert.Equal(new Game.Map.RoomKey(1, 49), h.Engine.ResumedAtRoom);
    }

    // The PvP response's flee runs its own distance, not Combat's RunDistance, and
    // the engine is held where it lands until the stay-away time is up.
    [Fact]
    public void FleeFromPlayer_RunsItsOwnDistance_AndStaysAwayBeforeResuming()
    {
        using FleeHarness h = HitAndRunFlee();
        h.Clock = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        h.ReversePath = (_, _) => new[]
        {
            Game.Map.Direction.S, Game.Map.Direction.W, Game.Map.Direction.U,
        };

        Assert.True(h.Health.FleeFromPlayer("Bob attacked us", rooms: 2, stayAway: TimeSpan.FromSeconds(60)));
        Assert.Equal(new[] { Game.Map.Direction.S }, h.Engine!.SentBacktrackMoves);

        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 49));
        Assert.Equal(2, h.Engine.SentBacktrackMoves.Count);

        // Landed after two rooms (RunDistance is 1, the trail three long): held.
        h.HostileInRoom = false;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 48));
        Assert.Equal(2, h.Engine.SentBacktrackMoves.Count);
        Assert.Null(h.Engine.ResumedAtRoom);
        (TimeSpan delay, Action wake) = Assert.Single(h.Scheduled);
        Assert.True(delay >= TimeSpan.FromSeconds(60));

        h.Clock += TimeSpan.FromSeconds(59);
        h.Health.Evaluate();
        Assert.Null(h.Engine.ResumedAtRoom);

        h.Clock += TimeSpan.FromSeconds(2);
        wake();
        Assert.Equal(new Game.Map.RoomKey(1, 48), h.Engine.ResumedAtRoom);
    }

    // The next flee of our own goes back to Combat's distance and resumes at once.
    [Fact]
    public void FleeFromPlayer_ItsDistanceAndWaitDontOutliveIt()
    {
        using FleeHarness h = HitAndRunFlee();
        h.Clock = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        h.ReversePath = (_, _) => new[] { Game.Map.Direction.S, Game.Map.Direction.W };

        h.Health.FleeFromPlayer("Bob attacked us", rooms: 2, stayAway: TimeSpan.Zero);
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 49));
        h.HostileInRoom = false;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 48));
        Assert.Equal(new Game.Map.RoomKey(1, 48), h.Engine!.ResumedAtRoom);

        h.Engine.SentBacktrackMoves.Clear();
        h.HostileInRoom = true;
        h.Health.RunFromBackstabFailure();
        h.HostileInRoom = false;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 47));
        Assert.Single(h.Engine.SentBacktrackMoves);          // RunDistance 1 again
    }

    [Fact]
    public void FleeFromPlayer_NoEngineRunning_SaysSo()
    {
        using FleeHarness h = new() { Engine = null };

        Assert.False(h.Health.FleeFromPlayer("Bob attacked us", rooms: 5, stayAway: TimeSpan.FromSeconds(30)));
    }

    // ----- a party follower never flees (user, 2026-10-10) --------------
    //
    // Whatever asks for the run. A walk or loop is up behind the follower hold, so
    // each of these would have had somewhere to run.

    private static FleeHarness FollowerWithAnEngine(Action? requestHeal = null)
    {
        FleeHarness h = HitAndRunFlee();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            requestPartyHeal: requestHeal);
        return h;
    }

    [Fact]
    public void Follower_LowHp_AsksForTheHeal_AndDoesNotRun()
    {
        int healRequested = 0;
        using FleeHarness h = FollowerWithAnEngine(() => healRequested++);
        h.State.InCombat = true;

        h.State.Hp = 30;

        Assert.Equal(1, healRequested);
        Assert.Empty(h.Engine!.SentBacktrackMoves);
    }

    // The same low mana that runs a leader or a solo character.
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void LowMana_RunsEveryoneButAFollower(bool follower, int moves)
    {
        using FleeHarness h = follower ? FollowerWithAnEngine() : HitAndRunFlee();
        h.HealthSettings.RunIfBelowMa = 20;
        h.State.MaxMa = 100;
        h.State.Ma = 100;
        h.State.InCombat = true;

        h.State.Ma = 5;

        Assert.Equal(moves, h.Engine!.SentBacktrackMoves.Count);
    }

    [Fact]
    public void Follower_HitAndRun_StandsAndFights()
    {
        using FleeHarness h = FollowerWithAnEngine();

        Assert.False(h.Health.RunInsteadOfFight("walk-in"));
        h.Health.BackstabLanded(runNow: true);

        Assert.Empty(h.Engine!.SentBacktrackMoves);
        Assert.Equal(0, h.Health.HitAndRunRuns);
    }

    [Fact]
    public void Follower_FailedBackstab_DoesNotRun()
    {
        using FleeHarness h = FollowerWithAnEngine();

        h.Health.RunFromBackstabFailure();

        Assert.Empty(h.Engine!.SentBacktrackMoves);
        Assert.False(h.Health.IsFleeInFlight);
    }

    [Fact]
    public void Follower_AttackedByAPlayer_DoesNotRun()
    {
        using FleeHarness h = FollowerWithAnEngine();

        Assert.False(h.Health.FleeFromPlayer("Bob attacked us", rooms: 3, stayAway: TimeSpan.FromSeconds(30)));

        Assert.Empty(h.Engine!.SentBacktrackMoves);
    }

    [Fact]
    public void Follower_FleeMonsterInTheRoom_DoesNotRun()
    {
        using FleeHarness h = FollowerWithAnEngine();

        Assert.Equal(FleeOutcome.Follower, h.Health.FleeFromMonster("ogre (#7) is here, relationship Flee", () => true));

        Assert.Empty(h.Engine!.SentBacktrackMoves);
    }

    // ----- a Flee-relationship monster in the room ----------------------
    //
    // FleeFromMonster is the run-if-below retreat started by a sight, not by HP: the
    // same route, distance, break and resume, at full health and outside a fight.

    private const string FleeSight = "ooze (#63) is here, relationship Flee";

    [Fact]
    public void FleeFromMonster_AtFullHealth_OutsideAFight_RunsAndThenResumes()
    {
        using FleeHarness h = HitAndRunFlee();
        h.HostileInRoom = false;                                     // a Flee monster is not counted a hostile
        int started = 0;
        h.Health.FleeStarted += () => started++;

        Assert.Equal(FleeOutcome.Started, h.Health.FleeFromMonster(FleeSight, () => true));

        Assert.Equal(new[] { Game.Map.Direction.S }, h.Engine!.SentBacktrackMoves);
        Assert.Contains("relationship Flee", h.Engine.PausedReason);
        Assert.Equal(1, started);
        Assert.True(h.Health.IsFleeing);
        Assert.False(h.Health.IsGateFleeing);                        // not a low-HP run
        Assert.Null(h.Engine.ResumedAtRoom);

        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 49));      // landed
        Assert.Equal(new Game.Map.RoomKey(1, 49), h.Engine.ResumedAtRoom);
        Assert.False(h.Health.IsFleeing);
    }

    [Fact]
    public void FleeFromMonster_RunsTheCombatTabsDistance_AndBreaksAFightUnderWay()
    {
        using FleeHarness h = HitAndRunFlee();
        h.Combat.RunDistance = 2;
        h.Combat.BreakBeforeFleeing = true;
        h.Health.IsServerEngaged = () => true;
        h.ReversePath = (_, _) => new[]
        {
            Game.Map.Direction.S, Game.Map.Direction.W, Game.Map.Direction.U,
        };

        Assert.Equal(FleeOutcome.Started, h.Health.FleeFromMonster(FleeSight, () => true));
        Assert.Equal(new[] { "break" }, h.SentLines);

        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 49));
        Assert.Equal(new[] { Game.Map.Direction.S, Game.Map.Direction.W }, h.Engine!.SentBacktrackMoves);
        Assert.Null(h.Engine.ResumedAtRoom);

        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 48));
        Assert.Equal(2, h.Engine.SentBacktrackMoves.Count);
        Assert.Equal(new Game.Map.RoomKey(1, 48), h.Engine.ResumedAtRoom);
    }

    // With Go backwards if running unticked the run goes on along the engine's own
    // route, out of the monster's room on the far side, and the engine is picked
    // up again where it lands.
    [Fact]
    public void FleeFromMonster_GoBackwardsUnticked_RunsOnAlongTheRoute()
    {
        using FleeHarness h = HitAndRunFlee();
        h.Combat.RunDirection = Models.Profile.RunDirection.Forward;
        h.Combat.RunDistance = 2;
        h.Engine!.PlannedForward.AddRange(new[]
        {
            Game.Map.Direction.E, Game.Map.Direction.N, Game.Map.Direction.W,
        });

        Assert.Equal(FleeOutcome.Started, h.Health.FleeFromMonster(FleeSight, () => true));
        Assert.Equal(new[] { Game.Map.Direction.E }, h.Engine.SentBacktrackMoves);

        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 51));
        Assert.Equal(new[] { Game.Map.Direction.E, Game.Map.Direction.N }, h.Engine.SentBacktrackMoves);

        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 52));
        Assert.Equal(2, h.Engine.SentBacktrackMoves.Count);
        Assert.Equal(new Game.Map.RoomKey(1, 52), h.Engine.ResumedAtRoom);
    }

    // A low-HP run has landed and waits for HP with the engine still paused. A Flee
    // monster seen there is run from: another leg, on the same engine.
    [Fact]
    public void FleeFromMonster_AfterALowHpRunHasLanded_RunsAgainFromThere()
    {
        using FleeHarness h = HitAndRunFlee();
        h.State.InCombat = true;
        h.State.Hp = 30;                                             // under the 20% run trigger
        Assert.Single(h.Engine!.SentBacktrackMoves);
        h.HostileInRoom = false;                                     // nothing followed
        h.State.InCombat = false;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 49));       // landed, still low
        Assert.Null(h.Engine.ResumedAtRoom);
        Assert.False(h.Health.IsFleeInFlight);

        Assert.Equal(FleeOutcome.Started, h.Health.FleeFromMonster(FleeSight, () => true));

        Assert.Equal(2, h.Engine.SentBacktrackMoves.Count);
        Assert.True(h.Health.IsFleeInFlight);
    }

    // The game refuses the run's first move: the retreat stops where it stands and
    // the run is no longer in flight, which is what the monster watch asks before
    // it lets self-defence fight the monster back.
    [Fact]
    public void FleeFromMonster_FirstMoveRefused_TheRunIsOver()
    {
        using FleeHarness h = HitAndRunFlee();
        Assert.Equal(FleeOutcome.Started, h.Health.FleeFromMonster(FleeSight, () => true));
        Assert.True(h.Health.IsFleeInFlight);

        h.Health.NoteMoveBlocked();

        Assert.False(h.Health.IsFleeInFlight);
        Assert.Single(h.Engine!.SentBacktrackMoves);                 // and no second run is chained
    }

    // A player's flee has landed and is staying away, still holding its own run
    // length. A Flee monster seen there is run from by the Combat tab's distance,
    // and the stay-away stands.
    [Fact]
    public void FleeFromMonster_DuringAPlayersStayAway_RunsTheCombatTabsDistance()
    {
        using FleeHarness h = HitAndRunFlee();
        h.Clock = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        h.ReversePath = (_, _) => new[]
        {
            Game.Map.Direction.S, Game.Map.Direction.W, Game.Map.Direction.U,
        };
        h.Health.FleeFromPlayer("Bob attacked us", rooms: 2, stayAway: TimeSpan.FromSeconds(60));
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 49));
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 48));       // landed; staying away
        Assert.Equal(2, h.Engine!.SentBacktrackMoves.Count);

        Assert.Equal(FleeOutcome.Started, h.Health.FleeFromMonster(FleeSight, () => true));
        Assert.Equal(3, h.Engine.SentBacktrackMoves.Count);

        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 47));       // RunDistance 1: landed
        Assert.Equal(3, h.Engine.SentBacktrackMoves.Count);
        Assert.Null(h.Engine.ResumedAtRoom);                         // the stay-away is not over
    }

    // The game refused the run's way out. A fresh sighting in the same room must
    // not start the same refused run again (a `break` and a move into the wall each
    // time): the refused way is remembered until the room is left.
    [Fact]
    public void FleeFromMonster_AWayOutRefused_IsNotTriedAgainFromThatRoom_UntilItIsLeft()
    {
        using FleeHarness h = HitAndRunFlee();
        h.Combat.BreakBeforeFleeing = true;
        h.Health.IsServerEngaged = () => true;
        Assert.Equal(FleeOutcome.Started, h.Health.FleeFromMonster(FleeSight, () => true));
        h.Health.NoteMoveBlocked();                                  // refused; the engine is handed back
        Assert.NotNull(h.Engine!.ResumedAtRoom);
        int breaks = h.SentLines.Count(l => l == "break");

        Assert.Equal(FleeOutcome.NoRoute, h.Health.FleeFromMonster(FleeSight, () => true));

        Assert.Single(h.Engine.SentBacktrackMoves);
        Assert.Equal(breaks, h.SentLines.Count(l => l == "break"));

        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 51));       // walked on, and came back
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 50));
        Assert.Equal(FleeOutcome.Started, h.Health.FleeFromMonster(FleeSight, () => true));
    }

    // The run waited for a move in flight to land, and the user paused the walk or
    // loop in the meantime: a paused one is idle for this flee, at the landing as
    // at the sighting.
    [Fact]
    public void FleeFromMonster_HeldForAMove_IsDroppedIfTheUserPausedMeanwhile()
    {
        using FleeHarness h = HitAndRunFlee();
        bool pending = true, userPaused = false;
        h.Health.IsMovePending = () => pending;
        h.Health.IsNavigationPausedByUser = () => userPaused;
        Assert.Equal(FleeOutcome.Started, h.Health.FleeFromMonster(FleeSight, () => true));

        userPaused = true;
        pending = false;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 51));

        Assert.Empty(h.Engine!.SentBacktrackMoves);
        Assert.False(h.Health.IsFleeInFlight);
        Assert.Null(h.Engine.PausedReason);
    }

    // ----- a flee cut short ------------------------------------------------
    //
    // "if we died, the flee needs to end" (user, 2026-10-10). A death, a profile
    // load and a drop of the line all end it at once: left standing it read as in
    // flight until the next move, and that move sent its next queued step.

    private static FleeHarness RunWithTwoRoomsQueued()
    {
        FleeHarness h = HitAndRunFlee();
        h.Combat.RunDistance = 3;
        h.ReversePath = (_, _) => new[]
        {
            Game.Map.Direction.S, Game.Map.Direction.W, Game.Map.Direction.U,
        };
        Assert.Equal(FleeOutcome.Started, h.Health.FleeFromMonster(FleeSight, () => true));
        Assert.Single(h.Engine!.SentBacktrackMoves);                 // S sent; W and U queued
        Assert.True(h.Health.IsFleeInFlight);
        return h;
    }

    [Theory]
    [InlineData("died")]
    [InlineData("another profile was loaded")]
    [InlineData("the profile was closed")]
    public void AFleeCutShort_EndsAtOnce_AndTheNextRoomChangeSendsNothing(string why)
    {
        using FleeHarness h = RunWithTwoRoomsQueued();
        List<LogEntry> logged = new();
        h.Log.EntryAdded += logged.Add;

        Assert.True(h.Health.EndFlee(why));

        Assert.False(h.Health.IsFleeInFlight);                       // combat engages again
        Assert.False(h.Health.IsFleeing);
        Assert.Contains(logged, e => e.Severity == LogSeverity.Info && e.Message.Contains($"flee ended — {why}"));

        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 7));        // the graveyard, or the next character's room
        h.Health.Evaluate();

        Assert.Single(h.Engine!.SentBacktrackMoves);                 // no queued step went out
        Assert.Null(h.Engine.ResumedAtRoom);                         // and nothing was resumed
        // A new sighting is a new run, not one "already under way".
        Assert.Equal(FleeOutcome.Started, h.Health.FleeFromMonster(FleeSight, () => true));
    }

    [Fact]
    public void EndFlee_WithNoFleeOn_SaysSo()
    {
        using FleeHarness h = HitAndRunFlee();

        Assert.False(h.Health.EndFlee("died"));
    }

    // A drop of the line ends the run too, but a walk lives through it: the one the
    // flee had paused is handed back, at the first game prompt of the next stay and
    // not before (the prompt data is as it was, so Evaluate alone would resume it
    // offline).
    [Fact]
    public void AFleeCutByADisconnect_HandsTheWalkBackAtTheFirstGamePrompt()
    {
        using FleeHarness h = RunWithTwoRoomsQueued();

        Assert.True(h.Health.EndFlee("disconnected", handBackLiveEngine: true));

        Assert.False(h.Health.IsFleeInFlight);
        h.Health.Evaluate();                                         // offline: a timer, a party event
        h.State.Hp = 199;
        Assert.Null(h.Engine!.ResumedAtRoom);
        Assert.Single(h.Engine.SentBacktrackMoves);

        h.Health.NoteInGamePrompt();

        Assert.Equal(new Game.Map.RoomKey(1, 50), h.Engine.ResumedAtRoom);
        Assert.Single(h.Engine.SentBacktrackMoves);                  // the queued steps are gone
        Assert.False(h.Health.IsFleeing);
    }

    // The engine the flee paused is no longer the one running (the loop is stopped
    // by the disconnect and restarts by its own reconnect resume): nothing is kept.
    [Fact]
    public void AFleeCutByADisconnect_WithItsEngineStopped_ResumesNothing()
    {
        using FleeHarness h = RunWithTwoRoomsQueued();
        FakeFleeEngine paused = h.Engine!;
        h.Engine = null;

        Assert.True(h.Health.EndFlee("disconnected", handBackLiveEngine: true));
        h.Health.NoteInGamePrompt();

        Assert.False(h.Health.IsFleeing);
        Assert.Null(paused.ResumedAtRoom);
    }

    // A death after the drop ends the hand-back as well.
    [Fact]
    public void ADeathAfterTheDisconnect_DropsTheHandBack()
    {
        using FleeHarness h = RunWithTwoRoomsQueued();
        h.Health.EndFlee("disconnected", handBackLiveEngine: true);

        Assert.True(h.Health.EndFlee("died"));
        h.Health.NoteInGamePrompt();

        Assert.Null(h.Engine!.ResumedAtRoom);
    }

    // The toolbar Stop ends the walk or loop without telling this class. The first
    // move of the retreat then lands with a step still queued: it is not sent
    // through a stopped engine, and the flee is over rather than left in flight (and
    // combat declining every engage) until enough rooms are crossed to drain it.
    [Theory]
    [InlineData(false)]   // nothing is running
    [InlineData(true)]    // another engine is
    public void AFlee_WhoseEngineWasStopped_EndsAtTheNextRoomChange_AndSendsNothing(bool anotherEngineRuns)
    {
        using FleeHarness h = RunWithTwoRoomsQueued();
        FakeFleeEngine stopped = h.Engine!;
        h.Engine = anotherEngineRuns ? new FakeFleeEngine() : null;

        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 51));       // the first move landed

        Assert.Single(stopped.SentBacktrackMoves);                   // the queued step stays unsent
        Assert.False(h.Health.IsFleeInFlight);
        Assert.False(h.Health.IsFleeing);
        Assert.Null(stopped.ResumedAtRoom);
        if (anotherEngineRuns) Assert.Empty(h.Engine!.SentBacktrackMoves);
    }

    [Fact]
    public void AFlee_WhoseEngineIsStillRunning_SendsItsNextQueuedStep()
    {
        using FleeHarness h = RunWithTwoRoomsQueued();

        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 51));

        Assert.Equal(2, h.Engine!.SentBacktrackMoves.Count);
        Assert.True(h.Health.IsFleeInFlight);
    }

    // A profile load or close takes the character's walk or loop with it: the engine
    // the flee had paused is stopped, not left paused for good.
    [Fact]
    public void AFleeCutByAProfileChange_StopsTheEngineItHadPaused()
    {
        using FleeHarness h = RunWithTwoRoomsQueued();
        List<(Game.Map.IRecoverableEngine Engine, string Why)> stopped = new();
        h.Health.StopMovementEngine = (engine, why) => stopped.Add((engine, why));

        Assert.True(h.Health.EndFlee("another profile was loaded", stopItsEngine: true));

        Assert.Equal((h.Engine!, "another profile was loaded"), Assert.Single(stopped));
        Assert.False(h.Health.IsFleeing);
    }

    [Fact]
    public void AFleeCutByADeath_LeavesStoppingTheEngineToTheDeathHalt()
    {
        using FleeHarness h = RunWithTwoRoomsQueued();
        int stopped = 0;
        h.Health.StopMovementEngine = (_, _) => stopped++;

        Assert.True(h.Health.EndFlee("died"));

        Assert.Equal(0, stopped);
    }

    // Which runs tell the engine to carry on from where they landed (user,
    // 2026-10-10): the ones that ran forward to get away from something. A
    // hit-and-run or a failed backstab's run comes back for the monster, a player's
    // is walked back after its stay-away, and nothing changes when running back.
    [Theory]
    [InlineData("monster", true, true)]
    [InlineData("low-hp", true, true)]
    [InlineData("hit-and-run", true, false)]
    [InlineData("backstab", true, false)]
    [InlineData("player", true, false)]
    [InlineData("monster", false, false)]
    [InlineData("low-hp", false, false)]
    public void AfterARun_TheEngineIsToldWhetherToCarryOn(string run, bool forward, bool carriesOn)
    {
        using FleeHarness h = HitAndRunFlee();
        h.Combat.RunDirection = forward ? Models.Profile.RunDirection.Forward : Models.Profile.RunDirection.Backward;
        h.Engine!.PlannedForward.Add(Game.Map.Direction.E);

        switch (run)
        {
            case "monster": h.Health.FleeFromMonster(FleeSight, () => true); break;
            case "hit-and-run": h.Health.BackstabLanded(runNow: true); break;
            case "backstab": h.Health.RunFromBackstabFailure(); break;
            case "player": h.Health.FleeFromPlayer("Bob attacked us", rooms: 1, stayAway: TimeSpan.Zero); break;
            default:
                h.State.InCombat = true;
                h.State.Hp = 30;                                     // under the 20% run trigger
                break;
        }
        Assert.Single(h.Engine.SentBacktrackMoves);

        h.HostileInRoom = false;
        h.State.InCombat = false;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 51));       // landed
        h.State.Hp = 200;                                            // and recovered

        Assert.Equal(new Game.Map.RoomKey(1, 51), h.Engine.ResumedAtRoom);
        Assert.Equal(carriesOn, h.Engine.CarriedOn);
    }

    // The run-if-below flee's own gate: with Auto-Heal and Auto-Rest both off the
    // engine that runs every flee is off. There is no carve-out for it like the
    // hang-up's all-off option.
    [Fact]
    public void FleeFromMonster_AutoHealAndAutoRestOff_SendsNothing()
    {
        using FleeHarness h = HitAndRunFlee();
        h.Enabled = false;

        Assert.Equal(FleeOutcome.EngineOff, h.Health.FleeFromMonster(FleeSight, () => true));

        Assert.Empty(h.Engine!.SentBacktrackMoves);
        Assert.Null(h.Engine.PausedReason);
        Assert.Empty(h.Sent);
    }

    // The low-HP rule: a follower does not run off alone. There is no HP to ask
    // for, so no @heal goes out either.
    [Fact]
    public void FleeFromMonster_APartyFollower_StaysWithTheParty()
    {
        using FleeHarness h = HitAndRunFlee();
        int heals = 0, waits = 0;
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => waits++,
            requestPartyOk: () => { },
            requestPartyHeal: () => heals++);

        Assert.Equal(FleeOutcome.Follower, h.Health.FleeFromMonster(FleeSight, () => true));

        Assert.Empty(h.Engine!.SentBacktrackMoves);
        Assert.Null(h.Engine.PausedReason);
        Assert.Equal(0, heals);
        Assert.Equal(0, waits);
    }

    [Fact]
    public void FleeFromMonster_NoWalkOrLoopRunning_SaysSo_AndSendsNoHangUp()
    {
        using FleeHarness h = new() { Engine = null };
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 200;

        Assert.Equal(FleeOutcome.NoEngine, h.Health.FleeFromMonster(FleeSight, () => true));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void FleeFromMonster_NoWayOut_SaysSo_AndLeavesTheEngineRunning()
    {
        using FleeHarness h = new() { LastSent = null };
        h.Combat.RunDirection = Models.Profile.RunDirection.Backward;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 50));
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 200;

        Assert.Equal(FleeOutcome.NoRoute, h.Health.FleeFromMonster(FleeSight, () => true));

        Assert.Empty(h.Engine!.SentBacktrackMoves);
        Assert.Null(h.Engine.PausedReason);
        Assert.False(h.Health.IsFleeing);
        Assert.Empty(h.Sent);
    }

    // A move sent at 0 HP or below is refused, and a flee that never lands would
    // hold the engine paused for good.
    [Fact]
    public void FleeFromMonster_WhileDown_StartsNothing()
    {
        using FleeHarness h = HitAndRunFlee();
        h.State.Hp = 0;

        Assert.Equal(FleeOutcome.Down, h.Health.FleeFromMonster(FleeSight, () => true));

        Assert.Empty(h.Engine!.SentBacktrackMoves);
        Assert.False(h.Health.IsFleeInFlight);
    }

    // A wimpy jump has just gone out for the same roster: a move behind it would be
    // walked from the wimpy location.
    [Fact]
    public void FleeFromMonster_RightAfterAnEscape_SendsNoMove()
    {
        using FleeHarness h = HitAndRunFlee();
        h.Clock = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        h.HealthSettings.SysGotoWimpyInsteadOfHanging = true;
        h.HealthSettings.SysGotoWimpyLocation = "town";
        h.Health.SetWimpyGoto(_ => true);
        Assert.Equal(EscapeOutcome.Jumped, h.Health.HangUpForMonster("ogre (#7) is here, relationship Hangup"));

        Assert.Equal(FleeOutcome.Escaping, h.Health.FleeFromMonster(FleeSight, () => true));
        Assert.Empty(h.Engine!.SentBacktrackMoves);

        h.Clock += TimeSpan.FromSeconds(3);
        Assert.Equal(FleeOutcome.Started, h.Health.FleeFromMonster(FleeSight, () => true));
    }

    // A follower never flees, but the wimpy jump is not a flee: it stands in for
    // the hang-up, and is allowed a follower like one (user, 2026-10-10: "this is
    // allowed, like a hangup").
    [Fact]
    public void Follower_WimpyJumpInPlaceOfAHangUp_IsStillMade()
    {
        using FleeHarness h = HitAndRunFlee();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => { });
        h.HealthSettings.SysGotoWimpyInsteadOfHanging = true;
        h.HealthSettings.SysGotoWimpyLocation = "town";
        string? jumpedTo = null;
        h.Health.SetWimpyGoto(where => { jumpedTo = where; return true; });

        Assert.Equal(EscapeOutcome.Jumped, h.Health.HangUpForMonster("ogre (#7) is here, relationship Hangup"));

        Assert.Equal("town", jumpedTo);
    }

    [Fact]
    public void FleeFromMonster_WhileAnotherFleeIsInFlight_SendsNoSecondMove()
    {
        using FleeHarness h = HitAndRunFlee();
        h.Health.RunFromBackstabFailure();

        Assert.Equal(FleeOutcome.AlreadyRunning, h.Health.FleeFromMonster(FleeSight, () => true));

        Assert.Single(h.Engine!.SentBacktrackMoves);
    }

    // The room display that shows the monster is read before the move into its room
    // is confirmed, so the run usually has to wait for that move to land. A Flee
    // monster is not counted a hostile, so the held run asks after the monster and
    // not after hostiles.
    [Theory]
    [InlineData(true, false, 1)]    // the monster is where the move landed; no hostile is
    [InlineData(false, true, 0)]    // the move carried us out of its room, into a hostile's
    public void FleeFromMonster_HeldForAMoveInFlight_AsksAfterTheMonsterWhenItLands(
        bool monsterThere, bool hostileThere, int expectedMoves)
    {
        using FleeHarness h = HitAndRunFlee();
        bool pending = true;
        h.Health.IsMovePending = () => pending;

        Assert.Equal(FleeOutcome.Started, h.Health.FleeFromMonster(FleeSight, () => monsterThere));
        Assert.Empty(h.Engine!.SentBacktrackMoves);
        Assert.True(h.Health.IsFleeInFlight);                        // combat holds its engages meanwhile

        pending = false;
        h.HostileInRoom = hostileThere;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 51));

        Assert.Equal(expectedMoves, h.Engine.SentBacktrackMoves.Count);
        Assert.Equal(expectedMoves == 1, h.Health.IsFleeInFlight);
    }

    // A held run from a fight still asks after hostiles: the monster's own check
    // does not outlive its run.
    [Fact]
    public void FleeFromMonster_ItsLandingCheckDoesNotOutliveIt()
    {
        using FleeHarness h = HitAndRunFlee();
        bool pending = true;
        h.Health.IsMovePending = () => pending;
        h.Health.FleeFromMonster(FleeSight, () => false);
        pending = false;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 51));       // dropped: no monster there
        Assert.Empty(h.Engine!.SentBacktrackMoves);

        pending = true;
        h.Health.RunFromBackstabFailure();                           // held, from a fight
        pending = false;
        h.HostileInRoom = true;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 52));

        Assert.Single(h.Engine.SentBacktrackMoves);
    }

    // Report paradigm-20260927-011659: one retreat at a time.
    [Fact]
    public void Flee_WhileAnotherIsInFlight_DoesNotSendASecondMove()
    {
        using FleeHarness h = HitAndRunFlee();

        h.Health.RunFromBackstabFailure();
        h.Health.RunFromBackstabFailure();

        Assert.Single(h.Engine!.SentBacktrackMoves);
    }

    // Same report: a flee whose move was refused waited forever for a landing.
    [Fact]
    public void Flee_MoveRefused_StopsTheRetreatHere()
    {
        using FleeHarness h = HitAndRunFlee();
        h.Combat.HitAndRunMaxRuns = 1;

        h.Health.BackstabLanded(runNow: true);                      // run 1 of 1
        h.Health.NoteMoveBlocked();

        Assert.False(h.Health.IsFleeInFlight);
        Assert.False(h.Health.RunInsteadOfFight("acid slime"));      // budget spent — fight
    }

    // Same report: the loop's step carried us out of the fight room while a run was
    // held for it — nothing to run from where it landed, and "back" leads to the fight.
    [Fact]
    public void HeldFlee_LandingRoomHasNoHostile_IsDropped()
    {
        using FleeHarness h = HitAndRunFlee();
        bool pending = true;
        h.Health.IsMovePending = () => pending;

        h.Health.RunFromBackstabFailure();                           // held for the move
        Assert.Empty(h.Engine!.SentBacktrackMoves);

        pending = false;
        h.HostileInRoom = false;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 49));

        Assert.Empty(h.Engine.SentBacktrackMoves);
        Assert.False(h.Health.IsFleeInFlight);
    }

    // HitAndRunMaxRuns caps the runs between backstabs (the first included); then fight.
    [Fact]
    public void HitAndRun_RunsUntilTheBudget_ThenFights_AndABackstabResetsIt()
    {
        using FleeHarness h = HitAndRunFlee();

        h.Health.BackstabLanded(runNow: true);                     // run 1
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 49));     // landed
        Assert.True(h.Health.RunInsteadOfFight("chaser"));         // run 2
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 48));
        Assert.True(h.Health.RunInsteadOfFight("chaser"));         // run 3
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 47));
        Assert.False(h.Health.RunInsteadOfFight("chaser"));        // spent — fight
        Assert.Equal(3, h.Engine!.SentBacktrackMoves.Count);

        h.Health.BackstabLanded(runNow: false);                    // a clean backstab
        Assert.Equal(0, h.Health.HitAndRunRuns);
        Assert.True(h.Health.RunInsteadOfFight("walk-in"));
    }

    // Report paradigm-20260927-003304: an engage while a run is already under way (or
    // held for a move to land) must not fight or spend another run.
    [Fact]
    public void HitAndRun_AlreadyRunning_DoesNotFightOrChargeTheBudget()
    {
        using FleeHarness h = HitAndRunFlee();
        h.Combat.HitAndRunMaxRuns = 1;

        h.Health.BackstabLanded(runNow: true);                     // run 1 of 1, not landed yet
        Assert.True(h.Health.RunInsteadOfFight("walk-in"));        // still running — no fight
        Assert.Equal(1, h.Health.HitAndRunRuns);
        Assert.Single(h.Engine!.SentBacktrackMoves);
    }

    // Report paradigm-20260926-230835: the flee fired on a room display that beat its
    // move's confirm, planned "back" from the room we'd just left, and walked on past.
    [Fact]
    public void Flee_WhileAMoveIsInFlight_WaitsAndRoutesFromTheLandingRoom()
    {
        using FleeHarness h = new();
        h.Combat.RunDirection = Models.Profile.RunDirection.Backward;
        h.Combat.BreakBeforeFleeing = false;
        h.Combat.RunDistance = 1;
        h.Engine!.JourneyOrigin = new Game.Map.RoomKey(1, 2146);
        h.ReversePath = (from, _) => from.Room == 2150 ? new[] { Game.Map.Direction.U } : new[] { Game.Map.Direction.D };
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 2146));   // loop sends d → 1/2150
        bool pending = true;
        h.Health.IsMovePending = () => pending;
        h.State.MaxHp = 33;
        h.State.HasPromptData = true;
        h.State.Hp = 32;

        h.Health.RunFromBackstabFailure();                           // display beat the confirm
        Assert.Empty(h.Engine.SentBacktrackMoves);

        pending = false;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 2150));   // the d lands

        Assert.Equal(new[] { Game.Map.Direction.U }, h.Engine.SentBacktrackMoves);
    }

    [Fact]
    public void Flee_Backward_ReversePathCapsAtRunDistance()
    {
        // A long reverse trail is trimmed to RunDistance — we flee only as far
        // as configured before re-evaluating, not all the way to the origin.
        using FleeHarness h = new();
        h.Combat.RunDirection = Models.Profile.RunDirection.Backward;
        h.Combat.BreakBeforeFleeing = false;
        h.Combat.RunDistance = 2;
        h.Engine!.JourneyOrigin = new Game.Map.RoomKey(1, 0);
        h.ReversePath = (_, _) => new[]
        {
            Game.Map.Direction.S, Game.Map.Direction.W,
            Game.Map.Direction.U, Game.Map.Direction.E,
        };
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 50));

        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;

        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 100));
        h.HostileInRoom = false;   // landed clear of the fight — nothing to re-flee from
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 101));   // beyond the cap
        Assert.Equal(2, h.Engine.SentBacktrackMoves.Count);
        Assert.Equal(
            new[] { Game.Map.Direction.S, Game.Map.Direction.W },
            h.Engine.SentBacktrackMoves);
    }

    // Crash-20260908-181131: fled out of the Negative Power Plane at -467 HP.
    // BFS routes through CMD-teleport hops, so the reverse trail's first step was
    // Direction.Teleport — which has no wire encoding, so EncodeMove threw off the
    // dispatcher and killed the app mid-fight.
    [Fact]
    public void Flee_Backward_StopsAtATeleportHop()
    {
        using FleeHarness h = new();
        h.Combat.RunDirection = Models.Profile.RunDirection.Backward;
        h.Combat.BreakBeforeFleeing = false;
        h.Combat.RunDistance = 4;
        h.Engine!.JourneyOrigin = new Game.Map.RoomKey(1, 0);
        h.ReversePath = (_, _) => new[]
        {
            Game.Map.Direction.S,
            Game.Map.Direction.W,
            Game.Map.Direction.Teleport,   // can only be crossed by its own command
            Game.Map.Direction.E,          // on the far side — unreachable by fleeing
        };
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 50));

        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;

        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 100));
        h.HostileInRoom = false;   // landed clear of the fight — nothing to re-flee from
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 101));

        Assert.Equal(
            new[] { Game.Map.Direction.S, Game.Map.Direction.W },
            h.Engine.SentBacktrackMoves);
        Assert.DoesNotContain(Game.Map.Direction.Teleport, h.Engine.SentBacktrackMoves);
    }

    [Fact]
    public void Flee_Backward_TeleportFirstStep_SkipsFleeEntirely()
    {
        // Nothing cardinal to walk, so there is no flee route at all. Standing
        // still and letting the other low-HP reactions handle it beats crashing.
        using FleeHarness h = new();
        h.Combat.RunDirection = Models.Profile.RunDirection.Backward;
        h.Combat.BreakBeforeFleeing = false;
        h.Combat.RunDistance = 3;
        h.Engine!.JourneyOrigin = new Game.Map.RoomKey(1, 0);
        h.LastSent = null;   // no last-move fallback either
        h.ReversePath = (_, _) => new[] { Game.Map.Direction.Teleport, Game.Map.Direction.N };
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 50));

        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;

        Assert.Empty(h.Engine.SentBacktrackMoves);
    }

    [Fact]
    public void Flee_Forward_StopsAtATeleportHop()
    {
        // Forward mode walks the engine's own planned route, which crosses the
        // same teleport exits — the report's engine had "next planned: Teleport".
        using FleeHarness h = new();
        h.Combat.RunDirection = Models.Profile.RunDirection.Forward;
        h.Combat.BreakBeforeFleeing = false;
        h.Combat.RunDistance = 4;
        h.Engine!.PlannedForward.AddRange(new[]
        {
            Game.Map.Direction.N, Game.Map.Direction.Teleport, Game.Map.Direction.S,
        });

        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;

        h.HostileInRoom = false;   // landed clear of the fight — nothing to re-flee from
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 100));
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 101));

        Assert.Equal(new[] { Game.Map.Direction.N }, h.Engine.SentBacktrackMoves);
    }

    [Fact]
    public void Flee_AutoResume_OnHpRecovery()
    {
        using FleeHarness h = new();
        h.Combat.RunDistance = 1;
        h.LastSent = Game.Map.Direction.N;

        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;
        Assert.NotNull(h.Engine!.PausedReason);
        h.HostileInRoom = false;                                  // landed clear of the fight
        h.State.InCombat = false;
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 100));   // the flee lands

        // HP climbs back above 20% (default RunIfBelowHp).
        h.State.Hp = 150;

        Assert.NotNull(h.Engine.ResumedAtRoom);
        Assert.Equal(new Game.Map.RoomKey(1, 100), h.Engine.ResumedAtRoom);
    }

    // ----- rest-interruption recovery (server breaks our rest) ----

    [Fact]
    public void Rest_ServerBreaksRest_OutOfCombat_ReRests()
    {
        // We rested; server confirmed (Resting). Then a monster enters
        // and swings — server boots us back to (Standing). HP still
        // below rest-target → on the next Evaluate tick we re-send
        // `rest`.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        Assert.Equal(1, h.SentLines.Count(l => l == "rest"));

        // Server prompt confirms we're resting.
        h.State.Position = PlayerPosition.Resting;
        Assert.True(h.Health.RestInFlight);

        // Server breaks rest — position flips to Standing. We're not
        // in combat (mob hit someone else in the room, didn't engage
        // us directly — common party scenario).
        h.State.Position = PlayerPosition.Standing;

        Assert.Equal(2, h.SentLines.Count(l => l == "rest"));
        Assert.True(h.Health.RestInFlight);
    }

    [Fact]
    public void Rest_ServerBreaksRest_InCombat_HoldsUntilCombatEnds()
    {
        // Server breaks rest because we got engaged. Don't fight the
        // combat by re-sending rest mid-fight — wait for combat to
        // clear, then rest goes out again.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        Assert.Equal(1, h.SentLines.Count(l => l == "rest"));

        h.State.Position = PlayerPosition.Resting;
        h.State.InCombat = true;
        h.State.Position = PlayerPosition.Standing;     // server stood us up

        Assert.Equal(1, h.SentLines.Count(l => l == "rest"));  // not yet
        Assert.False(h.Health.RestInFlight);                   // latch dropped

        // Combat ends — next Evaluate tick re-rests.
        h.State.InCombat = false;
        Assert.Equal(2, h.SentLines.Count(l => l == "rest"));
        Assert.True(h.Health.RestInFlight);
    }

    [Fact]
    public void Rest_PositionStandingBeforeConfirm_NoSpuriousReRest()
    {
        // Race protection: we send rest, but an HP-changed tick fires
        // BEFORE the server's (Resting) prompt arrives. Position is
        // still Standing. We must NOT treat that as an interruption —
        // we haven't confirmed the rest landed yet.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        Assert.Equal(1, h.SentLines.Count(l => l == "rest"));

        // HP drops further before the (Resting) prompt — Position
        // hasn't transitioned to Resting yet.
        h.State.Hp = 30;

        Assert.Equal(1, h.SentLines.Count(l => l == "rest"));
        Assert.True(h.Health.RestInFlight);
    }

    [Fact]
    public void Rest_RecoveryComplete_ClearsLatchAndConfirmFlag()
    {
        // After full recovery, both _restInFlight and the prompt-
        // confirmed flag reset so the next low-HP cycle starts clean.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        h.State.Position = PlayerPosition.Resting;
        Assert.True(h.Health.RestInFlight);

        h.State.Hp = 200;       // recovered — gate clears, post-rest path fires
        Assert.False(h.Health.RestInFlight);

        // Next low-HP must rest cleanly, not get tricked by stale
        // confirm state.
        h.State.Position = PlayerPosition.Standing;
        h.State.Hp = 50;
        Assert.Equal(2, h.SentLines.Count(l => l == "rest"));
    }

    // ----- Meditate vs Rest ------------------------------------------

    [Fact]
    public void Meditate_OnlyMaGated_PrefersMeditate()
    {
        // Caster: MA dropped below trigger, HP at max → meditate.
        HealthSettings s = new() { UseMeditateAbility = true };
        using Harness h = new(s);
        h.State.MaxHp = 200;
        h.State.Hp = 200;        // HP healthy first so HP gate stays clear
        h.State.MaxMa = 100;
        h.State.HasPromptData = true;
        h.State.Ma = 20;         // below default 30% trigger

        Assert.Contains("meditate", h.SentLines);
        Assert.DoesNotContain("rest", h.SentLines);
    }

    [Fact]
    public void Meditate_UseMeditateOff_FallsBackToRest()
    {
        HealthSettings s = new() { UseMeditateAbility = false };
        using Harness h = new(s);
        h.State.MaxHp = 200;
        h.State.Hp = 200;
        h.State.MaxMa = 100;
        h.State.HasPromptData = true;
        h.State.Ma = 20;

        Assert.Contains("rest", h.SentLines);
        Assert.DoesNotContain("meditate", h.SentLines);
    }

    [Fact]
    public void Meditate_BothPoolsGated_MeditateBeforeRestingFlipsOrder()
    {
        HealthSettings s = new() { UseMeditateAbility = true, MeditateBeforeResting = true };
        using Harness h = new(s);
        h.State.MaxHp = 200;
        h.State.MaxMa = 100;
        h.State.HasPromptData = true;
        h.State.Hp = 30;        // below rest-trigger
        h.State.Ma = 20;        // below rest-trigger

        Assert.Contains("meditate", h.SentLines);
    }

    [Fact]
    public void Meditate_BothPoolsGated_DefaultOrderUsesRest()
    {
        // Default MeditateBeforeResting=false → rest covers both pools
        // for non-Kai classes.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.MaxMa = 100;
        h.State.HasPromptData = true;
        h.State.Hp = 30;
        h.State.Ma = 20;

        Assert.Contains("rest", h.SentLines);
    }

    [Fact]
    public void Meditate_ServerBreaksMeditate_OutOfCombat_ReMeditates()
    {
        // Report: meditate never re-engaged after a bless interrupted it.
        // We meditate; server confirms (Meditating). A self-bless fires and
        // knocks us back to (Standing) — same shape as Rest_ServerBreaksRest_
        // OutOfCombat_ReRests, but for the Meditating position the confirm/
        // interrupt latch used to never recognize at all.
        HealthSettings s = new() { UseMeditateAbility = true };
        using Harness h = new(s);
        h.State.MaxHp = 200;
        h.State.Hp = 200;         // HP healthy — only MA gates.
        h.State.MaxMa = 100;
        h.State.HasPromptData = true;
        h.State.Ma = 20;          // below default rest trigger
        Assert.Equal(1, h.SentLines.Count(l => l == "meditate"));

        // Server prompt confirms we're meditating.
        h.State.Position = PlayerPosition.Meditating;
        Assert.True(h.Health.RestInFlight);

        // A bless (or anything else) interrupts it in place — no room move.
        h.State.Position = PlayerPosition.Standing;

        Assert.Equal(2, h.SentLines.Count(l => l == "meditate"));
        Assert.True(h.Health.RestInFlight);
    }

    [Fact]
    public void Rest_RepeatedInterruptionsBelowTarget_EachOneStaysAssertedAndResends()
    {
        // Report paradigm-20260916-035714: a rest cycle survived one interruption
        // (a heal cast standing the character up) but not a SECOND one in the same
        // cycle — the fix must hold across an arbitrary run of interruptions, not
        // just the first, since wasActivelyResting is recomputed fresh every tick
        // from _restInFlight + _restConfirmedByPrompt (which the prior resend
        // re-establishes before the next interruption lands).
        using Harness h = new();
        h.State.MaxMa = 0;
        h.SetPrompt(hp: 200, maxHp: 400);   // below 60% trigger (240)
        Assert.True(h.HealthGateHeld);
        Assert.Equal(1, h.SentLines.Count(l => l == "rest"));

        h.State.Position = PlayerPosition.Resting;

        // First interruption: HP climbed to 260 (above 240 trigger, below 380 target).
        h.State.Hp = 260;
        h.State.Position = PlayerPosition.Standing;
        Assert.True(h.HealthGateHeld);
        Assert.Equal(2, h.SentLines.Count(l => l == "rest"));

        h.State.Position = PlayerPosition.Resting;

        // Second interruption in the SAME cycle: HP climbed further to 300 (still
        // above trigger, still below target). Must ALSO stay asserted + resend.
        h.State.Hp = 300;
        h.State.Position = PlayerPosition.Standing;
        Assert.True(h.HealthGateHeld);
        Assert.Equal(3, h.SentLines.Count(l => l == "rest"));
    }

    [Fact]
    public void Rest_Interrupted_WithTheReRestPutOffForASneak_StillRestsOnToTheTarget()
    {
        // Report paradigm-20261004-202253: a ShadowRest character resting from 63
        // toward 121 of 151 HP was stood up at 96 by a buff ticked "cast while
        // resting". The re-rest waits for the sneak that goes first, and on the next
        // pass nothing remembered the recovery: the gate cleared against the 77
        // trigger and the rest was dropped at 64%.
        using Harness h = new();
        bool sneakFirst = false;
        h.Health.SetShadowRest(() => true, () => true, () => true, () => { });
        h.Health.SetSneakBeforeRestProbe(() => sneakFirst);
        h.Settings.UtilizeShadowRest = true;
        h.State.MaxMa = 0;
        h.SetPrompt(hp: 200, maxHp: 400);   // below the 60% trigger (240); target 380
        Assert.Equal(1, h.SentLines.Count(l => l == "rest"));
        h.State.Position = PlayerPosition.Resting;

        // A cast stands us up at 260 (over the trigger, far under the target), and
        // the rest has to wait for the sneak.
        sneakFirst = true;
        h.State.Hp = 260;
        h.State.Position = PlayerPosition.Standing;
        Assert.True(h.HealthGateHeld);
        Assert.Equal(1, h.SentLines.Count(l => l == "rest"));

        // Another pass while still waiting: the recovery is not forgotten.
        h.State.Hp = 261;
        Assert.True(h.HealthGateHeld);

        // The sneak lands; the rest goes out again.
        sneakFirst = false;
        h.Health.Evaluate();
        Assert.True(h.HealthGateHeld);
        Assert.Equal(2, h.SentLines.Count(l => l == "rest"));
    }

    [Fact]
    public void Meditate_InterruptedBelowTarget_GateStaysAssertedAndReMeditates()
    {
        // Report paradigm-20260915-211744: SelfBlessWhileResting stands the
        // character up mid-meditate to recast a due buff (vlwa). MA had already
        // climbed past the rest-TRIGGER (30%) but was nowhere near the
        // rest-MAX target (95%) — the interruption must not let the clear-floor
        // fall back to the trigger and release the gate early; it should stay
        // asserted and keep re-meditating until the real target is reached.
        HealthSettings s = new() { UseMeditateAbility = true };
        using Harness h = new(s);
        h.State.MaxHp = 200;
        h.State.Hp = 200;         // HP healthy — only MA gates.
        h.State.MaxMa = 100;
        h.State.HasPromptData = true;
        h.State.Ma = 20;          // below the 30% rest trigger
        Assert.Equal(1, h.SentLines.Count(l => l == "meditate"));
        Assert.True(h.Health.MaGateAsserted);

        h.State.Position = PlayerPosition.Meditating;
        Assert.True(h.Health.RestInFlight);

        // Mana climbed to 60 (above the 30 trigger, far below the 95 target)
        // when a due self-buff stands the character up.
        h.State.Ma = 60;
        h.State.Position = PlayerPosition.Standing;

        Assert.True(h.Health.MaGateAsserted);
        Assert.Equal(2, h.SentLines.Count(l => l == "meditate"));
        Assert.True(h.Health.RestInFlight);
    }

    // ----- Hangup-on-emergency --------------------------------------

    [Fact]
    public void Hangup_HpBelowTrigger_SendsDisconnect()
    {
        using Harness h = new();
        // Prompt-accurate ordering (Hp before HasPromptData): the hangup now
        // fires anywhere in the (deathFloor, hangTrigger] window, so the value
        // must be settled before the prompt flip — otherwise a transient Hp=0
        // (a dropped state) would itself trip the disconnect.
        h.SetPrompt(hp: 5, maxHp: 200);   // 2.5% — below default 5% hang threshold

        Assert.Contains("=x", h.SentLines);
    }

    [Fact]
    public void Hangup_SignalsIntentionalDisconnect()
    {
        // The emergency hangup drops the carrier on purpose. It must flag the
        // HangupSignal so MainWindowViewModel classifies the drop as intentional
        // and the reactive-reconnect path stands down — otherwise the client
        // dials straight back into the danger it just fled.
        using Harness h = new();
        Assert.False(h.Hangup.ConsumeDisconnectIntent());

        h.SetPrompt(hp: 5, maxHp: 200);   // below default 5% hang threshold — fires

        Assert.Contains("=x", h.SentLines);
        Assert.True(h.Hangup.ConsumeDisconnectIntent());
    }

    [Fact]
    public void Hangup_ClosesCarrierAfterExitCommand()
    {
        // The exit command alone leaves the drop to the server. The hangup now
        // also asks the client to close the socket itself — the exit command
        // must go out first, then the disconnect request.
        using Harness h = new();
        Assert.Equal(0, h.HangupDisconnectCount);

        h.SetPrompt(hp: 5, maxHp: 200);   // below default 5% hang threshold — fires

        Assert.Contains("=x", h.SentLines);
        Assert.Equal(1, h.HangupDisconnectCount);
    }

    [Fact]
    public void Hangup_NoExitCommand_DoesNotCloseCarrier()
    {
        // With no exit command configured the hangup can't fire (it falls back to
        // rest), so it must not half-drop the client either — no socket close.
        using Harness h = new() { HangupCommand = null };

        h.SetPrompt(hp: 5, maxHp: 200);

        Assert.DoesNotContain("=x", h.SentLines);
        Assert.Equal(0, h.HangupDisconnectCount);
    }

    // ----- @panic (MegaMUD parity) -----------------------------------

    [Fact]
    public void Panic_WhenLeadingAndOptedIn_BroadcastsOnEmergencyHangup()
    {
        // Leader with "use @panic while leading": crossing the hang floor says a
        // bare ".@panic" on say, then still escapes (the hangup fires too).
        using Harness h = new()
        {
            SelfIsLeader = true,
            Party = new PartySettings { UsePanicWhileLeading = true },
        };

        h.SetPrompt(hp: 5, maxHp: 200);   // below default 5% hang threshold

        Assert.Contains(".@panic", h.SentLines);
        Assert.Contains("=x", h.SentLines);           // escape still happens
    }

    [Fact]
    public void Panic_NotLeading_NoBroadcast()
    {
        // Opted in but following, not leading — no @panic (only a leader panics).
        using Harness h = new()
        {
            SelfIsLeader = false,
            Party = new PartySettings { UsePanicWhileLeading = true },
        };

        h.SetPrompt(hp: 5, maxHp: 200);

        Assert.DoesNotContain(".@panic", h.SentLines);
        Assert.Contains("=x", h.SentLines);           // still hangs up for ourselves
    }

    [Fact]
    public void Panic_LeadingButOptedOut_NoBroadcast()
    {
        // Leading but "use @panic while leading" is off — no broadcast.
        using Harness h = new()
        {
            SelfIsLeader = true,
            Party = new PartySettings { UsePanicWhileLeading = false },
        };

        h.SetPrompt(hp: 5, maxHp: 200);

        Assert.DoesNotContain(".@panic", h.SentLines);
        Assert.Contains("=x", h.SentLines);
    }

    [Fact]
    public void RespondToReceivedPanic_HangsUp()
    {
        // A partymate's @panic makes us bail regardless of our own HP — default
        // config has no wimpy location, so it drops the carrier.
        using Harness h = new();
        // Healthy HP: the response is driven by the panic, not our own floor.
        h.SetPrompt(hp: 200, maxHp: 200);

        bool acted = h.Health.RespondToReceivedPanic("Bob");

        Assert.True(acted);
        Assert.Contains("=x", h.SentLines);
        Assert.Equal(1, h.HangupDisconnectCount);
        Assert.True(h.Hangup.ConsumeDisconnectIntent());
    }

    [Fact]
    public void RespondToReceivedPanic_WimpyJumps_WhenConfigured()
    {
        // With sys-goto-wimpy configured + firing, a received @panic jumps instead
        // of dropping the carrier.
        HealthSettings s = new()
        {
            SysGotoWimpyInsteadOfHanging = true,
            SysGotoWimpyLocation = "wimpy-room",
        };
        using Harness h = new(s) { WimpyFireResult = true };
        h.SetPrompt(hp: 200, maxHp: 200);

        bool acted = h.Health.RespondToReceivedPanic("Bob");

        Assert.True(acted);
        Assert.Equal("wimpy-room", h.WimpyFiredWith);
        Assert.DoesNotContain("=x", h.SentLines);     // jumped, didn't drop
        Assert.Equal(0, h.HangupDisconnectCount);
    }

    [Fact]
    public void RespondToReceivedPanic_DisableHangups_SuppressesCarrierDrop()
    {
        // DisableHangups master switch: a received @panic must never force-drop the
        // carrier. With no wimpy location it simply stays put.
        using Harness h = new();
        h.General.DisableHangups = true;
        h.SetPrompt(hp: 200, maxHp: 200);

        bool acted = h.Health.RespondToReceivedPanic("Bob");

        Assert.False(acted);
        Assert.DoesNotContain("=x", h.SentLines);
        Assert.Equal(0, h.HangupDisconnectCount);
    }

    [Fact]
    public void RespondToReceivedPanic_DisableHangups_StillWimpyJumps()
    {
        // DisableHangups blocks the carrier drop but not the wimpy jump (which
        // doesn't drop the carrier) — an opted-out character still escapes to safety.
        HealthSettings s = new()
        {
            SysGotoWimpyInsteadOfHanging = true,
            SysGotoWimpyLocation = "wimpy-room",
        };
        using Harness h = new(s) { WimpyFireResult = true };
        h.General.DisableHangups = true;
        h.SetPrompt(hp: 200, maxHp: 200);

        bool acted = h.Health.RespondToReceivedPanic("Bob");

        Assert.True(acted);
        Assert.Equal("wimpy-room", h.WimpyFiredWith);
        Assert.DoesNotContain("=x", h.SentLines);
    }

    // The hang-up penalty line is asked for once per hang-up actually sent, after
    // the exit command, and is told which hang-up was the PvP response's. A wimpy
    // jump or a suppressed drop sends no hang-up, so asks for no line.
    [Fact]
    public void HangupPenaltyLog_ToldAfterEachHangupSent_WithThePvpResponseMarked()
    {
        using Harness low = new();
        List<bool> lowAsked = new();
        low.Health.SetHangupPenaltyLog(pvp =>
        {
            Assert.Contains("=x", low.SentLines);
            lowAsked.Add(pvp);
        });
        low.SetPrompt(hp: 5, maxHp: 200);
        Assert.Equal(new[] { false }, lowAsked);

        using Harness panic = new();
        List<bool> panicAsked = new();
        panic.Health.SetHangupPenaltyLog(panicAsked.Add);
        panic.SetPrompt(hp: 200, maxHp: 200);
        panic.Health.RespondToReceivedPanic("Bob");
        Assert.Equal(new[] { false }, panicAsked);

        using Harness pvp = new();
        List<bool> pvpAsked = new();
        pvp.Health.SetHangupPenaltyLog(pvpAsked.Add);
        pvp.SetPrompt(hp: 200, maxHp: 200);
        Assert.True(pvp.Health.HangUpForPvp("Bob attacked us"));
        Assert.Equal(new[] { true }, pvpAsked);
    }

    [Fact]
    public void HangupPenaltyLog_NotToldWhenNoHangupGoesOut()
    {
        HealthSettings s = new()
        {
            SysGotoWimpyInsteadOfHanging = true,
            SysGotoWimpyLocation = "wimpy-room",
        };
        using Harness jumped = new(s) { WimpyFireResult = true };
        int asked = 0;
        jumped.Health.SetHangupPenaltyLog(_ => asked++);
        jumped.SetPrompt(hp: 200, maxHp: 200);
        Assert.True(jumped.Health.HangUpForPvp("Bob is here"));

        using Harness disabled = new();
        disabled.General.DisableHangups = true;
        disabled.Health.SetHangupPenaltyLog(_ => asked++);
        disabled.SetPrompt(hp: 5, maxHp: 200);
        Assert.False(disabled.Health.RespondToReceivedPanic("Bob"));
        Assert.False(disabled.Health.HangUpForPvp("Bob is here"));

        Assert.Equal(0, asked);
    }

    // A Hangup-relationship monster in the room takes the low-HP trigger's escape,
    // whatever our HP: the sight is the trigger.
    [Fact]
    public void HangUpForMonster_HangsUp_AtFullHealth_WithNoHostileCounted()
    {
        using Harness h = new() { HostileInRoom = false };
        List<bool> asked = new();
        h.Health.SetHangupPenaltyLog(pvp =>
        {
            Assert.Contains("=x", h.SentLines);
            asked.Add(pvp);
        });
        h.SetPrompt(hp: 200, maxHp: 200);

        Assert.Equal(EscapeOutcome.HungUp, h.Health.HangUpForMonster("ogre (#7) is here, relationship Hangup"));

        Assert.Contains("=x", h.SentLines);
        Assert.Equal(1, h.HangupDisconnectCount);
        Assert.True(h.Hangup.ConsumeDisconnectIntent());
        Assert.Equal(new[] { false }, asked);
    }

    // Disable Hangups stops all of it, as it does for the low-HP trigger: no
    // carrier drop, no wimpy jump in its place, no penalty line.
    [Fact]
    public void HangUpForMonster_DisableHangups_SendsNothing()
    {
        HealthSettings s = new()
        {
            SysGotoWimpyInsteadOfHanging = true,
            SysGotoWimpyLocation = "wimpy-room",
        };
        using Harness h = new(s) { WimpyFireResult = true };
        h.General.DisableHangups = true;
        int asked = 0;
        h.Health.SetHangupPenaltyLog(_ => asked++);
        h.SetPrompt(hp: 200, maxHp: 200);

        Assert.Equal(EscapeOutcome.HangupsDisabled, h.Health.HangUpForMonster("ogre (#7) is here, relationship Hangup"));

        Assert.DoesNotContain("=x", h.SentLines);
        Assert.Null(h.WimpyFiredWith);
        Assert.Equal(0, h.HangupDisconnectCount);
        Assert.False(h.Hangup.ConsumeDisconnectIntent());
        Assert.Equal(0, asked);
    }

    // The master-switch rule (user, 2026-10-09): with the switch off nothing
    // responds unless Allow hangup in all-off mode is ticked.
    [Theory]
    [InlineData(false, EscapeOutcome.AllOff)]
    [InlineData(true, EscapeOutcome.HungUp)]
    public void HangUpForMonster_AllOff_FollowsAllowHangupInAllOffMode(bool allowed, EscapeOutcome expected)
    {
        HealthSettings s = new()
        {
            SysGotoWimpyInsteadOfHanging = true,
            SysGotoWimpyLocation = "wimpy-room",
        };
        using Harness h = new(s) { MasterSwitchOff = true };
        h.General.AllowHangupInAllOffMode = allowed;
        h.SetPrompt(hp: 200, maxHp: 200);

        Assert.Equal(expected, h.Health.HangUpForMonster("ogre (#7) is here, relationship Hangup"));

        Assert.Equal(allowed, h.SentLines.Contains("=x"));
        Assert.Equal(allowed ? 1 : 0, h.HangupDisconnectCount);
        // Held, it is all held: the wimpy jump is not tried either.
        Assert.Equal(allowed ? "wimpy-room" : null, h.WimpyFiredWith);
    }

    // Disable Hangups outranks the all-off carve-out here as it does for low HP.
    [Fact]
    public void HangUpForMonster_DisableHangups_OutranksAllowHangupInAllOffMode()
    {
        using Harness h = new() { MasterSwitchOff = true };
        h.General.AllowHangupInAllOffMode = true;
        h.General.DisableHangups = true;
        h.SetPrompt(hp: 200, maxHp: 200);

        Assert.Equal(EscapeOutcome.HangupsDisabled, h.Health.HangUpForMonster("ogre (#7) is here, relationship Hangup"));

        Assert.DoesNotContain("=x", h.SentLines);
    }

    [Fact]
    public void HangUpForMonster_WimpyJumps_WhenConfigured()
    {
        HealthSettings s = new()
        {
            SysGotoWimpyInsteadOfHanging = true,
            SysGotoWimpyLocation = "wimpy-room",
        };
        using Harness h = new(s) { WimpyFireResult = true };
        h.SetPrompt(hp: 200, maxHp: 200);

        Assert.Equal(EscapeOutcome.Jumped, h.Health.HangUpForMonster("ogre (#7) is here, relationship Hangup"));

        Assert.Equal("wimpy-room", h.WimpyFiredWith);
        Assert.DoesNotContain("=x", h.SentLines);
        Assert.Equal(0, h.HangupDisconnectCount);
    }

    [Fact]
    public void HangUpForMonster_NoExitCommand_ReportsNothingSent()
    {
        using Harness h = new() { HangupCommand = null };
        h.SetPrompt(hp: 200, maxHp: 200);

        Assert.Equal(EscapeOutcome.NotSent, h.Health.HangUpForMonster("ogre (#7) is here, relationship Hangup"));

        Assert.Equal(0, h.HangupDisconnectCount);
    }

    // ----- one escape answers the danger --------------------------------
    //
    // Low HP, a Hangup monster and a PvP enemy can all be read off one room
    // roster. Whichever asks first sends the escape; one asked for a moment later
    // sends nothing more and is told the danger is already answered.

    [Fact]
    public void LowHp_RightAfterAMonsterHangUp_SendsNothingMore()
    {
        using Harness h = new()
        {
            SelfIsLeader = true,
            Party = new PartySettings { UsePanicWhileLeading = true },
        };
        List<bool> penaltyAsked = new();
        h.Health.SetHangupPenaltyLog(penaltyAsked.Add);
        h.SetPrompt(hp: 200, maxHp: 200);
        Assert.Equal(EscapeOutcome.HungUp, h.Health.HangUpForMonster("ogre (#7) is here, relationship Hangup"));

        // The same roster, read by the low-HP re-check, with HP through the trigger.
        h.SetPrompt(hp: 5, maxHp: 200);
        h.Health.ReevaluateEmergencyHangup();

        Assert.Equal(1, h.SentLines.Count(line => line == "=x"));
        Assert.Equal(1, h.HangupDisconnectCount);
        Assert.Equal(new[] { false }, penaltyAsked);
        Assert.DoesNotContain(".@panic", h.SentLines);
    }

    // The repeat is skipped, not the hang-up: still here and still in danger once
    // the moment has passed, the low-HP trigger fires as normal.
    [Fact]
    public void LowHp_ThreeSecondsAfterAMonsterHangUp_HangsUp()
    {
        using Harness h = new();
        h.SetPrompt(hp: 200, maxHp: 200);
        Assert.Equal(EscapeOutcome.HungUp, h.Health.HangUpForMonster("ogre (#7) is here, relationship Hangup"));
        h.SetPrompt(hp: 5, maxHp: 200);
        Assert.Equal(1, h.SentLines.Count(line => line == "=x"));

        h.Clock += TimeSpan.FromSeconds(3);
        h.Health.ReevaluateEmergencyHangup();

        Assert.Equal(2, h.SentLines.Count(line => line == "=x"));
        Assert.Equal(2, h.HangupDisconnectCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PvpHangUpAndLowHp_Together_SendOneEscape(bool pvpFirst)
    {
        using Harness h = new();
        List<bool> penaltyAsked = new();
        h.Health.SetHangupPenaltyLog(penaltyAsked.Add);
        h.SetPrompt(hp: 200, maxHp: 200);

        if (pvpFirst)
        {
            Assert.True(h.Health.HangUpForPvp("Bob is here"));
            h.SetPrompt(hp: 5, maxHp: 200);
        }
        else
        {
            h.SetPrompt(hp: 5, maxHp: 200);
            // Told the danger is answered, so the PvP response counts it as hung up.
            Assert.True(h.Health.HangUpForPvp("Bob is here"));
        }

        Assert.Equal(1, h.SentLines.Count(line => line == "=x"));
        Assert.Equal(1, h.HangupDisconnectCount);
        Assert.Equal(new[] { pvpFirst }, penaltyAsked);
    }

    [Fact]
    public void AnEscapeThreeSecondsAfterAnother_GoesOut()
    {
        using Harness h = new();
        h.SetPrompt(hp: 200, maxHp: 200);
        Assert.True(h.Health.HangUpForPvp("Bob is here"));
        Assert.Equal(EscapeOutcome.AlreadyEscaping, h.Health.HangUpForMonster("ogre (#7) is here, relationship Hangup"));

        h.Clock += TimeSpan.FromSeconds(3);

        Assert.Equal(EscapeOutcome.HungUp, h.Health.HangUpForMonster("ogre (#7) is here, relationship Hangup"));
        Assert.Equal(2, h.SentLines.Count(line => line == "=x"));
    }

    // A jump has answered the danger as much as a hang-up has: a hang-up asked for
    // on its heels is not sent after it.
    [Fact]
    public void AHangUpAskedForRightAfterAWimpyJump_SendsNothingMore()
    {
        HealthSettings s = new()
        {
            SysGotoWimpyInsteadOfHanging = true,
            SysGotoWimpyLocation = "wimpy-room",
        };
        using Harness h = new(s) { WimpyFireResult = true };
        h.SetPrompt(hp: 200, maxHp: 200);
        Assert.Equal(EscapeOutcome.Jumped, h.Health.HangUpForMonster("ogre (#7) is here, relationship Hangup"));

        h.WimpyFireResult = false;   // a second jump would be refused and fall through to the hang-up
        Assert.True(h.Health.RespondToReceivedPanic("Bob"));
        Assert.DoesNotContain("=x", h.SentLines);
        Assert.Equal(0, h.HangupDisconnectCount);

        h.Clock += TimeSpan.FromSeconds(3);
        Assert.True(h.Health.RespondToReceivedPanic("Bob"));
        Assert.Contains("=x", h.SentLines);
    }

    // Only an escape that went out counts. One that could not be sent leaves
    // nothing to repeat, so the hang-up asked for right after it goes out.
    [Fact]
    public void AnEscapeThatWasNotSent_HoldsOffNothing()
    {
        using Harness h = new() { HangupCommand = null };
        h.SetPrompt(hp: 200, maxHp: 200);
        Assert.Equal(EscapeOutcome.NotSent, h.Health.HangUpForMonster("ogre (#7) is here, relationship Hangup"));

        h.HangupCommand = "=x";

        Assert.Equal(EscapeOutcome.HungUp, h.Health.HangUpForMonster("ogre (#7) is here, relationship Hangup"));
        Assert.Equal(1, h.SentLines.Count(line => line == "=x"));
    }

    // A clock set back must not stretch the moment into a held-off hang-up.
    [Fact]
    public void AClockSetBack_DoesNotHoldOffTheNextEscape()
    {
        using Harness h = new();
        h.SetPrompt(hp: 200, maxHp: 200);
        Assert.True(h.Health.HangUpForPvp("Bob is here"));

        h.Clock -= TimeSpan.FromHours(1);

        Assert.Equal(EscapeOutcome.HungUp, h.Health.HangUpForMonster("ogre (#7) is here, relationship Hangup"));
    }

    [Fact]
    public void WimpyGoto_Enabled_AndFires_SkipsHangup()
    {
        // "Sys goto wimpy instead of hanging": the emergency escape jumps instead of
        // dropping the carrier. The wimpy delegate fires (returns true) → no exit
        // command, no socket close, and it was asked for the configured location.
        HealthSettings s = new()
        {
            SysGotoWimpyInsteadOfHanging = true,
            SysGotoWimpyLocation = "lostcity",
        };
        using Harness h = new(s) { WimpyFireResult = true };

        h.SetPrompt(hp: 5, maxHp: 200);   // below 5% hang threshold with a hostile

        Assert.Equal("lostcity", h.WimpyFiredWith);
        Assert.DoesNotContain("=x", h.SentLines);   // no hangup exit command
        Assert.Equal(0, h.HangupDisconnectCount);   // carrier not closed
    }

    [Fact]
    public void WimpyGoto_Enabled_ButCantFire_FallsBackToHangup()
    {
        // Power off on this BBS / location gone → the delegate returns false, so the
        // normal hangup runs instead (a mis-set escape never strands a low-HP char).
        HealthSettings s = new()
        {
            SysGotoWimpyInsteadOfHanging = true,
            SysGotoWimpyLocation = "lostcity",
        };
        using Harness h = new(s) { WimpyFireResult = false };

        h.SetPrompt(hp: 5, maxHp: 200);

        Assert.Equal("lostcity", h.WimpyFiredWith);   // it tried
        Assert.Contains("=x", h.SentLines);           // then hung up
        Assert.Equal(1, h.HangupDisconnectCount);
    }

    [Fact]
    public void WimpyGoto_NoLocationSet_DoesNotAttempt_AndHangsUp()
    {
        // Opted in but no location chosen — nothing to jump to, so the delegate is
        // never called and the normal hangup fires.
        HealthSettings s = new()
        {
            SysGotoWimpyInsteadOfHanging = true,
            SysGotoWimpyLocation = string.Empty,
        };
        using Harness h = new(s) { WimpyFireResult = true };

        h.SetPrompt(hp: 5, maxHp: 200);

        Assert.Null(h.WimpyFiredWith);       // never attempted
        Assert.Contains("=x", h.SentLines);  // hung up as normal
    }

    [Fact]
    public void WimpyGoto_MortallyWounded_StillFires_NoHangup()
    {
        // `sys` commands aren't gated by the mortally-wounded state (confirmed
        // mechanic), so the wimpy escape fires even below 0 HP — the jump is sent on
        // a gate-piercing wire. No fallback hangup when it dispatches.
        HealthSettings s = new()
        {
            SysGotoWimpyInsteadOfHanging = true,
            SysGotoWimpyLocation = "lostcity",
        };
        using Harness h = new(s) { WimpyFireResult = true };

        h.SetPrompt(hp: -2, maxHp: 200);   // below 0 — mortally wounded, still in-window

        Assert.Equal("lostcity", h.WimpyFiredWith);   // fired despite being dropped
        Assert.DoesNotContain("=x", h.SentLines);      // no hangup
        Assert.Equal(0, h.HangupDisconnectCount);
    }

    [Fact]
    public void WimpyGoto_Disabled_HangsUpNormally()
    {
        // Feature off → the delegate is never consulted; the hangup fires as before.
        HealthSettings s = new()
        {
            SysGotoWimpyInsteadOfHanging = false,
            SysGotoWimpyLocation = "lostcity",
        };
        using Harness h = new(s) { WimpyFireResult = true };

        h.SetPrompt(hp: 5, maxHp: 200);

        Assert.Null(h.WimpyFiredWith);
        Assert.Contains("=x", h.SentLines);
    }

    [Fact]
    public void Hangup_ZeroSetting_FiresAtZeroNotAbove()
    {
        // 0 is a live trigger now — "hang the moment I drop", not a disable. The
        // off-switch is GeneralSettings.DisableHangups. So at 1 HP (above 0) it
        // holds; the instant HP hits 0 it fires.
        HealthSettings s = new() { HangIfBelowHp = 0 };
        using Harness h = new(s);
        // Settle Hp before the prompt flips on (SetPrompt does this ordering), so
        // the default Hp=0 doesn't trip the 0 trigger during setup.
        h.SetPrompt(hp: 1, maxHp: 200);             // above the 0 trigger
        Assert.DoesNotContain("=x", h.SentLines);

        h.State.Hp = 0;                             // dropped — at the trigger
        Assert.Contains("=x", h.SentLines);
    }

    [Fact]
    public void Hangup_AboveThreshold_NoFire()
    {
        using Harness h = new();
        h.SetPrompt(hp: 50, maxHp: 200);   // 25% — above 5% hang threshold

        Assert.DoesNotContain("=x", h.SentLines);
    }

    [Fact]
    public void Hangup_ZeroMana_HealthyHp_NeverHangs()
    {
        // Mana is NOT a hangup trigger — only HP is. A drained caster with
        // full HP just meditates / rests; it must never auto-disconnect.
        using Harness h = new();
        h.SetPrompt(hp: 200, maxHp: 200, ma: 0, maxMa: 100);

        Assert.DoesNotContain("=x", h.SentLines);
    }

    [Fact]
    public void Hangup_SingleShot_DoesNotRefire()
    {
        // First crossing fires; subsequent property changes within
        // the same low-HP window must not re-fire.
        using Harness h = new();
        h.SetPrompt(hp: 5, maxHp: 200);
        int hangCount = h.SentLines.Count(l => l == "=x");
        Assert.Equal(1, hangCount);

        h.State.Hp = 3;        // even lower — still no second hang
        Assert.Equal(1, h.SentLines.Count(l => l == "=x"));
    }

    // ----- bleeding-out window (per-BBS death floor) ----------------
    // 0 HP only drops a MajorMUD character (bleeding out — revivable, still
    // able to hang up); death happens at the per-realm negative floor. The
    // emergency hangup must stay live all the way through that window, down to
    // but not past the floor.

    [Fact]
    public void Hangup_JustDroppedAtZero_StillHangs()
    {
        // Exactly 0 HP — the top of the bleeding-out window. Old logic bailed
        // at Hp<=0; now the disconnect fires (a dropped character can hang up).
        using Harness h = new();
        h.SetPrompt(hp: 0, maxHp: 200);

        Assert.Contains("=x", h.SentLines);
    }

    [Fact]
    public void Hangup_BleedingOutAboveFloor_StillHangs()
    {
        // Dropped and bleeding out, but above the -25 floor → still alive,
        // still able to escape.
        using Harness h = new();
        h.SetPrompt(hp: -10, maxHp: 200);

        Assert.Contains("=x", h.SentLines);
    }

    [Fact]
    public void Hangup_BleedingOutNonCaster_StillHangs()
    {
        // The regression this restructure fixes: a non-caster (Ma 0/0) at
        // negative HP used to hit Evaluate's `Hp<=0 && Ma<=0` early-return and
        // never reach the hangup. It must fire now.
        using Harness h = new();
        h.SetPrompt(hp: -10, maxHp: 200, ma: 0, maxMa: 0);

        Assert.Contains("=x", h.SentLines);
    }

    [Fact]
    public void Hangup_AtDeathFloor_DoesNotHang()
    {
        // Exactly at the floor — already dead, nothing left to disconnect.
        using Harness h = new();
        h.SetPrompt(hp: -25, maxHp: 200);

        Assert.DoesNotContain("=x", h.SentLines);
    }

    [Fact]
    public void Hangup_PastDeathFloor_DoesNotHang()
    {
        using Harness h = new();
        h.SetPrompt(hp: -30, maxHp: 200);   // overshot the floor → dead

        Assert.DoesNotContain("=x", h.SentLines);
    }

    [Fact]
    public void Hangup_PiercesEngineSendGateHold()
    {
        // The escape hangup must survive the very EngineSendGate hold that a
        // drop raises: the ordinary wire sender is gate-wrapped (drops silently
        // while held), but the hangup rides a separate un-wrapped sender. Prove
        // it lands even with the gate locked.
        PlayerState state = new();
        LogService log = new();
        MovementCoordinator coordinator = new(log);
        GeneralSettings general = new();
        HealthSettings settings = new();

        EngineSendGate gate = new();
        List<byte[]> wrappedSent = new();
        List<byte[]> hangupSent = new();

        HealthManager health = new(state, coordinator,
            readSettings: () => settings,
            isEnabled: () => true,
            readHangupCommand: () => "=x",
            getActiveMovementEngine: null,
            getLastSentDirection: null,
            readCombatSettings: null,
            readGeneralSettings: () => general,
            hasEngageableHostiles: () => false,
            readDeathFloor: () => -25,
            log: log,
            hangupSignal: null);
        // Regular sends are gate-wrapped; the hangup sender is raw.
        health.SetWireSender(gate.WrapEngineSender(wrappedSent.Add));
        health.SetHangupWireSender(hangupSent.Add);

        // A drop would hold the gate — simulate it.
        gate.Hold(PlayerDroppedGate.HoldReason);

        state.Hp = 0;
        state.MaxHp = 200;
        state.HasPromptData = true;   // fires Evaluate → emergency hangup

        string Decode(List<byte[]> l) =>
            l.Count == 0 ? "" : Encoding.Latin1.GetString(l[^1]).TrimEnd('\r');

        Assert.Equal("=x", Decode(hangupSent));   // hangup pierced the hold
        Assert.DoesNotContain(wrappedSent,
            b => Encoding.Latin1.GetString(b).TrimEnd('\r') == "=x");

        health.Dispose();
    }

    [Fact]
    public void Hangup_CustomFloor_FiresDownToConfiguredFloor()
    {
        // A realm with a deeper floor keeps the window open further.
        using Harness h = new() { DeathFloor = -50 };
        h.SetPrompt(hp: -40, maxHp: 200);   // above -50 → still hangs

        Assert.Contains("=x", h.SentLines);
    }

    [Fact]
    public void Hangup_CustomFloor_BailsAtConfiguredFloor()
    {
        using Harness h = new() { DeathFloor = -50 };
        h.SetPrompt(hp: -50, maxHp: 200);   // at the deeper floor → dead

        Assert.DoesNotContain("=x", h.SentLines);
    }

    [Fact]
    public void Hangup_PositiveFloorClampsToZero()
    {
        // A misconfigured positive floor collapses to 0, restoring the old
        // positive-band-only behavior — a bleeding-out char below 0 won't hang.
        using Harness h = new() { DeathFloor = 10 };
        h.SetPrompt(hp: -5, maxHp: 200);

        Assert.DoesNotContain("=x", h.SentLines);
    }

    // ----- negative hangup, both modes (issue 107) ------------------
    // The hang trigger is a point on one continuous HP scale that runs from the
    // top down through 0 into the negatives — HP% goes negative while bleeding out
    // (as par shows), so a percentage trigger goes negative just like an absolute
    // one. Either way the user can set the hangup deep in the bleeding-out band,
    // closer to death, bounded at the per-BBS death floor.

    [Fact]
    public void Hangup_PercentMode_NegativeTrigger_FiresInBleedOut()
    {
        // -6 % of 200 max = -12 HP trigger; floor -25. Dropping to -15 sits inside
        // (-25, -12] and fires — a percentage hangup set past 0 into the bleed band.
        HealthSettings s = new()
        {
            HpThresholdMode = ThresholdMode.Percentage,
            HangIfBelowHp = -6,
        };
        using Harness h = new(s);
        h.SetPrompt(hp: -15, maxHp: 200);

        Assert.Contains("=x", h.SentLines);
    }

    [Fact]
    public void Hangup_PercentMode_NegativeTrigger_AboveTrigger_NoFire()
    {
        // Above the -12 HP trigger the -6 % setting resolves to — hold the line.
        HealthSettings s = new()
        {
            HpThresholdMode = ThresholdMode.Percentage,
            HangIfBelowHp = -6,
        };
        using Harness h = new(s);
        h.SetPrompt(hp: -10, maxHp: 200);   // -10 > -12 → no fire

        Assert.DoesNotContain("=x", h.SentLines);
    }

    [Fact]
    public void Hangup_ValueMode_NegativeTrigger_FiresInBleedOut()
    {
        // Trigger at -10, floor at -25: dropping to -15 sits inside (-25, -10] and
        // fires — a hangup deliberately set past 0 into the bleeding-out band.
        HealthSettings s = new()
        {
            HpThresholdMode = ThresholdMode.Absolute,
            HangIfBelowHp = -10,
        };
        using Harness h = new(s);
        h.SetPrompt(hp: -15, maxHp: 200);

        Assert.Contains("=x", h.SentLines);
    }

    [Fact]
    public void Hangup_ValueMode_NegativeTrigger_AboveTrigger_NoFire()
    {
        // Bleeding out, but above the chosen -10 trigger — hold the connection so
        // a party heal / revive can still reach a character who set a deep hangup.
        HealthSettings s = new()
        {
            HpThresholdMode = ThresholdMode.Absolute,
            HangIfBelowHp = -10,
        };
        using Harness h = new(s);
        h.SetPrompt(hp: -5, maxHp: 200);

        Assert.DoesNotContain("=x", h.SentLines);
    }

    [Fact]
    public void Hangup_ValueMode_ZeroTrigger_FiresAtZero()
    {
        // 0 is a live "hang the moment I drop" trigger, not a disable — the same
        // in Value mode as Percentage.
        HealthSettings s = new()
        {
            HpThresholdMode = ThresholdMode.Absolute,
            HangIfBelowHp = 0,
        };
        using Harness h = new(s);
        h.SetPrompt(hp: 0, maxHp: 200);

        Assert.Contains("=x", h.SentLines);
    }

    [Fact]
    public void Hangup_ValueMode_TriggerAtDeathFloor_Disabled()
    {
        // Sliding the trigger to the death floor collapses the fire window (empty),
        // the natural "never hang up" position — a bleeding-out char won't drop.
        HealthSettings s = new()
        {
            HpThresholdMode = ThresholdMode.Absolute,
            HangIfBelowHp = -25,   // == default death floor
        };
        using Harness h = new(s);
        h.SetPrompt(hp: -10, maxHp: 200);

        Assert.DoesNotContain("=x", h.SentLines);
    }

    [Fact]
    public void Hangup_Fires_ShortCircuitsRest()
    {
        // Once we've committed to disconnecting there's no point resting — the
        // hangup returns early from Evaluate before the rest-out branch.
        using Harness h = new();
        h.SetPrompt(hp: 5, maxHp: 200);   // below both hang- and rest-trigger

        Assert.Contains("=x", h.SentLines);
        Assert.DoesNotContain("rest", h.SentLines);
    }

    [Fact]
    public void Hangup_NoExitCommand_FallsBackToRest()
    {
        // Couldn't-send (no exit command configured) latches the single-shot
        // but doesn't short-circuit — normal recovery still runs as a fallback.
        using Harness h = new() { HangupCommand = null };
        h.SetPrompt(hp: 5, maxHp: 200);

        Assert.DoesNotContain("=x", h.SentLines);
        Assert.Contains("rest", h.SentLines);
    }

    [Fact]
    public void Hangup_DroppedInAllOffMode_StillHangs()
    {
        // The all-off carve-out honours the bleeding-out window too, so an AFK
        // character that dropped with the master switch off still gets its escape.
        using Harness h = new();
        h.MasterSwitchOff = true;
        h.General = new Models.Profile.GeneralSettings { AllowHangupInAllOffMode = true };
        h.SetPrompt(hp: -10, maxHp: 200);

        Assert.Contains("=x", h.SentLines);
    }

    // ----- all-off-mode hangup carve-out ----------------------------

    [Fact]
    public void AllOff_HangupAllowed_HpBelowTrigger_StillHangs()
    {
        // Master switch off but the opt-in keeps the emergency hangup live.
        using Harness h = new();
        h.MasterSwitchOff = true;
        h.General = new Models.Profile.GeneralSettings { AllowHangupInAllOffMode = true };
        h.SetPrompt(hp: 5, maxHp: 200);   // 2.5% — below default 5% hang threshold

        Assert.Contains("=x", h.SentLines);
    }

    [Fact]
    public void AllOff_HangupNotAllowed_HpBelowTrigger_NoHang()
    {
        // Master switch off and carve-out off (default) — fully dormant.
        using Harness h = new();
        h.MasterSwitchOff = true;
        // h.General left at default (AllowHangupInAllOffMode = false)
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 5;

        Assert.DoesNotContain("=x", h.SentLines);
        Assert.NotEmpty(h.HeldByMasterSwitch);
    }

    // The hang-up matrix (user, 2026-10-09). Master switch off: every automatic
    // hang-up fires only with Allow hangup in all-off mode. Master switch on: the
    // low-HP hang-up sits behind Auto-Rest (Auto-Heal alone no longer carries it)
    // and the others need no toggle. Disable Hangups stops them all.
    public static IEnumerable<object[]> HangupMatrix()
    {
        foreach (bool masterOff in new[] { false, true })
        foreach (bool allow in new[] { false, true })
        foreach (bool disabled in new[] { false, true })
        foreach (bool rest in new[] { false, true })
            yield return new object[] { masterOff, allow, disabled, rest };
    }

    private static Harness MatrixHarness(bool masterOff, bool allow, bool disabled, bool rest)
    {
        Harness h = new() { MasterSwitchOff = masterOff, RestEnabled = rest };
        h.General = new Models.Profile.GeneralSettings
        {
            AllowHangupInAllOffMode = allow,
            DisableHangups = disabled,
        };
        return h;
    }

    [Theory]
    [MemberData(nameof(HangupMatrix))]
    public void HangupMatrix_LowHp(bool masterOff, bool allow, bool disabled, bool rest)
    {
        using Harness h = MatrixHarness(masterOff, allow, disabled, rest);
        h.SetPrompt(hp: 5, maxHp: 200);

        bool expected = !disabled && (masterOff ? allow : rest);
        Assert.Equal(expected, h.SentLines.Contains("=x"));
    }

    [Theory]
    [MemberData(nameof(HangupMatrix))]
    public void HangupMatrix_ReceivedPanic(bool masterOff, bool allow, bool disabled, bool rest)
    {
        using Harness h = MatrixHarness(masterOff, allow, disabled, rest);
        h.SetPrompt(hp: 200, maxHp: 200);

        bool acted = h.Health.RespondToReceivedPanic("Tank");

        bool expected = !disabled && (!masterOff || allow);
        Assert.Equal(expected, acted);
        Assert.Equal(expected, h.SentLines.Contains("=x"));
    }

    [Theory]
    [MemberData(nameof(HangupMatrix))]
    public void HangupMatrix_Pvp(bool masterOff, bool allow, bool disabled, bool rest)
    {
        using Harness h = MatrixHarness(masterOff, allow, disabled, rest);
        h.SetPrompt(hp: 200, maxHp: 200);

        bool acted = h.Health.HangUpForPvp("Raider is here");

        bool expected = !disabled && (!masterOff || allow);
        Assert.Equal(expected, acted);
        Assert.Equal(expected, h.SentLines.Contains("=x"));
    }

    [Theory]
    [MemberData(nameof(HangupMatrix))]
    public void HangupMatrix_HangupMonster(bool masterOff, bool allow, bool disabled, bool rest)
    {
        using Harness h = MatrixHarness(masterOff, allow, disabled, rest);
        h.SetPrompt(hp: 200, maxHp: 200);

        EscapeOutcome outcome = h.Health.HangUpForMonster("ogre (#7) is here, relationship Hangup");

        EscapeOutcome expected = disabled ? EscapeOutcome.HangupsDisabled
            : masterOff && !allow ? EscapeOutcome.AllOff
            : EscapeOutcome.HungUp;
        Assert.Equal(expected, outcome);
        Assert.Equal(expected == EscapeOutcome.HungUp, h.SentLines.Contains("=x"));
    }

    // With the master switch off and no opt-in, a received @panic or a PvP
    // hang-up takes no wimpy jump either: nothing of ours responds.
    [Fact]
    public void MasterSwitchOff_NoOptIn_NoWimpyJumpForPanicOrPvp()
    {
        HealthSettings s = new()
        {
            SysGotoWimpyInsteadOfHanging = true,
            SysGotoWimpyLocation = "wimpy-room",
        };
        using Harness h = new(s) { WimpyFireResult = true, MasterSwitchOff = true };
        h.SetPrompt(hp: 200, maxHp: 200);

        Assert.False(h.Health.RespondToReceivedPanic("Tank"));
        Assert.False(h.Health.HangUpForPvp("Raider is here"));

        Assert.Null(h.WimpyFiredWith);
        Assert.Equal(2, h.HeldByMasterSwitch.Count);
    }

    // A held low-HP hang-up is not latched: it goes out as soon as the switch
    // that held it allows.
    [Fact]
    public void LowHpHangup_HeldByMasterSwitch_FiresWhenSwitchedBackOn()
    {
        using Harness h = new() { MasterSwitchOff = true };
        h.SetPrompt(hp: 5, maxHp: 200);
        Assert.DoesNotContain("=x", h.SentLines);

        h.MasterSwitchOff = false;
        h.Health.Evaluate();

        Assert.Contains("=x", h.SentLines);
    }

    [Fact]
    public void AllOff_HangupAllowed_AboveThreshold_NoHang()
    {
        // Carve-out on but HP healthy — no spurious hangup.
        using Harness h = new();
        h.MasterSwitchOff = true;
        h.General = new Models.Profile.GeneralSettings { AllowHangupInAllOffMode = true };
        h.SetPrompt(hp: 50, maxHp: 200);   // 25% — above 5% hang threshold

        Assert.DoesNotContain("=x", h.SentLines);
    }

    // ----- master "Disable hangups" kill-switch ---------------------

    [Fact]
    public void DisableHangups_HpBelowTrigger_NoHang()
    {
        // Engine live, HP well below the hang threshold, but the master
        // kill-switch silences the emergency hangup.
        using Harness h = new();
        h.General = new Models.Profile.GeneralSettings { DisableHangups = true };
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 5;        // 2.5% — would normally hang

        Assert.DoesNotContain("=x", h.SentLines);
    }

    [Fact]
    public void DisableHangups_OverridesAllowHangupInAllOffMode()
    {
        // Both the all-off carve-out AND the master kill-switch are set —
        // DisableHangups wins, so a character with the master switch off at
        // lethal HP still won't auto-disconnect.
        using Harness h = new();
        h.MasterSwitchOff = true;
        h.General = new Models.Profile.GeneralSettings
        {
            AllowHangupInAllOffMode = true,
            DisableHangups = true,
        };
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 5;

        Assert.DoesNotContain("=x", h.SentLines);
    }

    // ----- hostile-aware emergency-hangup gate ----------------------
    // A low-HP disconnect is an escape from a fight; with no hostile in the room
    // there's nothing to flee, so a wounded-but-safe character stays connected and
    // rests instead of looping through reconnect → hang up → reconnect. The latch
    // re-arms when the danger passes so a fresh hostile fires anew.

    [Fact]
    public void Hangup_NoHostileInRoom_HoldsConnection()
    {
        // Below the trigger but the room is clear — no disconnect.
        using Harness h = new() { HostileInRoom = false };
        h.SetPrompt(hp: 5, maxHp: 200);   // 2.5% — below the 5% hang trigger

        Assert.DoesNotContain("=x", h.SentLines);
    }

    [Fact]
    public void Hangup_HostileArrivesWhileLow_FiresViaRoomRecheck()
    {
        // Sitting below the trigger in a clear room; a hostile then wanders in.
        // The room-observation re-check (not a prompt change) fires the escape.
        using Harness h = new() { HostileInRoom = false };
        h.SetPrompt(hp: 5, maxHp: 200);
        Assert.DoesNotContain("=x", h.SentLines);

        h.HostileInRoom = true;
        h.Health.ReevaluateEmergencyHangup();

        Assert.Contains("=x", h.SentLines);
    }

    [Fact]
    public void Hangup_RearmsAfterRecovery_FiresOnFreshCrossing()
    {
        // Low + hostile fires once; HP recovers above the trigger (re-arm); a
        // second drop with a hostile present fires a fresh disconnect.
        using Harness h = new();   // HostileInRoom defaults true
        h.SetPrompt(hp: 5, maxHp: 200);
        Assert.Equal(1, h.SentLines.Count(l => l == "=x"));

        h.State.Hp = 180;          // recovered above the trigger → re-arm
        h.Clock += TimeSpan.FromSeconds(30);   // recovering took longer than a moment
        h.State.Hp = 5;            // dropped again with a hostile present
        Assert.Equal(2, h.SentLines.Count(l => l == "=x"));
    }

    [Fact]
    public void Hangup_ReconnectIntoSafeRoom_ThenHostile_FiresAgain()
    {
        // The exact reported scenario. First episode fires and drops the carrier;
        // we reconnect still below the trigger but into a clear room — the latch
        // re-arms without firing — and only re-fires once a hostile appears.
        using Harness h = new();
        h.SetPrompt(hp: 5, maxHp: 200);
        Assert.Equal(1, h.SentLines.Count(l => l == "=x"));

        h.HostileInRoom = false;              // reconnected into a clear room
        h.Health.ReevaluateEmergencyHangup();
        Assert.Equal(1, h.SentLines.Count(l => l == "=x"));   // re-armed, no fire

        h.Clock += TimeSpan.FromSeconds(30);  // the reconnect took longer than a moment
        h.HostileInRoom = true;               // a hostile wanders in
        h.Health.ReevaluateEmergencyHangup();
        Assert.Equal(2, h.SentLines.Count(l => l == "=x"));
    }

    [Fact]
    public void RoomRecheck_EngineOff_NoCarveOut_DoesNotHang()
    {
        // The room re-check honours the same engine-off gate as Evaluate: auto-heal
        // off and no all-off carve-out means a hostile arrival can't sneak the
        // hangup past the disabled engine.
        using Harness h = new();
        h.AutoHealRestEnabled = false;   // engine off; General default → carve-out off
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 5;                  // below trigger, but engine off → no fire
        Assert.DoesNotContain("=x", h.SentLines);

        h.Health.ReevaluateEmergencyHangup();   // hostile present (default), engine off
        Assert.DoesNotContain("=x", h.SentLines);
    }

    // ----- party-role-aware recovery ---------------------------------

    [Fact]
    public void Follower_RecoversToFloorPlusOne_NotRestMax()
    {
        // Default trigger 60% of 200 = 120; default rest-max 95% = 190.
        // As a follower the gate clears the moment HP climbs one past the
        // floor (121), well before rest-max.
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => { });
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        Assert.True(h.HealthGateHeld);

        h.State.Hp = 121;            // one past the 120 floor
        Assert.False(h.HealthGateHeld);
    }

    [Fact]
    public void Follower_AtFloor_GateStillHeld()
    {
        // Exactly at the floor is still "below or equal" — gate holds
        // until strictly above.
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => { });
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        Assert.True(h.HealthGateHeld);

        h.State.Hp = 120;            // at floor, not past it
        Assert.True(h.HealthGateHeld);
    }

    [Fact]
    public void Leader_RecoversToRestMax_NotFloor()
    {
        // Same wiring but role selector says leader → full topoff target.
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => false,
            requestPartyWait: () => { },
            requestPartyOk: () => { });
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        Assert.True(h.HealthGateHeld);

        h.State.Hp = 121;            // above floor but below 190 target
        Assert.True(h.HealthGateHeld);

        h.State.Hp = 190;            // rest-max
        Assert.False(h.HealthGateHeld);
    }

    [Fact]
    public void Follower_GateAssert_RequestsWait_RestMax_RequestsOk()
    {
        int waits = 0, oks = 0;
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => waits++,
            requestPartyOk: () => oks++);
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;

        h.State.Hp = 50;             // below floor → @wait
        Assert.Equal(1, waits);
        Assert.Equal(0, oks);

        h.State.Hp = 121;            // trigger+1: movement gate clears, still below rest-max
        Assert.Equal(1, waits);
        Assert.Equal(0, oks);        // leader NOT released yet (report 222618)

        h.State.Hp = 190;            // rest-max ceiling → @ok
        h.SettleOk();
        Assert.Equal(1, waits);
        Assert.Equal(1, oks);
    }

    [Fact]
    public void Follower_WaitOk_FireOncePerCycle_NoSpam()
    {
        int waits = 0, oks = 0;
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => waits++,
            requestPartyOk: () => oks++);
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;

        h.State.Hp = 50;             // @wait
        h.State.Hp = 40;             // still below — no second @wait
        h.State.Hp = 30;
        Assert.Equal(1, waits);

        h.State.Hp = 121;            // trigger+1: movement gate clears, not yet rested
        Assert.Equal(0, oks);        // @ok held until rest-max (report 222618)

        h.State.Hp = 190;            // rest-max → @ok
        h.SettleOk();
        h.State.Hp = 195;            // still above — no second @ok
        Assert.Equal(1, oks);
    }

    [Fact]
    public void Follower_FreshDropWhileStillSignaled_ReAsksForWait()
    {
        // The @ok waits for full rest-max, so the signal stays latched across a
        // second drop — but the leader may have given up on the first wait by then
        // (its "If leading, wait only" window). A fresh drop re-asks (report
        // paradigm-20260925-210928).
        int waits = 0;
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => waits++,
            requestPartyOk: () => { });
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;

        h.State.Hp = 50;             // below floor → @wait
        h.State.Hp = 121;            // trigger+1: gate clears, still signaled (not at rest-max)
        h.Clock += TimeSpan.FromMinutes(1);
        h.State.Hp = 60;             // dropped again → fresh drop re-asks
        Assert.Equal(2, waits);
    }

    // Report paradigm-20260929-233636: HP bouncing across the floor within a second
    // is one dip — the re-ask waits out the resend interval.
    [Fact]
    public void Follower_ReDropWithinSeconds_DoesNotReAsk()
    {
        int waits = 0;
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => waits++,
            requestPartyOk: () => { });
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;

        h.State.Hp = 50;             // @wait
        h.State.Hp = 121;            // gate clears
        h.State.Hp = 60;             // straight back under
        Assert.Equal(1, waits);
    }

    [Fact]
    public void Follower_HpDropsWhileManaGateHeld_ReAsksForWait()
    {
        // The report's shape: the mana gate held for minutes, then HP dropped too —
        // a fresh drop of the OTHER pool is still a new reason to stop the leader.
        int waits = 0;
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => waits++,
            requestPartyOk: () => { });
        h.SetPrompt(hp: 100, maxHp: 100, ma: 100, maxMa: 100);

        h.State.Ma = 20;             // mana floor → @wait
        Assert.True(h.ManaGateHeld);
        h.Clock += TimeSpan.FromMinutes(1);
        h.State.Hp = 30;             // HP floor while mana still held → re-ask
        Assert.Equal(2, waits);
    }

    [Fact]
    public void Follower_DraggedWhileRecovering_ReAsksOncePerInterval()
    {
        int waits = 0;
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => waits++,
            requestPartyOk: () => { });
        h.SetPrompt(hp: 100, maxHp: 100, ma: 100, maxMa: 100);
        h.State.Ma = 20;             // mana floor → @wait
        Assert.Equal(1, waits);

        h.Clock += TimeSpan.FromSeconds(10);
        h.Moved();  // the leader walked us on — it isn't waiting
        Assert.Equal(2, waits);
        h.Moved();  // same drag, next room — rate-limited
        Assert.Equal(2, waits);

        h.Clock += TimeSpan.FromSeconds(10);
        h.Moved();
        Assert.Equal(3, waits);
    }

    [Fact]
    public void Solo_Moving_NeverReAsksForWait()
    {
        int waits = 0;
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => false,
            requestPartyWait: () => waits++,
            requestPartyOk: () => { });
        h.SetPrompt(hp: 100, maxHp: 100, ma: 100, maxMa: 100);
        h.State.Ma = 20;
        int afterDrop = waits;

        h.Clock += TimeSpan.FromSeconds(10);
        h.Moved();
        Assert.Equal(afterDrop, waits);
    }

    [Fact]
    public void Follower_BasisWobbleMidGearSwap_NeverOksWhileTheGateHolds()
    {
        // report paradigm-20260926-121252: MA 246 under a 64 % trigger (247 of 386).
        // A Pre-rest Mana set streaming on moved the live max (386 → 421) and, for a
        // moment, the Default-set basis — rest-max dipped under 246 and read "rested"
        // → @ok, then the next prompt re-sent @wait, flapping 3-4× a second.
        int waits = 0, oks = 0;
        int defaultMa = 386;
        using Harness h = new();
        h.Settings.RestIfBelowMa = 64;
        h.Settings.RestMaxMa = 65;
        h.Health.SetRestPoolMaxProviders(null, () => defaultMa, null, null);
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => waits++,
            requestPartyOk: () => oks++);
        h.SetPrompt(hp: 100, maxHp: 100, ma: 386, maxMa: 386);

        h.State.Ma = 246;            // below 247 → gate + @wait
        Assert.True(h.ManaGateHeld);
        Assert.Equal(1, waits);

        defaultMa = 370;             // mid-swap wobble: rest-max 65 % of 370 = 241 ≤ 246
        h.State.MaxMa = 421;         // the Pre-rest set's bigger pool lands
        h.Health.Evaluate();
        defaultMa = 386;             // swap settles
        h.Health.Evaluate();

        Assert.Equal(0, oks);        // never released while still below the floor
        Assert.Equal(1, waits);      // so no re-@wait either — no flap
    }

    [Fact]
    public void Follower_DisabledMidRecovery_ReleasesOk()
    {
        int oks = 0;
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => oks++);
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;             // @wait sent
        Assert.Equal(0, oks);

        h.AutoHealRestEnabled = false;
        h.Health.Evaluate();         // engine toggled off mid-recovery
        Assert.Equal(1, oks);        // leader released, not left hanging
    }

    // ----- opportunistic follower rest (leader resting) ------------

    [Fact]
    public void Opportunistic_LeaderResting_RestsAboveOwnTrigger()
    {
        // Follower, leader resting, HP above our own 60% rest-trigger
        // (no gate) but below the 95% rest-max → we ride the downtime and
        // top off with `rest` (UseMeditateAbility default off).
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            isLeaderResting: () => true);
        h.SetPrompt(hp: 150, maxHp: 200);   // 75% — above trigger, below rest-max

        Assert.False(h.HealthGateHeld);     // no floor breach → no gate
        Assert.Contains("rest", h.SentLines);
    }

    [Fact]
    public void Opportunistic_LeaderNotResting_DoesNotRest()
    {
        // Same vitals, but the leader is up and moving → no downtime to
        // exploit, so a follower above its own floor stays standing.
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            isLeaderResting: () => false);
        h.SetPrompt(hp: 150, maxHp: 200);

        Assert.DoesNotContain("rest", h.SentLines);
        Assert.DoesNotContain("meditate", h.SentLines);
    }

    [Fact]
    public void Opportunistic_AlreadyAtRestMax_DoesNotRest()
    {
        // Nothing to top off (both pools at/above rest-max) → no rest even
        // with the leader resting.
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            isLeaderResting: () => true);
        h.SetPrompt(hp: 200, maxHp: 200);   // full HP, no mana pool

        Assert.DoesNotContain("rest", h.SentLines);
        Assert.DoesNotContain("meditate", h.SentLines);
    }

    [Fact]
    public void Opportunistic_MeditatePrefersWhenManaLowerPct()
    {
        // UseMeditateAbility on, no gate asserted: pick by live fill —
        // MA% (50) < HP% (85) → meditate the more-depleted pool first.
        HealthSettings s = new() { UseMeditateAbility = true };
        using Harness h = new(s);
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            isLeaderResting: () => true);
        h.SetPrompt(hp: 170, maxHp: 200, ma: 50, maxMa: 100); // 85% HP, 50% MA

        Assert.Contains("meditate", h.SentLines);
        Assert.DoesNotContain("rest", h.SentLines);
    }

    [Fact]
    public void Opportunistic_RestsWhenHpLowerPct()
    {
        // UseMeditateAbility on but HP% (65) < MA% (90) → rest the more-
        // depleted HP pool.
        HealthSettings s = new() { UseMeditateAbility = true };
        using Harness h = new(s);
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            isLeaderResting: () => true);
        h.SetPrompt(hp: 130, maxHp: 200, ma: 90, maxMa: 100); // 65% HP, 90% MA

        Assert.Contains("rest", h.SentLines);
        Assert.DoesNotContain("meditate", h.SentLines);
    }

    [Fact]
    public void Opportunistic_MeditateBeforeResting_OverridesPct()
    {
        // MeditateBeforeResting + any mana missing → meditate even though
        // HP% (65) is the lower pool (would otherwise rest).
        HealthSettings s = new() { UseMeditateAbility = true, MeditateBeforeResting = true };
        using Harness h = new(s);
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            isLeaderResting: () => true);
        h.SetPrompt(hp: 130, maxHp: 200, ma: 80, maxMa: 100); // 65% HP, 80% MA

        Assert.Contains("meditate", h.SentLines);
        Assert.DoesNotContain("rest", h.SentLines);
    }

    [Fact]
    public void Opportunistic_InCombat_DoesNotRest()
    {
        // Leader resting + below rest-max, but we're mid-combat → never
        // rest (same guard as the gated path).
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            isLeaderResting: () => true);
        h.State.InCombat = true;
        h.SetPrompt(hp: 150, maxHp: 200);

        Assert.DoesNotContain("rest", h.SentLines);
    }

    [Fact]
    public void Opportunistic_HostilesPresent_DoesNotRest()
    {
        // Engageable mob in the room breaks rest every round → don't even
        // try, just like the gated rest path.
        using Harness h = new();
        h.HostilesPresent = true;
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            isLeaderResting: () => true);
        h.SetPrompt(hp: 150, maxHp: 200);

        Assert.DoesNotContain("rest", h.SentLines);
    }

    [Fact]
    public void Opportunistic_DoesNotRequestPartyWait()
    {
        // Riding the leader's voluntary rest must NOT @wait — they're
        // already halted, and we're above our floor (no gate).
        int waits = 0;
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => waits++,
            requestPartyOk: () => { },
            isLeaderResting: () => true);
        h.SetPrompt(hp: 150, maxHp: 200);

        Assert.Contains("rest", h.SentLines);   // we did opportunistically rest
        Assert.Equal(0, waits);                 // but never pinged the leader
    }

    [Fact]
    public void Opportunistic_LeaderStandsUp_FiresPostRestChain()
    {
        // We rested in the downtime; the leader rising flips the selector
        // false → the shared recovery branch runs the post-rest chain and
        // clears the latch.
        bool leaderResting = true;
        HealthSettings s = new() { PostRestCommand = "stand" };
        using Harness h = new(s);
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            isLeaderResting: () => leaderResting);
        h.SetPrompt(hp: 150, maxHp: 200);
        Assert.Contains("rest", h.SentLines);
        Assert.True(h.Health.RestInFlight);

        leaderResting = false;
        h.Health.Evaluate();                // leader stood — re-evaluate
        Assert.Contains("stand", h.SentLines);
        Assert.False(h.Health.RestInFlight);
    }

    // ----- leader-waited rest + poison gate ------------------------

    [Fact]
    public void LeaderWaited_NotPoisoned_RestsAboveOwnTrigger()
    {
        // WE lead and a member has @wait-held us; not poisoned, HP above our
        // own floor but below rest-max → use the forced downtime to top off.
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => false,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            isLeaderWaited: () => true,
            isSelfPoisoned: () => false);
        h.SetPrompt(hp: 150, maxHp: 200);   // 75% — above trigger, below rest-max

        Assert.False(h.HealthGateHeld);     // no floor breach → no gate
        Assert.Contains("rest", h.SentLines);
    }

    [Fact]
    public void LeaderWaited_Poisoned_DoesNotRest()
    {
        // Same held state, but poisoned → skip the downtime rest (poison ticks
        // would just keep breaking it).
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => false,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            isLeaderWaited: () => true,
            isSelfPoisoned: () => true);
        h.SetPrompt(hp: 150, maxHp: 200);

        Assert.DoesNotContain("rest", h.SentLines);
        Assert.DoesNotContain("meditate", h.SentLines);
    }

    [Fact]
    public void LeaderNotWaited_DoesNotRest()
    {
        // Not held → no forced downtime, so a leader above its floor stays up.
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => false,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            isLeaderWaited: () => false,
            isSelfPoisoned: () => false);
        h.SetPrompt(hp: 150, maxHp: 200);

        Assert.DoesNotContain("rest", h.SentLines);
    }

    [Fact]
    public void LeaderWaited_AboveRestMax_StillRestsTowardFull()
    {
        // Report -132906: @wait-held leader already above its rest-max floor
        // (HP 98% / MA 90%) but not full. The old rest-max ceiling left it standing
        // idle through the whole wait; a wait is bounded downtime, so it now rests
        // toward FULL (the wait's own release ends it, not the rest-max floor).
        HealthSettings s = new()
        {
            HpThresholdMode = ThresholdMode.Percentage, RestIfBelowHp = 30, RestMaxHp = 70,
            MaThresholdMode = ThresholdMode.Percentage, RestIfBelowMa = 30, RestMaxMa = 50,
            UseMeditateAbility = false,   // deterministic: rest, not meditate
        };
        using Harness h = new(s);
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => false,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            isLeaderWaited: () => true,
            isSelfPoisoned: () => false);
        h.SetPrompt(hp: 187, maxHp: 189, ma: 139, maxMa: 154);   // above rest-max, below full

        Assert.False(h.HealthGateHeld);   // no floor breach → no gate
        Assert.Contains("rest", h.SentLines);
    }

    [Fact]
    public void LeaderWaited_AtFull_DoesNotRest()
    {
        // Nothing to recover at full → no rest even while @wait-held (the downtime
        // top-off only fires while a pool is short of Max).
        HealthSettings s = new()
        {
            HpThresholdMode = ThresholdMode.Percentage, RestIfBelowHp = 30, RestMaxHp = 70,
            MaThresholdMode = ThresholdMode.Percentage, RestIfBelowMa = 30, RestMaxMa = 50,
            UseMeditateAbility = false,
        };
        using Harness h = new(s);
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => false,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            isLeaderWaited: () => true,
            isSelfPoisoned: () => false);
        h.SetPrompt(hp: 189, maxHp: 189, ma: 154, maxMa: 154);   // full

        Assert.DoesNotContain("rest", h.SentLines);
        Assert.DoesNotContain("meditate", h.SentLines);
    }

    [Fact]
    public void LeaderWaited_AtRestMax_DoesNotRest()
    {
        // Held but already topped off → nothing to recover, so no rest.
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => false,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            isLeaderWaited: () => true,
            isSelfPoisoned: () => false);
        h.SetPrompt(hp: 200, maxHp: 200);

        Assert.DoesNotContain("rest", h.SentLines);
        Assert.DoesNotContain("meditate", h.SentLines);
    }

    [Fact]
    public void Opportunistic_Poisoned_DoesNotRest()
    {
        // Follower mirroring the leader's rest, but poisoned → the poison gate
        // blocks the downtime rest.
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            isLeaderResting: () => true,
            isSelfPoisoned: () => true);
        h.SetPrompt(hp: 150, maxHp: 200);

        Assert.DoesNotContain("rest", h.SentLines);
        Assert.DoesNotContain("meditate", h.SentLines);
    }

    [Fact]
    public void Opportunistic_PoisonSelectorFalse_StillRests()
    {
        // Poison selector wired but reporting false → the follower still rides
        // the leader's downtime (guards against the gate being inverted).
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            isLeaderResting: () => true,
            isSelfPoisoned: () => false);
        h.SetPrompt(hp: 150, maxHp: 200);

        Assert.Contains("rest", h.SentLines);
    }

    [Fact]
    public void Poisoned_BelowFloor_StillRestsThroughGate()
    {
        // The poison gate only blocks the downtime paths — a poisoned character
        // below its own HP floor still rests through the normal gated branch.
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            isLeaderResting: () => false,
            isSelfPoisoned: () => true);
        h.SetPrompt(hp: 50, maxHp: 200);    // 25% — below the 60% rest floor

        Assert.True(h.HealthGateHeld);
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

    // ----- a room whose own spell does damage (user, 2026-10-09) -----
    // "we should heal but not actively try to rest in a room like this": the game
    // re-casts the room's spell every six seconds and each hit breaks the rest.

    private const string MagmaHeat = "magma heat (#526)";

    private static List<string> InfoLines(Harness h)
    {
        List<string> lines = new();
        h.Log.EntryAdded += e => { if (e.Severity == LogSeverity.Info) lines.Add(e.Message); };
        return lines;
    }

    [Fact]
    public void DamagingRoom_RestDue_NoHoldNoRest_AndSaidOncePerRoom()
    {
        using Harness h = new();
        string? hurting = MagmaHeat;
        h.Health.SetRoomSpellDamageProbe(() => hurting);
        List<string> info = InfoLines(h);

        h.SetPrompt(hp: 30, maxHp: 100, ma: 10, maxMa: 100);   // both under their rest triggers
        h.Health.Evaluate();

        Assert.False(h.HealthGateHeld);                         // movement isn't held: the walk carries on out
        Assert.False(h.ManaGateHeld);
        Assert.Empty(h.SentLines);                              // no rest, no meditate
        Assert.Equal(MagmaHeat, h.Health.RestDeferredByRoomSpell);
        Assert.True(h.Health.HpRestDeferredByRoomSpell);

        // Standing idle while the room keeps hitting: every prompt asks again, and
        // nothing is tried, broken and tried again.
        for (int hp = 29; hp > 20; hp--)
        {
            h.State.Hp = hp;
            h.Health.Evaluate();
        }
        Assert.Empty(h.SentLines);
        Assert.Single(info, l => l.Contains("does damage — not resting here"));
    }

    [Fact]
    public void DamagingRoom_TheRestStartsInTheNextRoomThatDoesNotHurt()
    {
        using Harness h = new();
        string? hurting = MagmaHeat;
        h.Health.SetRoomSpellDamageProbe(() => hurting);
        List<string> info = InfoLines(h);
        h.SetPrompt(hp: 30, maxHp: 100);
        h.Health.Evaluate();
        Assert.False(h.HealthGateHeld);

        // Another damaging room: still nothing, and it says so for this room too.
        h.Moved();
        Assert.False(h.HealthGateHeld);
        Assert.Equal(2, info.Count(l => l.Contains("does damage — not resting here")));

        // Out of the heat. Only the room changed; no prompt did.
        hurting = null;
        h.Moved();

        Assert.True(h.HealthGateHeld);
        Assert.Contains("rest", h.SentLines);
        Assert.Null(h.Health.RestDeferredByRoomSpell);
        Assert.Single(info, l => l.Contains("the rest put off in a damaging room starts here"));
    }

    [Fact]
    public void DamagingRoom_HealedBackAboveTheTrigger_NothingIsOwed()
    {
        using Harness h = new();
        string? hurting = MagmaHeat;
        h.Health.SetRoomSpellDamageProbe(() => hurting);
        List<string> info = InfoLines(h);
        h.SetPrompt(hp: 30, maxHp: 100);
        h.Health.Evaluate();

        h.State.Hp = 90;                                        // the heals kept up
        h.Health.Evaluate();
        Assert.Null(h.Health.RestDeferredByRoomSpell);
        Assert.False(h.Health.HpRestDeferredByRoomSpell);

        hurting = null;
        h.Moved();
        Assert.False(h.HealthGateHeld);
        Assert.DoesNotContain(info, l => l.Contains("starts here"));
    }

    [Fact]
    public void DamagingRoom_ReleasesARestAlreadyHeld()
    {
        // Resting in the volcano with the feather on; the feather comes off.
        using Harness h = new();
        string? hurting = null;
        h.Health.SetRoomSpellDamageProbe(() => hurting);
        h.SetPrompt(hp: 30, maxHp: 100);
        h.Health.Evaluate();
        Assert.True(h.HealthGateHeld);

        hurting = MagmaHeat;
        h.Health.Evaluate();
        Assert.False(h.HealthGateHeld);
    }

    [Fact]
    public void DamagingRoom_NoDowntimeRestEither()
    {
        // The leader lies down in the volcano: a follower above its own floor would
        // top off beside it anywhere else.
        using Harness h = new();
        h.Health.SetRoomSpellDamageProbe(() => MagmaHeat);
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => { },
            isLeaderResting: () => true);
        List<string> info = InfoLines(h);

        h.SetPrompt(hp: 150, maxHp: 200);

        Assert.Empty(h.SentLines);
        Assert.Single(info, l => l.Contains("the leader is resting, but this room's magma heat (#526) does damage"));
    }

    [Fact]
    public void DamagingRoom_WinsOverRestUpHere()
    {
        using Harness h = new();
        h.Health.SetRoomSpellDamageProbe(() => MagmaHeat);
        h.RestHere = (true, true);
        h.SetPrompt(hp: 80, maxHp: 100, ma: 80, maxMa: 100);

        Assert.False(h.Health.HoldForRestHere());
    }

    [Fact]
    public void DamagingRoom_FollowerSendsNoWait()
    {
        // No hold means no @wait: the follower goes on with the leader and asks in
        // the next room that doesn't hurt.
        using Harness h = new();
        string? hurting = MagmaHeat;
        int waits = 0;
        h.Health.SetRoomSpellDamageProbe(() => hurting);
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => waits++,
            requestPartyOk: () => { },
            isSelfPoisoned: () => false);
        h.SetPrompt(hp: 30, maxHp: 100);
        h.Health.Evaluate();
        Assert.Equal(0, waits);

        hurting = null;
        h.Moved();
        Assert.Equal(1, waits);
    }

    [Theory]
    [InlineData(false, false)]   // Auto-Rest off
    [InlineData(true, true)]     // Sprint mode / a loop's do-not-rest room
    public void DamagingRoom_NoRestWasComingAnyway_NothingIsDeferred(bool restEnabled, bool skipRestHere)
    {
        // "Deferred for the room" turns the rest-time heal on while standing. With
        // resting ruled out by the user's own settings there is no rest to stand in for.
        using Harness h = new();
        h.RestEnabled = restEnabled;
        h.SkipRestHere = skipRestHere;
        h.Health.SetRoomSpellDamageProbe(() => MagmaHeat);
        h.SetPrompt(hp: 30, maxHp: 100);
        h.Health.Evaluate();

        Assert.Null(h.Health.RestDeferredByRoomSpell);
        Assert.False(h.Health.HpRestDeferredByRoomSpell);
    }

    [Fact]
    public void DamagingRoom_ManaOnly_NoMeditateEither()
    {
        // Room damage breaks a meditation as it does a rest (user, 2026-10-10).
        using Harness h = new();
        h.Health.SetRoomSpellDamageProbe(() => MagmaHeat);
        h.SetPrompt(hp: 100, maxHp: 100, ma: 10, maxMa: 100);
        h.Health.Evaluate();

        Assert.False(h.ManaGateHeld);
        Assert.Empty(h.SentLines);
        Assert.Equal(MagmaHeat, h.Health.RestDeferredByRoomSpell);
        Assert.False(h.Health.HpRestDeferredByRoomSpell);   // no HP rest is owed, so no standing rest-time heal
    }

    [Fact]
    public void DamagingRoom_SpellUntickedWhileStandingThere_RestsAtTheNextCheck()
    {
        // Settings → Periodic Damage Room Spells: the probe answers from the saved
        // choice each time, so unticking the spell needs no room change.
        using Harness h = new();
        string? hurting = MagmaHeat;
        h.Health.SetRoomSpellDamageProbe(() => hurting);
        h.SetPrompt(hp: 30, maxHp: 100);
        h.Health.Evaluate();
        Assert.Empty(h.SentLines);

        hurting = null;
        h.Health.Evaluate();

        Assert.True(h.HealthGateHeld);
        Assert.Contains("rest", h.SentLines);
        Assert.Null(h.Health.RestDeferredByRoomSpell);
    }

    [Fact]
    public void DamagingRoom_FollowerDraggedInWithAWaitOut_ReleasesIt_AndAsksAgainInTheNextRoom()
    {
        // The deficit starts in an ordinary room: gate up, @wait out, resting. The
        // leader's wait window runs out and it walks on into the volcano with the
        // follower in tow. "A follower keeps following" (user, 2026-10-10): no second
        // @wait from inside the heat, and the one that is out is released.
        using Harness h = new();
        string? hurting = null;
        int waits = 0, oks = 0;
        h.Health.SetRoomSpellDamageProbe(() => hurting);
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => waits++,
            requestPartyOk: () => oks++,
            isSelfPoisoned: () => false);
        List<string> info = InfoLines(h);
        h.SetPrompt(hp: 30, maxHp: 100);
        h.Health.Evaluate();
        Assert.True(h.HealthGateHeld);
        Assert.Equal((1, 0), (waits, oks));
        int rests = h.SentLines.Count(l => l == "rest");
        Assert.Equal(1, rests);

        h.Clock += TimeSpan.FromMinutes(1);   // long past the re-ask interval
        hurting = MagmaHeat;
        h.Moved();

        Assert.Equal((1, 1), (waits, oks));
        Assert.False(h.HealthGateHeld);
        Assert.Equal(MagmaHeat, h.Health.RestDeferredByRoomSpell);
        Assert.Single(info, l => l.Contains("releasing the leader with @ok"));

        // Every prompt in the heat, and the next burning room, ask nothing more.
        h.State.Hp = 28;
        h.Health.Evaluate();
        h.Clock += TimeSpan.FromMinutes(1);
        h.Moved();
        Assert.Equal((1, 1), (waits, oks));
        Assert.Equal(rests, h.SentLines.Count(l => l == "rest"));

        // Out of it: the rest that was owed, and the @wait that goes with it.
        hurting = null;
        h.Moved();
        Assert.True(h.HealthGateHeld);
        Assert.Equal((2, 1), (waits, oks));
        Assert.Equal(rests + 1, h.SentLines.Count(l => l == "rest"));
    }

    [Fact]
    public void DamagingRoom_BeginsToBarWhileAWaitIsOut_ReleasesTheLeader()
    {
        // No move at all: the feather comes off under a resting follower.
        using Harness h = new();
        string? hurting = null;
        int waits = 0, oks = 0;
        h.Health.SetRoomSpellDamageProbe(() => hurting);
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => waits++,
            requestPartyOk: () => oks++,
            isSelfPoisoned: () => false);
        h.SetPrompt(hp: 30, maxHp: 100);
        h.Health.Evaluate();
        Assert.Equal((1, 0), (waits, oks));

        hurting = MagmaHeat;
        h.Health.Evaluate();
        h.Health.Evaluate();

        Assert.Equal((1, 1), (waits, oks));   // released once, not on every prompt
    }

    [Fact]
    public void DamagingRoom_LeftForARoomTheMapDoesNotHold_NothingStaysOwedHere()
    {
        // A move out of a placed room into an unplaced one is no NoteRoomChanged. The
        // deferral has to end all the same, or the rest-time heal goes on being cast
        // standing in an ordinary room.
        using Harness h = new();
        string? hurting = MagmaHeat;
        h.Health.SetRoomSpellDamageProbe(() => hurting);
        List<string> info = InfoLines(h);
        h.SetPrompt(hp: 30, maxHp: 100);
        h.Health.Evaluate();
        Assert.True(h.Health.HpRestDeferredByRoomSpell);

        hurting = null;   // an unplaced room bars nothing
        h.Health.NoteRoomPlacementChanged();

        Assert.Null(h.Health.RestDeferredByRoomSpell);
        Assert.False(h.Health.HpRestDeferredByRoomSpell);
        Assert.True(h.HealthGateHeld);
        Assert.Contains("rest", h.SentLines);

        // Placed again, in another burning room: the rest under way is given up, and
        // it is said for this room as for any new one.
        hurting = MagmaHeat;
        h.Health.NoteRoomPlacementChanged(new MudPlay.Game.Map.RoomKey(16, 2));
        Assert.False(h.HealthGateHeld);
        Assert.Equal(2, info.Count(l => l.Contains("not resting here; healing as set")));
    }

    [Fact]
    public void DamagingRoom_TrackerFlappingInOneRoom_SaysItOnce()
    {
        // Lost and found again in the same barred room, over and over (a grid of
        // look-alike rooms): one line for the room, not one per flap.
        using Harness h = new();
        var room = new MudPlay.Game.Map.RoomKey(16, 10109);
        string? hurting = MagmaHeat;
        h.Health.SetRoomSpellDamageProbe(() => hurting);
        List<string> info = InfoLines(h);
        h.Health.NoteRoomPlacementChanged(room);
        h.SetPrompt(hp: 30, maxHp: 100);
        h.Health.Evaluate();

        for (int flap = 0; flap < 3; flap++)
        {
            hurting = null;
            h.Health.NoteRoomPlacementChanged();       // lost
            hurting = MagmaHeat;
            h.Health.NoteRoomPlacementChanged(room);   // found, where we were
        }

        Assert.Single(info, l => l.Contains("not resting here; healing as set"));
    }

    [Fact]
    public void DamagingRoom_PlacementChangeWithNothingOwed_DoesNothing()
    {
        using Harness h = new();
        h.Health.SetRoomSpellDamageProbe(() => null);
        h.SetPrompt(hp: 30, maxHp: 100);
        h.Health.Evaluate();
        int sent = h.SentLines.Count;

        h.Health.NoteRoomPlacementChanged();   // the tracker lost its place mid-rest

        Assert.True(h.HealthGateHeld);
        Assert.Equal(sent, h.SentLines.Count);   // the rest under way isn't sent again
    }

    [Fact]
    public void DamagingRoom_RestGivenUpWhenTheRoomStartsToBar_RunsNoPostRestCommands()
    {
        // Resting with the feather on; it comes off. Nothing was recovered, so the
        // commands that follow a finished rest don't go out.
        using Harness h = new(new HealthSettings { PostRestCommand = "look;exits" });
        string? hurting = null;
        h.Health.SetRoomSpellDamageProbe(() => hurting);
        h.SetPrompt(hp: 30, maxHp: 100);
        h.Health.Evaluate();
        Assert.Contains("rest", h.SentLines);

        hurting = MagmaHeat;
        h.Health.Evaluate();

        Assert.False(h.HealthGateHeld);
        Assert.DoesNotContain("look", h.SentLines);
        Assert.DoesNotContain("exits", h.SentLines);

        // The rest that does finish, in the next room, still runs them.
        hurting = null;
        h.Moved();
        h.State.Hp = 96;
        Assert.Contains("look", h.SentLines);
        Assert.Contains("exits", h.SentLines);
    }

    // ----- "do not rest in this room" (per-waypoint) -----------------

    [Fact]
    public void DoNotRestRoom_BelowThreshold_SuppressesBothRestGates()
    {
        // In a loop's "do not rest" room we never raise the rest hold even when
        // HP and MA are both below "rest if below" — the loop advances instead.
        using Harness h = new();
        h.AutoHealRestEnabled = true;
        h.SkipRestHere = true;
        h.SetPrompt(hp: 30, maxHp: 100, ma: 10, maxMa: 100);   // both under default triggers

        h.Health.Evaluate();

        Assert.False(h.HealthGateHeld);
        Assert.False(h.ManaGateHeld);
    }

    // ----- "rest up here" (per-waypoint) ------------------------------

    [Fact]
    public void RestHereRoom_RestsToRestMax_EvenAboveTheTrigger()
    {
        // Defaults: rest if below 60 %, rest to 95 %. At 80 % a normal room walks on;
        // a rest-up-here room holds for the rest, and the one-tick confirm keeps it.
        using Harness h = new();
        h.AutoHealRestEnabled = true;
        h.SetPrompt(hp: 80, maxHp: 100, ma: 100, maxMa: 100);
        h.Health.Evaluate();
        Assert.False(h.HealthGateHeld);

        h.RestHere = (true, false);
        Assert.True(h.Health.HoldForRestHere());
        Assert.True(h.HealthGateHeld);
        Assert.False(h.ManaGateHeld);
    }

    [Fact]
    public void RestHereRoom_AlreadyAtRestMax_DoesNotHold()
    {
        using Harness h = new();
        h.AutoHealRestEnabled = true;
        h.RestHere = (true, true);
        h.SetPrompt(hp: 96, maxHp: 100, ma: 96, maxMa: 100);

        Assert.False(h.Health.HoldForRestHere());
        Assert.False(h.HealthGateHeld);
        Assert.False(h.ManaGateHeld);
    }

    [Fact]
    public void RestHereMana_HoldsOnlyForMana()
    {
        using Harness h = new();
        h.AutoHealRestEnabled = true;
        h.RestHere = (false, true);
        h.SetPrompt(hp: 80, maxHp: 100, ma: 80, maxMa: 100);

        Assert.True(h.Health.HoldForRestHere());
        Assert.False(h.HealthGateHeld);
        Assert.True(h.ManaGateHeld);
    }

    [Fact]
    public void DoNotRest_WinsOverRestHere_OnTheSameRoom()
    {
        using Harness h = new();
        h.AutoHealRestEnabled = true;
        h.RestHere = (true, true);
        h.SkipRestHere = true;
        h.SetPrompt(hp: 80, maxHp: 100, ma: 80, maxMa: 100);

        Assert.False(h.Health.HoldForRestHere());
    }

    [Fact]
    public void NormalRoom_BelowThreshold_StillRests_WhenSkipOff()
    {
        // Regression: the flag off = today's behavior; a below-threshold room rests.
        using Harness h = new();
        h.AutoHealRestEnabled = true;
        h.SkipRestHere = false;
        h.SetPrompt(hp: 30, maxHp: 100);

        h.Health.Evaluate();

        Assert.True(h.HealthGateHeld);
    }

    [Fact]
    public void SteppingIntoDoNotRestRoom_ReleasesAnAlreadyHeldRestGate()
    {
        // Held the rest gate in a normal room; the loop then reaches a do-not-rest
        // room → the hold is released so the loop resumes and walks out.
        using Harness h = new();
        h.AutoHealRestEnabled = true;
        h.SetPrompt(hp: 30, maxHp: 100);
        h.Health.Evaluate();
        Assert.True(h.HealthGateHeld);

        h.SkipRestHere = true;
        h.Health.Evaluate();
        Assert.False(h.HealthGateHeld);
    }

    [Fact]
    public void LeavingDoNotRestRoom_ReArmsRestGate_OnRoomChangeAlone()
    {
        // Report 141450: a do-not-rest room suppresses the rest gate with the pool
        // still low. Leaving it must re-arm the gate on the room change alone — a
        // plain Standing hop out raises no prompt change to re-run Evaluate, so
        // without this the deficit rides untended all lap (the "whole loop won't
        // rest" symptom, worst when the flagged room is the circle-start).
        using Harness h = new();
        h.AutoHealRestEnabled = true;

        // Low mana while standing in a do-not-rest room: gate is suppressed.
        h.SkipRestHere = true;
        h.SetPrompt(hp: 100, maxHp: 100, ma: 10, maxMa: 100);
        h.Health.Evaluate();
        Assert.False(h.ManaGateHeld);

        // Step OUT into a normal room — only the room change, no new prompt.
        h.SkipRestHere = false;
        h.Moved();

        // The gate re-arms so the loop rests in the next restable room.
        Assert.True(h.ManaGateHeld);
    }

    [Fact]
    public void LeavingNormalRoom_DoesNotSpuriouslyAssertRestGate()
    {
        // Guard: the re-arm only fires after a do-not-rest room. A room change with
        // pools full and no do-not-rest deferral must not assert a rest gate.
        using Harness h = new();
        h.AutoHealRestEnabled = true;
        h.SetPrompt(hp: 100, maxHp: 100, ma: 100, maxMa: 100);
        h.Health.Evaluate();
        Assert.False(h.ManaGateHeld);

        h.Moved();
        Assert.False(h.ManaGateHeld);
    }

    // ----- gear-swap max-pool fluctuation freeze -------------------------
    // Regression for paradigm-20260916-141742: a pre-rest gear swap's +MaxMana
    // confirmations stream in AFTER the send finishes, so PlayerState.MaxMa (and the
    // %-based rest target derived from it) walks through partial values while the pool
    // value stays pinned. A percentage target that momentarily drops UNDER the pinned
    // mana cleared the rest gate as "recovered" though mana never rose — recovery-complete
    // reverted to Default, the max jumped back, the pool re-read below trigger, and the
    // pre-rest swap fired again: the Pre-rest⇄Default gear thrash. The gate must hold
    // through the unsettled window and only re-decide once the max holds steady.

    [Fact]
    public void MaGateResting_GearSwapLowersMaxUnderPinnedMana_HoldsGateThenClearsOnceSettled()
    {
        HealthSettings s = new()
        {
            MaThresholdMode = ThresholdMode.Percentage,
            RestIfBelowMa = 50,   // trigger 50% of max
            RestMaxMa = 60,       // rest-target 60% of max
            UseMeditateAbility = true,
        };
        using Harness h = new(s) { DeferPost = true };

        // max 800 → trigger 400, target 480. Ma 390 < 400 → assert + meditate.
        h.SetPrompt(hp: 700, maxHp: 700, ma: 390, maxMa: 800);
        h.DrainPost();
        Assert.True(h.ManaGateHeld);
        Assert.Contains("meditate", h.SentLines);

        // Sitting to recover — now actively resting, so the clear floor is the target.
        h.State.Position = PlayerPosition.Meditating;
        h.Health.Evaluate();
        Assert.True(h.ManaGateHeld);

        // A pre-rest gear swap lowers MaxMa to 600 (target 60% → 360). Mana is pinned at
        // 390 — it never regenerated — yet 390 >= 360 would clear the gate on the old
        // code. The max just moved, so the gate must FREEZE, not clear.
        h.Clock += TimeSpan.FromMilliseconds(200);
        h.SetPrompt(hp: 700, maxHp: 700, ma: 390, maxMa: 600);
        Assert.True(h.ManaGateHeld);

        // Max holds steady past the settle window. Now the gate re-decides against the
        // settled max (target 360) — the pool genuinely sits above it → clears once.
        h.Clock += TimeSpan.FromSeconds(2);
        h.Health.Evaluate();
        Assert.False(h.ManaGateHeld);
    }

    // A real regen recovery with a STEADY max still clears at the target — the freeze
    // must not strand a genuine top-off (it only holds while the max is moving).
    [Fact]
    public void MaGateResting_ManaRegensToTargetWithSteadyMax_ClearsNormally()
    {
        HealthSettings s = new()
        {
            MaThresholdMode = ThresholdMode.Percentage,
            RestIfBelowMa = 50,
            RestMaxMa = 60,
            UseMeditateAbility = true,
        };
        using Harness h = new(s) { DeferPost = true };

        h.SetPrompt(hp: 700, maxHp: 700, ma: 390, maxMa: 800);   // < 400 trigger → assert
        h.DrainPost();
        Assert.True(h.ManaGateHeld);
        h.State.Position = PlayerPosition.Meditating;
        h.Health.Evaluate();
        Assert.True(h.ManaGateHeld);

        // Regen climbs mana to the target with max unchanged — a genuine recovery.
        h.Clock += TimeSpan.FromSeconds(5);
        h.SetPrompt(hp: 700, maxHp: 700, ma: 490, maxMa: 800);   // >= 480 target
        Assert.False(h.ManaGateHeld);
    }
}
