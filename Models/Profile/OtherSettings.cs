namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character "Other" settings — the misc bucket. Stored as the
/// <c>"Other"</c> entry in <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// Phase 6 wires one field — the suicide-lives threshold. The rest of
/// the Other tab (lock / trap / hangup / ignore-ailment / auto-engage
/// toggles) ships its consumers in Phases 7 / 11 / 13 and adds fields
/// here as those engines land. The tab still renders the full stub
/// catalog underneath the wired group so the user sees the surface
/// from day one.
/// </remarks>
public sealed class OtherSettings
{
    /// <summary>
    /// Block <c>@do suicide</c> / <c>@party suicide</c> when remaining
    /// lives are at or below this threshold. Default 3 per the Phase 6
    /// spec — protects players who haven't yet built up a comfortable
    /// lives buffer. Setting to <c>0</c> allows forced suicide through
    /// all lives. Pushed into
    /// <see cref="Game.Remote.RemoteCommandManager.MaxSuicideLivesThreshold"/>.
    /// </summary>
    /// <remarks>
    /// The engine still hard-blocks suicide commands when the live
    /// lives count is unknown (no <c>LivesProvider</c> bound) — the
    /// conservative default until the Phase 9 DEATH section wires
    /// up live-lives tracking. This setting only takes effect once
    /// that lives source is connected.
    /// </remarks>
    public int MaxSuicideLivesThreshold { get; set; } = 3;

    // ----- Ignored ailments ---------------------------------------------
    // Per user direction: the four "Ignore X" toggles gate whether
    // catching that ailment triggers an automatic @wait to the party
    // leader (or, when we ARE the leader, makes our own engines pause
    // until the affect is gone). Default UNCHECKED — most parties
    // want to pause on every ailment by default; the toggle is for
    // edge cases ("we're at the boss fight, don't pause for a poison
    // tick"). Once the message-matching engine ships, these flags
    // become the user-configurable input to WaitTriggerEngine.
    // Always-on triggers (over-encumbered, MovementPrevented,
    // Stunned = movement+attack prevented) bypass these flags.

    /// <summary>Don't auto-cure / don't @wait for poison. Default false (pause).</summary>
    public bool IgnorePoison    { get; set; }

    /// <summary>Don't auto-cure / don't @wait for blindness. Default false (pause).</summary>
    public bool IgnoreBlindness { get; set; }

    /// <summary>Don't auto-cure / don't @wait for confusion. Default false (pause).</summary>
    public bool IgnoreConfusion { get; set; }

    /// <summary>Don't auto-cure / don't @wait for disease. Default false (pause).</summary>
    public bool IgnoreDiseased  { get; set; }
}
