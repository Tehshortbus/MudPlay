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
    public override string Title => "Toolbar";
    public override bool IsDirty => _dirty;

    public override IEnumerable<string> SearchableLabels =>
        ToolbarItemCatalogue.AllEntries.Select(e => e.Label).Prepend("Toolbar");

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
    /// Currently-selected row in the editor — the Up / Down / Delete
    /// commands act on this one.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChangeKeybindCommand))]
    private ToolbarRowViewModel? _selectedRow;

    /// <summary>
    /// Catalogue entries the user has not already placed on the toolbar.
    /// Drives the "Add Button" dropdown picker.
    /// </summary>
    public ObservableCollection<ToolbarItemCatalogue.Entry> AvailableActions { get; } = new();

    /// <summary>Selected entry in the Add-button dropdown.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddButtonCommand))]
    [NotifyPropertyChangedFor(nameof(IsCustomCommandSelected))]
    private ToolbarItemCatalogue.Entry? _selectedAvailable;

    /// <summary>
    /// Free-text typed by the user when <see cref="IsCustomCommandSelected"/>
    /// is true. Stub — Phase 4 PR 4.8 lets the textbox appear so the
    /// eventual UX is visible, but Add stays disabled until a later PR
    /// extends the persistence model.
    /// </summary>
    [ObservableProperty] private string _customCommand = string.Empty;

    /// <summary>True when the user picked the <c>Custom command…</c> sentinel from the dropdown.</summary>
    public bool IsCustomCommandSelected
        => SelectedAvailable is { ActionId: CustomCommandActionId };

    // Synthetic dropdown entries: the editor exposes "── Add separator ──"
    // and "Custom command…" alongside the real catalogue picks so the
    // user has one Add flow instead of three. Action ids are sentinels —
    // they never appear in the live catalogue, and the live toolbar
    // render-time lookup (ToolbarItemCatalogue.Find) returns null for
    // them so unknown rows are simply skipped.
    private const string SeparatorActionId     = "__separator__";
    private const string CustomCommandActionId = "__customcommand__";

    private static readonly ToolbarItemCatalogue.Entry SeparatorSentinel = new(
        SeparatorActionId, "── Add separator ──", "IconMinus", string.Empty,
        Tooltip: "Inserts a separator at the end of the layout.",
        InDefaultLayout: false);

    private static readonly ToolbarItemCatalogue.Entry CustomCommandSentinel = new(
        CustomCommandActionId, "Custom command…", "IconTools", string.Empty,
        Tooltip: "Adds a button that sends an arbitrary command — wires in a later PR.",
        InDefaultLayout: false);

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
        _keybindings.BindingChanged += OnBindingChanged;

        LoadFromProfile();
        _suppressDirty = false;
    }

    private void OnBindingChanged(BuiltInAction action)
    {
        foreach (ToolbarRowViewModel row in Rows)
        {
            if (row.BoundAction == action)
                row.RefreshShortcutHint(_keybindings.Get(action));
        }
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
        RefreshAvailableActions();
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

    private void RefreshAvailableActions()
    {
        HashSet<string> placed = new(
            Rows.Where(r => r.Kind == ToolbarItemKind.Button && r.ActionId is not null)
                .Select(r => r.ActionId!),
            StringComparer.OrdinalIgnoreCase);

        AvailableActions.Clear();
        foreach (ToolbarItemCatalogue.Entry e in ToolbarItemCatalogue.AllEntries)
        {
            if (placed.Contains(e.ActionId)) continue;
            AvailableActions.Add(e);
        }
        // Sentinels are always available — separators can be added many times,
        // and a Custom command can be added many times.
        AvailableActions.Add(SeparatorSentinel);
        AvailableActions.Add(CustomCommandSentinel);
    }

    // ----- Commands -----

    [RelayCommand(CanExecute = nameof(CanAddButton))]
    private void AddButton()
    {
        if (SelectedAvailable is null) return;

        if (SelectedAvailable.ActionId == SeparatorActionId)
        {
            Rows.Add(new ToolbarRowViewModel(ToolbarItemKind.Separator, null));
            SelectedRow = Rows[^1];
            Dirty();
            return;
        }

        // Custom command — stub for Phase 4 PR 4.8 (no persistence shape yet).
        // CanAddButton blocks this branch from firing today; left as a guard.
        if (SelectedAvailable.ActionId == CustomCommandActionId) return;

        ToolbarRowViewModel newRow = new(ToolbarItemKind.Button, SelectedAvailable.ActionId);
        if (newRow.BoundAction is BuiltInAction a)
            newRow.RefreshShortcutHint(_keybindings.Get(a));
        Rows.Add(newRow);
        SelectedRow = Rows[^1];
        RefreshAvailableActions();
        Dirty();
    }

    private bool CanAddButton()
        => SelectedAvailable is not null
        && SelectedAvailable.ActionId != CustomCommandActionId;

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

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected()
    {
        if (SelectedRow is null) return;
        Rows.Remove(SelectedRow);
        SelectedRow = null;
        RefreshAvailableActions();
        Dirty();
    }

    private bool CanDeleteSelected() => SelectedRow is not null;

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
        RefreshAvailableActions();
        Dirty();
    }

    /// <summary>
    /// Open the keybind editor for the currently-selected row. The
    /// dialog handles both rebinding and clearing — conflict detection
    /// inside the dialog blocks Save when the chosen combo collides
    /// with another toolbar button or any user macro. No-ops for
    /// separator rows and for button rows whose action isn't
    /// rebindable (toolbar entries like ToggleCapture, ActionGetAll,
    /// or any non-<see cref="BuiltInAction"/>).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeKeybind))]
    private async Task ChangeKeybindAsync()
    {
        if (SelectedRow?.BoundAction is not BuiltInAction action) return;

        ViewModels.Keybind.KeybindEditDialogViewModel vm =
            new(action, _keybindings, _macros);
        KeyChord chord = await _dialogs
            .OpenWindowAsync<ViewModels.Keybind.KeybindEditDialogViewModel, KeyChord>(vm);
        if (!chord.Equals(_keybindings.Get(action)))
            _keybindings.Rebind(action, chord);
    }

    private bool CanChangeKeybind() => SelectedRow?.BoundAction is not null;

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
