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

    // Reverse index for resolving a room "You notice ..." entry (e.g.
    // "a long sword") back to its item Number. Keyed by the normalized
    // name (article/count stripped, lowercased) so loose room wording
    // matches the canonical MDB Name. First-write-wins on collisions —
    // duplicate display names are rare and the first id is as good as
    // any for an auto-get decision.
    private readonly Dictionary<string, int> _byNormalizedName = new();

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
    /// Resolve a single room "You notice ..." entry (e.g.
    /// <c>"a long sword"</c>) to its item <c>Number</c>, or <c>null</c>
    /// when nothing in the active set matches. Leading articles
    /// (<c>a/an/the/some</c>) and a leading count are stripped before
    /// matching; an exact normalized hit is preferred, falling back to a
    /// de-pluralized form (drop a trailing <c>s</c>). Cash entries
    /// (<c>"500 gold coins"</c>) won't be in <c>Items.json</c> and so
    /// return <c>null</c> — the caller skips them naturally.
    /// </summary>
    public int? FindByName(string roomEntry)
    {
        if (string.IsNullOrWhiteSpace(roomEntry)) return null;
        string key = Normalize(roomEntry);
        if (key.Length == 0) return null;

        if (_byNormalizedName.TryGetValue(key, out int number))
            return number;

        // De-plural fallback: room may say "two torches" → "torch".
        if (key.EndsWith('s')
            && _byNormalizedName.TryGetValue(key[..^1], out number))
            return number;

        return null;
    }

    /// <summary>
    /// Lower-case, trim, and strip a leading article / count token so a
    /// loose room phrasing collapses to the canonical item name shape.
    /// Shared by the reverse-index build and the
    /// <see cref="FindByName"/> lookup so both sides agree on the key.
    /// </summary>
    private static string Normalize(string raw)
    {
        string s = raw.Trim().ToLowerInvariant();

        // Drop a leading "and " left over from list-splitting safety.
        if (s.StartsWith("and ", StringComparison.Ordinal))
            s = s[4..].TrimStart();

        // Strip a single leading article.
        foreach (string article in _articles)
        {
            if (s.StartsWith(article, StringComparison.Ordinal))
            {
                s = s[article.Length..].TrimStart();
                break;
            }
        }

        // Strip a leading count token (digits or a spelled small number).
        int sp = s.IndexOf(' ');
        if (sp > 0)
        {
            string first = s[..sp];
            if (IsCountToken(first))
                s = s[(sp + 1)..].TrimStart();
        }

        return s.Trim();
    }

    private static readonly string[] _articles = { "the ", "an ", "a ", "some " };

    private static bool IsCountToken(string token)
    {
        if (token.Length == 0) return false;
        bool allDigits = true;
        foreach (char c in token)
            if (!char.IsDigit(c)) { allDigits = false; break; }
        if (allDigits) return true;
        return token is "one" or "two" or "three" or "four" or "five"
            or "six" or "seven" or "eight" or "nine" or "ten";
    }

    /// <summary>
    /// Reload the store from <paramref name="setName"/>'s
    /// <c>Items.json</c>. Pass <c>null</c> to clear. Wired by
    /// <see cref="AppServices"/> to
    /// <see cref="GameDataCache.ActiveSetChanged"/>.
    /// </summary>
    public void OnActiveSetChanged(string? setName)
    {
        _names.Clear();
        _byNormalizedName.Clear();
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
            string key = Normalize(name);
            if (key.Length > 0)
                _byNormalizedName.TryAdd(key, number);
            parsed++;
        }

        _cache.EvictTable("Items");

        _log?.Log(LogSeverity.Info, "ItemNameStore",
            $"Loaded {parsed} item name(s) from '{setName}'.");

        StoreReloaded?.Invoke();
    }
}
