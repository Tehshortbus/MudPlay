using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.ViewModels.Guides;
using MudPlay.ViewModels.Profile;

namespace MudPlay.ViewModels;

// Modeless Profile Management window VM — the one place to add / rename / delete
// / swap characters, move a character to a BBS or realm, and add / remove /
// rename BBSes and their realms. Three linked columns: BBSes → the selected BBS's
// realms → the characters on the selected realm. A BBS's / realm's actual settings
// (host, port, logon, realm mechanics) stay in the Settings BBS tab; the settings
// buttons hand off there with the record selected.
// Structural ops commit immediately via ProfileService / BbsProfileStore
// (folder moves + deletes on disk), so the window needs no Save/Cancel staging.
// Mutating the LOADED character (swap / delete / rename / assign of the current
// profile, or removing the BBS it lives on) requires being disconnected; every
// other profile is freely mutable. Current-profile lifecycle (New / Save / Save
// As) is routed back to the MainWindowViewModel commands that used to live on
// the File menu, so the collapsed menu loses no capability.
public sealed partial class ProfileManagerViewModel : ObservableObject, IDisposable
{
    private readonly Func<bool> _isDisconnected;
    private readonly Func<ProfileRef, Task> _swapToProfile;
    private readonly Func<string?, Task> _newProfile;
    private readonly Func<string?, Task> _saveCurrent;
    private readonly Func<string?, Task> _saveCurrentAs;
    private readonly Action<ProfileRef> _nameCurrentDraft;
    private readonly Action<string, string?> _editBbsSettings;
    private readonly RealmCatalog _realms;
    private bool _suppressRealmEdits;
    private readonly ProfileService _profile;
    private readonly BbsProfileStore _bbs;
    private readonly DialogService _dialogs;
    private readonly ConfirmService _confirm;
    private readonly SettingsService _settings;
    private readonly LogService? _log;
    private bool _disposed;

    // Load hands the window its close: once a character is loaded there's nothing
    // left to do here.
    public event Action? CloseRequested;

    public ObservableCollection<string> Bbses { get; } = new();
    public ObservableCollection<ProfileManagerRow> Profiles { get; } = new();

