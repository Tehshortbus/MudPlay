using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

// Adds the app-wide window-toggle hotkeys (now sourced from
// KeybindingStore) to a child window so re-pressing the hotkey closes
// the open window — the toggle convention. Avalonia's window-level
// KeyBindings only fire when that window has focus, so without this
// every panel had its own focus surface and the hotkey toggle was
// unreachable.
//
// On KeybindingStore.BindingChanged the bindings for every attached
// window get rebuilt — keeping the runtime in sync with whatever the
// user just rebound. The earlier hardcoded literal-gestures approach
// drifted any time the store was edited.
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

    // Compose the keybindings on MainWindow itself — called once from
    // MainWindow's ctor so the main window picks up the same store-driven
    // shortcuts as the child panels. The XAML KeyBinding literals
    // previously here have been removed in favour of this one source of
    // truth.
    //
    // DataContext is set by App.OnFrameworkInitializationCompleted *after*
    // the ctor runs, so we can't Rebuild synchronously. We hook two events
    // to cover both orderings: Window.Opened (definitive — fires after
    // Show, by which time DataContext is set) and
    // StyledElement.DataContextChanged (covers the case where DataContext
    // is swapped post-Show, currently unused but cheap insurance).
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
        // Single-action rebind and bulk reload (profile load / close)
        // both drive the same "rebuild all attached windows" loop.
        AppServices.Current.Keybindings.BindingChanged  += _ => RebuildAll();
        AppServices.Current.Keybindings.BindingsReloaded += RebuildAll;
    }

    private static void RebuildAll()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
            { MainWindow.DataContext: MainWindowViewModel vm } _) return;
        for (int i = _attached.Count - 1; i >= 0; i--)
        {
            if (_attached[i].TryGetTarget(out Window? w)) Rebuild(w, vm);
            else _attached.RemoveAt(i);
        }
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

    // Resolve the MainWindow VM command a BuiltInAction fires.
    // Toolbar/Action-menu actions go through the catalogue's
    // ToolbarItemCatalogue.Entry.CommandName — the same dynamic
    // command-name binding the live toolbar uses — so any catalogue entry
    // (including ones added later) becomes keybindable with no change here.
    // An unwired stub command (property absent on the VM) resolves to null,
    // making the chord a silent no-op that matches how the live toolbar
    // renders those buttons disabled. File-menu actions aren't toolbar
    // items, so they fall back to an explicit name map.
    private static System.Windows.Input.ICommand? ResolveCommand(MainWindowViewModel vm, BuiltInAction action)
    {
        string? commandName = ToolbarItemCatalogue.Find(action.ToString())?.CommandName
                              ?? FileMenuCommandName(action);
        if (commandName is null) return null;
        return vm.GetType().GetProperty(commandName)?.GetValue(vm) as System.Windows.Input.ICommand;
    }

    // Command-property names for the File-menu actions that have no toolbar catalogue entry.
    private static string? FileMenuCommandName(BuiltInAction action) => action switch
    {
        BuiltInAction.NewProfile    => "NewProfileCommand",
        BuiltInAction.OpenProfile   => "OpenProfileCommand",
        BuiltInAction.SaveProfile   => "SaveProfileCommand",
        BuiltInAction.SaveProfileAs => "SaveProfileAsCommand",
        BuiltInAction.Quit          => "QuitCommand",
        _                           => null,
    };
}
