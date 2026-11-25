using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game;

// "Sysop goto" power: jump to a curated board location with `sys goto <name>`. The
// name is sent VERBATIM — the game resolves it — so the client can't tell from the
// command alone where the player lands. The user's SysopGotos table (per-BBS) is how
// the client knows: each row ties a keyword to the map/room it lands in.
//
// This class is the gate + the landing resync. It does NOT rewrite the typed line;
// it either passes it through (not our command / power off) or swallows it, gating on
// combat / hostiles / table membership / level, then sends and arms a landing resync.
//
// Delegate-injected (shaped like SysopGodLifeRecovery) so it stays a pure Game-layer
// unit: the Services layer wires the state reads, the send, the terminal-status write
// (marshalled — a status write from inside the message pump re-enters the emulator's
// Feed, so the wiring Posts it), and the position commit.
public sealed class SysopGotoManager
{
    private readonly Func<bool> _enabled;
    private readonly Func<IReadOnlyList<SysopGotoLocation>> _locations;
    private readonly Func<bool> _inCombat;
    // The character's level, or null when the statline hasn't been parsed yet — a
    // level gate can't be judged on an unknown level.
    private readonly Func<int?> _knownLevel;
    // The display name of a landing room (via RoomGraphManager), or null when the key
    // isn't in the active graph. Used both to arm the resync and to validate the row.
    private readonly Func<RoomKey, string?> _roomName;
    // The name of the room we're in now, or null when the position isn't known.
    private readonly Func<string?> _roomHere;
    private readonly Action<string> _send;
    // Sends a bare Enter. A sys-goto emits no room display on its own (only a statline
    // redisplay), so we force one so the landing resync has a room name to match.
    private readonly Action _forceRoomDisplay;
    private readonly Action<string> _writeStatus;
    private readonly Action<RoomKey> _commitLocated;
    // A jump put us in a room the table doesn't name. Answers whether that stopped a run.
    private readonly Func<string, bool> _landedElsewhere;
    // False while the master switch (Auto-All) is off: a typed goto is then the
    // user's alone and goes as typed.
    private readonly Func<bool> _masterSwitchOn;
    private readonly MessageRouter _router;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;

    private const string LogCat = "SysopGoto";

    // A goto refused for combat, sent once the `break` it sent has ended the fight.
    // The game answers a `break` in one round, so one not answered by then was not
    // the fight ending and the goto is dropped.
    private (SysopGotoLocation Loc, DateTimeOffset BrokeAt)? _afterBreak;
    private static readonly TimeSpan BreakWindow = TimeSpan.FromSeconds(6);

    // A fired jump awaiting its landing: the key we expect to be in, that room's
    // display name (captured at fire time), and when we sent. The next room display
    // whose name matches commits the position; a mismatch or the timeout drops it and
    // lets normal tracking / Lost-recovery take over — so a refused jump never lies to
    // the tracker.
    private (RoomKey Key, string ExpectedName, DateTimeOffset SentAt, string Keyword, string? LeftRoom)? _armed;
    private static readonly TimeSpan LandingWindow = TimeSpan.FromSeconds(5);

    public SysopGotoManager(
        Func<bool> enabled,
        Func<IReadOnlyList<SysopGotoLocation>> locations,
        Func<bool> inCombat,
        Func<int?> knownLevel,
        Func<RoomKey, string?> roomName,
        Func<string?> roomHere,
        Action<string> send,
        Action forceRoomDisplay,
        Action<string> writeStatus,
        Action<RoomKey> commitLocated,
        Func<string, bool> landedElsewhere,
        Func<bool> masterSwitchOn,
        MessageRouter router,
        LogService? log = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(inCombat);
        ArgumentNullException.ThrowIfNull(knownLevel);
        ArgumentNullException.ThrowIfNull(roomName);
        ArgumentNullException.ThrowIfNull(roomHere);
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(forceRoomDisplay);
        ArgumentNullException.ThrowIfNull(writeStatus);
        ArgumentNullException.ThrowIfNull(commitLocated);
        ArgumentNullException.ThrowIfNull(landedElsewhere);
        ArgumentNullException.ThrowIfNull(masterSwitchOn);
        ArgumentNullException.ThrowIfNull(router);
        _enabled = enabled;
        _locations = locations;
        _inCombat = inCombat;
        _knownLevel = knownLevel;
        _roomName = roomName;
        _roomHere = roomHere;
        _send = send;
        _forceRoomDisplay = forceRoomDisplay;
        _writeStatus = writeStatus;
        _commitLocated = commitLocated;
        _landedElsewhere = landedElsewhere;
        _masterSwitchOn = masterSwitchOn;
        _router = router;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _log = log;
        // Subscribed for the client's lifetime, as the manager is.
        _ = router.Subscribe(KnownPatterns.CombatStatus, OnCombatStatus);
        _ = router.Subscribe(KnownPatterns.CommandNoEffect, OnCommandNoEffect);
    }

