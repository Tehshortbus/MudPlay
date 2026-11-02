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
using MudPlay.Game.Map;
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

    public const string SectionId = "bosses";
    public const string SectionTitle = "Bosses";
    public override string Id => SectionId;
    public override string Title => SectionTitle;
    public override Control View => _view ??= new BossesSectionView { DataContext = this };

    // The grid binds this filtered view; heartbeat refresh + persist iterate the
    // backing collection so a live filter never hides a row from either.
    public DataGridCollectionView Rows { get; }

    // A timer change arrived while a cell was being edited; the re-sort waits for it.
    private bool _resortPending;

    [ObservableProperty] private BossRowViewModel? _selectedRow;
    [ObservableProperty] private bool _isParadigmRealm;
    [ObservableProperty] private bool _hasBosses;
    [ObservableProperty] private string _activeSummary = string.Empty;
    [ObservableProperty] private string _filterText = string.Empty;

    // What it takes to hurt a boss: the hit magic a weapon needs, between a lowest
    // and a highest, and the spell immunity, at a level or lower. Each lists the
    // levels the bosses in the table have (BossReachFilter).
    public ObservableCollection<string> HitMagicOptions { get; } = new() { BossReachFilter.Any };
    public ObservableCollection<string> SpellImmunityOptions { get; } = new() { BossReachFilter.Any };
    [ObservableProperty] private string? _selectedHitMagicMin = BossReachFilter.Any;
    [ObservableProperty] private string? _selectedHitMagicMax = BossReachFilter.Any;
    [ObservableProperty] private string? _selectedSpellImmunity = BossReachFilter.Any;

    // What it takes to hurt a boss (hit magic, spell level). Unbound: nothing is
    // known, so every boss reads as hurt by anything.
    private readonly Func<BossDef, (int HitMagic, int SpellLevel)>? _reach;

    private readonly ProfileService? _profile;

    // The column widths the user dragged the table to, by column key.
    public IReadOnlyDictionary<string, double> ColumnWidths =>
        _profile?.Current?.BossColumnWidths ?? (IReadOnlyDictionary<string, double>)new Dictionary<string, double>();

    // Remember dragged column widths on the character. The view calls this as it
    // goes away, with only the columns whose width differs from what it showed.
    public void SaveColumnWidths(IReadOnlyDictionary<string, double> changed)
    {
        if (changed.Count == 0 || _profile?.Current is not { } current) return;
        Dictionary<string, double> widths = current.BossColumnWidths is { } had ? new(had) : new();
        foreach ((string key, double width) in changed) widths[key] = width;
        current.BossColumnWidths = widths;
        _profile.Save();
    }

    public BossesSectionViewModel(GameDataCache gameData, BossStore bosses, BossTimerStore timers, TickEngine tick,
        ProfileService? profile = null, Func<BossDef, (int HitMagic, int SpellLevel)>? reach = null)
    {
        _profile = profile;
        _reach = reach;
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
        if (Dispatcher.UIThread.CheckAccess()) RefreshAndResort();
        else Dispatcher.UIThread.Post(RefreshAndResort);
    }

    private void OnHeartbeat()
    {
        RefreshStatuses();
        if (_resortPending) Resort();
    }

    // A logged / changed timer flips a row's active-vs-idle status and its sort key.
    // The grid's sort is applied when the tab opens (BossesSectionView.ApplyDefaultSort)
    // and does NOT re-run on a row property change, so a boss that just went active
    // would sit wherever it was alphabetically until the tab is reopened. Re-apply the
    // sort here so it floats up into the active group live. Only on a timer change —
    // NOT on every heartbeat, which would re-shuffle (and reset scroll/selection) each
    // second even though the active set hasn't changed.
    private void RefreshAndResort()
    {
        RefreshStatuses();
        Resort();
    }

    // Re-sort now, or — while a grid cell is being edited, when Avalonia refuses a
    // Refresh (the crash in report Crash-20260929-045332, a room display stamping a
    // boss kill mid-edit) — on the first heartbeat after the edit ends.
    private void Resort()
    {
        if (Rows.IsEditingItem || Rows.IsAddingNew)
        {
            _resortPending = true;
            return;
        }
        _resortPending = false;
        Rows.Refresh();
    }

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

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnSelectedHitMagicMinChanged(string? value) => ApplyFilter();
    partial void OnSelectedHitMagicMaxChanged(string? value) => ApplyFilter();
    partial void OnSelectedSpellImmunityChanged(string? value) => ApplyFilter();

    // Filter the grid by boss name OR room substring (case-insensitive), and by the
    // two reach dropdowns; with none of them set the filter is off.
    private void ApplyFilter()
    {
        string q = FilterText.Trim();
        string? magicMin = SelectedHitMagicMin, magicMax = SelectedHitMagicMax, immunity = SelectedSpellImmunity;
        bool reach = BossReachFilter.IsChoice(magicMin) || BossReachFilter.IsChoice(magicMax)
            || BossReachFilter.IsChoice(immunity);
        Rows.Filter = q.Length == 0 && !reach
            ? null
            : o => o is BossRowViewModel r
                   && (q.Length == 0
                       || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                       || r.Rooms.Contains(q, StringComparison.OrdinalIgnoreCase)
                       || r.RespawnDisplay.Contains(q, StringComparison.OrdinalIgnoreCase))
                   && BossReachFilter.AtLeast(magicMin, r.HitMagicNeeded)
                   && BossReachFilter.AtMost(magicMax, r.HitMagicNeeded)
                   && BossReachFilter.AtMost(immunity, r.SpellLevelNeeded);
    }

    // Re-list a dropdown's levels for the bosses now in the table, keeping the
    // choice when that level is still asked for. In place, entry by entry: clearing
    // a list a ComboBox is bound to blanks its selection.
    private static void RelistReach(ObservableCollection<string> options, IReadOnlyList<string> wanted)
    {
        for (int i = options.Count - 1; i >= 0; i--)
            if (!wanted.Contains(options[i])) options.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
            if (i >= options.Count || options[i] != wanted[i]) options.Insert(i, wanted[i]);
    }

    private static string StillListed(ObservableCollection<string> options, string? chosen) =>
        chosen is not null && options.Contains(chosen) ? chosen : BossReachFilter.Any;

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
            (int hitMagic, int spellLevel) = _reach?.Invoke(def) ?? default;
            _allRows.Add(new BossRowViewModel(def, realm, hrs, _timers, OnRowEdited, OnMarkRequested, canGrabAll)
            {
                HitMagicNeeded = hitMagic,
                SpellLevelNeeded = spellLevel,
            });
        }
        HasBosses = _allRows.Count > 0;
        // The lists read off the bosses now in the table; a choice whose level is
        // gone falls back to (Any).
        string? magicMin = SelectedHitMagicMin, magicMax = SelectedHitMagicMax, immunity = SelectedSpellImmunity;
        RelistReach(HitMagicOptions, BossReachFilter.Options(_allRows.Select(r => r.HitMagicNeeded)));
        RelistReach(SpellImmunityOptions, BossReachFilter.Options(_allRows.Select(r => r.SpellLevelNeeded)));
        SelectedHitMagicMin = StillListed(HitMagicOptions, magicMin);
        SelectedHitMagicMax = StillListed(HitMagicOptions, magicMax);
        SelectedSpellImmunity = StillListed(SpellImmunityOptions, immunity);
        _suppress = false;
        UpdateSummary();
    }

    // StopBefore toggled inline → persist the whole list as an overlay delta.
    private void OnRowEdited()
    {
        if (_suppress) return;
        Persist();
    }

    // The toolbar's bulk toggles act on the rows the filter is showing. All ticked →
    // untick them all; otherwise tick them all. One save for the lot.
    [RelayCommand]
    private void ToggleAllStopBefore() =>
        ToggleAll(_ => true, r => r.StopBefore, (r, on) => r.StopBefore = on);

    // Grab All only exists on a row whose boss resolves to a monster or an item.
    [RelayCommand]
    private void ToggleAllGrabAll() =>
        ToggleAll(r => r.CanGrabAll, r => r.GrabAll, (r, on) => r.GrabAll = on);

    // Put Stop before and Grab All back to each boss's own defaults (set in the Manage
    // dialog) for every boss in the table, filter or not.
    [RelayCommand]
    private void ResetToDefaults()
    {
        _suppress = true;
        foreach (BossRowViewModel row in _allRows)
        {
            row.StopBefore = row.DefaultStopBefore;
            if (row.CanGrabAll) row.GrabAll = row.DefaultGrabAll;
        }
        _suppress = false;
        Persist();
    }

    private void ToggleAll(Func<BossRowViewModel, bool> applies,
        Func<BossRowViewModel, bool> isOn, Action<BossRowViewModel, bool> set)
    {
        List<BossRowViewModel> rows = Rows.OfType<BossRowViewModel>().Where(applies).ToList();
        if (rows.Count == 0) return;
        bool turnOn = !rows.All(isOn);
        _suppress = true;
        foreach (BossRowViewModel row in rows) set(row, turnOn);
        _suppress = false;
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

    // Double-click a boss row → walk to its room. One room walks there immediately
    // (arm + start, like the Roomba room Goto button). Several rooms open the picker,
    // sorted nearest→furthest, where Run starts the walk and Load only arms it (the
    // GOTO arm-without-start path). A boss with no walkable room recorded is a no-op.
    [RelayCommand]
    private async Task GotoBoss(BossRowViewModel? row)
    {
        if (row is null) return;
        IReadOnlyList<RoomKey> rooms = row.RoomKeys;
        if (rooms.Count == 0) return;
        if (rooms.Count == 1)
        {
            AppServices.Current.GoWalkTo(rooms[0]);
            return;
        }

        RoomKey? current = AppServices.Current.RoomTracker.State.CurrentRoom?.Key;
        BfsMapper bfs = AppServices.Current.Bfs;
        MovementFilter movement = AppServices.Current.Movement;
        RoomGraphManager graph = AppServices.Current.RoomGraph;
        IReadOnlyList<BossRoomOption> options = BossGotoRooms.BuildOptions(
            rooms,
            key => current is { } c ? bfs.DistanceBetween(c, key, movement) : null,
            key => graph.GetRoom(key)?.DisplayName);

        GameDataCache.Load listedOn = AppServices.Current.GameData.ActiveLoad;
        BossRoomPick? pick = await AppServices.Current.Dialogs
            .OpenWindowAsync<BossRoomPickerDialogViewModel, BossRoomPick?>(
                new BossRoomPickerDialogViewModel(row.Name, options));
        if (pick is null) return;
        // The picker stays open through a change of game data, and its rooms are
        // numbers of the data it was opened on.
        GameDataLoadChange change = GameDataLoadChange.Between(listedOn, AppServices.Current.GameData.ActiveLoad);
        if (change.Changed)
        {
            AppServices.Current.Log.Info("Bosses",
                $"{row.Name}: walk to {pick.Room} not started: {change.Why} while the room picker was open");
            AppServices.Current.WriteTerminalNotice(
                $"[Walk to {pick.Room} not started: {change.Why} while the boss room picker was open]");
            return;
        }
        if (pick.StartNow) AppServices.Current.GoWalkTo(pick.Room);
        else AppServices.Current.QueueWalkTo(pick.Room);
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
        string path = picked[0].Path.LocalPath;
        List<BossDef>? imported;
        try { imported = JsonStore.Load<List<BossDef>>(path); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            AppServices.Current.Log.Warn("Bosses", $"boss table not imported: {ex.Message}");
            AppServices.Current.WriteTerminalNotice($"[Boss table not imported: '{Path.GetFileName(path)}' isn't a boss table that can be read]");
            return;
        }
        if (imported is null) return;

        var byName = new Dictionary<string, BossDef>(StringComparer.OrdinalIgnoreCase);
        foreach (BossDef? b in imported)
            if (b?.Name is { } name && !string.IsNullOrWhiteSpace(name)) byName[name.Trim().ToLowerInvariant()] = b;

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
