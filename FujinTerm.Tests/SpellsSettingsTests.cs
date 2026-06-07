using System.Text.Json;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.0a sub-A — <see cref="SpellsSettings"/> defaults + JSON round-trip.
/// </summary>
public sealed class SpellsSettingsTests
{
    [Fact]
    public void Defaults_AreSane()
    {
        SpellsSettings dto = new();

        // Category priority defaults are an ordered 1..7 fence — protects
        // CastingDirector's between-round dispatch against a future
        // accidental tie that would scramble cast order.
        Assert.Equal(1, dto.PriorityMinorPartyHeal);
        Assert.Equal(2, dto.PriorityMajorPartyHeal);
        Assert.Equal(3, dto.PriorityMinorSelfHeal);
        Assert.Equal(4, dto.PriorityMajorSelfHeal);
        Assert.Equal(5, dto.PriorityCuring);
        Assert.Equal(6, dto.PriorityBuffing);
        Assert.Equal(7, dto.PriorityDebuffing);

        // All spell-name slots empty by default (user configures per character).
        Assert.Null(dto.MinorHealSpell);
        Assert.Null(dto.MajorHealSpell);
        Assert.Null(dto.HpRegenSpell);
        Assert.Null(dto.MaRegenSpell);
        Assert.Null(dto.WhenHpFullSpell);
        Assert.Null(dto.WhenMaFullSpell);

        Assert.Null(dto.CureHoldsSpell);
        Assert.Null(dto.CurePoisonSpell);
        Assert.Null(dto.CureDiseaseSpell);
        Assert.Null(dto.CureBlindnessSpell);

        Assert.Null(dto.RoomLightSpell);

        Assert.Null(dto.Bless1Spell);
        Assert.Null(dto.Bless5Spell);
        Assert.Null(dto.Bless10Spell);
    }

    [Fact]
    public void Defaults_PrioritiesAreStrictlyMonotonic()
    {
        SpellsSettings dto = new();
        int[] order =
        {
            dto.PriorityMinorPartyHeal,
            dto.PriorityMajorPartyHeal,
            dto.PriorityMinorSelfHeal,
            dto.PriorityMajorSelfHeal,
            dto.PriorityCuring,
            dto.PriorityBuffing,
            dto.PriorityDebuffing,
        };
        for (int i = 1; i < order.Length; i++)
            Assert.True(order[i] > order[i - 1],
                $"priority slot {i} ({order[i]}) must be strictly greater than slot {i - 1} ({order[i - 1]})");
    }

    [Fact]
    public void JsonRoundTrip_PreservesEveryField()
    {
        SpellsSettings dto = new()
        {
            PriorityMinorPartyHeal = 5,
            PriorityMajorPartyHeal = 1,
            PriorityMinorSelfHeal  = 6,
            PriorityMajorSelfHeal  = 2,
            PriorityCuring         = 3,
            PriorityBuffing        = 7,
            PriorityDebuffing      = 4,

            MinorHealSpell     = "cure-light-wounds",
            MajorHealSpell     = "cure-critical-wounds",
            HpRegenSpell       = "troll-skin",
            MaRegenSpell       = "kindred-spirit",
            WhenHpFullSpell    = "warcry",
            WhenMaFullSpell    = "ancient-curse",

            CureHoldsSpell     = "freedom",
            CurePoisonSpell    = "antidote",
            CureDiseaseSpell   = "purify",
            CureBlindnessSpell = "vision",

            RoomLightSpell     = "light",

            Bless1Spell  = "bless",
            Bless2Spell  = "shield",
            Bless3Spell  = "haste",
            Bless4Spell  = "armor",
            Bless5Spell  = "resist-fire",
            Bless6Spell  = "resist-cold",
            Bless7Spell  = "true-seeing",
            Bless8Spell  = "stoneskin",
            Bless9Spell  = "fly",
            Bless10Spell = "guardian",
        };

        string json = JsonSerializer.Serialize(dto);
        SpellsSettings? round = JsonSerializer.Deserialize<SpellsSettings>(json);

        Assert.NotNull(round);
        Assert.Equal(dto.PriorityMinorPartyHeal, round!.PriorityMinorPartyHeal);
        Assert.Equal(dto.PriorityMajorPartyHeal, round.PriorityMajorPartyHeal);
        Assert.Equal(dto.PriorityMinorSelfHeal,  round.PriorityMinorSelfHeal);
        Assert.Equal(dto.PriorityMajorSelfHeal,  round.PriorityMajorSelfHeal);
        Assert.Equal(dto.PriorityCuring,         round.PriorityCuring);
        Assert.Equal(dto.PriorityBuffing,        round.PriorityBuffing);
        Assert.Equal(dto.PriorityDebuffing,      round.PriorityDebuffing);

        Assert.Equal(dto.MinorHealSpell,     round.MinorHealSpell);
        Assert.Equal(dto.MajorHealSpell,     round.MajorHealSpell);
        Assert.Equal(dto.HpRegenSpell,       round.HpRegenSpell);
        Assert.Equal(dto.MaRegenSpell,       round.MaRegenSpell);
        Assert.Equal(dto.WhenHpFullSpell,    round.WhenHpFullSpell);
        Assert.Equal(dto.WhenMaFullSpell,    round.WhenMaFullSpell);

        Assert.Equal(dto.CureHoldsSpell,     round.CureHoldsSpell);
        Assert.Equal(dto.CurePoisonSpell,    round.CurePoisonSpell);
        Assert.Equal(dto.CureDiseaseSpell,   round.CureDiseaseSpell);
        Assert.Equal(dto.CureBlindnessSpell, round.CureBlindnessSpell);

        Assert.Equal(dto.RoomLightSpell,     round.RoomLightSpell);

        Assert.Equal(dto.Bless1Spell,  round.Bless1Spell);
        Assert.Equal(dto.Bless2Spell,  round.Bless2Spell);
        Assert.Equal(dto.Bless3Spell,  round.Bless3Spell);
        Assert.Equal(dto.Bless4Spell,  round.Bless4Spell);
        Assert.Equal(dto.Bless5Spell,  round.Bless5Spell);
        Assert.Equal(dto.Bless6Spell,  round.Bless6Spell);
        Assert.Equal(dto.Bless7Spell,  round.Bless7Spell);
        Assert.Equal(dto.Bless8Spell,  round.Bless8Spell);
        Assert.Equal(dto.Bless9Spell,  round.Bless9Spell);
        Assert.Equal(dto.Bless10Spell, round.Bless10Spell);
    }
}
