using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;

namespace MudPlay.Game;

// State machine that drives the auto-disarm flow for @trap <direction> remote
// commands and the walker's trapped exits. Owns the queue of pending requests
// and the telepath-the-sender-on-completion contract.
//
// One request is in flight at a time: Idle → DisarmPending → Idle per direction;
// later requests queue FIFO. A @trap stop aborts whatever's in flight, drains the
// queue, telepaths each queued sender that their trap was cancelled, and returns
// to State.Idle.
//
// Every request disarms directly with `disarm trap <dir>`: the game accepts it
// without searching for the trap first (GAME_MECHANICS "Exit traps — search and disarm"), so a
// confirming `search` would only waste rounds.
//
// The capability gate (CanDisarm) lives in this manager so the handler can
// interrogate it before deciding whether to enqueue or send a denial reply. It
// reads a positive parsed Traps stat OR a class/race game-data trap-skill grant,
// so a character whose Traps value isn't captured yet still counts as able. The
// handler also owns the channel-aware silence (Say when no skill → silent; every
// other channel → a refusal) since the channel context lives at handler dispatch
// time.
//
// An exit we disarmed ourselves stays down until the engine re-arms it (5 minutes
// on Stock, about 2 on Paradigm — GAME_MECHANICS "Exit traps — search and disarm"),
// so a request for it inside that window is answered clear without a disarm, which
// would cost a command and end the sneak. Only our own success starts the clock:
// "already disarmed" means someone else's timer, and we can't tell when it started.
//
// A request ends on a successful disarm, on a reply meaning there's no trap that
// way, or once MaxDisarmAttempts disarms have all failed.
//
// A trap's failure wording is per exit and the imported rooms don't carry it, so a
// trap we've never seen (Paradigm has more trapped exits than the Stock list covers)
// answers with a line nothing recognises. The reply watchdog treats silence as a
// trap that went off — retry, then stop the walk — and logs the lines that arrived,
// so the unknown wording shows up in the program log and the bug report.
public sealed class TrapDisarmManager : IDisposable
{
    private readonly MessageRouter _router;
    private readonly PlayerStats _stats;
    private readonly GameDataCache _gameData;
    private readonly LogService? _log;
    private readonly IDisposable _disarmedSub;
    private readonly IDisposable _triggeredSub;
    private readonly IDisposable _failedAnySub;
    private readonly IDisposable _noEffectSub;
    private readonly IDisposable _alreadyDisarmedSub;
    private readonly WireSender _wire = new();
    private readonly Func<TimeSpan, Action, IDisposable>? _scheduleDelay;
    private readonly Func<DateTimeOffset> _clock;
    private Func<RoomKey?>? _currentRoom;

    // When we last disarmed each exit, keyed by room + short direction.
    private readonly Dictionary<(RoomKey Room, string Dir), DateTimeOffset> _ourDisarms = new();
    // The room the request in flight started in; null when the room wasn't known.
    private RoomKey? _currentRoomKey;
    private bool _disposed;

    // A disarm reply comes back with the next prompt; this is long enough to cover lag.
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(8);
    private const int MaxLinesKept = 6;
    private IDisposable? _replyWatchdog;
    // Non-prompt lines seen since the last disarm went out, for the timeout log.
    private readonly List<string> _linesSinceDisarm = new();

    // FIFO queue of pending trap requests (oldest at the front).
    private readonly Queue<TrapRequest> _queue = new();
    // The currently-in-flight request, or null when idle.
    private TrapRequest? _current;
    private State _state = State.Idle;
    private bool _heldForRest;
    // True while the movement engines hold for a rest or a meditate. Unset → never.
    private Func<bool>? _restHold;
    private int _disarmAttempts;
    // Stock "failed to disarm any" replies on the request in flight. That line also
    // answers an exit with no trap, so they only count as fumbles once the same
    // request proves a trap is there (a success or a trap going off).
    private int _unconfirmedFailures;

