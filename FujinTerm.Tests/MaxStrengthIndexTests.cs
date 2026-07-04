using System.IO;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Coverage for <see cref="MaxStrengthIndex"/> — the per-set door bash ceiling
/// (strongest race's Strength cap plus the best <c>+Strength</c> gear any class can
/// wear), which supersedes the old hardcoded 200. Each test seeds an isolated temp
/// game-data set so the walk is deterministic.
/// </summary>
public sealed class MaxStrengthIndexTests : IDisposable
{
    private readonly string _root;

    public MaxStrengthIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-maxstr-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private void SeedSet(string setName, params (string Table, string Json)[] tables)
    {
        string dir = Path.Combine(_root, setName);
        Directory.CreateDirectory(dir);
        foreach (var (table, json) in tables)
            File.WriteAllText(Path.Combine(dir, table + ".json"), json);
    }

    // Two races (strongest xSTR = Ogre 190) and two classes with divergent gear access.
    private const string Races =
        "[{\"Number\":1,\"Name\":\"Ogre\",\"mSTR\":70,\"xSTR\":190}," +
        " {\"Number\":2,\"Name\":\"Human\",\"mSTR\":40,\"xSTR\":145}]";

    private const string Classes =
        "[{\"Number\":1,\"Name\":\"Warrior\",\"WeaponType\":8,\"ArmourType\":9}," +
        " {\"Number\":2,\"Name\":\"Mage\",\"WeaponType\":9,\"ArmourType\":0}]";

    // - ogre necklace  : Neck (Worn 8),  +10 STR — best of two neck pieces
    // - lesser necklace: Neck (Worn 8),  +4  STR — loses the Neck slot to the ogre one
    // - ring of might  : Finger (Worn 4),+5  STR — the doubled slot counts it twice
    // - cursed gauntlet: Hands (Worn 3), +50 STR but StrReq 999 — unreachable, excluded
    // - war axe        : Weapon,         +20 STR, ClassRest [Warrior] — Warrior-only
    private const string Items =
        "[{\"Number\":10,\"Name\":\"ogre necklace\",\"ItemType\":2,\"Worn\":8,\"StrReq\":0,\"Abil-0\":46,\"AbilVal-0\":10}," +
        " {\"Number\":11,\"Name\":\"lesser necklace\",\"ItemType\":2,\"Worn\":8,\"StrReq\":0,\"Abil-0\":46,\"AbilVal-0\":4}," +
        " {\"Number\":12,\"Name\":\"ring of might\",\"ItemType\":2,\"Worn\":4,\"StrReq\":0,\"Abil-0\":46,\"AbilVal-0\":5}," +
        " {\"Number\":13,\"Name\":\"cursed gauntlet\",\"ItemType\":2,\"Worn\":3,\"StrReq\":999,\"Abil-0\":46,\"AbilVal-0\":50}," +
        " {\"Number\":14,\"Name\":\"war axe\",\"ItemType\":1,\"WeaponType\":2,\"StrReq\":0,\"ClassRest-0\":1,\"Abil-0\":46,\"AbilVal-0\":20}]";

    // Held Unique-Pool (code 188) items — stats apply while merely carried, ungated by
    // class / StrReq, one item per pool.
    //  - red / green sphere : pool 1, +15 / +8 STR — pool contributes its best (15), not both
    //  - amulet of carrying : pool 2, +5  STR — a distinct pool stacks on top
    private const string HeldItems =
        "[{\"Number\":20,\"Name\":\"floating red sphere\",\"ItemType\":0,\"Worn\":0,\"StrReq\":0,\"Abil-0\":46,\"AbilVal-0\":15,\"Abil-1\":188,\"AbilVal-1\":1}," +
        " {\"Number\":21,\"Name\":\"floating green sphere\",\"ItemType\":0,\"Worn\":0,\"StrReq\":0,\"Abil-0\":46,\"AbilVal-0\":8,\"Abil-1\":188,\"AbilVal-1\":1}," +
        " {\"Number\":22,\"Name\":\"amulet of carrying\",\"ItemType\":0,\"Worn\":0,\"StrReq\":0,\"Abil-0\":46,\"AbilVal-0\":5,\"Abil-1\":188,\"AbilVal-1\":2}]";

