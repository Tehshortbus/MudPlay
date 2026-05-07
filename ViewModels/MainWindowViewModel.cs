using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Net;
using FujinTerm.Terminal;

namespace FujinTerm.ViewModels;

/// <summary>
/// View-model for the main window. Owns the terminal emulator and the
/// active Telnet connection, and exposes the bindable state and commands
/// the XAML uses (host, port, status text, Connect / Disconnect / Dump
/// buttons).
///
/// CommunityToolkit.Mvvm source-generators expand each [ObservableProperty]
/// backing field into a public property with INotifyPropertyChanged change
/// notification, and each [RelayCommand] async method into an ICommand
/// suitable for binding directly to a button.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private TelnetClient? _telnet;

    /// <summary>The screen buffer the UI renders. Lifetime spans the whole window.</summary>
    public TerminalEmulator Emulator { get; } = new(80, 25);

    [ObservableProperty]
    private string _host = "playpenbbs.com";

    [ObservableProperty]
    private int _port = 23;

    // IsConnected is the canonical state. The other two attributes here keep
    // the inverse "IsDisconnected" property and the two commands' CanExecute
    // in lockstep — the toolkit re-evaluates them on every change.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    private bool _isConnected;

    public bool IsDisconnected => !IsConnected;

    [ObservableProperty]
    private string _statusText = "Disconnected.";

    [ObservableProperty]
    private bool _isDumping;

    // Where the "Dump" button writes session captures. Hardcoded to the
    // project root for now; change here if you want them elsewhere.
    private const string DumpDirectory = "/home/fujin/Desktop/Projects/FujinTerm";

    public MainWindowViewModel()
    {
        // The emulator emits replies (DSR, DA) it needs sent back to the
        // host; forward those onto the live telnet connection if any.
        Emulator.ResponseReady += bytes =>
        {
            var t = _telnet;
            if (t is not null) _ = t.SendAsync(bytes);
        };
    }

    /// <summary>Bound to the Connect button.</summary>
    [RelayCommand(CanExecute = nameof(IsDisconnected))]
    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(Host) || Port <= 0)
        {
            StatusText = "Enter host and port.";
            return;
        }

        var client = new TelnetClient
        {
            Cols = Emulator.Screen.Cols,
            Rows = Emulator.Screen.Rows,
            TerminalType = "ansi-bbs",
        };

        // Telnet client events fire on a background thread. Marshal anything
        // that touches UI state through the dispatcher so bindings stay safe.
        client.DataReceived += data =>
        {
            // Copy out of the rented buffer because the emitter may reuse it
            // for the next read before our UI-thread post runs.
            var copy = data.ToArray();
            Dispatcher.UIThread.Post(() => Emulator.Feed(copy));
        };
        client.Connected += () =>
            Dispatcher.UIThread.Post(() => { IsConnected = true; StatusText = $"Connected to {Host}:{Port}"; });
        client.Disconnected += () =>
            Dispatcher.UIThread.Post(() => { IsConnected = false; StatusText = "Disconnected."; });
        client.Log += msg => Dispatcher.UIThread.Post(() => StatusText = msg);

        try
        {
            StatusText = $"Connecting to {Host}:{Port}…";
            await client.ConnectAsync(Host, Port);
            _telnet = client;
        }
        catch (Exception ex)
        {
            StatusText = $"Connect failed: {ex.Message}";
            await client.DisposeAsync();
        }
    }

    /// <summary>Bound to the Disconnect button.</summary>
    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task DisconnectAsync()
    {
        var t = _telnet;
        _telnet = null;
        if (t is not null) await t.DisposeAsync();
        IsConnected = false;
        StatusText = "Disconnected.";
    }

    /// <summary>
    /// Send raw key bytes from the terminal control to the server. Called
    /// by the view's UserInput handler; no-op if not connected.
    /// </summary>
    public void SendUserInput(byte[] data)
    {
        var t = _telnet;
        if (t is not null) _ = t.SendAsync(data);
    }

    /// <summary>
    /// Toggle session capture. When enabled, every cleaned byte from the
    /// host is appended to a timestamped .bin file in <see cref="DumpDirectory"/>.
    /// </summary>
    [RelayCommand]
    private void ToggleDump()
    {
        var t = _telnet;
        if (IsDumping)
        {
            t?.StopDump();
            IsDumping = false;
            StatusText = "Dump stopped.";
            return;
        }
        if (t is null)
        {
            StatusText = "Connect before starting a dump.";
            return;
        }
        var name = $"dump-{DateTime.Now:yyyyMMdd-HHmmss}.bin";
        var path = Path.Combine(DumpDirectory, name);
        try
        {
            t.StartDump(path);
            IsDumping = true;
            StatusText = $"Dumping to {name}";
        }
        catch (Exception ex)
        {
            StatusText = $"Dump failed: {ex.Message}";
        }
    }
}
