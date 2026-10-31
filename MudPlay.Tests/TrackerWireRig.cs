using System.IO;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;

namespace MudPlay.Tests;

// The room tracker and the recovery gate wired as the client wires them, with a
// small game on the other end of the wire. Every command goes out through a wire
// sender (typed by the test, or sent by the walker) and through the outbound
// observer, and the game answers in text fed to a real terminal emulator: the
// echo on the prompt row, then a room display or a refusal, then a new prompt.
// The lines reach the tracker the way they do live: LineExtractor into
// RoomDisplayParser, MovementRefusalDetector, InboundMoveEchoScanner, DeathDetector,
// and the MessageRouter patterns AppServices subscribes it to. Rows are stamped on a
// manual clock, which moves on between commands.
//
// The game's world is the room graph itself unless a test says otherwise: a room
// shows its name and its exits other than text exits, a move with no exit is
// refused, and an exit leads where the graph says.
//
// Each chunk the game sends is handled as MainWindowViewModel handles one: the
// prompt scanner reads it first, then the terminal, then whatever was posted to the
// UI thread meanwhile (Post) runs.
internal sealed class TrackerWireRig : IDisposable
{
    private readonly string _prompt;

    private readonly string _root;
    private readonly Queue<string> _toGame = new();

    public ManualClock Clock { get; } = new();

    // The gate's timeouts (the wait for a reverse move's landing), run when a test
    // says the time is up.
    public ManualTimers Timers { get; } = new();
    public LogService Log { get; } = new(capacity: 20000);
    public GameDataCache Cache { get; }
    public RoomGraphManager Graph { get; }
    public RoomTracker Tracker { get; }
    public EngineRecoveryGate Gate { get; }
    public AutoWalkManager Walker { get; }
    public List<string> Sent { get; } = new();
    public List<RecoveryFailedEvent> Failures { get; } = new();
    public MovementCoordinator Coordinator { get; }
    // The rows the game's text comes out as, for a component the test attaches beside
    // the tracker (as AppServices attaches the condition tracker).
    public LineExtractor Lines => _lines;
    public MessageRouter Router { get; } = new();
    public WirePromptScanner Prompts { get; } = new();
    private readonly Queue<Action> _posted = new();

    private readonly TerminalEmulator _emulator;
    private readonly LineExtractor _lines;
    private readonly RoomDisplayParser _display;
    private readonly MovementRefusalDetector _refusals;
    private readonly InboundMoveEchoScanner _echoes;
    private readonly OutboundMovementObserver _outbound;
    private readonly FollowMoveObserver _follow;
    private readonly Game.DeathDetector _deaths;

    // Where the character really is.
    public RoomKey Position { get; set; }

    // Exits a room shows that the graph doesn't know (a stale map).
    public Dictionary<RoomKey, Direction[]> ExtraShownExits { get; } = new();

    // Exits of the graph a room doesn't show (a hidden exit, a closed door).
    public Dictionary<RoomKey, Direction[]> UnshownExits { get; } = new();

    // Rooms the game shows as too dark to see.
    public HashSet<RoomKey> DarkRooms { get; } = new();

    // A one-off answer to the next command, in place of the game's own; the
    // character stays put.
    public Queue<string> NextAnswers { get; } = new();

    // What the next move that goes through shows in place of the room it reaches
    // ("You are blind.").
    public Queue<string> NextMoveShows { get; } = new();

    // The game's own answer to a command a test models (a room command, a gate that
    // refuses), or null to answer it as the room graph does; the character stays put.
    public Func<string, string?>? GameAnswer { get; set; }

    // How a room's exits line names an exit ("closed gate north"), or null for the
    // bare direction.
    public Func<RoomKey, Direction, string?>? ExitWords { get; set; }

    // Whether the prompt row echoes each command, as a statline the client can
    // read does. Off, the tracker never sees an echo and times arrivals instead.
    public bool Echo { get; set; } = true;

