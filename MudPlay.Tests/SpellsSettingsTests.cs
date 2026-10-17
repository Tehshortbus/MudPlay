using System.Linq;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// SpellsSettings defaults and how its bless slots persist.
public sealed class SpellsSettingsTests
{
    [Fact]
    public void Defaults_AreSane()
    {
        SpellsSettings dto = new();

        // Category priority defaults are an ordered 1..10 fence — protects
        // CastingDirector's between-round dispatch against a future
        // accidental tie that would scramble cast order. Emergency leads, the
        // major heals go ahead of the minor ones, and Priority buffs sit between
        // the two self heals (user, 2026-10-06).
        Assert.Equal(1, dto.PriorityEmergencyHeal);
        Assert.Equal(2, dto.PriorityMajorPartyHeal);
        Assert.Equal(3, dto.PriorityMinorPartyHeal);
        Assert.Equal(4, dto.PriorityMajorSelfHeal);
        Assert.Equal(5, dto.PriorityPriorityBuffs);
        Assert.Equal(6, dto.PriorityMinorSelfHeal);
        Assert.Equal(7, dto.PriorityDownedAllyHeal);
        Assert.Equal(8, dto.PriorityCuring);
        Assert.Equal(9, dto.PriorityBuffing);
        Assert.Equal(10, dto.PriorityDebuffing);

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
        // user fills a slot, so a fresh profile serialises no bless data. The
        // per-slot recast leads are likewise absent until one is set off default.
        Assert.Empty(dto.BlessSlots);
        Assert.Empty(dto.BlessSlotRecastMargins);

        // Ailment-coordination toggles default UNCHECKED — most parties want to
        // pause (@wait) and broadcast (.@blind) on every ailment. Each Ignore* now
        // gates both.
        Assert.False(dto.IgnorePoison);
        Assert.False(dto.IgnoreBlindness);
        Assert.False(dto.IgnoreConfusion);
        Assert.False(dto.IgnoreDiseased);
    }

    [Fact]
    public void Defaults_PrioritiesAreStrictlyMonotonic()
    {
        SpellsSettings dto = new();
        int[] order =
        {
            dto.PriorityEmergencyHeal,
            dto.PriorityMajorPartyHeal,
            dto.PriorityMinorPartyHeal,
            dto.PriorityMajorSelfHeal,
            dto.PriorityPriorityBuffs,
            dto.PriorityMinorSelfHeal,
            dto.PriorityDownedAllyHeal,
            dto.PriorityCuring,
            dto.PriorityBuffing,
            dto.PriorityDebuffing,
        };
        for (int i = 1; i < order.Length; i++)
            Assert.True(order[i] > order[i - 1],
                $"priority slot {i} ({order[i]}) must be strictly greater than slot {i - 1} ({order[i - 1]})");
    }

    // ----- Bless-slot count policy + sparse persistence --------------

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
