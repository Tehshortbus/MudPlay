using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Events;

// In-memory store + dispatcher for the loaded character's ScheduledEvent
// entries. Owns the merge / save path against CharacterProfile.Events, exposes
// CRUD for the settings editor, reconciles saved-target references against
// LoopManager + LairManager, and dispatches fired actions into the existing
// movement / command stack.
//
// The dispatcher is end-to-end executable — Fire can be called and the four
// action types route correctly into AutoWalkManager, LoopRunner,
// AutoLairManager, and the bound wire sender. The trigger sources (At time /
// Every / Logon / Logoff / Re-log) subscribe the appropriate sources to call
// Fire.
//
// Saved-target reconciliation: subscribes to LoopManager.LoopsChanged +
// LairManager.SetupsChanged. On either, walks every event whose ActionType is
// Loop or AutoLair and auto-disables any whose referenced name is no longer in
// the corresponding manager's collection. Auto-disable is sticky: re-creating a
// same-named target later doesn't auto-restore — the user re-enables manually
// after confirming the new target matches their intent.
//
// Threading: profile lifecycle + manager-change events fire on the UI thread
// (the producers marshal upstream). Fire is called from the trigger sources on
// the UI thread too, so no internal locking is needed on the events list.
public sealed class EventManager : IDisposable
{
    private readonly ProfileService? _profile;
    private readonly LoopManager? _loops;
    private readonly LairManager? _lairs;
    private readonly LoopRunner? _loopRunner;
    private readonly AutoLairManager? _autoLair;
    private readonly AutoWalkManager? _walker;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    // Tracks which events the reconciler auto-disabled (vs the user manually
    // flipping Disabled). The Settings.Events row badge renders "↻ target missing"
    // only for keys in this set. Cleared on profile load.
    private readonly HashSet<ScheduledEvent> _autoDisabled = new();

    // Snapshot of "what was happening" when an event-walk took over. Set in
    // ExecuteWalkTo, consumed in OnResumeWalkEvent when the walker reaches the
    // event's target. Null when no resume is queued. Internal-set so tests can
    // assert on it; production callers use the public surface.
    internal EventResumePlan? PendingResumeForTests
    {
        get => _pendingResume;
        set => _pendingResume = value;
    }
    private EventResumePlan? _pendingResume;

    // Live delegate reference so we can unsubscribe symmetrically. Null when no
    // resume watcher is attached.
    private Action<WalkEvent>? _resumeWatcher;

    // The loaded character's events — empty when no profile is active.
    public ObservableCollection<ScheduledEvent> Events { get; } = new();

    // Returns true when the reconciler — not the user — flipped this event's
    // Disabled flag on. Drives the row badge.
    public bool IsAutoDisabled(ScheduledEvent e) => _autoDisabled.Contains(e);

    // Fires after the reconciler auto-disables one or more events. The row-badge
    // refresh listens for this; here so the engine surface stays observable.
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

    // Parameterless ctor for tests / in-memory scenarios — no profile, no engines.
    public EventManager() { }

