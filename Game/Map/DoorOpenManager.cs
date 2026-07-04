using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Map;

// State machine that drives the walker's bash → pick → open flow for a
// single door. Mirrors TrapDisarmManager's shape: one request in flight
// at a time, FIFO queue for follow-ups, the walker (or any other caller)
// gets a single terminal DoorOpenResult via callback when the request
// settles.
//
// Decision matrix per request (see DoorPolicy):
//   1) Honour OtherSettings.PicklocksOverBash — if set, try pick <dir>
//      first when picking is viable.
//   2) Otherwise prefer bash <dir> when the door is bashable (canBash
//      from the modifier, strength req feasible vs the active set's
//      MaxStrengthIndex.MaxAchievableStrength, live PlayerStats.Strength
//      meets the threshold).
//   3) If only one verb is viable, use it. If neither is, fail
//      immediately with DoorOpenResult.Failed("no viable verb").
//   4) On verb failure (server "fails" line), retry up to the configured
//      cap; if exhausted AND the other verb is viable, fall back to it;
//      otherwise fail.
//   5) Bash success → Opened directly (bash both unlocks AND swings the
//      door). Pick success → open <dir> → on "is now open" → Opened.
//
// Single-pattern matchers: the regex doesn't capture direction so other
// in-flight commands (manual user typing) could in theory match the wrong
// context. The walker funnels through one request at a time and the
// keyed-door FSM inherits the same matchers — practical conflict is
// negligible.
public sealed class DoorOpenManager : IDisposable
{
    private readonly MessageRouter _router;
    private readonly PlayerStats _stats;
    private readonly Func<int> _maxBashProvider;
    private readonly Func<int> _maxPickProvider;
    private readonly Func<int> _maxBashStrengthProvider;
    private readonly Func<bool> _picklocksOverBashProvider;
    private readonly Func<int, string?> _itemNameLookup;
    private readonly LogService? _log;
    private readonly IDisposable _bashOkSub;
    private readonly IDisposable _bashFailSub;
    private readonly IDisposable _pickOkSub;
    private readonly IDisposable _pickFailSub;
    private readonly IDisposable _notLockedSub;
    private readonly IDisposable _openedSub;
    private readonly IDisposable _alreadyOpenSub;
    private readonly IDisposable _lockedSub;
    private readonly IDisposable _keyOkSub;
    private readonly IDisposable _keyUnknownSub;
    private readonly WireSender _wire = new();
    private bool _disposed;

    private readonly Queue<DoorRequest> _queue = new();
    private DoorRequest? _current;
    private DoorState _state = DoorState.Idle;
    private string? _verb;                                // "bash" / "pick"
    private int _verbAttempts;
    private bool _triedFallbackVerb;

    // Current state — exposed for tests + diagnostics.
    public DoorState CurrentState => _state;

    // Direction of the in-flight request, or null when idle.
    public string? CurrentDirection => _current?.DirectionShort;

    // Outstanding queue depth (excludes the in-flight request).
    public int QueueDepth => _queue.Count;

    public DoorOpenManager(
        MessageRouter router,
        PlayerStats stats,
        Func<int> maxBashAttemptsProvider,
        Func<int> maxPickAttemptsProvider,
        Func<bool> picklocksOverBashProvider,
        Func<int, string?>? itemNameLookup = null,
        Func<int>? maxBashableStrengthProvider = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(maxBashAttemptsProvider);
        ArgumentNullException.ThrowIfNull(maxPickAttemptsProvider);
        ArgumentNullException.ThrowIfNull(picklocksOverBashProvider);
        _router = router;
        _stats = stats;
        _maxBashProvider = maxBashAttemptsProvider;
        _maxPickProvider = maxPickAttemptsProvider;
        _picklocksOverBashProvider = picklocksOverBashProvider;
        // The per-set bash ceiling (MaxStrengthIndex). Absent it, fall back to the
        // conservative constant so plain-door tests can construct without game data.
        _maxBashStrengthProvider = maxBashableStrengthProvider
                                   ?? (() => DoorPolicy.UnbashableStrengthThreshold);
        // Default to "no items known" so plain-door tests can construct
        // the manager without an ItemNameStore.
        _itemNameLookup = itemNameLookup ?? (_ => null);
        _log = log;

        _bashOkSub      = _router.Subscribe(KnownPatterns.DoorBashSuccess,      OnBashSuccess);
        _bashFailSub    = _router.Subscribe(KnownPatterns.DoorBashFailure,      OnBashFailure);
        _pickOkSub      = _router.Subscribe(KnownPatterns.DoorPickSuccess,      OnPickSuccess);
        _pickFailSub    = _router.Subscribe(KnownPatterns.DoorPickFailure,      OnPickFailure);
        _notLockedSub   = _router.Subscribe(KnownPatterns.DoorPickNotLocked,    OnPickNotLocked);
        _openedSub      = _router.Subscribe(KnownPatterns.DoorOpenedNow,        OnOpened);
        _alreadyOpenSub = _router.Subscribe(KnownPatterns.DoorAlreadyOpen,      OnOpened);
        _lockedSub      = _router.Subscribe(KnownPatterns.DoorIsLocked,         OnIsLocked);
        _keyOkSub       = _router.Subscribe(KnownPatterns.DoorKeyUnlockSuccess, OnKeyUnlockSuccess);
        _keyUnknownSub  = _router.Subscribe(KnownPatterns.DoorKeyUnknown,       OnKeyUnknown);
    }

