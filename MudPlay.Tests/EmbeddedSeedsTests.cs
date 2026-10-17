using System;
using System.IO;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The seed data ships EMBEDDED in the assembly (Defaults/*.seed.json), so a
// self-contained single-file build carries it inside the exe and replacing just
// the binary refreshes the seeds. AppPaths.ExtractEmbeddedSeeds is the launch-time
// step that materializes them to disk; these pin that it produces the full set and
// the shipped content (a regression guard against the csproj reverting to loose
// Content, which would silently drop the embedded resources).
public sealed class EmbeddedSeedsTests : IDisposable
{
    private readonly string _dir;

    public EmbeddedSeedsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mudplay-seeds-" + Path.GetRandomFileName());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup */ }
    }

    [Fact]
    public void ExtractEmbeddedSeeds_WritesEveryShippedSeedFile()
    {
        AppPaths.ExtractEmbeddedSeeds(_dir);

        foreach (string name in new[]
        {
            "MonsterOverlay.stock.seed.json", "MonsterOverlay.paradigm.seed.json",
            "ItemOverlay.stock.seed.json",    "ItemOverlay.paradigm.seed.json",
            "Messages.stock.seed.json",       "Messages.paradigm.seed.json",
            "MonsterMessages.seed.json",      "BossDefs.seed.json",
            "QuestDefs.seed.json",            "Triggers.seed.json",
        })
        {
            string path = Path.Combine(_dir, name);
            Assert.True(File.Exists(path), $"embedded seed not materialized: {name}");
            Assert.True(new FileInfo(path).Length > 0, $"materialized seed is empty: {name}");
        }
    }

    // Paradigm's data leaves the Witchunter quest unguarded, so the crawl reads it as
    // open to everyone and the login dump announced it to a Gypsy (report
    // paradigm-20261001-235024). The shipped quest list carries the class lock for it.
    [Theory]
    [InlineData(1)]   // Magebane
    [InlineData(4)]   // Draka's Blade
    public void ShippedQuestList_LocksTheWitchunterQuestToWitchunters(int step)
    {
        AppPaths.ExtractEmbeddedSeeds(_dir);
        QuestStore quests = new(seedPath: Path.Combine(_dir, "QuestDefs.seed.json"));

        System.Collections.Generic.List<int>? lockedTo = quests.Resolve(50, step).ClassRestrict;
        Assert.Equal(new[] { 2 }, lockedTo);   // class 2 = Witchunter on both realms

        MudPlay.Game.Quests.CrawledQuest unguarded = new(Flag: 50, Step: step, RequiredLevel: 15,
            Bonuses: Array.Empty<MudPlay.Game.Quests.QuestBonus>(), AwardItems: Array.Empty<int>(),
            ClassIds: null, RaceIds: null, RequiredAlignment: null);
        Assert.True(MudPlay.Game.Quests.QuestEligibilityResolver.IsIneligible(unguarded, classId: 10, null, lockedTo, false, false, false));
        Assert.False(MudPlay.Game.Quests.QuestEligibilityResolver.IsIneligible(unguarded, classId: 2, null, lockedTo, false, false, false));
    }

    [Fact]
    public void ExtractEmbeddedSeeds_CarriesShippedContent_GuardianIsFriend()
    {
        // Proves the embedded pipeline delivers the actual shipped values — the
        // ganghouse guardian default that a stale copy-if-missing bootstrap froze
        // out of existing installs (elite amber guardian ships as Friend).
        AppPaths.ExtractEmbeddedSeeds(_dir);

        string json = File.ReadAllText(Path.Combine(_dir, "MonsterOverlay.paradigm.seed.json"));
        int at = json.IndexOf("elite amber guardian", StringComparison.Ordinal);
        Assert.True(at >= 0, "elite amber guardian missing from the embedded paradigm overlay seed");
        // Its Relationship follows the Name within the same small record object.
        string window = json.Substring(at, Math.Min(160, json.Length - at));
        Assert.Contains("\"Friend\"", window);
    }

    [Fact]
    public void ParadigmMonsterSeed_CarriesLocationsAsCompleteTriples()
    {
        // The Monsters table's Landmass / Region / Area columns read these out of the
        // Defaults seed. A location with a label missing at one of the three levels would
        // show a half-filled row, so the shipped file must carry all three or none.
        AppPaths.ExtractEmbeddedSeeds(_dir);
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_dir, "MonsterOverlay.paradigm.seed.json")));

        int located = 0;
        foreach (System.Text.Json.JsonElement rec in doc.RootElement.EnumerateArray())
        {
            bool landmass = rec.TryGetProperty("Landmass", out System.Text.Json.JsonElement l) && !string.IsNullOrWhiteSpace(l.GetString());
            bool region = rec.TryGetProperty("Region", out System.Text.Json.JsonElement r) && !string.IsNullOrWhiteSpace(r.GetString());
            bool area = rec.TryGetProperty("Area", out System.Text.Json.JsonElement a) && !string.IsNullOrWhiteSpace(a.GetString());
            Assert.True(landmass == region && region == area,
                $"monster {rec.GetProperty("Number")} has a partial location");
            if (region) located++;
        }
        Assert.True(located > 1000, $"only {located} monsters carry a location in the paradigm seed");
    }

    [Fact]
    public void ParadigmMonsterSeed_LandmassesArePlainPlaceNames()
    {
        // A landmass is a big named land (Mainland, Albion, Shadowmere…). A place reached
        // through another one — a galleon, a cavern, a plane inside a fortress — is an
        // area of that region, so no landmass may read "X (in Y)" or carry the word
        // "Pocket" or "landmass".
        AppPaths.ExtractEmbeddedSeeds(_dir);
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_dir, "MonsterOverlay.paradigm.seed.json")));

        var landmasses = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
        foreach (System.Text.Json.JsonElement rec in doc.RootElement.EnumerateArray())
            if (rec.TryGetProperty("Landmass", out System.Text.Json.JsonElement l) && l.GetString() is { Length: > 0 } name)
                landmasses.Add(name);

        Assert.Contains("Mainland", landmasses);
        Assert.Contains("Albion", landmasses);
        foreach (string name in landmasses)
        {
            Assert.DoesNotContain("(in ", name);
            Assert.DoesNotContain("Pocket", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("landmass", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ExtractEmbeddedNavSeed_UnzipsEachRealmTree()
    {
        // nav-seed ships as an embedded zip per realm; the extract must reconstruct
        // the Loops tree + Favorites.json under nav-seed/{realm}/ (portable unzip).
        AppPaths.ExtractEmbeddedNavSeed(_dir);

        foreach (string realm in new[] { "stock", "paradigm" })
        {
            string realmDir = Path.Combine(_dir, "nav-seed", realm);
            Assert.True(File.Exists(Path.Combine(realmDir, "Favorites.json")),
                $"{realm} Favorites.json not unzipped");
            string loops = Path.Combine(realmDir, "Loops");
            Assert.True(Directory.Exists(loops), $"{realm} Loops/ not unzipped");
            Assert.NotEmpty(Directory.EnumerateFiles(loops, "*.loop", SearchOption.AllDirectories));
        }
    }
}
