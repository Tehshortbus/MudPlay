using System.IO;
using System.Linq;
using System.Text.Json;
using FujinTerm.Game;

namespace FujinTerm.Services;

// Live mirror of the imported MajorMUD game data on disk. Holds the raw
// JsonDocument per loaded table for the currently active set, exposes set
// switching with an ActiveSetChanged event, and provides the eviction primitive
// consumers need to drop raw JSON once it has been folded into typed model
// collections (the per-tab services own those conversions).
//
// Storage layout is the one written by MdbImporter:
// Data/game data/{set-name}/{table-name}.json. Each subdirectory of
// AppPaths.GameDataRoot is a switchable set; the set picker on the Game Data menu
// reads AvailableSets.
//
// Tables are loaded lazily — SwitchSet only flips the active set name and clears
// the prior set's table cache; consumers pull tables on demand via GetRawTable.
// This keeps the working set small when only a few tables are actually needed
// for a given subsystem.
//
// Memory hygiene: once a per-tab consumer has converted the raw JsonDocument into
// typed model collections it calls EvictTable to drop the raw bytes — savings on
// large data sets land in the 150–170 MB range. EvictAll wipes everything
// without changing the active set.
//
// Wiring: AppServices constructs the cache and subscribes it to
// ProfileService.ProfileLoaded + ProfileService.BbsPinApplied so the pinned BBS's
// BbsProfile.ActiveGameDataSet drives the switch automatically (falling back to
// GlobalSettings.DefaultGameDataSet when no BBS is pinned). Manual switches via
// the File → Game Data → Active set menu write back to the resolved BBS profile.
public sealed class GameDataCache
{
    private readonly Dictionary<string, JsonDocument> _tables = new(StringComparer.OrdinalIgnoreCase);

    // Root the cache walks (AppPaths.GameDataRoot). Captured at construction.
    public string GameDataRoot { get; }

    // Name of the active set, or null when nothing is selected.
    public string? ActiveSet { get; private set; }

