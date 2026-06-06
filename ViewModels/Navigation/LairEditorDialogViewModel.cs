using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

/// <summary>
/// Modeless editor for a saved <see cref="LairSetup"/> — rename, edit
/// notes, remove markers, adjust per-marker respawn overrides. Adding
/// a marker requires picking a room on the map; that's the rail's job
/// (right-click → "Add to Auto-Lair"), not the editor's.
/// </summary>
/// <remarks>
/// Save persists via <see cref="LairManager.Save"/>, which fires
/// <see cref="LairManager.SetupsChanged"/> so the rail refreshes.
/// Cancel / X discards every edit; the dialog works on its own row
/// view-models until Save runs.
/// </remarks>
public sealed partial class LairEditorDialogViewModel : ObservableObject, IDialogViewModel<LairSetup?>
{
    public event Action<LairSetup?>? CloseRequested;

    private readonly LairSetup _original;
    private readonly LairManager _setups;
    private readonly RoomGraphManager _graph;
    private readonly LairTimerStore _timers;
    private readonly ConfirmService? _confirm;
    private readonly bool _isNew;

    /// <summary>Window title — "Create Setup" when new, "Edit Setup" otherwise.</summary>
    public string DialogTitle => _isNew ? "Create Auto-Lair Setup" : "Edit Auto-Lair Setup";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(HasNameError))]
    private string _name = string.Empty;

    [ObservableProperty] private string _notes = string.Empty;

    /// <summary>Per-marker row, ordered by Map / Room for a stable display.</summary>
    public ObservableCollection<LairMarkerRowViewModel> Markers { get; } = new();

    /// <summary>True when the name field is non-empty after trim.</summary>
    public bool HasName => !string.IsNullOrWhiteSpace(Name);

    /// <summary>Inline-validation flag for the name TextBox.</summary>
    public bool HasNameError => !HasName;

    /// <summary>Enabled-state for the Save button — name set + at least one marker.</summary>
    public bool CanSave => HasName && Markers.Count > 0;

    public LairEditorDialogViewModel(
        LairSetup setup,
        LairManager setups,
        RoomGraphManager graph,
        LairTimerStore timers,
        ConfirmService? confirm = null,
        bool isNew = false)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(setups);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(timers);
        _original = setup;
        _setups = setups;
        _graph = graph;
        _timers = timers;
        _confirm = confirm;
        _isNew = isNew;

        Name  = setup.Name ?? string.Empty;
        Notes = setup.Notes ?? string.Empty;
        foreach (LairMarker m in setup.Markers) AddMarkerRow(m);
        Markers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanSave));
    }

    private void AddMarkerRow(LairMarker marker)
    {
        RoomKey key = new(marker.Map, marker.Room);
        string roomName = _graph.GetRoom(key)?.DisplayName ?? key.ToString();
        int? defaultRespawn = _timers.DefaultRespawnSeconds(key);
        Markers.Add(new LairMarkerRowViewModel(
            key, roomName, defaultRespawn, marker.OverrideRespawnSeconds, marker.Skip));
    }

    [RelayCommand]
    private void RemoveMarker(LairMarkerRowViewModel? row)
    {
        if (row is null) return;
        Markers.Remove(row);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanSave) return;

        // Renaming an existing setup to a name that's already taken
        // would silently clobber. Mirror LoopEditorDialog's behaviour:
        // confirm the overwrite explicitly.
        string trimmed = Name.Trim();
        bool nameChanged = !string.Equals(trimmed, _original.Name, StringComparison.OrdinalIgnoreCase);
        if (nameChanged && _setups.Get(trimmed) is not null && _confirm is not null)
        {
            bool ok = await _confirm.ConfirmAsync(
                title: "Overwrite existing setup?",
                body: $"A setup named \"{trimmed}\" already exists on this BBS. Overwrite it?");
            if (!ok) return;
        }

        // Persist a fresh LairSetup. We don't mutate _original — the
        // rail rows hold references to it via LairSetupRowViewModel,
        // and the LairManager keys on Name for the on-disk file.
        List<LairMarker> markers = new(Markers.Count);
        foreach (LairMarkerRowViewModel r in Markers)
        {
            markers.Add(new LairMarker(
                map: r.Key.Map,
                room: r.Key.Room,
                overrideRespawnSeconds: r.OverrideRespawnSeconds,
                skip: r.Skip));
        }

        LairSetup saved = new(trimmed, markers) { Notes = Notes ?? string.Empty };

        // Renaming an existing setup needs the old file deleted —
        // LairManager keys files by name so a rename leaves the old
        // file orphaned otherwise.
        if (!_isNew && nameChanged && !string.IsNullOrWhiteSpace(_original.Name))
            _setups.Delete(_original.Name);

        _setups.Save(saved);
        CloseRequested?.Invoke(saved);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

/// <summary>
/// One row in the editor's marker grid: the room key + display name
/// + game-data default respawn (read-only context) + user override
/// (editable) + Skip toggle.
/// </summary>
public sealed partial class LairMarkerRowViewModel : ObservableObject
{
    public RoomKey Key { get; }
    public string RoomName { get; }

    /// <summary>"5/100 — Sewer Lair" header for the row.</summary>
    public string DisplayHeader => $"{Key.Map}/{Key.Room} — {RoomName}";

    /// <summary>
    /// Game-data default respawn in seconds, surfaced read-only so the
    /// user can see what the override is replacing. <c>null</c> when no
    /// default could be resolved (lookup missed or the room isn't
    /// tagged as a lair in the active set).
    /// </summary>
    public int? DefaultRespawnSeconds { get; }

    /// <summary>"default 1800s" / "no default" hint next to the override field.</summary>
    public string DefaultHint =>
        DefaultRespawnSeconds is int s
            ? $"game default {s}s"
            : "no game-data default";

    [ObservableProperty] private int? _overrideRespawnSeconds;
    [ObservableProperty] private bool _skip;

    public LairMarkerRowViewModel(
        RoomKey key,
        string roomName,
        int? defaultRespawnSeconds,
        int? overrideRespawnSeconds,
        bool skip)
    {
        Key = key;
        RoomName = roomName;
        DefaultRespawnSeconds = defaultRespawnSeconds;
        _overrideRespawnSeconds = overrideRespawnSeconds;
        _skip = skip;
    }
}
