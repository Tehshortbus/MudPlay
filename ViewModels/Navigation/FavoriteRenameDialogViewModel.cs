using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

// Tiny rename dialog for a saved favourite room — single text box + Save /
// Cancel. Returns the new label on Save, null on Cancel. The caller
// (NavigationViewModel) writes the result through FavoritesStore.Rename.
public sealed partial class FavoriteRenameDialogViewModel : ObservableObject, IDialogViewModel<string?>
{
    public event Action<string?>? CloseRequested;

    public FavoriteRenameDialogViewModel(string currentLabel, string coordTag)
    {
        _label = currentLabel ?? string.Empty;
        CoordTag = coordTag;
    }

    // The label being edited. Empty is a valid value — falls back to the room's graph name at render time.
    [ObservableProperty] private string _label;

    // Subtitle shown under the input — the room's m/r coord so the user knows which favourite they're editing.
    public string CoordTag { get; }

    [RelayCommand]
    private void Save() => CloseRequested?.Invoke(Label?.Trim() ?? string.Empty);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
