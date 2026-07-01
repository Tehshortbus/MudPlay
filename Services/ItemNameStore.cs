using System.Collections.Generic;
using System.Text.Json;

namespace FujinTerm.Services;

/// <summary>
/// Lightweight in-memory index of <c>Items.json</c> for the active
/// game-data set, mapping the MDB <c>Number</c> field to its
/// <c>Name</c>. Used by walker / handler code that needs to resolve
/// an item id back to the verbatim name to send to the game (door
/// keys via <c>use &lt;name&gt; &lt;dir&gt;</c>, tickets via
/// inventory checks, etc.). Also exposes two slot-filtered name lists
/// (<see cref="WeaponNames"/> / <see cref="OffHandNames"/>) for the
/// Settings → Combat typeahead boxes.
/// </summary>
/// <remarks>
/// Subscribes to <see cref="GameDataCache.ActiveSetChanged"/>, loads
/// the raw <c>Items.json</c>, populates the int → string map plus the
/// slot-filtered name lists in a single pass, and evicts the raw
/// <see cref="JsonDocument"/>. Only the fields these indexes need
/// (Number, Name, ItemType, Worn, Encum) are retained — full item editing
/// is owned by the Game Data browser and reads its own copy.
/// </remarks>
public sealed class ItemNameStore
{
    private readonly GameDataCache _cache;
    private readonly LogService? _log;
    private readonly Dictionary<int, string> _names = new();

    // Slot-filtered, alphabetically-sorted, de-duplicated name lists
    // for the Combat-tab typeahead boxes. Classification follows MMUD
    // Explorer parity (frmBSCalc.frm / frmMain.frm): a weapon is
    // ItemType == 1; an off-hand item is Worn == 12 (shields, tomes,
    // the bard lute, etc.). No class dual-wields, so one-handed weapons
    // never appear in the off-hand list.
    private string[] _weaponNames = Array.Empty<string>();
    private string[] _offHandNames = Array.Empty<string>();

    // Reverse index for resolving a room "You notice ..." entry (e.g.
    // "a long sword") back to its item Number. Keyed by the normalized
    // name (article/count stripped, lowercased) so loose room wording
    // matches the canonical MDB Name. First-write-wins on collisions —
    // duplicate display names are rare and the first id is as good as
    // any for an auto-get decision.
    private readonly Dictionary<string, int> _byNormalizedName = new();

    // Item Number → Encum (carry weight). Lets InventoryManager adjust the
    // live encumbrance estimate when an item enters / leaves the pack between
    // full 'i' dumps (see WeightOf).
    private readonly Dictionary<int, int> _encumByNumber = new();

    /// <summary>Active set the store was last loaded from, or <c>null</c> if empty.</summary>
    public string? ActiveSet { get; private set; }

    /// <summary>
    /// Alphabetically-sorted, distinct names of every weapon
    /// (<c>ItemType == 1</c>) in the active set. Suggestion source for
    /// the Combat-tab weapon typeahead boxes. Empty when no set is
    /// active.
    /// </summary>
    public IReadOnlyList<string> WeaponNames => _weaponNames;

    /// <summary>
    /// Alphabetically-sorted, distinct names of every off-hand item
    /// (<c>Worn == 12</c> — shields, tomes, instruments) in the active
    /// set. Suggestion source for the Combat-tab off-hand typeahead
    /// boxes. Empty when no set is active.
    /// </summary>
    public IReadOnlyList<string> OffHandNames => _offHandNames;

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
    /// Carry weight (MDB <c>Encum</c>) of the item a game display name refers
    /// to, or <c>null</c> when nothing in the active set matches. Name matching
    /// reuses <see cref="FindByName"/>'s article/count normalization, so
    /// "a torch" / "torch" both resolve. Used by the inventory tracker to move
    /// the encumbrance estimate as items enter / leave the pack.
    /// </summary>
    public int? WeightOf(string displayName)
        => FindByName(displayName) is int number
           && _encumByNumber.TryGetValue(number, out int encum)
            ? encum : null;

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

    /// <summary>Read an integer field from a JSON row, or <c>0</c> when
    /// the property is missing / non-numeric. Used for the slot-classifier
    /// reads (ItemType / Worn) where absent means "not that slot".</summary>
    private static int ReadInt(JsonElement row, string property)
        => row.TryGetProperty(property, out JsonElement el)
           && el.ValueKind == JsonValueKind.Number
           && el.TryGetInt32(out int v)
            ? v : 0;

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
        _encumByNumber.Clear();
        _weaponNames = Array.Empty<string>();
        _offHandNames = Array.Empty<string>();
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
        SortedSet<string> weapons = new(StringComparer.OrdinalIgnoreCase);
        SortedSet<string> offHands = new(StringComparer.OrdinalIgnoreCase);
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
            _encumByNumber[number] = ReadInt(row, "Encum");

            if (ReadInt(row, "ItemType") == 1) weapons.Add(name);
            if (ReadInt(row, "Worn") == 12) offHands.Add(name);

            parsed++;
        }

        _weaponNames = new string[weapons.Count];
        weapons.CopyTo(_weaponNames);
        _offHandNames = new string[offHands.Count];
        offHands.CopyTo(_offHandNames);

        _cache.EvictTable("Items");

        _log?.Log(LogSeverity.Info, "ItemNameStore",
            $"Loaded {parsed} item name(s) from '{setName}' "
            + $"({_weaponNames.Length} weapon, {_offHandNames.Length} off-hand).");

        StoreReloaded?.Invoke();
    }
}
