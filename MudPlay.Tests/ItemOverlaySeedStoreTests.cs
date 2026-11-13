using System.IO;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The game lets a character carry one item of a Unique Pool (item ability 188) at a
// time (user, 2026-11-12), so the installed defaults start each such item ticked
// Carry only one: Roomba then plans it one copy a trip with nothing set up.
public sealed class ItemOverlaySeedStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-item-seed-" + Path.GetRandomFileName());
    private readonly string _set = "item-seed-" + Path.GetRandomFileName();

    public ItemOverlaySeedStoreTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, _set));
        File.WriteAllText(Path.Combine(_root, _set, "Items.json"), """
            [
              { "Number": 1103, "Name": "glass orb", "ItemType": 10, "Encum": 10,
                "Abil-0": 43, "AbilVal-0": 664, "Abil-3": 188, "AbilVal-3": 4 },
              { "Number": 2, "Name": "war hammer", "ItemType": 1, "Encum": 10, "Abil-0": 46, "AbilVal-0": 5 }
            ]
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void AUniquePoolItem_StartsAsCarryOnlyOne_AndNoOtherDoes()
    {
        GameDataCache cache = new(_root);
        cache.SwitchSet(_set);
        ItemOverlaySeedStore seed = new(cache);

        seed.Load(_set);

        Assert.True(seed.GetOverlay(1103).CarryOnlyOne);
        Assert.Null(seed.GetOverlay(2).CarryOnlyOne);
    }
}
