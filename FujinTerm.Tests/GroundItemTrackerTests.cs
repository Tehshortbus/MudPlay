using System.Reflection;
using FujinTerm.Game.Inventory;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins <see cref="GroundItemTracker"/>: the "You notice &lt;list&gt; here."
/// survey becomes a per-room, cash-filtered snapshot of floor loot. A fresh
/// survey supersedes the prior list, a wrapped survey stitches across two
/// rows, and a room change clears the snapshot.
/// </summary>
public sealed class GroundItemTrackerTests
{
    private static (GroundItemTracker ground, MessageRouter router, LineExtractor lines) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router);
        LineExtractor lines = new(new TerminalEmulator(80, 24));
        ground.AttachLineExtractor(lines);
        return (ground, router, lines);
    }

    // Single-line survey path — through the router pattern subscription.
    private static void FeedRoom(MessageRouter router, string text) =>
        router.Dispatch(new LineExtractor.EmittedLine(
            text, Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false));

    // Multi-line stitch path — through the LineExtractor's emitted-line event.
    private static void FeedLine(LineExtractor lines, string text)
    {
        FieldInfo? field = typeof(LineExtractor).GetField(
            "LineEmitted", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(lines) is Action<LineExtractor.EmittedLine> handler)
        {
            handler(new LineExtractor.EmittedLine(
                text, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
        }
    }

    [Fact]
    public void EmptyBeforeSurvey()
    {
        var (ground, _, _) = Setup();
        Assert.Empty(ground.Items);
    }

    [Fact]
    public void SingleLineSurvey_KeepsItems_DropsCash()
    {
        var (ground, router, _) = Setup();

        FeedRoom(router, "You notice a rusty dagger, 5 gold crowns, "
                       + "a silver ring and 2 copper farthings here.");

        // "5 gold crowns" / "2 copper farthings" are cash — filtered out.
        Assert.Equal(new[] { "a rusty dagger", "a silver ring" }, ground.Items);
    }

    [Fact]
    public void SingularCashEntry_IsFiltered()
    {
        var (ground, router, _) = Setup();

        FeedRoom(router, "You notice a platinum piece and a torch here.");

        // "a platinum" is the singular cash form; "a torch" is a real item.
        Assert.Equal(new[] { "a torch" }, ground.Items);
    }

    [Fact]
    public void FreshSurvey_SupersedesPrior()
    {
        var (ground, router, _) = Setup();

        FeedRoom(router, "You notice a rusty dagger here.");
        FeedRoom(router, "You notice a healing potion and a shield here.");

        Assert.Equal(new[] { "a healing potion", "a shield" }, ground.Items);
    }

    [Fact]
    public void MultiLineWrap_StitchesAcrossRows()
    {
        var (ground, _, lines) = Setup();

        FeedLine(lines, "You notice a rusty dagger, a torch and a");
        FeedLine(lines, "healing potion here.");

        Assert.Equal(new[] { "a rusty dagger", "a torch", "a healing potion" }, ground.Items);
    }

    [Fact]
    public void RoomChanged_ClearsSnapshot()
    {
        var (ground, router, _) = Setup();
        FeedRoom(router, "You notice a rusty dagger here.");

        ground.OnRoomChanged();

        Assert.Empty(ground.Items);
    }

    [Fact]
    public void Disposed_StopsTracking()
    {
        var (ground, router, _) = Setup();
        ground.Dispose();

        FeedRoom(router, "You notice a rusty dagger here.");

        Assert.Empty(ground.Items);
    }
}
