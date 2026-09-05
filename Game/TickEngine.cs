using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game;

// The combat-cycle heartbeat. Every automation engine (HealthManager,
// CastingDirector, CombatManager) subscribes to CombatTickElapsed or one of
// the regen-tick events; the status bar binds to the observable last-tick
// timestamps for the countdown display.
//
// Combat tick is fixed at 5 s — invariant across MajorMUD realm flavours.
// Two sources drive the event:
//   Damage-driven: server damage lines (UserHits + MobHits) are the canonical
//     "a tick just elapsed" signal. On match, the tick fires immediately and
//     LastCombatTick is stamped at now.
//   Timer fallback: a 100 ms DispatcherTimer checks whether 5 s has elapsed
//     since the last stamped tick and fires otherwise. This keeps the
//     heartbeat going when the user is idle / out of combat / between hits.
//
// HP and MA regen ticks use the same timer-fallback only — server damage
// lines don't correlate to regen. Intervals are realm-specific, so we don't
// assume a realm. HpRegenInterval and ManaRegenInterval default to
// TimeSpan.Zero (disabled — the corresponding events don't fire); the
// Settings.Health and Settings.RealmType surfaces populate them.
public sealed partial class TickEngine : ObservableObject, IDisposable
{
    // Combat tick interval — universal across MajorMUD realm flavours.
    public static readonly TimeSpan CombatTickInterval = TimeSpan.FromSeconds(5);

    // Coarse watchdog heartbeat. Unlike the combat / regen ticks this carries no
    // cycle semantics — it's a plain "another second passed" poll off the same
    // 100 ms timer, so a subscriber (the idle-stall watchdog on
    // CombatStateTracker) can re-check its state at ~1 s granularity instead of
    // waiting for the coarse 5 s combat tick. That's what lets a stuck-gate
    // recovery land within a second or two of its threshold rather than being
    // quantized up to the next combat tick.
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

    private readonly DispatcherTimer _timer;
    private readonly List<IDisposable> _patternSubs = new();
    private readonly Func<DateTimeOffset> _now;
    private DateTimeOffset? _lastHeartbeat;
    private bool _disposed;

    // HP regen interval. TimeSpan.Zero disables the regen event.
    public TimeSpan HpRegenInterval { get; set; } = TimeSpan.Zero;

    // MA / KAI regen interval. TimeSpan.Zero disables the regen event.
    public TimeSpan ManaRegenInterval { get; set; } = TimeSpan.Zero;

    // Wall-clock time of the last combat tick, or null before the first fire.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeToNextCombatTick))]
    private DateTimeOffset? _lastCombatTick;

