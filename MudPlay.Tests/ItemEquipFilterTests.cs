using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins the per-character equip predicate behind the Equipment Manager's item
// search: the alignment-word → AlignmentBucket reduction and the
// level / class / alignment / weapon-type / armour-tier
// ItemEquipFilter.CanEquip gate. Each player dimension must degrade gracefully — an
// unknown level / class / alignment skips that filter rather than hiding gear — and
// a class grant (item Abil-59 ClassOk, or a matching ClassRest) must
// bypass weapon / armour type gating while anti-magic still blocks magical gear.
public sealed class ItemEquipFilterTests
{
    // A class profile with permissive weapon / armour tiers (All Weapons + max armour)
    // so the level / class / alignment tests aren't incidentally gated by item type;
    // the type-gating tests override weaponType / armourType explicitly.
    private static ClassEquipProfile Cls(
        int number, int weaponType = 8, int armourType = 9, bool antiMagic = false) =>
        new(number, weaponType, armourType, antiMagic);

    // ===== BucketForWord =====

    [Theory]
    [InlineData("Saint", AlignmentBucket.Good)]
    [InlineData("Lawful", AlignmentBucket.Good)]
    [InlineData("good", AlignmentBucket.Good)]       // case-insensitive
    [InlineData("Neutral", AlignmentBucket.Neutral)]
    [InlineData("Seedy", AlignmentBucket.Evil)]
    [InlineData("Outlaw", AlignmentBucket.Evil)]
    [InlineData("Criminal", AlignmentBucket.Evil)]
    [InlineData("Villain", AlignmentBucket.Evil)]
    [InlineData("Fiend", AlignmentBucket.Evil)]
    public void BucketForWord_KnownLadderWord_MapsToBucket(string word, AlignmentBucket expected)
    {
        Assert.Equal(expected, ItemEquipFilter.BucketForWord(word));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Wanderer")]   // not an alignment word
    public void BucketForWord_UnknownOrBlank_IsNull(string? word)
    {
        Assert.Null(ItemEquipFilter.BucketForWord(word));
    }

    // ===== CanEquip — level =====

    [Fact]
    public void CanEquip_NoRestrictions_AllowsAnyCharacter()
    {
        GameRow doc = TestRows.Row("{}");
        Assert.True(ItemEquipFilter.CanEquip(doc, level: 40, Cls(3), AlignmentBucket.Evil));
    }

    [Fact]
    public void CanEquip_NoRowAtAll_IsNotWearable()
    {
        Assert.False(ItemEquipFilter.CanEquip(default, level: 40, Cls(3), AlignmentBucket.Evil));
    }

    [Fact]
    public void CanEquip_MinLevel_BlocksBelowAllowsAtOrAbove()
    {
        GameRow doc = TestRows.Row("""{ "Abil-0": 135, "AbilVal-0": 10 }""");

        Assert.False(ItemEquipFilter.CanEquip(doc, level: 5, ClassEquipProfile.Unknown, null));
        Assert.True(ItemEquipFilter.CanEquip(doc, level: 10, ClassEquipProfile.Unknown, null));
        Assert.True(ItemEquipFilter.CanEquip(doc, level: 25, ClassEquipProfile.Unknown, null));
    }

    [Fact]
    public void CanEquip_MaxLevel_BlocksAboveAllowsAtOrBelow()
    {
        GameRow doc = TestRows.Row("""{ "Abil-0": 136, "AbilVal-0": 20 }""");

        Assert.False(ItemEquipFilter.CanEquip(doc, level: 25, ClassEquipProfile.Unknown, null));
        Assert.True(ItemEquipFilter.CanEquip(doc, level: 20, ClassEquipProfile.Unknown, null));
    }

    [Fact]
    public void CanEquip_UnknownLevel_SkipsLevelFilter()
    {
        GameRow doc = TestRows.Row("""{ "Abil-0": 135, "AbilVal-0": 30 }""");

        // level <= 0 means the character hasn't been stat'd — don't hide the item.
        Assert.True(ItemEquipFilter.CanEquip(doc, level: 0, ClassEquipProfile.Unknown, null));
    }

    // ===== CanEquip — alignment =====

    [Fact]
    public void CanEquip_GoodOnly_AllowsGoodBlocksEvil()
    {
        GameRow doc = TestRows.Row("""{ "Abil-0": 97 }""");

        Assert.True(ItemEquipFilter.CanEquip(doc, level: 0, ClassEquipProfile.Unknown, AlignmentBucket.Good));
        Assert.False(ItemEquipFilter.CanEquip(doc, level: 0, ClassEquipProfile.Unknown, AlignmentBucket.Evil));
    }

    [Fact]
    public void CanEquip_NotEvil_BlocksEvilAllowsGoodAndNeutral()
    {
        GameRow doc = TestRows.Row("""{ "Abil-0": 111 }""");

        Assert.False(ItemEquipFilter.CanEquip(doc, level: 0, ClassEquipProfile.Unknown, AlignmentBucket.Evil));
        Assert.True(ItemEquipFilter.CanEquip(doc, level: 0, ClassEquipProfile.Unknown, AlignmentBucket.Good));
        Assert.True(ItemEquipFilter.CanEquip(doc, level: 0, ClassEquipProfile.Unknown, AlignmentBucket.Neutral));
    }

    [Fact]
    public void CanEquip_UnknownAlignment_SkipsAlignmentFilter()
    {
        GameRow doc = TestRows.Row("""{ "Abil-0": 97 }""");

        // A null bucket (no who-word seen) shouldn't hide an aligned item.
        Assert.True(ItemEquipFilter.CanEquip(doc, level: 0, ClassEquipProfile.Unknown, null));
    }

    // ===== CanEquip — class allow-list =====

    [Fact]
    public void CanEquip_ClassRestriction_AllowsListedBlocksOthers()
    {
        GameRow doc = TestRows.Row("""{ "ClassRest-0": 3, "ClassRest-1": 7 }""");

        Assert.True(ItemEquipFilter.CanEquip(doc, level: 0, Cls(3), null));
        Assert.True(ItemEquipFilter.CanEquip(doc, level: 0, Cls(7), null));
        Assert.False(ItemEquipFilter.CanEquip(doc, level: 0, Cls(5), null));
    }

    [Fact]
    public void CanEquip_UnknownClass_SkipsClassFilter()
    {
        GameRow doc = TestRows.Row("""{ "ClassRest-0": 3 }""");

        // An Unknown class means the class name didn't resolve — don't hide the item.
        Assert.True(ItemEquipFilter.CanEquip(doc, level: 0, ClassEquipProfile.Unknown, null));
    }

    [Fact]
    public void CanEquip_AllDimensionsTogether_RequiresEachToPass()
    {
        // Level 10-20, good-only, class 3 only.
        GameRow doc = TestRows.Row(
            """{ "Abil-0": 135, "AbilVal-0": 10, "Abil-1": 136, "AbilVal-1": 20, "Abil-2": 97, "ClassRest-0": 3 }""");

        Assert.True(ItemEquipFilter.CanEquip(doc, level: 15, Cls(3), AlignmentBucket.Good));
        Assert.False(ItemEquipFilter.CanEquip(doc, level: 25, Cls(3), AlignmentBucket.Good)); // level
        Assert.False(ItemEquipFilter.CanEquip(doc, level: 15, Cls(5), AlignmentBucket.Good)); // class
        Assert.False(ItemEquipFilter.CanEquip(doc, level: 15, Cls(3), AlignmentBucket.Evil)); // alignment
    }

    // ===== CanEquip — weapon type gating =====

    [Fact]
    public void CanEquip_StaffClass_BlocksNonStaffWeaponAllowsDaggerAndQuarterstaff()
    {
        // The reported bug: a Mystic (WeaponType 9 = Staff) was offered an adamantite
        // longsword (WeaponType 2). A Staff class may only hold dagger (#68) or
        // quarterstaff (#100) without a class grant.
        ClassEquipProfile mystic = Cls(15, weaponType: 9, armourType: 1);

        GameRow longsword = TestRows.Row("""{ "Number": 1740, "ItemType": 1, "WeaponType": 2 }""");
        GameRow dagger = TestRows.Row("""{ "Number": 68, "ItemType": 1, "WeaponType": 2 }""");
        GameRow quarterstaff = TestRows.Row("""{ "Number": 100, "ItemType": 1, "WeaponType": 1 }""");

        Assert.False(ItemEquipFilter.CanEquip(longsword, level: 0, mystic, null));
        Assert.True(ItemEquipFilter.CanEquip(dagger, level: 0, mystic, null));
        Assert.True(ItemEquipFilter.CanEquip(quarterstaff, level: 0, mystic, null));
    }

    [Fact]
    public void CanEquip_Any1HClass_AcceptsOneHandRejectsTwoHand()
    {
        // WeaponType 4 = Any 1H → item WeaponType 0 (1H Blunt) or 2 (1H Sharp) only.
        ClassEquipProfile any1h = Cls(3, weaponType: 4);

        GameRow blunt1h = TestRows.Row("""{ "ItemType": 1, "WeaponType": 0 }""");
        GameRow sharp1h = TestRows.Row("""{ "ItemType": 1, "WeaponType": 2 }""");
        GameRow sharp2h = TestRows.Row("""{ "ItemType": 1, "WeaponType": 3 }""");

        Assert.True(ItemEquipFilter.CanEquip(blunt1h, level: 0, any1h, null));
        Assert.True(ItemEquipFilter.CanEquip(sharp1h, level: 0, any1h, null));
        Assert.False(ItemEquipFilter.CanEquip(sharp2h, level: 0, any1h, null));
    }

    [Fact]
    public void CanEquip_AllWeaponsClass_AcceptsEveryWeaponType()
    {
        ClassEquipProfile allWeapons = Cls(3, weaponType: 8);

        for (int wt = 0; wt <= 3; wt++)
        {
            GameRow weapon = TestRows.Row($$"""{ "ItemType": 1, "WeaponType": {{wt}} }""");
            Assert.True(ItemEquipFilter.CanEquip(weapon, level: 0, allWeapons, null));
        }
    }

    [Fact]
    public void CanEquip_ClassOkGrant_BypassesWeaponTypeGating()
    {
        // Same longsword, but Abil-59 grants class 15 — weapon type gating is skipped.
        ClassEquipProfile mystic = Cls(15, weaponType: 9, armourType: 1);
        GameRow granted = TestRows.Row(
            """{ "Number": 1740, "ItemType": 1, "WeaponType": 2, "Abil-0": 59, "AbilVal-0": 15 }""");

        Assert.True(ItemEquipFilter.CanEquip(granted, level: 0, mystic, null));
    }

    [Fact]
    public void CanEquip_ClassRestGrant_BypassesWeaponTypeGating()
    {
        // A ClassRest naming the class also grants it past the weapon type gate.
        ClassEquipProfile mystic = Cls(15, weaponType: 9, armourType: 1);
        GameRow granted = TestRows.Row(
            """{ "ItemType": 1, "WeaponType": 2, "ClassRest-0": 15 }""");

        Assert.True(ItemEquipFilter.CanEquip(granted, level: 0, mystic, null));
    }

    // ===== CanEquip — armour tier & shield =====

    [Fact]
    public void CanEquip_ArmourTier_BlocksAboveClassTierAllowsAtOrBelow()
    {
        // Class ArmourType is a ceiling: usable only when it meets the item's tier.
        ClassEquipProfile mystic = Cls(15, weaponType: 9, armourType: 1);

        GameRow plate = TestRows.Row("""{ "ItemType": 0, "ArmourType": 8 }""");
        GameRow robe = TestRows.Row("""{ "ItemType": 0, "ArmourType": 1 }""");

        Assert.False(ItemEquipFilter.CanEquip(plate, level: 0, mystic, null));
        Assert.True(ItemEquipFilter.CanEquip(robe, level: 0, mystic, null));
    }

    [Fact]
    public void CanEquip_OffHandShield_BarredToOneHandWeaponClassUnlessGranted()
    {
        GameRow shield = TestRows.Row("""{ "ItemType": 0, "ArmourType": 1, "Worn": 12 }""");

        // A one-hand-weapon class (WeaponType 4) can't take an off-hand shield without a grant.
        Assert.False(ItemEquipFilter.CanEquip(shield, level: 0, Cls(3, weaponType: 4, armourType: 9), null));

        // A two-hand-weapon class (WeaponType 5) isn't subject to the shield bar.
        Assert.True(ItemEquipFilter.CanEquip(shield, level: 0, Cls(3, weaponType: 5, armourType: 9), null));

        // A ClassRest grant bypasses the shield bar for the 1H class.
        GameRow granted = TestRows.Row(
            """{ "ItemType": 0, "ArmourType": 1, "Worn": 12, "ClassRest-0": 3 }""");
        Assert.True(ItemEquipFilter.CanEquip(granted, level: 0, Cls(3, weaponType: 4, armourType: 9), null));
    }

    // ===== CanEquip — anti-magic =====

    [Fact]
    public void CanEquip_AntiMagicClass_BlocksMagicalItemEvenWhenGranted()
    {
        // A magical item (Abil-28) granted to the class via ClassRest — an anti-magic
        // class (e.g. Witchunter) is still barred; a normal class can wear it.
        GameRow magical = TestRows.Row(
            """{ "ItemType": 0, "ArmourType": 1, "Abil-0": 28, "ClassRest-0": 20 }""");

        Assert.False(ItemEquipFilter.CanEquip(magical, level: 0, Cls(20, antiMagic: true), null));
        Assert.True(ItemEquipFilter.CanEquip(magical, level: 0, Cls(20), null));
    }

    // The Stock engine's gear check: Seedy wears as Neutral (its Evil gear bucket
    // starts at Outlaw) and not-Neutral (113) is never checked. Paradigm keeps both.
    [Fact]
    public void Stock_SeedyWearsAsNeutral_AndNotNeutralIsIgnored()
    {
        Assert.Equal(AlignmentBucket.Neutral, ItemEquipFilter.GearBucketForWord("Seedy", MudPlay.Game.RealmType.Stock));
        Assert.Equal(AlignmentBucket.Evil, ItemEquipFilter.GearBucketForWord("Seedy", MudPlay.Game.RealmType.ParaMud));
        Assert.Equal(AlignmentBucket.Evil, ItemEquipFilter.GearBucketForWord("Outlaw", MudPlay.Game.RealmType.Stock));

        GameRow notNeutral = TestRows.Row("""{ "Abil-0": 113 }""");
        Assert.True(ItemEquipFilter.CanEquip(notNeutral, 0, ClassEquipProfile.Unknown,
            AlignmentBucket.Neutral, MudPlay.Game.RealmType.Stock));
        Assert.False(ItemEquipFilter.CanEquip(notNeutral, 0, ClassEquipProfile.Unknown,
            AlignmentBucket.Neutral, MudPlay.Game.RealmType.ParaMud));
    }
}
