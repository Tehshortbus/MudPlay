using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game.Events;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.ViewModels.Settings;
using Xunit;

namespace MudPlay.Tests;

// The "When" event trigger (money / encumbrance / experience / level conditions)
// and the Roomba event action.
public sealed class EventStateTriggerTests
{
    private static EventConditionEvaluator.Readings Now(
        long? copper = 0, int? enc = 0, long? exp = 0, int? level = 1) => new(copper, enc, exp, level);

    private static EventCondition Cond(
        EventConditionStat stat, EventComparison cmp, long value,
        CoinDenomination denom = CoinDenomination.Copper) =>
        new() { Stat = stat, Comparison = cmp, Value = value, Denomination = denom };

    // ----- evaluator ---------------------------------------------------

    [Theory]
    [InlineData(EventComparison.AtLeast, 10, 10, true)]
    [InlineData(EventComparison.AtLeast, 9, 10, false)]
    [InlineData(EventComparison.AtMost, 10, 10, true)]
    [InlineData(EventComparison.Above, 10, 10, false)]
    [InlineData(EventComparison.Above, 11, 10, true)]
    [InlineData(EventComparison.Below, 9, 10, true)]
    [InlineData(EventComparison.Equal, 10, 10, true)]
    [InlineData(EventComparison.NotEqual, 10, 10, false)]
    public void Comparisons(EventComparison cmp, int level, int value, bool expected)
    {
        Assert.Equal(expected,
            EventConditionEvaluator.Holds(Cond(EventConditionStat.Level, cmp, value), Now(level: level)));
    }

    [Fact]
    public void Money_IsCountedInTheConditionsDenomination()
    {
        // 5 platinum = 50,000 copper.
        EventCondition fivePlat = Cond(EventConditionStat.Money, EventComparison.AtLeast, 5, CoinDenomination.Platinum);

        Assert.False(EventConditionEvaluator.Holds(fivePlat, Now(copper: 49_999)));
        Assert.True(EventConditionEvaluator.Holds(fivePlat, Now(copper: 50_000)));
    }

    [Fact]
    public void AnUnknownReading_NeverHolds()
    {
        // The inventory hasn't been read yet: "money below 1,000" must not fire on it.
        EventCondition broke = Cond(EventConditionStat.Money, EventComparison.Below, 1_000);
        Assert.False(EventConditionEvaluator.Holds(broke, Now(copper: null)));
    }

    [Fact]
    public void AllConditionsMustHold_AndNoneNeverFires()
    {
        List<EventCondition> both = new()
        {
            Cond(EventConditionStat.Money, EventComparison.AtLeast, 5, CoinDenomination.Platinum),
            Cond(EventConditionStat.Encumbrance, EventComparison.AtLeast, 60),
        };

        Assert.False(EventConditionEvaluator.AllHold(both, Now(copper: 60_000, enc: 40)));
        Assert.True(EventConditionEvaluator.AllHold(both, Now(copper: 60_000, enc: 61)));
        Assert.False(EventConditionEvaluator.AllHold(new List<EventCondition>(), Now()));
    }

    [Fact]
    public void Describe_ReadsLikeTheEditor()
    {
        List<EventCondition> both = new()
        {
            Cond(EventConditionStat.Money, EventComparison.AtLeast, 5, CoinDenomination.Platinum),
            Cond(EventConditionStat.Encumbrance, EventComparison.AtLeast, 60),
        };
        Assert.Equal("money ≥ 5 platinum and encumbrance ≥ 60%", EventConditionEvaluator.Describe(both));
    }

    // ----- watcher: once per crossing -----------------------------------

    private sealed class Rig
    {
        public readonly EventManager Manager = EventEngineHarness.ManagerWithoutGameData();
        public readonly List<string> Sent = new();
        public bool InGame = true;
        public EventConditionEvaluator.Readings Readings = Now();
        public readonly EventStateWatcher Watcher;
        public readonly ScheduledEvent Event;

