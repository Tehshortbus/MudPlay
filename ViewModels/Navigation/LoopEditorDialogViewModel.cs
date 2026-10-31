using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.ViewModels.Guides;

namespace MudPlay.ViewModels.Navigation;

// Modeless editor for an existing Loop — rename, edit notes, add (search box or
// a map click) / reorder / remove waypoints, edit per-waypoint commands + delays
// inline.
//
// Save mutates the loop in place + persists via LoopManager.Save, which
// fires LoopsChanged so the Navigation pane refreshes. Cancel / X discards
// every edit; the dialog works on its own row view-models until Save runs.
public sealed partial class LoopEditorDialogViewModel : ObservableObject, IDialogViewModel<Loop?>
{
    public event Action<Loop?>? CloseRequested;

    private readonly Loop _original;
    private readonly LoopManager _loops;
    // The game-data set the loop was opened on: its rooms are that set's. The window
    // is modeless and stays open through a switch of game data, after which Save
    // would write it into the other set's Loops folder, a file every character and
    // running client on that set reads, and a room added would be the other set's.
    private readonly OpenedOnGameData _openedOn;

    // Why the loop can't be saved or added to right now, or null: another game-data
    // set is active than the one it was opened on.
    private string? OtherGameDataRefusal() => _openedOn.Refusal("loop", _loops.SetName);
    private readonly RoomGraphManager _graph;
    private readonly LoopRunner? _runner;
    private readonly ConfirmService? _confirm;
    private readonly bool _isNew;

    // The loop's runnable steps as opened, so Save can tell whether the user
    // actually edited the cycle. Gates the "apply to the running loop?" prompt
    // — a Save with no step change must not nag. Compared against the dialog's
    // own current rows (not the runner's live copy): the runner rotates its
    // waypoint list to the current room at Start, so a positional diff against
    // it would false-positive; a restart re-rotates anyway, making rotation
    // irrelevant to whether the run would differ.
    private readonly List<(string Room, string? Command, long DelayMs, bool DoNotRest, bool DoNotAttack, bool RestHereHp, bool RestHereMana, long LapWaitMs)> _openedSteps;

    // Window title — flips between "Create Loop" (when the dialog was opened
    // on a fresh empty loop) and "Edit Loop" (mutating an existing saved
    // loop). Bound from the AXAML Window.Title.
    public string DialogTitle => _isNew ? "Create Loop" : "Edit Loop";

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;

    // "Set as favorite" — surfaces this loop in the terminal's right-click
    // Favorites menu (green), where clicking it starts the loop.
    [ObservableProperty] private bool _favorite;

    // Loop-wide "only attack in lair rooms" (an Entire Loop Setting). When set,
    // the loop only engages hostiles in game-data lair rooms; every other room
    // is walked through as if auto-combat were off. Mirrored by the nav rail's
    // Entire Loop Settings flyout. This and the lair wait below are the loaded
    // character's settings for the loop, as Favorite is its choice: the window
    // opens on that character's and Save keeps them for it, not in the loop file.
    [ObservableProperty] private bool _onlyAttackInLairRooms;
    [ObservableProperty] private bool _waitToDebuffBeforeLairs;
    [ObservableProperty] private int _lairDebuffWaitChoice;

    public ObservableCollection<LoopWaypointRowViewModel> Waypoints { get; } = new();

    [ObservableProperty] private LoopWaypointRowViewModel? _selectedRow;

    // Text input for the "add waypoint" row. Accepts the same input dialects
    // as the Navigation window's room search box — coordinate ("1/297",
    // "1,297", bare "297" across all maps) or substring against room names.
    // Monster matches are deliberately omitted; this row's only job is to
    // pick a waypoint room.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddWaypointError))]
    private string _newWaypointQuery = string.Empty;

    // Inline validation message for the add-waypoint row. Empty when the
    // input is valid OR not yet evaluated; set after a failed Add attempt.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddWaypointError))]
    private string _addWaypointError = string.Empty;

    public bool HasAddWaypointError => !string.IsNullOrEmpty(AddWaypointError);

    // Live-search dropdown rows. Mirrors the Navigation search box behaviour
    // minus the monster category — rooms only, since the editor only ever
    // needs to pick a waypoint room.
    public ObservableCollection<RoomSearchResult> SearchResults { get; } = new();

