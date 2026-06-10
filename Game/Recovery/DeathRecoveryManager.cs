using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.Combat;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Recovery;

/// <summary>
/// Phase 9 PR 9.I — death observation aggregator. Composes
/// <see cref="DeathLineWatcher.PlayerDied"/> and the per-character
/// <see cref="CharacterProfile.DeathHistory"/> into a live observable
/// shape that the Workshop DEATH section binds to.
/// </summary>
/// <remarks>
/// <para>
/// Owns four observables surfaced to the UI:
/// </para>
/// <list type="bullet">
/// <item><see cref="LivesRemaining"/> — most-recent lives count from
/// the <c>You now have N lives remaining.</c> line (via
/// <c>DeathDetector → RoomTracker.NoteDeath</c> which writes
/// <see cref="DeathRecord.LivesRemaining"/> to the profile). We mirror
/// the latest record's count so binders don't have to walk the list.</item>
/// <item><see cref="LastKiller"/> — most-recent killer name from the
/// <c>You have been slain by &lt;X&gt;.</c> line (via
/// <see cref="DeathLineWatcher.PlayerDied"/>).</item>
/// <item><see cref="LastDeathAt"/> — wall-clock time of the most-
/// recent death event we observed.</item>
/// <item><see cref="DeathCount"/> — total deaths in the profile's
/// history.</item>
/// </list>
/// <para>
/// The <c>@comeback</c> remote command is a separate party-pickup
/// flow (stranded-follower → leader) owned by
/// <see cref="Remote.PartyComebackManager"/>, not this aggregator —
/// it has nothing to do with death recovery.
/// </para>
/// </remarks>
public sealed partial class DeathRecoveryManager : ObservableObject, IDisposable
{
    /// <summary>LogService category — appears as <c>[DeathRecovery]</c>
    /// rows per observation + comeback request.</summary>
    public const string LogCategory = "DeathRecovery";

    private readonly DeathLineWatcher _deathWatcher;
    private readonly ProfileService _profile;
    private readonly LogService? _log;
    private bool _disposed;

    [ObservableProperty]
    [field: Owner(typeof(DeathRecoveryManager))]
    private int _livesRemaining;

    [ObservableProperty]
    [field: Owner(typeof(DeathRecoveryManager))]
    private string? _lastKiller;

    [ObservableProperty]
    [field: Owner(typeof(DeathRecoveryManager))]
    private DateTimeOffset? _lastDeathAt;

    [ObservableProperty]
    [field: Owner(typeof(DeathRecoveryManager))]
    private int _deathCount;

    public DeathRecoveryManager(
        DeathLineWatcher deathWatcher,
        ProfileService profile,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(deathWatcher);
        ArgumentNullException.ThrowIfNull(profile);
        _deathWatcher = deathWatcher;
        _profile = profile;
        _log = log;

        _deathWatcher.PlayerDied += OnPlayerDied;
        _profile.ProfileLoaded += OnProfileLoaded;

        // Seed observables from whatever profile is already loaded
        // (handles late-construction order — engine wired AFTER
        // initial profile load).
        SyncFromProfile();
    }

    private void OnPlayerDied(PlayerDiedEvent evt)
    {
        LastKiller = evt.Killer;
        LastDeathAt = evt.At;
        _log?.Info(LogCategory, $"player slain by={evt.Killer}");
        // LivesRemaining + DeathCount update via the
        // DeathDetector → RoomTracker.NoteDeath → profile.DeathHistory
        // path. We mirror it after the profile write lands.
        SyncFromProfile();
    }

    private void OnProfileLoaded(CharacterProfile _) => SyncFromProfile();

    private void SyncFromProfile()
    {
        CharacterProfile? p = _profile.Current;
        if (p?.DeathHistory is null || p.DeathHistory.Count == 0)
        {
            // No history yet on this profile.
            DeathCount = 0;
            return;
        }

        DeathRecord latest = p.DeathHistory[^1];
        LivesRemaining = latest.LivesRemaining;
        DeathCount = p.DeathHistory.Count;
        if (latest.At != default)
            LastDeathAt = latest.At;
    }

    /// <summary>
    /// Refresh the observables from the loaded profile's death
    /// history. Public so the Workshop DEATH section can request a
    /// manual refresh after edits (mark-recovered, etc.).
    /// </summary>
    public void Refresh() => SyncFromProfile();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _deathWatcher.PlayerDied -= OnPlayerDied;
        _profile.ProfileLoaded -= OnProfileLoaded;
    }
}
