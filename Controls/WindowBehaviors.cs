using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace FujinTerm.Controls;

// Attached behaviours for top-level windows.
//
// RaiseToFrontOnClickProperty brings a window to the front of the z-order
// whenever the user clicks anywhere inside it. The app's tool windows are all
// modeless and owned by the main window (Window.Show(owner)); on several Linux
// window managers an owned window does not auto-raise above its siblings when
// clicked, and Window.Activate alone won't restack it, so with two or three
// panels open the one you click can stay buried under another. A tunnelling
// InputElement.PointerPressedEvent handler — registered with handledEventsToo
// so a child control that marks the click handled can't swallow it before we
// see it — momentarily toggles Window.Topmost (setting it lifts the window to
// the top of its band; clearing it does not lower it again) to force the WM to
// raise the owned window, then activates it for focus. The main window itself
// has no owner and is left to the WM — force-raising it would make it jump
// above its own owned tool windows and flicker — so it only gets activated.
// Wired app-wide via a single Style Selector="Window" in App.axaml, so every
// window opts in with no per-window code.
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
        if (sender is not Window window) return;

        // The main window has no owner; leave its z-order to the WM. Forcing it
        // up would make it jump above its own owned tool windows and flicker, so
        // just activate it for focus (the pre-existing behaviour).
        if (window.Owner is null)
        {
            window.Activate();
            return;
        }

        // Owned tool window. Activate() raises+focuses on Windows/macOS, but
        // several Linux WMs refuse to restack an owned window above its siblings,
        // so a momentary Topmost toggle is needed to force the raise. Preserve a
        // window that was genuinely Topmost.
        if (!window.Topmost)
        {
            window.Topmost = true;
            window.Topmost = false;
        }
        window.Activate();
    }
}
