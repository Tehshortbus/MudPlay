using System.ComponentModel;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Spells;

/// <summary>
/// Phase 9 PR 9.D — unified spell-decision engine. Subscribes to
/// <see cref="PlayerState"/> + <see cref="TickEngine.CombatTickElapsed"/>,
/// reads <see cref="Conditions.ConditionTracker.ActiveFlags"/>, and
/// routes the chosen cast through <see cref="CastCoordinator"/>.
/// </summary>
/// <remarks>
/// <para>
/// One unified priority list lifted from
/// <see cref="SpellsSettings"/>'s <c>PriorityXxxx</c> slots. Lower
/// number = higher precedence. Default order is the MegaMUD-parity
/// shape (Minor party heal → Major party heal → Minor self heal →
/// Major self heal → Curing → Buffing → Debuffing); the user is
/// free to re-order any of the seven via the Spells settings tab.
/// </para>
/// <para>
/// Per-category meaning:
/// </para>
/// <list type="bullet">
/// <item><b>Minor / Major party heal</b> — single-target party heal
/// when a member is below threshold, group AOE party heal when
/// multiple are. v1 unwired (party-cast lands with PartySettings
/// extensions in a follow-up commit).</item>
/// <item><b>Minor / Major self heal</b> — <see cref="SpellsSettings.MinorHealSpell"/>
/// / <see cref="SpellsSettings.MajorHealSpell"/> against the local
/// player. Thresholds: <see cref="HealthSettings.MinorHealCombatTrigger"/>
/// / <see cref="HealthSettings.MajorHealCombatTrigger"/> while
/// <see cref="PlayerState.InCombat"/>; <see cref="HealthSettings.HealRestTrigger"/>
/// during rest.</item>
/// <item><b>Curing</b> — remove an active ailment. The actual
/// ailment state comes from <see cref="Conditions.ConditionTracker"/>
/// (game-data Messages tab owns the patterns). Per-ailment cure
/// spells are <see cref="SpellsSettings.CureHoldsSpell"/> etc.
/// Internal order inside the Curing slot: movement-prevented →
/// poison → disease → blindness.</item>
/// <item><b>Buffing</b> — recast player buffs (Bless1–10 slots).
/// v1 unwired.</item>
/// <item><b>Debuffing</b> — an in-between action sourced from the
/// combat engine. The DECISION (config + once-per-room /
/// once-per-target gating) is owned by
/// <see cref="Combat.CombatManager"/> /
/// <see cref="Combat.CombatSpellChooser"/>; this director only casts
/// the debuff through the shared in-between window (wired via
/// <see cref="SetCombatDebuffSource"/>) so it competes against the
/// survival casts above by the user's
/// <see cref="SpellsSettings.PriorityDebuffing"/> rank. No-op until
/// wired.</item>
/// </list>
/// <para>
/// Every evaluation walks the priority list and picks the first
/// candidate that's actually ready to fire. The
/// <see cref="CastCoordinator"/>'s recent-cast cooldown handles
/// "one cast per round" naturally — if we evaluate mid-round the
/// cooldown blocks; on the next tick it clears and the highest-
/// priority candidate gets through.
/// </para>
/// <para>
/// Master enable flag is
/// <see cref="AutoActionDefaults.AutoHealRest"/> — shared with
/// <see cref="Health.HealthManager"/> so the user has one toggle
/// covering both passive rest + active heal-spell. When the spell
/// pickers on the Spells tab are empty, the engine no-ops without
/// further checks.
/// </para>
/// </remarks>
public sealed class CastingDirector : IDisposable
{
    /// <summary>LogService category — appears as <c>[CastDirector]</c>
    /// rows per evaluation + decision.</summary>
    public const string LogCategory = "CastDirector";

    private readonly PlayerState _state;
    private readonly CastCoordinator _cast;
    private readonly Conditions.ConditionTracker? _conditions;
    private readonly PartyState? _party;
    private Func<bool>? _isStealthedFunc;
    private Func<(string Spell, string? Target)?>? _combatDebuffSource;
    private Action? _combatDebuffCommit;
    private readonly Func<SpellsSettings> _readSpells;
    private readonly Func<HealthSettings> _readHealth;
    private readonly Func<PartySettings>? _readPartySettings;
    private readonly Func<bool> _isEnabled;
    private readonly LogService? _log;

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

