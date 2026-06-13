using System.Collections.Generic;
using System.IO;
using System.Linq;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData.Tables;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the Game Data → Items "Other Info" use-cast rendering: a non-weapon
/// usable item (potion / wand / scroll) carrying a CastsSp (Abil 43) must
/// surface the cast spell's effect — the damage / heal it does — not just the
/// spell's name. The weapon use-cast path already did this; this guards the
/// non-weapon path that previously dropped the effect sub-row.
/// </summary>
public sealed class ItemUseCastEffectTests : IDisposable
{
    private readonly string _root;
    private readonly GameDataCache _cache;

    public ItemUseCastEffectTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-usecast-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _cache = new GameDataCache(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private void Seed(string table, string json)
    {
        string dir = Path.Combine(_root, "v1.11p");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{table}.json"), json);
    }

    private IReadOnlyList<KeyValuePair<string, string>> OtherInfoFor(string itemNumber)
    {
        ItemsSectionViewModel vm = new(_cache);
        return vm.BuildOtherInfoForTests(itemNumber);
    }

    [Fact]
    public void NonWeaponUseCast_ShowsEffectDamage()
    {
        // Spell #7 "lightning": Damage (Abil 1) with a 30–50 base range,
        // obtainable at level 5. At base level 0 the calculator clamps up to
        // ReqLevel, so the rendered effect is "Dmg 30–50".
        Seed("Spells",
            "[{\"Number\":7,\"Name\":\"lightning\",\"ReqLevel\":5,\"MinBase\":30,\"MaxBase\":50," +
             "\"Abil-0\":1,\"AbilVal-0\":0}]");

        // Item #100: a non-weapon (ItemType 2) usable that casts spell #7 on use.
        Seed("Items",
            "[{\"Number\":100,\"Name\":\"Wand of Lightning\",\"ItemType\":2," +
             "\"Abil-0\":43,\"AbilVal-0\":7}]");
        _cache.SwitchSet("v1.11p");

        IReadOnlyList<KeyValuePair<string, string>> info = OtherInfoFor("100");

        // The cast spell's name still surfaces (CastsSp → Spells.Name).
        Assert.Contains(info, kv => kv.Value == "lightning");
        // ...and now its effect/damage does too.
        KeyValuePair<string, string> effect =
            info.Single(kv => kv.Key.Trim() == "Effect");
        Assert.Equal("Dmg 30–50", effect.Value);
    }

    [Fact]
    public void WeaponUseCast_StillShowsEffectDamage()
    {
        // Same spell, but cast by a weapon (ItemType 1) — the existing weapon
        // use-cast path must keep rendering the effect after the fix.
        Seed("Spells",
            "[{\"Number\":7,\"Name\":\"lightning\",\"ReqLevel\":5,\"MinBase\":30,\"MaxBase\":50," +
             "\"Abil-0\":1,\"AbilVal-0\":0}]");
        Seed("Items",
            "[{\"Number\":101,\"Name\":\"Storm Blade\",\"ItemType\":1," +
             "\"Abil-0\":43,\"AbilVal-0\":7}]");
        _cache.SwitchSet("v1.11p");

        IReadOnlyList<KeyValuePair<string, string>> info = OtherInfoFor("101");

        Assert.Contains(info, kv => kv.Key == "Casts (on use)" && kv.Value == "lightning");
        KeyValuePair<string, string> effect =
            info.Single(kv => kv.Key.Trim() == "Effect");
        Assert.Equal("Dmg 30–50", effect.Value);
    }
}
