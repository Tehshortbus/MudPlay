using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.ViewModels;

/// <summary>
/// Staged editor for the per-BBS room blacklist. Pre-loaded with the
/// store's current entries; in-dialog Add / Remove mutate a local
/// working copy. <b>Save</b> commits the working copy to the store
/// (which persists + redraws); <b>Cancel</b> discards.
/// </summary>
/// <remarks>
/// Add flow: the user types a Map number and a Room number; as
/// soon as both parse cleanly the dialog looks up the room in the
/// active graph and pre-fills the Name preview from
/// <see cref="Room.DisplayName"/>. The Add button is enabled when
/// the key resolves AND the key isn't already in the working list.
/// </remarks>
public sealed partial class BlacklistEditorDialogViewModel
    : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    private readonly RoomBlacklistStore _store;
    private readonly RoomGraphManager _graph;

    public ObservableCollection<BlacklistedRoom> Entries { get; } = new();

    /// <summary>
    /// Live mirror of the list's multi-selection set. The dialog's
    /// code-behind syncs this whenever <c>EntriesList.SelectionChanged</c>
    /// fires (Avalonia exposes <c>SelectedItems</c> as a non-bindable
    /// <see cref="System.Collections.IList"/>, so the sync is imperative).
    /// <see cref="RemoveSelected"/> drops every entry in this set so a
    /// Ctrl-/Shift-selection of several rows removes them all at once.
    /// </summary>
    public ObservableCollection<BlacklistedRoom> SelectedEntries { get; } = new();

    /// <summary>Map number input in the Add row (display string so empty stays empty).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddNamePreview))]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private string _addMap = string.Empty;

    /// <summary>Room number input in the Add row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddNamePreview))]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private string _addRoom = string.Empty;

    public bool CanRemoveSelected => SelectedEntries.Count > 0;

    /// <summary>True when both inputs parse + room exists + isn't already listed.</summary>
    public bool CanAdd
    {
        get
        {
            if (!TryParseAddKey(out RoomKey key)) return false;
            if (_graph.GetRoom(key) is null) return false;
            foreach (BlacklistedRoom e in Entries)
                if (e.Map == key.Map && e.Room == key.Room) return false;
            return true;
        }
    }

    /// <summary>
    /// Name preview for the Add row. Reads from the active set's
    /// Rooms.json via <see cref="RoomGraphManager.GetRoom"/>; shows
    /// the room's <see cref="Room.DisplayName"/> when it exists, a
    /// placeholder otherwise.
    /// </summary>
    public string AddNamePreview
    {
        get
        {
            if (!TryParseAddKey(out RoomKey key)) return "(enter map and room number)";
            Room? r = _graph.GetRoom(key);
            if (r is null) return $"(no room at {key} in this game-data set)";
            return r.DisplayName;
        }
    }

    public BlacklistEditorDialogViewModel(RoomBlacklistStore store, RoomGraphManager graph)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(graph);
        _store = store;
        _graph = graph;

        // Snapshot the store's current entries into the working copy.
        foreach (BlacklistedRoom e in _store.Entries)
            Entries.Add(new BlacklistedRoom(e.Map, e.Room, e.Name));

        // The Remove-selected button enables off the live selection count.
        SelectedEntries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanRemoveSelected));
    }

    [RelayCommand]
    private void AddRow()
    {
        if (!CanAdd) return;
        if (!TryParseAddKey(out RoomKey key)) return;
        string name = _graph.GetRoom(key)?.DisplayName ?? "???";
        Entries.Add(new BlacklistedRoom(key.Map, key.Room, name));
        AddMap = string.Empty;
        AddRoom = string.Empty;
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(AddNamePreview));
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedEntries.Count == 0) return;
        // Snapshot first: removing from Entries re-fires the control's
        // SelectionChanged, which clears + rebuilds SelectedEntries mid-loop.
        foreach (BlacklistedRoom sel in SelectedEntries.ToList())
            Entries.Remove(sel);
        SelectedEntries.Clear();
        OnPropertyChanged(nameof(CanAdd));               // free-up the (map,room) tuples for re-add
    }

    [RelayCommand]
    private void Save()
    {
        _store.ReplaceAll(Entries);                      // persists + fires Changed → map redraws
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    private bool TryParseAddKey(out RoomKey key)
    {
        key = default;
        if (!int.TryParse(AddMap, out int m)  || m <= 0) return false;
        if (!int.TryParse(AddRoom, out int r) || r <= 0) return false;
        key = new RoomKey(m, r);
        return true;
    }
}