    /// <summary>
    /// Constructor with optional <see cref="Conditions.ConditionTracker"/>
    /// (for ailment cures) and <see cref="PartyState"/> +
    /// <see cref="PartySettings"/> reader (for party-cast). Pass
    /// <c>null</c> for tests / engines that don't need the dependencies;
    /// the matching Pick* methods short-circuit.
    /// </summary>
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
            _conditions.ConditionApplied += OnConditionApplied;
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

    /// <summary>Hook to <see cref="TickEngine.CombatTickElapsed"/> —
    /// drives between-round evaluations.</summary>
    public void OnCombatTick() => Evaluate();

    /// <summary>
    /// Wire a stealth-state predicate so the Buff slot can skip
    /// candidate casts that would break stealth. Typically pointed
    /// at <c>StealthManager.IsStealthed</c>. Optional — when unset
    /// the buff slot fires regardless of stealth (back-compat for
    /// tests / pre-Cluster-3 callers).
    /// </summary>
    public void SetStealthGate(Func<bool> isStealthed) =>
        _isStealthedFunc = isStealthed;

    /// <summary>
    /// Wire the combat engine's in-between debuff bridge. A debuff is an
    /// in-between action (≤1/round) in the realm's round model, but the
    /// DECISION — config + once-per-room / once-per-target gating — lives in
    /// <see cref="Combat.CombatManager"/>. This director just rides the shared
    /// in-between window so the debuff competes against survival casts by the
    /// user's <see cref="SpellsSettings.PriorityDebuffing"/> rank (default
    /// lowest, so heals win). <paramref name="source"/> answers "is there a
    /// debuff to fire?" (spell code + target; null target ⇒ area/multi);
    /// <paramref name="commit"/> is invoked only after the coordinator confirms
    /// the cast, advancing the combat engine's per-room bookkeeping. Optional —
    /// until wired the Debuffing slot is a no-op.
    /// </summary>
    public void SetCombatDebuffSource(Func<(string Spell, string? Target)?> source, Action commit)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(commit);
        _combatDebuffSource = source;
        _combatDebuffCommit = commit;
    }

    private void OnConditionApplied(Models.GameData.MessageRecord _) => Evaluate();

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

    /// <summary>
    /// Run one decision pass: walk the priority list and fire the
    /// first ready candidate. Returns the spell that was cast (for
    /// diagnostics / tests), or <c>null</c> if nothing matched.
    /// </summary>
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
                SpellCategory.Curing          => Wrap(PickCure(spells)),
                SpellCategory.Buffing         => Wrap(PickBuff(spells, health)),
                SpellCategory.Debuffing       => PickDebuff(),
                _                              => null,
            };

            if (pick is not { } cand) continue;
            if (string.IsNullOrWhiteSpace(cand.Spell)) continue;
            if (!_cast.TryCast(cand.Spell, cand.Target)) return null;

            // Combat-sourced debuff landed — advance the combat engine's
            // once-per-room / once-per-target bookkeeping so it won't re-fire.
            if (category == SpellCategory.Debuffing) _combatDebuffCommit?.Invoke();

            _log?.Info(LogCategory,
                $"{category} fired spell={cand.Spell} target={cand.Target ?? "<self>"} " +
                $"hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa}");
            return cand.Spell;
        }

        return null;
    }

    private static CastCandidate? Wrap(string? spell) =>
        string.IsNullOrWhiteSpace(spell) ? null : new CastCandidate(spell, Target: null);

    /// <summary>Categories in priority order (lowest int first, ties
    /// broken by category enum order for determinism).</summary>
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
        if (string.IsNullOrWhiteSpace(spells.MinorHealSpell)) return null;

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

        return spells.MinorHealSpell;
    }

    // ----- Curing -----------------------------------------------------

    /// <summary>
    /// Walk the cure-priority list and return the first configured
    /// spell whose matching ailment is currently active.
    /// MovementPrevented covers paralyze / hold / sleep — they all
    /// render the same to a player (can't act); the user's
    /// CureHoldsSpell is the catch-all.
    /// </summary>
    private string? PickCure(SpellsSettings spells)
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

    // ----- Party heal -------------------------------------------------

    /// <summary>
    /// Walk live party members; cast the minor party heal on whoever
    /// is below <see cref="PartySettings.MinorHealMemberThresholdPercent"/>.
    /// When <see cref="PartySettings.AoeMinMembers"/> or more members are
    /// below the threshold AND a group spell is configured, fire the
    /// AOE variant instead (no target).
    /// </summary>
    private CastCandidate? PickMinorPartyHeal(PartySettings? settings) =>
        PickPartyHeal(settings,
            threshold: settings?.MinorHealMemberThresholdPercent ?? 70,
            singleSpell: settings?.MinorPartyHealSpell,
            aoeSpell:    settings?.MinorPartyHealAoeSpell);

    /// <summary>Symmetric to <see cref="PickMinorPartyHeal"/> at the
    /// major / critical threshold.</summary>
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
            return new CastCandidate(singleSpell, Target: lowest.Name);

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

    /// <summary>
    /// Walk the user's Bless1–10 slots in order; return the first one
    /// that's configured AND not currently active on us.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Active" is a Messages-tab signal in v1: each buff's
    /// MessageRecord has AppliedMessage (the cast / refresh line)
    /// and AppliedEndsWith (the wear-off line);
    /// <see cref="Conditions.ConditionTracker.IsActiveByName"/>
    /// answers from the live set.
    /// </para>
    /// <para>
    /// <b>Intended canonical path (deferred until Spells gamedata
    /// lands)</b>: any player-castable buff has a duration in the
    /// game-data Spells table. The engine should record cast time
    /// at TryCast and compute <c>active until = cast_time +
    /// spell.Duration</c>. The Messages path stays as a fallback
    /// for server-confirmed early wear-off (dispels, area-clears),
    /// but duration is the authoritative answer. Until the Spells
    /// model + spell-name → record lookup ship, we rely on the
    /// Messages path alone.
    /// </para>
    /// <para>
    /// MA-floor gate: only consider buffs when MA is at or above
    /// <see cref="HealthSettings.BlessIfAboveMa"/>. Mirrors MegaMUD's
    /// "don't burn buff mana when we'll need it for heals soon"
    /// behaviour.
    /// </para>
    /// </remarks>
    private string? PickBuff(SpellsSettings spells, HealthSettings health)
    {
        if (_state.MaxMa <= 0) return null;
        int maPct = (int)Math.Round(_state.Ma * 100.0 / _state.MaxMa);
        if (maPct < health.BlessIfAboveMa) return null;

        // Buffs are expensive; never burn a round on them mid-combat
        // unless the user explicitly opts in. v1 hard-gates on
        // out-of-combat — refine later if buff-during-combat
        // toggles get added.
        if (_state.InCombat) return null;

        // Stealth gate: any cast breaks sneak / hide; suppress buffs
        // entirely while stealthed so a backstab window stays open.
        if (_isStealthedFunc?.Invoke() == true) return null;

        // All 14 buff slots walk together: 10 explicit Bless picks +
        // HpRegen + MaRegen + WhenHpFull + WhenMaFull. Each has its
        // own eligibility predicate. First eligible AND not-active
        // slot fires.
        (string? Spell, bool Eligible)[] slots =
        {
            (spells.Bless1Spell,      true),
            (spells.Bless2Spell,      true),
            (spells.Bless3Spell,      true),
            (spells.Bless4Spell,      true),
            (spells.Bless5Spell,      true),
            (spells.Bless6Spell,      true),
            (spells.Bless7Spell,      true),
            (spells.Bless8Spell,      true),
            (spells.Bless9Spell,      true),
            (spells.Bless10Spell,     true),
            (spells.HpRegenSpell,     true),
            (spells.MaRegenSpell,     true),
            // WhenHp/MaFull additionally require the matching pool
            // to be at max — they're "downtime, ready for next
            // fight" buffs.
            (spells.WhenHpFullSpell,  _state.MaxHp > 0 && _state.Hp >= _state.MaxHp),
            (spells.WhenMaFullSpell,  _state.MaxMa > 0 && _state.Ma >= _state.MaxMa),
        };

        foreach ((string? slot, bool eligible) in slots)
        {
            if (!eligible) continue;
            if (string.IsNullOrWhiteSpace(slot)) continue;
            if (_conditions?.IsActiveByName(slot) == true) continue;
            return slot;
        }
        return null;
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
            _conditions.ConditionApplied -= OnConditionApplied;
    }
}

/// <summary>One picked cast — spell name + optional target string.
/// Used internally by <see cref="CastingDirector"/> to thread through
/// the unified Pick* → TryCast pipeline.</summary>
public readonly record struct CastCandidate(string Spell, string? Target);

/// <summary>Spell-decision categories. Order matches the user-facing
/// Spells settings tab; numeric position is just for deterministic
/// tiebreak when two priority slots share the same int.</summary>
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
