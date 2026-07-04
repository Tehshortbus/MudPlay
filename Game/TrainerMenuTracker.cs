using System.Text;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

// Detects the brief excursion into the in-game trainer stats menu so
// downstream subscribers can react when the user returns to the realm —
// specifically, AutoPartyManager uses MenuExited to refresh any party members
// whose [Invited] slot is still open on our leader-side state but whose own
// party view was dissolved by the brief absence.
//
// State machine, leader-side only:
//   1. User sends train stats (or just train) on the wire — ObserveOutbound
//      arms ExpectingMenuWindow seconds of "expecting menu" time.
//   2. Within that window, the anchored MenuTrainerStatsMarker line ("Point
//      Cost Chart") is observed — confirms we're in the menu, snapshots the
//      current non-self roster, and flips _inMenu = true.
//   3. While in the menu, the in-game prompt (StatusLine) doesn't fire because
//      the trainer screen is a full-screen ANSI menu.
//   4. When the user exits the menu, the next in-game prompt fires; that's our
//      exit signal — fire MenuExited and reset.
//
// The outbound-command gate guarantees no chat / gossip line containing "Point
// Cost Chart" can ever flip the state machine on its own — without an outbound
// train stats the marker is ignored entirely.
//
// Initial character creation is the one entry path with no outbound train
// stats: the game menu walks class → race → alignment → training on its own.
// That training screen still renders the same full-screen menu, and its top
// row carries the "MAJOR MUD Character Creation" box beside the "Point Cost
// Chart" panel. A marker line bearing BOTH phrases stands in for the outbound
// gate — a chat line can't carry both — so the state machine flips and
// downstream input switches to character mode just as it does for in-game
// training.
public sealed class TrainerMenuTracker : IDisposable
{
    private readonly PartyState _party;
    private readonly LogService? _log;
    private readonly IDisposable _markerSub;
    private readonly IDisposable _promptSub;
    private bool _disposed;

    // Window after observing outbound train stats during which a marker line
    // confirms entry.
    public TimeSpan ExpectingMenuWindow { get; set; } = TimeSpan.FromSeconds(5);

    // Test seam.
    public Func<DateTime> NowProvider { get; set; } = () => DateTime.UtcNow;

    // Substring unique to the initial character-creation training screen — the
    // "MAJOR MUD Character Creation" box shares the menu's top terminal row
    // with the "Point Cost Chart" panel. Its presence on a marker line
    // confirms menu entry without an outbound train stats: character creation
    // walks class → race → alignment → training with no such command, so the
    // outbound gate never arms. A single line carrying BOTH this phrase and
    // "Point Cost Chart" is the full-screen menu, not chat noise.
    private const string CharacterCreationSignature = "Character Creation";

    private DateTime? _expectingMenuSince;
    private bool _inMenu;
    private List<string> _rosterSnapshot = new();

    // True while we believe the trainer-stats menu is the active screen.
    public bool IsInTrainerMenu => _inMenu;

    // Snapshot of non-self party member names taken at the moment we confirmed
    // menu entry. Subscribers to MenuExited inspect this to decide who to
    // re-invite.
    public IReadOnlyList<string> RosterAtMenuEntry => _rosterSnapshot;

    // Fires once when the trainer-stats marker confirms entry into the
    // full-screen menu. Lets subscribers (e.g. the terminal's character-mode
    // input switch) react to the excursion start.
    public event Action? MenuEntered;

    // Fires once when the in-game prompt returns after an armed menu session —
    // the user has exited the trainer screen.
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

    // Called by the wire-send path so we can spot the user's own outbound
    // train stats / train command. This is the gate that prevents chat-noise
    // false positives — without an outbound train command the menu-marker
    // handler is a no-op.
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

    private void OnMenuMarker(MatchResult match)
    {
        // Two confirmation paths:
        //  1. In-game `train stats` / `train` — ObserveOutbound armed a
        //     short window and this marker landed inside it.
        //  2. Initial character creation — the training screen is reached
        //     from the class/race/alignment flow with no outbound `train
        //     stats`, so the gate never armed. The char-creation training
        //     row carries the "Character Creation" box title next to the
        //     "Point Cost Chart" panel; a line bearing both phrases is the
        //     full-screen menu, not chat noise, so it stands in for the gate.
        bool gateArmed = false;
        if (_expectingMenuSince is { } armedAt)
        {
            if (NowProvider() - armedAt <= ExpectingMenuWindow)
                gateArmed = true;
            else
                _expectingMenuSince = null; // window lapsed — disarm
        }

        bool characterCreation =
            match.Text.Contains(CharacterCreationSignature, StringComparison.OrdinalIgnoreCase);

        if (!gateArmed && !characterCreation) return;
        if (_inMenu) return;
        _inMenu = true;
        _rosterSnapshot = _party.Members
            .Where(m => !m.IsSelf && !string.IsNullOrEmpty(m.Name))
            .Select(m => m.Name)
            .ToList();
        _log?.Log(LogSeverity.Info, "TrainerMenu",
            $"Entered trainer menu ({(gateArmed ? "train-stats gate" : "character creation")}) — "
            + $"snapshot {_rosterSnapshot.Count} non-self member(s).");
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
