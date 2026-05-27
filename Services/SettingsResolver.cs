using System.Text.Json;
using System.Text.Json.Nodes;
using FujinTerm.Models.Profile;
using FujinTerm.Models.Settings;

namespace FujinTerm.Services;

/// <summary>
/// Single read / write API for the 4-tier settings + game-data override
/// hierarchy. Resolution order at read time: Character → BBS → Global →
/// Defaults (first hit wins, per property). Writes target a specific tier;
/// clears remove an override at a tier so the next-lower tier shows through.
/// </summary>
/// <remarks>
/// Storage model: each writable tier file (<see cref="GlobalSettings"/>,
/// <see cref="BbsProfile"/>, <see cref="CharacterProfile"/>) carries a
/// <c>Settings</c> dictionary keyed by tab name and a <c>GameDataOverrides</c>
/// dictionary keyed by table → record-id. Values are partial JSON — only the
/// fields the user actually overrode at that tier. This resolver overlays
/// those partial blobs onto a base DTO produced from <c>new T()</c> (the
/// Defaults tier for settings) and deserializes the result. Two JSON
/// round-trips per resolve; cache when the cost shows up in profiling.
/// </remarks>
public sealed class SettingsResolver
{
    private readonly SettingsService _settings;
    private readonly BbsProfileStore _bbsStore;
    private readonly ProfileService _profile;
    private BbsProfile? _activeBbs;

    public SettingsResolver(SettingsService settings, BbsProfileStore bbsStore, ProfileService profile)
    {
        _settings = settings;
        _bbsStore = bbsStore;
        _profile = profile;

        _profile.ProfileLoaded += OnProfileLoaded;
        _profile.ProfileClosed += OnProfileClosed;

        // Pick up the active BBS now in case a profile was auto-loaded before
        // this resolver was constructed (AppServices wires Settings/Profile/Bbs
        // first, then the resolver).
        if (_profile.Current is not null)
        {
            OnProfileLoaded(_profile.Current);
        }
    }

    // ----- Settings (typed DTOs, tab-keyed) ------------------------------

    /// <summary>
    /// Resolve the merged DTO for <paramref name="tabName"/> across all four
    /// tiers. Starts from <c>new T()</c> (Defaults) and overlays Global, BBS,
    /// and Character deltas in order. Returns a fresh instance — callers may
    /// mutate without affecting cached state.
    /// </summary>
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

    /// <summary>
    /// Write <paramref name="value"/> into <paramref name="tier"/>'s file as
    /// the delta for <paramref name="tabName"/>, then persist the tier file
    /// to disk. <see cref="SettingsTier.Defaults"/> is read-only and throws.
    /// </summary>
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

    /// <summary>
    /// Remove the override for <paramref name="tabName"/> at the specified
    /// tier so the next-lower tier shows through on the next
    /// <see cref="Resolve{T}"/> call. No-op if no override exists.
    /// </summary>
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

    // ----- Game-data record overrides (string-keyed) ---------------------

    /// <summary>
    /// Resolve a single game-data record (e.g. a Spell or Item) across all
    /// four tiers. The Defaults tier for game data is the imported MDB → JSON
    /// set under <c>Data/game data/{set}/</c>; resolver consumers supply that
    /// base via <paramref name="defaultsRecord"/>.
    /// </summary>
    public T ResolveGameData<T>(string table, string recordId, T defaultsRecord) where T : class, new()
    {
        JsonObject merged = ToJsonObject(defaultsRecord);

        Overlay(merged, GetGameDataDelta(_settings.Current.GameDataOverrides, table, recordId));
        Overlay(merged, GetGameDataDelta(_activeBbs?.GameDataOverrides,        table, recordId));
        Overlay(merged, GetGameDataDelta(_profile.Current?.GameDataOverrides,  table, recordId));

        return JsonSerializer.Deserialize<T>(merged.ToJsonString(), JsonStore.Options)
            ?? throw new InvalidOperationException(
                $"ResolveGameData<{typeof(T).Name}>('{table}', '{recordId}') produced null.");
    }

