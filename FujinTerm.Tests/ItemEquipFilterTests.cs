using System.Text.Json;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.Inventory;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the per-character equip predicate behind the Equipment Manager's item
/// search: the alignment-word → <see cref="AlignmentBucket"/> reduction and the
/// level / class / alignment / weapon-type / armour-tier
/// <see cref="ItemEquipFilter.CanEquip"/> gate (a faithful port of MMUD-Explorer's
/// <c>ItemIsUsableByChar</c>). Each player dimension must degrade gracefully — an
/// unknown level / class / alignment skips that filter rather than hiding gear — and
/// a class grant (item <c>Abil-59</c> ClassOk, or a matching <c>ClassRest</c>) must
/// bypass weapon / armour type gating while anti-magic still blocks magical gear.
/// </summary>
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
        using JsonDocument doc = JsonDocument.Parse("{}");
        Assert.True(ItemEquipFilter.CanEquip(doc.RootElement, level: 40, Cls(3), AlignmentBucket.Evil));
    }

    [Fact]
    public void CanEquip_MinLevel_BlocksBelowAllowsAtOrAbove()
    {
        using JsonDocument doc = JsonDocument.Parse("""{ "Abil-0": 135, "AbilVal-0": 10 }""");

        Assert.False(ItemEquipFilter.CanEquip(doc.RootElement, level: 5, ClassEquipProfile.Unknown, null));
        Assert.True(ItemEquipFilter.CanEquip(doc.RootElement, level: 10, ClassEquipProfile.Unknown, null));
        Assert.True(ItemEquipFilter.CanEquip(doc.RootElement, level: 25, ClassEquipProfile.Unknown, null));
    }

    [Fact]
    public void CanEquip_MaxLevel_BlocksAboveAllowsAtOrBelow()
    {
        using JsonDocument doc = JsonDocument.Parse("""{ "Abil-0": 136, "AbilVal-0": 20 }""");

        Assert.False(ItemEquipFilter.CanEquip(doc.RootElement, level: 25, ClassEquipProfile.Unknown, null));
        Assert.True(ItemEquipFilter.CanEquip(doc.RootElement, level: 20, ClassEquipProfile.Unknown, null));
    }

    [Fact]
    public void CanEquip_UnknownLevel_SkipsLevelFilter()
    {
        using JsonDocument doc = JsonDocument.Parse("""{ "Abil-0": 135, "AbilVal-0": 30 }""");

        // level <= 0 means the character hasn't been stat'd — don't hide the item.
        Assert.True(ItemEquipFilter.CanEquip(doc.RootElement, level: 0, ClassEquipProfile.Unknown, null));
    }

    // ===== CanEquip — alignment =====

    [Fact]
    public void CanEquip_GoodOnly_AllowsGoodBlocksEvil()
    {
        using JsonDocument doc = JsonDocument.Parse("""{ "Abil-0": 97 }""");

        Assert.True(ItemEquipFilter.CanEquip(doc.RootElement, level: 0, ClassEquipProfile.Unknown, AlignmentBucket.Good));
        Assert.False(ItemEquipFilter.CanEquip(doc.RootElement, level: 0, ClassEquipProfile.Unknown, AlignmentBucket.Evil));
    }

    [Fact]
    public void CanEquip_NotEvil_BlocksEvilAllowsGoodAndNeutral()
    {
        using JsonDocument doc = JsonDocument.Parse("""{ "Abil-0": 111 }""");

        Assert.False(ItemEquipFilter.CanEquip(doc.RootElement, level: 0, ClassEquipProfile.Unknown, AlignmentBucket.Evil));
        Assert.True(ItemEquipFilter.CanEquip(doc.RootElement, level: 0, ClassEquipProfile.Unknown, AlignmentBucket.Good));
        Assert.True(ItemEquipFilter.CanEquip(doc.RootElement, level: 0, ClassEquipProfile.Unknown, AlignmentBucket.Neutral));
    }

    [Fact]
    public void CanEquip_UnknownAlignment_SkipsAlignmentFilter()
    {
        using JsonDocument doc = JsonDocument.Parse("""{ "Abil-0": 97 }""");

        // A null bucket (no who-word seen) shouldn't hide an aligned item.
        Assert.True(ItemEquipFilter.CanEquip(doc.RootElement, level: 0, ClassEquipProfile.Unknown, null));
    }

    // ===== CanEquip — class allow-list =====

    [Fact]
    public void CanEquip_ClassRestriction_AllowsListedBlocksOthers()
    {
        using JsonDocument doc = JsonDocument.Parse("""{ "ClassRest-0": 3, "ClassRest-1": 7 }""");

        Assert.True(ItemEquipFilter.CanEquip(doc.RootElement, level: 0, Cls(3), null));
        Assert.True(ItemEquipFilter.CanEquip(doc.RootElement, level: 0, Cls(7), null));
        Assert.False(ItemEquipFilter.CanEquip(doc.RootElement, level: 0, Cls(5), null));
    }

    [Fact]
    public void CanEquip_UnknownClass_SkipsClassFilter()
    {
        using JsonDocument doc = JsonDocument.Parse("""{ "ClassRest-0": 3 }""");

        // An Unknown class means the class name didn't resolve — don't hide the item.
        Assert.True(ItemEquipFilter.CanEquip(doc.RootElement, level: 0, ClassEquipProfile.Unknown, null));
    }

    [Fact]
    public void CanEquip_AllDimensionsTogether_RequiresEachToPass()
    {
        // Level 10-20, good-only, class 3 only.
        using JsonDocument doc = JsonDocument.Parse(
            """{ "Abil-0": 135, "AbilVal-0": 10, "Abil-1": 136, "AbilVal-1": 20, "Abil-2": 97, "ClassRest-0": 3 }""");

        Assert.True(ItemEquipFilter.CanEquip(doc.RootElement, level: 15, Cls(3), AlignmentBucket.Good));
        Assert.False(ItemEquipFilter.CanEquip(doc.RootElement, level: 25, Cls(3), AlignmentBucket.Good)); // level
        Assert.False(ItemEquipFilter.CanEquip(doc.RootElement, level: 15, Cls(5), AlignmentBucket.Good)); // class
        Assert.False(ItemEquipFilter.CanEquip(doc.RootElement, level: 15, Cls(3), AlignmentBucket.Evil)); // alignment
    }

    // ===== CanEquip — weapon type gating =====

    [Fact]
    public void CanEquip_StaffClass_BlocksNonStaffWeaponAllowsDaggerAndQuarterstaff()
    {
        // The reported bug: a Mystic (WeaponType 9 = Staff) was offered an adamantite
        // longsword (WeaponType 2). A Staff class may only hold dagger (#68) or
        // quarterstaff (#100) without a class grant.
        ClassEquipProfile mystic = Cls(15, weaponType: 9, armourType: 1);

        using JsonDocument longsword = JsonDocument.Parse("""{ "Number": 1740, "ItemType": 1, "WeaponType": 2 }""");
        using JsonDocument dagger = JsonDocument.Parse("""{ "Number": 68, "ItemType": 1, "WeaponType": 2 }""");
        using JsonDocument quarterstaff = JsonDocument.Parse("""{ "Number": 100, "ItemType": 1, "WeaponType": 1 }""");

        Assert.False(ItemEquipFilter.CanEquip(longsword.RootElement, level: 0, mystic, null));
        Assert.True(ItemEquipFilter.CanEquip(dagger.RootElement, level: 0, mystic, null));
        Assert.True(ItemEquipFilter.CanEquip(quarterstaff.RootElement, level: 0, mystic, null));
    }

    [Fact]
    public void CanEquip_Any1HClass_AcceptsOneHandRejectsTwoHand()
    {
        // WeaponType 4 = Any 1H → item WeaponType 0 (1H Blunt) or 2 (1H Sharp) only.
        ClassEquipProfile any1h = Cls(3, weaponType: 4);

        using JsonDocument blunt1h = JsonDocument.Parse("""{ "ItemType": 1, "WeaponType": 0 }""");
        using JsonDocument sharp1h = JsonDocument.Parse("""{ "ItemType": 1, "WeaponType": 2 }""");
        using JsonDocument sharp2h = JsonDocument.Parse("""{ "ItemType": 1, "WeaponType": 3 }""");

        Assert.True(ItemEquipFilter.CanEquip(blunt1h.RootElement, level: 0, any1h, null));
        Assert.True(ItemEquipFilter.CanEquip(sharp1h.RootElement, level: 0, any1h, null));
        Assert.False(ItemEquipFilter.CanEquip(sharp2h.RootElement, level: 0, any1h, null));
    }

    [Fact]
    public void CanEquip_AllWeaponsClass_AcceptsEveryWeaponType()
    {
        ClassEquipProfile allWeapons = Cls(3, weaponType: 8);

        for (int wt = 0; wt <= 3; wt++)
        {
            using JsonDocument weapon = JsonDocument.Parse($$"""{ "ItemType": 1, "WeaponType": {{wt}} }""");
            Assert.True(ItemEquipFilter.CanEquip(weapon.RootElement, level: 0, allWeapons, null));
        }
    }

    [Fact]
    public void CanEquip_ClassOkGrant_BypassesWeaponTypeGating()
    {
        // Same longsword, but Abil-59 grants class 15 — weapon type gating is skipped.
        ClassEquipProfile mystic = Cls(15, weaponType: 9, armourType: 1);
        using JsonDocument granted = JsonDocument.Parse(
            """{ "Number": 1740, "ItemType": 1, "WeaponType": 2, "Abil-0": 59, "AbilVal-0": 15 }""");

        Assert.True(ItemEquipFilter.CanEquip(granted.RootElement, level: 0, mystic, null));
    }

    [Fact]
    public void CanEquip_ClassRestGrant_BypassesWeaponTypeGating()
    {
        // A ClassRest naming the class also grants it past the weapon type gate.
        ClassEquipProfile mystic = Cls(15, weaponType: 9, armourType: 1);
        using JsonDocument granted = JsonDocument.Parse(
            """{ "ItemType": 1, "WeaponType": 2, "ClassRest-0": 15 }""");

        Assert.True(ItemEquipFilter.CanEquip(granted.RootElement, level: 0, mystic, null));
    }

    // ===== CanEquip — armour tier & shield =====

    [Fact]
    public void CanEquip_ArmourTier_BlocksAboveClassTierAllowsAtOrBelow()
    {
        // Class ArmourType is a ceiling: usable only when it meets the item's tier.
        ClassEquipProfile mystic = Cls(15, weaponType: 9, armourType: 1);

        using JsonDocument plate = JsonDocument.Parse("""{ "ItemType": 0, "ArmourType": 8 }""");
        using JsonDocument robe = JsonDocument.Parse("""{ "ItemType": 0, "ArmourType": 1 }""");

        Assert.False(ItemEquipFilter.CanEquip(plate.RootElement, level: 0, mystic, null));
        Assert.True(ItemEquipFilter.CanEquip(robe.RootElement, level: 0, mystic, null));
    }

    [Fact]
    public void CanEquip_OffHandShield_BarredToOneHandWeaponClassUnlessGranted()
    {
        using JsonDocument shield = JsonDocument.Parse("""{ "ItemType": 0, "ArmourType": 1, "Worn": 12 }""");

        // A one-hand-weapon class (WeaponType 4) can't take an off-hand shield without a grant.
        Assert.False(ItemEquipFilter.CanEquip(shield.RootElement, level: 0, Cls(3, weaponType: 4, armourType: 9), null));

        // A two-hand-weapon class (WeaponType 5) isn't subject to the shield bar.
        Assert.True(ItemEquipFilter.CanEquip(shield.RootElement, level: 0, Cls(3, weaponType: 5, armourType: 9), null));

        // A ClassRest grant bypasses the shield bar for the 1H class.
        using JsonDocument granted = JsonDocument.Parse(
            """{ "ItemType": 0, "ArmourType": 1, "Worn": 12, "ClassRest-0": 3 }""");
        Assert.True(ItemEquipFilter.CanEquip(granted.RootElement, level: 0, Cls(3, weaponType: 4, armourType: 9), null));
    }

    // ===== CanEquip — anti-magic =====

    [Fact]
    public void CanEquip_AntiMagicClass_BlocksMagicalItemEvenWhenGranted()
    {
        // A magical item (Abil-28) granted to the class via ClassRest — an anti-magic
        // class (e.g. Witchunter) is still barred; a normal class can wear it.
        using JsonDocument magical = JsonDocument.Parse(
            """{ "ItemType": 0, "ArmourType": 1, "Abil-0": 28, "ClassRest-0": 20 }""");

        Assert.False(ItemEquipFilter.CanEquip(magical.RootElement, level: 0, Cls(20, antiMagic: true), null));
        Assert.True(ItemEquipFilter.CanEquip(magical.RootElement, level: 0, Cls(20), null));
    }
}
