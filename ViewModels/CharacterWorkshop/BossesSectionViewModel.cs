using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Views.CharacterWorkshop;

namespace MudPlay.ViewModels.CharacterWorkshop;

// BOSSES section — the per-realm boss list from BossStore, with respawn timers
// resolved from game data (or a per-boss override). The table is read-mostly:
// StopBefore toggles inline and per-row Mark / Reset / Clear drive the timer;
// adding / editing / removing bosses (and respawn overrides) happens in the Manage
// Bosses dialog. A filter narrows the grid by boss name or room; Import / Export
// share the realm's table as a JSON file. Live countdowns refresh on the heartbeat.
public sealed partial class BossesSectionViewModel : WorkshopSectionViewModel
{
    private readonly BossStore _bosses;
    private readonly BossTimerStore _timers;
    private readonly GameDataCache _gameData;
    private readonly TickEngine _tick;
    private readonly ObservableCollection<BossRowViewModel> _allRows = new();
    private Control? _view;
    private bool _suppress;

    public override string Id => "bosses";
    public override string Title => "Bosses";
    public override Control View => _view ??= new BossesSectionView { DataContext = this };

    // The grid binds this filtered view; heartbeat refresh + persist iterate the
    // backing collection so a live filter never hides a row from either.
    public DataGridCollectionView Rows { get; }

    [ObservableProperty] private BossRowViewModel? _selectedRow;
    [ObservableProperty] private bool _isParadigmRealm;
    [ObservableProperty] private bool _hasBosses;
    [ObservableProperty] private string _activeSummary = string.Empty;
    [ObservableProperty] private string _filterText = string.Empty;

