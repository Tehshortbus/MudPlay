using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

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
    private const string TabKey = "Health";

    private readonly ProfileService _profile;
    private Control? _view;
    private bool _suppressDirty;
    private bool _dirty;

    public override string Id => "health";
    public override string Title => "Health";
    public override bool IsDirty => _dirty;

    // True when a profile is loaded — editor is hidden otherwise.
    public bool HasProfile => _profile.Current is not null;

    public override Control View => _view ??= new HealthSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Health", "HP", "Mana", "Kai",
        "Rest max", "Rest if below", "Heal rest", "Heal combat", "Heal during rest",
        "Minor heal combat", "Major heal combat",
        "Heal if above", "Heal if above resting", "Heal if above combat", "Mana floor",
        "Run if below", "Hang up if below", "Bless if above",
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
    [ObservableProperty] private int _blessIfAboveMa   = 70;

    // ----- Meditation -----------------------------------------------

    [ObservableProperty] private bool _useMeditateAbility;
    [ObservableProperty] private bool _meditateBeforeResting;

    // ----- Resting commands -----------------------------------------

    [ObservableProperty] private string _preRestCommand  = string.Empty;
    [ObservableProperty] private string _postRestCommand = string.Empty;

    public HealthSectionViewModel() : this(
        AppServices.Current.Profile,
        TryGetPlayerState(),
        TryGetDeathFloor) { }

    public HealthSectionViewModel(ProfileService profile, Game.PlayerState? state = null,
        Func<int>? readDeathFloor = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _state = state;
        _readDeathFloor = readDeathFloor;
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        if (_state is not null) _state.PropertyChanged += OnStateChanged;
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= OnProfileClosedExternally;
            if (_state is not null) _state.PropertyChanged -= OnStateChanged;
        });
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
    }

    private static Game.PlayerState? TryGetPlayerState()
    {
        try { return AppServices.Current.PlayerState; }
        catch { return null; }    // design-time
    }

    // Active-BBS death floor (BbsProfile.PlayerDiesAtHp), read through the same
    // ResolveActiveBbs path the engine's readDeathFloor uses so the Hang-up
    // ticker's lower bound matches where the game actually kills the character.
    private static int TryGetDeathFloor()
    {
        try { return AppServices.Current.ResolveActiveBbs()?.PlayerDiesAtHp ?? -25; }
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
    // yet — conversion strings then render empty.
    public int LiveMaxHp => _state?.MaxHp ?? 0;

    // Live MaxMa from Game.PlayerState.
    public int LiveMaxMa => _state?.MaxMa ?? 0;

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
            OnPropertyChanged(nameof(BlessIfAboveMaConverted));
        }
    }

    // Render the live conversion of a threshold field against the player's live
    // max. Percentage mode shows the absolute equivalent ("= 120/200"); Value mode
    // shows the percentage ("= 60%"). Empty string when no connection / no prompt
    // data yet so the layout doesn't render a misleading "= 0/0".
    private static string FormatConversion(int value, int max, bool isPercentageMode)
    {
        if (max <= 0) return string.Empty;
        if (isPercentageMode)
        {
            int abs = (int)Math.Round(max * value / 100.0);
            return $"{abs}/{max}";
        }
        int pct = (int)Math.Round(value * 100.0 / max);
        return $"{pct}%";
    }

    // ----- HP conversion strings -----
    public string RestMaxHpConverted              => FormatConversion(RestMaxHp,              LiveMaxHp, HpModePercentage);
    public string RestIfBelowHpConverted          => FormatConversion(RestIfBelowHp,          LiveMaxHp, HpModePercentage);
    public string HealRestTriggerConverted        => FormatConversion(HealRestTrigger,        LiveMaxHp, HpModePercentage);
    public string MinorHealCombatTriggerConverted => FormatConversion(MinorHealCombatTrigger, LiveMaxHp, HpModePercentage);
    public string MajorHealCombatTriggerConverted => FormatConversion(MajorHealCombatTrigger, LiveMaxHp, HpModePercentage);
    public string RunIfBelowHpConverted           => FormatConversion(RunIfBelowHp,           LiveMaxHp, HpModePercentage);
    public string HangIfBelowHpConverted          => FormatConversion(HangIfBelowHp,          LiveMaxHp, HpModePercentage);

    // ----- MA conversion strings -----
    public string RestMaxMaConverted              => FormatConversion(RestMaxMa,              LiveMaxMa, MaModePercentage);
    public string RestIfBelowMaConverted          => FormatConversion(RestIfBelowMa,          LiveMaxMa, MaModePercentage);
    public string HealIfAboveMaRestingConverted   => FormatConversion(HealIfAboveMaResting,   LiveMaxMa, MaModePercentage);
    public string HealIfAboveMaCombatConverted    => FormatConversion(HealIfAboveMaCombat,    LiveMaxMa, MaModePercentage);
    public string RunIfBelowMaConverted           => FormatConversion(RunIfBelowMa,           LiveMaxMa, MaModePercentage);
    public string BlessIfAboveMaConverted         => FormatConversion(BlessIfAboveMa,         LiveMaxMa, MaModePercentage);

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        HealthSettings dto = new()
        {
            HpThresholdMode        = HpModeAbsolute ? ThresholdMode.Absolute : ThresholdMode.Percentage,
            RestMaxHp              = Clamp(RestMaxHp),
            RestIfBelowHp          = Clamp(RestIfBelowHp),
            RunIfBelowHp           = Clamp(RunIfBelowHp),
            HangIfBelowHp          = ClampHang(HangIfBelowHp),
            HealRestTrigger        = Clamp(HealRestTrigger),
            MinorHealCombatTrigger = Clamp(MinorHealCombatTrigger),
            MajorHealCombatTrigger = Clamp(MajorHealCombatTrigger),

            MaThresholdMode        = MaModeAbsolute ? ThresholdMode.Absolute : ThresholdMode.Percentage,
            RestMaxMa              = Clamp(RestMaxMa),
            RestIfBelowMa          = Clamp(RestIfBelowMa),
            HealIfAboveMaResting   = Clamp(HealIfAboveMaResting),
            HealIfAboveMaCombat    = Clamp(HealIfAboveMaCombat),
            RunIfBelowMa           = Clamp(RunIfBelowMa),
            BlessIfAboveMa         = Clamp(BlessIfAboveMa),

            UseMeditateAbility     = UseMeditateAbility,
            MeditateBeforeResting  = MeditateBeforeResting,

            PreRestCommand         = PreRestCommand  ?? string.Empty,
            PostRestCommand        = PostRestCommand ?? string.Empty,
        };

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();

        ClearDirty();
    }

    public override void Discard()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
    }

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
        HealthSettings dto = ReadOrDefault();

        HpModePercentage = dto.HpThresholdMode == ThresholdMode.Percentage;
        HpModeAbsolute   = dto.HpThresholdMode == ThresholdMode.Absolute;
        RestMaxHp              = dto.RestMaxHp;
        RestIfBelowHp          = dto.RestIfBelowHp;
        HealRestTrigger        = dto.HealRestTrigger;
        MinorHealCombatTrigger = dto.MinorHealCombatTrigger;
        MajorHealCombatTrigger = dto.MajorHealCombatTrigger;
        RunIfBelowHp           = dto.RunIfBelowHp;
        HangIfBelowHp          = dto.HangIfBelowHp;

        MaModePercentage = dto.MaThresholdMode == ThresholdMode.Percentage;
        MaModeAbsolute   = dto.MaThresholdMode == ThresholdMode.Absolute;
        RestMaxMa        = dto.RestMaxMa;
        RestIfBelowMa    = dto.RestIfBelowMa;
        HealIfAboveMaResting = dto.HealIfAboveMaResting;
        HealIfAboveMaCombat  = dto.HealIfAboveMaCombat;
        RunIfBelowMa     = dto.RunIfBelowMa;
        BlessIfAboveMa   = dto.BlessIfAboveMa;

        UseMeditateAbility    = dto.UseMeditateAbility;
        MeditateBeforeResting = dto.MeditateBeforeResting;

        PreRestCommand  = dto.PreRestCommand  ?? string.Empty;
        PostRestCommand = dto.PostRestCommand ?? string.Empty;
    }

    private HealthSettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new HealthSettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json))
            return new HealthSettings();
        try
        {
            return JsonSerializer.Deserialize<HealthSettings>(json) ?? new HealthSettings();
        }
        catch
        {
            // Malformed delta — fall back to defaults rather than throwing.
            return new HealthSettings();
        }
    }

    // ----- IsDirty plumbing -----------------------------------------

    private void ClearDirty()
    {
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void MarkDirty()
    {
        if (_suppressDirty) return;
        if (_dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    // HP column
    partial void OnHpModePercentageChanged(bool value)
    {
        if (value) HpModeAbsolute = false;
        RefreshAllHpConverted();
        RefreshHangBounds();
        MarkDirty();
    }
    partial void OnHpModeAbsoluteChanged(bool value)
    {
        if (value) HpModePercentage = false;
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
    partial void OnRunIfBelowHpChanged(int value)             { OnPropertyChanged(nameof(RunIfBelowHpConverted));           MarkDirty(); }
    partial void OnHangIfBelowHpChanged(int value)            { OnPropertyChanged(nameof(HangIfBelowHpConverted));          MarkDirty(); }

    // MA column
    partial void OnMaModePercentageChanged(bool value)
    {
        if (value) MaModeAbsolute = false;
        RefreshAllMaConverted();
        MarkDirty();
    }
    partial void OnMaModeAbsoluteChanged(bool value)
    {
        if (value) MaModePercentage = false;
        RefreshAllMaConverted();
        MarkDirty();
    }
    partial void OnRestMaxMaChanged(int value)                { OnPropertyChanged(nameof(RestMaxMaConverted));            MarkDirty(); }
    partial void OnRestIfBelowMaChanged(int value)            { OnPropertyChanged(nameof(RestIfBelowMaConverted));        MarkDirty(); }
    partial void OnHealIfAboveMaRestingChanged(int value)     { OnPropertyChanged(nameof(HealIfAboveMaRestingConverted)); MarkDirty(); }
    partial void OnHealIfAboveMaCombatChanged(int value)      { OnPropertyChanged(nameof(HealIfAboveMaCombatConverted));  MarkDirty(); }
    partial void OnRunIfBelowMaChanged(int value)             { OnPropertyChanged(nameof(RunIfBelowMaConverted));         MarkDirty(); }
    partial void OnBlessIfAboveMaChanged(int value)           { OnPropertyChanged(nameof(BlessIfAboveMaConverted));       MarkDirty(); }

    private void RefreshAllHpConverted()
    {
        OnPropertyChanged(nameof(RestMaxHpConverted));
        OnPropertyChanged(nameof(RestIfBelowHpConverted));
        OnPropertyChanged(nameof(HealRestTriggerConverted));
        OnPropertyChanged(nameof(MinorHealCombatTriggerConverted));
        OnPropertyChanged(nameof(MajorHealCombatTriggerConverted));
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
        OnPropertyChanged(nameof(BlessIfAboveMaConverted));
    }

    // Meditation
    partial void OnUseMeditateAbilityChanged(bool value)      => MarkDirty();
    partial void OnMeditateBeforeRestingChanged(bool value)   => MarkDirty();

    // Resting commands
    partial void OnPreRestCommandChanged(string value)        => MarkDirty();
    partial void OnPostRestCommandChanged(string value)       => MarkDirty();
}
