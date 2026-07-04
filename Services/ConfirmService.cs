using FujinTerm.Models.Settings;
using FujinTerm.ViewModels;

namespace FujinTerm.Services;

// Routes "are you sure?" prompts through a single per-action API so the many
// call sites (window close, disconnect command, settings apply, list-row
// deletes) all check the same flag and surface the same dialog. Each method
// short-circuits to true when its corresponding Settings flag is off, so callers
// can always await and act on the bool without branching on the setting
// themselves.
//
// Owns the live ConfirmSettings mirror that AppServices hydrates from
// SettingsService on startup and on every Global-tier write — the BBS section's
// confirm checkboxes feed in through that same path.
public sealed class ConfirmService
{
    private readonly DialogService _dialogs;

    // Live mirror of the persisted Global-tier confirm flags.
    public ConfirmSettings Settings { get; private set; } = new();

    public ConfirmService(DialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        _dialogs = dialogs;
    }

    // Replace the live settings — called by AppServices on profile / global-settings change.
    public void ApplyFrom(ConfirmSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
    }

    // Prompt before the app exits. Returns true if the close should proceed
    // (either the setting is off or the user clicked Yes). Used by MainWindow's
    // Closing handler when the close was triggered by the user (window X, File →
    // Quit). Programmatic shutdowns (none today) would skip this by not calling
    // it.
    public Task<bool> ConfirmExitAsync()
        => Settings.ConfirmExit
            ? PromptAsync("Exit FujinTerm", "Are you sure you want to exit?", "Exit")
            : Task.FromResult(true);

    // Prompt before a user-initiated disconnect. App-initiated disconnects
    // (carrier-lost auto-reconnect cycle, remote @hangup, future
    // health-threshold drops) must NOT call this — only the toolbar / hotkey /
    // menu paths the user triggered should.
    public Task<bool> ConfirmHangupAsync()
        => Settings.ConfirmHangup
            ? PromptAsync("Disconnect", "Are you sure you want to disconnect from the BBS?", "Disconnect")
            : Task.FromResult(true);

    // Prompt before a settings save (Settings → OK / Apply, GameData browser
    // saves, anywhere user state is about to be written to a JSON file).
    public Task<bool> ConfirmSaveAsync()
        => Settings.ConfirmSaveSettings
            ? PromptAsync("Save settings", "Save your changes?", "Save")
            : Task.FromResult(true);

    // Prompt before a destructive delete. what names the thing being removed so
    // the dialog reads naturally ("Are you sure you want to delete the toolbar
    // row 'Connect'?").
    public Task<bool> ConfirmDeleteAsync(string what)
        => Settings.ConfirmDeletes
            ? PromptAsync("Delete", $"Are you sure you want to delete {what}?", "Delete")
            : Task.FromResult(true);

    // Generic Yes/No prompt for one-off cases that don't have a dedicated
    // Settings.Confirm* toggle (e.g. "apply changes to the running loop now?").
    // Always prompts — there's no per-call skip flag — so callers that need a
    // user-controllable skip should add their own bool setting and gate on it.
    public Task<bool> ConfirmAsync(string title, string body, string yesLabel = "Yes")
        => PromptAsync(title, body, yesLabel);

    private async Task<bool> PromptAsync(string title, string body, string yesLabel)
    {
        ConfirmDialogViewModel vm = new(title, body, yesLabel: yesLabel);
        return await _dialogs.OpenWindowAsync<ConfirmDialogViewModel, bool>(vm);
    }
}
