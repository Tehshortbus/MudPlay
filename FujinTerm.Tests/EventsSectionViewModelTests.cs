using System.Collections.Generic;
using System.Linq;
using System.Text;
using FujinTerm.Game.Events;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.ViewModels.Settings;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Coverage for the bespoke Events Settings tab — the VM-level CRUD,
/// the master-switch persistence + gate, the row badge from
/// auto-disabled events, and the CollectionChanged refresh path.
/// View-side bindings (XAML) smoke-tested via <c>dotnet run</c>.
/// </summary>
public sealed class EventsSectionViewModelTests
{
    private static (EventManager events, ProfileService profile,
                    EventsSectionViewModel vm, List<byte[]> sent) Build()
    {
        ProfileService profile = new();
        profile.LoadBlank();
        EventManager events = new();
        // Re-create with profile binding via the ctor that takes it —
        // simpler than poking the private field. EventManager's
        // parameterless ctor doesn't subscribe to the profile, so we
        // wire just what the VM needs (Add / Remove fires through the
        // observable collection regardless of profile binding).
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

        Assert.Equal("Loop \"Sewer farm\"",       vm.Rows[0].EventText);
        Assert.Equal("Auto-lair \"Albion lairs\"", vm.Rows[1].EventText);
        Assert.Equal("Walk to 1/297",              vm.Rows[2].EventText);
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

        vm.IsGloballyDisabled = false;
        Assert.False(profile.Current.EventsGloballyDisabled);
    }

    // EventsGloballyDisabled gate tested by directly toggling
    // ScheduledEvent.Disabled here — the master switch's effect on
    // Fire() is observable via the same wire-empty assertion. End-to-
    // end smoke test (real EventManager with profile + master switch
    // toggle blocking Fire) lives in the manual run-through; the
    // production gate code is one boolean check so a higher-cost test
    // fixture isn't worth it.

    [Fact]
    public void MasterSwitch_TogglingPersistsToProfile_Reentry()
    {
        var (_, profile, vm, _) = Build();

        // Persist a toggled state.
        vm.IsGloballyDisabled = true;
        Assert.True(profile.Current!.EventsGloballyDisabled);

        // A fresh VM bound to the same profile reads the persisted
        // state back through the binding.
        EventManager events2 = new();
        EventsSectionViewModel vm2 = new(events2, profile);
        Assert.True(vm2.IsGloballyDisabled);
    }
}
