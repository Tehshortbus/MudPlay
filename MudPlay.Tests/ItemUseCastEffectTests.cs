using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;
using MudPlay.ViewModels.GameData.Tables;
using Xunit;

namespace MudPlay.Tests;

// Pins the Game Data → Items use-cast rendering: an item carrying a CastsSp
// (Abil 43) surfaces the cast spell as a clickable "Casts" link (name + record
// number) carrying the spell's effect — the damage / heal it does. Weapon and
// non-weapon usables both route through the typed CastsSpells collection now
// (the on-use / proc message lives on the shared spell record).
public sealed class ItemUseCastEffectTests : IDisposable
{
    private readonly string _root;
    private readonly GameDataCache _cache;

    public ItemUseCastEffectTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-usecast-tests-" + Path.GetRandomFileName());
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

    private IReadOnlyList<CastsSpellRow> CastsFor(string itemNumber) =>
        new ItemMdbViewBuilder(_cache, playerCharm: 50).Build(itemNumber).CastsSpells
        ?? System.Array.Empty<CastsSpellRow>();

    private IReadOnlyList<ShopSaleRow> ShopSalesFor(string itemNumber)
    {
        ItemsSectionViewModel vm = new(_cache);
        return vm.BuildShopSalesForTests(itemNumber);
    }

    private IReadOnlyList<DroppedByRow> DroppedByFor(string itemNumber)
    {
        ItemsSectionViewModel vm = new(_cache);
        return vm.BuildDroppedByForTests(itemNumber);
    }

    private IReadOnlyList<PlacedInRow> PlacedInFor(string itemNumber)
    {
        ItemsSectionViewModel vm = new(_cache);
        return vm.BuildPlacedInForTests(itemNumber);
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

        CastsSpellRow cast = Assert.Single(CastsFor("100"));

        // The cast spell surfaces as a clickable link (name + record number)...
        Assert.Equal("Casts (on use)", cast.Label);
        Assert.Equal("lightning (#7)", cast.SpellName);
        // ...carrying the spell's effect / damage.
        Assert.Equal("Dmg 30–50", cast.Effect);
    }

    [Fact]
    public void UseCast_PerLevelSpell_ScalesToItemRequiredLevel()
    {
        // Spell #8 "spear": pure per-level scaling — no flat base, only
        // MinInc/MaxInc per level (slope 2 / 3, denominator 1). At level 0 it
        // yields 0, so it would render no damage unless evaluated at the item's
        // level. ReqLevel 0 means there's no spell-side clamp to lift it.
        Seed("Spells",
            "[{\"Number\":8,\"Name\":\"spear\",\"ReqLevel\":0,\"Cap\":50," +
             "\"MinBase\":0,\"MaxBase\":0,\"MinInc\":2,\"MinIncLVLs\":1," +
             "\"MaxInc\":3,\"MaxIncLVLs\":1,\"Abil-0\":1,\"AbilVal-0\":0}]");

        // Item #102: a weapon (ItemType 1) whose required level is encoded as
        // ability code 135 (MinLevel) = 45. Its use-cast (Abil 43) is spear.
        // The cast must scale to 45: Dmg 2*45 – 3*45 = "Dmg 90–135".
        Seed("Items",
            "[{\"Number\":102,\"Name\":\"Nexus Spear\",\"ItemType\":1," +
             "\"Abil-0\":135,\"AbilVal-0\":45,\"Abil-1\":43,\"AbilVal-1\":8}]");
        _cache.SwitchSet("v1.11p");

        CastsSpellRow cast = Assert.Single(CastsFor("102"));

        Assert.Equal("Casts (on use)", cast.Label);
        Assert.Equal("spear (#8)", cast.SpellName);
        Assert.Equal("Dmg 90–135", cast.Effect);
    }

    [Fact]
    public void UseCast_RandomPool_ShowsElementNamesAndSharedDamage()
    {
        // Spell #20 "random dmg": MME's random-cast encoding — no direct
        // damage, a single EndCast (Abil 151) with AbilVal 0, and MinBase /
        // MaxBase holding a spell-NUMBER range (21–23). On cast the game fires
        // one of spells 21/22/23 at random; each does Damage 5–15. The Effect
        // row must surface the pool names + the shared damage, not blank.
        Seed("Spells",
            "[{\"Number\":20,\"Name\":\"random dmg\",\"MinBase\":21,\"MaxBase\":23,\"Cap\":32," +
             "\"Abil-0\":151,\"AbilVal-0\":0}," +
             "{\"Number\":21,\"Name\":\"rocks\",\"MinBase\":5,\"MaxBase\":15,\"Abil-0\":1,\"AbilVal-0\":0}," +
             "{\"Number\":22,\"Name\":\"ice\",\"MinBase\":5,\"MaxBase\":15,\"Abil-0\":1,\"AbilVal-0\":0}," +
             "{\"Number\":23,\"Name\":\"fire\",\"MinBase\":5,\"MaxBase\":15,\"Abil-0\":1,\"AbilVal-0\":0}]");

        // Item #103: a weapon whose required level is 45 (Abil 135), with a
        // 100%-per-swing proc (Abil 114 = 100) casting spell #20.
        Seed("Items",
            "[{\"Number\":103,\"Name\":\"Warhammer\",\"ItemType\":1," +
             "\"Abil-0\":135,\"AbilVal-0\":45,\"Abil-1\":114,\"AbilVal-1\":100," +
             "\"Abil-2\":43,\"AbilVal-2\":20}]");
        _cache.SwitchSet("v1.11p");

        CastsSpellRow cast = Assert.Single(CastsFor("103"));

        Assert.Equal("Casts (100%/swing)", cast.Label);
        Assert.Equal("random dmg (#20)", cast.SpellName);
        Assert.Equal("EndCast (random): rocks / ice / fire (Dmg 5–15)", cast.Effect);
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

        CastsSpellRow cast = Assert.Single(CastsFor("101"));

        Assert.Equal("Casts (on use)", cast.Label);
        Assert.Equal("lightning (#7)", cast.SpellName);
        Assert.Equal("Dmg 30–50", cast.Effect);
    }