    [Fact]
    public void MaxAchievableStrength_StrongestRacePlusBestGearOverAllClasses()
    {
        // Ogre base 190. Warrior wears war axe (+20, class-granted), ogre necklace
        // (+10), two rings (+5 x2). Mage can't wield the axe, so its gear tops out at
        // +20 — Warrior's +40 wins. The +50 gauntlet is StrReq-gated out. => 190 + 40.
        SeedSet("realm", ("Races", Races), ("Classes", Classes), ("Items", Items));
        GameDataCache cache = new(_root);
        cache.SwitchSet("realm");

        MaxStrengthIndex index = new(cache);

        Assert.Equal(230, index.MaxAchievableStrength);
    }

    [Fact]
    public void MaxAchievableStrength_HeldUniquePoolItems_BestPerPoolSummed()
    {
        // Ogre base 190, no worn/wielded gear. Held: pool 1 (red +15 / green +8) yields its
        // best 15 — only one item per pool may be held, so 15 not 23; pool 2 (+5) stacks.
        // => 190 + 15 + 5 = 210 (a naive sum-all-held would give 218 or 228).
        SeedSet("held", ("Races", Races), ("Classes", Classes), ("Items", HeldItems));
        GameDataCache cache = new(_root);
        cache.SwitchSet("held");

        MaxStrengthIndex index = new(cache);

        Assert.Equal(210, index.MaxAchievableStrength);
    }

    [Fact]
    public void MaxAchievableStrength_NoGear_IsRacialCapAlone()
    {
        // Empty (but present) Items table — the walk runs, finds no +Strength gear,
        // and returns the strongest race's trainable cap unchanged.
        SeedSet("bare", ("Races", Races), ("Classes", Classes), ("Items", "[]"));
        GameDataCache cache = new(_root);
        cache.SwitchSet("bare");

        MaxStrengthIndex index = new(cache);

        Assert.Equal(190, index.MaxAchievableStrength);
    }

    [Fact]
    public void MaxAchievableStrength_MissingItemsTable_FallsBackToDefault()
    {
        // No Items.json at all — the walk can't run, so the conservative default
        // stands rather than a race-only under-estimate.
        SeedSet("noitems", ("Races", Races), ("Classes", Classes));
        GameDataCache cache = new(_root);
        cache.SwitchSet("noitems");

        MaxStrengthIndex index = new(cache);

        Assert.Equal(DoorPolicy.UnbashableStrengthThreshold, index.MaxAchievableStrength);
    }

    [Fact]
    public void MaxAchievableStrength_NoActiveSet_FallsBackToDefault()
    {
        GameDataCache cache = new(_root);
        MaxStrengthIndex index = new(cache);

        Assert.Equal(DoorPolicy.UnbashableStrengthThreshold, index.MaxAchievableStrength);
    }

    [Fact]
    public void MaxAchievableStrength_RecomputesOnSetSwitch()
    {
        // A weaker realm: strongest race xSTR 100, no gear. Switching invalidates the
        // memoised value so the next read reflects the new set.
        SeedSet("strong", ("Races", Races), ("Classes", Classes), ("Items", Items));
        SeedSet("weak",
            ("Races", "[{\"Number\":1,\"Name\":\"Pixie\",\"mSTR\":10,\"xSTR\":100}]"),
            ("Classes", Classes),
            ("Items", "[]"));

        GameDataCache cache = new(_root);
        MaxStrengthIndex index = new(cache);

        cache.SwitchSet("strong");
        Assert.Equal(230, index.MaxAchievableStrength);

        cache.SwitchSet("weak");
        Assert.Equal(100, index.MaxAchievableStrength);
    }
}
