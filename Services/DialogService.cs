using Avalonia.Controls;
using Avalonia.Threading;
using FujinTerm.Views;

namespace FujinTerm.Services;

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

    // Record the application's main window so dialogs can be owned by it. Called
    // once during app startup from App.OnFrameworkInitializationCompleted.
    public void SetMainWindow(Window mainWindow)
    {
        _mainWindow = mainWindow;
    }

    // Register that TViewModel dialogs are hosted by a TWindow. Called once per
    // dialog (typically from AppServices.Initialize or a small bootstrap method).
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
        }

        viewModel.CloseRequested += OnCloseRequested;
        window.Closed += OnWindowClosed;

        window.Show(_mainWindow);
        return tcs.Task;
    }

    // One-shot "show this text" affordance for ad-hoc notices (e.g. "no
    // associated Messages found" from a Spells double-click). Uses the same
    // InfoDialog as the About / License menu entries but without the
    // toggle-tracker — every call opens a fresh window the user dismisses with
    // Close. Returns silently when the main window isn't set (matches the
    // unit-test path).
    public void ShowInfo(string title, string body)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_mainWindow is null) return;

        InfoDialog dlg = new();
        dlg.Configure(title, body);
        dlg.Show(_mainWindow);
    }
}
