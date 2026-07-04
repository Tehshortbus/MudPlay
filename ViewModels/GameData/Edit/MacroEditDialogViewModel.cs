using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Edit;

// Per-record edit dialog for Game Data Browser → Macros. Editable fields: Name, the
// (Key + 3 modifier) chord, Command string (with ^M / ; multi-step split semantics),
// Enabled flag. Save returns the updated Macro via the dialog service's TResult; Cancel
// returns null. Conflict detection runs live as the user changes the chord: StatusMessage
// + HasError flag the row when the chord is forbidden by KeybindRegistry or already bound
// by another macro.
public sealed partial class MacroEditDialogViewModel : ObservableObject, IDialogViewModel<Macro>
{
    public event Action<Macro?>? CloseRequested;

    private readonly Macro _original;
    private readonly MacroStore _store;
    private readonly KeybindingStore _keybindings;

    [ObservableProperty] private string _command = string.Empty;
    [ObservableProperty] private bool _enabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(KeyDisplay))]
    private Key? _selectedKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyDisplay))]
    [NotifyPropertyChangedFor(nameof(CaptureButtonText))]
    // CanSave + StatusMessage both depend on IsCapturing — without these
    // the Save button stays greyed-out after exiting capture even when
    // the new chord is valid, because the binding never re-evaluates.
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    private bool _isCapturing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _ctrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _shift;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _alt;

    public string Title => $"Macro — {(_original.Command.Length > 0 ? _original.Command : "(new)")}";

    // Capture button label — flips to "Press key…" while in capture mode.
    public string CaptureButtonText => IsCapturing ? "Press key…" : "Capture";

    // The chord shown in the read-only key display. Empty until the user picks one.
    public string KeyDisplay
    {
        get
        {
            if (SelectedKey is null) return IsCapturing ? "(waiting for key…)" : "(not set)";
            string mods = (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "");
            (string DisplayName, Key Key)? entry = KeybindRegistry.FindBindable(SelectedKey.Value);
            return mods + (entry?.DisplayName ?? SelectedKey.Value.ToString());
        }
    }

    // Inline status line under the chord row. Red on error, muted preview on success.
    public string StatusMessage
    {
        get
        {
            if (SelectedKey is null)
                return IsCapturing
                    ? "Press a key combination — release a non-modifier key to confirm. Esc cancels."
                    : "Click Capture and press the keybind.";
            if (KeybindRegistry.IsForbidden(_keybindings, SelectedKey.Value, Ctrl, Shift, Alt, out string? reason))
                return reason!;
            if (_store.IsDuplicate(SelectedKey.Value.ToString(), Ctrl, Shift, Alt, excluding: _original))
                return "Another macro is already bound to this chord.";
            return $"Chord: {KeyDisplay}";
        }
    }

    public bool HasError =>
        SelectedKey is null
        || KeybindRegistry.IsForbidden(_keybindings, SelectedKey.Value, Ctrl, Shift, Alt, out _)
        || _store.IsDuplicate(SelectedKey.Value.ToString(), Ctrl, Shift, Alt, excluding: _original);

    public bool CanSave => !HasError && !IsCapturing;

    public MacroEditDialogViewModel(Macro original, MacroStore store, KeybindingStore keybindings)
    {
        _original     = original;
        _store        = store;
        _keybindings  = keybindings;

        Command = original.Command;
        Enabled = original.Enabled;
        Ctrl    = original.Ctrl;
        Shift   = original.Shift;
        Alt     = original.Alt;

        // Hydrate SelectedKey from the original Key string. Falls
        // through to null when the stored key isn't a valid Avalonia
        // Key (legacy data, mistyped) — the user is forced to capture
        // a fresh key in that case.
        if (Enum.TryParse<Key>(original.Key, ignoreCase: true, out Key parsed))
            SelectedKey = parsed;
    }

    // Toggle into capture mode — the code-behind handles the actual key events.
    [RelayCommand]
    private void StartCapture() => IsCapturing = true;

    // Called from the dialog's code-behind on every KeyDown / KeyUp while capture is
    // active. Tracks modifiers as the user holds them; commits the chord on the first
    // non-modifier key release. Escape cancels. key is the key just pressed or released,
    // modifiers is the live modifier state from the event, isKeyDown is true for KeyDown
    // and false for KeyUp. Returns true when capture mode handled the event and the caller
    // should mark it handled.
    public bool ProcessCaptureKey(Key key, KeyModifiers modifiers, bool isKeyDown)
    {
        if (!IsCapturing) return false;

        // Escape always cancels — never accepted as a bindable key.
        if (key == Key.Escape)
        {
            IsCapturing = false;
            return true;
        }

        // Modifier keys never commit on their own — they just update
        // the modifier state for the upcoming non-modifier press.
        bool isModifierOnly = key is Key.LeftCtrl or Key.RightCtrl
                                  or Key.LeftShift or Key.RightShift
                                  or Key.LeftAlt or Key.RightAlt
                                  or Key.LWin or Key.RWin;

        // On KeyDown, snapshot the live chord for the status preview
        // but DON'T commit yet — wait for the matching KeyUp so the
        // user can roll over modifiers without prematurely sealing.
        if (isKeyDown)
        {
            if (isModifierOnly) return true;
            // Preview: show the chord shape live.
            SelectedKey = key;
            Ctrl  = modifiers.HasFlag(KeyModifiers.Control);
            Shift = modifiers.HasFlag(KeyModifiers.Shift);
            Alt   = modifiers.HasFlag(KeyModifiers.Alt);
            return true;
        }

        // KeyUp on a non-modifier key commits the chord and exits.
        if (!isModifierOnly && SelectedKey == key)
        {
            IsCapturing = false;
        }
        return true;
    }

    [RelayCommand]
    private void Save()
    {
        if (!CanSave || SelectedKey is null) return;
        Macro updated = new(
            Key:     SelectedKey.Value.ToString(),
            Ctrl:    Ctrl,
            Shift:   Shift,
            Alt:     Alt,
            Command: Command ?? string.Empty,
            Enabled: Enabled);
        CloseRequested?.Invoke(updated);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
