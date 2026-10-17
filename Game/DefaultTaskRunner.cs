using System;
using System.Text.Json;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game;

// Runs the character's Settings → General "Default task" once per game entry.
// DoNothing is the historical behaviour (leave the client idle); BeginLoop and
// BeginAutoLair start the configured loop / lair the moment the character is
// confirmed in-game — the same effect as hitting Run on that loop / lair in the
// Navigation window, closest-waypoint routing included.
//
// Fire trigger: we need BOTH signals of "in the game world" before starting —
//   * the first WirePromptScanner.PromptObserved (the canonical "we're in
//     MajorMUD" marker), AND
//   * a known current room (RoomTracker.State.CurrentRoom != null) so the runner
//     routes from where we actually are rather than expanding from waypoint 0.
// MajorMUD prints the room before the prompt, so the room is normally known by
// the first prompt; we still gate on both and fire on whichever lands second to
// stay correct if the ordering ever slips.
//
// Party-rebuild hold: when this game entry is a reconnect (a prior in-session
// disconnect) AND we were in a party before that drop, we hold for the
// PartyManager.DisconnectGraceWindow ("if leading, wait X" window) before
// starting — giving the party time to re-form after a board crash / cleanup
// redial. The hold applies whether or not we were the leader, per user
// direction. A first connect, or a reconnect where we were solo, starts
// immediately.
//
// Lifetime: app-scoped like EventScheduler. The TelnetClient is per-connection
// (owned by MainWindowViewModel), so this subscribes to the stable singletons
// (WirePromptScanner, RoomTracker, PartyState) directly and takes Connected /
// Disconnected via NotifyConnected / NotifyDisconnected from MainWindowVM.
//
// Threading: PromptObserved and RoomTracker.StateChanged already marshal onto
// the UI dispatcher upstream; the notify methods are called from MainWindowVM on
// the UI thread; the hold timer is a DispatcherTimer. Everything runs on the UI
// thread.
public sealed class DefaultTaskRunner : IDisposable
{
    private const string GeneralTabKey = "General";

    private readonly WirePromptScanner _prompt;
    private readonly RoomTracker _tracker;
    private readonly ProfileService _profile;
    private readonly LoopManager _loops;
    private readonly LairManager _lairs;
    private readonly LoopRunner _loopRunner;
    private readonly AutoLairManager _autoLair;
    private readonly PartyState _partyState;
    private readonly PartyManager _party;
    private readonly LogService? _log;

    // Live per-connection latches.
    private bool _isConnected;
    private bool _inGameConfirmed;   // first prompt seen this connection
    private bool _taskFired;         // default task already launched this connection (one-shot)
    private bool _sawPartyThisConnection;

    // Carried across the reconnect. Set at the disconnect that followed an
    // in-game session; consumed by the next game entry's hold decision. Reset on
    // profile load / close.
    private bool _hadInSessionDisconnect;
    private bool _wasInPartyBeforeDrop;

    // One-shot party-rebuild hold; non-null only while counting down.
    private IDisposable? _holdTimer;
    // Arms one tick after the delay; disposing what it returns cancels it.
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;

    private bool _disposed;

    public DefaultTaskRunner(
        WirePromptScanner prompt,
        RoomTracker tracker,
        ProfileService profile,
        LoopManager loops,
        LairManager lairs,
        LoopRunner loopRunner,
        AutoLairManager autoLair,
        PartyState partyState,
        PartyManager party,
        LogService? log = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        _schedule = schedule ?? DispatcherTicks.Once;
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(lairs);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(autoLair);
        ArgumentNullException.ThrowIfNull(partyState);
        ArgumentNullException.ThrowIfNull(party);
        _prompt = prompt;
        _tracker = tracker;
        _profile = profile;
        _loops = loops;
        _lairs = lairs;
        _loopRunner = loopRunner;
        _autoLair = autoLair;
        _partyState = partyState;
        _party = party;
        _log = log;

        _prompt.PromptObserved += OnPromptObserved;
        _tracker.StateChanged += OnRoomStateChanged;
        _tracker.PlayerDeathInferred += OnDeathInferred;
        _partyState.PropertyChanged += OnPartyStateChanged;
        _profile.ProfileLoaded += OnProfileLoaded;
        _profile.ProfileClosed += OnProfileClosed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _prompt.PromptObserved -= OnPromptObserved;
        _tracker.StateChanged -= OnRoomStateChanged;
        _tracker.PlayerDeathInferred -= OnDeathInferred;
        _partyState.PropertyChanged -= OnPartyStateChanged;
        _profile.ProfileLoaded -= OnProfileLoaded;
        _profile.ProfileClosed -= OnProfileClosed;
        CancelHold();
    }

    // ----- diagnostic snapshot (bug report) --------------------------

    // True while the party-rebuild hold is counting down before the task starts.
    public bool IsHoldingForParty => _holdTimer is not null;

