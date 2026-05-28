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

    /// <summary>
    /// Extracts completed lines from the emulator's screen stream. Foundation
    /// for every later-phase "what did the server say" subsystem
    /// (MessageRouter, ChatRouter, Triggers, prompt parser).
    /// </summary>
    public LineExtractor Lines { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BbsBadgeText))]
    private string _host = "playpenbbs.com";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BbsBadgeText))]
    private int _port = 23;

    /// <summary>Label shown in the toolbar's BBS badge ("host:port" by skeleton design).</summary>
    public string BbsBadgeText => $"{Host}:{Port}";

    // Connection state is a small FSM: Idle → Connecting → Connected → Idle.
    // The single ToggleConnectionCommand drives every transition; everything
    // else (button visuals, menu label, status badge) reads off IsConnected
    // + IsConnecting.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel))]
    private bool _isConnecting;

    public bool IsDisconnected => !IsConnected;

    /// <summary>True when there is no active connection AND no connect attempt in flight.</summary>
    public bool IsIdle => !IsConnected && !IsConnecting;

    /// <summary>
    /// Header text for the single Connect ↔ Disconnect menu entry / button
    /// tooltip. Three-state cycle: Idle → "Connect" → Connecting → "Cancel
    /// connect" → Connected → "Disconnect".
    /// </summary>
    public string ConnectionLabel
        => IsConnected ? "Disconnect"
         : IsConnecting ? "Cancel connect"
         : "Connect";

    /// <summary>
    /// Cancels an in-flight connect attempt — covers both the socket-level
    /// <see cref="TelnetClient.ConnectAsync"/> and the inter-attempt
    /// <see cref="Task.Delay"/>. Cleared in the finally block.
    /// </summary>
    private CancellationTokenSource? _connectCts;

    /// <summary>
    /// Maximum number of connect attempts (initial + retries). Phase 4
    /// Settings.BBS will surface the knob (issue #6); until then it's a constant.
    /// </summary>
    private const int MaxConnectAttempts = 3;

    /// <summary>
    /// Wait time between connect attempts. Phase 4 Settings.BBS will surface
    /// the knob (issue #6).
    /// </summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Per-attempt socket timeout. The OS default (~75s on Linux for
    /// unreachable hosts) is far too long for a BBS client. Phase 4
    /// Settings.BBS will surface the knob (issue #6).
    /// </summary>
    private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Most recent message in <see cref="AppServices.Log"/> — drives the
    /// status bar's right slot. The MainWindow surface gives "latest log
    /// line" preview here; the full filterable view lives in the Log
    /// pane (F9).
    /// </summary>
    public string LatestLogText => AppServices.Current.Log.Latest?.Message ?? string.Empty;

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
        Lines = new LineExtractor(Emulator);

        // The emulator emits replies (DSR, DA) it needs sent back to the
        // host; forward those onto the live telnet connection if any.
        Emulator.ResponseReady += bytes =>
        {
            var t = _telnet;
            if (t is not null) _ = t.SendAsync(bytes);
        };

        // Status bar's right slot follows LogService.Latest. Posting through
        // the dispatcher because EntryAdded fires on the producer's thread
        // (Telnet read loop, automation engines, etc.) and the bound UI
        // property must change on the UI thread.
        AppServices.Current.Log.EntryAdded += OnLogEntryAdded;
    }

    private void OnLogEntryAdded(Services.LogEntry _)
        => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(LatestLogText)));

    /// <summary>
    /// Single Connect ↔ Disconnect action. Click while idle starts a
    /// connect attempt (with auto-retry on failure); click while a connect
    /// is in flight cancels it; click while connected disconnects.
    /// </summary>
    /// <remarks>
    /// <c>AllowConcurrentExecutions = true</c> matters: CommunityToolkit.Mvvm's
    /// default <c>AsyncRelayCommand</c> behaviour is to disable the command
    /// while the task is running, which would mean a second click during a
    /// long-running connect attempt does nothing — the cancel path would be
    /// unreachable. With concurrent executions allowed, the second click
    /// re-enters this method and hits the <c>IsConnecting</c> branch, which
    /// cancels the in-flight attempt.
    /// </remarks>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleConnectionAsync()
    {
        if (IsConnected)        { await DisconnectInternalAsync();   return; }
        if (IsConnecting)       { _connectCts?.Cancel();             return; }
        await ConnectWithRetriesAsync();
    }

    private async Task DisconnectInternalAsync()
    {
        TelnetClient? t = _telnet;
        _telnet = null;
        if (t is not null) await t.DisposeAsync();
        IsConnected = false;

        WriteTerminalStatus($"[DISCONNECTED FROM: {Host} {Port}]", TerminalStatusKind.Notice);
        AppServices.Current.Log.Info("Telnet", $"Disconnected from {Host}:{Port}");
    }

    private async Task ConnectWithRetriesAsync()
    {
        if (string.IsNullOrWhiteSpace(Host) || Port <= 0)
        {
            WriteTerminalStatus("[NO HOST / PORT SET — EDIT THE FIELDS BELOW THE TOOLBAR.]",
                                TerminalStatusKind.Error);
            AppServices.Current.Log.Warn("Connect", "Enter host and port.");
            return;
        }

        _connectCts = new CancellationTokenSource();
        IsConnecting = true;
        try
        {
            for (int attempt = 1; attempt <= MaxConnectAttempts; attempt++)
            {
                if (_connectCts.IsCancellationRequested) break;

                WriteTerminalStatus($"[CONNECTING TO: {Host} {Port}]", TerminalStatusKind.Notice);
                AppServices.Current.Log.Info("Connect",
                    $"Connecting to {Host}:{Port} (attempt {attempt}/{MaxConnectAttempts})…");

                TelnetClient client = BuildTelnetClient();

                // Per-attempt CTS: linked to the user-cancel token AND a
                // ConnectAttemptTimeout so a dead host doesn't make us wait
                // ~75 seconds for the OS to give up.
                using CancellationTokenSource attemptCts =
                    CancellationTokenSource.CreateLinkedTokenSource(_connectCts.Token);
                attemptCts.CancelAfter(ConnectAttemptTimeout);

                bool attemptFailed = false;
                try
                {
                    await client.ConnectAsync(Host, Port, attemptCts.Token);
                    _telnet = client;
                    return;  // success — IsConnected flips via Connected event handler.
                }
                catch (OperationCanceledException) when (_connectCts.IsCancellationRequested)
                {
                    // User clicked the toolbar / menu again — propagate as cancel.
                    await client.DisposeAsync();
                    WriteTerminalStatus("[CONNECT CANCELLED]", TerminalStatusKind.Notice);
                    AppServices.Current.Log.Info("Connect", "Connect cancelled.");
                    return;
                }
                catch (OperationCanceledException)
                {
                    // Timeout fired (attemptCts but not _connectCts).
                    await client.DisposeAsync();
                    int seconds = (int)ConnectAttemptTimeout.TotalSeconds;
                    WriteTerminalStatus($"[CONNECTION FAILED: timed out after {seconds}s]",
                                        TerminalStatusKind.Error);
                    AppServices.Current.Log.Error("Connect",
                        $"Attempt {attempt} timed out after {seconds}s.");
                    attemptFailed = true;
                }
                catch (Exception ex)
                {
                    await client.DisposeAsync();
                    WriteTerminalStatus($"[CONNECTION FAILED: {ex.Message}]", TerminalStatusKind.Error);
                    AppServices.Current.Log.Error("Connect", $"Attempt {attempt} failed: {ex.Message}");
                    attemptFailed = true;
                }

                if (attemptFailed && attempt < MaxConnectAttempts)
                {
                    int seconds = (int)RetryDelay.TotalSeconds;
                    WriteTerminalStatus($"[RETRYING IN: {seconds} SECONDS...]",
                                        TerminalStatusKind.Notice);
                    try
                    {
                        await Task.Delay(RetryDelay, _connectCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        WriteTerminalStatus("[CONNECT CANCELLED]", TerminalStatusKind.Notice);
                        AppServices.Current.Log.Info("Connect", "Connect cancelled.");
                        return;
                    }
                }
            }

            WriteTerminalStatus($"[GIVING UP AFTER {MaxConnectAttempts} ATTEMPTS.]",
                                TerminalStatusKind.Error);
            AppServices.Current.Log.Error("Connect",
                $"Gave up after {MaxConnectAttempts} attempts.");
        }
        finally
        {
            IsConnecting = false;
            _connectCts?.Dispose();
            _connectCts = null;
        }
    }

    private TelnetClient BuildTelnetClient()
    {
        TelnetClient client = new()
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
            byte[] copy = data.ToArray();
            // Feed the Wire Inspector buffer too — the post-IAC stream is
            // what the parser sees, which is exactly what the debug window
            // wants to surface.
            AppServices.Current.Wire.Append(copy);
            Dispatcher.UIThread.Post(() => Emulator.Feed(copy));
        };
        client.Connected += () =>
        {
            AppServices.Current.Log.Info("Telnet", $"Connected to {Host}:{Port}");
            Dispatcher.UIThread.Post(() => IsConnected = true);
        };
        client.Disconnected += () =>
        {
            // Don't log here; DisconnectInternalAsync already did, and a
            // server-initiated drop will fire this too.
            Dispatcher.UIThread.Post(() => IsConnected = false);
        };
        // TelnetClient's Log event carries IAC negotiation trace lines;
        // route them into LogService at Debug severity so the Log pane can
        // surface them when DBG is checked and the status bar gets the
        // latest via LatestLogText.
        client.Log += msg => AppServices.Current.Log.Debug("Telnet", msg);

        return client;
    }

    private enum TerminalStatusKind { Notice, Error }

    /// <summary>
    /// Write a single bracketed status line into the terminal canvas itself
    /// (in addition to LogService). Mirrors the classic-BBS-client cadence
    /// the user expects: "[CONNECTING TO: …]" / "[DISCONNECTED FROM: …]" /
    /// etc. Coloured via inline ANSI SGR so the emulator does the painting.
    /// </summary>
    private void WriteTerminalStatus(string text, TerminalStatusKind kind)
    {
        string sgr = kind switch
        {
            TerminalStatusKind.Notice => "\x1b[33;1m",   // bright yellow
            TerminalStatusKind.Error  => "\x1b[31;1m",   // bright red
            _ => string.Empty,
        };
        string line = $"\r\n{sgr}{text}\x1b[0m\r\n";
        byte[] bytes = System.Text.Encoding.Latin1.GetBytes(line);
        Emulator.Feed(bytes);
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
            AppServices.Current.Log.Info("Capture", "Capture stopped.");
            return;
        }
        if (t is null)
        {
            AppServices.Current.Log.Warn("Capture", "Connect before starting a capture.");
            return;
        }
        var name = $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.fjtc";
        var path = Path.Combine(CaptureDirectory, name);
        try
        {
            t.StartDump(path);
            IsDumping = true;
            AppServices.Current.Log.Info("Capture", $"Capturing to {name}");
        }
        catch (Exception ex)
        {
            AppServices.Current.Log.Error("Capture", $"Capture failed: {ex.Message}");
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

    // ----- Placeholder shell-window plumbing -----------------------------

    /// <summary>
    /// Tracks one open placeholder per panel id so re-opening a panel from
    /// the menu / toolbar activates the existing window instead of stacking
    /// duplicates. Cleared by each window's <c>Closed</c> handler.
    /// </summary>
    private readonly Dictionary<string, PlaceholderShellWindow> _placeholders = new();

    private void OpenPlaceholder(string id, string panelName, string phaseTag, string headline, string description)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_placeholders.TryGetValue(id, out PlaceholderShellWindow? existing))
        {
            existing.Activate();
            return;
        }

        PlaceholderShellWindow window = new();
        window.Configure(panelName, phaseTag, headline, description);
        window.Closed += (_, _) => _placeholders.Remove(id);
        _placeholders[id] = window;
        window.Show(main);
    }

    // Singleton handle for the live LogPaneWindow — re-opening from menu or
    // toolbar activates the existing window instead of stacking duplicates.
    private LogPaneWindow? _logPane;

    [RelayCommand]
    private void OpenLogPane()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_logPane is { } existing)
        {
            existing.Activate();
            return;
        }

        LogPaneWindow window = new()
        {
            DataContext = new LogPaneViewModel(AppServices.Current.Log, Application.Current),
        };
        window.Closed += (_, _) => _logPane = null;
        _logPane = window;
        window.Show(main);
    }

    private BackscrollWindow? _backscroll;

    [RelayCommand]
    private void OpenBackscroll() => OpenBackscrollInternal(focusSearch: false);

    /// <summary>
    /// Terminal context menu → Edit → Find in scrollback. Opens the backscroll
    /// window (or activates it if already open) and lands focus on the search
    /// box so the user can type immediately.
    /// </summary>
    [RelayCommand]
    private void FindInScrollback() => OpenBackscrollInternal(focusSearch: true);

    private void OpenBackscrollInternal(bool focusSearch)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_backscroll is { } existing)
        {
            existing.Activate();
            if (focusSearch && existing.DataContext is BackscrollViewModel existingVm)
            {
                existingVm.FocusSearchOnOpen = true;
                // Already-opened window: nudge focus via the same hook the
                // first-open path uses, by toggling activation. Simpler than
                // exposing yet another event.
                existing.Focus();
            }
            return;
        }

        BackscrollViewModel vm = new(Emulator.Screen.Scrollback)
        {
            FocusSearchOnOpen = focusSearch,
        };
        BackscrollWindow window = new() { DataContext = vm };
        window.Closed += (_, _) => _backscroll = null;
        _backscroll = window;
        window.Show(main);
    }

    [RelayCommand]
    private void OpenConversation()
        => OpenPlaceholder(
            id: "conversation",
            panelName: "Conversation",
            phaseTag: "Phase 2",
            headline: "Chat / telepath / gossip view",
            description:
                "Single window with per-message-type filter toggles (Gossip / " +
                "Telepath / Gang / Say / Broadcast / System / Realm-events) and its " +
                "own input field that routes to the live game. Backed by ChatRouter + " +
                "the app-scoped ChatHistoryStore.");

    [RelayCommand]
    private void OpenParty()
        => OpenPlaceholder(
            id: "party",
            panelName: "Party",
            phaseTag: "Phase 6",
            headline: "Party tracker",
            description:
                "Leader at top, HP / MA bars per member, leader-star highlight. " +
                "Driven by PartyManager (par-poller + follows-you / stops-following " +
                "pattern matchers). Compact and detail modes.");

    [RelayCommand]
    private void OpenSettings()
        => OpenPlaceholder(
            id: "settings",
            panelName: "Settings",
            phaseTag: "Phase 4",
            headline: "Settings hub — sixteen tabs",
            description:
                "Sidebar-tree navigation (General / Display / Comms / BBS / Toolbar / " +
                "Statline / Auto-Lair / Talk / Health / Spells / Combat / PvP / Party / " +
                "Cash / Sounds / Other / Events). Scope selector + per-setting tier " +
                "picker (installed defaults / for all characters / only for this BBS / " +
                "only for this character). Apply / OK / Cancel commit model.");

    [RelayCommand]
    private void OpenGameDataBrowser()
        => OpenPlaceholder(
            id: "game-data",
            panelName: "Game Data Browser",
            phaseTag: "Phase 5",
            headline: "MDB-imported tables + user overrides",
            description:
                "Tabs for Monsters / Items / Spells / Spell Messages / Conditions / " +
                "Triggers / Rooms / Paths / Lairs / Shops / Races / Classes / " +
                "TextBlocks / Players / Favorites / Macros. Per-record tier picker. " +
                "Unified inline Spell + Spell-Messages editor.");

    [RelayCommand]
    private void OpenNavigation()
        => OpenPlaceholder(
            id: "navigation",
            panelName: "Navigation",
            phaseTag: "Phase 7",
            headline: "Map + walk + loops + Auto-Lair",
            description:
                "Single unified window. Always-visible map (BFS planar layout from " +
                "MDB Rooms+Paths). Left rail: room tree, favorites, saved loops. " +
                "Trust-by-default RoomTracker; walk-from-anywhere; Auto-Lair " +
                "scheduler with entry-triggered respawn + wait-room logic.");

    [RelayCommand]
    private void OpenSpellBook()
        => OpenPlaceholder(
            id: "spell-book",
            panelName: "Spell Book",
            phaseTag: "Phase 9",
            headline: "Click-to-cast spell list",
            description:
                "MegaMUD-parity columns: level / mana / code / name / abilities. " +
                "Re-Check button to re-fetch from the server. Filterable. Driven by " +
                "the active game-data set's Spells table merged with character " +
                "overrides.");

    [RelayCommand]
    private void OpenSessionStats()
        => OpenPlaceholder(
            id: "session-stats",
            panelName: "Session Stats",
            phaseTag: "Phase 8",
            headline: "Observed combat / time / session counters",
            description:
                "Player Statistics (observed Miss / Hit / Crit / BS / sneak / dodge " +
                "rates), Time Analysis (moving / attacking / resting), Session " +
                "Statistics (online time, monsters killed, exp earned). Plus kills/hr " +
                "sparkline. Counters reset on connect.");

    [RelayCommand]
    private void OpenWorkshop()
        => OpenPlaceholder(
            id: "workshop",
            panelName: "Character Workshop",
            phaseTag: "Phase 9",
            headline: "Unified character hub — six section groups",
            description:
                "STATS (Sheet / CP Alloc / Builds / Character Planner) — PROGRESS " +
                "(Levels / EXP-CP / Spells) — EQUIP (Slots / Sets+Triggers / Find " +
                "Items) — COMBAT (Preview) — QUESTS — DEATH. Absorbs the old Player " +
                "Status panel into STATS → Status; View → Player Status (F4) opens " +
                "Workshop on that section.");

    // ----- Edit-dialog placeholders --------------------------------------
    // Each later-phase window opens its own row-editor when the user clicks
    // into a list. Until those windows ship real rows, the editor placeholders
    // are reachable only via Tools → Preview placeholder dialogs ▶.

    [RelayCommand]
    private void PreviewSpellEditDialog()
        => OpenPlaceholder(
            id: "dialog-spell-edit",
            panelName: "Spell — Edit",
            phaseTag: "Phase 5",
            headline: "Spell + linked Spell-Messages editor",
            description:
                "Inline two-pane layout (key UX improvement over MegaMUD): spell " +
                "fields on the left, list of linked match-message patterns on the " +
                "right with Add / Edit / Remove. Tier picker on every editable " +
                "field — installed defaults / for all characters / only for this " +
                "BBS / only for this character.");

    [RelayCommand]
    private void PreviewTriggerEditDialog()
        => OpenPlaceholder(
            id: "dialog-trigger-edit",
            panelName: "Trigger — Edit",
            phaseTag: "Phase 5",
            headline: "User-defined pattern → action",
            description:
                "Match-type picker (Literal / Wildcard / Regex), pattern field, " +
                "scope (any line / chat-only / system-only), named capture groups → " +
                "session variables, multi-action list (Send command / Show " +
                "notification / Play sound / Set variable).");

    [RelayCommand]
    private void PreviewAliasEditDialog()
        => OpenPlaceholder(
            id: "dialog-alias-edit",
            panelName: "Alias — Edit",
            phaseTag: "Phase 5",
            headline: "Command-substitution alias",
            description:
                "Short-form to full command-string expansion. Per UI-design-spec " +
                "§9b. Lives alongside Triggers in the Game Data browser; tier-aware " +
                "via the standard 4-tier hierarchy.");

    [RelayCommand]
    private void PreviewConditionEditDialog()
        => OpenPlaceholder(
            id: "dialog-condition-edit",
            panelName: "Condition — Edit",
            phaseTag: "Phase 5",
            headline: "Non-spell condition pattern + effect flags",
            description:
                "Blinded / poisoned / paralyzed / confused / diseased / regenerating " +
                "/ etc. Pattern + bitfield of which behaviours the condition flips on " +
                "(ignore / recheck / wait / rest-hp / rest-mana / don't-rest-run / " +
                "hangup). Consumed by Phase 13 automation engines.");

    [RelayCommand]
    private void PreviewMacroEditDialog()
        => OpenPlaceholder(
            id: "dialog-macro-edit",
            panelName: "Macro — Edit",
            phaseTag: "Phase 10",
            headline: "Keybind → command string",
            description:
                "Capture-key button, command field with $variable substitution " +
                "(shares the Trigger user-variable system), conflict warning row " +
                "when the gesture collides with a built-in. Excluded keys (Enter, " +
                "Esc, Tab, Backspace, Alt+F4) are blocked.");

    [RelayCommand]
    private void PreviewEventEditDialog()
        => OpenPlaceholder(
            id: "dialog-event-edit",
            panelName: "Event — Edit",
            phaseTag: "Phase 11",
            headline: "Scheduled / lifecycle event",
            description:
                "Trigger types: AtTime (HH:MM) / Every (s/m/h) / Logon / Logoff / " +
                "Re-log. Action types: Send command / Run macro / Play sound / " +
                "Show notification / Walk to / Change-or-start loop. AFK-only flag, " +
                "enabled flag, name.");

    [RelayCommand]
    private void PreviewAmbiguousLocationDialog()
        => OpenPlaceholder(
            id: "dialog-ambiguous-location",
            panelName: "Ambiguous Location",
            phaseTag: "Phase 7",
            headline: "Reconciliation prompt for the room tracker",
            description:
                "Surfaces when footprint matching finds more than one candidate " +
                "room of equal score. Lists the candidates; user clicks the right " +
                "one. Modeless — the walker pauses but the terminal keeps taking " +
                "input while the user decides.");

    [RelayCommand]
    private void PreviewImportConflictDialog()
        => OpenPlaceholder(
            id: "dialog-import-conflict",
            panelName: "Import Conflict",
            phaseTag: "Phase 5",
            headline: "Single reusable importer-conflict resolver",
            description:
                "Row-level diff for any importer (MDB tables, Spell Messages, " +
                "MegaMUD .mp paths, favorites). Per-row actions: skip / overwrite " +
                "/ merge / rename. Replaces MudProxy's four variant dialogs with " +
                "one component.");

    /// <summary>
    /// Tools → Wire Inspector. Singleton-ish: a second open activates the
    /// existing window rather than spawning a duplicate.
    /// </summary>
    private WireInspectorWindow? _wireInspector;

    [RelayCommand]
    private void OpenWireInspector()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_wireInspector is { } existing)
        {
            existing.Activate();
            return;
        }

        WireInspectorWindow window = new()
        {
            DataContext = new WireInspectorViewModel(AppServices.Current.Wire),
        };
        window.Closed += (_, _) => _wireInspector = null;
        _wireInspector = window;
        window.Show(main);
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
            AppServices.Current.Log.Warn("ShellLaunch", $"Could not open {AppPaths.LogsDir}");
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
            AppServices.Current.Log.Warn("Help", "Could not open help.");
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
            Connect / Disconnect (toggle) ... Ctrl+K
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
            AppServices.Current.Log.Error("Replay", "Replay unavailable — no main window.");
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

        AppServices.Current.Log.Info("Replay", $"Replaying {Path.GetFileName(path)}…");
        _ = Task.Run(async () =>
        {
            try
            {
                await player.PlayAsync().ConfigureAwait(false);
                AppServices.Current.Log.Info("Replay", "Replay complete.");
            }
            catch (Exception ex)
            {
                AppServices.Current.Log.Error("Replay", $"Replay failed: {ex.Message}");
            }
        });
    }
}
