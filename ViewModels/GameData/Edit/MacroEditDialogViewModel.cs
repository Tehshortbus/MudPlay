using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Edit;

/// <summary>
/// Per-record edit dialog for Game Data Browser → Macros. Editable
/// fields: Name, the (Key + 3 modifier) chord, Command string (with
/// <c>^M</c> / <c>;</c> multi-step split semantics), Enabled flag.
/// Save returns the updated <see cref="Macro"/> via the dialog
/// service's <c>TResult</c>; Cancel returns <c>null</c>. Conflict
/// detection runs live as the user changes the chord:
/// <see cref="StatusMessage"/> + <see cref="HasError"/> flag the row
/// when the chord is forbidden by <see cref="KeybindRegistry"/> or
/// already bound by another macro.
/// </summary>
public sealed partial class MacroEditDialogViewModel : ObservableObject, IDialogViewModel<Macro>
{
    public event Action<Macro?>? CloseRequested;

    private readonly Macro _original;
    private readonly MacroStore _store;

    /// <summary>Picker items for the Key combo box — display name + underlying Key.</summary>
    public IReadOnlyList<KeyEntry> AvailableKeys { get; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _command = string.Empty;
    [ObservableProperty] private bool _enabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private KeyEntry? _selectedKey;

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

    public string Title => $"Macro — {(_original.Name.Length > 0 ? _original.Name : "(new)")}";

    /// <summary>Inline status line under the chord row. Red on error, muted preview on success.</summary>
    public string StatusMessage
    {
        get
        {
            if (SelectedKey is null) return "Pick a key.";
            if (KeybindRegistry.IsForbidden(SelectedKey.Key, Ctrl, Shift, Alt, out string? reason))
                return reason!;
            if (_store.IsDuplicate(SelectedKey.Key.ToString(), Ctrl, Shift, Alt, excluding: _original))
                return "Another macro is already bound to this chord.";
            return $"Chord: {ChordLabel}";
        }
    }

    public bool HasError =>
        SelectedKey is null
        || KeybindRegistry.IsForbidden(SelectedKey.Key, Ctrl, Shift, Alt, out _)
        || _store.IsDuplicate(SelectedKey.Key.ToString(), Ctrl, Shift, Alt, excluding: _original);

    public bool CanSave => !HasError;

    private string ChordLabel
    {
        get
        {
            string mods = (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "");
            return mods + (SelectedKey?.DisplayName ?? string.Empty);
        }
    }

    public MacroEditDialogViewModel(Macro original, MacroStore store)
    {
        _original = original;
        _store    = store;

        AvailableKeys = KeybindRegistry.BindableKeys
            .Select(b => new KeyEntry(b.DisplayName, b.Key))
            .ToArray();

        Name    = original.Name;
        Command = original.Command;
        Enabled = original.Enabled;
        Ctrl    = original.Ctrl;
        Shift   = original.Shift;
        Alt     = original.Alt;

        // Hydrate SelectedKey from the original Key string. Falls
        // through to null when the stored key isn't in the bindable
        // list (legacy data, future deprecated keys, etc.) — the user
        // is forced to pick a new key in that case.
        if (Enum.TryParse<Key>(original.Key, ignoreCase: true, out Key parsed))
            SelectedKey = AvailableKeys.FirstOrDefault(k => k.Key == parsed);
    }

    [RelayCommand]
    private void Save()
    {
        if (!CanSave || SelectedKey is null) return;
        Macro updated = new(
            Name:    (Name ?? string.Empty).Trim(),
            Key:     SelectedKey.Key.ToString(),
            Ctrl:    Ctrl,
            Shift:   Shift,
            Alt:     Alt,
            Command: Command ?? string.Empty,
            Enabled: Enabled);
        CloseRequested?.Invoke(updated);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    /// <summary>Combo-box item — display name + the underlying Avalonia key.</summary>
    public sealed record KeyEntry(string DisplayName, Key Key)
    {
        public override string ToString() => DisplayName;
    }
}
