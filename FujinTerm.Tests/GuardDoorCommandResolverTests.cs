using System.IO;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Decoding a placed monster's greet chain into the <c>ask</c> commands that
/// open a door on its own room. Fixture mirrors the grove shadow guard: greet
/// 1433 lists ask-topics pointing at 1435 (empty → LinkTo 1436), whose
/// <c>checkability 133 4:remoteaction 1423 66 0 3</c> opens the W exit of room
/// 1423 for a Phoenix-quest character.
/// </summary>
public sealed class GuardDoorCommandResolverTests : IDisposable
{
    private readonly string _root;

    public GuardDoorCommandResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-guarddoor-tests-" + Path.GetRandomFileName());
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

        // The `ask` noun is the last word of the monster name.
        Assert.Contains(cmds, c => c.Command == "ask guard morukai");
        Assert.Contains(cmds, c => c.Command == "ask guard orfeo");
        Assert.Contains(cmds, c => c.Command == "ask guard passage");
        Assert.Contains(cmds, c => c.Command == "ask guard phoenix");
        Assert.Contains(cmds, c => c.Command == "ask guard prophecy");
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
        Assert.Equal("ask keeper open", cmds[0].Command);
        Assert.Equal(Direction.S, cmds[0].Direction);   // dir index 1 → S
        Assert.Equal(0, cmds[0].AbilityGate);            // no checkability
    }
}
