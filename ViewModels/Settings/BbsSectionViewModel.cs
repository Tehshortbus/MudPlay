using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.Profile;
using FujinTerm.Models.Settings;
using FujinTerm.Services;
using FujinTerm.ViewModels.Profile;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

// "BBS" tab. Owns the list of saved BBS records (globally shared across every
// character) and the field-editor for whichever one is selected. Per-character
// credentials (username, password, menu-nav sequence) live on the character
// profile.
//
// Apply walks the cached in-memory BBS profiles and persists every dirty one.
// Discard reloads the currently-selected BBS from disk so pending edits are
// dropped. Adding / deleting a BBS commits immediately (those are structural, not
// field-level edits — the OK / Cancel commit only covers field tweaks).
public sealed partial class BbsSectionViewModel : SettingsSectionViewModel
{
    private readonly BbsProfileStore _bbsStore;
    private readonly ProfileService _profile;
    private readonly PasswordProtector _passwords;
    private readonly DisplayConfig _display;
    private readonly SettingsService _globalSettings;
    private readonly Dictionary<string, BbsProfile> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private string? _pendingPassword;          // null = unchanged; "" = clear; else write
    private bool _suppressDirty = true;
    private bool _dirty;
    private Control? _view;

    public override string Id => "bbs";
    public override string Title => "BBS + Display";
    public override bool IsDirty => _dirty;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "BBS", "Host", "Port", "Telnet", "Redial", "Cleanup", "Reconnect",
        "Sysop", "Terminal", "Cols", "Rows", "NAWS", "Connection",
        "Game entry command", "Game exit command", "Enter realm", "Logoff",
        "Player dies at", "Death floor", "Bleeding out", "Dropped", "Hangup HP",
        "Auto-refine death floor", "Trace death floor", "Slow death", "Learn floor",
        "Disconnect pattern", "Party disconnect", "Logoff pattern", "Logs off",
        "Player disconnect line",
        "Display", "Scrollback", "Backscroll", "Buffer",
        "Confirm", "Confirm exit", "Confirm hangup", "Confirm save", "Confirm delete",
    };

    public override Control View => _view ??= new BbsSectionView { DataContext = this };

    // Names of every saved BBS profile (left rail of the tab).
    public ObservableCollection<string> AvailableBbsNames { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private string? _selectedBbsName;

    public bool HasSelection => SelectedBbsName is not null;

    // ----- Editable fields, populated from the selected BbsProfile -----
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private int _port = 23;
    [ObservableProperty] private int _maxRedials = 3;
    [ObservableProperty] private int _redialPauseSeconds = 5;
    [ObservableProperty] private int _cleanupPeriodMinutes;
    [ObservableProperty] private int _noResponseTimeoutSeconds;
    [ObservableProperty] private bool _reconnectOnFailedConnect;
    [ObservableProperty] private bool _reconnectOnCarrierLost;
    [ObservableProperty] private bool _reconnectOnNoResponse;
    [ObservableProperty] private bool _reconnectAfterCleanup;
    [ObservableProperty] private bool _hasSysopPowers;
    [ObservableProperty] private int _terminalCols = 80;
    [ObservableProperty] private int _terminalRows = 25;
    [ObservableProperty] private int _scrollbackLines = 4_000;

    // ----- Game-menu commands (per-BBS) -----
    // The two main-menu picks for entering / leaving the realm. Stored
    // per-BBS because the menu key bindings are a property of the realm /
    // front-end, not the character.
    [ObservableProperty] private string _gameEntryCommand = "E";
    [ObservableProperty] private string _gameExitCommand = "=x";

    // ----- Realm mechanics (per-BBS) -----
    // The negative-HP floor at which a character actually dies (0 HP only drops
    // you into a revivable bleed-out). The emergency auto-hangup reads it to
    // keep firing through the whole bleeding-out window. Seeded at the standard
    // -25.
    [ObservableProperty] private int _playerDiesAtHp = -25;

    // When on, the death-floor tracer refines PlayerDiesAtHp from observed slow
    // deaths (a bleed-out lands right at the true floor). Off pins the manual
    // value. Default on.
    [ObservableProperty] private bool _autoRefineDeathFloor = true;

    // Board-specific player-disconnect line (see BbsProfile.DisconnectPattern).
    // Optional literal pattern — {name} captures the disconnecting player, *
    // swallows a varying run. Empty = only the built-in "just disconnected" /
    // "just hung up" forms are watched.
    [ObservableProperty] private string? _disconnectPattern;

    // Per-BBS label for the top (runic) denomination — some realms rename it,
    // which changes both the coin wording the server sends and the keyword the
    // client keys currency commands on. Blank falls back to "runic" on save.
    [ObservableProperty] private string _runicCurrencyName = "runic";

    // ----- Per-character credentials -----
    // True when any character profile is loaded — including unsaved drafts.
    // Credentials, sysop flag, and menu nav all bind against the in-memory
    // CharacterProfile; the password is encrypted with the per-user .credkey (not
    // anything keyed on the profile name) so an unsaved draft can carry them
    // forward into its first Save just fine. Only used now to dim the credentials
    // block when literally no profile object exists (a state we never actually
    // reach at runtime, but the guard keeps designer-time previews honest).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CredentialsHint))]
    [NotifyPropertyChangedFor(nameof(IsCredentialsHintWarning))]
    private bool _hasProfile;

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _showPassword;

    // Suicide-password display only — captured passively by
    // SuicidePasswordTracker when the user runs `set suicide` in-game.
    // No editor; the BBS-tab field is read-only and hidden when nothing
    // is stored. ShowSuicidePassword toggles the obfuscation char.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuicidePassword))]
    private string _suicidePassword = string.Empty;

    [ObservableProperty] private bool _showSuicidePassword;

    // True when the loaded profile carries a stored suicide password.
    public bool HasSuicidePassword => !string.IsNullOrEmpty(SuicidePassword);

    // ----- Confirm prompts (Global tier — install-wide UX preferences) -----
    // Persisted in GlobalSettings.Settings["Confirm"] and mirrored live
    // onto AppServices.Current.Confirm by ApplyConfirmFromGlobalSettings.
    // Explicit `= false` defaults — fresh installs / first-open of this
    // tab render every checkbox unchecked so no nagging dialogs land on
    // a user who hasn't asked for them.
    [ObservableProperty] private bool _confirmExit = false;
    [ObservableProperty] private bool _confirmHangup = false;
    [ObservableProperty] private bool _confirmSaveSettings = false;
    [ObservableProperty] private bool _confirmDeletes = false;

    // Editable rows for the per-character menu-nav sequence.
    public ObservableCollection<MenuStepEditorViewModel> MenuNavSteps { get; } = new();

    // Logon sequences from other saved characters, offered as import sources so a
    // new (or additional) character doesn't have to retype a flow another
    // character already worked out. Every character is listed, not just ones on
    // this BBS — some BBSes share a front-end, so a cross-BBS flow is often a
    // mostly-right starting point. Rebuilt whenever the selected BBS or loaded
    // profile changes.
    public ObservableCollection<MenuNavImportOption> ImportSourceOptions { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportMenuNavCommand))]
    private MenuNavImportOption? _selectedImportSource;

    // Drives the picker's enabled state — false hides / greys the import row when
    // no other character has any logon steps to borrow.
    public bool HasImportSources => ImportSourceOptions.Count > 0;

    // Helper text under the credentials section.
    public string CredentialsHint
    {
        get
        {
            if (!HasProfile)
                return "Load or create a profile to edit credentials.";
            return _profile.CurrentProfileName is { } name
                ? $"For character: {name}"
                : "(default profile - You haven't saved this profile)";
        }
    }

    // True when the credentials hint should be drawn in a warning color (e.g.,
    // red) — currently only for the unsaved-draft case, so the user can see at a
    // glance that their edits won't persist until they Save / Save As.
    public bool IsCredentialsHintWarning =>
        HasProfile && _profile.CurrentProfileName is null;

    public BbsSectionViewModel(
        BbsProfileStore bbsStore,
        ProfileService profile,
        PasswordProtector passwords,
        DisplayConfig display,
        SettingsService globalSettings)
    {
        ArgumentNullException.ThrowIfNull(bbsStore);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(passwords);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(globalSettings);
        _bbsStore = bbsStore;
        _profile = profile;
        _passwords = passwords;
        _display = display;
        _globalSettings = globalSettings;

        Action<CharacterProfile> onProfileLoaded = _ => RefreshProfileState();
        // SuicidePasswordTracker writes a new encrypted blob and
        // calls NotifyMutated on commit; pick that up so the
        // Settings → BBS field reflects the freshly-captured value
        // without requiring the user to reload the section.
        Action<CharacterProfile> onProfileMutated = _ => RefreshSuicidePassword();
        _profile.ProfileLoaded += onProfileLoaded;
        _profile.ProfileClosed += RefreshProfileState;
        _profile.ProfileMutated += onProfileMutated;
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= onProfileLoaded;
            _profile.ProfileClosed -= RefreshProfileState;
            _profile.ProfileMutated -= onProfileMutated;
        });
        RefreshProfileState();
        LoadConfirmFromGlobalSettings();

        ReloadBbsList();
        // Default selection to the loaded character's active BBS when it's
        // in the list — re-entering settings should land on the BBS the
        // user is currently dialed at.
        string? preferred = _profile.CurrentBbsName;
        SelectedBbsName = preferred is not null && AvailableBbsNames.Contains(preferred)
            ? preferred
            : AvailableBbsNames.FirstOrDefault();
        // OnSelectedBbsNameChanged short-circuits while _suppressDirty is
        // true (so the initial property assignment doesn't mark dirty), so
        // we have to call ReloadSelected ourselves here. Without this, the
        // editor stays blank until the user clicks a different BBS — even
        // when there's only one in the list and it's already selected.
        if (SelectedBbsName is not null) ReloadSelected();
        _suppressDirty = false;

        // If the auto-picked selection doesn't match the profile's current
        // pin — common case: blank draft (BbsName null) on first open with
        // one BBS in the list — mark dirty so OK stamps the pin even when
        // the user doesn't touch any field.
        if (!string.Equals(SelectedBbsName, _profile.CurrentBbsName, StringComparison.OrdinalIgnoreCase))
        {
            Dirty();
        }
    }

    public override void Apply()
    {
        // Rename pass: if the Name field differs from the selected key, the
        // user retitled this BBS. Move the on-disk file + cache entry and
        // refresh the selection so the list shows the new name.
        if (SelectedBbsName is { } oldName
            && !string.IsNullOrWhiteSpace(Name)
            && !string.Equals(oldName, Name, StringComparison.OrdinalIgnoreCase))
        {
            RenameSelected(oldName, Name);
        }

        foreach (BbsProfile profile in _loaded.Values)
        {
            // The website is now edited under Settings → Toolbar + Shortcuts and
            // may have just been written there in the same OK. Re-read the
            // on-disk value into this cached copy so our save folds it in rather
            // than clobbering it with the WebsiteUrl loaded at selection time.
            if (_bbsStore.Get(profile.Name) is { WebsiteUrl: var url })
                profile.WebsiteUrl = url;
            _bbsStore.Save(profile);
        }

        ApplyToCurrentProfile();
        SaveConfirmToGlobalSettings();

        ClearDirty();
    }

    // Hydrate the four Confirm* observables from the Global-tier settings file.
    // Runs once at ctor time; Discard re-runs it to roll back unsaved edits.
    private void LoadConfirmFromGlobalSettings()
    {
        ConfirmSettings dto = new();
        Dictionary<string, System.Text.Json.JsonElement>? bucket =
            _globalSettings.Current.Settings;
        if (bucket is not null
            && bucket.TryGetValue("Confirm", out System.Text.Json.JsonElement json))
        {
            try
            {
                dto = System.Text.Json.JsonSerializer.Deserialize<ConfirmSettings>(json) ?? new();
            }
            catch
            {
                dto = new ConfirmSettings();
            }
        }
        bool prev = _suppressDirty;
        _suppressDirty = true;
        ConfirmExit         = dto.ConfirmExit;
        ConfirmHangup       = dto.ConfirmHangup;
        ConfirmSaveSettings = dto.ConfirmSaveSettings;
        ConfirmDeletes      = dto.ConfirmDeletes;
        _suppressDirty = prev;
    }

    // Persist the four Confirm* observables back into the Global tier and trigger
    // the live mirror via SettingsService.GlobalSettingsChanged.
    private void SaveConfirmToGlobalSettings()
    {
        ConfirmSettings dto = new()
        {
            ConfirmExit         = ConfirmExit,
            ConfirmHangup       = ConfirmHangup,
            ConfirmSaveSettings = ConfirmSaveSettings,
            ConfirmDeletes      = ConfirmDeletes,
        };
        _globalSettings.Current.Settings ??= new Dictionary<string, System.Text.Json.JsonElement>();
        _globalSettings.Current.Settings["Confirm"] =
            System.Text.Json.JsonSerializer.SerializeToElement(dto);
        _globalSettings.Save();
    }

    // Push the BBS section's character-side decisions onto the loaded profile. The
    // BBS link is now the folder the profile lives under (there's no BbsName field
    // on the DTO), so "pinning a BBS" means one of three things depending on
    // profile state:
    //   • Blank draft → ProfileService.PinDraftBbs records the home BBS so the
    //     draft's first Save lands under it.
    //   • Named profile, same BBS → nothing to move; just re-commit credentials.
    //   • Named profile, different BBS → re-home the on-disk folder. No name clash
    //     in the destination → silent move now. Clash → prompt for a new name
    //     asynchronously and finish the move in the continuation (Apply itself
    //     stays synchronous; the Settings window may close while the prompt is up).
    // Every path ends in CommitCredentials, which writes the per-BBS credential
    // slice and fires the mutate / pin notifications.
    private void ApplyToCurrentProfile()
    {
        if (SelectedBbsName is not { } bbs) return;
        CharacterProfile? character = _profile.Current;
        if (character is null) return;

        // Blank draft: just record the home BBS; there's no folder to move.
        if (_profile.CurrentProfileName is null)
        {
            _profile.PinDraftBbs(bbs);
            CommitCredentials(bbs, character);
            return;
        }

        // Named profile staying on its current BBS: no move needed.
        if (string.Equals(bbs, _profile.CurrentBbsName, StringComparison.OrdinalIgnoreCase))
        {
            CommitCredentials(bbs, character);
            return;
        }

        // Named profile changing BBS → re-home. A same-named profile already
        // under the destination BBS forces a rename (async-after-Apply);
        // otherwise the move is silent and immediate.
        if (_profile.Exists(bbs, _profile.CurrentProfileName))
        {
            _ = ReHomeWithRenameAsync(bbs, character);
        }
        else
        {
            _profile.ReHome(bbs);
            CommitCredentials(bbs, character);
        }
    }

    // Re-home flow for the name-clash case: prompt the user for a fresh profile
    // name in the destination BBS, then move + commit. Fire-and-forget from
    // ApplyToCurrentProfile so Apply stays synchronous; cancelling the prompt
    // leaves the profile where it is.
    private async Task ReHomeWithRenameAsync(string bbs, CharacterProfile character)
    {
        string? currentName = _profile.CurrentProfileName;
        if (currentName is null) return; // named-profile path only.

        ProfileNameInputDialogViewModel vm = new(
            suggestedName: DeriveUniqueName(bbs, currentName),
            exists:        name => _profile.Exists(bbs, name));

        string? newName = await AppServices.Current.Dialogs.OpenWindowAsync<
            ProfileNameInputDialogViewModel, string>(vm);
        if (string.IsNullOrWhiteSpace(newName))
        {
            AppServices.Current.Log.Info("Profile",
                $"Re-home of '{currentName}' to BBS '{bbs}' cancelled — left in place.");
            return;
        }

        try
        {
            _profile.ReHome(bbs, newName);
        }
        catch (Exception ex)
        {
            AppServices.Current.Log.Error("Profile",
                $"Re-home of '{currentName}' to BBS '{bbs}' failed: {ex.Message}");
            return;
        }
        CommitCredentials(bbs, character);
    }

    // Suggest a destination profile name that doesn't already exist under the
    // given BBS, so the rename prompt's default is valid. Tries the original name
    // first, then appends " 2", " 3", …
    private string DeriveUniqueName(string bbs, string baseName)
    {
        if (!_profile.Exists(bbs, baseName)) return baseName;
        for (int n = 2; ; n++)
        {
            string candidate = $"{baseName} {n}";
            if (!_profile.Exists(bbs, candidate)) return candidate;
        }
    }

    // Write the per-BBS credential slice (username, password, menu-nav, sysop
    // flag) onto the loaded profile and persist. Runs whenever any
    // CharacterProfile is loaded (draft or named) because the inline
    // EncryptedPassword is keyed off the per-user .credkey, not the profile name —
    // a draft's BbsCredentials survive into its first Save. Ends in the mutate /
    // pin notifications so the main window's title / Host / Port re-resolve and any
    // Quick Connect override clears.
    private void CommitCredentials(string bbs, CharacterProfile character)
    {
        // Case-insensitive: BBS names are folder names on a case-insensitive
        // FS, so a 'Playpen' credential must resolve for a 'playpen' BBS.
        character.BbsCredentials ??= new(StringComparer.OrdinalIgnoreCase);
        if (!character.BbsCredentials.TryGetValue(bbs, out BbsCredentials? cred))
        {
            cred = new BbsCredentials();
            character.BbsCredentials[bbs] = cred;
        }
        cred.EncryptedUsername = string.IsNullOrEmpty(Username) ? null : _passwords.Protect(Username);
        cred.MenuNavSteps = MenuNavSteps.Select(vm => vm.ToModel()).ToList();
        cred.HasSysopPowers = HasSysopPowers;

        if (_pendingPassword is not null)
        {
            cred.EncryptedPassword = _pendingPassword.Length == 0
                ? null
                : _passwords.Protect(_pendingPassword);
            _pendingPassword = null;
        }

        // Save() no-ops on drafts (no name to write to). NotifyMutated
        // always fires so observers refresh either way.
        _profile.Save();
        _profile.NotifyMutated();
        _profile.NotifyBbsPinApplied();
    }

    private void RenameSelected(string oldName, string newName)
    {
        if (!_loaded.TryGetValue(oldName, out BbsProfile? profile))
        {
            profile = _bbsStore.Get(oldName);
            if (profile is null) return;
        }

        // Don't trample an existing BBS with the new name.
        if (_loaded.ContainsKey(newName) || _bbsStore.Get(newName) is not null) return;

        // Move the whole Data/BBS/{old}/ subtree — bbs.json, side-files, and
        // every nested character profile — to the new name. The old
        // Delete+Save pair recursively destroyed the nested profiles and left
        // every reference to the BBS name (credentials, recent list) dangling.
        _bbsStore.Rename(oldName, newName);
        profile.Name = newName;
        _loaded.Remove(oldName);
        _loaded[newName] = profile;

        // The BBS name keys per-character credentials and the recent-profiles
        // refs — cascade the rename so logon-nav / passwords, the File → Recent
        // menu, and the "import logon steps" picker follow the new name.
        _profile.RenameBbs(oldName, newName);
        CascadeRecentProfiles(oldName, newName);

        _suppressDirty = true;
        ReloadBbsList();
        SelectedBbsName = newName;
        _suppressDirty = false;
    }

    // Rewrite the Global-tier recent-profiles + last-used pointers that named
    // the old BBS. They're (bbs, char) refs; without the rewrite the File →
    // Recent menu and startup auto-load point at a BBS folder that no longer
    // exists. Saving fires GlobalSettingsChanged so the live menu rebuilds.
    private void CascadeRecentProfiles(string oldName, string newName)
    {
        GlobalSettings settings = _globalSettings.Current;
        bool changed = false;

        if (settings.RecentProfiles is { } recents)
        {
            for (int i = 0; i < recents.Count; i++)
            {
                if (string.Equals(recents[i].Bbs, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    recents[i] = recents[i] with { Bbs = newName };
                    changed = true;
                }
            }
        }

        if (settings.LastUsedProfile is { } last
            && string.Equals(last.Bbs, oldName, StringComparison.OrdinalIgnoreCase))
        {
            settings.LastUsedProfile = last with { Bbs = newName };
            changed = true;
        }

        if (changed) _globalSettings.Save();
    }

    public override void Discard()
    {
        // Drop every cached in-memory edit and re-fetch from disk on the
        // next selection. Keeps the Apply contract: Cancel really cancels.
        _loaded.Clear();
        if (SelectedBbsName is not null)
        {
            _suppressDirty = true;
            ReloadSelected();
            _suppressDirty = false;
        }

        // Roll Confirm* observables back to their on-disk values too —
        // they're independent of the BBS cache but share this section's
        // dirty bit.
        LoadConfirmFromGlobalSettings();

        // Roll the live DisplayConfig back to the *active* BBS, not the
        // BBS that happened to be selected in the editor. Otherwise the
        // terminal canvas keeps the discarded preview font.
        SyncDisplayToActiveBbs();
        ClearDirty();
    }

    private void SyncDisplayToActiveBbs()
    {
        string? activeName = _profile.CurrentBbsName;
        BbsProfile? active = string.IsNullOrEmpty(activeName) ? null : _bbsStore.Get(activeName);
        BbsProfile values = active ?? new BbsProfile();
        _display.ScrollbackLines = values.ScrollbackLines;
        _display.TerminalCols = values.TerminalCols;
        _display.TerminalRows = values.TerminalRows;
    }

    [RelayCommand]
    private void AddBbs()
    {
        string baseName = "New BBS";
        string name = baseName;
        int n = 2;
        while (_bbsStore.Get(name) is not null || _loaded.ContainsKey(name))
        {
            name = $"{baseName} {n++}";
        }
        BbsProfile fresh = new() { Name = name, Host = string.Empty, Port = 23 };
        _bbsStore.Save(fresh);
        _loaded[name] = fresh;
        ReloadBbsList();
        SelectedBbsName = name;
    }

    [RelayCommand]
    private async Task DeleteBbsAsync()
    {
        if (SelectedBbsName is not { } name) return;
        if (!await AppServices.Current.Confirm.ConfirmDeleteAsync($"the BBS '{name}'")) return;
        _bbsStore.Delete(name);
        _loaded.Remove(name);
        ReloadBbsList();
        SelectedBbsName = AvailableBbsNames.FirstOrDefault();
    }

    partial void OnSelectedBbsNameChanged(string? value)
    {
        if (_suppressDirty) return;
        _suppressDirty = true;
        ReloadSelected();
        _suppressDirty = false;
    }

    private void ReloadBbsList()
    {
        AvailableBbsNames.Clear();
        foreach (string name in _bbsStore.ListNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            AvailableBbsNames.Add(name);
        }
    }

    private void ReloadSelected()
    {
        if (SelectedBbsName is not { } name)
        {
            ResetFields();
            return;
        }

        if (!_loaded.TryGetValue(name, out BbsProfile? profile))
        {
            profile = _bbsStore.Get(name) ?? new BbsProfile { Name = name };
            _loaded[name] = profile;
        }

        Name = profile.Name;
        Host = profile.Host;

        LoadCredentialsFor(name);
        Port = profile.Port;
        MaxRedials = profile.MaxRedials;
        RedialPauseSeconds = profile.RedialPauseSeconds;
        CleanupPeriodMinutes = profile.CleanupPeriodMinutes;
        NoResponseTimeoutSeconds = profile.NoResponseTimeoutSeconds;
        ReconnectOnFailedConnect = profile.ReconnectOnFailedConnect;
        ReconnectOnCarrierLost = profile.ReconnectOnCarrierLost;
        ReconnectOnNoResponse = profile.ReconnectOnNoResponse;
        ReconnectAfterCleanup = profile.ReconnectAfterCleanup;
        TerminalCols = profile.TerminalCols;
        TerminalRows = profile.TerminalRows;
        ScrollbackLines = profile.ScrollbackLines;
        GameEntryCommand = profile.GameEntryCommand;
        GameExitCommand = profile.GameExitCommand;
        PlayerDiesAtHp = profile.PlayerDiesAtHp;
        AutoRefineDeathFloor = profile.AutoRefineDeathFloor;
        DisconnectPattern = profile.DisconnectPattern;
        RunicCurrencyName = profile.RunicCurrencyName;
    }

    private void LoadCredentialsFor(string bbsName)
    {
        _pendingPassword = null;
        MenuNavSteps.Clear();
        if (!HasProfile)
        {
            Username = string.Empty;
            Password = string.Empty;
            HasSysopPowers = false;
            return;
        }
        CharacterProfile? character = _profile.Current;
        if (character?.BbsCredentials is not null
            && character.BbsCredentials.TryGetValue(bbsName, out BbsCredentials? cred))
        {
            // Username is encrypted at rest; decrypted for the UI
            // because the doc shows it plainly.
            Username = cred.EncryptedUsername is { } enc
                ? (_passwords.Unprotect(enc) ?? string.Empty)
                : string.Empty;
            // Password isn't pulled from the credential store eagerly — that
            // would surface the plaintext over a logging boundary every time
            // the user clicks around. Show empty + a placeholder; typing a
            // new one replaces, leaving it empty preserves the existing.
            Password = string.Empty;
            HasSysopPowers = cred.HasSysopPowers;
            foreach (MenuStep step in cred.MenuNavSteps)
            {
                MenuNavSteps.Add(MenuStepEditorViewModel.FromModel(step, Dirty));
            }
        }
        else
        {
            Username = string.Empty;
            Password = string.Empty;
            HasSysopPowers = false;
        }

        RefreshImportSources(bbsName);
    }

    // Load every other saved character's logon steps into the import picker. Runs
    // on each BBS-select / profile-load (the editing target drives which pair is
    // excluded). Reads profiles straight from disk without switching to them, so a
    // corrupt one is skipped rather than aborting the whole list.
    private void RefreshImportSources(string editingBbs)
    {
        ImportSourceOptions.Clear();
        SelectedImportSource = null;

        if (HasProfile)
        {
            var loaded = new List<(string bbs, string name, CharacterProfile profile)>();
            foreach (ProfileRef r in _profile.ListAll())
            {
                CharacterProfile? p;
                try
                {
                    p = JsonStore.Load<CharacterProfile>(AppPaths.CharacterProfileFile(r.Bbs, r.Name));
                }
                catch (InvalidDataException)
                {
                    // One unreadable profile shouldn't blank the picker for the rest.
                    continue;
                }
                if (p is not null) loaded.Add((r.Bbs, r.Name, p));
            }

            foreach (MenuNavImportOption option in MenuNavImportOption.Build(
                         loaded, editingBbs, _profile.CurrentBbsName, _profile.CurrentProfileName))
                ImportSourceOptions.Add(option);
        }

        OnPropertyChanged(nameof(HasImportSources));
    }

    private void RefreshProfileState()
    {
        HasProfile = _profile.Current is not null;
        OnPropertyChanged(nameof(CredentialsHint));
        OnPropertyChanged(nameof(IsCredentialsHintWarning));
        if (SelectedBbsName is not null)
        {
            _suppressDirty = true;
            LoadCredentialsFor(SelectedBbsName);
            _suppressDirty = false;
        }
        RefreshSuicidePassword();
    }

    // Hydrate SuicidePassword from the loaded profile's encrypted blob. Runs on
    // every profile load / mutate / close so the field reflects the live state —
    // including the wipe case where Game.SuicidePasswordTracker saw `pro`'s "You do
    // not have a suicide password set." line and cleared the stored value.
    private void RefreshSuicidePassword()
    {
        string decrypted = string.Empty;
        if (_profile.Current is { } profile
            && profile.EncryptedSuicidePassword is { Length: > 0 } blob)
        {
            decrypted = _passwords.Unprotect(blob) ?? string.Empty;
        }
        _suppressDirty = true;
        SuicidePassword = decrypted;
        _suppressDirty = false;
    }

    private void ResetFields()
    {
        BbsProfile defaults = new();
        Name = defaults.Name;
        Host = defaults.Host;
        Port = defaults.Port;
        MaxRedials = defaults.MaxRedials;
        RedialPauseSeconds = defaults.RedialPauseSeconds;
        CleanupPeriodMinutes = defaults.CleanupPeriodMinutes;
        NoResponseTimeoutSeconds = defaults.NoResponseTimeoutSeconds;
        ReconnectOnFailedConnect = defaults.ReconnectOnFailedConnect;
        ReconnectOnCarrierLost = defaults.ReconnectOnCarrierLost;
        ReconnectOnNoResponse = defaults.ReconnectOnNoResponse;
        ReconnectAfterCleanup = defaults.ReconnectAfterCleanup;
        TerminalCols = defaults.TerminalCols;
        TerminalRows = defaults.TerminalRows;
        ScrollbackLines = defaults.ScrollbackLines;
        GameEntryCommand = defaults.GameEntryCommand;
        GameExitCommand = defaults.GameExitCommand;
        PlayerDiesAtHp = defaults.PlayerDiesAtHp;
        AutoRefineDeathFloor = defaults.AutoRefineDeathFloor;
        DisconnectPattern = defaults.DisconnectPattern;
        RunicCurrencyName = defaults.RunicCurrencyName;
    }

    private void Dirty()
    {
        if (_suppressDirty || _dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void ClearDirty()
    {
        if (!_dirty) return;
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    // Field-change hooks: writes the new value into the in-memory cache for
    // the currently-selected BBS so Apply has something fresh to persist.
    private void PushToCache()
    {
        if (_suppressDirty) return;
        if (SelectedBbsName is not { } name) return;
        if (!_loaded.TryGetValue(name, out BbsProfile? profile)) return;

        profile.Host = Host;
        profile.Port = Port;
        profile.MaxRedials = MaxRedials;
        profile.RedialPauseSeconds = RedialPauseSeconds;
        profile.CleanupPeriodMinutes = CleanupPeriodMinutes;
        profile.NoResponseTimeoutSeconds = NoResponseTimeoutSeconds;
        profile.ReconnectOnFailedConnect = ReconnectOnFailedConnect;
        profile.ReconnectOnCarrierLost = ReconnectOnCarrierLost;
        profile.ReconnectOnNoResponse = ReconnectOnNoResponse;
        profile.ReconnectAfterCleanup = ReconnectAfterCleanup;
        profile.TerminalCols = TerminalCols;
        profile.TerminalRows = TerminalRows;
        profile.ScrollbackLines = ScrollbackLines;
        profile.GameEntryCommand = string.IsNullOrWhiteSpace(GameEntryCommand)
            ? new BbsProfile().GameEntryCommand : GameEntryCommand.Trim();
        profile.GameExitCommand = string.IsNullOrWhiteSpace(GameExitCommand)
            ? new BbsProfile().GameExitCommand : GameExitCommand.Trim();
        // Death floor is a negative-HP value; a positive entry is meaningless
        // (0 HP already means dropped), so clamp to <= 0 at the point of storage.
        profile.PlayerDiesAtHp = Math.Min(0, PlayerDiesAtHp);
        profile.AutoRefineDeathFloor = AutoRefineDeathFloor;
        profile.DisconnectPattern = string.IsNullOrWhiteSpace(DisconnectPattern)
            ? null : DisconnectPattern.Trim();
        profile.RunicCurrencyName = string.IsNullOrWhiteSpace(RunicCurrencyName)
            ? new BbsProfile().RunicCurrencyName : RunicCurrencyName.Trim();
    }

    partial void OnNameChanged(string value)                    { Dirty(); }
    partial void OnUsernameChanged(string value)                { Dirty(); }
    partial void OnPasswordChanged(string value)
    {
        if (_suppressDirty) return;
        _pendingPassword = value;
        Dirty();
    }

    // Toggling Show ON pulls the stored password out of the credential store on
    // demand — so users can verify what's saved without leaking the plaintext
    // through the UI on every Settings open. Toggling OFF leaves the box as-is (the
    // user may have started editing); if they haven't touched it, _pendingPassword
    // stays null and the Apply path no-ops the credential store.
    partial void OnShowPasswordChanged(bool value)
    {
        if (!value) return;
        if (!HasProfile) return;
        if (!string.IsNullOrEmpty(Password)) return;
        if (_pendingPassword is not null) return;
        if (SelectedBbsName is not { } bbs) return;

        CharacterProfile? character = _profile.Current;
        if (character?.BbsCredentials is null) return;
        if (!character.BbsCredentials.TryGetValue(bbs, out BbsCredentials? cred)) return;
        if (cred.EncryptedPassword is not { } blob) return;

        string? pw = _passwords.Unprotect(blob);
        if (string.IsNullOrEmpty(pw)) return;

        // Suppress the OnPasswordChanged side-effect: this assignment is a
        // reveal, not a user edit. Without the gate, _pendingPassword would
        // get stamped with the same value and Apply would re-encrypt it
        // back to the profile as if the user had retyped it.
        _suppressDirty = true;
        try { Password = pw; }
        finally { _suppressDirty = false; }
    }
    partial void OnHostChanged(string value)                    { PushToCache(); Dirty(); }
    partial void OnPortChanged(int value)                       { PushToCache(); Dirty(); }
    partial void OnMaxRedialsChanged(int value)                 { PushToCache(); Dirty(); }
    partial void OnRedialPauseSecondsChanged(int value)         { PushToCache(); Dirty(); }
    partial void OnCleanupPeriodMinutesChanged(int value)       { PushToCache(); Dirty(); }
    partial void OnNoResponseTimeoutSecondsChanged(int value)   { PushToCache(); Dirty(); }
    partial void OnReconnectOnFailedConnectChanged(bool value)  { PushToCache(); Dirty(); }
    partial void OnReconnectOnCarrierLostChanged(bool value)    { PushToCache(); Dirty(); }
    partial void OnReconnectOnNoResponseChanged(bool value)     { PushToCache(); Dirty(); }
    partial void OnReconnectAfterCleanupChanged(bool value)     { PushToCache(); Dirty(); }
    partial void OnHasSysopPowersChanged(bool value)            { Dirty(); }
    partial void OnTerminalColsChanged(int value)               { PushToCache(); Dirty(); }
    partial void OnTerminalRowsChanged(int value)               { PushToCache(); Dirty(); }

    partial void OnScrollbackLinesChanged(int value)            { PushToCache(); Dirty(); }
    partial void OnGameEntryCommandChanged(string value)        { PushToCache(); Dirty(); }
    partial void OnGameExitCommandChanged(string value)         { PushToCache(); Dirty(); }
    partial void OnPlayerDiesAtHpChanged(int value)             { PushToCache(); Dirty(); }
    partial void OnAutoRefineDeathFloorChanged(bool value)      { PushToCache(); Dirty(); }
    partial void OnDisconnectPatternChanged(string? value)      { PushToCache(); Dirty(); }
    partial void OnRunicCurrencyNameChanged(string value)       { PushToCache(); Dirty(); }

    // Confirm flags are Global-tier, not per-BBS — they don't push into
    // the per-BBS cache, just mark the section dirty so Apply commits
    // them via SaveConfirmToGlobalSettings.
    partial void OnConfirmExitChanged(bool value)               { Dirty(); }
    partial void OnConfirmHangupChanged(bool value)             { Dirty(); }
    partial void OnConfirmSaveSettingsChanged(bool value)       { Dirty(); }
    partial void OnConfirmDeletesChanged(bool value)            { Dirty(); }

    [RelayCommand]
    private void AddMenuStep()
    {
        if (_suppressDirty) return;
        MenuNavSteps.Add(new MenuStepEditorViewModel(Dirty));
        Dirty();
    }

    [RelayCommand]
    private void RemoveMenuStep(MenuStepEditorViewModel? step)
    {
        if (step is null) return;
        if (!MenuNavSteps.Remove(step)) return;
        Dirty();
    }

    [RelayCommand]
    private void MoveMenuStepUp(MenuStepEditorViewModel? step)
    {
        if (step is null) return;
        int i = MenuNavSteps.IndexOf(step);
        if (i <= 0) return;
        MenuNavSteps.Move(i, i - 1);
        Dirty();
    }

    [RelayCommand]
    private void MoveMenuStepDown(MenuStepEditorViewModel? step)
    {
        if (step is null) return;
        int i = MenuNavSteps.IndexOf(step);
        if (i < 0 || i >= MenuNavSteps.Count - 1) return;
        MenuNavSteps.Move(i, i + 1);
        Dirty();
    }

    // Replace the current character's logon steps with a copy of the chosen
    // source's. Destructive by design but recoverable: Settings is a Save/Cancel
    // window, so Cancel / X drops the import if it wasn't the right starting point.
    [RelayCommand(CanExecute = nameof(CanImportMenuNav))]
    private void ImportMenuNav()
    {
        if (SelectedImportSource is not { } src) return;
        MenuNavSteps.Clear();
        foreach (MenuStep step in src.Steps)
            MenuNavSteps.Add(MenuStepEditorViewModel.FromModel(step, Dirty));
        Dirty();
    }

    private bool CanImportMenuNav() => SelectedImportSource is not null;
}
