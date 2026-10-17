using System;
using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Tokens;

// Executes a picked token route.
//
// SOLO: use the transport token, then resume the walk from its landing to the
// destination. If the current room has monsters/NPCs (a token can't be used there)
// it walks the overland route toward the destination and uses the token at the first
// monster-free room instead (auto-combat clears hostiles on the way; friendly-NPC
// rooms are walked past) — a genuinely-shorter token route always reaches a clear
// room before arrival.
//
// PARTY LEADER: the leader goes LAST. In a monster-free room it broadcasts
// `.@party use token of <place>` (the say-relay all followers act on — no per-player
// grant needed), then stays put and watches its OWN room for each member's use
// (their WitnessMessage, via TokenTracker.MemberDeparted) — one per member means all
// ported. On a shortfall it checks who's still in the room and re-directs just those
// with a targeted `@do use token of <place>`, up to MaxRegroupRetries at
// RegroupIntervalMs. All ported → the leader uses its own token last and resumes the
// walk. Some still stranded after the retries → depends on the "use when party
// incomplete" setting: either the leader tokens anyway and walks on (leaving them),
// or it fails out in the room (RegroupFailed → nav header) and sits. A party FOLLOWER
// declines (TryBegin false) so the caller walks the overland route instead.
//
// Delegate-injected (no live line stream); driven by
// TokenTracker.TokenUsed / TokenTracker.MemberDeparted, Walker.Event, and
// RoomTracker.StateChanged, all already on the UI thread — so it needs no locking.
public sealed class TokenRouteCoordinator
{
    private enum Phase
    {
        Idle,
        WalkingToClear,  // NPCs present — walking overland (party following) to a monster-free room.
        Regrouping,      // (leader) in a clear room, sending members across and waiting for them to port.
        AwaitingUse,     // sent `use token of <place>`, waiting for the success line (TokenUsed).
        AwaitingLanding, // teleport succeeded, waiting for the room tracker to confirm the landing room.
    }

    private const int LandingConfirmTimeoutMs = 8000;
    private const int UseConfirmTimeoutMs = 6000;
    private const int PostLandSettleMs = 700;
    // Party regroup: re-check who's still in the room and re-direct them this often, up
    // to this many retries after the first `.@party` broadcast, before giving up.
    private const int RegroupIntervalMs = 6000;
    private const int MaxRegroupRetries = 3;

    private readonly Func<bool> _inParty;
    private readonly Func<bool> _isLeader;
    private readonly Func<IReadOnlyList<string>> _partyMembers;       // given-names of the members to bring
    private readonly Func<IReadOnlyList<string>> _membersStillHere;   // given-names of members still in the room
    private readonly Func<bool> _useWhenIncomplete;                   // token anyway vs fail out if stranded
    private readonly Func<bool> _roomHasNpc;
    private readonly Action<RoomKey> _walkToDest;
    private readonly Action _stopWalker;
    private readonly Action<string> _send;
    private readonly Action<int, Action> _schedule;
    private readonly LogService? _log;

    // Fired when a party regroup fails after the retries (and the setting says wait) —
    // carries the nav-header reason. The leader does not token; it sits.
    public event Action<string>? RegroupFailed;

    private string _place = "";          // normalized, for matching the success line
    private string _displayPlace = "";   // original casing/article, for building the use command
    private RoomKey _landing;
    private RoomKey _destination;
    private RoomKey? _lastRoom;
    // Invalidates a scheduled callback when the phase moves on before it fires.
    private int _generation;

    private readonly HashSet<string> _regroupPending = new(StringComparer.OrdinalIgnoreCase);
    private int _regroupRetries;

