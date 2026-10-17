using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game.Events;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.Settings;
using Xunit;

namespace MudPlay.Tests;

// Coverage for the bespoke Events Settings tab — the VM-level CRUD,
// the master-switch persistence + gate, the row badge from
// auto-disabled events, and the CollectionChanged refresh path.
// View-side bindings (XAML) smoke-tested via dotnet run.
public sealed class EventsSectionViewModelTests
{
    private static (EventManager events, ProfileService profile,
                    EventsSectionViewModel vm, List<byte[]> sent) Build()
    {
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        EventManager events = EventEngineHarness.ManagerWithoutGameData();
        EventsSectionViewModel vm = new(events, profile);
        List<byte[]> sent = new();
        events.SetWireSender(sent.Add);
        return (events, profile, vm, sent);
    }

    private static ScheduledEvent CommandEvent(EventTriggerType trigger, string cmd, string? name = null) =>
        new()
        {
            Name = name ?? $"{trigger}-{cmd}",
            TriggerType = trigger,
            ActionType = EventActionType.Command,
            CommandText = cmd,
        };

    // ----- Rows + selection ----------------------------------------

    [Fact]
    public void Rows_MirrorEventManagerEvents()
    {
        var (events, _, vm, _) = Build();
        events.Add(CommandEvent(EventTriggerType.Logon, "stat", "a"));
        events.Add(CommandEvent(EventTriggerType.Logoff, "save", "b"));

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("a", vm.Rows[0].Name);
        Assert.Equal("b", vm.Rows[1].Name);
    }

    [Fact]
    public void EventRow_FormatsTriggerLabels()
    {
        var (events, _, vm, _) = Build();
        events.Add(new ScheduledEvent
        {
            TriggerType = EventTriggerType.Every,
            EveryAmount = 30,
            EveryUnit = EventTimeUnit.Seconds,
            ActionType = EventActionType.Command,
            CommandText = "stat",
        });
        events.Add(new ScheduledEvent
        {
            TriggerType = EventTriggerType.AtTime,
            AtTime = "21:30",
            ActionType = EventActionType.Command,
            CommandText = "who",
        });
        events.Add(new ScheduledEvent
        {
            TriggerType = EventTriggerType.Every,
            EveryAmount = 1,
            EveryUnit = EventTimeUnit.Minutes,
            ActionType = EventActionType.Command,
            CommandText = "look",
        });

        Assert.Equal("Every 30 seconds", vm.Rows[0].TimeText);
        Assert.Equal("At 21:30",         vm.Rows[1].TimeText);
        Assert.Equal("Every 1 minute",   vm.Rows[2].TimeText);
    }

    [Fact]
    public void EventRow_FormatsActionLabels()
    {
        var (events, _, vm, _) = Build();
        events.Add(new ScheduledEvent
        {
            TriggerType = EventTriggerType.Logon,
            ActionType = EventActionType.Loop,
            LoopName = "Sewer farm",
        });
        events.Add(new ScheduledEvent
        {
            TriggerType = EventTriggerType.Logon,
            ActionType = EventActionType.AutoLair,
            AutoLairSetupName = "Albion lairs",
        });
        events.Add(new ScheduledEvent
        {
            TriggerType = EventTriggerType.Logon,
            ActionType = EventActionType.WalkTo,
            WalkToTarget = new Models.Profile.RoomRef(1, 297),
        });

        events.Add(new ScheduledEvent
        {
            TriggerType = EventTriggerType.Boss,
            BossName = "Sarnak",
            BossMoment = EventBossMoment.Guaranteed,
            BossLeadMinutes = 5,
            ActionType = EventActionType.Loop,
            LoopName = "Sewer farm",
            StopAfterLaps = 3,
            StopBossName = "Sarnak",
            StopBossMoment = EventBossMoment.Killed,
            Then = EventThenType.Resume,
        });

        // The action, its stop rule (loop / auto-lair), then what comes after — an
        // older event with no Then saved: a walk-to goes back, anything else stops.
        Assert.Equal("Loop \"Sewer farm\" (until stopped) → stop",       vm.Rows[0].EventText);
        Assert.Equal("Auto-lair \"Albion lairs\" (until stopped) → stop", vm.Rows[1].EventText);
        Assert.Equal("Walk to 1/297 → go back",                           vm.Rows[2].EventText);
        Assert.Equal("Loop \"Sewer farm\" (until 3 laps or Sarnak is killed) → go back", vm.Rows[3].EventText);
        Assert.Equal("Sarnak full timer hits 0 -5m", vm.Rows[3].TimeText);
    }

