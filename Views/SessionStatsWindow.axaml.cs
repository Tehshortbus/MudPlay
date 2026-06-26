using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

/// <summary>
/// Modeless Session Stats window. Bound to <see cref="SessionStatsViewModel"/>;
/// code-behind attaches the persisted window-layout, wires the global-hotkeys
/// handler, disposes the VM on close, and hosts the panel drag-reorder gesture
/// (grip → drag → drop), applying the VM's saved panel order on open and pushing
/// reorders back through <see cref="SessionStatsViewModel.SaveOrder"/>.
/// </summary>
public partial class SessionStatsWindow : Window
{
    // In-process carrier for the dragged panel's Tag id. Avalonia 12's
    // DataTransfer surface replaced the legacy string-keyed DataObject.
    private static readonly DataFormat<string> PanelFormat =
        DataFormat.CreateInProcessFormat<string>("fujin-session-stats-panel");

    // The panel id under the press point, captured on pointer-down (only when
    // the press lands on a grip) and promoted to a drag once the pointer moves
    // past the threshold.
    private string? _pressedId;
    private Point _pressOrigin;

    // DoDragDropAsync needs the originating PointerPressedEventArgs; we detect
    // the drag in PointerMoved, so hold the press args.
    private PointerPressedEventArgs? _pressArgs;

    public SessionStatsWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "session-stats");
        Closed += OnClosed;

        if (this.FindControl<StackPanel>("PanelHost") is { } host)
        {
            // Tunnel so the grip records the pressed panel before the inner
            // controls (expander headers, the Reset button) handle the click.
            host.AddHandler(PointerPressedEvent, OnPanelPointerPressed, RoutingStrategies.Tunnel);
            host.AddHandler(PointerMovedEvent, OnPanelPointerMoved, RoutingStrategies.Tunnel);
            host.AddHandler(DragDrop.DragOverEvent, OnPanelDragOver);
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
        // Left-button only — right-click is the show/hide context menu.
        // The grip is the sole drag trigger so clicking panel body keeps its
        // native behaviour (expander toggle, button, text selection).
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !IsOnGrip(e.Source as StyledElement))
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

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(PanelFormat, id));
        await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Move);
    }

    private void OnPanelDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = e.DataTransfer.Contains(PanelFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;

    private void OnPanelDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not SessionStatsViewModel vm) return;
        if (this.FindControl<StackPanel>("PanelHost") is not { } host) return;
        if (e.DataTransfer.TryGetValue(PanelFormat) is not { } draggedId) return;

        string? targetId = PanelIdOf(e.Source as StyledElement);
        if (targetId is null || targetId == draggedId) return;

        Control? dragged = PanelWithTag(host, draggedId);
        Control? target = PanelWithTag(host, targetId);
        if (dragged is null || target is null) return;

        int from = host.Children.IndexOf(dragged);
        int to = host.Children.IndexOf(target);
        if (from < 0 || to < 0) return;
        host.Children.Move(from, to);

        vm.SaveOrder(OrderedTags(host));
    }

    /// <summary>Reorder the panel host's children to match the VM's saved order.</summary>
    private void ApplySavedOrder()
    {
        if (DataContext is not SessionStatsViewModel vm) return;
        if (this.FindControl<StackPanel>("PanelHost") is not { } host) return;

        IReadOnlyList<string> order = vm.PanelOrder;
        for (int targetIdx = 0; targetIdx < order.Count; targetIdx++)
        {
            Control? panel = PanelWithTag(host, order[targetIdx]);
            if (panel is null) continue;
            int cur = host.Children.IndexOf(panel);
            if (cur >= 0 && cur != targetIdx)
                host.Children.Move(cur, targetIdx);
        }
    }

    // Walk up from the event source to the nearest grip-classed element; stop at
    // the host so a press on the panel body (not the grip) yields false.
    private bool IsOnGrip(StyledElement? src)
    {
        for (StyledElement? e = src; e is not null and not StackPanel { Name: "PanelHost" }; e = e.Parent)
            if (e is Border b && b.Classes.Contains("grip"))
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
