using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Profile;

// Picker over the saved character profiles on disk, each a ProfileRef (bbs, char) pair
// — profiles are BBS-scoped now, so the bare character name is no longer a unique key.
// Selecting + the confirm button — or double-clicking a row — commits the chosen ref;
// Cancel / title-bar X returns null. Serves File → Open profile (default wording) and
// any other "pick a profile" flow (e.g. Settings → Toolbar + Shortcuts "Copy from
// profile") via the overridable title / prompt / confirm-label strings.
public sealed partial class ProfilePickerDialogViewModel : ObservableObject, IDialogViewModel<ProfileRef>
{
    public event Action<ProfileRef?>? CloseRequested;

    public ObservableCollection<ProfileRef> Profiles { get; } = new();

    // Window title — the caller's base wording plus the absolute folder the list is read
    // from (AppPaths.BbsDir), e.g. "Open profile - C:\Users\…\FujinTerm\Data\BBS", so
    // it's obvious at a glance which data directory the app is actually using.
    public string WindowTitle { get; }

    // Prompt line above the list — defaults to the Open-profile wording.
    public string Prompt { get; }

    // Confirm-button caption — defaults to "Open".
    public string ConfirmLabel { get; }

    // Empty-state message shown when no profiles are available.
    public string EmptyMessage { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(HasProfiles))]
    private ProfileRef? _selectedProfile;

    public bool HasSelection => SelectedProfile is not null;
    public bool HasProfiles  => Profiles.Count > 0;

    public ProfilePickerDialogViewModel(
        IEnumerable<ProfileRef> profiles,
        string windowTitle = "Open profile",
        string prompt = "Pick a character profile to load.",
        string confirmLabel = "Open",
        string emptyMessage = "No saved profiles yet — use File → Save profile as… to create one.")
    {
        WindowTitle  = $"{windowTitle} - {AppPaths.BbsDir}";
        Prompt       = prompt;
        ConfirmLabel = confirmLabel;
        EmptyMessage = emptyMessage;

        foreach (ProfileRef r in profiles
            .OrderBy(static p => p.Bbs, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static p => p.Name, StringComparer.OrdinalIgnoreCase))
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
