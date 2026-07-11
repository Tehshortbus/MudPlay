using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace FujinTerm.Services;

// In-memory index of the active set's room-entry hazards: the harmful spell a
// room casts on the player when they step in (Room.Spell), mapped to the item(s)
// that make the room safe. Backs the navigation hazard-avoidance / gating pass —
// when a planned route would enter a room whose cast-on-enter spell harms and the
// player carries no counter item, the walker routes around it, offers the item as
// a path requirement, or (with the per-item auto-obtain flag) acquires it first.
//
// A room-entry spell is a *protectable* hazard when the 1.11p data ships an item
// that counters it. Three protection shapes are decoded, all reached by walking
// the spell's Abil-0..9 chain:
//   • Damage (Abil 1) / EndCast (Abil 151) death-timer chain — countered by an
//     item whose NegateSpell-0..9 cancels the spell or ANY member of its EndCast
//     chain (the gnomish fish-helm negates the black-water / freeze-drown chain).
//   • TextBlock (Abil 148) → TBInfo whose Action leads with `failitem <item#>` —
//     holding any listed item aborts the harmful chain (Silver River's rafts).
//   • TextBlock → TBInfo with `checkspell <spell#>` — a buff gate; safe while the
//     buff is active, which the player gets by carrying+using an item that casts
//     it (the desert's waterskin).
// Spells that harm but ship no counter (plain magma damage) are deliberately NOT
// indexed: routing around survivable terrain would strand legitimate paths, and
// the router only ever asks about a room it could actually make safe.
//
// Mirrors ShopStockIndex / MonsterDropIndex: subscribes to
// GameDataCache.ActiveSetChanged, reads the raw tables it needs once (Rooms for
// the room-entry spell set, Spells for the ability chains, Items for the
// NegateSpell / CastsSp reverse maps, TBInfo for the failitem / checkspell
// directives), builds the map, and evicts every JsonDocument it touched.
public sealed class RoomHazardIndex
{
    // One protectable room-entry hazard: the player is safe iff they carry at
    // least one item from EVERY requirement group. Multiple groups arise only
    // when a single spell layers protections (a damage negator AND a textblock
    // gate); the common case is a single group. Groups are never empty — an
    // uncounterable hazard is simply not indexed.
    public sealed class RoomHazard
    {
        public IReadOnlyList<IReadOnlyList<int>> RequirementGroups { get; }

        public RoomHazard(IReadOnlyList<IReadOnlyList<int>> groups)
        {
            RequirementGroups = groups;
        }

        // Every distinct protecting item across all groups — the set the route
        // requirement display and on-demand acquisition draw from.
        public IReadOnlyList<int> ProtectingItems =>
            RequirementGroups.SelectMany(static g => g).Distinct().ToArray();

        // The counters with no in-group alternative: every requirement group that
        // holds a single item. These are the items the route MUST carry (no
        // substitute exists), so they're the only ones safe to hand the
        // each-required path-item acquisition pipeline — an any-of group (2+
        // options) can't be expressed there without acquiring every option, so it
        // stays a manual choice the route picker still surfaces.
        public IReadOnlyList<int> MandatoryItems =>
            RequirementGroups.Where(static g => g.Count == 1)
                .Select(static g => g[0]).Distinct().ToArray();

        // True when the player carries at least one item from every group.
        public bool IsSatisfiedBy(Func<int, bool> carries)
        {
            ArgumentNullException.ThrowIfNull(carries);
            return RequirementGroups.All(g => g.Any(carries));
        }
    }

    private const int SpellAbilSlots = 10;   // Spells: Abil-0..9
    private const int ItemAbilSlots = 20;    // Items:  Abil-0..19
    private const int NegateSlots = 10;      // Items:  NegateSpell-0..9
    private const int AbilDamage = 1;
    private const int AbilCastsSp = 43;
    private const int AbilTextBlock = 148;
    private const int AbilEndCast = 151;
    private const int MaxChainDepth = 16;

    private readonly GameDataCache _cache;
    private readonly LogService? _log;
    private readonly Dictionary<int, RoomHazard> _hazardBySpell = new();

    // Set the index was last built from, or null if empty.
    public string? ActiveSet { get; private set; }

    // Number of distinct protectable room-entry spells indexed.
    public int HazardCount => _hazardBySpell.Count;

    // Fires after every successful (re)load, including the transition to
    // no-set-active.
    public event Action? StoreReloaded;

    public RoomHazardIndex(GameDataCache cache, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
    }

    // Hazard for a room's cast-on-enter spell, or null when the spell is benign,
    // unknown, or ships no counter item. Pass Room.Spell.
    public RoomHazard? HazardForSpell(int spell)
        => spell > 0 && _hazardBySpell.TryGetValue(spell, out RoomHazard? h) ? h : null;

