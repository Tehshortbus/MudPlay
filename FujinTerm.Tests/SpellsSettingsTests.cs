using System.Linq;
using System.Text.Json;
using FujinTerm.Game;
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

        // Mana-regen reroll is inert by default: a null threshold disables
        // rerolling so nothing fires until the user sets one; cap defaults to 3.
        Assert.Null(dto.ManaRegenRerollThreshold);
        Assert.Equal(3, dto.ManaRegenRerollCap);

        Assert.Null(dto.CureHoldsSpell);
        Assert.Null(dto.CurePoisonSpell);
        Assert.Null(dto.CureDiseaseSpell);
        Assert.Null(dto.CureBlindnessSpell);

        Assert.Null(dto.RoomLightSpell);

        // Bless slots start empty — the sparse map has no entries until the
        // user fills a slot, so a fresh profile serialises no bless data.
        Assert.Empty(dto.BlessSlots);

        // Ailment-coordination toggles default UNCHECKED — most parties want
        // to pause (@wait) and broadcast (.@poisoned) on every ailment.
        Assert.False(dto.IgnorePoison);
        Assert.False(dto.IgnoreBlindness);
        Assert.False(dto.IgnoreConfusion);
        Assert.False(dto.IgnoreDiseased);
        Assert.False(dto.DoNotAnnouncePoison);
        Assert.False(dto.DoNotAnnounceBlindness);
        Assert.False(dto.DoNotAnnounceConfusion);
        Assert.False(dto.DoNotAnnounceDiseased);
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

            ManaRegenRerollThreshold = -5,   // negative rolls are valid (mana flux subtracts)
            ManaRegenRerollCap       = 4,

            CureHoldsSpell     = "freedom",
            CurePoisonSpell    = "antidote",
            CureDiseaseSpell   = "purify",
            CureBlindnessSpell = "vision",

            RoomLightSpell     = "light",

            BlessSlots = new()
            {
                [1]  = "bless",       [2] = "shield",     [3] = "haste",
                [4]  = "armor",       [5] = "resist-fire", [6] = "resist-cold",
                [7]  = "true-seeing", [8] = "stoneskin",  [9] = "fly",
                [10] = "guardian",    [13] = "sanctuary", // a sparse gap + a beyond-10 slot
            },

            IgnorePoison           = true,
            IgnoreBlindness        = true,
            IgnoreConfusion        = true,
            IgnoreDiseased         = true,
            DoNotAnnouncePoison    = true,
            DoNotAnnounceBlindness = true,
            DoNotAnnounceConfusion = true,
            DoNotAnnounceDiseased  = true,
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

        Assert.Equal(dto.ManaRegenRerollThreshold, round.ManaRegenRerollThreshold);
        Assert.Equal(dto.ManaRegenRerollCap,       round.ManaRegenRerollCap);

        Assert.Equal(dto.CureHoldsSpell,     round.CureHoldsSpell);
        Assert.Equal(dto.CurePoisonSpell,    round.CurePoisonSpell);
        Assert.Equal(dto.CureDiseaseSpell,   round.CureDiseaseSpell);
        Assert.Equal(dto.CureBlindnessSpell, round.CureBlindnessSpell);

        Assert.Equal(dto.RoomLightSpell,     round.RoomLightSpell);

        Assert.Equal(dto.BlessSlots, round.BlessSlots);

        Assert.Equal(dto.IgnorePoison,           round.IgnorePoison);
        Assert.Equal(dto.IgnoreBlindness,        round.IgnoreBlindness);
        Assert.Equal(dto.IgnoreConfusion,        round.IgnoreConfusion);
        Assert.Equal(dto.IgnoreDiseased,         round.IgnoreDiseased);
        Assert.Equal(dto.DoNotAnnouncePoison,    round.DoNotAnnouncePoison);
        Assert.Equal(dto.DoNotAnnounceBlindness, round.DoNotAnnounceBlindness);
        Assert.Equal(dto.DoNotAnnounceConfusion, round.DoNotAnnounceConfusion);
        Assert.Equal(dto.DoNotAnnounceDiseased,  round.DoNotAnnounceDiseased);
    }

    // ----- Bless-slot count policy + sparse persistence --------------

    [Theory]
    [InlineData(RealmType.Stock, SpellsSettings.StockBlessSlotCount)]
    [InlineData(RealmType.ParaMud, SpellsSettings.ParaMudBlessSlotCount)]
    public void BlessSlotCountFor_MatchesRealmCap(RealmType realm, int expected)
        => Assert.Equal(expected, SpellsSettings.BlessSlotCountFor(realm));

    [Fact]
    public void BlessSlotCounts_StockTenParaMudFifteen()
    {
        Assert.Equal(10, SpellsSettings.StockBlessSlotCount);
        Assert.Equal(15, SpellsSettings.ParaMudBlessSlotCount);
    }

    [Fact]
    public void BlessSlots_SerializeSparsely_NoGhostSlots()
    {
        SpellsSettings dto = new();
        dto.BlessSlots[1]  = "bles";
        dto.BlessSlots[3]  = "prot";
        dto.BlessSlots[12] = "shie";   // a ParaMud-only slot

        string json = JsonSerializer.Serialize(dto);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement slots = doc.RootElement.GetProperty("BlessSlots");

        // Only the three filled slots persist — no null-valued ghost keys for
        // the empty 2 / 4-11 / 13-15 positions.
        Assert.Equal(3, slots.EnumerateObject().Count());
        Assert.Equal("bles", slots.GetProperty("1").GetString());
        Assert.Equal("prot", slots.GetProperty("3").GetString());
        Assert.Equal("shie", slots.GetProperty("12").GetString());
        Assert.False(slots.TryGetProperty("2", out _));
    }

    [Fact]
    public void BlessSlots_RoundTrip_PreservesOutOfRangeSlots()
    {
        // A pick in slot 15 (only visible on a 15-slot ParaMud realm) must
        // survive a round-trip so a profile stays portable when loaded on a
        // 10-slot Stock realm and back.
        SpellsSettings original = new();
        original.BlessSlots[2]  = "bles";
        original.BlessSlots[15] = "gsha";

        string json = JsonSerializer.Serialize(original);
        SpellsSettings restored = JsonSerializer.Deserialize<SpellsSettings>(json)!;

        Assert.Equal("bles", restored.BlessSlots[2]);
        Assert.Equal("gsha", restored.BlessSlots[15]);
        Assert.Equal(2, restored.BlessSlots.Count);
    }
}
