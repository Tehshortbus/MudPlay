using System.Collections.Generic;
using System.Text.Json;

namespace MudPlay.Services;

// In-memory set of the active game-data set's room-entry spells that STRIP player
// buffs on entry — a room whose cast-on-enter spell (Room.Spell) removes or
// dispels magical effects. Backs the auto-buff suppression gate: re-casting a buff
// in a room that immediately strips it just burns mana, so CastingDirector skips
// the Buffing category while the player stands in such a room. It also tells the
// hazard-buff tracker which rooms end a buff it can only time (StripsBuff): "dying
// or walking through a room that purges buffs, negate magic" (user, 2026-10-10).
//
// A room-entry spell strips buffs when any member of its EndCast chain carries a
// buff-removal ability: RemovesSpell (Abil 122 — removes a specific spell effect)
// or DispellMagic (Abil 73 — dispels magical effects). Both are the game's own
// encoding of "this takes your buffs away", so keying on the data lets the client
// learn strip rooms per set without a hand-maintained list.
//
// For that tracker two more ways a room takes buffs off are read, each kept out of
// the cast pass's gate: a spell the room spell's textblock casts, and a spell a
// room command casts (asked whether those should end the tracked buff too: "yes",
// user, 2026-10-10). Negate magic ahead of a teleport is the usual one: `enter
// tapestry`, `enter portal`, `go courtyard`.
//
// Mirrors RoomHazardIndex: subscribes to GameDataCache.ActiveSetChanged, reads
// Rooms (for the room-entry spell and command sets), Spells (for the ability
// chains) and TBInfo (for the casts) once, builds the set, and evicts the
// tables it touched.
public sealed class RoomBuffStripIndex
{
    private const int SpellAbilSlots = 10;   // Spells: Abil-0..9
    private static readonly string[] AbilFields = NumberedFields.Of("Abil-", SpellAbilSlots);
    private static readonly string[] AbilValFields = NumberedFields.Of("AbilVal-", SpellAbilSlots);
    private const int AbilDispelMagic = 73;
    private const int AbilRemovesSpell = 122;
    private const int AbilEndCast = 151;
    private const int MaxChainDepth = 16;

    private readonly GameDataCache _cache;
    private readonly LogService? _log;
    // Per stripping room-entry spell, what its chain takes off: everything (a
    // DispellMagic ability: negate magic), and the spells its RemovesSpell abilities
    // name.
    private readonly Dictionary<int, (bool Dispels, HashSet<int> Removes)> _stripSpells = new();

    // Per room-entry spell, what a spell cast by its textblock takes off (Stock's
    // hydra boss check casts portal dispel that way). Kept apart from _stripSpells on
    // purpose: StripsBuffs is the cast pass's "don't buff in this room", asked of what
    // the room spell does of itself, and a textblock's cast can hang on a condition.
    private readonly Dictionary<int, (bool Dispels, HashSet<int> Removes)> _textblockStrips = new();

    // Per room command textblock (Room.Cmd), the command lines whose casts take
    // something off: `enter tapestry:…:cast 310:teleport …` casts negate magic.
    private readonly Dictionary<int, List<(string Keyword, bool Dispels, HashSet<int> Removes)>> _commandStrips = new();

    // Where ReadSpellAbils keeps a spell's MinBase / MaxBase, behind its ability
    // slots: a TextBlock ability with no value names its block there.
    private const int MinBaseSlot = SpellAbilSlots * 2;
    private const int MaxBaseSlot = MinBaseSlot + 1;

    // Set the index was last built from, or null if empty.
    public string? ActiveSet { get; private set; }

    public RoomBuffStripIndex(GameDataCache cache, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
    }

    // True when a room's cast-on-enter spell strips buffs. Pass Room.Spell (0 =
    // no room spell). Benign, unknown, and zero spells all read false.
    public bool StripsBuffs(int spell)
        => spell > 0 && _stripSpells.ContainsKey(spell);

    // True when a room's cast-on-enter spell takes this one buff off: its chain, or a
    // spell its textblock casts, dispels magic or removes the buff by number. For a
    // buff tracked by its clock alone (no wear-off line to read), where a room that
    // only removes other spells must not count: the drowning cure's rooms share a
    // map with the desert, and take nothing off but drowning.
    public bool StripsBuff(int roomSpell, int buffSpell)
        => roomSpell > 0
           && ((_stripSpells.TryGetValue(roomSpell, out (bool Dispels, HashSet<int> Removes) own) && Takes(own, buffSpell))
               || (_textblockStrips.TryGetValue(roomSpell, out (bool Dispels, HashSet<int> Removes) cast) && Takes(cast, buffSpell)));

    // True when a command of the room (its Room.Cmd textblock) casts a spell that
    // takes this one buff off. The keyword is matched whole, as the game matches it.
    // Says what the line would do, not that the game carried it out: a line's steps
    // run left to right, so a cast ahead of its conditions lands whatever comes of
    // them, and one behind them only if they passed.
    public bool CommandStripsBuff(int roomCmd, string command, int buffSpell)
    {
        if (roomCmd <= 0 || string.IsNullOrWhiteSpace(command)) return false;
        if (!_commandStrips.TryGetValue(roomCmd, out List<(string Keyword, bool Dispels, HashSet<int> Removes)>? lines))
            return false;
        string typed = command.Trim();
        foreach ((string keyword, bool dispels, HashSet<int> removes) in lines)
            if (keyword.Equals(typed, StringComparison.OrdinalIgnoreCase) && Takes((dispels, removes), buffSpell))
                return true;
        return false;
    }

