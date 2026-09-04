using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One boss row in the Bosses tab. StopBefore is the only inline-editable field
// (a quick toggle); name / rooms / flags are edited in the Manage Bosses dialog.
// The live timer columns — the 100% countdown (StatusDisplay) and the per-window
// early countdowns (Early1/2/3) — are recomputed on the heartbeat: each early
// column counts down to its spawn window and blanks once that window has passed.
// Every timer column also exposes a numeric sort key so the DataGrid can sort by
// remaining time (blank / expired rows sort last).
public sealed partial class BossRowViewModel : ObservableObject
{
    private const long InactiveSort = long.MaxValue;

    private readonly BossTimerStore _timers;
    private readonly Action _onEdit;
    private readonly Action<BossRowViewModel> _onMarkRequested;
    // The resolved def this row came from. Only StopBefore is inline-editable; every
    // other field (rooms, override, Notes, ShowInTable, flags) is carried through
    // unchanged so a StopBefore toggle can't clobber a Manage-dialog edit.
    private readonly BossDef _def;
    private RealmType _realm;
    private int? _respawnHours;
    private bool _suppress;

    // Displayed name (read-only in the grid). Rooms / Notes are held for the filter
    // and tooltip; the rest of the def is edited only in the Manage dialog.
    [ObservableProperty] private string _name = string.Empty;
    public string Rooms { get; set; } = string.Empty;   // "map/room; map/room" — held for the filter
    public string Notes { get; set; } = string.Empty;   // free-text nuance, edited in the Manage dialog; shown read-only on the tab

    [ObservableProperty] private bool _stopBefore;

    // Blind-grab-on-kill flag (inline-editable, like StopBefore): when set, the moment
    // this boss dies the client fires `get <item>` for its whole drop table (a monster
    // boss), or `get <item>` on room entry (an item boss).
    [ObservableProperty] private bool _grabAll;

    // Whether Grab-All can apply to this boss at all — its name resolves to a specific
    // monster or item. False for an unresolvable name (a touch-to-awaken mechanic like
    // Iceforge); the tab hides the checkbox and shows a "cannot resolve" tooltip.
    public bool CanGrabAll { get; }

    // Static respawn length ("10h" / "Cleanup" / "?") + its sort key (hours).
    [ObservableProperty] private string _respawnDisplay = string.Empty;
    [ObservableProperty] private int _respawnSortKey = int.MaxValue;

    // Live 100% countdown + sort key (seconds remaining; InactiveSort when idle).
    [ObservableProperty] private string _statusDisplay = string.Empty;
    [ObservableProperty] private long _fullSortKey = InactiveSort;

    // Live early-window countdowns (display order 5% / 10% / 20% on Paradigm; the
    // single 87.5% on Stock in slot 1) + sort keys.
    [ObservableProperty] private string _early1Display = string.Empty;
    [ObservableProperty] private string _early2Display = string.Empty;
    [ObservableProperty] private string _early3Display = string.Empty;
    [ObservableProperty] private long _early1SortKey = InactiveSort;
    [ObservableProperty] private long _early2SortKey = InactiveSort;
    [ObservableProperty] private long _early3SortKey = InactiveSort;

    [ObservableProperty] private bool _isCleanup;
    [ObservableProperty] private bool _isActive;

    // Last time this boss's timer was set — by a Mark / Now button, a back-dated Mark,
    // or an auto-detected kill (all land on BossTimerStore.KilledAt). Shown in local
    // time; blank when never recorded. Sort key is the kill time in Unix seconds, with
    // long.MinValue for "never" so those rows sort last regardless of direction.
    [ObservableProperty] private string _lastKilledDisplay = string.Empty;
    [ObservableProperty] private long _lastKilledSortKey = long.MinValue;

    public BossRespawnType RespawnType { get; }

