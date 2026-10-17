using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Game.Map.MpFile;
using MudPlay.Services;
using MudPlay.ViewModels.Guides;

namespace MudPlay.ViewModels.Navigation;

// The MegaMUD .mp import review: the file decoded (header, path details) beside the
// loop it becomes, over one table whose every line is a .mp step on the left and our
// translation of it on the right, so the two are checked line for line. A step that
// didn't translate is a blank line to fill in. "Verify loop in MudPlay" applies the
// rooms typed in, re-translates from them, and checks our navigation can walk the
// result all the way round as a loop. Accept saves it, adding the rooms ticked
// "Stash" to the character's stash rooms; Reject closes without saving.
// The MudPlay side can be reshaped too: a step's row left out of the loop, a room
// inserted below any row, and rows moved up or down. The shape (_layout) survives
// every re-translation, and the map comparison redraws after each change.
public sealed partial class MpImportReviewViewModel : ObservableObject, IDialogViewModel<Loop?>
{
    private readonly string _path;
    private readonly MpLoopFile _file;
    private readonly MpFileImporter _importer;
    private readonly RoomGraphManager _graph;
    private readonly LoopManager _loops;
    // The game-data set the file was translated on: the rooms in the rows are that
    // set's (see OpenedOnGameData), and Accept writes into that set's Loops folder.
    private readonly OpenedOnGameData _openedOn;
    private readonly MovementFilter? _filter;
    private readonly LogService? _log;
    private MegaMudRoomsFile? _roomsMd;
    private IReadOnlyList<MpTranslation> _candidates = Array.Empty<MpTranslation>();
    private List<MpSourceRow> _sourceRows = new();

    // Rooms the user has set by hand, by step, held across every verify. Clearing a
    // row's box releases it.
    private readonly Dictionary<int, RoomKey> _fixedRooms = new();

    // The rows in the user's order: a step's index, or a row they inserted. Steps
    // left out of the loop are in _removed. Built once; a re-translation rebuilds each
    // step's row but keeps this shape.
    private readonly List<object> _layout = new();
    private readonly HashSet<int> _removed = new();

    public event Action<Loop?>? CloseRequested;

    public MpImportReviewViewModel(
        string path, MpLoopFile file, MegaMudRoomsFile? roomsMd,
        MpFileImporter importer, RoomGraphManager graph, LoopManager loops,
        MovementFilter? filter, LogService? log = null,
        MapComparisonOverlay? mapComparison = null, Action<RoomKey>? showOnMap = null)
    {
        _mapComparison = mapComparison;
        _showOnMap = showOnMap;
        _path = path;
        _file = file;
        _roomsMd = roomsMd;
        _importer = importer;
        _graph = graph;
        _loops = loops;
        _openedOn = new OpenedOnGameData(loops.SetName);
        _filter = filter;
        _log = log;

        FileName = Path.GetFileName(path);
        Label = file.Label;
        Author = string.IsNullOrWhiteSpace(file.Author) ? "(none)" : file.Author;
        PathDetails = new[]
        {
            $"Steps: {file.Steps.Count}" + (file.DeclaredStepCount >= 0 && file.DeclaredStepCount != file.Steps.Count
                ? $" (the file says {file.DeclaredStepCount})" : ""),
            $"Start hash: {file.StartHashExits}" + (file.IsLoop ? "" : $" · end hash: {file.EndHashExits}"),
            $"Gold needed: {file.Gold}",
            $"Item needed: {Or(file.RequiredItem)}",
            $"If it fails, run: {Or(file.FailPath)}",
            $"When finished, run: {Or(file.SuccessPath)}",
            $"Use: {Or(file.Use)}",
        };
        Problems = file.Problems;

        for (int i = 0; i < file.Steps.Count; i++) _layout.Add(i);

        LoopName = MpFileImporter.StripMapRoomSuffix(file.Label) is { Length: > 0 } clean
            ? clean
            : Path.GetFileNameWithoutExtension(path);
        Notes = BuildNotes(file);

        Retranslate(resetAnchor: true);
        _log?.Info("MpImporter", $"reviewing {FileName}: {TranslationSummary}");
    }

    // ----- the file ------------------------------------------------

