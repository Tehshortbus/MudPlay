using System.Collections.Generic;
using MudPlay.Game.GameData;
using MudPlay.Models.GameData;
using Xunit;

namespace MudPlay.Tests;

// Pins the pure ability-code → cured-ailment map at the heart of CureSpellIndex.
// The codes are verified against real stock + paradigm Spells data (see the class
// header): CurePoison(20), Freedom(81), DispellMagic(73) targeting the apply code,
// and RemovesSpell(122) targeting a disease-apply spell.
public sealed class CureSpellIndexTests
{
    private static readonly IReadOnlySet<int> NoDisease = new HashSet<int>();

    private static MessageFlags Cured(IReadOnlyList<(int, int)> abilities, IReadOnlySet<int>? disease = null)
        => CureSpellIndex.CuredFlags(abilities, disease ?? NoDisease);

    [Fact]
    public void CurePoisonCode_CuresPoison()
        => Assert.Equal(MessageFlags.Poisoned, Cured(new[] { (20, 0) }));

    [Fact]
    public void FreedomCode_CuresHold()
        => Assert.Equal(MessageFlags.MovementPrevented, Cured(new[] { (81, 0) }));

    [Theory]
    [InlineData(107)] // BlindUser
    [InlineData(53)]  // BlindingLight
    public void DispellMagic_TargetingBlindApply_CuresBlind(int target)
        => Assert.Equal(MessageFlags.Blinded, Cured(new[] { (73, target) }));

    [Theory]
    [InlineData(74)] // HoldPerson
    [InlineData(75)] // Paralyze
    public void DispellMagic_TargetingHoldApply_CuresHold(int target)
        => Assert.Equal(MessageFlags.MovementPrevented, Cured(new[] { (73, target) }));

    [Fact]
    public void DispellMagic_TargetingPoisonApply_CuresPoison()
        => Assert.Equal(MessageFlags.Poisoned, Cured(new[] { (73, 19) }));

    [Fact]
    public void RemovesSpell_TargetingDiseaseApply_CuresDisease()
        => Assert.Equal(MessageFlags.Diseased,
            Cured(new[] { (122, 323) }, new HashSet<int> { 323, 445 }));

    // A yeti's death spell "roar cancel" (#1368) removes the roars (#193, #1250), which
    // carry HoldPerson: it ends the hold they put on the room.
    [Fact]
    public void RemovesSpell_TargetingHoldApply_CuresHold()
        => Assert.Equal(MessageFlags.MovementPrevented,
            CureSpellIndex.CuredFlags(new[] { (122, 193), (122, 1250) }, NoDisease, new HashSet<int> { 193, 1250 }));

    [Fact]
    public void RemovesSpell_TargetingNonDiseaseSpell_CuresNothing()
        => Assert.Equal(MessageFlags.None,
            Cured(new[] { (122, 794) }, new HashSet<int> { 323, 445 }));

    [Fact]
    public void HealPlusCure_RegistersAsPoisonCure()
        // curing wind / merciful grace: Heal(18) + CurePoison(20).
        => Assert.Equal(MessageFlags.Poisoned, Cured(new[] { (18, 0), (20, 100) }));

    [Theory]
    // A DIRECT apply code is NOT a cure — only the DispellMagic/RemovesSpell
    // indirection is. This is what separates "blind" (BlindUser direct) from
    // "cure blindness" (DispellMagic targeting BlindUser).
    [InlineData(107)] // BlindUser — apply blind
    [InlineData(74)]  // HoldPerson — apply hold
    [InlineData(19)]  // Poison — apply poison
    public void DirectApplyCode_CuresNothing(int applyCode)
        => Assert.Equal(MessageFlags.None, Cured(new[] { (applyCode, 0) }));

    [Fact]
    public void RealCurePoisonEncoding_CuresPoisonOnly()
        // Actual stock cure poison: CurePoison(20), DispellMagic(73)=19 (Poison),
        // RemovesSpell(122)=794/798 (poison DoTs, not in the disease set).
        => Assert.Equal(MessageFlags.Poisoned,
            Cured(new[] { (20, 0), (73, 19), (122, 794), (122, 798) }, new HashSet<int> { 323 }));

    [Fact]
    public void NoRelevantCodes_CuresNothing()
        => Assert.Equal(MessageFlags.None, Cured(new[] { (18, 0), (22, -8) }));
}
