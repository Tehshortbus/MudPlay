using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Services;

namespace FujinTerm.Game;

/// <summary>
/// One row in <see cref="PartyState.Members"/>. Carries the per-member
/// snapshot the PartyWindow renders (HP / MA + status flags) plus the
/// baseline HP / MA captured at party-join time so the UI can show
/// both the absolute number and the current percentage
/// (<c>H:690 94%</c>).
/// </summary>
/// <remarks>
/// <para>
/// Ownership: <see cref="PartyManager"/> is the sole writer of every
/// observable field below — the Phase 3 PR 3.5 IL-scan test enforces it.
/// Consumers (PartyWindow VM, future automation engines) bind to these
/// properties and never call the setters.
/// </para>
/// <para>
/// PR 6.1 only populates <see cref="Name"/>, <see cref="Class"/>,
/// <see cref="HpPercent"/>, <see cref="MpPercent"/>, <see cref="Position"/>,
/// and <see cref="IsLeader"/> from <c>par</c> + follows-you / stops-following
/// lines. PR 6.4 fills in <see cref="BaselineHp"/> / <see cref="BaselineMp"/>
/// via the on-join <c>@health</c> exchange. PR 6.5 surfaces the status
/// flags (<see cref="Resting"/>, <see cref="Meditating"/>, etc.) from
/// observation lines.
/// </para>
/// </remarks>
public sealed partial class PartyMember : ObservableObject
{
    /// <summary>Player name as it appears in the <c>par</c> table and chat lines.</summary>
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private string _name = string.Empty;

    /// <summary>
    /// Class string (e.g. "Mage", "Cleric"). Empty until first <c>par</c>
    /// observation — follows-you doesn't disclose class.
    /// </summary>
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private string _class = string.Empty;

    /// <summary>
    /// Baseline HP captured at party-join via the on-join <c>@health</c>
    /// exchange (PR 6.4). 0 until that exchange completes — UI shows
    /// "—%" until both BaselineHp and HpPercent are known.
    /// </summary>
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private int _baselineHp;

    /// <summary>Baseline MA at join. Same provenance as <see cref="BaselineHp"/>.</summary>
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private int _baselineMp;

    /// <summary>Current HP as a percentage (0–100) of <see cref="BaselineHp"/>. Updated by every <c>par</c> poll.</summary>
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private int _hpPercent;

    /// <summary>Current MA as a percentage (0–100) of <see cref="BaselineMp"/>.</summary>
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private int _mpPercent;

    /// <summary>Stance / position observed on the most recent <c>par</c> row.</summary>
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private PlayerPosition _position;

    /// <summary>
    /// True if this member is the party leader (marked with <c>*</c> in the
    /// <c>par</c> table). Drives the leader-star UI badge.
    /// </summary>
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _isLeader;

    /// <summary>
    /// True when this member is the locally connected character (the row
    /// MajorMUD prints as <c>ME</c> in <c>par</c> output). The PartyWindow
    /// uses this to subtly differentiate the local row.
    /// </summary>
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _isSelf;

    // ----- Per-member status flags (PR 6.5+) -----------------------------
    // Surfaced as the PartyWindow row's compact flag indicator. Each
    // tracks an ailment / state observed via dedicated message patterns
    // (cure messages, ailment-applied lines, follow-up par parses).

    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _resting;
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _meditating;
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _blinded;
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _poisoned;
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _diseased;
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _confused;
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _paralyzed;
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _hidden;
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _sneaking;
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _held;
}
