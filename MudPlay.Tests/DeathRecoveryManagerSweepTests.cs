using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// The Stock spill sweep, run end to end over the recovery harness, the real walker
// and the real movement controller, against a small stand-in for the game.
public sealed partial class DeathRecoveryManagerTests
{
    // A room filter that avoids rooms and can't open doors: the walker won't plan
    // into the one or through the other.
    private sealed class AvoidFilter : IRoomFilter
    {
        public HashSet<RoomKey> Avoided { get; } = new();
        public bool DoorsStayShut { get; set; }
        public bool IsAvoided(RoomKey key) => Avoided.Contains(key);
        public bool IsExitBlocked(in RoomExit exit) => DoorsStayShut && exit.Hint == RoomExitHint.Door;
    }

    //        4
    //        │
    //        2
    //        │
    //        1 ── 3        1 is the death room.
    //
    // The engine's spill order from 1 is 2, 4, 3: north and on from there first, and
    // 3 only once the walk has come back for the east exit.
    private const string CrossJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Crossing", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/3", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Lane", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Yard", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Dead End", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static string RoomRow(int room, Dictionary<string, string> exits)
    {
        string[] cols = { "N", "S", "E", "W", "NE", "NW", "SE", "SW", "U", "D" };
        return $"{{ \"Map Number\": 1, \"Room Number\": {room}, \"Name\": \"Room {room}\", \"Light\": 0, \"Shop\": 0, "
            + "\"Lair\": \"\", \"Delay\": 0, "
            + string.Join(", ", cols.Select(c => $"\"{c}\": \"{exits.GetValueOrDefault(c, "0")}\"")) + " }";
    }

