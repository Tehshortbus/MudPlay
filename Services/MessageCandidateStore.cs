using System.IO;
using MudPlay.Models.GameData;

namespace MudPlay.Services;

// In-memory cache of staged, unrecognized-message candidates for the active
// game-data set. Parallels MessageStore/MonsterMessageStore's load/save shape,
// but candidates are pure runtime-observed state rather than curated data —
// there's nothing to ship as a starting point, so unlike those two stores
// there is no universal-seed fallback: a missing per-set file just means an
// empty catalogue.
//
// Wiring: AppServices subscribes the store to GameDataCache.ActiveSetChanged —
// on every set switch the per-set file at AppPaths.MessageCandidatesFile is
// reloaded (missing file ⇒ empty). Game.MessageCandidateWatcher is the sole
// writer via RecordSighting; the Game Data Browser's Unrecognized Lines tab and the
// LogPane double-click flow both read/dismiss/remove through this store.
public sealed class MessageCandidateStore
{
    private readonly LogService? _log;

    // Live mirror of the active set's staged candidates. BulkObservableCollection
    // so a full (re)load raises one Reset instead of Clear + N Add, matching
    // MessageStore/MonsterMessageStore's rationale.
    public BulkObservableCollection<MessageCandidateRecord> Candidates { get; } = new();

    // Set name currently sourcing Candidates, or null when none is active.
    public string? ActiveSet { get; private set; }

    // Debounce flag for QueueSave — RecordSighting is a hot path (every
    // unrecognized line in active play can call it), unlike MessageStore's rare,
    // interactive edits, so a synchronous atomic-rename JsonStore.Save on every
    // single occurrence-bump would mean disk I/O per repeated unrecognized
    // line. Mirrors SpellCoverageAuditor's QueueRun idiom: mutate Candidates
    // immediately (an open Browser tab reflects it live), coalesce the actual
    // write onto the dispatcher.
    private bool _saveQueued;

    public MessageCandidateStore() { }

    public MessageCandidateStore(LogService log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    // Switch the catalogue to setName's on-disk file. Pass null to clear (no
    // set active).
    public void Load(string? setName)
    {
        ActiveSet = setName;
        if (string.IsNullOrWhiteSpace(setName)) { Candidates.ReplaceAll([]); return; }

        List<MessageCandidateRecord> loaded =
            TryLoad(AppPaths.MessageCandidatesFile(setName)) ?? [];
        Candidates.ReplaceAll(loaded);
    }

    // Parsed list (possibly empty) iff the file existed AND parsed cleanly;
    // null for missing/corrupt so Load falls back to an empty catalogue.
    private List<MessageCandidateRecord>? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonStore.Load<List<MessageCandidateRecord>>(path);
        }
        catch (Exception ex)
        {
            _log?.Log(LogSeverity.Warn, "MessageCandidates",
                $"Failed to load '{path}': {ex.Message}");
            return null;
        }
    }

    // Persist Candidates to ActiveSet's file immediately (synchronous) — used by
    // the debounced QueueSave callback and available directly for tests.
    public void Save()
    {
        if (string.IsNullOrWhiteSpace(ActiveSet)) return;
        JsonStore.Save(AppPaths.MessageCandidatesFile(ActiveSet), Candidates);
    }

    // Insert-or-bump keyed by ComputeId(rawText). A dismissed record still gets
    // bumped on a repeat sighting — dismissal only stops re-alerting
    // (MessageCandidateWatcher's first-sighting log), it doesn't stop dedup
    // tracking, so a recurring line that was already dismissed as boring
    // doesn't quietly resurface and re-alert. Returns IsNew so the caller knows
    // whether to log a first-sighting Warn.
    // map/room tag the FIRST sighting's location (a locator hint) — a bump keeps the
    // original location rather than overwriting, so the record shows where the line
    // was first noticed even if it later recurs elsewhere.
    public (MessageCandidateRecord Record, bool IsNew) RecordSighting(
        string rawText, DateTimeOffset when, int? map = null, int? room = null)
    {
        string id = MessageCandidateRecord.ComputeId(rawText);
        for (int i = 0; i < Candidates.Count; i++)
        {
            if (Candidates[i].Id != id) continue;
            // A dismissed candidate is frozen — a recurrence neither bumps its
            // count nor re-saves (the watcher already gates on IsDismissed; this
            // keeps a direct call honest too).
            if (Candidates[i].Dismissed) return (Candidates[i], false);
            MessageCandidateRecord bumped = Candidates[i] with
            {
                LastSeenAt = when,
                Occurrences = Candidates[i].Occurrences + 1,
            };
            Candidates[i] = bumped;
            QueueSave();
            return (bumped, false);
        }

        MessageCandidateRecord created = new(
            id, rawText, when, when, Occurrences: 1, Dismissed: false, Map: map, Room: room);
        Candidates.Add(created);
        QueueSave();
        return (created, true);
    }

    // Dismiss — marks the record Dismissed and freezes it: the row stays in the
    // table (occurrence count frozen where it was) but the watcher then ignores
    // every future recurrence of that text entirely (IsDismissed gate) — no
    // re-add, no bump, no re-alert. A final "decided, stop tracking" verdict, as
    // distinct from Remove (hard delete, which lets a later recurrence re-capture
    // the line as new). No-op if id isn't found.
    public void Dismiss(string id)
    {
        for (int i = 0; i < Candidates.Count; i++)
        {
            if (Candidates[i].Id != id) continue;
            if (Candidates[i].Dismissed) return;
            Candidates[i] = Candidates[i] with { Dismissed = true };
            QueueSave();
            return;
        }
    }

    // Cheap existence check by raw text, without mutating — lets
    // MessageCandidateWatcher's burst-cap gate tell "this repeats an
    // already-staged candidate" (always let through, dedup is free) apart
    // from "this would create a new one" (subject to the cap) without a
    // wasted insert-then-undo.
    public bool Contains(string rawText)
    {
        string id = MessageCandidateRecord.ComputeId(rawText);
        foreach (MessageCandidateRecord c in Candidates)
            if (c.Id == id) return true;
        return false;
    }

    // True when this exact text is already staged AND marked dismissed — the
    // watcher uses this to drop a recurrence of a dismissed line entirely (no
    // re-add, no occurrence bump, no re-alert): dismissal is a final "I've
    // decided about this line, stop tracking it" verdict.
    public bool IsDismissed(string rawText)
    {
        string id = MessageCandidateRecord.ComputeId(rawText);
        foreach (MessageCandidateRecord c in Candidates)
            if (c.Id == id) return c.Dismissed;
        return false;
    }

    // Hard removal — used once a candidate is successfully converted into a
    // real MessageRecord (it's real data now, no longer a candidate).
    public void Remove(string id)
    {
        for (int i = 0; i < Candidates.Count; i++)
        {
            if (Candidates[i].Id != id) continue;
            Candidates.RemoveAt(i);
            QueueSave();
            return;
        }
    }

    private void QueueSave()
    {
        if (_saveQueued) return;
        _saveQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => { _saveQueued = false; Save(); },
            Avalonia.Threading.DispatcherPriority.Background);
    }
}
