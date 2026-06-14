using System.Text;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

/// <summary>
/// Detects the brief excursion into the in-game trainer stats menu so
/// downstream subscribers can react when the user returns to the realm —
/// specifically, <see cref="AutoPartyManager"/> uses
/// <see cref="MenuExited"/> to refresh any party members whose
/// <c>[Invited]</c> slot is still open on our leader-side state but
/// whose own party view was dissolved by the brief absence.
/// </summary>
/// <remarks>
/// <para>
/// State machine, leader-side only:
/// </para>
/// <list type="number">
///   <item>User sends <c>train stats</c> (or just <c>train</c>) on the
///         wire — <see cref="ObserveOutbound"/> arms
///         <see cref="ExpectingMenuWindow"/> seconds of "expecting
///         menu" time.</item>
///   <item>Within that window, the anchored
///         <see cref="KnownPatterns.MenuTrainerStatsMarker"/> line
///         (<c>"Point Cost Chart"</c>) is observed — confirms we're
///         in the menu, snapshots the current non-self roster, and
///         flips <c>_inMenu = true</c>.</item>
///   <item>While in the menu, the in-game prompt
///         (<see cref="KnownPatterns.StatusLine"/>) doesn't fire
///         because the trainer screen is a full-screen ANSI menu.</item>
///   <item>When the user exits the menu, the next in-game prompt
///         fires; that's our exit signal — fire
///         <see cref="MenuExited"/> and reset.</item>
/// </list>
/// <para>
/// The outbound-command gate guarantees no chat / gossip line containing
/// <c>"Point Cost Chart"</c> can ever flip the state machine on its own —
/// without an outbound <c>train stats</c> the marker is ignored entirely.
/// </para>
/// </remarks>
public sealed class TrainerMenuTracker : IDisposable
{
    private readonly PartyState _party;
    private readonly LogService? _log;
    private readonly IDisposable _markerSub;
    private readonly IDisposable _promptSub;
    private bool _disposed;

    /// <summary>Window after observing outbound <c>train stats</c> during which a marker line confirms entry.</summary>
    public TimeSpan ExpectingMenuWindow { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Test seam.</summary>
    public Func<DateTime> NowProvider { get; set; } = () => DateTime.UtcNow;

    private DateTime? _expectingMenuSince;
    private bool _inMenu;
    private List<string> _rosterSnapshot = new();

    /// <summary>True while we believe the trainer-stats menu is the active screen.</summary>
    public bool IsInTrainerMenu => _inMenu;

    /// <summary>
    /// Snapshot of non-self party member names taken at the moment we
    /// confirmed menu entry. Subscribers to <see cref="MenuExited"/>
    /// inspect this to decide who to re-invite.
    /// </summary>
    public IReadOnlyList<string> RosterAtMenuEntry => _rosterSnapshot;

    /// <summary>
    /// Fires once when the trainer-stats marker confirms entry into the
    /// full-screen menu. Lets subscribers (e.g. the terminal's
    /// character-mode input switch) react to the excursion start.
    /// </summary>
    public event Action? MenuEntered;

    /// <summary>
    /// Fires once when the in-game prompt returns after an armed menu
    /// session — the user has exited the trainer screen.
    /// </summary>
    public event Action? MenuExited;

    public TrainerMenuTracker(MessageRouter router, PartyState party, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(party);
        _party = party;
        _log   = log;
        _markerSub = router.Subscribe(KnownPatterns.MenuTrainerStatsMarker, OnMenuMarker);
        _promptSub = router.Subscribe(KnownPatterns.StatusLine,             OnPrompt);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _markerSub.Dispose();
        _promptSub.Dispose();
    }

    /// <summary>
    /// Called by the wire-send path so we can spot the user's own
    /// outbound <c>train stats</c> / <c>train</c> command. This is the
    /// gate that prevents chat-noise false positives — without an
    /// outbound train command the menu-marker handler is a no-op.
    /// </summary>
    public void ObserveOutbound(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > 32) return;
        string raw = Encoding.Latin1.GetString(bytes);
        string cmd = raw.TrimEnd('\r', '\n', '\0').Trim().ToLowerInvariant();
        if (cmd != "train stats" && cmd != "train") return;
        _expectingMenuSince = NowProvider();
        _log?.Log(LogSeverity.Info, "TrainerMenu",
            $"Observed outbound `{cmd}` — armed menu-marker watch for {ExpectingMenuWindow.TotalSeconds:0} s.");
    }

    private void OnMenuMarker(MatchResult _)
    {
        if (_expectingMenuSince is null) return;
        DateTime now = NowProvider();
        if (now - _expectingMenuSince.Value > ExpectingMenuWindow)
        {
            _expectingMenuSince = null;
            return;
        }
        if (_inMenu) return;
        _inMenu = true;
        _rosterSnapshot = _party.Members
            .Where(m => !m.IsSelf && !string.IsNullOrEmpty(m.Name))
            .Select(m => m.Name)
            .ToList();
        _log?.Log(LogSeverity.Info, "TrainerMenu",
            $"Entered trainer menu — snapshot {_rosterSnapshot.Count} non-self member(s).");
        MenuEntered?.Invoke();
    }

    private void OnPrompt(MatchResult _)
    {
        if (!_inMenu) return;
        _inMenu = false;
        _expectingMenuSince = null;
        _log?.Log(LogSeverity.Info, "TrainerMenu", "Exited trainer menu — firing MenuExited.");
        MenuExited?.Invoke();
    }
}
