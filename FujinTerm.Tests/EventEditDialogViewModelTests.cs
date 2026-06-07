using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.ViewModels.Settings;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Coverage for <see cref="EventEditDialogViewModel"/> — the field
/// hydration from an existing event, the WHEN / WHAT validation that
/// gates Save, the materialised result on commit, and the
/// CloseRequested contract for the modeless dialog protocol.
/// </summary>
public sealed class EventEditDialogViewModelTests
{
    // ----- Hydration --------------------------------------------------

    [Fact]
    public void Constructor_PopulatesFieldsFromExistingEvent()
    {
        ScheduledEvent existing = new()
        {
            Name = "stat refresh",
            TriggerType = EventTriggerType.Every,
            EveryAmount = 30,
            EveryUnit = EventTimeUnit.Seconds,
            ActionType = EventActionType.Command,
            CommandText = "stat",
            Disabled = true,
        };

        EventEditDialogViewModel vm = new(existing, isNew: false);

        Assert.Equal("stat refresh", vm.Name);
        Assert.True(vm.IsTriggerEvery);
        Assert.Equal(30, vm.EveryAmount);
        Assert.Equal(EventTimeUnit.Seconds, vm.EveryUnit);
        Assert.True(vm.IsActionCommand);
        Assert.Equal("stat", vm.CommandText);
        Assert.True(vm.DisabledFlag);
    }

    [Fact]
    public void Constructor_DefaultsWhenFieldsAreMissing()
    {
        EventEditDialogViewModel vm = new(new ScheduledEvent(), isNew: true);

        Assert.True(vm.IsTriggerLogon);
        Assert.True(vm.IsActionWalkTo);
        Assert.Equal("12:00", vm.AtTime);
        Assert.Equal(30, vm.EveryAmount);
        Assert.Equal(EventTimeUnit.Seconds, vm.EveryUnit);
        Assert.Equal("New Event", vm.DialogTitle);
    }

    [Fact]
    public void Constructor_WalkToCoordHydratesAsText()
    {
        ScheduledEvent existing = new()
        {
            TriggerType = EventTriggerType.Logon,
            ActionType = EventActionType.WalkTo,
            WalkToTarget = new RoomRef(1, 297),
        };
        EventEditDialogViewModel vm = new(existing, isNew: false);
        Assert.Equal("1/297", vm.WalkToText);
    }

    // ----- Validation gates Save --------------------------------------

