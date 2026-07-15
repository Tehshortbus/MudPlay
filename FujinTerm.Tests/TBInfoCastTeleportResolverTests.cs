using System.IO;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Game.Spells;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class TBInfoCastTeleportResolverTests : IDisposable
{
    private readonly string _root;

    public TBInfoCastTeleportResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-castteleport-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private (TBInfoStore Store, KnownSpellCatalog Catalog) NewSet(string tbinfoJson, string spellsJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"), tbinfoJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "Spells.json"), spellsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore store = new(cache);
        store.OnActiveSetChanged("alpha");
        return (store, new KnownSpellCatalog(cache));
    }

    // v1.11p map 1 rooms 178-180, CMD 9115 → spell 923 "bridge jump":
    // Abil-0 140 (TeleportRoom) AbilVal-0 0 (random), Abil-1 141
    // (TeleportMap) AbilVal-1 1, MinBase 2590, MaxBase 2594.
    private const string BridgeJumpTbInfo = """
        [ { "Number": 9115, "LinkTo": 0,
            "Action": "jump west:message 2664:cast 923\njump east:message 2664:cast 923\n",
            "Called From": "Room 1/178, Room 1/179, Room 1/180" } ]
        """;
    private const string BridgeJumpSpells = """
        [ { "Number": 923, "Name": "bridge jump", "Short": "bjump",
            "MinBase": 2590, "MaxBase": 2594,
            "Abil-0": 140, "AbilVal-0": 0,
            "Abil-1": 141, "AbilVal-1": 1,
            "Abil-2": 120, "AbilVal-2": 2798 } ]
        """;

    [Fact]
    public void RandomRangeCast_ExpandsEveryLandingRoom()
    {
        (TBInfoStore store, KnownSpellCatalog catalog) = NewSet(BridgeJumpTbInfo, BridgeJumpSpells);

        var results = TBInfoCastTeleportResolver
            .EnumerateCastTeleports(store, 9115, sourceMap: 1, catalog)
            .ToList();

        Assert.Equal(2, results.Count);                       // jump west + jump east
        Assert.Equal("jump west", results[0].Keyword);
        Assert.Equal("jump east", results[1].Keyword);

        foreach (var r in results)
        {
            Assert.True(r.Random);
            Assert.Equal(0, r.MinLevel);
            Assert.Equal(
                new[]
                {
                    new RoomKey(1, 2590), new RoomKey(1, 2591), new RoomKey(1, 2592),
                    new RoomKey(1, 2593), new RoomKey(1, 2594),
                },
                r.Destinations);
        }
    }

    [Fact]
    public void FixedRoomCast_ReturnsSingleDestination_NotRandom()
    {
        const string tbinfo = """
            [ { "Number": 50, "LinkTo": 0,
                "Action": "step portal:cast 200\n", "Called From": "Room 1/5" } ]
            """;
        // AbilVal-140 = 487 (fixed room), AbilVal-141 = 2 (map 2).
        const string spells = """
            [ { "Number": 200, "Name": "portal", "Short": "port",
                "MinBase": 0, "MaxBase": 0,
                "Abil-0": 140, "AbilVal-0": 487,
                "Abil-1": 141, "AbilVal-1": 2 } ]
            """;
        (TBInfoStore store, KnownSpellCatalog catalog) = NewSet(tbinfo, spells);

        var r = Assert.Single(
            TBInfoCastTeleportResolver.EnumerateCastTeleports(store, 50, sourceMap: 1, catalog));
        Assert.Equal("step portal", r.Keyword);
        Assert.False(r.Random);
        Assert.Equal(new RoomKey(2, 487), Assert.Single(r.Destinations));
    }

    [Fact]
    public void NoTeleportMapAbility_DefaultsToSourceMap()
    {
        const string tbinfo = """
            [ { "Number": 60, "LinkTo": 0,
                "Action": "blink:cast 300\n", "Called From": "Room 7/9" } ]
            """;
        // No Abil 141 — destination map should fall back to the room's map.
        const string spells = """
            [ { "Number": 300, "Name": "blink", "Short": "blnk",
                "MinBase": 0, "MaxBase": 0,
                "Abil-0": 140, "AbilVal-0": 42 } ]
            """;
        (TBInfoStore store, KnownSpellCatalog catalog) = NewSet(tbinfo, spells);

        var r = Assert.Single(
            TBInfoCastTeleportResolver.EnumerateCastTeleports(store, 60, sourceMap: 7, catalog));
        Assert.Equal(new RoomKey(7, 42), Assert.Single(r.Destinations));
    }

    [Fact]
    public void CastSpellWithoutTeleportAbility_IsSkipped()
    {
        const string tbinfo = """
            [ { "Number": 70, "LinkTo": 0,
                "Action": "pray:cast 400\n", "Called From": "Room 1/1" } ]
            """;
        // Healing spell — no Abil 140, so it is not a teleport command.
        const string spells = """
            [ { "Number": 400, "Name": "heal", "Short": "heal",
                "MinBase": 30, "MaxBase": 45,
                "Abil-0": 2, "AbilVal-0": 0 } ]
            """;
        (TBInfoStore store, KnownSpellCatalog catalog) = NewSet(tbinfo, spells);

        Assert.Empty(TBInfoCastTeleportResolver.EnumerateCastTeleports(store, 70, sourceMap: 1, catalog));
    }

    [Fact]
    public void MinLevelDirective_SurfacedOnTuple()
    {
        const string tbinfo = """
            [ { "Number": 80, "LinkTo": 0,
                "Action": "leap:minlevel 15 1220:cast 200\n", "Called From": "Room 1/5" } ]
            """;
        const string spells = """
            [ { "Number": 200, "Name": "portal", "Short": "port",
                "Abil-0": 140, "AbilVal-0": 487, "Abil-1": 141, "AbilVal-1": 2 } ]
            """;
        (TBInfoStore store, KnownSpellCatalog catalog) = NewSet(tbinfo, spells);

        var r = Assert.Single(
            TBInfoCastTeleportResolver.EnumerateCastTeleports(store, 80, sourceMap: 1, catalog));
        Assert.Equal(15, r.MinLevel);
    }

    [Fact]
    public void UnknownSpell_IsSkipped()
    {
        const string tbinfo = """
            [ { "Number": 90, "LinkTo": 0,
                "Action": "vanish:cast 99999\n", "Called From": "Room 1/1" } ]
            """;
        (TBInfoStore store, KnownSpellCatalog catalog) = NewSet(tbinfo, "[]");

        Assert.Empty(TBInfoCastTeleportResolver.EnumerateCastTeleports(store, 90, sourceMap: 1, catalog));
    }

    // ----- IsRandomTeleport (drives the map's cast-exit classification) -----

    [Fact]
    public void IsRandomTeleport_TrueForZeroRoomSpanningRange()
    {
        // AbilVal-140 = 0 (random), MinBase..MaxBase = 1183..1206 → 24 rooms.
        SpellFormulaInput spell = new()
        {
            MinBase = 1183,
            MaxBase = 1206,
            Abilities = new[] { new SpellAbility(140, 0), new SpellAbility(141, 9) },
        };
        Assert.True(TBInfoCastTeleportResolver.IsRandomTeleport(spell));
    }

    [Fact]
    public void IsRandomTeleport_FalseForFixedRoomTeleport()
    {
        // AbilVal-140 = 487 (a fixed destination) is deterministic.
        SpellFormulaInput spell = new()
        {
            MinBase = 0,
            MaxBase = 0,
            Abilities = new[] { new SpellAbility(140, 487), new SpellAbility(141, 2) },
        };
        Assert.False(TBInfoCastTeleportResolver.IsRandomTeleport(spell));
    }

    [Fact]
    public void IsRandomTeleport_FalseForSingleRoomRange()
    {
        // A degenerate "random" range of exactly one room is deterministic.
        SpellFormulaInput spell = new()
        {
            MinBase = 1200,
            MaxBase = 1200,
            Abilities = new[] { new SpellAbility(140, 0) },
        };
        Assert.False(TBInfoCastTeleportResolver.IsRandomTeleport(spell));
    }

    [Fact]
    public void IsRandomTeleport_FalseForNonTeleportSpell()
    {
        // No Abil 140 at all — a heal, buff, etc.
        SpellFormulaInput spell = new()
        {
            MinBase = 30,
            MaxBase = 45,
            Abilities = new[] { new SpellAbility(2, 0) },
        };
        Assert.False(TBInfoCastTeleportResolver.IsRandomTeleport(spell));
    }

    [Fact]
    public void IsRandomTeleport_FalseForImplausiblyWideRange()
    {
        // A span past the defensive ceiling is treated as a misparse, not a
        // real teleport, so it never trips the random classification.
        SpellFormulaInput spell = new()
        {
            MinBase = 1,
            MaxBase = 9999,
            Abilities = new[] { new SpellAbility(140, 0) },
        };
        Assert.False(TBInfoCastTeleportResolver.IsRandomTeleport(spell));
    }
}
