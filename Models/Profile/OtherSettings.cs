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

    /// <summary>
    /// Walker max <c>sea &lt;dir&gt;</c> retries when revealing a
    /// hidden exit (<c>(Hidden)</c> modifier) along the path. Default
    /// 20 — mirrors the trap-search cap since it's the same verb,
    /// kept separate so the user can tune them independently.
    /// </summary>
    public int MaxHiddenSearchAttempts { get; set; } = 20;

    /// <summary>
    /// When <c>true</c>, <see cref="Game.HopTimingCalibrator"/> logs
    /// one Info line per observed hop with the wall-clock time + the
    /// current <see cref="Game.EncumbranceLevel"/>. Used to calibrate
    /// the Settings → Auto-Lair tab's per-encumbrance seconds-per-hop
    /// defaults against in-game truth. Off by default — it's a
    /// developer / data-collection knob, not a normal-play affordance.
    /// </summary>
    public bool LogMovementHopTiming { get; set; }

    // ----- Phase 9 verbose diagnostic toggles -----------------------
    // Per docs/10-phase-9-automation-engines.md § Cross-cut 3.
    // Each toggle gates whether its category's Debug-severity log lines
    // reach the LogPane. Info+ severity for the same category is always
    // on; these only control the verbose Debug rows. Off by default —
    // verbose channels are loud and only useful when troubleshooting a
    // specific subsystem.

    /// <summary>Enable Debug-severity logs from <c>Combat</c> category
    /// (CombatManager swing decisions + target picks + weapon swaps).
    /// Off by default.</summary>
    public bool VerboseCombat { get; set; }

    /// <summary>Enable Debug-severity logs from <c>RoomClassifier</c>
    /// category (Player / Monster / Unknown decisions per Also-Here
    /// entry, including the prefix-strip trail). Off by default.</summary>
    public bool VerboseRoomClassifier { get; set; }

    /// <summary>Enable Debug-severity logs from <c>Casting</c> category
    /// (CastingDirector tier evaluation + candidate scoring +
    /// per-tier-1/2/3 trace). Off by default.</summary>
    public bool VerboseCasting { get; set; }

    /// <summary>Enable Debug-severity logs from <c>Cash</c> category
    /// (CashManager pick / drop decisions + in-flight deltas +
    /// encumbrance gates). Off by default.</summary>
    public bool VerboseCash { get; set; }

    /// <summary>Enable Debug-severity logs from <c>Stealth</c> category
    /// (StealthManager FSM transitions + silent-loss detection).
    /// Off by default.</summary>
    public bool VerboseStealth { get; set; }

    /// <summary>
    /// When <c>true</c>, <see cref="Game.Combat.RoundDamageTracker"/>
    /// writes one row per combat round to
    /// <c>Data/Logs/combat-{sessionStart}.log</c> with the full round
    /// detail (observed lines + pre/post HP/MA snapshots + gate states +
    /// decisions taken). Off by default — independent of the LogPane
    /// Verbose toggles above.
    /// </summary>
    public bool WriteCombatRoundTrace { get; set; }

    // ----- Run-away behavior (HealthManager + walker integration) ---
    // Triggered by HealthSettings.RunIfBelowHp crossing. Flee
    // distance is CombatSettings.RunDistance (rooms to move before
    // re-evaluating). These two knobs shape HOW the retreat moves.

    /// <summary>Direction strategy when fleeing. Forward continues
    /// along the active walker path (away from where we entered);
    /// Backward retraces the steps we just came from.</summary>
    public RunDirection RunDirection { get; set; } = RunDirection.Backward;

    /// <summary>When true, HealthManager sends <c>break</c> before
    /// the first flee move so the auto-attack disengages and the
    /// move can land cleanly. When false the flee starts mid-fight
    /// and the server may reject the first move because we're still
    /// engaged — fast option for users who'd rather take the chance
    /// than waste a round on <c>break</c>.</summary>
    public bool BreakBeforeFleeing { get; set; } = true;
}

/// <summary>Direction strategy for the auto-flee path.</summary>
public enum RunDirection
{
    /// <summary>Retrace the path we just walked in on. Default —
    /// safer because we know what rooms we passed through.</summary>
    Backward,

    /// <summary>Continue along the active walker path away from
    /// where we came in. Faster but moves into unscouted rooms.</summary>
    Forward,
}
