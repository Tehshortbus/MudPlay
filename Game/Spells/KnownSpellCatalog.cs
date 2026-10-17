using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using MudPlay.Game.Calculators;
using MudPlay.Game.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Spells;

// Builds the per-class learnable-spell list from the active game-data set's
// Spells + Classes tables. The eligibility filter applies MajorMUD's own
// spell-learnability rules, so the Spell Book shows exactly the spells the game
// would let the character know.
//
// Class identity flows in as the stat-reported class name; we resolve it to the
// Classes.Number (and its MageryType / MageryLVL) before filtering.
//
// Two realm-global flags default to false here: Kai-autolearn disable (Kai
// classes auto-learn unless the realm disables it) and GreaterMUD mode. The
// data-set version is assumed >= 1.7 (modern MajorMUD / MegaMUD data), so the
// explicit-class-token check always applies.
public sealed class KnownSpellCatalog
{
    // Magery enum: None=0, Mage=1, Priest=2, Druid=3, Bard=4, Kai=5. Class
    // MageryType values are the same enum, so they compare directly against
    // Spells.Magery.
    private const int MageryNone = 0;
    private const int MageryKai = 5;

    private readonly GameDataCache _cache;

    // Realm-global learnability flags, both false (a stock realm): nothing reads
    // them from game data. Non-const so the branches that read them don't trip
    // unreachable-code analysis.
    private readonly bool _disableKaiAutolearn = false;
    private readonly bool _greaterMud = false;

