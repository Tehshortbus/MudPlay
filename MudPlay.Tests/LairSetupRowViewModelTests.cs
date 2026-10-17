using MudPlay.Models.Profile;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// LairSetupRowViewModel formats the right-rail row labels.
// Pure display logic; no graph, no manager.
public sealed class LairSetupRowViewModelTests
{
    [Fact]
    public void EmptySetup_AnchorKeyEmpty_SubLabelZero()
    {
        var row = new LairSetupRowViewModel(new LairSetup("empty", Array.Empty<LairMarker>()));
        Assert.Equal(string.Empty, row.AnchorKey);
        Assert.Equal("0 lairs", row.SubLabel);
        Assert.Equal("empty", row.Name);
    }

    [Fact]
    public void SingleMarker_AnchorKeyAndSubLabel()
    {
        var row = new LairSetupRowViewModel(new LairSetup("solo", new[]
        {
            new LairMarker(7, 50),
        }));
        Assert.Equal("7/50", row.AnchorKey);
        // Singular form when exactly one marker.
        Assert.Equal("1 lair", row.SubLabel);
    }

    [Fact]
    public void MultipleMarkers_AnchorKeyIsFirst_PluralSubLabel()
    {
        var row = new LairSetupRowViewModel(new LairSetup("multi", new[]
        {
            new LairMarker(1, 100),
            new LairMarker(1, 101),
            new LairMarker(1, 102),
        }));
        Assert.Equal("1/100", row.AnchorKey);
        Assert.Equal("3 lairs", row.SubLabel);
    }
}
