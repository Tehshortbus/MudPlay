namespace FujinTerm.Game.Combat;

/// <summary>
/// Phase 11 — divides the session's <i>in-game</i> wall-clock time across the
/// player's activities for the Time Analysis panel (a reproduction of the
/// MegaMUD Time Analysis screen): how long spent <b>waiting</b>, <b>moving</b>,
/// <b>attacking</b>, <b>resting for HP</b>, and <b>resting for mana</b>, plus
/// the overlay time spent <b>blinded</b>, <b>poisoned</b>, <b>diseased</b>,
/// <b>confused</b>, and <b>held</b> (movement-prevented). Produces a
/// <see cref="TimeAnalysisStats"/> snapshot the window binds.
/// </summary>
/// <remarks>
/// <para>
/// The five activity buckets are mutually exclusive — at any instant the player
/// is in exactly one — so they partition the session and sum to "time on". A
/// priority resolves overlap: combat &gt; movement &gt; resting &gt; waiting.
/// Blinded / poisoned / diseased / confused / held are <i>overlays</i> measured
/// independently: an affliction runs concurrently with whatever you're doing
/// (you stay poisoned through a fight), so folding it into the activity
/// partition would swallow real combat or resting time and misrepresent the
/// session.
/// </para>
/// <para>
/// The tracker is a self-settling state machine, not a polling timer. It caches
/// the latest activity inputs and, on each input change (and at
/// <see cref="Snapshot"/>), attributes the wall-clock slice since the last
/// settle to the bucket that was active across it. The one time-dependent edge
/// is "moving": a room change opens a short <see cref="MovementWindow"/> during
/// which the player counts as travelling; when it lapses with no further input
/// the slice splits, the head landing in <c>Moving</c> and the tail in whatever
/// resting / waiting state currently holds — so a long idle after a hop doesn't
/// over-count movement, with no periodic tick required.
/// </para>
/// <para>
/// Inputs are pushed in via the <c>Note*</c> methods rather than subscribed
/// here, since they span three sources (<c>PlayerState</c>,
/// <c>ConditionTracker</c>, <c>RoomTracker</c>); <c>AppServices</c> wires those
/// events to the forwarders. Every <c>Note*</c> call and <see cref="Snapshot"/>
/// runs on the marshalled dispatch thread (the sources all fire there), so the
/// counters are lock-free, matching <see cref="CombatSessionTracker"/>. The
/// injectable clock keeps the time arithmetic unit-testable.
/// </para>
/// <para>
/// The tracker is gated on being <i>in-game</i>: it stays disarmed (accruing
/// nothing) until <see cref="NoteInGame"/> — wired to the first in-game prompt
/// of the session — arms it, and <see cref="Suspend"/> re-disarms it on
/// disconnect / character switch. So BBS-menu and offline time never count, and
/// <c>TimeOn</c> equals the activity partition's sum rather than raw wall-clock.
/// </para>
/// </remarks>
public sealed class TimeAnalysisTracker
{
    /// <summary>How long after a room change the player still counts as
    /// travelling. Bounds the movement tail when the next input is far off.</summary>
    public static readonly TimeSpan MovementWindow = TimeSpan.FromSeconds(3);

    private enum Activity { Waiting, Moving, Attacking, RestingHp, RestingMa }

    /// <summary>The independent affliction overlays, in snapshot order. Each is a
    /// concurrent in/out timer — index into the <see cref="_afflictionOn"/> /
    /// <see cref="_afflictionSince"/> / <see cref="_afflictionTotal"/> arrays.</summary>
    private enum Affliction { Blinded, Poisoned, Diseased, Confused, Held }

    private const int AfflictionCount = 5;

    private readonly Func<DateTimeOffset> _clock;

    private DateTimeOffset _lastSettle;

    // Gate: time accrues only while in-game. Disarmed until the first in-game
    // prompt of the session (NoteInGame) and re-disarmed on disconnect /
    // character switch (Suspend), so BBS-menu and offline spans never count.
    private bool _started;

    // Latest activity inputs (cached so a settle can classify the slice that
    // just ended before the new value takes effect for the next slice).
    private bool _inCombat;
    private PlayerPosition _position;
    private int _hp, _maxHp, _ma, _maxMa;
    private DateTimeOffset? _lastRoomChangeAt;

