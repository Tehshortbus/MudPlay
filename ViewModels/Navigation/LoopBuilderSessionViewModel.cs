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
    [ObservableProperty] private bool _closeLoop;
    [ObservableProperty] private int _expandedStepCount;
    [ObservableProperty] private string _unreachableSummary = string.Empty;

    public bool HasClicks => Clicks.Count > 0;
    public bool CanSave   => Clicks.Count >= 2 && string.IsNullOrEmpty(UnreachableSummary);

    partial void OnCloseLoopChanged(bool value) => Reexpand();

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
        (var steps, _) = _loops.ExpandClickedRooms(_clicks, CloseLoop, _filter);
        if (steps.Count == 0) return null;

        Loop loop = new(ProposedName, steps)
        {
            IsCircular = CloseLoop,
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
            OnPropertyChanged(nameof(CanSave));
            return;
        }
        (var steps, var unreachable) = _loops.ExpandClickedRooms(_clicks, CloseLoop, _filter);
        ExpandedStepCount = steps.Count;
        UnreachableSummary = unreachable.Count == 0
            ? string.Empty
            : $"{unreachable.Count} unreachable segment(s)";
        OnPropertyChanged(nameof(CanSave));
    }
}

/// <summary>Single click row shown in the bottom strip — index + room label.</summary>
public sealed record LoopBuilderRow(int Index, RoomKey Key, string Name);
