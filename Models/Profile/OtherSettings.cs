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
    public int MaxSuicideLivesThreshold { get; set; } = 5;

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

    // ----- Game-menu commands -------------------------------------------
    // The two commands the client uses to enter / leave the realm from
    // the MajorMUD main menu. Defaults are the standard menu picks
    // ("E" = enter realm, "=x" = logoff from main menu). Persisted per
    // character so different BBS dialects (alternate menu key bindings)
    // can be overridden if needed.

    /// <summary>
    /// Sent on profile's first session load or post-cleanup re-login
    /// once the client detects the main menu. Default <c>"E"</c>
    /// (Enter the Realm). The cleanup-warning / main-menu detection
    /// + delayed send logic lands in a follow-up PR once the
    /// message-matching engine + small scheduler exist; this field is
    /// persisted-and-ready now so the user can pre-configure the
    /// command per character.
    /// </summary>
    public string GameEntryCommand { get; set; } = "E";

    /// <summary>
    /// Sent when an incoming <c>@hangup</c> with the
    /// <see cref="Models.GameData.PlayerRemoteControls.HangupDisconnect"/>
    /// permission is received, OR when the client observes a cleanup
    /// warning and the X→wait→logoff sequence reaches the main menu.
    /// Default <c>"=x"</c> (logoff from main menu). The full cleanup
    /// automation flow ships in a follow-up; the @hangup direct
    /// handler ships now and uses this verbatim.
    /// </summary>
    public string GameExitCommand  { get; set; } = "=x";

    /// <summary>
    /// Caps the search loop in the @trap handler — how many
    /// <c>sea &lt;dir&gt;</c> attempts we'll make before giving up and
    /// telepathing the sender that we couldn't find a trap. Default
    /// 20, range 1..100. Surfaced above the disarm-attempts row in
    /// Settings → Other.
    /// </summary>
    public int MaxTrapSearchAttempts { get; set; } = 20;

    /// <summary>
    /// Caps the disarm-retry loop in the @trap handler — how many
    /// <c>disarm trap &lt;dir&gt;</c> attempts we'll make after the
    /// trap has been spotted before giving up. Default 5, range
    /// 1..50. Damage-aware abort (stop early if the trap fires and
    /// we lose HP) ships with the Phase 13 HealthManager wiring.
    /// </summary>
    public int MaxTrapDisarmAttempts { get; set; } = 5;

    // ----- Door / lock handling --------------------------------------

    /// <summary>
    /// Walker's max <c>bash &lt;dir&gt;</c> retries before giving up
    /// on a single door. Hits when the player's strength is below
    /// the door's requirement and the server keeps replying with
    /// <c>"attempts to bash through fail"</c>. Default 10 per user
    /// direction.
    /// </summary>
    public int MaxBashAttempts { get; set; } = 10;

    /// <summary>
    /// Walker's max <c>pick &lt;dir&gt;</c> retries before giving up
    /// on a single door. Picking is probabilistic — the skill can
    /// fail even when the value meets the door requirement. Default
    /// 10 per user direction.
    /// </summary>
    public int MaxPickAttempts { get; set; } = 10;

    /// <summary>
    /// When <c>true</c>, the walker prefers <c>pick &lt;dir&gt;</c>
    /// over <c>bash &lt;dir&gt;</c> on doors where both verbs are
    /// viable. Bash is louder and breaks stealth; thieves typically
    /// flip this on. Default <c>false</c> (bash-first).
    /// </summary>
    public bool PicklocksOverBash { get; set; }
}
