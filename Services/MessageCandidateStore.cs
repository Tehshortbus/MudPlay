using System.IO;
using MudPlay.Models.GameData;

namespace MudPlay.Services;

// In-memory cache of staged, unrecognized-message candidates for the active
// realm. Parallels MessageStore/MonsterMessageStore's load/save shape, but
// candidates are pure runtime-observed state rather than curated data —
// there's nothing to ship as a starting point, so unlike those two stores
// there is no universal-seed fallback: a missing file just means an empty
// catalogue.
//
// Wiring: AppServices reloads it from the active realm's file
// (AppPaths.RealmMessageCandidatesFile) on every profile load / realm re-pin; a
// realm with no file yet takes a copy of its game-data set's legacy one.
// Game.MessageCandidateWatcher is the sole writer via RecordSighting; the Game
// Data Browser's Unrecognized Lines tab and the
// LogPane double-click flow both read/dismiss/remove through this store.
//
// Saving: a change is written SaveDelay after it, together with every change made
// in the meantime, and the write itself runs on a pool thread. Every repeat of a
// staged line bumps its count, so a grind that repeats one (a cast line per fight)
// used to rewrite the whole file on the UI thread every few seconds: 4,441 writes
// in a night, one of them a 5.3 s freeze when the disk stalled (reports
// Hang-20261021-035337, Hang-20261021-065221). A realm switch, the client's exit
// and the restart after a data-folder move write what is still waiting at once
// (Save), so nothing is left unsaved.
//
// Bounded: at most MaxPending lines wait for review. A new line past that pushes
// out the pending line seen longest ago. Dismissed lines are the user's verdicts
// and are never pushed out.
public sealed class MessageCandidateStore
{
    // How long a change waits for the others that follow it before the file is
    // written. A crash loses at most this much of the queue, which only ever holds
    // lines waiting for review and their counts.
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(30);

    // Pending (not dismissed) lines kept for review.
    public const int MaxPending = 2000;

    private readonly LogService? _log;
    private readonly Func<TimeSpan, Action, IDisposable> _arm;

    // Live mirror of the active set's staged candidates. BulkObservableCollection
    // so a full (re)load raises one Reset instead of Clear + N Add, matching
    // MessageStore/MonsterMessageStore's rationale.
    public BulkObservableCollection<MessageCandidateRecord> Candidates { get; } = new();

    // The realm whose lines are loaded, or null when none. Unrecognized lines are
    // what the characters on a realm saw there, so they are the realm's: they were
    // kept with the game-data set, which two realms can share.
    public string? ActiveRealmFolder { get; private set; }

    // Every client on the realm records into the one file. What this client changed
    // since it last read or wrote the file is kept here, so that a write is the
    // file as the last character left it plus these changes, not this client's whole
    // copy over theirs. Each change is marked with the change count it was made at,
    // and is forgotten once a write that holds it has reached the file.
    private readonly SharedFileStamp _stamp = new();
    private readonly Dictionary<string, long> _changedHere = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _removedHere = new(StringComparer.Ordinal);

    // Changes made so far, counted on the UI thread.
    private long _changes;

    // The armed save, or null when nothing waits to be written.
    private IDisposable? _saveDue;

    // The write lane. One write runs at a time, under _writeGate, and the newest
    // list waiting replaces an older one that hasn't started. _stampGate keeps the
    // pool thread's mark of the file and the UI thread's look at it apart.
    private readonly object _writeGate = new();
    private readonly object _stampGate = new();
    private long _stampMarks;
    private readonly object _laneGate = new();
    private PendingWrite? _waiting;
    private bool _draining;
    private Task _lane = Task.CompletedTask;
    private long _changesWritten;
    private bool _writeFailureLogged;

    // The list as it stood at a change count, for one realm's file.
    private sealed record PendingWrite(string Path, List<MessageCandidateRecord> Records, long Changes);

    // arm: how a save is put off by SaveDelay; the dispatcher's one-shot timer by
    // default.
    public MessageCandidateStore(LogService? log = null, Func<TimeSpan, Action, IDisposable>? arm = null)
    {
        _log = log;
        _arm = arm ?? DispatcherTicks.Once;
    }

