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
    /// When leading a party, drop incoming <c>@wait</c> broadcasts so the
    /// leader's automation keeps running instead of pausing on a
    /// follower's request. Off (default) honours <c>@wait</c> regardless
    /// of leadership. Consumed by <see cref="Game.Remote.PartyEssentialHandlers"/>.
    /// </summary>
    public bool IgnoreWaitWhenLeading { get; set; }

    /// <summary>
    /// When the party leader fails to bash a door we can see ("You see
    /// &lt;leader&gt; attempt to bash the door to the &lt;dir&gt;."), pitch in by
    /// forcing the same door — <c>bash &lt;dir&gt;</c> or <c>pick &lt;dir&gt;</c>
    /// depending on <see cref="OtherSettings.PicklocksOverBash"/>. Only
    /// fires when the actor is our current <see cref="Game.PartyState.LeaderName"/>.
    /// Off by default. Consumed by <c>LeaderDoorAssistManager</c>.
    /// </summary>
    public bool HelpLeaderOpenDoors { get; set; }

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

    // ----- Party-cast spell pickers (CastingDirector PR 9.D) --------
    // Each Minor / Major slot owns BOTH a single-target spell and an
    // AOE / group spell. CastingDirector picks single vs AOE at cast
    // time based on how many party members are below the threshold.

    /// <summary>Single-target heal cast when one party member drops
    /// below <see cref="MinorHealMemberThresholdPercent"/>.</summary>
    public string? MinorPartyHealSpell { get; set; }

    /// <summary>Group AOE heal cast when
    /// <see cref="AoeMinMembers"/>+ members are below
    /// <see cref="MinorHealMemberThresholdPercent"/>.</summary>
    public string? MinorPartyHealAoeSpell { get; set; }

    /// <summary>Symmetric major / critical single-target heal.</summary>
    public string? MajorPartyHealSpell { get; set; }

    /// <summary>Symmetric major / critical group AOE heal.</summary>
    public string? MajorPartyHealAoeSpell { get; set; }

    /// <summary>Cast Minor party heal when any party member's HP%
    /// falls below this value. Default 70.</summary>
    public int MinorHealMemberThresholdPercent { get; set; } = 70;

    /// <summary>Cast Major party heal when any party member's HP%
    /// falls below this value. Default 40 — mirrors self-heal's
    /// MajorHealCombatTrigger default.</summary>
    public int MajorHealMemberThresholdPercent { get; set; } = 40;

    /// <summary>Minimum number of party members below threshold for
    /// the engine to switch from single-target to AOE / group heal.
    /// Default 2. Clamped to ≥ 2 at engine time so a misconfig can't
    /// fire AOE on a single hurt member.</summary>
    public int AoeMinMembers { get; set; } = 2;

    // ----- Capacity --------------------------------------------------

    /// <summary>
    /// Cap on engageable hostiles while in an active party — overrides
    /// the Combat tab's <see cref="CombatSettings.MaxMonstersInRoom"/>
    /// whenever <see cref="Game.PartyState.IsInParty"/> is true. Range
    /// 1..20; default 20 (same as the Combat default, so it's a no-op
    /// until the user tightens it). <see cref="CombatSettings.MinMonstersInRoom"/>
    /// still applies — only the upper bound is party-scoped.
    /// </summary>
    public int MaxMonstersWhenPartying { get; set; } = 20;

    // Party-cure + party-buff pickers ship in a follow-up commit —
    // they need per-member condition tracking + per-member active-
    // buff tracking, both of which are deferred until the spellbook
    // gamedata duration model lands.
}

/// <summary>Local character's combat rank within a party.</summary>
public enum PartyRank
{
    Front,
    Mid,
    Back,
}
