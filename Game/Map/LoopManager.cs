using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Per-game-data-set catalogue of saved navigation loops. CRUD over the
// JSON files under Data/game data/{set}/Loops/, plus the builder helpers
// the Navigation window needs to turn a sequence of clicked rooms into a
// runnable loop (gap-fill BFS for non-adjacent clicks, three-tier fallback
// for ambiguous click orders).
//
// Lifecycle: the catalogue is keyed by the active game-data set (the
// realm's MDB), so the same loop library follows the realm across every
// BBS / character that points at that set. When the active set changes,
// LoadAll reloads from disk. Mutating methods (Save, Delete) update the
// in-memory cache and fire LoopsChanged so the UI can refresh. Another client
// running on the same set writes to the same folder, so NavFolderWatcher follows
// what it saved, renamed, moved or deleted.
//
// Gap-fill: when the user clicks rooms that aren't directly connected,
// waypoint expansion runs BfsMapper.FindPath between consecutive clicks
// and inlines the intermediate hops. Surfaces a per-segment error when a
// segment can't be pathed — the editor disables Save until the user
// removes the bad click.
public sealed class LoopManager
{
    private readonly BfsMapper _bfs;
    private readonly RoomGraphManager _graph;
    private readonly LogService? _log;

    private string? _setName;
    private readonly Dictionary<string, Loop> _loops = new(StringComparer.OrdinalIgnoreCase);

    // Game-data set that Loops is currently sourced from. null when no set is active.
    public string? SetName => _setName;

    // Loaded loops, ordered alphabetically by name.
    public IReadOnlyList<Loop> Loops =>
        _loops.Values.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    // Fires after any mutation to Loops (load, save, delete, an outside change
    // taken in).
    public event Action? LoopsChanged;

    private readonly SharedFolderStamp _stamp = new();
    private readonly HashSet<string> _unreadable = new(StringComparer.Ordinal);

    // When the folder was last re-read because another client had changed it; null
    // until it has been.
    public DateTimeOffset? LastOutsideReadAt { get; private set; }

    // Names of the loops this client is running, has paused or has staged. Plugged in
    // by the app so a re-read can say which of them was changed under it; it never
    // swaps or drops the copy the run holds.
    public Func<IReadOnlyCollection<string>>? NamesInUse { get; set; }

    // Which loops the loaded character has favourited is the profile's to keep, not
    // the loop file's. The store that keeps it plugs in here: the lookup stamps
    // Loop.Favorite on every loop this catalogue hands out, and the setter takes a
    // change back.
    private Func<string, bool>? _isFavorite;
    private Action<string, bool>? _setFavorite;

    public void AttachFavorites(Func<string, bool> isFavorite, Action<string, bool> setFavorite)
    {
        _isFavorite = isFavorite ?? throw new ArgumentNullException(nameof(isFavorite));
        _setFavorite = setFavorite ?? throw new ArgumentNullException(nameof(setFavorite));
        RefreshFavorites();
    }

    // Re-read every loop's favourite flag (another character loaded, or the list
    // changed) and tell the views.
    public void RefreshFavorites()
    {
        foreach (Loop loop in _loops.Values) Stamp(loop);
        LoopsChanged?.Invoke();
    }

    // Add the loop named name to the loaded character's favourites, or take it out.
    public void SetFavorite(string name, bool favorite)
    {
        if (_setFavorite is null) return;
        _setFavorite(name, favorite);
        RefreshFavorites();
    }

    private void StampFavorite(Loop loop)
    {
        if (_isFavorite is { } isFavorite) loop.Favorite = isFavorite(loop.Name);
    }

    // How a loop is run (only attack in lair rooms, the wait to enter lairs) is the
    // loaded character's to keep as well, by loop name. The store that keeps it plugs
    // in here: own gives the character's pair for a loop, or null when it has set
    // neither; keep takes a changed pair, with the pair the loop file holds (what the
    // character ran the loop with until now); forget drops a deleted loop's.
    private Func<string, LoopOwnSettings?>? _ownSettings;
    private Action<string, LoopOwnSettings, LoopOwnSettings>? _keepSettings;
    private Action<string>? _forgetSettings;

    // The loaded character's settings for a loop changed: the loop's name, or null
    // when every loop's may have (another character loaded). For whoever holds a loop
    // apart from the catalogue, a run above all.
    public event Action<string?>? SettingsChanged;

