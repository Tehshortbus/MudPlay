using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia.Threading;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Events;

/// <summary>
/// Phase 8 PR 8.2 — connection-aware lifecycle + timers that decide
/// when each <see cref="ScheduledEvent"/> fires. <see cref="EventManager"/>
/// owns storage + dispatch; this class owns the trigger sources.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle (per spec):
/// </para>
/// <list type="bullet">
///   <item><b>Logon</b> fires on every game entry — the first
///   <see cref="WirePromptScanner.PromptObserved"/> after
///   <see cref="NotifyConnected"/>. Connect, Login automation,
///   menu-entry automation all happen before this point; the prompt
///   is the canonical "we're in MajorMUD" signal.</item>
///   <item><b>Re-log</b> fires alongside Logon, but only when the
///   game entry is a reconnect (a prior in-session disconnect
///   happened). Never on the first connect of a profile session. The
///   <see cref="_hadInSessionDisconnect"/> latch tracks this and
///   resets on profile load.</item>
///   <item><b>Logoff</b> is NOT fired here — it's the caller's
///   responsibility. <see cref="EventManager.FireLogoffEvents"/> gets
///   called from the user-initiated disconnect path BEFORE the wire
///   closes. Dropped connections skip it entirely.</item>
///   <item><b>AtTime</b> events check on a 30 s ticker; an event
///   matching the current HH:mm wall-clock local fires (deduped via
///   <see cref="_atTimeFiredAt"/> so a single matched minute fires
///   each event at most once). Only fires while in-game.</item>
///   <item><b>Every</b> events get a dedicated
///   <see cref="DispatcherTimer"/> per event. Timers start fresh at
///   each first-prompt-after-connect (per spec — no anchor
///   preservation across disconnect). Stop on disconnect.</item>
/// </list>
/// <para>
/// TelnetClient lifecycle plumbing: the client is per-connection
/// (owned by MainWindowViewModel) so this scheduler doesn't subscribe
/// directly. Instead MainWindowVM calls
/// <see cref="NotifyConnected"/> / <see cref="NotifyDisconnected"/>
/// when its <see cref="TelnetClient"/> raises the corresponding
/// events. <see cref="WirePromptScanner"/> IS a stable singleton on
/// <see cref="AppServices"/>, so the prompt signal wires directly.
/// </para>
/// <para>
/// Threading: <see cref="DispatcherTimer"/> already callbacks on the
/// UI dispatcher. The notify methods are called from MainWindowVM
/// which is on the UI thread. All <see cref="EventManager.Fire"/>
/// calls land on the UI thread.
/// </para>
/// </remarks>
public sealed class EventScheduler : IDisposable
{
    /// <summary>How often the AtTime ticker re-evaluates the wall clock.</summary>
    private static readonly TimeSpan AtTimeTickInterval = TimeSpan.FromSeconds(30);

    private readonly EventManager _events;
    private readonly WirePromptScanner _prompt;
    private readonly CleanupWarningWatcher? _cleanup;
    private readonly ProfileService? _profile;
    private readonly LogService? _log;

    private readonly DispatcherTimer _atTimeTicker;
    private readonly Dictionary<ScheduledEvent, DispatcherTimer> _everyTimers = new();
    /// <summary>(event → minute string we last fired at) so AtTime doesn't double-fire within the same minute.</summary>
    private readonly Dictionary<ScheduledEvent, string> _atTimeFiredAt = new();

    private bool _isInGame;
    private bool _hadInSessionDisconnect;
    /// <summary>
    /// Latched true after the first cleanup-warning observation fires
    /// Logoff events. The BBS warns repeatedly during a cleanup cycle
    /// ("5 min", "4 min", "3 min", …); without this latch every
    /// Logoff action would fire on every warning. Reset on the next
    /// fresh TCP connect so a future cleanup gets a clean shot.
    /// </summary>
    private bool _logoffFiredThisSession;
    private bool _disposed;

    public EventScheduler(
        EventManager events,
        WirePromptScanner prompt,
        CleanupWarningWatcher? cleanup = null,
        ProfileService? profile = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(prompt);
        _events = events;
        _prompt = prompt;
        _cleanup = cleanup;
        _profile = profile;
        _log = log;

        _atTimeTicker = new DispatcherTimer { Interval = AtTimeTickInterval };
        _atTimeTicker.Tick += OnAtTimeTick;

        prompt.PromptObserved += OnPromptObserved;
        if (cleanup is not null)
        {
            // Logoff events fire ONCE per cleanup-warning observation —
            // when the BBS announces upcoming shutdown. That's the only
            // window in which Logoff actions (bank cash, stash gear,
            // etc.) actually reach the game; the disconnect button is
            // too late.
            cleanup.WarningObserved += OnCleanupWarningObserved;
        }
        if (profile is not null)
        {
            profile.ProfileLoaded += OnProfileLoaded;
            profile.ProfileClosed += OnProfileClosed;
        }
        events.Events.CollectionChanged += OnEventsCollectionChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _atTimeTicker.Tick -= OnAtTimeTick;
        _atTimeTicker.Stop();
        StopAllEveryTimers();

        _prompt.PromptObserved -= OnPromptObserved;
        if (_cleanup is not null) _cleanup.WarningObserved -= OnCleanupWarningObserved;
        if (_profile is not null)
        {
            _profile.ProfileLoaded -= OnProfileLoaded;
            _profile.ProfileClosed -= OnProfileClosed;
        }
        _events.Events.CollectionChanged -= OnEventsCollectionChanged;
    }

    // ----- Telnet-driven notifications -------------------------------

