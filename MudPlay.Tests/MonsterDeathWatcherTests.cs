using MudPlay.Game.Combat;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// MonsterDeathWatcher recognizes deaths generically from the exp signal: a kill's
// "You gain N experience." followed by *Combat Off* within a window. Per-monster
// DeathLine matching was retired — death messages are arbitrary flavor with no shared
// keyword/colour, so the exp line is the only reliable generic signal (our own
// targeting names the mob).
public sealed class MonsterDeathWatcherTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public MonsterDeathWatcher Watcher { get; }
        // The clock the Watcher reads: the real one unless a test sets its own.
        public Func<DateTimeOffset> WatcherNow { get; set; } = () => DateTimeOffset.UtcNow;
        public List<MonsterDeathEvent> Events { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Watcher = new MonsterDeathWatcher(Router, Log, now: () => WatcherNow());
            Watcher.MonsterDied += Events.Add;
        }

        public void FeedRouter(string line)
        {
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
        }

        // A command as the server echoes it: the prompt row, then its trailing
        // text as a second line carrying the same timestamp.
        public void FeedEchoed(string command)
        {
            DateTimeOffset at = DateTimeOffset.UtcNow;
            Router.Dispatch(new LineExtractor.EmittedLine(
                "[HP=724/MA=343]:", Array.Empty<CellAttributes>(), at, IsPromptLine: true));
            Router.Dispatch(new LineExtractor.EmittedLine(
                command, Array.Empty<CellAttributes>(), at, IsPromptLine: false));
        }

        // A prompt row with nothing after it, stamped apart from whatever is fed
        // next so that line can't read as its echo.
        public void FeedBarePrompt()
        {
            Router.Dispatch(new LineExtractor.EmittedLine(
                "[HP=724/MA=343]:", Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow.AddSeconds(-1), IsPromptLine: true));
        }

        public void Dispose() => Watcher.Dispose();
    }

    [Fact]
    public void ExpThenCombatOff_FiresDeath()
    {
        using Harness h = new();

        h.FeedRouter("You gain 9 experience.");
        h.FeedRouter("*Combat Off*");

        Assert.Single(h.Events);
        MonsterDeathEvent evt = h.Events[0];
        Assert.Equal(9, evt.ExperienceGained);
    }

    [Fact]
    public void CombatOffWithoutRecentExp_DoesNotFire()
    {
        using Harness h = new();

        h.FeedRouter("*Combat Off*");

        Assert.Empty(h.Events);
    }

    [Fact]
    public void DoesNotDoubleFire_OnSecondCombatOff()
    {
        // The exp is consumed on the first *Combat Off*, so a later non-death
        // *Combat Off* (a thrown-weapon bounce, a mid-spell interrupt) can't re-fire
        // a phantom death on the stale exp.
        using Harness h = new();

        h.FeedRouter("You gain 9 experience.");
        h.FeedRouter("*Combat Off*");
        Assert.Single(h.Events);

        h.FeedRouter("*Combat Off*");
        Assert.Single(h.Events);
    }

    [Fact]
    public void CombatOffOutsideExpWindow_DoesNotFire()
    {
        // A *Combat Off* more than the 5s window after the exp isn't that kill's Off.
        using Harness h = new();
        DateTimeOffset clock = DateTimeOffset.UnixEpoch;
        h.WatcherNow = () => clock;

        h.FeedRouter("You gain 9 experience.");
        clock += TimeSpan.FromSeconds(6);
        h.FeedRouter("*Combat Off*");

        Assert.Empty(h.Events);
    }

    // Report paradigm-20261010-145330: a room spell killed three of four and printed no
    // *Combat Off* (the survivor kept it running). The attack then sent at the survivor
    // printed one ahead of its *Combat Engaged*, inside the window the exp lines had
    // opened, and was read as a fourth death.
    [Fact]
    public void CombatOffAnsweringAnEchoedAttack_IsNotADeath()
    {
        using Harness h = new();

        h.FeedRouter("You gain 16250 experience.");
        h.FeedRouter("You gain 16250 experience.");
        h.FeedRouter("You gain 16250 experience.");
        h.FeedEchoed("aslt brute zombie");
        h.FeedRouter("*Combat Off*");
        h.FeedRouter("*Combat Engaged*");

        Assert.Empty(h.Events);

        // The exp lines are spent: the buff cast next prints an Off of its own.
        h.FeedRouter("*Combat Off*");
        Assert.Empty(h.Events);
    }

    [Fact]
    public void CombatOffAnsweringATypedBreak_IsNotADeath()
    {
        using Harness h = new();

        h.FeedRouter("You gain 9 experience.");
        h.FeedEchoed("break");
        h.FeedRouter("*Combat Off*");

        Assert.Empty(h.Events);
    }

    // The kill's own Off follows its exp line, with the command that led to it further
    // back, so it is still a death.
    [Fact]
    public void KillAfterAnEchoedAttack_StillFiresDeath()
    {
        using Harness h = new();

        h.FeedEchoed("aslt brute zombie");
        h.FeedRouter("*Combat Engaged*");
        h.FeedRouter("The brute zombie keels over like a hewn tree!");
        h.FeedRouter("You gain 16250 experience.");
        h.FeedRouter("*Combat Off*");

        Assert.Single(h.Events);
    }

    // Text the game appends to a prompt row without clearing it reads as an echo. An
    // exp line that arrived that way must not turn its own Off into a command's.
    [Fact]
    public void ExpLineOnAPromptRow_ThenCombatOff_StillFiresDeath()
    {
        using Harness h = new();

        h.FeedEchoed("You gain 9 experience.");
        h.FeedRouter("*Combat Off*");

        Assert.Single(h.Events);
    }

    // A statline the extractor can't split arrives as one line, prompt and command
    // together, so no echo is read and the Off keeps its old reading.
    [Fact]
    public void CommandOnAnUnsplitStatline_KeepsTheOldReading()
    {
        using Harness h = new();

        h.FeedRouter("You gain 9 experience.");
        h.FeedRouter("<724hp 343ma> aslt brute zombie");
        h.FeedRouter("*Combat Off*");

        Assert.Single(h.Events);
    }

    // The prompt redrawn between a command's echo and its Off doesn't end the echo.
    [Fact]
    public void CombatOffAfterAnEcho_PromptRedrawnBetween_IsNotADeath()
    {
        using Harness h = new();

        h.FeedRouter("You gain 9 experience.");
        h.FeedEchoed("break");
        h.FeedBarePrompt();
        h.FeedRouter("*Combat Off*");

        Assert.Empty(h.Events);
    }

    // A command typed ahead is echoed at once and answered after the lines of the
    // round it waited for, so other lines sit between its echo and its Off.
    [Fact]
    public void CombatOffAfterAnEcho_RoundLinesBetween_IsNotADeath()
    {
        using Harness h = new();

        h.FeedRouter("You gain 9 experience.");
        h.FeedEchoed("break");
        h.FeedRouter("The brute zombie swings at you with its arm!");
        h.FeedRouter("The brute zombie swings at you, but you dodge out of the way!");
        h.FeedRouter("*Combat Off*");

        Assert.Empty(h.Events);
    }

    // The same typed-ahead command, but the round it waited for kills: the kill's
    // Off follows its own exp line and is a death, whatever was echoed before it.
    [Fact]
    public void KillInTheRoundATypedAheadCommandWaitedFor_StillFiresDeath()
    {
        using Harness h = new();

        h.FeedRouter("You gain 9 experience.");
        h.FeedEchoed("break");
        h.FeedRouter("The brute zombie swings at you with its arm!");
        h.FeedRouter("The brute zombie keels over like a hewn tree!");
        h.FeedRouter("You gain 16250 experience.");
        h.FeedBarePrompt();
        h.FeedRouter("*Combat Off*");

        Assert.Single(h.Events);
        Assert.Equal(16250, h.Events[0].ExperienceGained);
    }

    // A *Combat Engaged* the game left on a prompt row is the game's line, not a
    // command, so the Off after it keeps the old reading.
    [Fact]
    public void CombatStatusOnAPromptRow_IsNotACommand()
    {
        using Harness h = new();

        h.FeedRouter("You gain 9 experience.");
        h.FeedRouter("You hear movement to the east.");
        h.FeedEchoed("*Combat Engaged*");
        h.FeedRouter("*Combat Off*");

        Assert.Single(h.Events);
    }

    // A room spell's kill is raised on its exp line by the combat manager, from its
    // own handler of that line. The exp is spent there, so the *Combat Off* the game
    // prints after it (Stock after each kill, Paradigm after the last of a room)
    // counts nothing more. That must hold whichever of the two subscribed first: here
    // the counting handler is registered ahead of the watcher.
    [Fact]
    public void KillCountedOnItsExpLine_IsNotCountedAgainOnItsCombatOff_WhoeverSubscribedFirst()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        MonsterDeathWatcher? watcher = null;
        List<MonsterDeathEvent> events = new();
        using IDisposable counter = router.Subscribe(KnownPatterns.UserGainExperience, m =>
            watcher!.NoteRoomSpellKill(int.Parse(m.Groups[0]),
                new[] { new MonsterDeathIdentity(1, "brute zombie") }));
        using MonsterDeathWatcher late = watcher = new MonsterDeathWatcher(router, new LogService());
        watcher.MonsterDied += events.Add;
        void Feed(string line) => router.Dispatch(new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        Feed("You gain 16250 experience.");
        Feed("*Combat Off*");
        Feed("You gain 16250 experience.");
        Feed("*Combat Off*");

        Assert.Equal(2, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal(16250, e.ExperienceGained);
            Assert.Equal("brute zombie", Assert.Single(e.RoomSpellRoster!).Name);
        });
    }

    // Not closed: an echo the game printed on a row of its own, away from the prompt,
    // is not known for an echo, so the Off after it keeps the old reading.
    [Fact]
    public void EchoOnARowOfItsOwn_KeepsTheOldReading()
    {
        using Harness h = new();

        h.FeedRouter("You gain 9 experience.");
        h.FeedBarePrompt();
        h.FeedRouter("break");
        h.FeedRouter("*Combat Off*");

        Assert.Single(h.Events);
    }
}
