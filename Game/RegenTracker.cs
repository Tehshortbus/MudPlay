using System.ComponentModel;

namespace MudPlay.Game;

// Watches PlayerState and runs four regen cycles on the cadence the realm's wire
// shows (RealmRegenProfile):
//
//   HpNatural — the standing HP gain. Anchored on the first one seen.
//   MpNatural — the standing mana gain, likewise.
//   HpRest    — the resting HP gain, while resting.
//   MpMedi    — the meditating mana gain, while meditating.
//
// The game pays its passive regen on a combat-round boundary (GAME_MECHANICS "The
// engine clock — one fast tick drives every timer"), so the cycles share one grid
// with the round: a round seen on the wire slides every running cycle onto it
// (NoteRound). On Stock HP and mana come on the same pass, so each gain is the
// other's moment too. On Paradigm the standing HP gain comes two rounds after the
// character's last HP gain, rest gains included, so a rest can leave it a round
// off the mana pass: there a mana gain places the HP cycle only while no HP gain
// is being seen (HP at max), and standing up re-anchors it on the last rest gain.
//
// A rest or meditate tick is counted from the command on Stock, in whole one-second
// game ticks, so those cycles anchor on the game tick the posture began in: the
// first gain comes 20 to 21 s (rest) or 14 to 15 s (meditate) after the command.
// A gain near such a tick is that cycle's and re-anchors it; it is never taken for
// the 30 s pass. On Paradigm a rest gain rides the round grid
// whenever the character lay down, so the rest cycle anchors on the last grid point;
// and a meditate gain rides a 15 s grid the mana pass sits on, so the meditate cycle
// anchors on the mana cycle.
public sealed class RegenTracker : IDisposable
{
    private readonly PlayerState _state;
    private readonly Func<DateTimeOffset> _clock;

    private DateTimeOffset? _lastArtifactAt;
    private int _lastHp;
    private int _lastMa;
    private DateTimeOffset? _lastHpTickAt;
    private DateTimeOffset? _lastMaTickAt;
    private bool _hpBaselineSet;
    private bool _maBaselineSet;
    private bool _disposed;

    private RealmRegenProfile _cadence = RealmRegenProfile.Stock;
    // What passive regen can pay this character per gain, when known.
    private Func<HpRegenExpectation?>? _hpExpectation;
    // The last HP gain that was a regen amount. While those keep coming, a gain of
    // another size is another source; when they stop, the expected amounts are off.
    private DateTimeOffset? _lastRegenSizedHpAt;
    // When the standing HP cycle last took a gain as its tick, and how many it has
    // taken in a row, each on the tick the one before pointed at.
    private DateTimeOffset? _lastHpNaturalTickAt;
    private int _hpNaturalTicksInARow;
    // Where the rest and meditate cycles were anchored when the posture began.
    private DateTimeOffset? _restStartedAt;
    private DateTimeOffset? _mediStartedAt;
    // The last combat round seen on the wire, for anchoring a grid-riding rest.
    private DateTimeOffset? _lastRoundAt;
    private static readonly TimeSpan RoundStep = TickEngine.CombatTickInterval;
    // How far a coasting anchor may sit from a seen round and still be slid onto it.
    // The game's tick runs a shade over a second and makes up a whole second in one
    // jump every couple of minutes, so a second and a bit covers an honest drift.
    private static readonly TimeSpan GridTolerance = TimeSpan.FromMilliseconds(1250);
    // The last moment known to be on the round grid: a round seen, or a regen gain
    // taken as one. A gain is judged against it only while it is this fresh.
    private DateTimeOffset? _gridReference;
    private static readonly TimeSpan GridReferenceTrusted = TimeSpan.FromSeconds(60);
    // How far off the grid a gain may be and still be regen.
    private static readonly TimeSpan OnGridWithin = TimeSpan.FromMilliseconds(750);
    // How far a gain may fall from a tick counted from a command and still be it:
    // the count runs in whole game ticks, so a count of plain seconds from the
    // posture change can be most of a second out until the first gain is seen.
    private static readonly TimeSpan CommandTickWithin = TimeSpan.FromMilliseconds(1250);
    // Two cycles this close to their ticks at one gain are paid together in it.
    private static readonly TimeSpan SameGainWithin = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan GameTick = TimeSpan.FromSeconds(1);
    // A round or pass this recent still says where the one-second game ticks fall.
    private static readonly TimeSpan GameTickPhaseTrusted = TimeSpan.FromSeconds(45);
    // The last gain turned away as off the grid. A second one a whole number of
    // rounds after it is the grid itself having moved (the game making up a second).
    private DateTimeOffset? _lastOffGridGain;
    private static readonly TimeSpan SameGridWithin = TimeSpan.FromMilliseconds(300);

