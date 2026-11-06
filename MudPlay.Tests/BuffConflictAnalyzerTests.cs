using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// BuffAffectSet.CanCoLand answers "could these two buff slots ever land on the same
// character" — the precondition for a RemovesSpell relation between their spells to
// matter. Pure given the raw targeting flags, no live party/spellbook wiring needed.
public sealed class BuffConflictAnalyzerTests
{
    private static BuffAffectSet SelfOnly() =>
        BuffAffectSet.From(isWholePartySpell: false, wholePartyOn: false, castOnSelf: true, allMembers: false, targets: System.Array.Empty<string>());

    private static BuffAffectSet WholeParty(bool on = true) =>
        BuffAffectSet.From(isWholePartySpell: true, wholePartyOn: on, castOnSelf: false, allMembers: false, targets: System.Array.Empty<string>());

    private static BuffAffectSet Members(params string[] names) =>
        BuffAffectSet.From(isWholePartySpell: false, wholePartyOn: false, castOnSelf: false, allMembers: false, targets: names);

    private static BuffAffectSet AllMembers() =>
        BuffAffectSet.From(isWholePartySpell: false, wholePartyOn: false, castOnSelf: false, allMembers: true, targets: System.Array.Empty<string>());

    private static BuffAffectSet Inert() =>
        BuffAffectSet.From(isWholePartySpell: false, wholePartyOn: false, castOnSelf: false, allMembers: false, targets: System.Array.Empty<string>());

    [Fact]
    public void TwoSelfCastSlots_CoLand()
    {
        Assert.True(BuffConflictAnalyzer.CanCoLand(SelfOnly(), SelfOnly()));
    }

    [Fact]
    public void SelfCastAndWholeParty_CoLand()
    {
        // Whole-party always lands on self too — this is the existing SelfBuffCoverage case.
        Assert.True(BuffConflictAnalyzer.CanCoLand(SelfOnly(), WholeParty()));
    }

    [Fact]
    public void ToggledOffWholeParty_NeverCoLands()
    {
        Assert.False(BuffConflictAnalyzer.CanCoLand(SelfOnly(), WholeParty(on: false)));
    }

    [Fact]
    public void TwoWholePartySlots_CoLand()
    {
        Assert.True(BuffConflictAnalyzer.CanCoLand(WholeParty(), WholeParty()));
    }

    [Fact]
    public void OverlappingNamedMembers_CoLand()
    {
        Assert.True(BuffConflictAnalyzer.CanCoLand(Members("aragorn", "gimli"), Members("gimli", "legolas")));
    }

    [Fact]
    public void DisjointNamedMembers_DoNotCoLand()
    {
        Assert.False(BuffConflictAnalyzer.CanCoLand(Members("aragorn"), Members("legolas")));
    }

    [Fact]
    public void AllMembersVersusNamedMember_CoLand()
    {
        Assert.True(BuffConflictAnalyzer.CanCoLand(AllMembers(), Members("legolas")));
    }

    [Fact]
    public void AllMembersVersusSelfOnly_DoNotCoLand()
    {
        // AllMembers never implies self unless CastOnSelf is also set.
        Assert.False(BuffConflictAnalyzer.CanCoLand(AllMembers(), SelfOnly()));
    }

    [Fact]
    public void InertSlot_NeverCoLandsWithAnything()
    {
        Assert.False(BuffConflictAnalyzer.CanCoLand(Inert(), WholeParty()));
        Assert.False(BuffConflictAnalyzer.CanCoLand(Inert(), SelfOnly()));
    }

    [Fact]
    public void Resolve_FindsBothDirections()
    {
        List<BuffOverwritePair> pairs = new()
        {
            new BuffOverwritePair("chan", "Chant", "bles", "Bless"),
            new BuffOverwritePair("bles", "Bless", "pois", "Poison Ward"),
        };

        (string? removedBy, string? removes) = BuffConflictAnalyzer.Resolve(pairs, "bles");
        Assert.Equal("Chant", removedBy);
        Assert.Equal("Poison Ward", removes);
    }

    [Fact]
    public void Resolve_NoMatch_ReturnsNulls()
    {
        (string? removedBy, string? removes) = BuffConflictAnalyzer.Resolve(new List<BuffOverwritePair>(), "bles");
        Assert.Null(removedBy);
        Assert.Null(removes);
    }