    // One disarm attempt settled: true = disarmed, false = failed. Feeds the
    // session's disarm success rate.
    public event Action<bool>? DisarmAttempted;

    // Max disarm trap <dir> attempts before giving up. Default 5; pushed from
    // Models.Profile.OtherSettings.MaxTrapDisarmAttempts.
    public int MaxDisarmAttempts { get; set; } = 5;

    // True when the local character has the Traps skill — either a positive parsed
    // Traps stat, or a selected class / race that grants the skill in the active
    // game data. The inference covers the gap when the Traps value hasn't been
    // captured yet (a freshly loaded profile that hasn't run `stat` this session, a
    // brand-new character): race and class are chosen at creation and shown on the
    // train-stats screen, so the game-data grant tells us capability even with a
    // defaulted-zero Traps value. Mirrors the party-delegation capability check.
    public bool CanDisarm =>
        _stats.Traps > 0
        || AbilityNames.ClassOrRaceGrantsTraps(_gameData, _stats.Class, _stats.Race);

    // The disarm skill, which the game never shows: the Traps `stat` prints is the
    // find skill, carrying worn +FindTraps / +Traps gear (abilities 40 / 179) that
    // doesn't help a disarm, while +DisarmTraps gear (41) helps only the disarm.
    // Class and race grants carry no value in any game-data set, so gear is the only
    // difference. Null until a positive Traps has been read — no skill, no odds.
    public int? DisarmSkill
    {
        get
        {
            if (_stats.Traps <= 0) return null;
            (int find, int disarm) = _wornTrapBonuses?.Invoke() ?? (0, 0);
            return Math.Max(0, _stats.Traps - find + disarm);
        }
    }

    public TrapDisarmOdds? DisarmOdds => DisarmSkill is { } skill ? TrapDisarmOdds.For(skill) : null;

    private Func<(int Find, int Disarm)>? _wornTrapBonuses;

    // Worn gear's (FindTraps + Traps, DisarmTraps) ability totals.
    public void SetWornTrapBonuses(Func<(int Find, int Disarm)> bonuses) => _wornTrapBonuses = bonuses;

    // Diagnostic: true when CanDisarm is satisfied ONLY by the class/race game-data
    // grant (no parsed Traps value). Surfaced in the bug report so a "walker walked
    // through a trap" capture shows whether capability came from stats or inference.
    public bool SkillInferredFromClassOrRace =>
        _stats.Traps <= 0
        && AbilityNames.ClassOrRaceGrantsTraps(_gameData, _stats.Class, _stats.Race);

    // Current state, for diagnostics.
    public State CurrentState => _state;

    // Current request's direction, or null when idle.
    public string? CurrentDirection => _current?.Direction;

    // Outstanding queue depth (excludes the in-flight request).
    public int QueueDepth => _queue.Count;

    // The lines that arrived the last time a disarm went unanswered — the likely
    // wording of a trap we don't know yet. Surfaced in the bug report.
    public string? LastUnansweredReply { get; private set; }

    // How long a trap we disarmed stays down, by realm.
    public TimeSpan RearmTime => _gameData.ActiveRealm == RealmType.ParaMud
        ? TimeSpan.FromMinutes(2)
        : TimeSpan.FromMinutes(5);

    // The exits we disarmed that haven't re-armed yet, for the bug report.
    public string RecentDisarmsDescription()
    {
        DateTimeOffset now = _clock();
        IEnumerable<string> live = _ourDisarms
            .Where(kv => now - kv.Value < RearmTime)
            .Select(kv => $"{kv.Key.Room.Map}/{kv.Key.Room.Room} {kv.Key.Dir} {(now - kv.Value).TotalSeconds:0}s ago");
        string text = string.Join(", ", live);
        return text.Length == 0 ? "(none)" : text;
    }

