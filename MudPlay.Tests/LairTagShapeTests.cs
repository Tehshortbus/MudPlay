using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// Pins the lair-tag shape the imported Rooms.Lair cells use, because a parser
// that misses it fails silently — it reports "no monsters here" rather than
// throwing, which read as "these loops have no lairs" across an entire realm.
public sealed class LairTagShapeTests
{
    // The shape this realm's Rooms.Lair cells actually use: a max-regen header,
    // the monster ids, then the spawn-group index in trailing brackets.
    private const string MaxHeaderShape = "(Max 1): 53,54,55,56,58,123,124,125,752,753,754,[10-10-15-1]";

    [Fact]
    public void ParseLairTag_ReadsTheMaxHeaderShape()
    {
        RoomTooltipBuilder.ParseLairTag(MaxHeaderShape, out int? max, out IReadOnlyList<int> ids);

        Assert.Equal(1, max);
        Assert.Equal(new[] { 53, 54, 55, 56, 58, 123, 124, 125, 752, 753, 754 }, ids);
    }

    [Fact]
    public void ParseLairTag_DoesNotMistakeTheGroupIndexForMonsterIds()
    {
        // "[10-10-15-1]" is a spawn-rule key, not a mob list. Leaking those
        // numbers in would invent monsters that aren't there.
        RoomTooltipBuilder.ParseLairTag("(Max 2): 503,[30-24-24-2]", out _, out IReadOnlyList<int> ids);
        Assert.Equal(new[] { 503 }, ids);
    }

    [Fact]
    public void ParseLairTag_ToleratesEmpty()
    {
        RoomTooltipBuilder.ParseLairTag("", out int? max, out IReadOnlyList<int> ids);
        Assert.Null(max);
        Assert.Empty(ids);
    }
}
