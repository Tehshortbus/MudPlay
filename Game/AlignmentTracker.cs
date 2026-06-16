using System;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game;

/// <summary>
/// Tracks whether the local character's last-observed alignment is stale.
/// MajorMUD prints "A dark cloud passes over you" whenever the character's
/// alignment shifts toward evil (an evil-point gain) — but the alignment word
/// the Character Workshop displays (parsed from <c>who</c>) only updates on the
/// next <c>who</c>. This watcher flags the alignment stale on that line and
/// clears the flag once our own row is re-observed in a <c>who</c> response.
/// </summary>
/// <remarks>
/// Long-lived (app lifetime), so the dark-cloud line is caught even while the
/// Character Workshop is closed; the Workshop's Character Info tab reads
/// <see cref="IsStale"/> when it opens and tracks it live via
/// <see cref="StaleChanged"/>.
/// </remarks>
public sealed class AlignmentTracker : IDisposable
{
    private readonly PlayerStats _stats;
    private readonly PlayerDatabase _players;
    private readonly IDisposable _darkCloudSub;

    /// <summary>True when a dark-cloud line has fired since the last <c>who</c> refresh.</summary>
    public bool IsStale { get; private set; }

    /// <summary>Raised whenever <see cref="IsStale"/> changes.</summary>
    public event Action? StaleChanged;

    public AlignmentTracker(MessageRouter router, PlayerStats stats, PlayerDatabase players)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(players);
        _stats = stats;
        _players = players;

        _darkCloudSub = router.Subscribe(
            Services.Patterns.KnownPatterns.AlignmentDarkCloud, _ => SetStale(true));
        _players.ObservationRecorded += OnObservationRecorded;
    }

    // Our own row re-observed in a `who` → the displayed alignment is fresh
    // again. Names are matched on GIVEN name (the same key the observation was
    // recorded under), case-insensitively.
    private void OnObservationRecorded(string givenName)
    {
        if (!IsStale) return;
        (string self, _) = PlayerObservation.SplitName(_stats.Name);
        if (!string.IsNullOrEmpty(self)
            && string.Equals(self, givenName, StringComparison.OrdinalIgnoreCase))
        {
            SetStale(false);
        }
    }

    private void SetStale(bool value)
    {
        if (IsStale == value) return;
        IsStale = value;
        StaleChanged?.Invoke();
    }

    public void Dispose()
    {
        _darkCloudSub.Dispose();
        _players.ObservationRecorded -= OnObservationRecorded;
    }
}
