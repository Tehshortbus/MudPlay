using System.ComponentModel;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Follower-side reconnect auto-rejoin. When we drop out of a party (a crash, a
// carrier loss, or any disconnect) and later re-enter the game, this telepaths
// @comeback to the leader we were following so they walk back and re-collect us.
// Once @comeback is on the wire the leader owns the whole recovery — they decide
// whether to come (distance / party-full gates), walk to us, and re-invite. A
// follower runs no movement of their own, so beyond the short room wait below
// there is nothing here to wait on: fire @comeback and we're done.
//
// Crash-vs-clean-close memory. The leader we follow is remembered in a
// crash-survivable slot (AppServices write-throughs _rememberedLeader into the
// loaded profile via PersistLeader + Save on every change). A hard crash leaves
// that slot populated, so the next launch hydrates it and rejoins; a clean
// shutdown clears it (see MainWindow's Closing handler), so a deliberate quit
// forgets. A deliberate in-game leave (leader dissolves / uninvites / we
// unfollow) clears the slot the same way — only an unexpected drop leaves it
// set. The slot always mirrors live follower membership, so a crash at any
// moment retains the right leader. Because the slot is empty on a fresh session
// with no prior party, a first-time connect never fires @comeback — rejoin is
// reconnect-only by construction.
//
// One-shot per connect. Arm() (called on every client.Connected, alongside
// StatlineReconcile.Arm) opens a latch that fires on the first in-game prompt —
// the statline the server prints once we're back in the realm. The prompt is
// used deliberately over the room display: a dark room prints no exits, so a
// room-display latch would survive until a light later reveals a room and then
// misfire @comeback long after the reconnect (at a leader we may already have
// rejoined). The statline lands even in the dark, bounding the fire to the
// reconnect window. If no leader is remembered the fire is a no-op, so a fresh
// session with no prior party stays quiet.
//
// The @comeback carries our room ("@comeback M/R") so the leader walks straight to
// us — but only a room confirmed since this connect. The tracker can still hold the
// room we dropped from (the server may put us back somewhere else), and a Paradigm
// `rm` resync lands a moment after the first prompt. So the prompt starts a short
// wait for a fresh confirmation, and only if none comes in RoomWait does the bare
// @comeback (leader backtracks) go out.
//
// Remembered-leader auto-join override. IsRememberedLeader is handed to
// AutoPartyManager as a force-accept predicate: when the leader we're
// remembering re-invites us, we auto-follow even if their per-player "join if
// invited" flag is off. Remembering that we were in their party is itself the
// standing consent to rejoin them.
//
// Read-only on party state: it observes PartyState (never writes it) and only
// emits an outbound telepath, so it composes with every other party subsystem.
public sealed class PartyRejoinCoordinator : IDisposable
{
    public const string LogCategory = "PartyRejoin";

    private readonly WirePromptScanner _scanner;
    private readonly PartyState _party;
    private readonly RoomTracker _tracker;
    private readonly Func<bool> _isAutoEnabled;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    // The leader we currently follow / should try to rejoin, as a given name.
    // Crash-survivable via PersistLeader; mirrors live follower membership.
    private string? _rememberedLeader;

    // One-shot rejoin latch — opened by Arm on connect, consumed on the first
    // in-game prompt.
    private bool _armed;
    private bool _disposed;

    // A room confirmed since Arm — the only kind we'll hand the leader.
    private bool _roomConfirmedSinceArm;

    // Waiting on that confirmation before firing: the leader to telepath.
    private string? _pendingLeader;
    private IDisposable? _roomWaitTimer;
    // Arms one tick after the delay; disposing what it returns cancels it.
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    public static readonly TimeSpan RoomWait = TimeSpan.FromSeconds(5);

    // Write-through sink for the crash-survivable memory. AppServices wires this
    // to stamp the loaded profile + Save() so a crash retains the value. Null keeps
    // only the in-memory _rememberedLeader.
    public Action<string?>? PersistLeader { get; set; }

    // The leader we'd try to rejoin on the next reconnect, or null when there's
    // no party to return to. Surfaced in the bug report's Party section.
    public string? RememberedLeader => _rememberedLeader;

    // "If leading, accept @comeback for (min)" (Settings → Party): a drop longer than
    // this and the party has moved on, so re-entry sends no @comeback.
    public TimeSpan ComebackWindow { get; set; } = TimeSpan.FromMinutes(2);

    private readonly Func<DateTimeOffset> _now;

    // When this session last lost the connection; null after a relaunch (a crash
    // leaves no drop time), and then the @comeback goes out.
    private DateTimeOffset? _droppedAt;

    // The leader we're about to @comeback, while we wait for our room to confirm.
    public string? WaitingForRoomToRejoin => _pendingLeader;

    public PartyRejoinCoordinator(
        WirePromptScanner scanner,
        PartyState party,
        RoomTracker tracker,
        Func<bool>? isAutoEnabled = null,
        LogService? log = null,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _schedule = schedule ?? DispatcherTicks.Once;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(tracker);
        _scanner = scanner;
        _party = party;
        _tracker = tracker;
        // Master auto-responses gate — when it returns false (kill switch
        // engaged) the rejoin stays silent, same as MainMenuEntryAutomation.
        // Defaults to always-on when unwired.
        _isAutoEnabled = isAutoEnabled ?? (() => true);
        _log = log;

        // First in-game prompt after a connect is our fire signal: the statline
        // proves we're back in the realm and lands even in a dark room, so the
        // fire can't be deferred to a later light-reveal.
        _scanner.PromptObserved += OnInGamePrompt;
        _party.PropertyChanged += OnPartyChanged;
        _tracker.StateChanged += OnRoomStateChanged;
    }

    // Bind the outbound wire — the gate-wrapped engine sender the other party
    // engines use. MainWindowViewModel supplies it after telnet connects.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Seed the crash-survivable memory from the loaded profile. AppServices
    // calls this on ProfileLoaded. Resets the per-session arm latch so a profile
    // swap can't leave a stale arm behind. Does not write-through — the value
    // came straight off disk.
    public void HydrateRememberedLeader(string? leader)
    {
        _rememberedLeader = Normalize(leader);
        _armed = false;
        _droppedAt = null;
        CancelRoomWait();
    }

    // The connection dropped while we were in-game. Only the first drop of an outage
    // counts; failed redials don't restamp it.
    public void NoteDisconnected() => _droppedAt ??= _now();

    // Open the one-shot rejoin latch. Called on every connect; the @comeback
    // only actually fires on the next in-game prompt if a leader is remembered.
    public void Arm()
    {
        _armed = true;
        _roomConfirmedSinceArm = false;
        CancelRoomWait();
    }

    // Force-accept predicate for AutoPartyManager: true when name is the leader
    // we're remembering across a reconnect, so their re-invite is auto-followed
    // even without a per-player join-if-invited grant. Compares on given name so
    // a long-form invite line ("MudPlay WuzHere") matches a short-form remembered
    // leader ("MudPlay").
    public bool IsRememberedLeader(string name)
    {
        if (_rememberedLeader is null || string.IsNullOrEmpty(name)) return false;
        return GivenNameOf(name).Equals(_rememberedLeader, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _party.PropertyChanged -= OnPartyChanged;
        _scanner.PromptObserved -= OnInGamePrompt;
        _tracker.StateChanged -= OnRoomStateChanged;
        CancelRoomWait();
    }

    // ----- Follower-membership tracking ---------------------------------

    private bool IsFollower => _party.IsInParty && !_party.SelfIsLeader
        && !string.IsNullOrEmpty(_party.LeaderName);

    private void OnPartyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PartyState.IsInParty)
            or nameof(PartyState.SelfIsLeader) or nameof(PartyState.LeaderName)))
            return;

        SyncRememberedLeader();
    }

    // Keep the remembered leader mirroring live follower membership. Set when we
    // become (or switch to) a follower, clear when we leave — both write through
    // so disk stays crash-accurate.
    private void SyncRememberedLeader()
    {
        if (IsFollower)
        {
            string given = GivenNameOf(_party.LeaderName!);
            if (string.Equals(given, _rememberedLeader, StringComparison.OrdinalIgnoreCase)) return;
            _rememberedLeader = given;
            PersistLeader?.Invoke(given);
            _log?.Info(LogCategory, $"Following {given} — remembered for reconnect rejoin.");
        }
        else if (_rememberedLeader is not null)
        {
            _log?.Info(LogCategory, $"Left party — forgetting reconnect leader {_rememberedLeader}.");
            _rememberedLeader = null;
            PersistLeader?.Invoke(null);
        }
    }

    // Forget the remembered leader without touching live party state. Called when
    // that leader tells us they aren't coming (a @forget decline) so a later
    // reconnect doesn't keep pestering them with @comeback. Write-through clears
    // the crash-survivable slot too.
    public void ForgetRememberedLeader(string leaderGiven)
    {
        if (_rememberedLeader is null) return;
        if (!GivenNameOf(leaderGiven).Equals(_rememberedLeader, StringComparison.OrdinalIgnoreCase)) return;
        _log?.Info(LogCategory, $"{_rememberedLeader} declined our rejoin — forgetting them.");
        _rememberedLeader = null;
        PersistLeader?.Invoke(null);
    }

    // ----- Rejoin flow --------------------------------------------------

    private void OnInGamePrompt(PromptObservation _)
    {
        if (!_armed) return;
        _armed = false; // one-shot per connect
        DateTimeOffset? droppedAt = _droppedAt;
        _droppedAt = null;
        if (_rememberedLeader is null) return;
        if (!_isAutoEnabled())
        {
            _log?.Info(LogCategory,
                $"Auto-responses off — not auto-rejoining {_rememberedLeader}.");
            return;
        }
        if (droppedAt is { } at && _now() - at > ComebackWindow)
        {
            _log?.Info(LogCategory,
                $"Offline {(_now() - at).TotalMinutes:0.#} min, past the {ComebackWindow.TotalMinutes:0} min @comeback window — not asking {_rememberedLeader} to come back.");
            return;
        }
        if (FreshRoom() is not null)
        {
            SendComeback(_rememberedLeader);
            return;
        }
        _pendingLeader = _rememberedLeader;
        _roomWaitTimer?.Dispose();
        _roomWaitTimer = _schedule(RoomWait, OnRoomWaitElapsed);
        _log?.Info(LogCategory,
            $"Reconnected while following {_pendingLeader} — waiting up to {RoomWait.TotalSeconds:0}s for our room to confirm before @comeback.");
    }

    private void OnRoomStateChanged(RoomTransition t)
    {
        if (t.NewConfidence != RoomConfidence.Confirmed) return;
        if (!_armed && _pendingLeader is null) return;
        _roomConfirmedSinceArm = true;
        if (_pendingLeader is { } leader && FreshRoom() is not null)
        {
            CancelRoomWait();
            SendComeback(leader);
        }
    }

    private void OnRoomWaitElapsed()
    {
        _roomWaitTimer = null;
        if (_pendingLeader is not { } leader) return;
        CancelRoomWait();
        _log?.Info(LogCategory, $"our room didn't confirm within {RoomWait.TotalSeconds:0}s — @comeback without it.");
        SendComeback(leader);
    }

    private void CancelRoomWait()
    {
        _pendingLeader = null;
        _roomWaitTimer?.Dispose();
        _roomWaitTimer = null;
    }

    private Room? FreshRoom() =>
        _roomConfirmedSinceArm && _tracker.State.Confidence == RoomConfidence.Confirmed
            ? _tracker.State.CurrentRoom
            : null;

    private void SendComeback(string leader)
    {
        // Asked again here: the send can come seconds after the prompt that
        // armed it (the wait for our room), and the master switch may have gone
        // off in between.
        if (!_isAutoEnabled())
        {
            _log?.Info(LogCategory, $"Auto-responses off — not asking {leader} to come back.");
            return;
        }
        // A bare @comeback makes the leader backtrack their own trail to find us.
        string payload = FreshRoom() is { } room ? $"@comeback {room.Key}" : "@comeback";
        _wire.Send($"/{leader} {payload}");
        _log?.Info(LogCategory,
            $"Reconnected while following {leader} — sent {payload}; leader now owns the pickup.");
    }

    // First whitespace-delimited token — mirrors PartyManager.GivenNameOf so a
    // long-form room-entry name ("MudPlay WuzHere") matches a short-form
    // remembered leader ("MudPlay").
    private static string GivenNameOf(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }

    private static string? Normalize(string? name)
        => string.IsNullOrWhiteSpace(name) ? null : name.Trim();
}
