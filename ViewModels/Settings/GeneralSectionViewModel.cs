using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "General" tab. Per-character: default startup task, auto-connect,
/// and the boot-up state of every Action-menu auto-toggle in both
/// Manual-Mode and Auto-Mode columns.
/// </summary>
/// <remarks>
/// Loads from <see cref="CharacterProfile.Settings"/> on construction
/// and after a <see cref="ProfileService.ProfileLoaded"/> event so the
/// editor reflects whichever profile is currently active. Apply writes
/// back into the same JSON dictionary and calls
/// <see cref="ProfileService.Save"/>.
/// </remarks>
public sealed partial class GeneralSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "General";

    private readonly ProfileService _profile;
    private bool _suppressDirty = true;
    private bool _dirty;
    private Control? _view;

    public override string Id => "general";
    public override string Title => "General";
    public override bool IsDirty => _dirty;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "General", "Auto-connect", "Default task", "Do nothing",
        "Begin loop", "Begin auto-roam", "Manual-Mode Defaults",
        "Auto-Mode Defaults", "Auto-Combat", "Auto-Nuke",
        "Auto-Heal", "Auto-Rest", "Auto-Bless", "Auto-Light",
    };

    public override Control View => _view ??= new GeneralSectionView { DataContext = this };

    /// <summary>True when a profile is loaded — editor is hidden otherwise.</summary>
    public bool HasProfile => _profile.Current is not null;

    // ----- Initial task (three radios — mutual exclusion handled by GroupName) -----
    [ObservableProperty] private bool _isTaskDoNothing = true;
    [ObservableProperty] private bool _isTaskBeginLoop;
    [ObservableProperty] private bool _isTaskBeginAutoRoam;

    [ObservableProperty] private string? _defaultLoopName;
    [ObservableProperty] private bool _autoConnect;

    // ----- Manual-Mode defaults -----
    [ObservableProperty] private bool _mmAutoCombat;
    [ObservableProperty] private bool _mmAutoNuke;
    [ObservableProperty] private bool _mmAutoHealRest;
    [ObservableProperty] private bool _mmAutoBless;
    [ObservableProperty] private bool _mmAutoLight;

    // ----- Auto-Mode defaults -----
    [ObservableProperty] private bool _amAutoCombat;
    [ObservableProperty] private bool _amAutoNuke;
    [ObservableProperty] private bool _amAutoHealRest;
    [ObservableProperty] private bool _amAutoBless;
    [ObservableProperty] private bool _amAutoLight;

    public GeneralSectionViewModel(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;

        LoadFromProfile();
        _suppressDirty = false;
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        GeneralSettings dto = new()
        {
            DefaultTask = IsTaskBeginLoop      ? InitialTask.BeginLoop
                        : IsTaskBeginAutoRoam  ? InitialTask.BeginAutoRoam
                        : InitialTask.DoNothing,
            DefaultLoopName = string.IsNullOrWhiteSpace(DefaultLoopName) ? null : DefaultLoopName,
            AutoConnect = AutoConnect,
            ManualMode = SnapshotManual(),
            AutoMode   = SnapshotAuto(),
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
        IsTaskBeginAutoRoam  = dto.DefaultTask == InitialTask.BeginAutoRoam;
        DefaultLoopName      = dto.DefaultLoopName;
        AutoConnect          = dto.AutoConnect;

        AutoActionDefaults m = dto.ManualMode;
        MmAutoCombat = m.AutoCombat;
        MmAutoNuke = m.AutoNuke;
        MmAutoHealRest = m.AutoHealRest;
        MmAutoBless = m.AutoBless;
        MmAutoLight = m.AutoLight;

        AutoActionDefaults a = dto.AutoMode;
        AmAutoCombat = a.AutoCombat;
        AmAutoNuke = a.AutoNuke;
        AmAutoHealRest = a.AutoHealRest;
        AmAutoBless = a.AutoBless;
        AmAutoLight = a.AutoLight;
    }

    private GeneralSettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new GeneralSettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json)) return new GeneralSettings();

        return JsonSerializer.Deserialize<GeneralSettings>(json.GetRawText())
               ?? new GeneralSettings();
    }

    private AutoActionDefaults SnapshotManual() => new()
    {
        AutoCombat = MmAutoCombat, AutoNuke = MmAutoNuke, AutoHealRest = MmAutoHealRest,
        AutoBless = MmAutoBless, AutoLight = MmAutoLight,
    };

    private AutoActionDefaults SnapshotAuto() => new()
    {
        AutoCombat = AmAutoCombat, AutoNuke = AmAutoNuke, AutoHealRest = AmAutoHealRest,
        AutoBless = AmAutoBless, AutoLight = AmAutoLight,
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
    partial void OnIsTaskBeginAutoRoamChanged(bool value) { if (value) UncheckOtherTasks(2); Dirty(); }
    partial void OnDefaultLoopNameChanged(string? value)  => Dirty();
    partial void OnAutoConnectChanged(bool value)         => Dirty();
    partial void OnMmAutoCombatChanged(bool value)        => Dirty();
    partial void OnMmAutoNukeChanged(bool value)          => Dirty();
    partial void OnMmAutoHealRestChanged(bool value)      => Dirty();
    partial void OnMmAutoBlessChanged(bool value)         => Dirty();
    partial void OnMmAutoLightChanged(bool value)         => Dirty();
    partial void OnAmAutoCombatChanged(bool value)        => Dirty();
    partial void OnAmAutoNukeChanged(bool value)          => Dirty();
    partial void OnAmAutoHealRestChanged(bool value)      => Dirty();
    partial void OnAmAutoBlessChanged(bool value)         => Dirty();
    partial void OnAmAutoLightChanged(bool value)         => Dirty();

    /// <summary>
    /// Belt + braces on top of RadioButton.GroupName — the View's
    /// GroupName handles the click-time mutual-exclusion, this guarantees
    /// programmatic state changes (Discard / ReloadAfterProfileSwap)
    /// can't leave two task radios true at once.
    /// </summary>
    private void UncheckOtherTasks(int keep)
    {
        if (keep != 0 && IsTaskDoNothing)     IsTaskDoNothing = false;
        if (keep != 1 && IsTaskBeginLoop)     IsTaskBeginLoop = false;
        if (keep != 2 && IsTaskBeginAutoRoam) IsTaskBeginAutoRoam = false;
    }
}
