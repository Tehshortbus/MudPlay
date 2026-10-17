using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Views.Settings;

namespace MudPlay.ViewModels.Settings;

// "Health" tab — two-column layout (HP left, Mana / Kai right) with a Percentage
// / Value mode picker per column so the user can express a threshold either way.
// Persists as the "Health" entry in CharacterProfile.Settings.
//
// Wires DTO storage only. There's no ApplyToServices call — the engines that read
// these values, HealthManager (rest / hang / run flow) and CastingDirector
// (heal-cast thresholds), subscribe to ProfileService.ProfileLoaded and re-read
// the DTO from there.
public sealed partial class HealthSectionViewModel : SettingsSectionViewModel
{
    private readonly ProfileService _profile;
    private readonly CombatProfileStagingSession _session;
    private Control? _view;
    private bool _suppressDirty;

    public override string Id => "health";
    public override string Title => "Health";

    // The whole Health tab is now PER COMBAT PROFILE — its dirtiness + commit are
    // owned by the shared staging session the Combat tab also drives, so a Health
    // edit and a Combat edit save as one unit and a chip switch swaps both.
    public override bool IsDirty => _session.IsDirty;

    // Header shown on the "this whole tab is per combat profile" border — tracks the
    // active profile so a chip switch on the Combat tab re-labels it.
    public string ActiveProfileLabel =>
        $"Combat profile: {(string.IsNullOrWhiteSpace(_session.Active.Name) ? $"Profile {_session.ActiveIndex + 1}" : _session.Active.Name.Trim())}";

    // The active profile's accent colour — the Health-tab border recolours on a chip
    // switch to match the active chip on the Combat tab.
    public Avalonia.Media.IBrush ActiveProfileAccentBrush =>
        CombatProfilePalette.SolidBrush(_session.ActiveIndex + 1);
    public Avalonia.Media.IBrush ActiveProfileAccentSoftBrush =>
        CombatProfilePalette.SoftBrush(_session.ActiveIndex + 1);

    // True when a profile is loaded — editor is hidden otherwise.
    public bool HasProfile => _profile.Current is not null;

