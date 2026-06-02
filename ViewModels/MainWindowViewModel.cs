using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Input;
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
    private CancellationTokenSource? _cleanupReconnectCts;
    // Counts reactive reconnect arms in a row (carrier-lost / no-response).
    // Resets to 0 on a successful Connect or any user-initiated disconnect /
    // connect — so the (REDIAL m/N) hint in the armed status only reflects
    // the current uninterrupted disconnect cycle.
    private int _reactiveReconnectCount;
    // GC root for the who-list parser — it subscribes to LineExtractor
    // in its ctor and stays alive as long as MainWindowViewModel does.
    private readonly Game.WhoListParser _whoListParser;
    // GC root for the look-on-player parser — sibling to the who-list
    // parser; populates race / class / equipment from `look <player>`.
    private readonly Game.LookParser _lookParser;

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
    /// Live mirror of the Global-tier toolbar visibility settings.
    /// Each toolbar Button in the XAML binds its <c>IsVisible</c> to a
    /// property on this so edits in Settings → Toolbar apply
    /// immediately on Apply / OK.
    /// </summary>
    public Services.ToolbarConfig Toolbar => AppServices.Current.Toolbar;

    /// <summary>
    /// Render-ready view-models for the dynamic toolbar
    /// <c>ItemsControl</c>. Mirrors <see cref="ToolbarConfig.Layout"/>;
    /// each entry resolves through
    /// <see cref="ToolbarItemCatalogue"/> and binds against the matching
    /// command on this view-model. Rebuilt whenever <c>Layout</c>
    /// changes (Settings → Toolbar Apply path).
    /// </summary>
    public ObservableCollection<ToolbarButtonItem> ToolbarItems { get; } = new();

    /// <summary>
    /// File → Quick Connect target. Wins over <see cref="ResolveActiveBbs"/>
    /// once set; cleared when the user picks a (different) BBS via
    /// Settings → BBS, or when a new profile loads.
    /// </summary>
    private (string Host, int Port)? _quickConnectTarget;

    /// <summary>
    /// Tracks the BBS pin observed on the last profile-mutation event so we
    /// can detect when the user actually changed BBS (vs. tweaked an
    /// unrelated setting). Used to drop <see cref="_quickConnectTarget"/>.
    /// </summary>
    private string? _lastSeenBbsName;

    /// <summary>
    /// Host the active connection target resolves to. Quick Connect wins
    /// over the saved BBS pin when set; otherwise the user's BBS Host
    /// stands in.
    /// </summary>
    public string Host => _quickConnectTarget?.Host ?? ResolveActiveBbs()?.Host ?? string.Empty;

    /// <summary>Port the active connection target resolves to. <c>0</c> when nothing is configured.</summary>
    public int Port => _quickConnectTarget?.Port ?? ResolveActiveBbs()?.Port ?? 0;

    /// <summary>
    /// Name of the dial target — Quick Connect's <c>host:port</c> when
    /// active, otherwise the active BBS's display name (or <c>null</c>).
    /// Consumed by the title bar and the connect-status banner.
    /// </summary>
    public string? ActiveBbsName => _quickConnectTarget is { } qc
        ? $"Quick Connect: {qc.Host}:{qc.Port}"
        : ResolveActiveBbs()?.Name;

    /// <summary>
    /// Optional URL field on the active BBS's <see cref="BbsProfile.WebsiteUrl"/>.
    /// Drives the Help → BBS site menu item's enable state + the actual launch.
    /// Quick Connect targets have no website (Quick Connect bypasses the
    /// BBS profile store entirely), so this is <c>null</c> in that case.
    /// </summary>
    public string? BbsWebsiteUrl => _quickConnectTarget is null
        ? ResolveActiveBbs()?.WebsiteUrl
        : null;

    /// <summary>True when <see cref="BbsWebsiteUrl"/> looks launch-able — gates the Help menu item.</summary>
    public bool HasBbsWebsite => !string.IsNullOrWhiteSpace(BbsWebsiteUrl);

    /// <summary>
    /// Window title — "FujinTerm — {profile} — {bbs}". When no profile
    /// is loaded the placeholder <c>{default}</c> stands in; when no
    /// BBS is selected <c>{No BBS}</c> stands in. Both slots always
    /// render so the title bar shape stays consistent.
    /// </summary>
    public string WindowTitle
    {
        get
        {
            string profile = AppServices.Current.Profile.CurrentProfileName ?? "{default}";
            string bbs     = ActiveBbsName ?? "{No BBS}";
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
    [NotifyPropertyChangedFor(nameof(IsReconnectPending))]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel))]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusText))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(IsReconnectPending))]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel))]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusText))]
    private bool _isConnecting;

    public bool IsDisconnected => !IsConnected;

    /// <summary>True when there is no active connection AND no connect attempt in flight.</summary>
    public bool IsIdle => !IsConnected && !IsConnecting;

    /// <summary>
    /// True when an auto-reconnect is armed but hasn't fired yet — covers
    /// both the predictive cleanup scheduler and the reactive carrier-lost
    /// path. Drives the toolbar / menu's Connect label so a re-press
    /// cancels the pending redial instead of immediately dialling.
    /// </summary>
    public bool IsReconnectPending
        => _cleanupReconnectCts is not null && !IsConnected && !IsConnecting;

    /// <summary>
    /// Header text for the single Connect ↔ Disconnect menu entry / button
    /// tooltip. Four-state cycle: ReconnectPending → "Cancel reconnect" →
    /// Idle → "Connect" → Connecting → "Cancel connect" → Connected →
    /// "Disconnect".
    /// </summary>
    public string ConnectionLabel
        => IsConnected ? "Disconnect"
         : IsConnecting ? "Cancel connect"
         : IsReconnectPending ? "Cancel reconnect"
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
    /// Per-attempt socket timeout. The OS default (~75s on Linux for
    /// unreachable hosts) is far too long for a BBS client. Could be a
    /// per-BBS knob in a future PR; constant for now since most BBSes
    /// behave similarly on this dimension.
    /// </summary>
    private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Why the most recent connection ended. Drives the reactive
    /// reconnect decision (<see cref="BbsProfile.ReconnectOnFailedConnect"/> /
    /// <see cref="BbsProfile.ReconnectOnCarrierLost"/>) and is reset on
    /// every successful new connection.
    /// </summary>
    private enum DisconnectCause
    {
        /// <summary>No disconnect this session (initial state or just reset).</summary>
        None,
        /// <summary>User clicked Disconnect — never auto-retry.</summary>
        UserInitiated,
        /// <summary>Initial dial threw or timed out before reaching the BBS.</summary>
        FailedConnect,
        /// <summary>Connected session ended without our initiation — server-side drop.</summary>
        CarrierLost,
        /// <summary>Socket died after a long quiet stretch — TCP keepalive caught a hung server.</summary>
        NoResponse,
    }

    private DisconnectCause _lastDisconnectCause = DisconnectCause.None;
    private bool _userInitiatedDisconnect;

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
        AppServices.Current.Profile.ProfileLoaded += OnProfileLoadedForConnect;
        AppServices.Current.Profile.ProfileClosed += () => { ClearQuickConnect(); SyncProfileMenuState(); RefreshBbsBindings(); };
        // ProfileMutated fires from BbsSectionViewModel.Apply after the
        // BBS pin has been stamped onto the profile — works for both
        // named profiles and unsaved drafts (Save no-ops on drafts but
        // the mutation signal still fires).
        AppServices.Current.Profile.ProfileMutated += _ => OnProfileMutatedForBbs();
        AppServices.Current.Profile.BbsPinApplied += _ => { ClearQuickConnect(); RefreshBbsBindings(); };

        // Seed the BBS-pin sentinel so OnProfileMutatedForBbs can detect
        // the first real change against a known baseline.
        _lastSeenBbsName = ResolveActiveBbs()?.Name;

        // Cleanup-warning banner: when the BBS announces nightly shutdown
        // on the wire, drop a yellow banner into the terminal canvas so
        // the user knows to type `quit` at a safe room. The auto-reconnect
        // schedule is armed later, on the Disconnected event.
        AppServices.Current.Cleanup.WarningObserved += OnCleanupWarningObserved;

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

        // who-list observer: subscribes to LineExtractor on its own
        // (the table is multi-line — needs state, doesn't fit
        // MessageRouter's stateless dispatch). Feeds every observed
        // player into PlayerDatabase.
        _whoListParser = new Game.WhoListParser(Lines, AppServices.Current.Players, AppServices.Current.Log);
        _lookParser    = new Game.LookParser   (Lines, AppServices.Current.Players, AppServices.Current.Log);

        // PartyManager lives at AppServices level (so the @-command engine
        // and PartyWindow can grab a stable reference), but its par-block
        // state machine needs the per-session LineExtractor. Same wiring
        // shape as TriggerEngine.AttachLineExtractor.
        AppServices.Current.Party.AttachLineExtractor(Lines);
        // Phase 6 PR 6.5 — auto-invite on reconnect needs a wire-sender
        // to send "invite <name>" when a disconnected member returns
        // within the grace window AND we're the party leader.
        AppServices.Current.Party.SetWireSender(SendUserInput);

        // Macro dispatcher needs a wire-send callback before it can fire.
        // TerminalControl + ConversationWindow's input both call into the
        // dispatcher on KeyDown — without a sender bound, the call returns
        // false and the keystroke falls through to normal handling.
        AppServices.Current.MacroDispatcher.SetSender(SendUserInput);

        // Trigger engine subscribes to the LineExtractor for game-message
        // dispatch (chat + system-log subscriptions wired in its ctor) and
        // borrows the same wire sender so a fired trigger's Response goes
        // through the canonical SendUserInput path.
        AppServices.Current.Triggers.AttachLineExtractor(Lines);
        AppServices.Current.Triggers.SetSender(SendUserInput);

        // Remote-command engine borrows the same wire-sender so a handler's
        // ctx.Reply(text) routes through SendUserInput exactly like a
        // typed command would. The Phase 6 PR 6.3 PartyEssentialHandlers
        // also need their own copy for the @party <sub> → local-command
        // relay (uses the wire-sender directly, bypassing ctx.Reply).
        AppServices.Current.RemoteCommands.SetWireSender(SendUserInput);
        AppServices.Current.PartyEssentials.SetWireSender(SendUserInput);
        // Phase 6 PR 6.4 — poller needs the same wire-sender to send
        // @health round-trip requests and the periodic par poll.
        AppServices.Current.PartyPoller.SetWireSender(SendUserInput);
        // Phase 6 PR 6.7 — emit @wait when we start resting and @ok
        // when we finish, so the party leader's pause-gate can react.
        AppServices.Current.PartyRest.SetWireSender(SendUserInput);
        // Phase 6 PR 6.8 — Auto-Exp-Reset + future panic / kill
        // broadcasts go through PartyBroadcaster.
        AppServices.Current.PartyBroadcaster.SetWireSender(SendUserInput);
        // AutoPartyManager — consumes per-player InviteToPartyIfSeen
        // and JoinPartyIfInvited flags, sends `invite <given>` and
        // `follow <given>` over the wire.
        AppServices.Current.AutoParty.SetWireSender(SendUserInput);
        // HangupHandler — sends the configured GameExitCommand when
        // an authorised sender telepaths @hangup.
        AppServices.Current.Hangup.SetWireSender(SendUserInput);
        // MainMenuEntryAutomation — same sender; armed below when
        // LoginAutomator's LoggedIntoGame fires (only point in the
        // session where the entry command is allowed to auto-fire).
        AppServices.Current.MainMenuEntry.SetWireSender(SendUserInput);

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
            if (t is not null) _ = t.SendAsync(bytes);
        };

        // Build the dynamic toolbar items now, then rebuild whenever the
        // user reorders / adds / removes via Settings → Toolbar (which
        // mutates Toolbar.Layout on Apply).
        RebuildToolbarItems();
        Toolbar.Layout.CollectionChanged += (_, _) => RebuildToolbarItems();
        PropertyChanged += SyncToolbarStateFlags;
    }

    /// <summary>
    /// Walks <see cref="ToolbarConfig.Layout"/> and rebuilds
    /// <see cref="ToolbarItems"/>. Each <c>Button</c> row is resolved
    /// through <see cref="ToolbarItemCatalogue"/>; the command property
    /// is fetched by reflection from the catalogue's
    /// <c>CommandName</c> so adding a new toolbar action is a one-line
    /// catalogue entry. Unknown action ids are skipped.
    /// </summary>
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

    /// <summary>Mirrors current connection / capture state onto matching toolbar rows.</summary>
    private void SyncToolbarStateFlags(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsConnected)
         && e.PropertyName != nameof(IsConnecting)
         && e.PropertyName != nameof(IsDumping)) return;

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
        }
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

    private async Task DisconnectInternalAsync()
    {
        // Mark the user-initiated nature BEFORE we close the socket, so
        // the Disconnected event handler (which races with the await
        // below) can distinguish this from a server-side drop and skip
        // the carrier-lost auto-reconnect.
        _userInitiatedDisconnect = true;
        _lastDisconnectCause = DisconnectCause.UserInitiated;
        _reactiveReconnectCount = 0;

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

        // Per-BBS retry knobs. ReconnectOnFailedConnect gates the loop:
        // when off, the user gets one shot and we surface the error — no
        // silent retries. When on, the loop runs up to MaxRedials with
        // RedialPauseSeconds between attempts. Defaults fall through to
        // a 1-attempt floor if a BBS has bogus values.
        BbsProfile? activeBbs = ResolveActiveBbs();
        int maxAttempts = (activeBbs?.ReconnectOnFailedConnect ?? false)
            ? Math.Max(1, activeBbs?.MaxRedials ?? 1)
            : 1;
        TimeSpan retryDelay = TimeSpan.FromSeconds(Math.Max(1, activeBbs?.RedialPauseSeconds ?? 5));

        _connectCts = new CancellationTokenSource();
        IsConnecting = true;
        try
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (_connectCts.IsCancellationRequested) break;

                WriteTerminalStatus($"[CONNECTING TO: {Host} {Port}]", TerminalStatusKind.Notice);
                AppServices.Current.Log.Info("Connect",
                    $"Connecting to {Host}:{Port} (attempt {attempt}/{maxAttempts})…");

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
            // Arm the main-menu-entry automation NOW — the BBS-login
            // sequence just completed, so we expect the MajorMUD main
            // menu to render shortly. If it does, the engine sends the
            // configured GameEntryCommand. If it doesn't render in the
            // arm window, the latch closes — protects against in-game
            // chat that happens to look like the menu line.
            AppServices.Current.MainMenuEntry.Arm();
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

    // ----- Cleanup-warning auto-reconnect ------------------------------

    private void OnCleanupWarningObserved(CleanupWarning warning)
    {
        Dispatcher.UIThread.Post(() =>
        {
            WriteTerminalStatus(
                $"[CLEANUP WARNING — BBS GOES DOWN IN {warning.MinutesRemaining} MIN — QUIT AT A SAFE ROOM TO ARM AUTO-RECONNECT.]",
                TerminalStatusKind.Notice);
            AppServices.Current.Log.Warn("Cleanup",
                $"Server announced shutdown in {warning.MinutesRemaining} minute(s) at {warning.ObservedAt.LocalDateTime:HH:mm:ss}.");
        });
    }

    /// <summary>
    /// On disconnect, if a cleanup warning was observed during this
    /// session AND the active BBS has <see cref="BbsProfile.ReconnectAfterCleanup"/>
    /// enabled, arm a one-shot reconnect at the moment we think the BBS
    /// is back online. Formula:
    /// <code>
    /// shutdown_at = warning_observed_at + warning_minutes_remaining
    /// reconnect_at = max(now, shutdown_at) + BBS.CleanupPeriodMinutes
    /// </code>
    /// Handles both the clean-quit-before-shutdown case (we take the
    /// long path: full warning countdown + user-set cleanup duration)
    /// and the dirty-shutdown case (the <c>max</c> collapses to
    /// <c>now + cleanup</c>, since shutdown_at has already passed).
    /// </summary>
    private void TryScheduleCleanupReconnect()
    {
        CleanupWarning? maybeWarning = AppServices.Current.Cleanup.Latest;
        if (maybeWarning is not { } warning) return;

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

        _ = Task.Delay(delay, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (IsConnected || IsConnecting) return;
                _cleanupReconnectCts?.Dispose();
                _cleanupReconnectCts = null;
                NotifyReconnectPendingChanged();
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
        if (reason is not null)
        {
            AppServices.Current.Log.Info("Cleanup", $"Auto-reconnect cancelled — {reason}.");
            WriteTerminalStatus("[AUTO-RECONNECT CANCELLED.]", TerminalStatusKind.Notice);
        }
    }

    /// <summary>
    /// Fires PropertyChanged for the bindings that depend on whether a
    /// reconnect is armed. Called from every site that flips the CTS
    /// between null and an instance so the toolbar / menu label updates.
    /// </summary>
    private void NotifyReconnectPendingChanged()
    {
        OnPropertyChanged(nameof(IsReconnectPending));
        OnPropertyChanged(nameof(ConnectionLabel));
    }

    /// <summary>
    /// Distinguish a server-side carrier drop from a TCP-keepalive
    /// timeout. The connection was alive; now the socket died. If
    /// keepalive was enabled on the active BBS AND the wire was silent
    /// for longer than the configured idle window before the drop,
    /// attribute to <see cref="DisconnectCause.NoResponse"/>; otherwise
    /// <see cref="DisconnectCause.CarrierLost"/>. The threshold gets a
    /// small grace (+5s) so a server that responded just before the
    /// idle window closes doesn't get mis-classified as silent.
    /// </summary>
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

    /// <summary>
    /// Arm a reactive reconnect if the relevant <see cref="BbsProfile"/>
    /// toggle matches <see cref="_lastDisconnectCause"/>. Shares
    /// <see cref="_cleanupReconnectCts"/> with the predictive cleanup
    /// scheduler so only one reconnect can be pending at a time. Never
    /// fires for <see cref="DisconnectCause.UserInitiated"/> regardless
    /// of any toggle state — that's the "user said no, don't dial back"
    /// safeguard.
    /// </summary>
    private void TryScheduleReactiveReconnect()
    {
        BbsProfile? bbs = ResolveActiveBbs();
        if (bbs is null) return;

        // FailedConnect is fully handled inside ConnectWithRetriesAsync
        // (its retry-loop IS the response to ReconnectOnFailedConnect);
        // UserInitiated never auto-retries by policy. That leaves
        // CarrierLost / NoResponse — each gated on its own toggle.
        bool shouldRetry = _lastDisconnectCause switch
        {
            DisconnectCause.CarrierLost => bbs.ReconnectOnCarrierLost,
            DisconnectCause.NoResponse  => bbs.ReconnectOnNoResponse,
            _ => false,
        };
        if (!shouldRetry) return;

        // Redial budget — same MaxRedials knob the in-flight retry loop
        // uses. Stop arming once we've burned through it; the user can
        // still click Connect manually.
        int maxRedials = Math.Max(1, bbs.MaxRedials);
        _reactiveReconnectCount++;
        if (_reactiveReconnectCount > maxRedials)
        {
            WriteTerminalStatus(
                $"[AUTO-RECONNECT GAVE UP AFTER {maxRedials} REDIAL{(maxRedials == 1 ? "" : "S")}.]",
                TerminalStatusKind.Error);
            AppServices.Current.Log.Error("Reconnect",
                $"Reactive reconnect budget exhausted ({maxRedials}).");
            _reactiveReconnectCount = 0;
            return;
        }

        TimeSpan delay = TimeSpan.FromSeconds(Math.Max(1, bbs.RedialPauseSeconds));
        _cleanupReconnectCts?.Cancel();
        _cleanupReconnectCts?.Dispose();
        _cleanupReconnectCts = new CancellationTokenSource();
        NotifyReconnectPendingChanged();
        CancellationToken token = _cleanupReconnectCts.Token;

        string reasonLabel = _lastDisconnectCause == DisconnectCause.NoResponse
            ? "no response"
            : "carrier lost";
        WriteTerminalStatus(
            $"[AUTO-RECONNECT ARMED ({reasonLabel.ToUpperInvariant()}, REDIAL {_reactiveReconnectCount}/{maxRedials}) — DIALING IN {(int)delay.TotalSeconds}s. PRESS CONNECT TO CANCEL.]",
            TerminalStatusKind.Notice);
        AppServices.Current.Log.Info("Reconnect",
            $"Reactive reconnect scheduled ({reasonLabel}, redial {_reactiveReconnectCount}/{maxRedials}) in {(int)delay.TotalSeconds}s.");

        _ = Task.Delay(delay, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (IsConnected || IsConnecting) return;
                _cleanupReconnectCts?.Dispose();
                _cleanupReconnectCts = null;
                NotifyReconnectPendingChanged();
                _ = ConnectWithRetriesAsync();
            });
        }, TaskScheduler.Default);
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
    }

    /// <summary>
    /// ProfileLoaded handler that wires in the Settings → General
    /// "Auto-connect when profile loads" toggle. Runs the original
    /// post-load refresh chain first, then — only when not already
    /// connected/connecting, the loaded profile has a usable BBS pin,
    /// and the GeneralSettings.AutoConnect flag is on — kicks off
    /// <see cref="ConnectWithRetriesAsync"/>.
    /// </summary>
    /// <remarks>
    /// async void is intentional: ProfileLoaded is an Action&lt;CharacterProfile&gt;
    /// event and we want fire-and-forget on the connect attempt so the
    /// caller (typically File → Open profile) doesn't block on the
    /// retry loop. The connect path already self-marshals UI updates
    /// and never throws to the caller.
    /// </remarks>
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

    /// <summary>
    /// ProfileMutated runs for every settings tab's Apply. We only want
    /// to drop the Quick Connect override when the BBS pin itself
    /// changed — display / toolbar / statline edits shouldn't kick the
    /// user off a quick-dialled target.
    /// </summary>
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

    /// <summary>
    /// Drops the Quick Connect override and pushes the BBS-derived
    /// bindings back into the title bar / connect button.
    /// </summary>
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
                IsConnected = true;
                // Fresh session — drop any cleanup warning carried over,
                // clear any pending auto-reconnect schedule, and reset the
                // reactive-redial counter so the next disconnect cycle
                // starts from 1/N again.
                AppServices.Current.Cleanup.Reset();
                _reactiveReconnectCount = 0;
                CancelCleanupReconnect("connected");
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

                // Categorise: if the user clicked Disconnect, the flag was
                // set in DisconnectInternalAsync. Otherwise distinguish a
                // server-side carrier drop from a TCP keepalive timeout by
                // looking at how long the wire was silent before the drop:
                // long silence + keepalive-enabled implies the OS's probe
                // train detected an unresponsive server.
                if (_userInitiatedDisconnect)
                {
                    _userInitiatedDisconnect = false;
                    _lastDisconnectCause = DisconnectCause.UserInitiated;
                }
                else if (wasConnected)
                {
                    _lastDisconnectCause = ClassifyServerSideDrop();
                }

                // Predictive scheduler first (cleanup warning gives a
                // deterministic reconnect-at). Reactive only fires if
                // predictive didn't arm anything.
                TryScheduleCleanupReconnect();
                if (_cleanupReconnectCts is null) TryScheduleReactiveReconnect();
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
    /// Bridge for the Settings window's Statline tab: pushes a single
    /// string to the BBS as Latin-1 bytes, returns whether the send
    /// could even be attempted (i.e. we have a live socket).
    /// </summary>
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

        // Alias check first — first-word match, case-insensitive. When
        // an enabled alias's name matches, the engine returns the
        // multi-step expansion + we send each step in place of the raw
        // text. No match → fall through to the verbatim send below.
        // This is the only surface aliases fire from today; the
        // terminal canvas is char-by-char and would need client-side
        // line-mode to participate — explicitly out of scope for now.
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

    /// <summary>Singleton handle for the live PartyWindow — re-press toggles closed (CLAUDE.md window rule).</summary>
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
        ProfileService profile = AppServices.Current.Profile;
        FujinTerm.ViewModels.Profile.ProfilePickerDialogViewModel vm =
            new(profile.ListNames());

        string? name = await AppServices.Current.Dialogs.OpenWindowAsync<
            FujinTerm.ViewModels.Profile.ProfilePickerDialogViewModel, string>(vm);
        if (string.IsNullOrEmpty(name)) return;

        try
        {
            profile.Load(name);
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

        FujinTerm.ViewModels.Profile.ProfileNameInputDialogViewModel vm = new(
            suggestedName: profile.CurrentProfileName ?? "character",
            exists:        profile.Exists);

        string? name = await AppServices.Current.Dialogs.OpenWindowAsync<
            FujinTerm.ViewModels.Profile.ProfileNameInputDialogViewModel, string>(vm);
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

    /// <summary>
    /// Singleton-ish handle to the Quick Connect window so re-press of
    /// the menu / hotkey toggles it closed (per CLAUDE.md).
    /// </summary>
    private QuickConnectWindow? _quickConnect;

    /// <summary>File → Quick Connect. Modeless dialog; on commit the host/port becomes the connect target.</summary>
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
            DataContext = new SettingsWindowViewModel(
                svc.Profile, svc.Log,
                sendText: SendTextFromSettings,
                initialSectionId: sectionId),
        };
        window.Closed += (_, _) => _settings = null;
        _settings = window;
        window.Show(main);
    }

    /// <summary>
    /// Singleton-ish handle to the Game Data Browser. Re-press of the
    /// command / hotkey toggles it closed (per CLAUDE.md).
    /// </summary>
    private FujinTerm.Views.GameData.GameDataBrowserWindow? _gameDataBrowser;

    [RelayCommand]
    private void OpenGameDataBrowser() => ShowGameDataBrowser(initialSectionId: null);

    [RelayCommand] private void OpenGameDataPlayers()  => ShowGameDataBrowser("players");
    [RelayCommand] private void OpenGameDataMacros()   => ShowGameDataBrowser("macros");
    [RelayCommand] private void OpenGameDataTriggers() => ShowGameDataBrowser("triggers");
    [RelayCommand] private void OpenGameDataAliases()  => ShowGameDataBrowser("aliases");

    /// <summary>
    /// Open the Game Data Browser, optionally pre-selected to a named
    /// section. Toggles per the standard window-command rule
    /// (CLAUDE.md): when the browser is already open re-press behavior
    /// depends on what was requested —
    /// <list type="bullet">
    ///   <item>no section requested (toolbar / Ctrl+G) → close it.</item>
    ///   <item>section requested AND already showing → close it.</item>
    ///   <item>section requested AND different from current → switch
    ///     the existing window's section, activate, do not respawn.</item>
    /// </list>
    /// Switching in place beats closing-and-respawning because it
    /// preserves search state, scroll position, and any per-section
    /// VM caches the user has primed.
    /// </summary>
    private void ShowGameDataBrowser(string? initialSectionId)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        if (_gameDataBrowser is { } existing)
        {
            if (initialSectionId is null
                || (existing.DataContext is FujinTerm.ViewModels.GameData.GameDataBrowserViewModel vm
                    && string.Equals(vm.SelectedSection?.Id, initialSectionId, StringComparison.OrdinalIgnoreCase)))
            {
                existing.Close();
                return;
            }

            if (existing.DataContext is FujinTerm.ViewModels.GameData.GameDataBrowserViewModel vmRoute)
            {
                FujinTerm.ViewModels.GameData.GameDataSectionViewModel? target =
                    vmRoute.Sections.FirstOrDefault(s => string.Equals(s.Id, initialSectionId, StringComparison.OrdinalIgnoreCase));
                if (target is not null) vmRoute.SelectedSection = target;
                existing.Activate();
                return;
            }

            existing.Close();
            return;
        }

        FujinTerm.Views.GameData.GameDataBrowserWindow window = new()
        {
            DataContext = new FujinTerm.ViewModels.GameData.GameDataBrowserViewModel(
                AppServices.Current.GameData,
                AppServices.Current.Triggers,
                AppServices.Current.Aliases,
                AppServices.Current.Players,
                AppServices.Current.Macros,
                AppServices.Current.Messages,
                AppServices.Current.MonsterMessages,
                AppServices.Current.MonsterOverlaySeed,
                AppServices.Current.ItemOverlaySeed,
                AppServices.Current.Resolver,
                AppServices.Current.Dialogs,
                AppServices.Current.Keybindings,
                initialSectionId),
        };
        window.Closed += (_, _) => _gameDataBrowser = null;
        _gameDataBrowser = window;
        window.Show(main);
    }

    /// <summary>
    /// Items bound to File → Game Data → Active set. Each entry has a
    /// checkbox-style header (checked = currently active set) and a
    /// command that flips <see cref="GameDataCache.ActiveSet"/> + writes
    /// the resolved BBS's <see cref="BbsProfile.ActiveGameDataSet"/>
    /// field (falling back to <c>GlobalSettings.DefaultGameDataSet</c>
    /// when no BBS is pinned).
    /// </summary>
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

    /// <summary>
    /// Flip the active set and persist the user's choice. Active set is
    /// a BBS-scoped setting (every character on the same realm shares
    /// the same MajorMUD MDB); we write to the resolved BBS profile
    /// when one is pinned, else fall through to global settings so the
    /// menu still works before any BBS is configured.
    /// </summary>
    private void SwitchActiveGameDataSet(string setName)
    {
        AppServices.Current.GameData.SwitchSet(setName);

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

    /// <summary>
    /// File → Game Data → Import .mdb… — picks an Access database,
    /// runs the Phase 5 PR 5.1 importer, switches to the new set
    /// on success.
    /// </summary>
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
            SwitchActiveGameDataSet(result.FolderName);
        }
        else
        {
            WriteTerminalStatus("[MDB IMPORT FAILED — see Program Log]", TerminalStatusKind.Error);
        }
    }

    /// <summary>
    /// Compose the terminal-status line for a successful MDB import.
    /// Carries entry + table totals plus a format-tag derived from the
    /// MajorMUD MDB shape: 9 user tables = old realm format, 10 = new
    /// format. Anything else (or any per-table skips) flips the line
    /// red so the user notices the structural drift.
    /// </summary>
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

    [RelayCommand]
    private void OpenMajorMudWiki() => ShellLaunch.OpenUrl(AppInfo.MajorMudWikiUrl);

    [RelayCommand]
    private void OpenMajorMudReddit() => ShellLaunch.OpenUrl(AppInfo.MajorMudRedditUrl);

    [RelayCommand]
    private void OpenMudInfo() => ShellLaunch.OpenUrl(AppInfo.MudInfoUrl);

    /// <summary>
    /// Help → BBS site. Opens the active BBS's <see cref="BbsProfile.WebsiteUrl"/>
    /// in the OS default browser. Silently no-ops when no URL is set —
    /// the menu item's <see cref="HasBbsWebsite"/> binding keeps it
    /// disabled in that state, but we guard here too in case the user
    /// triggered it some other way.
    /// </summary>
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
              • JetDatabaseReader           — MIT (Phase 5 MDB import)

            Other dependencies arrive with their respective phases; their
            licenses will appear here once they're added.
            """);

    // ----- Live-bound input-gesture labels for menu items ---------------
    // Each property reads the current chord for one BuiltInAction.
    // Refreshed in bulk by RefreshKeybindLabels() when KeybindingStore
    // fires BindingChanged. The XAML menu items bind their InputGesture
    // to these — so rebinding through the context-menu editor updates
    // every menu's shortcut display immediately.

    public string ConversationGesture     => GetGesture(Models.Profile.BuiltInAction.OpenConversation);
    public string PartyGesture            => GetGesture(Models.Profile.BuiltInAction.OpenParty);
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

    /// <summary>Fire PropertyChanged for every *Gesture label so menus refresh after a rebind.</summary>
    private void RefreshKeybindLabels()
    {
        OnPropertyChanged(nameof(ConversationGesture));
        OnPropertyChanged(nameof(PartyGesture));
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

    /// <summary>Help → About FujinTerm.</summary>
    [RelayCommand]
    private void OpenAbout()
        => ShowInfoDialog("About FujinTerm",
            $"""
            {AppInfo.DisplayName}
            A modern Avalonia BBS terminal client with MajorMUD-aware features.

            Source: {AppInfo.RepoUrl}
            License: MIT — see LICENSE in the project root for the full text.

            Built on .NET 10 + Avalonia 12 (CommunityToolkit.Mvvm source-gen).
            Bundles JetDatabaseReader 2.2.0 (MIT) for pure-managed .mdb / .accdb
            imports without Wine or mdbtools.

            Help → Licenses… lists every third-party component shipped in
            this build.
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
