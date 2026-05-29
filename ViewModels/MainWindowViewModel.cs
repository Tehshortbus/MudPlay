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
using System.Collections.ObjectModel;
using FujinTerm.Models.Profile;
using FujinTerm.Models.Settings;
using FujinTerm.Services;
using FujinTerm.Terminal;
using FujinTerm.ViewModels.Settings;
using FujinTerm.Views;
using FujinTerm.Views.Settings;

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
    private LoginAutomator? _automator;
    private Action<PromptObservation>? _loginKillSwitch;

    /// <summary>The screen buffer the UI renders. Lifetime spans the whole window.</summary>
    public TerminalEmulator Emulator { get; } = new(80, 25);

    /// <summary>
    /// Extracts completed lines from the emulator's screen stream. Foundation
    /// for every later-phase "what did the server say" subsystem
    /// (MessageRouter, ChatRouter, Triggers, prompt parser).
    /// </summary>
    public LineExtractor Lines { get; }

    /// <summary>
    /// Terminal-canvas font size — forwarded from
    /// <see cref="AppServices.Display"/> so the Settings → Display tab's
    /// edits reach the live canvas without bouncing through a save cycle.
    /// </summary>
    public double TerminalFontSize => AppServices.Current.Display.FontSize;

    /// <summary>
    /// Host the active BBS resolves to. Read-only from the UI — the user
    /// picks the active BBS in Settings → BBS, and that selection's Host /
    /// Port drives the connect button.
    /// </summary>
    public string Host => ResolveActiveBbs()?.Host ?? string.Empty;

    /// <summary>Port the active BBS resolves to. <c>0</c> when no BBS is configured.</summary>
    public int Port => ResolveActiveBbs()?.Port ?? 0;

    /// <summary>
    /// Name of the BBS the connect button will dial. Follows the same
    /// preference order as <see cref="ResolveActiveBbs"/> — the loaded
    /// character's pin first, then a fallback to the first BBS in the
    /// global list.
    /// </summary>
    public string? ActiveBbsName => ResolveActiveBbs()?.Name;

    /// <summary>Window title — "FujinTerm — {profile} — {bbs}", trimmed when bits are missing.</summary>
    public string WindowTitle
    {
        get
        {
            string? profile = AppServices.Current.Profile.CurrentProfileName;
            string? bbs = ActiveBbsName;
            if (profile is null && bbs is null) return "FujinTerm";
            if (profile is null) return $"FujinTerm — {bbs}";
            if (bbs is null)     return $"FujinTerm — {profile}";
            return $"FujinTerm — {profile} — {bbs}";
        }
    }

    /// <summary>True when the connect button has somewhere to dial.</summary>
    public bool CanConnect => !string.IsNullOrWhiteSpace(Host) && Port > 0;

    // Connection state is a small FSM: Idle → Connecting → Connected → Idle.
    // The single ToggleConnectionCommand drives every transition; everything
    // else (button visuals, menu label, status-bar stoplight) reads off
    // IsConnected + IsConnecting.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel))]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusText))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel))]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusText))]
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

    /// <summary>Status-bar stoplight label — pure state, no host / port detail.</summary>
    public string ConnectionStatusText
        => IsConnected ? "Connected"
         : IsConnecting ? "Connecting…"
         : "Disconnected";

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

    // ----- Status-bar tick countdowns -----------------------------------
    // Each cycle is rendered as a single text label. HP / MA append the
    // bonus cycle (" / 12.5") only while Position=Resting / Meditating.

    [ObservableProperty] private string _combatTickText = "Tick —";
    [ObservableProperty] private string _hpTickText = "HP —";
    [ObservableProperty] private string _maTickText = "MA —";

    /// <summary>
    /// 500 ms repaint cadence for the three status-bar tick countdowns —
    /// fast enough to look live without burning cycles. State sourced from
    /// AppServices.Tick (combat) + AppServices.Regen (HP / MA).
    /// </summary>
    private readonly DispatcherTimer _statusTickRefresh;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptureMenuLabel))]
    private bool _isDumping;

    /// <summary>Label shown on the Tools menu's capture-toggle entry.</summary>
    public string CaptureMenuLabel => IsDumping ? "Stop capture" : "Start capture";

    // Where session captures land when the user toggles capture. Stays under
    // the user's Data/Logs folder so it's covered by the same rotation policy
    // as DebugLogWriter output.
    private static string CaptureDirectory => AppPaths.LogsDir;

    /// <summary>
    /// Tees the live transcript to a .log file when the user clicks the
    /// Capture toolbar button / menu entry. Subscribes to the same
    /// ScrollbackBuffer the Backscroll window consumes, so the file is a
    /// 1:1 record of what the user saw — with colours preserved via inline
    /// ANSI SGR escapes.
    /// </summary>
    public CaptureSession Capture { get; }

    public MainWindowViewModel()
    {
        Lines = new LineExtractor(Emulator);
        Capture = new CaptureSession(Emulator.Screen.Scrollback);

        // 100 ms refresh — matches TickEngine's internal cadence so the
        // countdown ticks down by 0.1 s each repaint instead of jumping
        // in 0.5 s chunks.
        _statusTickRefresh = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _statusTickRefresh.Tick += (_, _) => RefreshStatusBarTicks();
        _statusTickRefresh.Start();
        RefreshStatusBarTicks();

        // Seed File → Recent profile slots + Save profile label.
        RecentProfiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Recent0));
            OnPropertyChanged(nameof(Recent1));
            OnPropertyChanged(nameof(Recent2));
            OnPropertyChanged(nameof(Recent3));
            OnPropertyChanged(nameof(Recent4));
            OnPropertyChanged(nameof(HasRecents));
        };
        RebuildRecentProfiles();
        SyncProfileMenuState();
        AppServices.Current.Profile.ProfileLoaded += _ => { SyncProfileMenuState(); RefreshBbsBindings(); };
        AppServices.Current.Profile.ProfileClosed += () => { SyncProfileMenuState(); RefreshBbsBindings(); };
        // ProfileMutated fires from BbsSectionViewModel.Apply after the
        // BBS pin has been stamped onto the profile — works for both
        // named profiles and unsaved drafts (Save no-ops on drafts but
        // the mutation signal still fires).
        AppServices.Current.Profile.ProfileMutated += _ => RefreshBbsBindings();

        // Forward DisplayConfig.FontSize changes to TerminalFontSize so the
        // bound TerminalControl re-renders when the Display tab changes the
        // font live. Also resize the live scrollback when ScrollbackLines
        // moves.
        AppServices.Current.Display.PropertyChanged += OnDisplayChanged;

        // Apply the loaded profile's persisted scrollback size now — the
        // buffer was constructed with the default; AppServices already
        // populated DisplayConfig from the profile by the time we got here.
        int initialScrollback = AppServices.Current.Display.ScrollbackLines;
        if (initialScrollback > 0 && initialScrollback != Emulator.Screen.Scrollback.Capacity)
        {
            Emulator.Screen.Scrollback.SetCapacity(initialScrollback);
        }

        // Apply the active BBS's terminal-grid size to the live emulator.
        // Without this the emulator stays at the 80×25 ctor default even
        // when the BBS file says otherwise.
        ApplyTerminalSize();

        // Every emitted line fans out through the central MessageRouter so
        // chat / combat / triggers / etc. all share one dispatch path.
        Lines.LineEmitted += line => AppServices.Current.Router.Dispatch(line);

        // The emulator emits replies (DSR, DA) it needs sent back to the
        // host; forward those onto the live telnet connection if any.
        Emulator.ResponseReady += bytes =>
        {
            var t = _telnet;
            if (t is not null) _ = t.SendAsync(bytes);
        };

    }

    private void OnDisplayChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Services.DisplayConfig.FontSize))
        {
            OnPropertyChanged(nameof(TerminalFontSize));
        }
        else if (e.PropertyName == nameof(Services.DisplayConfig.ScrollbackLines))
        {
            int newCapacity = AppServices.Current.Display.ScrollbackLines;
            if (newCapacity > 0) Emulator.Screen.Scrollback.SetCapacity(newCapacity);
        }
        else if (e.PropertyName == nameof(Services.DisplayConfig.TerminalCols)
              || e.PropertyName == nameof(Services.DisplayConfig.TerminalRows))
        {
            ApplyTerminalSize();
        }
    }

    /// <summary>
    /// Resize the live emulator screen and (if connected) re-advertise the
    /// new dimensions to the BBS via Telnet NAWS. Reads from
    /// <see cref="DisplayConfig"/> so any caller that wrote into it picks
    /// up the same source of truth.
    /// </summary>
    private void ApplyTerminalSize()
    {
        int cols = AppServices.Current.Display.TerminalCols;
        int rows = AppServices.Current.Display.TerminalRows;
        if (cols <= 0 || rows <= 0) return;
        if (cols == Emulator.Screen.Cols && rows == Emulator.Screen.Rows)
        {
            // Same size — still re-send NAWS in case the server lost state.
            _ = _telnet?.SendWindowSizeAsync(cols, rows);
            return;
        }
        Emulator.Resize(cols, rows);
        _ = _telnet?.SendWindowSizeAsync(cols, rows);
    }

    /// <summary>
    /// Repaint the status-bar tick countdowns. Source-of-truth:
    /// <see cref="Game.TickEngine.TimeToNextCombatTick"/> for combat;
    /// <see cref="Game.RegenTracker"/> for HP / MA. HP and MA show the
    /// natural cycle by default and append the bonus cycle (rest / medi)
    /// when the player is resting or meditating — the two cycles have
    /// independent anchors and can be desynced.
    /// </summary>
    private void RefreshStatusBarTicks()
    {
        Game.RegenTracker regen = AppServices.Current.Regen;
        Game.TickEngine tick = AppServices.Current.Tick;

        CombatTickText = FormatCountdown("Tick", tick.TimeToNextCombatTick);
        HpTickText     = FormatPair("HP",
                                    regen.GetTimeToNextHpNaturalTick(),
                                    regen.GetTimeToNextHpRestTick());
        MaTickText     = FormatPair("MA",
                                    regen.GetTimeToNextMpNaturalTick(),
                                    regen.GetTimeToNextMpMediTick());
    }

    private static string FormatCountdown(string label, TimeSpan? remaining)
        => remaining is null
            ? $"{label} —"
            : $"{label} {remaining.Value.TotalSeconds:0.0}";

    private static string FormatPair(string label, TimeSpan? natural, TimeSpan? bonus)
    {
        string naturalText = natural is null ? "—" : $"{natural.Value.TotalSeconds:0.0}";
        return bonus is null
            ? $"{label} {naturalText}"
            : $"{label} {naturalText} / {bonus.Value.TotalSeconds:0.0}";
    }

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
        DetachLoginKillSwitch();
        _automator?.Dispose();
        _automator = null;
        if (t is not null) await t.DisposeAsync();
        IsConnected = false;

        WriteTerminalStatus($"[DISCONNECTED FROM: {Host} {Port}]", TerminalStatusKind.Notice);
        AppServices.Current.Log.Info("Telnet", $"Disconnected from {Host}:{Port}");
    }

    private async Task ConnectWithRetriesAsync()
    {
        if (string.IsNullOrWhiteSpace(Host) || Port <= 0)
        {
            WriteTerminalStatus("[NO BBS SELECTED — OPEN SETTINGS → BBS, PICK ONE, AND SAVE.]",
                                TerminalStatusKind.Error);
            AppServices.Current.Log.Warn("Connect", "No active BBS — open Settings → BBS first.");
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
                    ArmLoginAutomator(client);
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

    /// <summary>
    /// Looks up the matching <see cref="BbsProfile"/> by host, pulls the
    /// loaded character's credentials for that BBS, and arms a
    /// <see cref="LoginAutomator"/> against the live socket. No-op when no
    /// BBS record matches, no profile is loaded, or the credentials are
    /// missing — the user just gets the raw login prompt.
    /// </summary>
    private void ArmLoginAutomator(TelnetClient client)
    {
        DetachLoginKillSwitch();
        _automator?.Dispose();
        _automator = null;

        BbsProfile? bbs = ResolveActiveBbs();
        if (bbs is null) return;  // no active BBS — caller already aborted the connect.

        CharacterProfile? character = AppServices.Current.Profile.Current;
        BbsCredentials? creds = null;
        character?.BbsCredentials?.TryGetValue(bbs.Name, out creds);

        LoginAutomator? automator = LoginAutomator.TryBuild(
            creds,
            AppServices.Current.Passwords,
            (text, ct) => client.SendTextAsync(text, ct),
            msg => AppServices.Current.Log.Debug("LoginAuto", msg));
        if (automator is null)
        {
            AppServices.Current.Log.Debug("LoginAuto",
                $"No menu-nav configured on '{AppServices.Current.Profile.CurrentProfileName ?? "(no profile)"}' for BBS '{bbs.Name}' — manual login.");
            return;
        }

        string bbsName = bbs.Name;
        automator.LoggedIntoGame += () =>
        {
            AppServices.Current.Log.Info("LoginAuto", $"Login automation complete for '{bbsName}'.");
            DetachLoginKillSwitch();
        };
        automator.Aborted += reason =>
        {
            AppServices.Current.Log.Warn("LoginAuto", $"'{bbsName}': {reason}");
            DetachLoginKillSwitch();
        };
        _automator = automator;

        // Hard kill-switch: the moment WirePromptScanner observes any
        // MajorMUD status line (`[HP=...]` on the wire), we know we're
        // inside the game. Dispose the automator immediately regardless
        // of where it sits in its step queue — no later step the user
        // may have authored can run, even if it references {username}
        // or {password}. Belt-and-braces on top of the auto-dispose at
        // FireDone: if the user's menu-nav doesn't structurally end at
        // "we're now in game" (extra trailing steps, a step that never
        // matches, etc.), this is the final defence that stops any of
        // them from firing in-game.
        WirePromptScanner scanner = AppServices.Current.PromptScanner;
        Action<PromptObservation>? handler = null;
        handler = _ =>
        {
            LoginAutomator? a = _automator;
            if (a is null) { DetachLoginKillSwitch(); return; }
            int stepsRun = a.CurrentStepIndex;
            int stepsTotal = a.StepCount;
            a.Dispose();
            _automator = null;
            DetachLoginKillSwitch();
            AppServices.Current.Log.Info("LoginAuto",
                $"In-game prompt observed — force-disposed automator for '{bbsName}' after {stepsRun}/{stepsTotal} step(s).");
        };
        scanner.PromptObserved += handler;
        _loginKillSwitch = handler;

        automator.Start();
    }

    private void DetachLoginKillSwitch()
    {
        if (_loginKillSwitch is null) return;
        AppServices.Current.PromptScanner.PromptObserved -= _loginKillSwitch;
        _loginKillSwitch = null;
    }

    /// <summary>
    /// Resolve which BBS the connect target reads off of. Preference order:
    /// <list type="number">
    ///   <item><description>The pin on the loaded character profile
    ///     (<c>CharacterProfile.BbsName</c>).</description></item>
    ///   <item><description>The first BBS in the global list (alphabetical),
    ///     so a user on a blank draft can still click Connect without
    ///     opening Settings first.</description></item>
    /// </list>
    /// Returns <c>null</c> only when there's no profile, no pin AND zero
    /// BBSes saved on disk.
    /// </summary>
    private static BbsProfile? ResolveActiveBbs()
    {
        string? name = AppServices.Current.Profile.Current?.BbsName;
        if (!string.IsNullOrEmpty(name))
        {
            BbsProfile? pinned = AppServices.Current.Bbs.Get(name);
            if (pinned is not null) return pinned;
        }

        string? first = AppServices.Current.Bbs.ListNames()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return first is null ? null : AppServices.Current.Bbs.Get(first);
    }

    private void RefreshBbsBindings()
    {
        OnPropertyChanged(nameof(Host));
        OnPropertyChanged(nameof(Port));
        OnPropertyChanged(nameof(ActiveBbsName));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(CanConnect));
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
            // Feed the Wire Inspector buffer — the post-IAC stream is what
            // the parser sees, which is exactly what the debug window wants
            // to surface. Thread-safe (its own internal lock).
            AppServices.Current.Wire.Append(copy);
            // PromptScanner + Emulator both write through observable state
            // bound by the UI, so they must run on the UI thread. Same post
            // keeps them aligned within one dispatch tick.
            Dispatcher.UIThread.Post(() =>
            {
                AppServices.Current.PromptScanner.Append(copy);
                _automator?.Feed(copy);
                Emulator.Feed(copy);
            });
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
    /// Convenience: encode a text line (Latin-1 + CRLF) and send it to the
    /// server. Used by the Conversation window's input field — typing in
    /// the chat panel feeds the game the same way as typing in the
    /// terminal does. Also scans the typed verb for heal-shaped commands
    /// so the regen tracker can gate any HP / MA upticks during the
    /// artifact grace window.
    /// </summary>
    public void SendUserText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (LooksLikeHealShapedCommand(text))
        {
            AppServices.Current.Regen.RecordArtifact();
        }

        byte[] bytes = System.Text.Encoding.Latin1.GetBytes(text + "\r\n");
        SendUserInput(bytes);
    }

    /// <summary>
    /// Heuristic: does <paramref name="line"/> start with a verb that
    /// usually moves HP or MA upward? Conservative — false positives just
    /// waste a few seconds of regen samples; false negatives let a heal
    /// pollute the running average, so be generous on the verb list.
    /// Refined by Phase 5 spell-event patterns (issue #8).
    /// </summary>
    private static bool LooksLikeHealShapedCommand(string line)
    {
        ReadOnlySpan<char> verb = FirstWord(line);
        if (verb.IsEmpty) return false;
        return verb.Equals("cast",  StringComparison.OrdinalIgnoreCase)
            || verb.Equals("drink", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("quaff", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("eat",   StringComparison.OrdinalIgnoreCase)
            || verb.Equals("apply", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("use",   StringComparison.OrdinalIgnoreCase)
            || verb.Equals("read",  StringComparison.OrdinalIgnoreCase)
            || verb.Equals("brew",  StringComparison.OrdinalIgnoreCase)
            || verb.Equals("bandage", StringComparison.OrdinalIgnoreCase);
    }

    private static ReadOnlySpan<char> FirstWord(string line)
    {
        int start = 0;
        while (start < line.Length && char.IsWhiteSpace(line[start])) start++;
        int end = start;
        while (end < line.Length && !char.IsWhiteSpace(line[end])) end++;
        return line.AsSpan(start, end - start);
    }

    /// <summary>
    /// Toggle session capture. The file lives at
    /// <c>Data/Logs/capture-yyyyMMdd-HHmmss.log</c> and receives one line
    /// per completed terminal row, prefixed with <c>[HH:mm:ss]</c> and
    /// encoded with inline ANSI SGR escapes so colour is preserved when
    /// the file is viewed through any ANSI-aware tool (<c>less -R</c>,
    /// modern terminals, web log viewers).
    /// </summary>
    [RelayCommand]
    private void ToggleDump()
    {
        if (IsDumping)
        {
            string? path = Capture.FilePath;
            Capture.Stop();
            IsDumping = false;
            AppServices.Current.Log.Info("Capture",
                path is null ? "Capture stopped." : $"Capture stopped — {Path.GetFileName(path)}");
            return;
        }

        string name = $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.log";
        string fullPath = Path.Combine(CaptureDirectory, name);
        try
        {
            Capture.Start(fullPath);
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

        // Toggle convention: clicking the same menu / hotkey / toolbar entry
        // a second time closes the window instead of activating it.
        if (_placeholders.TryGetValue(id, out PlaceholderShellWindow? existing))
        {
            existing.Close();
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

        // Toggle convention — see OpenPlaceholder.
        if (_logPane is { } existing)
        {
            existing.Close();
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

        // Toggle convention — see OpenPlaceholder. Find-in-scrollback is
        // the same toggle: hitting it while Backscroll is already open
        // closes the window. Opening freshly with focusSearch=true lands
        // focus on the search box.
        if (_backscroll is { } existing)
        {
            existing.Close();
            return;
        }

        BackscrollViewModel vm = new(Emulator)
        {
            FocusSearchOnOpen = focusSearch,
        };
        BackscrollWindow window = new() { DataContext = vm };
        window.Closed += (_, _) => _backscroll = null;
        _backscroll = window;
        window.Show(main);
    }

    private ConversationWindow? _conversation;

    [RelayCommand]
    private void OpenConversation()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        // Toggle convention — see OpenPlaceholder.
        if (_conversation is { } existing)
        {
            existing.Close();
            return;
        }

        ConversationWindow window = new()
        {
            DataContext = new ConversationViewModel(
                AppServices.Current.ChatHistory,
                SendUserText,
                Application.Current),
        };
        window.Closed += (_, _) => _conversation = null;
        _conversation = window;
        window.Show(main);
    }

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

    private SettingsWindow? _settings;

    // ----- Profile file management (Phase 4 PR 4.5a) ----------------------

    /// <summary>
    /// Most-recent-first list of saved profile names. Drives the inline
    /// File-menu recent entries (<see cref="Recent0"/>..<see cref="Recent4"/>).
    /// Rebuilt from <c>GlobalSettings</c> on startup and after every
    /// profile save.
    /// </summary>
    public ObservableCollection<string> RecentProfiles { get; } = new();

    // Indexed accessors so the File menu can lay out five fixed MenuItems
    // instead of a flyout submenu. Avalonia ItemsSource inside MenuItem
    // wraps each item in its own MenuItem, which loses the parent VM as
    // the DataContext (the command resolution via $parent[Window] is
    // fragile across popup ownership). Binding to the parent VM directly
    // sidesteps that entirely.
    public string? Recent0 => RecentProfiles.Count > 0 ? RecentProfiles[0] : null;
    public string? Recent1 => RecentProfiles.Count > 1 ? RecentProfiles[1] : null;
    public string? Recent2 => RecentProfiles.Count > 2 ? RecentProfiles[2] : null;
    public string? Recent3 => RecentProfiles.Count > 3 ? RecentProfiles[3] : null;
    public string? Recent4 => RecentProfiles.Count > 4 ? RecentProfiles[4] : null;

    /// <summary>True when at least one recent profile is queued — gates the Separator.</summary>
    public bool HasRecents => RecentProfiles.Count > 0;

    /// <summary>True when a named profile is loaded — gates File → Save profile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveProfileLabel))]
    private bool _hasNamedProfile;

    public string SaveProfileLabel => HasNamedProfile
        ? $"_Save profile  ·  {AppServices.Current.Profile.CurrentProfileName}"
        : "_Save profile…";

    /// <summary>
    /// Blank-slate the running profile. The outgoing profile is auto-saved
    /// first (handled inside ProfileService.LoadBlank), then Current is
    /// replaced with a fresh in-memory draft. The user names + persists
    /// it later via File → Save profile (which routes to Save As since
    /// the draft has no name yet).
    /// </summary>
    [RelayCommand]
    private void NewProfile()
    {
        AppServices.Current.Profile.LoadBlank();
        SyncProfileMenuState();
    }

    [RelayCommand]
    private async Task OpenProfileAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        IStorageFolder? profilesFolder = await main.StorageProvider.TryGetFolderFromPathAsync(AppPaths.ProfilesDir);
        IReadOnlyList<IStorageFile> files = await main.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open profile",
            AllowMultiple = false,
            SuggestedStartLocation = profilesFolder,
            FileTypeFilter = [new FilePickerFileType("Character profile (.json)") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0) return;

        string name = Path.GetFileNameWithoutExtension(files[0].Name);
        try
        {
            AppServices.Current.Profile.Load(name);
            PromoteRecent(name);
            SyncProfileMenuState();
        }
        catch (Exception ex)
        {
            AppServices.Current.Log.Error("Profile", $"Failed to load '{name}': {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        ProfileService profile = AppServices.Current.Profile;
        if (profile.Current is null)
        {
            AppServices.Current.Log.Warn("Profile", "Nothing to save — no profile loaded.");
            return;
        }
        if (profile.IsBlankDraft)
        {
            await SaveProfileAsAsync();
            return;
        }
        profile.Save();
        AppServices.Current.Log.Info("Profile", $"Saved profile '{profile.CurrentProfileName}'.");
    }

    [RelayCommand]
    private async Task SaveProfileAsAsync()
    {
        ProfileService profile = AppServices.Current.Profile;
        if (profile.Current is null)
        {
            AppServices.Current.Log.Warn("Profile", "Nothing to save — no profile loaded.");
            return;
        }
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        IStorageFolder? profilesFolder = await main.StorageProvider.TryGetFolderFromPathAsync(AppPaths.ProfilesDir);
        IStorageFile? file = await main.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save profile as",
            SuggestedStartLocation = profilesFolder,
            SuggestedFileName = profile.CurrentProfileName ?? "character",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("Character profile (.json)") { Patterns = ["*.json"] }],
            ShowOverwritePrompt = true,
        });
        if (file is null) return;

        // Profile names map to files under Data/profiles/{name}.json. If the
        // picker landed somewhere else we still pull just the basename and
        // write into Data/profiles — keeps ProfileService's layout invariant.
        string name = Path.GetFileNameWithoutExtension(file.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        profile.SaveAs(name);
        PromoteRecent(name);
        SyncProfileMenuState();
        AppServices.Current.Log.Info("Profile", $"Saved profile '{name}'.");
    }

    [RelayCommand]
    private void OpenRecentProfile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!AppServices.Current.Profile.Exists(name))
        {
            AppServices.Current.Log.Warn("Profile", $"Recent profile '{name}' no longer exists.");
            RecentProfiles.Remove(name);
            return;
        }
        try
        {
            AppServices.Current.Profile.Load(name);
            PromoteRecent(name);
            SyncProfileMenuState();
        }
        catch (Exception ex)
        {
            AppServices.Current.Log.Error("Profile", $"Failed to load '{name}': {ex.Message}");
        }
    }

    private void PromoteRecent(string profileName)
    {
        SettingsService settingsSvc = AppServices.Current.Settings;
        GlobalSettings settings = settingsSvc.Current;
        settings.RecentProfiles ??= new();
        settings.RecentProfiles.Remove(profileName);
        settings.RecentProfiles.Insert(0, profileName);
        while (settings.RecentProfiles.Count > GlobalSettings.RecentProfilesLimit)
            settings.RecentProfiles.RemoveAt(settings.RecentProfiles.Count - 1);
        settings.LastUsedProfileName = profileName;
        settingsSvc.Save();
        RebuildRecentProfiles();
    }

    private void RebuildRecentProfiles()
    {
        RecentProfiles.Clear();
        IList<string>? source = AppServices.Current.Settings.Current.RecentProfiles;
        if (source is null) return;
        foreach (string name in source) RecentProfiles.Add(name);
    }

    private void SyncProfileMenuState()
        => HasNamedProfile = !AppServices.Current.Profile.IsBlankDraft && AppServices.Current.Profile.Current is not null;

    [RelayCommand]
    private void OpenSettings() => OpenSettingsAt(null);

    [RelayCommand]
    private void OpenBbsSettings() => OpenSettingsAt("bbs");

    private void OpenSettingsAt(string? sectionId)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        // Toggle convention with edit-window save-on-toggle policy:
        // re-press of the same hotkey / menu while the window is open
        // routes through ApplyAndClose (Save path). Title-bar X / Cancel
        // button discards. See CLAUDE.md "Architecture rules". For a
        // deep-link (BBS list etc.) on a window that's already open, jump
        // to the requested section instead of saving + closing.
        if (_settings is { } existing)
        {
            if (existing.DataContext is SettingsWindowViewModel vm)
            {
                if (sectionId is not null)
                {
                    SettingsSectionViewModel? section = vm.Sections
                        .FirstOrDefault(s => string.Equals(s.Id, sectionId, StringComparison.OrdinalIgnoreCase));
                    if (section is not null) vm.SelectedSection = section;
                    existing.Activate();
                    return;
                }
                vm.ApplyAndClose();
            }
            else
            {
                existing.Close();
            }
            return;
        }

        AppServices svc = AppServices.Current;
        SettingsWindow window = new()
        {
            DataContext = new SettingsWindowViewModel(svc.Profile, svc.Log, sectionId),
        };
        window.Closed += (_, _) => _settings = null;
        _settings = window;
        window.Show(main);
    }

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

        // Toggle convention — see OpenPlaceholder.
        if (_wireInspector is { } existing)
        {
            existing.Close();
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

    /// <summary>
    /// Tools → Clear chatlog. Wipes every entry from the app-singleton
    /// ChatHistoryStore — the Conversation window's contents go with it
    /// (it binds to the same store) and a fresh open shows an empty list.
    /// Destructive; the spec doesn't ask for a confirm dialog yet.
    /// </summary>
    [RelayCommand]
    private void ClearChatlog()
    {
        AppServices.Current.ChatHistory.Clear();
        AppServices.Current.Log.Info("Chatlog", "Cleared chat history.");
    }

    /// <summary>
    /// Tools → Export chatlog… Saves the entire ChatHistoryStore (no
    /// channel filter, no day-separator filter) to a plain-text file the
    /// user picks.
    /// </summary>
    [RelayCommand]
    private async Task ExportChatlogAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        IStorageFile? file = await main.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export chatlog",
            SuggestedFileName = $"chatlog-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType("Plain text (.txt)") { Patterns = ["*.txt"] }],
        });

        if (file is null) return;

        await using Stream stream = await file.OpenWriteAsync();
        await AppServices.Current.ChatHistory.ExportAsync(stream).ConfigureAwait(false);
        AppServices.Current.Log.Info("Chatlog", $"Exported chatlog to {file.Name}");
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
              Player Workshop ............... F4  (wired Phase 9)
              Navigation .................... F5  (wired Phase 7)
              Spell Book .................... F7  (wired Phase 9)
              Backscroll .................... F10 (wired Phase 1)
              Session Stats ................. F11 (wired Phase 8)
              Settings ...................... Ctrl+,  (Phase 4)

            Tools
              Program Log ................... F9  (wired Phase 1)
              Wire Inspector ................ (no shortcut — toolbar / menu)

            Game Data
              Browser ....................... Ctrl+G  (Phase 5)

            File
              New / Open / Save profile ..... Ctrl+N / Ctrl+O / Ctrl+S  (Phase 4)

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

    /// <summary>Open InfoDialogs are tracked per title so menu / hotkey re-press toggles them shut.</summary>
    private readonly Dictionary<string, InfoDialog> _infoDialogs = new(StringComparer.Ordinal);

    private void ShowInfoDialog(string title, string body)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        // Toggle convention — see OpenPlaceholder. About / License /
        // Keyboard shortcuts each get their own tracker by title.
        if (_infoDialogs.TryGetValue(title, out InfoDialog? existing))
        {
            existing.Close();
            return;
        }

        InfoDialog dlg = new();
        dlg.Configure(title, body);
        dlg.Closed += (_, _) => _infoDialogs.Remove(title);
        _infoDialogs[title] = dlg;
        dlg.Show(main);
    }

}
