using System.Text;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Combat;

// Auto-attack engine. Subscribes to RoomEntityClassifier.EntitiesObserved and
// (for re-fire pacing) to KnownPatterns.PartyAttackAnnounce. Picks a target per
// MonsterOverlay.Priority + CombatSettings.TargetOrder, filters out anything not
// flagged MonsterRelationship.Enemy, and sends the configured attack command.
// Server auto-repeats swings each 5-second round; CombatManager re-picks when
// the room re-displays without the current target, and resumes (re-engages)
// when the server reports *Combat Off* mid-fight — e.g. a manual buff / heal
// cast interrupts our round but the mob is still alive and swinging at us.
//
// Target selection — single source of truth across the engine:
//   1. Classifier filters Also-Here to EntityKind.Monster.
//   2. Each monster's MonsterOverlay is resolved via MonsterOverlaySeedStore
//      (Defaults tier) merged with SettingsResolver.ResolveGameData (Global /
//      BBS / Char overrides).
//   3. Engageable = MonsterRelationship.Enemy (Relationship-based only).
//   4. Engageable list is sorted by MonsterAttackPriority (First=0 highest,
//      Last=4 lowest), tiebreak by appearance order in the Also-Here line.
//   5. CombatSettings.TargetOrder picks Normal = first sorted (highest prio) or
//      Reverse = last sorted (lowest prio).
//
// A "moves to attack X" announce drives two independent knobs (the "who" and
// the "when" of party combat):
//
// Target Priority — WHO (CombatSettings.TargetPriority):
//   - Default      — pick our own target; ignore others' announces.
//   - FollowLeader — switch our target to the party leader's announced monster
//                    (PartyState.LeaderName).
//   - FollowMember — switch to the named TargetPriorityMemberName's monster.
// Either follow mode applies the standard un-actionable failback: if game data
// proves we can't hit the followed monster (no weapon hits, every attack spell
// level-blocked) we re-pick our own next actionable target instead of following
// into a fight we can't contribute to.
//
// Attack Order — WHEN (CombatSettings.AttackTiming): re-fires our OWN current
// target to control initiative order; never switches the monster.
//   - Default         — never re-fire.
//   - AttackLastParty — re-fire on a party member's announce (excludes
//                       non-party players).
//   - AttackLastRoom  — re-fire on anyone's announce.
//   - AttackAfter     — re-fire only on the named AttackAfterPlayerName's
//                       announce.
//
// Our own announce never drives either knob (we already swung; matched against
// the own-name reader). Attack Order re-fire requires a non-null CurrentTarget —
// we can only re-issue against a target we already chose. The interim-pick rule:
// on room entry we dispatch our own target immediately (OnEntitiesObserved),
// then a follow mode switches to the leader / member's target the moment they
// announce.
public sealed partial class CombatManager : IDisposable
{
    // LogService category — appears as [Combat] rows per swing decision + target
    // swap + re-fire.
    public const string LogCategory = "Combat";

    // Fires when a combat line arrives but the room shows no engageable target and
    // we hold none — something is swinging at us that our room view has lost (a
    // hostile that leapt in after an empty render, an arrival line we missed). We
    // send a CR to force a short re-display; CombatRedisplaySettle listens here to
    // hold the movement loop until that re-display reveals the hostile (Combat gate
    // takes over) or confirms the room is really empty (loop steps on). Only fires
    // when the CR actually went out — a dark room suppresses the CR and is handled
    // by DarkRoomMovementSettle instead, so this stays scoped to the lit-room case.
    public event Action? RoomAppearsEmptyDuringCombat;

    private readonly RoomEntityClassifier _classifier;
    private readonly MonsterMessageStore _monsters;
    private readonly Func<int, MonsterOverlay> _resolveOverlay;
    // Optional room-aware name→Number resolver: prefers the record actually in the
    // current room so a display name shared across zones picks this room's variant.
    // Null (tests / legacy ctor) → the name→Number step keeps its first-match scan.
    private readonly Func<string, int?>? _roomAwareResolve;
    // Resolves a debuff slot's cast-code to its catalog row so the between-round
    // debuff dispatch can reject a mis-slotted spell (an attack spell with
    // non-zero energy, or a targeting scope that doesn't fit single vs area).
    private readonly Func<string, Game.Spells.KnownSpell?>? _resolveSpellByCode;
    // Cast-codes already warned about as invalid debuff-slot configs, so the
    // program log gets one line per bad slot rather than one per round.
    private readonly HashSet<string> _warnedInvalidDebuffSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly PartyState _party;
    private readonly Func<CombatSettings> _readSettings;
    private readonly Func<PartySettings>? _readPartySettings;
    private readonly Func<bool> _isEnabled;
    private readonly Func<string?> _readOwnGivenName;
    private readonly LogService? _log;
    // Marshals a callback to the UI dispatcher (production) so a coalesced
    // AttackTiming re-fire flushes one turn after the round's announce burst.
    private readonly Action<Action> _post;

    private readonly IDisposable _announceSub;
    private readonly IDisposable _castAnnounceSub;
    private readonly IDisposable _userHitsSub;
    private readonly IDisposable _mobHitsSub;
    private readonly IDisposable _mobMissesSub;
    private readonly IDisposable _targetGoneSub;
    private readonly IDisposable _weaponNoEffectSub;
    private readonly IDisposable _fistsNoEffectSub;
    private readonly IDisposable _spellNoEffectSub;
    private readonly IDisposable _commandNoEffectSub;
    private readonly IDisposable _combatStatusSub;
    private readonly IDisposable _expGainSub;
    private readonly IDisposable _monsterProtectSub;
    private readonly IDisposable _bsResolveHitsSub;
    private readonly IDisposable _bsResolveMissesSub;
    private readonly IDisposable _attackConfirmHitsSub;
    private readonly IDisposable _attackConfirmMissesSub;

    // Minimum gap between safety-net `l` refreshes. Keeps a flurry of miss/hit
    // lines from spamming the server.
    private static readonly TimeSpan RoomRefreshCooldown = TimeSpan.FromSeconds(3);

    // How long to wait for the server's *Combat Engaged* after sending a fresh
    // attack before assuming the attack hit a stale room view and firing a CR
    // re-display. One combat round — a real engage prints the line well within a
    // round, so a full round with no confirmation means the named target isn't
    // actually here.
    private static readonly TimeSpan EngageConfirmWindow = TimeSpan.FromSeconds(5);

    private Action<byte[]>? _wireSender;
    // Gear actuation is owned by EquipmentManager (the sole actuator). Combat
    // decides which weapon/loadout it wants and hands the act off through these
    // delegates — it never touches the wire for gear itself. swapWeapon equips a
    // weapon + off-hand immediately (the fast, unpaced path that must land before
    // the next swing); prepBackstabArmor applies the Backstab set's armor as a
    // synchronous burst in the pre-move sequence, before the sn. Until wired both
    // no-op, so the manager stays a pure decider (tests assert on the decision,
    // not the actuation).
    private Action<string?, string?, bool>? _swapWeapon;
    private Action? _prepBackstabArmor;
    private Func<bool>? _isStealthed;
    private Func<int, bool>? _hasSeeHidden;
    private Func<bool>? _seeHiddenClearActive;
    // HealthManager's engage-to-clear signal: a room hostile is blocking a needed
    // rest while Auto-Combat is OFF (and HP still above the flee trigger). Like the
    // see-hidden latch, when true it makes us engage despite being disabled and
    // bypass the Min/Max gate — to kill the rest-blocker so recovery can proceed.
    private Func<bool>? _restClearActive;
    private Func<bool>? _shadowRestHolding;

    // Passive-neutral recovery hold: engage a KillOnSight neutral only when we're not
    // below the rest trigger, so we can rest/meditate between kills (a neutral never
    // attacks until we hit it). See the hold in the engage flow + ResumeAfterRecovery.
    private Func<bool>? _recoveryPending;         // HealthManager.IsRecoveringRest
    private Func<bool>? _hasAttackingHostile;     // CombatStateTracker.HasHostileMonster (Enemy present)
    private Action? _clearInCombatForHold;        // CombatStateTracker.ClearInCombatForRecoveryHold
    private bool _holdingForRecovery;

    // Reports whether the character is standing in a too-dark room
    // (RoomTracker.IsInDarkRoom). Gates the CR "where am I" room re-displays: a CR
    // in the dark returns only "you can't see anything" — nothing to re-read — and
    // that stale dark line is consumed by the movement loop's dead-reckoning as a
    // false confirmation of an in-flight step, collapsing the dark-room settle
    // window (see TrySendRoomRefresh). null until wired → fail-open (refreshes
    // send exactly as before).
    private Func<bool>? _isInDarkRoom;

    // Reports whether a movement engine (walker / loop / auto-lair) is
    // currently attached and driving us through rooms (EngineRecoveryGate.
    // AttachedEngine is not null). Gates the Min/Max monsters skip below —
    // see SetMovementActiveGate. null until wired → fail-open (the gate
    // always applies, this check's original unconditional behavior).
    private Func<bool>? _isMovementActive;

    // Reports whether an AttackPrevented condition is active on us
    // (ConditionTracker.IsAttackPrevented) — a stun/petrify/etc. line latched and
    // its wear-off not yet seen. While true the server rejects every attack the
    // player issues, weapon AND spell, so the engine holds ALL offensive output
    // (see AttacksBlocked) and retries each round until it clears. null until wired
    // → fail-open (never blocks, the pre-flag behavior).
    private Func<bool>? _attacksPrevented;

    // Edge-tracker so AttacksBlocked logs the hold and the release once each, not
    // once per suppressed send.
    private bool _attackBlockLogged;

    private string? _currentTarget;

    // Guard-redirect memory. MajorMUD "guarded" monsters (a brigand chief guarded
    // by brigands) can't be hit directly while a guard is in the room — each swing
    // we aim at the chief is redirected to a guard, announced by "<guard> moves to
    // protect <chief>". We stash the intended priority here (leaving _currentTarget
    // on the chief) and re-attack it by name after each guard falls, until no guard
    // remains and the swing lands on the chief. Durable across the room-cleared
    // reset that a guard death's roster desync can trigger — that reset nulls
    // _currentTarget and disarms the normal resume, so this separate memory is what
    // drives recovery (the reported "chief left unattacked after the last guard
    // died" stall). Cleared when the priority itself dies / leaves / on room change.
    private string? _guardBlockedTarget;

    // Target-Priority follow deferral. When we're partied, in a multi-mob room,
    // and configured to follow the leader's / a member's target, we hold our own
    // room-entry pick and wait for that player's "moves to attack X" announce so
    // we engage THEIR target — priority/order settings never change HOW we engage
    // (verb/spell/weapon still come from the pickers), only WHICH mob and WHEN.
    // _awaitingFollowAnnounce latches while we're holding; TryFollowTargetPriority
    // clears it when the followed announce lands, and OnCombatTick clears it with
    // a fallback to our own game-data pick if no announce arrives that round.
    private bool _awaitingFollowAnnounce;

    // Set only for the duration of the tick fallback's re-entrant
    // OnEntitiesObserved call so the defer branch doesn't re-latch and strand us
    // — the fallback's whole point is to make our OWN independent pick this round.
    private bool _followDeferBypass;

    // Set only for the duration of ResumeEngage's re-dispatch so the "already
    // engaged, server is still swinging → don't re-send" short-circuit is bypassed.
    // The interrupt (heal/bless/buff) turned our auto-attack OFF, so the round's
    // action MUST be re-sent — but we keep _currentTarget set (unlike the old
    // clear-and-refresh) so DispatchRoundAction's new-target reset doesn't zero the
    // round-cycle phase / attack-spell cascade on every interrupt.
    private bool _resumeBypassEngagedGuard;

    // Simultaneous-arrival settle (see the long note at the arm site in
    // OnEntitiesObserved). _arrivalSettleArmed latches while a burst's first engage
    // is held; _arrivalSettleBypass is set only for the settle callback's re-entrant
    // OnEntitiesObserved so it dispatches instead of re-arming. _scheduleArrivalSettle
    // is the injected UI-thread one-shot (null in tests that don't opt in → the old
    // immediate-engage path). The window only needs to span the synchronous
    // processing of a burst + its room re-display; it doubles as the (rare) lone-spawn
    // engage latency, so keep it short.
    private static readonly TimeSpan ArrivalSettleWindow = TimeSpan.FromMilliseconds(350);
    private bool _arrivalSettleArmed;
    private bool _arrivalSettleBypass;
    private Action<TimeSpan, Action>? _scheduleArrivalSettle;

    // The caster's per-round cascade SWITCH (cap-switch / decision-change) is decided
    // on the round's damage line — for a MaxCasts-1 nuke that's the spell's OWN killing
    // blow. The kill's death/exp/*Combat Off* often arrive in a LATER server packet
    // than that damage line, so deferring "past the current burst" (an immediate UI-post)
    // still fires the alternate before the kill packet is processed → the alternate
    // corpse-casts at the just-killed mob ("You don't see X here!"; reports
    // paradigm-20260815-201731 / -202241, confirmed high-mana so it's the cap-switch, not
    // the mana fallback). Instead delay the switch dispatch a short REAL-TIME window so
    // the adjacent kill packet lands and the exp-inferred drop nulls the target first;
    // the delayed dispatch then re-validates and skips a dead target. 750ms was the
    // original window, chosen as "far under" a 5s round — but a capped multi-projectile
    // attack spell (e.g. a priest's disr) resolves its own cast in under 2s, so the
    // 750ms wait was itself eating most of the remaining reaction time before the
    // server locked in one more round of the capped spell (MaxCasts=1 still firing
    // twice; report paradigm-20260822-003106). Bridging one adjacent network packet
    // only needs on the order of a round-trip, not half a second, so the window is cut
    // to 200ms — still enough for the trailing kill packet to land and re-validate away,
    // while leaving far more of the round for the switch to actually beat the server's
    // next auto-repeat. Injected one-shot scheduler; null in tests that don't opt in
    // (falls back to _post).
    private static readonly TimeSpan SwitchDispatchDelay = TimeSpan.FromMilliseconds(200);
    private Action<TimeSpan, Action>? _scheduleSwitchDispatch;

    // Starts TickEngine's timer fallback when a combat-spell engage is locally
    // blocked before any attack reaches the server. Optional injection keeps the
    // combat layer UI/timer-free; production wires TickEngine.EnsureCombatTickAnchor.
    // Once a real combat line has anchored the cycle, the callback is a no-op.
    private Action? _ensureCombatTickAnchor;

    // Backstab flee-on-failure action, bound in AppServices to
    // HealthManager.RunFromBackstabFailure. null until wired; even when wired it
    // fires only on a detected failure AND when CombatSettings.RunIfBackstabFails
    // is on, so a failed backstab otherwise just logs and the fight continues.
    private Action? _backstabFailureFlee;

    // Surprise-round resolution watch. Armed the instant a `bs` goes out
    // (DispatchRoundAction) and disarmed by the first of OUR own combat-result
    // lines that names the target: a line carrying "surprise" means the opener
    // landed; a line without it (a whiff, or a folded normal round) means the
    // backstab failed. Kept tight — also cleared on room change / target death /
    // room clear / target-gone so a missed resolution line can't leave the
    // re-fire suppression latched forever. _pendingBackstabSpecies is the resolved
    // species the line must name, which rejects the broad UserMisses pattern's
    // self-emote false positives ("You feel much better!").
    private bool _awaitingBackstabResolution;
    private string? _pendingBackstabSpecies;

    // Spell-vs-weapon mode bridge (combat-spell economy, opt-in via
    // SetCombatSpellCaster). Non-null = the current round's action is a combat
    // spell against this RawName, so the tick heartbeat (OnCombatTick) must
    // re-issue the cast each round (casts don't auto-repeat server-side the way
    // weapon swings do). null = weapon mode (server auto-repeats) or idle. Every
    // weapon SendAttack clears it — any swing exits spell mode. Lives in the main
    // file because SendAttack touches it; the rest of the spell machinery is in
    // CombatManager.Spells.cs.
    private string? _castingSpellTarget;

    // Round counter for the alternating action orders (CombatActionOrder.Alternate*),
    // driving the per-round spell-vs-physical phase. 0 at engage / room-clear /
    // target change, then advanced once per round by the OnCombatTick alternation
    // branch. Even rounds are the first phase (spell for AlternateSpellPhysical,
    // physical for AlternatePhysicalSpell). Unused by the fixed orders.
    private int _alternationRound;

    // Real-time stamp of the last _alternationRound advance — see
    // AlternationAdvanceMinGap. Reset to MinValue everywhere _alternationRound
    // resets to 0, so a fresh fight's first phase flip is never blocked by a
    // stale stamp from a previous one.
    private DateTimeOffset _lastAlternationAdvanceAt = DateTimeOffset.MinValue;

    // Minimum real time between two _alternationRound advances. TickEngine's
    // CombatTickElapsed — what drives the advance — fires on every hit/miss line
    // (only 250ms-debounced), not once per true ~5s MajorMUD round: a monster's
    // counter-swing line landing a beat after the player's own can each trip a
    // separate tick within the SAME round. Without this guard that advances the
    // phase twice in one round, flipping physical→spell (or back) mid-round
    // instead of on the next real one — the reported "switched too fast, moved to
    // attack then instantly wanted the spell". 4s sits comfortably below
    // TickEngine.CombatTickInterval (5s) so the genuine next-round tick is never
    // itself rejected, while safely rejecting the ~1-2s stray re-fire observed.
    private static readonly TimeSpan AlternationAdvanceMinGap = TimeSpan.FromSeconds(4);

    // Real-time stamp of the last attack-spell MaxCasts tally in the spell heartbeat,
    // gated by AttackTallyMinGap. LEGACY FALLBACK — only used when ReadRoundCount is
    // unwired (tests that don't opt in). Reset to MinValue wherever the per-target
    // cascade resets (alongside _lastAlternationAdvanceAt).
    private DateTimeOffset _lastAttackTallyAt = DateTimeOffset.MinValue;

    // Robust round-count tally. When wired, MaxCasts counts one cast per REAL combat
    // round instead of the fragile AttackTallyMinGap wall-clock on the damage-line
    // heartbeat — the heartbeat trips several times a round (our multi-hit + the mob's
    // swing + a stale interval), and no single wall-clock threshold separates a genuine
    // round tick from a premature one: reports paradigm-20260819-120938 (premature
    // tally → capped before lbol fired) vs -055820 (genuine fast tick rejected →
    // tallied a round late) are that exact tension. One round = one tally.
    // Set to the current round on engage so the first tally waits for the next round
    // CLOSE; unwired leaves it -1 and the legacy wall-clock path runs.
    //
    // Wired (AppServices) to ConfirmedAttackCastCount, NOT RoundDamageTracker's own
    // RoundCount — that tracker's 5s timer-driven window is sized for DPS/session
    // stats, not for this. A fast multi-projectile caster can complete more than one
    // real cast inside a single RoundDamageTracker window (its "first hit" anchor
    // only starts counting once combat produces a damage line, so a slow-opening
    // fight's window can span 8-10s+), silently under-counting MaxCasts by exactly
    // the number of casts that landed inside one window (report paradigm-20260822-003106:
    // MaxCasts=1 still cast twice, on a fight RoundDamageTracker measured as one
    // 10s round). ConfirmedAttackCastCount instead increments directly off each
    // observed cast-confirmation line, so it can't bundle two real casts into one tally.
    internal Func<int>? ReadRoundCount { get; set; }
    private int _lastTalliedRound = -1;

    // How many real single-target attack-spell casts OnAttackCastConfirmed has
    // observed landing this session — the precise signal ReadRoundCount is wired to
    // (see its comment). Monotonic, like RoundDamageTracker.RoundCount; callers
    // baseline against it via _lastTalliedRound, never read it as a target-scoped
    // count.
    internal int ConfirmedAttackCastCount { get; private set; }

    // Real-time stamp of the last OnAttackCastConfirmed increment, for the
    // multi-projectile grouping window (ConfirmedCastGroupWindow) — a spell that
    // fires more than one damage line per cast (many single-target attack spells do)
    // must count as ONE cast, not one per line. 1.2s comfortably covers the
    // sub-second spacing observed between one cast's own projectiles while staying
    // well under the shortest real round-to-round gap.
    private DateTimeOffset _lastConfirmedAttackCastAt = DateTimeOffset.MinValue;
    private string? _lastConfirmedAttackCastTarget;
    private static readonly TimeSpan ConfirmedCastGroupWindow = TimeSpan.FromMilliseconds(1200);

    // Minimum real time between two MaxCasts tallies — the SAME round-spacing guard
    // AlternationAdvanceMinGap applies to the phase advance, for the same reason: the
    // damage-line-driven tick fires 2-3× within one real ~5s round (a multi-hit attack
    // spell's several damage lines plus the mob's counter-swing), and tallying every
    // trip counted a MaxCasts=2 spell to its cap in a single round — the engage spell
    // swapping a round early (report paradigm-20260815-130957). Kept equal to the
    // alternation gap; both sit below TickEngine.CombatTickInterval (5s) so the genuine
    // next-round tick is never itself rejected.
    private static readonly TimeSpan AttackTallyMinGap = AlternationAdvanceMinGap;

    // Clock for AlternationAdvanceMinGap, mirroring CastingDirector.SetClock —
    // tests inject a fake clock so a synchronous burst of Tick() calls can
    // exercise multi-round phase timing without real elapsed wall-clock time.
    // Scoped to just this one check; every other timestamp in this file still
    // reads DateTimeOffset.Now directly.
    private Func<DateTimeOffset> _now = () => DateTimeOffset.Now;

    private string? _lastAttackCommand;
    private DateTimeOffset _lastRoomRefreshAt = DateTimeOffset.MinValue;
    private bool _disposed;

