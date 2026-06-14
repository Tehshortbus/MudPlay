using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Toolbar" tab — list editor for the per-character toolbar layout.
/// Top-to-bottom here ≡ left-to-right on the live toolbar. Each row is
/// either a button (resolved through <see cref="ToolbarItemCatalogue"/>)
/// or a separator. Apply persists the layout to the loaded profile and
/// re-hydrates the live <see cref="ToolbarConfig"/> through the standard
/// <see cref="ProfileService.NotifyMutated"/> path.
/// </summary>
public sealed partial class ToolbarSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Toolbar";

    private readonly ProfileService _profile;
    private readonly KeybindingStore _keybindings;
    private readonly MacroStore _macros;
    private readonly DialogService _dialogs;
    private bool _suppressDirty = true;
    private bool _dirty;
    private Control? _view;

    public override string Id => "toolbar";
    public override string Title => "Toolbar + Shortcuts";
    public override bool IsDirty => _dirty;

    public override IEnumerable<string> SearchableLabels =>
        ToolbarItemCatalogue.AllEntries.Select(e => e.Label)
            .Prepend("Shortcuts")
            .Prepend("Toolbar + Shortcuts");

    public override Control View => _view ??= new ToolbarSectionView { DataContext = this };

    /// <summary>True when any character profile is loaded.</summary>
    public bool HasProfile => _profile.Current is not null;

    /// <summary>Editable per-row view-models for the layout.</summary>
    public ObservableCollection<ToolbarRowViewModel> Rows { get; } = new();

    // ----- Visibility + orientation (Phase 4 wired) -----------------------
    // Three editor knobs that map onto ToolbarSettings.Visible / Vertical /
    // Side. RadioButton bindings go via IsLeftSide / IsRightSide mirrors
    // (Avalonia's RadioButton.IsChecked can't bind to an enum directly).

    /// <summary>Master visibility toggle (Show toolbar).</summary>
    [ObservableProperty] private bool _showToolbar = true;

    /// <summary>True = vertical orientation; false = horizontal top mount.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VerticalSideEnabled))]
    private bool _verticalToolbar;

    /// <summary>Edge picked for the vertical mount; ignored when <see cref="VerticalToolbar"/> = false.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLeftSide))]
    [NotifyPropertyChangedFor(nameof(IsRightSide))]
    private ToolbarSide _verticalSide = ToolbarSide.Left;

    /// <summary>Left radio bound here — two-way mirror of <see cref="VerticalSide"/>.</summary>
    public bool IsLeftSide
    {
        get => VerticalSide == ToolbarSide.Left;
        set { if (value) VerticalSide = ToolbarSide.Left; }
    }

    /// <summary>Right radio bound here — two-way mirror of <see cref="VerticalSide"/>.</summary>
    public bool IsRightSide
    {
        get => VerticalSide == ToolbarSide.Right;
        set { if (value) VerticalSide = ToolbarSide.Right; }
    }

    /// <summary>Radios are disabled until the user opts into vertical mode.</summary>
    public bool VerticalSideEnabled => VerticalToolbar;

    /// <summary>
    /// Currently-selected row in the toolbar list — the Move / Remove /
    /// keybind commands for the toolbar section act on this one.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveFromToolbarCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChangeToolbarKeybindCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetToolbarKeybindCommand))]
    private ToolbarRowViewModel? _selectedRow;

    /// <summary>
    /// Actions not currently on the toolbar, rendered as the lower
    /// "Shortcuts" list. Each is still keybindable — an action can own a
    /// shortcut whether or not it has a toolbar button. Promoting one
    /// (Add to toolbar) moves it up into <see cref="Rows"/>; removing a
    /// toolbar button drops it back here.
    /// </summary>
    public ObservableCollection<ToolbarRowViewModel> ShortcutRows { get; } = new();

    /// <summary>Currently-selected row in the Shortcuts list.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToToolbarCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChangeShortcutKeybindCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetShortcutKeybindCommand))]
    private ToolbarRowViewModel? _selectedShortcutRow;

    public ToolbarSectionViewModel(
        ProfileService profile,
        KeybindingStore keybindings,
        MacroStore macros,
        DialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(keybindings);
        ArgumentNullException.ThrowIfNull(macros);
        ArgumentNullException.ThrowIfNull(dialogs);
        _profile     = profile;
        _keybindings = keybindings;
        _macros      = macros;
        _dialogs     = dialogs;
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        // Live-refresh the per-row shortcut hint when any built-in
        // binding moves — the catalogue's hardcoded ShortcutHint is a
        // seed, not the source of truth once the user starts rebinding.
        _keybindings.BindingChanged  += OnBindingChanged;
        _keybindings.BindingsReloaded += RefreshAllShortcutHints;

        LoadFromProfile();
        _suppressDirty = false;
    }

    private void OnBindingChanged(BuiltInAction action)
    {
        foreach (ToolbarRowViewModel row in Rows)
            if (row.BoundAction == action)
                row.RefreshShortcutHint(_keybindings.Get(action));
        foreach (ToolbarRowViewModel row in ShortcutRows)
            if (row.BoundAction == action)
                row.RefreshShortcutHint(_keybindings.Get(action));
    }

    // Reset-all-shortcuts (BindingsReloaded) doesn't carry a single
    // action, so re-pull every row's live chord.
    private void RefreshAllShortcutHints()
    {
        foreach (ToolbarRowViewModel row in Rows)
            if (row.BoundAction is BuiltInAction a)
                row.RefreshShortcutHint(_keybindings.Get(a));
        foreach (ToolbarRowViewModel row in ShortcutRows)
            if (row.BoundAction is BuiltInAction a)
                row.RefreshShortcutHint(_keybindings.Get(a));
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        ToolbarSettings dto = new()
        {
            Layout   = Rows.Select(r => r.ToModel()).ToList(),
            Visible  = ShowToolbar,
            Vertical = VerticalToolbar,
            Side     = VerticalSide,
        };

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();
        _profile.NotifyMutated();
        ClearDirty();
    }

    public override void Discard()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
    }

    private void OnProfileChanged(CharacterProfile _) => ReloadAfterProfileSwap();
    private void OnProfileClosedExternally() => ReloadAfterProfileSwap();

    private void ReloadAfterProfileSwap()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
        OnPropertyChanged(nameof(HasProfile));
    }

    private void LoadFromProfile()
    {
        ToolbarSettings dto = ReadDtoOrDefault();
        List<ToolbarItem> items = dto.Layout is { Count: > 0 } layout ? layout : ToolbarDefaults.Build();
        Rows.Clear();
        foreach (ToolbarItem item in items)
        {
            ToolbarRowViewModel row = new(item.Kind, item.ActionId);
            if (row.BoundAction is BuiltInAction a)
                row.RefreshShortcutHint(_keybindings.Get(a));
            Rows.Add(row);
        }
        ShowToolbar     = dto.Visible;
        VerticalToolbar = dto.Vertical;
        VerticalSide    = dto.Side;
        RefreshShortcutRows();
    }

    private ToolbarSettings ReadDtoOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new ToolbarSettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json)) return new ToolbarSettings();
        try
        {
            return JsonSerializer.Deserialize<ToolbarSettings>(json.GetRawText()) ?? new ToolbarSettings();
        }
        catch
        {
            return new ToolbarSettings();
        }
    }

    /// <summary>
    /// Rebuild the lower "Shortcuts" list: every catalogue action whose
    /// button isn't currently placed on the toolbar. These stay
    /// keybindable even with no toolbar button — that's the whole point
    /// of the unified pool. Preserves the current selection by action id
    /// so a rebuild (after add/remove) doesn't clear the highlight.
    /// </summary>
    private void RefreshShortcutRows()
    {
        HashSet<string> placed = new(
            Rows.Where(r => r.Kind == ToolbarItemKind.Button && r.ActionId is not null)
                .Select(r => r.ActionId!),
            StringComparer.OrdinalIgnoreCase);

        string? keepSelected = SelectedShortcutRow?.ActionId;
        ShortcutRows.Clear();
        foreach (ToolbarItemCatalogue.Entry e in ToolbarItemCatalogue.AllEntries)
        {
            if (placed.Contains(e.ActionId)) continue;
            ToolbarRowViewModel row = new(ToolbarItemKind.Button, e.ActionId);
            if (row.BoundAction is BuiltInAction a)
                row.RefreshShortcutHint(_keybindings.Get(a));
            ShortcutRows.Add(row);
        }
        if (keepSelected is not null)
            SelectedShortcutRow = ShortcutRows.FirstOrDefault(
                r => string.Equals(r.ActionId, keepSelected, StringComparison.OrdinalIgnoreCase));
    }

    // ----- Commands -----

    /// <summary>Promote the selected Shortcuts-list action to a toolbar button.</summary>
    [RelayCommand(CanExecute = nameof(CanAddToToolbar))]
    private void AddToToolbar()
    {
        if (SelectedShortcutRow?.ActionId is not { } actionId) return;
        ToolbarRowViewModel newRow = new(ToolbarItemKind.Button, actionId);
        if (newRow.BoundAction is BuiltInAction a)
            newRow.RefreshShortcutHint(_keybindings.Get(a));
        Rows.Add(newRow);
        SelectedRow = newRow;
        RefreshShortcutRows();   // the promoted action leaves the shortcuts list
        Dirty();
    }

    private bool CanAddToToolbar() => SelectedShortcutRow is not null;

    /// <summary>Append a separator to the toolbar (separators are toolbar-only).</summary>
    [RelayCommand]
    private void AddSeparator()
    {
        Rows.Add(new ToolbarRowViewModel(ToolbarItemKind.Separator, null));
        SelectedRow = Rows[^1];
        Dirty();
    }

    /// <summary>
    /// Remove the selected toolbar row. A button drops back into the
    /// Shortcuts list (still keybindable); a separator is just deleted.
    /// No confirm prompt — the move is non-destructive and revertible via
    /// the section's Cancel/Discard.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveFromToolbar))]
    private void RemoveFromToolbar()
    {
        if (SelectedRow is null) return;
        Rows.Remove(SelectedRow);
        SelectedRow = null;
        RefreshShortcutRows();
        Dirty();
    }

    private bool CanRemoveFromToolbar() => SelectedRow is not null;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp()
    {
        if (SelectedRow is null) return;
        int i = Rows.IndexOf(SelectedRow);
        if (i <= 0) return;
        ToolbarRowViewModel moved = SelectedRow;
        Rows.Move(i, i - 1);
        // Keep the row selected so the user can press Move up / down again
        // without re-clicking. The index changed but the value didn't, so
        // we have to nudge the CanExecute observers manually.
        SelectedRow = moved;
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        Dirty();
    }

    private bool CanMoveUp() => SelectedRow is not null && Rows.IndexOf(SelectedRow) > 0;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown()
    {
        if (SelectedRow is null) return;
        int i = Rows.IndexOf(SelectedRow);
        if (i < 0 || i >= Rows.Count - 1) return;
        ToolbarRowViewModel moved = SelectedRow;
        Rows.Move(i, i + 1);
        SelectedRow = moved;
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        Dirty();
    }

    private bool CanMoveDown() => SelectedRow is not null && Rows.IndexOf(SelectedRow) < Rows.Count - 1;

    [RelayCommand]
    private void ResetToDefaults()
    {
        Rows.Clear();
        foreach (ToolbarItem item in ToolbarDefaults.Build())
        {
            ToolbarRowViewModel row = new(item.Kind, item.ActionId);
            if (row.BoundAction is BuiltInAction a)
                row.RefreshShortcutHint(_keybindings.Get(a));
            Rows.Add(row);
        }
        SelectedRow = null;
        RefreshShortcutRows();
        Dirty();
    }

    /// <summary>
    /// Open the keybind editor for <paramref name="row"/>. The dialog
    /// handles both rebinding and clearing — conflict detection inside
    /// it blocks Save when the chosen combo collides with another
    /// built-in action or any user macro. No-ops for rows whose action
    /// isn't rebindable (separators, or any non-<see cref="BuiltInAction"/>).
    /// </summary>
    private async Task ChangeKeybindForRowAsync(ToolbarRowViewModel? row)
    {
        if (row?.BoundAction is not BuiltInAction action) return;

        ViewModels.Keybind.KeybindEditDialogViewModel vm =
            new(action, _keybindings, _macros);
        KeyChord chord = await _dialogs
            .OpenWindowAsync<ViewModels.Keybind.KeybindEditDialogViewModel, KeyChord>(vm);
        if (!chord.Equals(_keybindings.Get(action)))
            _keybindings.Rebind(action, chord);
    }

    [RelayCommand(CanExecute = nameof(CanChangeToolbarKeybind))]
    private Task ChangeToolbarKeybind() => ChangeKeybindForRowAsync(SelectedRow);

    private bool CanChangeToolbarKeybind() => SelectedRow?.BoundAction is not null;

    [RelayCommand(CanExecute = nameof(CanChangeShortcutKeybind))]
    private Task ChangeShortcutKeybind() => ChangeKeybindForRowAsync(SelectedShortcutRow);

    private bool CanChangeShortcutKeybind() => SelectedShortcutRow?.BoundAction is not null;

    /// <summary>Restore <paramref name="row"/>'s action to its seed chord (or unbind if it had none).</summary>
    private void ResetKeybindForRow(ToolbarRowViewModel? row)
    {
        if (row?.BoundAction is not BuiltInAction action) return;
        KeyChord def = KeybindingStore.DefaultBindings.TryGetValue(action, out KeyChord d)
            ? d : KeyChord.Empty;
        if (!def.Equals(_keybindings.Get(action)))
            _keybindings.Rebind(action, def);
    }

    [RelayCommand(CanExecute = nameof(CanChangeToolbarKeybind))]
    private void ResetToolbarKeybind() => ResetKeybindForRow(SelectedRow);

    [RelayCommand(CanExecute = nameof(CanChangeShortcutKeybind))]
    private void ResetShortcutKeybind() => ResetKeybindForRow(SelectedShortcutRow);

    /// <summary>Reset every built-in chord back to its default in one shot.</summary>
    [RelayCommand]
    private void ResetAllKeybinds() => _keybindings.ResetAllToDefaults();

    // Auto-generated PropertyChanged hooks for the visibility / orientation
    // observables — re-route into the shared Dirty() helper so the Apply
    // button lights up on any of the three knobs.
    partial void OnShowToolbarChanged(bool value)         => Dirty();
    partial void OnVerticalToolbarChanged(bool value)     => Dirty();
    partial void OnVerticalSideChanged(ToolbarSide value) => Dirty();

    private void Dirty()
    {
        if (_suppressDirty || _dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void ClearDirty()
    {
        if (!_dirty) return;
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }
}

/// <summary>
/// One row in <see cref="ToolbarSectionViewModel.Rows"/>. Exposes the
/// catalogue-resolved label + icon resource key + shortcut so the
/// editor row template can render <c>[icon] [label] [(shortcut)]</c>
/// without doing the lookup in XAML.
/// </summary>
public sealed partial class ToolbarRowViewModel : ObservableObject
{
    public ToolbarItemKind Kind { get; }
    public string? ActionId { get; }

    /// <summary>
    /// The <see cref="BuiltInAction"/> this row binds to, when the
    /// action id parses as one of the rebindable enum members. Non-
    /// rebindable rows (separators, ToggleCapture, ActionGetAll, …)
    /// return <c>null</c>; the editor uses this to gate the Change /
    /// Reset keybind commands.
    /// </summary>
    public BuiltInAction? BoundAction { get; }

    public bool IsButton => Kind == ToolbarItemKind.Button;
    public bool IsSeparator => Kind == ToolbarItemKind.Separator;

    public string DisplayLabel { get; }
    public string? IconResourceKey { get; }

    [ObservableProperty] private string? _shortcutHint;

    public ToolbarRowViewModel(ToolbarItemKind kind, string? actionId)
    {
        Kind = kind;
        ActionId = actionId;

        if (kind == ToolbarItemKind.Separator)
        {
            DisplayLabel = "──── Separator ────";
            IconResourceKey = null;
            return;
        }

        ToolbarItemCatalogue.Entry? entry = ToolbarItemCatalogue.Find(actionId);
        DisplayLabel = entry?.Label ?? $"(unknown action: {actionId})";
        IconResourceKey = entry?.IconResourceKey;
        // Catalogue ShortcutHint is the seed display; the section VM
        // replaces it with the live KeybindingStore label once it
        // resolves the row's BuiltInAction.
        ShortcutHint = entry?.ShortcutHint is { } s ? $"({s})" : null;

        if (actionId is not null
            && Enum.TryParse(actionId, ignoreCase: false, out BuiltInAction parsed))
        {
            BoundAction = parsed;
        }
    }

    /// <summary>Push the live keybind label into the row (or clear when unbound).</summary>
    public void RefreshShortcutHint(KeyChord chord)
        => ShortcutHint = chord.IsEmpty ? null : $"({chord.Label})";

    public ToolbarItem ToModel() => new() { Kind = Kind, ActionId = ActionId };
}
