namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character "General" settings — what to do on logon and the
/// master on/off state for every auto-engine. Stored as the
/// <c>"General"</c> entry in <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// <see cref="AutoMode"/> is the single source of truth for whether
/// each auto-engine fires. The earlier ManualMode column (mirroring
/// MegaMUD's manual-vs-auto preset pair) is gone — engines either
/// run or they don't; the per-character preset story belongs on
/// the engines themselves, not on a duplicate column here.
/// </remarks>
public sealed class GeneralSettings
{
    /// <summary>What to do once logon completes.</summary>
    public InitialTask DefaultTask { get; set; } = InitialTask.DoNothing;

    /// <summary>
    /// Loop name to start when <see cref="DefaultTask"/> is
    /// <see cref="InitialTask.BeginLoop"/>. Picker UI lands in Phase 7
    /// with LoopManager; persisted as a string here so the value
    /// survives until then.
    /// </summary>
    public string? DefaultLoopName { get; set; }

    /// <summary>
    /// Named Auto-Lair configuration to start when <see cref="DefaultTask"/>
    /// is <see cref="InitialTask.BeginAutoLair"/>. Picker UI lands in
    /// Phase 7 with the Auto-Lair scheduler; persisted as a string here
    /// so the value survives until then.
    /// </summary>
    public string? DefaultAutoLairName { get; set; }

    /// <summary>Connect to the configured BBS as soon as the profile loads.</summary>
    public bool AutoConnect { get; set; }

    /// <summary>
    /// Before persisting changes, copy the existing profile JSON to
    /// <c>{name}.json.bak</c>. Off by default; users who want a safety
    /// net for hand-edits or settings churn can flip it on.
    /// </summary>
    public bool BackupOnSave { get; set; }

    /// <summary>
    /// Master on/off state for every auto-engine. Each flag gates
    /// whether the matching Phase 9 engine actually fires:
    /// <see cref="AutoActionDefaults.AutoCombat"/> gates
    /// <see cref="Game.Combat.CombatManager"/> + the
    /// <see cref="Game.Combat.CombatStateTracker"/>'s Combat-gate
    /// assertion; <see cref="AutoActionDefaults.AutoHealRest"/>
    /// gates <see cref="Game.Health.HealthManager"/>; the others land
    /// as their engines do (PR 9.D / 9.E / 9.F / etc.). Loading the
    /// profile, manual edit in Settings → General, and the toolbar
    /// Toggle* commands all write the same field.
    /// </summary>
    public AutoActionDefaults AutoMode { get; set; } = new();

    // ----- Emergency hangup carve-out --------------------------------

    /// <summary>
    /// When <c>true</c>, the <see cref="Game.Health.HealthManager"/>
    /// emergency-hangup branch (HP below <c>HealthSettings.HangIfBelowHp</c>
    /// → send the configured Game-Exit command) still fires even when
    /// Auto-Heal/Rest — and every other auto-engine — is off. The rest of
    /// the health engine stays disabled; only the kill-the-connection
    /// safety net runs. Default <c>false</c>: hanging up is a deliberate
    /// last resort, so an all-engines-off character won't auto-disconnect
    /// unless the user opts in. Char-tier; surfaced in Settings → General
    /// next to the auto-engine switches.
    /// </summary>
    public bool AllowHangupInAllOffMode { get; set; }

    // ----- Master hangup kill-switch ---------------------------------

    /// <summary>
    /// When <c>true</c>, NO automatic hangup mechanic may drop the
    /// carrier — the client disconnects only when the user explicitly
    /// asks (hotkey / toolbar / menu). Suppresses all four automatic
    /// paths: the <c>@hangup</c> and <c>@relog</c> remote commands, the
    /// <see cref="Game.Health.HealthManager"/> low-HP emergency hangup,
    /// and the <see cref="Game.CleanupLogoutOrchestrator"/> nightly-cleanup
    /// log-off. Hard-overrides <see cref="AllowHangupInAllOffMode"/> — if
    /// the user has explicitly disabled hangups, the emergency carve-out
    /// stays silenced too. Default <c>false</c>. Char-tier; surfaced as
    /// the "Disable hangups" toolbar toggle whose pressed state is
    /// remembered per character, like the auto-mode toggles.
    /// </summary>
    public bool DisableHangups { get; set; }

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

    /// <summary>Re-enable Auto-Combat on reconnect. Default off.</summary>
    public bool ReEnableAutoCombatOnReconnect   { get; set; }

    /// <summary>Re-enable Auto-Nuke on reconnect. Default off.</summary>
    public bool ReEnableAutoNukeOnReconnect     { get; set; }

    /// <summary>Re-enable Auto-Heal/Rest on reconnect. Default off.</summary>
    public bool ReEnableAutoHealRestOnReconnect { get; set; }

    /// <summary>Re-enable Auto-Bless on reconnect. Default off.</summary>
    public bool ReEnableAutoBlessOnReconnect    { get; set; }

    /// <summary>Re-enable Auto-Light on reconnect. Default off.</summary>
    public bool ReEnableAutoLightOnReconnect    { get; set; }

    /// <summary>Re-enable Auto-Get-Items on reconnect. Default off.</summary>
    public bool ReEnableAutoGetItemsOnReconnect { get; set; }

    /// <summary>Re-enable Auto-Get-Cash on reconnect. Default off.</summary>
    public bool ReEnableAutoGetCashOnReconnect  { get; set; }

    /// <summary>Re-enable Auto-Sneak on reconnect. Default off.</summary>
    public bool ReEnableAutoSneakOnReconnect    { get; set; }

    /// <summary>Re-enable Auto-Hide on reconnect. Default off.</summary>
    public bool ReEnableAutoHideOnReconnect     { get; set; }

    /// <summary>Re-enable Auto-Search on reconnect. Default off.</summary>
    public bool ReEnableAutoSearchOnReconnect   { get; set; }
}