    // Reload the index for setName. Pass null to clear. Wired by AppServices to
    // GameDataCache.ActiveSetChanged.
    public void OnActiveSetChanged(string? setName)
    {
        _hazardBySpell.Clear();
        ActiveSet = setName;

        if (string.IsNullOrWhiteSpace(setName))
        {
            _log?.Info("RoomHazardIndex", "No active set; cleared.");
            StoreReloaded?.Invoke();
            return;
        }

        JsonDocument? rooms = _cache.GetRawTable("Rooms");
        JsonDocument? spells = _cache.GetRawTable("Spells");
        if (rooms is null || spells is null)
        {
            _log?.Info("RoomHazardIndex",
                $"Active set '{setName}' missing Rooms/Spells; empty.");
            StoreReloaded?.Invoke();
            return;
        }

        // Only spells that actually appear as a Room.Spell are candidates — this
        // excludes the (damaging) attack-spell table from the harmful scan.
        HashSet<int> roomSpells = CollectRoomSpells(rooms);
        Dictionary<int, (int[] Abil, int[] Val)> spellAbils = ReadSpellAbils(spells);
        Dictionary<int, List<int>> negatorsBySpell = new();
        Dictionary<int, List<int>> castersBySpell = new();
        ReadItemReverseMaps(negatorsBySpell, castersBySpell);
        Dictionary<int, string> tbActions = ReadTbActions();

        foreach (int spell in roomSpells)
        {
            RoomHazard? hazard = BuildHazard(spell, spellAbils, negatorsBySpell, castersBySpell, tbActions);
            if (hazard is not null) _hazardBySpell[spell] = hazard;
        }

        _cache.EvictTable("Rooms");
        _cache.EvictTable("Spells");
        _cache.EvictTable("Items");
        _cache.EvictTable("TBInfo");

        _log?.Info("RoomHazardIndex",
            $"Indexed {_hazardBySpell.Count} protectable room-entry hazard(s) from '{setName}'.");

        StoreReloaded?.Invoke();
    }

    // Resolve one room-entry spell to a hazard, or null when it neither harms with
    // a counter nor gates on a held item.
    private RoomHazard? BuildHazard(
        int rootSpell,
        Dictionary<int, (int[] Abil, int[] Val)> spellAbils,
        Dictionary<int, List<int>> negatorsBySpell,
        Dictionary<int, List<int>> castersBySpell,
        Dictionary<int, string> tbActions)
    {
        HashSet<int> chain = new();
        List<int> textBlocks = new();
        bool damaging = WalkSpellChain(rootSpell, 0, spellAbils, chain, textBlocks);

        List<IReadOnlyList<int>> groups = new();

        // Damage / death-timer path: any item negating any chain member protects.
        if (damaging)
        {
            List<int> negators = new();
            foreach (int member in chain)
            {
                if (!negatorsBySpell.TryGetValue(member, out List<int>? items)) continue;
                foreach (int itemId in items)
                    if (!negators.Contains(itemId)) negators.Add(itemId);
            }
            if (negators.Count > 0) groups.Add(negators);
        }

        // TextBlock paths: failitem (hold to abort) and checkspell (carry the buff
        // source) each contribute a requirement group.
        HashSet<int> visitedTb = new();
        foreach (int tb in textBlocks)
            ScanTextBlock(tb, 0, tbActions, castersBySpell, visitedTb, groups);

        return groups.Count > 0 ? new RoomHazard(groups) : null;
    }

    // Depth-first walk of a spell's EndCast chain. Returns true when any chain
    // member deals damage; appends every TextBlock (Abil 148) target it reaches.
    private bool WalkSpellChain(
        int spell, int depth,
        Dictionary<int, (int[] Abil, int[] Val)> spellAbils,
        HashSet<int> chain, List<int> textBlocks)
    {
        if (depth > MaxChainDepth || spell <= 0 || !chain.Add(spell)) return false;
        if (!spellAbils.TryGetValue(spell, out (int[] Abil, int[] Val) ab)) return false;

        bool damaging = false;
        for (int k = 0; k < SpellAbilSlots; k++)
        {
            int a = ab.Abil[k];
            int v = ab.Val[k];
            if (a == AbilDamage) damaging = true;
            else if (a == AbilEndCast && v > 0)
                damaging |= WalkSpellChain(v, depth + 1, spellAbils, chain, textBlocks);
            else if (a == AbilTextBlock && v > 0 && !textBlocks.Contains(v))
                textBlocks.Add(v);
        }
        return damaging;
    }

