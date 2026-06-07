using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.ViewModels.Navigation;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// Modeless editor for one <see cref="ScheduledEvent"/>. Phase 8 PR
/// 8.4 — WHEN (trigger) and WHAT (action) split into two separate
/// radio groups so the user isn't picking from a single mixed list
/// the way MegaMUD's dialog forces.
/// </summary>
/// <remarks>
/// <para>
/// Edit semantics: the VM works on its own field set; the original
/// <see cref="ScheduledEvent"/> isn't touched until Save. Save returns
/// the materialised event (either fresh-constructed for a new event,
/// or with the original's mutations applied for a modify). Cancel
/// returns <c>null</c>. The caller decides whether the result goes
/// into <see cref="EventManager.Add"/> (new) or
/// <see cref="EventManager.Replace"/> (modify).
/// </para>
/// <para>
/// Walk-to / Loop / Auto-lair pickers bind to the live
/// <see cref="LoopManager"/> / <see cref="LairManager"/> collections
/// + <see cref="RoomSearchService"/> so the user sees the same names
/// + room references the rest of the app uses.
/// </para>
/// </remarks>
public sealed partial class EventEditDialogViewModel : ObservableObject, IDialogViewModel<ScheduledEvent?>
{
    public event Action<ScheduledEvent?>? CloseRequested;

    private readonly bool _isNew;
    private readonly LoopManager? _loops;
    private readonly LairManager? _lairs;
    private readonly RoomSearchService? _search;

    public EventEditDialogViewModel(
        ScheduledEvent existing,
        bool isNew,
        LoopManager? loops = null,
        LairManager? lairs = null,
        RoomSearchService? search = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        _isNew = isNew;
        _loops = loops;
        _lairs = lairs;
        _search = search;

        // Saved-loop / saved-setup dropdown contents — snapshot on
        // open. Edits to the underlying manager while the dialog is
        // open don't ripple through; user closes + re-opens to see new
        // names. Matches every other dropdown in the app.
        if (loops is not null)
            foreach (Loop l in loops.Loops) AvailableLoopNames.Add(l.Name);
        if (lairs is not null)
            foreach (LairSetup s in lairs.Setups) AvailableAutoLairNames.Add(s.Name);

        Name = existing.Name;
        DisabledFlag = existing.Disabled;

        switch (existing.TriggerType)
        {
            case EventTriggerType.Logoff: IsTriggerLogoff = true; break;
            case EventTriggerType.Relog:  IsTriggerRelog  = true; break;
            case EventTriggerType.AtTime: IsTriggerAtTime = true; break;
            case EventTriggerType.Every:  IsTriggerEvery  = true; break;
            default:                      IsTriggerLogon  = true; break;
        }
        AtTime = existing.AtTime ?? "12:00";
        EveryAmount = existing.EveryAmount ?? 30;
        EveryUnit = existing.EveryUnit ?? EventTimeUnit.Seconds;

        switch (existing.ActionType)
        {
            case EventActionType.Loop:     IsActionLoop     = true; break;
            case EventActionType.AutoLair: IsActionAutoLair = true; break;
            case EventActionType.Command:  IsActionCommand  = true; break;
            default:                       IsActionWalkTo   = true; break;
        }
        WalkToText = existing.WalkToTarget is { } t ? $"{t.Map}/{t.Room}" : string.Empty;
        LoopName = existing.LoopName;
        AutoLairSetupName = existing.AutoLairSetupName;
        CommandText = existing.CommandText ?? string.Empty;
    }

    public string DialogTitle => _isNew ? "New Event" : "Edit Event";

    // ----- Common fields ---------------------------------------------

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _disabledFlag;

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

    /// <summary>Picker source for the EveryUnit ComboBox.</summary>
    public IReadOnlyList<EventTimeUnit> EveryUnits { get; } =
        new[] { EventTimeUnit.Seconds, EventTimeUnit.Minutes, EventTimeUnit.Hours };

    public string AtTimeError =>
        IsTriggerAtTime && !LooksLikeHHmm(AtTime) ? "Use HH:mm (24-hour)." : string.Empty;

