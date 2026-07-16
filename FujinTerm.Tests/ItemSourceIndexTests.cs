using System.IO;
using System.Linq;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// Pins ItemSourceIndex's two reverse-acquisition paths:
//   * Containers — invert ChestContentsReader so an item lists the chests it
//     drops from (item 10 ← Wooden Chest), and a chest-loot giveitem does NOT
//     also masquerade as a "given by" (its Called-From roots at a Spell).
//   * Givers — a TBInfo `giveitem` attributed via Called-From to its monster /
//     room root, with the requirement gate read off the award line:
//       - takeitem  → "turn in <item>"      (Monster #300 turn-in)
//       - price     → "purchase"            (Monster #301 merchant give)
//       - giveability → "quest reward"      (Room 3/606 dragon-statue reward)
//     and a textblock→textblock chain that roots at a monster two hops up.
//   * Self-invalidation on a set swap (the index is lazy, no eviction).
public sealed class ItemSourceIndexTests : IDisposable
{
    private readonly string _root;

    public ItemSourceIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-itemsrc-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string ItemsJson = """
        [
          { "Number": 100, "Name": "Wooden Chest", "ItemType": 8, "Abil-0": 43, "AbilVal-0": 200 },
          { "Number": 10, "Name": "Gold Ring", "ItemType": 2 },
          { "Number": 20, "Name": "Spider Silk", "ItemType": 2 },
          { "Number": 21, "Name": "Dragon Key", "ItemType": 2 },
          { "Number": 22, "Name": "Fang Blade", "ItemType": 1 },
          { "Number": 23, "Name": "Health Potion", "ItemType": 2 },
          { "Number": 24, "Name": "Dragon Hide Vest", "ItemType": 0 }
        ]
        """;

    private const string SpellsJson = """
        [
          { "Number": 200, "Name": "Open Wooden Chest", "Abil-0": 148, "AbilVal-0": 500 }
        ]
        """;

    private const string MonstersJson = """
        [
          { "Number": 300, "Name": "Martok" },
          { "Number": 301, "Name": "Gnome Merchant" },
          { "Number": 302, "Name": "Dragon Lord" }
        ]
        """;

    private const string RoomsJson = """
        [
          { "Map Number": 3, "Room Number": 606, "Name": "Dragon Statue" }
        ]
        """;

    // 500 — chest loot (Spell-rooted → never a giver).
    // 610 — Martok turn-in: takeitem 20 → giveitem 22.
    // 620 — dragon statue quest reward: giveitem 21 + giveability.
    // 630 — gnome merchant purchase: price → giveitem 23.
    // 640/641 — textblock chain: giveitem 24, called by TB 641, which is called
    //           by Monster 302.
    private const string TBInfoJson = """
        [
          { "Number": 500, "LinkTo": 0, "Action": "giveitem 10\n", "Called From": "Spell #200" },
          { "Number": 610, "LinkTo": 0, "Action": "give blade:takeitem 20 999:giveitem 22\n", "Called From": "Monster #300" },
          { "Number": 620, "LinkTo": 0, "Action": "insert fang:checkability 126 4:giveitem 21:giveability 126 5\n", "Called From": "Room 3/606" },
          { "Number": 630, "LinkTo": 0, "Action": "buy potion:price 5000 999:giveitem 23\n", "Called From": "Monster #301" },
          { "Number": 640, "LinkTo": 0, "Action": "giveitem 24\n", "Called From": "Textblock #641" },
          { "Number": 641, "LinkTo": 0, "Action": "text 640\n", "Called From": "Monster #302" }
        ]
        """;

    private GameDataCache NewCache(string setName = "alpha")
    {
        string dir = Path.Combine(_root, setName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Items.json"), ItemsJson);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), SpellsJson);
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), MonstersJson);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), RoomsJson);
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), TBInfoJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet(setName);
        return cache;
    }

    private static ItemSourceIndex NewIndex(GameDataCache cache)
    {
        // Wire TBInfoStore to the cache exactly as AppServices does so a set swap
        // reloads its entries — the index reads the store's typed entries and
        // must see them clear when the new set has no TBInfo.
        TBInfoStore tb = new(cache);
        cache.ActiveSetChanged += tb.OnActiveSetChanged;
        tb.OnActiveSetChanged(cache.ActiveSet);
        return new ItemSourceIndex(cache, tb);
    }

    [Fact]
    public void ContainersOf_LootedItem_ListsHostChest()
    {
        ItemSourceIndex index = NewIndex(NewCache());

        var containers = index.ContainersOf(10);
        ItemSource src = Assert.Single(containers);
        Assert.Equal(100, src.ContainerItemId);
        Assert.Equal("Wooden Chest", src.ContainerName);
        Assert.Equal(1.0, src.Probability, 3);
    }

    [Fact]
    public void GiversOf_ChestLootItem_IsEmpty()
    {
        // Item 10 is chest loot only — its giveitem block roots at a Spell, which
        // is not a walkable giver (the Found-in path covers it instead).
        ItemSourceIndex index = NewIndex(NewCache());
        Assert.Empty(index.GiversOf(10));
    }

    [Fact]
    public void GiversOf_MonsterTurnIn_NamesRequiredItem()
    {
        ItemSourceIndex index = NewIndex(NewCache());

        ItemGiver giver = Assert.Single(index.GiversOf(22));
        Assert.Equal(ItemGiverKind.Monster, giver.Kind);
        Assert.Equal(300, giver.Number);
        Assert.Equal("Martok", giver.Name);
        Assert.Equal("turn in Spider Silk", giver.Requirement);
    }

    [Fact]
    public void GiversOf_RoomQuestReward_AttributesToRoom()
    {
        ItemSourceIndex index = NewIndex(NewCache());

        ItemGiver giver = Assert.Single(index.GiversOf(21));
        Assert.Equal(ItemGiverKind.Room, giver.Kind);
        Assert.Equal(3, giver.Map);
        Assert.Equal(606, giver.Room);
        Assert.Equal("Dragon Statue", giver.Name);
        Assert.Equal("quest reward", giver.Requirement);
    }

    [Fact]
    public void GiversOf_MerchantPrice_ReadsPurchase()
    {
        ItemSourceIndex index = NewIndex(NewCache());

        ItemGiver giver = Assert.Single(index.GiversOf(23));
        Assert.Equal(ItemGiverKind.Monster, giver.Kind);
        Assert.Equal("Gnome Merchant", giver.Name);
        Assert.Equal("purchase", giver.Requirement);
    }

    [Fact]
    public void GiversOf_TextblockChain_RootsAtMonster()
    {
        ItemSourceIndex index = NewIndex(NewCache());

        ItemGiver giver = Assert.Single(index.GiversOf(24));
        Assert.Equal(ItemGiverKind.Monster, giver.Kind);
        Assert.Equal(302, giver.Number);
        Assert.Equal("Dragon Lord", giver.Name);
        Assert.Equal(string.Empty, giver.Requirement);
    }

    [Fact]
    public void SwitchingSets_RebuildsFromNewSet()
    {
        GameDataCache cache = NewCache();
        ItemSourceIndex index = NewIndex(cache);
        Assert.NotEmpty(index.GiversOf(22));   // populate against 'alpha'

        // A set with no game-data tables clears both maps on next query.
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        cache.SwitchSet("empty");
        Assert.Empty(index.GiversOf(22));
        Assert.Empty(index.ContainersOf(10));
    }
}
