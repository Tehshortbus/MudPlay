using System.Linq;
using MudPlay.Models.Settings;
using Xunit;

namespace MudPlay.Tests;

public sealed class HelpWebsitesSettingsTests
{
    [Fact]
    public void DefaultLinks_SeedsTheFourReferenceSites()
    {
        var links = HelpWebsitesSettings.DefaultLinks();

        Assert.Equal(4, links.Count);
        Assert.All(links, l =>
        {
            Assert.False(string.IsNullOrWhiteSpace(l.Label));
            Assert.StartsWith("https://", l.Url);
        });
        // The Facebook group is the entry added on top of the original three.
        Assert.Contains(links, l => l.Url == "https://www.facebook.com/groups/4826389426");
    }

    [Fact]
    public void NewInstance_DefaultsToTheSeededLinks()
    {
        HelpWebsitesSettings dto = new();
        Assert.Equal(
            HelpWebsitesSettings.DefaultLinks().Select(l => l.Url),
            dto.Links.Select(l => l.Url));
    }
}