    // A DispellMagic is read as taking everything. In the Stock engine one with a
    // value takes only the spells that carry that ability (GAME_MECHANICS "Cure-spell
    // identification — which spells REMOVE each ailment"); every one a room spell or
    // a room command reaches in the two sets has value 0, which takes them all.
    private static bool Takes((bool Dispels, HashSet<int> Removes) strip, int buffSpell)
        => strip.Dispels || strip.Removes.Contains(buffSpell);

    // Reload the index for setName. Pass null to clear. Wired by AppServices to
    // GameDataCache.ActiveSetChanged.
    public void OnActiveSetChanged(string? setName)
    {
        _stripSpells.Clear();
        _textblockStrips.Clear();
        _commandStrips.Clear();
        ActiveSet = setName;

        if (string.IsNullOrWhiteSpace(setName))
        {
            _log?.Info("RoomBuffStripIndex", "No active set; cleared.");
            return;
        }

        GameTable? rooms = _cache.GetTable("Rooms");
        GameTable? spells = _cache.GetTable("Spells");
        if (rooms is null || spells is null)
        {
            _log?.Info("RoomBuffStripIndex",
                $"Active set '{setName}' missing Rooms/Spells; empty.");
            return;
        }

        // Only spells that actually appear as a Room.Spell are candidates — this
        // keeps the offensive dispel/attack-spell table out of the scan.
        HashSet<int> roomSpells = CollectRoomSpells(rooms);
        Dictionary<int, int[]> spellAbils = ReadSpellAbils(spells);

        foreach (int spell in roomSpells)
        {
            bool dispels = false;
            HashSet<int> removes = new();
            if (ChainStripsBuffs(spell, 0, spellAbils, new HashSet<int>(), ref dispels, removes))
                _stripSpells[spell] = (dispels, removes);
        }

        IndexTextblockCasts(rooms, roomSpells, spellAbils);

        _cache.EvictTable("Rooms");
        _cache.EvictTable("Spells");
        _cache.EvictTable("TBInfo");

        _log?.Info("RoomBuffStripIndex",
            $"Indexed {_stripSpells.Count} buff-stripping room-entry spell(s) from '{setName}', "
            + $"{_textblockStrips.Count} more through a textblock, and {_commandStrips.Count} room command block(s) that cast one.");

    }

    // The strips a textblock's `cast` reaches: from a room spell's own textblock, and
    // from the command lines of a room's CMD block.
    private void IndexTextblockCasts(GameTable rooms, HashSet<int> roomSpells, Dictionary<int, int[]> spellAbils)
    {
        Dictionary<int, string> blocks = ReadTextBlockActions();
        if (blocks.Count == 0) return;

        foreach (int spell in roomSpells)
        {
            if (!spellAbils.TryGetValue(spell, out int[]? abil)) continue;
            bool dispels = false, strips = false;
            HashSet<int> removes = new();
            HashSet<int> seen = new();
            for (int k = 0; k < SpellAbilSlots; k++)
            {
                if (abil[k * 2] != Game.Spells.SpellTextBlock.AbilityCode) continue;
                int block = Game.Spells.SpellTextBlock.Number(abil[k * 2 + 1], abil[MinBaseSlot], abil[MaxBaseSlot]);
                strips |= BlockStrips(block, 0, blocks, spellAbils, seen, ref dispels, removes);
            }
            if (strips) _textblockStrips[spell] = (dispels, removes);
        }

        foreach (int cmd in CollectRoomCommands(rooms))
        {
            if (!blocks.TryGetValue(cmd, out string? action)) continue;
            List<(string Keyword, bool Dispels, HashSet<int> Removes)>? lines = null;
            foreach (string raw in action.Split('\n'))
            {
                // A command line leads with what the player types.
                string[] parts = raw.Split(':');
                string keyword = parts[0].Trim();
                if (parts.Length < 2 || keyword.Length == 0) continue;

                bool dispels = false;
                HashSet<int> removes = new();
                if (StepsStrip(parts, 1, 0, blocks, spellAbils, new HashSet<int> { cmd }, ref dispels, removes))
                    (lines ??= new()).Add((keyword, dispels, removes));
            }
            if (lines is not null) _commandStrips[cmd] = lines;
        }
    }

