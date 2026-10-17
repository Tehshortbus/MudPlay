using System.Text;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Combat;

// Auto-attack engine. Subscribes to RoomEntityClassifier.EntitiesObserved and
// (for re-fire pacing) to KnownPatterns.PartyAttackAnnounce. Picks a target per
// MonsterOverlay.Priority + CombatSettings.TargetOrder, filters out anything
// MonsterEngagement doesn't call engageable, and sends the configured attack command.
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
//   3. Engageable per MonsterEngagement: Enemy, a KillOnSight Neutral, or an
//      instance the user hand-engaged.
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

    // A `bs` we sent was answered: the line that answered it, and whether the stab
    // landed. Session Stats keeps backstabs' own hit / miss rate off it.
    public event Action<string, bool>? BackstabResolved;

    private readonly MessageRouter _router;
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
    private readonly IDisposable _roomPoisedSub;
    private readonly IDisposable _partyCombatSub;
    private readonly IDisposable _followDragSub;
    private readonly IDisposable _userHitsSub;
    private readonly IDisposable _mobHitsSub;
    private readonly IDisposable _mobMissesSub;
    private readonly IDisposable _targetGoneSub;
    private readonly IDisposable _weaponNoEffectSub;
    private readonly IDisposable _weaponLandedSub;
    private readonly IDisposable _fistsNoEffectSub;
    private readonly IDisposable _spellNoEffectSub;
    private readonly IDisposable _commandNoEffectSub;
    private readonly IDisposable _combatStatusSub;
    private readonly IDisposable _expGainSub;
    private readonly IDisposable _monsterProtectSub;
    private readonly IDisposable _roomSpawnSub;
    private readonly IDisposable _bsResolveHitsSub;
    private readonly IDisposable _bsResolveMissesSub;
    private readonly IDisposable _attackConfirmHitsSub;
    private readonly IDisposable _attackConfirmMissesSub;
    // Self-defense: a monster swinging at us (hits / misses / we-dodge) makes the
    // engine fight back even if the monster isn't auto-engageable on its own.
    private readonly IDisposable _selfDefenseHitsSub;
    private readonly IDisposable _selfDefenseMissesSub;
    private readonly IDisposable _selfDefenseDodgesSub;
    // The router's readings of an attack on us, for the user's break hold.
    private readonly IDisposable[] _attackOnUsSubs;

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
    // Live worn-weapon reader (EquipmentManager.WornWeapon). Server-confirmed, so
    // it lags _usingAlternateWeapon by exactly the round-trip the belief flag skips
    // — which is what makes it the right witness for a no-effect line.
    private Func<string?>? _readWornWeapon;
    private Func<bool>? _isStealthed;
    // StealthManager.TakeSneakBrokeOnEntry — our sneak broke entering this room.
    private Func<bool>? _takeSneakBrokeOnEntry;
    // True while a plain walk-to (travel) drives — self-defense stands down then.
    private Func<bool>? _selfDefenseSuppressedByTravel;
    private Func<MonsterRelationship, bool>? _noAnswerComing;
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

    // Fear (ConditionTracker.IsFeared): the server refuses weapon attacks and attack
    // spells ("You are too afraid!"), but debuffs and the between-round heals / buffs
    // / cures still go out. null until wired → never blocks.
    private Func<bool>? _isFeared;

    // Edge-tracker so AttacksBlocked logs the hold and the release once each, not
    // once per suppressed send.
    private bool _attackBlockLogged;

    private string? _currentTarget;

    // True once this room's hostile count met the [min..max] engage window, i.e.
    // we committed to fighting here. Drives the "Kill all engaged" override: once
    // committed, we finish the room to empty even after kills drop the count below
    // the Min floor (see MonsterCountGate). Reset on a physical room change
    // (NotePreMove); exposed for CombatStateTracker's mirror of the same gate.
    private bool _committedToRoom;
    public bool HasCommittedToCurrentRoom => _committedToRoom;

    // True once we've swung/cast an attack in THIS room, i.e. the fight is underway.
    // Gates the AoE debuff under CombatSettings.AreaDebuffFirstRoundOnly: a debuff is
    // only worth landing on entry (before the first round softens nothing yet), so once
    // this flips the debuff is abandoned for the room. Set in NoteAttackSent, reset on a
    // physical room change (NotePreMove) — the same lifetime as the AoE-debuff per-room
    // cap it rides alongside.
    private bool _combatSeenThisRoom;

    // Guard-redirect memory. MajorMUD "guarded" monsters (a brigand chief guarded
    // by brigands) can't be hit directly while a guard is in the room — each swing
    // we aim at the chief is redirected to a guard, announced by "<guard> moves to
    // protect <chief>". We stash the intended priority here (leaving _currentTarget
    // on the chief) and re-attack it by name after each guard falls, until no guard
    // remains and the swing lands on the chief. Durable across the room-cleared
    // reset that a guard death's roster desync can trigger — that reset nulls
    // _currentTarget and disarms the normal resume, so this separate memory is what
    // drives recovery (the reported "chief left unattacked after the last guard
    // died" stall). Cleared when a room read no longer lists the priority, on a
    // target-not-here reply, and on room change.
    private string? _guardBlockedTarget;

    // The last kill while _guardBlockedTarget was set with a room re-display coming.
    // Until a room read newer than it says whether the priority still stands, the
    // guard retry waits, for at most GuardKillJudgeWait: a kill can be the
    // priority's own.
    private DateTimeOffset _guardFightKillAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan GuardKillJudgeWait = TimeSpan.FromSeconds(2);

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

    // Attack-not-last deferral (AttackTiming.AttackNotLast, party of 3+). The
    // inverse of attack-last: hold our own room-entry pick, then commit ONCE right
    // after the first party member's announce, and never re-fire. _awaitingNotLast
    // latches while holding; TryReleaseNotLastHold clears it on the first party
    // announce, OnPartyCombatLine on a party member seen fighting, and the in-round
    // deadline (ArmHoldDeadline) or OnCombatTick's fallback on our own pick if
    // neither comes. _notLastDeferBypass mirrors _followDeferBypass — set only
    // for the re-entrant dispatch so the hold doesn't re-latch.
    private bool _awaitingNotLast;
    private bool _notLastDeferBypass;

    // The fight in this room is already under way: our own attack went out here, or a
    // damage line showed a party member fighting a monster here (OnPartyCombatLine).
    // The not-last and follow holds only order the room's opening; once the party is
    // fighting there is no announce left to wait for, and waiting would cost a round
    // (user, 2026-10-17: "we should never drop a round because we didn't witness the
    // situations"). _followedTargetHere is the monster the followed player was last
    // seen hitting here, taken in place of waiting for their announce.
    //
    // Both, with the hold deadline, belong to the room they were seen in, so they are
    // dropped when our move is sent (NoteMoveSent). The tracker confirms the new room
    // only on its exits line, after the new room's "Also here:" has been read and
    // has latched its own hold, so NoteRoomLeft drops only what is older than that
    // roster read: what a teleport, a drag or a flee left from the room before.
    // _roomStateAt is when either was last set.
    //
    // A room changed with no move of ours sent and no drag line (a teleport, a drag
    // through a text exit, a fear run) shows its roster before anything says it is
    // another room, so a fight seen before a fresh roster read belongs to no known
    // room until that display's exits line places it (_rosterRoomPlaced): the
    // tracker's change of room (NoteRoomLeft), or the same room shown again
    // (NoteRoomShownAgain). Until then the holds wait as at any opening, with their
    // deadline, so the round is never lost to not knowing which room this is. A death
    // is a change of room to the tracker (it has us nowhere until the death room is
    // shown); leaving the game drops the fight outright (NoteGameLeft), since a relog
    // can land in the same room.
    private bool _fightUnderwayHere;
    private string? _followedTargetHere;
    private DateTimeOffset _roomStateAt = DateTimeOffset.MinValue;
    private bool _rosterRoomPlaced = true;

    private bool FightRoomUnplaced =>
        _fightUnderwayHere && !_rosterRoomPlaced && _roomStateAt < _rosterReadAt;

    // The holds' in-round deadline. Attack Order is the ideal place of our announce in
    // the party's order (user, 2026-10-17: "attack order is quite literally the ideal
    // attack announce order"), met when we can see the others' announces and never
    // worth a round. An announce made any time before the round fires counts for that
    // round (GAME_MECHANICS "Attack announce lines and round commits"), so a hold
    // nobody released engages HoldDeadlineMargin before the projected round. The
    // margin covers the arrival settle the engage may still pass through (350 ms) and
    // the trip to the game, against a projection a round seen on the wire keeps within
    // tens of milliseconds (the round runs 5.03 to 5.06 s). With no round placed
    // (_timeToNextRound null) the hold waits UnplacedHoldWait: party clients answer a
    // room display within the second (report paradigm-20261017-050150).
    private static readonly TimeSpan HoldDeadlineMargin = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan UnplacedHoldWait = TimeSpan.FromSeconds(1);
    private Func<TimeSpan?>? _timeToNextRound;
    private Action<TimeSpan, Action>? _scheduleHoldDeadline;
    private bool _holdDeadlineArmed;
    private DateTimeOffset _holdDeadlineArmedAt = DateTimeOffset.MinValue;
    // When we last left a room: our move sent, the drag line, or (from the roster read
    // it confirms) the tracker's room confirm.
    private DateTimeOffset _lastRoomLeaveAt = DateTimeOffset.MinValue;
    private int _holdDeadlineGeneration;
    private DateTimeOffset? _holdDeadlineDue;

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
    private bool _arrivalSettleRearmed;   // the one extra window OnArrivalSettleElapsed may take
    private DateTimeOffset _arrivalSettleArmedAt;
    private bool _arrivalSettleBypass;
    private Action<TimeSpan, Action>? _scheduleArrivalSettle;

    // A not-last / follow hold that ended (an announce, or its round-long fallback)
    // while the room's last word was an arrival goes through the settle like any
    // first engage. The release rides the settle so its engage doesn't hold again:
    // the party walks in a beat after us on every step, so without this the hold
    // re-latched on every release and never attacked (report paradigm-20261017-050150).
    // Holds the name of the hold that ended, for the log and the bug report.
    private string? _arrivalSettleCarriedRelease;

    // Set only while EngageReleasedHold re-dispatches the room, naming the hold that
    // ended. The bypass flags can't say this: a resume and the backstab re-announce
    // lift the follow hold too, with no hold having ended.
    private string? _releasingHold;

    // Summon-on-death re-scan hold. The monster just killed summons another as it
    // dies, and that one isn't on the roster until the room is displayed again.
    // While armed, a fresh target pick waits for the re-display (ArmSummonRescan /
    // OnEntitiesObserved); SummonRescanWindow bounds the wait when none arrives.
    // Rides the arrival-settle scheduler, so it stays inert until that is wired.
    private static readonly TimeSpan SummonRescanWindow = TimeSpan.FromMilliseconds(1500);
    private bool _summonRescanArmed;
    private Func<int, bool>? _summonsOnDeath;

    // True while a fresh pick is waiting on the re-display after such a kill.
    internal bool AwaitingSummonRescan => _summonRescanArmed;

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

    // Hit and Run tactics (CombatSettings.HitAndRunTactics), bound to HealthManager:
    // _hitAndRunBackstabLanded reports each landed backstab (true = something is still
    // standing, run now); _hitAndRunInsteadOfFight is asked before any fight would open
    // with a plain attack, and returns true when it started a retreat instead.
    private Action<bool>? _hitAndRunBackstabLanded;
    private Func<string, bool>? _hitAndRunInsteadOfFight;
    // Between a landed backstab and its settle check (one dispatch turn), hold any new
    // engage — the settle decides whether we run, and a plain attack sent first would
    // read as "fought without a backstab" (report paradigm-20260927-000454).
    private bool _hitAndRunSettlePending;


    // HealthManager.IsFleeInFlight — while a flee's move is on its way, don't open a
    // fight in the room we're leaving (a swipe on the way out would draw a `bs` that
    // lands in the next room).
    private Func<bool>? _isFleeInFlight;
    public void SetFleeInFlightProbe(Func<bool> isFleeInFlight)
    {
        ArgumentNullException.ThrowIfNull(isFleeInFlight);
        _isFleeInFlight = isFleeInFlight;
    }

    // HealthManager.KeepRunning — a low-HP / MA run is on and a hostile followed us:
    // run on instead of opening a fight, unless there's nowhere to run (false), when
    // we fight back rather than stand there being hit.
    private Func<bool>? _keepRunning;
    public void SetKeepRunning(Func<bool> keepRunning) => _keepRunning = keepRunning;

    // Our own move is in flight: sent, and the new room hasn't displayed yet
    // (RoomTracker Pending). Anything that "creeps into the room" in that gap arrived
    // in the room we're LEAVING — the server prints the new room only once we're in
    // it — so an arrival-driven first engage then would land its attack in the next
    // room ("Your command had no effect."; report paradigm-20260927-095328, a rat
    // coming down as we went up). The new room's display decides instead; a refused
    // move (NoteMoveRefused) means we're still here, and the held arrival engages.
    //
    // The same goes for a monster that swings at us in that gap: it is in the room
    // we're leaving, and fighting back sent `aslt brute zombie` into the lair being
    // entered, which then read as a fight already under way and opened with the room
    // attack, its debuff never cast (report paradigm-20261010-231540). No fight is
    // opened on that swing, and the attacker is not marked engaged either: the mark is
    // kept by name, so it would pass to a Friend or neutral of that name in the room
    // we land in. The attackers wait in _attackersHeldForMove and are marked only if
    // the move is refused or never lands.
    //
    // The tracker's Pending has no end of its own: a step the game swallowed, or a
    // refusal in words nobody reads, leaves it standing, and a hold with it. So a move
    // counts as in flight for MoveInFlightBound only, measured from when it went out
    // (moveSentAt; where nobody says, from when the first thing was held for it). That
    // bounds every hold here, the arrival's included, and when it runs out with
    // something held the room we're still in is engaged as after a refusal.
    private Func<bool>? _isMoveInFlight;
    private Func<DateTimeOffset?>? _moveSentAt;
    private bool _arrivalHeldForMove;
    private readonly HashSet<string> _attackersHeldForMove = new(StringComparer.OrdinalIgnoreCase);
    // When the hold now standing began; null with nothing held.
    private DateTimeOffset? _moveHoldSince;
    private int _moveHoldGeneration;

    // A landing shows within a second of the step; three allow for a slow link, and
    // leave a swallowed step at most one combat round with nothing sent back.
    public static readonly TimeSpan MoveInFlightBound = TimeSpan.FromSeconds(3);

    public void SetMoveInFlightProbe(Func<bool> isMoveInFlight, Func<DateTimeOffset?>? moveSentAt = null)
    {
        ArgumentNullException.ThrowIfNull(isMoveInFlight);
        _isMoveInFlight = isMoveInFlight;
        _moveSentAt = moveSentAt;
    }

    private bool MoveInFlight() =>
        _isMoveInFlight?.Invoke() == true && MoveInFlightLeft() > TimeSpan.Zero;

    private TimeSpan MoveInFlightLeft()
    {
        DateTimeOffset now = _now();
        return MoveInFlightBound - (now - (_moveSentAt?.Invoke() ?? _moveHoldSince ?? now));
    }

    // Something is held for our move. The first thing held starts the clock that ends
    // the hold if neither a landing nor a refusal does.
    private void HoldForOurMove()
    {
        _arrivalHeldForMove = true;
        if (_moveHoldSince is not null) return;
        _moveHoldSince = _now();
        ScheduleMoveHoldEnd(++_moveHoldGeneration);
    }

    private void ScheduleMoveHoldEnd(int generation)
    {
        TimeSpan left = MoveInFlightLeft();
        _scheduleArrivalSettle?.Invoke(
            left > MoveHoldMinWait ? left : MoveHoldMinWait, () => OnMoveHoldTimeUp(generation));
    }
    private static readonly TimeSpan MoveHoldMinWait = TimeSpan.FromMilliseconds(50);

    // A quiet attacker sends no further line to end its own hold on, so a timer does.
    private void OnMoveHoldTimeUp(int generation)
    {
        if (_disposed || generation != _moveHoldGeneration || _moveHoldSince is null) return;
        // Another step went out since: the clock runs from that one.
        if (MoveInFlightLeft() > TimeSpan.Zero)
        {
            ScheduleMoveHoldEnd(generation);
            return;
        }
        EndMoveHoldInThisRoom(MoveNeverLanded);
    }

    // The same end on the next combat line, for a client with no timer wired and for
    // a line that beats the timer to it.
    private void EndMoveHoldIfTimeIsUp()
    {
        if (_moveHoldSince is null || MoveInFlightLeft() > TimeSpan.Zero) return;
        EndMoveHoldInThisRoom(MoveNeverLanded);
    }

    private static readonly string MoveNeverLanded =
        $"no landing and no refusal within {MoveInFlightBound.TotalSeconds:0} seconds of the step";

    // We landed (a room read anew, or the room-change wipe): whoever swung at us as we
    // left stayed behind or is listed here, and nothing is owed to the old room.
    private void DropMoveHold()
    {
        _attackersHeldForMove.Clear();
        _moveHoldSince = null;
        _moveHoldGeneration++;
    }

    // We never left: the attackers held are marked engaged and the room is engaged.
    private void EndMoveHoldInThisRoom(string why)
    {
        _arrivalHeldForMove = false;
        _userEngagedInstances.UnionWith(_attackersHeldForMove);
        DropMoveHold();
        if (_classifier.Current is not { } cur) return;
        _log?.Combat(LogCategory, $"engage held for our move: {why} — engaging the room we're still in");
        _arrivalSettleBypass = true;
        try { OnEntitiesObserved(cur); }
        finally { _arrivalSettleBypass = false; }
    }

    // For the bug report: whether an engage is held for our move, for how long, and
    // which attackers wait on it.
    public string MoveHoldSummary => _moveHoldSince is not { } since
        ? "no"
        : $"yes, for {(_now() - since).TotalSeconds:0.0}s; {_attackersHeldForMove.Count} attacker(s) waiting"
            + (_attackersHeldForMove.Count == 0 ? string.Empty : $": {string.Join(", ", _attackersHeldForMove)}");

    // With no fight of ours on and our move in flight, a combat line is no reason to
    // open one: the *Combat Off* left standing from the last fight would otherwise
    // re-attack on the first swing, or on a party member's hit, in the room we're
    // leaving. A fight already under way is not held; its re-attack is the round's own.
    private bool HoldsNewFightForOurMove()
    {
        if (_currentTarget is not null || !MoveInFlight()) return false;
        if (!_arrivalHeldForMove)
            _log?.Combat(LogCategory,
                "combat line while our move is in flight and no fight of ours is on — no attack sent from "
                + "the room we're leaving; the room we land in decides");
        HoldForOurMove();
        return true;
    }

    public void NoteMoveRefused()
    {
        // A refused move never left the room (knocked flat, a bonk), so the surprise
        // spent here stays spent. Re-opened, it held every heal for a backstab that
        // could never come and would have opened the fight back up with `bs` (report
        // paradigm-20261013-092344).
        if (_openerSpentBeforeMove) _backstabOpenerConsumed = true;
        _openerSpentBeforeMove = false;
        if (_disposed || (!_arrivalHeldForMove && _moveHoldSince is null)) return;
        EndMoveHoldInThisRoom("the move was refused");
    }

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

    // Resolves an attack-spell cast-code to its game-data caster-message template
    // (Models.GameData.MessageRecord.CasterMessage), wired in AppServices to the
    // same per-spell lookup CombatSession already uses for its stats. OnAttackCastConfirmed
    // needs this because a physical-shaped confirmation ("You <verb> <target> for N
    // damage!") isn't the only cast wording a single-target attack spell can use —
    // many attack spells narrate the hit in third person ("Spiritual power strikes
    // <target> for N damage!", never starting with "You "), and some split the
    // announce and the damage attribution across two lines entirely (an incantation
    // line with no target name, then the third-person damage line). Neither line of
    // such a spell ever satisfies the physical shape, so ConfirmedAttackCastCount
    // never advanced for it — the round-count tally froze, MaxCastsPerRoom was never
    // perceived as reached, and the configured alternate attack spell could never be
    // reached (report paradigm-20260913-040159: soul capped at 1 cast/room, never
    // switched to god's wrath). Null when unwired (tests) or the spell has no usable
    // caster-message record — those callers keep the physical-only behavior.
    //
    // A list, because a spell's own record can hold only its cast emote ("You make a
    // complex gesture!") while the damage wording sits on another record of the same
    // name; a cast then confirms on any of them.
    internal Func<string, IReadOnlyList<CasterMessageMatcher>>? ResolveAttackSpellMatchers { get; set; }

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

    // Diagnostics: `*Combat Engaged*` has answered the attack last sent. While true a
    // fumble line re-sends no attack.
    public bool EngagedSinceLastAttack => EngagedForLastAttack;

    // Diagnostics: whether this room's backstab surprise round is spent, and whether an
    // attack is waiting on the engine send gate to lift (see OnWireReleased).
    public bool BackstabOpenerSpent => _backstabOpenerConsumed;
    public bool AttackHeldBySendGate => _attackHeldByGate;

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
    // (NoteUnattributedDeath drops the dead target, the room re-picks the survivor
    // and swings) is a SendAttack, not a resume, so it never touched the resume
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
    // fresh NoteBetweenRoundCast stamp) still resumes exactly once.
    private DateTimeOffset _lastSpellResumeForBetweenRoundCastAt = DateTimeOffset.MinValue;

    // Weapon-mode twin of the above. A physical swing doesn't itself cause an Off,
    // so the weapon resume can't self-retrigger — but it used to lean on ResumePacing
    // alone, which is a wall-clock (2.5s) gate that also blocks a DISTINCT second
    // between-round cast landing inside the window. Two party heals a beat apart (two
    // members hurt in adjacent rounds) then left the second heal's re-attack paced
    // out for a full round — combat sat Off, a round + the mob's exp lost, until the
    // NEXT cast fell past the window and drove the resume (report
    // paradigm-20260923-210406). Stamp it per interrupt so each distinct between-round
    // cast resumes the weapon exactly once, bypassing ResumePacing (the mob-swing /
    // tick resumes still honour it for spin control).
    private DateTimeOffset _lastWeaponResumeForBetweenRoundCastAt = DateTimeOffset.MinValue;

    // How recent a between-round cast must be for the next *Combat Off* to count
    // as that cast's interrupt. Generous enough to cover send→Off network
    // latency, far shorter than a round so a later pummel Off can't be
    // misattributed.
    private static readonly TimeSpan CastInterruptResumeWindow = TimeSpan.FromSeconds(3);

    // Set when a between-round cast (survival heal / buff) spent its round while we
    // were mid attack-spell combat and our attack spell hasn't been re-announced
    // since. It does NOT gate between-round casting: a 0-energy between-round cast and
    // the combat attack are INDEPENDENT slots that both land the same round (the
    // attack auto-repeats server-side and re-fires on the cast's *Combat Off* —
    // GAME_MECHANICS "Between-round cast slot vs the combat attack"), so a heal never
    // actually costs the round's attack and CastingDirector must not decline one on
    // this. Retained as diagnostic / cascade state only: it's surfaced in the
    // bug-report snapshot and read by the stale-state cleanup guards (a between-round
    // cast leaves _castingSpellTarget live; if the fight ends before the *Combat Off*
    // resume lands, that target must be cleared). Weapon-mode combat never sets this —
    // only an attack SPELL does. Set in NoteBetweenRoundCast (only while
    // _castingSpellTarget is live), cleared when a real attack goes back out
    // (NoteAttackSent) or the room clears (OnRoomCleared).
    private bool _spellAttackOwed;

    // The server is repeating a spell we announced: every round's landing line is
    // ours though we send nothing more.
    public bool IsRepeatingSpell => _announcedSpellCode is not null && !_combatOff;

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

    // When a monster death was last detected this burst (stamped by every kill
    // path: NoteUnattributedDeath, DropTargetForInferredKill and
    // ForceAoeMultiKillReparse). The between-round-cast resume
    // must not fire when a kill just happened: a mob's dying *Combat Off* and a
    // between-round heal in the same round both land inside CastInterruptResumeWindow,
    // so the resume would misread the KILL's Off as its cast's interrupt and re-
    // engage — against a roster the kill's forced re-display hasn't yet cleared.
    // That fires an "aa <corpse>" at the emptied room ("Your command had no
    // effect."). Suppressing the resume for this window hands re-engagement to the
    // death→re-display→re-observe path, which re-picks any survivor from the
    // resynced roster (or clears the gate on an empty room).
    private DateTimeOffset _lastDeathAt = DateTimeOffset.MinValue;

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
    // its *Combat Off* (wire order: death line → exp → Off). Death lines are per-monster
    // flavour the client doesn't read, so without this only MonsterDeathWatcher's exp+Off
    // correlation drops the target, a beat AFTER this handler's resume already
    // re-attacked the corpse ("You don't see X here!", then a full round idle).
    // Stamping the exp here lets OnCombatStatus treat an Off that a fresh exp
    // explains — and that NO between-round survival cast explains — as our kill's
    // Off, dropping the dead target before the resume fires. Consumed on use so a
    // stale exp from an earlier identical-exp kill can't mark a later non-kill Off
    // (a thrown weapon emits one per strike).
    private DateTimeOffset _lastExpGainAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan ExpKillWindow = TimeSpan.FromSeconds(3);

    // Exp lines seen this combat round. ≥2 means an AoE (ours or a hand-cast one) killed
    // several mobs at once — the multi-kill signal the roster-wipe path keys on. Reset at
    // the round boundary (OnCombatTick).
    private int _expGainsThisRound;

    // This round's exp lines that arrived after the room was last read from the game:
    // the kills the roster still lists. An "Also here:" read after a kill comes from
    // the game after it, so that monster is already off the list, and counting its
    // exp against the list a second time emptied a roster that held only the survivor
    // (report paradigm-20261010-145330: 3 kills, a re-read showing 1, "cleared all 1").
    // _rosterReadAt tells a fresh read from a re-issue of the same one.
    private int _expGainsSinceRosterRead;
    private DateTimeOffset _rosterReadAt = DateTimeOffset.MinValue;

    // Whether the *Combat Off* being handled is a command's and not a kill's.
    private readonly CommandOffProbe _offProbe;

    // A kill made by our own room-attack spell, raised on its exp line with the exp
    // gained and the kinds of monster the room listed (one of them is the dead one).
    // Such a kill is counted here because nothing later can count it: on Paradigm a
    // room spell's kill prints no *Combat Off* while anything in the room survives,
    // so the exp + Off pairing MonsterDeathWatcher runs on never completes for it.
    // Wired (AppServices) to MonsterDeathWatcher.NoteRoomSpellKill, which raises the
    // death every other kill raises and spends the exp line, so the *Combat Off* the
    // game does print (Stock after each kill, Paradigm after the last of a room)
    // counts nothing twice.
    //
    // Raised before this class touches its own state for the kill, as the death
    // subscribers need: the summon and drop holds have to be up before a cleared
    // roster releases the walker.
    public event Action<int?, IReadOnlyList<MonsterDeathIdentity>>? RoomSpellKill;

    // When our room spell's own damage line was last read. An exp line is taken for
    // that spell's kill only inside RoomSpellKillWindow of it: the kills of a round
    // arrive in one burst behind the line that made them. That keeps the count to
    // rounds our spell landed in, whatever else may print an exp line (GAME_MECHANICS
    // doesn't record whether a kill elsewhere ever does).
    private DateTimeOffset _roomSpellLandedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan RoomSpellKillWindow = TimeSpan.FromSeconds(2);

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

    // Swap-and-reswing tries per species after the normal weapon's no-effect, while
    // live gear still shows the normal weapon. A couple can be answers to swings sent
    // before the swap landed (report paradigm-20260910-214553); past the cap the swap
    // isn't landing. Room-scoped like the fail-sets.
    private readonly Dictionary<string, int> _normalNoEffectRetries = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxNormalNoEffectRetries = 3;

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
    // dispatch in a room; reset false when we move: every move command sent, typed or
    // engine-driven (NoteMoveSent), plus the pre-move hook (PrepBackstabForMove) and the
    // classifier's RoomChange observation as backups. The classifier only emits
    // RoomChange for an EMPTY new room — a populated one keeps and re-emits the roster
    // it already parsed — so a hand-typed move into an occupied room relied on the move
    // send alone. Gates BackstabPending.
    private bool _backstabOpenerConsumed;

    // Engine send-gate probe and whether an attack was decided while it held (see
    // SetWireHoldProbe / OnWireReleased).
    private Func<bool>? _wireHeld;
    private bool _attackHeldByGate;

    // AttackTiming re-fire coalescing. A combat round resolves the whole party's
    // auto-attacks at once, so several "moves to attack <our-target>" announces
    // arrive as one burst (one server flush → one synchronous line batch). Firing
    // a re-fire per announce would send several redundant attack commands that
    // round; we only need ONE, landing after the last announce so our swing sits
    // last in initiative. ScheduleAttackOrderRefire records the pending
    // target/announcer and flushes once the announces go quiet (RefireSettle), or on
    // the next dispatcher turn when no settle scheduler is wired, collapsing N
    // announces into one send. Either way it still lands well inside the ~5s round;
    // we never hold the attack toward the round tick.
    private bool _refireFlushScheduled;
    private string? _refireTarget;
    private string? _refireAnnouncer;

    // Each server announce line often arrives on its OWN dispatcher turn, and a party
    // member reacting to someone else's announce (an attack-after, another attack-last)
    // lands a few hundred ms behind it. So the re-fire waits until qualifying announces
    // have gone quiet for RefireSettle and goes out once, after the last of them — a
    // next-turn flush fired between them, leaving a later member behind us (the
    // triple-attack of report paradigm-20260922-130230, then a 3-member party where the
    // attack-last member swung before the attack-after one). Every qualifying announce
    // that lands after ours is answered — there's no per-round cap (user, 2026-09-28).
    private static readonly TimeSpan RefireSettle = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RefireAnswerWindow = TimeSpan.FromSeconds(2);
    private int _refireGeneration;
    private Action<TimeSpan, Action>? _scheduleRefireSettle;

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
        _router       = router;
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

        _offProbe = new CommandOffProbe(router);
        _classifier.EntitiesObserved += OnEntitiesObserved;
        _announceSub  = router.Subscribe(KnownPatterns.PartyAttackAnnounce, OnAttackAnnounce);
        _castAnnounceSub = router.Subscribe(KnownPatterns.PartyCastAnnounce, OnCastAnnounce);
        _roomPoisedSub = router.Subscribe(KnownPatterns.PartyRoomPoised, OnRoomPoised);
        _partyCombatSub = router.Subscribe(KnownPatterns.UserHits, OnPartyCombatLine);
        // A follower is dragged with no move of its own sent (FollowMoveObserver).
        _followDragSub = router.Subscribe(KnownPatterns.PartyFollowMove, _ => { if (!_disposed) LeaveRoomFight(); });
        _userHitsSub  = router.Subscribe(KnownPatterns.UserHits,  OnCombatLine);
        _mobHitsSub   = router.Subscribe(KnownPatterns.MobHits,   OnCombatLine);
        _mobMissesSub = router.Subscribe(KnownPatterns.MobMisses, OnCombatLine);
        // Self-defense marking rides the same incoming-attack lines (each carries the
        // attacker's name in group 0). Separate from OnCombatLine because that also
        // fires on our OWN UserHits, which aren't an incoming attack.
        _selfDefenseHitsSub   = router.Subscribe(KnownPatterns.MobHits,    OnAttackedInSelfDefense);
        _selfDefenseMissesSub = router.Subscribe(KnownPatterns.MobMisses,  OnAttackedInSelfDefense);
        _selfDefenseDodgesSub = router.Subscribe(KnownPatterns.UserDodges, OnAttackedInSelfDefense);
        _attackOnUsSubs = new[]
        {
            router.Subscribe(KnownPatterns.MobHits,       OnAttackOnUsUnderUserBreakHold),
            router.Subscribe(KnownPatterns.MobMisses,     OnAttackOnUsUnderUserBreakHold),
            router.Subscribe(KnownPatterns.MobAttacksYou, OnAttackOnUsUnderUserBreakHold),
            router.Subscribe(KnownPatterns.UserDodges,    OnAttackOnUsUnderUserBreakHold),
        };
        router.LineDispatched += OnLineUnderUserBreakHold;
        _targetGoneSub = router.Subscribe(KnownPatterns.TargetNotHere, OnTargetNotHere);
        _weaponNoEffectSub = router.Subscribe(KnownPatterns.WeaponNoEffect, OnWeaponNoEffect);
        _weaponLandedSub   = router.Subscribe(KnownPatterns.UserHits,       OnUserWeaponLanded);
        _fistsNoEffectSub  = router.Subscribe(KnownPatterns.FistsNoEffect,  OnFistsNoEffect);
        _spellNoEffectSub  = router.Subscribe(KnownPatterns.SpellNoEffect,  OnSpellNoEffect);
        _commandNoEffectSub = router.Subscribe(KnownPatterns.CommandNoEffect, OnCommandNoEffect);
        _combatStatusSub   = router.Subscribe(KnownPatterns.CombatStatus,   OnCombatStatus);
        _expGainSub        = router.Subscribe(KnownPatterns.UserGainExperience, OnUserGainExperience);
        _monsterProtectSub = router.Subscribe(KnownPatterns.MonsterMovesToProtect, OnMonsterProtect);
        _roomSpawnSub = router.Subscribe(KnownPatterns.RoomSpawnArrival, OnRoomSpawnArrival);

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

    // True while the engine send gate is holding (EngineSendGate.IsLocked): the
    // wrapped sender drops whatever we send. An attack decided then isn't recorded as
    // sent; it's re-decided when the hold lifts (OnWireReleased).
    public void SetWireHoldProbe(Func<bool> isHeld)
    {
        ArgumentNullException.ThrowIfNull(isHeld);
        _wireHeld = isHeld;
    }

    // Whether a damage line is a room's own damage and not a fight's (the same probe
    // CombatStateTracker and the round clock are given). Unset, every line counts.
    private Func<string, bool>? _isNotCombatLine;

    public void SetNotCombatLineProbe(Func<string, bool> isNotCombatLine)
    {
        ArgumentNullException.ThrowIfNull(isNotCombatLine);
        _isNotCombatLine = isNotCombatLine;
    }

    // The engine send gate's last hold cleared. An attack decided while it was up never
    // reached the wire, so re-decide the room now rather than waiting on the 5 s engage
    // check (report paradigm-20260930-085259: the train-stats screen's hold was still up
    // when the room we were moved to listed a nasty zombie; the swing was dropped, combat
    // believed it sent, and every re-display stopped at "already engaged").
    public void OnWireReleased()
    {
        if (!_attackHeldByGate) return;
        _attackHeldByGate = false;
        if (_disposed || !Fighting()) return;
        _log?.Combat(LogCategory, $"send hold lifted — re-deciding the attack on '{_currentTarget}' it held");
        _currentTarget = null;
        if (_classifier.Current is { } obs) OnEntitiesObserved(obs);
    }

    // An attack decided while the send gate holds: nothing goes out, so it isn't
    // stamped as sent. A held backstab keeps the surprise round for the re-decide.
    private bool HoldAttackIfGated(string verb, string target)
    {
        if (_wireHeld?.Invoke() != true) return false;
        _attackHeldByGate = true;
        if (verb.Equals("bs", StringComparison.OrdinalIgnoreCase)) _backstabOpenerConsumed = false;
        _log?.Combat(LogCategory, $"attack on '{target}' held — the send gate is up; re-decided when it lifts");
        return true;
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

    public void SetFearGate(Func<bool> isFeared)
    {
        ArgumentNullException.ThrowIfNull(isFeared);
        _isFeared = isFeared;
    }

    // True while an AttackPrevented condition is active: a message flagged
    // AttackPrevented latched (stun / petrify / bind / …) and its wear-off hasn't
    // fired. The server refuses every attack command in that state — weapon AND
    // spell — so every attack chokepoint below (SendAttack, the combat-spell and
    // pre-attack-debuff casts, the fumble re-send) consults this and holds,
    // retrying each round until it clears. Edge-logged at Combat level so a report
    // shows exactly when the hold began and lifted. Fear holds the same attacks
    // except the debuff (`debuff`), which is left out of the edge log so a feared
    // debuff check can't flip it every round.
    private bool AttacksBlocked(bool debuff = false)
    {
        // The user's break hold stops every attack and the debuff with it. Asked
        // first and left out of the edge log, which is about conditions: the hold
        // logs its own start and end.
        if (_userBreakHold is not null) return true;
        bool prevented = _attacksPrevented?.Invoke() == true;
        if (debuff) return prevented;
        bool blocked = prevented || _isFeared?.Invoke() == true;
        if (blocked != _attackBlockLogged)
        {
            _attackBlockLogged = blocked;
            _log?.Combat(LogCategory, blocked
                ? "attacks held — AttackPrevented condition or fear active"
                : "attacks resumed — AttackPrevented condition / fear cleared");
        }
        return blocked;
    }

    // Wire the gear actuator (EquipmentManager, the sole gear owner). swapWeapon
    // equips a weapon + off-hand immediately — the unpaced fast path that must
    // land before the next swing; prepBackstabArmor applies the Backstab set's
    // armor synchronously in the pre-move sequence, before the sn (null when no
    // backstab-armor automation is wired — the weapon still gets swapped either
    // way). readWornWeapon reads back what the server has confirmed is on the hand,
    // so weapon-effectiveness evidence can be booked against the weapon that
    // actually swung rather than the one combat has already asked for.
    // Whether we carry or wear an item by name (null while the inventory hasn't been
    // read). An alternate weapon we don't have can't be swapped to.
    private Func<string, bool?>? _hasItem;
    public void SetCarriedCheck(Func<string, bool?> hasItem) => _hasItem = hasItem;

    // The hit magic the character has of its own, from class and race, and whether
    // the realm adds a weapon's magic on top of it (Stock) or takes the higher of
    // the two (Paradigm). Unset reads as none of its own.
    private Func<int>? _innateHitMagic;
    private Func<bool>? _hitMagicAdds;
    public void SetInnateHitMagic(Func<int> innate, Func<bool> addsToWeapon)
    {
        ArgumentNullException.ThrowIfNull(innate);
        ArgumentNullException.ThrowIfNull(addsToWeapon);
        _innateHitMagic = innate;
        _hitMagicAdds = addsToWeapon;
    }

    public void SetWeaponActuator(
        Action<string?, string?, bool> swapWeapon, Action? prepBackstabArmor = null,
        Func<string?>? readWornWeapon = null)
    {
        ArgumentNullException.ThrowIfNull(swapWeapon);
        _swapWeapon = swapWeapon;
        _prepBackstabArmor = prepBackstabArmor;
        _readWornWeapon = readWornWeapon;
    }

    // The monster name we last sent `attack` against, or null when no fight is in
    // flight.
    public string? CurrentTarget => _currentTarget;

    // Who a MonsterDied event is about when it carries no identity: the live target,
    // else the target an exp-inferred kill just dropped (still inside ExpKillWindow).
    // A kill inferred from an exp gain nulls _currentTarget before the death's
    // *Combat Off* fires (so the round's alternate can't corpse-cast), which left
    // every death-event consumer reading CurrentTarget with no name — the boss-timer
    // attribution, the temp-death-spell nudge, and the summon-on-death recheck, which
    // then never re-scanned for the bone snake a stitched zombie's death spell summons
    // (report paradigm-20260925-115042).
    public string? DeathAttributionTarget =>
        _currentTarget
        ?? (_inferredKillPendingRemoval is { } name
            && DateTimeOffset.Now - _inferredKillPendingAt < ExpKillWindow
                ? name
                : null);

    // The monster the round's combat spell was announced against, or null when the
    // round isn't in spell mode. Distinct from CurrentTarget: a weapon attack and a
    // spell attack are separate modes, and only one is live at a time. Anything
    // asking "have we attacked something here?" must consult both.
    public string? CastingSpellTarget => _castingSpellTarget;

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
        // monster long gone, with SpellAttackOwed stuck true, silently blocked
        // every automatic heal/cure/bless). A report showing a CastingSpellTarget
        // that doesn't match CurrentTarget, or SpellAttackOwed=true with no live
        // fight, is this bug re-occurring.
        string? CastingSpellTarget,
        bool SpellAttackOwed,
        // The configured Attack Order (AttackTiming) + the live initiative-hold state.
        // An "attack-last / attack-not-last not respected" report needs both: the mode
        // the user set, and whether we're currently HOLDING our pick for a party
        // announce ("not-last" / "follow") or have already committed (null).
        string AttackTiming,
        string? AwaitingAttackOrderHold,
        // The cast tallies behind each slot's Max casts, and how many of our attack
        // casts have been seen landing this session. A cap that never trips shows as
        // a tally that never moved (report paradigm-20261006-051627).
        string SpellCastTally,
        int ConfirmedAttackCasts);

    // UI-thread only (router handlers + the capture both run there), so no lock.
    public DebugState Snapshot() => new(
        _currentTarget, _usingAlternateWeapon,
        _awaitingBackstabResolution, _pendingBackstabSpecies,
        _guardBlockedTarget, _alternationRound,
        _lastCastAction?.ToString(), _announcedSpellCode,
        _userEngagedInstances.ToArray(),
        _castingSpellTarget, _spellAttackOwed,
        _readSettings().AttackTiming.ToString(),
        _awaitingNotLast ? "not-last" : _awaitingFollowAnnounce ? "follow"
            : _arrivalSettleCarriedRelease is { } ended
                ? $"{ended} hold ended; engaging when the arrival settle elapses"
            : null,
        _spellChooser.DescribeCasts(), ConfirmedAttackCastCount);

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

    // Wire the sneak-broke-on-entry probe (StealthManager.TakeSneakBrokeOnEntry): with
    // RunIfBackstabFails on, a sneak that broke on the way in is a backstab that would
    // fail, so we run instead of opening with a plain attack.
    public void SetSneakBrokeOnEntryProbe(Func<bool> takeSneakBrokeOnEntry)
    {
        ArgumentNullException.ThrowIfNull(takeSneakBrokeOnEntry);
        _takeSneakBrokeOnEntry = takeSneakBrokeOnEntry;
    }

    // Wire the self-defense travel gate: returns true while a PLAIN walk-to (travel
    // to a destination) is driving — as opposed to looping / Auto-Lair (farming) or
    // idle. While travelling, self-defense stands down so an evil character running
    // through a hostile town keeps running past the guards instead of stopping to
    // fight them (bad at low levels). Optional; unset ⇒ never suppressed.
    public void SetSelfDefenseTravelGate(Func<bool> isTravellingWalkTo)
    {
        ArgumentNullException.ThrowIfNull(isTravellingWalkTo);
        _selfDefenseSuppressedByTravel = isTravellingWalkTo;
    }

    // Wire the read of MonsterRelationshipWatcher.NoAnswerComing, asked for the two
    // relationships that are answered on sight and otherwise left out of the fight.
    // It is true for Hangup while no hang-up will come (Disable Hangups on, or the
    // minute after a reconnect), and for Flee while no run will (nothing to run
    // along, no way out, a follower, the flee's switches off). Self-defence then
    // fights one back when it attacks. Unset ⇒ neither is ever fought back.
    public void SetNoAnswerComingProbe(Func<MonsterRelationship, bool> noAnswerComing)
    {
        ArgumentNullException.ThrowIfNull(noAnswerComing);
        _noAnswerComing = noAnswerComing;
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

    // Wire Hit and Run tactics — HealthManager.BackstabLanded / RunInsteadOfFight.
    public void SetHitAndRunHooks(Action<bool> backstabLanded, Func<string, bool> runInsteadOfFight)
    {
        ArgumentNullException.ThrowIfNull(backstabLanded);
        ArgumentNullException.ThrowIfNull(runInsteadOfFight);
        _hitAndRunBackstabLanded = backstabLanded;
        _hitAndRunInsteadOfFight = runInsteadOfFight;
    }

    // A force-clear is making the engine fight this room with Auto-Combat off: a
    // stealth runner's see-hidden or failed-sneak clear, or a rest-blocker clear.
    public bool ForceClearActive =>
        _seeHiddenClearActive?.Invoke() == true || _restClearActive?.Invoke() == true;

    // Whether the engine is fighting: Auto-Combat is on, or a force-clear has it
    // fighting this room regardless. Every handler asks this, not the toggle alone.
    // Asking the toggle alone left a force-clear with only its first attack: the kill
    // wasn't taken off the roster, nothing re-picked the next monster, and the
    // character stood being hit until something re-displayed the room (report
    // paradigm-20261007-143049: two rounds between a harpy's death and the attack on
    // the gorgon beside it).
    //
    // Nor while the user's own `break` holds the attack (_userBreakHold): the engine
    // then stands down as it does with Auto-Combat off, force-clears included.
    private bool Fighting() => _userBreakHold is null && EngineOn();

    // Wire the combat-off stealth-runner clears: seeHiddenClearActive reports whether
    // CombatStateTracker has latched a force-clear for the current room (a stealth
    // runner hit a SeeHidden monster, or its sneak failed into a room inside the
    // Min/Max window, with that toggle on). The tracker owns the decision + latch — it fires first
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
        if (_classifier.Current is not { } obs)
        {
            TrySendRoomRefresh("rest-clear: no room view yet");
            return;
        }
        // HealthManager arms the clear off EITHER a roster hostile OR being kept
        // InCombat by live combat lines. When it's the InCombat side (a chasing mob
        // hitting us that the last room view doesn't list), the current roster has no
        // target to engage — force a re-display so the server hands us the true roster;
        // the resulting observation drives the engage. If the room is genuinely empty,
        // that same re-display clears the stale InCombat that armed us, so this can't spin.
        if (HasEngageable(obs))
            OnEntitiesObserved(obs);
        else
            TrySendRoomRefresh("rest-clear: roster has no hostile, refreshing to find the blocker");
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
        if (!Fighting()) return;
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
        if (!Fighting()) return;
        if (_classifier.Current is { } live) OnEntitiesObserved(live);
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

    // Whether a monster's death spell summons another (MonsterDeathSummonIndex).
    public void SetDeathSummonProbe(Func<int, bool> summonsOnDeath)
    {
        ArgumentNullException.ThrowIfNull(summonsOnDeath);
        _summonsOnDeath = summonsOnDeath;
    }

    // Called with the corpse still on the roster. When the monster summons as it
    // dies, hold the next pick for the room re-display that will show the summon,
    // and ask for that display.
    private void ArmSummonRescan(string deadRawName)
    {
        if (_scheduleArrivalSettle is null || _summonsOnDeath is null) return;
        if (_classifier.Current is not { } obs) return;
        int number = ResolveMonsterNumber(obs, deadRawName);
        if (number < 0 || !_summonsOnDeath(number)) return;

        _summonRescanArmed = true;
        _log?.Combat(LogCategory,
            $"killed {deadRawName}, which summons as it dies — holding the next pick for the room re-display");
        TrySendRoomRefresh("summon-on-death kill");
        _scheduleArrivalSettle(SummonRescanWindow, OnSummonRescanElapsed);
    }

    private void OnSummonRescanElapsed()
    {
        if (_disposed || !_summonRescanArmed) return;
        _summonRescanArmed = false;
        _log?.Combat(LogCategory,
            "summon re-scan: no room re-display arrived — picking from the roster as it stands");
        if (_classifier.Current is { } cur) OnEntitiesObserved(cur);
    }

    // The one-shot delay scheduler for the cascade switch dispatch (see
    // SwitchDispatchDelay). Wired in AppServices to a DispatcherTimer; tests inject a
    // controllable seam.
    public void SetSwitchDispatchScheduler(Action<TimeSpan, Action> schedule)
        => _scheduleSwitchDispatch = schedule;

    // The one-shot delay scheduler for the attack-order re-fire's quiet window
    // (RefireSettle). Wired in AppServices to a DispatcherTimer; left null in tests
    // that keep the next-turn flush.
    public void SetRefireSettleScheduler(Action<TimeSpan, Action> schedule)
        => _scheduleRefireSettle = schedule;

    // Fires a due between-round cast (CastingDirector) when a backstab round settles;
    // true when one went out, so the settle leaves the re-attack to the cast's resume.
    private Func<bool>? _castBeforeReannounce;
    public void SetCastBeforeBackstabReannounce(Func<bool> tryCast) => _castBeforeReannounce = tryCast;

    // The arrival-settle window elapsed with no authoritative room re-display having
    // superseded it. Re-run the engage decision against whatever the room now holds —
    // the whole burst has landed, so a room that met the multi-attack threshold rooms
    // on this first action. No-ops if a room display already engaged us (arm cleared)
    // or the room emptied out from under us.
    private void OnArrivalSettleElapsed()
    {
        if (_disposed || !_arrivalSettleArmed) return;
        _arrivalSettleArmed = false;
        // A release is spent on this window's engage. Every path that doesn't engage
        // drops it: what decides instead (a re-display, the new room) reads the room
        // anew, and the hold's fallback still bounds that wait to a round.
        string? carriedRelease = _arrivalSettleCarriedRelease;
        _arrivalSettleCarriedRelease = null;
        if (_classifier.Current is not { } cur) return;
        // A monster died DURING this settle window (a burst kill landing between arm and
        // now). Its death forced a roster resync (a room re-display) that hasn't arrived
        // yet, so the accumulated roster can still name the just-killed mob — or a fresh
        // same-named arrival that isn't confirmed in-room. Engaging now re-picks that
        // name and corpse-casts at a target the server rejects a beat later with
        // "you don't see X here" (report paradigm-20260916-141344: nebo at a dead
        // muckworm). Defer to the in-flight resync: force the re-display and let the
        // confirmed, pruned roster drive a clean engage instead of racing it.
        if (_lastDeathAt > _arrivalSettleArmedAt)
        {
            NoteArrivalSettleDecision("a kill landed during the window — deferred to the room re-display");
            _log?.Combat(LogCategory,
                "arrival-settle: a kill landed during the window — deferring engage to the "
                + "roster resync so the round's action can't corpse-cast a just-killed mob");
            TrySendRoomRefresh("kill during arrival-settle");
            return;
        }
        // The same kill with no target of ours to drop: nothing stamps a death for it,
        // but its exp line still came. A kill's exp line is always for a monster of our
        // fight or our room (GAME_MECHANICS "Kill detection and monster-kill message
        // order"), so the roster may still hold the corpse. An exp line that wasn't a
        // kill's costs only the re-display's round trip.
        //
        // The settle has disarmed, so only a re-display engages the arrival now. When
        // none could be asked for (a dark room, or one asked for and not read yet) the
        // settle waits one more window for it, then engages the room as it stands.
        if (_lastExpGainAt > _arrivalSettleArmedAt)
        {
            if (TrySendRoomRefresh("exp gain during arrival-settle", askAgainOnceRead: true))
            {
                NoteArrivalSettleDecision("an exp line came during the window — deferred to the room re-display");
                _log?.Combat(LogCategory,
                    "arrival-settle: an exp line came during the window with no target of ours to pin it on — "
                    + "deferring engage to the room re-display so the round's action can't corpse-cast a just-killed mob");
                return;
            }
            if (!_arrivalSettleRearmed && _scheduleArrivalSettle is { } schedule)
            {
                _arrivalSettleRearmed = true;
                _arrivalSettleArmed = true;
                _arrivalSettleArmedAt = DateTimeOffset.Now;
                _arrivalSettleCarriedRelease = carriedRelease;
                NoteArrivalSettleDecision("an exp line came during the window and no re-display could be asked for — waited one more window");
                _log?.Combat(LogCategory,
                    "arrival-settle: an exp line came during the window and no re-display could be asked for — "
                    + $"waiting {ArrivalSettleWindow.TotalMilliseconds:F0}ms more, then engaging the room as it stands");
                schedule(ArrivalSettleWindow, OnArrivalSettleElapsed);
                return;
            }
        }
        if (MoveInFlight())
        {
            HoldForOurMove();
            NoteArrivalSettleDecision("our move was in flight — left to the new room's display");
            _log?.Combat(LogCategory,
                "arrival-settle: our move is in flight — that arrival is in the room we're leaving; "
                + "the new room's display decides");
            return;
        }
        string released = carriedRelease is null ? "" : $" (the {carriedRelease} hold had ended)";
        NoteArrivalSettleDecision("window elapsed with no room re-display — engaged the accumulated room" + released);
        _log?.Combat(LogCategory,
            "arrival-settle: window elapsed with no room re-display — engaging the accumulated room" + released);
        _arrivalSettleBypass = true;
        try
        {
            if (carriedRelease is not null) EngageReleasedHold(cur, carriedRelease);
            else OnEntitiesObserved(cur);
        }
        finally { _arrivalSettleBypass = false; }
    }

    // Wired in AppServices: the time to the projected round (null when no round is
    // placed) and a UI-thread one-shot. Unwired, a hold ends on the round tick's
    // fallback alone.
    public void SetHoldDeadline(Func<TimeSpan?> timeToNextRound, Action<TimeSpan, Action> schedule)
    {
        _timeToNextRound = timeToNextRound;
        _scheduleHoldDeadline = schedule;
    }

    // For the bug report: what the holds read in this room.
    public string AttackOrderRoomState =>
        $"hold deadline {(_holdDeadlineDue is { } due ? $"due {due:HH:mm:ss.fff}" : "not armed")}"
        + $"{(_scheduleHoldDeadline is null ? " (not wired)" : "")}; fight under way here: "
        + (FightRoomUnplaced
            ? "seen before the room was last read, not yet placed in this room (holds wait as at an opening)"
            : _fightUnderwayHere ? "yes" : "no")
        + $"; followed player last seen fighting: {_followedTargetHere ?? "(nobody seen)"}";

    private void ArmHoldDeadline()
    {
        // A deadline armed since we last left a room is this room's and stands: a
        // re-display of the room must not push it back, and with no round placed it is
        // counted from now. One armed before that is another room's: this room's hold
        // gets its own.
        if ((_holdDeadlineArmed && _holdDeadlineArmedAt >= _lastRoomLeaveAt)
            || _scheduleHoldDeadline is not { } schedule) return;
        TimeSpan? toRound = _timeToNextRound?.Invoke();
        TimeSpan wait = toRound is { } r ? r - HoldDeadlineMargin : UnplacedHoldWait;
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        _holdDeadlineArmed = true;
        _holdDeadlineArmedAt = DateTimeOffset.Now;
        _holdDeadlineDue = _holdDeadlineArmedAt + wait;
        int generation = ++_holdDeadlineGeneration;
        _log?.Combat(LogCategory,
            $"attack-order / follow hold: engaging in {wait.TotalMilliseconds:F0}ms unless released first ("
            + (toRound is { } t ? $"round due in {t.TotalMilliseconds:F0}ms" : "no round placed") + ")");
        schedule(wait, () => OnHoldDeadline(generation));
    }

    private void DisarmHoldDeadline()
    {
        _holdDeadlineArmed = false;
        _holdDeadlineDue = null;
        _holdDeadlineArmedAt = DateTimeOffset.MinValue;
        _holdDeadlineGeneration++;
    }

    private void ForgetRoomFight()
    {
        _fightUnderwayHere = false;
        _followedTargetHere = null;
    }

    // We are leaving the room: our move went out, or the game is dragging us behind
    // the party leader (a follower sends no move; the drag line comes before the new
    // room's display). The fight and the hold's deadline we leave are that room's
    // (see _roomStateAt).
    private void LeaveRoomFight()
    {
        _lastRoomLeaveAt = DateTimeOffset.Now;
        ForgetRoomFight();
        DisarmHoldDeadlineKeepingHeldPick();
    }

    // A hold still latched after its deadline goes (a refused move leaves us in the
    // room, or the new room's roster already re-latched it) gets a deadline of its own,
    // so it can't wait out the round.
    private void DisarmHoldDeadlineKeepingHeldPick()
    {
        DisarmHoldDeadline();
        if (_awaitingNotLast || _awaitingFollowAnnounce) ArmHoldDeadline();
    }

    private void OnHoldDeadline(int generation)
    {
        if (_disposed || generation != _holdDeadlineGeneration) return;
        _holdDeadlineArmed = false;
        _holdDeadlineDue = null;
        if (!_awaitingNotLast && !_awaitingFollowAnnounce) return;
        if (!Fighting() || _currentTarget is not null || _classifier.Current is not { } obs) return;
        string hold = _awaitingFollowAnnounce ? "target-priority follow" : "attack-not-last";
        _log?.Combat(LogCategory,
            $"{hold} — nobody released the hold before the round; engaging our own pick so the round isn't lost");
        EngageReleasedHold(obs, hold);
    }

    // Engages the room once a not-last or follow hold has ended. Both holds lift
    // together: when both settings apply they are one wait, ended by whichever
    // releases first, and the engage that ends it must not latch the other. Their
    // round fallbacks used to re-latch each other, so with nobody announcing we
    // never attacked.
    private void EngageReleasedHold(RoomEntitiesObservation obs, string hold)
    {
        _awaitingNotLast = false;
        _awaitingFollowAnnounce = false;
        DisarmHoldDeadline();
        bool notLastBypassWas = _notLastDeferBypass, followBypassWas = _followDeferBypass;
        string? releasingWas = _releasingHold;
        _notLastDeferBypass = true;
        _followDeferBypass = true;
        _releasingHold = hold;
        try { OnEntitiesObserved(obs); }
        finally
        {
            _notLastDeferBypass = notLastBypassWas;
            _followDeferBypass = followBypassWas;
            _releasingHold = releasingWas;
        }
    }

    // What the arrival settle last did when its window elapsed, for the bug report:
    // its log lines are combat diagnostics, off unless the user turned them on.
    private (DateTimeOffset At, string Text)? _lastArrivalSettleDecision;

    public string? LastArrivalSettleDecision => _lastArrivalSettleDecision is { } d
        ? $"{d.At:HH:mm:ss.fff} {d.Text}"
        : null;

    private void NoteArrivalSettleDecision(string text) => _lastArrivalSettleDecision = (DateTimeOffset.Now, text);

    private void OnEntitiesObserved(RoomEntitiesObservation obs)
    {
        // A room display (or the room-change wipe) supersedes an arrival held for our
        // move: we've landed, and what's here is on the new roster.
        if (obs.Source is RoomObservationSource.AlsoHere or RoomObservationSource.RoomChange)
        {
            _arrivalHeldForMove = false;
            // The re-display a summon-on-death kill was waiting for (or a new room).
            _summonRescanArmed = false;
        }

        bool freshRead = obs.Source == RoomObservationSource.AlsoHere && obs.At != _rosterReadAt;
        if (freshRead)
        {
            _rosterReadAt = obs.At;
            _rosterRoomPlaced = false;
            _expGainsSinceRosterRead = 0;
            _heldRoomLookPending = false;
            if (_guardBlockedTarget is { } guarded) JudgeGuardBlockByRoster(obs, guarded);
        }
        // Only a room read anew says we've landed: the old roster is dispatched again
        // by several paths while the move is still in flight.
        bool landed = freshRead || obs.Source == RoomObservationSource.RoomChange;
        if (landed)
        {
            DropMoveHold();
            _openerSpentBeforeMove = false;
        }

        // The roster says whether the monster a break holds the attack on has died
        // or gone. When the hold ends here the rest of this pass engages whatever
        // is left, as after any kill.
        if (_userBreakHold is { } heldMonster)
            JudgeUserBreakHoldByRoster(obs, heldMonster, freshRead);

        CombatSettings settings = _readSettings();

        // Fresh observation of the room — any pending follow / not-last deferral from
        // a previous observation is stale. Re-evaluated below once we know the
        // engageable set (a re-observe of the SAME room re-arms it if still apt).
        // An arrival only adds to the room the holds are on, and the settle it arms
        // would return before re-arming: the party's walk-ins and its first attack
        // come in one flush, so clearing here lost that attack to the settle.
        if (obs.Source != RoomObservationSource.Arrival)
        {
            _awaitingFollowAnnounce = false;
            if (!_notLastDeferBypass) _awaitingNotLast = false;
        }

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
        // settle's own re-entrant call (bypass) so it can proceed to dispatch. A hold
        // release the settle carried is dropped with it: the fresh read holds anew,
        // and the hold's round-long fallback still engages.
        if (!_arrivalSettleBypass && obs.Source != RoomObservationSource.Arrival)
        {
            _arrivalSettleArmed = false;
            _arrivalSettleCarriedRelease = null;
        }

        // A confirmed move into an EMPTY room re-opens the surprise round (the
        // classifier emits RoomChange only then). A move into an occupied room is
        // re-armed by NoteMoveSent, before the new room's Also-here dispatch reads
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
        bool forceClearOverride = ForceClearActive;
        if (_userBreakHold is { } standingDownFor)
            _log?.Combat(LogCategory,
                $"attack held by the user's break on '{standingDownFor}' — not engaging ({obs.Source})");
        if (_userBreakHold is not null || (!_isEnabled() && !forceClearOverride))
        {
            _currentTarget = null;
            // AutoCombat going off mid-fight must drop the attack-spell cascade
            // too, not just the target, so no stale spell target or latch outlives the
            // toggle (report paradigm-20260824-012300).
            ClearAttackSpellCascadeState();
            return;
        }

        // A player outside our party is in the room with our room attack running
        // (this observation may be their arrival): stop it before its next round.
        BreakRoomAttackForBystander();

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
                // Engageability is MonsterEngagement's call alone. The
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

        // A roster we already had, handed back while our move is in flight, is the
        // room we're leaving: opening a fight on it sends the attack into the room we
        // land in. Whatever hands it back (a switch re-running the room, a rest that
        // wants the room cleared, a recovery resuming) waits as a swing does. An
        // arrival has its own hold, behind the settle below; the dispatch that ends a
        // hold (the settle bypass) is the room we stayed in and goes through.
        if (_currentTarget is null && !landed && !_arrivalSettleBypass
            && obs.Source != RoomObservationSource.Arrival && MoveInFlight())
        {
            if (!_arrivalHeldForMove)
                _log?.Combat(LogCategory,
                    $"our move is in flight and this roster is the room we're leaving ({obs.Source}, read before) — "
                    + "no fight opened on it; the room we land in decides");
            HoldForOurMove();
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
            else
            {
                // We engaged this room because its count met the [min..max] window —
                // remember that so "Kill all engaged" can hold us here to finish the
                // survivors even after kills drop the count below the Min floor.
                if (engageable.Count >= min && engageable.Count <= max)
                    _committedToRoom = true;

                if (!MonsterCountGate.WithinWindow(
                        engageable.Count, min, max,
                        settings.KillAllEngaged, _committedToRoom))
                {
                    _log?.Combat(LogCategory,
                        $"min/max gate skip — count={engageable.Count} window=[{min}..{max}]");
                    // Clear target so we don't keep swinging at an old pick
                    // that's now out-of-window after a kill.
                    _currentTarget = null;
                    return;
                }

                if (settings.KillAllEngaged && _committedToRoom
                    && engageable.Count < min && engageable.Count > 0)
                {
                    // "Kill all engaged" is holding us here: we committed to this room
                    // above the Min floor and it's now below it, but we finish the
                    // survivors per normal combat settings instead of moving on
                    // (mixed HP pools left the tanky ones alive after the engage).
                    _log?.Combat(LogCategory,
                        $"kill-all-engaged: finishing {engageable.Count} leftover(s) below min {min} (engaged this room)");
                }
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
            if (_releasingHold is { } endedHold)
            {
                _arrivalSettleCarriedRelease = endedHold;
                _log?.Combat(LogCategory,
                    $"arrival-settle: the {endedHold} hold "
                    + "has ended — engaging when the settle elapses, without holding again");
            }
            if (!_arrivalSettleArmed)
            {
                _arrivalSettleArmed = true;
                _arrivalSettleRearmed = false;
                _arrivalSettleArmedAt = DateTimeOffset.Now;
                _log?.Combat(LogCategory,
                    $"arrival-settle: holding first engage {ArrivalSettleWindow.TotalMilliseconds:F0}ms " +
                    "so a simultaneous burst + room re-display decide together");
                _scheduleArrivalSettle(ArrivalSettleWindow, OnArrivalSettleElapsed);
            }
            return;
        }

        // The monster just killed summons another as it dies, and the room hasn't
        // been displayed since. Picking now commits to whatever else stands here,
        // and the summoned monster (the same fight's next form, often the higher
        // priority) is left to the rest of the party (report
        // paradigm-20261007-185740: a dark priest attacked while the greater hellion
        // the Champion of Blood turned into went untouched). The re-display carries
        // the full roster and decides; it and a room change disarm this above.
        if (_summonRescanArmed && _currentTarget is null) return;

        // Sort by Priority asc (First=0 highest, Last=4 lowest), then
        // by appearance order for stable tiebreak.
        engageable.Sort((a, b) =>
        {
            int p = a.Priority.CompareTo(b.Priority);
            return p != 0 ? p : a.AppearanceIndex.CompareTo(b.AppearanceIndex);
        });

        // Mid-fight room-attack upgrade: a mob ARRIVED and the engageable count now meets
        // the multi-attack's MinEnemies, but we're mid-fight on a single-target spell. The
        // "already engaged" guard just below would return without re-deciding, so the room
        // kept getting single-target pecks until the next damage-driven tick re-chose
        // (report paradigm-20260923-091205: "kept using the single target attack instead
        // of shifting to room attack"). If the chooser, with the fresh count, now picks the
        // room attack, announce the switch here for NEXT round via the same path a cap-switch
        // uses — DeferSwitchDispatch is idempotent and next-round, so there's no same-round
        // double-cast. Weapon-mode arrivals are still caught by the weapon→spell re-climb on
        // the next tick; this covers the spell-mode case the guard was swallowing.
        if (obs.Source == RoomObservationSource.Arrival
            && CombatSpellsWired
            && _castingSpellTarget is { } upgradeTarget
            && _lastCastAction is not (CombatSpellAction.MultiAttack or CombatSpellAction.MultiAttack2)
            && engageable.Any(e => string.Equals(e.RawName, upgradeTarget, StringComparison.OrdinalIgnoreCase)))
        {
            CombatSpellContext upgradeCtx = BuildContext(
                settings, obs, upgradeTarget, CountEngageable(obs), ResolveMonsterNumber(obs, upgradeTarget));
            CombatSpellDecision upgrade = _spellChooser.Choose(settings, upgradeCtx);
            if (upgrade.Action is CombatSpellAction.MultiAttack or CombatSpellAction.MultiAttack2)
            {
                _log?.Combat(LogCategory,
                    $"arrival room-attack upgrade at '{upgradeTarget}': a mob arrived and the chooser "
                    + $"now picks {upgrade.Spell} ({upgrade.Action}) — announcing the switch for next round");
                DeferSwitchDispatch(settings, upgradeTarget, "arrival room-attack upgrade",
                    _announcedSpellCode, upgrade.Spell);
                return;
            }
        }

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

        // Room-attack channel persists across a kill. When a room/multi-attack spell
        // (hsto) is on record for this engagement (_roomChannelSpell) but the round's
        // target was just cleared by a kill (_currentTarget null — the guard above
        // couldn't catch it), the AoE is STILL hitting every survivor and roamer each
        // round, so there is nothing to re-issue (a re-send would cost nothing but add
        // nothing — GAME_MECHANICS "Room-attack spells: cast bare, persistent
        // channel"). If the chooser, peeked against the fresh
        // roster, would STILL pick that same room spell, re-anchor the round to a
        // surviving mob so the per-round heartbeat keeps driving the channel (tally, no
        // send) and return without dispatching. If it now picks something else — the
        // count fell below MinEnemies, MaxCastsPerRoom is spent, or mana dropped under
        // the AoE floor — the channel is over: clear the marker and fall through so the
        // normal chain switches off the room spell. The chooser peek is side-effect
        // free (counters advance in MarkCast, not Choose), so it can't over-tally.
        if (CombatSpellsWired
            && _currentTarget is null
            && _roomChannelSpell is { } channelSpell
            && engageable.Count > 0)
        {
            EngageableCandidate anchor = engageable[0];   // highest-priority survivor
            CombatSpellContext channelCtx = BuildContext(
                settings, obs, anchor.RawName, engageable.Count,
                ResolveMonsterNumber(obs, anchor.RawName));
            CombatSpellDecision still = _spellChooser.Choose(settings, channelCtx);
            if (still.Action is CombatSpellAction.MultiAttack or CombatSpellAction.MultiAttack2
                && string.Equals(still.Spell, channelSpell, StringComparison.OrdinalIgnoreCase))
            {
                _currentTarget = anchor.RawName;
                _castingSpellTarget = anchor.RawName;
                _log?.Combat(LogCategory,
                    $"room-attack channel '{channelSpell}' still live — re-anchoring to "
                    + $"{anchor.RawName} without recast (the AoE already covers the room)");
                return;
            }
            _roomChannelSpell = null;   // conditions lapsed — channel over, let the chain switch off it
        }

        // Target-Priority follow deferral: when we're partied in a multi-mob room
        // and configured to follow the leader's / a member's target, hold our own
        // pick and wait for that player's announce (TryFollowTargetPriority engages
        // their target; OnCombatTick falls back to our own pick if none arrives).
        // Skipped during the fallback's re-entrant call (_followDeferBypass) and
        // once we already hold a target (mid-fight re-observe — don't stall).
        //
        // Attack-not-last deferral: hold our own pick and wait for the first party
        // member's announce, then engage ONCE (TryReleaseNotLastHold; OnCombatTick
        // falls back to our own pick if none arrives that round). Only functional in
        // a party of 3+ — a smaller party behaves like Default and never holds.
        //
        // When both apply they latch together as one wait (EngageReleasedHold ends
        // both): the followed player's attack (announce or damage line) ends it on
        // their target, any other party announce or damage line ends it on our own
        // pick, and the in-round deadline (ArmHoldDeadline) ends it before the round
        // fires, with the round tick's fallback behind that.
        bool holdForFollow = !_followDeferBypass && _currentTarget is null &&
            ShouldWaitForFollow(settings, engageable.Count, obs);
        bool holdNotLast = !_notLastDeferBypass && _currentTarget is null &&
            ShouldWaitForNotLast(settings);
        if ((holdForFollow || holdNotLast) && FightRoomUnplaced)
        {
            _log?.Combat(LogCategory,
                "a fight was seen before this room was read, and nothing has placed it here yet "
                + "(a teleport or drag sends no move of ours) — holding as at an opening until "
                + "the display's exits line says which room this is");
        }
        else if ((holdForFollow || holdNotLast) && _fightUnderwayHere)
        {
            // The holds order only the room's opening (_fightUnderwayHere). Mid-fight a
            // pick goes out now: onto the followed player's target when they've been
            // seen hitting one that is still here, else our own.
            if (holdForFollow && _followedTargetHere is { } followed
                && engageable.Any(e => string.Equals(e.RawName, followed, StringComparison.OrdinalIgnoreCase))
                && FollowedPlayerName(settings) is { } followName)
            {
                _log?.Combat(LogCategory,
                    $"the fight here is under way — following {GivenName(followName)} onto {followed}, seen fighting it, without waiting");
                TryFollowTargetPriority(settings, GivenName(followName), followed);
                return;
            }
            _log?.Combat(LogCategory,
                "the fight here is under way — picking now, no attack-order / follow hold");
            holdForFollow = holdNotLast = false;
        }
        if (holdForFollow)
        {
            _awaitingFollowAnnounce = true;
            _log?.Combat(LogCategory,
                $"target-priority {settings.TargetPriority} — holding own pick, " +
                $"awaiting follow announce (engageable={engageable.Count})");
        }
        if (holdNotLast)
        {
            _awaitingNotLast = true;
            _log?.Combat(LogCategory,
                $"attack-not-last — holding own pick, awaiting first party announce " +
                $"(engageable={engageable.Count})");
        }
        if (holdForFollow || holdNotLast)
        {
            ArmHoldDeadline();
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
        // Mid-flee: don't open anything in the room we're leaving (a swipe on the way
        // out would draw an attack — or a `bs` — that lands in the next room).
        // A resume that stands down for the run leaves the interrupt armed: when the
        // run finds no way out (every move refused while knocked flat), the next swing
        // at us is what brings the attack back. Cleared, nothing re-engaged until the
        // room changed, and the fight was never answered (report paradigm-20261013-092344).
        if (_isFleeInFlight?.Invoke() == true)
        {
            _log?.Combat(LogCategory, "fleeing — not engaging on the way out");
            _currentTarget = null;
            if (_resumeBypassEngagedGuard) _combatOff = true;
            return;
        }
        if (_keepRunning?.Invoke() == true)
        {
            _log?.Combat(LogCategory, "still below the run trigger — running on, not turning to fight");
            _currentTarget = null;
            if (_resumeBypassEngagedGuard) _combatOff = true;
            return;
        }

        // We snuck in for a backstab but made a sound entering: the surprise is gone
        // and the backstab would fail, so run now rather than open with a plain swing
        // (report paradigm-20260926-222210). Hit and Run covers this itself — it
        // backstabs everything, so a room it can't backstab is a room it leaves —
        // through its own retreat so the give-up-after-N-runs budget applies; without
        // it, Run if BS fails decides.
        if (settings.DoBackstab && !_backstabOpenerConsumed && _currentTarget is null
            && _takeSneakBrokeOnEntry?.Invoke() == true)
        {
            _backstabOpenerConsumed = true;
            if (settings.HitAndRunTactics)
            {
                if (_hitAndRunInsteadOfFight?.Invoke("sneak broke entering the room — the backstab would fail") == true)
                {
                    _currentTarget = null;
                    return;
                }
            }
            else if (settings.RunIfBackstabFails)
            {
                _log?.Info(LogCategory, "sneak broke entering the room — backstab would fail; running");
                _backstabFailureFlee?.Invoke();
                return;
            }
            else
                _log?.Info(LogCategory, "sneak broke entering the room — Run if BS fails and Hit and Run are off; fighting");
        }

        bool backstabPending = BackstabPending(settings, obs);
        // A sneaking character opening with a plain attack reads as a missed backstab
        // unless the log names the monster that spotted it (report
        // paradigm-20261004-201936).
        if (!backstabPending && settings.DoBackstab && !_backstabOpenerConsumed && _currentTarget is null
            && _isStealthed?.Invoke() == true && SeeHiddenOccupant(obs) is { } seer)
            _log?.Combat(LogCategory,
                $"no backstab in this room — {seer} sees hidden, so the sneak is spotted; opening with a normal attack");
        EngageableCandidate? choice = null;
        EngageableCandidate? actionableFallback = null;
        foreach (EngageableCandidate cand in ordered)
        {
            EngageAssessment assess = AssessEngage(settings, cand.MonsterNumber, cand.ResolvedName);
            if (assess != EngageAssessment.CanAct)
            {
                _log?.Combat(LogCategory,
                    $"skip un-actionable {cand.RawName} (#{cand.MonsterNumber}) — {assess}" +
                    WeaponGateNote(settings, cand.MonsterNumber, cand.ResolvedName));
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

        // Hit and Run tactics: never open a fight without a backstab. A monster that
        // walks in after our backstab, one that chased us, a room we entered seen —
        // run, re-sneak and come back instead (report paradigm-20260926-233241). Not
        // when a backstab can't work against it however we come back (a see-hidden
        // monster, a don't-backstab target), and HealthManager lets it fight once the
        // run budget is spent or no retreat can start.
        if (settings.HitAndRunTactics && _hitAndRunSettlePending)
        {
            _log?.Combat(LogCategory, $"hit and run — holding {picked.RawName} until the backstab's round settles");
            _currentTarget = null;
            return;
        }
        if (settings.DoBackstab && settings.HitAndRunTactics && _currentTarget is null
            && !backstabPending && !picked.DontBackstab && !RoomHasSeeHidden(obs)
            && _hitAndRunInsteadOfFight?.Invoke($"{picked.RawName} would be fought without a backstab") == true)
        {
            return;
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
        // bypassRecastInterval: a monster arriving is independent of whatever
        // we were just doing, so the newcomer's attack goes out this tick rather
        // than waiting out the burst guard (report paradigm-20260916-033047). A
        // CastingDirector buff can't hold it up any more: buffs are paced on
        // CastCoordinator's separate between-round clock.
        DispatchRoundAction(settings, picked, engageable.Count, obs, bypassRecastInterval: true);
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

    // Parameterless view of BackstabPending against the LIVE settings + room, for
    // callers outside the combat pass (the sneak-maintenance defer gate). While
    // true, a stealth backstab opener is still owed here — a between-round buff /
    // cure would break sneak and cost the surprise round, so maintenance casting
    // holds until the opener fires (_backstabOpenerConsumed) or the room clears.
    // False when there's no room observation yet (nothing to open on).
    public bool IsBackstabOpenerPending() =>
        _classifier.Current is { } obs && BackstabPending(_readSettings(), obs);

    // True from a backstab's send until its round resolves (landed / failed).
    public bool IsBackstabRoundUnresolved => _awaitingBackstabResolution;

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
    private bool RoomHasSeeHidden(RoomEntitiesObservation obs) => SeeHiddenOccupant(obs) is not null;

    // The first monster in the room that carries SeeHidden, by the name the room shows.
    private string? SeeHiddenOccupant(RoomEntitiesObservation obs)
    {
        if (_hasSeeHidden is null) return null;
        for (int i = 0; i < obs.Entities.Count; i++)
        {
            RoomEntity e = obs.Entities[i];
            if (e.Kind != EntityKind.Monster) continue;
            if (e.MonsterNumber is not int n) continue;
            if (_hasSeeHidden(n)) return e.RawName;
        }
        return null;
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
        _normalNoEffectRetries.Clear();
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
    // announced spell, the round-owed diagnostic latch (_spellAttackOwed), the
    // alternation/tally clocks, the observed-immunity set, and the chooser's own
    // cast-cap counters. Shared by OnRoomCleared (a genuine end-of-fight), the
    // AutoCombat-disabled early return (toggling combat off must not leave the
    // round latched to a target that's no longer being fought), and OnPlayerDeath
    // (the corpse/respawn room has nothing to do with whatever spell was
    // mid-flight). Report paradigm-20260824-012300: a stale spellTarget on a
    // monster long gone left the resume logic waiting on a *Combat Off* that would
    // never come, stranding the attack — clearing the cascade here is what frees it.
    private void ClearAttackSpellCascadeState()
    {
        _castingSpellTarget = null;
        _lastCastAction = null;
        _roomChannelSpell = null;   // end-of-fight ends any room-attack channel
        _alternationRound = 0;
        _lastAlternationAdvanceAt = DateTimeOffset.MinValue;
        _lastAttackTallyAt = DateTimeOffset.MinValue;
        _spellAttackOwed = false;
        _spellResumeHeldForUserAttack = false;
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
    // (_spellAttackOwed) never clears and the character never resumes
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
        _userAttackedLast = null;
        EndUserBreakHold("the connection dropped");
        NoteGameLeft();
        if (_currentTarget is not null || _spellAttackOwed)
            _log?.Combat(LogCategory,
                $"disconnect cleared stale combat state — target={_currentTarget ?? "(none)"}, "
                + $"spellTarget={_castingSpellTarget ?? "(none)"}, spellAttackOwed={_spellAttackOwed}");
        _currentTarget = null;
        _deadAttackWordWarned = false;
        _engagedSinceLastAttack = false;
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
    // Every move command sent, typed or engine-driven, re-opens the surprise round for
    // the room we're heading into. It lands before that room's "Also here:" is parsed,
    // which is when the opener is decided (report paradigm-20260930-084955: a typed
    // `se` sneaked into a frost serpent's room and opened with `a`, the opener still
    // spent from the last fight). Flag-only: the stealth gate still decides whether a
    // bs fires, so a move that doesn't go through can't backstab from a broken sneak.
    public void NoteMoveSent()
    {
        LeaveRoomFight();
        if (!_readSettings().DoBackstab) return;
        if (_backstabOpenerConsumed) _openerSpentBeforeMove = true;
        _backstabOpenerConsumed = false;
    }

    // Set while a move that re-opened the surprise round hasn't landed yet: the
    // opener was spent in the room it left.
    private bool _openerSpentBeforeMove;


    public void PrepBackstabForMove()
    {
        CombatSettings settings = _readSettings();
        if (!settings.DoBackstab) return;

        // A new sneak-approach re-opens the surprise round for the room we're
        // about to enter — the next action there is a genuine backstab opener
        // again. Reset before the early-return so it still fires when we backstab
        // with the equipped weapon (no dedicated BackstabWeapon configured).
        if (_backstabOpenerConsumed) _openerSpentBeforeMove = true;
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
        // Leaving the room ends the room-attack channel — the next room re-earns its
        // own first cast (and re-derives the marker).
        _roomChannelSpell = null;
        _spellChooser.ResetForNewRoom();
        // Leaving the room ends our commitment to it — the next room re-earns
        // the "Kill all engaged" hold by meeting the engage window again.
        _committedToRoom = false;
        // Fresh room — the AoE debuff's "first round only" gate re-opens (combat
        // hasn't been seen here yet).
        _combatSeenThisRoom = false;
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
        if (!Fighting()) return;
        if (_currentTarget is null) return;

        // Canonicalize the target to base species — strip any flavor
        // prefix. The classifier's ResolvedName is the canonical form;
        // _currentTarget holds RawName. We resolve by scanning the
        // current observation.
        string species = ResolveSpeciesFromCurrentTarget();
        CombatSettings settings = _readSettings();

        // Book the failure against the weapon that actually swung. _usingAlternateWeapon
        // flips the moment an `eq` is SENT, so when combat swaps and attacks in one
        // batch, a no-effect that is really the server answering the PREVIOUS weapon's
        // swing lands on the new weapon's ledger — and an alternate that hits perfectly
        // well gets written into the fail-set, exhausting the weapon path and making the
        // monster read Unkillable for the rest of the room (report paradigm-20260910-214553:
        // throwing hammers drew the no-effect, the golden broadsword was blamed, and the
        // engine then refused to swing at a mob it was impaling for 24 a hit).
        bool onAlternate = AlternateConfirmedOnHand(settings) ?? _usingAlternateWeapon;
        if (onAlternate != _usingAlternateWeapon)
            _log?.Combat(LogCategory,
                $"weapon-no-effect attributed to the {(onAlternate ? "ALTERNATE" : "normal")} weapon " +
                $"from live gear (engine believed {(_usingAlternateWeapon ? "alternate" : "normal")} — swap still in flight)");

        if (onAlternate)
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
                    $"weapon-no-effect on ALT against {species} — forcing a real re-equip of the alternate + retry");
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

        // Swap NOW and re-send the attack so we don't waste a round — while the
        // alternate can come to hand and the swap hasn't kept failing to land. An
        // alternate we don't carry or wear (or none at all) can't, and re-sending the
        // swing drew "no effect" as fast as the server could answer (report
        // paradigm-20260929-230446: 1,100+ `a centaur outcast`, the alternate not in
        // inventory).
        bool alternateToHand = !string.IsNullOrWhiteSpace(settings.AlternateWeapon)
            && _hasItem?.Invoke(settings.AlternateWeapon) != false;
        int tries = _normalNoEffectRetries.GetValueOrDefault(species);
        if (alternateToHand && tries < MaxNormalNoEffectRetries)
        {
            _normalNoEffectRetries[species] = tries + 1;
            EquipForAttack(settings, wantAlternate: true);
            if (_currentTarget is { } tgt)
                SendAttack(settings.AlternateAttackCommand, tgt, priority: null);
            return;
        }

        // The alternate won't come to hand: count it out against this species too and
        // take the exhausted-weapon path — a spell if one can take the round, else drop
        // the target and re-assess.
        if (_alternateWeaponFailedMonsters.Add(species))
            _log?.Combat(LogCategory, alternateToHand
                ? $"weapon-no-effect vs {species} — the alternate never came to hand after {tries} swaps; counting it out too"
                : $"weapon-no-effect vs {species} — no alternate weapon on hand; counting it out too");
        if (TryFallBackToSpellAfterWeaponFail(settings)) return;
        _log?.Combat(LogCategory, $"weapon-no-effect vs {species} — no weapon of ours hurts it, re-assessing");
        _currentTarget = null;
        TrySendRoomRefresh($"weapon exhausted vs {species} — re-pick / move on");
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
    //
    // When the attack in use is a martial-arts strike the line means something
    // else: the strike itself can't hurt this monster (its magic is past the
    // character's own hit magic). That is the weapon line's case, so it is handled
    // as one: the species is booked as failed and the next pick moves on.
    private void OnFistsNoEffect(MatchResult match)
    {
        CombatSettings settings = _readSettings();
        string? inUse = _usingAlternateWeapon ? settings.AlternateAttackCommand : settings.NormalAttackCommand;
        if (MartialArtsCommand.IsStrike(inUse))
        {
            _log?.Combat(LogCategory, $"`{inUse}` had no effect — the strike can't hurt this monster");
            OnWeaponNoEffect(match);
            return;
        }

        _log?.Warn(LogCategory, "fists-no-effect — forcing a weapon re-pick from live gear");
        _usingAlternateWeapon = false;

        // Force a re-equip on the next attack by triggering a fresh
        // pick. The simplest path: drop _currentTarget so
        // OnEntitiesObserved re-decides + re-equips on the next
        // observation. (The classifier re-fires on every full room
        // display + arrival.)
        _currentTarget = null;
    }

    // Whether live gear confirms the configured ALTERNATE weapon is on the hand:
    // true when the worn weapon is it, false when it's demonstrably something else,
    // null when gear can't answer (reader unwired, no inventory dump yet, empty
    // hand, or no alternate configured) and the caller must fall back to the belief
    // flag. Deliberately keyed on the alternate alone rather than comparing both
    // slots — NormalWeapon is legitimately blank when the normal loadout comes from
    // the Workshop Default set, and "worn isn't the alternate" is the whole answer.
    private bool? AlternateConfirmedOnHand(CombatSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AlternateWeapon)) return null;
        string? worn = _readWornWeapon?.Invoke();
        if (string.IsNullOrWhiteSpace(worn)) return null;
        return string.Equals(worn.Trim(), settings.AlternateWeapon.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    // Our own swing landed damage — direct proof the weapon on the hand hurts this
    // species, so drop it from that weapon's fail-set. Without this the fail-sets
    // are one-way for the whole room: a single mis-attributed or one-off no-effect
    // permanently exhausts the weapon path and the retarget loop skips the monster
    // as Unkillable, no matter how much damage the very next round deals.
    private void OnUserWeaponLanded(MatchResult match)
    {
        if (!Fighting()) return;
        if (match.Groups.Count < 3) return;
        // Only our OWN swing counts. UserHits also fires for party members ("Bob
        // hacks ...") and for reactive gear ("The armour spikes stab ..."), neither
        // of which says anything about what's in our hand.
        if (!string.Equals(match.Groups[0], "You", StringComparison.OrdinalIgnoreCase)) return;

        string species = ResolveSpeciesByName(match.Groups[1]);
        if (string.IsNullOrEmpty(species)) return;

        CombatSettings settings = _readSettings();
        bool onAlternate = AlternateConfirmedOnHand(settings) ?? _usingAlternateWeapon;
        HashSet<string> failSet = onAlternate
            ? _alternateWeaponFailedMonsters
            : _normalWeaponFailedMonsters;
        if (!failSet.Remove(species)) return;

        _cannotAttackAnnounced.Remove(species);
        _log?.Combat(LogCategory,
            $"{species} dropped from the {(onAlternate ? "alternate" : "normal")}-weapon fail-set — " +
            "a swing just landed with it");
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

        if (!Fighting()) return;
        CombatSettings settings = _readSettings();

        // AttackNotLast: if we're holding for the first party announce, this is it —
        // engage our own pick once and stop (no follow, no re-fire).
        if (TryReleaseNotLastHold(announcer, followable: !IsRoomAttackAnnounce(announcedTarget))) return;

        // A room attack's wildcard target ("everyone in the room") is not a mob —
        // never route it through Target Priority (following it would try to engage a
        // nonexistent monster and drop our real target). It's a room-wide commit, so
        // it goes straight to Attack Order as that member's round commit.
        if (IsRoomAttackAnnounce(announcedTarget))
        {
            HandleAttackOrderRefire(settings, announcer, announcedTarget);
            return;
        }

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

        if (!Fighting()) return;
        if (TryReleaseNotLastHold(announcer)) return;
        HandleAttackOrderRefire(_readSettings(), announcer, announcedTarget);
    }

    // Paradigm-only "<player> is poised to assault the room!" — a party member is
    // already mid-room-spell as we enter. It carries no target (a room commit hits
    // everything), so route it as a room commit: attack-last re-fires our own action to
    // land after their already-committed room. Own-name / master-off guards mirror the
    // announce handlers.
    private void OnRoomPoised(MatchResult match)
    {
        if (match.Groups.Count == 0) return;
        string announcer = match.Groups[0];
        if (announcer.Length == 0) return;

        string? ownName = _readOwnGivenName();
        if (ownName is { Length: > 0 } &&
            string.Equals(announcer, ownName, StringComparison.OrdinalIgnoreCase))
            return;

        if (!Fighting()) return;
        if (TryReleaseNotLastHold(announcer)) return;
        HandleAttackOrderRefire(_readSettings(), announcer, RoomWildcardTarget);
    }

    // A damage line between a party member and a monster on our roster: the party is
    // fighting here whether or not we read its announce (we can walk in after it, or
    // the announce can fall in a flush we read as something else). It ends the
    // not-last hold on our own pick, and the followed player hitting a monster ends
    // the follow hold on that monster, as their announce would. Party members only:
    // a stranger's fight isn't the party's turn. Only damage lines count, since they
    // name both sides; how another player's miss reads to onlookers isn't on record.
    private void OnPartyCombatLine(MatchResult match)
    {
        if (_disposed || !_party.IsInParty || _classifier.Current is not { } obs) return;
        // With our move in flight the line is the room we're leaving's.
        if (MoveInFlight()) return;

        string? own = _readOwnGivenName();
        List<string> names = new();
        foreach (RoomEntity e in obs.Entities)
            if (e.Kind == EntityKind.Monster) names.Add(e.RawName);
        if (names.Count == 0) return;
        foreach (PartyMember m in _party.Members)
        {
            string given = GivenName(m.Name);
            if (given.Length > 0 && !string.Equals(given, own, StringComparison.OrdinalIgnoreCase))
                names.Add(given);
        }
        if (!DamageLineAttributor.TryAttribute(match.Text, names, out DamageAttribution hit)) return;

        string? member, monster;
        if (hit.Source is { } src && IsPartyMember(src) && hit.Target is { } tgt && obs.FindMonster(tgt) is not null)
            (member, monster) = (src, tgt);
        else if (hit.Target is { } victim && IsPartyMember(victim) && hit.Source is { } mob && obs.FindMonster(mob) is not null)
            (member, monster) = (victim, null);
        else
            return;

        CombatSettings settings = _readSettings();
        _fightUnderwayHere = true;
        _roomStateAt = DateTimeOffset.Now;
        bool followed = IsFollowedPlayer(settings, member);
        if (followed && monster is not null) _followedTargetHere = monster;

        if (!Fighting() || _currentTarget is not null) return;
        if (followed && monster is not null && (_awaitingFollowAnnounce || _awaitingNotLast))
        {
            _log?.Combat(LogCategory,
                $"{member} is fighting {monster} (no announce read) — taking their target");
            TryFollowTargetPriority(settings, member, monster);
            return;
        }
        if (_awaitingNotLast)
        {
            _awaitingNotLast = false;
            _log?.Combat(LogCategory,
                $"attack-not-last — {member} is already fighting here (no announce read); engaging our own pick once");
            EngageReleasedHold(obs, "attack-not-last");
        }
    }

    // The wildcard "target" a room attack commits against. A room spell announces as
    // "<player> moves to attack everyone in the room." (never "moves to cast … upon …"),
    // and the Paradigm poised line is normalised onto the same token, so attack-last can
    // recognise a room-wide commit with one comparison.
    internal const string RoomWildcardTarget = "everyone in the room";

    private static bool IsRoomAttackAnnounce(string announcedTarget)
        => string.Equals(announcedTarget, RoomWildcardTarget, StringComparison.OrdinalIgnoreCase);

    // AttackNotLast: hold our own room-entry pick until the first party member
    // announces, then commit once. Only functional in a party of 3+ — Members
    // mirrors the par table INCLUDING the local player, so >= 3 is "at least two
    // other people". A party of 2 or fewer behaves like Default (never holds, so
    // OnEntitiesObserved dispatches immediately and no re-fire is scheduled).
    private bool ShouldWaitForNotLast(CombatSettings settings)
        => settings.AttackTiming == AttackTiming.AttackNotLast
        && _party.IsInParty
        && _party.Members.Count >= 3;

    // Release the AttackNotLast hold on the FIRST party member's announce: engage our
    // own pick once (bypassing the hold so it doesn't re-latch). Returns true when it
    // consumed the announce. Only party members trip it (a stranger's swing isn't the
    // party's turn); the hold's own party-size gate means we're always partied here.
    // An attack on a monster by the player Target Priority follows is left to
    // TryFollowTargetPriority (followable), which ends the wait on their target.
    private bool TryReleaseNotLastHold(string announcer, bool followable = false)
    {
        if (!_awaitingNotLast) return false;
        if (!IsPartyMember(announcer)) return false;
        if (followable && IsFollowedPlayer(_readSettings(), announcer))
            return false;

        _awaitingNotLast = false;
        if (_classifier.Current is { } obs)
        {
            _log?.Combat(LogCategory,
                $"attack-not-last — first party announce ({announcer}); engaging our own pick once");
            EngageReleasedHold(obs, "attack-not-last");
        }
        return true;
    }

    // The player Target Priority follows (the leader's or a named member's full
    // name), or null when it follows no one.
    private string? FollowedPlayerName(CombatSettings settings) => settings.TargetPriority switch
    {
        TargetPriority.FollowLeader => _party.LeaderName,
        TargetPriority.FollowMember => settings.TargetPriorityMemberName,
        _                           => null,
    };

    // Announcers carry the given name; the followed name may be a full `par` name.
    private bool IsFollowedPlayer(CombatSettings settings, string announcer)
        => FollowedPlayerName(settings) is { Length: > 0 } followName
        && string.Equals(GivenName(announcer), GivenName(followName), StringComparison.OrdinalIgnoreCase);

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

        if (FollowedPlayerName(settings) is not { Length: > 0 } followName) return false;

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

        if (!IsFollowedPlayer(settings, announcer)) return false;

        // The followed player just announced — our deferral is resolved either way
        // (we engage their target below, or the un-actionable failback re-picks our
        // own), so drop the hold before OnCombatTick's fallback can also fire. A
        // not-last hold latched beside it is the same wait and ends with it.
        string? endedHold = _awaitingFollowAnnounce ? "target-priority follow"
            : _awaitingNotLast ? "attack-not-last" : null;
        _awaitingFollowAnnounce = false;
        _awaitingNotLast = false;
        if (endedHold is not null) DisarmHoldDeadline();

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
                if (endedHold is not null) EngageReleasedHold(liveObs, endedHold);
                else OnEntitiesObserved(liveObs);
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
                // A re-announce before the round fires costs nothing and the last one
                // counts (GAME_MECHANICS "Attack announce lines and round commits"), so
                // a cast just sent at our own pick doesn't hold the move onto theirs.
                DispatchRoundAction(settings, cand, CountEngageable(liveObs), liveObs,
                    bypassRecastInterval: true);
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

        // A room attack ("<player> moves to attack everyone in the room.", or the
        // Paradigm "poised to assault the room!") commits against the WHOLE room — our
        // target included — so it's a valid attack-last commit even though its announced
        // "target" is the room, not our specific mob. For any other announce, reposition
        // only against OUR priority target — ignoring announces on other monsters (this
        // also makes a caster's heal/buff announce, "... upon <player>", a safe no-op on
        // the OnCastAnnounce path).
        if (!IsRoomAttackAnnounce(announcedTarget) &&
            !string.Equals(announcedTarget, target, StringComparison.OrdinalIgnoreCase))
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
            // AttackNotLast commits exactly once via the hold release
            // (TryReleaseNotLastHold) and deliberately never re-fires; Default is own
            // cadence. Both fall through to no re-fire here.
            _                            => false,
        };
        if (!fire) return;

        // The game prints lines in the order it handled the commands, and answers each
        // attack with its own *Combat Engaged* (GAME_MECHANICS "Non-swing actions break
        // combat (casting, equipping)"). An announce read before our last attack was
        // answered was handled ahead of it, so ours already lands after it: a re-fire
        // there was the extra attack every fight sent (report
        // paradigm-20261013-153710). The window lets a refused attack, which never gets
        // its Engaged, stop blocking re-fires.
        if (!_engagedSinceLastAttack && DateTimeOffset.Now - _lastAttackSentAt < RefireAnswerWindow)
        {
            _log?.Combat(LogCategory,
                $"{settings.AttackTiming}: {announcer}'s announce came before our attack was answered — ours already lands after it, no re-fire");
            return;
        }

        // Coalesce the round's burst: record this as the pending re-fire and flush once
        // the announces go quiet (see RefireSettle), so several party announces collapse
        // into a single attack command that lands after the last of them.
        _refireTarget = target;
        _refireAnnouncer = announcer;
        if (_scheduleRefireSettle is { } later)
        {
            int generation = ++_refireGeneration;
            later(RefireSettle, () => { if (generation == _refireGeneration) FlushAttackOrderRefire(); });
            return;
        }
        if (_refireFlushScheduled) return;
        _refireFlushScheduled = true;
        _post(FlushAttackOrderRefire);
    }

    // Trailing-edge flush of a coalesced AttackTiming re-fire (see the _refire*
    // fields). Runs once the round's announce burst has gone quiet, so a
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

        if (!Fighting()) return;
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
        // in flight — is held upstream in ScheduleAttackOrderRefire so no second
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

        // Re-fire through the round chooser so we re-issue the action our configured
        // order actually calls for this round — a spells-first build re-announces its
        // attack spell, a room build re-announces its room spell (bare), a weapon build
        // re-swings — instead of always reverting to the physical weapon command, which
        // used to interrupt a spells-first / rooming rotation with a wasted weapon swing.
        // Re-announcing a combat spell is harmless: mana is spent once per round when it
        // fires, not on the announce, so the re-post just re-orders our action to land
        // after the party's commits (GAME_MECHANICS). DispatchRoundAction against our
        // unchanged _currentTarget never switches the monster (its per-target cap reset
        // is guarded on a target change); bypassRecastInterval lets the re-announce land
        // this round rather than being absorbed by the burst guard. Falls back to the
        // literal weapon command only when the target isn't in our room view.
        if (_classifier.Current is { } refireObs && TryBuildCandidate(refireObs, target) is { } cand)
        {
            DispatchRoundAction(settings, cand, CountEngageable(refireObs), refireObs,
                                bypassRecastInterval: true);
            return;
        }

        SendAttack(settings.NormalAttackCommand, target, refire: true,
                   refireReason: $"{settings.AttackTiming} announcer={announcer}");
    }

    // Build an EngageableCandidate for the monster matching name (RawName or
    // ResolvedName, case-insensitive) in obs, resolving its overlay priority.
    // Returns null when no numbered monster entity matches — the caller falls
    // back to a literal attack command.
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
    // The same debounced re-display, asked for from outside the engine: a monster's
    // mid-fight summon (MonsterSummonWatcher). Only while the combat engine is on,
    // since keeping its roster true is what the re-display is for.
    public bool RequestRoomRefresh(string context)
    {
        if (!Fighting()) return false;
        bool sent = TrySendRoomRefresh(context);
        if (sent) _log?.Combat(LogCategory, $"room re-display ({context})");
        return sent;
    }

    // askAgainOnceRead: the cooldown keeps a burst from asking twice for one
    // re-display, so once that re-display has been read a new question isn't a
    // double. For a caller whose question the last read came too early to answer.
    private bool TrySendRoomRefresh(string context, bool askAgainOnceRead = false)
    {
        if (_wireSender is null) return false;
        if (_isInDarkRoom?.Invoke() == true)
        {
            _log?.Combat(LogCategory,
                $"dark room — CR re-display suppressed ({context})");
            return false;
        }
        DateTimeOffset now = DateTimeOffset.Now;
        if (now - _lastRoomRefreshAt < RoomRefreshCooldown)
        {
            if (!askAgainOnceRead) return false;
            if (_rosterReadAt <= _lastRoomRefreshAt)
            {
                _log?.Combat(LogCategory,
                    $"CR re-display not re-sent ({context}) — the one sent "
                    + $"{(now - _lastRoomRefreshAt).TotalMilliseconds:F0}ms ago hasn't been read yet");
                return false;
            }
            _log?.Combat(LogCategory,
                $"CR re-display sent inside the cooldown ({context}) — the last one has already been read");
        }
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
    private void OnCombatLine(MatchResult match)
    {
        if (!Fighting()) return;
        // A room's own damage is nothing swinging at us: no re-attack to wake, no
        // unseen monster to re-display the room for (CombatStateTracker's probe, the
        // round clock's test).
        if (_isNotCombatLine?.Invoke(match.Text) == true) return;
        EndMoveHoldIfTimeIsUp();

        // Resume-after-interrupt: a combat line arrived while our
        // auto-attack is off (we cast a buff/heal mid-round, got
        // stunned, etc.) and the room still holds an engageable mob.
        // The server stopped swinging for us but the fight is clearly
        // ongoing. The line reaching here while _combatOff is not ours (we're
        // not attacking): a mob's swing, a party member's hit, or a room's
        // own damage, each of which the hit patterns match. Re-pick +
        // re-issue the attack. Gated on _combatOff so a normal in-combat
        // line (server still swinging) never re-fires. TryResumeEngage's
        // guard and the deferred dispatch's target check are what keep
        // this off a monster a party member has just killed.
        if (_combatOff
            && _classifier.Current is { } live
            && HasEngageable(live))
        {
            if (HoldsNewFightForOurMove()) return;
            TryResumeEngage(live);
            return;
        }

        if (_currentTarget is not null) return;
        if (_wireSender is null) return;

        if (_classifier.Current is { } cur && HasEngageable(cur)) return;

        // Whoever swung is on no roster, whether or not the re-display goes out.
        _classifier.NoteUnlistedArrival("a combat line with nobody on the roster to fight");
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

    // Pre-emptive sibling of OnCombatLine, keyed on a directionless spawn line
    // ("A slimeworm crashes through the ground into the room!"). RoomEntryWatcher's
    // arrival parser only handles the "<name> <verb> in from <dir>" form — a spawn's
    // free-form flavor carries no direction and no cleanly-parseable name, so nothing
    // adds it to the roster and the mob stays invisible until it swings. That swing is
    // what OnCombatLine currently recovers on, a full round late (the reported lag: a
    // resting mage sat unengaged while ten slimeworms piled in, only engaging once the
    // first bite tripped the empty-room net). Firing the same bare-CR refresh on the
    // spawn line pulls the authoritative "Also here:" a round earlier, before the hit.
    // Gated identically to OnCombatLine: auto-combat on, no held target, and a roster
    // that shows nothing engageable — an already-tracked room needs no refresh.
    private void OnRoomSpawnArrival(MatchResult _)
    {
        if (!Fighting()) return;
        if (_currentTarget is not null) return;
        if (_wireSender is null) return;
        if (_classifier.Current is { } cur && HasEngageable(cur)) return;

        if (TrySendRoomRefresh("monster spawned into the room"))
        {
            _log?.Combat(LogCategory,
                "room-spawn arrival while room shows no engageable — sending CR for short re-display");
            RoomAppearsEmptyDuringCombat?.Invoke();
        }
    }

    // Increments ConfirmedAttackCastCount once per REAL single-target attack/
    // alternate/drain-spell cast landing (or missing) against the current target —
    // the precise signal ReadRoundCount now runs on (see its declaration comment).
    // Gated on _castingSpellTarget: that field is only set while the round's action
    // is our announced single-target spell (a weapon swing clears it on send), so any
    // qualifying combat-result line reaching here while it's set is that spell's own
    // result, never a swing. The "You " prefix + target-name check (mirroring
    // OnBackstabResolutionLine's filtering — both UserHits and UserMisses also fire
    // for party members' actions and (UserMisses) self-emotes) covers a physical-
    // shaped line; ResolveAttackSpellMatchers covers the third-person shape some
    // attack spells use instead. Consecutive lines inside ConfirmedCastGroupWindow
    // are one cast's own multi-projectile results, not a second cast — only the
    // group's first line increments. The grouping also requires the SAME target: a
    // kill that immediately re-engages a fresh mob within the window must still count
    // that mob's opening cast, not fold it into the corpse's tally.
    private void OnAttackCastConfirmed(MatchResult match)
    {
        if (_castingSpellTarget is not { } target) return;

        string text = match.Text;

        // Our own cast that its target negated or resisted names that target in full,
        // so it counts only when that is the monster fought: the substring test below
        // would take a buff resisted by a member "Batu" for a cast at a "bat".
        if (NegatedCastLine.TryRead(text, out NegatedCastLine.Cast negated) && negated.Caster == NegatedCastLine.Self)
        {
            string at = negated.Target.Trim();
            int space = at.IndexOf(' ');
            string given = space >= 0 ? at[..space] : at;
            if (!string.Equals(at, target, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(given, target, StringComparison.OrdinalIgnoreCase))
                return;
        }

        // Two message shapes confirm our own single-target attack-spell round. The
        // physical shape ("You hit the orc for 10 damage!") also covers a first-person
        // attack spell and is checked first since it needs no game-data lookup. Some
        // attack spells instead narrate the hit in third person and never start with
        // "You " (ResolveAttackSpellMatchers' comment has the full story) — those are
        // confirmed against the announced spell's own caster-message template instead,
        // which pins down the target the same way the physical check's substring
        // search does.
        bool physicalShape = text.StartsWith("You ", StringComparison.Ordinal)
            && text.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0;
        // A room spell's line names the room or nobody ("… scorches your foes for N
        // damage!", "… ravages your foe for N damage!"), never the monster the round
        // is anchored to, so the wording alone confirms it. Only the caster is shown
        // that line; a witness and a victim each get their own (GAME_MECHANICS "Damage
        // lines — who hit whom"). Held to the target name it never counted, so a room
        // spell's cast cap was never reached (report paradigm-20261006-051627).
        bool roomSpell = AnnouncedSpellIsRoomWide();
        // Asked for a room spell whatever the physical shape says: only the spell's
        // own wording may stamp it as landed (below).
        bool ownSpellLine = (roomSpell || !physicalShape)
            && _announcedSpellCode is { } announcedSpell
            && ResolveAttackSpellMatchers?.Invoke(announcedSpell) is { } matchers
            && matchers.Any(m => roomSpell ? m.TryMatch(text, out _) : m.ConfirmsTarget(text, target));
        bool spellShape = !physicalShape && ownSpellLine;
        if (!physicalShape && !spellShape) return;

        DateTimeOffset now = _now();
        // The room spell landed this round: an exp line right behind it is its kill
        // (NoteRoomSpellKill). Not on the physical shape, which with a room spell
        // announced is any line that starts "You " and names the anchor monster, a
        // monster's own hit on us among them ("You are burned by the <monster> for N
        // damage!"), and an exp line after one of those is not known to be the spell's.
        if (roomSpell && ownSpellLine) _roomSpellLandedAt = now;
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

        bool landed = text.IndexOf("surprise", StringComparison.OrdinalIgnoreCase) >= 0;
        BackstabResolved?.Invoke(text, landed);
        if (landed)
        {
            _log?.Combat(LogCategory, $"backstab landed (surprise) vs '{species}'");
            // The landing itself starts the hit-and-run count over — now, not at the
            // settle, so a walk-in engaged in the meantime sees a fresh budget.
            if (_readSettings().HitAndRunTactics) _hitAndRunBackstabLanded?.Invoke(false);
        }
        else
        {
            _log?.Info(LogCategory, $"backstab failed (no surprise) vs '{species}'");
        }

        // Decide what comes next once the round's lines have settled — the rest of that
        // swing can still kill the target (report paradigm-20260927-013856). New engages
        // hold until then.
        //
        // A beat, not a dispatch turn: the kill lines can land on a later turn than the
        // surprise line (report paradigm-20260927-023516 — the settle read "target still
        // up", ran from a filthbug the same round had killed, and sent a `break` ahead of
        // its *Combat Off*). Uses the same delay scheduler as the arrival settle.
        string? bsTarget = _currentTarget;
        _hitAndRunSettlePending = true;
        if (_scheduleArrivalSettle is { } later) later(BackstabSettleDelay, () => SettleBackstab(bsTarget, landed));
        else _post(() => SettleBackstab(bsTarget, landed));
    }

    private static readonly TimeSpan BackstabSettleDelay = TimeSpan.FromMilliseconds(600);

    // A backstab's round has settled. Run if the options say so — Hit and Run after a
    // landed one with anything still standing, Run if BS fails after a failed one — and
    // otherwise, with the target still up, re-announce the round's own action. A
    // backstab is silent and the server just keeps swinging, so without this the
    // configured action (a spell first, or the `a <target>` fallback) never goes out
    // and the party never sees what we're fighting (report paradigm-20260927-023144).
    // The target died when the kill inference dropped it; a corpse the roster hasn't
    // dropped yet doesn't count.
    private void SettleBackstab(string? bsTarget, bool landed)
    {
        _hitAndRunSettlePending = false;
        if (_disposed) return;
        CombatSettings settings = _readSettings();
        bool targetAlive = bsTarget is not null
            && string.Equals(_currentTarget, bsTarget, StringComparison.OrdinalIgnoreCase);
        int others = 0;
        if (_classifier.Current is { } obs)
        {
            others = CountEngageable(obs);
            if (bsTarget is not null && obs.Entities.Any(e => e.Kind == EntityKind.Monster
                    && string.Equals(e.RawName, bsTarget, StringComparison.OrdinalIgnoreCase)))
                others--;
        }
        bool standing = targetAlive || others > 0;

        if (landed)
        {
            if (settings.HitAndRunTactics && _hitAndRunBackstabLanded is not null)
            {
                _log?.Info(LogCategory, standing
                    ? $"hit and run — backstab landed; {(targetAlive ? "target still up" : "")}{(targetAlive && others > 0 ? ", " : "")}{(others > 0 ? $"{others} other hostile(s) here" : "")} — running to re-sneak"
                    : "hit and run — backstab landed and cleared the room");
                _hitAndRunBackstabLanded(standing);
            }
        }
        else if (!standing)
        {
            if (settings.HitAndRunTactics || settings.RunIfBackstabFails)
                _log?.Info(LogCategory, "backstab failed, but the round killed the target and the room is clear — not running");
        }
        else if (settings.HitAndRunTactics)
        {
            // A backstab that swung without surprise is a miss — Hit and Run runs from
            // it the same as from one that landed and left something standing.
            _log?.Info(LogCategory, "hit and run — backstab failed (no surprise) — running to re-sneak");
            _hitAndRunInsteadOfFight?.Invoke("backstab failed (no surprise)");
        }
        else if (settings.RunIfBackstabFails)
        {
            _backstabFailureFlee?.Invoke();
        }

        if (_isFleeInFlight?.Invoke() == true) return;   // we're leaving
        // The attack-order timings re-fire on other players' announces to stay last;
        // a settle re-announce of our own would break that ordering.
        if (settings.AttackTiming != AttackTiming.Default) return;
        // A buff / heal the sneak held until the stab goes first: its *Combat Off*
        // resume re-attacks, so the target still sees our attack this round (report
        // paradigm-20260928-165954: shad waited out a whole extra round behind `a`).
        if (targetAlive && _castBeforeReannounce?.Invoke() == true)
        {
            _log?.Combat(LogCategory, $"backstab round over ({(landed ? "landed" : "failed")}) — a held between-round cast goes first; its resume re-attacks {bsTarget}");
            return;
        }
        if (targetAlive && _classifier.Current is { } live)
        {
            _log?.Combat(LogCategory, $"backstab round over ({(landed ? "landed" : "failed")}) — re-announcing the round's attack on {bsTarget}");
            _resumeBypassEngagedGuard = true;
            _followDeferBypass = true;
            try { OnEntitiesObserved(live); }
            finally
            {
                _resumeBypassEngagedGuard = false;
                _followDeferBypass = false;
            }
        }
    }

    // Disarm the surprise-round watch. Called on resolution and on every signal
    // that the fight has moved past the opener (room change, target death, room
    // clear, target-gone) so a resolution line we never saw can't leave the watch
    // (and its re-fire suppression) latched.
    // Reset States: an unresolved backstab latch holds sneak-keeping (SneakGuard).
    public void ClearBackstabLatch() => ClearBackstabResolution();

    private void ClearBackstabResolution()
    {
        _awaitingBackstabResolution = false;
        _pendingBackstabSpecies = null;
    }

    // "You don't see <X> here!" — server can't find the target we just attacked.
    // Different from MonsterDeathWatcher's path: catches cases where the kill
    // went unseen, the mob fled, or a partymate killed it between our send and
    // the server's resolve. Drop the current target and refresh the room so the
    // next observation picks a fresh target.
    private void OnTargetNotHere(MatchResult m)
    {
        if (m.Groups.Count > 0 && !_classifier.RefusalNamesMonster(m.Groups[0], _currentTarget)) return;
        DropMissingTarget("target-not-here");
    }

    // Our attack came back as speech: `a kobold thief` answered with `You say "a kobold
    // thief"`. With no monster of that name in the room and talk-slow off, the server
    // reads the attack as a say (with talk-slow on it's "Your command had no effect.",
    // handled by OnCommandNoEffect) — report stock-20260924-013525. The same meaning as "You don't see X
    // here!", and the usual cause is a monster a PARTY MEMBER killed — no exp for us,
    // so no death was seen and it stayed on the roster, re-picked and re-"said" after
    // every kill. Matched against the exact line we just sent, so ordinary talk (ours
    // or a macro's) can't trip it.
    private static readonly TimeSpan AttackEchoWindow = TimeSpan.FromSeconds(5);

    public void NoteOwnSay(string message)
    {
        if (_lastAttackCommand is not { Length: > 0 } attack) return;
        if (!string.Equals(message.Trim(), attack, StringComparison.OrdinalIgnoreCase)) return;
        if (DateTimeOffset.Now - _lastAttackSentAt > AttackEchoWindow) return;
        DropMissingTarget("attack-read-as-say");
    }

    private void DropMissingTarget(string reason)
    {
        if (!Fighting()) return;
        if (_wireSender is null) return;
        if (_currentTarget is null) return;

        string gone = _currentTarget;
        _log?.Combat(LogCategory,
            $"{reason} — dropping target={gone} + refreshing room");
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
        TrySendRoomRefresh(reason);
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
        if (!Fighting()) return;
        if (_wireSender is null) return;
        if (_currentTarget is null) return;

        // The line names no command, so read the echo ahead of it: a between-round
        // cast the character can't use (a heal slot holding another class's spell)
        // draws the same line every round, and blaming it on the attack dropped a
        // live target, cleared the fight and let a rest through under the monster's
        // swings (report stock-20261004-150645).
        if (_router.ReplyIsForCommandNotNaming(_currentTarget))
        {
            _log?.Combat(LogCategory,
                $"command-no-effect answers '{_router.CommandEchoedBeforeLine}', not the attack — keeping target={_currentTarget}");
            return;
        }

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

    // A kill happened, and deaths carry no monster identity (the exp + *Combat Off*
    // signal names no one), so there's no roster slot handed to us to drop.
    //
    // We WERE swinging at something and a kill just landed, so _currentTarget is
    // our best identity for the corpse: the *Combat Off* means OUR fight ended.
    // Drop it from the live roster so the synthetic EntitiesObserved re-picks the
    // next survivor immediately, or clears the room and releases the Combat gate
    // when it was the last mob. Without this the dead mob lingers, the next tick
    // re-swings at the corpse ("Your command had no effect."), and recovery waits
    // out the idle-stall watchdog — the reported "tries to re-attack the monster
    // that clearly died" and "sits idle after a kill until the timeout" stalls.
    // Only when the target name doesn't match any roster slot do we fall back to
    // the debounced CR re-display, letting the server hand back the true roster.
    public void NoteUnattributedDeath()
    {
        // A kill of ours with the attack held. When its exp line came before any
        // *Combat Off* did, in the round the break was typed in, the round beat the
        // break to the game and still landed (_userBreakHoldKillRaced). A
        // single-target attack can only have killed the monster it was on, the held
        // one, whatever else of that name stands here: the hold ends and the death is
        // handled as that monster's. A room attack may have killed any of them, and
        // so may whatever paid the exp later on (a damage shield), so for those the
        // room is read again and its roster decides: one fewer of the held monster's
        // name ends the hold (JudgeUserBreakHoldByRoster).
        if (_userBreakHold is { } held)
        {
            if (_userBreakHoldRoomAttack || !_userBreakHoldKillRaced)
            {
                if (LookAtRoomUnderHold("a kill with the attack held"))
                    _log?.Combat(LogCategory,
                        $"a kill landed with the attack held on '{held}' and may be another monster's — "
                        + "re-displaying the room to see what is left");
                return;
            }
            EndUserBreakHold("it died");
            if (!Fighting()) return;
            _currentTarget = held;
        }
        if (!Fighting()) return;
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

        // A kill in a guard fight: ask for the room, whose re-display says whether the
        // guarded priority still stands (JudgeGuardBlockByRoster). The removal below
        // can't: it drops our target's slot, which through the redirect is usually the
        // priority's, whichever of them died. It's asked for even inside the cooldown
        // once the last re-display has been read: that one came before this kill.
        //
        // The guard retry waits for that read (TryGuardRetry), but only while one is
        // coming: this CR, or one sent and not read yet. With none (a dark room) the
        // retry goes out at once, as a stall with the priority alive and unattacked
        // costs more than a retry at a corpse, which its "You don't see" reply clears.
        // The wait ends by itself after GuardKillJudgeWait: the retry runs on a
        // *Combat Off*, and none may come.
        if (_guardBlockedTarget is { } guarded)
        {
            bool sent = TrySendRoomRefresh("kill in a guard fight", askAgainOnceRead: true);
            bool reDisplayComing = sent
                || (_isInDarkRoom?.Invoke() != true && _lastRoomRefreshAt > _rosterReadAt);
            if (reDisplayComing)
            {
                DateTimeOffset killAt = DateTimeOffset.Now;
                _guardFightKillAt = killAt;
                _scheduleArrivalSettle?.Invoke(GuardKillJudgeWait, () => OnGuardKillWaitElapsed(killAt));
                _log?.Combat(LogCategory, sent
                    ? $"kill while '{guarded}' is guard-blocked — re-displaying the room to see whether it still stands"
                    : $"kill while '{guarded}' is guard-blocked — the re-display already asked for will say whether it still stands");
            }
            else
                _log?.Combat(LogCategory,
                    $"kill while '{guarded}' is guard-blocked — no re-display can come; the guard retry isn't held");
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

        // Clear the target BEFORE the removal re-fires EntitiesObserved so the
        // re-pick sees a clean slate.
        _currentTarget = null;
        _castingSpellTarget = null;   // end spell mode on the kill too — no corpse re-cast
        ClearBackstabResolution();
        ArmSummonRescan(presumedDead);
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

        // The interrupt's *Combat Off* ended any room-attack channel: a between-round
        // cast (a mid-fight buff like `flux`) breaks the running room spell, which is
        // why we're resuming at all. A room spell otherwise outlives its kills with no
        // Off, so the post-kill "channel still live — re-anchor without recast" rule
        // must not survive this — it re-anchored onto a mob the dead channel was no
        // longer hitting and never re-attacked (report paradigm-20260924-123009).
        if (_roomChannelSpell is { } broken)
        {
            _log?.Combat(LogCategory, $"room-attack channel '{broken}' ended by the interrupt — re-dispatching");
            _roomChannelSpell = null;
        }

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
    private bool TryResumeEngage(RoomEntitiesObservation live, bool bypassAttackGuard = false,
        bool bypassPacing = false)
    {
        // The user hand-typed this round's attack (a combat spell or a swing) — don't
        // re-send our auto attack over the top of it. The override clears at the next
        // combat tick, so the engine resumes on the following round (report
        // paradigm-20260814-135715). EXCEPTION: our OWN between-round cast re-engaging the
        // weapon (bypassAttackGuard) — that Off is provably our buff/heal interrupting the
        // swing, so re-engaging is correct regardless of the override; and during a
        // buff-recast sequence combat stays Off so NO tick fires to clear a stale override,
        // and honoring it here idles a full round (report paradigm-20260922-073350).
        if (_userAttackOverride && !bypassAttackGuard)
        {
            _log?.Combat(LogCategory, "resume suppressed — user attack override holds this round");
            NoteResumeDecision("held — the user's own attack has this round", log: false);
            return false;
        }
        DateTimeOffset now = DateTimeOffset.Now;
        // A fresh swing already went out a beat ago — this resume is redundant
        // and would double on top of it. Happens on a kill: the death→re-observe
        // path re-engages the surviving mob, then the kill's *Combat Off* re-arms
        // _combatOff and the next mob swing line drops in here (the reported solo
        // double-send). Skip while a real attack is still this recent — unless a
        // between-round cast is what produced this Off (see bypassAttackGuard).
        //
        // That Off is one the fresh swing hasn't been answered past. An Off that
        // follows the swing's own *Combat Engaged* ended it, so there is nothing to
        // double: a typed `eq` half a second after the attack broke the fight, the
        // round's lines and its tick all came inside the guard, the heal that followed
        // drew no Off of its own (combat was already off) and the monster went
        // unattacked until it died a round later (report paradigm-20261009-122342).
        // Not right after a kill, though: the Off that ends an answered attack is then
        // most likely the kill's own, and if the corpse is still on the roster (its
        // removal can fail) the guard is what keeps a swing off it.
        bool answeredThenStopped = _offEndedAnsweredAttack && now - _lastDeathAt >= DeathInterruptWindow;
        if (!bypassAttackGuard && !answeredThenStopped && now - _lastAttackSentAt < ResumeAfterAttackGuard)
        {
            NoteResumeDecision(
                $"skipped — our attack went out {(now - _lastAttackSentAt).TotalMilliseconds:F0}ms ago and "
                + (_offEndedAnsweredAttack ? "a kill came right behind it" : "the game hasn't answered it"),
                log: _resumeSkipLoggedForAttackAt != _lastAttackSentAt);
            _resumeSkipLoggedForAttackAt = _lastAttackSentAt;
            return false;
        }
        // A distinct between-round-cast resume (bypassPacing) is guarded once-per-cast
        // by its own stamp, so it must not be paced out by a prior resume. Still stamp
        // _lastInterruptResumeAt below so the paced mob-swing / tick resumes stay
        // deduped against it and can't double this one in the same round.
        if (!bypassPacing && now - _lastInterruptResumeAt < ResumePacing)
        {
            NoteResumeDecision(
                $"not again — re-attacked {(now - _lastInterruptResumeAt).TotalMilliseconds:F0}ms ago (one resume a round)",
                log: false);
            return false;
        }
        _lastInterruptResumeAt = now;
        // Reaching here inside the guard's window without a bypass means the answered
        // attack is what let it through: worth a line, the rest ResumeEngage logs.
        bool pastGuardOnAnswer = !bypassAttackGuard && now - _lastAttackSentAt < ResumeAfterAttackGuard;
        NoteResumeDecision(
            pastGuardOnAnswer
                ? $"re-attacking — the attack sent {(now - _lastAttackSentAt).TotalMilliseconds:F0}ms ago was engaged, then stopped"
                : "re-attacking",
            log: pastGuardOnAnswer);
        DeferResumeEngage(live);
        return true;
    }

    // What TryResumeEngage last decided and when, for the bug report: a resume that
    // was skipped used to leave no trace at all. The guard's skip is logged once per
    // attack it was skipped for, since every line of a round's burst asks again; the
    // pacing skip is the same burst asking after a resume ResumeEngage already logged.
    private (DateTimeOffset At, string Text)? _lastResumeDecision;
    private DateTimeOffset _resumeSkipLoggedForAttackAt = DateTimeOffset.MinValue;

    public string? LastResumeDecision => _lastResumeDecision is { } d
        ? $"{d.At:HH:mm:ss.fff} {d.Text}"
        : null;

    private void NoteResumeDecision(string text, bool log)
    {
        _lastResumeDecision = (DateTimeOffset.Now, text);
        if (log) _log?.Combat(LogCategory, $"attack resume with combat off: {text}");
    }

    // Both TryResumeEngage callers (OnCombatLine's mob-swing resume, OnCombatTick's
    // deterministic weapon resume) are driven by a DAMAGE line — which always lands
    // BEFORE a round's own "dies." / "You gain N experience." when THIS round's
    // attack is the killing blow. Firing ResumeEngage synchronously here re-swings
    // at a target the SAME burst (or an adjacent packet) is about to reveal as dead
    // — the death watcher hasn't nulled _currentTarget yet. Defer the actual
    // re-engage by SwitchDispatchDelay (the window CombatManager.Spells'
    // DeferSwitchDispatch already proved is enough for the kill's exp/Off to land)
    // and re-validate the target before dispatching; a bare _post can't bridge a
    // kill landing in a LATER network packet, only wall-clock can (see
    // DeferSwitchDispatch). Report paradigm-20260905-205200: every kill wasted a
    // full round on "You don't see X here!" / "Your command had no effect." before
    // the engine noticed and moved on — 1s per mob, adding up over a session.
    private void DeferResumeEngage(RoomEntitiesObservation live)
    {
        string? target = _currentTarget;
        // Snapshot the last-attack clock at SCHEDULE time. If a real attack fires during
        // the deferral window, the round's action has already gone out — a resume now
        // would double it. This catches the deferred-resume double-fire that no
        // scheduling-time guard can (the guard passed before the attack landed): the
        // pre-attack-then-attack sends hsto mid-deferral, then this resume re-picks and
        // re-sends hsto (report paradigm-20260916-105458). A between-round-cast bypass
        // resume is unaffected — its cast doesn't advance _lastAttackSentAt, so the
        // snapshot is unchanged and the legitimate resume still fires.
        DateTimeOffset attackAtSchedule = _lastAttackSentAt;

        void Dispatch()
        {
            if (_disposed || !Fighting()) return;
            if (_lastAttackSentAt != attackAtSchedule)
            {
                _log?.Combat(LogCategory,
                    "resume skipped — an attack fired during the deferral (the round's action already went out)");
                return;
            }
            if (HoldsNewFightForOurMove()) return;
            if (target is not null
                && (!string.Equals(_currentTarget, target, StringComparison.OrdinalIgnoreCase)
                    || _classifier.Current is not { } fresh
                    || !TargetPresent(fresh, target)))
            {
                _log?.Combat(LogCategory,
                    $"resume skipped — target '{target}' gone before the deferred dispatch "
                    + "(kill/leave landed first); no corpse-attack");
                return;
            }
            ResumeEngage(_classifier.Current ?? live);
        }

        if (_scheduleSwitchDispatch is { } schedule)
            schedule(SwitchDispatchDelay, Dispatch);
        else
            _post(Dispatch);
    }

    // Signal from Spells.CastingDirector.CastFired that a between-round cast
    // (self-heal / cure / buff / debuff) just went to the server. Arms
    // OnCombatStatus to attribute the imminent *Combat Off* to that cast and
    // resume the weapon attack promptly (see _betweenRoundCastAt). Also arms
    // _spellAttackOwed (diagnostic only) while an attack spell was actively cycling.
    // Engine-armed between-round cast (CastingDirector survival heals/buffs, and
    // our own internal casts). Never rate-limited — the engine casts at most once
    // per round, so each is a legitimate resume.
    public void NoteBetweenRoundCast() => NoteBetweenRoundCast(manual: false);

    // Manual (hand-typed) between-round cast, routed here by OutboundCastObserver.
    // Flagged so the spell-mode resume can rate-limit a mashed burst (see
    // ManualResumePacing) without ever throttling the engine's per-round resumes.
    public void NoteManualBetweenRoundCast() => NoteBetweenRoundCast(manual: true);

    // A mid-fight gear swap (swap-to-Default-on-combat, or any set change during a
    // live fight) streams wear/eq commands, and on Paradigm each wear/eq drops
    // *Combat Off* — breaking our sustained attack exactly like a between-round
    // survival cast does. AppServices pokes this at the swap-complete edge so the
    // wear's imminent *Combat Off* is attributed to our own action and the existing
    // interrupt-resume window re-engages promptly, instead of idling until the mob's
    // next swing wakes OnCombatLine (reports paradigm-20260908-051035 / -095552:
    // swapped to Default mid-fight, then sat un-attacking ~4s). No-op unless
    // auto-combat is on and a fight is actually live, so an out-of-combat pre-rest
    // swap can't arm a phantom resume.
    public void NoteGearSwapInterrupt()
    {
        if (_disposed) return;
        if (!Fighting()) return;
        if (_classifier.Current is not { } live || !HasEngageable(live)) return;
        _log?.Combat(LogCategory, "gear swap during a live fight — arming interrupt resume for the wear's *Combat Off*");
        NoteBetweenRoundCast();
    }

    // An `eq` / `wear` / `wield` / `rem` of the user's own (routed by
    // OutboundGearObserver: typed, or sent by a macro, trigger or event of theirs)
    // stops a fight the same way, on both realms, and the fight is to be picked up
    // again at once while Auto-Combat is on (user, 2026-10-09; GAME_MECHANICS
    // "Non-swing actions break combat (casting, equipping)"). Arms the same latch a
    // cast does, so the *Combat Off* the command draws re-attacks on the spot, and it
    // lapses with the cast window when the command stops nothing (an item we don't
    // have).
    //
    // Two differences from a cast of ours. It is marked manual, so in spell mode a
    // run of typed commands is rate-limited like a run of typed casts. And in weapon
    // mode its resume keeps ResumePacing (see _typedGearStamp): the engine casts once
    // a round, a user can type three gear commands in a second.
    public void NoteTypedGearCommand(string command)
    {
        if (_disposed || !Fighting()) return;
        if (_classifier.Current is not { } live || !HasEngageable(live)) return;
        _log?.Combat(LogCategory, $"the user's '{command}' during a live fight — arming a re-attack for its *Combat Off*");
        NoteBetweenRoundCast(manual: true);
        _typedGearStamp = _betweenRoundCastAt;
    }

    // The _betweenRoundCastAt stamp a typed gear command armed, MinValue when the
    // latch is a cast's.
    private DateTimeOffset _typedGearStamp = DateTimeOffset.MinValue;

    // The game refused the cast the latch was armed for (`You have already cast a
    // spell this round!`). A refused cast prints no *Combat Off* (GAME_MECHANICS "One
    // between-round spell per combat round"), so nothing is coming for the latch to
    // answer, and left armed for its three seconds it took the Off of our own re-sent
    // attack for the cast's (report paradigm-20261009-120757, 12:07:17). A typed gear
    // command's latch is left alone: the refusal isn't about it.
    private void DisarmCastResumeOnRefusal()
    {
        if (_betweenRoundCastAt == _typedGearStamp) return;
        if (DateTimeOffset.Now - _betweenRoundCastAt >= CastInterruptResumeWindow) return;
        _betweenRoundCastAt = DateTimeOffset.MinValue;
        _log?.Combat(LogCategory, "between-round cast refused — resume disarmed (a refused cast draws no *Combat Off*)");
    }

    // ----- The user's break hold -------------------------------------------
    // A `break` the user sent themselves holds the engine's attack on the monster it
    // was fighting (user, 2026-10-10: "a manual break should hold attacking that
    // target until the user types something to attack it, but if that target dies,
    // it should clear and engage the next target if there is one"). Before, the
    // target was kept and the next combat line or round tick re-attacked it, so a
    // typed `break` lasted a second or two.
    //
    // While the hold stands the engine sends no attack of any kind and picks no
    // other monster in the room: Fighting() reads false, exactly as with Auto-Combat
    // off, and AttacksBlocked() is the same answer at every send. Heals and buffs
    // between rounds aren't the engine's attacks and carry on. A monster hitting us
    // changes nothing: the user chose to stand. A walk or loop waits in the room on
    // the Combat gate as in any fight; a flee is the health engine's own move, which
    // that gate doesn't hold, and still runs the character out.
    //
    // It ends when the user attacks (any attack word or attack spell, at any
    // monster), when the monster dies or leaves, when we leave the room, on a
    // disconnect, a profile load or Reset States. The Auto-Combat toggle and the
    // master switch don't end it: with the engine off there is nothing to hold
    // back, and it is still in force when the engine comes back on.
    //
    // The monster is known by name only, and no line says a monster we aren't
    // attacking has died: a bystander gets no experience for the kill and the client
    // reads no death line. So its death is taken from three things (user,
    // 2026-10-10):
    //   - the room listing fewer monsters of that name than it did ("the break
    //     hold should end on the death of any monster matching the name");
    //   - an exp line of our own (NoteUnattributedDeath);
    //   - a whole round in which it attacked nobody ("hostile monsters attack
    //     every round, so if we go 1 round and we dont see that monster attacking
    //     anyone that we do know is in the room, assume its dead"). That silence
    //     is how a death the client can't see is known. Where it can see, the
    //     room's own listing is the better evidence: the silence makes the client
    //     look at the room, and the look decides by the count above. A monster
    //     that is only quiet (a neutral that never fought back, one stunned or
    //     held, one whose attack printed no line the client reads) is still
    //     listed, and the hold stands. In the dark a look lists nobody, so there
    //     the silence alone decides.
    private string? _userBreakHold;
    private DateTimeOffset _userBreakHoldSince;
    // How many monsters of that name the room listed when last seen. One fewer is
    // the death, or the leaving, of one of them.
    private int _userBreakHoldCount;
    // Whose relayed `break` set it ("<name>'s @do", "<name>'s @party"); null for
    // the user's own.
    private string? _userBreakHoldAskedBy;
    // The break stopped a room attack: a kill that lands behind it may be any
    // monster's, so only a look at the room says whether the held one still stands.
    private bool _userBreakHoldRoomAttack;
    // The terminal was told of the hold, so it is told of its end too. Not for a
    // break typed with the engine off, which every hand-fought break is.
    private bool _userBreakHoldAnnounced;
    // Whom the user last attacked by hand here, for a break typed while the engine
    // holds no target of its own (Auto-Combat off, hand-fighting).
    private string? _userAttackedLast;
    private (DateTimeOffset At, string Text)? _lastUserBreakHoldEnd;
    // An exp line came under the hold before any *Combat Off* did, inside the round
    // the break was typed in: the round beat the break to the game and its kill is
    // the held monster's (a single-target attack has no other). An exp line later
    // than that is something else's doing, a damage shield's for one, and says
    // nothing of which monster died.
    private bool _userBreakHoldOffSeen;
    private bool _userBreakHoldKillRaced;
    private static readonly TimeSpan UserBreakRaceWindow = TimeSpan.FromSeconds(6);
    // The user's attack took the hold off but named no monster we could place: the
    // round is theirs, and the next one picks a target as any fresh fight does.
    private bool _repickAfterUserAttack;

    // When the held monster was last seen attacking: the moment of the break, then
    // each attack line of its own (MonsterAttackLine). A round tick with no such
    // line for HeldMonsterSilentRound is a silent round. The length is a round and
    // a second: a round's lines arrive within a moment of the tick that marks it,
    // in no fixed order with it, so the tick straight after an attack line is
    // always inside it and the one after that, a whole round on, is always past.
    private DateTimeOffset _heldMonsterAttackedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan HeldMonsterSilentRound = TimeSpan.FromSeconds(6);
    // A silent round in a lit room asked for the room to be read, and that reading
    // hasn't been judged yet.
    private bool _heldSilenceReadAsked;
    // A reading showed the monster silent but still listed. A monster that goes on
    // being silent isn't looked at every round: the next look its silence asks for
    // comes no sooner than this.
    private DateTimeOffset _heldSilenceReadNotBefore = DateTimeOffset.MinValue;
    private static readonly TimeSpan HeldSilenceReadBackOff = TimeSpan.FromSeconds(30);
    // We asked to see the room under the hold and its roster hasn't come yet. A
    // room nobody is left in prints no "Also here:" at all, so the display ending
    // with this still set is the answer "empty" (NoteRoomDisplayed). A reading
    // comes back within a moment; one still owed HeldRoomReadWait after it was
    // sent for, at a round tick, is not coming.
    private bool _heldRoomLookPending;
    private DateTimeOffset _heldRoomLookSentAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan HeldRoomReadWait = TimeSpan.FromSeconds(3);

    // The monster a typed `break` is holding the attack on, or null.
    public string? UserBreakHoldTarget => _userBreakHold;

    // The hold is keeping an engine that would otherwise fight from fighting. The
    // combat tracker's idle watchdog reads it: a quiet room under the hold is meant,
    // not a stall, and its force-clear would let a walk step out of the room.
    public bool AttackHeldByUserBreak => _userBreakHold is not null && EngineOn();

    // A line for the terminal as the hold is set and as it ends.
    public event Action<string>? UserBreakHoldNotice;

    // Diagnostics for the bug report.
    public string UserBreakHoldSummary
    {
        get
        {
            string last = _lastUserBreakHoldEnd is { } e
                ? $"last ended {e.At:HH:mm:ss.fff} ({e.Text})"
                : "none ended this session";
            return _userBreakHold is { } held
                ? $"on '{held}' since {_userBreakHoldSince:HH:mm:ss.fff}"
                  + (_userBreakHoldAskedBy is { } by ? $", asked for by {by}" : ", the user's own")
                  + $", {_userBreakHoldCount} of that name listed"
                  + $", its last attack line (or the break) {(_now() - _heldMonsterAttackedAt).TotalSeconds:0.0}s ago"
                  + (_heldSilenceReadAsked ? ", silent for a round: the room is being read to see whether it is still listed" : "")
                  + (_heldSilenceReadNotBefore > _now()
                      ? $", silent but listed at the last look: no look for its silence for another {(_heldSilenceReadNotBefore - _now()).TotalSeconds:0}s"
                      : "")
                  + (_userBreakHoldRoomAttack ? ", a room attack broken off" : "")
                  + (EngineOn() ? "" : ", engine off") + $"; {last}"
                : $"(none); {last}";
        }
    }

    private bool EngineOn() => _isEnabled() || ForceClearActive;

    // The user's own `break` went out (routed by OutboundBreakObserver, at send, so
    // one typed ahead of the round counts although the game answers it later).
    // askedBy says whose relayed `break` it was ("<name>'s @do", "<name>'s @party");
    // such a break holds the attack exactly as the user's own does (user,
    // 2026-10-10).
    public void NoteUserBreak(string word, string? askedBy = null)
    {
        if (_disposed || _userBreakHold is not null) return;
        string whose = WhoseCommand(askedBy);
        string? target = _currentTarget ?? _castingSpellTarget;
        if (target is null
            && _userAttackedLast is { } byHand
            && _classifier.Current is { } room
            && TargetPresent(room, byHand))
            target = byHand;
        if (target is null)
        {
            _log?.Combat(LogCategory,
                $"{whose} '{word}' — no attack of ours or theirs is on record here, nothing to hold");
            return;
        }

        _userBreakHold = target;
        _userBreakHoldSince = DateTimeOffset.Now;
        _userBreakHoldRoomAttack = _roomChannelSpell is not null;
        _userBreakHoldAnnounced = EngineOn();
        _userBreakHoldOffSeen = false;
        _userBreakHoldKillRaced = false;
        _repickAfterUserAttack = false;
        _userBreakHoldAskedBy = askedBy;
        _userBreakHoldCount = _classifier.Current is { } listing ? CountOfName(listing, target) : 0;
        _heldMonsterAttackedAt = _now();
        _heldSilenceReadAsked = false;
        _heldSilenceReadNotBefore = DateTimeOffset.MinValue;
        // The round clock only runs once something has placed a round; a break
        // sent before the fight's first line would otherwise never see a boundary.
        _ensureCombatTickAnchor?.Invoke();
        _log?.Info(LogCategory,
            $"{whose} '{word}' — attack held on '{target}' until the user attacks, it dies or leaves, or we leave the room"
            + (_userBreakHoldAnnounced ? "" : " (engine off: nothing to hold back for now)"));
        if (_userBreakHoldAnnounced)
            UserBreakHoldNotice?.Invoke(askedBy is null
                ? $"Attack on {target} held by your break: attack to carry on"
                : $"Attack on {target} held by {askedBy} break: attack to carry on");

        // A neutral the user hit by hand was the engine's to finish only because of
        // that attack. Breaking from it takes that back: whatever ends the hold
        // later, it is not fought again until the user attacks it again. A hostile
        // is fought for being hostile and is not in this set.
        if (_userEngagedInstances.Remove(target))
            _log?.Combat(LogCategory,
                $"'{target}' was a neutral the user had engaged — the break takes it back out of the engine's hands");

        // Stand the engine down as the Auto-Combat-off branch of OnEntitiesObserved
        // does, now and not at the next room event: a target or spell left standing
        // is what the resumes and the round heartbeat act on.
        _currentTarget = null;
        _guardBlockedTarget = null;
        _awaitingFollowAnnounce = false;
        _awaitingNotLast = false;
        _arrivalSettleArmed = false;
        _arrivalSettleCarriedRelease = null;
        DisarmHoldDeadline();
        ClearBackstabResolution();
        ClearAttackSpellCascadeState();
    }

    // The user's own attack went out (OutboundBreakObserver): an attack word or an
    // attack spell's cast code, with what it was aimed at. It ends the hold, and the
    // engine carries on with what the user attacked: this round is theirs, and the
    // monster they named becomes the target so its death is read and the next one
    // picked as in any fight.
    public void NoteUserAttack(string word, string? target, string? askedBy = null)
    {
        if (_disposed) return;
        string? held = _userBreakHold;
        EngageableCandidate? aimedAt = ResolveManualTarget(target ?? held ?? _userAttackedLast);
        if (aimedAt is { } hit) _userAttackedLast = hit.RawName;
        if (held is null) return;

        string line = target is null ? word : $"{word} {target}";
        EndUserBreakHold(askedBy is null
            ? $"the user sent '{line}'"
            : $"'{line}' sent by {askedBy}");
        if (!Fighting()) return;
        NoteUserAttackOverride($"{WhoseCommand(askedBy)} '{word}' took the break hold off", target ?? held);
        if (aimedAt is { } cand
            && MonsterEngagement.IsEngageable(
                ResolveOverlay(cand.MonsterNumber), _userEngagedInstances.Contains(cand.RawName)))
            _currentTarget = cand.RawName;
        if (_currentTarget is null)
        {
            _repickAfterUserAttack = true;
            _ensureCombatTickAnchor?.Invoke();
        }
    }

    private static string WhoseCommand(string? askedBy) => askedBy ?? "the user's";

    // Every RoomTracker.StateChanged (wired in AppServices). A change of room is
    // NoteRoomLeft's. A display that the tracker reads as the room it already had us
    // in, from Confirmed to Confirmed, places the roster just read in the room the
    // fight on record was seen in (_rosterRoomPlaced). A display it can't place
    // (Suspect, Lost) places nothing, and the holds wait for their deadline.
    public void NoteRoomTransition(RoomTransition t, DateTimeOffset? lastMoveSentAt)
    {
        if (_disposed) return;
        if (t.NewRoom?.Key != t.PreviousRoom?.Key) NoteRoomLeft(lastMoveSentAt);
        else if (t is { Displayed: true, NewRoom: not null,
                        PreviousConfidence: RoomConfidence.Confirmed, NewConfidence: RoomConfidence.Confirmed })
            NoteRoomShownAgain();
    }

    // The room shown is the one we were in. A hold latched on its roster only
    // because the fight on record couldn't yet be placed here is let go, and the
    // pick goes out as mid-fight picks do.
    private void NoteRoomShownAgain()
    {
        if (_rosterRoomPlaced) return;
        _rosterRoomPlaced = true;
        if (!_fightUnderwayHere || (!_awaitingNotLast && !_awaitingFollowAnnounce)) return;
        if (!Fighting() || _currentTarget is not null || _classifier.Current is not { } obs) return;
        _log?.Combat(LogCategory,
            "the room was shown again with no move of ours — the fight seen here is this room's; "
            + "the attack-order / follow hold isn't needed, picking now");
        _awaitingNotLast = false;
        _awaitingFollowAnnounce = false;
        DisarmHoldDeadline();
        OnEntitiesObserved(obs);
    }

    // The character left the game: out to the board's menu (AppServices), or the
    // link dropped (OnDisconnected). There is no room left to hold in, and the room
    // shown on the way back in is a new opening, even when it is the same room: a
    // relog that lands where we were reads as the room shown again.
    public void NoteGameLeft()
    {
        if (_disposed) return;
        if (_fightUnderwayHere || _awaitingNotLast || _awaitingFollowAnnounce)
            _log?.Combat(LogCategory, "the game was left — dropping the attack-order / follow room state");
        _awaitingNotLast = false;
        _awaitingFollowAnnounce = false;
        LeaveRoomFight();
    }

    // The room the tracker has us in changed (NoteRoomTransition), and
    // lastMoveSentAt is when a move of ours last went out (our own step, a flee's,
    // a follow behind the leader). Leaving the room ends the hold. A change of
    // room with no move since the break is not a leaving: the tracker corrected
    // where it thinks we stand (a re-localisation in a grid of like-named rooms),
    // the monster is where it was, and the hold stands. Whether a move went out is
    // the test RoomEntityClassifier makes before it wipes a roster on a room change.
    //
    // After a real move the new room's roster may already have been read under the
    // hold, which stood the engine down for it, so it is looked at again once the
    // move's own handlers have run.
    public void NoteRoomLeft(DateTimeOffset? lastMoveSentAt)
    {
        if (_disposed) return;
        // The new room's roster may already be read, with its hold armed (see
        // _roomStateAt): only what is older than that read is the room we left's.
        if (_roomStateAt < _rosterReadAt) ForgetRoomFight();
        _rosterRoomPlaced = true;
        // The new room began with that roster read.
        if (_rosterReadAt > _lastRoomLeaveAt) _lastRoomLeaveAt = _rosterReadAt;
        if (_holdDeadlineArmedAt < _rosterReadAt) DisarmHoldDeadlineKeepingHeldPick();
        if (_userBreakHold is { } held && !(lastMoveSentAt >= _userBreakHoldSince))
        {
            _log?.Combat(LogCategory,
                $"the tracker's room changed with no move of ours since the break — a position correction; the hold on '{held}' stands");
            return;
        }
        _userAttackedLast = null;
        if (_userBreakHold is null) return;
        EndUserBreakHold("we left the room");
        _post(() =>
        {
            if (!_disposed && _userBreakHold is null && _classifier.Current is { } here)
                OnEntitiesObserved(here);
        });
    }

    // A profile was loaded (the hold was the last character's), or Reset States
    // asked for every held state to go.
    public void ClearUserBreakHold(string why)
    {
        _userAttackedLast = null;
        EndUserBreakHold(why);
    }

    // How many monsters the room lists under the held monster's name.
    private static int CountOfName(RoomEntitiesObservation obs, string rawName)
    {
        int count = 0;
        foreach (RoomEntity e in obs.Entities)
            if (e.Kind == EntityKind.Monster
                && string.Equals(e.RawName, rawName, StringComparison.OrdinalIgnoreCase))
                count++;
        return count;
    }

    // A reading of the room under the hold. One fewer of the name than before is the
    // death (or the leaving) of one of them, which ends the hold (user, 2026-10-10);
    // one more is noted so its own going is seen. As many as before, on the reading
    // a silent round asked for, is a monster that is quiet and alive: the hold
    // stands, and its silence asks for no other look for HeldSilenceReadBackOff.
    private void JudgeUserBreakHoldByRoster(RoomEntitiesObservation obs, string held, bool freshRead)
    {
        // The roster wiped for a change of room lists nobody because it was emptied,
        // not because anyone was seen gone. Whether we left is NoteRoomLeft's to
        // say: it is told of the same change, with the move to judge it by.
        if (obs.Source == RoomObservationSource.RoomChange) return;
        int listed = CountOfName(obs, held);
        if (listed < _userBreakHoldCount)
        {
            EndUserBreakHold(listed == 0
                ? "it is no longer in the room"
                : "one of that name died or left the room");
            return;
        }
        _userBreakHoldCount = listed;
        if (!freshRead || !_heldSilenceReadAsked) return;
        _heldSilenceReadAsked = false;
        _heldSilenceReadNotBefore = _now() + HeldSilenceReadBackOff;
        _log?.Info(LogCategory,
            $"'{held}' made no attack for a round but the room still lists it — the hold stands "
            + $"(its silence asks for no other look for {HeldSilenceReadBackOff.TotalSeconds:0}s)");
    }

    // Every line read under the hold: an attack by the held monster, on us or on
    // anyone else here, says it still stands.
    private void OnLineUnderUserBreakHold(Terminal.LineExtractor.EmittedLine line)
    {
        if (_disposed || _userBreakHold is not { } held) return;
        if (MonsterAttackLine.IsBy(line, held, NamesInRoomWith(held)))
            NoteHeldMonsterAttacked();
    }

    // The same from the router's own patterns for an attack on us (mob hits, mob
    // misses, mob attacks, our dodge), which go by wording alone where the line
    // classifier needs a miss's colour.
    private void OnAttackOnUsUnderUserBreakHold(MatchResult match)
    {
        if (_disposed || _userBreakHold is not { } held) return;
        Dictionary<string, string> names = NamesInRoomWith(held);
        if (DamageLineAttributor.LeadingActor(match.Text, names.Keys) is { } lead
            && names[lead].Equals(held, StringComparison.OrdinalIgnoreCase))
            NoteHeldMonsterAttacked();
    }

    // The silence clock starts again. A reading its silence had asked for is no
    // longer waited on: whenever it comes it is judged by the count like any other.
    private void NoteHeldMonsterAttacked()
    {
        _heldMonsterAttackedAt = _now();
        _heldSilenceReadAsked = false;
    }

    // Every name the room's occupants go by, mapped to the name the roster lists
    // them under, with the held monster's own.
    private Dictionary<string, string> NamesInRoomWith(string held)
    {
        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase) { [held] = held };
        if (_classifier.Current is not { } room) return names;
        foreach (RoomEntity e in room.Entities)
        {
            if (string.IsNullOrWhiteSpace(e.RawName)) continue;
            names[e.RawName] = e.RawName;
            if (!string.IsNullOrWhiteSpace(e.ResolvedName)) names.TryAdd(e.ResolvedName, e.RawName);
        }
        return names;
    }

    // A round boundary under the hold (OnCombatTick, the round clock every engine
    // runs on), with no attack line from the held monster for a round.
    //
    // Lit room, engine on: the room is read, and that reading decides
    // (JudgeUserBreakHoldByRoster, or NoteRoomDisplayed for a room that lists
    // nobody). A reading that was sent for and hasn't come by the next boundary is
    // not waited for: the monster is taken for dead.
    // Dark room: a look lists nobody whoever is there, so the silence is all there
    // is to go by, and the monster is taken for dead at once.
    // Engine off: nothing automatic is sent and nothing waits on the answer. The
    // hold is left as it is and judged here once the engine is back on.
    private void NoteRoundBoundaryUnderUserBreakHold()
    {
        if (_userBreakHold is not { } held) return;
        DateTimeOffset now = _now();
        if (!EngineOn())
        {
            _heldSilenceReadAsked = false;
            return;
        }
        if (_heldSilenceReadAsked && _heldRoomLookPending && now - _heldRoomLookSentAt >= HeldRoomReadWait)
        {
            EndUserBreakHoldForTheDead(held, "no attack from it for a round and the room read never came: taken for dead");
            return;
        }
        if (now - _heldMonsterAttackedAt < HeldMonsterSilentRound) return;
        if (_wireSender is null || _isInDarkRoom?.Invoke() == true)
        {
            EndUserBreakHoldForTheDead(held, "no attack from it for a round in a room that can't be read: taken for dead");
            return;
        }
        if (!_heldSilenceReadAsked)
        {
            if (now < _heldSilenceReadNotBefore) return;
            _heldSilenceReadAsked = true;
            _log?.Info(LogCategory,
                $"no attack line from '{held}' for a round — reading the room to see whether it is still listed");
        }
        // On another re-display's cooldown the heartbeat asks again.
        LookAtRoomUnderHold("held monster silent for a round");
    }

    // The hold ends on a monster taken for dead with no reading of the room to go
    // by: it is dropped from the roster as a death is, which picks the next target.
    private void EndUserBreakHoldForTheDead(string held, string why)
    {
        EndUserBreakHold(why);
        if (!Fighting()) return;
        if (!_classifier.RemoveDeadEntity(held) && _classifier.Current is { } here)
            OnEntitiesObserved(here);
    }

    // The 1 s heartbeat: the room read a silent round asked for, sent once another
    // re-display's cooldown lets it out.
    public void OnHeartbeat()
    {
        if (_disposed || _userBreakHold is null) return;
        if (!_heldSilenceReadAsked || _heldRoomLookPending) return;
        LookAtRoomUnderHold("held monster silent for a round");
    }

    private bool LookAtRoomUnderHold(string context)
    {
        if (_wireSender is null || !EngineOn() || !TrySendRoomRefresh(context)) return false;
        _heldRoomLookPending = true;
        _heldRoomLookSentAt = _now();
        return true;
    }

    // A room display has finished (not a `look <direction>` at the next room). When
    // it is the one asked for under the hold and it listed nobody, the held monster
    // is gone with everyone else: the hold ends, and the roster that still names it
    // is emptied, which lets go of the Combat gate. With an "Also here:" in it the
    // roster was read on that line and has already decided.
    //
    // A guard fight's kill is judged the same way: a display finished after it with
    // no "Also here:" read since lists nobody, the priority included. That's how the
    // room answers when the priority's kill emptied it (JudgeGuardBlockByRoster only
    // hears from a display that lists someone).
    public void NoteRoomDisplayed()
    {
        if (_disposed) return;
        if (_guardBlockedTarget is { } guarded && _guardFightKillAt > _rosterReadAt)
            DropGuardBlock(guarded, "is gone: the room re-displayed after the kill with nobody in it");
        if (_userBreakHold is not { } held || !_heldRoomLookPending) return;
        _log?.Combat(LogCategory,
            $"the room re-displayed with nobody in it — '{held}' is gone; dropping the stale roster");
        EndUserBreakHold("the room it was read in lists nobody");
        _classifier.NoteRoomChanged();
    }

    private void EndUserBreakHold(string why)
    {
        if (_userBreakHold is not { } held) return;
        _userBreakHold = null;
        _userBreakHoldCount = 0;
        _userBreakHoldAskedBy = null;
        _heldSilenceReadAsked = false;
        _heldSilenceReadNotBefore = DateTimeOffset.MinValue;
        _heldRoomLookPending = false;
        _lastUserBreakHoldEnd = (DateTimeOffset.Now, $"'{held}': {why}");
        _log?.Info(LogCategory, $"attack hold on '{held}' ended — {why}");
        if (_userBreakHoldAnnounced)
            UserBreakHoldNotice?.Invoke($"Attack hold on {held} ended: {why}");
    }

    // Whether a client command is an attack of the engine's: the swing last sent, an
    // attack word, or an attack spell's cast. Asked under the hold before a fumble
    // re-fires the last client command, which may be the attack sent ahead of the
    // user's break.
    private bool IsAttackLine(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        string cmd = command.Trim();
        if (_lastAttackCommand is { Length: > 0 } swing
            && cmd.Equals(swing, StringComparison.OrdinalIgnoreCase)) return true;
        int space = cmd.IndexOf(' ');
        string word = space >= 0 ? cmd[..space] : cmd;
        return AttackCommandWords.IsAttack(word) || _isCombatSpell?.Invoke(word) == true;
    }

    // ----- Manual user-attack override -------------------------------------
    // When the user hand-types an attack this round — a combat spell (round energy
    // 1–1000, see CombatSpellIndex / GAME_MECHANICS) or a physical attack verb — they
    // took the round's action, so the engine must NOT re-send its own auto attack until
    // the next round. Set here, checked by the between-round resume (TryResumeEngage),
    // and cleared at the top of the next combat tick so control returns next round. A
    // hand-cast IN-BETWEEN spell (heal / buff / cure, energy 0) is NOT an override — it
    // keeps the resume-after-cast behaviour (NoteManualBetweenRoundCast).
    private bool _userAttackOverride;
    // The between-round-cast resume was skipped for the override while in spell mode;
    // the next round's tick owes the re-announce (ResumeSpellHeldForUserAttack).
    private bool _spellResumeHeldForUserAttack;
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
    // Self-defense: a monster is swinging at us (a mob-hits / mob-misses / we-dodge
    // line — each carries the attacker's name in group 0). When auto-combat is
    // engaging this room (so _isEnabled: auto-combat on AND not a do-not-attack /
    // combat-suppressed room) and the attacker's relationship lets us fight back —
    // Friend, Enemy, or Neutral, and Hangup or Flee only while the answer of their
    // own is not coming: a Hangup monster is answered with a hang-up on sight and a
    // Flee monster with a run (MonsterRelationshipWatcher), so one is fought back
    // only when no hang-up will come (Disable Hangups on, or the minute after a
    // reconnect) and the other only when no run will (user, 2026-10-09: "fight
    // back", asked of each) — mark its instance user-engaged so the engine takes it
    // over,
    // even a Friend it would normally leave alone or a neutral we never provoked.
    // Report paradigm-20260921-132800: a hand-attacked Friend (a Friend-relationship
    // NPC) kept hitting the player every round while the engine sat idle, because a
    // Friend is never auto-engageable on its own and, after a buff dropped combat,
    // nothing re-engaged it. Re-runs the room dispatch on a genuinely new mark so a
    // stuck / idle engine picks it up immediately.
    //
    // A line with no damage in it is a swing only when the game printed it as one.
    // The miss pattern is a shape ("The <monster> <verb> at you") that an NPC's
    // greeting has too, and on the shape alone a greeted Friend was bashed (report
    // paradigm-20261012-132421: "The old man looks at you curiously, …").
    private void OnAttackedInSelfDefense(MatchResult match)
    {
        if (_disposed || !Fighting()) return;
        if (match.PatternId == KnownPatterns.MobMisses && !MonsterAttackLine.IsOnUs(match.Line))
        {
            _log?.Combat(LogCategory,
                $"self-defense: '{match.Text.Trim()}' has a swing's shape but not a miss's colour — "
                + "not taken for an attack");
            return;
        }
        // Running a walk-to through a hostile town (e.g. an evil character in a guarded
        // city): keep running rather than turning to fight. Looping / Auto-Lair / idle
        // still defend.
        if (_selfDefenseSuppressedByTravel?.Invoke() == true) return;
        EndMoveHoldIfTimeIsUp();
        if (match.Groups.Count == 0) return;
        if (ResolveManualTarget(match.Groups[0]) is not { } cand) return;

        MonsterRelationship rel = ResolveOverlay(cand.MonsterNumber).Relationship
                                  ?? MonsterRelationship.Enemy;
        if ((rel is MonsterRelationship.Flee or MonsterRelationship.Hangup)
            && _noAnswerComing?.Invoke(rel) != true) return;

        if (_userEngagedInstances.Contains(cand.RawName)) return;   // already engaged — nothing new
        if (MoveInFlight())
        {
            HoldForOurMove();
            if (_attackersHeldForMove.Add(cand.RawName))
                _log?.Combat(LogCategory,
                    $"self-defense: '{cand.RawName}' ({rel}) swung at us while our move is in flight — not answered "
                    + "from the room we're leaving; the room we land in decides");
            return;
        }
        _userEngagedInstances.Add(cand.RawName);
        _log?.Combat(LogCategory,
            $"self-defense: '{cand.RawName}' ({rel}) is attacking us — marking engaged so the engine fights back");
        if (_classifier.Current is { } obs) OnEntitiesObserved(obs);   // re-decide now; unstick an idle engine
    }

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

    // Re-announce the attack spell after a between-round cast's *Combat Off* stopped
    // its server-side repeat. Shared by the Off handler (the usual, same-round resume)
    // and the round tick (a resume a user-typed attack held for a round).
    private void ResumeSpellAfterBetweenRoundCast(RoomEntitiesObservation live, string target,
        EngageableCandidate cand)
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
        // just closed — tally it toward MaxCasts NOW, before the re-decide inside
        // DispatchRoundAction. The heartbeat can't (it bails while _combatOff), so
        // without this the resume re-announces the just-capped spell uncapped (LBOL
        // 2x, report paradigm-20260820-063541). Gated on ReadRoundCount having moved
        // since the last tally (a cast of ours confirmed), so a heal that interrupted
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
                new CombatSpellDecision(interruptedAction, interruptedSpell), target);
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
            $"between-round-cast resume → re-announcing attack spell on {target}");
        // bypassRecastInterval: the re-announce goes out this tick rather than
        // waiting out the burst guard. (The buff/heal that dropped *Combat Off* is
        // paced on the between-round clock and can't trip the guard itself.) This
        // resume is guarded to fire once per interrupt (see above), so the bypass
        // can't compound into a burst.
        DispatchRoundAction(_readSettings(), cand, CountEngageable(live), live,
            bypassRecastInterval: true);
    }

    // The round after a user-typed attack held the between-round-cast resume: the
    // override is over, combat is still off, and the spell is still owed. Skipped
    // when an attack has gone out since the cast or the target has left the room.
    private void ResumeSpellHeldForUserAttack()
    {
        if (_castingSpellTarget is not { } target
            || _lastAttackSentAt > _betweenRoundCastAt
            || _betweenRoundCastAt == _lastSpellResumeForBetweenRoundCastAt
            || _classifier.Current is not { } live
            || !TargetPresent(live, target)
            || TryBuildCandidate(live, target) is not { } cand)
            return;
        ResumeSpellAfterBetweenRoundCast(live, target, cand);
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
    // identical for every monster, so no per-monster death message is needed.
    private void OnUserGainExperience(MatchResult match)
    {
        _lastExpGainAt = DateTimeOffset.Now;
        _expGainsThisRound++;
        _expGainsSinceRosterRead++;
        if (_userBreakHold is not null && !_userBreakHoldOffSeen
            && DateTimeOffset.Now - _userBreakHoldSince < UserBreakRaceWindow)
            _userBreakHoldKillRaced = true;
        _lastExpWasRoomSpellKill = false;
        NoteRoomSpellKill(match);
        if (_currentTarget is not null && _attackSentSinceDeath)
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
        // One kill under a running room spell is the same trap: the spell keeps
        // firing with no *Combat Off*, the corpse stays on the roster, and the count
        // never falls below the room spell's minimum, so the survivor is roomed
        // round after round instead of getting the single-target attack (report
        // paradigm-20261007-131120: hsto killed one of two, then hsto again).
        else if (_roomChannelSpell is not null)
            ForceAoeMultiKillReparse("kill under a room spell");
    }

    // Count an exp line as a kill of our room spell (RoomSpellKill) when all of this
    // holds, and leave it to its *Combat Off* otherwise, as before:
    //   - a room attack of ours is running in this room (_roomChannelSpell);
    //   - its damage line was read just ahead of the exp line (RoomSpellKillWindow),
    //     so the exp belongs to a round the spell landed in;
    //   - the room still lists a monster the exp can be: this round's exp lines since
    //     the room was read don't outnumber the monsters it listed. An exp line past
    //     that is some monster the roster never held, and is not counted here.
    private void NoteRoomSpellKill(MatchResult exp)
    {
        if (RoomSpellKill is not { } raise) return;
        if (_roomChannelSpell is not { } spell) return;
        if (_now() - _roomSpellLandedAt >= RoomSpellKillWindow)
        {
            LeaveRoomSpellExp($"exp line under room spell '{spell}' with no damage line of the spell read ahead of it");
            return;
        }
        if (_classifier.Current is not { } roster)
        {
            LeaveRoomSpellExp($"exp line under room spell '{spell}' with no roster of the room read");
            return;
        }

        // Every monster the room lists, not only the ones we would attack: the spell
        // hits the room, so a neutral standing in it dies and pays exp like the rest.
        int listed = roster.Entities.Count(e => e.Kind == EntityKind.Monster);
        if (_expGainsSinceRosterRead > listed)
        {
            LeaveRoomSpellExp(
                $"exp line {_expGainsSinceRosterRead} under room spell '{spell}' with {listed} monster(s) listed");
            return;
        }

        // The dead one is whichever of them the spell killed.
        List<MonsterDeathIdentity> kinds = new();
        foreach (RoomEntity e in roster.Entities)
        {
            if (e.Kind != EntityKind.Monster) continue;
            bool seen = false;
            foreach (MonsterDeathIdentity k in kinds)
                if (e.MonsterNumber is { } n ? k.Number == n
                    : k.Number is null && string.Equals(k.Name, e.ResolvedName, StringComparison.OrdinalIgnoreCase))
                {
                    seen = true;
                    break;
                }
            if (!seen) kinds.Add(new MonsterDeathIdentity(e.MonsterNumber, e.ResolvedName));
        }

        int? gained = exp.Groups.Count > 0 && int.TryParse(exp.Groups[0], out int amount) ? amount : null;
        _lastExpWasRoomSpellKill = true;
        _roomSpellKillsCounted++;
        _lastRoomSpellExp = (DateTimeOffset.Now,
            $"counted: kill under room spell '{spell}' ({_expGainsSinceRosterRead} of {listed} listed)");
        _log?.Combat(LogCategory,
            $"kill under room spell '{spell}' — counted on its exp line ({_expGainsSinceRosterRead} of {listed} listed)");
        raise(gained, kinds);

        // Counted, so spent: this exp line no longer explains a *Combat Off* to come.
        // The caller drops the target and re-reads the room for the kill; an Off
        // inside ExpKillWindow of it would otherwise be taken for a second kill, of
        // whatever survivor that re-read had just made the target.
        _lastExpGainAt = DateTimeOffset.MinValue;
    }

    // An exp line that came while a room spell of ours ran and was not counted on the
    // spot. It is no loss: the *Combat Off* after it counts it as every kill was
    // counted before. Logged so a kill count that looks short can be traced.
    private void LeaveRoomSpellExp(string what)
    {
        _lastRoomSpellExp = (DateTimeOffset.Now, $"left to its *Combat Off*: {what}");
        _log?.Combat(LogCategory, $"{what} — not counted as a kill here; left to its *Combat Off*");
    }

    private int _roomSpellKillsCounted;
    private (DateTimeOffset At, string Text)? _lastRoomSpellExp;

    // The exp line read last was counted as a kill of our room spell.
    private bool _lastExpWasRoomSpellKill;

    // True on Stock. Read in one place, StockRoomSpellRunsOn.
    private Func<bool>? _onStock;

    public void SetStockRealmProbe(Func<bool> onStock)
    {
        ArgumentNullException.ThrowIfNull(onStock);
        _onStock = onStock;
    }

    // Whether the *Combat Off* being handled is the one Stock prints after a kill of
    // our room spell, which does not stop the spell.
    //
    // Stock prints it because _distribute_experience ends the killer's combat like
    // that of any player aimed at the dead monster, whatever the killer was attacking
    // with (@0x44ca6e–0x44caa2). The room cast is not done at that point: it goes on
    // through the rest of the room and, run as a round action, ends by putting the
    // caster back on the round queue (_cast_no_target @0x4446c9–0x444701:
    // _engage_autocombat(caster, -1, 0xffff, 1, spell)), and next round that entry
    // casts the spell again with no command from us (_do_autocombat_for_user
    // @0x44c7a5–0x44c7cf). The kill's own _kill_autocombat (@0x44ca97) comes before
    // that re-queue, not after it, and the next round's target check passes an entry
    // with no target (_validate_auto_combat @0x44c124). The spell stops by itself on
    // the round it finds nothing to hit, with a line of its own and no *Combat Off*.
    //
    // Read as the spell ending, that Off had the next combat line or round tick send
    // the room spell again, which breaks the one running and costs the round.
    //
    // Paradigm prints no *Combat Off* for a room spell's kill while a monster stands,
    // and one after the kill that empties the room, where its spell has ended (user,
    // 2026-10-10): that Off is read as the end, as before.
    private bool StockRoomSpellRunsOn()
        => _lastExpWasRoomSpellKill && _offProbe.FollowsExpLine && _onStock?.Invoke() == true;

    // Diagnostics for the bug report: how many kills were counted on their exp lines
    // this session, and what became of the last exp line read under a room spell.
    public string RoomSpellKillSummary => _lastRoomSpellExp is { } last
        ? $"{_roomSpellKillsCounted} this session; last exp line {last.At:HH:mm:ss.fff} {last.Text}"
        : "(no exp line read under a room spell this session)";

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
        //
        // Only the kills the roster still lists count (_expGainsSinceRosterRead): once
        // the room has been read again, what it lists is what outlived them.
        //
        // Under a room spell of ours the kills are set against every monster listed,
        // not only the ones we would attack: the spell kills a neutral standing in
        // the room as readily, and that kill said nothing of the hostile beside it
        // (one hostile and two neutrals: the first neutral's exp line emptied the
        // roster and let go of the gate with the hostile alive).
        int listed = _classifier.Current is not { } cur ? 0
            : _roomChannelSpell is not null ? cur.Entities.Count(e => e.Kind == EntityKind.Monster)
            : CountEngageable(cur);
        if (listed > 0 && _expGainsSinceRosterRead >= listed)
        {
            _log?.Combat(LogCategory,
                $"AoE multi-kill ({_expGainsSinceRosterRead} exp) cleared all {listed} listed hostile(s) — "
                + "dropping the stale roster so the combat gate releases without the idle-stall wait");
            _classifier.NoteRoomChanged();
        }
        else if (listed > 0 && _expGainsThisRound >= listed)
        {
            _log?.Combat(LogCategory,
                $"AoE multi-kill: {_expGainsThisRound} exp this round, {_expGainsSinceRosterRead} since the room "
                + $"was last read — the {listed} hostile(s) it lists outlived the rest, roster kept");
        }

        if (TrySendRoomRefresh(context))
            _log?.Combat(LogCategory,
                $"{context} ({_expGainsThisRound} exp this round) — re-parsing room with "
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
            if (_userBreakHold is not null) _userBreakHoldOffSeen = true;

            if (StockRoomSpellRunsOn())
            {
                _lastExpWasRoomSpellKill = false;
                _log?.Combat(LogCategory,
                    "*Combat Off* after our room spell's kill — on Stock the spell is queued for the next round "
                    + "all the same; left running, nothing re-sent");
                return;
            }

            // Only the Off that stops the attack says how it ended; one arriving while
            // combat is already off adds nothing to that.
            if (!_combatOff) _offEndedAnsweredAttack = _engagedSinceLastAttack;
            _combatOff = true;
            _engageConfirmed = false;
            _engagedSinceLastAttack = false;

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

            // Prompt kill drop. A fresh exp gain explains this Off (wire: death → exp →
            // Off) while NO between-round survival cast does — so it's our kill's Off,
            // not a mid-fight heal's. Nothing else names the kill, so without this the
            // resume paths below re-attack the corpse ("You don't see X here!") and
            // stall a round waiting out the recovery — the reported "won't re-engage
            // after a kill until the next NPC attacks." Arm the existing
            // death-suppression guards (stamp _lastDeathAt) and drop spell mode so the
            // heartbeat can't re-cast at the corpse; the death→re-observe re-picks the
            // survivor. The no-between-round-cast gate keeps a heal's Off — or a party
            // share-exp landing beside one — from being misread as a kill (that path
            // resumes below).
            bool expExplainsOff = _currentTarget is not null
                && DateTimeOffset.Now - _lastExpGainAt < ExpKillWindow
                && DateTimeOffset.Now - _betweenRoundCastAt >= CastInterruptResumeWindow;
            // Nor is it a kill's when it answers a command (CommandOffProbe): the exp
            // is then an earlier kill's, a room spell's that left this target standing,
            // and the Off is the one our attack on that survivor prints ahead of its
            // *Combat Engaged* (or a typed `break`). Dropping the target for it threw
            // the fresh attack away (report paradigm-20261010-145330).
            if (expExplainsOff && _offProbe.CommandAnswered is { } answered)
            {
                _log?.Combat(LogCategory,
                    $"*Combat Off* answers '{answered}', not a kill — target={_currentTarget} kept");
            }
            else if (expExplainsOff)
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
            // The Off nothing of ours explains: no cast in the window, no kill (that
            // would have dropped the target above). Nothing resumes on it here, so say
            // what it is waiting for: OnCombatLine or OnCombatTick, weapon or spell.
            // Not every time: an attack that stops itself after each strike (KAI
            // pummel) prints this Off every round.
            else if (_offEndedAnsweredAttack
                && Fighting()
                && DateTimeOffset.Now - _unexplainedOffLoggedAt > UnexplainedOffLogSpacing
                && _currentTarget is { } stillHere
                && _classifier.Current is { } room
                && HasEngageable(room))
            {
                _unexplainedOffLoggedAt = DateTimeOffset.Now;
                _log?.Combat(LogCategory,
                    $"*Combat Off* with no cast of ours behind it and '{stillHere}' still in the room "
                    + "(a stun, an attack that stops after each strike) — re-attacking on the next combat line or round tick");
            }

            if (!suppressBetweenRoundResume
                && DateTimeOffset.Now - _betweenRoundCastAt < CastInterruptResumeWindow
                && _betweenRoundCastAt != _lastWeaponResumeForBetweenRoundCastAt
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
                    // Stamp this interrupt as resumed BEFORE dispatching so a later Off
                    // for the SAME cast can't re-fire, and let this distinct cast bypass
                    // ResumePacing — a second party heal a beat after the first must still
                    // re-attack, not sit paced out for a round. A typed gear command
                    // keeps the pacing (NoteTypedGearCommand).
                    _lastWeaponResumeForBetweenRoundCastAt = _betweenRoundCastAt;
                    bool typedGear = _betweenRoundCastAt == _typedGearStamp;
                    _log?.Combat(LogCategory, typedGear
                        ? "typed gear command's *Combat Off* → re-engaging weapon attack"
                        : "between-round-cast resume → re-engaging weapon attack");
                    TryResumeEngage(live,
                        bypassAttackGuard: _lastAttackSentAt <= _betweenRoundCastAt,
                        bypassPacing: !typedGear);
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
            // The attack already fired this round if our last attack went out AFTER the
            // between-round cast. That's the pre-attack-debuff-then-attack sequence
            // (isto → hsto same round, both 0-energy debuff + real attack): the attack's
            // OWN *Combat Off* then lands in the debuff's resume window and would
            // re-announce the attack a SECOND time (report paradigm-20260916-083245: two
            // hsto sent back-to-back as monsters arrived). The weapon resume above guards
            // this via ResumeAfterAttackGuard; the spell resume needs the same check. A
            // genuine mid-round survival cast (heal/buff) fires AFTER the attack, so
            // _lastAttackSentAt <= _betweenRoundCastAt and the resume still runs.
            bool attackAlreadyFiredThisRound = _lastAttackSentAt > _betweenRoundCastAt;
            if (!suppressBetweenRoundResume
                && DateTimeOffset.Now - _betweenRoundCastAt < CastInterruptResumeWindow
                && _betweenRoundCastAt != _lastSpellResumeForBetweenRoundCastAt
                && !attackAlreadyFiredThisRound
                && !manualPaced
                && _castingSpellTarget is { } spellTarget
                && (DateTimeOffset.Now - _lastDeathAt >= DeathInterruptWindow || survivorReadied)
                && _classifier.Current is { } liveSpell
                && TargetPresent(liveSpell, spellTarget)
                && TryBuildCandidate(liveSpell, spellTarget) is { } spellCand)
            {
                if (_userAttackOverride)
                {
                    // The user hand-typed this round's attack, so ours waits — but this
                    // Off stopped theirs too, and in spell mode nothing else brings the
                    // fight back: the heartbeat bails while combat is off, and a monster
                    // whose swing wording isn't parsed never wakes the mob-swing resume
                    // (report paradigm-20261003-194358: 65 s without an attack). Owe
                    // the re-announce to the next round's tick, when the override ends.
                    _spellResumeHeldForUserAttack = true;
                    _ensureCombatTickAnchor?.Invoke();
                    _log?.Combat(LogCategory,
                        $"between-round-cast resume held — user attack override; re-announcing on {spellTarget} next round");
                }
                else
                    ResumeSpellAfterBetweenRoundCast(liveSpell, spellTarget, spellCand);
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
            _engagedSinceLastAttack = true;
            _awaitingEngageSince = null;
        }
    }

    // Guard/redirect recognition — "<guard> moves to protect <priority>". While
    // a guarded monster (brigand chief) is shielded, our swing lands on the
    // guard, not the chief. We don't move _currentTarget onto the guard (the
    // server is already swinging the guard for us, and leaving _currentTarget on
    // the chief keeps the kill credited to the fight we chose); we just remember
    // the priority so each guard's death re-attacks it (TryGuardRetry). Only
    // acts when the protected monster is the one we're already trying to kill —
    // a protect line shielding some other room monster isn't ours to chase.
    private void OnMonsterProtect(MatchResult match)
    {
        if (!Fighting()) return;
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
        if (!Fighting()) return;
        if (_castingSpellTarget is not null) return;   // spell mode owns its re-cast
        if (_wireSender is null) return;

        DateTimeOffset now = DateTimeOffset.Now;
        if (HoldGuardRetryForUnjudgedKill(priority, now)) return;
        if (now - _lastAttackSentAt < ResumeAfterAttackGuard) return;
        if (now - _lastInterruptResumeAt < ResumePacing) return;
        _lastInterruptResumeAt = now;

        _currentTarget = priority;
        _log?.Combat(LogCategory, $"guard retry — re-attacking priority '{priority}' after guard fell");
        SendAttack(_readSettings().NormalAttackCommand, priority, refire: true,
                   refireReason: "guard retry");
    }

    // A kill in a guard fight doesn't say whether a guard or the guarded priority
    // fell: both pay exp and print *Combat Off* alike. The room read from the game
    // does. While it lists the priority the chase goes on; once it doesn't, the
    // priority is dead or gone, and a block left standing sends the next guard retry
    // at it, taking the target from whatever fight is left. Matched on either name
    // so only a roster plainly without the priority ends the chase.
    private void JudgeGuardBlockByRoster(RoomEntitiesObservation obs, string priority)
    {
        foreach (RoomEntity e in obs.Entities)
        {
            if (e.Kind != EntityKind.Monster) continue;
            if (string.Equals(e.RawName, priority, StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.ResolvedName, priority, StringComparison.OrdinalIgnoreCase))
            {
                _lastGuardRosterCheck = (DateTimeOffset.Now, $"'{priority}' still listed — block kept");
                return;
            }
        }
        DropGuardBlock(priority, "no longer listed");
    }

    private void DropGuardBlock(string priority, string why)
    {
        _guardBlockedTarget = null;
        _lastGuardRosterCheck = (DateTimeOffset.Now, $"'{priority}' {why} — block dropped");
        _log?.Combat(LogCategory, $"guarded priority '{priority}' {why} — clearing guard block");
    }

    // A guard fight's kill that no room read has judged yet holds the guard retry
    // (true while it waits), for at most GuardKillJudgeWait.
    private bool HoldGuardRetryForUnjudgedKill(string priority, DateTimeOffset now)
    {
        if (_guardFightKillAt <= _rosterReadAt || now - _guardFightKillAt >= GuardKillJudgeWait) return false;
        _log?.Combat(LogCategory,
            $"guard retry at '{priority}' held — the kill at {_guardFightKillAt:HH:mm:ss.fff} may have been "
            + "its own; waiting for the room read");
        return true;
    }

    // The wait for a guard fight's kill to be judged is up with no room read. Nothing
    // else would retry: the retry runs on a *Combat Off*, and with the roster
    // emptied by the kill's removal (which drops the priority's slot whichever of
    // them died) none may come. So retry now; at a corpse, the "You don't see"
    // reply ends the block.
    private void OnGuardKillWaitElapsed(DateTimeOffset killAt)
    {
        if (_disposed || _guardBlockedTarget is not { } priority) return;
        if (_guardFightKillAt != killAt || killAt <= _rosterReadAt) return;
        _guardFightKillAt = DateTimeOffset.MinValue;
        _lastGuardRosterCheck = (DateTimeOffset.Now,
            $"no room read within {GuardKillJudgeWait.TotalSeconds:F0}s of the kill at {killAt:HH:mm:ss.fff} — block kept, retried");
        _log?.Combat(LogCategory,
            $"no room read came within {GuardKillJudgeWait.TotalSeconds:F0}s of the kill — guard retry at '{priority}'");
        TryGuardRetry();
    }

    // What the last room read made of a guard block, for the bug report, led by a
    // kill in the fight that no read has judged yet.
    private (DateTimeOffset At, string Text)? _lastGuardRosterCheck;

    public string? LastGuardRosterCheck
    {
        get
        {
            string? last = _lastGuardRosterCheck is { } c ? $"{c.At:HH:mm:ss.fff} {c.Text}" : null;
            if (_guardBlockedTarget is null || _guardFightKillAt <= _rosterReadAt) return last;
            string pending = $"kill at {_guardFightKillAt:HH:mm:ss.fff}, not judged yet";
            return last is null ? pending : $"{pending}; before it {last}";
        }
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
        _roomChannelSpell = null;   // switched off the room spell onto the weapon
        string verb = string.IsNullOrWhiteSpace(command) ? "a" : command.Trim();
        WarnOnDeadAttackWord(verb);
        string line = $"{verb} {target}";
        if (priority is { } prio)
            _log?.Combat(LogCategory, $"attack target={target} cmd={verb} prio={prio}");
        else
            _log?.Combat(LogCategory, $"attack target={target} cmd={verb}");
        _lastAttackCommand = line;
        if (_wireSender is null) return;
        if (HoldAttackIfGated(verb, target)) return;
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
        _roomChannelSpell = null;   // switched off the room spell onto the weapon
        string verb = string.IsNullOrWhiteSpace(command) ? "a" : command.Trim();
        WarnOnDeadAttackWord(verb);
        string line = $"{verb} {target}";
        _log?.Combat(LogCategory,
            $"re-fire target={target} cmd={verb} timing={refireReason}");
        _lastAttackCommand = line;
        if (_wireSender is null) return;
        if (HoldAttackIfGated(verb, target)) return;
        _pendingAttackEchoVerb = verb;   // claim our own swing so the attack observer doesn't read it as manual
        _wireSender(Encoding.Latin1.GetBytes(line + "\r"));
        NoteAttackSent();
    }

    // An attack command the game doesn't take is still sent as the user set it; the
    // log says so once a connection, at the first swing made with it, so a fight
    // that never engages has its reason on record.
    private bool _deadAttackWordWarned;

    private void WarnOnDeadAttackWord(string verb)
    {
        if (_deadAttackWordWarned || !MartialArtsCommand.IsDeadKickLead(verb)) return;
        _deadAttackWordWarned = true;
        _log?.Warn(LogCategory,
            $"attack command `{verb}` is not a command the game takes: kick's shortest form is `kic`. "
            + "It is sent as set; change it in Settings → Combat (Normal / Alternate weapon attack command).");
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
    // Returns true when it re-sent the weapon swing (so the caller knows combat
    // handled the fumble and need not fall back to a generic re-fire). False when it
    // sent nothing: this isn't a weapon fight it can act on (spell mode, no target,
    // attacks blocked, no prior swing), or the swing has already been answered. The
    // caller then re-fires the last client command generically, which covers the
    // fumbled attack SPELL / item-use / other client commands (HandleFumble).
    //
    // The swing is re-sent only until the game answers it with `*Combat Engaged*`:
    // that line means the attack went through, and from then on the game repeats it
    // each round by itself. A fumble line in a fight that is under way is some other
    // command's (or the game's own round), and a fresh attack sent for it would break
    // the fight off and start it over (GAME_MECHANICS "Confusion fumbles — actions
    // fail and must be re-sent").
    public bool OnActionFailed()
    {
        if (_disposed || !Fighting()) return false;
        if (_castingSpellTarget is not null) return false;
        if (_currentTarget is null) return false;
        if (_lastAttackCommand is not { Length: > 0 } line) return false;
        if (_wireSender is null) return false;
        if (AttacksBlocked()) return false;   // can't re-send a swing while attacks are blocked
        if (EngagedForLastAttack)
        {
            _log?.Combat(LogCategory, $"action failed with the fight engaged — '{line}' is not re-sent");
            return false;
        }

        _combatOff = false;
        _log?.Combat(LogCategory, $"action failed — re-sending last attack '{line}'");
        _wireSender(Encoding.Latin1.GetBytes(line + "\r"));
        NoteAttackSent();
        return true;
    }

    // `*Combat Engaged*` has answered the attack last sent, weapon or spell, and no
    // `*Combat Off*` has come since: the game is carrying that attack on by itself.
    // Cleared by every attack sent, so a fresh one is unanswered until its own
    // Engaged line arrives.
    private bool _engagedSinceLastAttack;
    private bool EngagedForLastAttack => _engagedSinceLastAttack && !_combatOff;

    // The `*Combat Off*` that turned combat off came after `*Combat Engaged*` had
    // answered the attack last sent: the game took that attack and something then
    // stopped it (a typed `eq`, a cast, a stun). Nothing of ours is in flight, however
    // recently the attack went out, so ResumeAfterAttackGuard has nothing to protect.
    // Only meaningful while _combatOff; the next Off that turns combat off sets it anew.
    private bool _offEndedAnsweredAttack;
    private DateTimeOffset _unexplainedOffLoggedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan UnexplainedOffLogSpacing = TimeSpan.FromSeconds(12);

    // A fumble line arrived (ConditionTracker.ActionFailed): decide what, if anything,
    // goes out again. In order:
    //   1. an attack the game hasn't answered yet is re-sent (OnActionFailed);
    //   2. the engine's own attack in a fight already engaged is left alone, whoever
    //      would have re-sent it;
    //   3. anything else the client sent last is re-fired through the send gate: a
    //      heal, a buff, an item use. Only the attack is held back once engaged
    //      (user, 2026-10-09), in a weapon fight and a spell fight alike.
    // lastClientCommand is the gate's last send as text; replayLastClientCommand
    // re-sends it (EngineSendGate.ReplayLastClientCommand, which itself leaves out
    // bare moves and anything the user typed).
    public void HandleFumble(string? lastClientCommand, Action replayLastClientCommand)
    {
        ArgumentNullException.ThrowIfNull(replayLastClientCommand);
        if (OnActionFailed()) return;
        if (AttackAlreadyEngaged(lastClientCommand)) return;
        // The last client command can be the attack we sent ahead of the user's
        // break. Re-fired for a fumble it would be an attack under the hold.
        if (_userBreakHold is not null && IsAttackLine(lastClientCommand))
        {
            _log?.Combat(LogCategory,
                $"action failed with the attack held by the user's break — '{lastClientCommand}' is not re-sent");
            return;
        }
        replayLastClientCommand();
    }

    // Whether a client command is this engine's own attack — the weapon swing last
    // sent, or a cast of the attack spell announced — in a fight the game has already
    // engaged. The fumble re-fire asks before repeating the last client command: an
    // attack that is under way must not be sent again.
    public bool AttackAlreadyEngaged(string? command)
    {
        if (_disposed || !EngagedForLastAttack || string.IsNullOrWhiteSpace(command)) return false;
        string cmd = command.Trim();
        if (_lastAttackCommand is { Length: > 0 } swing
            && cmd.Equals(swing, StringComparison.OrdinalIgnoreCase)) return true;
        if (_announcedSpellCode is not { Length: > 0 } spell) return false;
        int space = cmd.IndexOf(' ');
        string word = space >= 0 ? cmd[..space] : cmd;
        return word.Equals(spell, StringComparison.OrdinalIgnoreCase);
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
        _fightUnderwayHere = true;
        _roomStateAt = _lastAttackSentAt;
        // The fight is underway in this room — closes the AoE debuff's "first round
        // only" window (the pre-attack debuff fires BEFORE this in the same dispatch,
        // so it still gets its entry shot).
        _combatSeenThisRoom = true;
        // A swing since the last death means the death→re-observe path has run;
        // a later cast-interrupt Off should resume, not stand down (see
        // _attackSentSinceDeath).
        _attackSentSinceDeath = true;
        // The owed attack just went out — clear the diagnostic latch (see
        // _spellAttackOwed). No-op for a weapon swing, which never set it.
        _spellAttackOwed = false;
        _engagedSinceLastAttack = false;
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
        if (!Fighting()) { _awaitingEngageSince = null; return; }
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
        _offProbe.Dispose();
        _classifier.EntitiesObserved -= OnEntitiesObserved;
        _announceSub.Dispose();
        _castAnnounceSub.Dispose();
        _roomPoisedSub.Dispose();
        _partyCombatSub.Dispose();
        _followDragSub.Dispose();
        _userHitsSub.Dispose();
        _mobHitsSub.Dispose();
        _mobMissesSub.Dispose();
        _targetGoneSub.Dispose();
        _weaponNoEffectSub.Dispose();
        _weaponLandedSub.Dispose();
        _fistsNoEffectSub.Dispose();
        _spellNoEffectSub.Dispose();
        _commandNoEffectSub.Dispose();
        _combatStatusSub.Dispose();
        _expGainSub.Dispose();
        _monsterProtectSub.Dispose();
        _roomSpawnSub.Dispose();
        _bsResolveHitsSub.Dispose();
        _bsResolveMissesSub.Dispose();
        _attackConfirmHitsSub.Dispose();
        _attackConfirmMissesSub.Dispose();
        _selfDefenseHitsSub.Dispose();
        _selfDefenseMissesSub.Dispose();
        _selfDefenseDodgesSub.Dispose();
        foreach (IDisposable sub in _attackOnUsSubs) sub.Dispose();
        _router.LineDispatched -= OnLineUnderUserBreakHold;
    }

    private readonly record struct EngageableCandidate(
        string RawName,
        string ResolvedName,
        int MonsterNumber,
        MonsterAttackPriority Priority,
        int AppearanceIndex,
        bool DontBackstab);
}
