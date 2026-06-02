using System.Text;
using FujinTerm.Game;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the latch model. The entry command must NEVER fire on a
/// menu-shaped line unless the engine was just armed by the BBS-
/// login-automation completion event — otherwise a malicious player
/// could gossip "[E] . Enter the Realm" mid-game and trick the
/// client into re-entering when the player wanted to stay menu-side.
/// </summary>
public sealed class MainMenuEntryAutomationTests
{
    private static readonly DateTime Now = new(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc);

    private static (MainMenuEntryAutomation engine, MessageRouter router, GameCommands commands) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GameCommands commands = new();   // defaults: "E" / "=x"
        MainMenuEntryAutomation engine = new(router, commands)
        {
            NowProvider = () => Now,
        };
        engine.SetWireSender(_ => { });
        return (engine, router, commands);
    }

    private static void DispatchMenuLine(MessageRouter router)
    {
        LineExtractor.EmittedLine line = new(
            "[E] . Enter the Realm",
            new CellAttributes[20],
            DateTimeOffset.UnixEpoch,
            IsPromptLine: false);
        router.Dispatch(line);
    }

    [Fact]
    public void MenuLine_WhenNotArmed_DoesNothing()
    {
        // The default state — engine sitting idle, an in-game chat or
        // room description matching the pattern. Must not fire.
        var (engine, router, _) = Setup();
        DispatchMenuLine(router);
        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void Arm_ThenMenuLine_SendsEntryCommand()
    {
        var (engine, router, _) = Setup();
        engine.Arm();

        DispatchMenuLine(router);

        byte[] sent = Assert.Single(engine.LastSentForTests);
        Assert.Equal("E\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void Arm_ConsumesOnFirstFire_NoReFire()
    {
        // Latch must be one-shot — once the entry command goes out,
        // a subsequent menu line (in-game chat etc.) is ignored.
        var (engine, router, _) = Setup();
        engine.Arm();

        DispatchMenuLine(router);
        DispatchMenuLine(router);
        DispatchMenuLine(router);

        Assert.Single(engine.LastSentForTests);
    }

    [Fact]
    public void Arm_ExpiresAfterWindow_NoFire()
    {
        // Login completed but main menu never rendered (BBS hung,
        // dropped, whatever). After the arm window, an in-game
        // matching line must NOT fire.
        var (engine, router, _) = Setup();
        DateTime t0 = Now;
        engine.NowProvider = () => t0;
        engine.ArmWindow = TimeSpan.FromSeconds(15);
        engine.Arm();

        // Advance past the window.
        engine.NowProvider = () => t0.AddSeconds(16);
        DispatchMenuLine(router);

        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void Arm_BeforeWindowExpires_StillFires()
    {
        var (engine, router, _) = Setup();
        DateTime t0 = Now;
        engine.NowProvider = () => t0;
        engine.ArmWindow = TimeSpan.FromSeconds(15);
        engine.Arm();

        // Inside the window.
        engine.NowProvider = () => t0.AddSeconds(10);
        DispatchMenuLine(router);

        Assert.Single(engine.LastSentForTests);
    }

    [Fact]
    public void RespectsCustomEntryCommand()
    {
        var (engine, router, commands) = Setup();
        commands.EntryCommand = "enter";
        engine.Arm();

        DispatchMenuLine(router);

        Assert.Equal("enter\r", Encoding.Latin1.GetString(Assert.Single(engine.LastSentForTests)));
    }

    [Fact]
    public void BlankEntryCommand_SendsNothing()
    {
        // Defensive — a user-misconfigured blank entry command shouldn't
        // dump a lone CR on the wire when armed.
        var (engine, router, commands) = Setup();
        commands.EntryCommand = string.Empty;
        engine.Arm();

        DispatchMenuLine(router);

        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void IsArmed_ReflectsLatchState()
    {
        var (engine, _, _) = Setup();
        DateTime t0 = Now;
        engine.NowProvider = () => t0;

        Assert.False(engine.IsArmed);
        engine.Arm();
        Assert.True(engine.IsArmed);

        engine.NowProvider = () => t0.Add(engine.ArmWindow + TimeSpan.FromSeconds(1));
        Assert.False(engine.IsArmed);
    }

    [Fact]
    public void MenuLineWithVariableWhitespace_StillMatches()
    {
        // Some BBSes pad the brackets differently — regex should be
        // tolerant of the spacing variants.
        var (engine, router, _) = Setup();
        engine.Arm();
        LineExtractor.EmittedLine line = new(
            "[E]  .  Enter the Realm   (something extra)",
            new CellAttributes[40],
            DateTimeOffset.UnixEpoch,
            IsPromptLine: false);
        router.Dispatch(line);

        Assert.Single(engine.LastSentForTests);
    }

    [Fact]
    public void Dispose_StopsHearingPattern()
    {
        var (engine, router, _) = Setup();
        engine.Arm();
        engine.Dispose();

        DispatchMenuLine(router);

        Assert.Empty(engine.LastSentForTests);
    }
}
