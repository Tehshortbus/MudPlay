using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

// Ephemeral session for the Navigation window's Loop mode. Tracks the user's
// clicked rooms and uses LoopManager.ExpandWaypoints to BFS-gap-fill the path
// between them so the saved loop is ready to run. On Save the click list
// becomes the loop's Waypoints (each click → one LoopWaypoint with no command
// attached); commands and delays are added later via the loop editor.
public sealed partial class LoopBuilderSessionViewModel : ObservableObject
{
    private readonly LoopManager _loops;
    private readonly RoomGraphManager _graph;
    private readonly IRoomFilter? _filter;
    private readonly List<RoomKey> _clicks = new();

    public LoopBuilderSessionViewModel(LoopManager loops, RoomGraphManager graph, IRoomFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(graph);
        _loops = loops;
        _graph = graph;
        _filter = filter;
        ProposedName = $"Loop {DateTime.Now:HH-mm}";
    }

    // Click sequence rendered in the bottom strip — read-only externally.
    public ObservableCollection<LoopBuilderRow> Clicks { get; } = new();

    [ObservableProperty] private string _proposedName = "";
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private int _expandedStepCount;
    [ObservableProperty] private string _unreachableSummary = string.Empty;

    // Flattened RoomKey sequence for the map's loop-builder polyline: every
    // click + every BFS-filled intermediate room + the closing leg back to
    // click 0. Null when fewer than two clicks. Refreshed alongside
    // ExpandedStepCount on every click change.
    [ObservableProperty] private IReadOnlyList<RoomKey>? _previewedRoomKeys;

    // Just the clicks the user laid down, in order — used by the map's
    // numbered waypoint markers. Distinct from PreviewedRoomKeys which
    // carries every BFS-filled intermediate.
    [ObservableProperty] private IReadOnlyList<RoomKey>? _waypointKeys;

    public bool HasClicks => Clicks.Count > 0;
    public bool CanSave   => Clicks.Count >= 2 && string.IsNullOrEmpty(UnreachableSummary);

    public void AddClick(RoomKey key)
    {
        if (_graph.GetRoom(key) is not { } room) return;
        // Adjacent duplicate? Drop — clicking the same room twice in a
        // row would gap-fill to nothing.
        if (_clicks.Count > 0 && _clicks[^1].Equals(key)) return;
        _clicks.Add(key);
        // 1-based index matches the map's red numbered waypoint
        // markers + the post-edit RenumberRows pass. Clicks.Count is
        // pre-Add, so the first row gets 1, not 0.
        Clicks.Add(new LoopBuilderRow(Clicks.Count + 1, key, room.DisplayName));
        OnPropertyChanged(nameof(HasClicks));
        Reexpand();
    }

    public void RemoveLastClick()
    {
        if (_clicks.Count == 0) return;
        _clicks.RemoveAt(_clicks.Count - 1);
        Clicks.RemoveAt(Clicks.Count - 1);
        OnPropertyChanged(nameof(HasClicks));
        Reexpand();
    }

    // Remove the click at index (CURRENT NAV row click → remove). No-op when
    // index is out of range.
    public void RemoveClickAt(int index)
    {
        if (index < 0 || index >= _clicks.Count) return;
        _clicks.RemoveAt(index);
        Clicks.RemoveAt(index);
        RenumberRows();
        OnPropertyChanged(nameof(HasClicks));
        Reexpand();
    }

    // Move the click at fromIndex to toIndex (CURRENT NAV drag-reorder).
    // No-op when indices match or either is out of range.
    public void MoveClick(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex) return;
        if (fromIndex < 0 || fromIndex >= _clicks.Count) return;
        if (toIndex   < 0 || toIndex   >= _clicks.Count) return;