    // Every character ticked in the (multi-select) list — drives "Launch": one
    // new client instance is spawned per selected character.
    public ObservableCollection<ProfileManagerRow> SelectedProfiles { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBbsSelection))]
    [NotifyPropertyChangedFor(nameof(AssignableBbses))]
    private string? _selectedBbs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfileSelection))]
    private ProfileManagerRow? _selectedProfile;

    [ObservableProperty] private string? _assignTargetBbs;

    // The selected BBS's realms (the middle column) and the one selected, whose
    // characters the right column lists.
    public ObservableCollection<string> Realms { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRealmSelection))]
    [NotifyPropertyChangedFor(nameof(CanRemoveRealm))]
    [NotifyPropertyChangedFor(nameof(CharactersHeader))]
    private string? _selectedRealm;

    public bool HasRealmSelection => SelectedRealm is not null;
    public bool CanRemoveRealm => SelectedRealm is not null && Realms.Count > 1;
    public string RealmsHeader => SelectedBbs is { } bbs ? $"Realms on {bbs}" : "Realms";
    public string CharactersHeader => SelectedRealm is { } realm ? $"Characters on {realm}" : "Characters";

    // The selected realm's game data, changed right here (committed at once).
    public IReadOnlyList<string> GameDataSetOptions { get; }
    [ObservableProperty] private string _realmGameDataSet = Settings.BbsSectionViewModel.GlobalDefaultSet;

    // Destination for "Move to realm" (any realm of the character's BBS).
    [ObservableProperty] private string? _assignTargetRealm;
    [ObservableProperty] private bool _isProfilesEmpty = true;
    [ObservableProperty] private string _currentProfileLabel = "No character loaded";

    // First-run tour: glow the Add buttons when the tour points at them.
    [ObservableProperty] private bool _highlightAddBbs;
    [ObservableProperty] private bool _highlightAddCharacter;

    public bool HasBbsSelection => SelectedBbs is not null;
    public bool HasProfileSelection => SelectedProfile is not null;

    // Destination choices for "Move to BBS" — every saved BBS except the one
    // the selected character already lives under.
    public IEnumerable<string> AssignableBbses =>
        Bbses.Where(b => !string.Equals(b, SelectedBbs, StringComparison.OrdinalIgnoreCase));

    public ProfileManagerViewModel(
        Func<bool> isDisconnected,
        Func<ProfileRef, Task> swapToProfile,
        Func<string?, Task> newProfile,
        Func<string?, Task> saveCurrent,
        Func<string?, Task> saveCurrentAs,
        Action<string, string?> editBbsSettings,
        Action<ProfileRef> nameCurrentDraft)
    {
        _isDisconnected = isDisconnected;
        _swapToProfile = swapToProfile;
        _newProfile = newProfile;
        _saveCurrent = saveCurrent;
        _saveCurrentAs = saveCurrentAs;
        _editBbsSettings = editBbsSettings;
        _nameCurrentDraft = nameCurrentDraft;
        _profile = AppServices.Current.Profile;
        _bbs = AppServices.Current.Bbs;
        _dialogs = AppServices.Current.Dialogs;
        _confirm = AppServices.Current.Confirm;
        _settings = AppServices.Current.Settings;
        _log = AppServices.Current.Log;
        _realms = AppServices.Current.Realms;
        GameDataSetOptions = new[] { Settings.BbsSectionViewModel.GlobalDefaultSet }
            .Concat(AppServices.Current.GameData.AvailableSets.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            .ToList();

        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileMutated += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedHandler;
        // A realm edit in Settings → BBS re-pins; pick up the renamed / added realms.
        _profile.BbsPinApplied += OnProfileChanged;
        // A BBS renamed in Settings → BBS (where Add BBS hands its "New BBS" for
        // naming) or removed there.
        _profile.BbsRenamed += OnBbsRenamed;
        _profile.BbsDeleted += OnBbsDeleted;
        AppServices.Current.TourActionChanged += OnTourActionChanged;
        OnTourActionChanged();

        ReloadBbses();
        // Land on the loaded character's BBS so its record is selected on open.
        if (_profile.CurrentBbsName is { } cur)
            SelectedBbs = Bbses.FirstOrDefault(b => string.Equals(b, cur, StringComparison.OrdinalIgnoreCase))
                          ?? Bbses.FirstOrDefault();
        else
            SelectedBbs = Bbses.FirstOrDefault();
        RefreshCurrentLabel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _profile.ProfileLoaded -= OnProfileChanged;
        _profile.ProfileMutated -= OnProfileChanged;
        _profile.ProfileClosed -= OnProfileClosedHandler;
        _profile.BbsPinApplied -= OnProfileChanged;
        _profile.BbsRenamed -= OnBbsRenamed;
        _profile.BbsDeleted -= OnBbsDeleted;
        AppServices.Current.TourActionChanged -= OnTourActionChanged;
    }

    private void OnTourActionChanged()
    {
        string? a = AppServices.Current.CurrentTourAction;
        HighlightAddBbs = a == GuideActions.AddBbs;
        HighlightAddCharacter = a == GuideActions.AddCharacter;
    }

    private void OnProfileChanged(CharacterProfile _) { RefreshCurrentLabel(); ReloadRealms(keep: SelectedRealm); }
    private void OnProfileClosedHandler() { RefreshCurrentLabel(); ReloadRealms(keep: SelectedRealm); }

    private void OnBbsRenamed(string oldName, string newName)
    {
        bool wasSelected = string.Equals(SelectedBbs, oldName, StringComparison.OrdinalIgnoreCase);
        ReloadBbses();
        if (wasSelected) SelectedBbs = newName;
        RefreshCurrentLabel();
    }

    private void OnBbsDeleted(string _) => ReloadBbses();

    partial void OnSelectedBbsChanged(string? value)
    {
        OnPropertyChanged(nameof(RealmsHeader));
        ReloadRealms(keep: null);
    }

    partial void OnSelectedRealmChanged(string? value)
    {
        LoadRealmGameDataSet();
        ReloadProfiles();
    }

    partial void OnSelectedProfileChanged(ProfileManagerRow? value) =>
        AssignTargetRealm = value?.Realm;

    // Commit the realm's game data as soon as it's picked; the loaded character's
    // realm re-pins so its game data switches.
    partial void OnRealmGameDataSetChanged(string value)
    {
        if (_suppressRealmEdits || SelectedBbs is not { } bbs || SelectedRealm is not { } realm) return;
        _realms.SetGameDataSet(bbs, realm, value == Settings.BbsSectionViewModel.GlobalDefaultSet ? null : value);
        RepinIfLoadedOn(bbs);
    }

    private void RefreshCurrentLabel() =>
        CurrentProfileLabel = _profile.Current is null
            ? "No character loaded"
            : _profile.CurrentProfileName is null
                ? (_profile.CurrentBbsName is { } draftBbs
                    ? $"{{default}} on {draftBbs} — not a character yet: nothing is kept until Save names it"
                    : "{default} — not a character yet: nothing is kept until Save names it")
                : $"{_profile.CurrentBbsName} / {_profile.CurrentProfileName}";

    private void ReloadBbses()
    {
        string? keep = SelectedBbs;
        Bbses.Clear();
        foreach (string name in _bbs.ListNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            Bbses.Add(name);
        OnPropertyChanged(nameof(AssignableBbses));
        if (keep is not null && Bbses.Contains(keep, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(SelectedBbs, keep, StringComparison.Ordinal)) SelectedBbs = keep;
        }
        else if (SelectedBbs is not null) SelectedBbs = Bbses.FirstOrDefault();
    }

    // Fill the realm column for the selected BBS, keeping `keep` selected when it's
    // still there, else the loaded character's realm (on its own BBS), else the first.
    private void ReloadRealms(string? keep)
    {
        Realms.Clear();
        BbsProfile? board = SelectedBbs is { } bbs ? _bbs.Get(bbs) : null;
        foreach (RealmProfile realm in board?.Realms ?? new()) Realms.Add(realm.Name);
        string? loadedRealm = board is not null
            && string.Equals(_profile.CurrentBbsName, board.Name, StringComparison.OrdinalIgnoreCase)
                ? board.RealmFor(_profile.Current?.Realm)?.Name : null;
        string? pick = board?.RealmFor(keep ?? loadedRealm)?.Name;
        if (string.Equals(SelectedRealm, pick, StringComparison.Ordinal))
        {
            LoadRealmGameDataSet();
            ReloadProfiles();
        }
        else SelectedRealm = pick;
        OnPropertyChanged(nameof(CanRemoveRealm));
    }

    private void LoadRealmGameDataSet()
    {
        string? set = SelectedBbs is { } bbs && SelectedRealm is { } realm
            ? _bbs.Get(bbs)?.RealmFor(realm)?.ActiveGameDataSet : null;
        _suppressRealmEdits = true;
        RealmGameDataSet = set is not null && GameDataSetOptions.Contains(set)
            ? set : Settings.BbsSectionViewModel.GlobalDefaultSet;
        _suppressRealmEdits = false;
    }

    // The characters on the selected realm (a character with no / an unknown realm
    // plays the BBS's first).
    private void ReloadProfiles()
    {
        Profiles.Clear();
        if (SelectedBbs is { } bbs && SelectedRealm is { } selectedRealm)
        {
            BbsProfile? board = _bbs.Get(bbs);
            foreach (ProfileRef r in _profile.ListAll()
                         .Where(r => string.Equals(r.Bbs, bbs, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                string? realm = board?.RealmFor(_profile.RealmOf(r))?.Name;
                if (!string.Equals(realm, selectedRealm, StringComparison.OrdinalIgnoreCase)) continue;
                bool isCurrent = string.Equals(_profile.CurrentBbsName, r.Bbs, StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(_profile.CurrentProfileName, r.Name, StringComparison.Ordinal);
                Profiles.Add(new ProfileManagerRow(r, isCurrent, realm));
            }
        }
        IsProfilesEmpty = Profiles.Count == 0;
    }

    // A realm change on the loaded character's BBS (rename, removal, game data)
    // re-pins so its realm stores and game data follow.
    private void RepinIfLoadedOn(string bbs)
    {
        if (string.Equals(_profile.CurrentBbsName, bbs, StringComparison.OrdinalIgnoreCase))
            _profile.NotifyBbsPinApplied();
    }

    // ----- Realms ---------------------------------------------------------

    [RelayCommand]
    private void AddRealm()
    {
        if (SelectedBbs is not { } bbs || _realms.Add(bbs) is not { } name) return;
        ReloadRealms(keep: name);
    }

    [RelayCommand]
    private async Task RenameRealmAsync()
    {
        if (SelectedBbs is not { } bbs || SelectedRealm is not { } oldName) return;
        ProfileNameInputDialogViewModel vm = new(
            oldName,
            n => !string.Equals(n, oldName, StringComparison.OrdinalIgnoreCase)
                 && Realms.Contains(n, StringComparer.OrdinalIgnoreCase));
        string? newName = await _dialogs.OpenWindowAsync<ProfileNameInputDialogViewModel, string>(vm);
        if (string.IsNullOrWhiteSpace(newName)) return;
        if (_realms.Rename(bbs, oldName, newName) is { } problem)
        {
            _dialogs.ShowInfo("Realm not renamed", problem);
            return;
        }
        RepinIfLoadedOn(bbs);
        ReloadRealms(keep: newName.Trim());
    }

    [RelayCommand]
    private async Task RemoveRealmAsync()
    {
        if (SelectedBbs is not { } bbs || SelectedRealm is not { } name || Realms.Count <= 1) return;
        bool loadedOnIt = string.Equals(_profile.CurrentBbsName, bbs, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_bbs.Get(bbs)?.RealmFor(_profile.Current?.Realm)?.Name, name,
                StringComparison.OrdinalIgnoreCase);
        if (loadedOnIt && !EnsureDisconnected("remove the realm your loaded character plays")) return;
        // Characters on the realm are deleted with it, so that confirmation always
        // shows; an empty realm follows the "confirm deletes" setting.
        IReadOnlyList<ProfileRef> characters = _realms.CharactersOn(bbs, name);
        (string body, string yes) = RealmCatalog.RemovalPrompt(bbs, name, characters);
        bool confirmed = characters.Count > 0
            ? await _confirm.ConfirmAsync("Remove realm", body, yes)
            : await _confirm.ConfirmDeleteAsync($"the realm “{name}” from “{bbs}”");
        if (!confirmed) return;
        if (!_realms.Remove(bbs, name)) return;
        RepinIfLoadedOn(bbs);
        ReloadRealms(keep: null);
    }

    // Realm settings (menu commands, death floor, cleanup time, currency name) are
    // edited in Settings → BBS + Display; open it on this realm.
    [RelayCommand]
    private void EditRealm()
    {
        if (SelectedBbs is { } bbs) _editBbsSettings(bbs, SelectedRealm);
    }

    // Put the selected character on the picked realm of its BBS. The loaded one
    // must be disconnected first, since its game data and realm stores switch.
    [RelayCommand]
    private void AssignToRealm()
    {
        if (SelectedProfile is not { } row || AssignTargetRealm is not { } realm) return;
        if (string.Equals(row.Realm, realm, StringComparison.OrdinalIgnoreCase)) return;
        if (row.IsCurrent && !EnsureDisconnected("move the loaded character to another realm")) return;
        _profile.AssignRealm(row.Ref, realm);
        if (row.IsCurrent) _profile.NotifyBbsPinApplied();
        _log?.Info("Profile", $"Assigned '{row.Name}' to realm '{realm}' on '{row.Ref.Bbs}'.");
        ReloadProfiles();
    }

    // Refuse an op that would strand the live in-game session, pointing the user
    // at the fix. Returns true only when it's safe to proceed (disconnected).
    private bool EnsureDisconnected(string action)
    {
        if (_isDisconnected()) return true;
        _dialogs.ShowInfo("Disconnect first", $"Disconnect from the BBS before you {action}.");
        return false;
    }

    private string DeriveUniqueName(string bbs, string baseName)
    {
        if (!_profile.Exists(bbs, baseName)) return baseName;
        for (int n = 2; ; n++)
        {
            string candidate = $"{baseName} {n}";
            if (!_profile.Exists(bbs, candidate)) return candidate;
        }
    }

    // ----- BBS list -------------------------------------------------------

    [RelayCommand]
    private void AddBbs()
    {
        AppServices.Current.NotifyTourAction?.Invoke(GuideActions.AddBbs);
        string baseName = "New BBS";
        string name = baseName;
        int n = 2;
        while (_bbs.Exists(name)) name = $"{baseName} {n++}";
        _bbs.Save(new BbsProfile { Name = name, Host = string.Empty, Port = 23 });
        _log?.Info("BBS", $"Added BBS '{name}'.");
        ReloadBbses();
        SelectedBbs = name;
        // A fresh record has a placeholder name and no host — useless until it's
        // filled in, and nothing else in this window would tell the user that.
        // Hand straight off to the editor rather than leaving a dead entry.
        _editBbsSettings(name, null);
    }

    // Structural ops (add / rename / remove) live here; everything else about a
    // BBS — host, port, redial, terminal size, logon steps — is the Settings BBS
    // tab, so this jumps there with the selected record already loaded.
    [RelayCommand]
    private void EditBbs()
    {
        if (SelectedBbs is not { } name) return;
        _editBbsSettings(name, null);
    }

    [RelayCommand]
    private async Task RemoveBbsAsync()
    {
        if (SelectedBbs is not { } name) return;
        int count = _profile.ListAll().Count(r => string.Equals(r.Bbs, name, StringComparison.OrdinalIgnoreCase));
        string scope = count == 0
            ? $"the BBS '{name}'"
            : $"the BBS '{name}' and all {count} character{(count == 1 ? "" : "s")} saved under it";
        bool deletingCurrent = _profile.CurrentProfileName is not null
            && string.Equals(_profile.CurrentBbsName, name, StringComparison.OrdinalIgnoreCase);
        if (deletingCurrent && !EnsureDisconnected("remove the BBS your loaded character lives on")) return;
        if (!await _confirm.ConfirmDeleteAsync(scope)) return;

        if (deletingCurrent) _profile.Close();   // no save — else the outgoing save resurrects the folder
        _bbs.Delete(name);
        _profile.NotifyBbsDeleted(name);
        _log?.Info("BBS", $"Removed BBS '{name}'.");
        if (deletingCurrent) _profile.LoadDefaultProfile();
        // A default draft sitting on the removed BBS moves off it too.
        else if (_profile.CurrentProfileName is null
                 && string.Equals(_profile.CurrentBbsName, name, StringComparison.OrdinalIgnoreCase))
            _profile.LoadDefaultProfile();
        ReloadBbses();
        SelectedBbs = Bbses.FirstOrDefault();
    }

    [RelayCommand]
    private async Task RenameBbsAsync()
    {
        if (SelectedBbs is not { } oldName) return;
        ProfileNameInputDialogViewModel vm = new(
            oldName,
            n => !string.Equals(n, oldName, StringComparison.OrdinalIgnoreCase) && _bbs.Exists(n));
        string? newName = await _dialogs.OpenWindowAsync<ProfileNameInputDialogViewModel, string>(vm);
        if (string.IsNullOrWhiteSpace(newName)
            || string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase)) return;
        if (_bbs.Exists(newName))
        {
            _dialogs.ShowInfo("BBS not renamed", $"A BBS named “{newName}” already exists.");
            return;
        }
        if (!_bbs.Rename(oldName, newName))
        {
            _dialogs.ShowInfo("BBS not renamed", $"“{oldName}” couldn't be renamed to “{newName}”.");
            return;
        }
        _profile.RenameBbs(oldName, newName);
        RecentProfileList.RekeyBbs(_settings, oldName, newName);
        _log?.Info("BBS", $"Renamed BBS '{oldName}' → '{newName}'.");
        ReloadBbses();
        SelectedBbs = newName;
    }

    // ----- Characters -----------------------------------------------------

    [RelayCommand]
    private async Task AddProfileAsync()
    {
        if (SelectedBbs is not { } bbs) return;
        AppServices.Current.NotifyTourAction?.Invoke(GuideActions.AddCharacter);
        ProfileNameInputDialogViewModel vm = new("character", n => _profile.Exists(bbs, n));
        string? name = await _dialogs.OpenWindowAsync<ProfileNameInputDialogViewModel, string>(vm);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_profile.Exists(bbs, name))
        {
            _dialogs.ShowInfo("Character not created",
                $"A character named “{name}” already exists on “{bbs}”.");
            return;
        }
        ProfileRef added = new(bbs, name);

        // On the unnamed default profile, the character added is the one to play
        // (user, 2026-10-14: "if they're on default and they hit add ... that
        // profile should just be loaded and then it closes the profile management
        // after the load"). Adding used to leave the client on the unnamed profile,
        // where a new player stayed. It is done by naming what is loaded: the same
        // character a fresh one would be, plus anything already set up on the
        // unnamed profile, which naming keeps and loading another would have asked
        // about. On a named character, Add only adds, and the window stays open.
        if (_profile.Current is not null && _profile.CurrentProfileName is null)
        {
            _nameCurrentDraft(added);
            if (SelectedRealm is { } playing) _profile.AssignRealm(added, playing);
            RepinIfLoadedOn(bbs);
            AppServices.Current.NotifyTourAction?.Invoke(GuideActions.CharacterAdded);
            AppServices.Current.Log.Info("Profile",
                $"Added '{name}' on '{bbs}' from the unnamed profile, which it now is; Profile Management closed.");
            CloseRequested?.Invoke();
            return;
        }

        _profile.CreateProfile(bbs, name);
        // A new character plays the realm selected in the middle column.
        if (SelectedRealm is { } realm) _profile.AssignRealm(added, realm);
        AppServices.Current.NotifyTourAction?.Invoke(GuideActions.CharacterAdded);
        ReloadProfiles();
        SelectedProfile = Profiles.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
    }

    // Make a new character from a MegaMUD character file. The file is read and
    // shown as a review (what comes across, what doesn't, and why) before anything
    // is written; accepting it creates the character on the selected BBS and realm.
    [RelayCommand]
    private async Task ImportMegaMudProfileAsync()
    {
        if (SelectedBbs is not { } bbs) return;
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        IReadOnlyList<Avalonia.Platform.Storage.IStorageFile> picked =
            await main.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Import MegaMUD profile",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("MegaMUD character (*.ini)") { Patterns = new[] { "*.ini" } },
                    Avalonia.Platform.Storage.FilePickerFileTypes.All,
                },
            });
        if (picked.Count == 0) return;
        string path = picked[0].Path.LocalPath;

        MegaMudImportPlan plan;
        try
        {
            // MegaMUD writes the file in the Windows ANSI code page; Latin-1 reads
            // every byte of it without loss.
            string text = await File.ReadAllTextAsync(path, System.Text.Encoding.Latin1);
            plan = MegaMudProfileImporter.Read(MegaMudIni.Parse(text), Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowInfo("Couldn't read the file", ex.Message);
            return;
        }
        if (!plan.Lines.Any(static l => l.WasImported))
        {
            _dialogs.ShowInfo("Nothing to import",
                "That file doesn't look like a MegaMUD character file: none of its settings were recognised.");
            return;
        }

        string? realm = SelectedRealm;
        bool bbsHasCharacters = _profile.ListAll().Any(r => string.Equals(r.Bbs, bbs, StringComparison.OrdinalIgnoreCase));
        MegaMudImportDialogViewModel review = new(plan, Path.GetFileName(path), bbs, realm, n => _profile.Exists(bbs, n),
            bbsHasCharacters);
        MegaMudImportChoice? choice = await _dialogs.OpenWindowAsync<MegaMudImportDialogViewModel, MegaMudImportChoice>(review);
        if (choice is null || _profile.Exists(bbs, choice.Name)) return;

        _profile.CreateProfile(bbs, choice.Name,
            fresh => plan.ApplyTo(fresh, bbs, choice.ImportLogin, AppServices.Current.Passwords, choice.Edits));
        if (realm is not null) _profile.AssignRealm(new ProfileRef(bbs, choice.Name), realm);
        if (choice.ApplyBbsSettings && _bbs.Get(bbs) is { } board)
        {
            plan.ApplyToBbs(board, choice.Edits);
            _bbs.Save(board);
        }
        AppServices.Current.Log.Info("Profile",
            $"Imported MegaMUD profile '{Path.GetFileName(path)}' as '{choice.Name}' on '{bbs}': "
            + $"{plan.Lines.Count(static l => l.WasImported)} setting(s) carried over, "
            + $"{plan.Lines.Count(static l => !l.WasImported)} left behind, {choice.Edits.Count} changed in the review, login {(choice.ImportLogin ? "stored" : "not stored")}, "
            + $"BBS redial and cleanup settings {(choice.ApplyBbsSettings ? "applied" : "left as they were")}.");
        ReloadProfiles();
        SelectedProfile = Profiles.FirstOrDefault(r => string.Equals(r.Name, choice.Name, StringComparison.Ordinal));
    }

    [RelayCommand]
    private async Task RenameProfileAsync()
    {
        if (SelectedBbs is not { } bbs || SelectedProfile is not { } row) return;
        string oldName = row.Ref.Name;
        if (row.IsCurrent && !EnsureDisconnected("rename the loaded character")) return;
        ProfileNameInputDialogViewModel vm = new(
            oldName,
            n => !string.Equals(n, oldName, StringComparison.Ordinal) && _profile.Exists(bbs, n));
        string? newName = await _dialogs.OpenWindowAsync<ProfileNameInputDialogViewModel, string>(vm);
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, oldName, StringComparison.Ordinal)) return;
        if (_profile.Exists(bbs, newName))
        {
            _dialogs.ShowInfo("Character not renamed",
                $"A character named “{newName}” already exists on “{bbs}”.");
            return;
        }
        bool wasCurrent = row.IsCurrent;
        _profile.MoveProfile(bbs, oldName, bbs, newName);
        if (wasCurrent) _profile.NotifyMutated();   // title / bindings refresh (same-BBS rename)
        ReloadProfiles();
        SelectedProfile = Profiles.FirstOrDefault(r => string.Equals(r.Name, newName, StringComparison.Ordinal));
    }

    [RelayCommand]
    private async Task CopyProfileAsync()
    {
        if (SelectedBbs is not { } bbs || SelectedProfile is not { } row) return;
        string from = row.Ref.Name;
        string suggested = $"{from} copy";
        ProfileNameInputDialogViewModel vm = new(suggested, n => _profile.Exists(bbs, n));
        string? name = await _dialogs.OpenWindowAsync<ProfileNameInputDialogViewModel, string>(vm);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_profile.Exists(bbs, name))
        {
            _dialogs.ShowInfo("Character not copied",
                $"A character named “{name}” already exists on “{bbs}”.");
            return;
        }
        _profile.CopyProfile(bbs, from, name);
        ReloadProfiles();
        SelectedProfile = Profiles.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
    }

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        if (SelectedBbs is not { } bbs || SelectedProfile is not { } row) return;
        if (row.IsCurrent && !EnsureDisconnected("delete the loaded character")) return;
        if (!await _confirm.ConfirmDeleteAsync($"the character '{row.Ref.Name}' on '{bbs}'")) return;
        _profile.DeleteProfile(bbs, row.Ref.Name);   // deleting current reloads the default (fires events → refresh)
        ReloadProfiles();
    }

    // The list is multi-select. Load brings the whole selection online, using
    // this client when it's free and spawning new instances (the --profile
    // multi-instance path) for the rest — so ticking four characters and
    // hitting Load leaves you with four running clients, one per character.
    //   • This client is DISCONNECTED (idle) → reuse it for the FIRST selection
    //     (a swap in place); each of the rest opens in its own new client.
    //   • This client is CONNECTED (actively playing) → don't disturb it: every
    //     selection opens in its own new client, instead of the jarring
    //     disconnect → swap → reconnect the old flow would force.
    [RelayCommand]
    private async Task LoadAsync()
    {
        List<ProfileManagerRow> targets = SelectedProfiles.Count > 0
            ? SelectedProfiles.OrderBy(r => Profiles.IndexOf(r)).ToList()
            : SelectedProfile is { } one ? new List<ProfileManagerRow> { one } : new();
        if (targets.Count == 0) return;

        if (!_isDisconnected())
        {
            LaunchNewClients(targets);   // playing → leave this client be
        }
        else
        {
            // Idle client: the first selection loads here (already loaded → no-op),
            // the rest open new.
            ProfileManagerRow first = targets[0];
            if (!first.IsCurrent) await _swapToProfile(first.Ref);
            if (targets.Count > 1) LaunchNewClients(targets.Skip(1).ToList());
        }
        CloseRequested?.Invoke();
    }

    private void LaunchNewClients(IReadOnlyList<ProfileManagerRow> targets)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            _dialogs.ShowInfo("Load", "Couldn't locate the MudPlay executable to launch new clients.");
            return;
        }

        int launched = 0;
        foreach (ProfileManagerRow row in targets)
        {
            try
            {
                ProcessStartInfo psi = new(exe) { UseShellExecute = false };
                psi.ArgumentList.Add("--profile");
                psi.ArgumentList.Add($"{row.Ref.Bbs}/{row.Ref.Name}");
                Process.Start(psi);
                launched++;
            }
            catch (Exception ex)
            {
                _log?.Warn("Profile",
                    $"Failed to launch '{row.Ref.Name}' on '{row.Ref.Bbs}': {ex.Message}");
            }
        }
        if (launched > 0)
            _log?.Info("Profile", $"Launched {launched} client(s) from Profile Management.");
    }

    [RelayCommand]
    private async Task AssignToBbsAsync()
    {
        if (SelectedBbs is not { } fromBbs || SelectedProfile is not { } row) return;
        if (AssignTargetBbs is not { } toBbs || string.Equals(fromBbs, toBbs, StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.ShowInfo("Move to BBS", "Pick a different destination BBS.");
            return;
        }
        if (row.IsCurrent && !EnsureDisconnected("move the loaded character to another BBS")) return;

        string name = row.Ref.Name;
        string targetName = name;
        if (_profile.Exists(toBbs, targetName))
        {
            // Name clash on the destination — prompt for a fresh name.
            ProfileNameInputDialogViewModel vm = new(DeriveUniqueName(toBbs, name), n => _profile.Exists(toBbs, n));
            string? chosen = await _dialogs.OpenWindowAsync<ProfileNameInputDialogViewModel, string>(vm);
            if (string.IsNullOrWhiteSpace(chosen)) return;
            if (_profile.Exists(toBbs, chosen))
            {
                _dialogs.ShowInfo("Not moved",
                    $"A character named “{chosen}” already exists on “{toBbs}”.");
                return;
            }
            targetName = chosen;
        }

        bool wasCurrent = row.IsCurrent;
        _profile.MoveProfile(fromBbs, name, toBbs, targetName);
        // Realm names belong to the old board: start on the new board's first realm.
        _profile.AssignRealm(new ProfileRef(toBbs, targetName), null);
        if (wasCurrent) { _profile.NotifyMutated(); _profile.NotifyBbsPinApplied(); }   // active BBS changed
        _log?.Info("Profile", targetName == name
            ? $"Assigned '{name}' to BBS '{toBbs}'."
            : $"Assigned '{name}' to BBS '{toBbs}' as '{targetName}'.");
        ReloadProfiles();
    }

    // ----- Current-profile lifecycle (the collapsed File-menu actions) ----

    [RelayCommand]
    private async Task NewCurrentAsync()
    {
        if (!EnsureDisconnected("start a new character")) return;
        // The draft lands on the BBS picked here, not the first one on disk.
        await _newProfile(SelectedBbs);
        RefreshAfterCurrentChange();
    }

    // On the default draft this asks for a name, under the BBS selected here, and
    // the list gains a character.
    [RelayCommand]
    private async Task SaveCurrentAsync()
    {
        await _saveCurrent(SelectedBbs);
        RefreshAfterCurrentChange();
    }

    [RelayCommand]
    private async Task SaveCurrentAsAsync()
    {
        await _saveCurrentAs(SelectedBbs);
        RefreshAfterCurrentChange();
    }

    private void RefreshAfterCurrentChange()
    {
        ReloadBbses();
        ReloadProfiles();
        RefreshCurrentLabel();
    }
}
