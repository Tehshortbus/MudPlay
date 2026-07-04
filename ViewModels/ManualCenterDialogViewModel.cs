using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.ViewModels.Navigation;

namespace FujinTerm.ViewModels;

// Right-click → "Center on…" prompt. Mirrors the Navigation window's
// room-search behaviour — coordinate input ("1/297", "1,297", bare "297") or
// room-name substring — and shows a live results dropdown. On commit returns
// the chosen RoomKey; Cancel / X returns null.
public sealed partial class ManualCenterDialogViewModel : ObservableObject, IDialogViewModel<RoomKey?>
{
    public event Action<RoomKey?>? CloseRequested;

    private readonly RoomGraphManager _graph;

    // User-typed query — accepts the same input dialects as the Navigation
    // rail's search box (key, bare number, name substring). Empty until the
    // user types.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(ErrorMessage))]
    private string _query = string.Empty;

    // Live-search dropdown rows. Rooms only (no monsters).
    public ObservableCollection<RoomSearchResult> SearchResults { get; } = new();

    public bool HasSearchResults => SearchResults.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    private RoomSearchResult? _selectedSearchResult;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public string ErrorMessage
    {
        get
        {
            string q = (Query ?? string.Empty).Trim();
            if (q.Length == 0) return string.Empty;
            if (SearchResults.Count > 0) return string.Empty;
            if (ResolveLiteralQuery(q) is not null) return string.Empty;
            return $"No graph match for '{q}'.";
        }
    }

    // Enabled when there's at least one resolvable target (dropdown row or literal parse).
    public bool CanCommit
        => SelectedSearchResult is not null
        || SearchResults.Count > 0
        || ResolveLiteralQuery(Query) is not null;

    public ManualCenterDialogViewModel(RoomGraphManager graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
    }

    [RelayCommand]
    private void Commit()
    {
        RoomKey? resolved =
              SelectedSearchResult?.Key
           ?? (SearchResults.Count > 0 ? SearchResults[0].Key : (RoomKey?)null)
           ?? ResolveLiteralQuery(Query);
        if (resolved is not { } key) return;
        CloseRequested?.Invoke(key);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    // ----- live search ------------------------------------------------

    // Same 120 ms debounce window the Navigation search uses so the
    // dialog feels identical.
    private DispatcherTimer? _searchDebounce;
    private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(120);

    partial void OnQueryChanged(string value)
    {
        _searchDebounce ??= new DispatcherTimer { Interval = SearchDebounceDelay };
        _searchDebounce.Stop();
        _searchDebounce.Tick -= OnSearchDebounceTick;
        _searchDebounce.Tick += OnSearchDebounceTick;
        _searchDebounce.Start();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce?.Stop();
        RebuildSearchResults(Query);
    }

    private void RebuildSearchResults(string query)
    {
        SearchResults.Clear();
        string needle = (query ?? string.Empty).Trim();
        if (needle.Length == 0)
        {
            OnPropertyChanged(nameof(HasSearchResults));
            OnPropertyChanged(nameof(CanCommit));
            OnPropertyChanged(nameof(ErrorMessage));
            return;
        }

        List<RoomSearchResult> matches = new();

        // Coordinate input — same parse rules as
        // NavigationViewModel + the LoopEditor's add-room field.
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
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(ErrorMessage));
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

    // Last-resort literal resolution for the case where the user typed
    // something the debounced search hadn't rebuilt for yet (e.g. typing the
    // full coordinate and pressing Enter immediately). Returns null when
    // ambiguous.
    private RoomKey? ResolveLiteralQuery(string? query)
    {
        string q = (query ?? string.Empty).Trim();
        if (q.Length == 0) return null;

        (int? mapPart, int? roomPart) = TryParseCoordinate(q);
        if (mapPart is int m && roomPart is int r
            && _graph.GetRoom(new RoomKey(m, r)) is not null)
            return new RoomKey(m, r);

        RoomKey? exact = null;
        List<RoomKey> substrings = new();
        foreach (Room r2 in _graph.Rooms)
        {
            if (string.Equals(r2.Name, q, StringComparison.OrdinalIgnoreCase))
            {
                if (exact is not null) return null;
                exact = r2.Key;
            }
            else if (r2.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                substrings.Add(r2.Key);
            }
        }
        if (exact is { } e) return e;
        if (substrings.Count == 1) return substrings[0];
        return null;
    }
}
