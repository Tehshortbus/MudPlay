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
    private Func<bool>? _inputCaptured;
    private Func<bool>? _buffStripRoom;
    private Func<bool>? _tokenBuffPause;
    // True when sneak-maintenance casts (buffs + cures) should be HELD for the
    // next empty room: auto-sneak is on, auto-combat won't clear the current room,
    // and an NPC is present. Casting breaks sneak (GAME_MECHANICS) and you can't
    // re-sneak with an NPC in the room, so a stealth runner defers the cast until
    // a room it can cast in and re-sneak. Null / unwired → never defer (tests +
    // non-stealth play behave exactly as before). Emergency survival casts are
    // never in this set.
    private Func<bool>? _deferMaintenanceWhileStealthed;
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
    // In-game latch — the socket flag above is true again the instant a reconnect's
    // TCP connect lands, which is the BBS login/menu screen, NOT the game world. The
    // 1s heartbeat never stops and HasPromptData/Hp stay stale-true across a drop, so
    // an ungated loop drains buffs onto the login prompts during re-entry (report
    // paradigm-20260908-053448). Set true on any disconnect (PauseBuffTimers), cleared
    // on the first real in-game prompt after reconnect (ResumeBuffTimers, fired off
    // PromptScanner.PromptObserved which never matches the BBS menu). Mirrors the same
    // fix already shipped for the party poller's par/@health telepaths.
    private bool _suspended;
    // Edge-log guard for the AttackPrevented hold below (AttacksPrevented) — mirrors
    // CombatManager._attackBlockLogged so a report shows exactly when the hold began
    // and lifted instead of a line per Evaluate().
    private bool _conditionBlockLogged;
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

    // When we last cast a between-round spell (heal / cure / buff / debuff / item).
    // The game allows only ONE 0-energy between-round cast per round across all of
    // them — a second draws "You have already cast a spell this round!" and does NOT
    // fire. The between-round cycle runs on the SAME ~5s cadence as combat rounds and
    // applies WHETHER OR NOT WE ARE IN COMBAT (confirmed 2026-09-16, user), so the slot
    // is time-scoped, not gated on _state.InCombat: it's "spent" for RoundWindow after a
    // cast. Freed early at the true round boundary (NotifyRoundComplete, wired to
    // TickEngine.CombatTickElapsed — NOT *Combat Off*, which fires per kill and would
    // re-open the slot mid-round in a multi-mob fight); out of combat, where no tick
    // fires, the RoundWindow lapse frees it. The old InCombat gate masked the slot false
    // during the brief between-kill *Combat Off* flicker, so a freshly-arrived monster's
    // pre-attack debuff re-fired into a spent round and its rejection latched the block
    // that then delayed the coupled attack a whole round (report paradigm-20260916-074131).
    private DateTime _betweenRoundSlotUsedAt = DateTime.MinValue;

    // The between-round / combat-round cadence — one 0-energy cast per this window.
    private static readonly TimeSpan RoundWindow = TimeSpan.FromSeconds(5);

    // How recent a between-round send has to be for an AttackPrevented apply to be
    // treated as the block that swallowed IT specifically, rather than an unrelated
    // later hit (see OnConditionApplied). Same-burst collisions land within a line
    // or two of each other — well under a second in practice — so this stays far
    // short of RoundWindow to avoid misfiring on a stun that lands seconds after an
    // already-confirmed cast.
    private static readonly TimeSpan CollisionWindow = TimeSpan.FromSeconds(2);

    // True while this round's single between-round slot is spent (a cast landed within
    // the current round window and no round tick has freed it since).
    private bool SlotSpentThisRound => _now() - _betweenRoundSlotUsedAt < RoundWindow;

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
    // Given a cast code, the cast codes of the buffs its spell removes (RemovesSpell).
    // Used to re-attribute a wear-off that lands right after a clobbering cast to its
    // VICTIM rather than the just-cast survivor (bless & chant share the wear-off line).
    private Func<string, IReadOnlyCollection<string>>? _removesShortsFor;
    private Func<string, bool>? _isPartyWideBuff;

    // The last buff we SUCCESSFULLY cast (identity known independent of any shared
    // condition message) and when. A wear-off arriving within ClobberWindow of a cast
    // that removes other buffs is that victim's wear-off, not the caster's.
    private string? _lastCastShort;
    private DateTime _lastCastAt;
    private static readonly TimeSpan ClobberWindow = TimeSpan.FromSeconds(5);

    // The self-buff last confirmed via an applied line, and when. The applied line is
    // many-to-one — one "you feel lucky" matches several buff records (bless, chant,
    // glass orb, …), so OnConditionApplied fires once PER record. We confirm exactly ONE
    // buff per burst (records within AppliedBurstWindow) and ignore the rest, so casting
    // bless doesn't also refresh chant's timer off the shared line.
    private string? _appliedBurstShort;
    private DateTime _appliedBurstAt;
    private static readonly TimeSpan AppliedBurstWindow = TimeSpan.FromMilliseconds(400);
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
    // Configured buffs another configured buff PERMANENTLY removes (Paradigm continuous
    // removal, one-directional): loser cast code → winning buff name. Never maintained (the
    // winner keeps stripping them), for ANY target; the watchdog labels them "covered by".
    private Func<IReadOnlyDictionary<string, string>>? _suppressedBuffs;
    // STOCK one-directional conflicts: loser cast code → remover cast code. Both are
    // maintained (not suppressed); PickUnifiedBuff orders each remover before the losers it
    // removes so the at-cast strip doesn't knock them off. Empty off stock.
    private Func<IReadOnlyDictionary<string, string>>? _collisionOrder;
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

    // Wire the token buff-pause gate. When the predicate returns true, a Paradigm
    // transport-token use is imminent (or in flight) — its negate magic will wipe every
    // buff — so the Buffing category is held until the use fires or the window times
    // out. Same rationale as the buff-strip-room gate; heals / cures / debuffs run
    // normally. Optional — until wired, buffs fail open.
    public void SetTokenBuffPauseGate(Func<bool> isTokenUseImminent)
    {
        ArgumentNullException.ThrowIfNull(isTokenUseImminent);
        _tokenBuffPause = isTokenUseImminent;
    }

    // Wire the sneak-maintenance defer gate. When the predicate returns true
    // (auto-sneak on + auto-combat not clearing this room + an NPC present), the
    // Buffing and Curing categories are HELD rather than cast — a stealth runner
    // walking combat-off waits for an empty room so the cast (which breaks sneak)
    // can be followed by a re-sneak, instead of stripping sneak in a room it's only
    // passing through. Optional — until wired, maintenance never defers. The hold
    // is additionally skipped while resting / meditating (a stationary recovery
    // should cast normally) — that guard lives at the decision pass.
    public void SetStealthMaintenanceDeferGate(Func<bool> shouldDefer)
    {
        ArgumentNullException.ThrowIfNull(shouldDefer);
        _deferMaintenanceWhileStealthed = shouldDefer;
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
    // removesShortsFor (optional): given a cast code, the cast codes of the buffs its
    // spell removes (RemovesSpell) — lets a wear-off that lands right after a clobbering
    // cast be attributed to its victim rather than the just-cast survivor.
    public void SetBuffDurationSources(
        Func<string, (string Caster, long DurationSec)?> buffInfoByShort,
        Func<MessageRecord, string?> shortFromAppliedRecord,
        Func<string, IReadOnlyCollection<string>>? removesShortsFor = null)
    {
        ArgumentNullException.ThrowIfNull(buffInfoByShort);
        ArgumentNullException.ThrowIfNull(shortFromAppliedRecord);
        _buffInfoByShort = buffInfoByShort;
        _shortFromAppliedRecord = shortFromAppliedRecord;
        _removesShortsFor = removesShortsFor;
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

    // Wire the permanently-suppressed-buff source: loser cast code → winning buff name for
    // one-directional conflicts (Paradigm continuous removal). PickUnifiedBuff skips a
    // suppressed slot for any target so we never waste rounds casting a buff the winner
    // re-strips; the Buff Watchdog labels it "covered by".
    public void SetSuppressedBuffs(Func<IReadOnlyDictionary<string, string>> suppressed)
    {
        ArgumentNullException.ThrowIfNull(suppressed);
        _suppressedBuffs = suppressed;
    }

    // The current permanently-suppressed-buff map (loser code → winning buff name) — the
    // Buff Watchdog reads this to label a suppressed buff "covered by". Empty off Paradigm
    // or unwired.
    public IReadOnlyDictionary<string, string> CurrentSuppressedBuffs()
        => _suppressedBuffs?.Invoke() ?? _emptyCoverage;

    // Wire the STOCK collision-free ordering source: loser cast code → remover cast code for
    // one-directional conflicts, where (unlike Paradigm) removes fire only at cast so both
    // can coexist if the remover is cast first. PickUnifiedBuff re-sorts the priority list so
    // each remover precedes the losers it removes; the loser is still maintained (not
    // suppressed), and the clobber-clear re-applies it after each remover recast. Empty off
    // stock (the Paradigm branch suppresses instead — SetSuppressedBuffs).
    public void SetCollisionOrder(Func<IReadOnlyDictionary<string, string>> collisionOrder)
    {
        ArgumentNullException.ThrowIfNull(collisionOrder);
        _collisionOrder = collisionOrder;
    }

    // The current stock collision-order map (loser code → remover code) — the Buff Watchdog
    // reads this to annotate a one-way loser "both kept" instead of flagging a conflict.
    // Empty off stock or unwired.
    public IReadOnlyDictionary<string, string> CurrentCollisionOrder()
        => _collisionOrder?.Invoke() ?? _emptyCoverage;

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
        _betweenRoundSlotUsedAt = DateTime.MinValue;
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

    // Freeze the live buff timers on a disconnect — record when so the reconnect resumes
    // them intact. Used INSTEAD of clearing on ANY disconnect (the buffs persist server-
    // side through link-death and an auto-reconnect is coming); on resume, only the timers
    // whose real expiry lapsed while we were away drop and recast. A fresh character
    // (ProfileLoaded → ResetBuffTracking) is the only path that wipes everything.
    public void PauseBuffTimers()
    {
        // Latch the loop off until we're confirmed back in the game world — set
        // unconditionally (even with no armed timers) so a disconnect with nothing
        // active still can't cast into the login/menu. Cleared in ResumeBuffTimers.
        _suspended = true;
        _pausedAt = _activeUntil.Count > 0 ? _now() : null;
        if (_pausedAt is not null)
            _log?.Info(LogCategory, $"buff timers paused (drop) — {_activeUntil.Count} armed, frozen until reconnect");
    }

    // Resume after a reconnect. Buffs persist server-side through a link-death + auto-
    // reconnect, so a brief hang must NOT rebuff — we keep BOTH self and party timers
    // across the gap and let each ride its real (absolute) expiry. Party members stayed
    // ONLINE, so theirs already read the correctly reduced remaining; ours were frozen
    // for the display only (PausedAtUtc), never shifted, so on resume they too reflect
    // the true elapsed time. Any timer whose absolute expiry passed while we were away —
    // self or party — is dropped so it shows "not up" and recasts; the rest stay, so the
    // client doesn't recast buffs that are still up. (Self timers used to be wiped here
    // wholesale, forcing a full rebuff on every re-entry — report paradigm-20260908-205555.)
    public void ResumeBuffTimers()
    {
        // First in-game prompt after a (re)connect — lift the cast-suspend latch
        // BEFORE the no-timers early-return, so casting always resumes even when the
        // disconnect had no armed timers to unfreeze. Fired on every prompt; harmless
        // to clear repeatedly.
        _suspended = false;
        if (_pausedAt is null) return;
        _pausedAt = null;

        List<(string Target, string Short)>? expired = null;
        foreach ((string Target, string Short) key in _activeUntil.Keys)
            if (_activeUntil[key].Until <= _now())
                (expired ??= new()).Add(key);
        if (expired is not null)
            foreach ((string, string) key in expired) _activeUntil.Remove(key);

        _log?.Info(LogCategory,
            $"buff timers resumed — kept across the drop; dropped {expired?.Count ?? 0} that lapsed while offline.");
    }

    // A combat round tick elapsed (wired to TickEngine.CombatTickElapsed) — free the
    // between-round cast slot so the new round can cast once. Keyed to the combat
    // round cadence, NOT *Combat Off*: *Combat Off* fires per kill, so in a multi-mob
    // room it would re-open the slot several times a round and let the storm back in.
    public void NotifyRoundComplete() => _betweenRoundSlotUsedAt = DateTime.MinValue;

    // An external between-round cast — the combat engine's pre-attack debuff, which
    // fires directly rather than through Evaluate — just went out. Spend this round's
    // single between-round slot so Evaluate won't queue a second one and draw "You have
    // already cast a spell this round!". Freed at the round tick (NotifyRoundComplete)
    // or after RoundWindow. Stamped regardless of _state.InCombat — the per-round cap
    // applies whether or not the client thinks it's in combat (and the pre-attack debuff
    // fires during the between-kill *Combat Off* flicker where InCombat reads false).
    public void MarkBetweenRoundSlotUsed() => _betweenRoundSlotUsedAt = _now();

    // True when this round's single between-round cast is already spent — the same
    // predicate Evaluate gates on (so a null from Evaluate means "slot gone", not
    // "nothing due"). The combat engine reads this before a pre-attack debuff so it
    // won't fire a doomed cast into a spent slot.
    public bool BetweenRoundSlotUsed => SlotSpentThisRound;

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
            _betweenRoundSlotUsedAt = _now();
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
        // A between-round cast can be swallowed by a stun/petrify/bind that applies a
        // line LATER in the same round's burst than the send: the tick-driven pass
        // that fires the cast runs straight off a server combat line (OnCombatTick,
        // "before the round's prompt refreshes HP") — so it can run, and send, before
        // a stun a line further down that same burst has even been parsed, racing
        // right past the AttacksPrevented gate above. Recognize the after-the-fact
        // evidence here: an AttackPrevented apply landing within CollisionWindow of
        // our own between-round send is almost certainly the block that ate it, so
        // release both cooldowns immediately instead of stranding the round's one
        // cast slot until the condition happens to outlast the normal ~5.5s cooldown
        // on its own. A false positive here just costs one extra between-round
        // attempt, harmlessly caught by the server's own "already cast a spell this
        // round!" pattern if the swallowed cast actually landed.
        if (r.Flags.HasFlag(MessageFlags.AttackPrevented)
            && SlotSpentThisRound
            && _now() - _betweenRoundSlotUsedAt <= CollisionWindow)
        {
            _betweenRoundSlotUsedAt = DateTime.MinValue;
            _cast.ReleaseBetweenRoundCooldown();
            // A swallowed self-heal never moved Hp/Ma, so IsStaleSelfHealRepeat would
            // otherwise read the retry as the SAME stale-pool re-evaluation its 8s
            // guard exists to suppress (a heal that landed but whose confirm the
            // client hasn't parsed yet) and hold it for the rest of that window —
            // comfortably outlasting a 4-5s stun and silently eating the retry this
            // whole fix exists to enable. This cast never landed at all, so the guard
            // has nothing to protect here; drop it so the retry isn't mistaken for a
            // duplicate of a cast that in fact never happened.
            _lastSelfHealCast = null;
            _log?.Combat(LogCategory,
                "between-round cast released — AttackPrevented landed right after the send");
        }

        // A self-cast buff confirmed via its AppliedMessage — start (or refresh) its
        // duration timer keyed to self so the recast window is honoured. Party-cast
        // confirmation rides OnLine instead.
        //
        // The applied/condition line is SHARED and MANY-TO-ONE: a single "You feel lucky"
        // matches several records (bless, chant, glass orb, dark blessing on Paradigm),
        // so this handler fires once PER matched record. Only the buff we actually cast
        // is really ours. While a fresh pending self-buff exists (armed on send, same
        // staleness window OnSelfBuffRejected uses), attribute the WHOLE burst to it:
        // refresh the pending buff and IGNORE every sibling record — otherwise casting
        // bless also refreshes chant / glass orb off the shared line. With nothing fresh
        // pending, trust the record map (e.g. the reactive HP-regen HoT has no pending).
        bool pendingFresh = _pendingSelfBuffShort is not null
            && _now() - _pendingSelfBuffArmedAt <= PendingSelfBuffRejectionWindow;

        // The buff this event would confirm: the one we actually SENT (fresh pending)
        // regardless of which shared record fired, else whatever the record maps to
        // (e.g. the reactive HP-regen HoT, which has no pending).
        string? shortCode = pendingFresh ? _pendingSelfBuffShort : _shortFromAppliedRecord?.Invoke(r);
        if (shortCode is null) { Evaluate(); return; }

        // One shared "you feel lucky" matches many records, firing this once per record.
        // Confirm ONLY the first buff of the burst; a different short within the window is
        // a sibling of the same line → ignore (so bless's line doesn't refresh chant too).
        if (_appliedBurstShort is not null
            && _now() - _appliedBurstAt <= AppliedBurstWindow
            && !string.Equals(shortCode, _appliedBurstShort, StringComparison.OrdinalIgnoreCase))
        {
            Evaluate();
            return;
        }

        if (_buffInfoByShort?.Invoke(shortCode) is { } info)
        {
            // Preserve the recast lead armed on send (StartSelfBuffTimer ran first for a
            // bless-slot cast); default it for anything confirmed without a prior
            // optimistic timer (e.g. the HP-regen HoT).
            int margin = _activeUntil.TryGetValue(("", shortCode), out (DateTime Until, int MarginSec, int TotalSec) prev)
                ? prev.MarginSec
                : DefaultRecastMarginSec;
            _activeUntil[("", shortCode)] = (_now().AddSeconds(info.DurationSec), margin, (int)info.DurationSec);
            NoteSuccessfulCast(shortCode);
            _appliedBurstShort = shortCode;
            _appliedBurstAt = _now();
            _log?.Combat(LogCategory,
                $"self-buff {shortCode} confirmed active (applied line) — "
                + $"duration {info.DurationSec}s, recast in {Math.Max(0L, info.DurationSec - margin)}s");
        }
        // Landed — the real duration timer is authoritative, so the optimistic pending
        // marker mustn't later be treated as an unlanded cast (a subsequent cast failure
        // must not drop THIS timer). Trailing siblings are handled by the burst guard.
        if (_pendingSelfBuffShort == shortCode) _pendingSelfBuffShort = null;
        // NOTE: the mana-regen reroll engine is fed off the SEND path (StartSelfBuffTimer),
        // NOT here — a roll spell confirms via the shared "mana regenerating" condition,
        // which can't be mapped back to the specific spell (paradigm-20260830-110918).
        Evaluate();
    }

    // Remember the buff we just SUCCESSFULLY cast, keyed off an identity we KNOW (the
    // party-cast confirm names the spell, a self-buff carries its pending short) rather
    // than a shared condition message. Lets OnConditionEnded tell a clobber victim's
    // wear-off from the caster's own.
    private void NoteSuccessfulCast(string shortCode)
    {
        if (string.IsNullOrEmpty(shortCode)) return;
        _lastCastShort = shortCode;
        _lastCastAt = _now();

        // A landed buff clobbers the buffs its spell removes (RemovesSpell) — the game
        // strips them, so drop any active timer we still hold for one. A stripped buff
        // must not keep reading as if it's up (report paradigm-20260910-001023: chan
        // removes gbls, yet gbls's timer kept ticking). Keyed off the deterministic
        // removes relationship, NOT the ambiguous shared wear-off line, so it clears
        // the RIGHT buff; the clobber conflict is still surfaced by the config-side ⚠.
        if (_removesShortsFor?.Invoke(shortCode) is { Count: > 0 } victims)
            foreach (string victim in victims)
                ClearTimersForShort(victim, clobberedBy: shortCode);
    }

    // Remove every active timer (self + any member) whose cast code matches — used when
    // a landing buff strips it everywhere it was up. Skips the caster itself so a spell
    // that lists its own family can never wipe the timer it just armed.
    private void ClearTimersForShort(string shortCode, string clobberedBy)
    {
        if (string.IsNullOrWhiteSpace(shortCode)
            || string.Equals(shortCode, clobberedBy, StringComparison.OrdinalIgnoreCase))
            return;

        // The clobbering buff strips this one in-game, so release its applied-latch in
        // the ConditionTracker too — not just the timer below. A latched applied line
        // is deduped, so if the clobbered buff is later RE-CAST its confirm would never
        // re-fire, and that re-cast could then never drive its own clobber-clear
        // (report paradigm-20260910-012303: a re-cast greater bless never dropped an
        // active chant because gbls stayed latched from before chant clobbered it).
        if (_conditions is not null && _shortFromAppliedRecord is { } resolve)
            _conditions.ReleaseApplied(rec =>
                string.Equals(resolve(rec), shortCode, StringComparison.OrdinalIgnoreCase));

        List<(string Target, string Short)>? doomed = null;
        foreach ((string Target, string Short) key in _activeUntil.Keys)
            if (string.Equals(key.Short, shortCode, StringComparison.OrdinalIgnoreCase))
                (doomed ??= new()).Add(key);
        if (doomed is null) return;
        foreach ((string, string) key in doomed) _activeUntil.Remove(key);
        _log?.Combat(LogCategory,
            $"buff {shortCode} clobbered by {clobberedBy} (removes) — {doomed.Count} timer(s) cleared");
    }

    private void OnConditionEnded(MessageRecord r)
    {
        string? resolved = _shortFromAppliedRecord?.Invoke(r);

        // A wear-off that lands right after a SUCCESSFUL clobbering cast is the shared,
        // ambiguous side of that clobber: bless & chant share the wear-off message, so
        // "the effects of bless wear off" here could resolve to the just-cast SURVIVOR
        // and wrongly clear its fresh timer. Ignore it — the clobbered victim's own
        // timer was already dropped deterministically at the clobbering cast's landing
        // (NoteSuccessfulCast → ClearTimersForShort), so this guard's only remaining job
        // is protecting the survivor. A genuine later wear-off falls outside the window
        // and clears normally below.
        if (resolved is not null
            && _lastCastShort is { } caster
            && _now() - _lastCastAt <= ClobberWindow
            && _removesShortsFor?.Invoke(caster) is { Count: > 0 } victims)
        {
            HashSet<string> pair = new(victims, StringComparer.OrdinalIgnoreCase) { caster };
            if (pair.Contains(resolved))
            {
                _log?.Combat(LogCategory,
                    $"wear-off ({resolved}) ignored — shared clobber line from just-cast {caster}; "
                    + "survivor + victim timers left intact for the conflict display");
                Evaluate();
                return;
            }
        }

        // Server-confirmed early wear-off — drop the self timer so the next
        // pass re-attempts immediately rather than waiting out a stale clock.
        if (resolved is { } shortCode && _activeUntil.Remove(("", shortCode)))
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
        NoteSuccessfulCast(p.Short);   // the "You cast <spell> on …" confirm names it reliably
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
        NoteSuccessfulCast(man.Short);   // the resolved "You cast <spell> on …" names it reliably

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
        // Dropped, or reconnected but still at the BBS login/menu — the socket flag
        // above lies (true from TCP connect onward), so hold every cast until the
        // first in-game prompt clears the latch. Without this, buffs drain onto the
        // login prompts during re-entry (report paradigm-20260908-053448).
        if (_suspended) return null;
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
        // Stun / petrify / bind refuse EVERY cast, not just attacks — a between-round
        // heal sent into this window is silently swallowed (no mana spent, no confirm
        // line), yet CastCoordinator still stamps its round cooldown as if it landed,
        // stranding the slot for a full round even after the condition clears (an
        // emergency heal lost its retry window and the character died mid-stunlock).
        // Hold here instead, same as CombatManager.AttacksBlocked, so the slot is
        // still free the instant ConditionEnded's re-evaluate fires. Corrects the
        // GAME_MECHANICS.md note that only attacks were governed by AttackPrevented —
        // a between-round self-heal is refused by it too.
        if (AttacksPrevented()) return null;

        // Emergency is the last-resort self-save: once it's due, nothing client-side
        // may hold it back — not the attack-owed alternation debt below, not the
        // between-round pacing cooldown, not the stale-repeat duplicate guard that
        // exists for a different scenario entirely. Those all pace OTHER categories
        // against each other; Emergency skips the pacing, not the game's own rules —
        // a genuine server rejection (no mana, a round the server says is already
        // spent) still lands and is handled the normal way through TryCast, this
        // only removes SELF-imposed waiting when nothing about the server actually
        // blocks it. AttacksPrevented above still applies to Emergency too: that one
        // is a real server block (stunned/petrified/bound refuses EVERY cast, no
        // exceptions), so bypassing it would only waste the attempt without landing
        // it any sooner — the wait there is unavoidable, not self-imposed. (Session
        // 2026-09-18: dmer sat as the top queued candidate for 5 real seconds at 31%
        // HP, held by attackOwed alone, while the character kept taking hits with a
        // ready heal sitting unused — "nothing else matters" once Emergency is due.)
        if (healRestEnabled && TryFireEmergencyNow() is { } emergencySpell)
            return emergencySpell;

        if (_cast.IsCastBlocked) return null;
        // A prior survival cast already spent a round the combat engine's attack
        // spell was owed — sit out entirely so that resume can reclaim the very
        // next round, rather than re-firing again ourselves the instant HP dips
        // (which it always will while nothing is fighting back). Applies to every
        // OTHER category — Emergency bypasses it above.
        if (_attackOwed?.Invoke() == true) return null;

        // One between-round spell (heal / cure / buff / debuff / item) per combat
        // round: the game allows a single 0-energy cast per round, so a second draws
        // "You have already cast a spell this round!" and does NOT fire. Once this
        // round's slot is spent, suppress further between-round attempts in combat
        // instead of sending doomed casts — the mageshield recast storm came from the
        // engine firing several buffs a round because the per-hit combat tick kept
        // clearing the coordinator's one-per-round cooldown (report
        // paradigm-20260816-101702). The slot frees at the true round boundary
        // (NotifyRoundComplete) or after RoundWindow. The per-round cap applies in AND
        // out of combat (the between-round cycle runs on the same ~5s tick regardless),
        // so the gate is no longer combat-only.
        if (SlotSpentThisRound) return null;

        string? cast = RunDecisionPass(healRestEnabled, blessEnabled);
        // Mark the round's single between-round slot spent so a second Evaluate this
        // round doesn't send another (doomed) cast; freed at the round boundary / window.
        if (cast is not null) _betweenRoundSlotUsedAt = _now();
        return cast;
    }

    // True while an AttackPrevented condition (stun / petrify / bind) is active —
    // see CombatManager.AttacksBlocked, the identical check for the attack slot.
    // Edge-logged so a report shows exactly when the hold began and lifted.
    private bool AttacksPrevented()
    {
        bool blocked = _conditions?.IsAttackPrevented == true;
        if (blocked != _conditionBlockLogged)
        {
            _conditionBlockLogged = blocked;
            _log?.Combat(LogCategory, blocked
                ? "between-round casts held — AttackPrevented condition active"
                : "between-round casts resumed — AttackPrevented condition cleared");
        }
        return blocked;
    }

    // Guards ONLY against the same instant's several reactive Evaluate() calls (an
    // Hp property change and an Ma property change off ONE prompt line each fire
    // their own OnStateChanged) sending the identical emergency cast twice — not a
    // pacing choice like the gates Emergency bypasses, just enough to keep one real
    // trigger from becoming two sends. Comfortably shorter than a combat round.
    private static readonly TimeSpan EmergencyRefireGuard = TimeSpan.FromMilliseconds(800);
    private DateTime _lastEmergencyFireAt = DateTime.MinValue;

    // Fire Emergency ahead of every pacing gate in Evaluate() — see the call site's
    // comment. Still goes through CastCoordinator.TryCast normally (a genuine
    // server rejection — no mana, fizzle, already-cast-this-round — still applies
    // and is handled exactly as any other cast's failure is), so this only removes
    // the SELF-imposed waits: reset the between-round cooldown immediately before
    // the attempt (instead of waiting out its ~5.5s timer) and skip the stale-
    // repeat guard (built for a different scenario — a heal that already landed but
    // whose confirm hasn't parsed yet — which would otherwise read a heal that was
    // truly just BLOCKED, and so never moved Hp/Ma, as a suspicious duplicate).
    private string? TryFireEmergencyNow()
    {
        string? spell = PickEmergencySelfHeal(_readSpells(), _readHealth());
        if (spell is null) return null;
        if (_now() - _lastEmergencyFireAt < EmergencyRefireGuard) return null;
        if (_manaCostLookup?.Invoke(spell) is { } cost && _state.Ma < cost) return null;

        _cast.ReleaseBetweenRoundCooldown();
        if (!_cast.TryCast(spell, target: null, bypassRecastInterval: true)) return null;

        _lastEmergencyFireAt = _now();
        _lastSelfHealCast = (spell, _state.Hp, _state.Ma, _now());
        _betweenRoundSlotUsedAt = _now();
        _log?.Combat(LogCategory,
            $"EmergencyHeal fired (priority bypass — ahead of attack-owed/pacing) "
            + $"spell={spell} hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa}");
        // Same downstream bookkeeping the normal dispatch fires on a successful cast
        // (combat's attack-resume + attack-owed arming, stealth's re-sneak) — this is
        // still a between-round cast, just one that skipped the pacing gates to get here.
        CastFired?.Invoke();
        return spell;
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

        // Sneak-maintenance defer: hold buffs + cures for the next empty room when
        // a stealth runner is walking combat-off through an occupied room (the
        // gate predicate folds auto-sneak + auto-combat-off + NPC-present). Casting
        // breaks sneak and you can't re-sneak with an NPC here, so we wait for a
        // room we can cast in and re-sneak — the re-sneak itself happens on CastFired
        // (StealthManager.ReSneakAfterCast). Skipped while resting / meditating: a
        // stationary recovery has already stopped, so a due cure/buff there should
        // fire (not wait) — and it's the only place a maintenance heal casts anyway.
        // Emergency survival (emergency/major heal / flee / hangup) and combat
        // debuffs are never in the deferred set, so a low-HP character still
        // heals/flees here.
        bool deferSneakMaintenance =
            _deferMaintenanceWhileStealthed?.Invoke() == true
            && _state.Position is not (PlayerPosition.Resting or PlayerPosition.Meditating);

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
                SpellCategory.EmergencyHeal   => healRestEnabled ? Wrap(PickEmergencySelfHeal(spells, health)) : null,
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

            // Sneak-maintenance defer: a buff/cure came due, but we're sneak-walking
            // combat-off through an occupied room — hold it for the next empty room
            // so the cast can be followed by a re-sneak. Logged only here (where a
            // candidate was actually produced) so the hold isn't spammed every poll.
            if (deferSneakMaintenance && category is SpellCategory.Buffing or SpellCategory.Curing)
            {
                _log?.Combat(LogCategory,
                    $"sneak-maintenance held: {cand.Spell} ({category}) — room occupied, waiting for a clear room to cast + re-sneak.");
                continue;
            }

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
                                        or SpellCategory.MajorSelfHeal
                                        or SpellCategory.EmergencyHeal;
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
            string prioLabel = cat switch
            {
                SpellCategory.EmergencyHeal => "emergency",
                SpellCategory.DownedAllyHeal => "rescue",
                _ => p.ToString(),
            };
            q.Add((p, -1, $"{spell.Trim()}({prioLabel})"));
        }
        if (healRestEnabled)
        {
            // Order added here doesn't matter — the queue re-sorts by each
            // category's real priority number below.
            AddSurvival(SpellCategory.EmergencyHeal, PickEmergencySelfHeal(spells, health));
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
    // priorities). Every category — Emergency and DownedAlly included — reads a
    // user-reorderable slot; the defaults put Emergency first (1) and DownedAlly
    // fourth, but the user can move any of them.
    private static int CategoryPriority(SpellsSettings s, SpellCategory cat) => cat switch
    {
        SpellCategory.EmergencyHeal => s.PriorityEmergencyHeal,
        SpellCategory.DownedAllyHeal => s.PriorityDownedAllyHeal,
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
        // Every between-round category, Emergency and DownedAlly included, walked
        // in the user's Spells-tab priority order. Emergency defaults to slot 1
        // (leads), DownedAlly to slot 4, but both are reorderable like the rest.
        (SpellCategory Cat, int Prio)[] order =
        {
            (SpellCategory.EmergencyHeal,  s.PriorityEmergencyHeal),
            (SpellCategory.MinorPartyHeal, s.PriorityMinorPartyHeal),
            (SpellCategory.MajorPartyHeal, s.PriorityMajorPartyHeal),
            (SpellCategory.DownedAllyHeal, s.PriorityDownedAllyHeal),
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
        foreach ((SpellCategory cat, int _) in order) yield return cat;
    }

    // ----- Self heal --------------------------------------------------

    // Last-resort self-save. Deliberately does NOT gate on ManaClearsHealFloor
    // (unlike Major/Minor below) — an emergency spends whatever mana is left
    // rather than conserving the pool for a "later" that might not come, and it
    // fires in ANY state (combat, resting, mid-walk), not just combat/rest like
    // Minor's own position gate. Falls back Emergency → Major → Minor so a
    // player who's only configured the older two tiers still gets a life-threat
    // save at the new, lower trigger once they set EmergencyHealTrigger.
    private string? PickEmergencySelfHeal(SpellsSettings spells, HealthSettings health)
    {
        if (_state.MaxHp <= 0) return null;
        int trigger = ResolveHealHpTrigger(health.HpThresholdMode, health.EmergencyHealTrigger);
        if (_state.Hp > trigger) return null;
        if (!string.IsNullOrWhiteSpace(spells.EmergencyHealSpell)) return spells.EmergencyHealSpell;
        if (!string.IsNullOrWhiteSpace(spells.MajorHealSpell)) return spells.MajorHealSpell;
        return spells.MinorHealSpell;
    }

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
        // Party-heal is a party-only concern. The roster now keeps a lone self row
        // even when solo (PartyWindow self-display) — gate on IsInParty so that row
        // never draws a party-heal cast at the solo player (self-heal owns self).
        if (!_party.IsInParty) return null;
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
        // Buff-strip-room gate: the room casts a buff-removal spell on entry, so
        // any buff we put up is torn straight back off. Skip the whole category
        // here — heals / cures still run their own paths.
        if (_buffStripRoom?.Invoke() == true)
        {
            _log?.Combat(LogCategory, "buff skipped — room strips buffs on entry.");
            return null;
        }

        // Token buff-pause: a transport-token use is imminent and will wipe every buff
        // (negate magic), so don't burn mana putting one up now — it resumes when the
        // use fires or the pause times out.
        if (_tokenBuffPause?.Invoke() == true)
        {
            _log?.Combat(LogCategory, "buff skipped — token use imminent (buffs about to be wiped).");
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

        // A buff another configured buff PERMANENTLY removes (one-directional, Paradigm
        // continuous removal — e.g. greater bless keeps stripping chant) is never
        // maintained on ANY target: the winner re-strips it, so casting it just burns
        // rounds. Empty off Paradigm and for mutual pairs (those are last-cast-wins).
        IReadOnlyDictionary<string, string>? suppressed = _suppressedBuffs?.Invoke();

        // STOCK collision-free ordering: loser cast code → remover cast code. Empty off
        // stock (Paradigm suppresses instead). Applied as a stable reorder below so each
        // remover is cast before the losers it removes — the at-cast strip then fires with
        // the loser not yet up, and the loser (still maintained) is cast right after.
        IReadOnlyDictionary<string, string>? collisionOrder = _collisionOrder?.Invoke();

        // Cast-priority order. Default (PriorityTopDown off) walks the list grouped
        // by type (self → whole-party → item) regardless of how the config rows are
        // arranged; top-to-bottom priority (and only once the user hand-arranged the
        // rows) walks the stored list as-is. Same category definition the config
        // panel groups by — see BuffPriorityOrder.
        IReadOnlyList<Models.Profile.BuffSlot> ordered = BuffPriorityOrder.InPriorityOrder(
            buffs.Slots, buffs.PriorityTopDown, buffs.ManualOrder,
            s => BuffPriorityOrder.Category(
                ItemCastToken.IsToken(s.Spell),
                s.Spell is { } sp && _isPartyWideBuff?.Invoke(sp) == true));

        // Layer the stock remover-before-removed constraint on top of the chosen priority
        // order (no-op off stock / with no one-way pairs). Stable: it only lifts a remover
        // ahead of a loser it would otherwise strip, leaving everything else in place.
        if (collisionOrder is { Count: > 0 })
            ordered = BuffPriorityOrder.OrderRemoversFirst(ordered, collisionOrder);

        foreach (Models.Profile.BuffSlot slot in ordered)
        {
            if (string.IsNullOrWhiteSpace(slot.Spell)) continue;

            // Permanently removed by another configured buff — never maintain it (the
            // winner keeps stripping it). Skips self, member, and whole-party casts alike.
            if (suppressed is not null && suppressed.ContainsKey(slot.Spell)) continue;

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

            // One untargeted, self-landing cast covers a whole-party buff AND a plain
            // self-cast — they're the same command (no target, keyed "" since it confirms
            // under its own cast code), so a single path decides both. A whole-party spell
            // fires only while its master Party toggle (WholePartyOn) is enabled. CastSolo
            // then decides whether that enabled buff also fires while alone — it must never
            // bypass an unchecked master toggle (report paradigm-20260909-220212). A lone
            // character is a party of one, so an enabled whole-party cast still lands on us.
            // A self / single-target spell fires on CastOnSelf. The `covered` supersession
            // skip applies only to a self-cast (a whole-party cast IS the covering buff,
            // never superseded). Self is resolved BEFORE the per-member loop below, so we
            // always bless ourselves first.
            bool wantSelfCast = partyWide
                ? slot.WholePartyOn && (inParty ? partyAllowed : slot.CastSolo && selfEligible)
                : slot.CastOnSelf && selfEligible && (covered is null || !covered.ContainsKey(slot.Spell));
            if (wantSelfCast && IsRecastDue("", slot.Spell))
                return new CastCandidate(slot.Spell, Target: null, slot.RecastMarginSec);

            // Member targets (single-target spell) — party-gated, one per pass. Skipped for
            // a whole-party spell (no aimed form — it only casts untargeted, above) and for
            // an item token (`use` takes no target).
            if (!partyWide && !isItem && partyAllowed && _party is not null)
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
// numeric position is the deterministic tiebreak when two priority slots share
// the same int (PrioritisedCategories sorts by priority, then by this value) —
// so a category's declaration position here IS its tie-break rank. Emergency
// leads (a stale profile from before this category existed can still have its
// old MajorSelfHeal rank sitting on slot 1, colliding with Emergency's new
// default there; the tiebreak — not just the default — must resolve that in
// Emergency's favor), then DownedAllyHeal (a dead caster rescues nobody), then
// the rest in tab order.
public enum SpellCategory
{
    // Last-resort self-save. Reorderable via SpellsSettings.PriorityEmergencyHeal
    // (defaults to slot 1, so it leads). See CastingDirector.PickEmergencySelfHeal.
    EmergencyHeal  = 0,
    // A downed-ally rescue. Reorderable via SpellsSettings.PriorityDownedAllyHeal
    // (defaults to slot 4).
    DownedAllyHeal = 1,
    MinorPartyHeal = 2,
    MajorPartyHeal = 3,
    MinorSelfHeal  = 4,
    MajorSelfHeal  = 5,
    Curing         = 6,
    Buffing        = 7,
    Debuffing      = 8,
}
