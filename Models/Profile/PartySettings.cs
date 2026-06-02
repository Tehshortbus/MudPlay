namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character "Party" settings — drives the Phase 6 party services
/// (<see cref="Game.PartyPoller"/>, <see cref="Game.PartyManager"/>,
/// <see cref="Game.Remote.PartyBroadcaster"/>). Stored as the
/// <c>"Party"</c> entry in <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// <para>
/// PR 6.9 ships the three knobs that map onto live Phase 6 services
/// (par cadence + auto-invite + auto-exp-reset). Spell / heal / bless
/// picker values from the Party tab UI aren't persisted here yet —
/// their consumer (<c>CastingDirector</c> in Phase 12) doesn't exist,
/// so locking the schema before that lands is premature. They'll get
/// their own fields when Phase 12 starts wiring them.
/// </para>
/// <para>
/// Rank is persisted (Front / Mid / Back) so the user's choice
/// survives across sessions even though Phase 6 doesn't yet consume
/// it — display-only on the PartyWindow / scoreboard for now.
/// </para>
/// </remarks>
public sealed class PartySettings
{
    /// <summary>
    /// Cadence for <see cref="Game.PartyPoller"/>'s periodic <c>par</c>
    /// poll, in seconds. MegaMUD default is 5; range 1..60.
    /// </summary>
    public int ParPollFrequencySec { get; set; } = 5;

    /// <summary>
    /// When a party member disconnects and reconnects within the
    /// <see cref="Game.PartyManager.DisconnectGraceWindow"/>, the leader
    /// auto-sends <c>invite &lt;name&gt;</c>. Off lets the user re-invite
    /// manually.
    /// </summary>
    public bool AutoInviteReconnecting { get; set; } = true;

    /// <summary>
    /// "If leading, wait only" — leader-side cap on how long we keep
    /// watching for a dropped party member to return. Drives
    /// <see cref="Game.PartyManager.DisconnectGraceWindow"/>: when a
    /// member's "X just hung up!!!" / "X just disconnected!!!." line
    /// fires (or par observes them missing without a per-player
    /// signal), we record the moment and re-invite them if they
    /// re-enter the realm within this window. Stored as total
    /// seconds. Range 0..3600 (1 hour). Default 90.
    /// </summary>
    public int IfLeadingWaitTotalSec { get; set; } = 90;

    /// <summary>
    /// On loop / Auto-Lair start (Phase 7 trigger), broadcast
    /// <c>@Reset</c> to every party member so their exp / kill counters
    /// zero together. Gated by
    /// <see cref="Game.Remote.PartyBroadcaster.AutoExpResetEnabled"/>.
    /// </summary>
    public bool ResetStatisticsOnLoopStart { get; set; } = true;

    /// <summary>
    /// Persisted local-character rank — Front / Mid / Back. Phase 12
    /// CombatManager will read this when it wires party-aware target
    /// ordering; PR 6.9 just persists the user's choice.
    /// </summary>
    public PartyRank Rank { get; set; } = PartyRank.Mid;

    /// <summary>
    /// Seconds to wait after sending <c>invite X</c> before sending the
    /// first follow-up <c>/X @join</c> nag. Range 1..60, default 5.
    /// Drives <see cref="Game.AutoPartyManager"/>'s nag escalation.
    /// </summary>
    public int JoinNagInitialDelaySec { get; set; } = 5;

    /// <summary>
    /// Cadence for the <c>@join</c> resend after the initial nag fires.
    /// Range 1..60, default 10. Stops once the target joins, telepaths
    /// us back, or <see cref="JoinNagMaxTotalSec"/> elapses.
    /// </summary>
    public int JoinNagFrequencySec { get; set; } = 10;

    /// <summary>
    /// Hard cap on the total nag window measured from the initial
    /// <c>invite</c>. Past this many seconds we give up and stop
    /// sending <c>@join</c>. Range 5..600, default 55.
    /// </summary>
    public int JoinNagMaxTotalSec { get; set; } = 55;
}

/// <summary>Local character's combat rank within a party.</summary>
public enum PartyRank
{
    Front,
    Mid,
    Back,
}