    // Set when the server emits *Combat Off* — our auto-attack stopped. The
    // server fires this on a kill AND whenever a round is interrupted by a
    // non-attack action (we manually cast a buff / heal mid-round, got stunned,
    // etc.). On a kill the room re-display / death path clears _currentTarget and
    // we re-pick normally; on an interrupt the target is still alive in the room
    // but the server is no longer swinging for us. This flag lets the next
    // incoming mob combat-line (OnCombatLine) resume the attack instead of
    // short-circuiting on the stale _currentTarget ("server still swinging").
    // Cleared the moment we send any attack or the server reports Engaged.
    private bool _combatOff;

    // CastingDirector/diagnostics view of _combatOff — stuck true here with a live
    // CastingSpellTarget is the signature of report paradigm-20260824-215802: an
    // engage whose attack cast got blocked (round already spent) left OnCombatTick's
    // spell-mode heartbeat permanently gated, so nothing ever retried the attack.
    public bool CombatOff => _combatOff;

    // Timestamp of the last interrupt-resume (see TryResumeEngage) — paces
    // re-engages to one per round so a non-sustaining attack (KAI pummel, which
    // emits *Combat Off* after every strike) can't spin.
    private DateTimeOffset _lastInterruptResumeAt = DateTimeOffset.MinValue;

    // Minimum spacing between interrupt-resumes. Shorter than a combat round
    // (~5s) so a legitimate next-round resume isn't blocked, but long enough to
    // swallow the instant Off/Engaged cycle a per-strike attack produces.
    private static readonly TimeSpan ResumePacing = TimeSpan.FromMilliseconds(2500);

    // Rate limit for the spell-mode attack-resume that a MANUAL cast arms. A user
    // mashing hand-typed casts (e.g. a room/utility spell) re-stamps the arming
    // each keypress, so each would otherwise fire its own re-attack — the reported
    // manual-cast → attack-spell spam. Engine survival casts are never paced (they
    // resume once per round); this bounds only the hand-typed burst. Timestamp is
    // separate from _lastInterruptResumeAt so it can't perturb the weapon resume.
    private static readonly TimeSpan ManualResumePacing = TimeSpan.FromMilliseconds(2500);
    private DateTimeOffset _lastManualCastResumeAt = DateTimeOffset.MinValue;
    private bool _lastBetweenRoundCastManual;   // was the last between-round cast hand-typed?

    // Timestamp of the last attack we actually sent to the wire (set by
    // NoteAttackSent on every SendAttack). Distinct from _lastInterruptResumeAt,
    // which only tracks resumes: an attack fired by the death→re-observe path
    // (NoteMonsterDied clears the dead target, the room re-picks the survivor and
    // swings) is a SendAttack, not a resume, so it never touched the resume
    // stamp. The interrupt-resume must see it to avoid doubling on top of it.
    private DateTimeOffset _lastAttackSentAt = DateTimeOffset.MinValue;

    // How recently a real attack must have gone out for a pending interrupt-resume
    // to be treated as redundant and skipped. On a kill the server fires *Combat
    // Off*, but the death→re-observe path can re-engage the surviving mob a beat
    // BEFORE that Off is processed; the Off then re-arms _combatOff and the next
    // mob swing line would fire a redundant resume that doubles our attack on the
    // same target (the reported solo double-send). Sized well under a round so a
    // genuine next-round resume (we sat idle after a between-round cast) still
    // fires — only a resume landing right on the heels of a fresh swing is
    // suppressed.
    private static readonly TimeSpan ResumeAfterAttackGuard = TimeSpan.FromMilliseconds(1500);

    // When a between-round CastingDirector cast was last sent (armed by
    // NoteBetweenRoundCast). The *Combat Off* the server fires in response
    // arrives within CastInterruptResumeWindow of this stamp; that lets
    // OnCombatStatus attribute the Off to OUR cast and resume the weapon attack
    // immediately, instead of idling a full round. A per-strike Off from a
    // non-sustaining attack (KAI pummel) lands well outside the window, so it
    // never trips this path.
    private DateTimeOffset _betweenRoundCastAt = DateTimeOffset.MinValue;

    // Which _betweenRoundCastAt stamp the spell-resume branch (below) has
    // already fired a re-announce for. Casting — unlike a weapon swing —
    // itself drops *Combat Off* every time (CONFIRMED mechanic), so the
    // resume's OWN re-announce produces another Off that satisfies the exact
    // same "within CastInterruptResumeWindow of _betweenRoundCastAt" check
    // that fired it. Without this guard that self-triggers every round for
    // the full 3s window — dozens of casts in a burst (report
    // paradigm-20260813-081016: "why did it spam turn like that", triggered
    // by a single legitimate mihe self-heal interrupt). Set to the stamp just
    // resumed for; compared, not cleared, so a genuinely NEW interrupt (a
    // fresh NoteBetweenRoundCast stamp) still resumes exactly once. The
    // weapon-resume branch needs no equivalent guard — a physical swing
    // doesn't itself cause an Off, so it can't retrigger itself this way.
    private DateTimeOffset _lastSpellResumeForBetweenRoundCastAt = DateTimeOffset.MinValue;

    // How recent a between-round cast must be for the next *Combat Off* to count
    // as that cast's interrupt. Generous enough to cover send→Off network
    // latency, far shorter than a round so a later pummel Off can't be
    // misattributed.
    private static readonly TimeSpan CastInterruptResumeWindow = TimeSpan.FromSeconds(3);

    // True when a between-round cast (survival heal / buff) just spent its round
    // while we were mid attack-spell combat, and our attack spell hasn't gone back
    // out since. Exposed to CastingDirector (IsSpellAttackOwed, wired to its
    // attack-owed gate in AppServices) so it declines to fire ANOTHER survival cast
    // until the owed attack round happens — the game allows exactly one cast per
    // round, so back-to-back heal/buff rounds with no attack between them is a
    // scheduling bug, not a losing fight: CastingDirector evaluates before
    // CombatManager every tick and, with nothing to stop it, keeps re-claiming the
    // round the instant HP dips again, which it always will while nothing is
    // fighting back. Weapon-mode combat never sets this — a swing auto-repeats
    // server-side and never competes with CastingDirector for the cast slot, only
    // an attack SPELL does. Set in NoteBetweenRoundCast (only while
    // _castingSpellTarget is live), cleared the moment a real attack goes back out
    // (NoteAttackSent) or the room clears (OnRoomCleared).
    private bool _spellAttackOwed;

    // CastingDirector's view of _spellAttackOwed — the round belongs to the
    // pending attack spell, not another survival cast.
    public bool IsSpellAttackOwed => _spellAttackOwed;

    // A pre-attack DEBUFF fired the combat attack immediately this round
    // (TryPreAttackInBetween → DeferPostDebuffAttack) instead of deferring it to the
    // debuff's *Combat Off*. Every *Combat Off* carrying THIS between-round stamp must
    // then be barred from the resume paths, or the attack fires a second time (the
    // debuff and the attack are independent slots, report paradigm-20260825-103417).
    // Keyed to the stamp, not a one-shot flag, because casting the attack between
    // rounds can itself drop a quick second Off inside CastInterruptResumeWindow — a
    // bool would only catch the first. A later genuine between-round cast re-stamps
    // _betweenRoundCastAt, so its Off no longer matches and resumes normally; reset to
    // MinValue on any target/round reset so a stale stamp can't bar a legit resume.
    private DateTimeOffset _suppressResumeForBetweenRoundStamp = DateTimeOffset.MinValue;

    // When a monster death was last detected this burst (stamped by
    // NoteMonsterDied / NoteUnattributedDeath). The between-round-cast resume
    // must not fire when a kill just happened: a mob's dying *Combat Off* and a
    // between-round heal in the same round both land inside CastInterruptResumeWindow,
    // so the resume would misread the KILL's Off as its cast's interrupt and re-
    // engage — against a roster the kill's forced re-display hasn't yet cleared.
    // That fires an "aa <corpse>" at the emptied room ("Your command had no
    // effect."). Suppressing the resume for this window hands re-engagement to the
    // death→re-display→re-observe path, which re-picks any survivor from the
    // resynced roster (or clears the gate on an empty room).
    private DateTimeOffset _lastDeathAt = DateTimeOffset.MinValue;

    // When a monster death was last detected via a MATCHED DEATH LINE — stamped
    // only by NoteMonsterDied, NOT by the exp+Off fallback below. A matched death
    // already handled the kill and re-picked the next survivor, so the same
    // burst's *Combat Off* must not ALSO trip the exp-inference: doing so drops
    // the freshly-picked survivor and re-attacks it, double-firing "aa <next>"
    // right after the first kill (report paradigm-20260812-215511). Distinct from
    // _lastDeathAt, which the exp-inference itself stamps and so can't self-gate on.
    private DateTimeOffset _lastMatchedDeathAt = DateTimeOffset.MinValue;

    // How long after a detected death a *Combat Off* is treated as the kill's Off
    // rather than a cast interrupt. A death line and its *Combat Off* arrive in the
    // same server burst (milliseconds apart), so this only needs to bridge that gap
    // with margin; kept well under a round so a genuine cast-interrupt resume in the
    // NEXT round (no kill) still fires.
    private static readonly TimeSpan DeathInterruptWindow = TimeSpan.FromSeconds(2);

    // Whether a real attack has gone out since the last death was stamped. The
    // between-round-cast resume suppresses itself for DeathInterruptWindow so the
    // death→re-observe path owns the re-engage from a resynced roster — but only
    // while that path still owes us a swing. Once it has re-picked a survivor and
    // sent one (this flag flips true in NoteAttackSent), the roster is already
    // clean and a later *Combat Off* is our own cast interrupting the fresh swing,
    // not the kill's Off; suppressing it there idles a full round. A boolean, not a
    // _lastAttackSentAt > _lastDeathAt compare, because the death stamp and the
    // re-observe swing can land on the same DateTimeOffset.Now tick.
    private bool _attackSentSinceDeath;

    // Prompt kill signal. A kill emits "You gain N experience." immediately before
    // its *Combat Off* (wire order: death line → exp → Off). Each realm gives its
    // monsters CUSTOM death messages that overlap across species and aren't in our
    // game data, so we can't match a death line to the flavored target we were
    // fighting — the specific-death matcher misses and only the lagging exp+Off
    // fallback correlation (a separate watcher) eventually drops it, a beat AFTER
    // this handler's resume already re-attacked the corpse ("You don't see X
    // here!", then a full round idle). Stamping the exp here lets OnCombatStatus
    // treat an Off that a fresh exp explains — and that NO between-round survival
    // cast explains — as our kill's Off, dropping the dead target before the resume
    // fires. Consumed on use so a stale exp from an earlier identical-exp kill can't
    // mark a later non-kill Off (a thrown weapon emits one per strike).
    private DateTimeOffset _lastExpGainAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan ExpKillWindow = TimeSpan.FromSeconds(3);

    // Exp lines seen this combat round. ≥2 means an AoE (ours or a hand-cast one) killed
    // several mobs at once — the multi-kill signal the roster-wipe path keys on. Reset at
    // the round boundary (OnCombatTick).
    private int _expGainsThisRound;

    // The target a prompt exp-inferred kill just dropped, remembered so the kill's
    // *Combat Off* can still drop it from the live roster. The exp line lands BEFORE
    // *Combat Off* and nulls _currentTarget (so the round's alternate can't corpse-cast)
    // — but that leaves NoteUnattributedDeath, which fires on the Off and removes the
    // dead mob from the roster, with no identity to remove. In a MULTI-mob room the
    // lingering corpse is then re-picked ("aa <dead mob>" → "Your command had no
    // effect."), stalling until the fallback re-display (report paradigm-20260815-081045).
    // Timestamped so a stale value can't remove a later, living mob — honoured only
    // within ExpKillWindow of the Off, exactly like the exp→Off correlation itself.
    private string? _inferredKillPendingRemoval;
    private DateTimeOffset _inferredKillPendingAt = DateTimeOffset.MinValue;

    // Server-confirmed engagement, driven ONLY by the wire *Combat Engaged* /
    // *Combat Off* lines — unlike _combatOff (optimistically cleared on every
    // attack send), this stays a faithful mirror of what the server actually told
    // us. The engage-verification safety net keys off this: an attack we sent
    // that the server never acknowledged with *Combat Engaged* means we swung at
    // a target that isn't in the room we can currently see (stale room view — a
    // movement was in flight, or the named mob walked out / was replaced).
    private bool _engageConfirmed;

    // When non-null, the moment a fresh attack went out for which we are still
    // waiting on *Combat Engaged*. Armed by NoteAttackSent, disarmed on
    // confirmation (or on the room going empty). Once EngageConfirmWindow elapses
    // with no confirmation, VerifyEngagement drops the stale target and fires a
    // bare CR to force a room re-display so we re-pick from what's actually here.
    private DateTimeOffset? _awaitingEngageSince;

    // ----- Weapon-swap decision state ---------------------------------

    // True when we've swapped to the alternate weapon for the current room (a
    // no-effect line fired against the normal weapon vs the current target's
    // species). Cleared on room-cleared.
    private bool _usingAlternateWeapon;

    // Canonical species names that produced a no-effect line against our normal
    // weapon. Room-scoped — cleared on room-cleared so a fresh room re-tries the
    // normal weapon. Keyed to EngageableCandidate.ResolvedName (base species, not
    // the prefixed display name).
    private readonly HashSet<string> _normalWeaponFailedMonsters =
        new(StringComparer.OrdinalIgnoreCase);

    // Canonical species that also produced a no-effect line against our ALTERNATE
    // weapon — the weapon path is then exhausted for that species. Room-scoped and
    // cleared alongside the normal fail-set. Feeds WeaponPathExhausted so a
    // Physical-first round falls back to the attack-spell cascade instead of
    // swinging uselessly.
    private readonly HashSet<string> _alternateWeaponFailedMonsters =
        new(StringComparer.OrdinalIgnoreCase);

    // Monster Number → canonical species (ResolvedName), rebuilt from every room
    // observation. Bridges the Number-keyed actionability check (CanEngageMonster,
    // which the walker gate calls with only a Number) to the species-keyed runtime
    // fail-sets above, so a monster proven un-hittable this room by observed "no
    // effect" lines — not just by game-data prediction — reads as un-actionable.
    // Room-scoped, cleared on room-cleared.
    private readonly Dictionary<int, string> _speciesByNumber = new();

    // Species we've already surfaced a "cannot attack" line for this room — keeps
    // the status message to once per species instead of once per failed round.
    private readonly HashSet<string> _cannotAttackAnnounced =
        new(StringComparer.OrdinalIgnoreCase);

    // Backstab only lands on the surprise round — the very first action taken in
    // a freshly-approached room. Once ANY combat action fires here (bs, spell, or
    // swing) the surprise is spent, so re-picking `bs` on a re-engage (interrupt
    // resume, target re-pick) would whiff a wasted round. Set true after the first
    // dispatch in a room; reset false on a genuine room change — both the pre-move
    // hook (PrepBackstabForMove, when a movement engine drives) AND the classifier's
    // RoomChange observation in OnEntitiesObserved (which fires on every confirmed
    // transition, so manual hand-walking re-opens the surprise round too). Gates
    // BackstabPending.
    private bool _backstabOpenerConsumed;

    // AttackTiming re-fire coalescing. A combat round resolves the whole party's
    // auto-attacks at once, so several "moves to attack <our-target>" announces
    // arrive as one burst (one server flush → one synchronous line batch). Firing
    // a re-fire per announce would send several redundant attack commands that
    // round; we only need ONE, landing after the last announce so our swing sits
    // last in initiative. HandleAttackOrderRefire records the pending
    // target/announcer and posts a single flush to the next dispatcher turn — it
    // runs after the whole burst has been processed, collapsing N announces into
    // one send. The flush is near-immediate (next turn), so it still lands well
    // inside the ~5s round; we never hold the attack toward the round tick.
    private bool _refireFlushScheduled;
    private string? _refireTarget;
    private string? _refireAnnouncer;

    public CombatManager(
        MessageRouter router,
        RoomEntityClassifier classifier,
        MonsterMessageStore monsters,
        Func<int, MonsterOverlay> resolveOverlay,
        PartyState party,
        Func<CombatSettings> readSettings,
        Func<bool> isEnabled,
        Func<string?> readOwnGivenName,
        Action<Action> post,
        LogService? log = null,
        Func<PartySettings>? readPartySettings = null,
        Func<string, int?>? roomAwareResolve = null,
        Func<string, Game.Spells.KnownSpell?>? resolveSpellByCode = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(monsters);
        ArgumentNullException.ThrowIfNull(resolveOverlay);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(readSettings);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(readOwnGivenName);
        ArgumentNullException.ThrowIfNull(post);
        _post         = post;
        _classifier   = classifier;
        _monsters     = monsters;
        _resolveOverlay = resolveOverlay;
        _roomAwareResolve = roomAwareResolve;
        _resolveSpellByCode = resolveSpellByCode;
        _party        = party;
        _readSettings = readSettings;
        _isEnabled    = isEnabled;
        _readOwnGivenName = readOwnGivenName;
        _readPartySettings = readPartySettings;
        _log = log;

        _classifier.EntitiesObserved += OnEntitiesObserved;
        _announceSub  = router.Subscribe(KnownPatterns.PartyAttackAnnounce, OnAttackAnnounce);
        _castAnnounceSub = router.Subscribe(KnownPatterns.PartyCastAnnounce, OnCastAnnounce);
        _userHitsSub  = router.Subscribe(KnownPatterns.UserHits,  OnCombatLine);
        _mobHitsSub   = router.Subscribe(KnownPatterns.MobHits,   OnCombatLine);
        _mobMissesSub = router.Subscribe(KnownPatterns.MobMisses, OnCombatLine);
        _targetGoneSub = router.Subscribe(KnownPatterns.TargetNotHere, OnTargetNotHere);
        _weaponNoEffectSub = router.Subscribe(KnownPatterns.WeaponNoEffect, OnWeaponNoEffect);
        _fistsNoEffectSub  = router.Subscribe(KnownPatterns.FistsNoEffect,  OnFistsNoEffect);
        _spellNoEffectSub  = router.Subscribe(KnownPatterns.SpellNoEffect,  OnSpellNoEffect);
        _commandNoEffectSub = router.Subscribe(KnownPatterns.CommandNoEffect, OnCommandNoEffect);
        _combatStatusSub   = router.Subscribe(KnownPatterns.CombatStatus,   OnCombatStatus);
        _expGainSub        = router.Subscribe(KnownPatterns.UserGainExperience, OnUserGainExperience);
        _monsterProtectSub = router.Subscribe(KnownPatterns.MonsterMovesToProtect, OnMonsterProtect);

        // Backstab surprise-round resolution rides on our own hit / miss lines.
        // Separate subscriptions from OnCombatLine (fan-out) so the resolution
        // watch doesn't perturb the resume/refresh net. UserMisses is otherwise
        // unsubscribed here; the handler self-gates on _awaitingBackstabResolution,
        // so its breadth (it also matches self-emotes) is harmless.
        _bsResolveHitsSub   = router.Subscribe(KnownPatterns.UserHits,   OnBackstabResolutionLine);
        _bsResolveMissesSub = router.Subscribe(KnownPatterns.UserMisses, OnBackstabResolutionLine);

        // High tieBreak so this runs ahead of TickEngine's own UserHits subscription
        // (default 0) — ConfirmedAttackCastCount must already reflect a landed cast
        // by the time that same line's cascade reaches OnCombatTick's re-decide. Both
        // hits AND misses count — MaxCasts caps cast ATTEMPTS (what the server auto-
        // repeats each round), not just successful hits, so a spell that keeps
        // whiffing must still hit its cap instead of casting forever unpunished.
        _attackConfirmHitsSub   = router.Subscribe(KnownPatterns.UserHits,   OnAttackCastConfirmed, tieBreak: 50);
        _attackConfirmMissesSub = router.Subscribe(KnownPatterns.UserMisses, OnAttackCastConfirmed);
    }

    // Bind the wire sender — typically the TelnetClient.SendAsync wrapper that
    // MainWindowViewModel exposes. Until set, CombatManager silently no-ops on
    // its outbound side (state transitions still log).
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Test-only clock override for AlternationAdvanceMinGap — see _now.
    public void SetClock(Func<DateTimeOffset> now)
    {
        ArgumentNullException.ThrowIfNull(now);
        _now = now;
    }

    // Wire the recovery anchor for a blocked initial combat-spell dispatch. The
    // next projected combat round then drives OnCombatTick even in a fresh session
    // where no hit/miss line has seeded TickEngine yet.
    public void SetCombatTickAnchor(Action ensureAnchor)
    {
        ArgumentNullException.ThrowIfNull(ensureAnchor);
        _ensureCombatTickAnchor = ensureAnchor;
    }

    // Wire the dark-room probe (RoomTracker.IsInDarkRoom). With it set, the CR
    // "where am I" room re-displays are suppressed while we can't see — see
    // _isInDarkRoom / TrySendRoomRefresh. Until set, refreshes send unconditionally.
    public void SetDarkRoomProbe(Func<bool> isInDarkRoom)
    {
        ArgumentNullException.ThrowIfNull(isInDarkRoom);
        _isInDarkRoom = isInDarkRoom;
    }

    // Wire the movement-active probe (EngineRecoveryGate.AttachedEngine is not
    // null) — see _isMovementActive. Until set, the Min/Max monsters gate
    // applies unconditionally, its original behavior.
    public void SetMovementActiveGate(Func<bool> isMovementActive)
    {
        ArgumentNullException.ThrowIfNull(isMovementActive);
        _isMovementActive = isMovementActive;
    }

