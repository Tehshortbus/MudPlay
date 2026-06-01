using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Profile;

/// <summary>
/// File → Open profile dialog. Lists every named character profile that
/// has a primary <c>profile.json</c> on disk (the per-folder layout shipped
/// in the Phase 5 restructure) and lets the user pick one to load. Selecting
/// + Open — or double-clicking a row — commits the profile name; Cancel /
/// title-bar X returns <c>null</c>.
/// </summary>
public sealed partial class ProfilePickerDialogViewModel : ObservableObject, IDialogViewModel<string>
{
    public event Action<string?>? CloseRequested;

    public ObservableCollection<string> Profiles { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(HasProfiles))]
    private string? _selectedProfile;

    public bool HasSelection => !string.IsNullOrEmpty(SelectedProfile);
    public bool HasProfiles  => Profiles.Count > 0;

    public ProfilePickerDialogViewModel(IEnumerable<string> profileNames)
    {
        foreach (string name in profileNames.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase))
            Profiles.Add(name);

        SelectedProfile = Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(HasProfiles));
    }

    [RelayCommand]
    private void Open()
    {
        if (string.IsNullOrEmpty(SelectedProfile)) return;
        CloseRequested?.Invoke(SelectedProfile);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