    [Fact]
    public void FormatTooltip_CombinesBothDirections()
    {
        Assert.Equal("Removed by: Chant\nRemoves: Poison Ward", BuffConflictAnalyzer.FormatTooltip("Chant", "Poison Ward"));
        Assert.Equal("Removed by: Chant", BuffConflictAnalyzer.FormatTooltip("Chant", null));
        Assert.Null(BuffConflictAnalyzer.FormatTooltip(null, null));
    }

    [Fact]
    public void RemovedSpellNumbers_ExtractsOnlyAbil122Values()
    {
        SpellFormulaInput formula = new()
        {
            Abilities = new[]
            {
                new SpellAbility(Code: 4, Value: 0),
                new SpellAbility(Code: 122, Value: 5581),
                new SpellAbility(Code: 122, Value: 347),
                new SpellAbility(Code: 58, Value: 0),
            },
        };
        Assert.Equal(new HashSet<int> { 5581, 347 }, BuffConflictAnalyzer.RemovedSpellNumbers(formula));
    }

    // Confirmed Paradigm data (Spells #304 "zeal" / #5581 "greater zeal"): each
    // carries an Abil-122 RemovesSpell pointing at the other, so casting either one
    // strips the other's timer — "Add all blesses" must only ever offer one.
    private static SelfBlessCandidate Zeal() => new("zeal", "zeal", 304, ReqLevel: 25, ManaCost: 20, Removes: new[] { 5581 });
    private static SelfBlessCandidate GreaterZeal() => new("grze", "greater zeal", 5581, ReqLevel: 44, ManaCost: 28, Removes: new[] { 304 });

    [Fact]
    public void SelectSelfBlessCandidates_MutualFamily_BothListed_OnlyHighestReqLevelRecommended()
    {
        List<SelfBlessPick> result = BuffConflictAnalyzer
            .SelectSelfBlessCandidates(new[] { Zeal(), GreaterZeal() }, Array.Empty<ExistingBuffSlot>())
            .ToList();

        // Every family member gets a row — nothing is dropped — but only the
        // highest-ReqLevel one is pre-checked, so the player can still switch to
        // the other by hand.
        Assert.Equal(2, result.Count);
        Assert.True(result.Single(p => p.Candidate.Name == "greater zeal").Recommended);
        Assert.False(result.Single(p => p.Candidate.Name == "zeal").Recommended);
    }

    [Fact]
    public void SelectSelfBlessCandidates_UnlearnedHigherReqLevelMember_NeverRecommended()
    {
        // The class's full roster is browsable regardless of what's actually been
        // trained — but the tie-break must never auto-check something the game
        // would refuse to cast. Greater zeal isn't learned; zeal is.
        SelfBlessCandidate unlearnedGreaterZeal = GreaterZeal() with { IsObtained = false };
        SelfBlessCandidate learnedZeal = Zeal() with { IsObtained = true };

        List<SelfBlessPick> result = BuffConflictAnalyzer
            .SelectSelfBlessCandidates(new[] { learnedZeal, unlearnedGreaterZeal }, Array.Empty<ExistingBuffSlot>())
            .ToList();

        Assert.Equal(2, result.Count);
        Assert.True(result.Single(p => p.Candidate.Name == "zeal").Recommended);
        Assert.False(result.Single(p => p.Candidate.Name == "greater zeal").Recommended);
    }

    [Fact]
    public void SelectSelfBlessCandidates_NoObtainedMemberInFamily_ListedButNoneRecommended()
    {
        SelfBlessCandidate unlearnedZeal = Zeal() with { IsObtained = false };
        SelfBlessCandidate unlearnedGreaterZeal = GreaterZeal() with { IsObtained = false };

        List<SelfBlessPick> result = BuffConflictAnalyzer
            .SelectSelfBlessCandidates(new[] { unlearnedZeal, unlearnedGreaterZeal }, Array.Empty<ExistingBuffSlot>())
            .ToList();

        Assert.Equal(2, result.Count);
        Assert.All(result, p => Assert.False(p.Recommended));
    }

