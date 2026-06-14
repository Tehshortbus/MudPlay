using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class NeedsRegistryTests
{
    [Fact]
    public void Post_NewNeed_FiresEventAndIsOutstanding()
    {
        NeedsRegistry r = new();
        Need? fired = null;
        r.NeedPosted += n => fired = n;

        Need posted = r.Post(NeedKind.LightSource, "illu>=42", "AutoLightManager");

        Assert.Equal(NeedKind.LightSource, posted.Kind);
        Assert.Equal("illu>=42", posted.Descriptor);
        Assert.Equal(posted, fired);
        Assert.Single(r.Outstanding(NeedKind.LightSource));
    }

    [Fact]
    public void Post_DuplicateKindDescriptor_DedupesAndDoesNotRefire()
    {
        NeedsRegistry r = new();
        int fires = 0;
        r.NeedPosted += _ => fires++;

        r.Post(NeedKind.LightSource, "illu>=42", "AutoLightManager");
        r.Post(NeedKind.LightSource, "illu>=42", "SomeoneElse");

        Assert.Equal(1, fires);
        Assert.Single(r.Outstanding(NeedKind.LightSource));
    }

    [Fact]
    public void TryClaim_HandsOutOnce_ThenNoSecondClaimant()
    {
        NeedsRegistry r = new();
        r.Post(NeedKind.LightSource, "illu>=42", "AutoLightManager");

        Assert.True(r.TryClaim(NeedKind.LightSource, "AutoGet", out Need claimed));
        Assert.Equal("illu>=42", claimed.Descriptor);
        // Claimed need still outstanding, but not re-claimable.
        Assert.Single(r.Outstanding(NeedKind.LightSource));
        Assert.False(r.TryClaim(NeedKind.LightSource, "AutoGet2", out _));
    }

    [Fact]
    public void TryClaim_NothingToClaim_ReturnsFalse()
    {
        NeedsRegistry r = new();
        Assert.False(r.TryClaim(NeedKind.LightSource, "AutoGet", out Need need));
        Assert.Equal(default, need);
    }

    [Fact]
    public void Resolve_RemovesNeed()
    {
        NeedsRegistry r = new();
        Need n = r.Post(NeedKind.LightSource, "illu>=42", "AutoLightManager");
        r.TryClaim(NeedKind.LightSource, "AutoGet", out _);

        Assert.True(r.Resolve(n));
        Assert.Empty(r.Outstanding(NeedKind.LightSource));
        Assert.False(r.Resolve(n));
    }

    [Fact]
    public void Release_ReturnsClaimedNeedToPool()
    {
        NeedsRegistry r = new();
        Need n = r.Post(NeedKind.LightSource, "illu>=42", "AutoLightManager");
        r.TryClaim(NeedKind.LightSource, "AutoGet", out _);

        Assert.True(r.Release(n));
        // Back in the unclaimed pool — claimable again.
        Assert.True(r.TryClaim(NeedKind.LightSource, "AutoGet2", out Need reclaimed));
        Assert.Equal(n.Descriptor, reclaimed.Descriptor);
    }

    [Fact]
    public void Release_UnclaimedNeed_ReturnsFalse()
    {
        NeedsRegistry r = new();
        Need n = r.Post(NeedKind.LightSource, "illu>=42", "AutoLightManager");
        Assert.False(r.Release(n));
    }

    [Fact]
    public void Clear_DropsAllNeeds()
    {
        NeedsRegistry r = new();
        r.Post(NeedKind.LightSource, "illu>=42", "AutoLightManager");
        r.Post(NeedKind.LightSource, "illu>=100", "AutoLightManager");

        r.Clear();

        Assert.Empty(r.Outstanding(NeedKind.LightSource));
    }
}