    // Load realmFolder's lines. legacySet names the game-data set the realm runs
    // on: a realm with no file of its own yet takes a copy of the one that set
    // carried, from when the lines were kept per set. What the realm being left
    // still had waiting is written to its file first.
    public void Load(string? realmFolder, string? legacySet = null)
    {
        Save();
        ActiveRealmFolder = string.IsNullOrWhiteSpace(realmFolder) ? null : realmFolder;
        _changedHere.Clear();
        _removedHere.Clear();
        if (ActiveRealmFolder is null) { Candidates.ReplaceAll([]); return; }

        string path = AppPaths.RealmMessageCandidatesFile(ActiveRealmFolder);
        AdoptLegacyFile(path, legacySet);
        MarkStamp(path);
        List<MessageCandidateRecord> loaded = TryLoad(path) ?? [];
        HashSet<string> pushedOut = OldestPastCap(loaded);
        if (pushedOut.Count > 0)
        {
            // Dropped before the list is shown, in one pass: a file grown far past
            // the cap would otherwise be trimmed a line and a list change at a time.
            loaded.RemoveAll(c => pushedOut.Contains(c.Id));
            foreach (string id in pushedOut) _removedHere[id] = ++_changes;
            QueueSave();
            _log?.Info("MessageCandidates",
                $"{pushedOut.Count} unrecognized line(s) seen longest ago left the review queue: it keeps {MaxPending} pending lines.");
        }
        Candidates.ReplaceAll(loaded);
    }

