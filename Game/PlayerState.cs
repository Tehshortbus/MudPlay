using CommunityToolkit.Mvvm.ComponentModel;
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
}
