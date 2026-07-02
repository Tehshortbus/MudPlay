using System;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.Inventory;
using FujinTerm.Services;

namespace FujinTerm.Game.Light;

/// <summary>
/// The player's live carried illumination — the <c>charIllu</c> half of the MME
/// visibility model (<see cref="LightModel"/>). Two sources sum into it:
/// worn <c>+illu</c> gear (ability codes 13 / 14, folded by
/// <see cref="CharacterCalculator.AggregateEquipmentStats"/> into
/// <c>PlusIlluminate</c>) and the strength a currently-readied light projects
/// (ability code 54, <c>IlluTarget</c>, resolved from the catalogue by the
/// readied light's parsed name). The readied light is reported separately from
/// worn gear in the inventory snapshot, so the two never double-count.
/// </summary>
/// <remarks>
/// Reads the live <see cref="InventoryManager"/> snapshot through a provider
/// delegate rather than the manager directly, so the value tracks the latest
/// <c>i</c> dump without this service subscribing to anything. The same figure
/// feeds the map tooltip's room-light phrasing and the auto-light route
/// predictor, keeping "what the player can see" identical across both.
/// </remarks>
public sealed class PlayerIllumination
{
    private readonly Func<InventorySnapshot> _snapshot;
    private readonly LightItemIndex _lights;
    private readonly GameDataCache _cache;

    public PlayerIllumination(Func<InventorySnapshot> snapshot, LightItemIndex lights, GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(lights);
        ArgumentNullException.ThrowIfNull(cache);
        _snapshot = snapshot;
        _lights = lights;
        _cache = cache;
    }

    /// <summary>
    /// The player's current carried illumination: worn <c>+illu</c> gear plus
    /// the readied light's projected strength. <c>0</c> before the first
    /// <c>i</c> dump (empty snapshot, no readied light).
    /// </summary>
    public int Current
    {
        get
        {
            InventorySnapshot snap = _snapshot();
            int readied = snap.ReadiedLight is { } rl
                ? _lights.FindByName(rl.Name)?.Strength ?? 0
                : 0;
            return WornOnly + readied;
        }
    }

    /// <summary>
    /// The worn-<c>+illu</c> half of <see cref="Current"/> on its own — the
    /// readied light's projection excluded. This is the baseline the auto-light
    /// planner needs: a light it picks to ready <i>replaces</i> the currently
    /// readied one, so the strength it must project is measured from worn gear
    /// alone, never crediting a light it may swap out. <c>0</c> before the first
    /// <c>i</c> dump.
    /// </summary>
    public int WornOnly => CharacterCalculator
        .AggregateEquipmentStats(_snapshot().EquippedItems, _cache)
        .Totals.PlusIlluminate;
}
