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

    /// <summary>
    /// Currently-selected row in the editor — the Up / Down / Delete
    /// commands act on this one.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
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

    public ToolbarSectionViewModel(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;

        LoadFromProfile();
        _suppressDirty = false;
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        ToolbarSettings dto = new()
        {
            Layout = Rows.Select(r => r.ToModel()).ToList(),
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
        List<ToolbarItem> items = ReadOrDefault();
        Rows.Clear();
        foreach (ToolbarItem item in items)
        {
            Rows.Add(new ToolbarRowViewModel(item.Kind, item.ActionId));
        }
        RefreshAvailableActions();
    }

    private List<ToolbarItem> ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return ToolbarDefaults.Build();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json)) return ToolbarDefaults.Build();
        ToolbarSettings? dto = JsonSerializer.Deserialize<ToolbarSettings>(json.GetRawText());
        return dto?.Layout is { Count: > 0 } layout ? layout : ToolbarDefaults.Build();
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

        Rows.Add(new ToolbarRowViewModel(ToolbarItemKind.Button, SelectedAvailable.ActionId));
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
            Rows.Add(new ToolbarRowViewModel(item.Kind, item.ActionId));
        }
        SelectedRow = null;
        RefreshAvailableActions();
        Dirty();
    }

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
public sealed class ToolbarRowViewModel
{
    public ToolbarItemKind Kind { get; }
    public string? ActionId { get; }

    public bool IsButton => Kind == ToolbarItemKind.Button;
    public bool IsSeparator => Kind == ToolbarItemKind.Separator;

    public string DisplayLabel { get; }
    public string? IconResourceKey { get; }
    public string? ShortcutHint { get; }

    public ToolbarRowViewModel(ToolbarItemKind kind, string? actionId)
    {
        Kind = kind;
        ActionId = actionId;

        if (kind == ToolbarItemKind.Separator)
        {
            DisplayLabel = "──── Separator ────";
            IconResourceKey = null;
            ShortcutHint = null;
            return;
        }

        ToolbarItemCatalogue.Entry? entry = ToolbarItemCatalogue.Find(actionId);
        DisplayLabel = entry?.Label ?? $"(unknown action: {actionId})";
        IconResourceKey = entry?.IconResourceKey;
        ShortcutHint = entry?.ShortcutHint is { } s ? $"({s})" : null;
    }

    public ToolbarItem ToModel() => new() { Kind = Kind, ActionId = ActionId };
}
