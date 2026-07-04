using System.IO;
using System.Text.Json;
using FujinTerm.Models.GameData;

namespace FujinTerm.Services;

// In-memory cache of the active game-data set's ItemOverlay seed — the
// Defaults-tier baseline for per-item automation behaviour (the 9 Options
// checkboxes + MinToKeep / MaxToGet) before any user Global / BBS /
// Character override is applied.
//
// Seeds are realm-flavored. Each realm family (stock MajorMUD, Paradigm, …)
// ships its own decoded-from-Items.md seed file under
// AppPaths.BundledItemOverlaySeedFile; the active set's Info.json[0].Legit
// field picks which realm's seed to load:
//   Legit = 0 or 1 → stock seed.
//   Legit = 2      → paradigm seed.
//   Anything else  → fall back to stock (safest default).
//
// Wiring: AppServices subscribes the store to
// GameDataCache.ActiveSetChanged — every set switch rereads the new set's
// Info.json, picks the realm, and reloads the matching seed file. Consumers
// call GetOverlay to retrieve the seed baseline for a specific item Number;
// that overlay is then passed to SettingsResolver.ResolveGameData as the
// Defaults-tier record over which higher-tier deltas are merged.
//
// The seed file itself is never written by the app. To reset a seed, delete
// the user-writable copy at AppPaths.ItemOverlaySeedFile and relaunch —
// AppPaths.EnsureGlobalSeedsBootstrapped re-copies it from the bundled
// source.
//
// This service is a near-direct sibling of MonsterOverlaySeedStore; the two
// share their realm-resolution logic and lifetime contract. Only the
// per-record payload + the lookup table type differ.
public sealed class ItemOverlaySeedStore
{
    private readonly LogService? _log;
    private readonly Dictionary<int, ItemOverlay> _byNumber = new();

    // Realm flavor currently sourcing the cache, or null when none loaded.
    public string? ActiveRealm { get; private set; }

    // Set name currently sourcing the cache, or null when none active.
    public string? ActiveSet { get; private set; }

    public int Count => _byNumber.Count;

    public ItemOverlaySeedStore() { }

    public ItemOverlaySeedStore(LogService log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    // Switch the cache to whichever realm-seed matches setName's
    // Info.json[0].Legit. Pass null to clear (no set active). Errors loading
    // Info.json or the seed file produce an empty cache and a warning log
    // entry — the resolver then falls back to its own new ItemOverlay()
    // defaults.
    public void Load(string? setName)
    {
        _byNumber.Clear();
        ActiveSet = setName;
        ActiveRealm = null;
        if (string.IsNullOrWhiteSpace(setName)) return;

        string realm = ResolveRealm(setName);
        ActiveRealm = realm;

        string path = AppPaths.ItemOverlaySeedFile(realm);
        if (!File.Exists(path))
        {
            _log?.Log(LogSeverity.Info, "ItemOverlaySeed",
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
                // Name in the seed file is purely for human inspection.
                // The runtime overlay's Name is a user-override hook —
                // dropping it on load keeps the resolver from treating
                // the MDB-canonical name as a tier-0 Name override.
                _byNumber[rec.Number] = new ItemOverlay
                {
                    AutoCollect     = rec.AutoCollect,
                    AutoDiscard     = rec.AutoDiscard,
                    AutoFind        = rec.AutoFind,
                    AutoOpen        = rec.AutoOpen,
                    AutoBuy         = rec.AutoBuy,
                    AutoSell        = rec.AutoSell,
                    CannotBeTaken   = rec.CannotBeTaken,
                    MustHaveMinimum = rec.MustHaveMinimum,
                    LoyalItem       = rec.LoyalItem,
                    MinToKeep       = rec.MinToKeep is { } mtk ? mtk.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                    MaxToGet        = rec.MaxToGet  is { } mtg ? mtg.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                };
            }
            _log?.Log(LogSeverity.Info, "ItemOverlaySeed",
                $"Loaded {_byNumber.Count} records from '{path}' (realm '{realm}').");
        }
        catch (Exception ex)
        {
            _log?.Log(LogSeverity.Warn, "ItemOverlaySeed",
                $"Failed to load '{path}': {ex.Message}");
            _byNumber.Clear();
        }
    }

    // Defaults-tier overlay for itemNumber. Returns a blank ItemOverlay when
    // the seed has no record for that item (i.e. the item's stock values
    // match the runtime defaults already — every flag off, MinToKeep = None,
    // MaxToGet = All).
    public ItemOverlay GetOverlay(int itemNumber) =>
        _byNumber.TryGetValue(itemNumber, out ItemOverlay? overlay)
            ? overlay
            : new ItemOverlay();

    // Reads Info.json[0].Legit from the set's folder and maps to a realm name.
    private string ResolveRealm(string setName)
    {
        string infoPath = Path.Combine(AppPaths.GameDataSetDir(setName), "Info.json");
        if (!File.Exists(infoPath))
        {
            _log?.Log(LogSeverity.Info, "ItemOverlaySeed",
                $"No Info.json at '{infoPath}'; defaulting realm to 'stock'.");
            return "stock";
        }

        try
        {
            using FileStream fs = File.OpenRead(infoPath);
            using JsonDocument doc = JsonDocument.Parse(fs);
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
            _log?.Log(LogSeverity.Warn, "ItemOverlaySeed",
                $"Failed to parse '{infoPath}': {ex.Message}; defaulting realm to 'stock'.");
        }
        return "stock";
    }

    // Wire shape on disk. Mirrors Defaults/ItemOverlay.{realm}.seed.json's
    // JSON layout — Number + Name + the overridable fields. Boolean flags
    // only ever appear in the JSON when true (the decoder omits them
    // otherwise to keep the seed file lean). Name is kept on the wire for
    // human inspection but discarded on load.
    private sealed record SeedRecord
    {
        public int     Number          { get; init; }
        public string? Name            { get; init; }
        public bool?   AutoCollect     { get; init; }
        public bool?   AutoDiscard     { get; init; }
        public bool?   AutoFind        { get; init; }
        public bool?   AutoOpen        { get; init; }
        public bool?   AutoBuy         { get; init; }
        public bool?   AutoSell        { get; init; }
        public bool?   CannotBeTaken   { get; init; }
        public bool?   MustHaveMinimum { get; init; }
        public bool?   LoyalItem       { get; init; }
        public int?    MinToKeep       { get; init; }
        public int?    MaxToGet        { get; init; }
    }
}
