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
}