        public Rig()
        {
            Manager.SetWireSender(b => Sent.Add(Encoding.Latin1.GetString(b)));
            Event = new ScheduledEvent
            {
                Name = "rich",
                TriggerType = EventTriggerType.State,
                Conditions = new() { Cond(EventConditionStat.Money, EventComparison.AtLeast, 1_000) },
                ActionType = EventActionType.Command,
                CommandText = "dep all",
            };
            Manager.Events.Add(Event);
            Watcher = new EventStateWatcher(Manager, () => InGame, () => Readings);
        }

        public void Set(long copper) { Readings = Now(copper: copper); Watcher.Evaluate(); }
    }

    [Fact]
    public void FiresOncePerCrossing()
    {
        Rig r = new();
        r.Set(500);
        Assert.Empty(r.Sent);

        r.Set(1_200);                       // crosses
        r.Set(1_500);                       // still true — no repeat
        Assert.Single(r.Sent);

        r.Set(300);                         // drops below: re-arms
        r.Set(2_000);                       // crosses again
        Assert.Equal(2, r.Sent.Count);
    }

    [Fact]
    public void AlreadyTrueAtLogin_FiresOnce()
    {
        Rig r = new();
        r.Set(5_000);
        r.Set(5_100);
        Assert.Single(r.Sent);
    }

    [Fact]
    public void OutOfGame_NothingIsEvaluated_AndTheCrossingIsCaughtOnReturn()
    {
        Rig r = new() { InGame = false };
        r.Set(5_000);
        Assert.Empty(r.Sent);

        r.InGame = true;
        r.Watcher.Evaluate();
        Assert.Single(r.Sent);
    }

    [Fact]
    public void ADisabledEvent_DoesNotFire()
    {
        Rig r = new();
        r.Event.Disabled = true;
        r.Set(5_000);
        Assert.Empty(r.Sent);
    }

    // ----- Roomba action -------------------------------------------------

    [Fact]
    public void RoombaAction_StartsTheChosenSweep()
    {
        EventManager mgr = EventEngineHarness.ManagerWithoutGameData();
        List<EventRoombaMode> started = new();
        mgr.SetRoombaStarter(mode => { started.Add(mode); return null; });

        mgr.Fire(new ScheduledEvent { ActionType = EventActionType.Roomba, RoombaMode = EventRoombaMode.InventoryOnly });
        mgr.NoteRoombaFinished(finished: true);
        mgr.Fire(new ScheduledEvent { ActionType = EventActionType.Roomba });

        Assert.Equal(new[] { EventRoombaMode.InventoryOnly, EventRoombaMode.Sort }, started);
    }

    // ----- editor ------------------------------------------------------

    [Fact]
    public void Editor_WhenWithNoConditions_CannotSave()
    {
        EventEditDialogViewModel vm = new(new ScheduledEvent(), isNew: true) { IsTriggerState = true };
        Assert.False(vm.CanSave);

        vm.AddConditionCommand.Execute(null);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void Editor_SavesConditionsAndRoombaMode()
    {
        EventEditDialogViewModel vm = new(new ScheduledEvent(), isNew: true);
        ScheduledEvent? saved = null;
        vm.CloseRequested += e => saved = e;

        vm.AddConditionCommand.Execute(null);
        EventConditionRowViewModel row = vm.Conditions.Single();
        row.SelectedStat = "Encumbrance %";
        row.SelectedComparison = "≥";
        row.Value = 60;
        vm.IsActionRoomba = true;
        vm.SelectedRoombaMode = "Inventory only";
        vm.SaveCommand.Execute(null);

        Assert.NotNull(saved);
        Assert.Equal(EventTriggerType.State, saved!.TriggerType);
        EventCondition c = Assert.Single(saved.Conditions!);
        Assert.Equal(EventConditionStat.Encumbrance, c.Stat);
        Assert.Equal(EventComparison.AtLeast, c.Comparison);
        Assert.Equal(60, c.Value);
        Assert.Equal(EventActionType.Roomba, saved.ActionType);
        Assert.Equal(EventRoombaMode.InventoryOnly, saved.RoombaMode);
    }
}
