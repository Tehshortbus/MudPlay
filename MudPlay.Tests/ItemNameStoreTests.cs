using System.IO;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class ItemNameStoreTests : IDisposable
{
    private readonly string _root;

    public ItemNameStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-itemnames-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string ItemsJson = """
        [
          { "Number": 172,  "Name": "black star key",   "Encum": 5,  "ItemType": 7 },
          { "Number": 1980, "Name": "ancient brass key", "Encum": 5,  "ItemType": 7 },
          { "Number": 1720, "Name": "white dragon boots","Encum": 65, "ItemType": 0 }
        ]
        """;

    private ItemNameStore NewStore()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), ItemsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        ItemNameStore store = new(cache);
        store.OnActiveSetChanged("alpha");
        return store;
    }

    [Fact]
    public void GetName_KnownItemId_ReturnsName()
    {
        ItemNameStore s = NewStore();
        Assert.Equal("black star key", s.GetName(172));
        Assert.Equal("ancient brass key", s.GetName(1980));
    }

    // A record name passes as written; the loose wordings FindByName accepts
    // (an article, a count) do not.
    [Fact]
    public void IsRecordName_TakesOnlyTheNameAsRecorded()
    {
        ItemNameStore s = NewStore();
        Assert.True(s.IsRecordName("black star key"));
        Assert.True(s.IsRecordName("Black Star Key"));
        Assert.NotNull(s.FindByName("a black star key"));
        Assert.False(s.IsRecordName("a black star key"));
        Assert.False(s.IsRecordName("2 black star key"));
        Assert.False(s.IsRecordName("silver key"));
    }

    [Fact]
    public void GetName_UnknownItemId_ReturnsNull()
    {
        ItemNameStore s = NewStore();
        Assert.Null(s.GetName(9999));
    }

    [Fact]
    public void ActiveSetChanged_ToNull_ClearsStore()
    {
        ItemNameStore s = NewStore();
        Assert.NotNull(s.GetName(172));

        s.OnActiveSetChanged(null);

        Assert.Null(s.GetName(172));
    }

    // ----- The off-hand list --------------------------------------------

    // Mix of weapons (ItemType==1), off-hand items (Worn==12), and neither.
    private const string SlotItemsJson = """
        [
          { "Number": 10, "Name": "Zaxe",        "ItemType": 1, "Worn": 0  },
          { "Number": 11, "Name": "axe",         "ItemType": 1, "Worn": 0  },
          { "Number": 12, "Name": "axe",         "ItemType": 1, "Worn": 0  },
          { "Number": 13, "Name": "main-gauche", "ItemType": 1, "Worn": 12 },
          { "Number": 14, "Name": "black shield","ItemType": 0, "Worn": 12 },
          { "Number": 15, "Name": "aegis shield","ItemType": 0, "Worn": 12 },
          { "Number": 16, "Name": "leather cap", "ItemType": 0, "Worn": 5  }
        ]
        """;

    private ItemNameStore NewSlotStore()
    {
        Directory.CreateDirectory(Path.Combine(_root, "slots"));
        File.WriteAllText(Path.Combine(_root, "slots", "Items.json"), SlotItemsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("slots");
        ItemNameStore store = new(cache);
        store.OnActiveSetChanged("slots");
        return store;
    }

    [Fact]
    public void OffHandNames_ContainsOnlyWorn12_Sorted()
    {
        ItemNameStore s = NewSlotStore();
        // main-gauche is a weapon worn in the off-hand slot, so it shows
        // in both lists (parity: off-hand box = Worn==12 regardless of
        // ItemType). Armour off-hands (shields) appear here too.
        Assert.Equal(new[] { "aegis shield", "black shield", "main-gauche" }, s.OffHandNames);
    }

    [Fact]
    public void SlotLists_ClearOnNullSet()
    {
        ItemNameStore s = NewSlotStore();
        Assert.NotEmpty(s.OffHandNames);

        s.OnActiveSetChanged(null);

        Assert.Empty(s.OffHandNames);
    }

    [Fact]
    public void OriginalFixture_HasNoWeaponsOrOffHands()
    {
        // The key/boots fixture has no Worn==12 → the off-hand list stays
        // empty, proving classification is field-driven.
        ItemNameStore s = NewStore();
        Assert.Empty(s.OffHandNames);
    }

    // The game totals a carried item's abilities when it isn't a weapon and has no
    // wear slot, so only those count from the pack.
    [Fact]
    public void PackStealthOf_CountsOnlyCarriedOnlyItems()
    {
        const string json = """
            [
              { "Number": 690,  "Name": "log raft",            "ItemType": 10, "Worn": 0,  "Abil-0": 27, "AbilVal-0": -125 },
              { "Number": 1835, "Name": "stormmetal corselet", "ItemType": 0,  "Worn": 11, "Abil-2": 27, "AbilVal-2": -40 },
              { "Number": 2223, "Name": "lunarblade",          "ItemType": 1,  "Worn": 1,  "Abil-0": 27, "AbilVal-0": -50 },
              { "Number": 77,   "Name": "healing potion",      "ItemType": 6,  "Worn": 0 }
            ]
            """;
        Directory.CreateDirectory(Path.Combine(_root, "pack"));
        File.WriteAllText(Path.Combine(_root, "pack", "Items.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet("pack");
        ItemNameStore store = new(cache);
        store.OnActiveSetChanged("pack");

        Assert.Equal(-125, store.PackStealthOf("log raft"));
        Assert.Equal(0, store.PackStealthOf("stormmetal corselet"));
        Assert.Equal(0, store.PackStealthOf("lunarblade"));
        Assert.Equal(0, store.PackStealthOf("healing potion"));
        Assert.Equal(0, store.PackStealthOf("no such thing"));
    }
}
