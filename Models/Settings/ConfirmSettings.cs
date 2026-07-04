namespace FujinTerm.Models.Settings;

// UX-confirmation preferences shown in Settings → BBS's Display group.
// Each flag governs whether the corresponding action prompts the user
// before proceeding. Defaults are all false so the historical no-prompt
// behaviour is preserved until the user opts in.
//
// Global tier — these are install-wide UX preferences, not per-BBS or
// per-character. Stored as the "Confirm" entry inside
// GlobalSettings.Settings.
public sealed class ConfirmSettings
{
    // Prompt before the application exits (window X / File → Quit /
    // hotkey). Explicit = false default — fresh installs land with no
    // prompts so power users aren't nagged out of the gate.
    public bool ConfirmExit { get; set; } = false;

    // Prompt before a user-initiated disconnect (toolbar / hotkey /
    // File → Disconnect). App-initiated disconnects — carrier-lost
    // reconnect cycles, remote @hangup, future health-threshold drops —
    // bypass the prompt; this flag only applies to actions the user
    // explicitly took. Default off.
    public bool ConfirmHangup { get; set; } = false;

    // Prompt before saving settings (Settings → OK / Apply) and other
    // JSON-write commits (Game Data browser saves). "No" returns the user
    // to whatever they were doing with no save and no window close. Default
    // off.
    public bool ConfirmSaveSettings { get; set; } = false;

    // Prompt before destructive list-row removals (toolbar row delete, BBS
    // profile delete, future game-data record deletes, etc.). Default off.
    public bool ConfirmDeletes { get; set; } = false;
}
