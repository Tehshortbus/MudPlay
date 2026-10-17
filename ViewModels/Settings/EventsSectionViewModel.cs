using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Events;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Views.Settings;
using MudPlay.ViewModels.Guides;

namespace MudPlay.ViewModels.Settings;

// "Events" tab — bespoke section that surfaces the per-character scheduled /
// lifecycle events for editing.
//
// Renders a table of every ScheduledEvent on the loaded profile (the events
// themselves are owned by EventManager), plus the master "Disable all events"
// switch that gates EventManager.Fire regardless of per-row state.
//
// Persistence model: the events list itself persists through EventManager.Add /
// Replace / Remove immediately on user action — no Apply / Discard cycle. The
// master "Disable all events" switch is per-character and persists on every
// toggle. Matches the user expectation "flip the switch and walk away".
public sealed partial class EventsSectionViewModel : SettingsSectionViewModel
{
    // How often the "NEXT" countdown column re-renders while the tab is open.
    private static readonly TimeSpan CountdownRefreshInterval = TimeSpan.FromSeconds(1);

    private readonly EventManager _events;
    private readonly ProfileService _profile;
    private readonly EventScheduler? _scheduler;
    private readonly LogService? _log;
    private readonly DispatcherTimer _countdownTicker;
    private Control? _view;

    public override string Id => "events";
    public override string Title => "Events";

    // True when a profile is loaded — editor is hidden otherwise.
    public bool HasProfile => _profile.Current is not null;

    // The table rows. Mirrors EventManager.Events with display formatting per row.
    public ObservableCollection<EventRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ModifyCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private EventRowViewModel? _selectedRow;

    // Two-way bound to CharacterProfile.EventsGloballyDisabled. Persists on every
    // toggle so the user doesn't have to remember to click an Apply button on the
    // master switch.
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

    // How many events may wait behind the running one, and for how many minutes
    // before one is dropped (CharacterProfile.EventQueueLimit /
    // EventQueueWaitMinutes), and the paused-event limit below. Character tier and saved on every change, like the
    // rest of this tab: the events they bound are the character's own.
    public int QueueLimit
    {
        get => Math.Clamp(_profile.Current?.EventQueueLimit ?? EventManager.DefaultMaxQueued,
            1, EventManager.MaxQueuedCeiling);
        set
        {
            if (_profile.Current is not { } current) return;
            int clamped = Math.Clamp(value, 1, EventManager.MaxQueuedCeiling);
            if (QueueLimit == clamped) return;
            current.EventQueueLimit = clamped == EventManager.DefaultMaxQueued ? null : clamped;
            _profile.Save();
            _log?.Info("Events", $"Settings → Events: up to {clamped} event(s) may wait behind the running one.");
            OnPropertyChanged();
        }
    }

    public int QueueWaitMinutes
    {
        get => Math.Clamp(_profile.Current?.EventQueueWaitMinutes ?? EventManager.DefaultMaxQueueWaitMinutes,
            1, EventManager.MaxQueueWaitMinutesCeiling);
        set
        {
            if (_profile.Current is not { } current) return;
            int clamped = Math.Clamp(value, 1, EventManager.MaxQueueWaitMinutesCeiling);
            if (QueueWaitMinutes == clamped) return;
            current.EventQueueWaitMinutes = clamped == EventManager.DefaultMaxQueueWaitMinutes ? null : clamped;
            _profile.Save();
            _log?.Info("Events", $"Settings → Events: a waiting event is dropped after {clamped} minute(s).");
            OnPropertyChanged();
        }
    }

    // CharacterProfile.EventSuspendedIdleMinutes: how long a paused event may stand
    // with nothing moving before it is given up.
    public int SuspendedIdleMinutes
    {
        get => Math.Clamp(_profile.Current?.EventSuspendedIdleMinutes ?? EventManager.DefaultSuspendedIdleMinutes,
            1, EventManager.SuspendedIdleMinutesCeiling);
        set
        {
            if (_profile.Current is not { } current) return;
            int clamped = Math.Clamp(value, 1, EventManager.SuspendedIdleMinutesCeiling);
            if (SuspendedIdleMinutes == clamped) return;
            current.EventSuspendedIdleMinutes = clamped == EventManager.DefaultSuspendedIdleMinutes ? null : clamped;
            _profile.Save();
            _log?.Info("Events", $"Settings → Events: a paused event is given up after {clamped} minute(s) standing still.");
            OnPropertyChanged();
        }
    }

