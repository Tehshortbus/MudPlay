using System;
using System.IO;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins the room-aware monster-name resolution that disambiguates a display name
// shared across zones to the record actually placed / summoned in the current
// room — the fix for the HP-lookup and per-monster spell-override picking the
// first same-named record regardless of zone.
public sealed class RoomAwareMonsterResolverTests : IDisposable
{
    private readonly string _root;

    public RoomAwareMonsterResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-roomaware-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // Two "orc lieutenant" records (barracks #101 vs slums #202) and two "zombie"
    // records (graveyard #303 placed vs tunnels #404). Necromancer #500 summons the
    // graveyard zombie #303 on death (DeathSpell 900 → Summon AbilVal 303).
    private const string MonstersJson = """
        [
          { "Number": 101, "Name": "orc lieutenant" },
          { "Number": 202, "Name": "orc lieutenant" },
          { "Number": 303, "Name": "zombie" },
          { "Number": 404, "Name": "zombie" },
          { "Number": 500, "Name": "necromancer", "DeathSpell": 900 }
        ]
        """;

    private const string SpellsJson = """
        [
          { "Number": 900, "Name": "raise dead", "Abil-0": 12, "AbilVal-0": 303 }
        ]
        """;

    // baseName stands in for the classifier's flavor-prefix stripping (tested
    // separately); identity by default so a raw name is its own base.
    private (RoomAwareMonsterResolver Resolver, GameDataCache Cache) NewResolver(
        Func<string, string?>? baseName = null)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Monsters.json"), MonstersJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "Spells.json"), SpellsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        var spawns = new MonsterSpawnIndex(cache);
        var summons = new MonsterSummonTargetsIndex(cache);
        // currentRoom is unused by ResolveInRoom (tests pass the room explicitly).
        var resolver = new RoomAwareMonsterResolver(
            cache, () => null, baseName ?? (s => s), spawns, summons);
        return (resolver, cache);
    }

    private static Room RoomWith(int npc = 0, string? lair = null) => new()
    {
        Key = new RoomKey(15, 2104),
        Name = "Orc Barracks",
        Npc = npc,
        RawLairTag = lair,
        Exits = RoomExitSet.Empty,
    };

    [Fact]
    public void PrefersTheLairMemberInThisRoom()
    {
        // Barracks lair holds #101; a look at "orc lieutenant" here resolves #101,
        // not the slums #202 that a first-match scan would return.
        (RoomAwareMonsterResolver r, _) = NewResolver();
        Room barracks = RoomWith(lair: "(Max 2): 101,[5]");
        Assert.Equal(101, r.ResolveInRoom(barracks, "orc lieutenant"));
    }

    [Fact]
    public void PrefersTheNpcFixtureInThisRoom()
    {
        // The graveyard zombie #303 is the room's NPC fixture; "zombie" → #303.
        (RoomAwareMonsterResolver r, _) = NewResolver();
        Room graveyard = RoomWith(npc: 303);
        Assert.Equal(303, r.ResolveInRoom(graveyard, "zombie"));
    }

    [Fact]
    public void WidensToASummonersMinion()
    {
        // The room places necromancer #500, which summons the graveyard zombie #303
        // on death. A "zombie" here resolves to #303 (the summoned record), not the
        // tunnels #404.
        (RoomAwareMonsterResolver r, _) = NewResolver();
        Room lair = RoomWith(npc: 500);
        Assert.Equal(303, r.ResolveInRoom(lair, "zombie"));
    }

    [Fact]
    public void ReturnsNullWhenNoRoomMonsterHasTheName()
    {
        // A wandering "goblin" isn't placed/summoned here → null, so the caller
        // falls back to its existing first-match behaviour.
        (RoomAwareMonsterResolver r, _) = NewResolver();
        Room barracks = RoomWith(lair: "(Max 2): 101,[5]");
        Assert.Null(r.ResolveInRoom(barracks, "goblin"));
    }

    [Fact]
    public void MatchIsCaseInsensitiveAndTrimmed()
    {
        (RoomAwareMonsterResolver r, _) = NewResolver();
        Room barracks = RoomWith(lair: "(Max 2): 101,[5]");
        Assert.Equal(101, r.ResolveInRoom(barracks, "  Orc Lieutenant  "));
    }

    [Fact]
    public void FlavorPrefixedNameMatchesTheRoomsBaseRecord()
    {
        // The game shows "short orc lieutenant" / "fierce orc lieutenant" — flavor
        // prefixes on the base record "orc lieutenant" (#101, in the lair). Once the
        // classifier strips the prefix to "orc lieutenant", both resolve to this
        // room's #101, not the slums #202.
        (RoomAwareMonsterResolver r, _) = NewResolver(
            baseName: n => n.EndsWith("orc lieutenant", StringComparison.OrdinalIgnoreCase)
                ? "orc lieutenant" : n);
        Room barracks = RoomWith(lair: "(Max 1): 101,[5]");
        Assert.Equal(101, r.ResolveInRoom(barracks, "short orc lieutenant"));
        Assert.Equal(101, r.ResolveInRoom(barracks, "fierce orc lieutenant"));
    }
}
