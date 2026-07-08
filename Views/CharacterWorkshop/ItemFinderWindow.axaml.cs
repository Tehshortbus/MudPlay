using System;
using Avalonia.Controls;
using Avalonia.Input;
using FujinTerm.Game.Inventory;
using FujinTerm.Services;
using FujinTerm.ViewModels.CharacterWorkshop;

namespace FujinTerm.Views.CharacterWorkshop;

public partial class ItemFinderWindow : Window
{
    private ItemFinderViewModel? _vm;

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
}
