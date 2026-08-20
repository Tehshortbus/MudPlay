namespace MudPlay.Models.Profile;

// Per-character "General" settings — what to do on logon and the master on/off
// state for every auto-engine. Stored as the "General" entry in
// CharacterProfile.Settings.
//
// AutoMode is the single source of truth for whether each auto-engine fires.
// The earlier ManualMode column (mirroring MegaMUD's manual-vs-auto preset
// pair) is gone — engines either run or they don't; the per-character preset
// story belongs on the engines themselves, not on a duplicate column here.
public sealed class GeneralSettings
{
    // What to do once logon completes.
    public InitialTask DefaultTask { get; set; } = InitialTask.DoNothing;

    // Loop name to start when DefaultTask is BeginLoop.
    public string? DefaultLoopName { get; set; }

    // Named Auto-Lair configuration to start when DefaultTask is BeginAutoLair.
    public string? DefaultAutoLairName { get; set; }

    // Connect to the configured BBS as soon as the profile loads.
    public bool AutoConnect { get; set; }

    // Before persisting changes, copy the existing profile JSON to
    // {name}.json.bak. Off by default; users who want a safety net for
    // hand-edits or settings churn can flip it on.
    public bool BackupOnSave { get; set; }

    // Auto-scale the terminal glyphs up to fill the window while keeping the
    // fixed cell grid (cols/rows unchanged — this is a purely visual zoom, no
    // NAWS effect). Off by default: the grid renders at the configured font
    // size and sits centred with empty margin when the window is larger. On:
    // TerminalControl fits the font to the viewport, capped so it never grows
    // absurdly large. Char-tier; surfaced in Settings → General.
    public bool ScaleTerminalToWindow { get; set; }

    // When on (the default), typing while another (modeless) window is focused
    // falls through to the terminal — so you can keep sending commands with a
    // dialog open — UNLESS a text field in that window owns the keystroke, or the
    // key is one the dialog needs (Tab / Escape / menu chords). Off restores the
    // classic behaviour where keys go only to the focused window. Gates
    // TerminalInputRouter.Enabled; surfaced in Settings → General.
    public bool TypeToTerminalFromOtherWindows { get; set; } = true;

    // When on (the default), the animated splash plays on the terminal at startup
    // until a session begins. Off shows only the static header (the "MudPlay"
    // title, byline, and hint) — those stay regardless. Surfaced in Settings →
    // General. Read at launch so its Global-tier value applies before a profile
    // loads; also pushed live through DisplayConfig.SplashAnimate on Apply, so
    // toggling it stops/starts a splash that's already on screen.
    public bool ShowStartupMudAnimation { get; set; } = true;

    // Terminal canvas font family as an avares:// URI. Null = the bundled MX437
    // CP437 bitmap font (the default). Char-tier — the font choice follows the
    // character, not the board it happens to be connected to.
    public string? TerminalFontFamily { get; set; }

    // Terminal canvas font size in points. Null = 16 (the default). Char-tier;
    // relocated here from the per-BBS Display settings.
    public double? TerminalFontSize { get; set; }

    // Navigation map hover-tooltip font family as an avares:// URI. Null = the
    // bundled MX437 CP437 bitmap font (the FontTerminal resource the tooltip has
    // always used). Independent of the terminal-canvas font above so the map
    // tooltip can be tuned on its own. Char-tier.
    public string? NavTooltipFontFamily { get; set; }

    // Navigation map hover-tooltip font size in points. Null = 13 (the size the
    // tooltip has always rendered at). Char-tier.
    public double? NavTooltipFontSize { get; set; }

    // LIVE on/off state for every auto-engine — the state the toolbar toggles
    // drive and each engine reads per-tick. Each flag gates whether the matching
    // engine fires: AutoActionDefaults.AutoCombat gates Game.Combat.CombatManager
    // + the Game.Combat.CombatStateTracker's Combat-gate assertion;
    // AutoActionDefaults.AutoHealRest gates Game.Health.HealthManager; the others
    // gate their own engines. The toolbar Toggle* commands write this directly.
    // It is transient across a session — reconciled back to AutoModeBase (below)
    // at profile load and at each loop / auto-lair circuit start.
    public AutoActionDefaults AutoMode { get; set; } = new();

    // BASE (default) engine modes for this character — the Settings → General
    // "base modes" checkboxes edit THIS, not the live AutoMode. It is the state
    // the engines settle into at profile load and when a loop / auto-lair circuit
    // begins, so the user can flip live toolbar toggles mid-route (e.g. combat
    // off to sprint 500 rooms to a loop) without touching their normal defaults —
    // the circuit start flips the toolbar back to match these boxes, once per run.
    // Deliberately decoupled from AutoMode: a toolbar flip changes AutoMode, never
    // this. Null on a character that predates the split — treated as equal to
    // AutoMode (see resolve sites) so nothing changes until the boxes are saved.
    public AutoActionDefaults? AutoModeBase { get; set; }

    // ----- Emergency hangup carve-out --------------------------------

