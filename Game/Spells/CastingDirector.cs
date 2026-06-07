using System.ComponentModel;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Spells;

/// <summary>
/// Phase 9 PR 9.D — unified self+party heal / cure / buff decision
/// engine. Replaces MudProxy's separate <c>HealingManager</c> +
/// <c>CureManager</c> + <c>BuffManager</c>. Subscribes to
/// <see cref="PlayerState"/> + <see cref="TickEngine.CombatTickElapsed"/>
/// and routes the picked cast through <see cref="CastCoordinator"/>.
/// </summary>
/// <remarks>
/// <para>
/// Decision model — three priority tiers:
/// </para>
/// <list type="number">
/// <item><b>Life-threat (hard priority)</b> — when current HP% falls
/// to or below <see cref="HealthSettings.MajorHealCombatTrigger"/>,
/// cast <see cref="SpellsSettings.MajorHealSpell"/> immediately (or
/// fall back to <see cref="SpellsSettings.MinorHealSpell"/> if no
/// major heal is configured). Fires on any <see cref="PlayerState.Hp"/>
/// change — does NOT wait for the combat tick. The
/// <see cref="CastCoordinator"/>'s recent-cast cooldown still gates
/// the actual wire emit so we don't double-cast within a round.</item>
/// <item><b>Ailment cures (ordered priority)</b> — deferred to a
/// follow-up PR; PlayerState doesn't yet track
/// (Poisoned)/(Blinded)/(Paralyzed)/(Diseased) status. The decision
/// shape is sketched in
/// <see cref="SpellsSettings.CureHoldsSpell"/> / etc.</item>
/// <item><b>Routine heals (weighted)</b> — when HP% is below the
/// per-context trigger:
/// <see cref="HealthSettings.MinorHealCombatTrigger"/> while
/// <see cref="PlayerState.InCombat"/> is true,
/// <see cref="HealthSettings.HealRestTrigger"/> while resting. Casts
/// <see cref="SpellsSettings.MinorHealSpell"/>. Fires on combat-tick
/// boundaries so we send between rounds, not mid-swing.</item>
/// </list>
/// <para>
/// Master enable flag is
/// <see cref="AutoActionDefaults.AutoHealRest"/> — shared with
/// <see cref="Health.HealthManager"/> so the user has one toggle
/// covering both passive rest + active heal-spell. When the spell
/// pickers on the Spells tab are empty, the engine no-ops without
/// further checks.
/// </para>
/// <para>
/// Party-cast, cures, and buff recasts ship as follow-up commits on
/// this PR — the foundation lands first so the user can smoke-test
/// the heal path end-to-end.
/// </para>
/// </remarks>
public sealed class CastingDirector : IDisposable
{
    /// <summary>LogService category — appears as <c>[CastDirector]</c>
    /// rows per evaluation + decision.</summary>
    public const string LogCategory = "CastDirector";

    private readonly PlayerState _state;
    private readonly CastCoordinator _cast;
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
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(cast);
        ArgumentNullException.ThrowIfNull(readSpells);
        ArgumentNullException.ThrowIfNull(readHealth);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _state = state;
        _cast = cast;
        _readSpells = readSpells;
        _readHealth = readHealth;
        _isEnabled = isEnabled;
        _log = log;

        _state.PropertyChanged += OnStateChanged;
    }

    /// <summary>
    /// Hook the combat-tick boundary. Routine heals fire here so we
    /// emit between rounds rather than mid-swing. Tier 1 life-threat
    /// fires on the immediate HP-changed event instead.
    /// </summary>
    public void OnCombatTick() => Evaluate(tickDriven: true);

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerState.Hp):
            case nameof(PlayerState.MaxHp):
            case nameof(PlayerState.InCombat):
            case nameof(PlayerState.Position):
            case nameof(PlayerState.HasPromptData):
                // Hp changes drive Tier 1 immediate. Other property
                // changes re-evaluate in case the context shifted (e.g.
                // InCombat just flipped true and a Tier-3 candidate
                // becomes eligible). HasPromptData triggers the first
                // evaluation after PromptParser's initial state load.
                Evaluate(tickDriven: false);
                break;
        }
    }

    /// <summary>
    /// Run one decision pass. Returns the spell that was cast (for
    /// diagnostics / tests), or <c>null</c> if no candidate matched
    /// the live state.
    /// </summary>
    public string? Evaluate(bool tickDriven)
    {
        if (!_isEnabled()) return null;
        if (!_state.HasPromptData) return null;
        if (_state.MaxHp <= 0) return null;
        if (_state.Hp <= 0) return null;     // dead — DeathRecoveryManager owns this case
        if (_cast.IsCastBlocked) return null;

        SpellsSettings spells = _readSpells();
        HealthSettings health = _readHealth();

        int hpPct = (int)Math.Round(_state.Hp * 100.0 / _state.MaxHp);

        // Tier 1 — life-threat self-heal. Fires on Hp PropertyChanged
        // so a damage spike triggers the cast before the next tick.
        if (hpPct <= health.MajorHealCombatTrigger)
        {
            string? lifeSaver = !string.IsNullOrWhiteSpace(spells.MajorHealSpell)
                ? spells.MajorHealSpell
                : spells.MinorHealSpell;
            if (!string.IsNullOrWhiteSpace(lifeSaver))
            {
                if (_cast.TryCast(lifeSaver))
                {
                    _log?.Info(LogCategory,
                        $"life-threat heal hp%={hpPct} spell={lifeSaver}");
                    return lifeSaver;
                }
                return null;
            }
        }

        // Tier 3 — routine self-heal. Tick-driven (between rounds) to
        // avoid pre-empting the combat swing. The user's intent for
        // "heal during combat below X%" is satisfied by Tier 1 above
        // when HP is critical; this branch covers the moderate range.
        if (!tickDriven) return null;

        string? routineSpell = spells.MinorHealSpell;
        if (string.IsNullOrWhiteSpace(routineSpell)) return null;

        bool inCombat = _state.InCombat;
        int threshold = inCombat
            ? health.MinorHealCombatTrigger
            : health.HealRestTrigger;

        if (hpPct > threshold) return null;
        // Out-of-combat heal-spell-during-rest is gated on the player
        // actually being in a recovery posture — otherwise we'd cast
        // mid-walk between rooms. HealthManager owns the (Resting)
        // emit; we just key off the resulting position.
        if (!inCombat && _state.Position != PlayerPosition.Resting) return null;

        if (_cast.TryCast(routineSpell))
        {
            _log?.Info(LogCategory,
                $"routine heal hp%={hpPct} spell={routineSpell} context={(inCombat ? "combat" : "rest")}");
            return routineSpell;
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.PropertyChanged -= OnStateChanged;
    }
}
