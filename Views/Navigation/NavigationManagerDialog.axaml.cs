using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FujinTerm.ViewModels.Navigation;

namespace FujinTerm.Views.Navigation;

// Modeless dialog hosting saved Loops + Auto-Lair markers for
// rename/delete/unmark actions. Surfaced from the NavigationWindow's "Manage"
// chip in the action row.
//
// Drag-drop lives in code-behind (allowed for drag-session state per CLAUDE.md):
// a leaf row is the drag source; a folder node — or the empty tree area (root) —
// is the drop target. The actual move routes back through the view-model's
// MoveLoopToFolder / MoveLairToFolder so the store + on-disk layout stay the
// single source of truth.
public partial class NavigationManagerDialog : Window
{
    // In-process object reference carried by the drag session. Avalonia 12's
    // DataTransfer surface replaced the legacy string-keyed DataObject.
    private static readonly DataFormat<object> RowFormat =
        DataFormat.CreateInProcessFormat<object>("fujin-nav-manager-row");

    // The leaf row under the press point, captured on pointer-down and
    // promoted to a drag once the pointer moves past the threshold.
    private object? _pressedRow;
    private Point _pressOrigin;

    // DoDragDropAsync requires the originating PointerPressedEventArgs as its
    // trigger; we detect the drag in PointerMoved, so hold the press args.
    private PointerPressedEventArgs? _pressArgs;

    public NavigationManagerDialog()
    {
        InitializeComponent();
        WireDragDrop(WalkTreeView);
        // DoubleTapped only routes as Bubble, so the handler must be
        // registered on that strategy to fire at all.
        WalkTreeView.AddHandler(DoubleTappedEvent, OnRowDoubleTapped, RoutingStrategies.Bubble);
    }

    // Double-clicking a leaf opens its editor — same action as the Edit
    // button / context menu, just the faster gesture. Folder nodes are
    // ignored (LeafRowOf returns null for them).
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not NavigationManagerDialogViewModel vm) return;
        switch (LeafRowOf(e.Source as StyledElement))
        {
            case ManagerLoopRow loop:      vm.EditLoopCommand.Execute(loop); break;
            case ManagerLairSetupRow lair: vm.EditLairSetupCommand.Execute(lair); break;
        }
    }

    private void WireDragDrop(TreeView tree)
    {
        // Tunnel so we record the pressed row before inner controls
        // (the Edit / ✕ buttons) get a chance to handle the event.
        tree.AddHandler(PointerPressedEvent, OnRowPointerPressed, RoutingStrategies.Tunnel);
        tree.AddHandler(PointerMovedEvent, OnRowPointerMoved, RoutingStrategies.Tunnel);
        tree.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        tree.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Left-button only — right-click is the context-menu gesture.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressedRow = null;
            _pressArgs = null;
            return;
        }
        _pressedRow = LeafRowOf(e.Source as StyledElement);
        _pressOrigin = e.GetPosition(this);
        _pressArgs = e;
    }

    private async void OnRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedRow is null || _pressArgs is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressedRow = null;
            _pressArgs = null;
            return;
        }
        Point now = e.GetPosition(this);
        if (Math.Abs(now.X - _pressOrigin.X) < 4 && Math.Abs(now.Y - _pressOrigin.Y) < 4)
            return;

        object row = _pressedRow;
        PointerPressedEventArgs trigger = _pressArgs;
        _pressedRow = null;
        _pressArgs = null;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(RowFormat, row));
        await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Move);
    }

    // Routes a walk-to dropdown click back into the VM. Same minimal
    // code-behind pattern as the Navigation window's search dropdown —
    // a ListBox.ItemTemplate row can't host an ICommand without an extra
    // binding helper, so a pointer handler keeps the wiring lean.
    private void OnWalkToResultClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: RoomSearchResult result }) return;
        if (DataContext is not NavigationManagerDialogViewModel vm) return;
        vm.SelectSearchResultCommand.Execute(result);
        e.Handled = true;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = e.DataTransfer.Contains(RowFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not NavigationManagerDialogViewModel vm) return;
        if (!e.DataTransfer.Contains(RowFormat)) return;

        object? row = e.DataTransfer.TryGetValue(RowFormat);
        string folder = TargetFolderOf(e.Source as StyledElement);
        switch (row)
        {
            case ManagerLoopRow loop: vm.MoveLoopToFolder(loop, folder); break;
            case ManagerLairSetupRow lair: vm.MoveLairToFolder(lair, folder); break;
        }
    }

    // Walk up the logical tree from the event source to the nearest
    // leaf-row DataContext. A press that started on a folder node (or
    // anywhere non-leaf) yields null so we don't drag folders.
    private static object? LeafRowOf(StyledElement? src)
    {
        for (StyledElement? e = src; e is not null; e = e.Parent)
        {
            switch (e.DataContext)
            {
                case ManagerLoopRow:
                case ManagerLairSetupRow:
                    return e.DataContext;
                case NavFolderNodeViewModel:
                    return null;
            }
        }
        return null;
    }

    // Resolve the destination folder from whatever sits under the drop
    // point: a folder node → its path; a leaf → that leaf's folder
    // (drop beside its siblings); empty tree area → root ("").
    private static string TargetFolderOf(StyledElement? src)
    {
        for (StyledElement? e = src; e is not null; e = e.Parent)
        {
            switch (e.DataContext)
            {
                case NavFolderNodeViewModel folder: return folder.Path;
                case ManagerLoopRow loop:           return loop.Source.Folder;
                case ManagerLairSetupRow lair:      return lair.Source.Folder;
            }
        }
        return string.Empty;
    }
}
