using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.Light;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

// "Auto-Light" tab — provisioning knobs for the auto-light engine: how many
// hours of lit time to carry into a dark route, which light source to buy /
// ready, and when a dwindling supply triggers a resupply run. Persists as the
// "AutoLight" entry in CharacterProfile.Settings.
//
// The light dropdown is fed from the active set's LightItemIndex so it only
// offers real light items; the leading AutoPickLabel entry stores as null and
// lets the engine choose per route. These values only bite once the
// AutoActionDefaults.AutoLight master flag (Settings → General) is on.
public sealed partial class AutoLightSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "AutoLight";

    // Dropdown sentinel for "let the engine pick" — persists as a null
    // AutoLightSettings.PreferredLightName.
    public const string AutoPickLabel = "Automatic (per route)";

    private readonly ProfileService _profile;
    private readonly LightItemIndex? _lights;
    private readonly GameDataCache? _data;
    private Control? _view;
    private bool _suppressDirty;
    private bool _dirty;

    public override string Id => "autolight";
    public override string Title => "Auto-Light";
    public override bool IsDirty => _dirty;

    // True when a profile is loaded — editor is hidden otherwise.
    public bool HasProfile => _profile.Current is not null;

    public override Control View => _view ??= new AutoLightSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Auto-Light", "Light", "Lantern", "Torch", "Illumination", "Darkness",
        "Carry hours", "Reorder threshold", "Preferred light", "Provisioning",
    };

    // Light names offered in the dropdown: the auto sentinel followed by every
    // light in the active set, in MDB order.
    public ObservableCollection<string> AvailableLights { get; } = new();

    [ObservableProperty] private int _carryHours = 6;
    [ObservableProperty] private int _reorderThresholdMinutes = 60;
    [ObservableProperty] private string _selectedLight = AutoPickLabel;

    public AutoLightSectionViewModel() : this(
        AppServices.Current.Profile, TryGetLights(), TryGetData()) { }

    public AutoLightSectionViewModel(ProfileService profile, LightItemIndex? lights, GameDataCache? data)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _lights = lights;
        _data = data;
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        if (_data is not null) _data.ActiveSetChanged += OnActiveSetChanged;
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= OnProfileClosedExternally;
            if (_data is not null) _data.ActiveSetChanged -= OnActiveSetChanged;
        });

        RebuildLightList();
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
    }

    private static LightItemIndex? TryGetLights()
    {
        try { return AppServices.Current.Lights; } catch { return null; }   // design-time
    }

    private static GameDataCache? TryGetData()
    {
        try { return AppServices.Current.GameData; } catch { return null; } // design-time
    }

    // Human-readable summary of what CarryHours buys for the chosen light —
    // mirrors the user's "12 hours → 6 lanterns" mental model. Auto /
    // provisioning-off / unknown-light cases each read plainly.
    public string CarryReadout
    {
        get
        {
            if (CarryHours <= 0)
                return "Provisioning off — readies one light on demand, no stocking up.";
            if (SelectedLight == AutoPickLabel)
                return "Auto-sized to each route's darkest room.";
            LightItem? light = _lights?.FindByName(SelectedLight);
            if (light is not { } l || l.BurnTime.TotalHours <= 0)
                return $"Carry {CarryHours} h of light.";
            int count = (int)Math.Ceiling(CarryHours / l.BurnTime.TotalHours);
            double each = Math.Round(l.BurnTime.TotalHours, 1);
            return $"≈ {count} × {l.Name} ({each} h each) for {CarryHours} h of light.";
        }
    }

    private void RebuildLightList()
    {
        string previous = SelectedLight;
        AvailableLights.Clear();
        AvailableLights.Add(AutoPickLabel);
        if (_lights is not null)
            foreach (LightItem l in _lights.All)
                if (!AvailableLights.Contains(l.Name))
                    AvailableLights.Add(l.Name);

        // Keep the prior pick if the new set still carries it; else fall back to
        // auto so the box never shows a name absent from its own item list.
        SelectedLight = AvailableLights.Contains(previous) ? previous : AutoPickLabel;
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        AutoLightSettings dto = new()
        {
            CarryHours = Math.Clamp(CarryHours, 0, 48),
            ReorderThresholdMinutes = Math.Clamp(ReorderThresholdMinutes, 0, 600),
            PreferredLightName = SelectedLight == AutoPickLabel ? null : SelectedLight,
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

    private void OnActiveSetChanged(string? _)
    {
        _suppressDirty = true;
        RebuildLightList();
        _suppressDirty = false;
        OnPropertyChanged(nameof(CarryReadout));
    }

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
        AutoLightSettings dto = ReadOrDefault();
        CarryHours = dto.CarryHours;
        ReorderThresholdMinutes = dto.ReorderThresholdMinutes;
        SelectedLight = string.IsNullOrWhiteSpace(dto.PreferredLightName)
                        || !AvailableLights.Contains(dto.PreferredLightName!)
            ? AutoPickLabel
            : dto.PreferredLightName!;
        OnPropertyChanged(nameof(CarryReadout));
    }

    private AutoLightSettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new AutoLightSettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json))
            return new AutoLightSettings();
        try
        {
            return JsonSerializer.Deserialize<AutoLightSettings>(json) ?? new AutoLightSettings();
        }
        catch
        {
            // Malformed delta — fall back to defaults rather than throwing.
            return new AutoLightSettings();
        }
    }

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

    partial void OnCarryHoursChanged(int value)              { OnPropertyChanged(nameof(CarryReadout)); MarkDirty(); }
    partial void OnReorderThresholdMinutesChanged(int value) => MarkDirty();
    partial void OnSelectedLightChanged(string value)        { OnPropertyChanged(nameof(CarryReadout)); MarkDirty(); }
}
