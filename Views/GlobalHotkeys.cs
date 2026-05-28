using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

/// <summary>
/// Adds the app-wide window-toggle hotkeys (F2 / F3 / F4 / F5 / F7 /
/// F9 / F10 / F11 / Ctrl+, / Ctrl+G / Ctrl+K / Ctrl+Q / F1) to a child
/// window so re-pressing the hotkey closes the open window — the
/// Phase 2 toggle convention. Avalonia's window-level KeyBindings only
/// fire when that window has focus, so without this every panel had its
/// own focus surface and the hotkey toggle was unreachable.
/// </summary>
/// <remarks>
/// Earlier this helper tried to mirror MainWindow's <c>KeyBindings</c>
/// collection by value. That fails for XAML-defined bindings: the
/// <c>kb.Command</c> property is the binding expression and may not have
/// resolved to an <see cref="ICommand"/> at the moment a child window is
/// being constructed. Reaching into <see cref="MainWindowViewModel"/>
/// directly and wiring KeyBindings against the relay-command instances
/// sidesteps the indirection entirely.
/// </remarks>
public static class GlobalHotkeys
{
    public static void Attach(Window window)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
            {
                MainWindow.DataContext: MainWindowViewModel vm
            } desktop) return;
        if (ReferenceEquals(window, desktop.MainWindow)) return;

        Add(window, "F1",            vm.OpenHelpTopicsCommand);
        Add(window, "F2",            vm.OpenConversationCommand);
        Add(window, "F3",            vm.OpenPartyCommand);
        Add(window, "F4",            vm.OpenWorkshopCommand);
        Add(window, "F5",            vm.OpenNavigationCommand);
        Add(window, "F7",            vm.OpenSpellBookCommand);
        Add(window, "F9",            vm.OpenLogPaneCommand);
        Add(window, "F10",           vm.OpenBackscrollCommand);
        Add(window, "F11",           vm.OpenSessionStatsCommand);
        Add(window, "Ctrl+OemComma", vm.OpenSettingsCommand);
        Add(window, "Ctrl+G",        vm.OpenGameDataBrowserCommand);
        Add(window, "Ctrl+K",        vm.ToggleConnectionCommand);
        Add(window, "Ctrl+Q",        vm.QuitCommand);
    }

    private static void Add(Window window, string gesture, System.Windows.Input.ICommand command)
    {
        window.KeyBindings.Add(new KeyBinding
        {
            Gesture = KeyGesture.Parse(gesture),
            Command = command,
        });
    }
}