    // Wire the attack-prevented probe (ConditionTracker.IsAttackPrevented). While
    // it returns true the engine issues no attack — see _attacksPrevented /
    // AttacksBlocked. Until set, attacks never block (the pre-flag behavior).
    public void SetAttackPreventedGate(Func<bool> isAttackPrevented)
    {
        ArgumentNullException.ThrowIfNull(isAttackPrevented);
        _attacksPrevented = isAttackPrevented;
    }

    // True while an AttackPrevented condition is active: a message flagged
    // AttackPrevented latched (stun / petrify / bind / …) and its wear-off hasn't
    // fired. The server refuses every attack command in that state — weapon AND
    // spell — so every attack chokepoint below (SendAttack, the combat-spell and
    // pre-attack-debuff casts, the fumble re-send) consults this and holds,
    // retrying each round until it clears. Edge-logged at Combat level so a report
    // shows exactly when the hold began and lifted.
    private bool AttacksBlocked()
    {
        bool blocked = _attacksPrevented?.Invoke() == true;
        if (blocked != _attackBlockLogged)
        {
            _attackBlockLogged = blocked;
            _log?.Combat(LogCategory, blocked
                ? "attacks held — AttackPrevented condition active"
                : "attacks resumed — AttackPrevented condition cleared");
        }
        return blocked;
    }

    // Wire the gear actuator (EquipmentManager, the sole gear owner). swapWeapon
    // equips a weapon + off-hand immediately — the unpaced fast path that must
    // land before the next swing; prepBackstabArmor applies the Backstab set's
    // armor synchronously in the pre-move sequence, before the sn (null when no
    // backstab-armor automation is wired — the weapon still gets swapped either
    // way).
    public void SetWeaponActuator(
        Action<string?, string?, bool> swapWeapon, Action? prepBackstabArmor = null)
    {
        ArgumentNullException.ThrowIfNull(swapWeapon);
        _swapWeapon = swapWeapon;
        _prepBackstabArmor = prepBackstabArmor;
    }

    // The monster name we last sent `attack` against, or null when no fight is in
    // flight.
    public string? CurrentTarget => _currentTarget;

    // True while combat has deliberately swapped to the alternate weapon for the
    // monster it's fighting (a magic-required mob, or a normal-weapon-no-effect
    // fallback). The auto-equip gear-set triggers consult this so a Default-set
    // apply on combat-entry doesn't slam the normal weapon back on and undo the
    // per-monster swap — the reported "swaps to alternate, then back to normal,
    // then to alternate again" flapping. Reverts to false when the room clears
    // (OnRoomCleared) or a fresh weapon pick lands on the normal weapon.
    public bool IsWeaponOverrideActive => _usingAlternateWeapon;

    // Immutable view of the combat decision state, for the bug-report
    // engine-state dump. The believed-worn weapon is no longer shadowed here —
    // live inventory is authoritative (the actuator diffs against it), so the
    // report reads the worn weapon straight from the inventory snapshot instead.
    public readonly record struct DebugState(
        string? CurrentTarget,
        bool UsingAlternateWeapon,
        bool AwaitingBackstabResolution,
        string? PendingBackstabSpecies,
        string? GuardBlockedTarget,
        int AlternationRound,
        // The round's committed combat-spell action + the cast-code the server is
        // auto-repeating (null in weapon / idle mode). "DrainSpell" here shows the
        // drain override is the active action — the key runtime tell a drain report
        // needs alongside the resolved Combat settings + live HP.
        string? LastCastAction,
        string? AnnouncedSpell,
        // Passive neutrals the user hand-engaged (turned hostile), which the engine is
        // now finishing like enemies. A "why isn't it fighting the neutral I attacked?"
        // report needs to see whether the instance actually got marked.
        IReadOnlyList<string> UserEngagedInstances,
        // The attack-spell cascade's own latched state — surfaced separately from
        // CurrentTarget/AnnouncedSpell because these two can legitimately go stale
        // relative to it (report paradigm-20260824-012300: a spellTarget on a
        // monster long gone, with IsSpellAttackOwed stuck true, silently blocked
        // every automatic heal/cure/bless). A report showing a CastingSpellTarget
        // that doesn't match CurrentTarget, or SpellAttackOwed=true with no live
        // fight, is this bug re-occurring.
        string? CastingSpellTarget,
        bool SpellAttackOwed);

    // UI-thread only (router handlers + the capture both run there), so no lock.
    public DebugState Snapshot() => new(
        _currentTarget, _usingAlternateWeapon,
        _awaitingBackstabResolution, _pendingBackstabSpecies,
        _guardBlockedTarget, _alternationRound,
        _lastCastAction?.ToString(), _announcedSpellCode,
        _userEngagedInstances.ToArray(),
        _castingSpellTarget, _spellAttackOwed);

    // Wire the backstab gating delegates: isStealthed reports whether the character
    // holds any stealth that opens a backstab — sneaking OR (optimistically) hidden
    // (StealthManager.IsStealthed) — and hasSeeHidden reports whether a given
    // monster Number carries the SeeHidden ability (SeeHiddenIndex). Until set,
    // backstab never fires — the engine sends the normal attack regardless of
    // CombatSettings.DoBackstab.
    public void SetBackstabHooks(Func<bool> isStealthed, Func<int, bool> hasSeeHidden)
    {
        ArgumentNullException.ThrowIfNull(isStealthed);
        ArgumentNullException.ThrowIfNull(hasSeeHidden);
        _isStealthed = isStealthed;
        _hasSeeHidden = hasSeeHidden;
    }

    // Wire the "backstab failed → flee" action — bound in AppServices to
    // HealthManager.RunFromBackstabFailure. Invoked only when the surprise round
    // is detected as failed (no `surprise` on the first swing) AND
    // CombatSettings.RunIfBackstabFails is on. TryFlee itself no-ops without an
    // active movement engine, so a hand-walked failure just logs.
    public void SetBackstabFailureFlee(Action flee)
    {
        ArgumentNullException.ThrowIfNull(flee);
        _backstabFailureFlee = flee;
    }

    // Wire the combat-off "clear hostiles when seen Hidden" override:
    // seeHiddenClearActive reports whether CombatStateTracker has latched a
    // force-clear for the current room (stealth runner hit a SeeHidden monster
    // with the toggle on). The tracker owns the decision + latch — it fires first
    // on the shared observation and also holds the walker gate so we actually
    // stop to fight. When it returns true and combat is OFF, the engine engages
    // anyway and bypasses the Min/Max gate to clear the whole room. Until set,
    // the override never fires.
    public void SetSeeHiddenClearGate(Func<bool> seeHiddenClearActive)
    {
        ArgumentNullException.ThrowIfNull(seeHiddenClearActive);
        _seeHiddenClearActive = seeHiddenClearActive;
    }

    // Wire HealthManager's engage-to-clear-a-rest-blocker signal (see _restClearActive).
    public void SetRestClearGate(Func<bool> restClearActive)
    {
        ArgumentNullException.ThrowIfNull(restClearActive);
        _restClearActive = restClearActive;
    }

    // Re-run the room engage while the rest-clear override is active — HealthManager
    // pokes this when a hostile is blocking a needed rest with Auto-Combat OFF, to
    // fire the first attack (the server then auto-repeats it). No-op unless the
    // override is still asserted, so a stale poke can't engage after recovery.
    public void RequestRestClearEngage()
    {
        if (_disposed) return;
        if (_restClearActive?.Invoke() != true) return;
        if (_classifier.Current is { } obs) OnEntitiesObserved(obs);
    }

    // Wire the ShadowRest combat hold: shadowRestHolding reports whether
    // HealthManager is mid-ShadowRest-recovery (a solo, stealthed ShadowRest
    // character resting toward rest-max — see GAME_MECHANICS "ShadowRest"). While
    // it returns true we stand down before dispatching a round so the character
    // stays hidden and the rest isn't broken by our own swing; the opener stays
    // unspent. HealthManager fires ResumeAfterShadowRest once recovery tops off,
    // which re-runs the room and opens with the held-back backstab. Until set, the
    // hold never engages.
    public void SetShadowRestSuppression(Func<bool> shadowRestHolding)
    {
        ArgumentNullException.ThrowIfNull(shadowRestHolding);
        _shadowRestHolding = shadowRestHolding;
    }

    // Wire the passive-neutral recovery hold: while recoveryPending is true (below
    // the rest trigger) and no on-sight attacker is in the room, stand down before
    // engaging a KillOnSight NEUTRAL and clear InCombat so HealthManager rests to
    // rest-max first — a neutral never attacks until we hit it, so the un-engaged
    // ones are harmless meanwhile. HealthManager fires ResumeAfterRecovery when the
    // rest tops off. Enemies (on-sight attackers) are never held — hasAttackingHostile
    // short-circuits it. Until set, the hold never engages.
    public void SetNeutralRecoveryHold(
        Func<bool> recoveryPending, Func<bool> hasAttackingHostile, Action clearInCombat)
    {
        ArgumentNullException.ThrowIfNull(recoveryPending);
        ArgumentNullException.ThrowIfNull(hasAttackingHostile);
        ArgumentNullException.ThrowIfNull(clearInCombat);
        _recoveryPending = recoveryPending;
        _hasAttackingHostile = hasAttackingHostile;
        _clearInCombatForHold = clearInCombat;
    }

    // Resume combat after a recovery hold completes — re-run the last observation so
    // the held KillOnSight neutral re-picks now that we're topped off. Gated on the
    // hold flag so an ordinary recovery (no held neutral) doesn't churn.
    public void ResumeAfterRecovery()
    {
        if (!_isEnabled()) return;
        if (!_holdingForRecovery) return;
        _holdingForRecovery = false;
        if (_classifier.Current is { } live) OnEntitiesObserved(live);
    }

    // Whether a picked target is a Neutral-relationship monster — a KillOnSight
    // neutral (an un-tagged neutral would never have been picked as engageable). Used
    // by the recovery hold so only neutrals defer for rest; enemies always engage.
    private bool IsNeutral(EngageableCandidate e)
        => (ResolveOverlay(e.MonsterNumber).Relationship ?? MonsterRelationship.Enemy)
           == MonsterRelationship.Neutral;

    // Resume normal combat after a ShadowRest recovery completes — re-run the last
    // observation so the target re-picks and the backstab opener fires now that the
    // hold has lifted. No-op when combat is off or no observation is cached.
    public void ResumeAfterShadowRest()
    {
        if (!_isEnabled()) return;
        if (_classifier.Current is { } live) OnEntitiesObserved(live);
    }

