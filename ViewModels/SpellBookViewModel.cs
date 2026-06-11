using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.Spells;

namespace FujinTerm.ViewModels;

/// <summary>
/// Modeless Spell Book window VM. Renders the active class's full learnable
/// list (<see cref="SpellbookState.Available"/>) with an obtained checkmark
/// and level-scaled effect / mana figures, rebuilding whenever the book's
/// class, level, or obtained set changes.
/// </summary>
/// <remarks>
/// The book itself is the single source of truth (fed by the live
/// <c>spells</c> / <c>pow</c> list and the learn-scroll signal); this VM is a
/// pure projection. The class name shown in the header comes from an optional
/// provider (the loaded profile's last <c>stat</c> snapshot in production,
/// <c>null</c> in tests) — <see cref="SpellbookState"/> only knows the numeric
/// class.
/// </remarks>
public sealed partial class SpellBookViewModel : ObservableObject, IDisposable
{
    private readonly SpellbookState _book;
    private readonly Func<string?>? _classNameProvider;
    private bool _disposed;

    public SpellBookViewModel(SpellbookState book, Func<string?>? classNameProvider = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        _book = book;
        _classNameProvider = classNameProvider;
        _book.Changed += OnBookChanged;
        Rebuild();
    }

    /// <summary>The rendered, filtered spell rows.</summary>
    public ObservableCollection<SpellBookRowViewModel> Rows { get; } = new();

    /// <summary>Free-text filter over Short code + Name (case-insensitive).</summary>
    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>When true, hide spells the character hasn't obtained yet.</summary>
    [ObservableProperty] private bool _showObtainedOnly;

    /// <summary>Window title-strip header: class + level.</summary>
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

    /// <summary>Footer summary: obtained-of-total + filtered count.</summary>
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

    private void OnBookChanged()
    {
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

        string filter = SearchText.Trim();
        Rows.Clear();
        foreach (KnownSpell spell in _book.Available)
        {
            bool obtained = _book.IsObtained(spell.Number);
            if (ShowObtainedOnly && !obtained) continue;
            if (filter.Length > 0 && !Matches(spell, filter)) continue;
            Rows.Add(new SpellBookRowViewModel(spell, obtained, _book.Level, ResolveChain));
        }

        OnPropertyChanged(nameof(StatusText));
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
