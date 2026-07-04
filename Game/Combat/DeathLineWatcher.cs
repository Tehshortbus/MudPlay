using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Combat;

// Observes the canonical local-player death line ("You have been slain by
// <killer>.") and emits PlayerDied. Pure observation; the recovery flow
// (corpse-walk, item re-pickup, gear re-equip) lives in DeathRecoveryManager.
//
// Separated from DeathRecoveryManager because the death event has multiple
// consumers besides recovery: HealthManager clears any held HealthRecovery /
// ManaRecovery gate on death; CombatManager stops swinging; CashManager cancels
// any in-flight pickup batch; the Workshop DEATH section records the death +
// location for the history list. One watcher feeds them all.
//
// The pattern carries the killer's name in KnownPatterns.UserSlain's first
// capture; consumers read it off PlayerDiedEvent.Killer.
public sealed class DeathLineWatcher : IDisposable
{
    // LogService category for the death notification row.
    public const string LogCategory = "Death";

    private readonly LogService? _log;
    private readonly IDisposable _slainSub;
    private bool _disposed;

    // Fired once per observed local-death line. Consumers run on the
    // MessageRouter's marshalled thread.
    public event Action<PlayerDiedEvent>? PlayerDied;

    public DeathLineWatcher(MessageRouter router, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        _log = log;
        _slainSub = router.Subscribe(KnownPatterns.UserSlain, OnSlain);
    }

    private void OnSlain(MatchResult match)
    {
        string killer = match.Groups.Count > 0 ? match.Groups[0].Trim() : string.Empty;
        PlayerDiedEvent evt = new(killer, DateTimeOffset.Now);
        _log?.Warn(LogCategory,
            killer.Length > 0 ? $"slain by {killer}" : "slain (killer unknown)");
        PlayerDied?.Invoke(evt);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _slainSub.Dispose();
    }
}
