using System.Text;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Gmcp;
using MudPlay.Game.Map;
using MudPlay.Models.Settings;
using MudPlay.Net;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

public sealed partial class GmcpIntegrationTests
{
    private sealed class Session : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-gmcp-" + Guid.NewGuid().ToString("N"));
        private readonly object _dispatch = new();
        private readonly StringBuilder _text = new();
        public T Read<T>(Func<T> inspect) { lock (_dispatch) return inspect(); }
        public string Transcript => Read(() => _text.ToString());
        public void ClearText() { lock (_dispatch) _text.Clear(); }
        public ServerFeed Feed { get; } = new();
        public WirePromptScanner Scanner { get; } = new();
        public PlayerState Player { get; } = new();
        public PlayerStats Stats { get; } = new();
        public LogService Log { get; } = new();
        public PromptParser Prompt { get; }
        public StatParser StatParser { get; }
        public RoomTracker Tracker { get; }
        public RoomGraphManager Graph { get; }
        public RoomEntityClassifier Classifier { get; }
        public CombatStateTracker Combat { get; }
        public Game.Conditions.ConditionTracker Conditions { get; } = new(new MessageStore());
        public Game.Stealth.StealthManager Stealth { get; }
        public MonsterHpTracker Hp { get; }
        public ManualTimers Timers { get; } = new();
        public GmcpIntegration Gmcp { get; }
        // Fed as the connection feeds it (MainWindowViewModel.ArmConnection), and
        // recording, as the player's Record box would have it.
        public GmcpMonitor Monitor { get; } = new() { Enabled = true };
        public TelnetClient Telnet { get; } = new();
        public Game.Inventory.InventoryManager Inventory { get; }
        public Game.Inventory.AutoBuyManager Shop { get; }
        public Game.Spells.SpellbookState Book { get; }
        public Game.Spells.SpellListParser SpellParser { get; }
        public PartyManager Party { get; }
        public ChatRouter Chat { get; }
        public AbilBreakdownParser Abilities { get; }
        public Game.Spells.CastCoordinator Cast { get; }
        public Game.Spells.CastingDirector Director { get; }
        public MessageRouter Router { get; }
        private readonly RoomDisplayParser _display;
        private readonly EncumbranceParser _encumbrance;

        // rooms and monsterTable, when given, are the game data's Rooms and Monsters
        // tables, imported as the active set; maxHp is the game data's max HP lookup.
        public Session(bool active = true, ManualClock? clock = null, string? rooms = null, string? monsterTable = null,
            Func<int, int?>? maxHp = null)
        {
            Directory.CreateDirectory(_root);
            MessageRouter router = new();
            Router = router;
            DefaultPatterns.Seed(router);
            Feed.Lines.LineEmitted += router.Dispatch;
            Feed.Lines.ChatLineEmitted += router.Dispatch;
            Feed.Lines.ReadEnded += router.EndRead;
            Prompt = new(Scanner, Player);
            StatParser = new(Stats, Log, schedule: new ManualTimers().Arm);
            StatParser.AttachLineExtractor(Feed.Lines);
            GameDataCache cache = new(_root);
            if (rooms is not null || monsterTable is not null)
            {
                string set = Path.Combine(_root, "alpha");
                Directory.CreateDirectory(set);
                if (rooms is not null) File.WriteAllText(Path.Combine(set, "Rooms.json"), rooms);
                if (monsterTable is not null) File.WriteAllText(Path.Combine(set, "Monsters.json"), monsterTable);
                cache.SwitchSet("alpha");
            }
            Graph = new(cache);
            if (rooms is not null) Graph.OnActiveSetChanged("alpha");
            Tracker = new(Graph);
            // As the client wires it: a too-dark line marks the room dark.
            router.Subscribe(KnownPatterns.RoomPitchBlack, _ => Tracker.NoteDarkRoomEntered());
            router.Subscribe(KnownPatterns.RoomVeryDark, _ => Tracker.NoteDarkRoomEntered());
            MonsterMessageStore monsters = new();
            Classifier = new(router, monsters, new PlayerDatabase(new ProfileService(), () => null), Tracker, Log,
                gameData: monsterTable is null ? null : cache);
            Classifier.AttachLineExtractor(Feed.Lines);
            Combat = new(router, new MovementCoordinator(Log), Classifier, monsters, Player, () => true,
                resolveOverlay: null, Log, clock is null ? null : () => clock.Now);
            _encumbrance = new(router, Player, Log);
            Gmcp = new(Prompt, StatParser, Graph, Tracker, Classifier, Combat, _encumbrance, Log, router);
            Stealth = new(router, Player, Log);
            Hp = new(maxHp ?? (_ => 100), _ => 0);
            Gmcp.BindConsumers(Conditions, Stealth, Hp);
            Inventory = new(Log, isKey: name => name.Contains("key", StringComparison.OrdinalIgnoreCase));
            Inventory.AttachLineExtractor(Feed.Lines);
            Shop = new(router, _ => null, _ => 0, () => false, Log, schedule: Timers.Arm);
            Shop.AttachLineExtractor(Feed.Lines);
            Book = new(new Game.Spells.KnownSpellCatalog(cache));
            SpellParser = new(Book, Log);
            SpellParser.AttachLineExtractor(Feed.Lines);
            Party = new(router, new PartyState(), Log) { LocalCharacterName = "Tester" };
            Party.AttachLineExtractor(Feed.Lines);
            Chat = new(router, log: Log);
            Abilities = new(Log);
            Abilities.AttachLineExtractor(Feed.Lines);
            Cast = new(router, Log);
            Director = new(Player, Cast, () => new Models.Profile.SpellsSettings(), () => new Models.Profile.HealthSettings(), () => false, Log);
            Gmcp.BindMilestone2(Inventory, Shop, Book, SpellParser, Party, Chat, Abilities, Director);
            Classifier.EntitiesObserved += Hp.NoteRoomEntities;
            _display = new(Feed.Lines, Tracker, Log) { GmcpOwnsRoom = () => Gmcp.State.Owns("Anomaly.Room.Info") };
            _display.RoomParsed += Gmcp.ObserveRoom;
            Scanner.PromptObserved += Gmcp.ObservePrompt;
            Gmcp.Configure(GmcpIntegration.Packages.ToDictionary(p => p, _ => active ? GmcpMode.Active : GmcpMode.Shadow));
            Telnet.DataReceived += Display;
            Telnet.GmcpReceived += Monitor.NoteReceived;
            Telnet.GmcpNegotiated += Monitor.NoteHandshake;
            Telnet.GmcpReceived += message => { lock (_dispatch) Gmcp.Receive(message); };
            Telnet.GmcpNegotiated += enabled => { lock (_dispatch) { if (!enabled) Gmcp.Disconnected(); } };
            Gmcp.SetSender((package, payload) =>
            {
                Monitor.NoteSent(package, payload);
                return Telnet.SendGmcpAsync(package, payload);
            });
        }

        public void Send(string package, object payload) => Gmcp.Receive(new(package, JsonSerializer.SerializeToElement(payload)));
        private void Display(ReadOnlyMemory<byte> bytes)
        {
            lock (_dispatch)
            {
                _text.Append(Encoding.Latin1.GetString(bytes.Span));
                if (_text.Length > 64000) _text.Remove(0, _text.Length - 32000);
                Scanner.Append(bytes.Span);
                Feed.Emulator.Feed(bytes.Span);
            }
        }
        public void Text(string text)
        {
            Monitor.NoteText(Cp437.Encode(text));
            Scanner.Append(Encoding.ASCII.GetBytes(text));
            Feed.Raw(text);
        }
        private void Wire(byte[] bytes)
        {
            byte[] output = new byte[Math.Max(1, bytes.Length)];
            int count = Telnet.Interpret(bytes, output);
            if (count > 0) Display(output.AsMemory(0, count));
        }
        private static byte[] ServerPacket(string package, JsonElement payload) => new byte[] { 255, 250, 201 }
            .Concat(Encoding.UTF8.GetBytes(package + " " + payload.GetRawText())).Concat(new byte[] { 255, 240 }).ToArray();

        public void Replay(string name)
        {
            foreach (JsonElement frame in GmcpStateTests.Frames(name))
            {
                if (frame.GetProperty("dir").GetString() != "s2c") continue;
                string? kind = frame.GetProperty("kind").GetString();
                if (frame.TryGetProperty("raw", out JsonElement raw)) Wire(Convert.FromBase64String(raw.GetString()!));
                else if (kind == "gmcp") Wire(ServerPacket(frame.GetProperty("pkg").GetString()!, frame.GetProperty("msg")));
                else if (kind == "negotiation") Wire(new byte[] { 255, frame.GetProperty("telnet").GetString()!.Contains("WONT",
                    StringComparison.Ordinal) ? (byte)252 : (byte)251, 201 });
                else if (kind == "text") Text(frame.GetProperty("text").GetString()! + "\r\n");
            }
        }
        public void ReplayCapture(string name) =>
            ReplayCaptureFile(Path.Combine(AppContext.BaseDirectory, "Gmcp", "Captured", name + ".jsonl"));

