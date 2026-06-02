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
    /// <summary>Prompt before the application exits (window X / File → Quit / hotkey).</summary>
    public bool ConfirmExit { get; set; }

    /// <summary>
    /// Prompt before a user-initiated disconnect (toolbar / hotkey /
    /// File → Disconnect). App-initiated disconnects — carrier-lost
    /// reconnect cycles, remote <c>@hangup</c>, future health-threshold
    /// drops — bypass the prompt; this flag only applies to actions
    /// the user explicitly took.
    /// </summary>
    public bool ConfirmHangup { get; set; }

    /// <summary>
    /// Prompt before saving settings (Settings → OK / Apply) and other
    /// JSON-write commits (Game Data browser saves). "No" returns the
    /// user to whatever they were doing with no save and no window
    /// close.
    /// </summary>
    public bool ConfirmSaveSettings { get; set; }

    /// <summary>
    /// Prompt before destructive list-row removals (toolbar row delete,
    /// BBS profile delete, future game-data record deletes, etc.).
    /// </summary>
    public bool ConfirmDeletes { get; set; }
}