    public BossesSectionViewModel(GameDataCache gameData, BossStore bosses, BossTimerStore timers, TickEngine tick)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(bosses);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(tick);
        _gameData = gameData;
        _bosses = bosses;
        _timers = timers;
        _tick = tick;
        Rows = new DataGridCollectionView(_allRows);
        _gameData.ActiveSetChanged += OnActiveSetChanged;
        _timers.Changed += OnTimersChanged;
        _tick.HeartbeatElapsed += OnHeartbeat;
        Rebuild();
    }

    public override void Dispose()
    {
        _gameData.ActiveSetChanged -= OnActiveSetChanged;
        _timers.Changed -= OnTimersChanged;
        _tick.HeartbeatElapsed -= OnHeartbeat;
    }

    private void OnActiveSetChanged(string? _) => Rebuild();

    private void OnTimersChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshStatuses();
        else Dispatcher.UIThread.Post(RefreshStatuses);
    }

    private void OnHeartbeat() => RefreshStatuses();

    private void RefreshStatuses()
    {
        foreach (BossRowViewModel row in _allRows) row.RefreshStatus();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int active = _timers.ActiveTimers(_gameData.ActiveRealm).Count;
        ActiveSummary = active == 0 ? "No boss timers active" : $"{active} boss timer{(active == 1 ? "" : "s")} active";
    }

    // Filter the grid by boss name OR room substring (case-insensitive); empty clears.
    partial void OnFilterTextChanged(string value)
    {
        string q = value.Trim();
        Rows.Filter = q.Length == 0
            ? null
            : o => o is BossRowViewModel r
                   && (r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                       || r.Rooms.Contains(q, StringComparison.OrdinalIgnoreCase)
                       || r.RespawnDisplay.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void Rebuild()
    {
        _suppress = true;
        RealmType realm = _gameData.ActiveRealm;
        IsParadigmRealm = realm == RealmType.ParaMud;
        _allRows.Clear();
        foreach (BossDef def in _bosses.ResolveForRealm(realm)
                     .Where(b => b.ShowInTable)
                     .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
        {
            int? hrs = def.RespawnType == BossRespawnType.Timed
                ? BossCatalog.EffectiveRegenHours(_gameData, def)
                : null;
            // Grab-All only applies when the name resolves to a specific monster or
            // item; otherwise the row hides the checkbox (shows a "cannot resolve" tip).
            bool canGrabAll = BossGrabClassifier.Classify(_gameData, def) != Game.Inventory.BossGrabKind.None;
            _allRows.Add(new BossRowViewModel(def, realm, hrs, _timers, OnRowEdited, OnMarkRequested, canGrabAll));
        }
        HasBosses = _allRows.Count > 0;
        _suppress = false;
        UpdateSummary();
    }

    // StopBefore toggled inline → persist the whole list as an overlay delta.
    private void OnRowEdited()
    {
        if (_suppress) return;
        Persist();
    }

    // Persist the visible rows over the full resolved list. This path only carries a
    // StopBefore toggle — add / edit / remove and the ShowInTable flag live in the
    // Manage dialog — so every boss NOT represented by a visible row (the other realm,
    // or one hidden via ShowInTable) is carried through untouched.
    private void Persist()
    {
        var merged = new List<BossDef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BossRowViewModel row in _allRows)
        {
            BossDef d = row.ToDef();
            if (seen.Add(d.Name)) merged.Add(d);
        }
        foreach (BossDef b in _bosses.Resolve())
            if (seen.Add(b.Name)) merged.Add(b);
        _bosses.Save(merged);
    }

    // Row's Mark button — open the modeless mark-time dialog (defaults to now,
    // user-editable) and stamp the chosen time on commit.
    private async void OnMarkRequested(BossRowViewModel row)
    {
        DateTimeOffset? at = await AppServices.Current.Dialogs
            .OpenWindowAsync<MarkTimerDialogViewModel, DateTimeOffset?>(
                new MarkTimerDialogViewModel(row.Name, DateTimeOffset.Now));
        if (at is { } when)
        {
            _timers.MarkKilled(row.Name.Trim().ToLowerInvariant(), when);
            RefreshStatuses();
        }
    }

    [RelayCommand]
    private async Task ManageBosses()
    {
        bool saved = await AppServices.Current.Dialogs
            .OpenWindowAsync<ManageBossesDialogViewModel, bool>(
                new ManageBossesDialogViewModel(_bosses, _gameData));
        if (saved) Rebuild();
    }

    // Open the @timer sync merge window: request timers from other clients over chat
    // and fold chosen ones into the table. Modeless — responses trickle in live.
    [RelayCommand]
    private async Task SyncTimers()
    {
        bool applied = await AppServices.Current.Dialogs
            .OpenWindowAsync<BossTimerSyncViewModel, bool>(
                new BossTimerSyncViewModel(
                    _bosses, _timers, _gameData, AppServices.Current.Chat, AppServices.Current.SendTypedInput));
        if (applied) RefreshStatuses();
    }

    // Open the Chest Offload helper: open the containers a boss dropped, then sell
    // the loot shop-by-shop. Modeless — the terminal stays live so you can walk.
    [RelayCommand]
    private async Task OpenChestOffload()
        => await AppServices.Current.Dialogs
            .OpenWindowAsync<ChestOffloadViewModel, bool>(new ChestOffloadViewModel());

    // Export the active realm's boss table (names, rooms, flags, respawn overrides)
    // to a JSON file the user can share.
    [RelayCommand]
    private async Task Export()
    {
        if (MainWindow is not { } main) return;
        RealmType realm = _gameData.ActiveRealm;
        IStorageFile? file = await main.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export boss table",
            SuggestedFileName = $"bosses-{(realm == RealmType.ParaMud ? "paramud" : "stock")}.json",
            DefaultExtension = "json",
            FileTypeChoices = new[] { new FilePickerFileType("Boss table (JSON)") { Patterns = new[] { "*.json" } } },
        });
        if (file is null) return;
        List<BossDef> table = _bosses.ResolveForRealm(realm).ToList();
        JsonStore.Save(file.Path.LocalPath, table);
    }

    // Import a shared boss table: imported entries overlay the current catalog by
    // name (imported wins), anything not mentioned is preserved. Non-destructive.
    [RelayCommand]
    private async Task Import()
    {
        if (MainWindow is not { } main) return;
        IReadOnlyList<IStorageFile> picked = await main.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import boss table",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Boss table (JSON)") { Patterns = new[] { "*.json" } },
                FilePickerFileTypes.All,
            },
        });
        if (picked.Count == 0) return;
        if (JsonStore.Load<List<BossDef>>(picked[0].Path.LocalPath) is not { } imported) return;

        var byName = new Dictionary<string, BossDef>(StringComparer.OrdinalIgnoreCase);
        foreach (BossDef b in imported)
            if (!string.IsNullOrWhiteSpace(b.Name)) byName[b.Name.Trim().ToLowerInvariant()] = b;

        var merged = new List<BossDef>(byName.Values);
        foreach (BossDef b in _bosses.Resolve())
            if (!byName.ContainsKey(b.Name)) merged.Add(b);

        _bosses.Save(merged);
        Rebuild();
    }

    private static Window? MainWindow =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } m }
            ? m : null;
}