    public bool HasSearchResults => SearchResults.Count > 0;

    // User-highlighted row in the dropdown ListBox. Enter on the TextBox
    // commits this row when set; falls back to the top result otherwise, then
    // to the literal query (key parse / single name match) so an experienced
    // user can type "1/297<Enter>" without ever moving focus to the dropdown.
    [ObservableProperty] private RoomSearchResult? _selectedSearchResult;

    // Debounce keystrokes — same 120 ms window the Navigation search
    // box uses so the editor feels identical to the user.
    private DispatcherTimer? _searchDebounce;
    private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(120);

    public LoopEditorDialogViewModel(
        Loop loop,
        LoopManager loops,
        RoomGraphManager graph,
        LoopRunner? runner = null,
        ConfirmService? confirm = null,
        bool isNew = false,
        OpenedOnGameData? roomsOf = null)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(graph);
        _original = loop;
        _loops = loops;
        // roomsOf names the set for a loop that didn't come off the active one's
        // catalogue or map: the running loop, stamped with the set it was started on.
        _openedOn = roomsOf ?? new OpenedOnGameData(loops.SetName);
        _graph = graph;
        _runner = runner;
        _confirm = confirm;
        _isNew = isNew;
        _name = loop.Name;
        _notes = loop.Notes ?? string.Empty;
        _favorite = loop.Favorite;
        _onlyAttackInLairRooms = loop.OnlyAttackInLairRooms;
        _waitToDebuffBeforeLairs = LairEntryDebuffModes.IsOn(loop.LairEntryDebuff);
        _lairDebuffWaitChoice = LairEntryDebuffModes.ChoiceOf(loop.LairEntryDebuff);
        Waypoints.CollectionChanged += OnRowsChanged;
        foreach (LoopWaypoint w in loop.Waypoints)
            Waypoints.Add(new LoopWaypointRowViewModel(w, graph));
        RenumberRows();

