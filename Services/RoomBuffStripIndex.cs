using System.Collections.Generic;
using System.Text.Json;

namespace FujinTerm.Services;

// In-memory set of the active game-data set's room-entry spells that STRIP player
// buffs on entry — a room whose cast-on-enter spell (Room.Spell) removes or
// dispels magical effects. Backs the auto-buff suppression gate: re-casting a buff
// in a room that immediately strips it just burns mana, so CastingDirector skips
// the Buffing category while the player stands in such a room.
//
// A room-entry spell strips buffs when any member of its EndCast chain carries a
// buff-removal ability: RemovesSpell (Abil 122 — removes a specific spell effect)
// or DispellMagic (Abil 73 — dispels magical effects). Both are the game's own
// encoding of "this takes your buffs away", so keying on the data lets the client
// learn strip rooms per set without a hand-maintained list.
//
// Mirrors RoomHazardIndex: subscribes to GameDataCache.ActiveSetChanged, reads
// Rooms (for the room-entry spell set) and Spells (for the ability chains) once,
// builds the set, and evicts the JsonDocuments it touched.
public sealed class RoomBuffStripIndex
{
    private const int SpellAbilSlots = 10;   // Spells: Abil-0..9
    private const int AbilDispelMagic = 73;
    private const int AbilRemovesSpell = 122;
    private const int AbilEndCast = 151;
    private const int MaxChainDepth = 16;

    private readonly GameDataCache _cache;
    private readonly LogService? _log;
    private readonly HashSet<int> _stripSpells = new();

    // Set the index was last built from, or null if empty.
    public string? ActiveSet { get; private set; }

    // Number of distinct buff-stripping room-entry spells indexed.
    public int StripSpellCount => _stripSpells.Count;

    // Fires after every successful (re)load, including the transition to
    // no-set-active.
    public event Action? StoreReloaded;

    public RoomBuffStripIndex(GameDataCache cache, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
    }

    // True when a room's cast-on-enter spell strips buffs. Pass Room.Spell (0 =
    // no room spell). Benign, unknown, and zero spells all read false.
    public bool StripsBuffs(int spell)
        => spell > 0 && _stripSpells.Contains(spell);

    // Reload the index for setName. Pass null to clear. Wired by AppServices to
    // GameDataCache.ActiveSetChanged.
    public void OnActiveSetChanged(string? setName)
    {
        _stripSpells.Clear();
        ActiveSet = setName;

        if (string.IsNullOrWhiteSpace(setName))
        {
            _log?.Info("RoomBuffStripIndex", "No active set; cleared.");
            StoreReloaded?.Invoke();
            return;
        }

        JsonDocument? rooms = _cache.GetRawTable("Rooms");
        JsonDocument? spells = _cache.GetRawTable("Spells");
        if (rooms is null || spells is null)
        {
            _log?.Info("RoomBuffStripIndex",
                $"Active set '{setName}' missing Rooms/Spells; empty.");
            StoreReloaded?.Invoke();
            return;
        }

        // Only spells that actually appear as a Room.Spell are candidates — this
        // keeps the offensive dispel/attack-spell table out of the scan.
        HashSet<int> roomSpells = CollectRoomSpells(rooms);
        Dictionary<int, int[]> spellAbils = ReadSpellAbils(spells);

        foreach (int spell in roomSpells)
        {
            if (ChainStripsBuffs(spell, 0, spellAbils, new HashSet<int>()))
                _stripSpells.Add(spell);
        }

        _cache.EvictTable("Rooms");
        _cache.EvictTable("Spells");

        _log?.Info("RoomBuffStripIndex",
            $"Indexed {_stripSpells.Count} buff-stripping room-entry spell(s) from '{setName}'.");

        StoreReloaded?.Invoke();
    }

    // Depth-first walk of a spell's EndCast chain. Returns true when any chain
    // member carries a buff-removal ability (RemovesSpell / DispellMagic).
    private bool ChainStripsBuffs(
        int spell, int depth, Dictionary<int, int[]> spellAbils, HashSet<int> seen)
    {
        if (depth > MaxChainDepth || spell <= 0 || !seen.Add(spell)) return false;
        if (!spellAbils.TryGetValue(spell, out int[]? abil)) return false;

        for (int k = 0; k < SpellAbilSlots; k++)
        {
            int a = abil[k * 2];
            int v = abil[k * 2 + 1];
            if (a is AbilRemovesSpell or AbilDispelMagic) return true;
            if (a == AbilEndCast && v > 0
                && ChainStripsBuffs(v, depth + 1, spellAbils, seen))
                return true;
        }
        return false;
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

    // Spell Number → flat [Abil0, Val0, Abil1, Val1, …] slot array (2 * SpellAbilSlots).
    private static Dictionary<int, int[]> ReadSpellAbils(JsonDocument spells)
    {
        Dictionary<int, int[]> map = new();
        foreach (JsonElement row in spells.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!TryReadInt(row, "Number", out int number) || number <= 0) continue;

            int[] slots = new int[SpellAbilSlots * 2];
            for (int k = 0; k < SpellAbilSlots; k++)
            {
                TryReadInt(row, $"Abil-{k}", out slots[k * 2]);
                TryReadInt(row, $"AbilVal-{k}", out slots[k * 2 + 1]);
            }
            map[number] = slots;
        }
        return map;
    }

    private static bool TryReadInt(JsonElement row, string property, out int value)
    {
        value = 0;
        return row.TryGetProperty(property, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }
}
