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
using MudPlay.ViewModels.Guides;
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
    private readonly Game.Map.InboundMoveEchoScanner _inboundMoveEchoScanner;
    private readonly Game.Map.CombatEntryRefusalHandler _combatEntryRefusalHandler;

    // The screen buffer the UI renders. Lifetime spans the whole window.
    public TerminalEmulator Emulator { get; } = new(80, 25);

    // Startup mud-throw splash on the terminal. Plays from launch and is cleared
    // the moment a session begins (first connect or first server data), reverting
    // the control to the live emulator. Bound to TerminalControl.SplashActive.
    [ObservableProperty] private bool _showSplash = true;

    // Whether the mud figure animates (Settings → General "Show the mud-throwing
    // startup animation"). When off, the splash still shows its static header (title +
    // byline + hint). Read at construction; bound to TerminalControl.SplashAnimate.
    [ObservableProperty] private bool _splashAnimate = true;

    // Extracts completed lines from the emulator's screen stream. Foundation
    // for every "what did the server say" subsystem (MessageRouter,
    // ChatRouter, Triggers, prompt parser).
    public LineExtractor Lines { get; }

    // Terminal-canvas font size — forwarded from AppServices.Display so the
    // Settings → General edits reach the live canvas without bouncing
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

    // Live layout for the customizable terminal right-click menu — the MainWindow
    // code-behind rebuilds the ContextMenu from this and re-runs on its
    // CollectionChanged, mirroring how the toolbar rebuilds from Toolbar.Layout.
    public Services.ContextMenuConfig ContextMenu => AppServices.Current.ContextMenu;

    // Live app state the guides' probes read.
    internal static readonly GuideProbes GuideProbes = new(
        HasGameData: () => AppServices.Current.GameData.AvailableSets.Count > 0,
        // A BBS only counts once it has a host — a freshly-Added record is an empty
        // placeholder, so the tour keeps guiding through host/port + OK instead of
        // treating the click of Add as "done".
        HasBbs: () => System.Linq.Enumerable.Any(
            System.Linq.Enumerable.Select(AppServices.Current.Bbs.ListNames(), AppServices.Current.Bbs.Get),
            b => b is not null && !string.IsNullOrWhiteSpace(b.Host)),
        HasCharacter: () => System.Linq.Enumerable.Any(AppServices.Current.Profile.ListAll()),
        CharacterLoaded: () => AppServices.Current.Profile.CurrentProfileName is not null,
        IsConnected: () => AppServices.Current.Connection.Connected);

    // The Visual Guides runner, the first-run setup tour among them. The
    // GuideOverlay and the floating GuideCardWindow bind to it; MainWindow's
    // code-behind auto-starts the setup tour on open (a prerequisite missing and
    // not dismissed), and Help → Visual Guides opens any guide. Progress and the
    // setup dismissal persist to the Global tier.
    public GuideViewModel Guide { get; } = new(
        GuideCatalogue.Build(GuideProbes),
        progress: () => AppServices.Current.Settings.Current.Guides,
        saveProgress: () => AppServices.Current.Settings.Save(),
        persistSetupDismiss: () =>
        {
            AppServices.Current.Settings.Current.FirstRunTutorialDismissed = true;
            AppServices.Current.Settings.Save();
            AppServices.Current.Log.Info("Tutorial", "first-run setup tour: dismissed — won't auto-show again");
        },
        log: message => AppServices.Current.Log.Info("Tutorial", $"visual guide {message}"));

    // Render-ready view-models for the dynamic toolbar ItemsControl. Mirrors
    // ToolbarConfig.Layout; each entry resolves through ToolbarItemCatalogue
    // and binds against the matching command on this view-model. Rebuilt
    // whenever Layout changes (Settings → Toolbar Apply path).
    public ObservableCollection<ToolbarButtonItem> ToolbarItems { get; } = new();

    // File → Quick Connect target. Wins over ResolveActiveBbs once set;
    // cleared when the user picks a (different) BBS via Settings → BBS + Display, or
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

    // Window title — "MudPlay v{version} — {profile} — {bbs}:{realm}". The version
    // is the running build's AppInfo.Version. When no profile is loaded the
    // placeholder {default} stands in; when no BBS is selected {No BBS} stands
    // in. Both slots always render so the title bar shape stays consistent. The
    // realm tells apart two clients on different realms of the same board.
    //
    // While an update is waiting, the scrolling banner replaces the version slot
    // rather than the whole title: the version is the stale part anyway, and the
    // character + BBS have to survive so someone running several clients can still
    // tell their taskbar buttons apart.
    public string WindowTitle
    {
        get
        {
            string lead    = _marqueeTimer is null
                ? $"MudPlay v{AppInfo.Version}"
                : UpdateTitleMarquee.Frame(_marqueeFrame);
            return $"{lead} — {TitleTail}";
        }
    }

    // "{profile} — {bbs}:{realm}": the part of the title that says whose window it
    // is. Every other window carries the same tail after its own name
    // (WindowTitleTail), so two clients' Settings windows can be told apart.
    public string TitleTail
    {
        get
        {
            string profile = AppServices.Current.Profile.CurrentProfileName ?? "{default}";
            string bbs     = _quickConnectTarget is null && AppServices.Current.ResolveActiveRealm() is { } active
                ? $"{active.Bbs.Name}:{active.Realm.Name}"
                : ActiveBbsName ?? "{No BBS}";
            return $"{profile} — {bbs}";
        }
    }

    // Slow enough to read, fast enough to look like it's moving. Each tick is a
    // substring plus one property-changed, so the cost is noise.
    private static readonly TimeSpan MarqueeInterval = TimeSpan.FromMilliseconds(220);

    // Drives the title-bar crawl. Null whenever no update is available — its
    // nullness IS the "banner showing?" flag WindowTitle reads.
    private DispatcherTimer? _marqueeTimer;
    private int _marqueeFrame;

    // Start/stop the crawl to match the update service's verdict. Called once at
    // construction (the startup check may still be in flight) and again on every
    // availability flip.
    private void SyncUpdateMarquee()
    {
        bool wanted = AppServices.Current.Update.UpdateAvailable;
        if (wanted == (_marqueeTimer is not null)) return;

        if (!wanted)
        {
            _marqueeTimer!.Stop();
            _marqueeTimer = null;
        }
        else
        {
            _marqueeFrame = 0;
            _marqueeTimer = new DispatcherTimer(MarqueeInterval, DispatcherPriority.Background, (_, _) =>
            {
                _marqueeFrame = (_marqueeFrame + 1) % UpdateTitleMarquee.Period;
                OnPropertyChanged(nameof(WindowTitle));
            });
            _marqueeTimer.Start();
        }
        OnPropertyChanged(nameof(WindowTitle));
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
    // state, so opening a recent profile is disconnected-only (Save stays
    // available). Re-evaluate its CanExecute when the wire flips.
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
    // buttons and the Action-menu check items write here (the Settings → General
    // checkboxes set the base modes these are reconciled to). The partial
    // OnXxxChanged handlers persist to the profile and refresh the toolbar
    // IsActive badge.
    [ObservableProperty] private bool _isAutoCombatActive;
    [ObservableProperty] private bool _isAutoNukeActive;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsAutoHealRestActive))] private bool _isAutoHealActive;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsAutoHealRestActive))] private bool _isAutoRestActive;
    [ObservableProperty] private bool _isAutoBlessActive;
    [ObservableProperty] private bool _isAutoLightActive;
    [ObservableProperty] private bool _isAutoGetItemsActive;
    [ObservableProperty] private bool _isAutoGetCashActive;
    [ObservableProperty] private bool _isAutoSneakActive;
    [ObservableProperty] private bool _isAutoHideActive;
    [ObservableProperty] private bool _isAutoSearchActive;

    // Master "Disable hangups" toggle. When on, every automatic disconnect
    // path (@hangup / @relog remote commands, low-HP emergency hangup,
    // nightly-cleanup log-off) is suppressed — the client drops the carrier
    // only on an explicit user action. Persisted in
    // Models.Profile.GeneralSettings.DisableHangups and reseeded on profile
    // load like the auto-mode toggles. It gates disconnects, not auto-engines.
    [ObservableProperty] private bool _isDisableHangupsActive;

    // Sprint Mode. A transient "just get me there" movement mode. While on:
    // HealthManager never pauses movement to rest/heal-wait (still casts
    // configured heal spells), and Auto-Combat / Get-Items / Search / Get-Cash
    // are forced off (nothing to fight / loot / search for). It auto-turns-off
    // — restoring exactly the engines it silenced — the instant a go-to walk
    // arrives, a loop starts its next lap, or an auto-lair is about to enter the
    // next lair; and manually turning any of those four engines back on ends it
    // too. See OnIsSprintModeActiveChanged + the nav-event handlers. Persisted
    // in Models.Profile.GeneralSettings.SprintMode, reseeded on profile load.
    // It's a movement mode, not an auto-engine.
    [ObservableProperty] private bool _isSprintModeActive;

    // The auto-engines Sprint Mode forced off when it turned on — remembered so
    // ending Sprint restores exactly those (and only those). Sprint forces off
    // only engines that were ON, so one the user later re-enables by hand is
    // never in this set and its state is preserved. Session-only, not persisted.
    private bool _sprintTurnedOffCombat;
    private bool _sprintTurnedOffGetItems;
    private bool _sprintTurnedOffSearch;
    private bool _sprintTurnedOffGetCash;

    // True while Sprint is programmatically flipping those engines (force-off on
    // start, restore on end), so their change handlers don't misread Sprint's
    // own writes as a manual re-enable and recurse.
    private bool _sprintDrivingEngines;

    // Auto-Combat was on and a "Run + Combat off" start turned it off for the trip:
    // it comes back on where Sprint would end (the walk-to arriving, the loop's
    // first waypoint, the Auto-Lair's first lair). Turning combat on by hand before
    // then drops the promise. Session-only.
    private bool _tripTurnedOffCombat;

    private void ApplyRunStartMode(Game.Map.RunStartMode mode)
    {
        switch (mode)
        {
            case Game.Map.RunStartMode.CombatOff:
                if (!IsAutoCombatActive) return;
                IsAutoCombatActive = false;
                _tripTurnedOffCombat = true;
                AppServices.Current.Log.Info("AutoMode", "Run + Combat off: Auto-Combat off until the run begins.");
                break;
            case Game.Map.RunStartMode.Sprint:
                if (IsSprintModeActive) return;
                IsSprintModeActive = true;
                _tripStartedSprint = true;
                AppServices.Current.Log.Info("AutoMode", "Sprint: Sprint Mode on until the run begins.");
                break;
        }
    }

    // A Sprint start turned Sprint Mode on and it hasn't ended yet.
    private bool _tripStartedSprint;

    // The open "which waiting events should run?" prompt, so a second ask brings
    // it forward instead of opening another.
    private HeldEventsPromptViewModel? _heldEventsPrompt;

    // The events manager asks which waiting events should still run: the master
    // switch held them for a long spell (EventManager's queue choice). Ticked
    // ones run in their order and the rest are dropped. Closed by its X, every
    // listed event is dropped ("drop all of them"; user, 2026-10-10).
    private async Task ShowHeldEventsPromptAsync()
    {
        AppServices services = AppServices.Current;
        if (_heldEventsPrompt is { Withdrawn: false } open && services.Dialogs.RaiseIfOpen(open)) return;
        IReadOnlyList<Game.Events.EventManager.HeldQueueEntry> waiting = services.Events.OfferQueueChoice();
        if (waiting.Count == 0) return;

        HeldEventsPromptViewModel prompt = new(waiting);
        _heldEventsPrompt = prompt;
        IReadOnlyList<Models.GameData.ScheduledEvent>? run;
        try
        {
            run = await services.Dialogs
                .OpenWindowAsync<HeldEventsPromptViewModel, IReadOnlyList<Models.GameData.ScheduledEvent>>(prompt);
        }
        catch (InvalidOperationException ex)
        {
            // No main window to own it. Nobody can be asked, and a question left
            // standing would hold every later event behind it.
            services.Log.Warn("Events", $"Couldn't open the waiting-events prompt ({ex.Message}).");
            services.Events.DropQueueChoice("the Waiting Events window could not be shown");
            return;
        }
        finally
        {
            if (ReferenceEquals(_heldEventsPrompt, prompt)) _heldEventsPrompt = null;
        }

        if (run is not null) services.Events.ResolveQueueChoice(run);
        // Closed by its X. Not when the manager took the question back (the
        // switch went off again, the queue is gone), nor when the whole client is
        // closing and took the window with it.
        else if (!prompt.Withdrawn && MainWindowIsOpen())
            services.Events.DropQueueChoice(Game.Events.EventManager.PromptClosedUnanswered);
    }

    private static bool MainWindowIsOpen() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow.IsVisible: true };

    // Movement was stopped on purpose: the Stop button or its hotkey, the Navigation
    // window's Stop, or a remote @stop (which pauses, so endsTheRun is false). The
    // live auto toggles go back to the character's base modes, as they do when a
    // walk-to arrives: one switched off by hand for the trip (Auto-Combat, to get
    // somewhere without fighting) comes back even if the user forgets it, instead
    // of the character standing in a room of monsters with combat off (reports
    // paradigm-20260923-163805, -163843, -164004).
    //
    // Two cases keep their own rule. A Run / Sprint start stopped before it began
    // is settled by its Settings → Other box (OnUserStoppedRun). And while Sprint
    // Mode is on, the autos it silenced are Sprint's to restore when it ends.
    private void OnUserStopped(string reason, bool endsTheRun)
    {
        bool tripPending = _tripTurnedOffCombat || _tripStartedSprint;
        if (endsTheRun) OnUserStoppedRun();
        if (tripPending || IsSprintModeActive) return;
        ReconcileAutoModeToBase(reason);
    }

    // The user stopped the run before it began (a walk-to short of its destination, a
    // loop still walking to its start, an Auto-Lair short of its first lair). Settings →
    // Other decides whether the autos a Run / Sprint start turned off come back now or
    // stay off (the default); either way the trip's promise ends here, so a later,
    // unrelated arrival doesn't flip them.
    private void OnUserStoppedRun()
    {
        if (!_tripTurnedOffCombat && !_tripStartedSprint) return;
        Models.Profile.OtherSettings other = AppServices.Current.Resolver.Resolve<Models.Profile.OtherSettings>("Other");
        if (_tripTurnedOffCombat)
        {
            if (other.RunStopRestoresCombat) EndTripCombatOff();
            else
            {
                _tripTurnedOffCombat = false;
                AppServices.Current.Log.Info("AutoMode", "Run stopped before it began — Auto-Combat stays off (Settings → Other).");
            }
        }
        if (_tripStartedSprint)
        {
            _tripStartedSprint = false;
            if (other.SprintStopEndsSprint && IsSprintModeActive)
            {
                IsSprintModeActive = false;
                AppServices.Current.Log.Info("AutoMode", "Sprint stopped before it began — Sprint Mode ended, its autos back on (Settings → Other).");
            }
            else AppServices.Current.Log.Info("AutoMode", "Sprint stopped before it began — Sprint Mode stays on (Settings → Other).");
        }
    }

    // The trip's over (walk-to arrived, loop circuit / Auto-Lair begun): give back the
    // combat a "Run + Combat off" start took away.
    private void EndTripCombatOff()
    {
        if (!_tripTurnedOffCombat) return;
        _tripTurnedOffCombat = false;
        // With the master switch off it is the switch's to give back.
        if (AppServices.Current.AutoModeController.KeepsForSwitchOn("Run + Combat off", d => d.AutoCombat = true)) return;
        if (!IsAutoCombatActive) IsAutoCombatActive = true;
        AppServices.Current.Log.Info("AutoMode", "Run + Combat off: the run has begun — Auto-Combat back on.");
    }

    // The master switch (Game.AutoModeController): drives the "Auto-All" button's
    // depressed state and the menu item's tick. It follows the switch itself, not
    // the eleven toggles: with every toggle unticked by hand the switch is still on.
    [ObservableProperty] private bool _isMasterSwitchOn = true;

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

    // "Hangup watch off 0:59" beside the connection light, for the minute after a
    // reconnect that follows a hang-up for a Hangup-relationship monster. Empty
    // otherwise. MonsterRelationshipWatcher owns the countdown; this mirrors its text.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHangupWatchHoldVisible))]
    private string _hangupWatchHoldText = string.Empty;

    public bool IsHangupWatchHoldVisible => !string.IsNullOrEmpty(HangupWatchHoldText);

    // Status-bar statline-mismatch warning: the explanation shown as its tooltip.
    // Set while StatlineReconciler has the mismatch flagged; empty hides it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatlineMismatchVisible))]
    private string _statlineMismatchTip = string.Empty;

    public bool IsStatlineMismatchVisible => !string.IsNullOrEmpty(StatlineMismatchTip);

    // ----- Status-bar location slot (mirrors NavigationViewModel) ----

    // Text shown in the bottom status bar's location slot. Mirrors the
    // Navigation window's current-room label so the two stay in sync — the
    // bottom bar should never show "Unknown location" while the Navigation
    // strip knows where the player is.
    [ObservableProperty] private string _locationText = "Unknown location";
    [ObservableProperty] private string _expRateText = "0/hr";
    [ObservableProperty] private string _timeToLevelText = string.Empty;

    // The status bar under the terminal: rows of items laid out in Settings → BBS +
    // Display (global). Built in the constructor, rebuilt when the layout is saved.
    public ViewModels.StatusBar.StatusBarViewModel StatusBar { get; }


    // ----- Engine-state chip (mirrors the Navigation window's badge) -----

    private Game.Map.WalkState _walkerState   = Game.Map.WalkState.Idle;
    private bool               _loopRunning;
    private bool               _autoLairOn;

    // Route cards up for a Recover Now's walk, and whether any was picked from.
    private int  _recoverNowCardsOpen;
    private bool _recoverNowWalkPicked;

    // Label inside the chip — short upper-case state tag. PAUSED while the user's
    // own pause holds a running engine, so a paused loop doesn't go on reading
    // LOOPING.
    public string EngineActionBadge =>
        !EngineActionIsIdle && AppServices.Current.MovementControl.IsUserPaused ? "PAUSED"
        : _autoLairOn                                   ? "AUTO-LAIR"
        : _loopRunning                                  ? "LOOPING"
        : SpillSweepRunning                             ? "RECOVERING"
        : (_walkerState != Game.Map.WalkState.Idle)     ? "WALKING"
        :                                                 "IDLE";

    // A Stock spill sweep stands in rooms looking, getting and searching with the
    // walker idle. Stop and Pause are live for it then, so the chip mustn't read IDLE.
    private static bool SpillSweepRunning => AppServices.Current.DeathRecovery.SpillSweepActive;

    public bool EngineActionIsIdle    => !_autoLairOn && !_loopRunning && _walkerState == Game.Map.WalkState.Idle && !SpillSweepRunning;
    public bool EngineActionIsWalking => !_autoLairOn && !_loopRunning && (_walkerState != Game.Map.WalkState.Idle || SpillSweepRunning);
    public bool EngineActionIsLooping => !_autoLairOn &&  _loopRunning;
    public bool EngineActionIsLair    =>  _autoLairOn;

    private void OnWalkerEngineEvent(Game.Map.WalkEvent e)
        => Dispatcher.UIThread.Post(() =>
        {
            _walkerState = AppServices.Current.Walker.State;
            RefreshEngineActionChip();
            if (e.Kind == Game.Map.WalkEventKind.Started && !_loopRunning && !_autoLairOn)
                Guide.NotifyActionDone(GuideActions.WalkStarted);
            // A standalone walk-to ARRIVING settles the live auto-engines back into the
            // character's base modes, the same way loop-start does, so the toggles you
            // flipped for the trip return to their defined state once you're there. Only
            // an arrival: a walk stopped or failed mid-route — an errand (auto-deposit,
            // sell detour, …) taking the walker over to replan, say — hasn't got you
            // there, so your trip toggles stay (report paradigm-20260929-191251: combat
            // turned itself back on when a bank detour replanned the walk to a loop).
            // Nor an errand's own leg arriving. Only a STANDALONE walk-to counts: during a
            // loop or auto-lair the walker also fires these on each sub-path, and those
            // are handled by the lap-boundary / pre-lair hooks instead. Guard on the live
            // engine state, not cached flags, to avoid a stale-field race.
            if (e.Kind == Game.Map.WalkEventKind.Finished
                && AppServices.Current.LoopRunner.State == Game.Map.LoopState.Idle
                && !AppServices.Current.AutoLair.IsActive
                && !AppServices.Current.ErrandHasTheWalker)
            {
                // End Sprint first (restoring the engines it silenced), then let the base
                // modes get the final word — same ordering as the loop-start reconcile.
                if (IsSprintModeActive) IsSprintModeActive = false;
                EndTripCombatOff();
                ReconcileAutoModeToBase("walk-to end");
            }
        });

    private void OnLoopRunnerEngineEvent(Game.Map.LoopEvent e)
        => Dispatcher.UIThread.Post(() =>
        {
            _loopRunning = AppServices.Current.LoopRunner.State != Game.Map.LoopState.Idle;
            RefreshEngineActionChip();
            if (e.Kind == Game.Map.LoopEventKind.Started) Guide.NotifyActionDone(GuideActions.LoopStarted);
            // Loop status owns the location slot while active — refresh on every
            // event so the lap counter ticks over on RepeatStarted and the slot
            // clears on Stopped.
            RefreshLocationSlot();
            // A loop moving from its walk-to-start INTO looping (ReachedFirstWaypoint)
            // or wrapping into its next lap (RepeatStarted) ends Sprint Mode — you
            // sprinted the leg that got you here; looping runs normally with the
            // engines restored. Done BEFORE the base-modes reconcile below so base
            // modes get the final word over Sprint's restore at a loop start.
            if ((e.Kind == Game.Map.LoopEventKind.ReachedFirstWaypoint
                 || e.Kind == Game.Map.LoopEventKind.RepeatStarted)
                && IsSprintModeActive)
                IsSprintModeActive = false;
            if (e.Kind == Game.Map.LoopEventKind.ReachedFirstWaypoint) EndTripCombatOff();
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
            if (active) Guide.NotifyActionDone(GuideActions.AutoLairStarted);
        });

    // Latches the auto-lair base-modes reconcile to the FIRST lair entry of a run
    // (not every subsequent lair the circuit clears). Re-armed on ActiveChanged.
    // The loop path needs no latch — its ReachedFirstWaypoint is already one-shot.
    private bool _autoLairBaseReconciled;

    private void OnAutoLairPhaseChangedForBase(Game.Map.AutoLairPhase phase)
        => Dispatcher.UIThread.Post(() =>
        {
            // About to step into the next lair — end Sprint so we cross the
            // threshold with the engines (combat especially) restored and fight it
            // normally. Sprint got us here fast; it doesn't enter the lair.
            if (phase == Game.Map.AutoLairPhase.Entering && IsSprintModeActive)
                IsSprintModeActive = false;
            if (phase == Game.Map.AutoLairPhase.Entering) EndTripCombatOff();

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
            // The whole chip, not only its label: a solver starting or ending (the
            // spill sweep) changes which state it shows with no walker event.
            RefreshEngineActionChip();
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

    // Set by an involuntary drop; the next successful connect plays the Reconnected
    // sound and clears it.
    private bool _soundOwesReconnect;

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
        // trusted player, or HealthManager's emergency hangup. Never
        // auto-retries (user is presumed to be in a
        // dangerous spot and needs to manually decide whether to come back).
        // The matching Game.MainMenuEntryAutomation.Arm also skips when this
        // fired, so the user manually re-enters and the post-entry
        // stat/exp/i refresh doesn't spam the wire.
        HangupInitiated,
        // Deliberate relog originated client-side — remote @relog from a
        // trusted player, or a `;o` the player sent. The character gracefully exits
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

    // 100 ms repaint cadence for the three status-bar tick countdowns — fast
    // enough to look live without burning cycles. State sourced from
    // AppServices.Tick (combat) + AppServices.Regen (HP / MA).
    private readonly DispatcherTimer _statusTickRefresh;
    private readonly DispatcherTimer _engineStateSample;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptureMenuLabel))]
    private bool _isDumping;

    // Label shown on the Tools menu's capture-toggle entry.
    public string CaptureMenuLabel => IsDumping ? "Stop capture" : "Start capture";

    // Where session captures land when the user toggles capture. Stays under
    // the app folder's Logs folder so it's covered by the same rotation policy
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

        StatusBar = new ViewModels.StatusBar.StatusBarViewModel(this, preview: false);
        StatusBar.Apply(Models.Settings.StatusBarSettings.Read(AppServices.Current.Settings.Current));

        Lines = new LineExtractor(Emulator);
        Capture = new CaptureSession(Emulator.Screen.Scrollback);

        // The control API's transcript endpoints need the live screen, and we own
        // it. AppServices is built before this ctor, so it takes a provider rather
        // than the instance.
        AppServices.Current.SetEmulatorProvider(() => Emulator);
        AppServices.Current.SetConnectionProvider(() =>
            new AppServices.ConnectionSnapshot(IsConnected, IsConnecting, IsReconnectPending));

        // Live-screen watch for the character-creation stat box. It's drawn with
        // cursor positioning, so its marker row never completes as an emitted
        // line until teardown — too late to flip arrow keys into direct-input
        // mode. Scanning the screen each feed lets TrainerMenuTracker arm
        // character mode the moment the box appears. Cheap: pre-filtered on the
        // stat-box marker and skipped entirely once armed.
        Emulator.ScreenUpdated += OnScreenUpdatedForTrainerMenu;
        Emulator.ScreenUpdated += OnScreenUpdatedForSuicidePrompt;
        // A typed line is kept in the sent-command trail only when it was typed
        // at the game's own prompt: what answers any other prompt may be a
        // password, and the game's prompts for those reach us too late to know.
        // The prompt is whatever Settings -> Statline makes it, so the row is
        // asked of the pattern built from that setting.
        AppServices.Current.SentCommands.TypedAtGamePrompt =
            line => SentCommandTrail.RowIsGamePrompt(
                CursorRowText(), line, AppServices.Current.CurrentStatlinePromptRegex);

        // Feed the crash reporter a live-state snapshot so a fatal fault's
        // Crash-<timestamp>.md carries the same scrollback / log / engine dump a
        // manual bug report would. Reads the current Emulator at call time (it
        // can be swapped per connection); guarded on the reporter's side.
        CrashReporter.RegisterStateProvider(() =>
            BugReportBuilder.RenderStateOnly(
                BugReportBuilder.Capture(AppServices.Current, Emulator)));
        CrashReporter.RegisterTextFilter(document =>
            BugReportBuilder.BuildScrubber(AppServices.Current).Apply(document));

        // 100 ms refresh — matches TickEngine's internal cadence so the
        // countdown ticks down by 0.1 s each repaint instead of jumping
        // in 0.5 s chunks.
        _statusTickRefresh = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        // The exp/hr beside the location is a continuously-decaying rate (windowed
        // experience ÷ elapsed time), so it rides this same tick — the location's
        // own room / engine events fire too rarely to keep it live, which left it
        // frozen at its entry value (0/hr at session start) while the Session
        // Stats window, ticking the same tracker, showed the real rate. The
        // setters' equality checks drop the repaint when the text is unchanged, so
        // most ticks cost only a few string compares.
        _statusTickRefresh.Tick += (_, _) =>
        {
            RefreshStatusBarTicks();
            RefreshLocationSlot();
        };
        _statusTickRefresh.Start();

        // Once a second: note how long each engine has been in its state (for a
        // bug report's "Holds and waits"), and let the heartbeat decide whether
        // its next status line is due.
        BugReportHoldsAndWaits.RegisterProbes(AppServices.Current);
        _engineStateSample = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = EngineStateWatch.SampleInterval,
        };
        _engineStateSample.Tick += (_, _) =>
        {
            DateTimeOffset now = DateTimeOffset.Now;
            AppServices.Current.StateWatch.Sample(now);
            AppServices.Current.Heartbeat.Tick(now);
            AppServices.Current.Suicide.CheckTimeout();
        };
        _engineStateSample.Start();
        AppServices.Current.Heartbeat.LogStartup(
            AppServices.Current.GameData.ActiveSet, AppServices.Current.Settings.Current.LastUsedProfile?.Name);
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
        AppServices.Current.SetRoomGameDataOpener(OpenRoomGameData);
        AppServices.Current.SetNavigateToRoomOpener(FocusNavigationOnRoom);
        AppServices.Current.SetQueueWalkOpener(QueueWalkToRoom);
        AppServices.Current.SetGoWalkOpener(GoWalkToRoom);
        AppServices.Current.SetErrandWalkOpener(ErrandWalkToRoom);
        AppServices.Current.SetCenterNavigationIfOpenOpener(CenterNavigationOnRoomIfOpen);
        AppServices.Current.SetHighlightWhereOpener(HighlightWhereRoomIfOpen);
        AppServices.Current.SetLeaderRouteOpener(ShowLeaderRouteIfOpen);
        AppServices.Current.SetLeaderGotoOpener(ShowLeaderGotoIfOpen);
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
        // rooms in 1.x exports). Modeless yes/no asks whether to keep
        // the new name with the realm's data; session-deduped so a single
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
        // Notify both the display labels (Recent0..9 — "name - bbs")
        // and the raw profile names (ProfileName0..9 — used as the
        // OpenRecentProfile command parameter) on every list change.
        RecentProfiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Recent0));
            OnPropertyChanged(nameof(Recent1));
            OnPropertyChanged(nameof(Recent2));
            OnPropertyChanged(nameof(Recent3));
            OnPropertyChanged(nameof(Recent4));
            OnPropertyChanged(nameof(Recent5));
            OnPropertyChanged(nameof(Recent6));
            OnPropertyChanged(nameof(Recent7));
            OnPropertyChanged(nameof(Recent8));
            OnPropertyChanged(nameof(Recent9));
            OnPropertyChanged(nameof(ProfileName0));
            OnPropertyChanged(nameof(ProfileName1));
            OnPropertyChanged(nameof(ProfileName2));
            OnPropertyChanged(nameof(ProfileName3));
            OnPropertyChanged(nameof(ProfileName4));
            OnPropertyChanged(nameof(ProfileName5));
            OnPropertyChanged(nameof(ProfileName6));
            OnPropertyChanged(nameof(ProfileName7));
            OnPropertyChanged(nameof(ProfileName8));
            OnPropertyChanged(nameof(ProfileName9));
            OnPropertyChanged(nameof(HasRecents));
        };
        RebuildRecentProfiles();
        SyncProfileMenuState();
        AppServices.Current.Profile.ProfileLoaded += OnProfileLoadedForConnect;
        // On profile load the base-modes checkboxes set the live engine positions
        // (no-op for a character that predates the base/live split). Runs before
        // the badge reseed below; the reconcile also reseeds when it changes state.
        AppServices.Current.Profile.ProfileLoaded += _ => ReconcileAutoModeToBase("profile load");
        AppServices.Current.AutoModeController.KillSwitchToggled += off =>
            Dispatcher.UIThread.Post(() => IsMasterSwitchOn = !off);
        AppServices.Current.AutoModeController.ResetByProfileLoad += () =>
            Dispatcher.UIThread.Post(() => IsMasterSwitchOn = true);
        // Posted: the switch can be switched by a remote `@auto-all` from inside
        // the chat pump, and a window is not opened (nor a notice written) there.
        AppServices.Current.Events.QueueChoiceNeeded += () =>
            Dispatcher.UIThread.Post(() => _ = ShowHeldEventsPromptAsync());
        AppServices.Current.Events.QueueChoiceWithdrawn += () =>
            Dispatcher.UIThread.Post(() => _heldEventsPrompt?.Withdraw());
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
        // A realm change re-pins: the title and the recent list show the realm.
        AppServices.Current.Profile.BbsPinApplied += _ => { ClearQuickConnect(); RefreshBbsBindings(); RebuildRecentProfiles(); };

        // Seed the BBS-pin sentinel so OnProfileMutatedForBbs can detect
        // the first real change against a known baseline.
        _lastSeenBbsName = ResolveActiveBbs()?.Name;

        // Help-menu website list (Global tier). Seed now, then re-read on every
        // global-settings Save so the Toolbar + Shortcuts editor's changes land
        // in the menu without a relaunch.
        RefreshHelpLinks();
        AppServices.Current.Settings.GlobalSettingsChanged += global =>
        {
            if (StatusBar.Apply(Models.Settings.StatusBarSettings.Read(global)))
                AppServices.Current.Log.Info("StatusBar", $"Layout changed: {StatusBar.Describe()}.");
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
        // cleanup window), which switches the reactive reconnect from
        // the 5s cycle to a single long-delay attempt at now +
        // CleanupPeriodMinutes.
        AppServices.Current.Cleanup.CleanupModeDetected += OnCleanupModeDetected;

        // Forward DisplayConfig.FontSize changes to TerminalFontSize so the
        // bound TerminalControl re-renders when Settings → General changes
        // the font live. Also resize the live scrollback when ScrollbackLines
        // (Settings → BBS + Display) moves.
        AppServices.Current.Display.PropertyChanged += OnDisplayChanged;

        // Seed the Game Data menu's set list. Rebuild on every
        // signal that could change which row carries the checkmark:
        // a different set is now active, a different BBS got pinned,
        // a profile re-mutated (BBS rename), or a fresh profile loaded.
        RebuildGameDataSetsMenu();
        AppServices.Current.GameData.ActiveSetChanged += _ => RebuildGameDataSetsMenu();
        // A game-data table whose on-disk JSON can't be parsed (a corrupt / truncated
        // file from a bad or older MDB import) is dropped instead of crashing the
        // lookup (see GameDataCache). Surface it loudly on the terminal — a silently
        // missing table leaves the engines short of data with no obvious symptom.
        // POST it: the lookup can fire from inside the emulator's message pump, and
        // WriteTerminalStatus re-feeds the emulator — a synchronous call would re-enter
        // Emulator.Feed and crash (same reason as the combat-profile echo below).
        AppServices.Current.GameData.TableParseFailed += table =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => WriteTerminalStatus(
                $"[GAME DATA: table '{table}' is corrupt and was not loaded — re-import the set; "
                + "some features will be missing data]", TerminalStatusKind.Error));
        // A profile whose set's name can't be a folder runs with no game data; say so
        // where the user looks, not only in the program log. The start-up profile was
        // refused before this subscription existed, so its notice is read here once.
        AppServices.Current.GameDataSetRefused += notice =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                WriteTerminalStatus($"[GAME DATA: {notice}]", TerminalStatusKind.Error));
        if (AppServices.Current.RefusedGameDataSetNotice is { } refusedAtStart)
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // As with the start-up profile notice: the splash would hide it.
                if (ShowSplash) ShowSplash = false;
                WriteTerminalStatus($"[GAME DATA: {refusedAtStart}]", TerminalStatusKind.Error);
            });
        // A statline mismatch the reconciler couldn't fix leaves HP unreadable, so
        // every HP-gated engine stalls with no visible cause — warn on the terminal
        // once and keep a status-bar warning up until a prompt matches. POST it:
        // FlagChanged fires from inside PromptScanner.Append.
        AppServices.Current.StatlineReconcile.FlagChanged +=
            () => Avalonia.Threading.Dispatcher.UIThread.Post(OnStatlineFlagChanged);
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

        // Sys Goto flyout, rebuilt on profile load (character/BBS swap) and on any
        // profile mutation (a credentials edit fires ProfileMutated) so toggling the
        // power or editing the table refreshes the rows. The catalogue capability gate
        // hides the whole flyout when the power is off.
        RebuildSysopGotoMenu();
        AppServices.Current.Profile.ProfileLoaded += _ => RebuildSysopGotoMenu();
        AppServices.Current.Profile.ProfileMutated += _ => RebuildSysopGotoMenu();

        // Casting spell profiles (Settings → Combat): the Action → Combat Profiles
        // fly-out and the toolbar profile-menu button share one item list, rebuilt on any
        // profile change; every swap echoes its report to the terminal.
        // POST the echo, don't call it inline: an @profile remote swap fires from
        // INSIDE the emulator's message pump (mid-parse of the incoming telepath),
        // and WriteTerminalStatus re-feeds the emulator — a synchronous echo there
        // re-enters Emulator.Feed reentrantly and crashed the receiving client.
        // Deferring lands it after the pump unwinds, like the other pump-fired
        // notices (OnQuestAvailable / OnEquipSlotBlocked / OnGhSweepCompleted).
        AppServices.Current.CombatProfiles.Announce =
            report => Avalonia.Threading.Dispatcher.UIThread.Post(
                () => WriteTerminalStatus($"[{report}]", TerminalStatusKind.Notice));
        // Generic terminal-notice sink (e.g. the Buff Watchdog "Unlearned spells"
        // dump): text arrives already bracketed, so pass it through verbatim.
        AppServices.Current.SetTerminalNotice(
            text => Avalonia.Threading.Dispatcher.UIThread.Post(
                () => WriteTerminalStatus(text, TerminalStatusKind.Notice)));
        // Let non-main surfaces (Settings → BBS + Display) open the Profile Management window.
        AppServices.Current.SetOpenProfileManager(OpenProfileManager);
        AppServices.Current.SetStatusBarPreviewFactory(() => new ViewModels.StatusBar.StatusBarViewModel(this, preview: true));
        RebuildCombatProfilesMenu();
        AppServices.Current.CombatProfiles.Changed += RebuildCombatProfilesMenu;

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
        // Chat rides its own event (LineExtractor withholds it from LineEmitted so no
        // game-state parser can be driven by player-typed text); the router takes both
        // lanes, and itself confines chat lines to the conversation.* patterns.
        Lines.LineEmitted     += line => AppServices.Current.Router.Dispatch(line);
        Lines.ChatLineEmitted += line => AppServices.Current.Router.Dispatch(line);
        Lines.SlowLine        += LogSlowLine;

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
        _whoListParser.ListRead += AppServices.Current.PvpStrangers.NoteWhoRead;
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
        // Windows a profile reopens when it loads (see WindowLayoutStore), by the id
        // each window attaches under.
        Services.WindowLayoutStore layouts = AppServices.Current.WindowLayouts;
        layouts.RegisterOpener("backscroll", OpenBackscroll);
        layouts.RegisterOpener("buffwatchdog", OpenBuffWatchdog);
        layouts.RegisterOpener("conversation", OpenConversation);
        layouts.RegisterOpener("gamedata", OpenGameDataBrowser);
        layouts.RegisterOpener("logpane", OpenLogPane);
        layouts.RegisterOpener("monster-intel", OpenMonsterIntel);
        layouts.RegisterOpener("navigation", OpenNavigation);
        layouts.RegisterOpener("party", OpenParty);
        layouts.RegisterOpener("playersseen", OpenPlayersSeen);
        layouts.RegisterOpener("round-totals", OpenRoundTotals);
        layouts.RegisterOpener("session-stats", OpenSessionStats);
        layouts.RegisterOpener("settings", OpenSettings);
        layouts.RegisterOpener("spellbook", OpenSpellBook);
        layouts.RegisterOpener("transactions", OpenTransactionHistory);
        layouts.RegisterOpener("wireinspector", OpenWireInspector);
        layouts.RegisterOpener("workshop", OpenWorkshop);
        _monsterLookParser.TargetObserved += OnMonsterLookTarget;
        AppServices.Current.MonsterHpEstimates.EstimateChanged += OnMonsterEstimateChanged;
        // A kill in the room retires whatever target we last looked at.
        AppServices.Current.MonsterDeath.MonsterDied += OnMonsterDied;
        AppServices.Current.AutoEquip.HeldSetChanged += OnHeldGearSetChanged;
        // Quest becomes available (trained past its min level, or the login dump) → a
        // yellow terminal notice.
        AppServices.Current.QuestAvailability.QuestBecameAvailable += OnQuestAvailable;

        // Roomba sweep finished → a terminal notice, same style as the quest one.
        AppServices.Current.GhSweep.SweepCompleted += OnGhSweepCompleted;

        // A gear-set slot got blocked (can't wear the item — alignment/level/class
        // or the game refused the wear) → a yellow terminal notice so the user
        // knows to adjust the set instead of the engine silently skipping it.
        AppServices.Current.Equipment.SlotBlockedAnnounced += OnEquipSlotBlocked;

        // `set follow blind` is on: say so once in the terminal. Posted, since it is
        // raised from inside the message pump.
        AppServices.Current.FollowModes.BlindNoticed += () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => WriteTerminalStatus(
                "[Follow mode is Blind: following prints no room, so the map can't track you as a "
                + "follower — `set follow normal` turns the room displays back on]",
                TerminalStatusKind.Notice));

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
        // And confirms a fired `sys goto` landing: when the shown room name matches
        // the expected landing, SysopGoto commits the position (a from-anywhere
        // teleport has no graph edge for the tracker to follow otherwise).
        _roomDisplayParser.RoomParsed += obs => AppServices.Current.SysopGoto.OnRoomDisplayed(obs.Name);
        // And settles a boss's roster vanish: the "Also here:" it went missing from is a
        // kill only if this display is the same room.
        _roomDisplayParser.RoomParsed += obs => AppServices.Current.BossTimers.OnRoomDisplayed(obs.Name);
        // A room display is the statline reconciler's in-game gate. Not the first
        // matching prompt — a prompt that never matches is the very thing it
        // watches for.
        _roomDisplayParser.RoomParsed += _ => AppServices.Current.StatlineReconcile.NoteRoomDisplayed();
        // Ends the walker hold a post-kill floor check put up.
        _roomDisplayParser.RoomParsed += _ => AppServices.Current.AutoGetItems.NoteRoomDisplayed();
        // Ends the walker hold a summon-on-death kill put up, when the room came
        // back empty.
        _roomDisplayParser.RoomParsed += _ => AppServices.Current.SummonSettle.NoteRoomDisplayed();
        // Tells the hang-up item check that the room, floor list included, has been read.
        _roomDisplayParser.RoomParsed += _ => AppServices.Current.HangupItems.NoteRoomDisplayed();
        // Under a typed break's hold the combat engine asks to see the room once a
        // fight in it goes quiet; a display that lists nobody is how an emptied room
        // answers. A `look <direction>` shows the next room and answers nothing.
        _roomDisplayParser.RoomParsed += _ =>
        {
            if (!AppServices.Current.RoomTracker.IsPeekSuppressed())
                AppServices.Current.Combat.NoteRoomDisplayed();
        };
        _movementRefusalDetector = new Game.Map.MovementRefusalDetector(Lines,
            AppServices.Current.RoomTracker, AppServices.Current.Log,
            AppServices.Current.Conditions.IsConfuseFumbleLine,
            AppServices.Current.Conditions.IsActiveHoldLine);
        _movementRefusalDetector.PaidCrossingRefused += AppServices.Current.OnPaidCrossingRefused;
        // Feeds the server's move-command echo ("[HP=..]:e") to the tracker so it
        // confirms a move's landing on that causal signal rather than guessing by
        // timing — the fix for phantom-advancing through identically-named grids.
        // Passes the active statline matcher so the echo is read after whatever
        // prompt the player configured, not just the default shape.
        _inboundMoveEchoScanner = new Game.Map.InboundMoveEchoScanner(Lines,
            AppServices.Current.RoomTracker,
            () => AppServices.Current.CurrentStatlinePromptRegex);
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
        // Stages lines neither the message catalogue above nor any registered
        // Router pattern recognized, as review candidates for the catalogue.
        AppServices.Current.MessageCandidateWatcher.AttachLineExtractor(Lines);
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
        // Sysop room-status parser — reads the `sys st` block. Inert until an
        // outbound sysop status arms it.
        AppServices.Current.SysRoomStatus.AttachLineExtractor(Lines);
        // Self bank-balance listing — parses the `bank` command's per-bank
        // "On deposit: N copper farthings" blocks so the route picker can weigh a
        // buy the purse can't cover against money on deposit.
        AppServices.Current.BankBalance.AttachLineExtractor(Lines);
        // Telepath acks ("--- Telepath Sent to X ---" / "--- Telepath Not Sent ---")
        // retire or resend the pacer's in-flight telepaths.
        AppServices.Current.Telepaths.AttachLineExtractor(Lines);
        AppServices.Current.Telepaths.SetWriter(WriteToWire);
        // Quest-flag reader — parses the `abil` / `sys god … abil` replies during the
        // login completion sync.
        AppServices.Current.QuestFlagReader.AttachLineExtractor(Lines);
        // The @quest command's dedicated on-demand reader shares the same reply shapes.
        AppServices.Current.QuestQueryReader.AttachLineExtractor(Lines);
        // Let a replaced / re-imported user emote image drop its cached bitmap.
        AppServices.Current.Emotes.ConversationImageInvalidate = Views.ConversationMessageInlines.InvalidateEmoteImage;
        // Token charge tracker — parses each held token's `look` "Uses remaining: N"
        // reply and the "You invoke the token…" use line (Paradigm transport tokens).
        AppServices.Current.Tokens.AttachLineExtractor(Lines);
        // General limited-use item charges — captures "Uses remaining: N" from any
        // `look` reply (tokens ride the same line feed; other items when looked).
        AppServices.Current.ItemCharges.AttachLineExtractor(Lines);
        // Stock use-counting confirms a use by the item's cast message on the line feed.
        AppServices.Current.ItemUseCounts.AttachLineExtractor(Lines);
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
        // Another player's corpse recovered for them: its lines fill the handoff record.
        AppServices.Current.CorpseHandoff.AttachLineExtractor(Lines);
        // The Emulator lives here, so hand death-recovery a provider for the
        // backscroll tail it snapshots at each death ("How did I Die?").
        AppServices.Current.DeathRecovery.AttachTranscriptTail(
            () => TranscriptSnapshot.Tail(Emulator, 200, withCells: true));
        // The chunk now being drawn is where a bug report stops copying when the
        // board's menu arrives in it.
        AppServices.Current.InGameCapture.FeedTime = () => Emulator.Screen.FeedTimestamp;
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
        // Each engine is handed the same sender under its own name, which the
        // sent-command trail prints beside every command it puts out.
        Action<byte[]> Named(string sender) => AppServices.Current.EngineGate.Named(engineSend, sender);

        // A move step put on the wire while the gate was locked is silently dropped;
        // when the last hold clears, re-drive any step the walker is still stalled
        // on so it doesn't sit forever with a route drawn and nothing sent (the
        // auto-train loop-resume stall, report paradigm-20260813-063517).
        AppServices.Current.EngineGate.Released += AppServices.Current.Walker.NudgeStalledStep;
        // Likewise an attack decided while the gate held (a room shown the moment the
        // train-stats screen closed): combat re-decides it once the hold lifts.
        AppServices.Current.EngineGate.Released += AppServices.Current.Combat.OnWireReleased;

        // Combat-gated-entry handler sends `break` on refusal — needs the same
        // gate-wrapped wire path.
        _combatEntryRefusalHandler.SetWireSender(Named("CombatEntryRefusal"));
        AppServices.Current.AlignmentCheck.SetWireSender(Named("AlignmentCheck"));

        // Auto-invite on reconnect needs a wire-sender to send
        // "invite <name>" when a disconnected member returns within the
        // grace window AND we're the party leader.
        AppServices.Current.Party.SetWireSender(Named("Party"));

        // Macro dispatcher needs a wire-send callback before it can fire.
        // TerminalControl + ConversationWindow's input both call into the
        // dispatcher on KeyDown — without a sender bound, the call returns
        // false and the keystroke falls through to normal handling.
        // What a macro, an event's Command action and a trigger's response send is a
        // command the user set up, not one an engine decided: marked so, a gear
        // command among them mid-fight gets the same re-attack a typed one does.
        // (An alias expands a typed line and goes out as typed already; the Action
        // menu's Equip entries apply a gear set through the Equipment Manager, which
        // re-attacks after its own swap.)
        Action<byte[]> userCommandSend = AppServices.Current.EngineGate.MarkUserCommands(engineSend);
        AppServices.Current.MacroDispatcher.SetSender(AppServices.Current.EngineGate.Named(userCommandSend, "Macro"));

        // Bind the same gate-wrapped wire path to the EventManager so
        // Command-action events route through SendUserInput like every
        // other engine.
        AppServices.Current.Events.SetWireSender(AppServices.Current.EngineGate.Named(userCommandSend, "Event"));

        // Trigger engine subscribes to the LineExtractor for game-message
        // dispatch (chat + system-log subscriptions wired in its ctor) and
        // borrows the same wire sender so a fired trigger's Response goes
        // through the canonical SendUserInput path.
        AppServices.Current.Triggers.AttachLineExtractor(Lines);
        AppServices.Current.Triggers.SetSender(AppServices.Current.EngineGate.Named(userCommandSend, "Trigger"));

        // Remote-command engine borrows the same wire-sender so a handler's
        // ctx.Reply(text) routes through SendUserInput exactly like a
        // typed command would. The PartyEssentialHandlers also need their
        // own copy for the @party <sub> → local-command relay (uses the
        // wire-sender directly, bypassing ctx.Reply).
        AppServices.Current.RemoteCommands.SetWireSender(Named("RemoteCommands"));
        AppServices.Current.PartyEssentials.SetWireSender(Named("PartyEssentials"));
        // Settings → Talk auto-greet — needs the wire-sender to emit
        // greet/look at newly-seen non-party players.
        AppServices.Current.Greet.SetWireSender(Named("Greet"));
        // Settings → Talk reactive-look — needs the wire-sender to emit
        // look-back / look-on-arrival at other players.
        AppServices.Current.PlayerLook.SetWireSender(Named("PlayerLook"));

        // Sysop room-status probe. Deliberately on the gate-wrapped sender that
        // routes through SendUserInput: the parser arms off the outbound bytes,
        // so a probe sent on the raw engine path would come back unrecognised.
        AppServices.Current.SysStatus.SetWireSender(Named("SysStatus"));
        // A user-typed `@timer sync` (instead of the Bosses-tab "Sync Timers…" button)
        // should still surface the responses — auto-open the merge window when we see
        // our own request go out on chat. Same idea for `@roomba sync` (the receiver's
        // adopt window) and for `@loop send yes` (the window in which that player's
        // loop is saved). See OnChatForTransfers.
        AppServices.Current.Chat.EntryClassified += OnChatForTransfers;
        AppServices.Current.Chat.UsersLineSent += OnUsersLineSentForTimerSync;
        // Poller needs the same wire-sender to send @health round-trip
        // requests and the periodic par poll.
        AppServices.Current.PartyPoller.SetWireSender(Named("PartyPoller"));
        // Once-a-day party stats probe sends @level + @version on the same path.
        AppServices.Current.PartyProbe.SetWireSender(Named("PartyProbe"));
        // Ally-drop rescue sends `aid <name>` + `/<given> @health` on the same
        // gate-wrapped path (held while WE are mortally wounded, like every engine).
        AppServices.Current.AllyDropped.SetWireSender(Named("AllyDropped"));
        // Emit @wait when we start resting and @ok when we finish, so
        // the party leader's pause-gate can react.
        AppServices.Current.PartyRest.SetWireSender(Named("PartyRest"));
        AppServices.Current.TooHeavyWait.SetWireSender(Named("TooHeavyWait"));
        // Outbound ailment-sync — the say-announce (".@poisoned" etc.)
        // rides the same engine sender; the @wait/@ok side routes through
        // PartyRest's sender bound just above.
        AppServices.Current.AilmentSync.SetWireSender(Named("AilmentSync"));
        // Auto-Exp-Reset + panic / kill broadcasts go through
        // PartyBroadcaster.
        AppServices.Current.PartyBroadcaster.SetWireSender(Named("PartyBroadcaster"));
        // AutoPartyManager — consumes per-player InviteToPartyIfSeen
        // and JoinPartyIfInvited flags, sends `invite <given>` and
        // `follow <given>` over the wire.
        AppServices.Current.AutoParty.SetWireSender(Named("AutoParty"));
        // HangupHandler — sends the configured GameExitCommand when
        // an authorised sender telepaths @hangup.
        AppServices.Current.Hangup.SetWireSender(Named("Hangup"));
        // RelogHandler — same exit command, but arms RelogSignal so the
        // Disconnected handler forces a reconnect-and-login cycle.
        AppServices.Current.Relog.SetWireSender(Named("Relog"));
        // DivertHandler — repeats incoming telepaths to a target while
        // @divert is active; rides the same gate-wrapped pipeline.
        AppServices.Current.Divert.SetWireSender(Named("Divert"));
        // Follower-side @comeback. Telepaths @comeback to the leader
        // when the party walks off without us; rides the same
        // gate-wrapped pipeline.
        AppServices.Current.ComebackRequest.SetWireSender(Named("ComebackRequest"));
        AppServices.Current.LeaderBossTravel.SetWireSender(Named("LeaderBossTravel"));
        // Follower-side reconnect auto-rejoin — telepaths @comeback + @invite
        // to re-form the party after a drop; same gate-wrapped pipeline.
        AppServices.Current.PartyRejoin.SetWireSender(Named("PartyRejoin"));
        // Leader-side reconnect recovery — telepaths the @where probe + decline
        // @forget when re-collecting a returning member; same pipeline.
        AppServices.Current.PartyComeback.SetWireSender(Named("PartyComeback"));
        // Death-recovery auto-grab (`get`) + auto-equip (`wear` / `hold`)
        // ride the same gate-wrapped pipeline as the other engines.
        AppServices.Current.DeathRecovery.SetWireSender(Named("DeathRecovery"));
        // Auto-train is the ONE automation allowed to type on the `train stats`
        // screen — entering the form (`train stats`) raises the TrainerScreenGate
        // hold that silences every other engine, so its own `train stats` + each
        // Enter-driven CP keystroke must pierce that hold. Bind it to the raw,
        // un-wrapped SendUserInput (same escape hatch the low-HP hangup uses);
        // ObserveOutbound still fires on this path, so the `train stats` send is
        // what arms the tracker and raises the gate in the first place.
        AppServices.Current.AutoTrain.SetWireSender(AppServices.Current.EngineGate.Named(SendUserInput, "AutoTrain"));
        // It also reads the form it is about to type into: the trained stats and CP
        // Left stand on the terminal grid, which only this session holds.
        AppServices.Current.AutoTrain.SetScreenReader(BuildVisibleScreenText);
        // TrainerWalk sends `train` / `stat` only OUTSIDE the form (bare `train`
        // has no Point Cost Chart, so it never raises MenuOwnsKeyboard; `stat`
        // fires post-training), so it stays on the gate-wrapped pipeline.
        AppServices.Current.TrainerWalk.SetWireSender(Named("TrainerWalk"));
        // Level-up announcer broadcasts "I can now train to level: N" on
        // the configured channel through the same gate-wrapped pipeline.
        AppServices.Current.LevelUp.SetWireSender(Named("LevelUp"));
        // CombatManager sends `attack <target>` on target pick via the
        // same engine-send pipeline; the gate-wrapped sender prevents
        // the swing command from landing mid-password-entry on a stale
        // combat round.
        AppServices.Current.Combat.SetWireSender(Named("Combat"));
        AppServices.Current.Combat.SetWireHoldProbe(() => AppServices.Current.EngineGate.IsLocked);
        // CombatStateTracker sends `break` before releasing the walker when the
        // user toggles auto-attack off mid-fight (CombatSettings.BreakBeforeFleeing).
        AppServices.Current.CombatTracker.SetWireSender(Named("CombatTracker"));
        // Post-death graveyard resync — a CR to re-observe the respawn room if it
        // hasn't landed shortly after death. Wire-send only (not a move), so it
        // rides engineSend even though the death halt holds the movement gate.
        AppServices.Current.PlayerDeathHalt.SetWireSender(Named("PlayerDeathHalt"));
        // HealthManager sends rest / stand / pre- / post-rest commands
        // via the same gate-wrapped engine pipeline.
        AppServices.Current.Health.SetWireSender(Named("Health"));
        // The emergency low-HP hangup is the one Health send that must survive
        // an EngineSendGate hold — a mortally-wounded character can still hang
        // up, and that hold is exactly what a drop raises. Bind it to the raw,
        // un-wrapped SendUserInput so the escape hangup pierces the gate.
        AppServices.Current.Health.SetHangupWireSender(AppServices.Current.EngineGate.Named(SendUserInput, "Health (hang-up)"));
        // After the exit command goes out, close the carrier ourselves rather
        // than waiting on the server to notice — see RequestHangupDisconnect.
        AppServices.Current.Health.SetHangupDisconnect(RequestHangupDisconnect);
        AppServices.Current.MonsterWatch.HoldChanged += () =>
            HangupWatchHoldText = AppServices.Current.MonsterWatch.HoldText ?? string.Empty;
        // The raw, gate-piercing wire for `sys goto` (SysopGotoManager). Sys commands
        // are honoured at any HP — mortally-wounded included — so the jump (and the
        // wimpy escape built on it) must survive the EngineSendGate hold, exactly like
        // the emergency hangup above. Same un-wrapped SendUserInput.
        AppServices.Current.SetRawWireSender(AppServices.Current.EngineGate.Named(SendUserInput, "Client (raw sender)"));
        // CastCoordinator's `c <spell> [target]` emits still respect the
        // suicide-password / trainer-menu lockouts (gate-wrapped), but ride
        // the raw send, NOT engineSend — engineSend funnels through
        // SendUserInput, whose OutboundCastObserver exists to catch a
        // hand-typed cast and would otherwise mistake the combat engine's own
        // announce for one (see SendEngineWireRaw).
        AppServices.Current.Cast.SetWireSender(
            AppServices.Current.EngineGate.Named(
                AppServices.Current.EngineGate.WrapEngineSender(SendEngineWireRaw), "Cast"));
        // Mana-regen reroll engine's `abil 145` query + cooldown-bypassing
        // recast ride the same gate-wrapped pipeline as the cast engine.
        AppServices.Current.SetEngineWireSender(Named("Client (shared sender)"));
        // Item-cast buff sequencer's wield/use/wield commands ride the
        // same gate-wrapped pipeline as the other engines.
        AppServices.Current.ItemCast.SetWireSender(Named("ItemCast"));
        // EquipmentManager's paced `wear` commands ride the same
        // gate-wrapped pipeline (@equip <set> set-apply).
        AppServices.Current.Equipment.SetWireSender(Named("Equipment"));
        // CashManager's `get all <coin>` commands ride the gate-wrapped
        // pipeline like the other engines.
        AppServices.Current.Cash.SetWireSender(Named("Cash"));
        // AutoGetItemsManager's `get <name>` commands ride the same
        // gate-wrapped pipeline.
        AppServices.Current.AutoGetItems.SetWireSender(Named("AutoGetItems"));
        // The hang-up item check's own two sends, `i` and a room redisplay; its
        // gets go out through the auto-get engine above.
        AppServices.Current.HangupItems.SetWireSender(Named("HangupItems"));
        // The loot-automation engines — AutoDiscard's `drop`, AutoBuy's `buy`,
        // AutoSell's `sell` — all ride the same gate-wrapped pipeline.
        AppServices.Current.AutoDiscard.SetWireSender(Named("AutoDiscard"));
        AppServices.Current.AutoBuy.SetWireSender(Named("AutoBuy"));
        AppServices.Current.AutoSell.SetWireSender(Named("AutoSell"));
        // Base auto-search — the per-room `sea` rides the same gate-wrapped
        // pipeline so it can't land mid-password-prompt.
        AppServices.Current.AutoSearch.SetWireSender(Named("AutoSearch"));
        // Let the route picker's "Search en route" card flip the master Auto-Search
        // toggle on for its leg (and back off when the counter lands) through the same
        // observable the toolbar/menu drive, so the badge + persistence stay in sync.
        AppServices.Current.SetAutoSearchEnabled = on => IsAutoSearchActive = on;
        // Active auto-light — the route's `use <light>` / `rem <old>` swap
        // rides the same gate-wrapped pipeline.
        AppServices.Current.AutoLightProvisioner.SetWireSender(Named("AutoLightProvisioner"));
        // Auto-light provisioning detour — the `buy <light>` at the shop rides
        // the same gate-wrapped pipeline.
        AppServices.Current.AutoLightShopRouter.SetWireSender(Named("AutoLightShopRouter"));
        // Hazard-buff provisioning — the `use <waterskin>` that raises a
        // checkspell hazard buff rides the same gate-wrapped pipeline.
        AppServices.Current.AutoHazardCounterProvisioner.SetWireSender(Named("AutoHazardCounterProvisioner"));
        // Shop-source routing — the `buy <item>` at the detour shop
        // rides the same gate-wrapped pipeline.
        AppServices.Current.PathItemShopRouter.SetWireSender(Named("PathItemShopRouter"));
        // Give-source routing — the `ask <npc> <keyword>` / room-CMD give
        // command at the detour giver rides the same gate-wrapped pipeline.
        AppServices.Current.PathItemGiveRouter.SetWireSender(Named("PathItemGiveRouter"));
        // Summon-source routing — the room-CMD summon keyword and the post-kill
        // `look` re-survey ride the same gate-wrapped pipeline.
        AppServices.Current.PathItemSummonRouter.SetWireSender(Named("PathItemSummonRouter"));
        // Party-inventory deferral — the `@party give` / `@do give`
        // hand-off rides the same gate-wrapped pipeline. The @have probe
        // itself broadcasts through the already-bound PartyBroadcaster.
        AppServices.Current.PartyPathItemGate.SetWireSender(Named("PartyPathItemGate"));
        // StashRoomManager's `hide N <coin>` commands ride the same
        // gate-wrapped pipeline.
        AppServices.Current.Stash.SetWireSender(Named("Stash"));
        // Roomba Mode's `get`/`drop` sort-phase commands ride the same
        // gate-wrapped pipeline.
        AppServices.Current.GhSweep.SetWireSender(Named("Roomba"));
        // Auto-deposit reroute's bank `dep` command rides the same
        // gate-wrapped pipeline.
        AppServices.Current.AutoDeposit.SetWireSender(Named("AutoDeposit"));
        // @drop-all / @deposit-all / @share emit drop / dep / with / give
        // on the same gate-wrapped pipeline.
        AppServices.Current.InventoryAction.SetWireSender(Named("InventoryAction"));
        // @heal receive side polls `par` so CastingDirector heals the
        // requester off fresh party HP.
        AppServices.Current.Heal.SetWireSender(Named("Heal"));
        // StealthManager's auto-sneak / auto-hide commands ride the same gate-wrapped pipeline.
        AppServices.Current.Stealth.SetWireSender(Named("Stealth"));
        // @do passthrough — gate-wrapped because a malicious caller's
        // payload shouldn't be able to land mid-suicide-password entry.
        AppServices.Current.Do.SetWireSender(Named("Do"));
        // @trap auto-disarm — gate-wrapped (same reason).
        AppServices.Current.TrapDisarm.SetWireSender(Named("TrapDisarm"));
        // Party trap delegation — `look <name>` race probes + `.@trap <dir>`
        // say broadcasts ride the same gate-wrapped pipeline.
        AppServices.Current.TrapDelegation.SetWireSender(Named("TrapDelegation"));
        // DoorOpenManager wire-sender — gate-wrapped so the bash/pick
        // sequence can't land in a password-entry prompt. Walker
        // routes door exits through Door.Enqueue at step-send time.
        AppServices.Current.Door.SetWireSender(Named("Door"));
        // LeaderDoorAssistManager — same gate-wrapped sender so helping the
        // leader force a door can't fire mid-password-prompt.
        AppServices.Current.LeaderDoorAssist.SetWireSender(Named("LeaderDoorAssist"));
        AppServices.Current.DetourCombat.HoldChanged += OnDetourCombatHold;
        AppServices.Current.PyramidSolver.RunThroughChanged += OnPyramidRunThrough;
        AppServices.Current.Walker.SetDoorEnqueuer(AppServices.Current.Door.Enqueue);
        AppServices.Current.Walker.SetDoorStopper(AppServices.Current.Door.StopAll);
        // Loop runner shares the same door FSM so a closed door mid-circuit is
        // bashed/picked/keyed instead of detaching the whole lap.
        AppServices.Current.LoopRunner.SetDoorEnqueuer(AppServices.Current.Door.Enqueue);
        AppServices.Current.LoopRunner.SetDoorStopper(AppServices.Current.Door.StopAll);
        // HiddenExitRevealManager — same gate-wrapped sender so the
        // sea loop can't land mid-password-prompt. Walker routes
        // SearchableHidden exits here.
        AppServices.Current.HiddenSearch.SetWireSender(Named("HiddenSearch"));
        AppServices.Current.Walker.SetHiddenSearchEnqueuer(AppServices.Current.HiddenSearch.Enqueue);
        AppServices.Current.Walker.SetHiddenSearchStopper(AppServices.Current.HiddenSearch.StopAll);
        // Loop runner shares the same reveal FSM so a hidden exit mid-circuit is
        // uncovered with sea <dir> instead of failing the lap.
        AppServices.Current.LoopRunner.SetHiddenSearchEnqueuer(AppServices.Current.HiddenSearch.Enqueue);
        AppServices.Current.LoopRunner.SetHiddenSearchStopper(AppServices.Current.HiddenSearch.StopAll);
        // WinchManager — same gate-wrapped sender so the pull + poll `l` can't land
        // mid-password-prompt. Both engines route a winch gate here so it's pulled +
        // waited-open instead of firing the move blindly into a still-closed gate.
        AppServices.Current.Winch.SetWireSender(Named("Winch"));
        AppServices.Current.Walker.SetWinchEnqueuer(AppServices.Current.Winch.Enqueue);
        AppServices.Current.Walker.SetWinchStopper(AppServices.Current.Winch.StopAll);
        AppServices.Current.LoopRunner.SetWinchEnqueuer(AppServices.Current.Winch.Enqueue);
        AppServices.Current.LoopRunner.SetWinchStopper(AppServices.Current.Winch.StopAll);
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
        AppServices.Current.LoopRunner.SetItemNameResolver(id => AppServices.Current.ItemNames.GetName(id));
        AppServices.Current.Walker.SetPartyLeaderCheck(isLeaderWithFollowers);
        AppServices.Current.LoopRunner.SetTeleportResolver(teleportResolver);
        AppServices.Current.LoopRunner.SetPartyLeaderCheck(isLeaderWithFollowers);
        // Recover Now is a walk the user asked for: a walk-to to the death room, on
        // the route cards like any other.
        AppServices.Current.DeathRecovery.SetDemandedWalk(room =>
        {
            _ = WalkForRecoverNowAsync(room);
            return true;
        });

        // The cards closed without a pick: no walk went out, so the Recover Now that
        // asked for one isn't left waiting for an arrival. Pressed twice, two sets of
        // cards are up for the one request: it is forgotten only when the last of
        // them has closed and none was picked from. (Nor then, if an earlier press's
        // walk is still under way: the recovery manager checks that.)
        async Task WalkForRecoverNowAsync(Game.Map.RoomKey room)
        {
            if (_recoverNowCardsOpen++ == 0) _recoverNowWalkPicked = false;
            bool picked = await MudPlay.ViewModels.Navigation.RouteChoicePrompt.WalkAsync(AppServices.Current, room);
            _recoverNowWalkPicked |= picked;
            if (--_recoverNowCardsOpen == 0 && !_recoverNowWalkPicked)
                AppServices.Current.DeathRecovery.RecoverNowCardsClosed();
        }
        // A party-splitting CMD teleport (chime-style, Darkwood's `go vortex`)
        // dissolves the follow chain even though the `.@party <kw>` relay sent
        // everyone through, so the party must be re-invited on landing. Both
        // movement engines route that reform through AutoPartyManager, which holds
        // the movement gate until the group re-forms. The "X is no longer following
        // you." lines the split prints are that expected drop, not members left
        // behind — without the note the leader walked back through the teleport to
        // fetch them.
        AppServices.Current.ApplyRunStartMode = ApplyRunStartMode;
        AppServices.Current.NoteUserStoppedRun = () => OnUserStopped("stopped by the user", endsTheRun: true);
        // A remote @stop arrives inside the remote-command pump, and the reset can
        // write to the terminal, so it is posted.
        AppServices.Current.NoteRemoteStop = () => Dispatcher.UIThread.Post(
            () => OnUserStopped("@stop", endsTheRun: false));
        // A change of game data stops a run as the Stop button does, and can arrive
        // inside the line pump: posted for the same reason.
        AppServices.Current.NoteGameDataStop = reason => Dispatcher.UIThread.Post(
            () => OnUserStopped(reason, endsTheRun: true));
        AppServices.Current.Walker.SetPartySplitHandler(OnLeaderPartySplitTeleport);
        AppServices.Current.LoopRunner.SetPartySplitHandler(OnLeaderPartySplitTeleport);

        static void OnLeaderPartySplitTeleport()
        {
            AppServices.Current.PartyComeback.NoteOwnTeleport();
            AppServices.Current.AutoParty.NotePartySplitTeleport();
        }
        // Stopping the walk mid-reform drops the re-invite hold so the user
        // isn't pinned by the PartyInvite gate until the group rejoins.
        AppServices.Current.Walker.SetPartySplitAbortHandler(
            () => AppServices.Current.AutoParty.AbortReformWaits("walk stopped"));
        // Walker + loop runner — gate-wrapped so a long walk doesn't
        // blast moves through a password-entry prompt. Their sends are route steps,
        // which sneak keeping never takes for later.
        Action<byte[]> routeSend = AppServices.Current.EngineGate.WrapEngineSender(SendUserInput, routeSteps: true);
        AppServices.Current.Walker.SetWireSender(AppServices.Current.EngineGate.Named(routeSend, "Walker"));
        AppServices.Current.LoopRunner.SetWireSender(AppServices.Current.EngineGate.Named(routeSend, "LoopRunner"));
        // Paradigm position resolver — its `rm` re-sync ride the same
        // gate-wrapped pipeline so it can't land mid-password-prompt.
        AppServices.Current.ParadigmResync.SetWireSender(Named("ParadigmResync"));
        // The tracker asks the game to show the room when a step through a
        // teleporting exit wasn't followed by the displays the game owes it: a bare
        // Enter, through the same gate.
        // Not with the master switch off: the tracker goes on reading the rooms it
        // is shown, but asks for none.
        AppServices.Current.RoomTracker.SetRoomRedisplay(() =>
        {
            if (AppServices.Current.AutoModeController.Blocks("Position fixes")) return;
            engineSend(System.Text.Encoding.Latin1.GetBytes("\r"));
        });
        // Teleport-maze solver — its look-peeks + reshuffle moves ride the same
        // gate-wrapped pipeline. The RoomParsed feed that drives its relocalize
        // is subscribed below beside the RoomDisplayParser.
        AppServices.Current.MazeSolver.SetWireSender(Named("MazeSolver"));
        // Pyramid solver — its climb moves + door/sphinx commands ride the same
        // gate-wrapped pipeline.
        AppServices.Current.PyramidSolver.SetWireSender(Named("PyramidSolver"));
        // Summon-on-death CR recheck rides the same gate-wrapped pipeline.
        AppServices.Current.SummonSettle.SetWireSender(Named("SummonSettle"));
        // Recovery gate's tier-3 look-sweep rides the same gate-wrapped pipeline
        // so its `look <dir>` peeks can't land mid-password-prompt.
        AppServices.Current.Recovery.SetWireSender(Named("Recovery"));
        // SuicideHandler — bypasses the engine gate because it OWNS
        // the suicide flow (and needs its `suicide` + password sends
        // to land even while SuicidePasswordTracker has the gate
        // locked for the password-prompt phase). Uses the raw
        // SendUserInput, not the wrapped engineSend.
        AppServices.Current.Suicide.SetWireSender(AppServices.Current.EngineGate.Named(SendUserInput, "Suicide"));
        // MainMenuEntryAutomation — same sender; armed below when
        // LoginAutomator's LoggedIntoGame fires (only point in the
        // session where the entry command is allowed to auto-fire).
        AppServices.Current.MainMenuEntry.SetWireSender(Named("MainMenuEntry"));
        // StatlineReconciler — gate-wrapped so a `set statline` resend can't
        // land mid-password-prompt. Armed unconditionally on every connect
        // (below) so the live statline reconciles to the editor regardless of
        // whether login was automated.
        AppServices.Current.StatlineReconcile.SetWireSender(Named("StatlineReconcile"));

        // CleanupLogoutOrchestrator — proactive log-off on the nightly
        // shutdown warning. Safe = room clear of killable NPCs AND not
        // mid-combat (the two signals CombatStateTracker owns). Gated on
        // the active BBS's ReconnectAfterCleanup toggle (same switch the
        // predictive reconnect scheduler reads), and uses the user-
        // initiated disconnect path so the reconnect still arms afterward.
        AppServices.Current.CleanupLogout.SetWireSender(Named("CleanupLogout"));
        AppServices.Current.CleanupLogout.SetSafePredicate(
            () => !AppServices.Current.CombatTracker.HasEngageableHostiles
                && !AppServices.Current.PlayerState.InCombat);
        AppServices.Current.CleanupLogout.SetConnectedCheck(() => IsConnected);
        // Pause the between-round cast loop while the link is down (its heartbeat
        // driver keeps firing regardless of connection); the buff timers already
        // freeze/resume across the gap.
        AppServices.Current.CastDirector.SetConnectedGate(() => IsConnected);
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

        // A --profile launch argument that didn't resolve (typo / ambiguous bare
        // name), or a startup profile that failed to load, surfaces its reason on
        // the terminal at startup — dismiss the splash
        // so it's visible — instead of quietly coming up on a blank profile. Posted
        // so it lands after the ctor unwinds and the window is showing.
        if (StartupOptions.ProfileNotice is { } profileNotice)
        {
            StartupOptions.ProfileNotice = null;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (ShowSplash) ShowSplash = false;
                WriteTerminalStatus($"[{profileNotice}]", TerminalStatusKind.Error);
            });
        }

        // A self-update that failed relaunches this (old) build right after leaving
        // its step log beside the executable; say so, and where the log is, so the
        // user can attach it to a bug report.
        if (Services.Update.UpdatePlatform.RecentFailedUpdateLog(TimeSpan.FromMinutes(10)) is { } failedUpdateLog)
        {
            AppServices.Current.Log.Warn("Update", $"the last update failed and was rolled back — see '{failedUpdateLog}'");
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (ShowSplash) ShowSplash = false;
                WriteTerminalStatus(
                    $"[The update failed and this version was restored. What went wrong is in {failedUpdateLog} — please attach it to a bug report.]",
                    TerminalStatusKind.Error);
            });
        }

        // Startup auto-connect. The profile the app launched with — a --profile
        // argument (how Profile Management's "Load" spawns an instance) or the
        // auto-load-last profile — is loaded by AppServices BEFORE this ctor
        // subscribes OnProfileLoadedForConnect above, so that load's ProfileLoaded
        // fired with no handler attached and Settings → General "Auto-connect when
        // profile loads" was never honoured (report paradigm-20260913-005647).
        // Replay the decision once now that the handler + BBS bindings are live.
        // OnProfileLoadedForConnect self-guards — a blank draft, no resolved BBS, no
        // host/port, Auto-connect off, or an already-live connection all early-return
        // — so this is a no-op except the intended case: launching straight into a
        // profile with Auto-connect on. Posted so it lands after the window shows.
        if (AppServices.Current.Profile.Current is { } startupProfile)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => OnProfileLoadedForConnect(startupProfile));

        // Title-bar update banner. Seeded now because the startup check may have
        // already landed, and followed after that — AvailabilityChanged fires from
        // wherever the HTTP await resumed, so the handler marshals.
        SyncUpdateMarquee();
        AppServices.Current.Update.AvailabilityChanged += () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(SyncUpdateMarquee);
    }

    // Enabler for view-handled toolbar buttons (no CommandName) — a command-less
    // Button renders disabled, so these carry an always-executable no-op while
    // their real action runs in MainWindow's Click / PointerReleased handlers.
    private static readonly ICommand ToolbarNoOpCommand = new RelayCommand(() => { });

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

            // A catalogue entry with no CommandName is a VIEW-handled button — its
            // action lives in MainWindow's Click / PointerReleased handlers (the
            // Combat-Profile menu fly-out). Give it an always-executable no-op so
            // Avalonia doesn't render the command-less button as disabled.
            ICommand? command = string.IsNullOrEmpty(entry.CommandName)
                ? ToolbarNoOpCommand
                : GetType().GetProperty(entry.CommandName)?.GetValue(this) as ICommand;

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

            List<ToolbarMenuAction>? subActions = entry.SubActions?
                .Select(a => new ToolbarMenuAction(a.Label,
                    GetType().GetProperty(a.CommandName)?.GetValue(this) as ICommand, a.Tooltip,
                    a.Key, a.Parameter))
                .ToList();

            string actionId = entry.ActionId;
            ToolbarButtonItem row = new(
                ToolbarItemKind.Button, entry.ActionId,
                label: entry.Label,
                iconResourceKey: entry.IconResourceKey,
                tooltip: tooltip,
                command: command,
                alternateIconResourceKey: alt,
                subActions: subActions,
                savedChoice: AppServices.Current.Profile.Current?.ToolbarSplitChoices is { } picks
                             && picks.TryGetValue(actionId, out string? pick) ? pick : null,
                saveChoice: key => SaveToolbarSplitChoice(actionId, key));

            ApplyToolbarRowState(row);
            ToolbarItems.Add(row);
        }
    }

    // Run whatever a split toolbar button is set to (its ▾ pick) — for a keybind on that
    // button, so the hotkey does what the button does. Reads the live row when the button
    // is on the toolbar, else the saved pick, else the button's first option.
    public void RunToolbarPick(string actionId)
    {
        if (ToolbarItems.FirstOrDefault(r => r.ActionId == actionId && r.HasSubActions) is { Command: { } live })
        {
            live.Execute(null);
            return;
        }
        if (ToolbarItemCatalogue.Find(actionId) is not { SubActions: { Count: > 0 } subs }) return;
        string? saved = AppServices.Current.Profile.Current?.ToolbarSplitChoices is { } picks
                        && picks.TryGetValue(actionId, out string? k) ? k : null;
        ToolbarItemCatalogue.SubAction pick = subs.FirstOrDefault(s => s.Key == saved) ?? subs[0];
        if (GetType().GetProperty(pick.CommandName)?.GetValue(this) is ICommand cmd && cmd.CanExecute(pick.Parameter))
            cmd.Execute(pick.Parameter);
    }

    // A split toolbar button's ▾ pick, saved to the character so the button keeps it.
    private static void SaveToolbarSplitChoice(string actionId, string key)
    {
        ProfileService profiles = AppServices.Current.Profile;
        if (profiles.Current is not { } profile) return;
        (profile.ToolbarSplitChoices ??= new())[actionId] = key;
        profiles.Save();
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
         && e.PropertyName != nameof(IsAutoHealActive)
         && e.PropertyName != nameof(IsAutoRestActive)
         && e.PropertyName != nameof(IsAutoBlessActive)
         && e.PropertyName != nameof(IsAutoLightActive)
         && e.PropertyName != nameof(IsAutoGetItemsActive)
         && e.PropertyName != nameof(IsAutoGetCashActive)
         && e.PropertyName != nameof(IsAutoSneakActive)
         && e.PropertyName != nameof(IsAutoHideActive)
         && e.PropertyName != nameof(IsAutoSearchActive)
         && e.PropertyName != nameof(IsDisableHangupsActive)
         && e.PropertyName != nameof(IsSprintModeActive)
         && e.PropertyName != nameof(CombatProfileCycleLabel)
         && e.PropertyName != nameof(IsMasterSwitchOn)) return;

        foreach (ToolbarButtonItem row in ToolbarItems)
        {
            if (row.IsButton) ApplyToolbarRowState(row);
        }
    }

    private void ApplyToolbarRowState(ToolbarButtonItem row)
    {
        switch (row.ActionId)
        {
            // Both profile buttons show the active profile number ("P1") as their label.
            case "CycleCombatProfile":
            case "CombatProfileMenu":
                row.BadgeText = CombatProfileCycleLabel;
                break;
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
            case "ToggleSprintMode":
                row.IsActive = IsSprintModeActive;
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
            case "ToggleAutoHeal":
                row.IsActive = IsAutoHealActive;
                break;
            case "ToggleAutoRest":
                row.IsActive = IsAutoRestActive;
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
                // Depressed = the master switch is on.
                row.IsActive = IsMasterSwitchOn;
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

    // The monster last looked at, whose running HP estimate the status bar follows,
    // and the range its look gave.
    private string? _lookedMonster;
    private string _lookedBand = "";

    private void OnMonsterLookTarget(Game.MonsterLookObserved obs)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Game.Combat.MonsterHpRead? read = AppServices.Current.MonsterHpEstimates.OnLook(obs.Name, obs.Estimate);
            _lookedMonster = obs.Name;
            _lookedBand = obs.Estimate.Describe();
            TargetHpText = read is { } r ? $"TGT HP: {_lookedBand} [~{r.BestGuess}]" : $"TGT HP: {_lookedBand}";
            // Also drop a yellow line into the terminal scrollback so the estimate
            // is logged, not only shown in the status bar's target item (which the
            // user may have taken off the bar), unless Settings → Other has the
            // line switched off: max HP, the wound
            // band's shorthand and range, and the best guess from the damage seen and
            // regen — "[large orc: 100 HP, Crit: 20-29, ~24]".
            if (!AppServices.Current.Resolver
                    .Resolve<Models.Profile.OtherSettings>("Other").PrintMonsterHpOnLook)
                return;
            string wound = $"{Game.MonsterLookParser.WoundShorthand(obs.Wound)}: {_lookedBand}";
            string line = read is { } l
                ? $"{obs.Name}: {l.MaxHp} HP, {wound}, ~{l.BestGuess}"
                : $"{obs.Name}: {wound}";
            WriteTerminalStatus($"[{line}]", TerminalStatusKind.Notice);
        });

    // Damage landed on the looked-at monster: the bracketed best guess follows it; the
    // range stays the look's.
    private void OnMonsterEstimateChanged(string name)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_lookedMonster is not { } looked || !looked.Equals(name, StringComparison.OrdinalIgnoreCase)) return;
            if (AppServices.Current.MonsterHpEstimates.Estimate(looked) is int e)
                TargetHpText = $"TGT HP: {_lookedBand} [~{e}]";
        });

    private void OnMonsterDied(Game.Combat.MonsterDeathEvent _)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            TargetHpText = "";
            _lookedMonster = null;
        });

    private void OnQuestAvailable(string questName)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            WriteTerminalStatus($"[{questName} Quest is Now Available]", TerminalStatusKind.Notice));

    private void OnEquipSlotBlocked(Game.Inventory.EquipmentManager.EquipBlock block)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            WriteTerminalStatus(
                $"[{block.ItemName} skipped, unable to wear — adjust set to correct]",
                TerminalStatusKind.Notice));

    private void OnGhSweepCompleted(Game.Map.GhSweepReport report)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            WriteTerminalStatus(report.StoppedBecause is { } why
                ? $"[Ganghouse roomba stopped: {why}]"
                : "[Ganghouse roomba complete]", TerminalStatusKind.Notice));

    private static void LogSlowLine(LineExtractor.EmittedLine line, TimeSpan took)
        => AppServices.Current.Log.Warn("Lines", LineExtractor.SlowLineNotice(line, took));

    // The login sequence sends stat / exp / inventory (and the user's who, etc.) right
    // after entering the realm. Wait for that to finish rendering, then dump the quests
    // this character can now begin so the lines land as a clean block at the end of login.
    private static async Task AnnounceAvailableQuestsAfterLoginAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        // Run the sync-then-announce on the UI thread: the flag probe collects on the
        // line-emit (UI) thread and paces its sends off the dispatcher, and when the
        // opt-in sync is on it must finish marking completes BEFORE the availability dump
        // so a freshly-completed quest doesn't announce as still-available.
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            AppServices svc = AppServices.Current;
            if (svc.QuestFlagSync.EnabledForCurrentProfile)
            {
                try { await svc.QuestFlagSync.SyncAsync().ConfigureAwait(true); }
                catch (Exception ex)
                {
                    svc.Log.Warn("QuestFlags", $"login quest-flag sync failed: {ex.Message}");
                }
            }
            svc.QuestAvailability.AnnounceLoginAvailable();
            // Read held Paradigm transport-token charges (no-op off Paradigm / with
            // no tokens held). Fire-and-forget so it doesn't delay the announce; by
            // this point (5s after login) the inventory `i` has been parsed.
            _ = svc.Tokens.RefreshAsync();
        });
    }

    private void OnRecoveryFailed(Game.Map.RecoveryFailedEvent e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowLostRecoveryDialogAsync(e));

    private async void ShowLostRecoveryDialogAsync(Game.Map.RecoveryFailedEvent e)
    {
        var vm = new ViewModels.Navigation.LostRecoveryDialogViewModel(e.EngineName, e.Detail, e.LastGoodRoom);
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

    private void OnRoomNameLearned(Game.Map.NameLearnedEvent e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => PromptForLearnedNameAsync(e));

    private async void PromptForLearnedNameAsync(Game.Map.NameLearnedEvent e)
    {
        // Asked once per name per room in a session, and not at all with no realm
        // or set to keep the name under: the store decides, and remembers.
        Game.Map.LearnedRoomNameStore names = AppServices.Current.LearnedRoomNames;
        if (!names.AskAbout(e.Key, e.ObservedName)) return;
        // Where it was seen, for a Save that comes after either has changed.
        string? realm = names.RealmFolder;
        string? set = AppServices.Current.RoomGraph.ActiveSet;

        // The name on file for the room, when the game has printed another since.
        string? keptBefore = names.NameFor(e.Key);
        var vm = new ViewModels.RoomNameLearnedDialogViewModel(e.Key, e.ObservedName, keptBefore);
        bool keep = await AppServices.Current.Dialogs
            .OpenWindowAsync<ViewModels.RoomNameLearnedDialogViewModel, bool>(vm);
        if (!keep) return;

        Game.Map.LearnedRoomNameStore.KeepResult result =
            names.Keep(e.Key, e.ObservedName, seenOnRealm: realm, seenOnSet: set);
        // Save was pressed and nothing reached the file: say so where the user is
        // looking, the program log having the detail. Posted, as every status line
        // is that may be written while the emulator is being fed.
        if (Game.Map.LearnedRoomNameStore.WhyNotStored(result) is { } why)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => WriteTerminalStatus(
                $"[Room name for {e.Key} not saved: {why}]", TerminalStatusKind.Notice));
    }

    private void RefreshLocationSlot()
    {
        LocationText = BuildLocationText();
        RefreshExpRate();
    }

    private static string BuildLocationText()
    {
        // Loop status takes over the location slot while a loop is active. While
        // the runner is still walking to the loop's entry (Approaching), the slot
        // shows the same walk-to readout as a plain goto — current room,
        // destination, remaining steps — because we haven't begun the loop yet.
        // Only once the circle is actually running does it collapse to the lap and
        // the step within it — "lap 12 · step 36 of 60", the step being the next to
        // send, as the Navigation window counts it.
        Game.Map.LoopRunner runner = AppServices.Current.LoopRunner;
        if (runner.State != Game.Map.LoopState.Idle && runner.CurrentLoop is not null)
        {
            if (runner.State == Game.Map.LoopState.Approaching) return BuildWalkLocationText();
            string lap = $"lap {runner.CompletedLaps + 1}";
            return LoopStepText(runner) is { Length: > 0 } step ? $"{lap} · {step}" : lap;
        }

        // A plain walk-to (goto / favourite) gets the same C/D/Steps readout as a
        // loop approach. Checked before the tracker fallback below, which would
        // otherwise win (the current room is non-null throughout a walk).
        if (AppServices.Current.Walker.State is Game.Map.WalkState.Walking or Game.Map.WalkState.Paused
            && AppServices.Current.Walker.Destination is not null)
            return BuildWalkLocationText();

        Game.Map.RoomState state = AppServices.Current.RoomTracker.State;
        Game.Map.Room? room = state.CurrentRoom;
        // Without an active game-data set the room graph is empty, so the
        // tracker can never locate us — every state reads "Lost", which
        // misleads a fresh profile into thinking something broke. Surface
        // the real cause instead and point the user at the fix. Clears
        // automatically once a set loads (RoomGraph.GraphReloaded → here).
        if (room is null && AppServices.Current.RoomGraph.RoomCount == 0)
            return "Load a game data set to use navigation";
        // The map/room number, not the room name. Names run long ("Newhaven,
        // Arena", …) and were overflowing the narrow status slot; the key is
        // always short and identifies the room just as well for a player watching
        // the strip.
        if (room is not null) return room.Key.ToString();
        return state.Confidence switch
        {
            Game.Map.RoomConfidence.Pending        => "Pending move…",
            Game.Map.RoomConfidence.Lost           => "Lost — pick a room on the map",
            Game.Map.RoomConfidence.PendingRespawn => "Awaiting respawn…",
            _                                      => "Unknown location",
        };
    }

    // "step 36 of 60" — where the running loop is in its circle; empty while it
    // has no steps laid out.
    public static string LoopStepText(Game.Map.LoopRunner runner)
    {
        int total = runner.StepCount;
        return total <= 0 ? string.Empty : $"step {Math.Min(total, runner.CurrentIndex + 1)} of {total}";
    }

    // "C: M/R  D: M/R  Steps: N" — the walk-to readout shared by a plain goto and a
    // loop's approach leg. Remaining steps = total path length minus the next-step
    // index (CurrentStepIndex), clamped at 0.
    private static string BuildWalkLocationText()
    {
        Game.Map.AutoWalkManager walker = AppServices.Current.Walker;
        Game.Map.Room? here = AppServices.Current.RoomTracker.State.CurrentRoom;
        string cur = here is { } r ? r.Key.ToString() : "?";
        string dest = walker.Destination is { } d ? d.ToString() : "?";
        int remaining = Math.Max(0, walker.StepCount - walker.CurrentStepIndex);
        return $"C: {cur} D: {dest} Steps: {remaining}";
    }

    // The session exp rate ("1.2M/hr") and the time to the next level ("TNL: 42m
    // (+0.35 lvls)"), each its own status-bar item. TNL uses the SAME estimate as the
    // Session Stats "time to next level" readout (banked-aware target level +
    // game-data exp chart) so the two never drift — an earlier stat-line "exp to
    // next" ÷ rate ignored banked levels and desynced from Session Stats. The
    // "(+N.NN lvls)" is the banked-levels ratio (whole banked + progress toward the
    // next) — since TNL targets the next UNearned level, that bracket says how far
    // past your trained level your exp already sits, so a big TNL time on a low
    // level reads clearly. Empty until there is a rate to estimate from.
    private void RefreshExpRate()
    {
        double xpHr = AppServices.Current.SessionActivity.Snapshot().ExperiencePerHour;
        ExpRateText = $"{Game.Combat.RateText.Compact(xpHr)}/hr";
        if (xpHr <= 0)
        {
            TimeToLevelText = string.Empty;
            return;
        }
        (Game.Calculators.TimeToLevelEstimator.Result est, TimeSpan? remaining) = AppServices.Current.SelfTimeToLevel();
        if (remaining is not { } tnl)
        {
            TimeToLevelText = string.Empty;
            return;
        }
        string time = tnl <= TimeSpan.Zero ? "ready"
            : Game.Calculators.ExperienceTableCalculator.FormatTimeToLevel(tnl);
        TimeToLevelText = $"TNL: {time} (+{Game.Calculators.TrainBudgetCalculator.FormatBankableLevels(est.BankableLevelsFractional)} lvls)";
    }

    // Repaint the status-bar tick countdowns. Source-of-truth:
    // Game.TickEngine.TimeToNextCombatTick for combat; Game.RegenTracker
    // for HP / MA. HP and MA show the natural cycle by default and append
    // the bonus cycle (rest / medi) when the player is resting or
    // meditating — the two cycles have independent anchors and can be
    // desynced.
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
            // hangup flag is on. Programmatic disconnects (the
            // auto-reconnect cycle, remote @hangup, the emergency
            // hangup) never come through here, so they bypass this
            // prompt.
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
            WriteTerminalStatus("[NO BBS SELECTED — OPEN SETTINGS → BBS + DISPLAY, PICK ONE, AND SAVE.]",
                                TerminalStatusKind.Error);
            AppServices.Current.Log.Warn("Connect", "No active BBS — open Settings → BBS + Display first.");
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
        automator.StepSkipped += reason => AppServices.Current.Log.Info("LoginAuto",
            $"'{bbsName}': {reason} — type it at the prompt; the rest of the logon steps carry on");
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

        string when = ArmTimedReconnect(delay);
        AppServices.Current.Log.Info("Cleanup",
            $"Reconnect scheduled at {when} — warning observed at " +
            $"{warning.ObservedAt.LocalDateTime:HH:mm:ss} with {warning.MinutesRemaining}m remaining " +
            $"+ {bbs.CleanupPeriodMinutes}m cleanup period.");
    }

    // The PvP response hung up with "Re-connect after PvP" on: dial back in once
    // the wait is over. Shares the cleanup reconnect's timer, countdown and cancel
    // (pressing Connect), since only one timed dial-back can be pending.
    private void SchedulePvpReconnect(TimeSpan delay, bool enterRealm)
    {
        // A hang-up normally leaves the next login at the menu for the user to
        // enter by hand. With "enter the realm" set, this dial-back is the user's
        // standing instruction to go back in, so it enters like any other login.
        string when = ArmTimedReconnect(
            delay, beforeDial: enterRealm ? AppServices.Current.HangupSignal.AllowNextEntry : null);
        AppServices.Current.Log.Info("PvP",
            $"Reconnect scheduled at {when}, {delay.TotalMinutes:0} min after the PvP hang-up"
            + (enterRealm ? "." : "; it stops at the menu."));
    }

    // Arm the one-shot timed dial-back and announce it. Returns the dial time.
    private string ArmTimedReconnect(TimeSpan delay, Action? beforeDial = null)
    {
        CancelCleanupReconnect(reason: null);
        _cleanupReconnectCts = new CancellationTokenSource();
        NotifyReconnectPendingChanged();
        CancellationToken token = _cleanupReconnectCts.Token;

        string when = (DateTimeOffset.Now + delay).LocalDateTime.ToString("HH:mm:ss");
        int minutes = (int)delay.TotalMinutes;
        int seconds = delay.Seconds;
        WriteTerminalStatus(
            $"[AUTO-RECONNECT ARMED — DIALING AT {when} (IN {minutes}m{seconds:D2}s). PRESS CONNECT TO CANCEL.]",
            TerminalStatusKind.Notice);

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
                beforeDial?.Invoke();
                _ = ConnectWithRetriesAsync();
            });
        }, TaskScheduler.Default);
        return when;
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
    private void ScheduleRelogReconnect(string reason)
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
            $"{reason} — reconnecting in {FormatDelay(delay)}.");
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
    // caller (a File → Recent pick, a Profile Management swap) doesn't
    // block on the retry loop. The connect path already self-marshals UI updates and never
    // throws to the caller.
    private async void OnProfileLoadedForConnect(Models.Profile.CharacterProfile _)
    {
        ClearQuickConnect();
        SyncProfileMenuState();
        RefreshBbsBindings();

        if (IsConnected || IsConnecting) return;

        // A --reconnect relaunch (the self-updater restoring a session that was live
        // when the update ran) dials regardless of the toggle. Consumed here so only
        // the startup profile load honours it — a later profile load in the same
        // session falls back to the user's actual Auto-connect preference.
        bool forced = StartupOptions.ForceReconnect;
        StartupOptions.ForceReconnect = false;

        Models.Profile.GeneralSettings general =
            AppServices.Current.Resolver.Resolve<Models.Profile.GeneralSettings>("General");
        if (!general.AutoConnect && !forced) return;

        // No usable BBS resolves → silently skip. Explicit Connect prints
        // the "no BBS selected" guidance; the auto-connect path doesn't
        // need to be noisy about something the user didn't manually trigger.
        if (ResolveActiveBbs() is null) return;
        if (string.IsNullOrWhiteSpace(Host) || Port <= 0) return;

        AppServices.Current.Log.Info("Connect", forced
            ? "Auto-connect on profile load — restoring the session the update restarted."
            : "Auto-connect on profile load — General → Auto-connect is on.");
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
                using PerformanceMonitor.Scope feed = AppServices.Current.Performance.Measure("terminal feed");
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
                if (_soundOwesReconnect)
                {
                    _soundOwesReconnect = false;
                    AppServices.Current.Sounds.Fire(Game.Sounds.SoundCues.Reconnected);
                }
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
                // A new link starts at the board's login: nothing from here is
                // copied into a bug report until the game is entered.
                AppServices.Current.InGameCapture.NotifyConnected();
                // The Simulator checks a kept ranking against the character as this
                // connection shows it: the last one's `stat` and inventory are history.
                AppServices.Current.ForgetSimulatorCharacterReads();
                // On a realm that drops items for a hang-up, holds movement from here
                // until what was held before the link dropped has been checked for.
                AppServices.Current.HangupItems.NoteConnected();
                // Same lifecycle signal to the default-task runner — it resets its
                // per-connection latches and fires the configured startup task on
                // the first in-game prompt with a known room.
                AppServices.Current.DefaultTaskRunner.NotifyConnected();
                // Reconcile the statline to the editor on EVERY connect —
                // unconditional, unlike the auto-login-gated engines. Resets
                // the Synced latch + retry counter; the actual resend (if any)
                // is mismatch-gated and fires off a run of unmatched in-game
                // prompts.
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
                // reconnect (Settings → General). Only on a reconnect — a
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
                // General "re-enable on reconnect" flags). A
                // failed connect attempt fires Disconnected with
                // wasConnected=false and must NOT arm this.
                if (wasConnected) _hadDisconnectThisSession = true;
                // Ahead of everything below that tears the fight down (the PvP
                // fight, combat, the room's occupants): the list of what was held
                // also says what fight the character left the game in.
                // Guarded: ending a check clears a movement gate, whose subscribers run
                // here and now, and a throw this early would skip the whole handler,
                // the reconnect scheduling included.
                if (wasConnected)
                {
                    try { AppServices.Current.HangupItems.NoteLinkDropping(); }
                    catch (Exception ex)
                    {
                        AppServices.Current.Log.Warn(Game.Inventory.HangupItemRecheck.LogCategory,
                            $"Couldn't note the link dropping ({ex.GetType().Name}: {ex.Message}).");
                    }
                }
                // Snapshot the followers we were leading while PartyState is still
                // intact — par reconciliation after the reconnect wipes the roster,
                // so the leader-side reform must capture them now. Only on a real
                // in-game drop; a failed redial (wasConnected=false) keeps the good
                // snapshot from the preceding drop rather than overwriting it empty.
                if (wasConnected) AppServices.Current.PartyReform.NoteDisconnected();
                // The follower's reconnect @comeback is skipped after too long a drop.
                if (wasConnected) AppServices.Current.PartyRejoin.NoteDisconnected();
                // That reconnect request is the only one for this split: the
                // left-behind watch stands down until we follow again.
                if (wasConnected) AppServices.Current.ComebackRequest.NoteDisconnected();
                // Leaving the game takes us off every member list we were still on.
                if (wasConnected) AppServices.Current.Party.NoteLinkDropped();
                // A member's @comeback kept for the master switch names a place
                // from the stay that just ended.
                AppServices.Current.RemoteCommands.DropHeldComebacks("disconnected");
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
                // A prompt the link cut off part-way must not be completed by the
                // next connection's first bytes.
                AppServices.Current.PromptScanner.Reset();
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
                // A running/paused/recovering loop has no way to know the
                // connection died mid-recovery — stop it cleanly instead of
                // leaving a stale wait to misread whatever the reconnect
                // redisplays. Resumes itself on the first in-game prompt after
                // reconnect (see LoopRunner.NotifyDisconnected).
                AppServices.Current.LoopRunner.NotifyDisconnected();
                // A move still awaiting its room display will never get one now.
                AppServices.Current.RoomTracker.NoteConnectionLost();
                // A Stock spill sweep would time out stop after stop against the dead
                // wire, or set off again on reconnect.
                AppServices.Current.DeathRecovery.NotifyDisconnected();
                AppServices.Current.CorpseHandoff.NotifyDisconnected();
                // A trade agreed to on a route card is not made after a drop: where
                // the character stands and how far the trade had got can't be vouched
                // for. A walk still standing fetches as before, and trades nothing.
                AppServices.Current.EndTradeSession();
                // The party is rebuilt after a drop, and what its members hold by
                // then is not known: a hand-over remembered from before isn't kept.
                if (wasConnected) AppServices.Current.PartyHandOvers.Clear("disconnected");
                // A fight with a player can't outlive the connection; left standing
                // it would keep the combat engine stood down after the reconnect.
                AppServices.Current.PvpFight.Stop("disconnected", resume: false, connected: false);

                // Drop per-session condition state so a fresh login starts clean: any
                // non-auto-clearing condition (no AppliedEndsWith) must not survive the
                // disconnect. Buff-duration timers are handled below, AFTER the cause is
                // known — an unexpected drop freezes them (resume on reconnect) rather
                // than clearing, so a brief drop doesn't lose the recast clock.
                AppServices.Current.Conditions.ClearAll("disconnect");
                AppServices.Current.ManaRegen.Reset();
                // Combat's mid-round state (current target, an in-flight attack
                // spell, the between-round-cast resume latch) can't survive the
                // drop either — see CombatManager.OnDisconnected for why a stale
                // latch here silently stops the character from ever resuming the
                // fight after reconnect (report paradigm-20260827-203548).
                AppServices.Current.Combat.OnDisconnected();
                // A flee does not run on offline. The loop was stopped above and
                // restarts by its own reconnect resume; a walk lives through the
                // drop, so one the flee had paused is handed back at the first
                // game prompt of the next stay.
                AppServices.Current.Health.EndFlee("disconnected", handBackLiveEngine: true);
                // The replies to discards still on the wire went with the connection.
                // Hides a full room refused stay held: the pack is as it was.
                AppServices.Current.AutoDiscard.Reset("disconnected", keepHeld: true);
                // Likewise the replies to inventory reads still out: counted, they
                // would make the first open after the reconnect pass over its own.
                AppServices.Current.ChestOpens.NoteDisconnected();
                // Whoever was in the room is not known to be there when we are
                // back: left standing, the roster is re-issued while offline (the
                // Auto-Combat toggle, the PvP fight stopped above) and acted on.
                AppServices.Current.RoomClassifier.NoteGameLeft();
                // A drop with a Hangup monster in sight turns the watch off for the
                // first minute back in the game.
                AppServices.Current.MonsterWatch.NoteDisconnected();

                // Categorise: if the user clicked Disconnect, the flag was
                // set in DisconnectInternalAsync. Otherwise check for a
                // pending intentional-hangup signal (raised by
                // HangupHandler or HealthManager's emergency hangup right
                // before the exit command lands). Only
                // when neither user-flag nor hangup-signal is set do we
                // fall back to server-side classification (carrier vs
                // keepalive-timeout) based on wire-silence duration.
                // Read on every disconnect, so one sent before a drop of another
                // kind can't be left standing for the next.
                SentExitCommand.Intent sentExit = AppServices.Current.SentExit.Consume();
                string relogReason = "Remote @relog";
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
                // The engines' own hang-ups and @relog send the realm's exit command
                // too, which may be one of these two; their signals above say what
                // was meant, so the command itself only decides when it went out
                // on its own.
                else if (sentExit == SentExitCommand.Intent.Relog)
                {
                    _lastDisconnectCause = DisconnectCause.RelogInitiated;
                    relogReason = $"'{SentExitCommand.RelogCommand}' sent";
                }
                else if (sentExit == SentExitCommand.Intent.StayDown)
                {
                    _lastDisconnectCause = DisconnectCause.UserInitiated;
                    AppServices.Current.Log.Info("Reconnect",
                        $"'{SentExitCommand.StayDownCommand}' sent — staying disconnected, no redial.");
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

                // Buff timers: ANY disconnect stops them (buffs stay on the character
                // while it is out of the game), and the first in-game prompt lets each
                // run on with exactly the time it had left, however long the gap. The
                // cast pass is latched off with them, in the game or not, so nothing is
                // cast at the board's login. What they had left is saved with the
                // character by BuffTimerKeeper, when InGameCapture is told below.
                AppServices.Current.CastDirector.PauseBuffTimers();
                // A hazard counter's buff is on the character through the drop too.
                // Its clock stops here and runs on, with what it had left, from the
                // same first in-game prompt.
                AppServices.Current.AutoHazardCounterProvisioner.Pause();
                // The reconnect's splash and login menu ride the same line extractor.
                AppServices.Current.MessageCandidateWatcher.NotifyLeftGame();
                AppServices.Current.InGameCapture.NotifyDisconnected();
                // The list of what was held was saved at the top of this handler,
                // or by the line above when the link was never counted as up; only
                // after that may a check still under way be dropped.
                AppServices.Current.HangupItems.NoteDisconnected();
                // A drop we didn't ask for plays the Disconnected sound, and arms the
                // Reconnected one for when the link comes back.
                if (_lastDisconnectCause is DisconnectCause.CarrierLost or DisconnectCause.NoResponse)
                {
                    _soundOwesReconnect = true;
                    AppServices.Current.Sounds.Fire(Game.Sounds.SoundCues.Disconnected);
                }

                // A remote @relog forces the dial-back unconditionally —
                // the sender explicitly asked to relog, so we bypass the
                // cleanup/reactive scheduling (which gates on per-BBS
                // toggles) entirely.
                // Read whatever the disconnect was, so a delay armed for a hang-up
                // that never dropped the line can't attach itself to a later one.
                (TimeSpan Delay, bool EnterRealm)? pvpReconnect = AppServices.Current.PvpResponse.TakeReconnect();
                if (_lastDisconnectCause == DisconnectCause.RelogInitiated)
                {
                    ScheduleRelogReconnect(relogReason);
                }
                else if (_lastDisconnectCause == DisconnectCause.HangupInitiated && pvpReconnect is { } pvp)
                {
                    // The Hangup-monster watch's minute off is for a reconnect the
                    // user makes. This one dials and enters on its own, and would
                    // stand the character beside the monster with nobody watching.
                    AppServices.Current.MonsterWatch.CancelHold();
                    SchedulePvpReconnect(pvp.Delay, pvp.EnterRealm);
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
        // route them into LogService at Debug severity so the Program Log
        // shows them when its Debug box is ticked.
        client.Log += msg => AppServices.Current.Log.Debug("Telnet", msg);

        return client;
    }

    private enum TerminalStatusKind { Notice, Error }

    // Write a single bracketed status line into the terminal canvas itself
    // (in addition to LogService). Mirrors the classic-BBS-client cadence
    // the user expects: "[CONNECTING TO: …]" / "[DISCONNECTED FROM: …]" /
    // etc. Coloured via inline ANSI SGR so the emulator does the painting.
    // Drops a coloured status line into the terminal scrollback by FEEDING it back
    // through the emulator. Re-entrancy hazard: any caller firing from inside the
    // emulator's message pump (a MessageRouter / ChatRouter / remote-command
    // handler) must Dispatcher.UIThread.Post this — a synchronous call there
    // re-enters Emulator.Feed while it's mid-parse and crashes the client.
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

    private void OnStatlineFlagChanged()
    {
        string? notice = AppServices.Current.StatlineReconcile.FlagNotice;
        if (notice is null)
        {
            StatlineMismatchTip = string.Empty;
            return;
        }
        if (IsStatlineMismatchVisible) return;
        WriteTerminalStatus(notice, TerminalStatusKind.Error);
        StatlineMismatchTip = notice.Trim('[', ']', ' ')
            + "\n\nClick to open Settings -> Statline. This warning clears as soon as a prompt matches.";
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

    // An @suicide with a stored password answers the game's password prompt, which
    // has no line end until answered and so is only ever seen on the cursor's row.
    private void OnScreenUpdatedForSuicidePrompt()
    {
        Game.Remote.SuicideHandler suicide = AppServices.Current.Suicide;
        if (!suicide.AwaitingPasswordPrompt) return;
        suicide.ObserveCursorRow(CursorRowText());
    }

    // What the terminal shows on the row the cursor is on: where a prompt that is
    // waiting for an answer stands.
    private string CursorRowText()
    {
        TerminalScreen screen = Emulator.Screen;
        int row = Math.Clamp(screen.CursorY, 0, screen.Rows - 1);
        return TranscriptSnapshot.RowText(screen.Row(row));
    }

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

    // Where the line SendUserInput is carrying came from, when it is the user's
    // but not from the terminal's keyboard: an alias's step, a window, the local
    // API. Null for the keyboard. Only the sent-command trail reads it; to every
    // other observer these lines are the user's own, as before.
    private SentCommandTrail.Origin? _userLineOrigin;

    // True while a line typed in the Conversation window's input is going out.
    private bool _conversationLine;

    // SendUserInput for a line the user sent some other way than by typing it at
    // the terminal, so the trail can say which.
    private void SendUserInputAs(SentCommandTrail.Origin origin, byte[] data)
    {
        SentCommandTrail.Origin? outer = _userLineOrigin;
        _userLineOrigin = origin;
        try { SendUserInput(data); }
        finally { _userLineOrigin = outer; }
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
        // Whether the line is the user's own (typed, an alias, a window, the local
        // API) and not an engine's decides what a typed `suicide` covers.
        EngineSendGate sendGate = AppServices.Current.EngineGate;
        AppServices.Current.SuicidePassword.ObserveOutbound(data,
            usersOwn: !sendGate.SendingClientCommand && sendGate.CurrentSender is null);
        // Sent-command trail. A line a wrapped sender is putting out is already
        // in it (the gate records each command once); what reaches here unwrapped
        // is typed, or a named sender going round the gate.
        // An @suicide waiting to answer the game's password prompt: any other
        // line that goes out first is what the game takes as the password, so
        // the stored one must not follow it.
        AppServices.Current.Suicide.NoteLineSent(data);
        // Keys from the terminal: a whole line at once, or one key at a time while
        // a form owns the keyboard. The trail makes lines of them either way, and
        // tells the chat log each one as the user's own send (TypedLineEntered).
        bool typedAtTerminal = !sendGate.SendingClientCommand && sendGate.CurrentSender is null && _userLineOrigin is null;
        if (!sendGate.SendingClientCommand)
        {
            if (sendGate.CurrentSender is { } pastGate)
                AppServices.Current.SentCommands.Record(
                    System.Text.Encoding.Latin1.GetString(data).TrimEnd('\r', '\n', '\0'),
                    SentCommandTrail.Origin.Engine, pastGate, SentCommandTrail.Outcome.Sent, "goes round the send gate");
            else if (_userLineOrigin is { } origin)
                AppServices.Current.SentCommands.ObserveUserLine(data, origin);
            else
                AppServices.Current.SentCommands.ObserveTyped(data);
        }
        // A `recover corpse <name> <password>` for another player arms the record of
        // what comes out of their corpse, when the user typed it: at the terminal or
        // in the Conversation window's input.
        bool typedInConversation = _conversationLine && _userLineOrigin == SentCommandTrail.Origin.Window
            && !sendGate.SendingClientCommand && sendGate.CurrentSender is null;
        AppServices.Current.CorpseHandoff.ObserveOutbound(data, usersOwn: typedAtTerminal || typedInConversation);
        // Stat-screen parser — gates on outbound `stat` so chat lines
        // containing "Strength: 60" or similar can't bleed into the
        // PlayerStats snapshot.
        AppServices.Current.Stats.ObserveOutbound(data);
        // Movement observer — peeks for `look <dir>` (so the next room
        // display is dropped as a peek) and text-exit movement verbs
        // (so the step is captured for replay-from-last-Confirmed).
        AppServices.Current.OutboundMovement.ObserveOutbound(data);
        // Between a death and the first display of the room it left the character
        // in, a room command that moves the character means the next display is of
        // some other room: nothing is learned from that death.
        AppServices.Current.ArrivalLearner.ObserveOutbound(data);
        // Cast observer — a manually-typed cast-code either overrides the round (a
        // combat spell — the user's attack; the engine holds its auto attack) or arms
        // the between-round-cast resume (an in-between heal/buff so a still-alive target
        // is re-attacked at once instead of a round late).
        // A loop waypoint's command block comes through here too, and the user typed
        // none of it: a cast or attack in it claims no round and overrides nothing.
        bool typed = !AppServices.Current.LoopRunner.SendingOwnCommand;
        if (typed) AppServices.Current.OutboundCast.ObserveOutbound(data);
        // Attack observer — a manually-typed physical attack verb (a/aa/bash/smash/bs/…)
        // is a user override: the engine holds its own swing until the next round.
        if (typed) AppServices.Current.OutboundAttack.ObserveOutbound(data);
        // Gear observer — the user's own eq / wear / wield / rem mid-fight arms a
        // re-attack for the *Combat Off* it draws, typed or sent by a macro, trigger or
        // event of theirs. An engine's gear commands come through here as well (the
        // Equipment Manager, an item cast, corpse recovery), each with its own
        // re-attack after the swap, and are left to it.
        if (typed && AppServices.Current.EngineGate.SendingUsersOwnCommand)
            AppServices.Current.OutboundGear.ObserveOutbound(data);
        // Break observer — the user's own `break` holds the combat engine's attack on
        // the monster it was fighting, and their own attack lets it go: typed, or
        // sent by a macro, trigger or event of theirs. An engine's break or attack is
        // neither, and the raw wire's lines (`sys goto` sends a break ahead of a
        // jump) are an engine's although nothing wraps them. A line a party member
        // relayed with `@do` or `@party` counts with the user's (user, 2026-10-10)
        // and carries their name.
        string? relayedFor =
            AppServices.Current.Do.SendingFor is { } doSender ? $"{doSender}'s @do"
            : AppServices.Current.PartyEssentials.RelayingFor is { } partySender ? $"{partySender}'s @party"
            : null;
        if (typed && !AppServices.Current.SendingEngineRawCommand
            && (AppServices.Current.EngineGate.SendingUsersOwnCommand || relayedFor is not null))
            AppServices.Current.OutboundBreak.ObserveOutbound(data, relayedFor);
        // Left-behind watch — a typed `leave` is the user leaving the party, and a
        // typed room command can draw an exit's refusal; neither is a follower the
        // leader walked off without. Typed lines only, as for gear.
        if (typed && !AppServices.Current.EngineGate.SendingClientCommand)
            AppServices.Current.ComebackRequest.ObserveOutbound(data);
        // Sneak — a typed command that ends a sneak (search, gear, a door…) marks it
        // broken, the same as an engine send through the gate. Short lines only: a
        // command, not a paste. A line a wrapped sender is putting out is the gate's
        // to report, once it has gone; reporting it here as well judged every client
        // command twice.
        if (data.Length is > 0 and <= 128 && !AppServices.Current.EngineGate.SendingClientCommand)
        {
            string sent = System.Text.Encoding.Latin1.GetString(data).TrimEnd('\r', '\n', '\0');
            AppServices.Current.NoteSentForSneak(sent);
            // A room command that casts a dispel ends a tracked hazard buff, judged
            // at the same point and for the same reason: once per command.
            AppServices.Current.NoteSentForRoomCommand(sent);
        }
        // Chat router — every whole line on its way out, so an outgoing telepath's
        // conversation entry has its text, and with whose send it was: one the
        // client sends by itself or for another player (a relay-back, a `@do`, a
        // `@party` relay, a sender going round the gate) is told from the user's
        // own (a window's button, a macro, a trigger, an event, an alias) when its
        // confirmation comes, or when a request is seen to come back on a channel.
        // What was typed at the terminal has been told already, line by line, from
        // the keys themselves (above): these bytes may be one key of a line.
        if (!typedAtTerminal)
            AppServices.Current.Chat.ObserveOutbound(data,
                usersOwn: AppServices.Current.EngineGate.SendingUsersOwnCommand
                    && sendGate.CurrentSender is null
                    && AppServices.Current.Do.SendingFor is null
                    && AppServices.Current.PartyEssentials.RelayingFor is null);
        // Auto-party — every outbound `invite X` (whoever sent it) starts X's invite
        // cooldown, so two re-invite paths firing together send one invite.
        AppServices.Current.AutoParty.ObserveOutbound(data);
        // Sysop room-status parser — arms only on an outbound `sys st`. The gate
        // is a security control here, not noise suppression: the block it parses
        // drives a programmatic SetLocated, so an always-on match would let any
        // line on our screen relocate the character.
        AppServices.Current.SysRoomStatus.ObserveOutbound(data);
        // Unrecognized-line watcher — remember the command briefly so its server
        // echo isn't staged as an unknown candidate line.
        AppServices.Current.MessageCandidateWatcher.ObserveOutbound(data);
        // Token tracker — a `use <token>` here triggers a re-look to reconcile its
        // remaining charges (Paradigm-gated inside the observer).
        AppServices.Current.Tokens.ObserveOutbound(data);
        // Limited-use item charges — arms on an outbound `look <item>` so the reply's
        // "Uses remaining: N" is captured (tokens' login looks included).
        AppServices.Current.ItemCharges.ObserveOutbound(data);
        // Stock use-counting — counts an outbound `use <item>` for a limited-use item
        // (stock has no charge line; no-op on Paradigm, which reads the look reply).
        AppServices.Current.ItemUseCounts.ObserveOutbound(data);
        // Chest Offload — a typed `open <chest>` is tracked like the window's own Open.
        AppServices.Current.OutboundOpen.ObserveOutbound(data);
        // A board log-off the player sent (`;o`, `=x`) decides what the drop that
        // follows it means: come straight back, or stay off.
        AppServices.Current.SentExit.ObserveOutbound(data);
        AppServices.Current.Telepaths.Send(data);
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
        // The one observer an engine send still feeds. It doesn't INTERPRET the send
        // (the reason the rest are skipped) — it only remembers the text briefly so
        // the server's echo of it isn't staged as an unrecognized line. Without this,
        // an engine-issued cast echoed a truncated command back ("swan", "tige") and
        // the capture queue filled with the client's own output.
        AppServices.Current.MessageCandidateWatcher.ObserveOutbound(data);
        AppServices.Current.Telepaths.Send(data);
    }

    // The socket write behind TelepathPacer — everything that reaches the wire from
    // SendUserInput / SendEngineWireRaw ends here, telepaths just a little later.
    private void WriteToWire(byte[] data)
    {
        TelnetClient? t = _telnet;
        if (t is null) return;
        // A command going out is when the prompt is certain to be on the cursor's row:
        // the scanner checks it against Settings -> Statline (its input runs on the UI
        // thread, the pacer may not).
        if (data.Length > 0 && data[^1] is (byte)'\r' or (byte)'\n')
        {
            if (Dispatcher.UIThread.CheckAccess()) AppServices.Current.PromptScanner.NoteCommandSent();
            else Dispatcher.UIThread.Post(AppServices.Current.PromptScanner.NoteCommandSent);
        }
        _ = FireSendAsync(t, data);
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

    // Auto-open the boss-timer merge window when the user sends `@timer sync` by hand
    // (rather than via the Bosses-tab "Sync Timers…" button) — otherwise nothing is
    // collecting and the responders' `@timerdata` replies vanish, the "I got a string
    // back but nothing happened" report. Only our own outbound request should trigger
    // it: TelepathOutgoing is always ours, a Local "You say" line has a null speaker
    // (the self-say form), and a gang or broadcast line a board shows us back carries
    // our own name, so an inbound request we're merely responding to is excluded. On
    // a board that does not show us our own line on a channel, a request typed there
    // isn't caught; that path uses the button.
    //
    // The same watcher serves the other two transfers. Our own `@roomba sync` opens
    // the receiver's adopt window, so the responders' `@roombadata` replies are
    // accepted (the reply proves they've granted us; the receiver only needs to
    // know we asked). Our own telepathed `@loop send yes` (or `y`) opens the window
    // in which that player's loop is saved: the telepath's echo names them.
    //
    // Each transfer goes by its own channels only (TransferChannels). A request or
    // a yes typed on another one opens nothing, since the data would not be taken
    // there, and the user is told where it does work.
    private void OnChatForTransfers(MudPlay.Game.ChatLogEntry e)
    {
        // Our own outbound request looks different per channel: a directed telepath is its
        // own TelepathOutgoing echo, a say echoes as "You say" (null speaker), and a gang
        // (or say, or broadcast) line on some boards echoes tagged with our character
        // name. An inbound request we'd merely respond to has someone else's name and is
        // excluded.
        if (Game.Remote.TransferChannels.OwnRequest(e, IsSelfName) is not { } asked) return;
        if (!Game.Remote.TransferChannels.Allows(asked.Transfer, asked.Channel))
        {
            string where = Game.Remote.TransferChannels.Refusal(asked.Transfer);
            AppServices.Current.Log.Info("Transfers", $"our own {e.Message.Trim()} went out on {asked.Channel}: {where}; nothing will be taken from it.");
            // Posted: this runs inside the chat pump, and a notice feeds the terminal.
            Dispatcher.UIThread.Post(() => AppServices.Current.WriteTerminalNotice($"[{where}]"));
            return;
        }
        // The broadcast channel is not listened to while its Talk-tab box is ticked,
        // whatever a transfer allows: the answers would not be read.
        if (asked.Channel == Game.Remote.RemoteChannel.Broadcast && AppServices.Current.RemoteCommands.DisableBroadcastChannel)
        {
            AppServices.Current.Log.Info("Transfers", $"our own {e.Message.Trim()} went out on the broadcast channel, which is switched off on the Talk tab; nothing will be taken from it.");
            Dispatcher.UIThread.Post(() => AppServices.Current.WriteTerminalNotice(
                "[the broadcast channel is switched off for @commands: Settings → Talk → Disallow @commands from broadcast]"));
            return;
        }
        // Whose request it was. The client also sends these lines for other
        // players (a `@do /name @roomba sync`, a relay-back): taken as the user's,
        // such a send would have this client accept data nobody here asked for.
        // Asked of the one place that knows, for every transfer and channel: the
        // lines the user sent, as they went out. The line coming back shows only
        // that this client said it.
        bool usersOwn = Game.Remote.TransferChannels.IsUsersOwn(e, asked.Channel, AppServices.Current.Chat);
        switch (asked.Transfer)
        {
            case Game.Remote.Transfer.TimerSync:
                // A telepath's confirmation names the player it reached: the one
                // whose answer is an answer to that request, when the request was
                // the user's (ChatRouter pairs the confirmation with the send
                // addressed to that player, and says whose it was). One the client
                // sent for another player (a relay-back, a `@do`) still opens the
                // window, which says so, and every answer to it is only listed.
                OpenTimerSyncWindow(asked.Channel,
                    asked.Channel == Game.Remote.RemoteChannel.Telepath && usersOwn ? e.Speaker : null, usersOwn);
                break;
            case Game.Remote.Transfer.RoombaSync:
                if (usersOwn) AppServices.Current.RoombaSync.NoteSyncRequested();
                else LogTransferNotTheUsers(e);
                break;
            case Game.Remote.Transfer.LoopSend:
                string[] words = e.Message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length != 3 || !Game.Remote.LoopShareHandler.IsConfirm(words[2]) || e.Speaker is not { Length: > 0 } to)
                    break;
                if (usersOwn) AppServices.Current.LoopShareInbox.NoteSendConfirmed(to);
                else LogTransferNotTheUsers(e);
                break;
        }
    }

    private static void LogTransferNotTheUsers(MudPlay.Game.ChatLogEntry e) => AppServices.Current.Log.Info("Transfers",
        $"our {e.Message.Trim()} seen on {e.Channel} was not a line the user sent in the last few seconds (the client sends such lines for another player's remote command): nothing is taken for it.");

    private static bool IsSelfName(string speaker)
    {
        string? self = AppServices.Current.Party.LocalCharacterName;
        if (string.IsNullOrEmpty(self)) return false;
        static string Given(string n) { int s = n.IndexOf(' '); return s >= 0 ? n[..s] : n; }
        return Given(self).Equals(Given(speaker), StringComparison.OrdinalIgnoreCase);
    }

    // Our kill times as the user's first `@timer sync` with no window open went
    // out, and when. The window its echo opens counts "asked" from then, not from
    // the echo: a kill of ours in between is a change after asking. Kept only as
    // long as an echo can still be paired with the line.
    private (DateTimeOffset At, Dictionary<string, DateTimeOffset?> KillTimes)? _killTimesAtTimerSyncAsked;

    private void OnUsersLineSentForTimerSync(string line)
    {
        if (AppServices.Current.TimerSyncWindowActive
            || !Game.Remote.BossTimerSyncCollector.AsksForTimerSync(
                line, () => AppServices.Current.RemoteCommands.DisableBroadcastChannel))
            return;
        if (FreshKillTimesAtTimerSyncAsked() is not null) return;   // the first request counts
        _killTimesAtTimerSyncAsked = (DateTimeOffset.UtcNow, ViewModels.CharacterWorkshop.BossTimerSyncViewModel.KillTimesNow(
            AppServices.Current.Bosses, AppServices.Current.BossTimers, AppServices.Current.GameData.ActiveRealm));
    }

    private Dictionary<string, DateTimeOffset?>? FreshKillTimesAtTimerSyncAsked() =>
        _killTimesAtTimerSyncAsked is { } taken && DateTimeOffset.UtcNow - taken.At <= Game.ChatRouter.PendingCaptureStaleAfter
            ? taken.KillTimes
            : null;

    // askedOn: the channel our request went out on, the one its answers are taken on.
    // telepathedTo: the player it was telepathed to, when it was.
    // byUser: the request was the user's own, not one the client sent for another
    // player's remote command.
    private async void OpenTimerSyncWindow(Game.Remote.RemoteChannel askedOn, string? telepathedTo, bool byUser)
    {
        // Already collecting: the open window's collector sees the request go out
        // too, and takes answers on its channel as well.
        if (AppServices.Current.TimerSyncWindowActive) return;
        Dictionary<string, DateTimeOffset?>? killTimesWhenAsked = byUser ? FreshKillTimesAtTimerSyncAsked() : null;
        _killTimesAtTimerSyncAsked = null;
        var vm = new ViewModels.CharacterWorkshop.BossTimerSyncViewModel(
            AppServices.Current.Bosses,
            AppServices.Current.BossTimers,
            AppServices.Current.GameData,
            AppServices.Current.Chat,
            AppServices.Current.SendTypedInput,
            preArmedOn: askedOn,
            preArmedTo: telepathedTo,
            preArmedByUser: byUser,
            killTimesWhenAsked: killTimesWhenAsked);
        await AppServices.Current.Dialogs
            .OpenWindowAsync<ViewModels.CharacterWorkshop.BossTimerSyncViewModel, bool>(vm);
    }

    // Convenience: encode a text line (Latin-1 + CRLF) and send it to the
    // server as a typed line, with alias expansion (the Conversation window's
    // input, Reset States' re-poll, the local API). Also scans the typed verb for
    // heal-shaped commands so the regen tracker can gate any HP / MA upticks
    // during the artifact grace window.
    // origin is where the text came from (a window, the local API), for the
    // sent-command trail: nothing typed at the terminal comes this way.
    public void SendUserText(string text, SentCommandTrail.Origin origin)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Rapid-fire multi-command: a typed line carrying the macro
        // separators (';' or '^M') fans out into several wire lines the
        // same way macros / aliases do, so the player can queue commands
        // like "sea n;sea n;n". A line with no separator sends verbatim
        // (untrimmed), preserving prior behavior. Each resulting line is
        // then alias-expanded + sent on its own.
        foreach (string line in MacroStore.SplitTypedInput(text))
            SendOneUserLine(line, origin);
    }

    private void SendOneUserLine(string text, SentCommandTrail.Origin origin)
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
                // What goes out is the alias's text, not what was entered: the
                // trail names it so, wherever the alias was entered.
                SendUserInputAs(SentCommandTrail.Origin.Alias, stepBytes);
            }
            return;
        }

        if (LooksLikeHealShapedCommand(text))
        {
            AppServices.Current.Regen.RecordArtifact();
        }

        byte[] bytes = System.Text.Encoding.Latin1.GetBytes(text + "\r\n");
        SendUserInputAs(origin, bytes);
    }

    // Toolbar "EXP" button — send the in-game "exp" command exactly as if the
    // player typed it (alias expansion + wire send). No-ops harmlessly when
    // disconnected (SendUserInput drops it with no socket).
    [RelayCommand]
    private void SendExp() => SendOneUserLine("exp", SentCommandTrail.Origin.Window);

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
    // Logs/capture-yyyyMMdd-HHmmss.log and receives one line per
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
    //
    // The confirm prompt has to happen HERE, not in the window's Closing handler:
    // desktop.Shutdown() is the forced variant, which still raises Closing but
    // discards the handler's e.Cancel — so the handler's prompt would appear over an
    // app already on its way out, and its Save would never be reached.
    [RelayCommand]
    private async Task QuitAsync()
    {
        if (AppServices.Current.Confirm.Settings.ConfirmExit
            && !await AppServices.Current.Confirm.ConfirmExitAsync())
            return;
        if (!await SettleUnnamedDraftAsync("closing the client")) return;
        ShutdownNow();
    }

    // Close the app after the caller has already settled confirmation. Latches the
    // window's exit-confirmed flag first so its Closing handler skips straight to the
    // profile save instead of popping a prompt nobody can answer. Marshalled because
    // the updater calls this from whatever thread its download continuation ran on.
    private static void ShutdownNow() => Dispatcher.UIThread.Post(() =>
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        if (desktop.MainWindow is MudPlay.Views.MainWindow main)
        {
            main.MarkExitConfirmed();
            // Before the shutdown, not from the main window's Closing handler: the
            // shutdown may close the Settings window first, and its edits go with it.
            (main.DataContext as MainWindowViewModel)?.SavePendingSettingsForExit();
        }
        desktop.Shutdown();
    });

    // The client is exiting with the Settings window open: save its pending edits
    // (SettingsWindowViewModel.ApplyPendingForExit). Run on every exit path, ahead
    // of the profile's own save. A section that fails to save must not stop the exit.
    public void SavePendingSettingsForExit()
    {
        if (_settings?.DataContext is not SettingsWindowViewModel vm) return;
        try { vm.ApplyPendingForExit(); }
        catch (Exception ex)
        {
            AppServices.Current.Log.Error("Settings", $"Saving the open Settings window on exit failed: {ex.Message}");
        }
    }

    // Stand the live session down for the self-updater's restart, and report what the
    // relaunched build has to restore. Called once the new build is staged and
    // verified, so by the time we drop the connection the update is going to happen.
    //
    // The socket is closed through the normal user-initiated path, which stamps the
    // cause and stops the carrier-lost auto-reconnect from racing the shutdown. The
    // profile itself is saved by the window's Closing handler on the way out.
    // The installer awaits this from wherever its download continuation landed, so it
    // hops to the UI thread before touching connection state.
    public Task<Services.Update.UpdateRelaunch> PrepareForUpdateRestartAsync() =>
        Dispatcher.UIThread.InvokeAsync(StandDownForUpdateAsync);

    private async Task<Services.Update.UpdateRelaunch> StandDownForUpdateAsync()
    {
        // The update is going ahead whatever is answered here, so backing out of
        // the question is the same as discarding.
        await SettleUnnamedDraftAsync("restarting for the update");
        bool wasLive = IsConnected || IsConnecting;
        if (IsConnected) await DisconnectInternalAsync();
        else if (IsConnecting) _connectCts?.Cancel();

        ProfileService profiles = AppServices.Current.Profile;
        string? token = profiles.CurrentProfileName is { } name && profiles.CurrentBbsName is { } bbs
            ? $"{bbs}/{name}"
            : null;

        AppServices.Current.Log.Info("Update",
            $"Session stood down for update restart (profile={token ?? "none"}, reconnect={wasLive}).");
        return new Services.Update.UpdateRelaunch(token, wasLive && token is not null);
    }

    // ----- Window raise / close -----------------------------------------

    // Bring an open window forward without ever closing it — for a deep link that just
    // switched it to the section / record asked for.
    private static void RaiseExisting(Avalonia.Controls.Window window) => DialogService.RaiseExisting(window);

    // Every window's menu / hotkey / toolbar entry works the same way: not open → open it;
    // open but buried → bring it to the front; already in front → close it. Closing only
    // when it's in front keeps a re-press from shutting a window the user was hunting for
    // behind another one (or another client). close is the window's own exit — Settings
    // saves first, like its OK button.
    private static void RaiseOrClose(Avalonia.Controls.Window window, Action? close = null)
        => DialogService.RaiseOrClose(window, close);

    // Singleton handle for the live LogPaneWindow — re-opening from menu or
    // toolbar activates the existing window instead of stacking duplicates.
    private LogPaneWindow? _logPane;

    [RelayCommand]
    private void OpenLogPane()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        // A re-press raises the open window, or closes it when it's in front (RaiseOrClose).
        if (_logPane is { } existing)
        {
            RaiseOrClose(existing);
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

        // A re-press raises the open window, or closes it when it's in front (RaiseOrClose).
        if (_backscroll is { } existing)
        {
            RaiseOrClose(existing);
            return;
        }

        Services.DisplayConfig display = AppServices.Current.Display;
        BackscrollViewModel vm = new(
            Emulator, display.BackscrollWheelLines, display.FontFamily, display.FontSize);
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

        BugReportBuilder.BugReportCapture capture;
        try { capture = BugReportBuilder.Capture(svc, Emulator); }
        catch (BugReportBuilder.PrivacyException ex)
        {
            // Nothing is written that couldn't be masked; the user is told why,
            // on the terminal and in the log.
            svc.Log.Error("BugReport", ex.Message);
            Dispatcher.UIThread.Post(() => svc.WriteTerminalNotice(ex.Message));
            return;
        }

        string? description = await svc.Dialogs
            .OpenWindowAsync<BugReportDialogViewModel, string>(new BugReportDialogViewModel());
        if (string.IsNullOrWhiteSpace(description)) return;

        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string path = Path.Combine(desktop, BugReportBuilder.FileName(capture));
            await File.WriteAllTextAsync(path, BugReportBuilder.Render(capture, description));
            svc.Log.Info("BugReport", $"Wrote bug report to {path}");
            Guide.NotifyActionDone(GuideActions.BugReportWritten);
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

        // A re-press raises the open window, or closes it when it's in front (RaiseOrClose).
        if (_conversation is { } existing)
        {
            RaiseOrClose(existing);
            return;
        }

        ConversationWindow window = new()
        {
            DataContext = new ConversationViewModel(
                AppServices.Current.ChatHistory,
                AppServices.Current.CommandHistory,
                text =>
                {
                    _conversationLine = true;
                    try { SendUserText(text, SentCommandTrail.Origin.Window); }
                    finally { _conversationLine = false; }
                },
                ClearChatlog,
                Application.Current,
                AppServices.Current.Resolver.Resolve<Models.Profile.TalkSettings>("Talk"),
                AppServices.Current.Profile,
                AppServices.Current.Display),
        };
        window.Closed += (_, _) => _conversation = null;
        _conversation = window;
        window.Show(main);
    }

    // Singleton handle for the live PartyWindow (see RaiseOrClose).
    private PartyWindow? _partyWindow;

    [RelayCommand]
    private void OpenParty()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_partyWindow is { } existing) { RaiseOrClose(existing); return; }

        PartyWindow window = new()
        {
            DataContext = new PartyViewModel(
                AppServices.Current.PartyState,
                data => SendUserInputAs(SentCommandTrail.Origin.Window, data)),
        };
        window.Closed += (_, _) => _partyWindow = null;
        _partyWindow = window;
        window.Show(main);
    }

    // Singleton handle for the live Buff Watchdog window (see RaiseOrClose).
    private BuffWatchdogWindow? _buffWatchdog;

    [RelayCommand]
    private void OpenBuffWatchdog()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        Guide.NotifyActionDone(GuideActions.BuffWatchdogOpened);

        if (_buffWatchdog is { } existing) { RaiseOrClose(existing); return; }

        BuffWatchdogWindow window = new()
        {
            DataContext = new BuffWatchdogViewModel(),
        };
        window.Closed += (_, _) => _buffWatchdog = null;
        _buffWatchdog = window;
        window.Show(main);
    }

    // Singleton handle for the live Profile Management window (see RaiseOrClose). The VM borrows the current-profile lifecycle (New / Save / Save As
    // / swap) from this VM so the collapsed File menu loses no capability.
    private ProfileManagerWindow? _profileManager;

    [RelayCommand]
    private void OpenProfileManager()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        // Tick the first-run tour's "open Profile Management" line — from here, so
        // it fires however the window was opened (menu, Ctrl+P, or toolbar).
        Guide.NotifyActionDone(GuideActions.ProfileManagement);

        if (_profileManager is { } existing) { RaiseOrClose(existing); return; }

        ProfileManagerWindow window = new()
        {
            DataContext = new ProfileManagerViewModel(
                () => IsDisconnected, SwapToProfileRefAsync, NewProfileAsync, SaveLoadedProfileAsync, SaveProfileAsAsync,
                OpenBbsSettingsFor, NameCurrentDraft),
        };
        window.Closed += (_, _) => _profileManager = null;
        _profileManager = window;
        window.Show(main);
    }

    private SettingsWindow? _settings;

    // ----- Profile file management ----------------------------------------

    // Most-recent-first list of saved profile names. Drives the inline
    // File-menu recent entries (Recent0..Recent9). Rebuilt from
    // GlobalSettings on startup and after every profile save.
    public ObservableCollection<ProfileRef> RecentProfiles { get; } = new();

    // Indexed accessors so the File menu can lay out ten fixed MenuItems
    // instead of a flyout submenu. Avalonia ItemsSource inside MenuItem
    // wraps each item in its own MenuItem, which loses the parent VM as
    // the DataContext (the command resolution via $parent[Window] is
    // fragile across popup ownership). Binding to the parent VM directly
    // sidesteps that entirely.
    //
    // RecentLabel format: "<profile> - <bbs>:<realm>" (or just "<profile>" when
    // no BBS is pinned yet). The menu XAML prepends the slot number /
    // mnemonic "_N)  ". Lets the user disambiguate generically-named
    // profiles by the BBS and realm they play.
    public string? Recent0 => RecentLabel(0);
    public string? Recent1 => RecentLabel(1);
    public string? Recent2 => RecentLabel(2);
    public string? Recent3 => RecentLabel(3);
    public string? Recent4 => RecentLabel(4);
    public string? Recent5 => RecentLabel(5);
    public string? Recent6 => RecentLabel(6);
    public string? Recent7 => RecentLabel(7);
    public string? Recent8 => RecentLabel(8);
    public string? Recent9 => RecentLabel(9);

    // ProfileNameN parallel accessors — the (bbs, char) ref for the click
    // handler. RecentN are display strings only. Ten fixed slots is the ceiling
    // (GlobalSettings.MaxRecentProfilesShown); how many actually populate is
    // capped by RebuildRecentProfiles per the user's "recent profiles shown".
    public ProfileRef? ProfileName0 => RecentProfiles.Count > 0 ? RecentProfiles[0] : null;
    public ProfileRef? ProfileName1 => RecentProfiles.Count > 1 ? RecentProfiles[1] : null;
    public ProfileRef? ProfileName2 => RecentProfiles.Count > 2 ? RecentProfiles[2] : null;
    public ProfileRef? ProfileName3 => RecentProfiles.Count > 3 ? RecentProfiles[3] : null;
    public ProfileRef? ProfileName4 => RecentProfiles.Count > 4 ? RecentProfiles[4] : null;
    public ProfileRef? ProfileName5 => RecentProfiles.Count > 5 ? RecentProfiles[5] : null;
    public ProfileRef? ProfileName6 => RecentProfiles.Count > 6 ? RecentProfiles[6] : null;
    public ProfileRef? ProfileName7 => RecentProfiles.Count > 7 ? RecentProfiles[7] : null;
    public ProfileRef? ProfileName8 => RecentProfiles.Count > 8 ? RecentProfiles[8] : null;
    public ProfileRef? ProfileName9 => RecentProfiles.Count > 9 ? RecentProfiles[9] : null;

    private string? RecentLabel(int index)
    {
        if (index < 0 || index >= RecentProfiles.Count) return null;
        ProfileRef recent = RecentProfiles[index];
        if (string.IsNullOrEmpty(recent.Bbs)) return recent.Name;
        AppServices svc = AppServices.Current;
        string? realm = svc.Bbs.Get(recent.Bbs)?.RealmFor(svc.Profile.RealmOf(recent))?.Name;
        return realm is null ? $"{recent.Name} - {recent.Bbs}" : $"{recent.Name} - {recent.Bbs}:{realm}";
    }

    // True when at least one recent profile is queued — gates the Separator.
    public bool HasRecents => RecentProfiles.Count > 0;

    // True when a named profile is loaded — gates File → Save profile.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveProfileLabel))]
    private bool _hasNamedProfile;

    public string SaveProfileLabel => HasNamedProfile
        ? $"_Save profile  ·  {AppServices.Current.Profile.CurrentProfileName}"
        : "_Save profile…";

    // True while it's safe to swap the active profile — only when the wire
    // is down. Loading a different (or blank) profile mid-session would fire
    // ProfileLoaded and reload every per-character service against the new
    // scope while still connected to the old character's game, desyncing
    // settings / party / game-data state. Gates opening a recent profile;
    // Save doesn't swap the active profile, so it stays available.
    private bool CanSwapProfile => IsDisconnected;

    // File → Save. A named character is saved in place.
    //
    // The default profile is not a character, and Save on it asks for a name (user,
    // 2026-10-12: "if someone hits just save, with the default profile.. it should
    // pop up save as.. so they have a proper named profile"; a new player had
    // played, saved and loaded for days on the unnamed default and had no profile
    // of his own). With no name given nothing is saved: the default profile itself
    // is no longer written (ProfileService.Save).
    [RelayCommand]
    private Task SaveProfileAsync() => SaveLoadedProfileAsync(selectedBbs: null);

    // selectedBbs: the BBS picked in Profile Management, where a default draft is
    // named under it (as its Save As does).
    private async Task SaveLoadedProfileAsync(string? selectedBbs)
    {
        ProfileService profile = AppServices.Current.Profile;
        if (profile.Current is null)
        {
            AppServices.Current.Log.Warn("Profile", "Nothing to save — no profile loaded.");
            return;
        }
        if (profile.CurrentProfileName is { } name)
        {
            profile.Save();
            AppServices.Current.Log.Info("Profile", $"Saved profile '{name}'.");
            return;
        }
        if (BbsToSaveUnder(selectedBbs) is not { } bbs)
        {
            ShowInfoDialog("Save profile",
                "Add a BBS first (Settings → BBS + Display). Profiles are saved under a BBS.");
            return;
        }
        if (await NameAndSaveAsync(bbs,
                $"This profile has no name yet, so it can't be saved. Give it a name to save it as a character "
                + $"on {bbs}.") != DraftNaming.Named)
            AppServices.Current.Log.Info("Profile", "Not saved: the unnamed profile was given no name.");
    }

    // How a request to name the loaded profile ended.
    private enum DraftNaming { Named, Discarded, BackedOut }

    // True while closing the client or loading another profile would lose what an
    // unnamed draft holds.
    public bool UnnamedDraftNeedsSettling => AppServices.Current.Profile.UnnamedDraftHasChanges;

    // The unnamed draft is never written, so before it is left behind (the client
    // closing, another profile loading) the user is asked once: name it, discard
    // it, or stay (user, 2026-10-12: "Ask when closing"). True when the caller may
    // go ahead. A draft nothing was changed on is left without a word.
    public async Task<bool> SettleUnnamedDraftAsync(string before)
    {
        ProfileService profile = AppServices.Current.Profile;
        if (!profile.UnnamedDraftHasChanges) return true;
        // Asked once at a time: a close pressed again from the taskbar while the
        // question is up stacked a second copy of it (user's report, 2026-10-14).
        // The repeat waits on the open question's answer instead.
        if (_settlingDraft is { } open) return await open;
        TaskCompletionSource<bool> answer = new();
        _settlingDraft = answer.Task;
        try
        {
            bool goAhead = await AskAboutTheUnnamedDraftAsync(profile, before);
            answer.SetResult(goAhead);
            return goAhead;
        }
        finally
        {
            _settlingDraft = null;
            answer.TrySetResult(false);
        }
    }

    private Task<bool>? _settlingDraft;

    private async Task<bool> AskAboutTheUnnamedDraftAsync(ProfileService profile, string before)
    {
        DraftNaming outcome;
        if (BbsToSaveUnder(selectedBbs: null) is { } bbs)
            outcome = await NameAndSaveAsync(bbs,
                $"This profile has no name, so what you set up on it is lost by {before}. Give it a name to "
                + $"keep it as a character on {bbs}, or discard it.", offerDiscard: true);
        else
            outcome = await AppServices.Current.Confirm.ConfirmAsync("Unnamed profile",
                $"This profile has no name, and there is no BBS to save a character under, so what you set up on "
                + $"it is lost by {before}. Discard it?", "Discard")
                ? DraftNaming.Discarded : DraftNaming.BackedOut;

        if (outcome == DraftNaming.Discarded)
        {
            profile.DiscardUnnamedDraftChanges();
            AppServices.Current.Log.Info("Profile", $"The unnamed profile's changes were discarded before {before}.");
        }
        return outcome != DraftNaming.BackedOut;
    }

    // Return to the default (blank) profile — the "new blank character" action
    // exposed by the Profile Management window. An outgoing named profile is
    // auto-saved first (inside LoadDefaultProfile); an outgoing unnamed draft with
    // something to keep is asked about. Then Current becomes the Global default.
    // Not a menu/keybind command any more; Profile Management gates it on its own
    // disconnected check.
    private async Task NewProfileAsync(string? onBbs)
    {
        if (!await SettleUnnamedDraftAsync("starting a new one")) return;
        AppServices.Current.Profile.LoadDefaultProfile(onBbs);
        // The active BBS just changed with it — connection info, title, BBS-tier
        // settings and game-data set follow the pin.
        AppServices.Current.Profile.NotifyBbsPinApplied();
        SyncProfileMenuState();
    }

    // "Save as" from the Profile Management window: name the loaded profile (e.g. a
    // fresh {default} draft) and write it under a BBS. Prompts for the name.
    private async Task SaveProfileAsAsync(string? selectedBbs)
    {
        ProfileService profile = AppServices.Current.Profile;
        if (profile.Current is null)
        {
            AppServices.Current.Log.Warn("Profile", "Nothing to save — no profile loaded.");
            return;
        }

        if (BbsToSaveUnder(selectedBbs) is not { } bbs)
        {
            ShowInfoDialog("Save profile",
                "Add a BBS first (Settings → BBS + Display). Profiles are saved under a BBS.");
            return;
        }
        await NameAndSaveAsync(bbs, note: null);
    }

    // Profiles are BBS-scoped. Prefer the explicitly-pinned BBS, but fall back to
    // the active BBS shown in the title bar (ResolveActiveBbs) so a fresh {default}
    // draft can be named against the BBS the user is looking at. Only a truly
    // BBS-less install has nowhere to save. A default draft saves under the BBS
    // picked in Profile Management; a named character stays on its own BBS.
    private string? BbsToSaveUnder(string? selectedBbs)
    {
        ProfileService profile = AppServices.Current.Profile;
        string? bbs = profile.CurrentProfileName is null
            ? selectedBbs ?? profile.CurrentBbsName ?? ResolveActiveBbs()?.Name
            : profile.CurrentBbsName ?? ResolveActiveBbs()?.Name;
        return string.IsNullOrWhiteSpace(bbs) ? null : bbs;
    }

    // Name the loaded unnamed profile, with no question: Profile Management's Add
    // on the unnamed profile, where the name was just typed.
    private void NameCurrentDraft(ProfileRef target)
    {
        AppServices.Current.Profile.SaveAs(target.Bbs, target.Name);
        // As NameAndSaveAsync: SaveAs fires no load, so the chrome is refreshed here.
        RefreshBbsBindings();
        PromoteRecent(target);
        SyncProfileMenuState();
        AppServices.Current.Log.Info("Profile", $"Saved profile '{target.Name}' on '{target.Bbs}'.");
    }

    // Ask for a name and save the loaded profile under it on bbs.
    private async Task<DraftNaming> NameAndSaveAsync(string bbs, string? note, bool offerDiscard = false)
    {
        ProfileService profile = AppServices.Current.Profile;
        MudPlay.ViewModels.Profile.ProfileNameInputDialogViewModel vm = new(
            suggestedName: profile.CurrentProfileName ?? "character",
            exists:        name => profile.Exists(bbs, name),
            note:          note,
            offerDiscard:  offerDiscard);

        string? name = await AppServices.Current.Dialogs.OpenWindowAsync<
            MudPlay.ViewModels.Profile.ProfileNameInputDialogViewModel, string>(vm);
        if (string.IsNullOrWhiteSpace(name))
            return vm.Discarded ? DraftNaming.Discarded : DraftNaming.BackedOut;

        profile.SaveAs(bbs, name);
        // SaveAs changes the loaded profile's identity (CurrentProfileName) but
        // fires no ProfileLoaded/Mutated event, so the title bar + OS process
        // title would otherwise keep showing the pre-save name ({default} for a
        // freshly-named draft). Refresh the chrome explicitly.
        RefreshBbsBindings();
        PromoteRecent(new ProfileRef(bbs, name));
        SyncProfileMenuState();
        AppServices.Current.Log.Info("Profile", $"Saved profile '{name}' on '{bbs}'.");
        return DraftNaming.Named;
    }

    [RelayCommand(CanExecute = nameof(CanSwapProfile))]
    private async Task OpenRecentProfileAsync(ProfileRef? recent)
    {
        if (recent is null) return;
        ProfileService profile = AppServices.Current.Profile;
        if (!profile.Exists(recent.Bbs, recent.Name))
        {
            AppServices.Current.Log.Warn("Profile",
                $"Recent profile '{recent.Name}' on '{recent.Bbs}' no longer exists.");
            // Drop it from the persisted list, not just the in-memory copy —
            // an in-memory-only remove let the dead ref reload from global.json
            // on the next rebuild and reappear in File → Recent.
            PruneMissingRecentProfiles();
            return;
        }
        await SwapToProfileRefAsync(recent);
    }

    // Load a profile by ref, promote it to recent, and sync the profile menu.
    // Shared by File → Recent and the Profile Management window's Swap-to.
    // Deferred off the click call stack: loading a profile repositions/resizes
    // the main window (WindowLayoutStore restores saved bounds on ProfileLoaded);
    // running that synchronously would move the window while the menu popup is
    // still open, stranding the flyout. Posting lets the menu close first.
    //
    // An unnamed draft with something to keep is settled first (name it, discard
    // it, or stay on it).
    private async Task SwapToProfileRefAsync(ProfileRef target)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (!await SettleUnnamedDraftAsync("loading another profile")) return;
        try
        {
            AppServices.Current.Profile.Load(target.Bbs, target.Name);
            PromoteRecent(target);
            SyncProfileMenuState();
        }
        catch (Exception ex)
        {
            AppServices.Current.Log.Error("Profile",
                $"Failed to load '{target.Name}' on '{target.Bbs}': {ex.Message}");
        }
    }

    private void PromoteRecent(ProfileRef profileRef)
    {
        SettingsService settingsSvc = AppServices.Current.Settings;
        GlobalSettings settings = settingsSvc.Current;
        settings.RecentProfiles ??= new();
        settings.RecentProfiles.RemoveAll(r => r == profileRef);
        settings.RecentProfiles.Insert(0, profileRef);
        // Retain up to the hard ceiling regardless of how many the menu shows, so
        // raising "recent profiles shown" reveals more without re-loading them.
        while (settings.RecentProfiles.Count > GlobalSettings.MaxRecentProfilesShown)
            settings.RecentProfiles.RemoveAt(settings.RecentProfiles.Count - 1);
        settings.LastUsedProfile = profileRef;
        settingsSvc.Save();
        RebuildRecentProfiles();
    }

    private bool _pruningRecents;

    private void RebuildRecentProfiles()
    {
        // Drop refs whose on-disk profile is gone (deleted / renamed away) before
        // mirroring — otherwise a stale ref reloads from global.json every rebuild
        // and keeps reappearing in File → Recent.
        PruneMissingRecentProfiles();

        RecentProfiles.Clear();
        GlobalSettings settings = AppServices.Current.Settings.Current;
        if (settings.RecentProfiles is not { } source) return;
        // Show only the configured number (clamped to the retained ceiling); the
        // rest stay on disk for when the user raises the count.
        int shown = Math.Clamp(settings.RecentProfilesShown, 0, GlobalSettings.MaxRecentProfilesShown);
        for (int i = 0; i < source.Count && i < shown; i++) RecentProfiles.Add(source[i]);
    }

    // Remove recent-profile refs pointing at a profile that no longer exists and
    // persist the trimmed list. Save() fires GlobalSettingsChanged synchronously,
    // which re-enters RebuildRecentProfiles → back here; the guard makes that pass
    // a no-op (the list is already clean) so there's no recursion.
    private void PruneMissingRecentProfiles()
    {
        if (_pruningRecents) return;
        if (AppServices.Current.Settings.Current.RecentProfiles is not { Count: > 0 } list) return;
        ProfileService profile = AppServices.Current.Profile;
        int removed = list.RemoveAll(r => !profile.Exists(r.Bbs, r.Name));
        if (removed == 0) return;
        AppServices.Current.Log.Info("Profile",
            $"Pruned {removed} stale recent-profile entr{(removed == 1 ? "y" : "ies")} (profile no longer exists).");
        _pruningRecents = true;
        try { AppServices.Current.Settings.Save(); }
        finally { _pruningRecents = false; }
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

    // View → Events menu entry. Opens the Settings window deep-linked to
    // the Events tab.
    [RelayCommand]
    private void OpenEvents() => OpenSettingsAt("events");

    // Singleton handle to the Player Workshop window (see RaiseOrClose; a deep link
    // switches to the requested section first).
    private Views.CharacterWorkshop.CharacterWorkshopWindow? _workshop;

    // Terminal right-click "Workshop: <tab>" deep-link — opens the Workshop on a
    // section (string param = the WorkshopSectionViewModel.Id; a sub-tab's id opens
    // the tab that holds it).
    [RelayCommand]
    private void OpenWorkshopTab(string? sectionId) => OpenWorkshopAt(sectionId);

    // Terminal right-click "Calculator: <x>" deep-link — opens the Workshop on the
    // Calculators tab and reveals that calculator (param = CalculatorId name).
    [RelayCommand]
    private void OpenWorkshopCalculator(string? calculatorId)
        => OpenWorkshopAt("calculators", calculatorId);

    // Terminal right-click "Settings: <tab>" deep-link — opens the Settings window
    // straight to a section (param = the SettingsSectionViewModel.Id).
    [RelayCommand]
    private void OpenSettingsTab(string? sectionId) => OpenSettingsAt(sectionId);

    // Terminal right-click "Game Data: <table>" deep-link — opens the Game Data
    // Browser on a section (param = the browser section id).
    [RelayCommand]
    private void OpenGameDataSection(string? sectionId) => ShowGameDataBrowser(sectionId);

    private void OpenWorkshopAt(string? sectionId, string? calculatorId = null)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        Guide.NotifyActionDone(GuideActions.WorkshopOpened);

        if (_workshop is { } existing)
        {
            // Already on the section asked for: an ordinary re-press (RaiseOrClose). A
            // deep link elsewhere switches first and only raises. The Workshop shell
            // holds no window-level pending state (each editable section owns its own
            // Save / Apply / Cancel), so closing needs no save.
            if (existing.DataContext is not ViewModels.CharacterWorkshop.CharacterWorkshopViewModel vm
                || calculatorId is null && (sectionId is null || vm.IsShowing(sectionId)))
            {
                RaiseOrClose(existing);
                return;
            }
            if (sectionId is not null) vm.Select(sectionId);
            if (calculatorId is not null
                && vm.SelectedSection is ViewModels.CharacterWorkshop.LazyWorkshopSection { Section: ViewModels.CharacterWorkshop.CalculatorsSectionViewModel calc })
                calc.NavigateToCalculator(calculatorId);
            RaiseExisting(existing);
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

        // Calculator deep-link on a fresh open: the section is already selected
        // via the ctor's initialSectionId; reveal the target calculator (its view
        // builds lazily, so NavigateToCalculator arms a pending-scroll the view
        // honors on first layout).
        if (calculatorId is not null
            && workshopVm.SelectedSection
                is ViewModels.CharacterWorkshop.LazyWorkshopSection { Section: ViewModels.CharacterWorkshop.CalculatorsSectionViewModel calcSection })
        {
            calcSection.NavigateToCalculator(calculatorId);
        }
    }

    // Singleton-ish handle to the Quick Connect window (see RaiseOrClose).
    private QuickConnectWindow? _quickConnect;

    // File → Quick Connect. Modeless dialog; on commit the host/port becomes the connect target.
    [RelayCommand]
    private async Task OpenQuickConnectAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_quickConnect is { } existing) { RaiseOrClose(existing); return; }

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

    // Profile Management's "BBS settings…" / "Realm settings…" — open Settings on the
    // BBS tab with that record selected, so a freshly-added BBS has an obvious
    // path to the host / port it still needs.
    private void OpenBbsSettingsFor(string bbsName, string? realmName) => OpenSettingsAt("bbs", bbsName, realmName);

    private void OpenSettingsAt(string? sectionId, string? bbsName = null, string? realmName = null)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        Guide.NotifyActionDone(GuideActions.SettingsOpened);

        // Already on the section asked for: an ordinary re-press (RaiseOrClose), whose
        // close saves like OK — the title-bar X would throw the edits away. A deep link
        // elsewhere (BBS list etc.) jumps there first and only raises.
        if (_settings is { } existing)
        {
            if (existing.DataContext is SettingsWindowViewModel current
                && bbsName is null
                && (sectionId is null
                    || string.Equals(current.SelectedSection?.Id, sectionId, StringComparison.OrdinalIgnoreCase)))
            {
                RaiseOrClose(existing, current.ApplyAndClose);
                return;
            }
            if (existing.DataContext is SettingsWindowViewModel vm && sectionId is not null)
            {
                SettingsSectionViewModel? section = vm.Sections
                    .FirstOrDefault(s => string.Equals(s.Id, sectionId, StringComparison.OrdinalIgnoreCase));
                if (section is not null) vm.SelectedSection = section;
                if (bbsName is not null) vm.SelectBbs(bbsName, realmName);
            }
            RaiseExisting(existing);
            return;
        }

        AppServices svc = AppServices.Current;
        SettingsWindow window = new()
        {
            DataContext = new SettingsWindowViewModel(
                svc.Profile, svc.Log,
                sendText: SendTextFromSettings,
                initialSectionId: sectionId,
                initialBbsName: bbsName,
                initialRealmName: realmName),
        };
        window.Closed += (_, _) =>
        {
            if (window.DataContext is SettingsWindowViewModel vm) vm.Dispose();
            _settings = null;
        };
        _settings = window;
        window.Show(main);
    }

    // Singleton-ish handle to the Game Data Browser (see RaiseOrClose).
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

    // Game Data menu → "Modify avoid rooms…". Staged editor over the
    // per-character avoided + stash room sets (both on MovementFilter). Save
    // commits both sets + recolours the map; Cancel discards.
    [RelayCommand]
    private async Task OpenAvoidRoomsEditorAsync()
    {
        var svc = AppServices.Current;
        ViewModels.AvoidRoomsEditorDialogViewModel vm = new(svc.Movement, svc.RoomGraph);
        await svc.Dialogs.OpenWindowAsync<
            ViewModels.AvoidRoomsEditorDialogViewModel, bool>(vm);
    }

    // Game Data menu → "Manage Sets…". Immediate-action dialog: copy or
    // move the ticked kinds of a set's data into another set, or delete a
    // set (game-data tables + loops). A delete drops the set from the menu,
    // so rebuild the set list once the dialog closes.
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
    //   - no section requested (toolbar / Ctrl+G), or the section requested
    //     is already showing → RaiseOrClose: raise it when buried, close it
    //     when in front.
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

        Guide.NotifyActionDone(GuideActions.GameDataBrowserOpened);

        if (_gameDataBrowser is { } existing)
        {
            // Already on the section asked for: an ordinary re-press (RaiseOrClose). A
            // record link, or a section elsewhere, navigates first and only raises.
            MudPlay.ViewModels.GameData.GameDataBrowserViewModel? existingVm =
                existing.DataContext as MudPlay.ViewModels.GameData.GameDataBrowserViewModel;
            if (rowSelector is null && (initialSectionId is null
                    || string.Equals(existingVm?.SelectedSection?.Id, initialSectionId, StringComparison.OrdinalIgnoreCase)))
            {
                RaiseOrClose(existing);
                return;
            }

            if (rowSelector is not null && initialSectionId is not null)
            {
                existingVm?.NavigateToRecord(initialSectionId, rowSelector);
            }
            else if (initialSectionId is not null && existingVm is not null
                     && !string.Equals(existingVm.SelectedSection?.Id, initialSectionId, StringComparison.OrdinalIgnoreCase))
            {
                MudPlay.ViewModels.GameData.GameDataSectionViewModel? target =
                    existingVm.Sections.FirstOrDefault(s => string.Equals(s.Id, initialSectionId, StringComparison.OrdinalIgnoreCase));
                if (target is not null) existingVm.SelectedSection = target;
            }

            RaiseExisting(existing);
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
            AppServices.Current.MessageCandidates,
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

    // Items listed at the top of the Game Data menu, one per imported set. Each entry has a
    // checkbox-style header (checked = currently active set) and a command
    // that flips GameDataCache.ActiveSet + writes the active realm's
    // RealmProfile.ActiveGameDataSet (falling back to
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

    // ----- Casting spell profiles (Settings → Combat quick-swap) -----

    // Shared item list for the Action → Combat Profiles fly-out and the toolbar's
    // profile-menu button — one "N) name" row per configured profile, the active
    // one checked. Rebuilt on any CombatProfileManager.Changed.
    public ObservableCollection<CombatProfileMenuItem> CombatProfileItems { get; } = new();

    // Drives the Profiles menu / flyout visibility (always ≥1 once a profile is
    // loaded).
    [ObservableProperty] private bool _hasCombatProfiles;

    // The label on the toolbar's two profile buttons (cycle and menu) —
    // "P<active#>". ApplyToolbarRowState copies it onto their rows' badges;
    // SyncToolbarStateFlags re-runs it.
    [ObservableProperty] private string _combatProfileCycleLabel = "P1";

    private void RebuildCombatProfilesMenu()
    {
        Game.Combat.CombatProfileManager mgr = AppServices.Current.CombatProfiles;
        CombatProfileItems.Clear();
        IReadOnlyList<Models.Profile.CombatSpellProfile> profiles = mgr.Profiles;
        int active = mgr.ActiveIndex;
        for (int i = 0; i < profiles.Count; i++)
        {
            int index = i;
            CombatProfileItems.Add(new CombatProfileMenuItem(
                number: i + 1,
                name: profiles[i].Name,
                isActive: i == active,
                switchCommand: new RelayCommand(() => AppServices.Current.CombatProfiles.SwitchToIndex(index))));
        }
        HasCombatProfiles = profiles.Count > 0;
        CombatProfileCycleLabel = "P" + (active >= 0 ? active + 1 : 1);
    }

    // Toolbar cycle button — left-click advances to the next profile (wraps). A
    // live quick-swap of the saved profiles (independent of the staged Settings
    // editor).
    [RelayCommand]
    private void CycleCombatProfile() => AppServices.Current.CombatProfiles.Cycle();

    // Right-click twin of the cycle button (invoked from the toolbar code-behind) —
    // steps back to the previous profile (wraps).
    public void CycleCombatProfileBack() => AppServices.Current.CombatProfiles.CycleBack();

    // The terminal right-click Favorites flyout — starred GOTO rooms, then
    // favourited loops and auto-lair setups, numbered 1..N.
    public ObservableCollection<FavoriteMenuItem> Favorites { get; } = new();

    private bool _hasFavorites;

    // Drives the flyout's visibility — the whole submenu hides when it has no
    // rows.
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

    // The terminal right-click "Sys Gotos" flyout — the usable `sys goto` locations
    // for the active BBS (empty when the power is off; the flyout is capability-gated
    // out of the menu entirely then). Each row fires the jump on click, greyed (null
    // command) when the character is below the row's Min level.
    public ObservableCollection<FavoriteMenuItem> SysopGotoItems { get; } = new();

    private void RebuildSysopGotoMenu()
    {
        var s = AppServices.Current;
        SysopGotoItems.Clear();
        int number = 0;
        foreach (Models.Profile.SysopGotoLocation loc in s.SysopGoto.UsableNow)
        {
            string landing = s.RoomGraph.GetRoom(new Game.Map.RoomKey(loc.Map, loc.Room)) is { } r
                ? r.Name
                : $"{loc.Map}/{loc.Room}";
            bool firable = s.SysopGoto.MeetsLevel(loc);
            string label = firable
                ? $"{loc.Name} — {landing}"
                : $"{loc.Name} — {landing} (needs L{loc.MinLevel})";
            Models.Profile.SysopGotoLocation target = loc;
            SysopGotoItems.Add(new FavoriteMenuItem($"{++number})", label, GotoFavBrush,
                firable ? new RelayCommand(() => FireSysopGoto(target)) : null));
        }
    }

    // Fire a Sys Goto from a menu, surfacing any gate refusal to the terminal (the
    // typed-command path writes its own; the menu path routes it here).
    private void FireSysopGoto(Models.Profile.SysopGotoLocation loc)
    {
        if (!AppServices.Current.SysopGoto.TryFire(loc, out string refusal)
            && !string.IsNullOrEmpty(refusal))
            AppServices.Current.WriteTerminalNotice(refusal);
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
        if (s.RefuseStartForMasterSwitch("Loop")) return;
        s.MovementControl.StartUserRun(() =>
        {
            if (s.AutoLair.IsActive) s.AutoLair.Stop("loop favorite started");
            _ = MudPlay.ViewModels.Navigation.RouteChoicePrompt.StartLoopAsync(s, loop);
        });
    }

    // Start a favourited auto-lair setup from the flyout — mirrors the
    // Navigation manager's "Load" then Start: stop conflicting engines, replace
    // the live markers with the setup's, and begin auto-lairing.
    private void StartLairFavorite(Models.Profile.LairSetup setup)
    {
        var s = AppServices.Current;
        if (s.RefuseStartForMasterSwitch("Auto-Lair")) return;
        s.MovementControl.StartUserRun(() =>
        {
            if (s.LoopRunner.State != Game.Map.LoopState.Idle) s.LoopRunner.Stop("auto-lair favorite started");
            if (s.AutoLair.IsActive) s.AutoLair.Stop("auto-lair favorite started");
            s.AutoLair.Clear();
            foreach (Models.Profile.LairMarker m in setup.Markers)
                s.AutoLair.Mark(new Game.Map.RoomKey(m.Map, m.Room), m.OverrideRespawnSeconds);
            s.AutoLair.Start();
        });
    }

    // Walk to a starred favourite from the right-click flyout — stop any running
    // movement engine first, then hand off to the shared route picker (the same
    // entry point the Navigation manager's Walk buttons use).
    private async Task WalkToFavoriteRoomAsync(Game.Map.RoomKey key)
    {
        // Before the Stop: a run frozen by the master switch is not given up for
        // a walk that won't start.
        if (AppServices.Current.RefuseStartForMasterSwitch("Walk")) return;
        MovementStop();
        await MudPlay.ViewModels.Navigation.RouteChoicePrompt.WalkAsync(AppServices.Current, key);
    }

    // Flip the active set and persist the user's choice. Active set is a
    // realm-scoped setting (every character on the same realm shares the same
    // MajorMUD MDB); we write it to the active realm when one resolves, else
    // fall through to global settings so the menu still works
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

        if (AppServices.Current.ResolveActiveRealm() is { } active)
        {
            active.Realm.ActiveGameDataSet = setName;
            AppServices.Current.Bbs.Save(active.Bbs);
        }
        else
        {
            AppServices.Current.Settings.Current.DefaultGameDataSet = setName;
            AppServices.Current.Settings.Save();
        }

        RebuildGameDataSetsMenu();
    }

    // Game Data → Import .mdb… — picks an Access database, runs the
    // importer, switches to the new set on success.
    [RelayCommand]
    private async Task ImportMdbAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        // Tick the first-run tour's "Import .mdb" line the moment the import runs.
        Guide.NotifyActionDone(GuideActions.ImportMdb);

        // With Profile Management open, as the first-run guide used to leave it, the
        // client sat looking frozen at this point until the user tried to close it
        // from the taskbar, and worked with that window closed (user's report,
        // 2026-10-14: the file picker, which belongs to the main window, was out of
        // reach). That window applies its edits as they are made, so closing it
        // here loses nothing.
        if (_profileManager is { } openManager)
        {
            openManager.Close();
            AppServices.Current.Log.Info("MDB", "Profile Management closed ahead of the import's file picker");
        }

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

        // Importing after launch runs on the splash screen (no session yet), which
        // overlays the terminal and hides the import's status / error lines. Dismiss
        // it so the import output is actually visible on the terminal.
        if (ShowSplash) ShowSplash = false;

        WriteTerminalStatus("[MDB IMPORT STARTED]", TerminalStatusKind.Notice);
        MdbImportResult result = await importer.ImportAsync(path);

        if (result.Success)
        {
            AppServices.Current.Log.Info("MDB", result.Message);
            WriteTerminalStatus(BuildMdbCompleteStatus(result), TerminalStatusKindFor(result));
            // Seed base navigation loops + GOTO favourites for the realm before we
            // switch to the set, so the ensuing set-switch loads the seeded files.
            // Once-only per set (marker), additive, best-effort.
            NavSeedBootstrapper.SeedIfNeeded(result.FolderName, AppServices.Current.Log);
            // The Simulator's rankings for the set are not deleted for the import:
            // each carries the stamps of the table files it was made on, and is
            // dropped when it is next listed if the import changed one of them.
            SwitchActiveGameDataSet(result.FolderName);
            Guide.NotifyActionDone(GuideActions.GameDataImported);
        }
        else
        {
            // An empty / malformed MDB no longer swaps to a broken set — it fails here
            // with the reason (and the reader's catalog scan) in the Program Log.
            AppServices.Current.Log.Warn("MDB", result.Message);
            WriteTerminalStatus("[MDB IMPORT FAILED — see Program Log]", TerminalStatusKind.Error);
        }
    }

    // Compose the terminal-status line for a successful MDB import: the totals, then
    // what this export carries. A Lairs table and room-command item sources each come
    // or don't, independently, so each is named rather than folded into one "format".
    // FEWER than 9 tables (a truncated MDB) flips the line red; MORE than 10 (a newer
    // export with extra tables) is fine — the extras are imported but unused. A
    // zero-table MDB never reaches here: ImportAsync fails such an import outright.
    internal static string BuildMdbCompleteStatus(MdbImportResult r)
    {
        string entries = $"{r.RowsImported:N0} entries";

        string tablesPart = r.TablesSkipped == 0
            ? $"{r.TablesImported} tables"
            : $"{r.TablesImported}/{r.TablesFound} tables ({r.TablesSkipped} skipped)";

        string lairs = r.HasLairsTable ? "lairs table: yes" : "lairs table: no";
        string roomCommands = r.RoomCommandItems > 0
            ? $"room commands: yes ({r.RoomCommandItems:N0} items)"
            : "room commands: no";

        // The "see Program Log" hint fires whenever the user has reason to dig
        // in — skipped tables OR a suspiciously small MDB (fewer game tables
        // than any MajorMUD export ships).
        string tooFew = r.TablesFound < 9 ? " — UNEXPECTED TABLE COUNT" : string.Empty;
        bool needsLogPointer = r.TablesSkipped > 0 || r.TablesFound < 9;
        string logHint = needsLogPointer ? " — see Program Log" : string.Empty;

        return $"[MDB IMPORT COMPLETE: {r.FolderName} — {tablesPart}, {entries} — {lairs}, {roomCommands}{tooFew}{logHint}]";
    }

    private static TerminalStatusKind TerminalStatusKindFor(MdbImportResult r)
        => (r.TablesSkipped > 0 || r.TablesFound < 9)
           ? TerminalStatusKind.Error
           : TerminalStatusKind.Notice;

    // Game Data → Import loops (MegaMUD .mp)… — runs the exact same
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
            log: s.Log,
            movementFilter: s.Movement);
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

    // Singleton handle for the live NavigationWindow (see RaiseOrClose).
    private Views.Navigation.NavigationWindow? _navigationWindow;

    [RelayCommand]
    private void OpenNavigation()
    {
        Guide.NotifyActionDone(GuideActions.NavigationOpened);
        if (_navigationWindow is { } existing) { RaiseOrClose(existing); return; }
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
            RaiseExisting(existing);
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

    // Registered on AppServices — the Game Data room chips (a monster's lair /
    // placed / summoned rooms, the Rooms-tab double-click) and the shop popup's
    // clickable room title route here to open/focus the map, re-root it on the
    // chosen room, select it, and show its details in the ROOM INFO panel.
    private void FocusNavigationOnRoom(Game.Map.RoomKey key)
        => EnsureNavigationWindow()?.SelectAndInspect(key);

    // Registered on AppServices — the item record's "Queue Walking here" shop
    // links route here to open/focus the map and ARM the walk (QueuedDestination),
    // exactly as picking a nav search result does; the user then clicks Run.
    private void QueueWalkToRoom(Game.Map.RoomKey key)
        => EnsureNavigationWindow()?.QueueDestination(key);

    // Registered on AppServices — the Roomba room list's "Goto" button routes here
    // to open/focus the map, arm the room, and START the walk immediately (the full
    // "Walk here" path), rather than only arming it like QueueWalkToRoom.
    private void GoWalkToRoom(Game.Map.RoomKey key)
        => _ = EnsureNavigationWindow()?.QueueAndStartWalkTo(key);

    // A walk started on the player's behalf (a Sell Tour stop). True once the walker
    // is heading for the room or the character already stands in it; false when the
    // walk was called off (the route picker cancelled, a held trip not resumed).
    private async Task<bool> ErrandWalkToRoom(Game.Map.RoomKey key)
    {
        if (EnsureNavigationWindow() is not { } navigation) return false;
        await navigation.QueueAndStartWalkTo(key, askOnlyOverAvoids: true);
        AppServices services = AppServices.Current;
        return (services.Walker.State != Game.Map.WalkState.Idle && services.Walker.Destination == key)
            || services.RoomTracker.State.CurrentRoom?.Key == key;
    }

    // Room-detail exit clicks re-root the popup on the neighbour and let an
    // already-open map follow — but must not summon the map if it's closed,
    // so this centres only when the window is up (no EnsureNavigationWindow).
    private void CenterNavigationOnRoomIfOpen(Game.Map.RoomKey key)
    {
        if (_navigationWindow?.DataContext is ViewModels.Navigation.NavigationViewModel vm)
            vm.OnFloorChangeRequested(key);
    }

    // Centre the map on a room and flash it green as an @where reply does, opening or
    // raising the Navigation window first: unlike the reply's own flash, this one was
    // asked for (a double-click in Players Seen), so the map comes to the user.
    private void ShowRoomOnMap(Game.Map.RoomKey key)
    {
        if (AppServices.Current.RoomGraph.GetRoom(key) is null)
        {
            AppServices.Current.Log.Info("Navigation",
                $"Room {key.Map}/{key.Room} isn't in the active game data's map, so there is nothing to show.");
            return;
        }
        if (EnsureNavigationWindow() is not { } vm) return;
        AppServices.Current.Log.Info("Navigation", $"Showing {key.Map}/{key.Room} on the map.");
        vm.ShowWhereHighlight(key);
    }

    // Flash + centre an @where reply's room on the map, but only if it's open —
    // an answered "where are you?" lights up where they are without summoning the
    // window over what you're doing.
    private void HighlightWhereRoomIfOpen(Game.Map.RoomKey key)
    {
        if (_navigationWindow?.DataContext is ViewModels.Navigation.NavigationViewModel vm)
            vm.ShowWhereHighlight(key);
    }

    // Draw another player's route from their @path reply on the map, but only if it's
    // open — same rule as the @where flash.
    private void ShowLeaderRouteIfOpen(string sender, Game.Remote.PathReport report)
    {
        if (_navigationWindow?.DataContext is ViewModels.Navigation.NavigationViewModel vm)
            vm.ShowLeaderRoute(sender, report);
    }

    private void ShowLeaderGotoIfOpen(string sender, Game.Map.RoomKey dest)
    {
        if (_navigationWindow?.DataContext is ViewModels.Navigation.NavigationViewModel vm)
            vm.ShowLeaderGoto(sender, dest);
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
            _ = MudPlay.ViewModels.Navigation.RouteChoicePrompt.StartLoopAsync(s, staged);
            return;
        }
        OpenNavManager(startOnGotoTab: true);
    }

    // Toolbar Pause — pauses the running engine (no-op if already paused / idle).
    [RelayCommand]
    private void MovementPause() => AppServices.Current.MovementControl.Pause();

    // Toolbar Stop — backs the running engine fully out to Idle.
    [RelayCommand]
    private void MovementStop()
    {
        // The first Stop holds a money or training errand; a second ends it.
        if (!AppServices.Current.MovementControl.StopByUser()) return;
        OnUserStopped("stopped by the user", endsTheRun: true);
    }

    // Singleton handle for the one Navigation Management dialog — either entry point
    // (toolbar Start fallback, map window button) reuses it rather than stacking a
    // second identical window.
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
            // Already on the tab asked for: an ordinary re-press (RaiseOrClose); the
            // other entry point switches tabs and only raises.
            if (existing.DataContext is ViewModels.Navigation.NavigationManagerDialogViewModel evm)
            {
                int tab = evm.SelectedTabIndex;
                evm.SelectTab(startOnGotoTab);
                if (evm.SelectedTabIndex != tab) { RaiseExisting(existing); return; }
            }
            RaiseOrClose(existing);
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
            startOnGotoTab: startOnGotoTab,
            movementFilter: s.Movement);

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

    // Singleton handle for the live SpellBookWindow (see RaiseOrClose).
    private SpellBookWindow? _spellBook;

    [RelayCommand]
    private void OpenSpellBook()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_spellBook is { } existing) { RaiseOrClose(existing); return; }

        SpellBookWindow window = new()
        {
            DataContext = new SpellBookViewModel(
                AppServices.Current.Spellbook,
                () => AppServices.Current.Profile.Current?.LastKnownStats?.Class,
                () => AppServices.Current.PlayerStats.Spellcasting,
                () => AppServices.Current.GameData.ActiveRealm == Game.RealmType.ParaMud,
                () => Game.Calculators.CharacterCalculator.SpellDamageBonus(
                    AppServices.Current.PlayerStats,
                    AppServices.Current.Inventory.Snapshot.EquippedItems,
                    AppServices.Current.GameData)),
        };
        window.Closed += (_, _) => _spellBook = null;
        _spellBook = window;
        window.Show(main);
    }

    // Singleton handle for the live MonsterIntelWindow (see RaiseOrClose).
    private MonsterIntelWindow? _monsterIntel;

    [RelayCommand]
    private void OpenMonsterIntel()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        Guide.NotifyActionDone(GuideActions.MonsterIntelOpened);

        if (_monsterIntel is { } existing) { RaiseOrClose(existing); return; }

        var svc = AppServices.Current;
        MonsterIntelWindow window = new()
        {
            DataContext = new MonsterIntelViewModel(
                svc.GameData, svc.MonsterCatalog, svc.Resolver,
                svc.PlayerStats, svc.Inventory, svc.Spellbook, svc.ItemMagic,
                svc.MonsterObservations, svc.PlayerState,
                buffProvider: () => svc.Profile.Current?.PartyBuffs,
                profile: svc.Profile,
                isBuffUp: svc.Conditions.IsActiveByName),
        };
        window.Closed += (_, _) => _monsterIntel = null;
        _monsterIntel = window;
        window.Show(main);
    }

    // Singleton handle for the live RoundTotalsWindow (see RaiseOrClose).
    private RoundTotalsWindow? _roundTotals;

    [RelayCommand]
    private void OpenRoundTotals()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_roundTotals is { } existing) { RaiseOrClose(existing); return; }

        RoundTotalsWindow window = new()
        {
            DataContext = new RoundTotalsViewModel(AppServices.Current.RoundTotals, AppServices.Current.Profile),
        };
        window.Closed += (_, _) => _roundTotals = null;
        _roundTotals = window;
        window.Show(main);
    }

    // Singleton handle for the live SessionStatsWindow (see RaiseOrClose).
    private SessionStatsWindow? _sessionStats;

    [RelayCommand]
    private void OpenSessionStats()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_sessionStats is { } existing) { RaiseOrClose(existing); return; }

        SessionStatsWindow window = new()
        {
            DataContext = new SessionStatsViewModel(
                AppServices.Current.CombatSession,
                AppServices.Current.TimeAnalysis,
                AppServices.Current.SessionActivity,
                AppServices.Current.HpMaHistory,
                AppServices.Current.LoopRunner,
                AppServices.Current.RoomGraph,
                AppServices.Current.SessionStatsLayout,
                AppServices.Current.PlayerStats,
                AppServices.Current.GameData,
                AppServices.Current.Currency,
                AppServices.Current.SelfTimeToLevel,
                OpenTransactionHistory,
                OpenPlayersSeen),
        };
        window.Closed += (_, _) => _sessionStats = null;
        _sessionStats = window;
        window.Show(main);
    }

    // Singleton handle for the live TransactionHistoryWindow (see RaiseOrClose).
    private TransactionHistoryWindow? _transactionHistory;

    [RelayCommand]
    private void OpenTransactionHistory()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_transactionHistory is { } existing) { RaiseOrClose(existing); return; }

        TransactionHistoryWindow window = new()
        {
            DataContext = new TransactionHistoryViewModel(AppServices.Current.TransactionHistory),
        };
        window.Closed += (_, _) => _transactionHistory = null;
        _transactionHistory = window;
        window.Show(main);
    }

    // Singleton handle for the live PlayersSeenWindow (see RaiseOrClose).
    private PlayersSeenWindow? _playersSeen;

    [RelayCommand]
    private void OpenPlayersSeen()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_playersSeen is { } existing) { RaiseOrClose(existing); return; }

        PlayersSeenWindow window = new()
        {
            DataContext = new PlayersSeenViewModel(AppServices.Current.PlayerSightings, ShowRoomOnMap),
        };
        window.Closed += (_, _) => _playersSeen = null;
        _playersSeen = window;
        window.Show(main);
    }

    [RelayCommand]
    private void OpenWorkshop() => OpenWorkshopAt(null);

    // Tools → Wire Inspector. Single instance: a re-press raises or closes it
    // (RaiseOrClose).
    private WireInspectorWindow? _wireInspector;

    [RelayCommand]
    private void OpenWireInspector()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        // A re-press raises the open window, or closes it when it's in front (RaiseOrClose).
        if (_wireInspector is { } existing)
        {
            RaiseOrClose(existing);
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

    // Tools → Open Logs folder…
    [RelayCommand]
    private void OpenLogsFolder()
    {
        if (!ShellLaunch.OpenPath(AppPaths.LogsDir))
            AppServices.Current.Log.Warn("ShellLaunch", $"Could not open {AppPaths.LogsDir}");
    }

    // Tools → Clear chatlog, and the Conversation window's Clear All. Wipes every entry from the app-singleton
    // ChatHistoryStore — the Conversation window's contents go with it
    // (it binds to the same store) and a fresh open shows an empty list — and
    // truncates the persisted talk.log so the on-disk copy matches. Destructive;
    // the menu entry asks nothing, the window's button confirms in its flyout.
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
    // to these — so rebinding in Settings → Toolbar + Shortcuts updates
    // every menu's shortcut display immediately.

    public string ConversationGesture     => GetGesture(Models.Profile.BuiltInAction.OpenConversation);
    public string PartyGesture            => GetGesture(Models.Profile.BuiltInAction.OpenParty);
    public string BuffWatchdogGesture     => GetGesture(Models.Profile.BuiltInAction.OpenBuffWatchdog);
    public string ProfileManagerGesture   => GetGesture(Models.Profile.BuiltInAction.OpenProfileManager);
    public string WorkshopGesture         => GetGesture(Models.Profile.BuiltInAction.OpenWorkshop);
    public string NavigationGesture       => GetGesture(Models.Profile.BuiltInAction.OpenNavigation);
    public string SpellBookGesture        => GetGesture(Models.Profile.BuiltInAction.OpenSpellBook);
    public string MonsterIntelGesture     => GetGesture(Models.Profile.BuiltInAction.OpenMonsterIntel);
    public string LogPaneGesture          => GetGesture(Models.Profile.BuiltInAction.OpenLogPane);
    public string BackscrollGesture       => GetGesture(Models.Profile.BuiltInAction.OpenBackscroll);
    public string SessionStatsGesture     => GetGesture(Models.Profile.BuiltInAction.OpenSessionStats);
    public string RoundTotalsGesture      => GetGesture(Models.Profile.BuiltInAction.OpenRoundTotals);
    public string SettingsGesture         => GetGesture(Models.Profile.BuiltInAction.OpenSettings);
    public string GameDataBrowserGesture  => GetGesture(Models.Profile.BuiltInAction.OpenGameDataBrowser);
    public string ToggleConnectionGesture => GetGesture(Models.Profile.BuiltInAction.ToggleConnection);
    public string SaveProfileGesture      => GetGesture(Models.Profile.BuiltInAction.SaveProfile);
    public string QuitGesture             => GetGesture(Models.Profile.BuiltInAction.Quit);

    private static string GetGesture(Models.Profile.BuiltInAction action)
        => AppServices.Current.Keybindings.Get(action).Label;

    // Fire PropertyChanged for every *Gesture label so menus refresh after a rebind.
    private void RefreshKeybindLabels()
    {
        OnPropertyChanged(nameof(ConversationGesture));
        OnPropertyChanged(nameof(PartyGesture));
        OnPropertyChanged(nameof(BuffWatchdogGesture));
        OnPropertyChanged(nameof(ProfileManagerGesture));
        OnPropertyChanged(nameof(WorkshopGesture));
        OnPropertyChanged(nameof(NavigationGesture));
        OnPropertyChanged(nameof(SpellBookGesture));
        OnPropertyChanged(nameof(LogPaneGesture));
        OnPropertyChanged(nameof(BackscrollGesture));
        OnPropertyChanged(nameof(SessionStatsGesture));
        OnPropertyChanged(nameof(RoundTotalsGesture));
        OnPropertyChanged(nameof(SettingsGesture));
        OnPropertyChanged(nameof(GameDataBrowserGesture));
        OnPropertyChanged(nameof(ToggleConnectionGesture));
        OnPropertyChanged(nameof(SaveProfileGesture));
        OnPropertyChanged(nameof(QuitGesture));
    }

    private AboutWindow? _aboutWindow;

    // Help → About MudPlay. A modeless, read-only window: program name + version,
    // a clickable repo link, a tab per bundled license (MIT / SIL OFL / CC BY-SA),
    // and a community thank-you. Pressing the command while it's open behaves as
    // RaiseOrClose.
    [RelayCommand]
    private void OpenAbout()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_aboutWindow is { } existing)
        {
            RaiseOrClose(existing);
            return;
        }

        AboutWindow window = new() { DataContext = new AboutWindowViewModel() };
        window.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow = window;
        window.Show(main);
    }

    private GuidePickerWindow? _guidePicker;

    // Help → Visual Guides. A modeless picker of every guide with its progress;
    // picking one runs it in the floating step card. RaiseOrClose on a re-press.
    [RelayCommand]
    private void OpenVisualGuides()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_guidePicker is { } existing)
        {
            RaiseOrClose(existing);
            return;
        }

        GuidePickerWindow window = new() { DataContext = new GuidePickerViewModel(Guide) };
        window.Closed += (_, _) => _guidePicker = null;
        _guidePicker = window;
        window.Show(main);
    }

    private MudPlay.Views.UpdateWindow? _updateWindow;

    // Help / Tools → Update the Client. A modeless window that reads the update service's
    // cached verdict (or checks fresh), and — on the user's request — downloads,
    // verifies, swaps the new build in, and relaunches. Pressing the command while
    // it's open behaves as RaiseOrClose.
    [RelayCommand]
    private void OpenUpdate()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_updateWindow is { } existing)
        {
            RaiseOrClose(existing);
            return;
        }

        MudPlay.Views.UpdateWindow window = new()
        {
            DataContext = new UpdateWindowViewModel(
                AppServices.Current.Update, PrepareForUpdateRestartAsync, ShutdownNow),
        };
        window.Closed += (_, _) => _updateWindow = null;
        _updateWindow = window;
        window.Show(main);
    }

    private MudPlay.Views.Help.HelpWindow? _helpWindow;

    // Help → Help topics. A modeless, read-only compendium: a searchable table of
    // contents (left) that drives a rendered content pane (right), covering how
    // features work, how to use the client, and what each setting means. Pressing
    // the command while it's open behaves as RaiseOrClose.
    [RelayCommand]
    private void OpenHelpWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_helpWindow is { } existing)
        {
            RaiseOrClose(existing);
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
    // ToolbarItemCatalogue routes its ToggleAutoCombat / ToggleAutoHeal
    // entries here by command-name reflection. (The Settings → General
    // checkboxes write the base modes, GeneralSettings.AutoModeBase, not
    // this live state.)

    // Flip the live IsAutoCombatActive bit (which the partial OnXxxChanged
    // hook persists to GeneralSettings.AutoMode.AutoCombat and refreshes
    // the toolbar IsActive badge). Bound from the toolbar ToggleAutoCombat
    // button + the menu hotkey.
    [RelayCommand]
    private void ToggleAutoCombat() => IsAutoCombatActive = !IsAutoCombatActive;

    // Flip the live IsAutoNukeActive bit.
    [RelayCommand]
    private void ToggleAutoNuke() => IsAutoNukeActive = !IsAutoNukeActive;

    // The combined control: both on turns both off, anything else turns both on.
    [RelayCommand]
    private void ToggleAutoHealRest() => IsAutoHealRestActive = !IsAutoHealRestActive;

    // Flip the live IsAutoHealActive bit.
    [RelayCommand]
    private void ToggleAutoHeal() => IsAutoHealActive = !IsAutoHealActive;

    // Flip the live IsAutoRestActive bit.
    [RelayCommand]
    private void ToggleAutoRest() => IsAutoRestActive = !IsAutoRestActive;

    // The combined Auto Rest / Heal control over the two separate switches: lit
    // only while both are on, and setting it sets both.
    public bool IsAutoHealRestActive
    {
        get => IsAutoHealActive && IsAutoRestActive;
        set
        {
            IsAutoHealActive = value;
            IsAutoRestActive = value;
        }
    }

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

    // Flip the live IsSprintModeActive bit (the partial OnXxxChanged hook
    // persists it to GeneralSettings.SprintMode, forces/restores Auto Combat,
    // and the toolbar IsActive badge follows). Bound from the toolbar /
    // Action-menu / hotkey.
    [RelayCommand]
    private void ToggleSprintMode() => IsSprintModeActive = !IsSprintModeActive;

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

    // The master switch. Delegates to the shared Game.AutoModeController so the
    // toolbar button, the Action-menu item and the @auto-all remote command all
    // drive one switch. The controller's profile write fires ProfileSaving, which
    // reseeds the toggle observables; IsMasterSwitchOn follows KillSwitchToggled.
    [RelayCommand]
    private void AllAutoOff() => AppServices.Current.AutoModeController.ToggleAll(Game.AutoModeController.ByUserPress);

    // "Reset States" — the manual recovery escape hatch. First every engine goes
    // back to idle (AppServices.ResetEngineStates: walks, loops, lair, detours,
    // recoveries and any room an engine meant to walk back to — a user's walk kept
    // ending by heading back toward where they'd used a token). Then it drops every condition
    // active on us (ConditionTracker.ClearAll), which cascades through each
    // owner's ActiveFlags edge: the Confused / Held self-chips clear, their
    // ConfusionGate / HeldGate release, and the ailment @wait balances to @ok.
    // Then it sweeps the remaining self-row ailment chips so the party window
    // shows a clean self row, and sweeps the ailment chips of EVERY party member
    // (a stuck badge on another member, e.g. the leader, is the reported case).
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
        // Every engine back to idle first — a walk, detour or recovery left running
        // (or holding a room to walk back to) is exactly what a reset has to clear.
        AppServices.Current.ResetEngineStates("Reset States (manual)");

        AppServices.Current.Conditions.ClearAll("reset");

        // Clear the ailment chips for EVERY party member, not just self. A stuck
        // HELD / ailment badge on another member (e.g. the leader) can't self-clear
        // when that client never sent the matching off-signal, and Reset States is
        // the manual escape hatch for it (report paradigm-20260820-122200). Reading
        // the roster names and clearing by name is safe (SetMemberAilment normalises
        // to given name); a member with no name is skipped.
        Game.PartyManager party = AppServices.Current.Party;
        foreach (Game.PartyMember m in party.State.Members)
        {
            if (string.IsNullOrWhiteSpace(m.Name)) continue;
            party.SetMemberAilment(m.Name, Models.GameData.MessageFlags.Confused, false);
            party.SetMemberAilment(m.Name, Models.GameData.MessageFlags.MovementPrevented, false);
            party.SetMemberAilment(m.Name, Models.GameData.MessageFlags.Poisoned, false);
            party.SetMemberAilment(m.Name, Models.GameData.MessageFlags.Blinded, false);
            party.SetMemberAilment(m.Name, Models.GameData.MessageFlags.Diseased, false);
        }

        AppServices.Current.CombatTracker.ResetCombatState("Reset States (manual)", forgetSneakBreak: true);

        // Force back into the Default gear set — a stuck rest set or a half-finished
        // swap is exactly what a manual reset rescues — and re-poll `health` (the
        // compact one-line HP/pool readout — far less scroll than the full stat
        // screen) so a max HP/mana high-water mark that drifted above the real ceiling
        // re-latches to the authoritative value (a stale max is what strands a rest).
        Game.Inventory.EquipResult equip =
            AppServices.Current.Equipment.ApplyByTrigger(Models.Profile.EquipTriggerType.Default);
        SendUserText("health", SentCommandTrail.Origin.Window);
        // Re-observe the room so the trackers and the post-force-clear rest hold
        // settle on what's actually here.
        SendUserInputAs(SentCommandTrail.Origin.Window, System.Text.Encoding.Latin1.GetBytes("\r"));
        AppServices.Current.Health.Evaluate();

        AppServices.Current.Log.Info(Game.Conditions.ConditionTracker.LogCategory,
            "Reset States — self conditions, ailment chips, combat state, and derived movement holds cleared; "
            + $"re-equipping Default set ({equip}) and re-polling `health` to re-latch max HP/mana (manual).");
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
    private void DropAll() => RunDrop(Game.Remote.InventoryActionHandler.DropScope.Unworn);

    // "Drop Everything" — everything held: worn gear, light, keys and coins too.
    [RelayCommand]
    private void DropEverything() => RunDrop(Game.Remote.InventoryActionHandler.DropScope.Full);

    // "Drop Coins" / "Drop Keys" — just the coins / just the key ring.
    [RelayCommand]
    private void DropCoins() => RunDrop(Game.Remote.InventoryActionHandler.DropScope.Coins);

    [RelayCommand]
    private void DropKeys() => RunDrop(Game.Remote.InventoryActionHandler.DropScope.Keys);

    private static void RunDrop(Game.Remote.InventoryActionHandler.DropScope scope) =>
        AppServices.Current.Log.Info(Game.Inventory.InventoryManager.LogCategory,
            AppServices.Current.InventoryAction.DropAll(scope));

    // "Hide All" family — the Drop All sweeps with `hide`, stashing everything in the
    // room (only a search turns it up again) instead of leaving it on the floor.
    [RelayCommand]
    private void HideAll() => RunHide(Game.Remote.InventoryActionHandler.DropScope.Unworn);

    [RelayCommand]
    private void HideEverything() => RunHide(Game.Remote.InventoryActionHandler.DropScope.Full);

    [RelayCommand]
    private void HideCoins() => RunHide(Game.Remote.InventoryActionHandler.DropScope.Coins);

    [RelayCommand]
    private void HideKeys() => RunHide(Game.Remote.InventoryActionHandler.DropScope.Keys);

    private static void RunHide(Game.Remote.InventoryActionHandler.DropScope scope) =>
        AppServices.Current.Log.Info(Game.Inventory.InventoryManager.LogCategory,
            AppServices.Current.InventoryAction.HideAll(scope));

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
        // Going back to Default is how a set held from this menu is let go (any equip
        // asked for by hand drops the hold; see EquipmentManager.ManualEquipStarting).
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

    // Wear one of the six gear sets by its short name (default / backstab / resthp /
    // restma / moving / bossing — EquipmentManager resolves them). Backs the Equip
    // toolbar button's ▾ picks and the Action → Equip submenu.
    //
    // Any set but Default is a hold: it goes on and stays on, with every automatic
    // gear swap off, until the same entry is picked again (or Default is). The menu
    // shows the held set ticked.
    [RelayCommand]
    private void EquipSet(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return;
        Game.Inventory.AutoEquipCoordinator auto = AppServices.Current.AutoEquip;
        Models.Profile.EquipmentSet? set = AppServices.Current.Equipment.FindSetByKeyword(keyword);
        if (set is { Trigger: Models.Profile.EquipTriggerType.Default })
        {
            EquipAll();
            return;
        }
        if (set is not null && auto.HeldSet == set.Trigger)
        {
            auto.ReleaseHeldSet();
            return;
        }
        Game.Inventory.EquipResult result = AppServices.Current.Equipment.ApplyByKeyword(keyword);
        if (set is not null && result is Game.Inventory.EquipResult.Applied or Game.Inventory.EquipResult.NoChange)
        {
            auto.HoldSet(set!.Trigger);
            string name = string.IsNullOrWhiteSpace(set.Name) ? keyword : set.Name;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => WriteTerminalStatus(
                $"[{name} will stay equipped until you deselect it.]", TerminalStatusKind.Notice));
            return;
        }
        string? note = result switch
        {
            Game.Inventory.EquipResult.NoChange => $"Gear set '{keyword}' already worn.",
            Game.Inventory.EquipResult.NotFound => $"No gear set '{keyword}' configured.",
            Game.Inventory.EquipResult.Busy     => "Equip already in progress.",
            _ => null,
        };
        if (note is not null)
            AppServices.Current.Log.Info(Game.Inventory.EquipmentManager.LogCategory, note);
    }

    // Which set, if any, is held from the Equip menu — the menu's tick marks.
    public bool IsBackstabSetHeld => HeldGearSet == Models.Profile.EquipTriggerType.Backstab;
    public bool IsRestHpSetHeld   => HeldGearSet == Models.Profile.EquipTriggerType.PreRestHp;
    public bool IsRestManaSetHeld => HeldGearSet == Models.Profile.EquipTriggerType.PreRestMana;
    public bool IsMovingSetHeld   => HeldGearSet == Models.Profile.EquipTriggerType.WhileMoving;
    public bool IsBossingSetHeld  => HeldGearSet == Models.Profile.EquipTriggerType.Bossing;

    private static Models.Profile.EquipTriggerType? HeldGearSet => AppServices.Current.AutoEquip.HeldSet;

    private void OnHeldGearSetChanged()
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(IsBackstabSetHeld));
            OnPropertyChanged(nameof(IsRestHpSetHeld));
            OnPropertyChanged(nameof(IsRestManaSetHeld));
            OnPropertyChanged(nameof(IsMovingSetHeld));
            OnPropertyChanged(nameof(IsBossingSetHeld));
        });

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

        // The toggles are the master switch's to guard: with it off nothing is
        // re-enabled, Auto-Train included, and the switch stays off.
        if (!AppServices.Current.AutoModeController.ReEnableOnReconnect(general)) return;

        profile.Settings ??= new();
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

        // Reseed the surfaced observables (toolbar badges, Action-menu ticks)
        // from the freshly persisted AutoMode.
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
        // OnAutoAttackChanged clears only the Combat gate. Sibling room-observation
        // gate-holders (the deferred-cash / get-items / search Acquisition holds) —
        // and CombatManager's own re-pick — re-evaluate solely on a fresh
        // observation, so toggling AutoCombat with none pending strands things
        // until a manual room re-display. Turning it OFF mid-fight needs this to
        // release the walker (and clear InCombat if the room is clear); turning it
        // back ON needs it just as much, or CombatManager.OnEntitiesObserved never
        // runs again for the unchanged current roster — its early-return while
        // disabled already nulled _currentTarget, and nothing re-picks a target
        // for it until some UNRELATED fresh observation happens to arrive (report
        // paradigm-20260827-203644: toggling AutoCombat off then back on mid-fight,
        // meant to un-stick a stalled fight, silently did nothing — the character
        // never resumed attacking the monster still sitting in the same room).
        ReevaluateCombatForCurrentRoom();
        MaybeEndSprintOnManualEngineEnable(value);
        // Anyone else changing it mid-detour (the user, Sprint, a base-mode reset)
        // takes it over, so the detour's end doesn't undo their choice.
        if (!_detourDrivingCombat) _detourTurnedOffCombat = false;
        if (!_climbDrivingEngines) _climbTurnedOffCombat = false;
        // Combat turned back on before the run began: nothing left to give back.
        if (value) _tripTurnedOffCombat = false;
    }

    // ----- Pyramid run-through ----------------------------------------------
    // Floors 1 and 2 of the Great Pyramid are run through: the climb turns the real
    // Auto Combat / Nuke / Rest / Get Items / Get Cash / Search / Hide / Light
    // toggles off as it comes onto them and back on as it reaches floor 3 or ends
    // (PyramidSolver decides; the list is the user's, 2026-10-03 — Heal, Bless and
    // Sneak stay as they are),
    // so those engines stop sending and the toolbar shows the state. Only a toggle
    // the climb turned off is turned back on, and one the player changes by hand in
    // between is theirs from then on — switching it on makes the climb wait for it.
    private bool _climbDrivingEngines;
    private bool _climbTurnedOffCombat;
    private bool _climbTurnedOffRest;
    private bool _climbTurnedOffGetItems;
    private bool _climbTurnedOffGetCash;
    private bool _climbTurnedOffSearch;
    private bool _climbTurnedOffNuke;
    private bool _climbTurnedOffHide;
    private bool _climbTurnedOffLight;

    private void OnPyramidRunThrough(bool on) => Dispatcher.UIThread.Post(() =>
    {
        _climbDrivingEngines = true;
        try
        {
            if (on)
            {
                _climbTurnedOffCombat   = IsAutoCombatActive;
                _climbTurnedOffRest     = IsAutoRestActive;
                _climbTurnedOffGetItems = IsAutoGetItemsActive;
                _climbTurnedOffGetCash  = IsAutoGetCashActive;
                _climbTurnedOffSearch   = IsAutoSearchActive;
                _climbTurnedOffNuke     = IsAutoNukeActive;
                _climbTurnedOffHide     = IsAutoHideActive;
                _climbTurnedOffLight    = IsAutoLightActive;
                if (IsAutoCombatActive)   IsAutoCombatActive   = false;
                if (IsAutoRestActive)     IsAutoRestActive     = false;
                if (IsAutoGetItemsActive) IsAutoGetItemsActive = false;
                if (IsAutoGetCashActive)  IsAutoGetCashActive  = false;
                if (IsAutoSearchActive)   IsAutoSearchActive   = false;
                if (IsAutoNukeActive)     IsAutoNukeActive     = false;
                if (IsAutoHideActive)     IsAutoHideActive     = false;
                if (IsAutoLightActive)    IsAutoLightActive    = false;
                AppServices.Current.Log.Info("AutoMode",
                    "Pyramid floors 1-2: Auto Combat, Nuke, Rest, Get Items, Get Cash, Search, Hide and Light off until floor 3 (switch one back on to override).");
                return;
            }

            // The master switch went off since: everything stays off, as asked. The
            // switch remembered the toggles as the climb had left them, unticked,
            // so the ones the climb owes back are handed to it and return when it
            // is switched on, not as base modes and not never.
            if (AppServices.Current.AutoModeController.KeepsForSwitchOn("Pyramid run-through over", d =>
                {
                    d.AutoCombat   = _climbTurnedOffCombat;
                    d.AutoNuke     = _climbTurnedOffNuke;
                    d.AutoRest     = _climbTurnedOffRest;
                    d.AutoLight    = _climbTurnedOffLight;
                    d.AutoGetItems = _climbTurnedOffGetItems;
                    d.AutoGetCash  = _climbTurnedOffGetCash;
                    d.AutoHide     = _climbTurnedOffHide;
                    d.AutoSearch   = _climbTurnedOffSearch;
                }))
                return;

            // Sprint, switched on since, keeps its four off; turning one on here
            // would end it. They pass to Sprint, which gives them back when it ends.
            if (IsSprintModeActive)
            {
                _sprintTurnedOffCombat   |= _climbTurnedOffCombat;
                _sprintTurnedOffGetItems |= _climbTurnedOffGetItems;
                _sprintTurnedOffGetCash  |= _climbTurnedOffGetCash;
                _sprintTurnedOffSearch   |= _climbTurnedOffSearch;
            }
            else
            {
                if (_climbTurnedOffCombat)   IsAutoCombatActive   = true;
                if (_climbTurnedOffGetItems) IsAutoGetItemsActive = true;
                if (_climbTurnedOffGetCash)  IsAutoGetCashActive  = true;
                if (_climbTurnedOffSearch)   IsAutoSearchActive   = true;
            }
            if (_climbTurnedOffRest)  IsAutoRestActive  = true;
            if (_climbTurnedOffNuke)  IsAutoNukeActive  = true;
            if (_climbTurnedOffHide)  IsAutoHideActive  = true;
            if (_climbTurnedOffLight) IsAutoLightActive = true;
            AppServices.Current.Log.Info("AutoMode",
                "Pyramid run-through over: the autos the climb switched off are back on.");
        }
        finally
        {
            if (!on)
                _climbTurnedOffCombat = _climbTurnedOffRest = _climbTurnedOffGetItems =
                    _climbTurnedOffGetCash = _climbTurnedOffSearch = _climbTurnedOffNuke =
                    _climbTurnedOffHide = _climbTurnedOffLight = false;
            _climbDrivingEngines = false;
        }
    });

    // ----- Detour combat hold ------------------------------------------------
    // A sell / deposit detour with its "No combat" box ticked turns the real
    // Auto-Combat toggle off once it leaves the loop's rooms, and back on when it's
    // back or over (DetourCombatHold decides) — so a rest still fights to clear the
    // room and the toolbar shows the state. Only a toggle the detour turned off is
    // turned back on.
    private bool _detourTurnedOffCombat;
    private bool _detourDrivingCombat;

    private void OnDetourCombatHold(bool hold) => Dispatcher.UIThread.Post(() =>
    {
        _detourDrivingCombat = true;
        try
        {
            if (hold)
            {
                if (!IsAutoCombatActive) return;
                IsAutoCombatActive = false;
                _detourTurnedOffCombat = true;
            }
            else if (_detourTurnedOffCombat)
            {
                _detourTurnedOffCombat = false;
                // With the master switch off it is the switch's to give back.
                if (!AppServices.Current.AutoModeController.KeepsForSwitchOn("Detour combat hold", d => d.AutoCombat = true))
                    IsAutoCombatActive = true;
            }
        }
        finally { _detourDrivingCombat = false; }
    });

    partial void OnIsAutoNukeActiveChanged(bool value)
    {
        PersistAutoModeFlag("AutoNuke", value, d => d.AutoNuke = value);
        if (!_climbDrivingEngines) _climbTurnedOffNuke = false;
    }

    partial void OnIsAutoRestActiveChanged(bool value)
    {
        PersistAutoModeFlag("AutoRest", value, d => d.AutoRest = value);
        if (!_climbDrivingEngines) _climbTurnedOffRest = false;
        // Mirror the AutoCombat path: a genuine flip must re-evaluate the
        // health engine at once. Toggling off releases a held HP/MA recovery
        // gate immediately (Evaluate clears it) so the walker stops sitting idle
        // mid-rest; toggling on re-asserts and rests now instead of waiting for
        // the next HP-changed event. A profile reseed sets this without a real
        // user toggle — skip then.
        if (_suppressAutoEngineWriteback > 0) return;
        AppServices.Current.Health?.Evaluate();
    }

    partial void OnIsAutoHealActiveChanged(bool value)
    {
        PersistAutoModeFlag("AutoHeal", value, d => d.AutoHeal = value);
        if (_suppressAutoEngineWriteback > 0) return;
        // A heal that's due casts now rather than on the next HP change; and the
        // health engine runs while either switch is on, so it re-decides too.
        AppServices.Current.CastDirector?.Evaluate();
        AppServices.Current.Health?.Evaluate();
    }

    partial void OnIsAutoBlessActiveChanged(bool value)
    {
        PersistAutoModeFlag("AutoBless", value, d => d.AutoBless = value);
        // Mirror OnIsAutoHealActiveChanged: a genuine flip must re-evaluate
        // CastingDirector at once rather than waiting for the next unrelated HP/
        // mana/position/combat event to happen to trigger one — previously
        // enabling Auto Bless could sit doing nothing for an arbitrary stretch
        // (report paradigm-20260824-012300). Evaluate() already gates bless on
        // _autoBlessEnabled internally, so calling it on either transition is
        // safe — a disable just finds nothing eligible to fire.
        if (_suppressAutoEngineWriteback > 0) return;
        AppServices.Current.CastDirector?.Evaluate();
    }

    partial void OnIsAutoLightActiveChanged(bool value)
    {
        PersistAutoModeFlag("AutoLight", value, d => d.AutoLight = value);
        if (!_climbDrivingEngines) _climbTurnedOffLight = false;
    }

    partial void OnIsAutoGetItemsActiveChanged(bool value)
    {
        PersistAutoModeFlag("AutoGetItems", value, d => d.AutoGetItems = value);
        // The auto-discard engine's held hides waited for its switch; switched off,
        // its piles still waiting to be sent come back.
        AppServices.Current.AutoDiscard.OnRulesChanged();
        // An open the auto-open engine still owed is forgotten as the switch goes
        // off, not at the next pack change: by then it may be back on.
        if (!value) AppServices.Current.AutoOpen.Recheck();
        if (!_climbDrivingEngines) _climbTurnedOffGetItems = false;
        MaybeEndSprintOnManualEngineEnable(value);
    }

    partial void OnIsAutoGetCashActiveChanged(bool value)
    {
        PersistAutoModeFlag("AutoGetCash", value, d => d.AutoGetCash = value);
        if (!_climbDrivingEngines) _climbTurnedOffGetCash = false;
        MaybeEndSprintOnManualEngineEnable(value);
    }

    partial void OnIsAutoSneakActiveChanged(bool value)
    {
        PersistAutoModeFlag("AutoSneak", value, d => d.AutoSneak = value);
        // A genuine flip to on sneaks now (when nothing is driving the moves) instead
        // of waiting for the next NPC-free room. A profile reseed isn't one.
        if (value && _suppressAutoEngineWriteback == 0) AppServices.Current.Stealth?.NoteAutoSneakSwitchedOn();
    }

    partial void OnIsAutoHideActiveChanged(bool value)
    {
        PersistAutoModeFlag("AutoHide", value, d => d.AutoHide = value);
        if (!_climbDrivingEngines) _climbTurnedOffHide = false;
    }

    partial void OnIsAutoSearchActiveChanged(bool value)
    {
        PersistAutoModeFlag("AutoSearch", value, d => d.AutoSearch = value);
        if (!_climbDrivingEngines) _climbTurnedOffSearch = false;
        MaybeEndSprintOnManualEngineEnable(value);
    }

    partial void OnIsDisableHangupsActiveChanged(bool value)
        => PersistGeneralFlag("DisableHangups", value, g => g.DisableHangups = value);

    partial void OnIsSprintModeActiveChanged(bool value)
    {
        if (!value) _tripStartedSprint = false;
        PersistGeneralFlag("SprintMode", value, g => g.SprintMode = value);
        // A profile reseed sets this without a real user toggle — skip the engine
        // coupling so loading a Sprint-on character doesn't stomp the engines'
        // own independently-reseeded values.
        if (_suppressAutoEngineWriteback > 0) return;
        if (value) ForceEnginesOffForSprint();
        else RestoreEnginesAfterSprint();
    }

    // Sprint forces Auto-Combat / Get-Items / Search / Get-Cash off for the
    // duration (a "just keep moving" mode has nothing to fight / loot / search
    // for) and remembers which it actually turned off so it can restore exactly
    // those. Guarded so the engines' own change handlers don't read these writes
    // as a manual re-enable.
    private void ForceEnginesOffForSprint()
    {
        _sprintDrivingEngines = true;
        try
        {
            _sprintTurnedOffCombat   = IsAutoCombatActive;
            _sprintTurnedOffGetItems = IsAutoGetItemsActive;
            _sprintTurnedOffSearch   = IsAutoSearchActive;
            _sprintTurnedOffGetCash  = IsAutoGetCashActive;
            if (IsAutoCombatActive)   IsAutoCombatActive   = false;
            if (IsAutoGetItemsActive) IsAutoGetItemsActive = false;
            if (IsAutoSearchActive)   IsAutoSearchActive   = false;
            if (IsAutoGetCashActive)  IsAutoGetCashActive  = false;
        }
        finally { _sprintDrivingEngines = false; }
    }

    // Turn back on exactly the engines Sprint forced off. One the user re-enabled
    // by hand was never in the turned-off set, so it's left as the user set it.
    private void RestoreEnginesAfterSprint()
    {
        _sprintDrivingEngines = true;
        try
        {
            // With the master switch off they are the switch's to give back.
            if (AppServices.Current.AutoModeController.KeepsForSwitchOn("Sprint Mode ended", d =>
                {
                    d.AutoCombat   = _sprintTurnedOffCombat;
                    d.AutoGetItems = _sprintTurnedOffGetItems;
                    d.AutoSearch   = _sprintTurnedOffSearch;
                    d.AutoGetCash  = _sprintTurnedOffGetCash;
                }))
                return;
            if (_sprintTurnedOffCombat)   IsAutoCombatActive   = true;
            if (_sprintTurnedOffGetItems) IsAutoGetItemsActive = true;
            if (_sprintTurnedOffSearch)   IsAutoSearchActive   = true;
            if (_sprintTurnedOffGetCash)  IsAutoGetCashActive  = true;
        }
        finally
        {
            _sprintTurnedOffCombat = _sprintTurnedOffGetItems =
                _sprintTurnedOffSearch = _sprintTurnedOffGetCash = false;
            _sprintDrivingEngines = false;
        }
    }

    // Sprint Mode and its four suppressed engines are mutually exclusive: turning
    // any of them back on by hand ends Sprint. Ending Sprint restores the others
    // it silenced; the one just re-enabled is already on, so restore leaves it on.
    // Ignored while Sprint itself is driving the engine and during a profile reseed.
    private void MaybeEndSprintOnManualEngineEnable(bool value)
    {
        if (!value || _sprintDrivingEngines || _suppressAutoEngineWriteback > 0) return;
        if (IsSprintModeActive) IsSprintModeActive = false;
    }

    // Re-decide combat for the room we're standing in: the Combat gate, then the room
    // roster re-emitted so CombatManager picks a target from it now rather than on some
    // later, unrelated room observation (see OnIsAutoCombatActiveChanged).
    private static void ReevaluateCombatForCurrentRoom()
    {
        AppServices.Current.CombatTracker?.OnAutoAttackChanged();
        AppServices.Current.RoomClassifier?.ReemitCurrent();
    }

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
    // live actually changed) is enough — except combat, which only decides on a room
    // observation: a reset that turns it back on re-decides the room we're in, or a
    // monster already here goes unfought (report paradigm-20260930-084435: a walk-to
    // ended in a room with a large mummy, combat came back on, and nothing attacked).
    private void ReconcileAutoModeToBase(string reason)
    {
        if (AppServices.Current.Profile.Current is not { } profile) return;
        bool combatWas = ReadGeneralFromProfile(profile).AutoMode.AutoCombat;
        // The switch decides and writes: with it off nothing is settled, and
        // already settled writes nothing.
        if (AppServices.Current.AutoModeController.ReconcileToBase(reason) is not { } result) return;

        if (result.LiveChanged)
        {
            SyncAutoEngineTogglesFromProfile();
            AppServices.Current.Log.Info("AutoMode",
                $"Auto-engines reset to base modes ({reason}).");
            if (result.Live.AutoCombat != combatWas)
            {
                ReevaluateCombatForCurrentRoom();
                AppServices.Current.Log.Info("AutoMode",
                    $"Auto-combat {(result.Live.AutoCombat ? "on" : "off")} after the reset — combat re-decided for the current room.");
            }
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
            IsSprintModeActive   = general.SprintMode;
            IsAutoCombatActive   = am.AutoCombat;
            IsAutoNukeActive     = am.AutoNuke;
            IsAutoHealActive     = am.AutoHeal;
            IsAutoRestActive     = am.AutoRest;
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

    // Open InfoDialogs are tracked per title so a menu / hotkey re-press reuses the existing one.
    private readonly Dictionary<string, InfoDialog> _infoDialogs = new(StringComparer.Ordinal);

    private void ShowInfoDialog(string title, string body)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        // Each title gets its own tracker.
        if (_infoDialogs.TryGetValue(title, out InfoDialog? existing))
        {
            RaiseOrClose(existing);
            return;
        }

        InfoDialog dlg = new();
        dlg.Configure(title, body);
        dlg.Closed += (_, _) => _infoDialogs.Remove(title);
        _infoDialogs[title] = dlg;
        dlg.Show(main);
    }

}
