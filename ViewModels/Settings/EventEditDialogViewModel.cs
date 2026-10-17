using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Game.Sounds;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;

namespace MudPlay.ViewModels.Settings;

// Modeless editor for one ScheduledEvent, in the order an event runs: WHEN
// (trigger), DO (action — with a STOP AFTER rule for a loop / auto-lair, which
// never ends by itself), then THEN (what happens once the action is done). Each
// is its own radio group, so the user isn't picking from a single mixed list the
// way MegaMUD's dialog forces.
//
// Edit semantics: the VM works on its own field set; the original ScheduledEvent
// isn't touched until Save. Save returns the materialised event (either
// fresh-constructed for a new event, or with the original's mutations applied for
// a modify). Cancel returns null. The caller decides whether the result goes into
// EventManager.Add (new) or EventManager.Replace (modify).
//
// Walk-to / Loop / Auto-lair pickers bind to the live LoopManager / LairManager
// collections + RoomSearchService so the user sees the same names + room
// references the rest of the app uses; the boss pickers list the Bosses tab's
// bosses for the active realm, and the Then-event picker the character's other
// events.
public sealed partial class EventEditDialogViewModel : ObservableObject, IDialogViewModel<ScheduledEvent?>
{
    public event Action<ScheduledEvent?>? CloseRequested;

    private readonly bool _isNew;
    private readonly LoopManager? _loops;
    private readonly LairManager? _lairs;
    private readonly RoomSearchService? _search;
    private readonly Func<RoomKey, string?>? _stopBeforeBoss;

    // Set once the user picks a Then on a new event; until then the default Then
    // follows the action (a command does nothing after, anything else goes back
    // to what was running).
    private bool _thenChosen;
    private bool _settingThenDefault;

