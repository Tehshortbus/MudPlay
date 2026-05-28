namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character "General" settings — what to do on logon and the
/// default state of every Action-menu auto-toggle. Stored as the
/// <c>"General"</c> entry in <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// Manual / Auto-Mode pair: MegaMUD's convention is two presets a
/// player flips between depending on whether they want hands-on or
/// hands-off play. Both columns default to all auto-engines enabled;
/// users tighten one or both as they prefer.
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

    /// <summary>Auto-engine boot state for Manual-Mode play.</summary>
    public AutoActionDefaults ManualMode { get; set; } = new();

    /// <summary>Auto-engine boot state for Auto-Mode play.</summary>
    public AutoActionDefaults AutoMode { get; set; } = new();
}