    // Whether the CombatTickElapsed invocation in flight was driven by a server combat
    // line (RecordCombatTick) rather than the 5 s timer fallback. Set immediately before
    // each Invoke, so a synchronous subscriber reads the current tick's source. A
    // damage-line-driven tick fires DURING the round's line burst — before the round's
    // prompt refreshes HP — so a between-round decision on it runs on stale HP;
    // CastingDirector reads this to hold its non-heal casts until a fresh-HP pass.
    public bool LastCombatTickWasDamageDriven { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeToNextHpRegenTick))]
    private DateTimeOffset? _lastHpRegenTick;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeToNextManaRegenTick))]
    private DateTimeOffset? _lastManaRegenTick;

    // Time remaining to the next combat tick, or null if no tick has been
    // observed yet.
    public TimeSpan? TimeToNextCombatTick => RemainingFor(LastCombatTick, CombatTickInterval);

    public TimeSpan? TimeToNextHpRegenTick =>
        HpRegenInterval == TimeSpan.Zero ? null : RemainingFor(LastHpRegenTick, HpRegenInterval);

    public TimeSpan? TimeToNextManaRegenTick =>
        ManaRegenInterval == TimeSpan.Zero ? null : RemainingFor(LastManaRegenTick, ManaRegenInterval);

    // Fired on every combat tick — every 5 s, refreshed by damage lines.
    public event Action? CombatTickElapsed;

    // Fired every HeartbeatInterval (1 s) — a coarse watchdog poll with no cycle
    // semantics (see HeartbeatInterval).
    public event Action? HeartbeatElapsed;

    // Fired at HpRegenInterval when configured.
    public event Action? HpRegenTickElapsed;

    // Fired at ManaRegenInterval when configured.
    public event Action? ManaRegenTickElapsed;

    public TickEngine(MessageRouter router, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        _now = now ?? (() => DateTimeOffset.Now);

        // Damage-driven combat tick stamping. Any combat-round line —
        // hit, miss, or otherwise — anchors the cycle; the server beats
        // out one round every CombatTickInterval regardless of whether
        // the swing connected. 250 ms debounce in RecordCombatTick
        // collapses the duplicates a single round produces (UserHits's
        // broad regex matches mob-on-player lines too, plus we'll see
        // separate Hit and Miss lines in the same round if you're
        // fighting multiple mobs).
        _patternSubs.Add(router.Subscribe(KnownPatterns.UserHits,  _ => RecordCombatTick()));
        _patternSubs.Add(router.Subscribe(KnownPatterns.MobHits,   _ => RecordCombatTick()));
        _patternSubs.Add(router.Subscribe(KnownPatterns.MobMisses, _ => RecordCombatTick()));

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _timer.Tick += (_, _) => OnTimerTick();
        _timer.Start();
    }

    // Ensure the timer fallback has a combat-cycle anchor even when no generic
    // hit/miss pattern has matched yet. CombatManager uses this after a fresh
    // engage's attack spell is locally blocked by a cast that already spent the
    // round: no attack reached the server, so there is no *Combat Engaged* marker,
    // and some monsters' armour-block wording does not match UserHits / MobHits /
    // MobMisses. Without an anchor, the fallback loop below never starts because
    // LastCombatTick remains null, leaving the pending attack with no retry event.
    //
    // Do not move an existing anchor. Once a real combat line has established the
    // server's cadence, that observation remains more authoritative than the local
    // blocked-engage timestamp.
    public void EnsureCombatTickAnchor()
    {
        if (_disposed || LastCombatTick is not null) return;
        LastCombatTick = _now();
    }

    // Damage-line callback. Stamps LastCombatTick at now and fires
    // CombatTickElapsed the first time per debounce window. Subsequent damage
    // hits for the same physical line (the UserHits regex is broad enough to
    // also match mob-on-player hits, so both pattern subs fire on a single
    // line) refresh the timestamp without firing again.
    private void RecordCombatTick()
    {
        DateTimeOffset now = _now();
        bool fresh = LastCombatTick is null
            || now - LastCombatTick.Value >= TimeSpan.FromMilliseconds(250);
        LastCombatTick = now;
        if (fresh)
        {
            LastCombatTickWasDamageDriven = true;
            CombatTickElapsed?.Invoke();
        }
    }

    private void OnTimerTick()
    {
        DateTimeOffset now = _now();

        // Combat tick fallback. The server's cycle is "like clockwork"
        // — every 5 s from the observed anchor — so we project forward
        // in exact CombatTickInterval steps rather than re-anchoring at
        // `now`. Re-anchoring at `now` would drift the predicted ticks
        // ~100 ms later per cycle (the timer's own period), which after
        // an hour would be seconds off the real server-side cycle.
        // The while loop catches multi-cycle gaps (e.g. system sleep).
        while (LastCombatTick is { } combat && now - combat >= CombatTickInterval)
        {
            LastCombatTick = combat + CombatTickInterval;
            // Timer-fallback tick: no round burst is in flight, so the last prompt's HP
            // is current — mark this tick HP-fresh for the between-round decision.
            LastCombatTickWasDamageDriven = false;
            CombatTickElapsed?.Invoke();
        }

        // HP / MA regen — pure timer-driven. Each runs independently when
        // its interval is non-zero. First fire seeds the "last" timestamp.
        if (HpRegenInterval > TimeSpan.Zero)
        {
            if (LastHpRegenTick is not { } hp)
            {
                LastHpRegenTick = now;
            }
            else if (now - hp >= HpRegenInterval)
            {
                LastHpRegenTick = now;
                HpRegenTickElapsed?.Invoke();
            }
        }

        if (ManaRegenInterval > TimeSpan.Zero)
        {
            if (LastManaRegenTick is not { } ma)
            {
                LastManaRegenTick = now;
            }
            else if (now - ma >= ManaRegenInterval)
            {
                LastManaRegenTick = now;
                ManaRegenTickElapsed?.Invoke();
            }
        }

        // Watchdog heartbeat — a plain 1 s poll re-anchored at now (precision
        // doesn't matter for a stuck-state re-check, unlike the projected combat
        // cycle). First tick seeds the stamp without firing.
        if (_lastHeartbeat is not { } beat)
        {
            _lastHeartbeat = now;
        }
        else if (now - beat >= HeartbeatInterval)
        {
            _lastHeartbeat = now;
            HeartbeatElapsed?.Invoke();
        }

        // Refresh the countdown properties so the status bar updates each
        // tick of the dispatcher timer.
        OnPropertyChanged(nameof(TimeToNextCombatTick));
        OnPropertyChanged(nameof(TimeToNextHpRegenTick));
        OnPropertyChanged(nameof(TimeToNextManaRegenTick));
    }

    // Deterministic test seam for the DispatcherTimer callback. Production only
    // reaches OnTimerTick through the live 100ms timer above.
    internal void PollTimersForTests() => OnTimerTick();

    private static TimeSpan? RemainingFor(DateTimeOffset? last, TimeSpan interval)
    {
        if (last is not { } anchor) return null;
        TimeSpan rem = anchor + interval - DateTimeOffset.Now;
        return rem < TimeSpan.Zero ? TimeSpan.Zero : rem;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        foreach (IDisposable sub in _patternSubs) sub.Dispose();
        _patternSubs.Clear();
    }
}