    // Whether the power is granted on the active BBS.
    public bool Enabled => _enabled();

    // The configured locations when the power is on, else empty. Menus render from
    // this; per-row firability at the current level is MeetsLevel.
    public IReadOnlyList<SysopGotoLocation> UsableNow =>
        _enabled() ? _locations() : System.Array.Empty<SysopGotoLocation>();

    // A row's destination is safe to reach at the current level. A row with no gate
    // (MinLevel <= 0) always passes; a gated row passes when the level is known and
    // high enough. Level UNKNOWN passes for a manual fire (the user asked explicitly);
    // the route planner (SysopGotoRoutePlanner) skips an unknown level and must not
    // rely on this.
    public bool MeetsLevel(SysopGotoLocation loc)
    {
        if (loc.MinLevel <= 0) return true;
        return _knownLevel() is not int lvl || lvl >= loc.MinLevel;
    }

    // Intercept a typed input line. Returns true when we handled it (either fired it
    // or refused it — in both cases the caller must NOT also send the line); false
    // when it isn't ours (not `sys goto …`, or the power is off) so the caller sends
    // it normally and lets the game respond.
    public bool TryHandleTypedLine(string line)
    {
        string? name = ParseGotoName(line);
        if (name is null) return false;                 // not `sys goto <name>`
        if (!_enabled()) return false;                  // power off — pass through, let the game refuse
        if (!_masterSwitchOn()) return SendAsTyped(line.Trim(), name);

        if (TryFire(name, out string refusal))
            return true;
        if (!string.IsNullOrEmpty(refusal))
            _writeStatus(refusal);
        return true;                                    // ours; swallowed even on refusal
    }

