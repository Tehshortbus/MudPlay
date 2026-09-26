using System.IO;
using System.Text.Json;
using MudPlay.Models.GameData;

namespace MudPlay.Services;

// In-memory cache of the active game-data set's MonsterOverlay seed — the
// Defaults-tier baseline for per-monster automation behavior (relationship /
// priority / DontBackstab) and location labels (Landmass / Region / Area)
// before any user Global / BBS / Character override is applied.
//
// Seeds are realm-flavored. Each realm family (stock MajorMUD, Paradigm, …)
// ships its own decoded-from-Monsters.md seed file under
// AppPaths.BundledMonsterOverlaySeedFile; the active set's Info.json[0].Legit
// field picks which realm's seed to load:
//   Legit = 0 or 1 → stock seed.
//   Legit = 2      → paradigm seed.
//   Anything else  → fall back to stock (safest default).
//
// Wiring: AppServices subscribes the store to
// GameDataCache.ActiveSetChanged — every set switch rereads the new set's
// Info.json, picks the realm, and reloads the matching seed file. Consumers
// call GetOverlay to retrieve the seed baseline for a specific monster
// Number; that overlay is then passed to SettingsResolver.ResolveGameData as
// the Defaults-tier record over which higher-tier deltas are merged.
//
// The seed file itself is never written by the app. To reset a seed, delete
// the user-writable copy at AppPaths.MonsterOverlaySeedFile and relaunch —
// AppPaths.EnsureGlobalSeedsBootstrapped re-copies it from the bundled
// source.
public sealed class MonsterOverlaySeedStore
{
    private readonly LogService? _log;
    private readonly Dictionary<int, MonsterOverlay> _byNumber = new();

    // Realm flavor currently sourcing the cache, or null when none loaded.
    public string? ActiveRealm { get; private set; }

    // Set name currently sourcing the cache, or null when none active.
    public string? ActiveSet { get; private set; }

    public int Count => _byNumber.Count;

    // Distinct, sorted location labels the seed carries — the typeahead lists for the
    // monster record's Landmass / Region / Area boxes, so a new monster is filed under
    // an existing name rather than a near-miss spelling.
    public MonsterLocationSuggestions LocationSuggestions { get; private set; } = MonsterLocationSuggestions.Empty;

    public MonsterOverlaySeedStore() { }

