using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game;

// One row in PartyState.Members. Carries the per-member snapshot the
// PartyWindow renders (HP / MA + status flags) plus the baseline HP / MA
// captured at party-join time so the UI can show both the absolute number and
// the current percentage (H:690 94%).
//
// Ownership: PartyManager is the sole writer of every observable field below —
// the IL-scan test enforces it. Consumers (PartyWindow VM, automation engines)
// bind to these properties and never call the setters.
//
// `par` + follows-you / stops-following lines populate Name, Class, HpPercent,
// MpPercent, Position, and IsLeader. The on-join `@health` exchange fills in
// BaselineHp / BaselineMp. Observation lines surface the status flags (Resting,
// Meditating, etc.).
public sealed partial class PartyMember : ObservableObject
{
    // Player name as it appears in the `par` table and chat lines.
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private string _name = string.Empty;

    // Class string (e.g. "Mage", "Cleric"). Empty until first `par`
    // observation — follows-you doesn't disclose class.
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private string _class = string.Empty;

    // Baseline HP captured at party-join via the on-join `@health` exchange. 0
    // until that exchange completes — UI shows "—%" until both BaselineHp and
    // HpPercent are known.
    [ObservableProperty]
    [field: Owner(typeof(PartyManager))]
    [NotifyPropertyChangedFor(nameof(HpDisplay))]
    [NotifyPropertyChangedFor(nameof(HpRichDisplay))]
    private int _baselineHp;

    // Baseline MA at join. Same provenance as BaselineHp.
    [ObservableProperty]
    [field: Owner(typeof(PartyManager))]
    [NotifyPropertyChangedFor(nameof(MaDisplay))]
    [NotifyPropertyChangedFor(nameof(MaRichDisplay))]
    private int _baselineMp;

    // Current HP as a percentage (0–100) of BaselineHp. Updated by every `par` poll.
    [ObservableProperty]
    [field: Owner(typeof(PartyManager))]
    [NotifyPropertyChangedFor(nameof(HpDisplay))]
    [NotifyPropertyChangedFor(nameof(HpRichDisplay))]
    private int _hpPercent;

    // Current MA as a percentage (0–100) of BaselineMp.
    [ObservableProperty]
    [field: Owner(typeof(PartyManager))]
    [NotifyPropertyChangedFor(nameof(MaDisplay))]
    [NotifyPropertyChangedFor(nameof(MaRichDisplay))]
    private int _mpPercent;

    // True when this member's secondary pool is kai (Mystic / monk family)
    // rather than mana — kai operates differently and the party row must label
    // it K:, not M:. Set alongside BaselineMp (the field that gates whether the
    // secondary bar renders at all): the self row reads it from
    // PlayerState.ManaType, other members from their @health reply's KAI=/MA=
    // keyword.
    [ObservableProperty]
    [field: Owner(typeof(PartyManager))]
    [NotifyPropertyChangedFor(nameof(MaRichDisplay))]
    private bool _isKai;

    // PartyWindow display string for HP. When BaselineHp is known (the on-join
    // `@health` exchange completed and we captured this member's max), shows
    // "current/max" computed from BaselineHp * HpPercent / 100. Until the
    // baseline arrives, falls back to "percent%". Bound directly by the
    // PartyWindow row template — no converter needed.
    //
    // Computed both ways instead of inverting the percent because the percent is
    // the LIVE field every `par` poll refreshes; the baseline is captured once
    // at join time and stays constant. Net result: as `par` percentages tick
    // down during combat we rerender the exact current value (rounded) without
    // sending an @health round-trip per tick.
    public string HpDisplay => BaselineHp > 0
        ? $"{BaselineHp * HpPercent / 100}/{BaselineHp}"
        : $"{HpPercent}%";

    // PartyWindow display string for MA / KAI. Same rules as HpDisplay.
    // Warriors / no-mana classes have BaselineMp = 0 and the PartyWindow hides
    // the whole MA column via GreaterThanZeroConverter, so the fallback "0%"
    // never actually renders for those rows.
    public string MaDisplay => BaselineMp > 0
        ? $"{BaselineMp * MpPercent / 100}/{BaselineMp}"
        : $"{MpPercent}%";

    // PartyWindow rich display string for HP — form "H:cur/max pct%" (e.g.
    // "H:779/817 95%"). Falls back to just "H:pct%" when baseline is unknown so
    // the row doesn't render the awkward double-percent "H:95% 95%".
    public string HpRichDisplay => BaselineHp > 0
        ? $"H:{BaselineHp * HpPercent / 100}/{BaselineHp} {HpPercent}%"
        : $"H:{HpPercent}%";

    // PartyWindow rich display string for MA / KAI — symmetric with HpRichDisplay.
    // Prefix is K: for kai (Mystic / monk), M: for mana.
    public string MaRichDisplay => BaselineMp > 0
        ? $"{(IsKai ? "K" : "M")}:{BaselineMp * MpPercent / 100}/{BaselineMp} {MpPercent}%"
        : $"{(IsKai ? "K" : "M")}:{MpPercent}%";

    // Stance / position observed on the most recent `par` row.
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private PlayerPosition _position;

    // True if this member is the party leader (marked with * in the `par`
    // table). Drives the leader-star UI badge.
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _isLeader;

    // Combat rank parsed from the last column of the `par` table (Frontrank /
    // Midrank / Backrank). Drives the per-row rank chip in the PartyWindow —
    // shown on every row, colour-coded per rank, not just for the local self
    // row. Defaults to PartyRank.Mid when the par column is absent or
    // unrecognised.
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private PartyRank _rank = PartyRank.Mid;

    // True when this member is the locally connected character (the row
    // MajorMUD prints as ME in `par` output). The PartyWindow uses this to
    // subtly differentiate the local row.
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _isSelf;

    // ----- Per-member status flags ----------------------------------------
    // Surfaced as the PartyWindow row's compact flag indicator. The combat
    // engine sets the ailment flags as it recognises the applied / cured /
    // par-confirmation messages — for now Resting and Meditating are the only
    // ones live-wired (from the par state column).
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

    // True when this member has asked the party to @wait and hasn't yet sent
    // @ok. Owned by PartyEssentialHandlers — the receive side of the @wait/@ok
    // protocol mirrors its WaitingMembers set onto the matching PartyMember row
    // so the PartyWindow can render a "WAIT" chip per-member without bouncing
    // through a separate collection.
    [ObservableProperty] [field: Owner(typeof(Remote.PartyEssentialHandlers))] private bool _isWaiting;

    // True when we've sent `invite X` on the wire (or observed the server's
    // "You have invited X to follow you." confirmation) but X hasn't yet
    // accepted via `follow`. Drives the "Invited" chip + × uninvite button in
    // the PartyWindow. Flipped false when PartyManager.OnFollowsYou fires for the
    // same given name; the row is removed entirely on the "X has been removed
    // from your followers." uninvite confirmation. HP / MA columns + on-join
    // @health round-trip suppress themselves while this is true — invited rows
    // carry no health data until they actually join.
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _isInvited;
}
