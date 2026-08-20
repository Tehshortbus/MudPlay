using System.Text;
using MudPlay.Game;
using MudPlay.Game.Health;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

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
        public bool AutoHealRestEnabled { get; set; } = true;

        /// <summary>Char-tier General settings. Default instance has
        /// AllowHangupInAllOffMode=false, so the all-off carve-out stays
        /// dormant unless a test opts in.</summary>
        public Models.Profile.GeneralSettings General { get; set; } = new();

        /// <summary>User-configured hangup command (Settings → BBS →
        /// Game-menu commands). Default <c>=x</c> matches the default
        /// value shipped on <c>BbsProfile.GameExitCommand</c>. Set to
        /// null to test the "not configured" branch.</summary>
        public string? HangupCommand { get; set; } = "=x";

        /// <summary>When true, HealthManager's rest-out branch skips —
        /// mirrors CombatStateTracker.HasEngageableHostiles in app code.
        /// Defaults false (room clear) so existing tests don't need to
        /// touch it.</summary>
        public bool HostilesPresent { get; set; }

        /// <summary>Hostile-in-room signal gating the emergency hangup —
        /// mirrors CombatStateTracker.HasHostileMonster (auto-attack
        /// independent). Defaults true so the existing hangup tests, which
        /// predate the gate, keep firing exactly as before.</summary>
        public bool HostileInRoom { get; set; } = true;

        /// <summary>Per-BBS negative-HP death floor (BbsProfile.PlayerDiesAtHp).
        /// Default -25 matches the seeded value. The emergency hangup fires
        /// anywhere in the bleeding-out window down to — but not past — this.</summary>
        public int DeathFloor { get; set; } = -25;

        /// <summary>Simulates a loop's "do not rest in this room" flag — when true,
        /// HealthManager suppresses the rest hold (the loop would advance instead of
        /// resting). Defaults false so existing tests rest normally. AppServices
        /// wires this to the running loop's current-room DoNotRest waypoint.</summary>
        public bool SkipRestHere { get; set; }

        /// <summary>Local character poisoned — AppServices wires this to
        /// ConditionTracker.IsPoisoned. Poison prevents a rest from taking.</summary>
        public bool Poisoned { get; set; }

        /// <summary>The hangup-intent signal wired into the emergency-hangup
        /// path. Tests peek it (non-consuming) to assert an intentional drop was
        /// flagged, so the reactive-reconnect path stands down.</summary>
        public HangupSignal Hangup { get; } = new();

        /// <summary>ShadowRest predicates (AppServices wires these to the class
        /// ability, StealthManager.IsStealthed, and !PartyState.IsInParty). All
        /// default to the "engaged" side so a test only sets the one it's probing;
        /// the UtilizeShadowRest setting still has to be on for it to fire.</summary>
        public bool ShadowRestClass { get; set; } = true;
        public bool Stealthed { get; set; } = true;
        public bool Solo { get; set; } = true;

        /// <summary>Increments each time HealthManager fires the ShadowRest resume
        /// callback (recovery topped off to rest-max). AppServices wires this to
        /// CombatManager.ResumeAfterShadowRest.</summary>
        public int ShadowRestResumeCount { get; private set; }

        /// <summary>Increments each time the emergency hangup asks the client to
        /// close the carrier after sending the exit command. AppServices wires
        /// this to MainWindowViewModel.RequestHangupDisconnect.</summary>
        public int HangupDisconnectCount { get; private set; }

        /// <summary>Controllable clock for the reconfirm-hold timeout backstop.
        /// Advance it to simulate the window elapsing with no room re-display.</summary>
        public DateTimeOffset Clock = DateTimeOffset.UtcNow;

        public Harness(HealthSettings? settings = null)
        {
            Settings = settings ?? new HealthSettings();
            Coordinator = new MovementCoordinator(Log);
            Health = new HealthManager(State, Coordinator,
                readSettings: () => Settings,
                isEnabled: () => AutoHealRestEnabled,
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
                now: () => Clock);
            Health.SetWireSender(b => Sent.Add(b));
            Health.SetHangupDisconnect(() => HangupDisconnectCount++);
            Health.SetShadowRest(
                shadowRestClass: () => ShadowRestClass,
                isStealthed: () => Stealthed,
                isSolo: () => Solo,
                onRecovered: () => ShadowRestResumeCount++);
            Health.SetDoNotRestSelector(() => SkipRestHere);
            Health.SetPartyRoleSync(
                isPartyFollower: () => false,
                requestPartyWait: () => { },
                requestPartyOk: () => { },
                isSelfPoisoned: () => Poisoned);
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

        h.Health.NoteRoomChanged();           // a move re-observes
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

        h.Health.NoteRoomChanged();
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

        h.Health.NoteRoomChanged();
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

        h.Health.NoteRoomChanged();
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

    [Fact]
    public void HpBelowRunTrigger_InCombat_SendsFleeOnce()
    {
        // Default RunIfBelowHp=20% — set HP to 30 against MaxHp=200
        // (15%) while in combat. Run-threshold detection latches
        // _fledThisCombat=true; the wire emit is currently log-only
        // because MajorMUD has no `flee` command and the right
        // replacement (walker-driven retreat) ships with Cluster 5b.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;

        Assert.DoesNotContain("flee", h.SentLines);   // engine never sent the bogus command
        Assert.True(h.Health.FledThisCombat);

        // Drop further → still no spam.
        h.State.Hp = 25;
        Assert.DoesNotContain("flee", h.SentLines);
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
    public void FledThisCombat_ReArmsOnNextCombat()
    {
        // Latch flips in fight #1, clears when combat ends, re-arms
        // for fight #2.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;
        Assert.True(h.Health.FledThisCombat);

        h.State.InCombat = false;
        h.State.Hp = 100;
        Assert.False(h.Health.FledThisCombat);

        h.State.InCombat = true;
        h.State.Hp = 25;
        Assert.True(h.Health.FledThisCombat);
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

        Assert.True(h.Health.FledThisCombat);
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

        Assert.False(h.Health.FledThisCombat);
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

        Assert.False(h.Health.FledThisCombat);
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

        Assert.False(h.Health.FledThisCombat);
    }

    [Fact]
    public void RunLatchAndRest_BothHappen_AfterCombatEnds()
    {
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;
        Assert.True(h.Health.FledThisCombat);
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
        Assert.True(h.Health.FledThisCombat);

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
        Assert.True(h.Health.FledThisCombat);
        Assert.DoesNotContain("flee", h.SentLines);
    }

    [Fact]
    public void Follower_WithoutHealCallbackWired_FallsBackToFlee()
    {
        // isPartyFollower true but no requestPartyHeal callback wired: the
        // null-guard falls through to the flee path rather than silently doing
        // nothing at the run trigger.
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => { });   // requestPartyHeal omitted (null)
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.State.HasPromptData = true;
        h.State.Hp = 30;

        // Flee path taken (still a no-op on the wire without an engine), and
        // the single-shot latch is set.
        Assert.True(h.Health.FledThisCombat);
        Assert.DoesNotContain("flee", h.SentLines);
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
        Assert.Equal(1, oks);             // @ok fires exactly once, now that we're rested
        Assert.Equal(1, waits);           // and @wait never re-fired mid-recovery
    }

    // ----- Multi-step flee + auto-resume (Cluster 5b foundation) ----

    /// <summary>Fake engine for testing the flee dispatch — captures
    /// every call instead of touching real walker plumbing.</summary>
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

        public Game.Map.Direction? PeekNextPlannedDirection() => NextPlanned;
        public IReadOnlyList<Game.Map.Direction> PeekPlannedDirections(int count) =>
            PlannedForward.Take(count).ToList();
        public void SendBacktrackMove(Game.Map.Direction d) => SentBacktrackMoves.Add(d);
        public void PauseForRecovery(string reason) => PausedReason = reason;
        public void ResumeAfterRecovery(Game.Map.RoomKey k) => ResumedAtRoom = k;
        public void AbortFromRecoveryFailure(string _) { }
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
                isEnabled: () => true,
                readHangupCommand: () => string.Empty,
                getActiveMovementEngine: () => Engine,
                getLastSentDirection: () => LastSent,
                readCombatSettings: () => Combat,
                readGeneralSettings: null,
                hasEngageableHostiles: () => HostilesPresent,
                log: Log,
                hasHostileInRoom: () => HostileInRoom,
                findReversePath: (from, to) => ReversePath?.Invoke(from, to),
                post: a => { if (DeferFlee) Posted.Enqueue(a); else a(); });
            Health.SetWireSender(b => Sent.Add(b));
        }

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

        Assert.True(h.Health.FledThisCombat);
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

        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 102));
        Assert.Equal(3, h.Engine.SentBacktrackMoves.Count);    // stopped
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
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 101));   // beyond the cap
        Assert.Equal(2, h.Engine.SentBacktrackMoves.Count);
        Assert.Equal(
            new[] { Game.Map.Direction.S, Game.Map.Direction.W },
            h.Engine.SentBacktrackMoves);
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
        h.Health.NoteRoomChanged(new Game.Map.RoomKey(1, 100));   // record room
        Assert.NotNull(h.Engine!.PausedReason);

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
        // `rest` (mirrors MudProxy's OnRestingStateChanged).
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

    // ----- Meditate vs Rest (Cluster 5c) -----------------------------

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

    // ----- Hangup-on-emergency (Cluster 5c) -------------------------

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
        Assert.False(h.Hangup.PeekForTests().DisconnectExpected);

        h.SetPrompt(hp: 5, maxHp: 200);   // below default 5% hang threshold — fires

        Assert.Contains("=x", h.SentLines);
        Assert.True(h.Hangup.PeekForTests().DisconnectExpected);
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
        // character that dropped with every engine off still gets its escape.
        using Harness h = new();
        h.AutoHealRestEnabled = false;
        h.General = new Models.Profile.GeneralSettings { AllowHangupInAllOffMode = true };
        h.SetPrompt(hp: -10, maxHp: 200);

        Assert.Contains("=x", h.SentLines);
    }

    // ----- all-off-mode hangup carve-out ----------------------------

    [Fact]
    public void AllOff_HangupAllowed_HpBelowTrigger_StillHangs()
    {
        // Engine disabled but the opt-in keeps the emergency hangup live.
        using Harness h = new();
        h.AutoHealRestEnabled = false;
        h.General = new Models.Profile.GeneralSettings { AllowHangupInAllOffMode = true };
        h.SetPrompt(hp: 5, maxHp: 200);   // 2.5% — below default 5% hang threshold

        Assert.Contains("=x", h.SentLines);
    }

    [Fact]
    public void AllOff_HangupNotAllowed_HpBelowTrigger_NoHang()
    {
        // Engine disabled and carve-out off (default) — fully dormant.
        using Harness h = new();
        h.AutoHealRestEnabled = false;
        // h.General left at default (AllowHangupInAllOffMode = false)
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 5;

        Assert.DoesNotContain("=x", h.SentLines);
    }

    [Fact]
    public void AllOff_HangupAllowed_AboveThreshold_NoHang()
    {
        // Carve-out on but HP healthy — no spurious hangup.
        using Harness h = new();
        h.AutoHealRestEnabled = false;
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
        // DisableHangups wins, so an all-engines-off character at lethal
        // HP still won't auto-disconnect.
        using Harness h = new();
        h.AutoHealRestEnabled = false;
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

    // ----- party-role-aware recovery (PR 9.B role fix) ---------------

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
        h.State.Hp = 195;            // still above — no second @ok
        Assert.Equal(1, oks);
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

    [Fact]
    public void NoRoleSync_RecoversToRestMax_NoSignals()
    {
        // Backward-compat: without SetPartyRoleSync the engine behaves as
        // solo/leader — rest-max target, no party signals.
        using Harness h = new();
        h.State.MaxHp = 200;
        h.State.HasPromptData = true;
        h.State.Hp = 50;
        h.State.Hp = 121;            // above floor but below rest-max
        Assert.True(h.HealthGateHeld);
        h.State.Hp = 190;
        Assert.False(h.HealthGateHeld);
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
    public void Opportunistic_NoLeaderRestSelector_DoesNotRest()
    {
        // Backward-compat: the 3-arg SetPartyRoleSync leaves isLeaderResting
        // null, so opportunistic top-off never engages even as a follower.
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => true,
            requestPartyWait: () => { },
            requestPartyOk: () => { });
        h.SetPrompt(hp: 150, maxHp: 200);

        Assert.DoesNotContain("rest", h.SentLines);
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
    public void LeaderWaited_NoWaitSelector_DoesNotRest()
    {
        // Backward-compat: without isLeaderWaited wired, a leader never rests
        // just because it's paused.
        using Harness h = new();
        h.Health.SetPartyRoleSync(
            isPartyFollower: () => false,
            requestPartyWait: () => { },
            requestPartyOk: () => { });
        h.SetPrompt(hp: 150, maxHp: 200);

        Assert.DoesNotContain("rest", h.SentLines);
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
        h.Health.NoteRoomChanged();

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

        h.Health.NoteRoomChanged();
        Assert.False(h.ManaGateHeld);
    }
}
