using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.Views.Settings;

namespace MudPlay.ViewModels.Settings;

// "Toolbar" tab — list editor for the per-character toolbar layout.
// Top-to-bottom here maps to left-to-right on the live toolbar. Each row is
// either a button (resolved through ToolbarItemCatalogue) or a separator. Apply
// persists the layout to the loaded profile and re-hydrates the live
// ToolbarConfig through the standard ProfileService.NotifyMutated path.
public sealed partial class ToolbarSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Toolbar";

    private readonly ProfileService _profile;
    private readonly KeybindingStore _keybindings;
    private readonly MacroStore _macros;
    private readonly DialogService _dialogs;
    private readonly SettingsService _globalSettings;
    private readonly BbsProfileStore _bbsStore;
    private bool _suppressDirty = true;
    private bool _dirty;
    private Control? _view;

    public override string Id => "toolbar";
    public override string Title => "Toolbar + Shortcuts";
    public override bool IsDirty => _dirty;

    public override IEnumerable<string> SearchableLabels =>
        ToolbarItemCatalogue.AllEntries.Select(e => e.Label)
            .Concat(new[]
            {
                "Toolbar + Shortcuts", "Shortcuts",
                "Help menu websites", "Website", "BBS website", "Help links",
            });

    public override Control View => _view ??= new ToolbarSectionView { DataContext = this };

    // True when any character profile is loaded.
    public bool HasProfile => _profile.Current is not null;

    // Editable per-row view-models for the layout.
    public ObservableCollection<ToolbarRowViewModel> Rows { get; } = new();

    // ----- Terminal right-click menu editor (Char tier) -------------------
    // The customizable entries below the pinned Favorites / Recent walk flyouts.
    // ContextMenuRows is the placed layout; ContextMenuPool is the fixed pool of
    // everything addable (MenuActionCatalogue). Persisted to
    // CharacterProfile.Settings["ContextMenu"] on Apply; the main window rebuilds
    // its ContextMenu from the re-hydrated AppServices.ContextMenu.
    private const string ContextMenuKey = "ContextMenu";

    public ObservableCollection<ContextMenuRowViewModel> ContextMenuRows { get; } = new();
    public ObservableCollection<ContextMenuRowViewModel> ContextMenuPool { get; } =
        new(MenuActionCatalogue.AllEntries.Select(e => new ContextMenuRowViewModel(e)));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveContextMenuEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveContextMenuUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveContextMenuDownCommand))]
    [NotifyPropertyChangedFor(nameof(AddContextMenuButtonText))]
    private ContextMenuRowViewModel? _selectedContextMenuRow;

    // The "Add" button relabels to make the folder flow clear: with a folder (or
    // one of its children) selected in the placed list, a new item lands INSIDE
    // that folder.
    public string AddContextMenuButtonText =>
        SelectedContextMenuRow is { } r && (r.IsFolder || r.Depth == 1)
            ? "Add into folder"
            : "Add to menu";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddContextMenuEntryCommand))]
    private ContextMenuRowViewModel? _selectedContextMenuPoolRow;

    // Inline feedback for import / export (mirrors CopyStatus).
    [ObservableProperty] private string? _contextMenuStatus;

    // ----- Help-menu website list (Global tier) ---------------------------
    // The reference links shown under Help. Global scope — shared across every
    // character/BBS — so this block stays live in the tab even with no profile
    // loaded (unlike the toolbar/shortcuts editor below it). Persisted to
    // GlobalSettings.Settings["HelpWebsites"] on Apply; the main window's Help
    // menu re-reads on GlobalSettingsChanged.
    public ObservableCollection<HelpWebsiteRowViewModel> WebsiteRows { get; } = new();

    // The active BBS's own website — a per-BBS value (BbsProfile.WebsiteUrl)
    // relocated here from the BBS tab, edited as a special row tied to whichever
    // BBS is active. Persisted via read-modify-write against a fresh disk copy
    // on Apply so a concurrent BBS-section save can't clobber it. Empty /
    // whitespace clears the field.
    [ObservableProperty] private string? _bbsWebsiteUrl;

    // Per-BBS toggle for whether the active BBS's site appears in the Help menu
    // at all. Independent of the URL — unchecking hides the "BBS site ↗" entry
    // even with a URL saved. Persisted alongside BbsWebsiteUrl.
    [ObservableProperty] private bool _showBbsWebsiteInHelp = true;

    // Display name of the BBS the website field ties to, or null when no BBS is
    // configured. Gates the field's editability in the view.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveBbs))]
    private string? _activeBbsName;

    public bool HasActiveBbs => !string.IsNullOrEmpty(ActiveBbsName);

    // ----- Visibility + position ------------------------------------------
    // Editor knobs that map onto ToolbarSettings.Visible / Position. The four
    // edge radios bind via bool mirrors (IsTop / IsBottom / IsLeft / IsRight)
    // because Avalonia's RadioButton.IsChecked can't bind to an enum directly.
    // All four share one group and stay live only while ShowToolbar is on;
    // when it's off the toolbar is hidden and the position choice greys out.

    // Master visibility toggle (Show toolbar).
    [ObservableProperty] private bool _showToolbar = true;

    // Edge the toolbar docks to; ignored while ShowToolbar = false.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTop))]
    [NotifyPropertyChangedFor(nameof(IsBottom))]
    [NotifyPropertyChangedFor(nameof(IsLeft))]
    [NotifyPropertyChangedFor(nameof(IsRight))]
    private ToolbarPosition _position = ToolbarPosition.Top;

    // Top radio bound here — two-way mirror of Position.
    public bool IsTop
    {
        get => Position == ToolbarPosition.Top;
        set { if (value) Position = ToolbarPosition.Top; }
    }

    // Bottom radio bound here — two-way mirror of Position.
    public bool IsBottom
    {
        get => Position == ToolbarPosition.Bottom;
        set { if (value) Position = ToolbarPosition.Bottom; }
    }

    // Left radio bound here — two-way mirror of Position.
    public bool IsLeft
    {
        get => Position == ToolbarPosition.Left;
        set { if (value) Position = ToolbarPosition.Left; }
    }

    // Right radio bound here — two-way mirror of Position.
    public bool IsRight
    {
        get => Position == ToolbarPosition.Right;
        set { if (value) Position = ToolbarPosition.Right; }
    }

    // Currently-selected row in the toolbar list — the Move / Remove / keybind
    // commands for the toolbar section act on this one.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveFromToolbarCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChangeToolbarKeybindCommand))]
    private ToolbarRowViewModel? _selectedRow;

    // Actions not currently on the toolbar, rendered as the lower "Shortcuts"
    // list. Each is still keybindable — an action can own a shortcut whether or
    // not it has a toolbar button. Promoting one (Add to toolbar) moves it up
    // into Rows; removing a toolbar button drops it back here.
    public ObservableCollection<ToolbarRowViewModel> ShortcutRows { get; } = new();

    // Currently-selected row in the Shortcuts list.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToToolbarCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChangeShortcutKeybindCommand))]
    private ToolbarRowViewModel? _selectedShortcutRow;

    // Inline result notice for the most recent "Copy from profile" run —
    // surfaced in the tab (never as a system toast). Lists keybinds that were
    // skipped because they clash with this profile's macros.
    [ObservableProperty] private string? _copyStatus;

    public ToolbarSectionViewModel(
        ProfileService profile,
        KeybindingStore keybindings,
        MacroStore macros,
        DialogService dialogs,
        SettingsService globalSettings,
        BbsProfileStore bbsStore)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(keybindings);
        ArgumentNullException.ThrowIfNull(macros);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(globalSettings);
        ArgumentNullException.ThrowIfNull(bbsStore);
        _profile        = profile;
        _keybindings    = keybindings;
        _macros         = macros;
        _dialogs        = dialogs;
        _globalSettings = globalSettings;
        _bbsStore       = bbsStore;
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        // Live-refresh the per-row shortcut hint when any built-in
        // binding moves — the catalogue's hardcoded ShortcutHint is a
        // seed, not the source of truth once the user starts rebinding.
        _keybindings.BindingChanged  += OnBindingChanged;
        _keybindings.BindingsReloaded += RefreshAllShortcutHints;
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= OnProfileClosedExternally;
            _keybindings.BindingChanged  -= OnBindingChanged;
            _keybindings.BindingsReloaded -= RefreshAllShortcutHints;
        });

        LoadFromProfile();
        LoadWebsites();
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
        // Help-menu website list + active BBS website are Global/BBS scoped —
        // persist them regardless of whether a character profile is loaded (the
        // toolbar/shortcuts layout below is character-scoped and only saves when
        // one is).
        SaveWebsites();

        if (_profile.Current is { } profile)
        {
            ToolbarSettings dto = new()
            {
                Layout   = Rows.Select(r => r.ToModel()).ToList(),
                Visible  = ShowToolbar,
                Position = Position,
            };

            profile.Settings ??= new();
            profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);

            ContextMenuSettings menuDto = new() { Layout = BuildContextMenuLayout() };
            profile.Settings[ContextMenuKey] = JsonSerializer.SerializeToElement(menuDto);

            _profile.Save();
            _profile.NotifyMutated();
        }

        ClearDirty();
    }

    public override void Discard()
    {
        _suppressDirty = true;
        LoadFromProfile();
        LoadWebsites();
        _suppressDirty = false;
        ClearDirty();
    }

    private void OnProfileChanged(CharacterProfile _) => ReloadAfterProfileSwap();
    private void OnProfileClosedExternally() => ReloadAfterProfileSwap();

    private void ReloadAfterProfileSwap()
    {
        _suppressDirty = true;
        LoadFromProfile();
        // A profile swap can change the active BBS, so re-resolve the BBS
        // website field + its label alongside the layout.
        LoadWebsites();
        _suppressDirty = false;
        ClearDirty();
        CopyStatus = null;
        OnPropertyChanged(nameof(HasProfile));
        CopyFromProfileCommand.NotifyCanExecuteChanged();
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
        ShowToolbar = dto.Visible;
        Position    = dto.Position;
        RefreshShortcutRows();
        LoadContextMenuFromProfile();
    }

    // ----- Terminal right-click menu: load / apply / edit -----------------
    private void LoadContextMenuFromProfile()
    {
        ContextMenuSettings dto = ReadContextMenuDtoOrDefault();
        PopulateContextMenuRows(dto.Layout is { Count: > 0 } layout ? layout : ContextMenuDefaults.Build());
        SelectedContextMenuRow = null;
    }

    // Replace the placed rows from a set of entries — FLATTENING folders (a
    // folder row followed by its children at Depth 1). Unknown ids are dropped so
    // the editor never shows a dead row. Shared by profile-load, reset, imports.
    private void PopulateContextMenuRows(IEnumerable<ContextMenuEntry> items)
    {
        foreach (ContextMenuRowViewModel row in ContextMenuRows) row.PropertyChanged -= OnContextMenuRowChanged;
        ContextMenuRows.Clear();
        foreach (ContextMenuEntry item in items)
        {
            if (item.Kind == ContextMenuEntryKind.Folder)
            {
                ContextMenuRows.Add(HookContextMenuRow(ContextMenuRowViewModel.Folder(item.Label)));
                if (item.Children is { } children)
                    foreach (ContextMenuEntry child in children)
                        if (FromModelOrNull(child, depth: 1) is { } crow) ContextMenuRows.Add(crow);
            }
            else if (FromModelOrNull(item) is { } row) ContextMenuRows.Add(row);
        }
        RefreshContextMenuPool();
    }

    // Rebuild the "Add an item" pool. Single-instance entries (the Favorites /
    // Recent walk fly-outs — you'd never want two) drop out once they're placed,
    // and come back if removed; everything else can be added any number of times.
    private void RefreshContextMenuPool()
    {
        var placed = new HashSet<string>(
            ContextMenuRows.Where(r => !r.IsSeparator && !r.IsFolder && r.Id is not null).Select(r => r.Id!),
            StringComparer.OrdinalIgnoreCase);
        string? selectedId = SelectedContextMenuPoolRow?.Id;
        ContextMenuPool.Clear();
        foreach (MenuActionCatalogue.Entry e in MenuActionCatalogue.AllEntries)
        {
            if (e.EntryKind == MenuActionCatalogue.Kind.WalkFlyout && placed.Contains(e.Id)) continue;
            ContextMenuPool.Add(new ContextMenuRowViewModel(e));
        }
        SelectedContextMenuPoolRow = selectedId is null
            ? null
            : ContextMenuPool.FirstOrDefault(r => string.Equals(r.Id, selectedId, StringComparison.OrdinalIgnoreCase));
    }

    // A placed row from a leaf entry (separator or catalogue entry + its optional
    // custom label). Folders are handled by the caller; unknown ids are dropped.
    private ContextMenuRowViewModel? FromModelOrNull(ContextMenuEntry item, int depth = 0)
    {
        if (item.Kind == ContextMenuEntryKind.Separator)
            return HookContextMenuRow(ContextMenuRowViewModel.Separator(depth));
        if (item.Kind == ContextMenuEntryKind.Folder) return null;   // caller handles folders
        if (MenuActionCatalogue.Find(item.Id) is not { } def) return null;
        return HookContextMenuRow(new ContextMenuRowViewModel(def, item.Label, depth));
    }

    // The flat editor rows → the nested persisted layout: a Depth-0 folder row
    // adopts the Depth-1 rows that follow it as its Children.
    private List<ContextMenuEntry> BuildContextMenuLayout()
    {
        List<ContextMenuEntry> result = new();
        ContextMenuEntry? currentFolder = null;
        foreach (ContextMenuRowViewModel row in ContextMenuRows)
        {
            ContextMenuEntry entry = row.ToModel();
            if (row.Depth == 0)
            {
                result.Add(entry);
                currentFolder = row.IsFolder ? entry : null;
            }
            else if (currentFolder?.Children is { } kids) kids.Add(entry);
            else result.Add(entry);   // orphaned child (shouldn't happen) — demote to top
        }
        return result;
    }

    private ContextMenuRowViewModel HookContextMenuRow(ContextMenuRowViewModel row)
    {
        row.PropertyChanged += OnContextMenuRowChanged;
        return row;
    }

    private void OnContextMenuRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContextMenuRowViewModel.CustomLabel)) Dirty();
    }

    private ContextMenuSettings ReadContextMenuDtoOrDefault()
        => _profile.Current is { } profile ? ReadContextMenuDtoFrom(profile) : new ContextMenuSettings();

    private static ContextMenuSettings ReadContextMenuDtoFrom(CharacterProfile profile)
    {
        if (profile.Settings is null) return new ContextMenuSettings();
        if (!profile.Settings.TryGetValue(ContextMenuKey, out JsonElement json)) return new ContextMenuSettings();
        try { return JsonSerializer.Deserialize<ContextMenuSettings>(json.GetRawText()) ?? new ContextMenuSettings(); }
        catch { return new ContextMenuSettings(); }
    }

    private ToolbarSettings ReadDtoOrDefault()
        => _profile.Current is { } profile ? ReadDtoFrom(profile) : new ToolbarSettings();

    // Pull the Toolbar settings DTO out of any profile, defaulting on absence /
    // malformed JSON.
    private static ToolbarSettings ReadDtoFrom(CharacterProfile profile)
    {
        if (profile.Settings is null) return new ToolbarSettings();
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

    // Rebuild the lower "Shortcuts" list: every catalogue action whose button
    // isn't currently placed on the toolbar, plus every keybind-only action that
    // has no catalogue entry at all (the File-menu actions). All of these stay
    // keybindable even with no toolbar button — that's the whole point of the
    // unified pool. Preserves the current selection by action id so a rebuild
    // (after add/remove) doesn't clear the highlight.
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
        // Keybind-only actions (no catalogue entry ⇒ no toolbar button, e.g. the
        // File-menu New/Open/Save/Save As/Quit) surface here so their chords are
        // still editable. Absorbs any future menu-only action automatically.
        foreach (BuiltInAction action in Enum.GetValues<BuiltInAction>())
        {
            string id = action.ToString();
            if (ToolbarItemCatalogue.Find(id) is not null) continue;
            ToolbarRowViewModel row = new(ToolbarItemKind.Button, id);
            row.RefreshShortcutHint(_keybindings.Get(action));
            ShortcutRows.Add(row);
        }
        if (keepSelected is not null)
            SelectedShortcutRow = ShortcutRows.FirstOrDefault(
                r => string.Equals(r.ActionId, keepSelected, StringComparison.OrdinalIgnoreCase));
    }

    // ----- Commands -----

    // Promote the selected Shortcuts-list action to a toolbar button.
    [RelayCommand(CanExecute = nameof(CanAddToToolbar))]
    private void AddToToolbar()
    {
        if (SelectedShortcutRow is not { IsToolbarEligible: true, ActionId: { } actionId }) return;
        ToolbarRowViewModel newRow = new(ToolbarItemKind.Button, actionId);
        if (newRow.BoundAction is BuiltInAction a)
            newRow.RefreshShortcutHint(_keybindings.Get(a));
        Rows.Add(newRow);
        SelectedRow = newRow;
        RefreshShortcutRows();   // the promoted action leaves the shortcuts list
        Dirty();
    }

    // Only catalogue-backed rows can be promoted; keybind-only rows (File-menu
    // actions) have no toolbar button, so the button stays disabled for them.
    private bool CanAddToToolbar() => SelectedShortcutRow?.IsToolbarEligible == true;

    // Append a separator to the toolbar (separators are toolbar-only).
    [RelayCommand]
    private void AddSeparator()
    {
        Rows.Add(new ToolbarRowViewModel(ToolbarItemKind.Separator, null));
        SelectedRow = Rows[^1];
        Dirty();
    }

    // Remove the selected toolbar row. A button drops back into the Shortcuts
    // list (still keybindable); a separator is just deleted. No confirm prompt —
    // the move is non-destructive and revertible via the section's
    // Cancel/Discard.
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

    // ----- Help-menu website editor ---------------------------------------

    // Hydrate WebsiteRows from the Global-tier list and the BBS-website field
    // from the active BBS. Runs at ctor, on Discard, and on profile swap.
    private void LoadWebsites()
    {
        HelpWebsitesSettings dto = ReadHelpWebsitesOrDefault();
        WebsiteRows.Clear();
        foreach (HelpWebsite link in dto.Links)
            WebsiteRows.Add(HelpWebsiteRowViewModel.FromModel(link, Dirty));

        BbsProfile? active = AppServices.Current.ResolveActiveBbs();
        ActiveBbsName = active?.Name;
        BbsWebsiteUrl = active?.WebsiteUrl;
        ShowBbsWebsiteInHelp = active?.ShowWebsiteInHelp ?? true;
    }

    // Read the Global-tier website list, falling back to the seeded defaults on
    // absence / malformed JSON so the editor always shows the reference links.
    private HelpWebsitesSettings ReadHelpWebsitesOrDefault()
    {
        if (_globalSettings.Current.Settings is { } bucket
            && bucket.TryGetValue("HelpWebsites", out JsonElement json))
        {
            try
            {
                return JsonSerializer.Deserialize<HelpWebsitesSettings>(json.GetRawText())
                       ?? new HelpWebsitesSettings();
            }
            catch
            {
                return new HelpWebsitesSettings();
            }
        }
        return new HelpWebsitesSettings();
    }

    // Persist the website list to the Global tier (dropping blank-URL rows) and
    // the active BBS's website via read-modify-write. Fires GlobalSettingsChanged
    // on save so the Help menu re-composes.
    private void SaveWebsites()
    {
        HelpWebsitesSettings dto = new()
        {
            Links = WebsiteRows
                .Select(r => r.ToModel())
                .Where(l => !string.IsNullOrWhiteSpace(l.Url))
                .ToList(),
        };
        _globalSettings.Current.Settings ??= new Dictionary<string, JsonElement>();
        _globalSettings.Current.Settings["HelpWebsites"] = JsonSerializer.SerializeToElement(dto);
        _globalSettings.Save();

        // Re-read the active BBS off disk so this write folds into whatever the
        // BBS section may have just saved (the BBS section likewise re-reads
        // WebsiteUrl before its own save — the two are order-independent).
        if (AppServices.Current.ResolveActiveBbs() is { } active)
        {
            active.WebsiteUrl = string.IsNullOrWhiteSpace(BbsWebsiteUrl) ? null : BbsWebsiteUrl.Trim();
            active.ShowWebsiteInHelp = ShowBbsWebsiteInHelp;
            _bbsStore.Save(active);
        }
    }

    [RelayCommand]
    private void AddWebsite()
    {
        WebsiteRows.Add(new HelpWebsiteRowViewModel(Dirty) { Label = "New link", Url = "https://" });
        Dirty();
    }

    // Move / remove act on the row whose inline button was clicked (passed as
    // the command parameter, per the navigation-window pattern) so the user
    // never has to select a row first. No-op at the list boundaries.
    [RelayCommand]
    private void RemoveWebsite(HelpWebsiteRowViewModel? row)
    {
        if (row is null) return;
        WebsiteRows.Remove(row);
        Dirty();
    }

    [RelayCommand]
    private void MoveWebsiteUp(HelpWebsiteRowViewModel? row)
    {
        if (row is null) return;
        int i = WebsiteRows.IndexOf(row);
        if (i <= 0) return;
        WebsiteRows.Move(i, i - 1);
        Dirty();
    }

    [RelayCommand]
    private void MoveWebsiteDown(HelpWebsiteRowViewModel? row)
    {
        if (row is null) return;
        int i = WebsiteRows.IndexOf(row);
        if (i < 0 || i >= WebsiteRows.Count - 1) return;
        WebsiteRows.Move(i, i + 1);
        Dirty();
    }

    [RelayCommand]
    private void ResetWebsitesToDefault()
    {
        WebsiteRows.Clear();
        foreach (HelpWebsite link in HelpWebsitesSettings.DefaultLinks())
            WebsiteRows.Add(HelpWebsiteRowViewModel.FromModel(link, Dirty));
        Dirty();
    }

    partial void OnBbsWebsiteUrlChanged(string? value) => Dirty();

    partial void OnShowBbsWebsiteInHelpChanged(bool value) => Dirty();

    // Open the keybind editor for row. The dialog allows a chord that already
    // belongs to another built-in action (it warns which one loses it); on save we
    // steal it — unbinding that owner first so no two actions hold the same chord.
    // Both rows refresh their displayed chord via the store's BindingChanged event
    // (the previous owner's row falls back to "unbound"). A macro / system-reserved
    // collision still blocks Save inside the dialog. No-ops for rows whose action
    // isn't rebindable (separators, or any non-BuiltInAction).
    private async Task ChangeKeybindForRowAsync(ToolbarRowViewModel? row)
    {
        if (row?.BoundAction is not BuiltInAction action) return;

        ViewModels.Keybind.KeybindEditDialogViewModel vm =
            new(action, _keybindings, _macros);
        KeyChord chord = await _dialogs
            .OpenWindowAsync<ViewModels.Keybind.KeybindEditDialogViewModel, KeyChord>(vm);
        if (chord.Equals(_keybindings.Get(action))) return;

        if (!chord.IsEmpty && _keybindings.FindAction(chord) is BuiltInAction victim && victim != action)
            _keybindings.Rebind(victim, KeyChord.Empty);
        _keybindings.Rebind(action, chord);
    }

    [RelayCommand(CanExecute = nameof(CanChangeToolbarKeybind))]
    private Task ChangeToolbarKeybind() => ChangeKeybindForRowAsync(SelectedRow);

    private bool CanChangeToolbarKeybind() => SelectedRow?.BoundAction is not null;

    [RelayCommand(CanExecute = nameof(CanChangeShortcutKeybind))]
    private Task ChangeShortcutKeybind() => ChangeKeybindForRowAsync(SelectedShortcutRow);

    private bool CanChangeShortcutKeybind() => SelectedShortcutRow?.BoundAction is not null;

    // Reset every built-in chord — across both the toolbar and shortcuts lists —
    // back to its default in one shot. Drives the "Reset Keybinds to Default"
    // button; per-row keybind resets fold into this single affordance (no
    // separate per-row reset button).
    [RelayCommand]
    private void ResetAllKeybinds() => _keybindings.ResetAllToDefaults();

    // Copy another character's toolbar layout + keybinds wholesale. Per the
    // staged-layout / live-keybind split: the source's layout loads into the
    // pending Rows (committed on Apply, reverted on Cancel), while its keybinds
    // apply immediately through KeybindingStore.Rebind — mirroring the per-row
    // Change keybind path. Chords that clash with one of THIS profile's macros
    // are skipped (a keybind can never share a chord with a macro) and reported
    // inline via CopyStatus.
    [RelayCommand(CanExecute = nameof(HasProfile))]
    private async Task CopyFromProfileAsync()
    {
        if (_profile.Current is null) return;

        List<ProfileRef> candidates = _profile.ListAll()
            .Where(r => !(string.Equals(r.Bbs,  _profile.CurrentBbsName,     StringComparison.OrdinalIgnoreCase)
                       && string.Equals(r.Name, _profile.CurrentProfileName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (candidates.Count == 0)
        {
            CopyStatus = "No other saved profiles to copy from.";
            return;
        }

        ViewModels.Profile.ProfilePickerDialogViewModel picker = new(
            candidates,
            windowTitle: "Copy toolbar + shortcuts",
            prompt: "Copy the toolbar layout and keybinds from another character. "
                  + "This replaces the current toolbar, shortcuts, and keybinds.",
            confirmLabel: "Copy");

        ProfileRef? picked = await _dialogs
            .OpenWindowAsync<ViewModels.Profile.ProfilePickerDialogViewModel, ProfileRef>(picker);
        if (picked is null) return;

        CharacterProfile? source;
        try
        {
            source = JsonStore.Load<CharacterProfile>(AppPaths.CharacterProfileFile(picked.Bbs, picked.Name));
        }
        catch (Exception ex)
        {
            CopyStatus = $"Couldn't read '{picked.Name}': {ex.Message}";
            return;
        }
        if (source is null)
        {
            CopyStatus = $"Couldn't read '{picked.Name}'.";
            return;
        }

        ApplyCopiedLayout(source);
        ApplyCopiedKeybinds(source, out List<string> skipped);

        CopyStatus = skipped.Count == 0
            ? $"Copied toolbar + shortcuts from {picked.Name}. Apply to keep the layout."
            : $"Copied from {picked.Name}. Skipped {skipped.Count} keybind(s) that clash with this "
              + $"profile's macros: {string.Join(", ", skipped)}. Apply to keep the layout.";
    }

    // Load the source profile's toolbar layout + orientation into the pending
    // Rows.
    private void ApplyCopiedLayout(CharacterProfile source)
    {
        ToolbarSettings dto = ReadDtoFrom(source);
        List<ToolbarItem> items = dto.Layout is { Count: > 0 } layout ? layout : ToolbarDefaults.Build();
        Rows.Clear();
        foreach (ToolbarItem item in items)
        {
            ToolbarRowViewModel row = new(item.Kind, item.ActionId);
            if (row.BoundAction is BuiltInAction a)
                row.RefreshShortcutHint(_keybindings.Get(a));
            Rows.Add(row);
        }
        // These setters route through Dirty() via the OnXChanged partials.
        ShowToolbar = dto.Visible;
        Position    = dto.Position;
        SelectedRow = null;
        RefreshShortcutRows();
        Dirty();
    }

    // Rebind every built-in action to the source profile's effective chord (its
    // sparse override, else the seed default). Skips — and reports — any chord
    // already owned by one of this profile's macros. Internal built-in conflicts
    // can't arise: the source set was already conflict-free when authored.
    private void ApplyCopiedKeybinds(CharacterProfile source, out List<string> skipped)
    {
        skipped = new();
        Dictionary<BuiltInAction, KeyChord> overrides = source.BuiltInKeybindings ?? new();

        HashSet<BuiltInAction> actions = new(KeybindingStore.DefaultBindings.Keys);
        foreach (BuiltInAction a in overrides.Keys) actions.Add(a);

        foreach (BuiltInAction action in actions)
        {
            KeyChord target = overrides.TryGetValue(action, out KeyChord ov)
                ? ov
                : KeybindingStore.DefaultBindings.TryGetValue(action, out KeyChord def) ? def : KeyChord.Empty;

            if (target.Equals(_keybindings.Get(action))) continue;

            if (!target.IsEmpty
                && _macros.FindMatch(target.Key.ToString(), target.Ctrl, target.Shift, target.Alt) is not null)
            {
                skipped.Add(KeybindingStore.ActionLabel(action));
                continue;
            }

            _keybindings.Rebind(action, target);
        }
    }

    // ----- Terminal right-click menu: rail commands + import / export -----
    private static Window? HostWindow =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } m } ? m : null;

    // Where a new leaf lands relative to the selection, and at what depth: as the
    // first child of a selected folder, a sibling inside the folder a selected
    // child belongs to, else after a selected top-level row (or the end).
    private int LeafInsertIndex(out int depth)
    {
        if (SelectedContextMenuRow is not { } sel) { depth = 0; return ContextMenuRows.Count; }
        int i = ContextMenuRows.IndexOf(sel);
        depth = sel.IsFolder || sel.Depth == 1 ? 1 : 0;
        return i + 1;
    }

    // Index just past the selected row's whole top-level block (a folder + its
    // children), for inserting another top-level row (folder / after a block).
    private int TopLevelInsertIndex()
    {
        if (SelectedContextMenuRow is not { } sel) return ContextMenuRows.Count;
        int i = ContextMenuRows.IndexOf(sel);
        while (i > 0 && ContextMenuRows[i].Depth == 1) i--;   // back to the block's top row
        int j = i + 1;
        while (j < ContextMenuRows.Count && ContextMenuRows[j].Depth == 1) j++;  // skip its children
        return j;
    }

    private bool CanAddContextMenuEntry() => SelectedContextMenuPoolRow is not null;
    [RelayCommand(CanExecute = nameof(CanAddContextMenuEntry))]
    private void AddContextMenuEntry()
    {
        if (SelectedContextMenuPoolRow is not { Id: { } id } || MenuActionCatalogue.Find(id) is not { } def) return;
        int at = LeafInsertIndex(out int depth);
        ContextMenuRowViewModel row = HookContextMenuRow(new ContextMenuRowViewModel(def, null, depth));
        ContextMenuRows.Insert(at, row);
        SelectedContextMenuRow = row;
        RefreshContextMenuPool();   // a placed single-instance entry (walk fly-out) leaves the pool
        Dirty();
    }

    [RelayCommand]
    private void AddContextMenuSeparator()
    {
        int at = LeafInsertIndex(out int depth);
        ContextMenuRowViewModel row = HookContextMenuRow(ContextMenuRowViewModel.Separator(depth));
        ContextMenuRows.Insert(at, row);
        SelectedContextMenuRow = row;
        Dirty();
    }

    // A user-defined folder (top level only) — a named fly-out submenu. Add items
    // to it by selecting it and adding from the pool.
    [RelayCommand]
    private void AddContextMenuFolder()
    {
        int at = TopLevelInsertIndex();
        ContextMenuRowViewModel row = HookContextMenuRow(ContextMenuRowViewModel.Folder("New folder"));
        ContextMenuRows.Insert(at, row);
        SelectedContextMenuRow = row;
        Dirty();
    }

    private bool CanRemoveContextMenuEntry() => SelectedContextMenuRow is not null;
    [RelayCommand(CanExecute = nameof(CanRemoveContextMenuEntry))]
    private void RemoveContextMenuEntry()
    {
        if (SelectedContextMenuRow is not { } row) return;
        int idx = ContextMenuRows.IndexOf(row);
        // Removing a folder takes its contiguous Depth-1 children with it.
        int end = idx + 1;
        if (row.IsFolder)
            while (end < ContextMenuRows.Count && ContextMenuRows[end].Depth == 1) end++;
        for (int k = end - 1; k >= idx; k--)
        {
            ContextMenuRows[k].PropertyChanged -= OnContextMenuRowChanged;
            ContextMenuRows.RemoveAt(k);
        }
        SelectedContextMenuRow = ContextMenuRows.Count == 0
            ? null
            : ContextMenuRows[System.Math.Min(idx, ContextMenuRows.Count - 1)];
        RefreshContextMenuPool();   // a removed single-instance entry returns to the pool
        Dirty();
    }

    // Move rows [start..end) so they begin at dest (an index in the pre-removal
    // list). Used for block-aware folder moves.
    private void MoveContextMenuBlock(int start, int end, int dest)
    {
        List<ContextMenuRowViewModel> block = new();
        for (int k = start; k < end; k++) block.Add(ContextMenuRows[k]);
        for (int k = end - 1; k >= start; k--) ContextMenuRows.RemoveAt(k);
        int insertAt = dest > start ? dest - block.Count : dest;
        for (int k = 0; k < block.Count; k++) ContextMenuRows.Insert(insertAt + k, block[k]);
    }

    // Remove `row` and re-insert it at `insertAt` (an index in the list AFTER the
    // removal) with `depth`, keeping it selected. Used when a Move up/down steps
    // an item into or out of a folder (its depth changes).
    private void ReinsertContextMenuRow(ContextMenuRowViewModel row, int insertAt, int depth)
    {
        ContextMenuRows.Remove(row);
        row.Depth = depth;
        ContextMenuRows.Insert(insertAt, row);
        SelectedContextMenuRow = row;
        // Depth changed under the same selection, so the Add-button label
        // (which reads the selected row's depth) must re-evaluate.
        OnPropertyChanged(nameof(AddContextMenuButtonText));
        Dirty();
    }

    private void SwapContextMenuRows(int a, int b, ContextMenuRowViewModel keepSelected)
    {
        ContextMenuRows.Move(a, b);
        SelectedContextMenuRow = keepSelected;
        Dirty();
    }

    private bool CanMoveContextMenuUp() => SelectedContextMenuRow is not null;
    [RelayCommand(CanExecute = nameof(CanMoveContextMenuUp))]
    private void MoveContextMenuUp()
    {
        if (SelectedContextMenuRow is not { } row) return;
        int i = ContextMenuRows.IndexOf(row);
        if (i <= 0) return;

        // A folder moves as a whole block, swapping with the previous top-level block.
        if (row.IsFolder)
        {
            int end = i + 1;
            while (end < ContextMenuRows.Count && ContextMenuRows[end].Depth == 1) end++;
            int p = i - 1;
            while (p > 0 && ContextMenuRows[p].Depth == 1) p--;
            MoveContextMenuBlock(i, end, p);
            SelectedContextMenuRow = row;
            Dirty();
            return;
        }

        ContextMenuRowViewModel above = ContextMenuRows[i - 1];
        if (row.Depth == 1)
        {
            // Inside a folder: reorder among siblings, or if it's the first child,
            // pop it OUT above the folder.
            if (above.Depth == 1) SwapContextMenuRows(i, i - 1, row);
            else ReinsertContextMenuRow(row, i - 1, 0);   // above the folder header
            return;
        }

        // Top-level item: swapping with a folder (its header or last child) steps
        // it INTO that folder as the last child; otherwise a plain swap up.
        if (above.IsFolder || above.Depth == 1) ReinsertContextMenuRow(row, i, 1);
        else SwapContextMenuRows(i, i - 1, row);
    }

    private bool CanMoveContextMenuDown() => SelectedContextMenuRow is not null;
    [RelayCommand(CanExecute = nameof(CanMoveContextMenuDown))]
    private void MoveContextMenuDown()
    {
        if (SelectedContextMenuRow is not { } row) return;
        int i = ContextMenuRows.IndexOf(row);
        if (i < 0) return;

        if (row.IsFolder)
        {
            int end = i + 1;
            while (end < ContextMenuRows.Count && ContextMenuRows[end].Depth == 1) end++;
            if (end >= ContextMenuRows.Count) return;
            int next = end + 1;
            while (next < ContextMenuRows.Count && ContextMenuRows[next].Depth == 1) next++;
            MoveContextMenuBlock(end, next, i);
            SelectedContextMenuRow = row;
            Dirty();
            return;
        }

        int j = i + 1;
        if (row.Depth == 1)
        {
            // Inside a folder: reorder among siblings, or if it's the last child,
            // pop it OUT below the folder.
            if (j < ContextMenuRows.Count && ContextMenuRows[j].Depth == 1) SwapContextMenuRows(i, j, row);
            else ReinsertContextMenuRow(row, i, 0);   // below the folder block
            return;
        }

        // Top-level item: swapping with a folder header steps it INTO that folder
        // as the first child; otherwise a plain swap down.
        if (j >= ContextMenuRows.Count) return;
        if (ContextMenuRows[j].IsFolder) ReinsertContextMenuRow(row, i + 1, 1);
        else SwapContextMenuRows(i, j, row);
    }

    [RelayCommand]
    private void ResetContextMenu()
    {
        PopulateContextMenuRows(ContextMenuDefaults.Build());
        SelectedContextMenuRow = null;
        ContextMenuStatus = null;
        Dirty();
    }

    // Import the terminal right-click menu from another character profile (staged
    // into the rows, committed on Apply). Reuses the profile picker.
    [RelayCommand(CanExecute = nameof(HasProfile))]
    private async Task ImportContextMenuFromProfileAsync()
    {
        if (_profile.Current is null) return;
        List<ProfileRef> candidates = _profile.ListAll()
            .Where(r => !(string.Equals(r.Bbs, _profile.CurrentBbsName, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(r.Name, _profile.CurrentProfileName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (candidates.Count == 0) { ContextMenuStatus = "No other saved profiles to import from."; return; }

        ViewModels.Profile.ProfilePickerDialogViewModel picker = new(
            candidates,
            windowTitle: "Import right-click menu",
            prompt: "Copy the terminal right-click menu from another character. This replaces the current menu — Apply to keep it.",
            confirmLabel: "Import");
        ProfileRef? picked = await _dialogs
            .OpenWindowAsync<ViewModels.Profile.ProfilePickerDialogViewModel, ProfileRef>(picker);
        if (picked is null) return;

        CharacterProfile? source;
        try { source = JsonStore.Load<CharacterProfile>(AppPaths.CharacterProfileFile(picked.Bbs, picked.Name)); }
        catch (Exception ex) { ContextMenuStatus = $"Couldn't read '{picked.Name}': {ex.Message}"; return; }
        if (source is null) { ContextMenuStatus = $"Couldn't read '{picked.Name}'."; return; }

        PopulateContextMenuRows(ReadContextMenuDtoFrom(source).Layout is { Count: > 0 } l ? l : ContextMenuDefaults.Build());
        SelectedContextMenuRow = null;
        Dirty();
        ContextMenuStatus = $"Imported the right-click menu from {picked.Name}. Apply to keep it.";
    }

    // Export the current (staged) menu to a JSON file for sharing with friends.
    [RelayCommand]
    private async Task ExportContextMenuFileAsync()
    {
        if (HostWindow is not { } main) return;
        IStorageFile? file = await main.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export right-click menu",
            SuggestedFileName = "terminal-menu.json",
            DefaultExtension = "json",
            FileTypeChoices = new[] { new FilePickerFileType("Right-click menu (JSON)") { Patterns = new[] { "*.json" } } },
        });
        if (file is null) return;
        ContextMenuSettings dto = new() { Layout = BuildContextMenuLayout() };
        JsonStore.Save(file.Path.LocalPath, dto);
        ContextMenuStatus = "Exported the current right-click menu.";
    }

    // Import a shared menu file into the staged rows (committed on Apply).
    [RelayCommand]
    private async Task ImportContextMenuFileAsync()
    {
        if (HostWindow is not { } main) return;
        IReadOnlyList<IStorageFile> picked = await main.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import right-click menu",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Right-click menu (JSON)") { Patterns = new[] { "*.json" } },
                FilePickerFileTypes.All,
            },
        });
        if (picked.Count == 0) return;
        ContextMenuSettings? dto;
        try { dto = JsonStore.Load<ContextMenuSettings>(picked[0].Path.LocalPath); }
        catch (Exception ex) { ContextMenuStatus = $"Couldn't read that file: {ex.Message}"; return; }
        if (dto is null) { ContextMenuStatus = "That file didn't contain a right-click menu."; return; }

        PopulateContextMenuRows(dto.Layout is { Count: > 0 } l ? l : ContextMenuDefaults.Build());
        SelectedContextMenuRow = null;
        Dirty();
        ContextMenuStatus = "Imported a right-click menu from file. Apply to keep it.";
    }

    // Auto-generated PropertyChanged hooks for the visibility / position
    // observables — re-route into the shared Dirty() helper so the Apply
    // button lights up on either knob.
    partial void OnShowToolbarChanged(bool value)         => Dirty();
    partial void OnPositionChanged(ToolbarPosition value) => Dirty();

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