    public EventEditDialogViewModel(
        ScheduledEvent existing,
        bool isNew,
        LoopManager? loops = null,
        LairManager? lairs = null,
        RoomSearchService? search = null,
        IReadOnlyList<string>? bossNames = null,
        IReadOnlyList<string>? eventNames = null,
        IReadOnlyList<(string Label, double Fraction)>? bossWindows = null,
        // The character's stash rooms and the active set's banks, labelled, for the
        // stash-transfer action.
        IReadOnlyList<(string Label, RoomRef Room)>? stashRooms = null,
        IReadOnlyList<(string Label, RoomRef Room)>? banks = null,
        // The boss whose room this is, when that boss is marked "stop before
        // entering" on the Bosses tab; null for any other room.
        Func<RoomKey, string?>? stopBeforeBoss = null)
    {
        _stopBeforeBoss = stopBeforeBoss;
        ArgumentNullException.ThrowIfNull(existing);
        _isNew = isNew;
        _loops = loops;
        _lairs = lairs;
        _search = search;
        RoomSuggestions = (text, _) => Task.FromResult<IEnumerable<object>>(SuggestRooms(text));

        // Dropdown contents — snapshot on open. Edits to the underlying
        // managers while the dialog is open don't ripple through; user closes
        // + re-opens to see new names. Matches every other dropdown in the app.
        if (loops is not null)
            foreach (Loop l in loops.Loops) AvailableLoopNames.Add(l.Name);
        if (lairs is not null)
            foreach (LairSetup s in lairs.Setups) AvailableAutoLairNames.Add(s.Name);
        foreach (string b in bossNames ?? Array.Empty<string>()) AvailableBossNames.Add(b);
        foreach (string n in eventNames ?? Array.Empty<string>())
            if (!string.Equals(n, existing.Name, StringComparison.OrdinalIgnoreCase)) AvailableEventNames.Add(n);

        Name = existing.Name;
        DisabledFlag = existing.Disabled;

        TransferStash = FillRoomPicker(_stashRooms, AvailableStashRooms, stashRooms, existing.TransferStash, "not a stash room now");
        TransferBank = FillRoomPicker(_banks, AvailableBanks, banks, existing.TransferBank, "not a bank in this game data");

        SoundOptions = new[] { NoSoundLabel }
            .Concat(SoundTones.All.Select(static t => t.Label)).Append(SoundFileLabel).ToArray();
        string? tone = SoundTones.All.FirstOrDefault(t => t.Id == existing.Sound).Label;
        if (string.IsNullOrWhiteSpace(existing.Sound)) SelectedSound = NoSoundLabel;
        else if (tone is not null) SelectedSound = tone;
        else
        {
            SelectedSound = SoundFileLabel;
            SoundFile = existing.Sound;
        }

        switch (existing.TriggerType)
        {
            case EventTriggerType.Logoff: IsTriggerLogoff = true; break;
            case EventTriggerType.Relog:  IsTriggerRelog  = true; break;
            case EventTriggerType.AtTime: IsTriggerAtTime = true; break;
            case EventTriggerType.Every:  IsTriggerEvery  = true; break;
            case EventTriggerType.State:  IsTriggerState  = true; break;
            case EventTriggerType.Boss:   IsTriggerBoss   = true; break;
            default:                      IsTriggerLogon  = true; break;
        }
        foreach (EventCondition c in existing.Conditions ?? new List<EventCondition>())
            Conditions.Add(new EventConditionRowViewModel(c, RemoveCondition));
        Conditions.CollectionChanged += (_, _) => Refresh();
        AtTime = existing.AtTime ?? "12:00";
        EveryAmount = existing.EveryAmount ?? 30;
        EveryUnit = existing.EveryUnit ?? EventTimeUnit.Seconds;
        BossName = existing.BossName;
        // One option per Bosses-tab column (its early windows, then the full timer),
        // then the kill and a cleanup reset.
        foreach ((string label, double fraction) in bossWindows ?? new[] { ("87.5%", 0.875) })
            _bossMoments.Add(($"{label} column hits 0", EventBossMoment.EarlyWindow, fraction));
        _bossMoments.Add(("Guaranteed (full) hits 0", EventBossMoment.Guaranteed, null));
        _bossMoments.Add(("Is killed", EventBossMoment.Killed, null));
        _bossMoments.Add(("Cleanup reset", EventBossMoment.CleanupReset, null));
        BossMomentOptions = _bossMoments.Select(m => m.Label).ToList();
        SelectedBossMoment = MomentLabel(existing.BossMoment ?? EventBossMoment.EarlyWindow, existing.BossWindowFraction);
        BossLeadMinutes = existing.BossLeadMinutes ?? 0;

        switch (existing.ActionType)
        {
            case EventActionType.Loop:     IsActionLoop     = true; break;
            case EventActionType.AutoLair: IsActionAutoLair = true; break;
            case EventActionType.Command:  IsActionCommand  = true; break;
            case EventActionType.Roomba:   IsActionRoomba   = true; break;
            case EventActionType.Wait:     IsActionWait     = true; break;
            case EventActionType.RestUp:   IsActionRestUp   = true; break;
            case EventActionType.BankTrip: IsActionBankTrip = true; break;
            case EventActionType.StashTransfer: IsActionStashTransfer = true; break;
            default:                       IsActionWalkTo   = true; break;
        }
        SelectedRoombaMode = existing.RoombaMode == EventRoombaMode.InventoryOnly
            ? RoombaModeOptions[1]
            : RoombaModeOptions[0];
        WalkToText = RoomText(existing.WalkToTarget);
        WalkToEntersBossRoom = existing.WalkToEntersBossRoom == true;
        LoopName = existing.LoopName;
        AutoLairSetupName = existing.AutoLairSetupName;
        CommandText = existing.CommandText ?? string.Empty;
        WaitSeconds = existing.WaitSeconds ?? 30;

        StopAfterLapsOn = existing.StopAfterLaps is > 0;
        StopAfterLaps = existing.StopAfterLaps is > 0 and var laps ? laps : 1;
        StopAfterMinutesOn = existing.StopAfterMinutes is > 0;
        StopAfterMinutes = existing.StopAfterMinutes is > 0 and var minutes ? minutes : 30;
        StopWhenBossOn = !string.IsNullOrWhiteSpace(existing.StopBossName);
        StopBossName = existing.StopBossName;
        SelectedStopBossMoment = MomentLabel(existing.StopBossMoment ?? EventBossMoment.Killed, existing.StopBossWindowFraction);
        StopBossLeadMinutes = existing.StopBossLeadMinutes ?? 0;
        foreach (EventCondition c in existing.StopConditions ?? new List<EventCondition>())
            StopConditions.Add(new EventConditionRowViewModel(c, RemoveStopCondition));
        StopConditions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ThenNeverRuns));

        _thenChosen = !isNew;
        SetThen(isNew ? DefaultThen() : existing.ResolvedThen);
        ThenLoopName = existing.ThenLoopName;
        ThenAutoLairSetupName = existing.ThenAutoLairSetupName;
        ThenWalkToText = RoomText(existing.ThenWalkTo);
        ThenWalkToEntersBossRoom = existing.ThenWalkToEntersBossRoom == true;
        ThenEventName = existing.ThenEventName;
    }

    public string DialogTitle => _isNew ? "New Event" : "Edit Event";

    // ----- Common fields ---------------------------------------------

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _disabledFlag;

    // ----- Sound -----------------------------------------------------

    // The sound this event plays when it fires: none, a built-in tone, or the
    // user's own file. Settings → Sounds → Event sounds decides whether event
    // sounds play at all, and how loud.
    public const string NoSoundLabel = "(no sound)";
    public const string SoundFileLabel = "Custom file…";

    public IReadOnlyList<string> SoundOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSoundFile), nameof(HasSound))]
    private string _selectedSound = NoSoundLabel;

    [ObservableProperty] private string _soundFile = string.Empty;

    public bool IsSoundFile => SelectedSound == SoundFileLabel;
    public bool HasSound => SelectedSound != NoSoundLabel;

    // The sound as it is stored: a tone id, a file path, or null for none.
    internal string? SelectedSoundValue()
    {
        if (IsSoundFile) return string.IsNullOrWhiteSpace(SoundFile) ? null : SoundFile.Trim();
        return SoundTones.All.FirstOrDefault(t => t.Label == SelectedSound).Id;
    }

    [RelayCommand]
    private void TestSound()
    {
        if (SelectedSoundValue() is not { } sound) return;
        try { AppServices.Current.Sounds.Preview(SoundCues.EventFired, sound); }
        catch (InvalidOperationException) { /* AppServices uninitialized — tests */ }
    }

    // ----- WHEN (trigger) — mutually-exclusive flags managed by XAML radios -----

    [ObservableProperty] private bool _isTriggerLogon;
    [ObservableProperty] private bool _isTriggerLogoff;
    [ObservableProperty] private bool _isTriggerRelog;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AtTimeError))]
    private bool _isTriggerAtTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AtTimeError))]
    private string _atTime = "12:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EveryError))]
    private bool _isTriggerEvery;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EveryError))]
    private int _everyAmount = 30;

    [ObservableProperty] private EventTimeUnit _everyUnit = EventTimeUnit.Seconds;

    // "When" — fires once the listed conditions all hold (EventTriggerType.State).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConditionsError))]
    private bool _isTriggerState;

    public ObservableCollection<EventConditionRowViewModel> Conditions { get; } = new();

    public string ConditionsError =>
        IsTriggerState && Conditions.Count == 0 ? "Add at least one condition." : string.Empty;
    public bool HasConditionsError => ConditionsError.Length > 0;

    [RelayCommand]
    private void AddCondition()
    {
        Conditions.Add(new EventConditionRowViewModel(
            new EventCondition { Stat = EventConditionStat.Money, Comparison = EventComparison.AtLeast },
            RemoveCondition));
        IsTriggerState = true;
    }

    private void RemoveCondition(EventConditionRowViewModel row) => Conditions.Remove(row);

    // Boss timer — a moment on one boss's timer (EventTriggerType.Boss).
    [ObservableProperty] private bool _isTriggerBoss;
    [ObservableProperty] private string? _bossName;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BossLeadApplies))]
    private string _selectedBossMoment = string.Empty;
    [ObservableProperty] private int _bossLeadMinutes;

    private readonly List<(string Label, EventBossMoment Moment, double? Fraction)> _bossMoments = new();
    public IReadOnlyList<string> BossMomentOptions { get; }
    public ObservableCollection<string> AvailableBossNames { get; } = new();
    // "N minutes early" means nothing for a kill.
    public bool BossLeadApplies => SelectedBoss() != EventBossMoment.Killed;

    // Picker source for the EveryUnit ComboBox.
    public IReadOnlyList<EventTimeUnit> EveryUnits { get; } =
        new[] { EventTimeUnit.Seconds, EventTimeUnit.Minutes, EventTimeUnit.Hours };

    public string AtTimeError =>
        IsTriggerAtTime && !LooksLikeHHmm(AtTime) ? "Use HH:mm (24-hour)." : string.Empty;

    public string EveryError =>
        IsTriggerEvery && EveryAmount <= 0 ? "Must be ≥ 1." : string.Empty;

    // ----- DO (action) ------------------------------------------------

    [ObservableProperty] private bool _isActionWalkTo;
    [ObservableProperty] private string _walkToText = string.Empty;

    // The event's choice for a walk-to room that is a boss room marked "stop before
    // entering": ticked walks in, unticked ends the walk one room short. Only
    // offered (and only saved) while the room in the box is such a room.
    [ObservableProperty] private bool _walkToEntersBossRoom;
    [ObservableProperty] private bool _thenWalkToEntersBossRoom;

    public string WalkToBossNotice => BossNotice(WalkToText, WalkToEntersBossRoom, "This walk");
    public bool HasWalkToBossNotice => WalkToBossNotice.Length > 0;
    public string ThenWalkToBossNotice => BossNotice(ThenWalkToText, ThenWalkToEntersBossRoom, "Then's walk");
    public bool HasThenWalkToBossNotice => ThenWalkToBossNotice.Length > 0;

    [ObservableProperty] private bool _isActionLoop;
    [ObservableProperty] private string? _loopName;

    // Saved loops the user can pick from for the Loop action (and a Then loop).
    public ObservableCollection<string> AvailableLoopNames { get; } = new();

    [ObservableProperty] private bool _isActionAutoLair;
    [ObservableProperty] private string? _autoLairSetupName;

    // Saved auto-lair setups for the AutoLair action (and a Then auto-lair).
    public ObservableCollection<string> AvailableAutoLairNames { get; } = new();

    [ObservableProperty] private bool _isActionCommand;
    [ObservableProperty] private string _commandText = string.Empty;

    [ObservableProperty] private bool _isActionRoomba;
    public IReadOnlyList<string> RoombaModeOptions { get; } = new[] { "Sort", "Inventory only" };
    [ObservableProperty] private string _selectedRoombaMode = "Sort";

    [ObservableProperty] private bool _isActionWait;
    [ObservableProperty] private int _waitSeconds = 30;

    [ObservableProperty] private bool _isActionRestUp;
    [ObservableProperty] private bool _isActionBankTrip;

    // Stash transfer: one of the character's stash rooms, to one of the game data's
    // banks. The pickers hold labels; the rooms behind them are looked up on Save.
    [ObservableProperty] private bool _isActionStashTransfer;
    [ObservableProperty] private string? _transferStash;
    [ObservableProperty] private string? _transferBank;
    public ObservableCollection<string> AvailableStashRooms { get; } = new();
    public ObservableCollection<string> AvailableBanks { get; } = new();
    private readonly Dictionary<string, RoomRef> _stashRooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RoomRef> _banks = new(StringComparer.Ordinal);

    // Fills one picker and returns the label of the saved room. A saved room that is
    // no longer on the list (unmarked as a stash, or a different game-data set) keeps
    // an entry of its own, so opening and saving the event doesn't drop it.
    private static string? FillRoomPicker(
        Dictionary<string, RoomRef> byLabel, ObservableCollection<string> labels,
        IReadOnlyList<(string Label, RoomRef Room)>? options, RoomRef? saved, string goneNote)
    {
        string? selected = null;
        foreach ((string label, RoomRef room) in options ?? Array.Empty<(string, RoomRef)>())
        {
            if (!byLabel.TryAdd(label, room)) continue;
            labels.Add(label);
            if (saved is not null && room.Map == saved.Map && room.Room == saved.Room) selected ??= label;
        }
        if (saved is null || selected is not null) return selected;
        string gone = $"{saved.Map}/{saved.Room} ({goneNote})";
        byLabel[gone] = saved;
        labels.Add(gone);
        return gone;
    }

    // ----- STOP AFTER (loop / auto-lair) -------------------------------

    public bool ShowsStopAfter => IsActionLoop || IsActionAutoLair;

    [ObservableProperty] private bool _stopAfterLapsOn;
    [ObservableProperty] private int _stopAfterLaps = 1;
    [ObservableProperty] private bool _stopAfterMinutesOn;
    [ObservableProperty] private int _stopAfterMinutes = 30;
    [ObservableProperty] private bool _stopWhenBossOn;
    [ObservableProperty] private string? _stopBossName;
    // The same moments as the Boss trigger (BossMomentOptions).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StopBossLeadApplies))]
    private string _selectedStopBossMoment = string.Empty;
    [ObservableProperty] private int _stopBossLeadMinutes;
    public bool StopBossLeadApplies => MomentEntry(SelectedStopBossMoment).Moment != EventBossMoment.Killed;

    public ObservableCollection<EventConditionRowViewModel> StopConditions { get; } = new();

    [RelayCommand]
    private void AddStopCondition() =>
        StopConditions.Add(new EventConditionRowViewModel(
            new EventCondition { Stat = EventConditionStat.Encumbrance, Comparison = EventComparison.AtLeast, Value = 80 },
            RemoveStopCondition));

    private void RemoveStopCondition(EventConditionRowViewModel row) => StopConditions.Remove(row);

    // ----- THEN (after the action) ------------------------------------

    [ObservableProperty] private bool _isThenNothing;
    [ObservableProperty] private bool _isThenResume;
    [ObservableProperty] private bool _isThenLoop;
    [ObservableProperty] private bool _isThenAutoLair;
    [ObservableProperty] private bool _isThenWalkTo;
    [ObservableProperty] private bool _isThenEvent;
    [ObservableProperty] private string? _thenLoopName;
    [ObservableProperty] private string? _thenAutoLairSetupName;
    [ObservableProperty] private string _thenWalkToText = string.Empty;
    [ObservableProperty] private string? _thenEventName;

    // The character's other events, for a Then that fires one.
    public ObservableCollection<string> AvailableEventNames { get; } = new();

    // Then runs only once the action is done — a loop / auto-lair with no stop
    // rule never is, so say so.
    public bool ThenNeverRuns =>
        ShowsStopAfter && !IsThenNothing
        && !(StopAfterLapsOn && IsActionLoop) && !StopAfterMinutesOn && !StopWhenBossOn
        && StopConditions.Count == 0;

    // WHAT-side validation happens on Save (popup), not inline — fewer red labels
    // cluttering the form. WHEN-side format errors stay inline because they're
    // objectively wrong syntax the user can see at a glance. Command never errors
    // at edit time: an empty CommandText is a valid event whose Fire sends a bare
    // carriage return.
    public bool HasAtTimeError => AtTimeError.Length > 0;
    public bool HasEveryError  => EveryError.Length  > 0;

    public bool CanSave =>
        AtTimeError.Length == 0
        && EveryError.Length == 0
        && ConditionsError.Length == 0;

    // ----- Save / Cancel ----------------------------------------------

    // Keeps the Disabled box in step with a switch of `original` made elsewhere (a
    // remote @event) while this window is open on it. The window works on a copy
    // taken when it opened, and Save replaces the event with that copy: without
    // this, a save would quietly put the event back the way it was before the
    // switch. Only the box follows; everything else typed here stays as typed.
    // Cancel changes nothing, so the switch stands. Returns what stops following,
    // for the caller to run when the window closes.
    public Action FollowSwitches(ObservableCollection<ScheduledEvent> events, ScheduledEvent original)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(original);
        void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // The same event put back in its place is a switch; an edit puts in
            // another.
            if (e.Action == NotifyCollectionChangedAction.Replace
                && e.OldItems is [ScheduledEvent before] && e.NewItems is [ScheduledEvent after]
                && ReferenceEquals(before, original) && ReferenceEquals(after, original))
                DisabledFlag = original.Disabled;
        }
        events.CollectionChanged += OnChanged;
        return () => events.CollectionChanged -= OnChanged;
    }

    [RelayCommand]
    private void Save()
    {
        if (!CanSave) return;

        // WHAT-side validation: if the user selected an action but
        // didn't fill in its target, popup a message + stay open.
        // Doesn't apply to Command (empty = bare CR is intentional).
        if (TryGetMissingTargetMessage() is { } missing)
        {
            // Try the popup; fall through silently if AppServices isn't
            // initialized. Either way, we don't fire CloseRequested.
            try { AppServices.Current.Dialogs.ShowInfo("Event not saved", missing); }
            catch (InvalidOperationException) { /* AppServices uninitialized — tests */ }
            return;
        }

        ScheduledEvent result = new()
        {
            Name = Name?.Trim() ?? string.Empty,
            Disabled = DisabledFlag,
            Sound = SelectedSoundValue(),
            TriggerType = SelectedTriggerType(),
            ActionType = SelectedActionType(),
            Then = SelectedThen(),
        };

        switch (result.TriggerType)
        {
            case EventTriggerType.AtTime:
                result.AtTime = AtTime;
                break;
            case EventTriggerType.Every:
                result.EveryAmount = EveryAmount;
                result.EveryUnit = EveryUnit;
                break;
            case EventTriggerType.State:
                result.Conditions = Conditions.Select(static c => c.ToModel()).ToList();
                break;
            case EventTriggerType.Boss:
                result.BossName = BossName;
                result.BossMoment = SelectedBoss();
                result.BossWindowFraction = SelectedBossEntry().Fraction;
                result.BossLeadMinutes = BossLeadApplies && BossLeadMinutes > 0 ? BossLeadMinutes : null;
                break;
        }

        switch (result.ActionType)
        {
            case EventActionType.WalkTo:
                if (ResolveRoom(WalkToText) is { Ok: true } wt)
                {
                    result.WalkToTarget = new RoomRef(wt.Map!.Value, wt.Room!.Value);
                    if (WalkToEntersBossRoom && StopBeforeBossIn(wt) is not null) result.WalkToEntersBossRoom = true;
                }
                break;
            case EventActionType.Loop:
                result.LoopName = LoopName;
                break;
            case EventActionType.AutoLair:
                result.AutoLairSetupName = AutoLairSetupName;
                break;
            case EventActionType.Command:
                result.CommandText = CommandText;
                break;
            case EventActionType.Roomba:
                result.RoombaMode = SelectedRoombaMode == RoombaModeOptions[1]
                    ? EventRoombaMode.InventoryOnly
                    : EventRoombaMode.Sort;
                break;
            case EventActionType.Wait:
                result.WaitSeconds = WaitSeconds;
                break;
            case EventActionType.StashTransfer:
                result.TransferStash = TransferStash is { } stash ? _stashRooms.GetValueOrDefault(stash) : null;
                result.TransferBank = TransferBank is { } bank ? _banks.GetValueOrDefault(bank) : null;
                break;
        }

        if (ShowsStopAfter)
        {
            if (IsActionLoop && StopAfterLapsOn) result.StopAfterLaps = StopAfterLaps;
            if (StopAfterMinutesOn) result.StopAfterMinutes = StopAfterMinutes;
            if (StopWhenBossOn)
            {
                var stop = MomentEntry(SelectedStopBossMoment);
                result.StopBossName = StopBossName;
                result.StopBossMoment = stop.Moment;
                result.StopBossWindowFraction = stop.Fraction;
                result.StopBossLeadMinutes = StopBossLeadApplies && StopBossLeadMinutes > 0 ? StopBossLeadMinutes : null;
            }
            if (StopConditions.Count > 0)
                result.StopConditions = StopConditions.Select(static c => c.ToModel()).ToList();
        }

        switch (result.Then)
        {
            case EventThenType.Loop:
                result.ThenLoopName = ThenLoopName;
                break;
            case EventThenType.AutoLair:
                result.ThenAutoLairSetupName = ThenAutoLairSetupName;
                break;
            case EventThenType.WalkTo:
                if (ResolveRoom(ThenWalkToText) is { Ok: true } tw)
                {
                    result.ThenWalkTo = new RoomRef(tw.Map!.Value, tw.Room!.Value);
                    if (ThenWalkToEntersBossRoom && StopBeforeBossIn(tw) is not null) result.ThenWalkToEntersBossRoom = true;
                }
                break;
            case EventThenType.Event:
                result.ThenEventName = ThenEventName;
                break;
        }

        CloseRequested?.Invoke(result);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    internal string? TryGetMissingTargetMessage()
    {
        if (IsTriggerBoss && string.IsNullOrWhiteSpace(BossName))
            return "No boss selected. Pick one from the Bosses tab's list.";
        if (IsActionWalkTo)
        {
            WalkToResolution wt = ResolveWalkTo();
            if (!wt.Ok)
                return wt.ErrorMessage
                    ?? "No walk-to target selected. Enter a coordinate (e.g. 1/297) or an unambiguous room name.";
        }
        if (IsActionLoop && string.IsNullOrWhiteSpace(LoopName))
            return "No loop selected. Pick a saved loop from the dropdown.";
        if (IsActionAutoLair && string.IsNullOrWhiteSpace(AutoLairSetupName))
            return "No auto-lair setup selected. Pick a saved setup from the dropdown.";
        if (IsActionWait && WaitSeconds <= 0)
            return "Wait needs a number of seconds.";
        if (IsActionStashTransfer && (TransferStash is null || !_stashRooms.ContainsKey(TransferStash)))
            return AvailableStashRooms.Count == 0
                ? "You have no stash rooms. Mark one on the Navigation map (right-click → Toggle: Stash room)."
                : "No stash room selected. Pick one from the dropdown.";
        if (IsActionStashTransfer && (TransferBank is null || !_banks.ContainsKey(TransferBank)))
            return "No bank selected. Pick one from the dropdown.";
        if (ShowsStopAfter && StopWhenBossOn && string.IsNullOrWhiteSpace(StopBossName))
            return "No boss selected for Stop after's boss timer.";
        if (IsThenLoop && string.IsNullOrWhiteSpace(ThenLoopName))
            return "No loop selected for Then.";
        if (IsThenAutoLair && string.IsNullOrWhiteSpace(ThenAutoLairSetupName))
            return "No auto-lair setup selected for Then.";
        if (IsThenWalkTo && ResolveRoom(ThenWalkToText) is { Ok: false } then)
            return then.ErrorMessage ?? "No room for Then's walk-to. Enter a coordinate (e.g. 1/297) or an unambiguous room name.";
        if (IsThenEvent && string.IsNullOrWhiteSpace(ThenEventName))
            return "No event selected for Then.";
        return null;
    }

    // ----- Radio groups -----------------------------------------------

    // The XAML radios enforce mutual exclusion via GroupName, but
    // programmatic callers don't go through the radios — so each flag, when it
    // flips on, clears its siblings. This way the VM contract — "exactly one
    // trigger, one action, one Then" — holds however a property was set.
    partial void OnIsTriggerLogonChanged(bool value)  { if (value) SetTrigger(EventTriggerType.Logon);  Refresh(); }
    partial void OnIsTriggerLogoffChanged(bool value) { if (value) SetTrigger(EventTriggerType.Logoff); Refresh(); }
    partial void OnIsTriggerRelogChanged(bool value)  { if (value) SetTrigger(EventTriggerType.Relog);  Refresh(); }
    partial void OnIsTriggerAtTimeChanged(bool value) { if (value) SetTrigger(EventTriggerType.AtTime); Refresh(); }
    partial void OnIsTriggerEveryChanged(bool value)  { if (value) SetTrigger(EventTriggerType.Every);  Refresh(); }
    partial void OnIsTriggerStateChanged(bool value)  { if (value) SetTrigger(EventTriggerType.State);  Refresh(); }
    partial void OnIsTriggerBossChanged(bool value)   { if (value) SetTrigger(EventTriggerType.Boss);   Refresh(); }

    private void SetTrigger(EventTriggerType t)
    {
        IsTriggerLogon  = t == EventTriggerType.Logon;
        IsTriggerLogoff = t == EventTriggerType.Logoff;
        IsTriggerRelog  = t == EventTriggerType.Relog;
        IsTriggerAtTime = t == EventTriggerType.AtTime;
        IsTriggerEvery  = t == EventTriggerType.Every;
        IsTriggerState  = t == EventTriggerType.State;
        IsTriggerBoss   = t == EventTriggerType.Boss;
    }

    partial void OnIsActionWalkToChanged(bool value)   { if (value) SetAction(EventActionType.WalkTo);   Refresh(); }
    partial void OnIsActionLoopChanged(bool value)     { if (value) SetAction(EventActionType.Loop);     Refresh(); }
    partial void OnIsActionAutoLairChanged(bool value) { if (value) SetAction(EventActionType.AutoLair); Refresh(); }
    partial void OnIsActionCommandChanged(bool value)  { if (value) SetAction(EventActionType.Command);  Refresh(); }
    partial void OnIsActionRoombaChanged(bool value)   { if (value) SetAction(EventActionType.Roomba);   Refresh(); }
    partial void OnIsActionWaitChanged(bool value)     { if (value) SetAction(EventActionType.Wait);     Refresh(); }
    partial void OnIsActionRestUpChanged(bool value)   { if (value) SetAction(EventActionType.RestUp);   Refresh(); }
    partial void OnIsActionBankTripChanged(bool value) { if (value) SetAction(EventActionType.BankTrip); Refresh(); }
    partial void OnIsActionStashTransferChanged(bool value) { if (value) SetAction(EventActionType.StashTransfer); Refresh(); }

    private void SetAction(EventActionType a)
    {
        IsActionWalkTo   = a == EventActionType.WalkTo;
        IsActionLoop     = a == EventActionType.Loop;
        IsActionAutoLair = a == EventActionType.AutoLair;
        IsActionCommand  = a == EventActionType.Command;
        IsActionRoomba   = a == EventActionType.Roomba;
        IsActionWait     = a == EventActionType.Wait;
        IsActionRestUp   = a == EventActionType.RestUp;
        IsActionBankTrip = a == EventActionType.BankTrip;
        IsActionStashTransfer = a == EventActionType.StashTransfer;
        if (!_thenChosen) SetThenDefault();
    }

    partial void OnIsThenNothingChanged(bool value)  { if (value) ChooseThen(EventThenType.Nothing);  Refresh(); }
    partial void OnIsThenResumeChanged(bool value)   { if (value) ChooseThen(EventThenType.Resume);   Refresh(); }
    partial void OnIsThenLoopChanged(bool value)     { if (value) ChooseThen(EventThenType.Loop);     Refresh(); }
    partial void OnIsThenAutoLairChanged(bool value) { if (value) ChooseThen(EventThenType.AutoLair); Refresh(); }
    partial void OnIsThenWalkToChanged(bool value)   { if (value) ChooseThen(EventThenType.WalkTo);   Refresh(); }
    partial void OnIsThenEventChanged(bool value)    { if (value) ChooseThen(EventThenType.Event);    Refresh(); }

    private void ChooseThen(EventThenType t)
    {
        if (!_settingThenDefault) _thenChosen = true;
        SetThen(t);
    }

    private void SetThen(EventThenType t)
    {
        IsThenNothing  = t == EventThenType.Nothing;
        IsThenResume   = t == EventThenType.Resume;
        IsThenLoop     = t == EventThenType.Loop;
        IsThenAutoLair = t == EventThenType.AutoLair;
        IsThenWalkTo   = t == EventThenType.WalkTo;
        IsThenEvent    = t == EventThenType.Event;
    }

    // A command does nothing after; anything else goes back to what was running.
    private EventThenType DefaultThen() =>
        IsActionCommand ? EventThenType.Nothing : EventThenType.Resume;

    private void SetThenDefault()
    {
        _settingThenDefault = true;
        try { SetThen(DefaultThen()); }
        finally { _settingThenDefault = false; }
    }

    partial void OnAtTimeChanged(string value)          => OnPropertyChanged(nameof(AtTimeError));
    partial void OnEveryAmountChanged(int value)        => OnPropertyChanged(nameof(EveryError));
    partial void OnStopAfterLapsOnChanged(bool value)      => OnPropertyChanged(nameof(ThenNeverRuns));
    partial void OnStopAfterMinutesOnChanged(bool value)   => OnPropertyChanged(nameof(ThenNeverRuns));
    partial void OnStopWhenBossOnChanged(bool value)
    {
        // A boss-triggered event most likely camps that same boss.
        if (value && string.IsNullOrWhiteSpace(StopBossName)) StopBossName = BossName;
        OnPropertyChanged(nameof(ThenNeverRuns));
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(AtTimeError));
        OnPropertyChanged(nameof(EveryError));
        OnPropertyChanged(nameof(HasAtTimeError));
        OnPropertyChanged(nameof(HasEveryError));
        OnPropertyChanged(nameof(ConditionsError));
        OnPropertyChanged(nameof(HasConditionsError));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ShowsStopAfter));
        OnPropertyChanged(nameof(ThenNeverRuns));
        RefreshBossNotices();
    }

    private EventTriggerType SelectedTriggerType()
    {
        if (IsTriggerLogoff) return EventTriggerType.Logoff;
        if (IsTriggerRelog)  return EventTriggerType.Relog;
        if (IsTriggerAtTime) return EventTriggerType.AtTime;
        if (IsTriggerEvery)  return EventTriggerType.Every;
        if (IsTriggerState)  return EventTriggerType.State;
        if (IsTriggerBoss)   return EventTriggerType.Boss;
        return EventTriggerType.Logon;
    }

    private EventActionType SelectedActionType()
    {
        if (IsActionLoop)     return EventActionType.Loop;
        if (IsActionAutoLair) return EventActionType.AutoLair;
        if (IsActionCommand)  return EventActionType.Command;
        if (IsActionRoomba)   return EventActionType.Roomba;
        if (IsActionWait)     return EventActionType.Wait;
        if (IsActionRestUp)   return EventActionType.RestUp;
        if (IsActionBankTrip) return EventActionType.BankTrip;
        if (IsActionStashTransfer) return EventActionType.StashTransfer;
        return EventActionType.WalkTo;
    }

    private EventThenType SelectedThen()
    {
        if (IsThenResume)   return EventThenType.Resume;
        if (IsThenLoop)     return EventThenType.Loop;
        if (IsThenAutoLair) return EventThenType.AutoLair;
        if (IsThenWalkTo)   return EventThenType.WalkTo;
        if (IsThenEvent)    return EventThenType.Event;
        return EventThenType.Nothing;
    }

    private EventBossMoment SelectedBoss() => SelectedBossEntry().Moment;

    private (string Label, EventBossMoment Moment, double? Fraction) SelectedBossEntry() => MomentEntry(SelectedBossMoment);

    private (string Label, EventBossMoment Moment, double? Fraction) MomentEntry(string label)
    {
        int i = _bossMoments.FindIndex(m => m.Label == label);
        return _bossMoments[Math.Max(0, i)];
    }

    // The option for a saved moment — for an early window, the column with its
    // fraction (the earliest when none was saved).
    private string MomentLabel(EventBossMoment moment, double? fraction)
    {
        int i = _bossMoments.FindIndex(m => m.Moment == moment
            && (moment != EventBossMoment.EarlyWindow || fraction is not { } f
                || Math.Abs(f - m.Fraction!.Value) < 0.001));
        return BossMomentOptions[Math.Max(0, i)];
    }

    // Three-state result of resolving a room box: resolved (Map+Room set, no
    // error), unresolved-with-reason (ErrorMessage set — no match or ambiguous), or
    // empty (everything null — the validator supplies the default "No walk-to
    // target selected" message).
    internal readonly record struct WalkToResolution(int? Map, int? Room, string? ErrorMessage)
    {
        public bool Ok => Map is not null && Room is not null;
    }

    internal WalkToResolution ResolveWalkTo() => ResolveRoom(WalkToText);

    // ----- Walk-to room suggestions -----------------------------------

    private const int MaxRoomSuggestions = 50;

    // Feeds the dropdown under both Walk to boxes as the user types. The shape is
    // the one AutoCompleteBox's AsyncPopulator takes; the search itself is quick
    // and runs where it is called, on the UI thread, which is where the room
    // graph lives.
    public Func<string?, CancellationToken, Task<IEnumerable<object>>> RoomSuggestions { get; }

    // The places a typed name, coordinate, boss or GOTO favourite could mean: the
    // Navigation window's own room search, so the same text finds the same rooms
    // in both. Rows with nowhere to walk to are left out, since picking one would
    // put no room in the box.
    internal IReadOnlyList<RoomSearchResult> SuggestRooms(string? text)
    {
        string needle = text?.Trim() ?? string.Empty;
        if (_search is null || needle.Length == 0) return Array.Empty<RoomSearchResult>();
        return _search.Search(needle, source: null, cap: 200,
                includeMonsters: false, includeFavorites: true, includeBosses: true)
            .Where(static m => !m.IsInformational)
            .Take(MaxRoomSuggestions)
            .ToList();
    }

    // A saved room as the box shows it: "1/297 - Bank of Godfrey", the same text a
    // picked suggestion leaves there, so a reopened event names its room instead
    // of showing a bare number. Just the coordinate when the room isn't on the map.
    private string RoomText(RoomRef? room)
    {
        if (room is null) return string.Empty;
        string coord = $"{room.Map}/{room.Room}";
        RoomSearchResult? known = _search?.Search(coord, source: null, cap: 1, includeMonsters: false)
            .FirstOrDefault(m => m.MonsterTag is null && m.Key.Map == room.Map && m.Key.Room == room.Room);
        return known?.DisplayName ?? coord;
    }

    // ----- Walk-to into a stop-before boss room -------------------------

    private string? StopBeforeBossIn(WalkToResolution room) =>
        room.Ok ? _stopBeforeBoss?.Invoke(new RoomKey(room.Map!.Value, room.Room!.Value)) : null;

    // The boss whose stop-before room a Walk to box holds, null for any other room.
    // Read from the box's coordinate alone (typed, or left by a picked suggestion):
    // it is asked on every keystroke, and a name search isn't.
    private (RoomKey Room, string Boss)? StopBeforeBossIn(string text)
    {
        if (_stopBeforeBoss is null || string.IsNullOrWhiteSpace(text)) return null;
        int nameAt = text.IndexOf(" - ", StringComparison.Ordinal);
        (int? map, int? room) = RoomSearchService.TryParseCoordinate(nameAt > 0 ? text[..nameAt] : text);
        if (map is not int m || room is not int r) return null;
        RoomKey key = new(m, r);
        return _stopBeforeBoss(key) is { } boss ? (key, boss) : null;
    }

    // Shown under a Walk to box whose room is such a boss room: what this walk
    // will do about the stop, as the tick box under it stands.
    private string BossNotice(string text, bool enters, string walk)
    {
        if (StopBeforeBossIn(text) is not { } hit) return string.Empty;
        string marked = $"{hit.Boss}'s room is marked Stop before entering on the Bosses tab.";
        return enters
            ? $"{marked} {walk} ignores that and goes into the room."
            : $"{marked} {walk} ends in the room next to it and does NOT go in.";
    }

    // Shown in the Then block while the action's walk keeps the stop. The walk ends
    // outside the boss room and Then is all that happens after it, so an event set
    // up this way never enters the room unless Then (or an event it fires) does.
    // That can be the point (walk up, look in, let a follow-on event decide), so
    // this explains rather than blocks.
    public string StopsOutsideHint
    {
        get
        {
            if (!IsActionWalkTo || WalkToEntersBossRoom || StopBeforeBossIn(WalkToText) is not { } hit)
                return string.Empty;
            string outside = $"This event stops in the room next to {hit.Boss}'s room and does not go in. Then is what happens from there.";
            const string goIn = "To go in, set Then to Walk to this same room and tick its box, or fire a follow-on event that walks in.";

            if (IsThenWalkTo && StopBeforeBossIn(ThenWalkToText) is { } then && then.Room.Equals(hit.Room))
                return ThenWalkToEntersBossRoom
                    ? $"{outside} As set, Then walks into the room."
                    : $"{outside} As set, Then walks to the same room and stops short of it again, so the event never goes in. Tick Then's box to walk in.";
            if (IsThenEvent)
                return $"{outside} As set, Then fires {(string.IsNullOrWhiteSpace(ThenEventName) ? "another event" : $"\"{ThenEventName}\"")} from outside the room: "
                    + "that event has to do whatever comes next (look in, walk in).";
            string asSet = IsThenResume ? "Then goes back to what was running, without entering."
                : IsThenNothing ? "Then does nothing, so the character stays outside the room."
                : IsThenWalkTo ? "Then walks on to a different room, without entering."
                : "Then starts from outside the room, without entering.";
            return $"{outside} As set, {asSet} {goIn}";
        }
    }

    public bool HasStopsOutsideHint => StopsOutsideHint.Length > 0;

    private void RefreshBossNotices()
    {
        OnPropertyChanged(nameof(WalkToBossNotice));
        OnPropertyChanged(nameof(HasWalkToBossNotice));
        OnPropertyChanged(nameof(ThenWalkToBossNotice));
        OnPropertyChanged(nameof(HasThenWalkToBossNotice));
        OnPropertyChanged(nameof(StopsOutsideHint));
        OnPropertyChanged(nameof(HasStopsOutsideHint));
    }

    partial void OnWalkToTextChanged(string value) => RefreshBossNotices();
    partial void OnThenWalkToTextChanged(string value) => RefreshBossNotices();
    partial void OnWalkToEntersBossRoomChanged(bool value) => RefreshBossNotices();
    partial void OnThenWalkToEntersBossRoomChanged(bool value) => RefreshBossNotices();
    partial void OnThenEventNameChanged(string? value) => RefreshBossNotices();

    // Resolve a room box via RoomSearchService. Accepts coord (1/297, 1 297,
    // 1,297) directly, alone or leading a picked suggestion's "1/297 - Name"; for
    // names, requires exactly one room-name match (room-tier only — monster
    // matches don't qualify here since walk-to means a destination, not a mob).
    // Distinguishes no-match vs ambiguous-match in the error so the user-facing
    // popup can say the right thing instead of blanket "no target selected".
    private WalkToResolution ResolveRoom(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(null, null, null);

        // Coord short-circuit — works without a RoomSearchService.
        int nameAt = text.IndexOf(" - ", StringComparison.Ordinal);
        (int? coordMap, int? coordRoom) = RoomSearchService.TryParseCoordinate(nameAt > 0 ? text[..nameAt] : text);
        if (coordMap is int cm && coordRoom is int cr)
            return new(cm, cr, null);

        if (_search is null) return new(null, null, null);

        // Cap chosen large enough to count ambiguity without
        // truncating it into a false "no match". The old cap=5
        // would fill on a popular room-name prefix before the
        // ambiguity could even be observed.
        IReadOnlyList<RoomSearchResult> matches =
            _search.Search(text, source: null, cap: 50, includeAcronyms: false);
        // Want exactly one ROOM match (MonsterTag null). Monster
        // matches are ignored — the editor's WalkTo is for places,
        // not mob lairs.
        List<RoomSearchResult> rooms = matches
            .Where(mm => mm.MonsterTag is null && !mm.IsInformational)
            .ToList();
        if (rooms.Count == 0)
            return new(null, null,
                $"No room matches '{text}'. Pick one from the list that opens as you type, or enter a coordinate (e.g. 1/297).");
        if (rooms.Count > 1)
            return new(null, null,
                $"'{text}' matches {rooms.Count} rooms — pick the one you mean from the list that opens as you type, or use a coordinate (e.g. 1/297).");
        return new(rooms[0].Key.Map, rooms[0].Key.Room, null);
    }

    private static bool LooksLikeHHmm(string s) =>
        TimeOnly.TryParseExact(s, "HH:mm", out _);
}
