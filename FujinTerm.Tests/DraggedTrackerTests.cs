using FujinTerm.Game;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

public sealed class DraggedTrackerTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private static (MessageRouter router, PlayerState player, DraggedTracker tracker) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerState player = new();
        DraggedTracker tracker = new(router, player);
        return (router, player, tracker);
    }

    /// <summary>
    /// Drive the character mortally wounded — negative HP with real prompt
    /// data behind it, matching what PlayerState.IsMortallyWounded gates on.
    /// </summary>
    private static void Drop(PlayerState p, int hp = -4, int maxHp = 150)
    {
        p.MaxHp = maxHp;
        p.Hp = hp;
        p.HasPromptData = true;
    }

    [Fact]
    public void DragLine_WhileMortallyWounded_RecordsDragger()
    {
        var (router, player, tracker) = Setup();
        Drop(player);

        router.Dispatch(Line("Fujin is dragging you around."));

        Assert.Equal("Fujin", tracker.DraggedBy);
    }

    [Fact]
    public void DragLine_WhileHealthy_IsIgnored()
    {
        // A stray match on a standing character must not pin a phantom
        // dragger — only a mortally-wounded body can be dragged.
        var (router, player, tracker) = Setup();
        player.MaxHp = 150; player.Hp = 120; player.HasPromptData = true;

        router.Dispatch(Line("Fujin is dragging you around."));

        Assert.Null(tracker.DraggedBy);
    }

    [Fact]
    public void Recovery_ClearsDragger()
    {
        // HP back positive means we're standing and drag ourselves — the
        // dragger record clears the moment IsMortallyWounded goes false.
        var (router, player, tracker) = Setup();
        Drop(player);
        router.Dispatch(Line("Fujin is dragging you around."));
        Assert.Equal("Fujin", tracker.DraggedBy);

        player.Hp = 60;

        Assert.Null(tracker.DraggedBy);
    }

    [Fact]
    public void SecondDragger_Overwrites_WhenBodyChangesHands()
    {
        // A second party member takes over the drag — the latest dragger
        // wins (there's no "stopped dragging" line to reset between them).
        var (router, player, tracker) = Setup();
        Drop(player);
        router.Dispatch(Line("Fujin is dragging you around."));
        router.Dispatch(Line("Raijin is dragging you around."));

        Assert.Equal("Raijin", tracker.DraggedBy);
    }

    [Fact]
    public void Dispose_StopsTracking()
    {
        var (router, player, tracker) = Setup();
        Drop(player);
        tracker.Dispose();

        router.Dispatch(Line("Fujin is dragging you around."));

        Assert.Null(tracker.DraggedBy);
    }
}
