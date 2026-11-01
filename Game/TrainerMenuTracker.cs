using System.Text;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;

namespace MudPlay.Game;

// Detects the brief excursion into the in-game trainer stats menu so
// downstream subscribers can react when the user returns to the realm —
// specifically, AutoPartyManager uses MenuExited to re-collect the party a
// leader's own train broke up.
//
// State machine:
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
//
// From an outbound `train stats` the keyboard is held until exactly one of:
// the trainer screen is seen (then until the form is left, as for any form), the
// game's own refusal line, or HoldCap with neither. Nothing else releases it
// meanwhile: output already on its way when the command went out answers earlier
// commands (a room an Enter redrew, the prompt a party line redraws), and lines
// may come between the game's echo of `train stats` and the form (reports
// paradigm-20261026-225703, -225718, -225733: the release went on such a room
// display and prompt, and the party's telepaths and `par` polls were typed into
// the Family Name box the game had just opened). Held engine sends are dropped,
// not queued, so the hold is capped: a longer one would drop heals and attacks.
// A trainer screen within LateFormWindow after the cap takes the keyboard back.
public sealed class TrainerMenuTracker : IDisposable
{
    private readonly PartyState _party;
    private readonly LogService? _log;
    private readonly IDisposable _markerSub;
    private readonly IDisposable _promptSub;
    private readonly IDisposable _roomSub;
    private readonly IDisposable[] _refusalSubs;
    private bool _disposed;

    // Window after observing outbound train stats during which a marker line
    // confirms entry.
    public TimeSpan ExpectingMenuWindow { get; set; } = TimeSpan.FromSeconds(5);

    // The longest the keyboard is held for the answer to `train stats` with no
    // trainer screen seen.
    public TimeSpan HoldCap { get; set; } = TimeSpan.FromSeconds(10);

    // After the cap, how long a trainer screen still takes the keyboard back.
    public TimeSpan LateFormWindow { get; set; } = TimeSpan.FromSeconds(10);

    private readonly Func<DateTime> _now;

    // The framed "Point Cost Chart" panel beside the creation/train box title.
    // Public so the screen-feed path (MainWindowViewModel) can cheaply pre-check
    // for it before building screen text to hand to ObserveScreen. The emitted-
    // line path matches the same text via KnownPatterns.MenuTrainerStatsMarker.
    public const string StatBoxMarker = "Point Cost Chart";

    private DateTime? _expectingMenuSince;
    private bool _inMenu;
    private List<string> _rosterSnapshot = new();
    private bool _ledAtMenuEntry;

    // Input-mode state, driven by the outbound `train stats` command rather
    // than the marker (see InputMenuEntered).
    private bool _inputMenuActive;

    // How far the game has got answering the last `train stats`. The game echoes
    // each command on its prompt row before its result (GAME_MECHANICS "Movement
    // on the wire — command echo, failure lines, and what actually moves you"):
    // Echoed only says how far a log or report got, it releases nothing.
    private enum FormAnswer { None, Pending, Echoed, Form }
    private FormAnswer _answer;
    private DateTime _trainStatsSentAt;
    private readonly MessageRouter _router;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private IDisposable? _capTick;
    private IDisposable? _lateTick;
    private bool _awaitingLateForm;
    // What the screen held when `train stats` went out: a trainer screen left on it
    // from before (the `Char. Creation` box lingers after exit), the form as it
    // read then, and how many times the screen had been cleared. A trainer screen
    // counts only once the screen has been cleared since, or when none was on it
    // then, or when the form reads with other figures than it did (a box redrawn
    // in place over the old one), so a leftover box is neither the answer nor a
    // form to plan from.
    private bool _leftoverAtSend;
    private TrainStatsScreen? _leftoverForm;
    private long _clearsAtSend;

