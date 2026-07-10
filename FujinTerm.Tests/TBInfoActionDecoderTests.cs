using System.Collections.Generic;
using System.IO;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// Pins the greet-textblock decoder (a port of MegaMUD Explorer's command
// decoder) against a hand-built TBInfo fixture: keyword grouping, the
// nada→LinkTo hop, the directive table (item/addexp/cast/cost/teleport),
// weighted-random delta percents, checkspell recursion, and loop detection.
public sealed class TBInfoActionDecoderTests : IDisposable
{
    private readonly string _root;

    public TBInfoActionDecoderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-tbdecode-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string TBInfoJson = """
        [
          { "Number": 100, "LinkTo": 0,
            "Action": "box:200\npackage:200\ngift:300\nchance:400\ntravel:500\nloop:600\n" },
          { "Number": 200, "LinkTo": 210, "Action": "" },
          { "Number": 210, "LinkTo": 0,
            "Action": "takeitem 50:addexp 1000000:cast 10:text 999\n" },
          { "Number": 300, "LinkTo": 0, "Action": "additem 50:price 500g\n" },
          { "Number": 400, "LinkTo": 0, "Action": "random 410\n" },
          { "Number": 410, "LinkTo": 0, "Action": "50:cast 10\n100:teleport 5 2\n" },
          { "Number": 500, "LinkTo": 0, "Action": "teleport 7 3\n" },
          { "Number": 600, "LinkTo": 0, "Action": "checkspell 5 600\n" }
        ]
        """;

    private const string SpellsJson = """
        [ { "Number": 10, "Name": "fireball" }, { "Number": 5, "Name": "heal" } ]
        """;

    private const string ItemsJson = """
        [ { "Number": 50, "Name": "golden box" } ]
        """;

    private (TBInfoStore Store, GameDataCache Cache) NewFixture()
    {
        string dir = Path.Combine(_root, "alpha");
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), TBInfoJson);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), SpellsJson);
        File.WriteAllText(Path.Combine(dir, "Items.json"), ItemsJson);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore store = new(cache);
        store.OnActiveSetChanged("alpha");
        return (store, cache);
    }

    private List<string> Decode(int greet)
    {
        (TBInfoStore store, GameDataCache cache) = NewFixture();
        return TBInfoActionDecoder.DecodeGreet(store, cache, rooms: null, greet)
            .Select(l => $"{l.Depth}:{l.Text}")
            .ToList();
    }

    [Fact]
    public void UnknownGreet_ReturnsEmpty()
    {
        Assert.Empty(Decode(0));
        Assert.Empty(Decode(9999));
    }

    [Fact]
    public void SynonymKeywords_ShareOnePointer_GroupedWithOr()
    {
        List<string> lines = Decode(100);
        // box and package both point at block 200 → one grouped keyword line.
        Assert.Contains("0:box OR package", lines);
        Assert.DoesNotContain("0:package", lines);
    }

    [Fact]
    public void NadaBlock_FollowsLinkTo_ToDirectiveBlock()
    {
        List<string> lines = Decode(100);
        // block 200 has no Action → follows LinkTo 210, whose directives appear
        // one level under the keyword.
        Assert.Contains("1:Item, take: golden box", lines);
    }

    [Fact]
    public void DirectiveTable_ResolvesNamesAndFormats()
    {
        List<string> lines = Decode(100);
        Assert.Contains("1:Item, take: golden box", lines); // item id → name, verb prefix
        Assert.Contains("1:AddExp: 1,000,000", lines);      // thousands separators
        Assert.Contains("1:Cast: fireball", lines);         // spell id → name
        Assert.Contains("1:text 999", lines);               // unrecognised → raw
        Assert.Contains("1:Item, add: golden box", lines);
        Assert.Contains("1:Cost: 500 gold", lines);         // coin suffix from trailing 'g'
    }

    [Fact]
    public void Teleport_ParsesRoomThenMap()
    {
        List<string> lines = Decode(100);
        // `teleport 7 3` → room 7, map 3 → shown map/room without a room graph.
        Assert.Contains("1:Teleport: 3/7", lines);
    }

    [Fact]
    public void RandomBlock_EmitsDeltaPercentsThenDirectives()
    {
        List<string> lines = Decode(100);
        Assert.Contains("1:random: 410", lines);
        // cumulative weights 50 then 100 → per-outcome deltas 50% / 50%.
        Assert.Contains("2:50%", lines);
        Assert.Contains("3:Cast: fireball", lines);
        Assert.Contains("3:Teleport: 2/5", lines);
    }

    [Fact]
    public void Checkspell_RecursesAndDetectsLoop()
    {
        List<string> lines = Decode(100);
        Assert.Contains("1:Checkspell: heal", lines);
        Assert.Contains("2:(loop → textblock 600)", lines);
    }
}
