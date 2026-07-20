using FujinTerm.Controls;
using Xunit;

namespace FujinTerm.Tests;

// Covers MapControl.HeatBucketIndex — the lair-heat colour keying. MajorMUD
// lair respawns start at 30s and step in 30s intervals, so a lair's colour is
// chosen by its 30s bucket (0 = 30s, 1 = 60s, ...); buckets 0..9 are the fixed
// 30s..5min rainbow, higher buckets fall into the purple->black tail.
public sealed class MapControlHeatTests
{
    [Theory]
    [InlineData(30, 0)]    // fastest lair -> red
    [InlineData(60, 1)]
    [InlineData(90, 2)]
    [InlineData(150, 4)]
    [InlineData(300, 9)]   // 5min -> last fixed stop (purple)
    [InlineData(330, 10)]  // first tail bucket
    [InlineData(600, 19)]  // deep in the tail
    public void HeatBucketIndex_MapsSecondsToThirtySecondBucket(int seconds, int expected)
    {
        Assert.Equal(expected, MapControl.HeatBucketIndex(seconds));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(29)]
    public void HeatBucketIndex_SubMinimum_ClampsToFastestBucket(int seconds)
    {
        // Anything under the 30s floor still lands on the fastest (red) stop
        // rather than a negative index.
        Assert.Equal(0, MapControl.HeatBucketIndex(seconds));
    }
}
