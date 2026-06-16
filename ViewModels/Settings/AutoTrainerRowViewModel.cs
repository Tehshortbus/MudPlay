using System;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.GameData;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// One discovered trainer in the Settings → Auto-Trainer table: its name,
/// host map/room, served level range, and the per-trainer Allow toggle that
/// decides whether auto-train may use it. Carries the raw <see cref="TrainerShop"/>
/// so the section's level / class filters can evaluate it. Flipping
/// <see cref="Allowed"/> marks the parent section dirty via the supplied callback.
/// </summary>
public sealed partial class AutoTrainerRowViewModel : ObservableObject
{
    private readonly TrainerShop _shop;
    private readonly Action _onAllowedChanged;

    /// <summary>Shops.Number — the persistence key for the allow/disallow set.</summary>
    public int ShopNumber => _shop.Number;
    public string Name => _shop.Name;
    /// <summary>Class restriction (0 = universal); used by the "my class only" filter.</summary>
    public int ClassRest => _shop.ClassRest;
    /// <summary>Host room as <c>"map/room"</c>, or "—" when the shop has no resolvable room.</summary>
    public string MapRoom { get; }
    /// <summary>Served level range as <c>"min–max"</c>.</summary>
    public string LevelRange { get; }

    [ObservableProperty] private bool _allowed;

    public AutoTrainerRowViewModel(TrainerShop shop, string mapRoom, string levelRange,
                                   bool allowed, Action onAllowedChanged)
    {
        ArgumentNullException.ThrowIfNull(onAllowedChanged);
        _shop = shop;
        MapRoom = mapRoom;
        LevelRange = levelRange;
        _allowed = allowed;
        _onAllowedChanged = onAllowedChanged;
    }

    /// <summary>True when this trainer serves a character at <paramref name="level"/>.</summary>
    public bool ServesLevel(int level) => _shop.ServesLevel(level);

    partial void OnAllowedChanged(bool value) => _onAllowedChanged();
}