    [Fact]
    public void SelectSelfBlessCandidates_OrderedByReqLevel_NotAlphabetically()
    {
        // Report: a level-50 pick ("dark flagellation") landed alphabetically
        // between two low-level ones instead of at the end where it belongs.
        SelfBlessCandidate darkFlagellation = new("dfla", "dark flagellation", 900, ReqLevel: 50, ManaCost: 40, Removes: Array.Empty<int>());
        SelfBlessCandidate bless = new("bles", "bless", 100, ReqLevel: 2, ManaCost: 4, Removes: Array.Empty<int>());
        SelfBlessCandidate ironFaith = new("irfa", "iron faith", 200, ReqLevel: 20, ManaCost: 15, Removes: Array.Empty<int>());

        List<SelfBlessPick> result = BuffConflictAnalyzer
            .SelectSelfBlessCandidates(new[] { darkFlagellation, bless, ironFaith }, Array.Empty<ExistingBuffSlot>())
            .ToList();

        Assert.Equal(new[] { "bless", "iron faith", "dark flagellation" }, result.Select(p => p.Candidate.Name));
    }

    [Fact]
    public void SelectSelfBlessCandidates_SameReqLevel_BreaksTieAlphabetically()
    {
        SelfBlessCandidate zeal = new("zeal", "zeal", 1, ReqLevel: 25, ManaCost: 20, Removes: Array.Empty<int>());
        SelfBlessCandidate agony = new("agny", "agony", 2, ReqLevel: 25, ManaCost: 10, Removes: Array.Empty<int>());

        List<SelfBlessPick> result = BuffConflictAnalyzer
            .SelectSelfBlessCandidates(new[] { zeal, agony }, Array.Empty<ExistingBuffSlot>())
            .ToList();

        Assert.Equal(new[] { "agony", "zeal" }, result.Select(p => p.Candidate.Name));
    }

    [Fact]
    public void SelectSelfBlessCandidates_UnrelatedCandidates_BothRecommended()
    {
        SelfBlessCandidate other = new("bles", "bless", 100, ReqLevel: 5, ManaCost: 10, Removes: Array.Empty<int>());
        List<SelfBlessPick> result = BuffConflictAnalyzer
            .SelectSelfBlessCandidates(new[] { Zeal(), other }, Array.Empty<ExistingBuffSlot>())
            .ToList();

        Assert.Equal(2, result.Count);
        Assert.All(result, p => Assert.True(p.Recommended));
    }

    [Fact]
    public void SelectSelfBlessCandidates_ExistingSlotAlreadyRemovesCandidate_ListedButNotRecommended()
    {
        // An existing self-cast slot for greater zeal already covers this — zeal is
        // still listed (so the player can swap to it deliberately) but doesn't come
        // pre-checked, since adding it on top would just get overwritten every recast.
        ExistingBuffSlot existingGreaterZeal = new(5581, SelfAffect(), Removes: new[] { 304 });
        SelfBlessPick result = Assert.Single(BuffConflictAnalyzer
            .SelectSelfBlessCandidates(new[] { Zeal() }, new[] { existingGreaterZeal }));

        Assert.Equal("zeal", result.Candidate.Name);
        Assert.False(result.Recommended);
    }

    [Fact]
    public void SelectSelfBlessCandidates_CandidateWouldRemoveExistingSlot_ListedButNotRecommended()
    {
        // The reverse direction: an existing manually-configured zeal slot would be
        // clobbered by checking greater zeal — don't auto-touch the user's existing
        // config, but still list it so they can make that swap themselves.
        ExistingBuffSlot existingZeal = new(304, SelfAffect(), Removes: new[] { 5581 });
        SelfBlessPick result = Assert.Single(BuffConflictAnalyzer
            .SelectSelfBlessCandidates(new[] { GreaterZeal() }, new[] { existingZeal }));

        Assert.Equal("greater zeal", result.Candidate.Name);
        Assert.False(result.Recommended);
    }

    [Fact]
    public void SelectSelfBlessCandidates_ExistingSlotCannotCoLandWithSelf_StillRecommended()
    {
        // An existing slot aimed only at other party members (never self) can't
        // conflict with a self-cast candidate, even if the spell numbers overlap.
        BuffAffectSet membersOnly = BuffAffectSet.From(
            isWholePartySpell: false, wholePartyOn: false, castOnSelf: false, allMembers: false,
            targets: new[] { "legolas" });
        ExistingBuffSlot existingMemberSlot = new(5581, membersOnly, Removes: new[] { 304 });
        SelfBlessPick result = Assert.Single(BuffConflictAnalyzer
            .SelectSelfBlessCandidates(new[] { Zeal() }, new[] { existingMemberSlot }));

        Assert.Equal("zeal", result.Candidate.Name);
        Assert.True(result.Recommended);
    }