    // True when this game entry qualifies as a reconnect that followed a party
    // session — the condition that arms the party-rebuild hold. Surfaced for the
    // bug report so a "task didn't start on time" capture explains the delay.
    public bool PendingPartyRebuildHold => _hadInSessionDisconnect && _wasInPartyBeforeDrop;

    // True when a task still waiting to start was called off for this connection
    // because the character turned out to have died while the link was down.
    public bool StoodDownForDeath { get; private set; }

    // ----- telnet-driven notifications -------------------------------

    // MainWindowVM calls this right after its TelnetClient.Connected handler runs.
    public void NotifyConnected()
    {
        _isConnected = true;
        _inGameConfirmed = false;
        _taskFired = false;
        _sawPartyThisConnection = false;
        StoodDownForDeath = false;
        CancelHold();
    }

    // MainWindowVM calls this when its TelnetClient.Disconnected handler runs.
    public void NotifyDisconnected()
    {
        // Only promote the next connect to a party-aware reconnect when we
        // actually reached the game world this session — a failed connect that
        // never saw a prompt isn't a reconnect and never held a party.
        if (_inGameConfirmed)
        {
            _hadInSessionDisconnect = true;
            _wasInPartyBeforeDrop = _sawPartyThisConnection;
        }
        _isConnected = false;
        _inGameConfirmed = false;
        CancelHold();
    }

    // ----- in-game detection -----------------------------------------

    private void OnPromptObserved(PromptObservation _)
    {
        if (!_isConnected || _inGameConfirmed) return;
        _inGameConfirmed = true;
        MaybeFire();
    }

    private void OnRoomStateChanged(RoomTransition _) => MaybeFire();

    // RoomTracker.PlayerDeathInferred: the character died while the link was down
    // and stands stripped in the temple. The death stops the engines that are
    // running; a task still waiting out the party-rebuild window, or for the room
    // to be known, would start after that stop and walk it out. Not on this
    // connection any more.
    private void OnDeathInferred()
    {
        if (!_isConnected) return;
        // Already started and not holding: nothing here to call off. The engine
        // stop that comes with this death reaches what it started.
        if (_holdTimer is null && _taskFired) return;
        CancelHold();
        _taskFired = true;
        StoodDownForDeath = true;
        _log?.Info("DefaultTask", "the character died while the link was down — the default task is not started on this connection.");
    }

    // Fire once when connected, the first prompt has landed, and the current
    // room is known — whichever of prompt / room arrives second triggers it.
    private void MaybeFire()
    {
        if (_taskFired || !_isConnected || !_inGameConfirmed) return;
        if (_tracker.State.CurrentRoom is null) return;
        _taskFired = true;
        FireDefaultTask();
    }

    // ----- party observation -----------------------------------------