    // The death room with ten ways out, each to a room with one more beyond it: twenty
    // rooms within reach of a spill, more than one sweep walks to.
    private static string StarJson()
    {
        string[] cols = { "N", "S", "E", "W", "NE", "NW", "SE", "SW", "U", "D" };
        string[] back = { "S", "N", "W", "E", "SW", "SE", "NW", "NE", "D", "U" };
        List<string> rows = new() { RoomRow(1, cols.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => $"1/{11 + x.i}")) };
        for (int i = 0; i < 10; i++)
        {
            rows.Add(RoomRow(11 + i, new() { [back[i]] = "1/1", [cols[i]] = $"1/{31 + i}" }));
            rows.Add(RoomRow(31 + i, new() { [back[i]] = $"1/{11 + i}" }));
        }
        return "[" + string.Join(",\n", rows) + "]";
    }

    // Room 2 is one step north of the death room through a door, and thirteen rooms
    // away by the only other road: east, up a street of twelve rooms (10 to 21), and
    // down into 2. The engine spills through the door; a walker that can't open it
    // would have to go the long way round.
    private static string LongWayRoundJson()
    {
        List<string> rows = new()
        {
            RoomRow(1, new() { ["N"] = "1/2 (Door)", ["E"] = "1/10" }),
            RoomRow(2, new() { ["S"] = "1/1 (Door)", ["U"] = "1/21" }),
            RoomRow(10, new() { ["W"] = "1/1", ["N"] = "1/11" }),
        };
        for (int r = 11; r <= 20; r++)
            rows.Add(RoomRow(r, new() { ["S"] = $"1/{r - 1}", ["N"] = $"1/{r + 1}" }));
        rows.Add(RoomRow(21, new() { ["S"] = "1/20", ["D"] = "1/2" }));
        return "[" + string.Join(",\n", rows) + "]";
    }

    // Plays the game's side of a Stock recovery over the harness and the real walker:
    // each command the client sends is answered as the game would answer it. A move
    // shows the room it leads to (floor survey first, then the exits line that
    // confirms it), a `look` shows this room's floor and its exits line, a `look
    // <dir>` shows the neighbour's floor, a `get` of something on the floor confirms
    // it, and a `sea` moves the room's hidden items into view.
    private sealed class SpillWorld : IDisposable
    {
        private static readonly Dictionary<string, Direction> Moves = new()
        {
            ["n"] = Direction.N, ["s"] = Direction.S, ["e"] = Direction.E, ["w"] = Direction.W,
            ["ne"] = Direction.NE, ["nw"] = Direction.NW, ["se"] = Direction.SE, ["sw"] = Direction.SW,
            ["u"] = Direction.U, ["d"] = Direction.D,
        };

        private readonly Dictionary<RoomKey, List<string>> _floor = new();
        private readonly Dictionary<RoomKey, (int Searches, List<string> Items)> _hidden = new();
        private int _answered;

        public GraphHarness H { get; }
        public RoomKey Here { get; private set; }
        // A look shows nothing, as through a closed door or past a hidden exit.
        public bool DoorsClosed { get; set; }
        // Rooms the game refuses every move into, and rooms a move into gets no
        // answer for at all.
        public HashSet<int> Barred { get; } = new();
        public HashSet<int> Silent { get; } = new();
        // Rooms with no light: a move in gets the too-dark line, with no floor and
        // no exits line.
        public HashSet<int> Dark { get; } = new();
        // Every room walked into by a move the client sent, in order.
        public List<int> Walked { get; } = new();
        // Called with the room a step lands in, before that room is shown.
        public Action<int>? OnStepInto { get; set; }

        public SpillWorld(string graphJson)
        {
            H = new GraphHarness(graphJson: graphJson, withWalker: true) { Paradigm = false };
        }

        public DeathRecoveryManager Recovery => H.Recovery;
        public string State => H.Recovery.SpillSweepState;
        public string? Note => H.Latest.RecoveryMessage;

        public void Put(int room, params string[] items) => _floor[new RoomKey(1, room)] = items.ToList();

        // Hidden on the room's hidden side, found by the given search (the first,
        // unless a miss or two is asked for).
        public void Hide(int room, string item, int foundOnSearch = 1) =>
            _hidden[new RoomKey(1, room)] = (foundOnSearch, new List<string> { item });

        // Stand in a room without a command having been sent (the walk before the
        // death, and being back in the death room for Recover Now).
        public void Enter(int room)
        {
            Here = new RoomKey(1, room);
            ShowRoom();
        }

        public void Die(EquippedItem[] worn, string[] carried)
        {
            H.Snapshot = H.SnapWith(worn, carried);
            H.Tracker.NoteDeath(2, "You have 2 lives left.");
        }

        // Recover Now from inside the death room, then the given number of heartbeats.
        public void RecoverNow(int beats)
        {
            _answered = H.Sent.Count;
            Assert.True(H.Recovery.RecoverNow(H.Latest));
            Run(beats);
        }

        public void Run(int beats)
        {
            for (int i = 0; i < beats; i++)
            {
                Answer();
                H.Heartbeat();
                Answer();
            }
        }

        // Heartbeats until the sweep's state reads as given.
        public void RunUntil(string stateContains)
        {
            for (int i = 0; i < 400; i++)
            {
                if (State.Contains(stateContains, StringComparison.Ordinal)) return;
                Run(1);
            }
            Assert.Fail($"the sweep never reached '{stateContains}'; it reads: {State}");
        }

        // Answer what is on the wire without a heartbeat passing.
        public void Settle() => Answer();

        // Commands the sweep itself sent since the mark: looks, gets and searches.
        public List<string> SweepSendsSince(int mark) => H.Sent.Skip(mark)
            .Where(c => c.StartsWith("look", StringComparison.Ordinal)
                || c.StartsWith("get ", StringComparison.Ordinal) || c == "sea")
            .ToList();

        private void Answer()
        {
            while (_answered < H.Sent.Count)
            {
                string cmd = H.Sent[_answered++];
                if (Moves.TryGetValue(cmd, out Direction dir))
                {
                    RoomKey target = H.Graph.GetRoom(Here)!.Exits[dir].Target;
                    if (Silent.Contains(target.Room)) continue;
                    if (Barred.Contains(target.Room))
                    {
                        H.Tracker.NoteMoveBlocked();
                        continue;
                    }
                    Here = target;
                    Walked.Add(Here.Room);
                    OnStepInto?.Invoke(Here.Room);
                    if (Dark.Contains(Here.Room)) H.Tracker.NoteDarkRoomEntered();
                    else ShowRoom();
                }
                else if (cmd == "look")
                {
                    // The room again, in wire order: its floor, then the exits line,
                    // which the room parser hands the tracker (a re-confirm in place)
                    // before recovery reads the same line.
                    Room room = H.Graph.GetRoom(Here)!;
                    Survey(Here);
                    H.Tracker.NoteRoomObserved(new RoomObservation(room.Name, new HashSet<Direction>(room.Exits.Keys)));
                    H.Feed.Line("Obvious exits: " + string.Join(", ",
                        room.Exits.Keys.Select(d => d.ToLongName())));
                }
                else if (cmd.StartsWith("look ", StringComparison.Ordinal)
                    && DirectionExtensions.TryFromLongName(cmd[5..], out Direction peek))
                {
                    if (!DoorsClosed) Survey(H.Graph.GetRoom(Here)!.Exits[peek].Target);
                }
                else if (cmd.StartsWith("get ", StringComparison.Ordinal))
                {
                    if (_floor.TryGetValue(Here, out List<string>? floor) && floor.Remove(cmd[4..]))
                        H.Feed.Line($"You took {cmd[4..]}.");
                }
                else if (cmd == "sea" && _hidden.TryGetValue(Here, out (int Searches, List<string> Items) stash))
                {
                    if (stash.Searches > 1)
                    {
                        _hidden[Here] = (stash.Searches - 1, stash.Items);
                        continue;
                    }
                    _hidden.Remove(Here);
                    if (!_floor.TryGetValue(Here, out List<string>? floor)) _floor[Here] = floor = new List<string>();
                    floor.AddRange(stash.Items);
                    Survey(Here);
                }
            }
        }

        private void ShowRoom()
        {
            Room room = H.Graph.GetRoom(Here)!;
            Survey(Here);
            H.Tracker.NoteRoomObserved(new RoomObservation(room.Name, new HashSet<Direction>(room.Exits.Keys)));
        }

        private void Survey(RoomKey room)
        {
            if (_floor.TryGetValue(room, out List<string>? items) && items.Count > 0)
                H.FeedSurvey(string.Join(", ", items));
        }

        public void Dispose() => H.Dispose();
    }

    private static EquippedItem[] Worn(params string[] names) =>
        names.Select((n, i) => new EquippedItem(n, i == 0 ? "Weapon Hand" : "Head")).ToArray();

    // The usual start: died at the crossing wearing a sword (which is on its floor)
    // and carrying a rope (which isn't), and standing back in the death room.
    private static SpillWorld SwordHereRopeOut(string graphJson = CrossJson, bool doorsClosed = true)
    {
        SpillWorld w = new(graphJson) { DoorsClosed = doorsClosed };
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.Enter(1);
        return w;
    }

    // ----- the plan --------------------------------------------------

    [Fact]
    public void Sweep_WalksTheEnginesOrder_GetsWhatItFinds_AndComesBack()
    {
        // Every way out of the death room is shut, so the looks show nothing and the
        // order is the engine's alone. The walker opens what it has to on the way.
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.Enter(1);
        w.Die(Worn("iron sword", "steel helm"), new[] { "torch", "rope" });
        w.Put(1, "iron sword");
        w.Put(2, "steel helm");
        w.Put(4, "torch");
        w.Put(3, "rope");

        w.Enter(1);
        w.RecoverNow(beats: 40);

        // North and on from there before the east exit is come back for.
        Assert.Equal(new[] { 2, 4, 2, 1, 3, 1 }, w.Walked.ToArray());
        Assert.Contains("get steel helm", w.H.Sent);
        Assert.Contains("get torch", w.H.Sent);
        Assert.Contains("get rope", w.H.Sent);
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
        Assert.Equal(new RoomKey(1, 1), w.Here);   // back where recovery started
    }

    [Fact]
    public void Sweep_PeeksTheDeathRoomsExitsInTheEnginesOrder_BeforeItWalks()
    {
        using SpillWorld w = SwordHereRopeOut(doorsClosed: false);
        w.Put(3, "rope");

        w.RecoverNow(beats: 40);

        List<string> looks = w.H.Sent.Where(s => s.StartsWith("look ")).ToList();
        Assert.Equal(new[] { "look north", "look east" }, looks.ToArray());
        // The look east showed the rope, so that room is walked to first, not north.
        Assert.Equal(new[] { 3, 1 }, w.Walked.ToArray());
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
    }

    [Fact]
    public void Sweep_StopsAsSoonAsNothingIsMissing()
    {
        using SpillWorld w = new(CrossJson);
        w.Enter(1);
        w.Die(Worn("iron sword", "steel helm"), Array.Empty<string>());
        w.Put(1, "iron sword");
        w.Put(2, "steel helm");

        w.Enter(1);
        w.RecoverNow(beats: 40);

        Assert.Equal(new[] { 2, 1 }, w.Walked.ToArray());   // never on to 4 or 3
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
    }

    [Fact]
    public void Sweep_RoomTheWalkerCannotReach_IsSkipped()
    {
        using SpillWorld w = SwordHereRopeOut();
        w.H.Filter.Avoided.Add(new RoomKey(1, 2));   // and 4 lies beyond it

        w.RecoverNow(beats: 40);

        Assert.Equal(new[] { 3, 1 }, w.Walked.ToArray());
        Assert.Equal(DeathRecoveryStatus.Partial, w.H.Latest.Status);
        Assert.Equal(new[] { "rope" }, w.H.Latest.UnrecoveredItems);
        Assert.Contains("every room in the plan was tried", w.Note);
    }

    [Fact]
    public void Sweep_RoomOnlyALongWayRoundReaches_IsSkipped_ByTheRouteItWouldWalk()
    {
        // The engine put the rope behind the door, one step north. The walker can't
        // open it, and its road there is thirteen rooms: over what a leg may be.
        using SpillWorld w = SwordHereRopeOut(LongWayRoundJson());
        w.H.Filter.DoorsStayShut = true;
        w.Put(2, "rope");

        w.RecoverNow(beats: 300);

        Assert.DoesNotContain(2, w.Walked);
        Assert.Contains(21, w.Walked);   // the road exists: twelve rooms of it is walked for the next stop
        Assert.Equal(new[] { "rope" }, w.H.Latest.UnrecoveredItems);
        Assert.Equal(new RoomKey(1, 1), w.Here);
    }

    [Fact]
    public void Sweep_RoomTheGameWontLetUsInto_IsGivenUpOn_AndTheRestGoesOn()
    {
        // The walker plans a route to 2 and the game refuses the step (a door it
        // can't open, a trap it won't cross). That stop and the one beyond it are
        // given up on, and the sweep goes on to the next.
        using SpillWorld w = SwordHereRopeOut();
        w.Barred.Add(2);
        w.Put(3, "rope");

        w.RecoverNow(beats: 120);

        Assert.Equal(new[] { 3, 1 }, w.Walked.ToArray());
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
    }

    [Fact]
    public void Sweep_StopTheWalkerStopsShortOf_IsSkippedAtOnce()
    {
        // Room 2 is a boss room marked "stop before entering": the walker ends one
        // room short of it, which here is where we stand, so the leg never arrives.
        using SpillWorld w = SwordHereRopeOut();
        w.H.Walker!.SetBossStopRooms(() => new HashSet<RoomKey> { new(1, 2) });
        w.Put(3, "rope");

        w.RecoverNow(beats: 15);   // not the 20 s a leg that never arrives is given

        // Straight on to the next stop (the walker passes through 2 for it, which its
        // rule allows), and round to the rope, all inside those few seconds.
        Assert.Equal(new[] { 2, 4 }, w.Walked.Take(2).ToArray());
        Assert.Contains("get rope", w.H.Sent);
    }

    [Fact]
    public void Sweep_WalksToTwelveRoomsAtMost_ThenComesBack()
    {
        using SpillWorld w = new(StarJson());
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });   // neither is anywhere

        w.Enter(1);
        w.RecoverNow(beats: 200);

        // The engine's order here: north and the room beyond it, then (walking back
        // through the full death room) each of its other nine exits, and only then
        // the rooms beyond those. Twelve stops is as far as one sweep goes.
        Assert.Equal(new[] { 11, 31, 12, 13, 14, 15, 16, 17, 18, 19, 20, 32 },
            w.Walked.Where(r => r != 1).Distinct().ToArray());
        Assert.Contains("the limit", w.Note);
        Assert.Equal(DeathRecoveryStatus.Partial, w.H.Latest.Status);
        Assert.Equal(new RoomKey(1, 1), w.Here);
    }

    // ----- the trail -------------------------------------------------

    [Fact]
    public void Sweep_WithItemsStillMissing_SearchesTheTrail_DeathRoomFirst()
    {
        // When no floor nearby had room the engine hides the item in a room walked
        // before the death, starting with the death room itself. Only a search shows
        // it, and each item is found on its own roll, so a room is searched twice.
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.Enter(3);
        w.Enter(1);                                    // the trail: 1, then 3
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.Hide(3, "rope");

        Assert.Equal(new[] { 1, 3 }, w.H.Latest.Trail!.Select(r => r.Room).ToArray());

        w.Enter(1);
        w.RecoverNow(beats: 80);

        // The three spill rooms, back to the death room to search it, then the trail.
        Assert.Equal(new[] { 2, 4, 2, 1, 3, 1, 3, 1 }, w.Walked.ToArray());
        Assert.Equal(3, w.H.Sent.Count(s => s == "sea"));   // twice in 1, once in 3 (found)
        Assert.Contains("get rope", w.H.Sent);
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
    }

    [Fact]
    public void Sweep_ASearchThatMisses_IsTriedOnceMore()
    {
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.Hide(1, "rope", foundOnSearch: 2);

        w.Enter(1);
        w.RecoverNow(beats: 80);

        Assert.Equal(2, w.H.Sent.Count(s => s == "sea"));
        Assert.Contains("get rope", w.H.Sent);
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
    }

    [Fact]
    public void Sweep_NeverSearchesAStashRoom()
    {
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.H.StashRooms.Add(new RoomKey(1, 3));
        w.Enter(3);
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.Hide(3, "rope");

        w.Enter(1);
        w.RecoverNow(beats: 80);

        Assert.Equal(2, w.H.Sent.Count(s => s == "sea"));   // the death room only
        Assert.DoesNotContain("get rope", w.H.Sent);
        Assert.Equal(DeathRecoveryStatus.Partial, w.H.Latest.Status);
    }

    [Fact]
    public void Sweep_HostileAtATrailStop_HoldsTheSearchUntilItIsGone()
    {
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.Hide(1, "rope");
        w.Enter(1);
        w.RecoverNow(beats: 0);
        w.RunUntil("getting items at 1/1");   // back in the death room, its trail stop

        w.H.Hostiles = true;
        w.Run(10);
        Assert.DoesNotContain("sea", w.H.Sent);   // the game refuses a search in a fight

        w.H.Hostiles = false;
        w.Run(10);
        Assert.Contains("sea", w.H.Sent);
        Assert.Contains("get rope", w.H.Sent);
    }

    [Fact]
    public void Sweep_WithAutoSearchOn_CountsItsEntrySearch_AndTellsItOfOurs()
    {
        // Auto-search searches every room walked into. At a trail stop that is one of
        // the two tries, so the sweep sends one `sea` of its own there, and says so,
        // so auto-search doesn't search again as the next walk leaves.
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.H.AutoSearches = true;
        w.Enter(3);
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");

        w.Enter(1);
        w.RecoverNow(beats: 80);

        // Walked into 1 and into 3 as trail stops: one search of ours in each.
        Assert.Equal(2, w.H.Sent.Count(s => s == "sea"));
        Assert.Equal(new[] { new RoomKey(1, 1), new RoomKey(1, 3) }, w.H.Searched.ToArray());
    }

    // ----- what starts it --------------------------------------------

    [Fact]
    public void Sweep_EmptyDeathRoomFloor_StartsOnTheLooksExitsLine()
    {
        // An empty floor prints no survey; the look's exits line says it has none.
        using SpillWorld w = new(CrossJson);
        w.Enter(1);
        w.Die(Worn("iron sword"), Array.Empty<string>());
        w.Put(2, "iron sword");

        w.Enter(1);
        w.RecoverNow(beats: 40);

        Assert.Equal(new[] { 2, 1 }, w.Walked.ToArray());
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
    }

    [Fact]
    public void RecoverNow_InTheRoom_WaitsForTheLooksReply_HoweverLate()
    {
        // No timer stands in for the reply: a slow one would be read as an empty
        // floor, the sweep would start, and the late survey would be taken for the
        // first neighbour's.
        using GraphHarness h = new() { Paradigm = false };
        Die(h, new[] { new EquippedItem("iron sword", "Weapon Hand") }, new[] { "rope" });
        h.EnterGates();
        Assert.True(h.Recovery.RecoverNow(h.Latest));
        h.Sent.Clear();

        for (int i = 0; i < 10; i++) h.Heartbeat();
        Assert.Empty(h.Sent);
        Assert.Equal("idle", h.Recovery.SpillSweepState);

        h.FeedSurvey("an iron sword");
        Assert.Contains("get iron sword", h.Sent);
    }

    [Fact]
    public void Stock_WalkIn_WithNoSurveyOfItsOwn_DoesNotTakeTheLastRoomsFloorForThisOne()
    {
        // The floor list still holds a re-read of the room we left; this floor printed
        // nothing. That is an empty floor, not those items.
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());            // standing next door
        h.FeedSurvey("a torch");                       // a look there shows a torch
        h.Tracker.NoteMoveSent(Direction.S);           // then the step into the death room
        h.Sent.Clear();

        h.EnterGates();

        Assert.DoesNotContain("get torch", h.Sent);
    }

    [Fact]
    public void Sweep_WaitsForAutoAll()
    {
        using SpillWorld w = new(CrossJson);
        bool autoOn = false;
        w.H.Recovery.SetAutoEnabledProbe(() => autoOn);
        w.Enter(1);
        w.Die(Worn("iron sword", "steel helm"), Array.Empty<string>());
        w.Put(1, "iron sword");
        w.Put(2, "steel helm");

        w.Enter(1);
        w.RecoverNow(beats: 20);
        Assert.DoesNotContain(w.H.Sent, s => s.StartsWith("look "));
        Assert.Empty(w.Walked);

        autoOn = true;
        w.Run(40);
        Assert.Equal(new[] { 2, 1 }, w.Walked.ToArray());
    }

    [Fact]
    public void Sweep_WaitsOutAHostileInTheDeathRoom()
    {
        using SpillWorld w = new(CrossJson);
        w.Enter(1);
        w.Die(Worn("iron sword", "steel helm"), Array.Empty<string>());
        w.Put(1, "iron sword");
        w.Put(2, "steel helm");
        w.H.Hostiles = true;

        w.Enter(1);
        w.RecoverNow(beats: 20);
        Assert.Empty(w.Walked);

        w.H.Hostiles = false;
        w.Run(40);
        Assert.Equal(new[] { 2, 1 }, w.Walked.ToArray());
    }

    [Fact]
    public void Sweep_DoesNotStart_WhileAnotherEngineDrives_AndIsNotLeftWaiting()
    {
        using SpillWorld w = SwordHereRopeOut();
        w.H.OtherEngine = true;   // a loop, Auto-Lair, an errand, or following a leader

        w.RecoverNow(beats: 20);
        Assert.DoesNotContain(w.H.Sent, s => s.StartsWith("look "));
        Assert.Contains("Recover Now runs it", w.Note);

        w.H.OtherEngine = false;
        w.Run(40);
        Assert.Empty(w.Walked);   // it gave way; it doesn't start by itself later
    }

    [Fact]
    public void AWalkTheUserMadeToTheDeathRoom_EarnsTheWholeSweep()
    {
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.Put(4, "rope");
        w.H.Recovery.AutoRecover = true;
        w.Enter(3);

        int mark = w.H.Sent.Count;
        Assert.True(w.H.Walker!.WalkTo(new RoomKey(1, 1)));
        w.Run(60);

        Assert.Contains("get iron sword", w.H.Sent.Skip(mark));
        Assert.Contains(4, w.Walked);   // two rooms out, where no look reaches
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
    }

    [Fact]
    public void AnotherEnginesArrivalInTheDeathRoom_PeeksOnly_AndWalksToWhatThePeekShows()
    {
        // A loop's approach or flee return, or an Auto-Lair hop, ends its walk in the
        // death room. That earns what it always did: the looks, and a walk to a
        // neighbour seen holding our items. Not the engine's order, not the trail.
        using SpillWorld w = new(CrossJson);
        w.Enter(1);
        w.Die(Worn("iron sword", "steel helm"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.Put(3, "steel helm");   // next door, where a look sees it
        w.Put(4, "rope");         // two rooms out, where none does
        w.H.Recovery.AutoRecover = true;
        w.Enter(2);
        w.H.OtherEngine = true;

        Assert.True(w.H.Walker!.WalkTo(new RoomKey(1, 1)));
        w.Run(60);

        Assert.Equal(new[] { 1, 3, 1 }, w.Walked.ToArray());
        Assert.Contains("get steel helm", w.H.Sent);
        Assert.DoesNotContain("sea", w.H.Sent);
        Assert.Equal(new[] { "rope" }, w.H.Latest.UnrecoveredItems);
    }

    [Fact]
    public void AnotherEnginesArrival_WithNothingSeenNextDoor_WalksNowhere()
    {
        using SpillWorld w = new(CrossJson);
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.Put(4, "rope");
        w.H.Recovery.AutoRecover = true;
        w.Enter(2);
        w.H.OtherEngine = true;

        Assert.True(w.H.Walker!.WalkTo(new RoomKey(1, 1)));
        w.Run(60);

        Assert.Equal(new[] { 1 }, w.Walked.ToArray());
        Assert.Contains(w.H.Sent, s => s.StartsWith("look "));
    }

    // ----- what ends it: another walk, another engine ----------------

    // Brings a sweep to the named phase. The legs are held on the Combat gate so a
    // walking phase can be caught: nothing is on the wire for them yet.
    private static SpillWorld SweepIn(string phase)
    {
        SpillWorld w;
        switch (phase)
        {
            case "peek":
                w = SwordHereRopeOut();
                w.RecoverNow(beats: 0);
                w.RunUntil("peeking");
                break;
            case "grab":
                w = SwordHereRopeOut();
                w.RecoverNow(beats: 0);
                w.RunUntil("getting items at 1/2");
                break;
            case "search":
                w = SwordHereRopeOut();
                w.RecoverNow(beats: 0);
                w.RunUntil("searching 1/1");
                break;
            case "walking out":
                w = SwordHereRopeOut();
                w.RecoverNow(beats: 0);
                w.RunUntil("peeking");
                w.H.Coordinator.AssertGate(MovementCoordinator.CombatGate);
                w.RunUntil("walking to 1/2");
                break;
            default:   // walking back
                w = SwordHereRopeOut();
                w.Put(2, "rope");
                w.RecoverNow(beats: 0);
                w.RunUntil("getting items at 1/2");
                w.H.Coordinator.AssertGate(MovementCoordinator.CombatGate);
                w.RunUntil("walking back");
                break;
        }
        return w;
    }

    [Theory]
    [InlineData("peek")]
    [InlineData("grab")]
    [InlineData("search")]
    [InlineData("walking out")]
    [InlineData("walking back")]
    public void Sweep_AWalkItDidNotStart_EndsItInEveryPhase_WithNothingMoreSent(string phase)
    {
        using SpillWorld w = SweepIn(phase);
        int mark = w.H.Sent.Count;
        int walked = w.Walked.Count;
        RoomKey from = w.Here;
        RoomKey to = from.Equals(new RoomKey(1, 3)) ? new RoomKey(1, 1) : new RoomKey(1, 3);

        Assert.True(w.H.Walker!.WalkTo(to));   // the user's own walk-to
        w.H.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        w.Run(60);

        Assert.StartsWith("idle; the last one ended: another walk started", w.State);
        Assert.Empty(w.SweepSendsSince(mark));
        Assert.Equal(to, w.Here);                       // their walk arrived
        Assert.Equal(WalkState.Idle, w.H.Walker.State);
        // Nothing but that walk's own steps moved the character.
        Assert.Equal(to.Room, w.Walked[^1]);
        Assert.True(w.Walked.Count - walked <= 2);
    }

    [Theory]
    [InlineData("peek")]
    [InlineData("grab")]
    [InlineData("search")]
    [InlineData("walking out")]
    [InlineData("walking back")]
    public void Sweep_AnotherEngineTakingOver_EndsItInEveryPhase_WithNothingMoreSent(string phase)
    {
        using SpillWorld w = SweepIn(phase);
        int mark = w.H.Sent.Count;
        int walked = w.Walked.Count;

        w.H.OtherEngine = true;   // a loop was started, or a leader began to be followed
        w.Run(2);
        w.H.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        w.Run(60);

        Assert.Contains("another engine is driving", w.State);
        Assert.Empty(w.H.Sent.Skip(mark));              // not a look, a get, a search or a step
        Assert.Equal(walked, w.Walked.Count);
        Assert.Equal(WalkState.Idle, w.H.Walker!.State);   // its own leg was stopped, not left to run
    }

    [Fact]
    public void Sweep_AMoveSentWhileTheExitsAreLookedThrough_EndsIt_AndThePositionIsLeftAlone()
    {
        // Room changes are ignored during the looks. If something moved the character
        // meanwhile, the tracker must not be put back in the death room and a leg
        // planned from there.
        using SpillWorld w = SweepIn("peek");
        int mark = w.H.Sent.Count;

        w.H.Tracker.NoteMoveSent(Direction.N);   // typed, or a leader's drag
        w.Run(20);

        Assert.Contains("may have moved", w.State);
        Assert.NotEqual(RoomConfidence.Confirmed, w.H.Tracker.State.Confidence);   // still waiting on that move
        Assert.DoesNotContain(w.H.Sent.Skip(mark), s => s is "n" or "e" or "s" or "w");
    }

    // ----- Stop, Pause and the holds ---------------------------------

    [Theory]
    [InlineData("peek")]
    [InlineData("grab")]
    [InlineData("search")]
    public void Sweep_WithTheWalkerIdle_ReadsAsRunning_SoStopReachesIt(string phase)
    {
        using SpillWorld w = SweepIn(phase);
        Assert.Equal(WalkState.Idle, w.H.Walker!.State);
        Assert.Equal(MovementEngineState.Running, w.H.Controller!.State);
        int mark = w.H.Sent.Count;

        w.H.Controller.Stop();
        w.Run(40);

        Assert.Contains("user stop from toolbar", w.State);
        Assert.Empty(w.H.Sent.Skip(mark));
        Assert.Equal(MovementEngineState.Idle, w.H.Controller.State);
        Assert.Contains("Recover Now", w.Note);
    }

    [Fact]
    public void Sweep_StopWhileALegIsOut_EndsIt_AndStartsNoOther()
    {
        using SpillWorld w = SweepIn("walking out");

        w.H.Controller!.Stop();
        w.H.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        w.Run(40);

        Assert.Empty(w.Walked);
        Assert.Equal(WalkState.Idle, w.H.Walker!.State);
        Assert.StartsWith("idle; the last one ended", w.State);
    }

    [Fact]
    public void Sweep_Paused_SendsNothing_AndGoesOnWhenResumed()
    {
        using SpillWorld w = SweepIn("grab");
        w.Put(4, "rope");
        int mark = w.H.Sent.Count;

        w.H.Controller!.Pause();
        Assert.Equal(MovementEngineState.Paused, w.H.Controller.State);
        w.Run(30);
        Assert.Empty(w.H.Sent.Skip(mark));

        w.H.Controller.Resume();
        w.Run(40);
        Assert.Contains("get rope", w.H.Sent);
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
    }

    [Fact]
    public void Sweep_AutoAllGoingOff_HoldsEveryLookGetAndSearch_UntilItIsBack()
    {
        using SpillWorld w = SwordHereRopeOut();
        bool autoOn = true;
        w.Recovery.SetAutoEnabledProbe(() => autoOn);
        w.Put(4, "rope");
        w.RecoverNow(beats: 0);
        w.RunUntil("peeking");
        int mark = w.H.Sent.Count;

        autoOn = false;
        w.Run(30);
        Assert.Empty(w.H.Sent.Skip(mark));   // the second look waits too

        autoOn = true;
        w.Run(60);
        Assert.Contains("get rope", w.H.Sent);
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
    }

    [Fact]
    public void Sweep_ARest_HoldsTheGetsOfAStopJustReached_AndTheyGoOutAfterIt()
    {
        // The rest gate goes up as the step into the stop lands. A helper action
        // waits a rest out, so the floor read on arriving is kept and its gets go out
        // when the rest is over.
        using SpillWorld w = SwordHereRopeOut();
        w.Put(2, "rope");
        w.OnStepInto = room =>
        {
            if (room == 2) w.H.Coordinator.AssertGate(MovementCoordinator.HealthRecoveryGate);
        };

        w.RecoverNow(beats: 30);
        Assert.Equal(new RoomKey(1, 2), w.Here);
        Assert.DoesNotContain("get rope", w.H.Sent);

        w.H.Coordinator.ClearGate(MovementCoordinator.HealthRecoveryGate);
        w.Run(30);
        Assert.Contains("get rope", w.H.Sent);
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
    }

    [Fact]
    public void Sweep_HeldLeg_IsNotAStall_ButWhenTheTimeRunsOutItEndsInPlace()
    {
        using SpillWorld w = SweepIn("walking out");

        w.Run(100);   // far past the 20 s a stalled leg is given
        Assert.Contains("walking to 1/2", w.State);

        w.Run(600);
        Assert.Contains("out of time", w.Note);
        Assert.Contains("movement held", w.Note);
        Assert.Equal(WalkState.Idle, w.H.Walker!.State);   // no walk left queued behind the hold

        w.H.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        w.Run(40);
        Assert.Empty(w.Walked);
    }

    [Fact]
    public void Sweep_LeavingARoomWithAHostileEngaged_EndsInPlace_AndNeverWalksBack()
    {
        using SpillWorld w = SwordHereRopeOut();
        w.RecoverNow(beats: 0);
        w.RunUntil("getting items at 1/2");
        int mark = w.H.Sent.Count;

        w.Recovery.NoteEngagedTargetAbandoned();   // the walker's abandoned-combat halt fired
        w.Run(60);

        Assert.Contains("hostile still engaged", w.State);
        Assert.Equal(new RoomKey(1, 2), w.Here);
        Assert.Empty(w.H.Sent.Skip(mark));
    }

    // ----- the walk back ---------------------------------------------

    [Fact]
    public void Sweep_WalkBackTheGameRefuses_EndsWhereItStands()
    {
        using SpillWorld w = SwordHereRopeOut();
        w.Put(2, "rope");
        w.RecoverNow(beats: 0);
        w.RunUntil("getting items at 1/2");
        w.Barred.Add(1);

        w.Run(80);

        Assert.Equal(new RoomKey(1, 2), w.Here);
        Assert.Equal(WalkState.Idle, w.H.Walker!.State);
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);   // the pile itself is complete
        Assert.StartsWith("idle; the last one ended: the walk back", w.State);
    }

    [Fact]
    public void Sweep_WalkBackThatGetsNoAnswer_IsGivenUp_AndItsLegStopped()
    {
        using SpillWorld w = SwordHereRopeOut();
        w.Put(2, "rope");
        w.RecoverNow(beats: 0);
        w.RunUntil("getting items at 1/2");
        w.Silent.Add(1);

        w.Run(80);

        Assert.Contains("reached no new room", w.State);
        Assert.Equal(WalkState.Idle, w.H.Walker!.State);
    }

    [Fact]
    public void Sweep_WalkBackHeldTooLong_IsDropped_NotLeftToSetOffLater()
    {
        using SpillWorld w = SweepIn("walking back");

        w.Run(130);
        Assert.Contains("the walk back took over 120 s", w.State);
        Assert.Equal(WalkState.Idle, w.H.Walker!.State);

        w.H.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        w.Run(40);
        Assert.Equal(new RoomKey(1, 2), w.Here);
    }

    // ----- dropped outright ------------------------------------------

    [Fact]
    public void Sweep_PositionLost_EndsIt()
    {
        using SpillWorld w = SweepIn("grab");
        int mark = w.H.Sent.Count;

        w.H.Tracker.OnGraphReloaded();   // the tracker no longer knows where we are
        w.Run(40);

        Assert.Contains("position lost", w.State);
        Assert.Empty(w.H.Sent.Skip(mark));
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("disconnect")]
    [InlineData("profile")]
    [InlineData("death")]
    public void Sweep_IsDropped_WithNothingMoreSent(string by)
    {
        using SpillWorld w = SweepIn("grab");
        int mark = w.H.Sent.Count;
        DeathRecord pile = w.H.Latest;
        string? note = pile.RecoveryMessage;

        switch (by)
        {
            case "reset": w.Recovery.CancelTrip(); break;
            case "disconnect": w.Recovery.NotifyDisconnected(); break;
            case "profile": w.H.Profile.LoadDefaultProfile(); break;
            default:
                w.H.Tracker.NoteDeath(1, "You have 1 lives left.");
                w.H.Walker!.Stop("player died");   // as the app's death halt does
                break;
        }
        w.Run(60);

        Assert.Equal("idle; the last one ended: " + by switch
        {
            "reset" => "cancelled (Reset States)",
            "disconnect" => "disconnected",
            "profile" => "the character was changed",
            _ => "died during the sweep",
        }, w.State);
        Assert.Empty(w.SweepSendsSince(mark));
        Assert.Equal(note, pile.RecoveryMessage);   // no result is written up for a sweep that didn't finish
        Assert.Equal(MovementEngineState.Idle, w.H.Controller!.State);
    }

    // ----- second review: starts, darkness, stops left, pauses --------

    [Fact]
    public void DeferredSweep_DoesNotStart_WithAnotherEnginesMoveOnTheWire()
    {
        // A loop's flee return ends in the death room with a hostile there, so the
        // sweep waits. The kill frees the loop's next move and the sweep in the same
        // instant, and the heartbeat ticks before that move lands. Started then, the
        // looks go out from a room the character is leaving, the landing is ignored
        // for their length, and the tracker is anchored back in the death room.
        using SpillWorld w = new(CrossJson);
        w.Enter(1);
        w.Die(Worn("iron sword", "steel helm"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.H.Recovery.AutoRecover = true;
        w.Enter(2);
        w.H.OtherEngine = true;
        w.H.Hostiles = true;
        Assert.True(w.H.Walker!.WalkTo(new RoomKey(1, 1)));
        w.Run(8);
        Assert.Contains("waiting to start", w.State);

        w.H.Hostiles = false;
        w.H.Tracker.NoteMoveSent(Direction.N);     // the loop's own move, on the wire
        int mark = w.H.Sent.Count;
        w.H.Heartbeat();                           // ticks before the move lands
        w.Enter(2);                                // it lands: we are in 1/2 now
        for (int i = 0; i < 6; i++) w.H.Heartbeat();

        Assert.DoesNotContain(w.H.Sent.Skip(mark), s => s.StartsWith("look "));
        Assert.Equal(new RoomKey(1, 2), w.H.Tracker.State.CurrentRoom!.Key);
        Assert.Equal("idle", w.State);
    }

    [Fact]
    public void DeferredSweep_DoesNotStart_WithATypedMoveOnTheWire()
    {
        using SpillWorld w = SwordHereRopeOut(doorsClosed: false);
        w.H.Hostiles = true;
        w.RecoverNow(beats: 6);
        Assert.Contains("waiting to start", w.State);

        w.H.Hostiles = false;
        w.H.Tracker.NoteMoveSent(Direction.E);
        int mark = w.H.Sent.Count;
        w.H.Heartbeat();
        w.Enter(3);
        for (int i = 0; i < 8; i++) w.H.Heartbeat();

        Assert.Empty(w.H.Sent.Skip(mark));
        Assert.Equal(new RoomKey(1, 3), w.H.Tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void DeferredSweep_StartsAfterAMoveTheGameRefused()
    {
        // The move bounced: we stand confirmed in the death room again, and the wait
        // is over.
        using SpillWorld w = SwordHereRopeOut();
        w.Put(2, "rope");
        w.H.Hostiles = true;
        w.RecoverNow(beats: 6);
        w.H.Hostiles = false;
        w.H.Tracker.NoteMoveSent(Direction.E);
        w.H.Heartbeat();
        Assert.Contains("waiting to start", w.State);

        w.H.Tracker.NoteMoveBlocked();
        w.Run(40);

        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
    }

    [Fact]
    public void DeferredSweep_IsCalledOffByStop()
    {
        using SpillWorld w = SwordHereRopeOut();
        w.H.Hostiles = true;
        w.RecoverNow(beats: 6);
        Assert.Contains("waiting to start", w.State);
        int mark = w.H.Sent.Count;

        w.H.Controller!.Stop();
        w.H.Hostiles = false;
        w.Run(20);

        Assert.Empty(w.H.Sent.Skip(mark));
        Assert.Equal("idle", w.State);
    }

    [Fact]
    public void Stock_WalkIntoADarkDeathRoom_StaysArmedForTheLookThatFollowsALight()
    {
        // A dark room confirms with no display at all. Nothing has been read, so the
        // floor is not taken as empty and no sweep sets off from it.
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSent(Direction.S);
        h.Tracker.NoteDarkRoomEntered();
        Assert.True(h.Tracker.IsInDarkRoom);

        for (int i = 0; i < 5; i++) h.Heartbeat();
        Assert.Empty(h.Sent);
        Assert.Equal("idle", h.Recovery.SpillSweepState);

        h.FeedSurvey("a torch");   // a light went on and the room was looked at
        Assert.Contains("get torch", h.Sent);
    }

    [Fact]
    public void Sweep_CharacterMovedOffAStopWhilePaused_EndsOnResume_AndSearchesNothing()
    {
        // At a trail stop a typed move pauses navigation; the user steps into their
        // stash room and presses Resume. The search owed to the stop must not go out
        // where the character now stands.
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.H.StashRooms.Add(new RoomKey(1, 3));
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.Enter(1);
        w.RecoverNow(beats: 0);
        w.RunUntil("getting items at 1/1");
        int mark = w.H.Sent.Count;

        w.H.Controller!.Pause();                   // what a typed move does
        w.H.Tracker.NoteMoveSent(Direction.E);
        w.Enter(3);
        for (int i = 0; i < 5; i++) w.H.Heartbeat();
        Assert.Contains("getting items at 1/1", w.State);   // paused: it waits, and doesn't end itself

        w.H.Controller.Resume();
        for (int i = 0; i < 6; i++) w.H.Heartbeat();

        Assert.Empty(w.H.Sent.Skip(mark));
        Assert.Contains("moved off the stop", w.State);
        Assert.False(w.H.Coordinator.IsGateAsserted(MovementCoordinator.UserGate));
    }

    [Fact]
    public void Sweep_UnderTheUsersPause_NeverEndsItself_AndItsClocksStop()
    {
        // A sweep that ended itself under a pause would leave the user's gate up with
        // nothing running, and the next walk the client started would begin held.
        using SpillWorld w = SweepIn("grab");
        w.Put(4, "rope");

        w.H.Controller!.Pause();
        w.Run(800);   // past the ten minutes and the two for a walk back

        Assert.Contains("getting items at 1/2", w.State);
        Assert.Equal(MovementEngineState.Paused, w.H.Controller.State);

        w.H.Controller.Resume();
        w.Run(40);
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);   // with its time intact
    }

    [Fact]
    public void Sweep_StopReachedWithoutWalking_DoesNotTakeAPeekedFloorForItsOwn()
    {
        // No spill room can be walked to, so the first stop is the death room itself,
        // as a trail stop. The floor list still holds what the look east showed (a
        // rope like ours, where the walker won't go). That is not this room's floor:
        // no get goes out for it, and ours, which the search reveals, is still got.
        using SpillWorld w = new(CrossJson);
        w.H.Filter.Avoided.Add(new RoomKey(1, 2));
        w.H.Filter.Avoided.Add(new RoomKey(1, 3));
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.Put(3, "rope");
        w.Hide(1, "rope");

        w.Enter(1);
        w.RecoverNow(beats: 40);

        Assert.Equal(1, w.H.Sent.Count(s => s == "get rope"));
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
    }

    [Fact]
    public void Sweep_OutOfTimeWhileFreeToMove_WalksBack()
    {
        // A hostile at the last trail stop holds the search, not the walking. When the
        // ten minutes are up the sweep can still move, so it goes back.
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.Enter(3);
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        int into3 = 0;
        w.OnStepInto = room =>
        {
            if (room == 3 && ++into3 == 2) w.H.Hostiles = true;   // back there as a trail stop
        };

        w.Enter(1);
        w.RecoverNow(beats: 700);

        Assert.Equal(new RoomKey(1, 1), w.Here);
        Assert.Contains("out of time (600 s)", w.Note);
        Assert.DoesNotContain("movement held", w.Note);
        Assert.DoesNotContain("not put back on", w.Note);
    }

    [Fact]
    public void Disconnect_WhileTheDeathRoomGrabSettles_StartsNoSweepAfterwards()
    {
        using SpillWorld w = SwordHereRopeOut();
        w.RecoverNow(beats: 0);
        w.Settle();                                // the look is answered, the sword got
        Assert.Contains("get iron sword", w.H.Sent);
        int mark = w.H.Sent.Count;

        w.Recovery.NotifyDisconnected();
        w.Run(20);

        Assert.Empty(w.H.Sent.Skip(mark));
        Assert.Equal("idle", w.State);
    }

    [Fact]
    public void Sweep_BackToADeathRoomTheWalkerStopsBefore_FinishesOneRoomShort()
    {
        // The death room is marked "stop before entering", so the walk back ends one
        // room short. That is the arrival, at once, and the gear goes back on.
        using SpillWorld w = SwordHereRopeOut();
        w.H.Walker!.SetBossStopRooms(() => new HashSet<RoomKey> { new(1, 1) });

        w.RecoverNow(beats: 30);

        Assert.Equal(new RoomKey(1, 3), w.Here);
        Assert.Equal("idle; the last one ended: every room in the plan was tried", w.State);
        Assert.DoesNotContain("not put back on", w.Note);
    }

    [Fact]
    public void WhoseWalkItWas_IsAskedAtTheNextHeartbeat_NotInsideTheArrival()
    {
        // An errand that walked here (a token route, say) still reads as driving
        // while the arrival is being handled, and is done a moment later. The walk was
        // the user's Recover-style walk-to all the same: it earns the whole sweep.
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.Put(4, "rope");
        w.H.Recovery.AutoRecover = true;
        w.Enter(3);
        w.H.OtherEngine = true;

        Assert.True(w.H.Walker!.WalkTo(new RoomKey(1, 1)));
        w.Settle();                 // the arrival, handled with the errand still up
        w.H.OtherEngine = false;    // and over by the next heartbeat
        w.Run(60);

        Assert.Contains(4, w.Walked);
        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
    }

    [Fact]
    public void ALoopWhoseApproachWalkJustEndedHere_GetsTheShortSweep()
    {
        // The other way round: inside the arrival the loop isn't running yet (its
        // walk has only just ended); by the next heartbeat it is.
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.Put(4, "rope");
        w.H.Recovery.AutoRecover = true;
        w.Enter(3);

        Assert.True(w.H.Walker!.WalkTo(new RoomKey(1, 1)));
        w.Settle();
        w.H.OtherEngine = true;
        w.Run(60);

        Assert.Equal(new[] { 1 }, w.Walked.ToArray());   // the looks only
        Assert.Equal(new[] { "rope" }, w.H.Latest.UnrecoveredItems);
    }

    // ----- third review ------------------------------------------------

    [Fact]
    public void RecoverNow_WhoseWalkWasStopped_DoesNotSweepOnALaterWalkInByHand()
    {
        // Recover Now from two rooms off, then Stop while it walks. Coming into the
        // death room by hand later is not that Recover Now arriving.
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.Enter(4);
        Assert.True(w.Recovery.RecoverNow(w.H.Latest));
        w.H.Controller!.Stop();
        w.Settle();                                    // the step already sent lands
        Assert.Equal(new RoomKey(1, 2), w.Here);

        w.H.Tracker.NoteMoveSentByObserver(Direction.S);
        w.H.Sent.Add("s");
        w.Run(40);

        Assert.Equal(new RoomKey(1, 1), w.Here);
        Assert.DoesNotContain(w.H.Sent, s => s.StartsWith("look ") || s.StartsWith("get "));
        Assert.DoesNotContain(4, w.Walked.Skip(2));
    }

    [Fact]
    public void RecoverNowWalk_APvpFightBeginsAndEnds_StillRecoversOnArrival()
    {
        // The fight stops the walk, ends any sweep, and resumes the walk's journey
        // when it is over. That is not the user's Stop: the arrival is still the
        // Recover Now's (Auto-Recover is off, so nothing else would pick up).
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.Enter(1);
        w.Die(Worn("iron sword"), Array.Empty<string>());
        w.Put(1, "iron sword");
        w.Enter(4);
        Assert.True(w.Recovery.RecoverNow(w.H.Latest));
        WalkJourney journey = w.H.Walker!.Journey!;

        w.H.Walker.Stop("PvP: fighting Bob");
        w.Recovery.EndSpillSweep("a fight with a player began");
        w.Settle();
        Assert.True(w.H.Walker.ResumeJourney(journey, planThroughAcquirableGates: true));
        w.Run(8);

        Assert.Equal(new RoomKey(1, 1), w.Here);
        Assert.Contains("get iron sword", w.H.Sent);
    }

    [Fact]
    public void Sweep_EndedByAnEngine_SendsNothingMore()
    {
        using SpillWorld w = SweepIn("walking out");
        w.Recovery.EndSpillSweep("a fight with a player began");
        w.Settle();
        int mark = w.H.Sent.Count;

        w.Run(20);

        Assert.StartsWith("idle", w.State);
        Assert.Empty(w.H.Sent.Skip(mark));
    }

    [Fact]
    public void RecoverNow_PressedAgainWhileWalking_SecondCardsClosed_StillRecoversOnArrival()
    {
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.Enter(1);
        w.Die(Worn("iron sword"), Array.Empty<string>());
        w.Put(1, "iron sword");
        w.Enter(4);
        Assert.True(w.Recovery.RecoverNow(w.H.Latest));   // picked, and walking

        w.Recovery.SetDemandedWalk(_ => true);            // pressed again: cards go up
        Assert.True(w.Recovery.RecoverNow(w.H.Latest));
        w.Recovery.RecoverNowCardsClosed();               // and are closed unpicked
        w.Run(8);

        Assert.Equal(new RoomKey(1, 1), w.Here);
        Assert.Contains("get iron sword", w.H.Sent);
    }

    // 1 is the death room; 2 (north) and 3 (east) both border it and each other.
    private static string TriangleJson() => "[" + string.Join(",\n",
        RoomRow(1, new() { ["N"] = "1/2", ["E"] = "1/3" }),
        RoomRow(2, new() { ["S"] = "1/1", ["SE"] = "1/3" }),
        RoomRow(3, new() { ["W"] = "1/1", ["NW"] = "1/2" })) + "]";

    [Fact]
    public void PassThroughGrab_ArmedInOneNeighbour_AsksForTheNextNeighboursFloorOnce()
    {
        // The grab armed in 2 (whose floor is bare) is still armed when 3's floor
        // prints, ahead of the exits line that confirms 3. It asks for the helm
        // there; arriving in 3 must not ask again.
        using SpillWorld w = new(TriangleJson()) { DoorsClosed = true };
        w.Enter(1);
        w.Die(Worn("iron sword", "steel helm"), Array.Empty<string>());
        w.H.Recovery.AutoRecover = true;
        w.Put(1, "iron sword");
        w.Put(3, "steel helm");
        w.Enter(1);
        w.Settle();                                       // the sword is got

        w.H.Tracker.NoteMoveSentByObserver(Direction.N);
        w.H.Sent.Add("n");
        w.Settle();
        w.H.Tracker.NoteMoveSentByObserver(Direction.SE);
        w.H.Sent.Add("se");
        w.Settle();

        Assert.Equal(new RoomKey(1, 3), w.Here);
        Assert.Equal(1, w.H.Sent.Count(s => s == "get steel helm"));
        Assert.True(w.H.Latest.UnrecoveredItems is null or { Count: 0 },
            $"still listed: {string.Join(", ", w.H.Latest.UnrecoveredItems ?? new())}");
    }

    [Fact]
    public void Sweep_ThroughADarkRoom_SaysSoInThePileNote()
    {
        // Back from a death there is no light. A dark stop is walked into and out of
        // with nothing read, and the note must not pass that off as a room looked at.
        using SpillWorld w = SwordHereRopeOut();
        w.Put(2, "rope");
        w.Dark.Add(2);

        w.RecoverNow(beats: 60);

        Assert.Contains(2, w.Walked);
        Assert.Equal(new[] { "rope" }, w.H.Latest.UnrecoveredItems);
        Assert.Contains("1 of the rooms it stopped in was dark", w.Note);
    }

    [Fact]
    public void Sweep_ThroughLitRoomsOnly_SaysNothingOfTheDark()
    {
        using SpillWorld w = SwordHereRopeOut();
        w.RecoverNow(beats: 60);

        Assert.Equal(DeathRecoveryStatus.Partial, w.H.Latest.Status);
        Assert.DoesNotContain("dark", w.Note);
    }

    [Fact]
    public void Sweep_PausedDuringTheLooks_UserWalksOff_EndsOnResume_WithTheGateClear()
    {
        using SpillWorld w = SwordHereRopeOut(doorsClosed: false);
        w.RecoverNow(beats: 0);
        w.RunUntil("peeking");
        w.H.Controller!.Pause();
        w.H.Tracker.NoteMoveSentByObserver(Direction.E);
        w.H.Sent.Add("e");
        w.Run(5);
        Assert.Equal(new RoomKey(1, 3), w.Here);
        Assert.Contains("peeking", w.State);

        w.H.Controller.Resume();
        w.Run(5);

        Assert.StartsWith("idle", w.State);
        Assert.False(w.H.Coordinator.IsGateAsserted(MovementCoordinator.UserGate));
    }

    [Fact]
    public void DeferredSweep_ThatCannotStartForTenMinutes_IsDropped()
    {
        using SpillWorld w = SwordHereRopeOut();
        w.H.Hostiles = true;
        w.RecoverNow(beats: 6);
        Assert.Contains("waiting to start", w.State);

        w.Run(610);
        Assert.Equal("idle", w.State);

        w.H.Hostiles = false;
        int mark = w.H.Sent.Count;
        w.Run(20);
        Assert.Empty(w.H.Sent.Skip(mark));
    }

    [Fact]
    public void AnEnginesOwnWalkThatJustEndedHere_GetsTheShortSweep_ThoughNothingDrivesAnyMore()
    {
        // A PvP flee, or the walk back from one, that happens to end in a death room.
        // The engine is done by the time anyone asks, but the walk was not the user's.
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.Put(4, "rope");
        w.H.Recovery.AutoRecover = true;
        w.Enter(3);
        w.H.EngineWalkEndedAt.Add(new RoomKey(1, 1));

        Assert.True(w.H.Walker!.WalkTo(new RoomKey(1, 1)));
        w.Run(60);

        Assert.Equal(new[] { 1 }, w.Walked.ToArray());
        Assert.Equal(new[] { "rope" }, w.H.Latest.UnrecoveredItems);
    }

    [Fact]
    public void Sweep_ItsLeg_IsNotAWalkAnythingCanSaveToResume()
    {
        // A PvP fight, a party comeback, a light-shop detour and an event run all save
        // "the walk under way" from the walker's journey and resume it afterwards. A
        // sweep's leg ends with the sweep, so the walker reports no journey for it.
        using SpillWorld w = SweepIn("walking out");

        Assert.NotEqual(WalkState.Idle, w.H.Walker!.State);
        Assert.Equal(new RoomKey(1, 2), w.H.Walker.Destination);
        Assert.Null(w.H.Walker.Journey);
    }

    // ----- fourth review -----------------------------------------------

    [Fact]
    public void RecoverNowWalk_TakenForADetourAndResumed_StillRecoversOnArrival()
    {
        // An errand takes the walk for a side trip of its own and then resumes the
        // journey (the light-shop detour: a character that has just died has lost its
        // lights). The walker says Stopped for the walk it took; the arrival is still
        // the Recover Now's, with Auto-Recover off.
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.Enter(1);
        w.Die(Worn("iron sword"), Array.Empty<string>());
        w.Put(1, "iron sword");
        w.Enter(4);
        Assert.False(w.H.Recovery.AutoRecover);
        Assert.True(w.Recovery.RecoverNow(w.H.Latest));
        WalkJourney journey = w.H.Walker!.Journey!;

        Assert.True(w.H.Walker.WalkTo(new RoomKey(1, 4)));   // the detour's own walk
        w.Run(2);
        Assert.True(w.H.Walker.ResumeJourney(journey));      // and the walk is picked back up
        w.Run(8);

        Assert.Equal(new RoomKey(1, 1), w.Here);
        Assert.Contains("get iron sword", w.H.Sent);
    }

    [Fact]
    public void Paradigm_RecoverNowWalk_StoppedAndResumedByADetour_StillForcesTheCorpseGrab()
    {
        // The same through a stop and a resume, as the sell detour and the PvP flee
        // do it.
        using SpillWorld w = new(CrossJson);
        w.H.Paradigm = true;
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "corpse of Ermias");
        w.Enter(4);
        Assert.True(w.Recovery.RecoverNow(w.H.Latest));
        WalkJourney journey = w.H.Walker!.Journey!;

        w.H.Walker.Stop("sell detour");
        w.Settle();
        Assert.True(w.H.Walker.ResumeJourney(journey, planThroughAcquirableGates: true));
        w.Run(8);

        Assert.Equal(new RoomKey(1, 1), w.Here);
        Assert.Contains("recover corpse Ermias", w.H.Sent);   // off the arrival's own display
    }

    [Fact]
    public void Paradigm_RecoverNowWalk_LeftToArrive_ForcesTheCorpseGrab()
    {
        using SpillWorld w = new(CrossJson);
        w.H.Paradigm = true;
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "corpse of Ermias");
        w.Enter(4);
        Assert.True(w.Recovery.RecoverNow(w.H.Latest));
        w.Run(3);

        Assert.Equal(new RoomKey(1, 1), w.Here);
        Assert.Contains("recover corpse Ermias", w.H.Sent);
    }

    [Fact]
    public void RecoverNow_PressedTwice_StillRecoversOnArrival()
    {
        // The second press replaces the first's walk, which the walker stops. That
        // stop is not the user calling the Recover Now off.
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.Enter(4);
        Assert.True(w.Recovery.RecoverNow(w.H.Latest));
        Assert.True(w.Recovery.RecoverNow(w.H.Latest));
        w.Run(6);
        Assert.Contains("get iron sword", w.H.Sent);
    }

    [Fact]
    public void RecoverNow_WhoseFirstLegGoesElsewhere_IsStillCalledOffByStop()
    {
        // The trip is to the death room; its first leg is a side trip (for an item
        // the route needs, say). It is the trip's destination that marks the walk as
        // the Recover Now's, so Stop calls it off on that leg too.
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Put(1, "iron sword");
        w.Enter(2);
        w.Recovery.SetDemandedWalk(room =>
        {
            w.H.Walker!.BeginJourney(new WalkJourney(room));
            return w.H.Walker.WalkTo(new RoomKey(1, 4), supersedeSilently: true);
        });
        Assert.True(w.Recovery.RecoverNow(w.H.Latest));

        w.H.Controller!.Stop();
        w.Settle();                                    // the step already sent lands (1/4)
        w.H.Tracker.NoteMoveSentByObserver(Direction.S);
        w.H.Sent.Add("s");
        w.Settle();
        w.H.Tracker.NoteMoveSentByObserver(Direction.S);
        w.H.Sent.Add("s");
        w.Run(20);

        Assert.Equal(new RoomKey(1, 1), w.Here);
        Assert.DoesNotContain(w.H.Sent, s => s.StartsWith("get ") || s.StartsWith("look"));
    }

    [Fact]
    public void DeferredSweep_GetsSentWhileItWaits_AreCountedBeforeAnySweepStarts()
    {
        // An empty floor on a deliberate arrival, and the sweep waits on a hostile.
        // Then the room shows ours (a search's reveal) and the gets go out as the
        // hostile leaves. The sweep must not start over them: while its looks run the
        // "You took" lines aren't read, and it would walk out after items in the pack.
        using SpillWorld w = new(CrossJson) { DoorsClosed = true };
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });
        w.Enter(3);
        w.H.Hostiles = true;
        w.H.Recovery.AutoRecover = true;
        Assert.True(w.H.Walker!.WalkTo(new RoomKey(1, 1)));
        w.Run(4);
        Assert.Contains("waiting to start", w.State);

        w.H.Hostiles = false;
        w.H.FeedSurvey("iron sword, rope");
        Assert.Contains("get iron sword", w.H.Sent);
        w.H.Heartbeat();                                   // ticks before the replies are in
        w.H.Feed.Line("You took iron sword.");
        w.H.Feed.Line("You took rope.");
        for (int i = 0; i < 3; i++) w.H.Heartbeat();

        Assert.Equal(DeathRecoveryStatus.Recovered, w.H.Latest.Status);
        Assert.DoesNotContain(w.H.Sent, s => s.StartsWith("look "));
    }

    [Fact]
    public void Sweep_ThatGaveWayAtItsStart_IsNotStartedByALaterSurveyInTheRoom()
    {
        using SpillWorld w = SwordHereRopeOut();
        w.H.OtherEngine = true;
        w.RecoverNow(beats: 6);
        Assert.Contains("Recover Now runs it", w.Note);

        w.H.OtherEngine = false;
        w.H.FeedSurvey("a lantern");   // a later display of the room, nothing of ours on it
        w.Run(20);

        Assert.DoesNotContain(w.H.Sent, s => s.StartsWith("look "));
        Assert.Empty(w.Walked);
    }

    [Fact]
    public void Paradigm_NeverSweeps()
    {
        using SpillWorld w = new(CrossJson);
        w.H.Paradigm = true;
        w.Enter(1);
        w.Die(Worn("iron sword"), new[] { "rope" });

        w.Enter(1);
        w.RecoverNow(beats: 40);

        Assert.Empty(w.Walked);
        Assert.DoesNotContain(w.H.Sent, s => s.StartsWith("look ") || s == "sea");
        Assert.Equal("idle", w.State);
    }
}
