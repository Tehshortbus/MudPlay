using System.Text;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game;

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
    private readonly IDisposable _roomSub;
    private bool _disposed;

    // Window after observing outbound train stats during which a marker line
    // confirms entry.
    public TimeSpan ExpectingMenuWindow { get; set; } = TimeSpan.FromSeconds(5);

    private readonly Func<DateTime> _now;

    // Substring unique to the initial character-creation training screen — the
    // "MAJOR MUD Character Creation" box shares the menu's top terminal row
    // with the "Point Cost Chart" panel. Its presence on a marker line
    // confirms menu entry without an outbound train stats: character creation
    // walks class → race → alignment → training with no such command, so the
    // outbound gate never arms. A single line carrying BOTH this phrase and
    // "Point Cost Chart" is the full-screen menu, not chat noise.
    private const string CharacterCreationSignature = "Character Creation";

    // ParaMUD abbreviates the box title to "Char. Creation" rather than the
    // stock "Character Creation"; match both so the creation stat box is
    // recognised on either realm.
    private const string CharCreationAbbrevSignature = "Char. Creation";

    // The framed "Point Cost Chart" panel beside the creation/train box title.
    // Public so the screen-feed path (MainWindowViewModel) can cheaply pre-check
    // for it before building screen text to hand to ObserveScreen. The emitted-
    // line path matches the same text via KnownPatterns.MenuTrainerStatsMarker.
    public const string StatBoxMarker = "Point Cost Chart";

    private DateTime? _expectingMenuSince;
    private bool _inMenu;
    private List<string> _rosterSnapshot = new();

    // Input-mode state, driven by the outbound `train stats` command rather
    // than the marker (see InputMenuEntered). _skipNextInputPrompt swallows the
    // command's own `[HP=...]:train stats` prompt echo so it isn't mistaken for
    // the menu-exit prompt.
    private bool _inputMenuActive;
    private bool _skipNextInputPrompt;
    // Same echo-swallow as _skipNextInputPrompt, but for the live-wire prompt
    // path (NotifyLivePromptObserved). Kept separate so the committed-line and
    // in-place-redraw release triggers each consume their own echo without
    // fighting over one flag.
    private bool _skipNextLivePrompt;

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

    public TrainerMenuTracker(MessageRouter router, PartyState party, LogService? log = null, Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(party);
        _party = party;
        _log   = log;
        _markerSub = router.Subscribe(KnownPatterns.MenuTrainerStatsMarker, OnMenuMarker);
        _promptSub = router.Subscribe(KnownPatterns.StatusLine,             OnPrompt);
        _roomSub   = router.Subscribe(KnownPatterns.RoomExits,              OnRoomDisplay);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _markerSub.Dispose();
        _promptSub.Dispose();
        _roomSub.Dispose();
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
        _expectingMenuSince = _now();
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
            _skipNextLivePrompt  = true; // same, for the live-wire prompt path
            if (!_inputMenuActive)
            {
                _inputMenuActive = true;
                _log?.Log(LogSeverity.Info, "TrainerMenu",
                    "Armed character-mode input for `train stats` screen.");
                InputMenuEntered?.Invoke();
            }
        }
    }

    // Live-screen detection for the character-creation stat box, complementing
    // the emitted-line OnMenuMarker. The creation box is painted with cursor
    // positioning, so its "Point Cost Chart" marker row never completes as an
    // emitted line until the box is torn down — too late: arrow keys stay
    // captured by history-recall for the whole creation session (the reported
    // "arrows cycle the given/family names" bug). Scanning the live screen each
    // feed flips character-mode input the moment the box appears. Scoped to
    // character creation (the box carries the "Character Creation" / "Char.
    // Creation" title): the in-game `train stats` screen has no such title and
    // keeps arming off its outbound command, so this can't false-positive on it
    // or on chat noise. Fed the flattened visible screen by the terminal-feed
    // path, which pre-filters on StatBoxMarker so this runs only when a stat box
    // is actually on screen.
    public void ObserveScreen(string screenText)
    {
        if (_inputMenuActive) return;   // already armed — nothing to do
        if (string.IsNullOrEmpty(screenText)) return;
        if (!screenText.Contains(StatBoxMarker, StringComparison.OrdinalIgnoreCase)) return;

        bool creation =
            screenText.Contains(CharacterCreationSignature, StringComparison.OrdinalIgnoreCase)
            || screenText.Contains(CharCreationAbbrevSignature, StringComparison.OrdinalIgnoreCase);
        if (!creation) return;

        _inputMenuActive = true;
        _log?.Log(LogSeverity.Info, "TrainerMenu",
            "Character-creation stat box detected on screen — armed character-mode input.");
        InputMenuEntered?.Invoke();
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
            if (_now() - armedAt <= ExpectingMenuWindow)
                gateArmed = true;
            else
                _expectingMenuSince = null; // window lapsed — disarm
        }

        bool characterCreation =
            match.Text.Contains(CharacterCreationSignature, StringComparison.OrdinalIgnoreCase)
            || match.Text.Contains(CharCreationAbbrevSignature, StringComparison.OrdinalIgnoreCase);

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
        //
        // BOTH keyboard-owning flags are cleared BEFORE firing any exit event.
        // InputMenuExited drives the auto-train loop-resume walk synchronously, and
        // TrainerScreenGate holds the engine send-gate while MenuOwnsKeyboard
        // (_inputMenuActive || _inMenu) is true. If _inMenu were still set when
        // InputMenuExited fired, the gate would stay held and silently drop the
        // resume's first move byte with no retry — the walker draws the route back
        // but never steps (report paradigm-20260813-063517).
        bool fireInputExited = false;
        if (_inputMenuActive)
        {
            if (_skipNextInputPrompt)
                _skipNextInputPrompt = false;
            else
            {
                _inputMenuActive = false;
                fireInputExited = true;
            }
        }

        bool fireMenuExited = _inMenu;
        if (_inMenu)
        {
            _inMenu = false;
            _expectingMenuSince = null;
        }

        if (fireInputExited)
        {
            _log?.Log(LogSeverity.Info, "TrainerMenu",
                "In-game prompt returned — keyboard released, firing InputMenuExited.");
            InputMenuExited?.Invoke();
        }
        if (fireMenuExited)
        {
            _log?.Log(LogSeverity.Info, "TrainerMenu", "Exited trainer menu — firing MenuExited.");
            MenuExited?.Invoke();
        }
    }

    // A room display ("Obvious exits: …") is emitted the instant the trainer /
    // creation form closes and you're back in the world. The returning `[HP=…]:`
    // prompt, by contrast, only regen-redraws in place until you type — so it
    // isn't committed as a line (and OnPrompt can't fire) until your NEXT
    // command's Enter. Waiting for that means the command itself is typed while
    // the form's character-mode still holds the keyboard: it goes out a byte at a
    // time, and its outbound observers never see the whole token (StatParser's
    // `stat` gate stayed shut, so the Player Workshop never updated — report
    // paradigm-20260906-090057). The room display is the prompt "back in the
    // world" signal, so release line-mode on it. No-op unless the keyboard is
    // actually held, so a normal room display in play does nothing.
    private void OnRoomDisplay(MatchResult _)
    {
        if (!MenuOwnsKeyboard) return;
        ForceExit("room display observed — back in the world");
    }

    // Fed by AppServices for every live statline WirePromptScanner sees off the
    // raw wire — including the in-place redraws the committed StatusLine pattern
    // never emits as a line. The returning `[HP=…]:` after a `train stats` form
    // closes only regen-redraws in place (no CR), so OnPrompt (committed) can't
    // fire until the user's NEXT command's Enter — by which point that command
    // was typed in the form's character-mode and sent byte-by-byte (report
    // paradigm-20260906-090057). This releases line-mode on the exit prompt the
    // instant the form closes, even in a dark room with no "Obvious exits" line.
    // Scoped to the char-mode (`train stats`) state; the marker-confirmed party
    // flow stays on the committed OnPrompt. Skips the command's own echo statline
    // first (the form emits no statline, so the very next live prompt is the
    // exit). ForceExit tears both flags down before firing, preserving the
    // send-gate ordering the loop-resume depends on.
    public void NotifyLivePromptObserved()
    {
        if (!_inputMenuActive) return;
        if (_skipNextLivePrompt) { _skipNextLivePrompt = false; return; }
        ForceExit("in-place statline prompt observed — back in the world");
    }

    // Force the keyboard release when the normal exit-prompt detection is missed — the
    // AutoTrainManager CP-replay ExitGrace fallback calls this. Mirrors OnPrompt's
    // teardown: clear BOTH keyboard-owning flags BEFORE firing the exit events, so the
    // event-driven TrainerScreenGate re-evaluates with MenuOwnsKeyboard=false and releases
    // the engine send-gate before the loop-resume those events drive sends its first move.
    // Without this, a missed exit prompt leaves the gate held and the resumed loop's first
    // move byte is silently dropped — the walker draws the route but stands idle (report
    // paradigm-20260815-072308: 10 minutes idle after CP allocation). No-op when nothing
    // is held; unconditional otherwise (no skip-echo logic — this is the recovery path).
    public void ForceExit(string reason)
    {
        if (!MenuOwnsKeyboard) return;
        _log?.Log(LogSeverity.Info, "TrainerMenu",
            $"Forcing trainer keyboard release ({reason}) — exit prompt missed.");
        bool fireInputExited = _inputMenuActive;
        bool fireMenuExited = _inMenu;
        _inputMenuActive = false;
        _inMenu = false;
        _expectingMenuSince = null;
        _skipNextInputPrompt = false;
        _skipNextLivePrompt = false;
        if (fireInputExited) InputMenuExited?.Invoke();
        if (fireMenuExited) MenuExited?.Invoke();
    }
}