    private void AdoptLegacyFile(string realmFile, string? legacySet)
    {
        if (string.IsNullOrWhiteSpace(legacySet) || File.Exists(realmFile)) return;
        string legacy = AppPaths.LegacySetMessageCandidatesFile(legacySet);
        if (!File.Exists(legacy)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(realmFile)!);
            File.Copy(legacy, realmFile);
        }
        catch (IOException)
        {
            // Another client on the realm copied it first — the file is where it belongs.
        }
    }

    // Re-read the lines when another client on the realm has written them, keeping
    // what this client changed since. Called on the heartbeat. True when re-read.
    public bool TakeInOutsideChanges()
    {
        if (ActiveRealmFolder is null) return false;
        string path = AppPaths.RealmMessageCandidatesFile(ActiveRealmFolder);
        if (!ChangedOutside(path) || !Rebase(path)) return false;
        // A deferred write that found the file changed under it was let go (see
        // Write); the lines it held go out again on top of what was just taken in.
        if (_changes > Interlocked.Read(ref _changesWritten)) QueueSave();
        return true;
    }

    private void MarkStamp(string path)
    {
        lock (_stampGate)
        {
            _stamp.Mark(path);
            _stampMarks++;
        }
    }

    private (DateTime Seen, long Marks) LookStamp(string path)
    {
        lock (_stampGate) return (_stamp.Look(path), _stampMarks);
    }

    // Mark with what a read looked at, unless this client's own write marked the
    // file since: that mark is newer, and putting the older one back would make
    // our own write look like another client's.
    private void MarkStampAs((DateTime Seen, long Marks) look)
    {
        lock (_stampGate)
            if (_stampMarks == look.Marks) _stamp.MarkAs(look.Seen);
    }

    private bool ChangedOutside(string path)
    {
        lock (_stampGate) return _stamp.ChangedOutside(path);
    }

    // Forget the changes a finished write has put in the file: all of them, or (for
    // a read of the file) those written before the read began. A write landing
    // during the read may be missing from what was read, so its changes are kept.
    private void ForgetWrittenChanges(long? upTo = null)
    {
        long written = upTo ?? Interlocked.Read(ref _changesWritten);
        Forget(_changedHere, written);
        Forget(_removedHere, written);

        static void Forget(Dictionary<string, long> marks, long upTo)
        {
            if (marks.Count == 0) return;
            List<string>? done = null;
            foreach ((string id, long at) in marks)
                if (at <= upTo) (done ??= new()).Add(id);
            if (done is null) return;
            foreach (string id in done) marks.Remove(id);
        }
    }

    // Make the list the file's, with this client's unsaved changes on top: a line
    // both saw keeps the later sighting, the higher count and a dismissal from
    // either. False when the file is there but can't be read (the list is kept).
    private bool Rebase(string path)
    {
        long writtenBeforeRead = Interlocked.Read(ref _changesWritten);
        (DateTime, long) seen = LookStamp(path);
        List<MessageCandidateRecord>? onFile = File.Exists(path) ? TryLoad(path) : [];
        if (onFile is null) return false;

        ForgetWrittenChanges(writtenBeforeRead);
        Dictionary<string, MessageCandidateRecord> mine = new(StringComparer.Ordinal);
        foreach (MessageCandidateRecord c in Candidates)
            if (_changedHere.ContainsKey(c.Id)) mine[c.Id] = c;

        List<MessageCandidateRecord> merged = new(onFile.Count + mine.Count);
        foreach (MessageCandidateRecord theirs in onFile)
        {
            if (_removedHere.ContainsKey(theirs.Id)) continue;
            if (!mine.Remove(theirs.Id, out MessageCandidateRecord? ours)) { merged.Add(theirs); continue; }
            MessageCandidateRecord latest = theirs.LastSeenAt > ours.LastSeenAt ? theirs : ours;
            merged.Add(latest with
            {
                Occurrences = Math.Max(theirs.Occurrences, ours.Occurrences),
                Dismissed = theirs.Dismissed || ours.Dismissed,
            });
        }
        merged.AddRange(mine.Values);   // first seen here

        MarkStampAs(seen);
        Candidates.ReplaceAll(merged);
        return true;
    }

    // Parsed list (possibly empty) iff the file existed AND parsed cleanly;
    // null for missing/corrupt so Load falls back to an empty catalogue.
    private List<MessageCandidateRecord>? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        List<MessageCandidateRecord>? read;
        try
        {
            read = JsonStore.Load<List<MessageCandidateRecord>>(path);
        }
        catch (Exception ex)
        {
            _log?.Log(LogSeverity.Warn, "MessageCandidates",
                $"Failed to load '{path}': {ex.Message}");
            return null;
        }
        return read is null ? null : Usable(read, path);
    }

    // A field the file leaves out reads back null, and every candidate is keyed by its
    // Id. One with its line is given back the Id the line makes; one without a line is
    // nothing to review, and is dropped.
    private List<MessageCandidateRecord> Usable(List<MessageCandidateRecord> read, string path)
    {
        List<MessageCandidateRecord> usable = new(read.Count);
        foreach (MessageCandidateRecord? c in read)
        {
            if (c?.RawText is null) continue;
            usable.Add(c.Id is null ? c with { Id = MessageCandidateRecord.ComputeId(c.RawText) } : c);
        }
        int dropped = read.Count - usable.Count;
        if (dropped > 0)
            _log?.Log(LogSeverity.Warn, "MessageCandidates",
                $"'{path}': left out {dropped} entr{(dropped == 1 ? "y" : "ies")} with no line — not unrecognized lines this client saved.");
        return usable;
    }

    // Write the realm's file now, on this thread, when a change waits: what is on
    // file, plus this client's changes. For a realm switch and the client's exit; a
    // change on its own is written later and off the UI thread (see QueueSave).
    public void Save()
    {
        _saveDue?.Dispose();
        _saveDue = null;
        if (_changes <= Interlocked.Read(ref _changesWritten)) return;
        // Another client writing between the snapshot's look at the file and the
        // write sends it round again, on top of their lines; a few rounds at most.
        for (int round = 0; round < 3; round++)
            if (TakeSnapshot() is not { } write || Write(write) != WriteOutcome.FileChangedUnderIt) break;
        ForgetWrittenChanges();
    }

    // Completes when the writes handed off so far have finished. The client never
    // waits on it; a test does, to read the file a deferred save wrote.
    internal Task WhenSavedAsync()
    {
        lock (_laneGate) return _lane;
    }

    // On the UI thread: take in another client's lines if they wrote the file,
    // then copy the list as it stands.
    private PendingWrite? TakeSnapshot()
    {
        ForgetWrittenChanges();
        if (ActiveRealmFolder is null) return null;
        string path = AppPaths.RealmMessageCandidatesFile(ActiveRealmFolder);
        if (ChangedOutside(path)) Rebase(path);
        return new PendingWrite(path, [.. Candidates], _changes);
    }

    // The save SaveDelay after the first change it holds: the list goes to the
    // pool thread, and the UI thread is back at once.
    private void OnSaveDue()
    {
        _saveDue = null;
        if (TakeSnapshot() is not { } write) return;
        lock (_laneGate)
        {
            _waiting = write;
            if (_draining) return;   // the running lane takes it before it stops
            _draining = true;
            _lane = Task.Run(DrainWrites);
        }
    }

    private void DrainWrites()
    {
        while (true)
        {
            PendingWrite? next;
            lock (_laneGate)
            {
                next = _waiting;
                _waiting = null;
                // Cleared under the same lock that hands a list over, so a list
                // handed after this look starts a lane of its own.
                if (next is null) { _draining = false; return; }
            }
            try { Write(next); }
            catch (Exception ex)
            {
                // Anything Write's own catch doesn't expect. The lane goes on, and the
                // changes stay marked for the next save.
                _log?.Warn("MessageCandidates", $"Saving '{next.Path}' failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private enum WriteOutcome { Written, AlreadyOnDisk, FileChangedUnderIt, Failed }

    // Write one list. A list no newer than what is on disk is skipped: a save made
    // at once (Save) can overtake a deferred one still waiting its turn. So is one
    // whose file another client wrote since the list was taken: written, it would
    // put back the lines their write took out and drop the ones it added. The
    // heartbeat takes their lines in and arms the save again (TakeInOutsideChanges).
    private WriteOutcome Write(PendingWrite write)
    {
        lock (_writeGate)
        {
            if (write.Changes <= Interlocked.Read(ref _changesWritten)) return WriteOutcome.AlreadyOnDisk;
            if (ChangedOutside(write.Path)) return WriteOutcome.FileChangedUnderIt;
            try
            {
                JsonStore.Save(write.Path, write.Records);
                MarkStamp(write.Path);
                Interlocked.Exchange(ref _changesWritten, write.Changes);
                _writeFailureLogged = false;
                return WriteOutcome.Written;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The changes stay marked, and the next change or the exit writes
                // them again. Logged once until a write succeeds.
                if (!_writeFailureLogged)
                    _log?.Warn("MessageCandidates", $"Couldn't save '{write.Path}': {ex.Message}");
                _writeFailureLogged = true;
                return WriteOutcome.Failed;
            }
        }
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
            MarkChanged(id);
            return (bumped, false);
        }

        MessageCandidateRecord created = new(
            id, rawText, when, when, Occurrences: 1, Dismissed: false, Map: map, Room: room);
        Candidates.Add(created);
        _removedHere.Remove(id);
        MarkChanged(id);
        foreach (string oldest in OldestPastCap(Candidates)) Remove(oldest);
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
            MarkChanged(id);
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
            _changedHere.Remove(id);
            _removedHere[id] = ++_changes;
            QueueSave();
            return;
        }
    }

    private void MarkChanged(string id)
    {
        _changedHere[id] = ++_changes;
        QueueSave();
    }

    // The pending lines of `list` past MaxPending, seen longest ago first. A new
    // line past the cap pushes out one, quietly: the queue only ever holds lines
    // waiting for review, and a log line per push would bury the log once full.
    private static HashSet<string> OldestPastCap(IReadOnlyCollection<MessageCandidateRecord> list)
    {
        int over = list.Count(c => !c.Dismissed) - MaxPending;
        if (over <= 0) return [];
        return list.Where(c => !c.Dismissed)
            .OrderBy(c => c.LastSeenAt)
            .Take(over)
            .Select(c => c.Id)
            .ToHashSet(StringComparer.Ordinal);
    }

    // Put the write off until SaveDelay after the first change it will hold.
    private void QueueSave() => _saveDue ??= _arm(SaveDelay, OnSaveDue);
}
