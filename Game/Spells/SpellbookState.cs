using System.Collections.Generic;

namespace FujinTerm.Game.Spells;

// In-memory model of the local character's spell book: the full set of spells
// the current class can ever learn (Available, from KnownSpellCatalog) paired
// with the subset the character has actually obtained (keyed by Spells.Number).
//
// Available is the class list with the level gate ignored (queried at level 0)
// — every spell the class can reach, each row carrying its own ReqLevel; the
// obtained checkmark and the per-spell formula results scale to Level.
//
// The obtained set is fed by the live spells / pow list (a full snapshot via
// SetObtainedByNames) and the learn-scroll line (an incremental add via
// MarkObtainedByName). Both report the spell's full Name, so resolution matches
// against Available by name. Reroll clears it (ClearObtained).
public sealed class SpellbookState
{
    private readonly KnownSpellCatalog _catalog;
    private readonly HashSet<int> _obtained = new();
    private List<KnownSpell> _available = new();
    private SpellPick[] _availablePicks = Array.Empty<SpellPick>();

    public SpellbookState(KnownSpellCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    // The active Classes.Number the book is built for (0 = none / no class observed yet).
    public int ClassNumber { get; private set; }

    // The character level the per-spell formula results scale to. Does not gate Available.
    public int Level { get; private set; }

    // Character alignment used by the eligibility filter (0 = unknown / unrestricted).
    public int CharAlign { get; private set; }

    // Every spell the current class can learn, sorted by ReqLevel then Name. Empty for non-magery classes.
    public IReadOnlyList<KnownSpell> Available => _available;

    // The Available spells as distinct (by cast-code) SpellPick entries, ordered
    // by name — the suggestion source for the Settings spell-picker typeahead
    // boxes. Each carries the 4-letter Short cast-code (the value the box commits)
    // alongside the full name. Empty for non-magery classes. Rebuilt only when the
    // class list rebuilds; a bare level change leaves it unchanged.
    public IReadOnlyList<SpellPick> AvailablePicks => _availablePicks;

    // Fires when the available list or the obtained set changes.
    public event Action? Changed;

    // Resolve a Spells.Number to its full Name across the whole table. The Spell
    // Book's per-spell effect rollup uses this to render a RemovesSpell (Abil 122)
    // target, which can point at any spell rather than only the current class's
    // learnable list.
    public string? ResolveSpellName(int spellNumber) => _catalog.GetSpellNameByNumber(spellNumber);

    // Build the textblock → cast-spell reverse index across the whole Spells
    // table. The Spell Book's per-spell effect rollup uses it to expand an
    // Abil-148 (TextBlock) reference into the real effect the textblock casts,
    // which can point at any spell rather than only the current class's learnable
    // list.
    public IReadOnlyDictionary<int, IReadOnlyList<KnownSpell>> BuildCastByTextblockIndex()
        => _catalog.BuildCastByTextblockIndex();

    // The per-round mana cost of the spell with this Spells.Short cast-code, or
    // null when no available spell matches. Level-independent — the energy
    // multiplier folds in, the player level does not. The casting engine uses it
    // to skip a survival cast we can't actually pay for. Case-insensitive on the
    // trimmed cast-code; first match wins (duplicate Shorts share a cost).
    public int? ManaCostOf(string castCode)
        => FindByCastCode(castCode) is { } s ? (int)SpellCalculator.ManaCost(s.Formula) : null;

    // The available spell whose Spells.Short cast-code matches castCode
    // (case-insensitive, trimmed), or null when none does. First match wins —
    // duplicate Shorts share a formula. The Settings spell-picker preview uses it
    // to pull a pick's level-scaled numbers (mana cost, roll range) without
    // re-walking the list itself.
    public KnownSpell? FindByCastCode(string castCode)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return null;
        string target = castCode.Trim();
        foreach (KnownSpell s in _available)
            if (string.Equals(s.Short.Trim(), target, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    // The cast-on-use items the active class can use (wands / scrolls / potions
    // carrying an Items code-43 CastsSp ability). The Spell Book lists these
    // alongside learnable spells. Empty when no class is set yet.
    public IReadOnlyList<ClassCastItem> GetCastItems() => _catalog.GetClassCastItems(ClassNumber);

    // True when the character has learned the spell with this Spells.Number.
    public bool IsObtained(int spellNumber) => _obtained.Contains(spellNumber);

    // How many spells the character has obtained.
    public int ObtainedCount => _obtained.Count;

    // Rebuild Available for a new class+level. The available list only depends on
    // class+alignment (the level gate is ignored), so it's recomputed only when
    // those change; a bare level change still fires Changed so formula displays
    // rescale. Obtained numbers that fall outside the new class list (e.g. reroll
    // into a different class) are dropped.
    public void Refresh(int classNumber, int level, int charAlign = 0)
    {
        bool classChanged = classNumber != ClassNumber || charAlign != CharAlign;
        bool levelChanged = level != Level;
        ClassNumber = classNumber;
        Level = level;
        CharAlign = charAlign;

        if (classChanged)
        {
            _available = new List<KnownSpell>(_catalog.Query(classNumber, level: 0, charAlign));
            _obtained.RemoveWhere(n => !_available.Exists(s => s.Number == n));
            _availablePicks = _available
                .Where(s => !string.IsNullOrWhiteSpace(s.Short))
                .GroupBy(s => s.Short.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new SpellPick(g.Key, g.First().Name.Trim()))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (classChanged || levelChanged) Changed?.Invoke();
    }

    // Replace the obtained set with exactly the spells named in names (the
    // authoritative snapshot from a spells / pow block). Names that don't resolve
    // to an available spell are ignored. No-ops (no event) when the resolved set
    // matches the current one.
    public void SetObtainedByNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        HashSet<int> next = new();
        foreach (string name in names)
            if (FindAvailableByName(name) is { } s) next.Add(s.Number);

        if (next.SetEquals(_obtained)) return;
        _obtained.Clear();
        _obtained.UnionWith(next);
        Changed?.Invoke();
    }

    // Mark a single spell obtained by its full Name — the learn-scroll signal
    // ("…learn the spell harm."). Returns the resolved spell (so a caller can
    // surface it), or null when the name doesn't match an available spell. Fires
    // Changed only when the spell was newly added.
    public KnownSpell? MarkObtainedByName(string name)
    {
        if (FindAvailableByName(name) is not { } match) return null;
        if (_obtained.Add(match.Number)) Changed?.Invoke();
        return match;
    }

    // Drop every obtained spell (reroll / "you have no spells").
    public void ClearObtained()
    {
        if (_obtained.Count == 0) return;
        _obtained.Clear();
        Changed?.Invoke();
    }

    private KnownSpell? FindAvailableByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string target = name.Trim();
        foreach (KnownSpell s in _available)
            if (string.Equals(s.Name.Trim(), target, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }
}