    public string EveryError =>
        IsTriggerEvery && EveryAmount <= 0 ? "Must be ≥ 1." : string.Empty;

    // ----- WHAT (action) ----------------------------------------------

    [ObservableProperty] private bool _isActionWalkTo;
    [ObservableProperty] private string _walkToText = string.Empty;

    [ObservableProperty] private bool _isActionLoop;
    [ObservableProperty] private string? _loopName;

    /// <summary>Saved loops the user can pick from for the Loop action.</summary>
    public ObservableCollection<string> AvailableLoopNames { get; } = new();

    [ObservableProperty] private bool _isActionAutoLair;
    [ObservableProperty] private string? _autoLairSetupName;

    /// <summary>Saved auto-lair setups for the AutoLair action.</summary>
    public ObservableCollection<string> AvailableAutoLairNames { get; } = new();

    [ObservableProperty] private bool _isActionCommand;
    [ObservableProperty] private string _commandText = string.Empty;

    /// <summary>
    /// WHAT-side validation happens on Save (popup), not inline —
    /// fewer red labels cluttering the form. WHEN-side format errors
    /// stay inline because they're objectively wrong syntax the user
    /// can see at a glance. Command never errors at edit time: an
    /// empty CommandText is a valid event whose Fire sends a bare
    /// carriage return.
    /// </summary>
    public bool HasAtTimeError => AtTimeError.Length > 0;
    public bool HasEveryError  => EveryError.Length  > 0;

    public bool CanSave =>
        AtTimeError.Length == 0
        && EveryError.Length == 0;