    // Formula family the active set targets, derived from its Info table:
    // Legit == 2 → RealmType.ParaMud (GreaterMUD / Paradigm), anything else →
    // RealmType.Stock. Returns RealmType.Stock when no set is active or the Info
    // table is missing / malformed (Stock is the safe default — most realms are
    // Stock-derived).
    //
    // Reads through GetRawTable, so the first access lazily loads (and caches)
    // Info.json. The realm only changes when the active set changes, so callers
    // can read this per-calculation cheaply; the Info table is tiny.
    public RealmType ActiveRealm
    {
        get
        {
            JsonDocument? doc = GetRawTable("Info");
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return RealmType.Stock;
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (row.TryGetProperty("Legit", out JsonElement legit)
                    && legit.ValueKind == JsonValueKind.Number
                    && legit.TryGetInt32(out int code))
                {
                    return code == 2 ? RealmType.ParaMud : RealmType.Stock;
                }
            }
            return RealmType.Stock;
        }
    }

    // Fires whenever ActiveSet changes — including the transition from a real set
    // to null (no set picked). Carries the new set name. Subscribers should drop
    // any per-set state they had cached and re-pull whatever they need from the
    // cache.
    public event Action<string?>? ActiveSetChanged;

    // Optional log sink — when set (production wires AppServices.Log after
    // construction), every SwitchSet emits an Info entry naming the outgoing +
    // incoming set so the user can verify swap success in the program log. Tests
    // leave it null.
    public LogService? Log { get; set; }

    public GameDataCache() : this(AppPaths.GameDataRoot) { }

    // Test seam — lets tests point at an isolated root.
    internal GameDataCache(string gameDataRoot)
    {
        GameDataRoot = gameDataRoot;
        Directory.CreateDirectory(GameDataRoot);
    }

    // Imported sets visible on disk, alphabetical. Each entry is a folder name
    // directly under GameDataRoot — pass these to SwitchSet to activate.
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

    // Table names currently held in the raw cache for ActiveSet. Empty before any
    // GetRawTable call; empty again after EvictAll.
    public IReadOnlyList<string> LoadedTables
    {
        get
        {
            lock (_tables) return _tables.Keys.ToArray();
        }
    }

    // Flip the active set. Pass null to clear. Fires ActiveSetChanged with the
    // new value when the effective set changes; suppresses the event on no-op
    // switches.
    //
    // Setting an unknown set name still flips ActiveSet to that string — the
    // cache assumes the caller knows what they're doing (e.g. a profile points at
    // a set that hasn't been imported yet, so the user can see the mismatch in
    // the UI rather than silently falling back).
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

    // Reload the raw cache for the current set. Cheaper than SwitchSet(null) →
    // SwitchSet(name) because no event fires.
    public void Reload()
    {
        EvictAll();
    }

    // Lazy-load (or return the cached) JsonDocument for tableName in the active
    // set. Returns null when no set is active or the table file does not exist on
    // disk. The table name is matched case-insensitively against the JSON
    // filename.
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

    // Try-get variant of GetRawTable.
    public bool TryGetRawTable(string tableName, out JsonDocument? doc)
    {
        doc = GetRawTable(tableName);
        return doc is not null;
    }

    // Lookup the Name field of the row in tableName whose Number field equals
    // number. Returns null when the table isn't in the active set, the row isn't
    // found, or either field is missing on the matched row. Used by edit dialogs
    // to render GameDataLink back-references as human-readable labels.
    public string? FindNameByNumber(string tableName, int number)
    {
        JsonDocument? doc = GetRawTable(tableName);
        if (doc is null) return null;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("Number", out JsonElement numEl)) continue;
            if (numEl.ValueKind != JsonValueKind.Number) continue;
            if (!numEl.TryGetInt32(out int rowNum) || rowNum != number) continue;
            if (!row.TryGetProperty("Name", out JsonElement nameEl)) return null;
            return nameEl.GetString();
        }
        return null;
    }

    // Return the full row in tableName whose Number field equals number, or
    // null when the table isn't in the active set or no row matches. Mirrors
    // FindNameByNumber but hands back the whole JsonElement so a caller can
    // read arbitrary fields (Abil-N / NegateSpell-N / Action …), not just Name.
    // The returned JsonElement stays valid until the next SwitchSet / EvictTable.
    // Backs the room-hazard index's Spells / Items / TBInfo record reads.
    public JsonElement? FindRowByNumber(string tableName, int number)
    {
        JsonDocument? doc = GetRawTable(tableName);
        if (doc is null) return null;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("Number", out JsonElement numEl)) continue;
            if (numEl.ValueKind != JsonValueKind.Number) continue;
            if (numEl.TryGetInt32(out int rowNum) && rowNum == number) return row;
        }
        return null;
    }

    // Return the row in tableName whose Name field equals name (case-insensitive),
    // or null when the table isn't in the active set or no row matches. The
    // returned JsonElement stays valid for the lifetime of the cached
    // JsonDocument (until the next SwitchSet / EvictTable). Used by the
    // trap-delegation capability lookup to resolve a party member's class / race
    // row before scanning its abilities.
    public JsonElement? FindRowByName(string tableName, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        JsonDocument? doc = GetRawTable(tableName);
        if (doc is null) return null;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("Name", out JsonElement nameEl)) continue;
            if (nameEl.ValueKind != JsonValueKind.String) continue;
            if (string.Equals(nameEl.GetString(), name, StringComparison.OrdinalIgnoreCase))
                return row;
        }
        return null;
    }

    // Drop the cached JsonDocument for one table. Used by per-tab consumers after
    // they've folded the raw JSON into typed model collections.
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

    // Drop every cached table. Called implicitly by SwitchSet and Reload; callers
    // can use it to free memory after a bulk-conversion pass too.
    public void EvictAll()
    {
        lock (_tables)
        {
            foreach (JsonDocument doc in _tables.Values) doc.Dispose();
            _tables.Clear();
        }
    }

    // Resolve a table-name → JSON file path under the given set. Looks for a
    // case-insensitive match against existing files so the caller can pass either
    // the table's wire name or the on-disk filename. Returns null when no match
    // exists.
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
