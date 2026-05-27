using Avalonia.Controls;
using Avalonia.Threading;

namespace FujinTerm.Services;

/// <summary>
/// Modeless-only window spawner. <see cref="OpenWindowAsync{TViewModel,TResult}"/>
/// is the single API — uses <see cref="Window.Show(Window)"/> (not
/// <c>ShowDialog</c>) plus a <see cref="TaskCompletionSource{TResult}"/> that
/// completes when the VM raises <see cref="IDialogViewModel{TResult}.CloseRequested"/>
/// or the user closes the window. No modal wrapper exists — modal-by-mistake
/// is impossible (see CLAUDE.md "All windows are modeless").
/// </summary>
/// <remarks>
/// <para>
/// Each phase that ships a dialog calls <see cref="RegisterWindow{TViewModel,TWindow}"/>
/// once at startup to map its ViewModel type to its Window type. The service
/// news up the Window, sets its <c>DataContext</c> to the supplied VM, parents
/// it to the main window, and shows it.
/// </para>
/// <para>
/// Ownership: every dialog is owned by the main window
/// (<see cref="SetMainWindow"/>) so closing main tears down all open dialogs.
/// Avalonia handles owner tracking when <see cref="Window.Show(Window)"/> is
/// called with an owner argument.
/// </para>
/// <para>
/// Phase 0 ships the plumbing only — no dialogs are registered yet. Later
/// phases register from their bootstrap code; Phase 4 will be the first
/// consumer (settings window).
/// </para>
/// </remarks>
public sealed class DialogService
{
    private readonly Dictionary<Type, Func<Window>> _windowFactories = new();
    private Window? _mainWindow;

    /// <summary>
    /// Record the application's main window so dialogs can be owned by it.
    /// Called once during app startup from <c>App.OnFrameworkInitializationCompleted</c>.
    /// </summary>
    public void SetMainWindow(Window mainWindow)
    {
        _mainWindow = mainWindow;
    }

    /// <summary>
    /// Register that <typeparamref name="TViewModel"/> dialogs are hosted by a
    /// <typeparamref name="TWindow"/>. Each phase calls this once for its
    /// dialogs (typically from <c>AppServices.Initialize</c> or a small phase
    /// bootstrap method).
    /// </summary>
    public void RegisterWindow<TViewModel, TWindow>()
        where TWindow : Window, new()
    {
        _windowFactories[typeof(TViewModel)] = static () => new TWindow();
    }

    /// <summary>
    /// Open a modeless dialog for <paramref name="viewModel"/> and return a
    /// task that completes when the VM signals close (commit returns the
    /// payload; cancel / window-X returns <c>default</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No window type was registered for <typeparamref name="TViewModel"/>,
    /// or <see cref="SetMainWindow"/> was never called.
    /// </exception>
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
}
