using System.Collections.Generic;
using System.Text.Json;

namespace MudPlay.Services;

// In-memory index of Items.json for the active game-data set, mapping the
// MDB Number field to its Name. Walker / handler code resolves an item id
// back to the verbatim name to send to the game (door keys via
// use <name> <dir>, tickets via inventory checks, etc.). Also exposes the
// off-hand name list (OffHandNames) the equipment classifier reads.
//
// Subscribes to GameDataCache.ActiveSetChanged, loads the Items table,
// populates the int → string map plus the off-hand list in a single pass,
// then evicts the table. Only the fields these indexes need (Number, Name,
// ItemType, Worn, Encum, Price, WeaponType, ArmourType and a carried item's
// Stealth) are retained — full item editing is owned by
// the Game Data browser and reads its own copy.
public sealed class ItemNameStore
{
    private readonly GameDataCache _cache;
    private readonly LogService? _log;
    private readonly Dictionary<int, string> _names = new();

    // Alphabetically-sorted, de-duplicated off-hand names: Worn == 12 (shields,
    // tomes, the bard lute, etc.).
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

    // Item Number → ItemType (MDB slot/kind code). Lets the auto-open engine
    // confirm a flagged item is actually a container (ItemType == 8) before
    // sending open (see ItemTypeOf).
    private readonly Dictionary<int, int> _itemTypeByNumber = new();
    private readonly Dictionary<int, int> _priceByNumber = new();

    // Item Number → WeaponType / ArmourType (MDB subtype codes, meaningful only
    // when ItemType is Weapon / Armour respectively). Lets Roomba Mode's room
    // labels narrow past the top-level category (see WeaponTypeOf / ArmourTypeOf).
    private readonly Dictionary<int, int> _weaponTypeByNumber = new();
    private readonly Dictionary<int, int> _armourTypeByNumber = new();

    // Item Number → the Stealth (ability 27) it adds or takes away just by being in
    // the pack. The game totals a carried item's abilities when it is not a weapon
    // and has no wear slot (GAME_MECHANICS "Abilities of carried items"): a log raft
    // is Stealth -125 for as long as it is held. Only items with a value are kept.
    private readonly Dictionary<int, int> _packStealthByNumber = new();
    private const int StealthAbilityCode = 27;
    private const int ItemAbilitySlots = 20;
    private static readonly string[] AbilFields = NumberedFields.Of("Abil-", ItemAbilitySlots);
    private static readonly string[] AbilValFields = NumberedFields.Of("AbilVal-", ItemAbilitySlots);

    public string? ActiveSet { get; private set; }

    // What carrying the named item does to Stealth: negative for a raft, 0 for
    // anything worn, wielded, unknown or without a Stealth ability.
    public int PackStealthOf(string displayName)
        => FindByName(displayName) is int number
           && _packStealthByNumber.TryGetValue(number, out int stealth)
            ? stealth : 0;

    // Alphabetically-sorted, distinct names of every off-hand item
    // (Worn == 12 — shields, tomes, instruments) in the active set, which the
    // equipment classifier checks an off-hand against. Empty when no set is
    // active.
    public IReadOnlyList<string> OffHandNames => _offHandNames;

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

    // True when the text is an item's record name as it stands: no article, no
    // count, nothing reworded. The engine's own lines print that name verbatim,
    // where a textblock's flavour text ("a green potion", "the heavy bloodstone
    // orb") words the item its own way, so this tells the two apart where
    // FindByName's loose match would accept both.
    public bool IsRecordName(string text)
        => FindByName(text) is int number
           && string.Equals(GetName(number), text, StringComparison.OrdinalIgnoreCase);

    // Carry weight (MDB Encum) of the item a game display name refers to, or
    // null when nothing in the active set matches. Name matching reuses
    // FindByName's article/count normalization, so "a torch" / "torch" both
    // resolve.
    public int? WeightOf(string displayName)
        => FindByName(displayName) is int number
           && _encumByNumber.TryGetValue(number, out int encum)
            ? encum : null;

    // Equip-location code (MDB Worn) of the item a game display name refers
    // to, or null when nothing in the active set matches. Name matching
    // reuses FindByName's article/count normalization. Lets a freshly-worn piece
    // be slotted by its real location when the wear line itself names none.
    public int? WornCodeOf(string displayName)
        => FindByName(displayName) is int number
           && _wornByNumber.TryGetValue(number, out int worn)
            ? worn : null;

    // ItemType (MDB kind code) of the item id, or null when the id isn't in
    // the active set.
    public int? ItemTypeOf(int number)
        => _itemTypeByNumber.TryGetValue(number, out int t) ? t : null;

    // Base Price (buy cost before shop markup) of the item id, or null when the id
    // isn't in the active set. Used to pick the cheapest counter among a hazard's
    // any-of options when the route picker offers "obtain, then cross".
    public int? PriceOf(int number)
        => _priceByNumber.TryGetValue(number, out int p) ? p : null;

