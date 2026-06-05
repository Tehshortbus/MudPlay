using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

/// <summary>
/// Ephemeral session for the Navigation window's Loop mode. Tracks
/// the user's clicked rooms and uses
/// <see cref="LoopManager.ExpandClickedRooms"/> to BFS-gap-fill the
/// path between them so the saved loop is ready to run.
/// </summary>
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

    /// <summary>Click sequence rendered in the bottom strip — read-only externally.</summary>
    public ObservableCollection<LoopBuilderRow> Clicks { get; } = new();

    [ObservableProperty] private string _proposedName = "";
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private int _expandedStepCount;
    [ObservableProperty] private string _unreachableSummary = string.Empty;

    /// <summary>
    /// Flattened RoomKey sequence for the map's loop-builder polyline:
    /// every click + every BFS-filled intermediate room + the closing
    /// leg back to click 0. Null when fewer than two clicks. Refreshed
    /// alongside <see cref="ExpandedStepCount"/> on every click change.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<RoomKey>? _previewedRoomKeys;

    public bool HasClicks => Clicks.Count > 0;
    public bool CanSave   => Clicks.Count >= 2 && string.IsNullOrEmpty(UnreachableSummary);

    public void AddClick(RoomKey key)
    {
        if (_graph.GetRoom(key) is not { } room) return;
        // Adjacent duplicate? Drop — clicking the same room twice in a
        // row would gap-fill to nothing.
        if (_clicks.Count > 0 && _clicks[^1].Equals(key)) return;
        _clicks.Add(key);
        Clicks.Add(new LoopBuilderRow(Clicks.Count, key, room.DisplayName));
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

    public void Clear()
    {
        _clicks.Clear();
        Clicks.Clear();
        ExpandedStepCount = 0;
        UnreachableSummary = string.Empty;
        PreviewedRoomKeys = null;
        OnPropertyChanged(nameof(HasClicks));
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>
    /// Persist the current loop under <see cref="ProposedName"/>.
    /// Returns the saved <see cref="Loop"/>, or null when the session
    /// isn't ready to save.
    /// </summary>
    public Loop? Save()
    {
        if (!CanSave) return null;
        (var steps, _) = _loops.ExpandClickedRooms(_clicks, _filter);
        if (steps.Count == 0) return null;

        Loop loop = new(ProposedName, steps)
        {
            UserWaypoints = new List<RoomKey>(_clicks),
            Notes = Notes ?? string.Empty,
        };
        _loops.Save(loop);
        Clear();
        return loop;
    }

    private void Reexpand()
    {
        if (_clicks.Count < 2)
        {
            ExpandedStepCount = 0;
            UnreachableSummary = string.Empty;
            PreviewedRoomKeys = null;
            OnPropertyChanged(nameof(CanSave));
            return;
        }
        (var steps, var unreachable) = _loops.ExpandClickedRooms(_clicks, _filter);
        ExpandedStepCount = steps.Count;
        UnreachableSummary = unreachable.Count == 0
            ? string.Empty
            : $"{unreachable.Count} unreachable segment(s)";
        PreviewedRoomKeys = unreachable.Count == 0
            ? BuildRoomKeySequence(_clicks[0], steps)
            : null;     // hide preview when the cycle has gaps
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>
    /// Walk the expanded direction list from <paramref name="start"/>
    /// and accumulate every room the cycle visits. Returns null if
    /// any intermediate exit doesn't resolve (graph mutation between
    /// expansion and walking — shouldn't happen in normal use but the
    /// null guard keeps the renderer from drawing a partial polyline).
    /// </summary>
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

/// <summary>Single click row shown in the bottom strip — index + room label.</summary>
public sealed record LoopBuilderRow(int Index, RoomKey Key, string Name);