    [Fact]
    public void SelectSelfBlessCandidates_NoCandidates_ReturnsEmpty()
    {
        Assert.Empty(BuffConflictAnalyzer.SelectSelfBlessCandidates(
            Array.Empty<SelfBlessCandidate>(), Array.Empty<ExistingBuffSlot>()));
    }

    private static BuffAffectSet SelfAffect() => BuffAffectSet.From(
        isWholePartySpell: false, wholePartyOn: false, castOnSelf: true, allMembers: false,
        targets: Array.Empty<string>());

    // ----- OneDirectionalLosers (permanent-winner suppression) -----

    [Fact]
    public void OneDirectionalLosers_SuppressesTheOneWayLoser()
    {
        // greater bless removes chant; chant does NOT remove greater bless → chant is a
        // permanent loser under gbls (Paradigm re-strips it every ~3s), covered by gbls.
        List<BuffOverwritePair> pairs = new()
        {
            new BuffOverwritePair("gbls", "greater bless", "chan", "chant"),
        };

        IReadOnlyDictionary<string, string> losers = BuffConflictAnalyzer.OneDirectionalLosers(pairs);

        Assert.True(losers.ContainsKey("chan"));
        Assert.Equal("greater bless", losers["chan"]);
        Assert.False(losers.ContainsKey("gbls"));   // the winner is never a loser
    }

    [Fact]
    public void OneDirectionalLosers_ExcludesMutualPairs()
    {
        // bless ↔ greater bless each remove the other → mutual (last-cast-wins), so neither
        // is a permanent loser — nothing suppressed.
        List<BuffOverwritePair> pairs = new()
        {
            new BuffOverwritePair("gbls", "greater bless", "bles", "bless"),
            new BuffOverwritePair("bles", "bless", "gbls", "greater bless"),
        };

        Assert.Empty(BuffConflictAnalyzer.OneDirectionalLosers(pairs));
    }

    // ----- OneDirectionalRemoverCodes (stock collision-order source) -----

    [Fact]
    public void OneDirectionalRemoverCodes_MapsLoserToRemoverCode()
    {
        // Same one-way relation as OneDirectionalLosers, but keyed to the remover's cast
        // CODE (for ordering) rather than its display name (for the "covered by" label).
        List<BuffOverwritePair> pairs = new()
        {
            new BuffOverwritePair("gbls", "greater bless", "chan", "chant"),
        };

        IReadOnlyDictionary<string, string> map = BuffConflictAnalyzer.OneDirectionalRemoverCodes(pairs);

        Assert.Equal("gbls", map["chan"]);          // loser chant → remover gbls (cast first)
        Assert.False(map.ContainsKey("gbls"));       // the remover is never a loser
    }

    [Fact]
    public void OneDirectionalRemoverCodes_ExcludesMutualPairs()
    {
        // Mutual pairs are last-cast-wins — you can't keep both by ordering, so they're
        // excluded from the ordering constraint just as they are from suppression.
        List<BuffOverwritePair> pairs = new()
        {
            new BuffOverwritePair("gbls", "greater bless", "bles", "bless"),
            new BuffOverwritePair("bles", "bless", "gbls", "greater bless"),
        };

        Assert.Empty(BuffConflictAnalyzer.OneDirectionalRemoverCodes(pairs));
    }

    // ----- OverwritePairs (who counts as a winner) -----

    // greater zeal removes unholy fanaticism; unholy fanaticism does not remove it back.
    private static ConfiguredBuff GreaterZeal(BuffAffectSet affect) =>
        new("gzea", "greater zeal", 6001, new[] { 6002 }, affect);

    private static ConfiguredBuff UnholyFanaticism(BuffAffectSet affect) =>
        new("unfa", "unholy fanaticism", 6002, Array.Empty<int>(), affect);

    [Fact]
    public void OverwritePairs_RemoverTickedOffEverywhere_IsNoWinner()
    {
        // Greater zeal is configured but none of its boxes are ticked, so it is never cast
        // and can't strip unholy fanaticism: nothing is suppressed, the ticked buff stays
        // castable (Buff Watchdog showed it "covered by greater zeal" and nothing cast it).
        IReadOnlyList<BuffOverwritePair> pairs = BuffConflictAnalyzer.OverwritePairs(new[]
        {
            GreaterZeal(Inert()),
            UnholyFanaticism(WholeParty()),
        });

        Assert.Empty(pairs);
        Assert.Empty(BuffConflictAnalyzer.OneDirectionalLosers(pairs));
        Assert.Empty(BuffConflictAnalyzer.OneDirectionalRemoverCodes(pairs));
    }

