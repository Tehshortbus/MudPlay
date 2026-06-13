using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using FujinTerm.Services;

namespace FujinTerm.Game.Spells;

/// <summary>
/// Builds the per-class learnable-spell list from the active game-data
/// set's <c>Spells</c> + <c>Classes</c> tables. The eligibility filter is
/// a faithful port of MMUD Explorer's <c>SpellIsUsable</c>
/// (<c>modMMudFunc.bas</c>) — the same getter <c>GetSpellByShort</c> uses
/// to decide what a class can learn — so the Spell Book shows exactly the
/// spells the game would let the character know.
/// </summary>
/// <remarks>
/// <para>
/// Class identity flows in as the <c>stat</c>-reported class name; we
/// resolve it to the MDB <c>Classes.Number</c> (and its
/// <c>MageryType</c> / <c>MageryLVL</c>) before filtering, mirroring
/// <c>GetClassMagery</c> / <c>GetClassMageryLVL</c>.
/// </para>
/// <para>
/// Two realm-global flags from the VB source — <c>bDisableKaiAutolearn</c>
/// (Kai classes auto-learn unless the realm disables it; maps to the
/// game-data <c>Info.DisableKai</c> flag, wired in a follow-up) and
/// <c>bGreaterMUD</c> — default to <c>false</c> here. The version gate
/// <c>nNMRVer &gt;= 1.7</c> is treated as satisfied: modern MajorMUD /
/// MegaMUD data is &gt;= 1.7, so the explicit-class-token check always
/// applies.
/// </para>
/// </remarks>
public sealed class KnownSpellCatalog
{
    // enmMagicEnum (modMMudDatabase.bas): None=0, Mage=1, Priest=2,
    // Druid=3, Bard=4, Kai=5. Class MageryType values are the same enum,
    // so they compare directly against Spells.Magery.
    private const int MageryNone = 0;
    private const int MageryKai = 5;

    private readonly GameDataCache _cache;

    // Realm-global flags from SpellIsUsable. Not yet wired to game data
    // (Info.DisableKai → DisableKaiAutolearn is a planned follow-up); both
    // default false, matching a stock realm. Non-const so the branches
    // that read them don't trip unreachable-code analysis.
    private readonly bool _disableKaiAutolearn = false;
    private readonly bool _greaterMud = false;

    public KnownSpellCatalog(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    /// <summary>
    /// Resolve a <c>stat</c>-reported class name (e.g. "Mystic") to its
    /// <c>Classes.Number</c>. Case-insensitive, trimmed. Returns
    /// <c>null</c> when the active set has no class by that name.
    /// </summary>
    public int? ResolveClassNumber(string className)
    {
        if (string.IsNullOrWhiteSpace(className)) return null;
        JsonDocument? doc = _cache.GetRawTable("Classes");
        if (doc is null) return null;

        string target = className.Trim();
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            string? name = ReadString(row, "Name");
            if (name is not null && string.Equals(name.Trim(), target, StringComparison.OrdinalIgnoreCase))
                return ReadInt(row, "Number");
        }
        return null;
    }

    /// <summary>
    /// Reverse of <see cref="ResolveClassNumber"/>: resolve a
    /// <c>Classes.Number</c> to its display <c>Name</c>. Returns <c>null</c>
    /// when the active set has no class with that number. Used by the
    /// party-bless picker to compare a slot's stored class numbers against a
    /// <c>PartyMember.Class</c> name.
    /// </summary>
    public string? ResolveClassName(int classNumber)
    {
        if (classNumber < 1) return null;
        JsonDocument? doc = _cache.GetRawTable("Classes");
        if (doc is null) return null;

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
            if (ReadInt(row, "Number") == classNumber)
                return ReadString(row, "Name");
        return null;
    }

