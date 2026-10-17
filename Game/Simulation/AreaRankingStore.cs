using MudPlay.Services;

namespace MudPlay.Game.Simulation;

// The Simulator's finished area rankings, each kept for the game-data set and
// character it was made on (SimulatorOwner). The Simulator shows the active
// pairing's own and no other's: switching game data or character puts that
// pairing's ranking on show when it has one, and switching back brings the first
// one back without ranking again.
//
// Kept in two places. In memory, the MaxKept most recently made or shown (a ranking
// is a few hundred rows, each with its tour's rooms and its runs' figures: about a
// megabyte for a whole realm). On disk, one file a pairing (AreaRankingFiles), so a
// ranking outlives the client: written when it is kept, read back the first time
// its pairing is shown with nothing in memory (Load), never at startup. Letting one
// go from memory to stay within MaxKept leaves its file, and it is read back when
// its pairing is next shown.
//
// What is told here is when a ranking stops being true, and it then goes from both
// places, whether or not its set is active and whether or not a Simulator is open:
// a set's data replaced (ForgetSet), a character renamed, moved or deleted
// (RenameCharacter, ForgetCharacter, RenameBbs). What can't be told (the character
// re-geared, the tables replaced while the client was closed) is caught when a
// ranking is listed again, from what it carries of the world and character it was
// made for.
//
// A set read in again (picked again from the menu, imported over itself) is not a
// set whose data was replaced: its tables may be the very same files. Nothing goes
// for that (user, 2026-10-11). The world a ranking carries is compared with the new
// load's when it is next asked for, from memory (Get) as from disk (Load), and only
// one whose tables did change is dropped.
//
// The memory side is the UI thread's alone, as the Simulator's view-model is. File
// work runs off it, one piece at a time in the order asked, so a delete asked after
// a write lands after it; what a read finds comes back through post.
public sealed class AreaRankingStore
{
    public const int MaxKept = 8;

    // Least recently made or shown first.
    private readonly List<(SimulatorOwner Owner, AreaRanking Ranking)> _kept = new();

    private readonly AreaRankingFiles _files;
    private readonly Action<Action> _post;
    private readonly LogService? _log;
    private readonly Action<Action>? _runFileWork;
    private readonly object _ioLock = new();
    private Task _io = Task.CompletedTask;
    // Moves with every forget and rename; see Load.
    private long _epoch;
    private bool _writeFailureSaid;

    // What the files have been through lately, newest last, for the bug report:
    // saved, read back, dropped and why.
    private readonly List<string> _diskNotes = new();
    private const int MaxDiskNotes = 12;

    // post hands what a read found back to the UI thread. runFileWork runs a piece
    // of file work; left out, the work is queued off the calling thread, each piece
    // after the one before.
    public AreaRankingStore(AreaRankingFiles files, Action<Action> post, LogService? log = null,
        Action<Action>? runFileWork = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(post);
        _files = files;
        _post = post;
        _log = log;
        _runFileWork = runFileWork;
    }

    public int Count => _kept.Count;

    public IReadOnlyList<string> DiskNotes => _diskNotes.ToArray();

    // Whether this very ranking is still held in memory: false once it has been
    // dropped or forgotten, or let go to stay within MaxKept.
    public bool Holds(AreaRanking ranking) => _kept.Exists(k => ReferenceEquals(k.Ranking, ranking));

    // The ranking held in memory for owner, or null. Asking counts as showing it.
    //
    // worldNow is the game data as it is (null: it can't be read, or the caller has
    // none to compare with). A ranking made in another world is dropped, from memory
    // and from disk, with the reason a read from disk would give: memory never
    // hands back what the file would be refused for. One held since before the
    // set's load in hand began and that can't be compared now (no world in hand) is
    // not handed back either, and stays for the next ask.
    public AreaRanking? Get(SimulatorOwner owner, RankingWorld? worldNow = null)
    {
        int at = _kept.FindIndex(k => k.Owner == owner);
        if (at < 0) return null;
        (SimulatorOwner, AreaRanking Ranking) found = _kept[at];
        // worldNow is the active set's. Asked for a pairing of another set, it says
        // nothing about that set's tables.
        bool ofActiveSet = _activeSet is null || string.Equals(owner.Set, _activeSet, StringComparison.OrdinalIgnoreCase);
        if (ofActiveSet && worldNow is not null && found.Ranking.World is { } made)
        {
            if (worldNow.DiffersFrom(made) is { } why)
            {
                _epoch++;
                _kept.RemoveAt(at);
                _madeBeforeReload.Remove(found.Ranking);
                Enqueue(() => _files.Delete(owner));
                Note($"the saved area ranking for {owner.Label} was dropped: {why}");
                return null;
            }
            _madeBeforeReload.Remove(found.Ranking);
        }
        else if (_madeBeforeReload.TryGetValue(found.Ranking, out _))
        {
            return null;
        }
        _kept.RemoveAt(at);
        _kept.Add(found);
        return found.Ranking;
    }

