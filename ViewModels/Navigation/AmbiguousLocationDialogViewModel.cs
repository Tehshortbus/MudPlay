using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

/// <summary>
/// Modeless candidate-picker shown when the room tracker sees an
/// observation that matches more than one room in the active graph.
/// The user picks one of the listed candidates, or chooses to set
/// the location themselves later via the map's right-click "I am here"
/// affordance.
/// </summary>
/// <remarks>
/// <para>
/// Result semantics: <c>RoomKey</c> on Pick (caller routes to
/// <see cref="RoomTracker.SetLocated(RoomKey, DateTimeOffset?)"/>);
/// <c>null</c> on Defer / title-bar X / Escape. A null result means
/// "leave the tracker where it is — the user will sort it out from
/// the map." It is explicitly NOT an error or a cancel-the-walker
/// signal.
/// </para>
/// <para>
/// The candidate list is supplied at construction time and never
/// mutates — if the tracker sees a new round of ambiguity the
/// Navigation window closes any open dialog and opens a fresh one
/// with the new candidates.
/// </para>
/// </remarks>
public sealed partial class AmbiguousLocationDialogViewModel : ObservableObject, IDialogViewModel<RoomKey?>
{
    public event Action<RoomKey?>? CloseRequested;

    /// <summary>Observation that triggered the ambiguity — shown verbatim so the user can cross-check.</summary>
    public string ObservationName { get; }

    /// <summary>Comma-joined exits from the observation (e.g. "n, s, e, w") for at-a-glance verification.</summary>
    public string ObservationExits { get; }

    /// <summary>Candidates surfaced for the user to pick from. Read-only after construction.</summary>
    public ReadOnlyObservableCollection<AmbiguousCandidate> Candidates { get; }

    /// <summary>Header line displayed above the list.</summary>
    public string Header => Candidates.Count == 1
        ? "One room matches — confirm it's the right one:"
        : $"{Candidates.Count} rooms match — which are you in?";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPick))]
    private AmbiguousCandidate? _selected;

    /// <summary>True when a row is selected and Pick is meaningful.</summary>
    public bool CanPick => Selected is not null;

    public AmbiguousLocationDialogViewModel(
        string observationName,
        IReadOnlyList<Direction> observationExits,
        IReadOnlyList<AmbiguousCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(observationExits);
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
            throw new ArgumentException("AmbiguousLocationDialog requires at least one candidate.", nameof(candidates));

        ObservationName = string.IsNullOrWhiteSpace(observationName) ? "(unnamed room)" : observationName;
        ObservationExits = FormatExits(observationExits);

        var backing = new ObservableCollection<AmbiguousCandidate>(candidates);
        Candidates = new ReadOnlyObservableCollection<AmbiguousCandidate>(backing);
        _selected = candidates.Count == 1 ? candidates[0] : null;
    }

    [RelayCommand]
    private void Pick()
    {
        if (Selected is null) return;
        CloseRequested?.Invoke(Selected.Key);
    }

    /// <summary>
    /// "I'll set it myself" — closes the dialog without resolving the
    /// tracker. The user is expected to right-click a room on the map
    /// and use "I am here" to land manually.
    /// </summary>
    [RelayCommand]
    private void Defer() => CloseRequested?.Invoke(null);

    private static string FormatExits(IReadOnlyList<Direction> exits)
    {
        if (exits.Count == 0) return "(none)";
        return string.Join(", ", exits.Select(ShortLabel));
    }

    private static string ShortLabel(Direction d) => d switch
    {
        Direction.N => "n",
        Direction.S => "s",
        Direction.E => "e",
        Direction.W => "w",
        Direction.NE => "ne",
        Direction.NW => "nw",
        Direction.SE => "se",
        Direction.SW => "sw",
        Direction.U => "u",
        Direction.D => "d",
        _ => "?",
    };
}

/// <summary>
/// One row in the candidate list. Display-only — the dialog returns
/// the chosen row's <see cref="Key"/> through its result task.
/// </summary>
public sealed record AmbiguousCandidate(RoomKey Key, string Name, string? AreaHint)
{
    /// <summary>"map/room" string used in the list column.</summary>
    public string KeyLabel => $"{Key.Map}/{Key.Room}";

    /// <summary>The candidate name, falling back to the same "???" the room map uses.</summary>
    public string DisplayName => string.IsNullOrEmpty(Name) ? "???" : Name;
}