    // Subtype accessors (WeaponType / ArmourType / Worn). The int? here means "id
    // known?" — NOT "is this the right category?": the population loop writes an
    // entry for every parsed item and a missing MDB column reads as 0, so a KNOWN
    // id always returns a non-null code (0 = the first enum value), and only an
    // unknown id returns null. So a caller must gate on ItemType before reading a
    // subtype — GhItemClassifier.Matches does, comparing WeaponType only for a
    // Weapon and ArmourType only for Armour — or it would compare 0 cross-category.

    // WeaponType (MDB subtype code, meaningful only when ItemType is Weapon)
    // of the item id, or null when the id isn't in the active set.
    public int? WeaponTypeOf(int number)
        => _weaponTypeByNumber.TryGetValue(number, out int t) ? t : null;

    // ArmourType (MDB subtype code, meaningful only when ItemType is Armour)
    // of the item id, or null when the id isn't in the active set.
    public int? ArmourTypeOf(int number)
        => _armourTypeByNumber.TryGetValue(number, out int t) ? t : null;

    // Worn (MDB equip-slot code) of the item id, or null when the id isn't in
    // the active set — what tells Neck / Wrist / Off-Hand items apart, which
    // ItemType and ArmourType don't. Backed by the same _wornByNumber index
    // WornCodeOf uses.
    public int? WornOf(int number)
        => _wornByNumber.TryGetValue(number, out int w) ? w : null;

    // Lower-case, trim, and strip a leading article / count token so a loose
    // room phrasing collapses to the canonical item name shape. Shared by the
    // reverse-index build and the FindByName lookup so both sides agree on
    // the key — and by DeathRecoveryManager's stock ground-get matching, which
    // compares a deathpile name against a "You notice"/"You took" phrasing.
    internal static string Normalize(string raw)
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

    // Read an integer field from a row, or 0 when the property is
    // missing / non-numeric. Used for the slot-classifier reads
    // (ItemType / Worn) where absent means "not that slot".
    private static int ReadInt(GameRow row, string property)
        => row.TryGetProperty(property, out GameValue el)
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

    private static int PackStealth(GameRow row)
    {
        int total = 0;
        for (int i = 0; i < ItemAbilitySlots; i++)
            if (ReadInt(row, AbilFields[i]) == StealthAbilityCode)
                total += ReadInt(row, AbilValFields[i]);
        return total;
    }

    // Reload the store from setName's Items.json. Pass null to clear. Wired
    // by AppServices to GameDataCache.ActiveSetChanged.
    public void OnActiveSetChanged(string? setName)
    {
        _names.Clear();
        _byNormalizedName.Clear();
        _encumByNumber.Clear();
        _wornByNumber.Clear();
        _itemTypeByNumber.Clear();
        _priceByNumber.Clear();
        _weaponTypeByNumber.Clear();
        _armourTypeByNumber.Clear();
        _packStealthByNumber.Clear();
        _offHandNames = Array.Empty<string>();
        ActiveSet = setName;

        if (string.IsNullOrWhiteSpace(setName))
        {
            _log?.Log(LogSeverity.Info, "ItemNameStore", "No active set; cleared.");
            return;
        }

        GameTable? doc = _cache.GetTable("Items");
        if (doc is null)
        {
            _log?.Log(LogSeverity.Info, "ItemNameStore",
                $"Active set '{setName}' has no Items.json; empty.");
            return;
        }

        int parsed = 0;
        SortedSet<string> offHands = new(StringComparer.OrdinalIgnoreCase);
        foreach (GameRow row in doc.Rows)
        {
            if (!row.TryGetProperty("Number", out GameValue nEl)
                || nEl.ValueKind != JsonValueKind.Number
                || !nEl.TryGetInt32(out int number)
                || number <= 0)
                continue;
            if (!row.TryGetProperty("Name", out GameValue nameEl)
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
            int itemType = ReadInt(row, "ItemType");
            _itemTypeByNumber[number] = itemType;
            _priceByNumber[number] = ReadInt(row, "Price");
            _weaponTypeByNumber[number] = ReadInt(row, "WeaponType");
            _armourTypeByNumber[number] = ReadInt(row, "ArmourType");

            if (worn == 12) offHands.Add(name);
            if (itemType != 1 && worn == 0 && PackStealth(row) is int packStealth and not 0)
                _packStealthByNumber[number] = packStealth;

            parsed++;
        }

        _offHandNames = new string[offHands.Count];
        offHands.CopyTo(_offHandNames);

        _cache.EvictTable("Items");

        _log?.Log(LogSeverity.Info, "ItemNameStore",
            $"Loaded {parsed} item name(s) from '{setName}' "
            + $"({_offHandNames.Length} off-hand).");

    }
}
