using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using MudPlay.Game.Events;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Unit coverage for EventManager + its
// ScheduledEvent DTO: DTO round-trip, command-splitter contract, CRUD
// on the in-memory list, and the Command-action wire path (the only
// action dispatchable without a real movement-engine stack).
public sealed class EventManagerTests
{
    // ----- DTO round-trip ------------------------------------------

    [Fact]
    public void ScheduledEvent_RoundTripsThroughJson()
    {
        ScheduledEvent original = new()
        {
            Name = "stat refresh",
            TriggerType = EventTriggerType.Every,
            EveryAmount = 30,
            EveryUnit = EventTimeUnit.Seconds,
            ActionType = EventActionType.Command,
            CommandText = "stat",
            Disabled = true,
        };
        string json = JsonSerializer.Serialize(original, JsonStore.Options);
        ScheduledEvent? round = JsonSerializer.Deserialize<ScheduledEvent>(json, JsonStore.Options);

        Assert.NotNull(round);
        Assert.Equal("stat refresh", round!.Name);
        Assert.Equal(EventTriggerType.Every, round.TriggerType);
        Assert.Equal(30, round.EveryAmount);
        Assert.Equal(EventTimeUnit.Seconds, round.EveryUnit);
        Assert.Equal(EventActionType.Command, round.ActionType);
        Assert.Equal("stat", round.CommandText);
        Assert.True(round.Disabled);
        Assert.Null(round.AtTime);
        Assert.Null(round.LoopName);
    }

    [Theory]
    [InlineData("09:30", 9, 30)]
    [InlineData("21:30", 21, 30)]
    [InlineData("00:00", 0, 0)]
    public void TryParseAtTime_AcceptsHHmm(string input, int expectedHour, int expectedMinute)
    {
        ScheduledEvent e = new() { AtTime = input };
        System.TimeOnly? parsed = e.TryParseAtTime();
        Assert.NotNull(parsed);
        Assert.Equal(expectedHour, parsed!.Value.Hour);
        Assert.Equal(expectedMinute, parsed.Value.Minute);
    }

    [Theory]
    [InlineData("9:30 PM")]   // 12-hour format not accepted
    [InlineData("")]
    [InlineData("not a time")]
    public void TryParseAtTime_RejectsMalformed(string input)
    {
        Assert.Null(new ScheduledEvent { AtTime = input }.TryParseAtTime());
    }

    [Theory]
    [InlineData(30, EventTimeUnit.Seconds, 30)]
    [InlineData(5, EventTimeUnit.Minutes, 300)]
    [InlineData(2, EventTimeUnit.Hours, 7200)]
    public void TryParseEvery_BuildsCorrectTimeSpan(int amount, EventTimeUnit unit, int expectedSeconds)
    {
        ScheduledEvent e = new() { EveryAmount = amount, EveryUnit = unit };
        System.TimeSpan? span = e.TryParseEvery();
        Assert.NotNull(span);
        Assert.Equal(expectedSeconds, (int)span!.Value.TotalSeconds);
    }

    [Fact]
    public void TryParseEvery_NullOrZero_ReturnsNull()
    {
        Assert.Null(new ScheduledEvent().TryParseEvery());
        Assert.Null(new ScheduledEvent { EveryAmount = 0, EveryUnit = EventTimeUnit.Minutes }.TryParseEvery());
        Assert.Null(new ScheduledEvent { EveryAmount = 5 }.TryParseEvery());
    }

    // ----- Command splitter ----------------------------------------

    [Theory]
    [InlineData("look", new[] { "look" })]
    [InlineData("look;sit", new[] { "look", "sit" })]
    [InlineData("look^Msit", new[] { "look", "sit" })]
    [InlineData("look; sit; abil 145^M", new[] { "look", "sit", "abil 145" })]
    [InlineData("look;;sit", new[] { "look", "sit" })]
    [InlineData("look; ;o", new[] { "look", ";o" })]
    [InlineData("", new string[0])]
    [InlineData("  ", new string[0])]
    public void SplitCommand_HandlesSeparators(string input, string[] expected)
    {
        string[] actual = EventManager.SplitCommand(input).ToArray();
        Assert.Equal(expected, actual);
    }

