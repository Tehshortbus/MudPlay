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
/// <item><b>Debuffing</b> — combat pre-cast spells on enemies /
/// room (CombatSettings.PreAttack*, MultiAttackSpell). v1 unwired.</item>
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
    private readonly Func<SpellsSettings> _readSpells;
    private readonly Func<HealthSettings> _readHealth;
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
        : this(state, cast, conditions: null, readSpells, readHealth, isEnabled, log) { }

    /// <summary>
    /// Constructor that wires <see cref="Conditions.ConditionTracker"/>
    /// so the engine can fire ailment cures. The legacy ctor stays
    /// for tests that exercise heal-only behaviour without spinning up
    /// the Messages-tab dependency.
    /// </summary>
    public CastingDirector(
        PlayerState state,
        CastCoordinator cast,
        Conditions.ConditionTracker? conditions,
        Func<SpellsSettings> readSpells,
        Func<HealthSettings> readHealth,
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
        _readSpells = readSpells;
        _readHealth = readHealth;
        _isEnabled = isEnabled;
        _log = log;

        _state.PropertyChanged += OnStateChanged;
        if (_conditions is not null)
            _conditions.ConditionApplied += OnConditionApplied;
    }

    /// <summary>Hook to <see cref="TickEngine.CombatTickElapsed"/> —
    /// drives between-round evaluations.</summary>
    public void OnCombatTick() => Evaluate();

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

        foreach (SpellCategory category in PrioritisedCategories(spells))
        {
            string? pick = category switch
            {
                SpellCategory.MinorPartyHeal  => PickMinorPartyHeal(spells, health),
                SpellCategory.MajorPartyHeal  => PickMajorPartyHeal(spells, health),
                SpellCategory.MinorSelfHeal   => PickMinorSelfHeal(spells, health),
                SpellCategory.MajorSelfHeal   => PickMajorSelfHeal(spells, health),
                SpellCategory.Curing          => PickCure(spells),
                SpellCategory.Buffing         => PickBuff(spells, health),
                SpellCategory.Debuffing       => PickDebuff(spells, health),
                _                              => null,
            };

            if (string.IsNullOrWhiteSpace(pick)) continue;
            if (!_cast.TryCast(pick)) return null;

            _log?.Info(LogCategory,
                $"{category} fired spell={pick} hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa}");
            return pick;
        }

        return null;
    }

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

    // ----- Party heal — pending PartySettings extensions --------------

    private string? PickMinorPartyHeal(SpellsSettings _, HealthSettings __) => null;
    private string? PickMajorPartyHeal(SpellsSettings _, HealthSettings __) => null;

    // ----- Buffing — pending bless-active tracking --------------------

    private string? PickBuff(SpellsSettings _, HealthSettings __) => null;

    // ----- Debuffing — pending CombatSettings pre-attack chain --------

    private string? PickDebuff(SpellsSettings _, HealthSettings __) => null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.PropertyChanged -= OnStateChanged;
        if (_conditions is not null)
            _conditions.ConditionApplied -= OnConditionApplied;
    }
}

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
