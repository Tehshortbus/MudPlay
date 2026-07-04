using System.Collections.Generic;
using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

// "Auto-Lair" tab — per-character scheduler tuning. Marked-room catalogue lives
// BBS-side in LairManager; the heuristic + weighting + per-encumbrance hop-time
// table live here.
//
// Apply pushes the live settings into AppServices.AutoLair (heuristic / penalty /
// engage timeout) AND replaces AutoLairManager.TravelCostModel with the right
// impl — flat vs encumbrance-gated — built from the section's current values. The
// scheduler picks up the new estimates on its next tick.
public sealed partial class AutoLairSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "AutoLair";

    private readonly ProfileService _profile;
    private Control? _view;
    private bool _suppressDirty;
    private bool _dirty;

    public override string Id => "auto-lair";
    public override string Title => "Auto-Lair";
    public override bool IsDirty => _dirty;

    public bool HasProfile => _profile.Current is not null;

    public string Description =>
        "Per-character tuning for the Auto-Lair scheduler. The marked-room list itself is per BBS " +
        "(saved via the Navigation window's \"Save lairs\" button); these knobs say how aggressively " +
        "the scheduler chases ready-now lairs, how it estimates travel time, and how long the engage " +
        "window holds at a lair before re-evaluating.";

    public override Control View => _view ??= new AutoLairSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels
    {
        get
        {
            yield return Title;
            yield return "Heuristic";
            yield return "Default";
            yield return "Throughput";
            yield return "Idle penalty";
            yield return "Engage timeout";
            yield return "Travel cost";
            yield return "Flat seconds per hop";
            yield return "Encumbrance gated";
            yield return "None";
            yield return "Light";
            yield return "Medium";
            yield return "Heavy";
            yield return "Encumbered";
        }
    }

    // ----- Routing heuristic ----------------------------------------

    // Backing enum value bound to the radio group via two booleans below.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDefaultHeuristic))]
    [NotifyPropertyChangedFor(nameof(IsThroughputHeuristic))]
    private AutoLairHeuristic _heuristic = AutoLairHeuristic.Default;

    public bool IsDefaultHeuristic
    {
        get => Heuristic == AutoLairHeuristic.Default;
        set { if (value) Heuristic = AutoLairHeuristic.Default; }
    }
    public bool IsThroughputHeuristic
    {
        get => Heuristic == AutoLairHeuristic.Throughput;
        set { if (value) Heuristic = AutoLairHeuristic.Throughput; }
    }

    [ObservableProperty] private double _idlePenalty = 1.0;
    [ObservableProperty] private int    _engageTimeoutSeconds = 30;

    // ----- Travel cost mode -----------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFlatMode))]
    [NotifyPropertyChangedFor(nameof(IsEncumbranceGatedMode))]
    private AutoLairTravelCostMode _travelCostMode = AutoLairTravelCostMode.Flat;

    public bool IsFlatMode
    {
        get => TravelCostMode == AutoLairTravelCostMode.Flat;
        set { if (value) TravelCostMode = AutoLairTravelCostMode.Flat; }
    }
    public bool IsEncumbranceGatedMode
    {
        get => TravelCostMode == AutoLairTravelCostMode.EncumbranceGated;
        set { if (value) TravelCostMode = AutoLairTravelCostMode.EncumbranceGated; }
    }

    [ObservableProperty] private double _flatSecondsPerHop = 1.5;

    // ----- Per-encumbrance seconds-per-hop --------------------------

    [ObservableProperty] private double _hopNone       = 1.0;
    [ObservableProperty] private double _hopLight      = 1.5;
    [ObservableProperty] private double _hopMedium     = 2.5;
    [ObservableProperty] private double _hopHeavy      = 4.0;
    [ObservableProperty] private double _hopEncumbered = 6.0;

    public AutoLairSectionViewModel()
        : this(AppServices.Current.Profile) { }

    public AutoLairSectionViewModel(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= OnProfileClosedExternally;
        });
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        AutoLairSettings dto = BuildDto();

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();

        ApplyToServices(dto);
        ClearDirty();
    }

    public override void Discard()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
    }

    private AutoLairSettings BuildDto() => new()
    {
        Heuristic = Heuristic,
        IdlePenalty = Math.Max(0, IdlePenalty),
        EngageTimeoutSeconds = Math.Clamp(EngageTimeoutSeconds, 1, 3600),
        TravelCostMode = TravelCostMode,
        FlatSecondsPerHop = Math.Max(0.1, FlatSecondsPerHop),
        HopTimesByEncumbrance = new EncumbranceHopTimes
        {
            None       = Math.Max(0.1, HopNone),
            Light      = Math.Max(0.1, HopLight),
            Medium     = Math.Max(0.1, HopMedium),
            Heavy      = Math.Max(0.1, HopHeavy),
            Encumbered = Math.Max(0.1, HopEncumbered),
        },
    };

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
        AutoLairSettings dto = ReadOrDefault();
        Heuristic = dto.Heuristic;
        IdlePenalty = dto.IdlePenalty;
        EngageTimeoutSeconds = dto.EngageTimeoutSeconds;
        TravelCostMode = dto.TravelCostMode;
        FlatSecondsPerHop = dto.FlatSecondsPerHop;
        HopNone       = dto.HopTimesByEncumbrance.None;
        HopLight      = dto.HopTimesByEncumbrance.Light;
        HopMedium     = dto.HopTimesByEncumbrance.Medium;
        HopHeavy      = dto.HopTimesByEncumbrance.Heavy;
        HopEncumbered = dto.HopTimesByEncumbrance.Encumbered;
        ApplyToServices(dto);
    }

    private AutoLairSettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new AutoLairSettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json)) return new AutoLairSettings();
        try
        {
            return JsonSerializer.Deserialize<AutoLairSettings>(json) ?? new AutoLairSettings();
        }
        catch
        {
            return new AutoLairSettings();
        }
    }

    // Push the DTO into the live AutoLairManager — both the scalar knobs
    // (heuristic / idle penalty / engage timeout) AND the travel-cost model
    // selection. The replacement is atomic from the scheduler's perspective; the
    // next tick (≤ 1 s away) sees the new estimate.
    private static void ApplyToServices(AutoLairSettings dto)
    {
        AppServices svcs = AppServices.Current;
        svcs.AutoLair.Heuristic = dto.Heuristic;
        svcs.AutoLair.IdlePenalty = Math.Max(0, dto.IdlePenalty);
        svcs.AutoLair.EngageTimeoutSeconds = Math.Clamp(dto.EngageTimeoutSeconds, 1, 3600);

        svcs.AutoLair.TravelCostModel = dto.TravelCostMode switch
        {
            AutoLairTravelCostMode.EncumbranceGated =>
                new EncumbranceGatedTravelCostModel(svcs.PlayerState, dto.HopTimesByEncumbrance),
            _ =>
                new FlatTravelCostModel(Math.Max(0.1, dto.FlatSecondsPerHop)),
        };
    }

    // ----- IsDirty plumbing -----------------------------------------

    private void ClearDirty()
    {
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnHeuristicChanged(AutoLairHeuristic value)       => MarkDirty();
    partial void OnIdlePenaltyChanged(double value)                 => MarkDirty();
    partial void OnEngageTimeoutSecondsChanged(int value)           => MarkDirty();
    partial void OnTravelCostModeChanged(AutoLairTravelCostMode value) => MarkDirty();
    partial void OnFlatSecondsPerHopChanged(double value)           => MarkDirty();
    partial void OnHopNoneChanged(double value)                     => MarkDirty();
    partial void OnHopLightChanged(double value)                    => MarkDirty();
    partial void OnHopMediumChanged(double value)                   => MarkDirty();
    partial void OnHopHeavyChanged(double value)                    => MarkDirty();
    partial void OnHopEncumberedChanged(double value)               => MarkDirty();

    private void MarkDirty()
    {
        if (_suppressDirty) return;
        if (_dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }
}
