using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Keybind;

// Per-action rebind dialog for built-in app actions (BuiltInAction). Capture-UX
// mirror of GameData.Edit.MacroEditDialogViewModel — same click-Capture → press
// chord → KeyUp-commits state machine, lighter because there's no Command /
// Enabled / Name to manage. Conflict detection runs against BOTH stores: a chord
// can never bind to a built-in action AND a macro at the same time, so the dialog
// rejects macro collisions just as the macro dialog rejects built-in collisions.
public sealed partial class KeybindEditDialogViewModel : ObservableObject, IDialogViewModel<KeyChord>
{
    public event Action<KeyChord>? CloseRequested;

    private readonly BuiltInAction _action;
    private readonly KeyChord _originalChord;
    private readonly KeybindingStore _keybindings;
    private readonly MacroStore _macros;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyDisplay))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private Key? _selectedKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyDisplay))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _ctrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyDisplay))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _shift;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyDisplay))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _alt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyDisplay))]
    [NotifyPropertyChangedFor(nameof(CaptureButtonText))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isCapturing;

    public string Title => $"Keybind — {KeybindingStore.ActionLabel(_action)}";
    public string ActionLabel => KeybindingStore.ActionLabel(_action);
    public string CaptureButtonText => IsCapturing ? "Press key…" : "Capture";

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

    public string StatusMessage
    {
        get
        {
            if (SelectedKey is null)
                return IsCapturing
                    ? "Press a key combination — release a non-modifier key to confirm. Esc cancels."
                    : "Click Capture and press the keybind, or Clear to leave the action unbound.";
            if (KeybindRegistry.ExcludedKeys.Contains(SelectedKey.Value))
                return $"'{SelectedKey.Value}' can't be bound as a keybind.";
            // Check OS / terminal reserved (Alt+F4, Ctrl+C/V) — these
            // never live in any store but still need to be refused.
            if (IsSystemReserved(SelectedKey.Value, Ctrl, Shift, Alt, out string? sysReason))
                return sysReason!;
            // Built-in collision: another action already owns this chord.
            BuiltInAction? builtIn = _keybindings.FindAction(CurrentChord);
            if (builtIn is BuiltInAction b && b != _action)
                return $"Already bound to built-in action: {KeybindingStore.ActionLabel(b)}.";
            // Macro collision: a user-defined macro owns this chord.
            Macro? macro = _macros.FindMatch(SelectedKey.Value.ToString(), Ctrl, Shift, Alt);
            if (macro is not null)
                return $"Already bound to macro: {macro.Command}.";
            return $"Chord: {KeyDisplay}";
        }
    }

    public bool HasError
    {
        get
        {
            if (SelectedKey is null) return false;  // empty chord is valid (unbind)
            if (KeybindRegistry.ExcludedKeys.Contains(SelectedKey.Value)) return true;
            if (IsSystemReserved(SelectedKey.Value, Ctrl, Shift, Alt, out _)) return true;
            BuiltInAction? builtIn = _keybindings.FindAction(CurrentChord);
            if (builtIn is BuiltInAction b && b != _action) return true;
            if (_macros.FindMatch(SelectedKey.Value.ToString(), Ctrl, Shift, Alt) is not null) return true;
            return false;
        }
    }

    public bool CanSave => !HasError && !IsCapturing;

    private KeyChord CurrentChord =>
        SelectedKey is null ? KeyChord.Empty : new KeyChord(SelectedKey.Value, Ctrl, Shift, Alt);

    public KeybindEditDialogViewModel(
        BuiltInAction action,
        KeybindingStore keybindings,
        MacroStore macros)
    {
        _action       = action;
        _originalChord = keybindings.Get(action);
        _keybindings  = keybindings;
        _macros       = macros;

        Hydrate(_originalChord);
    }

    private void Hydrate(KeyChord chord)
    {
        if (chord.IsEmpty)
        {
            SelectedKey = null;
            Ctrl = Shift = Alt = false;
            return;
        }
        SelectedKey = chord.Key;
        Ctrl  = chord.Ctrl;
        Shift = chord.Shift;
        Alt   = chord.Alt;
    }

    [RelayCommand]
    private void StartCapture() => IsCapturing = true;

    // Wipe the chord to KeyChord.Empty — leaves the action unbound on Save.
    [RelayCommand]
    private void Clear() => Hydrate(KeyChord.Empty);

    // Called from the dialog code-behind on every KeyDown / KeyUp while capture is
    // active; commits on the first non-modifier key release.
    public bool ProcessCaptureKey(Key key, KeyModifiers modifiers, bool isKeyDown)
    {
        if (!IsCapturing) return false;

        if (key == Key.Escape)
        {
            IsCapturing = false;
            return true;
        }

        bool isModifierOnly = key is Key.LeftCtrl or Key.RightCtrl
                                  or Key.LeftShift or Key.RightShift
                                  or Key.LeftAlt or Key.RightAlt
                                  or Key.LWin or Key.RWin;

        if (isKeyDown)
        {
            if (isModifierOnly) return true;
            SelectedKey = key;
            Ctrl  = modifiers.HasFlag(KeyModifiers.Control);
            Shift = modifiers.HasFlag(KeyModifiers.Shift);
            Alt   = modifiers.HasFlag(KeyModifiers.Alt);
            return true;
        }

        if (!isModifierOnly && SelectedKey == key)
            IsCapturing = false;
        return true;
    }

    [RelayCommand]
    private void Save()
    {
        if (!CanSave) return;
        CloseRequested?.Invoke(CurrentChord);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(_originalChord);

    // OS / terminal reserved chords — duplicated from KeybindRegistry's private
    // list because we need the EXCLUDING semantics (the action being edited
    // shouldn't flag itself as a built-in collision, but Alt+F4 + Ctrl+C/V stay
    // forbidden regardless of context).
    private static bool IsSystemReserved(Key key, bool ctrl, bool shift, bool alt, out string? reason)
    {
        if (key == Key.F4  && alt && !ctrl && !shift) { reason = "Reserved by OS: Close window."; return true; }
        if (key == Key.C   && ctrl && !alt && !shift) { reason = "Reserved by terminal: Copy.";   return true; }
        if (key == Key.V   && ctrl && !alt && !shift) { reason = "Reserved by terminal: Paste.";  return true; }
        reason = null;
        return false;
    }
}
