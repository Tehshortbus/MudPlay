using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using FujinTerm.Models.Profile;
using FujinTerm.Models.Settings;

namespace FujinTerm.Services;

// Single read / write API for the 4-tier settings + game-data override
// hierarchy. Resolution order at read time: Character → BBS → Global → Defaults
// (first hit wins, per property). Writes target a specific tier; clears remove
// an override at a tier so the next-lower tier shows through.
//
// Storage model:
//   - Settings live inline as a tab-keyed dictionary on each tier's primary
//     JSON (GlobalSettings.Settings / BbsProfile.Settings /
//     CharacterProfile.Settings). The resolver overlays them onto a base DTO
//     produced from new T() for the Defaults tier.
//   - Game-data record overrides live in per-set side-files inside the tier's
//     folder — {table}_overrides.{set}.json, one file per (table × game-data
//     set) pair. Avoids monolithic blobs and lets users compare overrides
//     between realms (e.g. monster_overrides.v1.11p.json vs
//     monster_overrides.paradigm.json) side-by-side.
public sealed class SettingsResolver
{
    private readonly SettingsService _settings;
    private readonly BbsProfileStore _bbsStore;
    private readonly ProfileService _profile;
    private readonly Func<string?>? _activeSetProvider;
    private BbsProfile? _activeBbs;

    // Per-path cache of override side-files. Keyed by the absolute path returned
    // from AppPaths.OverrideFile; value is null when the file is absent or empty
    // so we can distinguish "checked, missing" from "not yet checked." Without
    // this cache the Game Data Browser triggered ~N rows × 3 tiers disk reads
    // per table on open — 10s of thousands of stat() calls. Invalidated by
    // WriteGameDataAt / ClearGameDataAt, which mutate the same path.
    private readonly Dictionary<string, Dictionary<string, JsonElement>?> _overrideCache =
        new(StringComparer.Ordinal);

    // Guards _overrideCache. The Game Data Browser now runs each tab's row-build
    // on Task.Run, so two activations in quick succession can hit the resolver
    // concurrently from different threads. Plain Dictionary isn't safe under
    // concurrent read+write, so reads and writes lock on this object.
    private readonly object _overrideCacheLock = new();

    public SettingsResolver(
        SettingsService settings,
        BbsProfileStore bbsStore,
        ProfileService profile,
        Func<string?>? activeSetProvider = null)
    {
        _settings = settings;
        _bbsStore = bbsStore;
        _profile = profile;
        _activeSetProvider = activeSetProvider;

        _profile.ProfileLoaded += OnProfileLoaded;
        _profile.ProfileClosed += OnProfileClosed;
        // A draft pinning a BBS (Settings → BBS Apply) doesn't reload the
        // profile, so refresh the active BBS off the pin signal too.
        _profile.BbsPinApplied += _ => RefreshActiveBbs();

        // Pick up the active BBS now in case a profile was auto-loaded before
        // this resolver was constructed (AppServices wires Settings/Profile/Bbs
        // first, then the resolver).
        if (_profile.Current is not null)
        {
            RefreshActiveBbs();
        }
    }

    // ----- Settings (typed DTOs, tab-keyed) ------------------------------

    // Resolve the merged DTO for tabName across all four tiers. Starts from
    // new T() (Defaults) and overlays Global, BBS, and Character deltas in
    // order. Returns a fresh instance — callers may mutate without affecting
    // cached state.
    public T Resolve<T>(string tabName) where T : class, new()
    {
        JsonObject merged = ToJsonObject(new T());

        Overlay(merged, _settings.Current.Settings, tabName);
        Overlay(merged, _activeBbs?.Settings,        tabName);
        Overlay(merged, _profile.Current?.Settings,  tabName);

        return JsonSerializer.Deserialize<T>(merged.ToJsonString(), JsonStore.Options)
            ?? throw new InvalidOperationException(
                $"Resolve<{typeof(T).Name}>('{tabName}') produced a null deserialization result.");
    }

    // Write value into tier's file as the delta for tabName, then persist the
    // tier file to disk. SettingsTier.Defaults is read-only and throws.
    public void WriteAt<T>(SettingsTier tier, string tabName, T value) where T : class
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        JsonElement asJson = JsonSerializer.SerializeToElement(value, JsonStore.Options);

