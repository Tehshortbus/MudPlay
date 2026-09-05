using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Edit;

// One monster that a monster summons: the target monster number (for the record
// link), its display name, and the parenthetical "how + chance" context (e.g.
// "between rounds, 25%"). Built by MonsterMdbInfoBuilder.BuildOutgoingSummonLabels.
internal readonly record struct OutgoingSummon(int MonsterNumber, string Name, string Context)
{
    // The plain one-line label, e.g. "silvery skull (between rounds, 30%)".
    public string Label => $"{Name} ({Context})";
}

// Builds the Monster edit dialog's right-pane "Other Info (from MDB)" — the read-only
// record assembly shown for a monster (stats, cash, spells, greet keywords, attacks,
// between-rounds, item drops, and the where-it-comes-from room/summon lists). Extracted
// from the Monsters browser tab so the same record can be opened by Number from outside
// the browser (the Navigation Room Info panel → MonsterRecordDialogService) as well as
// from a browser row (MonstersSectionViewModel.OpenEditAsync). Pure read of the active
// set's tables; the JSON field readers are small private copies (the grid keeps its own).
public sealed class MonsterMdbInfoBuilder
{
    private readonly GameDataCache _cache;
    private readonly RoomGraphManager? _roomGraph;
    private readonly TBInfoStore _tb;
    private readonly DialogService? _dialogs;

    // MajorMUD's HP-regen tick: a monster heals its HPRegen amount once every 90 seconds
    // (18 combat rounds × 5 s). (GreaterMUD's 30 s / 6 rounds would branch here off a realm
    // flag if/when that realm is supported.)
    private const int RegenIntervalSeconds = 90;

    public MonsterMdbInfoBuilder(
        GameDataCache cache, RoomGraphManager? roomGraph, TBInfoStore tb, DialogService? dialogs = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(tb);
        _cache = cache;
        _roomGraph = roomGraph;
        _tb = tb;
        _dialogs = dialogs;
    }

    // Right-pane "Other Info" for the Monster edit dialog. Row order + transforms:
    //   1. WCC No / Experience (with ExpMulti when > 1)
    //   2. Regen Time / Game Limit (suffix "(no respawn)" when RegenTime = 0)
    //   3. Type / Alignment / Undead
    //   4. HP / HP Regen — kept on separate rows per user spec.
    //   5. AC/DR combined slash, MR, Follow %, Charm LVL
    //   6. Cash — raw R/P/G/S/C breakdown
    //   7. Weapon / Create Spell / Death Spell / Greet (cross-ref names)
    //   8. BS Defense (when > 0)
    //   9. Abilities — friendly labels from AbilityNames; Abil-N = 146 (Guarded by) split into
    //      its own row with monster-name cross-ref.
    //  10. Item Drops 0..9 / Attacks 0..4 / Mid Spells 0..4 — first row in each group carries
    //      the section label; subsequent rows have a blank key so they indent visually under
    //      the header.
    // Per user spec we deliberately omit combat-sim outputs (predicted damage etc. — Workshop
    // COMBAT Preview territory) and the alignment-derived [Hostile]/[Not-Hostile] tag.
    public IReadOnlyList<MdbInfoRow> Build(string wccNoStr)
    {
        List<MdbInfoRow> kv = new();
        if (!int.TryParse(wccNoStr, out int wccNo)) return kv;

        JsonDocument? doc = _cache.GetRawTable("Monsters");
        if (doc is null) return kv;

        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("Number", out JsonElement numProp)) continue;
            if (numProp.ValueKind != JsonValueKind.Number) continue;
            if (numProp.GetInt32() != wccNo) continue;

            AddRow(kv, "WCC No", wccNoStr);

            int exp = ReadInt(el, "EXP");
            int multi = ReadInt(el, "ExpMulti");
            if (exp != 0)
            {
                string expDisplay = exp.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
                if (multi > 1) expDisplay += $" (×{multi})";
                AddRow(kv, "Experience", expDisplay);
            }

            int regenTime = ReadInt(el, "RegenTime");
            if (regenTime > 0)
                AddRow(kv, "Regen Time", $"{regenTime} hour{(regenTime == 1 ? "" : "s")}");