    private void OnPartyStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Sticky: once we've been in a party this connection, remember it even
        // after the roster empties, so the disconnect-time snapshot survives the
        // teardown-ordering race where PartyState clears before NotifyDisconnected.
        if (e.PropertyName == nameof(PartyState.IsInParty) && _partyState.IsInParty)
            _sawPartyThisConnection = true;
    }

    // ----- profile lifecycle -----------------------------------------

    private void OnProfileLoaded(CharacterProfile _) => ResetReconnectLatches();
    private void OnProfileClosed() => ResetReconnectLatches();

    private void ResetReconnectLatches()
    {
        _hadInSessionDisconnect = false;
        _wasInPartyBeforeDrop = false;
        CancelHold();
    }

    // ----- task dispatch ---------------------------------------------

    private void FireDefaultTask()
    {
        GeneralSettings gen = ReadGeneralOrDefault();

        switch (gen.DefaultTask)
        {
            case InitialTask.DoNothing:
                _log?.Debug("DefaultTask", "entered game — default task is Do nothing.");
                return;

            case InitialTask.BeginLoop:
                if (string.IsNullOrWhiteSpace(gen.DefaultLoopName))
                {
                    _log?.Warn("DefaultTask", "Begin loop selected but no loop is configured — nothing to start.");
                    return;
                }
                if (_loops.Get(gen.DefaultLoopName) is not { } loop)
                {
                    _log?.Warn("DefaultTask",
                        $"Begin loop selected but loop '{gen.DefaultLoopName}' is not in the active game-data set — nothing to start.");
                    return;
                }
                StartOrHold($"loop '{loop.Name}'", () => StartLoop(loop));
                return;

            case InitialTask.BeginAutoLair:
                if (string.IsNullOrWhiteSpace(gen.DefaultAutoLairName))
                {
                    _log?.Warn("DefaultTask", "Begin Auto-Lair selected but no Auto-Lair is configured — nothing to start.");
                    return;
                }
                if (_lairs.Get(gen.DefaultAutoLairName) is not { } setup)
                {
                    _log?.Warn("DefaultTask",
                        $"Begin Auto-Lair selected but Auto-Lair '{gen.DefaultAutoLairName}' is not in the active game-data set — nothing to start.");
                    return;
                }
                StartOrHold($"Auto-Lair '{setup.Name}'", () => StartLair(setup));
                return;
        }
    }

    // Hold for the party-rebuild window on a party-session reconnect; otherwise
    // start now. The window is the same "if leading, wait X" value that gates the
    // party reconnect machinery (PartyManager.DisconnectGraceWindow).
    private void StartOrHold(string label, Action start)
    {
        TimeSpan window = _party.DisconnectGraceWindow;
        if (PendingPartyRebuildHold && window > TimeSpan.Zero)
        {
            _log?.Info("DefaultTask",
                $"reconnect after a party session — holding {window.TotalSeconds:0}s for party rebuild before starting {label}.");
            CancelHold();
            _held = (label, start);
            _holdTimer = _schedule(window, OnHoldElapsed);
            return;
        }

        start();
    }

    // The task waiting out the hold: its name ("loop 'Ring'") and its start. Null
    // when none is, and once it was called off.
    private (string Label, Action Start)? _held;

    // The party-rebuild window is over: start what was held, if it still is.
    private void OnHoldElapsed()
    {
        if (_held is not { } held) return;
        CancelHold();
        if (_isConnected) held.Start();
        else _log?.Info("DefaultTask", $"party-rebuild hold elapsed but no longer connected — skipping {held.Label}.");
    }

    // The task waiting out the hold, as it reads in the notice of what a stop
    // dropped ("loop 'Ring', held for the party to rebuild,"); null when none is.
    public string? HeldStart => _held is { } held ? $"{held.Label}, held for the party to rebuild," : null;

    // The game data was read in afresh while the task waited: it is called off for
    // this connection, not started when the window ends. The loop or the lair setup
    // it holds was picked by name from the catalogue of the game data active then
    // (loops and lair setups are kept with each set), and its rooms are that
    // data's; looked up again under another set the name is another loop, or none.
    // The user is told what was dropped, and starts what they want.
    public void DropHold(string reason)
    {
        if (_held is not { } held) return;
        CancelHold();
        _log?.Info("DefaultTask", $"{held.Label} is not started when the party-rebuild hold ends: {reason}.");
    }

    // The master switch, asked with what is about to start (true = off;
    // AutoModeController.Blocks counts it). The default task is automatic, so with
    // the switch off it is skipped, not kept for when the switch comes back on:
    // by then the user is at the keys and starts what they want. Checked at the
    // start itself so a start waiting out the party-rebuild hold is covered too.
    public Func<string, bool>? BlockedByMasterSwitch { get; set; }

    private bool SkippedForMasterSwitch(string what)
    {
        if (BlockedByMasterSwitch?.Invoke($"default task {what}") != true) return false;
        _log?.Info("DefaultTask", $"{what} not started: the master switch is off.");
        return true;
    }

    private void StartLoop(Loop loop)
    {
        if (!_isConnected) return;
        if (SkippedForMasterSwitch($"loop '{loop.Name}'")) return;
        if (_loopRunner.Start(loop))
            _log?.Info("DefaultTask", $"started loop '{loop.Name}'.");
        else
            _log?.Warn("DefaultTask", $"loop '{loop.Name}' failed to start (needs ≥2 waypoints and a known room).");
    }

    // Domain-only mirror of NavigationViewModel.RunSetup: stop any in-flight loop,
    // reload the setup's markers into the scheduler, and Start. The Navigation
    // window's own ActiveChanged subscription flips its map UI over — we don't
    // touch UI mode here.
    private void StartLair(LairSetup setup)
    {
        if (!_isConnected) return;
        if (SkippedForMasterSwitch($"Auto-Lair '{setup.Name}'")) return;

        if (_loopRunner.State != LoopState.Idle)
            _loopRunner.Stop("default task: auto-lair");
        if (_autoLair.IsActive)
            _autoLair.Stop("default task: auto-lair reload");

        _autoLair.Clear();
        foreach (LairMarker m in setup.Markers)
            _autoLair.Mark(new RoomKey(m.Map, m.Room), m.OverrideRespawnSeconds);

        if (_autoLair.Start(setup.Name))
            _log?.Info("DefaultTask", $"started Auto-Lair '{setup.Name}' ({setup.Markers.Count} markers).");
        else
            _log?.Warn("DefaultTask", $"Auto-Lair '{setup.Name}' failed to start (needs ≥2 markers and a known room).");
    }

    private void CancelHold()
    {
        _held = null;
        _holdTimer?.Dispose();
        _holdTimer = null;
    }

    private GeneralSettings ReadGeneralOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new GeneralSettings();
        if (!profile.Settings.TryGetValue(GeneralTabKey, out JsonElement json)) return new GeneralSettings();
        try
        {
            return JsonSerializer.Deserialize<GeneralSettings>(json.GetRawText()) ?? new GeneralSettings();
        }
        catch
        {
            // Malformed General JSON → treat as unset (Do nothing). The Settings
            // tab rewrites it cleanly on its next Save.
            return new GeneralSettings();
        }
    }
}