    // ----- Save / Cancel ----------------------------------------------

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
            // initialized (tests). Either way, we don't fire CloseRequested.
            try { AppServices.Current.Dialogs.ShowInfo("Event not saved", missing); }
            catch (InvalidOperationException) { /* AppServices uninitialized — tests */ }
            return;
        }

        ScheduledEvent result = new()
        {
            Name = Name?.Trim() ?? string.Empty,
            Disabled = DisabledFlag,
            TriggerType = SelectedTriggerType(),
            ActionType = SelectedActionType(),
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
        }

        switch (result.ActionType)
        {
            case EventActionType.WalkTo:
                if (TryResolveWalkTo(out int m, out int r))
                    result.WalkToTarget = new RoomRef(m, r);
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
        }

        CloseRequested?.Invoke(result);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    /// <summary>
    /// Internal so tests can poke the validation path without going
    /// through a dispatcher / DialogService.
    /// </summary>
    internal string? TryGetMissingTargetMessage()
    {
        if (IsActionWalkTo && !TryResolveWalkTo(out _, out _))
            return "No walk-to target selected. Enter a coordinate (e.g. 1/297) or an unambiguous room name.";
        if (IsActionLoop && string.IsNullOrWhiteSpace(LoopName))
            return "No loop selected. Pick a saved loop from the dropdown.";
        if (IsActionAutoLair && string.IsNullOrWhiteSpace(AutoLairSetupName))
            return "No auto-lair setup selected. Pick a saved setup from the dropdown.";
        return null;
    }

    /// <summary>
    /// Toggle-source partials. The XAML radios enforce mutual
    /// exclusion via <c>GroupName</c>, but tests + programmatic
    /// callers don't go through the radios — so the partials also
    /// clear sibling flags when a new one flips on. This way the VM
    /// contract — "exactly one trigger, exactly one action" — holds
    /// regardless of how the property was set.
    /// </summary>
    partial void OnIsTriggerLogonChanged(bool value)
    {
        if (value) { IsTriggerLogoff = IsTriggerRelog = IsTriggerAtTime = IsTriggerEvery = false; }
        Refresh();
    }
    partial void OnIsTriggerLogoffChanged(bool value)
    {
        if (value) { IsTriggerLogon = IsTriggerRelog = IsTriggerAtTime = IsTriggerEvery = false; }
        Refresh();
    }
    partial void OnIsTriggerRelogChanged(bool value)
    {
        if (value) { IsTriggerLogon = IsTriggerLogoff = IsTriggerAtTime = IsTriggerEvery = false; }
        Refresh();
    }
    partial void OnIsTriggerAtTimeChanged(bool value)
    {
        if (value) { IsTriggerLogon = IsTriggerLogoff = IsTriggerRelog = IsTriggerEvery = false; }
        Refresh();
    }
    partial void OnIsTriggerEveryChanged(bool value)
    {
        if (value) { IsTriggerLogon = IsTriggerLogoff = IsTriggerRelog = IsTriggerAtTime = false; }
        Refresh();
    }
    partial void OnIsActionWalkToChanged(bool value)
    {
        if (value) { IsActionLoop = IsActionAutoLair = IsActionCommand = false; }
        Refresh();
    }
    partial void OnIsActionLoopChanged(bool value)
    {
        if (value) { IsActionWalkTo = IsActionAutoLair = IsActionCommand = false; }
        Refresh();
    }
    partial void OnIsActionAutoLairChanged(bool value)
    {
        if (value) { IsActionWalkTo = IsActionLoop = IsActionCommand = false; }
        Refresh();
    }
    partial void OnIsActionCommandChanged(bool value)
    {
        if (value) { IsActionWalkTo = IsActionLoop = IsActionAutoLair = false; }
        Refresh();
    }
    partial void OnAtTimeChanged(string value)          => OnPropertyChanged(nameof(AtTimeError));
    partial void OnEveryAmountChanged(int value)        => OnPropertyChanged(nameof(EveryError));

    private void Refresh()
    {
        OnPropertyChanged(nameof(AtTimeError));
        OnPropertyChanged(nameof(EveryError));
        OnPropertyChanged(nameof(HasAtTimeError));
        OnPropertyChanged(nameof(HasEveryError));
        OnPropertyChanged(nameof(CanSave));
    }

    private EventTriggerType SelectedTriggerType()
    {
        if (IsTriggerLogoff) return EventTriggerType.Logoff;
        if (IsTriggerRelog)  return EventTriggerType.Relog;
        if (IsTriggerAtTime) return EventTriggerType.AtTime;
        if (IsTriggerEvery)  return EventTriggerType.Every;
        return EventTriggerType.Logon;
    }

    private EventActionType SelectedActionType()
    {
        if (IsActionLoop)     return EventActionType.Loop;
        if (IsActionAutoLair) return EventActionType.AutoLair;
        if (IsActionCommand)  return EventActionType.Command;
        return EventActionType.WalkTo;
    }

    /// <summary>
    /// Resolve <see cref="WalkToText"/> via
    /// <see cref="RoomSearchService"/>. Accepts coord (<c>1/297</c>,
    /// <c>1 297</c>, <c>1,297</c>) directly; for names, requires
    /// exactly one room-name match (room-tier only — monster matches
    /// don't qualify here since walk-to means a destination, not a
    /// mob).
    /// </summary>
    internal bool TryResolveWalkTo(out int map, out int room)
    {
        map = 0; room = 0;
        if (string.IsNullOrWhiteSpace(WalkToText)) return false;

        // Coord short-circuit — works without a RoomSearchService.
        (int? coordMap, int? coordRoom) = RoomSearchService.TryParseCoordinate(WalkToText);
        if (coordMap is int cm && coordRoom is int cr)
        {
            map = cm; room = cr;
            return true;
        }

        if (_search is null) return false;
        IReadOnlyList<RoomSearchResult> matches =
            _search.Search(WalkToText, source: null, cap: 5, includeAcronyms: false);
        // Want exactly one ROOM match (MonsterTag null). Monster
        // matches are ignored — the editor's WalkTo is for places,
        // not mob lairs.
        List<RoomSearchResult> rooms = matches
            .Where(mm => mm.MonsterTag is null && !mm.IsInformational)
            .ToList();
        if (rooms.Count != 1) return false;
        map = rooms[0].Key.Map;
        room = rooms[0].Key.Room;
        return true;
    }

    private static bool LooksLikeHHmm(string s) =>
        TimeOnly.TryParseExact(s, "HH:mm", out _);
}
