using System;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 10.8 — the <see cref="KnownPatterns.TrainAttainLevel"/> pattern that
/// detects a <c>train</c> level-up ("…you receive training to attain level N.")
/// and captures the attained level for the auto-train coordinator.
/// </summary>
public sealed class TrainLinePatternTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private static MessageRouter SeededRouter()
    {
        var router = new MessageRouter();
        DefaultPatterns.Seed(router);
        return router;
    }

    [Fact]
    public void TrainAttainLevel_CapturesNewLevel()
    {
        MessageRouter router = SeededRouter();
        MatchResult? hit = null;
        router.Subscribe(KnownPatterns.TrainAttainLevel, m => hit = m);

        router.Dispatch(Line("You hand over 1 gold crown and you receive training to attain level 3."));

        Assert.NotNull(hit);
        Assert.Equal("3", hit.Value.Groups[0]);
    }

    [Fact]
    public void TrainAttainLevel_IgnoresUnrelatedLines()
    {
        MessageRouter router = SeededRouter();
        bool fired = false;
        router.Subscribe(KnownPatterns.TrainAttainLevel, _ => fired = true);

        router.Dispatch(Line("You don't have enough experience to train."));

        Assert.False(fired);
    }
}
