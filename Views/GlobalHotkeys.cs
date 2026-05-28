using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;

namespace FujinTerm.Views;

/// <summary>
/// Attaches the MainWindow's <c>KeyBindings</c> to a child window so the
/// app-wide hotkeys (F2 / F4 / F9 / F10 / F11 / Ctrl+, / Ctrl+G / Ctrl+Q
/// / Ctrl+K / F1) still fire when the focus is in a child window
/// (Conversation, Backscroll, Wire Inspector, etc.). Without this, only
/// MainWindow's own focus surface honoured the hotkeys — the toggle
/// behaviour was unreachable once a panel had focus.
/// </summary>
/// <remarks>
/// Implementation: copies <see cref="Window.KeyBindings"/> from
/// <c>desktop.MainWindow</c> into the target. New KeyBinding instances
/// wrap the same <see cref="KeyBinding.Command"/> and <see cref="KeyBinding.Gesture"/>
/// — so re-pressing F2 from inside Conversation routes to the same
/// <c>OpenConversationCommand</c> that opens it, and the existing toggle
/// logic in <c>MainWindowViewModel</c> handles the close path.
/// </remarks>
public static class GlobalHotkeys
{
    /// <summary>
    /// Call from each child window's constructor (or <c>Opened</c>) to
    /// mirror MainWindow's hotkeys into <paramref name="window"/>.
    /// </summary>
    public static void Attach(Window window)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;
        if (ReferenceEquals(window, main)) return;

        foreach (KeyBinding kb in main.KeyBindings)
        {
            window.KeyBindings.Add(new KeyBinding
            {
                Gesture = kb.Gesture,
                Command = kb.Command,
                CommandParameter = kb.CommandParameter,
            });
        }
    }
}
