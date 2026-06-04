using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FujinTerm.Controls;
using FujinTerm.ViewModels.Navigation;

namespace FujinTerm.Views.Navigation;

/// <summary>
/// Modeless Navigation window. Bound to
/// <see cref="ViewModels.Navigation.NavigationViewModel"/>; PR 7.10
/// ships the shell with status strip + mode bar + map placeholder.
/// The MapControl lands in PR 7.11; the right-rail tree / favourites
/// / loop builder land in PRs 7.12–7.17.
/// </summary>
public partial class NavigationWindow : Window
{
    public NavigationWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "navigation");

        // PR 7.14 — route the map's right-click events into the VM so
        // the context menu items target the clicked room. The
        // ContextMenu itself is wired declaratively in AXAML; here we
        // just update ContextRoomKey before it opens.
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

    /// <summary>
    /// CURRENT NAV ListBox auto-scroll. The VM republishes
    /// <see cref="NavigationViewModel.CurrentNavSelectedRow"/> on every
    /// step advance / lair-state change; we mirror the row into the
    /// ListBox's view via ScrollIntoView so a 60-step path doesn't
    /// require the user to scroll the rail manually as the walker
    /// progresses. Posted via the dispatcher so the call lands AFTER
    /// the ItemsControl has materialised the new container.
    /// </summary>
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

        label.Text = Game.Map.RoomTooltipBuilder.Build(room, svc.RoomGraph, svc.GameData, svc.TBInfo, svc.MonsterSpawns);

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

    private void OnMapRoomRightClicked(Game.Map.RoomKey key, Point _)
    {
        if (DataContext is NavigationViewModel vm) vm.ContextRoomKey = key;
    }

    private void OnMapRoomLeftClicked(Game.Map.RoomKey key, Point _)
    {
        if (DataContext is NavigationViewModel vm) vm.OnRoomLeftClicked(key);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Routes a search-result click back into the VM. We can't put a
    /// command directly on the result row inside a ListBox.ItemTemplate
    /// without an extra ICommand binding helper, so a code-behind
    /// pointer handler keeps the wiring minimal.
    /// </summary>
    private void OnSearchResultClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: RoomSearchResult result }) return;
        if (DataContext is not NavigationViewModel vm) return;
        vm.SelectSearchResultCommand.Execute(result);
        e.Handled = true;
    }
}
