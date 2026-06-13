using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Profile;

/// <summary>
/// File → Open profile dialog. Lists every saved character profile that has
/// a primary <c>profile.json</c> on disk, as a <see cref="ProfileRef"/>
/// (bbs, char) pair — profiles are BBS-scoped now, so the bare character name
/// is no longer a unique key. Selecting + Open — or double-clicking a row —
/// commits the chosen ref; Cancel / title-bar X returns <c>null</c>.
/// </summary>
public sealed partial class ProfilePickerDialogViewModel : ObservableObject, IDialogViewModel<ProfileRef>
{
    public event Action<ProfileRef?>? CloseRequested;

    public ObservableCollection<ProfileRef> Profiles { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(HasProfiles))]
    private ProfileRef? _selectedProfile;

    public bool HasSelection => SelectedProfile is not null;
    public bool HasProfiles  => Profiles.Count > 0;

    public ProfilePickerDialogViewModel(IEnumerable<ProfileRef> profiles)
    {
        foreach (ProfileRef r in profiles
            .OrderBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static p => p.Bbs, StringComparer.OrdinalIgnoreCase))
            Profiles.Add(r);

        SelectedProfile = Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(HasProfiles));
    }

    [RelayCommand]
    private void Open()
    {
        if (SelectedProfile is null) return;
        CloseRequested?.Invoke(SelectedProfile);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