    // Scan one TBInfo Action for the two item-gate directives. failitem items form
    // one group; checkspell buff-source items form another. Forwards through an
    // empty Action's LinkTo (dialogue-only blocks) but does not chase the failure
    // branches — those describe what happens WITHOUT protection, not a source of it.
    private void ScanTextBlock(
        int tb, int depth,
        Dictionary<int, string> tbActions,
        Dictionary<int, List<int>> castersBySpell,
        HashSet<int> visited, List<IReadOnlyList<int>> groups)
    {
        if (depth > MaxChainDepth || tb <= 0 || !visited.Add(tb)) return;
        if (!tbActions.TryGetValue(tb, out string? action) || string.IsNullOrWhiteSpace(action))
            return;

        List<int> failItems = new();
        List<int> checkspellCasters = new();

        foreach (string line in action.Split('\n'))
        {
            foreach (string raw in line.Split(':'))
            {
                string tok = raw.Trim();
                if (tok.Length == 0) continue;
                if (StartsWith(tok, "failitem"))
                {
                    int itemId = FirstIntAfter(tok, "failitem");
                    if (itemId > 0 && !failItems.Contains(itemId)) failItems.Add(itemId);
                }
                else if (StartsWith(tok, "checkspell"))
                {
                    int buffSpell = FirstIntAfter(tok, "checkspell");
                    if (buffSpell > 0 && castersBySpell.TryGetValue(buffSpell, out List<int>? casters))
                        foreach (int itemId in casters)
                            if (!checkspellCasters.Contains(itemId)) checkspellCasters.Add(itemId);
                }
            }
        }

        if (failItems.Count > 0) groups.Add(failItems);
        if (checkspellCasters.Count > 0) groups.Add(checkspellCasters);
    }

    private static HashSet<int> CollectRoomSpells(JsonDocument rooms)
    {
        HashSet<int> set = new();
        foreach (JsonElement row in rooms.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (TryReadInt(row, "Spell", out int spell) && spell > 0) set.Add(spell);
        }
        return set;
    }

    private static Dictionary<int, (int[] Abil, int[] Val)> ReadSpellAbils(JsonDocument spells)
    {
        Dictionary<int, (int[], int[])> map = new();
        foreach (JsonElement row in spells.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!TryReadInt(row, "Number", out int number) || number <= 0) continue;

            int[] abil = new int[SpellAbilSlots];
            int[] val = new int[SpellAbilSlots];
            for (int k = 0; k < SpellAbilSlots; k++)
            {
                TryReadInt(row, $"Abil-{k}", out abil[k]);
                TryReadInt(row, $"AbilVal-{k}", out val[k]);
            }
            map[number] = (abil, val);
        }
        return map;
    }

    private void ReadItemReverseMaps(
        Dictionary<int, List<int>> negatorsBySpell,
        Dictionary<int, List<int>> castersBySpell)
    {
        JsonDocument? items = _cache.GetRawTable("Items");
        if (items is null) return;

        foreach (JsonElement row in items.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!TryReadInt(row, "Number", out int itemId) || itemId <= 0) continue;

            for (int k = 0; k < NegateSlots; k++)
            {
                if (TryReadInt(row, $"NegateSpell-{k}", out int spell) && spell > 0)
                    Add(negatorsBySpell, spell, itemId);
            }
            for (int k = 0; k < ItemAbilSlots; k++)
            {
                if (TryReadInt(row, $"Abil-{k}", out int a) && a == AbilCastsSp
                    && TryReadInt(row, $"AbilVal-{k}", out int spell) && spell > 0)
                    Add(castersBySpell, spell, itemId);
            }
        }
    }

    private Dictionary<int, string> ReadTbActions()
    {
        Dictionary<int, string> map = new();
        JsonDocument? tb = _cache.GetRawTable("TBInfo");
        if (tb is null) return map;

        foreach (JsonElement row in tb.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!TryReadInt(row, "Number", out int number) || number <= 0) continue;
            if (row.TryGetProperty("Action", out JsonElement el)
                && el.ValueKind == JsonValueKind.String)
            {
                string? action = el.GetString();
                if (!string.IsNullOrEmpty(action)) map[number] = action;
            }
        }
        return map;
    }

    private static void Add(Dictionary<int, List<int>> map, int key, int value)
    {
        if (!map.TryGetValue(key, out List<int>? list)) map[key] = list = new List<int>();
        if (!list.Contains(value)) list.Add(value);
    }

    private static bool StartsWith(string tok, string keyword)
        => tok.StartsWith(keyword, StringComparison.OrdinalIgnoreCase);

    // First contiguous integer following keyword in tok (VB-val style: skip the
    // keyword and any spaces, read the digit run). 0 when none.
    private static int FirstIntAfter(string tok, string keyword)
    {
        int idx = tok.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;
        int i = idx + keyword.Length;
        while (i < tok.Length && !char.IsDigit(tok[i])) i++;
        int start = i;
        while (i < tok.Length && char.IsDigit(tok[i])) i++;
        return i > start
            ? int.Parse(tok.AsSpan(start, i - start), NumberStyles.Integer, CultureInfo.InvariantCulture)
            : 0;
    }

    private static bool TryReadInt(JsonElement row, string property, out int value)
    {
        value = 0;
        return row.TryGetProperty(property, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }
}
