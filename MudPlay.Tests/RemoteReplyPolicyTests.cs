using System.IO;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MudPlay.Game;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// The remote-command reply policy, across the handlers: with "Warn sender on
// invalid / denied remote command" off, a command that was not carried out is
// answered with nothing; with it on, the answer goes out on the channel the
// command came in on. The replies another MudPlay client reads to drive its own
// behaviour are the documented exception, and go either way.
public sealed class RemoteReplyPolicyTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Sender = "Friend";

    private readonly string _root;

    public RemoteReplyPolicyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-reply-policy-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1 ↔ 1/2 ↔ 1/3, and 1/9 that nothing leads to.
    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Island",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed class Rig : IDisposable
    {
        public required RemoteCommandManager Engine { get; init; }
        // The router the engine's chat lines arrive through.
        public required MessageRouter Router { get; init; }
        // What Engine put on the wire, through the sender the client binds.
        public List<byte[]> Replies { get; } = new();
        public required RoomTracker Tracker { get; init; }
        public required MovementController Controller { get; init; }
        public required AutoLairManager AutoLair { get; init; }
        public required LairTimerStore Timers { get; init; }
        // Everything any handler sent to the game.
        public required List<string> Game { get; init; }

        public void Dispose()
        {
            Controller.Dispose();
            AutoLair.Dispose();
            Timers.Dispose();
        }
    }

    // One engine with the handlers a party member's command can fail on. The
    // character is fresh: no prompt, no stat screen and no inventory read, no
    // room, no saved loop, following someone who isn't the sender.
    private Rig Build(int traps = 50)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        List<string> game = new();
        void ToGame(byte[] bytes) => game.Add(Encoding.Latin1.GetString(bytes));
        AutoWalkManager walker = new(graph, bfs, tracker, coord);
        walker.SetWireSender(ToGame);
        LoopRunner loopRunner = new(tracker, coord, graph: graph, bfs: bfs);
        loopRunner.SetWireSender(ToGame);
        LoopManager loops = new(bfs, graph);
        LairManager lairs = new();
        LairTimerStore timers = new(cache, graph, tracker);
        AutoLairManager autoLair = new(walker, tracker, graph, bfs, timers, log: null, coordinator: coord);
        MovementController controller = new(walker, loopRunner, autoLair, coord);
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        FavoritesStore favorites = new(profile, cache, () => cache.ActiveSet);
        BossStore bosses = new();
        RoomSearchService search = new(graph, cache, bfs, new RoomBlacklistStore(), favorites: favorites, bosses: bosses);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState party = new() { IsInParty = true, SelfIsLeader = false, LeaderName = "Boss" };
        party.Members.Add(new PartyMember { Name = "Boss", IsLeader = true });
        party.Members.Add(new PartyMember { Name = "Me", IsSelf = true });
        party.Members.Add(new PartyMember { Name = Sender });
        PlayerDatabase players = new(new ProfileService(), () => null);
        players.RecordObservation(Sender, @class: null, race: null, alignment: null,
            title: null, gang: null, role: null, nowUtc: Now);
        players.EditCustomization(Sender, new PlayerCustomization(RemoteControls: PlayerRemoteControls.All));
        RemoteCommandManager engine = new(new ChatRouter(router), party, players);

        _ = new MovePlayerHandler(engine, search, graph, tracker, walker, loops, loopRunner,
            lairs, autoLair, coord, controller, favorites, bosses, bfs, new LoopShareHandler(loops));

        InventoryManager inventory = new(log: null, itemWeightResolver: null, slotResolver: null);
        inventory.AttachLineExtractor(new LineExtractor(new TerminalEmulator(80, 24)));
        GroundItemTracker ground = new(router, new CurrencyNaming());
        _ = new InventoryQueryHandler(engine, inventory, ground);
        CashSettings cash = new();
        InventoryActionHandler actions = new(engine, inventory, ground, party, () => cash, new CurrencyNaming());
        actions.SetWireSender(ToGame);

        PartyEssentialHandlers essentials = new(engine, new PlayerState(), party);
        essentials.SetWireSender(ToGame);
        _ = new HelpHandler(engine);
        _ = new KillHandler(engine, _ => { game.Add("(retarget)"); return null; });
        _ = new AttackTargetingRemoteHandler(engine, TestProfiles.Service());
        _ = new AutoModeRemoteHandler(engine, profile, new AutoModeController(profile));
        TrapDisarmManager trapManager = new(router, new PlayerStats { Traps = traps }, cache);
        trapManager.SetWireSender(ToGame);
        _ = new TrapHandler(engine, trapManager);
        GhRoomLabelStore labels = new(profile);
        _ = new RoombaQueryHandler(engine, new GhItemLocationStore(new ItemNameStore(cache)), labels);
        _ = new GetStashHandler(engine,
            onHandCopper: () => 0, send: game.Add, armTimer: (_, _) => { },
            limitCollection: _ => { }, surveyedCopper: () => 0, collectSurveyed: _ => { },
            forceAutoGetCash: _ => { }, collectionInUse: () => true);

        Rig rig = new()
        {
            Engine = engine, Router = router, Tracker = tracker, Controller = controller,
            AutoLair = autoLair, Timers = timers, Game = game,
        };
        engine.SetWireSender(rig.Replies.Add);
        return rig;
    }

    private static ChatLine On(ChatChannel channel, string msg) =>
        new(channel, Sender, msg);

    private static List<string> Sent(Rig r) =>
        r.Replies.Select(b => Encoding.Latin1.GetString(b)).ToList();

    // Each line is a command the fresh character of Build can't carry out.
    public static TheoryData<string> Failures => new()
    {
        "@goto",
        "@goto 599",
        "@goto no such place anywhere",
        "@goto 1/9",
        "@loop",
        "@loop last",
        "@loop no such loop",
        "@loop send",
        "@loop send no such loop",
        "@loop send yes",
        "@loop send no",
        "@lair",
        "@lair no such setup",
        "@rego",
        "@health",
        "@status",
        "@lives",
        "@where",
        "@invite",
        "@join",
        "@enc",
        "@inv",
        "@have",
        "@have rope",
        "@drop-all",
        "@drop-all sideways",
        "@hide-all",
        "@hide-all full coins",
        "@share",
        "@help nosuchcommand",
        "@kill",
        "@atkprio 9",
        "@atkorder first",
        "@auto-combat maybe",
        "@auto-all maybe",
        "@trap",
        "@trap sideways",
        "@roomba",
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void WarnOff_AFailedCommand_IsAnsweredWithNothing(string command)
    {
        using Rig r = Build();
        r.Engine.WarnOnDenial = false;

        ChatLines.Hear(r.Router, On(ChatChannel.TelepathIncoming, command));

        Assert.Empty(Sent(r));
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public void WarnOn_AFailedCommand_IsAnsweredOnTheChannelItCameInOn(string command)
    {
        foreach ((ChatChannel channel, string prefix) in new[]
        {
            (ChatChannel.TelepathIncoming, $"/{Sender} {{"),
            (ChatChannel.Gangpath, "bg {"),
            (ChatChannel.Local, $">{Sender} {{"),
        })
        {
            using Rig r = Build();

            ChatLines.Hear(r.Router, On(channel, command));

            List<string> sent = Sent(r);
            Assert.NotEmpty(sent);
            Assert.All(sent, line => Assert.StartsWith(prefix, line));
            // And in ASCII: a long dash can't be encoded on the wire at all.
            Assert.All(sent, line => Assert.All(line.TrimEnd('\r'), c => Assert.InRange(c, ' ', '~')));
        }
    }

    // The second of two @stops is an answer: the sender wanted us stopped, and
    // we are.
    [Fact]
    public void WarnOff_ARepeatedStop_IsStillAnswered()
    {
        using Rig r = Build();
        ChatLines.Hear(r.Router, On(ChatChannel.TelepathIncoming, "@stop"));
        r.Replies.Clear();
        r.Engine.WarnOnDenial = false;

        ChatLines.Hear(r.Router, On(ChatChannel.TelepathIncoming, "@stop"));

        Assert.Equal($"/{Sender} {{already @stopped}}\r", Assert.Single(Sent(r)));
    }

    // A disarm asked of a character with no Traps skill needs a state the fresh
    // character doesn't start in.
    [Fact]
    public void WarnOff_ADisarmAskedOfACharacterWithoutTheSkill_IsAnsweredWithNothing()
    {
        using Rig r = Build(traps: 0);
        r.Engine.WarnOnDenial = false;

        ChatLines.Hear(r.Router, On(ChatChannel.TelepathIncoming, "@trap n"));

        Assert.Empty(Sent(r));
        Assert.Empty(r.Game);
    }

    // A failed command sends nothing to the game either.
    [Theory]
    [MemberData(nameof(Failures))]
    public void AFailedCommand_SendsNothingToTheGame(string command)
    {
        using Rig r = Build();

        ChatLines.Hear(r.Router, On(ChatChannel.TelepathIncoming, command));

        Assert.Empty(r.Game);
    }

    // What another MudPlay client reads goes whatever the Warn setting says: a
    // leader's wealth probe counts "wealth unknown" as an answer, and its stash
    // transfer waits on a member's "busy" and on its @deposit-all answer.
    [Theory]
    [InlineData("@wealth", "wealth unknown")]
    [InlineData("@get-stash", "busy")]
    [InlineData("@deposit-all", "wealth unknown")]
    public void WarnOff_AnAnswerAnotherClientReads_IsStillSent(string command, string opening)
    {
        using Rig r = Build();
        r.Engine.WarnOnDenial = false;

        ChatLines.Hear(r.Router, On(ChatChannel.TelepathIncoming, command));

        Assert.StartsWith($"/{Sender} {{{opening}", Assert.Single(Sent(r)));
    }

    // An answer that is the result of a query that was carried out is not a
    // refusal, however empty the result.
    [Theory]
    [InlineData("@who", "no one")]
    [InlineData("@what", "nothing on the ground here")]
    [InlineData("@path", "not moving")]
    [InlineData("@roomba severed head", "no record of")]
    public void WarnOff_AnEmptyResult_IsStillAnAnswer(string command, string opening)
    {
        using Rig r = Build();
        r.Engine.WarnOnDenial = false;

        ChatLines.Hear(r.Router, On(ChatChannel.TelepathIncoming, command));

        Assert.StartsWith($"/{Sender} {{{opening}", Assert.Single(Sent(r)));
    }

    // The helper itself: a null reason is the configured failure message.
    [Fact]
    public void Refuse_WithNoReason_SendsTheFailureMessage_AndNothingWhenWarnIsOff()
    {
        using Rig r = Build();
        r.Engine.FailureMessage = "not for you";
        r.Engine.RegisterHandler("@probe", PlayerRemoteControls.QueryVersion, ctx => ctx.Refuse());

        ChatLines.Hear(r.Router, On(ChatChannel.TelepathIncoming, "@probe"));
        Assert.Equal($"/{Sender} {{not for you}}\r", Assert.Single(Sent(r)));

        r.Replies.Clear();
        r.Engine.WarnOnDenial = false;
        ChatLines.Hear(r.Router, On(ChatChannel.TelepathIncoming, "@probe"));
        Assert.Empty(Sent(r));
    }

    // A caller on the machine itself is told why, whatever the Warn setting says
    // to other players.
    [Fact]
    public void LocalInvocation_IsToldWhy_EvenWithWarnOff()
    {
        using Rig r = Build();
        r.Engine.WarnOnDenial = false;
        List<string> replies = new();

        r.Engine.TryInvokeLocal("@goto", null, replies.Add);

        Assert.Equal("@goto requires a destination", Assert.Single(replies));
    }

    // The gate lives in one place. A handler that read the setting itself could
    // gate one failure and forget the next; none does. The bug report reads it only
    // to print it, and decides nothing by it.
    [Fact]
    public void OnlyTheEngineReadsTheWarnSetting()
    {
        using AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(typeof(RemoteCommandManager).Assembly.Location);
        List<string> readers = new();
        foreach (TypeDefinition type in asm.MainModule.GetTypes())
        {
            TypeDefinition outer = type;
            while (outer.DeclaringType is not null) outer = outer.DeclaringType;
            if (outer.Name == nameof(RemoteCommandManager)) continue;
            if (outer.Name == nameof(MudPlay.Services.BugReportAutomationSections)) continue;

            foreach (MethodDefinition method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (Instruction instr in method.Body.Instructions)
                {
                    if (instr.Operand is MethodReference { Name: "get_" + nameof(RemoteCommandManager.WarnOnDenial) } call
                        && call.DeclaringType.Name == nameof(RemoteCommandManager))
                        readers.Add($"{type.FullName}.{method.Name}");
                }
            }
        }

        Assert.Empty(readers);
    }
}