    // Symmetric tear-down for the five ProfileService / LoopManager / LairManager
    // subscriptions wired in the engine-bound ctor. Today AppServices keeps
    // EventManager alive for the app's lifetime so this is mostly hygiene — but it
    // keeps the rule explicit in case a future refactor scopes EventManager
    // per-character. Idempotent.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachResumeWatcher();
        if (_profile is not null)
        {
            _profile.ProfileLoaded -= LoadFrom;
            _profile.ProfileClosed -= Clear;
            _profile.ProfileSaving -= SnapshotForSave;
        }
        if (_loops is not null)  _loops.LoopsChanged -= ReconcileTargets;
        if (_lairs is not null)  _lairs.SetupsChanged -= ReconcileTargets;
    }
    private bool _disposed;

    // Bind the wire sender for EventActionType.Command dispatch. Same shape as the
    // other automation engines — the main window VM supplies the gate-wrapped
    // SendUserInput.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Test seam — bytes the engine asked to write to the wire.
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    // ----- CRUD ------------------------------------------------------

    // Append a new event and persist. No duplicate check.
    public void Add(ScheduledEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Events.Add(e);
        ReconcileTargets();
        _profile?.Save();
    }

    // Replace an existing event by reference. Persists. No-op when original isn't
    // in the list.
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

    // Remove an event by reference. Persists. No-op when not in the list.
    public bool Remove(ScheduledEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!Events.Remove(e)) return false;
        _autoDisabled.Remove(e);
        _profile?.Save();
        return true;
    }

    // ----- Dispatch --------------------------------------------------

    // Execute the event's action. Skips when Disabled is true OR when the
    // fire-time safety net detects a missing saved target (Loop / AutoLair name no
    // longer in the manager's collection). The safety net mirrors what
    // ReconcileTargets does on LoopsChanged / SetupsChanged — defense in depth for
    // races and direct-disk profile edits.
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

    // Synchronously fires every EventTriggerType.Logoff event in the list. Called
    // from the user-initiated disconnect path BEFORE the wire closes (and BEFORE
    // the dropped-connection auto-reconnect kicks in for dropped paths — those
    // skip Logoff entirely because nobody calls this method on a server-side
    // drop). Caller is responsible for the bounded flush window between this call
    // and the actual DisposeAsync so the wire writes have time to drain. Returns
    // the number of events actually dispatched so the caller can skip the flush
    // wait when nothing fired.
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

        // Snapshot the current activity BEFORE stopping engines so the
        // resume hook can pick up where we left off when the walk
        // completes. If there's already a pending plan (i.e. a prior
        // event-walk is in flight when this one fires), keep the
        // ORIGINAL plan — we want to resume the user's actual activity,
        // not the previous event-walk's destination.
        EventResumePlan? plan = _pendingResume ?? SnapshotCurrentActivity();

        // Supersede any running engine so the walk owns the wire.
        EngineSupersede.StopOthers(
            _walker, _loopRunner, _autoLair,
            SupersedeKeep.Walker, "event walk-to");

        // Detach any prior watcher first — the cascade case means the
        // previous walk-to is being replaced by this one. The plan
        // we just snapshotted (which is the previous walk's plan in
        // the cascade case) gets re-attached below.
        DetachResumeWatcher();

        RoomKey key = new(target.Map, target.Room);
        if (!_walker.WalkTo(key))
        {
            _log?.Warn("Events", $"Event '{Label(e)}' walk-to {key.Map}/{key.Room} failed.");
            return;
        }

        // Walk to event-target unlike @goto's monster-room special case:
        // events specify the destination as a RoomRef coord, no neighbour
        // wait-room logic. Walker drops us at the exact room.

        // Attach the resume watcher AFTER WalkTo returns so the
        // Started / Stopped events that WalkTo itself raises (for
        // any walk it superseded) don't reach our handler.
        if (plan is not null)
        {
            _pendingResume = plan;
            AttachResumeWatcher();
            _log?.Info("Events",
                $"Event '{Label(e)}' walking to {key.Map}/{key.Room}; will resume {plan.Describe()} on arrival.");
        }
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
        EngineSupersede.StopOthers(
            _walker, _loopRunner, _autoLair,
            SupersedeKeep.Loop, "event supersede");
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
        EngineSupersede.StopOthers(
            _walker, _loopRunner, _autoLair,
            SupersedeKeep.Lair, "event supersede");

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

    // Split a Command action's text on ^M AND ';' boundaries — both denote a CR
    // break (matching the existing Macro / Trigger / Alias multi-step splitter).
    // An empty chunk between two consecutive separators is dropped so "look;;sit"
    // sends two lines rather than three. Internal so tests can assert the splitter
    // contract without firing a real event end-to-end.
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

    // Walk every Loop / AutoLair-action event and auto-disable any whose
    // referenced saved name is no longer in the manager's collection. Idempotent —
    // already-disabled events are left alone. Re-runs on profile load + every
    // LoopsChanged / SetupsChanged event.
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

    // Fire-time auto-disable. Differs from AutoDisableInternal in that it persists
    // + notifies immediately (since the caller isn't part of a bulk
    // reconciliation pass).
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

    // ----- Walk-to auto-resume ---------------------------------------

    // Snapshot the engine that was actively driving movement before an event-walk
    // takes over. Precedence — AutoLair beats LoopRunner beats the one-shot
    // walker, because the higher engines drive the lower ones (a Loop's approach
    // walk is "really" the loop). Returns null when nothing was running, in which
    // case the event-walk just runs and ends — no resume.
    internal EventResumePlan? SnapshotCurrentActivity()
    {
        if (_autoLair is { IsActive: true } al && al.Marked.Count > 0)
        {
            Dictionary<RoomKey, int?> snap = new();
            foreach (RoomKey k in al.Marked)
                snap[k] = al.Overrides.TryGetValue(k, out int? v) ? v : null;
            return new EventResumePlan.AutoLair(snap);
        }
        if (_loopRunner is not null
            && _loopRunner.State is not LoopState.Idle
            && _loopRunner.CurrentLoop is { } loop)
        {
            return new EventResumePlan.Loop(loop);
        }
        if (_walker is { State: WalkState.Walking } && _walker.Destination is { } dest)
        {
            return new EventResumePlan.Walker(dest);
        }
        return null;
    }

    private void AttachResumeWatcher()
    {
        if (_walker is null || _resumeWatcher is not null) return;
        _resumeWatcher = OnResumeWalkEvent;
        _walker.Event += _resumeWatcher;
    }

    private void DetachResumeWatcher()
    {
        if (_walker is null || _resumeWatcher is null) return;
        _walker.Event -= _resumeWatcher;
        _resumeWatcher = null;
    }

    // Watch the walker for the outcome of an in-flight event-walk. Finished →
    // execute the resume plan. Failed / Stopped → drop the plan (user can
    // intervene). Pause / Resume / Started are not interesting — the walk is still
    // in progress.
    internal void OnResumeWalkEvent(WalkEvent e)
    {
        switch (e.Kind)
        {
            case WalkEventKind.Finished:
                EventResumePlan? plan = _pendingResume;
                _pendingResume = null;
                DetachResumeWatcher();
                if (plan is not null) ExecuteResume(plan);
                break;
            case WalkEventKind.Failed:
            case WalkEventKind.Stopped:
                _log?.Info("Events",
                    $"Event walk-to interrupted ({e.Kind}); dropping resume plan.");
                _pendingResume = null;
                DetachResumeWatcher();
                break;
        }
    }

    // Re-dispatch the activity that was running before the event-walk took over.
    // Stops nothing first — the walker is Idle by now (we just hit Finished), and
    // the other engines were stopped at the start of the event-walk so they're
    // already inactive.
    private void ExecuteResume(EventResumePlan plan)
    {
        switch (plan)
        {
            case EventResumePlan.Loop l:
                if (_loopRunner is null) return;
                _log?.Info("Events", $"Resuming loop '{l.SavedLoop.Name}' after event-walk.");
                _loopRunner.Start(l.SavedLoop);
                break;
            case EventResumePlan.AutoLair al:
                if (_autoLair is null) return;
                _log?.Info("Events", $"Resuming auto-lair ({al.Markers.Count} markers) after event-walk.");
                _autoLair.Clear();
                foreach (KeyValuePair<RoomKey, int?> kv in al.Markers)
                    _autoLair.Mark(kv.Key, kv.Value);
                _autoLair.Start();
                break;
            case EventResumePlan.Walker w:
                if (_walker is null) return;
                _log?.Info("Events", $"Resuming walk to {w.Destination.Map}/{w.Destination.Room} after event-walk.");
                _walker.WalkTo(w.Destination);
                break;
        }
    }

    // What to do after the event-walk reaches its target. Discriminated across the
    // three engine types SnapshotCurrentActivity distinguishes. Internal so tests
    // can pattern-match the snapshot.
    internal abstract record EventResumePlan
    {
        public abstract string Describe();

        public sealed record Loop(Game.Map.Loop SavedLoop) : EventResumePlan
        {
            public override string Describe() => $"loop '{SavedLoop.Name}'";
        }
        public sealed record AutoLair(Dictionary<RoomKey, int?> Markers) : EventResumePlan
        {
            public override string Describe() => $"auto-lair ({Markers.Count} markers)";
        }
        public sealed record Walker(RoomKey Destination) : EventResumePlan
        {
            public override string Describe() => $"walk to {Destination.Map}/{Destination.Room}";
        }
    }
}