    // scheduleDelay runs the reply watchdog on the router's thread; null leaves
    // the wait to the reply lines alone.
    public TrapDisarmManager(
        MessageRouter router, PlayerStats stats, GameDataCache gameData, LogService? log = null,
        Func<TimeSpan, Action, IDisposable>? scheduleDelay = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(gameData);
        _router   = router;
        _stats    = stats;
        _gameData = gameData;
        _log      = log;
        _scheduleDelay = scheduleDelay;
        _clock = clock ?? (static () => DateTimeOffset.Now);

        _disarmedSub = _router.Subscribe(KnownPatterns.TrapDisarmedSuccess, OnDisarmedSuccess);
        _triggeredSub = _router.Subscribe(KnownPatterns.TrapDisarmTriggered, OnDisarmTriggered);
        _failedAnySub = _router.Subscribe(KnownPatterns.TrapDisarmFailedAny, OnDisarmFailedAny);
        _noEffectSub = _router.Subscribe(KnownPatterns.CommandNoEffect, OnNoEffect);
        _alreadyDisarmedSub = _router.Subscribe(KnownPatterns.TrapAlreadyDisarmed, OnAlreadyDisarmed);
        _router.LineDispatched += OnLineDispatched;
    }

    // Bind the wire-sender. Same shape as the rest of the engine-side handlers —
    // MainWindowVM supplies the gate-wrapped SendUserInput.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // A disarm stands a resting character up (GAME_MECHANICS "What ends a rest"),
    // and the rest that follows a trap going off is there because HP is low: the
    // next try waits it out rather than risk the trap again on what's left.
    public void SetRestHold(Func<bool> isHeld) => _restHold = isHeld;

    // The in-flight disarm is waiting for a rest hold to end.
    public bool HeldForRest => _heldForRest;

    // The rest hold changed: a disarm that was waiting on it goes out once it's over.
    public void NotifyRestHoldChanged()
    {
        if (_disposed || !_heldForRest || _state != State.DisarmPending || _current is null) return;
        if (_restHold?.Invoke() == true) return;
        _heldForRest = false;
        _log?.Log(LogSeverity.Info, "Trap", "Rest over — disarming.");
        SendDisarm();
    }

