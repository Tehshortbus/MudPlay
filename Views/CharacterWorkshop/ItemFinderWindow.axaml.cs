using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using FujinTerm.Game.Inventory;
using FujinTerm.Services;
using FujinTerm.ViewModels.CharacterWorkshop;

namespace FujinTerm.Views.CharacterWorkshop;

public partial class ItemFinderWindow : Window
{
    private ItemFinderViewModel? _vm;

    // Columns that read best low-to-high on the first click: the name/type text
    // and the slot order. Every other column here is a numeric stat where the
    // useful answer is "which item has the most", so those flip to descending
    // first (see OnGridSorting).
    private static readonly HashSet<string> _ascendingFirstPaths = new(StringComparer.Ordinal)
    {
        "Name", "TypeLabel", "SlotOrder",
    };

    public ItemFinderWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.VisibleColumnsChanged -= ApplyColumnVisibility;
        _vm = DataContext as ItemFinderViewModel;
        if (_vm is null) return;
        _vm.VisibleColumnsChanged += ApplyColumnVisibility;
        ApplyColumnVisibility();
    }

    // Show a tagged stat column only while the VM reports some visible row carries a
    // value for it, so a narrowed result set collapses its all-blank columns and the
    // grid stays no wider than the data warrants. Untagged columns (Slot / Name) are
    // the always-on anchors — left untouched.
    private void ApplyColumnVisibility()
    {
        if (_vm is null) return;
        foreach (DataGridColumn col in ItemsGrid.Columns)
            if (col.Tag is string key)
                col.IsVisible = _vm.IsColumnVisible(key);
    }

    // Double-click a result → jump to that item's Game Data record. A double-tap
    // also selects the row, so SelectedItem is the double-clicked entry. The finder
    // stays open (modeless) alongside the browser.
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ItemsGrid.SelectedItem is ItemFinderEntry entry && entry.Number > 0)
            AppServices.Current.OpenItemGameData(entry.Number);
    }

    // Take over sorting so numeric stat columns lead with their biggest values.
    // The DataGrid's built-in default is ascending-first, which buries the useful
    // items (the highest damage / AC / bonus) at the bottom and puts zeros and
    // negatives on top. We drive the CollectionView's SortDescriptions directly —
    // the header arrow follows that collection, so no per-column state to poke.
    private void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (ItemsGrid.CollectionView is not { } view) return;
        string? path = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(path)) return;

        e.Handled = true;

        ListSortDirection firstClick = _ascendingFirstPaths.Contains(path)
            ? ListSortDirection.Ascending
            : ListSortDirection.Descending;

        DataGridSortDescription? existing = null;
        foreach (DataGridSortDescription d in view.SortDescriptions)
        {
            if (d.HasPropertyPath && d.PropertyPath == path) { existing = d; break; }
        }

        ListSortDirection next = existing is null
            ? firstClick
            : Opposite(existing.Direction);

        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(DataGridSortDescription.FromPath(path, next, (System.Globalization.CultureInfo?)null));

        static ListSortDirection Opposite(ListSortDirection d) =>
            d == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
    }
}
