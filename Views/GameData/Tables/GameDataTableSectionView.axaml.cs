using System.Collections.Generic;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
using FujinTerm.ViewModels.GameData.Tables;

namespace FujinTerm.Views.GameData.Tables;

// Populates the DataGrid's columns from the bound view-model's Columns list —
// each VM supplies a different ordered list, so the columns can't be authored in
// XAML and must be rebuilt when the DataContext is wired up.
//
// No hand-written InitializeComponent here: the Avalonia name generator owns
// that method (per AvaloniaNameGeneratorBehavior = InitializeComponent) so the
// x:Name="RowsGrid" field gets populated. Overriding it manually short-circuits
// the generator and leaves x:Name fields null — which is how this view first
// shipped and crashed every section open with an NRE on RowsGrid.Columns.
public partial class GameDataTableSectionView : UserControl
{
    private bool _columnsBuilt;

    // ----- Sort preservation across VM reloads ---------------------------
    // A VM reload reassigns the bound rows collection wholesale (one
    // PropertyChanged instead of N CollectionChanged — deliberate for
    // 27k-row tables), which makes the DataGrid throw away its current
    // CollectionView and build a fresh, unsorted one. That silently drops
    // whatever column sort the user picked (e.g. editing a player then
    // saving snapped the Players grid back to default order). We snapshot
    // the active sort on every sort action and reapply it whenever the
    // ItemsSource is swapped.
    private readonly List<DataGridSortDescription> _sortSnapshot = new();

    // Subscribed to the bound VM's ScrollToRowRequested so a cross-section
    // navigation (Shops double-click → Rooms tab + room) actually brings the
    // target row on-screen. Tracked here so re-binding swaps cleanly.
    private GameDataTableSectionViewModel? _scrollSubscriptionTarget;

    public GameDataTableSectionView()
    {
        InitializeComponent();
        // Either trigger can fire first depending on layout timing;
        // guard via _columnsBuilt so the second is a no-op.
        // A DataContext swap means a different section — drop any captured
        // sort so it can't be reapplied to an unrelated table's columns.
        DataContextChanged   += (_, _) => { _sortSnapshot.Clear(); TryBuildColumns(); WireAddRemoveButtons(); WireScrollHook(); };
        AttachedToVisualTree += (_, _) => { TryBuildColumns(); WireAddRemoveButtons(); WireScrollHook(); };

        // Double-click any row → invoke the section's OpenEditCommand
        // with the row as the argument. Sections that don't expose an
        // editor (e.g. read-only Info tab) leave the command null and
        // the double-click is a no-op.
        RowsGrid.DoubleTapped += (_, _) =>
        {
            if (DataContext is IEditableTableSectionViewModel editable
                && RowsGrid.SelectedItem is GameDataRow row
                && editable.OpenEditCommand is { } cmd
                && cmd.CanExecute(row))
            {
                cmd.Execute(row);
            }
        };

        // Sync multi-selection from the DataGrid into the VM's
        // SelectedRows collection so Remove can act on every highlighted
        // row, not just the keyboard-focused one. Avalonia exposes
        // SelectedItems as a non-bindable IList — has to be wired
        // imperatively.
        RowsGrid.SelectionChanged += (_, _) =>
        {
            if (DataContext is not GameDataTableSectionViewModel vm) return;
            vm.SelectedRows.Clear();
            foreach (object? item in RowsGrid.SelectedItems)
            {
                if (item is GameDataRow row) vm.SelectedRows.Add(row);
            }
        };

        // Deferred so the read runs AFTER the DataGrid has applied the
        // sort the user just requested (Sorting fires pre-apply).
        RowsGrid.Sorting += (_, _) =>
            Dispatcher.UIThread.Post(SnapshotSort, DispatcherPriority.Background);

        // ItemsSource swap == a VM reload built a fresh CollectionView;
        // restore the user's sort onto it once it's live.
        RowsGrid.PropertyChanged += (_, e) =>
        {
            if (e.Property == DataGrid.ItemsSourceProperty)
                Dispatcher.UIThread.Post(RestoreSort, DispatcherPriority.Background);
        };
    }

    // Capture the grid's live sort so a reload can restore it.
    private void SnapshotSort()
    {
        _sortSnapshot.Clear();
        if (RowsGrid.CollectionView is { } view)
            foreach (DataGridSortDescription description in view.SortDescriptions)
                _sortSnapshot.Add(description);
    }

