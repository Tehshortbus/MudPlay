using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.Combat;
using FujinTerm.Services;

namespace FujinTerm.Game;

/// <summary>
/// Live player state for the connected character — HP / mana / position /
/// mana type. Updated by <see cref="PromptParser"/> on every status-line
/// match; consumed by the status bar, the Phase 9 Workshop STATS section,
/// and any automation engine that gates on HP / MP / position.
/// </summary>
/// <remarks>
/// <para>
/// Ownership model: every <see cref="ObservablePropertyAttribute"/>-backed
/// field declares its sole writer. <see cref="PromptParser"/> is the only
/// class allowed to mutate these fields (the Phase 3 PR 3.5 IL-scan test
/// enforces this at build time). Consumers — including the status bar
/// view-model — bind to these properties; they never call setters.
/// </para>
/// <para>
/// <see cref="MaxHp"/> / <see cref="MaxMa"/> are populated from the
/// largest observed running value, since the baseline MajorMUD statline
/// doesn't emit a denominator. Phase 12 Settings → Statline lets users
/// build a richer wildcard string that includes <c>%H</c>/<c>%M</c>; the
/// parser will start consuming those captures from there.
/// </para>
/// </remarks>
public sealed partial class PlayerState : ObservableObject
{
    [ObservableProperty] [field: Owner(typeof(PromptParser))] private int _hp;
    [ObservableProperty] [field: Owner(typeof(PromptParser))] private int _maxHp;
    [ObservableProperty] [field: Owner(typeof(PromptParser))] private int _ma;
    [ObservableProperty] [field: Owner(typeof(PromptParser))] private int _maxMa;
    [ObservableProperty] [field: Owner(typeof(PromptParser))] private ManaType _manaType;
    [ObservableProperty] [field: Owner(typeof(PromptParser))] private PlayerPosition _position;

    /// <summary>
    /// True once <see cref="PromptParser"/> has observed at least one
    /// status line. UI surfaces show <c>—</c> placeholders when this is
    /// <c>false</c> so the first-launch / pre-connect state doesn't
    /// render as <c>HP 0/0</c>.
    /// </summary>
    [ObservableProperty] [field: Owner(typeof(PromptParser))] private bool _hasPromptData;

    /// <summary>
    /// Latest observed encumbrance bracket from the <c>enc</c> line.
    /// Stays <see cref="EncumbranceLevel.Unknown"/> until the player
    /// runs <c>enc</c> (or it gets reported during an inventory).
    /// Drives the Auto-Lair scheduler's per-hop travel-cost lookup and
    /// the hop-timing calibrator's bucket tag.
    /// </summary>
    [ObservableProperty] [field: Owner(typeof(EncumbranceParser))] private EncumbranceLevel _encumbrance;

    /// <summary>
    /// True while the live combat state is "engaged" — set when the
    /// server reports <c>*Combat Engaged*</c> OR a damage line fires;
    /// cleared on <c>*Combat Off*</c>. Cross-cuts every Phase 9 engine
    /// that gates on "are we currently fighting": CombatManager
    /// (PR 9.A) only swings while true, HealthManager (PR 9.B) won't
    /// initiate <c>rest</c> while true, CashManager (PR 9.E) defers
    /// auto-pickup while true. Phase 9 PR 9.0c
    /// <see cref="Game.Combat.RoundDamageTracker"/> drives the
    /// damage-line-driven decay.
    /// </summary>
    [ObservableProperty] [field: Owner(typeof(CombatStateTracker))] private bool _inCombat;

    /// <summary>
    /// True while we're confirmed sneaking — the server emitted
    /// <c>Sneaking...</c> on the last room entry. Cleared on
    /// <c>"You make a sound as you enter the room!"</c> OR when a
    /// room-entry doesn't carry the sneak confirmation (silent loss).
    /// Drives CombatManager's pre-attack-buff suppression so we don't
    /// burn the backstab window casting buffs.
    /// </summary>
    [ObservableProperty] [field: Owner(typeof(Game.Stealth.StealthManager))] private bool _isSneaking;

    /// <summary>
    /// True while we're confirmed hidden — server confirmed the
    /// <c>hide</c> command. Cleared on any action that breaks hide
    /// (move, attack, cast). Same role as
    /// <see cref="IsSneaking"/>: read by CombatManager's
    /// backstab-window logic.
    /// </summary>
    [ObservableProperty] [field: Owner(typeof(Game.Stealth.StealthManager))] private bool _isHidden;
}