        public void ReplayCaptureFile(string path)
        {
            foreach (string line in File.ReadLines(path).Skip(1))
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement frame = document.RootElement;
                if (frame.GetProperty("dir").GetString() != "s2c") continue;
                if (frame.TryGetProperty("raw", out JsonElement raw)) { Wire(Convert.FromBase64String(raw.GetString()!)); continue; }
                string? kind = frame.GetProperty("kind").GetString();
                if (kind == "text") Text(frame.GetProperty("text").GetString()!);
                else if (kind == "gmcp") Wire(ServerPacket(frame.GetProperty("pkg").GetString()!, frame.GetProperty("msg")));
                else if (kind == "negotiation")
                {
                    string[] tokens = frame.GetProperty("telnet").GetString()!.Split(' ');
                    byte command = tokens[1] switch { "DO" => 253, "DONT" => 254, "WILL" => 251, "WONT" => 252, _ => 0 };
                    byte option = tokens[2] switch { "GMCP" => 201, "ECHO" => 1, "SGA" => 3, "BINARY" => 0, "NAWS" => 31, "LINEMODE" =>
                        34, "TTYPE" => 24, _ => byte.Parse(tokens[2]) };
                    Wire(new byte[] { 255, command, option });
                }
            }
        }

        public void Dispose()
        {
            Director.Dispose();
            Cast.Dispose();
            Inventory.Dispose();
            Shop.Dispose();
            SpellParser.Dispose();
            Party.Dispose();
            Chat.Dispose();
            Abilities.Dispose();
            Conditions.Dispose();
            Stealth.Dispose();
            _display.Dispose();
            _encumbrance.Dispose();
            Combat.Dispose();
            Classifier.Dispose();
            StatParser.Dispose();
            Prompt.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private static async Task Until(Func<bool> ready, string condition, int seconds = 30, Func<string?>? ended = null)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!ready() && DateTime.UtcNow < deadline)
        {
            Assert.True(ended?.Invoke() is null, "Connection ended while waiting for " + condition + ": " + ended?.Invoke());
            await Task.Delay(25);
        }
        Assert.True(ready(), "Timed out waiting for " + condition);
    }

    private static async Task Login(Session session, string account, string password)
    {
        string host = Environment.GetEnvironmentVariable("MUDPLAY_GMCP_HOST") ?? "192.168.1.100";
        int port = int.Parse(Environment.GetEnvironmentVariable("MUDPLAY_GMCP_PORT") ?? "2350");
        Assert.True(host == "127.0.0.1" || host == "192.168.1.100" && port == 2350, "Live tests only run against the isolated host or its local recording proxy.");
        await session.Telnet.ConnectAsync(host, port);
        await Until(() => session.Transcript.Contains("account name", StringComparison.OrdinalIgnoreCase), "test account prompt", ended: () => session.Telnet.EndedBecause);
        session.ClearText();
        await session.Telnet.SendTextAsync(account + "\r\n");
        await Until(() => session.Transcript.Contains("password", StringComparison.OrdinalIgnoreCase), "test password prompt", ended: () => session.Telnet.EndedBecause);
        session.ClearText();
        await session.Telnet.SendTextAsync(password + "\r\n");
        await Until(() => session.Transcript.Contains("Selection:", StringComparison.OrdinalIgnoreCase), "test board menu", ended: () => session.Telnet.EndedBecause);
        session.ClearText();
        await session.Telnet.SendTextAsync("1\r\n");
        await Until(() => session.Transcript.Contains("[MMUDREBORN]:", StringComparison.OrdinalIgnoreCase), "test realm menu", ended: () => session.Telnet.EndedBecause);
        session.ClearText();
        await session.Telnet.SendTextAsync("e\r\n");
        await Until(() => session.Read(() => session.Gmcp.State.Owns("Anomaly.Room.Info")
            || session.Gmcp.State.Snapshot("Anomaly.Room.Info") is not null), "initial server snapshots", ended: () => session.Telnet.EndedBecause);
        await Until(() => session.Read(() => session.Gmcp.State.RosterComplete), "complete initial monster baselines", ended: () => session.Telnet.EndedBecause);
    }

    private static async Task Move(Session session, string direction)
    {
        JsonElement room = session.Read(() => session.Gmcp.State.Snapshot("Anomaly.Room.Info")!.Value);
        string destination = room.GetProperty("exits").EnumerateArray().Single(e => e.GetProperty("dir").GetString() == direction).GetProperty("to").GetString()!;
        await session.Telnet.SendTextAsync(direction + "\r\n");
        await Until(() => session.Read(() => session.Gmcp.State.CurrentRoom == destination
            && session.Gmcp.State.Owns("Anomaly.Room.Info") && session.Gmcp.State.RosterComplete), "movement to server-provided exit destination");
    }

    [LiveGmcpFact]
    public async Task IsolatedHost_ActiveDarkLockedHiddenAndCombatLoop()
    {
        using Session session = new();
        try
        {
            await Login(session, Environment.GetEnvironmentVariable("MUDPLAY_GMCP_ACCOUNT")!, Environment.GetEnvironmentVariable("MUDPLAY_GMCP_PASSWORD")!);
            Assert.Equal("1,1", session.Read(() => session.Gmcp.State.CurrentRoom));
            foreach (string direction in new[] { "n", "n", "n", "w" }) await Move(session, direction);
            Assert.Equal("1,198", session.Read(() => session.Gmcp.State.CurrentRoom));
            Assert.True(session.Read(() => session.Gmcp.State.Snapshot("Anomaly.Room.Info")!.Value.GetProperty("light").GetInt32() < 0));
            JsonElement Door() => session.Read(() => session.Gmcp.State.Snapshot("Anomaly.Room.Info")!.Value)
                .GetProperty("exits").EnumerateArray().Single(e => e.GetProperty("dir").GetString() == "n").GetProperty("door");
            Assert.NotEqual(JsonValueKind.Null, Door().ValueKind);
            for (int attempt = 0; attempt < 5 && Door().GetProperty("state").GetString() != "open"; attempt++)
            {
                await session.Telnet.SendTextAsync("pick n\r\n");
                await Task.Delay(1100);
                await session.Telnet.SendTextAsync("open n\r\n");
                await Task.Delay(1100);
            }
            Assert.Equal("open", Door().GetProperty("state").GetString());
            await Move(session, "n");
            await Move(session, "s");
            foreach (string direction in new[] { "e", "s", "s", "s" }) await Move(session, direction);
            Assert.Equal("1,1", session.Read(() => session.Gmcp.State.CurrentRoom));
            foreach (string direction in new[] { "s", "s", "w", "w", "s", "s" }) await Move(session, direction);
            Assert.Equal("1,123", session.Read(() => session.Gmcp.State.CurrentRoom));
            RoomEntity target = session.Read(() => session.Classifier.Current!.Value.Entities.First(e => e.Kind == EntityKind.Monster
                && !e.RawName.Contains("guard", StringComparison.OrdinalIgnoreCase)));
            await session.Telnet.SendTextAsync("attack " + target.RawName + "\r\n");
            await Until(() => session.Read(() => target.RuntimeId is { } uid && !session.Gmcp.State.StaticMonsters.ContainsKey(uid)),
                "monster death and identity eviction", seconds: 90);
            await session.Telnet.SendTextAsync("rest\r\n");
            await Until(() => session.Read(() => session.Player.Position == PlayerPosition.Resting), "server-driven resting state");
            foreach (string direction in new[] { "s", "w", "w" }) await Move(session, direction);
            Assert.Equal("1,140", session.Read(() => session.Gmcp.State.CurrentRoom));
            JsonElement hidden = session.Read(() => session.Gmcp.State.Snapshot("Anomaly.Room.Info")!.Value)
                .GetProperty("exits").EnumerateArray().Single(e => e.GetProperty("dir").GetString() == "u");
            Assert.True(hidden.GetProperty("hidden").GetBoolean());
            await Move(session, "u");
            await Move(session, "d");
            foreach (string direction in new[] { "n", "n", "n", "n", "n", "e", "e", "e", "e" }) await Move(session, direction);
            Assert.Equal("1,1", session.Read(() => session.Gmcp.State.CurrentRoom));
            Assert.DoesNotContain(session.Log.Snapshot(), entry => entry.Source == "GMCP" && entry.Message.Contains("Cannot apply", StringComparison.Ordinal));
            await session.Telnet.SendTextAsync("quit\r\n");
        }
        finally { await session.Telnet.DisconnectAsync(); }
    }

    [LiveGmcpFact]
    public async Task IsolatedHost_ActiveWalkRecoveryResubscriptionWontAndReconnect()
    {
        string account = Environment.GetEnvironmentVariable("MUDPLAY_GMCP_ACCOUNT")!;
        string password = Environment.GetEnvironmentVariable("MUDPLAY_GMCP_PASSWORD")!;
        using Session session = new();
        using Session sysop = new(active: false);
        try
        {
            await Login(session, account, password);
            string origin = session.Read(() => session.Gmcp.State.CurrentRoom!);
            JsonElement room = session.Read(() => session.Gmcp.State.Snapshot("Anomaly.Room.Info")!.Value);
            JsonElement exit = room.GetProperty("exits").EnumerateArray().First(e => e.GetProperty("kind").GetString() == "normal");
            string destination = exit.GetProperty("to").GetString()!;
            await session.Telnet.SendTextAsync(exit.GetProperty("dir").GetString()! + "\r\n");
            await Until(() => session.Read(() => session.Gmcp.State.CurrentRoom == destination && session.Gmcp.State.Owns("Anomaly.Room.Info")), "active movement confirmation");
            JsonElement back = session.Read(() => session.Gmcp.State.Snapshot("Anomaly.Room.Info")!.Value)
                .GetProperty("exits").EnumerateArray().First(e => e.GetProperty("to").GetString() == origin);
            await session.Telnet.SendTextAsync(back.GetProperty("dir").GetString()! + "\r\n");
            await Until(() => session.Read(() => session.Gmcp.State.CurrentRoom == origin && session.Gmcp.State.RosterComplete), "return baseline");
            await session.Telnet.SendGmcpAsync("Core.Supports.Remove", new[] { "Anomaly.Mob" });
            await Until(() => session.Read(() => session.Gmcp.State.StaticMonsters.Count == 0), "scoped monster removal");
            await session.Telnet.SendGmcpAsync("Core.Supports.Add", new[] { "Anomaly.Mob 1" });
            await Until(() => session.Read(() => session.Gmcp.State.RosterComplete), "subscription restoration");
            await session.Gmcp.RefreshAsync();
            await Login(sysop, Environment.GetEnvironmentVariable("MUDPLAY_GMCP_SYSOP_ACCOUNT")!, Environment.GetEnvironmentVariable("MUDPLAY_GMCP_SYSOP_PASSWORD")!);
            await sysop.Telnet.SendTextAsync("sysop configure gmcp off\r\n");
            await Until(() => session.Read(() => !session.Gmcp.State.Compatible), "WONT fallback");
            session.ClearText();
            await session.Telnet.SendTextAsync("look\r\n");
            await Until(() => session.Transcript.Contains("[HP=", StringComparison.Ordinal), "text fallback prompt");
            Assert.False(session.Read(() => session.Gmcp.State.Owns("Anomaly.Char.Vitals")));
            await sysop.Telnet.SendTextAsync("sysop configure gmcp on\r\n");
            await Until(() => session.Read(() => session.Gmcp.State.Owns("Anomaly.Char.Vitals") && session.Gmcp.State.RosterComplete), "fresh handshake after WILL");
            await session.Telnet.SendTextAsync("quit\r\n");
            await session.Telnet.DisconnectAsync();
            using Session reconnect = new();
            try
            {
                await Login(reconnect, account, password);
                Assert.Equal(origin, reconnect.Read(() => reconnect.Gmcp.State.CurrentRoom));
                Assert.True(reconnect.Read(() => reconnect.Gmcp.State.RosterComplete));
                await reconnect.Telnet.SendTextAsync("quit\r\n");
            }
            finally { await reconnect.Telnet.DisconnectAsync(); }
        }
        finally
        {
            await sysop.Telnet.SendTextAsync("sysop configure gmcp on\r\n");
            await session.Telnet.DisconnectAsync();
            await sysop.Telnet.DisconnectAsync();
        }
    }

    [Theory]
    [InlineData("01-handshake")]
    [InlineData("02-walk")]
    [InlineData("03-fight")]
    [InlineData("04-leave-and-return")]
    [InlineData("06-resubscribe")]
    [InlineData("07-recovery-request")]
    [InlineData("09-reconnect")]
    [InlineData("10-gmcp-off-midsession")]
    [InlineData("12-transport")]
    public void CapturedServerSessions_ReachExistingModelsWithoutContractFailures(string name)
    {
        using Session session = new();
        session.ReplayCapture(name);
        Assert.True(session.Player.HasPromptData);
        Assert.DoesNotContain(session.Log.Snapshot(), entry => entry.Source == "GMCP" &&
            (entry.Message.Contains("Rejected", StringComparison.Ordinal) || entry.Message.Contains("Cannot apply", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("01-handshake")]
    [InlineData("02-walk")]
    [InlineData("03-fight")]
    [InlineData("04-leave-and-return")]
    [InlineData("06-resubscribe")]
    [InlineData("07-recovery-request")]
    public void CapturedServerSessions_ShadowParityHasNoUnexplainedDifferences(string name)
    {
        using Session session = new(active: false);
        session.ReplayCapture(name);
        Assert.Empty(session.Log.Snapshot().Where(entry => entry.Source == "GMCP shadow").Select(entry => entry.Message));
    }

    // The game prints a regen tick's prompt first and the server's vitals follow, up
    // to a second behind (export gmcp-20261029-150714: [HP=10] at 64.866 s, its
    // vitals at 65.811 s). Compared at the prompt, against the vitals still showing
    // the tick before, nearly every tick read as a difference: 267 in a 22-minute
    // session (export gmcp-20261029-152755). Compared as the vitals arrive, only a
    // real difference shows.
    [Fact]
    public void ShadowVitals_AreComparedAsTheyArrive_NotAtThePromptAhead()
    {
        using Session session = new(active: false);
        session.Replay("01-handshake");
        static object Vitals(int hp) => new { hp, maxhp = 32, mana = 18, maxmana = 22, manaType = "mana", level = 1, exp = 728, expNeeded = 1850 };
        string[] Shadow() => session.Log.Snapshot().Where(e => e.Source == "GMCP shadow").Select(e => e.Message).ToArray();

        session.Text("[HP=9/MA=18]:\r\n");
        session.Send("Anomaly.Char.Vitals", Vitals(9));
        session.Text("[HP=10/MA=18]:\r\n");          // the tick, its vitals still on the way
        Assert.Empty(Shadow());
        session.Send("Anomaly.Char.Vitals", Vitals(10));
        Assert.Empty(Shadow());

        session.Text("[HP=11/MA=18]:\r\n");
        session.Send("Anomaly.Char.Vitals", Vitals(12));
        Assert.Equal("Char.Vitals.hp: GMCP=12; text=11", Assert.Single(Shadow()));
    }

    [Fact]
    public void ActiveVitals_OverrideTextOnlyUntilTheirOwnPackageIsReset()
    {
        using Session session = new();
        session.Replay("01-handshake");
        Assert.Equal(150, session.Player.Hp);
        Assert.Equal(150, session.Player.MaxHp);
        session.Text("[HP=3]:\r\n");
        Assert.Equal(150, session.Player.Hp);
        session.Send("Anomaly.Session.Reset", new { scope = "package", package = "Anomaly.Char.Vitals" });
        session.Text("[HP=7]:\r\n");
        Assert.Equal(7, session.Player.Hp);
        Assert.True(session.Gmcp.State.Owns("Anomaly.Char.Status"));
    }

    [Fact]
    public void RoomReset_DoesNotLoseTheMoveItsBaselineMustConfirm()
    {
        using Session session = new();
        session.Replay("01-handshake");
        int answers = 0;
        session.Tracker.MoveConfirmed += () => answers++;
        session.Tracker.NoteMoveSent(Direction.N);
        session.Send("Anomaly.Session.Reset", new { scope = "room", room = "1,2" });
        // Between the reset and the arriving room the position is still known; an Unknown here sends the walker into an rm resync each step.
        Assert.NotEqual(RoomConfidence.Unknown, session.Tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), session.Tracker.State.CurrentRoom!.Key);
        Dictionary<string, JsonElement> room = GmcpStateTests.Frames("01-handshake").First(f =>
            f.TryGetProperty("pkg", out JsonElement package) && package.GetString() == "Anomaly.Room.Info")
            .GetProperty("msg").EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        room["room"] = JsonSerializer.SerializeToElement("1,2");
        session.Send("Anomaly.Room.Info", room);
        session.Send("Anomaly.Room.Occupants", new { room = "1,2", mobs = Array.Empty<object>(), players = Array.Empty<object>() });
        session.Text("Town Square\r\nObvious exits: north, south\r\n");
        Assert.Equal(1, answers);
        Assert.False(session.Tracker.HasQueuedMoves);
        Assert.Equal(new RoomKey(1, 2), session.Tracker.State.CurrentRoom!.Key);
        Assert.NotNull(session.Graph.GetRoom(new RoomKey(1, 1)));
    }

    // Room.Info comes ahead of the arriving room's occupants and its text display.
    // Confirmed at once, the room let a loop take its next step past a monster
    // nobody knew was there (report paradigm-20261029-071539). The move is answered
    // at the text display's exits, as on the text path, so the floor survey ("You
    // notice … here.", which the server's room data doesn't carry) and the server's
    // roster are both in by then.
    // A `look <dir>` that shows nothing (no exit that way) leaves the peek flag armed
    // for 3 s. A move inside that window got its room from the server, but its display
    // read as the peek, so the room stayed held and the move unanswered. A peek can't
    // come between the server's room and its display (export gmcp-20261029-150551:
    // a peek sends no GMCP at all), so the held room's display answers the move.
    [Fact]
    public void ArrivingRoom_AfterALookThatShowedNothing_IsConfirmedByItsDisplay()
    {
        using Session session = new();
        session.Replay("07-recovery-request");
        Dictionary<string, JsonElement> room = session.Gmcp.State.Snapshot("Anomaly.Room.Info")!.Value
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        session.Tracker.NoteLookSent();
        session.Text("You don't see anything special that way.\r\n[HP=9/MA=2]:");

        session.Tracker.NoteMoveSent(Direction.N);
        session.Send("Anomaly.Session.Reset", new { scope = "room", room = "1,2" });
        room["room"] = JsonSerializer.SerializeToElement("1,2");
        room["name"] = JsonSerializer.SerializeToElement("Rat Den");
        session.Send("Anomaly.Room.Info", room);
        session.Send("Anomaly.Room.Occupants", new { room = "1,2", mobs = Array.Empty<object>(), players = Array.Empty<object>() });
        Assert.Equal(RoomConfidence.Pending, session.Tracker.State.Confidence);

        session.Text("\r\nRat Den\r\nObvious exits: south\r\n[HP=9/MA=2]:");

        Assert.Equal(new RoomKey(1, 2), session.Tracker.State.CurrentRoom!.Key);
        Assert.NotEqual(RoomConfidence.Pending, session.Tracker.State.Confidence);
    }

    [Fact]
    public void ArrivingRoom_IsConfirmedOnlyOnceItsTextDisplayIsRead()
    {
        using Session session = new();
        session.Replay("07-recovery-request");
        Dictionary<string, JsonElement> room = session.Gmcp.State.Snapshot("Anomaly.Room.Info")!.Value
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        JsonElement monster = session.Gmcp.State.Snapshot("Anomaly.Room.Occupants")!.Value.GetProperty("mobs")[0];
        JsonElement liveBaseline = GmcpStateTests.Frames("07-recovery-request").Last(f =>
            f.TryGetProperty("pkg", out JsonElement package) && package.GetString() == "Anomaly.Mob.Update"
            && f.GetProperty("msg").GetProperty("full").GetBoolean()).GetProperty("msg");
        session.Send("Anomaly.Session.Reset", new { scope = "room", room = "1,2" });
        room["room"] = JsonSerializer.SerializeToElement("1,2");
        bool? monsterKnownWhenConfirmed = null;
        session.Tracker.MoveConfirmed += () => monsterKnownWhenConfirmed = session.Combat.HasRoomNpc;
        session.Tracker.NoteMoveSent(Direction.N);

        session.Send("Anomaly.Room.Info", room);
        Assert.Null(monsterKnownWhenConfirmed);
        Assert.Equal(RoomConfidence.Pending, session.Tracker.State.Confidence);

        session.Send("Anomaly.Room.Occupants", new { room = "1,2", mobs = new[] { monster }, players = Array.Empty<object>() });
        Dictionary<string, JsonElement> live = liveBaseline.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        live["room"] = JsonSerializer.SerializeToElement("1,2");
        session.Send("Anomaly.Mob.Update", live);
        Assert.True(session.Combat.HasRoomNpc);
        Assert.Null(monsterKnownWhenConfirmed);     // the roster alone doesn't answer the move

        session.Text("Rat Den\r\nYou notice a rusty dagger here.\r\nAlso here: large giant rat.\r\nObvious exits: south\r\n");
        Assert.True(monsterKnownWhenConfirmed);
        Assert.Equal(new RoomKey(1, 2), session.Tracker.State.CurrentRoom!.Key);
    }

    // The server's list for the arriving room can lack a monster the display's
    // "Also here:" shows, and name it a moment after (report
    // paradigm-20261031-014901: a troll back in its lair). Read on the list alone,
    // the room confirmed empty and the loop walked on past it. The display's
    // monster counts, so the fight holds the walk; the server's next list replaces it.
    [Fact]
    public void ArrivingRoom_AMonsterTheServersListLacks_IsReadFromTheDisplay()
    {
        using Session session = new(monsterTable: """[{ "Number": 12, "Name": "giant rat" }]""");
        session.Replay("01-handshake");
        Dictionary<string, JsonElement> room = session.Gmcp.State.Snapshot("Anomaly.Room.Info")!.Value
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        room["room"] = JsonSerializer.SerializeToElement("1,2");
        bool? fightWhenConfirmed = null;
        session.Tracker.MoveConfirmed += () => fightWhenConfirmed = session.Combat.HasEngageableHostiles;
        session.Tracker.NoteMoveSent(Direction.N);

        session.Send("Anomaly.Session.Reset", new { scope = "room", room = "1,2" });
        session.Send("Anomaly.Room.Info", room);
        session.Send("Anomaly.Room.Occupants", new { room = "1,2", mobs = Array.Empty<object>(), players = Array.Empty<object>() });
        session.Text("Rat Den\r\nAlso here: large giant rat.\r\nObvious exits: south\r\n");

        Assert.Equal(new RoomKey(1, 2), session.Tracker.State.CurrentRoom!.Key);
        Assert.True(fightWhenConfirmed);
        Assert.Contains(session.Classifier.Current!.Value.Entities, e => e.RawName == "large giant rat");

        session.Send("Anomaly.Room.Occupants", new { room = "1,2", mobs = Array.Empty<object>(), players = Array.Empty<object>() });
        Assert.Empty(session.Classifier.Current!.Value.Entities);
    }

    // The bug report shows which server messages came when, beside the program
    // log's clock, but never a chat line's words.
    [Fact]
    public void RecentMessages_ForTheBugReport_KeepTheOrder_ButNoChatText()
    {
        using Session session = new();
        session.Replay("01-handshake");
        session.Send("Anomaly.Comm.Line", new { channel = "gossip", from = "Someone", text = "a private matter" });

        string recent = session.Gmcp.DescribeRecent();

        Assert.True(recent.IndexOf("Anomaly.Session.Reset", StringComparison.Ordinal)
                    < recent.IndexOf("Anomaly.Room.Info", StringComparison.Ordinal));
        Assert.Contains("Anomaly.Comm.Line", recent);
        Assert.DoesNotContain("a private matter", recent);
    }

    // A follower's drag: the server's room comes ahead of "-- Following your Party
    // leader <dir> --" and the display (export gmcp-20261029-070501). Applied at
    // once, the room took no move, and the drag line then queued one from the new
    // room, left in flight for the rest of the drag.
    [Fact]
    public void FollowerDrag_TheLeadersRoomWaitsForTheDragLine_AndLeavesNoMoveInFlight()
    {
        using Session session = new();
        using FollowMoveObserver drags = new(session.Router, session.Tracker);
        session.Replay("01-handshake");
        Dictionary<string, JsonElement> room = session.Gmcp.State.Snapshot("Anomaly.Room.Info")!.Value
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        room["room"] = JsonSerializer.SerializeToElement("1,2");

        session.Send("Anomaly.Session.Reset", new { scope = "room", room = "1,2" });
        session.Send("Anomaly.Room.Info", room);
        session.Text("-- Following your Party leader north --\r\nTown Square\r\nObvious exits: south\r\n");

        Assert.Equal(new RoomKey(1, 2), session.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(RoomConfidence.Confirmed, session.Tracker.State.Confidence);
        Assert.False(session.Tracker.HasQueuedMoves);
    }

    // A roster change while our move is in flight (a monster walking into the room
    // we're leaving, or the new room's list ahead of its text) isn't the room we land
    // in read anew: on the text path it's an arrival line, which waits for the move.
    // Read as a fresh roster, it told combat we had landed, and a fight could open
    // in the room we were leaving. Once the room is confirmed, its list is read as
    // that room's own.
    [Fact]
    public void ActiveRoster_ARosterChangeMidMoveIsAnArrival_TheConfirmedRoomIsReadAnew()
    {
        using Session session = new();
        session.Replay("07-recovery-request");
        Dictionary<string, JsonElement> room = session.Gmcp.State.Snapshot("Anomaly.Room.Info")!.Value
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        JsonElement monster = session.Gmcp.State.Snapshot("Anomaly.Room.Occupants")!.Value.GetProperty("mobs")[0];
        List<RoomObservationSource> sources = new();
        session.Classifier.EntitiesObserved += obs => sources.Add(obs.Source);
        session.Tracker.NoteMoveSent(Direction.N);

        session.Send("Anomaly.Room.Occupants", new { room = session.Gmcp.State.CurrentRoom, mobs = Array.Empty<object>(), players = Array.Empty<object>() });
        Assert.Equal([RoomObservationSource.Arrival], sources);

        session.Send("Anomaly.Session.Reset", new { scope = "room", room = "1,2" });
        room["room"] = JsonSerializer.SerializeToElement("1,2");
        session.Send("Anomaly.Room.Info", room);
        session.Send("Anomaly.Room.Occupants", new { room = "1,2", mobs = new[] { monster }, players = Array.Empty<object>() });
        session.Text("Rat Den\r\nAlso here: large giant rat.\r\nObvious exits: south\r\n");

        Assert.Equal(RoomObservationSource.AlsoHere, sources[^1]);
        Assert.Equal(new RoomKey(1, 2), session.Tracker.State.CurrentRoom!.Key);
    }

    // Crossing the level line the game prints only the gain line, and with the
    // server's vitals owned the gain line stands down, so nothing raised the gain and
    // the level-up announcement went quiet (export gmcp-20261029-152755). A rise in
    // the server's exp within a session is the gain; a session's first vitals only
    // re-anchors, since it can carry exp earned elsewhere.
    [Fact]
    public void ActiveVitals_ARiseInExp_IsAGain_ButNotTheSessionsFirstVitals()
    {
        using Session session = new();
        List<long> gains = new();
        session.StatParser.ExperienceGained += gains.Add;
        object Vitals(long exp) => new { hp = 24, maxhp = 32, mana = 21, maxmana = 22, manaType = "mana", level = 1, exp, expNeeded = 1850 };

        session.Replay("01-handshake");
        session.Send("Anomaly.Char.Vitals", Vitals(1842));
        session.Text("You gain 15 experience.\r\n");
        session.Send("Anomaly.Char.Vitals", Vitals(1857));
        session.Send("Anomaly.Char.Vitals", Vitals(1857));

        Assert.Equal(1857, session.Stats.Exp);
        Assert.Equal(1857, gains.Last());
        int gainsBefore = gains.Count;

        session.Gmcp.Disconnected();
        session.Replay("09-reconnect");
        session.Send("Anomaly.Char.Vitals", Vitals(5000));
        Assert.Equal(gainsBefore, gains.Count);
    }

    // A monster that walks out is gone from the server's roster as it is from the
    // text's after its "just left" line, and is read as that departure. Read as the
    // room re-read, a boss that wandered off was missing from it, and the boss
    // timers' fallback started its timer as for a kill (export gmcp-20261029-143336:
    // `Anomaly.Mob.Gone` with "why":"left" 16 ms after "just left to the east"). A
    // death stays a re-read of the room, which is what that fallback looks for.
    [Theory]
    [InlineData("left", RoomObservationSource.Departure)]
    [InlineData("died", RoomObservationSource.AlsoHere)]
    public void ActiveRoster_AMonsterThatWalkedOut_IsADeparture(string why, RoomObservationSource expected)
    {
        using Session session = new();
        session.Replay("07-recovery-request");
        Assert.True(session.Combat.HasRoomNpc);
        JsonElement occupants = session.Gmcp.State.Snapshot("Anomaly.Room.Occupants")!.Value;
        long uid = occupants.GetProperty("mobs")[0].GetProperty("uid").GetInt64();
        string room = occupants.GetProperty("room").GetString()!;
        int before = session.Classifier.Current!.Value.Entities.Count;
        List<RoomObservationSource> sources = new();
        session.Classifier.EntitiesObserved += obs => sources.Add(obs.Source);

        session.Send("Anomaly.Mob.Gone", new { uid, room, why });

        Assert.Equal(expected, Assert.Single(sources));
        Assert.Equal(before - 1, session.Classifier.Current!.Value.Entities.Count);
    }

    // Walking out of a fight into an empty room: the text path reads that as a
    // room-change wipe, which tells the walker a fight was walked out on. With the
    // server's roster owned no such read came, and the walker walked on.
    [Fact]
    public void ActiveRoster_WalkingOutOfAFightIntoAnEmptyRoom_IsNoticed()
    {
        using Session session = new();
        session.Replay("07-recovery-request");
        Assert.True(session.Combat.HasRoomNpc);
        List<string> abandoned = new();
        session.Combat.EngagedTargetAbandoned += abandoned.Add;
        Dictionary<string, JsonElement> room = session.Gmcp.State.Snapshot("Anomaly.Room.Info")!.Value
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        session.Tracker.NoteMoveSent(Direction.N);

        session.Send("Anomaly.Session.Reset", new { scope = "room", room = "1,2" });
        room["room"] = JsonSerializer.SerializeToElement("1,2");
        session.Send("Anomaly.Room.Info", room);
        session.Send("Anomaly.Room.Occupants", new { room = "1,2", mobs = Array.Empty<object>(), players = Array.Empty<object>() });
        session.Text("Empty Hall\r\nObvious exits: south\r\n");

        Assert.NotEmpty(abandoned);
    }

    // With the roster on the text, the text display's "Also here:" is read before
    // its exits line, which is what lets the held room through.
    [Fact]
    public void ArrivingRoom_WithTheRosterOnTheText_IsConfirmedByTheTextDisplay()
    {
        using Session session = new();
        session.Replay("01-handshake");
        session.Gmcp.State.SetMode("Anomaly.Room.Occupants", GmcpMode.Shadow);
        Dictionary<string, JsonElement> room = session.Gmcp.State.Snapshot("Anomaly.Room.Info")!.Value
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        session.Send("Anomaly.Session.Reset", new { scope = "room", room = "1,2" });
        room["room"] = JsonSerializer.SerializeToElement("1,2");
        int entitiesWhenConfirmed = -1;
        session.Tracker.MoveConfirmed += () => entitiesWhenConfirmed = session.Classifier.Current?.Entities.Count ?? 0;
        session.Tracker.NoteMoveSent(Direction.N);

        session.Send("Anomaly.Room.Info", room);
        Assert.Equal(-1, entitiesWhenConfirmed);

        session.Text("Darkwood Forest\r\nAlso here: nasty baby green dragon.\r\nObvious exits: northwest, southeast\r\n");
        Assert.True(entitiesWhenConfirmed > 0);
        Assert.Equal(new RoomKey(1, 2), session.Tracker.State.CurrentRoom!.Key);
    }

    // A room too dark to see shows one line in place of its display: no "Also
    // here:" and no exits line to wait for, so that line lets the held room through,
    // and the darkness it reported stays.
    [Fact]
    public void ArrivingDarkRoom_WithTheRosterOnTheText_IsConfirmedByItsCantSeeLine()
    {
        using Session session = new();
        session.Replay("01-handshake");
        session.Gmcp.State.SetMode("Anomaly.Room.Occupants", GmcpMode.Shadow);
        Dictionary<string, JsonElement> room = session.Gmcp.State.Snapshot("Anomaly.Room.Info")!.Value
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        session.Send("Anomaly.Session.Reset", new { scope = "room", room = "1,2" });
        room["room"] = JsonSerializer.SerializeToElement("1,2");
        session.Tracker.NoteMoveSent(Direction.N);
        session.Send("Anomaly.Room.Info", room);
        Assert.Equal(RoomConfidence.Pending, session.Tracker.State.Confidence);

        session.Text("The room is very dark - you can't see anything\r\n");

        Assert.Equal(new RoomKey(1, 2), session.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(RoomConfidence.Confirmed, session.Tracker.State.Confidence);
        Assert.True(session.Tracker.IsInDarkRoom);
    }

    [Fact]
    public void CharacterReset_DropsOldMovementBeforeTheNewCharactersBaseline()
    {
        using Session session = new();
        session.Replay("01-handshake");
        session.Tracker.NoteMoveSent(Direction.N);
        session.Send("Anomaly.Session.Reset", new { scope = "all", reason = "character" });
        Assert.False(session.Tracker.HasQueuedMoves);
        Assert.Null(session.Tracker.State.CurrentRoom);
        session.Replay("01-handshake");
        session.Tracker.NoteMoveSent(Direction.N);
        session.Gmcp.Disconnected();
        session.Replay("09-reconnect");
        Assert.False(session.Tracker.HasQueuedMoves);
    }

    [Fact]
    public void ActiveStats_KeepUnprovidedIdentityLivesAndCpOnText()
    {
        using Session session = new();
        session.Replay("01-handshake");
        Assert.Equal(70, session.Stats.Strength);
        Assert.Equal(12, session.Stats.Level);
        Assert.Equal(24, session.Stats.ArmourClass);
        Assert.Equal(4, session.Stats.MaxArmourClass);
        session.StatParser.ObserveOutbound(Encoding.ASCII.GetBytes("stat\r"));
        session.Text("Name: Test Hero                  Lives/CP:           8/15\r\n");
        session.Text("Class: Warrior        Level: 1        Stealth:            65\r\n");
        session.Text("Strength:   3       Agility:   4          Tracking:     0\r\n");
        Assert.Equal("Test Hero", session.Stats.Name);
        Assert.Equal(8, session.Stats.Lives);
        Assert.Equal(15, session.Stats.Cp);
        Assert.Equal(70, session.Stats.Strength);
        Assert.Equal(12, session.Stats.Level);
    }

    [Fact]
    public void ActiveRoster_ReachesCombatModelsAndDeathClearsTheGate()
    {
        using Session session = new();
        session.Replay("07-recovery-request");
        RoomEntity monster = Assert.Single(session.Classifier.Current!.Value.Entities.Where(e => e.Kind == EntityKind.Monster));
        Assert.Equal(102, monster.RuntimeId);
        Assert.Equal(12, monster.MonsterNumber);
        Assert.True(session.Combat.HasRoomNpc);
        session.Send("Anomaly.Mob.Gone", new { uid = 102, room = session.Gmcp.State.CurrentRoom, why = "died" });
        Assert.Empty(session.Classifier.Current!.Value.Entities);
        Assert.False(session.Combat.HasRoomNpc);
    }

    [Fact]
    public void WontAndReconnect_RestoreTextThenFreshServerState()
    {
        using Session session = new();
        session.Replay("07-recovery-request");
        session.Gmcp.Disconnected();
        session.Text("[HP=9/MA=2]:\r\n");
        Assert.Equal(9, session.Player.Hp);
        Assert.Empty(session.Gmcp.State.StaticMonsters);
        Assert.Null(session.Classifier.Current);
        session.Replay("09-reconnect");
        Assert.True(session.Gmcp.State.RosterComplete);
        Assert.True(session.Gmcp.State.Owns("Anomaly.Char.Vitals"));
    }

    [Fact]
    public void RoomProjection_PreservesCommandTeleportsAndKnownRemoteActionRequirements()
    {
        JsonElement value = GmcpStateTests.Frames("01-handshake").First(frame => frame.TryGetProperty("pkg", out JsonElement package)
            && package.GetString() == "Anomaly.Room.Info").GetProperty("msg");
        Dictionary<string, JsonElement> fields = value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        List<Dictionary<string, JsonElement>> exits = value.GetProperty("exits").EnumerateArray().Select(e => e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value)).ToList();
        exits[0]["kind"] = JsonSerializer.SerializeToElement("hidden");
        exits[0]["hidden"] = JsonSerializer.SerializeToElement(true);
        fields["exits"] = JsonSerializer.SerializeToElement(exits);
        Room previous = new() { Key = new(1, 1), Name = "Town Square", Exits = new Dictionary<Direction, RoomExit>
        {
            [Direction.N] = new(new(1, 2), RoomExitHint.MultiActionHidden, "remote actions"),
            [Direction.Teleport] = new(new(9, 9), RoomExitHint.Teleport, null, TextCommands: new[] { "go portal" })
        } };
        Room projected = GmcpProjection.Room(JsonSerializer.SerializeToElement(fields), previous);
        Assert.Equal(RoomExitHint.MultiActionHidden, projected.Exits[Direction.N].Hint);
        Assert.Equal(new RoomKey(9, 9), projected.Exits[Direction.Teleport].Target);
        Assert.Equal(0u, projected.ExitMask & (1u << (int)Direction.Teleport));
    }

    // The server sends a lever's or a room command's door as a plain door or gate.
    // Taken as one, the walker sent the door handler at 8/909's portcullis (1000
    // picklocks/strength) and gave up, where `lift portcullis` opens it (report
    // paradigm-20261030-182928). A door the graph knows no lever for stays a door.
    [Theory]
    [InlineData("door", true, RoomExitHint.MultiActionHidden)]
    [InlineData("gate", true, RoomExitHint.MultiActionHidden)]
    [InlineData("door", false, RoomExitHint.Door)]
    public void RoomProjection_KeepsADoorTheGraphOpensByAction(string kind, bool lever, RoomExitHint expected)
    {
        RoomKey pathway = new(8, 909), stronghold = new(8, 910);
        MultiActionExitData lift = new(1, false, [new ExitAction(1, ["lift portcullis"], null, Rolled: true)]) { DoorOrGate = (pathway, Direction.W) };
        RoomExit known = lever
            ? new(stronghold, RoomExitHint.MultiActionHidden, "Door [1000 picklocks/strength]") { StatRequirement = 1000, MultiAction = lift }
            : new(stronghold, RoomExitHint.Door, "Door [1000 picklocks/strength]") { StatRequirement = 1000 };
        Room previous = new() { Key = pathway, Name = "Dragon's Teeth Hills, Pathway", Exits = new Dictionary<Direction, RoomExit> { [Direction.W] = known } };
        object closed = new { state = "locked", key = (int?)null, bashable = true };
        object shut = new { item = (int?)null, minLevel = (int?)null, maxLevel = (int?)null, @class = (int?)null, race = (int?)null };
        JsonElement value = JsonSerializer.SerializeToElement(new
        {
            room = "8,909", name = "Dragon's Teeth Hills, Pathway", light = 0, spell = (int?)null,
            exits = new object[]
            {
                new { dir = "w", to = "8,910", kind, hidden = false, found = true,
                      door = kind == "door" ? closed : null, gate = kind == "gate" ? shut : null,
                      toll = (object?)null, phrase = (string?)null },
            },
        });

        RoomExit projected = GmcpProjection.Room(value, previous).Exits[Direction.W];

        Assert.Equal(expected, projected.Hint);
        Assert.Equal(lever ? lift : null, projected.MultiAction);
    }

    // An exit taken by a command (`go path`, `go manhole`) is in the server's exits
    // with "kind":"text" but never in the display's "Obvious exits:" (exports
    // gmcp-20261029-150551 and gmcp-20261029-152626, five rooms). Read as shown, the
    // shadow compare flagged every such room and the active room read the exit as a
    // plain one open to step through.
    [Fact]
    public void RoomObservation_LeavesOutAnExitTakenByACommand()
    {
        JsonElement value = JsonSerializer.SerializeToElement(new
        {
            room = "1,1680", name = "Darkwood Forest",
            exits = new object[]
            {
                new { dir = "ne", to = "1,1403", kind = "normal", hidden = false, found = true, door = (object?)null, gate = (object?)null, toll = (object?)null, phrase = (string?)null },
                new { dir = "sw", to = "1,1403", kind = "text", hidden = false, found = true, door = (object?)null, gate = (object?)null, toll = (object?)null, phrase = "go path" },
            },
        });

        Assert.Equal(new[] { Direction.NE }, GmcpProjection.Observation(value).Exits.ToArray());
    }

    // The server names no kind for a trap, a teleport or a ticket (the kinds it sends
    // are in export gmcp-20261029-143336), so it calls those exits "normal". Read as
    // plain, they lost what the walker acts on: the trap's disarm, the ticket gate and
    // its item, a boat's fare. An exit the server does name keeps the server's reading.
    [Theory]
    [InlineData(RoomExitHint.Trap)]
    [InlineData(RoomExitHint.Teleport)]
    [InlineData(RoomExitHint.Ticket)]
    public void RoomProjection_KeepsTheGraphsTrapTeleportAndTicketExits(RoomExitHint known)
    {
        JsonElement value = GmcpStateTests.Frames("01-handshake").First(frame => frame.TryGetProperty("pkg", out JsonElement package)
            && package.GetString() == "Anomaly.Room.Info").GetProperty("msg");
        JsonElement edge = value.GetProperty("exits")[0];
        Assert.Equal("normal", edge.GetProperty("kind").GetString());
        Direction direction = Enum.Parse<Direction>(edge.GetProperty("dir").GetString()!, ignoreCase: true);
        RoomKey target = GmcpProjection.Key(edge.GetProperty("to").GetString()!);
        Room previous = new() { Key = GmcpProjection.Key(value.GetProperty("room").GetString()!), Name = "Town Square",
            Exits = new Dictionary<Direction, RoomExit>
            {
                [direction] = new(target, known, "raw", KeyItemId: 1234, TrapDamage: 40, FareCopper: 500,
                                  TextCommands: new[] { "go portal" }),
            } };

        RoomExit projected = GmcpProjection.Room(value, previous).Exits[direction];

        Assert.Equal(known, projected.Hint);
        Assert.Equal(1234, projected.KeyItemId);
        Assert.Equal(40, projected.TrapDamage);
        Assert.Equal(500, projected.FareCopper);
        Assert.Equal(new[] { "go portal" }, projected.TextCommands);

        // Once the exit leads somewhere else, the graph's reading no longer applies.
        Room moved = previous with { Exits = new Dictionary<Direction, RoomExit>
            { [direction] = previous.Exits[direction] with { Target = new RoomKey(99, 99) } } };
        Assert.Equal(RoomExitHint.None, GmcpProjection.Room(value, moved).Exits[direction].Hint);
    }

    [Fact]
    public void RoomSnapshot_UpdatesGraphAndPreservesLockedAndHiddenExits()
    {
        using Session session = new();
        session.Replay("02-walk");
        // The recording leaves out the room text a real server prints after each step.
        session.Text("Rat Warren\r\nAlso here: large giant rat, large giant rat.\r\nObvious exits: up\r\n");
        Assert.Equal(GmcpProjection.Key(session.Gmcp.State.CurrentRoom!), session.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(RoomConfidence.Confirmed, session.Tracker.State.Confidence);
        Assert.NotNull(session.Graph.GetRoom(session.Tracker.State.CurrentRoom.Key));
    }

    // Shadow is a fallback: the server's vitals stand in until the text's first prompt,
    // and from it the text is the source and the server's vitals are only compared.
    [Fact]
    public void ShadowVitals_StandInUntilTheFirstPrompt_ThenAreOnlyCompared()
    {
        using Session session = new(active: false);
        session.Replay("01-handshake");
        Assert.True(session.Player.HasPromptData);
        Assert.Equal(150, session.Player.Hp);
        session.Text("[HP=4]:\r\n");
        Assert.Equal(4, session.Player.Hp);
        // Compared as the server's vitals arrive, against the prompt before them.
        session.Send("Anomaly.Char.Vitals", session.Gmcp.State.Snapshot("Anomaly.Char.Vitals")!.Value);
        Assert.Equal(4, session.Player.Hp);
        Assert.Contains(session.Log.Snapshot(), item => item.Source == "GMCP shadow" && item.Message.Contains("Char.Vitals.hp", StringComparison.Ordinal));
        Assert.DoesNotContain(session.Log.Snapshot(), item => item.Source == "GMCP shadow" && item.Message.Contains("drop", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ShadowVisibility_UsesTheTextWhenAHiddenOccupantIsActuallyVisible()
    {
        using Session session = new(active: false);
        session.Replay("07-recovery-request");
        session.Send("Anomaly.Room.Occupants", new { room = session.Gmcp.State.CurrentRoom, mobs = new[] { new { uid = 102, hidden = true } }, players = Array.Empty<object>() });
        session.Text("Also here: large giant rat.\r\n");
        Assert.DoesNotContain(session.Log.Snapshot(), entry => entry.Source == "GMCP shadow");
    }

    [Fact]
    public void CurrentMonsterHp_ComesFromLiveBaselineAndDeltaWithoutDoubleCountingTextDamage()
    {
        using Session session = new();
        session.Replay("07-recovery-request");
        RoomEntity monster = session.Classifier.Current!.Value.Entities.Single(e => e.Kind == EntityKind.Monster);
        Assert.Equal(12, session.Hp.Estimate(monster.RawName));
        session.Send("Anomaly.Mob.Update", new { uid = monster.RuntimeId, room = session.Gmcp.State.CurrentRoom, full = false, hp = 5 });
        Assert.Equal(5, session.Hp.Estimate(monster.RawName));
        Assert.Equal(5, session.Hp.TargetOf(monster.RawName)!.Value.Hp);
    }

    [Fact]
    public void ActiveStatus_UpdatesConditionsAndStealthAndReleasesThemOnFallback()
    {
        using Session session = new();
        session.Replay("01-handshake");
        Dictionary<string, JsonElement> value = session.Gmcp.State.Snapshot("Anomaly.Char.Status")!.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        foreach (string field in new[] { "blind", "poisoned", "diseased", "confused", "sneaking" }) value[field] = JsonSerializer.SerializeToElement(true);
        session.Send("Anomaly.Char.Status", value);
        Assert.True(session.Conditions.IsBlinded);
        Assert.True(session.Conditions.IsPoisoned);
        Assert.True(session.Conditions.IsDiseased);
        Assert.True(session.Conditions.IsConfused);
        Assert.True(session.Stealth.IsSneaking);
        session.Gmcp.Disconnected();
        Assert.False(session.Conditions.IsBlinded);
    }

    [Fact]
    public void LairDeadline_IsEligibilityForCurrentRoomAndExpiresWithRoomState()
    {
        using Session session = new();
        session.Replay("07-recovery-request");
        RoomKey room = GmcpProjection.Key(session.Gmcp.State.CurrentRoom!);
        Assert.True(session.Gmcp.LairEligibility(room).Known);
        Assert.NotNull(session.Gmcp.LairEligibility(room).EligibleAt);
        session.Send("Anomaly.Session.Reset", new { scope = "room", room = "1,999" });
        Assert.False(session.Gmcp.LairEligibility(room).Known);
    }

    // The server reports combat only when it flips, so a long fight carries one
    // status with combat on; its rounds' lines are what show the stall watchdog the
    // fight is still going.
    [Fact]
    public void ActiveStatus_ALongFightsLinesKeepTheStallWatchdogFromClearingIt()
    {
        ManualClock clock = new();
        using Session session = new(clock: clock);
        session.Replay("01-handshake");
        List<string> wire = new();
        session.Combat.SetWireSender(bytes => wire.Add(Encoding.Latin1.GetString(bytes)));
        Dictionary<string, JsonElement> status = session.Gmcp.State.Snapshot("Anomaly.Char.Status")!.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        status["combat"] = JsonSerializer.SerializeToElement(true);
        session.Send("Anomaly.Char.Status", status);

        for (int round = 0; round < 4; round++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            session.Text("You hit the giant rat for 5 damage!\r\n");
            session.Combat.OnCombatTick();
        }

        Assert.True(session.Player.InCombat);
        Assert.Empty(wire);
    }

    // A room reset only takes the room's snapshots away. Raised as a change to every
    // package, it applied the cached inventory and party again on each step, as a
    // fresh `i` read and a `par` reply that answered waits with data from before
    // the move.
    [Fact]
    public void RoomReset_DoesNotReplayTheInventoryOrPartyAsFreshReads()
    {
        using Session session = new();
        session.Replay("13-milestone2");
        Assert.True(session.Gmcp.State.Owns("Anomaly.Items.List"));
        Assert.True(session.Gmcp.State.Owns("Anomaly.Group.Members"));
        int reads = 0, parReplies = 0;
        session.Inventory.FullInventoryParsed += () => reads++;
        session.Party.ParReplyRead += () => parReplies++;

        session.Send("Anomaly.Session.Reset", new { scope = "room", room = "1,2" });

        Assert.Equal(0, reads);
        Assert.Equal(0, parReplies);
        Assert.NotEqual(DateTimeOffset.MinValue, session.Inventory.Snapshot.LastUpdated);
    }

    // The server's party snapshot owns the roster, but only the text says why a
    // member went: left behind, the leader goes back for them, and either way they
    // stay in the grace window a returning member is re-invited from.
    [Fact]
    public void ActiveGroup_TheGamesDepartureLines_StillSayWhyAMemberWent()
    {
        using Session session = new();
        session.Replay("13-milestone2");
        Assert.True(session.Party.State.SelfIsLeader);
        List<string> leftBehind = new();
        session.Party.MemberLeftBehind += leftBehind.Add;

        session.Text("Gpriest is no longer following you.\r\n");

        Assert.Equal(["Gpriest"], leftBehind);
        Assert.True(session.Party.WasRecentlyPartied("Gpriest"));
        Assert.Contains(session.Party.State.Members, m => m.Name == "Gpriest");   // the roster waits for the snapshot
    }

    // The server's inventory sets the coins but carries no weight, which comes from
    // the text's `i`. Coins weigh one per three, so a snapshot that moves the purse
    // moves the weight as a text coin line does; with the snapshot owned, those
    // lines don't.
    [Fact]
    public void ActiveItems_CoinsMoveTheWeightAndADepositIsReckonedAtOnce()
    {
        using Session session = new();
        session.Replay("13-milestone2");
        session.Text("You are carrying 30 copper farthings.\r\nYou have no keys.\r\nWealth: 30 copper farthings\r\nEncumbrance: 300/2000 - None [15%]\r\n");
        Dictionary<string, JsonElement> items = session.Gmcp.State.Snapshot("Anomaly.Items.List")!.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        Dictionary<string, JsonElement> coins = items["coins"].EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        int weightBefore = session.Inventory.Snapshot.Encumbrance.CurrentWeight;
        long coinsBefore = session.Inventory.Snapshot.Currency.TotalCoinCount;
        coins["copper"] = JsonSerializer.SerializeToElement(coins["copper"].GetInt32() + 300);
        items["coins"] = JsonSerializer.SerializeToElement(coins);

        session.Send("Anomaly.Items.List", items);

        Assert.Equal(weightBefore + (int)((coinsBefore + 300) / 3 - coinsBefore / 3), session.Inventory.Snapshot.Encumbrance.CurrentWeight);

        // An auto-deposit is reckoned when it's sent: the snapshot only follows the
        // deposit's text, and the walk home is planned before then.
        long wealth = session.Inventory.Snapshot.Currency.TotalCopperValue;
        session.Inventory.NoteAutoDeposit(100);
        Assert.Equal(wealth - 100, session.Inventory.Snapshot.Currency.TotalCopperValue);
    }

    // Handing the inventory back to text keeps the weight reading, which was the
    // text's all along; zeroed, it left coin collection ungated until the next `i`.
    [Fact]
    public void ItemsHandedBackToText_KeepTheWeightReading()
    {
        using Session session = new();
        session.Replay("13-milestone2");
        session.Text("You are carrying 30 copper farthings.\r\nYou have no keys.\r\nWealth: 30 copper farthings\r\nEncumbrance: 300/2000 - None [15%]\r\n");
        int weight = session.Inventory.Snapshot.Encumbrance.CurrentWeight;
        Assert.True(weight > 0);

        session.Gmcp.State.SetMode("Anomaly.Items.List", GmcpMode.Off);

        Assert.Equal(weight, session.Inventory.Snapshot.Encumbrance.CurrentWeight);
        Assert.Equal(2000, session.Inventory.Snapshot.Encumbrance.MaxWeight);
    }

    // With the server's party owned a `par` reply is dropped, so the poller asks the
    // server for its party snapshot instead, which answers as the reply would. Off
    // the server's party, `par` goes out as before.
    [Fact]
    public void ActiveGroup_ThePartyPollAsksTheServerInsteadOfSendingPar()
    {
        using Session session = new();
        session.Replay("13-milestone2");
        List<string> requests = new();
        session.Gmcp.SetSender((package, payload) =>
        {
            requests.Add(package + " " + JsonSerializer.Serialize(payload));
            return Task.CompletedTask;
        });
        List<string> wire = new();
        using PartyPoller poller = new(session.Chat, session.Party.State, session.Party, session.Log, schedule: new ManualTimers().Arm);
        poller.SetWireSender(bytes => wire.Add(Encoding.Latin1.GetString(bytes)));
        poller.AskServerForParty = session.Gmcp.RequestGroup;

        Assert.True(poller.RequestPar("a party heal check"));
        Assert.Empty(wire);
        Assert.Contains(requests, r => r.StartsWith("Anomaly.Request", StringComparison.Ordinal) && r.Contains("Anomaly.Group.Members", StringComparison.Ordinal));

        session.Gmcp.State.SetMode("Anomaly.Group.Members", GmcpMode.Off);
        session.Party.State.IsInParty = true;
        Assert.True(poller.RequestPar("a party heal check"));
        Assert.Equal(["par\r"], wire);
    }

    // The server lists every occupant; "Also here:" leaves out a hidden player until
    // a search reveals them, and never lists the leader we follow. The roster the
    // client acts on is the one the text would give (GAME_MECHANICS "Hiding" and
    // "Co-location and the `Also here:` line").
    [Fact]
    public void ActiveRoster_LeavesOutAHiddenPlayerAndTheLeaderWeFollow()
    {
        using Session session = new();
        session.Replay("07-recovery-request");
        session.Party.State.IsInParty = true;
        session.Party.State.LeaderName = "Boss Hogg";

        session.Send("Anomaly.Room.Occupants", new
        {
            room = session.Gmcp.State.CurrentRoom,
            mobs = new[] { new { uid = 102, hidden = false } },
            players = new[] { new { name = "Sneaky", hidden = true }, new { name = "Boss", hidden = false }, new { name = "Bystander", hidden = false } },
        });

        Assert.Equal(["Bystander"], session.Classifier.Current!.Value.Entities.Where(e => e.Kind == EntityKind.Player).Select(e => e.RawName));

        session.Send("Anomaly.Room.Occupants", new
        {
            room = session.Gmcp.State.CurrentRoom,
            mobs = new[] { new { uid = 102, hidden = false } },
            players = new[] { new { name = "Sneaky", hidden = false } },
        });
        Assert.Contains(session.Classifier.Current!.Value.Entities, e => e.RawName == "Sneaky");   // a search revealed them
    }

    // Tools → GMCP Monitor records the session from the handshake on and exports it
    // in the capture format the tests replay: an export of a walk replayed into a
    // fresh client gives that client the same server messages and text, escape
    // sequences included, in the same order.
    [Fact]
    public void GmcpMonitor_ExportReplaysToTheSameState()
    {
        string folder = Path.Combine(Path.GetTempPath(), "mudplay-gmcp-monitor-" + Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try
        {
            using Session recorded = new();
            recorded.ReplayCapture("02-walk");
            Assert.True(recorded.Monitor.IsRecording);
            string export = recorded.Monitor.Export(text => text, "test");
            string path = Path.Combine(folder, "walk.jsonl");
            File.WriteAllText(path, export);

            using Session replayed = new();
            replayed.ReplayCaptureFile(path);

            Assert.Equal(
                recorded.Monitor.Snapshot().Select(f => (f.FromServer, f.Package, f.Body)),
                replayed.Monitor.Snapshot().Select(f => (f.FromServer, f.Package, f.Body)));
            Assert.Contains(recorded.Monitor.Snapshot(), f => f.Package is null && f.Body.Contains("Obvious exits", StringComparison.Ordinal));
            Assert.Contains(recorded.Monitor.Snapshot(), f => f.Package == "Anomaly.Room.Info");
            Assert.Contains("\\u001b[", export, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // The monitor is off until the player turns it on, so it isn't one more log
    // growing on its own: off, a session records nothing; turned on mid-session,
    // it records from then.
    [Fact]
    public void GmcpMonitor_RecordsNothingUntilTurnedOn()
    {
        using Session session = new();
        session.Monitor.Enabled = false;
        session.Replay("01-handshake");
        Assert.Empty(session.Monitor.Snapshot());
        Assert.False(session.Monitor.IsRecording);

        session.Monitor.Enabled = true;
        session.Text("You notice a rusty dagger here.\r\n");

        GmcpMonitor.Frame frame = Assert.Single(session.Monitor.Snapshot());
        Assert.Contains("rusty dagger", frame.Body, StringComparison.Ordinal);
        Assert.True(session.Monitor.IsRecording);
        Assert.False(new GmcpMonitor().Enabled);
    }

    // The export masks what the bug report masks (here the account name the login
    // prompt echoes), in the text and in the server's messages alike.
    [Fact]
    public void GmcpMonitor_ExportIsMasked()
    {
        using Session session = new();
        session.ReplayCapture("02-walk");

        string export = session.Monitor.Export(text => text.Replace("gmage", "[redacted]", StringComparison.OrdinalIgnoreCase), "test");

        Assert.DoesNotContain("gmage", export, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"dir\":\"c2s\",\"kind\":\"text\"", export, StringComparison.Ordinal);
    }

    // The server's combat flag is its auto-attack state: a cast mid-fight turns it
    // off with the monster still standing, as *Combat Off* does. It starts a fight
    // but doesn't end one; the room clearing does.
    [Fact]
    public void ActiveStatus_CombatOffMidFight_LeavesTheFightOnUntilTheRoomClears()
    {
        using Session session = new();
        session.Replay("07-recovery-request");
        Assert.True(session.Combat.HasRoomNpc);
        Dictionary<string, JsonElement> status = session.Gmcp.State.Snapshot("Anomaly.Char.Status")!.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        status["combat"] = JsonSerializer.SerializeToElement(true);
        session.Send("Anomaly.Char.Status", status);
        Assert.True(session.Player.InCombat);

        status["combat"] = JsonSerializer.SerializeToElement(false);
        session.Send("Anomaly.Char.Status", status);
        Assert.True(session.Player.InCombat);
        Assert.False(session.Combat.IsServerEngaged);

        session.Send("Anomaly.Mob.Gone", new { uid = 102, room = session.Gmcp.State.CurrentRoom, why = "died" });
        Assert.False(session.Player.InCombat);
    }

    // A monster's swing at us is a fight whatever the server's flag says.
    [Fact]
    public void ActiveStatus_ACombatLineStartsTheFightEvenWithTheServerFlagOff()
    {
        using Session session = new();
        session.Replay("07-recovery-request");
        Assert.False(session.Player.InCombat);

        session.Text("The large giant rat bites you for 3 damage!\r\n");

        Assert.True(session.Player.InCombat);
    }

    [Fact]
    public void ActiveStealth_IsNotClearedByTheTextArrivalWatchdog()
    {
        using Session session = new();
        session.Replay("01-handshake");
        Dictionary<string, JsonElement> status = session.Gmcp.State.Snapshot("Anomaly.Char.Status")!.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        status["sneaking"] = JsonSerializer.SerializeToElement(true);
        session.Send("Anomaly.Char.Status", status);
        session.Stealth.NoteRoomChanged();
        session.Stealth.NoteRoomChanged();
        Assert.True(session.Stealth.IsSneaking);
        Assert.True(session.Stealth.IsStealthedHere);
    }

    [Fact]
    public async Task ExplicitRefresh_RequestsSnapshotsAndMonsterBaselines()
    {
        using Session session = new();
        session.Replay("07-recovery-request");
        List<(string Package, JsonElement Payload)> requests = new();
        session.Gmcp.SetSender((package, payload) => { requests.Add((package, JsonSerializer.SerializeToElement(payload))); return Task.CompletedTask; });
        await session.Gmcp.RefreshAsync();
        Assert.Contains(requests, r => r.Payload.GetProperty("package").GetString() == "Anomaly.Mob.Info" && r.Payload.GetProperty("uid").GetInt64() == 102);
        Assert.Contains(requests, r => r.Payload.GetProperty("package").GetString() == "Anomaly.Char.Vitals");
    }

    [Fact]
    public void Settings_RoundTripPerPackageModesWithoutAffectingOtherBoards()
    {
        BbsProfile profile = new() { GmcpModes = new() { ["Anomaly.Char.Vitals"] = GmcpMode.Active } };
        BbsProfile restored = JsonSerializer.Deserialize<BbsProfile>(JsonSerializer.Serialize(profile))!;
        Assert.Equal(GmcpMode.Active, restored.GmcpModes["Anomaly.Char.Vitals"]);
        Assert.Empty(new BbsProfile().GmcpModes);
    }
}