    // Reapply the snapshotted sort onto the freshly-built CollectionView after a reload.
    private void RestoreSort()
    {
        if (_sortSnapshot.Count == 0) return;
        if (RowsGrid.CollectionView is not { } view) return;
        // The user re-sorted after the last snapshot (or the new view is
        // already sorted) — don't stomp it.
        if (view.SortDescriptions.Count > 0) return;

        // Restoring the descriptions reorders the rows; the DataGrid keys
        // the header arrow off the same SortDescriptions collection, so the
        // glyph follows without touching per-column state.
        foreach (DataGridSortDescription description in _sortSnapshot)
            view.SortDescriptions.Add(description);
    }

    // Wire ScrollToRowRequested to DataGrid.ScrollIntoView. Deferred via the
    // dispatcher so the call runs AFTER the DataGrid has materialised the row
    // container for the new SelectedItem — calling ScrollIntoView in the same
    // frame as the SelectedItem change can no-op when the container doesn't
    // exist yet (virtualised DataGrid).
    private void WireScrollHook()
    {
        // Unhook the previous VM (if any) before binding the new one;
        // the View can be re-DataContext'd when its host section is
        // reused, and a stale subscription would scroll the wrong grid.
        if (_scrollSubscriptionTarget is { } prev)
            prev.ScrollToRowRequested -= OnScrollToRowRequested;
        _scrollSubscriptionTarget = null;

        if (DataContext is not GameDataTableSectionViewModel vm) return;
        vm.ScrollToRowRequested += OnScrollToRowRequested;
        _scrollSubscriptionTarget = vm;
    }

    private void OnScrollToRowRequested(GameDataRow row)
    {
        // Defer past the current dispatcher frame: the SelectedItem source
        // change just landed, and the DataGrid still needs to realise the
        // row container before ScrollIntoView can locate it.
        Dispatcher.UIThread.Post(() =>
        {
            try { RowsGrid.ScrollIntoView(row, null); }
            catch { /* virtualised DataGrid can throw if row isn't materialised yet — harmless */ }
        }, DispatcherPriority.Background);
    }

    // Conditionally surface the Add / Remove buttons next to the search filter
    // when the section exposes those commands. Sections that don't (MDB-derived
    // read-only tabs) leave both null and the buttons stay hidden. Command wired
    // imperatively rather than via XAML binding because
    // IEditableTableSectionViewModel's optional members aren't surfaced by
    // compiled bindings.
    private void WireAddRemoveButtons()
    {
        if (DataContext is not IEditableTableSectionViewModel editable) return;

        if (editable.AddCommand is { } add)
        {
            AddButton.Command   = add;
            AddButton.IsVisible = true;
        }
        if (editable.RemoveCommand is { } remove)
        {
            RemoveButton.Command   = remove;
            RemoveButton.IsVisible = true;
        }
    }

    private void TryBuildColumns()
    {
        if (_columnsBuilt) return;
        if (DataContext is not GameDataTableSectionViewModel vm) return;

        RowsGrid.Columns.Clear();
        int index = 0;
        foreach (string column in vm.Columns)
        {
            // Bind each data column to its positional cell on the row —
            // GameDataRow.Cells is ordered to match Columns, so the
            // indexer round-trip is stable. CustomSortComparer handles
            // numeric columns properly (cell values are strings, so the
            // DataGrid's default sort would treat EXP as
            // "0, 1, 10, 100, 11, 2…").
            // Friendly header when the VM maps one (e.g. "AC" for the
            // ArmourClass key); raw column name otherwise.
            string header = vm.ColumnHeaders is { } headers
                && headers.TryGetValue(column, out string? friendly)
                    ? friendly
                    : column;
            RowsGrid.Columns.Add(new DataGridTextColumn
            {
                Header             = header,
                Binding            = new Binding($"Cells[{index}].Value"),
                Width              = DataGridLength.Auto,
                CustomSortComparer = new NumericAwareCellComparer(index),
            });
            index++;
        }
        // Trailing virtual "Use" column — shows which tier (Def / Glob /
        // BBS / Char) owns the row's current values. Bound to the
        // GameDataRow.UseLabel computed property rather than a cell.
        // Skipped for engine-backed sections (Macros / Triggers / Aliases
        // / Players) where every row lives at one tier and the badge
        // would always read the same.
        if (vm.ShowUseColumn)
        {
            RowsGrid.Columns.Add(new DataGridTextColumn
            {
                Header  = GameDataTableSectionViewModel.UseColumnName,
                Binding = new Binding(nameof(GameDataRow.UseLabel)),
                Width   = DataGridLength.Auto,
            });
        }
        _columnsBuilt = true;
    }
}
