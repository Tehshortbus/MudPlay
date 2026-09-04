using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MudPlay.Controls;
using MudPlay.ViewModels.Navigation;

namespace MudPlay.Views.Navigation;

// Modeless Navigation window. Bound to
// ViewModels.Navigation.NavigationViewModel: status strip + mode bar, the
// Controls.MapControl canvas, and the right-rail room tree / favourites / loop
// builder.
public partial class NavigationWindow : Window
{
    // Legend-drag state. _legendGrab is the cursor's offset within the legend at
    // press time, so the drag moves the legend under the same point it was grabbed.
    private bool _legendDragging;
    private Point _legendGrab;

    public NavigationWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "navigation");

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
            // Shift+right-click fires a single unambiguous floor/teleport jump
            // straight from the press (see OnMapRoomRightClicked) and cancels the
            // menu that would otherwise open on release.
            if (map.ContextMenu is ContextMenu roomMenu) roomMenu.Opening += OnRoomMenuOpening;
        }

        // Keyboard focus → the map by default so numpad / arrow keys
        // drive the crawler immediately when the window comes to the
        // foreground. Without this, keys silently route to whichever
        // control happened to grab focus last (often the right-rail
        // search box or nothing at all), and the user has to click
        // the map first before navigation works.
        Opened    += (_, _) => FocusMap();
        Activated += (_, _) => FocusMap();
        // Position the legend once the first layout pass has sized the map (its
        // Bounds are 0 until then). No-op when the legend starts hidden.
        Opened    += (_, _) => Dispatcher.UIThread.Post(ApplyLegendPosition, DispatcherPriority.Background);

        // Building-loop ListBox — click row to remove, drag to reorder.
        if (this.FindControl<ListBox>("BuilderClicksList") is { } builderList)
            WireBuilderClicksList(builderList);

        // Rail folder trees — drag a leaf row onto a folder node (or the
        // empty tree area) to move it. GOTO favourites, Loops, and
        // Auto-Lair setups each get the same leaf-drag → folder-drop
        // wiring; the drop handler routes by leaf type to the matching
        // VM move method.
        if (this.FindControl<ListBox>("FavoriteTreeView") is { } favTree) WireRowDragDrop(favTree);
        if (this.FindControl<ListBox>("NavTreeView")      is { } navTree) WireRowDragDrop(navTree);

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
                // Expose the map's live browse state so a movement step defers
                // the layout re-root while the user is panning / crawling / has
                // just jumped the view (see NavigationViewModel.RefreshFromTracker).
                if (this.FindControl<MapControl>("MapHost") is { } browseMap)
                    vm.IsMapBrowsing = () => browseMap.IsAutoFollowSuppressed;
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
        // Toggling the legend back on re-clamps it into the currently-visible map
        // — a position left off-view by a since-shrunk / covered window snaps back.
        // (A live shrink while it's already shown is left alone, per design.)
        if (e.PropertyName == nameof(NavigationViewModel.LegendVisible))
        {
            if (DataContext is NavigationViewModel { LegendVisible: true })
                Dispatcher.UIThread.Post(ApplyLegendPosition, DispatcherPriority.Background);
            return;
        }

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

        Services.AppServices svc = MudPlay.Services.AppServices.Current;
        if (svc.RoomGraph.GetRoom(k) is not { } room)
        {
            popup.IsVisible = false;
            return;
        }

        label.Text = Game.Map.RoomTooltipBuilder.Build(room, svc.RoomGraph, svc.GameData, svc.TBInfo, svc.MonsterSpawns, svc.SpellCatalog, svc.PlayerIllumination.Current, svc.RoomFloorItems);

        // Font is char-tier configurable (Settings → General → Navigation tooltip
        // font). Read it live per hover so a Settings change lands on the next
        // hover with no rebind plumbing — the tooltip is transient anyway.
        label.FontFamily = new Avalonia.Media.FontFamily(svc.Display.NavTooltipFontFamily);
        label.FontSize = svc.Display.NavTooltipFontSize;

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

    // Set when a Shift+right-click fired a floor/teleport shortcut, so the menu
    // that Avalonia would open on the following release is cancelled instead.
    private bool _suppressRoomMenu;

    private void OnMapRoomRightClicked(Game.Map.RoomKey? key, Point _, KeyModifiers modifiers)
    {
        _suppressRoomMenu = false;
        if (DataContext is not NavigationViewModel vm) return;
        vm.ContextRoomKey = key;   // rebuilds the up/down/teleport context synchronously
        // Shift held on a room whose sole jump is an up-only / down-only / lone
        // teleport → do it now and skip the menu.
        if (key is not null && (modifiers & KeyModifiers.Shift) != 0)
            _suppressRoomMenu = vm.TryQuickFloorTeleportShortcut();
    }

    private void OnRoomMenuOpening(object? sender, CancelEventArgs e)
    {
        if (!_suppressRoomMenu) return;
        e.Cancel = true;
        _suppressRoomMenu = false;
    }

    private void OnMapRoomLeftClicked(Game.Map.RoomKey key, Point _)
    {
        if (DataContext is NavigationViewModel vm) vm.OnRoomLeftClicked(key);
    }

    // ----- Draggable map legend -------------------------------------------

    private void OnLegendPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border legend) return;
        if (!e.GetCurrentPoint(legend).Properties.IsLeftButtonPressed) return;
        _legendGrab = e.GetPosition(legend);      // cursor offset within the legend
        _legendDragging = true;
        e.Pointer.Capture(legend);
        e.Handled = true;
    }

    private void OnLegendPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_legendDragging || sender is not Border legend) return;
        if (this.FindControl<MapControl>("MapHost") is not { } map) return;
        Point cursor = e.GetPosition(map);
        legend.Margin = ClampLegend(cursor.X - _legendGrab.X, cursor.Y - _legendGrab.Y,
                                    legend.Bounds.Size, map.Bounds.Size);
    }

    private void OnLegendPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_legendDragging) return;
        _legendDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        if (sender is not Border legend) return;
        // Persist the dropped position (install-wide, Global tier). Save fires
        // GlobalSettingsChanged — harmless here (the map only re-reads nav-line
        // styles from it, which are unchanged).
        var store = MudPlay.Services.AppServices.Current.Settings;
        store.Current.MapLegendX = legend.Margin.Left;
        store.Current.MapLegendY = legend.Margin.Top;
        store.Save();
    }

    // Place the legend at its stored (or default bottom-left) position, clamped so
    // it stays fully inside the current map viewport. No-op until both the legend
    // and the map are laid out. Called on window open and on legend toggle-on.
    private void ApplyLegendPosition()
    {
        if (this.FindControl<Border>("MapLegend") is not { IsVisible: true } legend) return;
        if (this.FindControl<MapControl>("MapHost") is not { } map) return;

        // Clear the margin BEFORE measuring: a stale position past the (now
        // smaller) map edge starves the legend's arrange, collapsing its size to 0
        // — which would defeat the clamp below and leave it stuck off-view. With
        // the margin cleared it arranges at full size, so we get a real size to
        // clamp against. Mirrors the hover tooltip's measure dance.
        legend.Margin = new Thickness(0);
        legend.InvalidateMeasure();
        legend.UpdateLayout();
        Size legendSize = legend.Bounds.Size;
        Size mapSize = map.Bounds.Size;
        if (legendSize.Width <= 0 || mapSize.Width <= 0) return;   // not laid out yet

        var g = MudPlay.Services.AppServices.Current.Settings.Current;
        double x, y;
        if (g.MapLegendX is { } sx && g.MapLegendY is { } sy)
        {
            x = sx;
            y = sy;
        }
        else
        {
            // Default: bottom-left, 12px inset (the pre-drag placement).
            x = 12;
            y = mapSize.Height - legendSize.Height - 12;
        }
        legend.Margin = ClampLegend(x, y, legendSize, mapSize);
    }

    // Clamp a (Left, Top) legend position so the whole legend stays inside the map
    // viewport. When the legend is larger than the viewport the ceiling collapses
    // to 0 (pinned top-left) rather than going negative.
    private static Thickness ClampLegend(double x, double y, Size legendSize, Size mapSize)
    {
        double maxX = Math.Max(0, mapSize.Width - legendSize.Width);
        double maxY = Math.Max(0, mapSize.Height - legendSize.Height);
        return new Thickness(Math.Clamp(x, 0, maxX), Math.Clamp(y, 0, maxY), 0, 0);
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

    // Enter in the search box resolves the typed text: when it lands on exactly one
    // (walkable) match, arm it — flipping the goto button to that room, same as
    // clicking the row. Ambiguous (0 or >1 results) does nothing; the user picks.
    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not NavigationViewModel vm) return;
        if (vm.SearchResults.Count == 1 && !vm.SearchResults[0].IsInformational)
        {
            vm.SelectSearchResultCommand.Execute(vm.SearchResults[0]);
            e.Handled = true;
        }
    }

    // Open the recent-destinations flyout from the goto button. The flyout is
    // attached to the search box (not this button) so it drops straight down over
    // the right rail; showing it here keeps the goto button as the affordance.
    private void OnGotoButtonClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<TextBox>("SearchBox") is { } searchBox)
            FlyoutBase.ShowAttachedFlyout(searchBox);
    }


    // Picking a recent destination arms it (VM OnSelectedGotoHistoryChanged) and
    // should dismiss the flyout — otherwise it lingers until a click elsewhere.
    // Only a real pick closes it (the VM resets the selection to null afterwards,
    // which re-fires this with no added item); the Hide is deferred so the
    // SelectedItem binding + arming finish before the popup tears down.
    private void OnGotoHistorySelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        HideGotoFlyout();
    }

    // Clearing the queued destination should also dismiss the flyout (the bound
    // Command runs the clear; this just closes the popup). Same deferred Hide as
    // a history pick — the button itself vanishes once HasQueuedDestination flips
    // false, so the flyout has nothing left to show anyway.
    private void OnClearDestinationClick(object? sender, RoutedEventArgs e) => HideGotoFlyout();

    private void HideGotoFlyout() => Dispatcher.UIThread.Post(() =>
    {
        if (this.FindControl<TextBox>("SearchBox") is { } searchBox)
            FlyoutBase.GetAttachedFlyout(searchBox)?.Hide();
    });

    // ----- Building-loop click list ---------------------------------

    private void WireBuilderClicksList(ListBox list)
    {
        // Single-click any row (outside the per-row buttons) opens that
        // waypoint's action editor so a command can be attached while
        // building — the ✕ button removes, ↑ / ↓ reorder. (Left-click used
        // to delete; that was a foot-gun once actions became editable in
        // place — the delete now lives on the roomier ✕ box.) Drag-and-drop
        // reorder is a known follow-up.
        list.AddHandler(PointerReleasedEvent, BuilderRowPointerReleased);
    }

    private void BuilderRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Bubble up to find a ListBoxItem wrapping a LoopBuilderRow.
        // Skip the click when the pointer is over one of the per-row
        // buttons (↑ / ↓ / ✕) — those have their own handlers.
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
        if (DataContext is NavigationViewModel vm
            && vm.EditBuilderWaypointActionCommand.CanExecute(row))
            vm.EditBuilderWaypointActionCommand.Execute(row);
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
        DataFormat.CreateInProcessFormat<object>("mudplay-nav-rail-row");

    // The leaf row under the press point, captured on pointer-down and
    // promoted to a drag once the pointer moves past the threshold.
    private object? _pressedRow;
    private Point _pressOrigin;

    // DoDragDropAsync requires the originating PointerPressedEventArgs as
    // its trigger; we detect the drag in PointerMoved, so hold the press
    // args.
    private PointerPressedEventArgs? _pressArgs;

    private void WireRowDragDrop(Control tree)
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
            // A flat row wraps the leaf/folder VM; unwrap it so a press on the row
            // chrome (indent / chevron) resolves the same as one on the content.
            object? dc = e.DataContext is NavFlatRow flat ? flat.Item : e.DataContext;
            switch (dc)
            {
                case FavoriteRowViewModel:
                case LoopRowViewModel:
                case LairSetupRowViewModel:
                    return dc;
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
            object? dc = e.DataContext is NavFlatRow flat ? flat.Item : e.DataContext;
            switch (dc)
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
