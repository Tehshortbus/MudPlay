using System.Collections.Generic;
using System.IO;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The player defense profile Monster Intel + route Details seed their "Hits You %"
// from. Pins the AC-rounding rule: item AC is stored ×10, so the summed PlusAC
// carries tenths, and the game's integer combat AC is the FLOOR of that total —
// a trailing .5 must NOT round up (that over-stated AC by 1: user-reported a
// projected 61.5 that the sim was seeding as 62).
public sealed class IncomingHitEstimatorTests : IDisposable
{
    private readonly string _root;
    private const string Set = "test-set";

    public IncomingHitEstimatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-hitest-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, Set));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // Build a cache whose only item carries the given ×10 ArmourClass, worn in one slot.
    private GameDataCache CacheWithItemAc(int rawArmourClassTimesTen)
    {
        File.WriteAllText(Path.Combine(_root, Set, "Items.json"),
            $$"""[ { "Name": "test plate", "ArmourClass": {{rawArmourClassTimesTen}} } ]""");
        var cache = new GameDataCache(_root);
        cache.SwitchSet(Set);
        return cache;
    }

    private static PlayerDefenseProfile Defense(GameDataCache cache)
    {
        // Race/Class left blank so no innate AC folds in — the item is the only AC source.
        var stats = new PlayerStats { Name = "Tester", Level = 20, Agility = 50, Charm = 50 };
        var worn = new List<EquippedItem> { new("test plate", "Body") };
        return IncomingHitEstimator.BuildLiveDefense(
            stats, worn, new EncumbranceReading(0, 100, 0, EncumbranceLevel.None),
            cache, buffs: null, spells: null, questBonuses: null);
    }

    // 615 / 10 = 61.5 → floor 61. The regressing bug rounded this up to 62.
    [Fact]
    public void Ac_FractionalHalf_FloorsNotRoundsUp()
        => Assert.Equal(61, Defense(CacheWithItemAc(615)).Ac);

    // 365 / 10 = 36.5 → 36 (floor); confirms tenths are always dropped, not rounded.
    [Fact]
    public void Ac_FractionalHalf_EvenInteger_AlsoFloors()
        => Assert.Equal(36, Defense(CacheWithItemAc(365)).Ac);

    // A whole-number AC is unchanged.
    [Fact]
    public void Ac_WholeNumber_Unchanged()
        => Assert.Equal(40, Defense(CacheWithItemAc(400)).Ac);
}