    /// <summary>
    /// Called by MainWindowVM right after its
    /// <see cref="TelnetClient.Connected"/> handler runs. Resets the
    /// "are we in-game?" latch — the first prompt observation will
    /// flip it to true and fire Logon (+ optionally Re-log) events.
    /// Also resets the once-per-session Logoff-fire latch so a future
    /// cleanup-warning cycle can dispatch Logoff events again.
    /// </summary>
    public void NotifyConnected()
    {
        _isInGame = false;
        _logoffFiredThisSession = false;
    }

    /// <summary>
    /// Called by MainWindowVM when its
    /// <see cref="TelnetClient.Disconnected"/> handler runs. Latches
    /// the Re-log flag (so the next reconnect's Logon also fires
    /// Re-log) and stops all timers.
    /// </summary>
    public void NotifyDisconnected()
    {
        // Only set the Re-log latch when we actually made it in-game
        // for THIS session — a failed connect that never reached the
        // prompt shouldn't promote the next connect to a "reconnect".
        if (_isInGame) _hadInSessionDisconnect = true;
        _isInGame = false;
        _atTimeTicker.Stop();
        StopAllEveryTimers();
    }

    /// <summary>
    /// BBS announced upcoming shutdown — fire user-configured Logoff
    /// events ONCE per session so cleanup commands (bank, store gear,
    /// etc.) hit the wire while we still have a connection. The
    /// watcher fires per warning-line observation and the BBS warns
    /// repeatedly during a cleanup cycle (5 / 4 / 3 / 2 / 1 min); the
    /// <see cref="_logoffFiredThisSession"/> latch prevents the same
    /// Logoff event from running 5+ times.
    /// </summary>
    private void OnCleanupWarningObserved(Services.CleanupWarning _)
    {
        if (!_isInGame) return;            // not in-game means no commands would land anyway.
        if (_logoffFiredThisSession) return; // already fired this session — wait for reconnect.
        int fired = _events.FireLogoffEvents();
        _logoffFiredThisSession = true;    // latch even on fired==0 so we don't re-evaluate per warning.
        if (fired > 0)
            _log?.Info("Events", $"Cleanup warning observed — fired {fired} Logoff event(s).");
    }

    private void OnPromptObserved(PromptObservation _)
    {
        if (_isInGame) return;  // only the FIRST prompt this session counts.
        _isInGame = true;

        FireByTriggerType(EventTriggerType.Logon);
        if (_hadInSessionDisconnect) FireByTriggerType(EventTriggerType.Relog);

        // Start AtTime ticker + Every-timers from scratch.
        _atTimeFiredAt.Clear();
        _atTimeTicker.Start();
        RebuildEveryTimers();
    }

    // ----- Profile lifecycle -----------------------------------------

    private void OnProfileLoaded(Models.Profile.CharacterProfile _)
    {
        // New profile session — clear Re-log latch + rebuild timers
        // if we happen to already be in-game.
        _hadInSessionDisconnect = false;
        if (_isInGame)
        {
            _atTimeFiredAt.Clear();
            RebuildEveryTimers();
        }
    }

    private void OnProfileClosed()
    {
        _hadInSessionDisconnect = false;
        StopAllEveryTimers();
        _atTimeTicker.Stop();
        _atTimeFiredAt.Clear();
    }

    // ----- AtTime ticker ----------------------------------------------

    private void OnAtTimeTick(object? sender, EventArgs e)
    {
        if (!_isInGame) return;
        // Truncate to HH:mm so a 30 s tick at 09:30:15 and 09:30:45 both
        // resolve to "09:30" and the dedup check below catches the
        // double-fire.
        string nowKey = DateTime.Now.ToString("HH:mm");

        foreach (ScheduledEvent ev in _events.Events)
        {
            if (ev.Disabled) continue;
            if (ev.TriggerType != EventTriggerType.AtTime) continue;
            if (ev.AtTime != nowKey) continue;
            if (_atTimeFiredAt.TryGetValue(ev, out string? lastKey) && lastKey == nowKey) continue;

            _atTimeFiredAt[ev] = nowKey;
            _events.Fire(ev);
        }
    }

    // ----- Every-timer management ------------------------------------

    private void OnEventsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Add / Remove / Replace all warrant a full rebuild. The list
        // is small (handfuls of events at most) so cost is trivial vs
        // tracking deltas precisely.
        if (_isInGame) RebuildEveryTimers();
    }

    private void RebuildEveryTimers()
    {
        StopAllEveryTimers();
        foreach (ScheduledEvent ev in _events.Events)
        {
            if (ev.Disabled) continue;
            if (ev.TriggerType != EventTriggerType.Every) continue;
            if (ev.TryParseEvery() is not { } interval) continue;

            DispatcherTimer t = new() { Interval = interval };
            // Capture by value — the timer outlives this loop iteration.
            ScheduledEvent captured = ev;
            t.Tick += (_, _) => _events.Fire(captured);
            _everyTimers[ev] = t;
            t.Start();
        }
    }

    private void StopAllEveryTimers()
    {
        foreach (DispatcherTimer t in _everyTimers.Values) t.Stop();
        _everyTimers.Clear();
    }

    // ----- Helpers ----------------------------------------------------

    private void FireByTriggerType(EventTriggerType type)
    {
        // Snapshot before iterating in case an action mutates the list.
        List<ScheduledEvent> snapshot = new();
        foreach (ScheduledEvent ev in _events.Events)
            if (ev.TriggerType == type && !ev.Disabled) snapshot.Add(ev);
        foreach (ScheduledEvent ev in snapshot) _events.Fire(ev);
    }
}
