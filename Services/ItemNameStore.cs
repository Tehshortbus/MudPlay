using System.Collections.Generic;
using System.Text.Json;

namespace FujinTerm.Services;

/// <summary>
/// Lightweight in-memory index of <c>Items.json</c> for the active
/// game-data set, mapping the MDB <c>Number</c> field to its
/// <c>Name</c>. Used by walker / handler code that needs to resolve
/// an item id back to the verbatim name to send to the game (door
/// keys via <c>use &lt;name&gt; &lt;dir&gt;</c>, tickets via
/// inventory checks, etc.).
/// </summary>
/// <remarks>
/// Subscribes to <see cref="GameDataCache.ActiveSetChanged"/>, loads
/// the raw <c>Items.json</c>, populates the int → string map, and
/// evicts the raw <see cref="JsonDocument"/>. Only Number + Name are
/// retained — full item editing is owned by the Game Data browser
/// and reads its own copy.
/// </remarks>
public sealed class ItemNameStore
{
    private readonly GameDataCache _cache;
    private readonly LogService? _log;
    private readonly Dictionary<int, string> _names = new();

    /// <summary>Active set the store was last loaded from, or <c>null</c> if empty.</summary>
    public string? ActiveSet { get; private set; }

    /// <summary>Number of entries in the active store.</summary>
    public int EntryCount => _names.Count;

    /// <summary>Fires after every successful (re)load, including the transition to no-set-active.</summary>
    public event Action? StoreReloaded;

    public ItemNameStore(GameDataCache cache) : this(cache, log: null) { }

    public ItemNameStore(GameDataCache cache, LogService? log)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
    }

    /// <summary>
    /// Get the canonical name for the given item id, or <c>null</c>
    /// when the id isn't in the active set. The returned string is
    /// the verbatim MDB <c>Name</c> — fed straight into the game's
    /// <c>use &lt;name&gt; &lt;dir&gt;</c> verb.
    /// </summary>
    public string? GetName(int itemId)
        => _names.TryGetValue(itemId, out string? name) ? name : null;

    /// <summary>
    /// Reload the store from <paramref name="setName"/>'s
    /// <c>Items.json</c>. Pass <c>null</c> to clear. Wired by
    /// <see cref="AppServices"/> to
    /// <see cref="GameDataCache.ActiveSetChanged"/>.
    /// </summary>
    public void OnActiveSetChanged(string? setName)
    {
        _names.Clear();
        ActiveSet = setName;

        if (string.IsNullOrWhiteSpace(setName))
        {
            _log?.Log(LogSeverity.Info, "ItemNameStore", "No active set; cleared.");
            StoreReloaded?.Invoke();
            return;
        }

        JsonDocument? doc = _cache.GetRawTable("Items");
        if (doc is null)
        {
            _log?.Log(LogSeverity.Info, "ItemNameStore",
                $"Active set '{setName}' has no Items.json; empty.");
            StoreReloaded?.Invoke();
            return;
        }

        int parsed = 0;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!row.TryGetProperty("Number", out JsonElement nEl)
                || nEl.ValueKind != JsonValueKind.Number
                || !nEl.TryGetInt32(out int number)
                || number <= 0)
                continue;
            if (!row.TryGetProperty("Name", out JsonElement nameEl)
                || nameEl.ValueKind != JsonValueKind.String)
                continue;
            string? name = nameEl.GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            _names[number] = name;
            parsed++;
        }

        _cache.EvictTable("Items");

        _log?.Log(LogSeverity.Info, "ItemNameStore",
            $"Loaded {parsed} item name(s) from '{setName}'.");

        StoreReloaded?.Invoke();
    }
}