    public override Control View => _view ??= new HealthSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Health", "HP", "Mana", "Kai",
        "Rest max", "Rest if below", "Heal rest", "Heal combat", "Heal during rest",
        "Minor heal combat", "Major heal combat", "Emergency heal",
        "Heal if above", "Heal if above resting", "Heal if above combat", "Mana floor",
        "Run if below", "Hang up if below",
        "Sys goto wimpy", "Wimpy", "Wimpy goto", "Wimpy location",
        "Use meditate ability", "Meditate before resting",
        "Pre-rest", "Post-rest", "Pre-meditate", "Post-meditate",
        "Percentage", "Value", "Absolute",
    };

    // ----- HP column ------------------------------------------------

    [ObservableProperty] private bool _hpModePercentage = true;
    [ObservableProperty] private bool _hpModeAbsolute;

    [ObservableProperty] private int _restMaxHp        = 95;
    [ObservableProperty] private int _restIfBelowHp    = 60;
    [ObservableProperty] private int _healRestTrigger  = 80;
    [ObservableProperty] private int _minorHealCombatTrigger = 70;
    [ObservableProperty] private int _majorHealCombatTrigger = 40;
    [ObservableProperty] private int _emergencyHealTrigger = 20;
    [ObservableProperty] private int _runIfBelowHp     = 20;
    [ObservableProperty] private int _hangIfBelowHp    = 5;

    // ----- MA / Kai column ------------------------------------------

    [ObservableProperty] private bool _maModePercentage = true;
    [ObservableProperty] private bool _maModeAbsolute;

    [ObservableProperty] private int _restMaxMa        = 95;
    [ObservableProperty] private int _restIfBelowMa    = 30;
    [ObservableProperty] private int _healIfAboveMaResting = 50;
    [ObservableProperty] private int _healIfAboveMaCombat;
    [ObservableProperty] private int _runIfBelowMa     = 10;

    // ----- Resting Options ------------------------------------------

    [ObservableProperty] private bool _useMeditateAbility;
    [ObservableProperty] private bool _meditateBeforeResting;
    [ObservableProperty] private bool _utilizeShadowRest;

    // ----- Resting commands -----------------------------------------

    [ObservableProperty] private string _preRestCommand  = string.Empty;
    [ObservableProperty] private string _postRestCommand = string.Empty;

    // ----- Sys goto wimpy (escape instead of the emergency hangup) --

    // When on, the emergency low-HP hangup (Hang-up if below) instead breaks combat
    // and fires `sys goto <SysGotoWimpyLocation>`. Only usable when the active BBS
    // grants the Sysop goto power (SysGotoAvailable gates the checkbox); the engine
    // falls back to the normal hangup if the power is off or the location is gone.
    [ObservableProperty] private bool _sysGotoWimpyInsteadOfHanging;

    // The Sys Goto location keyword the wimpy escape jumps to — a Name from the
    // active BBS's Sys Goto table (WimpyGotoChoices).
    [ObservableProperty] private string? _sysGotoWimpyLocation;

    // The active BBS's Sys Goto location keywords, for the wimpy-escape picker.
    // Empty when the power is off. Rebuilt on load / profile mutate / set change.
    public ObservableCollection<string> WimpyGotoChoices { get; } = new();

    // Optional LIVE (unsaved) view of the BBS tab's sysop-goto state, supplied by
    // SettingsWindowViewModel (which owns both sections). When wired, the wimpy gate
    // + picker track the BBS tab's pending edits so ticking Sysop goto there enables
    // this immediately, without a Save round-trip; when null (Health tab standalone)
    // they fall back to the persisted per-BBS power.
    private Func<bool>? _liveSysGotoEnabled;
    private Func<IReadOnlyList<string>>? _liveSysGotoLocations;

    // Whether the Sysop goto power is available — gates the wimpy checkbox and
    // picker. Prefers the BBS tab's live pending flag when wired, else the persisted
    // power. False at design time / before services exist.
    public bool SysGotoAvailable =>
        _liveSysGotoEnabled?.Invoke() ?? (AppServices.CurrentOrNull?.SysopGoto.Enabled ?? false);

    // Bind the wimpy gate + picker to the BBS tab's live sysop-goto state. Call
    // NotifyLiveSysGotoChanged whenever that state changes so this tab re-reads it.
    public void BindLiveSysGoto(Func<bool> enabled, Func<IReadOnlyList<string>> locations)
    {
        _liveSysGotoEnabled = enabled;
        _liveSysGotoLocations = locations;
        RefreshWimpyGoto();
    }

    public void NotifyLiveSysGotoChanged() => RefreshWimpyGoto();

    // Convenience for SettingsWindowViewModel, which builds the shared session and
    // passes the same instance to the Combat, Health, Spells and Party section VMs.
    // "Include in combat profile" checkboxes, one per header on this tab.
    // The profile chips, so profiles can be switched from this tab too.
    public CombatProfileChipBar ChipBar { get; }
    public CombatProfileGroupToggle HealthHpInProfile { get; }
    public CombatProfileGroupToggle HealthManaInProfile { get; }
    public CombatProfileGroupToggle RestingOptionsInProfile { get; }
    public CombatProfileGroupToggle EmergencyEscapeInProfile { get; }
    public CombatProfileGroupToggle RestingCommandsInProfile { get; }

    public HealthSectionViewModel(CombatProfileStagingSession session) : this(
        session,
        AppServices.Current.Profile,
        TryGetPlayerState(),
        TryGetDeathFloor,
        TryGetGameData()) { }

    public HealthSectionViewModel(CombatProfileStagingSession session, ProfileService profile,
        Game.PlayerState? state = null, Func<int>? readDeathFloor = null, GameDataCache? gameData = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(profile);
        _session = session;
        _profile = profile;
        _state = state;
        _readDeathFloor = readDeathFloor;
        _gameData = gameData;
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        // A BBS-tab edit (toggling Sysop goto, or editing the location table) commits
        // via NotifyMutated → refresh the wimpy picker's availability + choices. Only
        // touches WimpyGotoChoices / SysGotoAvailable, never the loaded settings, so
        // it can't clobber unsaved Health-tab edits.
        _profile.ProfileMutated += OnProfileMutatedRefreshWimpy;
        if (_state is not null) _state.PropertyChanged += OnStateChanged;
        if (_gameData is not null) _gameData.ActiveSetChanged += OnActiveSetChanged;
        // The conversions resolve against the Default-gear maxima, which a `stat` can
        // re-record without the live max moving at all.
        Game.Health.DefaultPoolBaselineKeeper? baseline = AppServices.CurrentOrNull?.PoolBaseline;
        if (baseline is not null) baseline.Changed += OnPoolBaselineChanged;

        // The Health section of the shared working profiles is folded / loaded on the
        // same events the Combat tab drives.
        _session.CaptureRequested += CaptureHealthBoxesToActive;
        _session.LoadRequested += OnSessionLoad;
        _session.ReloadAllRequested += OnSessionLoad;
        _session.ChipsChanged += OnSessionChipsChanged;
        _session.Committed += OnSessionCommitted;
        ChipBar = new CombatProfileChipBar(_session);
        HealthHpInProfile = new CombatProfileGroupToggle(_session, Models.Profile.CombatProfileGroup.HealthHp, MarkDirty);
        HealthManaInProfile = new CombatProfileGroupToggle(_session, Models.Profile.CombatProfileGroup.HealthMana, MarkDirty);
        RestingOptionsInProfile = new CombatProfileGroupToggle(_session, Models.Profile.CombatProfileGroup.RestingOptions, MarkDirty);
        EmergencyEscapeInProfile = new CombatProfileGroupToggle(_session, Models.Profile.CombatProfileGroup.EmergencyEscape, MarkDirty);
        RestingCommandsInProfile = new CombatProfileGroupToggle(_session, Models.Profile.CombatProfileGroup.RestingCommands, MarkDirty);

        RefreshShadowRestAvailability();
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= OnProfileClosedExternally;
            _profile.ProfileMutated -= OnProfileMutatedRefreshWimpy;
            if (_state is not null) _state.PropertyChanged -= OnStateChanged;
            if (_gameData is not null) _gameData.ActiveSetChanged -= OnActiveSetChanged;
            if (baseline is not null) baseline.Changed -= OnPoolBaselineChanged;
            _session.CaptureRequested -= CaptureHealthBoxesToActive;
            _session.LoadRequested -= OnSessionLoad;
            _session.ReloadAllRequested -= OnSessionLoad;
            _session.ChipsChanged -= OnSessionChipsChanged;
            _session.Committed -= OnSessionCommitted;
            ChipBar.Dispose();
            HealthHpInProfile.Dispose();
            HealthManaInProfile.Dispose();
            RestingOptionsInProfile.Dispose();
            EmergencyEscapeInProfile.Dispose();
            RestingCommandsInProfile.Dispose();
        });
        _suppressDirty = true;
        LoadHealthBoxesFrom(_session.Active.Health);
        _suppressDirty = false;
    }

    // The active profile's Health folded from / loaded into the boxes — the shared
    // session's CaptureRequested / Load handlers. Mutates in place; the Combat tab
    // folds its own combat / weapon fields on the same profile.
    private void CaptureHealthBoxesToActive() => _session.Active.Health = BuildHealthDto();

    private void OnSessionLoad()
    {
        _suppressDirty = true;
        LoadHealthBoxesFrom(_session.Active.Health);
        _suppressDirty = false;
    }

    private void OnSessionCommitted() => OnPropertyChanged(nameof(IsDirty));

    private void OnSessionChipsChanged()
    {
        OnPropertyChanged(nameof(ActiveProfileLabel));
        OnPropertyChanged(nameof(ActiveProfileAccentBrush));
        OnPropertyChanged(nameof(ActiveProfileAccentSoftBrush));
    }

    // The Health section from the current boxes — shared by the session's Capture
    // fold (into the working profile) and the eventual Commit.
    private HealthSettings BuildHealthDto() => new()
    {
        HpThresholdMode        = HpModeAbsolute ? ThresholdMode.Absolute : ThresholdMode.Percentage,
        RestMaxHp              = Clamp(RestMaxHp),
        RestIfBelowHp          = Clamp(RestIfBelowHp),
        RunIfBelowHp           = Clamp(RunIfBelowHp),
        HangIfBelowHp          = ClampHang(HangIfBelowHp),
        HealRestTrigger        = Clamp(HealRestTrigger),
        MinorHealCombatTrigger = Clamp(MinorHealCombatTrigger),
        MajorHealCombatTrigger = Clamp(MajorHealCombatTrigger),
        EmergencyHealTrigger   = Clamp(EmergencyHealTrigger),

        MaThresholdMode        = MaModeAbsolute ? ThresholdMode.Absolute : ThresholdMode.Percentage,
        RestMaxMa              = Clamp(RestMaxMa),
        RestIfBelowMa          = Clamp(RestIfBelowMa),
        HealIfAboveMaResting   = Clamp(HealIfAboveMaResting),
        HealIfAboveMaCombat    = Clamp(HealIfAboveMaCombat),
        RunIfBelowMa           = Clamp(RunIfBelowMa),

        UseMeditateAbility     = UseMeditateAbility,
        MeditateBeforeResting  = MeditateBeforeResting,
        UtilizeShadowRest      = UtilizeShadowRest,

        SysGotoWimpyInsteadOfHanging = SysGotoWimpyInsteadOfHanging,
        SysGotoWimpyLocation         = SysGotoWimpyLocation ?? string.Empty,

        PreRestCommand         = PreRestCommand  ?? string.Empty,
        PostRestCommand        = PostRestCommand ?? string.Empty,
    };

    private static Game.PlayerState? TryGetPlayerState()
    {
        try { return AppServices.Current.PlayerState; }
        catch { return null; }    // design-time
    }

    private static GameDataCache? TryGetGameData()
    {
        try { return AppServices.Current.GameData; }
        catch { return null; }    // design-time
    }

    private readonly GameDataCache? _gameData;

    // ShadowRest is a Paradigm-only ability (game-data code 1103); on a stock realm
    // no class carries it, so the Utilize-shadowrest toggle could never do anything.
    // Hide it unless the active game-data set actually ships a ShadowRest class.
    // Recomputed when the active set flips (a modeless Settings window can outlive a
    // Game Data set switch).
    [ObservableProperty] private bool _shadowRestAvailable;

    private void OnActiveSetChanged(string? _)
    {
        RefreshShadowRestAvailability();
        RefreshWimpyGoto();   // a renumbered set can change which rows resolve
    }

    private void OnProfileMutatedRefreshWimpy(CharacterProfile _) => RefreshWimpyGoto();

    private void RefreshShadowRestAvailability()
    {
        ShadowRestAvailable =
            Game.GameData.AbilityNames.AnyClassHasShadowRest(_gameData?.GetTable("Classes"));
    }

    // Rebuild the wimpy-escape picker from the active BBS's Sys Goto locations and
    // re-raise the checkbox's enable-gate. A stored selection that's no longer in the
    // table is kept as a choice so the user still sees what's configured.
    private void RefreshWimpyGoto()
    {
        // Build the desired keyword set — the BBS tab's LIVE locations when wired (so
        // the picker fills the moment a row is added there), else the persisted set —
        // always including any stored selection so it stays visible / selectable even
        // if its row was removed.
        List<string> desired = new();
        if (_liveSysGotoLocations is { } live)
        {
            foreach (string name in live())
                if (!string.IsNullOrWhiteSpace(name) && !desired.Contains(name)) desired.Add(name);
        }
        else if (AppServices.CurrentOrNull is { } svc)
        {
            foreach (Models.Profile.SysopGotoLocation loc in svc.SysopGoto.UsableNow)
                if (!desired.Contains(loc.Name)) desired.Add(loc.Name);
        }
        if (!string.IsNullOrEmpty(SysGotoWimpyLocation) && !desired.Contains(SysGotoWimpyLocation!))
            desired.Add(SysGotoWimpyLocation!);

        // Reconcile the bound collection IN PLACE — never Clear(). A Clear blanks the
        // ComboBox's SelectedItem, and the two-way binding then writes null back to
        // SysGotoWimpyLocation; when a refresh fires mid-Apply (the BBS section's Apply
        // raises ProfileMutated before this section's own Apply runs) that null gets
        // persisted, silently dropping the just-picked location. Remove departed rows,
        // append new ones, leave the rest (and the selection) untouched.
        for (int i = WimpyGotoChoices.Count - 1; i >= 0; i--)
            if (!desired.Contains(WimpyGotoChoices[i])) WimpyGotoChoices.RemoveAt(i);
        foreach (string name in desired)
            if (!WimpyGotoChoices.Contains(name)) WimpyGotoChoices.Add(name);

        OnPropertyChanged(nameof(SysGotoAvailable));
    }

    // Active realm's death floor (RealmProfile.PlayerDiesAtHp), read through the same
    // ResolveActiveBbs path the engine's readDeathFloor uses so the Hang-up
    // ticker's lower bound matches where the game actually kills the character.
    private static int TryGetDeathFloor()
    {
        try { return AppServices.Current.ResolveActiveRealm()?.Realm.PlayerDiesAtHp ?? -25; }
        catch { return -25; }    // design-time
    }

    private readonly Func<int>? _readDeathFloor;
    private readonly Game.PlayerState? _state;

    // Lower bound for the Hang-up ticker — the bottom of the continuous HP scale,
    // i.e. the per-BBS death floor. Value mode expresses it as raw HP; Percentage
    // mode as a % of live max (rounded toward zero so the floor % still maps to a
    // firing HP, not one tick past the floor). Without a live max (disconnected)
    // Percentage mode falls back to a permissive -100 %; the engine's fire window
    // never fires past the true floor regardless of what the ticker allows.
    public int HangMinimum
    {
        get
        {
            int floor = Math.Min(0, _readDeathFloor?.Invoke() ?? -25);
            if (HpModeAbsolute) return floor;
            if (LiveMaxHp <= 0) return -100;
            return (int)Math.Ceiling(floor * 100.0 / LiveMaxHp);
        }
    }

    // Live MaxHp from Game.PlayerState. 0 when no connection / no prompt observed
    // yet. Drives the Hang-up ticker's death-floor scale (a live-pool concern).
    public int LiveMaxHp => _state?.MaxHp ?? 0;

    // Live MaxMa from Game.PlayerState.
    public int LiveMaxMa => _state?.MaxMa ?? 0;

    // The max the rest / heal / run / hang CONVERSION strings resolve against — the
    // DEFAULT gear set's pool (what the thresholds are tuned for), so the displayed
    // "= N/M" stays put while a Pre-rest set that alters the pool is worn, letting you
    // adjust your values mid-rest. Falls back to the live pool max before a stat
    // screen / when none of the Default set's items is owned. The FromDefaultSet half
    // drives the "(def)" / "(live)" marker on each conversion, so a figure that isn't
    // the character's live max reads as the Default-set basis rather than a wrong number.
    private (int Max, bool FromDefaultSet) PreviewHp =>
        AppServices.CurrentOrNull?.RestPreviewMaxHp() ?? (_state?.MaxHp ?? 0, false);
    private (int Max, bool FromDefaultSet) PreviewMa =>
        AppServices.CurrentOrNull?.RestPreviewMaxMa() ?? (_state?.MaxMa ?? 0, false);

    // NumericUpDown Maximum for the threshold fields: 100 in Percentage mode (a % can't
    // exceed 100), the absolute ceiling in Value mode.
    public decimal HpThresholdMax => HpModePercentage ? 100 : 100_000;
    public decimal MaThresholdMax => MaModePercentage ? 100 : 100_000;

    private void OnStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Game.PlayerState.MaxHp))
        {
            OnPropertyChanged(nameof(LiveMaxHp));
            OnPropertyChanged(nameof(RestMaxHpConverted));
            OnPropertyChanged(nameof(RestIfBelowHpConverted));
            OnPropertyChanged(nameof(HealRestTriggerConverted));
            OnPropertyChanged(nameof(MinorHealCombatTriggerConverted));
            OnPropertyChanged(nameof(MajorHealCombatTriggerConverted));
            OnPropertyChanged(nameof(EmergencyHealTriggerConverted));
            OnPropertyChanged(nameof(RunIfBelowHpConverted));
            OnPropertyChanged(nameof(HangIfBelowHpConverted));
            OnPropertyChanged(nameof(HangMinimum));   // % floor tracks live max
        }
        else if (e.PropertyName == nameof(Game.PlayerState.MaxMa))
        {
            OnPropertyChanged(nameof(LiveMaxMa));
            OnPropertyChanged(nameof(RestMaxMaConverted));
            OnPropertyChanged(nameof(RestIfBelowMaConverted));
            OnPropertyChanged(nameof(HealIfAboveMaRestingConverted));
            OnPropertyChanged(nameof(HealIfAboveMaCombatConverted));
            OnPropertyChanged(nameof(RunIfBelowMaConverted));
        }
    }

    // Render the conversion of a threshold field against the preview basis.
    // Percentage mode shows the absolute equivalent ("120/200"); Value mode shows the
    // percentage ("60%"). Tagged "(def)" when the basis is the Default gear set's pool,
    // "(live)" when it's the live max. Empty string when no connection / no prompt data
    // yet so the layout doesn't render a misleading "0/0".
    private static string FormatConversion(int value, (int Max, bool FromDefaultSet) basis, bool isPercentageMode)
    {
        (int max, bool fromDefaultSet) = basis;
        if (max <= 0) return string.Empty;
        string tag = fromDefaultSet ? " (def)" : " (live)";
        if (isPercentageMode)
        {
            int abs = (int)Math.Round(max * value / 100.0);
            return $"{abs}/{max}{tag}";
        }
        int pct = (int)Math.Round(value * 100.0 / max);
        return $"{pct}%{tag}";
    }

    // ----- HP conversion strings (resolve against the Default-set basis) -----
    public string RestMaxHpConverted              => FormatConversion(RestMaxHp,              PreviewHp, HpModePercentage);
    public string RestIfBelowHpConverted          => FormatConversion(RestIfBelowHp,          PreviewHp, HpModePercentage);
    public string HealRestTriggerConverted        => FormatConversion(HealRestTrigger,        PreviewHp, HpModePercentage);
    public string MinorHealCombatTriggerConverted => FormatConversion(MinorHealCombatTrigger, PreviewHp, HpModePercentage);
    public string MajorHealCombatTriggerConverted => FormatConversion(MajorHealCombatTrigger, PreviewHp, HpModePercentage);
    public string EmergencyHealTriggerConverted   => FormatConversion(EmergencyHealTrigger,   PreviewHp, HpModePercentage);
    public string RunIfBelowHpConverted           => FormatConversion(RunIfBelowHp,           PreviewHp, HpModePercentage);
    public string HangIfBelowHpConverted          => FormatConversion(HangIfBelowHp,          PreviewHp, HpModePercentage);

    // ----- MA conversion strings (resolve against the Default-set basis) -----
    public string RestMaxMaConverted              => FormatConversion(RestMaxMa,              PreviewMa, MaModePercentage);
    public string RestIfBelowMaConverted          => FormatConversion(RestIfBelowMa,          PreviewMa, MaModePercentage);
    public string HealIfAboveMaRestingConverted   => FormatConversion(HealIfAboveMaResting,   PreviewMa, MaModePercentage);
    public string HealIfAboveMaCombatConverted    => FormatConversion(HealIfAboveMaCombat,    PreviewMa, MaModePercentage);
    public string RunIfBelowMaConverted           => FormatConversion(RunIfBelowMa,           PreviewMa, MaModePercentage);

    // Commit through the shared session (folds every tab + persists Settings
    // ["Combat"] + ["Spells"] + ["Health"] + the profile blob + weapons as one unit).
    // Guarded on the session's dirty flag, so whichever tab's Apply runs first
    // commits and the others no-op.
    public override void Apply() => _session.CommitIfDirty();

    public override void Discard() => _session.DiscardAndReset();

    // Clamp threshold inputs to the realistic range. Percentage values run 0..100;
    // absolute values run 0..100,000 (covers every realistic HP/MA pool). One floor
    // for both modes keeps the engine code from having to remember which mode
    // produced the number.
    private static int Clamp(int value) => Math.Clamp(value, 0, 100_000);

    // Hang-up is the one HP threshold that may go negative — in either mode it
    // opens down to the per-BBS death floor (HangMinimum) so the user can set it
    // inside the bleeding-out band, closer to death.
    private int ClampHang(int value) =>
        Math.Clamp(value, HangMinimum, 100_000);

    // The shared session subscribes to ProfileLoaded first (it is constructed before
    // this VM) and drives the box reload via ReloadAllRequested; this handler only
    // refreshes the non-staging HasProfile gate + the wimpy picker.
    private void OnProfileChanged(CharacterProfile _)
    {
        RefreshWimpyGoto();
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(IsDirty));
    }
    private void OnProfileClosedExternally()
    {
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(IsDirty));
    }

    private void LoadHealthBoxesFrom(HealthSettings dto)
    {
        HpModePercentage = dto.HpThresholdMode == ThresholdMode.Percentage;
        HpModeAbsolute   = dto.HpThresholdMode == ThresholdMode.Absolute;
        RestMaxHp              = dto.RestMaxHp;
        RestIfBelowHp          = dto.RestIfBelowHp;
        HealRestTrigger        = dto.HealRestTrigger;
        MinorHealCombatTrigger = dto.MinorHealCombatTrigger;
        MajorHealCombatTrigger = dto.MajorHealCombatTrigger;
        EmergencyHealTrigger   = dto.EmergencyHealTrigger;
        RunIfBelowHp           = dto.RunIfBelowHp;
        HangIfBelowHp          = dto.HangIfBelowHp;

        MaModePercentage = dto.MaThresholdMode == ThresholdMode.Percentage;
        MaModeAbsolute   = dto.MaThresholdMode == ThresholdMode.Absolute;
        RestMaxMa        = dto.RestMaxMa;
        RestIfBelowMa    = dto.RestIfBelowMa;
        HealIfAboveMaResting = dto.HealIfAboveMaResting;
        HealIfAboveMaCombat  = dto.HealIfAboveMaCombat;
        RunIfBelowMa     = dto.RunIfBelowMa;

        UseMeditateAbility    = dto.UseMeditateAbility;
        MeditateBeforeResting = dto.MeditateBeforeResting;
        UtilizeShadowRest     = dto.UtilizeShadowRest;

        SysGotoWimpyInsteadOfHanging = dto.SysGotoWimpyInsteadOfHanging;
        SysGotoWimpyLocation = string.IsNullOrEmpty(dto.SysGotoWimpyLocation)
            ? null : dto.SysGotoWimpyLocation;

        PreRestCommand  = dto.PreRestCommand  ?? string.Empty;
        PostRestCommand = dto.PostRestCommand ?? string.Empty;

        RefreshWimpyGoto();   // populate the picker + include any stored selection
    }

    // ----- IsDirty plumbing -----------------------------------------

    // Route every box edit into the shared session's single dirty flag (a combat
    // profile spans this tab + the Combat tab). The session clears it on Commit /
    // Discard; this VM just re-raises IsDirty for the Save-button gate.
    private void MarkDirty()
    {
        if (_suppressDirty) return;
        _session.MarkDirty();
        OnPropertyChanged(nameof(IsDirty));
    }

    // HP column
    partial void OnHpModePercentageChanged(bool value)
    {
        if (value) HpModeAbsolute = false;
        OnPropertyChanged(nameof(HpThresholdMax));
        if (value) ClampHpToPercent();      // a % can't exceed 100
        RefreshAllHpConverted();
        RefreshHangBounds();
        MarkDirty();
    }
    partial void OnHpModeAbsoluteChanged(bool value)
    {
        if (value) HpModePercentage = false;
        OnPropertyChanged(nameof(HpThresholdMax));
        RefreshAllHpConverted();
        RefreshHangBounds();
        MarkDirty();
    }

    // The Hang-up ticker's floor tracks the HP radial (raw death floor in Value
    // mode, that floor as a % of max in Percentage mode). Re-raise it on a mode
    // flip and snap the value back into the new range — the two modes' floors
    // differ, so a value valid in one can sit past the other's bound.
    private void RefreshHangBounds()
    {
        OnPropertyChanged(nameof(HangMinimum));
        int clamped = ClampHang(HangIfBelowHp);
        if (clamped != HangIfBelowHp) HangIfBelowHp = clamped;
    }
    partial void OnRestMaxHpChanged(int value)                { OnPropertyChanged(nameof(RestMaxHpConverted));              MarkDirty(); }
    partial void OnRestIfBelowHpChanged(int value)            { OnPropertyChanged(nameof(RestIfBelowHpConverted));          MarkDirty(); }
    partial void OnHealRestTriggerChanged(int value)          { OnPropertyChanged(nameof(HealRestTriggerConverted));        MarkDirty(); }
    partial void OnMinorHealCombatTriggerChanged(int value)   { OnPropertyChanged(nameof(MinorHealCombatTriggerConverted)); MarkDirty(); }
    partial void OnMajorHealCombatTriggerChanged(int value)   { OnPropertyChanged(nameof(MajorHealCombatTriggerConverted)); MarkDirty(); }
    partial void OnEmergencyHealTriggerChanged(int value)     { OnPropertyChanged(nameof(EmergencyHealTriggerConverted));   MarkDirty(); }
    partial void OnRunIfBelowHpChanged(int value)             { OnPropertyChanged(nameof(RunIfBelowHpConverted));           MarkDirty(); }
    partial void OnHangIfBelowHpChanged(int value)            { OnPropertyChanged(nameof(HangIfBelowHpConverted));          MarkDirty(); }

    // MA column
    partial void OnMaModePercentageChanged(bool value)
    {
        if (value) MaModeAbsolute = false;
        OnPropertyChanged(nameof(MaThresholdMax));
        if (value) ClampMaToPercent();      // a % can't exceed 100
        RefreshAllMaConverted();
        MarkDirty();
    }
    partial void OnMaModeAbsoluteChanged(bool value)
    {
        if (value) MaModePercentage = false;
        OnPropertyChanged(nameof(MaThresholdMax));
        RefreshAllMaConverted();
        MarkDirty();
    }

    // In Percentage mode a threshold can't exceed 100% — snap any Value-mode leftover
    // above 100 back down when the user flips to Percentage.
    private void ClampHpToPercent()
    {
        if (RestMaxHp > 100) RestMaxHp = 100;
        if (RestIfBelowHp > 100) RestIfBelowHp = 100;
        if (HealRestTrigger > 100) HealRestTrigger = 100;
        if (MinorHealCombatTrigger > 100) MinorHealCombatTrigger = 100;
        if (MajorHealCombatTrigger > 100) MajorHealCombatTrigger = 100;
        if (EmergencyHealTrigger > 100) EmergencyHealTrigger = 100;
        if (RunIfBelowHp > 100) RunIfBelowHp = 100;
        if (HangIfBelowHp > 100) HangIfBelowHp = 100;
    }
    private void ClampMaToPercent()
    {
        if (RestMaxMa > 100) RestMaxMa = 100;
        if (RestIfBelowMa > 100) RestIfBelowMa = 100;
        if (HealIfAboveMaResting > 100) HealIfAboveMaResting = 100;
        if (HealIfAboveMaCombat > 100) HealIfAboveMaCombat = 100;
        if (RunIfBelowMa > 100) RunIfBelowMa = 100;
    }
    partial void OnRestMaxMaChanged(int value)                { OnPropertyChanged(nameof(RestMaxMaConverted));            MarkDirty(); }
    partial void OnRestIfBelowMaChanged(int value)            { OnPropertyChanged(nameof(RestIfBelowMaConverted));        MarkDirty(); }
    partial void OnHealIfAboveMaRestingChanged(int value)     { OnPropertyChanged(nameof(HealIfAboveMaRestingConverted)); MarkDirty(); }
    partial void OnHealIfAboveMaCombatChanged(int value)      { OnPropertyChanged(nameof(HealIfAboveMaCombatConverted));  MarkDirty(); }
    partial void OnRunIfBelowMaChanged(int value)             { OnPropertyChanged(nameof(RunIfBelowMaConverted));         MarkDirty(); }

    private void OnPoolBaselineChanged()
    {
        RefreshAllHpConverted();
        RefreshAllMaConverted();
    }

    private void RefreshAllHpConverted()
    {
        OnPropertyChanged(nameof(RestMaxHpConverted));
        OnPropertyChanged(nameof(RestIfBelowHpConverted));
        OnPropertyChanged(nameof(HealRestTriggerConverted));
        OnPropertyChanged(nameof(MinorHealCombatTriggerConverted));
        OnPropertyChanged(nameof(MajorHealCombatTriggerConverted));
        OnPropertyChanged(nameof(EmergencyHealTriggerConverted));
        OnPropertyChanged(nameof(RunIfBelowHpConverted));
        OnPropertyChanged(nameof(HangIfBelowHpConverted));
    }

    private void RefreshAllMaConverted()
    {
        OnPropertyChanged(nameof(RestMaxMaConverted));
        OnPropertyChanged(nameof(RestIfBelowMaConverted));
        OnPropertyChanged(nameof(HealIfAboveMaRestingConverted));
        OnPropertyChanged(nameof(HealIfAboveMaCombatConverted));
        OnPropertyChanged(nameof(RunIfBelowMaConverted));
    }

    // Resting Options
    partial void OnUseMeditateAbilityChanged(bool value)      => MarkDirty();
    partial void OnMeditateBeforeRestingChanged(bool value)   => MarkDirty();
    partial void OnUtilizeShadowRestChanged(bool value)       => MarkDirty();

    // Resting commands
    partial void OnPreRestCommandChanged(string value)        => MarkDirty();
    partial void OnPostRestCommandChanged(string value)       => MarkDirty();

    // Sys goto wimpy
    partial void OnSysGotoWimpyInsteadOfHangingChanged(bool value) => MarkDirty();
    partial void OnSysGotoWimpyLocationChanged(string? value)      => MarkDirty();
}
