using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Decoding a placed monster's greet chain into the ask commands that
// open a door on its own room. Fixture mirrors the grove shadow guard: greet
// 1433 lists ask-topics pointing at 1435 (empty → LinkTo 1436), whose
// checkability 133 4:remoteaction 1423 66 0 3 opens the W exit of room
// 1423 for a Phoenix-quest character.
public sealed class GuardDoorCommandResolverTests : IDisposable
{
    private readonly string _root;

    public GuardDoorCommandResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-guarddoor-tests-" + Path.GetRandomFileName());
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

    // The grove shadow guard's greet chain.
    private const string GuardTbInfo = """
        [
          { "Number": 1433, "LinkTo": 0,
            "Action": "morukai:1435\norfeo:1435\npassage:1435\nphoenix:1435\nprophecy:1435\n",
            "Called From": "Monster #503" },
          { "Number": 1435, "LinkTo": 1436, "Action": null, "Called From": "" },
          { "Number": 1436, "LinkTo": 0,
            "Action": "checkability 133 4:remoteaction 1423 66 0 3:message 1841\n",
            "Called From": "" }
        ]
        """;

    [Fact]
    public void Resolve_GuardGreet_YieldsAskCommandForEveryTopic()
    {
        TBInfoStore store = NewStore(GuardTbInfo);

        var cmds = GuardDoorCommandResolver
            .Resolve(store, greetNumber: 1433, monsterName: "shadow guard", hostRoomNumber: 1423)
            .ToList();

        // Five topics, all opening the same W door, gated on PhoenixQuest (133).
        Assert.Equal(5, cmds.Count);
        Assert.All(cmds, c => Assert.Equal(Direction.W, c.Direction));
        Assert.All(cmds, c => Assert.Equal(133, c.AbilityGate));

        // Each command asks the guard, by its full name, about one topic.
        Assert.Contains(cmds, c => c.Command == "ask shadow guard morukai");
        Assert.Contains(cmds, c => c.Command == "ask shadow guard orfeo");
        Assert.Contains(cmds, c => c.Command == "ask shadow guard passage");
        Assert.Contains(cmds, c => c.Command == "ask shadow guard phoenix");
        Assert.Contains(cmds, c => c.Command == "ask shadow guard prophecy");
    }

    [Fact]
    public void Resolve_RemoteActionTargetsDifferentRoom_NoMatch()
    {
        // The greet's remoteaction targets room 1423; asking about a monster
        // whose host room is elsewhere yields nothing (same-room guard only).
        TBInfoStore store = NewStore(GuardTbInfo);

        var cmds = GuardDoorCommandResolver
            .Resolve(store, 1433, "shadow guard", hostRoomNumber: 9999)
            .ToList();

        Assert.Empty(cmds);
    }

    [Fact]
    public void Resolve_SingleWordMonsterName_UsesWholeNameAsNoun()
    {
        TBInfoStore store = NewStore(GuardTbInfo);

        var cmds = GuardDoorCommandResolver
            .Resolve(store, 1433, "sentinel", hostRoomNumber: 1423)
            .ToList();

        Assert.NotEmpty(cmds);
        Assert.All(cmds, c => Assert.StartsWith("ask sentinel ", c.Command));
    }

    [Fact]
    public void Resolve_MissingGreet_ReturnsEmpty()
    {
        TBInfoStore store = NewStore(GuardTbInfo);
        Assert.Empty(GuardDoorCommandResolver.Resolve(store, 0, "shadow guard", 1423));
        Assert.Empty(GuardDoorCommandResolver.Resolve(store, 9999, "shadow guard", 1423));
    }

    [Fact]
    public void Resolve_NoMonsterName_ReturnsEmpty()
    {
        TBInfoStore store = NewStore(GuardTbInfo);
        Assert.Empty(GuardDoorCommandResolver.Resolve(store, 1433, null, 1423));
        Assert.Empty(GuardDoorCommandResolver.Resolve(store, 1433, "  ", 1423));
    }

