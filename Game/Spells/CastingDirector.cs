using System.ComponentModel;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game.Spells;

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

    // Re-cast a buff once it's within this many seconds of expiry (or already
    // expired / never confirmed). Matches the user's "15→0s-of-expiry recast
    // window".
    private const int RecastMarginSec = 15;

    private readonly PlayerState _state;
    private readonly CastCoordinator _cast;
    private readonly Conditions.ConditionTracker? _conditions;
    private readonly PartyState? _party;
    private Func<bool>? _isStealthedFunc;
    private Func<(string Spell, string? Target)?>? _combatDebuffSource;
    private Action? _combatDebuffCommit;
    private Func<string, int?>? _manaCostLookup;
    private Func<bool>? _autoBlessEnabled;
    private Func<string, long?>? _itemCastDuration;
    private Func<string, bool>? _executeItemCast;
    private Func<string, int?>? _itemCastManaCost;
    private readonly Func<SpellsSettings> _readSpells;
    private readonly Func<HealthSettings> _readHealth;
    private readonly Func<PartySettings>? _readPartySettings;
    private readonly Func<bool> _isEnabled;
    private readonly LogService? _log;

    // ----- Buff-duration tracking (self + party) ----------------------
    // Per (targetKey, spellShort) → UTC instant the buff wears off.
    // targetKey "" = self; otherwise the member's given name lower-cased.
    private readonly Dictionary<(string Target, string Short), DateTime> _activeUntil = new();
    // The one outstanding party-buff cast awaiting CasterMessage
    // confirmation. CastCoordinator's cooldown guarantees ≤1 in flight.
    private (string Short, string Target, long DurationSec, CasterMessageMatcher Matcher)? _pendingPartyCast;
    private Func<string, (string Caster, long DurationSec)?>? _buffInfoByShort;
    private Func<MessageRecord, string?>? _shortFromAppliedRecord;
    private Func<int, string?>? _classNameByNumber;
    private Func<string, bool>? _isPartyWideBuff;
    private Action<string>? _selfBuffLandedSink;
    private Func<DateTime> _now = () => DateTime.UtcNow;
    private LineExtractor? _lines;

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

    // Hook to TickEngine.CombatTickElapsed — drives between-round evaluations.
    public void OnCombatTick() => Evaluate();

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

    // Wire a class-number → class-name resolver (typically backed by Classes.json)
    // so party-bless slots — which store the set of class numbers a buff applies to —
    // can be matched against each PartyMember.Class (a class name). Optional — until
    // wired, party-bless slots never match a member and the party-buff picker no-ops.
    public void SetClassResolver(Func<int, string?> classNameByNumber)
    {
        ArgumentNullException.ThrowIfNull(classNameByNumber);
        _classNameByNumber = classNameByNumber;
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

    // Wire a sink notified with the 4-letter cast code every time one of OUR
    // self-buffs is confirmed to have landed (via the ConditionTracker AppliedMessage
    // path). The mana-regen reroll engine subscribes here to learn when a code-145
    // roll spell (nature tap / mana flux) re-landed so it can read abil 145 and
    // reroll a bad value; the sink itself owns the spell / realm filtering. Optional
    // — until wired, self-buff landings only refresh the recast timer.
    public void SetSelfBuffLandedSink(Action<string> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _selfBuffLandedSink = sink;
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
        _pendingPartyCast = null;
    }

    // True when a buff on targetKey ("" = self) is due to be (re)cast: either never
    // confirmed-active, or within RecastMarginSec seconds of expiry.
    private bool IsRecastDue(string targetKey, string spellShort)
    {
        if (!_activeUntil.TryGetValue((targetKey, spellShort), out DateTime until))
            return true;
        return (until - _now()).TotalSeconds <= RecastMarginSec;
    }

    private void OnConditionApplied(MessageRecord r)
    {
        // A self-cast buff confirmed via its AppliedMessage — start (or
        // refresh) its duration timer keyed to self so the recast window
        // is honoured. Party-cast confirmation rides OnLine instead.
        if (_shortFromAppliedRecord?.Invoke(r) is { } shortCode)
        {
            if (_buffInfoByShort?.Invoke(shortCode) is { } info)
                _activeUntil[("", shortCode)] = _now().AddSeconds(info.DurationSec);
            // Feed the reroll engine: a re-landed code-145 roll spell wants its
            // fresh roll read off abil 145. The sink filters for the roll spell
            // + Paradigm realm; here we just report the confirmed landing.
            _selfBuffLandedSink?.Invoke(shortCode);
        }
        Evaluate();
    }

    private void OnConditionEnded(MessageRecord r)
    {
        // Server-confirmed early wear-off — drop the self timer so the next
        // pass re-attempts immediately rather than waiting out a stale clock.
        if (_shortFromAppliedRecord?.Invoke(r) is { } shortCode)
            _activeUntil.Remove(("", shortCode));
        Evaluate();
    }

    private void OnLine(LineExtractor.EmittedLine line)
    {
        if (_pendingPartyCast is not { } p) return;
        if (!p.Matcher.ConfirmsTarget(line.Text, p.Target)) return;

        string key = p.Target.Trim().ToLowerInvariant();
        _activeUntil[(key, p.Short)] = _now().AddSeconds(p.DurationSec);
        _log?.Combat(LogCategory,
            $"party-buff confirmed spell={p.Short} target={p.Target} " +
            $"duration={p.DurationSec}s");
        _pendingPartyCast = null;
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
        if (!_isEnabled()) return null;
        if (!_state.HasPromptData) return null;
        if (_state.MaxHp <= 0) return null;
        if (_state.Hp <= 0) return null;     // dead — DeathRecoveryManager owns this case
        if (_cast.IsCastBlocked) return null;

        SpellsSettings spells = _readSpells();
        HealthSettings health = _readHealth();

        PartySettings? partySettings = _readPartySettings?.Invoke();

        foreach (SpellCategory category in PrioritisedCategories(spells))
        {
            CastCandidate? pick = category switch
            {
                SpellCategory.MinorPartyHeal  => PickMinorPartyHeal(partySettings),
                SpellCategory.MajorPartyHeal  => PickMajorPartyHeal(partySettings),
                SpellCategory.MinorSelfHeal   => Wrap(PickMinorSelfHeal(spells, health)),
                SpellCategory.MajorSelfHeal   => Wrap(PickMajorSelfHeal(spells, health)),
                SpellCategory.Curing          => PickCure(spells),
                SpellCategory.Buffing         => (_autoBlessEnabled?.Invoke() ?? true)
                                                     ? PickBuff(spells, health, partySettings)
                                                     : null,
                SpellCategory.Debuffing       => PickDebuff(),
                _                              => null,
            };

            if (pick is not { } cand) continue;
            if (string.IsNullOrWhiteSpace(cand.Spell)) continue;

            // Item-cast buff (#-token in a Bless slot): bypass the raw cast path
            // entirely — run the equip → use → re-equip sequence and key the
            // recast timer by the token. Only buff slots carry tokens.
            if (category == SpellCategory.Buffing && ItemCastToken.IsToken(cand.Spell))
            {
                if (TryFireItemCast(cand.Spell)) return cand.Spell;
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

            if (!_cast.TryCast(cand.Spell, cand.Target)) return null;

            // Combat-sourced debuff landed — advance the combat engine's
            // once-per-room / once-per-target bookkeeping so it won't re-fire.
            if (category == SpellCategory.Debuffing) _combatDebuffCommit?.Invoke();

            // Party-buff cast sent — arm CasterMessage confirmation so the
            // duration timer only starts once we observe it land. Self buffs
            // (null target) confirm via the ConditionTracker AppliedMessage.
            if (category == SpellCategory.Buffing && cand.Target is { } tgt)
                ArmPartyBuffConfirm(cand.Spell, tgt);

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

    private static CastCandidate? Wrap(string? spell) =>
        string.IsNullOrWhiteSpace(spell) ? null : new CastCandidate(spell, Target: null);

    // Fire an item-cast buff: resolve its recast duration, run the equip → use →
    // re-equip sequence, then start the round cooldown + the token-keyed recast
    // timer. Returns false (so a later category can try) when the token doesn't
    // resolve to a real buff or the sequence didn't send. The timer is set
    // proactively from the cast spell's computed duration rather than awaiting an
    // AppliedMessage, since the landing buff confirms under the spell's own cast
    // code, not the token.
    private bool TryFireItemCast(string token)
    {
        if (_itemCastDuration is null || _executeItemCast is null) return false;
        if (_itemCastDuration(token) is not { } durationSec || durationSec <= 0) return false;
        if (!_executeItemCast(token)) return false;

        _cast.NotifyExternalCastSent();
        _activeUntil[("", token)] = _now().AddSeconds(durationSec);
        _log?.Combat(LogCategory, $"Buffing item-cast fired token={token} duration={durationSec}s");
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
        foreach ((SpellCategory cat, int _) in order) yield return cat;
    }

    // ----- Self heal --------------------------------------------------

    private string? PickMajorSelfHeal(SpellsSettings spells, HealthSettings health)
    {
        if (_state.MaxHp <= 0) return null;
        if (!ManaClearsHealFloor(health)) return null;
        int hpPct = (int)Math.Round(_state.Hp * 100.0 / _state.MaxHp);
        if (hpPct > health.MajorHealCombatTrigger) return null;
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

        int hpPct = (int)Math.Round(_state.Hp * 100.0 / _state.MaxHp);
        // Use the in-combat trigger while engaged, the rest-time
        // trigger otherwise (matches the user's two-threshold mental
        // model from the Health tab).
        int trigger = _state.InCombat
            ? health.MinorHealCombatTrigger
            : health.HealRestTrigger;
        if (hpPct > trigger) return null;

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
        if (hpPct > health.MajorHealCombatTrigger
            && !string.IsNullOrWhiteSpace(spells.HpRegenSpell)
            && IsRecastDue("", spells.HpRegenSpell))
            return spells.HpRegenSpell;

        return string.IsNullOrWhiteSpace(spells.MinorHealSpell) ? null : spells.MinorHealSpell;
    }

    // Mana-floor gate for self heals: only cast a heal when the caster pool sits at
    // or above HealIfAboveMaCombat (in combat) or HealIfAboveMaResting (resting /
    // idle), so a low pool regenerates instead of being drained on heal spells. A
    // floor of 0 disables the gate. The value is read per MaThresholdMode
    // (percentage of MaxMa, or absolute MA); an unknown pool (MaxMa 0, percentage
    // mode) never blocks a heal so the safety path isn't suppressed by missing
    // prompt data.
    private bool ManaClearsHealFloor(HealthSettings health)
    {
        int floor = _state.InCombat
            ? health.HealIfAboveMaCombat
            : health.HealIfAboveMaResting;
        if (floor <= 0) return true;
        if (health.MaThresholdMode == ThresholdMode.Absolute)
            return _state.Ma >= floor;
        if (_state.MaxMa <= 0) return true;
        return (int)Math.Round(_state.Ma * 100.0 / _state.MaxMa) >= floor;
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

    // ----- Party heal -------------------------------------------------

    // Walk live party members; cast the minor party heal on whoever is below
    // MinorHealMemberThresholdPercent. When AoeMinMembers or more members are below
    // the threshold AND a group spell is configured, fire the AOE variant instead
    // (no target).
    private CastCandidate? PickMinorPartyHeal(PartySettings? settings) =>
        PickPartyHeal(settings,
            threshold: settings?.MinorHealMemberThresholdPercent ?? 70,
            singleSpell: settings?.MinorPartyHealSpell,
            aoeSpell:    settings?.MinorPartyHealAoeSpell);

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

        // Mana-floor gate applies to *mana-drawing* buffs only ("don't burn buff
        // mana when we'll need it for heals soon"). It is NOT a whole-category
        // early-out: a free item-cast buff (a charge wand / proc item whose
        // use-spell costs 0 mana) recasts regardless, so the floor + per-cast
        // affordability are applied per-slot below via IsBuffAffordable. MA
        // unknown (MaxMa 0) blocks mana-drawing buffs but not free item-casts.
        bool manaBuffsAllowed = _state.MaxMa > 0
            && (int)Math.Round(_state.Ma * 100.0 / _state.MaxMa) >= health.BlessIfAboveMa;

        // Self buffs first (rows 1–10 + regen + when-full), then party buffs.
        if (PickSelfBuff(spells, manaBuffsAllowed) is { } self) return self;
        return PickPartyBuff(party, manaBuffsAllowed);
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

    // Walk the self-buff slots (the sparse Bless slots in slot-index order, then
    // MaRegen + WhenHpFull + WhenMaFull) and return the first configured slot due to
    // recast on us. Hard-gated to out-of-combat — self buffs are expensive and
    // shouldn't burn a combat round.
    //
    // HpRegenSpell deliberately does NOT live here: an HP-regen HoT is treated as
    // assisted healing, cast reactively by the minor-self-heal path
    // (PickMinorSelfHeal) when HP trips the trigger — not maintained always-up like a
    // buff. A user who wants a constantly-refreshed regen buff puts that spell in a
    // Bless slot instead. MaRegenSpell stays a downtime buff — it tops the mana pool
    // (and feeds the reroll engine), it doesn't heal HP.
    private CastCandidate? PickSelfBuff(SpellsSettings spells, bool manaBuffsAllowed)
    {
        if (_state.InCombat) return null;

        // Bless slots first (in priority = slot-index order), then the mana-regen
        // / "when full" downtime buffs.
        IEnumerable<(string? Spell, bool Eligible)> slots =
            spells.BlessSlots.OrderBy(kv => kv.Key)
                .Select(kv => ((string?)kv.Value, true))
                .Concat(new (string? Spell, bool Eligible)[]
                {
                    (spells.MaRegenSpell,     true),
                    // WhenHp/MaFull additionally require the matching pool to be
                    // at max — they're "downtime, ready for next fight" buffs.
                    (spells.WhenHpFullSpell,  _state.MaxHp > 0 && _state.Hp >= _state.MaxHp),
                    (spells.WhenMaFullSpell,  _state.MaxMa > 0 && _state.Ma >= _state.MaxMa),
                });

        foreach ((string? slot, bool eligible) in slots)
        {
            if (!eligible) continue;
            if (string.IsNullOrWhiteSpace(slot)) continue;
            if (!IsBuffAffordable(slot, manaBuffsAllowed)) continue;
            if (!IsRecastDue("", slot)) continue;
            return new CastCandidate(slot, Target: null);
        }
        return null;
    }

    // Walk the party-bless slots in priority order and pick one buff to cast. A
    // party-wide buff (its Spells.Targets is Full / Divided Party Area — e.g. a
    // mage's shimmering mirage or a chant) blankets the whole party in a single cast,
    // so it's sent once with no target and no class filter, keyed for recast like a
    // self-buff (it lands on us too). A single-target buff (e.g. a priest's bless) is
    // cast on the first class-matched member due to recast — a member matches when
    // their PartyMember.Class is in the slot's checked class set. Gated by the
    // Other-tab "bless party while resting" / "bless party during combat" toggles.
    private CastCandidate? PickPartyBuff(PartySettings? party, bool manaBuffsAllowed)
    {
        if (_party is null) return null;
        if (party is null) return null;
        if (_party.Members.Count == 0) return null;

        bool whileResting  = party.BlessWhileResting;
        bool duringCombat  = party.BlessDuringCombat;
        if (_state.InCombat && !duringCombat) return null;
        if (_state.Position == PlayerPosition.Resting && !whileResting) return null;

        foreach (PartyBlessSlot slot in party.BlessSlots)
        {
            if (string.IsNullOrWhiteSpace(slot.Spell)) continue;
            if (!IsBuffAffordable(slot.Spell, manaBuffsAllowed)) continue;

            // Party-wide buff — one cast covers everyone, so class checkboxes
            // (which member to target) don't apply. Recast keyed to self ("")
            // since it lands on us and confirms via the AppliedMessage path.
            if (_isPartyWideBuff?.Invoke(slot.Spell) == true)
            {
                if (!IsRecastDue("", slot.Spell)) continue;
                return new CastCandidate(slot.Spell, Target: null);
            }

            // Single-target buff — needs at least one class-matched member.
            if (slot.ClassNumbers.Count == 0) continue;
            foreach (PartyMember m in _party.Members)
            {
                if (m.IsSelf) continue;
                if (!MemberMatchesClasses(m, slot.ClassNumbers)) continue;
                // Given name only: MajorMUD targets a cast by first name token, and
                // the recast key must match the target we stash for confirmation so
                // the buff doesn't re-fire every round on a "Given Family" mismatch.
                string given = GivenName(m.Name);
                string key = given.ToLowerInvariant();
                if (!IsRecastDue(key, slot.Spell)) continue;
                return new CastCandidate(slot.Spell, given);
            }
        }
        return null;
    }

    // True when the member's class name resolves to any of the slot's checked class
    // numbers (case-insensitive). Requires the class-number → name resolver to be
    // wired; no resolver => no match.
    private bool MemberMatchesClasses(PartyMember m, IReadOnlyList<int> classNumbers)
    {
        if (_classNameByNumber is null) return false;
        if (string.IsNullOrWhiteSpace(m.Class)) return false;
        foreach (int n in classNumbers)
        {
            string? name = _classNameByNumber(n);
            if (!string.IsNullOrWhiteSpace(name)
                && string.Equals(name.Trim(), m.Class.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Arm the pending party-buff confirmation: resolve the buff's CasterMessage
    // template + duration and stash it so the next matching server line starts the
    // duration timer keyed to the target. Clears any prior pending cast
    // (CastCoordinator's cooldown guarantees <=1 in flight). No-op (clears pending)
    // when the buff has no resolvable caster template.
    private void ArmPartyBuffConfirm(string shortCode, string target)
    {
        _pendingPartyCast = null;
        if (_buffInfoByShort?.Invoke(shortCode) is not { } info) return;
        if (CasterMessageMatcher.TryCreate(info.Caster) is not { } matcher) return;
        _pendingPartyCast = (shortCode, target, info.DurationSec, matcher);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.PropertyChanged -= OnStateChanged;
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
public readonly record struct CastCandidate(string Spell, string? Target);

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
}