            int gameLimit = ReadInt(el, "GameLimit");
            if (gameLimit > 0)
                AddRow(kv, "Game Limit",
                    regenTime == 0 ? $"{gameLimit} (no respawn)" : gameLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));

            AddRowIfPresent(kv, "Type",     LookupEnums.FormatMonType(ReadString(el, "Type")));
            AddRowIfPresent(kv, "Alignment", LookupEnums.FormatMonAlignment(ReadString(el, "Align")));

            // Undead is a byte-boolean: 0 = not undead, any non-zero = undead.
            // Across 1.11p it holds 0, 1, AND 255 (the MDB's Boolean True stored
            // as -1), so the test must be != 0 — never == 1, which silently drops
            // the 255 rows (banshee, zombie cat, skeletal steed, …).
            if (ReadInt(el, "Undead") != 0) AddRow(kv, "Undead", "Yes");

            // HP + HP Regen combined into one row, in the form
            // "7200 (Regens: 2000 HPs every 90 seconds [18 rounds])".
            int hp = ReadInt(el, "HP");
            if (hp > 0)
            {
                string hpDisplay = hp.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
                int hpRegen = ReadInt(el, "HPRegen");
                if (hpRegen > 0)
                    hpDisplay += $" (Regens: {hpRegen:N0} HPs every {RegenIntervalSeconds} seconds [{RegenIntervalSeconds / 5} rounds])";
                AddRow(kv, "HP", hpDisplay);
            }

            int ac = ReadInt(el, "ArmourClass");
            int dr = ReadInt(el, "DamageResist");
            if (ac != 0 || dr != 0) AddRow(kv, "AC/DR", $"{ac}/{dr}");

            AddRowIfNonZero(kv, "MR",        ReadInt(el, "MagicRes"));
            AddRowIfNonZero(kv, "Follow %",  ReadInt(el, "Follow%"));
            AddRowIfNonZero(kv, "Charm LVL", ReadInt(el, "CharmLVL"));

            // Cash — raw coin breakdown ("2R/3P/5G/10S/2C"). Skipped
            // entirely when every coin slot is zero.
            string cash = BuildCashBreakdown(el);
            if (!string.IsNullOrEmpty(cash)) AddRow(kv, "Cash (up to)", cash);

            // Weapon — cross-ref to Items.Name
            int weaponId = ReadInt(el, "Weapon");
            if (weaponId > 0)
                AddRow(kv, "Weapon", LookupItemName(weaponId) ?? $"Item #{weaponId}");

            // Create / Death spells — cross-ref to Spells.Name with brief
            // effect descriptor when one of the primary Abil codes is set.
            int createSpell = ReadInt(el, "CreateSpell");
            if (createSpell > 0) kv.Add(SpellRefRow("Create Spell", "", createSpell, 0, ""));
            int deathSpell = ReadInt(el, "DeathSpell");
            if (deathSpell > 0) kv.Add(SpellRefRow("Death Spell", "", deathSpell, 0, ""));

            int greetTxt = ReadInt(el, "GreetTXT");
            if (greetTxt > 0)
            {
                // Decode the greet textblock into the "what can I ask, and what
                // flags fire when I do" tree, then group it by keyword: each
                // depth-0 line is a player keyword, the deeper lines beneath it
                // are the effects it triggers. The dialog surfaces the keywords as
                // chips and flies out each one's effects on click — so a block with
                // dozens of keywords stays compact on the tab. A block with no
                // player keywords (a pure dialogue greet) keeps the plain row.
                IReadOnlyList<TBInfoActionLine> decoded =
                    TBInfoActionDecoder.DecodeGreet(_tb, _cache, _roomGraph, greetTxt);
                IReadOnlyList<GreetKeyword> keywords = GroupGreetKeywords(decoded);
                if (keywords.Count > 0)
                    kv.Add(new MdbInfoRow("Greet", $"Textblock #{greetTxt}", Keywords: keywords));
                else
                    AddRow(kv, "Greet", $"Textblock #{greetTxt}");
            }

            AddRowIfNonZero(kv, "BS Defense", ReadInt(el, "BSDefense"));

            // Abilities — iterate Abil-0..9, friendly labels via
            // AbilityNames. Code 146 = "Guarded by" splits into its
            // own row with monster-name resolution. Values render
            // signed ("+5" / "-50") so resist-style abilities read
            // unambiguously.
            List<string> abilities = new();
            List<int>    guards    = new();
            for (int i = 0; i < 10; i++)
            {
                int code = ReadInt(el, $"Abil-{i}");
                if (code == 0) continue;
                int val = ReadInt(el, $"AbilVal-{i}");
                if (code == 146)
                {
                    guards.Add(val);
                    continue;
                }
                // Code 1 ("Damage") is a spell/item effect code with no defined
                // effect on a monster — MMUD-Explorer treats it as a non-stat
                // ability, and it appears on exactly one monster in the stock
                // data with value 0. Inert noise; don't surface it.
                if (code == 1) continue;
                string label = AbilityNames.GetName(code) ?? $"Ability {code}";
                abilities.Add(val == 0 ? label : $"{label} {FormatSigned(val)}");
            }
            if (abilities.Count > 0)
                AddRow(kv, "Abilities", string.Join("\n", abilities));
            if (guards.Count > 0)
            {
                List<string> guardNames = new();
                foreach (int g in guards)
                    guardNames.Add(LookupMonsterName(g) ?? $"Monster #{g}");
                AddRow(kv, "Guarded by", string.Join(", ", guardNames));
            }

            // Item Drops — one clickable chip per non-zero DropItem-N slot,
            // each jumping to the item's Items-tab record. Chip label is
            // "name (pct%)" (pct omitted when the slot has no drop chance).
            List<(int Id, string Text)> drops = new();
            for (int i = 0; i < 10; i++)
            {
                int itemId = ReadInt(el, $"DropItem-{i}");
                if (itemId == 0) continue;
                int pct = ReadInt(el, $"DropItem%-{i}");
                string itemName = LookupItemName(itemId) ?? $"Item #{itemId}";
                drops.Add((itemId, pct > 0 ? $"{itemName} ({pct}%)" : itemName));
            }
            AddItemList(kv, "Item Drops", drops);

            // ----- Mob's Attacks (each AttType-N in 1..3 with Att%>0 gets its own
            // header + sub-rows). AttType 1 (normal) / 3 (rob) → Min-Max + Accuracy.
            // AttType 2 (spell) → AttAcc holds the spell id, AttMax the cast level,
            // AttMin the success %. Per-attack percent uses AttTrue% when present. -----
            int monsterEnergy = ReadInt(el, "Energy");
            bool hasAttacks = false;
            for (int i = 0; i < 5; i++)
            {
                int at = ReadInt(el, $"AttType-{i}");
                if (at >= 1 && at <= 3 && ReadInt(el, $"Att%-{i}") > 0) { hasAttacks = true; break; }
                if (ReadInt(el, $"MidSpell-{i}") > 0)                  { hasAttacks = true; break; }
            }
            if (hasAttacks)
            {
                // Monster energy is the per-round budget the mob spends on
                // attacks/spells. Stock MajorMUD is always 1000; paradigm / future
                // realms may differ.
                AddRow(kv, "Mob's Attacks",
                    monsterEnergy > 0
                        ? $"{monsterEnergy} energy/round"
                        : string.Empty);

                for (int i = 0; i < 5; i++)
                {
                    int attType = ReadInt(el, $"AttType-{i}");
                    int attPct  = ReadInt(el, $"Att%-{i}");
                    if (attType < 1 || attType > 3 || attPct <= 0) continue;

                    string attName = ReadString(el, $"AttName-{i}").Trim();
                    int attEnergy  = ReadInt(el, $"AttEnergy-{i}");
                    int hitSpell   = ReadInt(el, $"AttHitSpell-{i}");
                    int trueRound  = (int)Math.Round(ReadDouble(el, $"AttTrue%-{i}"));
                    int displayPct = trueRound > 0 ? trueRound : attPct;

                    string header = string.IsNullOrEmpty(attName)
                        ? $"({displayPct}%) Attack {i + 1}"
                        : $"({displayPct}%) {attName}";

                    // The attack name is often a full sentence ("claws you with its
                    // plague-ridden claws") — far wider than the key column. Emit it as
                    // a full-width header row so it wraps in full, then indent its stats
                    // beneath it as empty-key value rows.
                    kv.Add(new MdbInfoRow(header, string.Empty, FullWidth: true));

                    if (attType == 1 || attType == 3)
                    {
                        int min = ReadInt(el, $"AttMin-{i}");
                        int max = ReadInt(el, $"AttMax-{i}");
                        int acc = ReadInt(el, $"AttAcc-{i}");
                        AddRow(kv, string.Empty, $"Min-Max: {min}-{max}");
                        AddRow(kv, string.Empty, $"Accuracy: {acc}");
                        AddRow(kv, string.Empty, FormatEnergyRow(attEnergy, monsterEnergy));
                        if (hitSpell > 0)
                            kv.Add(SpellRefRow(string.Empty, "Hit Spell: ", hitSpell, 0, ""));
                    }
                    else // attType == 2 (spell-attack)
                    {
                        int spellId   = ReadInt(el, $"AttAcc-{i}");
                        int spellLvl  = ReadInt(el, $"AttMax-{i}");
                        int successPc = ReadInt(el, $"AttMin-{i}");
                        kv.Add(SpellRefRow(string.Empty, "Spell: ", spellId, spellLvl,
                            spellLvl > 0 ? $" lvl {spellLvl}" : ""));
                        AddRow(kv, string.Empty, $"Success %: {successPc}");
                        AddRow(kv, string.Empty, FormatEnergyRow(attEnergy, monsterEnergy));
                        if (hitSpell > 0)
                            kv.Add(SpellRefRow(string.Empty, "Hit Spell: ", hitSpell, 0, ""));
                    }
                }
            }

            // ----- Between Rounds (formerly Mid Spells) -----
            // MidSpell% is stored as a cumulative threshold across the 5
            // slots (slot 0's value is its raw chance; slot N's is the
            // running sum). The display shows the DELTA so each row reads
            // as the actual chance for that spell to fire.
            int cumulative = 0;
            for (int i = 0, shown = 0; i < 5; i++)
            {
                int spellId = ReadInt(el, $"MidSpell-{i}");
                if (spellId == 0) continue;
                int threshold = ReadInt(el, $"MidSpell%-{i}");
                int delta = threshold - cumulative;
                cumulative = threshold;
                int lvl = ReadInt(el, $"MidSpellLVL-{i}");
                string spellName = ResolveSpellWithEffect(spellId, lvl);
                string numTag = $" [#{spellId}]";                        // the Spells-table Number
                string tail   = lvl > 0 ? $", lvl {lvl}]" : "]";
                string row = $"({delta}%) [{spellName}{numTag}{tail}";
                // The spell name links to its Spell record; the chance, spell number, and
                // level stay plain text around it. spellId is body-scoped so the lambda
                // captures this slot's value.
                var inlines = new List<MdbInline>
                {
                    new($"({delta}%) ["),
                    new(spellName, new AsyncRelayCommand(() => AppServices.Current.OpenSpellRecordAsync(spellId))),
                    new(numTag + tail),
                };
                kv.Add(new MdbInfoRow(shown == 0 ? "Between Rounds" : string.Empty, row, Inlines: inlines));
                shown++;
            }

            // ----- Summons (what THIS monster spawns) -----
            List<OutgoingSummon> produces = FindOutgoingSummons(el);
            for (int j = 0; j < produces.Count; j++)
            {
                OutgoingSummon s = produces[j];
                // The summoned monster's name links to its Monster record; the
                // "how + chance" context stays plain text after it.
                int monNum = s.MonsterNumber;
                var inlines = new List<MdbInline>
                {
                    new(s.Name, new AsyncRelayCommand(() => AppServices.Current.OpenMonsterRecordAsync(monNum))),
                    new($" ({s.Context})"),
                };
                kv.Add(new MdbInfoRow(j == 0 ? "Summons" : string.Empty, s.Label, Inlines: inlines));
            }

            // ----- Spawns In (lair rooms) -----
            AddRoomList(kv, "Spawns In", FindSpawnRooms(wccNo),
                k => $"{k.Map}/{k.Room} (lair: {SpawnLairMax(k)})");

            // ----- Placed In (fixed NPC room) -----
            AddRoomList(kv, "Placed In", FindPlacedRooms(wccNo));

            // ----- Summoned (spell sources) -----
            // Monsters that aren't laired OR placed are often conjured by a summon
            // spell (Abil 12). Resolve who casts that spell: a ROOM via its room-spell
            // (a location) and/or a MONSTER in combat / on death / on spawn.
            List<(int Number, string Name)> summonSpells = FindSummonSpells(wccNo);
            if (summonSpells.Count > 0)
            {
                HashSet<int> spellIds = summonSpells.Select(s => s.Number).ToHashSet();

                // Room-spell summons (room casts on entry) — a location.
                AddRoomList(kv, "Summoned In", FindRoomSpellRooms(spellIds));

                // Monster casters with their context tag(s). Falls back to the bare
                // spell name when no room or monster caster resolves.
                List<string> mobs = FindSummoningMonsters(spellIds);
                string by = mobs.Count > 0
                    ? string.Join(", ", mobs)
                    : string.Join(", ", summonSpells.Select(s => $"{s.Name} (spell)"));
                AddRow(kv, "Summoned By", by);
            }

            break;
        }
        return kv;
    }

    // Rooms whose lair names wccNo, sorted by map then room. Reads the in-memory
    // RoomGraphManager (already parsed for the active set). Empty when the graph isn't
    // wired or the monster lairs nowhere.
    private IReadOnlyList<RoomKey> FindSpawnRooms(int wccNo)
    {
        if (_roomGraph is null) return Array.Empty<RoomKey>();
        List<RoomKey> rooms = new();
        foreach (Room room in _roomGraph.Rooms)
        {
            if (LairNamesMonster(room.RawLairTag, wccNo))
                rooms.Add(room.Key);
        }
        rooms.Sort(static (a, b) => a.Map != b.Map ? a.Map.CompareTo(b.Map) : a.Room.CompareTo(b.Room));
        return rooms;
    }

    // Rooms that "place" wccNo via their NPC field (fixed-spawn home room), sorted by map
    // then room. Empty when the graph isn't wired or the monster is laired-only / unplaced.
    private IReadOnlyList<RoomKey> FindPlacedRooms(int wccNo)
    {
        if (_roomGraph is null) return Array.Empty<RoomKey>();
        List<RoomKey> rooms = new();
        foreach (Room room in _roomGraph.Rooms)
        {
            if (room.Npc == wccNo)
                rooms.Add(room.Key);
        }
        rooms.Sort(static (a, b) => a.Map != b.Map ? a.Map.CompareTo(b.Map) : a.Room.CompareTo(b.Room));
        return rooms;
    }

    // Spells (number + name) that summon wccNo (best-effort — see SpellSummons). Covers
    // monsters that aren't laired or placed but are conjured by a caster.
    private List<(int Number, string Name)> FindSummonSpells(int wccNo)
    {
        List<(int, string)> spells = new();
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return spells;
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (!SpellSummons(el, wccNo)) continue;
            int num = ReadInt(el, "Number");
            string name = ReadString(el, "Name").Trim();
            spells.Add((num, string.IsNullOrEmpty(name) ? $"Spell #{num}" : name));
        }
        return spells;
    }

    // Rooms whose room-spell (Room.Spell, cast when a player enters) is one of spellIds — i.e.
    // rooms that summon the monster on entry. Sorted by map then room.
    private IReadOnlyList<RoomKey> FindRoomSpellRooms(IReadOnlySet<int> spellIds)
    {
        if (_roomGraph is null) return Array.Empty<RoomKey>();
        List<RoomKey> rooms = new();
        foreach (Room room in _roomGraph.Rooms)
        {
            if (room.Spell != 0 && spellIds.Contains(room.Spell))
                rooms.Add(room.Key);
        }
        rooms.Sort(static (a, b) => a.Map != b.Map ? a.Map.CompareTo(b.Map) : a.Room.CompareTo(b.Room));
        return rooms;
    }

    // Monsters that cast one of spellIds, each tagged with how (combat / death / on spawn) —
    // e.g. "Argak the Grey (death)". Deduped by label.
    private List<string> FindSummoningMonsters(IReadOnlySet<int> spellIds)
    {
        List<string> casters = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        JsonDocument? doc = _cache.GetRawTable("Monsters");
        if (doc is null) return casters;
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            string? ctx = SummonContext(el, spellIds);
            if (ctx is null) continue;
            int num = ReadInt(el, "Number");
            string name = ReadString(el, "Name").Trim();
            string label = $"{(string.IsNullOrEmpty(name) ? $"Monster #{num}" : name)} ({ctx})";
            if (seen.Add(label)) casters.Add(label);
        }
        return casters;
    }

    // Monsters this monster summons, each as "<name> (<how>[, <chance>])" — the reverse of
    // FindSummoningMonsters. Walks the viewed monster's own spell slots (create / death /
    // spell-attack / hit-spell / between-rounds), resolves each summon spell to its target
    // monster, and tags how + the % chance where the cast carries one.
    private List<OutgoingSummon> FindOutgoingSummons(JsonElement monster)
    {
        JsonDocument? spellsDoc = _cache.GetRawTable("Spells");
        if (spellsDoc is null) return new();

        // Index spells by number so each cast slot is an O(1) resolve.
        Dictionary<int, JsonElement> spellByNum = new();
        foreach (JsonElement s in spellsDoc.RootElement.EnumerateArray())
        {
            int num = ReadInt(s, "Number");
            if (num > 0) spellByNum[num] = s;
        }

        return BuildOutgoingSummonLabels(
            monster,
            spellId => spellByNum.TryGetValue(spellId, out JsonElement s) ? SummonTargets(s) : Array.Empty<int>(),
            target => LookupMonsterName(target) ?? $"Monster #{target}");
    }

    // Pure label-builder behind FindOutgoingSummons — extracted so the context + chance logic
    // is testable without a loaded cache. summonTargetsOf maps a spell id to the monster(s) it
    // summons (empty for non-summon spells); nameOf maps a monster number to its display name.
    internal static List<OutgoingSummon> BuildOutgoingSummonLabels(
        JsonElement monster,
        Func<int, IReadOnlyList<int>> summonTargetsOf,
        Func<int, string> nameOf)
    {
        List<OutgoingSummon> result = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        void Emit(int spellId, string context, string? chance)
        {
            if (spellId <= 0) return;
            foreach (int target in summonTargetsOf(spellId))
            {
                string paren = chance is null ? context : $"{context}, {chance}";
                OutgoingSummon entry = new(target, nameOf(target), paren);
                if (seen.Add(entry.Label)) result.Add(entry);
            }
        }

        // Certain casts — no chance figure.
        Emit(ReadInt(monster, "CreateSpell"), "on spawn", null);
        Emit(ReadInt(monster, "DeathSpell"),  "on death", null);

        // Combat — spell-attacks (carry an Att% chance) and per-hit spells.
        for (int i = 0; i < 5; i++)
        {
            if (ReadInt(monster, $"AttType-{i}") == 2)
            {
                int truePct = (int)Math.Round(ReadDouble(monster, $"AttTrue%-{i}"));
                int pct = truePct > 0 ? truePct : ReadInt(monster, $"Att%-{i}");
                Emit(ReadInt(monster, $"AttAcc-{i}"), "combat", pct > 0 ? $"{pct}%" : null);
            }
            Emit(ReadInt(monster, $"AttHitSpell-{i}"), "combat, on hit", null);
        }

        // Between-rounds — MidSpell% is a cumulative threshold across the
        // slots, so the per-spell chance is the delta (mirrors the
        // "Between Rounds" rendering above).
        int cumulative = 0;
        for (int i = 0; i < 5; i++)
        {
            int spellId = ReadInt(monster, $"MidSpell-{i}");
            if (spellId == 0) continue;
            int threshold = ReadInt(monster, $"MidSpell%-{i}");
            int delta = threshold - cumulative;
            cumulative = threshold;
            Emit(spellId, "between rounds", delta > 0 ? $"{delta}%" : null);
        }

        return result;
    }

    // How monster casts a spell in spellIds, or null if it doesn't. Combat = a spell-attack
    // (AttType 2 whose AttAcc is the spell), a per-hit spell (AttHitSpell), or a between-rounds
    // spell (MidSpell); plus the DeathSpell ("death") and CreateSpell ("on spawn"). A monster
    // can carry several.
    internal static string? SummonContext(JsonElement monster, IReadOnlySet<int> spellIds)
    {
        bool combat = false;
        for (int i = 0; i < 5; i++)
        {
            if (ReadInt(monster, $"AttType-{i}") == 2 && spellIds.Contains(ReadInt(monster, $"AttAcc-{i}")))
                combat = true;
            if (spellIds.Contains(ReadInt(monster, $"AttHitSpell-{i}"))) combat = true;
            if (spellIds.Contains(ReadInt(monster, $"MidSpell-{i}"))) combat = true;
        }
        bool death = spellIds.Contains(ReadInt(monster, "DeathSpell"));
        bool spawn = spellIds.Contains(ReadInt(monster, "CreateSpell"));
        if (!combat && !death && !spawn) return null;

        List<string> parts = new(3);
        if (combat) parts.Add("combat");
        if (death) parts.Add("death");
        if (spawn) parts.Add("on spawn");
        return string.Join(", ", parts);
    }

    // True when spell summons monster monsterNo. Summon spells carry ability code 12; the
    // summoned monster number is inconsistently encoded, so this is best-effort (see
    // SummonTargets).
    internal static bool SpellSummons(JsonElement spell, int monsterNo)
        => monsterNo > 0 && SummonTargets(spell).Contains(monsterNo);

    // Monster number(s) spell summons, or empty when it isn't a summon spell. Encoding is
    // inconsistent: the summon abilities' own positive values win, and only when none name a
    // target does the spell's MinBase stand in (e.g. "raptor summon" → 509 = tetraraptor).
    internal static IReadOnlyList<int> SummonTargets(JsonElement spell)
    {
        List<int> targets = new();
        bool isSummon = false;
        for (int i = 0; i < 10; i++)
        {
            if (ReadInt(spell, $"Abil-{i}") != 12) continue;   // 12 = summon
            isSummon = true;
            int target = ReadInt(spell, $"AbilVal-{i}");
            if (target > 0 && !targets.Contains(target)) targets.Add(target);
        }
        if (!isSummon) return Array.Empty<int>();
        if (targets.Count == 0)
        {
            int minBase = ReadInt(spell, "MinBase");
            if (minBase > 0) targets.Add(minBase);
        }
        return targets;
    }

    // Append a "label (N)" row listing every room as a clickable map/room chip that
    // opens the Navigation map on that room and selects it. Not truncated — the pane
    // scrolls. Falls back to a plain comma-joined string in design-time (no live app
    // services). No-op when rooms is empty.
    private void AddRoomList(List<MdbInfoRow> kv, string label, IReadOnlyList<RoomKey> rooms,
        Func<RoomKey, string>? labelFor = null)
    {
        if (rooms.Count == 0) return;
        labelFor ??= static k => $"{k.Map}/{k.Room}";
        string list = string.Join(", ", rooms.Select(labelFor));
        IReadOnlyList<RoomLink>? links = _dialogs is not null
            ? rooms.Select(k => new RoomLink(labelFor(k), k)).ToList()
            : null;
        kv.Add(new MdbInfoRow($"{label} ({rooms.Count})", list, Rooms: links));
    }

    // Per-room lair cap — the "(Max N)" from a room's lair tag. 0 when the room / tag can't
    // be read (shouldn't happen for a room FindSpawnRooms already matched by its lair tag).
    private int SpawnLairMax(RoomKey key)
        => _roomGraph?.GetRoom(key) is { } room
           && RoomTooltipBuilder.TryParseLairMax(room.RawLairTag, out int max)
            ? max : 0;

    // Append an "Item Drops (N)" row listing every dropped item as a clickable chip that jumps
    // the browser's Items tab to that item. No-op when the monster drops nothing.
    private static void AddItemList(List<MdbInfoRow> kv, string label, IReadOnlyList<(int Id, string Text)> items)
    {
        if (items.Count == 0) return;
        string list = string.Join(", ", items.Select(i => i.Text));
        IReadOnlyList<ItemLink> links = items.Select(i => new ItemLink(i.Text, i.Id)).ToList();
        kv.Add(new MdbInfoRow($"{label} ({items.Count})", list, Items: links));
    }

    // Group the flat, depth-tagged greet decode into per-keyword blocks: a depth-0 line opens
    // a new keyword; the deeper lines that follow (re-indented relative to it) are the effects
    // that fire when it's asked. Empty when the block has no player keywords.
    private static IReadOnlyList<GreetKeyword> GroupGreetKeywords(IReadOnlyList<TBInfoActionLine> lines)
    {
        List<GreetKeyword> keywords = new();
        string? keyword = null;
        List<string> effects = new();
        foreach (TBInfoActionLine line in lines)
        {
            if (line.Depth == 0)
            {
                if (keyword is not null)
                    keywords.Add(new GreetKeyword(keyword, effects));
                keyword = line.Text;
                effects = new List<string>();
            }
            else if (keyword is not null)
            {
                effects.Add(new string(' ', (line.Depth - 1) * 2) + line.Text);
            }
        }
        if (keyword is not null)
            keywords.Add(new GreetKeyword(keyword, effects));
        return keywords;
    }

    // True when a Room.RawLairTag lists wccNo as one of its spawn monsters. v1.11p tags are
    // "(Max N): id,id,…,[group-index]" — the monster ids precede the bracketed group key; the
    // [..] suffix is parsed off so its digits aren't mistaken for monster ids.
    internal static bool LairNamesMonster(string? rawLairTag, int wccNo)
    {
        if (string.IsNullOrEmpty(rawLairTag)) return false;
        int bracket = rawLairTag.IndexOf('[');
        string head = bracket >= 0 ? rawLairTag[..bracket] : rawLairTag;
        int colon = head.IndexOf(':');
        if (colon < 0) return false;
        foreach (string token in head[(colon + 1)..]
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, out int id) && id == wccNo) return true;
        }
        return false;
    }

    // ----- Field readers + row helpers (private copies; the grid keeps its own) -----

    private static int ReadInt(JsonElement el, string field)
    {
        if (!el.TryGetProperty(field, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }

    private static double ReadDouble(JsonElement el, string field)
    {
        if (!el.TryGetProperty(field, out JsonElement v)) return 0d;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : 0d;
    }

    // "+5" / "-50" / "0" — used by signed-value ability rows.
    private static string FormatSigned(int n) => n > 0
        ? "+" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // "Energy: 250 (Max 4x/round)" — divides monster total by per-attack cost.
    private static string FormatEnergyRow(int attEnergy, int monsterEnergy)
    {
        if (attEnergy <= 0) return $"Energy: {attEnergy}";
        if (monsterEnergy <= 0) return $"Energy: {attEnergy}";
        return $"Energy: {attEnergy} (Max {monsterEnergy / attEnergy}x/round)";
    }

    private static string ReadString(JsonElement el, string field)
    {
        if (!el.TryGetProperty(field, out JsonElement v)) return string.Empty;
        return v.ValueKind switch
        {
            JsonValueKind.Null      => string.Empty,
            JsonValueKind.Undefined => string.Empty,
            JsonValueKind.String    => v.GetString() ?? string.Empty,
            JsonValueKind.Number    => v.ToString(),
            _                        => v.ToString(),
        };
    }

    private static void AddRow(List<MdbInfoRow> kv, string label, string value)
        => kv.Add(new MdbInfoRow(label, value));

    private static void AddRowIfPresent(List<MdbInfoRow> kv, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value)) AddRow(kv, label, value);
    }

    private static void AddRowIfNonZero(List<MdbInfoRow> kv, string label, int value)
    {
        if (value != 0) AddRow(kv, label, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // "2R/3P/5G/10S/2C" — only non-zero coins, slash-separated.
    private static string BuildCashBreakdown(JsonElement el)
    {
        List<string> parts = new();
        void Maybe(string field, string letter)
        {
            int v = ReadInt(el, field);
            if (v > 0) parts.Add(v.ToString(System.Globalization.CultureInfo.InvariantCulture) + letter);
        }
        Maybe("R", "R");
        Maybe("P", "P");
        Maybe("G", "G");
        Maybe("S", "S");
        Maybe("C", "C");
        return string.Join("/", parts);
    }

    // ----- Cross-reference helpers (Items / Spells / Monsters) -----
    // Thin shims over GameDataCache.FindNameByNumber: skip non-positive ids (0 = "no link"
    // sentinel) and treat empty/missing Name as null so callers get one boolean check.

    private string? LookupItemName(int itemId)    => LookupName("Items",    itemId);
    private string? LookupSpellName(int spellId)  => LookupName("Spells",   spellId);
    private string? LookupMonsterName(int monNum) => LookupName("Monsters", monNum);

    private string? LookupName(string table, int number)
    {
        if (number <= 0) return null;
        string? name = _cache.FindNameByNumber(table, number);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    // "{spell name} (effect)" — spell name with a brief effect descriptor when the spell's
    // primary Abil-N entries surface a recognisable effect. Falls back to just the spell name.
    // castLevel scales the damage range; pass 0 for a monster's raw hit / death / create spell.
    private string ResolveSpellWithEffect(int spellId, int castLevel = 0)
    {
        string name = LookupSpellName(spellId) ?? $"Spell #{spellId}";
        string effect = ResolveSpellEffect(spellId, castLevel);
        return string.IsNullOrEmpty(effect) ? name : $"{name} ({effect})";
    }

    // A spell cross-reference row whose spell NAME links to its Spell record — the same
    // clickable MdbInline treatment the Between Rounds / Summons rows use, so every spell
    // a monster references (spell-attack, hit / create / death spell) opens its record on
    // click, with the optional plain-text prefix/suffix ("Spell: " / " lvl N") around it.
    // Falls back to a plain row when the id doesn't resolve to a spell.
    private MdbInfoRow SpellRefRow(string key, string prefix, int spellId, int castLevel, string suffix)
    {
        string spell = ResolveSpellWithEffect(spellId, castLevel);
        string num = spellId > 0 ? $" [#{spellId}]" : string.Empty;   // the Spells-table Number
        string plain = $"{prefix}{spell}{num}{suffix}";
        if (spellId <= 0) return new MdbInfoRow(key, plain);
        int id = spellId;   // capture this slot's id for the click lambda
        List<MdbInline> inlines = new();
        if (prefix.Length > 0) inlines.Add(new MdbInline(prefix));
        // The name (+ effect) is the clickable link; the [#N] spell number is plain
        // identifying text after it.
        inlines.Add(new MdbInline(spell, new AsyncRelayCommand(() => AppServices.Current.OpenSpellRecordAsync(id))));
        inlines.Add(new MdbInline(num));
        if (suffix.Length > 0) inlines.Add(new MdbInline(suffix));
        return new MdbInfoRow(key, plain, Inlines: inlines);
    }

    // Brief comma-joined effect descriptor from a spell's primary Abil-N codes.
    private string ResolveSpellEffect(int spellId, int castLevel)
    {
        if (spellId <= 0) return string.Empty;
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return string.Empty;

        JsonElement? found = null;
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("Number", out JsonElement n)) continue;
            if (n.ValueKind != JsonValueKind.Number) continue;
            if (n.GetInt32() == spellId) { found = el; break; }
        }
        if (found is null) return string.Empty;
        JsonElement s = found.Value;

        // Min/Max with optional level scaling.
        int minBase    = ReadInt(s, "MinBase");
        int maxBase    = ReadInt(s, "MaxBase");
        int minInc     = ReadInt(s, "MinInc");
        int maxInc     = ReadInt(s, "MaxInc");
        int minIncLvls = ReadInt(s, "MinIncLVLs");
        int maxIncLvls = ReadInt(s, "MaxIncLVLs");
        int cap        = ReadInt(s, "Cap");

        int min = minBase;
        int max = maxBase;
        if (castLevel > 0)
        {
            if (minIncLvls > 0) min += (minInc / minIncLvls) * castLevel;
            if (maxIncLvls > 0) max += (maxInc / maxIncLvls) * castLevel;
            if (cap > 0)
            {
                if (min > cap) min = cap;
                if (max > cap) max = cap;
            }
        }

        // Pick out the primary effect codes. Full ability-chain recursion is out of scope
        // here — the Spells tab digs into the full chain; the Monster dialog only surfaces
        // the headline effect so the row reads at a glance.
        List<string> effects = new();
        for (int i = 0; i < 10; i++)
        {
            int code = ReadInt(s, $"Abil-{i}");
            if (code == 0) continue;
            string? desc = code switch
            {
                 1 => FormatRange("dmg",   min, max),  // Damage
                17 => FormatRange("dmg",   min, max),  // Damage(-MR)
                18 => FormatRange("heal",  min, max),  // Heal
                 8 => FormatRange("drain", min, max),  // DrainLife
                19 => "poison",
                60 => "fear",
                71 => "confusion",
                95 => "slay",
                53 => "blindness",
                12 => "summon",
                _  => null,
            };
            if (!string.IsNullOrEmpty(desc) && !effects.Contains(desc)) effects.Add(desc);
        }
        return string.Join(", ", effects);
    }

    // "dmg 10-30" / "dmg 10" / "" when both ends are zero.
    private static string FormatRange(string label, int min, int max)
    {
        if (min == 0 && max == 0) return string.Empty;
        if (min == max) return $"{label} {min}";
        return $"{label} {min}-{max}";
    }
}
