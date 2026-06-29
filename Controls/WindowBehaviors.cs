using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace FujinTerm.Controls;

/// <summary>
/// Attached behaviours for top-level windows.
/// </summary>
/// <remarks>
/// <see cref="RaiseToFrontOnClickProperty"/> brings a window to the front of the
/// z-order whenever the user clicks anywhere inside it. The app's tool windows
/// are all modeless and owned by the main window (<c>Window.Show(owner)</c>); on
/// several Linux window managers an owned window does <i>not</i> auto-raise above
/// its siblings when clicked, so with two or three panels open the one you click
/// can stay buried under another. A tunnelling
/// <see cref="InputElement.PointerPressedEvent"/> handler — registered with
/// <c>handledEventsToo</c> so a child control that marks the click handled can't
/// swallow it before we see it — calls <see cref="Window.Activate"/>, which
/// raises and focuses the window. Wired app-wide via a single
/// <c>Style Selector="Window"</c> in <c>App.axaml</c>, so every window opts in
/// with no per-window code.
/// </remarks>
public static class WindowBehaviors
{
    public static readonly AttachedProperty<bool> RaiseToFrontOnClickProperty =
        AvaloniaProperty.RegisterAttached<Window, bool>("RaiseToFrontOnClick", typeof(WindowBehaviors));

    public static void SetRaiseToFrontOnClick(Window window, bool value) =>
        window.SetValue(RaiseToFrontOnClickProperty, value);

    public static bool GetRaiseToFrontOnClick(Window window) =>
        window.GetValue(RaiseToFrontOnClickProperty);

    static WindowBehaviors()
    {
        RaiseToFrontOnClickProperty.Changed.AddClassHandler<Window>(OnChanged);
    }

    private static void OnChanged(Window window, AvaloniaPropertyChangedEventArgs e)
    {
        // Idempotent: removing a handler that was never added is a no-op, so a
        // re-applied style setter can't double-subscribe.
        window.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        if (e.GetNewValue<bool>())
            window.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed,
                RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Window window) window.Activate();
    }
}