    /// <summary>
    /// Mirror of <c>GetClassMagery</c> / <c>GetClassMageryLVL</c>: returns
    /// the class's magery type (enmMagicEnum value) and emits its magery
    /// level. A class number with no matching row resolves to
    /// <c>None</c> / <c>0</c>.
    /// </summary>
    public int ResolveClassMagery(int classNumber, out int mageryLvl)
    {
        mageryLvl = 0;
        if (classNumber < 1) return MageryNone;
        JsonDocument? doc = _cache.GetRawTable("Classes");
        if (doc is null) return MageryNone;

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (ReadInt(row, "Number") != classNumber) continue;
            mageryLvl = ReadInt(row, "MageryLVL");
            return ReadInt(row, "MageryType");
        }
        return MageryNone;
    }

    /// <summary>
    /// Every spell <paramref name="classNumber"/> can learn, gated to
    /// <paramref name="level"/> (pass <c>0</c> for the full list ignoring
    /// the level requirement). Sorted by <see cref="KnownSpell.ReqLevel"/>
    /// then <see cref="KnownSpell.Name"/>. Empty for classes with no
    /// magery (Warriors, Thieves, etc.).
    /// </summary>
    public IReadOnlyList<KnownSpell> Query(int classNumber, int level, int charAlign = 0)
    {
        List<KnownSpell> results = new();
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return results;

        int classMagery = ResolveClassMagery(classNumber, out int classMageryLvl);
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!IsUsable(row, classNumber, classMagery, classMageryLvl, level, charAlign, andLearnable: true))
                continue;
            results.Add(ToKnownSpell(row));
        }

        results.Sort(static (a, b) =>
        {
            int byLevel = a.ReqLevel.CompareTo(b.ReqLevel);
            return byLevel != 0 ? byLevel : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return results;
    }

    /// <summary>
    /// Look up a single learnable spell by its <c>Short</c> cast-code for
    /// the given class — the catalog equivalent of
    /// <c>GetSpellByShort</c>. Returns <c>null</c> when no usable spell
    /// matches. <paramref name="level"/> <c>0</c> ignores the level gate.
    /// </summary>
    public KnownSpell? GetByShort(string shortCode, int classNumber, int level = 0, int charAlign = 0)
    {
        if (string.IsNullOrWhiteSpace(shortCode)) return null;
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return null;

        string target = shortCode.Trim();
        int classMagery = ResolveClassMagery(classNumber, out int classMageryLvl);
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            string? s = ReadString(row, "Short");
            if (s is null || !string.Equals(s.Trim(), target, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsUsable(row, classNumber, classMagery, classMageryLvl, level, charAlign, andLearnable: true))
                continue;
            return ToKnownSpell(row);
        }
        return null;
    }

    /// <summary>
    /// Look up a single learnable spell by its full <c>Name</c> for the
    /// given class. The <c>spells</c> / <c>pow</c> list rows and the
    /// learn-scroll line ("…learn the spell <i>harm</i>.") both report the
    /// spell's Name rather than its Short code, so the obtained-signal path
    /// resolves through here. Returns <c>null</c> when no usable spell
    /// matches. <paramref name="level"/> <c>0</c> ignores the level gate.
    /// </summary>
    public KnownSpell? GetByName(string name, int classNumber, int level = 0, int charAlign = 0)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return null;

        string target = name.Trim();
        int classMagery = ResolveClassMagery(classNumber, out int classMageryLvl);
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            string? n = ReadString(row, "Name");
            if (n is null || !string.Equals(n.Trim(), target, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsUsable(row, classNumber, classMagery, classMageryLvl, level, charAlign, andLearnable: true))
                continue;
            return ToKnownSpell(row);
        }
        return null;
    }

    /// <summary>
    /// Resolve a <c>Spells.Number</c> to its full <c>Name</c> across the
    /// entire Spells table (no class / magery / learnable filter) — the
    /// catalog equivalent of MMUD Explorer's <c>GetSpellName</c>. Used to
    /// render the <c>RemovesSpell</c> (Abil 122) target by name, which can
    /// reference any spell, not just the active class's learnable list.
    /// Returns <c>null</c> when no row has that number.
    /// </summary>
    public string? GetSpellNameByNumber(int spellNumber)
    {
        if (spellNumber < 1) return null;
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return null;

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (ReadInt(row, "Number") != spellNumber) continue;
            return ReadString(row, "Name");
        }
        return null;
    }

    /// <summary>
    /// Resolve a <c>Spells.Number</c> to its <see cref="SpellFormulaInput"/>
    /// across the entire Spells table (no class / magery / learnable filter).
    /// Used by the Game Data Items pane to render a weapon's use-cast / proc
    /// spell effect without depending on a class spell book. Returns
    /// <c>null</c> when no row has that number.
    /// </summary>
    public SpellFormulaInput? GetFormulaByNumber(int spellNumber)
    {
        if (spellNumber < 1) return null;
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return null;

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (ReadInt(row, "Number") != spellNumber) continue;
            return ToFormula(row);
        }
        return null;
    }

    /// <summary>
    /// Build the reverse index from a <c>TBInfo</c> textblock number to every
    /// spell that textblock casts, parsed from each spell row's denormalised
    /// <c>Casted By</c> column (e.g. <c>"Textblock #2910, Textblock #2911"</c>).
    /// The Spell Book uses it to expand an Abil-148 (TextBlock) reference into
    /// the real effect(s) the textblock applies — duration + stat bonuses for
    /// transform spells like <c>form of the dragon</c> — instead of an opaque
    /// record number. General across data sets: any spell whose <c>Casted By</c>
    /// names a textblock is linked, with no per-spell special-casing. Empty when
    /// the active set has no Spells table.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<KnownSpell>> BuildCastByTextblockIndex()
    {
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return EmptyCastIndex;

        Dictionary<int, List<KnownSpell>> map = new();
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
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

    // "Casted By" lists each source as "Textblock #<number>" (comma-joined).
    // \d+ captures the full number so "#291" never partial-matches "#2910".
    private static readonly Regex CastedByTextblock =
        new(@"Textblock\s*#(\d+)", RegexOptions.IgnoreCase);

    /// <summary>
    /// Faithful port of <c>SpellIsUsable(nSpell, nClass, nLevel,
    /// nCharAlign, bAndLearnable)</c>. <paramref name="classMagery"/> /
    /// <paramref name="classMageryLvl"/> are the pre-resolved
    /// <c>GetClassMagery</c> / <c>GetClassMageryLVL</c> values for
    /// <paramref name="classNumber"/>.
    /// </summary>
    private bool IsUsable(
        JsonElement row, int classNumber, int classMagery, int classMageryLvl,
        int level, int charAlign, bool andLearnable)
    {
        if (classNumber < 1) return true;       // VB: nClass < 1 → usable
        if (level < 0) level = 0;
        if (charAlign < 0) charAlign = 0;

        int spellMagery = ReadInt(row, "Magery");
        int spellMageryLvl = ReadInt(row, "MageryLVL");
        int reqLevel = ReadInt(row, "ReqLevel");
        int learnable = ReadInt(row, "Learnable");
        string? learnedFrom = ReadString(row, "Learned From");
        string? classes = ReadString(row, "Classes");

        // bAndLearnable gate: a spell is learnable when Learnable==1, OR it
        // has a LearnedFrom source (Len>=5), OR it's a Kai spell with
        // autolearn enabled and a real level requirement.
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
            // The VB magery-mismatch exception requires Spells.Magery==0,
            // which is impossible inside this branch — so a mismatch is
            // always disqualifying.
            if (classMagery != spellMagery) return false;
            if (classMageryLvl > 0 && classMageryLvl < spellMageryLvl) return false;
            if (classMagery != MageryKai && learnable == 0) return false;
            if (classMagery == MageryKai && _disableKaiAutolearn && learnable == 0) return false;
        }

        // skip_magery_check: explicit class-token restriction (nNMRVer>=1.7
        // assumed). A non-"(*)" Classes field limits the spell to the
        // listed class numbers.
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
                int code = ReadInt(row, $"Abil-{x}");
                switch (code)
                {
                    case 97 or 98 or 112: // requires good / evil / neutral
                        if (charAlign == 1 && code != 97) return false;
                        if (charAlign == 2 && code != 112) return false;
                        if (charAlign == 3 && code != 98) return false;
                        break;
                    case 110 or 111 or 113: // requires NOT good / evil / neutral
                        if (charAlign == 1 && code == 110) return false;
                        if (charAlign == 2 && code == 113) return false;
                        if (charAlign == 3 && code == 111) return false;
                        break;
                }
            }
        }

        return true;
    }

    /// <summary>Project a Spells row into a <see cref="KnownSpell"/> + its formula inputs.</summary>
    private static KnownSpell ToKnownSpell(JsonElement row)
        => new(
            Number: ReadInt(row, "Number"),
            Short: ReadString(row, "Short") ?? string.Empty,
            Name: ReadString(row, "Name") ?? string.Empty,
            Magery: ReadInt(row, "Magery"),
            MageryLvl: ReadInt(row, "MageryLVL"),
            ReqLevel: ReadInt(row, "ReqLevel"),
            Targets: ReadInt(row, "Targets"),
            Formula: ToFormula(row));

    private static SpellFormulaInput ToFormula(JsonElement row)
    {
        List<SpellAbility> abilities = new();
        for (int x = 0; x < 10; x++)
        {
            int code = ReadInt(row, $"Abil-{x}");
            if (code == 0) continue; // empty slot — the calculator ignores code 0 anyway
            abilities.Add(new SpellAbility(code, ReadInt(row, $"AbilVal-{x}")));
        }

        return new SpellFormulaInput
        {
            Number = ReadInt(row, "Number"),
            MinBase = ReadInt(row, "MinBase"),
            MinInc = ReadInt(row, "MinInc"),
            MinIncLVLs = ReadInt(row, "MinIncLVLs"),
            MaxBase = ReadInt(row, "MaxBase"),
            MaxInc = ReadInt(row, "MaxInc"),
            MaxIncLVLs = ReadInt(row, "MaxIncLVLs"),
            Dur = ReadInt(row, "Dur"),
            DurInc = ReadInt(row, "DurInc"),
            DurIncLVLs = ReadInt(row, "DurIncLVLs"),
            Cap = ReadInt(row, "Cap"),
            ReqLevel = ReadInt(row, "ReqLevel"),
            EnergyCost = ReadInt(row, "EnergyCost"),
            ManaCost = ReadInt(row, "ManaCost"),
            Abilities = abilities,
        };
    }

    // ----- NUL-aware JSON readers ---------------------------------------
    // The MdbImporter writes a literal NUL char ("\0") for empty Jet text
    // columns, so a plain GetString can hand back "\0" rather than null.
    // These mirror the same sentinel handling SpellCoverageAuditor uses.

    private static string? ReadString(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out JsonElement el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        string? raw = el.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (char c in raw)
            if (c != '\0' && !char.IsWhiteSpace(c)) return raw;
        return null;
    }

    private static int ReadInt(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out JsonElement el)) return 0;
        if (el.ValueKind != JsonValueKind.Number) return 0;
        return el.TryGetInt32(out int n) ? n : 0;
    }
}
