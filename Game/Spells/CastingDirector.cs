using System.Collections.Specialized;
using System.ComponentModel;
using MudPlay.Game.Health;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Spells;

// Unified spell-decision engine. Subscribes to PlayerState +
// TickEngine.CombatTickElapsed, reads the ConditionTracker's active flags, and
// routes the chosen cast through CastCoordinator.
//
// One unified priority list lifted from SpellsSettings's PriorityXxxx slots. Lower
// number = higher precedence. Default order is the MegaMUD-parity shape (Minor
// party heal → Major party heal → Minor self heal → Major self heal → Curing →
// Buffing → Debuffing); the user is free to re-order any of the seven via the
// Spells settings tab.
//
// Per-category meaning:
//   - Minor / Major party heal — single-target party heal when a member is below
//     threshold, group AOE party heal when multiple are.
//   - Minor / Major self heal — MinorHealSpell / MajorHealSpell against the local
//     player. Thresholds: MinorHealCombatTrigger / MajorHealCombatTrigger while in
//     combat; HealRestTrigger during rest. When an HP-regen HoT (HpRegenSpell) is
//     configured, the minor path casts it FIRST — ahead of the single-target heal —
//     whenever HP trips the minor trigger while still above the major (life-threat)
//     trigger and the HoT isn't already ticking on us; a running HoT falls through
//     to the instant single-target heal.
//   - Curing — remove an active ailment. The actual ailment state comes from
//     ConditionTracker (game-data Messages tab owns the patterns). Per-ailment cure
//     spells are CureHoldsSpell etc. Internal order inside the Curing slot:
//     movement-prevented → poison → disease → blindness.
//   - Buffing — recast player buffs (Bless1–10 slots).
//   - Debuffing — an in-between action sourced from the combat engine. The DECISION
//     (config + once-per-room / once-per-target gating) is owned by CombatManager /
//     CombatSpellChooser; this director only casts the debuff through the shared
//     in-between window (wired via SetCombatDebuffSource) so it competes against the
//     survival casts above by the user's PriorityDebuffing rank. No-op until wired.
//
// Every evaluation walks the priority list and picks the first candidate that's
// actually ready to fire. The CastCoordinator's recent-cast cooldown handles "one
// cast per round" naturally — if we evaluate mid-round the cooldown blocks; on the
// next tick it clears and the highest-priority candidate gets through.
//
// Master enable flag is AutoActionDefaults.AutoHealRest — shared with HealthManager
// so the user has one toggle covering both passive rest + active heal-spell. When
// the spell pickers on the Spells tab are empty, the engine no-ops without further
// checks.
public sealed class CastingDirector : IDisposable
{
    // LogService category — appears as [CastDirector] rows per evaluation +
    // decision.
    public const string LogCategory = "CastDirector";

    // Default recast lead when a bless slot doesn't carry its own: re-cast a buff
    // once it's within this many seconds of expiry (or already expired / never
    // confirmed). Each self / party bless slot can override this via its per-slot
    // "recast within" picker (0 = wait for actual expiry); the chosen lead travels
    // with the buff's active timer in _activeUntil.
    private const int DefaultRecastMarginSec = SpellsSettings.DefaultBlessRecastMarginSec;

    // Optimistic self-buff recast clock used the instant a self-buff cast is
    // SENT — before its AppliedMessage confirms — so a second evaluation on the
    // same round can't re-issue the buff while the first is still in flight. When
    // the buff's real effect duration is resolvable it's used; otherwise this
    // conservative fallback holds the recast until the AppliedMessage lands with
    // the true duration.
    private const int UnknownBuffRecastFallbackSec = 60;

    // Self-heal duplicate-suppression window. OnCombatTick wipes the
    // CastCoordinator's one-cast-per-round cooldown, so a second Evaluate can fire
    // on STALE pool data (the server hasn't reflected the first heal yet) and
    // re-issue the identical heal. Unlike buffs, heals carry no recast timer, so
    // this guard suppresses a byte-for-byte repeat (same spell, unchanged HP + MA)
    // within this window. Once the pool moves — the heal landed, or damage came in
    // — the guard no longer matches and a fresh heal is free to fire.
    private static readonly TimeSpan SameSelfHealStaleGuard = TimeSpan.FromSeconds(8);

    // How long after arming a self-buff's pending marker an AlreadyCastThisRound /
    // fizzle rejection can still plausibly be about that send. Our recast draws its
    // rejection within the same ~5-6s round; a later one is an unrelated cast the
    // spell-less server line got misattributed. One generous round (report -130111).
    private static readonly TimeSpan PendingSelfBuffRejectionWindow = TimeSpan.FromSeconds(7);

    private readonly PlayerState _state;
    private readonly CastCoordinator _cast;
    private readonly Conditions.ConditionTracker? _conditions;
    private readonly PartyState? _party;
    // Rest-target pool ceilings for the self-heal triggers — the DEFAULT gear set's
    // max HP + the current gear's real (stat-screen) max — so a heal anchors to the
    // loadout the user tuned (like the rest gates), not a Pre-rest set's altered pool.
    // Null until wired → the heal falls back to the live _state.MaxHp.
    private Func<int>? _restDefaultMaxHp;
    private Func<int>? _restRealMaxHp;
    private Func<bool>? _isStealthedFunc;
    private Func<bool>? _inputCaptured;
    private Func<bool>? _buffStripRoom;
    private Func<(string Spell, string? Target)?>? _combatDebuffSource;
    private Action? _combatDebuffCommit;
    private Func<string, int?>? _manaCostLookup;
    private Func<bool>? _autoBlessEnabled;
    private Func<bool>? _attackOwed;
    private Func<bool>? _isTriggeredRest;
    // True while a MANA-recovery rest is in progress — the mana-rest lock. Asserted
    // when mana drops below its rest trigger and held (through a combat interruption)
    // until mana tops back up to the rest-max target. Gates the "cast before resting
    // for mana" slot: unlike _isTriggeredRest (any recovery rest, drops on combat)
    // this is mana-specific and combat-durable, matching what that flag means. Left
    // unwired, the pre-rest slot falls back to never-eligible (fails closed).
    private Func<bool>? _isManaRestActive;
    // True while the telnet link is up. Null (unwired, tests) = treat as connected.
    // Gates the whole between-round loop: while disconnected the wire-send is a no-op
    // but TryCast still returns true and arms the recast timer, so an ungated loop
    // (now heartbeat-driven every 1s) would "cast" phantom buffs into a dead socket.
    private Func<bool>? _isConnected;
    // Reports whether the combat tick currently firing OnCombatTick was driven by a
    // server combat line (TickEngine.RecordCombatTick) rather than the 5 s timer
    // fallback. A damage-line-driven tick fires DURING the round's line burst, before
    // the round's prompt has refreshed HP — so _state.Hp still holds the previous
    // round's value. Null (unwired, tests) = treat every tick as fresh.
    private Func<bool>? _combatTickDamageDriven;
    private Func<string, long?>? _itemCastDuration;
    private Func<string, bool>? _executeItemCast;
    private Func<string, int?>? _itemCastManaCost;
    private readonly Func<SpellsSettings> _readSpells;
    private readonly Func<HealthSettings> _readHealth;
    private readonly Func<PartySettings>? _readPartySettings;
    private readonly Func<bool> _isEnabled;
    private readonly LogService? _log;

    // Given names of aided-but-still-off-roster downed allies the rescue engine
    // (AllyDroppedHandler) wants topped up by name. A dropped ally leaves `par`
    // so PickPartyHeal's Members walk can't see them; this feeds them back in as
    // the highest-priority heal target. Null / empty until wired.
    private Func<IReadOnlyList<string>>? _downedAllies;

    // ----- Buff-duration tracking (self + party) ----------------------
    // Per (targetKey, spellShort) → the buff's wear-off instant plus the slot's
    // recast lead (seconds before Until we re-cast). targetKey "" = self;
    // otherwise the member's given name lower-cased. The lead travels with the
    // timer so IsRecastDue and the "recast in Xs" logs use the slot's own value.
    private readonly Dictionary<(string Target, string Short), (DateTime Until, int MarginSec, int TotalSec)> _activeUntil = new();
    // The one outstanding party-buff cast awaiting CasterMessage
    // confirmation. CastCoordinator's cooldown guarantees ≤1 in flight.
    private (string Short, string Target, long DurationSec, int MarginSec, CasterMessageMatcher Matcher)? _pendingPartyCast;
    // A HAND-TYPED single-target buff cast (`gbls fuj`) awaiting its success line. We
    // know only the shorthand the user typed after the code; the caster line names the
    // resolved target in full, which we prefix-match against the shorthand to arm that
    // member's timer. Engine casts arm _pendingPartyCast instead (ArmPartyBuffConfirm
    // clears this), so only a genuine hand-cast leaves one of these armed.
    private (string Short, string Prefix, long DurationSec, int MarginSec, CasterMessageMatcher Matcher)? _pendingManualCast;
    // The self-buff whose optimistic recast timer was armed on send but hasn't yet
    // been confirmed landed. Cleared when its AppliedMessage confirms (the real
    // duration timer takes over) OR when a server landing-failure arrives — a fizzle
    // / interrupt / no-mana means the buff never landed, so the phantom timer must be
    // dropped or the buff sits "active" for its whole assumed duration and never
    // re-attempts (an ~90s uptime hole after a single fizzle).
    private string? _pendingSelfBuffShort;

    // When _pendingSelfBuffShort was armed. A between-round cast we just sent draws
    // its AlreadyCastThisRound rejection within the same round, so a rejection
    // arriving much later cannot be about that send — it's an unrelated cast the
    // server's spell-less rejection line got misattributed to this buff. Guards the
    // timer-drop in OnCastFailed against a stale marker (report -130111).
    private DateTime _pendingSelfBuffArmedAt;

    // True once we've cast a between-round spell (heal / cure / buff / debuff /
    // item) THIS combat round. The game allows only ONE 0-energy between-round cast
    // per round across all of them — a second draws "You have already cast a spell
    // this round!" and does NOT fire. So while this is set we suppress further
    // between-round casts (in combat) rather than send doomed ones. Cleared on the
    // combat ROUND TICK (NotifyRoundComplete, wired to TickEngine.CombatTickElapsed —
    // NOT *Combat Off*, which fires per kill and would re-open the slot mid-round in a
    // multi-mob fight). Never consulted out of combat, where no per-round cap applies.
    private bool _betweenRoundSlotUsed;

    // True only for the duration of a single Evaluate driven by a damage-line combat
    // tick (set in OnCombatTick, cleared in its finally). While set, the non-heal
    // survival categories (cure / buff / debuff) are skipped: HP is unconfirmed for
    // this round (the prompt hasn't landed), so spending the round's one between-round
    // slot on a non-heal could pre-empt a life-threat heal that becomes due the instant
    // the prompt arrives — report paradigm-20260904-214056, where an armour buff fired
    // on a stale HP=254 read while the player was actually at 117 and died two rounds
    // later. Heals stay eligible (safe on the stale read; the prompt's reactive Evaluate
    // fires the real one). Always false for reactive / idle / timer-fallback passes.
    private bool _hpUnconfirmedThisPass;

    // A mana-regen roll-spell reroll the reroller staged (last roll below its
    // threshold). It's offered by PickSelfBuff at PriorityBuffing and cast through
    // the normal between-round pass — so it competes with a due heal/cure and
    // spends the one-per-round slot, instead of firing on the raw wire — then
    // cleared once it goes out. Null when no reroll is pending.
    private string? _pendingManaRegenReroll;

    // Set when the live buff timers are frozen on an unexpected drop (carrier lost /
    // keep-alive timeout). While set, a reconnect shifts every Until forward by the
    // offline gap so each buff keeps the remaining it had at the drop instead of the
    // clock counting down (server-side link-death holds the buffs). null = running.
    private DateTime? _pausedAt;

    private Func<string, (string Caster, long DurationSec)?>? _buffInfoByShort;
    private Func<MessageRecord, string?>? _shortFromAppliedRecord;
    private Func<string, bool>? _isPartyWideBuff;
    // The character's party-buff plan (Party window). Null / no reader ⇒ no party
    // buffs. Read live each pass so an edit in the Party window takes effect at once.
    private Func<Models.Profile.BuffSettings?>? _readPartyBuffs;
    // "Is this member (given name) listed in the room's 'Also here:'?" — used ONLY to
    // clear a hidden-target back-off when the member reappears. Party membership already
    // guarantees same-room (if 'par' lists them they're here), so this is NOT a
    // pre-emptive cast gate — a present-but-not-listed member is just hiding. Null ⇒
    // no room list (tests / before wiring), so a hidden member stays backed off until
    // we move.
    private Func<string, bool>? _isMemberInRoom;