    // True while we believe the trainer-stats menu is the active screen
    // (marker-confirmed — never sets on a cursor-positioned box whose marker
    // row doesn't complete as a line; use IsInputMenuActive for the realm-independent read).
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
    // a cursor-positioned box whose "Point Cost Chart" row doesn't complete as a
    // line, that flag never confirms — yet the screen owns the keyboard, and the
    // box's FIRST field is the Family Name, so a stray automated `par\r` types
    // into it and overwrites the character's last name. IsInputMenuActive
    // carries that case; this property folds both in.
    public bool MenuOwnsKeyboard => _inputMenuActive || _inMenu;

    // The form answering the last `train stats` is up: a trainer screen drawn
    // since the command was on the terminal at the end of a read. Short of this
    // nothing may be typed into it.
    public bool FormShown => _inputMenuActive && _answer == FormAnswer.Form;

    // Fires when FormShown turns true, a keyboard taken back included.
    public event Action? FormDrawn;

    // The keyboard was let go at HoldCap with no trainer screen, and one may still
    // take it back. An AutoTrainManager pass waits on through it.
    public bool AwaitingLateForm => _awaitingLateForm;

    // Fires when a `train stats` ends with no trainer screen after the keyboard was
    // let go at the cap: LateFormWindow ran out, or ForceExit cut it short.
    public event Action? LateFormWindowClosed;

    // Whether the screen has been drawn afresh since the last `train stats`.
    public bool ScreenDrawnSinceTrainStats =>
        !_leftoverAtSend
        || (ScreenClears?.Invoke() ?? 0) > _clearsAtSend
        || (TrainStatsScreen.TryRead(ReadScreen?.Invoke()) is { } now && !SameForm(now, _leftoverForm));

    private static bool SameForm(TrainStatsScreen now, TrainStatsScreen? before) =>
        before is not null && now.CpLeft == before.CpLeft && now.Stats.SequenceEqual(before.Stats);

    // The visible screen as newline-joined rows, bound by the terminal session.
    // The form is told by what it draws there, not by a line, so neither a box
    // whose marker row never completes nor a chat line naming the panel misleads.
    public Func<string>? ReadScreen { get; set; }

    // How many times the terminal's whole screen has been cleared, bound by the
    // terminal session (TerminalEmulator.ScreenClears).
    public Func<long>? ScreenClears { get; set; }

    // The keyboard state, in words for a bug report: what holds it, for how long,
    // and whether a trainer screen on the terminal is left from before.
    public string Describe()
    {
        double held = (_now() - _trainStatsSentAt).TotalSeconds;
        if (!MenuOwnsKeyboard)
            return _awaitingLateForm
                ? $"off (no trainer screen {HoldCap.TotalSeconds:0} s after `train stats`; one until "
                  + $"{(HoldCap + LateFormWindow).TotalSeconds:0} s takes the keyboard back; {held:0.0} s since)"
                : "off";
        if (!_inputMenuActive || _answer == FormAnswer.None) return "on (trainer/creation box)";
        string why = _answer switch
        {
            FormAnswer.Pending => "waiting for the game's answer to `train stats`",
            FormAnswer.Echoed => "`train stats` echoed, waiting for the trainer screen",
            _ => "the trainer screen is up",
        };
        string leftover = _leftoverAtSend && !ScreenDrawnSinceTrainStats
            ? "; a trainer screen left from before is on the terminal and doesn't count"
            : string.Empty;
        return $"on ({why}; held {held:0.0} s{leftover})";
    }

    // Snapshot of non-self party member names taken at the moment we confirmed
    // menu entry. Subscribers to MenuExited inspect this to decide who to
    // re-invite.
    public IReadOnlyList<string> RosterAtMenuEntry => _rosterSnapshot;

    // Whether we led that party: by the menu's exit the server's party snapshot
    // may already have dissolved it, so it's read at entry.
    public bool LedAtMenuEntry => _ledAtMenuEntry;

