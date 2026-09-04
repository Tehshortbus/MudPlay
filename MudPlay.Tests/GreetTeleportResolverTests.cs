using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Decoding a placed monster's greet chain into the `ask <noun> <keyword>`
// command that TRANSPORTS the player out of the room. Fixture mirrors the
// Floating Citadel's Grey Lord: greet 366 exposes an UNGATED `teleport` topic
// (→ block 370, a bare `teleport 224 1`) and a quest-GATED `code`/`word` topic
// (→ block 371, alignment + checkability + giveability gates before the same
// teleport). Only the ungated topic is synthesised into a routable edge.
public sealed class GreetTeleportResolverTests : IDisposable
{
    private readonly string _root;

    public GreetTeleportResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-greetteleport-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private TBInfoStore NewStore(string json)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore store = new(cache);
        store.OnActiveSetChanged("alpha");
        return store;
    }

    // The Grey Lord's greet: an ungated `teleport` topic and a quest-gated
    // `code`/`word` topic (both land in 1/224), plus a `return` topic that only
    // prints a message (no teleport directive at all).
    private const string GreyLordTbInfo = """
        [
          { "Number": 366, "LinkTo": 0,
            "Action": "teleport:369\ncode:368\nword:368\nreturn:382\n",
            "Called From": "Monster #251" },
          { "Number": 369, "LinkTo": 370, "Action": null, "Called From": "" },
          { "Number": 370, "LinkTo": 0, "Action": "teleport 224 1:message 837\n", "Called From": "" },
          { "Number": 368, "LinkTo": 371, "Action": null, "Called From": "" },
          { "Number": 371, "LinkTo": 0,
            "Action": "evilaligned -50 839:goodaligned 29 839:checkability 127 2:giveability 127 3:teleport 224 1:message 837\n",
            "Called From": "" },
          { "Number": 382, "LinkTo": 0, "Action": "message 900\n", "Called From": "" }
        ]
        """;

    [Fact]
    public void Resolve_GreyLord_YieldsOnlyTheUngatedTeleport()
    {
        TBInfoStore store = NewStore(GreyLordTbInfo);

        var teleports = GreetTeleportResolver
            .Resolve(store, greetNumber: 366, monsterName: "The Grey Lord")
            .ToList();

        // The gated code/word topics are skipped; the return topic has no teleport
        // directive — so only the ungated `teleport` keyword survives.
        Assert.Single(teleports);
        Assert.Equal("ask Lord teleport", teleports[0].Command);   // noun = last word of the name
        Assert.Equal(new RoomKey(1, 224), teleports[0].Destination); // teleport <room> <map> → (map, room)
        Assert.Equal(0, teleports[0].MinLevel);
        Assert.Equal(0, teleports[0].RequiredClass);   // ungated by class
    }

    [Fact]
    public void Resolve_ClassGatedTeleport_IsKeptWithRequiredClass()
    {
        // The barmaid's bard-only "adventure" teleport (issue #455): a `class N`
        // gate restricts the transport to one class. It is NOT skipped like an
        // alignment/ability gate — it's surfaced with RequiredClass so the edge's
        // ClassGate keeps it for that class and drops it for every other. The
        // `testskill` attribute-roll token in the same block must NOT abort it
        // (it's not one of the untrackable gates).
        const string tbinfo = """
            [
              { "Number": 40, "LinkTo": 0, "Action": "adventure:41\n", "Called From": "" },
              { "Number": 41, "LinkTo": 0,
                "Action": "class 12:testskill 5 50 999:teleport 391 1:message 1\n", "Called From": "" }
            ]
            """;
        TBInfoStore store = NewStore(tbinfo);

        var teleports = GreetTeleportResolver.Resolve(store, 40, "the barmaid").ToList();
        Assert.Single(teleports);
        Assert.Equal("ask barmaid adventure", teleports[0].Command);
        Assert.Equal(new RoomKey(1, 391), teleports[0].Destination);
        Assert.Equal(12, teleports[0].RequiredClass);   // class 12 → surfaced, not skipped
        Assert.Equal(0, teleports[0].MinLevel);
    }

    [Fact]
    public void Resolve_GatedTeleport_IsSkipped()
    {
        // A greet whose ONLY teleport topic is alignment/ability gated yields
        // nothing — the client can't verify the gate, so it must not be routed.
        const string tbinfo = """
            [
              { "Number": 10, "LinkTo": 0, "Action": "warp:11\n", "Called From": "" },
              { "Number": 11, "LinkTo": 0,
                "Action": "checkability 200 3:teleport 50 2:message 1\n", "Called From": "" }
            ]
            """;
        TBInfoStore store = NewStore(tbinfo);
        Assert.Empty(GreetTeleportResolver.Resolve(store, 10, "portal warden"));
    }

    [Fact]
    public void Resolve_MinLevelGatedTeleport_IsKeptWithLevelFloor()
    {
        // A `minlevel` gate is NOT an untrackable gate — BFS honours a level floor
        // on the edge — so it's surfaced, not skipped.
        const string tbinfo = """
            [
              { "Number": 20, "LinkTo": 0, "Action": "chime:21\n", "Called From": "" },
              { "Number": 21, "LinkTo": 0,
                "Action": "minlevel 20 999:teleport 500 2:message 1\n", "Called From": "" }
            ]
            """;
        TBInfoStore store = NewStore(tbinfo);

        var teleports = GreetTeleportResolver.Resolve(store, 20, "old sage").ToList();
        Assert.Single(teleports);
        Assert.Equal("ask sage chime", teleports[0].Command);
        Assert.Equal(new RoomKey(2, 500), teleports[0].Destination);
        Assert.Equal(20, teleports[0].MinLevel);
    }

    [Fact]
    public void Resolve_TopicWithoutTeleport_YieldsNothing()
    {
        const string tbinfo = """
            [
              { "Number": 30, "LinkTo": 0, "Action": "rumor:31\n", "Called From": "" },
              { "Number": 31, "LinkTo": 0, "Action": "message 42\n", "Called From": "" }
            ]
            """;
        TBInfoStore store = NewStore(tbinfo);
        Assert.Empty(GreetTeleportResolver.Resolve(store, 30, "gossip"));
    }

    [Fact]
    public void Resolve_MissingGreetOrName_ReturnsEmpty()
    {
        TBInfoStore store = NewStore(GreyLordTbInfo);
        Assert.Empty(GreetTeleportResolver.Resolve(store, 0, "The Grey Lord"));
        Assert.Empty(GreetTeleportResolver.Resolve(store, 9999, "The Grey Lord"));
        Assert.Empty(GreetTeleportResolver.Resolve(store, 366, null));
        Assert.Empty(GreetTeleportResolver.Resolve(store, 366, "  "));
    }
}