    // Activity partition (sums to TimeOn).
    private TimeSpan _waiting, _moving, _attacking, _restingHp, _restingMa;

    // Affliction overlays — each its own little in/out timer, indexed by
    // Affliction. Parallel arrays keep the five overlays uniform: _afflictionOn
    // is the live state, _afflictionSince the instant it last switched on, and
    // _afflictionTotal the accumulated time while on.
    private readonly bool[] _afflictionOn = new bool[AfflictionCount];
    private readonly DateTimeOffset[] _afflictionSince = new DateTimeOffset[AfflictionCount];
    private readonly TimeSpan[] _afflictionTotal = new TimeSpan[AfflictionCount];

    /// <summary>Raised after any input updates the tallies, so the Time Analysis
    /// VM can refresh. Fires on the dispatch thread.</summary>
    public event Action? Changed;

    public TimeAnalysisTracker(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (static () => DateTimeOffset.Now);
        DateTimeOffset now = _clock();
        _lastSettle = now;
        _position = PlayerPosition.Standing;
        for (int i = 0; i < AfflictionCount; i++) _afflictionSince[i] = now;
    }

    /// <summary>Push the player's combat / position / vitals (all carried on the
    /// same <c>PlayerState</c> source). Pool levels distinguish resting for HP
    /// from resting for mana.</summary>
    public void NotePlayerState(
        bool inCombat, PlayerPosition position, int hp, int maxHp, int ma, int maxMa)
    {
        Settle(_clock());
        _inCombat = inCombat;
        _position = position;
        _hp = hp; _maxHp = maxHp; _ma = ma; _maxMa = maxMa;
        Changed?.Invoke();
    }

    /// <summary>Push the local player's affliction state (from
    /// <c>ConditionTracker</c>). Each flag is an overlay timer, independent of
    /// the activity partition and of one another. <paramref name="held"/> is the
    /// movement-prevented condition.</summary>
    public void NoteAfflictions(
        bool blinded, bool poisoned, bool diseased, bool confused, bool held)
    {
        DateTimeOffset now = _clock();
        Settle(now);
        SetAffliction(Affliction.Blinded, blinded, now);
        SetAffliction(Affliction.Poisoned, poisoned, now);
        SetAffliction(Affliction.Diseased, diseased, now);
        SetAffliction(Affliction.Confused, confused, now);
        SetAffliction(Affliction.Held, held, now);
        Changed?.Invoke();
    }

    // Flip one overlay's live state; a fresh on-edge stamps the start instant so
    // Settle accrues from here. (Settle has already drained the time up to now
    // for any overlay that was already on, so no time is lost on a toggle.)
    private void SetAffliction(Affliction a, bool on, DateTimeOffset now)
    {
        int i = (int)a;
        if (on && !_afflictionOn[i]) _afflictionSince[i] = now;
        _afflictionOn[i] = on;
    }

    /// <summary>Mark that the player just moved to a new room — opens the
    /// <see cref="MovementWindow"/> during which they count as travelling.</summary>
    public void NoteRoomChanged()
    {
        DateTimeOffset now = _clock();
        Settle(now);
        _lastRoomChangeAt = now;
        Changed?.Invoke();
    }

    /// <summary>Arm the tracker: the player reached the in-game prompt, so begin
    /// attributing wall-clock time. Idempotent within a session — only the first
    /// call after construction / <see cref="Suspend"/> takes effect, so it's safe
    /// to wire to every prompt observation. Re-anchors the settle cursor to now,
    /// excluding the preceding BBS-menu / offline span.</summary>
    public void NoteInGame()
    {
        if (_started) return;
        DateTimeOffset now = _clock();
        _started = true;
        _lastSettle = now;
        for (int i = 0; i < AfflictionCount; i++) _afflictionSince[i] = now;
        Changed?.Invoke();
    }

    /// <summary>Disarm the tracker, freezing the tallies where they stand —
    /// called on disconnect and at the character-switch boundary. Accrued totals
    /// survive (a play session spans reconnects); accrual simply pauses until the
    /// next in-game prompt re-arms it via <see cref="NoteInGame"/>.</summary>
    public void Suspend()
    {
        Settle(_clock());   // capture time up to the pause point (no-op if already disarmed)
        _started = false;
        Changed?.Invoke();
    }

