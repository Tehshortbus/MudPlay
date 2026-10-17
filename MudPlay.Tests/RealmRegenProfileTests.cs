using MudPlay.Game;
using Xunit;

namespace MudPlay.Tests;

public sealed class RealmRegenProfileTests
{

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
