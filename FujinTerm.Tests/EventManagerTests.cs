using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using FujinTerm.Game.Events;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Unit coverage for <see cref="EventManager"/> + its
/// <see cref="ScheduledEvent"/> DTO. Focused on the surfaces PR 8.1
/// ships end-to-end: DTO round-trip, command-splitter contract, CRUD
/// on the in-memory list, and the Command-action wire path (the only
/// action dispatchable without a real movement-engine stack).
/// Loop / AutoLair / reconciliation tests land in PR 8.2 alongside the
/// trigger-source wiring + a richer test fixture.
/// </summary>
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
        string json = JsonSerializer.Serialize(original);
        ScheduledEvent? round = JsonSerializer.Deserialize<ScheduledEvent>(json);

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
        EventManager mgr = new();
        ScheduledEvent e = new() { Name = "a" };
        mgr.Add(e);
        Assert.Single(mgr.Events);
        Assert.Same(e, mgr.Events[0]);
    }

    [Fact]
    public void Remove_ByReference_TakesEventOut()
    {
        EventManager mgr = new();
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
        EventManager mgr = new();
        Assert.False(mgr.Remove(new ScheduledEvent { Name = "ghost" }));
    }

    [Fact]
    public void Replace_ByReference_SwapsInPlace()
    {
        EventManager mgr = new();
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
        EventManager mgr = new();
        Assert.False(mgr.Replace(new ScheduledEvent(), new ScheduledEvent()));
    }

    // ----- Command action dispatch (wire path) --------------------

    [Fact]
    public void Fire_CommandAction_SendsExpectedBytes()
    {
        EventManager mgr = new();
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
        EventManager mgr = new();
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

    [Fact]
    public void Fire_DisabledEvent_DoesNothing()
    {
        EventManager mgr = new();
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
        EventManager mgr = new();
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
        string json = JsonSerializer.Serialize(original);
        CharacterProfile? round = JsonSerializer.Deserialize<CharacterProfile>(json);

        Assert.NotNull(round);
        Assert.NotNull(round!.Events);
        Assert.Equal(2, round.Events!.Count);
        Assert.Equal("stat",       round.Events[0].Name);
        Assert.Equal("logon greet", round.Events[1].Name);
        Assert.Equal(EventTriggerType.Logon, round.Events[1].TriggerType);
    }
}