        // Snapshot the opened steps using the same derivation Save uses
        // (row.Key.ToString() / Command / DelayMs) so the dirty check is an
        // apples-to-apples comparison immune to Room-string reformatting.
        _openedSteps = Waypoints
            .Select(r => (r.Key.ToString(), r.Command, r.DelayMs, r.DoNotRest, r.DoNotAttack, r.RestHereHp, r.RestHereMana, r.LapWaitMs))
            .ToList();
    }

    // ----- row commands ----------------------------------------------

    [RelayCommand]
    private void MoveUp(LoopWaypointRowViewModel? row)
    {
        if (row is null) return;
        int idx = Waypoints.IndexOf(row);
        if (idx <= 0) return;
        Waypoints.Move(idx, idx - 1);
        RenumberRows();
    }

    [RelayCommand]
    private void MoveDown(LoopWaypointRowViewModel? row)
    {
        if (row is null) return;
        int idx = Waypoints.IndexOf(row);
        if (idx < 0 || idx >= Waypoints.Count - 1) return;
        Waypoints.Move(idx, idx + 1);
        RenumberRows();
    }

    [RelayCommand]
    private void Remove(LoopWaypointRowViewModel? row)
    {
        if (row is null) return;
        Waypoints.Remove(row);
        if (SelectedRow == row) SelectedRow = null;
        RenumberRows();
    }

    partial void OnNewWaypointQueryChanged(string value)
    {
        // Debounce the rebuild — matches the Navigation search box's
        // 120 ms window so a 200-room substring scan doesn't run per
        // keystroke. Clear errors immediately so the user isn't
        // staring at a stale "no match" line while they type.
        AddWaypointError = string.Empty;
        _searchDebounce ??= new DispatcherTimer { Interval = SearchDebounceDelay };
        _searchDebounce.Stop();
        _searchDebounce.Tick -= OnSearchDebounceTick;
        _searchDebounce.Tick += OnSearchDebounceTick;
        _searchDebounce.Start();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce?.Stop();
        RebuildSearchResults(NewWaypointQuery);
    }

    // Rebuild SearchResults from query. Two input dialects, both mirroring
    // the Navigation search box: coordinate ("1/297", "1,297", "1 297", or
    // bare "297" across all maps) and room-name substring. Monster matches
    // are intentionally omitted.
    internal void RebuildSearchResults(string query)
    {
        // The rows listed are the active game data's rooms. Under another set than
        // the loop's none are, so a row of that set can't be taken once back on it.
        if (OtherGameDataRefusal() is { } refusal)
        {
            DropSearchResults();
            AddWaypointError = refusal;
            return;
        }
        SearchResults.Clear();
        string needle = (query ?? string.Empty).Trim();
        if (needle.Length == 0)
        {
            OnPropertyChanged(nameof(HasSearchResults));
            return;
        }

        List<RoomSearchResult> matches = new();

        // Coordinate input — same parse rules as NavigationViewModel's
        // search box.
        (int? mapPart, int? roomPart) = TryParseCoordinate(needle);
        if (mapPart is int m && roomPart is int r
            && _graph.GetRoom(new RoomKey(m, r)) is { } exactRoom)
        {
            matches.Add(new RoomSearchResult(exactRoom.Key, exactRoom.DisplayName, null));
        }
        else if (mapPart is null && roomPart is int onlyRoom)
        {
            foreach (Room room in _graph.Rooms)
            {
                if (room.Key.Room != onlyRoom) continue;
                matches.Add(new RoomSearchResult(room.Key, room.DisplayName, null));
                if (matches.Count >= 200) break;
            }
        }

        // Name substring — gated by needle length to keep
        // single-character queries from flooding the dropdown.
        if (needle.Length >= 2)
        {
            foreach (Room room in _graph.Rooms)
            {
                if (!room.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                 && !room.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (matches.Any(x => x.Key.Equals(room.Key))) continue;
                matches.Add(new RoomSearchResult(room.Key, room.DisplayName, null));
                if (matches.Count >= 200) break;
            }
        }

        foreach (RoomSearchResult rr in matches
                     .OrderBy(rr => rr.PrimaryLine, StringComparer.OrdinalIgnoreCase)
                     .Take(50))
            SearchResults.Add(rr);
        OnPropertyChanged(nameof(HasSearchResults));
    }

    // Nothing listed or highlighted is kept through a refusal: whatever is held may
    // have been picked while another game data's rooms were on show.
    private void DropSearchResults()
    {
        SearchResults.Clear();
        SelectedSearchResult = null;
        OnPropertyChanged(nameof(HasSearchResults));
    }

    // Parse a coordinate token. Returns (map, room) when both numbers were
    // supplied (separator: /, ,, or whitespace), (null, room) for a bare
    // single number, or (null, null) for non-numeric input.
    private static (int? Map, int? Room) TryParseCoordinate(string text)
    {
        string[] parts = text.Split(new[] { '/', ',', ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && int.TryParse(parts[0], out int onlyRoom))
            return (null, onlyRoom);
        if (parts.Length == 2
            && int.TryParse(parts[0], out int map)
            && int.TryParse(parts[1], out int room))
            return (map, room);
        return (null, null);
    }

    // Add a waypoint at the end of the list. Priority: the dropdown's
    // highlighted row → the top dropdown row → the literal query parsed as a
    // key or matched as a unique name. Empty input is a no-op (the dropdown
    // is empty + the textbox has nothing to add).
    [RelayCommand]
    private void AddWaypoint()
    {
        if (OtherGameDataRefusal() is { } refusal)
        {
            DropSearchResults();
            AddWaypointError = refusal;
            return;
        }
        RoomKey? resolved =
              SelectedSearchResult?.Key
           ?? (SearchResults.Count > 0 ? SearchResults[0].Key : (RoomKey?)null)
           ?? ResolveLiteralQuery(NewWaypointQuery);

        if (resolved is null)
        {
            string query = (NewWaypointQuery ?? string.Empty).Trim();
            AddWaypointError = query.Length == 0
                ? "Enter a room key (1/297) or room name."
                : $"No graph match for '{query}'.";
            return;
        }

        // The same room twice in a row is allowed on purpose: a zero-length "stay
        // put" leg that runs another command in place (e.g. two barmaid steps — hand
        // in pies, then convert the coin). The expander treats it as 0 moves, so it
        // saves + runs cleanly.
        Waypoints.Add(new LoopWaypointRowViewModel(
            new LoopWaypoint(resolved.Value), _graph));
        RenumberRows();
        NewWaypointQuery = string.Empty;
        SearchResults.Clear();
        SelectedSearchResult = null;
        OnPropertyChanged(nameof(HasSearchResults));
        AddWaypointError = string.Empty;
    }

    // Append a waypoint for a room picked on the Navigation map. The map's left-click
    // is routed here while this dialog is open (see AppServices.TryCaptureLoopWaypoint /
    // the dialog code-behind). Ignores a click on a room not in the loaded graph;
    // same-room-twice-in-a-row is allowed on purpose, same as AddWaypoint above.
    public void AddWaypointByRoomKey(RoomKey key)
    {
        if (OtherGameDataRefusal() is { } refusal)
        {
            AddWaypointError = refusal;
            return;
        }
        if (_graph.GetRoom(key) is null) return;
        Waypoints.Add(new LoopWaypointRowViewModel(new LoopWaypoint(key), _graph));
        RenumberRows();
        AddWaypointError = string.Empty;
    }

    // Last-resort literal resolution — covers the case where the user typed
    // something that didn't show up in SearchResults (e.g. an exact key whose
    // debounce hadn't fired yet). Returns null when ambiguous so we don't
    // guess.
    private RoomKey? ResolveLiteralQuery(string? query)
    {
        string q = (query ?? string.Empty).Trim();
        if (q.Length == 0) return null;

        if (RoomKey.TryParseWire(q, out RoomKey key)
            && _graph.GetRoom(key) is not null)
            return key;

        RoomKey? exact = null;
        List<RoomKey> substrings = new();
        foreach (Room r in _graph.Rooms)
        {
            if (string.Equals(r.Name, q, StringComparison.OrdinalIgnoreCase))
            {
                if (exact is not null) return null;
                exact = r.Key;
            }
            else if (r.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                substrings.Add(r.Key);
            }
        }
        if (exact is { } e) return e;
        if (substrings.Count == 1) return substrings[0];
        return null;
    }

    // ----- dialog commit ---------------------------------------------

    // Inline validation message for the Save row. Empty when the dialog is in
    // a savable state; populated on the next Save click with whatever's
    // blocking it (empty name, too few waypoints, no BBS bound, etc.) so the
    // user sees feedback instead of a button that visibly does nothing.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveError))]
    private string _saveError = string.Empty;

    public bool HasSaveError => !string.IsNullOrEmpty(SaveError);

    [RelayCommand]
    private async Task SaveAsync()
    {
        string newName = (Name ?? string.Empty).Trim();
        if (newName.Length == 0)
        {
            SaveError = "Loop needs a name.";
            return;
        }
        if (Waypoints.Count < 2)
        {
            SaveError = "Cycles need at least 2 waypoints.";
            return;
        }
        if (_loops.SetName is null)
        {
            // LoopManager silently no-ops the Save call when no game-data
            // set is active — surface the cause instead of disappearing
            // the dialog with no feedback.
            SaveError = "No active game-data set — select one before saving loops.";
            return;
        }
        if (OtherGameDataRefusal() is { } refusal)
        {
            SaveError = refusal;
            return;
        }

        // Name-conflict guard: refuse if the new name would overwrite
        // a DIFFERENT loop. Renaming an existing loop to its own
        // current name (i.e. no rename, just a Save) is the intentional
        // overwrite case and is allowed. A loop of that name another client saved a
        // moment ago counts, so the folder is read first, without waiting for it to
        // settle.
        _loops.TakeInOutsideChanges();
        bool wouldOverwriteAnother = _loops.Loops.Any(l =>
            string.Equals(l.Name, newName, StringComparison.OrdinalIgnoreCase)
            && (_isNew || !string.Equals(l.Name, _original.Name, StringComparison.OrdinalIgnoreCase)));
        if (wouldOverwriteAnother)
        {
            SaveError = $"A loop named '{newName}' already exists on this BBS — pick a different name.";
            return;
        }

        var waypoints = new List<LoopWaypoint>(Waypoints.Count);
        foreach (LoopWaypointRowViewModel row in Waypoints)
        {
            // Normalize inline edits: an empty/whitespace command clears the
            // command AND its delay (a delay only fires around a command step),
            // mirroring the build-time dialog's convention.
            string? cmd = string.IsNullOrWhiteSpace(row.Command) ? null : row.Command.Trim();
            long delay = cmd is null ? 0 : Math.Max(0, row.DelayMs);
            waypoints.Add(new LoopWaypoint(row.Key, cmd, delay, row.DoNotRest, row.DoNotAttack)
            {
                RestHereHp = row.RestHereHp,
                RestHereMana = row.RestHereMana,
                LapWaitMs = Math.Max(0, row.LapWaitMs),
                StartHere = row.StartHere,
            });
        }

        if (_loops.WhyNotFolder(_original.Folder) is { } badFolder)
        {
            SaveError = $"Loop not saved: its folder '{_original.Folder}' can't be used. {badFolder}";
            return;
        }

        bool renamed = !string.Equals(newName, _original.Name, StringComparison.OrdinalIgnoreCase);
        string oldName = _original.Name;
        // The catalogue's own object: put back as it was when the save fails.
        string oldNotes = _original.Notes;
        bool oldOnlyAttackInLairRooms = _original.OnlyAttackInLairRooms;
        LairEntryDebuffMode oldLairEntryDebuff = _original.LairEntryDebuff;
        List<LoopWaypoint> oldWaypoints = _original.Waypoints;

        _original.Name                  = newName;
        _original.Notes                 = Notes ?? string.Empty;
        _original.OnlyAttackInLairRooms = OnlyAttackInLairRooms;
        _original.LairEntryDebuff = LairEntryDebuffModes.From(WaitToDebuffBeforeLairs, LairDebuffWaitChoice);
        _original.Waypoints             = waypoints;

        // A rename saves under the new name first and drops the old loop (its file,
        // and the character's two loop settings kept under its name) only once that
        // is written, so a failed save loses nothing. A brand-new loop has no old one:
        // its draft name may even be another loop's.
        string? failed;
        try
        {
            bool saved = renamed && !_isNew ? _loops.SaveRenamed(_original, oldName) : _loops.Save(_original);
            failed = saved ? null : "the loop's folder can't be used (see the program log).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed = ex.Message;
        }
        if (failed is not null)
        {
            _original.Name                  = oldName;
            _original.Notes                 = oldNotes;
            _original.OnlyAttackInLairRooms = oldOnlyAttackInLairRooms;
            _original.LairEntryDebuff       = oldLairEntryDebuff;
            _original.Waypoints             = oldWaypoints;
            AppServices.CurrentOrNull?.Log.Warn("Loops", $"loop '{oldName}' not saved as '{newName}': {failed}");
            SaveError = $"Loop not saved: {failed}";
            return;
        }
        GuideActions.Report(GuideActions.LoopSaved);
        // The tick box is this character's choice, kept apart from the loop file.
        _loops.SetFavorite(_original.Name, Favorite);
        SaveError = string.Empty;

        // If we just edited the live running loop, reconcile the runner with
        // what we saved. Two cases:
        if (_runner is { } runner && IsEditingRunningLoop(runner, oldName))
        {
            if (_confirm is { } confirm && StepsEditedSinceOpen(waypoints))
            {
                // The runnable PATH changed — ask whether to restart with the
                // new definition. The user might be tweaking for next time and
                // not want their current lap disrupted, OR they might be fixing a
                // bug and want it applied immediately. Yes → stop+restart (which
                // adopts the new name too); No → leave the runner on the in-memory
                // pre-edit version — name and path both stay as they're actually
                // running, and a later Run picks up the new disk version.
                Loop? asked = runner.CurrentLoop;
                bool restart = await confirm.ConfirmAsync(
                    "Apply to running loop?",
                    "You edited the loop that's currently running. Restart the runner with the new version now?",
                    yesLabel: "Restart now");
                // The prompt is modeless: a Yes for a run that has since stopped, or
                // been replaced, would restart a loop the user ended or end the one
                // they started.
                if (restart && !ReferenceEquals(runner.CurrentLoop, asked))
                {
                    AppServices.CurrentOrNull?.Log.Debug("Loops",
                        $"restart of loop '{_original.Name}' with its edit dropped: the run it was asked for has ended");
                    restart = false;
                }
                // A restart is a start: with the master switch off it is refused
                // (with the notice) and the runner stays on the version it holds.
                if (restart && !AppServices.Current.RefuseStartForMasterSwitch("Loop"))
                {
                    runner.Stop("edited; restarting with new definition");
                    runner.Start(_original);
                }
            }
            else if (renamed)
            {
                // Name/notes-only edit: the live path is identical to what we
                // saved, so renaming in place is truthful and doesn't disrupt the
                // lap. Push the new name onto the runner so the nav header + status
                // chip stop holding the old (often builder-generated) name.
                runner.RenameCurrentLoop(newName);
            }
        }

        CloseRequested?.Invoke(_original);
    }

    // True when runner's current loop is the one we just edited. Matched by
    // the pre-rename name so a rename during the edit still detects the
    // running-loop case.
    private static bool IsEditingRunningLoop(LoopRunner runner, string oldName)
        => runner.CurrentLoop is { } cur
        && string.Equals(cur.Name, oldName, StringComparison.OrdinalIgnoreCase);

    // True when the saved waypoint sequence (rooms, per-waypoint commands,
    // delays, in order) differs from what the dialog opened on — i.e. the user
    // actually changed the runnable cycle. A Save that only renames or edits
    // notes leaves this false, so it never offers a mid-lap restart (the runner
    // would drive the identical path). Name / notes still persist via Save.
    private bool StepsEditedSinceOpen(List<LoopWaypoint> current)
    {
        if (current.Count != _openedSteps.Count) return true;
        for (int i = 0; i < current.Count; i++)
        {
            (string room, string? command, long delayMs, bool doNotRest, bool doNotAttack, bool restHp, bool restMana, long lapWaitMs) = _openedSteps[i];
            if (!string.Equals(current[i].Room, room, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.Equals(current[i].Command ?? string.Empty, command ?? string.Empty,
                    StringComparison.Ordinal))
                return true;
            if (current[i].DelayMs != delayMs) return true;
            if (current[i].DoNotRest != doNotRest) return true;
            if (current[i].DoNotAttack != doNotAttack) return true;
            if (current[i].RestHereHp != restHp || current[i].RestHereMana != restMana) return true;
            if (current[i].LapWaitMs != lapWaitMs) return true;
        }
        return false;
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    // ----- internals -------------------------------------------------

    // One start room per loop (LoopWaypoint.StartHere): ticking a row's clears the rest.
    private void OnRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (LoopWaypointRowViewModel row in e.NewItems?.OfType<LoopWaypointRowViewModel>() ?? [])
            row.PropertyChanged += OnRowChanged;
        foreach (LoopWaypointRowViewModel row in e.OldItems?.OfType<LoopWaypointRowViewModel>() ?? [])
            row.PropertyChanged -= OnRowChanged;
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LoopWaypointRowViewModel.StartHere)
            || sender is not LoopWaypointRowViewModel { StartHere: true } ticked) return;
        foreach (LoopWaypointRowViewModel row in Waypoints)
            if (!ReferenceEquals(row, ticked)) row.StartHere = false;
    }

    private void RenumberRows()
    {
        for (int i = 0; i < Waypoints.Count; i++)
            Waypoints[i].Index = i + 1;
    }
}

