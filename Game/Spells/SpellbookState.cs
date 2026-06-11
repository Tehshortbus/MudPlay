using System.Collections.Generic;

namespace FujinTerm.Game.Spells;

/// <summary>
/// In-memory model of the local character's spell book: the full set of
/// spells the current class can ever learn (<see cref="Available"/>, from
/// <see cref="KnownSpellCatalog"/>) paired with the subset the character has
/// actually obtained (the obtained set, keyed by <c>Spells.Number</c>).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Available"/> is the class list with the level gate ignored
/// (<c>Query(classNumber, level: 0)</c>) — mirroring MMUD Explorer's
/// <c>PasteSpells</c>, which calls <c>SpellIsUsable(..., nClass, , , True)</c>
/// with no level argument. The window shows every spell the class can reach,
/// each row carrying its own <see cref="KnownSpell.ReqLevel"/>; the obtained
/// checkmark and the per-spell formula results scale to <see cref="Level"/>.
/// </para>
/// <para>
/// The obtained set is fed by the live <c>spells</c> / <c>pow</c> list (a
/// full snapshot — <see cref="SetObtainedByNames"/>) and the learn-scroll
/// line (an incremental add — <see cref="MarkObtainedByName"/>). Both report
/// the spell's full <c>Name</c>, so resolution matches against
/// <see cref="Available"/> by name. Reroll clears it
/// (<see cref="ClearObtained"/>).
/// </para>
/// </remarks>
public sealed class SpellbookState
{
    private readonly KnownSpellCatalog _catalog;
    private readonly HashSet<int> _obtained = new();
    private List<KnownSpell> _available = new();
    private string[] _availableNames = Array.Empty<string>();

    public SpellbookState(KnownSpellCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    /// <summary>The active <c>Classes.Number</c> the book is built for (0 = none / no class observed yet).</summary>
    public int ClassNumber { get; private set; }

    /// <summary>The character level the per-spell formula results scale to. Does not gate <see cref="Available"/>.</summary>
    public int Level { get; private set; }

    /// <summary>Character alignment used by the eligibility filter (0 = unknown / unrestricted).</summary>
    public int CharAlign { get; private set; }

    /// <summary>Every spell the current class can learn, sorted by ReqLevel then Name. Empty for non-magery classes.</summary>
    public IReadOnlyList<KnownSpell> Available => _available;

    /// <summary>
    /// The <see cref="Available"/> spell names as a distinct, alphabetically
    /// sorted list — the suggestion source for the Settings spell-picker
    /// typeahead boxes. Empty for non-magery classes. Rebuilt only when the
    /// class list rebuilds; a bare level change leaves the names unchanged.
    /// </summary>
    public IReadOnlyList<string> AvailableNames => _availableNames;

    /// <summary>Fires when the available list or the obtained set changes.</summary>
    public event Action? Changed;

    /// <summary>True when the character has learned the spell with this <c>Spells.Number</c>.</summary>
    public bool IsObtained(int spellNumber) => _obtained.Contains(spellNumber);

    /// <summary>How many spells the character has obtained.</summary>
    public int ObtainedCount => _obtained.Count;

    /// <summary>
    /// Rebuild <see cref="Available"/> for a new class+level. The available
    /// list only depends on class+alignment (the level gate is ignored), so
    /// it's recomputed only when those change; a bare level change still
    /// fires <see cref="Changed"/> so formula displays rescale. Obtained
    /// numbers that fall outside the new class list (e.g. reroll into a
    /// different class) are dropped.
    /// </summary>
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
            _availableNames = _available
                .Select(s => s.Name.Trim())
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (classChanged || levelChanged) Changed?.Invoke();
    }

    /// <summary>
    /// Replace the obtained set with exactly the spells named in
    /// <paramref name="names"/> (the authoritative snapshot from a
    /// <c>spells</c> / <c>pow</c> block). Names that don't resolve to an
    /// available spell are ignored. No-ops (no event) when the resolved set
    /// matches the current one.
    /// </summary>
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

    /// <summary>
    /// Mark a single spell obtained by its full <c>Name</c> — the
    /// learn-scroll signal ("…learn the spell <i>harm</i>."). Returns the
    /// resolved spell (so a caller can surface it), or <c>null</c> when the
    /// name doesn't match an available spell. Fires <see cref="Changed"/>
    /// only when the spell was newly added.
    /// </summary>
    public KnownSpell? MarkObtainedByName(string name)
    {
        if (FindAvailableByName(name) is not { } match) return null;
        if (_obtained.Add(match.Number)) Changed?.Invoke();
        return match;
    }

    /// <summary>Drop every obtained spell (reroll / "you have no spells").</summary>
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
