using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FujinTerm.ViewModels;

// Modeless dialog VM for File → Quick Connect. Takes a free-form host (DNS
// name or IP) plus a port, then raises ConnectRequested when the user clicks
// Connect. No persistence — the values stay in memory on the main window's
// quick-connect target and disappear when the user dials somewhere else.
public sealed partial class QuickConnectViewModel : ObservableObject
{
    // Raised when the user commits — host + port are read off the VM directly.
    public event System.Action? ConnectRequested;

    // Raised when the user cancels / closes the dialog without committing.
    public event System.Action? Cancelled;

    // Host text — IPv4, IPv6, or DNS name. No validation past "non-empty".
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _hostText = string.Empty;

    // TCP port. Default 23 (the standard telnet port).
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private int _port = 23;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void Connect() => ConnectRequested?.Invoke();

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();

    private bool CanConnect()
        => !string.IsNullOrWhiteSpace(HostText) && Port is > 0 and < 65536;
}
