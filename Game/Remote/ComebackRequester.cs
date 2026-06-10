using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Remote;

/// <summary>
/// Follower-side <c>@comeback</c> sender. When the party leader walks off
/// and a movement-blocking condition leaves us behind, this telepaths
/// <c>@comeback &lt;map&gt;/&lt;room&gt;</c> (or a bare <c>@comeback</c>)
/// to the leader so their <see cref="PartyComebackManager"/> recovers us.
/// </summary>
/// <remarks>
/// <para>
/// <b>Disambiguating "left behind" from a deliberate unfollow.</b> The
/// game prints <c>"You are no longer following X."</c> for three distinct
/// situations: the leader uninvited us, we issued our own <c>unfollow</c>,
/// or we genuinely couldn't keep up. Only the third warrants an automatic
/// <c>@comeback</c>. The tell is a movement-failure line fired the instant
/// before — <c>"You can't seem to move anywhere!"</c> (a prevents-movement
/// gamedata flag) or <c>"...too heavy to move"</c> (over-encumbered). If
/// one of those landed inside <see cref="LeftBehindWindow"/> of the
/// "no longer following" line, we were stranded; otherwise it was
/// deliberate and we stay quiet.
/// </para>
/// <para>
/// The leader's name comes from the <c>"no longer following"</c> line's
/// capture group (already a given/first name — the <c>\w+</c> pattern
/// never spans a space). The room comes from
/// <see cref="RoomTracker"/> when its confidence is
/// <see cref="RoomConfidence.Confirmed"/>; otherwise we send a bare
/// <c>@comeback</c> and let the leader backtrack to find us.
/// </para>
/// </remarks>
public sealed class ComebackRequester : IDisposable
{
    private const string LogCategory = "Comeback";

    /// <summary>Maximum gap between a movement-failure line and the
    /// following <c>"You are no longer following X."</c> for the pair to
    /// count as a genuine left-behind. A deliberate uninvite/unfollow has
    /// no preceding failure, so its gap is effectively infinite.</summary>
    private static readonly TimeSpan LeftBehindWindow = TimeSpan.FromSeconds(3);

    private readonly RoomTracker _tracker;
    private readonly LogService? _log;
    private readonly List<IDisposable> _subs = new();

    private Action<byte[]>? _wireSender;
    private DateTimeOffset _moveFailedAt = DateTimeOffset.MinValue;
    private bool _disposed;

    /// <summary>Test seam for the clock so the
    /// <see cref="LeftBehindWindow"/> gate is deterministic.</summary>
    internal Func<DateTimeOffset> NowProvider { get; set; } = static () => DateTimeOffset.Now;

    /// <summary>Mirrors
    /// <see cref="Models.Profile.OtherSettings.AutoRequestComebackWhenLeftBehind"/>.
    /// When false, left-behind detection still runs but no
    /// <c>@comeback</c> is sent.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Test-visible record of every wire payload sent.</summary>
    internal List<byte[]> LastSentForTests { get; } = new();

    public ComebackRequester(MessageRouter router, RoomTracker tracker, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
        _log = log;

        _subs.Add(router.Subscribe(KnownPatterns.MovementFailedStuck, OnMovementFailed));
        _subs.Add(router.Subscribe(KnownPatterns.MovementFailedHeavy, OnMovementFailed));
        _subs.Add(router.Subscribe(KnownPatterns.PartyYouNoLongerFollowing, OnNoLongerFollowing));
    }

    /// <summary>Bind the outbound wire — the same
    /// <c>TelnetClient.SendAsync</c> wrapper the other engines use.</summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (IDisposable sub in _subs) sub.Dispose();
        _subs.Clear();
    }

    private void OnMovementFailed(MatchResult _) => _moveFailedAt = NowProvider();

    private void OnNoLongerFollowing(MatchResult result)
    {
        DateTimeOffset now = NowProvider();
        if (now - _moveFailedAt > LeftBehindWindow)
            return; // deliberate uninvite / our own unfollow — stay quiet
        _moveFailedAt = DateTimeOffset.MinValue; // consume the failure so it can't arm a later line

        string leaderGiven = result.Groups.Count > 0 ? result.Groups[0].Trim() : string.Empty;
        if (string.IsNullOrEmpty(leaderGiven))
            return;

        if (!Enabled)
        {
            _log?.Debug(LogCategory, $"left behind by {leaderGiven} but auto-@comeback disabled");
            return;
        }

        // Only attach a room when we're confident where we are; a stale
        // guess would send the leader to the wrong place. Bare @comeback
        // makes the leader backtrack the path they just walked.
        string payload = "@comeback";
        if (_tracker.State.Confidence == RoomConfidence.Confirmed
            && _tracker.State.CurrentRoom is { } room)
            payload = $"@comeback {room.Key}";

        string wire = $"/{leaderGiven} {payload}";
        byte[] bytes = Encoding.Latin1.GetBytes(wire + "\r");
        LastSentForTests.Add(bytes);
        _wireSender?.Invoke(bytes);
        _log?.Info(LogCategory, $"left behind by {leaderGiven} — sent {payload}");
    }
}