    // The room the player stands in, which keys the remembered disarms.
    public void SetCurrentRoom(Func<RoomKey?> room) => _currentRoom = room;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disarmedSub.Dispose();
        _triggeredSub.Dispose();
        _failedAnySub.Dispose();
        _noEffectSub.Dispose();
        _alreadyDisarmedSub.Dispose();
        _router.LineDispatched -= OnLineDispatched;
        CancelWatchdog();
    }

    // Queue a new @trap <direction> request. direction must already be normalised
    // to the short form ("n" / "ne" / "u" / etc.). reply is the per-request
    // channel-bound callback the handler captured at dispatch time; the manager
    // invokes it once on terminal state (success / stop).
    //
    // Same-direction duplicate while already in-flight or queued is silently
    // ignored — we're already on it; sending a second {Trap to the N disarmed.}
    // would be misleading.
    public void Enqueue(string direction, string sender, Action<string> reply)
    {
        if (string.IsNullOrEmpty(direction)) return;
        ArgumentNullException.ThrowIfNull(reply);

        // Same-direction duplicate while already in flight → ignore.
        if (_current is { } cur && cur.Direction.Equals(direction, StringComparison.OrdinalIgnoreCase))
        {
            _log?.Log(LogSeverity.Debug, "Trap",
                $"@trap {direction} from {sender} duplicates in-flight request; ignored.");
            return;
        }
        // Same-direction duplicate in the queue → ignore.
        foreach (TrapRequest q in _queue)
        {
            if (q.Direction.Equals(direction, StringComparison.OrdinalIgnoreCase))
            {
                _log?.Log(LogSeverity.Debug, "Trap",
                    $"@trap {direction} from {sender} duplicates queued request; ignored.");
                return;
            }
        }

        _queue.Enqueue(new TrapRequest(direction, sender, reply));
        _log?.Log(LogSeverity.Info, "Trap",
            $"@trap {direction} queued (sender={sender}, depth={_queue.Count}).");
        TryStartNext();
    }

    // Abort the current in-flight request (if any), drain the queue, and telepath
    // each pending sender that their trap was cancelled. The stop sender does NOT
    // receive a notification here — the handler that called Stop sends its own
    // {ok} ack to them.
    public void StopAll()
    {
        if (_current is { } cur)
        {
            cur.Reply(TrapReply.Stopped);
            _current = null;
            _currentRoomKey = null;
        }
        while (_queue.Count > 0)
        {
            TrapRequest q = _queue.Dequeue();
            q.Reply(TrapReply.Stopped);
        }
        _state = State.Idle;
        _heldForRest = false;
        _disarmAttempts = 0;
        _unconfirmedFailures = 0;
        CancelWatchdog();
        _log?.Log(LogSeverity.Info, "Trap", "Trap flow stopped — queue drained.");
    }

    // ----- State machine -------------------------------------------------

    private void TryStartNext()
    {
        // A reply can enqueue the next request re-entrantly, so re-check the state
        // each pass rather than assume we're still idle.
        while (_state == State.Idle && _queue.Count > 0)
        {
            TrapRequest next = _queue.Dequeue();
            RoomKey? room = _currentRoom?.Invoke();
            if (StillDisarmed(room, next.Direction) is { } ago)
            {
                _log?.Log(LogSeverity.Info, "Trap",
                    $"We disarmed the trap to the {next.Direction} {ago.TotalSeconds:0}s ago "
                    + $"(re-arms after {RearmTime.TotalMinutes:0} min) — crossing without a disarm.");
                next.Reply(TrapReply.DisarmedEarlier(next.Direction, ago));
                continue;
            }
            _current = next;
            _currentRoomKey = room;
            _disarmAttempts = 0;
            _unconfirmedFailures = 0;
            _state = State.DisarmPending;
            SendDisarm();
        }
    }

    // How long ago we disarmed this exit, while that's still under the re-arm time.
    private TimeSpan? StillDisarmed(RoomKey? room, string direction)
    {
        if (room is not { } r || NormaliseDirection(direction) is not { } dir) return null;
        if (!_ourDisarms.TryGetValue((r, dir), out DateTimeOffset at)) return null;
        TimeSpan ago = _clock() - at;
        if (ago >= TimeSpan.Zero && ago < RearmTime) return ago;
        _ourDisarms.Remove((r, dir));
        return null;
    }

    // Remember (disarmed) or forget (the trap is armed) the exit in flight.
    private void NoteExitState(bool disarmed)
    {
        if (_currentRoomKey is not { } r || _current is null
            || NormaliseDirection(_current.Direction) is not { } dir) return;
        if (disarmed) _ourDisarms[(r, dir)] = _clock();
        else _ourDisarms.Remove((r, dir));
    }

    private void SendDisarm()
    {
        if (_current is not { } cur) return;
        if (_restHold?.Invoke() == true)
        {
            CancelWatchdog();
            _heldForRest = true;
            _log?.Log(LogSeverity.Info, "Trap",
                $"Disarm {cur.Direction} held — resting; trying once the rest is over.");
            return;
        }
        _disarmAttempts++;
        _linesSinceDisarm.Clear();
        CancelWatchdog();
        _replyWatchdog = _scheduleDelay?.Invoke(ReplyTimeout, OnReplyTimeout);
        _wire.Send($"disarm trap {cur.Direction}");
        _log?.Log(LogSeverity.Info, "Trap",
            $"Disarming {cur.Direction} (attempt {_disarmAttempts}/{MaxDisarmAttempts}).");
    }

    private void OnDisarmedSuccess(MatchResult result)
    {
        if (_state != State.DisarmPending) return;
        if (_current is not { } cur) return;
        if (!MatchesCurrentDirection(result)) return;

        ConfirmFailures();
        DisarmAttempted?.Invoke(true);
        NoteExitState(disarmed: true);
        cur.Reply(TrapReply.Disarmed(cur.Direction));
        CompleteCurrent();
    }

    // The disarm failed and set the trap off, both realms. Each trap prints its own
    // wording (`You try to disarm the trap, but instead trigger it!`, `You trigger
    // the trap, and a large spear shoots out!`, …); none names a direction, so it's
    // taken for the disarm we have pending. A trap that fired is a trap that's
    // there, so unlike Stock's ambiguous failure line this never walks on. Try again up to
    // MaxDisarmAttempts, then give up and report it — the walker stops rather
    // than walk into a trap it couldn't clear.
    private void OnDisarmTriggered(MatchResult _) => HandleTriggered();

    private void HandleTriggered()
    {
        if (_state != State.DisarmPending) return;
        if (_current is not { } cur) return;
        ConfirmFailures();
        DisarmAttempted?.Invoke(false);
        NoteExitState(disarmed: false);
        if (_disarmAttempts >= MaxDisarmAttempts)
        {
            _log?.Log(LogSeverity.Info, "Trap",
                $"Disarm {cur.Direction} set the trap off {_disarmAttempts} time(s) — giving up.");
            cur.Reply(TrapReply.CouldNotDisarm(cur.Direction, _disarmAttempts));
            CompleteCurrent();
            return;
        }
        _log?.Log(LogSeverity.Info, "Trap", $"Disarm {cur.Direction} set the trap off — trying again.");
        SendDisarm();
    }

    // Stock's `You failed to disarm any trap to the <dir>.` is its only failure line,
    // and it also answers a direction with no trap (user, 2026-09-27) — so it can't
    // tell a fumble from an empty exit. Retry up to MaxDisarmAttempts; if every try
    // says the same, take it as no trap there (already disarmed, or not set) and
    // report the exit clear so the walk carries on (the user's call: the worst case
    // is walking into a live trap, which a failed disarm risks anyway).
    private void OnDisarmFailedAny(MatchResult result)
    {
        if (_state != State.DisarmPending) return;
        if (_current is not { } cur) return;
        if (!MatchesCurrentDirection(result)) return;
        _unconfirmedFailures++;
        if (_disarmAttempts < MaxDisarmAttempts)
        {
            _log?.Log(LogSeverity.Info, "Trap", $"Disarm {cur.Direction} failed — trying again.");
            SendDisarm();
            return;
        }
        _log?.Log(LogSeverity.Info, "Trap",
            $"Disarm {cur.Direction} failed {_disarmAttempts} time(s) — taking it as no trap there.");
        cur.Reply(TrapReply.NoTrapAfterFailures(cur.Direction, _disarmAttempts));
        CompleteCurrent();
    }

    // Paradigm: `disarm trap <dir>` answered "Your command had no effect.": there's no
    // trap that way (already disarmed, or not set right now), so there's nothing to do
    // and the exit is clear. Only read while our disarm is pending — the same line
    // answers any other command the server refuses.
    private void OnNoEffect(MatchResult _)
    {
        if (_state != State.DisarmPending) return;
        if (_current is not { } cur) return;
        _log?.Log(LogSeverity.Info, "Trap", $"No trap to the {cur.Direction} — nothing to disarm.");
        cur.Reply(TrapReply.NoTrap(cur.Direction));
        CompleteCurrent();
    }

    // Paradigm: "The trap is already disarmed." The trap is down, so the exit is safe
    // to cross (GAME_MECHANICS "Exit traps — search and disarm"). Not an attempt: no
    // roll was made.
    private void OnAlreadyDisarmed(MatchResult _)
    {
        if (_state != State.DisarmPending) return;
        if (_current is not { } cur) return;
        _log?.Log(LogSeverity.Info, "Trap", $"Trap to the {cur.Direction} is already disarmed — crossing.");
        cur.Reply(TrapReply.AlreadyDisarmed(cur.Direction));
        CompleteCurrent();
    }

    // Compare the captured \w+ from a regex match against the current request's
    // direction. BOTH sides are normalised to short form before compare — the
    // game prints the long form ("southeast" / "north" / "up"), and callers may
    // enqueue either form: the @trap handler passes the short form it parsed,
    // but the walker enqueues the long-form direction word. Normalising only the
    // observed side left a walker-enqueued "southeast" never matching the game's
    // long-form reply, so the flow stalled (report 132150). Normalising the stored
    // side too makes the match robust to whichever form the caller queued.
    private bool MatchesCurrentDirection(MatchResult result)
    {
        if (_current is null) return false;
        if (result.Groups.Count == 0) return false;
        string? observed = NormaliseDirection(result.Groups[0]);
        string? expected = NormaliseDirection(_current.Direction);
        return observed is not null
               && expected is not null
               && observed.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    // No recognised reply in time. Most likely a trap whose failure wording isn't in
    // our list: handle it as one that went off (a lost reply just costs a retry) and
    // log what arrived so the wording can be added.
    private void OnReplyTimeout()
    {
        _replyWatchdog = null;
        if (_disposed || _state != State.DisarmPending || _current is not { } cur) return;
        LastUnansweredReply = _linesSinceDisarm.Count > 0
            ? string.Join(" | ", _linesSinceDisarm)
            : "(no lines)";
        _log?.Log(LogSeverity.Info, "Trap",
            $"disarm trap {cur.Direction}: no recognised reply in {ReplyTimeout.TotalSeconds:0}s — "
            + $"treating it as a trap that went off. Lines since the disarm: {LastUnansweredReply}");
        HandleTriggered();
    }

    private void OnLineDispatched(LineExtractor.EmittedLine line)
    {
        if (_state != State.DisarmPending || line.IsPromptLine) return;
        string text = line.Text.Trim();
        if (text.Length == 0 || _linesSinceDisarm.Count >= MaxLinesKept) return;
        if (text.StartsWith("disarm trap", StringComparison.OrdinalIgnoreCase)) return;   // our echo
        _linesSinceDisarm.Add(text);
    }

    // A trap proved to be there, so the request's earlier "failed to disarm any"
    // replies were real fumbles.
    private void ConfirmFailures()
    {
        for (; _unconfirmedFailures > 0; _unconfirmedFailures--) DisarmAttempted?.Invoke(false);
    }

    private void CancelWatchdog()
    {
        _replyWatchdog?.Dispose();
        _replyWatchdog = null;
    }

    private void CompleteCurrent()
    {
        CancelWatchdog();
        _current = null;
        _currentRoomKey = null;
        _state = State.Idle;
        _heldForRest = false;
        _disarmAttempts = 0;
        _unconfirmedFailures = 0;
        TryStartNext();
    }

    // Normalise a direction token (long or short) to its canonical short form.
    // Returns null for unrecognised inputs so the handler can deny with a clear
    // "unknown direction" message instead of queueing a request that will never
    // resolve.
    public static string? NormaliseDirection(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        return input.Trim().ToLowerInvariant() switch
        {
            "n"  or "north"     => "n",
            "s"  or "south"     => "s",
            "e"  or "east"      => "e",
            "w"  or "west"      => "w",
            "ne" or "northeast" => "ne",
            "nw" or "northwest" => "nw",
            "se" or "southeast" => "se",
            "sw" or "southwest" => "sw",
            "u"  or "up"        => "u",
            "d"  or "down"      => "d",
            _                   => null,
        };
    }

    // Phases of one in-flight trap request.
    public enum State
    {
        Idle,
        DisarmPending,
    }

    // One queued (or in-flight) trap request. Reply is the channel-bound callback
    // the handler captured from RemoteCommandContext at dispatch time — invoking
    // it later telepaths / says-back to the original sender on the same channel
    // they used.
    private sealed record TrapRequest(string Direction, string Sender, Action<string> Reply);
}
