namespace FujinTerm.Models.Settings;

/// <summary>
/// UX-confirmation preferences shown in Settings → BBS's Display group.
/// Each flag governs whether the corresponding action ("are you sure?"-
/// prompts) prompts the user before proceeding. Defaults are all
/// <c>false</c> so the historical no-prompt behaviour is preserved
/// until the user opts in.
/// </summary>
/// <remarks>
/// Global tier — these are install-wide UX preferences, not per-BBS
/// or per-character. Stored as the <c>"Confirm"</c> entry inside
/// <see cref="GlobalSettings.Settings"/>.
/// </remarks>
public sealed class ConfirmSettings
{
    /// <summary>
    /// Prompt before the application exits (window X / File → Quit /
    /// hotkey). Explicit <c>= false</c> default — fresh installs land
    /// with no prompts so power users aren't nagged out of the gate.
    /// </summary>
    public bool ConfirmExit { get; set; } = false;

    /// <summary>
    /// Prompt before a user-initiated disconnect (toolbar / hotkey /
    /// File → Disconnect). App-initiated disconnects — carrier-lost
    /// reconnect cycles, remote <c>@hangup</c>, future health-threshold
    /// drops — bypass the prompt; this flag only applies to actions
    /// the user explicitly took. Default off.
    /// </summary>
    public bool ConfirmHangup { get; set; } = false;

    /// <summary>
    /// Prompt before saving settings (Settings → OK / Apply) and other
    /// JSON-write commits (Game Data browser saves). "No" returns the
    /// user to whatever they were doing with no save and no window
    /// close. Default off.
    /// </summary>
    public bool ConfirmSaveSettings { get; set; } = false;

    /// <summary>
    /// Prompt before destructive list-row removals (toolbar row delete,
    /// BBS profile delete, future game-data record deletes, etc.).
    /// Default off.
    /// </summary>
    public bool ConfirmDeletes { get; set; } = false;
}