    // How long after a command the game answers it: one hop, for a move.
    public TimeSpan AnswerDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    // prompt: the statline the game prints; the default isn't one the prompt
    // scanner's class-default pattern reads. paradigm: the set is a Paradigm realm's
    // (Info.json Legit 2), where `rm` names the room. tbinfoJson: the set's TBInfo,
    // for rooms whose commands the graph reads.
    public TrackerWireRig(string roomsJson, string prompt = "[HP=100/100]:", bool paradigm = false,
        string? tbinfoJson = null)
    {
        _prompt = prompt;
        _root = Path.Combine(Path.GetTempPath(), "mudplay-wire-rig-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), roomsJson);
        if (paradigm) File.WriteAllText(Path.Combine(_root, "alpha", "Info.json"), "[{\"Legit\":2}]");
        if (tbinfoJson is not null) File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"), tbinfoJson);
        Cache = new GameDataCache(_root);
        Cache.SwitchSet("alpha");
        TBInfoStore? tbinfo = null;
        if (tbinfoJson is not null)
        {
            tbinfo = new TBInfoStore(Cache);
            tbinfo.OnActiveSetChanged("alpha");
        }
        Graph = new RoomGraphManager(Cache, Log, tbinfo);
        Graph.OnActiveSetChanged("alpha");

        // Each read moves the clock one tick, as the wall clock does between two rows:
        // a row stamped like the prompt row before it reads as that prompt's echo.
        _emulator = new TerminalEmulator(80, 25, () =>
        {
            Clock.Advance(TimeSpan.FromTicks(1));
            return Clock.Now;
        });
        _lines = new LineExtractor(_emulator, Clock.Timestamp);
        DefaultPatterns.Seed(Router);
        _lines.LineEmitted += Router.Dispatch;

        Tracker = new RoomTracker(Graph, Log, () => Clock.Now);
        Gate = new EngineRecoveryGate(Graph, Tracker, Log, timeoutSchedule: Timers.Arm);
        Tracker.SetEngineAttachedProbe(() => Gate.HasAttachedEngine);
        Graph.GraphReloaded += () => Tracker.OnGraphReloaded();
        Gate.RecoveryFailed += Failures.Add;

        // The same subscriptions AppServices makes for the tracker.
        Router.Subscribe(KnownPatterns.DirectionFailed, _ => Tracker.NoteDirectionFailed());
        Router.Subscribe(KnownPatterns.RoomPitchBlack, _ => Tracker.NoteDarkRoomEntered());
        Router.Subscribe(KnownPatterns.RoomVeryDark, _ => Tracker.NoteDarkRoomEntered());
        Router.Subscribe(KnownPatterns.BlindMoveStarved, _ => Tracker.NoteBlindMove());
        _follow = new FollowMoveObserver(Router, Tracker, Log);

        _display = new RoomDisplayParser(_lines, Tracker, Log);
        _display.RoomParsed += Gate.OnRoomObserved;
        _refusals = new MovementRefusalDetector(_lines, Tracker, Log);
        _echoes = new InboundMoveEchoScanner(_lines, Tracker);
        _outbound = new OutboundMovementObserver(Tracker, Log);
        _deaths = new Game.DeathDetector(Tracker, Log);
        _deaths.AttachLineExtractor(_lines);

        Coordinator = new MovementCoordinator(Log);
        Walker = new AutoWalkManager(Graph, new BfsMapper(Graph), Tracker, Coordinator, log: Log, recovery: Gate);
        Walker.SetWireSender(Send);

        _emulator.Feed(Encoding.Latin1.GetBytes(_prompt));
    }

    // What the client puts on the wire, typed or an engine's: the outbound observer
    // reads it as SendUserInput has it read, and the game answers on Pump.
    public void Send(byte[] bytes)
    {
        LastSentAt = Clock.Now;
        string command = Encoding.Latin1.GetString(bytes).TrimEnd('\r', '\n');
        Sent.Add(command);
        _outbound.ObserveOutbound(bytes);
        _toGame.Enqueue(command);
    }

    // A command typed at the keyboard, a moment after whatever was shown last.
    public void Type(string command)
    {
        Clock.Advance(TimeSpan.FromMilliseconds(200));
        Send(Encoding.Latin1.GetBytes(command + "\r"));
    }

    // Type a command and let the game answer everything sent until the wire is quiet.
    public void Do(string command)
    {
        Type(command);
        Pump();
    }

    // The game answers each command in order, a little later each time; an answer
    // that has the walker send its next step is answered in turn.
    public void Pump(int limit = 2000)
    {
        while (_toGame.Count > 0 && limit-- > 0)
        {
            Clock.Advance(AnswerDelay);
            Answer(_toGame.Dequeue());
        }
    }

    // The party leader moves and the game drags us along with nothing typed: the
    // follow line, then the room we're pulled into.
    public void Drag(Direction dir)
    {
        Clock.Advance(TimeSpan.FromMilliseconds(400));
        if (!TryMove(dir)) throw new InvalidOperationException($"no {dir} exit from {Position}");
        Feed($"\r\n -- Following your Party leader {dir.ToLongName()} --\r\n" + RoomText(Position) + _prompt);
    }

    // The room we stand in, shown with nothing typed (logging in, a respawn). It
    // starts on a line of its own: on the prompt row it would read as an echo.
    public void ShowRoom() => Feed("\r\n" + RoomText(Position) + _prompt);

    // A line of the game's with nothing typed, and a prompt after it.
    public void Show(string line) => Feed("\r\n" + line + "\r\n" + _prompt);

    // The link drops: the game answers nothing more of what was sent.
    public void DropLink() => _toGame.Clear();

    // Work for the UI thread, run once the chunk being handled is done.
    public void Post(Action action) => _posted.Enqueue(action);

    private void Answer(string command)
    {
        StringBuilder text = new();
        if (Echo) text.Append(command);         // the echo, on the prompt row
        text.Append("\r\n");
        if (NextAnswers.TryDequeue(out string? scripted))
            text.Append(scripted).Append("\r\n");
        else if (GameAnswer?.Invoke(command) is { } own)
            text.Append(own).Append("\r\n");
        else if (command is "l" or "look")
            text.Append(RoomText(Position));
        else if (DirectionExtensions.TryFromToken(command, out Direction dir))
            text.Append(TryMove(dir) ? Arrival() : "There is no exit in that direction!\r\n");
        else if (TryTextMove(command))
            text.Append(Arrival());
        text.Append(_prompt);
        Feed(text.ToString());
    }

    private string Arrival() =>
        NextMoveShows.TryDequeue(out string? shown) ? shown + "\r\n" : RoomText(Position);

    private bool TryMove(Direction dir)
    {
        if (Graph.GetRoom(Position) is not { } room || !room.Exits.TryGetValue(dir, out RoomExit exit)) return false;
        Position = exit.Target;
        return true;
    }

    private bool TryTextMove(string command)
    {
        if (Graph.GetRoom(Position) is not { } room) return false;
        foreach (RoomExit exit in room.Exits.Values)
            if (exit.TextCommands is { } commands && commands.Any(c => c.Equals(command, StringComparison.OrdinalIgnoreCase)))
            {
                Position = exit.Target;
                return true;
            }
        return false;
    }

    private string RoomText(RoomKey key)
    {
        if (DarkRooms.Contains(key)) return "The room is very dark - you can't see anything\r\n";
        Room room = Graph.GetRoom(key) ?? throw new InvalidOperationException($"no room {key}");
        IEnumerable<Direction> shown = room.Exits
            .Where(e => e.Value.Hint != RoomExitHint.Text)
            .Select(e => e.Key);
        if (ExtraShownExits.TryGetValue(key, out Direction[]? extra)) shown = shown.Concat(extra);
        if (UnshownExits.TryGetValue(key, out Direction[]? hidden)) shown = shown.Except(hidden);
        string exits = string.Join(", ", shown.Distinct().OrderBy(d => (int)d)
            .Select(d => ExitWords?.Invoke(key, d) ?? d.ToLongName()));
        return $"\u001b[1;36m{room.Name}\u001b[0m\r\nObvious exits: {(exits.Length == 0 ? "NONE!" : exits)}\r\n";
    }

    // When the client last put a command on the wire, and last read from the game,
    // as the telnet client keeps them.
    public DateTimeOffset LastSentAt { get; private set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastReadAt { get; private set; } = DateTimeOffset.MinValue;

    private void Feed(string text)
    {
        LastReadAt = Clock.Now;
        byte[] chunk = Encoding.Latin1.GetBytes(text);
        Prompts.Append(chunk);
        _emulator.Feed(chunk);
        for (int limit = 1000; _posted.Count > 0 && limit-- > 0;) _posted.Dequeue()();
    }

    public RoomKey? Believed => Tracker.State.CurrentRoom?.Key;

    public void Dispose()
    {
        _follow.Dispose();
        _deaths.Dispose();
        _display.Dispose();
        _refusals.Dispose();
        _echoes.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    // ----- room data ---------------------------------------------------

    // One room of a Rooms.json, exits as "dir" → "map/room" (or "map/room (Text: go path)").
    public static string Room(int map, int room, string name, params (string Dir, string To)[] exits)
    {
        string[] slots = { "N", "S", "E", "W", "NE", "NW", "SE", "SW", "U", "D" };
        Dictionary<string, string> bySlot = exits.ToDictionary(e => e.Dir, e => e.To);
        string slotsJson = string.Join(",", slots.Select(s => $"\"{s}\":\"{(bySlot.TryGetValue(s, out string? to) ? to : "0")}\""));
        return $"{{\"Map Number\":{map},\"Room Number\":{room},\"Name\":\"{name}\",\"Light\":0,\"Shop\":0,\"Lair\":\"\",\"Delay\":0,{slotsJson}}}";
    }

    public static string Rooms(IEnumerable<string> rooms) => "[" + string.Join(",\n", rooms) + "]";
}