    // Fires once when the trainer-stats marker confirms entry into the
    // full-screen menu. Lets subscribers (e.g. the terminal's character-mode
    // input switch) react to the excursion start.
    public event Action? MenuEntered;

    // Fires once when the in-game prompt returns after an armed menu session —
    // the user has exited the trainer screen.
    public event Action? MenuExited;

    // Input character-mode switch, driven by the outbound `train stats`
    // command rather than the marker. A full-screen box drawn with cursor
    // positioning may not complete its "Point Cost Chart" row as a line until
    // it's torn down, so a marker-gated switch could leave arrow keys captured
    // by history-recall for the whole session. Forms whose marker row completes
    // as it is drawn still work, but the command is the realm-independent
    // signal. Kept separate from MenuEntered/MenuExited so the
    // party re-invite flow stays marker-confirmed (no false-positive invites).
    public event Action? InputMenuEntered;

    // Fires when the in-game prompt returns after the `train stats` screen —
    // the screen has closed, so line-mode input should resume.
    public event Action? InputMenuExited;

    // schedule runs the cap and late-form ticks (DispatcherTicks.Once live). Every
    // member is called on the UI thread: MainWindowViewModel posts ObserveOutbound
    // there for a send made off it.
    public TrainerMenuTracker(MessageRouter router, PartyState party, LogService? log = null, Func<DateTime>? now = null,
                              Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        _schedule = schedule ?? DispatcherTicks.Once;
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(party);
        _party = party;
        _log   = log;
        _router = router;
        _markerSub = router.Subscribe(KnownPatterns.MenuTrainerStatsMarker, OnMenuMarker);
        _promptSub = router.Subscribe(KnownPatterns.StatusLine,             OnPrompt);
        _roomSub   = router.Subscribe(KnownPatterns.RoomExits,              OnRoomDisplay);
        _refusalSubs = new[]
        {
            router.Subscribe(KnownPatterns.TrainStatsAltered,    OnRefusal),
            router.Subscribe(KnownPatterns.TrainStatsNotHere,    OnRefusal),
            router.Subscribe(KnownPatterns.TrainStatsTournament, OnRefusal),
        };
        router.LineDispatched += OnLineDispatched;
        router.ReadEnded += OnReadEnded;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _markerSub.Dispose();
        _promptSub.Dispose();
        _roomSub.Dispose();
        foreach (IDisposable sub in _refusalSubs) sub.Dispose();
        _router.LineDispatched -= OnLineDispatched;
        _router.ReadEnded -= OnReadEnded;
        _capTick?.Dispose();
        _lateTick?.Dispose();
    }

    // Whether a command line is `train` (false) or `train stats` (true), as the game
    // spells them: `trai` is its shortest `train`, and `st` its shortest `stats`
    // (GAME_MECHANICS "Command words and abbreviations"); null for anything else.
    // Typed `trai stats` drew the form with the keyboard still in line mode, so its
    // arrows recalled history and Space went into the line (report
    // stock-20261029-170825).
    private static bool? TrainWords(string line) =>
        line.Split(' ', StringSplitOptions.RemoveEmptyEntries) switch
        {
            [var verb] when verb.ToLowerInvariant() is "trai" or "train" => false,
            [var verb, var what] when verb.ToLowerInvariant() is "trai" or "train"
                && what.ToLowerInvariant() is "st" or "sta" or "stat" or "stats" => true,
            _ => null,
        };

    // Called by the wire-send path so we can spot the user's own outbound
    // train stats / train command. This is the gate that prevents chat-noise
    // false positives — without an outbound train command the menu-marker
    // handler is a no-op.
    public void ObserveOutbound(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > 32) return;
        string raw = Encoding.Latin1.GetString(bytes);
        string cmd = raw.TrimEnd('\r', '\n', '\0').Trim().ToLowerInvariant();
        if (TrainWords(cmd) is not { } trainStats) return;
        _expectingMenuSince = _now();
        _log?.Log(LogSeverity.Info, "TrainerMenu",
            $"Observed outbound `{cmd}` — armed menu-marker watch for {ExpectingMenuWindow.TotalSeconds:0} s.");

