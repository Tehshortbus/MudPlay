using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// A reorderable priority list rendered as fixed rows with Up / Down arrows.
/// The list is always kept in priority order top-to-bottom, so each row's
/// rank is its 1-based position — guaranteeing a contiguous permutation with
/// no duplicate or skipped numbers. Up moves a row earlier (lower number),
/// Down moves it later; the displaced neighbour swaps positions. Shared by
/// every Settings tab that orders a fixed set of categories (Combat priority,
/// Spell-type priority).
/// </summary>
public sealed partial class PriorityRankingViewModel : ObservableObject
{
    private readonly Action? _onReordered;

    /// <param name="onReordered">Invoked after any successful move so the
    /// host VM can mark its dirty flag.</param>
    public PriorityRankingViewModel(Action? onReordered = null) => _onReordered = onReordered;

    /// <summary>The ordered rows. Position 0 is the highest priority.</summary>
    public ObservableCollection<PriorityItemViewModel> Items { get; } = new();

    /// <summary>
    /// Rebuild the list from <paramref name="definitions"/>, ordered by the
    /// rank each key currently holds (via <paramref name="rankOf"/>). Ties or
    /// gaps in the stored ranks are tolerated — the stable sort keeps
    /// definition order for equal ranks and <see cref="Renumber"/> collapses
    /// the result back to a clean 1..N sequence.
    /// </summary>
    public void Load(
        IReadOnlyList<(string Key, string Label, string? Tip)> definitions,
        Func<string, int> rankOf)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(rankOf);

        Items.Clear();
        foreach ((string Key, string Label, string? Tip) def in
                 definitions.OrderBy(d => rankOf(d.Key)))
            Items.Add(new PriorityItemViewModel(def.Key, def.Label, def.Tip));

        Renumber();
    }

    /// <summary>The 1-based rank the given key currently holds, or 0 if the
    /// key isn't in the list. The host VM calls this on save.</summary>
    public int RankOf(string key)
    {
        for (int i = 0; i < Items.Count; i++)
            if (Items[i].Key == key) return i + 1;
        return 0;
    }

    [RelayCommand]
    private void MoveUp(PriorityItemViewModel item) => Move(item, -1);

    [RelayCommand]
    private void MoveDown(PriorityItemViewModel item) => Move(item, +1);

    private void Move(PriorityItemViewModel item, int delta)
    {
        int from = Items.IndexOf(item);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= Items.Count) return;

        Items.Move(from, to);
        Renumber();
        _onReordered?.Invoke();
    }

    private void Renumber()
    {
        for (int i = 0; i < Items.Count; i++)
        {
            PriorityItemViewModel row = Items[i];
            row.Rank = i + 1;
            row.CanMoveUp = i > 0;
            row.CanMoveDown = i < Items.Count - 1;
        }
    }
}
