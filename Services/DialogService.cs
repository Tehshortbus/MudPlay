using Avalonia.Controls;
using Avalonia.Threading;
using MudPlay.Views;

namespace MudPlay.Services;

// Modeless-only window spawner. OpenWindowAsync is the single API — uses
// Window.Show(Window) (not ShowDialog) plus a TaskCompletionSource that
// completes when the VM raises IDialogViewModel.CloseRequested or the user
// closes the window. No modal wrapper exists — modal-by-mistake is impossible
// (see CLAUDE.md "All windows are modeless").
//
// A dialog registers via RegisterWindow once at startup to map its ViewModel
// type to its Window type. The service news up the Window, sets its DataContext
// to the supplied VM, parents it to the main window, and shows it.
//
// Ownership: every dialog is owned by the main window (SetMainWindow) so closing
// main tears down all open dialogs. Avalonia handles owner tracking when
// Window.Show(Window) is called with an owner argument.
public sealed class DialogService
{
    private readonly Dictionary<Type, Func<Window>> _windowFactories = new();
    private Window? _mainWindow;

    // The window hosting each open dialog, keyed by its view-model, so a caller that holds
    // only the VM can raise its window on a re-press (see RaiseIfOpen).
    private readonly Dictionary<object, Window> _openByViewModel = new(ReferenceEqualityComparer.Instance);

    // Record the application's main window so dialogs can be owned by it. Called
    // once during app startup from App.OnFrameworkInitializationCompleted.
    public void SetMainWindow(Window mainWindow)
    {
        _mainWindow = mainWindow;
    }

    // Register that TViewModel dialogs are hosted by a TWindow. Called once per
    // dialog at startup (App.axaml.cs).
    public void RegisterWindow<TViewModel, TWindow>()
        where TWindow : Window, new()
    {
        _windowFactories[typeof(TViewModel)] = static () => new TWindow();
    }

    // Open a modeless dialog for viewModel and return a task that completes when
    // the VM signals close (commit returns the payload; cancel / window-X
    // returns default). Throws InvalidOperationException when no window type was
    // registered for TViewModel, or SetMainWindow was never called.
    public Task<TResult?> OpenWindowAsync<TViewModel, TResult>(TViewModel viewModel)
        where TViewModel : IDialogViewModel<TResult>
    {
        Dispatcher.UIThread.VerifyAccess();

        if (_mainWindow is null)
            throw new InvalidOperationException(
                "DialogService.SetMainWindow has not been called yet — cannot parent a dialog.");

        if (!_windowFactories.TryGetValue(typeof(TViewModel), out Func<Window>? factory))
            throw new InvalidOperationException(
                $"No window type registered for ViewModel '{typeof(TViewModel).Name}'. " +
                "Call DialogService.RegisterWindow<TViewModel, TWindow>() during startup.");

        Window window = factory();
        window.DataContext = viewModel;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;

        TaskCompletionSource<TResult?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCloseRequested(TResult? result)
        {
            // The VM's request to close also tears down the Window; the Window's
            // own Closed handler will TrySetResult(default) if we haven't beat it.
            tcs.TrySetResult(result);
            window.Close();
        }

        void OnWindowClosed(object? sender, EventArgs e)
        {
            // User closed via the title-bar X (or system close) without the VM
            // raising CloseRequested first. Treat as cancel.
            tcs.TrySetResult(default);
            viewModel.CloseRequested -= OnCloseRequested;
            window.Closed -= OnWindowClosed;
            _openByViewModel.Remove(viewModel);
        }

        viewModel.CloseRequested += OnCloseRequested;
        window.Closed += OnWindowClosed;
        _openByViewModel[viewModel] = window;

        AttachRestoreOwnerOnActivate(window);
        window.Show(_mainWindow);
        return tcs.Task;
    }

    // Raise the window hosting viewModel if it's still open; false when it isn't. Lets a
    // command that opens a dialog bring the existing one forward on a re-press instead of
    // opening a duplicate (CLAUDE.md's raise-to-front rule) — it never closes or commits.
    public bool RaiseIfOpen(object viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (!_openByViewModel.TryGetValue(viewModel, out Window? window)) return false;
        RaiseExisting(window);
        return true;
    }

    // The re-press of a command that opens viewModel's window: close it when it's
    // already in front, bring it forward when it's buried; false when it isn't open.
    public bool RaiseOrCloseIfOpen(object viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (!_openByViewModel.TryGetValue(viewModel, out Window? window)) return false;
        RaiseOrClose(window);
        return true;
    }

    // Bring an open window to the front: un-minimize first (a minimized window can't take
    // focus), then activate. Several Linux WMs won't restack an owned window above its
    // siblings on Activate alone, so a momentary Topmost flip forces the raise (as
    // WindowBehaviors does on click); a genuinely Topmost window is left as it is.
    public static void RaiseExisting(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        if (window.Owner is not null && !window.Topmost)
        {
            window.Topmost = true;
            window.Topmost = false;
        }
        window.Activate();
    }

    // A menu / hotkey / toolbar re-press on an open window: close it when it's already in
    // front, else bring it forward. close is the window's own way out (Settings saves
    // first); plain Close otherwise.
    public static void RaiseOrClose(Window window, Action? close = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (IsInFront(window)) (close ?? window.Close)();
        else RaiseExisting(window);
    }

    // Focused, or — while one of our windows has focus — above every window of ours it
    // overlaps. A hotkey is usually pressed in the terminal, so the window it toggles is
    // in plain view but not focused; focus alone would only ever "raise" it again.
    private static bool IsInFront(Window window)
    {
        if (!window.IsVisible || window.WindowState == WindowState.Minimized) return false;
        if (window.IsActive) return true;
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            return false;
        Window[] shown = desktop.Windows
            .Where(w => w.IsVisible && w.WindowState != WindowState.Minimized)
            .ToArray();
        if (!shown.Any(w => w.IsActive)) return false;   // another app is in front
        Window.SortWindowsByZOrder(shown);                // bottom first
        Avalonia.PixelRect bounds = Bounds(window);
        for (int i = Array.IndexOf(shown, window) + 1; i < shown.Length; i++)
            if (Bounds(shown[i]).Intersects(bounds)) return false;
        return true;
    }

    private static Avalonia.PixelRect Bounds(Window window)
        => new(window.Position, Avalonia.PixelSize.FromSize(window.FrameSize ?? window.ClientSize, window.RenderScaling));

    // Keep an owned modeless child coupled to the main window through a taskbar
    // restore. On some Linux WMs the app-group's last-active window (e.g. the map)
    // is what the taskbar surfaces, while the minimized owner (main window) stays
    // iconified — so the user sees the child but not the main. When the child is
    // activated while the owner is minimized, de-minimize the owner so restoring
    // the client from the taskbar brings the main window with it. No-op during
    // normal use (the owner isn't minimized then).
    private void AttachRestoreOwnerOnActivate(Window window) =>
        window.Activated += (_, _) =>
        {
            if (_mainWindow is { WindowState: WindowState.Minimized } m)
                m.WindowState = WindowState.Normal;
        };

    // One-shot "show this text" affordance for ad-hoc notices (e.g. "no
    // associated Messages found" from a Spells double-click). Uses the same
    // InfoDialog as the About / License menu entries but without the
    // toggle-tracker — every call opens a fresh window the user dismisses with
    // Close. Returns silently when the main window isn't set.
    public void ShowInfo(string title, string body)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_mainWindow is null) return;

        InfoDialog dlg = new();
        dlg.Configure(title, body);
        AttachRestoreOwnerOnActivate(dlg);
        dlg.Show(_mainWindow);
    }
}
