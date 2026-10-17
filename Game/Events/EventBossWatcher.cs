using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Events;

// Fires Boss-triggered events off the Bosses tab's timer table: when one of a boss's
// early spawn-window columns counts down to 0, its guaranteed spawn comes, a cleanup boss
// resets (each optionally BossLeadMinutes early), or it's killed. Once per kill —
// the kill time keys it — and only while in-game. A moment first seen more than
// StaleAfter late (the client wasn't running or connected then) doesn't fire.
public sealed class EventBossWatcher
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private readonly EventManager _events;
    private readonly BossStore _bosses;
    private readonly BossTimerStore _timers;
    private readonly GameDataCache _gameData;
    private readonly Func<RealmType> _realm;
    private readonly Func<bool> _inGame;
    private readonly LogService? _log;

    // (event, kill time) pairs already fired, so one kill fires an event once.
    private readonly HashSet<(ScheduledEvent Event, DateTimeOffset Killed)> _fired = new();

    private readonly Func<DateTimeOffset> _now;

    public EventBossWatcher(
        EventManager events, BossStore bosses, BossTimerStore timers, GameDataCache gameData,
        Func<RealmType> realm, Func<bool> inGame, LogService? log = null,
        Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(bosses);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(inGame);
        _events = events;
        _bosses = bosses;
        _timers = timers;
        _gameData = gameData;
        _realm = realm;
        _inGame = inGame;
        _log = log;
    }

    // The timed moments: called on the scheduler's clock tick.
    public void Evaluate()
    {
        if (!_inGame()) return;
        DateTimeOffset now = _now();
        foreach (ScheduledEvent e in BossEvents())
        {
            if (e.BossMoment == EventBossMoment.Killed) continue;
            if (FindBoss(e.BossName) is not { } def || _timers.KilledAt(def.Name) is not { } killed) continue;
            if (FireAt(e, def) is not { } at || now < at || now - at > StaleAfter) continue;
            if (!_fired.Add((e, killed))) continue;
            _log?.Info("Events", $"Boss event '{Label(e)}' fired: {def.Name} {e.BossMoment}.");
            _events.Fire(e);
        }
    }

    // A tracked boss died (BossTimerStore.BossKilled).
    public void OnBossKilled(BossDef def)
    {
        ArgumentNullException.ThrowIfNull(def);
        if (!_inGame()) return;
        foreach (ScheduledEvent e in BossEvents())
        {
            if (e.BossMoment != EventBossMoment.Killed) continue;
            if (!string.Equals(e.BossName, def.Name, StringComparison.OrdinalIgnoreCase)) continue;
            _log?.Info("Events", $"Boss event '{Label(e)}' fired: {def.Name} killed.");
            _events.Fire(e);
        }
    }

    // When a timed boss event is next due (local time), for Settings → Events' countdown;
    // null when the boss has no running timer, or for a Killed event.
    public DateTime? NextFire(ScheduledEvent e)
    {
        if (e.TriggerType != EventTriggerType.Boss || e.BossMoment == EventBossMoment.Killed) return null;
        if (FindBoss(e.BossName) is not { } def || FireAt(e, def) is not { } at) return null;
        return at > _now() ? at.LocalDateTime : null;
    }

    // Whether a loop / auto-lair's stop-after boss moment has come (a timed one; a
    // kill ends it through EventManager.NoteBossKilled). A moment already behind us
    // counts — its column has hit 0 — so the run stops straight away.
    public bool StopReached(ScheduledEvent e)
        => e.StopBossMoment is { } moment && moment != EventBossMoment.Killed
           && FindBoss(e.StopBossName) is { } def
           && FireAt(def, moment, e.StopBossWindowFraction, e.StopBossLeadMinutes) is { } at
           && _now() >= at;

    internal DateTimeOffset? FireAt(ScheduledEvent e, BossDef def)
        => FireAt(def, e.BossMoment, e.BossWindowFraction, e.BossLeadMinutes);

    // The moment on this kill's timer, less the lead.
    private DateTimeOffset? FireAt(BossDef def, EventBossMoment? which, double? fraction, int? leadMinutes)
    {
        if (_timers.KilledAt(def.Name) is not { } killed) return null;
        DateTimeOffset? moment = which switch
        {
            EventBossMoment.CleanupReset => def.RespawnType == BossRespawnType.Cleanup ? _timers.NextCleanupFor(def.Name) : null,
            EventBossMoment.EarlyWindow => TimedMoment(def, killed, fraction ?? BossTimerMath.SpawnFractions(_realm())[0]),
            EventBossMoment.Guaranteed => TimedMoment(def, killed, 1.0),
            _ => null,
        };
        return moment - TimeSpan.FromMinutes(Math.Max(0, leadMinutes ?? 0));
    }

    private DateTimeOffset? TimedMoment(BossDef def, DateTimeOffset killed, double fraction)
        => def.RespawnType != BossRespawnType.Cleanup
           && BossCatalog.EffectiveRegenHours(_gameData, def) is { } hours && hours > 0
            ? killed + TimeSpan.FromHours(hours * fraction)
            : null;

    private IEnumerable<ScheduledEvent> BossEvents()
        => _events.Events.Where(e => e.TriggerType == EventTriggerType.Boss && !e.Disabled).ToList();

    private BossDef? FindBoss(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : _bosses.ResolveForRealm(_realm())
                .FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string Label(ScheduledEvent e) => string.IsNullOrWhiteSpace(e.Name) ? "(unnamed)" : e.Name;
}
