using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.ViewModels;

/// <summary>
/// Right-click → "Center on…" prompt. The user enters a map number
/// and a room number; on OK the dialog returns the
/// <see cref="RoomKey"/> if the room is present in the active graph,
/// or <c>null</c> if Cancel / X was pressed. The Navigation window
/// rebuilds its BFS layout from that room so it becomes the new
/// centre of the map.
/// </summary>
public sealed partial class ManualCenterDialogViewModel : ObservableObject, IDialogViewModel<RoomKey?>
{
    public event Action<RoomKey?>? CloseRequested;

    private readonly RoomGraphManager _graph;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NamePreview))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(ErrorMessage))]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    private string _mapInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NamePreview))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(ErrorMessage))]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    private string _roomInput = string.Empty;

    /// <summary>Resolved room name when the inputs parse + exist; empty otherwise.</summary>
    public string NamePreview => TryResolve(out RoomKey k)
        ? (_graph.GetRoom(k)?.DisplayName ?? "(unnamed)")
        : string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public string ErrorMessage
    {
        get
        {
            string m = (MapInput ?? string.Empty).Trim();
            string r = (RoomInput ?? string.Empty).Trim();
            if (m.Length == 0 && r.Length == 0) return string.Empty;
            if (!int.TryParse(m, out int map) || map <= 0) return "Map must be a positive integer.";
            if (!int.TryParse(r, out int room) || room <= 0) return "Room must be a positive integer.";
            if (_graph.GetRoom(new RoomKey(map, room)) is null)
                return $"Room {map}/{room} isn't in the active game data.";
            return string.Empty;
        }
    }

    public bool CanCommit => TryResolve(out _);

    public ManualCenterDialogViewModel(RoomGraphManager graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
    }

    [RelayCommand]
    private void Commit()
    {
        if (!TryResolve(out RoomKey k)) return;
        CloseRequested?.Invoke(k);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    private bool TryResolve(out RoomKey key)
    {
        key = default;
        if (!int.TryParse((MapInput ?? string.Empty).Trim(), out int map) || map <= 0) return false;
        if (!int.TryParse((RoomInput ?? string.Empty).Trim(), out int room) || room <= 0) return false;
        RoomKey candidate = new(map, room);
        if (_graph.GetRoom(candidate) is null) return false;
        key = candidate;
        return true;
    }
}
