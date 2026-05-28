using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

/// <summary>
/// The combat-cycle heartbeat. Every automation engine (HealthManager,
/// CastingDirector, CombatManager) subscribes to <see cref="CombatTickElapsed"/>
/// or one of the regen-tick events; the status bar binds to the
/// observable last-tick timestamps for the countdown display.
/// </summary>
/// <remarks>
/// <para>
/// Combat tick is fixed at 5 s — invariant across MajorMUD realm flavours.
/// Two sources drive the event:
/// </para>
/// <list type="bullet">
///   <item><description>
///   <b>Damage-driven</b>: server damage lines (UserHits + MobHits) are
///   the canonical "a tick just elapsed" signal. On match, the tick fires
///   immediately and <see cref="LastCombatTick"/> is stamped at now.
///   </description></item>
///   <item><description>
///   <b>Timer fallback</b>: a 100 ms <see cref="DispatcherTimer"/> checks
///   whether 5 s has elapsed since the last stamped tick and fires
///   otherwise. This keeps the heartbeat going when the user is idle / out
///   of combat / between hits.
///   </description></item>
/// </list>
/// <para>
/// HP and MA regen ticks use the same timer-fallback only — server damage
/// lines don't correlate to regen. Intervals are realm-specific and the
/// spec is explicit: don't assume a realm. <see cref="HpRegenInterval"/>
/// and <see cref="ManaRegenInterval"/> default to <see cref="TimeSpan.Zero"/>
/// (disabled — the corresponding events don't fire). Phase 4
/// Settings.Health will surface the knobs and Phase 12 Settings.RealmType
/// will populate presets.
/// </para>
/// </remarks>
public sealed partial class TickEngine : ObservableObject, IDisposable
{
    /// <summary>Combat tick interval — universal across MajorMUD realm flavours.</summary>
    public static readonly TimeSpan CombatTickInterval = TimeSpan.FromSeconds(5);

    private readonly DispatcherTimer _timer;
    private readonly List<IDisposable> _patternSubs = new();
    private bool _disposed;

    /// <summary>HP regen interval. <see cref="TimeSpan.Zero"/> disables the regen event.</summary>
    public TimeSpan HpRegenInterval { get; set; } = TimeSpan.Zero;

    /// <summary>MA / KAI regen interval. <see cref="TimeSpan.Zero"/> disables the regen event.</summary>
    public TimeSpan ManaRegenInterval { get; set; } = TimeSpan.Zero;

    /// <summary>Wall-clock time of the last combat tick, or <c>null</c> before the first fire.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeToNextCombatTick))]
    private DateTimeOffset? _lastCombatTick;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeToNextHpRegenTick))]
    private DateTimeOffset? _lastHpRegenTick;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeToNextManaRegenTick))]
    private DateTimeOffset? _lastManaRegenTick;

    /// <summary>Time remaining to the next combat tick, or <c>null</c> if no tick has been observed yet.</summary>
    public TimeSpan? TimeToNextCombatTick => RemainingFor(LastCombatTick, CombatTickInterval);

    public TimeSpan? TimeToNextHpRegenTick =>
        HpRegenInterval == TimeSpan.Zero ? null : RemainingFor(LastHpRegenTick, HpRegenInterval);

    public TimeSpan? TimeToNextManaRegenTick =>
        ManaRegenInterval == TimeSpan.Zero ? null : RemainingFor(LastManaRegenTick, ManaRegenInterval);

    /// <summary>Fired on every combat tick — every 5 s, refreshed by damage lines.</summary>
    public event Action? CombatTickElapsed;

    /// <summary>Fired at <see cref="HpRegenInterval"/> when configured.</summary>
    public event Action? HpRegenTickElapsed;

    /// <summary>Fired at <see cref="ManaRegenInterval"/> when configured.</summary>
    public event Action? ManaRegenTickElapsed;

    public TickEngine(MessageRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);

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

    /// <summary>
    /// Damage-line callback. Stamps <see cref="LastCombatTick"/> at now
    /// and fires <see cref="CombatTickElapsed"/> the first time per
    /// debounce window. Subsequent damage hits for the same physical
    /// line (Megamind's UserHits regex is broad enough to also match
    /// mob-on-player hits, so both pattern subs fire on a single line)
    /// refresh the timestamp without firing again.
    /// </summary>
    private void RecordCombatTick()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        bool fresh = LastCombatTick is null
            || now - LastCombatTick.Value >= TimeSpan.FromMilliseconds(250);
        LastCombatTick = now;
        if (fresh) CombatTickElapsed?.Invoke();
    }

    private void OnTimerTick()
    {
        DateTimeOffset now = DateTimeOffset.Now;

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

        // Refresh the countdown properties so the status bar updates each
        // tick of the dispatcher timer.
        OnPropertyChanged(nameof(TimeToNextCombatTick));
        OnPropertyChanged(nameof(TimeToNextHpRegenTick));
        OnPropertyChanged(nameof(TimeToNextManaRegenTick));
    }

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
