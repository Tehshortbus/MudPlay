using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.Spells;

namespace FujinTerm.ViewModels;

// Modeless Spell Book window VM. Renders the active class's full learnable
// list (SpellbookState.Available) with an obtained checkmark and
// level-scaled effect / mana figures, rebuilding whenever the book's class,
// level, or obtained set changes.
//
// The book itself is the single source of truth (fed by the live spells /
// pow list and the learn-scroll signal); this VM is a pure projection. The
// class name shown in the header comes from an optional provider (the loaded
// profile's last stat snapshot in production, null in tests) —
// SpellbookState only knows the numeric class.
public sealed partial class SpellBookViewModel : ObservableObject, IDisposable
{
    private readonly SpellbookState _book;
    private readonly Func<string?>? _classNameProvider;
    private IReadOnlyList<ClassCastItem> _allCastItems = System.Array.Empty<ClassCastItem>();
    private bool _disposed;

    public SpellBookViewModel(SpellbookState book, Func<string?>? classNameProvider = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        _book = book;
        _classNameProvider = classNameProvider;
        _book.Changed += OnBookChanged;
        _allCastItems = _book.GetCastItems();
        Rebuild();
    }

    // The rendered, filtered spell rows.
    public ObservableCollection<SpellBookRowViewModel> Rows { get; } = new();

    // The class's cast-on-use items (wands / scrolls / potions), filtered by
    // the search box. Rendered in a section below the spell grid. Empty for a
    // class with no usable cast items.
    public ObservableCollection<SpellBookItemRowViewModel> CastItems { get; } = new();

    // True when the cast-item section has anything to show (drives its visibility).
    public bool HasCastItems => CastItems.Count > 0;

    // Header for the cast-item section, with the shown count.
    public string CastItemsHeader => $"Cast-on-use items ({CastItems.Count})";

    // Free-text filter over Short code + Name (case-insensitive).
    [ObservableProperty] private string _searchText = string.Empty;

    // When true, hide spells the character hasn't obtained yet.
    [ObservableProperty] private bool _showObtainedOnly;

    // When false (default), the list is level-gated — only spells the
    // character is high enough to have (ReqLevel <= Level). When true, the
    // full class list shows regardless of level, so the Lvl column doubles as
    // an unlock-projection. The gate is skipped while the level is unknown
    // (0).
    [ObservableProperty] private bool _showAllSpells;

    // Window title-strip header: class + level.
    public string HeaderText
    {
        get
        {
            if (_book.Available.Count == 0)
                return "Spell Book — no spells for this class";
            string? className = _classNameProvider?.Invoke();
            string classPart = string.IsNullOrWhiteSpace(className) ? "Spell Book" : className.Trim();
            return _book.Level > 0 ? $"{classPart} — Level {_book.Level}" : classPart;
        }
    }

    // Footer summary: obtained-of-total + filtered count.
    public string StatusText
    {
        get
        {
            int total = _book.Available.Count;
            if (total == 0) return "This class has no spell book.";
            string shown = Rows.Count == total ? string.Empty : $"  ·  showing {Rows.Count}";
            return $"{_book.ObtainedCount} of {total} learned{shown}";
        }
    }

    partial void OnSearchTextChanged(string value) => Rebuild();
    partial void OnShowObtainedOnlyChanged(bool value) => Rebuild();
    partial void OnShowAllSpellsChanged(bool value) => Rebuild();

    private void OnBookChanged()
    {
        // The class can change (reroll) — re-pull the cast-item set, which only
        // depends on class, not the per-keystroke search filter.
        _allCastItems = _book.GetCastItems();
        Rebuild();
        OnPropertyChanged(nameof(HeaderText));
    }

    private void Rebuild()
    {
        // Number → formula map so chained end-cast (Abil 151) spells in the
        // same class list resolve to a real follow-up rather than dropping.
        Dictionary<int, SpellFormulaInput> byNumber = new();
        foreach (KnownSpell s in _book.Available) byNumber[s.Number] = s.Formula;
        SpellFormulaInput? ResolveChain(int number)
            => byNumber.TryGetValue(number, out SpellFormulaInput f) ? f : null;

        // Textblock → cast-spell reverse index, built lazily on the first
        // Abil-148 row so a book with no TextBlock spells pays nothing, and a
        // book with several reuses the single full-table scan.
        IReadOnlyDictionary<int, IReadOnlyList<KnownSpell>>? castIndex = null;
        IReadOnlyList<KnownSpell> ResolveTextblockCasts(int textblock)
        {
            castIndex ??= _book.BuildCastByTextblockIndex();
            return castIndex.TryGetValue(textblock, out IReadOnlyList<KnownSpell>? list)
                ? list
                : System.Array.Empty<KnownSpell>();
        }

        string filter = SearchText.Trim();
        Rows.Clear();
        foreach (KnownSpell spell in _book.Available)
        {
            bool obtained = _book.IsObtained(spell.Number);
            if (ShowObtainedOnly && !obtained) continue;
            // Level gate (default): hide above-level spells unless Show-all is on.
            // Skipped when the level is unknown (0) so a fresh book isn't empty.
            if (!ShowAllSpells && _book.Level > 0 && spell.ReqLevel > _book.Level) continue;
            if (filter.Length > 0 && !Matches(spell, filter)) continue;
            Rows.Add(new SpellBookRowViewModel(
                spell, obtained, _book.Level, ResolveChain, _book.ResolveSpellName, ResolveTextblockCasts));
        }

        // Cast-on-use items: a separate section, filtered by the same search
        // box (item name or cast-spell name) but unaffected by the spell-only
        // level gate / obtained filter. Only class-specific sources are listed —
        // universal wands / scrolls anyone can use would bury the class's own
        // gear. (The casting automation still consumes the full usable set from
        // the book; this narrowing is display-only.)
        CastItems.Clear();
        // Ordered by the item's level requirement, lowest first, so the caster
        // reads the earliest-usable cast source at the top; item name breaks
        // ties (0-level items sort ahead of any gated one).
        IEnumerable<ClassCastItem> visibleCastItems = _allCastItems
            .Where(item => item.ClassRestricted)
            .Where(item => filter.Length == 0
                || item.ItemName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || item.SpellName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.MinLevel)
            .ThenBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase);
        foreach (ClassCastItem item in visibleCastItems)
            CastItems.Add(new SpellBookItemRowViewModel(item));

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasCastItems));
        OnPropertyChanged(nameof(CastItemsHeader));
    }

    private static bool Matches(in KnownSpell spell, string filter)
        => spell.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || spell.Short.Contains(filter, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _book.Changed -= OnBookChanged;
    }
}