    // ----- CRUD commands -------------------------------------------

    // NewCommand / ModifyCommand both open EventEditDialog via the
    // DialogService — see EventEditDialogViewModelTests for the
    // dialog-side coverage. The end-to-end open-edit-save flow is
    // smoke-tested manually because DialogService needs the Avalonia
    // window stack running.

    [Fact]
    public void RemoveCommand_RequiresSelection()
    {
        var (_, _, vm, _) = Build();
        Assert.False(vm.RemoveCommand.CanExecute(null));
    }

    [Fact]
    public void RemoveCommand_RemovesSelected()
    {
        var (events, _, vm, _) = Build();
        events.Add(CommandEvent(EventTriggerType.Logon, "stat", "a"));
        events.Add(CommandEvent(EventTriggerType.Logoff, "save", "b"));

        vm.SelectedRow = vm.Rows[0];
        Assert.True(vm.RemoveCommand.CanExecute(null));
        vm.RemoveCommand.Execute(null);

        Assert.Single(events.Events);
        Assert.Equal("b", events.Events[0].Name);
        Assert.Null(vm.SelectedRow);
    }

    // ----- Master switch -------------------------------------------

    [Fact]
    public void IsGloballyDisabled_PersistsToProfile()
    {
        var (_, profile, vm, _) = Build();

        Assert.False(vm.IsGloballyDisabled);
        vm.IsGloballyDisabled = true;
        Assert.True(profile.Current!.EventsGloballyDisabled);
        // A fresh VM bound to the same profile reads the persisted state back.
        Assert.True(new EventsSectionViewModel(EventEngineHarness.ManagerWithoutGameData(), profile).IsGloballyDisabled);

        vm.IsGloballyDisabled = false;
        Assert.False(profile.Current.EventsGloballyDisabled);
    }

    // ----- Queue limits ----------------------------------------------

    // The defaults aren't written to the profile (files hold deltas only); a
    // change is, kept inside its range, and going back to the default clears it.
    [Fact]
    public void QueueLimits_DefaultTo10And30_AndPersistToTheCharacter()
    {
        var (_, profile, vm, _) = Build();

        Assert.Equal(10, vm.QueueLimit);
        Assert.Equal(30, vm.QueueWaitMinutes);
        Assert.Equal(5, vm.SuspendedIdleMinutes);
        Assert.Null(profile.Current!.EventQueueLimit);
        Assert.Null(profile.Current.EventQueueWaitMinutes);
        Assert.Null(profile.Current.EventSuspendedIdleMinutes);

        vm.QueueLimit = 4;
        vm.QueueWaitMinutes = 120;
        vm.SuspendedIdleMinutes = 15;
        Assert.Equal(4, profile.Current.EventQueueLimit);
        Assert.Equal(120, profile.Current.EventQueueWaitMinutes);
        Assert.Equal(15, profile.Current.EventSuspendedIdleMinutes);

        vm.QueueLimit = 0;
        vm.QueueWaitMinutes = 100_000;
        vm.SuspendedIdleMinutes = 1_000;
        Assert.Equal(1, vm.QueueLimit);
        Assert.Equal(EventManager.MaxQueueWaitMinutesCeiling, vm.QueueWaitMinutes);
        Assert.Equal(EventManager.SuspendedIdleMinutesCeiling, vm.SuspendedIdleMinutes);

        vm.QueueLimit = 10;
        vm.QueueWaitMinutes = 30;
        vm.SuspendedIdleMinutes = 5;
        Assert.Null(profile.Current.EventQueueLimit);
        Assert.Null(profile.Current.EventQueueWaitMinutes);
        Assert.Null(profile.Current.EventSuspendedIdleMinutes);
    }
}