    public RegenCycle HpNatural { get; } = new("HP natural", RegenConstants.SeedStandingInterval);
    public RegenCycle HpRest    { get; } = new("HP rest",    RegenConstants.SeedRestingInterval);
    public RegenCycle MpNatural { get; } = new("MP natural", RegenConstants.SeedStandingInterval);
    public RegenCycle MpMedi    { get; } = new("MP medi",    RegenConstants.SeedMeditatingInterval);

    // Fired after an observed HP uptick that passes the artifact filter.
    public event Action<RegenSample>? HpTickObserved;

    // Fired for an HP gain left out for its size, with the reason.
    public event Action<int, string>? HpGainLeftOut;

    // Fired after an observed MA uptick that passes the artifact filter.
    public event Action<RegenSample>? MaTickObserved;

    public RegenTracker(PlayerState state, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _state.PropertyChanged += OnPlayerStateChanged;
    }

    // Time-to-next HP natural tick, or null before first observation.
    public TimeSpan? GetTimeToNextHpNaturalTick() => HpNatural.GetTimeToNext(_clock());

    // Time-to-next HP rest tick, or null when not resting.
    public TimeSpan? GetTimeToNextHpRestTick() => HpRest.GetTimeToNext(_clock());

    // Time-to-next MP natural tick, or null before first observation.
    public TimeSpan? GetTimeToNextMpNaturalTick() => MpNatural.GetTimeToNext(_clock());

    // Time-to-next MP meditate tick, or null when not meditating.
    public TimeSpan? GetTimeToNextMpMediTick() => MpMedi.GetTimeToNext(_clock());

    // Re-seed every cycle's tick cadence for the given realm family (see
    // RealmRegenProfile). Called once at wire-up and again on every
    // GameDataCache.ActiveSetChanged. Idempotent — re-applying the same realm
    // just re-asserts the same intervals.
    public void SetRealm(RealmType realm)
    {
        _cadence = RealmRegenProfile.For(realm);
        HpNatural.Reseed(_cadence.StandingInterval);
        MpNatural.Reseed(_cadence.ManaInterval);
        HpRest.Reseed(_cadence.RestingInterval);
        MpMedi.Reseed(_cadence.MeditatingInterval);
    }

    // Give the tracker the HP amounts regen can pay (HpRegenExpectationSource). With
    // none, every gain is judged on timing alone.
    public void SetHpExpectation(Func<HpRegenExpectation?> source) => _hpExpectation = source;

    // Why an HP gain of this size isn't regen, or null when it can be: more than
    // regen ever pays (a heal), or an amount regen doesn't pay while the amounts it
    // does pay are still arriving (a heal over time, a drain). A gain that fills HP
    // to its max can be any size up to the largest. When no regen-sized gain has
    // come for three standing intervals the expected amounts are taken to be off (a
    // stat not read yet, a buff's percent) and other sizes count again.
    public string? WhyHpGainIsNotRegen(int gain, int hpAfter, DateTimeOffset now)
    {
        if (_hpExpectation?.Invoke() is not { } expected) return null;
        if (gain > expected.Largest) return $"more than regen pays (at most +{expected.Largest})";
        bool filledToMax = _state.MaxHp > 0 && hpAfter >= _state.MaxHp;
        if (filledToMax || expected.Matches(gain)) return null;
        bool regenStillArriving = _lastRegenSizedHpAt is { } last
            && now - last <= HpNatural.Interval + HpNatural.Interval + HpNatural.Interval + TimeSpan.FromSeconds(5);
        return regenStillArriving ? $"not an amount regen pays ({expected})" : null;
    }

