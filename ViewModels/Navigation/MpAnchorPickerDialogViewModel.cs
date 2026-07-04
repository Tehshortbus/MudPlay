using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Game.Map.MpFile;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

// Multi-candidate disambiguation prompt for the .mp importer. Fires when
// MpFileImporter.Resolve finds two or more rooms that hash-match the file's
// start AND produce a closed loop from the recorded steps. User picks one;
// result is the chosen anchor's RoomKey, or null on cancel.
public sealed partial class MpAnchorPickerDialogViewModel
    : ObservableObject, IDialogViewModel<RoomKey?>
{
    public event Action<RoomKey?>? CloseRequested;

    public string Heading { get; }
    public string SubHeading { get; }

    public ObservableCollection<MpAnchorRow> Candidates { get; } = new();

    [ObservableProperty] private MpAnchorRow? _selected;

    public bool HasSelection => Selected is not null;

    partial void OnSelectedChanged(MpAnchorRow? value)
        => OnPropertyChanged(nameof(HasSelection));

    public MpAnchorPickerDialogViewModel(
        MpLoopFile file,
        IReadOnlyList<MpImportCandidate> candidates,
        RoomGraphManager graph)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(graph);

        Heading = $"Multiple rooms match '{file.Label}'";
        SubHeading =
            $"The .mp file's start (hash {file.StartHashExits}) closes to {candidates.Count} different rooms in the active BBS. "
          + "Pick the one you mean — comparing against the map helps.";

        foreach (MpImportCandidate c in candidates)
        {
            Room? room = graph.GetRoom(c.AnchorKey);
            string label = room is null
                ? c.AnchorKey.ToString()
                : $"{c.AnchorKey} — {room.Name}";
            string mismatchTag = c.HashMismatches == 0
                ? "exact"
                : $"{c.HashMismatches} per-step mismatch(es)";
            Candidates.Add(new MpAnchorRow(c.AnchorKey, label, mismatchTag));
        }
        if (Candidates.Count > 0) Selected = Candidates[0];
    }

    [RelayCommand]
    private void Pick() => CloseRequested?.Invoke(Selected?.Key);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

// One displayable candidate row in the picker dialog.
public sealed record MpAnchorRow(RoomKey Key, string Label, string Tag);
