using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Events;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Events" tab — bespoke section that surfaces the per-character
/// scheduled / lifecycle events for editing. Promoted from a stub in
/// Phase 8 PR 8.3.
/// </summary>
/// <remarks>
/// <para>
/// Renders a table of every <see cref="ScheduledEvent"/> on the
/// loaded profile (the events themselves are owned by
/// <see cref="EventManager"/>), plus the master "Disable all events"
/// switch that gates <see cref="EventManager.Fire"/> regardless of
/// per-row state.
/// </para>
/// <para>
/// New / Modify wire the
/// <see cref="ScheduledEvent"/> editor dialog in PR 8.4 — here they
/// either create a minimal placeholder event so the user can see the
/// table populate (New) or surface a "wiring lands in PR 8.4" notice
/// (Modify). Remove + master switch + the auto-disabled badge are
/// fully wired so the engine + reconciler can be smoke-tested through
/// the UI.
/// </para>
/// <para>
/// Persistence model: the events list itself persists through
/// <see cref="EventManager.Add"/> / <c>Replace</c> / <c>Remove</c>
/// immediately on user action — no Apply / Discard cycle. The master
/// "Disable all events" switch is per-character and persists on
/// every toggle. Matches the user expectation "flip the switch and
/// walk away".
/// </para>
/// </remarks>
public sealed partial class EventsSectionViewModel : SettingsSectionViewModel
{
    private readonly EventManager _events;
    private readonly ProfileService _profile;
    private readonly LogService? _log;
    private Control? _view;

    public override string Id => "events";
    public override string Title => "Events";

    /// <summary>Phase tag shown under the tab header.</summary>
    public string PhaseTag => "Phase 8 PR 8.3 (list view + master switch + reconciliation badge) + PR 8.4 (editor dialog)";

    /// <summary>Description shown under the phase tag.</summary>
    public string Description =>
        "User-defined scheduled or lifecycle events — fire on Logon / Logoff / Re-log, at a wall-clock time, " +
        "or on a recurring cadence. Actions walk to a room, start a saved loop or auto-lair setup, or send a " +
        "free-form command (with ^M / ; multi-fire).";

    /// <summary>True when a profile is loaded — editor is hidden otherwise.</summary>
    public bool HasProfile => _profile.Current is not null;

    /// <summary>The table rows. Mirrors <see cref="EventManager.Events"/> with display formatting per row.</summary>
    public ObservableCollection<EventRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ModifyCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private EventRowViewModel? _selectedRow;

    /// <summary>
    /// Two-way bound to <see cref="CharacterProfile.EventsGloballyDisabled"/>.
    /// Persists on every toggle so the user doesn't have to remember to
    /// click an Apply button on the master switch.
    /// </summary>
    public bool IsGloballyDisabled
    {
        get => _profile.Current?.EventsGloballyDisabled ?? false;
        set
        {
            if (_profile.Current is not { } current) return;
            if (current.EventsGloballyDisabled == value) return;
            current.EventsGloballyDisabled = value;
            _profile.Save();
            OnPropertyChanged();
        }
    }

    public override Control View => _view ??= new EventsSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Events", "Scheduled event", "Lifecycle event",
        "Logon", "Logoff", "Re-log", "At time", "Every",
        "Walk to", "Loop", "Auto-lair", "Command",
        "AFK only", "Disable all events", "Target missing",
    };

    public EventsSectionViewModel()
        : this(AppServices.Current.Events, AppServices.Current.Profile, AppServices.Current.Log) { }

    public EventsSectionViewModel(EventManager events, ProfileService profile, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(profile);
        _events = events;
        _profile = profile;
        _log = log;

        _events.Events.CollectionChanged += OnEventsCollectionChanged;
        _events.AutoDisabledChanged += OnAutoDisabledChanged;
        _profile.ProfileLoaded += OnProfileLoaded;
        _profile.ProfileClosed += OnProfileClosed;

        RebuildRows();
    }

    /// <summary>
    /// Open <see cref="EventEditDialogViewModel"/> with a blank event;
    /// on Save (non-null result) the new event is appended via
    /// <see cref="EventManager.Add"/>, which fires CollectionChanged
    /// and selects the new row. Cancel discards.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasProfileForCommand))]
    private async Task NewAsync()
    {
        ScheduledEvent template = new()
        {
            Name = string.Empty,
            TriggerType = EventTriggerType.Logon,
            ActionType = EventActionType.Command,
            CommandText = string.Empty,
        };
        EventEditDialogViewModel dialog = BuildDialogVM(template, isNew: true);
        ScheduledEvent? result = await AppServices.Current.Dialogs
            .OpenWindowAsync<EventEditDialogViewModel, ScheduledEvent?>(dialog);
        if (result is null) return;

        _events.Add(result);
        SelectedRow = Rows.FirstOrDefault(r => ReferenceEquals(r.Source, result));
    }

    /// <summary>
    /// Open <see cref="EventEditDialogViewModel"/> with the selected
    /// row's event as the starting state. On Save (non-null result)
    /// the original is replaced via <see cref="EventManager.Replace"/>.
    /// Cancel discards.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanModifyOrRemove))]
    private async Task ModifyAsync()
    {
        if (SelectedRow?.Source is not { } original) return;
        EventEditDialogViewModel dialog = BuildDialogVM(original, isNew: false);
        ScheduledEvent? edited = await AppServices.Current.Dialogs
            .OpenWindowAsync<EventEditDialogViewModel, ScheduledEvent?>(dialog);
        if (edited is null) return;

        _events.Replace(original, edited);
        SelectedRow = Rows.FirstOrDefault(r => ReferenceEquals(r.Source, edited));
    }

    private EventEditDialogViewModel BuildDialogVM(ScheduledEvent seed, bool isNew) =>
        new(seed, isNew,
            AppServices.Current.Loops,
            AppServices.Current.Lairs,
            AppServices.Current.RoomSearch);

    [RelayCommand(CanExecute = nameof(CanModifyOrRemove))]
    private void Remove()
    {
        if (SelectedRow?.Source is not { } target) return;
        _events.Remove(target);
        SelectedRow = null;
    }

    private bool HasProfileForCommand() => HasProfile;
    private bool CanModifyOrRemove() => SelectedRow is not null;

    // ----- Refresh paths ---------------------------------------------

    private void OnEventsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RebuildRows();

    private void OnAutoDisabledChanged()
    {
        // Re-flag each row's IsAutoDisabled without rebuilding the
        // collection — keeps the user's selection stable.
        foreach (EventRowViewModel row in Rows)
            row.IsAutoDisabled = _events.IsAutoDisabled(row.Source);
    }

    private void OnProfileLoaded(CharacterProfile _)
    {
        RebuildRows();
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(IsGloballyDisabled));
        NewCommand.NotifyCanExecuteChanged();
    }

    private void OnProfileClosed()
    {
        RebuildRows();
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(IsGloballyDisabled));
        NewCommand.NotifyCanExecuteChanged();
    }

    private void RebuildRows()
    {
        ScheduledEvent? keepSelected = SelectedRow?.Source;
        Rows.Clear();
        foreach (ScheduledEvent ev in _events.Events)
            Rows.Add(new EventRowViewModel(ev, _events.IsAutoDisabled(ev)));
        if (keepSelected is not null)
            SelectedRow = Rows.FirstOrDefault(r => ReferenceEquals(r.Source, keepSelected));
    }
}
