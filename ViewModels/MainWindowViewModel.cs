using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Net;
using FujinTerm.Services;
using FujinTerm.Terminal;
using FujinTerm.Views;

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
    [NotifyPropertyChangedFor(nameof(CaptureMenuLabel))]
    private bool _isDumping;

    /// <summary>Label shown on the Tools menu's capture-toggle entry.</summary>
    public string CaptureMenuLabel => IsDumping ? "Stop capture" : "Start capture";

    // Where session captures land when the user toggles capture. Stays under
    // the user's Data/Logs folder so it's covered by the same rotation policy
    // as DebugLogWriter output.
    private static string CaptureDirectory => AppPaths.LogsDir;

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
        var name = $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.fjtc";
        var path = Path.Combine(CaptureDirectory, name);
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

    /// <summary>Bound to File → Quit.</summary>
    [RelayCommand]
    private void Quit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    // ----- Polish commands (Phase 0 PR 0.11) -----------------------------

    /// <summary>View → Reset layout. Restores every panel to docked default.</summary>
    [RelayCommand]
    private void ResetLayout() => AppServices.Current.Panels.ResetToDefault();

    /// <summary>Tools → Open Logs folder… and Help → Open Logs folder…</summary>
    [RelayCommand]
    private void OpenLogsFolder()
    {
        if (!ShellLaunch.OpenPath(AppPaths.LogsDir))
            StatusText = $"Could not open {AppPaths.LogsDir}";
    }

    /// <summary>Help → Help topics… Opens the dev <c>docs/</c> folder when present.</summary>
    [RelayCommand]
    private void OpenHelpTopics()
    {
        string? docs = AppInfo.TryFindDocsFolder();
        if (docs is not null)
        {
            ShellLaunch.OpenPath(docs);
            return;
        }
        // Shipped builds don't carry docs/ — fall back to the repo readme.
        if (!ShellLaunch.OpenUrl(AppInfo.RepoUrl))
            StatusText = "Could not open help.";
    }

    [RelayCommand]
    private void OpenMajorMudWiki() => ShellLaunch.OpenUrl(AppInfo.MajorMudWikiUrl);

    [RelayCommand]
    private void OpenMajorMudReddit() => ShellLaunch.OpenUrl(AppInfo.MajorMudRedditUrl);

    [RelayCommand]
    private void ReportIssue() => ShellLaunch.OpenUrl(AppInfo.IssuesUrl);

    /// <summary>Help → Keyboard shortcuts… Opens a modeless info dialog.</summary>
    [RelayCommand]
    private void OpenKeyboardShortcuts()
        => ShowInfoDialog("Keyboard shortcuts — FujinTerm",
            """
            Connect ......................... Ctrl+K
            Disconnect ...................... Ctrl+D
            Quit ............................ Ctrl+Q

            View
              Conversation .................. F2  (wired Phase 2)
              Party ......................... F3  (wired Phase 6)
              Player Status ................. F4  (wired Phase 3)
              Map ........................... F5  (wired Phase 7)
              Workshop ...................... F6  (wired Phase 9)
              Spell Book .................... F7  (wired Phase 9)
              Log ........................... F9  (wired Phase 1)
              Backscroll .................... F10 (wired Phase 1)
              Session Stats ................. F11 (wired Phase 8)

            Settings ........................ Ctrl+,  (Phase 4)
            Open Game Data browser .......... Ctrl+G  (Phase 5)
            New / Open / Save profile ....... Ctrl+N / Ctrl+O / Ctrl+S  (Phase 4)

            Help topics ..................... F1  (this dialog's neighbor)

            More entries land as each phase wires its feature.
            """);

    /// <summary>Help → License… Project + third-party license summary.</summary>
    [RelayCommand]
    private void OpenLicense()
        => ShowInfoDialog("Licenses — FujinTerm",
            """
            FujinTerm is open source. See the LICENSE file in the project root
            for the full text.

            Third-party components used in this build:

              • Avalonia UI                — MIT
              • CommunityToolkit.Mvvm       — MIT
              • System.Data.OleDb           — MIT (Phase 5 MDB import)

            Other dependencies arrive with their respective phases; their
            licenses will appear here once they're added.
            """);

    /// <summary>Help → About FujinTerm.</summary>
    [RelayCommand]
    private void OpenAbout()
        => ShowInfoDialog("About FujinTerm",
            $"""
            {AppInfo.DisplayName}
            A modern Avalonia BBS terminal client with MajorMUD-aware features.

            Source: {AppInfo.RepoUrl}

            Built on .NET 10 + Avalonia 12 (CommunityToolkit.Mvvm source-gen).
            """);

    private static void ShowInfoDialog(string title, string body)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;
        InfoDialog dlg = new();
        dlg.Configure(title, body);
        dlg.Show(main);
    }

    /// <summary>
    /// Bound to Tools → Replay capture file… Opens a file picker, then feeds
    /// every chunk in the chosen <c>.fjtc</c> into the live emulator at the
    /// recorded cadence. Fire-and-forget — the playback runs on a background
    /// task and marshals each chunk through the dispatcher.
    /// </summary>
    [RelayCommand]
    private async Task ReplayCaptureFileAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
        {
            StatusText = "Replay unavailable — no main window.";
            return;
        }

        IReadOnlyList<IStorageFile> picked = await main.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open FujinTerm capture",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("FujinTerm capture (.fjtc)") { Patterns = ["*.fjtc"] },
                FilePickerFileTypes.All,
            ],
            SuggestedStartLocation = await main.StorageProvider.TryGetFolderFromPathAsync(AppPaths.LogsDir),
        });

        if (picked.Count == 0) return;
        string path = picked[0].Path.LocalPath;

        ReplayPlayer player = new(path);
        player.ChunkPlayed += chunk =>
        {
            byte[] copy = chunk.ToArray();
            Dispatcher.UIThread.Post(() => Emulator.Feed(copy));
        };

        StatusText = $"Replaying {Path.GetFileName(path)}…";
        _ = Task.Run(async () =>
        {
            try
            {
                await player.PlayAsync().ConfigureAwait(false);
                Dispatcher.UIThread.Post(() => StatusText = "Replay complete.");
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => StatusText = $"Replay failed: {ex.Message}");
            }
        });
    }
}
