using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using FujinTerm.Services;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

/// <summary>
/// Code-behind for MainWindow.axaml. Wires the terminal control's user-input
/// event to the view-model and re-focuses the terminal whenever a connection
/// is established (so the user can start typing right away).
/// </summary>
public partial class MainWindow : Window
{
    private TextBlock? _combatTickLabel;

    public MainWindow()
    {
        InitializeComponent();

        // Forward keystrokes captured by the terminal control to whatever
        // view-model is currently set as DataContext.
        Terminal.UserInput += bytes =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.SendUserInput(bytes);
        };

        // Subscribe to VM PropertyChanged so we can react to IsConnected.
        // Hooking via DataContextChanged covers the case where the VM is
        // swapped at runtime — even though today it's set once in App.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is INotifyPropertyChanged pc)
                pc.PropertyChanged += OnVmPropertyChanged;
        };

        Opened += (_, _) =>
        {
            _combatTickLabel = this.FindControl<TextBlock>("CombatTickLabel");
            AppServices.Current.Tick.CombatTickElapsed += OnCombatTickElapsed;
        };
        Closed += (_, _) =>
        {
            AppServices.Current.Tick.CombatTickElapsed -= OnCombatTickElapsed;
        };

        // Auto-save the loaded profile before exit. ProfileService.Save
        // no-ops on blank drafts (no name on disk to write to) and when
        // nothing is loaded, so the only path that hits disk is the
        // common case: a named profile is open. Saves the current
        // in-memory state so any per-session edits (BBS pin, settings
        // tab changes, etc.) survive a relaunch without requiring the
        // user to remember Ctrl+S.
        Closing += (_, _) =>
        {
            try { AppServices.Current.Profile.Save(); }
            catch (Exception ex)
            {
                AppServices.Current.Log.Error("Profile",
                    $"Auto-save on exit failed: {ex.Message}");
            }
        };
    }

    /// <summary>
    /// Pulse the Tick status-bar label amber for a brief beat each time
    /// TickEngine fires. Class is added immediately, removed after a 200 ms
    /// dispatcher delay so the user gets a visual heartbeat.
    /// </summary>
    private void OnCombatTickElapsed()
    {
        if (_combatTickLabel is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            _combatTickLabel.Classes.Add("Pulsing");
            DispatcherTimer.RunOnce(
                () => _combatTickLabel.Classes.Remove("Pulsing"),
                TimeSpan.FromMilliseconds(200));
        });
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // When we transition into "connected", move keyboard focus to the
        // terminal so typing goes to the BBS instead of the host textbox.
        if (e.PropertyName == nameof(MainWindowViewModel.IsConnected) &&
            DataContext is MainWindowViewModel vm && vm.IsConnected)
        {
            Terminal.Focus();
        }
    }
}
