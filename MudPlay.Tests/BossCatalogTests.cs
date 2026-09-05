using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The boss rule + a sanity check on the shipped BossDefs.seed.json so a bad
// regeneration is caught before it ships.
public sealed class BossCatalogTests
{
    [Theory]
    [InlineData(1, 0, true)]    // GameLimit 1 → boss even with no regen
    [InlineData(0, 1, true)]    // RegenTime >= 1h → boss
    [InlineData(0, 24, true)]
    [InlineData(0, 0, false)]   // ordinary mob
    public void IsBoss_MatchesGameLimitOrHourRegen(int gameLimit, int regen, bool expected)
        => Assert.Equal(expected, BossCatalog.IsBoss(gameLimit, regen));

    [Fact]
    public void BundledSeed_ParsesWithExpectedShape()
    {
        // The seed ships embedded in the assembly; extract to a scratch dir and read
        // it there rather than the launch-materialized cache under the data root.
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-boss-" + Path.GetRandomFileName());
        List<BossDef>? seed;
        try
        {
            AppPaths.ExtractEmbeddedSeeds(dir);
            string path = Path.Combine(dir, "BossDefs.seed.json");
            Assert.True(File.Exists(path), $"bundled seed missing at {path}");
            seed = JsonStore.Load<List<BossDef>>(path);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp cleanup */ }
        }
        Assert.NotNull(seed);

        List<BossDef> bosses = seed!;
        Assert.Equal(240, bosses.Count);

        List<BossDef> timed = bosses.Where(b => b.RespawnType == BossRespawnType.Timed).ToList();
        Assert.Equal(235, timed.Count);
        Assert.Equal(5, bosses.Count(b => b.RespawnType == BossRespawnType.Cleanup));
        Assert.Equal(142, bosses.Count(b => b.InStock));
        Assert.Equal(238, bosses.Count(b => b.InParadigm));
        Assert.Equal(43, bosses.Count(b => b.Rooms.Count > 1));
        Assert.Equal(4, bosses.Count(b => b.RespawnHoursOverride is not null));   // curated manual timers

        // Every timed boss resolved a game-data monster (name is the runtime match key).
        Assert.All(timed, b =>
        {
            Assert.NotNull(b.MonsterNumber);
            Assert.False(string.IsNullOrWhiteSpace(b.Name));
            Assert.NotEmpty(b.Rooms);
        });
        // Every room parses as map/room.
        Assert.All(bosses.SelectMany(b => b.Rooms), r =>
            Assert.True(MudPlay.Game.Map.RoomKey.TryParseWire(r, out _), $"bad room '{r}'"));
    }
}
