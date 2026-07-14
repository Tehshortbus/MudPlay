using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Combat;

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

    private readonly RoomEntityClassifier _classifier;
    private readonly MonsterMessageStore _monsters;
    private readonly Func<int, MonsterOverlay> _resolveOverlay;
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
    private readonly IDisposable _monsterProtectSub;
    private readonly IDisposable _bsResolveHitsSub;
    private readonly IDisposable _bsResolveMissesSub;

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
    private Action<string?, string?>? _swapWeapon;
    private Action? _prepBackstabArmor;
    private Func<bool>? _isStealthed;
    private Func<int, bool>? _hasSeeHidden;
    private Func<bool>? _seeHiddenClearActive;
    private Func<bool>? _shadowRestHolding;
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

    // Timestamp of the last interrupt-resume (see TryResumeEngage) — paces
    // re-engages to one per round so a non-sustaining attack (KAI pummel, which
    // emits *Combat Off* after every strike) can't spin.
    private DateTimeOffset _lastInterruptResumeAt = DateTimeOffset.MinValue;

    // Minimum spacing between interrupt-resumes. Shorter than a combat round
    // (~5s) so a legitimate next-round resume isn't blocked, but long enough to
    // swallow the instant Off/Engaged cycle a per-strike attack produces.
    private static readonly TimeSpan ResumePacing = TimeSpan.FromMilliseconds(2500);

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

    // How recent a between-round cast must be for the next *Combat Off* to count
    // as that cast's interrupt. Generous enough to cover send→Off network
    // latency, far shorter than a round so a later pummel Off can't be
    // misattributed.
    private static readonly TimeSpan CastInterruptResumeWindow = TimeSpan.FromSeconds(3);

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
        Func<PartySettings>? readPartySettings = null)
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
        _monsterProtectSub = router.Subscribe(KnownPatterns.MonsterMovesToProtect, OnMonsterProtect);

        // Backstab surprise-round resolution rides on our own hit / miss lines.
        // Separate subscriptions from OnCombatLine (fan-out) so the resolution
        // watch doesn't perturb the resume/refresh net. UserMisses is otherwise
        // unsubscribed here; the handler self-gates on _awaitingBackstabResolution,
        // so its breadth (it also matches self-emotes) is harmless.
        _bsResolveHitsSub   = router.Subscribe(KnownPatterns.UserHits,   OnBackstabResolutionLine);
        _bsResolveMissesSub = router.Subscribe(KnownPatterns.UserMisses, OnBackstabResolutionLine);
    }

    // Bind the wire sender — typically the TelnetClient.SendAsync wrapper that
    // MainWindowViewModel exposes. Until set, CombatManager silently no-ops on
    // its outbound side (state transitions still log).
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Wire the gear actuator (EquipmentManager, the sole gear owner). swapWeapon
    // equips a weapon + off-hand immediately — the unpaced fast path that must
    // land before the next swing; prepBackstabArmor applies the Backstab set's
    // armor synchronously in the pre-move sequence, before the sn (null when no
    // backstab-armor automation is wired — the weapon still gets swapped either
    // way).
    public void SetWeaponActuator(
        Action<string?, string?> swapWeapon, Action? prepBackstabArmor = null)
    {
        ArgumentNullException.ThrowIfNull(swapWeapon);
        _swapWeapon = swapWeapon;
        _prepBackstabArmor = prepBackstabArmor;
    }

    // The monster name we last sent `attack` against, or null when no fight is in
    // flight.
    public string? CurrentTarget => _currentTarget;

    // Immutable view of the combat decision state, for the bug-report
    // engine-state dump. The believed-worn weapon is no longer shadowed here —
    // live inventory is authoritative (the actuator diffs against it), so the
    // report reads the worn weapon straight from the inventory snapshot instead.
    public readonly record struct DebugState(
        string? CurrentTarget,
        bool UsingAlternateWeapon,
        bool AwaitingBackstabResolution,
        string? PendingBackstabSpecies,
        string? GuardBlockedTarget);

    // UI-thread only (router handlers + the capture both run there), so no lock.
    public DebugState Snapshot() => new(
        _currentTarget, _usingAlternateWeapon,
        _awaitingBackstabResolution, _pendingBackstabSpecies,
        _guardBlockedTarget);

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

    private void OnEntitiesObserved(RoomEntitiesObservation obs)
    {
        CombatSettings settings = _readSettings();

        // Fresh observation of the room — any pending follow-deferral from a
        // previous observation is stale. Re-evaluated below once we know the
        // engageable set (a re-observe of the SAME room re-arms it if still apt).
        _awaitingFollowAnnounce = false;

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

        // Combat-off override for stealth runners. Normally combat-off
        // means we don't engage at all. But a stealth character sprinting
        // a walk-to route (AutoSneak on, combat off) that hits a room with
        // a SeeHidden monster can't re-sneak there — and running onward
        // would drag/stack monsters across rooms, lethal when solo.
        // CombatStateTracker owns the decision + latch (and holds the
        // walker gate so we actually stop); when it has force-clear
        // latched, we engage anyway and bypass the Min/Max gate to clear
        // EVERYTHING so the route can resume sneaking.
        bool seeHiddenOverride = false;
        if (!_isEnabled())
        {
            if (_seeHiddenClearActive?.Invoke() == true)
            {
                seeHiddenOverride = true;
            }
            else
            {
                _currentTarget = null;
                return;
            }
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
            if (e.MonsterNumber is not int n) continue;

            MonsterOverlay overlay = ResolveOverlay(n);
            if ((overlay.Relationship ?? MonsterRelationship.Enemy) != MonsterRelationship.Enemy)
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

        if (engageable.Count == 0)
        {
            if (_currentTarget is not null)
            {
                // Dump the observation's entity breakdown so the user
                // can see WHY we think the room is empty — Unknown
                // (classifier doesn't recognise the name), MonsterNumber
                // null (record has no Monsters-table link), Relationship
                // not-Enemy (friendly NPC), or genuinely an empty list.
                // The "wasted re-attack mid-combat" symptom usually
                // means one of the first three caused a spurious empty
                // observation that null-ed the target between rounds.
                int total = obs.Entities.Count;
                int unknownCount = 0;
                int noNumberCount = 0;
                int friendlyCount = 0;
                foreach (RoomEntity e in obs.Entities)
                {
                    if (e.Kind != EntityKind.Monster) unknownCount++;
                    else if (e.MonsterNumber is null) noNumberCount++;
                    else
                    {
                        MonsterOverlay ov = ResolveOverlay(e.MonsterNumber.Value);
                        if ((ov.Relationship ?? MonsterRelationship.Enemy) != MonsterRelationship.Enemy)
                            friendlyCount++;
                    }
                }
                _log?.Combat(LogCategory,
                    $"room cleared — was=target={_currentTarget} " +
                    $"source={obs.Source} " +
                    $"obs-entities={total} (unknown={unknownCount} " +
                    $"no-monster-number={noNumberCount} friendly={friendlyCount})");
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
        // WHOLE room regardless of count so re-sneak is possible.
        if (!seeHiddenOverride)
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
        // list, keep going — the server is still swinging at it.
        if (_currentTarget is { } current &&
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
            if (UnengageableReason(settings, cand.MonsterNumber) is { } reason)
            {
                _log?.Combat(LogCategory,
                    $"skip un-actionable {cand.RawName} (#{cand.MonsterNumber}) — {reason}");
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
        ClearBackstabResolution();

        // Reset the combat-spell room economy — per-room debuff-once /
        // cast-cap / multi-attack counters + the damage-immunity map all
        // start fresh next room.
        _castingSpellTarget = null;
        _lastCastAction = null;
        _attackSpellImmuneSpecies.Clear();
        _spellChooser.ResetForNewRoom();

        // Revert an alt-weapon swap so the next fight opens on the normal
        // weapon — but skip it when backstab is active, since the pre-move
        // sequence re-gears to the backstab weapon before the next approach and
        // a normal-then-backstab double swap would be wasted commands.
        bool backstabActive = settings.DoBackstab
            && !string.IsNullOrWhiteSpace(settings.BackstabWeapon);
        if (!backstabActive && _usingAlternateWeapon)
            _swapWeapon?.Invoke(settings.NormalWeapon, settings.NormalOffHand);
        _usingAlternateWeapon = false;
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
        _swapWeapon?.Invoke(settings.BackstabWeapon, settings.BackstabOffHand);
        _prepBackstabArmor?.Invoke();
        _usingAlternateWeapon = false;
    }

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
    private void EquipForAttack(CombatSettings settings, bool wantAlternate)
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
        _swapWeapon?.Invoke(weapon, offHand);
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
            // Both weapons are out against this species — the weapon path is
            // exhausted. Remember the alternate failure (mirrors the normal
            // fail-set) and, when a caster is wired, re-run the round so a
            // Physical-first build falls back to the attack-spell cascade instead
            // of swinging uselessly. Guard the re-dispatch on a first-time Add so a
            // spell that also can't fire can't spin us in a swing → no-effect →
            // re-dispatch loop; the server's auto-repeat then just logs quietly.
            if (_alternateWeaponFailedMonsters.Add(species))
            {
                _log?.Combat(LogCategory, $"adding {species} to alternate-weapon fail-set");
                if (TryFallBackToSpellAfterWeaponFail(settings)) return;
            }
            _log?.Warn(LogCategory,
                $"weapon-no-effect on ALT against {species} — weapon path exhausted");
            return;
        }

        if (_normalWeaponFailedMonsters.Add(species))
            _log?.Combat(LogCategory, $"adding {species} to normal-weapon fail-set");

        // Swap NOW and re-send the attack so we don't waste a round.
        EquipForAttack(settings, wantAlternate: true);
        if (_currentTarget is { } tgt)
            SendAttack(settings.AlternateAttackCommand, tgt, priority: null);
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
    // *player* ("... upon Fujin"), and following onto that name would swing us at a
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

        DateTimeOffset now = DateTimeOffset.Now;
        if (now - _lastRoomRefreshAt < RoomRefreshCooldown) return;
        _lastRoomRefreshAt = now;

        _log?.Combat(LogCategory,
            "combat-line while room appears empty — sending CR for short re-display");
        _wireSender(Encoding.Latin1.GetBytes("\r"));
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

        _log?.Combat(LogCategory,
            $"target-not-here — dropping target={_currentTarget} + refreshing room");
        _currentTarget = null;
        // The server says the named target isn't here — a guarded priority we were
        // chasing is genuinely gone, so end the redirect chase (breaks the retry
        // loop if a guard-retry "aa <priority>" was what drew this line).
        _guardBlockedTarget = null;
        ClearBackstabResolution();

        // Force a refresh (debounce shared with OnCombatLine so a
        // simultaneous miss-line + target-not-here doesn't double-send).
        // Bare CR — same rationale as OnCombatLine.
        DateTimeOffset now = DateTimeOffset.Now;
        if (now - _lastRoomRefreshAt < RoomRefreshCooldown) return;
        _lastRoomRefreshAt = now;
        _wireSender(Encoding.Latin1.GetBytes("\r"));
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

        _log?.Combat(LogCategory,
            $"command-no-effect — dropping target={_currentTarget} + refreshing room");
        _currentTarget = null;
        // A no-effect swing means the named target isn't hittable now — end any
        // guard-redirect chase rather than re-firing at a target that won't resolve.
        _guardBlockedTarget = null;
        ClearBackstabResolution();

        DateTimeOffset now = DateTimeOffset.Now;
        if (now - _lastRoomRefreshAt < RoomRefreshCooldown) return;
        _lastRoomRefreshAt = now;
        _wireSender(Encoding.Latin1.GetBytes("\r"));
    }

    // A specific death-line matched, but the classifier couldn't attribute it to
    // any monster in the current room view (shared / suffix-flavored wordings:
    // the death line resolved to "spectre" while the roster holds "shadow
    // spectre", so RemoveDeadEntity found nothing to drop). The roster is now
    // stale — a mob is gone but our list still shows it. Without a nudge, combat
    // only learns the room emptied on the NEXT tick, when its swing at the ghost
    // target no-ops through OnCommandNoEffect — a ~5s stall before the walker can
    // resume. Force the same debounced room re-display those paths use so the
    // server hands us the true roster now: if the room's empty the Combat gate
    // clears immediately; if a survivor remains we re-pick it a beat sooner.
    public void NoteUnattributedDeath()
    {
        if (!_isEnabled()) return;
        if (_wireSender is null) return;
        if (_currentTarget is null) return;

        DateTimeOffset now = DateTimeOffset.Now;
        if (now - _lastRoomRefreshAt < RoomRefreshCooldown) return;
        _lastRoomRefreshAt = now;
        _log?.Combat(LogCategory,
            "unattributed death — forcing room re-display to resync roster");
        _wireSender(Encoding.Latin1.GetBytes("\r"));
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
        _currentTarget = null;     // force a fresh pick + equip

        // Bypass the follow-announce hold on this re-pick. We only get here mid-
        // fight (an interrupt turned OUR auto-attack off while already engaged),
        // so the party has already converged and the followed leader is swinging
        // at a mob it won't re-announce ("already engaged; no re-fire"). Leaving
        // ShouldWaitForFollow armed would park us awaiting an announce that never
        // comes, and OnCombatTick's fallback would only rescue us a full round
        // later (the reported "re-attack missed a combat round" after a between-
        // round heal). Picking our own target now lands on the same mob that
        // fallback would have chosen, minus the wasted round.
        _followDeferBypass = true;
        try { OnEntitiesObserved(live); }
        finally { _followDeferBypass = false; }
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
    // resume the weapon attack promptly (see _betweenRoundCastAt).
    public void NoteBetweenRoundCast() => _betweenRoundCastAt = DateTimeOffset.Now;

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

            // If this Off is the server's response to a between-round cast
            // we just fired (self-heal / cure / buff), re-issue the weapon
            // attack now rather than waiting for the mob's next swing or the
            // next combat tick — the "used a heal mid-fight, then stood idle
            // a full round" symptom. Attributed strictly to our own cast
            // (armed by NoteBetweenRoundCast) so a non-sustaining attack's
            // per-strike Off (KAI pummel) is never misread. Weapon mode only;
            // a live target must still be present; TryResumeEngage's pacing
            // is the backstop against any double-fire with the tick resume.
            if (DateTimeOffset.Now - _betweenRoundCastAt < CastInterruptResumeWindow
                && _castingSpellTarget is null
                && _currentTarget is not null
                && _classifier.Current is { } live
                && HasEngageable(live))
            {
                TryResumeEngage(live, bypassAttackGuard: true);
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

    private bool HasEngageable(RoomEntitiesObservation obs)
    {
        for (int i = 0; i < obs.Entities.Count; i++)
        {
            RoomEntity e = obs.Entities[i];
            if (e.Kind != EntityKind.Monster) continue;
            if (e.MonsterNumber is not int n) return true; // unknown → assume engageable
            MonsterOverlay overlay = ResolveOverlay(n);
            if ((overlay.Relationship ?? MonsterRelationship.Enemy) == MonsterRelationship.Enemy)
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
        _wireSender(Encoding.Latin1.GetBytes(line + "\r"));
        NoteAttackSent();
    }

    private void SendAttack(string command, string target, bool refire, string refireReason)
    {
        _combatOff = false;
        _castingSpellTarget = null;
        _lastCastAction = null;
        string verb = string.IsNullOrWhiteSpace(command) ? "a" : command.Trim();
        string line = $"{verb} {target}";
        _log?.Combat(LogCategory,
            $"re-fire target={target} cmd={verb} timing={refireReason}");
        _lastAttackCommand = line;
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(line + "\r"));
        NoteAttackSent();
    }

    // Confusion-fumble recovery, driven by ConditionTracker.ActionFailed (wired in
    // AppServices). MajorMUD confusion does not block attacking — each command you
    // send can fumble ("You fumble in confusion!"), consumed without executing, so
    // the server never engages and the target sits unattacked (the reported
    // "monsters present but not attacked unless I manually re-send" symptom). We
    // re-send the last weapon swing verbatim; it fires once per fumble line, so the
    // server's own fumble echoes pace the retries, and once a swing lands the server
    // auto-repeats and the fumbles stop. Weapon mode only — spell mode re-issues its
    // cast on the per-round tick (OnCombatTick), and _lastAttackCommand holds a
    // weapon verb we must not fire into a spell fight.
    public void OnActionFailed()
    {
        if (_disposed || !_isEnabled()) return;
        if (_castingSpellTarget is not null) return;
        if (_currentTarget is null) return;
        if (_lastAttackCommand is not { Length: > 0 } line) return;
        if (_wireSender is null) return;

        _combatOff = false;
        _log?.Combat(LogCategory, $"fumble — re-sending last attack '{line}'");
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

        if (_wireSender is null) return;
        DateTimeOffset now = DateTimeOffset.Now;
        if (now - _lastRoomRefreshAt < RoomRefreshCooldown) return;
        _lastRoomRefreshAt = now;
        _wireSender(Encoding.Latin1.GetBytes("\r"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
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
        _monsterProtectSub.Dispose();
        _bsResolveHitsSub.Dispose();
        _bsResolveMissesSub.Dispose();
    }

    private readonly record struct EngageableCandidate(
        string RawName,
        string ResolvedName,
        int MonsterNumber,
        MonsterAttackPriority Priority,
        int AppearanceIndex,
        bool DontBackstab);
}
