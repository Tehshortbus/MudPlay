using System.Linq;
using System.Text.Json;
using FujinTerm.Models.Settings;
using Xunit;

namespace FujinTerm.Tests;

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

    [Fact]
    public void RoundTripJson_PreservesLabelsUrlsAndOrder()
    {
        HelpWebsitesSettings original = new()
        {
            Links = new()
            {
                new HelpWebsite { Label = "First",  Url = "https://one.example/" },
                new HelpWebsite { Label = "Second", Url = "https://two.example/" },
            },
        };

        string json = JsonSerializer.Serialize(original);
        HelpWebsitesSettings? round = JsonSerializer.Deserialize<HelpWebsitesSettings>(json);

        Assert.NotNull(round);
        Assert.Equal(2, round!.Links.Count);
        Assert.Equal("First",                round.Links[0].Label);
        Assert.Equal("https://one.example/", round.Links[0].Url);
        Assert.Equal("Second",               round.Links[1].Label);
        Assert.Equal("https://two.example/", round.Links[1].Url);
    }
}
