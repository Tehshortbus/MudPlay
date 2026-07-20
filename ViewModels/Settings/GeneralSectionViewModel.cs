using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

// "General" tab. Per-character: default startup task, auto-connect, and the
// boot-up state of every Action-menu auto-toggle in both Manual-Mode and
// Auto-Mode columns.
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
    private Control? _view;

    public override string Id => "general";
    public override string Title => "General";
    public override bool IsDirty => _dirty;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "General", "Data files", "Open Data folder", "Change data directory",
        "Auto-connect", "Default task", "Do nothing",
        "Begin loop", "Begin Auto-Lair", "Backup profile",
        "Terminal font", "Font", "Font family", "Font size",
        "Scale terminal to window",
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

    public override Control View => _view ??= new GeneralSectionView { DataContext = this };

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
    [ObservableProperty] private bool _backupOnSave;
    [ObservableProperty] private bool _scaleTerminalToWindow;

    // PlayerCleanupDays moved to Settings → Other per user direction.
    // GlobalSettings.PlayerCleanupDays remains the canonical store —
    // OtherSectionViewModel now owns the edit surface.

    // Names of saved loop files available for the "Begin looping" picker. The
    // dropdown stays disabled while empty.
    public IReadOnlyList<string> LoopNames { get; } = Array.Empty<string>();

    // Names of saved Auto-Lair files available for the "Begin Auto-Lair" picker.
    public IReadOnlyList<string> AutoLairNames { get; } = Array.Empty<string>();

    // ----- Terminal font (char-tier) -----
    // Font family + size the terminal canvas renders with. Both used to live in
    // the per-BBS Display settings; they moved here so the choice follows the
    // character rather than whichever board it's connected to. The picker leads
    // with the two bundled faces — MX437 (the CP437 bitmap font that matches
    // classic BBS output) and JetBrains Mono — then lists every monospace font
    // installed on the system. Proportional faces are filtered out by
    // MonospaceFontCatalog since they'd mangle the fixed cell grid. The default
    // font and size 16 carry a "{default}" tag in the picker labels; a bundled
    // face persists as its avares:// URI while a system font persists as its
    // bare family name (both are valid FontFamily inputs).
    public IReadOnlyList<FontFamilyOption> FontFamilyOptions { get; } = BuildFontFamilyOptions();

    public IReadOnlyList<FontSizeOption> FontSizeOptions { get; } = BuildFontSizeOptions();

    [ObservableProperty] private FontFamilyOption? _selectedFontFamily;
    [ObservableProperty] private FontSizeOption? _selectedFontSize;

    private static IReadOnlyList<FontFamilyOption> BuildFontFamilyOptions()
    {
        // Bundled faces first, in a fixed order, so the default stays at the top.
        List<FontFamilyOption> list = new()
        {
            new FontFamilyOption("MX437 IBM VGA {default}", DisplayConfig.DefaultFontFamily),
            new FontFamilyOption("JetBrains Mono",
                "avares://FujinTerm/Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono"),
        };

        // Then every installed monospace font, skipping any that duplicates a
        // bundled face's family name so the picker never shows two identical
        // labels (e.g. a system-wide JetBrains Mono install).
        foreach (string name in MonospaceFontCatalog.Families)
        {
            if (name.Equals("JetBrains Mono", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Mx437 IBM VGA 8x16", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new FontFamilyOption(name, name));
        }

        return list;
    }

    private static IReadOnlyList<FontSizeOption> BuildFontSizeOptions()
    {
        double[] sizes = { 8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 22, 24, 28, 32 };
        List<FontSizeOption> list = new(sizes.Length);
        foreach (double s in sizes)
            list.Add(new FontSizeOption(
                s == DisplayConfig.DefaultFontSize ? $"{s:0} {{default}}" : $"{s:0}", s));
        return list;
    }

    // ----- Auto-engine master switches -----
    // Each AmXxx bool is the master on/off for the matching engine. Persisted to
    // GeneralSettings.AutoMode and read live by each engine's gating delegate in
    // AppServices. The XAML wires each CheckBox's IsEnabled to the matching
    // IsXxxWired flag so the user can see which engines are actually live vs
    // surface-only.
    [ObservableProperty] private bool _amAutoCombat;
    [ObservableProperty] private bool _amAutoNuke;
    [ObservableProperty] private bool _amAutoHealRest;
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
    public bool IsAutoHealRestWired => true;    // HealthManager
    public bool IsAutoNukeWired     => true;    // CombatSpellChooser multi-attack + debuff gate
    public bool IsAutoBlessWired    => true;    // CastingDirector Buffing-category gate
    public bool IsAutoLightWired    => true;    // AutoLightManager
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

        LoadFromProfile();
        _suppressDirty = false;
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        GeneralSettings dto = new()
        {
            DefaultTask = IsTaskBeginLoop      ? InitialTask.BeginLoop
                        : IsTaskBeginAutoLair  ? InitialTask.BeginAutoLair
                        : InitialTask.DoNothing,
            DefaultLoopName     = string.IsNullOrWhiteSpace(DefaultLoopName)     ? null : DefaultLoopName,
            DefaultAutoLairName = string.IsNullOrWhiteSpace(DefaultAutoLairName) ? null : DefaultAutoLairName,
            AutoConnect = AutoConnect,
            BackupOnSave = BackupOnSave,
            ScaleTerminalToWindow = ScaleTerminalToWindow,
            // Store null when the default is selected so the delta stays clean
            // and follows the app default if it ever changes (the picker label
            // literally reads "{default}").
            TerminalFontFamily = SelectedFontFamily is { } ff
                && ff.Uri != DisplayConfig.DefaultFontFamily ? ff.Uri : null,
            TerminalFontSize = SelectedFontSize is { } fs
                && fs.Value != DisplayConfig.DefaultFontSize ? fs.Value : null,
            AutoMode   = SnapshotAuto(),
            AllowHangupInAllOffMode         = AllowHangupInAllOffMode,
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
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);

        // Auto-train's boot flag isn't part of AutoMode — it lives in the
        // "AutoTrainer" entry the Auto-Trainer tab owns. Read-modify-write only
        // the AutoTrain bit so the tab's other fields (stats cascade, levels-to-
        // keep, announce, disabled trainers) survive this Save. Clearing the
        // stats cascade when train goes off mirrors the tab's own invariant.
        AutoTrainerSettings trainer = ReadAutoTrainerOrDefault();
        if (trainer.AutoTrain != AmAutoTrain)
        {
            trainer.AutoTrain = AmAutoTrain;
            if (!AmAutoTrain) trainer.AutoTrainStats = false;
            profile.Settings["AutoTrainer"] = JsonSerializer.SerializeToElement(trainer);
        }

        _profile.Save(backup: BackupOnSave);

        // Push the char-tier display settings into the live DisplayConfig — a
        // plain profile Save fires neither ProfileLoaded nor ProfileMutated, so
        // the AppServices seed path won't run; this is what makes the change
        // reach the live canvas on Apply.
        AppServices.Current.Display.ScaleToWindow = ScaleTerminalToWindow;
        AppServices.Current.Display.FontFamily =
            SelectedFontFamily?.Uri ?? DisplayConfig.DefaultFontFamily;
        AppServices.Current.Display.FontSize =
            SelectedFontSize?.Value ?? DisplayConfig.DefaultFontSize;

        ClearDirty();
    }

    public override void Discard()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
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

        IsTaskDoNothing      = dto.DefaultTask == InitialTask.DoNothing;
        IsTaskBeginLoop      = dto.DefaultTask == InitialTask.BeginLoop;
        IsTaskBeginAutoLair  = dto.DefaultTask == InitialTask.BeginAutoLair;
        DefaultLoopName      = dto.DefaultLoopName;
        DefaultAutoLairName  = dto.DefaultAutoLairName;
        AutoConnect          = dto.AutoConnect;
        BackupOnSave         = dto.BackupOnSave;
        ScaleTerminalToWindow = dto.ScaleTerminalToWindow;

        SelectedFontFamily = FontFamilyOptions.FirstOrDefault(o => o.Uri == dto.TerminalFontFamily)
                             ?? FontFamilyOptions[0];
        double size = dto.TerminalFontSize ?? DisplayConfig.DefaultFontSize;
        SelectedFontSize = FontSizeOptions.FirstOrDefault(o => o.Value == size)
                           ?? FontSizeOptions.First(o => o.Value == DisplayConfig.DefaultFontSize);

        AutoActionDefaults a = dto.AutoMode;
        AmAutoCombat   = a.AutoCombat;
        AmAutoNuke     = a.AutoNuke;
        AmAutoHealRest = a.AutoHealRest;
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

    private GeneralSettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new GeneralSettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json)) return new GeneralSettings();

        return JsonSerializer.Deserialize<GeneralSettings>(json.GetRawText())
               ?? new GeneralSettings();
    }

    // The Auto-Trainer tab owns the "AutoTrainer" entry; this tab only mirrors
    // its AutoTrain bit, so it reads that entry defensively (unset / malformed
    // → defaults) rather than assuming the tab has ever been saved.
    private AutoTrainerSettings ReadAutoTrainerOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new AutoTrainerSettings();
        if (!profile.Settings.TryGetValue("AutoTrainer", out JsonElement json)) return new AutoTrainerSettings();
        try
        {
            return JsonSerializer.Deserialize<AutoTrainerSettings>(json.GetRawText())
                   ?? new AutoTrainerSettings();
        }
        catch
        {
            // Malformed AutoTrainer JSON → treat as unset; the Auto-Trainer tab
            // rewrites it cleanly on its next Save.
            return new AutoTrainerSettings();
        }
    }

    private AutoActionDefaults SnapshotAuto() => new()
    {
        AutoCombat   = AmAutoCombat,
        AutoNuke     = AmAutoNuke,
        AutoHealRest = AmAutoHealRest,
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
    partial void OnBackupOnSaveChanged(bool value)           => Dirty();
    partial void OnScaleTerminalToWindowChanged(bool value)  => Dirty();
    partial void OnSelectedFontFamilyChanged(FontFamilyOption? value) => Dirty();
    partial void OnSelectedFontSizeChanged(FontSizeOption? value)     => Dirty();
    partial void OnAmAutoCombatChanged(bool value)           => Dirty();
    partial void OnAmAutoNukeChanged(bool value)             => Dirty();
    partial void OnAmAutoHealRestChanged(bool value)         => Dirty();
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

// Font-family picker row: the label shown in the General-tab dropdown and the
// avares:// URI persisted into GeneralSettings.TerminalFontFamily.
public sealed record FontFamilyOption(string Label, string Uri);

// Font-size picker row: the label shown in the dropdown (with a "{default}" tag
// on 16) and the point size persisted into GeneralSettings.TerminalFontSize.
public sealed record FontSizeOption(string Label, double Value);
