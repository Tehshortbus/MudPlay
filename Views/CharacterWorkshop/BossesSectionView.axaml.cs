using System;
using System.Collections;
using System.ComponentModel;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.ViewModels.CharacterWorkshop;

namespace MudPlay.Views.CharacterWorkshop;

public partial class BossesSectionView : UserControl
{
    // Column indices for the three early-window columns (see BossesSectionView.axaml).
    // Shifted +1 by the "Grab All" column inserted after "Stop before".
    private const int Early1 = 5, Early2 = 6, Early3 = 7;

    private BossesSectionViewModel? _vm;
    private string? _sortPath;
    private ListSortDirection _sortDir = ListSortDirection.Ascending;

    public BossesSectionView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // The tab's View is swapped into a ContentControl, so it re-attaches every time
        // the Bosses section is opened — re-apply the default sort there so a fresh open
        // always surfaces the running timers instead of a name-ordered list that reads
        // as empty.
        AttachedToVisualTree += (_, _) => ApplyDefaultSort();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as BossesSectionViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        ApplyRealmColumns();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BossesSectionViewModel.IsParadigmRealm))
            ApplyRealmColumns();
    }

    // Paradigm shows three early-window columns (5 / 10 / 20% off); Stock collapses
    // them to a single 87.5% column.
    private void ApplyRealmColumns()
    {
        // The x:Name field is null when this runs off DataContextChanged: the view
        // is freshly built and not yet attached, so the generated field isn't
        // assigned (the control IS in the name scope). Reach it through the name
        // scope instead of dereferencing the raw field — same idiom as
        // CalculatorsSectionView. Dereferencing the null field here threw out of the
        // View getter and left the whole tab blank.
        DataGrid? grid = BossGrid ?? this.FindControl<DataGrid>("BossGrid");
        if (grid is null || grid.Columns.Count <= Early3) return;
        bool para = _vm?.IsParadigmRealm ?? true;
        IReadOnlyList<string> labels = BossTimerMath.EarlyColumnLabels(para ? RealmType.ParaMud : RealmType.Stock);
        grid.Columns[Early1].Header = labels[0];
        grid.Columns[Early2].IsVisible = para;
        grid.Columns[Early3].IsVisible = para;
        if (para)
        {
            grid.Columns[Early2].Header = labels[1];
            grid.Columns[Early3].Header = labels[2];
        }
    }

    // Sort in three fixed groups regardless of direction — cleanup spawns
    // (DEAD/ALIVE) first, then active (counting) respawns, then unset / expired rows
    // — with the toggled direction ordering only within each group. The built-in
    // single-key sort can't express that (it would float the inactive sentinel to
    // the top when descending). The timer columns group by their remaining-time key;
    // the Boss (name) and Respawn (regen) columns reuse the SAME active/idle grouping
    // but order within each group by name / respawn length, so a name or respawn sort
    // still floats your running timers to the top. Every other column falls through.
    private void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (_vm is null || ComparerFor(e.Column.SortMemberPath) is not { } cmp) return;
        string path = e.Column.SortMemberPath!;
        e.Handled = true;
        _sortDir = _sortPath == path && _sortDir == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        ApplySort(path, cmp);
    }

    // Default sort applied on every open: the 100% countdown, active timers first. Reset
    // the toggle state so a subsequent header click toggles from this default.
    private void ApplyDefaultSort()
    {
        if (_vm is null || ComparerFor("FullSortKey") is not { } cmp) return;
        _sortDir = ListSortDirection.Ascending;
        ApplySort("FullSortKey", cmp);
    }

    // Resolve a column's fixed-group + within-group comparison, or null when it isn't a
    // sortable column. Shared by the user-click sort and the default open sort.
    private static (Func<BossRowViewModel, int> Group, Comparison<BossRowViewModel> Within)? ComparerFor(string? path)
        => path switch
        {
            "FullSortKey"       => TimerSort(static r => r.FullSortKey),
            "Early1SortKey"     => TimerSort(static r => r.Early1SortKey),
            "Early2SortKey"     => TimerSort(static r => r.Early2SortKey),
            "Early3SortKey"     => TimerSort(static r => r.Early3SortKey),
            "Name"              => (StatusGroup, static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)),
            "RespawnSortKey"    => (StatusGroup, static (a, b) => a.RespawnSortKey.CompareTo(b.RespawnSortKey)),
            // Recorded kills first (a long.MinValue key = never killed sorts last), by
            // kill time within the group — descending floats the most recent to the top.
            "LastKilledSortKey" => (static r => r.LastKilledSortKey == long.MinValue ? 1 : 0,
                                    static (a, b) => a.LastKilledSortKey.CompareTo(b.LastKilledSortKey)),
            _ => null,
        };

    private void ApplySort(string path, (Func<BossRowViewModel, int> Group, Comparison<BossRowViewModel> Within) cmp)
    {
        if (_vm is null) return;
        _sortPath = path;
        _vm.Rows.SortDescriptions.Clear();
        _vm.Rows.SortDescriptions.Add(DataGridSortDescription.FromComparer(
            new GroupedComparer(cmp.Group, cmp.Within, _sortDir == ListSortDirection.Descending)));
    }

    // Cleanup spawns (0), active (counting) timers (1), unset / expired (2) — derived
    // from the clicked timer column's key, which reads long.MaxValue when that window
    // isn't counting. Within-group order is by that same key.
    private static (Func<BossRowViewModel, int>, Comparison<BossRowViewModel>) TimerSort(Func<BossRowViewModel, long> key)
        => (r => r.IsCleanup ? 0 : key(r) == long.MaxValue ? 2 : 1,
            (a, b) => key(a).CompareTo(key(b)));

    // Cleanup, then bosses with a running timer, then idle ones — independent of any
    // per-column key, so the name / respawn sorts can share the timer grouping.
    private static int StatusGroup(BossRowViewModel r) => r.IsCleanup ? 0 : r.IsActive ? 1 : 2;

    // Sorts rows into fixed status groups (never reversed), ordering within each group
    // by the supplied comparison; the toggled direction flips only the within-group
    // order, so the active/idle split stays put whichever way you sort.
    private sealed class GroupedComparer : IComparer
    {
        private readonly Func<BossRowViewModel, int> _group;
        private readonly Comparison<BossRowViewModel> _within;
        private readonly bool _descending;

        public GroupedComparer(Func<BossRowViewModel, int> group, Comparison<BossRowViewModel> within, bool descending)
        {
            _group = group;
            _within = within;
            _descending = descending;
        }

        public int Compare(object? x, object? y)
        {
            if (x is not BossRowViewModel a || y is not BossRowViewModel b) return 0;
            int ga = _group(a), gb = _group(b);
            if (ga != gb) return ga.CompareTo(gb);     // cleanup, then active, then inactive
            int c = _within(a, b);
            return _descending ? -c : c;
        }
    }
}
