using System.IO;
using System.Linq;
using FujinTerm.Game.GameData;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// Pins ChestContentsReader's loot-tree decode: the Items(8)→Abil 43→Spells→Abil
// 148→TBInfo chain, cumulative-threshold brackets, nested `random`, failitem
// duds, the at-least-once survival product across independent draws, and the
// min/max item-count model. A synthetic set exercises every branch:
//
//   Item 100 (container) → Spell 200 → Textblock 500 (the open script)
//     500: message 1 : giveitem 10 : random 501 : random 501   (2 draws of 501)
//     501: 50 → giveitem 11        (q 0.50, 1 item, certain)
//          80 → random 502         (q 0.30, → Gem via 502)
//         100 → failitem 999       (q 0.20, dud → 0 items)
//     502: 100 → giveitem 12       (q 1.00, 1 item, certain)
//
// Expected drop chances: Gold Ring guaranteed (1.0); Silver Ring
// 1−(1−0.5)² = 0.75; Gem 1−(1−0.30)² = 0.51. Item count: top giveitem = 1
// guaranteed, each 501 draw yields 0..1 → total 1..3.
public sealed class ChestContentsReaderTests : IDisposable
{
    private readonly string _root;

    public ChestContentsReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-chest-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string ItemsJson = """
        [
          { "Number": 100, "Name": "Wooden Chest", "ItemType": 8,
            "Abil-0": 43, "AbilVal-0": 200 },
          { "Number": 10, "Name": "Gold Ring", "ItemType": 2 },
          { "Number": 11, "Name": "Silver Ring", "ItemType": 2 },
          { "Number": 12, "Name": "Gem", "ItemType": 2 }
        ]
        """;

    private const string SpellsJson = """
        [
          { "Number": 200, "Name": "Open Wooden Chest", "Abil-0": 148, "AbilVal-0": 500 }
        ]
        """;

    private const string TBInfoJson = """
        [
          { "Number": 500, "LinkTo": 0,
            "Action": "message 1:giveitem 10:random 501:random 501\n" },
          { "Number": 501, "LinkTo": 0,
            "Action": "50:giveitem 11\n80:random 502\n100:failitem 999\n" },
          { "Number": 502, "LinkTo": 0,
            "Action": "100:giveitem 12\n" }
        ]
        """;

    private GameDataCache NewCache()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), ItemsJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "Spells.json"), SpellsJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"), TBInfoJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        return cache;
    }

    [Fact]
    public void Read_NonContainer_ReturnsNull()
    {
        ChestContents? contents = ChestContentsReader.Read(NewCache(), 10);
        Assert.Null(contents);
    }

    [Fact]
    public void Read_MissingItem_ReturnsNull()
    {
        ChestContents? contents = ChestContentsReader.Read(NewCache(), 999999);
        Assert.Null(contents);
    }

    [Fact]
    public void Read_Chest_ResolvesEveryDrop()
    {
        ChestContents? contents = ChestContentsReader.Read(NewCache(), 100);
        Assert.NotNull(contents);
        Assert.Equal(3, contents!.Drops.Count);
        Assert.Equal(
            new[] { "Gold Ring", "Silver Ring", "Gem" },
            contents.Drops.Select(d => d.ItemName).ToArray());
    }

    [Fact]
    public void Read_Chest_ComputesAtLeastOnceProbabilities()
    {
        ChestContents contents = ChestContentsReader.Read(NewCache(), 100)!;
        ChestDrop gold = contents.Drops.Single(d => d.ItemName == "Gold Ring");
        ChestDrop silver = contents.Drops.Single(d => d.ItemName == "Silver Ring");
        ChestDrop gem = contents.Drops.Single(d => d.ItemName == "Gem");

        Assert.Equal(1.00, gold.Probability, 3);
        Assert.Equal(0.75, silver.Probability, 3);
        Assert.Equal(0.51, gem.Probability, 3);
    }

    [Fact]
    public void Read_Chest_ComputesMinMaxItemCount()
    {
        ChestContents contents = ChestContentsReader.Read(NewCache(), 100)!;
        Assert.Equal(1, contents.MinItems);
        Assert.Equal(3, contents.MaxItems);
    }
}