    /// <summary>Point-in-time copy of the session's time breakdown, settled to
    /// the current instant so the in-progress slice is included.</summary>
    public TimeAnalysisStats Snapshot()
    {
        DateTimeOffset now = _clock();
        Settle(now);
        return new TimeAnalysisStats(
            // In-game wall-clock is the activity partition's sum: time accrues
            // only while armed, so the partition already excludes BBS-menu and
            // offline spans. Deriving TimeOn from it keeps the buckets summing to
            // it by construction.
            TimeOn:    _waiting + _moving + _attacking + _restingHp + _restingMa,
            Waiting:   _waiting,
            Moving:    _moving,
            Attacking: _attacking,
            RestingHp: _restingHp,
            RestingMa: _restingMa,
            Blinded:   _afflictionTotal[(int)Affliction.Blinded],
            Poisoned:  _afflictionTotal[(int)Affliction.Poisoned],
            Diseased:  _afflictionTotal[(int)Affliction.Diseased],
            Confused:  _afflictionTotal[(int)Affliction.Confused],
            Held:      _afflictionTotal[(int)Affliction.Held]);
    }

    /// <summary>Zero every counter and re-anchor the settle cursor — the
    /// <c>@reset</c> / "Reset session" wipe, matching the other Phase 11
    /// trackers. Leaves the in-game arm state untouched, so a reset taken
    /// mid-session keeps counting from now; <see cref="Suspend"/> is the path
    /// that re-disarms for a fresh character / connection.</summary>
    public void Reset()
    {
        DateTimeOffset now = _clock();
        _lastSettle = now;
        _waiting = _moving = _attacking = _restingHp = _restingMa = TimeSpan.Zero;
        _inCombat = false;
        _position = PlayerPosition.Standing;
        _hp = _maxHp = _ma = _maxMa = 0;
        _lastRoomChangeAt = null;
        for (int i = 0; i < AfflictionCount; i++)
        {
            _afflictionOn[i] = false;
            _afflictionSince[i] = now;
            _afflictionTotal[i] = TimeSpan.Zero;
        }
        Changed?.Invoke();
    }

    // Attribute the wall-clock slice [_lastSettle, now] to the activity bucket(s)
    // that held across it, and advance the affliction overlays. Splits at the
    // movement-window boundary — the only edge that changes the classification
    // by the passage of time alone.
    private void Settle(DateTimeOffset now)
    {
        if (!_started) return;   // accrue only while in-game (see NoteInGame)
        if (now <= _lastSettle) return;

        DateTimeOffset cursor = _lastSettle;
        while (cursor < now)
        {
            Activity a = Classify(cursor);
            DateTimeOffset sliceEnd = now;
            if (a == Activity.Moving && _lastRoomChangeAt is { } moved)
            {
                DateTimeOffset windowEnd = moved + MovementWindow;
                if (windowEnd < sliceEnd) sliceEnd = windowEnd;
            }
            Add(a, sliceEnd - cursor);
            cursor = sliceEnd;
        }
        _lastSettle = now;

        for (int i = 0; i < AfflictionCount; i++)
        {
            if (!_afflictionOn[i]) continue;
            _afflictionTotal[i] += now - _afflictionSince[i];
            _afflictionSince[i] = now;
        }
    }

    // The active activity bucket at instant <paramref name="at"/>, by priority:
    // combat > movement (within the window) > resting > waiting. Afflictions are
    // overlays and deliberately absent here.
    private Activity Classify(DateTimeOffset at)
    {
        if (_inCombat) return Activity.Attacking;
        if (_lastRoomChangeAt is { } moved && at - moved < MovementWindow)
            return Activity.Moving;
        // Meditating regains mana; plain resting regains HP first, then mana
        // once HP is topped — inferred from the observed pool gaps.
        if (_position == PlayerPosition.Meditating) return Activity.RestingMa;
        if (_position == PlayerPosition.Resting)
            return _hp < _maxHp ? Activity.RestingHp
                 : _ma < _maxMa ? Activity.RestingMa
                 : Activity.RestingHp;
        return Activity.Waiting;
    }

    private void Add(Activity a, TimeSpan d)
    {
        switch (a)
        {
            case Activity.Waiting:   _waiting   += d; break;
            case Activity.Moving:    _moving    += d; break;
            case Activity.Attacking: _attacking += d; break;
            case Activity.RestingHp: _restingHp += d; break;
            case Activity.RestingMa: _restingMa += d; break;
        }
    }
}
