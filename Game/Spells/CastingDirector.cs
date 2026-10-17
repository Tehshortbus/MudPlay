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
//     movement-prevented → poison → disease → blindness. A cure ticked "Cure after
//     combat" waits until the fight is over (CureWaitsForFightEnd).
//   - Buffing — recast the unified buff list (CharacterProfile.PartyBuffs).
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
// Master enable flag is AutoActionDefaults.AutoHeal — the active heal-spell side;
// HealthManager's passive rest has its own switch (AutoRest). When
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

    // Self-cure throttle. A condition flag (blindness / poison / disease / hold) clears
    // only on a recognized wear-off line — casting the cure doesn't itself clear it. If
    // the flag is STUCK (a cure that can't remove that source, or a flag falsely latched
    // by a shared applied-line — reports paradigm-20260922-063422 / -095321), the Curing
    // category re-fires the same cure every round and drains mana to zero. This caps the
    // re-cast of the SAME self-cure to once per window; a cure that actually works clears
    // the flag long before then, so the throttle only bites a stuck flag.
    private static readonly TimeSpan SelfCureRetryWindow = TimeSpan.FromSeconds(15);

    // Party-cure back-off. A cure cast at a member clears their chip on the cast line,
    // but the next par poll puts a poison chip straight back when the cure didn't take
    // (a cure-poison spell only lowers the poison by its own value, so a heavy poison
    // outlasts it). Unthrottled, that loop re-cast the cure every 5 s, breaking our rest
    // each time and running mana dry (report paradigm-20261003-131851). Each cast that
    // follows a re-flag doubles the wait, up to PartyCureMaxWindow; the count starts
    // over once the chip has stayed clear for PartyCureSettledAfter.
    private static readonly TimeSpan PartyCureRetryWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PartyCureMaxWindow = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PartyCureSettledAfter = TimeSpan.FromSeconds(12);

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
    // next empty room: auto-sneak is on, and either our move is in flight (a cast
    // would land in the room we're entering) or auto-combat won't clear the current
    // room and an NPC is present. Casting breaks sneak (GAME_MECHANICS) and you can't
    // re-sneak with an NPC in the room, so a stealth runner defers the cast until
    // a room it can cast in and re-sneak. Null / unwired → never defer. Emergency survival casts are
    // never in this set.
    private Func<bool>? _deferMaintenanceWhileStealthed;
    private Func<(string Spell, string? Target)?>? _combatDebuffSource;
    private Action? _combatDebuffCommit;
    private Func<string, int?>? _manaCostLookup;
    private Func<bool>? _autoBlessEnabled;
    private Func<bool>? _isTriggeredRest;
    // True while a MANA-recovery rest is in progress — the mana-rest lock. Asserted
    // when mana drops below its rest trigger and held (through a combat interruption)
    // until mana tops back up to the rest-max target. Gates the "cast before resting
    // for mana" slot: unlike _isTriggeredRest (any recovery rest, drops on combat)
    // this is mana-specific and combat-durable, matching what that flag means. Left
    // unwired, the pre-rest slot falls back to never-eligible (fails closed).
    private Func<bool>? _isManaRestActive;
    // True while the telnet link is up. Null (unwired) = treat as connected.
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
    // round's value. Null (unwired) = treat every tick as fresh.
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
    // TimedFromSend marks a cast whose caster line can't name its target: no line
    // will confirm it, so its timer was armed when it was sent and this entry is
    // kept only to take that timer back if the game refuses the cast. Matcher is
    // null when the spell has no caster line to match at all.
    private (string Short, string Target, long DurationSec, int MarginSec, CasterMessageMatcher? Matcher,
             bool TimedFromSend, DateTime SentAt)? _pendingPartyCast;
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

    // Burst-settle guard. A round's hits arrive as a burst, and Paradigm redraws the
    // statline after EVERY line, so while the burst lands _state.Hp is a partial read
    // that is still falling. Picking anything on it spends the round's one between-round
    // slot too early: an armour buff fired on a stale HP=254 while the player was at 117
    // (report paradigm-20260904-214056), and a Major heal picked at a partial 236 left
    // the Emergency heal locked out of the slot when the burst finished at 72 (report
    // paradigm-20260928-131549). The server runs the cast after the round's output
    // either way (the echo lands on the final prompt), so waiting for HP to settle costs
    // nothing. _hpBurstAt is stamped by a damage-line combat tick and by every HP drop;
    // until BurstSettleWindow passes quietly, only an Emergency heal may fire (HP only
    // falls inside a burst, so a due Emergency tier can't turn out wrong), and a settled
    // pass is scheduled to pick the rest on the final HP.
    private DateTime _hpBurstAt = DateTime.MinValue;
    private int _lastSeenHp;
    private static readonly TimeSpan BurstSettleWindow = TimeSpan.FromMilliseconds(400);

    // Projected-round margin. A round nobody saw is where the client worked out it
    // should be, good to a few tens of milliseconds, and a cast is refused when it
    // reaches the game before the round it is for has begun (the game hands the
    // round's one cast back at its own tick). Sent a moment after the projected
    // boundary it costs nothing; sent on it, a projection running slightly ahead put
    // two casts in one round: `You have already cast a spell this round!` (report
    // paradigm-20261007-141844). So after a tick that wasn't seen on the wire, every
    // pick waits this long, an Emergency heal too: refused, it would lose the round.
    private DateTime _projectedTickAt = DateTime.MinValue;
    private static readonly TimeSpan ProjectedTickMargin = TimeSpan.FromMilliseconds(200);
    private Func<bool>? _combatTickPlaced;
    // Runs the settled pass after a delay (a one-shot dispatcher timer in the app). Unset = the guard is off, so a hold can never be
    // stranded without a scheduled re-evaluation.
    private Action<TimeSpan, Action>? _scheduleSettledPass;
    private bool _settledPassPending;
    // Logs the hold once per burst instead of once per statline redraw.
    private bool _settleHoldLogged;

    // A mana-regen roll-spell reroll the reroller staged (last roll below its
    // threshold). It's offered by PickManaRegenReroll at PriorityBuffing and cast
    // through the normal between-round pass — so it competes with a due heal/cure
    // and spends the one-per-round slot, instead of firing on the raw wire — then
    // cleared once it goes out. Null when no reroll is pending.
    private string? _pendingManaRegenReroll;

    // When the buff timers were frozen: the character left the game (the link
    // dropped, or it went out to the board's menu), or they were put back from a
    // saved profile. While set, each timer keeps the time it had left at this instant;
    // the resume moves the expiries on by the time spent away. null = running.
    private DateTime? _pausedAt;

    private Func<string, (string Caster, long DurationSec)?>? _buffInfoByShort;
    private Func<string, string?>? _spellNameByShort;
    private Func<MessageRecord, string?>? _shortFromAppliedRecord;
    // Given a cast code, the cast codes of the buffs its spell removes (RemovesSpell).
    // Used to re-attribute a wear-off that lands right after a clobbering cast to its
    // VICTIM rather than the just-cast survivor (bless & chant share the wear-off line).
    private Func<string, IReadOnlyCollection<string>>? _removesShortsFor;
    private Func<string, bool>? _isPartyWideBuff;

    // The due-queue text LogDueQueue last wrote, so an unchanged queue isn't re-logged.
    private string? _lastLoggedQueue;

    // The last buff we SUCCESSFULLY cast (identity known independent of any shared
    // condition message) and when. A wear-off arriving within ClobberWindow of a cast
    // that removes other buffs is that victim's wear-off, not the caster's.
    private string? _lastCastShort;
    private DateTime _lastCastAt;
    // Whether that cast landed on us (a self or whole-party cast) rather than on one
    // party member. Only a buff on us can strip a buff of ours.
    private bool _lastCastOnUs;
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
    // no room list (before wiring), so a hidden member stays backed off until
    // we move.
    private Func<string, bool>? _isMemberInRoom;

    // Given names (lower-cased) of party members a single-target buff couldn't reach
    // because they're HIDING — the server answered "You do not see <name> here!" to
    // our cast. We back off casting on them (no spam) until we MOVE (NoteRoomChanged
    // clears all) or they reappear in "Also here:" (cleared in DueUnifiedBuffs). The
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
    // in a party. DueUnifiedBuffs skips a covered slot; the Buff Watchdog labels it.
    private Func<IReadOnlyDictionary<string, string>>? _selfBuffCoverage;
    // Configured buffs another configured buff PERMANENTLY removes (Paradigm continuous
    // removal, one-directional): loser cast code → winning buff name. Never maintained (the
    // winner keeps stripping them), for ANY target; the watchdog labels them "covered by".
    private Func<IReadOnlyDictionary<string, string>>? _suppressedBuffs;
    // STOCK one-directional conflicts: loser cast code → remover cast code. Both are
    // maintained (not suppressed); DueUnifiedBuffs orders each remover before the losers it
    // removes so the at-cast strip doesn't knock them off. Empty off stock.
    private Func<IReadOnlyDictionary<string, string>>? _collisionOrder;
    private Action<string>? _selfBuffCastSink;
    private readonly Func<DateTime> _now;
    private LineExtractor? _lines;

    // Last self-heal we sent — spell code, the HP + MA it was sent at, and when.
    // Feeds SameSelfHealStaleGuard so a stale re-evaluation can't double-cast it.
    private (string Spell, int Hp, int Ma, DateTime At)? _lastSelfHealCast;

    // When each self-cure spell code was last cast. Feeds SelfCureRetryWindow so a stuck
    // condition flag can't re-fire the same cure every round.
    private readonly Dictionary<string, DateTime> _lastSelfCureAt = new(StringComparer.OrdinalIgnoreCase);

    // Cures already logged as held for the current fight (CureWaitsForFightEnd);
    // cleared when InCombat changes so each fight logs its hold once.
    private readonly HashSet<string> _curesHeldThisFight = new(StringComparer.OrdinalIgnoreCase);

    // Party cures in their back-off, keyed "spell target": when the last one went out
    // and how many have gone out since the member's chip last stayed clear.
    private readonly Dictionary<string, (DateTime At, int Casts)> _partyCures = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public CastingDirector(
        PlayerState state,
        CastCoordinator cast,
        Func<SpellsSettings> readSpells,
        Func<HealthSettings> readHealth,
        Func<bool> isEnabled,
        LogService? log = null,
        Func<DateTime>? now = null)
        : this(state, cast, conditions: null, party: null,
               readSpells, readHealth, readPartySettings: null,
               isEnabled, log, now: now) { }

    // Constructor with optional ConditionTracker (for ailment cures) and PartyState
    // + PartySettings reader (for party-cast). Pass null for engines that
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
        LogService? log = null,
        Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
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

        _lastSeenHp = _state.Hp;
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

    // Party-less overload, for a director with no PartyState.
    public CastingDirector(
        PlayerState state,
        CastCoordinator cast,
        Conditions.ConditionTracker? conditions,
        Func<SpellsSettings> readSpells,
        Func<HealthSettings> readHealth,
        Func<bool> isEnabled,
        LogService? log = null,
        Func<DateTime>? now = null)
        : this(state, cast, conditions, party: null,
               readSpells, readHealth, readPartySettings: null,
               isEnabled, log, now: now) { }

    // Wire the "is this combat tick damage-line-driven?" probe (TickEngine). A true
    // read marks a round burst as landing (see _hpBurstAt). Optional — unset means
    // every tick is treated as HP-fresh.
    public void SetCombatTickSource(Func<bool> isDamageDriven) =>
        _combatTickDamageDriven = isDamageDriven;

    // Wire the "can this combat tick be placed against the game's rounds?" probe
    // (TickEngine.LastCombatTickWasPlaced). A false read is the timer counting with
    // nothing seen for a long while: it frees no slot. Optional — unset means every
    // tick is a round boundary.
    public void SetCombatTickPlacement(Func<bool> isPlaced) =>
        _combatTickPlaced = isPlaced;

    // Wire the delayed-callback scheduler that runs the settled pass once a round
    // burst goes quiet (see _hpBurstAt). Without it the burst-settle hold is off.
    public void SetSettledPassScheduler(Action<TimeSpan, Action> schedule) =>
        _scheduleSettledPass = schedule;

    // Hook to TickEngine.CombatTickElapsed — drives between-round evaluations. A tick
    // fired straight off a server combat line runs while the round's burst is still
    // landing, so stamp the burst before the pass: RunDecisionPass then holds all but
    // an Emergency heal until HP settles.
    public void OnCombatTick()
    {
        // _combatTickDamageDriven unset is a caller with no tick source: nothing to
        // hold for either way.
        if (_combatTickDamageDriven?.Invoke() is { } seenOnTheWire)
        {
            if (seenOnTheWire) _hpBurstAt = _now();
            else _projectedTickAt = _now();
        }
        Evaluate();
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

    // Wire HealthManager.HpRestDeferredByRoomSpell. True while an HP rest is due and
    // isn't being taken because the room's own spell does damage (the rest would be
    // broken every few seconds). The rest-time heal is cast only while resting, so
    // without this it would never fire there and the room would wear the character
    // down unanswered: the heal goes out standing instead (user, 2026-10-09: "we
    // should heal but not actively try to rest in a room like this, because it'll
    // kill us if we dont have heals").
    public void SetRestDeferredGate(Func<bool> hpRestDeferredHere)
    {
        ArgumentNullException.ThrowIfNull(hpRestDeferredHere);
        _hpRestDeferredHere = hpRestDeferredHere;
    }

    private Func<bool>? _hpRestDeferredHere;

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

    // Wire the lair-entry buff hold (Game.Map.LairEntryDebuffHold). While it holds,
    // buff categories wait: a loop is about to step into a lair and the round's one
    // between-round cast is being kept for the combat profile's pre-attack debuff.
    // Heals and cures still go out. ILairEntryBuffHold has what is asked of it and
    // told to it. Optional.
    public void SetLairEntryBuffHold(ILairEntryBuffHold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        _lairHold = hold;
    }
    private ILairEntryBuffHold? _lairHold;

    // Connected, in the game and alive: the state every decision needs first.
    private bool CanDecide =>
        _isConnected?.Invoke() != false && !_suspended
        && _state.HasPromptData && _state.MaxHp > 0 && _state.Hp > 0;

    // A heal, cure or buff is due and would be cast as soon as the round's
    // between-round cast is free. The debuff isn't counted: it is what a caller
    // waiting on this wants the cast kept for. Nor is a buff a lair-entry hold is
    // keeping back: it won't take the cast.
    public bool HasCastDue()
    {
        if (!CanDecide) return false;
        bool healEnabled = _isEnabled();
        bool blessEnabled = _autoBlessEnabled?.Invoke() ?? true;
        if (!healEnabled && !blessEnabled) return false;

        HealthSettings health = _readHealth();
        if (healEnabled && DueHealOrCure(health) is not null) return true;
        return blessEnabled
            && (PickBuff(health, priority: true, peek: true) is not null
                || PickBuff(health, priority: false, peek: true) is not null);
    }

    // The heal or cure that is due and would take the round's between-round cast, by
    // its cast code; null when none is. For a caller that says what it waited on.
    public string? HealOrCureDue() =>
        CanDecide && _isEnabled() ? DueHealOrCure(_readHealth()) : null;

    private string? DueHealOrCure(HealthSettings health)
    {
        SpellsSettings spells = _readSpells();
        PartySettings? party = _readPartySettings?.Invoke();
        return PickEmergencySelfHeal(spells, health)
            ?? PickDownedAllyHeal(party, spells)?.Spell
            ?? PickMinorPartyHeal(party)?.Spell
            ?? PickMajorPartyHeal(party)?.Spell
            ?? PickMinorSelfHeal(spells, health)
            ?? PickMajorSelfHeal(spells, health)
            ?? PickCure(spells)?.Spell;
    }

    // The buffs of one kind (ticked "Priority buff", or the rest) that are due now, in
    // cast order, by the rules the decision pass picks with; a buff aimed at a party
    // member reads "code on name". A lair-entry hold is not applied, so the caller can
    // name what it is keeping back as well as what it is waiting for. Like HasCastDue
    // it only asks: log lines and the bug report read it, and reading changes nothing.
    public IReadOnlyList<string> BuffsDue(bool priority)
    {
        if (!CanDecide || _autoBlessEnabled?.Invoke() == false) return [];
        List<string> names = new();
        foreach (CastCandidate due in DueBuffs(_readHealth(), priority, peek: true))
        {
            string name = BuffName(due);
            if (!names.Contains(name)) names.Add(name);   // a staged reroll and its own slot
        }
        return names;
    }

    private static string BuffName(CastCandidate buff) =>
        buff.Target is null ? buff.Spell : $"{buff.Spell} on {buff.Target}";

    // ----- a use the client owns ------------------------------------------------
    // A buff the client keeps up on its own account by using an item: a hazard
    // counter (the desert waterskin), which Game.Map.AutoHazardCounterProvisioner
    // tracks. It is not a buff slot: the user never listed it, it answers to neither
    // Auto-Heal nor Auto-Bless, and it is due only where its hazard is. What it shares
    // with the slots is the sending: one between-round cast a round, held while a
    // sneak is being kept, and followed by the fight's resume and the re-sneak
    // (CastFired). So the provisioner keeps the clock and this pass does the sending.
    private Func<(string What, bool MustGoNow)?>? _clientUseDue;
    private Func<bool>? _fireClientUse;
    private Action? _clientUseHeld;

    // due: the command a use would send and whether it can wait, null when none is
    // due. fire: send it; true when it went out. heldForSneak: the pass held it to
    // keep a sneak.
    public void SetClientUseSource(
        Func<(string What, bool MustGoNow)?> due, Func<bool> fire, Action heldForSneak)
    {
        ArgumentNullException.ThrowIfNull(due);
        ArgumentNullException.ThrowIfNull(fire);
        ArgumentNullException.ThrowIfNull(heldForSneak);
        _clientUseDue = due;
        _fireClientUse = fire;
        _clientUseHeld = heldForSneak;
    }

    // The client's own use went out, from this pass or by another road (the step
    // into a hazard room, a lapse prompt). Either way it took the round's
    // between-round cast: the slot is spent, a fight resumes on the *Combat Off* it
    // draws and the sneak is taken again.
    public void NoteClientUseSent()
    {
        _clientUseHeldForSneak = false;
        _cast.NotifyExternalCastSent();
        _betweenRoundSlotUsedAt = _now();
        CastFired?.Invoke();
    }

    // Wire the sneak-keeping gate (Game.Stealth.SneakGuard.Holds). While it returns
    // true every in-between category but debuffing is HELD rather than cast (see the
    // decision pass). Optional — until wired, nothing defers.
    public void SetStealthMaintenanceDeferGate(Func<bool> shouldDefer)
    {
        ArgumentNullException.ThrowIfNull(shouldDefer);
        _deferMaintenanceWhileStealthed = shouldDefer;
    }

    // While true (a flee from the health gates), the emergency self-heal isn't held
    // for the sneak — it fires before we re-sneak.
    private Func<bool>? _emergencyHealBypass;

    public void SetEmergencyHealBypassProbe(Func<bool> bypass) => _emergencyHealBypass = bypass;

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

    // ----- Draw items (a deck of cards) ---------------------------------
    // A draw item's use deals one of several buffs at random, so a slot holding one
    // has no single duration and may not want every buff it can deal. One use goes out
    // in the between-round slot like any other buff; the card that lands is read off
    // its applied line. A wanted card arms the slot's timer for that card's duration;
    // an unwanted one (BuffSlot.RejectedOutcomes) leaves the slot due, so the next
    // cycle draws again. A use can be repeated every cycle (user, 2026-10-02).
    //
    // That is Paradigm's deck, which shuffles the old card away before it draws.
    // Stock's has no shuffle: used while a card is up it answers "Nothing happens."
    // and deals nothing. Such an item can't choose its card, so whatever it deals is
    // kept, and it is used again only once that card has worn off (user, 2026-10-02).
    private Func<string, IReadOnlyList<(int SpellNumber, string Name, long DurationSec)>?>? _drawOutcomes;
    private Func<string, bool>? _drawCanRedraw;

    // A draw that shows no card on an item that can't redraw was most likely turned
    // away because a card is still up (its wear-off line was missed, or a fortune
    // teller's reading). The refusal comes after the use has gone through, so it
    // costs a charge all the same: retry seldom, not every round.
    private const int NoRedrawRetrySec = 180;

    private bool CanRedraw(string token) => _drawCanRedraw?.Invoke(token) ?? true;

    // How long a sent draw is given to show its card before the slot counts as due
    // again (the use didn't happen, or the card's line wasn't recognised).
    private const int DrawSettleSec = 6;

    // Draws sent and still waiting for their card, by token → when the wait ends. Kept
    // apart from _activeUntil: a recast margin longer than the wait would read a
    // waiting draw as already due.
    private readonly Dictionary<string, DateTime> _drawPendingUntil = new(StringComparer.OrdinalIgnoreCase);

    // The card each draw item's running timer is for, by token: the timer row names
    // it, since a slot that keeps several cards doesn't say which one is up.
    private readonly Dictionary<string, string> _drawnOutcome = new(StringComparer.OrdinalIgnoreCase);

    // canRedraw: whether a use replaces the outcome already up. Left out, every draw
    // item is taken to.
    public void SetItemDrawSource(
        Func<string, IReadOnlyList<(int SpellNumber, string Name, long DurationSec)>?> outcomesOf,
        Func<string, bool>? canRedraw = null)
    {
        ArgumentNullException.ThrowIfNull(outcomesOf);
        _drawOutcomes = outcomesOf;
        _drawCanRedraw = canRedraw;
    }

    private bool TryFireDraw(string token)
    {
        if (_executeItemCast is null || !_executeItemCast(token)) return false;
        _cast.NotifyExternalCastSent();
        // A draw can deal the card already up (user, 2026-10-02), and a card still
        // latched as applied would land without a fresh applied event: the draw would
        // read as "no card seen" and go again, round after round. Every draw starts
        // from no card.
        if (_drawOutcomes?.Invoke(token) is { } outcomes)
            _conditions?.ReleaseApplied(r => outcomes.Any(o => IsOutcomeRecord(r, o.SpellNumber, o.Name)));
        // The slot's timer is set when the card lands (NoteDrawOutcome).
        _activeUntil.Remove(("", token));
        _drawnOutcome.Remove(token);
        _drawPendingUntil[token] = _now().AddSeconds(CanRedraw(token) ? DrawSettleSec : NoRedrawRetrySec);
        _log?.Info(LogCategory, $"draw item used token={token} — waiting for the card.");
        CastFired?.Invoke();
        return true;
    }

    // The record that just applied or ended, when it is one of a slotted draw item's
    // outcomes. Matched on the linked spell, or on the name: the two realms' card
    // records share an Id, so the one that fires may link the other realm's spell.
    private (Models.Profile.BuffSlot Slot, (int SpellNumber, string Name, long DurationSec) Outcome)? DrawOutcomeOf(MessageRecord r)
    {
        if (_drawOutcomes is null || _readPartyBuffs?.Invoke() is not { } buffs) return null;
        foreach (Models.Profile.BuffSlot slot in buffs.Slots)
        {
            if (!ItemCastToken.IsToken(slot.Spell) || _drawOutcomes(slot.Spell!) is not { Count: > 0 } outcomes) continue;
            foreach ((int SpellNumber, string Name, long DurationSec) o in outcomes)
                if (IsOutcomeRecord(r, o.SpellNumber, o.Name))
                    return (slot, o);
        }
        return null;
    }

    private static bool IsOutcomeRecord(MessageRecord r, int spellNumber, string name) =>
        string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)
        || (r.Links is { } links && links.Any(l =>
            l.Number == spellNumber && string.Equals(l.Table, "Spells", StringComparison.OrdinalIgnoreCase)));

    internal void NoteDrawOutcome(MessageRecord r)
    {
        if (DrawOutcomeOf(r) is not { } drawn) return;
        string token = drawn.Slot.Spell!;
        _drawPendingUntil.Remove(token);
        if (CanRedraw(token) && drawn.Slot.RejectedOutcomes.Contains(drawn.Outcome.SpellNumber))
        {
            _activeUntil.Remove(("", token));
            _log?.Info(LogCategory, $"draw item {token}: drew {drawn.Outcome.Name}, which isn't ticked — drawing again.");
            return;
        }
        long seconds = Math.Max(1, drawn.Outcome.DurationSec);
        _activeUntil[("", token)] = (_now().AddSeconds(seconds), drawn.Slot.RecastMarginSec, (int)seconds);
        _drawnOutcome[token] = drawn.Outcome.Name;
        _log?.Info(LogCategory, $"draw item {token}: drew {drawn.Outcome.Name} — keeping it for {seconds}s.");
    }

    // The card's wear-off: the slot is due again.
    internal void NoteDrawEnded(MessageRecord r)
    {
        if (DrawOutcomeOf(r) is not { } ended) return;
        _drawnOutcome.Remove(ended.Slot.Spell!);
        _drawPendingUntil.Remove(ended.Slot.Spell!);
        if (_activeUntil.Remove(("", ended.Slot.Spell!)))
            _log?.Info(LogCategory, $"draw item {ended.Slot.Spell}: {ended.Outcome.Name} wore off.");
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
    // that removes (supersedes) it while in a party. DueUnifiedBuffs skips a covered slot so
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
    // one-directional conflicts (Paradigm continuous removal). DueUnifiedBuffs skips a
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
    // can coexist if the remover is cast first. DueUnifiedBuffs re-sorts the priority list so
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

    // Wire the cast code → spell name lookup. The game's negate line names the spell in
    // full, so this is how it is tied back to the cast we sent. Optional — until wired,
    // no negate line is ever taken as ours.
    public void SetSpellNameLookup(Func<string, string?> spellNameByShort)
    {
        ArgumentNullException.ThrowIfNull(spellNameByShort);
        _spellNameByShort = spellNameByShort;
    }

    // A member negated one of our buffs and is now marked on its slot (cast code, the
    // member's lower-cased given name). Raised on the UI thread with the line.
    public event Action<string, string>? BuffNegated;

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
    // confirmation). For another character becoming the loaded one (ProfileLoaded):
    // its own saved timers, if it has any, are put back after this. Not for a
    // disconnect, where the timers are kept and stand still (PauseBuffTimers), nor
    // for a death, which takes only our own (ClearSelfBuffTracking).
    public void ResetBuffTracking()
    {
        _activeUntil.Clear();
        _carried.Clear();
        _hiddenTargets.Clear();
        _pendingPartyCast = null;
        _pendingManualCast = null;
        _pendingSelfBuffShort = null;
        _pendingManaRegenReroll = null;
        _lastSelfHealCast = null;
        _lastSelfCureAt.Clear();
        _partyCures.Clear();
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
        _carried.Clear();
        _pendingSelfBuffShort = null;
        _pendingManaRegenReroll = null;
        _lastSelfHealCast = null;
        _lastSelfCureAt.Clear();
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

    // Drop the run-out timers of anyone who is no longer in the party. A member's
    // timer outlives its expiry on purpose: the entry is what a negative recast
    // margin counts from, and the next cast overwrites it. Once they have left,
    // nothing casts on them again, so theirs sat in the store, and in every bug
    // report as "expired, not yet cleared", until the profile was reloaded (report
    // paradigm-20261010-145330). A timer still running is kept: whoever comes back
    // inside it is still buffed. So is one inside a negative margin's wait after the
    // run-out: a member off the roster for one pass (dropped and back, a party
    // re-formed) would otherwise be recast on the moment they returned, with the
    // wait the user set thrown away.
    private void DropExpiredTimersOfDeparted()
    {
        if (_activeUntil.Count == 0) return;
        DateTime now = _now();
        List<(string Target, string Short)>? doomed = null;
        foreach (KeyValuePair<(string Target, string Short), (DateTime Until, int MarginSec, int TotalSec)> kv in _activeUntil)
        {
            if (kv.Key.Target.Length == 0 || IsInPartyGiven(kv.Key.Target)) continue;
            int waitAfter = Math.Max(0, -EffectiveMargin(kv.Key.Target, kv.Key.Short, kv.Value.MarginSec));
            if (now >= kv.Value.Until.AddSeconds(waitAfter))
                (doomed ??= new()).Add(kv.Key);
        }
        if (doomed is null) return;
        foreach ((string, string) key in doomed) _activeUntil.Remove(key);
        _log?.Info(LogCategory,
            $"dropped {doomed.Count} expired buff timer(s) on target(s) no longer in the party: "
            + string.Join(", ", doomed.Select(k => $"{k.Short} on {k.Target}")) + ".");
    }

    // Anyone on the roster, ourselves included: a slot aimed at our own name keys its
    // timer by that name.
    private bool IsInPartyGiven(string given)
    {
        if (_party is null) return false;
        foreach (PartyMember m in _party.Members)
            if (string.Equals(GivenName(m.Name), given, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // The instant the timers were frozen, or null while running. The Buff Watchdog
    // reads this so its display stands still at that instant (the heartbeat is a wall
    // clock that keeps ticking while we are out of the game); remaining time is
    // reckoned against it until the resume moves the expiries on.
    public DateTime? PausedAtUtc => _pausedAt;

    // The timers on ourselves ran on after a pause: how long the pause was, and the
    // cast codes whose expiry was moved on by it. For a reader that keeps an expiry of
    // its own beside a timer (the Buff Watchdog's record of who a whole-party buff
    // covered), which has to move the same ones and no others.
    public event Action<TimeSpan, IReadOnlyCollection<string>>? TimersResumed;

    // The timers on ourselves that were carried over time out of the game: across a
    // pause in this run, or from the profile of an earlier one. Cast code → the
    // expiry the timer had when it was last carried, and whether it came from a
    // profile. A recast or the buff's own applied line sets a new expiry, which is
    // how a carried timer is told from one raised since, with no mark to keep at
    // every place a timer is armed.
    //
    // What is known about a carried buff is only what was known when the game was
    // left. So a death the client did not see and finds out about late takes exactly
    // these (ClearCarriedSelfTimers), where wiping every timer would take buffs
    // raised since.
    //
    // RanOnUntil is the expiry the timer would have had with no time out of the
    // game: where the same cast runs out on anyone else it landed on, who stayed in
    // the game for all the client knows (CarriedBuffRanOutOnParty).
    private readonly Dictionary<string, (DateTime Until, bool Restored, DateTime RanOnUntil)> _carried = new(StringComparer.OrdinalIgnoreCase);

    private bool IsCarried(string code, out (DateTime Until, bool Restored, DateTime RanOnUntil) mark)
    {
        return _carried.TryGetValue(code, out mark)
            && _activeUntil.TryGetValue(("", code), out (DateTime Until, int MarginSec, int TotalSec) timer)
            && timer.Until == mark.Until;
    }

    // A whole-party buff lands on us and on every member with the one cast, and has
    // the one timer, keyed to us. Carried over time out of the game, that timer says
    // what is left on US: it stood still while the members' ran on, "if the party
    // member is still in the game when we arent, its better to assume their timers
    // keep ticking" (user, 2026-10-10). So a carried timer vouches for nobody else,
    // and in a party the buff is due by the clock that did not stop. Without this
    // the recast came late for the members by exactly the time we were away.
    private bool CarriedBuffRanOutOnParty(string code)
    {
        if (!IsCarried(code, out (DateTime Until, bool Restored, DateTime RanOnUntil) mark)) return false;
        int margin = EffectiveMargin("", code, _activeUntil[("", code)].MarginSec);
        return (mark.RanOnUntil - _now()).TotalSeconds <= margin;
    }

    // The character is out of the game: the link dropped, or it left for the board's
    // menu. "buffs survive disconnects, relogs", and "as soon as we've disconnected
    // all timers need to be paused until we're back in the game, then they all
    // resume" (user, 2026-10-10). So the timers on OUR character stand still from
    // here. One on a party member does not: the member may have stayed in the game,
    // where its buff goes on running down, so that timer keeps its real expiry (see
    // ResumeBuffTimers). A fresh character (ProfileLoaded → ResetBuffTracking) is the
    // only path that wipes everything.
    public void PauseBuffTimers()
    {
        // Latch the loop off until we're confirmed back in the game world — set
        // unconditionally (even with no armed timers) so a disconnect with nothing
        // active still can't cast into the login/menu. Cleared in ResumeBuffTimers.
        _suspended = true;
        // The first pause stands: a second drop before any prompt is the same stretch
        // away, and the time since the first has to be given back whole.
        if (_pausedAt is not null || _activeUntil.Count == 0) return;
        DateTime now = _now();
        _pausedAt = now;
        int ours = 0;
        foreach (KeyValuePair<(string Target, string Short), (DateTime Until, int MarginSec, int TotalSec)> kv in _activeUntil)
        {
            if (kv.Key.Target.Length != 0) continue;
            ours++;
            // One that had already run out is kept for a recast wait, not for a buff.
            if (kv.Value.Until <= now) continue;
            // Carried before and not raised since: its mark stands, with the expiry
            // it had before the first time away.
            if (IsCarried(kv.Key.Short, out _)) continue;
            _carried[kv.Key.Short] = (kv.Value.Until, false, kv.Value.Until);
        }
        _log?.Info(LogCategory,
            $"buff timers paused — {ours} on ourselves keep the time they have left until we are back in the game; "
            + $"{_activeUntil.Count - ours} on party members run on");
    }

    // Back in the game.
    //
    // A timer on ourselves runs on with exactly the time it had left at the pause,
    // however long that was, and nothing is dropped for the time away. (Until
    // 2026-10-10 the expiries were left where they were, so the time away counted
    // against every buff and one that "lapsed" meanwhile was recast on the way in.)
    //
    // A timer on a party member keeps its real expiry, and one that passed while we
    // were away is dropped so the buff is cast again: the member stayed in the game
    // for all the client knows, and there its buff ran down the whole time (user,
    // 2026-08-28, and again 2026-10-10: "its better to assume their timers keep
    // ticking").
    public void ResumeBuffTimers()
    {
        // First in-game prompt after a (re)connect — lift the cast-suspend latch
        // BEFORE the no-timers early-return, so casting always resumes even when the
        // disconnect had no armed timers to unfreeze. Fired on every prompt; harmless
        // to clear repeatedly.
        _suspended = false;
        if (_pausedAt is not { } since) return;
        _pausedAt = null;

        DateTime now = _now();
        TimeSpan away = now - since;
        List<string> moved = new();
        List<string> partyClock = new();
        int lapsed = 0;
        foreach ((string Target, string Short) key in _activeUntil.Keys.ToList())
        {
            (DateTime Until, int MarginSec, int TotalSec) timer = _activeUntil[key];
            if (key.Target.Length != 0)
            {
                if (timer.Until > now) continue;
                _activeUntil.Remove(key);
                lapsed++;
                continue;
            }
            // Only what was running at the pause stood still. A buff whose line
            // showed on the way back in, ahead of the first prompt, started then.
            if (timer.Until.AddSeconds(-timer.TotalSec) > since) continue;
            bool carried = IsCarried(key.Short, out (DateTime Until, bool Restored, DateTime RanOnUntil) mark);
            _activeUntil[key] = (timer.Until + away, timer.MarginSec, timer.TotalSec);
            moved.Add(key.Short);
            if (!carried) continue;
            _carried[key.Short] = (timer.Until + away, mark.Restored, mark.RanOnUntil);
            LatchAsApplied(key.Short);
            if (_isPartyWideBuff?.Invoke(key.Short) == true)
                partyClock.Add($"{key.Short} {Math.Max(0L, (long)(mark.RanOnUntil - now).TotalSeconds)} s");
        }

        _log?.Info(LogCategory,
            $"buff timers resumed after {(long)away.TotalSeconds} s out of the game — {moved.Count} on ourselves run on with the time they had left; "
            + $"{lapsed} on party members ran out meanwhile and were dropped."
            + (partyClock.Count == 0 ? "" :
                " Whole-party buffs among them cover only us: in a party each is recast by the time it had left with no time away ("
                + string.Join(", ", partyClock) + ")."));
        TimersResumed?.Invoke(away, moved);
    }

    // The condition tracker holds a buff as applied from its applied line until its
    // wear-off line, and a wear-off line ends only what it holds (several buffs share
    // one wear-off text). Its log is cleared when the link drops, and a client started
    // since never had it, while the buff itself is still on the character and prints
    // nothing to say so. So for a buff carried over time out of the game the latch is
    // put back here, or its wear-off line would end nothing and its timer would run
    // on to the end over a buff that had gone. Re-latching, not keeping the latches
    // through the drop: a restart has none to keep, and one way serves both.
    private void LatchAsApplied(string code)
    {
        if (_conditions is null) return;
        if (_drawnOutcome.TryGetValue(code, out string? card) && _drawOutcomes?.Invoke(code) is { } outcomes)
        {
            foreach ((int SpellNumber, string Name, long DurationSec) outcome in outcomes)
                if (string.Equals(outcome.Name, card, StringComparison.OrdinalIgnoreCase))
                    _conditions.LatchApplied(r => IsOutcomeRecord(r, outcome.SpellNumber, outcome.Name));
            return;
        }
        if (_shortFromAppliedRecord is { } resolve)
            _conditions.LatchApplied(r => string.Equals(resolve(r), code, StringComparison.OrdinalIgnoreCase));
    }

    // Drop the timers on ourselves that were carried over time out of the game and
    // have not been raised again since. For a death the client did not see and
    // learns of after it is back in the game: it wiped the buffs that were on, and
    // any raised since are not its business. Returns how many went.
    public int ClearCarriedSelfTimers(string why)
    {
        List<string> gone = new();
        foreach (string code in _carried.Keys.ToList())
            if (IsCarried(code, out _)) gone.Add(code);
        _carried.Clear();
        foreach (string code in gone)
        {
            _activeUntil.Remove(("", code));
            _drawnOutcome.Remove(code);
        }
        if (gone.Count == 0) return 0;
        if (_conditions is not null && _shortFromAppliedRecord is { } resolve)
            _conditions.ReleaseApplied(r => resolve(r) is { } code && gone.Contains(code, StringComparer.OrdinalIgnoreCase));
        _log?.Info(LogCategory,
            $"dropped {gone.Count} buff timer(s) carried over time out of the game ({why}): {string.Join(", ", gone)}");
        return gone.Count;
    }

    // The timers on ourselves, for the profile: what each had left at the pause (or
    // has left now, when running). A timer on a party member is not saved: after a
    // restart there is no knowing that the member was in the game for the same
    // stretch, or is the one the name now belongs to.
    public List<Models.Profile.SavedBuffTimer> SnapshotSelfTimersForSave()
    {
        DateTime at = _pausedAt ?? _now();
        List<Models.Profile.SavedBuffTimer> saved = new();
        foreach (KeyValuePair<(string Target, string Short), (DateTime Until, int MarginSec, int TotalSec)> kv in _activeUntil)
        {
            if (kv.Key.Target.Length != 0) continue;
            saved.Add(new Models.Profile.SavedBuffTimer
            {
                Short = kv.Key.Short,
                RemainingSec = (int)Math.Round((kv.Value.Until - at).TotalSeconds),
                MarginSec = kv.Value.MarginSec,
                TotalSec = kv.Value.TotalSec,
                Outcome = _drawnOutcome.TryGetValue(kv.Key.Short, out string? card) ? card : null,
                RanOnUntilUtc = IsCarried(kv.Key.Short, out (DateTime Until, bool Restored, DateTime RanOnUntil) mark)
                    ? mark.RanOnUntil
                    : kv.Value.Until,
            });
        }
        return saved;
    }

    // Put back the timers a profile was saved with, standing still until the first
    // in-game prompt (ResumeBuffTimers). Called on a profile load, after
    // ResetBuffTracking. Returns how many were put back.
    public int RestoreSelfTimers(IReadOnlyList<Models.Profile.SavedBuffTimer> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        DateTime now = _now();
        int restored = 0;
        foreach (Models.Profile.SavedBuffTimer timer in saved)
        {
            if (string.IsNullOrWhiteSpace(timer.Short) || timer.TotalSec <= 0) continue;
            DateTime until = now.AddSeconds(timer.RemainingSec);
            _activeUntil[("", timer.Short)] = (until, timer.MarginSec, timer.TotalSec);
            if (!string.IsNullOrWhiteSpace(timer.Outcome)) _drawnOutcome[timer.Short] = timer.Outcome;
            if (timer.RemainingSec > 0) _carried[timer.Short] = (until, true, timer.RanOnUntilUtc);
            restored++;
        }
        if (restored == 0) return 0;
        // Standing still, and nothing cast, until the game is entered: a restore is
        // bookkeeping and sends nothing.
        _suspended = true;
        _pausedAt = now;
        return restored;
    }

    // A combat round tick elapsed (wired to TickEngine.CombatTickElapsed) — free the
    // between-round cast slot so the new round can cast once. Keyed to the combat
    // round cadence, NOT *Combat Off*: *Combat Off* fires per kill, so in a multi-mob
    // room it would re-open the slot several times a round and let the storm back in.
    //
    // A damage-line tick is read after the prompts of its own burst (the prompt scanner
    // runs ahead of the line extractor), so a heal fired off those prompts can already
    // be out when the tick lands. The server runs that cast after the round's output,
    // in the slot this tick would free, so a cast sent inside the burst keeps the slot
    // spent: freeing it let a second heal out into "You have already cast a spell this
    // round!" (report paradigm-20261004-054304).
    //
    // A tick that can't be placed against the game's rounds frees nothing: the slot
    // runs out its RoundWindow instead (see SetCombatTickPlacement).
    public void NotifyRoundComplete()
    {
        if (_combatTickPlaced?.Invoke() == false) return;
        if (_combatTickDamageDriven?.Invoke() == true
            && _now() - _betweenRoundSlotUsedAt < BurstSettleWindow)
            return;
        _betweenRoundSlotUsedAt = DateTime.MinValue;
    }

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
    // it and run the between-round pass now: PickManaRegenReroll offers it at
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
        {
            bool ours = kv.Key.Target.Length == 0;
            (DateTime Until, bool Restored, DateTime RanOnUntil) mark = default;
            bool carried = ours && IsCarried(kv.Key.Short, out mark);
            list.Add(new ActiveBuffTimer(kv.Key.Target, kv.Key.Short, kv.Value.Until,
                EffectiveMargin(kv.Key.Target, kv.Key.Short, kv.Value.MarginSec), kv.Value.TotalSec,
                ours && _drawnOutcome.TryGetValue(kv.Key.Short, out string? card) ? card : null,
                carried && mark.Restored,
                carried ? mark.RanOnUntil : null));
        }
        return list;
    }

    // True when a buff on targetKey ("" = self) is due to be (re)cast: either never
    // confirmed-active, or within the slot's recast lead of expiry. The lead is the
    // one stored with the timer when it was armed (0 ⇒ only once the buff has
    // actually expired). A read: a draw's wait that has run out is left in place (it
    // no longer holds anything, and the next draw overwrites it), so asking what is
    // due changes nothing.
    private bool IsRecastDue(string targetKey, string spellShort)
    {
        if (targetKey.Length == 0 && _drawPendingUntil.TryGetValue(spellShort, out DateTime waitEnds)
            && _now() < waitEnds)
            return false;
        if (!_activeUntil.TryGetValue((targetKey, spellShort), out (DateTime Until, int MarginSec, int TotalSec) t))
            return true;
        return (t.Until - _now()).TotalSeconds <= EffectiveMargin(targetKey, spellShort, t.MarginSec);
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
        if (CasterMessageMatcher.TryCreate(info.Caster) is { CanNameTarget: true } matcher)
            _pendingManualCast = (code, prefix, info.DurationSec, PartyBuffMargin(code), matcher);
        else
            TimeManualCastFromSend(code, prefix, info.DurationSec);
    }

    // A hand-typed single-target cast of a spell whose caster line can't name its
    // target: nothing will resolve the name we typed, so it is resolved against the
    // party now and the timer runs from the send. Ourselves → our own timer; one
    // member the shorthand fits → theirs; nobody, or more than one → no timer, since
    // the Buff Watchdog has no row to show it on.
    private void TimeManualCastFromSend(string code, string prefix, long durationSec)
    {
        if (SelfGivenLower().StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && SelfGivenLower().Length > 0)
        {
            StartSelfBuffTimer(code, SelfBuffMargin(code));
            return;
        }
        if (_party is null) return;
        string? given = null;
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf) continue;
            string name = GivenName(m.Name);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (given is not null) return;   // the shorthand fits two members
            given = name.ToLowerInvariant();
        }
        if (given is null) return;

        int margin = PartyBuffMargin(code);
        _activeUntil[(given, code)] = (_now().AddSeconds(durationSec), margin, (int)durationSec);
        NoteSuccessfulCast(code, given);
        _log?.Info(LogCategory,
            $"manual party-buff sent spell={code} target={given} — its cast line names no target, so the timer runs "
            + $"from the send: duration={durationSec}s, recast in {Math.Max(0L, durationSec - margin)}s.");
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

    // The margin to USE for a live timer, preferring the CURRENT slot config over the
    // value snapshotted when the buff was cast — so editing a slot's recast margin (e.g.
    // flipping it negative) redraws the watchdog bar and re-times the recast immediately,
    // not only after the next cast. Timers with no matching slot (hand casts, item-cast
    // tokens not carried as a slot's Spell) keep their cast-time margin.
    private int EffectiveMargin(string targetKey, string spellShort, int stored)
    {
        int margin = stored;
        if (_readPartyBuffs?.Invoke() is { } buffs)
            foreach (Models.Profile.BuffSlot slot in buffs.Slots)
                if (string.Equals(slot.Spell?.Trim(), spellShort, StringComparison.OrdinalIgnoreCase)
                    && (targetKey.Length != 0 || slot.CastOnSelf))
                {
                    margin = slot.RecastMarginSec;
                    break;
                }
        // A draw item that can't redraw is refused until its card is gone: a recast
        // lead would only send a use into that refusal.
        return margin > 0 && targetKey.Length == 0 && ItemCastToken.IsToken(spellShort) && !CanRedraw(spellShort)
            ? 0
            : margin;
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
        // Only a room-wide spell aimed at monsters draws the empty-room refusal, and
        // those are the combat engine's (its debuff mark, its room-attack channel) —
        // never a self-buff timer, whatever code the coordinator last sent.
        if (reason == CastFailureReason.NoTargets) return;
        if (reason == CastFailureReason.AlreadyCastThisRound)
            _betweenRoundSlotUsedAt = _now();
        // A party buff timed from its send (its caster line names no target) has no
        // landing to wait for, so the game's refusal is the only word that it never
        // went on: take the timer back. Same matching and staleness rule as the
        // self-buff marker below, for the same reason.
        if (_pendingPartyCast is { TimedFromSend: true } sent
            && string.Equals(spell, sent.Short, StringComparison.OrdinalIgnoreCase)
            && _now() - sent.SentAt <= PendingSelfBuffRejectionWindow)
        {
            _activeUntil.Remove((sent.Target.Trim().ToLowerInvariant(), sent.Short));
            _pendingPartyCast = null;
            _log?.Combat(LogCategory,
                $"party-buff {sent.Short} on {sent.Target} did not cast (reason={reason}) — dropped the timer armed on its send");
        }
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
        NoteDrawOutcome(r);

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
        // staleness window OnCastFailed uses), attribute the WHOLE burst to it:
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
            NoteSuccessfulCast(shortCode, targetKey: "");
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
    //
    // targetKey is who it landed on, as the timers are keyed: "" for us, else the
    // member's lower-cased given name. A whole-party buff lands on everyone whatever
    // the key says.
    private void NoteSuccessfulCast(string shortCode, string targetKey)
    {
        if (string.IsNullOrEmpty(shortCode)) return;
        bool everyone = _isPartyWideBuff?.Invoke(shortCode) == true;
        _lastCastShort = shortCode;
        _lastCastAt = _now();
        _lastCastOnUs = everyone || targetKey.Length == 0;

        // A landed buff clobbers the buffs its spell removes (RemovesSpell) — the game
        // strips them, so drop any active timer we still hold for one. A stripped buff
        // must not keep reading as if it's up (report paradigm-20260910-001023: chan
        // removes gbls, yet gbls's timer kept ticking). Keyed off the deterministic
        // removes relationship, NOT the ambiguous shared wear-off line, so it clears
        // the RIGHT buff; the clobber conflict is still surfaced by the config-side ⚠.
        if (_removesShortsFor?.Invoke(shortCode) is { Count: > 0 } victims)
            foreach (string victim in victims)
                ClearTimersForShort(victim, clobberedBy: shortCode, targetKey, everyone);
    }

    // Remove the active timer of a buff a landing buff strips — on whoever that buff
    // landed on, and no one else: a spell strips its removes off its own target only,
    // so smite cast on one member leaves greater smite on another untouched (report
    // paradigm-20261002-012234: the two cleared each other across targets and were
    // recast every few seconds). A whole-party buff lands on everyone, so it clears
    // every target's. Skips the caster itself so a spell that lists its own family can
    // never wipe the timer it just armed.
    private void ClearTimersForShort(string shortCode, string clobberedBy, string targetKey, bool everyone)
    {
        if (string.IsNullOrWhiteSpace(shortCode)
            || string.Equals(shortCode, clobberedBy, StringComparison.OrdinalIgnoreCase))
            return;

        List<(string Target, string Short)>? doomed = null;
        foreach ((string Target, string Short) key in _activeUntil.Keys)
            if (string.Equals(key.Short, shortCode, StringComparison.OrdinalIgnoreCase)
                && (everyone || string.Equals(key.Target, targetKey, StringComparison.OrdinalIgnoreCase)))
                (doomed ??= new()).Add(key);

        // The clobbering buff strips this one in-game, so release its applied-latch in
        // the ConditionTracker too — not just the timer below. A latched applied line
        // is deduped, so if the clobbered buff is later RE-CAST its confirm would never
        // re-fire, and that re-cast could then never drive its own clobber-clear
        // (report paradigm-20260910-012303: a re-cast greater bless never dropped an
        // active chant because gbls stayed latched from before chant clobbered it).
        // The tracker follows the conditions on US, so only a buff that landed on us
        // releases one.
        if ((everyone || targetKey.Length == 0) && _conditions is not null && _shortFromAppliedRecord is { } resolve)
            _conditions.ReleaseApplied(rec =>
                string.Equals(resolve(rec), shortCode, StringComparison.OrdinalIgnoreCase));

        if (doomed is null) return;
        foreach ((string, string) key in doomed) _activeUntil.Remove(key);
        _log?.Combat(LogCategory,
            $"buff {shortCode} clobbered by {clobberedBy} (removes) — {doomed.Count} timer(s) cleared");
    }

    private void OnConditionEnded(MessageRecord r)
    {
        NoteDrawEnded(r);
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
            && _lastCastOnUs
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

        // Server-confirmed wear-off — normally drop the self timer so the next pass
        // re-attempts immediately rather than waiting out a stale clock. But a NEGATIVE
        // margin deliberately defers the recast to |margin| seconds AFTER wear-off, so
        // keep that timer: IsRecastDue still gates on (Until - now) <= margin and won't
        // fire until the buffer elapses. Dropping it here would recast the instant the
        // wear-off line lands, collapsing the post-expiry buffer the user configured.
        if (resolved is { } shortCode
            && _activeUntil.TryGetValue(("", shortCode), out (DateTime Until, int MarginSec, int TotalSec) t))
        {
            int margin = EffectiveMargin("", shortCode, t.MarginSec);   // honor a live edit
            if (margin < 0)
                _log?.Combat(LogCategory,
                    $"self-buff {shortCode} wore off — negative margin keeps the timer; recast in "
                    + $"{Math.Max(0.0, (t.Until.AddSeconds(-margin) - _now()).TotalSeconds):0}s");
            else if (_activeUntil.Remove(("", shortCode)))
                _log?.Combat(LogCategory,
                    $"self-buff {shortCode} wore off (wear-off line) — recast timer cleared");
        }
        Evaluate();
    }

    private void OnLine(LineExtractor.EmittedLine line)
    {
        if (_pendingManualCast is { } man) ConfirmManualCast(man, line.Text);
        if (_pendingPartyCast is not { } p) return;
        // A cast timed from its send has nothing to wait for, and is kept only for
        // the game's answer to it. Past that, a "do not see" line is about something
        // else the user did, and must not take the member's running timer away.
        if (p.TimedFromSend && _now() - p.SentAt > PendingSelfBuffRejectionWindow)
        {
            _pendingPartyCast = null;
            return;
        }

        if (IsOwnNegatedCast(line.Text, p.Short, p.Target, p.SentAt))
        {
            _pendingPartyCast = null;
            MarkBuffNegated(p.Short, p.Target);
            return;
        }

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
            if (p.TimedFromSend) _activeUntil.Remove((p.Target.Trim().ToLowerInvariant(), p.Short));
            _pendingPartyCast = null;
            return;
        }

        if (p.Matcher?.ConfirmsTarget(line.Text, p.Target) != true) return;

        string key = p.Target.Trim().ToLowerInvariant();
        _activeUntil[(key, p.Short)] = (_now().AddSeconds(p.DurationSec), p.MarginSec, (int)p.DurationSec);
        NoteSuccessfulCast(p.Short, key);   // the "You cast <spell> on …" confirm names it reliably
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
        bool onSelf = string.Equals(given, SelfGivenLower(), StringComparison.OrdinalIgnoreCase);
        // The resolved "You cast <spell> on …" names it reliably.
        NoteSuccessfulCast(man.Short, onSelf ? string.Empty : given);
        if (given.Length == 0) return;

        if (onSelf)
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

    // The game's caster line for a cast the target negated, read as our own only when
    // it names the spell we sent, at the member we sent it at, within the window the
    // game answers a send in (user, 2026-10-17: "only act on casts we sent, matched by
    // spell name"). The same line also comes for a room spell the game charges to us
    // (the Underwater Passage's holding breath), which must not touch a buff of ours.
    // A resist is not a negate, so a resisted cast is never read here (GAME_MECHANICS
    // "Magic Resist (M.R.) and `TypeOfResists`").
    private bool IsOwnNegatedCast(string text, string shortCode, string target, DateTime sentAt)
    {
        if (!NegatedCastLine.TryReadOwnNegate(text, out string negatedAt, out string negatedSpell)) return false;
        if (_now() - sentAt > PendingSelfBuffRejectionWindow) return false;
        if (_spellNameByShort?.Invoke(shortCode) is not { Length: > 0 } spellName
            || !string.Equals(negatedSpell.Trim(), spellName.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(GivenName(negatedAt.Trim()), GivenName(target.Trim()), StringComparison.OrdinalIgnoreCase);
    }

    // A member negated our buff: it never landed, so no timer stands for it, and the
    // member is marked on every slot of that buff. Retrying would draw the same answer
    // every round for as long as they wear what stops it, so the mark holds until the
    // user re-ticks them (user, 2026-10-17).
    private void MarkBuffNegated(string shortCode, string target)
    {
        string key = GivenName(target.Trim()).ToLowerInvariant();
        _activeUntil.Remove((key, shortCode));
        if (_readPartyBuffs?.Invoke() is { } buffs)
            foreach (Models.Profile.BuffSlot slot in buffs.Slots)
                if (string.Equals(slot.Spell?.Trim(), shortCode, StringComparison.OrdinalIgnoreCase)
                    && !slot.NegatedTargets.Contains(key))
                    slot.NegatedTargets.Add(key);
        _log?.Info(LogCategory,
            $"party-buff {shortCode} on {target} was negated — not cast on them again until their box is re-ticked in the Buff Watchdog.");
        BuffNegated?.Invoke(shortCode, key);
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
                // Evaluate stamps the drop (NoteHpReading) before it decides.
                Evaluate();
                break;
            case nameof(PlayerState.InCombat):
                _curesHeldThisFight.Clear();
                Evaluate();
                break;
            case nameof(PlayerState.MaxHp):
            case nameof(PlayerState.Ma):
            case nameof(PlayerState.MaxMa):
            case nameof(PlayerState.Position):
            case nameof(PlayerState.HasPromptData):
                Evaluate();
                break;
        }
    }

    // Run one decision pass: walk the priority list and fire the first ready
    // candidate. Returns the spell that was cast (for diagnostics), or null
    // if nothing matched.
    public string? Evaluate()
    {
        NoteHpReading();
        // Ahead of every early return: this runs on each change of InCombat, which is
        // how the hold learns a fight began or ended even when nothing is to be cast.
        _lairHold?.Advance();
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
        // Ahead of the master switches: the timers are shown and reported whether or
        // not anything is being cast.
        DropExpiredTimersOfDeparted();
        // Two independent masters share this loop: the heal / cure / debuff
        // categories run under AutoHeal (_isEnabled), buffing runs under
        // AutoBless (_autoBlessEnabled), and each is gated separately in the
        // category switch below. Bless is controlled by the Auto-Bless toggle and
        // nothing else — so when AutoHeal is off but AutoBless is on we must
        // still fall through to the buffing category, not bail here. Only quit
        // when BOTH are off (nothing in the switch could fire).
        bool healEnabled = _isEnabled();
        bool blessEnabled = _autoBlessEnabled?.Invoke() ?? true;
        // The client's own use answers to neither toggle (see SetClientUseSource).
        (string What, bool MustGoNow)? ownUse = _clientUseDue?.Invoke();
        // Nothing due is nothing held, whatever keeps the pass below from running.
        if (ownUse is null) _clientUseHeldForSneak = false;
        if (!healEnabled && !blessEnabled && ownUse is null) return null;
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
        // still free the instant ConditionEnded's re-evaluate fires (GAME_MECHANICS
        // "Attack-prevented states (stun, petrify, bind)": the block is not attack-only).
        if (AttacksPrevented()) return null;
        if (_cast.IsCastBlocked) return null;
        // No attack-owed alternation gate here. A 0-energy between-round survival
        // cast and the combat attack are INDEPENDENT slots that both land in the
        // same round — the attack auto-repeats server-side and re-fires on the
        // cast's *Combat Off*, so casting a heal never actually costs the round's
        // attack (GAME_MECHANICS "Between-round cast slot vs the combat attack").
        // Gating this slot on the attack was wrong: it made a due top-priority
        // survival cast (even an emergency heal) sit out a whole round while its
        // own slot sat free, for a contention that doesn't exist. The one real
        // per-round limit is the shared between-round slot itself — enforced below.

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

        string? cast = RunDecisionPass(healEnabled, blessEnabled, ownUse);
        // Mark the round's single between-round slot spent so a second Evaluate this
        // round doesn't send another (doomed) cast; freed at the round boundary / window.
        if (cast is not null) _betweenRoundSlotUsedAt = _now();
        return cast;
    }

    // A falling HP read means a burst is still landing — stamp it so the pass that
    // reads it is treated as unsettled. Done here rather than in our own Hp handler:
    // other PlayerState subscribers run first and some re-enter Evaluate (the party
    // self row mirrors our HP, and its HpPercent change triggers a pass), so a stamp
    // in the handler came too late and a Major heal went out on a partial HP=273
    // while the burst ended at 160 (report paradigm-20261004-054304).
    private void NoteHpReading()
    {
        if (_state.Hp < _lastSeenHp) _hpBurstAt = _now();
        _lastSeenHp = _state.Hp;
    }

    // True while a round burst is still landing — a damage-line tick or an HP drop
    // within BurstSettleWindow. Always false when no scheduler is wired.
    private bool HpSettling() =>
        _scheduleSettledPass is not null && _now() - _hpBurstAt < BurstSettleWindow;

    // Queue one re-evaluation for when the current burst goes quiet. A later HP drop
    // extends the window; the pass then finds it still settling and re-queues for the
    // remainder, so at most one callback is ever outstanding.
    private void ScheduleSettledPass()
    {
        if (_settledPassPending || _scheduleSettledPass is null) return;
        _settledPassPending = true;
        TimeSpan wait = _hpBurstAt + BurstSettleWindow - _now();
        TimeSpan margin = _projectedTickAt + ProjectedTickMargin - _now();
        if (margin > wait) wait = margin;
        _scheduleSettledPass(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, () =>
        {
            _settledPassPending = false;
            if (!_disposed) Evaluate();
        });
    }

    // True for ProjectedTickMargin after a round tick that wasn't seen on the wire.
    // Always false when no scheduler is wired, like the burst hold.
    private bool PastProjectedTickOnly() =>
        _scheduleSettledPass is not null && _now() - _projectedTickAt < ProjectedTickMargin;

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

    // Walk the priority list and fire the first ready candidate. Returns the spell
    // that was cast (for diagnostics), or null if nothing matched.
    private string? RunDecisionPass(bool healEnabled, bool blessEnabled, (string What, bool MustGoNow)? ownUse)
    {
        SpellsSettings spells = _readSpells();
        HealthSettings health = _readHealth();

        PartySettings? partySettings = _readPartySettings?.Invoke();

        // Projected-round margin (see _projectedTickAt): nothing goes out on a round
        // boundary the client only worked out, until a moment after it.
        if (PastProjectedTickOnly())
        {
            ScheduleSettledPass();
            return null;
        }

        // Burst-settle guard (see _hpBurstAt): while a round's hits are still landing,
        // HP is a partial read. Only an Emergency heal already due on it may fire;
        // everything else waits for the settled pass, which picks on the final HP.
        bool settling = HpSettling();
        if (settling)
        {
            ScheduleSettledPass();
            if (!healEnabled || PickEmergencySelfHeal(spells, health) is null)
            {
                if (!_settleHoldLogged)
                {
                    _settleHoldLogged = true;
                    _log?.Combat(LogCategory,
                        $"between-round casts held — HP still settling mid-burst (hp={_state.Hp}/{_state.MaxHp}); re-deciding once it lands.");
                }
                return null;
            }
        }
        else
        {
            _settleHoldLogged = false;
        }

        // Log the full between-round queue (all due candidates, priority-ordered) before
        // firing the top one. Reached only when a cast can actually go out — Evaluate
        // gates on the cast cooldown upstream — so it lands ~once per between-round, not
        // every heartbeat poll.
        LogDueQueue(spells, health, partySettings, healEnabled, blessEnabled);

        // Sneak keeping (Game.Stealth.SneakGuard): every in-between cast ends a sneak,
        // so while a backstab is still owed here, our sneaked move is landing, or we're
        // sneaking past NPCs we won't fight, the in-between casts wait — heals included
        // (user, 2026-09-28): until the backstab fires, or until a room without NPCs
        // where the cast can go out and StealthManager.ReSneakAfterCast (on CastFired)
        // re-sneaks. Debuffs keep their own backstab gate in the combat engine. It holds
        // while resting too: a ShadowRest character rests stealthed beside NPCs, and a
        // cast there would give it away. A flee from the health gates lets the emergency
        // heal through; StealthManager holds its re-sneak until the heal has gone out.
        bool deferSneakMaintenance = _deferMaintenanceWhileStealthed?.Invoke() == true;
        bool emergencyHealBypass = _emergencyHealBypass?.Invoke() == true;
        _slotCastHeldForSneak = false;
        _clientUseHeldForSneak = false;
        SneakHeldCategory = null;

        // The client's own use (a hazard counter's buff) goes ahead of the user's
        // categories, an emergency heal excepted: it is due only in a short window
        // before the buff ends, and one round's delay to a buff or a lesser heal costs
        // less than the buff lapsing in a room that punishes it. It is one
        // between-round cast like the rest and is held for sneak keeping the same way,
        // until it can no longer wait. Mid-burst it waits for the settled pass.
        if (ownUse is { } own && !settling
            && !(healEnabled && PickEmergencySelfHeal(spells, health) is not null))
        {
            if (deferSneakMaintenance && !own.MustGoNow)
            {
                _log?.Combat(LogCategory,
                    $"sneak kept: {own.What} (hazard buff) held — the backstab is still owed, our sneaked move is landing, or we're sneaking past hostiles.");
                _clientUseHeldForSneak = true;
                _clientUseHeld?.Invoke();
            }
            else if (_fireClientUse?.Invoke() == true)
            {
                _log?.Combat(LogCategory,
                    $"hazard buff fired {own.What} hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa}");
                return own.What;
            }
        }

        foreach (SpellCategory category in PrioritisedCategories(spells))
        {
            CastCandidate? pick = category switch
            {
                // Heal / cure / debuff stay under AutoHeal; buffing under
                // AutoBless. When only one master is on, the other's categories
                // are skipped rather than the whole loop bailing.
                SpellCategory.EmergencyHeal   => healEnabled ? Wrap(PickEmergencySelfHeal(spells, health)) : null,
                SpellCategory.DownedAllyHeal  => healEnabled ? PickDownedAllyHeal(partySettings, spells) : null,
                SpellCategory.MinorPartyHeal  => healEnabled ? PickMinorPartyHeal(partySettings) : null,
                SpellCategory.MajorPartyHeal  => healEnabled ? PickMajorPartyHeal(partySettings) : null,
                SpellCategory.MinorSelfHeal   => healEnabled ? Wrap(PickMinorSelfHeal(spells, health)) : null,
                SpellCategory.MajorSelfHeal   => healEnabled ? Wrap(PickMajorSelfHeal(spells, health)) : null,
                SpellCategory.Curing          => healEnabled ? PickCure(spells) : null,
                SpellCategory.Buffing         => blessEnabled ? PickBuff(health, priority: false) : null,
                SpellCategory.PriorityBuffing => blessEnabled ? PickBuff(health, priority: true) : null,
                SpellCategory.Debuffing       => healEnabled ? PickDebuff() : null,
                _                              => null,
            };

            if (pick is not { } cand) continue;
            if (string.IsNullOrWhiteSpace(cand.Spell)) continue;

            // Mid-burst only the Emergency heal may jump ahead of the settled pass. A due
            // cast the user ranked above it keeps its place: stop here and let the
            // settled pass fire it on the final HP.
            if (settling && category != SpellCategory.EmergencyHeal) return null;

            // Sneak keeping: a cast came due while the sneak is being kept — hold it
            // (see deferSneakMaintenance above). Logged only here (where a
            // candidate was actually produced) so the hold isn't spammed every poll.
            if (deferSneakMaintenance && category is not SpellCategory.Debuffing
                && !(emergencyHealBypass && category == SpellCategory.EmergencyHeal))
            {
                _log?.Combat(LogCategory,
                    $"sneak kept: {cand.Spell} ({category}) held — the backstab is still owed, our sneaked move is landing, or we're sneaking past hostiles.");
                // Only one we could pay for stops the walk in the next clear room.
                if (UnpayableCost(cand.Spell) is null)
                {
                    _slotCastHeldForSneak = true;
                    SneakHeldCategory ??= category;
                }
                continue;
            }

            // Item-cast buff (#-token in a Bless slot): bypass the raw cast path
            // entirely — run the equip → use → re-equip sequence and key the
            // recast timer by the token. Only buff slots carry tokens.
            if (category is SpellCategory.Buffing or SpellCategory.PriorityBuffing
                && ItemCastToken.IsToken(cand.Spell))
            {
                if (TryFireItemCast(cand.Spell, cand.RecastMarginSec))
                {
                    NoteBuffCast(category, cand);
                    return cand.Spell;
                }
                continue; // unresolved / non-buff item — let a later category try
            }

            // Don't attempt a cast we can't pay for. Combat (offensive /
            // debuff) casts are gated by the Combat tab's MinManaPerCast
            // threshold, so the game-data affordability check applies only to
            // the survival categories owned here; an unknown cost never blocks.
            // Skip-and-continue so a cheaper lower-priority cast can still fire. (At a
            // lair's doorstep a buff never gets here unpayable: its picker has already
            // passed over it for the next due one, see CanPayFor.)
            if (category != SpellCategory.Debuffing && UnpayableCost(cand.Spell) is { } cost)
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

            // Self-cure throttle: a stuck condition flag would otherwise re-fire the same
            // cure every round (see SelfCureRetryWindow). Cap re-casts of the same self-cure
            // to once per window; a working cure clears the flag well before then.
            bool isSelfCure = category == SpellCategory.Curing && cand.Target is null;
            if (isSelfCure
                && _lastSelfCureAt.TryGetValue(cand.Spell, out DateTime lastCure)
                && _now() - lastCure < SelfCureRetryWindow)
            {
                _log?.Combat(LogCategory,
                    $"{category} skip spell={cand.Spell} (throttled — cast "
                    + $"{(_now() - lastCure).TotalSeconds:0}s ago, condition flag still set)");
                continue;
            }

            if (!_cast.TryCast(cand.Spell, cand.Target)) return null;

            // Combat-sourced debuff landed — advance the combat engine's
            // once-per-room / once-per-target bookkeeping so it won't re-fire.
            if (category == SpellCategory.Debuffing)
            {
                _combatDebuffCommit?.Invoke();
                _lairHold?.NoteDebuffSent(cand.Spell);
            }

            if (isSelfHeal)
                _lastSelfHealCast = (cand.Spell, _state.Hp, _state.Ma, _now());
            if (isSelfCure)
                _lastSelfCureAt[cand.Spell] = _now();
            if (category == SpellCategory.Curing && cand.Target is { } cured)
                NotePartyCureCast(cand.Spell, cured);

            // Buff cast sent — start the recast clock immediately. A party buff
            // (targeted) arms CasterMessage confirmation so the timer starts on
            // the observed land; a self buff (null target) starts an optimistic
            // timer NOW so a stale re-evaluation this round can't re-issue it
            // before the AppliedMessage confirms with the true duration.
            if (category is SpellCategory.Buffing or SpellCategory.PriorityBuffing)
            {
                if (cand.Target is { } tgt) ArmPartyBuffConfirm(cand.Spell, tgt, cand.RecastMarginSec);
                else StartSelfBuffTimer(cand.Spell, cand.RecastMarginSec);
                // A staged mana-regen reroll just went out through the priority
                // loop — consume it so it isn't re-offered next pass (the reroller
                // re-stages one if the fresh roll is still below threshold).
                if (string.Equals(cand.Spell, _pendingManaRegenReroll, StringComparison.OrdinalIgnoreCase))
                    _pendingManaRegenReroll = null;
                NoteBuffCast(category, cand);
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

    // A loop's lair-entry hold follows the buffs that go out: one standing outside a
    // lair for its priority buffs counts each (and says so on the always-on log, where
    // its wait is), and any buff going out is a buff it held getting its cast.
    private void NoteBuffCast(SpellCategory category, CastCandidate cast)
    {
        if (category == SpellCategory.PriorityBuffing) _lairHold?.NotePriorityBuffCast(BuffName(cast));
        else _lairHold?.NoteOrdinaryBuffCast(BuffName(cast));
    }

    // The combat engine sent its pre-attack debuff itself, not through a decision
    // pass: the round's between-round cast is spent, and a lair-entry hold keeping
    // buffs back for that debuff can let them through.
    public void NotePreAttackDebuffSent(string spell)
    {
        MarkBetweenRoundSlotUsed();
        _lairHold?.NoteDebuffSent(spell);
    }

    // Combat-diagnostics view of the between-round queue: every DUE candidate the
    // engines want cast this round — self/party heals, a cure, and every buff inside
    // its recast window — in type-priority order (the Spells-tab priorities), formatted
    // `code(typePrio)`; buffs additionally carry their slot number as `code(typePrio-
    // slot)`. Read-only: the heal/party/cure pickers don't mutate state, and buffs are
    // enumerated directly (so PickManaRegenReroll's reroll consumption is untouched).
    // Debuffs are omitted — they're the combat engine's decision and re-peeking it here
    // is not guaranteed side-effect-free. Only logged when the queue is non-empty AND
    // differs from the last one logged: it's evaluated every tick, and an unchanged
    // queue re-logged ~once a second filled the log ring on its own, pushing a whole
    // walk's worth of navigation history out of the bug report's log tail.
    private void LogDueQueue(SpellsSettings spells, HealthSettings health, PartySettings? party,
        bool healEnabled, bool blessEnabled)
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
        if (healEnabled)
        {
            // Order added here doesn't matter — the queue re-sorts by each
            // category's real priority number below.
            AddSurvival(SpellCategory.EmergencyHeal, PickEmergencySelfHeal(spells, health));
            AddSurvival(SpellCategory.DownedAllyHeal, PickDownedAllyHeal(party, spells)?.Spell);
            AddSurvival(SpellCategory.MinorPartyHeal, PickMinorPartyHeal(party)?.Spell);
            AddSurvival(SpellCategory.MajorPartyHeal, PickMajorPartyHeal(party)?.Spell);
            AddSurvival(SpellCategory.MinorSelfHeal, PickMinorSelfHeal(spells, health));
            AddSurvival(SpellCategory.MajorSelfHeal, PickMajorSelfHeal(spells, health));
            AddSurvival(SpellCategory.Curing, PickCure(spells)?.Spell);
        }
        if (blessEnabled)
        {
            int plainPrio = CategoryPriority(spells, SpellCategory.Buffing);
            int starredPrio = CategoryPriority(spells, SpellCategory.PriorityBuffing);
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
                    int buffPrio = slot.PriorityBuff ? starredPrio : plainPrio;
                    q.Add((buffPrio, slotNo, $"{code}({buffPrio}-{slotNo})"));
                }
            }
        }

        if (q.Count == 0)
        {
            _lastLoggedQueue = null;
            return;
        }
        string ordered = string.Join(", ", q.OrderBy(x => x.Prio).ThenBy(x => x.Slot).Select(x => x.Text));
        if (ordered == _lastLoggedQueue) return;
        _lastLoggedQueue = ordered;
        _log.Combat(LogCategory, $"{{spells queued={ordered}}}");
    }

    // The configured type-priority number for a between-round category (the Spells-tab
    // priorities). Every category — Emergency and DownedAlly included — reads a
    // user-reorderable slot; the defaults put Emergency first (1) and DownedAlly
    // seventh, but the user can move any of them.
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
        SpellCategory.PriorityBuffing => s.PriorityPriorityBuffs,
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
        if (_drawOutcomes?.Invoke(token) is { Count: > 0 }) return TryFireDraw(token);
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
        // (leads), DownedAlly to slot 7, but both are reorderable like the rest.
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
            (SpellCategory.PriorityBuffing, s.PriorityPriorityBuffs),
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

    // True when the emergency self-heal's trigger is met (and heals are on).
    // True when the last decision pass held a due cast (not a debuff) only because the
    // sneak was being kept. StealthManager stops the walk for it in the next NPC-free
    // room, where the cast can go out and we re-sneak.
    public bool HasSneakHeldCast => _slotCastHeldForSneak || _clientUseHeldForSneak;

    // The two things that can be held, kept apart because they stop being due by
    // different roads. A slot's cast is re-judged by every decision pass. The client's
    // own use can stop being due with no pass run at all (both toggles off, the
    // round's cast spent): the step sent it, it was drunk by hand, the route turned
    // away, the master switch went off. Left set then, the walk was held in every
    // room with no NPCs for a cast that was never coming.
    private bool _slotCastHeldForSneak;
    private bool _clientUseHeldForSneak;

    // What the first such held cast is — heal, cure or buff — for the Navigation
    // top bar's chip while the walk stops to cast it.
    public SpellCategory? SneakHeldCategory { get; private set; }

    public bool IsEmergencyHealDue =>
        _isEnabled() && PickEmergencySelfHeal(_readSpells(), _readHealth()) is not null;

    // The self-heal tiers (SelfHealPicker has the rules) read against the live pools.
    private string? PickEmergencySelfHeal(SpellsSettings spells, HealthSettings health) =>
        SelfHealPicker.Emergency(spells, health, SelfHealState());

    private string? PickMajorSelfHeal(SpellsSettings spells, HealthSettings health) =>
        SelfHealPicker.Major(spells, health, SelfHealState());

    private string? PickMinorSelfHeal(SpellsSettings spells, HealthSettings health) =>
        SelfHealPicker.Minor(spells, health, SelfHealState());

    private SelfHealInputs SelfHealState() => new(
        _state.Hp, _state.MaxHp, _state.Ma, _state.MaxMa, _state.InCombat,
        Resting: _state.Position == PlayerPosition.Resting || _hpRestDeferredHere?.Invoke() == true,
        HealHpTrigger: ResolveHealHpTrigger,
        Affordable: SpellAffordable,
        HpRegenRecastDue: spell => IsRecastDue("", spell));

    // Mirrors the decision-pass affordability skip (an unknown cost never blocks):
    // a spell is castable when we don't know its cost or the pool covers it. Used
    // so a minor heal only yields to major when major could actually fire.
    private bool SpellAffordable(string spell)
        => _manaCostLookup?.Invoke(spell) is not { } cost || _state.Ma >= cost;

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
         && SelfCureDue(spells.CureHoldsSpell, spells.CureHoldsAfterCombat))
            return spells.CureHoldsSpell;

        if (_conditions.IsPoisoned
         && SelfCureDue(spells.CurePoisonSpell, spells.CurePoisonAfterCombat))
            return spells.CurePoisonSpell;

        if (_conditions.IsDiseased
         && SelfCureDue(spells.CureDiseaseSpell, spells.CureDiseaseAfterCombat))
            return spells.CureDiseaseSpell;

        if (_conditions.IsBlinded
         && SelfCureDue(spells.CureBlindnessSpell, spells.CureBlindnessAfterCombat))
            return spells.CureBlindnessSpell;

        // Confusion has no cure slot on SpellsSettings: it is rare and short-lived
        // in stock MajorMUD.
        return null;
    }

    private bool SelfCureDue(string? spell, bool afterCombat) =>
        !string.IsNullOrWhiteSpace(spell) && !CureWaitsForFightEnd(spell, target: null, afterCombat);

    // A cure ticked "Cure after combat" sits the fight out, on us or on a party
    // member, and the picker moves on to the next ailment. A cure takes the round's
    // one between-round cast, and the next hit often puts the poison or knockdown
    // straight back (report paradigm-20261004-054304), so a user can keep that cast
    // for healing. InCombat stays true until the room is clear, through the
    // *Combat Off* between kills. Logged once per fight per cure.
    private bool CureWaitsForFightEnd(string spell, string? target, bool afterCombat)
    {
        if (!afterCombat || !_state.InCombat) return false;
        string key = target is null ? spell : $"{spell} {target}";
        if (_curesHeldThisFight.Add(key))
            _log?.Combat(LogCategory, $"Curing {key} held until the fight is over (Cure after combat)");
        return true;
    }

    // Walk live party members and cast the configured cure spell on the first member
    // whose ailment chip is set. The chip is mirrored from the member's inbound
    // @held / .@poisoned / .@diseased / .@blind announce by PartyAilmentTracker. Same
    // cure-spell config as self-cure; the target string routes the cast to the
    // member. Internal order mirrors self-cure (hold → poison → disease → blindness);
    // a held member was left out, so a @held never got its cure (report
    // paradigm-20260929-230346).
    // Confusion has no cure spell — a @confused chip is never picked up here (gap in
    // PartyAilmentTracker).
    private CastCandidate? PickPartyCure(SpellsSettings spells)
    {
        if (_party is null) return null;
        if (_party.Members.Count == 0) return null;

        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf) continue;
            CastCandidate? cure =
                PartyCureFor(m, m.Held, spells.CureHoldsSpell, spells.CureHoldsAfterCombat)
                ?? PartyCureFor(m, m.Poisoned, spells.CurePoisonSpell, spells.CurePoisonAfterCombat)
                ?? PartyCureFor(m, m.Diseased, spells.CureDiseaseSpell, spells.CureDiseaseAfterCombat)
                ?? PartyCureFor(m, m.Blinded, spells.CureBlindnessSpell, spells.CureBlindnessAfterCombat);
            if (cure is not null) return cure;
        }
        return null;
    }

    // The cure for one of a member's ailments, or null when they don't have it, no
    // spell is configured, the spell is in its back-off for them, or it is waiting for
    // the fight to end (so the picker moves on to their next ailment, then the next
    // member). A chip that has stayed clear since the last cast means the cure took:
    // the back-off record is dropped.
    private CastCandidate? PartyCureFor(PartyMember m, bool afflicted, string? spell, bool afterCombat)
    {
        if (string.IsNullOrWhiteSpace(spell) || MemberTarget(m) is not { } target) return null;
        string key = PartyCureKey(spell, target);
        if (_partyCures.TryGetValue(key, out (DateTime At, int Casts) last))
        {
            TimeSpan since = _now() - last.At;
            if (!afflicted)
            {
                if (since >= PartyCureSettledAfter) _partyCures.Remove(key);
                return null;
            }
            if (since < PartyCureWindow(last.Casts)) return null;
        }
        else if (!afflicted) return null;

        return CureWaitsForFightEnd(spell, target, afterCombat) ? null : new CastCandidate(spell, target);
    }

    private static string PartyCureKey(string spell, string target) => $"{spell} {target}";

    // 15 s after the first cast, doubling with each further one, capped.
    private static TimeSpan PartyCureWindow(int casts)
    {
        double secs = PartyCureRetryWindow.TotalSeconds * Math.Pow(2, Math.Min(casts - 1, 10));
        return secs >= PartyCureMaxWindow.TotalSeconds ? PartyCureMaxWindow : TimeSpan.FromSeconds(secs);
    }

    private void NotePartyCureCast(string spell, string target)
    {
        string key = PartyCureKey(spell, target);
        int casts = _partyCures.TryGetValue(key, out (DateTime At, int Casts) last) ? last.Casts + 1 : 1;
        _partyCures[key] = (_now(), casts);
        if (casts > 1)
            _log?.Info(LogCategory,
                $"party cure {spell} on {target}: cast {casts} and the ailment keeps coming back — "
                + $"next try in {PartyCureWindow(casts).TotalSeconds:0}s if it's still there");
    }

    // Party cures waiting out their back-off, for the bug report.
    public IReadOnlyList<string> DescribePartyCureBackoff()
    {
        DateTime now = _now();
        List<string> rows = new();
        foreach (KeyValuePair<string, (DateTime At, int Casts)> kv in _partyCures)
        {
            double left = (PartyCureWindow(kv.Value.Casts) - (now - kv.Value.At)).TotalSeconds;
            rows.Add($"{kv.Key}: {kv.Value.Casts} cast(s), "
                + (left > 0 ? $"next in {left:0}s" : "free to cast"));
        }
        return rows;
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

    // Name-targeted heal for a dropped ally that's been aided back to positive HP
    // but hasn't rejoined `par` yet. Confirmed mechanic: a heal cast at such an ally
    // by name still lands even though they're off the roster, so this keeps topping
    // them up until they recover / rejoin.
    //
    // Spell = the Emergency heal slot's spell (a downed ally is the definition of an
    // emergency, so it gets the same big heal the caster would use on itself in a
    // pinch), falling back to the major then minor PARTY heal when no emergency
    // spell is configured. Priority is unchanged — this runs in the DownedAlly slot
    // (its own reorderable priority), NOT the Emergency-heal slot; this only decides
    // the SPELL, not when the rescue fires.
    private CastCandidate? PickDownedAllyHeal(PartySettings? settings, SpellsSettings spells)
    {
        if (_downedAllies is null) return null;
        IReadOnlyList<string> allies = _downedAllies();
        if (allies.Count == 0) return null;

        string? spell = FirstNonBlank(
            spells.EmergencyHealSpell, settings?.MajorPartyHealSpell, settings?.MinorPartyHealSpell);
        if (string.IsNullOrWhiteSpace(spell)) return null;
        return new CastCandidate(spell, Target: GivenName(allies[0]));
    }

    private static string? FirstNonBlank(params string?[] candidates)
    {
        foreach (string? c in candidates)
            if (!string.IsNullOrWhiteSpace(c)) return c;
        return null;
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
            // SELF counts toward the AOE member gate above (a group heal covers
            // everyone, so self + an ally below can reach AoeMinMembers), but the
            // party-settings heal spells are for party MEMBERS only — they are never
            // cast AT self. The caster's own dips are the Spells + Ailments self-heal
            // slots' (mihe / mahe / emergency) job. So self is excluded as both the
            // single-target pick AND the fallback trigger (`lowest`, below).
            if (m.IsSelf) continue;
            if (lowest is null || m.HpPercent < lowest.HpPercent)
                lowest = m;
        }
        if (below == 0) return null;

        // The party AOE heal is HARD-gated by the member count: it fires ONLY when
        // AoeMinMembers or more members are below the threshold. AoeMinMembers is the
        // "use party healing spells when N or more members meet threshold" picker,
        // itself bounded 2–6, so "2" means two-or-more, full stop — the AOE never fires
        // for a lone below-member, even when no single-target spell is configured to
        // reach them (report: mrai firing at one member below). Below the count, the
        // single-target slot heals the individual dip; with no single-target set,
        // nothing party-side fires until the count is met.
        int aoeMin = Math.Max(2, settings.AoeMinMembers);
        if (below >= aoeMin && !string.IsNullOrWhiteSpace(aoeSpell))
            return new CastCandidate(aoeSpell, Target: null);

        if (!string.IsNullOrWhiteSpace(singleSpell) && lowest is not null)
            return new CastCandidate(singleSpell, Target: MemberTarget(lowest));

        return null;
    }

    // ----- Buffing ----------------------------------------------------

    // Pick the next buff to (re)cast: the first due one from the staged mana-regen
    // reroll, then the unified buff list in priority order (DueBuffs). Due means never
    // confirmed-active, or within the slot's RecastMarginSec expiry window.
    //
    // "Active" is duration-based: a buff's timer starts only when we observe OUR
    // successful cast (self via the ConditionTracker AppliedMessage, party via the
    // CasterMessage matcher) and runs for SpellCalculator.Duration seconds. No
    // confirmation => no timer => the next eligible pass re-attempts (the
    // CastCoordinator cooldown prevents spam). The ConditionEnded wear-off line
    // clears the timer early for dispels / area-clears.
    //
    // MA-floor gate: a buff is considered only while MA is at or above its own
    // BlessIfAboveMa (BuffSlot). Mirrors MegaMUD's "don't burn buff mana when we'll
    // need it for heals soon" behaviour, per buff.
    //
    // priority: the buffs ticked "Priority buff" (BuffSlot.PriorityBuff) when true,
    // every other buff when false. The two are separate between-round categories with
    // their own rank; everything else about a buff is the same in either.
    //
    // peek: the caller only asks what is due and will cast nothing. See DueBuffs.
    private CastCandidate? PickBuff(HealthSettings health, bool priority, bool peek = false)
    {
        // A loop is about to step into a lair and is keeping the round's cast for the
        // pre-attack debuff (LairEntryDebuffHold logs its own wait). While it lets the
        // priority buffs go first, only the others are kept back.
        // Standing there for mana it holds every buff but the ones that bring mana back.
        bool holdsAll = _lairHold is { BlockingBuffs: true };
        bool forMana = holdsAll && _lairHold is { HoldingForMana: true };
        if (holdsAll && !forMana) return null;
        if (!holdsAll && !priority && _lairHold is { BlockingOrdinaryBuffs: true }) return null;

        // A buff that loop let through and has given up on (it went out as often as
        // one may there) is held as well; the next due one takes its place.
        foreach (CastCandidate due in DueBuffs(health, priority, peek))
        {
            if (forMana && !due.RestoresMana) continue;
            if (_lairHold?.HasGivenUpOn(BuffName(due)) == true) continue;
            return due;
        }
        return null;
    }

    // Wire what tells a mana-regen buff from the rest: the roll spells whose buff is a
    // rate of mana regeneration. With a slot's own "cast before resting for mana" tick
    // and a staged reroll, these are the buffs that bring mana back, which a loop
    // standing outside a lair for mana lets through. Optional.
    public void SetManaRegenBuffProbe(Func<string, bool> isManaRegenBuff)
    {
        ArgumentNullException.ThrowIfNull(isManaRegenBuff);
        _isManaRegenBuff = isManaRegenBuff;
    }
    private Func<string, bool>? _isManaRegenBuff;

    // Every due buff of one kind, in cast order: PickBuff casts the first. Lazy, so
    // taking only the first does no more work than picking it did.
    //
    // The decision pass tidies as it picks: it drops a staged reroll that can't be
    // used, takes a member who is back in the room off the hidden list, and logs why
    // a buff was passed over. With peek set none of that happens. A question about
    // what is due (the lair-entry hold's, a log line's, the bug report's) must leave
    // the next decision exactly as it would have been: a bug report captured during a
    // lair hold once dropped the staged reroll the hold was keeping for later.
    private IEnumerable<CastCandidate> DueBuffs(HealthSettings health, bool priority, bool peek)
    {
        // Buff-strip-room gate: the room casts a buff-removal spell on entry, so
        // any buff we put up is torn straight back off. Skip the whole category
        // here — heals / cures still run their own paths.
        if (_buffStripRoom?.Invoke() == true)
        {
            if (!peek) _log?.Combat(LogCategory, "buff skipped — room strips buffs on entry.");
            yield break;
        }

        // Token buff-pause: a transport-token use is imminent and will wipe every buff
        // (negate magic), so don't burn mana putting one up now — it resumes when the
        // use fires or the pause times out.
        if (_tokenBuffPause?.Invoke() == true)
        {
            if (!peek) _log?.Combat(LogCategory, "buff skipped — token use imminent (buffs about to be wiped).");
            yield break;
        }

        // A staged mana-regen reroll leads — it's an immediate below-threshold recast
        // (front of the queue). Then the ONE unified buff list (self bless / regen /
        // when-full, whole-party, per-member). Mana-regen maintenance is just a
        // CastOnSelf slot in that list, so DueUnifiedBuffs handles it in place.
        if (PickManaRegenReroll(health, priority, peek) is { } rr) yield return rr;
        foreach (CastCandidate due in DueUnifiedBuffs(health, priority, peek)) yield return due;
    }

    // Whether mana is at or above this buff's own floor (BuffSlot.BlessIfAboveMa,
    // read per MaThresholdMode — a percent of MaxMa, or an absolute MA / kai value).
    // MA unknown (MaxMa 0) holds every mana-drawing buff.
    private bool ManaClearsFloor(Models.Profile.BuffSlot slot, HealthSettings health) =>
        _state.MaxMa > 0
        && _state.Ma >= PoolThreshold.Resolve(health.MaThresholdMode, slot.BlessIfAboveMa, _state.MaxMa);

    // Per-slot buff affordability for the buff pickers. The mana floor applies to
    // *mana-drawing* buffs only: a regular spell buff is allowed only when mana
    // clears its slot's floor (the cast's cost is checked as it is offered, CanPayFor); an
    // item-cast token is allowed when its use-spell is free (cost 0 / unresolved —
    // recast regardless of mana) or, when it draws mana, only if we both clear the
    // floor and can pay the cost from the current pool.
    private bool IsBuffAffordable(Models.Profile.BuffSlot slot, HealthSettings health)
    {
        string code = slot.Spell!;
        if (!ItemCastToken.IsToken(code)) return ManaClearsFloor(slot, health);
        int? cost = _itemCastManaCost?.Invoke(code);
        if (cost is not > 0) return true; // free / unresolved item-cast: never mana-gated
        return ManaClearsFloor(slot, health) && _state.Ma >= cost.Value;
    }

    // What a spell costs when mana is under it, else null. An unknown cost never
    // blocks. The one test of "can't be paid for" the dispatch and the buff pickers use.
    private int? UnpayableCost(string spell) =>
        _manaCostLookup?.Invoke(spell) is { } cost && _state.Ma < cost ? cost : null;

    // While a loop stands at a lair's doorstep, a due buff is offered only when its
    // spell can be paid for: one that can't is passed over for the next due buff, and
    // is not something the wait waits for (user, 2026-10-10, of the lair settings: "in
    // the event that a user cant cast a buff due to not enough mana, then it should
    // skip it"). Everywhere else the unpayable buff is offered as it always was, the
    // dispatch turns it down, and the buffs behind it wait for it. That is what keeps
    // cheap, short buffs from spending the mana a costly one at the head of the list
    // is waiting for: passed over everywhere, such a buff was never cast at all.
    // Widening the skip is this one condition.
    private bool CanPayFor(string spell, bool priority, bool peek)
    {
        if (_lairHold is not { SkipsUnpayableBuffs: true }) return true;
        if (ItemCastToken.IsToken(spell) || UnpayableCost(spell) is not { } cost) return true;
        if (!peek)
            _log?.Combat(LogCategory,
                $"{(priority ? SpellCategory.PriorityBuffing : SpellCategory.Buffing)} skip spell={spell} "
                + $"cost={cost} ma={_state.Ma} (insufficient mana) — the next due buff is offered instead");
        return false;
    }

    // A staged mana-regen reroll — an immediate below-threshold recast that jumps the
    // whole buff queue (bypasses the slot's recast timer) but still honours its
    // slot's rest / combat conditions and mana floor. If it can't be paid for right
    // now, drop it (the reroller re-stages on the next landing if still below
    // threshold) rather than stall the walk. The mana-regen buff's own MAINTENANCE
    // recast is a normal CastOnSelf slot in the unified list (DueUnifiedBuffs).
    private CastCandidate? PickManaRegenReroll(HealthSettings health, bool priority, bool peek)
    {
        if (_pendingManaRegenReroll is not { } reroll) return null;
        // The slot the reroll belongs to carries its conditions; a reroll whose slot
        // is gone has nothing to say when it may cast, and is dropped.
        Models.Profile.BuffSlot? slot = _readPartyBuffs?.Invoke()?.Slots.Find(
            s => string.Equals(s.Spell?.Trim(), reroll, StringComparison.OrdinalIgnoreCase));
        if (slot is null)
        {
            if (!peek) _pendingManaRegenReroll = null;
            return null;
        }
        if (slot.PriorityBuff != priority) return null;   // the other buff category's to cast
        if (!BuffTimingAllowed(slot)) return null;
        if (!IsBuffAffordable(slot, health))
        {
            if (!peek) _pendingManaRegenReroll = null;
            return null;
        }
        // Over its floor but under its cost: it stays staged for when mana is back,
        // and the buffs behind it are offered meanwhile.
        return CanPayFor(reroll, priority, peek)
            ? new CastCandidate(reroll, Target: null, DefaultRecastMarginSec, RestoresMana: true)
            : null;
    }

    // A buff's own timing gate, for every cast of it — on ourselves, on a party
    // member, across the party, or as a mana-regen reroll: allowed unless we're in
    // combat without its BlessDuringCombat, or in a TRIGGERED recovery rest without
    // its BlessWhileResting. Idle / standing / idly-resting is always allowed. Both
    // default OFF.
    private bool BuffTimingAllowed(Models.Profile.BuffSlot slot)
    {
        if (_state.InCombat && !slot.BlessDuringCombat) return false;
        if ((_isTriggeredRest?.Invoke() ?? false) && !slot.BlessWhileResting) return false;
        return true;
    }

    // Walk the ONE unified buff list (CharacterProfile.PartyBuffs) in priority order
    // and give each due buff in turn; the first is the one to cast. A slot can target
    // ourselves (CastOnSelf), the whole party in one cast (Targets 10 / 13,
    // WholePartyOn — lands on us too), and/or selected members (Targets 2). Every
    // target obeys the slot's own conditions (the mana floor, the rest / combat
    // switches, OnlyWhenHpFull / OnlyWhenMaFull); party / member targets also require
    // actually being in a party.
    //
    // A member is eligible only when in the party (MajorMUD parties are co-located, so
    // a roster name is in the room) — the one exception being a member who's HIDING:
    // the cast returns "You do not see <name> here!" and we back off (_hiddenTargets)
    // until we move or they reappear in "Also here:".
    private IEnumerable<CastCandidate> DueUnifiedBuffs(HealthSettings health, bool priority, bool peek)
    {
        if (_readPartyBuffs?.Invoke() is not { } buffs) yield break;

        // "When HP / MA full" fires at the REST-MAX target (the level we rest up to,
        // HealthSettings.RestMaxHp / RestMaxMa read per the threshold mode), not literal
        // 100% — so a buff meant for "topped off, ready for the next fight" triggers as
        // soon as a recovery rest finishes.
        int restMaxHp = PoolThreshold.Resolve(health.HpThresholdMode, health.RestMaxHp, _state.MaxHp);
        int restMaxMa = PoolThreshold.Resolve(health.MaThresholdMode, health.RestMaxMa, _state.MaxMa);

        // Party / member targets are only cast while actually in a party.
        bool inParty = _party?.IsInParty == true;

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
            if (slot.PriorityBuff != priority) continue;

            // Permanently removed by another configured buff — never maintain it (the
            // winner keeps stripping it). Skips self, member, and whole-party casts alike.
            if (suppressed is not null && suppressed.ContainsKey(slot.Spell)) continue;

            // Only-when-dark light spells are cast reactively by the auto-light system
            // on entering a dark room, not maintained here — skip them entirely.
            if (slot.OnlyWhenDark) continue;

            if (!IsBuffAffordable(slot, health)) continue;

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
            // gate. (Left unwired, the lock reads false, so the slot never fires.)
            bool timingAllowed = BuffTimingAllowed(slot);
            bool partyAllowed = inParty && timingAllowed;
            bool selfEligible = slot.CastBeforeRestingForMana
                ? (_isManaRestActive?.Invoke() ?? false)
                : timingAllowed;

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
            if (wantSelfCast
                && (IsRecastDue("", slot.Spell) || (partyWide && inParty && CarriedBuffRanOutOnParty(slot.Spell)))
                && CanPayFor(slot.Spell, priority, peek))
                yield return new CastCandidate(slot.Spell, Target: null, slot.RecastMarginSec,
                    RestoresMana: slot.CastBeforeRestingForMana || _isManaRegenBuff?.Invoke(slot.Spell) == true);

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
                    if (slot.NegatedTargets.Contains(key)) continue;
                    if (_hiddenTargets.Contains(key))
                    {
                        if (_isMemberInRoom?.Invoke(given) != true) continue;
                        if (!peek) _hiddenTargets.Remove(key);
                    }
                    if (!IsRecastDue(key, slot.Spell)) continue;
                    // The same spell for every member: unpayable for one is for all.
                    if (!CanPayFor(slot.Spell, priority, peek)) break;
                    yield return new CastCandidate(slot.Spell, given, slot.RecastMarginSec);
                }
            }
        }
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
        CasterMessageMatcher? matcher = CasterMessageMatcher.TryCreate(info.Caster);
        // A caster line that can't name who the spell went on (You cast {spellname}!,
        // a fixed line, or none) will never confirm this member. Waiting for one left
        // the timer unarmed for good, and the buff was cast on the same member every
        // round while the rest of the party went without (report
        // paradigm-20261008-222838). So the timer runs from the send, as a self-buff's
        // does, and is taken back on a refusal (OnCastFailed) or a member we can't see.
        bool timedFromSend = matcher is not { CanNameTarget: true };
        _pendingPartyCast = (shortCode, target, info.DurationSec, marginSec, matcher, timedFromSend, _now());
        if (!timedFromSend) return;

        string key = target.Trim().ToLowerInvariant();
        _activeUntil[(key, shortCode)] = (_now().AddSeconds(info.DurationSec), marginSec, (int)info.DurationSec);
        NoteSuccessfulCast(shortCode, key);
        _log?.Info(LogCategory,
            $"party-buff sent spell={shortCode} target={target} — its cast line names no target, so the timer runs "
            + $"from the send: duration={info.DurationSec}s, recast in {Math.Max(0L, info.DurationSec - marginSec)}s.");
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
// RestoresMana marks a buff that brings mana back (a mana-regen buff, or its staged
// reroll): the one kind a loop standing outside a lair for mana still lets out.
public readonly record struct CastCandidate(
    string Spell, string? Target,
    int RecastMarginSec = SpellsSettings.DefaultBlessRecastMarginSec,
    bool RestoresMana = false);

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
    // (defaults to slot 7).
    DownedAllyHeal = 1,
    MinorPartyHeal = 2,
    MajorPartyHeal = 3,
    MinorSelfHeal  = 4,
    MajorSelfHeal  = 5,
    Curing         = 6,
    Buffing        = 7,
    Debuffing      = 8,
    // The buffs ticked "Priority buff" (BuffSlot.PriorityBuff), cast at their own
    // rank (SpellsSettings.PriorityPriorityBuffs) instead of with the rest.
    PriorityBuffing = 9,
}
