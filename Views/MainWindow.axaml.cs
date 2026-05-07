using System.ComponentModel;
using Avalonia.Controls;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

/// <summary>
/// Code-behind for MainWindow.axaml. Wires the terminal control's user-input
/// event to the view-model and re-focuses the terminal whenever a connection
/// is established (so the user can start typing right away).
/// </summary>
public partial class MainWindow : Window
{
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
