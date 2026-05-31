using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

/// <summary>
/// Adds the app-wide window-toggle hotkeys (now sourced from
/// <see cref="KeybindingStore"/>) to a child window so re-pressing
/// the hotkey closes the open window — the Phase 2 toggle convention.
/// Avalonia's window-level KeyBindings only fire when that window has
/// focus, so without this every panel had its own focus surface and
/// the hotkey toggle was unreachable.
/// </summary>
/// <remarks>
/// On <see cref="KeybindingStore.BindingChanged"/> the bindings for
/// every attached window get rebuilt — keeping the runtime in sync
/// with whatever the user just rebound. The earlier hardcoded
/// literal-gestures approach drifted any time the store was edited.
/// </remarks>
public static class GlobalHotkeys
{
    private static readonly List<WeakReference<Window>> _attached = new();
    private static bool _subscribed;

    public static void Attach(Window window)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
            {
                MainWindow.DataContext: MainWindowViewModel vm
            } desktop) return;
        if (ReferenceEquals(window, desktop.MainWindow)) return;

        Rebuild(window, vm);

        _attached.Add(new WeakReference<Window>(window));
        EnsureStoreSubscription();
    }

    /// <summary>
    /// Compose the keybindings on <see cref="MainWindow"/> itself —
    /// called once from <see cref="MainWindow"/>'s ctor so the main
    /// window picks up the same store-driven shortcuts as the child
    /// panels. The XAML &lt;KeyBinding&gt; literals previously here
    /// have been removed in favour of this one source of truth.
    /// </summary>
    /// <remarks>
    /// DataContext is set by <c>App.OnFrameworkInitializationCompleted</c>
    /// *after* the ctor runs, so we can't Rebuild synchronously. We
    /// hook two events to cover both orderings: <see cref="Window.Opened"/>
    /// (definitive — fires after Show, by which time DataContext is set)
    /// and <see cref="StyledElement.DataContextChanged"/> (covers the
    /// case where DataContext is swapped post-Show, currently unused
    /// but cheap insurance).
    /// </remarks>
    public static void AttachMain(Window mainWindow)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
            { } _) return;

        void TryRebuild()
        {
            if (mainWindow.DataContext is MainWindowViewModel vm) Rebuild(mainWindow, vm);
        }

        mainWindow.Opened             += (_, _) => TryRebuild();
        mainWindow.DataContextChanged += (_, _) => TryRebuild();

        _attached.Add(new WeakReference<Window>(mainWindow));
        EnsureStoreSubscription();
    }

    private static void EnsureStoreSubscription()
    {
        if (_subscribed) return;
        _subscribed = true;
        AppServices.Current.Keybindings.BindingChanged += _ =>
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
                { MainWindow.DataContext: MainWindowViewModel vm } _) return;
            // Rebuild every still-alive attached window.
            for (int i = _attached.Count - 1; i >= 0; i--)
            {
                if (_attached[i].TryGetTarget(out Window? w)) Rebuild(w, vm);
                else _attached.RemoveAt(i);
            }
        };
    }

    private static void Rebuild(Window window, MainWindowViewModel vm)
    {
        // Clear any KeyBindings this helper installed previously so a
        // rebind doesn't leave the old chord still firing. KeyBindings
        // installed elsewhere (XAML, individual windows) aren't ours
        // to remove — none currently exist on the windows we attach to.
        window.KeyBindings.Clear();

        KeybindingStore store = AppServices.Current.Keybindings;
        foreach ((BuiltInAction action, KeyChord chord) in store.Bindings)
        {
            if (chord.IsEmpty) continue;
            System.Windows.Input.ICommand? command = ResolveCommand(vm, action);
            if (command is null) continue;
            string? gesture = chord.GestureString;
            if (gesture is null) continue;

            window.KeyBindings.Add(new KeyBinding
            {
                Gesture = KeyGesture.Parse(gesture),
                Command = command,
            });
        }
    }

    /// <summary>Map a <see cref="BuiltInAction"/> to the MainWindow VM command that fires it.</summary>
    private static System.Windows.Input.ICommand? ResolveCommand(MainWindowViewModel vm, BuiltInAction action) => action switch
    {
        BuiltInAction.OpenConversation     => vm.OpenConversationCommand,
        BuiltInAction.OpenParty            => vm.OpenPartyCommand,
        BuiltInAction.OpenWorkshop         => vm.OpenWorkshopCommand,
        BuiltInAction.OpenNavigation       => vm.OpenNavigationCommand,
        BuiltInAction.OpenSpellBook        => vm.OpenSpellBookCommand,
        BuiltInAction.OpenLogPane          => vm.OpenLogPaneCommand,
        BuiltInAction.OpenBackscroll       => vm.OpenBackscrollCommand,
        BuiltInAction.OpenSessionStats     => vm.OpenSessionStatsCommand,
        BuiltInAction.OpenSettings         => vm.OpenSettingsCommand,
        BuiltInAction.OpenGameDataBrowser  => vm.OpenGameDataBrowserCommand,
        BuiltInAction.OpenWireInspector    => vm.OpenWireInspectorCommand,
        BuiltInAction.ToggleConnection     => vm.ToggleConnectionCommand,
        BuiltInAction.ToggleCapture        => vm.ToggleDumpCommand,
        BuiltInAction.NewProfile           => vm.NewProfileCommand,
        BuiltInAction.OpenProfile          => vm.OpenProfileCommand,
        BuiltInAction.SaveProfile          => vm.SaveProfileCommand,
        BuiltInAction.SaveProfileAs        => vm.SaveProfileAsCommand,
        BuiltInAction.Quit                 => vm.QuitCommand,
        _                                  => null,
    };
}