    // Called by the MonsterDeath subscriber when a death-line match resolves to a
    // monster whose name might be ours. deadMonsterName is the base / display
    // name of the dead monster, lifted from the matched death-line's
    // MonsterDeathIdentity.Name. Clears _currentTarget when the dead monster
    // shares a name with our current target (either the raw / unflavored case
    // where two same-name mobs occupy the room, or the flavored case where the
    // resolved species matches). Without this, the next OnEntitiesObserved sees
    // another live entity with the same RawName still in the engageable list and
    // short-circuits ("server still swinging") — so we'd never re-issue `attack`
    // against the surviving instance, and CombatManager goes silent while the
    // other rats keep biting.
    public void NoteMonsterDied(string deadMonsterName)
    {
        if (string.IsNullOrEmpty(deadMonsterName)) return;

        // A kill just landed — mark it so the between-round-cast resume treats the
        // imminent *Combat Off* as the kill's Off, not its own cast's interrupt
        // (see _lastDeathAt). Stamped regardless of whether this death matches our
        // target: any death in the room means a re-observe is coming that owns the
        // re-engage.
        _lastDeathAt = DateTimeOffset.Now;
        _lastMatchedDeathAt = DateTimeOffset.Now;   // death LINE matched — gates the exp-inference off
        _attackSentSinceDeath = false;

        // The guarded priority itself fell — the redirect chase is over. Drop the
        // memory so no stray guard-retry fires "aa <priority>" at the corpse. Runs
        // before the _currentTarget guard below because a roster-desync room-clear
        // may have already nulled _currentTarget while the guard block is still set.
        if (_guardBlockedTarget is { } blocked &&
            string.Equals(blocked, deadMonsterName, StringComparison.OrdinalIgnoreCase))
        {
            _log?.Combat(LogCategory, $"guard priority '{blocked}' died — clearing guard block");
            _guardBlockedTarget = null;
        }

        if (_currentTarget is not { } current) return;

        // Direct RawName match — the unflavored case. Two "giant rat"
        // entries: `_currentTarget == "giant rat"` and the dead-line
        // gave us "giant rat". Whichever instance the server was
        // swinging at is the dead one; the other doesn't auto-engage.
        if (string.Equals(current, deadMonsterName, StringComparison.OrdinalIgnoreCase))
        {
            _log?.Combat(LogCategory,
                $"target died — clearing _currentTarget='{current}' (raw-name match)");
            _currentTarget = null;
            _castingSpellTarget = null;   // end spell mode on the kill, mirroring the weapon path — no corpse re-cast
            _spellChooser.ResetForNewTarget();   // a kill resets the cascade so the next mob (even same-named) reconsiders the normal spell
            ClearBackstabResolution();
            return;
        }

        // Resolved-name match — the flavored case. _currentTarget is
        // "angry kobold thief" (RawName); the dead-line resolves to
        // "kobold thief" (ResolvedName). The classifier's current
        // observation is the source of truth for the raw → resolved
        // mapping. Look up the entity matching our RawName and
        // compare its ResolvedName.
        if (_classifier.Current is { } obs)
        {
            for (int i = 0; i < obs.Entities.Count; i++)
            {
                RoomEntity e = obs.Entities[i];
                if (e.Kind != EntityKind.Monster) continue;
                if (!string.Equals(e.RawName, current, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(e.ResolvedName, deadMonsterName, StringComparison.OrdinalIgnoreCase)) continue;
                _log?.Combat(LogCategory,
                    $"target died — clearing _currentTarget='{current}' (resolved-name match)");
                _currentTarget = null;
                _castingSpellTarget = null;   // end spell mode on the kill, mirroring the weapon path — no corpse re-cast
                _spellChooser.ResetForNewTarget();   // a kill resets the cascade so the next mob (even same-named) reconsiders the normal spell
                ClearBackstabResolution();
                return;
            }
        }
    }

    // Force our combat target to the named monster and engage it this round,
    // regardless of the master auto-attack switch — the explicit-engage path
    // behind the @kill <target> remote command. When the named monster is in our
    // live room view, the round is dispatched through the full per-round chooser
    // so the configured weapon swap / attack-spell / backstab selection apply
    // exactly as they would for any single target. When we have no room view (or
    // the name isn't in it), we send a literal `attack <name>` and let the server
    // resolve the instance. No-op on a blank name.
    public void RetargetTo(string monsterName)
    {
        if (string.IsNullOrWhiteSpace(monsterName)) return;
        string target = monsterName.Trim();
        CombatSettings settings = _readSettings();

        if (_classifier.Current is { } liveObs &&
            TryBuildCandidate(liveObs, target) is { } cand)
        {
            _log?.Combat(LogCategory, $"@kill retarget → {cand.RawName}");
            DispatchRoundAction(settings, cand, CountEngageable(liveObs), liveObs);
            return;
        }

        // No room view (or the name isn't in it) — literal attack; the server
        // resolves the instance. Set _currentTarget so the re-fire / round
        // bookkeeping tracks it just like a chooser-dispatched engage.
        _currentTarget = target;
        SendAttack(settings.NormalAttackCommand, target, refire: true,
                   refireReason: "@kill retarget");
    }

    // Inject the UI-thread one-shot that defers a simultaneous-arrival burst's first
    // engage. Shaped like the walker's voyage scheduler so the Game layer stays
    // UI-free and tests drive a fake clock. Wired in AppServices to a DispatcherTimer;
    // left null in tests that don't exercise the settle (they keep the immediate path).
    public void SetArrivalSettleScheduler(Action<TimeSpan, Action> schedule)
        => _scheduleArrivalSettle = schedule;

    // The one-shot delay scheduler for the cascade switch dispatch (see
    // SwitchDispatchDelay). Wired in AppServices to a DispatcherTimer; tests inject a
    // controllable seam.
    public void SetSwitchDispatchScheduler(Action<TimeSpan, Action> schedule)
        => _scheduleSwitchDispatch = schedule;

    // The arrival-settle window elapsed with no authoritative room re-display having
    // superseded it. Re-run the engage decision against whatever the room now holds —
    // the whole burst has landed, so a room that met the multi-attack threshold rooms
    // on this first action. No-ops if a room display already engaged us (arm cleared)
    // or the room emptied out from under us.
    private void OnArrivalSettleElapsed()
    {
        if (_disposed || !_arrivalSettleArmed) return;
        _arrivalSettleArmed = false;
        if (_classifier.Current is not { } cur) return;
        _log?.Combat(LogCategory,
            "arrival-settle: window elapsed with no room re-display — engaging the accumulated room");
        _arrivalSettleBypass = true;
        try { OnEntitiesObserved(cur); }
        finally { _arrivalSettleBypass = false; }
    }

    private void OnEntitiesObserved(RoomEntitiesObservation obs)
    {
        CombatSettings settings = _readSettings();

        // Fresh observation of the room — any pending follow-deferral from a
        // previous observation is stale. Re-evaluated below once we know the
        // engageable set (a re-observe of the SAME room re-arms it if still apt).
        _awaitingFollowAnnounce = false;

        // Prune the user-engaged-neutral overrides for instances no longer in the
        // room (killed, or we changed rooms) so the takeover can't leak onto a
        // freshly-arrived same-named mob. Full-roster observations only — an arrival
        // line never removes a monster, so pruning on it could wrongly drop a
        // still-present engaged neutral mid-burst.
        if (_userEngagedInstances.Count > 0 && obs.Source != RoomObservationSource.Arrival)
        {
            HashSet<string> present = new(StringComparer.OrdinalIgnoreCase);
            foreach (RoomEntity e in obs.Entities)
                if (e.Kind == EntityKind.Monster) present.Add(e.RawName);
            _userEngagedInstances.RemoveWhere(r => !present.Contains(r));
        }

        // An authoritative non-arrival observation (the room re-display, a room
        // change, a death resync) supersedes a pending simultaneous-arrival settle:
        // it carries the full roster, so it drives the decision now and the settle
        // callback that fires later finds nothing armed and no-ops. Skipped on the
        // settle's own re-entrant call (bypass) so it can proceed to dispatch.
        if (!_arrivalSettleBypass && obs.Source != RoomObservationSource.Arrival)
            _arrivalSettleArmed = false;

        // A confirmed room change re-opens the surprise round for the room we're
        // entering. The pre-move hook (PrepBackstabForMove) already resets the
        // opener when a movement engine drives the walk, but hand-walking leaves
        // that hook silent — the classifier's synthetic RoomChange wipe is the one
        // signal that fires on EVERY transition, so keying the reset off it makes
        // manual moves re-arm the backstab too. Runs before the AlsoHere emit for
        // the new room, so the opener is already false when that dispatch reads
        // BackstabPending. Flag-only; the stealth gate still decides whether bs fires.
        if (obs.Source == RoomObservationSource.RoomChange)
        {
            _backstabOpenerConsumed = false;
            // The old room's surprise round is moot once we've moved on — drop any
            // unresolved watch so a missed resolution line can't strand the re-fire
            // suppression across rooms.
            ClearBackstabResolution();
            // A guarded priority we couldn't reach is left behind on a room change —
            // drop the redirect memory so we don't re-attack it into the new room.
            _guardBlockedTarget = null;
        }

        // See-hidden force-clear override for stealth runners — honoured with
        // auto-attack ON or OFF. A stealth character sprinting a walk-to route
        // (AutoSneak on) that hits a room with a SeeHidden monster can't re-sneak
        // there, and running onward would drag/stack monsters across rooms (lethal
        // solo). CombatStateTracker owns the decision + latch (and holds the walker
        // gate so we actually stop); when it's force-clear latched we bypass the
        // Min/Max gate below to clear EVERYTHING so the route can resume sneaking.
        // With combat OFF the latch additionally makes us engage despite being
        // disabled; with combat ON we'd engage anyway, and the latch's job is only
        // the Min/Max bypass.
        // Rest-blocker force-clear (report paradigm-20260901-093301): a hostile
        // blocking a needed rest with Auto-Combat OFF makes us engage-to-clear it
        // anyway, on the same footing as the see-hidden override — both bypass the
        // disabled gate here and the Min/Max gate below.
        bool forceClearOverride = _seeHiddenClearActive?.Invoke() == true
                                  || _restClearActive?.Invoke() == true;
        if (!_isEnabled() && !forceClearOverride)
        {
            _currentTarget = null;
            // AutoCombat going off mid-fight must drop the attack-spell cascade
            // too, not just the target — CastingDirector's IsSpellAttackOwed gate
            // (armed by NoteBetweenRoundCast while _castingSpellTarget is live)
            // otherwise survives the toggle and silently blocks every automatic
            // heal/cure/bless until something else happens to clear it (report
            // paradigm-20260824-012300: a stale spellTarget on a monster long
            // gone suppressed grhe for the rest of the session).
            ClearAttackSpellCascadeState();
            return;
        }

        // Score every Monster entity once. We need BOTH names:
        //   RawName       — full prefixed form ("angry kobold thief"),
        //                   used on the wire so the server engages the
        //                   specific instance, not whichever
        //                   "<adj> kobold thief" it happens to pick.
        //   ResolvedName  — base form ("kobold thief"), used for the
        //                   in-room counting / re-pick logic when the
        //                   server auto-continues against the same
        //                   base across multiple identical instances.
        List<EngageableCandidate> engageable = new();
        for (int i = 0; i < obs.Entities.Count; i++)
        {
            RoomEntity e = obs.Entities[i];
            if (e.Kind != EntityKind.Monster) continue;

            if (e.MonsterNumber is int n)
            {
                // Remember the Number→species mapping for the runtime actionability
                // bridge (CanEngageMonster), independent of relationship so the
                // gate can resolve any observed monster.
                _speciesByNumber[n] = e.ResolvedName;

                MonsterOverlay overlay = ResolveOverlay(n);
                if (!MonsterEngagement.IsEngageable(overlay, _userEngagedInstances.Contains(e.RawName)))
                    continue;
                // Engageability is Relationship-based ONLY. Earlier we
                // also required MonsterMessageRecord.DeathLine non-empty
                // as a "killable" proxy, but 152 of 1100 monsters in the
                // stock data set ship with empty DeathLine (incomplete
                // data, not actually unkillable — acid slime, etc.). The
                // overlay seed marks the real friendlies explicitly; if
                // a monster is Enemy / unmarked, it's a target.

                engageable.Add(new EngageableCandidate(
                    RawName:         e.RawName,
                    ResolvedName:    e.ResolvedName,
                    MonsterNumber:   n,
                    Priority:        overlay.Priority ?? MonsterAttackPriority.Normal,
                    AppearanceIndex: i,
                    DontBackstab:    overlay.DontBackstab ?? false));
            }
            else
            {
                // A monster the server coloured hostile whose name doesn't
                // resolve to a game-data record — e.g. a colour-stripped
                // arrival "dragon serpent" that misses the colour-prefixed
                // "red/white dragon serpent" records. HasEngageable already
                // fail-opens on a null number to hold the Combat gate (walker
                // pause); the attacker MUST follow through or the walker stops
                // on a monster we then never hit and the player just gets
                // pummelled. Sentinel -1 makes every number-keyed helper
                // (UnengageableReason, per-monster overrides, DontBackstab,
                // weapon-swap) fail open, so it's attacked with the normal
                // weapon by RawName at Normal priority.
                engageable.Add(new EngageableCandidate(
                    RawName:         e.RawName,
                    ResolvedName:    e.ResolvedName,
                    MonsterNumber:   -1,
                    Priority:        MonsterAttackPriority.Normal,
                    AppearanceIndex: i,
                    DontBackstab:    false));
            }
        }

        if (engageable.Count == 0)
        {
            if (_currentTarget is not null)
            {
                // Dump the observation's entity breakdown so the user
                // can see WHY we think the room is empty — Unknown
                // (classifier doesn't recognise the name → not a Monster
                // kind) or Relationship not-Enemy (friendly NPC). A Monster
                // with a null number is NOT counted here: it's now engaged
                // fail-open above, so it can never leave the room empty. The
                // "wasted re-attack mid-combat" symptom usually means one of
                // these caused a spurious empty observation that null-ed the
                // target between rounds.
                int total = obs.Entities.Count;
                int unknownCount = 0;
                int friendlyCount = 0;
                foreach (RoomEntity e in obs.Entities)
                {
                    if (e.Kind != EntityKind.Monster) unknownCount++;
                    else if (e.MonsterNumber is int mn)
                    {
                        MonsterOverlay ov = ResolveOverlay(mn);
                        if (!MonsterEngagement.IsEngageable(ov, _userEngagedInstances.Contains(e.RawName)))
                            friendlyCount++;
                    }
                }
                _log?.Combat(LogCategory,
                    $"room cleared — was=target={_currentTarget} " +
                    $"source={obs.Source} " +
                    $"obs-entities={total} (unknown={unknownCount} " +
                    $"friendly={friendlyCount})");
            }
            _currentTarget = null;
            // Room genuinely empty — no pending swing can be confirmed, so
            // disarm the engage-verify net (otherwise the next tick would
            // fire a spurious CR into an empty room).
            _awaitingEngageSince = null;
            OnRoomCleared(settings);
            return;
        }

        // Min/Max monsters gate — skip the room entirely when the
        // engageable count falls outside [Min, Max]. Default settings
        // (Min=0, Max=20) are effectively no-op. The user opts in by
        // tightening either bound. Inverted config (Min > Max) is
        // treated as "no gate" with a single log-once warning rather
        // than silently never engaging. The SeeHidden clear-override
        // bypasses the gate entirely — its whole point is clearing the
        // WHOLE room regardless of count so re-sneak is possible. The rest-blocker
        // force-clear (folded into forceClearOverride) bypasses it for the same
        // reason: kill whatever is blocking rest, regardless of room population.
        //
        // The whole point of this gate is "don't STOP here while passing
        // through" — it only makes sense while something is actually trying
        // to move us past the room (a walker / loop / auto-lair route). Gated
        // on _isMovementActive so it never applies while genuinely idle (no
        // engine attached — logged in and standing, or manually parked): with
        // nowhere to go anyway, standing undefended is strictly worse than
        // fighting back regardless of room population (the reported "logged
        // in, it buffed, but sat there doing nothing" against a 5-zombie room
        // with Max=2 — releasing the walker gate doesn't help when there's no
        // walker running to begin with). Unwired _isMovementActive fails open
        // to the gate always applying, matching this check's original,
        // unconditional behavior.
        if (!forceClearOverride && (_isMovementActive?.Invoke() ?? true))
        {
            int min = Math.Max(0, settings.MinMonstersInRoom);
            int max = settings.MaxMonstersInRoom > 0 ? settings.MaxMonstersInRoom : int.MaxValue;
            // In an active party the Party-tab cap overrides the Combat
            // upper bound (the lower bound stays Combat-owned).
            if (_party.IsInParty && _readPartySettings?.Invoke() is { MaxMonstersWhenPartying: > 0 } ps)
                max = ps.MaxMonstersWhenPartying;
            if (min > max)
            {
                // Misconfig — treat as off and warn once per room observation.
                _log?.Warn(LogCategory,
                    $"MinMonsters={min} > MaxMonsters={max} — gate disabled for this observation");
            }
            else if (engageable.Count < min || engageable.Count > max)
            {
                _log?.Combat(LogCategory,
                    $"min/max gate skip — count={engageable.Count} window=[{min}..{max}]");
                // Clear target so we don't keep swinging at an old pick
                // that's now out-of-window after a kill.
                _currentTarget = null;
                return;
            }
        }

        // Simultaneous-arrival settle. When several monsters stride in on the same
        // wire flush ("A goblin strides in." ×3 then the room re-displays), each
        // "strides in" line fires its own arrival observation. Engaging the FIRST
        // one commits us to a single-target action, and the full-room re-display
        // that follows can't upgrade it — the "already engaged" guard below sends us
        // straight back out — so a room that met the multi-attack threshold gets
        // pecked with single-target casts instead of nuked. Hold the first engage of
        // a burst for a short window: the authoritative room re-display (AlsoHere) or
        // the accumulated arrivals then drive one decision against the whole group,
        // so a room that qualifies rooms on its first action. Only the initial engage
        // debounces (_currentTarget is null); a mob wandering in mid-fight is handled
        // by the guard / heartbeat as before, and a lone spawn just engages a beat
        // later. No-op until a scheduler is wired (tests without one keep the old
        // immediate path); the settle callback re-enters with _arrivalSettleBypass so
        // it dispatches instead of re-arming.
        if (obs.Source == RoomObservationSource.Arrival && _currentTarget is null
            && _scheduleArrivalSettle is not null && !_arrivalSettleBypass)
        {
            if (!_arrivalSettleArmed)
            {
                _arrivalSettleArmed = true;
                _log?.Combat(LogCategory,
                    $"arrival-settle: holding first engage {ArrivalSettleWindow.TotalMilliseconds:F0}ms " +
                    "so a simultaneous burst + room re-display decide together");
                _scheduleArrivalSettle(ArrivalSettleWindow, OnArrivalSettleElapsed);
            }
            return;
        }

        // Sort by Priority asc (First=0 highest, Last=4 lowest), then
        // by appearance order for stable tiebreak.
        engageable.Sort((a, b) =>
        {
            int p = a.Priority.CompareTo(b.Priority);
            return p != 0 ? p : a.AppearanceIndex.CompareTo(b.AppearanceIndex);
        });

        // Server auto-attacks the specific named target each round;
        // re-sending the same command mid-fight would burn a swing.
        // If the exact RawName we last sent is still in the engageable
        // list, keep going — the server is still swinging at it. A resume
        // re-dispatch (ResumeEngage) bypasses this: combat was turned OFF by the
        // interrupt, so the server is NOT swinging and the round must be re-sent.
        if (!_resumeBypassEngagedGuard &&
            _currentTarget is { } current &&
            engageable.Any(e => string.Equals(e.RawName, current,
                                              StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        // Target-Priority follow deferral: when we're partied in a multi-mob room
        // and configured to follow the leader's / a member's target, hold our own
        // pick and wait for that player's announce (TryFollowTargetPriority engages
        // their target; OnCombatTick falls back to our own pick if none arrives).
        // Skipped during the fallback's re-entrant call (_followDeferBypass) and
        // once we already hold a target (mid-fight re-observe — don't stall).
        if (!_followDeferBypass && _currentTarget is null &&
            ShouldWaitForFollow(settings, engageable.Count, obs))
        {
            _awaitingFollowAnnounce = true;
            _log?.Combat(LogCategory,
                $"target-priority {settings.TargetPriority} — holding own pick, " +
                $"awaiting follow announce (engageable={engageable.Count})");
            return;
        }

        // Walk the engageable list in TargetOrder and pick the first
        // monster we can actually engage. A monster the game data proves
        // un-actionable (no weapon hits its Magical level AND every attack
        // spell is level-blocked by its SpellImmu) is skipped — logged with
        // the reason — and we try the next. TargetOrder.Normal walks
        // highest-priority first (sorted ascending); Reverse walks
        // lowest-priority first.
        IReadOnlyList<EngageableCandidate> ordered =
            settings.TargetOrder == TargetOrder.Reverse
                ? Enumerable.Reverse(engageable).ToList()
                : engageable;

        // On the backstab opener, prefer the highest-priority actionable monster
        // NOT flagged DontBackstab so the surprise round doesn't land on a
        // never-BS target. If every actionable monster is flagged we don't skip
        // the room — fall back to the highest-priority actionable one and open
        // with a normal attack (the chooser's BS gate suppresses the bs there).
        bool backstabPending = BackstabPending(settings, obs);
        EngageableCandidate? choice = null;
        EngageableCandidate? actionableFallback = null;
        foreach (EngageableCandidate cand in ordered)
        {
            EngageAssessment assess = AssessEngage(settings, cand.MonsterNumber, cand.ResolvedName);
            if (assess != EngageAssessment.CanAct)
            {
                _log?.Combat(LogCategory,
                    $"skip un-actionable {cand.RawName} (#{cand.MonsterNumber}) — {assess}");
                // Only a permanent give-up surfaces the "cannot attack" line; a
                // transient mana stall stays quiet (it's retryable once MA regens).
                if (assess == EngageAssessment.Unkillable)
                    AnnounceCannotAttack(cand.ResolvedName);
                continue;
            }
            actionableFallback ??= cand;
            if (backstabPending && cand.DontBackstab)
                continue;                 // prefer a non-flagged backstab target
            choice = cand;
            break;
        }
        // Backstab pending but every actionable monster is DontBackstab-flagged —
        // take the highest-priority actionable one and attack it normally.
        choice ??= actionableFallback;

        // No engageable hostile is actionable — we can neither hit nor spell
        // anything left in the room. Move past: clear the target and dispatch
        // nothing. CombatStateTracker releases the walker gate on this same
        // observation (it consults the same CanEngageMonster delegate), so the
        // walker steps to the next room.
        if (choice is not { } picked)
        {
            _log?.Combat(LogCategory,
                $"room un-actionable: {engageable.Count} hostile(s), none hittable — " +
                $"moving on (engageable=[" +
                $"{string.Join(",", engageable.Select(e => e.RawName))}])");
            _currentTarget = null;
            return;
        }

        // Log the re-pick decision so the user can audit why a fresh
        // attack went out — was it the first attack ever (current
        // null), did the target leave / die (current non-null,
        // engageable list shown), or did we get a "room cleared"
        // earlier that null-ed the target. Distinguishes the
        // "wasted-swing on re-display" symptom from a genuine
        // re-pick.
        if (_currentTarget is null)
        {
            _log?.Combat(LogCategory,
                $"re-pick: no current target — picking {picked.RawName} from " +
                $"[{string.Join(",", engageable.Select(e => e.RawName))}]");
        }
        else
        {
            _log?.Combat(LogCategory,
                $"re-pick: target '{_currentTarget}' not in engageable — " +
                $"switching to {picked.RawName} (engageable=[" +
                $"{string.Join(",", engageable.Select(e => e.RawName))}])");
        }

        // ShadowRest hold: a solo, stealthed ShadowRest character below a rest
        // floor stays hidden and rests instead of engaging. Stand down before any
        // dispatch so stealth holds and the backstab opener stays unspent — once
        // HealthManager tops off to rest-max it fires ResumeAfterShadowRest, which
        // re-runs this observation with the hold lifted and opens with the bs.
        // Placed after the empty-room / clear handling so a mob leaving mid-rest
        // still tears down cleanly.
        if (_shadowRestHolding?.Invoke() == true)
        {
            _log?.Combat(LogCategory,
                $"combat held — shadowrest recovering (would engage {picked.RawName})");
            _currentTarget = null;
            return;
        }

        // Passive-neutral recovery hold: the picked target is a KillOnSight NEUTRAL,
        // we're below the rest trigger, and nothing here attacks on sight. A neutral
        // won't attack until we hit it, so stand down and let HealthManager rest to
        // rest-max before we engage — clearing InCombat so the rest fires while the
        // gate keeps the walker put. ResumeAfterRecovery re-picks once recovered.
        if (_recoveryPending?.Invoke() == true
            && _hasAttackingHostile?.Invoke() != true
            && IsNeutral(picked)
            && !_userEngagedInstances.Contains(picked.RawName))   // user chose to fight this one now — don't sit and rest
        {
            _log?.Combat(LogCategory,
                $"combat held — recovering before engaging neutral {picked.RawName}");
            _currentTarget = null;
            _holdingForRecovery = true;
            _clearInCombatForHold?.Invoke();
            return;
        }

        // Pre-attack in-between pass: on a fresh engage let the in-between window
        // (a due survival cast, else the configured debuff — ranked by the
        // Spells+Ailments priority) fire BEFORE the attack, so a pre-attack debuff
        // lands ahead of the combat action rather than a round later. No-ops and
        // leaves state untouched unless a debuff is actually due, so the ordinary
        // engage is unchanged; when it fires, the attack resumes on the cast's
        // *Combat Off*.
        if (TryPreAttackInBetween(settings, picked, obs))
            return;

        // Decide + dispatch this round's action. The chooser owns the full
        // per-round category ordering (Backstab / Debuffing / Spells /
        // Physical) in the user-configured priority; DispatchRoundAction
        // maps its decision onto the wire (backstab verb, combat-spell cast,
        // or weapon swing). Spell categories only participate when the caster
        // is wired — otherwise the order is just Backstab vs Physical.
        DispatchRoundAction(settings, picked, engageable.Count, obs);
    }

    // True when a backstab is still owed for this room — sneaking, with
    // CombatSettings.DoBackstab on, the surprise round unspent, and no occupant
    // carrying SeeHidden. The BS round must fire before any spell or normal swing
    // or it's a guaranteed fail; once any action fires here the opener is spent
    // (_backstabOpenerConsumed) and re-engages fall back to the normal priority.
    // Shared by the backstab gate in OnEntitiesObserved and the combat-spell
    // chooser context so both agree on the gate.
    private bool BackstabPending(CombatSettings settings, RoomEntitiesObservation obs) =>
        settings.DoBackstab && !_backstabOpenerConsumed
            && _isStealthed?.Invoke() == true && !RoomHasSeeHidden(obs);

    // Equip the normal/alternate weapon and send the weapon attack command
    // against targetRaw. Sets CurrentTarget; SendAttack clears the spell-mode
    // bridge so the server's auto-repeat owns subsequent rounds. Shared by the
    // initial weapon path in OnEntitiesObserved and the heartbeat's
    // spell-conditions-lapsed fallback.
    private void SendWeaponAttack(
        CombatSettings settings, string targetRaw, bool useAlt,
        MonsterAttackPriority? priority = null)
    {
        EquipForAttack(settings, useAlt);
        string verb = useAlt
            ? settings.AlternateAttackCommand
            : settings.NormalAttackCommand;
        SendAttack(verb, targetRaw, priority);
        _currentTarget = targetRaw;
    }

    // True when any monster currently in the room carries SeeHidden — which
    // defeats a stealthed character's backstab (sneak or hide) for the whole room.
    // No-op (false) until the backstab hooks are wired.
    private bool RoomHasSeeHidden(RoomEntitiesObservation obs)
    {
        if (_hasSeeHidden is null) return false;
        for (int i = 0; i < obs.Entities.Count; i++)
        {
            RoomEntity e = obs.Entities[i];
            if (e.Kind != EntityKind.Monster) continue;
            if (e.MonsterNumber is not int n) continue;
            if (_hasSeeHidden(n)) return true;
        }
        return false;
    }

    // ----- Weapon-swap mechanics --------------------------------------

    // End-of-combat cleanup (room cleared). Resets the per-room fail-set +
    // spell economy, and reverts an alternate-weapon swap back to normal. The
    // backstab re-gear is NOT done here: equipping breaks sneak, so the backstab
    // loadout must be applied in the pre-move sequence, immediately before the
    // sn (see PrepBackstabForMove) — not raced against it at room-clear.
    private void OnRoomCleared(CombatSettings settings)
    {
        _normalWeaponFailedMonsters.Clear();
        _alternateWeaponFailedMonsters.Clear();
        _speciesByNumber.Clear();
        _cannotAttackAnnounced.Clear();
        ClearBackstabResolution();
        ClearAttackSpellCascadeState();

        // Revert an alt-weapon swap so the next fight opens on the normal
        // weapon — but skip it when backstab is active, since the pre-move
        // sequence re-gears to the backstab weapon before the next approach and
        // a normal-then-backstab double swap would be wasted commands.
        bool backstabActive = settings.DoBackstab
            && !string.IsNullOrWhiteSpace(settings.BackstabWeapon);
        if (!backstabActive && _usingAlternateWeapon)
            _swapWeapon?.Invoke(settings.NormalWeapon, settings.NormalOffHand, false);
        _usingAlternateWeapon = false;
    }

    // Reset the attack-spell cascade's per-target/per-room economy — the
    // announced spell, the round-owed latch CastingDirector gates every
    // survival cast on (IsSpellAttackOwed), the alternation/tally clocks, the
    // observed-immunity set, and the chooser's own cast-cap counters. Shared by
    // OnRoomCleared (a genuine end-of-fight), the AutoCombat-disabled early
    // return (toggling combat off must not leave the round latched to a target
    // that's no longer being fought), and OnPlayerDeath (the corpse/respawn
    // room has nothing to do with whatever spell was mid-flight). Report
    // paradigm-20260824-012300: a stale spellTarget on a monster long gone
    // left IsSpellAttackOwed permanently true, silently blocking every
    // automatic heal/cure/bless for the rest of the session.
    private void ClearAttackSpellCascadeState()
    {
        _castingSpellTarget = null;
        _lastCastAction = null;
        _alternationRound = 0;
        _lastAlternationAdvanceAt = DateTimeOffset.MinValue;
        _lastAttackTallyAt = DateTimeOffset.MinValue;
        _spellAttackOwed = false;
        // Drop a pending pre-attack-debuff suppress: if the fight ended (room clear,
        // combat off, death) before the debuff's *Combat Off* arrived, a stale stamp
        // could bar the first legit resume in the NEXT fight (unlikely — a new cast
        // re-stamps _betweenRoundCastAt — but cheap insurance).
        _suppressResumeForBetweenRoundStamp = DateTimeOffset.MinValue;
        _attackSpellImmuneSpecies.Clear();
        // Roster-clear reset, NOT a full new-room reset: this path also fires on the
        // AoE multi-kill's synthetic room-clear (same physical room), so it must keep
        // the area-debuff per-room cap so the debuff doesn't re-fire at the same
        // room's survivors. The genuine physical-room-change reset (which clears that
        // cap) lives in NotePreMove.
        _spellChooser.ResetForRosterClear();
    }

    // Player death: the corpse room and whatever comes after (recovery walk,
    // respawn) share nothing with the fight that just ended. Clears the same
    // attack-spell cascade state OnRoomCleared does, plus the current target —
    // OnRoomCleared alone isn't reached here since the room the fight was IN
    // doesn't necessarily go through its own clear path on a death mid-fight.
    // Wired to RoomTracker.PlayerDeathObserved in AppServices, alongside the
    // existing Conditions.ClearAll("death") wire.
    public void OnPlayerDeath()
    {
        _currentTarget = null;
        ClearAttackSpellCascadeState();
    }

    // A disconnect/reconnect invalidates any mid-round combat state. The
    // CombatGate correctly re-detects a hostile fresh on room-entry after
    // reconnect, but a stale _currentTarget / _castingSpellTarget surviving the
    // drop makes the resume logic think an attack spell is already in flight,
    // waiting on a *Combat Off* that was lost along with the connection — the
    // resume path only fires within CastInterruptResumeWindow of the interrupted
    // cast, so an Off that never arrives means the round-owed latch
    // (IsSpellAttackOwed) never clears and the character never resumes
    // attacking, even though the monster (still fighting server-side through the
    // link-death) keeps attacking back (report paradigm-20260827-203548; same
    // failure shape as paradigm-20260824-012300's stale-spellTarget lockup,
    // different trigger). Wired to the disconnect handler in
    // MainWindowViewModel alongside CastDirector's buff-timer pause and
    // Conditions.ClearAll("disconnect") — everything else about the fight is
    // gone the instant the wire drops (the server has moved the round on
    // without us by the time we're back), so the next room-entry engagement
    // starts clean and re-casts rather than waiting on an event that can no
    // longer arrive.
    public void OnDisconnected()
    {
        if (_currentTarget is not null || _spellAttackOwed)
            _log?.Combat(LogCategory,
                $"disconnect cleared stale combat state — target={_currentTarget ?? "(none)"}, "
                + $"spellTarget={_castingSpellTarget ?? "(none)"}, spellAttackOwed={_spellAttackOwed}");
        _currentTarget = null;
        ClearAttackSpellCascadeState();
    }

    // The idle-stall watchdog force-clears combat when a room falls quiet (empty,
    // no combat activity) — CombatStateTracker raises CombatForceCleared. Unlike a
    // clean room-clear, that path emits no fresh observation, so _currentTarget and
    // the attack-spell cascade would otherwise SURVIVE the clear. The between-round
    // director keys its debuff purely off _currentTarget + the last observation
    // (it has no InCombat/movement gate), so a stale target left here lets an AoE
    // debuff fire into the room the character has since walked out of — an `isto`
    // went out the instant the walker stepped away, still aimed at the prior
    // fight's mob (report paradigm-20260902-053911). Drop the target the same way
    // OnPlayerDeath / OnDisconnected do so the next genuine engage starts clean.
    public void OnCombatForceCleared()
    {
        if (_currentTarget is null && !_spellAttackOwed) return;
        _log?.Combat(LogCategory,
            $"combat force-clear dropped stale target — target={_currentTarget ?? "(none)"}, "
            + $"spellTarget={_castingSpellTarget ?? "(none)"}");
        _currentTarget = null;
        ClearAttackSpellCascadeState();
    }

    // Pre-move backstab prep — invoked from the walker / loop-runner pre-move
    // hook immediately before the sneak, so the whole approach sequence is
    // weapon → armor → sn → move. Equipping breaks sneak, so the gear MUST land
    // before the sn; the actuator sends both synchronously (SwapWeapon is
    // unpaced, ApplyBackstabArmor is a synchronous burst) so nothing trails into
    // the sneak. No-op unless backstab is enabled with a configured weapon.
    public void PrepBackstabForMove()
    {
        CombatSettings settings = _readSettings();
        if (!settings.DoBackstab) return;

        // A new sneak-approach re-opens the surprise round for the room we're
        // about to enter — the next action there is a genuine backstab opener
        // again. Reset before the early-return so it still fires when we backstab
        // with the equipped weapon (no dedicated BackstabWeapon configured).
        _backstabOpenerConsumed = false;

        if (string.IsNullOrWhiteSpace(settings.BackstabWeapon)) return;
        _swapWeapon?.Invoke(settings.BackstabWeapon, settings.BackstabOffHand, false);
        _prepBackstabArmor?.Invoke();
        _usingAlternateWeapon = false;
    }

    // Pre-move hook: reset the chooser's per-room cast economy before we step into
    // the next room. The AoE debuff / multi-attack caps count PER ROOM, and the AoE
    // debuff tags each mob's RawName so it fires once per room, re-firing only for a
    // later NEW arrival. RawNames repeat across a same-species hunt loop (every room
    // is "ironshell crab, scorpion crab, …"), so a room's tags would otherwise carry
    // into the next room and read the fresh crabs as "already debuffed" — the
    // once-per-room AoE silently skipped for the rest of the loop (report
    // paradigm-20260827-082106). The room-clear reset (OnRoomCleared) only fires on
    // an OBSERVED empty room, which a back-to-back populated loop never produces: the
    // new room's "Also here:" parses BEFORE the move confirms, so the classifier
    // emits no empty observation between rooms and the cap bleeds. Resetting here —
    // on the walker / loop-runner pre-move hook, which fires before the new room
    // displays — lands ahead of the next room's opener, so each room fires its AoE
    // debuff exactly once with no double-fire. Fires for every mover (backstab or
    // not), so unlike PrepBackstabForMove it is not gated on DoBackstab.
    public void NotePreMove()
    {
        _spellChooser.ResetForNewRoom();
        // Leaving the room drops any debuff still awaiting a rejection — its mark is
        // gone with the room reset, so there's nothing left to roll back.
        _debuffAwaitingConfirm = null;
    }

    // The room is confirmed genuinely CLEARED of all hostiles — the player just entered
    // a rest posture, and a rest only starts once nothing is left to fight. Reset the
    // AoE area-debuff room tags so a same-room RESPAWN after this is debuffed as a fresh
    // wave (report paradigm-20260903-070438). Distinct from NotePreMove (a physical
    // move) and from the mid-fight wave-clear roster reset, which keeps the tags for
    // hidden same-species survivors (report paradigm-20260902-160110). Wired in
    // AppServices to the PlayerState.Position rest edge.
    public void NoteRoomClearedByRest() => _spellChooser.ResetAreaDebuffTags();

    // Re-arm the surprise round for a fresh hide established in the current room —
    // the stationary hidden opener (a monster walks into a room the character is
    // hidden in). Unlike PrepBackstabForMove there's NO gear swap: equipping breaks
    // hide, so the backstab loadout must already be on before the `hid`. Flag-only,
    // so a hidden character re-hiding after a kill re-opens the surprise round for
    // the next monster that wanders in. Bound in AppServices to
    // StealthManager.StateChanged on the AttemptingHide/Idle -> Hidden edge.
    public void RearmBackstabForHide()
    {
        if (!_readSettings().DoBackstab) return;
        _backstabOpenerConsumed = false;
    }

    // Decide which weapon should be on for the next attack and hand it to the
    // actuator (EquipmentManager owns the wire + worn-diff + two-handed rule).
    // Called from OnEntitiesObserved just before SendAttack.
    private void EquipForAttack(CombatSettings settings, bool wantAlternate, bool force = false)
    {
        string? weapon;
        string? offHand;
        if (wantAlternate)
        {
            weapon = settings.AlternateWeapon;
            offHand = settings.AlternateOffHand;
            _usingAlternateWeapon = true;
        }
        else
        {
            weapon = settings.NormalWeapon;
            offHand = settings.NormalOffHand;
            _usingAlternateWeapon = false;
        }
        _swapWeapon?.Invoke(weapon, offHand, force);
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    // ----- No-effect handlers -----------------------------------------

    // Server says our normal weapon has no effect against the current target.
    // Swap to the alternate immediately — the message won't stop just because we
    // keep swinging the same weapon, so there's nothing to gain by tolerating a
    // few before switching. Add the species to the room-scoped fail-set so the
    // next target pick chooses the alternate preemptively. If we're already on
    // the alternate when this fires, the monster is genuinely unhittable for us
    // — log + leave it.
    private void OnWeaponNoEffect(MatchResult _)
    {
        if (!_isEnabled()) return;
        if (_currentTarget is null) return;

        // Canonicalize the target to base species — strip any flavor
        // prefix. The classifier's ResolvedName is the canonical form;
        // _currentTarget holds RawName. We resolve by scanning the
        // current observation.
        string species = ResolveSpeciesFromCurrentTarget();
        CombatSettings settings = _readSettings();

        if (_usingAlternateWeapon)
        {
            // SpellsFirst, the two Alternate* orders, AND CustomRoundCycle all take
            // the "try the spell cascade immediately" branch below — this is a
            // narrow double-weapon-failure edge case (both configured weapons just
            // proved immune), not a per-round scheduling decision, so it isn't worth
            // making phase-aware for the round-cycle order: once both weapons are
            // dead, casting beats force-retrying a proven-dead swing regardless of
            // which phase the cycle happened to be in when this fired.
            bool physicalFirst = settings.ActionOrder == CombatActionOrder.PhysicalFirst;

            // First no-effect while believed on the alternate. It's itself evidence
            // of a phantom swap (the actuator suppressed the `eq` off a stale
            // worn/carried snapshot, but the belief flag flipped anyway), so we
            // give the weapon one genuine try. Order depends on the build:
            //   - Physical-first wants the weapon truly exhausted before spells, so
            //     it force-swaps + retries here and only reaches the spell cascade
            //     once THAT fails (below).
            //   - Spells-first already cast its spells before ever swinging, so on
            //     the weapon fallback failing it prefers the spell cascade, and only
            //     force-retries the weapon when no spell can take the round.
            // Guarded on a first-time Add so the recovery runs once per species.
            if (_alternateWeaponFailedMonsters.Add(species))
            {
                _log?.Combat(LogCategory, $"adding {species} to alternate-weapon fail-set");
                if (!physicalFirst && TryFallBackToSpellAfterWeaponFail(settings)) return;
                _log?.Combat(LogCategory,
                    $"weapon-no-effect on ALT against {species} — forcing physical swap + retry");
                EquipForAttack(settings, wantAlternate: true, force: true);
                if (_currentTarget is { } retryTgt)
                {
                    SendAttack(settings.AlternateAttackCommand, retryTgt, priority: null);
                    return;
                }
            }

            // The alternate has now genuinely failed (the forced retry drew another
            // no-effect, or there was no target to retry against). Physical-first
            // reaches the spell cascade HERE — after the weapon is proven out — so a
            // configured spell can still take the round.
            if (physicalFirst && TryFallBackToSpellAfterWeaponFail(settings)) return;

            // The weapon is out and the spell cascade didn't take the round. Drop
            // the target and force a fresh room view; the retarget loop then
            // re-assesses (AssessEngage) — attack another hostile, cast if MA has
            // regenerated, or, if nothing here is actionable, release the walker to
            // move on. It owns the "cannot attack" announce so a transient mana
            // stall (retryable) isn't mislabelled as a permanent give-up.
            _log?.Combat(LogCategory,
                $"weapon-no-effect on ALT against {species} — weapon exhausted, re-assessing");
            _currentTarget = null;
            TrySendRoomRefresh($"weapon exhausted vs {species} — re-pick / move on");
            return;
        }

        if (_normalWeaponFailedMonsters.Add(species))
            _log?.Combat(LogCategory, $"adding {species} to normal-weapon fail-set");

        // Swap NOW and re-send the attack so we don't waste a round.
        EquipForAttack(settings, wantAlternate: true);
        if (_currentTarget is { } tgt)
            SendAttack(settings.AlternateAttackCommand, tgt, priority: null);
    }

    // Surface a "cannot attack <species>" line once per species this room — the
    // whole configured combat chain (both weapons + every attack spell) proved
    // ineffective, so the engine is giving up on it and moving on. Info level so
    // it shows in the program log the operator watches. The once-per-species guard
    // keeps a room full of the same immune mob from spamming a line every round.
    private void AnnounceCannotAttack(string species)
    {
        if (string.IsNullOrEmpty(species)) return;
        if (!_cannotAttackAnnounced.Add(species)) return;
        _log?.Info(LogCategory, $"cannot attack {species} — nothing in the combat chain can hurt it; moving on");
    }

    // "Your fists have no effect" — we're swinging bare-handed (a weapon that
    // isn't hitting, or one that left our hand). Drop the target so the next
    // observation re-decides and re-hands the weapon to the actuator, which
    // re-equips it when live gear shows it's no longer worn.
    private void OnFistsNoEffect(MatchResult _)
    {
        _log?.Warn(LogCategory, "fists-no-effect — forcing a weapon re-pick from live gear");
        _usingAlternateWeapon = false;

        // Force a re-equip on the next attack by triggering a fresh
        // pick. The simplest path: drop _currentTarget so
        // OnEntitiesObserved re-decides + re-equips on the next
        // observation. (The classifier re-fires on every full room
        // display + arrival.)
        _currentTarget = null;
    }

    // Map current target's RawName back to its base species via the live
    // observation. Falls back to _currentTarget when no match is found (orphaned
    // target).
    private string ResolveSpeciesFromCurrentTarget() =>
        _currentTarget is { } tgt ? ResolveSpeciesByName(tgt) : string.Empty;

    // Map a monster RawName back to its base species via the live observation
    // (strips any flavor prefix). Falls back to the raw name itself when no match
    // is found.
    private string ResolveSpeciesByName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;
        if (_classifier.Current is { } obs)
        {
            foreach (RoomEntity e in obs.Entities)
            {
                if (e.Kind != EntityKind.Monster) continue;
                if (string.Equals(e.RawName, rawName, StringComparison.OrdinalIgnoreCase))
                    return e.ResolvedName;
            }
        }
        return rawName;
    }

    // "Moves to attack X" announce handler. Drives two independent knobs: Target
    // Priority (WHO — switch our target to the followed player's monster) and
    // Attack Order (WHEN — re-fire our own target to control initiative). Target
    // Priority is consulted first; when it takes the round's action (the announce
    // was the leader / followed member's), Attack Order is skipped so we don't
    // double-send.
    private void OnAttackAnnounce(MatchResult match)
    {
        // (?<player>\w+) at positional 0, (?<target>.+?) at 1.
        if (match.Groups.Count < 2) return;
        string announcer = match.Groups[0];
        string announcedTarget = match.Groups[1].Trim();
        if (announcer.Length == 0 || announcedTarget.Length == 0) return;

        // Never react to our own announce — we already swung.
        string? ownName = _readOwnGivenName();
        if (ownName is { Length: > 0 } &&
            string.Equals(announcer, ownName, StringComparison.OrdinalIgnoreCase))
            return;

        if (!_isEnabled()) return;
        CombatSettings settings = _readSettings();

        // Target Priority owns WHO. If this announce is from the player we
        // follow, it switches our target (with the un-actionable failback)
        // and dispatches this round — returning true so Attack Order doesn't
        // also fire a redundant swing.
        if (TryFollowTargetPriority(settings, announcer, announcedTarget))
            return;

        // Attack Order owns WHEN — re-fire our own current target to reclaim last
        // position in initiative; never switches the monster. It runs under EVERY
        // Target Priority mode: following the leader's target (who) and attacking
        // last (when) are independent knobs, and a user who sets an attack-last
        // mode wants our *Combat Engaged* to land after the party's announces even
        // while following the leader's pick. HandleAttackOrderRefire no-ops for
        // Default AttackTiming, so a non-followed member's announce only drives a
        // re-fire when an explicit attack-last mode is on — the earlier "redundant
        // re-fire under a Default follow-priority" case stays fixed. (The leader's
        // own announce is consumed by the Target-Priority branch above, which
        // schedules the same re-fire on its already-engaged hold so a two-member
        // party still lands us last.)
        HandleAttackOrderRefire(settings, announcer, announcedTarget);
    }

    // "X moves to cast <spell> upon Y." — a caster's round action. GAME_MECHANICS:
    // a party member has "gone" for the round via EITHER a melee "moves to attack"
    // OR this spell form, so attack-last coordination must treat them as equivalent
    // per-member announce signals or it misses every spellcaster. This drives ONLY
    // the WHEN (Attack-Order re-fire) knob, never target-follow: a heal/buff names a
    // *player* ("... upon MudPlay"), and following onto that name would swing us at a
    // teammate. HandleAttackOrderRefire's "must equal our current target" guard
    // makes a non-mob cast a safe no-op; an offensive cast on our mob re-fires.
    private void OnCastAnnounce(MatchResult match)
    {
        if (match.Groups.Count < 2) return;
        string announcer = match.Groups[0];
        string announcedTarget = match.Groups[1].Trim();
        if (announcer.Length == 0 || announcedTarget.Length == 0) return;

        string? ownName = _readOwnGivenName();
        if (ownName is { Length: > 0 } &&
            string.Equals(announcer, ownName, StringComparison.OrdinalIgnoreCase))
            return;

        if (!_isEnabled()) return;
        HandleAttackOrderRefire(_readSettings(), announcer, announcedTarget);
    }

    // True when the room-entry picker should hold our own target and wait for the
    // followed player's attack announce instead of engaging independently. Only
    // applies while the priority/order settings are actually in force: we must be
    // in a party (disband / leaving reverts to our own game-data pick), configured
    // to follow the leader or a named member, that player must actually be in the
    // room with us (following someone not here is meaningless), and there must be a
    // choice to make (more than one engageable mob — with a single mob there's
    // nothing to coordinate, so we just take it). When any condition fails we fall
    // straight through to the normal independent pick.
    private bool ShouldWaitForFollow(CombatSettings settings, int engageableCount, RoomEntitiesObservation obs)
    {
        if (!_party.IsInParty) return false;
        if (engageableCount <= 1) return false;

        string? followName = settings.TargetPriority switch
        {
            TargetPriority.FollowLeader => _party.LeaderName,
            TargetPriority.FollowMember => settings.TargetPriorityMemberName,
            _                           => null,
        };
        if (followName is not { Length: > 0 }) return false;

        string followGiven = GivenName(followName);
        for (int i = 0; i < obs.Entities.Count; i++)
        {
            RoomEntity e = obs.Entities[i];
            if (e.Kind != EntityKind.Player) continue;
            if (string.Equals(GivenName(e.ResolvedName), followGiven, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Target Priority (the "who"): when configured to follow the party leader
    // (FollowLeader) or a named member (FollowMember), and THIS announce is from
    // that player, switch our target to their announced monster and dispatch this
    // round against it (through the full per-round chooser so the weapon swap /
    // spell selection apply). If game data proves the monster un-actionable for
    // us, re-pick our own next actionable target instead. Returns true when this
    // announce was the followed player's and we took the round's action; false
    // when Target Priority is Default or the announcer isn't the one we follow.
    private bool TryFollowTargetPriority(
        CombatSettings settings, string announcer, string announcedTarget)
    {
        // Target Priority is a party-coordination knob — it only follows while
        // we're actually partied. Disband / leave reverts us to our own game-data
        // pick (the announce falls through to Attack Order, which owns its own
        // party scoping per mode).
        if (!_party.IsInParty) return false;

        string? followName = settings.TargetPriority switch
        {
            TargetPriority.FollowLeader => _party.LeaderName,
            TargetPriority.FollowMember => settings.TargetPriorityMemberName,
            _                           => null,
        };
        if (followName is not { Length: > 0 }) return false;
        // FollowLeader reads LeaderName (full `par` name) and FollowMember reads a
        // user-typed name; the announcer is always a given name — normalise both.
        if (!string.Equals(GivenName(announcer), GivenName(followName), StringComparison.OrdinalIgnoreCase))
            return false;

        // The followed player just announced — our deferral is resolved either way
        // (we engage their target below, or the un-actionable failback re-picks our
        // own), so drop the hold before OnCombatTick's fallback can also fire.
        _awaitingFollowAnnounce = false;

        if (_classifier.Current is { } liveObs)
        {
            // Failback: only follow onto their target if WE can engage it.
            int annNumber = ResolveMonsterNumber(liveObs, announcedTarget);
            if (UnengageableReason(settings, annNumber) is { } annReason)
            {
                _log?.Combat(LogCategory,
                    $"target-priority {settings.TargetPriority} skipped — " +
                    $"{announcedTarget} un-actionable for us ({annReason}); " +
                    "re-picking our own target");
                _currentTarget = null;
                OnEntitiesObserved(liveObs);
                return true;
            }

            // The announced monster is in our room view → dispatch through
            // the per-round chooser against that specific instance.
            if (TryBuildCandidate(liveObs, announcedTarget) is { } cand)
            {
                // Same-target guard: the server already auto-swings at a target
                // once we've engaged it, so re-issuing purely to FOLLOW the same
                // mob would burn a redundant command. Hold the follow — but under
                // an attack-last mode this followed announce is still one the party
                // "went" on, so schedule the coalesced Attack-Order re-fire so our
                // *Combat Engaged* re-lands after it (ScheduleAttackOrderRefire
                // no-ops for Default AttackTiming).
                if (string.Equals(_currentTarget, cand.RawName, StringComparison.OrdinalIgnoreCase))
                {
                    _log?.Combat(LogCategory,
                        $"target-priority {settings.TargetPriority} follow={announcer} " +
                        $"→ {cand.RawName} already engaged; no follow re-fire");
                    ScheduleAttackOrderRefire(settings, announcer);
                    return true;
                }
                _log?.Combat(LogCategory,
                    $"target-priority {settings.TargetPriority} follow={announcer} " +
                    $"→ {cand.RawName}");
                DispatchRoundAction(settings, cand, CountEngageable(liveObs), liveObs);
                return true;
            }
        }

        // No room view (or the announced entity isn't in it) — literal attack
        // against the announced name; the server resolves the instance. Same-target
        // guard applies here too: if we're already on this name, don't re-follow —
        // but still schedule the attack-last re-fire (no-op under Default timing).
        if (string.Equals(_currentTarget, announcedTarget, StringComparison.OrdinalIgnoreCase))
        {
            _log?.Combat(LogCategory,
                $"target-priority {settings.TargetPriority} follow={announcer} " +
                $"→ {announcedTarget} already engaged; no follow re-fire");
            ScheduleAttackOrderRefire(settings, announcer);
            return true;
        }
        _currentTarget = announcedTarget;
        SendAttack(settings.NormalAttackCommand, announcedTarget, refire: true,
                   refireReason: $"target-priority {settings.TargetPriority} follow={announcer}");
        return true;
    }

    // Attack Order (the "when"): re-fire our OWN current target to reclaim last
    // position in initiative when another player announces against that same
    // target after us. Never switches the monster — Target Priority owns "who".
    // No-op unless we already have a target, the announce is against that exact
    // target, and the announcer qualifies for the configured mode:
    //   - AttackLastParty — any party member.
    //   - AttackLastRoom  — any player.
    //   - AttackAfter     — the named player only.
    //   - Default         — never (own cadence).
    // The "after us" condition is implicit: we only hold a target once we've
    // announced, so an announce arriving while _currentTarget is set is by
    // definition after ours; an announce that preceded ours never reaches here
    // (no target yet).
    private void HandleAttackOrderRefire(
        CombatSettings settings, string announcer, string announcedTarget)
    {
        if (_currentTarget is not { } target) return;   // nothing to re-fire at

        // Only reposition against OUR priority target — ignore announces on
        // any other monster in the room. This also makes a caster's heal/buff
        // announce ("... upon <player>") a safe no-op on the OnCastAnnounce path.
        if (!string.Equals(announcedTarget, target, StringComparison.OrdinalIgnoreCase))
            return;

        ScheduleAttackOrderRefire(settings, announcer);
    }

    // Qualify the announcer against the AttackTiming mode and, when they qualify,
    // record a coalesced re-fire of our CURRENT target. Shared by the Attack-Order
    // path (a member announces our target) and the Target-Priority already-engaged
    // hold (the leader re-announces the target we already follow) — both need our
    // *Combat Engaged* to re-land after the announce so attack-last stays true even
    // in a two-member party. Modes:
    //   - AttackLastParty — any party member.
    //   - AttackLastRoom  — any player.
    //   - AttackAfter     — the named player only.
    //   - Default         — never (own cadence).
    // The "after us" condition is implicit: we only hold a target once we've
    // announced, so an announce arriving while _currentTarget is set is by
    // definition after ours; an announce that preceded ours never reaches here.
    private void ScheduleAttackOrderRefire(CombatSettings settings, string announcer)
    {
        if (_currentTarget is not { } target) return;

        // Hold the re-fire while a bs we already sent is still resolving — a
        // repositioning swing fired now can register server-side before the `bs`
        // resolves and double on top of the surprise (open with bs, then stay
        // quiet). Once the watch clears, normal re-fire resumes. Note this covers
        // only the already-fired window; when the opener is still ARMED (unspent),
        // the re-fire is upgraded to the bs itself in FlushAttackOrderRefire rather
        // than suppressed, so attack-last still lands us last, opening with bs.
        if (_awaitingBackstabResolution) return;

        bool fire = settings.AttackTiming switch
        {
            AttackTiming.AttackLastParty => IsPartyMember(announcer),
            AttackTiming.AttackLastRoom  => true,
            AttackTiming.AttackAfter     => string.Equals(GivenName(announcer),
                                                GivenName(settings.AttackAfterPlayerName ?? string.Empty),
                                                StringComparison.OrdinalIgnoreCase),
            _                            => false,  // Default — own cadence
        };
        if (!fire) return;

        // Coalesce the round's burst: record this as the pending re-fire and
        // flush once on the next dispatcher turn (after the whole announce
        // batch), so several party announces collapse into a single attack
        // command that lands after the last of them.
        _refireTarget = target;
        _refireAnnouncer = announcer;
        if (_refireFlushScheduled) return;
        _refireFlushScheduled = true;
        _post(FlushAttackOrderRefire);
    }

    // Trailing-edge flush of a coalesced AttackTiming re-fire (see the _refire*
    // fields). Runs one dispatcher turn after the round's announce burst, so a
    // single attack goes out against our target once — after the last party
    // announce. Skips when combat was turned off, we were disposed, nothing is
    // pending, or the target died / changed during the burst (no longer our
    // current target).
    private void FlushAttackOrderRefire()
    {
        _refireFlushScheduled = false;
        if (_disposed) return;
        if (_refireTarget is not { } target) return;
        string? announcer = _refireAnnouncer;
        _refireTarget = null;
        _refireAnnouncer = null;

        if (!_isEnabled()) return;
        if (!string.Equals(_currentTarget, target, StringComparison.OrdinalIgnoreCase))
            return;

        CombatSettings settings = _readSettings();

        // Backstab-aware re-fire: when the surprise round is still armed for this
        // room (stealthed, opener unspent, no SeeHidden), the re-fire IS our
        // opener — send `bs <target>`, never the normal attack command. Firing the
        // ordinary swing here would spend the round as a plain attack and waste the
        // surprise (the reported `pu <target>` on the mystic). The game grants the
        // surprise on OUR first combat command being bs even after other party
        // members have already swung, so staying last in line and opening with bs
        // are not in tension. Arm the resolution watch + consume the opener exactly
        // as DispatchRoundAction does. (The awaiting-resolution window — bs already
        // in flight — is held upstream in HandleAttackOrderRefire so no second
        // command doubles on top of the surprise.)
        if (_classifier.Current is { } liveObs && BackstabPending(settings, liveObs))
        {
            SendAttack("bs", target, refire: true,
                       refireReason: $"{settings.AttackTiming} announcer={announcer} (backstab opener)");
            _currentTarget = target;
            _awaitingBackstabResolution = true;
            _pendingBackstabSpecies = ResolveSpeciesByName(target);
            _backstabOpenerConsumed = true;
            return;
        }

        SendAttack(settings.NormalAttackCommand, target, refire: true,
                   refireReason: $"{settings.AttackTiming} announcer={announcer}");
    }

    // Build an EngageableCandidate for the monster matching name (RawName or
    // ResolvedName, case-insensitive) in obs, resolving its overlay priority.
    // Returns null when no numbered monster entity matches — the caller falls
    // back to a literal attack command. Used by Target Priority to route a
    // followed target through the full per-round dispatch (weapon swap / spell
    // pick).
    private EngageableCandidate? TryBuildCandidate(RoomEntitiesObservation obs, string name)
    {
        for (int i = 0; i < obs.Entities.Count; i++)
        {
            RoomEntity e = obs.Entities[i];
            if (e.Kind != EntityKind.Monster) continue;
            if (e.MonsterNumber is not int n) continue;
            if (!string.Equals(e.RawName, name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(e.ResolvedName, name, StringComparison.OrdinalIgnoreCase))
                continue;

            MonsterOverlay overlay = ResolveOverlay(n);
            return new EngageableCandidate(
                RawName:         e.RawName,
                ResolvedName:    e.ResolvedName,
                MonsterNumber:   n,
                Priority:        overlay.Priority ?? MonsterAttackPriority.Normal,
                AppearanceIndex: i,
                DontBackstab:    overlay.DontBackstab ?? false);
        }
        return null;
    }

    // Fire the debounced bare-CR "where am I" room re-display shared by the combat
    // recovery paths (empty-room combat line, target-not-here, no-effect,
    // unattributed death, unconfirmed engage). Returns true when a CR actually went
    // out so the caller can log the send.
    //
    // Suppressed entirely in a dark room: a CR there re-emits only "the room is
    // very dark - you can't see anything" — no Also-Here list to re-read — and
    // worse, that stale dark line is consumed by RoomTracker's dead-reckoning
    // (NoteDarkRoomEntered) as a false confirmation of the movement loop's
    // in-flight step. The false-fast confirm collapses the dark-room settle window,
    // so the loop double-steps past lairs and drags late-populating monsters. We
    // can't see in the dark, so there's nothing to refresh.
    private bool TrySendRoomRefresh(string context)
    {
        if (_wireSender is null) return false;
        if (_isInDarkRoom?.Invoke() == true)
        {
            _log?.Combat(LogCategory,
                $"dark room — CR re-display suppressed ({context})");
            return false;
        }
        DateTimeOffset now = DateTimeOffset.Now;
        if (now - _lastRoomRefreshAt < RoomRefreshCooldown) return false;
        _lastRoomRefreshAt = now;
        _wireSender(Encoding.Latin1.GetBytes("\r"));
        return true;
    }

    // Safety net: a combat line (user hit / mob hit / mob miss) means something
    // is swinging at us — but if the classifier shows no engageable monster and
    // we have no current target, our view of the room is stale (entity dropped
    // after a death, arrival line lost, prefix not resolved against the overlay,
    // etc.). Send a bare CR (^M) so the server re-emits a short room view; the
    // classifier repopulates, OnEntitiesObserved picks a target, and the next
    // round we swing back. Debounced so a burst of combat lines doesn't flood the
    // wire.
    //
    // Bare CR is preferred over `l` because the server's CR response is the
    // compact "where am I" payload — the Also Here list plus prompt without the
    // room description, exits block, and ground-item enumeration that `l` dumps.
    private void OnCombatLine(MatchResult _)
    {
        if (!_isEnabled()) return;

        // Resume-after-interrupt: a combat line arrived while our
        // auto-attack is off (we cast a buff/heal mid-round, got
        // stunned, etc.) and the room still holds an engageable mob.
        // The server stopped swinging for us but the fight is clearly
        // ongoing — the only combat line that can reach here while
        // _combatOff is a *mob* swing (we're not attacking, so no
        // user-hit precedes the resume). Re-pick + re-issue the attack.
        // Gated on _combatOff so a normal in-combat line (server still
        // swinging) never re-fires. A just-killed mob can't produce a
        // combat line, so this won't swing at a corpse on a clean kill.
        if (_combatOff
            && _classifier.Current is { } live
            && HasEngageable(live))
        {
            TryResumeEngage(live);
            return;
        }

        if (_currentTarget is not null) return;
        if (_wireSender is null) return;

        if (_classifier.Current is { } cur && HasEngageable(cur)) return;

        if (TrySendRoomRefresh("combat line, room appears empty"))
        {
            _log?.Combat(LogCategory,
                "combat-line while room appears empty — sending CR for short re-display");
            // Something is swinging at us in a room our view shows empty. Signal
            // the settle watcher to hold the movement loop until the CR re-display
            // resolves — a hostile that leapt in must engage before we step past it.
            RoomAppearsEmptyDuringCombat?.Invoke();
        }
    }

    // Increments ConfirmedAttackCastCount once per REAL single-target attack/
    // alternate/drain-spell cast landing (or missing) against the current target —
    // the precise signal ReadRoundCount now runs on (see its declaration comment).
    // Gated on _castingSpellTarget: that field is only set while the round's action
    // is our announced single-target spell (a weapon swing clears it on send), so any
    // qualifying combat-result line reaching here while it's set is that spell's own
    // result, never a swing. The "You " prefix and target-name check mirror
    // OnBackstabResolutionLine's filtering — both UserHits and UserMisses also fire
    // for party members' actions and (UserMisses) self-emotes. Consecutive lines
    // inside ConfirmedCastGroupWindow are one cast's own multi-projectile results,
    // not a second cast — only the group's first line increments. The grouping also
    // requires the SAME target: a kill that immediately re-engages a fresh mob within
    // the window must still count that mob's opening cast, not fold it into the
    // corpse's tally.
    private void OnAttackCastConfirmed(MatchResult match)
    {
        if (_castingSpellTarget is not { } target) return;

        string text = match.Text;
        if (!text.StartsWith("You ", StringComparison.Ordinal)) return;
        if (text.IndexOf(target, StringComparison.OrdinalIgnoreCase) < 0) return;

        DateTimeOffset now = _now();
        bool grouped = now - _lastConfirmedAttackCastAt < ConfirmedCastGroupWindow
            && string.Equals(_lastConfirmedAttackCastTarget, target, StringComparison.OrdinalIgnoreCase);
        _lastConfirmedAttackCastAt = now;
        _lastConfirmedAttackCastTarget = target;
        if (grouped)
        {
            _log?.Combat(LogCategory,
                $"attack-cast projectile grouped spell={_announcedSpellCode ?? "?"} "
                + $"target='{target}' confirmedCount={ConfirmedAttackCastCount}");
            return;
        }

        ConfirmedAttackCastCount++;
        _log?.Combat(LogCategory,
            $"attack-cast confirmed spell={_announcedSpellCode ?? "?"} target='{target}' "
            + $"confirmedCount={ConfirmedAttackCastCount}");

        // Do not wait for TickEngine's next CombatTickElapsed to consume this
        // confirmation. A mob hit/miss commonly opens the server's round burst and
        // fires that heartbeat BEFORE our spell-result lines arrive; TickEngine then
        // debounces the immediately-following projectile lines. Waiting for another
        // heartbeat therefore postpones a MaxCasts=1 switch until the NEXT round,
        // after the server has already auto-repeated the capped spell. The Spells
        // partial applies the newly-observed cast directly to the existing chooser
        // tally and deferred-switch path. Its ReadRoundCount gate makes this active
        // only when the configured count source actually advanced (production wires
        // that source to ConfirmedAttackCastCount).
        ApplyConfirmedAttackCastToCap();
    }

    // Backstab surprise-round resolver. The opener swings exactly once; the first
    // of OUR combat-result lines naming the target settles the outcome:
    //   * carries "surprise" (e.g. "You surprise punch orc rogue for 30 damage!")
    //     → the backstab landed.
    //   * lacks it — a whiff ("You swing at dark cultist!") or a folded normal
    //     round ("You punch nasty dark cultist for 2 damage!") → it failed.
    // Self-gated on the armed watch, so lines outside the bs window are ignored.
    // The "You " prefix filters party members' hits (UserHits fires for them too),
    // and requiring the target's species in the line rejects the broad UserMisses
    // pattern's self-emote matches ("You feel much better!").
    private void OnBackstabResolutionLine(MatchResult match)
    {
        if (!_awaitingBackstabResolution) return;

        string text = match.Text;
        if (!text.StartsWith("You ", StringComparison.Ordinal)) return;

        string species = _pendingBackstabSpecies ?? string.Empty;
        if (species.Length == 0
            || text.IndexOf(species, StringComparison.OrdinalIgnoreCase) < 0)
            return;

        // First qualifying swing resolves the round exactly once.
        ClearBackstabResolution();

        if (text.IndexOf("surprise", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _log?.Combat(LogCategory, $"backstab landed (surprise) vs '{species}'");
            return;
        }

        _log?.Info(LogCategory, $"backstab failed (no surprise) vs '{species}'");
        if (_readSettings().RunIfBackstabFails)
            _backstabFailureFlee?.Invoke();
    }

    // Disarm the surprise-round watch. Called on resolution and on every signal
    // that the fight has moved past the opener (room change, target death, room
    // clear, target-gone) so a resolution line we never saw can't leave the watch
    // (and its re-fire suppression) latched.
    private void ClearBackstabResolution()
    {
        _awaitingBackstabResolution = false;
        _pendingBackstabSpecies = null;
    }

    // "You don't see <X> here!" — server can't find the target we just attacked.
    // Different from MonsterDeathWatcher's path: catches cases where the death
    // line was missed, the mob fled, or a partymate killed it between our send
    // and the server's resolve. Drop the current target and refresh the room so
    // the next observation picks a fresh target.
    private void OnTargetNotHere(MatchResult _)
    {
        if (!_isEnabled()) return;
        if (_wireSender is null) return;
        if (_currentTarget is null) return;

        string gone = _currentTarget;
        _log?.Combat(LogCategory,
            $"target-not-here — dropping target={gone} + refreshing room");
        _currentTarget = null;
        // The server says the named target isn't here — a guarded priority we were
        // chasing is genuinely gone, so end the redirect chase (breaks the retry
        // loop if a guard-retry "aa <priority>" was what drew this line).
        _guardBlockedTarget = null;
        ClearBackstabResolution();

        // In a DARK room TrySendRoomRefresh is a no-op (a bare CR returns no "Also
        // here:" line to rebuild the roster), so the named-gone target would linger in
        // the classifier roster and every re-pick would re-choose it → "You don't see
        // X here!" forever, while a DIFFERENT attacker already in the room never gets
        // engaged (report paradigm-20260827-133337: sat taking hits from a zombie cat,
        // only self-healing). Drop the phantom directly so the classifier re-fires an
        // observation and re-picks the present attacker. Lit rooms still use the CR
        // re-display, which rebuilds the roster and masks this.
        if (_isInDarkRoom?.Invoke() == true)
        {
            _classifier.RemoveDepartedEntity(gone);
            return;
        }

        // Force a refresh (debounce shared with OnCombatLine so a
        // simultaneous miss-line + target-not-here doesn't double-send).
        // Bare CR — same rationale as OnCombatLine.
        TrySendRoomRefresh("target-not-here");
    }

    // "Your command had no effect." — a generic failure the server emits when the
    // action we just sent did nothing. Mid-fight it means the attack we fired
    // didn't land: a stale target reference, or a partymate engaged/killed the mob
    // between our send and the server's resolve (report: a leader-announce follow
    // swing that no-op'd). VerifyEngagement doesn't cover this because it only
    // arms for the FIRST unconfirmed swing — once the fight is Engaged, later
    // no-op swings go unguarded and combat sits idle until a manual redisplay.
    // Same remedy as target-not-here: drop the target and force a short room
    // re-display so OnEntitiesObserved re-picks and re-engages. Gated on an active
    // fight (_currentTarget set) so an unrelated no-effect command outside combat
    // is ignored.
    private void OnCommandNoEffect(MatchResult _)
    {
        if (!_isEnabled()) return;
        if (_wireSender is null) return;
        if (_currentTarget is null) return;

        string gone = _currentTarget;
        _log?.Combat(LogCategory,
            $"command-no-effect — dropping target={gone} + refreshing room");
        _currentTarget = null;
        // A no-effect swing / cast means the named target isn't hittable now — a
        // spell→weapon switch that raced the kill fires `aa <corpse>` and lands
        // here, so end spell mode too (not just the weapon target) rather than
        // leaving the heartbeat to re-cast at the corpse next tick.
        _castingSpellTarget = null;
        // ...and end any guard-redirect chase rather than re-firing at a target
        // that won't resolve.
        _guardBlockedTarget = null;
        ClearBackstabResolution();

        // Dark-room path (same as target-not-here): the CR refresh can't rebuild the
        // roster in the dark, so drop the phantom directly to re-fire an observation
        // and re-pick a present attacker instead of freezing on the gone target.
        if (_isInDarkRoom?.Invoke() == true)
        {
            _classifier.RemoveDepartedEntity(gone);
            return;
        }

        TrySendRoomRefresh("command-no-effect");
    }

    // A kill happened but the death-line didn't hand us a roster slot to drop.
    // Two callers: (1) a specific death-line matched but the classifier couldn't
    // attribute it to any monster in the current room view (shared /
    // suffix-flavored wordings: the death line resolved to "spectre" while the
    // roster holds "shadow spectre", so RemoveDeadEntity found nothing to drop);
    // (2) a fallback death (exp + *Combat Off*) where the death line was missing
    // entirely — the norm for datasets missing per-monster DeathLine patterns.
    //
    // Either way we WERE swinging at something and a kill just landed, so
    // _currentTarget is our best identity for the corpse: a fallback death's
    // *Combat Off* means OUR fight ended, and a flavored specific death is the
    // same mob under a base-name wording. Attribute the death to _currentTarget
    // exactly like a matched death does — drop it from the live roster so the
    // synthetic EntitiesObserved re-picks the next survivor immediately, or
    // clears the room and releases the Combat gate when it was the last mob.
    // Without this the dead mob lingers, the next tick re-swings at the corpse
    // ("Your command had no effect."), and recovery waits out the idle-stall
    // watchdog — the reported "tries to re-attack the monster that clearly died"
    // and "sits idle after a kill until the timeout" stalls. Only when the target
    // name doesn't match any roster slot do we fall back to the debounced CR
    // re-display, letting the server hand back the true roster.
    public void NoteUnattributedDeath()
    {
        if (!_isEnabled()) return;
        if (_wireSender is null) return;

        // AoE room-wipe: ≥2 exp lines this round means an AoE (ours or a hand-cast one)
        // killed several mobs at once. Peeling them off the roster one-by-one re-fires the
        // re-pick against the still-cached survivors — which are ALSO dying this round — so
        // the engine re-engages one and corpse-casts a single-target spell at it (report
        // paradigm-20260817-105650: a manual `fbal` wiped 5 goblins, then `mmis dark goblin
        // archer` went out at a corpse → "You don't see dark goblin archer here!"). Instead,
        // drop all combat state and force ONE CR re-parse so the next observation is the
        // TRUE roster — we re-pick from what's actually left (usually nothing) rather than a
        // survivor the kills haven't cleared yet.
        if (_expGainsThisRound >= 2)
        {
            ForceAoeMultiKillReparse("AoE multi-kill");
            return;
        }

        // Normally the corpse's identity is the still-set _currentTarget. But a prompt
        // exp-inferred kill (OnUserGainExperience) fires BEFORE this Off and already
        // nulled _currentTarget — fall back to the target it stashed so the roster
        // removal still happens (else the dead mob lingers and gets re-picked). Bounded
        // by ExpKillWindow so a stale stash can't drop a later, living mob.
        string? presumedDead = _currentTarget;
        if (presumedDead is null
            && _inferredKillPendingRemoval is { } pending
            && DateTimeOffset.Now - _inferredKillPendingAt < ExpKillWindow)
        {
            presumedDead = pending;
        }
        _inferredKillPendingRemoval = null;
        if (presumedDead is null) return;

        // A kill we couldn't pin to a roster slot still leaves the roster stale
        // until it resolves — the exact window in which the between-round-cast
        // resume must not re-engage (see _lastDeathAt).
        _lastDeathAt = DateTimeOffset.Now;
        _attackSentSinceDeath = false;

        // Clear the target BEFORE the removal re-fires EntitiesObserved (mirrors
        // the specific-death ordering) so the re-pick sees a clean slate.
        _currentTarget = null;
        _castingSpellTarget = null;   // end spell mode on the kill too — no corpse re-cast
        ClearBackstabResolution();
        if (_classifier.RemoveDeadEntity(presumedDead))
        {
            _log?.Combat(LogCategory,
                $"unattributed death attributed to '{presumedDead}' — dropped from roster");
            return;
        }

        // Target name matched no roster slot (flavored / shared wording the
        // RawName doesn't cover). Force the debounced room re-display so the
        // server hands us the true roster now.
        if (TrySendRoomRefresh("unattributed death"))
            _log?.Combat(LogCategory,
                "unattributed death — forcing room re-display to resync roster");
    }

    // Re-engage the room after an interrupt turned our auto-attack off (an
    // in-between self-heal / buff cast, a stun, etc.) while an engageable mob is
    // still present. Disarms the interrupt flag, drops the stale target so
    // OnEntitiesObserved re-picks and re-equips cleanly, then re-issues the
    // round's action.
    private void ResumeEngage(RoomEntitiesObservation live)
    {
        _combatOff = false;
        _log?.Combat(LogCategory, "combat resumed after interrupt — re-engaging room");

        // Do NOT clear _currentTarget here. A heal / bless / buff interrupt mid-fight
        // is not a new engagement; dropping the target made DispatchRoundAction treat
        // the same mob as brand-new and run its new-target reset — zeroing the custom
        // round-cycle phase (_alternationRound) and wiping the attack-spell cascade
        // (MaxCasts / immune / announce latches). That snapped the cycle back to its
        // opening phase every interrupt ("confused which attack to use"). Keeping the
        // target makes the re-dispatch a same-target re-announce, so the phase/cascade
        // carry over. A target that died during the interrupt was already cleared by
        // the death watcher, so this still re-picks a fresh mob when appropriate.
        //
        // The interrupt turned our auto-attack OFF, so the round's action must be
        // re-sent — but keeping _currentTarget would otherwise trip the "already
        // engaged, server is still swinging → don't re-send" short-circuit in
        // OnEntitiesObserved. Bypass just that guard for this one re-dispatch.
        //
        // _followDeferBypass also lifts the follow-announce hold: we only get here
        // mid-fight, so the party has already converged and the followed leader is
        // swinging at a mob it won't re-announce — leaving ShouldWaitForFollow armed
        // would park us awaiting an announce that never comes.
        _resumeBypassEngagedGuard = true;
        _followDeferBypass = true;
        try { OnEntitiesObserved(live); }
        finally
        {
            _resumeBypassEngagedGuard = false;
            _followDeferBypass = false;
        }
    }

    // Round-paced wrapper over ResumeEngage: re-engages at most once per
    // ResumePacing window. The pacing is what keeps a re-issued attack that
    // itself emits *Combat Off* every strike (KAI pummel and other non-sustaining
    // attacks) from spinning — the resume can't fire again until the next round,
    // no matter how fast the off/engaged lines cycle. Shared by the mob-swing
    // resume (OnCombatLine) and the deterministic tick resume (OnCombatTick) so
    // the two paths never double-fire in a single round. Returns true when it
    // actually resumed.
    //
    // bypassAttackGuard is set only by the between-round-cast resume: that Off is
    // provably our own cast interrupting the swing (armed by NoteBetweenRoundCast
    // within CastInterruptResumeWindow), so the "a fresh swing is still going"
    // assumption behind ResumeAfterAttackGuard is false there — the cast just
    // cancelled it, and skipping the resume would idle a full round (the reported
    // heal-then-stall). ResumePacing still stands, so we never double-fire with
    // the tick resume in the same round.
    private bool TryResumeEngage(RoomEntitiesObservation live, bool bypassAttackGuard = false)
    {
        // The user hand-typed this round's attack (a combat spell or a swing) — don't
        // re-send our auto attack over the top of it. The override clears at the next
        // combat tick, so the engine resumes on the following round (report
        // paradigm-20260814-135715).
        if (_userAttackOverride)
        {
            _log?.Combat(LogCategory, "resume suppressed — user attack override holds this round");
            return false;
        }
        DateTimeOffset now = DateTimeOffset.Now;
        // A fresh swing already went out a beat ago — this resume is redundant
        // and would double on top of it. Happens on a kill: the death→re-observe
        // path re-engages the surviving mob, then the kill's *Combat Off* re-arms
        // _combatOff and the next mob swing line drops in here (the reported solo
        // double-send). Skip while a real attack is still this recent — unless a
        // between-round cast is what produced this Off (see bypassAttackGuard).
        if (!bypassAttackGuard && now - _lastAttackSentAt < ResumeAfterAttackGuard) return false;
        if (now - _lastInterruptResumeAt < ResumePacing) return false;
        _lastInterruptResumeAt = now;
        ResumeEngage(live);
        return true;
    }

    // Signal from Spells.CastingDirector.CastFired that a between-round cast
    // (self-heal / cure / buff / debuff) just went to the server. Arms
    // OnCombatStatus to attribute the imminent *Combat Off* to that cast and
    // resume the weapon attack promptly (see _betweenRoundCastAt). Also arms
    // _spellAttackOwed while an attack spell was actively cycling — this cast just
    // spent the round that owed us an attack, so CastingDirector must sit out the
    // next one (see _spellAttackOwed).
    // Engine-armed between-round cast (CastingDirector survival heals/buffs, and
    // our own internal casts). Never rate-limited — the engine casts at most once
    // per round, so each is a legitimate resume.
    public void NoteBetweenRoundCast() => NoteBetweenRoundCast(manual: false);

    // Manual (hand-typed) between-round cast, routed here by OutboundCastObserver.
    // Flagged so the spell-mode resume can rate-limit a mashed burst (see
    // ManualResumePacing) without ever throttling the engine's per-round resumes.
    public void NoteManualBetweenRoundCast() => NoteBetweenRoundCast(manual: true);

    // ----- Manual user-attack override -------------------------------------
    // When the user hand-types an attack this round — a combat spell (round energy
    // 1–1000, see CombatSpellIndex / GAME_MECHANICS) or a physical attack verb — they
    // took the round's action, so the engine must NOT re-send its own auto attack until
    // the next round. Set here, checked by the between-round resume (TryResumeEngage),
    // and cleared at the top of the next combat tick so control returns next round. A
    // hand-cast IN-BETWEEN spell (heal / buff / cure, energy 0) is NOT an override — it
    // keeps the resume-after-cast behaviour (NoteManualBetweenRoundCast).
    private bool _userAttackOverride;
    // One-shot echo claim: SendAttack stamps the verb it just sent so the attack
    // observer — which sees the engine's OWN swings too — drops that echo instead of
    // reading it as a manual override. Consumed synchronously as the send flows back
    // through SendUserInput's observer fan-out.
    private string? _pendingAttackEchoVerb;
    // cast-code → is it a combat spell (round energy 1–1000)? Wired from AppServices.
    private Func<string, bool>? _isCombatSpell;

    public void SetCombatSpellPredicate(Func<string, bool> isCombatSpell)
        => _isCombatSpell = isCombatSpell ?? throw new ArgumentNullException(nameof(isCombatSpell));

    // A manually-typed cast-code (routed by OutboundCastObserver). A combat spell is the
    // user taking the round's attack (override); an in-between spell keeps the resume.
    // The optional target lets the override mark a manually-engaged passive neutral.
    public void OnManualCastObserved(string castCode, string? target = null)
    {
        if (_isCombatSpell?.Invoke(castCode) == true)
            NoteUserAttackOverride($"manual combat spell '{castCode}'", target);
        else
            NoteManualBetweenRoundCast();
    }

    // A manually-typed physical attack verb (routed by OutboundAttackObserver). The
    // observer sees the engine's own swings too, so drop the one we just sent (echo
    // claim) and treat anything else as the user taking the round's attack.
    public void NoteAttackCommandObserved(string verb, string? target = null)
    {
        if (_pendingAttackEchoVerb is { } pending
            && string.Equals(pending, verb, StringComparison.OrdinalIgnoreCase))
        {
            _pendingAttackEchoVerb = null;   // our own send — consume the echo
            return;
        }
        NoteUserAttackOverride($"manual physical attack '{verb}'", target);
    }

    private void NoteUserAttackOverride(string reason, string? target = null)
    {
        _userAttackOverride = true;
        _log?.Combat(LogCategory,
            $"user attack override armed ({reason}) — holding the engine's auto attack until next round");
        MarkUserEngagedNeutral(target);
    }

    // A user who hand-attacks a PASSIVE neutral (Neutral relationship, not KillOnSight)
    // turns it hostile — once hit it keeps swinging back until dead — so the engine takes
    // over finishing it, exactly as if it were an enemy (see MonsterEngagement's
    // per-instance override). Only passive neutrals are marked: enemies and KillOnSight
    // neutrals already engage on their own, and a Friend/Flee target the user swung at is
    // theirs to manage. Keyed by RawName to match the rest of the engine's target space,
    // and set as _currentTarget so the takeover engages this instance next round.
    private void MarkUserEngagedNeutral(string? target)
    {
        if (ResolveManualTarget(target) is not { } cand) return;
        MonsterOverlay overlay = ResolveOverlay(cand.MonsterNumber);
        bool passiveNeutral = (overlay.Relationship ?? MonsterRelationship.Enemy)
                                  == MonsterRelationship.Neutral
                              && overlay.KillOnSight != true;
        if (!passiveNeutral) return;
        if (_userEngagedInstances.Add(cand.RawName))
            _log?.Combat(LogCategory,
                $"user hand-engaged passive neutral '{cand.RawName}' — engine takes over killing it");
        _currentTarget = cand.RawName;
    }

    // Resolve the room instance a manual attack/cast aimed at. The user types an
    // ABBREVIATED target to engage as fast as possible ("a rat" for "giant rat"), so
    // match the typed token against the full name, a name-prefix, or any word-prefix —
    // mirroring how the server resolves a short attack. Falls back to the current engine
    // target when the send carried no target (a bare "a" / self-targeting cast code).
    private EngageableCandidate? ResolveManualTarget(string? typed)
    {
        if (_classifier.Current is not { } obs) return null;
        string? name = string.IsNullOrWhiteSpace(typed) ? _currentTarget : typed.Trim();
        if (string.IsNullOrWhiteSpace(name)) return null;

        // Exact RawName/ResolvedName first — covers the engine's own full-name sends
        // and the _currentTarget fallback (both RawNames).
        if (TryBuildCandidate(obs, name) is { } exact) return exact;

        for (int i = 0; i < obs.Entities.Count; i++)
        {
            RoomEntity e = obs.Entities[i];
            if (e.Kind != EntityKind.Monster) continue;
            if (e.MonsterNumber is not int n) continue;
            if (!NameMatchesTyped(e.RawName, name) && !NameMatchesTyped(e.ResolvedName, name))
                continue;
            MonsterOverlay overlay = ResolveOverlay(n);
            return new EngageableCandidate(
                RawName:         e.RawName,
                ResolvedName:    e.ResolvedName,
                MonsterNumber:   n,
                Priority:        overlay.Priority ?? MonsterAttackPriority.Normal,
                AppearanceIndex: i,
                DontBackstab:    overlay.DontBackstab ?? false);
        }
        return null;
    }

    private static bool NameMatchesTyped(string? name, string typed)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string word in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (word.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // RawNames the user hand-engaged this room (passive neutrals turned hostile). Cleared
    // per-observation for names no longer present (prune-on-observe in OnEntitiesObserved)
    // so the takeover ends when the instance dies or the room changes. Keyed on RawName to
    // match _currentTarget and the overlay-vs-instance split MonsterEngagement documents.
    private readonly HashSet<string> _userEngagedInstances = new(StringComparer.OrdinalIgnoreCase);

    public bool IsUserEngagedInstance(string rawName) => _userEngagedInstances.Contains(rawName);

    // Cleared at the top of each combat tick (the round boundary) so the engine resumes
    // its auto attack next round. Called from the Spells partial's OnCombatTick.
    private void ClearUserAttackOverrideForNewRound()
    {
        if (!_userAttackOverride) return;
        _userAttackOverride = false;
        _log?.Combat(LogCategory, "user attack override cleared — new combat round, engine resumes");
    }

    private void NoteBetweenRoundCast(bool manual)
    {
        _betweenRoundCastAt = DateTimeOffset.Now;
        _lastBetweenRoundCastManual = manual;
        if (_castingSpellTarget is not null)
            _spellAttackOwed = true;
        // Start of the resume-timing chain: a between-round cast (a survival heal /
        // buff) will drop *Combat Off* and stop our sustained attack, so the next
        // Off within the window should re-engage. Logged so the cast→Off→resume
        // sequence is traceable end to end (report paradigm-20260813-065138).
        _log?.Combat(LogCategory,
            $"between-round cast noted ({(manual ? "manual" : "engine")}) — resume armed "
            + $"(spellTarget={_castingSpellTarget ?? "(none)"})");
    }

    // "You gain N experience." — the prompt kill signal. Only a kill grants exp, and
    // it lands on the wire BEFORE the kill's *Combat Off* (death flavour → exp → Off),
    // so recognise the kill HERE when we've committed an attack at a current target,
    // rather than waiting for the Off (OnCombatStatus, the backstop). Waiting let the
    // round's alternate attack corpse-cast at the just-killed mob — lbol kills the
    // rotworm, then `mmis rotworm` goes out at the corpse → "You don't see rotworm
    // here!" (report paradigm-20260814-230258). This is generic: the exp line is
    // identical for every monster, so no per-monster death message is needed. Skip
    // when a specific death line already dropped this kill (avoid a double-drop of a
    // freshly re-picked target).
    private void OnUserGainExperience(MatchResult _)
    {
        _lastExpGainAt = DateTimeOffset.Now;
        _expGainsThisRound++;
        if (_currentTarget is not null
            && _attackSentSinceDeath
            && DateTimeOffset.Now - _lastMatchedDeathAt >= DeathInterruptWindow)
        {
            DropTargetForInferredKill(
                "kill inferred from exp gain (prompt, pre-*Combat Off*) — dropping target so the round's alternate can't corpse-cast");
        }

        // AoE multi-kill MID-ROUND: a 2nd+ exp gain in the same round means an AoE
        // dropped several mobs at once. NoteUnattributedDeath's re-parse only fires on
        // the trailing *Combat Off* — but when a survivor keeps combat continuously
        // engaged, that Off never lands, so the stale EnemyCount keeps the engine (and
        // the server's auto-repeat) firing the room spell at the lone survivor round
        // after round (report paradigm-20260818-052120: "FBAL keeps rooming despite only
        // Queen Ant left, until I hit enter"). Force the roster re-parse now so the next
        // observation is the TRUE roster and the chooser drops to single-target once the
        // live count falls below the room spell's MinEnemies. Debounced + reset per round,
        // so this and the Off-path never double-CR and a normal one-kill-per-round fight
        // (exp count 1) never trips it.
        if (_expGainsThisRound >= 2)
            ForceAoeMultiKillReparse("AoE multi-kill mid-round");
    }

    // AoE room-wipe recovery: drop all combat state and force ONE debounced CR
    // re-parse so the next observation is the TRUE roster, re-picking from what's
    // actually left rather than a survivor the kills haven't cleared yet. Shared by
    // the *Combat Off* path (NoteUnattributedDeath) and the mid-round exp-gain path
    // (OnUserGainExperience) — the latter catches the survivor-keeps-combat case
    // where no Off ever lands.
    private void ForceAoeMultiKillReparse(string context)
    {
        _lastDeathAt = DateTimeOffset.Now;
        _attackSentSinceDeath = false;
        _currentTarget = null;
        _castingSpellTarget = null;
        _inferredKillPendingRemoval = null;
        ClearBackstabResolution();

        // When this round's kills account for EVERY engageable mob the roster still
        // lists, the AoE emptied the room — drop the stale roster NOW (an empty
        // observation) so the combat gate releases on the CR round-trip instead of
        // waiting out the 6s idle-stall watchdog. An emptied room's bare-CR re-display
        // carries no "Also here:" line, so without this the classifier fires no
        // observation and the gate sits held ~6s after every AoE room-wipe (report
        // paradigm-20260827-081208, an hsto mage's every fight). SAFE: it fires only
        // when kills >= the listed roster, so a survivor (fewer kills than mobs) leaves
        // the roster intact and the CR re-parse below still handles it; the CR also
        // re-asserts any hostile that arrived unlisted, exactly the idle-stall
        // watchdog's own optimistic-clear-plus-safety-probe pattern.
        int listed = _classifier.Current is { } cur ? CountEngageable(cur) : 0;
        if (listed > 0 && _expGainsThisRound >= listed)
        {
            _log?.Combat(LogCategory,
                $"AoE multi-kill ({_expGainsThisRound} exp) cleared all {listed} listed hostile(s) — "
                + "dropping the stale roster so the combat gate releases without the idle-stall wait");
            _classifier.NoteRoomChanged();
        }

        if (TrySendRoomRefresh(context))
            _log?.Combat(LogCategory,
                $"AoE multi-kill ({_expGainsThisRound} exp this round) — re-parsing room with "
                + "CR to re-pick from the true roster instead of firing at a survivor");
    }

    // Drop the current target for a kill inferred from an exp gain. Nulls both
    // _currentTarget and _castingSpellTarget and resets the cascade so the round's
    // next action can't re-attack the corpse. Keeping _currentTarget left a same-named
    // sibling looking like the still-engaged target (the RawName-keyed "already
    // engaged" guard), so it was never re-engaged AND inherited the dead mob's
    // advanced cascade — a fresh mob opening on the alternate instead of the normal.
    private void DropTargetForInferredKill(string reason)
    {
        _lastExpGainAt = DateTimeOffset.MinValue;   // consume — a later non-kill Off must not reuse it
        _lastDeathAt = DateTimeOffset.Now;
        _attackSentSinceDeath = false;
        // Hand the corpse's identity to the Off-path roster removal (NoteUnattributedDeath):
        // we're nulling _currentTarget here, so without this it can't tell which mob to drop.
        _inferredKillPendingRemoval = _currentTarget;
        _inferredKillPendingAt = DateTimeOffset.Now;
        _currentTarget = null;
        _castingSpellTarget = null;
        _spellChooser.ResetForNewTarget();
        _log?.Combat(LogCategory, reason);
    }

    // Track *Combat On*/*Combat Off*. Off arms the resume-after-interrupt path
    // (see _combatOff); Engaged means the server is swinging for us again, so we
    // disarm it.
    private void OnCombatStatus(MatchResult match)
    {
        if (match.Groups.Count == 0) return;
        string status = match.Groups[0];
        if (string.Equals(status, "Off", StringComparison.OrdinalIgnoreCase))
        {
            _combatOff = true;
            _engageConfirmed = false;

            // A pre-attack debuff already fired this round's combat attack immediately
            // (DeferPostDebuffAttack); every *Combat Off* carrying that debuff's
            // between-round stamp must NOT run either resume below, or the attack
            // double-fires. Matched by stamp (not a one-shot flag) so a quick second
            // Off from casting the attack between rounds is caught too; a genuine later
            // between-round cast re-stamps _betweenRoundCastAt and resumes normally. The
            // MinValue guard keeps a fight that never armed the suppress (both fields at
            // their MinValue default) from matching itself on every ordinary Off.
            bool suppressBetweenRoundResume =
                _suppressResumeForBetweenRoundStamp != DateTimeOffset.MinValue
                && _betweenRoundCastAt == _suppressResumeForBetweenRoundStamp;
            if (suppressBetweenRoundResume)
                _log?.Combat(LogCategory,
                    "*Combat Off* carries the pre-attack debuff's stamp — between-round-cast "
                    + "resume suppressed (the combat attack already fired this round)");

            // Prompt kill drop. A fresh exp gain explains this Off (wire: death →
            // exp → Off) while NO between-round survival cast does — so it's our
            // kill's Off, not a mid-fight heal's. The custom per-monster death
            // message didn't match, so without this the resume paths below re-attack
            // the corpse ("You don't see X here!") and stall a round waiting out the
            // recovery — the reported "won't re-engage after a kill until the next
            // NPC attacks." Arm the existing death-suppression guards (stamp
            // _lastDeathAt) and drop spell mode so the heartbeat can't re-cast at the
            // corpse; the death→re-observe re-picks the survivor. The no-between-
            // round-cast gate keeps a heal's Off — or a party share-exp landing
            // beside one — from being misread as a kill (that path resumes below).
            if (_currentTarget is not null
                && DateTimeOffset.Now - _lastExpGainAt < ExpKillWindow
                && DateTimeOffset.Now - _betweenRoundCastAt >= CastInterruptResumeWindow
                // ...and NO matched death line just handled this kill. If one did,
                // it already dropped the corpse and re-picked the next survivor —
                // this *Combat Off* is that kill's Off, so inferring a second kill
                // here would drop the fresh target and re-attack it (double-fire).
                && DateTimeOffset.Now - _lastMatchedDeathAt >= DeathInterruptWindow)
            {
                DropTargetForInferredKill(
                    "kill inferred from exp + *Combat Off* (no between-round cast) — " +
                    "dropping target so the resume can't re-attack the corpse");
            }

            // If this Off is the server's response to a between-round cast
            // we just fired (self-heal / cure / buff), re-issue the weapon
            // attack now rather than waiting for the mob's next swing or the
            // next combat tick — the "used a heal mid-fight, then stood idle
            // a full round" symptom. Attributed strictly to our own cast
            // (armed by NoteBetweenRoundCast) so a non-sustaining attack's
            // per-strike Off (KAI pummel) is never misread. Weapon mode only;
            // TryResumeEngage's pacing is the backstop against any double-fire
            // with the tick resume. Presence of a live target is proven by
            // HasEngageable, NOT by _currentTarget: a between-round self-heal
            // can land right after a prior swing dropped the target (an
            // unattributed-death / command-no-effect resync nulls it), and
            // ResumeEngage re-picks from the room anyway. Requiring
            // _currentTarget here stranded exactly that case — the reported
            // "cast mihe, then missed a combat round before re-attacking".
            //
            // But NOT when a kill just fired this burst AND its re-observe still
            // owes us a re-engage: the mob's dying *Combat Off* also lands inside
            // CastInterruptResumeWindow, and re-engaging on it would re-pick from a
            // roster the kill's forced re-display hasn't cleared yet — sending
            // "aa <corpse>" at the emptied room (the reported phantom attack).
            // bypassAttackGuard makes it worse by defeating the very
            // ResumeAfterAttackGuard that suppresses a double-send on a kill. Skip
            // for DeathInterruptWindow and let the death→re-observe path own the
            // re-engage from the resynced roster.
            //
            // The suppression only holds while that re-observe hasn't run yet. Once
            // it has re-picked a survivor and swung (_attackSentSinceDeath), the
            // roster is already clean and this Off is the CAST interrupting that
            // fresh swing — not the kill's Off. Suppressing it there strands the
            // survivor for a full round (the reported "cast a buff mid-fight, then
            // missed a combat round before re-attacking"), so resume.
            //
            // But bypass the attack-guard ONLY when the cast came at/after the last
            // swing — that's what proves the cast interrupted the swing, so the
            // "a fresh swing is still going" assumption behind ResumeAfterAttackGuard
            // is genuinely false. When a swing went out AFTER the cast (a kill's
            // death→re-observe re-engaged the survivor a beat after an earlier
            // between-round bless), that swing IS fresh and in flight; bypassing the
            // guard there fires a redundant second `aa <target>` on top of it — the
            // reported "doubled down on the physical attack". Fall through to the
            // guarded resume so ResumeAfterAttackGuard suppresses the double.
            // Diagnostic for the "won't re-engage after a mid-fight buff/heal" class
            // of stall (report paradigm-20260813-065138): while a between-round cast
            // is still within the resume window, log the exact state that decides the
            // weapon- and spell-mode resumes below, so a capture shows why a resume
            // did or didn't fire instead of leaving it invisible. Combat-level, so
            // it only surfaces with combat diagnostics on.
            if (DateTimeOffset.Now - _betweenRoundCastAt < CastInterruptResumeWindow)
                _log?.Combat(LogCategory,
                    $"*Combat Off* in resume window ({(DateTimeOffset.Now - _betweenRoundCastAt).TotalMilliseconds:F0}ms "
                    + $"after cast): spellTarget={_castingSpellTarget ?? "(none)"}, "
                    + $"engageable={_classifier.Current is { } cc && HasEngageable(cc)}, "
                    + $"sinceDeath={(DateTimeOffset.Now - _lastDeathAt).TotalMilliseconds:F0}ms, "
                    + $"castAtOrAfterLastSwing={_lastAttackSentAt <= _betweenRoundCastAt}, "
                    + $"spellResumeAlreadyFired={_betweenRoundCastAt == _lastSpellResumeForBetweenRoundCastAt}");

            if (!suppressBetweenRoundResume
                && DateTimeOffset.Now - _betweenRoundCastAt < CastInterruptResumeWindow
                && _castingSpellTarget is null
                && _classifier.Current is { } live
                && HasEngageable(live))
            {
                if (DateTimeOffset.Now - _lastDeathAt < DeathInterruptWindow
                    && !_attackSentSinceDeath)
                    _log?.Combat(LogCategory,
                        "between-round-cast resume suppressed — kill this burst; " +
                        "deferring re-engage to the death→re-observe path");
                else
                {
                    _log?.Combat(LogCategory, "between-round-cast resume → re-engaging weapon attack");
                    TryResumeEngage(live, bypassAttackGuard: _lastAttackSentAt <= _betweenRoundCastAt);
                }
            }

            // Spell analogue of the weapon resume above. A spell attack auto-repeats
            // server-side, but a between-round cast (a survival heal) drops *Combat
            // Off* and stops that repeat, so re-announce our spell. A kill already
            // cleared _castingSpellTarget, so this fires only on a true interrupt (not
            // a kill's Off); it re-decides through the chooser (DispatchRoundAction),
            // so a lapsed spell condition switches cleanly, and it forces a fresh
            // announce because the server dropped the repeat.
            // The per-interrupt guard (_betweenRoundCastAt != _last…) caps ONE
            // re-announce per between-round-cast stamp — enough for the engine's own
            // survival casts (each is a distinct round). But a user MASHING manual
            // casts (e.g. a room/utility spell) re-stamps _betweenRoundCastAt each
            // time, so each fresh stamp fires its own re-attack: a burst as fast as
            // they type (the reported manual-cast → mmis spam). Rate-limit ONLY the
            // manual-armed resume (ManualResumePacing) — the engine's per-round
            // survival casts must keep resuming every round, so they're never paced.
            //
            // DeathInterruptWindow normally blocks this right after a kill so a resume
            // can't re-announce at the corpse. But when a kill THIS burst left a
            // survivor in a multi-mob room, the death→re-observe already re-picked that
            // live survivor as BOTH _currentTarget and _castingSpellTarget — and its
            // re-cast lost the round's slot to the 500ms burst guard, leaving us parked
            // in _combatOff spell mode with no heartbeat retry (OnCombatTick bails while
            // _combatOff, and its deterministic resume is weapon-mode only). Nothing then
            // re-engaged until the survivor's OWN swing woke OnCombatLine ~5s later —
            // report paradigm-20260815-202319 ("not re-engaging combat after buffing
            // mid-combat"): buff armr → rotworm dies to lbol → survivor thin leprous
            // outcast sat un-attacked a full round. So when the current target IS the
            // spell target (the re-pick chose this exact mob) AND it's proven present in
            // the resynced roster (TargetPresent below), it's a survivor, not the
            // corpse — bypass the death window and resume immediately. The corpse case
            // can't reach here: a kill nulls _currentTarget, and a re-pick only re-sets
            // it to a mob RemoveDeadEntity left in the live roster.
            bool survivorReadied = _currentTarget is { } curT
                && string.Equals(curT, _castingSpellTarget, StringComparison.OrdinalIgnoreCase);
            bool manualPaced = _lastBetweenRoundCastManual
                && DateTimeOffset.Now - _lastManualCastResumeAt < ManualResumePacing;
            if (!suppressBetweenRoundResume
                && DateTimeOffset.Now - _betweenRoundCastAt < CastInterruptResumeWindow
                && _betweenRoundCastAt != _lastSpellResumeForBetweenRoundCastAt
                && !manualPaced
                && !_userAttackOverride   // user hand-typed this round's attack — hold our own until next round
                && _castingSpellTarget is { } spellTarget
                && (DateTimeOffset.Now - _lastDeathAt >= DeathInterruptWindow || survivorReadied)
                && _classifier.Current is { } liveSpell
                && TargetPresent(liveSpell, spellTarget)
                && TryBuildCandidate(liveSpell, spellTarget) is { } spellCand)
            {
                if (_lastBetweenRoundCastManual) _lastManualCastResumeAt = DateTimeOffset.Now;
                // Clear the interrupt flag before attempting the re-announce, mirroring
                // ResumeEngage's weapon-mode path — NOT only inside DispatchRoundAction's
                // TryCast-succeeded branch. This attempt can lose the round's single cast
                // slot to a survival heal firing the same instant (CastingDirector.Evaluate
                // runs immediately on the HP change that triggered this whole interrupt, not
                // just on the tick boundary), and DispatchRoundAction's own comment ("stay in
                // spell mode and retry next tick") only holds if OnCombatTick's heartbeat can
                // actually run next tick — it bails immediately while _combatOff is true. Left
                // set here, a single lost race parks the engine in spell mode with no path back
                // to a retry: the reported "won't re-engage after buffing/healing, sits there
                // until manual input" stall.
                // The interrupted attack spell held the round the between-round cast
                // just closed. RoundDamageTracker tie-breaks ahead of us on CombatStatus,
                // so it has already CloseCurrent'd that round (RoundCount++) — tally it
                // toward MaxCasts NOW, before the re-decide inside DispatchRoundAction.
                // The heartbeat can't (it bails while _combatOff), so without this the
                // resume re-announces the just-capped spell uncapped (LBOL 2x, report
                // paradigm-20260820-063541). Round-delta gated so a heal that interrupted
                // BEFORE the spell's first fire (no round opened) doesn't over-count;
                // death-window gated so the kill-left-a-survivor path (a legitimately
                // fresh cast on the survivor) is untouched.
                if (_announcedSpellCode is { } interruptedSpell
                    && _lastCastAction is { } interruptedAction
                    && DateTimeOffset.Now - _lastDeathAt >= DeathInterruptWindow
                    && ReadRoundCount?.Invoke() is { } interruptedRound
                    && interruptedRound != _lastTalliedRound)
                {
                    _spellChooser.MarkCast(
                        new CombatSpellDecision(interruptedAction, interruptedSpell), spellTarget);
                    _lastTalliedRound = interruptedRound;
                }
                _combatOff = false;
                _announcedSpellCode = null;
                // Mark this specific interrupt as resumed BEFORE dispatching — the
                // resume's own cast drops its own *Combat Off* a moment later, and
                // without this stamped first that Off would satisfy the exact same
                // condition above and re-fire again (see
                // _lastSpellResumeForBetweenRoundCastAt).
                _lastSpellResumeForBetweenRoundCastAt = _betweenRoundCastAt;
                _log?.Combat(LogCategory,
                    $"between-round-cast resume → re-announcing attack spell on {spellTarget}");
                // bypassRecastInterval: this re-attack lands within 500ms of the
                // survival buff/heal that dropped *Combat Off*; without the bypass the
                // burst guard defers it to the next tick and the mob swings free (the
                // "broke combat to cast armr, didn't re-attack until after they swung"
                // report). This resume is now guarded to fire once per interrupt (see
                // above), so the bypass can't compound into a burst.
                DispatchRoundAction(_readSettings(), spellCand, CountEngageable(liveSpell), liveSpell,
                    bypassRecastInterval: true);
            }

            // Guard-redirect recovery. When a guarded monster is our priority, each
            // guard death fires this Off; re-attack the priority by name so the next
            // guard steps in (or, once the last guard falls, the swing finally lands
            // on the chief). Placed after the cast-resume so its shared pacing stamps
            // suppress a double-send, and gated on _guardBlockedTarget so it's inert
            // outside a guard fight. This is the automated form of the manual
            // "aa <priority>" a player otherwise has to send when the last guard's
            // death desyncs the roster and the normal resume goes silent.
            TryGuardRetry();
        }
        else if (string.Equals(status, "Engaged", StringComparison.OrdinalIgnoreCase))
        {
            _combatOff = false;
            // Server acknowledged the swing — disarm the engage-verify
            // safety net; our attack landed on a real, present target.
            _engageConfirmed = true;
            _awaitingEngageSince = null;
        }
    }

    // Guard/redirect recognition — "<guard> moves to protect <priority>". While a
    // guarded monster (brigand chief) is shielded, our swing lands on the guard,
    // not the chief. We don't move _currentTarget onto the guard (the server is
    // already swinging the guard for us, and leaving _currentTarget on the chief
    // keeps its death line attributable); we just remember the priority so each
    // guard's death re-attacks it (TryGuardRetry). Only acts when the protected
    // monster is the one we're already trying to kill — a protect line shielding
    // some other room monster isn't ours to chase.
    private void OnMonsterProtect(MatchResult match)
    {
        if (!_isEnabled()) return;
        if (match.Groups.Count < 2) return;
        string guard = match.Groups[0].Trim();
        string protectedName = match.Groups[1].Trim();
        if (guard.Length == 0 || protectedName.Length == 0) return;

        bool isOurs =
            string.Equals(protectedName, _currentTarget, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(protectedName, _guardBlockedTarget, StringComparison.OrdinalIgnoreCase);
        if (!isOurs) return;

        if (!string.Equals(_guardBlockedTarget, protectedName, StringComparison.OrdinalIgnoreCase))
            _log?.Combat(LogCategory,
                $"guard redirect — '{guard}' shields priority '{protectedName}'; " +
                "will re-attack the priority as each guard falls");
        _guardBlockedTarget = protectedName;
    }

    // Re-attack a guard-shielded priority after a guard fell (driven by the *Combat
    // Off* each guard death emits). Sends a literal "aa <priority>" — the server
    // redirects it to any remaining guard (another protect line re-affirms the
    // block) or, once the last guard is dead, engages the priority directly. Paced
    // by the same stamps the interrupt-resume uses so it can't double with a normal
    // resume that already re-engaged this round, and stays quiet outside a guard
    // fight (_guardBlockedTarget null). Literal-name attack (not the room-view
    // chooser) on purpose: the failing case is precisely the one where the priority
    // has dropped from the room view, so a candidate can't be built.
    private void TryGuardRetry()
    {
        if (_guardBlockedTarget is not { Length: > 0 } priority) return;
        if (!_isEnabled()) return;
        if (_castingSpellTarget is not null) return;   // spell mode owns its re-cast
        if (_wireSender is null) return;

        DateTimeOffset now = DateTimeOffset.Now;
        if (now - _lastAttackSentAt < ResumeAfterAttackGuard) return;
        if (now - _lastInterruptResumeAt < ResumePacing) return;
        _lastInterruptResumeAt = now;

        _currentTarget = priority;
        _log?.Combat(LogCategory, $"guard retry — re-attacking priority '{priority}' after guard fell");
        SendAttack(_readSettings().NormalAttackCommand, priority, refire: true,
                   refireReason: "guard retry");
    }

    // Public current-room engageability probe for consumers (AutoSearch) that must
    // gate on THIS room's live roster, not the sticky cross-room combat gate
    // (HasEngageableHostiles), which stays asserted while combat winds down on a
    // left-behind target and made AutoSearch skip genuinely-empty rooms (report
    // paradigm-20260820-090736). Null (no observation yet) reads as "no hostile".
    public bool HasEngageableIn(RoomEntitiesObservation? obs) => obs is { } o && HasEngageable(o);

    private bool HasEngageable(RoomEntitiesObservation obs)
    {
        for (int i = 0; i < obs.Entities.Count; i++)
        {
            RoomEntity e = obs.Entities[i];
            if (e.Kind != EntityKind.Monster) continue;
            if (e.MonsterNumber is not int n) return true; // unknown → assume engageable
            MonsterOverlay overlay = ResolveOverlay(n);
            if (MonsterEngagement.IsEngageable(overlay, _userEngagedInstances.Contains(e.RawName)))
                return true;
        }
        return false;
    }

    private bool IsPartyMember(string name)
    {
        // Announce lines carry the GIVEN name ("Raijin"), while PartyMember.Name
        // holds the full `par` name ("Raijin WuzHere"). Match by given name — the
        // party-layer convention everywhere else — or AttackLastParty never
        // recognises a full-named member and the re-fire silently never happens.
        string given = GivenName(name);
        foreach (PartyMember m in _party.Members)
        {
            if (string.Equals(GivenName(m.Name), given, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // First whitespace-delimited token. MajorMUD announce / chat lines address
    // players by given name only, so all announcer-vs-roster matching normalises
    // to it (mirrors PartyManager / PartyPoller).
    private static string GivenName(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }

    private MonsterOverlay ResolveOverlay(int monsterNumber)
    {
        try { return _resolveOverlay(monsterNumber) ?? new MonsterOverlay(); }
        catch
        {
            // Resolver failure (no active set, malformed override file)
            // → fall back to defaults so the engine isn't wedged.
            return new MonsterOverlay();
        }
    }


    private void SendAttack(string command, string target, MonsterAttackPriority? priority = null)
    {
        // Held while AttackPrevented is active — send nothing; the per-round
        // heartbeat re-dispatches once the block clears.
        if (AttacksBlocked()) return;
        // We're swinging again — disarm the interrupt-resume path so the
        // next mob line doesn't re-fire on top of this attack.
        _combatOff = false;
        // Any weapon swing exits spell mode — the server auto-repeats the
        // swing, so the tick heartbeat must stop re-casting for this target.
        _castingSpellTarget = null;
        _lastCastAction = null;
        string verb = string.IsNullOrWhiteSpace(command) ? "a" : command.Trim();
        string line = $"{verb} {target}";
        if (priority is { } prio)
            _log?.Combat(LogCategory, $"attack target={target} cmd={verb} prio={prio}");
        else
            _log?.Combat(LogCategory, $"attack target={target} cmd={verb}");
        _lastAttackCommand = line;
        if (_wireSender is null) return;
        _pendingAttackEchoVerb = verb;   // claim our own swing so the attack observer doesn't read it as manual
        _wireSender(Encoding.Latin1.GetBytes(line + "\r"));
        NoteAttackSent();
    }

    private void SendAttack(string command, string target, bool refire, string refireReason)
    {
        if (AttacksBlocked()) return;   // held while AttackPrevented is active
        _combatOff = false;
        _castingSpellTarget = null;
        _lastCastAction = null;
        string verb = string.IsNullOrWhiteSpace(command) ? "a" : command.Trim();
        string line = $"{verb} {target}";
        _log?.Combat(LogCategory,
            $"re-fire target={target} cmd={verb} timing={refireReason}");
        _lastAttackCommand = line;
        if (_wireSender is null) return;
        _pendingAttackEchoVerb = verb;   // claim our own swing so the attack observer doesn't read it as manual
        _wireSender(Encoding.Latin1.GetBytes(line + "\r"));
        NoteAttackSent();
    }

    // Lost-action recovery, driven by ConditionTracker.ActionFailed (wired in
    // AppServices) — fires for ANY line flagged LastActionFailed: an action you
    // sent that the server refused or ate without executing. It isn't confusion-
    // specific; a confused "You fumble in confusion!" is just one line that can
    // carry the flag (confusion doesn't block attacking — each command can be
    // eaten, so the server never engages and the target sits unattacked, the
    // reported "monsters present but not attacked unless I re-send" symptom). We
    // re-send the last weapon swing verbatim, once per failure line, so the
    // server's own echoes pace the retries; once a swing lands the server
    // auto-repeats and the failures stop. Weapon mode only — spell mode re-issues
    // its cast on the per-round tick (OnCombatTick), and _lastAttackCommand holds a
    // weapon verb we must not fire into a spell fight.
    public void OnActionFailed()
    {
        if (_disposed || !_isEnabled()) return;
        if (_castingSpellTarget is not null) return;
        if (_currentTarget is null) return;
        if (_lastAttackCommand is not { Length: > 0 } line) return;
        if (_wireSender is null) return;
        if (AttacksBlocked()) return;   // can't re-send a swing while attacks are blocked

        _combatOff = false;
        _log?.Combat(LogCategory, $"action failed — re-sending last attack '{line}'");
        _wireSender(Encoding.Latin1.GetBytes(line + "\r"));
        NoteAttackSent();
    }

    // Arm the engage-verification timer after a fresh attack goes out. No-op once
    // the server has confirmed engagement (subsequent rounds of an
    // already-acknowledged fight don't re-arm — only the first unconfirmed swing
    // matters). VerifyEngagement consumes the timestamp on the combat tick.
    private void NoteAttackSent()
    {
        // Stamp every real swing unconditionally — the interrupt-resume guard
        // reads this even when engagement is already confirmed (the death→re-
        // observe re-engage happens mid-fight, long after Engaged).
        _lastAttackSentAt = DateTimeOffset.Now;
        // A swing since the last death means the death→re-observe path has run;
        // a later cast-interrupt Off should resume, not stand down (see
        // _attackSentSinceDeath).
        _attackSentSinceDeath = true;
        // The owed attack just went out — CastingDirector is clear to fire another
        // survival cast next round (see _spellAttackOwed). No-op for a weapon swing,
        // which never set this in the first place.
        _spellAttackOwed = false;
        if (_engageConfirmed) return;
        _awaitingEngageSince ??= DateTimeOffset.Now;
    }

    // Engage-verification safety net, run on every combat tick. If we sent an
    // attack and the server never answered with *Combat Engaged* within
    // EngageConfirmWindow, the named target isn't in the room we can actually see
    // (a movement was in flight when we swung, or the mob walked out / was
    // replaced). Drop the stale target and fire a bare CR to force a fresh room
    // display so OnEntitiesObserved re-picks from what's really here. Shares the
    // RoomRefreshCooldown debounce with the other CR-refresh paths.
    private void VerifyEngagement()
    {
        if (_awaitingEngageSince is not { } since) return;
        if (!_isEnabled()) { _awaitingEngageSince = null; return; }
        if (_engageConfirmed) { _awaitingEngageSince = null; return; }
        if (DateTimeOffset.Now - since < EngageConfirmWindow) return;

        // Window elapsed with no server confirmation — the swing hit a
        // stale room view. Disarm regardless so we don't loop on the same
        // unanswered attack.
        _awaitingEngageSince = null;
        _log?.Combat(LogCategory,
            $"attack unconfirmed after {EngageConfirmWindow.TotalSeconds:0}s " +
            $"(target={_currentTarget ?? _castingSpellTarget ?? "?"}) — CR re-display");
        _currentTarget = null;
        _castingSpellTarget = null;

        TrySendRoomRefresh("engage unconfirmed");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_cast is not null) _cast.CastFailed -= OnCombatCastFailed;
        _classifier.EntitiesObserved -= OnEntitiesObserved;
        _announceSub.Dispose();
        _castAnnounceSub.Dispose();
        _userHitsSub.Dispose();
        _mobHitsSub.Dispose();
        _mobMissesSub.Dispose();
        _targetGoneSub.Dispose();
        _weaponNoEffectSub.Dispose();
        _fistsNoEffectSub.Dispose();
        _spellNoEffectSub.Dispose();
        _commandNoEffectSub.Dispose();
        _combatStatusSub.Dispose();
        _expGainSub.Dispose();
        _monsterProtectSub.Dispose();
        _bsResolveHitsSub.Dispose();
        _bsResolveMissesSub.Dispose();
        _attackConfirmHitsSub.Dispose();
        _attackConfirmMissesSub.Dispose();
    }

    private readonly record struct EngageableCandidate(
        string RawName,
        string ResolvedName,
        int MonsterNumber,
        MonsterAttackPriority Priority,
        int AppearanceIndex,
        bool DontBackstab);
}
