using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Input;
using MudPlay.Net;
using System.Collections.ObjectModel;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.Terminal;
using MudPlay.ViewModels.Settings;
using MudPlay.Views;
using MudPlay.Views.Settings;

namespace MudPlay.ViewModels;

// View-model for the main window. Owns the terminal emulator and the active
// Telnet connection, and exposes the bindable state and commands the XAML
// uses (host, port, status text, Connect / Disconnect / Dump buttons).
//
// CommunityToolkit.Mvvm source-generators expand each [ObservableProperty]
// backing field into a public property with INotifyPropertyChanged change
// notification, and each [RelayCommand] async method into an ICommand
// suitable for binding directly to a button.
public partial class MainWindowViewModel : ObservableObject
{
    private TelnetClient? _telnet;
    private LoginAutomator? _automator;
    private Action<PromptObservation>? _loginKillSwitch;
    private CancellationTokenSource? _cleanupReconnectCts;
    // Counts reactive reconnect arms in a row (carrier-lost / no-response).
    // Resets to 0 on a user-initiated connect/disconnect OR after the
    // connection has stayed alive long enough to count as "real" (see
    // _stableConnectionResetCts). Resetting on plain TCP-Connected
    // would let a BBS-in-cleanup loop (connect → unavailable message →
    // drop → reconnect) run forever because each fresh connect would
    // zero the counter and never hit MaxRedials.
    private int _reactiveReconnectCount;
    // Fires after StableConnectionWindowSeconds of continuous connect
    // to reset _reactiveReconnectCount. Cancelled on every Disconnect
    // so a flap inside the window is held against the redial budget.
    private CancellationTokenSource? _stableConnectionResetCts;
    private const int StableConnectionWindowSeconds = 30;

    // Live "dialing in 4m 23s" countdown shown in the status bar when
    // a long-delay reconnect is armed. Only shown when the delay is
    // ≥ ReconnectCountdownThresholdSeconds — the 5s reactive cycle
    // doesn't need a status-bar countdown that flashes for one second
    // before vanishing.
    private DispatcherTimer? _reconnectCountdownTimer;
    private DateTimeOffset _reconnectFireAt;
    private const int ReconnectCountdownThresholdSeconds = 30;
    // GC root for the who-list parser — it subscribes to LineExtractor
    // in its ctor and stays alive as long as MainWindowViewModel does.
    private readonly Game.WhoListParser _whoListParser;
    // GC root for the look-on-player parser — sibling to the who-list
    // parser; populates race / class / equipment from `look <player>`.
    private readonly Game.LookParser _lookParser;
    // GC root for the look-on-monster parser — turns a `look <monster>`
    // wound descriptor into the status bar's Target HP window.
    private readonly Game.MonsterLookParser _monsterLookParser;
    // GC root for the room-display + movement-refusal parsers. Both
    // subscribe to LineExtractor in their ctors and stay alive with this
    // view-model.
    private readonly Game.Map.RoomDisplayParser _roomDisplayParser;
    private readonly Game.Map.MovementRefusalDetector _movementRefusalDetector;
    private readonly Game.Map.CombatEntryRefusalHandler _combatEntryRefusalHandler;

    // The screen buffer the UI renders. Lifetime spans the whole window.
    public TerminalEmulator Emulator { get; } = new(80, 25);

    // Startup mud-throw splash on the terminal. Plays from launch and is cleared
    // the moment a session begins (first connect or first server data), reverting
    // the control to the live emulator. Bound to TerminalControl.SplashActive.
    [ObservableProperty] private bool _showSplash = true;

    // Whether the mud figure animates (Settings → General "Show startup mud
    // animation"). When off, the splash still shows its static header (title +
    // byline + hint). Read at construction; bound to TerminalControl.SplashAnimate.
    [ObservableProperty] private bool _splashAnimate = true;

    // Extracts completed lines from the emulator's screen stream. Foundation
    // for every "what did the server say" subsystem (MessageRouter,
    // ChatRouter, Triggers, prompt parser).
    public LineExtractor Lines { get; }

    // Terminal-canvas font size — forwarded from AppServices.Display so the
    // Settings → Display tab's edits reach the live canvas without bouncing
    // through a save cycle.
    public double TerminalFontSize => AppServices.Current.Display.FontSize;

    // Whether the terminal auto-fits its font to the window (keeping the fixed
    // cell grid). Forwarded from AppServices.Display like TerminalFontSize so a
    // Settings → General edit reaches the live canvas immediately. Bound to
    // TerminalControl.ScaleToFit.
    public bool ScaleTerminalToWindow => AppServices.Current.Display.ScaleToWindow;

    // Terminal-canvas font family — forwarded from AppServices.Display (stored
    // there as an avares:// URI string) and wrapped into a FontFamily the
    // TerminalControl binds to, so a Settings → General font change reaches the
    // live canvas without a save/reload bounce.
    public Avalonia.Media.FontFamily TerminalFontFamily =>
        new(AppServices.Current.Display.FontFamily);

    // Live mirror of the Global-tier toolbar visibility settings. Each
    // toolbar Button in the XAML binds its IsVisible to a property on this so
    // edits in Settings → Toolbar apply immediately on Apply / OK.
    public Services.ToolbarConfig Toolbar => AppServices.Current.Toolbar;

    // Render-ready view-models for the dynamic toolbar ItemsControl. Mirrors
    // ToolbarConfig.Layout; each entry resolves through ToolbarItemCatalogue
    // and binds against the matching command on this view-model. Rebuilt
    // whenever Layout changes (Settings → Toolbar Apply path).
    public ObservableCollection<ToolbarButtonItem> ToolbarItems { get; } = new();

    // File → Quick Connect target. Wins over ResolveActiveBbs once set;
    // cleared when the user picks a (different) BBS via Settings → BBS, or
    // when a new profile loads.
    private (string Host, int Port)? _quickConnectTarget;

    // Tracks the BBS pin observed on the last profile-mutation event so we
    // can detect when the user actually changed BBS (vs. tweaked an unrelated
    // setting). Used to drop _quickConnectTarget.
    private string? _lastSeenBbsName;

    // Host the active connection target resolves to. Quick Connect wins over
    // the saved BBS pin when set; otherwise the user's BBS Host stands in.
    public string Host => _quickConnectTarget?.Host ?? ResolveActiveBbs()?.Host ?? string.Empty;

    // Port the active connection target resolves to. 0 when nothing is configured.
    public int Port => _quickConnectTarget?.Port ?? ResolveActiveBbs()?.Port ?? 0;

    // Name of the dial target — Quick Connect's host:port when active,
    // otherwise the active BBS's display name (or null). Consumed by the
    // title bar and the connect-status banner.
    public string? ActiveBbsName => _quickConnectTarget is { } qc
        ? $"Quick Connect: {qc.Host}:{qc.Port}"
        : ResolveActiveBbs()?.Name;

    // Optional URL field on the active BBS's BbsProfile.WebsiteUrl. Drives
    // the Help → BBS site menu item's enable state + the actual launch. Quick
    // Connect targets have no website (Quick Connect bypasses the BBS profile
    // store entirely), so this is null in that case.
    public string? BbsWebsiteUrl => _quickConnectTarget is null
        ? ResolveActiveBbs()?.WebsiteUrl
        : null;

    // True when BbsWebsiteUrl looks launch-able — gates the Help menu item.
    public bool HasBbsWebsite => !string.IsNullOrWhiteSpace(BbsWebsiteUrl);

    // Per-BBS toggle (BbsProfile.ShowWebsiteInHelp) for whether the "BBS site ↗"
    // entry appears in the Help menu at all — independent of HasBbsWebsite,
    // which only gates its enabled state. Edited under Settings → Toolbar +
    // Shortcuts. Defaults on when there's no active BBS to read.
    public bool ShowBbsWebsiteInHelp => ResolveActiveBbs()?.ShowWebsiteInHelp ?? true;

    // User-editable Help-menu website list (Global tier). Composed into the
    // Help menu by the code-behind, and edited under Settings → Toolbar +
    // Shortcuts. Repopulated from GlobalSettings by RefreshHelpLinks whenever
    // the global file is saved.
    public ObservableCollection<HelpWebsite> HelpLinks { get; } = new();

    // Re-read the Help website list from the Global-tier settings file and
    // refill HelpLinks. Falls back to the seeded defaults when the key is
    // absent or the stored JSON is malformed, so the Help menu always shows the
    // reference links even before the user touches the editor.
    private void RefreshHelpLinks()
    {
        HelpWebsitesSettings dto = new();
        if (AppServices.Current.Settings.Current.Settings is { } bucket
            && bucket.TryGetValue("HelpWebsites", out System.Text.Json.JsonElement json))
        {
            try
            {
                dto = System.Text.Json.JsonSerializer.Deserialize<HelpWebsitesSettings>(json.GetRawText())
                      ?? new HelpWebsitesSettings();
            }
            catch
            {
                dto = new HelpWebsitesSettings();
            }
        }

        HelpLinks.Clear();
        foreach (HelpWebsite link in dto.Links)
        {
            if (string.IsNullOrWhiteSpace(link.Url)) continue;
            HelpLinks.Add(link);
        }
    }

    // Window title — "MudPlay v{version} — {profile} — {bbs}". The version is
    // the running build's AppInfo.Version. When no profile is loaded the
    // placeholder {default} stands in; when no BBS is selected {No BBS} stands
    // in. Both slots always render so the title bar shape stays consistent.
    public string WindowTitle
    {
        get
        {
            string profile = AppServices.Current.Profile.CurrentProfileName ?? "{default}";
            string bbs     = ActiveBbsName ?? "{No BBS}";
            return $"MudPlay v{AppInfo.Version} — {profile} — {bbs}";
        }
    }

    // True when the connect button has somewhere to dial.
    public bool CanConnect => !string.IsNullOrWhiteSpace(Host) && Port > 0;

