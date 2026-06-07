using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Events;

/// <summary>
/// In-memory store + dispatcher for the loaded character's
/// <see cref="ScheduledEvent"/> entries. Owns the merge / save path
/// against <see cref="CharacterProfile.Events"/>, exposes CRUD for the
/// settings editor, reconciles saved-target references against
/// <see cref="LoopManager"/> + <see cref="LairManager"/>, and dispatches
/// fired actions into the existing movement / command stack.
/// </summary>
/// <remarks>
/// <para>
/// PR 8.1 scope: the dispatcher is end-to-end executable —
/// <see cref="Fire"/> can be called and the four action types route
/// correctly into <see cref="AutoWalkManager"/>, <see cref="LoopRunner"/>,
/// <see cref="AutoLairManager"/>, and the bound wire sender. The
/// triggers themselves (At time / Every / Logon / Logoff / Re-log) are
/// wired in PR 8.2 by subscribing the appropriate sources to call
/// <see cref="Fire"/>.
/// </para>
/// <para>
/// Saved-target reconciliation: subscribes to
/// <see cref="LoopManager.LoopsChanged"/> +
/// <see cref="LairManager.SetupsChanged"/>. On either, walks every
/// event whose <see cref="ScheduledEvent.ActionType"/> is
/// <see cref="EventActionType.Loop"/> or
/// <see cref="EventActionType.AutoLair"/> and auto-disables any whose
/// referenced name is no longer in the corresponding manager's
/// collection. Auto-disable is sticky: re-creating a same-named
/// target later doesn't auto-restore — the user re-enables manually
/// after confirming the new target matches their intent.
/// </para>
/// <para>
/// Threading: profile lifecycle + manager-change events fire on the UI
/// thread (the producers marshal upstream). <see cref="Fire"/> is
/// called from the PR 8.2 trigger sources on the UI thread too, so no
/// internal locking is needed on the events list.
/// </para>
/// </remarks>
public sealed class EventManager
{
    private readonly ProfileService? _profile;
    private readonly LoopManager? _loops;
    private readonly LairManager? _lairs;
    private readonly LoopRunner? _loopRunner;
    private readonly AutoLairManager? _autoLair;
    private readonly AutoWalkManager? _walker;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    /// <summary>
    /// Tracks which events the reconciler auto-disabled (vs the user
    /// manually flipping Disabled). The Settings.Events row badge
    /// (PR 8.3) renders "↻ target missing" only for keys in this set.
    /// Cleared on profile load.
    /// </summary>
    private readonly HashSet<ScheduledEvent> _autoDisabled = new();

    /// <summary>The loaded character's events — empty when no profile is active.</summary>
    public ObservableCollection<ScheduledEvent> Events { get; } = new();

    /// <summary>
    /// Returns true when the reconciler — not the user — flipped this
    /// event's <see cref="ScheduledEvent.Disabled"/> flag on. Drives
    /// the PR 8.3 row badge.
    /// </summary>
    public bool IsAutoDisabled(ScheduledEvent e) => _autoDisabled.Contains(e);

    /// <summary>
    /// Fires after the reconciler auto-disables one or more events.
    /// PR 8.3 listens for row-badge refresh; here so the engine
    /// surface stays observable.
    /// </summary>
    public event Action? AutoDisabledChanged;

    public EventManager(
        ProfileService profile,
        LoopManager loops,
        LairManager lairs,
        LoopRunner loopRunner,
        AutoLairManager autoLair,
        AutoWalkManager walker,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(lairs);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(autoLair);
        ArgumentNullException.ThrowIfNull(walker);
        _profile = profile;
        _loops = loops;
        _lairs = lairs;
        _loopRunner = loopRunner;
        _autoLair = autoLair;
        _walker = walker;
        _log = log;

        profile.ProfileLoaded += LoadFrom;
        profile.ProfileClosed += Clear;
        profile.ProfileSaving += SnapshotForSave;
        loops.LoopsChanged += ReconcileTargets;
        lairs.SetupsChanged += ReconcileTargets;
        if (profile.Current is { } current) LoadFrom(current);
    }

    /// <summary>Parameterless ctor for tests / in-memory scenarios — no profile, no engines.</summary>
    public EventManager() { }

    /// <summary>
    /// Bind the wire sender for <see cref="EventActionType.Command"/>
    /// dispatch. Same shape as the other automation engines — the main
    /// window VM supplies the gate-wrapped <c>SendUserInput</c>.
    /// </summary>
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    /// <summary>Test seam — bytes the engine asked to write to the wire.</summary>
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    // ----- CRUD ------------------------------------------------------

    /// <summary>Append a new event and persist. No duplicate check.</summary>
    public void Add(ScheduledEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Events.Add(e);
        ReconcileTargets();
        _profile?.Save();
    }

