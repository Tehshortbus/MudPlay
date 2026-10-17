using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Calculators;
using MudPlay.Game.Spells;

namespace MudPlay.ViewModels;

// Modeless Spell Book window VM. Renders the active class's full learnable
// list (SpellbookState.ClassSpells) with an obtained checkmark and
// level-scaled effect / mana figures, rebuilding whenever the book's class,
// level, or obtained set changes.
//
// The book itself is the single source of truth (fed by the live spells /
// pow list and the learn-scroll signal); this VM is a pure projection. The
// class name shown in the header comes from an optional provider (the loaded
// profile's last stat snapshot) —
// SpellbookState only knows the numeric class.
public sealed partial class SpellBookViewModel : ObservableObject, IDisposable
{
    private readonly SpellbookState _book;
    private readonly Func<string?>? _classNameProvider;
    private readonly Func<int>? _spellcastingProvider;
    private readonly Func<bool>? _isParadigmProvider;
    private readonly Func<int>? _spellDamageBonusProvider;
    private IReadOnlyList<ClassCastItem> _allCastItems = System.Array.Empty<ClassCastItem>();
    private bool _disposed;

    public SpellBookViewModel(
        SpellbookState book,
        Func<string?>? classNameProvider = null,
        Func<int>? spellcastingProvider = null,
        Func<bool>? isParadigmProvider = null,
        Func<int>? spellDamageBonusProvider = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        _book = book;
        _classNameProvider = classNameProvider;
        // Live Spellcasting stat, read fresh on each Rebuild — feeds the per-row
        // cast-success ("Success %") column. Null when unwired (rows then show "—").
        _spellcastingProvider = spellcastingProvider;
        // Paradigm lifts the Success % cap from 98 to 100 (SpellCastChance.Cap).
        _isParadigmProvider = isParadigmProvider;
        // The character's AlterSpDmg %, so the Effect column's damage / heal
        // figures are what they actually cast for. Null when unwired (no bonus).
        _spellDamageBonusProvider = spellDamageBonusProvider;
        _book.Changed += OnBookChanged;
        _allCastItems = _book.GetSpellBookCastItems();
        Rebuild();
    }

    // The rendered, filtered spell rows.
    public ObservableCollection<SpellBookRowViewModel> Rows { get; } = new();

    // The five category tabs, in display order — bound as the tab strip's
    // ItemsSource so the view never hand-lists them.
    public IReadOnlyList<SpellBookCategory> Categories { get; } =
        Enum.GetValues<SpellBookCategory>();

    // Which tab is active. All (the default) shows the unfiltered list; the
    // other four re-filter the same Rows via SpellBookCategoryClassifier — a
    // spell can satisfy more than one, so switching tabs doesn't move spells
    // between fixed buckets, it just re-tests the same list.
    [ObservableProperty] private SpellBookCategory _selectedCategory = SpellBookCategory.All;

    // The Items.Number of the item that TEACHES this spell, or 0 when none does
    // (trainer-taught spells — see TeachingNpcNumberFor). The window's double-click
    // opens that item's record. Pure — the view owns the actual open so this VM
    // stays a testable projection.
    public int TeachingItemNumberFor(SpellBookRowViewModel? row)
        => row is null ? 0 : _book.GetTeachingItemNumber(row.Number);

    // The Monsters.Number of the NPC that teaches this spell, or 0 when it isn't
    // NPC-taught. The window's double-click opens the NPC's record when the spell has
    // no teaching item (a trainer-only spell like a Paladin's divine disfavour).
    public int TeachingNpcNumberFor(SpellBookRowViewModel? row)
        => row is null ? 0 : _book.GetTeachingNpcNumber(row.Number);

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

    // Alignment boxes: a spell shows when a character of any ticked alignment could
    // use it. All three start ticked, so the whole class list shows — the client's
    // own alignment reading can lag, and hiding rows on it made learned spells look
    // missing (report paradigm-20261003-215442). An ungated spell fits all three.
    [ObservableProperty] private bool _showGood = true;
    [ObservableProperty] private bool _showNeutral = true;
    [ObservableProperty] private bool _showEvil = true;

    // Window title-strip header: class + level.
    public string HeaderText
    {
        get
        {
            if (_book.ClassSpells.Count == 0)
                return "Spell Book — no spells for this class";
            string? className = _classNameProvider?.Invoke();
            string classPart = string.IsNullOrWhiteSpace(className) ? "Spell Book" : className.Trim();
            return _book.Level > 0 ? $"{classPart} — Level {_book.Level}" : classPart;
        }
    }

