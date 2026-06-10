using System.IO;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class ItemNameStoreTests : IDisposable
{
    private readonly string _root;

    public ItemNameStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-itemnames-tests-" + Path.GetRandomFileName());
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
    public void Load_IndexesAllRows()
    {
        ItemNameStore s = NewStore();
        Assert.Equal(3, s.EntryCount);
    }

    [Fact]
    public void GetName_KnownItemId_ReturnsName()
    {
        ItemNameStore s = NewStore();
        Assert.Equal("black star key", s.GetName(172));
        Assert.Equal("ancient brass key", s.GetName(1980));
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
        Assert.True(s.EntryCount > 0);

        s.OnActiveSetChanged(null);

        Assert.Equal(0, s.EntryCount);
        Assert.Null(s.GetName(172));
    }

    // ----- Slot-filtered suggestion lists (Combat typeahead) --------

    // Mix of weapons (ItemType==1), off-hand items (Worn==12), and
    // neither — plus a "Zaxe" before "axe" to prove the lists sort,
    // and a duplicate "axe" name to prove they de-dup.
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
    public void WeaponNames_ContainsOnlyItemType1_SortedAndDeduped()
    {
        ItemNameStore s = NewSlotStore();
        // "axe" appears twice in source → one entry; sorted before "Zaxe".
        Assert.Equal(new[] { "axe", "main-gauche", "Zaxe" }, s.WeaponNames);
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
        Assert.NotEmpty(s.WeaponNames);
        Assert.NotEmpty(s.OffHandNames);

        s.OnActiveSetChanged(null);

        Assert.Empty(s.WeaponNames);
        Assert.Empty(s.OffHandNames);
    }

    [Fact]
    public void OriginalFixture_HasNoWeaponsOrOffHands()
    {
        // The key/boots fixture has ItemType 7/0 and no Worn==12 → both
        // slot lists stay empty, proving classification is field-driven.
        ItemNameStore s = NewStore();
        Assert.Empty(s.WeaponNames);
        Assert.Empty(s.OffHandNames);
    }
}