    // Fire a specific configured row (menu / picker path). Returns false + a refusal
    // reason when gated (caller may surface it); refusal is empty on success.
    public bool TryFire(SysopGotoLocation loc, out string refusal)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return TryFire(loc.Name, out refusal);
    }

    // With the master switch off the user is driving: the line goes as typed, with no
    // table, combat or level check (user, 2026-11-24). A landing the client can name,
    // from a table row or the numeric form, is still re-anchored. False when it can
    // name none, and the caller sends the line like any other.
    private bool SendAsTyped(string line, string name)
    {
        RoomKey? key = FindByName(name) is { } loc ? new RoomKey(loc.Map, loc.Room) : NumericLanding(name);
        _afterBreak = null;
        if (key is not { } landing)
        {
            _log?.Info(LogCat, $"Master switch off — '{line}' sent as typed; no landing known to re-anchor.");
            return false;
        }
        _log?.Info(LogCat, $"Master switch off — '{line}' sent as typed.");
        Dispatch(line, landing, name);
        return true;
    }

    // `sys goto <room> <map>`, which some boards take (report stock-20261124-183300:
    // `sys goto 2863 2` landed in 2/2863).
    private static RoomKey? NumericLanding(string name)
    {
        string[] parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int room)
            && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int map)
            ? new RoomKey(map, room)
            : null;
    }

    // Shared fire path for both the typed keyword and a picked row. Gate order below
    // is first-failure-wins; each failure has its own reason.
    private bool TryFire(string name, out string refusal)
    {
        refusal = string.Empty;
        name = name.Trim();

        SysopGotoLocation? loc = FindByName(name);
        if (loc is null)
        {
            refusal = $"Sys goto blocked — '{name}' isn't in your Sys Goto table (add it in Settings so the client can re-anchor after the jump).";
            _log?.Info(LogCat, $"Refused '{name}': not in the table.");
            return false;
        }
        if (loc.MinLevel > 0 && _knownLevel() is int lvl && lvl < loc.MinLevel)
        {
            refusal = $"Sys goto blocked — '{name}' needs level {loc.MinLevel} (you're {lvl}).";
            _log?.Info(LogCat, $"Refused '{name}': level {lvl} < required {loc.MinLevel}.");
            return false;
        }

        // The game refuses a `sys goto` only while ACTIVELY engaged (an attack
        // announced against a target) — a hostile merely PRESENT in the room is fine,
        // so there's no hostile-present gate (user 2026-09-08). Engaged, the client
        // sends `break` and the goto once the break takes (user, 2026-11-24).
        if (_inCombat())
        {
            _send("break");
            _afterBreak = (loc, _now());
            refusal = $"Sys goto '{loc.Name}' waits for the break: it goes as soon as combat stops.";
            _log?.Info(LogCat, $"'{loc.Name}' held: actively in combat — sent 'break'; the goto follows once combat stops.");
            return false;
        }

        DispatchGoto(loc);
        return true;
    }

    private void OnCombatStatus(MatchResult m)
    {
        if (m.Groups.Count == 0 || !string.Equals(m.Groups[0], "Off", StringComparison.OrdinalIgnoreCase)) return;
        FireAfterBreak("combat stopped");
    }

    // The break had nothing to stop: being attacked without fighting back doesn't
    // block a sys goto (user, 2026-11-24).
    private void OnCommandNoEffect(MatchResult _)
    {
        if (_router.ReplyIsForCommandNotNaming("break")) return;
        FireAfterBreak("the break had no effect, so we weren't fighting");
    }

    private void FireAfterBreak(string why)
    {
        if (_afterBreak is not { } pending) return;
        _afterBreak = null;
        if (_now() - pending.BrokeAt > BreakWindow)
        {
            _log?.Info(LogCat, $"'{pending.Loc.Name}' dropped: {why}, but past the break's round.");
            return;
        }
        if (!_enabled()) return;
        _log?.Info(LogCat, $"'{pending.Loc.Name}' firing after the break: {why}.");
        DispatchGoto(pending.Loc);
    }

    // Wimpy escape (HealthManager's "sys goto wimpy instead of hanging"): break
    // active combat, then jump to <name> — bypassing the interactive combat-refuse
    // gate because the caller WANTS the break-then-go, not a "run it again" prompt.
    // Still gated on the power being granted + the row existing (no level gate — a
    // life-saving escape isn't a routing choice). Returns true when it dispatched
    // the jump, false when it couldn't (power off / unknown location) so the caller
    // can fall back to the normal hangup.
    public bool TryFireForWimpy(string name)
    {
        if (!_enabled()) return false;
        name = (name ?? string.Empty).Trim();
        SysopGotoLocation? loc = FindByName(name);
        if (loc is null)
        {
            _log?.Info(LogCat, $"Wimpy goto '{name}' isn't in the table — falling back to hangup.");
            return false;
        }
        if (_inCombat())
        {
            _log?.Info(LogCat, $"Wimpy goto '{loc.Name}': actively in combat — sending 'break' before the jump.");
            _send("break");
        }
        _log?.Info(LogCat, $"Wimpy goto firing → 'sys goto {loc.Name}' (HP-escape substitute for hangup).");
        DispatchGoto(loc);
        return true;
    }

    // Router path (AutoWalkManager's sys-goto shortcut): fire the jump for a
    // location the planner already validated (in the table, level OK). No gates
    // here — combat is handled upstream by the movement coordinator's pause (the
    // walker stalls rather than reaching this step mid-fight), and the level /
    // table checks happened at planning time. Just the verbatim send + bare Enter
    // + landing resync, shared with every other fire path.
    public void FireForRoute(SysopGotoLocation loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        _log?.Info(LogCat, $"Router firing → 'sys goto {loc.Name}' (walk shortcut).");
        DispatchGoto(loc);
    }

    // Shared fire tail for the gated, wimpy, and router paths: send the verbatim
    // keyword, force the landing room display, and arm the name-matched resync.
    private void DispatchGoto(SysopGotoLocation loc) =>
        Dispatch($"sys goto {loc.Name}", new RoomKey(loc.Map, loc.Room), loc.Name);

    private void Dispatch(string command, RoomKey key, string keyword)
    {
        _afterBreak = null;
        string? landingName = _roomName(key);
        string? leftRoom = _roomHere();
        _send(command);
        // A sys-goto prints no room on its own — only a statline redisplay — so nudge
        // the game to show the landing room with a bare Enter; that display is what
        // OnRoomDisplayed matches to commit the resync. (Confirmed mechanic.)
        _forceRoomDisplay();
        if (landingName is { Length: > 0 })
        {
            _armed = (key, landingName, _now(), keyword, leftRoom);
            _log?.Info(LogCat, $"Fired '{command}' → landing {key} ('{landingName}'); armed resync.");
        }
        else
        {
            _armed = null;
            _log?.Info(LogCat,
                $"Fired '{command}' → landing {key}, but that room isn't in the active graph — can't arm a resync.");
        }
    }

    // Fed each time a room display is parsed. Commits an armed landing when the shown
    // room name matches the expected landing; drops the expectation on a mismatch or
    // once the landing window elapses.
    public void OnRoomDisplayed(string roomName)
    {
        if (_armed is not { } armed) return;
        if (_now() - armed.SentAt > LandingWindow)
        {
            _log?.Info(LogCat, $"Landing window elapsed for {armed.Key} ('{armed.ExpectedName}') — dropping resync.");
            _armed = null;
            return;
        }
        if (string.Equals(roomName?.Trim(), armed.ExpectedName, StringComparison.OrdinalIgnoreCase))
        {
            _log?.Info(LogCat, $"Landing confirmed — room '{armed.ExpectedName}' — committing position {armed.Key}.");
            _armed = null;
            _commitLocated(armed.Key);
        }
        else
        {
            _log?.Info(LogCat,
                $"Landing mismatch — expected '{armed.ExpectedName}' but saw '{roomName}' — dropping resync (jump may have been refused).");
            _armed = null;
            WarnIfLandedElsewhere(armed.Keyword, armed.Key, armed.ExpectedName, armed.LeftRoom, roomName);
        }
    }

    // Some keywords go to a room each board configures, so a table row can be right on
    // one board and wrong on another. The room we left showing again is a refused jump,
    // which says nothing about the row, and with no known room to compare there's no
    // telling the two apart.
    private void WarnIfLandedElsewhere(string keyword, RoomKey key, string expected, string? leftRoom, string? shown)
    {
        string landed = shown?.Trim() ?? string.Empty;
        if (landed.Length == 0 || string.IsNullOrWhiteSpace(leftRoom)
            || string.Equals(landed, leftRoom.Trim(), StringComparison.OrdinalIgnoreCase))
            return;
        _log?.Warn(LogCat, $"'sys goto {keyword}' landed in '{landed}', but its table row says {key} ('{expected}').");
        bool stopped = _landedElsewhere($"sys goto '{keyword}' landed in '{landed}', not {key}");
        _writeStatus($"[Sys goto '{keyword}' landed in {landed}, but your Sys Goto table says {expected} ({key}). "
            + (stopped ? "Navigation stopped. " : string.Empty)
            + "Correct its room in Settings → BBS + Display.]");
    }

    // True when a jump is fired but its landing hasn't been confirmed yet — surfaced
    // in the bug report so a stuck resync is visible.
    public bool HasArmedLanding => _armed is not null;
    public string? ArmedLandingSummary =>
        _armed is { } a ? $"{a.Key} ('{a.ExpectedName}'), sent {a.SentAt:HH:mm:ss}Z" : null;

    public string? AfterBreakSummary =>
        _afterBreak is { } p ? $"'{p.Loc.Name}', break sent {p.BrokeAt:HH:mm:ss}Z" : null;

    private SysopGotoLocation? FindByName(string name) =>
        _locations().FirstOrDefault(l => string.Equals(l.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase));

    // Extract the location keyword from a typed `sys goto <name>` line (case- and
    // whitespace-tolerant), or null when the line isn't a sys-goto command. Requires a
    // non-empty name after the verb.
    private static string? ParseGotoName(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        string trimmed = line.TrimStart();
        const string prefix = "sys goto ";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string name = trimmed[prefix.Length..].Trim();
        return name.Length > 0 ? name : null;
    }
}