    public string FileName { get; }
    public string Label { get; }
    public string Author { get; }
    [ObservableProperty] private string _startRoomText = string.Empty;
    [ObservableProperty] private string _endRoomText = string.Empty;
    public IReadOnlyList<string> PathDetails { get; }
    public IReadOnlyList<string> Problems { get; }
    public bool HasProblems => Problems.Count > 0;
    [ObservableProperty] private string _roomsMdNote = string.Empty;

    // The file's name doesn't say where the loop starts and no Rooms.md has named it:
    // the start is only a guess from its hash, so ask for the Rooms.md it was made with.
    [ObservableProperty] private bool _needsRoomsMd;

    // ----- our loop ------------------------------------------------

    [ObservableProperty] private string _loopName = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _statusIsGood;
    [ObservableProperty] private string _translationSummary = string.Empty;

    public ObservableCollection<MpAnchorChoice> AnchorChoices { get; } = new();
    [ObservableProperty] private bool _hasAnchorChoice;
    [ObservableProperty] private MpAnchorChoice? _selectedAnchor;

    // A start room picked while another game data was active. Translating from it is
    // matching the steps against the active rooms, so it waits for the first Verify,
    // Accept or Rooms.md load on the set the file was opened on. Until then the rows
    // are the earlier start room's, and every status says so (SetStatus).
    private bool _anchorWaiting;

    private const string AnchorWaitingNote =
        "The start room picked meanwhile is not in the rows yet: the next Verify or Accept on this loop's own game data rebuilds them from it and stops there.";

    partial void OnSelectedAnchorChanged(MpAnchorChoice? value)
    {
        if (value is null || _rebuilding) return;
        // Set before the refusal goes up, so the status names the wait with it.
        _anchorWaiting = OtherGameDataRefusal() is not null;
        if (_anchorWaiting) RefusedForOtherGameData("start room change");
        else TranslateFrom(value);
    }

    private void TranslateFrom(MpAnchorChoice anchor) =>
        BuildRows(_fixedRooms.Count > 0 || _roomsMd is not null
            ? _importer.Translate(_file, anchor.Anchor, _fixedRooms, _roomsMd)
            : _candidates.FirstOrDefault(c => c.Anchor.Equals(anchor.Anchor)), preserveEdits: true);

    // True when a start room was waiting. The rows are rebuilt from it, with the
    // rooms typed in since, and the click that got here goes no further: what it
    // would verify or save is not what the rows showed when it was pressed. A
    // command, delay or tick set on a step whose room changed is not carried over
    // (as on any change of start room), and the status says how many steps lost one.
    private bool RebuiltFromWaitingAnchor(string what)
    {
        if (!_anchorWaiting) return false;
        if (SelectedAnchor is not { } anchor)
        {
            _anchorWaiting = false;
            return false;
        }
        // A typed room that isn't in the map: said on the status, the pick still waiting.
        if (!TryReadTypedRooms(out Dictionary<int, RoomKey> typed)) return true;

        MpTranslatedRowViewModel[] shown = Rows.Where(r => !r.IsInserted).ToArray();
        _anchorWaiting = false;
        _fixedRooms.Clear();
        foreach ((int i, RoomKey k) in typed) _fixedRooms[i] = k;
        TranslateFrom(anchor);

        Dictionary<int, MpTranslatedRowViewModel> rebuilt = Rows.Where(r => !r.IsInserted).ToDictionary(r => r.Index);
        int lost = shown.Count(old => rebuilt.TryGetValue(old.Index, out MpTranslatedRowViewModel? now) && old.SettingsLostIn(now));
        SetStatus("The rows were rebuilt from the start room picked while other game data was active, and nothing was "
            + (what switch { "accept" => "saved", "verify" => "verified", _ => "loaded" })
            + " — check them, then press Verify or Accept again."
            + (lost > 0 ? $" The command, delay or ticks set on {lost} step(s) were not carried over." : ""), good: false);
        _log?.Info("MpImporter",
            $"{FileName}: {what} stopped, rows rebuilt from the start room picked under other game data — {TranslationSummary}"
            + (lost > 0 ? $"; settings on {lost} step(s) not carried over" : ""));
        return true;
    }

    private string? OtherGameDataRefusal() => _openedOn.Refusal("loop", _loops.SetName);