    public void AttachSettings(Func<string, LoopOwnSettings?> own,
        Action<string, LoopOwnSettings, LoopOwnSettings> keep, Action<string> forget)
    {
        _ownSettings = own ?? throw new ArgumentNullException(nameof(own));
        _keepSettings = keep ?? throw new ArgumentNullException(nameof(keep));
        _forgetSettings = forget ?? throw new ArgumentNullException(nameof(forget));
        RefreshSettings();
    }

    // Re-read every loop's settings for the character now loaded.
    public void RefreshSettings()
    {
        foreach (Loop loop in _loops.Values) StampSettings(loop);
        SettingsChanged?.Invoke(null);
    }

    // Set the loaded character's settings for the loop named name, live. The loop
    // file is not written: it is every character's, and these are this one's.
    public void SetSettings(string name, LoopOwnSettings settings)
    {
        if (!_loops.TryGetValue(name, out Loop? held)) return;
        held.OwnSettings = settings;
        KeepSettingsOf(held);
    }

    // Give loops held apart from the catalogue (the one a run started from, its
    // snapshot of the last run) the character's settings for the saved loop of their
    // name: only those named name, or all of them when it is null. A loop that was
    // never saved has none kept and is left as it is.
    public void CopySettingsTo(IEnumerable<Loop> copies, string? name)
    {
        ArgumentNullException.ThrowIfNull(copies);
        foreach (Loop copy in copies)
        {
            if (name is not null && !string.Equals(copy.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (_loops.TryGetValue(copy.Name, out Loop? held) && !ReferenceEquals(held, copy))
                copy.OwnSettings = held.OwnSettings;
        }
    }

    // The character's own pair where it has set one, else the pair the file holds.
    private void StampSettings(Loop loop) =>
        loop.OwnSettings = _ownSettings?.Invoke(loop.Name) ?? loop.SharedSettings;

    // loop carries the settings the character wants for it. Kept for the character
    // only when they are not the ones it runs the loop with already, so a character
    // that never changes a loop's settings goes on reading the file's.
    private void KeepSettingsOf(Loop loop)
    {
        LoopOwnSettings running = _ownSettings?.Invoke(loop.Name) ?? loop.SharedSettings;
        if (loop.OwnSettings == running) return;
        _keepSettings?.Invoke(loop.Name, loop.OwnSettings, loop.SharedSettings);
        SettingsChanged?.Invoke(loop.Name);
    }

    // What the file being written holds of the two settings is what the file of that
    // name held: a save can hand in a fresh object (a built loop, a received one, a
    // running loop's snapshot) that never read the file.
    private void KeepSharedSettings(Loop loop)
    {
        if (_loops.TryGetValue(loop.Name, out Loop? held) && !ReferenceEquals(held, loop))
            loop.SharedSettings = held.SharedSettings;
    }

    private void Stamp(Loop loop)
    {
        StampFavorite(loop);
        StampSettings(loop);
    }

    public LoopManager(BfsMapper bfs, RoomGraphManager graph, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(graph);
        _bfs = bfs;
        _graph = graph;
        _log = log;
    }

    // ----- Game-data-set lifecycle -----------------------------------

    // A set's loops read ahead of its switch, off the UI thread: LoadAll runs inside
    // the switch on the UI thread, and 639 loop files held it 0.9 s while three
    // clients loaded their profiles (Hang-20261101-201809). LoadAll takes each one
    // whose file is unchanged since it was read and reads the rest itself.
    private readonly object _preloadGate = new();
    private (string Set, Dictionary<string, (SharedFolderStamp.FileMark Mark, Loop Loop)> Loops)? _preloaded;

    public Task PreloadAsync(string setName) => Task.Run(() =>
    {
        if (!PathGuard.Usable(AppPaths.GameDataRoot, setName)) return;
        string folder = AppPaths.GameDataSetLoopsFolder(setName);
        if (!Directory.Exists(folder)) return;
        Dictionary<string, (SharedFolderStamp.FileMark, Loop)> read = new(StringComparer.Ordinal);
        foreach ((string path, SharedFolderStamp.FileMark mark) in NavTreeScan.Walk(folder, LoopFileSuffix).FilesWith(LoopFileSuffix))
        {
            try
            {
                if (JsonStore.Load<Loop>(path) is { } loop) read[path] = (mark, loop);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // LoadAll reads it again and reports it.
            }
        }
        lock (_preloadGate) _preloaded = (setName, read);
    });

    // The preloaded loop read from path while it held mark, taken once.
    private static Loop? Preloaded(Dictionary<string, (SharedFolderStamp.FileMark Mark, Loop Loop)>? preloaded,
        IReadOnlyDictionary<string, SharedFolderStamp.FileMark> seen, string path)
    {
        string full = Path.GetFullPath(path);
        return preloaded is not null && preloaded.Remove(full, out (SharedFolderStamp.FileMark Mark, Loop Loop) early)
            && seen.TryGetValue(full, out SharedFolderStamp.FileMark now) && now == early.Mark
            ? early.Loop : null;
    }

    // Rebuild the in-memory cache from disk for setName. Pass null to clear
    // (no set active). Idempotent on no-op transitions — calling with the
    // same name twice still rereads because the user may have hand-edited a
    // loop file between calls.
    public void LoadAll(string? setName)
    {
        _loops.Clear();
        _unreadable.Clear();
        // A name that can't be a set's folder is no set: nothing is read for it, and
        // nothing saved while it stands goes anywhere.
        if (!string.IsNullOrWhiteSpace(setName) && !PathGuard.Usable(AppPaths.GameDataRoot, setName))
        {
            PathGuard.Refuse("Loading a set's loops", "game-data set name", setName);
            setName = null;
        }
        // Blank is no set either; held as such, a save went to a folder of no name.
        _setName = string.IsNullOrWhiteSpace(setName) ? null : setName;

        if (string.IsNullOrWhiteSpace(setName))
        {
            _stamp.Accept(new Dictionary<string, SharedFolderStamp.FileMark>());
            LoopsChanged?.Invoke();
            return;
        }

        string folder = AppPaths.GameDataSetLoopsFolder(setName);
        if (!Directory.Exists(folder))
        {
            _stamp.Accept(new Dictionary<string, SharedFolderStamp.FileMark>());
            _log?.Info("Loops", $"no loops folder for set '{setName}'; empty catalogue.");
            LoopsChanged?.Invoke();
            return;
        }

        // One-shot migration: rename pre-suffix .json loop files to
        // .loop so subsequent loads find them. Skips lair files.
        MigrateLegacyJsonFilesIfPresent(folder);
        // Marked before the files are read, so a file another client writes meanwhile
        // shows on the next poll instead of being taken as already read.
        IReadOnlyDictionary<string, SharedFolderStamp.FileMark> seen =
            NavTreeScan.Walk(folder, LoopFileSuffix).FilesWith(LoopFileSuffix);
        _stamp.Accept(seen);

        Dictionary<string, (SharedFolderStamp.FileMark Mark, Loop Loop)>? preloaded;
        lock (_preloadGate)
        {
            preloaded = _preloaded is { } p && string.Equals(p.Set, setName, StringComparison.OrdinalIgnoreCase) ? p.Loops : null;
            _preloaded = null;
        }

        int loaded = 0;
        int failed = 0;
        int upgraded = 0;
        int readAhead = 0;
        // Recurse: loops may live in user-created sub-folders under the
        // Loops root. The relative directory is the loop's Folder.
        foreach (string path in Directory.EnumerateFiles(folder, "*" + LoopFileSuffix, SearchOption.AllDirectories))
        {
            try
            {
                Loop? loop = Preloaded(preloaded, seen, path);
                if (loop is not null) readAhead++;
                else loop = JsonStore.Load<Loop>(path);
                // Schema-validate: a JSON file dropped in this folder
                // that isn't a Loop (or one we can't deserialise) is
                // silently skipped — the user can hand-drop .loop
                // files without us blowing up.
                if (loop is null || string.IsNullOrWhiteSpace(loop.Name))
                {
                    failed++;
                    NavCatalogueReread.NoteSkipped(_unreadable, seen, path);
                    continue;
                }
                if (UpgradeIfNeeded(loop)) upgraded++;
                loop.Folder = NavFolders.RelativeFolder(folder, path);
                Stamp(loop);
                _loops[loop.Name] = loop;
                loaded++;
            }
            catch
            {
                failed++;
                NavCatalogueReread.NoteSkipped(_unreadable, seen, path);
            }
        }

        string detail = failed == 0
            ? $"Loaded {loaded} loop(s) for set '{setName}'"
            : $"Loaded {loaded} loop(s) for set '{setName}' ({failed} malformed file(s) skipped)";
        if (upgraded > 0) detail += $"; {upgraded} legacy loop(s) upgraded to v{Loop3Schema} in memory";
        if (readAhead > 0) detail += $"; {readAhead} read ahead of the switch";
        _log?.Info("Loops", detail + ".");
        LoopsChanged?.Invoke();
    }

    // Bumped by every read or write of the folder this client makes. A walk of the
    // tree that began before the bump may have seen it half done, so its result is
    // dropped (NavFolderWatcher).
    internal long Generation => _stamp.Generation;

    // True when the folder now holds loop files other than the ones this client
    // holds: another client saved, edited, renamed, moved or deleted a loop.
    internal bool DiffersFrom(NavTreeScan scan) =>
        _setName is not null && _stamp.Differs(scan.FilesWith(LoopFileSuffix));

    // Take in at once what another client did to the Loops folder, with no waiting
    // for it to settle: a check the caller is about to make against the catalogue
    // (the editor's name clash) must see it. Returns whether anything was re-read.
    public bool TakeInOutsideChanges()
    {
        if (_setName is null) return false;
        NavTreeScan scan = NavTreeScan.Walk(AppPaths.GameDataSetLoopsFolder(_setName), LoopFileSuffix);
        if (!scan.Complete || PlanOutside(scan) is not { } plan) return false;
        plan()();
        return true;
    }

    // Plan the re-read of the loop files in scan, or null when they are the ones held.
    // The plan is two steps. The first reads and parses the changed files from copies,
    // so it can run on any thread, and returns the second, which swaps the catalogue
    // over and must run on the UI thread, with nothing of this client's own written in
    // between. A file that can't be read leaves the copy held where it is. A loop a
    // run here holds is never touched: its object stays the one the run started from;
    // only the catalogue's entry moves on.
    internal Func<Action>? PlanOutside(NavTreeScan scan)
    {
        if (_setName is null || !DiffersFrom(scan)) return null;
        string root = AppPaths.GameDataSetLoopsFolder(_setName);
        IReadOnlyDictionary<string, SharedFolderStamp.FileMark> files = scan.FilesWith(LoopFileSuffix);
        Dictionary<string, SharedFolderStamp.FileMark> known = new(_stamp.Known);
        Dictionary<string, Loop> heldByPath = new(StringComparer.Ordinal);
        foreach (Loop l in _loops.Values) heldByPath[PathOf(root, l)] = l;
        HashSet<string> heldPaths = new(heldByPath.Keys, StringComparer.Ordinal);
        HashSet<string> reported = new(_unreadable);
        return () =>
        {
            Dictionary<string, NavCatalogueReread.Outcome<Loop>> outcomes =
                NavCatalogueReread.Preread(files, known, heldPaths, reported, path => ReadOutsideFile(root, path));
            return () => ApplyOutside(root, files, heldByPath, outcomes);
        };
    }

    private void ApplyOutside(string root, IReadOnlyDictionary<string, SharedFolderStamp.FileMark> files,
        IReadOnlyDictionary<string, Loop> heldByPath, IReadOnlyDictionary<string, NavCatalogueReread.Outcome<Loop>> outcomes)
    {
        NavReread<Loop> found = NavCatalogueReread.Run(_stamp.Known, files, _loops, heldByPath,
            l => l.Name,
            (a, b) => NavCatalogueReread.SameRecord(a, a.Folder, b, b.Folder),
            outcomes,
            path => ReadOutsideFile(root, path),
            _unreadable,
            (path, why) => _log?.Warn("Loops", $"loop file '{path}' changed on disk but can't be read ({why}); keeping the copy held."));
        _stamp.Accept(files);
        if (!found.AnyItemChanged) return;

        foreach (string name in found.Added.Concat(found.Changed)) Stamp(found.Fresh[name]);
        _loops.Clear();
        foreach ((string name, Loop loop) in found.Fresh) _loops[name] = loop;
        LastOutsideReadAt = DateTimeOffset.UtcNow;
        _log?.Info("Loops", NavCatalogueReread.Summary("loops", found));
        NoteRunsHere(found);
        LoopsChanged?.Invoke();
    }

    private static string PathOf(string root, Loop loop) =>
        Path.GetFullPath(Path.Combine(NavFolders.ToDirectory(root, loop.Folder), SafeFileName(loop.Name)));

    // A new catalogue entry for the file at path, or null when it isn't a loop. Runs
    // on a worker thread: it touches nothing of the catalogue (the favourite flag is
    // stamped on afterwards, on the UI thread) and UpgradeIfNeeded only logs.
    private Loop? ReadOutsideFile(string root, string path)
    {
        Loop? loop = JsonStore.Load<Loop>(path);
        if (loop is null || string.IsNullOrWhiteSpace(loop.Name)) return null;
        UpgradeIfNeeded(loop);
        loop.Folder = NavFolders.RelativeFolder(root, path);
        return loop;
    }

    private void NoteRunsHere(NavReread<Loop> found)
    {
        if (NamesInUse?.Invoke() is not { Count: > 0 } inUse) return;
        foreach (string name in found.Changed.Concat(found.Removed))
        {
            if (!inUse.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            string what = found.Removed.Contains(name, StringComparer.OrdinalIgnoreCase)
                ? "deleted or renamed on disk"
                : "edited on disk";
            _log?.Info("Loops", $"loop '{name}' was {what}; the run here keeps going from the copy it started with.");
        }
    }

    // Lookup by name. Returns null when the loop isn't in the catalogue.
    public Loop? Get(string name) =>
        _loops.TryGetValue(name, out Loop? loop) ? loop : null;

    // "<name> 2", or the next number up that no loop has: what a loop is saved as
    // when its own name belongs to another loop, so that one is never overwritten.
    // Names are matched without case, as the catalogue matches them.
    public string NumberedName(string name)
    {
        for (int n = 2; ; n++)
        {
            string candidate = $"{name} {n}";
            if (!_loops.ContainsKey(candidate)) return candidate;
        }
    }

    // The same rule read the other way: the number n when candidate is "<name> <n>"
    // exactly as NumberedName writes it (one space, then digits only, 2 or more, no
    // leading zero, the name matched without case), and 0 for any other name. So a
    // loop the user named "<name> 2a", "<name>2" or "<name> (old)" is not taken for
    // a numbered copy. The two must change together.
    public static int NumberedCopyOf(string candidate, string name)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(name);
        if (candidate.Length <= name.Length + 1
            || !candidate.StartsWith(name, StringComparison.OrdinalIgnoreCase)
            || candidate[name.Length] != ' ')
            return 0;
        ReadOnlySpan<char> digits = candidate.AsSpan(name.Length + 1);
        if (digits[0] == '0') return 0;
        foreach (char c in digits)
            if (c is < '0' or > '9') return 0;
        return int.TryParse(digits, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int n) && n >= 2 ? n : 0;
    }

    // The loop named name, then the loops NumberedName has saved beside it, lowest
    // number first: every loop an earlier copy of the same loop may be held as.
    public IEnumerable<Loop> WithNumberedCopies(string name)
    {
        if (Get(name) is { } named) yield return named;
        foreach ((Loop Loop, int Number) copy in _loops.Values
                     .Select(l => (Loop: l, Number: NumberedCopyOf(l.Name, name)))
                     .Where(p => p.Number > 0)
                     .OrderBy(p => p.Number)
                     .ToList())
            yield return copy.Loop;
    }

    // Resolve a typed loop name the way the remote @loop commands do: an exact
    // (case-insensitive) name wins outright; otherwise every loop whose name holds
    // every typed word, in any order ("godfrey bank" → "Bank of Godfrey Loop").
    // One result is a match; several are an ambiguity for the caller to report;
    // none is a miss.
    // Not RoomSearchService.FindAllByName: the exact step here is the loop table's own
    // key, which may differ from the name a loop file carries.
    public IReadOnlyList<Loop> FindByName(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<Loop>();
        if (_loops.TryGetValue(query.Trim(), out Loop? exact)) return new[] { exact };
        Loop[] found = Loops.Where(l => RoomSearchService.NameMatchesTokens(l.Name, query)).ToArray();
        if (found.Length > 0) return found;
        // Typed chat drops apostrophes — "kings road" should still find "King's Road",
        // which the word match splits into "King" + "s".
        string bare = query.Replace("'", "");
        return Loops.Where(l => RoomSearchService.NameMatchesTokens(l.Name.Replace("'", ""), bare)).ToArray();
    }

    // Deserialize a single loop file the user browsed to (may live outside the
    // active set's folder). Returns null if it isn't a valid loop. Doesn't add
    // it to the catalogue — the caller decides what to do with it.
    public Loop? LoadFile(string path)
    {
        try
        {
            Loop? loop = JsonStore.Load<Loop>(path);
            if (loop is null || string.IsNullOrWhiteSpace(loop.Name)) return null;
            UpgradeIfNeeded(loop);
            StampSettings(loop);
            return loop;
        }
        catch
        {
            return null;
        }
    }

    // Persist loop under GameDataSetLoopsFolder. No-op when no set is active.
    // False when nothing was saved: no set, or a folder that can't be one
    // (NavFolders.WhyNotFolder), refused with the reason in the log.
    //
    // The write is split. The loop itself (rooms, commands, delays, the per-room
    // marks, notes) goes to the file, which every character on the game data reads.
    // Its OnlyAttackInLairRooms and LairEntryDebuff are what the loaded character
    // wants for it and are kept for that character alone.
    public bool Save(Loop loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        if (string.IsNullOrWhiteSpace(loop.Name))
            throw new ArgumentException("Loop name is required.", nameof(loop));
        if (_setName is null) return false;

        string root = AppPaths.GameDataSetLoopsFolder(_setName);
        if (WhyNotFolder(loop.Folder) is { } whyNot)
        {
            _log?.Warn("Loops", $"loop '{loop.Name}' not saved: {whyNot}");
            return false;
        }
        // Saving normalises to the current schema version so a v2
        // loop edited in the new editor lands on disk as v3 cleanly.
        loop.SchemaVersion = Loop3Schema;
        loop.Folder = NavFolders.Normalize(loop.Folder);
        // A loop name is unique set-wide, so a folder change is a move:
        // any stale file for this name (in any folder) is removed after
        // the write so we don't leave a duplicate behind.
        string dir = NavFolders.ToDirectory(root, loop.Folder);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, SafeFileName(loop.Name));
        KeepSharedSettings(loop);
        // Written (atomically) before the stale copy goes: another client reading the
        // folder between the two sees the loop twice, never not at all.
        JsonStore.Save(path, loop);
        _stamp.NoteWritten(path);
        DeleteFileForName(root, loop.Name, keep: path);
        // A save may hand in a fresh object (a running loop's snapshot); it is
        // still the same favourite it was.
        StampFavorite(loop);
        _loops[loop.Name] = loop;
        KeepSettingsOf(loop);
        LoopsChanged?.Invoke();
        return true;
    }

    // Save a loop to an explicit path chosen via a Save-As dialog (rename + pick a
    // location). The loop's Name is taken from the file name so the on-disk name and
    // the catalogue key agree — Delete and the rail list both derive from Name. A
    // file that lands under the active set's Loops folder is indexed into the
    // catalogue (shows in the list, runnable); one saved elsewhere is a plain export
    // the "Load loop…" picker can still read back.
    public void SaveToPath(Loop loop, string path)
    {
        ArgumentNullException.ThrowIfNull(loop);
        if (string.IsNullOrWhiteSpace(path)) return;

        string name = Path.GetFileNameWithoutExtension(path);
        if (!string.IsNullOrWhiteSpace(name)) loop.Name = name;
        loop.SchemaVersion = Loop3Schema;

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        string root = string.IsNullOrWhiteSpace(_setName) ? "" : AppPaths.GameDataSetLoopsFolder(_setName);
        bool catalogued = root.Length > 0 && IsUnder(path, root);
        if (catalogued) KeepSharedSettings(loop);
        JsonStore.Save(path, loop);

        if (catalogued)
        {
            _stamp.NoteWritten(path);
            loop.Folder = NavFolders.RelativeFolder(root, path);
            // What is saved this way sets neither of the character's two settings
            // (the estimator has none), so a loop it replaces keeps them.
            Stamp(loop);
            _loops[loop.Name] = loop;
            _log?.Info("Loops", $"Saved loop '{loop.Name}' to {path}.");
            LoopsChanged?.Invoke();
        }
        else
        {
            _log?.Info("Loops", $"Exported loop '{loop.Name}' to {path} (outside the set's Loops folder; not catalogued).");
        }
    }

    // Whether filePath sits inside directory root (case-insensitive, normalised).
    private static bool IsUnder(string filePath, string root)
    {
        string full = Path.GetFullPath(filePath);
        string r = Path.GetFullPath(root);
        if (!r.EndsWith(Path.DirectorySeparatorChar)) r += Path.DirectorySeparatorChar;
        return full.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    // Move the loop named name into folder (empty = Loops root). No-op when
    // the loop isn't in the catalogue or no set is active.
    public bool Move(string name, string? folder)
    {
        if (Get(name) is not { } loop) return false;
        string target = NavFolders.Normalize(folder);
        if (string.Equals(NavFolders.Normalize(loop.Folder), target, StringComparison.OrdinalIgnoreCase))
            return false;
        // The catalogue's own object: a refused move leaves it where it was.
        string previous = loop.Folder;
        loop.Folder = target;
        if (Save(loop)) return true;
        loop.Folder = previous;
        return false;
    }

    // Why a loop can't be filed in folder, or null when it can (NavFolders.WhyNotFolder).
    // Null with no set active: nothing is saved then anyway.
    public string? WhyNotFolder(string? folder) =>
        _setName is null ? null : NavFolders.WhyNotFolder(AppPaths.GameDataSetLoopsFolder(_setName), folder);

    // Save loop under its new name, then drop the loop saved as oldName. The old one
    // goes only once the new one is written, so a save that is refused or throws
    // leaves it as it was. Where both names make one file name ("a/b" and "a_b"),
    // that file is the new loop's and stays.
    public bool SaveRenamed(Loop loop, string oldName)
    {
        ArgumentNullException.ThrowIfNull(loop);
        if (!Save(loop)) return false;
        if (!string.Equals(oldName, loop.Name, StringComparison.OrdinalIgnoreCase))
            Forget(oldName, keep: PathOf(AppPaths.GameDataSetLoopsFolder(_setName!), loop));
        return true;
    }

    // Delete the loop named name. No-op when not in the catalogue or no set
    // is active.
    public bool Delete(string name) => Forget(name, keep: null);

    private bool Forget(string name, string? keep)
    {
        if (_setName is null) return false;
        if (!_loops.Remove(name)) return false;

        _stamp.NoteHeldChanged();
        DeleteFileForName(AppPaths.GameDataSetLoopsFolder(_setName), name, keep);
        // A deleted loop's name is free for another; that one isn't a favourite,
        // and isn't run the way this one was.
        _setFavorite?.Invoke(name, false);
        _forgetSettings?.Invoke(name);
        LoopsChanged?.Invoke();
        return true;
    }

    // Delete the on-disk .loop file for name wherever it lives under root
    // (the name is unique set-wide). Best-effort — failures are logged.
    private void DeleteFileForName(string root, string name, string? keep = null)
    {
        if (!Directory.Exists(root)) return;
        string fileName = SafeFileName(name);
        foreach (string path in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
        {
            if (keep is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                File.Delete(path);
                _stamp.NoteGone(path);
            }
            catch (Exception ex)
            {
                _log?.Warn("Loops", $"failed to delete loop file '{path}': {ex.Message}");
            }
        }
    }

    // ----- builder helpers -------------------------------------------

    // Convenience wrapper around LoopExpander.Expand pre-bound to this
    // manager's BfsMapper, for callers that preview a loop's steps and
    // unreachable legs without running it.
    public (IReadOnlyList<LoopStep> Steps, IReadOnlyList<(RoomKey From, RoomKey To)> UnreachableSegments)
        ExpandWaypoints(IReadOnlyList<LoopWaypoint> waypoints, IRoomFilter? filter = null)
        => LoopExpander.Expand(waypoints, _bfs, _graph, filter, LoopLegMemo.Of(_graph));

    // The rooms a loop with these waypoints walks through, leg by leg, gaps left
    // where a leg doesn't route (LoopExpander.ResolveLegRoomKeys).
    public IReadOnlyList<RoomKey> ResolveRouteRoomKeys(IReadOnlyList<LoopWaypoint> waypoints, IRoomFilter? filter = null)
        => LoopExpander.ResolveLegRoomKeys(waypoints, _bfs, _graph, filter, LoopLegMemo.Of(_graph));

    // ----- internals -------------------------------------------------

    // Current schema version persisted on save.
    public const int Loop3Schema = 3;

    // Upgrade an in-memory Loop in place if its SchemaVersion is below the
    // current version. Returns true when an upgrade was applied so the
    // caller can surface a count in the load log.
    //
    // v1 → v3: file had IsCircular + flat Steps. Waypoints stays empty —
    // the v1 schema had no waypoint anchors to recover, so legacy loops
    // can't be re-expanded by avoid-list change until the user re-saves
    // them in the builder.
    //
    // v2 → v3: file had UserWaypoints + Steps. Copy UserWaypoints into
    // Waypoints with null commands; any mid-leg CommandLoopSteps in the
    // flat list are dropped (logged once) because v3 attaches commands to
    // waypoints, not arbitrary positions.
    //
    // The on-disk file isn't rewritten — upgrades are in-memory only so a
    // user who downgrades doesn't lose their old files. The next
    // user-driven Save persists the v3 form.
    private bool UpgradeIfNeeded(Loop loop)
    {
        if (loop.SchemaVersion >= Loop3Schema) return false;

        loop.Waypoints ??= new List<LoopWaypoint>();
        loop.Notes ??= string.Empty;

        // Pull legacy UserWaypoints out of the JsonExtensionData
        // bucket and rehydrate them as waypoints with no commands.
        // v2 also carried inline CommandLoopSteps in the flat Steps
        // list at arbitrary positions; v3 attaches commands to
        // waypoints, so mid-leg commands are silently dropped (with
        // a warning log) — the user can re-add via the editor.
        if (loop.LegacyFields is { } legacy
            && legacy.TryGetValue("UserWaypoints", out System.Text.Json.JsonElement uw)
            && uw.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (System.Text.Json.JsonElement entry in uw.EnumerateArray())
            {
                if (entry.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("Map", out System.Text.Json.JsonElement mapEl)) continue;
                if (!entry.TryGetProperty("Room", out System.Text.Json.JsonElement roomEl)) continue;
                if (!mapEl.TryGetInt32(out int map) || !roomEl.TryGetInt32(out int room)) continue;
                loop.Waypoints.Add(new LoopWaypoint(new RoomKey(map, room)));
            }
            int droppedCommands = CountLegacyCommandSteps(legacy);
            if (droppedCommands > 0)
                _log?.Warn("Loops",
                    $"v2 → v3 migration of '{loop.Name}': dropped {droppedCommands} mid-leg command step(s). " +
                    "Re-add via the loop editor to attach them to waypoints.");
        }
        loop.LegacyFields = null;
        loop.SchemaVersion = Loop3Schema;
        return true;
    }

    private static int CountLegacyCommandSteps(
        Dictionary<string, System.Text.Json.JsonElement> legacy)
    {
        if (!legacy.TryGetValue("Steps", out System.Text.Json.JsonElement steps)) return 0;
        if (steps.ValueKind != System.Text.Json.JsonValueKind.Array) return 0;
        int count = 0;
        foreach (System.Text.Json.JsonElement step in steps.EnumerateArray())
        {
            if (step.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            if (step.TryGetProperty("kind", out System.Text.Json.JsonElement kind)
                && kind.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(kind.GetString(), "command", StringComparison.Ordinal))
                count++;
        }
        return count;
    }

    // Make name filesystem-safe. The user can call a loop anything; we
    // strip path separators + reserved chars and append the loop suffix.
    private static string SafeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString() + LoopFileSuffix;
    }

    // Filename suffix for loop files in the shared Loops folder.
    public const string LoopFileSuffix = ".loop";

    // Rename pre-suffix-migration {name}.json files to {name}.loop so the new
    // LoadAll scan picks them up. Skips files whose target already exists.
    // Best-effort — failures are logged but don't break the load path.
    private void MigrateLegacyJsonFilesIfPresent(string folder)
    {
        if (!Directory.Exists(folder)) return;
        int moved = 0;
        foreach (string srcPath in Directory.EnumerateFiles(folder, "*.json"))
        {
            // Skip lair setups (those still carry the .lair.json
            // intermediate suffix — LairManager owns their migration).
            if (srcPath.EndsWith(LairManager.LairFileSuffix, StringComparison.OrdinalIgnoreCase)
                || srcPath.EndsWith(LairManager.LegacyLairFileSuffix, StringComparison.OrdinalIgnoreCase))
                continue;
            string baseName = Path.GetFileNameWithoutExtension(srcPath);
            if (string.IsNullOrWhiteSpace(baseName)) continue;
            string destPath = Path.Combine(folder, baseName + LoopFileSuffix);
            if (File.Exists(destPath)) continue;
            try
            {
                File.Move(srcPath, destPath);
                moved++;
            }
            catch (Exception ex)
            {
                _log?.Warn("Loops",
                    $"migration: failed to rename '{srcPath}' → '{destPath}': {ex.Message}");
            }
        }
        if (moved > 0)
            _log?.Info("Loops",
                $"migrated {moved} loop file(s) from .json → {LoopFileSuffix}.");
    }
}