    public TokenRouteCoordinator(
        Func<bool> inParty,
        Func<bool> isLeader,
        Func<IReadOnlyList<string>> partyMembers,
        Func<IReadOnlyList<string>> membersStillHere,
        Func<bool> useWhenIncomplete,
        Func<bool> roomHasNpc,
        Action<RoomKey> walkToDest,
        Action stopWalker,
        Action<string> send,
        Action<int, Action> schedule,
        LogService? log = null)
    {
        _inParty = inParty ?? throw new ArgumentNullException(nameof(inParty));
        _isLeader = isLeader ?? throw new ArgumentNullException(nameof(isLeader));
        _partyMembers = partyMembers ?? throw new ArgumentNullException(nameof(partyMembers));
        _membersStillHere = membersStillHere ?? throw new ArgumentNullException(nameof(membersStillHere));
        _useWhenIncomplete = useWhenIncomplete ?? throw new ArgumentNullException(nameof(useWhenIncomplete));
        _roomHasNpc = roomHasNpc ?? throw new ArgumentNullException(nameof(roomHasNpc));
        _walkToDest = walkToDest ?? throw new ArgumentNullException(nameof(walkToDest));
        _stopWalker = stopWalker ?? throw new ArgumentNullException(nameof(stopWalker));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _log = log;
    }

    public bool Active => _phase != Phase.Idle;

    // Our own token use has gone through and we're waiting to land. Read right after
    // TokenTracker.TokenUsed to tell a picked route's token from one the user used by
    // hand.
    public bool AwaitingLanding => _phase == Phase.AwaitingLanding;

    // Raised whenever the route's phase changes, so the Navigation window can show it
    // as a running route (Run/Stop reads Stop) while the walker itself is idle.
    public event Action? Changed;

    // One line for the Navigation header while the route is between walks. Null when
    // idle or while it's walking (the walk has its own status line).
    public string? StatusText => _phase switch
    {
        Phase.Regrouping => $"Token route to {_displayPlace}: waiting for {_regroupPending.Count} party member(s) to token across",
        Phase.AwaitingUse => $"Token route to {_displayPlace}: using the token",
        Phase.AwaitingLanding => $"Token route to {_displayPlace}: landing",
        _ => null,
    };

    private Phase _phaseBacking = Phase.Idle;

    // For a bug report: the token trip in progress, or null.
    public string? DescribePending() =>
        _phaseBacking == Phase.Idle && _regroupPending.Count == 0 ? null
        : $"phase {_phaseBacking}"
          + (_regroupPending.Count > 0 ? $", regroup awaited from {string.Join(", ", _regroupPending)} ({_regroupRetries} retry(ies))" : string.Empty);
    private Phase _phase
    {
        get => _phaseBacking;
        set
        {
            if (_phaseBacking == value) return;
            _phaseBacking = value;
            Changed?.Invoke();
        }
    }

    // Begin a picked token route. Returns false — so the caller walks overland — when
    // in a party but NOT the leader (a follower can't drive a token route). Solo or
    // leader: head to a monster-free room, then (solo) use the token or (leader)
    // regroup the party first; returns true.
    public bool TryBegin(string place, RoomKey landing, RoomKey destination)
    {
        if (string.IsNullOrWhiteSpace(place)) return false;
        if (_inParty() && !_isLeader())
        {
            _log?.Info("Tokens", $"token route to {place} declined — a party follower can't drive a token route; walking overland");
            return false;
        }

        _place = TokenCatalog.NormalizePlace(place);
        _displayPlace = place.Trim();
        _landing = landing;
        _destination = destination;
        _lastRoom = null;
        _regroupPending.Clear();
        _regroupRetries = 0;
        NextGeneration();

        if (!_roomHasNpc())
        {
            BeginInClearRoom();
        }
        else
        {
            _phase = Phase.WalkingToClear;
            _log?.Info("Tokens", $"token route to {_place}: room not clear — walking overland until a monster-free room");
            _walkToDest(_destination);
        }
        return true;
    }

    // In a monster-free room: a leader regroups the party first; solo uses right away.
    private void BeginInClearRoom()
    {
        if (_inParty() && _isLeader()) StartRegroup();
        else UseOwnToken();
    }