    // ----- CRUD on the events list --------------------------------

    [Fact]
    public void Add_AppendsToObservableList()
    {
        EventManager mgr = EventEngineHarness.ManagerWithoutGameData();
        ScheduledEvent e = new() { Name = "a" };
        mgr.Add(e);
        Assert.Single(mgr.Events);
        Assert.Same(e, mgr.Events[0]);
    }

    [Fact]
    public void Remove_ByReference_TakesEventOut()
    {
        EventManager mgr = EventEngineHarness.ManagerWithoutGameData();
        ScheduledEvent a = new() { Name = "a" };
        ScheduledEvent b = new() { Name = "b" };
        mgr.Add(a);
        mgr.Add(b);
        Assert.True(mgr.Remove(a));
        Assert.Single(mgr.Events);
        Assert.Same(b, mgr.Events[0]);
    }

    [Fact]
    public void Remove_UnknownEvent_ReturnsFalse()
    {
        EventManager mgr = EventEngineHarness.ManagerWithoutGameData();
        Assert.False(mgr.Remove(new ScheduledEvent { Name = "ghost" }));
    }

    [Fact]
    public void Replace_ByReference_SwapsInPlace()
    {
        EventManager mgr = EventEngineHarness.ManagerWithoutGameData();
        ScheduledEvent a = new() { Name = "a" };
        ScheduledEvent b = new() { Name = "b" };
        ScheduledEvent updated = new() { Name = "a-updated" };
        mgr.Add(a);
        mgr.Add(b);
        Assert.True(mgr.Replace(a, updated));
        Assert.Equal(2, mgr.Events.Count);
        Assert.Same(updated, mgr.Events[0]);
        Assert.Same(b, mgr.Events[1]);
    }

    [Fact]
    public void Replace_UnknownOriginal_ReturnsFalse()
    {
        EventManager mgr = EventEngineHarness.ManagerWithoutGameData();
        Assert.False(mgr.Replace(new ScheduledEvent(), new ScheduledEvent()));
    }

    // ----- Command action dispatch (wire path) --------------------

    [Fact]
    public void Fire_CommandAction_SendsExpectedBytes()
    {
        EventManager mgr = EventEngineHarness.ManagerWithoutGameData();
        List<byte[]> sent = new();
        mgr.SetWireSender(sent.Add);

        ScheduledEvent e = new()
        {
            Name = "stat",
            ActionType = EventActionType.Command,
            CommandText = "stat",
        };
        mgr.Fire(e);

        Assert.Single(sent);
        Assert.Equal("stat\r", Encoding.Latin1.GetString(sent[0]));
    }

    [Fact]
    public void Fire_CommandAction_MultiFire_SendsEachChunk()
    {
        EventManager mgr = EventEngineHarness.ManagerWithoutGameData();
        List<byte[]> sent = new();
        mgr.SetWireSender(sent.Add);

        ScheduledEvent e = new()
        {
            ActionType = EventActionType.Command,
            CommandText = "look; sit; abil 145^M",
        };
        mgr.Fire(e);

        Assert.Equal(3, sent.Count);
        Assert.Equal("look\r",     Encoding.Latin1.GetString(sent[0]));
        Assert.Equal("sit\r",      Encoding.Latin1.GetString(sent[1]));
        Assert.Equal("abil 145\r", Encoding.Latin1.GetString(sent[2]));
    }

