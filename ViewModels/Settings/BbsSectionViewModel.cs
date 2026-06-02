using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.Profile;
using FujinTerm.Models.Settings;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "BBS" tab. Owns the list of saved BBS records (globally shared across
/// every character) and the field-editor for whichever one is selected.
/// Per-character credentials (username, password, menu-nav sequence)
/// live on the character profile and ship in PR 4.5b / 4.5c — this PR
/// is the BBS record itself only.
/// </summary>
/// <remarks>
/// Apply walks the cached in-memory BBS profiles and persists every
/// dirty one. Discard reloads the currently-selected BBS from disk so
/// pending edits are dropped. Adding / deleting a BBS commits immediately
/// (those are structural, not field-level edits — the OK / Cancel commit
/// only covers field tweaks).
/// </remarks>
public sealed partial class BbsSectionViewModel : SettingsSectionViewModel
{
    private readonly BbsProfileStore _bbsStore;
    private readonly ProfileService _profile;
    private readonly PasswordProtector _passwords;
    private readonly DisplayConfig _display;
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
        "Display", "Font", "Font size", "Scrollback", "Backscroll", "Buffer",
        "Confirm", "Confirm exit", "Confirm hangup", "Confirm save", "Confirm delete",
    };

    public override Control View => _view ??= new BbsSectionView { DataContext = this };

    /// <summary>Names of every saved BBS profile (left rail of the tab).</summary>
    public ObservableCollection<string> AvailableBbsNames { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private string? _selectedBbsName;

    public bool HasSelection => SelectedBbsName is not null;

    // ----- Editable fields, populated from the selected BbsProfile -----
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private int _port = 23;
    [ObservableProperty] private string? _websiteUrl;
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
    [ObservableProperty] private double _fontSize = 16.0;
    [ObservableProperty] private int _scrollbackLines = 10_000;

    // ----- Per-character credentials (PR 4.5b) -----
    /// <summary>
    /// True when any character profile is loaded — including unsaved
    /// drafts. Credentials, sysop flag, and menu nav all bind against the
    /// in-memory CharacterProfile; the password is encrypted with the
    /// per-user .credkey (not anything keyed on the profile name) so an
    /// unsaved draft can carry them forward into its first Save just
    /// fine. Only used now to dim the credentials block when literally
    /// no profile object exists (a state we never actually reach at
    /// runtime, but the guard keeps designer-time previews honest).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CredentialsHint))]
    [NotifyPropertyChangedFor(nameof(IsCredentialsHintWarning))]
    private bool _hasProfile;

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _showPassword;

    /// <summary>Editable rows for the per-character menu-nav sequence.</summary>
    public ObservableCollection<MenuStepEditorViewModel> MenuNavSteps { get; } = new();

    /// <summary>Helper text under the credentials section.</summary>
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

    /// <summary>
    /// True when the credentials hint should be drawn in a warning color
    /// (e.g., red) — currently only for the unsaved-draft case, so the
    /// user can see at a glance that their edits won't persist until
    /// they Save / Save As.
    /// </summary>
    public bool IsCredentialsHintWarning =>
        HasProfile && _profile.CurrentProfileName is null;

    public BbsSectionViewModel(
        BbsProfileStore bbsStore,
        ProfileService profile,
        PasswordProtector passwords,
        DisplayConfig display)
    {
        ArgumentNullException.ThrowIfNull(bbsStore);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(passwords);
        ArgumentNullException.ThrowIfNull(display);
        _bbsStore = bbsStore;
        _profile = profile;
        _passwords = passwords;
        _display = display;

        _profile.ProfileLoaded += _ => RefreshProfileState();
        _profile.ProfileClosed += RefreshProfileState;
        RefreshProfileState();

        ReloadBbsList();
        // Default selection to the loaded character's active BBS when it's
        // in the list — re-entering settings should land on the BBS the
        // user is currently dialed at.
        string? preferred = _profile.Current?.BbsName;
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
        if (!string.Equals(SelectedBbsName, _profile.Current?.BbsName, StringComparison.OrdinalIgnoreCase))
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
            _bbsStore.Save(profile);
        }

        ApplyToCurrentProfile();

        ClearDirty();
    }

    /// <summary>
    /// Push the BBS section's character-side decisions onto the loaded
    /// profile. Two slices that fire independently:
    ///   • BbsName — pins the active BBS for the loaded character; works
    ///     even on the blank draft so the main window's connect target
    ///     resolves before the user has named their profile.
    ///   • Credentials — username, password, menu-nav. Needs a named
    ///     profile because the credential-store key embeds the char name.
    /// Both slices end with a <see cref="ProfileService.NotifyMutated"/>
    /// so the main window's title / Host / Port re-resolve.
    /// </summary>
    private void ApplyToCurrentProfile()
    {
        if (SelectedBbsName is not { } bbs) return;
        CharacterProfile? character = _profile.Current;
        if (character is null) return;

        // Slice 1 — pin the active BBS onto the character profile.
        character.BbsName = bbs;

        // Slice 2 — credentials. Runs whenever any CharacterProfile is
        // loaded (draft or named) because the inline EncryptedPassword
        // is keyed off the per-user .credkey, not the profile name. A
        // draft's BbsCredentials survive into the first Save and are
        // serialised right alongside the BbsName pin.
        character.BbsCredentials ??= new();
        if (!character.BbsCredentials.TryGetValue(bbs, out BbsCredentials? cred))
        {
            cred = new BbsCredentials();
            character.BbsCredentials[bbs] = cred;
        }
        cred.Username = Username;
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

        // The user explicitly pinned a BBS via this tab — let the main
        // window drop any Quick Connect override even when the pinned
        // name didn't actually change between opens.
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

        profile.Name = newName;
        _bbsStore.Delete(oldName);
        _bbsStore.Save(profile);
        _loaded.Remove(oldName);
        _loaded[newName] = profile;

        _suppressDirty = true;
        ReloadBbsList();
        SelectedBbsName = newName;
        _suppressDirty = false;
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

        // Roll the live DisplayConfig back to the *active* BBS, not the
        // BBS that happened to be selected in the editor. Otherwise the
        // terminal canvas keeps the discarded preview font.
        SyncDisplayToActiveBbs();
        ClearDirty();
    }

    private void SyncDisplayToActiveBbs()
    {
        string? activeName = _profile.Current?.BbsName;
        BbsProfile? active = string.IsNullOrEmpty(activeName) ? null : _bbsStore.Get(activeName);
        BbsProfile values = active ?? new BbsProfile();
        _display.FontSize = values.FontSize;
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
    private void DeleteBbs()
    {
        if (SelectedBbsName is not { } name) return;
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
        WebsiteUrl = profile.WebsiteUrl;
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
        FontSize = profile.FontSize;
        ScrollbackLines = profile.ScrollbackLines;
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
            Username = cred.Username;
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
    }

    private void ResetFields()
    {
        BbsProfile defaults = new();
        Name = defaults.Name;
        Host = defaults.Host;
        Port = defaults.Port;
        WebsiteUrl = defaults.WebsiteUrl;
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
        FontSize = defaults.FontSize;
        ScrollbackLines = defaults.ScrollbackLines;
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
        profile.WebsiteUrl = string.IsNullOrWhiteSpace(WebsiteUrl) ? null : WebsiteUrl;
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
        profile.FontSize = FontSize;
        profile.ScrollbackLines = ScrollbackLines;
    }

    partial void OnNameChanged(string value)                    { Dirty(); }
    partial void OnUsernameChanged(string value)                { Dirty(); }
    partial void OnPasswordChanged(string value)
    {
        if (_suppressDirty) return;
        _pendingPassword = value;
        Dirty();
    }

    /// <summary>
    /// Toggling Show ON pulls the stored password out of the credential store
    /// on demand — so users can verify what's saved without leaking the
    /// plaintext through the UI on every Settings open. Toggling OFF
    /// leaves the box as-is (the user may have started editing); if they
    /// haven't touched it, <see cref="_pendingPassword"/> stays null and
    /// the Apply path no-ops the credential store.
    /// </summary>
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
    partial void OnWebsiteUrlChanged(string? value)             { PushToCache(); Dirty(); }
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

    partial void OnFontSizeChanged(double value)                { PushToCache(); Dirty(); }
    partial void OnScrollbackLinesChanged(int value)            { PushToCache(); Dirty(); }

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
}
