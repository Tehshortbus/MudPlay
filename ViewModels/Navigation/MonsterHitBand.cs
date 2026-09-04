using Avalonia.Media;

namespace MudPlay.ViewModels.Navigation;

// The green / amber / red palette + band mapping for colouring monster names by
// their live "Hits You %" in the route Details window. Kept as one source so the
// BandRangeSlider's track colours and the monster-name tints agree exactly. A null
// hit% (no character context, or a monster with no physical attack) reads muted.
public static class MonsterHitBand
{
    public static readonly IBrush Green = new SolidColorBrush(Color.Parse("#5FB562"));
    public static readonly IBrush Yellow = new SolidColorBrush(Color.Parse("#D8B23A"));
    public static readonly IBrush Red = new SolidColorBrush(Color.Parse("#E06060"));
    public static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#7A7F87"));

    // hit% at or below greenMax → green; at or below yellowMax → amber; above → red;
    // an unknown (null) hit% → muted.
    public static IBrush BrushFor(int? hitPercent, int greenMax, int yellowMax)
        => hitPercent is not { } p ? Muted
            : p <= greenMax ? Green
            : p <= yellowMax ? Yellow
            : Red;
}