    [Fact]
    public void Resolve_GreetWithNoDoorOpener_ReturnsEmpty()
    {
        // A pure-dialogue greet (keyword points at a block with no remoteaction)
        // exposes no door command.
        const string tbinfo = """
            [
              { "Number": 200, "LinkTo": 0, "Action": "rumor:201\n", "Called From": "" },
              { "Number": 201, "LinkTo": 0, "Action": "message 42\n", "Called From": "" }
            ]
            """;
        TBInfoStore store = NewStore(tbinfo);
        Assert.Empty(GuardDoorCommandResolver.Resolve(store, 200, "old sage", 1423));
    }

    [Fact]
    public void Resolve_PureDialogueGreetLinksToKeywordBlock()
    {
        // Greet block itself is dialogue-only (empty Action) and hangs the real
        // keyword list off LinkTo — the resolver follows the hop.
        const string tbinfo = """
            [
              { "Number": 300, "LinkTo": 301, "Action": null, "Called From": "" },
              { "Number": 301, "LinkTo": 0, "Action": "open:302\n", "Called From": "" },
              { "Number": 302, "LinkTo": 0,
                "Action": "remoteaction 1500 5 0 1:message 9\n", "Called From": "" }
            ]
            """;
        TBInfoStore store = NewStore(tbinfo);

        var cmds = GuardDoorCommandResolver.Resolve(store, 300, "gate keeper", 1500).ToList();
        Assert.Single(cmds);
        Assert.Equal("ask gate keeper open", cmds[0].Command);
        Assert.Equal(Direction.S, cmds[0].Direction);   // dir index 1 → S
        Assert.Equal(0, cmds[0].AbilityGate);            // no checkability
    }

    // The graph build reads a greet once and asks it about every room its monster
    // stands in. One reading has to answer each room as a fresh Resolve would: a
    // keyword's first exit in that room, with the ability gate seen before it.
    [Fact]
    public void Read_AnswersEachRoom_AsResolveDoes()
    {
        const string tbinfo = """
            [
              { "Number": 400, "LinkTo": 0, "Action": "gate:401\nvault:402\nweather:403\n", "Called From": "" },
              { "Number": 401, "LinkTo": 0,
                "Action": "remoteaction 10 5 0 0:checkability 50 1:remoteaction 20 5 0 1:remoteaction 20 5 0 2\n",
                "Called From": "" },
              { "Number": 402, "LinkTo": 0,
                "Action": "checkability 60 1:remoteaction 20 5 0 3\nremoteaction 30 5 0 8\n", "Called From": "" },
              { "Number": 403, "LinkTo": 0, "Action": "message 9\n", "Called From": "" }
            ]
            """;
        TBInfoStore store = NewStore(tbinfo);

        GuardDoorCommandResolver.GreetDoors doors = GuardDoorCommandResolver.Read(store, 400, "the warden");
        Assert.False(doors.IsEmpty);

        foreach (int room in new[] { 10, 20, 30, 40, 0 })
            Assert.Equal(
                GuardDoorCommandResolver.Resolve(store, 400, "the warden", room).ToList(),
                doors.For(room).ToList());

        Assert.Equal(
            new[] { new GuardDoorCommandResolver.GuardDoorCommand("ask warden gate", Direction.N, 0) },
            doors.For(10));
        // Room 20: `gate` opens S (its first exit there, behind ability 50), `vault` opens W (behind 60).
        Assert.Equal(
            new[]
            {
                new GuardDoorCommandResolver.GuardDoorCommand("ask warden gate", Direction.S, 50),
                new GuardDoorCommandResolver.GuardDoorCommand("ask warden vault", Direction.W, 60),
            },
            doors.For(20));
        // The ability gate carries on down the block: 60 still stands at room 30's line.
        Assert.Equal(
            new[] { new GuardDoorCommandResolver.GuardDoorCommand("ask warden vault", Direction.U, 60) },
            doors.For(30));
        Assert.Empty(doors.For(40));

        // A greet that only talks opens nothing anywhere.
        Assert.True(GuardDoorCommandResolver.Read(store, 403, "the warden").IsEmpty);
    }
}
