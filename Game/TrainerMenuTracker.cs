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

    // Input-mode state, driven by the outbound `train stats` command rather
    // than the marker (see InputMenuEntered). _skipNextInputPrompt swallows the
    // command's own `[HP=...]:train stats` prompt echo so it isn't mistaken for
    // the menu-exit prompt.
    private bool _inputMenuActive;
    private bool _skipNextInputPrompt;

    // True while we believe the trainer-stats menu is the active screen
    // (marker-confirmed — never sets on a cursor-positioned menu whose marker
    // row doesn't scroll; use IsInputMenuActive for the realm-independent read).
    public bool IsInTrainerMenu => _inMenu;

    // True while character-mode input is armed for the `train stats` screen —
    // set on the outbound command and cleared when the in-game prompt returns.
    // Unlike IsInTrainerMenu this is realm-independent (driven by the command,
    // not the marker), so it's the reliable "the full-screen menu owns the
    // keyboard right now" signal that automated wire sends must respect.
    public bool IsInputMenuActive => _inputMenuActive;

    // The composite "a trainer / creation screen owns the keyboard right now"
    // read that automated wall-clock wire sends (par poll, @health nag, the
    // @heal-driven par) MUST gate on. It's the OR of both entry paths: the
    // command-armed `train stats` input session (realm-independent) and the
    // marker-confirmed full-screen menu (covers character creation, which has no
    // outbound command to arm on). Gating on IsInTrainerMenu alone is a trap: on
    // a cursor-positioned menu (Paradigm's stat box) the "Point Cost Chart"
    // marker row never scrolls inline, so that flag never confirms — yet the
    // screen owns the keyboard, and the box's FIRST field is the Family Name, so
    // a stray automated `par\r` types into it and overwrites the character's last
    // name. IsInputMenuActive carries that case; this property folds both in.
    public bool MenuOwnsKeyboard => _inputMenuActive || _inMenu;

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

    // Input character-mode switch, driven by the outbound `train stats`
    // command rather than the marker. A full-screen menu drawn with cursor
    // positioning (Paradigm's stat box) never emits its "Point Cost Chart"
    // marker row until it's torn down — the row only completes when it scrolls
    // off or is overwritten on exit — so a marker-gated switch leaves arrow
    // keys captured by history-recall for the whole session. Stock realms that
    // render the marker inline still work, but the command is the realm-
    // independent signal. Kept separate from MenuEntered/MenuExited so the
    // party re-invite flow stays marker-confirmed (no false-positive invites).
    public event Action? InputMenuEntered;

    // Fires when the in-game prompt returns after the `train stats` screen —
    // the screen has closed, so line-mode input should resume.
    public event Action? InputMenuExited;

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

        // The full-screen stat box is driven by the `train stats` command, not
        // its marker row (which on a cursor-positioned menu only completes on
        // teardown — too late, and on some realms never inline at all). Arm
        // character-mode input off the command itself so arrow keys reach the
        // wire immediately, realm-independently. Bare `train` opens a different
        // (line-driven) prompt, so it doesn't arm input.
        if (cmd == "train stats")
        {
            _skipNextInputPrompt = true; // the command's own prompt echo isn't a menu exit
            if (!_inputMenuActive)
            {
                _inputMenuActive = true;
                _log?.Log(LogSeverity.Info, "TrainerMenu",
                    "Armed character-mode input for `train stats` screen.");
                InputMenuEntered?.Invoke();
            }
        }
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
        // Input-mode teardown runs independently of the marker-confirmed party
        // flow below: the first in-game prompt after arming is the command's own
        // `[HP=...]:train stats` echo (skipped); the next one is the bare prompt
        // that returns when the user leaves the stat box, so line-mode resumes.
        if (_inputMenuActive)
        {
            if (_skipNextInputPrompt)
            {
                _skipNextInputPrompt = false;
            }
            else
            {
                _inputMenuActive = false;
                _log?.Log(LogSeverity.Info, "TrainerMenu",
                    "In-game prompt returned — firing InputMenuExited.");
                InputMenuExited?.Invoke();
            }
        }

        if (!_inMenu) return;
        _inMenu = false;
        _expectingMenuSince = null;
        _log?.Log(LogSeverity.Info, "TrainerMenu", "Exited trainer menu — firing MenuExited.");
        MenuExited?.Invoke();
    }
}
