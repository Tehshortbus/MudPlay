using FujinTerm.Game;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

public sealed class TickEngineTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private static (MessageRouter router, TickEngine tick) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        TickEngine tick = new(router);
        return (router, tick);
    }

    [Fact]
    public void UserHitsDamageLine_FiresCombatTickAndStampsTimestamp()
    {
        var (router, tick) = Setup();
        int fires = 0;
        tick.CombatTickElapsed += () => fires++;

        Assert.Null(tick.LastCombatTick);
        router.Dispatch(Line("Forged slashes Goblin for 17 damage!"));

        Assert.Equal(1, fires);
        Assert.NotNull(tick.LastCombatTick);
        tick.Dispose();
    }

    [Fact]
    public void MobHitsDamageLine_AlsoFiresCombatTick()
    {
        var (router, tick) = Setup();
        int fires = 0;
        tick.CombatTickElapsed += () => fires++;

        router.Dispatch(Line("The Goblin slashes you for 4 damage!"));

        Assert.Equal(1, fires);
        tick.Dispose();
    }

    [Fact]
    public void NonDamageLines_DontFireCombatTick()
    {
        var (router, tick) = Setup();
        int fires = 0;
        tick.CombatTickElapsed += () => fires++;

        router.Dispatch(Line("Forged gossips: hi"));
        router.Dispatch(Line("Obvious exits: north, south"));

        Assert.Equal(0, fires);
        Assert.Null(tick.LastCombatTick);
        tick.Dispose();
    }

    [Fact]
    public void CombatTickInterval_IsFiveSeconds()
    {
        // Pinning the universal MajorMUD combat-tick value so a stray
        // refactor doesn't silently change it. The spec is explicit:
        // 5 s is invariant across realm flavours.
        Assert.Equal(TimeSpan.FromSeconds(5), TickEngine.CombatTickInterval);
    }

    [Fact]
    public void RegenIntervals_DefaultToZero_DisablingRegenEvents()
    {
        var (_, tick) = Setup();
        Assert.Equal(TimeSpan.Zero, tick.HpRegenInterval);
        Assert.Equal(TimeSpan.Zero, tick.ManaRegenInterval);
        Assert.Null(tick.TimeToNextHpRegenTick);
        Assert.Null(tick.TimeToNextManaRegenTick);
        tick.Dispose();
    }

    [Fact]
    public void Dispose_StopsCombatTickEvents()
    {
        var (router, tick) = Setup();
        int fires = 0;
        tick.CombatTickElapsed += () => fires++;

        router.Dispatch(Line("Forged slashes Goblin for 17 damage!"));
        Assert.Equal(1, fires);

        tick.Dispose();
        router.Dispatch(Line("Forged slashes Goblin for 17 damage!"));
        Assert.Equal(1, fires);
    }
}