    public override Control View => _view ??= new EventsSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Events", "Scheduled event", "Lifecycle event",
        "Logon", "Logoff", "Re-log", "At time", "Every",
        "Walk to", "Loop", "Auto-lair", "Command",
        "Disable all events", "Target missing",
        "Events waiting at most", "Drop a waiting event after", "Event queue",
        "Give up a paused event after", "Paused event",
    };

    public EventsSectionViewModel()
        : this(AppServices.Current.Events, AppServices.Current.Profile,
               AppServices.Current.EventScheduler, AppServices.Current.Log) { }

    public EventsSectionViewModel(EventManager events, ProfileService profile,
                                  EventScheduler? scheduler = null, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(profile);
        _events = events;
        _profile = profile;
        _scheduler = scheduler;
        _log = log;

        _events.Events.CollectionChanged += OnEventsCollectionChanged;
        _events.AutoDisabledChanged += OnAutoDisabledChanged;
        _profile.ProfileLoaded += OnProfileLoaded;
        _profile.ProfileClosed += OnProfileClosed;

        // Live countdown for the "NEXT" column. Only the scheduler knows each
        // running Every-timer's due-time, so the row can't self-refresh — the
        // ticker pulls fresh due-times once a second while the tab is open.
        _countdownTicker = new DispatcherTimer { Interval = CountdownRefreshInterval };
        _countdownTicker.Tick += OnCountdownTick;
        _countdownTicker.Start();

        OnDispose(() =>
        {
            _events.Events.CollectionChanged -= OnEventsCollectionChanged;
            _events.AutoDisabledChanged -= OnAutoDisabledChanged;
            _profile.ProfileLoaded -= OnProfileLoaded;
            _profile.ProfileClosed -= OnProfileClosed;
            _countdownTicker.Tick -= OnCountdownTick;
            _countdownTicker.Stop();
        });

        RebuildRows();
    }

    // Open EventEditDialogViewModel with a blank event; on Save (non-null result)
    // the new event is appended via EventManager.Add, which fires CollectionChanged
    // and selects the new row. Cancel discards.
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
        GuideActions.Report(GuideActions.EventAdded);
        SelectedRow = Rows.FirstOrDefault(r => ReferenceEquals(r.Source, result));
    }

    // Open EventEditDialogViewModel with the selected row's event as the starting
    // state. On Save (non-null result) the original is replaced via
    // EventManager.Replace. Cancel discards. A remote @event that switches the
    // event while the window is open moves the window's Disabled box with it.
    [RelayCommand(CanExecute = nameof(CanModifyOrRemove))]
    private async Task ModifyAsync()
    {
        if (SelectedRow?.Source is not { } original) return;
        EventEditDialogViewModel dialog = BuildDialogVM(original, isNew: false);
        Action stopFollowing = dialog.FollowSwitches(_events.Events, original);
        ScheduledEvent? edited;
        try
        {
            edited = await AppServices.Current.Dialogs
                .OpenWindowAsync<EventEditDialogViewModel, ScheduledEvent?>(dialog);
        }
        finally
        {
            stopFollowing();
        }
        if (edited is null) return;

        _events.Replace(original, edited);
        SelectedRow = Rows.FirstOrDefault(r => ReferenceEquals(r.Source, edited));
    }

    private EventEditDialogViewModel BuildDialogVM(ScheduledEvent seed, bool isNew) =>
        new(seed, isNew,
            AppServices.Current.Loops,
            AppServices.Current.Lairs,
            AppServices.Current.RoomSearch,
            bossNames: AppServices.Current.Bosses.ResolveForRealm(AppServices.Current.GameData.ActiveRealm)
                .Select(b => b.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            eventNames: _events.Events.Select(e => e.Name).Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            bossWindows: BossWindowColumns(AppServices.Current.GameData.ActiveRealm),
            stashRooms: AppServices.Current.Movement.Stash
                .OrderBy(k => k.Map).ThenBy(k => k.Room)
                .Select(k => ($"{k.Map}/{k.Room} {AppServices.Current.RoomGraph.GetRoom(k)?.Name}".TrimEnd(),
                    new Models.Profile.RoomRef(k.Map, k.Room)))
                .ToList(),
            banks: Game.GameData.BankCatalog.Enumerate(AppServices.Current.GameData)
                .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Map).ThenBy(b => b.Room)
                .Select(b => ($"{b.Name} {b.Map}/{b.Room}", new Models.Profile.RoomRef(b.Map, b.Room)))
                .ToList(),
            // Asked live, so a Stop before entering box ticked on the Bosses tab while
            // this window is open shows up as the room is typed.
            stopBeforeBoss: room => AppServices.Current.BossStopRooms().Contains(room)
                ? AppServices.Current.BossInRoom(room) ?? "A boss"
                : null);

    // The Bosses tab's early-window columns for the realm, in the tab's order.
    private static IReadOnlyList<(string, double)> BossWindowColumns(Game.RealmType realm)
        => Game.Map.BossTimerMath.EarlyColumnLabels(realm)
            .Zip(Game.Map.BossTimerMath.EarlyFractionsInDisplayOrder(realm), (label, f) => (label, f))
            .ToList();

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

    private void OnCountdownTick(object? sender, EventArgs e) => RefreshNextCall();

    // Pull each row's next-fire due-time from the scheduler and reformat its
    // countdown text. Null scheduler (no engine) leaves every row at "—".
    private void RefreshNextCall()
    {
        foreach (EventRowViewModel row in Rows)
            row.UpdateNextCall(_scheduler?.GetNextFire(row.Source));
    }

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
        OnPropertyChanged(nameof(QueueLimit));
        OnPropertyChanged(nameof(QueueWaitMinutes));
        NewCommand.NotifyCanExecuteChanged();
    }

    private void OnProfileClosed()
    {
        RebuildRows();
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(IsGloballyDisabled));
        OnPropertyChanged(nameof(QueueLimit));
        OnPropertyChanged(nameof(QueueWaitMinutes));
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
        // Populate the countdown immediately so freshly added rows aren't blank
        // until the next ticker pass.
        RefreshNextCall();
    }
}