    public BossRowViewModel(
        BossDef def, RealmType realm, int? respawnHours, BossTimerStore timers,
        Action onEdit, Action<BossRowViewModel> onMarkRequested, bool canGrabAll = true)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(onEdit);
        ArgumentNullException.ThrowIfNull(onMarkRequested);
        _timers = timers;
        _onEdit = onEdit;
        _onMarkRequested = onMarkRequested;
        _def = def;
        CanGrabAll = canGrabAll;
        _suppress = true;
        Name = def.Name;
        Rooms = BossRoomText.Format(def.Rooms);
        Notes = def.Notes;
        StopBefore = def.StopBefore;
        GrabAll = def.GrabAll;
        RespawnType = def.RespawnType;
        RefreshDisplay(realm, respawnHours);
        _suppress = false;
    }

    // Recompute the static respawn column for the realm + game-data timer, then the
    // live columns.
    public void RefreshDisplay(RealmType realm, int? respawnHours)
    {
        _realm = realm;
        _respawnHours = respawnHours;
        IsCleanup = RespawnType == BossRespawnType.Cleanup;
        if (IsCleanup) { RespawnDisplay = "Cleanup"; RespawnSortKey = int.MaxValue; }
        else if (respawnHours is { } full && full > 0) { RespawnDisplay = $"{full}h"; RespawnSortKey = full; }
        else { RespawnDisplay = "?"; RespawnSortKey = int.MaxValue; }
        RefreshStatus();
    }

    // Recompute the live 100% + early-window countdowns from the kill time. All
    // blank when no timer is running (never killed / expired / Cleanup / no timer).
    public void RefreshStatus()
    {
        // Last-killed stamp is independent of the live countdown — it's meaningful even
        // for an expired / cleanup / idle row, so compute it before the state branches.
        RefreshLastKilled();

        // Cleanup bosses read a DEAD / ALIVE state, not a countdown: DEAD once marked
        // until the next BBS cleanup flips them back to ALIVE. No early windows.
        if (IsCleanup)
        {
            bool dead = _timers.IsCleanupDead(Name);
            IsActive = dead;
            StatusDisplay = dead ? "DEAD" : "ALIVE";
            FullSortKey = dead ? (long)(_timers.CleanupRemaining(Name)?.TotalSeconds ?? 0) : InactiveSort;
            Early1Display = Early2Display = Early3Display = string.Empty;
            Early1SortKey = Early2SortKey = Early3SortKey = InactiveSort;
            return;
        }

        DateTimeOffset? killed = _timers.KilledAt(Name);
        if (killed is not { } k || _respawnHours is not { } full || full <= 0)
        {
            ClearLive();
            return;
        }
        double fullSecs = full * 3600.0;
        double elapsed = (DateTimeOffset.UtcNow - k).TotalSeconds;
        if (elapsed >= fullSecs) { ClearLive(); return; }   // expired → not tracking

        IsActive = true;
        double fullRem = fullSecs - elapsed;
        int pct = (int)(elapsed / fullSecs * 100.0);   // how much of the timer has elapsed
        StatusDisplay = $"{BossTimerMath.FormatDuration(TimeSpan.FromSeconds(fullRem))} ({pct}%)";
        FullSortKey = (long)fullRem;

        IReadOnlyList<double> fracs = BossTimerMath.EarlyFractionsInDisplayOrder(_realm);
        (Early1Display, Early1SortKey) = Window(fracs, 0, elapsed, fullSecs);
        (Early2Display, Early2SortKey) = Window(fracs, 1, elapsed, fullSecs);
        (Early3Display, Early3SortKey) = Window(fracs, 2, elapsed, fullSecs);
    }

    // Countdown to the i-th early window, or blank once it has passed (or there's no
    // such window for this realm). Blank sorts last via InactiveSort.
    private static (string display, long sort) Window(IReadOnlyList<double> fracs, int i, double elapsed, double fullSecs)
    {
        if (i >= fracs.Count) return (string.Empty, InactiveSort);
        double rem = fracs[i] * fullSecs - elapsed;
        return rem > 0
            ? (BossTimerMath.FormatDuration(TimeSpan.FromSeconds(rem)), (long)rem)
            : (string.Empty, InactiveSort);
    }

    // Reflect BossTimerStore.KilledAt into the Last Killed column (local time + a
    // Unix-seconds sort key). Blank / long.MinValue when the boss has no recorded kill.
    private void RefreshLastKilled()
    {
        if (_timers.KilledAt(Name) is { } at)
        {
            LastKilledDisplay = at.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.CurrentCulture);
            LastKilledSortKey = at.ToUnixTimeSeconds();
        }
        else
        {
            LastKilledDisplay = string.Empty;
            LastKilledSortKey = long.MinValue;
        }
    }

    private void ClearLive()
    {
        IsActive = false;
        StatusDisplay = string.Empty; FullSortKey = InactiveSort;
        Early1Display = Early2Display = Early3Display = string.Empty;
        Early1SortKey = Early2SortKey = Early3SortKey = InactiveSort;
    }

    // Clone the source def with just the inline-edited fields (StopBefore, GrabAll)
    // applied — every other field (rooms, override, Notes, ShowInTable, flags)
    // round-trips untouched, so a toggle can't clobber a Manage-dialog edit.
    public BossDef ToDef()
    {
        BossDef d = _def.Clone();
        d.StopBefore = StopBefore;
        d.GrabAll = GrabAll;
        return d;
    }

    // Mark opens the date-time dialog (set / back-date). Reset stamps the kill at
    // the current time (quick "it just died"). Clear removes the running timer.
    [RelayCommand]
    private void Mark() => _onMarkRequested(this);

    [RelayCommand]
    private void ResetTimer() { _timers.MarkKilled(Name.Trim().ToLowerInvariant(), DateTimeOffset.Now); RefreshStatus(); }

    [RelayCommand]
    private void ClearTimer() { _timers.Reset(Name.Trim().ToLowerInvariant()); RefreshStatus(); }

    partial void OnStopBeforeChanged(bool value) { if (!_suppress) _onEdit(); }
    partial void OnGrabAllChanged(bool value) { if (!_suppress) _onEdit(); }
}
