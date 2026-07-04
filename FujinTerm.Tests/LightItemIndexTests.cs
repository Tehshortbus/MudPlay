using System;
using System.IO;
using System.Linq;
using FujinTerm.Game.Light;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class LightItemIndexTests : IDisposable
{
    private readonly string _root;

    public LightItemIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-lightindex-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // torch (100 / 800) and lantern (175 / 2400) with IlluTarget (code 54) in
    // slot 0; "hooded lantern" carries it in a later slot to prove the scan
    // walks past slot 0; incense is a light with no illumination (strength 0);
    // a sword is ItemType 1 and must not be catalogued.
    private const string ItemsJson = """
        [
          { "Number": 175, "Name": "torch",  "ItemType": 6, "UseCount": 800,
            "Abil-0": 54, "AbilVal-0": 100 },
          { "Number": 176, "Name": "lantern", "ItemType": 6, "UseCount": 2400,
            "Abil-0": 119, "AbilVal-0": 0, "Abil-1": 54, "AbilVal-1": 175 },
          { "Number": 286, "Name": "hooded lantern", "ItemType": 6, "UseCount": 1800,
            "Abil-0": 0, "AbilVal-0": -15, "Abil-3": 54, "AbilVal-3": 175 },
          { "Number": 284, "Name": "incense", "ItemType": 6, "UseCount": 1 },
          { "Number": 64,  "Name": "long sword", "ItemType": 1, "UseCount": 0,
            "Abil-0": 54, "AbilVal-0": 999 }
        ]
        """;

    private LightItemIndex NewIndex(string set = "alpha", string json = ItemsJson)
        => new(NewCache(set, json));

    private GameDataCache NewCache(string set, string json)
    {
        Directory.CreateDirectory(Path.Combine(_root, set));
        File.WriteAllText(Path.Combine(_root, set, "Items.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        return cache;
    }

    [Fact]
    public void All_CataloguesOnlyLightItems()
    {
        LightItemIndex idx = NewIndex();
        // torch, lantern, hooded lantern, incense — the sword (ItemType 1) is out.
        Assert.Equal(4, idx.All.Count);
        Assert.DoesNotContain(idx.All, l => l.Name == "long sword");
    }

    [Fact]
    public void FindByName_ReadsStrengthAndBurnBudget()
    {
        LightItemIndex idx = NewIndex();

        Assert.NotNull(idx.FindByName("torch"));
        LightItem torch = idx.FindByName("torch")!.Value;
        Assert.Equal(100, torch.Strength);
        Assert.Equal(800, torch.UseCount);
        Assert.Equal(80, torch.FullReadied);
        Assert.Equal(TimeSpan.FromMinutes(40), torch.BurnTime);

        LightItem lantern = idx.FindByName("lantern")!.Value;
        Assert.Equal(175, lantern.Strength);
        Assert.Equal(240, lantern.FullReadied);
        Assert.Equal(TimeSpan.FromHours(2), lantern.BurnTime);
    }

    [Fact]
    public void FindByName_FindsIlluTargetInLaterAbilitySlot()
    {
        LightItemIndex idx = NewIndex();
        Assert.Equal(175, idx.FindByName("hooded lantern")!.Value.Strength);
    }

    [Fact]
    public void FindByName_IsCaseInsensitiveAndTrimmed()
    {
        LightItemIndex idx = NewIndex();
        Assert.Equal("torch", idx.FindByName("  TORCH ")!.Value.Name);
    }

    [Fact]
    public void FindByName_UnknownOrNonLight_ReturnsNull()
    {
        LightItemIndex idx = NewIndex();
        Assert.Null(idx.FindByName("long sword"));   // real item, wrong type
        Assert.Null(idx.FindByName("moon-lamp"));    // absent
        Assert.Null(idx.FindByName(null));
    }

    [Fact]
    public void ZeroStrengthLight_IsStillCatalogued()
    {
        LightItemIndex idx = NewIndex();
        LightItem incense = idx.FindByName("incense")!.Value;
        Assert.Equal(0, incense.Strength);
        Assert.Equal(0, incense.FullReadied);
    }

    [Fact]
    public void ActiveSetSwitch_RebuildsCatalogue()
    {
        GameDataCache cache = NewCache("alpha", ItemsJson);
        LightItemIndex idx = new(cache);
        Assert.Equal(4, idx.All.Count);

        const string other = "beta";
        Directory.CreateDirectory(Path.Combine(_root, other));
        File.WriteAllText(Path.Combine(_root, other, "Items.json"),
            """[ { "Number": 175, "Name": "torch", "ItemType": 6, "UseCount": 800, "Abil-0": 54, "AbilVal-0": 100 } ]""");

        // The index subscribes to the cache; switching the set invalidates the
        // catalogue and the next query rebuilds against the new set.
        cache.SwitchSet(other);
        Assert.Single(idx.All);
        Assert.Null(idx.FindByName("lantern"));
    }
}
