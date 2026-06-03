using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
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
            map.RoomRightClicked += OnMapRoomRightClicked;
            map.RoomLeftClicked  += OnMapRoomLeftClicked;
            map.RoomHovered      += OnMapRoomHovered;
        }
    }

    private void OnMapRoomHovered(Game.Map.RoomKey? key, Point cursor)
    {
        Border? popup = this.FindControl<Border>("HoverTooltip");
        TextBlock? label = this.FindControl<TextBlock>("HoverTooltipText");
        if (popup is null || label is null) return;

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

        label.Text = Game.Map.RoomTooltipBuilder.Build(room, svc.RoomGraph, svc.GameData);

        // Anchor the popup near the cursor — offset a few pixels so
        // it doesn't sit directly under the pointer. The MapControl
        // shares the Grid column with this Border (Grid.Column="0"),
        // so the popup's Margin acts as a (Left, Top) offset within
        // the column.
        const double offsetX = 14;
        const double offsetY = 18;
        popup.Margin = new Thickness(cursor.X + offsetX, cursor.Y + offsetY, 0, 0);
        popup.IsVisible = true;
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
