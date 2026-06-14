using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.Combat;
using FujinTerm.Game.Map;
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
    private readonly RoomTracker _roomTracker;
    private readonly LogService? _log;
    private AutoWalkManager? _walker;
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
        RoomTracker roomTracker,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(deathWatcher);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(roomTracker);
        _deathWatcher = deathWatcher;
        _profile = profile;
        _roomTracker = roomTracker;
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

    /// <summary>
    /// Wire the walker used by <see cref="WalkToDeathRoom"/> /
    /// <see cref="RecoverNow"/>. Set post-construction because the
    /// <see cref="AutoWalkManager"/> is built after this manager in
    /// <see cref="AppServices"/>.
    /// </summary>
    public void AttachWalker(AutoWalkManager walker) => _walker = walker;

    /// <summary>
    /// The loaded profile's death history (oldest → newest). Empty when
    /// no profile is loaded or the lucky character has never died. The
    /// DEATH grid sorts this newest-first for display.
    /// </summary>
    public IReadOnlyList<DeathRecord> Records =>
        _profile.Current?.DeathHistory is { } list ? list : Array.Empty<DeathRecord>();

    /// <summary>
    /// Auto-grab a deathpile's lost items (ignoring per-item auto-get
    /// policy) when re-entering the death room. Persisted per-character.
    /// The grab itself is inert until inventory tracking records lost
    /// items; the preference is stored now.
    /// </summary>
    public bool AutoRecover
    {
        get => _profile.Current?.DeathAutoRecover ?? false;
        set
        {
            if (_profile.Current is not { } p || p.DeathAutoRecover == value) return;
            p.DeathAutoRecover = value;
            _profile.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Re-equip items that were worn at death after recovering them.
    /// Persisted per-character; inert until inventory tracking records
    /// what was equipped at death.
    /// </summary>
    public bool AutoEquip
    {
        get => _profile.Current?.DeathAutoEquip ?? false;
        set
        {
            if (_profile.Current is not { } p || p.DeathAutoEquip == value) return;
            p.DeathAutoEquip = value;
            _profile.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Manually flag a record as fully recovered (user pressed "Mark
    /// Recovered"). Sets <see cref="DeathRecoveryStatus.Recovered"/>,
    /// persists, and notifies binders.
    /// </summary>
    public void MarkRecovered(DeathRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Status == DeathRecoveryStatus.Recovered) return;
        record.Status = DeathRecoveryStatus.Recovered;
        record.RecoveryMessage = "Marked recovered by user.";
        _profile.Save();
        OnPropertyChanged(nameof(Records));
    }

    /// <summary>Remove a single record from the history.</summary>
    public void ClearSelected(DeathRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_profile.Current?.DeathHistory is not { } list || !list.Remove(record)) return;
        _profile.Save();
        SyncFromProfile();
        OnPropertyChanged(nameof(Records));
    }

    /// <summary>Remove every record whose status is Recovered.</summary>
    public void ClearAllRecovered()
    {
        if (_profile.Current?.DeathHistory is not { } list) return;
        if (list.RemoveAll(r => r.Status == DeathRecoveryStatus.Recovered) == 0) return;
        _profile.Save();
        SyncFromProfile();
        OnPropertyChanged(nameof(Records));
    }

    /// <summary>
    /// Walk to the room a death occurred in. Returns false when no
    /// walker is attached or the record has no recorded room.
    /// </summary>
    public bool WalkToDeathRoom(DeathRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_walker is null || record.Room is not { } r) return false;
        return _walker.WalkTo(new RoomKey(r.Map, r.Room));
    }

    /// <summary>
    /// Demand signal to recover a deathpile. If we're already standing
    /// in the death room, grab every matching pile item we can in place;
    /// otherwise start walking there and recover on arrival. The actual
    /// item grab + re-equip is deferred until inventory tracking records
    /// what was lost / worn at death — for now the in-room branch logs
    /// the intent and the walk branch just routes the walker.
    /// </summary>
    public bool RecoverNow(DeathRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Room is not { } target) return false;

        Room? here = _roomTracker.State.CurrentRoom;
        bool inRoom = here is not null && here.Key.Map == target.Map && here.Key.Room == target.Room;
        string where = record.RoomName ?? record.RoomKeyText;

        if (inRoom)
        {
            _log?.Info(LogCategory,
                $"recover-now: in death room {where} — grabbing pile (item grab deferred until inventory tracking lands)");
            return true;
        }

        bool walking = WalkToDeathRoom(record);
        if (walking)
            _log?.Info(LogCategory, $"recover-now: walking to {where} then recovering");
        return walking;
    }

    /// <summary>
    /// Append a synthetic death record (the "Simulate Death" button) so
    /// the DEATH grid + recovery flow can be exercised without dying in
    /// game. Decrements the displayed lives by one, floored at zero.
    /// </summary>
    public void SimulateDeath()
    {
        if (_profile.Current is not { } p) return;
        p.DeathHistory ??= new List<DeathRecord>();
        Room? here = _roomTracker.State.CurrentRoom;
        var record = new DeathRecord(
            DateTimeOffset.UtcNow,
            here is null ? null : new RoomRef(here.Key.Map, here.Key.Room),
            Math.Max(0, LivesRemaining - 1),
            "Simulated death (test).")
        {
            RecordNumber = p.DeathHistory.Count + 1,
            RoomName = here?.Name,
            Status = DeathRecoveryStatus.Active,
        };
        p.DeathHistory.Add(record);
        _profile.Save();
        SyncFromProfile();
        OnPropertyChanged(nameof(Records));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _deathWatcher.PlayerDied -= OnPlayerDied;
        _profile.ProfileLoaded -= OnProfileLoaded;
    }
}
