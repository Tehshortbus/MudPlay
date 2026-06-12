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

    /// <summary>
    /// Leader-side <c>@comeback</c> backtrack budget — when a stranded
    /// follower sends a bare <c>@comeback</c> (no target room), the
    /// leader pauses its active movement engine and walks backwards
    /// along the path just taken, room by room, up to this many rooms
    /// looking for the follower. If not recovered within the budget the
    /// leader gives up and goes idle to let the player handle it.
    /// Default 10, range 1..50. Ignored when the follower supplies an
    /// explicit room (<c>@comeback 9/1012</c>) — that path walks
    /// straight to the named room instead. Surfaced in Settings → Other.
    /// </summary>
    public int MaxComebackBacktrackRooms { get; set; } = 10;

    /// <summary>
    /// Follower-side auto-<c>@comeback</c>. When <c>true</c> (default) and
    /// a movement-blocking condition (prevents-movement gamedata flag or
    /// over-encumbrance) leaves us behind as the party leader walks off,
    /// we automatically telepath <c>@comeback &lt;room&gt;</c> to the
    /// leader so their party-recovery walk picks us up. When <c>false</c>,
    /// the left-behind is still detected but no request is sent — the
    /// player handles it manually. Defaults on: the request is a single
    /// telepath that moves nothing on our side, so being stranded silently
    /// is strictly the worse outcome. Char-tier setting; surfaced in
    /// Settings → Other.
    /// </summary>
    public bool AutoRequestComebackWhenLeftBehind { get; set; } = true;

    // Note: the former Phase 9 per-character verbose toggles
    // (VerboseCombat / VerboseRoomClassifier / VerboseCasting /
    // VerboseCash / VerboseStealth) + WriteCombatRoundTrace lived
    // here briefly. They moved to the Log pane menu as a single
    // "Combat diagnostics" umbrella switch (session-only, not
    // persisted) — see Services/LogDiagnosticState.cs. Verbose tracing
    // is a "while I'm debugging right now" affordance, not a per-
    // character preference, and keeping it off the profile saves it
    // from leaking on between sessions.

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

    // ----- Party bless gating ----------------------------------------
    // Two coarse gates the party-bless engine honors before it casts a
    // beneficial spell on a party member. Both default ON: blessing the
    // party is the normal expectation, and a player who wants to hold
    // casts under specific conditions opts out explicitly.

    /// <summary>When true (default), allow party-bless casts while the
    /// character is resting. Consumed by the party-bless path in
    /// <see cref="Game.Spells.CastingDirector"/>.</summary>
    public bool BlessWhileResting { get; set; } = true;

    /// <summary>When true (default), allow party-bless casts during
    /// combat. Consumed by the party-bless path in
    /// <see cref="Game.Spells.CastingDirector"/>.</summary>
    public bool BlessDuringCombat { get; set; } = true;
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
