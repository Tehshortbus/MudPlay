using System;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.Inventory;
using FujinTerm.Services;

namespace FujinTerm.Game.Light;

// The player's live carried illumination — the charIllu half of the LightModel
// visibility model. Two sources sum into it: worn +illu gear (ability codes 13 /
// 14, folded by CharacterCalculator.AggregateEquipmentStats into PlusIlluminate)
// and the strength a currently-readied light projects (ability code 54,
// IlluTarget, resolved from the catalogue by the readied light's parsed name). The
// readied light is reported separately from worn gear in the inventory snapshot,
// so the two never double-count.
//
// Reads the live InventoryManager snapshot through a provider delegate rather than
// the manager directly, so the value tracks the latest i dump without this service
// subscribing to anything. The same figure feeds the map tooltip's room-light
// phrasing and the auto-light route predictor, keeping "what the player can see"
// identical across both.
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

    // The player's current carried illumination: worn +illu gear plus the readied
    // light's projected strength. 0 before the first i dump (empty snapshot, no
    // readied light).
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

    // The worn-+illu half of Current on its own — the readied light's projection
    // excluded. This is the baseline the auto-light planner needs: a light it picks
    // to ready replaces the currently readied one, so the strength it must project
    // is measured from worn gear alone, never crediting a light it may swap out. 0
    // before the first i dump.
    public int WornOnly => CharacterCalculator
        .AggregateEquipmentStats(_snapshot().EquippedItems, _cache)
        .Totals.PlusIlluminate;
}