    // Given names (lower-cased) of party members a single-target buff couldn't reach
    // because they're HIDING — the server answered "You do not see <name> here!" to
    // our cast. We back off casting on them (no spam) until we MOVE (NoteRoomChanged
    // clears all) or they reappear in "Also here:" (cleared in PickPartyBuff). The
    // Buff Watchdog reads this to show "hidden — couldn't target".
    private readonly HashSet<string> _hiddenTargets = new(StringComparer.OrdinalIgnoreCase);

    // Given names (lower-cased) of members currently backed off as hidden — read by
    // the Buff Watchdog.
    public IReadOnlyCollection<string> HiddenPartyTargets => _hiddenTargets;

    // A room change (we moved) — retry every hidden target, in case they're no longer
    // hidden / no longer in the new room's occupancy.
    public void NoteRoomChanged()
    {
        if (_hiddenTargets.Count == 0) return;
        _hiddenTargets.Clear();
        _log?.Combat(LogCategory, "moved rooms — cleared hidden party-buff back-offs, will retry.");
    }
    // Self-buff cast code → the party-wide party buff that removes (supersedes) it while
    // in a party. PickSelfBuff skips a covered slot; the Buff Watchdog labels it.
    private Func<IReadOnlyDictionary<string, string>>? _selfBuffCoverage;
    private Action<string>? _selfBuffCastSink;
    private Func<DateTime> _now = () => DateTime.UtcNow;
    private LineExtractor? _lines;

    // Last self-heal we sent — spell code, the HP + MA it was sent at, and when.
    // Feeds SameSelfHealStaleGuard so a stale re-evaluation can't double-cast it.
    private (string Spell, int Hp, int Ma, DateTime At)? _lastSelfHealCast;

    private bool _disposed;

    public CastingDirector(
        PlayerState state,
        CastCoordinator cast,
        Func<SpellsSettings> readSpells,
        Func<HealthSettings> readHealth,
        Func<bool> isEnabled,
        LogService? log = null)
        : this(state, cast, conditions: null, party: null,
               readSpells, readHealth, readPartySettings: null,
               isEnabled, log) { }

    // Constructor with optional ConditionTracker (for ailment cures) and PartyState
    // + PartySettings reader (for party-cast). Pass null for tests / engines that
    // don't need the dependencies; the matching Pick* methods short-circuit.
    public CastingDirector(
        PlayerState state,
        CastCoordinator cast,
        Conditions.ConditionTracker? conditions,
        PartyState? party,
        Func<SpellsSettings> readSpells,
        Func<HealthSettings> readHealth,
        Func<PartySettings>? readPartySettings,
        Func<bool> isEnabled,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(cast);
        ArgumentNullException.ThrowIfNull(readSpells);
        ArgumentNullException.ThrowIfNull(readHealth);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _state = state;
        _cast = cast;
        _conditions = conditions;
        _party = party;
        _readSpells = readSpells;
        _readHealth = readHealth;
        _readPartySettings = readPartySettings;
        _isEnabled = isEnabled;
        _log = log;

        _state.PropertyChanged += OnStateChanged;
        // React to a PARTY MEMBER's HP dropping, not just our own state / the combat
        // tick. The party-heal picker reads each member's HpPercent, but that value
        // is refreshed by the `par` poll on its own cadence — without watching it, a
        // member falling below the heal threshold wasn't acted on until the next
        // self-state change or round tick (report paradigm-20260820-122341: heal fired
        // a full round late). Read-only on party state — no single-writer concern,
        // same watch PartyVitalsWatcher already does.
        if (_party is not null)
        {
            _party.Members.CollectionChanged += OnPartyMembersChanged;
            foreach (PartyMember m in _party.Members) WatchMember(m);
        }
        _cast.CastFailed += OnCastFailed;
        if (_conditions is not null)
        {
            _conditions.ConditionApplied += OnConditionApplied;
            _conditions.ConditionEnded += OnConditionEnded;
        }
    }

    // Old 3-arg + 4-arg ctors kept as a convenience overload so the
    // existing AppServices wiring + tests don't churn while
    // party-cast wiring lands.
    public CastingDirector(
        PlayerState state,
        CastCoordinator cast,
        Conditions.ConditionTracker? conditions,
        Func<SpellsSettings> readSpells,
        Func<HealthSettings> readHealth,
        Func<bool> isEnabled,
        LogService? log = null)
        : this(state, cast, conditions, party: null,
               readSpells, readHealth, readPartySettings: null,
               isEnabled, log) { }

    // Wire the "is this combat tick damage-line-driven?" probe (TickEngine). While it
    // reads true, OnCombatTick's pass treats _state.Hp as unconfirmed for the round and
    // holds the non-heal categories. Optional — unset means every tick is treated as
    // HP-fresh (the pre-guard behaviour).
    public void SetCombatTickSource(Func<bool> isDamageDriven) =>
        _combatTickDamageDriven = isDamageDriven;

    // Hook to TickEngine.CombatTickElapsed — drives between-round evaluations. A tick
    // fired straight off a server combat line runs before the round's prompt refreshes
    // HP, so flag the pass as HP-unconfirmed and let RunDecisionPass hold the non-heal
    // categories until a fresh-HP pass (the imminent prompt's reactive Evaluate).
    public void OnCombatTick()
    {
        _hpUnconfirmedThisPass = _combatTickDamageDriven?.Invoke() ?? false;
        try { Evaluate(); }
        finally { _hpUnconfirmedThisPass = false; }
    }

    // Hook to TickEngine.HeartbeatElapsed (1 s) — drives the SAME between-round
    // decision loop while OUT of combat. The combat tick only free-runs once a combat
    // line has anchored it, so idle buffing/curing would otherwise fire only on sparse
    // incidental events (~30 s apart at login). Off the 1 s heartbeat the loop drains
    // one cast whenever the CastCoordinator's ~5 s cast cooldown clears, so a login's
    // buffs queue up one-per-cooldown in priority order instead of trickling in. In
    // combat the combat tick owns the cadence, so skip here to avoid double-evaluating
    // a round (and to leave the in-combat between-round economy untouched).
    public void OnIdleHeartbeat()
    {
        if (_state.InCombat) return;
        Evaluate();
    }

    // Raised the instant a between-round cast (self-heal / cure / buff / debuff) is
    // sent to the server. The combat engine listens so it can attribute the *Combat
    // Off* the server fires in response to THIS cast — and re-issue the weapon
    // attack the moment that line arrives, instead of waiting a full round. Without
    // this signal a bare *Combat Off* is ambiguous (a non-sustaining attack like KAI
    // pummel emits one after every strike), so the engine can't safely resume on it
    // alone.
    public event Action? CastFired;

    // Wire a stealth-state predicate so the Buff slot can skip candidate casts that
    // would break stealth. Typically pointed at StealthManager.IsStealthed. Optional
    // — when unset the buff slot fires regardless of stealth.
    public void SetStealthGate(Func<bool> isStealthed) =>
        _isStealthedFunc = isStealthed;

    // Wire the self-heal rest-target ceilings (DEFAULT-set max HP + current gear's
    // real max) so heal triggers anchor to the Default set like the rest gates.
    public void SetRestPoolMaxHp(Func<int>? defaultMaxHp, Func<int>? realMaxHp)
    {
        _restDefaultMaxHp = defaultMaxHp;
        _restRealMaxHp = realMaxHp;
    }

    // Resolve a self-heal HP trigger against the Default-set basis + real-max cap.
    private int ResolveHealHpTrigger(ThresholdMode mode, int pct)
        => RestThresholds.ResolveValue(mode, pct,
            _restDefaultMaxHp?.Invoke() ?? 0, _restRealMaxHp?.Invoke() ?? 0, _state.MaxHp);

    // Wire a "the keyboard is captured by a full-screen menu" predicate. While
    // it returns true, Evaluate suppresses EVERY cast — not just buffs. When the
    // `train stats` stat box is up, character-mode input sends keystrokes raw to
    // the wire, so any automated cast text (bless, heal, cure) lands its letters
    // in the character-creation form (the "bles" family-name corruption). No cast
    // is safe to issue in that window. Optional — unset fails open (never gates).
    public void SetInputCaptureGate(Func<bool> isInputCaptured) =>
        _inputCaptured = isInputCaptured;

    // Wire the downed-ally rescue provider (AllyDroppedHandler). Each Evaluate
    // reads the current set of aided downed allies and heals the first one by
    // name at top priority. Optional — unset means no downed-ally heals.
    public void SetDownedAllyProvider(Func<IReadOnlyList<string>> provider) =>
        _downedAllies = provider;

