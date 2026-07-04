using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

// Modeless Session Stats window. Bound to SessionStatsViewModel;
// code-behind attaches the persisted window-layout, wires the global-hotkeys
// handler, disposes the VM on close, and hosts the panel drag-reorder gesture.
// A panel is dragged by its title label; an insertion line previews where it
// will land, and the VM's saved order is applied on open and pushed back on drop
// via SessionStatsViewModel.SaveOrder.
public partial class SessionStatsWindow : Window
{
    // In-process carrier for the dragged panel's Tag id. Avalonia 12's
    // DataTransfer surface replaced the legacy string-keyed DataObject.
    private static readonly DataFormat<string> PanelFormat =
        DataFormat.CreateInProcessFormat<string>("fujin-session-stats-panel");

    // Thin accent line slotted between panels during a drag to preview the drop
    // position. Non-hit-testable so it never intercepts the drag's hit-testing.
    private readonly Border _dropIndicator = new()
    {
        Height = 3,
        Margin = new Thickness(2, 0),
        CornerRadius = new CornerRadius(1.5),
        IsHitTestVisible = false,
    };

    // The panel id under the press point, captured on pointer-down (only when the
    // press lands on a title handle) and promoted to a drag past the threshold.
    private string? _pressedId;
    private Point _pressOrigin;

    // DoDragDropAsync needs the originating PointerPressedEventArgs; we detect the
    // drag in PointerMoved, so hold the press args.
    private PointerPressedEventArgs? _pressArgs;