    // Connection state is a small FSM: Idle → Connecting → Connected → Idle.
    // The single ToggleConnectionCommand drives every transition; everything
    // else (button visuals, menu label, status-bar stoplight) reads off
    // IsConnected + IsConnecting.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(IsReconnectPending))]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel))]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusText))]
    // Swapping the active profile mid-session desyncs it from the live game
    // state, so New / Open / Open-recent are disconnected-only (Save / Save-as
    // stay available). Re-evaluate their CanExecute when the wire flips.
    [NotifyCanExecuteChangedFor(nameof(NewProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenRecentProfileCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(IsReconnectPending))]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel))]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusText))]
    private bool _isConnecting;

    // True during the user-initiated disconnect path — between the toolbar
    // click and the wire actually closing. Drives the "Disconnecting…" label
    // + short-circuits the toggle command so a fast double-click can't
    // initiate a reconnect mid-disconnect.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel))]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusText))]
    private bool _isDisconnecting;

    // Live auto-engine toggles. Each mirrors the matching
    // GeneralSettings.AutoMode flag on the active profile; the toolbar Toggle
    // buttons, the Action-menu check items, and the Settings → General
    // checkboxes all write here. The partial OnXxxChanged handlers persist to
    // the profile and refresh the toolbar IsActive badge.
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsAllAutoOff))] private bool _isAutoCombatActive;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsAllAutoOff))] private bool _isAutoNukeActive;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsAllAutoOff))] private bool _isAutoHealRestActive;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsAllAutoOff))] private bool _isAutoBlessActive;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsAllAutoOff))] private bool _isAutoLightActive;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsAllAutoOff))] private bool _isAutoGetItemsActive;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsAllAutoOff))] private bool _isAutoGetCashActive;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsAllAutoOff))] private bool _isAutoSneakActive;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsAllAutoOff))] private bool _isAutoHideActive;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsAllAutoOff))] private bool _isAutoSearchActive;

    // Master "Disable hangups" toggle. When on, every automatic disconnect
    // path (@hangup / @relog remote commands, low-HP emergency hangup,
    // nightly-cleanup log-off) is suppressed — the client drops the carrier
    // only on an explicit user action. Persisted in
    // Models.Profile.GeneralSettings.DisableHangups and reseeded on profile
    // load like the auto-mode toggles. Not part of IsAllAutoOff — it gates
    // disconnects, not auto-engines.
    [ObservableProperty] private bool _isDisableHangupsActive;

    // True when every wired auto-engine is off — drives the "Auto-All" master
    // toggle's depressed/checked state. Mirrors
    // Game.AutoModeController.AllWiredOff but computed from the live
    // observables so the badge updates instantly.
    public bool IsAllAutoOff =>
        !IsAutoCombatActive && !IsAutoNukeActive && !IsAutoHealRestActive
        && !IsAutoBlessActive && !IsAutoLightActive && !IsAutoGetItemsActive
        && !IsAutoGetCashActive && !IsAutoSneakActive && !IsAutoHideActive
        && !IsAutoSearchActive;

    public bool IsDisconnected => !IsConnected;

    // True when there is no active connection AND no connect attempt in flight.
    public bool IsIdle => !IsConnected && !IsConnecting;

    // True when an auto-reconnect is armed but hasn't fired yet — covers both
    // the predictive cleanup scheduler and the reactive carrier-lost path.
    // Drives the toolbar / menu's Connect label so a re-press cancels the
    // pending redial instead of immediately dialling.
    public bool IsReconnectPending
        => _cleanupReconnectCts is not null && !IsConnected && !IsConnecting;

    // Header text for the single Connect ↔ Disconnect menu entry / button
    // tooltip. Four-state cycle: ReconnectPending → "Cancel reconnect" → Idle
    // → "Connect" → Connecting → "Cancel connect" → Connected → "Disconnect".
    public string ConnectionLabel
        => IsDisconnecting ? "Disconnecting…"
         : IsConnected ? "Disconnect"
         : IsConnecting ? "Cancel connect"
         : IsReconnectPending ? "Cancel reconnect"
         : "Connect";

    // Status-bar stoplight label — pure state, no host / port detail.
    public string ConnectionStatusText
        => IsDisconnecting ? "Disconnecting…"
         : IsConnected ? "Connected"
         : IsConnecting ? "Connecting…"
         : "Disconnected";

    // Live "dialing in 4m 23s" countdown text. Empty when no reconnect is
    // armed OR when the armed delay is too short to warrant a status-bar
    // countdown. Updated once per second by _reconnectCountdownTimer.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReconnectCountdownVisible))]
    private string _reconnectCountdownText = string.Empty;

    // True when ReconnectCountdownText should render. Bound by the status bar.
    public bool IsReconnectCountdownVisible => !string.IsNullOrEmpty(ReconnectCountdownText);

    // ----- Status-bar location slot (mirrors NavigationViewModel) ----

    // Text shown in the bottom status bar's location slot. Mirrors the
    // Navigation window's current-room label so the two stay in sync — the
    // bottom bar should never show "Unknown location" while the Navigation
    // strip knows where the player is.
    [ObservableProperty] private string _locationText = "Unknown location";


    // ----- Engine-state chip (mirrors the Navigation window's badge) -----

    private Game.Map.WalkState _walkerState   = Game.Map.WalkState.Idle;
    private bool               _loopRunning;
    private bool               _autoLairOn;

    // Label inside the chip — short upper-case state tag.
    public string EngineActionBadge =>
        _autoLairOn                                     ? "AUTO-LAIR"
        : _loopRunning                                  ? "LOOPING"
        : (_walkerState != Game.Map.WalkState.Idle)     ? "WALKING"
        :                                                 "IDLE";

    public bool EngineActionIsIdle    => !_autoLairOn && !_loopRunning && _walkerState == Game.Map.WalkState.Idle;
    public bool EngineActionIsWalking => !_autoLairOn && !_loopRunning && _walkerState != Game.Map.WalkState.Idle;
    public bool EngineActionIsLooping => !_autoLairOn &&  _loopRunning;
    public bool EngineActionIsLair    =>  _autoLairOn;

    private void OnWalkerEngineEvent(Game.Map.WalkEvent _)
        => Dispatcher.UIThread.Post(() =>
        {
            _walkerState = AppServices.Current.Walker.State;
            RefreshEngineActionChip();
        });

    private void OnLoopRunnerEngineEvent(Game.Map.LoopEvent e)
        => Dispatcher.UIThread.Post(() =>
        {
            _loopRunning = AppServices.Current.LoopRunner.State != Game.Map.LoopState.Idle;
            RefreshEngineActionChip();
            // Loop status owns the location slot while active — refresh on every
            // event so the lap counter ticks over on RepeatStarted and the slot
            // clears on Stopped.
            RefreshLocationSlot();
            // Circuit reached its first waypoint (walk-to done, looping begins) —
            // settle the live auto-engines into the character's base modes. Fires
            // once per run (the event itself is one-shot), never on lap wraps.
            if (e.Kind == Game.Map.LoopEventKind.ReachedFirstWaypoint)
                ReconcileAutoModeToBase("loop start");
        });

    private void OnAutoLairActiveChanged(bool active)
        => Dispatcher.UIThread.Post(() =>
        {
            _autoLairOn = active;
            // A fresh auto-lair run re-arms the once-per-run base-modes reconcile.
            if (active) _autoLairBaseReconciled = false;
            RefreshEngineActionChip();
        });

    // Latches the auto-lair base-modes reconcile to the FIRST lair entry of a run
    // (not every subsequent lair the circuit clears). Re-armed on ActiveChanged.
    // The loop path needs no latch — its ReachedFirstWaypoint is already one-shot.
    private bool _autoLairBaseReconciled;

    private void OnAutoLairPhaseChangedForBase(Game.Map.AutoLairPhase phase)
        => Dispatcher.UIThread.Post(() =>
        {
            if (phase != Game.Map.AutoLairPhase.Engaging) return;
            if (_autoLairBaseReconciled) return;
            _autoLairBaseReconciled = true;
            ReconcileAutoModeToBase("auto-lair start");
        });

    private void OnMovementControlStateChanged()
        => Dispatcher.UIThread.Post(() =>
        {
            foreach (ToolbarButtonItem row in ToolbarItems)
            {
                if (row.IsButton) ApplyToolbarRowState(row);
            }
        });

    private void RefreshEngineActionChip()
    {
        OnPropertyChanged(nameof(EngineActionBadge));
        OnPropertyChanged(nameof(EngineActionIsIdle));
        OnPropertyChanged(nameof(EngineActionIsWalking));
        OnPropertyChanged(nameof(EngineActionIsLooping));
        OnPropertyChanged(nameof(EngineActionIsLair));
    }

    // Cancels an in-flight connect attempt — covers both the socket-level
    // TelnetClient.ConnectAsync and the inter-attempt Task.Delay. Cleared in
    // the finally block.
    private CancellationTokenSource? _connectCts;

    // Per-attempt socket timeout. The OS default (~75s on Linux for
    // unreachable hosts) is far too long for a BBS client. Constant for now
    // since most BBSes behave similarly on this dimension.
    private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromSeconds(30);

    // Why the most recent connection ended. Drives the reactive reconnect
    // decision (BbsProfile.ReconnectOnFailedConnect /
    // BbsProfile.ReconnectOnCarrierLost) and is reset on every successful new
    // connection.
    private enum DisconnectCause
    {
        // No disconnect this session (initial state or just reset).
        None,
        // User clicked Disconnect — never auto-retry.
        UserInitiated,
        // Initial dial threw or timed out before reaching the BBS.
        FailedConnect,
        // Connected session ended without our initiation — server-side drop.
        CarrierLost,
        // Socket died after a long quiet stretch — TCP keepalive caught a hung server.
        NoResponse,
        // Deliberate hangup originated client-side — remote @hangup from a
        // trusted player, or a future hang-up-if-naked / hang-up-if-low-HP
        // automation. Never auto-retries (user is presumed to be in a
        // dangerous spot and needs to manually decide whether to come back).
        // The matching Game.MainMenuEntryAutomation.Arm also skips when this
        // fired, so the user manually re-enters and the post-entry
        // stat/exp/i refresh doesn't spam the wire.
        HangupInitiated,
        // Deliberate relog originated client-side — remote @relog from a
        // trusted player. The character gracefully exits
        // (Services.GameCommands.ExitCommand) and we force an unconditional
        // dial-back (ignoring the per-BBS reconnect toggles), then let the
        // normal login automation log back in. Unlike HangupInitiated the
        // entry latch is NOT suppressed — the whole round-trip is automatic.
        RelogInitiated,
    }

    private DisconnectCause _lastDisconnectCause = DisconnectCause.None;
    private bool _userInitiatedDisconnect;

    // ----- Status-bar tick countdowns -----------------------------------
    // Each cycle is rendered as a single text label. HP / MA append the
    // bonus cycle (" / 12.5") only while Position=Resting / Meditating.

    [ObservableProperty] private string _combatTickText = "Tick —";
    [ObservableProperty] private string _hpTickText = "HP —";
    [ObservableProperty] private string _maTickText = "MA —";

    // Estimated HP window of the last `look <monster>` target, e.g.
    // "Target: 35-48" — empty when nothing's been looked at (or after the target
    // dies / we change rooms), which hides the centre status slot. Fed by
    // MonsterLookParser; the game only reveals a coarse wound band, so this is the
    // absolute HP range that band implies against the monster's max HP.
    [ObservableProperty] private string _targetHpText = "";

    // 500 ms repaint cadence for the three status-bar tick countdowns — fast
    // enough to look live without burning cycles. State sourced from
    // AppServices.Tick (combat) + AppServices.Regen (HP / MA).
    private readonly DispatcherTimer _statusTickRefresh;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptureMenuLabel))]
    private bool _isDumping;

    // Label shown on the Tools menu's capture-toggle entry.
    public string CaptureMenuLabel => IsDumping ? "Stop capture" : "Start capture";

    // Where session captures land when the user toggles capture. Stays under
    // the user's Data/Logs folder so it's covered by the same rotation policy
    // as DebugLogWriter output.
    private static string CaptureDirectory => AppPaths.LogsDir;

    // Tees the live transcript to a .log file when the user clicks the
    // Capture toolbar button / menu entry. Subscribes to the same
    // ScrollbackBuffer the Backscroll window consumes, so the file is a 1:1
    // record of what the user saw — with colours preserved via inline ANSI
    // SGR escapes.
    public CaptureSession Capture { get; }

    public MainWindowViewModel()
    {
        // Install-global startup-animation preference, seeded from the Global default
        // profile during AppServices.Initialize (which runs before this ctor).
        _splashAnimate = AppServices.Current.Display.SplashAnimate;

        Lines = new LineExtractor(Emulator);
        Capture = new CaptureSession(Emulator.Screen.Scrollback);

        // Live-screen watch for the character-creation stat box. It's drawn with
        // cursor positioning, so its marker row never completes as an emitted
        // line until teardown — too late to flip arrow keys into direct-input
        // mode. Scanning the screen each feed lets TrainerMenuTracker arm
        // character mode the moment the box appears. Cheap: pre-filtered on the
        // stat-box marker and skipped entirely once armed.
        Emulator.ScreenUpdated += OnScreenUpdatedForTrainerMenu;

        // Feed the crash reporter a live-state snapshot so a fatal fault's
        // Crash-<timestamp>.md carries the same scrollback / log / engine dump a
        // manual bug report would. Reads the current Emulator at call time (it
        // can be swapped per connection); guarded on the reporter's side.
        CrashReporter.RegisterStateProvider(() =>
            BugReportBuilder.RenderStateOnly(
                BugReportBuilder.Capture(AppServices.Current, Emulator)));

        // 100 ms refresh — matches TickEngine's internal cadence so the
        // countdown ticks down by 0.1 s each repaint instead of jumping
        // in 0.5 s chunks.
        _statusTickRefresh = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        // The location slot's trailing exp/hr is a continuously-decaying rate
        // (windowed experience ÷ elapsed time), so it rides this same tick — the
        // slot's own room / engine events fire too rarely to keep it live, which
        // left it frozen at its entry value (0/hr at session start) while the
        // Session Stats window, ticking the same tracker, showed the real rate.
        // The LocationText setter's equality check drops the repaint when the
        // compact rate is unchanged, so most ticks cost only a string compare.
        _statusTickRefresh.Tick += (_, _) =>
        {
            RefreshStatusBarTicks();
            RefreshLocationSlot();
        };
        _statusTickRefresh.Start();
        RefreshStatusBarTicks();

        // Mirror RoomTracker into the status-bar location slot so the
        // bottom bar and the Navigation window's strip stay in sync.
        AppServices.Current.RoomTracker.StateChanged += OnRoomTrackerStateChanged;
        // Swapping game-data sets (incl. clearing to none) reseeds the
        // room graph; refresh the location slot so the "load a game data
        // set" hint appears when the graph empties and clears when it fills.
        AppServices.Current.RoomGraph.GraphReloaded +=
            () => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshLocationSlot);

        // Let deep VMs (the Item Finder's row double-click) open the single-
        // instance Game Data Browser at a specific item — only this VM can
        // spawn / toggle that window.
        AppServices.Current.SetItemGameDataOpener(OpenItemGameData);
        // Same for the room-detail popup: monster names jump to a monster
        // record, and the room title / exits centre the Nav map on a room.
        AppServices.Current.SetMonsterGameDataOpener(OpenMonsterGameData);
        AppServices.Current.SetMonstersAccuracyOpener(OpenMonstersWithAccuracy);
        AppServices.Current.SetRoomGameDataOpener(OpenRoomGameData);
        AppServices.Current.SetNavigateToRoomOpener(FocusNavigationOnRoom);
        AppServices.Current.SetQueueWalkOpener(QueueWalkToRoom);
        AppServices.Current.SetCenterNavigationIfOpenOpener(CenterNavigationOnRoomIfOpen);
        AppServices.Current.SetNavManagerOpener(OpenNavManager);
        AppServices.Current.SetTypedInputSender(SendUserText);

        // Engine-state chip — same shape as the Navigation window's
        // top-bar badge (IDLE / WALKING / LOOPING / AUTO-LAIR). Lives
        // on the status bar so the user always sees which engine is
        // driving without having to peek at the Nav window.
        AppServices.Current.Walker.Event           += OnWalkerEngineEvent;
        AppServices.Current.LoopRunner.Event       += OnLoopRunnerEngineEvent;
        AppServices.Current.AutoLair.ActiveChanged += OnAutoLairActiveChanged;
        // Auto-lair reaching the lair (Entering→Engaging) is its circuit-start —
        // the base-modes reconcile hooks it, latched once per run (see the handler).
        AppServices.Current.AutoLair.PhaseChanged  += OnAutoLairPhaseChangedForBase;
        // Coalesced engine state drives the Start / Pause / Stop toolbar
        // buttons' visibility + Pause↔Resume label; re-apply row state on
        // every transition so the toolbar mirrors the running engine.
        AppServices.Current.MovementControl.StateChanged += OnMovementControlStateChanged;
        // Name-learned prompt: fires when the tracker adopts a name
        // for a previously-unnamed room (typical of map-15 ganghouse
        // rooms in 1.x exports). Modeless yes/no asks whether to write
        // the new name back to Rooms.json; session-deduped so a single
        // walk doesn't re-prompt the same room on every observation.
        AppServices.Current.RoomTracker.NameLearned += OnRoomNameLearned;

        // EngineRecoveryGate terminal failure → modeless "Lost" info
        // dialog. The gate already aborted the engine; we just surface
        // the message so the user knows automation gave up.
        AppServices.Current.Recovery.RecoveryFailed += OnRecoveryFailed;
        AppServices.Current.Recovery.TierChanged    += OnRecoveryTierChanged;
        RefreshLocationSlot();

        // Train Now's stuck-at-level-N reconcile: when there's no banked
        // level left to train but the current level still has an affordable, unapplied
        // CP plan raise, the trainer-walk coordinator asks before spending CP. Wired
        // here (not in the per-connection wire-binding block) because it's a UI prompt,
        // not part of the gate-wrapped engine pipeline.
        AppServices.Current.TrainerWalk.SetCpSpendConfirm(() =>
            AppServices.Current.Confirm.ConfirmAsync(
                "Allocate CP", "Spend points IAW CP allocation plan?", "Spend"));

        // Seed File → Recent profile slots + Save profile label.
        // Notify both the display labels (Recent0..4 — "name - bbs")
        // and the raw profile names (ProfileName0..4 — used as the
        // OpenRecentProfile command parameter) on every list change.
        RecentProfiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Recent0));
            OnPropertyChanged(nameof(Recent1));
            OnPropertyChanged(nameof(Recent2));
            OnPropertyChanged(nameof(Recent3));
            OnPropertyChanged(nameof(Recent4));
            OnPropertyChanged(nameof(ProfileName0));
            OnPropertyChanged(nameof(ProfileName1));
            OnPropertyChanged(nameof(ProfileName2));
            OnPropertyChanged(nameof(ProfileName3));
            OnPropertyChanged(nameof(ProfileName4));
            OnPropertyChanged(nameof(HasRecents));
        };
        RebuildRecentProfiles();
        SyncProfileMenuState();
        AppServices.Current.Profile.ProfileLoaded += OnProfileLoadedForConnect;
        // On profile load the base-modes checkboxes set the live engine positions
        // (no-op for a character that predates the base/live split). Runs before
        // the badge reseed below; the reconcile also reseeds when it changes state.
        AppServices.Current.Profile.ProfileLoaded += _ => ReconcileAutoModeToBase("profile load");
        AppServices.Current.Profile.ProfileLoaded += _ => SyncAutoEngineTogglesFromProfile();
        AppServices.Current.Profile.ProfileMutated += _ => SyncAutoEngineTogglesFromProfile();
        AppServices.Current.Profile.ProfileSaving  += _ => SyncAutoEngineTogglesFromProfile();
        AppServices.Current.Profile.ProfileClosed += () => { ClearQuickConnect(); SyncProfileMenuState(); RefreshBbsBindings(); SyncAutoEngineTogglesFromProfile(); };
        // Seed at construction time so the toolbar IsActive badge is
        // right on first paint (even with no profile, the engines'
        // isEnabled delegates return false and the toggles render off).
        SyncAutoEngineTogglesFromProfile();
        // ProfileMutated fires from BbsSectionViewModel.Apply after the
        // BBS pin has been stamped onto the profile — works for both
        // named profiles and unsaved drafts (Save no-ops on drafts but
        // the mutation signal still fires).
        AppServices.Current.Profile.ProfileMutated += _ => OnProfileMutatedForBbs();
        AppServices.Current.Profile.BbsPinApplied += _ => { ClearQuickConnect(); RefreshBbsBindings(); };

        // Seed the BBS-pin sentinel so OnProfileMutatedForBbs can detect
        // the first real change against a known baseline.
        _lastSeenBbsName = ResolveActiveBbs()?.Name;

        // Help-menu website list (Global tier). Seed now, then re-read on every
        // global-settings Save so the Toolbar + Shortcuts editor's changes land
        // in the menu without a relaunch.
        RefreshHelpLinks();
        AppServices.Current.Settings.GlobalSettingsChanged += _ =>
        {
            RefreshHelpLinks();
            // A BBS rename rewrites the recent-profiles refs in the Global tier
            // — re-mirror so the File → Recent menu drops the old BBS name.
            RebuildRecentProfiles();
        };

        // Cleanup-warning banner: when the BBS announces nightly shutdown
        // on the wire, drop a yellow banner into the terminal canvas so
        // the user knows to type `quit` at a safe room. The auto-reconnect
        // schedule is armed later, on the Disconnected event.
        AppServices.Current.Cleanup.WarningObserved += OnCleanupWarningObserved;
        // CleanupModeDetected fires when the BBS rejects us mid-connect
        // with "this system is not available" (we connected during the
        // cleanup window). Used by TryScheduleReactiveReconnect to
        // switch from the 5s reactive cycle to a single long-delay
        // attempt at now + CleanupPeriodMinutes.
        AppServices.Current.Cleanup.CleanupModeDetected += OnCleanupModeDetected;

        // Forward DisplayConfig.FontSize changes to TerminalFontSize so the
        // bound TerminalControl re-renders when the Display tab changes the
        // font live. Also resize the live scrollback when ScrollbackLines
        // moves.
        AppServices.Current.Display.PropertyChanged += OnDisplayChanged;

        // Seed the File → Game Data → Active set menu. Rebuild on every
        // signal that could change which row carries the checkmark:
        // a different set is now active, a different BBS got pinned,
        // a profile re-mutated (BBS rename), or a fresh profile loaded.
        RebuildGameDataSetsMenu();
        AppServices.Current.GameData.ActiveSetChanged += _ => RebuildGameDataSetsMenu();
        AppServices.Current.Profile.BbsPinApplied      += _ => RebuildGameDataSetsMenu();
        AppServices.Current.Profile.ProfileMutated     += _ => RebuildGameDataSetsMenu();
        AppServices.Current.Profile.ProfileLoaded      += _ => RebuildGameDataSetsMenu();

        // Seed the terminal right-click Favorites flyout from the starred GOTO
        // favourites + favourited loops + favourited auto-lair setups, and
        // refresh it whenever any of them change (all three events also fire on
        // game-data set swaps, so a realm change refreshes too).
        RebuildFavoritesMenu();
        AppServices.Current.Favorites.Changed += RebuildFavoritesMenu;
        AppServices.Current.Loops.LoopsChanged += RebuildFavoritesMenu;
        AppServices.Current.Lairs.SetupsChanged += RebuildFavoritesMenu;

        // Recent GOTO destinations flyout, refreshed on every recorded walk (and
        // on profile swap, which GotoHistoryStore fires Changed for).
        RebuildRecentDestinationsMenu();
        AppServices.Current.GotoHistory.Changed += RebuildRecentDestinationsMenu;

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

        // Reactive hazard-buff re-raise: a lapse-damage prompt (the desert's
        // "you need water, soon!") mid-walk fires one `use` to re-raise, and a
        // second prompt with no swig confirmation halts the walk (out of charges).
        Lines.LineEmitted += line =>
            AppServices.Current.AutoHazardCounterProvisioner.OnServerLine(line.Text);

        // who-list observer: subscribes to LineExtractor on its own
        // (the table is multi-line — needs state, doesn't fit
        // MessageRouter's stateless dispatch). Feeds every observed
        // player into PlayerDatabase.
        _whoListParser = new Game.WhoListParser(Lines, AppServices.Current.Players, AppServices.Current.Log);
        _lookParser    = new Game.LookParser   (Lines, AppServices.Current.Players, AppServices.Current.Log);
        // Monster-look HP estimator. Name → Number prefers the record actually
        // placed / summoned in the current room (so an "orc lieutenant" here hits
        // this room's record, not a same-named one in another zone), then falls
        // back to the classifier's first-match. Number → max HP via the game-data
        // index.
        _monsterLookParser = new Game.MonsterLookParser(
            Lines,
            name => AppServices.Current.RoomAwareMonster.ResolveInCurrentRoom(name)
                    ?? AppServices.Current.RoomClassifier.ResolveLookedMonsterNumber(name),
            AppServices.Current.MonsterHp.MaxHp,
            AppServices.Current.Log);
        _monsterLookParser.TargetObserved += OnMonsterLookTarget;
        // A kill in the room retires whatever target we last looked at.
        AppServices.Current.MonsterDeath.MonsterDied += OnMonsterDied;
        // Quest becomes available (trained past its min level, or the login dump) → a
        // yellow terminal notice.
        AppServices.Current.QuestAvailability.QuestBecameAvailable += OnQuestAvailable;

        // Room-display + movement-refusal parsers feeding RoomTracker.
        // Same per-session LineExtractor binding shape as the who/look
        // parsers above.
        _roomDisplayParser       = new Game.Map.RoomDisplayParser(Lines,
            AppServices.Current.RoomTracker, AppServices.Current.Log);
        // Feed every room display into the teleport-maze solver so it can
        // relocalize after a random teleport. RoomParsed fires before the
        // tracker consumes the observation, so a look-peek (which the tracker
        // drops) still reaches the solver.
        _roomDisplayParser.RoomParsed += AppServices.Current.MazeSolver.OnRoomObserved;
        // Same feed drives the pyramid solver's F3 door-state reads and its scatter
        // (room-name) fail detection.
        _roomDisplayParser.RoomParsed += AppServices.Current.PyramidSolver.OnRoomObserved;
        // Same pre-suppression feed drives the recovery gate's tier-3 look-sweep
        // — it reads peeked neighbours the tracker would otherwise drop.
        _roomDisplayParser.RoomParsed += AppServices.Current.Recovery.OnRoomObserved;
        _movementRefusalDetector = new Game.Map.MovementRefusalDetector(Lines,
            AppServices.Current.RoomTracker, AppServices.Current.Log);
        // Combat-gated-entry refusal: `break` → 3s → revert move so the driving
        // engine retries. Gated on a movement engine actually driving.
        _combatEntryRefusalHandler = new Game.Map.CombatEntryRefusalHandler(Lines,
            AppServices.Current.RoomTracker,
            () => AppServices.Current.MovementControl.IsActive,
            AppServices.Current.Log);

        // PartyManager lives at AppServices level (so the @-command engine
        // and PartyWindow can grab a stable reference), but its par-block
        // state machine needs the per-session LineExtractor. Same wiring
        // shape as TriggerEngine.AttachLineExtractor.
        AppServices.Current.Party.AttachLineExtractor(Lines);
        // StatParser — same per-session LineExtractor binding so it can
        // see the lines emitted by the `stat` screen. Writes every
        // field onto AppServices.Current.PlayerStats; feeds
        // RemoteCommandManager.LivesProvider for the @suicide gate.
        AppServices.Current.Stats.AttachLineExtractor(Lines);
        // SpellListParser — reads `spells` / `pow` output into the Spell
        // Book's obtained set. App-level (survives reconnects), bound to
        // the per-session extractor here like Stats above.
        AppServices.Current.SpellList.AttachLineExtractor(Lines);
        // TrainLearnParser — marks powers obtained the moment training lists
        // them ("You learn the following Kai abilities:"). Same per-session
        // binding as SpellList above.
        AppServices.Current.TrainLearn.AttachLineExtractor(Lines);
        // DeathDetector — watches for the post-death
        // "You now have N lives remaining." line; fires
        // RoomTracker.NoteDeath which appends to
        // CharacterProfile.DeathHistory and transitions to
        // PendingRespawn ahead of the respawn-room display.
        AppServices.Current.Death.AttachLineExtractor(Lines);
        // ConditionTracker scans inbound lines against every game-data
        // Messages record's AppliedMessage / AppliedEndsWith pair to
        // surface live ActiveFlags.
        AppServices.Current.Conditions.AttachLineExtractor(Lines);
        // Pyramid solver watches lines for the sphinx "concealed passage" cue, the
        // golden-lion-key pickup, and the scatter room name.
        AppServices.Current.PyramidSolver.AttachLineExtractor(Lines);
        // Party-buff confirmation — CastingDirector watches inbound lines
        // for OUR caster echo ("You cast bless on Raijin!") to confirm a
        // pending party-bless cast landed before it starts the buff's
        // duration timer. Self-buff confirmation goes through the
        // ConditionTracker AppliedMessage path instead.
        AppServices.Current.CastDirector.AttachLineExtractor(Lines);
        // abil-breakdown parser — feeds the mana-regen reroll engine the rolled
        // `spells:` slice of `abil 145` after a nature-tap / mana-flux landing.
        AppServices.Current.AbilBreakdown.AttachLineExtractor(Lines);
        // Inbound ailment chip-clear — PartyAilmentTracker watches server
        // lines for OUR cure spell landing on a party member (matched by the
        // cure spell's CasterMessage template) and clears that member's
        // ailment chip. The chip-set side rides ChatRouter, not the line feed.
        AppServices.Current.PartyAilment.AttachLineExtractor(Lines);
        // Multi-line "Also here:" wrap stitching — the server wraps
        // long occupant lists at the 80-col boundary, so the regex
        // pattern only sees the first row. AttachLineExtractor
        // buffers continuations until the period.
        AppServices.Current.RoomClassifier.AttachLineExtractor(Lines);
        // Same shape for "You notice <list> here." — CashManager
        // buffers wrapped rows to parse the full cash + item list.
        AppServices.Current.Cash.AttachLineExtractor(Lines);
        // Same survey line drives item auto-get — its own buffer
        // stitches wrapped rows so the multi-line "You notice" parses.
        AppServices.Current.AutoGetItems.AttachLineExtractor(Lines);
        // Auto-buy watches the same emitted-line stream for the shop `list`
        // readout and stitches its stock table off the wrapped rows.
        AppServices.Current.AutoBuy.AttachLineExtractor(Lines);
        // And the @what / @get-all ground-item snapshot off the same survey.
        AppServices.Current.GroundItems.AttachLineExtractor(Lines);
        // Passively snapshots a `top N` leaderboard block for the Calculators
        // tab's XP/HR table — stitches the multi-row listing off the same feed.
        AppServices.Current.LeaderboardCapture.AttachLineExtractor(Lines);
        // Inventory parser reads the full `i` dump (carried currency,
        // Wealth, Encumbrance) so CashManager's gate has live carry
        // weight; it buffers the wrapped deposit echo too.
        AppServices.Current.Inventory.AttachLineExtractor(Lines);
        // Death-recovery watches for `You pick up ...` confirmations
        // to drive the deathpile Partial → Recovered transition + re-equip.
        AppServices.Current.DeathRecovery.AttachLineExtractor(Lines);
        // The Emulator lives here, so hand death-recovery a provider for the
        // backscroll tail it snapshots at each death ("How did I Die?").
        AppServices.Current.DeathRecovery.AttachTranscriptTail(
            () => TranscriptSnapshot.Tail(Emulator, 200));
        // Every engine wire-sender is routed through EngineGate's
        // wrapper. The wrapper short-circuits while
        // EngineGate.IsLocked is true (today: while
        // SuicidePasswordTracker is in a password-entry prompt),
        // so a stray par poll / auto-invite / @health round-trip
        // can't end up sent as the user's suicide password. User-
        // typed input doesn't go through this wrapper — TerminalControl
        // calls SendUserInput directly via the local-input buffer
        // flush.
        Action<byte[]> engineSend = AppServices.Current.EngineGate.WrapEngineSender(SendUserInput);

        // A move step put on the wire while the gate was locked is silently dropped;
        // when the last hold clears, re-drive any step the walker is still stalled
        // on so it doesn't sit forever with a route drawn and nothing sent (the
        // auto-train loop-resume stall, report paradigm-20260813-063517).
        AppServices.Current.EngineGate.Released += AppServices.Current.Walker.NudgeStalledStep;

        // Combat-gated-entry handler sends `break` on refusal — needs the same
        // gate-wrapped wire path.
        _combatEntryRefusalHandler.SetWireSender(engineSend);

        // Auto-invite on reconnect needs a wire-sender to send
        // "invite <name>" when a disconnected member returns within the
        // grace window AND we're the party leader.
        AppServices.Current.Party.SetWireSender(engineSend);

        // Macro dispatcher needs a wire-send callback before it can fire.
        // TerminalControl + ConversationWindow's input both call into the
        // dispatcher on KeyDown — without a sender bound, the call returns
        // false and the keystroke falls through to normal handling.
        AppServices.Current.MacroDispatcher.SetSender(engineSend);

        // Bind the same gate-wrapped wire path to the EventManager so
        // Command-action events route through SendUserInput like every
        // other engine.
        AppServices.Current.Events.SetWireSender(engineSend);

        // Trigger engine subscribes to the LineExtractor for game-message
        // dispatch (chat + system-log subscriptions wired in its ctor) and
        // borrows the same wire sender so a fired trigger's Response goes
        // through the canonical SendUserInput path.
        AppServices.Current.Triggers.AttachLineExtractor(Lines);
        AppServices.Current.Triggers.SetSender(engineSend);

        // Remote-command engine borrows the same wire-sender so a handler's
        // ctx.Reply(text) routes through SendUserInput exactly like a
        // typed command would. The PartyEssentialHandlers also need their
        // own copy for the @party <sub> → local-command relay (uses the
        // wire-sender directly, bypassing ctx.Reply).
        AppServices.Current.RemoteCommands.SetWireSender(engineSend);
        AppServices.Current.PartyEssentials.SetWireSender(engineSend);
        // Settings → Talk auto-greet — needs the wire-sender to emit
        // greet/look at newly-seen non-party players.
        AppServices.Current.Greet.SetWireSender(engineSend);
        // Settings → Talk reactive-look — needs the wire-sender to emit
        // look-back / look-on-arrival at other players.
        AppServices.Current.PlayerLook.SetWireSender(engineSend);
        // Poller needs the same wire-sender to send @health round-trip
        // requests and the periodic par poll.
        AppServices.Current.PartyPoller.SetWireSender(engineSend);
        // Once-a-day party stats probe sends @level + @version on the same path.
        AppServices.Current.PartyProbe.SetWireSender(engineSend);
        // Ally-drop rescue sends `aid <name>` + `/<given> @health` on the same
        // gate-wrapped path (held while WE are mortally wounded, like every engine).
        AppServices.Current.AllyDropped.SetWireSender(engineSend);
        // Emit @wait when we start resting and @ok when we finish, so
        // the party leader's pause-gate can react.
        AppServices.Current.PartyRest.SetWireSender(engineSend);
        // Outbound ailment-sync — the say-announce (".@poisoned" etc.)
        // rides the same engine sender; the @wait/@ok side routes through
        // PartyRest's sender bound just above.
        AppServices.Current.AilmentSync.SetWireSender(engineSend);
        // Auto-Exp-Reset + panic / kill broadcasts go through
        // PartyBroadcaster.
        AppServices.Current.PartyBroadcaster.SetWireSender(engineSend);
        // AutoPartyManager — consumes per-player InviteToPartyIfSeen
        // and JoinPartyIfInvited flags, sends `invite <given>` and
        // `follow <given>` over the wire.
        AppServices.Current.AutoParty.SetWireSender(engineSend);
        // HangupHandler — sends the configured GameExitCommand when
        // an authorised sender telepaths @hangup.
        AppServices.Current.Hangup.SetWireSender(engineSend);
        // RelogHandler — same exit command, but arms RelogSignal so the
        // Disconnected handler forces a reconnect-and-login cycle.
        AppServices.Current.Relog.SetWireSender(engineSend);
        // DivertHandler — repeats incoming telepaths to a target while
        // @divert is active; rides the same gate-wrapped pipeline.
        AppServices.Current.Divert.SetWireSender(engineSend);
        // Follower-side @comeback. Telepaths @comeback to the leader
        // when a movement-failure line strands us as the party walks
        // off; rides the same gate-wrapped pipeline.
        AppServices.Current.ComebackRequest.SetWireSender(engineSend);
        // Follower-side reconnect auto-rejoin — telepaths @comeback + @invite
        // to re-form the party after a drop; same gate-wrapped pipeline.
        AppServices.Current.PartyRejoin.SetWireSender(engineSend);
        // Leader-side reconnect recovery — telepaths the @where probe + decline
        // @forget when re-collecting a returning member; same pipeline.
        AppServices.Current.PartyComeback.SetWireSender(engineSend);
        // Death-recovery auto-grab (`get`) + auto-equip (`wear` / `hold`)
        // ride the same gate-wrapped pipeline as the other engines.
        AppServices.Current.DeathRecovery.SetWireSender(engineSend);
        // Auto-train is the ONE automation allowed to type on the `train stats`
        // screen — entering the form (`train stats`) raises the TrainerScreenGate
        // hold that silences every other engine, so its own `train stats` + each
        // Enter-driven CP keystroke must pierce that hold. Bind it to the raw,
        // un-wrapped SendUserInput (same escape hatch the low-HP hangup uses);
        // ObserveOutbound still fires on this path, so the `train stats` send is
        // what arms the tracker and raises the gate in the first place.
        AppServices.Current.AutoTrain.SetWireSender(SendUserInput);
        // TrainerWalk sends `train` / `stat` only OUTSIDE the form (bare `train`
        // has no Point Cost Chart, so it never raises MenuOwnsKeyboard; `stat`
        // fires post-training), so it stays on the gate-wrapped pipeline.
        AppServices.Current.TrainerWalk.SetWireSender(engineSend);
        // Level-up announcer broadcasts "I can now train to level: N" on
        // the configured channel through the same gate-wrapped pipeline.
        AppServices.Current.LevelUp.SetWireSender(engineSend);
        // CombatManager sends `attack <target>` on target pick via the
        // same engine-send pipeline; the gate-wrapped sender prevents
        // the swing command from landing mid-password-entry on a stale
        // combat round.
        AppServices.Current.Combat.SetWireSender(engineSend);
        // CombatStateTracker sends `break` before releasing the walker when the
        // user toggles auto-attack off mid-fight (CombatSettings.BreakBeforeFleeing).
        AppServices.Current.CombatTracker.SetWireSender(engineSend);
        // Post-death graveyard resync — a CR to re-observe the respawn room if it
        // hasn't landed shortly after death. Wire-send only (not a move), so it
        // rides engineSend even though the death halt holds the movement gate.
        AppServices.Current.PlayerDeathHalt.SetWireSender(engineSend);
        // HealthManager sends rest / stand / pre- / post-rest commands
        // via the same gate-wrapped engine pipeline.
        AppServices.Current.Health.SetWireSender(engineSend);
        // The emergency low-HP hangup is the one Health send that must survive
        // an EngineSendGate hold — a mortally-wounded character can still hang
        // up, and that hold is exactly what a drop raises. Bind it to the raw,
        // un-wrapped SendUserInput so the escape hangup pierces the gate.
        AppServices.Current.Health.SetHangupWireSender(SendUserInput);
        // After the exit command goes out, close the carrier ourselves rather
        // than waiting on the server to notice — see RequestHangupDisconnect.
        AppServices.Current.Health.SetHangupDisconnect(RequestHangupDisconnect);
        // CastCoordinator's `c <spell> [target]` emits still respect the
        // suicide-password / trainer-menu lockouts (gate-wrapped), but ride
        // the raw send, NOT engineSend — engineSend funnels through
        // SendUserInput, whose OutboundCastObserver exists to catch a
        // hand-typed cast and would otherwise mistake the combat engine's own
        // announce for one (see SendEngineWireRaw).
        AppServices.Current.Cast.SetWireSender(
            AppServices.Current.EngineGate.WrapEngineSender(SendEngineWireRaw));
        // Mana-regen reroll engine's `abil 145` query + cooldown-bypassing
        // recast ride the same gate-wrapped pipeline as the cast engine.
        AppServices.Current.SetEngineWireSender(engineSend);
        // Item-cast buff sequencer's wield/use/wield commands ride the
        // same gate-wrapped pipeline as the other engines.
        AppServices.Current.ItemCast.SetWireSender(engineSend);
        // EquipmentManager's paced `wear` commands ride the same
        // gate-wrapped pipeline (@equip-<set> set-apply).
        AppServices.Current.Equipment.SetWireSender(engineSend);
        // CashManager's `get all <coin>` commands ride the gate-wrapped
        // pipeline like the other engines.
        AppServices.Current.Cash.SetWireSender(engineSend);
        // AutoGetItemsManager's `get <name>` commands ride the same
        // gate-wrapped pipeline.
        AppServices.Current.AutoGetItems.SetWireSender(engineSend);
        // The loot-automation engines — AutoDiscard's `drop`, AutoBuy's `buy`,
        // AutoSell's `sell` — all ride the same gate-wrapped pipeline.
        AppServices.Current.AutoDiscard.SetWireSender(engineSend);
        AppServices.Current.AutoBuy.SetWireSender(engineSend);
        AppServices.Current.AutoSell.SetWireSender(engineSend);
        // Base auto-search — the per-room `sea` rides the same gate-wrapped
        // pipeline so it can't land mid-password-prompt.
        AppServices.Current.AutoSearch.SetWireSender(engineSend);
        // Active auto-light — the route's `use <light>` / `rem <old>` swap
        // rides the same gate-wrapped pipeline.
        AppServices.Current.AutoLightProvisioner.SetWireSender(engineSend);
        // Auto-light provisioning detour — the `buy <light>` at the shop rides
        // the same gate-wrapped pipeline.
        AppServices.Current.AutoLightShopRouter.SetWireSender(engineSend);
        // Hazard-buff provisioning — the `use <waterskin>` that raises a
        // checkspell hazard buff rides the same gate-wrapped pipeline.
        AppServices.Current.AutoHazardCounterProvisioner.SetWireSender(engineSend);
        // Shop-source routing — the `buy <item>` at the detour shop
        // rides the same gate-wrapped pipeline.
        AppServices.Current.PathItemShopRouter.SetWireSender(engineSend);
        // Give-source routing — the `ask <npc> <keyword>` / room-CMD give
        // command at the detour giver rides the same gate-wrapped pipeline.
        AppServices.Current.PathItemGiveRouter.SetWireSender(engineSend);
        // Party-inventory deferral — the `@party give` / `@do give`
        // hand-off rides the same gate-wrapped pipeline. The @have probe
        // itself broadcasts through the already-bound PartyBroadcaster.
        AppServices.Current.PartyPathItemGate.SetWireSender(engineSend);
        // StashRoomManager's `hide N <coin>` commands ride the same
        // gate-wrapped pipeline.
        AppServices.Current.Stash.SetWireSender(engineSend);
        // Auto-deposit reroute's bank `dep` command rides the same
        // gate-wrapped pipeline.
        AppServices.Current.AutoDeposit.SetWireSender(engineSend);
        // @drop-all / @deposit-all / @share emit drop / dep / with / give
        // on the same gate-wrapped pipeline.
        AppServices.Current.InventoryAction.SetWireSender(engineSend);
        // @heal receive side polls `par` so CastingDirector heals the
        // requester off fresh party HP.
        AppServices.Current.Heal.SetWireSender(engineSend);
        // Cluster 3 — StealthManager's auto-sneak / auto-hide commands.
        AppServices.Current.Stealth.SetWireSender(engineSend);
        // @do passthrough — gate-wrapped because a malicious caller's
        // payload shouldn't be able to land mid-suicide-password entry.
        AppServices.Current.Do.SetWireSender(engineSend);
        // @trap auto-disarm — gate-wrapped (same reason).
        AppServices.Current.TrapDisarm.SetWireSender(engineSend);
        // Party trap delegation — `look <name>` race probes + `.@trap <dir>`
        // say broadcasts ride the same gate-wrapped pipeline.
        AppServices.Current.TrapDelegation.SetWireSender(engineSend);
        // DoorOpenManager wire-sender — gate-wrapped so the bash/pick
        // sequence can't land in a password-entry prompt. Walker
        // routes door exits through Door.Enqueue at step-send time.
        AppServices.Current.Door.SetWireSender(engineSend);
        // LeaderDoorAssistManager — same gate-wrapped sender so helping the
        // leader force a door can't fire mid-password-prompt.
        AppServices.Current.LeaderDoorAssist.SetWireSender(engineSend);
        AppServices.Current.Walker.SetDoorEnqueuer(AppServices.Current.Door.Enqueue);
        AppServices.Current.Walker.SetDoorStopper(AppServices.Current.Door.StopAll);
        // Loop runner shares the same door FSM so a closed door mid-circuit is
        // bashed/picked/keyed instead of detaching the whole lap.
        AppServices.Current.LoopRunner.SetDoorEnqueuer(AppServices.Current.Door.Enqueue);
        AppServices.Current.LoopRunner.SetDoorStopper(AppServices.Current.Door.StopAll);
        // HiddenExitRevealManager — same gate-wrapped sender so the
        // sea loop can't land mid-password-prompt. Walker routes
        // SearchableHidden exits here.
        AppServices.Current.HiddenSearch.SetWireSender(engineSend);
        AppServices.Current.Walker.SetHiddenSearchEnqueuer(AppServices.Current.HiddenSearch.Enqueue);
        AppServices.Current.Walker.SetHiddenSearchStopper(AppServices.Current.HiddenSearch.StopAll);
        // Loop runner shares the same reveal FSM so a hidden exit mid-circuit is
        // uncovered with sea <dir> instead of failing the lap.
        AppServices.Current.LoopRunner.SetHiddenSearchEnqueuer(AppServices.Current.HiddenSearch.Enqueue);
        AppServices.Current.LoopRunner.SetHiddenSearchStopper(AppServices.Current.HiddenSearch.StopAll);
        // Teleport-exit wiring — walker resolves (source, destination)
        // → keyword via TBInfoTeleportResolver against the active
        // TBInfoStore, and pre-broadcasts the keyword to followers via
        // `.@party <kw>` when the local character is party leader.
        // Shared by walker + loop runner so both cross teleport exits
        // with the same keyword + follower-relay behavior.
        Func<Game.Map.RoomKey, Game.Map.RoomKey, string?> teleportResolver =
            (source, dest) =>
            {
                Game.Map.Room? src = AppServices.Current.RoomGraph.GetRoom(source);
                if (src is null || src.Cmd <= 0) return null;
                return Game.Map.TBInfoTeleportResolver.Resolve(
                    AppServices.Current.TBInfo, src.Cmd, dest);
            };
        Func<bool> isLeaderWithFollowers =
            () => AppServices.Current.Party.State.SelfIsLeader
                && AppServices.Current.Party.State.Members.Any(m => !m.IsSelf);
        AppServices.Current.Walker.SetTeleportResolver(teleportResolver);
        AppServices.Current.Walker.SetItemNameResolver(id => AppServices.Current.ItemNames.GetName(id));
        AppServices.Current.Walker.SetPartyLeaderCheck(isLeaderWithFollowers);
        AppServices.Current.LoopRunner.SetTeleportResolver(teleportResolver);
        AppServices.Current.LoopRunner.SetPartyLeaderCheck(isLeaderWithFollowers);
        // A party-splitting CMD teleport (chime-style) dissolves the follow
        // chain even though the `.@party <kw>` relay sent everyone through, so
        // the party must be re-invited on landing. Both movement engines route
        // that reform through AutoPartyManager, which holds the movement gate
        // until the group re-forms.
        AppServices.Current.Walker.SetPartySplitHandler(
            AppServices.Current.AutoParty.NotePartySplitTeleport);
        AppServices.Current.LoopRunner.SetPartySplitHandler(
            AppServices.Current.AutoParty.NotePartySplitTeleport);
        // Stopping the walk mid-reform drops the re-invite hold so the user
        // isn't pinned by the PartyInvite gate until the group rejoins.
        AppServices.Current.Walker.SetPartySplitAbortHandler(
            () => AppServices.Current.AutoParty.AbortReformWaits("walk stopped"));
        // Walker + loop runner — gate-wrapped so a long walk doesn't
        // blast moves through a password-entry prompt.
        AppServices.Current.Walker.SetWireSender(engineSend);
        AppServices.Current.LoopRunner.SetWireSender(engineSend);
        // Paradigm position resolver — its `rm` re-sync ride the same
        // gate-wrapped pipeline so it can't land mid-password-prompt.
        AppServices.Current.ParadigmResync.SetWireSender(engineSend);
        // Teleport-maze solver — its look-peeks + reshuffle moves ride the same
        // gate-wrapped pipeline. The RoomParsed feed that drives its relocalize
        // is subscribed below beside the RoomDisplayParser.
        AppServices.Current.MazeSolver.SetWireSender(engineSend);
        // Pyramid solver — its climb moves + door/sphinx commands ride the same
        // gate-wrapped pipeline.
        AppServices.Current.PyramidSolver.SetWireSender(engineSend);
        // Summon-on-death CR recheck rides the same gate-wrapped pipeline.
        AppServices.Current.SummonSettle.SetWireSender(engineSend);
        // Recovery gate's tier-3 look-sweep rides the same gate-wrapped pipeline
        // so its `look <dir>` peeks can't land mid-password-prompt.
        AppServices.Current.Recovery.SetWireSender(engineSend);
        // SuicideHandler — bypasses the engine gate because it OWNS
        // the suicide flow (and needs its `suicide` + password sends
        // to land even while SuicidePasswordTracker has the gate
        // locked for the password-prompt phase). Uses the raw
        // SendUserInput, not the wrapped engineSend.
        AppServices.Current.Suicide.SetWireSender(SendUserInput);
        // MainMenuEntryAutomation — same sender; armed below when
        // LoginAutomator's LoggedIntoGame fires (only point in the
        // session where the entry command is allowed to auto-fire).
        AppServices.Current.MainMenuEntry.SetWireSender(engineSend);
        // StatlineReconciler — gate-wrapped so a `set statline` resend can't
        // land mid-password-prompt. Armed unconditionally on every connect
        // (below) so the live statline reconciles to the editor regardless of
        // whether login was automated.
        AppServices.Current.StatlineReconcile.SetWireSender(engineSend);

        // CleanupLogoutOrchestrator — proactive log-off on the nightly
        // shutdown warning. Safe = room clear of killable NPCs AND not
        // mid-combat (the two signals CombatStateTracker owns). Gated on
        // the active BBS's ReconnectAfterCleanup toggle (same switch the
        // predictive reconnect scheduler reads), and uses the user-
        // initiated disconnect path so the reconnect still arms afterward.
        AppServices.Current.CleanupLogout.SetWireSender(engineSend);
        AppServices.Current.CleanupLogout.SetSafePredicate(
            () => !AppServices.Current.CombatTracker.HasEngageableHostiles
                && !AppServices.Current.PlayerState.InCombat);
        AppServices.Current.CleanupLogout.SetConnectedCheck(() => IsConnected);
        AppServices.Current.CleanupLogout.SetAutoLogoutEnabledCheck(
            () => ResolveActiveBbs()?.ReconnectAfterCleanup ?? false);
        AppServices.Current.CleanupLogout.SetDisconnectCallback(
            () => _ = DisconnectInternalAsync());

        // Refresh every menu's InputGesture text + the toolbar button
        // tooltips on rebind. Each gesture label property reads through
        // to KeybindingStore.Get(...) so PropertyChanged on all of them
        // is enough to update the menu; toolbar tooltips are baked into
        // ToolbarButtonItem at row-build time, so we re-run RebuildToolbarItems
        // to pick up the new label. BindingsReloaded covers the bulk
        // profile-load / -close path so the just-loaded chords surface
        // immediately without waiting for the user to rebind.
        AppServices.Current.Keybindings.BindingChanged  += _ => OnKeybindsChanged();
        AppServices.Current.Keybindings.BindingsReloaded += OnKeybindsChanged;

        void OnKeybindsChanged()
        {
            RefreshKeybindLabels();
            RebuildToolbarItems();
        }

        // The emulator emits replies (DSR, DA) it needs sent back to the
        // host; forward those onto the live telnet connection if any.
        Emulator.ResponseReady += bytes =>
        {
            var t = _telnet;
            if (t is not null) _ = FireSendAsync(t, bytes);
        };

        // Build the dynamic toolbar items now, then rebuild whenever the
        // user reorders / adds / removes via Settings → Toolbar (which
        // mutates Toolbar.Layout on Apply).
        RebuildToolbarItems();
        Toolbar.Layout.CollectionChanged += (_, _) => RebuildToolbarItems();
        PropertyChanged += SyncToolbarStateFlags;
    }

    // Walks ToolbarConfig.Layout and rebuilds ToolbarItems. Each Button
    // row is resolved through ToolbarItemCatalogue; the command property
    // is fetched by reflection from the catalogue's CommandName so adding
    // a new toolbar action is a one-line catalogue entry. Unknown action
    // ids are skipped.
    private void RebuildToolbarItems()
    {
        ToolbarItems.Clear();
        foreach (ToolbarItem item in Toolbar.Layout)
        {
            if (item.Kind == ToolbarItemKind.Separator)
            {
                ToolbarItems.Add(new ToolbarButtonItem(
                    ToolbarItemKind.Separator, null,
                    label: string.Empty,
                    iconResourceKey: null,
                    tooltip: string.Empty,
                    command: null));
                continue;
            }

            ToolbarItemCatalogue.Entry? entry = ToolbarItemCatalogue.Find(item.ActionId);
            if (entry is null) continue;

            ICommand? command = GetType().GetProperty(entry.CommandName)?.GetValue(this) as ICommand;

            // Live shortcut hint: if the action id parses as a BuiltInAction,
            // pull the current binding from KeybindingStore so a user rebind
            // (Settings → Toolbar → Change keybind…) updates the tooltip
            // immediately. Otherwise (ToggleCapture, ActionGetAll, …) fall
            // back to the catalogue's hardcoded hint.
            string? liveHint =
                Enum.TryParse(entry.ActionId, ignoreCase: false, out Models.Profile.BuiltInAction parsedAction)
                    ? AppServices.Current.Keybindings.Get(parsedAction) is { IsEmpty: false } live
                        ? live.Label
                        : null
                    : entry.ShortcutHint;

            string tooltip = entry.Tooltip
                          ?? (liveHint is null ? entry.Label : $"{entry.Label} ({liveHint})");

            // Connect button is the one row with a dual-icon (plug / unplug)
            // visual; everything else uses a single static glyph.
            string? alt = entry.ActionId == "ToggleConnection" ? "IconUnplug" : null;

            ToolbarButtonItem row = new(
                ToolbarItemKind.Button, entry.ActionId,
                label: entry.Label,
                iconResourceKey: entry.IconResourceKey,
                tooltip: tooltip,
                command: command,
                alternateIconResourceKey: alt);

            ApplyToolbarRowState(row);
            ToolbarItems.Add(row);
        }
    }

    // Mirrors current connection / capture state onto matching toolbar rows.
    private void SyncToolbarStateFlags(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsConnected)
         && e.PropertyName != nameof(IsConnecting)
         && e.PropertyName != nameof(IsDumping)
         && e.PropertyName != nameof(IsAutoCombatActive)
         && e.PropertyName != nameof(IsAutoNukeActive)
         && e.PropertyName != nameof(IsAutoHealRestActive)
         && e.PropertyName != nameof(IsAutoBlessActive)
         && e.PropertyName != nameof(IsAutoLightActive)
         && e.PropertyName != nameof(IsAutoGetItemsActive)
         && e.PropertyName != nameof(IsAutoGetCashActive)
         && e.PropertyName != nameof(IsAutoSneakActive)
         && e.PropertyName != nameof(IsAutoHideActive)
         && e.PropertyName != nameof(IsAutoSearchActive)
         && e.PropertyName != nameof(IsDisableHangupsActive)
         && e.PropertyName != nameof(IsAllAutoOff)) return;

        foreach (ToolbarButtonItem row in ToolbarItems)
        {
            if (row.IsButton) ApplyToolbarRowState(row);
        }
    }

    private void ApplyToolbarRowState(ToolbarButtonItem row)
    {
        switch (row.ActionId)
        {
            case "ToggleConnection":
                row.IsActive = IsConnecting;
                row.IsDanger = IsConnected;
                row.ShowAlternate = IsConnected;
                break;
            case "ToggleCapture":
                row.IsActive = IsDumping;
                break;
            case "ToggleDisableHangups":
                row.IsActive = IsDisableHangupsActive;
                break;
            case "ToggleAutoCombat":
                row.IsActive = IsAutoCombatActive;
                break;
            case "ToggleAutoNuke":
                row.IsActive = IsAutoNukeActive;
                break;
            case "ToggleAutoHealRest":
                row.IsActive = IsAutoHealRestActive;
                break;
            case "ToggleAutoBless":
                row.IsActive = IsAutoBlessActive;
                break;
            case "ToggleAutoLight":
                row.IsActive = IsAutoLightActive;
                break;
            case "ToggleAutoGetItems":
                row.IsActive = IsAutoGetItemsActive;
                break;
            case "ToggleAutoGetCash":
                row.IsActive = IsAutoGetCashActive;
                break;
            case "ToggleAutoSneak":
                row.IsActive = IsAutoSneakActive;
                break;
            case "ToggleAutoHide":
                row.IsActive = IsAutoHideActive;
                break;
            case "ToggleAutoSearch":
                row.IsActive = IsAutoSearchActive;
                break;
            case "ToggleAllAutoOff":
                // Depressed = auto-responses running; inverse of "all off".
                row.IsActive = !IsAllAutoOff;
                break;
            case "MovementStart":
            {
                // Always enabled: idle → run staged / open Manage; USER-paused →
                // resume; already running → open Manage (Go To) so the user can
                // switch where they're pathing mid-run. Tooltip keys off
                // IsUserPaused, not IsPaused — an engine wait (a mid-walk fight, a
                // rest) must not read as "Resume"; only the user's own pause does.
                Game.Map.MovementController ctl = AppServices.Current.MovementControl;
                row.IsActionEnabled = true;
                // Depress while a movement engine is in progress (loop / goto /
                // auto-lair, running or paused); back to default the moment
                // movement stops (idle). OnMovementControlStateChanged re-runs this.
                row.IsActive = ctl.IsActive;
                row.Tooltip = ctl.IsUserPaused
                    ? "Resume movement"
                    : "Start movement — run the staged loop, or open Manage to pick / switch one";
                break;
            }
            case "MovementPause":
            {
                // Pure pause: enabled while an engine is active and the user
                // hasn't already paused it. Stays enabled through engine waits so
                // the user can stack a manual pause on top of a fight/rest.
                Game.Map.MovementController ctl = AppServices.Current.MovementControl;
                row.IsActionEnabled = ctl.IsActive && !ctl.IsUserPaused;
                break;
            }
            case "MovementStop":
                row.IsActionEnabled = AppServices.Current.MovementControl.IsActive;
                break;
        }
    }

    private void OnDisplayChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Services.DisplayConfig.FontSize))
        {
            OnPropertyChanged(nameof(TerminalFontSize));
        }
        else if (e.PropertyName == nameof(Services.DisplayConfig.FontFamily))
        {
            OnPropertyChanged(nameof(TerminalFontFamily));
        }
        else if (e.PropertyName == nameof(Services.DisplayConfig.ScaleToWindow))
        {
            OnPropertyChanged(nameof(ScaleTerminalToWindow));
        }
        else if (e.PropertyName == nameof(Services.DisplayConfig.SplashAnimate))
        {
            // Setting the bound property flips TerminalControl.SplashAnimate,
            // whose class handler rebuilds the animator — so unchecking the
            // startup-animation toggle stops the running splash on the spot.
            SplashAnimate = AppServices.Current.Display.SplashAnimate;
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

    // Resize the live emulator screen and (if connected) re-advertise the
    // new dimensions to the BBS via Telnet NAWS. Reads from DisplayConfig
    // so any caller that wrote into it picks up the same source of truth.
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

    // Repaint the status-bar tick countdowns. Source-of-truth:
    // Game.TickEngine.TimeToNextCombatTick for combat; Game.RegenTracker
    // for HP / MA. HP and MA show the natural cycle by default and append
    // the bonus cycle (rest / medi) when the player is resting or
    // meditating — the two cycles have independent anchors and can be
    // desynced.
    private void OnRoomTrackerStateChanged(Game.Map.RoomTransition t)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // A genuine room change leaves the looked-at target behind — clear its
            // HP window so the status bar never shows a stale range. Confidence-only
            // flips (same room re-confirmed) keep it.
            if (t.NewRoom is not null && t.PreviousRoom is not null
                && !t.PreviousRoom.Key.Equals(t.NewRoom.Key))
                TargetHpText = "";
            RefreshLocationSlot();
        });

    private void OnMonsterLookTarget(Game.MonsterLookObserved obs)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Gated by Settings → Other "Show monster HP lookup" (default on).
            if (!AppServices.Current.Resolver
                    .Resolve<Models.Profile.OtherSettings>("Other").ShowMonsterHpLookup)
                return;
            string hp = obs.Estimate.Describe();
            TargetHpText = $"TGT HP: {hp}";
            // Also drop a yellow line into the terminal scrollback so the estimate
            // is logged, not only shown in the transient status slot.
            WriteTerminalStatus($"[{obs.Name} remaining Hitpoints: {hp}]", TerminalStatusKind.Notice);
        });

    private void OnMonsterDied(Game.Combat.MonsterDeathEvent _)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => TargetHpText = "");

    private void OnQuestAvailable(string questName)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            WriteTerminalStatus($"[{questName} Quest is Now Available]", TerminalStatusKind.Notice));

    // The login sequence sends stat / exp / inventory (and the user's who, etc.) right
    // after entering the realm. Wait for that to finish rendering, then dump the quests
    // this character can now begin so the lines land as a clean block at the end of login.
    private static async Task AnnounceAvailableQuestsAfterLoginAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => AppServices.Current.QuestAvailability.AnnounceLoginAvailable());
    }

    private void OnRecoveryFailed(Game.Map.RecoveryFailedEvent e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowLostRecoveryDialogAsync(e));

    private async void ShowLostRecoveryDialogAsync(Game.Map.RecoveryFailedEvent e)
    {
        var vm = new ViewModels.Navigation.LostRecoveryDialogViewModel(e.EngineName, e.Detail);
        await AppServices.Current.Dialogs
            .OpenWindowAsync<ViewModels.Navigation.LostRecoveryDialogViewModel, bool>(vm);
    }

    private void OnRecoveryTierChanged(Game.Map.RecoveryTierChangedEvent _)
        => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshRecoveryTierBools);

    // True when the engine-recovery gate is in tier 2 — engine chip border goes yellow.
    public bool IsTier2 => _isTier2;
    // True when the engine-recovery gate is in tier 3 — engine chip border goes red.
    public bool IsTier3 => _isTier3;
    private bool _isTier2;
    private bool _isTier3;

    private void RefreshRecoveryTierBools()
    {
        Game.Map.TierLevel tier = AppServices.Current.Recovery.CurrentTier;
        bool tier2 = tier == Game.Map.TierLevel.Tier2;
        bool tier3 = tier == Game.Map.TierLevel.Tier3;
        if (tier2 != _isTier2)
        {
            _isTier2 = tier2;
            OnPropertyChanged(nameof(IsTier2));
        }
        if (tier3 != _isTier3)
        {
            _isTier3 = tier3;
            OnPropertyChanged(nameof(IsTier3));
        }
    }

    // Rooms we've already prompted about this session. Prevents asking
    // the same yes/no twice when the player re-enters or the tracker
    // re-observes the same null-name room.
    private readonly HashSet<Game.Map.RoomKey> _nameLearnedPrompted = new();

    private void OnRoomNameLearned(Game.Map.NameLearnedEvent e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => PromptForLearnedNameAsync(e));

    private async void PromptForLearnedNameAsync(Game.Map.NameLearnedEvent e)
    {
        // Session-dedupe — one prompt per (RoomKey) per app run.
        if (!_nameLearnedPrompted.Add(e.Key)) return;

        var vm = new ViewModels.RoomNameLearnedDialogViewModel(e.Key, e.ObservedName);
        bool save = await AppServices.Current.Dialogs
            .OpenWindowAsync<ViewModels.RoomNameLearnedDialogViewModel, bool>(vm);
        if (!save) return;

        bool ok = AppServices.Current.RoomNamePersist.Persist(e.Key, e.ObservedName);
        if (!ok)
        {
            AppServices.Current.Log.Log(Services.LogSeverity.Warn, "Main",
                $"Failed to persist learned name '{e.ObservedName}' for {e.Key}.");
        }
    }

    private void RefreshLocationSlot()
    {
        // Loop status takes over the location slot while a loop is active. While
        // the runner is still walking to the loop's entry (Approaching), the slot
        // shows the same walk-to readout as a plain goto — current room,
        // destination, remaining steps, rate — because we haven't begun the loop
        // yet. Only once the circle is actually running does it collapse to the
        // terse lap counter + rate (the CURRENT NAV pane owns per-step detail).
        Game.Map.LoopRunner runner = AppServices.Current.LoopRunner;
        if (runner.State != Game.Map.LoopState.Idle && runner.CurrentLoop is not null)
        {
            if (runner.State == Game.Map.LoopState.Approaching)
            {
                LocationText = BuildWalkLocationText();
                return;
            }
            double xpHr = AppServices.Current.SessionActivity.Snapshot().ExperiencePerHour;
            LocationText = $"lap {runner.CompletedLaps + 1} · {RateWithTnl(xpHr)}";
            return;
        }

        // A plain walk-to (goto / favourite) gets the same C/D/Steps/rate readout
        // as a loop approach. Checked before the tracker fallback below, which
        // would otherwise win (the current room is non-null throughout a walk).
        if (AppServices.Current.Walker.State is Game.Map.WalkState.Walking or Game.Map.WalkState.Paused
            && AppServices.Current.Walker.Destination is not null)
        {
            LocationText = BuildWalkLocationText();
            return;
        }

        Game.Map.RoomState state = AppServices.Current.RoomTracker.State;
        Game.Map.Room? room = state.CurrentRoom;
        // Without an active game-data set the room graph is empty, so the
        // tracker can never locate us — every state reads "Lost", which
        // misleads a fresh profile into thinking something broke. Surface
        // the real cause instead and point the user at the fix. Clears
        // automatically once a set loads (RoomGraph.GraphReloaded → here).
        if (room is null && AppServices.Current.RoomGraph.RoomCount == 0)
        {
            LocationText = "Load a game data set to use navigation";
            return;
        }
        // Map/room number + the session exp rate — no room name. Names run long
        // ("Newhaven, Arena", …) and were overflowing the narrow status slot,
        // pushing the rate behind an ellipsis exactly when the name was longest.
        // The map/room key is always short and identifies the room just as well
        // for a player watching the strip.
        if (room is not null)
        {
            double xpHr = AppServices.Current.SessionActivity.Snapshot().ExperiencePerHour;
            LocationText = $"{room.Key} · {RateWithTnl(xpHr)}";
            return;
        }
        LocationText = state.Confidence switch
        {
            Game.Map.RoomConfidence.Pending        => "Pending move…",
            Game.Map.RoomConfidence.Lost           => "Lost — pick a room on the map",
            Game.Map.RoomConfidence.PendingRespawn => "Awaiting respawn…",
            _                                      => "Unknown location",
        };
    }

    // "C: M/R  D: M/R  Steps: N - rate/hr" — the walk-to readout shared by a
    // plain goto and a loop's approach leg. Remaining steps = total path length
    // minus the next-step index (CurrentStepIndex), clamped at 0.
    private static string BuildWalkLocationText()
    {
        Game.Map.AutoWalkManager walker = AppServices.Current.Walker;
        Game.Map.Room? here = AppServices.Current.RoomTracker.State.CurrentRoom;
        string cur = here is { } r ? r.Key.ToString() : "?";
        string dest = walker.Destination is { } d ? d.ToString() : "?";
        int remaining = Math.Max(0, walker.StepCount - walker.CurrentStepIndex);
        double xpHr = AppServices.Current.SessionActivity.Snapshot().ExperiencePerHour;
        return $"C: {cur} D: {dest} Steps: {remaining} - {RateWithTnl(xpHr)}";
    }

    // "<rate>/hr" with " - TNL: <time>" appended when the time-to-next-level can be
    // computed. Uses the SAME estimate as the Session Stats "time to next level"
    // readout (banked-aware target level + game-data exp chart) so the two never
    // drift — an earlier stat-line "exp to next" ÷ rate ignored banked levels and
    // desynced from Session Stats.
    private static string RateWithTnl(double xpHr)
    {
        string rate = $"{Game.Combat.RateText.Compact(xpHr)}/hr";
        if (xpHr <= 0) return rate;
        if (Game.Calculators.TimeToLevelEstimator.Estimate(
                AppServices.Current.PlayerStats, AppServices.Current.GameData, xpHr).Eta is not { } tnl)
            return rate;
        return $"{rate} - TNL: {(tnl <= TimeSpan.Zero ? "ready"
            : Game.Calculators.ExperienceTableCalculator.FormatTimeToLevel(tnl))}";
    }

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

    // Single Connect ↔ Disconnect action. Click while idle starts a
    // connect attempt (with auto-retry on failure); click while a connect
    // is in flight cancels it; click while connected disconnects.
    //
    // AllowConcurrentExecutions = true matters: CommunityToolkit.Mvvm's
    // default AsyncRelayCommand behaviour is to disable the command while
    // the task is running, which would mean a second click during a
    // long-running connect attempt does nothing — the cancel path would be
    // unreachable. With concurrent executions allowed, the second click
    // re-enters this method and hits the IsConnecting branch, which cancels
    // the in-flight attempt.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleConnectionAsync()
    {
        // Re-entry guard: a fast double-click while the disconnect
        // path is still flushing Logoff events would otherwise hit the
        // "if (!IsConnected) ... ConnectWithRetriesAsync()" branch and
        // immediately try to dial again. The disconnect path flips
        // IsDisconnecting=true at its start; this short-circuit makes
        // every subsequent click a no-op until the disconnect settles.
        if (IsDisconnecting) return;
        if (IsConnected)
        {
            // User-initiated disconnect path — prompt if the Confirm
            // hangup flag is on. Programmatic disconnects (carrier-lost
            // auto-reconnect cycle, remote @hangup, future health-
            // threshold drops) call DisconnectInternalAsync directly
            // and bypass this prompt.
            if (!await AppServices.Current.Confirm.ConfirmHangupAsync()) return;
            await DisconnectInternalAsync();
            return;
        }
        if (IsConnecting)       { _connectCts?.Cancel();             return; }
        // Auto-reconnect armed (predictive cleanup OR reactive carrier-lost):
        // first click cancels the pending redial — the user is opting out of
        // the loop, not asking to dial immediately. They can click again to
        // dial if that's what they actually wanted.
        if (IsReconnectPending)
        {
            _reactiveReconnectCount = 0;
            CancelCleanupReconnect("user clicked Cancel reconnect");
            return;
        }
        _reactiveReconnectCount = 0;
        await ConnectWithRetriesAsync();
    }

    // Grace before the emergency-hangup socket close so the just-sent realm exit
    // command clears the wire — the send is fire-and-forget and the disconnect
    // doesn't drain pending writes, so closing on the same beat could swallow it.
    private static readonly TimeSpan HangupExitFlushDelay = TimeSpan.FromMilliseconds(750);

    // HealthManager's low-HP hangup has just sent the realm exit command down the
    // raw wire; now hard-close the carrier ourselves instead of trusting the
    // server to drop it. Marshalled to the UI thread (health evaluation already
    // runs there, but this stays correct if that ever changes), delayed so the
    // exit command flushes, and a no-op if the server beat us to the drop.
    private void RequestHangupDisconnect()
    {
        if (Dispatcher.UIThread.CheckAccess()) _ = HangupDisconnectAfterFlushAsync();
        else Dispatcher.UIThread.Post(() => _ = HangupDisconnectAfterFlushAsync());
    }

    private async Task HangupDisconnectAfterFlushAsync()
    {
        // Runs on the UI thread; Task.Delay resumes on the captured Avalonia
        // context, so the socket/VM teardown stays on-thread. Tear down bare (not
        // DisconnectInternalAsync) so the Disconnected handler still consumes the
        // HangupSignal and classifies the drop as HangupInitiated. Guard on
        // IsConnected so a server that already dropped us inside the flush window
        // (its handler leaves _telnet non-null) doesn't get a redundant teardown.
        await Task.Delay(HangupExitFlushDelay);
        if (IsConnected) await TearDownConnectionAsync();
    }

    private async Task DisconnectInternalAsync()
    {
        // Mark the user-initiated nature BEFORE we close the socket, so
        // the Disconnected event handler (which races with the await
        // below) can distinguish this from a server-side drop and skip
        // the carrier-lost auto-reconnect.
        _userInitiatedDisconnect = true;
        _lastDisconnectCause = DisconnectCause.UserInitiated;
        _reactiveReconnectCount = 0;
        await TearDownConnectionAsync();
    }

    // Close the socket + tear down the session without claiming a cause. The
    // Disconnected event handler classifies the drop (user / hangup / server);
    // callers that need a specific cause set it before calling. The emergency
    // hangup uses this bare form so the handler falls through to
    // HangupSignal.ConsumeDisconnectIntent() — classifying it as HangupInitiated
    // and consuming the intent flag, exactly as a server-side drop after `=x`
    // did. Routing it through DisconnectInternalAsync instead would stamp
    // UserInitiated and leave that hangup flag unconsumed to poison the next drop.
    private async Task TearDownConnectionAsync()
    {
        // Flip IsDisconnecting so the toolbar label updates to
        // "Disconnecting…" immediately + a fast double-click hits the
        // re-entry guard in ToggleConnectionAsync instead of flipping
        // straight back into a reconnect.
        IsDisconnecting = true;

        // NB: Logoff events DON'T fire here. The button is a "close the
        // wire now" affordance — there's no time to flush bank /
        // store-gear / etc. cleanup commands. Logoff events instead fire
        // when CleanupWarningWatcher recognises the BBS announcing
        // upcoming shutdown (EventScheduler subscribes to it directly).

        try
        {
            TelnetClient? t = _telnet;
            _telnet = null;
            DetachLoginKillSwitch();
            _automator?.Dispose();
            _automator = null;
            if (t is not null) await t.DisposeAsync();
            IsConnected = false;
        }
        finally { IsDisconnecting = false; }

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

        // Per-BBS retry knobs. ReconnectOnFailedConnect gates the loop:
        // when off, the user gets one shot and we surface the error — no
        // silent retries. When on, the loop runs up to MaxRedials with
        // RedialPauseSeconds between attempts. Defaults fall through to
        // a 1-attempt floor if a BBS has bogus values. InfiniteRetries
        // overrides both: unlimited attempts at a fixed 3s pause (still
        // gated on ReconnectOnFailedConnect — it changes the count/pause,
        // not whether we retry at all).
        BbsProfile? activeBbs = ResolveActiveBbs();
        bool infiniteRetries = activeBbs?.InfiniteRetries ?? false;
        int maxAttempts = (activeBbs?.ReconnectOnFailedConnect ?? false)
            ? (infiniteRetries ? int.MaxValue : Math.Max(1, activeBbs?.MaxRedials ?? 1))
            : 1;
        TimeSpan retryDelay = infiniteRetries
            ? TimeSpan.FromSeconds(3)
            : TimeSpan.FromSeconds(Math.Max(1, activeBbs?.RedialPauseSeconds ?? 5));
        string ofMax = infiniteRetries ? "" : $"/{maxAttempts}";

        _connectCts = new CancellationTokenSource();
        IsConnecting = true;
        try
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (_connectCts.IsCancellationRequested) break;

                WriteTerminalStatus($"[CONNECTING TO: {Host} {Port}]", TerminalStatusKind.Notice);
                AppServices.Current.Log.Info("Connect",
                    $"Connecting to {Host}:{Port} (attempt {attempt}{ofMax})…");

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
                    _lastDisconnectCause = DisconnectCause.None;
                    ArmLoginAutomator(client);
                    return;  // success — IsConnected flips via Connected event handler.
                }
                catch (OperationCanceledException) when (_connectCts.IsCancellationRequested)
                {
                    // User clicked the toolbar / menu again — propagate as cancel.
                    await client.DisposeAsync();
                    WriteTerminalStatus("[CONNECT CANCELLED]", TerminalStatusKind.Notice);
                    AppServices.Current.Log.Info("Connect", "Connect cancelled.");
                    _lastDisconnectCause = DisconnectCause.UserInitiated;
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

                if (attemptFailed && attempt < maxAttempts)
                {
                    int seconds = (int)retryDelay.TotalSeconds;
                    WriteTerminalStatus($"[RETRYING IN: {seconds} SECONDS...]",
                                        TerminalStatusKind.Notice);
                    try
                    {
                        await Task.Delay(retryDelay, _connectCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        WriteTerminalStatus("[CONNECT CANCELLED]", TerminalStatusKind.Notice);
                        AppServices.Current.Log.Info("Connect", "Connect cancelled.");
                        _lastDisconnectCause = DisconnectCause.UserInitiated;
                        return;
                    }
                }
            }

            // Loop fell through — every attempt failed.
            _lastDisconnectCause = DisconnectCause.FailedConnect;
            WriteTerminalStatus($"[GAVE UP AFTER {maxAttempts} ATTEMPT{(maxAttempts == 1 ? "" : "S")}.]",
                                TerminalStatusKind.Error);
            AppServices.Current.Log.Error("Connect",
                $"Gave up after {maxAttempts} attempt(s).");
        }
        finally
        {
            IsConnecting = false;
            _connectCts?.Dispose();
            _connectCts = null;
        }
    }

    // Looks up the matching BbsProfile by host, pulls the loaded
    // character's credentials for that BBS, and arms a LoginAutomator
    // against the live socket. No-op when no BBS record matches, no
    // profile is loaded, or the credentials are missing — the user just
    // gets the raw login prompt.
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
            // Keep the main-menu-entry latch primed for the menu that renders
            // right after the final step. Auto-entry was ARMED at login start
            // (below) and refreshed on each step; here we just extend the window
            // once more for the imminent menu.
            AppServices.Current.MainMenuEntry.KeepArmed();
            // Once the post-entry refresh (stat / inventory / who) has printed, dump the
            // quests this character can now start — once, at the tail of the login sequence.
            _ = AnnounceAvailableQuestsAfterLoginAsync();
        };
        // Keep auto-entry armed while the login actively progresses, so the
        // realm-entry menu still fires the entry command even if a trailing
        // (mis-authored / MegaMUD-holdover) step never matches and LoggedIntoGame
        // never comes — the reported "did not re-enter on disconnect" case.
        automator.StepAdvanced += () => AppServices.Current.MainMenuEntry.KeepArmed();
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
            string? pending = a.PendingWaitPattern;
            a.Dispose();
            _automator = null;
            DetachLoginKillSwitch();
            // When the automator was still MID-sequence at this point (common on a
            // carrier-lost relog whose final menu prompt differs from a fresh
            // login), name the step it stalled on and the prompt it never saw — so
            // a capture explains why auto-entry didn't fire, instead of just "5/6".
            AppServices.Current.Log.Info("LoginAuto",
                stepsRun >= stepsTotal
                    ? $"In-game prompt observed — force-disposed automator for '{bbsName}' after {stepsRun}/{stepsTotal} step(s) (all steps had matched)."
                    : $"In-game prompt observed — force-disposed automator for '{bbsName}' after {stepsRun}/{stepsTotal} step(s); it was still awaiting step {stepsRun + 1}/{stepsTotal} (waiting for: \"{pending}\") — that menu prompt never arrived on this connect, so auto-entry never armed.");
        };
        scanner.PromptObserved += handler;
        _loginKillSwitch = handler;

        // Arm auto-entry at the START of the login (not on completion). This is
        // the single point that decides authorization for the connect: Arm()
        // consumes the hangup-suppression flag, so a login that follows a
        // health/manual hangup is NOT authorized and no later step can re-open
        // it. On an authorized login (fresh profile load, reconnect after an
        // unexpected drop, cleanup relog) the window stays primed via KeepArmed
        // as steps advance, so the realm-entry menu fires the entry command even
        // when the user's nav steps don't cleanly end at "in game".
        AppServices.Current.MainMenuEntry.Arm();

        automator.Start();
    }

    private void DetachLoginKillSwitch()
    {
        if (_loginKillSwitch is null) return;
        AppServices.Current.PromptScanner.PromptObserved -= _loginKillSwitch;
        _loginKillSwitch = null;
    }

    // ----- Cleanup-warning auto-reconnect ------------------------------

    private void OnCleanupWarningObserved(CleanupWarning warning)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // When ReconnectAfterCleanup is on the CleanupLogoutOrchestrator
            // takes over: it waits for a safe room, exits to the menu, and
            // drops the carrier for us — so the banner promises the managed
            // flow rather than telling the user to quit manually.
            bool autoLogout = ResolveActiveBbs()?.ReconnectAfterCleanup ?? false;
            string banner = autoLogout
                ? $"[CLEANUP WARNING — BBS GOES DOWN IN {warning.MinutesRemaining} MIN — WILL AUTO-LOG-OFF AT A SAFE ROOM, THEN RECONNECT.]"
                : $"[CLEANUP WARNING — BBS GOES DOWN IN {warning.MinutesRemaining} MIN — QUIT AT A SAFE ROOM TO ARM AUTO-RECONNECT.]";
            WriteTerminalStatus(banner, TerminalStatusKind.Notice);
            AppServices.Current.Log.Warn("Cleanup",
                $"Server announced shutdown in {warning.MinutesRemaining} minute(s) at {warning.ObservedAt.LocalDateTime:HH:mm:ss}.");
        });
    }

    private void OnCleanupModeDetected()
    {
        // Fires on the wire feed thread; marshal to UI before logging.
        // We deliberately don't drop a second terminal banner here —
        // the auto-reconnect armed banner already says "BBS IN
        // CLEANUP" in its reason label, so the user gets one message,
        // not two redundant ones stacking on top of each other.
        Dispatcher.UIThread.Post(() =>
        {
            AppServices.Current.Log.Warn("Cleanup",
                "Server returned 'this system is not available' — BBS is in cleanup mode right now.");
        });
    }

    // ----- Stable-connection counter reset -----------------------------

    // Arm a one-shot 30s timer that resets _reactiveReconnectCount only
    // if the connection stays alive for the full window. A flap inside
    // the window (BBS-in-cleanup connect → unavailable banner → drop)
    // cancels the timer and the counter keeps climbing toward
    // BbsProfile.MaxRedials.
    private void ArmStableConnectionReset()
    {
        CancelStableConnectionReset();
        _stableConnectionResetCts = new CancellationTokenSource();
        CancellationToken token = _stableConnectionResetCts.Token;
        _ = Task.Delay(TimeSpan.FromSeconds(StableConnectionWindowSeconds), token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsConnected) return;
                if (_reactiveReconnectCount > 0)
                    AppServices.Current.Log.Info("Reconnect",
                        $"Connection stable for {StableConnectionWindowSeconds}s; resetting redial counter (was {_reactiveReconnectCount}).");
                _reactiveReconnectCount = 0;
                _stableConnectionResetCts?.Dispose();
                _stableConnectionResetCts = null;
            });
        }, TaskScheduler.Default);
    }

    private void CancelStableConnectionReset()
    {
        if (_stableConnectionResetCts is null) return;
        try { _stableConnectionResetCts.Cancel(); } catch { }
        _stableConnectionResetCts.Dispose();
        _stableConnectionResetCts = null;
    }

    // ----- Live reconnect countdown ------------------------------------

    // Start (or restart) the status-bar countdown for an armed reconnect
    // that fires after delay. Short delays (< ReconnectCountdownThresholdSeconds)
    // don't get a countdown — the status bar would just flash a
    // "5s, 4s, 3s…" indicator for a moment before the dial fires.
    private void StartReconnectCountdown(TimeSpan delay)
    {
        if (delay.TotalSeconds < ReconnectCountdownThresholdSeconds)
        {
            StopReconnectCountdown();
            return;
        }
        _reconnectFireAt = DateTimeOffset.UtcNow + delay;
        RefreshReconnectCountdownText();
        _reconnectCountdownTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _reconnectCountdownTimer.Tick -= OnReconnectCountdownTick;
        _reconnectCountdownTimer.Tick += OnReconnectCountdownTick;
        _reconnectCountdownTimer.Start();
    }

    private void StopReconnectCountdown()
    {
        _reconnectCountdownTimer?.Stop();
        if (_reconnectCountdownTimer is not null)
            _reconnectCountdownTimer.Tick -= OnReconnectCountdownTick;
        ReconnectCountdownText = string.Empty;
    }

    private void OnReconnectCountdownTick(object? sender, EventArgs e)
        => RefreshReconnectCountdownText();

    private void RefreshReconnectCountdownText()
    {
        TimeSpan remaining = _reconnectFireAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero || !IsReconnectPending)
        {
            StopReconnectCountdown();
            return;
        }
        ReconnectCountdownText = $"Reconnect in {FormatDelay(remaining)}";
    }

    // On disconnect, if a cleanup warning was observed during this session
    // AND the active BBS has BbsProfile.ReconnectAfterCleanup enabled, arm
    // a one-shot reconnect at the moment we think the BBS is back online.
    // Formula:
    //   shutdown_at = warning_observed_at + warning_minutes_remaining
    //   reconnect_at = max(now, shutdown_at) + BBS.CleanupPeriodMinutes
    // Handles both the clean-quit-before-shutdown case (we take the long
    // path: full warning countdown + user-set cleanup duration) and the
    // dirty-shutdown case (the max collapses to now + cleanup, since
    // shutdown_at has already passed).
    private void TryScheduleCleanupReconnect()
    {
        CleanupWarning? maybeWarning = AppServices.Current.Cleanup.Latest;
        if (maybeWarning is not { } warning) return;

        // Intentional hangup beats a stale cleanup warning. If the
        // user observed a shutdown notice earlier in the session AND
        // then chose to hang up (or a hangup-automation engine did it
        // for them), the hangup intent is the more recent signal — the
        // user is presumed to be in a dangerous spot and shouldn't
        // be auto-redialed just because a cleanup warning was on file.
        if (_lastDisconnectCause == DisconnectCause.HangupInitiated)
        {
            AppServices.Current.Log.Debug("Cleanup",
                "Warning observed but disconnect was hangup-initiated — not scheduling.");
            return;
        }

        BbsProfile? bbs = ResolveActiveBbs();
        if (bbs is null || !bbs.ReconnectAfterCleanup)
        {
            AppServices.Current.Log.Debug("Cleanup",
                "Warning observed but ReconnectAfterCleanup is off — not scheduling.");
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        DateTimeOffset shutdownAt = warning.EstimatedShutdownAt;
        DateTimeOffset reconnectAt =
            (shutdownAt > now ? shutdownAt : now) +
            TimeSpan.FromMinutes(Math.Max(0, bbs.CleanupPeriodMinutes));

        TimeSpan delay = reconnectAt - now;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        CancelCleanupReconnect(reason: null);
        _cleanupReconnectCts = new CancellationTokenSource();
        NotifyReconnectPendingChanged();
        CancellationToken token = _cleanupReconnectCts.Token;

        string when = reconnectAt.LocalDateTime.ToString("HH:mm:ss");
        int minutes = (int)delay.TotalMinutes;
        int seconds = delay.Seconds;
        WriteTerminalStatus(
            $"[AUTO-RECONNECT ARMED — DIALING AT {when} (IN {minutes}m{seconds:D2}s). PRESS CONNECT TO CANCEL.]",
            TerminalStatusKind.Notice);
        AppServices.Current.Log.Info("Cleanup",
            $"Reconnect scheduled at {when} — warning observed at " +
            $"{warning.ObservedAt.LocalDateTime:HH:mm:ss} with {warning.MinutesRemaining}m remaining " +
            $"+ {bbs.CleanupPeriodMinutes}m cleanup period.");

        StartReconnectCountdown(delay);

        _ = Task.Delay(delay, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (IsConnected || IsConnecting) return;
                _cleanupReconnectCts?.Dispose();
                _cleanupReconnectCts = null;
                NotifyReconnectPendingChanged();
                StopReconnectCountdown();
                AppServices.Current.Cleanup.Reset();
                _ = ConnectWithRetriesAsync();
            });
        }, TaskScheduler.Default);
    }

    private void CancelCleanupReconnect(string? reason)
    {
        if (_cleanupReconnectCts is null) return;
        try { _cleanupReconnectCts.Cancel(); } catch { }
        _cleanupReconnectCts.Dispose();
        _cleanupReconnectCts = null;
        NotifyReconnectPendingChanged();
        StopReconnectCountdown();
        if (reason is not null)
        {
            AppServices.Current.Log.Info("Cleanup", $"Auto-reconnect cancelled — {reason}.");
            WriteTerminalStatus("[AUTO-RECONNECT CANCELLED.]", TerminalStatusKind.Notice);
        }
    }

    // Fires PropertyChanged for the bindings that depend on whether a
    // reconnect is armed. Called from every site that flips the CTS
    // between null and an instance so the toolbar / menu label updates.
    private void NotifyReconnectPendingChanged()
    {
        OnPropertyChanged(nameof(IsReconnectPending));
        OnPropertyChanged(nameof(ConnectionLabel));
    }

    // Distinguish a server-side carrier drop from a TCP-keepalive timeout.
    // The connection was alive; now the socket died. If keepalive was
    // enabled on the active BBS AND the wire was silent for longer than
    // the configured idle window before the drop, attribute to
    // DisconnectCause.NoResponse; otherwise DisconnectCause.CarrierLost.
    // The threshold gets a small grace (+5s) so a server that responded
    // just before the idle window closes doesn't get mis-classified as
    // silent.
    private DisconnectCause ClassifyServerSideDrop()
    {
        BbsProfile? bbs = ResolveActiveBbs();
        int idle = bbs?.NoResponseTimeoutSeconds ?? 0;
        if (idle <= 0) return DisconnectCause.CarrierLost;

        DateTimeOffset lastRead = _telnet?.LastDataReceived ?? DateTimeOffset.MinValue;
        if (lastRead == DateTimeOffset.MinValue) return DisconnectCause.NoResponse;

        double silentSeconds = (DateTimeOffset.UtcNow - lastRead).TotalSeconds;
        return silentSeconds >= idle + 5
            ? DisconnectCause.NoResponse
            : DisconnectCause.CarrierLost;
    }

    // ----- Reactive auto-reconnect (carrier-lost / failed-connect / no-response) ------

    // Arm a reactive reconnect if the relevant BbsProfile toggle matches
    // _lastDisconnectCause. Shares _cleanupReconnectCts with the predictive
    // cleanup scheduler so only one reconnect can be pending at a time.
    // Never fires for DisconnectCause.UserInitiated or
    // DisconnectCause.HangupInitiated regardless of any toggle state —
    // both are explicit "don't dial back" signals (user clicked
    // Disconnect, or an automation hung us up because we were in a
    // dangerous spot).
    private void TryScheduleReactiveReconnect()
    {
        BbsProfile? bbs = ResolveActiveBbs();
        if (bbs is null) return;

        // FailedConnect is fully handled inside ConnectWithRetriesAsync
        // (its retry-loop IS the response to ReconnectOnFailedConnect);
        // UserInitiated and HangupInitiated never auto-retry by policy.
        // That leaves CarrierLost / NoResponse — each gated on its own
        // toggle.
        bool shouldRetry = _lastDisconnectCause switch
        {
            DisconnectCause.CarrierLost => bbs.ReconnectOnCarrierLost,
            DisconnectCause.NoResponse  => bbs.ReconnectOnNoResponse,
            _ => false,
        };
        if (!shouldRetry) return;

        // Redial budget — same MaxRedials knob the in-flight retry loop
        // uses. Stop arming once we've burned through it; the user can
        // still click Connect manually. InfiniteRetries removes the budget
        // entirely: we never give up (and the pause below drops to 3s).
        bool infinite = bbs.InfiniteRetries;
        int maxRedials = Math.Max(1, bbs.MaxRedials);
        string ofBudget = infinite ? "" : $"/{maxRedials}";
        _reactiveReconnectCount++;
        if (!infinite && _reactiveReconnectCount > maxRedials)
        {
            WriteTerminalStatus(
                $"[AUTO-RECONNECT GAVE UP AFTER {maxRedials} REDIAL{(maxRedials == 1 ? "" : "S")}.]",
                TerminalStatusKind.Error);
            AppServices.Current.Log.Error("Reconnect",
                $"Reactive reconnect budget exhausted ({maxRedials}).");
            _reactiveReconnectCount = 0;
            return;
        }

        // Cleanup-mode override: BBS just rejected us with "this
        // system is not available". A 5s redial loop would hammer
        // it pointlessly — switch to a single long-delay attempt at
        // the BBS's CleanupPeriodMinutes setting (default 0 → fall
        // back to RedialPauseSeconds so the behaviour is unchanged
        // when the user hasn't configured the field).
        // Cleanup-mode's long-delay override still wins (its CleanupPeriodMinutes
        // ticker stays live under InfiniteRetries) — no point hammering a BBS
        // that's mid-maintenance every 3s. Otherwise InfiniteRetries forces 3s.
        bool cleanupMode = AppServices.Current.Cleanup.InCleanupMode;
        TimeSpan delay = cleanupMode && bbs.CleanupPeriodMinutes > 0
            ? TimeSpan.FromMinutes(bbs.CleanupPeriodMinutes)
            : TimeSpan.FromSeconds(infinite ? 3 : Math.Max(1, bbs.RedialPauseSeconds));

        _cleanupReconnectCts?.Cancel();
        _cleanupReconnectCts?.Dispose();
        _cleanupReconnectCts = new CancellationTokenSource();
        NotifyReconnectPendingChanged();
        CancellationToken token = _cleanupReconnectCts.Token;

        string reasonLabel = cleanupMode
            ? "BBS in cleanup"
            : _lastDisconnectCause == DisconnectCause.NoResponse
                ? "no response"
                : "carrier lost";

        // Banner phrasing: first arm of a disconnect cycle carries the
        // full "PRESS CONNECT TO CANCEL" instructional line; follow-up
        // arms inside the same cycle compress to the short progress
        // form so the terminal doesn't get spammed with the full text
        // ten times in a row.
        string bannerText = _reactiveReconnectCount == 1
            ? $"[AUTO-RECONNECT ARMED ({reasonLabel.ToUpperInvariant()}, REDIAL {_reactiveReconnectCount}{ofBudget}) — DIALING IN {FormatDelay(delay)}. PRESS CONNECT TO CANCEL.]"
            : $"[ATTEMPTING REDIAL {_reactiveReconnectCount}{ofBudget} IN {FormatDelay(delay)}.]";
        WriteTerminalStatus(bannerText, TerminalStatusKind.Notice);
        AppServices.Current.Log.Info("Reconnect",
            $"Reactive reconnect scheduled ({reasonLabel}, redial {_reactiveReconnectCount}{ofBudget}) in {FormatDelay(delay)}.");

        StartReconnectCountdown(delay);

        _ = Task.Delay(delay, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (IsConnected || IsConnecting) return;
                _cleanupReconnectCts?.Dispose();
                _cleanupReconnectCts = null;
                NotifyReconnectPendingChanged();
                StopReconnectCountdown();
                _ = ConnectWithRetriesAsync();
            });
        }, TaskScheduler.Default);
    }

    // Force an unconditional dial-back after a remote @relog. Unlike
    // TryScheduleReactiveReconnect this ignores the per-BBS reconnect
    // toggles — the sender explicitly asked to relog, so we always
    // complete the cycle. A short RedialPauseSeconds delay lets the
    // carrier fully drop before re-dialing; the normal login automation
    // runs on the reconnect (relog never suppresses the entry latch), so
    // the character ends up back in-game.
    private void ScheduleRelogReconnect()
    {
        BbsProfile? bbs = ResolveActiveBbs();
        TimeSpan delay = TimeSpan.FromSeconds(Math.Max(1, bbs?.RedialPauseSeconds ?? 5));

        _cleanupReconnectCts?.Cancel();
        _cleanupReconnectCts?.Dispose();
        _cleanupReconnectCts = new CancellationTokenSource();
        NotifyReconnectPendingChanged();
        CancellationToken token = _cleanupReconnectCts.Token;

        WriteTerminalStatus(
            $"[RELOG REQUESTED — DIALING BACK IN {FormatDelay(delay)}.]",
            TerminalStatusKind.Notice);
        AppServices.Current.Log.Info("Reconnect",
            $"Remote @relog — reconnecting in {FormatDelay(delay)}.");
        StartReconnectCountdown(delay);

        _ = Task.Delay(delay, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (IsConnected || IsConnecting) return;
                _cleanupReconnectCts?.Dispose();
                _cleanupReconnectCts = null;
                NotifyReconnectPendingChanged();
                StopReconnectCountdown();
                _ = ConnectWithRetriesAsync();
            });
        }, TaskScheduler.Default);
    }

    // Human-friendly delay rendering for the auto-reconnect banner / log.
    private static string FormatDelay(TimeSpan delay)
    {
        if (delay.TotalMinutes >= 1)
        {
            int min = (int)delay.TotalMinutes;
            int sec = delay.Seconds;
            return sec == 0 ? $"{min}m" : $"{min}m{sec:D2}s";
        }
        return $"{(int)delay.TotalSeconds}s";
    }

    // Resolve which BBS the connect target reads off of. Preference order:
    //   1. The BBS the loaded character profile lives under
    //      (ProfileService.CurrentBbsName).
    //   2. The first BBS in the global list (alphabetical), so a user on a
    //      blank draft can still click Connect without opening Settings
    //      first.
    // Returns null only when there's no profile, no pin AND zero BBSes
    // saved on disk.
    private static BbsProfile? ResolveActiveBbs()
        => AppServices.Current.ResolveActiveBbs();

    private void RefreshBbsBindings()
    {
        OnPropertyChanged(nameof(Host));
        OnPropertyChanged(nameof(Port));
        OnPropertyChanged(nameof(ActiveBbsName));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(BbsWebsiteUrl));
        OnPropertyChanged(nameof(HasBbsWebsite));
        OnPropertyChanged(nameof(ShowBbsWebsiteInHelp));

        // Mirror the title-bar identity onto the OS process name so
        // multiple instances are distinguishable in ps / top / htop.
        ProcessTitle.Set(AppServices.Current.Profile.CurrentProfileName, ResolveActiveBbs()?.Name);
    }

    // ProfileLoaded handler that wires in the Settings → General
    // "Auto-connect when profile loads" toggle. Runs the original
    // post-load refresh chain first, then — only when not already
    // connected/connecting, the loaded profile has a usable BBS pin, and
    // the GeneralSettings.AutoConnect flag is on — kicks off
    // ConnectWithRetriesAsync.
    //
    // async void is intentional: ProfileLoaded is an Action<CharacterProfile>
    // event and we want fire-and-forget on the connect attempt so the
    // caller (typically File → Open profile) doesn't block on the retry
    // loop. The connect path already self-marshals UI updates and never
    // throws to the caller.
    private async void OnProfileLoadedForConnect(Models.Profile.CharacterProfile _)
    {
        ClearQuickConnect();
        SyncProfileMenuState();
        RefreshBbsBindings();

        if (IsConnected || IsConnecting) return;

        Models.Profile.GeneralSettings general =
            AppServices.Current.Resolver.Resolve<Models.Profile.GeneralSettings>("General");
        if (!general.AutoConnect) return;

        // No usable BBS resolves → silently skip. Explicit Connect prints
        // the "no BBS selected" guidance; the auto-connect path doesn't
        // need to be noisy about something the user didn't manually trigger.
        if (ResolveActiveBbs() is null) return;
        if (string.IsNullOrWhiteSpace(Host) || Port <= 0) return;

        AppServices.Current.Log.Info("Connect", "Auto-connect on profile load — General → Auto-connect is on.");
        await ConnectWithRetriesAsync();
    }

    // ProfileMutated runs for every settings tab's Apply. We only want to
    // drop the Quick Connect override when the BBS pin itself changed —
    // display / toolbar / statline edits shouldn't kick the user off a
    // quick-dialled target.
    private void OnProfileMutatedForBbs()
    {
        string? current = ResolveActiveBbs()?.Name;
        if (!string.Equals(current, _lastSeenBbsName, StringComparison.Ordinal))
        {
            ClearQuickConnect();
        }
        _lastSeenBbsName = current;
        RefreshBbsBindings();
    }

    // Drops the Quick Connect override and pushes the BBS-derived bindings
    // back into the title bar / connect button.
    private void ClearQuickConnect()
    {
        if (_quickConnectTarget is null) return;
        _quickConnectTarget = null;
        RefreshBbsBindings();
    }

    private TelnetClient BuildTelnetClient()
    {
        BbsProfile? activeBbs = ResolveActiveBbs();
        TelnetClient client = new()
        {
            Cols = Emulator.Screen.Cols,
            Rows = Emulator.Screen.Rows,
            TerminalType = "ansi-bbs",
            NoResponseTimeoutSeconds = activeBbs?.NoResponseTimeoutSeconds ?? 0,
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
                if (ShowSplash) ShowSplash = false;   // real content now — dismiss the splash
                AppServices.Current.PromptScanner.Append(copy);
                AppServices.Current.Cleanup.Append(copy);
                _automator?.Feed(copy);
                Emulator.Feed(copy);
            });
        };
        client.Connected += () =>
        {
            AppServices.Current.Log.Info("Telnet", $"Connected to {Host}:{Port}");
            Dispatcher.UIThread.Post(() =>
            {
                if (ShowSplash) ShowSplash = false;   // session started — dismiss the splash
                IsConnected = true;
                // Fresh session — drop any cleanup-watcher state carried
                // over and clear any pending auto-reconnect schedule.
                // Do NOT zero _reactiveReconnectCount here: a BBS in
                // cleanup mode answers every dial with a banner + drop,
                // and resetting on the TCP-connect would burn through
                // unbounded redials instead of hitting MaxRedials.
                // Instead arm a 30s "this connection survived" timer
                // (StableConnectionWindowSeconds) that resets the
                // counter only if the connection lasts that long.
                AppServices.Current.Cleanup.Reset();
                AppServices.Current.CleanupLogout.Reset();
                CancelCleanupReconnect("connected");
                ArmStableConnectionReset();
                // Let the EventScheduler reset its "are we in-game?"
                // latch. The actual Logon fire happens on the first
                // PromptObserved, not here.
                AppServices.Current.EventScheduler.NotifyConnected();
                // Same lifecycle signal to the default-task runner — it resets its
                // per-connection latches and fires the configured startup task on
                // the first in-game prompt with a known room.
                AppServices.Current.DefaultTaskRunner.NotifyConnected();
                // Reconcile the statline to the editor on EVERY connect —
                // unconditional, unlike the auto-login-gated engines. Resets
                // the Synced latch + retry counter; the actual resend (if any)
                // is mismatch-gated and fires off the first in-game prompt.
                AppServices.Current.StatlineReconcile.Arm();
                // Arm the follower reconnect-rejoin latch on every connect. It
                // only fires (@comeback + @invite) on the first in-game room if
                // a party leader is remembered from before the drop.
                AppServices.Current.PartyRejoin.Arm();
                // Arm the leader-side reconnect party-reform latch too. Fires on
                // the first in-game room only if we snapshotted followers at the
                // preceding drop — reforms the party and holds the loop for them.
                AppServices.Current.PartyReform.Arm();
                // Re-enable any auto-actions the user opted into reviving on
                // reconnect (Settings → Other). Only on a reconnect — a
                // connect following a prior in-session disconnect — never on
                // the first connect of the session.
                if (_hadDisconnectThisSession)
                {
                    ReEnableAutoActionsOnReconnect();
                    // A drop can strand the cash/item deferred-collect hold on the
                    // Acquisition gate, pausing the loop until a manual `rm`. Arm the
                    // release for the first in-game prompt after this reconnect.
                    AppServices.Current.DeferredCollectResume.Arm();
                }
            });
        };
        client.Disconnected += () =>
        {
            // Don't log here; DisconnectInternalAsync already did, and a
            // server-initiated drop will fire this too.
            Dispatcher.UIThread.Post(() =>
            {
                bool wasConnected = IsConnected;
                IsConnected = false;
                // Mark the session as having dropped at least once so the
                // next Connected counts as a reconnect (arms the Settings →
                // Other "re-enable auto-actions on reconnect" flags). A
                // failed connect attempt fires Disconnected with
                // wasConnected=false and must NOT arm this.
                if (wasConnected) _hadDisconnectThisSession = true;
                // Snapshot the followers we were leading while PartyState is still
                // intact — par reconciliation after the reconnect wipes the roster,
                // so the leader-side reform must capture them now. Only on a real
                // in-game drop; a failed redial (wasConnected=false) keeps the good
                // snapshot from the preceding drop rather than overwriting it empty.
                if (wasConnected) AppServices.Current.PartyReform.NoteDisconnected();
                // Cancel any pending stable-window reset — this drop
                // happened before the 30s threshold, so the connect
                // didn't earn a counter reset.
                CancelStableConnectionReset();
                // Stop the event scheduler's timers and latch the Re-log
                // flag (only if we were in-game).
                AppServices.Current.EventScheduler.NotifyDisconnected();
                // Latch the default-task runner's reconnect / was-in-a-party
                // state so the next game entry can decide whether to hold for a
                // party reform before starting the task.
                AppServices.Current.DefaultTaskRunner.NotifyDisconnected();
                // Stop statline reconciliation until the next connect re-arms.
                AppServices.Current.StatlineReconcile.Disarm();
                // Pause Time Analysis accrual: we're no longer in-game, so
                // the offline span doesn't count. Totals freeze and resume
                // on the next in-game prompt after reconnect.
                AppServices.Current.TimeAnalysis.Suspend();
                // Suspend the party wall-clock cadences (par poll + @health) so
                // they don't fire `par` / telepaths into the BBS login menu during
                // re-entry, derailing the menu nav (report stock-20260731-004105).
                // They resume on the first in-game prompt after we're back.
                AppServices.Current.PartyPoller.NotifyDisconnected();
                AppServices.Current.PartyProbe.NotifyDisconnected();

                // Drop per-session condition state so a fresh login starts clean: any
                // non-auto-clearing condition (no AppliedEndsWith) must not survive the
                // disconnect. Buff-duration timers are handled below, AFTER the cause is
                // known — an unexpected drop freezes them (resume on reconnect) rather
                // than clearing, so a brief drop doesn't lose the recast clock.
                AppServices.Current.Conditions.ClearAll("disconnect");
                AppServices.Current.ManaRegen.Reset();

                // Categorise: if the user clicked Disconnect, the flag was
                // set in DisconnectInternalAsync. Otherwise check for a
                // pending intentional-hangup signal (raised by
                // HangupHandler / future hang-up-if-naked / -if-low-HP
                // automation right before the wire `=x` lands). Only
                // when neither user-flag nor hangup-signal is set do we
                // fall back to server-side classification (carrier vs
                // keepalive-timeout) based on wire-silence duration.
                if (_userInitiatedDisconnect)
                {
                    _userInitiatedDisconnect = false;
                    _lastDisconnectCause = DisconnectCause.UserInitiated;
                }
                else if (AppServices.Current.HangupSignal.ConsumeDisconnectIntent())
                {
                    _lastDisconnectCause = DisconnectCause.HangupInitiated;
                }
                else if (AppServices.Current.RelogSignal.ConsumeRelogIntent())
                {
                    _lastDisconnectCause = DisconnectCause.RelogInitiated;
                }
                else if (wasConnected)
                {
                    _lastDisconnectCause = ClassifyServerSideDrop();
                    // An involuntary host-side drop must auto-enter on its
                    // reconnect. Clear any stale suppress-entry intent left by
                    // an earlier deliberate hangup so it can't carry across the
                    // server drop and block re-entry (the deliberate-hangup and
                    // user-initiated paths were caught above, so they keep their
                    // "don't auto-enter" behavior).
                    if (_lastDisconnectCause is DisconnectCause.CarrierLost or DisconnectCause.NoResponse)
                        AppServices.Current.HangupSignal.AllowNextEntry();
                }

                // Buff timers: ANY disconnect freezes them (the buffs persist server-side
                // through link-death) — the first in-game prompt after reconnect resumes
                // them with the same remaining. A fresh character clears via ProfileLoaded,
                // and a resume gap longer than the longest buff clears them then; so a
                // brief manual disconnect keeps the recast clock instead of restarting it.
                AppServices.Current.CastDirector.PauseBuffTimers();

                // A remote @relog forces the dial-back unconditionally —
                // the sender explicitly asked to relog, so we bypass the
                // cleanup/reactive scheduling (which gates on per-BBS
                // toggles) entirely.
                if (_lastDisconnectCause == DisconnectCause.RelogInitiated)
                {
                    ScheduleRelogReconnect();
                }
                else
                {
                    // Predictive scheduler first (cleanup warning gives a
                    // deterministic reconnect-at). Reactive only fires if
                    // predictive didn't arm anything.
                    TryScheduleCleanupReconnect();
                    if (_cleanupReconnectCts is null) TryScheduleReactiveReconnect();
                }
            });
        };
        // TelnetClient's Log event carries IAC negotiation trace lines;
        // route them into LogService at Debug severity so the Log pane can
        // surface them when DBG is checked and the status bar gets the
        // latest via LatestLogText.
        client.Log += msg => AppServices.Current.Log.Debug("Telnet", msg);

        return client;
    }

    private enum TerminalStatusKind { Notice, Error }

    // Write a single bracketed status line into the terminal canvas itself
    // (in addition to LogService). Mirrors the classic-BBS-client cadence
    // the user expects: "[CONNECTING TO: …]" / "[DISCONNECTED FROM: …]" /
    // etc. Coloured via inline ANSI SGR so the emulator does the painting.
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

    // Bridge for the Settings window's Statline tab: pushes a single
    // string to the BBS as Latin-1 bytes, returns whether the send could
    // even be attempted (i.e. we have a live socket).
    private async Task<bool> SendTextFromSettings(string text)
    {
        TelnetClient? t = _telnet;
        if (t is null) return false;
        try
        {
            await t.SendTextAsync(text).ConfigureAwait(true);
            return true;
        }
        catch
        {
            // Caller surfaces a status banner on the Statline tab; we don't
            // want to crash the dialog because the socket died mid-send.
            return false;
        }
    }

    // Last feed's stat-box presence, tracked so the arm below is rising-edge only.
    private bool _statBoxOnScreen;

    // Per-feed screen watch feeding TrainerMenuTracker's live-screen detection
    // of the character-creation stat box (see the ScreenUpdated wiring in the
    // ctor). Arms character-mode input only on the RISING edge of the box
    // appearing — not every feed it's visible.
    //
    // Paradigm reuses the "Char. Creation" stat box for in-game `train stats`,
    // and the box lingers on screen after the user is already back at the in-game
    // prompt. A level-triggered arm re-armed off that stale box every feed, so the
    // tracker flapped held→resume→held (the in-game prompt fired InputMenuExited,
    // then the still-visible box re-armed) — which held the movement engine and
    // stalled the walker after training (it advanced one room per manual `rm`).
    // Presence is tracked unconditionally (even while armed) so the falling edge is
    // seen; the box must actually leave and reappear before it can re-arm.
    private void OnScreenUpdatedForTrainerMenu()
    {
        bool onScreen = ScreenShowsStatBoxMarker();
        bool rising = onScreen && !_statBoxOnScreen;
        _statBoxOnScreen = onScreen;

        if (!rising) return;
        if (AppServices.Current.TrainerMenu.IsInputMenuActive) return;   // already armed (e.g. via the `train stats` command)
        AppServices.Current.TrainerMenu.ObserveScreen(BuildVisibleScreenText());
    }

    // Allocation-free hot-path check: does any visible row carry the stat-box
    // marker? Almost never true outside the creation/train screen, so the full
    // screen-text build is skipped on nearly every feed.
    private bool ScreenShowsStatBoxMarker()
    {
        TerminalScreen screen = Emulator.Screen;
        Span<char> buf = stackalloc char[screen.Cols];
        for (int y = 0; y < screen.Rows; y++)
        {
            ReadOnlySpan<Cell> row = screen.Row(y);
            for (int x = 0; x < row.Length; x++) buf[x] = row[x].Char;
            if (buf[..row.Length].IndexOf(Game.TrainerMenuTracker.StatBoxMarker.AsSpan()) >= 0)
                return true;
        }
        return false;
    }

    // Flatten the live screen grid to newline-joined text for content scans.
    private string BuildVisibleScreenText()
    {
        TerminalScreen screen = Emulator.Screen;
        StringBuilder sb = new(screen.Rows * (screen.Cols + 1));
        for (int y = 0; y < screen.Rows; y++)
        {
            foreach (Cell cell in screen.Row(y)) sb.Append(cell.Char);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // Send raw key bytes from the terminal control to the server. Called
    // by the view's UserInput handler; no-op if not connected.
    public void SendUserInput(byte[] data)
    {
        // Observe outbound for the trainer-menu watcher — it gates on
        // the user's own `train stats` / `train` going out before
        // accepting the "Point Cost Chart" marker as menu confirmation.
        AppServices.Current.TrainerMenu.ObserveOutbound(data);
        // Suicide-password capture — during the AwaitingNewPassword
        // state, the next bytes the user types ARE the password. We
        // peek here (the bytes still flow to the server unchanged).
        AppServices.Current.SuicidePassword.ObserveOutbound(data);
        // Stat-screen parser — gates on outbound `stat` so chat lines
        // containing "Strength: 60" or similar can't bleed into the
        // PlayerStats snapshot.
        AppServices.Current.Stats.ObserveOutbound(data);
        // Movement observer — peeks for `look <dir>` (so the next room
        // display is dropped as a peek) and text-exit movement verbs
        // (so the step is captured for replay-from-last-Confirmed).
        AppServices.Current.OutboundMovement.ObserveOutbound(data);
        // Cast observer — a manually-typed cast-code either overrides the round (a
        // combat spell — the user's attack; the engine holds its auto attack) or arms
        // the between-round-cast resume (an in-between heal/buff so a still-alive target
        // is re-attacked at once instead of a round late).
        AppServices.Current.OutboundCast.ObserveOutbound(data);
        // Attack observer — a manually-typed physical attack verb (a/aa/bash/smash/bs/…)
        // is a user override: the engine holds its own swing until the next round.
        AppServices.Current.OutboundAttack.ObserveOutbound(data);
        // Chat router — capture engine-sent telepath "/<recipient> <message>"
        // bursts (party @-command broadcasts / nags) so the outgoing
        // conversation entry is attributed. Typed telepaths render on-screen
        // and are caught by the router's line sniff instead.
        AppServices.Current.Chat.ObserveOutbound(data);
        var t = _telnet;
        if (t is not null) _ = FireSendAsync(t, data);
    }

    // Raw wire write for engine sends that must NOT re-enter SendUserInput's
    // manual-input observers (TrainerMenu / SuicidePassword / Stats /
    // OutboundMovement / OutboundCast / Chat). Those observers exist to
    // interpret genuine keystrokes; feeding an engine-issued send back through
    // them makes it indistinguishable from something the user just typed.
    // OutboundCastObserver hit this: the combat engine's own attack-spell
    // announce was being read as a hand-typed cast, arming the between-round
    // resume signal, which re-announced the same spell on the *Combat Off*
    // that announcing it always causes — a self-sustaining recast loop capped
    // only by MaxCastsPerRoom (the reported "combat spamming hamm" bug). Still
    // reaches the live socket exactly like SendUserInput's tail; only the
    // observer fan-out is skipped.
    private void SendEngineWireRaw(byte[] data)
    {
        TelnetClient? t = _telnet;
        if (t is not null) _ = FireSendAsync(t, data);
    }

    // Fire-and-forget a send on the live socket without letting a mid-send
    // connection drop become fatal. `SendAsync` throws IOException /
    // SocketException (broken pipe) or ObjectDisposedException when the
    // socket dies between the null-check and the write; a timer-driven
    // engine send (e.g. the party poller ticking after a disconnect) that
    // discards the returned Task would surface that fault as a
    // TaskScheduler.UnobservedTaskException and crash the app. This wrapper
    // observes the Task and catches every fault internally — so the caller's
    // `_ =` discard can never carry an unobserved exception. The socket-death
    // path already logs the disconnect, so an expected drop is Debug noise;
    // anything unexpected still logs at Error but is never rethrown.
    private static async Task FireSendAsync(TelnetClient t, byte[] data)
    {
        try
        {
            await t.SendAsync(data).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            AppServices.Current.Log.Debug("Telnet", $"Send dropped (socket closed): {ex.Message}");
        }
        catch (Exception ex)
        {
            AppServices.Current.Log.Error("Telnet", $"Unexpected send failure: {ex.Message}");
        }
    }

    // Convenience: encode a text line (Latin-1 + CRLF) and send it to the
    // server. Used by the Conversation window's input field — typing in
    // the chat panel feeds the game the same way as typing in the terminal
    // does. Also scans the typed verb for heal-shaped commands so the regen
    // tracker can gate any HP / MA upticks during the artifact grace window.
    public void SendUserText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Rapid-fire multi-command: a typed line carrying the macro
        // separators (';' or '^M') fans out into several wire lines the
        // same way macros / aliases do, so the player can queue commands
        // like "sea n;sea n;n". A line with no separator sends verbatim
        // (untrimmed), preserving prior behavior. Each resulting line is
        // then alias-expanded + sent on its own.
        foreach (string line in MacroStore.SplitTypedInput(text))
            SendOneUserLine(line);
    }

    private void SendOneUserLine(string text)
    {
        // Alias check first — first-word match, case-insensitive. When
        // an enabled alias's name matches, the engine returns the
        // multi-step expansion + we send each step in place of the raw
        // text. No match → fall through to the verbatim send below.
        if (AppServices.Current.Aliases.TryExpand(text, out IReadOnlyList<string> steps))
        {
            foreach (string step in steps)
            {
                if (LooksLikeHealShapedCommand(step))
                    AppServices.Current.Regen.RecordArtifact();
                byte[] stepBytes = System.Text.Encoding.Latin1.GetBytes(step + "\r\n");
                SendUserInput(stepBytes);
            }
            return;
        }

        if (LooksLikeHealShapedCommand(text))
        {
            AppServices.Current.Regen.RecordArtifact();
        }

        byte[] bytes = System.Text.Encoding.Latin1.GetBytes(text + "\r\n");
        SendUserInput(bytes);
    }

    // Toolbar "EXP" button — send the in-game "exp" command exactly as if the
    // player typed it (alias expansion + wire send). No-ops harmlessly when
    // disconnected (SendUserInput drops it with no socket).
    [RelayCommand]
    private void SendExp() => SendOneUserLine("exp");

    // Heuristic: does line start with a verb that usually moves HP or MA
    // upward? Conservative — false positives just waste a few seconds of
    // regen samples; false negatives let a heal pollute the running
    // average, so be generous on the verb list.
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

    // Toggle session capture. The file lives at
    // Data/Logs/capture-yyyyMMdd-HHmmss.log and receives one line per
    // completed terminal row, prefixed with [HH:mm:ss] and encoded with
    // inline ANSI SGR escapes so colour is preserved when the file is
    // viewed through any ANSI-aware tool (less -R, modern terminals, web
    // log viewers).
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

    // Bound to File → Quit.
    [RelayCommand]
    private void Quit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    // ----- Placeholder shell-window plumbing -----------------------------

    // Tracks one open placeholder per panel id so re-opening a panel from
    // the menu / toolbar activates the existing window instead of stacking
    // duplicates. Cleared by each window's Closed handler.
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
            DataContext = new LogPaneViewModel(
                AppServices.Current.Log,
                Application.Current,
                AppServices.Current.LogDiagnostics),
        };
        window.Closed += (_, _) => _logPane = null;
        _logPane = window;
        window.Show(main);
    }

    private BackscrollWindow? _backscroll;

    [RelayCommand]
    private void OpenBackscroll()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        // Toggle convention — see OpenPlaceholder: pressing the command while
        // Backscroll is already open closes the window.
        if (_backscroll is { } existing)
        {
            existing.Close();
            return;
        }

        BackscrollViewModel vm = new(Emulator, AppServices.Current.Display.BackscrollWheelLines);
        BackscrollWindow window = new() { DataContext = vm };
        window.Closed += (_, _) => _backscroll = null;
        _backscroll = window;
        window.Show(main);
    }

    // Terminal context menu → "Bug report…". Freezes the current client
    // state (recent scrollback, program-log tail, all gameplay settings,
    // engine + player state) at click time, prompts for a description, then
    // writes a Markdown report to the Desktop named after the realm and the
    // click timestamp. Capture happens before the dialog so the report
    // reflects the moment of the problem, not when the user finishes typing.
    [RelayCommand]
    private async Task ReportBugAsync()
    {
        Services.AppServices svc = Services.AppServices.Current;

        BugReportBuilder.BugReportCapture capture = BugReportBuilder.Capture(svc, Emulator);

        string? description = await svc.Dialogs
            .OpenWindowAsync<BugReportDialogViewModel, string>(new BugReportDialogViewModel());
        if (string.IsNullOrWhiteSpace(description)) return;

        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string path = Path.Combine(desktop, BugReportBuilder.FileName(capture));
            await File.WriteAllTextAsync(path, BugReportBuilder.Render(capture, description));
            svc.Log.Info("BugReport", $"Wrote bug report to {path}");
        }
        catch (Exception ex)
        {
            svc.Log.Error("BugReport", $"Failed to write bug report: {ex.Message}");
        }
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
                AppServices.Current.CommandHistory,
                SendUserText,
                Application.Current,
                AppServices.Current.Resolver.Resolve<Models.Profile.TalkSettings>("Talk"),
                AppServices.Current.Profile),
        };
        window.Closed += (_, _) => _conversation = null;
        _conversation = window;
        window.Show(main);
    }

    // Singleton handle for the live PartyWindow — re-press toggles closed.
    private PartyWindow? _partyWindow;

    [RelayCommand]
    private void OpenParty()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_partyWindow is { } existing) { existing.Close(); return; }

        PartyWindow window = new()
        {
            DataContext = new PartyViewModel(
                AppServices.Current.PartyState,
                SendUserInput),
        };
        window.Closed += (_, _) => _partyWindow = null;
        _partyWindow = window;
        window.Show(main);
    }

    // Singleton handle for the live Buff Watchdog window — re-press toggles closed.
    private BuffWatchdogWindow? _buffWatchdog;

    [RelayCommand]
    private void OpenBuffWatchdog()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_buffWatchdog is { } existing) { existing.Close(); return; }

        BuffWatchdogWindow window = new()
        {
            DataContext = new BuffWatchdogViewModel(),
        };
        window.Closed += (_, _) => _buffWatchdog = null;
        _buffWatchdog = window;
        window.Show(main);
    }

    private SettingsWindow? _settings;

    // ----- Profile file management ----------------------------------------

    // Most-recent-first list of saved profile names. Drives the inline
    // File-menu recent entries (Recent0..Recent4). Rebuilt from
    // GlobalSettings on startup and after every profile save.
    public ObservableCollection<ProfileRef> RecentProfiles { get; } = new();

    // Indexed accessors so the File menu can lay out five fixed MenuItems
    // instead of a flyout submenu. Avalonia ItemsSource inside MenuItem
    // wraps each item in its own MenuItem, which loses the parent VM as
    // the DataContext (the command resolution via $parent[Window] is
    // fragile across popup ownership). Binding to the parent VM directly
    // sidesteps that entirely.
    //
    // RecentLabel format: "<profile> - <bbs>" (or just "<profile>" when
    // no BBS is pinned yet). The menu XAML prepends the slot number /
    // mnemonic "_N)  ". Lets the user disambiguate generically-named
    // profiles by the BBS they connect to.
    public string? Recent0 => RecentLabel(0);
    public string? Recent1 => RecentLabel(1);
    public string? Recent2 => RecentLabel(2);
    public string? Recent3 => RecentLabel(3);
    public string? Recent4 => RecentLabel(4);

    // ProfileNameN parallel accessors — the (bbs, char) ref for the click
    // handler. Recent0..4 are display strings only.
    public ProfileRef? ProfileName0 => RecentProfiles.Count > 0 ? RecentProfiles[0] : null;
    public ProfileRef? ProfileName1 => RecentProfiles.Count > 1 ? RecentProfiles[1] : null;
    public ProfileRef? ProfileName2 => RecentProfiles.Count > 2 ? RecentProfiles[2] : null;
    public ProfileRef? ProfileName3 => RecentProfiles.Count > 3 ? RecentProfiles[3] : null;
    public ProfileRef? ProfileName4 => RecentProfiles.Count > 4 ? RecentProfiles[4] : null;

    private string? RecentLabel(int index)
    {
        if (index < 0 || index >= RecentProfiles.Count) return null;
        ProfileRef recent = RecentProfiles[index];
        return string.IsNullOrEmpty(recent.Bbs) ? recent.Name : $"{recent.Name} - {recent.Bbs}";
    }

    // True when at least one recent profile is queued — gates the Separator.
    public bool HasRecents => RecentProfiles.Count > 0;

    // True when a named profile is loaded — gates File → Save profile.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveProfileLabel))]
    private bool _hasNamedProfile;

    public string SaveProfileLabel => HasNamedProfile
        ? $"_Save profile  ·  {AppServices.Current.Profile.CurrentProfileName}"
        : "_Save default profile";

    // True while it's safe to swap the active profile — only when the wire
    // is down. Loading a different (or blank) profile mid-session would fire
    // ProfileLoaded and reload every per-character service against the new
    // scope while still connected to the old character's game, desyncing
    // settings / party / game-data state. Gates New / Open / Open-recent;
    // Save / Save-as don't swap the active profile, so they stay available.
    private bool CanSwapProfile => IsDisconnected;

    // Return to the default profile. The outgoing profile is auto-saved first
    // (handled inside ProfileService.LoadDefaultProfile), then Current is
    // replaced with the Global default profile — the user's saved defaults, or
    // installed defaults on a fresh install. From there File → Save As names a
    // copy as an actual character; File → Save persists edits back to the default.
    [RelayCommand(CanExecute = nameof(CanSwapProfile))]
    private void NewProfile()
    {
        AppServices.Current.Profile.LoadDefaultProfile();
        SyncProfileMenuState();
    }

    [RelayCommand(CanExecute = nameof(CanSwapProfile))]
    private async Task OpenProfileAsync()
    {
        ProfileService profile = AppServices.Current.Profile;
        MudPlay.ViewModels.Profile.ProfilePickerDialogViewModel vm =
            new(profile.ListAll());

        ProfileRef? picked = await AppServices.Current.Dialogs.OpenWindowAsync<
            MudPlay.ViewModels.Profile.ProfilePickerDialogViewModel, ProfileRef>(vm);
        if (picked is null) return;

        try
        {
            profile.Load(picked.Bbs, picked.Name);
            PromoteRecent(picked);
            SyncProfileMenuState();
        }
        catch (Exception ex)
        {
            AppServices.Current.Log.Error("Profile",
                $"Failed to load '{picked.Name}' on '{picked.Bbs}': {ex.Message}");
        }
    }

    // File → Save. Persists the loaded profile in place: a named character to its
    // own file, the default profile to the Global default-profile file. Naming a
    // brand-new character is the separate File → Save As command.
    [RelayCommand]
    private void SaveProfile()
    {
        ProfileService profile = AppServices.Current.Profile;
        if (profile.Current is null)
        {
            AppServices.Current.Log.Warn("Profile", "Nothing to save — no profile loaded.");
            return;
        }
        profile.Save();
        AppServices.Current.Log.Info("Profile", profile.CurrentProfileName is { } name
            ? $"Saved profile '{name}'."
            : "Saved the default profile.");
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

        // Profiles are BBS-scoped. Prefer the explicitly-pinned BBS, but fall
        // back to the active BBS shown in the title bar (ResolveActiveBbs) so a
        // fresh {default} draft can be named against the BBS the user is looking
        // at — hitting Save on an unnamed draft should reach the name prompt,
        // not silently no-op. Only a truly BBS-less install has nowhere to save.
        string? bbs = profile.CurrentBbsName ?? ResolveActiveBbs()?.Name;
        if (string.IsNullOrWhiteSpace(bbs))
        {
            ShowInfoDialog("Save profile",
                "Add a BBS first (Settings → BBS). Profiles are saved under a BBS.");
            return;
        }

        MudPlay.ViewModels.Profile.ProfileNameInputDialogViewModel vm = new(
            suggestedName: profile.CurrentProfileName ?? "character",
            exists:        name => profile.Exists(bbs, name));

        string? name = await AppServices.Current.Dialogs.OpenWindowAsync<
            MudPlay.ViewModels.Profile.ProfileNameInputDialogViewModel, string>(vm);
        if (string.IsNullOrWhiteSpace(name)) return;

        profile.SaveAs(bbs, name);
        // SaveAs changes the loaded profile's identity (CurrentProfileName) but
        // fires no ProfileLoaded/Mutated event, so the title bar + OS process
        // title would otherwise keep showing the pre-save name ({default} for a
        // freshly-named draft). Refresh the chrome explicitly.
        RefreshBbsBindings();
        PromoteRecent(new ProfileRef(bbs, name));
        SyncProfileMenuState();
        AppServices.Current.Log.Info("Profile", $"Saved profile '{name}' on '{bbs}'.");
    }

    [RelayCommand(CanExecute = nameof(CanSwapProfile))]
    private void OpenRecentProfile(ProfileRef? recent)
    {
        if (recent is null) return;
        ProfileService profile = AppServices.Current.Profile;
        if (!profile.Exists(recent.Bbs, recent.Name))
        {
            AppServices.Current.Log.Warn("Profile",
                $"Recent profile '{recent.Name}' on '{recent.Bbs}' no longer exists.");
            RecentProfiles.Remove(recent);
            return;
        }
        // Defer the load off the menu-click call stack. Loading a profile
        // repositions/resizes the main window (WindowLayoutStore restores the
        // profile's saved bounds on ProfileLoaded); running that synchronously
        // here moves the window while the File menu's popup is still open, so the
        // flyout is left stranded at the window's old position until the click
        // returns. Posting lets the menu close first, then the reposition lands.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                profile.Load(recent.Bbs, recent.Name);
                PromoteRecent(recent);
                SyncProfileMenuState();
            }
            catch (Exception ex)
            {
                AppServices.Current.Log.Error("Profile",
                    $"Failed to load '{recent.Name}' on '{recent.Bbs}': {ex.Message}");
            }
        });
    }

    private void PromoteRecent(ProfileRef profileRef)
    {
        SettingsService settingsSvc = AppServices.Current.Settings;
        GlobalSettings settings = settingsSvc.Current;
        settings.RecentProfiles ??= new();
        settings.RecentProfiles.RemoveAll(r => r == profileRef);
        settings.RecentProfiles.Insert(0, profileRef);
        while (settings.RecentProfiles.Count > GlobalSettings.RecentProfilesLimit)
            settings.RecentProfiles.RemoveAt(settings.RecentProfiles.Count - 1);
        settings.LastUsedProfile = profileRef;
        settingsSvc.Save();
        RebuildRecentProfiles();
    }

    private void RebuildRecentProfiles()
    {
        RecentProfiles.Clear();
        IList<ProfileRef>? source = AppServices.Current.Settings.Current.RecentProfiles;
        if (source is null) return;
        foreach (ProfileRef recent in source) RecentProfiles.Add(recent);
    }

    private void SyncProfileMenuState()
    {
        HasNamedProfile = !AppServices.Current.Profile.IsBlankDraft && AppServices.Current.Profile.Current is not null;
        // Save-profile-as renames the *current* profile without flipping
        // HasNamedProfile, so the NotifyPropertyChangedFor on that bool
        // won't fire — nudge the label directly so its embedded profile
        // name refreshes on every state sync, rename included.
        OnPropertyChanged(nameof(SaveProfileLabel));
    }

    [RelayCommand]
    private void OpenSettings() => OpenSettingsAt(null);

    [RelayCommand]
    private void OpenBbsSettings() => OpenSettingsAt("bbs");

    // View → Events menu entry. Opens the Settings window deep-linked to
    // the Events tab (matches the MenuCommandIds.SettingsOpenEvents
    // reserved id).
    [RelayCommand]
    private void OpenEvents() => OpenSettingsAt("events");

    // Singleton handle to the Player Workshop window for the toggle
    // convention (re-press closes; deep-link to a section activates).
    private Views.CharacterWorkshop.CharacterWorkshopWindow? _workshop;

    [RelayCommand]
    private void OpenWorkshopDeath() => OpenWorkshopAt("death");

    private void OpenWorkshopAt(string? sectionId)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_workshop is { } existing)
        {
            // Deep-link re-press: switch section + raise, don't toggle closed.
            // The Workshop shell holds no window-level pending state (each
            // editable section owns its own Save / Apply / Cancel), so a plain
            // re-press just closes — no ApplyAndClose save path at the shell.
            if (existing.DataContext is ViewModels.CharacterWorkshop.CharacterWorkshopViewModel vm
                && sectionId is not null)
            {
                ViewModels.CharacterWorkshop.WorkshopSectionViewModel? section = vm.Sections
                    .FirstOrDefault(s => string.Equals(s.Id, sectionId, StringComparison.OrdinalIgnoreCase));
                if (section is not null) vm.SelectedSection = section;
                existing.Activate();
                return;
            }
            existing.Close();
            return;
        }

        AppServices svc = AppServices.Current;
        // Pull the loaded profile's stored stats into PlayerStats before the
        // Workshop reads them, so every tab opens populated from the profile.
        // Only when the live stats are blank (no `stat` parsed this session, or
        // the profile was loaded before its first capture) — a live snapshot
        // stays authoritative and isn't clobbered.
        if (string.IsNullOrEmpty(svc.PlayerStats.Name)
            && svc.Profile.Current?.LastKnownStats is { } storedStats)
        {
            svc.Stats.Hydrate(storedStats);
        }
        var workshopVm = new ViewModels.CharacterWorkshop.CharacterWorkshopViewModel(
            svc.DeathRecovery, svc.Profile, svc.PlayerStats, svc.GameData, svc.Inventory, svc.Players,
            svc.Alignment, svc.TrainerWalk, svc.Quests, svc.Equipment, svc.Leaderboards, sectionId);
        Views.CharacterWorkshop.CharacterWorkshopWindow window = new() { DataContext = workshopVm };
        // The Workshop VM + its sections are rebuilt on every open, so dispose
        // them on close to detach their long-lived service-event subscriptions.
        window.Closed += (_, _) =>
        {
            workshopVm.Dispose();
            _workshop = null;
        };
        _workshop = window;
        window.Show(main);
    }

    // Singleton-ish handle to the Quick Connect window so re-press of the
    // menu / hotkey toggles it closed.
    private QuickConnectWindow? _quickConnect;

    // File → Quick Connect. Modeless dialog; on commit the host/port becomes the connect target.
    [RelayCommand]
    private async Task OpenQuickConnectAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_quickConnect is { } existing) { existing.Close(); return; }

        QuickConnectViewModel vm = new();
        QuickConnectWindow window = new() { DataContext = vm };

        vm.ConnectRequested += async () =>
        {
            string host = vm.HostText.Trim();
            int port = vm.Port;
            window.Close();
            if (string.IsNullOrWhiteSpace(host) || port is <= 0 or > 65535) return;

            // If we're already on a connection, drop it first so the new
            // target can dial cleanly.
            if (IsConnected) await DisconnectInternalAsync();
            else if (IsConnecting) _connectCts?.Cancel();

            _quickConnectTarget = (host, port);
            RefreshBbsBindings();
            CancelCleanupReconnect("user opened Quick Connect");
            await ConnectWithRetriesAsync();
        };
        vm.Cancelled += () => window.Close();

        window.Closed += (_, _) => _quickConnect = null;
        _quickConnect = window;
        window.Show(main);
        await Task.CompletedTask;
    }

    private void OpenSettingsAt(string? sectionId)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        // Toggle convention with edit-window save-on-toggle policy:
        // re-press of the same hotkey / menu while the window is open
        // routes through ApplyAndClose (Save path). Title-bar X / Cancel
        // button discards. For a deep-link (BBS list etc.) on a window
        // that's already open, jump to the requested section instead of
        // saving + closing.
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
            DataContext = new SettingsWindowViewModel(
                svc.Profile, svc.Log,
                sendText: SendTextFromSettings,
                initialSectionId: sectionId),
        };
        window.Closed += (_, _) =>
        {
            if (window.DataContext is SettingsWindowViewModel vm) vm.Dispose();
            _settings = null;
        };
        _settings = window;
        window.Show(main);
    }

    // Singleton-ish handle to the Game Data Browser. Re-press of the
    // command / hotkey toggles it closed.
    private MudPlay.Views.GameData.GameDataBrowserWindow? _gameDataBrowser;

    [RelayCommand]
    private void OpenGameDataBrowser() => ShowGameDataBrowser(initialSectionId: null);

    [RelayCommand] private void OpenGameDataPlayers()  => ShowGameDataBrowser("players");
    [RelayCommand] private void OpenGameDataMacros()   => ShowGameDataBrowser("macros");
    [RelayCommand] private void OpenGameDataTriggers() => ShowGameDataBrowser("triggers");
    [RelayCommand] private void OpenGameDataAliases()  => ShowGameDataBrowser("aliases");

    // Registered on AppServices so the Item Finder's row double-click can jump
    // straight to an item's Game Data record. Opens (or re-focuses) the browser
    // at the Items section and selects the row whose "Number" matches.
    private void OpenItemGameData(int itemNumber)
    {
        string numStr = itemNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ShowGameDataBrowser("items",
            row => string.Equals(row.Get("Number"), numStr, StringComparison.Ordinal));
    }

    // Registered on AppServices so the room-detail popup's clickable monster
    // names jump straight to a monster's Game Data record. Opens (or
    // re-focuses) the browser at the Monsters section and selects the row whose
    // "Number" matches.
    private void OpenMonsterGameData(int monsterNumber)
    {
        string numStr = monsterNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ShowGameDataBrowser("monsters",
            row => string.Equals(row.Get("Number"), numStr, StringComparison.Ordinal));
    }

    // Registered on AppServices so an item's clickable bought/sold shop line
    // jumps straight to the host room's Game Data record. Opens (or re-focuses)
    // the browser at the Rooms section and selects the row whose Map Number +
    // Room Number match.
    private void OpenRoomGameData(int map, int room)
    {
        string mapStr  = map.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string roomStr = room.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ShowGameDataBrowser("rooms",
            row => string.Equals(row.Get("Map Number"),  mapStr,  StringComparison.Ordinal)
                && string.Equals(row.Get("Room Number"), roomStr, StringComparison.Ordinal));
    }

    // Game Data menu → "Modify Blacklist…". Staged editor over the
    // per-BBS room blacklist. Save commits + redraws the map; Cancel
    // discards.
    [RelayCommand]
    private async Task OpenBlacklistEditorAsync()
    {
        var svc = AppServices.Current;
        ViewModels.BlacklistEditorDialogViewModel vm = new(svc.RoomBlacklist, svc.RoomGraph);
        await svc.Dialogs.OpenWindowAsync<
            ViewModels.BlacklistEditorDialogViewModel, bool>(vm);
    }

    // Game Data menu → "Manage Sets…". Immediate-action dialog: copy or
    // move a set's loop library into another set, or delete a set
    // (game-data tables + loops). A delete drops the set from the menu, so
    // rebuild the set list once the dialog closes.
    [RelayCommand]
    private async Task OpenGameDataManagerAsync()
    {
        var svc = AppServices.Current;
        ViewModels.GameDataManagerViewModel vm = new(svc.GameDataSetManager, svc.GameData);
        await svc.Dialogs.OpenWindowAsync<
            ViewModels.GameDataManagerViewModel, bool>(vm);
        RebuildGameDataSetsMenu();
    }

    // Open the Game Data Browser, optionally pre-selected to a named
    // section. Toggles per the standard window-command rule: when the
    // browser is already open re-press behavior depends on what was
    // requested —
    //   - no section requested (toolbar / Ctrl+G) → close it.
    //   - section requested AND already showing → close it.
    //   - section requested AND different from current → switch the
    //     existing window's section, activate, do not respawn.
    // Switching in place beats closing-and-respawning because it preserves
    // search state, scroll position, and any per-section VM caches the
    // user has primed.
    //
    // A record-targeted open (rowSelector supplied — the Item Finder's
    // double-click) is NOT a toggle: it always opens/re-focuses the browser at
    // the requested section and selects the matching row, never closes.
    private void ShowGameDataBrowser(
        string? initialSectionId,
        Func<MudPlay.ViewModels.GameData.Tables.GameDataRow, bool>? rowSelector = null)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_gameDataBrowser is { } existing)
        {
            MudPlay.ViewModels.GameData.GameDataBrowserViewModel? existingVm =
                existing.DataContext as MudPlay.ViewModels.GameData.GameDataBrowserViewModel;

            if (rowSelector is not null && initialSectionId is not null)
            {
                existingVm?.NavigateToRecord(initialSectionId, rowSelector);
                existing.Activate();
                return;
            }

            if (initialSectionId is null
                || (existingVm is not null
                    && string.Equals(existingVm.SelectedSection?.Id, initialSectionId, StringComparison.OrdinalIgnoreCase)))
            {
                existing.Close();
                return;
            }

            if (existingVm is not null)
            {
                MudPlay.ViewModels.GameData.GameDataSectionViewModel? target =
                    existingVm.Sections.FirstOrDefault(s => string.Equals(s.Id, initialSectionId, StringComparison.OrdinalIgnoreCase));
                if (target is not null) existingVm.SelectedSection = target;
                existing.Activate();
                return;
            }

            existing.Close();
            return;
        }

        MudPlay.ViewModels.GameData.GameDataBrowserViewModel newVm = NewGameDataBrowserVm(initialSectionId);
        MudPlay.Views.GameData.GameDataBrowserWindow window = new() { DataContext = newVm };
        window.Closed += (_, _) => _gameDataBrowser = null;
        _gameDataBrowser = window;
        window.Show(main);

        // The constructor already selected the section; select the target row
        // once its table materialises (SelectRowMatching queues on cold load).
        if (rowSelector is not null && initialSectionId is not null)
            newVm.NavigateToRecord(initialSectionId, rowSelector);
    }

    // Build a Game Data Browser VM wired to the live services, selecting the given
    // section. Shared by the toggle command and the Hit Calculator's monster jump.
    private MudPlay.ViewModels.GameData.GameDataBrowserViewModel NewGameDataBrowserVm(string? initialSectionId)
        => new(
            AppServices.Current.GameData,
            AppServices.Current.Triggers,
            AppServices.Current.Aliases,
            AppServices.Current.Players,
            AppServices.Current.Macros,
            AppServices.Current.Messages,
            AppServices.Current.FlavorPrefixes,
            AppServices.Current.MonsterOverlaySeed,
            AppServices.Current.ItemOverlaySeed,
            AppServices.Current.Resolver,
            AppServices.Current.Dialogs,
            AppServices.Current.Keybindings,
            AppServices.Current.Profile,
            AppServices.Current.RoomGraph,
            AppServices.Current.PlayerStats,
            AppServices.Current.ItemSources,
            initialSectionId);

    // Registered on AppServices: the Hit Calculator's "Show me the Monsters" opens
    // (or re-focuses) the browser at the Monsters section with an "Acc ≥ minAcc"
    // filter. Unlike the toggle command, this never closes an open browser — it
    // always ends with the Monsters tab shown and filtered.
    private void OpenMonstersWithAccuracy(int minAcc)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_gameDataBrowser is { } existing)
        {
            (existing.DataContext as MudPlay.ViewModels.GameData.GameDataBrowserViewModel)
                ?.NavigateToMonstersAccuracy(minAcc);
            existing.Activate();
            return;
        }

        MudPlay.ViewModels.GameData.GameDataBrowserViewModel newVm = NewGameDataBrowserVm("monsters");
        MudPlay.Views.GameData.GameDataBrowserWindow window = new() { DataContext = newVm };
        window.Closed += (_, _) => _gameDataBrowser = null;
        _gameDataBrowser = window;
        window.Show(main);
        newVm.NavigateToMonstersAccuracy(minAcc);
    }

    // Items bound to File → Game Data → Active set. Each entry has a
    // checkbox-style header (checked = currently active set) and a command
    // that flips GameDataCache.ActiveSet + writes the resolved BBS's
    // BbsProfile.ActiveGameDataSet field (falling back to
    // GlobalSettings.DefaultGameDataSet when no BBS is pinned).
    public ObservableCollection<GameDataSetMenuItem> GameDataSets { get; } = new();

    private void RebuildGameDataSetsMenu()
    {
        GameDataSets.Clear();
        string? active = AppServices.Current.GameData.ActiveSet;
        foreach (string set in AppServices.Current.GameData.AvailableSets)
        {
            GameDataSets.Add(new GameDataSetMenuItem(
                name: set,
                isActive: string.Equals(set, active, StringComparison.OrdinalIgnoreCase),
                switchCommand: new RelayCommand(() => SwitchActiveGameDataSet(set))));
        }
    }

    // The terminal right-click Favorites flyout — always MaxStarred numbered
    // slots when any favourite is starred: filled slots first, then "(empty)"
    // placeholders so the fixed 10-slot layout is always visible.
    public ObservableCollection<FavoriteMenuItem> Favorites { get; } = new();

    private bool _hasFavorites;

    // Drives the flyout's visibility — the whole submenu hides when nothing is
    // starred (rather than showing ten empty rows to someone not using it).
    public bool HasFavorites => _hasFavorites;

    // Per-kind accent brushes for the Favorites flyout names — matched to the
    // CURRENT NAV per-engine colours (goto/walk-to = cyan-blue, loop = green,
    // auto-lair = amber) so the menu reads the same as the map + rail.
    private static readonly IBrush GotoFavBrush = new SolidColorBrush(Color.Parse("#5FB3D9"));
    private static readonly IBrush LoopFavBrush = new SolidColorBrush(Color.Parse("#7AB870"));
    private static readonly IBrush LairFavBrush = new SolidColorBrush(Color.Parse("#D4A24C"));

    // Rebuild the flyout: starred GOTO rooms (walk on click, blue), then
    // favourited loops (start the loop, green), then favourited auto-lair setups
    // (load + start, amber). Numbered 1..N sequentially; only the name is
    // accented — the "N)" prefix stays the default menu colour.
    private void RebuildFavoritesMenu()
    {
        var s = AppServices.Current;
        Favorites.Clear();
        int number = 0;

        // Rooms — resolve label (custom, else graph name), sorted by label.
        var roomRows = new List<(string label, Game.Map.RoomKey key)>();
        foreach (FavoriteRoom f in s.Favorites.StarredFavorites())
        {
            Game.Map.RoomKey key = new(f.Map, f.Room);
            string label = !string.IsNullOrWhiteSpace(f.Label)
                ? f.Label!
                : s.RoomGraph.GetRoom(key) is { } r ? r.Name : key.ToString();
            roomRows.Add((label, key));
        }
        roomRows.Sort((a, b) => string.Compare(a.label, b.label, StringComparison.OrdinalIgnoreCase));
        foreach ((string label, Game.Map.RoomKey key) in roomRows)
        {
            Game.Map.RoomKey target = key;
            Favorites.Add(new FavoriteMenuItem($"{++number})", label, GotoFavBrush,
                new AsyncRelayCommand(() => WalkToFavoriteRoomAsync(target))));
        }

        // Loops — favourited only, sorted by name; click starts the loop.
        foreach (Game.Map.Loop loop in s.Loops.Loops
                     .Where(l => l.Favorite)
                     .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            Game.Map.Loop target = loop;
            Favorites.Add(new FavoriteMenuItem($"{++number})", loop.Name, LoopFavBrush,
                new RelayCommand(() => StartLoopFavorite(target))));
        }

        // Auto-lair setups — favourited only, sorted by name; click loads + starts.
        foreach (Models.Profile.LairSetup setup in s.Lairs.Setups
                     .Where(x => x.Favorite)
                     .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            Models.Profile.LairSetup target = setup;
            Favorites.Add(new FavoriteMenuItem($"{++number})", setup.Name, LairFavBrush,
                new RelayCommand(() => StartLairFavorite(target))));
        }

        _hasFavorites = Favorites.Count > 0;
        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(HasWalkFlyouts));
    }

    // The last GOTO destinations (newest first, up to 10 — GotoHistoryStore caps
    // it), each walking there on click through the same route picker the Favorites
    // flyout uses. Kept in most-recent order (NOT sorted) so the top row is where
    // you just were.
    public ObservableCollection<FavoriteMenuItem> RecentDestinations { get; } = new();

    private bool _hasRecentDestinations;
    public bool HasRecentDestinations => _hasRecentDestinations;

    // Drives the shared separator below both walk-to flyouts — shown when either
    // Favorites or Recent destinations has rows.
    public bool HasWalkFlyouts => _hasFavorites || _hasRecentDestinations;

    private void RebuildRecentDestinationsMenu()
    {
        var s = AppServices.Current;
        RecentDestinations.Clear();
        int number = 0;
        foreach (Game.Map.RoomKey key in s.GotoHistory.All)
        {
            Game.Map.RoomKey target = key;
            string name = s.RoomGraph.GetRoom(key) is { } r ? r.Name : key.ToString();
            RecentDestinations.Add(new FavoriteMenuItem($"{++number})",
                $"{name} ({key.Map}/{key.Room})", GotoFavBrush,
                new AsyncRelayCommand(() => WalkToFavoriteRoomAsync(target))));
        }
        _hasRecentDestinations = RecentDestinations.Count > 0;
        OnPropertyChanged(nameof(HasRecentDestinations));
        OnPropertyChanged(nameof(HasWalkFlyouts));
    }

    // Start a favourited loop from the flyout — stop any conflicting engine
    // first, then hand the loop to the runner (which approaches the start
    // waypoint and begins the cycle).
    private void StartLoopFavorite(Game.Map.Loop loop)
    {
        var s = AppServices.Current;
        if (s.AutoLair.IsActive) s.AutoLair.Stop("loop favorite started");
        s.LoopRunner.Start(loop);
    }

    // Start a favourited auto-lair setup from the flyout — mirrors the
    // Navigation manager's "Load" then Start: stop conflicting engines, replace
    // the live markers with the setup's, and begin auto-lairing.
    private void StartLairFavorite(Models.Profile.LairSetup setup)
    {
        var s = AppServices.Current;
        if (s.LoopRunner.State != Game.Map.LoopState.Idle) s.LoopRunner.Stop("auto-lair favorite started");
        if (s.AutoLair.IsActive) s.AutoLair.Stop("auto-lair favorite started");
        s.AutoLair.Clear();
        foreach (Models.Profile.LairMarker m in setup.Markers)
            s.AutoLair.Mark(new Game.Map.RoomKey(m.Map, m.Room), m.OverrideRespawnSeconds);
        s.AutoLair.Start();
    }

    // Walk to a starred favourite from the right-click flyout — stop any running
    // movement engine first, then hand off to the shared route picker (the same
    // entry point the Navigation manager's Walk buttons use).
    private async Task WalkToFavoriteRoomAsync(Game.Map.RoomKey key)
    {
        MovementStop();
        await MudPlay.ViewModels.Navigation.RouteChoicePrompt.WalkAsync(AppServices.Current, key);
    }

    // Flip the active set and persist the user's choice. Active set is a
    // BBS-scoped setting (every character on the same realm shares the same
    // MajorMUD MDB); we write to the resolved BBS profile when one is
    // pinned, else fall through to global settings so the menu still works
    // before any BBS is configured.
    private void SwitchActiveGameDataSet(string setName)
    {
        // Re-selecting / re-importing the already-active set: SwitchSet no-ops on
        // an unchanged name, which after a re-import over the same set would leave
        // the stale tables cached until the user swapped away and back. Force a
        // re-ingest (fresh tables + ActiveSetChanged) in that case.
        Services.GameDataCache cache = AppServices.Current.GameData;
        if (string.Equals(cache.ActiveSet, setName, System.StringComparison.OrdinalIgnoreCase))
            cache.ReloadActiveSet();
        else
            cache.SwitchSet(setName);

        BbsProfile? bbs = ResolveActiveBbs();
        if (bbs is not null)
        {
            bbs.ActiveGameDataSet = setName;
            AppServices.Current.Bbs.Save(bbs);
        }
        else
        {
            AppServices.Current.Settings.Current.DefaultGameDataSet = setName;
            AppServices.Current.Settings.Save();
        }

        RebuildGameDataSetsMenu();
    }

    // File → Game Data → Import .mdb… — picks an Access database, runs the
    // importer, switches to the new set on success.
    [RelayCommand]
    private async Task ImportMdbAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        IReadOnlyList<IStorageFile> files = await main.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Pick a MajorMUD MDB file to import",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Access database (.mdb / .accdb)") { Patterns = new[] { "*.mdb", "*.accdb" } },
            },
        });
        if (files.Count == 0) return;

        string path = files[0].Path.LocalPath;
        MdbImporter importer = new();
        // Per-table errors go to the Program Log only — the terminal
        // gets a single summary line after the import finishes, with
        // counts sourced from MdbImportResult.TablesSkipped (so we
        // don't keep a separate UI counter in sync with the worker).
        importer.OnStatusChanged += s => AppServices.Current.Log.Info("MDB", s);
        importer.OnError         += s => AppServices.Current.Log.Error("MDB", s);

        WriteTerminalStatus("[MDB IMPORT STARTED]", TerminalStatusKind.Notice);
        MdbImportResult result = await importer.ImportAsync(path);
        AppServices.Current.Log.Info("MDB", result.Message);

        if (result.Success)
        {
            WriteTerminalStatus(BuildMdbCompleteStatus(result), TerminalStatusKindFor(result));
            // Seed base navigation loops + GOTO favourites for the realm before we
            // switch to the set, so the ensuing set-switch loads the seeded files.
            // Once-only per set (marker), additive, best-effort.
            NavSeedBootstrapper.SeedIfNeeded(result.FolderName, AppServices.Current.Log);
            SwitchActiveGameDataSet(result.FolderName);
        }
        else
        {
            WriteTerminalStatus("[MDB IMPORT FAILED — see Program Log]", TerminalStatusKind.Error);
        }
    }

    // Compose the terminal-status line for a successful MDB import.
    // Carries entry + table totals plus a format-tag derived from the
    // MajorMUD MDB shape: 9 user tables = old realm format, 10 = new
    // format. Anything else (or any per-table skips) flips the line red so
    // the user notices the structural drift.
    private static string BuildMdbCompleteStatus(MdbImportResult r)
    {
        string entries = $"{r.RowsImported:N0} entries";

        string tablesPart = r.TablesSkipped == 0
            ? $"{r.TablesImported} tables"
            : $"{r.TablesImported}/{r.TablesFound} tables ({r.TablesSkipped} skipped)";

        string formatTag = r.TablesFound switch
        {
            9  => " (old format)",
            10 => " (new format)",
            _  => " — UNEXPECTED TABLE COUNT",   // < 9 or > 10
        };

        // The "see Program Log" hint fires whenever the user has reason
        // to dig in — skipped tables OR a wrong-shape MDB.
        bool needsLogPointer = r.TablesSkipped > 0 || r.TablesFound < 9 || r.TablesFound > 10;
        string logHint = needsLogPointer ? " — see Program Log" : string.Empty;

        return $"[MDB IMPORT COMPLETE: {r.FolderName} — {tablesPart}{formatTag}, {entries}{logHint}]";
    }

    private static TerminalStatusKind TerminalStatusKindFor(MdbImportResult r)
        => (r.TablesSkipped > 0 || r.TablesFound < 9 || r.TablesFound > 10)
           ? TerminalStatusKind.Error
           : TerminalStatusKind.Notice;

    // File → Game Data → Import loops (MegaMUD .mp)… — runs the exact same
    // .mp loop import as the Navigation Manage window's "Import .mp"
    // button. We spin up a transient manager view-model purely to reuse
    // its ImportMp command (file picker → parse → anchor-resolve →
    // LoopEditor), then dispose it via its Close command so the
    // LoopsChanged / SetupsChanged subscriptions it wires in its
    // constructor don't leak onto the long-lived managers.
    [RelayCommand]
    private async Task ImportMegaMudLoopsAsync()
    {
        var s = AppServices.Current;
        ViewModels.Navigation.NavigationManagerDialogViewModel vm = new(
            s.Loops,
            s.Lairs,
            s.LairTimers,
            s.RoomGraph,
            s.Confirm,
            s.Dialogs,
            runner: s.LoopRunner,
            mpImporter: s.MpImporter,
            log: s.Log);
        try
        {
            await vm.ImportMpCommand.ExecuteAsync(null);
        }
        finally
        {
            // Drops the constructor's event subscriptions (no UI consumer
            // is attached to CloseRequested on this transient instance).
            vm.CloseCommand.Execute(null);
        }
    }

    // Singleton handle for the live NavigationWindow — re-press toggles closed.
    private Views.Navigation.NavigationWindow? _navigationWindow;

    [RelayCommand]
    private void OpenNavigation()
    {
        if (_navigationWindow is { } existing) { existing.Close(); return; }
        EnsureNavigationWindow();
    }

    // Opens the Navigation window if it isn't already up and returns its VM.
    // Unlike OpenNavigation this never toggles closed — callers that want to
    // act on the live map (e.g. the room-detail popup's "centre here") must not
    // dismiss an already-open window.
    private ViewModels.Navigation.NavigationViewModel? EnsureNavigationWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return null;

        if (_navigationWindow is { } existing)
        {
            existing.Activate();
            return existing.DataContext as ViewModels.Navigation.NavigationViewModel;
        }

        ViewModels.Navigation.NavigationViewModel vm = new(AppServices.Current);
        Views.Navigation.NavigationWindow window = new() { DataContext = vm };
        window.Closed += (_, _) =>
        {
            if (window.DataContext is IDisposable d) d.Dispose();
            _navigationWindow = null;
        };
        // Keep the map coupled to the main window through a taskbar restore: on
        // some Linux WMs the taskbar surfaces this owned child (the app-group's
        // last-active window) while the minimized owner stays iconified, so the
        // user sees the map but not the main window. De-minimize the owner when
        // the map is activated while it's minimized. No-op during normal use.
        window.Activated += (_, _) =>
        {
            if (main.WindowState == Avalonia.Controls.WindowState.Minimized)
                main.WindowState = Avalonia.Controls.WindowState.Normal;
        };
        _navigationWindow = window;
        window.Show(main);
        return vm;
    }

    // Registered on AppServices — the room-detail popup's clickable room title
    // and exit destinations route here to open/focus the map and re-root it on
    // the chosen room.
    private void FocusNavigationOnRoom(Game.Map.RoomKey key)
        => EnsureNavigationWindow()?.OnFloorChangeRequested(key);

    // Registered on AppServices — the item record's "Queue Walking here" shop
    // links route here to open/focus the map and ARM the walk (QueuedDestination),
    // exactly as picking a nav search result does; the user then clicks Run.
    private void QueueWalkToRoom(Game.Map.RoomKey key)
        => EnsureNavigationWindow()?.QueueDestination(key);

    // Room-detail exit clicks re-root the popup on the neighbour and let an
    // already-open map follow — but must not summon the map if it's closed,
    // so this centres only when the window is up (no EnsureNavigationWindow).
    private void CenterNavigationOnRoomIfOpen(Game.Map.RoomKey key)
    {
        if (_navigationWindow?.DataContext is ViewModels.Navigation.NavigationViewModel vm)
            vm.OnFloorChangeRequested(key);
    }

    // Toolbar Start, which doubles as Resume. USER-paused → resume. Idle with a
    // loop staged (Manage dialog's Load) → run it straight away. Otherwise — idle
    // with nothing staged, OR already running a loop/goto — open the shared Manage
    // dialog on the Go To tab, so the user can pick or SWITCH destination by
    // hitting Run on a different favourite / loop mid-run.
    [RelayCommand]
    private void MovementStart()
    {
        var s = AppServices.Current;
        Game.Map.MovementController ctl = s.MovementControl;
        if (ctl.IsUserPaused)
        {
            ctl.Resume();
            return;
        }
        if (ctl.IsIdle && s.LoopRunner.StagedLoop is { } staged)
        {
            s.LoopRunner.Start(staged);
            return;
        }
        OpenNavManager(startOnGotoTab: true);
    }

    // Toolbar Pause — pauses the running engine (no-op if already paused / idle).
    [RelayCommand]
    private void MovementPause() => AppServices.Current.MovementControl.Pause();

    // Toolbar Stop — backs the running engine fully out to Idle.
    [RelayCommand]
    private void MovementStop() => AppServices.Current.MovementControl.Stop();

    // Singleton handle for the one Navigation Management dialog — re-press from
    // either entry point (toolbar Start fallback, map window button) focuses it
    // instead of stacking a second identical window.
    private Views.Navigation.NavigationManagerDialog? _manageWindow;

    // The single owner of the Navigation Management dialog. Browses / loads /
    // runs saved loops + lairs and hosts the Go To favourites tab — no map window
    // required. When the map is up and mid loop-build, its live LoopBuilder draft
    // is handed to the dialog so the Draft "save this loop" section shows. The
    // bool picks the default tab (toolbar Start → Go To, map button → Loops); an
    // already-open dialog is switched to that tab and re-focused.
    private void OpenNavManager(bool startOnGotoTab)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;
        if (_manageWindow is { } existing)
        {
            if (existing.DataContext is ViewModels.Navigation.NavigationManagerDialogViewModel evm)
                evm.SelectTab(startOnGotoTab);
            existing.Activate();
            return;
        }

        var s = AppServices.Current;
        var mapVm = _navigationWindow?.DataContext as ViewModels.Navigation.NavigationViewModel;
        var draft = mapVm?.LoopBuilder;   // non-null only while the map is in loop-build

        ViewModels.Navigation.NavigationManagerDialogViewModel vm = new(
            s.Loops,
            s.Lairs,
            s.LairTimers,
            s.RoomGraph,
            s.Confirm,
            s.Dialogs,
            folders: s.NavFolders,
            draft: draft,
            onDraftConsumed: draft is null ? null : () => mapVm!.ConsumeLoopBuildDraft(),
            runner: s.LoopRunner,
            mpImporter: s.MpImporter,
            log: s.Log,
            search: s.RoomSearch,
            walker: s.Walker,
            movement: s.MovementControl,
            autoLair: s.AutoLair,
            favorites: s.Favorites,
            startOnGotoTab: startOnGotoTab);

        Views.Navigation.NavigationManagerDialog window = new() { DataContext = vm };
        // The dialog's Close button raises CloseRequested; mirror what
        // DialogService does — close the window on it, and on any close run the
        // VM's own event-unsubscribe path (its [RelayCommand] Close) so the store
        // subscriptions it wired in its constructor don't leak on a title-bar X.
        void OnCloseRequested(bool _) => window.Close();
        vm.CloseRequested += OnCloseRequested;
        window.Closed += (_, _) =>
        {
            vm.CloseRequested -= OnCloseRequested;
            vm.CloseCommand.Execute(null);
            _manageWindow = null;
        };
        // Same taskbar-restore coupling DialogService applies to owned children.
        window.Activated += (_, _) =>
        {
            if (main.WindowState == Avalonia.Controls.WindowState.Minimized)
                main.WindowState = Avalonia.Controls.WindowState.Normal;
        };
        _manageWindow = window;
        window.Show(main);
    }

    // Singleton handle for the live SpellBookWindow — re-press toggles closed.
    private SpellBookWindow? _spellBook;

    [RelayCommand]
    private void OpenSpellBook()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_spellBook is { } existing) { existing.Close(); return; }

        SpellBookWindow window = new()
        {
            DataContext = new SpellBookViewModel(
                AppServices.Current.Spellbook,
                () => AppServices.Current.Profile.Current?.LastKnownStats?.Class),
        };
        window.Closed += (_, _) => _spellBook = null;
        _spellBook = window;
        window.Show(main);
    }

    // Singleton handle for the live SessionStatsWindow — re-press toggles closed.
    private SessionStatsWindow? _sessionStats;

    [RelayCommand]
    private void OpenSessionStats()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_sessionStats is { } existing) { existing.Close(); return; }

        SessionStatsWindow window = new()
        {
            DataContext = new SessionStatsViewModel(
                AppServices.Current.CombatSession,
                AppServices.Current.TimeAnalysis,
                AppServices.Current.SessionActivity,
                AppServices.Current.HpMaHistory,
                AppServices.Current.SessionStatsLayout,
                AppServices.Current.PlayerStats,
                AppServices.Current.GameData,
                AppServices.Current.Currency,
                OpenTransactionHistory,
                OpenPlayersSeen),
        };
        window.Closed += (_, _) => _sessionStats = null;
        _sessionStats = window;
        window.Show(main);
    }

    // Singleton handle for the live TransactionHistoryWindow — re-press toggles closed.
    private TransactionHistoryWindow? _transactionHistory;

    [RelayCommand]
    private void OpenTransactionHistory()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_transactionHistory is { } existing) { existing.Close(); return; }

        TransactionHistoryWindow window = new()
        {
            DataContext = new TransactionHistoryViewModel(AppServices.Current.TransactionHistory),
        };
        window.Closed += (_, _) => _transactionHistory = null;
        _transactionHistory = window;
        window.Show(main);
    }

    // Singleton handle for the live PlayersSeenWindow — re-press toggles closed.
    private PlayersSeenWindow? _playersSeen;

    [RelayCommand]
    private void OpenPlayersSeen()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_playersSeen is { } existing) { existing.Close(); return; }

        PlayersSeenWindow window = new()
        {
            DataContext = new PlayersSeenViewModel(AppServices.Current.PlayerSightings),
        };
        window.Closed += (_, _) => _playersSeen = null;
        _playersSeen = window;
        window.Show(main);
    }

    [RelayCommand]
    private void OpenWorkshop() => OpenWorkshopAt(null);

    // Tools → Wire Inspector. Singleton-ish: a second open activates the
    // existing window rather than spawning a duplicate.
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
            DataContext = new WireInspectorViewModel(
                AppServices.Current.Wire, AppServices.Current.CombatClassifier,
                AppServices.Current.WireInspectorVisibility),
        };
        window.Closed += (_, _) => _wireInspector = null;
        _wireInspector = window;
        window.Show(main);
    }

    // ----- Polish commands -----------------------------------------------

    // View → Reset layout. Restores every panel to docked default.
    [RelayCommand]
    private void ResetLayout() => AppServices.Current.Panels.ResetToDefault();

    // Tools → Open Logs folder… and Help → Open Logs folder…
    [RelayCommand]
    private void OpenLogsFolder()
    {
        if (!ShellLaunch.OpenPath(AppPaths.LogsDir))
            AppServices.Current.Log.Warn("ShellLaunch", $"Could not open {AppPaths.LogsDir}");
    }

    // Tools → Clear chatlog. Wipes every entry from the app-singleton
    // ChatHistoryStore — the Conversation window's contents go with it
    // (it binds to the same store) and a fresh open shows an empty list — and
    // truncates the persisted talk.log so the on-disk copy matches. Destructive;
    // no confirm dialog.
    [RelayCommand]
    private void ClearChatlog()
    {
        AppServices.Current.ChatHistory.Clear();
        AppServices.Current.SessionLog.TruncateConversations();
        AppServices.Current.Log.Info("Chatlog", "Cleared chat history.");
    }

    // Help → one of the user-editable website links. Opens the link's URL in
    // the OS default browser; no-ops on a blank / malformed URL (the editor
    // already drops empty rows, but the guard keeps a stray invocation safe).
    [RelayCommand]
    private void OpenHelpLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!ShellLaunch.OpenUrl(url))
            AppServices.Current.Log.Warn("Help", $"Could not open website: {url}");
    }

    // Help → BBS site. Opens the active BBS's BbsProfile.WebsiteUrl in the
    // OS default browser. Silently no-ops when no URL is set — the menu
    // item's HasBbsWebsite binding keeps it disabled in that state, but we
    // guard here too in case the user triggered it some other way.
    [RelayCommand]
    private void OpenBbsWebsite()
    {
        string? url = BbsWebsiteUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!ShellLaunch.OpenUrl(url))
            AppServices.Current.Log.Warn("Help", $"Could not open BBS website: {url}");
    }

    [RelayCommand]
    private void ReportIssue() => ShellLaunch.OpenUrl(AppInfo.IssuesUrl);

    // ----- Live-bound input-gesture labels for menu items ---------------
    // Each property reads the current chord for one BuiltInAction.
    // Refreshed in bulk by RefreshKeybindLabels() when KeybindingStore
    // fires BindingChanged. The XAML menu items bind their InputGesture
    // to these — so rebinding through the context-menu editor updates
    // every menu's shortcut display immediately.

    public string ConversationGesture     => GetGesture(Models.Profile.BuiltInAction.OpenConversation);
    public string PartyGesture            => GetGesture(Models.Profile.BuiltInAction.OpenParty);
    public string BuffWatchdogGesture     => GetGesture(Models.Profile.BuiltInAction.OpenBuffWatchdog);
    public string WorkshopGesture         => GetGesture(Models.Profile.BuiltInAction.OpenWorkshop);
    public string NavigationGesture       => GetGesture(Models.Profile.BuiltInAction.OpenNavigation);
    public string SpellBookGesture        => GetGesture(Models.Profile.BuiltInAction.OpenSpellBook);
    public string LogPaneGesture          => GetGesture(Models.Profile.BuiltInAction.OpenLogPane);
    public string BackscrollGesture       => GetGesture(Models.Profile.BuiltInAction.OpenBackscroll);
    public string SessionStatsGesture     => GetGesture(Models.Profile.BuiltInAction.OpenSessionStats);
    public string SettingsGesture         => GetGesture(Models.Profile.BuiltInAction.OpenSettings);
    public string GameDataBrowserGesture  => GetGesture(Models.Profile.BuiltInAction.OpenGameDataBrowser);
    public string ToggleConnectionGesture => GetGesture(Models.Profile.BuiltInAction.ToggleConnection);
    public string NewProfileGesture       => GetGesture(Models.Profile.BuiltInAction.NewProfile);
    public string OpenProfileGesture      => GetGesture(Models.Profile.BuiltInAction.OpenProfile);
    public string SaveProfileGesture      => GetGesture(Models.Profile.BuiltInAction.SaveProfile);
    public string SaveProfileAsGesture    => GetGesture(Models.Profile.BuiltInAction.SaveProfileAs);
    public string QuitGesture             => GetGesture(Models.Profile.BuiltInAction.Quit);

    private static string GetGesture(Models.Profile.BuiltInAction action)
        => AppServices.Current.Keybindings.Get(action).Label;

    // Fire PropertyChanged for every *Gesture label so menus refresh after a rebind.
    private void RefreshKeybindLabels()
    {
        OnPropertyChanged(nameof(ConversationGesture));
        OnPropertyChanged(nameof(PartyGesture));
        OnPropertyChanged(nameof(BuffWatchdogGesture));
        OnPropertyChanged(nameof(WorkshopGesture));
        OnPropertyChanged(nameof(NavigationGesture));
        OnPropertyChanged(nameof(SpellBookGesture));
        OnPropertyChanged(nameof(LogPaneGesture));
        OnPropertyChanged(nameof(BackscrollGesture));
        OnPropertyChanged(nameof(SessionStatsGesture));
        OnPropertyChanged(nameof(SettingsGesture));
        OnPropertyChanged(nameof(GameDataBrowserGesture));
        OnPropertyChanged(nameof(ToggleConnectionGesture));
        OnPropertyChanged(nameof(NewProfileGesture));
        OnPropertyChanged(nameof(OpenProfileGesture));
        OnPropertyChanged(nameof(SaveProfileGesture));
        OnPropertyChanged(nameof(SaveProfileAsGesture));
        OnPropertyChanged(nameof(QuitGesture));
    }

    private AboutWindow? _aboutWindow;

    // Help → About MudPlay. A modeless, read-only window: program name + version,
    // a clickable repo link, a tab per bundled license (MIT / SIL OFL / CC BY-SA),
    // and a community thank-you. Toggle convention — pressing the command while
    // it's open closes it (see OpenPlaceholder).
    [RelayCommand]
    private void OpenAbout()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_aboutWindow is { } existing)
        {
            existing.Close();
            return;
        }

        AboutWindow window = new() { DataContext = new AboutWindowViewModel() };
        window.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow = window;
        window.Show(main);
    }

    private MudPlay.Views.Help.HelpWindow? _helpWindow;

    // Help → Help topics. A modeless, read-only compendium: a searchable table of
    // contents (left) that drives a rendered content pane (right), covering how
    // features work, how to use the client, and what each setting means. Same
    // toggle convention — pressing the command while it's open closes it.
    [RelayCommand]
    private void OpenHelpWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_helpWindow is { } existing)
        {
            existing.Close();
            return;
        }

        MudPlay.Views.Help.HelpWindow window = new()
        {
            DataContext = new MudPlay.ViewModels.Help.HelpWindowViewModel(),
        };
        window.Closed += (_, _) => _helpWindow = null;
        _helpWindow = window;
        window.Show(main);
    }

    // ----- Auto-engine toggle commands --------------------------------
    // ToolbarItemCatalogue routes its ToggleAutoCombat / ToggleAutoHealRest
    // entries here by command-name reflection. The Settings → General
    // checkboxes write directly to GeneralSettings.AutoMode; both paths
    // converge on the same profile field.

    // Flip the live IsAutoCombatActive bit (which the partial OnXxxChanged
    // hook persists to GeneralSettings.AutoMode.AutoCombat and refreshes
    // the toolbar IsActive badge). Bound from the toolbar ToggleAutoCombat
    // button + the menu hotkey.
    [RelayCommand]
    private void ToggleAutoCombat() => IsAutoCombatActive = !IsAutoCombatActive;

    // Flip the live IsAutoNukeActive bit.
    [RelayCommand]
    private void ToggleAutoNuke() => IsAutoNukeActive = !IsAutoNukeActive;

    // Flip the live IsAutoHealRestActive bit.
    [RelayCommand]
    private void ToggleAutoHealRest() => IsAutoHealRestActive = !IsAutoHealRestActive;

    // Flip the live IsAutoBlessActive bit.
    [RelayCommand]
    private void ToggleAutoBless() => IsAutoBlessActive = !IsAutoBlessActive;

    // Flip the live IsAutoLightActive bit.
    [RelayCommand]
    private void ToggleAutoLight() => IsAutoLightActive = !IsAutoLightActive;

    // Flip the live IsAutoGetItemsActive bit.
    [RelayCommand]
    private void ToggleAutoGetItems() => IsAutoGetItemsActive = !IsAutoGetItemsActive;

    // Flip the live IsAutoGetCashActive bit.
    [RelayCommand]
    private void ToggleAutoGetCash() => IsAutoGetCashActive = !IsAutoGetCashActive;

    // Flip the live IsAutoSneakActive bit.
    [RelayCommand]
    private void ToggleAutoSneak() => IsAutoSneakActive = !IsAutoSneakActive;

    // Flip the live IsAutoHideActive bit.
    [RelayCommand]
    private void ToggleAutoHide() => IsAutoHideActive = !IsAutoHideActive;

    // Flip the live IsAutoSearchActive bit.
    [RelayCommand]
    private void ToggleAutoSearch() => IsAutoSearchActive = !IsAutoSearchActive;

    // Flip the live IsDisableHangupsActive bit (the partial OnXxxChanged
    // hook persists it to GeneralSettings.DisableHangups and the toolbar
    // IsActive badge follows). Bound from the toolbar / Action-menu / hotkey.
    [RelayCommand]
    private void ToggleDisableHangups() => IsDisableHangupsActive = !IsDisableHangupsActive;

    // File-menu toggle for the app-level "reopen last profile on startup" setting.
    // Reads / writes GlobalSettings directly (it's global, not per-profile) so the
    // check state always reflects what startup will do; the getter needs no reseed.
    public bool AutoLoadLastProfile
    {
        get => AppServices.Current.Settings.Current.AutoLoadLastProfile;
        set
        {
            if (value == AppServices.Current.Settings.Current.AutoLoadLastProfile) return;
            AppServices.Current.Settings.Current.AutoLoadLastProfile = value;
            AppServices.Current.Settings.Save();
            OnPropertyChanged();
        }
    }

    // Master "Auto-All" kill-switch. Delegates to the shared
    // Game.AutoModeController so the toolbar button, the Action-menu item,
    // and the @auto-all remote command all drive one session snapshot. The
    // controller's profile write fires ProfileSaving, which reseeds the
    // nine toggle observables (and thereby IsAllAutoOff).
    [RelayCommand]
    private void AllAutoOff() => AppServices.Current.AutoModeController.ToggleAll();

    // "Reset States" — the manual recovery escape hatch. Drops every condition
    // active on us (ConditionTracker.ClearAll), which cascades through each
    // owner's ActiveFlags edge: the Confused / Held self-chips clear, their
    // ConfusionGate / HeldGate release, and the ailment @wait balances to @ok.
    // Then it sweeps the remaining self-row ailment chips so the party window
    // shows a clean self row. Self-only — other members' state is untouched.
    // Finally it force-clears combat state so a stuck Combat gate (stale roster
    // parking the walker "fighting" an empty room) releases and the Fighting chip
    // goes away — the conditions sweep alone never touched it.
    //
    // The case this rescues: a condition that latched on a shared applied line
    // (many confusion sources emit "You are confused!") but carries its own
    // specific wear-off text, so a generic "confusion wears off" ends the
    // siblings but strands this one active — leaving the nav paused on a
    // phantom "waiting - confused" that no in-game line will clear.
    [RelayCommand]
    private void ResetStates()
    {
        AppServices.Current.Conditions.ClearAll("reset");

        Game.PartyManager party = AppServices.Current.Party;
        if (party.LocalCharacterName is { Length: > 0 } me)
        {
            party.SetMemberAilment(me, Models.GameData.MessageFlags.Confused, false);
            party.SetMemberAilment(me, Models.GameData.MessageFlags.MovementPrevented, false);
            party.SetMemberAilment(me, Models.GameData.MessageFlags.Poisoned, false);
            party.SetMemberAilment(me, Models.GameData.MessageFlags.Blinded, false);
            party.SetMemberAilment(me, Models.GameData.MessageFlags.Diseased, false);
        }

        AppServices.Current.CombatTracker.ResetCombatState("Reset States (manual)");

        AppServices.Current.Log.Info(Game.Conditions.ConditionTracker.LogCategory,
            "Reset States — self conditions, ailment chips, combat state, and derived movement holds cleared (manual).");
    }

    // ----- Inventory / equipment bulk actions (Action menu + toolbar) -----
    // Local twins of the @get-all / @drop-all / @deposit-all remote commands
    // and the Default gear set. Reuse the same engine backends; the status
    // line the engine would reply on the party channel is surfaced in the
    // program log instead.

    // "Get All" — pick up every item on the room floor.
    [RelayCommand]
    private void GetAll() => AppServices.Current.Log.Info(
        Game.Inventory.InventoryManager.LogCategory,
        AppServices.Current.InventoryAction.GetAll());

    // "Drop All" — drop every carried-but-unworn item.
    [RelayCommand]
    private void DropAll() => AppServices.Current.Log.Info(
        Game.Inventory.InventoryManager.LogCategory,
        AppServices.Current.InventoryAction.DropAll());

    // "Deposit All" — bank wealth down to the keep-on-hand floor.
    [RelayCommand]
    private void DepositAll() => AppServices.Current.Log.Info(
        Game.Inventory.InventoryManager.LogCategory,
        AppServices.Current.InventoryAction.DepositAll());

    // "Equip All" — walk the character into the Default gear set. The
    // engine logs the apply and the wire shows the wears, so only the
    // no-wire outcomes (already worn / not configured / busy) get a
    // program-log note here.
    [RelayCommand]
    private void EquipAll()
    {
        Game.Inventory.EquipResult result =
            AppServices.Current.Equipment.ApplyByTrigger(Models.Profile.EquipTriggerType.Default);
        string? note = result switch
        {
            Game.Inventory.EquipResult.NoChange => "Default gear set already worn.",
            Game.Inventory.EquipResult.NotFound => "No default gear set configured.",
            Game.Inventory.EquipResult.Busy     => "Equip already in progress.",
            _ => null, // Applied — the engine logs the apply and the wire shows it.
        };
        if (note is not null)
            AppServices.Current.Log.Info(Game.Inventory.EquipmentManager.LogCategory, note);
    }

    // Re-entrancy-safe suppression depth for the auto-engine writeback: a reseed
    // (SyncAutoEngineTogglesFromProfile) can trigger a Save whose ProfileSaving /
    // ProfileMutated handlers reseed again, nesting the guard. A plain bool would
    // let the inner scope's exit un-suppress the outer one, so its remaining
    // observable assignments would log "User turned X on/off" and re-persist. A
    // counter stays suppressed until every nested scope has exited.
    private int _suppressAutoEngineWriteback;

    // Set true the first time a live connection drops in the current app
    // session. Gates ReEnableAutoActionsOnReconnect so the re-enable only
    // fires on an actual reconnect, never the first dial.
    private bool _hadDisconnectThisSession;

    // Re-enable each auto-action whose Settings → General "re-enable on
    // reconnect" flag is ticked, by flipping its persisted state back ON in
    // the active profile — AutoMode bits for the ten AutoMode engines, and the
    // separate AutoTrainerSettings.AutoTrain bit for Auto-Train. Engines read
    // their flag live (per-tick), so the persisted flip alone revives them;
    // SyncAutoEngineTogglesFromProfile then reseeds the toolbar observables so
    // the badges match. No-op when no profile is loaded or no flag is ticked.
    private void ReEnableAutoActionsOnReconnect()
    {
        Models.Profile.GeneralSettings general =
            AppServices.Current.Resolver.Resolve<Models.Profile.GeneralSettings>("General");

        bool any = general.ReEnableAutoCombatOnReconnect
                || general.ReEnableAutoNukeOnReconnect
                || general.ReEnableAutoHealRestOnReconnect
                || general.ReEnableAutoBlessOnReconnect
                || general.ReEnableAutoLightOnReconnect
                || general.ReEnableAutoGetItemsOnReconnect
                || general.ReEnableAutoGetCashOnReconnect
                || general.ReEnableAutoSneakOnReconnect
                || general.ReEnableAutoHideOnReconnect
                || general.ReEnableAutoSearchOnReconnect
                || general.ReEnableAutoTrainOnReconnect;
        if (!any) return;
        if (AppServices.Current.Profile.Current is not { } profile) return;

        profile.Settings ??= new();
        Models.Profile.GeneralSettings dto = ReadGeneralFromProfile(profile);
        Models.Profile.AutoActionDefaults am = dto.AutoMode;
        if (general.ReEnableAutoCombatOnReconnect)   am.AutoCombat   = true;
        if (general.ReEnableAutoNukeOnReconnect)     am.AutoNuke     = true;
        if (general.ReEnableAutoHealRestOnReconnect) am.AutoHealRest = true;
        if (general.ReEnableAutoBlessOnReconnect)    am.AutoBless    = true;
        if (general.ReEnableAutoLightOnReconnect)    am.AutoLight    = true;
        if (general.ReEnableAutoGetItemsOnReconnect) am.AutoGetItems = true;
        if (general.ReEnableAutoGetCashOnReconnect)  am.AutoGetCash  = true;
        if (general.ReEnableAutoSneakOnReconnect)    am.AutoSneak    = true;
        if (general.ReEnableAutoHideOnReconnect)     am.AutoHide     = true;
        if (general.ReEnableAutoSearchOnReconnect)   am.AutoSearch   = true;

        profile.Settings["General"] =
            System.Text.Json.JsonSerializer.SerializeToElement(dto);

        // Auto-train isn't an AutoMode bit — flip it in the "AutoTrainer" entry
        // via read-modify-write so the trainer tab's other fields survive.
        if (general.ReEnableAutoTrainOnReconnect)
        {
            Models.Profile.AutoTrainerSettings trainer = ReadAutoTrainerFromProfile(profile);
            if (!trainer.AutoTrain)
            {
                trainer.AutoTrain = true;
                profile.Settings["AutoTrainer"] =
                    System.Text.Json.JsonSerializer.SerializeToElement(trainer);
            }
        }

        AppServices.Current.Profile.Save();

        // Reseed the surfaced observables (toolbar badges) from the freshly
        // persisted AutoMode. The other eight auto-actions have no live UI
        // state — engines pick the flip up on their next tick.
        SyncAutoEngineTogglesFromProfile();

        AppServices.Current.Log.Info(
            "Reconnect", "Re-enabled opted-in auto-actions after reconnect");
    }

    // Connecting with no game-data set loaded should immediately show the
    // navigation hint rather than waiting for the first room transition
    // (which never confidently resolves without a graph).
    partial void OnIsConnectedChanged(bool value) => RefreshLocationSlot();

    partial void OnIsAutoCombatActiveChanged(bool value)
    {
        PersistAutoModeFlag("AutoCombat", value, d => d.AutoCombat = value);
        // A profile reseed sets this without a real user toggle — skip the
        // re-eval (the tracker gets its own observations). A genuine flip
        // re-evaluates the combat gate at once so toggling off mid-round
        // releases the walker (and clears InCombat if the room is clear)
        // instead of stalling until the next room re-display.
        if (_suppressAutoEngineWriteback > 0) return;
        AppServices.Current.CombatTracker?.OnAutoAttackChanged();
        // OnAutoAttackChanged clears only the Combat gate. Sibling room-observation
        // gate-holders (the deferred-cash / get-items / search Acquisition holds)
        // re-evaluate solely on a fresh observation, so turning combat off with one
        // of them asserted strands the walker "Paused by: Acquisition" until a
        // manual room re-display. Re-emit the current observation once the fight is
        // disengaged so every gate-holder re-evaluates together.
        if (!value) AppServices.Current.RoomClassifier?.ReemitCurrent();
    }

    partial void OnIsAutoNukeActiveChanged(bool value)
        => PersistAutoModeFlag("AutoNuke", value, d => d.AutoNuke = value);

    partial void OnIsAutoHealRestActiveChanged(bool value)
    {
        PersistAutoModeFlag("AutoHealRest", value, d => d.AutoHealRest = value);
        // Mirror the AutoCombat path: a genuine flip must re-evaluate the
        // health engine at once. Toggling off releases a held HP/MA recovery
        // gate immediately (Evaluate's disabled branch clears it) so the walker
        // stops sitting idle mid-rest; toggling on re-asserts and rests now
        // instead of waiting for the next HP-changed event. A profile reseed
        // sets this without a real user toggle — skip then.
        if (_suppressAutoEngineWriteback > 0) return;
        AppServices.Current.Health?.Evaluate();
    }

    partial void OnIsAutoBlessActiveChanged(bool value)
        => PersistAutoModeFlag("AutoBless", value, d => d.AutoBless = value);

    partial void OnIsAutoLightActiveChanged(bool value)
        => PersistAutoModeFlag("AutoLight", value, d => d.AutoLight = value);

    partial void OnIsAutoGetItemsActiveChanged(bool value)
        => PersistAutoModeFlag("AutoGetItems", value, d => d.AutoGetItems = value);

    partial void OnIsAutoGetCashActiveChanged(bool value)
        => PersistAutoModeFlag("AutoGetCash", value, d => d.AutoGetCash = value);

    partial void OnIsAutoSneakActiveChanged(bool value)
        => PersistAutoModeFlag("AutoSneak", value, d => d.AutoSneak = value);

    partial void OnIsAutoHideActiveChanged(bool value)
        => PersistAutoModeFlag("AutoHide", value, d => d.AutoHide = value);

    partial void OnIsAutoSearchActiveChanged(bool value)
        => PersistAutoModeFlag("AutoSearch", value, d => d.AutoSearch = value);

    partial void OnIsDisableHangupsActiveChanged(bool value)
        => PersistGeneralFlag("DisableHangups", value, g => g.DisableHangups = value);

    // Reset the LIVE auto-engine state (AutoMode) to the character's BASE modes —
    // the Settings → General base-modes checkboxes (GeneralSettings.AutoModeBase).
    // Called at profile load and at the first start of a loop / auto-lair circuit,
    // so a user who flipped live toolbar toggles to travel to the circuit (combat
    // off to sprint 500 rooms to a loop, say) settles into it with their normal
    // defaults restored, toolbar badges and all. The decision is AutoActionDefaults
    // .ReconcileToBase: a no-op when live already matches the base; a one-time seed
    // (base := live) for a pre-split character with no base yet, so base-apply then
    // drives every future load; otherwise live settles to the base. Engines read
    // their flag per-tick, so persisting the flip + reseeding the badges (only when
    // live actually changed) is enough — no explicit per-engine re-eval here.
    private void ReconcileAutoModeToBase(string reason)
    {
        if (AppServices.Current.Profile.Current is not { } profile) return;
        Models.Profile.GeneralSettings dto = ReadGeneralFromProfile(profile);

        Models.Profile.AutoModeReconcileResult result =
            Models.Profile.AutoActionDefaults.ReconcileToBase(dto.AutoModeBase, dto.AutoMode);
        if (!result.BaseSeeded && !result.LiveChanged) return;   // already settled — nothing to write

        dto.AutoModeBase = result.Base;
        dto.AutoMode = result.Live;
        profile.Settings ??= new();
        profile.Settings["General"] =
            System.Text.Json.JsonSerializer.SerializeToElement(dto);
        AppServices.Current.Profile.Save();

        if (result.LiveChanged)
        {
            SyncAutoEngineTogglesFromProfile();
            AppServices.Current.Log.Info("AutoMode",
                $"Auto-engines reset to base modes ({reason}).");
        }
        else
        {
            // Legacy profile just adopted its live modes as the base — live is
            // unchanged, so the badges already match; no reseed needed.
            AppServices.Current.Log.Info("AutoMode",
                $"Adopted current live modes as this character's base modes ({reason}).");
        }
    }

    private void PersistAutoModeFlag(string flag, bool value,
                                     Action<Models.Profile.AutoActionDefaults> mutator)
    {
        if (_suppressAutoEngineWriteback > 0) return;
        if (AppServices.Current.Profile.Current is not { } profile) return;
        profile.Settings ??= new();
        Models.Profile.GeneralSettings dto = ReadGeneralFromProfile(profile);
        mutator(dto.AutoMode);
        profile.Settings["General"] =
            System.Text.Json.JsonSerializer.SerializeToElement(dto);
        AppServices.Current.Profile.Save();
        AppServices.Current.Log.Info("AutoMode", $"User turned {flag} {(value ? "on" : "off")}.");
    }

    // Persist a top-level Models.Profile.GeneralSettings field (vs
    // PersistAutoModeFlag's nested AutoMode bits). Same suppress guard so a
    // profile-load reseed of the observable doesn't re-persist what it just
    // read.
    private void PersistGeneralFlag(string flag, bool value,
                                    Action<Models.Profile.GeneralSettings> mutator)
    {
        if (_suppressAutoEngineWriteback > 0) return;
        if (AppServices.Current.Profile.Current is not { } profile) return;
        profile.Settings ??= new();
        Models.Profile.GeneralSettings dto = ReadGeneralFromProfile(profile);
        mutator(dto);
        profile.Settings["General"] =
            System.Text.Json.JsonSerializer.SerializeToElement(dto);
        AppServices.Current.Profile.Save();
        AppServices.Current.Log.Info("General", $"User turned {flag} {(value ? "on" : "off")}.");
    }

    // On profile load (or close), reseed the toggle observables from the
    // persisted Models.Profile.GeneralSettings.AutoMode so the toolbar
    // IsActive badges match what the engines will actually read. Suppress
    // writeback during the reseed — otherwise the partial OnXxxChanged
    // hooks would re-persist what we just read.
    private void SyncAutoEngineTogglesFromProfile()
    {
        _suppressAutoEngineWriteback++;
        try
        {
            Models.Profile.CharacterProfile? profile = AppServices.Current.Profile.Current;
            Models.Profile.GeneralSettings general = profile is null
                ? new Models.Profile.GeneralSettings()
                : ReadGeneralFromProfile(profile);
            Models.Profile.AutoActionDefaults am = general.AutoMode;
            IsDisableHangupsActive = general.DisableHangups;
            IsAutoCombatActive   = am.AutoCombat;
            IsAutoNukeActive     = am.AutoNuke;
            IsAutoHealRestActive = am.AutoHealRest;
            IsAutoBlessActive    = am.AutoBless;
            IsAutoLightActive    = am.AutoLight;
            IsAutoGetItemsActive = am.AutoGetItems;
            IsAutoGetCashActive  = am.AutoGetCash;
            IsAutoSneakActive    = am.AutoSneak;
            IsAutoHideActive     = am.AutoHide;
            IsAutoSearchActive   = am.AutoSearch;
            // (Auto-train is no toolbar/menu surface — the Player Workshop
            // CP-Alloc toggles + Settings → Auto-Trainer own its sync now.)
        }
        finally
        {
            _suppressAutoEngineWriteback--;
        }
    }

    private static Models.Profile.AutoTrainerSettings ReadAutoTrainerFromProfile(
        Models.Profile.CharacterProfile profile)
    {
        if (profile.Settings is null) return new Models.Profile.AutoTrainerSettings();
        if (!profile.Settings.TryGetValue("AutoTrainer", out System.Text.Json.JsonElement json))
            return new Models.Profile.AutoTrainerSettings();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Models.Profile.AutoTrainerSettings>(
                       json.GetRawText())
                   ?? new Models.Profile.AutoTrainerSettings();
        }
        catch
        {
            // Malformed AutoTrainer JSON → treat as unset rather than crash the
            // reseed; the settings tab rewrites it cleanly on next Save.
            return new Models.Profile.AutoTrainerSettings();
        }
    }

    private static Models.Profile.GeneralSettings ReadGeneralFromProfile(
        Models.Profile.CharacterProfile profile)
    {
        if (profile.Settings is null) return new Models.Profile.GeneralSettings();
        if (!profile.Settings.TryGetValue("General", out System.Text.Json.JsonElement json))
            return new Models.Profile.GeneralSettings();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Models.Profile.GeneralSettings>(
                       json.GetRawText())
                   ?? new Models.Profile.GeneralSettings();
        }
        catch
        {
            return new Models.Profile.GeneralSettings();
        }
    }

    // Open InfoDialogs are tracked per title so menu / hotkey re-press toggles them shut.
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
