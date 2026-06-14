using FujinTerm.Game.GameData;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Spot-check tests for <see cref="LookupEnums"/>: every formatter accepts
/// a raw string from the game-data JSON, parses it, and returns the
/// MMUD-Explorer-compatible label. Unknown codes surface as
/// <c>Unknown(N)</c> so missing entries are visible rather than silent.
/// </summary>
public sealed class LookupEnumsTests
{
    [Theory]
    [InlineData("0",  "Armour")]
    [InlineData("1",  "Weapon")]
    [InlineData("8",  "Container")]
    [InlineData("10", "Special")]
    public void FormatItemType_MapsKnown(string raw, string expected)
        => Assert.Equal(expected, LookupEnums.FormatItemType(raw));

    [Theory]
    [InlineData("2",  "Head")]
    [InlineData("5",  "Feet")]
    [InlineData("11", "Torso")]
    [InlineData("14", "Wrist")]
    public void FormatWornSlot_MapsKnown(string raw, string expected)
        => Assert.Equal(expected, LookupEnums.FormatWornSlot(raw));

    [Theory]
    [InlineData("0", "1H Blunt")]
    [InlineData("2", "1H Sharp")]
    [InlineData("3", "2H Sharp")]
    public void FormatWeaponType_MapsKnown(string raw, string expected)
        => Assert.Equal(expected, LookupEnums.FormatWeaponType(raw));

    [Theory]
    [InlineData("0", "Natural")]
    [InlineData("3", "Leather")]
    [InlineData("6", "Leather")]
    [InlineData("9", "Platemail")]
    public void FormatArmourType_MapsKnown(string raw, string expected)
        => Assert.Equal(expected, LookupEnums.FormatArmourType(raw));

    [Theory]
    [InlineData("0", "Copper")]
    [InlineData("4", "Runic")]
    public void FormatCurrency_MapsKnown(string raw, string expected)
        => Assert.Equal(expected, LookupEnums.FormatCurrency(raw));

    [Theory]
    [InlineData("0", "User")]
    [InlineData("4", "Monster")]
    [InlineData("13", "Full Party Area")]
    public void FormatSpellTargets_MapsKnown(string raw, string expected)
        => Assert.Equal(expected, LookupEnums.FormatSpellTargets(raw));

    [Theory]
    [InlineData("0", "Cold")]
    [InlineData("6", "Poison")]
    public void FormatSpellAttackType_MapsKnown(string raw, string expected)
        => Assert.Equal(expected, LookupEnums.FormatSpellAttackType(raw));

    [Theory]
    [InlineData("7",  "Bank")]
    [InlineData("0",  "General")]
    [InlineData("12", "Deed Shop")]
    public void FormatShopType_MapsKnown(string raw, string expected)
        => Assert.Equal(expected, LookupEnums.FormatShopType(raw));

    [Theory]
    [InlineData("0", "Solo")]
    [InlineData("3", "Stationary")]
    public void FormatMonType_MapsKnown(string raw, string expected)
        => Assert.Equal(expected, LookupEnums.FormatMonType(raw));

    [Theory]
    [InlineData("0", "Good")]
    [InlineData("4", "Lawful Good")]
    [InlineData("6", "Lawful Evil")]
    public void FormatMonAlignment_MapsKnown(string raw, string expected)
        => Assert.Equal(expected, LookupEnums.FormatMonAlignment(raw));

    [Theory]
    [InlineData("0", "None")]
    [InlineData("1", "Mage")]
    [InlineData("5", "Kai")]
    public void FormatMagery_MapsKnown(string raw, string expected)
        => Assert.Equal(expected, LookupEnums.FormatMagery(raw));

    [Theory]
    [InlineData("0", "1H Blunt")]
    [InlineData("8", "Any Weapon")]
    [InlineData("9", "Staff")]
    public void FormatClassWeaponType_MapsKnown(string raw, string expected)
        => Assert.Equal(expected, LookupEnums.FormatClassWeaponType(raw));

    [Fact]
    public void Format_Unknown_ReturnsUnknownWithCode()
        => Assert.Equal("Unknown(99)", LookupEnums.FormatItemType("99"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    public void Format_NonNumeric_ReturnsInputUnchanged(string? raw)
        => Assert.Equal(raw, LookupEnums.FormatItemType(raw));

    [Fact]
    public void FormatAbilityCode_Zero_RendersAsEmpty()
        => Assert.Equal(string.Empty, LookupEnums.FormatAbilityCode("0"));

    [Fact]
    public void FormatAbilityCode_Known_RendersName()
        => Assert.Equal("AC", LookupEnums.FormatAbilityCode("2"));

    [Fact]
    public void FormatAbilityValue_Zero_RendersAsEmpty()
        => Assert.Equal(string.Empty, LookupEnums.FormatAbilityValue("0"));

    [Fact]
    public void FormatAbilityValue_Nonzero_RendersUnchanged()
        => Assert.Equal("15", LookupEnums.FormatAbilityValue("15"));
}