    // True, with the reason on the status line and in the log, when another game-data
    // set is active than the one the file was opened on: nothing is saved, verified
    // or translated until that set is active again.
    private bool RefusedForOtherGameData(string what)
    {
        if (OtherGameDataRefusal() is not { } refusal) return false;
        SetStatus(refusal, good: false);
        _log?.Info("MpImporter",
            $"{FileName}: {what} refused: {_openedOn.LogReason(_loops.SetName)}");
        return true;
    }

    public ObservableCollection<MpTranslatedRowViewModel> Rows { get; } = new();

    // ----- map comparison ------------------------------------------

    private readonly MapComparisonOverlay? _mapComparison;
    private readonly Action<RoomKey>? _showOnMap;

    // "Display on map": draw MegaMUD's recorded moves (followed literally from the
    // start room) and our converted loop (walked as the loop runner would) on the
    // Navigation map in two colours, so the steps where they part are visible.
    [ObservableProperty] private bool _displayOnMap;

    partial void OnDisplayOnMapChanged(bool value)
    {
        if (value) RefreshMapComparison(recentre: true);
        else _mapComparison?.Clear();
    }

    private void RefreshMapComparison(bool recentre = false)
    {
        if (!DisplayOnMap || _mapComparison is null) return;
        RoomKey? start = _fixedRooms.TryGetValue(0, out RoomKey set) ? set : SelectedAnchor?.Anchor;
        if (start is not { } s)
        {
            _mapComparison.Clear();
            return;
        }
        (IReadOnlyList<RoomKey> recorded, IReadOnlyList<RoomKey> stuck) = _importer.DeadReckon(_file, s);
        IReadOnlyList<RoomKey> converted = _loops.ResolveRouteRoomKeys(
            MpFileImporter.Waypoints(Rows.Select(r => r.ToWaypoint())), _filter);
        _mapComparison.Show(recorded, converted, stuck);
        if (recentre) _showOnMap?.Invoke(s);
        _log?.Info("MpImporter", $"{FileName}: map comparison — recorded {recorded.Count} rooms ({stuck.Count} stuck), converted {converted.Count} rooms");
    }

    // The window closed (Accept, Reject or the title-bar X): take the comparison off the map.
    public void OnWindowClosed() => _mapComparison?.Clear();

    public bool CanAccept => _file.IsLoop;

    private bool _rebuilding;

