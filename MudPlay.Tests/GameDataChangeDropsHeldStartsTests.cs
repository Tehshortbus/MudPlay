using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.GameData;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Pvp;
using MudPlay.Game.Spells;
using MudPlay.Game.Train;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// What would start moving by itself later, with every engine idle when the game data
// is read in afresh: the run a fight with a player stood down to put back at its end,
// a leader's party training trip between two of its walks, and the default task
// waiting out the party-rebuild window. The stop finds nothing running in any of
// them; each is let go with it, on another set and on the same set read in again.
// Each is wired to the stop as AppServices wires it.
public sealed class GameDataChangeDropsHeldStartsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mudplay-gdc-held-" + Path.GetRandomFileName());
    // The default task's loop is read from the set's Loops folder under the app's
    // own data root; a name of its own keeps it from every other test's.
    private readonly string _loopSet = "gdc-held-" + Path.GetRandomFileName().Replace(".", "");
    private readonly List<IDisposable> _dispose = new();

    public void Dispose()
    {
        foreach (IDisposable d in Enumerable.Reverse(_dispose))
        {
            try { d.Dispose(); }
            catch { /* best-effort teardown */ }
        }
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
        try { Directory.Delete(AppPaths.GameDataSetDir(_loopSet), recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private static string Room(int room, string name, string n = "0", string s = "0", string e = "0", string w = "0") => $$"""
          { "Map Number": 1, "Room Number": {{room}}, "Name": "{{name}}",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "{{n}}", "S": "{{s}}", "E": "{{e}}", "W": "{{w}}",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        """;

    // alpha: 1/1 ↔ 1/2 ↔ 1/3 north to south. beta: the same room numbers wired east
    // to west, so a run of one walks other rooms on the other.
    private static readonly string Alpha =
        $"[\n{Room(1, "A", n: "1/2")},\n{Room(2, "B", n: "1/3", s: "1/1")},\n{Room(3, "C", s: "1/2")}\n]";
    private static readonly string Beta =
        $"[\n{Room(1, "Other A", e: "1/2")},\n{Room(2, "Other B", e: "1/3", w: "1/1")},\n{Room(3, "Other C", w: "1/2")}\n]";

    private sealed class World
    {
        public required GameDataCache Cache { get; init; }
        public required RoomGraphManager Graph { get; init; }
        public required BfsMapper Bfs { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required MovementCoordinator Coordinator { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required LoopRunner Runner { get; init; }
        public required AutoLairManager Lairs { get; init; }
        public required MovementController Controller { get; init; }
        public required WirePromptScanner Prompt { get; init; }
        public required LogService Log { get; init; }
        // Every command an engine put on the wire, in order.
        public List<string> Sent { get; } = new();
        // The notice the last change of game data came to; null when nothing was said.
        public string? Said;

        public string? Change(bool reimport)
        {
            Said = null;
            if (reimport) Cache.ReloadActiveSet();
            else Cache.SwitchSet("beta");
            Tracker.SetLocated(new RoomKey(1, 1));
            return Said;
        }

        public bool Idle => Runner.State == LoopState.Idle && !Lairs.IsActive && Walker.State == WalkState.Idle;
    }

    private World Build()
    {
        foreach ((string set, string rooms) in new[] { ("alpha", Alpha), ("beta", Beta) })
        {
            Directory.CreateDirectory(Path.Combine(_root, set));
            File.WriteAllText(Path.Combine(_root, set, "Rooms.json"), rooms);
        }
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        LogService log = new();
        WirePromptScanner prompt = new();
        EngineRecoveryGate gate = new(graph, tracker);
        AutoWalkManager walker = new(graph, bfs, tracker, coord, log: log, recovery: gate);
        LoopRunner runner = new(tracker, coord, prompt, log: log, graph: graph, recovery: gate, bfs: bfs, postToUi: a => a());
        LairTimerStore timers = new(cache, graph, tracker);
        AutoLairManager lairs = new(walker, tracker, graph, bfs, timers, log, coord);
        MovementController controller = new(walker, runner, lairs, coord, log);
        _dispose.Add(timers);
        _dispose.Add(lairs);
        _dispose.Add(controller);
        World w = new()
        {
            Cache = cache, Graph = graph, Bfs = bfs, Tracker = tracker, Coordinator = coord, Walker = walker,
            Runner = runner, Lairs = lairs, Controller = controller, Prompt = prompt, Log = log,
        };
        void Send(byte[] b) => w.Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r', '\n'));
        walker.SetWireSender(Send);
        runner.SetWireSender(Send);
        gate.SetWireSender(Send);
        gate.TryResync = _ => true;
        gate.TryResyncOnce = (_, _, _) => false;

        // The app's subscriptions, in its order: the stop, the graph, the marks.
        StopOnGameDataChange stop = new(() => cache.ActiveLoad, () => controller);
        cache.ActiveSetChanged += _ => w.Said = stop.OnActiveSetChanged()?.Notice;
        cache.ActiveSetChanged += graph.OnActiveSetChanged;
        graph.GraphReloaded += () => tracker.OnGraphReloaded();
        graph.GraphReloaded += bfs.OnGraphReloaded;
        cache.ActiveSetChanged += set => lairs.DropMarksOfOtherGameData(set);
        return w;
    }

    private static Loop Ab() => new("ab", new[] { new RoomKey(1, 1), new RoomKey(1, 2) });

    private static string Why(bool reimport) => reimport
        ? "game data 'alpha' was read in again"
        : "the game data changed from 'alpha' to 'beta'";

    // ----- the run a fight with a player put aside ----------------------------------

    private sealed record Fight(PvpFight Pvp, HeldDetourResume Held, MessageRouter Router);

    // A fight wired to the engines as AppServices wires it: it stops what is running
    // through the holder, and the holder is the controller's to ask and let go.
    // registered false leaves that last line out.
    private Fight NewFight(World w, bool registered = true)
    {
        MessageRouter router = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        DefaultPatterns.Seed(router);
        RoomEntityClassifier classifier = new(router, new MonsterMessageStore(), players, new LogService());
        RoomDepartureWatcher departure = new(router, classifier);
        PvpFight? fight = null;
        HeldDetourResume held = new(w.Walker, w.Runner, w.Lairs, "a fight", () => fight!.IsActive, w.Log);
        fight = new PvpFight(
            router, classifier,
            pvpEnabled: () => true,
            inParty: _ => false,
            readSettings: () => new PvpSettings(),
            attackCommandFor: given => $"a {given}",
            send: _ => { },
            cast: (_, _) => true,
            spellInfo: _ => null,
            manaMeets: _ => true,
            stepToward: _ => true,
            exitsHere: () => new List<PvpChaseExit>(),
            suspendEngines: why => held.Suspend(why),
            backOnTask: () => held.Interrupted == DetourResumeKind.None || w.Controller.State != MovementEngineState.Idle,
            leadingParty: () => false,
            schedule: (_, _) => { },
            now: () => DateTimeOffset.UtcNow);
        if (registered) w.Controller.AddHeldStart(() => held.Held, reason => held.Drop(reason));
        _dispose.Add(classifier);
        _dispose.Add(departure);
        _dispose.Add(fight);
        players.RecordObservation("Bob", "Mage", null, null, null, null, null, DateTime.UtcNow);
        return new Fight(fight, held, router);
    }

    private static void Engage(World w, Fight f)
    {
        f.Router.Dispatch(new LineExtractor.EmittedLine(
            "Also here: Bob.", Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));
        Assert.True(f.Pvp.Engage("Bob", chase: false, "test"));
        Assert.True(w.Idle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALoopAFightStopped_IsDropped_AndTheFightsEndPutsNothingBack(bool reimport)
    {
        World w = Build();
        Fight f = NewFight(w);
        w.Tracker.SetLocated(new RoomKey(1, 1));
        w.Runner.Start(Ab());
        Engage(w, f);
        Assert.Equal("loop 'ab', held for a fight,", f.Held.Held);
        string[] sent = w.Sent.ToArray();

        string? said = w.Change(reimport);

        Assert.Equal($"[Loop 'ab', held for a fight, dropped: {Why(reimport)}]", said);
        Assert.Null(f.Held.Held);
        Assert.Equal(DetourResumeKind.None, f.Held.Interrupted);
        // The fight is not ended by it.
        Assert.True(f.Pvp.IsActive);
        Assert.Contains(w.Log.Snapshot(), e => e.Severity == LogSeverity.Info && e.Source == "Movement"
            && e.Message.StartsWith("loop 'ab', stopped for a fight, will not be put back when that is over: game data ", StringComparison.Ordinal));

        f.Pvp.Stop("Bob left");

        Assert.True(w.Idle);
        Assert.Null(w.Runner.CurrentLoop);
        Assert.Equal(sent, w.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AWalkAFightStopped_IsDropped_AndTheFightsEndPutsNothingBack(bool reimport)
    {
        World w = Build();
        Fight f = NewFight(w);
        w.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(w.Walker.WalkTo(new RoomKey(1, 3)));
        Engage(w, f);
        string[] sent = w.Sent.ToArray();

        string? said = w.Change(reimport);

        Assert.Equal($"[Walk to 1/3, held for a fight, dropped: {Why(reimport)}]", said);
        f.Pvp.Stop("Bob left");

        Assert.True(w.Idle);
        Assert.Equal(sent, w.Sent);
    }

    // The same set read in again keeps the lair marks, so the fight's end would start
    // Auto-Lair on them again.
    [Fact]
    public void AutoLairAFightStopped_IsDropped_WhenTheSetIsReadInAgain()
    {
        World w = Build();
        Fight f = NewFight(w);
        w.Tracker.SetLocated(new RoomKey(1, 1));
        w.Lairs.Mark(new RoomKey(1, 1));
        w.Lairs.Mark(new RoomKey(1, 3));
        Assert.True(w.Lairs.Start());
        Engage(w, f);
        string[] sent = w.Sent.ToArray();

        string? said = w.Change(reimport: true);

        Assert.Equal($"[Auto-Lair, held for a fight, dropped: {Why(reimport: true)}]", said);
        Assert.Equal(2, w.Lairs.Marked.Count);
        f.Pvp.Stop("Bob left");

        Assert.True(w.Idle);
        Assert.Equal(sent, w.Sent);
    }

    [Fact]
    public void WithNoChangeOfGameData_TheFightsEndPutsTheLoopBack()
    {
        World w = Build();
        Fight f = NewFight(w);
        w.Tracker.SetLocated(new RoomKey(1, 1));
        w.Runner.Start(Ab());
        Engage(w, f);

        // A SwitchSet to the name already active is no new load.
        w.Cache.SwitchSet("ALPHA");
        f.Pvp.Stop("Bob left");

        Assert.NotEqual(LoopState.Idle, w.Runner.State);
        Assert.Equal("ab", w.Runner.CurrentLoop?.Name);
        Assert.Null(f.Held.Held);
    }

    // A fight that ended without putting its run back (the character died in it)
    // holds nothing: a change of game data afterwards has nothing of it to name.
    [Fact]
    public void AFightThatEndedWithoutPuttingItsRunBack_HoldsNothing()
    {
        World w = Build();
        Fight f = NewFight(w);
        w.Tracker.SetLocated(new RoomKey(1, 1));
        w.Runner.Start(Ab());
        Engage(w, f);

        f.Pvp.Stop("we died", resume: false);

        Assert.Null(f.Held.Held);
        Assert.Null(w.Change(reimport: false));
        Assert.True(w.Idle);
    }

    // What the drop guards: unasked, the stop says nothing and the fight's end starts
    // the loop on the other set's rooms.
    [Fact]
    public void WithoutThat_TheFightsEndStartsTheLoopOnTheOtherSetsRooms()
    {
        World w = Build();
        Fight f = NewFight(w, registered: false);
        w.Tracker.SetLocated(new RoomKey(1, 1));
        w.Runner.Start(Ab());
        Engage(w, f);
        int before = w.Sent.Count;

        Assert.Null(w.Change(reimport: false));
        f.Pvp.Stop("Bob left");

        Assert.Equal(LoopState.Running, w.Runner.State);
        Assert.Equal(new[] { "e" }, w.Sent.Skip(before));
    }

    // ----- the default task waiting out the party-rebuild window ---------------------

    private sealed record Task1(DefaultTaskRunner Runner, PartyState Party, ManualTimers Hold);

    // A character whose default task is the loop "ab", back in the game after a drop
    // of the line that followed a party session: the task is held for the window.
    // registered false leaves the controller unable to ask about the hold.
    private Task1 HeldDefaultTask(World w, bool registered = true)
    {
        LoopManager loops = new(w.Bfs, w.Graph);
        loops.LoadAll(_loopSet);
        loops.Save(Ab());
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState partyState = new();
        PartyManager party = new(router, partyState);
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        profile.Current!.Settings = new Dictionary<string, JsonElement>
        {
            ["General"] = JsonSerializer.SerializeToElement(
                new GeneralSettings { DefaultTask = InitialTask.BeginLoop, DefaultLoopName = "ab" }),
        };
        ManualTimers hold = new();
        DefaultTaskRunner runner = new(
            w.Prompt, w.Tracker, profile, loops, new LairManager(), w.Runner, w.Lairs, partyState, party, w.Log,
            schedule: hold.Arm);
        _dispose.Add(runner);
        if (registered) w.Controller.AddHeldStart(() => runner.HeldStart, reason => runner.DropHold(reason));

        byte[] promptBytes = Encoding.Latin1.GetBytes("[HP=100]: ");
        // A stay in the game in a party, with the room not known yet (so the task
        // does not start on it), then the drop and the way back in.
        runner.NotifyConnected();
        w.Prompt.Append(promptBytes);
        partyState.IsInParty = true;
        runner.NotifyDisconnected();
        runner.NotifyConnected();
        w.Tracker.SetLocated(new RoomKey(1, 1));
        w.Prompt.Append(promptBytes);

        Assert.True(runner.IsHoldingForParty);
        Assert.Equal("loop 'ab', held for the party to rebuild,", runner.HeldStart);
        Assert.True(w.Idle);
        return new Task1(runner, partyState, hold);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheDefaultTaskHeldForTheParty_IsCalledOff_AndTheWindowsEndStartsNothing(bool reimport)
    {
        World w = Build();
        Task1 t = HeldDefaultTask(w);

        string? said = w.Change(reimport);

        Assert.Equal($"[Loop 'ab', held for the party to rebuild, dropped: {Why(reimport)}]", said);
        Assert.False(t.Runner.IsHoldingForParty);
        Assert.Null(t.Runner.HeldStart);
        Assert.Contains(w.Log.Snapshot(), e => e.Severity == LogSeverity.Info && e.Source == "DefaultTask"
            && e.Message.StartsWith("loop 'ab' is not started when the party-rebuild hold ends: game data ", StringComparison.Ordinal));
        Assert.Equal(0, t.Hold.Pending);   // the window's timer went with it

        t.Hold.RunAll();

        Assert.True(w.Idle);
        Assert.Empty(w.Sent);
    }

    [Fact]
    public void WithNoChangeOfGameData_TheWindowsEndStartsTheDefaultTask()
    {
        World w = Build();
        Task1 t = HeldDefaultTask(w);

        w.Cache.SwitchSet("ALPHA");
        t.Hold.RunAll();

        Assert.False(t.Runner.IsHoldingForParty);
        Assert.Equal(LoopState.Running, w.Runner.State);
        Assert.Equal("ab", w.Runner.CurrentLoop?.Name);
        Assert.Equal(new[] { "n" }, w.Sent);
    }

    // What calling it off guards: the loop picked from the old data's catalogue is
    // started on the other set's rooms when the window ends.
    [Fact]
    public void WithoutThat_TheWindowsEndStartsTheOldSetsLoopOnTheOtherSetsRooms()
    {
        World w = Build();
        Task1 t = HeldDefaultTask(w, registered: false);

        Assert.Null(w.Change(reimport: false));
        t.Hold.RunAll();

        Assert.Equal(LoopState.Running, w.Runner.State);
        Assert.Equal(new[] { "e" }, w.Sent);
    }

    // ----- a leader's party training trip between two of its walks -------------------

    private sealed class Trip
    {
        public required PartyTrainCoordinator Coordinator { get; init; }
        public required TrainerWalkManager Trainer { get; init; }
        // What the trip sent: its orders to the members and its own commands.
        public required List<string> Orders { get; init; }
        // The timers the trip armed and has not been given: each wait's own.
        public required List<(TimeSpan After, Action Fire)> Timers { get; init; }
        public required ContinuationPump Pump { get; init; }
        public Task Running = Task.CompletedTask;

        // Lets the trip go on from whatever ended its wait, as far as it gets.
        public void GoesOn() => Pump.Drain();
    }

    // A leader with two members to train, each at a trainer of its own: one in the
    // room the party stands in (1/1), one at 1/3. The trip is started as the
    // coordinator's tick starts it, and stands in its wait for the first member's
    // train with the walk to 1/3 still to come. calledOff false leaves the stop
    // unable to end it. offByTheErrand: the errand's end calls it off first, in the
    // same stop, as the app's abandon hook does.
    private Trip TripWaitingAtItsFirstStop(World w, bool calledOff = true, bool offByTheErrand = false)
    {
        File.WriteAllText(Path.Combine(_root, "alpha", "Info.json"), "[{\"Legit\":1}]");
        File.WriteAllText(Path.Combine(_root, "beta", "Info.json"), "[{\"Legit\":1}]");
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerStats stats = new();
        StatParser parser = new(stats);
        InventoryManager inventory = new();
        PartyState partyState = new();
        TrainerMenuTracker menu = new(router, partyState);
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        AutoTrainManager autoTrain = new(stats, parser, w.Cache, inventory, profile, menu,
            new ListedEffectCatalog(new MessageStore(), w.Cache), router);
        TrainerWalkManager trainer = new(stats, parser, w.Cache, profile, w.Tracker, w.Bfs,
            w.Walker, w.Runner, w.Lairs, autoTrain, router, w.Log);
        trainer.SetWireSender(_ => { });

        partyState.Members.Add(new PartyMember { Name = "Lead", IsSelf = true });
        partyState.Members.Add(new PartyMember { Name = "Bob" });
        partyState.Members.Add(new PartyMember { Name = "Cid" });
        partyState.IsInParty = true;

        List<string> orders = new();
        List<(TimeSpan After, Action Fire)> timers = new();
        PartyTrainCoordinator coordinator = new(
            partyState, trainer,
            expPerHour: () => 0,
            holdings: () => null,
            keepOnHandCopper: () => 0,
            largestDeposit: () => (null, 0),
            runicName: () => "runic",
            currentRoom: () => w.Tracker.State.CurrentRoom?.Key,
            distance: (a, b) => Math.Abs(a.Room - b.Room),
            trainers: () => new[]
            {
                new TrainerShop(1, "Warrior Trainer", 1, 1, "A", 1, 50, ClassRest: 1, Markup: 0),
                new TrainerShop(2, "Mage Trainer", 1, 3, "C", 1, 50, ClassRest: 2, Markup: 0),
            },
            nearestBankBranch: (_, _) => null,
            walkTo: key => w.Walker.WalkTo(key, planThroughAcquirableGates: true),
            send: orders.Add,
            recordedVersion: _ => (null, null),
            reformParty: _ => { },
            armTimer: (after, fire) => timers.Add((after, fire)),
            inCombat: () => false,
            selfLevel: () => 10,
            selfExp: () => 0,
            recordedLevel: _ => null,
            selfTimeToLevel: () => null,
            telepathsPending: () => false,
            log: w.Log);
        Trip trip = new() { Coordinator = coordinator, Trainer = trainer, Orders = orders, Timers = timers, Pump = new ContinuationPump() };
        w.Walker.Event += e => coordinator.OnWalkEvent(e.Kind);
        if (offByTheErrand)
            w.Controller.EndingForGameDataChange += (reason, _) => coordinator.CallOffTrip(reason);
        if (calledOff)
            // AppServices.EndHeldRunsForGameDataChange's line for it.
            w.Controller.EndingForGameDataChange += (reason, stopped) =>
            {
                if ((coordinator.CallOffTrip(reason) | coordinator.TakeCalledOffFor(reason))
                    && !stopped.Contains("the training trip"))
                    stopped.Add("the party training trip");
            };
        _dispose.Add(parser);
        _dispose.Add(inventory);
        _dispose.Add(menu);
        _dispose.Add(autoTrain);
        _dispose.Add(trainer);
        _dispose.Add(coordinator);

        w.Tracker.SetLocated(new RoomKey(1, 1));
        PartyTrainStatus Ready(int classNumber) =>
            new(PartyTrainReadiness.Ready, Level: 5, ClassNumber: classNumber, LevelsToTrain: 1,
                CostCopper: 0, CashCopper: 0, SpareCopper: 0, BankCopper: 0, BankName: null, EtaSeconds: 0);
        trip.Timers.Clear();   // the coordinator's own ticks, not the trip's
        trip.Running = trip.Pump.Run(() => coordinator.RunTripForTests(
            new PartyTrainDecision(PartyTrainVerdict.Fire, new[] { "Bob", "Cid" }, Array.Empty<string>(), Array.Empty<string>(), "test"),
            new List<PartyTrainParticipant>
            {
                new("Lead", IsLeader: true, new PartyTrainStatus(PartyTrainReadiness.Waiting, 10, 1, 0, 0, 0, 0, 0, null, 0)),
                new("Bob", IsLeader: false, Ready(classNumber: 1)),
                new("Cid", IsLeader: false, Ready(classNumber: 2)),
            }));

        // Standing at the first trainer, its member told to train there, the trip
        // waiting for that train (its limit is the one timer armed).
        Assert.True(coordinator.TripRunning);
        Assert.Equal(
            new[] { "/Bob @ptrain trip on", "/Cid @ptrain trip on", "/Bob @ptrain train 6 1/1" }, trip.Orders);
        Assert.Single(trip.Timers);
        Assert.Equal(WalkState.Idle, w.Walker.State);
        return trip;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APartyTrainingTripInAWait_IsCalledOff_AndWalksToNoFurtherStop(bool reimport)
    {
        World w = Build();
        Trip trip = TripWaitingAtItsFirstStop(w);
        int orders = trip.Orders.Count;

        string? said = w.Change(reimport);
        trip.GoesOn();

        Assert.True(trip.Running.IsCompletedSuccessfully);
        Assert.Equal($"[The party training trip stopped: {Why(reimport)}]", said);
        Assert.False(trip.Coordinator.TripRunning);
        Assert.False(trip.Trainer.PartyTripActive);
        Assert.Contains(w.Log.Snapshot(), e => e.Severity == LogSeverity.Info
            && e.Message.StartsWith("Party train trip called off: game data ", StringComparison.Ordinal));

        // The wait's own limit running out afterwards hands the trip nothing.
        foreach ((_, Action fire) in trip.Timers.ToArray()) fire();
        trip.GoesOn();

        Assert.True(w.Idle);
        Assert.Empty(w.Sent);
        // The members are told the trip is off, and nothing with a room in it.
        Assert.Equal(new[] { "/Bob @ptrain trip off", "/Cid @ptrain trip off" }, trip.Orders.Skip(orders));
    }

    // Called off already by the errand's end in the same stop, it is named all the same.
    [Fact]
    public void ATripCalledOffByTheErrandsEnd_IsStillNamed()
    {
        World w = Build();
        Trip trip = TripWaitingAtItsFirstStop(w, offByTheErrand: true);

        string? said = w.Change(reimport: false);
        trip.GoesOn();

        Assert.True(trip.Running.IsCompletedSuccessfully);
        Assert.Equal($"[The party training trip stopped: {Why(false)}]", said);
        // Named once: the same reason again finds nothing of this trip to name.
        Assert.False(trip.Coordinator.TakeCalledOffFor("game data changed from 'alpha' to 'beta'"));
    }

    [Fact]
    public void WithNoChangeOfGameData_TheTripWalksOnToItsNextStop()
    {
        World w = Build();
        Trip trip = TripWaitingAtItsFirstStop(w);

        w.Cache.SwitchSet("ALPHA");
        foreach ((_, Action fire) in trip.Timers.ToArray()) fire();
        trip.GoesOn();

        Assert.Equal(WalkState.Walking, w.Walker.State);
        Assert.Equal(new RoomKey(1, 3), w.Walker.Destination);
        Assert.Equal(new[] { "n" }, w.Sent);
        Assert.True(trip.Coordinator.TripRunning);
        w.Walker.Stop("test over");
        trip.GoesOn();
        Assert.True(trip.Running.IsCompletedSuccessfully);
    }

    // What calling it off guards: the stop ends nothing of a trip in a wait, and when
    // the wait is over the trip walks to the trainer it planned on the old data.
    [Fact]
    public void WithoutThat_TheTripWalksOnAfterTheChange_ByTheOldDatasRoomNumber()
    {
        World w = Build();
        Trip trip = TripWaitingAtItsFirstStop(w, calledOff: false);

        Assert.Null(w.Change(reimport: false));
        foreach ((_, Action fire) in trip.Timers.ToArray()) fire();
        trip.GoesOn();

        Assert.Equal(WalkState.Walking, w.Walker.State);
        Assert.Equal(new RoomKey(1, 3), w.Walker.Destination);
        Assert.Equal(new[] { "e" }, w.Sent);
        w.Walker.Stop("test over");
        trip.GoesOn();
        Assert.True(trip.Running.IsCompletedSuccessfully);
    }
}