    // The rankings held since before their set's load in hand began, and not yet
    // compared with it. By reference: a ranking keeps its mark through a rename,
    // and one let go from memory takes its mark with it.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<AreaRanking, object> _madeBeforeReload = new();
    private static readonly object Marked = new();

    // Keep owner's ranking, just made, in place of any it had, in memory and on
    // disk. Returns the pairing whose ranking was let go from memory to stay within
    // MaxKept, or null; its file stays.
    public SimulatorOwner? Keep(SimulatorOwner owner, AreaRanking ranking)
    {
        ArgumentNullException.ThrowIfNull(ranking);
        SimulatorOwner? dropped = Hold(owner, ranking);
        if (HasFile(owner))
            Enqueue(() =>
            {
                (string? error, long bytes) = _files.Write(owner, ranking);
                _post(() => Wrote(owner, ranking, error, bytes));
            });
        return dropped;
    }

    // A pairing with both a set and a character has a file. Whether its names are
    // fit to make a path is the files' to judge, and to log when they are not.
    private static bool HasFile(SimulatorOwner owner) => owner.Set is { Length: > 0 } && owner.Character is not null;

    private SimulatorOwner? Hold(SimulatorOwner owner, AreaRanking ranking)
    {
        _kept.RemoveAll(k => k.Owner == owner);
        _kept.Add((owner, ranking));
        if (_kept.Count <= MaxKept) return null;
        SimulatorOwner dropped = _kept[0].Owner;
        _kept.RemoveAt(0);
        return dropped;
    }

    // A ranking that can't be written is still held and listed; it will not be there
    // after a restart. Said once, since the cause (a full disk, a folder that can't
    // be written) will be the same for the next.
    private void Wrote(SimulatorOwner owner, AreaRanking ranking, string? error, long bytes)
    {
        if (error is null)
        {
            if (bytes <= 0) return;
            Note($"saved the area ranking for {owner.Label} ({ranking.Rows.Count} options, {bytes / 1024} KB)");
            return;
        }
        string line = $"the area ranking for {owner.Label} could not be saved ({error}); it is kept until the client closes";
        Remember(line);
        if (_writeFailureSaid) return;
        _writeFailureSaid = true;
        _log?.Warn("Simulator", line + ". Further failures to save a ranking are not logged.");
    }

    // Look on disk for owner's ranking, which memory does not hold: the first time a
    // pairing is shown since the client started, or since its ranking was let go
    // from memory. worldNow is the game data as it is; a ranking made in another is
    // dropped. found is called on the UI thread, with the ranking now held in memory
    // or with null (no file, or one that was dropped), unless a ranking was made or
    // read for owner in the meantime, which then stands.
    //
    // A read is still out when it comes back, and anything may have been forgotten
    // or renamed while it was: a re-rank started, the set's data replaced. The
    // delete or move that went with that is queued behind the read, so the read
    // found the file as it was before, and handing that back would put a forgotten
    // ranking into memory and onto the list. _epoch moves with every such change;
    // a read that comes back to another epoch is thrown away and asked again, which
    // by the order of the queue sees the files as the change left them.
    //
    // A read that comes back after a new load of its set began (LoadsBegun) was
    // compared with the world of a load that is gone. It is thrown away and not
    // asked again: its file is as it was, and whoever shows the pairing next asks
    // with the world as it then is.
    public void Load(SimulatorOwner owner, RankingWorld worldNow, Action<AreaRanking?> found)
    {
        ArgumentNullException.ThrowIfNull(worldNow);
        ArgumentNullException.ThrowIfNull(found);
        if (!HasFile(owner)) return;
        long epoch = _epoch;
        long loadsBegun = LoadsBegun(owner.Set);
        Enqueue(() =>
        {
            AreaRankingFiles.ReadResult read = _files.Read(owner, worldNow);
            _post(() =>
            {
                if (read.Dropped is { } why)
                    Note($"the saved area ranking for {owner.Label} was dropped: {why}");
                if (loadsBegun != LoadsBegun(owner.Set)) return;
                if (epoch != _epoch)
                {
                    Load(owner, worldNow, found);
                    return;
                }
                if (_kept.Any(k => k.Owner == owner)) return;
                if (read.Ranking is { } ranking)
                {
                    Hold(owner, ranking);
                    Remember($"read the saved area ranking for {owner.Label} (made {ranking.MadeAt.ToLocalTime():yyyy-MM-dd HH:mm} at L{ranking.Level}, {ranking.Rows.Count} options)");
                }
                found(read.Ranking);
            });
        });
    }

