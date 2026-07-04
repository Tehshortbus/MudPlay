using System.Collections.Generic;
using System.Text.Json;

namespace FujinTerm.Services;

// In-memory index of Items.json for the active game-data set, mapping the
// MDB Number field to its Name. Walker / handler code resolves an item id
// back to the verbatim name to send to the game (door keys via
// use <name> <dir>, tickets via inventory checks, etc.). Also exposes two
// slot-filtered name lists (WeaponNames / OffHandNames) for the
// Settings → Combat typeahead boxes.
//
// Subscribes to GameDataCache.ActiveSetChanged, loads the raw Items.json,
// populates the int → string map plus the slot-filtered name lists in a
// single pass, then evicts the raw JsonDocument. Only the fields these
// indexes need (Number, Name, ItemType, Worn, Encum) are retained — full
// item editing is owned by the Game Data browser and reads its own copy.
public sealed class ItemNameStore
{
    private readonly GameDataCache _cache;
    private readonly LogService? _log;
    private readonly Dictionary<int, string> _names = new();

    // Slot-filtered, alphabetically-sorted, de-duplicated name lists
    // for the Combat-tab typeahead boxes. Classification: a weapon is
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

    // Item Number → Worn (equip-location code). Lets InventoryManager resolve the
    // true slot for a freshly-worn piece — the "You are now wearing X." line names
    // no slot — so the worn set carries its real placement between full 'i' dumps
    // (see WornCodeOf).
    private readonly Dictionary<int, int> _wornByNumber = new();

    public string? ActiveSet { get; private set; }

    // Alphabetically-sorted, distinct names of every weapon (ItemType == 1)
    // in the active set — suggestion source for the Combat-tab weapon
    // typeahead boxes. Empty when no set is active.
    public IReadOnlyList<string> WeaponNames => _weaponNames;

    // Alphabetically-sorted, distinct names of every off-hand item
    // (Worn == 12 — shields, tomes, instruments) in the active set —
    // suggestion source for the Combat-tab off-hand typeahead boxes.
    // Empty when no set is active.
    public IReadOnlyList<string> OffHandNames => _offHandNames;

    public int EntryCount => _names.Count;

    // Fires after every successful (re)load, including the transition to no-set-active.
    public event Action? StoreReloaded;

    public ItemNameStore(GameDataCache cache) : this(cache, log: null) { }

    public ItemNameStore(GameDataCache cache, LogService? log)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
    }

    // Canonical name for the given item id, or null when the id isn't in the
    // active set. The returned string is the verbatim MDB Name — fed straight
    // into the game's use <name> <dir> verb.
    public string? GetName(int itemId)
        => _names.TryGetValue(itemId, out string? name) ? name : null;

    // Resolve a single room "You notice ..." entry (e.g. "a long sword") to
    // its item Number, or null when nothing in the active set matches.
    // Leading articles (a/an/the/some) and a leading count are stripped
    // before matching; an exact normalized hit is preferred, falling back to
    // a de-pluralized form (drop a trailing s). Cash entries ("500 gold
    // coins") won't be in Items.json and so return null — the caller skips
    // them naturally.
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

    // Carry weight (MDB Encum) of the item a game display name refers to, or
    // null when nothing in the active set matches. Name matching reuses
    // FindByName's article/count normalization, so "a torch" / "torch" both
    // resolve. Used by the inventory tracker to move the encumbrance estimate
    // as items enter / leave the pack.
    public int? WeightOf(string displayName)
        => FindByName(displayName) is int number
           && _encumByNumber.TryGetValue(number, out int encum)
            ? encum : null;

    // Equip-location code (MDB Worn) of the item a game display name refers
    // to, or null when nothing in the active set matches. Name matching
    // reuses FindByName's article/count normalization. Used by the inventory
    // tracker to slot a freshly-worn piece by its real location when the wear
    // line itself names none.
    public int? WornCodeOf(string displayName)
        => FindByName(displayName) is int number
           && _wornByNumber.TryGetValue(number, out int worn)
            ? worn : null;

    // Lower-case, trim, and strip a leading article / count token so a loose
    // room phrasing collapses to the canonical item name shape. Shared by the
    // reverse-index build and the FindByName lookup so both sides agree on
    // the key.
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

    // Read an integer field from a JSON row, or 0 when the property is
    // missing / non-numeric. Used for the slot-classifier reads
    // (ItemType / Worn) where absent means "not that slot".
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

    // Reload the store from setName's Items.json. Pass null to clear. Wired
    // by AppServices to GameDataCache.ActiveSetChanged.
    public void OnActiveSetChanged(string? setName)
    {
        _names.Clear();
        _byNormalizedName.Clear();
        _encumByNumber.Clear();
        _wornByNumber.Clear();
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
            int worn = ReadInt(row, "Worn");
            _wornByNumber[number] = worn;

            if (ReadInt(row, "ItemType") == 1) weapons.Add(name);
            if (worn == 12) offHands.Add(name);

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
