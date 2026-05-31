using System.IO;
using System.Linq;
using System.Text.Json;

namespace FujinTerm.Services;

/// <summary>
/// Live mirror of the imported MajorMUD game data on disk. Holds the
/// raw <see cref="JsonDocument"/> per loaded table for the currently
/// active set, exposes set switching with an
/// <see cref="ActiveSetChanged"/> event, and gives later phases the
/// eviction primitive they need to drop raw JSON once it has been
/// folded into typed model collections (per-tab services in PRs 5.5+
/// own those conversions).
/// </summary>
/// <remarks>
/// <para>
/// Storage layout is the one written by <see cref="MdbImporter"/>:
/// <c>Data/game data/{set-name}/{table-name}.json</c>. Each
/// subdirectory of <see cref="AppPaths.GameDataRoot"/> is a switchable
/// set; the set picker on the Game Data menu (Phase 5 PR 5.22) reads
/// <see cref="AvailableSets"/>.
/// </para>
/// <para>
/// Tables are loaded lazily — <see cref="SwitchSet"/> only flips the
/// active set name and clears the prior set's table cache; consumers
/// pull tables on demand via <see cref="GetRawTable"/>. This keeps
/// the working set small when only a few tables are actually needed
/// for a given subsystem.
/// </para>
/// <para>
/// Memory hygiene (per MudProxy's MemoryOptimization plan): once a
/// per-tab consumer has converted the raw <see cref="JsonDocument"/>
/// into typed model collections it calls <see cref="EvictTable"/> to
/// drop the raw bytes — savings on large data sets land in the
/// 150–170 MB range there. <see cref="EvictAll"/> wipes everything
/// without changing the active set.
/// </para>
/// <para>
/// Wiring (Phase 0 PR 0.2 event protocol): <see cref="AppServices"/>
/// constructs the cache and subscribes it to
/// <see cref="ProfileService.ProfileLoaded"/> +
/// <see cref="ProfileService.BbsPinApplied"/> so the pinned BBS's
/// <see cref="Models.Settings.BbsProfile.ActiveGameDataSet"/> drives
/// the switch automatically (falling back to
/// <see cref="Models.Settings.GlobalSettings.DefaultGameDataSet"/>
/// when no BBS is pinned). Manual switches via the File → Game Data
/// → Active set menu write back to the resolved BBS profile.
/// </para>
/// </remarks>
public sealed class GameDataCache
{
    private readonly Dictionary<string, JsonDocument> _tables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Root the cache walks (<see cref="AppPaths.GameDataRoot"/>). Captured at construction.</summary>
    public string GameDataRoot { get; }

    /// <summary>Name of the active set, or <c>null</c> when nothing is selected.</summary>
    public string? ActiveSet { get; private set; }

    /// <summary>
    /// Fires whenever <see cref="ActiveSet"/> changes — including the
    /// transition from a real set to <c>null</c> (no set picked).
    /// Carries the new set name. Subscribers should drop any per-set
    /// state they had cached and re-pull whatever they need from the
    /// cache.
    /// </summary>
    public event Action<string?>? ActiveSetChanged;

    /// <summary>
    /// Optional log sink — when set (production wires
    /// <see cref="AppServices.Log"/> after construction), every
    /// <see cref="SwitchSet"/> emits an Info entry naming the
    /// outgoing + incoming set so the user can verify swap success
    /// in the program log. Tests leave it null.
    /// </summary>
    public LogService? Log { get; set; }

    public GameDataCache() : this(AppPaths.GameDataRoot) { }

    /// <summary>Test seam — lets tests point at an isolated root.</summary>
    internal GameDataCache(string gameDataRoot)
    {
        GameDataRoot = gameDataRoot;
        Directory.CreateDirectory(GameDataRoot);
    }

    /// <summary>
    /// Imported sets visible on disk, alphabetical. Each entry is a
    /// folder name directly under <see cref="GameDataRoot"/> — pass
    /// these to <see cref="SwitchSet"/> to activate.
    /// </summary>
    public IReadOnlyList<string> AvailableSets => EnumerateSets();