    // What the casts in a run of textblock steps take off: a `cast <spell>` by that
    // spell's own chain, and a step that hands over to another block by that block.
    private bool StepsStrip(
        string[] steps, int from, int depth, Dictionary<int, string> blocks, Dictionary<int, int[]> spellAbils,
        HashSet<int> seenBlocks, ref bool dispels, HashSet<int> removes)
    {
        bool strips = false;
        for (int i = from; i < steps.Length; i++)
        {
            string step = steps[i].Trim();
            if (LeadsWith(step, "cast"))
            {
                strips |= ChainStripsBuffs(FirstNumber(step), 0, spellAbils, new HashSet<int>(), ref dispels, removes);
                continue;
            }
            foreach (string flow in RoomHazardIndex.BranchFlowDirectives)
            {
                if (!LeadsWith(step, flow)) continue;
                strips |= BlockStrips(FirstNumber(step), depth + 1, blocks, spellAbils, seenBlocks, ref dispels, removes);
                break;
            }
        }
        return strips;
    }

    private bool BlockStrips(
        int block, int depth, Dictionary<int, string> blocks, Dictionary<int, int[]> spellAbils,
        HashSet<int> seenBlocks, ref bool dispels, HashSet<int> removes)
    {
        if (depth > MaxChainDepth || block <= 0 || !seenBlocks.Add(block)) return false;
        if (!blocks.TryGetValue(block, out string? action)) return false;
        bool strips = false;
        foreach (string line in action.Split('\n'))
            strips |= StepsStrip(line.Split(':'), 0, depth, blocks, spellAbils, seenBlocks, ref dispels, removes);
        return strips;
    }

    private static bool LeadsWith(string step, string word)
        => step.StartsWith(word, StringComparison.OrdinalIgnoreCase)
           && (step.Length == word.Length || step[word.Length] == ' ');

    // The first whole number in a step (`cast 310`, `random 4013`), 0 when it has none.
    private static int FirstNumber(string step)
    {
        foreach (string word in step.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(word, out int value)) return value;
        return 0;
    }

    // Read straight from the cache, not through TBInfoStore: the store reloads on the
    // same set-change event and nothing orders the two.
    private Dictionary<int, string> ReadTextBlockActions()
    {
        Dictionary<int, string> map = new();
        GameTable? table = _cache.GetTable("TBInfo");
        if (table is null) return map;
        foreach (GameRow row in table.Rows)
        {
            if (!TryReadInt(row, "Number", out int number) || number <= 0) continue;
            if (row.TryGetProperty("Action", out GameValue action) && action.ValueKind == JsonValueKind.String
                && action.GetString() is { Length: > 0 } text)
                map[number] = text;
        }
        return map;
    }

    private static HashSet<int> CollectRoomCommands(GameTable rooms)
    {
        HashSet<int> set = new();
        foreach (GameRow row in rooms.Rows)
        {
            if (TryReadInt(row, "CMD", out int cmd) && cmd > 0) set.Add(cmd);
        }
        return set;
    }

    // Depth-first walk of a spell's EndCast chain. Returns true when any chain
    // member carries a buff-removal ability (RemovesSpell / DispellMagic), and says
    // which: dispels for a DispellMagic, removes for the spells a RemovesSpell names.
    private bool ChainStripsBuffs(
        int spell, int depth, Dictionary<int, int[]> spellAbils, HashSet<int> seen,
        ref bool dispels, HashSet<int> removes)
    {
        if (depth > MaxChainDepth || spell <= 0 || !seen.Add(spell)) return false;
        if (!spellAbils.TryGetValue(spell, out int[]? abil)) return false;

        bool strips = false;
        for (int k = 0; k < SpellAbilSlots; k++)
        {
            int a = abil[k * 2];
            int v = abil[k * 2 + 1];
            if (a == AbilDispelMagic)
            {
                strips = true;
                dispels = true;
            }
            else if (a == AbilRemovesSpell)
            {
                strips = true;
                if (v > 0) removes.Add(v);
            }
            else if (a == AbilEndCast && v > 0)
                strips |= ChainStripsBuffs(v, depth + 1, spellAbils, seen, ref dispels, removes);
        }
        return strips;
    }

    private static HashSet<int> CollectRoomSpells(GameTable rooms)
    {
        HashSet<int> set = new();
        foreach (GameRow row in rooms.Rows)
        {
            if (TryReadInt(row, "Spell", out int spell) && spell > 0) set.Add(spell);
        }
        return set;
    }

    // Spell Number → flat [Abil0, Val0, Abil1, Val1, …] slot array (2 * SpellAbilSlots),
    // then MinBase and MaxBase.
    private static Dictionary<int, int[]> ReadSpellAbils(GameTable spells)
    {
        Dictionary<int, int[]> map = new();
        foreach (GameRow row in spells.Rows)
        {
            if (!TryReadInt(row, "Number", out int number) || number <= 0) continue;

            int[] slots = new int[SpellAbilSlots * 2 + 2];
            for (int k = 0; k < SpellAbilSlots; k++)
            {
                TryReadInt(row, AbilFields[k], out slots[k * 2]);
                TryReadInt(row, AbilValFields[k], out slots[k * 2 + 1]);
            }
            TryReadInt(row, "MinBase", out slots[MinBaseSlot]);
            TryReadInt(row, "MaxBase", out slots[MaxBaseSlot]);
            map[number] = slots;
        }
        return map;
    }

    private static bool TryReadInt(GameRow row, string property, out int value)
    {
        value = 0;
        return row.TryGetProperty(property, out GameValue el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }
}
