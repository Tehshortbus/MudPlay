using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FujinTerm.Controls;
using FujinTerm.ViewModels.Navigation;

namespace FujinTerm.Views.Navigation;

// Modeless Navigation window. Bound to
// ViewModels.Navigation.NavigationViewModel: status strip + mode bar, the
// Controls.MapControl canvas, and the right-rail room tree / favourites / loop
// builder.
public partial class NavigationWindow : Window
{
    public NavigationWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "navigation");

        // Route the map's right-click events into the VM so the context menu
        // items target the clicked room. The ContextMenu itself is wired
        // declaratively in AXAML; here we just update ContextRoomKey before it
        // opens.
        if (this.FindControl<MapControl>("MapHost") is { } map)
        {
            map.RoomRightClicked       += OnMapRoomRightClicked;
            map.RoomLeftClicked        += OnMapRoomLeftClicked;
            map.RoomHovered            += OnMapRoomHovered;
            map.FloorChangeRequested   += OnMapFloorChangeRequested;
        }

        // Keyboard focus → the map by default so numpad / arrow keys
        // drive the crawler immediately when the window comes to the
        // foreground. Without this, keys silently route to whichever
        // control happened to grab focus last (often the right-rail
        // search box or nothing at all), and the user has to click
        // the map first before navigation works.
        Opened    += (_, _) => FocusMap();
        Activated += (_, _) => FocusMap();

        // Building-loop ListBox — click row to remove, drag to reorder.
        if (this.FindControl<ListBox>("BuilderClicksList") is { } builderList)
            WireBuilderClicksList(builderList);

        // Rail folder trees — drag a leaf row onto a folder node (or the
        // empty tree area) to move it. GOTO favourites, Loops, and
        // Auto-Lair setups each get the same leaf-drag → folder-drop
        // wiring; the drop handler routes by leaf type to the matching
        // VM move method.
        if (this.FindControl<TreeView>("FavoriteTreeView") is { } favTree) WireRowDragDrop(favTree);
        if (this.FindControl<TreeView>("NavTreeView")      is { } navTree) WireRowDragDrop(navTree);

        // Right-click → "Center on Player" routes through a VM event so
        // the command can sit on the VM (where the rest of the context-
        // menu commands live) while the actual centring + suppression
        // clear lives on the MapControl. DataContextChanged is the only
        // safe time to subscribe — DataContext is set externally after
        // the ctor by DialogService / App.OnFrameworkInitialization.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is NavigationViewModel vm)
            {
                vm.CenterOnPlayerRequested += OnCenterOnPlayerRequested;
                vm.PropertyChanged          += OnVmPropertyChanged;
            }
        };
    }

    private void OnCenterOnPlayerRequested()
    {
        if (this.FindControl<MapControl>("MapHost") is { } map)
            map.RecenterOnPlayer();
    }

    // CURRENT NAV ListBox auto-scroll. The VM republishes CurrentNavSelectedRow
    // on every step advance / lair-state change; we mirror the row into the
    // ListBox's view via ScrollIntoView so a 60-step path doesn't require the
    // user to scroll the rail manually as the walker progresses. Posted via the
    // dispatcher so the call lands AFTER the ItemsControl has materialised the
    // new container.
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NavigationViewModel.CurrentNavSelectedRow)) return;
        if (DataContext is not NavigationViewModel vm) return;
        if (vm.CurrentNavSelectedRow is not { } row) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<ListBox>("CurrentNavList") is { } list)
                list.ScrollIntoView(row);
        });
    }

    private void FocusMap()
    {
        if (this.FindControl<MapControl>("MapHost") is { } map)
            map.Focus();
    }

    private void OnMapFloorChangeRequested(Game.Map.RoomKey newOrigin)
    {
        if (DataContext is NavigationViewModel vm) vm.OnFloorChangeRequested(newOrigin);
    }

    private void OnMapRoomHovered(Game.Map.RoomKey? key, Point cursor)
    {
        Border? popup = this.FindControl<Border>("HoverTooltip");
        TextBlock? label = this.FindControl<TextBlock>("HoverTooltipText");
        MapControl? map = this.FindControl<MapControl>("MapHost");
        if (popup is null || label is null || map is null) return;

        if (key is not { } k)
        {
            popup.IsVisible = false;
            return;
        }

        Services.AppServices svc = FujinTerm.Services.AppServices.Current;
        if (svc.RoomGraph.GetRoom(k) is not { } room)
        {
            popup.IsVisible = false;
            return;
        }

        label.Text = Game.Map.RoomTooltipBuilder.Build(room, svc.RoomGraph, svc.GameData, svc.TBInfo, svc.MonsterSpawns, svc.SpellCatalog, svc.PlayerIllumination.Current);

        // Anchor near the cursor — offset a few pixels so the popup
        // doesn't sit directly under the pointer. The MapControl shares
        // the Grid column with this Border (Grid.Column="0"), so the
        // popup's Margin acts as a (Left, Top) offset in the same
        // coordinate space the cursor is reported in.
        const double offsetX = 14;
        const double offsetY = 18;

        // Measure with the popup briefly visible so DesiredSize reflects
        // real content rather than the (0,0) Avalonia returns for an
        // IsVisible=false element. Opacity=0 hides the flicker while we
        // compute + apply the final position.
        popup.Opacity = 0;
        popup.IsVisible = true;
        popup.Margin = new Thickness(0);          // clear stale margin so measure isn't biased
        popup.InvalidateMeasure();
        popup.UpdateLayout();                     // force layout pass to settle the measure
        Size desired = popup.Bounds.Size;
        Size viewport = map.Bounds.Size;

        // Edge-flip: when the default below-and-right anchor would put
        // the tooltip past the bottom / right edge of the visible map,
        // swap to above / left of the cursor instead. Without this the
        // tooltip renders off-screen and the user has to pan first.
        double anchorX = cursor.X + offsetX;
        if (anchorX + desired.Width > viewport.Width - 4)
            anchorX = Math.Max(0, cursor.X - offsetX - desired.Width);

        double anchorY = cursor.Y + offsetY;
        if (anchorY + desired.Height > viewport.Height - 4)
            anchorY = Math.Max(0, cursor.Y - offsetY - desired.Height);

        popup.Margin = new Thickness(anchorX, anchorY, 0, 0);
        popup.Opacity = 1;
    }

    private void OnMapRoomRightClicked(Game.Map.RoomKey? key, Point _)
    {
        if (DataContext is NavigationViewModel vm) vm.ContextRoomKey = key;
    }

    private void OnMapRoomLeftClicked(Game.Map.RoomKey key, Point _)
    {
        if (DataContext is NavigationViewModel vm) vm.OnRoomLeftClicked(key);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Routes a search-result click back into the VM. We can't put a command
    // directly on the result row inside a ListBox.ItemTemplate without an extra
    // ICommand binding helper, so a code-behind pointer handler keeps the wiring
    // minimal.
    private void OnSearchResultClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: RoomSearchResult result }) return;
        if (DataContext is not NavigationViewModel vm) return;
        vm.SelectSearchResultCommand.Execute(result);
        e.Handled = true;
    }

    // ----- Building-loop click list ---------------------------------

    private void WireBuilderClicksList(ListBox list)
    {
        // Single-click any row (outside the per-row buttons) removes
        // that waypoint. The Up / Down buttons in the template route
        // through MoveBuilderClickCommand for reorder. Drag-and-drop
        // reorder is a known follow-up — the current Avalonia drag-
        // drop API replaced DataObject with a DataTransfer surface
        // that's worth wiring centrally rather than per-feature.
        list.AddHandler(PointerReleasedEvent, BuilderRowPointerReleased);
    }

    private void BuilderRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Bubble up to find a ListBoxItem wrapping a LoopBuilderRow.
        // Skip the click when the pointer is over one of the reorder
        // buttons — those have their own command bindings.
        if (e.Source is Button) return;
        if (e.Source is not StyledElement el) return;
        LoopBuilderRow? row = null;
        for (StyledElement? cur = el; cur is not null; cur = (cur as Control)?.Parent as StyledElement)
        {
            if (cur is ListBoxItem { DataContext: LoopBuilderRow r })
            {
                row = r;
                break;
            }
        }
        if (row is null) return;
        if (DataContext is NavigationViewModel vm)
            vm.RemoveBuilderClickAt(row.Index);
    }

    // Per-row "✕" button — removes the waypoint at the row's index. Bound via
    // Click rather than Command so it can pull the DataContext off the button
    // without the ListBox-ancestor binding ceremony.
    private void OnBuilderRowRemoveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: LoopBuilderRow row }) return;
        if (DataContext is not NavigationViewModel vm) return;
        vm.RemoveBuilderClickAt(row.Index);
        e.Handled = true;
    }

    // ----- Rail folder drag-drop ------------------------------------
    // Mirrors NavigationManagerDialog's drag-drop: a leaf row is the
    // drag source, a folder node (or the empty tree area = root) is the
    // drop target. The move routes through the VM's public Move methods
    // so the store + on-disk layout stay the single source of truth.

    // In-process object reference carried by the drag session. Avalonia
    // 12's DataTransfer surface replaced the legacy string-keyed
    // DataObject.
    private static readonly DataFormat<object> RowFormat =
        DataFormat.CreateInProcessFormat<object>("fujin-nav-rail-row");

    // The leaf row under the press point, captured on pointer-down and
    // promoted to a drag once the pointer moves past the threshold.
    private object? _pressedRow;
    private Point _pressOrigin;

    // DoDragDropAsync requires the originating PointerPressedEventArgs as
    // its trigger; we detect the drag in PointerMoved, so hold the press
    // args.
    private PointerPressedEventArgs? _pressArgs;

    private void WireRowDragDrop(TreeView tree)
    {
        // Tunnel so we record the pressed row before inner controls
        // (the Load / Run / ✎ / ✕ buttons) get a chance to handle it.
        tree.AddHandler(PointerPressedEvent, OnRailRowPointerPressed, RoutingStrategies.Tunnel);
        tree.AddHandler(PointerMovedEvent, OnRailRowPointerMoved, RoutingStrategies.Tunnel);
        tree.AddHandler(DragDrop.DragOverEvent, OnRailDragOver);
        tree.AddHandler(DragDrop.DropEvent, OnRailDrop);
    }

    private void OnRailRowPointerPressed(object? sender, PointerPressedEventArgs e)
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

    private async void OnRailRowPointerMoved(object? sender, PointerEventArgs e)
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

    private void OnRailDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = e.DataTransfer.Contains(RowFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;

    private void OnRailDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not NavigationViewModel vm) return;
        if (!e.DataTransfer.Contains(RowFormat)) return;

        object? row = e.DataTransfer.TryGetValue(RowFormat);
        string folder = TargetFolderOf(e.Source as StyledElement);
        switch (row)
        {
            case FavoriteRowViewModel fav:   vm.MoveFavoriteToFolder(fav, folder); break;
            case LoopRowViewModel loop:      vm.MoveLoopToFolder(loop, folder); break;
            case LairSetupRowViewModel lair: vm.MoveSetupToFolder(lair, folder); break;
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
                case FavoriteRowViewModel:
                case LoopRowViewModel:
                case LairSetupRowViewModel:
                    return e.DataContext;
                case NavFolderNodeViewModel:
                    return null;
            }
        }
        return null;
    }

    // Resolve the destination folder from whatever sits under the drop
    // point: a folder node → its path; a leaf → that leaf's folder (drop
    // beside its siblings); empty tree area → root ("").
    private static string TargetFolderOf(StyledElement? src)
    {
        for (StyledElement? e = src; e is not null; e = e.Parent)
        {
            switch (e.DataContext)
            {
                case NavFolderNodeViewModel folder: return folder.Path;
                case FavoriteRowViewModel fav:      return fav.Folder;
                case LoopRowViewModel loop:         return loop.Source.Folder;
                case LairSetupRowViewModel lair:    return lair.Source.Folder;
            }
        }
        return string.Empty;
    }
}
