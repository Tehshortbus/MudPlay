using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using MudPlay.ViewModels.GameData.Edit;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// The route Details window's monster-name colouring: off → alignment tint, on →
// green/amber/red by each monster's live Hits-You-%, recolouring live as the toggle
// or thresholds change, and persisting the choice.
public sealed class RouteDetailsColoringTests
{
    private static readonly IBrush Align = Brushes.Magenta;   // stand-in alignment tint

    private static RoomDetailLink Monster(int? hit) =>
        new RoomDetailLink("beast", null, new RelayCommand(() => { }))
        {
            AlignAccent = Align,
            Accent = Align,
            HitPercent = hit,
        };

    private static RouteDetailRow Row(params RoomDetailLink[] monsters) =>
        new RouteDetailRow(
            new RouteStepRow(1, "13/497 Rugged Shoreline", "s"),
            monsters, new RelayCommand(() => { }));

    [Fact]
    public void ColouringOff_LeavesAlignmentTint()
    {
        RoomDetailLink mob = Monster(80);
        var vm = new RouteDetailsDialogViewModel("t", new[] { Row(mob) });
        Assert.False(vm.ColorByHitPercent);
        Assert.Same(Align, mob.Accent);
    }

    [Fact]
    public void ColouringOn_MapsEachMonsterToItsBand()
    {
        RoomDetailLink green = Monster(10), amber = Monster(30), red = Monster(80), unknown = Monster(null);
        _ = new RouteDetailsDialogViewModel(
            "t", new[] { Row(green, amber, red, unknown) },
            colorByHitPercent: true, greenMax: 15, yellowMax: 45, persist: null);

        Assert.Same(MonsterHitBand.Green, green.Accent);
        Assert.Same(MonsterHitBand.Yellow, amber.Accent);
        Assert.Same(MonsterHitBand.Red, red.Accent);
        Assert.Same(MonsterHitBand.Muted, unknown.Accent);
    }

    [Fact]
    public void Toggle_RecoloursAndRestores()
    {
        RoomDetailLink mob = Monster(80);
        var vm = new RouteDetailsDialogViewModel("t", new[] { Row(mob) });
        Assert.Same(Align, mob.Accent);
        vm.ColorByHitPercent = true;
        Assert.Same(MonsterHitBand.Red, mob.Accent);
        vm.ColorByHitPercent = false;
        Assert.Same(Align, mob.Accent);
    }

    [Fact]
    public void MovingGreenThreshold_RepaintsLive()
    {
        RoomDetailLink mob = Monster(30);   // amber under a 15/45 split
        var vm = new RouteDetailsDialogViewModel(
            "t", new[] { Row(mob) },
            colorByHitPercent: true, greenMax: 15, yellowMax: 45, persist: null);
        Assert.Same(MonsterHitBand.Yellow, mob.Accent);

        vm.GreenMax = 35;                    // now 30 <= green → green
        Assert.Same(MonsterHitBand.Green, mob.Accent);
    }

    [Fact]
    public void Changes_PersistWithRoundedValues()
    {
        (bool Enabled, int Green, int Yellow)? saved = null;
        var vm = new RouteDetailsDialogViewModel(
            "t", new[] { Row(Monster(10)) },
            colorByHitPercent: false, greenMax: 15, yellowMax: 45,
            persist: (e, g, y) => saved = (e, g, y));

        vm.ColorByHitPercent = true;
        Assert.Equal((true, 15, 45), saved);

        vm.YellowMax = 50.6;                 // slider feeds doubles; persisted rounded
        Assert.Equal((true, 15, 51), saved);
    }
}