    [Fact]
    public void BoughtSold_RendersAsClickableShopRow()
    {
        // Shop #5 sits in room 1/10 ("General Store"); item #200 lists it in
        // Obtained From. The bought/sold shop line renders as a clickable row
        // whose location names the room + locator and links to that room record.
        Seed("Shops", "[{\"Number\":5,\"Assigned To\":\"Room 1/10\"}]");
        Seed("Rooms", "[{\"Map Number\":1,\"Room Number\":10,\"Name\":\"General Store\"}]");
        Seed("Items", "[{\"Number\":200,\"Name\":\"Torch\",\"ItemType\":0,\"Obtained From\":\"Shop #5\"}]");
        _cache.SwitchSet("v1.11p");

        ShopSaleRow row = Assert.Single(ShopSalesFor("200"));
        Assert.Contains("General Store", row.Location);
        Assert.Contains("1/10", row.Location);
        // The host room resolved, so the location is a live navigation link.
        Assert.True(row.CanOpen);
    }

    [Fact]
    public void BoughtSold_PriceReflectsCharm()
    {
        // The buy price is charm-scaled, so the same shop item shows a different
        // BUY line at charm 30 vs 70 — the dialog's charm picker re-prices live
        // off this. The "@Ncha" prefix is gone (charm now lives in the picker).
        Seed("Shops", "[{\"Number\":5,\"Assigned To\":\"Room 1/10\",\"Markup%\":0}]");
        Seed("Rooms", "[{\"Map Number\":1,\"Room Number\":10,\"Name\":\"General Store\"}]");
        Seed("Items", "[{\"Number\":200,\"Name\":\"Torch\",\"ItemType\":0,\"Price\":1000,\"Currency\":0,\"Obtained From\":\"Shop #5\"}]");
        _cache.SwitchSet("v1.11p");

        string lowCharm  = new ItemMdbViewBuilder(_cache, 30).Build("200").Shops[0].Price;
        string highCharm = new ItemMdbViewBuilder(_cache, 70).Build("200").Shops[0].Price;

        Assert.NotEqual(lowCharm, highCharm);          // buy price shifts with charm
        Assert.Contains("BUY:", lowCharm);
        Assert.DoesNotContain("@", lowCharm);          // "@Ncha" prefix dropped
    }

    [Fact]
    public void BoughtSold_Absent_WhenItemHasNoShop()
    {
        // No shop reference in Obtained From → no bought/sold rows at all.
        Seed("Items", "[{\"Number\":201,\"Name\":\"Quest Relic\",\"ItemType\":0,\"Obtained From\":\"Monster #1(50%)\"}]");
        _cache.SwitchSet("v1.11p");

        Assert.Empty(ShopSalesFor("201"));
    }

    [Fact]
    public void DroppedBy_CarriesDropPercent()
    {
        // Item #300 drops from monster #42 at 10%; the "Dropped By" row must show
        // the drop rate — "Prismatic Dragon(10%)" — not just the monster name.
        Seed("Monsters", "[{\"Number\":42,\"Name\":\"Prismatic Dragon\"}]");
        Seed("Items", "[{\"Number\":300,\"Name\":\"Dragon Scale\",\"ItemType\":0,\"Obtained From\":\"Monster #42(10%)\"}]");
        _cache.SwitchSet("v1.11p");

        // Dropped By is now a list of clickable monster links (not an Other-Info
        // string); the label carries the drop rate, e.g. "Prismatic Dragon(10%)".
        IReadOnlyList<DroppedByRow> dropped = DroppedByFor("300");

        Assert.Contains(dropped, r => r.Label == "Prismatic Dragon(10%)");
    }

    [Fact]
    public void PlacedIn_ResolvesRoomNameAndLocator()
    {
        // Item #3796 (bogwood box) is floor-placed in room 14/10415; its "Obtained
        // From: Room 14/10415" must surface a clickable "Placed in" row naming the
        // room. Previously a room-only item rendered nothing (report-driven).
        Seed("Rooms", "[{\"Map Number\":14,\"Room Number\":10415,\"Name\":\"Damp Chamber, Platform\"}]");
        Seed("Items", "[{\"Number\":3796,\"Name\":\"bogwood box\",\"ItemType\":8,\"Obtained From\":\"Room 14/10415\"}]");
        _cache.SwitchSet("v1.11p");

        IReadOnlyList<PlacedInRow> placed = PlacedInFor("3796");

        Assert.Contains(placed, r => r.Location == "Damp Chamber, Platform - 14/10415" && r.CanOpen);
    }

    [Fact]
    public void PlacedIn_FallsBackToLocatorWhenRoomNameMissing()
    {
        // No Rooms row for the placement → the row still shows the bare "Room m/r"
        // locator rather than dropping the placement entirely.
        Seed("Items", "[{\"Number\":29,\"Name\":\"small sign\",\"ItemType\":8,\"Obtained From\":\"Room 1/224\"}]");
        _cache.SwitchSet("v1.11p");

        IReadOnlyList<PlacedInRow> placed = PlacedInFor("29");

        Assert.Contains(placed, r => r.Location == "Room 1/224" && r.CanOpen);
    }
}