    // Events are the user's own automation: with the master switch off a firing
    // is skipped (user, 2026-10-09); on, it runs as before.
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void Fire_FollowsTheMasterSwitch(bool switchOff, int expectedSends)
    {
        EventManager mgr = EventEngineHarness.ManagerWithoutGameData();
        List<byte[]> sent = new();
        mgr.SetWireSender(sent.Add);
        List<string> skipped = new();
        mgr.BlockedByMasterSwitch = what => { if (switchOff) skipped.Add(what); return switchOff; };
        int fired = 0;
        mgr.Fired += _ => fired++;

        mgr.Fire(new ScheduledEvent { Name = "stat", ActionType = EventActionType.Command, CommandText = "stat" });

        Assert.Equal(expectedSends, sent.Count);
        Assert.Equal(expectedSends, fired);
        Assert.Equal(switchOff ? 1 : 0, skipped.Count);
    }

    [Fact]
    public void Fire_DisabledEvent_DoesNothing()
    {
        EventManager mgr = EventEngineHarness.ManagerWithoutGameData();
        List<byte[]> sent = new();
        mgr.SetWireSender(sent.Add);

        ScheduledEvent e = new()
        {
            ActionType = EventActionType.Command,
            CommandText = "stat",
            Disabled = true,
        };
        mgr.Fire(e);

        Assert.Empty(sent);
    }

    [Fact]
    public void Fire_EmptyCommandText_SendsBareCarriageReturn()
    {
        EventManager mgr = EventEngineHarness.ManagerWithoutGameData();
        List<byte[]> sent = new();
        mgr.SetWireSender(sent.Add);

        mgr.Fire(new ScheduledEvent { ActionType = EventActionType.Command, CommandText = "" });
        mgr.Fire(new ScheduledEvent { ActionType = EventActionType.Command, CommandText = null });

        Assert.Equal(2, sent.Count);
        Assert.Equal("\r", Encoding.Latin1.GetString(sent[0]));
        Assert.Equal("\r", Encoding.Latin1.GetString(sent[1]));
    }

    // ----- CharacterProfile integration ---------------------------

    [Fact]
    public void CharacterProfile_RoundTripsEvents()
    {
        CharacterProfile original = new()
        {
            Name = "Tester",
            Events = new List<ScheduledEvent>
            {
                new() { Name = "stat", TriggerType = EventTriggerType.Every,
                        EveryAmount = 30, EveryUnit = EventTimeUnit.Seconds,
                        ActionType = EventActionType.Command, CommandText = "stat" },
                new() { Name = "logon greet", TriggerType = EventTriggerType.Logon,
                        ActionType = EventActionType.Command, CommandText = "who" },
            },
        };
        string json = JsonSerializer.Serialize(original, JsonStore.Options);
        CharacterProfile? round = JsonSerializer.Deserialize<CharacterProfile>(json, JsonStore.Options);

        Assert.NotNull(round);
        Assert.NotNull(round!.Events);
        Assert.Equal(2, round.Events!.Count);
        Assert.Equal("stat",       round.Events[0].Name);
        Assert.Equal("logon greet", round.Events[1].Name);
        Assert.Equal(EventTriggerType.Logon, round.Events[1].TriggerType);
    }

    // Events saved before Then existed keep the behavior they had: a walk-to goes
    // back to what was running, anything else stops.
    [Fact]
    public void ConvertToThen_WritesTheOldBehaviorIn()
    {
        ScheduledEvent walk = new() { ActionType = EventActionType.WalkTo, WalkToTarget = new RoomRef(1, 2) };
        ScheduledEvent loop = new() { ActionType = EventActionType.Loop, LoopName = "farm" };
        ScheduledEvent current = new() { ActionType = EventActionType.Command, Then = EventThenType.Event, ThenEventName = "x" };

        Assert.True(EventManager.ConvertToThen(new[] { walk, loop, current }));
        Assert.Equal(EventThenType.Resume, walk.Then);
        Assert.Equal(EventThenType.Nothing, loop.Then);
        Assert.Equal(EventThenType.Event, current.Then);
        Assert.False(EventManager.ConvertToThen(new[] { walk, loop, current }));
    }
}