    // A re-rank starts: owner's ranking is replaced from here on, as its list is.
    public void Forget(SimulatorOwner owner)
    {
        _epoch++;
        _kept.RemoveAll(k => k.Owner == owner);
        Enqueue(() => _files.Delete(owner));
    }

    // A character is now known as to (it was renamed or moved to another BBS, or
    // the default profile was given a name): its rankings on every set go with it,
    // in memory and on disk. Any kept under the new name were another character's,
    // which that name no longer is, and are dropped; nothing is left under the old
    // name for the next character to be given it. Returns how many held in memory
    // were carried.
    //
    // A name that only changes its capitals is the same character to memory, which
    // matches names without case, and carries nothing. Its file is still moved: a
    // file system that tells the two spellings apart would otherwise keep it under
    // the old one, where it is not looked for again.
    public int RenameCharacter(string? from, string? to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal)) return 0;
        _epoch++;
        bool capitalsOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
        if (!capitalsOnly)
            _kept.RemoveAll(k => string.Equals(k.Owner.Character, to, StringComparison.OrdinalIgnoreCase));
        int carried = 0;
        for (int i = 0; i < _kept.Count; i++)
        {
            if (!string.Equals(_kept[i].Owner.Character, from, StringComparison.OrdinalIgnoreCase)) continue;
            _kept[i] = (_kept[i].Owner with { Character = to }, _kept[i].Ranking);
            if (!capitalsOnly) carried++;
        }
        Enqueue(() => _files.MoveCharacter(from, to));
        return carried;
    }

    // A character's profile was deleted: so are its rankings, on every set. A
    // profile made later under that name is another character.
    public void ForgetCharacter(string? character)
    {
        if (character is null) return;
        _epoch++;
        _kept.RemoveAll(k => string.Equals(k.Owner.Character, character, StringComparison.OrdinalIgnoreCase));
        Enqueue(() => _files.MoveCharacter(character, null));
    }

    // A BBS was renamed: every character under it is known by the new name, loaded
    // or not. The loaded one has told of itself already (RenameCharacter) and is
    // under the new name by now, which is why nothing found there is dropped: a BBS
    // is only renamed to a name no BBS has.
    //
    // A name that only changes its capitals still moves the files, as it does for a
    // character (RenameCharacter).
    public void RenameBbs(string oldBbs, string newBbs)
    {
        if (string.Equals(oldBbs, newBbs, StringComparison.Ordinal)) return;
        _epoch++;
        string under = oldBbs + "/";
        for (int i = 0; i < _kept.Count; i++)
        {
            if (_kept[i].Owner.Character is not { } c || !c.StartsWith(under, StringComparison.OrdinalIgnoreCase)) continue;
            _kept[i] = (_kept[i].Owner with { Character = SimulatorOwner.CharacterKey(newBbs, c[under.Length..]) }, _kept[i].Ranking);
        }
        Enqueue(() => _files.MoveBbs(oldBbs, newBbs));
    }

    // A BBS was removed with every profile under it: so are their rankings.
    public void ForgetBbs(string bbs)
    {
        _epoch++;
        string under = bbs + "/";
        _kept.RemoveAll(k => k.Owner.Character is { } c && c.StartsWith(under, StringComparison.OrdinalIgnoreCase));
        Enqueue(() => _files.DeleteBbs(bbs));
    }

    // The set last told active.
    private string? _activeSet;

    // How many loads of each set have begun. A read from disk is compared with the
    // world of the load it was asked in; see Load.
    private readonly Dictionary<string, long> _loadsBegun = new(StringComparer.OrdinalIgnoreCase);

    private long LoadsBegun(string? set) => set is not null && _loadsBegun.TryGetValue(set, out long n) ? n : 0;

    // The active game-data set is now set: a load of it has just begun, by a switch
    // to it or by the same name again (the set read in afresh). Its tables may be
    // the files a ranking held for it was made on or other ones, and nothing says
    // which, so nothing is deleted: each ranking held for the set is marked, and
    // compared with the new load's world when it is next asked for (Get).
    //
    // The one kind that goes here is a ranking with no record of the tables it was
    // made on (it was made while the load could not be stamped, and was never
    // written). There is nothing to compare it by, so it cannot outlive the load it
    // was made in: it goes at every new load of its set, a switch back to the set
    // as much as the same name again. The set's files may have been replaced while
    // another set was active, and such a ranking would be listed over them. Returns
    // how many of those went from memory.
    public int NoteActiveSet(string? set)
    {
        _activeSet = set;
        if (set is not null) _loadsBegun[set] = LoadsBegun(set) + 1;
        foreach ((SimulatorOwner owner, AreaRanking ranking) in _kept)
            if (ranking.World is not null && string.Equals(owner.Set, set, StringComparison.OrdinalIgnoreCase))
                _madeBeforeReload.AddOrUpdate(ranking, Marked);
        return _kept.RemoveAll(k => k.Ranking.World is null && string.Equals(k.Owner.Set, set, StringComparison.OrdinalIgnoreCase));
    }

    // The data of set was replaced (its deletion, a copy or move of the user's
    // edits into or out of it): drop every character's ranking made on it, from
    // memory and from disk. Returns how many went from memory.
    //
    // A Simulator that is listing one of them is not told from here. Each of those
    // changes, made to the active set, also reads the set in again or switches away
    // from it, and that is what takes the ranking off the list.
    public int ForgetSet(string? set)
    {
        _epoch++;
        Enqueue(() =>
            {
                int deleted = _files.DeleteSet(set);
                if (deleted > 0)
                    _post(() => Note($"deleted {deleted} saved area ranking(s) made on game data '{set}': its data was replaced"));
            });
        return _kept.RemoveAll(k => string.Equals(k.Owner.Set, set, StringComparison.OrdinalIgnoreCase));
    }

    // Once each client start: delete the ranking files too old to be read back, for
    // every set, BBS and profile, loaded or not. It runs on the file queue like
    // every other piece of file work, so it can't fall between a write and the
    // delete or move asked after it. The files of the rankings held in memory are
    // left: one on show is not taken from under the list.
    public void SweepOld()
    {
        string[] held = _kept.Select(k => AreaRankingFiles.FileFor(k.Owner)).OfType<string>().ToArray();
        Enqueue(() =>
        {
            var skipped = new List<string>();
            int deleted = _files.SweepOld(held, skipped.Add);
            _post(() =>
            {
                // One line for the pass: with another client sweeping at the same
                // moment nearly every old file is one of these.
                if (skipped.Count > 0)
                    _log?.Log(LogSeverity.Debug, "Simulator",
                        $"the sweep of saved area rankings left {skipped.Count} file(s) it could not read, judge or take; the first: {skipped[0]}");
                if (deleted > 0)
                    Note($"deleted {deleted} saved area ranking(s) made more than {AreaRankingFiles.MaxAge.Days} days ago");
            });
        });
    }

    // File work never throws into the client: AreaRankingFiles reports what it
    // can't do, and anything else is logged here and the queue carries on.
    private void Enqueue(Action work)
    {
        void Guarded()
        {
            try { work(); }
            catch (Exception ex)
            {
                _log?.Warn("Simulator", $"area ranking file work failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        if (_runFileWork is { } run)
        {
            run(Guarded);
            return;
        }
        lock (_ioLock)
            _io = _io.ContinueWith(_ => Guarded(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void Note(string line)
    {
        Remember(line);
        _log?.Info("Simulator", line);
    }

    private void Remember(string line)
    {
        _diskNotes.Add($"{DateTime.Now:HH:mm:ss} {line}");
        if (_diskNotes.Count > MaxDiskNotes) _diskNotes.RemoveAt(0);
    }
}