        // The full-screen stat box is driven by the `train stats` command, not
        // its marker row (which a cursor-positioned box may only complete on
        // teardown — too late). Arm character-mode input off the command itself
        // so arrow keys reach the wire immediately, realm-independently. Bare
        // `train` opens a different (line-driven) prompt, so it doesn't arm input.
        if (trainStats)
        {
            // A form already drawn stays drawn: a second `train stats` goes into it.
            if (_answer != FormAnswer.Form)
            {
                EndLateFormWait();
                _answer = FormAnswer.Pending;
                _trainStatsSentAt = _now();
                _clearsAtSend = ScreenClears?.Invoke() ?? 0;
                string? screen = ReadScreen?.Invoke();
                _leftoverAtSend = TrainerScreenIn(screen) is not null;
                _leftoverForm = _leftoverAtSend ? TrainStatsScreen.TryRead(screen) : null;
                _capTick?.Dispose();
                _capTick = _schedule(HoldCap, OnHoldCap);
            }
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
    // feed flips character-mode input the moment the box appears. Scoped to that
    // kind of box (TrainStatsScreen.Kind.CharCreationBox, its "Character Creation"
    // / "Char. Creation" title): any other trainer screen arms off the outbound
    // `train stats` only, and an in-game `train stats` that draws this box is
    // already armed by its command, so this can't false-positive on chat noise.
    // Fed the flattened visible screen by the terminal-feed path, which
    // pre-filters on StatBoxMarker so this runs only when a stat box is actually
    // on screen.
    public void ObserveScreen(string screenText)
    {
        if (_inputMenuActive) return;   // already armed — nothing to do
        // A box after the cap let the keyboard go answers that `train stats`, not
        // character creation. This runs in the same screen update as OnReadEnded,
        // in whichever order the two were subscribed, so it takes the keyboard back
        // here too rather than arming it as a creation box with no answer to fill.
        if (_awaitingLateForm)
        {
            if (FreshTrainerScreen() is { } late) TakeBack(late);
            return;
        }
        if (string.IsNullOrEmpty(screenText)) return;
        if (!screenText.Contains(StatBoxMarker, StringComparison.OrdinalIgnoreCase)) return;

        if (TrainStatsScreen.Recognise(screenText) != TrainStatsScreen.Kind.CharCreationBox) return;

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
        // Whether the form answering `train stats` is up is the screen's to say
        // (OnReadEnded), not this line's.
        bool gateArmed = false;
        if (_expectingMenuSince is { } armedAt)
        {
            if (_now() - armedAt <= ExpectingMenuWindow)
                gateArmed = true;
            else
                _expectingMenuSince = null; // window lapsed — disarm
        }

        bool characterCreation = TrainStatsScreen.Recognise(match.Text) == TrainStatsScreen.Kind.CharCreationBox;

        if (!gateArmed && !characterCreation) return;
        if (_inMenu) return;
        _inMenu = true;
        _rosterSnapshot = _party.Members
            .Where(m => !m.IsSelf && !string.IsNullOrEmpty(m.Name))
            .Select(m => m.Name)
            .ToList();
        _ledAtMenuEntry = _party.IsInParty && _party.SelfIsLeader;
        _log?.Log(LogSeverity.Info, "TrainerMenu",
            $"Entered trainer menu ({(gateArmed ? "train-stats gate" : "character creation")}) — "
            + $"snapshot {_rosterSnapshot.Count} non-self member(s).");
        MenuEntered?.Invoke();
    }

    private void OnPrompt(MatchResult _)
    {
        // Input-mode teardown runs independently of the marker-confirmed party
        // flow below. Prompts before the trainer screen, the command's own
        // `[HP=...]:train stats` echo among them, are held; the next one after the
        // form is the bare prompt that returns when the user leaves the stat box,
        // so line-mode resumes.
        //
        // BOTH keyboard-owning flags are cleared BEFORE firing any exit event.
        // InputMenuExited drives the auto-train loop-resume walk synchronously, and
        // TrainerScreenGate holds the engine send-gate while MenuOwnsKeyboard
        // (_inputMenuActive || _inMenu) is true. If _inMenu were still set when
        // InputMenuExited fired, the gate would stay held and silently drop the
        // resume's first move byte with no retry — the walker draws the route back
        // but never steps (report paradigm-20260813-063517).
        if (HeldForTheAnswer("A prompt")) return;

        bool fireInputExited = _inputMenuActive;
        _inputMenuActive = false;
        ClearAnswer();

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
        if (HeldForTheAnswer("A room display")) return;
        ForceExit("room display observed — back in the world, exit prompt missed");
    }

    // The game's echo of `train stats` on its prompt row, logged for the record of
    // how far the answer got.
    private void OnLineDispatched(LineExtractor.EmittedLine line)
    {
        if (_answer != FormAnswer.Pending || !_inputMenuActive || !_router.LineIsCommandEcho) return;
        if (TrainWords(line.Text.Trim()) != true) return;
        _answer = FormAnswer.Echoed;
        _log?.Info("TrainerMenu", "The game echoed `train stats` — waiting for the trainer screen.");
    }

    // The game's own word that the form isn't coming.
    private void OnRefusal(MatchResult match)
    {
        if (!_inputMenuActive || _answer is not (FormAnswer.Pending or FormAnswer.Echoed)) return;
        ForceExit($"the game refused `train stats`: \"{match.Text.Trim()}\"");
    }

    private void OnReadEnded()
    {
        if (_inputMenuActive && _answer is FormAnswer.Pending or FormAnswer.Echoed)
        {
            if (FreshTrainerScreen() is { } shown) MarkFormShown(shown);
        }
        else if (_awaitingLateForm && !_inputMenuActive && FreshTrainerScreen() is { } late)
        {
            TakeBack(late);
        }
    }

    private void OnHoldCap()
    {
        _capTick = null;
        if (!_inputMenuActive || _answer is not (FormAnswer.Pending or FormAnswer.Echoed)) return;
        if (FreshTrainerScreen() is { } shown)
        {
            MarkFormShown(shown);
            return;
        }
        _log?.Warn("TrainerMenu",
            $"No trainer screen {HoldCap.TotalSeconds:0} s after `train stats` ({_answer}) — letting the keyboard go; "
            + $"one within {LateFormWindow.TotalSeconds:0} s more takes it back.");
        // Set ahead of the release, which an AutoTrainManager pass reads from.
        _awaitingLateForm = true;
        _lateTick = _schedule(LateFormWindow, OnLateFormWindowOut);
        Release("no trainer screen within the hold cap");
    }

    private void OnLateFormWindowOut()
    {
        _lateTick = null;
        if (!_awaitingLateForm) return;
        _awaitingLateForm = false;
        _log?.Info("TrainerMenu", "No trainer screen came after `train stats` — given up on it.");
        LateFormWindowClosed?.Invoke();
    }

    private void EndLateFormWait()
    {
        _lateTick?.Dispose();
        _lateTick = null;
        _awaitingLateForm = false;
    }

    // The trainer screen came after the cap let the keyboard go: it owns the
    // keyboard again, and the pass that sent `train stats` fills it.
    private void TakeBack(string kind)
    {
        EndLateFormWait();
        _inputMenuActive = true;
        _answer = FormAnswer.Echoed;
        _log?.Warn("TrainerMenu", $"The trainer screen turned up after the keyboard was let go ({kind}) — taking it back.");
        InputMenuEntered?.Invoke();
        if (_inputMenuActive && _answer == FormAnswer.Echoed) MarkFormShown(kind);
    }

    // The kind of trainer screen in the screen text: a kind the game is known to
    // draw, titled on a row that isn't chat (TrainStatsScreen.Recognise; the panel
    // alone could be anyone's line), or a form that reads.
    private static string? TrainerScreenIn(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (TrainStatsScreen.Recognise(text) is { } kind && kind != TrainStatsScreen.Kind.PanelOnly) return kind.ToString();
        return TrainStatsScreen.TryRead(text) is not null ? "a form that reads" : null;
    }

    // A trainer screen on a screen drawn since the last `train stats`.
    private string? FreshTrainerScreen() =>
        ScreenDrawnSinceTrainStats ? TrainerScreenIn(ReadScreen?.Invoke()) : null;

    private void MarkFormShown(string kind)
    {
        _answer = FormAnswer.Form;
        _capTick?.Dispose();
        _capTick = null;
        _log?.Info("TrainerMenu", $"The trainer screen is up ({kind}) — it owns the keyboard until the game's prompt returns.");
        FormDrawn?.Invoke();
    }

    private void ClearAnswer()
    {
        _answer = FormAnswer.None;
        _capTick?.Dispose();
        _capTick = null;
    }

    // While `train stats` waits for its answer nothing but the trainer screen, the
    // game's refusal or the cap ends the hold; this signal is logged and held.
    private bool HeldForTheAnswer(string what)
    {
        if (!_inputMenuActive || _answer is not (FormAnswer.Pending or FormAnswer.Echoed)) return false;
        _log?.Info("TrainerMenu",
            $"{what} came before the trainer screen ({_answer}) — the keyboard stays held for the answer to `train stats`.");
        return true;
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
    // flow stays on the committed OnPrompt. Prompts before the trainer screen are
    // held, the command's own echo statline among them (the form emits no
    // statline, so a prompt after it is the exit). ForceExit tears both flags down
    // before firing, preserving the send-gate ordering the loop-resume depends on.
    public void NotifyLivePromptObserved()
    {
        if (!_inputMenuActive) return;
        if (HeldForTheAnswer("An in-place prompt")) return;
        ForceExit("in-place statline prompt observed — back in the world");
    }

    // Force the keyboard release on the caller's word: the user left the form, the
    // game refused `train stats`, the link dropped or came up again, the profile
    // changed, or the AutoTrainManager CP-replay ExitGrace fallback found the exit
    // prompt missed. A late trainer screen no longer takes the keyboard back after
    // it. Mirrors OnPrompt's teardown: clear BOTH keyboard-owning flags BEFORE
    // firing the exit events, so the event-driven TrainerScreenGate re-evaluates
    // with MenuOwnsKeyboard=false and releases the engine send-gate before the
    // loop-resume those events drive sends its first move. Without this, a missed
    // exit prompt leaves the gate held and the resumed loop's first move byte is
    // silently dropped — the walker draws the route but stands idle (report
    // paradigm-20260815-072308: 10 minutes idle after CP allocation). No-op when
    // nothing is held.
    public void ForceExit(string reason)
    {
        bool lateWaitCut = _awaitingLateForm;
        EndLateFormWait();
        _expectingMenuSince = null;
        Release(reason);
        if (lateWaitCut) LateFormWindowClosed?.Invoke();
    }

    private void Release(string reason)
    {
        if (!MenuOwnsKeyboard) return;
        _log?.Log(LogSeverity.Info, "TrainerMenu", $"Forcing trainer keyboard release ({reason}).");
        bool fireInputExited = _inputMenuActive;
        bool fireMenuExited = _inMenu;
        _inputMenuActive = false;
        _inMenu = false;
        _expectingMenuSince = null;
        ClearAnswer();
        if (fireInputExited) InputMenuExited?.Invoke();
        if (fireMenuExited) MenuExited?.Invoke();
    }
}