        switch (tier)
        {
            case SettingsTier.Defaults:
                throw new InvalidOperationException("Defaults tier is read-only.");

            case SettingsTier.Global:
                _settings.Current.Settings ??= new();
                _settings.Current.Settings[tabName] = asJson;
                _settings.Save();
                break;

            case SettingsTier.Bbs:
                BbsProfile bbs = RequireActiveBbs();
                bbs.Settings ??= new();
                bbs.Settings[tabName] = asJson;
                _bbsStore.Save(bbs);
                break;

            case SettingsTier.Character:
                CharacterProfile chr = RequireLoadedProfile();
                chr.Settings ??= new();
                chr.Settings[tabName] = asJson;
                _profile.Save();
                break;
        }
    }

    // Remove the override for tabName at the specified tier so the next-lower
    // tier shows through on the next Resolve call. No-op if no override exists.
    public void ClearAt(SettingsTier tier, string tabName)
    {
        switch (tier)
        {
            case SettingsTier.Defaults:
                throw new InvalidOperationException("Defaults tier is read-only.");

            case SettingsTier.Global:
                if (_settings.Current.Settings?.Remove(tabName) == true) _settings.Save();
                break;

            case SettingsTier.Bbs:
                BbsProfile? bbs = _activeBbs;
                if (bbs?.Settings?.Remove(tabName) == true) _bbsStore.Save(bbs);
                break;

            case SettingsTier.Character:
                CharacterProfile? chr = _profile.Current;
                if (chr?.Settings?.Remove(tabName) == true) _profile.Save();
                break;
        }
    }

    // ----- Game-data record overrides (per-set side-files) ---------------

    // Fired after a successful WriteGameDataAt or a ClearGameDataAt that
    // actually removed an override. Payload identifies the (table, recordId,
    // tier) that was mutated.
    //
    // Automation engines that cache typed model collections from a game-data
    // table — RoomGraphManager, MessageStore-style catalogues, per-monster
    // respawn maps, etc. — subscribe here and either reload the affected record
    // (via ResolveGameData) or invalidate their whole table cache when an edit
    // lands. Engines that re-resolve on every use don't need to subscribe — the
    // resolver's own per-file cache is already coherent with disk by the time
    // this fires.
    public event Action<GameDataChange>? GameDataChanged;

    // Resolve a single game-data record (e.g. a Spell or Item) across all four
    // tiers. The Defaults tier for game data is the imported MDB → JSON set
    // under Data/game data/{set}/; resolver consumers supply that base via
    // defaultsRecord. Higher-tier overrides come from per-set side-files
    // ({table}_overrides.{set}.json).
    public T ResolveGameData<T>(string table, string recordId, T defaultsRecord) where T : class, new()
    {
        JsonObject merged = ToJsonObject(defaultsRecord);

        string? set = _activeSetProvider?.Invoke();
        if (set is null)
        {
            // No active set → no per-set override files to read.
            return JsonSerializer.Deserialize<T>(merged.ToJsonString(), JsonStore.Options)
                ?? throw new InvalidOperationException(
                    $"ResolveGameData<{typeof(T).Name}>('{table}', '{recordId}') produced null.");
        }

        Overlay(merged, ReadOverride(SettingsTier.Global,    null,                          table, set, recordId));
        Overlay(merged, ReadOverride(SettingsTier.Bbs,       _activeBbs?.Name,              table, set, recordId));
        Overlay(merged, ReadOverride(SettingsTier.Character, _profile.CurrentProfileName,   table, set, recordId));

        return JsonSerializer.Deserialize<T>(merged.ToJsonString(), JsonStore.Options)
            ?? throw new InvalidOperationException(
                $"ResolveGameData<{typeof(T).Name}>('{table}', '{recordId}') produced null.");
    }

    // Write value as the override for record recordId in table at the specified
    // tier. Lands in the per-set side-file for the currently active game-data
    // set.
    public void WriteGameDataAt<T>(SettingsTier tier, string table, string recordId, T value) where T : class
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        string set = RequireActiveSet();
        string scope = ResolveScopeName(tier);
        JsonElement asJson = JsonSerializer.SerializeToElement(value, JsonStore.Options);

        Dictionary<string, JsonElement> records = LoadOverrideFile(tier, scope, table, set);
        records[recordId] = asJson;
        SaveOverrideFile(tier, scope, table, set, records);
        GameDataChanged?.Invoke(new GameDataChange(table, recordId, tier));
    }

    // Remove the game-data override for recordId in table at the specified tier.
    // No-op when the side-file or record is absent.
    public void ClearGameDataAt(SettingsTier tier, string table, string recordId)
    {
        string? set = _activeSetProvider?.Invoke();
        if (set is null) return;
        string scope = ResolveScopeName(tier);

        Dictionary<string, JsonElement> records = LoadOverrideFile(tier, scope, table, set);
        if (records.Remove(recordId))
        {
            SaveOverrideFile(tier, scope, table, set, records);
            GameDataChanged?.Invoke(new GameDataChange(table, recordId, tier));
        }
    }

    // Which tier owns the highest-priority override for the given record, or
    // SettingsTier.Defaults when no tier has an override. Used by the Game Data
    // Browser's "Use" column to label each row with the tier its current values
    // come from.
    public SettingsTier GetGameDataSourceTier(string table, string recordId)
    {
        string? set = _activeSetProvider?.Invoke();
        if (set is null) return SettingsTier.Defaults;

        if (ReadOverride(SettingsTier.Character, _profile.CurrentProfileName, table, set, recordId) is not null)
            return SettingsTier.Character;
        if (ReadOverride(SettingsTier.Bbs, _activeBbs?.Name, table, set, recordId) is not null)
            return SettingsTier.Bbs;
        if (ReadOverride(SettingsTier.Global, null, table, set, recordId) is not null)
            return SettingsTier.Global;
        return SettingsTier.Defaults;
    }

    // ----- Active-BBS tracking -------------------------------------------

    private void OnProfileLoaded(CharacterProfile profile) => RefreshActiveBbs();

    // Recompute the active BBS from ProfileService.CurrentBbsName (the folder
    // the loaded profile lives under, or a draft's pinned BBS). Wired to both
    // ProfileService.ProfileLoaded and BbsPinApplied so a freshly-pinned draft
    // resolves BBS-tier overrides immediately, not only after a reload.
    private void RefreshActiveBbs()
    {
        string? bbs = _profile.CurrentBbsName;
        _activeBbs = string.IsNullOrWhiteSpace(bbs) ? null : _bbsStore.Get(bbs);
    }

    private void OnProfileClosed() => _activeBbs = null;

    private BbsProfile RequireActiveBbs() => _activeBbs
        ?? throw new InvalidOperationException(
            "Cannot write to BBS tier: no active BBS profile (load a character first).");

    private CharacterProfile RequireLoadedProfile() => _profile.Current
        ?? throw new InvalidOperationException(
            "Cannot write to Character tier: no profile loaded.");

    private string RequireActiveSet() => _activeSetProvider?.Invoke()
        ?? throw new InvalidOperationException(
            "Cannot write game-data override: no active game-data set.");

    // Whether an override can currently be written at tier without throwing.
    // SettingsTier.Defaults is always read-only (the MDB / seed is the source);
    // Global is always writable; Bbs needs an active BBS profile; Character
    // needs a named loaded profile. Lets game-data edit dialogs offer only valid
    // tiers instead of letting WriteGameDataAt throw from the Save handler.
    public bool CanWriteAt(SettingsTier tier) => tier switch
    {
        SettingsTier.Global    => true,
        SettingsTier.Bbs       => _activeBbs is not null,
        SettingsTier.Character => _profile.CurrentProfileName is not null,
        _                      => false,
    };

    // The tiers an override can currently be written at, most-specific first
    // (Character → BBS → Global). Excludes read-only Defaults and any tier whose
    // scope isn't active. Always contains at least Global.
    public IReadOnlyList<SettingsTier> WritableTiers()
    {
        List<SettingsTier> tiers = new(3);
        if (CanWriteAt(SettingsTier.Character)) tiers.Add(SettingsTier.Character);
        if (CanWriteAt(SettingsTier.Bbs))       tiers.Add(SettingsTier.Bbs);
        tiers.Add(SettingsTier.Global);
        return tiers;
    }

    private string ResolveScopeName(SettingsTier tier) => tier switch
    {
        SettingsTier.Defaults  => throw new InvalidOperationException("Defaults tier is read-only."),
        SettingsTier.Global    => string.Empty,
        SettingsTier.Bbs       => _activeBbs?.Name ?? throw new InvalidOperationException(
                                       "Cannot write to BBS tier: no active BBS."),
        SettingsTier.Character => _profile.CurrentProfileName ?? throw new InvalidOperationException(
                                       "Cannot write to Character tier: no named profile loaded."),
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };

    // ----- Override file I/O ---------------------------------------------

    private JsonElement? ReadOverride(
        SettingsTier tier,
        string? scope,
        string table,
        string set,
        string recordId)
    {
        // Skip tiers that don't have a usable scope (e.g. BBS tier
        // when no BBS is pinned, Char tier when no named profile).
        if (tier != SettingsTier.Global && string.IsNullOrEmpty(scope)) return null;

        string path = AppPaths.OverrideFile(tier, scope, table, set, CharacterBbs(tier));
        Dictionary<string, JsonElement>? records = GetCachedOverrideFile(path);
        if (records is null) return null;
        return records.TryGetValue(recordId, out JsonElement v) ? v : null;
    }

    // BBS the loaded profile lives under — required by AppPaths.OverrideFile for
    // the Character tier (profiles nest at BBS/{bbs}/profiles/{char}/), null for
    // any other tier.
    private string? CharacterBbs(SettingsTier tier)
        => tier == SettingsTier.Character ? _profile.CurrentBbsName : null;

    private Dictionary<string, JsonElement> LoadOverrideFile(
        SettingsTier tier, string scope, string table, string set)
    {
        string path = AppPaths.OverrideFile(tier, scope, table, set, CharacterBbs(tier));
        return GetCachedOverrideFile(path) ?? new();
    }

    private void SaveOverrideFile(
        SettingsTier tier, string scope, string table, string set,
        Dictionary<string, JsonElement> records)
    {
        string path = AppPaths.OverrideFile(tier, scope, table, set, CharacterBbs(tier));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonStore.Save(path, records);
        // Stash the freshly-written contents so subsequent reads don't
        // hit disk and so the in-memory cache stays in sync with the
        // file we just produced.
        lock (_overrideCacheLock)
        {
            _overrideCache[path] = records.Count == 0 ? null : records;
        }
    }

    private Dictionary<string, JsonElement>? GetCachedOverrideFile(string path)
    {
        lock (_overrideCacheLock)
        {
            if (_overrideCache.TryGetValue(path, out Dictionary<string, JsonElement>? cached))
                return cached;
        }
        // Load outside the lock so a slow disk read doesn't block other
        // threads checking unrelated paths. A race where two threads load
        // the same file is benign — same bytes either way.
        Dictionary<string, JsonElement>? loaded = JsonStore.Load<Dictionary<string, JsonElement>>(path);
        lock (_overrideCacheLock)
        {
            _overrideCache[path] = loaded;
        }
        return loaded;
    }

    // ----- Merge plumbing ------------------------------------------------

    private static JsonObject ToJsonObject<T>(T value) where T : class
    {
        string json = JsonSerializer.Serialize(value, JsonStore.Options);
        return JsonNode.Parse(json)!.AsObject();
    }

    private static void Overlay(JsonObject merged, Dictionary<string, JsonElement>? deltas, string tabName)
    {
        if (deltas is null || !deltas.TryGetValue(tabName, out JsonElement delta)) return;
        Overlay(merged, delta);
    }

    private static void Overlay(JsonObject merged, JsonElement? delta)
    {
        if (delta is null) return;
        // Defensive: only object-shaped deltas can be merged property-by-property.
        // Arrays / primitives at the top level make no sense for a tab DTO.
        if (delta.Value.ValueKind != JsonValueKind.Object) return;

        foreach (JsonProperty prop in delta.Value.EnumerateObject())
        {
            merged[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        }
    }
}

// Payload for SettingsResolver.GameDataChanged. Identifies the table + record +
// tier whose override was just written or cleared so subscribers can decide
// between a targeted reload (one record) or a full table cache invalidation.
// Table is the game-data table name (e.g. "Monsters", "Items"), RecordId the
// primary-key string of the affected record, Tier the tier the override was
// written at / cleared from.
public readonly record struct GameDataChange(string Table, string RecordId, SettingsTier Tier);