    public KnownSpellCatalog(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    // Resolve a stat-reported class name (e.g. "Mystic") to its
    // Classes.Number. Case-insensitive, trimmed. Returns null when the active
    // set has no class by that name.
    public int? ResolveClassNumber(string className)
    {
        if (string.IsNullOrWhiteSpace(className)) return null;
        GameTable? doc = _cache.GetTable("Classes");
        if (doc is null) return null;

        string target = className.Trim();
        foreach (GameRow row in doc.Rows)
        {
            string? name = ReadString(row, "Name");
            if (name is not null && string.Equals(name.Trim(), target, StringComparison.OrdinalIgnoreCase))
                return ReadInt(row, "Number");
        }
        return null;
    }

    // Returns the class's magery type (the Magery enum value) and emits its
    // magery level. A class number with no matching row resolves to None / 0.
    public int ResolveClassMagery(int classNumber, out int mageryLvl)
    {
        mageryLvl = 0;
        if (classNumber < 1) return MageryNone;
        GameRow? row = _cache.FindRowByNumber("Classes", classNumber);
        if (row is null) return MageryNone;

        mageryLvl = ReadInt(row.Value, "MageryLVL");
        return ReadInt(row.Value, "MageryType");
    }

    // Whether a class can cast ANY spell, by class name — true for a magery class,
    // false for the no-magery classes (Warrior / Witchunter / Ninja / Thief), and
    // NULL when the name isn't a class in the active set.
    //
    // The null case matters as much as the other two: callers use this to rule a
    // spell out, and an unrecognized name means "we don't know", which must never be
    // read as "can't cast". Distinct from ResolveClassMagery, which collapses unknown
    // and no-magery into the same 0.
    public bool? ClassCanCast(string? className)
    {
        if (string.IsNullOrWhiteSpace(className)) return null;
        if (ResolveClassNumber(className) is not { } number) return null;
        return ResolveClassMagery(number, out _) != MageryNone;
    }

    // The level at which a class first learns a room attack: a spell aimed at
    // everything in the room (Targets 9 / 11 / 12) that costs combat energy, so it
    // repeats every round and hits players outside the caster's party. 0 when the
    // class has none, and NULL when the name isn't a class in the active set — an
    // unknown class must read as "we don't know", never as "can't".
    public int? RoomAttackFromLevel(string? className)
    {
        if (string.IsNullOrWhiteSpace(className)) return null;
        if (ResolveClassNumber(className) is not { } number) return null;

        int lowest = 0;
        foreach (KnownSpell spell in Query(number, level: 0))
        {
            if (spell.Targets is not (9 or 11 or 12) || spell.Formula.EnergyCost <= 0) continue;
            int from = Math.Max(1, spell.ReqLevel);
            if (lowest == 0 || from < lowest) lowest = from;
        }
        return lowest;
    }

    // Query/IsUsable's charAlign encoding: 0 = unknown (skip alignment
    // filtering entirely — see IsUsable), 1 = Good, 2 = Neutral, 3 = Evil, 4 = evil
    // but short of Outlaw (Paradigm's Seedy), which counts as Evil except that an
    // evil-only spell needs Outlaw at least. Not a straight cast of AlignmentBucket
    // (Good=0/Neutral=1/Evil=2 there, since 0 there is a real band, not "unknown") —
    // this is the adapter between the live AlignmentBucket?
    // (ItemEquipFilter.GearBucketForWord) plus evil points, and this catalog's own
    // int scheme. Every spell's evil-only value is 0, so only the Outlaw floor matters.
    public static int CharAlignFor(AlignmentBucket? bucket, EvilPointRange? evilPoints = null) => bucket switch
    {
        AlignmentBucket.Good => 1,
        AlignmentBucket.Neutral => 2,
        AlignmentBucket.Evil => evilPoints?.MeetsEvilOnly(0) == false ? 4 : 3,
        _ => 0,
    };

    // Every spell classNumber can learn, gated to level (pass 0 for the full
    // list ignoring the level requirement). Sorted by ReqLevel then Name. Empty
    // for classes with no magery (Warriors, Thieves, etc.).
    public IReadOnlyList<KnownSpell> Query(int classNumber, int level, int charAlign = 0)
    {
        List<KnownSpell> results = new();
        GameTable? doc = _cache.GetTable("Spells");
        if (doc is null) return results;

        int classMagery = ResolveClassMagery(classNumber, out int classMageryLvl);
        foreach (GameRow row in doc.Rows)
        {
            if (!IsUsable(row, classNumber, classMagery, classMageryLvl, level, charAlign, andLearnable: true))
                continue;
            results.Add(ToKnownSpell(row));
        }

        results.Sort(CompareByReqLevelThenName);
        return results;
    }

    // Query's own ordering — ReqLevel then Name — extracted so SpellbookState can
    // re-sort after unioning an alignment-excluded, already-obtained spell back in
    // (RebuildAvailable) without duplicating the comparator.
    public static int CompareByReqLevelThenName(KnownSpell a, KnownSpell b)
    {
        int byLevel = a.ReqLevel.CompareTo(b.ReqLevel);
        return byLevel != 0 ? byLevel : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    // Every code-145 mana-regen ROLL spell in the active set (nature tap / mana
    // flux) — a 145 slot whose stored value is 0, so its magnitude is rolled from
    // the level-scaled Min/Max range each cast. Not class- or level-gated: the
    // mana-regen planner lists them by their own Magery so a player can model the
    // roll for a build they don't hold yet. Sorted by Magery then Name.
    public IReadOnlyList<KnownSpell> RollSpells()
    {
        List<KnownSpell> results = new();
        GameTable? doc = _cache.GetTable("Spells");
        if (doc is null) return results;

        foreach (GameRow row in doc.Rows)
        {
            KnownSpell spell = ToKnownSpell(row);
            if (RegenSpellClassifier.IsManaRegenRoll(spell.Formula))
                results.Add(spell);
        }

        results.Sort(static (a, b) =>
        {
            int byMagery = a.Magery.CompareTo(b.Magery);
            return byMagery != 0 ? byMagery : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return results;
    }

    // Resolve a Spells.Number to its full Name across the entire Spells table
    // (no class / magery / learnable filter). Used to render the RemovesSpell
    // (Abil 122) target by name, which can reference any spell, not just the
    // active class's learnable list. Returns null when no row has that number.
    public string? GetSpellNameByNumber(int spellNumber)
    {
        if (spellNumber < 1) return null;
        GameRow? row = _cache.FindRowByNumber("Spells", spellNumber);
        return row is null ? null : ReadString(row.Value, "Name");
    }

    // Resolve a Spells.Number to its raw Targets scope code across the entire Spells
    // table (no class / magery / learnable filter). Lets a cast-on-use item be
    // classified by the spell it casts — a whole-party item buff (Targets 10 / 13)
    // vs a single-target one — without the cast spell needing to be class-learnable.
    // Returns null when no row has that number.
    public int? GetTargetsByNumber(int spellNumber)
    {
        if (spellNumber < 1) return null;
        GameRow? row = _cache.FindRowByNumber("Spells", spellNumber);
        return row is null ? null : ReadInt(row.Value, "Targets");
    }

    // Resolve a Spells.Number to its SpellFormulaInput across the entire Spells
    // table (no class / magery / learnable filter), for rendering any spell's effect
    // without a class spell book. Returns null when no row has that number.
    public SpellFormulaInput? GetFormulaByNumber(int spellNumber)
    {
        if (spellNumber < 1) return null;
        GameRow? row = _cache.FindRowByNumber("Spells", spellNumber);
        return row is null ? null : ToFormula(row.Value);
    }

    // Build the reverse index from a TBInfo textblock number to every spell
    // that textblock casts, parsed from each spell row's denormalised
    // "Casted By" column (e.g. "Textblock #2910, Textblock #2911"). The Spell
    // Book uses it to expand an Abil-148 (TextBlock) reference into the real
    // effect(s) the textblock applies — duration + stat bonuses for transform
    // spells like "form of the dragon" — instead of an opaque record number.
    // General across data sets: any spell whose "Casted By" names a textblock
    // is linked, with no per-spell special-casing. Empty when the active set
    // has no Spells table.
    public IReadOnlyDictionary<int, IReadOnlyList<KnownSpell>> BuildCastByTextblockIndex()
    {
        GameTable? doc = _cache.GetTable("Spells");
        if (doc is null) return EmptyCastIndex;

        Dictionary<int, List<KnownSpell>> map = new();
        foreach (GameRow row in doc.Rows)
        {
            string? castedBy = ReadString(row, "Casted By");
            if (castedBy is null) continue;

            KnownSpell? projected = null;
            foreach (Match m in CastedByTextblock.Matches(castedBy))
            {
                if (!int.TryParse(m.Groups[1].Value, out int tb) || tb <= 0) continue;
                projected ??= ToKnownSpell(row);
                if (!map.TryGetValue(tb, out List<KnownSpell>? list))
                    map[tb] = list = new List<KnownSpell>();
                list.Add(projected.Value);
            }
        }

        Dictionary<int, IReadOnlyList<KnownSpell>> result = new(map.Count);
        foreach (KeyValuePair<int, List<KnownSpell>> kv in map) result[kv.Key] = kv.Value;
        return result;
    }

    private static readonly IReadOnlyDictionary<int, IReadOnlyList<KnownSpell>> EmptyCastIndex
        = new Dictionary<int, IReadOnlyList<KnownSpell>>();

    // MajorMUD ability code for a cast-on-use spell ("CastsSp"): the slot's
    // AbilVal is the Spells.Number the item casts when used.
    private const int CastsSpAbilityCode = 43;

    // A CastsSp slot only means "command-activated on-use cast" when nothing
    // reframes it. A preceding %Spell (114) turns the following CastsSp into an
    // automatic per-swing combat proc (extra damage lines while fighting — e.g.
    // a hellblade's sunsword); a preceding CastOnKill% (1114) turns it into an
    // on-kill proc that only fires on a monster death (e.g. a fukumen's
    // invigorate). Neither is something the player casts on command, so a
    // CastsSp riding on one of these modifiers is NOT a Spell Book cast source.
    private const int PctSpellAbilityCode = 114;
    private const int CastOnKillAbilityCode = 1114;

    // MajorMUD ability code for the item's minimum-level gate ("MinLevel"): the
    // slot's AbilVal is the character level required to wear / use the item.
    private const int MinLevelAbilityCode = 135;

    // MajorMUD ability code for a teaching item ("LearnSp"): the slot's AbilVal is
    // the Spells.Number the item teaches when read. Distinct from CastsSp (43),
    // which casts a spell on use rather than teaching it.
    private const int LearnSpAbilityCode = 42;

    // Items carry 20 ability slots (Abil-0..19) vs the 10 on Spells / Classes.
    private const int ItemAbilSlots = 20;

    // Items denormalise their class restriction into 10 ClassRest-N slots,
    // each a Classes.Number that may use the item.
    private const int ItemClassRestSlots = 10;

    // Long enough for an item's slots, so for a spell's ten too.
    private static readonly string[] AbilFields = NumberedFields.Of("Abil-", ItemAbilSlots);
    private static readonly string[] AbilValFields = NumberedFields.Of("AbilVal-", ItemAbilSlots);
    private static readonly string[] ClassRestFields = NumberedFields.Of("ClassRest-", ItemClassRestSlots);

    // A spellbook cast-on-use item must be equippable in one of the player's
    // equipment slots (MegaMUD's Hold panel). Weapons (ItemType 1) ready into the
    // Weapon slot; everything else is gated on a real Worn slot. Worn 0 (Nowhere)
    // and 1 (Everywhere) aren't equip slots, so non-equippable "use" items —
    // potions, food, containers/chests, scrolls, projectiles, and room Sign items
    // like the dark plated warchest — are excluded: you drink/eat/open/read those,
    // you don't ready them to cast a buff. Worn 2..19 spans Head…Face plus
    // Off-Hand and the generic Worn slot.
    private const int WeaponItemType = 1;
    private const int FirstWornSlot = 2;
    private const int LastWornSlot = 19;

    // Inventory slot label a wielded weapon occupies (weapons aren't in the Worn
    // table). Matches ItemCastSequencer's WeaponHandSlot + EquippedItem.Slot.
    private const string WeaponHandSlotLabel = "Weapon Hand";

    // Every cast-on-use item the class can use — an Items row that both
    // (a) is usable by classNumber via its ClassRest-0..9 restriction (no
    // entries => universal) and (b) carries a CastsSp ability (code 43) naming
    // a spell. Each result resolves the cast Spells.Name, the item's UseCount
    // charges (<= 0 = unlimited), and the cast spell's rendered affect line
    // scaled to the item's use-level. Sorted by item name then spell name.
    // Empty when no class is set, the set has no Items table, or nothing
    // matches.
    public IReadOnlyList<ClassCastItem> GetClassCastItems(int classNumber)
    {
        List<ClassCastItem> results = new();
        if (classNumber < 1) return results;

        GameTable? items = _cache.GetTable("Items");
        if (items is null) return results;

        Dictionary<int, (string Name, int ManaCost)>? spellInfo = null; // built lazily on first cast item
        IReadOnlyDictionary<int, IReadOnlyList<KnownSpell>>? tbIndex = null; // ditto, for textblock casts
        foreach (GameRow row in items.Rows)
        {
            if (!ItemUsableByClass(row, classNumber)) continue;
            // An item with no equip slot counts only when it is kept after use — a
            // class tool like Paradigm's deck of cards (report
            // paradigm-20261002-114043), not a potion, scroll or thrown flask.
            bool carried = !IsEquippableCastItem(row);
            if (carried && ReadInt(row, "Retain After Uses") != 1) continue;

            // One scan of the 20 ability slots pulls both facts we need: the
            // first command-activated CastsSp (code 43) slot names the use-spell,
            // and a MinLevel (code 135) slot names the level gate. An item casts a
            // single use-spell, so the first qualifying CastsSp wins. A CastsSp
            // preceded by a %Spell / CastOnKill% modifier is an automatic combat /
            // kill proc, not an on-use cast — it's skipped so proc weapons and
            // on-kill gear never masquerade as command-cast spell sources.
            int spellNumber = 0;
            int minLevel = 0;
            bool pendingProc = false;
            for (int i = 0; i < ItemAbilSlots; i++)
            {
                int code = ReadInt(row, AbilFields[i]);
                if (code == PctSpellAbilityCode || code == CastOnKillAbilityCode)
                {
                    pendingProc = true;
                    continue;
                }
                if (code == CastsSpAbilityCode)
                {
                    if (pendingProc) { pendingProc = false; continue; } // proc, not on-use
                    if (spellNumber <= 0) spellNumber = ReadInt(row, AbilValFields[i]);
                    continue;
                }
                if (code == MinLevelAbilityCode)
                    minLevel = ReadInt(row, AbilValFields[i]);
            }
            if (spellNumber <= 0) continue;

            string itemName = ReadString(row, "Name")?.Trim() ?? string.Empty;
            if (itemName.Length == 0) continue;

            spellInfo ??= BuildSpellInfoMap();
            (string spellName, int manaCost) = spellInfo.TryGetValue(spellNumber, out (string Name, int ManaCost) info)
                ? info
                : (string.Empty, 0);

            // The cast spell's effect scales to the item's use-level (its MinLevel
            // gate) — the same convention the Game Data Items pane uses when it
            // renders a weapon's use-cast spell: the item, not a character, delivers
            // the spell, so its required level is the spell's effective base level.
            string spellEffect = RenderCastEffect(spellNumber, minLevel, ref tbIndex);

            results.Add(new ClassCastItem(
                ItemNumber: ReadInt(row, "Number"),
                ItemName: itemName,
                SpellNumber: spellNumber,
                SpellName: spellName,
                ManaCost: manaCost,
                UseCount: ReadInt(row, "UseCount"),
                IsTwoHanded: LookupEnums.IsTwoHandedWeaponType(ReadInt(row, "WeaponType")),
                ClassRestricted: ItemClassRestricted(row, classNumber),
                MinLevel: minLevel,
                SpellEffect: spellEffect,
                WearSlot: carried ? string.Empty : WearSlotFor(row),
                Carried: carried,
                Outcomes: RandomOutcomes(spellNumber, minLevel, ref tbIndex),
                CanRedraw: CanRedraw(spellNumber)));
        }

        results.Sort(static (a, b) =>
        {
            int byItem = string.Compare(a.ItemName, b.ItemName, StringComparison.OrdinalIgnoreCase);
            return byItem != 0 ? byItem : string.Compare(a.SpellName, b.SpellName, StringComparison.OrdinalIgnoreCase);
        });
        return results;
    }

    // The Items.Number of the item that TEACHES the given spell — an Items row
    // carrying a LearnSp ability (code 42) whose value is the taught Spells.Number.
    // 0 when no item teaches it (trainer-taught spells learn from an NPC, not an
    // item) or no Items table is loaded. Powers the Spell Book's double-click-to-
    // item-record. A full-table scan, run only on that click.
    //
    // Matched on the EXACT spell number, deliberately: the data carries same-named
    // spells at different magery levels (e.g. divine disfavour #67 universal /
    // MageryLVL 2, taught by a scroll, vs #5087 Paladin-only / MageryLVL 1, taught
    // by a trainer). The Spell Book already shows only the version the class can
    // actually learn, so the teaching item must be for THAT version — matching a
    // same-named sibling would point at a scroll for a spell the class can't learn.
    //
    // Several items can teach one spell, and the first in the table isn't always one a
    // player can get: lesser arcane enchantment #5535 is taught by the out-of-play tome
    // #3656 as well as the shop-sold scroll #3678. So the spell's own "Learned From"
    // item wins, then an item in play, and an out-of-play one only as a last resort.
    public int GetTeachingItemNumber(int spellNumber)
    {
        if (spellNumber <= 0) return 0;
        GameTable? items = _cache.GetTable("Items");
        if (items is null) return 0;

        HashSet<int> named = LearnedFromItems(spellNumber);
        int inPlay = 0, outOfPlay = 0;
        foreach (GameRow row in items.Rows)
        {
            if (!TeachesSpell(row, spellNumber)) continue;
            int number = ReadInt(row, "Number");
            if (named.Contains(number)) return number;
            if (InGameFlag.IsOutOfPlay(row)) { if (outOfPlay == 0) outOfPlay = number; }
            else if (inPlay == 0) inPlay = number;
        }
        return inPlay != 0 ? inPlay : outOfPlay;
    }

    // Every in-play teaching item the class may use, as (item, spell taught) pairs —
    // the candidate list for buying a spell from a shop. One full Items scan.
    public IReadOnlyList<TeachingItem> GetTeachingItems(int classNumber)
    {
        List<TeachingItem> results = new();
        GameTable? items = _cache.GetTable("Items");
        if (items is null) return results;

        foreach (GameRow row in items.Rows)
        {
            if (InGameFlag.IsOutOfPlay(row) || !ItemUsableByClass(row, classNumber)) continue;
            string name = ReadString(row, "Name")?.Trim() ?? string.Empty;
            if (name.Length == 0) continue;
            for (int i = 0; i < ItemAbilSlots; i++)
            {
                if (ReadInt(row, AbilFields[i]) != LearnSpAbilityCode) continue;
                int spell = ReadInt(row, AbilValFields[i]);
                if (spell > 0) results.Add(new TeachingItem(ReadInt(row, "Number"), name, spell));
            }
        }
        return results;
    }

    private static bool TeachesSpell(GameRow item, int spellNumber)
    {
        for (int i = 0; i < ItemAbilSlots; i++)
            if (ReadInt(item, AbilFields[i]) == LearnSpAbilityCode
                && ReadInt(item, AbilValFields[i]) == spellNumber)
                return true;
        return false;
    }

    private static readonly Regex LearnedFromItemRe =
        new(@"Item\s*#\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The items the spell's "Learned From" annotation names ("Item #3678").
    private HashSet<int> LearnedFromItems(int spellNumber)
    {
        HashSet<int> numbers = new();
        if (_cache.FindRowByNumber("Spells", spellNumber) is not { } row) return numbers;
        foreach (Match m in LearnedFromItemRe.Matches(ReadString(row, "Learned From") ?? string.Empty))
            if (int.TryParse(m.Groups[1].Value, out int n)) numbers.Add(n);
        return numbers;
    }

    private static readonly Regex LearnedFromNpcRe =
        new(@"NPC\s*#\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The Monsters.Number of the NPC that teaches this spell — from the spell's
    // "Learned From" annotation ("NPC #696"), first NPC when several are listed. 0
    // when the spell isn't NPC-taught. Backs the Spell Book's double-click for
    // trainer-taught spells (which have no teaching item — a Paladin's divine
    // disfavour / greater healing learn from the gaunt one spell-weaver, #696).
    public int GetTeachingNpcNumber(int spellNumber)
    {
        if (spellNumber <= 0) return 0;
        GameRow? row = _cache.FindRowByNumber("Spells", spellNumber);
        if (row is null) return 0;
        Match m = LearnedFromNpcRe.Match(ReadString(row.Value, "Learned From") ?? string.Empty);
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : 0;
    }

    // TBInfo teaching-script tokens: a line teaching a spell to a class reads e.g.
    // `check class:class 3:minlevel 50 4025:price ...:learnspell 5087:message 4031`
    // — the class number, the min level gate, and one or more taught spells. The
    // data is dirty (truncated tokens), so match tolerantly per token rather than
    // parsing the whole grammar.
    private static readonly Regex TeachClassRe    = new(@"\bclass\s+(\d+)",      RegexOptions.Compiled);
    private static readonly Regex TeachMinLevelRe = new(@"\bminlevel\s+(\d+)",   RegexOptions.Compiled);
    private static readonly Regex TeachLearnRe    = new(@"\blearnspell\s+(\d+)", RegexOptions.Compiled);

    // Per-class trainer LEARN-level gates, parsed from the TBInfo teaching scripts.
    // A spell learned from an NPC (Spells."Learned From" = "NPC #...") is gated by a
    // `check class:class N:minlevel M ... learnspell S` line: class N can't learn
    // spell S until level M — often HIGHER than the spell's own ReqLevel (a Paladin's
    // divine disfavour #5087 gates at 50, not the spell row's ReqLevel 19). Returns
    // spellNumber → the lowest such M across every trainer for classNumber, so the
    // Spell Book can show the real unlock level instead of the misleading ReqLevel.
    // Empty when the class has no trainer-gated spells or there's no TBInfo table.
    public IReadOnlyDictionary<int, int> BuildTeachLevelsForClass(int classNumber)
    {
        Dictionary<int, int> result = new();
        if (classNumber < 1) return result;
        GameTable? doc = _cache.GetTable("TBInfo");
        if (doc is null) return result;

        foreach (GameRow row in doc.Rows)
        {
            string? action = ReadString(row, "Action");
            if (string.IsNullOrEmpty(action)
                || !action.Contains("learnspell", System.StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (string line in action.Split('\n'))
            {
                Match cm = TeachClassRe.Match(line);
                Match lm = TeachMinLevelRe.Match(line);
                if (!cm.Success || !lm.Success) continue;
                if (!int.TryParse(cm.Groups[1].Value, out int cls) || cls != classNumber) continue;
                if (!int.TryParse(lm.Groups[1].Value, out int lvl)) continue;

                foreach (Match sm in TeachLearnRe.Matches(line))
                    if (int.TryParse(sm.Groups[1].Value, out int spell))
                        result[spell] = result.TryGetValue(spell, out int cur)
                            ? System.Math.Min(cur, lvl) : lvl;
            }
        }
        return result;
    }

    // Render a cast item's spell as a compact affect line ("AC +10", "Dmg 14–22",
    // "Dur 8 · Strength +3") at the given use-level, reusing the same formatter the
    // spell grid uses so an item's cast reads identically to the learnable spell.
    // Returns empty when the spell doesn't resolve or decodes to no figure. The
    // textblock cast-index is a full-table scan, so it's built lazily on the first
    // cast item that needs it and shared across the rest of the scan.
    private string RenderCastEffect(
        int spellNumber, int castLevel, ref IReadOnlyDictionary<int, IReadOnlyList<KnownSpell>>? tbIndex)
    {
        if (GetFormulaByNumber(spellNumber) is not { } formula) return string.Empty;
        tbIndex ??= BuildCastByTextblockIndex();
        IReadOnlyDictionary<int, IReadOnlyList<KnownSpell>> idx = tbIndex;
        string rendered = SpellEffectFormatter.Format(
            formula, castLevel,
            resolveChain: GetFormulaByNumber,
            resolveSpellName: GetSpellNameByNumber,
            resolveTextblockCasts: tb => idx.TryGetValue(tb, out IReadOnlyList<KnownSpell>? list)
                ? list : System.Array.Empty<KnownSpell>(),
            resolveMonsterName: n => _cache.FindNameByNumber("Monsters", n));
        return rendered == "—" ? string.Empty : rendered;
    }

    // The spells a use-spell deals at random. Such a spell carries no effect of its
    // own: its textblock (ability 148) runs a `random <table>` step, and the spells
    // that table casts are the outcomes — the deck of cards' cards. Empty for an
    // ordinary cast spell. Each outcome's chance comes from its own "Casted By" entry
    // for the table ("Textblock #9822(12%)").
    private IReadOnlyList<CastOutcome> RandomOutcomes(
        int spellNumber, int castLevel, ref IReadOnlyDictionary<int, IReadOnlyList<KnownSpell>>? tbIndex)
    {
        if (_cache.FindRowByNumber("Spells", spellNumber) is not { } spell) return System.Array.Empty<CastOutcome>();
        int textblock = 0;
        for (int i = 0; i < SpellAbilSlots && textblock == 0; i++)
            if (ReadInt(spell, AbilFields[i]) == TextblockAbilityCode) textblock = ReadInt(spell, AbilValFields[i]);
        if (textblock <= 0 || TextblockAction(textblock) is not { } action) return System.Array.Empty<CastOutcome>();

        List<CastOutcome> outcomes = new();
        foreach (Match table in RandomTableStep.Matches(action))
        {
            if (!int.TryParse(table.Groups[1].Value, out int tableNumber)) continue;
            tbIndex ??= BuildCastByTextblockIndex();
            if (!tbIndex.TryGetValue(tableNumber, out IReadOnlyList<KnownSpell>? dealt)) continue;
            foreach (KnownSpell s in dealt)
                outcomes.Add(new CastOutcome(
                    s.Number, s.Name, ChanceFrom(s.Number, tableNumber),
                    RenderCastEffect(s.Number, castLevel, ref tbIndex),
                    SpellCalculator.Duration(s.Formula, castLevel)));
        }
        return outcomes;
    }

    // Whether using a draw item again deals a new outcome over the one already up.
    // A `failability N` step ahead of the table ends the use while the user has
    // ability N, and every outcome carries one (15, the fortune marker), so a card
    // that is up blocks the next draw. The use only gets past it when an earlier
    // `cast` step has just removed the outcomes — Paradigm's deck shuffles first,
    // Stock's does not.
    private bool CanRedraw(int spellNumber)
    {
        if (_cache.FindRowByNumber("Spells", spellNumber) is not { } spell) return true;
        int textblock = 0;
        for (int i = 0; i < SpellAbilSlots && textblock == 0; i++)
            if (ReadInt(spell, AbilFields[i]) == TextblockAbilityCode) textblock = ReadInt(spell, AbilValFields[i]);
        if (textblock <= 0 || TextblockAction(textblock) is not { } action) return true;

        Match table = RandomTableStep.Match(action);
        if (!table.Success || !int.TryParse(table.Groups[1].Value, out int tableNumber)) return true;
        List<GameRow> outcomes = new();
        foreach (int dealt in SpellsCastByTextblock(tableNumber))
            if (_cache.FindRowByNumber("Spells", dealt) is { } row) outcomes.Add(row);

        HashSet<int> removed = new();
        foreach (Match step in GateStep.Matches(action[..table.Index]))
        {
            if (!int.TryParse(step.Groups[2].Value, out int n)) continue;
            if (step.Groups[1].Value.Equals("cast", StringComparison.OrdinalIgnoreCase))
            {
                if (_cache.FindRowByNumber("Spells", n) is not { } cast) continue;
                for (int i = 0; i < SpellAbilSlots; i++)
                    if (ReadInt(cast, AbilFields[i]) == RemovesSpellAbilityCode) removed.Add(ReadInt(cast, AbilValFields[i]));
                continue;
            }
            foreach (GameRow outcome in outcomes)
            {
                if (removed.Contains(ReadInt(outcome, "Number"))) continue;
                for (int i = 0; i < SpellAbilSlots; i++)
                    if (ReadInt(outcome, AbilFields[i]) == n) return false;
            }
        }
        return true;
    }

    // The spells whose "Casted By" names a textblock: the outcomes of a random table.
    private IEnumerable<int> SpellsCastByTextblock(int textblock)
    {
        GameTable? doc = _cache.GetTable("Spells");
        if (doc is null) yield break;
        Regex named = new($@"Textblock\s*#{textblock}\b", RegexOptions.IgnoreCase);
        foreach (GameRow row in doc.Rows)
            if (ReadString(row, "Casted By") is { } by && named.IsMatch(by))
                yield return ReadInt(row, "Number");
    }

    private const int TextblockAbilityCode = 148;
    private const int RemovesSpellAbilityCode = 122;
    private const int SpellAbilSlots = 10;

    // `cast 5145` or `failability 15` in a textblock's action.
    private static readonly Regex GateStep = new(@"\b(cast|failability)\s+(\d+)", RegexOptions.IgnoreCase);

    // `random 9822` in a textblock's action: run that table.
    private static readonly Regex RandomTableStep = new(@"\brandom\s+(\d+)", RegexOptions.IgnoreCase);

    // The action script of one TBInfo textblock. Number is text in some exports and a
    // number in others.
    private string? TextblockAction(int number)
    {
        GameTable? doc = _cache.GetTable("TBInfo");
        if (doc is null) return null;
        foreach (GameRow row in doc.Rows)
        {
            if (!row.TryGetProperty("Number", out GameValue n)) continue;
            bool match = n.ValueKind == JsonValueKind.Number
                ? n.TryGetInt32(out int asInt) && asInt == number
                : int.TryParse(n.GetString(), out int asText) && asText == number;
            if (match) return ReadString(row, "Action");
        }
        return null;
    }

    // The percentage after one textblock in a spell's "Casted By" list, 0 when none.
    private int ChanceFrom(int spellNumber, int textblock)
    {
        if (_cache.FindRowByNumber("Spells", spellNumber) is not { } spell) return 0;
        string? castedBy = ReadString(spell, "Casted By");
        if (castedBy is null) return 0;
        Match m = Regex.Match(castedBy, $@"Textblock\s*#{textblock}\((-?\d+)%\)", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out int pct) ? pct : 0;
    }

    // Item class-usability: an item with no non-zero ClassRest-N slot is
    // universal (usable by every class); otherwise it's restricted to the
    // listed class numbers.
    private static bool ItemUsableByClass(GameRow row, int classNumber)
    {
        bool anyRestriction = false;
        for (int i = 0; i < ItemClassRestSlots; i++)
        {
            int c = ReadInt(row, ClassRestFields[i]);
            if (c == 0) continue;
            anyRestriction = true;
            if (c == classNumber) return true;
        }
        return !anyRestriction;
    }

    // True when the item names classNumber in an explicit ClassRest slot — a
    // class-specific cast source, as opposed to a universal item any class can
    // use. Distinct from ItemUsableByClass (which also passes universal items):
    // the Spell Book surfaces only the class-specific ones so a caster isn't
    // buried under every generic wand / scroll in the game.
    private static bool ItemClassRestricted(GameRow row, int classNumber)
    {
        for (int i = 0; i < ItemClassRestSlots; i++)
            if (ReadInt(row, ClassRestFields[i]) == classNumber) return true;
        return false;
    }

    // True when an item can be equipped in one of the player's equipment slots,
    // the precondition for using it to cast: a weapon (readied into the Weapon
    // slot) or an item worn in a real body / Off-Hand / Worn slot. Excludes
    // non-equippable "use" items (potions, food, containers, scrolls,
    // projectiles, room Signs) that can't be readied to cast a buff.
    private static bool IsEquippableCastItem(GameRow row)
    {
        if (ReadInt(row, "ItemType") == WeaponItemType) return true;
        int worn = ReadInt(row, "Worn");
        return worn is >= FirstWornSlot and <= LastWornSlot;
    }

    // The equip slot the cast item occupies, normalized to the inventory's
    // EquippedItem.Slot labels so the sequencer can look up what it displaces:
    // Off-Hand for a shield / warhorn, Neck for an amulet, the Weapon Hand for a
    // weapon. Prefer the real Worn slot — an item can be flagged a non-weapon
    // ItemType yet worn in a body slot (an engraved warhorn is ItemType=Armour,
    // Worn=Off-Hand), and `eq` drops it into that Worn slot; only a true weapon
    // (Worn 0/Nowhere) rides the weapon hand. Read Worn as an INT — it's a numeric
    // JSON field, so the string reader hands back null and the slot came back empty
    // (the bug that mislabeled the warhorn "Weapon Hand"). Empty only when neither
    // a mapped Worn slot nor a weapon, which the equippable filter already excludes.
    internal static string WearSlotFor(GameRow row)
    {
        int worn = ReadInt(row, "Worn");
        if (worn is >= FirstWornSlot and <= LastWornSlot)
            return LookupEnums.FormatWornSlot(worn) ?? WeaponHandSlotLabel;
        if (ReadInt(row, "ItemType") == WeaponItemType) return WeaponHandSlotLabel;
        return string.Empty;
    }

    // One-pass Spells.Number → (Name, ManaCost) map for resolving cast-item
    // spells (display name + whether using the item draws mana) without a
    // per-item table scan.
    private Dictionary<int, (string Name, int ManaCost)> BuildSpellInfoMap()
    {
        Dictionary<int, (string Name, int ManaCost)> map = new();
        GameTable? spells = _cache.GetTable("Spells");
        if (spells is null) return map;
        foreach (GameRow row in spells.Rows)
        {
            int number = ReadInt(row, "Number");
            if (number <= 0) continue;
            string name = ReadString(row, "Name")?.Trim() ?? string.Empty;
            map[number] = (name, ReadInt(row, "ManaCost"));
        }
        return map;
    }

    // "Casted By" lists each source as "Textblock #<number>" (comma-joined).
    // \d+ captures the full number so "#291" never partial-matches "#2910".
    private static readonly Regex CastedByTextblock =
        new(@"Textblock\s*#(\d+)", RegexOptions.IgnoreCase);

    // MajorMUD's spell-usability rule: can classNumber use this spell at level
    // / charAlign, and (when andLearnable) is it actually learnable. classMagery
    // / classMageryLvl are the pre-resolved magery type / level for
    // classNumber.
    private bool IsUsable(
        GameRow row, int classNumber, int classMagery, int classMageryLvl,
        int level, int charAlign, bool andLearnable)
    {
        if (classNumber < 1) return true;       // no class → treat as usable
        if (level < 0) level = 0;
        if (charAlign < 0) charAlign = 0;

        int spellMagery = ReadInt(row, "Magery");
        int spellMageryLvl = ReadInt(row, "MageryLVL");
        int reqLevel = ReadInt(row, "ReqLevel");
        int learnable = ReadInt(row, "Learnable");
        string? learnedFrom = ReadString(row, "Learned From");
        string? classes = ReadString(row, "Classes");

        // Learnable gate: a spell is learnable when Learnable==1, OR it has a
        // LearnedFrom source (length >= 5), OR it's a Kai spell with autolearn
        // enabled and a real level requirement.
        if (andLearnable)
        {
            bool notLearnable = learnable == 0
                && (learnedFrom?.Length ?? 0) < 5
                && (spellMagery != MageryKai
                    || (spellMagery == MageryKai && (_disableKaiAutolearn || reqLevel < 1)));
            if (notLearnable) return false;
        }

        // Magery check (skipped entirely when the spell has no magery).
        if (spellMagery != 0)
        {
            if (classMagery == MageryNone) return false;
            // The magery-mismatch exception requires Spells.Magery==0, which is
            // impossible inside this branch — so a mismatch is always
            // disqualifying.
            if (classMagery != spellMagery) return false;
            if (classMageryLvl > 0 && classMageryLvl < spellMageryLvl) return false;
            if (classMagery != MageryKai && learnable == 0) return false;
            if (classMagery == MageryKai && _disableKaiAutolearn && learnable == 0) return false;
        }

        // Explicit class-token restriction (data version assumed >= 1.7). A
        // non-"(*)" Classes field limits the spell to the listed class numbers.
        if (classes is not null && classes.Length > 2 && classes != "(*)")
        {
            if (!classes.Contains($"({classNumber})", StringComparison.OrdinalIgnoreCase)) return false;
        }

        if (level > 0 && level < reqLevel) return false;

        // Alignment-restriction abilities (only evaluated when a character
        // alignment is supplied, or for Kai under GreaterMUD).
        if (charAlign > 0 || (classMagery == MageryKai && _greaterMud))
        {
            for (int x = 0; x < 10; x++)
            {
                int code = ReadInt(row, AbilFields[x]);
                switch (code)
                {
                    case 97 or 98 or 112: // requires good / evil / neutral
                        if (charAlign == 1 && code != 97) return false;
                        if (charAlign == 2 && code != 112) return false;
                        if (charAlign == 3 && code != 98) return false;
                        if (charAlign == 4) return false;   // evil-only needs Outlaw
                        break;
                    case 110 or 111 or 113: // requires NOT good / evil / neutral
                        if (charAlign == 1 && code == 110) return false;
                        if (charAlign == 2 && code == 113) return false;
                        if (charAlign is 3 or 4 && code == 111) return false;
                        break;
                }
            }
        }

        return true;
    }

    // Project a Spells row into a KnownSpell + its formula inputs.
    private static KnownSpell ToKnownSpell(GameRow row)
        => new(
            Number: ReadInt(row, "Number"),
            Short: ReadString(row, "Short") ?? string.Empty,
            Name: ReadString(row, "Name") ?? string.Empty,
            Magery: ReadInt(row, "Magery"),
            MageryLvl: ReadInt(row, "MageryLVL"),
            ReqLevel: ReadInt(row, "ReqLevel"),
            Targets: ReadInt(row, "Targets"),
            Formula: ToFormula(row));

    // The formula projection is shared with MonsterCatalog via SpellFormulaReader
    // so both read a spell's scaling fields the same way.
    private static SpellFormulaInput ToFormula(GameRow row) => SpellFormulaReader.Read(row);

    // ----- NUL-aware JSON readers ---------------------------------------
    // The MdbImporter writes a literal NUL char ("\0") for empty Jet text
    // columns, so a plain GetString can hand back "\0" rather than null.
    // These mirror the same sentinel handling SpellCoverageAuditor uses.

    private static string? ReadString(GameRow row, string property)
    {
        if (!row.TryGetProperty(property, out GameValue el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        string? raw = el.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (char c in raw)
            if (c != '\0' && !char.IsWhiteSpace(c)) return raw;
        return null;
    }

    private static int ReadInt(GameRow row, string property)
    {
        if (!row.TryGetProperty(property, out GameValue el)) return 0;
        if (el.ValueKind != JsonValueKind.Number) return 0;
        return el.TryGetInt32(out int n) ? n : 0;
    }
}
