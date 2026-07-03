using FujinTerm.Game;
using Xunit;

namespace FujinTerm.Tests;

public sealed class RealmRegenProfileTests
{
    [Fact]
    public void Stock_MatchesMmudExplorerCadence()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), RealmRegenProfile.Stock.StandingInterval);
        Assert.Equal(TimeSpan.FromSeconds(20), RealmRegenProfile.Stock.RestingInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), RealmRegenProfile.Stock.MeditatingInterval);
    }

    [Fact]
    public void ParaMud_UsesTheMeasuredTenSecondGrid()
    {
        // Derived from live Paradigm captures: natural +rate/3 every 10 s, rest
        // riding the same grid. Meditate isn't split — same 10 s as Stock.
        Assert.Equal(TimeSpan.FromSeconds(10), RealmRegenProfile.ParaMud.StandingInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), RealmRegenProfile.ParaMud.RestingInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), RealmRegenProfile.ParaMud.MeditatingInterval);
    }

    [Theory]
    [InlineData(RealmType.Stock)]
    [InlineData(RealmType.ParaMud)]
    public void For_SelectsTheMatchingProfile(RealmType realm)
    {
        RealmRegenProfile expected = realm == RealmType.ParaMud
            ? RealmRegenProfile.ParaMud
            : RealmRegenProfile.Stock;
        Assert.Equal(expected, RealmRegenProfile.For(realm));
    }
}