    // Wire the combat engine's in-between debuff bridge. A debuff is an in-between
    // action (<=1/round) in the realm's round model, but the DECISION — config +
    // once-per-room / once-per-target gating — lives in CombatManager. This director
    // just rides the shared in-between window so the debuff competes against
    // survival casts by the user's PriorityDebuffing rank (default lowest, so heals
    // win). source answers "is there a debuff to fire?" (spell code + target; null
    // target => area/multi); commit is invoked only after the coordinator confirms
    // the cast, advancing the combat engine's per-room bookkeeping. Optional — until
    // wired the Debuffing slot is a no-op.
    public void SetCombatDebuffSource(Func<(string Spell, string? Target)?> source, Action commit)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(commit);
        _combatDebuffSource = source;
        _combatDebuffCommit = commit;
    }

    // Wire a cast-code → required-mana resolver (typically SpellbookState.ManaCostOf)
    // so the survival categories (heals / cures / buffs / party heals) skip any cast
    // the player can't pay for. Returns the spell's per-round mana cost, or null when
    // unknown (no spellbook / unrecognised code) — an unknown cost never blocks, so
    // the engine behaves exactly as before until a real cost is resolvable. Combat
    // (offensive / debuff) casts are NOT gated here: the Combat settings tab owns
    // their mana threshold via CombatSpellSlot.MinManaPerCast. Optional — until
    // wired, no affordability check runs.
    public void SetManaCostLookup(Func<string, int?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        _manaCostLookup = lookup;
    }

    // Wire the Auto-Bless auto-engine gate. When the predicate returns false, the
    // Buffing category is suppressed entirely (no Bless / regen / when-full buff
    // fires). Until called, buffs fail open (always allowed).
    public void SetAutoBlessGate(Func<bool> isEnabled)
    {
        ArgumentNullException.ThrowIfNull(isEnabled);
        _autoBlessEnabled = isEnabled;
    }

    // Wire the connection state so the between-round loop pauses on a disconnect and
    // resumes when the link's back — the buff timers already freeze/resume across the
    // gap (PauseBuffTimers / ResumeBuffTimers), and this stops the loop from casting
    // (and re-arming timers on a no-op send) while offline. Until called, fails open.
    public void SetConnectedGate(Func<bool> isConnected)
    {
        ArgumentNullException.ThrowIfNull(isConnected);
        _isConnected = isConnected;
    }

    // Wire CombatManager.IsSpellAttackOwed. The game allows exactly one cast per
    // round; when a survival cast just spent a round that owed the combat engine
    // its attack-spell resume, EVERY category here must sit out entirely until
    // that attack goes back out — not just Buffing. Left unwired, this fails open
    // (never suppressed), matching every other gate's default.
    public void SetAttackOwedGate(Func<bool> isAttackOwed)
    {
        ArgumentNullException.ThrowIfNull(isAttackOwed);
        _attackOwed = isAttackOwed;
    }

    // Wire HealthManager.IsRecoveringRest. True only while an auto-rest recovery
    // is in flight — HP or MA fell below its rest-if-below trigger and we're
    // resting back up to rest-max. The bless "while resting" gate keys on THIS,
    // not the raw Resting position: blessing runs while idle / moving / idly
    // resting, and defers only to a triggered recovery rest unless the user opts
    // in. Left unwired, this fails open (no triggered rest → bless never held on
    // resting grounds).
    public void SetTriggeredRestGate(Func<bool> isTriggeredRest)
    {
        ArgumentNullException.ThrowIfNull(isTriggeredRest);
        _isTriggeredRest = isTriggeredRest;
    }

    // Wire the mana-rest-lock gate for "cast before resting for mana" slots (see
    // _isManaRestActive). True while a mana-recovery rest is active — held through a
    // combat interruption until mana reaches its rest-max target.
    public void SetManaRestGate(Func<bool> isManaRestActive)
    {
        ArgumentNullException.ThrowIfNull(isManaRestActive);
        _isManaRestActive = isManaRestActive;
    }

    // Wire the buff-strip-room gate. When the predicate returns true, the current
    // room's cast-on-enter spell strips buffs (RemovesSpell / DispellMagic), so the
    // Buffing category is suppressed — re-casting a buff the room immediately tears
    // off just burns mana. Heals / cures / debuffs are unaffected. Optional — until
    // wired, buffs fail open (never suppressed on room grounds).
    public void SetBuffStripRoomGate(Func<bool> isBuffStripRoom)
    {
        ArgumentNullException.ThrowIfNull(isBuffStripRoom);
        _buffStripRoom = isBuffStripRoom;
    }

    // Wire the item-cast buff bridge. A Bless slot may hold an ItemCastToken
    // (#<item name>) instead of a cast-code; when picked, the buff is produced by
    // equipping + using an item rather than a direct cast. durationOf resolves a
    // token to the cast spell's effect duration in seconds (the recast clock),
    // returning null for an unresolvable token or a non-buff spell (no duration) so
    // it's never fired; execute runs the equip → use → re-equip sequence and returns
    // whether the lines were sent. Optional — until wired, an item-cast Bless slot is
    // skipped.
    public void SetItemCastSource(Func<string, long?> durationOf, Func<string, bool> execute)
    {
        ArgumentNullException.ThrowIfNull(durationOf);
        ArgumentNullException.ThrowIfNull(execute);
        _itemCastDuration = durationOf;
        _executeItemCast = execute;
    }

    // Wire the item-cast mana-cost resolver. Maps a Bless-slot ItemCastToken to the
    // cast spell's Spells.ManaCost — the mana using the item draws. A free item-cast
    // (most charge wands / proc gear, cost 0) bypasses the buff mana-floor and
    // recasts regardless; a paid item-cast (e.g. a shimmering greatsword) is held
    // until we both clear the floor and can pay. Returns null for an unresolvable
    // token. Optional — until wired (or when it returns null), an item-cast buff is
    // treated as free and never mana-gated.
    public void SetItemCastManaCost(Func<string, int?> manaCostOf)
    {
        ArgumentNullException.ThrowIfNull(manaCostOf);
        _itemCastManaCost = manaCostOf;
    }

    // Wire the buff-duration sources used by the recast-window logic. buffInfoByShort
    // maps a buff's 4-letter cast code to its caster-confirmation template (the
    // game-data CasterMessage, e.g. You cast {s} on {s}!) plus its computed effect
    // duration in seconds (from SpellCalculator.Duration at the live character
    // level); returns null for an unknown / message-less code.
    // shortFromAppliedRecord maps a fired MessageRecord (from the ConditionTracker's
    // ConditionApplied / ConditionEnded) back to the buff cast code it represents, so
    // a self-cast confirmed via its AppliedMessage starts / clears its duration
    // timer. Optional — until wired, no duration tracking runs and the buff pickers
    // fall back to the always-eligible path.
    public void SetBuffDurationSources(
        Func<string, (string Caster, long DurationSec)?> buffInfoByShort,
        Func<MessageRecord, string?> shortFromAppliedRecord)
    {
        ArgumentNullException.ThrowIfNull(buffInfoByShort);
        ArgumentNullException.ThrowIfNull(shortFromAppliedRecord);
        _buffInfoByShort = buffInfoByShort;
        _shortFromAppliedRecord = shortFromAppliedRecord;
    }

    // Wire the party-buff plan source (CharacterProfile.PartyBuffs) so the party-buff
    // picker reads the user's configured slots. Optional — until wired, the picker
    // no-ops. Read live each pass so a Party-window edit takes effect immediately.
    public void SetPartyBuffSource(Func<Models.Profile.BuffSettings?> readPartyBuffs)
    {
        ArgumentNullException.ThrowIfNull(readPartyBuffs);
        _readPartyBuffs = readPartyBuffs;
    }

    // Wire the room-presence gate: "is this member (given name) currently in the
    // room with us?" A single-target party buff only fires for a member who is both
    // in the party AND in the room, so a saved target who left / was uninvited / is
    // elsewhere is skipped. Optional — until wired, presence is unknown and the gate
    // stands down (every selected member is treated as present).
    public void SetRoomPresenceCheck(Func<string, bool> isMemberInRoom)
    {
        ArgumentNullException.ThrowIfNull(isMemberInRoom);
        _isMemberInRoom = isMemberInRoom;
    }

    // Wire a "is this buff party-wide?" check (typically backed by the active set's
    // Spells.Targets scope code). A party-wide buff (Full / Divided Party Area)
    // blankets the whole party in a single cast, so the party-buff picker sends just
    // the cast code with no target rather than looping per member. Optional — until
    // wired, every party-bless slot is treated as single-target and cast per
    // class-matched member.
    public void SetPartyWideBuffCheck(Func<string, bool> isPartyWideBuff)
    {
        ArgumentNullException.ThrowIfNull(isPartyWideBuff);
        _isPartyWideBuff = isPartyWideBuff;
    }

    // Wire the self-buff coverage source: self-buff cast code → the party-wide party buff
    // that removes (supersedes) it while in a party. PickSelfBuff skips a covered slot so
    // we let the party buff cover us instead of self-casting the removed spell.
    public void SetSelfBuffCoverage(Func<IReadOnlyDictionary<string, string>> coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        _selfBuffCoverage = coverage;
    }

    // The current self-buff coverage map (self code → covering party-buff code) — the
    // Buff Watchdog reads this to label a superseded self-buff "covered by". Empty when
    // solo or unwired.
    public IReadOnlyDictionary<string, string> CurrentSelfBuffCoverage()
        => _selfBuffCoverage?.Invoke() ?? _emptyCoverage;

    private static readonly IReadOnlyDictionary<string, string> _emptyCoverage =
        new Dictionary<string, string>();

    // Wire a sink notified with the 4-letter cast code every time one of OUR
    // self-buffs is CAST (StartSelfBuffTimer, right after the cast reaches the wire).
    // The mana-regen reroll engine subscribes here to read the fresh roll off abil 145
    // after a code-145 roll spell (nature tap / mana flux) goes out; the sink owns the
    // spell / realm filtering. Deliberately keyed to the send, NOT the AppliedMessage
    // confirm: a roll spell confirms via the SHARED "mana regenerating" condition,
    // which the applied-line path can't map back to the specific spell (#406), so the
    // confirm never fires for it and a confirm-keyed reroll never ran
    // (paradigm-20260830-110918). The send is the reliable per-cast signal, and firing
    // the abil query after the cast (TryCast precedes StartSelfBuffTimer) reads the
    // post-cast value. Optional — until wired, casts only arm the recast timer.
    public void SetSelfBuffCastSink(Action<string> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _selfBuffCastSink = sink;
    }

    // Override the clock used for buff-expiry math. Test seam — production uses
    // DateTime.UtcNow.
    public void SetClock(Func<DateTime> now)
    {
        ArgumentNullException.ThrowIfNull(now);
        _now = now;
    }

    // Subscribe to server lines so OUR party-buff casts can be confirmed against the
    // buff's CasterMessage template (capturing the target name) before the duration
    // timer starts. Self-cast confirmation goes through the ConditionTracker
    // AppliedMessage path instead, so this is only consulted while a party cast is
    // pending.
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // Clear all buff-duration state (active timers + any pending party-cast
    // confirmation). Call alongside ConditionTracker.ClearAll on disconnect / death
    // so stale durations don't suppress a fresh-session recast.
    public void ResetBuffTracking()
    {
        _activeUntil.Clear();
        _hiddenTargets.Clear();
        _pendingPartyCast = null;
        _pendingManualCast = null;
        _pendingSelfBuffShort = null;
        _pendingManaRegenReroll = null;
        _lastSelfHealCast = null;
        _betweenRoundSlotUsed = false;
        _pausedAt = null;
    }

    // OUR OWN death wiped OUR buffs — drop the self timers (keyed "") + any pending self
    // cast. Party members are unaffected (they stayed alive, still buffed), so THEIR
    // timers are kept: we shouldn't re-bless a party member just because we died. (A
    // full character swap still clears everything via ProfileLoaded → ResetBuffTracking.)
    public void ClearSelfBuffTracking()
    {
        int removed = RemoveTimersFor("");
        _pendingSelfBuffShort = null;
        _pendingManaRegenReroll = null;
        _lastSelfHealCast = null;
        _pendingPartyCast = null;   // a cast in flight when we died never landed
        _pendingManualCast = null;
        _log?.Info(LogCategory,
            $"self died — cleared {removed} self-buff timer(s); party-buff timers kept (those members are alive).");
    }

    // A party member died — death wipes EVERY buff on them, so drop each timer we hold on
    // that member + any hidden back-off for them. No-op for a name we hold no timer for
    // (the "<Name> has died." line also fires for mobs / randos).
    public void ClearMemberBuffTimers(string givenName)
    {
        if (string.IsNullOrWhiteSpace(givenName)) return;
        string key = GivenName(givenName).ToLowerInvariant();
        int removed = RemoveTimersFor(key);
        _hiddenTargets.Remove(key);
        if (removed > 0)
            _log?.Info(LogCategory,
                $"party member {key} died — cleared {removed} buff timer(s) on them (death wipes buffs).");
    }

    // Manually drop the timer for one (target, cast-code) — the user clicking the clear
    // (✕) on a Buff Watchdog row. target "" is a self / whole-party buff; a given name is
    // a single-target member. The next evaluation recasts it if it's still a configured,
    // due buff; a phantom timer (e.g. an ex-member's) simply disappears. Case-insensitive
    // so the row's stored key always matches.
    public void ClearBuffTimer(string? target, string? shortCode)
    {
        string t = (target ?? string.Empty).Trim();
        string s = (shortCode ?? string.Empty).Trim();
        (string Target, string Short)? doomed = null;
        foreach ((string Target, string Short) key in _activeUntil.Keys)
            if (string.Equals(key.Target, t, StringComparison.OrdinalIgnoreCase)
                && string.Equals(key.Short, s, StringComparison.OrdinalIgnoreCase))
            {
                doomed = key;
                break;
            }
        if (doomed is { } k && _activeUntil.Remove(k))
            _log?.Info(LogCategory, $"buff timer manually cleared — target=\"{t}\" spell={s}.");
    }

    // Remove every active-timer entry whose target matches (case-insensitive); returns
    // the count removed. target "" removes the self-keyed timers.
    private int RemoveTimersFor(string target)
    {
        List<(string Target, string Short)>? doomed = null;
        foreach ((string Target, string Short) key in _activeUntil.Keys)
            if (string.Equals(key.Target, target, StringComparison.OrdinalIgnoreCase))
                (doomed ??= new()).Add(key);
        if (doomed is null) return 0;
        foreach ((string, string) key in doomed) _activeUntil.Remove(key);
        return doomed.Count;
    }

    // The instant the timers were frozen on a disconnect, or null while running. The
    // Buff Watchdog reads this so its display freezes at the drop (the heartbeat is a
    // wall clock that keeps ticking while disconnected); the shift on resume then keeps
    // the on-screen remaining continuous across the gap.
    public DateTime? PausedAtUtc => _pausedAt;

    // Freeze the live buff timers on a disconnect — record when so the reconnect can
    // resume them with the same remaining. Used INSTEAD of clearing on ANY disconnect
    // (the buffs persist server-side through link-death and an auto-reconnect is coming);
    // a fresh character (ProfileLoaded) or a too-long gap on resume clears instead.
    public void PauseBuffTimers()
    {
        _pausedAt = _activeUntil.Count > 0 ? _now() : null;
        if (_pausedAt is not null)
            _log?.Info(LogCategory, $"buff timers paused (drop) — {_activeUntil.Count} armed, frozen until reconnect");
    }

    // Resume after a reconnect. WE were the one offline, so our OWN buffs are uncertain —
    // clear the self timers and re-establish them fresh. The other party members stayed
    // ONLINE, so their buffs kept counting toward their real (absolute) expiry the whole
    // time we were gone — so their timers are left exactly as they are (NOT shifted): they
    // now read the correctly reduced remaining. Any whose absolute expiry has already
    // passed while we were away are dropped so they show "not up" and recast.
    public void ResumeBuffTimers()
    {
        if (_pausedAt is null) return;
        _pausedAt = null;

        int selfCleared = RemoveTimersFor("");

        List<(string Target, string Short)>? expired = null;
        foreach ((string Target, string Short) key in _activeUntil.Keys)
            if (_activeUntil[key].Until <= _now())
                (expired ??= new()).Add(key);
        if (expired is not null)
            foreach ((string, string) key in expired) _activeUntil.Remove(key);

        _log?.Info(LogCategory,
            $"buff timers resumed — self cleared ({selfCleared}); party timers keep counting from real expiry "
            + $"(dropped {expired?.Count ?? 0} that lapsed while offline).");
    }

    // A combat round tick elapsed (wired to TickEngine.CombatTickElapsed) — free the
    // between-round cast slot so the new round can cast once. Keyed to the combat
    // round cadence, NOT *Combat Off*: *Combat Off* fires per kill, so in a multi-mob
    // room it would re-open the slot several times a round and let the storm back in.
    public void NotifyRoundComplete() => _betweenRoundSlotUsed = false;

    // An external between-round cast — the combat engine's pre-attack debuff,
    // which fires directly rather than through Evaluate — just went out. Spend
    // this round's single between-round slot so Evaluate won't queue a second one
    // and draw "You have already cast a spell this round!". Cleared on the round
    // tick by NotifyRoundComplete; no-op out of combat, where no per-round cap
    // applies.
    public void MarkBetweenRoundSlotUsed()
    {
        if (_state.InCombat) _betweenRoundSlotUsed = true;
    }

    // True when this round's single between-round cast is already spent — the same
    // predicate Evaluate gates on (so a null from Evaluate means "slot gone", not
    // "nothing due"). The combat engine reads this before a pre-attack debuff so it
    // won't fire a doomed cast into a spent slot.
    public bool BetweenRoundSlotUsed => _state.InCombat && _betweenRoundSlotUsed;

    // The mana-regen roll-spell reroller staged a reroll (its last roll came in
    // below the configured threshold). Instead of firing it on the raw wire, stash
    // it and run the between-round pass now: PickSelfBuff offers it at
    // PriorityBuffing (bypassing the slot's recast timer — the point is to recast
    // immediately), so a due heal/cure still wins and, in combat, it spends the
    // one-cast-per-round slot like every other between-round cast. Cleared in
    // RunDecisionPass when the reroll actually goes out.
    public void RequestManaRegenReroll(string castCode)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return;
        _pendingManaRegenReroll = castCode.Trim();
        Evaluate();
    }

    // Read-only snapshot of the live buff-duration timers for the Buff Watchdog
    // window — a copy so the caller never holds the live dictionary. Read on the UI
    // thread (same thread every _activeUntil write runs on), so no lock is needed.
    public IReadOnlyList<ActiveBuffTimer> SnapshotActiveBuffs()
    {
        List<ActiveBuffTimer> list = new(_activeUntil.Count);
        foreach (KeyValuePair<(string Target, string Short), (DateTime Until, int MarginSec, int TotalSec)> kv in _activeUntil)
            list.Add(new ActiveBuffTimer(kv.Key.Target, kv.Key.Short, kv.Value.Until, kv.Value.MarginSec, kv.Value.TotalSec));
        return list;
    }

    // True when a buff on targetKey ("" = self) is due to be (re)cast: either never
    // confirmed-active, or within the slot's recast lead of expiry. The lead is the
    // one stored with the timer when it was armed (0 ⇒ only once the buff has
    // actually expired).
    private bool IsRecastDue(string targetKey, string spellShort)
    {
        if (!_activeUntil.TryGetValue((targetKey, spellShort), out (DateTime Until, int MarginSec, int TotalSec) t))
            return true;
        return (t.Until - _now()).TotalSeconds <= t.MarginSec;
    }

    // True when spell is the same self-heal we just sent AND neither pool has
    // moved since AND we're still inside the stale window — i.e. a re-evaluation
    // firing before the server reflected the first cast. See
    // SameSelfHealStaleGuard for why this exists.
    private bool IsStaleSelfHealRepeat(string spell)
    {
        if (_lastSelfHealCast is not { } last) return false;
        return last.Spell == spell
            && last.Hp == _state.Hp
            && last.Ma == _state.Ma
            && (_now() - last.At) < SameSelfHealStaleGuard;
    }

    // Start the optimistic self-buff recast clock the instant a self-buff cast is
    // sent. Uses the buff's resolved effect duration when available, else a
    // conservative fallback; the AppliedMessage path later overwrites this with
    // the true duration once the buff confirms.
    private void StartSelfBuffTimer(string spellShort, int marginSec)
    {
        (string Caster, long DurationSec)? info = _buffInfoByShort?.Invoke(spellShort);
        bool resolved = info is { DurationSec: > 0 };
        long seconds = resolved ? info!.Value.DurationSec : UnknownBuffRecastFallbackSec;
        _activeUntil[("", spellShort)] = (_now().AddSeconds(seconds), marginSec, (int)seconds);
        _pendingSelfBuffShort = spellShort;   // awaiting land / fail — cleared by either
        _pendingSelfBuffArmedAt = _now();     // so a stale rejection can't drop this timer
        _log?.Combat(LogCategory,
            $"self-buff {spellShort} sent — optimistic timer {seconds}s"
            + (resolved ? "" : " (fallback — no resolved duration)")
            + $", recast in {Math.Max(0L, seconds - marginSec)}s; awaiting applied-line confirm");
        // Feed the mana-regen reroll engine off the SEND (this runs after TryCast, so
        // the cast is on the wire and the sink's abil 145 query reads the fresh roll).
        // The sink filters for the configured code-145 roll spell + realm; every other
        // self-buff send is a cheap no-op there.
        _selfBuffCastSink?.Invoke(spellShort);
    }

    // A manually-typed buff cast (the user entered its 4-letter cast code) — arm /
    // refresh its recast timer exactly as an engine cast does, anchored on the cast code.
    // The typed code is the reliable identity; we never infer WHICH buff landed from the
    // shared applied message (one Paradigm line names 11 records — bless / chant / …), so
    // a hand-cast is caught here rather than left for the ambiguous applied-line path.
    // A non-buff code (a combat / instant spell with no resolved duration) is inert.
    //
    // A BARE code (`bles`, or a whole-party `unfa`) targets self / the whole party —
    // keyed "" immediately, same as the engine. A code plus a NAME (`gbls fuj`) is a
    // single-target cast: we don't yet know the full target (only the shorthand we
    // typed), so we arm a pending confirm and resolve the member off the success line.
    public void NoteManualBuffCast(string castCode, string? target = null)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return;
        string code = castCode.Trim();
        if (_buffInfoByShort?.Invoke(code) is not { } info || info.DurationSec <= 0) return;

        // Whole-party (lands on everyone, no target token) and bare self casts key to "".
        bool wholeParty = _isPartyWideBuff?.Invoke(code) == true;
        string prefix = GivenName((target ?? string.Empty).Trim());
        if (wholeParty || prefix.Length == 0)
        {
            StartSelfBuffTimer(code, SelfBuffMargin(code));
            return;
        }

        // Single-target hand cast — arm a confirm keyed on the caster line, resolving
        // the full target name off it (prefix-matched to the shorthand). No matcher
        // (unresolvable caster template) → fall back to a self timer so it's not lost.
        if (CasterMessageMatcher.TryCreate(info.Caster) is { } matcher)
            _pendingManualCast = (code, prefix, info.DurationSec, PartyBuffMargin(code), matcher);
        else
            StartSelfBuffTimer(code, SelfBuffMargin(code));
    }

    // Recast lead for a single-target hand cast — the matching unified-list slot's
    // per-slot override, else the shared default.
    private int PartyBuffMargin(string castCode)
    {
        if (_readPartyBuffs?.Invoke() is { } buffs)
            foreach (Models.Profile.BuffSlot slot in buffs.Slots)
                if (string.Equals(slot.Spell?.Trim(), castCode, StringComparison.OrdinalIgnoreCase))
                    return slot.RecastMarginSec;
        return DefaultRecastMarginSec;
    }

    // The configured recast lead for a self-buff cast code: the matching unified-list
    // slot's per-slot override when the code occupies a CastOnSelf slot, else the
    // shared default (covers the mana-regen buff and any hand-cast buff not in a slot).
    private int SelfBuffMargin(string castCode)
    {
        if (_readPartyBuffs?.Invoke() is { } buffs)
            foreach (Models.Profile.BuffSlot slot in buffs.Slots)
                if (slot.CastOnSelf
                    && string.Equals(slot.Spell?.Trim(), castCode, StringComparison.OrdinalIgnoreCase))
                    return slot.RecastMarginSec;
        return DefaultRecastMarginSec;
    }

    // A server rejection of a between-round cast we just sent. "You have already cast
    // a spell this round!" (AlreadyCastThisRound) means the round's single 0-energy
    // between-round slot was already spent, so the spell we JUST sent did NOT fire —
    // latch the slot as spent (a backstop for the proactive one-per-round gate) so we
    // stop retrying until the next round. Fizzle / no-mana / interrupt likewise mean
    // the just-sent buff never landed. In every non-Blocked case drop the buff's
    // optimistic recast timer so it re-attempts next round rather than sitting
    // "active" un-cast. Local Blocked rejections fire before the send / inside the
    // same-round cooldown (the buff never went out), so clearing on them would defeat
    // the optimistic double-cast guard the timer exists for.
    //
    // CastCoordinator is shared with CombatManager's attack-spell cascade, and the
    // server's rejection line never names which cast it's about — spell carries
    // CastCoordinator's best guess (the cast code actually sent). Only drop the
    // pending buff's timer when it MATCHES: an attack-spell resume racing the same
    // round slot and losing is a real, unrelated rejection, not evidence the buff
    // (which may have already landed) needs recasting. Getting this wrong drops a
    // live buff's timer on every unrelated collision, forcing an immediate spurious
    // recast — report paradigm-20260824-233439 ("spamming vlwa"): the attack-spell
    // resume's own rejections kept killing vile ward's just-armed timer every few
    // seconds even though the original cast had already landed.
    private void OnCastFailed(CastFailureReason reason, string detail, string? spell)
    {
        if (reason == CastFailureReason.Blocked) return;
        if (reason == CastFailureReason.AlreadyCastThisRound)
            _betweenRoundSlotUsed = true;
        if (_pendingSelfBuffShort is not { } shortCode) return;
        if (!string.Equals(spell, shortCode, StringComparison.OrdinalIgnoreCase)) return;
        // Only OUR just-sent recast draws a rejection worth acting on. A rejection
        // arriving more than a round after the marker was armed can't be about a send
        // we just made — it's an UNRELATED cast (e.g. a user-typed manual heal spammed
        // at a dying party member) that the server's spell-less "already cast this
        // round" line got misattributed to this buff (CastCoordinator's _lastSpellSent
        // still holds our last own cast). Dropping the live buff's timer then forces a
        // spurious recast cascade — report paradigm-20260827-130111: prev (protection
        // from evil, ~152s) recast 5x in 25s while 75-150s still remained, driven by
        // manual `mahe` rejections. Keep the timer when the marker is stale.
        if (_now() - _pendingSelfBuffArmedAt > PendingSelfBuffRejectionWindow)
        {
            _log?.Combat(LogCategory,
                $"self-buff {shortCode} rejection ignored — pending marker is "
                + $"{(_now() - _pendingSelfBuffArmedAt).TotalSeconds:0}s stale; likely a misattributed unrelated cast");
            return;
        }
        _activeUntil.Remove(("", shortCode));
        _pendingSelfBuffShort = null;
        _log?.Combat(LogCategory,
            $"self-buff {shortCode} did not cast (reason={reason}) — dropped optimistic recast timer");
    }

    private void OnConditionApplied(MessageRecord r)
    {
        // A self-cast buff confirmed via its AppliedMessage — start (or
        // refresh) its duration timer keyed to self so the recast window
        // is honoured. Party-cast confirmation rides OnLine instead.
        if (_shortFromAppliedRecord?.Invoke(r) is { } shortCode)
        {
            if (_buffInfoByShort?.Invoke(shortCode) is { } info)
            {
                // Preserve the recast lead armed on send (StartSelfBuffTimer ran
                // first for a bless-slot cast); default it for anything confirmed
                // without a prior optimistic timer (e.g. the HP-regen HoT).
                int margin = _activeUntil.TryGetValue(("", shortCode), out (DateTime Until, int MarginSec, int TotalSec) prev)
                    ? prev.MarginSec
                    : DefaultRecastMarginSec;
                _activeUntil[("", shortCode)] = (_now().AddSeconds(info.DurationSec), margin, (int)info.DurationSec);
                _log?.Combat(LogCategory,
                    $"self-buff {shortCode} confirmed active (applied line) — "
                    + $"duration {info.DurationSec}s, recast in {Math.Max(0L, info.DurationSec - margin)}s");
            }
            // Landed — the real duration timer is now authoritative, so the pending
            // optimistic marker mustn't later be treated as an unlanded cast.
            if (_pendingSelfBuffShort == shortCode) _pendingSelfBuffShort = null;
            // NOTE: the mana-regen reroll engine is fed off the SEND path
            // (StartSelfBuffTimer), NOT here — a roll spell confirms via the shared
            // "mana regenerating" condition, which can't be mapped back to the specific
            // spell, so this applied-confirm never fires for it (paradigm-20260830-110918).
        }
        Evaluate();
    }

    private void OnConditionEnded(MessageRecord r)
    {
        // Server-confirmed early wear-off — drop the self timer so the next
        // pass re-attempts immediately rather than waiting out a stale clock.
        if (_shortFromAppliedRecord?.Invoke(r) is { } shortCode
            && _activeUntil.Remove(("", shortCode)))
            _log?.Combat(LogCategory,
                $"self-buff {shortCode} wore off (wear-off line) — recast timer cleared");
        Evaluate();
    }

    private void OnLine(LineExtractor.EmittedLine line)
    {
        if (_pendingManualCast is { } man) ConfirmManualCast(man, line.Text);
        if (_pendingPartyCast is not { } p) return;

        // "You do not see <target> here!" — the member is in the party (so in the room)
        // but HIDING, so a single-target cast can't land and the confirm we were waiting
        // for will never come. Back off casting on them (until we move / they reappear)
        // instead of retrying — and firing the failure — every round.
        if (IsTargetNotSeenLine(line.Text, p.Target))
        {
            // Store lower-cased: the recast key + the watchdog's lookup are both the
            // lower given name, and the watchdog matches ordinally.
            _hiddenTargets.Add(p.Target.Trim().ToLowerInvariant());
            _log?.Info(LogCategory,
                $"party-buff target {p.Target} is hidden (\"do not see … here\") — backing off until we move or they reappear.");
            _pendingPartyCast = null;
            return;
        }

        if (!p.Matcher.ConfirmsTarget(line.Text, p.Target)) return;

        string key = p.Target.Trim().ToLowerInvariant();
        _activeUntil[(key, p.Short)] = (_now().AddSeconds(p.DurationSec), p.MarginSec, (int)p.DurationSec);
        // Info, not Combat: the user wants to confirm the recast timer actually
        // armed and see when it will re-fire, and the combat-diagnostics channel is
        // off in normal play. Surface both the effect duration and the recast lead
        // (fires the slot's recast margin before expiry).
        long recastInSec = Math.Max(0L, p.DurationSec - p.MarginSec);
        _log?.Info(LogCategory,
            $"party-buff confirmed spell={p.Short} target={p.Target} " +
            $"duration={p.DurationSec}s — recast in {recastInSec}s.");
        _pendingPartyCast = null;
    }

    // Resolve a hand-typed single-target buff off its success line: pull the FULL target
    // name (prefix-matched to the shorthand we typed), then arm that member's timer — or
    // ours, if we named ourselves. A target outside the party isn't tracked by the Buff
    // Watchdog, so we don't arm a timer for it.
    private void ConfirmManualCast(
        (string Short, string Prefix, long DurationSec, int MarginSec, CasterMessageMatcher Matcher) man,
        string lineText)
    {
        if (!man.Matcher.TryResolveTarget(lineText, man.Prefix, out string full)) return;
        _pendingManualCast = null;

        string given = GivenName(full).ToLowerInvariant();
        if (given.Length == 0) return;

        if (string.Equals(given, SelfGivenLower(), StringComparison.OrdinalIgnoreCase))
        {
            StartSelfBuffTimer(man.Short, SelfBuffMargin(man.Short));   // we named ourselves
            return;
        }
        if (!IsPartyMemberGiven(given)) return;   // a non-party target the watchdog can't show

        _activeUntil[(given, man.Short)] =
            (_now().AddSeconds(man.DurationSec), man.MarginSec, (int)man.DurationSec);
        _log?.Info(LogCategory,
            $"manual party-buff confirmed spell={man.Short} target={given} " +
            $"duration={man.DurationSec}s — recast in {Math.Max(0L, man.DurationSec - man.MarginSec)}s.");
    }

    // Our own given name (lower-cased) from the party roster, or empty when solo.
    private string SelfGivenLower()
    {
        if (_party is not null)
            foreach (PartyMember m in _party.Members)
                if (m.IsSelf) return GivenName(m.Name).ToLowerInvariant();
        return string.Empty;
    }

    // Whether a lower-cased given name is a current non-self party member.
    private bool IsPartyMemberGiven(string given)
    {
        if (_party is null) return false;
        foreach (PartyMember m in _party.Members)
            if (!m.IsSelf
                && string.Equals(GivenName(m.Name), given, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // The server's "you can't see the target" answer to a single-target cast:
    // "You do not see <name> here!". Matched loosely (tolerates markup / spacing) but
    // only ever consulted for the pending cast's own target, so a false match is moot.
    private static bool IsTargetNotSeenLine(string text, string target)
    {
        string given = target.Trim();
        return given.Length > 0
            && text.Contains("do not see", StringComparison.OrdinalIgnoreCase)
            && text.Contains(given, StringComparison.OrdinalIgnoreCase)
            && text.Contains("here", StringComparison.OrdinalIgnoreCase);
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerState.Hp):
            case nameof(PlayerState.MaxHp):
            case nameof(PlayerState.Ma):
            case nameof(PlayerState.MaxMa):
            case nameof(PlayerState.InCombat):
            case nameof(PlayerState.Position):
            case nameof(PlayerState.HasPromptData):
                Evaluate();
                break;
        }
    }

    // Run one decision pass: walk the priority list and fire the first ready
    // candidate. Returns the spell that was cast (for diagnostics / tests), or null
    // if nothing matched.
    public string? Evaluate()
    {
        // Disconnected — pause the whole loop. The link is down (an in-flight
        // reconnect will restore it), sends no-op, and the buff timers are already
        // frozen (PauseBuffTimers); casting now would only re-arm recast timers off
        // phantom sends. Resumes when the gate reads connected again.
        if (_isConnected?.Invoke() == false) return null;
        // Two independent masters share this loop: the heal / cure / rest
        // categories run under AutoHealRest (_isEnabled), buffing runs under
        // AutoBless (_autoBlessEnabled), and each is gated separately in the
        // category switch below. Bless is controlled by the Auto-Bless toggle and
        // nothing else — so when AutoHealRest is off but AutoBless is on we must
        // still fall through to the buffing category, not bail here. Only quit
        // when BOTH are off (nothing in the switch could fire).
        bool healRestEnabled = _isEnabled();
        bool blessEnabled = _autoBlessEnabled?.Invoke() ?? true;
        if (!healRestEnabled && !blessEnabled) return null;
        // A full-screen menu owns the keyboard (train-stats box): any cast text
        // would corrupt its form, so suppress every category until it closes.
        if (_inputCaptured?.Invoke() == true) return null;
        if (!_state.HasPromptData) return null;
        if (_state.MaxHp <= 0) return null;
        if (_state.Hp <= 0) return null;     // dead — DeathRecoveryManager owns this case
        if (_cast.IsCastBlocked) return null;
        // A prior survival cast already spent a round the combat engine's attack
        // spell was owed — sit out entirely so that resume can reclaim the very
        // next round, rather than re-firing again ourselves the instant HP dips
        // (which it always will while nothing is fighting back). No exception for
        // urgency: engage / attack / heal-or-buff / attack / heal-or-buff / ... is
        // the fixed cadence regardless of how the fight is going.
        if (_attackOwed?.Invoke() == true) return null;

        // One between-round spell (heal / cure / buff / debuff / item) per combat
        // round: the game allows a single 0-energy cast per round, so a second draws
        // "You have already cast a spell this round!" and does NOT fire. Once this
        // round's slot is spent, suppress further between-round attempts in combat
        // instead of sending doomed casts — the mageshield recast storm came from the
        // engine firing several buffs a round because the per-hit combat tick kept
        // clearing the coordinator's one-per-round cooldown (report
        // paradigm-20260816-101702). The slot frees at the true round boundary
        // (NotifyRoundComplete). Out of combat no per-round cap applies, so the gate
        // is combat-only.
        if (_state.InCombat && _betweenRoundSlotUsed) return null;

        string? cast = RunDecisionPass(healRestEnabled, blessEnabled);
        // Mark the round's single between-round slot spent so a second Evaluate this
        // round doesn't send another (doomed) cast; cleared at the round boundary.
        if (cast is not null && _state.InCombat) _betweenRoundSlotUsed = true;
        return cast;
    }

    // Walk the priority list and fire the first ready candidate. Returns the spell
    // that was cast (for diagnostics / tests), or null if nothing matched.
    private string? RunDecisionPass(bool healRestEnabled, bool blessEnabled)
    {
        SpellsSettings spells = _readSpells();
        HealthSettings health = _readHealth();

        PartySettings? partySettings = _readPartySettings?.Invoke();

        // Log the full between-round queue (all due candidates, priority-ordered) before
        // firing the top one. Reached only when a cast can actually go out — Evaluate
        // gates on the cast cooldown upstream — so it lands ~once per between-round, not
        // every heartbeat poll.
        LogDueQueue(spells, health, partySettings, healRestEnabled, blessEnabled);

        // Stale-HP guard (see _hpUnconfirmedThisPass): when this pass is driven by a
        // damage-line combat tick, _state.Hp still holds the previous round's value —
        // the prompt that refreshes it lands later in the burst. Hold the non-heal
        // survival categories (cure / buff / debuff) so the round's one between-round
        // slot isn't spent on them while HP is unknown; the imminent prompt's reactive
        // Evaluate re-runs on fresh HP and fires a heal if one is due. Heals stay
        // eligible here — they're safe on the stale read (they simply don't fire when
        // it looks healthy, and the prompt catches the real drop). (report
        // paradigm-20260904-214056: an armour buff fired on a stale HP=254 read while
        // the player was already at 117 and died two rounds later.)
        bool holdNonHeal = _hpUnconfirmedThisPass && _state.InCombat;
        if (holdNonHeal)
            _log?.Combat(LogCategory,
                "between-round non-heal categories held — HP unconfirmed on a damage-driven tick (prompt pending).");

        foreach (SpellCategory category in PrioritisedCategories(spells))
        {
            if (holdNonHeal
                && category is SpellCategory.Curing or SpellCategory.Buffing or SpellCategory.Debuffing)
                continue;

            CastCandidate? pick = category switch
            {
                // Heal / cure / debuff stay under AutoHealRest; buffing under
                // AutoBless. When only one master is on, the other's categories
                // are skipped rather than the whole loop bailing.
                SpellCategory.DownedAllyHeal  => healRestEnabled ? PickDownedAllyHeal(partySettings) : null,
                SpellCategory.MinorPartyHeal  => healRestEnabled ? PickMinorPartyHeal(partySettings) : null,
                SpellCategory.MajorPartyHeal  => healRestEnabled ? PickMajorPartyHeal(partySettings) : null,
                SpellCategory.MinorSelfHeal   => healRestEnabled ? Wrap(PickMinorSelfHeal(spells, health)) : null,
                SpellCategory.MajorSelfHeal   => healRestEnabled ? Wrap(PickMajorSelfHeal(spells, health)) : null,
                SpellCategory.Curing          => healRestEnabled ? PickCure(spells) : null,
                SpellCategory.Buffing         => blessEnabled ? PickBuff(spells, health, partySettings) : null,
                SpellCategory.Debuffing       => healRestEnabled ? PickDebuff() : null,
                _                              => null,
            };

            if (pick is not { } cand) continue;
            if (string.IsNullOrWhiteSpace(cand.Spell)) continue;

            // Item-cast buff (#-token in a Bless slot): bypass the raw cast path
            // entirely — run the equip → use → re-equip sequence and key the
            // recast timer by the token. Only buff slots carry tokens.
            if (category == SpellCategory.Buffing && ItemCastToken.IsToken(cand.Spell))
            {
                if (TryFireItemCast(cand.Spell, cand.RecastMarginSec)) return cand.Spell;
                continue; // unresolved / non-buff item — let a later category try
            }

            // Don't attempt a cast we can't pay for. Combat (offensive /
            // debuff) casts are gated by the Combat tab's MinManaPerCast
            // threshold, so the game-data affordability check applies only to
            // the survival categories owned here; an unknown cost never blocks.
            // Skip-and-continue so a cheaper lower-priority cast can still fire.
            if (category != SpellCategory.Debuffing
                && _manaCostLookup?.Invoke(cand.Spell) is { } cost
                && _state.Ma < cost)
            {
                _log?.Combat(LogCategory,
                    $"{category} skip spell={cand.Spell} cost={cost} ma={_state.Ma} " +
                    "(insufficient mana)");
                continue;
            }

            // Self-heal duplicate guard: OnCombatTick wipes the coordinator's
            // one-cast-per-round cooldown, so a second Evaluate this round can
            // re-issue the SAME heal on stale pool data before the server
            // reflects the first. Suppress a byte-for-byte repeat inside the
            // stale window; skip-and-continue so a different (e.g. major) heal or
            // lower-priority cast can still fire.
            bool isSelfHeal = category is SpellCategory.MinorSelfHeal
                                        or SpellCategory.MajorSelfHeal;
            if (isSelfHeal && IsStaleSelfHealRepeat(cand.Spell))
            {
                _log?.Combat(LogCategory,
                    $"{category} skip spell={cand.Spell} (stale repeat — " +
                    $"hp={_state.Hp} ma={_state.Ma} unchanged since last cast)");
                continue;
            }

            if (!_cast.TryCast(cand.Spell, cand.Target)) return null;

            // Combat-sourced debuff landed — advance the combat engine's
            // once-per-room / once-per-target bookkeeping so it won't re-fire.
            if (category == SpellCategory.Debuffing) _combatDebuffCommit?.Invoke();

            if (isSelfHeal)
                _lastSelfHealCast = (cand.Spell, _state.Hp, _state.Ma, _now());

            // Buff cast sent — start the recast clock immediately. A party buff
            // (targeted) arms CasterMessage confirmation so the timer starts on
            // the observed land; a self buff (null target) starts an optimistic
            // timer NOW so a stale re-evaluation this round can't re-issue it
            // before the AppliedMessage confirms with the true duration.
            if (category == SpellCategory.Buffing)
            {
                if (cand.Target is { } tgt) ArmPartyBuffConfirm(cand.Spell, tgt, cand.RecastMarginSec);
                else StartSelfBuffTimer(cand.Spell, cand.RecastMarginSec);
                // A staged mana-regen reroll just went out through the priority
                // loop — consume it so it isn't re-offered next pass (the reroller
                // re-stages one if the fresh roll is still below threshold).
                if (string.Equals(cand.Spell, _pendingManaRegenReroll, StringComparison.OrdinalIgnoreCase))
                    _pendingManaRegenReroll = null;
            }

            _log?.Combat(LogCategory,
                $"{category} fired spell={cand.Spell} target={cand.Target ?? "<self>"} " +
                $"hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa}");
            // Tell the combat engine a between-round cast went out so it can
            // resume our weapon attack on the *Combat Off* this cast triggers.
            CastFired?.Invoke();
            return cand.Spell;
        }

        return null;
    }

    // Combat-diagnostics view of the between-round queue: every DUE candidate the
    // engines want cast this round — self/party heals, a cure, and every buff inside
    // its recast window — in type-priority order (the Spells-tab priorities), formatted
    // `code(typePrio)`; buffs additionally carry their slot number as `code(typePrio-
    // slot)`. Read-only: the heal/party/cure pickers don't mutate state, and buffs are
    // enumerated directly (so PickSelfBuff's mana-regen-reroll consumption is untouched).
    // Debuffs are omitted — they're the combat engine's decision and re-peeking it here
    // is not guaranteed side-effect-free. Only logged when the queue is non-empty.
    private void LogDueQueue(SpellsSettings spells, HealthSettings health, PartySettings? party,
        bool healRestEnabled, bool blessEnabled)
    {
        if (_log is null) return;
        List<(int Prio, int Slot, string Text)> q = new();

        void AddSurvival(SpellCategory cat, string? spell)
        {
            if (string.IsNullOrWhiteSpace(spell)) return;
            int p = CategoryPriority(spells, cat);
            string prioLabel = cat == SpellCategory.DownedAllyHeal ? "rescue" : p.ToString();
            q.Add((p, -1, $"{spell.Trim()}({prioLabel})"));
        }
        if (healRestEnabled)
        {
            AddSurvival(SpellCategory.DownedAllyHeal, PickDownedAllyHeal(party)?.Spell);
            AddSurvival(SpellCategory.MinorPartyHeal, PickMinorPartyHeal(party)?.Spell);
            AddSurvival(SpellCategory.MajorPartyHeal, PickMajorPartyHeal(party)?.Spell);
            AddSurvival(SpellCategory.MinorSelfHeal, PickMinorSelfHeal(spells, health));
            AddSurvival(SpellCategory.MajorSelfHeal, PickMajorSelfHeal(spells, health));
            AddSurvival(SpellCategory.Curing, PickCure(spells)?.Spell);
        }
        if (blessEnabled)
        {
            int buffPrio = CategoryPriority(spells, SpellCategory.Buffing);
            // The unified buff list, in priority (list) order. Self / whole-party slots
            // key their recast to "" (they land on us); a member-target slot's per-member
            // timers aren't enumerated here (best-effort self view). Only-when-dark light
            // slots are the auto-light system's job, not this queue.
            if (_readPartyBuffs?.Invoke() is { } buffs)
            {
                int slotNo = 0;
                foreach (Models.Profile.BuffSlot slot in buffs.Slots)
                {
                    slotNo++;
                    string? code = slot.Spell?.Trim();
                    if (string.IsNullOrWhiteSpace(code) || slot.OnlyWhenDark || !IsRecastDue("", code)) continue;
                    q.Add((buffPrio, slotNo, $"{code}({buffPrio}-{slotNo})"));
                }
            }
        }

        if (q.Count == 0) return;
        string ordered = string.Join(", ", q.OrderBy(x => x.Prio).ThenBy(x => x.Slot).Select(x => x.Text));
        _log.Combat(LogCategory, $"{{spells queued={ordered}}}");
    }

    // The configured type-priority number for a between-round category (the Spells-tab
    // priorities). DownedAllyHeal has none — it always leads — so it sorts first.
    private static int CategoryPriority(SpellsSettings s, SpellCategory cat) => cat switch
    {
        SpellCategory.DownedAllyHeal => int.MinValue,
        SpellCategory.MinorPartyHeal => s.PriorityMinorPartyHeal,
        SpellCategory.MajorPartyHeal => s.PriorityMajorPartyHeal,
        SpellCategory.MinorSelfHeal => s.PriorityMinorSelfHeal,
        SpellCategory.MajorSelfHeal => s.PriorityMajorSelfHeal,
        SpellCategory.Curing => s.PriorityCuring,
        SpellCategory.Buffing => s.PriorityBuffing,
        SpellCategory.Debuffing => s.PriorityDebuffing,
        _ => int.MaxValue,
    };

    private static CastCandidate? Wrap(string? spell) =>
        string.IsNullOrWhiteSpace(spell) ? null : new CastCandidate(spell, Target: null);

    // Fire an item-cast buff: resolve its recast duration, run the equip → use →
    // re-equip sequence, then start the round cooldown + the token-keyed recast
    // timer. Returns false (so a later category can try) when the token doesn't
    // resolve to a real buff or the sequence didn't send. The timer is set
    // proactively from the cast spell's computed duration rather than awaiting an
    // AppliedMessage, since the landing buff confirms under the spell's own cast
    // code, not the token.
    private bool TryFireItemCast(string token, int marginSec)
    {
        if (_itemCastDuration is null || _executeItemCast is null) return false;
        if (_itemCastDuration(token) is not { } durationSec || durationSec <= 0) return false;
        if (!_executeItemCast(token)) return false;

        _cast.NotifyExternalCastSent();
        _activeUntil[("", token)] = (_now().AddSeconds(durationSec), marginSec, (int)durationSec);
        // Same reasoning as the party-buff confirm: surface the armed recast timer
        // on the always-on Info channel, not combat diagnostics.
        long recastInSec = Math.Max(0L, durationSec - marginSec);
        _log?.Info(LogCategory,
            $"item-cast buff fired token={token} duration={durationSec}s — recast in {recastInSec}s.");
        CastFired?.Invoke();
        return true;
    }

    // Categories in priority order (lowest int first, ties broken by category enum
    // order for determinism).
    private static IEnumerable<SpellCategory> PrioritisedCategories(SpellsSettings s)
    {
        (SpellCategory Cat, int Prio)[] order =
        {
            (SpellCategory.MinorPartyHeal, s.PriorityMinorPartyHeal),
            (SpellCategory.MajorPartyHeal, s.PriorityMajorPartyHeal),
            (SpellCategory.MinorSelfHeal,  s.PriorityMinorSelfHeal),
            (SpellCategory.MajorSelfHeal,  s.PriorityMajorSelfHeal),
            (SpellCategory.Curing,         s.PriorityCuring),
            (SpellCategory.Buffing,        s.PriorityBuffing),
            (SpellCategory.Debuffing,      s.PriorityDebuffing),
        };
        Array.Sort(order, (a, b) =>
        {
            int p = a.Prio.CompareTo(b.Prio);
            return p != 0 ? p : ((int)a.Cat).CompareTo((int)b.Cat);
        });
        // A downed ally is a life-critical rescue — it always fires ahead of every
        // user-orderable category, so it leads the walk unconditionally.
        yield return SpellCategory.DownedAllyHeal;
        foreach ((SpellCategory cat, int _) in order) yield return cat;
    }

    // ----- Self heal --------------------------------------------------

    private string? PickMajorSelfHeal(SpellsSettings spells, HealthSettings health)
    {
        if (_state.MaxHp <= 0) return null;
        if (!ManaClearsHealFloor(health)) return null;
        // Trigger read per HpThresholdMode — percentage of MaxHp, or an absolute
        // HP value — then compared against raw HP.
        int majorTrigger = ResolveHealHpTrigger(health.HpThresholdMode, health.MajorHealCombatTrigger);
        if (_state.Hp > majorTrigger) return null;
        // Fall back to minor when the user hasn't configured a major
        // — better to fire something than skip the life-threat path.
        return !string.IsNullOrWhiteSpace(spells.MajorHealSpell)
            ? spells.MajorHealSpell
            : spells.MinorHealSpell;
    }

    private string? PickMinorSelfHeal(SpellsSettings spells, HealthSettings health)
    {
        if (_state.MaxHp <= 0) return null;
        if (!ManaClearsHealFloor(health)) return null;

        // Use the in-combat trigger while engaged, the rest-time
        // trigger otherwise (matches the user's two-threshold mental
        // model from the Health tab). Read per HpThresholdMode.
        int triggerValue = _state.InCombat
            ? health.MinorHealCombatTrigger
            : health.HealRestTrigger;
        int trigger = ResolveHealHpTrigger(health.HpThresholdMode, triggerValue);
        if (_state.Hp > trigger) return null;

        // Out-of-combat heal-spell-during-rest only — don't cast
        // mid-walk between rooms.
        if (!_state.InCombat && _state.Position != PlayerPosition.Resting) return null;

        // Prefer an HP-regen HoT (regeneration / rejuvinating field) over the
        // single-target heal: once it's ticking it restores far more per mana
        // than repeated instant heals, so cast it FIRST when the minor-heal
        // trigger trips. Two gates keep it safe:
        //  • It's only substituted while HP sits ABOVE the major-heal trigger —
        //    inside the life-threat band we want the instant top-up, never a
        //    slow HoT that heals a round later.
        //  • IsRecastDue is false once the HoT is confirmed active with
        //    remaining duration, so a running HoT falls through to the instant
        //    single-target heal for the immediate top-up while it ticks.
        int majorTrigger = ResolveHealHpTrigger(health.HpThresholdMode, health.MajorHealCombatTrigger);

        // Two exclusive bands. Once HP falls into the major-heal band, yield to
        // MajorSelfHeal instead of firing minor again. Minor is walked BEFORE major
        // (lower priority int by default), and without this lower bound minor
        // matched the whole Hp<=minorTrigger range and fired even at single-digit
        // HP — major was dead code in combat and the player died (report
        // paradigm-20260819-121247: minor cast at 13/142 HP with mana to spare).
        // Yield only when a major spell is configured AND affordable, so a
        // mana-starved caster still falls back to the cheaper minor heal rather
        // than healing nothing (the decision pass would skip an unaffordable major
        // and, with minor yielded, leave no heal at all).
        if (_state.Hp <= majorTrigger
            && !string.IsNullOrWhiteSpace(spells.MajorHealSpell)
            && SpellAffordable(spells.MajorHealSpell))
            return null;

        if (_state.Hp > majorTrigger
            && !string.IsNullOrWhiteSpace(spells.HpRegenSpell)
            && IsRecastDue("", spells.HpRegenSpell))
            return spells.HpRegenSpell;

        return string.IsNullOrWhiteSpace(spells.MinorHealSpell) ? null : spells.MinorHealSpell;
    }

    // Mirrors the decision-pass affordability skip (an unknown cost never blocks):
    // a spell is castable when we don't know its cost or the pool covers it. Used
    // so a minor heal only yields to major when major could actually fire.
    private bool SpellAffordable(string spell)
        => _manaCostLookup?.Invoke(spell) is not { } cost || _state.Ma >= cost;

    // Mana-floor gate for self heals: only cast a heal when the caster pool sits at
    // or above HealIfAboveMaCombat (in combat) or HealIfAboveMaResting (resting /
    // idle), so a low pool regenerates instead of being drained on heal spells. A
    // floor of 0 disables the gate. The value is read per MaThresholdMode
    // (percentage of MaxMa, or absolute MA); an unknown pool (MaxMa 0, percentage
    // mode) never blocks a heal so the safety path isn't suppressed by missing
    // prompt data.
    private bool ManaClearsHealFloor(HealthSettings health)
    {
        int floorValue = _state.InCombat
            ? health.HealIfAboveMaCombat
            : health.HealIfAboveMaResting;
        if (floorValue <= 0) return true;
        // Unknown pool (percentage mode, MaxMa 0) resolves to 0, so a heal is
        // never blocked before prompt data loads. Absolute mode compares raw MA.
        int floor = PoolThreshold.Resolve(health.MaThresholdMode, floorValue, _state.MaxMa);
        return _state.Ma >= floor;
    }

    // ----- Curing -----------------------------------------------------

    // Pick the next cure to fire: self first (the caster can't help anyone while
    // movement-prevented or blind, and a self-cure is the cheapest to confirm), then
    // party. Both scopes draw on the same SpellsSettings cure-spell config — a
    // MajorMUD cure spell targets self or another player, so the only difference is
    // the target string.
    private CastCandidate? PickCure(SpellsSettings spells)
    {
        if (PickSelfCure(spells) is { } selfSpell)
            return new CastCandidate(selfSpell, Target: null);
        return PickPartyCure(spells);
    }

    // Walk the cure-priority list and return the first configured spell whose
    // matching ailment is currently active on US. MovementPrevented covers paralyze /
    // hold / sleep — they all render the same to a player (can't act); the user's
    // CureHoldsSpell is the catch-all.
    private string? PickSelfCure(SpellsSettings spells)
    {
        if (_conditions is null) return null;

        if (_conditions.IsMovementPrevented
         && !string.IsNullOrWhiteSpace(spells.CureHoldsSpell))
            return spells.CureHoldsSpell;

        if (_conditions.IsPoisoned
         && !string.IsNullOrWhiteSpace(spells.CurePoisonSpell))
            return spells.CurePoisonSpell;

        if (_conditions.IsDiseased
         && !string.IsNullOrWhiteSpace(spells.CureDiseaseSpell))
            return spells.CureDiseaseSpell;

        if (_conditions.IsBlinded
         && !string.IsNullOrWhiteSpace(spells.CureBlindnessSpell))
            return spells.CureBlindnessSpell;

        // No CureConfusion picker on SpellsSettings yet (legacy: rare
        // and short-lived in stock MajorMUD). When added, slot it
        // last in the priority order. Same shape for any future
        // realm-specific status.
        return null;
    }

    // Walk live party members and cast the configured cure spell on the first member
    // whose ailment chip is set. The chip is mirrored from the member's inbound
    // .@poisoned / .@diseased / .@blind announce by PartyAilmentTracker. Same
    // cure-spell config as self-cure; the target string routes the cast to the
    // member. Internal order mirrors self-cure (poison → disease → blindness).
    // Confusion has no cure spell — a @confused chip is never picked up here (gap in
    // PartyAilmentTracker).
    private CastCandidate? PickPartyCure(SpellsSettings spells)
    {
        if (_party is null) return null;
        if (_party.Members.Count == 0) return null;

        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf) continue;
            if (m.Poisoned && !string.IsNullOrWhiteSpace(spells.CurePoisonSpell))
                return new CastCandidate(spells.CurePoisonSpell, MemberTarget(m));
            if (m.Diseased && !string.IsNullOrWhiteSpace(spells.CureDiseaseSpell))
                return new CastCandidate(spells.CureDiseaseSpell, MemberTarget(m));
            if (m.Blinded && !string.IsNullOrWhiteSpace(spells.CureBlindnessSpell))
                return new CastCandidate(spells.CureBlindnessSpell, MemberTarget(m));
        }
        return null;
    }

    // Resolve the cast-target string for a party member. Self resolves to null —
    // a self-cast in MajorMUD is the bare 4-letter spell code with no target, and
    // appending our own name (which the par table can carry as "Given Family")
    // makes the server reject the cast against a non-existent room target. Other
    // members resolve to their GIVEN name only; MajorMUD targets a cast by first
    // name token, so a "Given Family" par-row name would likewise miss.
    private static string? MemberTarget(PartyMember m) =>
        m.IsSelf ? null : GivenName(m.Name);

    private static string GivenName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }

    // ----- Downed-ally rescue heal ------------------------------------

    // Top-priority name-targeted heal for a dropped ally that's been aided back
    // to positive HP but hasn't rejoined `par` yet. Confirmed mechanic: a heal
    // cast at such an ally by name still lands even though they're off the
    // roster, so this keeps topping them up until they recover / rejoin. Prefers
    // the major party-heal spell (a downed ally is by definition critical),
    // falling back to the minor one; if neither is configured we can't heal, but
    // the rescue engine still aids + re-invites without us.
    private CastCandidate? PickDownedAllyHeal(PartySettings? settings)
    {
        if (_downedAllies is null) return null;
        if (settings is null) return null;
        IReadOnlyList<string> allies = _downedAllies();
        if (allies.Count == 0) return null;
        string? spell = !string.IsNullOrWhiteSpace(settings.MajorPartyHealSpell)
            ? settings.MajorPartyHealSpell
            : settings.MinorPartyHealSpell;
        if (string.IsNullOrWhiteSpace(spell)) return null;
        return new CastCandidate(spell, Target: GivenName(allies[0]));
    }

    // ----- Party heal -------------------------------------------------

    // Walk live party members; cast the minor party heal on whoever is below
    // MinorHealMemberThresholdPercent. When AoeMinMembers or more members are below
    // the threshold AND a group spell is configured, fire the AOE variant instead
    // (no target).
    private CastCandidate? PickMinorPartyHeal(PartySettings? settings)
    {
        // Severity precedence (same two-band rule as the self heal): if a member
        // has dropped into the major-party band, yield to MajorPartyHeal instead
        // of firing the minor party heal — otherwise minor, walked first, would
        // keep a critically-low member on minor heals while major stayed dead
        // code (report paradigm-20260819-121247, party-side). Yield only when the
        // major party heal can actually fire (configured + affordable), so a
        // mana-starved healer still falls back to the cheaper minor party heal.
        if (PickMajorPartyHeal(settings) is { } major && SpellAffordable(major.Spell))
            return null;

        return PickPartyHeal(settings,
            threshold: settings?.MinorHealMemberThresholdPercent ?? 70,
            singleSpell: settings?.MinorPartyHealSpell,
            aoeSpell:    settings?.MinorPartyHealAoeSpell);
    }

    // Symmetric to PickMinorPartyHeal at the major / critical threshold.
    private CastCandidate? PickMajorPartyHeal(PartySettings? settings) =>
        PickPartyHeal(settings,
            threshold: settings?.MajorHealMemberThresholdPercent ?? 40,
            singleSpell: settings?.MajorPartyHealSpell,
            aoeSpell:    settings?.MajorPartyHealAoeSpell);

    private CastCandidate? PickPartyHeal(
        PartySettings? settings, int threshold, string? singleSpell, string? aoeSpell)
    {
        if (_party is null) return null;
        if (settings is null) return null;
        if (_party.Members.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(singleSpell)
         && string.IsNullOrWhiteSpace(aoeSpell)) return null;

        // Count members below threshold + remember the lowest one
        // so single-target picks the most urgent target.
        int below = 0;
        PartyMember? lowest = null;
        foreach (PartyMember m in _party.Members)
        {
            // Invited-but-not-joined rows carry no health data — BaselineHp and
            // HpPercent stay 0 until the on-join @health exchange runs, so a
            // freshly-invited (or relogged-and-re-invited) member reads as 0% and
            // gets spam-healed every cast tick. They aren't a healable party member
            // until they actually follow, so skip them and wait for real vitals.
            if (m.IsInvited) continue;
            if (m.HpPercent >= threshold) continue;
            below++;
            if (lowest is null || m.HpPercent < lowest.HpPercent)
                lowest = m;
        }
        if (below == 0) return null;

        int aoeMin = Math.Max(2, settings.AoeMinMembers);
        if (below >= aoeMin && !string.IsNullOrWhiteSpace(aoeSpell))
            return new CastCandidate(aoeSpell, Target: null);

        if (!string.IsNullOrWhiteSpace(singleSpell) && lowest is not null)
            return new CastCandidate(singleSpell, Target: MemberTarget(lowest));

        // Below threshold but only AOE configured and below count
        // hasn't hit AoeMinMembers — accept the AOE anyway since
        // a single-target alternative wasn't picked. Matches the
        // user's "I configured AOE only because that's what I have"
        // intent.
        if (!string.IsNullOrWhiteSpace(aoeSpell))
            return new CastCandidate(aoeSpell, Target: null);

        return null;
    }

    // ----- Buffing ----------------------------------------------------

    // Pick the next buff to (re)cast. Self buffs (Bless1–10 + regen + when-full
    // slots) take precedence over party buffs; within each scope the slot order is
    // the priority order. Returns the first slot whose buff is due to recast — never
    // confirmed-active, or within the RecastMarginSec expiry window.
    //
    // "Active" is duration-based: a buff's timer starts only when we observe OUR
    // successful cast (self via the ConditionTracker AppliedMessage, party via the
    // CasterMessage matcher) and runs for SpellCalculator.Duration seconds. No
    // confirmation => no timer => the next eligible pass re-attempts (the
    // CastCoordinator cooldown prevents spam). The ConditionEnded wear-off line
    // clears the timer early for dispels / area-clears.
    //
    // MA-floor gate: only consider buffs when MA is at or above BlessIfAboveMa.
    // Mirrors MegaMUD's "don't burn buff mana when we'll need it for heals soon"
    // behaviour.
    private CastCandidate? PickBuff(SpellsSettings spells, HealthSettings health, PartySettings? party)
    {
        // Stealth gate: any cast — or an item-cast's equip/use/re-equip — breaks
        // sneak / hide; suppress buffs entirely while stealthed so a backstab
        // window stays open.
        if (_isStealthedFunc?.Invoke() == true) return null;

        // Buff-strip-room gate: the room casts a buff-removal spell on entry, so
        // any buff we put up is torn straight back off. Skip the whole category
        // here — heals / cures still run their own paths.
        if (_buffStripRoom?.Invoke() == true)
        {
            _log?.Combat(LogCategory, "buff skipped — room strips buffs on entry.");
            return null;
        }

        // Mana-floor gate applies to *mana-drawing* buffs only ("don't burn buff
        // mana when we'll need it for heals soon"). It is NOT a whole-category
        // early-out: a free item-cast buff (a charge wand / proc item whose
        // use-spell costs 0 mana) recasts regardless, so the floor + per-cast
        // affordability are applied per-slot below via IsBuffAffordable. MA
        // unknown (MaxMa 0) blocks mana-drawing buffs but not free item-casts.
        // BlessIfAboveMa read per MaThresholdMode — percentage of MaxMa, or an
        // absolute MA / kai value — then compared against raw MA.
        int blessFloor = PoolThreshold.Resolve(
            health.MaThresholdMode, health.BlessIfAboveMa, _state.MaxMa);
        bool manaBuffsAllowed = _state.MaxMa > 0 && _state.Ma >= blessFloor;

        // Mana-regen (+ its front-of-queue reroll) leads, then the ONE unified buff
        // list walked in priority order — self bless / when-full, whole-party, and
        // per-member buffs together.
        // A staged mana-regen reroll leads — it's an immediate below-threshold recast
        // (front of the queue). Then the ONE unified buff list (self bless / regen /
        // when-full, whole-party, per-member). Mana-regen maintenance is now just a
        // CastOnSelf slot in that list, so PickUnifiedBuff handles it in place.
        if (PickManaRegenReroll(spells, manaBuffsAllowed) is { } rr) return rr;
        return PickUnifiedBuff(spells, health, party, manaBuffsAllowed);
    }

    // Per-slot buff affordability for the buff pickers. A regular spell buff is
    // allowed only when mana-drawing buffs clear the BlessIfAboveMa floor (the
    // specific cast's cost is re-checked at dispatch); an item-cast token is allowed
    // when its use-spell is free (cost 0 / unresolved — recast regardless of mana)
    // or, when it draws mana, only if we both clear the floor and can pay the cost
    // from the current pool.
    private bool IsBuffAffordable(string slot, bool manaBuffsAllowed)
    {
        if (!ItemCastToken.IsToken(slot)) return manaBuffsAllowed;
        int? cost = _itemCastManaCost?.Invoke(slot);
        if (cost is not > 0) return true; // free / unresolved item-cast: never mana-gated
        return manaBuffsAllowed && _state.Ma >= cost.Value;
    }

    // A staged mana-regen reroll — an immediate below-threshold recast that jumps the
    // whole buff queue (bypasses the slot's recast timer) but still honours the self-
    // bless timing gates and the buff mana floor. If it can't be paid for right now,
    // drop it (the reroller re-stages on the next landing if still below threshold)
    // rather than stall the walk. The mana-regen buff's own MAINTENANCE recast is now
    // a normal CastOnSelf slot in the unified list (PickUnifiedBuff handles it).
    private CastCandidate? PickManaRegenReroll(SpellsSettings spells, bool manaBuffsAllowed)
    {
        if (!SelfBuffTimingAllowed(spells)) return null;
        if (_pendingManaRegenReroll is not { } reroll) return null;
        if (IsBuffAffordable(reroll, manaBuffsAllowed))
            return new CastCandidate(reroll, Target: null, DefaultRecastMarginSec);
        _pendingManaRegenReroll = null;
        return null;
    }

    // Self-buff timing gate shared by the mana-regen path and the CastOnSelf targets
    // in the unified walk: allowed unless we're in combat without SelfBlessDuringCombat,
    // or in a TRIGGERED recovery rest without SelfBlessWhileResting. Idle / standing /
    // idly-resting is always allowed. Both toggles default OFF.
    private bool SelfBuffTimingAllowed(SpellsSettings spells)
    {
        if (_state.InCombat && !spells.SelfBlessDuringCombat) return false;
        if ((_isTriggeredRest?.Invoke() ?? false) && !spells.SelfBlessWhileResting) return false;
        return true;
    }

    // Walk the ONE unified buff list (CharacterProfile.PartyBuffs) in priority order
    // and pick the first due buff to cast. A slot can target ourselves (CastOnSelf),
    // the whole party in one cast (Targets 10 / 13, WholePartyOn — lands on us too),
    // and/or selected members (Targets 2). Self targets obey the self-bless timing
    // gates (SpellsSettings); party / member targets obey the party-bless gates
    // (PartySettings) and require actually being in a party. Per-slot conditions
    // (OnlyWhenHpFull / OnlyWhenMaFull) must be met for the slot to fire.
    //
    // A member is eligible only when in the party (MajorMUD parties are co-located, so
    // a roster name is in the room) — the one exception being a member who's HIDING:
    // the cast returns "You do not see <name> here!" and we back off (_hiddenTargets)
    // until we move or they reappear in "Also here:".
    private CastCandidate? PickUnifiedBuff(SpellsSettings spells, HealthSettings health, PartySettings? party, bool manaBuffsAllowed)
    {
        if (_readPartyBuffs?.Invoke() is not { } buffs) return null;

        bool selfAllowed = SelfBuffTimingAllowed(spells);
        bool triggeredRest = _isTriggeredRest?.Invoke() ?? false;

        // "When HP / MA full" fires at the REST-MAX target (the level we rest up to,
        // HealthSettings.RestMaxHp / RestMaxMa read per the threshold mode), not literal
        // 100% — so a buff meant for "topped off, ready for the next fight" triggers as
        // soon as a recovery rest finishes.
        int restMaxHp = PoolThreshold.Resolve(health.HpThresholdMode, health.RestMaxHp, _state.MaxHp);
        int restMaxMa = PoolThreshold.Resolve(health.MaThresholdMode, health.RestMaxMa, _state.MaxMa);

        // Party / member targets are only cast while actually in a party, gated by
        // the Settings → Party toggles (default OFF → hold in combat / triggered rest).
        bool inParty = _party?.IsInParty == true;
        bool partyAllowed = inParty
            && !(_state.InCombat && !(party?.BlessDuringCombat ?? false))
            && !(triggeredRest && !(party?.BlessWhileResting ?? false));

        // In a party, a buff a configured party-wide buff removes (e.g. chant removes
        // bless) is left to that party buff — skip self-casting the superseded spell.
        IReadOnlyDictionary<string, string>? covered = _selfBuffCoverage?.Invoke();

        foreach (Models.Profile.BuffSlot slot in buffs.Slots)
        {
            if (string.IsNullOrWhiteSpace(slot.Spell)) continue;

            // Only-when-dark light spells are cast reactively by the auto-light system
            // on entering a dark room, not maintained here — skip them entirely.
            if (slot.OnlyWhenDark) continue;

            if (!IsBuffAffordable(slot.Spell, manaBuffsAllowed)) continue;

            // Per-slot conditions: the matching pool must be topped off to the rest-max
            // target for the "when full" slots.
            if (slot.OnlyWhenHpFull && !(_state.MaxHp > 0 && _state.Hp >= restMaxHp)) continue;
            if (slot.OnlyWhenMaFull && !(_state.MaxMa > 0 && _state.Ma >= restMaxMa)) continue;

            bool isItem = ItemCastToken.IsToken(slot.Spell);
            bool partyWide = _isPartyWideBuff?.Invoke(slot.Spell) == true;

            // Whole-party buff — one cast blankets the party (and us). Recast keyed to
            // self ("") since it confirms under its own cast code. An item-cast buff
            // can only be whole-party (`use` takes no target).
            if (partyWide)
            {
                if (!slot.WholePartyOn) continue;
                if (!partyAllowed) continue;
                if (!IsRecastDue("", slot.Spell)) continue;
                return new CastCandidate(slot.Spell, Target: null, slot.RecastMarginSec);
            }

            // "Cast before resting for mana": keep the regen buff up (recast on expiry)
            // only while the mana-rest lock is held — from when mana drops below its rest
            // trigger until it tops back up to rest-max — so the buff boosts the rest and
            // then stops. The lock is mana-specific and combat-durable: a mob walking in
            // mid-rest doesn't drop it, so the buff stays up through that combat until mana
            // recovers. Unchecked, the slot is maintained always-up via the normal self
            // gate. (Left unwired — tests — the lock reads false, so the slot never fires.)
            bool selfEligible = slot.CastBeforeRestingForMana
                ? (_isManaRestActive?.Invoke() ?? false)
                : selfAllowed;

            // Self target (CastOnSelf) — self-gated, keyed "". Works for a spell OR a
            // self item-cast (`use <item>`, whose buff lands on us).
            if (slot.CastOnSelf && selfEligible
                && (covered is null || !covered.ContainsKey(slot.Spell))
                && IsRecastDue("", slot.Spell))
                return new CastCandidate(slot.Spell, Target: null, slot.RecastMarginSec);

            // Member targets (single-target spell) — party-gated, one per pass. An item
            // token can't be aimed at a member (`use` takes no target), so skip it here.
            if (!isItem && partyAllowed && _party is not null)
            {
                foreach (PartyMember m in _party.Members)
                {
                    if (m.IsSelf) continue;
                    // Given name only: MajorMUD targets by first name token, and the
                    // recast key must match the target we stash for confirmation.
                    string given = GivenName(m.Name);
                    string key = given.ToLowerInvariant();
                    if (!slot.AllMembers && !slot.Targets.Contains(key)) continue;
                    if (_hiddenTargets.Contains(key))
                    {
                        if (_isMemberInRoom?.Invoke(given) != true) continue;
                        _hiddenTargets.Remove(key);
                    }
                    if (!IsRecastDue(key, slot.Spell)) continue;
                    return new CastCandidate(slot.Spell, given, slot.RecastMarginSec);
                }
            }
        }
        return null;
    }

    // Arm the pending party-buff confirmation: resolve the buff's CasterMessage
    // template + duration and stash it so the next matching server line starts the
    // duration timer keyed to the target. Clears any prior pending cast
    // (CastCoordinator's cooldown guarantees <=1 in flight). No-op (clears pending)
    // when the buff has no resolvable caster template.
    private void ArmPartyBuffConfirm(string shortCode, string target, int marginSec)
    {
        _pendingPartyCast = null;
        // An engine cast is the authority — supersede any hand-cast confirm the wire
        // observer just armed for the same send (engine casts flow through it too).
        _pendingManualCast = null;
        if (_buffInfoByShort?.Invoke(shortCode) is not { } info) return;
        if (CasterMessageMatcher.TryCreate(info.Caster) is not { } matcher) return;
        _pendingPartyCast = (shortCode, target, info.DurationSec, marginSec, matcher);
    }

    // ----- Debuffing — sourced from the combat engine -----------------
    // The combat engine owns the debuff DECISION (config + once-per-room /
    // once-per-target gating in CombatSpellChooser); we just cast it through
    // the shared in-between window at the user's PriorityDebuffing rank so it
    // competes with survival casts. The bridge is wired by
    // SetCombatDebuffSource; until then this is a no-op.
    private CastCandidate? PickDebuff()
    {
        if (_combatDebuffSource?.Invoke() is not { } debuff) return null;
        if (string.IsNullOrWhiteSpace(debuff.Spell)) return null;
        return new CastCandidate(debuff.Spell, debuff.Target);
    }

    // ----- Party-member HP watch (re-evaluate on a member's HpPercent change) ----

    private readonly HashSet<PartyMember> _watchedMembers = new();

    private void OnPartyMembersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (PartyMember m in e.OldItems) UnwatchMember(m);
        if (e.NewItems is not null) foreach (PartyMember m in e.NewItems) WatchMember(m);
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (PartyMember m in _watchedMembers) m.PropertyChanged -= OnMemberPropertyChanged;
            _watchedMembers.Clear();
            if (_party is not null) foreach (PartyMember m in _party.Members) WatchMember(m);
        }
    }

    private void WatchMember(PartyMember m)
    {
        if (_watchedMembers.Add(m)) m.PropertyChanged += OnMemberPropertyChanged;
    }

    private void UnwatchMember(PartyMember m)
    {
        if (_watchedMembers.Remove(m)) m.PropertyChanged -= OnMemberPropertyChanged;
    }

    private void OnMemberPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A member's HP moved — re-run the cast pipeline so a party heal fires this
        // round, not next. Evaluate() still honours the one-cast-per-round limit, so
        // an already-spent between-round slot correctly defers to the next round.
        if (e.PropertyName == nameof(PartyMember.HpPercent)) Evaluate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.PropertyChanged -= OnStateChanged;
        if (_party is not null)
        {
            _party.Members.CollectionChanged -= OnPartyMembersChanged;
            foreach (PartyMember m in _watchedMembers) m.PropertyChanged -= OnMemberPropertyChanged;
            _watchedMembers.Clear();
        }
        _cast.CastFailed -= OnCastFailed;
        if (_conditions is not null)
        {
            _conditions.ConditionApplied -= OnConditionApplied;
            _conditions.ConditionEnded -= OnConditionEnded;
        }
        if (_lines is not null)
            _lines.LineEmitted -= OnLine;
    }
}

// One picked cast — spell name + optional target string. Used internally by
// CastingDirector to thread through the unified Pick* → TryCast pipeline.
// RecastMarginSec carries the buff slot's recast lead through to the timer the
// Buffing branch arms; it's meaningless (and ignored) for non-buff picks, which
// leave it at the shared default.
public readonly record struct CastCandidate(
    string Spell, string? Target,
    int RecastMarginSec = SpellsSettings.DefaultBlessRecastMarginSec);

// Spell-decision categories. Order matches the user-facing Spells settings tab;
// numeric position is just for deterministic tiebreak when two priority slots share
// the same int.
public enum SpellCategory
{
    MinorPartyHeal = 0,
    MajorPartyHeal = 1,
    MinorSelfHeal  = 2,
    MajorSelfHeal  = 3,
    Curing         = 4,
    Buffing        = 5,
    Debuffing      = 6,
    // Not user-orderable — a downed ally is a life-critical rescue that always
    // outranks every other cast, so PrioritisedCategories emits it first
    // unconditionally rather than reading a priority slot.
    DownedAllyHeal = 7,
}