    // Point at the Rooms.md the loop was made with, to name its rooms.
    [RelayCommand]
    private async Task LoadRoomsMdAsync()
    {
        if (RefusedForOtherGameData("Rooms.md load") || RebuiltFromWaitingAnchor("Rooms.md load")) return;
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;
        IReadOnlyList<IStorageFile> picked = await main.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Pick the MegaMUD Rooms.md this loop was made with",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("MegaMUD Rooms.md") { Patterns = new[] { "*.md", "*.MD" } },
                FilePickerFileTypes.All,
            },
        });
        if (picked.Count == 0) return;
        MegaMudRoomsFile roomsMd;
        try
        {
            roomsMd = MegaMudRoomsFile.Parse(await File.ReadAllTextAsync(picked[0].Path.LocalPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Can't read that file: {ex.Message}", good: false);
            return;
        }
        // The game data can have changed while the file picker was up, and a start
        // room have been picked under the other one.
        if (RefusedForOtherGameData("Rooms.md load") || RebuiltFromWaitingAnchor("Rooms.md load")) return;
        _roomsMd = roomsMd;
        Retranslate(resetAnchor: _fixedRooms.Count == 0);
        SetStatus(NeedsRoomsMd
            ? "That Rooms.md doesn't name this loop's start room either — pick it from the list or type it in the first row."
            : "Rooms.md loaded — the start room is named now.", good: !NeedsRoomsMd);
        _log?.Info("MpImporter", $"{FileName}: Rooms.md loaded ({_roomsMd.Rooms.Count} rooms) — {TranslationSummary}");
    }

    // Apply the rooms typed in, re-translate from them, and check our navigation can
    // walk the result as a loop — every leg, round to the start.
    [RelayCommand]
    private void Verify()
    {
        if (RefusedForOtherGameData("verify")) return;
        NameTypedRooms();
        if (RebuiltFromWaitingAnchor("verify")) return;
        if (!ApplyTypedRooms()) return;
        CheckWalkable();
        RefreshMapComparison();
    }

    [RelayCommand]
    private void Accept()
    {
        if (!CanAccept) return;
        // Before the name is checked: under another set it would be checked against
        // that set's loops.
        if (RefusedForOtherGameData("accept")) return;
        NameTypedRooms();
        if (RebuiltFromWaitingAnchor("accept")) return;
        string name = LoopName.Trim();
        if (name.Length == 0) { SetStatus("Give the loop a name first.", good: false); return; }
        // A loop of that name another client saved a moment ago counts.
        _loops.TakeInOutsideChanges();
        if (_loops.Get(name) is not null) { SetStatus($"A loop named '{name}' already exists — rename this one.", good: false); return; }
        if (!ApplyTypedRooms()) return;
        if (CheckWalkable() is not { } waypoints) return;

        int stashed = AddStashRooms();

        Loop loop = new(name, waypoints) { Notes = Notes.Trim() };
        _loops.Save(loop);
        GuideActions.Report(GuideActions.MpImportAccepted);
        int blanks = Rows.Count(r => r.IsBlank && !r.IsRemoved && r.EffectiveRoom is null);
        int removed = Rows.Count(r => r.IsRemoved), added = Rows.Count(r => r.IsInserted && r.EffectiveRoom is not null);
        _log?.Info("MpImporter",
            $"accepted {FileName} as '{name}': {waypoints.Count} waypoint(s)"
            + (blanks > 0 ? $", {blanks} step(s) left blank" : "")
            + (removed > 0 ? $", {removed} step(s) left out" : "")
            + (added > 0 ? $", {added} room(s) added" : "")
            + $", {stashed} stash room(s) added");
        CloseRequested?.Invoke(loop);
    }

    // Stash rooms are a character setting, not part of a loop: the rows ticked
    // "Stash" (pre-ticked where MegaMUD marked a stash point) are added on Accept.
    // Returns how many were new.
    private int AddStashRooms()
    {
        if (_filter is null) return 0;
        List<RoomKey> toAdd = Rows.Where(r => r.MarkStash && !r.IsRemoved).Select(r => r.EffectiveRoom).OfType<RoomKey>()
            .Distinct().Where(k => !_filter.IsStash(k)).ToList();
        foreach (RoomKey k in toAdd) _filter.MarkStash(k);
        return toAdd.Count;
    }

    [RelayCommand]
    private void Reject()
    {
        _log?.Info("MpImporter", $"rejected {FileName}");
        CloseRequested?.Invoke(null);
    }

    // A room typed into an inserted row while another game data was active was named
    // from that set's rooms: name them from this one's.
    private void NameTypedRooms()
    {
        foreach (MpTranslatedRowViewModel row in Rows) row.NameTypedRoom();
    }

    // Take the rooms typed into the rows (an empty box releases one set before) and
    // re-translate from them. False when a typed room isn't in the map.
    private bool ApplyTypedRooms()
    {
        if (!TryReadTypedRooms(out Dictionary<int, RoomKey> updated)) return false;
        bool changed = updated.Count != _fixedRooms.Count || updated.Any(kv => !_fixedRooms.TryGetValue(kv.Key, out RoomKey v) || !v.Equals(kv.Value));
        if (!changed) return true;

        RoomKey anchor = updated.TryGetValue(0, out RoomKey first) ? first : SelectedAnchor?.Anchor ?? default;
        if (anchor.Equals(default(RoomKey)))
        {
            SetStatus("Set the first step's room (map/room) to start from.", good: false);
            return false;
        }
        _fixedRooms.Clear();
        foreach ((int i, RoomKey k) in updated) _fixedRooms[i] = k;
        BuildRows(_importer.Translate(_file, anchor, _fixedRooms, _roomsMd), preserveEdits: true);
        _log?.Info("MpImporter", $"{FileName}: re-translated with {_fixedRooms.Count} room(s) set by hand — {TranslationSummary}");
        return true;
    }

    // The rooms set by hand as the rows have them now: those held, with what is
    // typed in the boxes (an empty box releases one set before). False, with the
    // status saying which, when a typed room isn't in the map.
    private bool TryReadTypedRooms(out Dictionary<int, RoomKey> updated)
    {
        updated = new(_fixedRooms);
        foreach (MpTranslatedRowViewModel row in Rows)
        {
            if (row.IsInserted)
            {
                string typed = row.RoomText.Trim();
                if (typed.Length > 0 && (!RoomKey.TryParseWire(typed, out RoomKey added) || _graph.GetRoom(added) is null))
                {
                    SetStatus($"An added row: '{row.RoomText}' isn't a room in the map data (use map/room, e.g. 1/2150).", good: false);
                    return false;
                }
                continue;
            }
            if (!row.RoomEdited) continue;
            string text = row.RoomText.Trim();
            if (text.Length == 0) { updated.Remove(row.Index); continue; }
            if (!RoomKey.TryParseWire(text, out RoomKey k) || _graph.GetRoom(k) is null)
            {
                SetStatus($"Step {row.NumberText}: '{row.RoomText}' isn't a room in the map data (use map/room, e.g. 1/2150).", good: false);
                return false;
            }
            updated[row.Index] = k;
        }
        return true;
    }

    // Expand the rows' rooms the way the loop runner does (every leg planned, then the
    // closing leg back to the start) and report the result. The waypoints when every
    // leg can be walked; null, with the legs that can't marked, otherwise.
    private List<LoopWaypoint>? CheckWalkable()
    {
        foreach (MpTranslatedRowViewModel r in Rows) r.Unreachable = false;
        List<LoopWaypoint> waypoints = MpFileImporter.Waypoints(Rows.Select(r => r.ToWaypoint()));
        if (waypoints.Count < 2)
        {
            SetStatus("The loop needs at least two rooms — fill in the blank steps.", good: false);
            return null;
        }

        (IReadOnlyList<LoopStep> steps, IReadOnlyList<(RoomKey From, RoomKey To)> unreachable) =
            _loops.ExpandWaypoints(waypoints, _filter);
        int blanks = Rows.Count(r => r.IsBlank && !r.IsRemoved && string.IsNullOrWhiteSpace(r.RoomText));
        if (unreachable.Count == 0)
        {
            int moves = steps.Count(s => s is MoveLoopStep);
            SetStatus($"✓ Verified: MudPlay walks this as a loop — {waypoints.Count} rooms, {moves} moves, back to the start."
                + (blanks > 0 ? $" ({blanks} blank step(s) are skipped; the loop routes around them.)" : ""), good: true);
            _log?.Info("MpImporter", $"{FileName}: verified — {waypoints.Count} waypoints, {moves} moves");
            return waypoints;
        }

        foreach ((RoomKey from, _) in unreachable)
            foreach (MpTranslatedRowViewModel r in Rows.Where(r => r.EffectiveRoom is { } k && k.Equals(from)))
                r.Unreachable = true;
        (RoomKey f, RoomKey t) = unreachable[0];
        SetStatus($"✗ MudPlay can't walk this loop: no route from {Describe(f)} to {Describe(t)}"
            + (unreachable.Count > 1 ? $" (and {unreachable.Count - 1} more leg(s), marked ⚠)" : " (marked ⚠)")
            + ". Set a different room on those steps and verify again.", good: false);
        _log?.Info("MpImporter", $"{FileName}: verify failed — {unreachable.Count} unreachable leg(s), first {f}→{t}");
        return null;
    }

    // Recompute everything that depends on the Rooms.md and the start room.
    private void Retranslate(bool resetAnchor)
    {
        _rebuilding = true;
        try
        {
            _sourceRows = _file.Steps.Select((s, i) => new MpSourceRow(i + 1, s, NameFromRoomsMd(_roomsMd, s.HashExits))).ToList();
            StartRoomText = DescribeHeader(_file.Start, _roomsMd);
            EndRoomText = _file.End == _file.Start ? "(same as start)" : DescribeHeader(_file.End, _roomsMd);
            RoomsMdNote = _roomsMd is null
                ? "No Rooms.md loaded — rooms are shown by hash only."
                : $"Rooms.md loaded ({_roomsMd.Rooms.Count} named rooms).";
            NeedsRoomsMd = _file.IsLoop && _importer.StartHint(_file, _roomsMd) is null;

            _candidates = _file.IsLoop ? _importer.TranslateCandidates(_file, _roomsMd) : Array.Empty<MpTranslation>();
            MpAnchorChoice? keep = resetAnchor ? null : SelectedAnchor;
            AnchorChoices.Clear();
            foreach (MpTranslation t in _candidates)
                AnchorChoices.Add(new MpAnchorChoice(t.Anchor, $"{Describe(t.Anchor)} — {t.Summary}"));
            HasAnchorChoice = AnchorChoices.Count > 1;
            SelectedAnchor = AnchorChoices.FirstOrDefault(a => keep is not null && a.Anchor.Equals(keep.Anchor))
                             ?? AnchorChoices.FirstOrDefault();

            MpTranslation? chosen = SelectedAnchor is { } sel
                ? (_fixedRooms.Count > 0 ? _importer.Translate(_file, sel.Anchor, _fixedRooms, _roomsMd)
                                         : _candidates.First(c => c.Anchor.Equals(sel.Anchor)))
                : null;
            BuildRows(chosen, preserveEdits: !resetAnchor);

            if (!_file.IsLoop)
                SetStatus("This is a goto path, not a loop — it can be read here but not imported as a loop.", good: false);
            else if (_candidates.Count == 0)
                SetStatus($"No room in the active map data matches the start room (hash {_file.StartHashExits}). "
                        + "Type its map/room in the first step's box and press Verify.", good: false);
        }
        finally { _rebuilding = false; }
    }

    // Rebuild the rows from a translation. preserveEdits keeps what the user typed or
    // ticked on a row (by step) when its room didn't change.
    private void BuildRows(MpTranslation? t, bool preserveEdits)
    {
        Dictionary<int, MpTranslatedRowViewModel> old = preserveEdits
            ? Rows.Where(r => !r.IsInserted).ToDictionary(r => r.Index) : new();
        var steps = new MpTranslatedRowViewModel[_file.Steps.Count];
        for (int i = 0; i < _file.Steps.Count; i++)
        {
            MpTranslatedRow row = t?.Rows[i] ?? new MpTranslatedRow(i, null, MpRowStatus.Blank, "no start room yet", false);
            MpTranslatedRowViewModel vm = new(row, _file.Steps[i], _sourceRows[i], _graph) { IsRemoved = _removed.Contains(i) };
            if (old.TryGetValue(i, out MpTranslatedRowViewModel? prev) && Equals(prev.Room, vm.Room))
                vm.CopyEditsFrom(prev);
            steps[i] = vm;
        }
        Rows.Clear();
        foreach (object slot in _layout)
            Rows.Add(slot is int i ? steps[i] : (MpTranslatedRowViewModel)slot);

        TranslationSummary = t is null ? "Nothing translated yet." : $"Start {t.Anchor}: {t.Summary}.";
        SetStatus(string.Empty, good: false);
        RefreshMapComparison();
    }

    // ----- reshaping the MudPlay side ---------------------------

    // ✕ / ↺: an inserted row goes; a step's row is left out of the loop (or put back),
    // staying on screen beside its step.
    [RelayCommand]
    private void RemoveRow(MpTranslatedRowViewModel? row)
    {
        if (row is null) return;
        if (row.IsInserted)
        {
            _layout.Remove(row);
            Rows.Remove(row);
            Reshaped("removed an added room");
            return;
        }
        row.IsRemoved = !row.IsRemoved;
        if (row.IsRemoved) _removed.Add(row.Index);
        else _removed.Remove(row.Index);
        Reshaped(row.IsRemoved ? $"left step {row.NumberText} out" : $"put step {row.NumberText} back");
    }

    // +: a new row below, for a room of the user's to walk through. It takes the
    // room typed into its box.
    [RelayCommand]
    private void InsertBelow(MpTranslatedRowViewModel? row)
    {
        if (row is null) return;
        int at = Rows.IndexOf(row);
        if (at < 0) return;
        MpTranslatedRowViewModel added = new(_graph);
        // A typed room that names a real room goes on the map as soon as it's typed.
        added.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MpTranslatedRowViewModel.RoomText)
                && added.EffectiveRoom is { } k && _graph.GetRoom(k) is not null)
                RefreshMapComparison();
        };
        _layout.Insert(at + 1, added);
        Rows.Insert(at + 1, added);
        Reshaped($"added a room below row {at + 1}");
    }

    [RelayCommand]
    private void MoveUp(MpTranslatedRowViewModel? row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(MpTranslatedRowViewModel? row) => Move(row, +1);

    private void Move(MpTranslatedRowViewModel? row, int by)
    {
        if (row is null) return;
        int from = Rows.IndexOf(row), to = from + by;
        if (from < 0 || to < 0 || to >= Rows.Count) return;
        (_layout[from], _layout[to]) = (_layout[to], _layout[from]);
        Rows.Move(from, to);
        Reshaped($"moved row {from + 1} {(by < 0 ? "up" : "down")}");
    }

    // The loop changed shape: redraw the map comparison and ask for a fresh verify.
    private void Reshaped(string what)
    {
        foreach (MpTranslatedRowViewModel r in Rows) r.Unreachable = false;
        SetStatus("Loop edited — press Verify to check MudPlay can walk it.", good: false);
        RefreshMapComparison();
        _log?.Info("MpImporter", $"{FileName}: {what}");
    }

    private void SetStatus(string text, bool good)
    {
        // A start room still waiting is said after whatever else the status says,
        // and nothing reads as verified until the rows are rebuilt from it.
        Status = !_anchorWaiting ? text : text.Length == 0 ? AnchorWaitingNote : $"{text} {AnchorWaitingNote}";
        StatusIsGood = good && !_anchorWaiting;
    }

    private string Describe(RoomKey k) => _graph.GetRoom(k) is { } r ? $"{r.Name} ({k})" : k.ToString();

    private static string DescribeHeader(MpHeaderRoom h, MegaMudRoomsFile? roomsMd)
    {
        if (h.Code.Length == 0) return "(not in the file)";
        string text = $"{h.Code} · {h.Group} · {h.Name}";
        if (roomsMd?.ByCode(h.Code) is not { } e) return text;
        List<string> bits = new();
        if (e.Options != MegaMudRoomOptions.None) bits.Add($"options: {DescribeOptions(e.Options)}");
        if (e.MinLevel > 0 || e.MaxLevel > 0) bits.Add($"levels {e.MinLevel}–{e.MaxLevel}");
        if (e.ClassId > 0) bits.Add($"class {e.ClassId}");
        return bits.Count == 0 ? text : $"{text} ({string.Join("; ", bits)})";
    }

    internal static string DescribeOptions(MegaMudRoomOptions o)
    {
        List<string> names = new();
        if (o.HasFlag(MegaMudRoomOptions.Shop)) names.Add("shop");
        if (o.HasFlag(MegaMudRoomOptions.Bank)) names.Add("bank");
        if (o.HasFlag(MegaMudRoomOptions.Trainer)) names.Add("trainer");
        if (o.HasFlag(MegaMudRoomOptions.StopBefore)) names.Add("stop before entering");
        if (o.HasFlag(MegaMudRoomOptions.Avoid)) names.Add("avoid");
        if (o.HasFlag(MegaMudRoomOptions.HideInGoto)) names.Add("hidden in goto list");
        if (o.HasFlag(MegaMudRoomOptions.CommonRooms)) names.Add("in common rooms");
        int unknown = (int)o & ~0xFE;
        if (unknown != 0) names.Add($"0x{unknown:X}");
        return string.Join(", ", names);
    }

    private static string? NameFromRoomsMd(MegaMudRoomsFile? roomsMd, string hash)
    {
        if (roomsMd is null) return null;
        List<MegaMudRoomEntry> hits = roomsMd.ByHash(hash).ToList();
        return hits.Count == 1 ? $"{hits[0].Code} {hits[0].Name}" : null;
    }

    // What the loop can't carry, written into its notes so it isn't lost.
    private static string BuildNotes(MpLoopFile file)
    {
        List<string> lines = new()
        {
            string.IsNullOrWhiteSpace(file.Author)
                ? $"Imported from MegaMUD .mp ({file.Start.Group}/{file.Start.Code})."
                : $"Imported from MegaMUD .mp by {file.Author} ({file.Start.Group}/{file.Start.Code}).",
        };
        if (file.Gold > 0) lines.Add($"MegaMUD: needs {file.Gold} gold.");
        if (file.RequiredItem.Length > 0) lines.Add($"MegaMUD: needs item '{file.RequiredItem}'.");
        if (file.FailPath.Length > 0) lines.Add($"MegaMUD: runs {file.FailPath} if the path fails.");
        if (file.SuccessPath.Length > 0) lines.Add($"MegaMUD: runs {file.SuccessPath} when finished.");
        return string.Join("\n", lines);
    }

    private static string Or(string s) => string.IsNullOrWhiteSpace(s) ? "(none)" : s;
}

public sealed record MpAnchorChoice(RoomKey Anchor, string Text)
{
    public override string ToString() => Text;
}
