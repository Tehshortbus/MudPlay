using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.ViewModels.Guides;
using MudPlay.Views.Settings;

namespace MudPlay.ViewModels.Settings;

// "BBS" tab. Owns the list of saved BBS records (globally shared across every
// character) and the field-editor for whichever one is selected. Per-character
// credentials (username, password, menu-nav sequence) live on the character
// profile.
//
// Apply walks the cached in-memory BBS profiles and persists every dirty one,
// then commits the credential edits staged for every board the user touched —
// both halves of the tab honour edits made across several boards in one visit,
// not just the one selected when OK was pressed. Discard drops both caches and
// reloads the currently-selected BBS from disk. Adding / removing a BBS lives in
// Profile Management; realm add / rename / remove here is staged and carried out
// on OK.
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

    // Per-BBS staging for the credential block, the counterpart to _loaded. The
    // BBS-profile fields survive a click to another board because PushToCache
    // writes them into _loaded on every keystroke; credentials are read and
    // written as one bundle, so instead of pushing per-field they're captured
    // here the moment the selection leaves a board. Without this, editing a login
    // and then clicking a different BBS before OK dropped the edit silently —
    // Apply only ever committed whichever board happened to be selected.
    private readonly Dictionary<string, StagedCredentials> _stagedCredentials =
        new(StringComparer.OrdinalIgnoreCase);

    // Whether the credential block has been edited since the current board was
    // loaded. Gates staging so merely clicking through boards doesn't author a
    // credential entry on every one of them.
    private bool _credentialsTouched;

    // One board's pending credential edits. The password is held encrypted — the
    // plaintext is deliberately short-lived (see LoadCredentialsFor), and staging
    // it across several boards would otherwise keep every typed password in
    // memory for the life of the window. PasswordTouched separates "leave the
    // stored password alone" from "clear it".
    private sealed class StagedCredentials
    {
        public string Username = string.Empty;
        public bool PasswordTouched;
        public string? EncryptedPassword;
        public bool SysopStatus;
        public bool SysopGodLives;
        public bool SysopGoto;
        public List<MenuStep> MenuNavSteps = new();
        public string? AutoLogonSetup;
        public List<SysopGotoLocation> SysopGotos = new();
    }

    public override string Id => "bbs";
    public override string Title => "BBS + Display";
    public override bool IsDirty => _dirty;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "GMCP", "Server data", "Shadow", "BBS", "Host", "Port", "Telnet", "Redial", "Cleanup", "Reconnect",
        "Status bar", "Marquee", "Status bar row", "Custom text",
        "Sysop", "Sys Goto", "Terminal", "Cols", "Rows", "NAWS", "Connection",
        "Game entry command", "Game exit command", "Enter realm", "Logoff",
        "Player dies at", "Death floor", "Bleeding out", "Dropped", "Hangup HP",
        "PvP", "PvP enabled", "Player versus player",
        "Hang-up penalties", "Hangup penalty", "Hang-up penalty", "HP lost", "Items dropped",
        "Penalised in combat", "PvE", "Disconnect penalty",
        "Auto-refine death floor", "Trace death floor", "Slow death", "Learn floor",
        "Disconnect pattern", "Party disconnect", "Logoff pattern", "Logs off",
        "Player disconnect line",
        "Display", "Scrollback", "Backscroll", "Buffer",
        "Confirm", "Confirm exit", "Confirm hangup", "Confirm save", "Confirm delete",
    };

    protected override Control BuildView() => new BbsSectionView { DataContext = this };

    // Names of every saved BBS profile (left rail of the tab).
    public ObservableCollection<string> AvailableBbsNames { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private string? _selectedBbsName;

    public bool HasSelection => SelectedBbsName is not null;

    [RelayCommand]
    private async Task RefreshGmcp()
    {
        try { await AppServices.Current.Gmcp.RefreshAsync(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or ObjectDisposedException)
        {
            AppServices.Current.Log.Warn("GMCP", $"Refresh failed: {ex.Message}");
        }
    }

    private Views.GmcpDataWindow? _gmcpDataWindow;

    [RelayCommand]
    private void InspectGmcp()
    {
        if (_gmcpDataWindow is { } open)
        {
            DialogService.RaiseOrClose(open);
            return;
        }
        _gmcpDataWindow = new Views.GmcpDataWindow { DataContext = new GmcpDataViewModel(AppServices.Current.Gmcp) };
        _gmcpDataWindow.Closed += (_, _) => _gmcpDataWindow = null;
        _gmcpDataWindow.Show();
    }

    public IReadOnlyList<GmcpPackageOption> GmcpPackages { get; } =
        Game.Gmcp.GmcpIntegration.Packages.Select(p => new GmcpPackageOption(p)).ToArray();

    // Every package to one mode at once, from the row above the list.
    [RelayCommand]
    private void SetAllGmcpModes(Game.Gmcp.GmcpMode mode)
    {
        foreach (GmcpPackageOption option in GmcpPackages) option.Mode = mode;
    }

    // The server-data modes show only for a board whose server has completed a GMCP
    // handshake (BbsProfile.GmcpOffered); every other board would only be offered
    // settings that do nothing.
    [ObservableProperty] private bool _gmcpOffered;
    [ObservableProperty] private string _gmcpStatus = string.Empty;

    private void LoadGmcpState(BbsProfile profile)
    {
        Game.Gmcp.GmcpState? state = AppServices.CurrentOrNull?.Gmcp.State;
        bool connectedHere = state is { Compatible: true }
            && AppServices.CurrentOrNull?.Connection.Connected == true
            && string.Equals(profile.Name, _profile.CurrentBbsName, StringComparison.OrdinalIgnoreCase);
        GmcpOffered = profile.GmcpOffered || connectedHere;
        GmcpStatus = connectedHere
            ? $"Server data: contract {state!.ServerContract}, {(state.InSession ? "in the game" : "connected")}."
            : "This board's server offered GMCP on an earlier connection; not connected to it now.";
    }

    // Max-redials / redial-pause tickers grey out while InfiniteRetries forces
    // its own count + pause; the XAML binds their IsEnabled here.
    public bool RedialTickersEnabled => !InfiniteRetries;

    // First-run tour: glow the host/port fields when the tour points at them.
    [ObservableProperty] private bool _highlightHostPort;

    // ----- Editable fields, populated from the selected BbsProfile -----
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private int _port = 23;
    [ObservableProperty] private int _maxRedials = 3;
    [ObservableProperty] private int _redialPauseSeconds = 5;
    [ObservableProperty] private bool _infiniteRetries;
    [ObservableProperty] private int _cleanupPeriodMinutes;
    [ObservableProperty] private int _noResponseTimeoutSeconds;
    [ObservableProperty] private bool _reconnectOnFailedConnect;
    [ObservableProperty] private bool _reconnectOnCarrierLost;
    [ObservableProperty] private bool _reconnectOnNoResponse;
    [ObservableProperty] private bool _reconnectAfterCleanup;
    [ObservableProperty] private bool _sysopStatus;
    [ObservableProperty] private bool _sysopGodLives;
    [ObservableProperty] private bool _sysopGoto;
    [ObservableProperty] private int _terminalCols = 80;
    [ObservableProperty] private int _terminalRows = 25;
    [ObservableProperty] private int _scrollbackLines = 4_000;
    [ObservableProperty] private int _backscrollWheelLines = 5;

    // ----- Realms of the selected BBS -----
    // A board can host several versions of the game; each realm carries its own
    // game data, menu commands, death floor, cleanup time and currency name, and
    // its own folder of collected data. The fields below the list edit the selected
    // realm (RealmProfile) inside the cached BbsProfile, so they save with the BBS.
    public ObservableCollection<string> RealmNames { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveRealmCommand))]
    private string? _selectedRealmName;

    // The selected realm's frame colour — its own per realm (by position in the
    // list, so the first several never share one), so switching realms visibly
    // switches what you're editing. Same palette as the combat-profile frames.
    public Avalonia.Media.IBrush RealmAccentBrush => CombatProfilePalette.SolidBrush(SelectedRealmNumber());
    public Avalonia.Media.IBrush RealmAccentSoftBrush => CombatProfilePalette.SoftBrush(SelectedRealmNumber());

    private int SelectedRealmNumber() => Math.Max(0, RealmNames.IndexOf(SelectedRealmName ?? string.Empty)) + 1;

    // Which characters play the selected realm, shown in its frame. Read from the
    // saved assignments, then adjusted for this window's unsaved edits (a staged
    // rename, a realm added here, the loaded character's pending "Plays on realm").
    [ObservableProperty] private string _realmCharactersText = string.Empty;

    private void RefreshRealmCharacters()
    {
        OnPropertyChanged(nameof(RealmAccentBrush));
        OnPropertyChanged(nameof(RealmAccentSoftBrush));
        if (SelectedBbsName is not { } bbs || SelectedRealmName is not { } realm
            || AppServices.CurrentOrNull?.Realms is not { } realms)
        {
            RealmCharactersText = string.Empty;
            return;
        }
        string original = _realmRenames.FirstOrDefault(r =>
            string.Equals(r.Bbs, bbs, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.New, realm, StringComparison.OrdinalIgnoreCase)).Old ?? realm;
        bool saved = _bbsStore.Get(bbs)?.Realms.Any(r =>
            string.Equals(r.Name, original, StringComparison.OrdinalIgnoreCase)) == true;
        List<string> names = saved ? realms.CharactersOn(bbs, original).Select(c => c.Name).ToList() : new();
        if (_characterRealmTouched && CanPickCharacterRealm && _profile.CurrentProfileName is { } current)
        {
            names.RemoveAll(n => string.Equals(n, current, StringComparison.Ordinal));
            if (string.Equals(CharacterRealm, realm, StringComparison.OrdinalIgnoreCase)) names.Add(current);
            names.Sort(StringComparer.OrdinalIgnoreCase);
        }
        RealmCharactersText = names.Count == 0
            ? "No characters play this realm yet."
            : $"Characters on this realm: {string.Join(", ", names)}";
    }

    // Rename box for the selected realm; commits when it loses focus.
    [ObservableProperty] private string _realmName = string.Empty;

    // The selected realm's game-data set; GlobalDefaultSet means "use the Global
    // default" (RealmProfile.ActiveGameDataSet = null).
    public const string GlobalDefaultSet = "(global default)";
    public ObservableCollection<string> GameDataSetOptions { get; }
    [ObservableProperty] private string _realmGameDataSet = GlobalDefaultSet;

    // A realm naming a set that isn't installed here gets a picker entry of its
    // own, standing for that name: shown as the global default instead, any other
    // edit to the realm would save null over the set it chose.
    private string? _missingSet;
    private string? _missingSetOption;
    private static string NotInstalledOption(string set) => $"{set} (not installed)";

    // The picker's choice as the realm stores it: null for the global default.
    private string? StoredRealmSet() =>
        RealmGameDataSet == GlobalDefaultSet ? null
        : RealmGameDataSet == _missingSetOption ? _missingSet
        : RealmGameDataSet;

    // Realm renames / removals made in the window, applied on OK: a rename moves
    // the realm's data folder and re-points its characters; a removal deletes the
    // characters that play it (confirmed by name when it was staged) and its folder.
    private readonly List<(string Bbs, string Old, string New)> _realmRenames = new();
    private readonly List<(string Bbs, string Name, IReadOnlyList<ProfileRef> Characters)> _realmRemovals = new();

    // The loaded character's realm on its own BBS — editable only while that BBS
    // is the one selected. Committed on OK.
    [ObservableProperty] private string? _characterRealm;
    private bool _characterRealmTouched;
    public bool CanPickCharacterRealm =>
        HasProfile && SelectedBbsName is { } sel
        && string.Equals(sel, _profile.CurrentBbsName, StringComparison.OrdinalIgnoreCase);

    private static ObservableCollection<string> BuildGameDataSetOptions(GameDataCache gameData) =>
        new(gameData.AvailableSets.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).Prepend(GlobalDefaultSet));

    // ----- Game-menu commands (selected realm) -----
    // The two main-menu picks for entering / leaving the game.
    [ObservableProperty] private string _gameEntryCommand = "E";
    [ObservableProperty] private string _gameExitCommand = "=x";

    // ----- Realm mechanics (selected realm) -----
    // The negative-HP floor at which a character actually dies (0 HP only drops
    // you into a revivable bleed-out). The emergency auto-hangup reads it to
    // keep firing through the whole bleeding-out window. Seeded at the standard
    // -25.
    [ObservableProperty] private int _playerDiesAtHp = -25;

    // When on, the death-floor tracer refines PlayerDiesAtHp from observed slow
    // deaths (a bleed-out lands right at the true floor). Off pins the manual
    // value. Default on.
    [ObservableProperty] private bool _autoRefineDeathFloor = true;

    // Players can attack one another on the selected realm (RealmProfile.PvpEnabled).
    // The PvP settings and the care taken with room attacks around other players
    // only apply where this is ticked.
    [ObservableProperty] private bool _pvpEnabled;

    // ----- Hang-up penalty (selected realm) -----
    // What the board takes for a hang-up in a fight (RealmProfile.HangupPenaltyEnabled
    // and the fields after it): a share of max HP between two percentages and up to
    // a number of items, for PvP and, when the board penalises that too, for a fight
    // with a monster. The user records it; the program log and the bug report show
    // it. No hang-up waits on it.
    [ObservableProperty] private bool _hangupPenaltyEnabled;
    [ObservableProperty] private int _hangupPvpHpFromPercent = 25;
    [ObservableProperty] private int _hangupPvpHpToPercent = 50;
    [ObservableProperty] private int _hangupPvpItemsDropped;
    [ObservableProperty] private bool _hangupPvePenaltyEnabled;
    [ObservableProperty] private int _hangupPveHpFromPercent = 25;
    [ObservableProperty] private int _hangupPveHpToPercent = 50;
    [ObservableProperty] private int _hangupPveItemsDropped;
    // The board penalises every hang-up, in a fight or not
    // (RealmProfile.HangupOutsideFightPenaltyEnabled). It only decides whether a
    // death by the penalty is looked for after a hang-up outside a fight.
    [ObservableProperty] private bool _hangupOutsideFightPenaltyEnabled;

    // Nightly boss-cleanup wall-clock time ("HH:mm" in CleanupTimeZoneId) + its zone.
    // Drives the DEAD/ALIVE state of "Respawns @ Cleanup" bosses on the Bosses tab.
    // Default 21:00 in the computer's own zone (auto-detected); the zone is a
    // dropdown the user can override.
    [ObservableProperty] private string _cleanupTimeOfDay = "21:00";
    [ObservableProperty] private string _cleanupTimeZoneId = TimeZoneInfo.Local.Id;

    // Every system time-zone id (auto-detected local zone included), sorted — the
    // options for the cleanup-zone dropdown.
    public IReadOnlyList<string> TimeZoneIds => AllTimeZoneIds.Value;

    // Listing the system's time zones reads the whole tz database (a third of a
    // second), so it's done once per run rather than on every Settings open.
    private static readonly Lazy<IReadOnlyList<string>> AllTimeZoneIds = new(BuildTimeZoneIds);

    private static IReadOnlyList<string> BuildTimeZoneIds()
    {
        var ids = TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id).ToList();
        if (!ids.Contains(TimeZoneInfo.Local.Id, StringComparer.OrdinalIgnoreCase))
            ids.Add(TimeZoneInfo.Local.Id);
        ids.Sort(StringComparer.OrdinalIgnoreCase);
        return ids;
    }

    // Board-specific player-disconnect line (see BbsProfile.DisconnectPattern).
    // Optional literal pattern — {name} captures the disconnecting player, *
    // swallows a varying run. Empty = only the built-in "just disconnected" /
    // "just hung up" forms are watched.
    [ObservableProperty] private string? _disconnectPattern;

    // The selected realm's label for the top (runic) denomination — some realms
    // rename it, which changes both the coin wording the server sends and the
    // keyword the client keys currency commands on. Blank falls back to "runic".
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

    // ----- Status bar layout (Global tier) -----
    // Persisted in GlobalSettings.Settings["StatusBar"]; the main window rebuilds
    // its bar on SettingsService.GlobalSettingsChanged.
    public StatusBarEditorViewModel StatusBarEditor { get; }

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

    // Editable rows for the per-character `sys goto` location table (shown/enabled
    // only when SysopGoto is on).
    public ObservableCollection<SysopGotoRowViewModel> SysopGotos { get; } = new();

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
        SettingsService globalSettings,
        GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(bbsStore);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(passwords);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(globalSettings);
        ArgumentNullException.ThrowIfNull(gameData);
        _bbsStore = bbsStore;
        foreach (GmcpPackageOption option in GmcpPackages)
            option.PropertyChanged += (_, _) =>
            {
                PushToCache();
                Dirty();
            };
        _profile = profile;
        _passwords = passwords;
        _display = display;
        _globalSettings = globalSettings;
        GameDataSetOptions = BuildGameDataSetOptions(gameData);

        Action<CharacterProfile> onProfileLoaded = _ => RefreshProfileState();
        // SuicidePasswordTracker writes a new encrypted blob and
        // calls NotifyMutated on commit; pick that up so the
        // Settings → BBS field reflects the freshly-captured value
        // without requiring the user to reload the section.
        Action<CharacterProfile> onProfileMutated = _ => RefreshSuicidePassword();
        _profile.ProfileLoaded += onProfileLoaded;
        _profile.ProfileClosed += RefreshProfileState;
        _profile.ProfileMutated += onProfileMutated;
        if (AppServices.CurrentOrNull is { } svcs) svcs.TourActionChanged += OnTourActionChanged;
        OnTourActionChanged();
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= onProfileLoaded;
            _profile.ProfileClosed -= RefreshProfileState;
            _profile.ProfileMutated -= onProfileMutated;
            if (AppServices.CurrentOrNull is { } s) s.TourActionChanged -= OnTourActionChanged;
        });
        RefreshProfileState();
        LoadConfirmFromGlobalSettings();
        StatusBarEditor = new StatusBarEditorViewModel(
            () => AppServices.CurrentOrNull?.CreateStatusBarPreview());
        StatusBarEditor.Load(Models.Settings.StatusBarSettings.Read(_globalSettings.Current));
        StatusBarEditor.Changed += Dirty;
        OnDispose(StatusBarEditor.Dispose);

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
        ApplyRealmChanges();

        // Rename pass: if the Name field differs from the selected key, the
        // user retitled this BBS. Move the on-disk file + cache entry and
        // refresh the selection so the list shows the new name.
        // The name is a folder's: spaces typed around it are not part of it.
        if (SelectedBbsName is { } oldName
            && !string.IsNullOrWhiteSpace(Name)
            && !string.Equals(oldName, Name.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            RenameSelected(oldName, Name.Trim());
        }

        foreach (BbsProfile profile in _loaded.Values) SaveLoaded(profile);

        ApplyToCurrentProfile();
        bool realmMoved = ApplyCharacterRealm();
        SaveConfirmToGlobalSettings();

        // The active realm's settings, name or assignment may have changed: re-pin so
        // the game data, menu commands and realm stores follow.
        if (realmMoved || _realmRenames.Count > 0 || _realmRemovals.Count > 0
            || (_profile.CurrentBbsName is { } active && _loaded.ContainsKey(active)))
            _profile.NotifyBbsPinApplied();

        ResetCredentialStaging();
        ResetRealmStaging();
        ClearDirty();

        // First-run tour: OK/Apply on a BBS with a host completes the "Click OK"
        // step. Fires regardless of which section owns focus, since the Settings
        // window applies every dirty section on OK.
        if (!string.IsNullOrWhiteSpace(Host))
            AppServices.CurrentOrNull?.NotifyTourAction?.Invoke(GuideActions.BbsSaved);
    }

    // Drop every staged credential edit and the in-flight plaintext password.
    // Called once the edits have been committed (Apply) or abandoned (Discard,
    // character swap) — holding them past that point would re-commit stale
    // values onto whatever profile is loaded next.
    private void ResetRealmStaging()
    {
        _realmRenames.Clear();
        _realmRemovals.Clear();
        _characterRealmTouched = false;
    }

    private void ResetCredentialStaging()
    {
        _stagedCredentials.Clear();
        _credentialsTouched = false;
        _pendingPassword = null;
    }

    // Hydrate the four Confirm* observables from the Global-tier settings file.
    // Runs once at ctor time; Discard re-runs it to roll back unsaved edits.
    private void LoadConfirmFromGlobalSettings()
    {
        ConfirmSettings dto = SettingsSection.Read<ConfirmSettings>(_globalSettings.Current, "Confirm");
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
            SettingsSection.ToElement(dto);
        // The status-bar layout is the same Global tier and rides the same save.
        StatusBarEditor.ToSettings().WriteTo(_globalSettings.Current);
        _globalSettings.Save();
    }

    // Commit the selected BBS's per-character credentials onto the loaded
    // profile. Selecting a BBS here is now editing-only — it NEVER re-homes /
    // pins the loaded character (that moved to the Profile Management window's
    // explicit "Move to BBS"). So this just writes the credential slice for
    // whichever BBS the user is editing; the profile stays where it lives.
    private void ApplyToCurrentProfile()
    {
        if (_profile.Current is not { } character) return;

        // Fold the board on screen into the staging map so it commits alongside
        // the ones already staged. Unconditional (not gated on _credentialsTouched)
        // because an untouched selection still persists the starter sys-goto set —
        // see the seed in LoadCredentialsFor.
        if (SelectedBbsName is { } selected) StageCredentials(selected);
        if (_stagedCredentials.Count == 0) return;

        foreach ((string bbs, StagedCredentials staged) in _stagedCredentials)
            WriteCredentials(bbs, character, staged);

        // Save() no-ops on drafts (no name to write to). NotifyMutated always
        // fires so observers refresh either way. NotifyBbsPinApplied is NOT
        // fired here: editing a BBS's credentials no longer changes the active-BBS
        // identity (re-home moved to Profile Management), so a credential edit
        // warrants only a mutation signal — BbsPinApplied now fires solely on a
        // real active-BBS change.
        _profile.Save();
        _profile.NotifyMutated();
    }

    // Write one board's staged credential slice (username, password, menu-nav,
    // sysop flags) onto the loaded profile. Runs whenever any CharacterProfile is
    // loaded (draft or named) because the inline EncryptedPassword is keyed off
    // the per-user .credkey, not the profile name — a draft's BbsCredentials
    // survive into its first Save. Persisting is the caller's job: one Save covers
    // every board committed in the same Apply.
    private void WriteCredentials(string bbs, CharacterProfile character, StagedCredentials staged)
    {
        // Case-insensitive: BBS names are folder names on a case-insensitive
        // FS, so a 'Playpen' credential must resolve for a 'playpen' BBS.
        character.BbsCredentials ??= new(StringComparer.OrdinalIgnoreCase);
        if (!character.BbsCredentials.TryGetValue(bbs, out BbsCredentials? cred))
        {
            cred = new BbsCredentials();
            character.BbsCredentials[bbs] = cred;
        }
        cred.EncryptedUsername = string.IsNullOrEmpty(staged.Username)
            ? null
            : _passwords.Protect(staged.Username);
        cred.MenuNavSteps = staged.MenuNavSteps;
        cred.AutoLogonSetup = staged.AutoLogonSetup;
        cred.SysopStatus = staged.SysopStatus;
        cred.SysopGodLives = staged.SysopGodLives;
        cred.SysopGoto = staged.SysopGoto;
        cred.SysopGotos = staged.SysopGotos;

        if (staged.PasswordTouched) cred.EncryptedPassword = staged.EncryptedPassword;
    }

    private void RenameSelected(string oldName, string newName)
    {
        if (!_loaded.TryGetValue(oldName, out BbsProfile? profile))
        {
            profile = _bbsStore.Get(oldName);
            if (profile is null) return;
        }

        // The Name box takes anything typed, and the name becomes a folder's: one
        // with a slash in it would move the BBS and every character on it out of
        // the BBS folder. Refused here, where the user can be told why; the BBS
        // keeps the name it had and the rest of the Apply goes on.
        if (PathGuard.WhyNotPlain(newName, "BBS name") is { } notPlain)
        {
            AppServices.Current.Log.Info("BBS",
                $"Rename '{oldName}' → {PathGuard.Show(newName)} refused: {notPlain}");
            AppServices.Current.Dialogs.ShowInfo(
                "BBS not renamed", $"{notPlain} “{oldName}” keeps its name.");
            return;
        }

        // Don't trample an existing BBS with the new name. Test the folder, not
        // just a loadable bbs.json: BbsProfileStore.Rename moves the whole folder
        // and Directory.Move throws if the destination folder exists at all —
        // even a stray one with no bbs.json (half-deleted BBS, or one holding
        // only nested profiles). Guarding on Get(newName) alone let those slip
        // through and crashed the app with an unhandled IOException on Apply.
        if (_loaded.ContainsKey(newName) || _bbsStore.Exists(newName))
        {
            AppServices.Current.Log.Info("BBS",
                $"Rename '{oldName}' → '{newName}' refused: a folder for that name already exists.");
            AppServices.Current.Dialogs.ShowInfo(
                "BBS not renamed",
                $"A BBS named “{newName}” already exists. Pick a name that isn't in use.");
            return;
        }

        // Move the whole BBS/{old}/ subtree — bbs.json, side-files, and
        // every nested character profile — to the new name. The old
        // Delete+Save pair recursively destroyed the nested profiles and left
        // every reference to the BBS name (credentials, recent list) dangling.
        if (!_bbsStore.Rename(oldName, newName)) return;
        profile.Name = newName;
        _loaded.Remove(oldName);
        _loaded[newName] = profile;
        // ProfileService.RenameBbs re-keys the stored credentials below; carry
        // any *staged* ones across too, or an unsaved login edit would commit
        // under the name the board no longer has.
        if (_stagedCredentials.Remove(oldName, out StagedCredentials? stagedCred))
            _stagedCredentials[newName] = stagedCred;

        // The BBS name keys per-character credentials and the recent-profiles
        // refs — cascade the rename so logon-nav / passwords, the File → Recent
        // menu, and the "import logon steps" picker follow the new name.
        _profile.RenameBbs(oldName, newName);
        RecentProfileList.RekeyBbs(_globalSettings, oldName, newName);

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
        ResetCredentialStaging();
        ResetRealmStaging();
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
        StatusBarEditor.Load(Models.Settings.StatusBarSettings.Read(_globalSettings.Current));

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
        _display.BackscrollWheelLines = values.BackscrollWheelLines;
        _display.TerminalCols = values.TerminalCols;
        _display.TerminalRows = values.TerminalRows;
    }

    // Adding / removing BBSes moved to the Profile Management window (View →
    // Profile Management); this tab now only selects a saved BBS to edit its
    // fields. Renaming still happens here via the Name field on Apply. The
    // "Open Profile Management" button routes through the AppServices bridge
    // (the window is owned by the main VM).
    [RelayCommand]
    private void OpenProfileManager() => AppServices.Current.OpenProfileManager();

    // Capture the outgoing board's credential edits before the incoming one
    // overwrites the fields. This is the only transition that can lose them, so
    // it's the only place that has to stage — cheaper than mirroring PushToCache
    // across every credential control and the menu-step / sys-goto row VMs.
    partial void OnSelectedBbsNameChanging(string? oldValue, string? newValue)
    {
        if (_suppressDirty || !_credentialsTouched) return;
        if (oldValue is { } previous) StageCredentials(previous);
    }

    partial void OnSelectedBbsNameChanged(string? value)
    {
        if (_suppressDirty) return;
        _suppressDirty = true;
        ReloadSelected();
        _suppressDirty = false;
    }

    // Snapshot the credential block for one board into the staging map.
    private void StageCredentials(string bbsName)
    {
        if (!HasProfile) return;
        _stagedCredentials.TryGetValue(bbsName, out StagedCredentials? previous);

        StagedCredentials staged = new()
        {
            Username = Username,
            SysopStatus = SysopStatus,
            SysopGodLives = SysopGodLives,
            SysopGoto = SysopGoto,
            MenuNavSteps = MenuNavSteps.Select(vm => vm.ToModel()).ToList(),
            AutoLogonSetup = AutoSetupShown()?.Name,
            SysopGotos = SysopGotos.Select(vm => vm.ToModel()).ToList(),
        };

        if (_pendingPassword is not null)
        {
            staged.PasswordTouched = true;
            staged.EncryptedPassword = _pendingPassword.Length == 0
                ? null
                : _passwords.Protect(_pendingPassword);
        }
        else if (previous is not null)
        {
            // Re-staging a board the user came back to but didn't retype the
            // password on — the box shows empty by design, so an earlier
            // password edit would read as "untouched" and be dropped here.
            staged.PasswordTouched = previous.PasswordTouched;
            staged.EncryptedPassword = previous.EncryptedPassword;
        }

        _stagedCredentials[bbsName] = staged;
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
        InfiniteRetries = profile.InfiniteRetries;
        CleanupPeriodMinutes = profile.CleanupPeriodMinutes;
        NoResponseTimeoutSeconds = profile.NoResponseTimeoutSeconds;
        foreach (GmcpPackageOption option in GmcpPackages)
            option.Mode = profile.GmcpModes?.GetValueOrDefault(option.Package, Game.Gmcp.GmcpMode.Shadow) ?? Game.Gmcp.GmcpMode.Shadow;
        LoadGmcpState(profile);
        ReconnectOnFailedConnect = profile.ReconnectOnFailedConnect;
        ReconnectOnCarrierLost = profile.ReconnectOnCarrierLost;
        ReconnectOnNoResponse = profile.ReconnectOnNoResponse;
        ReconnectAfterCleanup = profile.ReconnectAfterCleanup;
        TerminalCols = profile.TerminalCols;
        TerminalRows = profile.TerminalRows;
        ScrollbackLines = profile.ScrollbackLines;
        BackscrollWheelLines = profile.BackscrollWheelLines;
        DisconnectPattern = profile.DisconnectPattern;

        // Land on the loaded character's realm when this is its BBS.
        string? characterRealm = CanPickCharacterRealm
            ? (_characterRealmTouched ? CharacterRealm : profile.RealmFor(_profile.Current?.Realm)?.Name)
            : null;
        LoadRealmList(profile, characterRealm);
        CharacterRealm = characterRealm;
        OnPropertyChanged(nameof(CanPickCharacterRealm));
    }

    private void LoadRealmList(BbsProfile profile, string? select)
    {
        RealmNames.Clear();
        foreach (RealmProfile r in profile.Realms) RealmNames.Add(r.Name);
        SelectedRealmName = profile.RealmFor(select)?.Name;
        LoadRealmFields();
        RefreshRealmCharacters();
    }

    // The realm selected in the list, inside the cached BbsProfile being edited.
    private RealmProfile? SelectedRealm() =>
        SelectedBbsName is { } bbs && _loaded.TryGetValue(bbs, out BbsProfile? profile)
            ? profile.Realms.FirstOrDefault(r =>
                string.Equals(r.Name, SelectedRealmName, StringComparison.OrdinalIgnoreCase))
            : null;

    private void LoadRealmFields()
    {
        RealmProfile realm = SelectedRealm() ?? new RealmProfile();
        RealmName = realm.Name;
        LoadRealmGameDataSet(realm.ActiveGameDataSet);
        GameEntryCommand = realm.GameEntryCommand;
        GameExitCommand = realm.GameExitCommand;
        PlayerDiesAtHp = realm.PlayerDiesAtHp;
        AutoRefineDeathFloor = realm.AutoRefineDeathFloor;
        PvpEnabled = realm.PvpEnabled;
        HangupPenaltyEnabled = realm.HangupPenaltyEnabled;
        (HangupPvpHpFromPercent, HangupPvpHpToPercent) = Game.Health.HangupPenaltyNotice.HpRange(
            realm.HangupPvpHpFromPercent, realm.HangupPvpHpToPercent);
        HangupPvpItemsDropped = Game.Health.HangupPenaltyNotice.Items(realm.HangupPvpItemsDropped);
        HangupPvePenaltyEnabled = realm.HangupPvePenaltyEnabled;
        (HangupPveHpFromPercent, HangupPveHpToPercent) = Game.Health.HangupPenaltyNotice.HpRange(
            realm.HangupPveHpFromPercent, realm.HangupPveHpToPercent);
        HangupPveItemsDropped = Game.Health.HangupPenaltyNotice.Items(realm.HangupPveItemsDropped);
        HangupOutsideFightPenaltyEnabled = realm.HangupOutsideFightPenaltyEnabled;
        CleanupTimeOfDay = realm.CleanupTimeOfDay;
        CleanupTimeZoneId = realm.CleanupTimeZoneId;
        RunicCurrencyName = realm.RunicCurrencyName;
    }

    // The new entry goes in before it's selected and the old one comes out after,
    // so the picker never loses its selection to a removed item.
    private void LoadRealmGameDataSet(string? set)
    {
        string? staleOption = _missingSetOption;
        bool missing = set is not null && !GameDataSetOptions.Contains(set);
        _missingSet = missing ? set : null;
        _missingSetOption = missing ? NotInstalledOption(set!) : null;
        if (_missingSetOption is { } option && option != staleOption) GameDataSetOptions.Add(option);
        RealmGameDataSet = _missingSetOption ?? set ?? GlobalDefaultSet;
        if (staleOption is not null && staleOption != _missingSetOption) GameDataSetOptions.Remove(staleOption);
    }

    partial void OnSelectedRealmNameChanged(string? value)
    {
        RefreshRealmCharacters();
        bool prev = _suppressDirty;
        _suppressDirty = true;
        LoadRealmFields();
        _suppressDirty = prev;
    }

    // Rename the selected realm (the name box lost focus). Refused when blank,
    // not a valid folder name, or taken on this BBS.
    partial void OnRealmNameChanged(string value)
    {
        if (_suppressDirty) return;
        if (SelectedBbsName is not { } bbs || SelectedRealm() is not { } realm) return;
        string name = value?.Trim() ?? string.Empty;
        if (string.Equals(name, realm.Name, StringComparison.Ordinal)) return;

        string? problem = RealmCatalog.NameProblem(name,
            RealmNames.Where(n => !string.Equals(n, realm.Name, StringComparison.OrdinalIgnoreCase)));
        if (problem is not null)
        {
            AppServices.CurrentOrNull?.Dialogs.ShowInfo("Realm not renamed", problem);
            _suppressDirty = true;
            RealmName = realm.Name;
            _suppressDirty = false;
            return;
        }

        // Fold a chain of renames made before OK into one (old → newest).
        int earlier = _realmRenames.FindIndex(r =>
            string.Equals(r.Bbs, bbs, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.New, realm.Name, StringComparison.OrdinalIgnoreCase));
        if (earlier >= 0) _realmRenames[earlier] = _realmRenames[earlier] with { New = name };
        else _realmRenames.Add((bbs, realm.Name, name));

        // Replacing the list entry makes the list box and the character's realm
        // picker drop their selection, so re-select both with changes suppressed.
        // The character follows the rename through ProfileService.RenameRealm on OK.
        string oldName = realm.Name;
        bool characterOnIt = string.Equals(CharacterRealm, oldName, StringComparison.OrdinalIgnoreCase);
        string? characterRealm = characterOnIt ? name : CharacterRealm;
        realm.Name = name;
        _suppressDirty = true;
        int index = RealmNames.IndexOf(oldName);
        if (index >= 0) RealmNames[index] = name;
        SelectedRealmName = name;
        CharacterRealm = characterRealm;
        _suppressDirty = false;
        Dirty();
    }

    // Add an empty realm to the selected BBS and select it. It starts with the
    // default settings and no collected data.
    [RelayCommand]
    private void AddRealm()
    {
        if (SelectedBbsName is not { } bbs || !_loaded.TryGetValue(bbs, out BbsProfile? profile)) return;
        RealmProfile realm = new() { Name = RealmCatalog.NextFreeName(profile) };
        profile.Realms.Add(realm);
        RealmNames.Add(realm.Name);
        SelectedRealmName = realm.Name;
        Dirty();
    }

    // Remove the selected realm (a BBS keeps at least one). Removing a realm
    // deletes the characters that play it and its collected data, on OK — so a
    // realm with characters is confirmed by name first, and the realm the loaded
    // character plays can't be removed here.
    [RelayCommand(CanExecute = nameof(CanRemoveRealm))]
    private async Task RemoveRealmAsync()
    {
        if (SelectedBbsName is not { } bbs || !_loaded.TryGetValue(bbs, out BbsProfile? profile)) return;
        if (SelectedRealm() is not { } realm || profile.Realms.Count <= 1) return;
        if (AppServices.CurrentOrNull is not { } svcs) return;

        int renamed = _realmRenames.FindIndex(r =>
            string.Equals(r.Bbs, bbs, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.New, realm.Name, StringComparison.OrdinalIgnoreCase));
        string original = renamed >= 0 ? _realmRenames[renamed].Old : realm.Name;
        // A realm added in this window isn't on disk: nothing plays it, nothing to delete.
        bool saved = _bbsStore.Get(bbs)?.Realms.Any(r =>
            string.Equals(r.Name, original, StringComparison.OrdinalIgnoreCase)) == true;
        IReadOnlyList<ProfileRef> characters = saved ? svcs.Realms.CharactersOn(bbs, original) : Array.Empty<ProfileRef>();

        if (characters.Any(c => string.Equals(c.Bbs, _profile.CurrentBbsName, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(c.Name, _profile.CurrentProfileName, StringComparison.Ordinal)))
        {
            svcs.Dialogs.ShowInfo("Realm not removed",
                $"Your loaded character plays “{realm.Name}”. Disconnect and remove it from Profile Management instead.");
            return;
        }
        if (saved)
        {
            (string body, string yes) = RealmCatalog.RemovalPrompt(bbs, realm.Name, characters);
            bool confirmed = characters.Count > 0
                ? await svcs.Confirm.ConfirmAsync("Remove realm", body, yes)
                : await svcs.Confirm.ConfirmDeleteAsync($"the realm “{realm.Name}” from “{bbs}”");
            if (!confirmed) return;
        }

        profile.Realms.Remove(realm);
        if (renamed >= 0) _realmRenames.RemoveAt(renamed);
        if (saved) _realmRemovals.Add((bbs, original, characters));

        // Removing the list entry clears the pickers' selections; restore them with
        // changes suppressed.
        string? characterRealm =
            string.Equals(CharacterRealm, realm.Name, StringComparison.OrdinalIgnoreCase)
                ? profile.Realms[0].Name : CharacterRealm;
        _suppressDirty = true;
        RealmNames.Remove(realm.Name);
        SelectedRealmName = profile.Realms[0].Name;
        CharacterRealm = characterRealm;
        _suppressDirty = false;
        Dirty();
    }

    private bool CanRemoveRealm() => SelectedRealmName is not null && RealmNames.Count > 1;

    partial void OnCharacterRealmChanged(string? value)
    {
        if (_suppressDirty) return;
        _characterRealmTouched = true;
        RefreshRealmCharacters();
        Dirty();
    }

    // OK: carry out the realm renames / removals — before a BBS rename, while the
    // folders still sit under the names they were made with. A renamed realm's BBS
    // is saved first, as RealmCatalog.Rename does: from the moment its folder
    // moves, the realm in play and the stores that follow it read the new name.
    private void ApplyRealmChanges()
    {
        if (AppServices.CurrentOrNull?.Realms is not { } realms) return;
        foreach (string bbs in _realmRenames.Select(r => r.Bbs).Distinct(StringComparer.OrdinalIgnoreCase))
            if (_loaded.TryGetValue(bbs, out BbsProfile? renamedIn)) SaveLoaded(renamedIn);
        foreach ((string bbs, string oldName, string newName) in _realmRenames)
            realms.MoveData(bbs, oldName, newName);
        foreach ((string bbs, string name, IReadOnlyList<ProfileRef> characters) in _realmRemovals)
            realms.DeleteContents(bbs, name, characters);
    }

    private void SaveLoaded(BbsProfile profile)
    {
        // The website is now edited under Settings → Toolbar + Shortcuts and
        // may have just been written there in the same OK. Re-read the
        // on-disk value into this cached copy so our save folds it in rather
        // than clobbering it with the WebsiteUrl loaded at selection time.
        if (_bbsStore.Get(profile.Name) is { WebsiteUrl: var url })
            profile.WebsiteUrl = url;
        // The realms renamed here, so a count written to one under its old name
        // meanwhile follows it to the new name.
        Dictionary<string, string> renamed = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string bbs, string oldName, string newName) in _realmRenames)
            if (string.Equals(bbs, profile.Name, StringComparison.OrdinalIgnoreCase)) renamed[oldName] = newName;
        _bbsStore.Save(profile, renamed);
    }

    // OK: put the loaded character on the realm picked for it.
    private bool ApplyCharacterRealm()
    {
        if (!_characterRealmTouched || _profile.Current is not { } character) return false;
        if (string.Equals(character.Realm, CharacterRealm, StringComparison.OrdinalIgnoreCase)) return false;
        character.Realm = CharacterRealm;
        _profile.Save();
        AppServices.CurrentOrNull?.Log.Info("BBS",
            $"Character '{_profile.CurrentProfileName ?? "(draft)"}' now plays realm '{CharacterRealm}'.");
        return true;
    }

    private void LoadCredentialsFor(string bbsName)
    {
        _pendingPassword = null;
        _credentialsTouched = false;
        _autoFilledSetup = null;
        MenuNavSteps.Clear();
        SysopGotos.Clear();
        if (!HasProfile)
        {
            Username = string.Empty;
            Password = string.Empty;
            SysopStatus = SysopGodLives = SysopGoto = false;
            return;
        }

        // Pending edits win over what's on the profile — same contract _loaded
        // gives the BBS-profile fields, so clicking back and forth between two
        // boards doesn't reset either one's half-finished login.
        if (_stagedCredentials.TryGetValue(bbsName, out StagedCredentials? staged))
        {
            Username = staged.Username;
            Password = string.Empty;
            SysopStatus = staged.SysopStatus;
            SysopGodLives = staged.SysopGodLives;
            SysopGoto = staged.SysopGoto;
            foreach (MenuStep step in staged.MenuNavSteps)
                MenuNavSteps.Add(MenuStepEditorViewModel.FromModel(step, CredentialsDirty));
            _autoFilledSetup = BuiltInBbses.Named(staged.AutoLogonSetup);
            foreach (SysopGotoLocation loc in staged.SysopGotos)
                SysopGotos.Add(SysopGotoRowViewModel.FromModel(loc, CredentialsDirty, RoomName));
            RefreshImportSources(bbsName);
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
            SysopStatus = cred.SysopStatus;
            SysopGodLives = cred.SysopGodLives;
            SysopGoto = cred.SysopGoto;
            foreach (MenuStep step in cred.MenuNavSteps)
            {
                MenuNavSteps.Add(MenuStepEditorViewModel.FromModel(step, CredentialsDirty));
            }
            // Steps the client filled in itself show as connect will walk them: the
            // setup installed game data calls for now takes their place.
            _autoFilledSetup = BuiltInBbses.AutoFilled(cred);
            if (_autoFilledSetup is not null && _loaded.TryGetValue(bbsName, out BbsProfile? savedBoard))
                FillSetupWhereBlank(BuiltInBbses.ForInstalledSet(RealmSetFor(savedBoard, bbsName)), stepsOnly: true);
            foreach (SysopGotoLocation loc in cred.SysopGotos)
            {
                SysopGotos.Add(SysopGotoRowViewModel.FromModel(loc, CredentialsDirty, RoomName));
            }
        }
        else
        {
            Username = string.Empty;
            Password = string.Empty;
            SysopStatus = SysopGodLives = SysopGoto = false;
            // A BBS with no credential yet still shows the starter goto locations, so
            // saving it persists them instead of an empty table — CommitCredentials
            // writes this collection wholesale over the model's own starter default,
            // so the seed has to live here too (mirrors BbsCredentials.SysopGotos).
            foreach (SysopGotoLocation loc in SysopGotoLocation.DefaultStarterSet())
            {
                SysopGotos.Add(SysopGotoRowViewModel.FromModel(loc, CredentialsDirty, RoomName));
            }
            // The logon steps come filled in, the setup the realm's game data calls
            // for, so typing a username and password and pressing OK is all the login
            // needs.
            if (_loaded.TryGetValue(bbsName, out BbsProfile? board))
            {
                _autoFilledSetup = BuiltInBbses.ForGameDataSet(RealmSetFor(board, bbsName));
                AddLogonSteps(_autoFilledSetup);
            }
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
        // Runs on profile load / close. Credentials are per-character, so a swap
        // while the window is open invalidates everything staged — committing it
        // would write the outgoing character's logins onto the incoming one.
        ResetCredentialStaging();
        _characterRealmTouched = false;
        HasProfile = _profile.Current is not null;
        OnPropertyChanged(nameof(CredentialsHint));
        OnPropertyChanged(nameof(IsCredentialsHintWarning));
        if (SelectedBbsName is not null)
        {
            // ReloadSelected reloads the credentials and the character's realm.
            _suppressDirty = true;
            ReloadSelected();
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
        InfiniteRetries = defaults.InfiniteRetries;
        CleanupPeriodMinutes = defaults.CleanupPeriodMinutes;
        NoResponseTimeoutSeconds = defaults.NoResponseTimeoutSeconds;
        foreach (GmcpPackageOption option in GmcpPackages) option.Mode = Game.Gmcp.GmcpMode.Shadow;
        ReconnectOnFailedConnect = defaults.ReconnectOnFailedConnect;
        ReconnectOnCarrierLost = defaults.ReconnectOnCarrierLost;
        ReconnectOnNoResponse = defaults.ReconnectOnNoResponse;
        ReconnectAfterCleanup = defaults.ReconnectAfterCleanup;
        TerminalCols = defaults.TerminalCols;
        TerminalRows = defaults.TerminalRows;
        ScrollbackLines = defaults.ScrollbackLines;
        BackscrollWheelLines = defaults.BackscrollWheelLines;
        DisconnectPattern = defaults.DisconnectPattern;
        RealmNames.Clear();
        SelectedRealmName = null;
        LoadRealmFields();
        CharacterRealm = null;
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

    // Dirty() for the credential block. The extra flag arms the staging pass on
    // the next board switch, so untouched boards the user merely clicked through
    // don't get a credential entry authored for them.
    private void CredentialsDirty()
    {
        if (_suppressDirty) return;
        _credentialsTouched = true;
        Dirty();
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
        profile.InfiniteRetries = InfiniteRetries;
        profile.CleanupPeriodMinutes = CleanupPeriodMinutes;
        profile.NoResponseTimeoutSeconds = NoResponseTimeoutSeconds;
        profile.GmcpModes = GmcpPackages.ToDictionary(p => p.Package, p => p.Mode);
        profile.ReconnectOnFailedConnect = ReconnectOnFailedConnect;
        profile.ReconnectOnCarrierLost = ReconnectOnCarrierLost;
        profile.ReconnectOnNoResponse = ReconnectOnNoResponse;
        profile.ReconnectAfterCleanup = ReconnectAfterCleanup;
        profile.TerminalCols = TerminalCols;
        profile.TerminalRows = TerminalRows;
        profile.ScrollbackLines = ScrollbackLines;
        profile.BackscrollWheelLines = BackscrollWheelLines;
        profile.DisconnectPattern = string.IsNullOrWhiteSpace(DisconnectPattern)
            ? null : DisconnectPattern.Trim();

        if (SelectedRealm() is not { } realm) return;
        RealmProfile defaults = new();
        realm.ActiveGameDataSet = StoredRealmSet();
        realm.GameEntryCommand = string.IsNullOrWhiteSpace(GameEntryCommand)
            ? defaults.GameEntryCommand : GameEntryCommand.Trim();
        realm.GameExitCommand = string.IsNullOrWhiteSpace(GameExitCommand)
            ? defaults.GameExitCommand : GameExitCommand.Trim();
        // Death floor is a negative-HP value; a positive entry is meaningless
        // (0 HP already means dropped), so clamp to <= 0 at the point of storage.
        realm.PlayerDiesAtHp = Math.Min(0, PlayerDiesAtHp);
        realm.AutoRefineDeathFloor = AutoRefineDeathFloor;
        realm.PvpEnabled = PvpEnabled;
        // The figures are kept when a switch goes off, so ticking it again brings
        // back what was entered. Stored inside the board's own limits.
        realm.HangupPenaltyEnabled = HangupPenaltyEnabled;
        (realm.HangupPvpHpFromPercent, realm.HangupPvpHpToPercent) =
            Game.Health.HangupPenaltyNotice.HpRange(HangupPvpHpFromPercent, HangupPvpHpToPercent);
        realm.HangupPvpItemsDropped = Game.Health.HangupPenaltyNotice.Items(HangupPvpItemsDropped);
        realm.HangupPvePenaltyEnabled = HangupPvePenaltyEnabled;
        (realm.HangupPveHpFromPercent, realm.HangupPveHpToPercent) =
            Game.Health.HangupPenaltyNotice.HpRange(HangupPveHpFromPercent, HangupPveHpToPercent);
        realm.HangupPveItemsDropped = Game.Health.HangupPenaltyNotice.Items(HangupPveItemsDropped);
        realm.HangupOutsideFightPenaltyEnabled = HangupOutsideFightPenaltyEnabled;
        realm.CleanupTimeOfDay = CleanupTimeOfDay?.Trim() ?? string.Empty;
        realm.CleanupTimeZoneId = string.IsNullOrWhiteSpace(CleanupTimeZoneId)
            ? defaults.CleanupTimeZoneId : CleanupTimeZoneId.Trim();
        realm.RunicCurrencyName = string.IsNullOrWhiteSpace(RunicCurrencyName)
            ? defaults.RunicCurrencyName : RunicCurrencyName.Trim();
    }

    partial void OnNameChanged(string value)                    { Dirty(); }
    partial void OnUsernameChanged(string value)                { CredentialsDirty(); }
    partial void OnPasswordChanged(string value)
    {
        if (_suppressDirty) return;
        _pendingPassword = value;
        CredentialsDirty();
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

        // A password edited earlier in this window sits in the staging map, not
        // on the profile — reveal what OK would write, not what's on disk. A
        // staged clear (touched with no blob) correctly reveals nothing.
        string? blob;
        if (_stagedCredentials.TryGetValue(bbs, out StagedCredentials? staged) && staged.PasswordTouched)
        {
            blob = staged.EncryptedPassword;
        }
        else
        {
            CharacterProfile? character = _profile.Current;
            if (character?.BbsCredentials is null) return;
            if (!character.BbsCredentials.TryGetValue(bbs, out BbsCredentials? cred)) return;
            blob = cred.EncryptedPassword;
        }
        if (blob is null) return;

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
    partial void OnHostChanged(string value)                    { PushToCache(); Dirty(); MaybeSignalHostPort(); }
    partial void OnPortChanged(int value)                       { PushToCache(); Dirty(); MaybeSignalHostPort(); }

    private void OnTourActionChanged()
        => HighlightHostPort = AppServices.CurrentOrNull?.CurrentTourAction == GuideActions.BbsHostPort;

    // First-run tour: the host/port line ticks once the USER enters a host (the
    // port has a sensible default). Gated on _suppressDirty so loading or
    // switching to a BBS that already has a host (a programmatic field change,
    // not a user edit) doesn't tick it — that was skipping the step before
    // anything was typed.
    private void MaybeSignalHostPort()
    {
        if (_suppressDirty) return;
        if (!string.IsNullOrWhiteSpace(Host) && Port > 0)
            AppServices.CurrentOrNull?.NotifyTourAction?.Invoke(GuideActions.BbsHostPort);
    }
    partial void OnMaxRedialsChanged(int value)                 { PushToCache(); Dirty(); }
    partial void OnRedialPauseSecondsChanged(int value)         { PushToCache(); Dirty(); }
    partial void OnInfiniteRetriesChanged(bool value)           { OnPropertyChanged(nameof(RedialTickersEnabled)); PushToCache(); Dirty(); }
    partial void OnCleanupPeriodMinutesChanged(int value)       { PushToCache(); Dirty(); }
    partial void OnNoResponseTimeoutSecondsChanged(int value)   { PushToCache(); Dirty(); }
    partial void OnReconnectOnFailedConnectChanged(bool value)  { PushToCache(); Dirty(); }
    partial void OnReconnectOnCarrierLostChanged(bool value)    { PushToCache(); Dirty(); }
    partial void OnReconnectOnNoResponseChanged(bool value)     { PushToCache(); Dirty(); }
    partial void OnReconnectAfterCleanupChanged(bool value)     { PushToCache(); Dirty(); }
    partial void OnSysopStatusChanged(bool value)               { CredentialsDirty(); }
    partial void OnSysopGodLivesChanged(bool value)             { CredentialsDirty(); }
    partial void OnSysopGotoChanged(bool value)                 { CredentialsDirty(); }
    partial void OnTerminalColsChanged(int value)               { PushToCache(); Dirty(); }
    partial void OnTerminalRowsChanged(int value)               { PushToCache(); Dirty(); }

    partial void OnScrollbackLinesChanged(int value)            { PushToCache(); Dirty(); }
    partial void OnBackscrollWheelLinesChanged(int value)       { PushToCache(); Dirty(); }
    partial void OnGameEntryCommandChanged(string value)        { PushToCache(); Dirty(); }
    partial void OnGameExitCommandChanged(string value)         { PushToCache(); Dirty(); }
    partial void OnPlayerDiesAtHpChanged(int value)             { PushToCache(); Dirty(); }
    partial void OnAutoRefineDeathFloorChanged(bool value)      { PushToCache(); Dirty(); }
    partial void OnPvpEnabledChanged(bool value)                { PushToCache(); Dirty(); }
    partial void OnHangupPenaltyEnabledChanged(bool value)      { PushToCache(); Dirty(); }
    partial void OnHangupPvpItemsDroppedChanged(int value)      { PushToCache(); Dirty(); }
    partial void OnHangupPvePenaltyEnabledChanged(bool value)   { PushToCache(); Dirty(); }
    partial void OnHangupPveItemsDroppedChanged(int value)      { PushToCache(); Dirty(); }
    partial void OnHangupOutsideFightPenaltyEnabledChanged(bool value) { PushToCache(); Dirty(); }

    // The board takes a share between the lower figure and the upper, so moving
    // one past the other carries the other along and the pair on screen is always
    // the pair that gets saved.
    partial void OnHangupPvpHpFromPercentChanged(int value)
    {
        if (HangupPvpHpToPercent < value) HangupPvpHpToPercent = value;
        PushToCache(); Dirty();
    }

    partial void OnHangupPvpHpToPercentChanged(int value)
    {
        if (HangupPvpHpFromPercent > value) HangupPvpHpFromPercent = value;
        PushToCache(); Dirty();
    }

    partial void OnHangupPveHpFromPercentChanged(int value)
    {
        if (HangupPveHpToPercent < value) HangupPveHpToPercent = value;
        PushToCache(); Dirty();
    }

    partial void OnHangupPveHpToPercentChanged(int value)
    {
        if (HangupPveHpFromPercent > value) HangupPveHpFromPercent = value;
        PushToCache(); Dirty();
    }
    partial void OnCleanupTimeOfDayChanged(string value)        { PushToCache(); Dirty(); }
    partial void OnCleanupTimeZoneIdChanged(string value)       { PushToCache(); Dirty(); }
    partial void OnDisconnectPatternChanged(string? value)      { PushToCache(); Dirty(); }
    partial void OnRunicCurrencyNameChanged(string value)       { PushToCache(); Dirty(); }
    partial void OnRealmGameDataSetChanged(string value)
    {
        PushToCache();
        Dirty();
        if (!_suppressDirty)
            FillSetupWhereBlank(BuiltInBbses.ForInstalledSet(StoredRealmSet() ?? _globalSettings.Current.DefaultGameDataSet));
    }

    // ----- Logon setups ---------------------------------------------------

    // The setup the client filled the shown logon steps in from on its own (a
    // character with no login saved, or one saved that way). While the steps are
    // still exactly that setup's they count as blank, so the setup the realm's game
    // data calls for can take their place. Null once the player applies one by hand.
    private BuiltInBbses.Preset? _autoFilledSetup;

    private BuiltInBbses.Preset? AutoSetupShown() =>
        _autoFilledSetup is { } setup && setup.HasSteps(MenuNavSteps.Select(vm => vm.ToModel())) ? setup : null;

    // The game data set of the realm the loaded character plays on the board (the
    // board's first realm for another board), or the global default it uses.
    private string? RealmSetFor(BbsProfile board, string bbsName)
    {
        string? realm = string.Equals(bbsName, _profile.CurrentBbsName, StringComparison.OrdinalIgnoreCase)
            ? _profile.Current?.Realm : null;
        return board.RealmFor(realm)?.ActiveGameDataSet ?? _globalSettings.Current.DefaultGameDataSet;
    }

    private void AddLogonSteps(BuiltInBbses.Preset setup)
    {
        foreach (MenuStep step in setup.CopyLogonSteps())
            MenuNavSteps.Add(MenuStepEditorViewModel.FromModel(step, CredentialsDirty));
    }

    // A game data set chosen for the realm fills in only what's blank: a host
    // already typed, or logon steps the player set, stay as they are. Null (no
    // data installed to go by) fills nothing.
    private void FillSetupWhereBlank(BuiltInBbses.Preset? setup, bool stepsOnly = false)
    {
        if (setup is null) return;
        if (!stepsOnly && setup.Host is not null && string.IsNullOrWhiteSpace(Host))
        {
            Host = setup.Host;
            Port = setup.Port;
        }
        if (!HasProfile || (MenuNavSteps.Count > 0 && AutoSetupShown() is null) || AutoSetupShown() == setup) return;
        MenuNavSteps.Clear();
        AddLogonSteps(setup);
        _autoFilledSetup = setup;
        CredentialsDirty();
    }

    // The chosen setup over what's there, the host and port too for a board's
    // setup. Pending like any other edit until OK.
    [RelayCommand]
    private void ApplyLogonSetup(string name)
    {
        if (_suppressDirty || BuiltInBbses.Named(name) is not { } setup) return;
        if (setup.Host is not null)
        {
            Host = setup.Host;
            Port = setup.Port;
        }
        if (!HasProfile) return;
        MenuNavSteps.Clear();
        AddLogonSteps(setup);
        _autoFilledSetup = null;
        CredentialsDirty();
    }

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
        MenuNavSteps.Add(new MenuStepEditorViewModel(CredentialsDirty));
        CredentialsDirty();
    }

    [RelayCommand]
    private void AddSysopGoto()
    {
        if (_suppressDirty) return;
        SysopGotos.Add(new SysopGotoRowViewModel(CredentialsDirty, RoomName));
        CredentialsDirty();
    }

    [RelayCommand]
    private void RemoveSysopGoto(SysopGotoRowViewModel? row)
    {
        if (row is null || _suppressDirty) return;
        if (!SysopGotos.Remove(row)) return;
        CredentialsDirty();
    }

    // Resolve a landing room's name for a row's read-only preview cell. Reads the
    // live active-set graph; null-safe before the services start (the
    // preview binding simply shows "(unknown room)").
    private static string? RoomName(int map, int room)
        => AppServices.Current is { } svc
            ? svc.RoomGraph.GetRoom(new Game.Map.RoomKey(map, room))?.Name
            : null;

    [RelayCommand]
    private void RemoveMenuStep(MenuStepEditorViewModel? step)
    {
        if (step is null) return;
        if (!MenuNavSteps.Remove(step)) return;
        CredentialsDirty();
    }

    [RelayCommand]
    private void MoveMenuStepUp(MenuStepEditorViewModel? step)
    {
        if (step is null) return;
        int i = MenuNavSteps.IndexOf(step);
        if (i <= 0) return;
        MenuNavSteps.Move(i, i - 1);
        CredentialsDirty();
    }

    [RelayCommand]
    private void MoveMenuStepDown(MenuStepEditorViewModel? step)
    {
        if (step is null) return;
        int i = MenuNavSteps.IndexOf(step);
        if (i < 0 || i >= MenuNavSteps.Count - 1) return;
        MenuNavSteps.Move(i, i + 1);
        CredentialsDirty();
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
            MenuNavSteps.Add(MenuStepEditorViewModel.FromModel(step, CredentialsDirty));
        CredentialsDirty();
    }

    private bool CanImportMenuNav() => SelectedImportSource is not null;
}