// Per-row VM for the editor's Waypoints ListBox. Carries the resolved room
// name (read-only) + the inline-editable command, delay, do-not-rest,
// do-not-attack, rest-up-here, lap-wait and start-here fields (each a column in
// the table).
public sealed partial class LoopWaypointRowViewModel : ObservableObject
{
    public RoomKey Key { get; }
    [ObservableProperty] private int _index;
    [ObservableProperty] private string _displayLabel = string.Empty;
    [ObservableProperty] private string? _command;
    [ObservableProperty] private long _delayMs;
    [ObservableProperty] private bool _doNotRest;
    [ObservableProperty] private bool _doNotAttack;
    [ObservableProperty] private bool _restHereHp;
    [ObservableProperty] private bool _restHereMana;
    [ObservableProperty] private long _lapWaitMs;
    [ObservableProperty] private bool _startHere;

    public LoopWaypointRowViewModel(LoopWaypoint source, RoomGraphManager graph)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(graph);
        Key = source.Key;
        _command = source.Command;
        _delayMs = source.DelayMs;
        _doNotRest = source.DoNotRest;
        _doNotAttack = source.DoNotAttack;
        _restHereHp = source.RestHereHp;
        _restHereMana = source.RestHereMana;
        _lapWaitMs = source.LapWaitMs;
        _startHere = source.StartHere;
        RefreshDisplayFor(graph);
    }

    private void RefreshDisplayFor(RoomGraphManager graph)
    {
        Room? room = graph.GetRoom(Key);
        DisplayLabel = room is null
            ? Key.ToString()
            : $"{room.DisplayName}  ·  {Key}";
    }
}
