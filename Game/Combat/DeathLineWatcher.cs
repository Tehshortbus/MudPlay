using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Combat;

/// <summary>
/// Phase 9 PR 9.0d — observes the canonical local-player death line
/// ("You have been slain by &lt;killer&gt;.") and emits
/// <see cref="PlayerDied"/>. Pure observation; the recovery flow
/// (corpse-walk, item re-pickup, gear re-equip) lives in
/// DeathRecoveryManager (PR 9.I).
/// </summary>
/// <remarks>
/// <para>
/// Separated from DeathRecoveryManager because the death event has
/// multiple consumers besides recovery: HealthManager (PR 9.B) clears
/// any held HealthRecovery / ManaRecovery gate on death; CombatManager
/// (PR 9.A) stops swinging; CashManager (PR 9.E) cancels any in-flight
/// pickup batch; the Phase 10 Workshop DEATH section records the
/// death + location for the history list. One watcher feeds them all.
/// </para>
/// <para>
/// The pattern carries the killer's name in
/// <see cref="KnownPatterns.UserSlain"/>'s first capture; consumers
/// read it off <see cref="PlayerDiedEvent.Killer"/>.
/// </para>
/// </remarks>
public sealed class DeathLineWatcher : IDisposable
{
    /// <summary>LogService category for the death notification row.</summary>
    public const string LogCategory = "Death";

    private readonly LogService? _log;
    private readonly IDisposable _slainSub;
    private bool _disposed;

    /// <summary>Fired once per observed local-death line. Consumers
    /// run on the MessageRouter's marshalled thread.</summary>
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
