using System.Collections.Specialized;
using System.IO;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Events;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.ViewModels.Settings;
using Xunit;
using Harness = MudPlay.Tests.EventEngineHarness;

namespace MudPlay.Tests;

// The remote @events and @event commands: a player granted Alter settings sees
// the character's Events and switches one on or off. What they may be told is an
// event's name and state, never what it sends.
public sealed class EventRemoteHandlerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
    private const string Friend = "Friend";
    private const string Stranger = "Stranger";

    private readonly string _root;
    private readonly string _loopSet = "test-remote-events-" + Path.GetRandomFileName();
    // The BBS a test saves its character under.
    private readonly string _bbs = "test-remote-events-" + Path.GetRandomFileName();

    public EventRemoteHandlerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-remote-events-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
        try { Directory.Delete(AppPaths.GameDataSetDir(_loopSet), recursive: true); }
        catch { /* best-effort */ }
        try { Directory.Delete(AppPaths.BbsFolder(_bbs), recursive: true); }
        catch { /* best-effort */ }
    }

    private sealed class Rig : IDisposable
    {
        public required Harness H { get; init; }
        public required RemoteCommandManager Engine { get; init; }
        // The router the engine's chat lines arrive through.
        public required MessageRouter Router { get; init; }
        // What Engine put on the wire, through the sender the client binds.
        public List<byte[]> Wire { get; } = new();
        public required EventRemoteHandler Handler { get; init; }
        public bool MasterSwitchOff { get; set; }

        public EventManager Events => H.Events;

        public List<string> Sent => Wire.Select(b => Encoding.Latin1.GetString(b)).ToList();

        public void Tell(string sender, string message) =>
            ChatLines.Hear(Router, ChatChannel.TelepathIncoming, sender, message);

        // One command from the trusted player, and the answers to it.
        public List<string> Ask(string message)
        {
            Wire.Clear();
            Tell(Friend, message);
            return Sent;
        }

        public void Dispose()
        {
            Handler.Dispose();
            Engine.Dispose();
            H.Dispose();
        }
    }

    // Friend holds Alter settings; Stranger holds everything but.
    private Rig Build()
    {
        Harness h = Harness.Create(_root, _loopSet);
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new(new ProfileService(), () => null);
        players.RecordObservation(Friend, null, null, null, null, null, null, Now);
        players.EditCustomization(Friend, new PlayerCustomization(RemoteControls: PlayerRemoteControls.AlterSettings));
        players.RecordObservation(Stranger, null, null, null, null, null, null, Now);
        players.EditCustomization(Stranger,
            new PlayerCustomization(RemoteControls: PlayerRemoteControls.All & ~PlayerRemoteControls.AlterSettings));
        RemoteCommandManager engine = new(new ChatRouter(router), new PartyState(), players);
        Rig? rig = null;
        engine.BlockedByMasterSwitch = _ => rig!.MasterSwitchOff;
        rig = new Rig { H = h, Engine = engine, Router = router, Handler = new EventRemoteHandler(engine, h.Events, h.Log) };
        engine.SetWireSender(rig.Wire.Add);
        return rig;
    }

    private static ScheduledEvent Command(string name, string text = "stat", bool disabled = false) => new()
    {
        Name = name, TriggerType = EventTriggerType.Every, EveryAmount = 30, EveryUnit = EventTimeUnit.Minutes,
        ActionType = EventActionType.Command, CommandText = text, Disabled = disabled,
    };

    private static ScheduledEvent BankTrip(string name) => new()
    {
        Name = name, TriggerType = EventTriggerType.AtTime, AtTime = "21:30", ActionType = EventActionType.BankTrip,
    };

    // A loop event whose loop isn't in the catalogue: the reconciler switches it
    // off the moment it is added.
    private static ScheduledEvent LoopNobodySaved(string name) => new()
    {
        Name = name, TriggerType = EventTriggerType.Logon, ActionType = EventActionType.Loop, LoopName = "Gone Loop",
    };

    private static string Reply(string text) => $"/{Friend} {{{text}}}\r";

    // ----- @events ---------------------------------------------------------

    [Fact]
    public void Events_WithNoneDefined_SaysSo()
    {
        using Rig r = Build();

        Assert.Equal(new[] { Reply("no events defined") }, r.Ask("@events"));
    }

    [Fact]
    public void Events_ListsEachByName_OnOrOff_InTheTabsOrder()
    {
        using Rig r = Build();
        r.Events.Add(Command("Night stats"));
        r.Events.Add(BankTrip("Bank run"));
        r.Events.Add(Command("Sell junk", disabled: true));

        Assert.Equal(new[] { Reply("events: 1 Night stats on, 2 Bank run on, 3 Sell junk off") }, r.Ask("@events"));
    }

    [Fact]
    public void Events_MarksOneSwitchedOffForAMissingTarget()
    {
        using Rig r = Build();
        r.Events.Add(Command("Night stats"));
        r.Events.Add(LoopNobodySaved("Lair loop"));

        Assert.Equal(new[] { Reply("events: 1 Night stats on, 2 Lair loop off (target missing)") }, r.Ask("@events"));
    }

    [Fact]
    public void Events_WithDisableAllEventsTicked_SaysSoFirst()
    {
        using Rig r = Build();
        r.Events.Add(Command("Night stats"));
        r.H.Profile.Current!.EventsGloballyDisabled = true;

        Assert.Equal(new[] { Reply("events (all disabled): 1 Night stats on") }, r.Ask("@events"));
    }

    [Fact]
    public void Events_AnUnnamedEvent_IsListedAsUnnamed()
    {
        using Rig r = Build();
        r.Events.Add(Command(""));

        Assert.Equal(new[] { Reply("events: 1 (unnamed) on") }, r.Ask("@events"));
    }

    // More than one telepath holds: numbered parts, every event in one of them,
    // in order, and none over the line the game takes.
    [Fact]
    public void Events_MoreThanFitOneReply_AreSentInNumberedParts()
    {
        using Rig r = Build();
        for (int i = 1; i <= 30; i++) r.Events.Add(Command($"Scheduled event number {i:00}"));

        List<string> sent = r.Ask("@events");

        Assert.True(sent.Count > 1);
        for (int i = 0; i < sent.Count; i++)
        {
            Assert.StartsWith($"/{Friend} {{events ({i + 1}/{sent.Count}): ", sent[i]);
            Assert.True(sent[i].TrimEnd('\r').Length <= 225, sent[i]);
        }
        // The numbering runs on through the parts: each event carries its place in
        // the whole list, not in its part.
        string all = string.Concat(sent);
        int at = -1;
        for (int i = 1; i <= 30; i++)
        {
            int found = all.IndexOf($" {i} Scheduled event number {i:00} on", StringComparison.Ordinal);
            Assert.True(found > at, $"event {i} missing or out of order");
            at = found;
        }
        Assert.Contains("): 1 Scheduled event number 01 on, 2 Scheduled", sent[0]);
        Assert.DoesNotContain("): 1 ", sent[1]);
    }

    // What an event sends is the user's own: a password typed into a command, the
    // names of its loop, its Auto-Lair setup, its boss, its rooms. No form says any
    // of it. The one-event answer does give the schedule the event runs on, which
    // the owner asked for: a period or a time of day, and nothing typed.
    [Fact]
    public void TheOneEventAnswer_GivesTheSchedule_AndNoAnswerCarriesWhatAnEventSendsOrNames()
    {
        using Rig r = Build();
        r.Events.Add(Command("Log in helper", text: "hunter2secret"));
        r.Events.Add(LoopNobodySaved("Lair loop"));
        r.Events.Add(new ScheduledEvent
        {
            Name = "Boss watch", TriggerType = EventTriggerType.Boss, BossName = "The Warden", BossLeadMinutes = 7,
            ActionType = EventActionType.AutoLair, AutoLairSetupName = "Crypt circuit",
            StopBossName = "Old Gatekeeper", Then = EventThenType.Loop, ThenLoopName = "Homeward",
        });
        r.Events.Add(new ScheduledEvent
        {
            Name = "Nightly haul", TriggerType = EventTriggerType.AtTime, AtTime = "03:15",
            ActionType = EventActionType.StashTransfer,
            TransferStash = new RoomRef(9, 413), TransferBank = new RoomRef(1, 2203),
        });
        r.Events.Add(new ScheduledEvent
        {
            Name = "Go north", TriggerType = EventTriggerType.Every, EveryAmount = 45, EveryUnit = EventTimeUnit.Minutes,
            ActionType = EventActionType.WalkTo, WalkToTarget = new RoomRef(17, 1488),
            Then = EventThenType.Event, ThenEventName = "Secret follow-up",
        });

        string asked = string.Concat(r.Ask("@event log in helper"))
            + string.Concat(r.Ask("@event lair loop"))
            + string.Concat(r.Ask("@event boss watch"))
            + string.Concat(r.Ask("@event nightly haul"))
            + string.Concat(r.Ask("@event 5"));
        string everything = asked
            + string.Concat(r.Ask("@events"))
            + string.Concat(r.Ask("@event lair loop on"))
            + string.Concat(r.Ask("@event boss watch on"))
            + string.Concat(r.Ask("@event 3 off"))
            + string.Concat(r.Ask("@event haul watch"));

        Assert.Contains("event 'Log in helper' is on: fires every 30 minutes, sends a command", asked);
        Assert.Contains("event 'Boss watch' is off (target missing): fires on a boss timer, runs Auto-Lair", asked);
        Assert.Contains("event 'Nightly haul' is on: fires at 03:15 daily, moves a stash to the bank", asked);
        Assert.Contains("event 5 'Go north' is on: fires every 45 minutes, walks to a room", asked);

        foreach (string kept in new[]
        {
            "hunter2secret", "Gone Loop", "Warden", "Crypt circuit", "Gatekeeper", "Homeward", "Secret follow-up",
            "413", "2203", "1488", "17/", "9/",
        })
            Assert.DoesNotContain(kept, everything);
    }

    [Fact]
    public void Events_WithWordsAfterIt_IsRefused_AndSwitchesNothing()
    {
        using Rig r = Build();
        ScheduledEvent e = Command("Night stats");
        r.Events.Add(e);

        Assert.Equal(
            new[] { Reply("usage: @events lists them; @event <name or number> on|off switches one") }, r.Ask("@events off"));
        Assert.False(e.Disabled);
    }

    // ----- @event <name> ----------------------------------------------------

    [Fact]
    public void Event_Bare_SaysItsState_ItsSchedule_AndItsKind()
    {
        using Rig r = Build();
        r.Events.Add(BankTrip("Bank run"));
        r.Events.Add(Command("Sell junk", disabled: true));

        Assert.Equal(new[] { Reply("event 'Bank run' is on: fires at 21:30 daily, makes a bank trip") }, r.Ask("@event bank run"));
        Assert.Equal(new[] { Reply("event 'Sell junk' is off: fires every 30 minutes, sends a command") }, r.Ask("@event sell junk"));
    }

    // The schedule of a trigger that runs on a clock, worded as the Events tab's
    // When column words it; the kind alone for the triggers that have none, and
    // for a boss timer, whose boss is not said.
    [Theory]
    [InlineData(EventTriggerType.Every, 1, EventTimeUnit.Seconds, null, "fires every 1 second", "Every 1 second")]
    [InlineData(EventTriggerType.Every, 90, EventTimeUnit.Seconds, null, "fires every 90 seconds", "Every 90 seconds")]
    [InlineData(EventTriggerType.Every, 1, EventTimeUnit.Minutes, null, "fires every 1 minute", "Every 1 minute")]
    [InlineData(EventTriggerType.Every, 1, EventTimeUnit.Hours, null, "fires every 1 hour", "Every 1 hour")]
    [InlineData(EventTriggerType.Every, 12, EventTimeUnit.Hours, null, "fires every 12 hours", "Every 12 hours")]
    [InlineData(EventTriggerType.AtTime, null, null, "06:05", "fires at 06:05 daily", "At 06:05")]
    [InlineData(EventTriggerType.AtTime, null, null, "23:59", "fires at 23:59 daily", "At 23:59")]
    [InlineData(EventTriggerType.Logon, null, null, null, "fires on logon", "On Logon")]
    [InlineData(EventTriggerType.Logoff, null, null, null, "fires on logoff", "On Logoff")]
    [InlineData(EventTriggerType.Relog, null, null, null, "fires on re-log", "On Re-log")]
    [InlineData(EventTriggerType.Boss, null, null, null, "fires on a boss timer", null)]
    [InlineData(EventTriggerType.State, null, null, null, "fires on a condition", null)]
    public void Event_Bare_GivesTheSchedule_AsTheEventsTabWordsIt(
        EventTriggerType trigger, int? amount, EventTimeUnit? unit, string? atTime, string fires, string? tab)
    {
        using Rig r = Build();
        ScheduledEvent e = new()
        {
            Name = "Probe", TriggerType = trigger, EveryAmount = amount, EveryUnit = unit, AtTime = atTime,
            BossName = "The Warden", ActionType = EventActionType.RestUp,
        };
        r.Events.Add(e);

        Assert.Equal(new[] { Reply($"event 'Probe' is on: {fires}, rests up") }, r.Ask("@event probe"));
        // The tab's own words for the same trigger, where it has a schedule to
        // word: the answer's are those, in lower case.
        if (tab is not null)
        {
            string tabText = new EventRowViewModel(e, isAutoDisabled: false).TimeText;
            Assert.Equal(tab, tabText);
            if (trigger is EventTriggerType.Every or EventTriggerType.AtTime)
                Assert.Contains(char.ToLowerInvariant(tabText[0]) + tabText[1..], fires);
        }
    }

    // A time or a period that doesn't parse (a profile edited by hand) is not
    // sent as it was typed: the kind alone.
    [Theory]
    [InlineData(EventTriggerType.AtTime, null, null, "tea time (map 1, room 5)", "fires at a set time")]
    [InlineData(EventTriggerType.AtTime, null, null, "9:30", "fires at a set time")]
    [InlineData(EventTriggerType.AtTime, null, null, null, "fires at a set time")]
    [InlineData(EventTriggerType.Every, 0, EventTimeUnit.Minutes, null, "fires on a timer")]
    [InlineData(EventTriggerType.Every, -5, EventTimeUnit.Minutes, null, "fires on a timer")]
    [InlineData(EventTriggerType.Every, null, EventTimeUnit.Minutes, null, "fires on a timer")]
    [InlineData(EventTriggerType.Every, 5, null, null, "fires on a timer")]
    public void Event_Bare_AScheduleThatDoesntParse_GivesTheKindAlone(
        EventTriggerType trigger, int? amount, EventTimeUnit? unit, string? atTime, string fires)
    {
        using Rig r = Build();
        r.Events.Add(new ScheduledEvent
        {
            Name = "Probe", TriggerType = trigger, EveryAmount = amount, EveryUnit = unit, AtTime = atTime,
            ActionType = EventActionType.RestUp,
        });

        Assert.Equal(new[] { Reply($"event 'Probe' is on: {fires}, rests up") }, r.Ask("@event probe"));
    }

    // The list doesn't carry the schedule: the name, the number and the state.
    [Fact]
    public void Events_TheList_DoesNotGiveTheSchedule()
    {
        using Rig r = Build();
        r.Events.Add(BankTrip("Bank run"));
        r.Events.Add(Command("Sell junk"));

        string list = Assert.Single(r.Ask("@events"));

        Assert.Equal(Reply("events: 1 Bank run on, 2 Sell junk on"), list);
        Assert.DoesNotContain("21:30", list);
        Assert.DoesNotContain("30 minutes", list);
    }

    [Fact]
    public void Event_Bare_WithDisableAllEventsTicked_SaysSo()
    {
        using Rig r = Build();
        r.Events.Add(BankTrip("Bank run"));
        r.H.Profile.Current!.EventsGloballyDisabled = true;

        Assert.Equal(
            new[] { Reply("event 'Bank run' is on: fires at 21:30 daily, makes a bank trip; all events are disabled") },
            r.Ask("@event bank run"));
    }

    // An event whose own name ends in a state word can still be asked about.
    [Fact]
    public void Event_WhoseWholeNameEndsInOn_IsAskedAbout_NotSwitched()
    {
        using Rig r = Build();
        ScheduledEvent e = Command("Lights on", disabled: true);
        r.Events.Add(e);

        Assert.Equal(new[] { Reply("event 'Lights on' is off: fires every 30 minutes, sends a command") }, r.Ask("@event lights on"));
        Assert.True(e.Disabled);

        Assert.Equal(new[] { Reply("event 'Lights on' switched on") }, r.Ask("@event lights on on"));
        Assert.False(e.Disabled);
    }

    // ----- @event <name> on | off --------------------------------------------

    [Theory]
    [InlineData("off")]
    [InlineData("disable")]
    [InlineData("OFF")]
    public void Event_Off_ByExactName_SwitchesItOff(string word)
    {
        using Rig r = Build();
        ScheduledEvent e = BankTrip("Bank run");
        r.Events.Add(e);

        Assert.Equal(new[] { Reply("event 'Bank run' switched off") }, r.Ask($"@event Bank run {word}"));
        Assert.True(e.Disabled);
        Assert.False(r.Events.IsAutoDisabled(e));
    }

    [Theory]
    [InlineData("on")]
    [InlineData("enable")]
    public void Event_On_SwitchesItOn(string word)
    {
        using Rig r = Build();
        ScheduledEvent e = Command("Sell junk", disabled: true);
        r.Events.Add(e);

        Assert.Equal(new[] { Reply("event 'Sell junk' switched on") }, r.Ask($"@event sell junk {word}"));
        Assert.False(e.Disabled);
    }

    // The words of the name in any order, as @loop takes a loop's.
    [Fact]
    public void Event_ByItsWordsInAnyOrder_IsFound()
    {
        using Rig r = Build();
        ScheduledEvent e = BankTrip("Evening bank run");
        r.Events.Add(e);
        r.Events.Add(Command("Night stats"));

        Assert.Equal(new[] { Reply("event 'Evening bank run' switched off") }, r.Ask("@event run bank off"));
        Assert.True(e.Disabled);
    }

    // A name that is one event's whole name wins over others that only hold it.
    [Fact]
    public void Event_AnExactName_WinsOverNamesThatContainIt()
    {
        using Rig r = Build();
        ScheduledEvent exact = BankTrip("Bank");
        r.Events.Add(exact);
        r.Events.Add(BankTrip("Bank run"));

        Assert.Equal(new[] { Reply("event 'Bank' switched off") }, r.Ask("@event bank off"));
        Assert.True(exact.Disabled);
    }

    [Fact]
    public void Event_ANameThatFitsSeveral_IsRefusedWithTheCandidates_AndSwitchesNothing()
    {
        using Rig r = Build();
        ScheduledEvent a = BankTrip("Morning bank run");
        ScheduledEvent b = BankTrip("Evening bank run");
        r.Events.Add(a);
        r.Events.Add(b);

        Assert.Equal(
            new[] { Reply("event name 'bank run' matches 2: 'Morning bank run' (number 1), 'Evening bank run' (number 2)") },
            r.Ask("@event bank run off"));
        Assert.False(a.Disabled);
        Assert.False(b.Disabled);
    }

    [Fact]
    public void Event_ANameNothingFits_IsRefused()
    {
        using Rig r = Build();
        r.Events.Add(BankTrip("Bank run"));

        Assert.Equal(new[] { Reply("no event named 'boss hunt'") }, r.Ask("@event boss hunt off"));
        Assert.Equal(new[] { Reply("no event named 'boss hunt'") }, r.Ask("@event boss hunt"));
    }

    [Theory]
    [InlineData("@event")]
    [InlineData("@event on")]
    [InlineData("@event off")]
    public void Event_WithNoName_IsRefusedWithTheUsage(string command)
    {
        using Rig r = Build();
        r.Events.Add(BankTrip("Bank run"));

        Assert.Equal(new[] { Reply("usage: @event <name or number> [on|off]") }, r.Ask(command));
    }

    [Fact]
    public void Event_AlreadyInThatState_SaysSo_AndSavesNothing()
    {
        using Rig r = Build();
        ScheduledEvent on = BankTrip("Bank run");
        ScheduledEvent off = Command("Sell junk", disabled: true);
        r.Events.Add(on);
        r.Events.Add(off);
        int changes = 0;
        r.Events.Events.CollectionChanged += (_, _) => changes++;

        Assert.Equal(new[] { Reply("event 'Bank run' is already on") }, r.Ask("@event bank run on"));
        Assert.Equal(new[] { Reply("event 'Sell junk' is already off") }, r.Ask("@event sell junk off"));
        Assert.Equal(0, changes);
    }

    // An event the client switched off because its loop is gone could not run:
    // a remote command doesn't switch it on.
    [Fact]
    public void Event_On_ForOneWhoseTargetIsMissing_IsRefused()
    {
        using Rig r = Build();
        ScheduledEvent e = LoopNobodySaved("Lair loop");
        r.Events.Add(e);
        Assert.True(r.Events.IsAutoDisabled(e));

        Assert.Equal(new[] { Reply("event 'Lair loop' can't be switched on: its loop is missing") }, r.Ask("@event lair loop on"));
        Assert.True(e.Disabled);
        Assert.Equal(new[] { Reply("event 'Lair loop' is off (target missing): fires on logon, runs a loop") }, r.Ask("@event lair loop"));
    }

    // Switched off in the middle of its run: the run goes on to its end, and the
    // answer says so.
    [Fact]
    public void Event_Off_WhileItsRunIsUnderWay_SaysTheRunFinishes()
    {
        using Rig r = Build();
        ScheduledEvent e = new()
        {
            Name = "Hold still", TriggerType = EventTriggerType.Every, EveryAmount = 1, EveryUnit = EventTimeUnit.Hours,
            ActionType = EventActionType.Wait, WaitSeconds = 600, Then = EventThenType.Nothing,
        };
        r.Events.Add(e);
        Assert.True(r.Events.Fire(e));
        Assert.True(r.Events.IsRunning(e));

        Assert.Equal(new[] { Reply("event 'Hold still' switched off; the run it has under way finishes") }, r.Ask("@event hold still off"));
        Assert.True(r.Events.IsRunning(e));
        Assert.False(r.Events.Fire(e));            // and it fires no more
    }

    [Fact]
    public void Event_On_WithDisableAllEventsTicked_SaysItWontFireYet()
    {
        using Rig r = Build();
        r.Events.Add(Command("Sell junk", disabled: true));
        r.H.Profile.Current!.EventsGloballyDisabled = true;

        Assert.Equal(
            new[] { Reply("event 'Sell junk' switched on; all events are disabled, so it won't fire yet") },
            r.Ask("@event sell junk on"));
    }

    // ----- who is obeyed, and what a refusal gives away -------------------------

    // Without the grant: the generic failure and nothing else, whether or not an
    // event of that name exists.
    [Theory]
    [InlineData("@events")]
    [InlineData("@event bank run")]
    [InlineData("@event bank run off")]
    [InlineData("@event no such thing")]
    [InlineData("@event no such thing off")]
    public void WithoutAlterSettings_OnlyTheGenericFailure_WhateverWasNamed(string command)
    {
        using Rig r = Build();
        ScheduledEvent e = BankTrip("Bank run");
        r.Events.Add(e);

        r.Tell(Stranger, command);

        Assert.Equal(new[] { $"/{Stranger} {{command invalid or not allowed}}\r" }, r.Sent);
        Assert.False(e.Disabled);
    }

    [Fact]
    public void WithoutAlterSettings_AndWarnOff_Silence()
    {
        using Rig r = Build();
        r.Events.Add(BankTrip("Bank run"));
        r.Engine.WarnOnDenial = false;

        r.Tell(Stranger, "@events");
        r.Tell(Stranger, "@event bank run off");

        Assert.Empty(r.Sent);
    }

    // The Warn setting silences every refusal and no answer.
    [Fact]
    public void WarnOff_RefusalsAreSilent_AnswersAreNot()
    {
        using Rig r = Build();
        r.Events.Add(BankTrip("Morning bank run"));
        r.Events.Add(BankTrip("Evening bank run"));
        r.Events.Add(LoopNobodySaved("Lair loop"));
        r.Engine.WarnOnDenial = false;

        Assert.Empty(r.Ask("@event"));
        Assert.Empty(r.Ask("@event boss hunt off"));
        Assert.Empty(r.Ask("@event bank run off"));
        Assert.Empty(r.Ask("@event lair loop on"));
        Assert.Empty(r.Ask("@events now"));

        Assert.Single(r.Ask("@events"));
        Assert.Single(r.Ask("@event morning bank run"));
        Assert.Equal(new[] { Reply("event 'Morning bank run' switched off") }, r.Ask("@event morning bank run off"));
    }

    // With the master switch off no remote command but @auto-all is followed.
    [Fact]
    public void MasterSwitchOff_NeitherIsFollowed_NorAnswered()
    {
        using Rig r = Build();
        ScheduledEvent e = BankTrip("Bank run");
        r.Events.Add(e);
        r.MasterSwitchOff = true;

        Assert.Empty(r.Ask("@events"));
        Assert.Empty(r.Ask("@event bank run off"));
        Assert.False(e.Disabled);
    }

    [Fact]
    public void ATrailingBang_OnEitherCommand_IsIgnored()
    {
        using Rig r = Build();
        ScheduledEvent e = BankTrip("Bank run");
        r.Events.Add(e);

        Assert.Equal(new[] { Reply("events: 1 Bank run on") }, r.Ask("@events!"));
        Assert.Equal(new[] { Reply("event 'Bank run' switched off") }, r.Ask("@event! bank run off"));
        Assert.True(e.Disabled);
    }

    [Fact]
    public void BothAnswer_OnTheChannelTheyCameInOn()
    {
        using Rig r = Build();
        r.Events.Add(BankTrip("Bank run"));

        ChatLines.Hear(r.Router, ChatChannel.Gangpath, Friend, "@events");
        ChatLines.Hear(r.Router, ChatChannel.Local, Friend, "@event bank run");

        Assert.Equal("bg {events: 1 Bank run on}\r", r.Sent[0]);
        Assert.StartsWith($">{Friend} {{event 'Bank run' is on", r.Sent[1]);
    }

    // ----- the local API --------------------------------------------------------

    [Fact]
    public void LocalInvocation_ListsAndSwitches_AndIsToldWhyOnARefusal()
    {
        using Rig r = Build();
        ScheduledEvent e = BankTrip("Bank run");
        r.Events.Add(e);
        r.Engine.WarnOnDenial = false;      // the caller has the machine: told all the same
        List<string> replies = new();

        Assert.Equal(RemoteCommandManager.LocalInvokeResult.Ok, r.Engine.TryInvokeLocal("@events", null, replies.Add));
        Assert.Equal(RemoteCommandManager.LocalInvokeResult.Ok,
            r.Engine.TryInvokeLocal("event", new[] { "bank", "run", "off" }, replies.Add));
        Assert.Equal(RemoteCommandManager.LocalInvokeResult.Ok,
            r.Engine.TryInvokeLocal("@event", new[] { "boss", "hunt", "on" }, replies.Add));

        Assert.Equal(
            new[] { "events: 1 Bank run on", "event 'Bank run' switched off", "no event named 'boss hunt'" }, replies);
        Assert.True(e.Disabled);
        Assert.Empty(r.Sent);               // nothing went out on a chat channel
        Assert.False(LocalApiActions_IsDestructive("@event"));
    }

    private static bool LocalApiActions_IsDestructive(string command) =>
        MudPlay.Services.Api.LocalApiActions.IsDestructive(command);

    [Fact]
    public void LocalInvocation_WithTheMasterSwitchOff_IsNotFollowed()
    {
        using Rig r = Build();
        ScheduledEvent e = BankTrip("Bank run");
        r.Events.Add(e);
        r.MasterSwitchOff = true;

        Assert.Equal(RemoteCommandManager.LocalInvokeResult.MasterSwitchOff,
            r.Engine.TryInvokeLocal("@event", new[] { "bank", "run", "off" }, _ => { }));
        Assert.False(e.Disabled);
    }

    // ----- what a switch does elsewhere -----------------------------------------

    // The list raises a Replace carrying the same event: what the scheduler, an
    // open Events tab and an open edit window each go by.
    [Fact]
    public void ASwitch_RaisesAReplaceForThatEvent()
    {
        using Rig r = Build();
        r.Events.Add(Command("Night stats"));
        ScheduledEvent e = BankTrip("Bank run");
        r.Events.Add(e);
        List<NotifyCollectionChangedEventArgs> raised = new();
        r.Events.Events.CollectionChanged += (_, args) => raised.Add(args);

        r.Ask("@event bank run off");

        NotifyCollectionChangedEventArgs change = Assert.Single(raised);
        Assert.Equal(NotifyCollectionChangedAction.Replace, change.Action);
        Assert.Equal(1, change.NewStartingIndex);
        Assert.Same(e, change.NewItems![0]);
        Assert.Same(e, r.Events.Events[1]);
    }

    // The Events tab, open while a remote command switches an event: the row shows
    // it at once, and the selection stays where it was.
    [Fact]
    public void TheOpenEventsTab_ShowsARemoteSwitch_AndKeepsItsSelection()
    {
        using Rig r = Build();
        r.Events.Add(Command("Night stats"));
        r.Events.Add(BankTrip("Bank run"));
        using EventsSectionViewModel tab = new(r.Events, r.H.Profile);
        tab.SelectedRow = tab.Rows[1];
        Assert.False(tab.Rows[1].ShowsOffBadge);

        r.Ask("@event bank run off");

        Assert.True(tab.Rows[1].IsDisabled);
        Assert.True(tab.Rows[1].ShowsOffBadge);
        Assert.False(tab.Rows[0].ShowsOffBadge);
        Assert.Same(r.Events.Events[1], tab.SelectedRow!.Source);

        r.Ask("@event bank run on");
        Assert.False(tab.Rows[1].ShowsOffBadge);
    }

    // An event the reconciler switched off shows the target-missing badge, not
    // the plain "off" one.
    [Fact]
    public void TheOpenEventsTab_AnAutoDisabledEvent_ShowsTargetMissing_NotOff()
    {
        using Rig r = Build();
        r.Events.Add(LoopNobodySaved("Lair loop"));
        using EventsSectionViewModel tab = new(r.Events, r.H.Profile);

        Assert.True(tab.Rows[0].IsAutoDisabled);
        Assert.False(tab.Rows[0].ShowsOffBadge);
    }

    // The edit window, open on an event a remote command then switches: its
    // Disabled box moves with the switch and what else was typed there stays, so
    // Save keeps both, and the note of who switched it.
    [Fact]
    public void TheOpenEditor_FollowsARemoteSwitch_AndSaveKeepsItWithTheOtherEdits()
    {
        using Rig r = Build();
        r.Events.Add(Command("Night stats"));
        ScheduledEvent e = Command("Sell junk");
        r.Events.Add(e);
        EventEditDialogViewModel editor = new(e, isNew: false) { Name = "Sell the junk", CommandText = "sell all" };
        Action stop = editor.FollowSwitches(r.Events.Events, e);
        Assert.False(editor.DisabledFlag);

        r.Ask("@event night stats off");            // another event: not this window's
        Assert.False(editor.DisabledFlag);
        r.Ask("@event sell junk off");

        Assert.True(editor.DisabledFlag);
        Assert.Equal("Sell the junk", editor.Name);
        Assert.Equal("sell all", editor.CommandText);

        ScheduledEvent? saved = null;
        editor.CloseRequested += result => saved = result;
        editor.SaveCommand.Execute(null);
        stop();
        Assert.True(r.Events.Replace(e, saved!));

        Assert.True(saved!.Disabled);
        Assert.Equal("Sell the junk", saved.Name);
        Assert.Equal("sell all", saved.CommandText);
        Assert.StartsWith("switched off by remote @event from Friend at ", r.Events.LastSwitch(saved));
    }

    // Cancel saves nothing, so the switch made while the window was open stands.
    // The box also stays the user's to set: ticked back before Save, the event is
    // saved on, and the note of the remote switch goes with the state it described.
    [Fact]
    public void TheOpenEditor_Cancelled_LeavesTheRemoteSwitch_AndTheBoxStaysTheUsers()
    {
        using Rig r = Build();
        ScheduledEvent e = Command("Sell junk");
        r.Events.Add(e);
        EventEditDialogViewModel cancelled = new(e, isNew: false) { Name = "Never saved" };
        Action stop = cancelled.FollowSwitches(r.Events.Events, e);
        r.Ask("@event sell junk off");
        bool closed = false;
        ScheduledEvent? result = e;
        cancelled.CloseRequested += given => { closed = true; result = given; };
        cancelled.CancelCommand.Execute(null);
        stop();

        Assert.True(closed);
        Assert.Null(result);
        Assert.True(e.Disabled);
        Assert.Equal("Sell junk", e.Name);
        Assert.Same(e, r.Events.Events[0]);

        // Once the window is closed nothing follows any more.
        r.Ask("@event sell junk on");
        Assert.True(cancelled.DisabledFlag);

        EventEditDialogViewModel overruled = new(e, isNew: false);
        Action stopSecond = overruled.FollowSwitches(r.Events.Events, e);
        r.Ask("@event sell junk off");
        Assert.True(overruled.DisabledFlag);
        overruled.DisabledFlag = false;
        ScheduledEvent? saved = null;
        overruled.CloseRequested += given => saved = given;
        overruled.SaveCommand.Execute(null);
        stopSecond();
        r.Events.Replace(e, saved!);

        Assert.False(saved!.Disabled);
        Assert.Null(r.Events.LastSwitch(saved));
    }

    // Logged at Info with who and which way; shown on the bug report's event line.
    [Fact]
    public void ASwitch_IsLoggedWithWhoAsked_AndKeptForTheBugReport()
    {
        using Rig r = Build();
        ScheduledEvent e = BankTrip("Bank run");
        r.Events.Add(e);
        Assert.Null(r.Events.LastSwitch(e));

        r.Ask("@event bank run off");

        Assert.Contains(r.H.EventLog, line => line.Contains("Event 'Bank run' switched off by remote @event from Friend"));
        Assert.StartsWith("switched off by remote @event from Friend at ", r.Events.LastSwitch(e));
    }

    // Saved with the character at once, as the edit window's box is: loaded
    // again, the event is still off.
    [Fact]
    public void ASwitch_IsSavedWithTheCharacter_AndStillSetAfterAReload()
    {
        new ProfileService().CreateProfile(_bbs, "Tester");
        using Rig r = Build();
        r.H.Profile.Load(_bbs, "Tester");
        r.Events.Add(BankTrip("Bank run"));
        r.Events.Add(Command("Sell junk", disabled: true));

        r.Ask("@event bank run off");
        r.Ask("@event sell junk on");

        ProfileService reloaded = new();
        CharacterProfile again = reloaded.Load(_bbs, "Tester");
        Assert.True(again.Events!.Single(e => e.Name == "Bank run").Disabled);
        Assert.False(again.Events!.Single(e => e.Name == "Sell junk").Disabled);
    }

    // ----- EventManager.FindByName, the rule @loop uses for a loop ----------------

    [Fact]
    public void FindByName_ExactThenWordsInAnyOrder_ThenWithoutApostrophes()
    {
        using Rig r = Build();
        r.Events.Add(BankTrip("King's Road run"));
        r.Events.Add(BankTrip("Bank"));
        r.Events.Add(BankTrip("Bank run"));
        r.Events.Add(Command(""));

        Assert.Equal(new[] { "Bank" }, r.Events.FindByName("BANK").Select(e => e.Name));
        Assert.Equal(new[] { "King's Road run", "Bank run" }, r.Events.FindByName("run").Select(e => e.Name));
        Assert.Equal(new[] { "King's Road run" }, r.Events.FindByName("kings road").Select(e => e.Name));
        Assert.Empty(r.Events.FindByName("nothing like it"));
        Assert.Empty(r.Events.FindByName("  "));
    }

    // Two events of one name: an exact name fits both, and neither is picked.
    [Fact]
    public void Event_TwoEventsOfOneName_IsAnAmbiguity()
    {
        using Rig r = Build();
        r.Events.Add(BankTrip("Bank run"));
        r.Events.Add(Command("Bank run"));

        Assert.Equal(
            new[] { Reply("event name 'bank run' matches 2: 'Bank run' (numbers 1, 2); use a number in place of the name") },
            r.Ask("@event bank run off"));
    }

    // Names that differ only by case are one name to the match, and are said once.
    [Fact]
    public void Event_NamesThatDifferOnlyByCase_AreOneNameInTheRefusal()
    {
        using Rig r = Build();
        r.Events.Add(BankTrip("Bank run"));
        r.Events.Add(Command("BANK RUN"));
        r.Events.Add(Command("bank run"));
        r.Events.Add(Command("Sell run"));

        Assert.Equal(
            new[] { Reply("event name 'run' matches 4: 'Bank run' (numbers 1, 2, 3), 'Sell run' (number 4); use a number in place of the name") },
            r.Ask("@event run"));
    }

    // More names fit the typed words than one refusal says: four, and the count
    // of the rest.
    [Fact]
    public void Event_MoreThanFourNamesFit_FourAreSaid_AndHowManyMore()
    {
        using Rig r = Build();
        foreach (string place in new[] { "north", "south", "east", "west", "upper", "lower", "inner" })
            r.Events.Add(Command($"Patrol {place}"));

        Assert.Equal(
            new[] { Reply("event name 'patrol' matches 7: 'Patrol north' (number 1), 'Patrol south' (number 2), 'Patrol east' (number 3), 'Patrol west' (number 4), +3 more") },
            r.Ask("@event patrol off"));
        Assert.All(r.Events.Events, e => Assert.False(e.Disabled));
    }

    // ----- by number -------------------------------------------------------------

    // An event with no name can't be named: its number in the list reaches it.
    // The answer says the number and that it has no name.
    [Fact]
    public void Event_ByNumber_AnUnnamedEvent_IsAskedAboutAndSwitched()
    {
        using Rig r = Build();
        r.Events.Add(Command("Night stats"));
        ScheduledEvent unnamed = Command("");
        r.Events.Add(unnamed);
        r.Events.Add(BankTrip("Bank run"));

        Assert.Equal(new[] { Reply("events: 1 Night stats on, 2 (unnamed) on, 3 Bank run on") }, r.Ask("@events"));
        Assert.Equal(
            new[] { Reply("event 2 (unnamed) is on: fires every 30 minutes, sends a command") }, r.Ask("@event 2"));
        Assert.Equal(new[] { Reply("event 2 (unnamed) switched off") }, r.Ask("@event 2 off"));
        Assert.True(unnamed.Disabled);
        Assert.Equal(new[] { Reply("event 2 (unnamed) is already off") }, r.Ask("@event 2 disable"));
        Assert.Equal(new[] { Reply("event 2 (unnamed) switched on") }, r.Ask("@event 2 enable"));
        Assert.False(unnamed.Disabled);
        Assert.All(new[] { r.Events.Events[0], r.Events.Events[2] }, e => Assert.False(e.Disabled));
    }

    // A named event by its number: the answer names it, so a sender whose list is
    // out of date sees which event the number reached. "#3" is the same number.
    [Fact]
    public void Event_ByNumber_ANamedEvent_IsNamedInTheAnswer()
    {
        using Rig r = Build();
        r.Events.Add(Command("Night stats"));
        r.Events.Add(Command("Sell junk"));
        ScheduledEvent bank = BankTrip("Bank run");
        r.Events.Add(bank);

        Assert.Equal(new[] { Reply("event 3 'Bank run' switched off") }, r.Ask("@event 3 off"));
        Assert.True(bank.Disabled);
        Assert.StartsWith("switched off by remote @event from Friend (by number 3) at ", r.Events.LastSwitch(bank));
        Assert.Equal(new[] { Reply("event 3 'Bank run' switched on") }, r.Ask("@event #3 on"));
        Assert.Equal(new[] { Reply("event 3 'Bank run' is already on") }, r.Ask("@event 3 on"));
        Assert.Equal(new[] { Reply("event 3 'Bank run' is on: fires at 21:30 daily, makes a bank trip") }, r.Ask("@event #3"));
    }

    [Fact]
    public void Event_ByNumber_OneWhoseTargetIsMissing_IsRefusedByNumberAndName()
    {
        using Rig r = Build();
        r.Events.Add(LoopNobodySaved("Lair loop"));

        Assert.Equal(
            new[] { Reply("event 1 'Lair loop' can't be switched on: its loop is missing") }, r.Ask("@event 1 on"));
    }

    // No such place in the list: refused with how many there are. Zero and a
    // negative number are numbers too, and are not hunted for as names.
    [Theory]
    [InlineData("@event 4 off", "no event number 4: there are 3")]
    [InlineData("@event 4", "no event number 4: there are 3")]
    [InlineData("@event #9 on", "no event number 9: there are 3")]
    [InlineData("@event 0", "no event number 0: there are 3")]
    [InlineData("@event 0 off", "no event number 0: there are 3")]
    [InlineData("@event -1 off", "no event number -1: there are 3")]
    [InlineData("@event #-2", "no event number -2: there are 3")]
    public void Event_ByANumberOutOfRange_IsRefusedWithHowManyThereAre(string command, string refusal)
    {
        using Rig r = Build();
        r.Events.Add(Command("Night stats"));
        r.Events.Add(Command("Sell junk"));
        r.Events.Add(BankTrip("Bank run"));

        Assert.Equal(new[] { Reply(refusal) }, r.Ask(command));
        Assert.All(r.Events.Events, e => Assert.False(e.Disabled));
    }

    [Fact]
    public void Event_ByANumberOutOfRange_WithOneEventOrNone_ReadsRight()
    {
        using Rig r = Build();
        Assert.Equal(new[] { Reply("no event number 1: no events defined") }, r.Ask("@event 1 off"));

        r.Events.Add(Command("Night stats"));
        Assert.Equal(new[] { Reply("no event number 2: there is 1") }, r.Ask("@event 2 off"));
    }

    // An event whose whole name is a number: the name comes first, and the list
    // shows both, so the sender can see which is which. "#1" is always the place.
    [Fact]
    public void Event_NamedWithANumber_TheNameWins_AndTheHashFormIsThePlace()
    {
        using Rig r = Build();
        ScheduledEvent first = BankTrip("Bank run");
        ScheduledEvent namedOne = Command("1");
        r.Events.Add(first);
        r.Events.Add(namedOne);
        r.Events.Add(Command("Sell junk"));

        Assert.Equal(new[] { Reply("event '1' switched off") }, r.Ask("@event 1 off"));
        Assert.True(namedOne.Disabled);
        Assert.False(first.Disabled);

        Assert.Equal(new[] { Reply("event 1 'Bank run' switched off") }, r.Ask("@event #1 off"));
        Assert.True(first.Disabled);
        Assert.Equal(new[] { Reply("events: 1 Bank run off, 2 1 off, 3 Sell junk on") }, r.Ask("@events"));
        Assert.Equal(new[] { Reply("event 2 '1' switched on") }, r.Ask("@event 2 on"));
    }

    // A number that is only part of a name is still a place: "3" is the third
    // event, not the one called "Farm 3".
    [Fact]
    public void Event_ANumberThatIsOnlyPartOfAName_IsThePlace()
    {
        using Rig r = Build();
        ScheduledEvent farm = Command("Farm 3");
        ScheduledEvent third = Command("Sell junk");
        r.Events.Add(farm);
        r.Events.Add(BankTrip("Bank run"));
        r.Events.Add(third);

        Assert.Equal(new[] { Reply("event 3 'Sell junk' switched off") }, r.Ask("@event 3 off"));
        Assert.True(third.Disabled);
        Assert.False(farm.Disabled);
        Assert.Equal(new[] { Reply("event 'Farm 3' switched off") }, r.Ask("@event farm 3 off"));
    }

    // Two events of one name can't be told apart by name. The refusal gives their
    // numbers, and a number switches the one meant.
    [Fact]
    public void Event_TwoEventsOfOneName_AreSwitchedByNumber()
    {
        using Rig r = Build();
        ScheduledEvent first = BankTrip("Bank run");
        ScheduledEvent second = Command("Bank run");
        r.Events.Add(Command("Night stats"));
        r.Events.Add(first);
        r.Events.Add(second);

        Assert.Equal(
            new[] { Reply("event name 'bank run' matches 2: 'Bank run' (numbers 2, 3); use a number in place of the name") },
            r.Ask("@event bank run off"));
        Assert.Equal(new[] { Reply("event 3 'Bank run' switched off") }, r.Ask("@event 3 off"));

        Assert.True(second.Disabled);
        Assert.False(first.Disabled);
        Assert.Equal(new[] { Reply("events: 1 Night stats on, 2 Bank run on, 3 Bank run off") }, r.Ask("@events"));
    }

    // Many events of one name: the first four numbers, and how many more.
    [Fact]
    public void Event_ManyEventsOfOneName_TheRefusalGivesFourNumbers_AndHowManyMore()
    {
        using Rig r = Build();
        for (int i = 0; i < 7; i++) r.Events.Add(Command("Bank run"));

        Assert.Equal(
            new[] { Reply("event name 'bank run' matches 7: 'Bank run' (numbers 1, 2, 3, 4 and 3 more); use a number in place of the name") },
            r.Ask("@event bank run"));
    }

    // ----- long names ------------------------------------------------------------

    private static readonly string LongName = "Scheduled " + new string('x', 180) + " tail";
    private static readonly string LongNameShown = "Scheduled " + new string('x', 27) + "...";

    private static void AssertFitsOneTelepath(IEnumerable<string> sent) =>
        Assert.All(sent, line => Assert.True(line.TrimEnd('\r').Length <= 225, $"{line.TrimEnd('\r').Length}: {line}"));

    // A name is shown cut short past forty characters, in every reply that
    // carries one, so none outgrows the one telepath it has to fit.
    [Fact]
    public void ALongName_IsShownCutShort_InEveryReply_AndEachFitsOneTelepath()
    {
        using Rig r = Build();
        Assert.Equal(40, LongNameShown.Length);
        ScheduledEvent e = new()
        {
            Name = LongName, TriggerType = EventTriggerType.Boss, BossName = "The Warden",
            ActionType = EventActionType.StashTransfer,
        };
        r.Events.Add(e);
        r.H.Profile.Current!.EventsGloballyDisabled = true;
        List<string> all = new();

        List<string> sent = r.Ask("@events");
        all.AddRange(sent);
        Assert.Equal(new[] { Reply($"events (all disabled): 1 {LongNameShown} on") }, sent);

        sent = r.Ask($"@event {LongName}");
        all.AddRange(sent);
        Assert.Equal(
            new[] { Reply($"event '{LongNameShown}' is on: fires on a boss timer, moves a stash to the bank; all events are disabled") },
            sent);

        all.AddRange(r.Ask("@event scheduled tail off"));           // found by its words, the cut ones too
        Assert.True(e.Disabled);
        sent = r.Ask($"@event {LongNameShown} on");                 // and by the name as it was shown
        all.AddRange(sent);
        Assert.Equal(
            new[] { Reply($"event '{LongNameShown}' switched on; all events are disabled, so it won't fire yet") },
            sent);

        sent = r.Ask($"@event {new string('q', 190)} off");         // the typed name comes back cut short too
        all.AddRange(sent);
        Assert.Equal(new[] { Reply($"no event named '{new string('q', 37)}...'") }, sent);

        Assert.Equal(5, all.Count);
        AssertFitsOneTelepath(all);
    }

    // Many events with long names, some switched off for a missing target: the
    // list's parts each fit one telepath, and so does the refusal that names them.
    [Fact]
    public void ManyLongNames_TheListsParts_AndTheAmbiguityRefusal_EachFitOneTelepath()
    {
        using Rig r = Build();
        for (int i = 0; i < 24; i++)
        {
            string name = $"Scheduled {new string((char)('a' + i), 150)} tail";
            r.Events.Add(i % 2 == 0 ? Command(name) : LoopNobodySaved(name));
        }
        r.H.Profile.Current!.EventsGloballyDisabled = true;

        List<string> list = r.Ask("@events");
        List<string> refusal = r.Ask($"@event scheduled {new string('z', 150)} tail off");
        List<string> ambiguous = r.Ask("@event scheduled tail off");

        Assert.True(list.Count > 3);
        Assert.StartsWith($"/{Friend} {{events (all disabled) (1/{list.Count}): 1 Scheduled aaa", list[0]);
        Assert.Equal(12, list.Sum(part => part.Split("off (target missing)").Length - 1));
        Assert.StartsWith($"/{Friend} {{event name 'scheduled tail' matches 24: 'Scheduled aaa", Assert.Single(ambiguous));
        Assert.EndsWith(" more}\r", ambiguous[0]);
        Assert.Single(refusal);
        AssertFitsOneTelepath(list.Concat(refusal).Concat(ambiguous));
    }

    // A name in characters the wire can't carry is counted as it goes out: the
    // engine spells a typographic ellipsis as three dots, so forty of them would
    // be a hundred and twenty characters if the cut were made first.
    [Fact]
    public void ANameInCharactersTheWireCantCarry_IsCutAsItGoesOut()
    {
        using Rig r = Build();
        r.Events.Add(Command(new string('…', 40)));

        Assert.Equal(new[] { Reply($"events: 1 {new string('.', 37)}... on") }, r.Ask("@events"));
    }

    // ----- a condition event switched on -------------------------------------------

    // A condition event fires when its condition turns true and waits for it to
    // stop holding before it can fire again. Switched off, it reads as not holding:
    // a reading taken meanwhile re-arms it, so switched on with the condition still
    // true it fires again at the next reading. Off and on with no reading between
    // leaves it as it was.
    [Fact]
    public void AConditionEvent_SwitchedOffAndOn_FiresAgain_OnceAReadingWasTakenWhileItWasOff()
    {
        using Rig r = Build();
        ScheduledEvent rich = new()
        {
            Name = "Rich", TriggerType = EventTriggerType.State, ActionType = EventActionType.Command, CommandText = "dep all",
            Conditions = new()
            {
                new EventCondition { Stat = EventConditionStat.Money, Comparison = EventComparison.AtLeast, Value = 1_000 },
            },
        };
        r.Events.Add(rich);
        EventStateWatcher watcher = new(
            r.Events, () => true, () => new EventConditionEvaluator.Readings(5_000, 0, 0, 1), r.H.Log);
        int Fired() => r.H.EventLog.Count(line => line.StartsWith("State event 'Rich' fired", StringComparison.Ordinal));

        watcher.Evaluate();
        Assert.Equal(1, Fired());
        watcher.Evaluate();
        Assert.Equal(1, Fired());                       // still holding: once per crossing

        Assert.Equal(
            new[] { Reply("event 'Rich' is on: fires on a condition, sends a command") }, r.Ask("@event rich"));
        Assert.Equal(new[] { Reply("event 'Rich' switched off") }, r.Ask("@event rich off"));
        Assert.Equal(new[] { Reply("event 'Rich' switched on") }, r.Ask("@event rich on"));
        watcher.Evaluate();
        Assert.Equal(1, Fired());                       // no reading while it was off

        r.Ask("@event rich off");
        watcher.Evaluate();                             // a reading while it is off
        Assert.Equal(1, Fired());
        Assert.Equal(new[] { Reply("event 'Rich' switched on") }, r.Ask("@event rich enable"));
        watcher.Evaluate();
        Assert.Equal(2, Fired());
    }

    // ----- the other readers of a braced reply leave these alone -----------------

    private static string Inner(string sent)
    {
        string opening = $"/{Friend} {{";
        Assert.StartsWith(opening, sent);
        Assert.EndsWith("}\r", sent);
        return sent[opening.Length..^2];
    }

    // An event's name is the user's and can say anything: a purse, a room, a
    // refusal. Every answer and refusal either command gives opens with fixed
    // words, so each is known as one of theirs whatever the name inside it.
    [Theory]
    [InlineData("Sell run (= 5 copper)")]
    [InlineData("Camp (map 1, room 5)")]
    [InlineData("Farm 2")]
    [InlineData("I can't come")]
    [InlineData("going idle")]
    [InlineData("no ailments check")]
    [InlineData("ailments: blind")]
    [InlineData("sending loop 'x'")]
    public void EveryReply_IsKnownAsOne_WhateverTheEventIsNamed(string name)
    {
        using Rig r = Build();
        List<string> wire = new();
        wire.AddRange(r.Ask("@events"));                          // none defined
        wire.AddRange(r.Ask("@events off"));                      // the list's usage
        wire.AddRange(r.Ask("@event"));                           // the one-event usage

        ScheduledEvent e = Command(name);
        r.Events.Add(e);
        wire.AddRange(r.Ask("@events"));
        wire.AddRange(r.Ask($"@event {name}"));                   // asked about
        wire.AddRange(r.Ask($"@event {name} off"));
        wire.AddRange(r.Ask($"@event {name} off"));               // already off
        wire.AddRange(r.Ask($"@event {name} on"));
        wire.AddRange(r.Ask($"@event {name} on"));                // already on
        wire.AddRange(r.Ask($"@event {name} nowhere"));           // nothing of that name
        wire.AddRange(r.Ask("@event 1"));                         // by number
        wire.AddRange(r.Ask("@event 1 off"));
        wire.AddRange(r.Ask("@event #1 on"));
        wire.AddRange(r.Ask("@event 7 off"));                     // no such number

        r.Events.Add(Command(name));
        wire.AddRange(r.Ask($"@event {name}"));                   // fits two
        r.Events.Remove(r.Events.Events[1]);

        ScheduledEvent lost = LoopNobodySaved(name + " loop");
        r.Events.Add(lost);
        wire.AddRange(r.Ask($"@event {name} loop on"));           // target missing
        wire.AddRange(r.Ask("@event 2 on"));                      // and by number
        wire.AddRange(r.Ask("@events"));
        r.Events.Remove(lost);

        ScheduledEvent unnamed = Command("");
        r.Events.Add(unnamed);
        wire.AddRange(r.Ask("@event 2"));                         // one with no name
        wire.AddRange(r.Ask("@event 2 off"));
        r.Events.Remove(unnamed);

        ScheduledEvent held = new()
        {
            Name = name + " hold", TriggerType = EventTriggerType.Every, EveryAmount = 1, EveryUnit = EventTimeUnit.Hours,
            ActionType = EventActionType.Wait, WaitSeconds = 600, Then = EventThenType.Nothing,
        };
        r.Events.Add(held);
        Assert.True(r.Events.Fire(held));
        wire.AddRange(r.Ask($"@event {name} hold off"));          // its run finishes

        r.H.Profile.Current!.EventsGloballyDisabled = true;
        wire.AddRange(r.Ask("@events"));                          // all disabled
        wire.AddRange(r.Ask($"@event {name}"));
        wire.AddRange(r.Ask($"@event {name} off"));
        wire.AddRange(r.Ask($"@event {name} on"));                // won't fire yet
        for (int i = 0; i < 30; i++) r.Events.Add(Command($"{name} {i:00}"));
        wire.AddRange(r.Ask("@events"));                          // numbered parts

        Assert.True(wire.Count >= 20, $"{wire.Count} replies");
        Assert.All(wire, sent => Assert.True(EventRemoteHandler.IsEventReplyLine(Inner(sent)), sent));
        Assert.All(wire, sent => Assert.True(UserNamedReply.IsLine(Inner(sent)), sent));
        Assert.All(wire, sent => Assert.True(UserNamedReply.IsMessage($"{{{Inner(sent)}}}"), sent));
    }

    [Theory]
    [InlineData("I can't come, forget me")]
    [InlineData("Standing; not moving; no ailments")]
    [InlineData("eventually coming to your location")]
    [InlineData("events are fun")]
    [InlineData("event horizon (map 1, room 5)")]
    [InlineData("Event 3 (map 1, room 5)")]
    [InlineData("event 3")]
    [InlineData("event 12 hours from now")]
    [InlineData("1,200 gold (= 12,000 copper)")]
    [InlineData("MudPlay 3.179.0")]
    public void OtherReplies_AreNotEventReplies(string inner)
    {
        Assert.False(EventRemoteHandler.IsEventReplyLine(inner));
        Assert.False(UserNamedReply.IsLine(inner));
    }

    // The one check the readers make covers the `@loop send` lines as well.
    [Theory]
    [InlineData("{sending loop 'King's Road' (3 rooms, 1 line)}")]
    [InlineData("{@loopresult 9da659db saved as 'King's Road 2'}")]
    [InlineData("  {events: Night stats on}  ")]
    [InlineData("events: Night stats on")]
    public void TheSharedCheck_TakesALoopSendLineAndAnEventReply_BracedOrNot(string message) =>
        Assert.True(UserNamedReply.IsMessage(message));
}
