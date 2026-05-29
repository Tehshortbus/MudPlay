using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FujinTerm.ViewModels;

/// <summary>
/// Modeless dialog VM for File → Quick Connect. Takes a free-form
/// host (DNS name or IP) plus a port, then raises
/// <see cref="ConnectRequested"/> when the user clicks Connect. No
/// persistence — the values stay in memory on the main window's
/// quick-connect target and disappear when the user dials somewhere
/// else.
/// </summary>
public sealed partial class QuickConnectViewModel : ObservableObject
{
    /// <summary>Raised when the user commits — host + port are read off the VM directly.</summary>
    public event System.Action? ConnectRequested;

    /// <summary>Raised when the user cancels / closes the dialog without committing.</summary>
    public event System.Action? Cancelled;

    /// <summary>Host text — IPv4, IPv6, or DNS name. No validation past "non-empty".</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _hostText = string.Empty;

    /// <summary>TCP port. Default 23 (the standard telnet port).</summary>
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