    public SessionStatsWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "session-stats");
        Closed += OnClosed;

        _dropIndicator.Background =
            this.TryFindResource("AccentCyanBrush", out object? res) && res is IBrush brush
                ? brush
                : Brushes.DeepSkyBlue;

        if (this.FindControl<StackPanel>("PanelHost") is { } host)
        {
            // Tunnel so the title handle records the pressed panel before the
            // inner controls (expander headers, the Reset button) handle the click.
            host.AddHandler(PointerPressedEvent, OnPanelPointerPressed, RoutingStrategies.Tunnel);
            host.AddHandler(PointerMovedEvent, OnPanelPointerMoved, RoutingStrategies.Tunnel);
            host.AddHandler(DragDrop.DragOverEvent, OnPanelDragOver);
            host.AddHandler(DragDrop.DragLeaveEvent, OnPanelDragLeave);
            host.AddHandler(DragDrop.DropEvent, OnPanelDrop);
        }

        // Apply the saved order once the children have materialised.
        Opened += (_, _) => ApplySavedOrder();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is SessionStatsViewModel vm) vm.Dispose();
    }

    // ----- Panel drag-reorder ---------------------------------------

    private void OnPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Left-button press on a title handle only. A click that doesn't move
        // never starts a drag, so a section title still toggles its expander and
        // right-click still opens the show/hide menu.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !IsOnDragHandle(e.Source as StyledElement))
        {
            _pressedId = null;
            _pressArgs = null;
            return;
        }
        _pressedId = PanelIdOf(e.Source as StyledElement);
        _pressOrigin = e.GetPosition(this);
        _pressArgs = e;
    }

    private async void OnPanelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedId is null || _pressArgs is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressedId = null;
            _pressArgs = null;
            return;
        }
        Point now = e.GetPosition(this);
        if (Math.Abs(now.X - _pressOrigin.X) < 4 && Math.Abs(now.Y - _pressOrigin.Y) < 4)
            return;

        string id = _pressedId;
        PointerPressedEventArgs trigger = _pressArgs;
        _pressedId = null;
        _pressArgs = null;

        StackPanel? host = this.FindControl<StackPanel>("PanelHost");
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(PanelFormat, id));
        try
        {
            await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Move);
        }
        finally
        {
            // Drop fires before the await returns; this also clears the preview
            // when the drag is cancelled or released outside the host.
            host?.Children.Remove(_dropIndicator);
        }
    }

    private void OnPanelDragOver(object? sender, DragEventArgs e)
    {
        if (this.FindControl<StackPanel>("PanelHost") is not { } host) return;
        if (!e.DataTransfer.Contains(PanelFormat))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = DragDropEffects.Move;

        // Slot the insertion line into the gap nearest the cursor.
        host.Children.Remove(_dropIndicator);
        int insertAt = host.Children.Count;
        double y = e.GetPosition(host).Y;
        for (int i = 0; i < host.Children.Count; i++)
        {
            Control child = host.Children[i];
            if (child.Tag is not string || !child.IsVisible) continue;
            if (y < child.Bounds.Y + child.Bounds.Height / 2)
            {
                insertAt = i;
                break;
            }
        }
        host.Children.Insert(insertAt, _dropIndicator);
    }

    private void OnPanelDragLeave(object? sender, DragEventArgs e)
    {
        if (this.FindControl<StackPanel>("PanelHost") is { } host)
            host.Children.Remove(_dropIndicator);
    }

    private void OnPanelDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not SessionStatsViewModel vm) return;
        if (this.FindControl<StackPanel>("PanelHost") is not { } host) return;
        if (e.DataTransfer.TryGetValue(PanelFormat) is not { } draggedId) return;

        host.Children.Remove(_dropIndicator);

        List<string> ids = OrderedTags(host);
        int oldIndex = ids.IndexOf(draggedId);
        if (oldIndex < 0) return;

        // Count the visible panels whose midpoint sits above the cursor — that's
        // the gap the dragged panel lands in. Removing it first shifts the gap
        // down by one when the drag was originally above the target.
        int gap = 0;
        double y = e.GetPosition(host).Y;
        foreach (Control child in host.Children)
        {
            if (child.Tag is not string || !child.IsVisible) continue;
            if (y >= child.Bounds.Y + child.Bounds.Height / 2) gap++;
            else break;
        }

        ids.RemoveAt(oldIndex);
        int insertIndex = Math.Clamp(gap > oldIndex ? gap - 1 : gap, 0, ids.Count);
        ids.Insert(insertIndex, draggedId);

        ApplyOrder(host, ids);
        vm.SaveOrder(ids);
    }

    // Reorder the panel host's children to match the VM's saved order.
    private void ApplySavedOrder()
    {
        if (DataContext is not SessionStatsViewModel vm) return;
        if (this.FindControl<StackPanel>("PanelHost") is { } host)
            ApplyOrder(host, vm.PanelOrder);
    }

    private static void ApplyOrder(StackPanel host, IReadOnlyList<string> ids)
    {
        for (int target = 0; target < ids.Count; target++)
        {
            Control? panel = PanelWithTag(host, ids[target]);
            if (panel is null) continue;
            int cur = host.Children.IndexOf(panel);
            if (cur >= 0 && cur != target)
                host.Children.Move(cur, target);
        }
    }

    // Walk up from the event source to the nearest element flagged as a drag
    // handle (a panel title); stop at the host so a press elsewhere yields false.
    private static bool IsOnDragHandle(StyledElement? src)
    {
        for (StyledElement? e = src; e is not null and not StackPanel { Name: "PanelHost" }; e = e.Parent)
            if (e.Classes.Contains("draghandle"))
                return true;
        return false;
    }

    // Nearest ancestor (or self) carrying a string Tag — the panel id.
    private static string? PanelIdOf(StyledElement? src)
    {
        for (StyledElement? e = src; e is not null; e = e.Parent)
            if (e is Control { Tag: string id })
                return id;
        return null;
    }

    private static Control? PanelWithTag(StackPanel host, string id)
    {
        foreach (Control child in host.Children)
            if (child.Tag as string == id)
                return child;
        return null;
    }

    private static List<string> OrderedTags(StackPanel host)
    {
        List<string> ids = new();
        foreach (Control child in host.Children)
            if (child.Tag is string id)
                ids.Add(id);
        return ids;
    }
}
