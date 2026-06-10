using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Items" tab — room-wide loot-timing toggle for
/// <see cref="Game.Inventory.AutoGetItemsManager"/>. Per-item
/// collect/ignore lives on each item's game-data
/// <see cref="Models.GameData.ItemOverlay.AutoCollect"/> flag, not here;
/// this tab carries only the "collect after combat finished" timing
/// choice. Persists as the <c>"ItemLoot"</c> entry in
/// <see cref="CharacterProfile.Settings"/>.
/// </summary>
public sealed partial class ItemsSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "ItemLoot";

    private readonly ProfileService _profile;
    private Control? _view;
    private bool _suppressDirty;
    private bool _dirty;

    public override string Id => "items";
    public override string Title => "Items";
    public override bool IsDirty => _dirty;

    /// <summary>True when a profile is loaded — editor is hidden otherwise.</summary>
    public bool HasProfile => _profile.Current is not null;

    public override Control View => _view ??= new ItemsSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Items", "Loot", "Auto-get", "Collect", "Pick up",
        "Collect after combat finished",
    };

    /// <summary>
    /// Defer item gets until the room's combat is finished. Mirrors the
    /// Cash tab's same-named toggle.
    /// </summary>
    [ObservableProperty] private bool _collectAfterCombatFinished;

    public ItemsSectionViewModel() : this(AppServices.Current.Profile) { }

    public ItemsSectionViewModel(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        ItemLootSettings dto = new()
        {
            CollectAfterCombatFinished = CollectAfterCombatFinished,
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
        ItemLootSettings dto = ReadOrDefault();
        CollectAfterCombatFinished = dto.CollectAfterCombatFinished;
    }

    private ItemLootSettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new ItemLootSettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json))
            return new ItemLootSettings();
        try
        {
            return JsonSerializer.Deserialize<ItemLootSettings>(json) ?? new ItemLootSettings();
        }
        catch
        {
            // Malformed delta — fall back to defaults rather than throwing.
            return new ItemLootSettings();
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

    partial void OnCollectAfterCombatFinishedChanged(bool value) => MarkDirty();
}