    // Header hint that spells out how the Success % column is computed — the
    // formula in variable form (the per-row tooltip fills in the actual numbers),
    // with the realm's cap (SpellCastChance.Cap: Stock 98, Kai 100; Paradigm 100).
    // The value it yields is the caster's chance to land the spell, so it reads
    // "Success % = …", not "Difficulty = …". When the caster has a spell-damage
    // bonus it also says the Effect column's damage figures include it.
    public string SuccessFormulaText
    {
        get
        {
            if (_book.ClassSpells.Count == 0) return string.Empty;
            bool paradigm = _isParadigmProvider?.Invoke() ?? false;
            string text = "Success % = your Spellcasting + the spell's difficulty  "
                + (paradigm ? "(capped at 100%)" : "(capped at 98%, 100% for Kai)");
            int bonus = _spellDamageBonusProvider?.Invoke() ?? 0;
            return bonus != 0
                ? text + string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"  ·  damage includes your {bonus:+0;-0}% spell damage")
                : text;
        }
    }

    // Footer summary: obtained-of-total + filtered count.
    public string StatusText
    {
        get
        {
            int total = _book.ClassSpells.Count;
            if (total == 0) return "This class has no spell book.";
            string shown = Rows.Count == total ? string.Empty : $"  ·  showing {Rows.Count}";
            return $"{_book.ObtainedCount} of {total} learned{shown}";
        }
    }

    partial void OnSearchTextChanged(string value) => Rebuild();
    partial void OnShowObtainedOnlyChanged(bool value) => Rebuild();
    partial void OnShowAllSpellsChanged(bool value) => Rebuild();
    partial void OnShowGoodChanged(bool value) => Rebuild();
    partial void OnShowNeutralChanged(bool value) => Rebuild();
    partial void OnShowEvilChanged(bool value) => Rebuild();

    private bool FitsATickedAlignment(in KnownSpell spell)
        => (ShowGood && BuffClassifier.IsAlignmentEligible(spell.Formula, AlignmentBucket.Good))
        || (ShowNeutral && BuffClassifier.IsAlignmentEligible(spell.Formula, AlignmentBucket.Neutral))
        || (ShowEvil && BuffClassifier.IsAlignmentEligible(spell.Formula, AlignmentBucket.Evil));
    partial void OnSelectedCategoryChanged(SpellBookCategory value) => Rebuild();

    private void OnBookChanged()
    {
        // The class can change (reroll) — re-pull the cast-item set, which only
        // depends on class, not the per-keystroke search filter.
        _allCastItems = _book.GetSpellBookCastItems();
        Rebuild();
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(SuccessFormulaText));
    }

    private void Rebuild()
    {
        // Number → formula map so chained end-cast (Abil 151) spells in the
        // same class list resolve to a real follow-up rather than dropping.
        Dictionary<int, SpellFormulaInput> byNumber = new();
        foreach (KnownSpell s in _book.ClassSpells) byNumber[s.Number] = s.Formula;
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

        // Trainer learn-level gates for this class (TBInfo). A spell taught by an NPC
        // often can't be learned until a level HIGHER than its ReqLevel (a Paladin's
        // divine disfavour gates at 50, not 19); use that as the effective unlock level
        // so the Lvl column and the level gate reflect when THIS class can learn it.
        IReadOnlyDictionary<int, int> teachLevels = _book.GetTeachLevels();

        int spellcasting = _spellcastingProvider?.Invoke() ?? 0;
        bool isParadigm = _isParadigmProvider?.Invoke() ?? false;
        int spellDamageBonus = _spellDamageBonusProvider?.Invoke() ?? 0;

        string filter = SearchText.Trim();
        Rows.Clear();
        foreach (KnownSpell spell in _book.ClassSpells)
        {
            bool obtained = _book.IsObtained(spell.Number);
            if (ShowObtainedOnly && !obtained) continue;
            int teachLevel = teachLevels.TryGetValue(spell.Number, out int tl) ? tl : 0;
            int effectiveLevel = System.Math.Max(spell.ReqLevel, teachLevel);
            // Level gate (default): hide above-level spells unless Show-all is on.
            // Skipped when the level is unknown (0) so a fresh book isn't empty.
            if (!ShowAllSpells && _book.Level > 0 && effectiveLevel > _book.Level) continue;
            if (!FitsATickedAlignment(spell)) continue;
            if (filter.Length > 0 && !Matches(spell, filter)) continue;
            if (!SpellBookCategoryClassifier.Matches(SelectedCategory, spell, _book.Level, ResolveChain)) continue;
            Rows.Add(new SpellBookRowViewModel(
                spell, obtained, _book.Level, ResolveChain, _book.ResolveSpellName,
                ResolveTextblockCasts, teachLevel, spellcasting, isParadigm, spellDamageBonus));
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