    // Broadcast the party across, then watch for each to port.
    private void StartRegroup()
    {
        _regroupPending.Clear();
        foreach (string member in _partyMembers())
            if (GivenName(member) is { Length: > 0 } given)
                _regroupPending.Add(given);

        if (_regroupPending.Count == 0) { UseOwnToken(); return; }

        _phase = Phase.Regrouping;
        _regroupRetries = 0;
        _log?.Info("Tokens", $"token route to {_place}: sending {_regroupPending.Count} member(s) across, then following: {string.Join(", ", _regroupPending)}");
        _send($".@party {UseCommand()}");   // say-relay: no per-player grant needed
        ScheduleRegroupTick();
    }

    // TokenTracker.MemberDeparted — a member used their token in our room (ported).
    public void OnMemberDeparted(string name)
    {
        if (_phase != Phase.Regrouping) return;
        if (GivenName(name) is not { Length: > 0 } given) return;
        if (_regroupPending.Remove(given))
        {
            _log?.Info("Tokens", $"token route to {_place}: {given} ported ({_regroupPending.Count} still here)");
            Changed?.Invoke();
            if (_regroupPending.Count == 0)
            {
                _log?.Info("Tokens", $"token route to {_place}: whole party across — leader following");
                UseOwnToken();
            }
        }
    }

    private void ScheduleRegroupTick()
    {
        int gen = _generation;
        _schedule(RegroupIntervalMs, () =>
        {
            if (gen != _generation || _phase != Phase.Regrouping) return;
            OnRegroupTick();
        });
    }

    private void OnRegroupTick()
    {
        // Check the room: members still standing here haven't ported (authoritative —
        // catches a port whose witness line we missed).
        var here = new HashSet<string>(_membersStillHere(), StringComparer.OrdinalIgnoreCase);
        _regroupPending.RemoveWhere(m => !here.Contains(m));

        if (_regroupPending.Count == 0) { UseOwnToken(); return; }

        if (_regroupRetries < MaxRegroupRetries)
        {
            _regroupRetries++;
            _log?.Info("Tokens", $"token route to {_place}: regroup retry {_regroupRetries}/{MaxRegroupRetries} — still here: {string.Join(", ", _regroupPending)}");
            // Re-broadcast rather than target: `.@party` is a room-local say, so only
            // the members still standing here (not yet ported) hear it — no per-player
            // grant needed, and those who already left don't get re-told.
            _send($".@party {UseCommand()}");
            ScheduleRegroupTick();
            return;
        }

        string missing = string.Join(", ", _regroupPending);
        if (_useWhenIncomplete())
        {
            _log?.Info("Tokens", $"token route to {_place}: {missing} never ported — using token anyway (setting) and leaving them");
            UseOwnToken();
        }
        else
        {
            string reason = $"Token regroup failed — {missing} couldn't follow to {_displayPlace}";
            _log?.Info("Tokens", $"token route to {_place}: {reason}; sitting for user action");
            StandDown();                 // sit in the room — the leader does NOT token
            RegroupFailed?.Invoke(reason);
        }
    }

    // Send the leader's / solo token use and wait for the success line. Uses the full
    // display name so the item matches (e.g. "token of the Lost City").
    private void UseOwnToken()
    {
        _phase = Phase.AwaitingUse;
        int gen = _generation;
        _log?.Info("Tokens", $"token route to {_place}: using token, awaiting teleport");
        _send(UseCommand());
        _schedule(UseConfirmTimeoutMs, () =>
        {
            if (gen != _generation || _phase != Phase.AwaitingUse) return;
            _log?.Info("Tokens", $"token route to {_place}: no teleport confirmation — use likely failed; walking overland to destination");
            StandDownAndWalk();
        });
    }

    private string UseCommand() => $"use {TokenCatalog.LookName(_displayPlace)}";