    // Bind the wire-sender. Same shape as TrapDisarmManager.SetWireSender
    // — MainWindowVM supplies the gate-wrapped SendUserInput.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Test seam — bytes the manager asked to write to the wire.
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bashOkSub.Dispose();
        _bashFailSub.Dispose();
        _pickOkSub.Dispose();
        _pickFailSub.Dispose();
        _notLockedSub.Dispose();
        _openedSub.Dispose();
        _alreadyOpenSub.Dispose();
        _lockedSub.Dispose();
        _keyOkSub.Dispose();
        _keyUnknownSub.Dispose();
    }

    // Queue a door-open request. The walker normalises direction to short
    // form ("n" / "ne" / "u") before calling. The callback fires exactly
    // once on terminal state. keyItemId is 0 for plain doors; non-zero
    // indexes the door's key in ItemNameStore. When the door is keyed and
    // has a stat alternative, the manager tries bash/pick first to save
    // the key's limited charges; only falls back to the single-shot
    // use <keyName> <dir> + open <dir> when bash/pick are unviable or
    // exhaust.
    public void Enqueue(
        Direction direction,
        int statRequirement,
        bool canBash,
        int keyItemId,
        string sender,
        Action<DoorOpenResult> reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        _queue.Enqueue(new DoorRequest(direction, statRequirement, canBash, keyItemId, sender, reply));
        _log?.Info("Door",
            $"open {DirectionShort(direction)} queued (sender={sender}, key={keyItemId}, depth={_queue.Count}).");
        TryStartNext();
    }

    // Overload for plain-door callers (no key). Equivalent to the keyed
    // Enqueue with keyItemId = 0.
    public void Enqueue(
        Direction direction,
        int statRequirement,
        bool canBash,
        string sender,
        Action<DoorOpenResult> reply)
        => Enqueue(direction, statRequirement, canBash, keyItemId: 0, sender, reply);

    // Abort the current request (if any) + drain the queue. Pending
    // callers receive a Failed("flow stopped") reply.
    public void StopAll()
    {
        if (_current is { } cur)
        {
            cur.Reply(new DoorOpenResult.Failed("door flow stopped"));
            _current = null;
        }
        while (_queue.Count > 0)
        {
            DoorRequest q = _queue.Dequeue();
            q.Reply(new DoorOpenResult.Failed("door flow stopped"));
        }
        _state = DoorState.Idle;
        _verb = null;
        _verbAttempts = 0;
        _triedFallbackVerb = false;
        _log?.Info("Door", "Door flow stopped — queue drained.");
    }

    // ----- request kickoff --------------------------------------------

    private void TryStartNext()
    {
        if (_state != DoorState.Idle) return;
        if (_queue.Count == 0) return;
        _current = _queue.Dequeue();
        _state = DoorState.SelectingVerb;
        _verbAttempts = 0;
        _triedFallbackVerb = false;
        StartChosenVerb();
    }

    private void StartChosenVerb()
    {
        if (_current is not { } cur) return;

        // Keyed doors with NO stat alternative go straight to the key
        // path. StatRequirement == 0 on a keyed door means "no
        // picklocks/strength alt" — distinct from a plain door where
        // 0 means "open by anyone". The walker can't tell these apart
        // from RoomExit alone; the keyed-door branch is the
        // discriminator here.
        if (cur.KeyItemId > 0 && cur.StatRequirement <= 0)
        {
            StartUseKey();
            return;
        }

        string? verb = DoorPolicy.ChooseVerb(
            cur.StatRequirement,
            cur.CanBash,
            _stats.Strength,
            _stats.Picklocks,
            _picklocksOverBashProvider(),
            _maxBashStrengthProvider());
        if (verb is null)
        {
            // No bash/pick viable. Try the key path if available;
            // otherwise we're stuck.
            if (cur.KeyItemId > 0)
            {
                StartUseKey();
                return;
            }
            FailCurrent($"no viable open verb (req {cur.StatRequirement}, str {_stats.Strength}, picks {_stats.Picklocks}, canBash {cur.CanBash})");
            return;
        }
        StartVerb(verb);
    }

    private void StartUseKey()
    {
        if (_current is not { } cur) return;
        string? keyName = _itemNameLookup(cur.KeyItemId);
        if (string.IsNullOrWhiteSpace(keyName))
        {
            FailCurrent($"key item {cur.KeyItemId} not in active item table");
            return;
        }
        _state = DoorState.WaitingUseKey;
        _wire.Send($"use {keyName} {cur.DirectionShort}");
        _log?.Info("Door",
            $"use {keyName} {cur.DirectionShort} (single-shot — keys have limited charges).");
    }

    private void StartVerb(string verb)
    {
        if (_current is not { } cur) return;
        _verb = verb;
        _verbAttempts = 0;
        _state = verb == "bash" ? DoorState.WaitingBash : DoorState.WaitingPick;
        SendVerb();
    }

    private void SendVerb()
    {
        if (_current is not { } cur || _verb is null) return;
        _verbAttempts++;
        _wire.Send($"{_verb} {cur.DirectionShort}");
        int cap = _verb == "bash" ? _maxBashProvider() : _maxPickProvider();
        _log?.Info("Door",
            $"{_verb} {cur.DirectionShort} (attempt {_verbAttempts}/{cap}).");
    }

    private void SendOpen()
    {
        if (_current is not { } cur) return;
        _state = DoorState.WaitingOpen;
        _wire.Send($"open {cur.DirectionShort}");
        _log?.Info("Door", $"open {cur.DirectionShort} (after pick success).");
    }

    // ----- message handlers -------------------------------------------

    private void OnBashSuccess(MatchResult _)
    {
        if (_state != DoorState.WaitingBash) return;
        if (_current is null) return;
        _log?.Info("Door", $"bash {_current.DirectionShort} succeeded.");
        SucceedCurrent();
    }

    private void OnBashFailure(MatchResult _)
    {
        if (_state != DoorState.WaitingBash) return;
        if (_current is null) return;
        int cap = _maxBashProvider();
        if (_verbAttempts < cap)
        {
            SendVerb();
            return;
        }
        TryFallbackOrFail("bash exhausted");
    }

    private void OnPickSuccess(MatchResult _)
    {
        if (_state != DoorState.WaitingPick) return;
        if (_current is null) return;
        _log?.Info("Door", $"pick {_current.DirectionShort} succeeded — sending open.");
        SendOpen();
    }

    private void OnPickFailure(MatchResult _)
    {
        if (_state != DoorState.WaitingPick) return;
        if (_current is null) return;
        int cap = _maxPickProvider();
        if (_verbAttempts < cap)
        {
            SendVerb();
            return;
        }
        TryFallbackOrFail("pick exhausted");
    }

    private void OnPickNotLocked(MatchResult _)
    {
        if (_state != DoorState.WaitingPick) return;
        if (_current is null) return;
        // Pick on an already-unlocked door — go straight to open.
        _log?.Info("Door", $"pick {_current.DirectionShort}: door wasn't locked — sending open.");
        SendOpen();
    }

    private void OnOpened(MatchResult _)
    {
        if (_current is null) return;
        // "is now open" / "already open" — the door is in the
        // desired state regardless of which verb just landed on the
        // wire. Accept from any active waiting state so a bash on an
        // already-open door (server replies "The door is already
        // open.") doesn't strand the FSM waiting for a
        // "bashed the door open" line that will never arrive.
        // Without this, the walker stalls indefinitely on the very
        // next door after a successful bash on the previous one.
        if (_state is DoorState.WaitingBash
                   or DoorState.WaitingPick
                   or DoorState.WaitingOpen
                   or DoorState.WaitingUseKey)
        {
            _log?.Info("Door",
                $"door {_current.DirectionShort} confirmed open (was {_state}).");
            SucceedCurrent();
        }
    }

    private void OnKeyUnlockSuccess(MatchResult _)
    {
        if (_state != DoorState.WaitingUseKey) return;
        if (_current is null) return;
        _log?.Info("Door", $"key unlock {_current.DirectionShort} succeeded — sending open.");
        SendOpen();
    }

    private void OnKeyUnknown(MatchResult _)
    {
        if (_state != DoorState.WaitingUseKey) return;
        if (_current is null) return;
        // "You have no <key>" / "You don't have" — terminal failure.
        // Keys are single-shot; no retry on the use verb.
        FailCurrent("use-key failed (key missing or wrong)");
    }

    private void OnIsLocked(MatchResult _)
    {
        if (_current is null) return;

        // Plain door open path hit a locked door. If a key is
        // available, swing into the key sequence; otherwise fail.
        if (_state == DoorState.WaitingOpen)
        {
            if (_current.KeyItemId > 0)
            {
                _log?.Info("Door",
                    $"open {_current.DirectionShort}: door is locked — swinging to key sequence.");
                StartUseKey();
                return;
            }
            FailCurrent("door is locked (no key available)");
        }
    }

    // ----- terminal transitions ---------------------------------------

    private void TryFallbackOrFail(string reason)
    {
        if (_current is not { } cur) return;
        if (_triedFallbackVerb)
        {
            // Bash + pick both exhausted. Keyed doors get one more
            // chance via the use-key path before we surrender.
            if (cur.KeyItemId > 0)
            {
                _log?.Info("Door",
                    $"{reason}; falling back to key for door {cur.DirectionShort}.");
                StartUseKey();
                return;
            }
            FailCurrent($"{reason}; fallback verb also exhausted");
            return;
        }
        _triedFallbackVerb = true;
        string? other = _verb == "bash" ? "pick" : "bash";
        // Validate the fallback is viable per the same policy gate.
        bool otherViable = other == "bash"
            ? cur.CanBash
              && cur.StatRequirement <= _maxBashStrengthProvider()
              && (cur.StatRequirement <= 0 || _stats.Strength >= cur.StatRequirement)
            : (cur.StatRequirement <= 0 || _stats.Picklocks >= cur.StatRequirement);
        if (!otherViable || other is null)
        {
            // No alt verb viable. Keyed doors fall through to the key.
            if (cur.KeyItemId > 0)
            {
                _log?.Info("Door",
                    $"{reason}; alt verb unviable, falling back to key for door {cur.DirectionShort}.");
                StartUseKey();
                return;
            }
            FailCurrent($"{reason}; no viable fallback verb");
            return;
        }
        _log?.Info("Door", $"falling back from {_verb} to {other}.");
        StartVerb(other);
    }

    private void SucceedCurrent()
    {
        if (_current is not { } cur) return;
        cur.Reply(DoorOpenResult.Opened.Instance);
        Reset();
    }

    private void FailCurrent(string reason)
    {
        if (_current is not { } cur) return;
        _log?.Warn("Door", $"door {cur.DirectionShort} failed: {reason}.");
        cur.Reply(new DoorOpenResult.Failed(reason));
        Reset();
    }

    private void Reset()
    {
        _current = null;
        _state = DoorState.Idle;
        _verb = null;
        _verbAttempts = 0;
        _triedFallbackVerb = false;
        TryStartNext();
    }

    // ----- helpers ----------------------------------------------------

    internal static string DirectionShort(Direction d) => d switch
    {
        Direction.N => "n",
        Direction.S => "s",
        Direction.E => "e",
        Direction.W => "w",
        Direction.NE => "ne",
        Direction.NW => "nw",
        Direction.SE => "se",
        Direction.SW => "sw",
        Direction.U => "u",
        Direction.D => "d",
        _ => "?",
    };

    // Public FSM state — exposed for tests + diagnostics.
    public enum DoorState
    {
        // No request in flight.
        Idle,
        // Picking the first verb based on player stats + settings.
        SelectingVerb,
        // Sent bash <dir>; awaiting success/failure line.
        WaitingBash,
        // Sent pick <dir>; awaiting success/failure line.
        WaitingPick,
        // Pick succeeded (or door was unlocked); sent open <dir>; awaiting opened line.
        WaitingOpen,
        // Sent use <keyName> <dir>; awaiting unlock-success / key-missing.
        WaitingUseKey,
    }

    private sealed record DoorRequest(
        Direction Direction,
        int StatRequirement,
        bool CanBash,
        int KeyItemId,
        string Sender,
        Action<DoorOpenResult> Reply)
    {
        public string DirectionShort => DoorOpenManager.DirectionShort(Direction);
    }
}
