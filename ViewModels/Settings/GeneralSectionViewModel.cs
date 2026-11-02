using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.Views.Settings;

namespace MudPlay.ViewModels.Settings;

// "General" tab. Per-character: default startup task, auto-connect, the
// boot-up (base) state of every auto-engine and whether each is turned back
// on after a reconnect.
//
// Loads from CharacterProfile.Settings on construction and after a
// ProfileService.ProfileLoaded event so the editor reflects whichever profile is
// currently active. Apply writes back into the same JSON dictionary and calls
// ProfileService.Save.
public sealed partial class GeneralSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "General";

    private readonly ProfileService _profile;
    private readonly SettingsService _globalSettings;
    private bool _suppressDirty = true;
    private bool _dirty;

    public override string Id => "general";
    public override string Title => "General";
    public override bool IsDirty => _dirty;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "General", "Data files", "Open Data folder", "Change data directory",
        "Auto-connect", "Default task", "Do nothing",
        "Begin loop", "Begin Auto-Lair", "Backup profile",
        "Terminal font", "Font", "Font family", "Font size",
        "Visual impairment", "Colour blindness", "Color blindness", "Colour blind", "Color blind",
        "Terminal colours", "Terminal colors", "ANSI colours", "ANSI colors",
        "Deuteranopia", "Protanopia", "Tritanopia", "Custom colours", "Accessibility", "Contrast",
        "Navigation tooltip font", "Navigation tooltip", "Map tooltip font",
        "Scale terminal to window",
        "Check for updates automatically", "Check for updates", "Updates",
        "Tab-complete inventory item names", "Tab autocomplete", "Inventory autocomplete",
        "Manual-Mode Defaults", "Auto-Mode Defaults",
        "Auto-Engines enabled on start",
        "Auto-Combat", "Auto-Nuke",
        "Auto-Heal", "Auto-Rest", "Auto-Bless", "Auto-Light", "Auto-Train",
        "Allow hangup in all-off mode",
        "Re-enable on reconnect", "Re-enable Auto-Combat", "Re-enable Auto-Nuke",
        "Re-enable Auto-Heal/Rest", "Re-enable Auto-Bless", "Re-enable Auto-Light",
        "Re-enable Auto-Get Items", "Re-enable Auto-Get Cash", "Re-enable Auto-Sneak",
        "Re-enable Auto-Hide", "Re-enable Auto-Search", "Re-enable Auto-Train",
    };

    protected override Control BuildView() => new GeneralSectionView { DataContext = this };

    // True when a profile is loaded — editor is hidden otherwise.
    public bool HasProfile => _profile.Current is not null;

    // Resolved absolute path to the platform Data root (XDG on Linux, %AppData% on
    // Windows, ~/Library/Application Support on macOS). Read-only display so the
    // user can copy the path or open it in the system file browser via the
    // adjacent button.
    public string DataFilesPath => AppPaths.DataRoot;

    // Opens the Data root in the OS file browser.
    [RelayCommand]
    private void OpenDataFolder()
    {
        if (!ShellLaunch.OpenPath(AppPaths.DataRoot))
            AppServices.Current.Log.Warn("ShellLaunch", $"Could not open {AppPaths.DataRoot}");
    }

    // Opens the "Change data directory" modeless dialog. On confirm the dialog runs
    // DataRootRelocator and restarts the app at the new location; this method's
    // task completes only on Cancel.
    [RelayCommand]
    private async Task ChangeDataFolderAsync()
    {
        DataRootRelocator.MovePlan plan = DataRootRelocator.Plan();
        DataDirectoryRelocateDialogViewModel vm = new(AppPaths.DataRoot, plan);
        await AppServices.Current.Dialogs.OpenWindowAsync<
            DataDirectoryRelocateDialogViewModel, bool>(vm);
    }

    // ----- Initial task (three radios — mutual exclusion handled by GroupName) -----
    [ObservableProperty] private bool _isTaskDoNothing = true;
    [ObservableProperty] private bool _isTaskBeginLoop;
    [ObservableProperty] private bool _isTaskBeginAutoLair;

    [ObservableProperty] private string? _defaultLoopName;
    [ObservableProperty] private string? _defaultAutoLairName;
    [ObservableProperty] private bool _autoConnect;
    [ObservableProperty] private bool _loadLastRanLoop = true;
    // Char-tier (GeneralSettings.AutoSyncQuestFlagsOnLogin). Read live by
    // QuestFlagSyncManager at login. Off by default — it sends commands on connect.
    [ObservableProperty] private bool _autoSyncQuestFlagsOnLogin;
    [ObservableProperty] private bool _backupOnSave;
    [ObservableProperty] private bool _scaleTerminalToWindow;
    [ObservableProperty] private bool _typeToTerminalFromOtherWindows = true;
    [ObservableProperty] private bool _inventoryTabCompleteEnabled = true;
    [ObservableProperty] private bool _showStartupMudAnimation = true;
    // Install-global (GlobalSettings.SnapWindows), read live by WindowSnapManager.
    [ObservableProperty] private bool _snapWindows = true;
    // Install-global (GlobalSettings.DrawWithGraphicsCard), read from the file at
    // launch: a change waits for the next one.
    [ObservableProperty] private bool _drawWithGraphicsCard;
    // Install-global (GlobalSettings.AutoCheckForUpdates): a background check against
    // GitHub Releases on launch and twice a day after, flagging a newer build (splash
    // banner, title-bar crawl, Help / Tools → Update the Client). Never installs on
    // its own.
    [ObservableProperty] private bool _autoCheckForUpdates = true;

    // Install-global (GlobalSettings.LocalApiEnabled / LocalApiPort). Opens a
    // loopback HTTP endpoint exposing live state + the program log for
    // diagnosing the client while it misbehaves. Off by default; the server
    // follows these values live via AppServices, so Apply is all it takes.
    [ObservableProperty] private bool _localApiEnabled;
    [ObservableProperty] private int _localApiPort = GlobalSettings.LocalApiDefaultPort;

    // Separate opt-in for commands that end the session or can't be undone.
    // Off by default — see GlobalSettings.LocalApiAllowDestructive.
    [ObservableProperty] private bool _localApiAllowDestructive;

    // Read-only mirrors for the status line under the checkbox, refreshed on
    // load: whether the socket actually came up, and why not if it didn't.
    [ObservableProperty] private string _localApiStatus = string.Empty;

    // The bearer token, blank until the user asks for it. Revealed rather than
    // copied to the clipboard because a VM has no TopLevel to reach a clipboard
    // through — same treatment as the stored suicide password, which the user
    // selects out of a read-only field.
    [ObservableProperty] private string _localApiToken = string.Empty;

    // Whether the socket is up right now, for the status line.
    private static Services.Api.LocalApiServer? Api => AppServices.Current?.LocalApi;

    private void RefreshLocalApiStatus()
    {
        if (Api is not { } api) { LocalApiStatus = string.Empty; return; }
        LocalApiStatus = api.IsListening
            ? $"Listening on http://127.0.0.1:{api.Port}/ — loopback only, token required."
            : api.LastStartError is { Length: > 0 } err
                ? $"Not listening — {err}"
                : "Not listening.";
    }

    // Reveal the token. Reading it mints one if none exists yet, which is why
    // this is a deliberate click rather than something the page does on open —
    // merely visiting Settings shouldn't create a credential.
    [RelayCommand]
    private void ShowLocalApiToken()
    {
        if (Api is not { } api) return;
        LocalApiToken = api.Auth.Token;
        RefreshLocalApiStatus();
    }

    // Replace the token, invalidating anything holding the old one. For when it
    // has been pasted somewhere it shouldn't have been.
    [RelayCommand]
    private void RegenerateLocalApiToken()
    {
        if (Api is not { } api) return;
        LocalApiToken = api.Auth.Regenerate();
        RefreshLocalApiStatus();
    }

    // Install-global (GlobalSettings.RecentProfilesShown): how many recent
    // profiles the File menu lists. Default 5; capped at MaxRecentProfilesShown.
    [ObservableProperty] private int _recentProfilesShown = 5;
    public int MaxRecentProfilesShown => GlobalSettings.MaxRecentProfilesShown;

    // Install-global (GlobalSettings.MapOtherFloorsLevels /
    // MapOtherFloorsMaxOverlapPercent): the Navigation map's shadowed other floors.
    // The open map picks them up off GlobalSettingsChanged.
    [ObservableProperty] private int _mapOtherFloorsLevels = 10;
    [ObservableProperty] private int _mapOtherFloorsMaxOverlapPercent = 50;
    [ObservableProperty] private bool _mapRouteLinesOnOtherFloors = true;
    public int MaxMapOtherFloorsLevels => GlobalSettings.MaxMapOtherFloorsLevels;

    // Buff Watchdog window layout — where the config table sits relative to the timer
    // bars. Persisted on the top-level CharacterProfile.BuffWatchdogLayout; the Buff
    // Watchdog reads it live (ProfileMutated) and reflows.
    public IReadOnlyList<BuffLayoutOption> BuffWatchdogLayoutOptions { get; } = new BuffLayoutOption[]
    {
        new("Config on top", BuffWatchdogLayout.ConfigTop),
        new("Config on bottom", BuffWatchdogLayout.ConfigBottom),
        new("Config on left", BuffWatchdogLayout.ConfigLeft),
        new("Config on right", BuffWatchdogLayout.ConfigRight),
    };

    [ObservableProperty] private BuffLayoutOption? _selectedBuffWatchdogLayout;

    // PlayerCleanupDays moved to Settings → Other per user direction.
    // GlobalSettings.PlayerCleanupDays remains the canonical store —
    // OtherSectionViewModel now owns the edit surface.

    // Saved loop names for the "Begin looping" picker, snapshotted from the
    // active game-data set's LoopManager. Loops are set-scoped, not per-character,
    // so this reflects whichever set is active when the tab loads. Refreshed on
    // profile reload (LoadFromProfile); like every other dropdown in the app, new
    // loops created elsewhere appear after a Settings close + reopen.
    public ObservableCollection<string> LoopNames { get; } = new();

    // Saved Auto-Lair names for the "Begin Auto-Lair" picker — same set-scoped
    // snapshot semantics as LoopNames.
    public ObservableCollection<string> AutoLairNames { get; } = new();

    // Navigation-line appearance rows (Global tier — see NavLineDefaults). One row
    // per map polyline; each edits colour + thickness. Built once, re-seeded from
    // GlobalSettings on every LoadFromProfile, written back in Apply.
    public ObservableCollection<NavLineStyleRowViewModel> NavLineRows { get; } = new();

    // The terminal's 16 base colours: the standard ones, a scheme for a kind of
    // colour blindness, or the user's own (Global tier). Seeded from GlobalSettings
    // on every LoadFromProfile, written back in Apply; the terminal repaints off
    // GlobalSettingsChanged.
    public TerminalColorsEditorViewModel TerminalColors { get; }

    // ----- Terminal font (char-tier) -----
    // Font family + size the terminal canvas renders with. Both used to live in
    // the per-BBS Display settings; they moved here so the choice follows the
    // character rather than whichever board it's connected to. The picker leads
    // with the two bundled faces — MX437 (the CP437 bitmap font that matches
    // classic BBS output) and JetBrains Mono — then lists every monospace font
    // installed on the system. Proportional faces are filtered out by
    // InstalledFontCatalog since they'd mangle the fixed cell grid. The default
    // font and DisplayConfig.DefaultFontSize carry a "{default}" tag in the
    // picker labels; a bundled
    // face persists as its avares:// URI while a system font persists as its
    // bare family name (both are valid FontFamily inputs).
    // Mutable so a stored font or size the lists don't offer is added and shown, not
    // swapped for the default (see SelectFamily / SelectSize).
    public ObservableCollection<FontFamilyOption> FontFamilyOptions { get; } = new(BuildFontFamilyOptions());

    public ObservableCollection<FontSizeOption> FontSizeOptions { get; } =
        new(FontSizeOption.Standard(DisplayConfig.DefaultFontSize));

    [ObservableProperty] private FontFamilyOption? _selectedFontFamily;
    [ObservableProperty] private FontSizeOption? _selectedFontSize;

    // ----- Navigation map hover-tooltip font (char-tier) -----
    // The map room tooltip's font + size. Shares the terminal font's family list
    // (its default is the same MX437 face the tooltip has always rendered with,
    // tagged "{default}"); the size list tags 13 instead of 16 as the default.
    // Independent selections so the tooltip can be tuned separately from the
    // terminal canvas.
    public ObservableCollection<FontSizeOption> NavTooltipFontSizeOptions { get; } =
        new(FontSizeOption.Standard(DisplayConfig.DefaultNavTooltipFontSize));

    [ObservableProperty] private FontFamilyOption? _selectedNavTooltipFontFamily;
    [ObservableProperty] private FontSizeOption? _selectedNavTooltipFontSize;

    private static IReadOnlyList<FontFamilyOption> BuildFontFamilyOptions()
    {
        // Bundled faces first, in a fixed order, so the default stays at the top.
        List<FontFamilyOption> list = new()
        {
            new FontFamilyOption("MX437 IBM VGA {default}", DisplayConfig.DefaultFontFamily),
            new FontFamilyOption("JetBrains Mono",
                "avares://MudPlay/Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono"),
        };

        // Then every installed monospace font, skipping any that duplicates a
        // bundled face's family name so the picker never shows two identical
        // labels (e.g. a system-wide JetBrains Mono install).
        foreach (string name in InstalledFontCatalog.Monospace)
        {
            if (name.Equals("JetBrains Mono", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Mx437 IBM VGA 8x16", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new FontFamilyOption(name, name));
        }

        return list;
    }

    // ----- Auto-engine master switches -----
    // Each AmXxx bool is this character's base on/off for the matching engine,
    // persisted to GeneralSettings.AutoModeBase. The live AutoMode (toolbar /
    // Action menu) is reconciled to it at profile load and at a loop / auto-lair
    // start, not on Save. The XAML wires each CheckBox's IsEnabled to the matching
    // IsXxxWired flag so the user can see which engines are actually live vs
    // surface-only.
    [ObservableProperty] private bool _amAutoCombat;
    [ObservableProperty] private bool _amAutoNuke;
    [ObservableProperty] private bool _amAutoHeal;
    [ObservableProperty] private bool _amAutoRest;
    [ObservableProperty] private bool _amAutoBless;
    [ObservableProperty] private bool _amAutoLight;
    [ObservableProperty] private bool _amAutoGetItems;
    [ObservableProperty] private bool _amAutoGetCash;
    [ObservableProperty] private bool _amAutoSneak;
    [ObservableProperty] private bool _amAutoHide;
    [ObservableProperty] private bool _amAutoSearch;

    // Auto-train's boot state is a mirror onto AutoTrainerSettings.AutoTrain
    // (the "AutoTrainer" entry), not AutoMode — the Auto-Trainer tab is the
    // primary editor for it. Surfaced here for parity with the other engines'
    // enabled-on-start checkboxes.
    [ObservableProperty] private bool _amAutoTrain;

    // ----- Emergency hangup carve-out ---------------------------------
    // Lets the HealthManager emergency-hangup branch fire even when every
    // auto-engine is off. Sits next to the auto-engine switches because
    // it's the one safety net that survives all-off mode.
    [ObservableProperty] private bool _allowHangupInAllOffMode;

    // ----- Re-enable auto-actions on reconnect ------------------------
    // One flag per auto-action (1-to-1 with AutoMode above). On a
    // reconnect, each action whose flag is on gets flipped back ON in
    // AutoMode. Default off for every action.
    [ObservableProperty] private bool _reEnableAutoCombatOnReconnect;
    [ObservableProperty] private bool _reEnableAutoNukeOnReconnect;
    [ObservableProperty] private bool _reEnableAutoHealRestOnReconnect;
    [ObservableProperty] private bool _reEnableAutoBlessOnReconnect;
    [ObservableProperty] private bool _reEnableAutoLightOnReconnect;
    [ObservableProperty] private bool _reEnableAutoGetItemsOnReconnect;
    [ObservableProperty] private bool _reEnableAutoGetCashOnReconnect;
    [ObservableProperty] private bool _reEnableAutoSneakOnReconnect;
    [ObservableProperty] private bool _reEnableAutoHideOnReconnect;
    [ObservableProperty] private bool _reEnableAutoSearchOnReconnect;
    [ObservableProperty] private bool _reEnableAutoTrainOnReconnect;

    // ----- Wired-state flags ------------------------------------------
    // True when the matching engine is live. The view's CheckBox.IsEnabled binds
    // to these so users see at a glance which toggles do anything.
    public bool IsAutoCombatWired   => true;    // CombatManager
    public bool IsAutoHealWired     => true;    // CastingDirector heal / cure categories
    public bool IsAutoRestWired     => true;    // HealthManager
    public bool IsAutoNukeWired     => true;    // CombatSpellChooser multi-attack + debuff gate
    public bool IsAutoBlessWired    => true;    // CastingDirector Buffing-category gate
    public bool IsAutoLightWired    => true;    // AutoLightProvisioner + AutoLightShopRouter
    public bool IsAutoGetItemsWired => true;    // AutoGetItemsManager
    public bool IsAutoGetCashWired  => true;    // CashManager + StashRoomManager (both gate here)
    public bool IsAutoSneakWired    => true;    // StealthManager auto-sneak
    public bool IsAutoHideWired     => true;    // StealthManager auto-hide
    public bool IsAutoSearchWired   => true;    // AutoSearchManager — bare `sea` on room entry
    public bool IsAutoTrainWired    => true;    // AutoTrainer walk engine (AutoTrainerSettings.AutoTrain)

    public GeneralSectionViewModel(ProfileService profile)
        : this(profile, AppServices.Current.Settings) { }

    public GeneralSectionViewModel(ProfileService profile, SettingsService globalSettings)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(globalSettings);
        _profile = profile;
        _globalSettings = globalSettings;
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= OnProfileClosedExternally;
        });

        foreach (NavLineKind kind in NavLineDefaults.All)
            NavLineRows.Add(new NavLineStyleRowViewModel(kind, Dirty));
        TerminalColors = new TerminalColorsEditorViewModel(Dirty);

        LoadFromProfile();
        _suppressDirty = false;
    }

    // Reset every nav-line colour + thickness to its factory default (NavLineDefaults).
    // Marks dirty via the rows' change callbacks; Apply persists the cleared deltas.
    [RelayCommand]
    private void RestoreNavLineDefaults()
    {
        foreach (NavLineStyleRowViewModel row in NavLineRows)
            row.ResetCommand.Execute(null);
    }

    // Build the Global-tier delta from the rows, or null when every line is default.
    private NavLineStyles? SnapshotNavLines()
    {
        NavLineStyles styles = new();
        foreach (NavLineStyleRowViewModel row in NavLineRows)
            styles.Set(row.Kind, row.ToOverride());
        return styles.IsEmpty ? null : styles;
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        // The engine checkboxes edit the BASE modes, not the live toolbar state.
        // Preserve whatever AutoMode the toolbar currently holds so saving the
        // Settings tab never yanks the live engines around — the base takes effect
        // at the next profile load / loop / auto-lair start, not on Save.
        GeneralSettings existing = ReadOrDefault();

        GeneralSettings dto = new()
        {
            DefaultTask = IsTaskBeginLoop      ? InitialTask.BeginLoop
                        : IsTaskBeginAutoLair  ? InitialTask.BeginAutoLair
                        : InitialTask.DoNothing,
            DefaultLoopName     = string.IsNullOrWhiteSpace(DefaultLoopName)     ? null : DefaultLoopName,
            DefaultAutoLairName = string.IsNullOrWhiteSpace(DefaultAutoLairName) ? null : DefaultAutoLairName,
            AutoConnect = AutoConnect,
            LoadLastRanLoop = LoadLastRanLoop,
            AutoSyncQuestFlagsOnLogin = AutoSyncQuestFlagsOnLogin,
            BackupOnSave = BackupOnSave,
            ScaleTerminalToWindow = ScaleTerminalToWindow,
            TypeToTerminalFromOtherWindows = TypeToTerminalFromOtherWindows,
            InventoryTabCompleteEnabled = InventoryTabCompleteEnabled,
            ShowStartupMudAnimation = ShowStartupMudAnimation,
            // Store null when the default is selected so the delta stays clean
            // and follows the app default if it ever changes (the picker label
            // literally reads "{default}").
            TerminalFontFamily = SelectedFontFamily is { } ff
                && ff.Uri != DisplayConfig.DefaultFontFamily ? ff.Uri : null,
            TerminalFontSize = SelectedFontSize is { } fs
                && fs.Value != DisplayConfig.DefaultFontSize ? fs.Value : null,
            NavTooltipFontFamily = SelectedNavTooltipFontFamily is { } nff
                && nff.Uri != DisplayConfig.DefaultFontFamily ? nff.Uri : null,
            NavTooltipFontSize = SelectedNavTooltipFontSize is { } nfs
                && nfs.Value != DisplayConfig.DefaultNavTooltipFontSize ? nfs.Value : null,
            AutoMode     = existing.AutoMode,   // live toolbar state — untouched by this Save
            AutoModeBase = SnapshotAuto(),       // the base the checkboxes define
            AllowHangupInAllOffMode         = AllowHangupInAllOffMode,
            // Preserve the live "Disable hangups" toolbar toggle — this tab
            // doesn't edit it, and Apply rebuilds GeneralSettings from scratch, so
            // without carrying it forward a Save here would reset it to false.
            DisableHangups                  = existing.DisableHangups,
            // Same carry-through for the live "Sprint Mode" toolbar toggle.
            SprintMode                      = existing.SprintMode,
            ReEnableAutoCombatOnReconnect   = ReEnableAutoCombatOnReconnect,
            ReEnableAutoNukeOnReconnect     = ReEnableAutoNukeOnReconnect,
            ReEnableAutoHealRestOnReconnect = ReEnableAutoHealRestOnReconnect,
            ReEnableAutoBlessOnReconnect    = ReEnableAutoBlessOnReconnect,
            ReEnableAutoLightOnReconnect    = ReEnableAutoLightOnReconnect,
            ReEnableAutoGetItemsOnReconnect = ReEnableAutoGetItemsOnReconnect,
            ReEnableAutoGetCashOnReconnect  = ReEnableAutoGetCashOnReconnect,
            ReEnableAutoSneakOnReconnect    = ReEnableAutoSneakOnReconnect,
            ReEnableAutoHideOnReconnect     = ReEnableAutoHideOnReconnect,
            ReEnableAutoSearchOnReconnect   = ReEnableAutoSearchOnReconnect,
            ReEnableAutoTrainOnReconnect    = ReEnableAutoTrainOnReconnect,
        };

        profile.Settings ??= new();
        profile.Settings[TabKey] = SettingsSection.ToElement(dto);

        // Buff Watchdog layout is a top-level per-character field; write it straight
        // onto the profile (not the DTO). The open Buff Watchdog reflows off the
        // ProfileMutated notice fired after Save below — but only when it actually
        // changed, since this tab otherwise deliberately avoids the mutated fan-out.
        BuffWatchdogLayout newLayout = SelectedBuffWatchdogLayout?.Value ?? BuffWatchdogLayout.ConfigTop;
        bool layoutChanged = profile.BuffWatchdogLayout != newLayout;
        profile.BuffWatchdogLayout = newLayout;

        // Auto-train's boot flag isn't part of AutoMode — it lives in the
        // "AutoTrainer" entry the Auto-Trainer tab owns. Read-modify-write only
        // the AutoTrain bit so the tab's other fields (levels-to-keep, announce,
        // disabled trainers, AutoTrainStats) survive this Save. AutoTrainStats is
        // now independent of AutoTrain (decoupled), so it is NOT force-cleared here.
        AutoTrainerSettings trainer = ReadAutoTrainerOrDefault();
        if (trainer.AutoTrain != AmAutoTrain)
        {
            trainer.AutoTrain = AmAutoTrain;
            SettingsSection.Write(profile, "AutoTrainer", trainer, "the General tab's auto-train box");
        }

        _profile.Save(backup: BackupOnSave);

        // Push the char-tier display settings into the live DisplayConfig — a
        // plain profile Save fires neither ProfileLoaded nor ProfileMutated, so
        // the AppServices seed path won't run; this is what makes the change
        // reach the live canvas on Apply.
        AppServices.Current.Display.ScaleToWindow = ScaleTerminalToWindow;
        AppServices.Current.TerminalInput.Enabled = TypeToTerminalFromOtherWindows;
        AppServices.Current.InventoryTabCompleteEnabled = InventoryTabCompleteEnabled;
        AppServices.Current.Display.FontFamily =
            SelectedFontFamily?.Uri ?? DisplayConfig.DefaultFontFamily;
        AppServices.Current.Display.FontSize =
            SelectedFontSize?.Value ?? DisplayConfig.DefaultFontSize;
        AppServices.Current.Display.NavTooltipFontFamily =
            SelectedNavTooltipFontFamily?.Uri ?? DisplayConfig.DefaultFontFamily;
        AppServices.Current.Display.NavTooltipFontSize =
            SelectedNavTooltipFontSize?.Value ?? DisplayConfig.DefaultNavTooltipFontSize;
        AppServices.Current.Display.SplashAnimate = ShowStartupMudAnimation;
        // The splash is install-global — persist it onto the Global default profile
        // (regardless of which profile is loaded), so it holds across launches and
        // profile swaps rather than living on this character's tier.
        _profile.WriteDefaultProfileStartupAnimation(ShowStartupMudAnimation);

        // Nav-line appearance is install-global — persist to GlobalSettings and Save.
        // Save fires GlobalSettingsChanged, so the live map (NavigationViewModel
        // listens) repaints with the new colours / thickness immediately.
        _globalSettings.Current.NavLines = SnapshotNavLines();
        // Terminal colours are install-global too; AppServices pushes them to the
        // palette off the same GlobalSettingsChanged, and the terminal repaints.
        _globalSettings.Current.TerminalColors = TerminalColors.ToSettings();
        // Window snapping is install-global too; WindowSnapManager reads it live off
        // the same GlobalSettings, so this is all the wiring the toggle needs.
        _globalSettings.Current.SnapWindows = SnapWindows;
        _globalSettings.Current.DrawWithGraphicsCard = DrawWithGraphicsCard;
        // Startup update check is install-global; AppServices reads it once at launch.
        _globalSettings.Current.AutoCheckForUpdates = AutoCheckForUpdates;
        // The API server subscribes to GlobalSettingsChanged, so Save() below is
        // what opens or closes the socket — no extra call needed here.
        _globalSettings.Current.LocalApiEnabled = LocalApiEnabled;
        _globalSettings.Current.LocalApiPort =
            LocalApiPort is >= 1024 and <= 65535 ? LocalApiPort : GlobalSettings.LocalApiDefaultPort;
        _globalSettings.Current.LocalApiAllowDestructive = LocalApiAllowDestructive;
        _globalSettings.Current.RecentProfilesShown =
            System.Math.Clamp(RecentProfilesShown, 0, GlobalSettings.MaxRecentProfilesShown);
        _globalSettings.Current.MapOtherFloorsLevels =
            System.Math.Clamp(MapOtherFloorsLevels, 1, GlobalSettings.MaxMapOtherFloorsLevels);
        _globalSettings.Current.MapOtherFloorsMaxOverlapPercent =
            System.Math.Clamp(MapOtherFloorsMaxOverlapPercent, 0, 100);
        _globalSettings.Current.MapRouteLinesOnOtherFloors = MapRouteLinesOnOtherFloors;
        _globalSettings.Save();

        // A plain profile Save fires neither ProfileLoaded nor ProfileMutated, so the
        // open Buff Watchdog wouldn't pick up a layout change. Nudge it only when the
        // layout actually changed, to avoid firing the mutated fan-out on every Save.
        if (layoutChanged) _profile.NotifyMutated();

        // Turning quest-flag sync ON while already playing: fire the check now (the
        // login trigger already passed), then re-report the now-current available quests.
        // The manager no-ops when not in-realm, so flipping it at the menu is harmless.
        if (!existing.AutoSyncQuestFlagsOnLogin && AutoSyncQuestFlagsOnLogin)
            _ = AppServices.Current?.QuestFlagSync.RunNowAsync();

        ClearDirty();
    }

    public override void Discard()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        // Undo a font previewed on the terminal and not saved.
        AppServices.Current.Display.FontFamily = SelectedFontFamily?.Uri ?? DisplayConfig.DefaultFontFamily;
        AppServices.Current.Display.FontSize = SelectedFontSize?.Value ?? DisplayConfig.DefaultFontSize;
        ClearDirty();
    }

    private void OnProfileChanged(CharacterProfile _) => ReloadAfterProfileSwap();
    private void OnProfileClosedExternally() => ReloadAfterProfileSwap();

    private void ReloadAfterProfileSwap()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
        OnPropertyChanged(nameof(HasProfile));
    }

    private void LoadFromProfile()
    {
        GeneralSettings dto = ReadOrDefault();

        // Populate the pickers BEFORE assigning the saved selections, and keep
        // the saved names in the lists even when they aren't in the active set —
        // otherwise the ComboBox coerces a not-in-list selection back to null and
        // silently drops the character's default on the next Save.
        PopulateTaskNameLists(dto.DefaultLoopName, dto.DefaultAutoLairName);

        IsTaskDoNothing      = dto.DefaultTask == InitialTask.DoNothing;
        IsTaskBeginLoop      = dto.DefaultTask == InitialTask.BeginLoop;
        IsTaskBeginAutoLair  = dto.DefaultTask == InitialTask.BeginAutoLair;
        DefaultLoopName      = dto.DefaultLoopName;
        DefaultAutoLairName  = dto.DefaultAutoLairName;
        AutoConnect          = dto.AutoConnect;
        LoadLastRanLoop      = dto.LoadLastRanLoop;
        AutoSyncQuestFlagsOnLogin = dto.AutoSyncQuestFlagsOnLogin;
        BackupOnSave         = dto.BackupOnSave;
        ScaleTerminalToWindow = dto.ScaleTerminalToWindow;
        TypeToTerminalFromOtherWindows = dto.TypeToTerminalFromOtherWindows;
        InventoryTabCompleteEnabled = dto.InventoryTabCompleteEnabled;
        // The splash is install-global — always reflect the Global default profile's
        // value, not the loaded profile's (possibly stale) copy of the field.
        ShowStartupMudAnimation = _profile.ReadDefaultProfileStartupAnimation();

        // Nav-line appearance is Global-tier too — seed the rows from GlobalSettings
        // (delta), not the loaded profile.
        NavLineStyles? navLines = _globalSettings.Current.NavLines;
        foreach (NavLineStyleRowViewModel row in NavLineRows)
            row.Load(navLines?.Get(row.Kind));
        TerminalColors.Load(_globalSettings.Current.TerminalColors);

        // Window snapping is Global-tier — reflect the live GlobalSettings value.
        SnapWindows = _globalSettings.Current.SnapWindows;
        DrawWithGraphicsCard = _globalSettings.Current.DrawWithGraphicsCard;
        AutoCheckForUpdates = _globalSettings.Current.AutoCheckForUpdates;
        LocalApiEnabled = _globalSettings.Current.LocalApiEnabled;
        LocalApiPort = _globalSettings.Current.LocalApiPort;
        LocalApiAllowDestructive = _globalSettings.Current.LocalApiAllowDestructive;
        RefreshLocalApiStatus();
        RecentProfilesShown = _globalSettings.Current.RecentProfilesShown;
        MapOtherFloorsLevels = _globalSettings.Current.MapOtherFloorsLevels;
        MapOtherFloorsMaxOverlapPercent = _globalSettings.Current.MapOtherFloorsMaxOverlapPercent;
        MapRouteLinesOnOtherFloors = _globalSettings.Current.MapRouteLinesOnOtherFloors;

        // Buff Watchdog layout is a top-level per-character field (not in the DTO).
        BuffWatchdogLayout layout = _profile.Current?.BuffWatchdogLayout ?? BuffWatchdogLayout.ConfigTop;
        SelectedBuffWatchdogLayout = BuffWatchdogLayoutOptions.FirstOrDefault(o => o.Value == layout)
                                     ?? BuffWatchdogLayoutOptions[0];

        SelectedFontFamily = SelectFamily(dto.TerminalFontFamily);
        SelectedFontSize = SelectSize(FontSizeOptions, dto.TerminalFontSize ?? DisplayConfig.DefaultFontSize);
        SelectedNavTooltipFontFamily = SelectFamily(dto.NavTooltipFontFamily);
        SelectedNavTooltipFontSize = SelectSize(NavTooltipFontSizeOptions,
            dto.NavTooltipFontSize ?? DisplayConfig.DefaultNavTooltipFontSize);

        // The checkboxes show the BASE modes. A character from before the
        // base/live split has no AutoModeBase yet — fall back to the live AutoMode
        // so their current setup seeds the base seamlessly on first open.
        AutoActionDefaults a = dto.AutoModeBase ?? dto.AutoMode;
        AmAutoCombat   = a.AutoCombat;
        AmAutoNuke     = a.AutoNuke;
        AmAutoHeal     = a.AutoHeal;
        AmAutoRest     = a.AutoRest;
        AmAutoBless    = a.AutoBless;
        AmAutoLight    = a.AutoLight;
        AmAutoGetItems = a.AutoGetItems;
        AmAutoGetCash  = a.AutoGetCash;
        AmAutoSneak    = a.AutoSneak;
        AmAutoHide     = a.AutoHide;
        AmAutoSearch   = a.AutoSearch;
        AmAutoTrain    = ReadAutoTrainerOrDefault().AutoTrain;

        AllowHangupInAllOffMode         = dto.AllowHangupInAllOffMode;
        ReEnableAutoCombatOnReconnect   = dto.ReEnableAutoCombatOnReconnect;
        ReEnableAutoNukeOnReconnect     = dto.ReEnableAutoNukeOnReconnect;
        ReEnableAutoHealRestOnReconnect = dto.ReEnableAutoHealRestOnReconnect;
        ReEnableAutoBlessOnReconnect    = dto.ReEnableAutoBlessOnReconnect;
        ReEnableAutoLightOnReconnect    = dto.ReEnableAutoLightOnReconnect;
        ReEnableAutoGetItemsOnReconnect = dto.ReEnableAutoGetItemsOnReconnect;
        ReEnableAutoGetCashOnReconnect  = dto.ReEnableAutoGetCashOnReconnect;
        ReEnableAutoSneakOnReconnect    = dto.ReEnableAutoSneakOnReconnect;
        ReEnableAutoHideOnReconnect     = dto.ReEnableAutoHideOnReconnect;
        ReEnableAutoSearchOnReconnect   = dto.ReEnableAutoSearchOnReconnect;
        ReEnableAutoTrainOnReconnect    = dto.ReEnableAutoTrainOnReconnect;
    }

    // Rebuild the loop / Auto-Lair picker lists from the active game-data set,
    // then guarantee the passed-in saved selections are present so the ComboBox
    // can hold a reference that outlives the active set (a per-character default
    // may point at a loop from a set that isn't currently loaded).
    // The stored font, added to the list when the list doesn't offer it (a font not
    // found on this machine, a size typed into the file). Falling back to the default
    // instead put a different font on the terminal and saved it over the stored one.
    private FontFamilyOption SelectFamily(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return FontFamilyOptions[0];
        if (FontFamilyOptions.FirstOrDefault(o => o.Uri == stored) is { } listed) return listed;
        FontFamilyOption kept = new(stored, stored);
        FontFamilyOptions.Add(kept);
        AppServices.Current?.Log.Info("Settings", $"Font '{stored}' isn't installed here; kept as the saved choice");
        return kept;
    }

    private static FontSizeOption SelectSize(ObservableCollection<FontSizeOption> options, double stored)
    {
        if (options.FirstOrDefault(o => o.Value == stored) is { } listed) return listed;
        FontSizeOption kept = new($"{stored:0.#}", stored);
        int at = 0;
        while (at < options.Count && options[at].Value < stored) at++;
        options.Insert(at, kept);
        return kept;
    }

    private void PopulateTaskNameLists(string? loopSelection, string? autoLairSelection)
    {
        LoopNames.Clear();
        foreach (Loop l in AppServices.Current.Loops.Loops)
            LoopNames.Add(l.Name);
        if (!string.IsNullOrWhiteSpace(loopSelection) && !LoopNames.Contains(loopSelection))
            LoopNames.Add(loopSelection);

        AutoLairNames.Clear();
        foreach (LairSetup s in AppServices.Current.Lairs.Setups)
            AutoLairNames.Add(s.Name);
        if (!string.IsNullOrWhiteSpace(autoLairSelection) && !AutoLairNames.Contains(autoLairSelection))
            AutoLairNames.Add(autoLairSelection);
    }

    private GeneralSettings ReadOrDefault() => SettingsSection.Read<GeneralSettings>(_profile.Current, TabKey);

    // The Auto-Trainer tab owns the "AutoTrainer" entry; this tab only mirrors
    // its AutoTrain bit, so it reads that entry defensively (unset / malformed
    // → defaults) rather than assuming the tab has ever been saved.
    private AutoTrainerSettings ReadAutoTrainerOrDefault() => SettingsSection.Read<AutoTrainerSettings>(_profile.Current, "AutoTrainer");

    private AutoActionDefaults SnapshotAuto() => new()
    {
        AutoCombat   = AmAutoCombat,
        AutoNuke     = AmAutoNuke,
        AutoHeal     = AmAutoHeal,
        AutoRest     = AmAutoRest,
        AutoBless    = AmAutoBless,
        AutoLight    = AmAutoLight,
        AutoGetItems = AmAutoGetItems,
        AutoGetCash  = AmAutoGetCash,
        AutoSneak    = AmAutoSneak,
        AutoHide     = AmAutoHide,
        AutoSearch   = AmAutoSearch,
    };

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

    // CTM source-gen requires one On*Changed partial per observable field that
    // wants to mark dirty. Wired manually to keep Dirty() centralised.
    partial void OnIsTaskDoNothingChanged(bool value)     { if (value) UncheckOtherTasks(0); Dirty(); }
    partial void OnIsTaskBeginLoopChanged(bool value)     { if (value) UncheckOtherTasks(1); Dirty(); }
    partial void OnIsTaskBeginAutoLairChanged(bool value) { if (value) UncheckOtherTasks(2); Dirty(); }
    partial void OnDefaultLoopNameChanged(string? value)     => Dirty();
    partial void OnDefaultAutoLairNameChanged(string? value) => Dirty();
    partial void OnAutoConnectChanged(bool value)            => Dirty();
    partial void OnLoadLastRanLoopChanged(bool value)        => Dirty();
    partial void OnAutoSyncQuestFlagsOnLoginChanged(bool value) => Dirty();
    partial void OnBackupOnSaveChanged(bool value)           => Dirty();
    partial void OnScaleTerminalToWindowChanged(bool value)  => Dirty();
    partial void OnTypeToTerminalFromOtherWindowsChanged(bool value) => Dirty();
    partial void OnInventoryTabCompleteEnabledChanged(bool value) => Dirty();
    partial void OnShowStartupMudAnimationChanged(bool value)        => Dirty();
    partial void OnSnapWindowsChanged(bool value)                    => Dirty();
    partial void OnDrawWithGraphicsCardChanged(bool value)           => Dirty();
    partial void OnAutoCheckForUpdatesChanged(bool value)       => Dirty();
    partial void OnLocalApiEnabledChanged(bool value)                => Dirty();
    partial void OnLocalApiPortChanged(int value)                    => Dirty();
    partial void OnLocalApiAllowDestructiveChanged(bool value)       => Dirty();
    partial void OnRecentProfilesShownChanged(int value)             => Dirty();
    partial void OnMapOtherFloorsLevelsChanged(int value)            => Dirty();
    partial void OnMapOtherFloorsMaxOverlapPercentChanged(int value) => Dirty();
    partial void OnMapRouteLinesOnOtherFloorsChanged(bool value)     => Dirty();
    partial void OnSelectedBuffWatchdogLayoutChanged(BuffLayoutOption? value) => Dirty();
    // Live preview: push straight to the terminal canvas as the user picks,
    // before Save/Cancel commit the choice; Discard puts the saved font back.
    // Not while loading: opening Settings put the stored font on the terminal,
    // which showed as the terminal resizing when the window closed.
    partial void OnSelectedFontFamilyChanged(FontFamilyOption? value)
    {
        if (!_suppressDirty && value is not null) AppServices.Current.Display.FontFamily = value.Uri;
        Dirty();
    }
    partial void OnSelectedFontSizeChanged(FontSizeOption? value)
    {
        if (!_suppressDirty && value is not null) AppServices.Current.Display.FontSize = value.Value;
        Dirty();
    }
    partial void OnSelectedNavTooltipFontFamilyChanged(FontFamilyOption? value) => Dirty();
    partial void OnSelectedNavTooltipFontSizeChanged(FontSizeOption? value)     => Dirty();
    partial void OnAmAutoCombatChanged(bool value)           => Dirty();
    partial void OnAmAutoNukeChanged(bool value)             => Dirty();
    partial void OnAmAutoHealChanged(bool value)             => Dirty();
    partial void OnAmAutoRestChanged(bool value)             => Dirty();
    partial void OnAmAutoBlessChanged(bool value)            => Dirty();
    partial void OnAmAutoLightChanged(bool value)            => Dirty();
    partial void OnAmAutoGetItemsChanged(bool value)         => Dirty();
    partial void OnAmAutoGetCashChanged(bool value)          => Dirty();
    partial void OnAmAutoSneakChanged(bool value)            => Dirty();
    partial void OnAmAutoHideChanged(bool value)             => Dirty();
    partial void OnAmAutoSearchChanged(bool value)           => Dirty();
    partial void OnAmAutoTrainChanged(bool value)            => Dirty();
    partial void OnAllowHangupInAllOffModeChanged(bool value)         => Dirty();
    partial void OnReEnableAutoCombatOnReconnectChanged(bool value)   => Dirty();
    partial void OnReEnableAutoNukeOnReconnectChanged(bool value)     => Dirty();
    partial void OnReEnableAutoHealRestOnReconnectChanged(bool value) => Dirty();
    partial void OnReEnableAutoBlessOnReconnectChanged(bool value)    => Dirty();
    partial void OnReEnableAutoLightOnReconnectChanged(bool value)    => Dirty();
    partial void OnReEnableAutoGetItemsOnReconnectChanged(bool value) => Dirty();
    partial void OnReEnableAutoGetCashOnReconnectChanged(bool value)  => Dirty();
    partial void OnReEnableAutoSneakOnReconnectChanged(bool value)    => Dirty();
    partial void OnReEnableAutoHideOnReconnectChanged(bool value)     => Dirty();
    partial void OnReEnableAutoSearchOnReconnectChanged(bool value)   => Dirty();
    partial void OnReEnableAutoTrainOnReconnectChanged(bool value)    => Dirty();

    // Belt + braces on top of RadioButton.GroupName — the View's GroupName handles
    // the click-time mutual-exclusion, this guarantees programmatic state changes
    // (Discard / ReloadAfterProfileSwap) can't leave two task radios true at once.
    private void UncheckOtherTasks(int keep)
    {
        if (keep != 0 && IsTaskDoNothing)     IsTaskDoNothing = false;
        if (keep != 1 && IsTaskBeginLoop)     IsTaskBeginLoop = false;
        if (keep != 2 && IsTaskBeginAutoLair) IsTaskBeginAutoLair = false;
    }
}

// A labelled Buff Watchdog layout choice for the General-tab dropdown.
public sealed record BuffLayoutOption(string Label, BuffWatchdogLayout Value);

// Font-family picker row: the label shown in a font dropdown and the value
// persisted — a bundled face's avares:// URI or an installed font's family name
// (both are valid FontFamily inputs).
public sealed record FontFamilyOption(string Label, string Uri);

// Font-size picker row: the label shown in the dropdown (with a "{default}" tag
// on the picker's default entry) and the point size persisted.
public sealed record FontSizeOption(string Label, double Value)
{
    // The point sizes every font-size picker offers (terminal, map tooltip,
    // Conversation window), with defaultSize tagged "{default}".
    public static IReadOnlyList<FontSizeOption> Standard(double defaultSize)
    {
        double[] sizes = { 8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 22, 24, 28, 32 };
        List<FontSizeOption> list = new(sizes.Length);
        foreach (double s in sizes)
            list.Add(new FontSizeOption(
                s == defaultSize ? $"{s:0} {{default}}" : $"{s:0}", s));
        return list;
    }
}
