using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;

namespace MudPlay.Game;

// Knows whether the character is inside the game, for the two things that depend
// on it.
//
// A bug report copies the terminal only from the time spent in the game. A report
// carries the backscroll, and one made at login would otherwise carry the board's
// login screen with it: the account name as typed, and whatever the board shows
// around the password. The terminal, the Backscroll window and the Wire Inspector
// still show all of it; only what a report copies out of them is held to these
// stretches (user, 2026-10-08).
//
// And the client stands down at the board's menu. A character that typed `exit` is
// on the board's menu with the link still up, and every engine went on sending as if
// it were in the game: the party poll, a buff, `sn`, and `set statline full` each
// landed on the menu as a selection (report paradigm-20261008-125639). AtBoardMenu
// says so, and AppServices holds the engines on it.
//
// In: the first statline prompt (the same signal every other "are we in the game"
// latch uses). It is seen before the terminal draws the bytes that carried it, so
// the room the character lands in counts as in the game with it.
// Out: the link dropping, a new link starting, the realm's "Your character has been
// saved" on exit, or the board's main menu. The last two are read out of the chunk
// that carries them; the stretch ends where that chunk began, so the menu drawn
// with it isn't copied either. A chat line shaped like one of them can only cost the
// few rows before the next prompt opens it again.
public sealed class InGameCapture : IDisposable
{
    private const string LogCategory = "Capture";

    private readonly WireBuffer _wire;
    private readonly WirePromptScanner _prompts;
    private readonly LogService? _log;
    private readonly IDisposable _savedSub;
    private readonly IDisposable _menuSub;

    public InGameCapture(MessageRouter router, WirePromptScanner prompts, WireBuffer wire, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(wire);
        _prompts = prompts;
        _wire = wire;
        _log = log;
        _prompts.PromptObserved += OnPrompt;
        _savedSub = router.Subscribe(KnownPatterns.RealmExitSaved, _ => LeaveForMenus("the character was saved on exit"));
        _menuSub = router.Subscribe(KnownPatterns.MainMenuEnterRealm, _ => LeaveForMenus("the board's main menu is showing"));
    }

    // The stretches spent in the game; a transcript row counts when it was
    // written inside one (TranscriptSnapshot.Tail's `only`).
    public CaptureWindow Window { get; } = new(on: false);

    public bool InGame => Window.IsOn;

    // InGame flipped.
    public event Action<bool>? InGameChanged;

    // The character left the game for the board's menus and the link is still up:
    // whatever is sent now is a menu selection. False at a fresh login, where the
    // login and entry automation have the menus to themselves.
    public bool AtBoardMenu { get; private set; }

    // AtBoardMenu flipped.
    public event Action<bool>? AtBoardMenuChanged;

    // When the chunk now being drawn arrived (the screen's feed stamp), so a
    // stretch can end where the chunk that carried the menu began. Unset, it falls
    // back to the clock.
    public Func<DateTimeOffset>? FeedTime { get; set; }

    // How much of the wire had been read when the game was last entered; null
    // while out of it. A report's raw wire section starts from here.
    public long? WireMark { get; private set; }

    public void NotifyConnected()
    {
        ConnectedAt = DateTimeOffset.Now;
        LinkChanged("a new connection");
    }

    public void NotifyDisconnected()
    {
        ConnectedAt = null;
        LinkChanged("the link dropped");
    }

    // When the present link came up, and when the game was last entered on it;
    // null while there is none. A bug report reads both.
    public DateTimeOffset? ConnectedAt { get; private set; }
    public DateTimeOffset? InGameSince { get; private set; }

    private void OnPrompt(PromptObservation _)
    {
        SetAtBoardMenu(false);
        if (Window.IsOn) return;
        Window.Set(true, DateTimeOffset.Now);
        InGameSince = DateTimeOffset.Now;
        WireMark = _wire.TotalBytes;
        _log?.Info(LogCategory, "in the game: a bug report copies the terminal from here");
        InGameChanged?.Invoke(true);
    }

    private void LeaveForMenus(string why)
    {
        if (!Window.IsOn) return;
        Close(FeedTime?.Invoke() ?? DateTimeOffset.Now, why);
        SetAtBoardMenu(true);
    }

    // With the link gone there is no menu to protect, and a new link starts at the
    // login, which the login automation has to be able to drive.
    private void LinkChanged(string why)
    {
        SetAtBoardMenu(false);
        if (Window.IsOn) Close(DateTimeOffset.Now, why);
    }

    private void Close(DateTimeOffset at, string why)
    {
        Window.Set(false, at);
        InGameSince = null;
        WireMark = null;
        _log?.Info(LogCategory, $"out of the game ({why}): a bug report copies nothing from here until the next game prompt");
        InGameChanged?.Invoke(false);
    }

    private void SetAtBoardMenu(bool at)
    {
        if (AtBoardMenu == at) return;
        AtBoardMenu = at;
        _log?.Info(LogCategory, at
            ? "at the board's menu: automatic commands are held until the game is entered again"
            : "automatic commands are no longer held for the board's menu");
        AtBoardMenuChanged?.Invoke(at);
    }

    public void Dispose()
    {
        _prompts.PromptObserved -= OnPrompt;
        _savedSub.Dispose();
        _menuSub.Dispose();
    }
}
