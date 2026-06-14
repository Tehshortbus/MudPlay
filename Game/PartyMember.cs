using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;
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
    [ObservableProperty]
    [field: Owner(typeof(PartyManager))]
    [NotifyPropertyChangedFor(nameof(HpDisplay))]
    [NotifyPropertyChangedFor(nameof(HpRichDisplay))]
    private int _baselineHp;

    /// <summary>Baseline MA at join. Same provenance as <see cref="BaselineHp"/>.</summary>
    [ObservableProperty]
    [field: Owner(typeof(PartyManager))]
    [NotifyPropertyChangedFor(nameof(MaDisplay))]
    [NotifyPropertyChangedFor(nameof(MaRichDisplay))]
    private int _baselineMp;

    /// <summary>Current HP as a percentage (0–100) of <see cref="BaselineHp"/>. Updated by every <c>par</c> poll.</summary>
    [ObservableProperty]
    [field: Owner(typeof(PartyManager))]
    [NotifyPropertyChangedFor(nameof(HpDisplay))]
    [NotifyPropertyChangedFor(nameof(HpRichDisplay))]
    private int _hpPercent;

    /// <summary>Current MA as a percentage (0–100) of <see cref="BaselineMp"/>.</summary>
    [ObservableProperty]
    [field: Owner(typeof(PartyManager))]
    [NotifyPropertyChangedFor(nameof(MaDisplay))]
    [NotifyPropertyChangedFor(nameof(MaRichDisplay))]
    private int _mpPercent;

    /// <summary>
    /// PartyWindow display string for HP. When <see cref="BaselineHp"/>
    /// is known (the on-join <c>@health</c> exchange completed and we
    /// captured this member's max), shows <c>"current/max"</c> computed
    /// from <c>BaselineHp * HpPercent / 100</c>. Until the baseline
    /// arrives, falls back to <c>"percent%"</c>. Bound directly by the
    /// PartyWindow row template — no converter needed.
    /// </summary>
    /// <remarks>
    /// Computed both ways instead of inverting the percent because the
    /// percent is the LIVE field every <c>par</c> poll refreshes; the
    /// baseline is captured once at join time and stays constant. Net
    /// result: as <c>par</c> percentages tick down during combat we
    /// rerender the exact current value (rounded) without sending an
    /// @health round-trip per tick.
    /// </remarks>
    public string HpDisplay => BaselineHp > 0
        ? $"{BaselineHp * HpPercent / 100}/{BaselineHp}"
        : $"{HpPercent}%";

    /// <summary>
    /// PartyWindow display string for MA / KAI. Same rules as
    /// <see cref="HpDisplay"/>. Warriors / no-mana classes have
    /// <see cref="BaselineMp"/> = 0 and the PartyWindow hides the
    /// whole MA column via <c>GreaterThanZeroConverter</c>, so the
    /// fallback "0%" never actually renders for those rows.
    /// </summary>
    public string MaDisplay => BaselineMp > 0
        ? $"{BaselineMp * MpPercent / 100}/{BaselineMp}"
        : $"{MpPercent}%";

    /// <summary>
    /// PartyWindow rich display string for HP — skeleton-design form
    /// "H:cur/max pct%" (e.g. "H:779/817 95%"). Falls back to just
    /// "H:pct%" when baseline is unknown so the row doesn't render
    /// the awkward double-percent "H:95% 95%".
    /// </summary>
    public string HpRichDisplay => BaselineHp > 0
        ? $"H:{BaselineHp * HpPercent / 100}/{BaselineHp} {HpPercent}%"
        : $"H:{HpPercent}%";

    /// <summary>
    /// PartyWindow rich display string for MA / KAI — symmetric with
    /// <see cref="HpRichDisplay"/>.
    /// </summary>
    public string MaRichDisplay => BaselineMp > 0
        ? $"M:{BaselineMp * MpPercent / 100}/{BaselineMp} {MpPercent}%"
        : $"M:{MpPercent}%";

    /// <summary>Stance / position observed on the most recent <c>par</c> row.</summary>
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private PlayerPosition _position;

    /// <summary>
    /// True if this member is the party leader (marked with <c>*</c> in the
    /// <c>par</c> table). Drives the leader-star UI badge.
    /// </summary>
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _isLeader;

    /// <summary>
    /// Combat rank parsed from the last column of the <c>par</c> table
    /// (<c>Frontrank</c> / <c>Midrank</c> / <c>Backrank</c>). Drives
    /// the per-row rank chip in the PartyWindow — the skeleton design
    /// shows it on every row, colour-coded per rank, not just for the
    /// local self row. Defaults to <see cref="PartyRank.Mid"/> when
    /// the par column is absent or unrecognised.
    /// </summary>
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private PartyRank _rank = PartyRank.Mid;

    /// <summary>
    /// True when this member is the locally connected character (the row
    /// MajorMUD prints as <c>ME</c> in <c>par</c> output). The PartyWindow
    /// uses this to subtly differentiate the local row.
    /// </summary>
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _isSelf;

    // ----- Per-member status flags ----------------------------------------
    // Surfaced as the PartyWindow row's compact flag indicator. The combat
    // engine (Phase 13) will set the ailment flags as it recognises the
    // applied / cured / par-confirmation messages — for now Resting and
    // Meditating are the only ones live-wired (from the par state column).
    //
    // Set chosen for PartyWindow surfacing: rest / meditate posture, the
    // four curable ailments that meaningfully change party tactics (blind,
    // poison, disease, confuse), the movement-prevented HELD flag (a member
    // says ".@held" so a party cure-holds caster can react), and the
    // cross-cutting WAIT coordination flag below. Stealth (hidden/sneaking)
    // and paralysis are tracked elsewhere by their own subsystems — no
    // reason to mirror them here.

    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _resting;
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _meditating;
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _blinded;
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _poisoned;
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _diseased;
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _confused;
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _held;

    /// <summary>
    /// True when this member has asked the party to <c>@wait</c> and
    /// hasn't yet sent <c>@ok</c>. Owned by
    /// <see cref="Remote.PartyEssentialHandlers"/> — the receive side of
    /// the @wait/@ok protocol mirrors its <c>WaitingMembers</c> set onto
    /// the matching PartyMember row so the PartyWindow can render a
    /// "WAIT" chip per-member without bouncing through a separate
    /// collection.
    /// </summary>
    [ObservableProperty] [field: Owner(typeof(Remote.PartyEssentialHandlers))] private bool _isWaiting;

    /// <summary>
    /// True when we've sent <c>invite X</c> on the wire (or observed
    /// the server's "You have invited X to follow you." confirmation)
    /// but X hasn't yet accepted via <c>follow</c>. Drives the
    /// "Invited" chip + <c>×</c> uninvite button in the PartyWindow.
    /// Flipped <c>false</c> when <see cref="PartyManager.OnFollowsYou"/>
    /// fires for the same given name; the row is removed entirely on
    /// the "X has been removed from your followers." uninvite
    /// confirmation. HP / MA columns + on-join @health round-trip
    /// suppress themselves while this is true — invited rows carry
    /// no health data until they actually join.
    /// </summary>
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _isInvited;
}
