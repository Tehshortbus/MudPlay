using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MudPlay.Models.GameData;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Tables;
using Xunit;

namespace MudPlay.Tests;

// A game-data edit never touches the imported table: it is a delta filed under the
// record's Number in a tier's own file, laid field by field over the seed record. The
// table row's Number is what finds it, so the row has to hand that number over as the
// same text the delta was filed under.
public sealed class GameDataDeltaTests : IDisposable
{
    private readonly string _root;
    private readonly string _set;
    private readonly GameDataCache _cache;

    public GameDataDeltaTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-delta-" + Path.GetRandomFileName());
        // Its own set name: the Global tier's override file is named after the set.
        _set = "delta-set-" + Path.GetRandomFileName();
        Directory.CreateDirectory(Path.Combine(_root, _set));
        File.WriteAllText(Path.Combine(_root, _set, "Monsters.json"),
            """[{"Number":10,"Name":"dark cleric","HP":45},{"Number":12,"Name":"old timer","HP":7}]""");
        _cache = new GameDataCache(_root);
        _cache.SwitchSet(_set);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
            File.Delete(AppPaths.OverrideFile(SettingsTier.Global, null, "Monsters", _set));
        }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task A_delta_is_found_by_the_rows_number_and_merges_over_the_seed_record()
    {
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        SettingsResolver resolver = new(new SettingsService(), new BbsProfileStore(), profile, () => _cache.ActiveSet);
        resolver.WriteGameDataAt(SettingsTier.Global, "Monsters", "12",
            new Dictionary<string, object> { ["Landmass"] = "Edited Land", ["OverridePreAttackCount"] = 5 });

        MonstersSectionViewModel section = new(_cache, resolver);
        await section.LoadAsync();
        GameDataRow edited = section.AllRows.Single(r => r.Get("Name") == "old timer");
        GameDataRow untouched = section.AllRows.Single(r => r.Get("Name") == "dark cleric");

        Assert.Equal(SettingsTier.Global, edited.SourceTier);
        Assert.Equal(SettingsTier.Defaults, untouched.SourceTier);
        // The row still shows the import: the delta lives beside the table.
        Assert.Equal("12", edited.Get("Number"));
        Assert.Equal("7", edited.Get("HP"));

        MonsterOverlay seed = new() { Region = "Seed Region", OverridePreAttackCount = 2 };
        MonsterOverlay merged = resolver.ResolveGameData("Monsters", edited.Get("Number")!, seed);
        Assert.Equal("Edited Land", merged.Landmass);          // only in the delta
        Assert.Equal(5, merged.OverridePreAttackCount);        // the delta wins
        Assert.Equal("Seed Region", merged.Region);            // untouched by the delta
        Assert.Equal(seed, resolver.ResolveGameData("Monsters", untouched.Get("Number")!, seed));
    }
}