    /// <summary>
    /// Replace an existing event by reference. Persists. No-op when
    /// <paramref name="original"/> isn't in the list.
    /// </summary>
    public bool Replace(ScheduledEvent original, ScheduledEvent updated)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(updated);
        int idx = Events.IndexOf(original);
        if (idx < 0) return false;
        Events[idx] = updated;
        _autoDisabled.Remove(original);
        ReconcileTargets();
        _profile?.Save();
        return true;
    }

    /// <summary>Remove an event by reference. Persists. No-op when not in the list.</summary>
    public bool Remove(ScheduledEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!Events.Remove(e)) return false;
        _autoDisabled.Remove(e);
        _profile?.Save();
        return true;
    }

    // ----- Dispatch --------------------------------------------------

    /// <summary>
    /// Execute the event's action. Skips when
    /// <see cref="ScheduledEvent.Disabled"/> is true OR when the
    /// fire-time safety net detects a missing saved target (Loop /
    /// AutoLair name no longer in the manager's collection). The
    /// safety net mirrors what <see cref="ReconcileTargets"/> does on
    /// LoopsChanged / SetupsChanged — defense in depth for races and
    /// direct-disk profile edits.
    /// </summary>
    public void Fire(ScheduledEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Disabled) return;
        // Master switch — Settings → Events "Disable all events" gate.
        // Per-character; checked here so the scheduler doesn't have to
        // worry about it and tests using the parameterless ctor (no
        // profile) keep firing.
        if (_profile?.Current?.EventsGloballyDisabled == true) return;

        switch (e.ActionType)
        {
            case EventActionType.WalkTo: ExecuteWalkTo(e); break;
            case EventActionType.Loop: ExecuteLoop(e); break;
            case EventActionType.AutoLair: ExecuteAutoLair(e); break;
            case EventActionType.Command: ExecuteCommand(e); break;
        }
    }

    /// <summary>
    /// Synchronously fires every <see cref="EventTriggerType.Logoff"/>
    /// event in the list. Called from the user-initiated disconnect
    /// path BEFORE the wire closes (and BEFORE the dropped-connection
    /// auto-reconnect kicks in for dropped paths — those skip Logoff
    /// entirely because nobody calls this method on a server-side
    /// drop). Caller is responsible for the bounded flush window
    /// between this call and the actual <c>DisposeAsync</c> so the
    /// wire writes have time to drain. Returns the number of events
    /// actually dispatched so the caller can skip the flush wait
    /// when nothing fired.
    /// </summary>
    public int FireLogoffEvents()
    {
        // Snapshot to a list so an event action that adds / removes
        // events mid-fire doesn't invalidate the iterator. Fairly
        // contrived but cheap insurance.
        List<ScheduledEvent> snapshot = Events
            .Where(e => e.TriggerType == EventTriggerType.Logoff && !e.Disabled)
            .ToList();
        foreach (ScheduledEvent e in snapshot) Fire(e);
        return snapshot.Count;
    }

    private void ExecuteWalkTo(ScheduledEvent e)
    {
        if (_walker is null) return;
        if (e.WalkToTarget is not { } target)
        {
            _log?.Warn("Events", $"Event '{Label(e)}' has no walk-to target; skipping.");
            return;
        }
        // Supersede any running engine so the walk owns the wire.
        if (_loopRunner?.State is not LoopState.Idle) _loopRunner?.Stop("event supersede");
        if (_autoLair?.IsActive == true) _autoLair?.Stop("event supersede");
        RoomKey key = new(target.Map, target.Room);
        if (!_walker.WalkTo(key))
            _log?.Warn("Events", $"Event '{Label(e)}' walk-to {key.Map}/{key.Room} failed.");
    }

    private void ExecuteLoop(ScheduledEvent e)
    {
        if (_loopRunner is null || _loops is null) return;
        if (string.IsNullOrWhiteSpace(e.LoopName)) return;
        Loop? saved = FindLoop(e.LoopName);
        if (saved is null)
        {
            AutoDisable(e, $"referenced loop '{e.LoopName}' was deleted or renamed");
            return;
        }
        // Supersede other engines.
        if (_autoLair?.IsActive == true) _autoLair?.Stop("event supersede");
        if (_walker?.State is not WalkState.Idle) _walker?.Stop("event supersede");
        if (!_loopRunner.Start(saved))
            _log?.Warn("Events", $"Event '{Label(e)}' loop '{saved.Name}' failed to start.");
    }

    private void ExecuteAutoLair(ScheduledEvent e)
    {
        if (_autoLair is null || _lairs is null) return;
        if (string.IsNullOrWhiteSpace(e.AutoLairSetupName)) return;
        LairSetup? setup = FindSetup(e.AutoLairSetupName);
        if (setup is null)
        {
            AutoDisable(e, $"referenced auto-lair setup '{e.AutoLairSetupName}' was deleted or renamed");
            return;
        }
        if (_loopRunner?.State is not LoopState.Idle) _loopRunner?.Stop("event supersede");
        if (_walker?.State is not WalkState.Idle) _walker?.Stop("event supersede");

        _autoLair.Clear();
        foreach (LairMarker m in setup.Markers)
            _autoLair.Mark(new RoomKey(m.Map, m.Room), m.OverrideRespawnSeconds);
        if (!_autoLair.Start())
            _log?.Warn("Events", $"Event '{Label(e)}' auto-lair '{setup.Name}' failed to start.");
    }

    private void ExecuteCommand(ScheduledEvent e)
    {
        // Empty CommandText is a valid Command action — fires a bare
        // CR. Useful for MOTD pagination + similar single-Enter
        // prompts. WireSender.Send("") encodes to just "\r".
        if (string.IsNullOrEmpty(e.CommandText))
        {
            _wire.Send(string.Empty);
            return;
        }
        foreach (string chunk in SplitCommand(e.CommandText))
        {
            if (chunk.Length == 0) continue;
            _wire.Send(chunk);
        }
    }

    /// <summary>
    /// Split a Command action's text on <c>^M</c> AND <c>;</c>
    /// boundaries — both characters denote a CR break (matching the
    /// existing Macro / Trigger / Alias multi-step splitter). An
    /// empty chunk between two consecutive separators is dropped so
    /// <c>"look;;sit"</c> sends two lines rather than three.
    /// </summary>
    /// <remarks>
    /// Internal so tests can assert the splitter contract without
    /// firing a real event end-to-end.
    /// </remarks>
    internal static IEnumerable<string> SplitCommand(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        // ^M is the literal two-character sequence ^ + M, not the
        // control character. Replace first so the subsequent split on
        // ';' handles both consistently.
        string normalised = text.Replace("^M", ";", StringComparison.Ordinal);
        foreach (string part in normalised.Split(';', StringSplitOptions.None))
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0) yield return trimmed;
        }
    }

    // ----- Reconciliation -------------------------------------------

    /// <summary>
    /// Walk every Loop / AutoLair-action event and auto-disable any
    /// whose referenced saved name is no longer in the manager's
    /// collection. Idempotent — already-disabled events are left
    /// alone. Re-runs on profile load + every LoopsChanged /
    /// SetupsChanged event.
    /// </summary>
    private void ReconcileTargets()
    {
        bool anyChanged = false;
        foreach (ScheduledEvent e in Events)
        {
            if (e.Disabled) continue;
            switch (e.ActionType)
            {
                case EventActionType.Loop:
                    if (FindLoop(e.LoopName) is null && !string.IsNullOrEmpty(e.LoopName))
                    {
                        AutoDisableInternal(e,
                            $"referenced loop '{e.LoopName}' was deleted or renamed");
                        anyChanged = true;
                    }
                    break;
                case EventActionType.AutoLair:
                    if (FindSetup(e.AutoLairSetupName) is null && !string.IsNullOrEmpty(e.AutoLairSetupName))
                    {
                        AutoDisableInternal(e,
                            $"referenced auto-lair setup '{e.AutoLairSetupName}' was deleted or renamed");
                        anyChanged = true;
                    }
                    break;
            }
        }
        if (anyChanged)
        {
            _profile?.Save();
            AutoDisabledChanged?.Invoke();
        }
    }

    /// <summary>
    /// Fire-time auto-disable. Differs from
    /// <see cref="AutoDisableInternal"/> in that it persists +
    /// notifies immediately (since the caller isn't part of a bulk
    /// reconciliation pass).
    /// </summary>
    private void AutoDisable(ScheduledEvent e, string reason)
    {
        AutoDisableInternal(e, reason);
        _profile?.Save();
        AutoDisabledChanged?.Invoke();
    }

    private void AutoDisableInternal(ScheduledEvent e, string reason)
    {
        e.Disabled = true;
        _autoDisabled.Add(e);
        _log?.Warn("Events", $"Event '{Label(e)}' auto-disabled — {reason}.");
    }

    // ----- Lookups ---------------------------------------------------

    private Loop? FindLoop(string? name) =>
        string.IsNullOrEmpty(name) || _loops is null
            ? null
            : _loops.Loops.FirstOrDefault(l =>
                string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    private LairSetup? FindSetup(string? name) =>
        string.IsNullOrEmpty(name) || _lairs is null
            ? null
            : _lairs.Setups.FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string Label(ScheduledEvent e) =>
        string.IsNullOrWhiteSpace(e.Name) ? "(unnamed)" : e.Name;

    // ----- Lifecycle -------------------------------------------------

    private void LoadFrom(CharacterProfile p)
    {
        Events.Clear();
        _autoDisabled.Clear();
        if (p.Events is not null)
            foreach (ScheduledEvent e in p.Events) Events.Add(e);
        ReconcileTargets();
    }

    private void Clear()
    {
        Events.Clear();
        _autoDisabled.Clear();
    }

    private void SnapshotForSave(CharacterProfile p)
    {
        p.Events = Events.Count == 0 ? null : Events.ToList();
    }
}
