using System.Text;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A small gang house played out against the real Roomba sweep manager: three rooms in
// a line (A, a weapons room; C between; B, an armour room), with the game's side kept
// here. Floors that `get` and `drop` change, displays printed from them as the whole
// room block (name, description, floor list, `Obvious exits:`), a search that
// finds what is hidden, and the game's own rules for a pickup: copies in plain sight
// are taken before a hidden stack, a hidden stack only once a search has found it, and
// on Paradigm a counted `get` for more than is there is refused whole. The bytes go
// through the emulator and the line extractor, echoes included.
internal sealed class RoombaHouse : IDisposable
{
    // The sweep's settle, pacing and backoff timers, the clock its pacing reads,
    // and the game's prompts as it reads them.
    private readonly ManualTimers _sweepTimers = new();
    private DateTimeOffset _sweepNow = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly WirePromptScanner _sweepPrompt = new();

    // The pace between queued commands runs out.
    private void PaceOut()
    {
        _sweepNow += GhSweepManager.MinCommandInterval;
        _sweepTimers.RunDue(GhSweepManager.PromptWaitTimeout);
    }

    public static readonly RoomKey A = new(1, 1);
    public static readonly RoomKey B = new(1, 2);
    public static readonly RoomKey C = new(1, 3);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mudplay-roomba-house-" + Path.GetRandomFileName());
    private readonly string _scratchBbs = "roomba-house-" + Path.GetRandomFileName();
    private readonly TerminalEmulator _emulator = new(80, 24);
    private readonly RoomTracker _tracker;
    private readonly MovementCoordinator _coordinator = new();
    private readonly InventoryManager _inventory;
    private RoomKey _here = A;
    private readonly Dictionary<string, int> _pack = new();
    private int _packHolds = 5000;
    private int _answered;
    private int _clock;

    public LogService Log { get; } = new();
    public List<string> Sent { get; } = new();
    public GhRoomLabelStore Labels { get; }
    public GhItemLocationStore Locations { get; }
    public GhSweepManager Sweep { get; }

    // What lies in plain sight and what is hidden, room by room.
    public Dictionary<RoomKey, Dictionary<string, int>> Floor { get; } = new()
        { [A] = new(), [B] = new(), [C] = new() };
    public Dictionary<RoomKey, Dictionary<string, int>> Stash { get; } = new()
        { [A] = new(), [B] = new(), [C] = new() };
    // The hidden stacks a search has found and that can be picked up.
    private readonly Dictionary<RoomKey, HashSet<string>> _found = new()
        { [A] = new(), [B] = new(), [C] = new() };

    // The statline ahead of an echoed command.
    public string Prompt { get; set; } = "[HP=611/MA=720]:";
    public bool Paradigm { get; set; } = true;
    // Whether the search being answered finds what is hidden. Each search rolls.
    public Func<bool> SearchFinds { get; set; } = () => true;
    // A line the game prints between a search's echo and its reply.
    public string? LineBetweenEchoAndReply { get; set; }
    // Whether the game answers this `get` with `You cannot carry that much!`.
    public Func<string, bool> TooHeavy { get; set; } = _ => false;
    // Every command the game answered, with the room it was answered in.
    public List<(RoomKey Room, string Command)> Played { get; } = new();
    // Who the room's display names under its floor list, if anyone.
    public string? AlsoHere { get; set; }
    // Whether the room being walked into is too dark to show.
    public Func<RoomKey, bool> IsDark { get; set; } = _ => false;