    private IReadOnlyList<string> EnumerateSets()
    {
        if (!Directory.Exists(GameDataRoot)) return Array.Empty<string>();
        return Directory.GetDirectories(GameDataRoot)
            .Select(Path.GetFileName)
            .Where(static n => !string.IsNullOrEmpty(n))
            .Select(static n => n!)
            .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Table names currently held in the raw cache for
    /// <see cref="ActiveSet"/>. Empty before any
    /// <see cref="GetRawTable"/> call; empty again after
    /// <see cref="EvictAll"/>.
    /// </summary>
    public IReadOnlyList<string> LoadedTables
    {
        get
        {
            lock (_tables) return _tables.Keys.ToArray();
        }
    }

    /// <summary>
    /// Flip the active set. Pass <c>null</c> to clear. Fires
    /// <see cref="ActiveSetChanged"/> with the new value when the
    /// effective set changes; suppresses the event on no-op switches.
    /// </summary>
    /// <remarks>
    /// Setting an unknown set name still flips <see cref="ActiveSet"/>
    /// to that string — the cache assumes the caller knows what
    /// they're doing (e.g. a profile points at a set that hasn't been
    /// imported yet, so the user can see the mismatch in the UI rather
    /// than silently falling back).
    /// </remarks>
    public void SwitchSet(string? setName)
    {
        // Normalize empty → null so the event payload is consistent.
        if (string.IsNullOrWhiteSpace(setName)) setName = null;

        if (string.Equals(ActiveSet, setName, StringComparison.OrdinalIgnoreCase)) return;

        string? outgoing = ActiveSet;
        EvictAll();
        ActiveSet = setName;
        Log?.Log(LogSeverity.Info, "GameData",
            (outgoing, setName) switch
            {
                (null, null)       => "No active game data set.",
                (null, not null)   => $"Loaded game data set '{setName}'.",
                (not null, null)   => $"Unloaded game data set '{outgoing}' (no set active).",
                (not null, not null) => $"Swapped game data set '{outgoing}' → '{setName}'.",
            });
        ActiveSetChanged?.Invoke(setName);
    }

    /// <summary>
    /// Reload the raw cache for the current set. Cheaper than
    /// SwitchSet(null) → SwitchSet(name) because no event fires.
    /// </summary>
    public void Reload()
    {
        EvictAll();
    }

    /// <summary>
    /// Lazy-load (or return the cached) <see cref="JsonDocument"/> for
    /// <paramref name="tableName"/> in the active set. Returns
    /// <c>null</c> when no set is active or the table file does not
    /// exist on disk. The table name is matched case-insensitively
    /// against the JSON filename.
    /// </summary>
    public JsonDocument? GetRawTable(string tableName)
    {
        if (ActiveSet is null) return null;
        ArgumentNullException.ThrowIfNull(tableName);

        lock (_tables)
        {
            if (_tables.TryGetValue(tableName, out JsonDocument? cached)) return cached;

            string? path = ResolveTablePath(ActiveSet, tableName);
            if (path is null || !File.Exists(path)) return null;

            // ReadAllBytes is fine — these JSON files are tens of MB at
            // most and we don't want to hold a FileStream while Parse
            // walks the buffer.
            byte[] bytes = File.ReadAllBytes(path);
            JsonDocument doc = JsonDocument.Parse(bytes);
            _tables[tableName] = doc;
            return doc;
        }
    }

    /// <summary>Try-get variant of <see cref="GetRawTable"/>.</summary>
    public bool TryGetRawTable(string tableName, out JsonDocument? doc)
    {
        doc = GetRawTable(tableName);
        return doc is not null;
    }

    /// <summary>
    /// Drop the cached <see cref="JsonDocument"/> for one table. Used
    /// by per-tab consumers after they've folded the raw JSON into
    /// typed model collections (the MemoryOptimization pattern).
    /// </summary>
    public bool EvictTable(string tableName)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        lock (_tables)
        {
            if (!_tables.Remove(tableName, out JsonDocument? doc)) return false;
            doc.Dispose();
            return true;
        }
    }

    /// <summary>
    /// Drop every cached table. Called implicitly by
    /// <see cref="SwitchSet"/> and <see cref="Reload"/>; callers can
    /// use it to free memory after a bulk-conversion pass too.
    /// </summary>
    public void EvictAll()
    {
        lock (_tables)
        {
            foreach (JsonDocument doc in _tables.Values) doc.Dispose();
            _tables.Clear();
        }
    }

    /// <summary>
    /// Resolve a table-name → JSON file path under the given set.
    /// Looks for a case-insensitive match against existing files so the
    /// caller can pass either the table's wire name or the on-disk
    /// filename. Returns <c>null</c> when no match exists.
    /// </summary>
    private string? ResolveTablePath(string setName, string tableName)
    {
        string setDir = Path.Combine(GameDataRoot, setName);
        if (!Directory.Exists(setDir)) return null;

        string exact = Path.Combine(setDir, tableName + ".json");
        if (File.Exists(exact)) return exact;

        // Case-insensitive fallback — Linux is case-sensitive at the
        // FS level, so a table named "Monsters" on a Wine-imported set
        // is "Monsters.json" but a hand-renamed one could be "monsters.json".
        string target = tableName + ".json";
        return Directory.EnumerateFiles(setDir, "*.json")
            .FirstOrDefault(p => string.Equals(Path.GetFileName(p), target, StringComparison.OrdinalIgnoreCase));
    }
}
