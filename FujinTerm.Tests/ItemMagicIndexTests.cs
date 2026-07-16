using System.IO;
using FujinTerm.Game.Combat;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="ItemMagicIndex"/> reads a weapon's magic-hit level as the SUM of
/// ability code 28 (Magical) + code 142 (HitMagic), matching the character
/// sheet's "Hit Magic" total. The regression these tests pin: an inherently
/// magical weapon carrying only code 28 (a "shimmering" longsword) must NOT
/// misread as level 0 — reading code 142 alone stranded such a weapon.
/// Uses an isolated temp root so fixtures never leak into real Data.
/// </summary>
public sealed class ItemMagicIndexTests : IDisposable
{
    private readonly string _root;

    public ItemMagicIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-imi-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private ItemMagicIndex NewIndex(string itemsJson)
    {
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Items.json"), itemsJson);

        GameDataCache cache = new(_root);
        cache.SwitchSet("set");
        return new ItemMagicIndex(cache);
    }

    [Fact]
    public void HitMagic_MagicalAbilityOnly_ReportsItsLevel()
    {
        // The shimmering-longsword regression: code 28 (Magical) val 1, no 142.
        // Must read level 1, not 0 — otherwise the walker strands itself
        // "un-actionable" against a monster the weapon could actually hit.
        ItemMagicIndex index = NewIndex(
            "[{\"Name\":\"shimmering longsword\",\"Abil-0\":28,\"AbilVal-0\":1}]");

        Assert.Equal(1, index.HitMagic("shimmering longsword"));
    }

    [Fact]
    public void HitMagic_HitMagicAbilityOnly_ReportsItsLevel()
    {
        ItemMagicIndex index = NewIndex(
            "[{\"Name\":\"blessed mace\",\"Abil-0\":142,\"AbilVal-0\":3}]");

        Assert.Equal(3, index.HitMagic("blessed mace"));
    }

    [Fact]
    public void HitMagic_BothAbilities_SumsThem()
    {
        // A weapon can carry Magical AND an explicit +HitMagic bonus; the char
        // sheet buckets 28+142 into one Hit Magic total, so the index sums too.
        ItemMagicIndex index = NewIndex(
            "[{\"Name\":\"runed blade\",\"Abil-0\":28,\"AbilVal-0\":2,"
            + "\"Abil-1\":142,\"AbilVal-1\":4}]");

        Assert.Equal(6, index.HitMagic("runed blade"));
    }

    [Fact]
    public void HitMagic_NoMagicAbility_ReturnsZero()
    {
        // A known weapon with neither ability is level 0 — it can only hit
        // non-magical monsters. Distinct from the -1 "unknown item" sentinel.
        ItemMagicIndex index = NewIndex(
            "[{\"Name\":\"iron dagger\",\"Abil-0\":31,\"AbilVal-0\":5}]");

        Assert.Equal(0, index.HitMagic("iron dagger"));
    }

    [Fact]
    public void HitMagic_UnknownItem_ReturnsNegativeOne()
    {
        // Fail-open sentinel: no row with that name → -1, so the caller treats
        // "no data" as "don't second-guess the configured weapon".
        ItemMagicIndex index = NewIndex(
            "[{\"Name\":\"iron dagger\"}]");

        Assert.Equal(-1, index.HitMagic("ghost weapon"));
    }

    [Fact]
    public void HitMagic_NullOrBlank_ReturnsNegativeOne()
    {
        ItemMagicIndex index = NewIndex("[]");

        Assert.Equal(-1, index.HitMagic(null));
        Assert.Equal(-1, index.HitMagic("   "));
    }

    [Fact]
    public void HitMagic_MatchIsCaseAndWhitespaceInsensitive()
    {
        ItemMagicIndex index = NewIndex(
            "[{\"Name\":\"Shimmering Longsword\",\"Abil-0\":28,\"AbilVal-0\":1}]");

        Assert.Equal(1, index.HitMagic("  shimmering longsword  "));
    }
}