    [Fact]
    public void CanSave_AtTime_RequiresHHmmFormat()
    {
        EventEditDialogViewModel vm = new(new ScheduledEvent(), isNew: true)
        {
            IsTriggerAtTime = true,
            AtTime = "9:30 PM",  // 12-hour not supported.
            IsActionCommand = true,
            CommandText = "stat",
        };
        Assert.False(vm.CanSave);

        vm.AtTime = "21:30";
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void CanSave_Every_RequiresPositiveAmount()
    {
        EventEditDialogViewModel vm = new(new ScheduledEvent(), isNew: true)
        {
            IsTriggerEvery = true,
            EveryAmount = 0,
            IsActionCommand = true,
            CommandText = "stat",
        };
        Assert.False(vm.CanSave);

        vm.EveryAmount = 5;
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void CanSave_Command_AllowsEmptyText_TreatedAsBareCR()
    {
        // Empty CommandText is intentional now — Fire sends a bare
        // CR (useful for MOTD pagination + single-Enter prompts).
        EventEditDialogViewModel vm = new(new ScheduledEvent(), isNew: true)
        {
            IsTriggerLogon = true,
            IsActionCommand = true,
            CommandText = "",
        };
        Assert.True(vm.CanSave);
    }

    // WHAT-side target validation lives in
    // TryGetMissingTargetMessage and surfaces via a Save-time popup
    // (DialogService.ShowInfo). CanSave only gates on the WHEN-side
    // format errors now. Coverage for the popup path:

    [Fact]
    public void TryGetMissingTargetMessage_WalkTo_EmptyText_ReturnsMessage()
    {
        EventEditDialogViewModel vm = new(new ScheduledEvent(), isNew: true)
        {
            IsTriggerLogon = true,
            IsActionWalkTo = true,
            WalkToText = "",
        };
        Assert.True(vm.CanSave);          // form-level OK
        Assert.NotNull(vm.TryGetMissingTargetMessage());
    }

    [Fact]
    public void TryGetMissingTargetMessage_WalkTo_ValidCoord_ReturnsNull()
    {
        EventEditDialogViewModel vm = new(new ScheduledEvent(), isNew: true)
        {
            IsTriggerLogon = true,
            IsActionWalkTo = true,
            WalkToText = "1/297",
        };
        Assert.Null(vm.TryGetMissingTargetMessage());
    }

    [Fact]
    public void TryGetMissingTargetMessage_Loop_NoSelection_ReturnsMessage()
    {
        EventEditDialogViewModel vm = new(new ScheduledEvent(), isNew: true)
        {
            IsTriggerLogon = true,
            IsActionLoop = true,
            LoopName = null,
        };
        Assert.True(vm.CanSave);
        Assert.NotNull(vm.TryGetMissingTargetMessage());
    }

    [Fact]
    public void TryGetMissingTargetMessage_AutoLair_NoSelection_ReturnsMessage()
    {
        EventEditDialogViewModel vm = new(new ScheduledEvent(), isNew: true)
        {
            IsTriggerLogon = true,
            IsActionAutoLair = true,
            AutoLairSetupName = null,
        };
        Assert.True(vm.CanSave);
        Assert.NotNull(vm.TryGetMissingTargetMessage());
    }

    [Fact]
    public void Save_WithMissingTarget_DoesNotFireCloseRequested()
    {
        EventEditDialogViewModel vm = new(new ScheduledEvent(), isNew: true)
        {
            IsTriggerLogon = true,
            IsActionLoop = true,
            LoopName = null,
        };
        bool fired = false;
        vm.CloseRequested += _ => fired = true;

        vm.SaveCommand.Execute(null);
        // The Save shorthand routes through TryGetMissingTargetMessage
        // and the DialogService popup (no-op without a main window in
        // tests) — but does NOT invoke CloseRequested.
        Assert.False(fired);
    }

    // ----- Save / Cancel emit CloseRequested --------------------------

    [Fact]
    public void Save_EmitsCloseRequestedWithMaterialisedEvent()
    {
        EventEditDialogViewModel vm = new(new ScheduledEvent(), isNew: true)
        {
            Name = "tick",
            IsTriggerEvery = true,
            EveryAmount = 30,
            EveryUnit = EventTimeUnit.Seconds,
            IsActionCommand = true,
            CommandText = "stat",
        };
        ScheduledEvent? captured = null;
        bool fired = false;
        vm.CloseRequested += e => { captured = e; fired = true; };

        vm.SaveCommand.Execute(null);

        Assert.True(fired);
        Assert.NotNull(captured);
        Assert.Equal("tick", captured!.Name);
        Assert.Equal(EventTriggerType.Every, captured.TriggerType);
        Assert.Equal(30, captured.EveryAmount);
        Assert.Equal(EventTimeUnit.Seconds, captured.EveryUnit);
        Assert.Equal(EventActionType.Command, captured.ActionType);
        Assert.Equal("stat", captured.CommandText);
        // Trigger-side fields that weren't selected stay null so the
        // JSON serialisation doesn't carry stale params from a flip.
        Assert.Null(captured.AtTime);
    }

    [Fact]
    public void Save_WalkToCoord_PopulatesRoomRef()
    {
        EventEditDialogViewModel vm = new(new ScheduledEvent(), isNew: true)
        {
            IsTriggerLogon = true,
            IsActionWalkTo = true,
            WalkToText = "1/297",
        };
        ScheduledEvent? captured = null;
        vm.CloseRequested += e => captured = e;
        vm.SaveCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.Equal(EventActionType.WalkTo, captured!.ActionType);
        Assert.NotNull(captured.WalkToTarget);
        Assert.Equal(1, captured.WalkToTarget!.Map);
        Assert.Equal(297, captured.WalkToTarget.Room);
    }

    [Fact]
    public void Cancel_EmitsCloseRequestedWithNull()
    {
        EventEditDialogViewModel vm = new(new ScheduledEvent(), isNew: true);
        bool fired = false;
        ScheduledEvent? captured = new ScheduledEvent();
        vm.CloseRequested += e => { captured = e; fired = true; };

        vm.CancelCommand.Execute(null);

        Assert.True(fired);
        Assert.Null(captured);
    }

    [Fact]
    public void Save_WhenInvalid_DoesNotEmit()
    {
        EventEditDialogViewModel vm = new(new ScheduledEvent(), isNew: true)
        {
            IsTriggerAtTime = true,
            AtTime = "garbage",
            IsActionCommand = true,
            CommandText = "stat",
        };
        bool fired = false;
        vm.CloseRequested += _ => fired = true;

        vm.SaveCommand.Execute(null);

        Assert.False(fired);
    }

    [Fact]
    public void Save_FlipsFromAtTimeToLogon_ClearsAtTimeOnResult()
    {
        ScheduledEvent existing = new()
        {
            TriggerType = EventTriggerType.AtTime,
            AtTime = "21:30",
            ActionType = EventActionType.Command,
            CommandText = "stat",
        };
        EventEditDialogViewModel vm = new(existing, isNew: false)
        {
            IsTriggerLogon = true,  // user flipped to Logon.
        };
        ScheduledEvent? captured = null;
        vm.CloseRequested += e => captured = e;
        vm.SaveCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.Equal(EventTriggerType.Logon, captured!.TriggerType);
        // AtTime field cleared on result so the persisted DTO doesn't
        // carry the stale "21:30" string from the prior trigger shape.
        Assert.Null(captured.AtTime);
    }
}