    // TokenTracker.TokenUsed — our token use succeeded (the CasterMessage line).
    public void OnTokenUsed(string usedPlace)
    {
        if (_phase != Phase.AwaitingUse) return;
        if (!string.Equals(TokenCatalog.NormalizePlace(usedPlace), _place, StringComparison.Ordinal)) return;

        _phase = Phase.AwaitingLanding;
        int gen = _generation;
        _log?.Info("Tokens", $"token route to {_place}: teleport confirmed, awaiting landing at {_landing}");
        _schedule(LandingConfirmTimeoutMs, () =>
        {
            if (gen != _generation || _phase != Phase.AwaitingLanding) return;
            _log?.Info("Tokens", $"token route to {_place}: landing room not confirmed in time — resuming walk anyway");
            ResumeWalk();
        });
    }

    // RoomTracker.StateChanged — entered/confirmed a room.
    public void OnRoomChanged(RoomKey? confirmedRoom)
    {
        if (confirmedRoom is not { } room) return;

        switch (_phase)
        {
            case Phase.WalkingToClear:
                if (_lastRoom is { } prev && prev.Equals(room)) return;   // same room, not a fresh entry
                _lastRoom = room;
                if (!_roomHasNpc())
                {
                    _log?.Info("Tokens", $"token route to {_place}: reached a monster-free room ({room})");
                    _stopWalker();
                    BeginInClearRoom();
                }
                break;

            case Phase.AwaitingLanding:
                if (room.Equals(_landing))
                {
                    int gen = _generation;
                    _schedule(PostLandSettleMs, () =>
                    {
                        if (gen != _generation || _phase != Phase.AwaitingLanding) return;
                        _log?.Info("Tokens", $"token route to {_place}: landed at {room} — resuming walk to {_destination}");
                        ResumeWalk();
                    });
                }
                break;
        }
    }

    // Walker.Event. While regrouping, using the token or landing, the route drives no
    // walk of its own — so any walk starting then is the user retargeting: abandon.
    // While walking overland to a clear room, the route ends with that walk.
    public void OnWalkEvent(WalkEvent e)
    {
        if (_phase is Phase.Regrouping or Phase.AwaitingUse or Phase.AwaitingLanding)
        {
            if (e.Kind == WalkEventKind.Started)
            {
                _log?.Info("Tokens", $"token route to {_place}: a new walk was started — abandoning the token route");
                StandDown();
            }
            return;
        }
        if (_phase != Phase.WalkingToClear) return;
        switch (e.Kind)
        {
            case WalkEventKind.Finished:
                // Reached the destination without ever hitting a clear room to token in —
                // no token needed, the overland walk already got us there.
                _log?.Info("Tokens", $"token route to {_place}: reached destination overland before a clear room — token unused");
                StandDown();
                break;
            case WalkEventKind.Failed:
            case WalkEventKind.Stopped:
                _log?.Info("Tokens", $"token route to {_place}: overland walk {e.Kind.ToString().ToLowerInvariant()} — abandoning token route");
                StandDown();
                break;
        }
    }

    private void ResumeWalk()
    {
        RoomKey dest = _destination;
        StandDown();
        _walkToDest(dest);
    }

    private void StandDownAndWalk()
    {
        RoomKey dest = _destination;
        StandDown();
        _walkToDest(dest);
    }

    // Reset States: stand down without resuming the walk it was part of.
    public void Cancel() => StandDown();

    private void StandDown()
    {
        _phase = Phase.Idle;
        _regroupPending.Clear();
        _regroupRetries = 0;
        NextGeneration();
    }

    private void NextGeneration() => _generation++;

    // "Boost" from "Boost the Bold" — party relays and @do address the given name only,
    // and the witness line prints it, so both sides match on it.
    private static string GivenName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        int sp = name.IndexOf(' ');
        return (sp < 0 ? name : name[..sp]).Trim();
    }
}
