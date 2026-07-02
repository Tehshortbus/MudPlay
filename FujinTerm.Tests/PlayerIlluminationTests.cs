using System;
using System.IO;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Light;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class PlayerIlluminationTests : IDisposable
{
    private readonly string _root;

    public PlayerIlluminationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-playerillu-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // A lantern projects 175 illu (ability code 54 = IlluTarget); a ring of
    // light wears +50 illu (code 13 folds into PlusIlluminate). The sword is
    // ordinary gear with no illumination.
    private const string ItemsJson = """
        [
          { "Number": 176, "Name": "lantern", "ItemType": 6, "UseCount": 2400,
            "Abil-0": 54, "AbilVal-0": 175 },
          { "Number": 900, "Name": "ring of light", "ItemType": 2,
            "Abil-0": 13, "AbilVal-0": 50 },
          { "Number": 64,  "Name": "long sword", "ItemType": 1 }
        ]
        """;

    private GameDataCache NewCache(string set = "alpha", string json = ItemsJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, set));
        File.WriteAllText(Path.Combine(_root, set, "Items.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        return cache;
    }

    private static InventorySnapshot SnapshotOf(
        EquippedItem[] worn, ReadiedLight? readied)
        => new(CurrencyHoldings.Empty, EncumbranceReading.Empty,
               worn, Array.Empty<string>(), DateTimeOffset.Now, readied);

    private static readonly EquippedItem[] IlluRing = { new("ring of light", "Finger") };
    private static readonly EquippedItem[] PlainSword = { new("long sword", "Weapon Hand") };

    [Fact]
    public void Current_SumsWornIlluAndReadiedLightStrength()
    {
        GameDataCache cache = NewCache();
        LightItemIndex lights = new(cache);
        InventorySnapshot snap = SnapshotOf(IlluRing, new ReadiedLight("lantern", 239));

        PlayerIllumination illu = new(() => snap, lights, cache);

        // worn +50 illu + lantern's 175 projected = 225.
        Assert.Equal(225, illu.Current);
    }

    [Fact]
    public void Current_NoReadiedLight_IsWornIlluOnly()
    {
        GameDataCache cache = NewCache();
        LightItemIndex lights = new(cache);
        InventorySnapshot snap = SnapshotOf(IlluRing, readied: null);

        PlayerIllumination illu = new(() => snap, lights, cache);

        Assert.Equal(50, illu.Current);
    }

    [Fact]
    public void Current_ReadiedLightOnly_IsProjectedStrength()
    {
        GameDataCache cache = NewCache();
        LightItemIndex lights = new(cache);
        InventorySnapshot snap = SnapshotOf(PlainSword, new ReadiedLight("lantern", 12));

        PlayerIllumination illu = new(() => snap, lights, cache);

        Assert.Equal(175, illu.Current);
    }

    [Fact]
    public void Current_EmptySnapshot_IsZero()
    {
        GameDataCache cache = NewCache();
        LightItemIndex lights = new(cache);

        PlayerIllumination illu = new(() => InventorySnapshot.Empty, lights, cache);

        Assert.Equal(0, illu.Current);
    }

    [Fact]
    public void Current_ReadiedLightUnknownToCatalogue_ContributesZero()
    {
        GameDataCache cache = NewCache();
        LightItemIndex lights = new(cache);
        // "moon-lamp" isn't in this set — a light the parser saw but the
        // catalogue can't price contributes no strength (worn illu still counts).
        InventorySnapshot snap = SnapshotOf(IlluRing, new ReadiedLight("moon-lamp", 4000));

        PlayerIllumination illu = new(() => snap, lights, cache);

        Assert.Equal(50, illu.Current);
    }

    [Fact]
    public void Current_ReReadsProviderEachCall()
    {
        GameDataCache cache = NewCache();
        LightItemIndex lights = new(cache);
        InventorySnapshot live = InventorySnapshot.Empty;

        PlayerIllumination illu = new(() => live, lights, cache);
        Assert.Equal(0, illu.Current);

        // A fresh `i` dump lights the lantern — the next read tracks it without
        // any subscription or cache invalidation on the calculator's part.
        live = SnapshotOf(IlluRing, new ReadiedLight("lantern", 240));
        Assert.Equal(225, illu.Current);
    }
}