    // When true, the Game.Health.HealthManager emergency-hangup branch (HP below
    // HealthSettings.HangIfBelowHp → send the configured Game-Exit command)
    // still fires even when Auto-Heal/Rest — and every other auto-engine — is
    // off. The rest of the health engine stays disabled; only the
    // kill-the-connection safety net runs. Default false: hanging up is a
    // deliberate last resort, so an all-engines-off character won't
    // auto-disconnect unless the user opts in. Char-tier; surfaced in Settings →
    // General next to the auto-engine switches.
    public bool AllowHangupInAllOffMode { get; set; }

    // ----- Master hangup kill-switch ---------------------------------

    // When true, NO automatic hangup mechanic may drop the carrier — the client
    // disconnects only when the user explicitly asks (hotkey / toolbar / menu).
    // Suppresses the @hangup and @relog remote commands and the
    // Game.Health.HealthManager low-HP emergency hangup. Hard-overrides
    // AllowHangupInAllOffMode — if the user has explicitly disabled hangups, the
    // emergency carve-out stays silenced too. The one carve-out is the
    // Game.CleanupLogoutOrchestrator nightly-cleanup log-off: when the active
    // BBS's ReconnectAfterCleanup opt-in is on, that graceful exit runs even
    // with hangups disabled, because opting into "manage the cleanup cycle for
    // me" already asks the client to exit the realm and drop the carrier at
    // shutdown — otherwise a kill-switch party member lingers in the realm while
    // the rest of the party exits, then gets yanked by the BBS at the worst
    // moment. Default false. Char-tier; surfaced as the "Disable hangups"
    // toolbar toggle whose pressed state is remembered per character, like the
    // auto-mode toggles.
    public bool DisableHangups { get; set; }

    // ----- Sprint Mode -------------------------------------------------

    // When true, Game.Health.HealthManager never pauses movement to rest/heal-wait
    // (both HealthRecoveryGate and ManaRecoveryGate stay suppressed, mirroring the
    // per-waypoint DoNotRest mechanism but globally) — configured heal spells still
    // fire on their normal HP/MA thresholds, and every other safety pause (avoid
    // rooms, hazard/trap detours, party sync, mortally-wounded) is untouched. Turning
    // it on also forces Auto Combat off for the duration (restored on turning it back
    // off) since no MajorMUD mechanic blocks movement due to being engaged, so a
    // "just keep moving" mode has nothing to fight for. The only thing that actually
    // stops a sprinting character is death. Default false: this is a deliberate
    // "arrive or die" opt-in, never the surprise. Char-tier; surfaced as the "Sprint
    // Mode" toolbar toggle whose pressed state is remembered per character, like the
    // auto-mode toggles.
    public bool SprintMode { get; set; }

    // ----- Re-enable auto-actions on reconnect -----------------------
    // One flag per auto-action (1-to-1 with AutoMode above). When a
    // reconnect happens (a TCP connect following a prior in-session
    // disconnect), each auto-action whose flag here is on gets flipped
    // back ON in AutoMode — covering the common case where the user
    // manually disabled an engine mid-session, dropped, and wants it live
    // again on the redial without re-toggling by hand. Default OFF for
    // every action: re-enabling automatically is an opt-in convenience,
    // never the surprise. First connect of an app session is NOT a
    // reconnect and never triggers these.

    // Re-enable Auto-Combat on reconnect. Default off.
    public bool ReEnableAutoCombatOnReconnect   { get; set; }

    // Re-enable Auto-Nuke on reconnect. Default off.
    public bool ReEnableAutoNukeOnReconnect     { get; set; }

    // Re-enable Auto-Heal/Rest on reconnect. Default off.
    public bool ReEnableAutoHealRestOnReconnect { get; set; }

    // Re-enable Auto-Bless on reconnect. Default off.
    public bool ReEnableAutoBlessOnReconnect    { get; set; }

    // Re-enable Auto-Light on reconnect. Default off.
    public bool ReEnableAutoLightOnReconnect    { get; set; }

    // Re-enable Auto-Get-Items on reconnect. Default off.
    public bool ReEnableAutoGetItemsOnReconnect { get; set; }

    // Re-enable Auto-Get-Cash on reconnect. Default off.
    public bool ReEnableAutoGetCashOnReconnect  { get; set; }

    // Re-enable Auto-Sneak on reconnect. Default off.
    public bool ReEnableAutoSneakOnReconnect    { get; set; }

    // Re-enable Auto-Hide on reconnect. Default off.
    public bool ReEnableAutoHideOnReconnect     { get; set; }

    // Re-enable Auto-Search on reconnect. Default off.
    public bool ReEnableAutoSearchOnReconnect   { get; set; }

    // Re-enable Auto-Train on reconnect. Default off. Auto-train's live flag
    // lives in AutoTrainerSettings.AutoTrain (not AutoMode above), so the
    // reconnect handler flips that entry rather than an AutoMode bit.
    public bool ReEnableAutoTrainOnReconnect    { get; set; }
}