// One row in ToolbarSectionViewModel.Rows. Exposes the catalogue-resolved
// label + icon resource key + shortcut so the editor row template can render
// [icon] [label] [(shortcut)] without doing the lookup in XAML.
public sealed partial class ToolbarRowViewModel : ObservableObject
{
    public ToolbarItemKind Kind { get; }
    public string? ActionId { get; }

    // The BuiltInAction this row binds to, when the action id parses as one of
    // the enum members. Only separators and unrecognised action ids return null;
    // the editor uses this to gate the Change / Reset keybind commands.
    public BuiltInAction? BoundAction { get; }

    // True only for rows backed by a ToolbarItemCatalogue entry — i.e. actions
    // that can carry a toolbar button. Keybind-only rows (File-menu actions with
    // no catalogue entry) are false, so "Add to toolbar" can't promote them.
    public bool IsToolbarEligible { get; }

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

        if (actionId is not null
            && Enum.TryParse(actionId, ignoreCase: false, out BuiltInAction parsed))
        {
            BoundAction = parsed;
        }

        ToolbarItemCatalogue.Entry? entry = ToolbarItemCatalogue.Find(actionId);
        IsToolbarEligible = entry is not null;
        if (entry is { } catalogued)
        {
            DisplayLabel = catalogued.Label;
            IconResourceKey = catalogued.IconResourceKey;
            // Catalogue ShortcutHint is the seed display; the section VM
            // replaces it with the live KeybindingStore label once it
            // resolves the row's BuiltInAction.
            ShortcutHint = catalogued.ShortcutHint is { } s ? $"({s})" : null;
        }
        else if (BoundAction is { } keybindOnly)
        {
            // Keybind-only action (File-menu New/Open/Save/Save As/Quit): no
            // catalogue entry, so no toolbar button and no icon, but it's still
            // rebindable from the Shortcuts list.
            DisplayLabel = KeybindingStore.ActionLabel(keybindOnly);
            IconResourceKey = null;
        }
        else
        {
            DisplayLabel = $"(unknown action: {actionId})";
            IconResourceKey = null;
        }
    }

    // Push the live keybind label into the row (or clear when unbound).
    public void RefreshShortcutHint(KeyChord chord)
        => ShortcutHint = chord.IsEmpty ? null : $"({chord.Label})";

    public ToolbarItem ToModel() => new() { Kind = Kind, ActionId = ActionId };
}

// One row in ToolbarSectionViewModel.WebsiteRows — an editable Help-menu website
// (label + URL). Both cells two-way bind in the editor; any edit dirties the
// parent section via the onDirty callback.
public sealed partial class HelpWebsiteRowViewModel : ObservableObject
{
    private readonly Action _onDirty;

    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private string _url = string.Empty;

    public HelpWebsiteRowViewModel(Action onDirty)
    {
        ArgumentNullException.ThrowIfNull(onDirty);
        _onDirty = onDirty;
    }

    public HelpWebsite ToModel() => new() { Label = Label.Trim(), Url = Url.Trim() };

    public static HelpWebsiteRowViewModel FromModel(HelpWebsite model, Action onDirty)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new HelpWebsiteRowViewModel(onDirty) { Label = model.Label, Url = model.Url };
    }

    partial void OnLabelChanged(string value) => _onDirty();
    partial void OnUrlChanged(string value) => _onDirty();
}