    public RoombaHouse()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/3", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/3", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "C", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """);
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 1 },
              { "Number": 2, "Name": "mace", "ItemType": 1, "Encum": 1 },
              { "Number": 3, "Name": "chain shirt", "ItemType": 0, "Encum": 1 },
              { "Number": 4, "Name": "leather cap", "ItemType": 0, "Encum": 1 },
              { "Number": 5, "Name": "iron helm", "ItemType": 0, "Encum": 1 }
            ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        Labels = new GhRoomLabelStore(profile);
        Labels.OnRealmChanged(AppPaths.BbsFolder(_scratchBbs));
        Labels.SetLabel(A, new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        Labels.SetLabel(B, new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        Labels.SetSearchesPerRoom(1);

        Locations = new GhItemLocationStore(names);
        Locations.OnRealmChanged(AppPaths.BbsFolder(_scratchBbs));

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null,
            isRoomName: line => graph.FindByName(line).Count > 0);
        LineExtractor lines = new(_emulator);
        lines.LineEmitted += router.Dispatch;
        ground.AttachLineExtractor(lines);
        _inventory = new InventoryManager(itemWeightResolver: names.WeightOf);
        _inventory.AttachLineExtractor(lines);

        _tracker = new RoomTracker(graph);
        MovementCoordinator coordinator = _coordinator;
        GhSweepManager? sweep = null;
        _tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(_tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        Sweep = sweep = new GhSweepManager(Labels, runner, _tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false, log: Log,
            isParadigm: () => Paradigm, inventory: _inventory, itemLocations: Locations, promptScanner: _sweepPrompt, now: () => _sweepNow, schedule: _sweepTimers.Arm);
        Sweep.SetWireSender(bytes => Sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => Sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        _tracker.SetLocated(A);
    }

    public void Dispose()
    {
        Sweep.Dispose();
        _inventory.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
        try
        {
            string bbsFolder = AppPaths.BbsFolder(_scratchBbs);
            if (Directory.Exists(bbsFolder)) Directory.Delete(bbsFolder, recursive: true);
        }
        catch { /* best effort */ }
    }

    // An `i` showing an empty pack that can carry this much. Every item weighs 1.
    public void PackCarries(int most)
    {
        _packHolds = most;
        ShowInventory();
    }

    private void ShowInventory()
    {
        int carried = _pack.Values.Sum();
        string list = carried == 0
            ? "nothing"
            : string.Join(", ", _pack.Where(s => s.Value > 0).Select(s => s.Value > 1 ? $"{s.Value} {s.Key}" : s.Key));
        Wire(
            $"You are carrying {list}.",
            "You have no keys.",
            "Wealth:    0 copper farthings",
            $"Encumbrance:    {carried}/{_packHolds}  -  None  [{carried * 100 / _packHolds}%]");
    }

    public void Wire(params string[] rows)
    {
        foreach (string row in rows) _emulator.Feed(Encoding.Latin1.GetBytes(row + "\r\n"));
    }

    // Answer every command the client has sent since the last call, as the game
    // would, until it stops sending. beforeEach runs ahead of each answer, for a
    // test to change the house at a moment of its choosing.
    public void PlayOn(Action? beforeEach = null)
    {
        for (int guard = 0; _answered < Sent.Count; guard++)
        {
            Assert.True(guard < 500, "the sweep never came to rest");
            beforeEach?.Invoke();
            string command = Sent[_answered++];
            if (command is not ("n" or "s")) Played.Add((_here, command));
            if (command is "n" or "s") Walk(command);
            else if (command == "sea") Search();
            else if (command == "i")
            {
                Wire(Prompt + "i");
                ShowInventory();
            }
            else if (command == "l")
            {
                Wire(Display(Prompt + "l"));
                PaceOut();
            }
            else if (command.StartsWith("get ", StringComparison.Ordinal)) Get(command[4..]);
            else if (command.StartsWith("drop ", StringComparison.Ordinal)) Drop(command[5..]);
        }
    }

    // Something other than Roomba holding the walk where it stands, as a rest does,
    // and letting it go again.
    public void HoldTheWalk() => _coordinator.AssertGate(MovementCoordinator.HealthRecoveryGate);
    public void ReleaseTheWalk() => _coordinator.ClearGate(MovementCoordinator.HealthRecoveryGate);

    // The next command the client has sent and the game has not yet answered.
    public string? NextCommand => _answered < Sent.Count ? Sent[_answered] : null;

    public void FireDispatchSettleTimeout() => _sweepTimers.RunDue(GhSweepManager.DispatchSettleTimeout);

    private static string NameOf(RoomKey room) => room.Equals(A) ? "A" : room.Equals(B) ? "B" : "C";

    private static string? ListLine(Dictionary<string, int> stacks) =>
        stacks.Count == 0
            ? null
            : $"You notice {string.Join(", ", stacks.Select(s => s.Value > 1 ? $"{s.Value} {s.Key}" : s.Key))} here.";

    private Direction[] ExitsHere => _here.Equals(A) ? new[] { Direction.N }
        : _here.Equals(B) ? new[] { Direction.S }
        : new[] { Direction.N, Direction.S };

    // The room as the game displays it: what was typed, then the room block. Its
    // name, its description, its visible floor, who else is there, its exits.
    private string[] Display(string promptRow)
    {
        List<string> rows = new()
        {
            promptRow,
            NameOf(_here),
            "    Bare stone walls rise to a vaulted ceiling, and the flagstones underfoot are",
            "worn smooth.",
        };
        if (ListLine(Floor[_here]) is { } list) rows.Add(list);
        if (AlsoHere is { } others) rows.Add("Also here: " + others + ".");
        rows.Add("Obvious exits: "
            + string.Join(", ", ExitsHere.Select(exit => exit == Direction.N ? "north" : "south")));
        return rows.ToArray();
    }

    private static (int Count, string Name) Counted(string argument)
    {
        int space = argument.IndexOf(' ');
        return space > 0 && int.TryParse(argument[..space], out int count)
            ? (count, argument[(space + 1)..])
            : (1, argument);
    }

    private void Walk(string direction)
    {
        // Stock forgets what a searcher found when they leave; Paradigm keeps it.
        if (!Paradigm) _found[_here].Clear();
        _here = (direction, _here) switch
        {
            ("n", var r) when r.Equals(A) => C,
            ("n", var r) when r.Equals(C) => B,
            ("s", var r) when r.Equals(B) => C,
            ("s", var r) when r.Equals(C) => A,
            _ => throw new InvalidOperationException(
                $"no exit {direction} from {_here}; sent so far: {string.Join(" | ", Sent)}"),
        };
        if (IsDark(_here))
        {
            Wire(Prompt + direction, "The room is very dark - you can't see anything");
            _tracker.NoteDarkRoomEntered(DateTimeOffset.UtcNow.AddSeconds(++_clock));
            return;
        }
        Wire(Display(Prompt + direction));
        _tracker.NoteRoomObserved(new RoomObservation(NameOf(_here), new HashSet<Direction>(ExitsHere)),
            DateTimeOffset.UtcNow.AddSeconds(++_clock));
    }

    private void Search()
    {
        string? list = SearchFinds() ? ListLine(Stash[_here]) : null;
        if (list is not null) foreach (string item in Stash[_here].Keys) _found[_here].Add(item);
        List<string> rows = new() { Prompt + "sea" };
        if (LineBetweenEchoAndReply is { } between) rows.Add(between);
        rows.Add(list ?? "Your search revealed nothing.");
        Wire(rows.ToArray());
        int sent = Sent.Count;
        _sweepTimers.RunDue(GhSweepManager.ReconSearchSettle);
        // A search sent in the middle of a room's batch is paced like the rest of it.
        if (Sweep.Phase == GhSweepManager.SweepPhase.Sorting && Sent.Count == sent)
            PaceOut();
    }

    private void Get(string argument)
    {
        (int count, string name) = Counted(argument);
        if (TooHeavy(argument))
        {
            Wire(Prompt + "get " + argument, "You cannot carry that much!");
            PaceOut();
            return;
        }
        int visible = Floor[_here].GetValueOrDefault(name);
        int hidden = _found[_here].Contains(name) ? Stash[_here].GetValueOrDefault(name) : 0;
        if (count > visible + hidden)
        {
            Wire(Prompt + "get " + argument, $"You don't see {argument} here.");
            PaceOut();
            return;
        }
        int fromVisible = Math.Min(count, visible);
        if (fromVisible > 0 && (Floor[_here][name] -= fromVisible) == 0) Floor[_here].Remove(name);
        int fromHidden = count - fromVisible;
        if (fromHidden > 0 && (Stash[_here][name] -= fromHidden) == 0) Stash[_here].Remove(name);
        _pack[name] = _pack.GetValueOrDefault(name) + count;
        Wire(Prompt + "get " + argument, $"You took {argument}.");
        PaceOut();
    }

    private void Drop(string argument)
    {
        (int count, string name) = Counted(argument);
        Floor[_here][name] = Floor[_here].GetValueOrDefault(name) + count;
        _pack[name] = _pack.GetValueOrDefault(name) - count;
        Wire(Prompt + "drop " + argument, $"You dropped {argument}.");
        PaceOut();
    }

    // ----- what the tests read ---------------------------------------------

    public List<string> SweepLog(string startingWith = "") => Log.Snapshot()
        .Where(e => e.Source == GhSweepManager.LogCategory
                    && e.Message.StartsWith(startingWith, StringComparison.Ordinal))
        .Select(e => e.Message)
        .ToList();

    public Dictionary<string, int> LoggedAt(RoomKey room) => Locations.Sightings
        .Where(s => s.Map == room.Map && s.Room == room.Room)
        .ToDictionary(s => s.ItemName, s => s.Quantity);

    public int Sends(string prefix) => Sent.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));
}