    [Fact]
    public void OverwritePairs_RemoverTickedOnSelf_WinsOverWholePartyBuff()
    {
        IReadOnlyList<BuffOverwritePair> pairs = BuffConflictAnalyzer.OverwritePairs(new[]
        {
            GreaterZeal(SelfOnly()),
            UnholyFanaticism(WholeParty()),
        });

        BuffOverwritePair pair = Assert.Single(pairs);
        Assert.Equal("gzea", pair.RemovingCode);
        Assert.Equal("unfa", pair.RemovedCode);
        Assert.Equal("greater zeal", BuffConflictAnalyzer.OneDirectionalLosers(pairs)["unfa"]);
    }

    [Fact]
    public void OverwritePairs_RemovedBuffTickedOffEverywhere_NoPair()
    {
        Assert.Empty(BuffConflictAnalyzer.OverwritePairs(new[]
        {
            GreaterZeal(SelfOnly()),
            UnholyFanaticism(WholeParty(on: false)),
        }));
    }

    [Fact]
    public void OverwritePairs_MembersOnlyRemoverAndSelfOnlyBuff_CannotCoLand_NoPair()
    {
        Assert.Empty(BuffConflictAnalyzer.OverwritePairs(new[]
        {
            GreaterZeal(Members("legolas")),
            UnholyFanaticism(SelfOnly()),
        }));
    }

    // ----- Casting (who a slot is cast on right now) -----

    private static readonly string[] Present = { "raijin" };

    [Fact]
    public void Casting_WholePartySlotWithSoloOff_WhileSolo_IsNoOne()
    {
        BuffSlot slot = new() { Spell = "unfa", WholePartyOn = true, CastSolo = false };

        Assert.True(BuffAffectSet.Casting(slot, true, false, inParty: false, Array.Empty<string>()).IsEmpty);
        Assert.True(BuffAffectSet.Casting(slot, true, false, inParty: true, Present).Everyone);
    }

    [Fact]
    public void Casting_AllMembersWhileSolo_IsNoOne()
    {
        BuffSlot slot = new() { Spell = "gzea", AllMembers = true };

        Assert.True(BuffAffectSet.Casting(slot, false, false, inParty: false, Present).IsEmpty);
        Assert.True(BuffAffectSet.Casting(slot, false, false, inParty: true, Present).Includes("raijin"));
    }

    [Fact]
    public void Casting_OnlyPresentTickedMembersNotNegated()
    {
        BuffSlot slot = new() { Spell = "gzea", Targets = { "raijin", "legolas" } };
        Assert.Equal(new[] { "raijin" }, BuffAffectSet.Casting(slot, false, false, true, Present).Members);

        slot.NegatedTargets.Add("raijin");
        Assert.True(BuffAffectSet.Casting(slot, false, false, true, Present).IsEmpty);
    }

    [Fact]
    public void Casting_OthersOnlySpell_NeverOnUs()
    {
        BuffSlot slot = new() { Spell = "renw", CastOnSelf = true };

        Assert.False(BuffAffectSet.Casting(slot, false, isOthersOnlySpell: true, true, Present).Self);
        Assert.True(BuffAffectSet.Casting(slot, false, isOthersOnlySpell: false, true, Present).Self);
    }

    // Two buffs that each remove the loser, one cast on us and one on Raijin, between
    // them cover the whole party, so the whole-party loser is left off.
    [Fact]
    public void OneDirectionalSuppressions_WinnersTogetherCoverTheParty()
    {
        IReadOnlyDictionary<string, BuffSuppression> suppressed = BuffConflictAnalyzer.OneDirectionalSuppressions(new[]
        {
            GreaterZeal(SelfOnly()),
            new ConfiguredBuff("zeal", "zeal", 6003, new[] { 6002 }, Members("raijin")),
            UnholyFanaticism(WholeParty()),
        });

        BuffSuppression sup = suppressed["unfa"];
        Assert.Equal("greater zeal", sup.WinnerName);
        Assert.True(sup.CoversWholeParty(Present));
        Assert.False(sup.CoversWholeParty(new[] { "raijin", "legolas" }));
    }
}