    public MonsterOverlaySeedStore(LogService log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    // Switch the cache to whichever realm-seed matches setName's
    // Info.json[0].Legit. Pass null to clear (no set active). Errors loading
    // Info.json or the seed file produce an empty cache and a warning log
    // entry — the resolver then falls back to its own new MonsterOverlay()
    // defaults.
    public void Load(string? setName)
    {
        _byNumber.Clear();
        LocationSuggestions = MonsterLocationSuggestions.Empty;
        ActiveSet = setName;
        ActiveRealm = null;
        if (string.IsNullOrWhiteSpace(setName)) return;

        string realm = ResolveRealm(setName);
        ActiveRealm = realm;

        // Per-realm user-writable seed at Data/Global/MonsterOverlay.{realm}.seed.json.
        // Bootstrapping from the bundled Defaults/ copy happens once at app
        // startup via AppPaths.EnsureGlobalSeedsBootstrapped().
        string path = AppPaths.MonsterOverlaySeedFile(realm);
        if (!File.Exists(path))
        {
            _log?.Log(LogSeverity.Info, "MonsterOverlaySeed",
                $"No seed file at '{path}' for realm '{realm}'; using empty baseline.");
            return;
        }

        try
        {
            List<SeedRecord>? records = JsonStore.Load<List<SeedRecord>>(path);
            if (records is null) return;
            foreach (SeedRecord rec in records)
            {
                if (rec.Number <= 0) continue;
                // The decoded JSON carries Name purely for human inspection
                // of the seed file — the runtime overlay's Name property is
                // a user-override hook, NOT a label. Drop it on the way in
                // so the resolver doesn't see the MDB-canonical name as a
                // Defaults-tier "override" (it'd flag every monster as
                // having a tier-0 Name override otherwise).
                _byNumber[rec.Number] = new MonsterOverlay
                {
                    Relationship = rec.Relationship,
                    Priority     = rec.Priority,
                    DontBackstab = rec.DontBackstab,
                    Landmass     = NullIfBlank(rec.Landmass),
                    Region       = NullIfBlank(rec.Region),
                    Area         = NullIfBlank(rec.Area),
                };
            }
            LocationSuggestions = new MonsterLocationSuggestions(
                DistinctSorted(_byNumber.Values.Select(o => o.Landmass)),
                DistinctSorted(_byNumber.Values.Select(o => o.Region)),
                DistinctSorted(_byNumber.Values.Select(o => o.Area)));
            _log?.Log(LogSeverity.Info, "MonsterOverlaySeed",
                $"Loaded {_byNumber.Count} records from '{path}' (realm '{realm}'); " +
                $"{_byNumber.Values.Count(o => o.Region is not null)} with a location.");
        }
        catch (Exception ex)
        {
            _log?.Log(LogSeverity.Warn, "MonsterOverlaySeed",
                $"Failed to load '{path}': {ex.Message}");
            _byNumber.Clear();
            LocationSuggestions = MonsterLocationSuggestions.Empty;
        }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static IReadOnlyList<string> DistinctSorted(IEnumerable<string?> values) =>
        values.Where(v => v is not null).Select(v => v!)
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .Order(StringComparer.OrdinalIgnoreCase)
              .ToArray();

    // Defaults-tier overlay for monsterNumber. Returns a blank MonsterOverlay
    // when the seed has no record for that monster (i.e. the monster's stock
    // values match the runtime defaults already — Enemy / Normal / no flags).
    public MonsterOverlay GetOverlay(int monsterNumber) =>
        _byNumber.TryGetValue(monsterNumber, out MonsterOverlay? overlay)
            ? overlay
            : new MonsterOverlay();

    // Reads Info.json[0].Legit from the set's folder and maps to a realm name.
    private string ResolveRealm(string setName)
    {
        string infoPath = Path.Combine(AppPaths.GameDataSetDir(setName), "Info.json");
        if (!File.Exists(infoPath))
        {
            _log?.Log(LogSeverity.Info, "MonsterOverlaySeed",
                $"No Info.json at '{infoPath}'; defaulting realm to 'stock'.");
            return "stock";
        }

        try
        {
            using FileStream fs = File.OpenRead(infoPath);
            using JsonDocument doc = JsonDocument.Parse(fs);
            // Info.json is an array with one object holding the export metadata.
            JsonElement root = doc.RootElement;
            JsonElement first =
                root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                    ? root[0]
                    : root;
            if (first.TryGetProperty("Legit", out JsonElement legit)
                && legit.TryGetInt32(out int legitValue))
            {
                return legitValue == 2 ? "paradigm" : "stock";
            }
        }
        catch (Exception ex)
        {
            _log?.Log(LogSeverity.Warn, "MonsterOverlaySeed",
                $"Failed to parse '{infoPath}': {ex.Message}; defaulting realm to 'stock'.");
        }
        return "stock";
    }

    // Wire shape on disk. Mirrors Defaults/MonsterOverlay.{realm}.seed.json's
    // JSON layout — Number + Name + the overridable seed fields. Name is kept
    // on the wire for human inspection but discarded on load.
    private sealed record SeedRecord
    {
        public int                    Number       { get; init; }
        public string?                Name         { get; init; }
        public MonsterRelationship?   Relationship { get; init; }
        public MonsterAttackPriority? Priority     { get; init; }
        public bool?                  DontBackstab { get; init; }
        public string?                Landmass     { get; init; }
        public string?                Region       { get; init; }
        public string?                Area         { get; init; }
    }
}