    // A combat round was seen on the wire at `at`. Every cycle that rides the round
    // grid is slid onto it.
    public void NoteRound(DateTimeOffset at)
    {
        _lastRoundAt = at;
        _gridReference = at;
        _lastOffGridGain = null;
        HpNatural.AlignTo(at, RoundStep, GridTolerance);
        MpNatural.AlignTo(at, RoundStep, GridTolerance);
        if (_cadence.RestingOnRoundGrid) HpRest.AlignTo(at, RoundStep, GridTolerance);
        if (_cadence.MeditatingOnManaGrid) MpMedi.AlignTo(at, RoundStep, GridTolerance);
    }

    // Whether an HP gain seen in this posture sits on the round grid: a standing gain
    // does on both realms, a resting one only where rest rides the grid. Meditating
    // is left out: no capture has shown its HP gains cleanly.
    public bool HpGainIsOnRoundGrid(PlayerPosition position) => position switch
    {
        PlayerPosition.Standing => true,
        PlayerPosition.Resting => _cadence.RestingOnRoundGrid,
        _ => false,
    };

    // Mark the moment as an artifact (heal / drink / etc.) so subsequent
    // up-deltas drop.
    public void RecordArtifact() => _lastArtifactAt = _clock();

    private void OnPlayerStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerState.Hp):       ConsiderHp();        break;
            case nameof(PlayerState.Ma):       ConsiderMa();        break;
            case nameof(PlayerState.Position): ApplyPositionChange(); break;
        }
    }

    private void ConsiderHp()
    {
        int current = _state.Hp;
        DateTimeOffset now = _clock();

        if (!_hpBaselineSet)
        {
            _lastHp = current;
            _hpBaselineSet = true;
            return;
        }

        int delta = current - _lastHp;
        _lastHp = current;

        if (delta <= 0) return;                  // damage / no change.
        if (IsInArtifactWindow(now)) return;     // heal-shaped event recently.
        if (WhyHpGainIsNotRegen(delta, current, now) is { } notRegen)
        {
            HpGainLeftOut?.Invoke(delta, notRegen);
            return;
        }
        if (_hpExpectation?.Invoke() is { } sizes && sizes.Matches(delta)) _lastRegenSizedHpAt = now;
        if (HpGainIsOnRoundGrid(_state.Position) && !OnTheRoundGrid(now)) return;

        // Credit whichever active HP cycle is closer to its boundary. A gain
        // that lands on both cycles' ticks advances both.
        // Where rest rides the round grid it takes the standing gain's place and
        // comes twice as often: the rest cycle owns the gain, and the standing cycle
        // is placed again on standing up (ApplyPositionChange).
        bool restOwnsTheGain = HpRest.IsActive && _cadence.RestingOnRoundGrid;
        bool restClaimed, natClaimed;
        if (restOwnsTheGain || !HpRest.IsActive)
        {
            restClaimed = ClaimIfDue(HpRest, now);
            natClaimed = !restOwnsTheGain && ClaimIfOnATick(HpNatural, now);
        }
        else
        {
            (restClaimed, natClaimed) = CreditCommandCountedGain(HpRest, _restStartedAt, HpNatural, now);
        }
        if (natClaimed) _hpNaturalTicksInARow = HpNaturalAnchorIsFresh(now) ? _hpNaturalTicksInARow + 1 : 1;
        if (!restClaimed && !natClaimed)
        {
            // No active cycle was due. A cycle that has taken two gains in a row on
            // its own ticks isn't moved by a gain that comes between them: a buff that
            // heals every spell round (GAME_MECHANICS "Heal-over-time buffs pay on the
            // spell round") lands on a combat round every 15 s and can be the size of
            // a regen gain. If the cycle is out of step after all, it stops taking
            // gains, goes stale within two intervals and anchors afresh.
            if (!restOwnsTheGain && _hpNaturalTicksInARow >= 2 && HpNaturalAnchorIsFresh(now))
            {
                HpGainLeftOut?.Invoke(delta, "between two ticks of the HP cycle");
                return;
            }
            // Otherwise anchor on this observation: the first gain, one that shows a
            // lone earlier gain was misplaced, or the first after a silence.
            if (restOwnsTheGain) HpRest.Start(now);
            else
            {
                HpNatural.Start(now);
                natClaimed = true;
                _hpNaturalTicksInARow = 1;
            }
        }
        if (natClaimed) _lastHpNaturalTickAt = now;
        // Where HP and mana come on the same pass (Stock), an HP gain is the mana
        // gain's moment too: keep the mana countdown live while mana sits at max.
        // On Paradigm HP comes three times per mana pass, so it says nothing of
        // which one carries the mana.
        if (natClaimed && HpNatural.Interval == MpNatural.Interval) MpNatural.Start(now);

        TimeSpan sinceLast = _lastHpTickAt is { } prevTick ? now - prevTick : TimeSpan.Zero;
        _lastHpTickAt = now;
        HpTickObserved?.Invoke(new RegenSample(now, delta, sinceLast, _state.Position));
    }

    private void ConsiderMa()
    {
        int current = _state.Ma;
        DateTimeOffset now = _clock();

        if (!_maBaselineSet)
        {
            _lastMa = current;
            _maBaselineSet = true;
            return;
        }

        int delta = current - _lastMa;
        _lastMa = current;

        if (delta <= 0) return;
        if (IsInArtifactWindow(now)) return;
        if (_state.Position != PlayerPosition.Meditating && !OnTheRoundGrid(now)) return;

        bool mediClaimed, natClaimed;
        if (_cadence.MeditatingOnManaGrid || !MpMedi.IsActive)
        {
            mediClaimed = ClaimIfDue(MpMedi, now);
            natClaimed = ClaimIfDue(MpNatural, now);
        }
        else
        {
            (mediClaimed, natClaimed) = CreditCommandCountedGain(MpMedi, _mediStartedAt, MpNatural, now);
        }
        if (!mediClaimed && !natClaimed)
        {
            // Where meditate rides the mana grid, a gain while meditating is a point
            // on the meditate grid whatever the countdown said, and only every other
            // one is the mana pass: it re-anchors the meditate cycle and leaves a
            // running mana cycle alone.
            bool onMediGrid = _cadence.MeditatingOnManaGrid && MpMedi.IsActive;
            if (onMediGrid) MpMedi.Start(now);
            if (!onMediGrid || !MpNatural.IsActive)
            {
                MpNatural.Start(now);
                natClaimed = true;
            }
        }
        // Lets a max-HP character still see a live HP countdown driven by observed
        // mana gains. Where HP runs on its own count (Paradigm), a cycle that HP
        // gains are placing is left alone: the pass can sit a round off it.
        if (natClaimed && (HpNatural.Interval == MpNatural.Interval || !HpGainsAreRecent(now)))
            HpNatural.Start(now);

        TimeSpan sinceLast = _lastMaTickAt is { } prevTick ? now - prevTick : TimeSpan.Zero;
        _lastMaTickAt = now;
        MaTickObserved?.Invoke(new RegenSample(now, delta, sinceLast, _state.Position));
    }

    // A gain while a command-counted cycle (Stock's rest or meditate) is running,
    // alongside the 30 s pass that keeps paying: credit the cycle whose tick the gain
    // is nearest, both when one gain carries both. A gain near the command-counted
    // tick is never left to fall through to the pass, which would move the pass's
    // countdown onto it.
    private static (bool Bonus, bool Pass) CreditCommandCountedGain(
        RegenCycle bonus, DateTimeOffset? bonusStartedAt, RegenCycle pass, DateTimeOffset now)
    {
        // Until a gain or a countdown rollover has moved it, the bonus cycle's anchor
        // is the command, where no tick falls.
        TimeSpan? bonusOff = OffNearestTick(bonus, now, anchorIsATick: bonus.Anchor != bonusStartedAt);
        TimeSpan? passOff = OffNearestTick(pass, now, anchorIsATick: true);
        bool bonusNear = bonusOff is { } b && b <= CommandTickWithin;
        bool passNear = passOff is { } p && p <= TimeSpan.FromMilliseconds(750);
        if (bonusNear && passNear)
        {
            // Both in reach: the nearer one, or both when the gain sits on both.
            if (bonusOff!.Value > passOff!.Value + SameGainWithin) bonusNear = false;
            else if (passOff.Value > bonusOff.Value + SameGainWithin) passNear = false;
        }
        if (bonusNear) bonus.Start(now);
        if (passNear) pass.Start(now);
        return (bonusNear, passNear);
    }

    // How far now is from the cycle's nearest tick: the anchor itself counts as one
    // only when it is a tick.
    private static TimeSpan? OffNearestTick(RegenCycle cycle, DateTimeOffset now, bool anchorIsATick)
    {
        if (cycle.Anchor is not { } anchor) return null;
        double interval = cycle.Interval.TotalMilliseconds;
        double elapsed = (now - anchor).TotalMilliseconds;
        double ticks = Math.Max(anchorIsATick ? 0 : 1, Math.Round(elapsed / interval));
        return TimeSpan.FromMilliseconds(Math.Abs(elapsed - ticks * interval));
    }

    // The standing HP cycle took a gain as its tick within the last two intervals
    // (and a little), so its anchor is a tick seen and not a projection grown stale.
    private bool HpNaturalAnchorIsFresh(DateTimeOffset now) =>
        HpNatural.IsActive && _lastHpNaturalTickAt is { } last
        && now - last <= HpNatural.Interval + HpNatural.Interval + TimeSpan.FromSeconds(2);

    // An HP gain was seen within the last two standing intervals, so the HP cycle
    // is being placed by its own gains.
    private bool HpGainsAreRecent(DateTimeOffset now) =>
        _lastHpTickAt is { } last && now - last <= HpNatural.Interval + HpNatural.Interval;

    // The gain falls on one of the cycle's ticks, a little early or late: record it
    // there. Judged from both sides, since the countdown rolls the anchor forward as
    // each tick passes and a gain a moment late would otherwise read as a whole
    // interval early. The reach covers the game's catch-up second.
    private static bool ClaimIfOnATick(RegenCycle cycle, DateTimeOffset now)
    {
        if (OffNearestTick(cycle, now, anchorIsATick: true) is not { } off || off > CommandTickWithin) return false;
        cycle.Start(now);
        return true;
    }

    // If cycle is active and the now-instant is at or past its next-tick
    // boundary (with a small grace), record the observation and return true.
    private static bool ClaimIfDue(RegenCycle cycle, DateTimeOffset now)
    {
        if (cycle.Anchor is not { } anchor) return false;
        TimeSpan elapsed = now - anchor;
        TimeSpan grace = TimeSpan.FromMilliseconds(750);
        if (elapsed + grace < cycle.Interval) return false;
        cycle.Start(now);
        return true;
    }

    // Start / stop the rest + medi bonus cycles on position transitions.
    // Leaving the position before the cycle's interval elapses cancels the
    // pending tick outright (no partial credit) — the server only fires the
    // rest / medi tick if the player stayed in the position for the full
    // interval. Re-entering re-anchors from the new transition.
    private void ApplyPositionChange()
    {
        DateTimeOffset now = _clock();
        if (_state.Position == PlayerPosition.Resting && !HpRest.IsActive)
        {
            HpRest.Start(_cadence.RestingOnRoundGrid ? LastGridPoint(now) : LastGameTick(now));
            _restStartedAt = HpRest.Anchor;
        }
        else if (_state.Position != PlayerPosition.Resting && HpRest.IsActive)
        {
            // Where rest rides the grid the next standing gain comes a standing
            // interval after the last rest gain, not where the cycle sat before the
            // rest: an odd number of rest gains moves it a round.
            if (_cadence.RestingOnRoundGrid)
            {
                HpRest.GetTimeToNext(now);
                if (HpRest.Anchor is { } lastRestGain)
                {
                    HpNatural.Start(lastRestGain);
                    _lastHpNaturalTickAt = lastRestGain;
                    _hpNaturalTicksInARow = 2;
                }
            }
            HpRest.Stop();
        }

        if (_state.Position == PlayerPosition.Meditating && !MpMedi.IsActive)
        {
            MpMedi.Start(_cadence.MeditatingOnManaGrid ? LastMeditateGridPoint(now) : LastGameTick(now));
            _mediStartedAt = MpMedi.Anchor;
        }
        else if (_state.Position != PlayerPosition.Meditating && MpMedi.IsActive)
        {
            MpMedi.Stop();
        }
    }

    // Regen is paid on a combat round, so a gain that falls between rounds is a heal
    // the artifact window didn't know of — the engine's own cast, a card dealt from a
    // deck, a heal over time — and is left out of the cycles. Judged only while the
    // grid reference is fresh; with none, every gain counts as before. A gain taken
    // as regen becomes the reference in turn.
    private bool OnTheRoundGrid(DateTimeOffset now)
    {
        if (_gridReference is not { } reference || now - reference > GridReferenceTrusted)
        {
            _gridReference = now;
            _lastOffGridGain = null;
            return true;
        }
        if (OffGrid(now - reference) <= OnGridWithin
            || (_lastOffGridGain is { } earlier && now - earlier >= RoundStep - SameGridWithin
                && OffGrid(now - earlier) <= SameGridWithin))
        {
            _gridReference = now;
            _lastOffGridGain = null;
            return true;
        }
        _lastOffGridGain = now;
        return false;
    }

    // How far a span is from a whole number of rounds.
    private static TimeSpan OffGrid(TimeSpan span)
    {
        long off = span.Ticks % RoundStep.Ticks;
        return TimeSpan.FromTicks(Math.Min(off, RoundStep.Ticks - off));
    }

    // The latest round-grid point at or before now, from the last round seen or,
    // failing that, the last standing gain (itself on the grid). now when neither
    // is known.
    private DateTimeOffset LastGridPoint(DateTimeOffset now)
    {
        DateTimeOffset? known = _lastRoundAt ?? HpNatural.Anchor ?? MpNatural.Anchor;
        if (known is not { } reference || reference > now) return now;
        long steps = (long)((now - reference).Ticks / RoundStep.Ticks);
        return reference + TimeSpan.FromTicks(steps * RoundStep.Ticks);
    }

    // The game tick a command landed in, for a cycle the game counts from the command
    // in whole ticks: the latest one-second point at or before now, from a round or a
    // pass seen lately (both fall on a game tick). With neither to go by, half a tick
    // back, which is the middle of where it can be.
    private DateTimeOffset LastGameTick(DateTimeOffset now)
    {
        DateTimeOffset? known = Latest(_lastRoundAt, Latest(HpNatural.Anchor, MpNatural.Anchor));
        if (known is not { } reference || reference > now || now - reference > GameTickPhaseTrusted)
            return now - TimeSpan.FromMilliseconds(500);
        long ticks = (now - reference).Ticks / GameTick.Ticks;
        return reference + TimeSpan.FromTicks(ticks * GameTick.Ticks);
    }

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;

    // The latest point at or before now on the meditate grid, which the mana pass
    // sits on: whole meditate intervals from the mana cycle's anchor. now before any
    // mana gain has been seen.
    private DateTimeOffset LastMeditateGridPoint(DateTimeOffset now)
    {
        if (MpNatural.Anchor is not { } pass || pass > now) return now;
        long steps = (now - pass).Ticks / MpMedi.Interval.Ticks;
        return pass + TimeSpan.FromTicks(steps * MpMedi.Interval.Ticks);
    }

    // True just after a heal-shaped event (RecordArtifact): a gain seen now isn't
    // credited to a regen cycle.
    public bool InArtifactWindow => IsInArtifactWindow(_clock());

    private bool IsInArtifactWindow(DateTimeOffset now)
        => _lastArtifactAt is { } at && now - at <= RegenConstants.ArtifactGraceWindow;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.PropertyChanged -= OnPlayerStateChanged;
    }
}

// One observed regen sample — payload of the tick-observed events.
// IntervalSinceLast is the wall-clock gap since the previous observed uptick
// of the same stream (HP or MA), or TimeSpan.Zero for the first sample. It
// carries the raw cadence a diagnostic can read a realm's real tick timing
// off of.
public readonly record struct RegenSample(
    DateTimeOffset Timestamp,
    int Delta,
    TimeSpan IntervalSinceLast,
    PlayerPosition Position);