        RoomKey key = _clicks[fromIndex];
        _clicks.RemoveAt(fromIndex);
        _clicks.Insert(toIndex, key);
        Clicks.Move(fromIndex, toIndex);
        RenumberRows();
        Reexpand();
    }

    private void RenumberRows()
    {
        for (int i = 0; i < Clicks.Count; i++)
        {
            Clicks[i] = Clicks[i] with { Index = i + 1 };
        }
    }

    public void Clear()
    {
        _clicks.Clear();
        Clicks.Clear();
        ExpandedStepCount = 0;
        UnreachableSummary = string.Empty;
        PreviewedRoomKeys = null;
        WaypointKeys = null;
        OnPropertyChanged(nameof(HasClicks));
        OnPropertyChanged(nameof(CanSave));
    }

    // Persist the current loop under ProposedName. Returns the saved Loop, or
    // null when the session isn't ready to save.
    public Loop? Save()
    {
        if (!CanSave) return null;
        Loop? loop = BuildTransient();
        if (loop is null) return null;
        _loops.Save(loop);
        Clear();
        return loop;
    }

    // Build a Loop from the current clicks WITHOUT writing it to disk. Used
    // by the Run button so transient "try this loop" runs don't pollute the
    // saved-loops list — the user persists explicitly via the Manage dialog.
    // Returns null when the session isn't ready (fewer than 2 clicks,
    // gap-fill unreachable). The build session is left intact so the user can
    // still Save it from Manage later.
    public Loop? BuildTransient()
    {
        if (!CanSave) return null;
        if (_clicks.Count < 2) return null;

        // Commands / delays start null — users attach them later via
        // the loop editor's per-waypoint inline editor (only meaningful
        // on a persisted loop).
        var waypoints = new List<LoopWaypoint>(_clicks.Count);
        foreach (RoomKey k in _clicks) waypoints.Add(new LoopWaypoint(k));

        return new Loop(ProposedName, waypoints)
        {
            Notes = Notes ?? string.Empty,
        };
    }

    private void Reexpand()
    {
        // Always refresh the waypoint marker list so the map's numbered
        // pins update on every click, even for the < 2 click case (one
        // marker still draws when the user has placed one room).
        WaypointKeys = _clicks.Count == 0 ? null : new List<RoomKey>(_clicks);

        if (_clicks.Count < 2)
        {
            ExpandedStepCount = 0;
            UnreachableSummary = string.Empty;
            PreviewedRoomKeys = null;
            OnPropertyChanged(nameof(CanSave));
            return;
        }
        var waypoints = new List<LoopWaypoint>(_clicks.Count);
        foreach (RoomKey k in _clicks) waypoints.Add(new LoopWaypoint(k));
        (var steps, var unreachable) = _loops.ExpandWaypoints(waypoints, _filter);
        ExpandedStepCount = steps.Count;
        UnreachableSummary = unreachable.Count == 0
            ? string.Empty
            : $"{unreachable.Count} unreachable segment(s)";
        PreviewedRoomKeys = unreachable.Count == 0
            ? BuildRoomKeySequence(_clicks[0], steps)
            : null;     // hide preview when the cycle has gaps
        OnPropertyChanged(nameof(CanSave));
    }

    // Walk the expanded direction list from start and accumulate every room
    // the cycle visits. Returns null if any intermediate exit doesn't resolve
    // (graph mutation between expansion and walking — shouldn't happen in
    // normal use but the null guard keeps the renderer from drawing a partial
    // polyline).
    private IReadOnlyList<RoomKey>? BuildRoomKeySequence(RoomKey start, IReadOnlyList<LoopStep> steps)
    {
        var sequence = new List<RoomKey>(steps.Count + 1) { start };
        RoomKey cursor = start;
        foreach (LoopStep step in steps)
        {
            if (step is not MoveLoopStep move) continue;
            if (_graph.GetRoom(cursor) is not { } room) return null;
            if (!room.Exits.TryGetValue(move.Direction, out RoomExit exit)) return null;
            cursor = exit.Target;
            sequence.Add(cursor);
        }
        return sequence;
    }
}

// Single click row shown in the bottom strip — index + room label.
public sealed record LoopBuilderRow(int Index, RoomKey Key, string Name);
