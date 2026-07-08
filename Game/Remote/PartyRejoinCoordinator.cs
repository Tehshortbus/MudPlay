using System.ComponentModel;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Remote;

// Follower-side reconnect auto-rejoin. When we drop out of a party (a crash, a
// carrier loss, or any disconnect) and later re-enter the game, this telepaths
// @comeback to the leader we were following so they walk back and re-collect us.
// Once @comeback is on the wire the leader owns the whole recovery — they decide
// whether to come (distance / party-full gates), walk to us, and re-invite. A
// follower runs no movement of their own, so there is nothing here to wait on or
// time out: fire @comeback and we're done.
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
// StatlineReconcile.Arm) opens a latch that fires on the first in-game room
// display — the moment we have a room to hand the leader. If no leader is
// remembered the fire is a no-op, so a fresh session with no prior party stays
// quiet.
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

    private readonly PartyState _party;
    private readonly RoomTracker _tracker;
    private readonly Func<bool> _isAutoEnabled;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();
    private readonly List<IDisposable> _subs = new();

    // The leader we currently follow / should try to rejoin, as a given name.
    // Crash-survivable via PersistLeader; mirrors live follower membership.
    private string? _rememberedLeader;

    // One-shot rejoin latch — opened by Arm on connect, consumed on the first
    // in-game room display.
    private bool _armed;
    private bool _disposed;

    // Write-through sink for the crash-survivable memory. AppServices wires this
    // to stamp the loaded profile + Save() so a crash retains the value. Null in
    // tests (the in-memory _rememberedLeader is enough there).
    public Action<string?>? PersistLeader { get; set; }

    // The leader we'd try to rejoin on the next reconnect, or null when there's
    // no party to return to. Surfaced in the bug report's Party section.
    public string? RememberedLeader => _rememberedLeader;

    // Test seam — every outbound wire buffer, in order.
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    public PartyRejoinCoordinator(
        MessageRouter router,
        PartyState party,
        RoomTracker tracker,
        Func<bool>? isAutoEnabled = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(tracker);
        _party = party;
        _tracker = tracker;
        // Master auto-responses gate — when it returns false (kill switch
        // engaged) the rejoin stays silent, same as MainMenuEntryAutomation.
        // Defaults to always-on so tests behave without wiring it.
        _isAutoEnabled = isAutoEnabled ?? (() => true);
        _log = log;

        // First in-game room display after a connect is our fire signal: it
        // proves we're in the realm and gives RoomTracker a room to send.
        _subs.Add(router.Subscribe(KnownPatterns.RoomExits, OnRoomDisplayed));
        _party.PropertyChanged += OnPartyChanged;
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
    }

    // Open the one-shot rejoin latch. Called on every connect; the @comeback
    // only actually fires on the next in-game room display if a leader is
    // remembered.
    public void Arm() => _armed = true;

    // Force-accept predicate for AutoPartyManager: true when name is the leader
    // we're remembering across a reconnect, so their re-invite is auto-followed
    // even without a per-player join-if-invited grant. Compares on given name so
    // a long-form invite line ("Fujin WuzHere") matches a short-form remembered
    // leader ("Fujin").
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
        foreach (IDisposable sub in _subs) sub.Dispose();
        _subs.Clear();
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

    private void OnRoomDisplayed(MatchResult _)
    {
        if (!_armed) return;
        _armed = false; // one-shot per connect
        if (_rememberedLeader is null) return;
        if (!_isAutoEnabled())
        {
            _log?.Info(LogCategory,
                $"Auto-responses off — not auto-rejoining {_rememberedLeader}.");
            return;
        }
        SendComeback(_rememberedLeader);
    }

    private void SendComeback(string leader)
    {
        // Attach our room only when we're confident of it — a stale guess would
        // send the leader to the wrong place. A bare @comeback makes the leader
        // backtrack their own trail to find us.
        string payload = "@comeback";
        if (_tracker.State.Confidence == RoomConfidence.Confirmed
            && _tracker.State.CurrentRoom is { } room)
            payload = $"@comeback {room.Key}";

        _wire.Send($"/{leader} {payload}");
        _log?.Info(LogCategory,
            $"Reconnected while following {leader} — sent {payload}; leader now owns the pickup.");
    }

    // First whitespace-delimited token — mirrors PartyManager.GivenNameOf so a
    // long-form room-entry name ("Fujin WuzHere") matches a short-form
    // remembered leader ("Fujin").
    private static string GivenNameOf(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }

    private static string? Normalize(string? name)
        => string.IsNullOrWhiteSpace(name) ? null : name.Trim();
}