    /// <summary>
    /// Write <paramref name="value"/> as the override for record
    /// <paramref name="recordId"/> in <paramref name="table"/> at the
    /// specified tier.
    /// </summary>
    public void WriteGameDataAt<T>(SettingsTier tier, string table, string recordId, T value) where T : class
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        JsonElement asJson = JsonSerializer.SerializeToElement(value, JsonStore.Options);

        switch (tier)
        {
            case SettingsTier.Defaults:
                throw new InvalidOperationException("Defaults tier is read-only.");

            case SettingsTier.Global:
                SetGameDataOverride(
                    _settings.Current.GameDataOverrides ??= new(),
                    table, recordId, asJson);
                _settings.Save();
                break;

            case SettingsTier.Bbs:
                BbsProfile bbs = RequireActiveBbs();
                SetGameDataOverride(
                    bbs.GameDataOverrides ??= new(),
                    table, recordId, asJson);
                _bbsStore.Save(bbs);
                break;

            case SettingsTier.Character:
                CharacterProfile chr = RequireLoadedProfile();
                SetGameDataOverride(
                    chr.GameDataOverrides ??= new(),
                    table, recordId, asJson);
                _profile.Save();
                break;
        }
    }

    /// <summary>
    /// Remove the game-data override for <paramref name="recordId"/> in
    /// <paramref name="table"/> at the specified tier.
    /// </summary>
    public void ClearGameDataAt(SettingsTier tier, string table, string recordId)
    {
        switch (tier)
        {
            case SettingsTier.Defaults:
                throw new InvalidOperationException("Defaults tier is read-only.");

            case SettingsTier.Global:
                if (RemoveGameDataOverride(_settings.Current.GameDataOverrides, table, recordId))
                    _settings.Save();
                break;

            case SettingsTier.Bbs:
                if (_activeBbs is { } bbs &&
                    RemoveGameDataOverride(bbs.GameDataOverrides, table, recordId))
                    _bbsStore.Save(bbs);
                break;

            case SettingsTier.Character:
                if (_profile.Current is { } chr &&
                    RemoveGameDataOverride(chr.GameDataOverrides, table, recordId))
                    _profile.Save();
                break;
        }
    }

    // ----- Active-BBS tracking -------------------------------------------

    private void OnProfileLoaded(CharacterProfile profile)
    {
        _activeBbs = string.IsNullOrWhiteSpace(profile.BbsName)
            ? null
            : _bbsStore.Get(profile.BbsName);
    }

    private void OnProfileClosed() => _activeBbs = null;

    private BbsProfile RequireActiveBbs() => _activeBbs
        ?? throw new InvalidOperationException(
            "Cannot write to BBS tier: no active BBS profile (load a character first).");

    private CharacterProfile RequireLoadedProfile() => _profile.Current
        ?? throw new InvalidOperationException(
            "Cannot write to Character tier: no profile loaded.");

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

    private static JsonElement? GetGameDataDelta(
        Dictionary<string, Dictionary<string, JsonElement>>? overrides,
        string table,
        string recordId)
    {
        if (overrides is null) return null;
        if (!overrides.TryGetValue(table, out var records)) return null;
        return records.TryGetValue(recordId, out var rec) ? rec : null;
    }

    private static void SetGameDataOverride(
        Dictionary<string, Dictionary<string, JsonElement>> overrides,
        string table,
        string recordId,
        JsonElement value)
    {
        if (!overrides.TryGetValue(table, out var records))
        {
            records = new();
            overrides[table] = records;
        }
        records[recordId] = value;
    }

    private static bool RemoveGameDataOverride(
        Dictionary<string, Dictionary<string, JsonElement>>? overrides,
        string table,
        string recordId)
    {
        if (overrides is null) return false;
        if (!overrides.TryGetValue(table, out var records)) return false;
        return records.Remove(recordId);
    }
}
