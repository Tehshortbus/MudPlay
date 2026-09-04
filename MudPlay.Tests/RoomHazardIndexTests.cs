using System.IO;
using System.Linq;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Decode coverage for the room-entry hazard index: which cast-on-enter spells
// resolve to a protectable hazard, and which item(s) counter each. Exercises the
// three protection shapes (damage/negate, textblock failitem, textblock
// checkspell), the EndCast chain walk, layered (multi-group) protections, and
// the room-spell candidate filter that keeps attack spells out of the scan.
public sealed class RoomHazardIndexTests : IDisposable
{
    private readonly string _root;

    public RoomHazardIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-hazard-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // Build an index over a one-set fixture. Every table is optional except
    // Rooms/Spells (the index bails without them); a null table isn't written.
    private RoomHazardIndex NewIndex(
        string roomsJson,
        string spellsJson,
        string? itemsJson = null,
        string? tbInfoJson = null,
        string set = "alpha")
    {
        string setDir = Path.Combine(_root, set);
        Directory.CreateDirectory(setDir);
        File.WriteAllText(Path.Combine(setDir, "Rooms.json"), roomsJson);
        File.WriteAllText(Path.Combine(setDir, "Spells.json"), spellsJson);
        if (itemsJson is not null)
            File.WriteAllText(Path.Combine(setDir, "Items.json"), itemsJson);
        if (tbInfoJson is not null)
            File.WriteAllText(Path.Combine(setDir, "TBInfo.json"), tbInfoJson);

        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        RoomHazardIndex index = new(cache);
        index.OnActiveSetChanged(set);
        return index;
    }

    // A room whose Spell column casts `spell` on entry.
    private static string Room(int spell) => $$"""
        [ { "Map Number": 1, "Room Number": 2, "Name": "R", "Spell": {{spell}} } ]
        """;

    [Fact]
    public void Damage_WithNegator_IsHazard()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            """ [ { "Number": 700, "Abil-0": 1, "AbilVal-0": 25 } ] """,
            """ [ { "Number": 42, "NegateSpell-0": 700 } ] """);

        RoomHazardIndex.RoomHazard? h = idx.HazardForSpell(700);
        Assert.NotNull(h);
        Assert.Contains(42, h!.ProtectingItems);
        Assert.True(h.IsSatisfiedBy(id => id == 42));
        Assert.False(h.IsSatisfiedBy(_ => false));
    }

    [Fact]
    public void Damage_NoNegator_NotIndexed()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            """ [ { "Number": 700, "Abil-0": 1, "AbilVal-0": 25 } ] """,
            """ [ { "Number": 42, "NegateSpell-0": 999 } ] """);

        // Damaging but no item counters it → deliberately not indexed.
        Assert.Null(idx.HazardForSpell(700));
        Assert.Equal(0, idx.HazardCount);
    }

    [Fact]
    public void Benign_RoomSpell_NotIndexed()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            // Abil 5 is neither damage (1), endcast (151), nor textblock (148).
            """ [ { "Number": 700, "Abil-0": 5, "AbilVal-0": 3 } ] """,
            """ [ { "Number": 42, "NegateSpell-0": 700 } ] """);

        Assert.Null(idx.HazardForSpell(700));
    }

    [Fact]
    public void EndCastChain_NegatedMember_IsHazard()
    {
        RoomHazardIndex idx = NewIndex(
            Room(100),
            """
            [ { "Number": 100, "Abil-0": 151, "AbilVal-0": 101 },
              { "Number": 101, "Abil-0": 1,   "AbilVal-0": 40  } ]
            """,
            // The negator cancels a downstream chain member (101), not the root.
            """ [ { "Number": 55, "NegateSpell-0": 101 } ] """);

        RoomHazardIndex.RoomHazard? h = idx.HazardForSpell(100);
        Assert.NotNull(h);
        Assert.Contains(55, h!.ProtectingItems);
    }

    [Fact]
    public void TextBlock_FailItem_GuardingHarm_IsHazard()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            // Abil 148 = TextBlock → TBInfo #50. The failitem guards a teleport
            // outcome (a movement hazard) — holding item 55 aborts it.
            """ [ { "Number": 700, "Abil-0": 148, "AbilVal-0": 50 } ] """,
            itemsJson: null,
            tbInfoJson: """ [ { "Number": 50, "Action": "failitem 55:teleport 12" } ] """);

        RoomHazardIndex.RoomHazard? h = idx.HazardForSpell(700);
        Assert.NotNull(h);
        Assert.Contains(55, h!.ProtectingItems);   // holding item 55 aborts the chain
    }

    [Fact]
    public void TextBlock_FailItem_BenignBranch_NotIndexed()
    {
        // The blackwood-forest shape (report paradigm-20260825-125954): a room-entry
        // spell with a `failitem` "counter" whose unprotected branch only summons a
        // monster / shifts alignment / prints a message — no damage, death, or forced
        // movement. That is NOT a hazard even though it ships a counter item, so the
        // router must not gate movement into the room.
        RoomHazardIndex idx = NewIndex(
            Room(700),
            """ [ { "Number": 700, "Abil-0": 148, "AbilVal-0": 50 } ] """,
            itemsJson: null,
            tbInfoJson: """
            [ { "Number": 50, "Action": "failitem 55:random 51" },
              { "Number": 51, "Action": "50:addevil 0\n100:summon 815" } ]
            """);

        Assert.Null(idx.HazardForSpell(700));
    }

    [Fact]
    public void TextBlock_MinBaseEncodedTb_IsHazard()
    {
        // The ice-cavern shape (spells 1144/1145): a TextBlock spell whose
        // AbilVal-0 is 0 — the TBInfo number lives in MinBase/MaxBase instead. Its
        // failitem counter (the rope+grapple) guards a teleport, so it must still be
        // indexed, or the route picker never offers it (report paradigm-20260810-202239).
        RoomHazardIndex idx = NewIndex(
            Room(700),
            """ [ { "Number": 700, "Abil-0": 148, "AbilVal-0": 0, "MinBase": 50, "MaxBase": 50 } ] """,
            itemsJson: null,
            tbInfoJson: """ [ { "Number": 50, "Action": "failitem 55:teleport 12" } ] """);

        RoomHazardIndex.RoomHazard? h = idx.HazardForSpell(700);
        Assert.NotNull(h);
        Assert.Contains(55, h!.ProtectingItems);
    }

    [Fact]
    public void TextBlock_ZeroAbilValAndNoBase_NotIndexed()
    {
        // AbilVal-0 = 0 with no MinBase/MaxBase → no TBInfo to reach, so nothing
        // to index. Guards the fallback from inventing a hazard out of a base of 0.
        RoomHazardIndex idx = NewIndex(
            Room(700),
            """ [ { "Number": 700, "Abil-0": 148, "AbilVal-0": 0 } ] """,
            itemsJson: null,
            tbInfoJson: """ [ { "Number": 50, "Action": "failitem 55" } ] """);

        Assert.Null(idx.HazardForSpell(700));
    }

    [Fact]
    public void TextBlock_CheckSpell_IsHazard()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            """ [ { "Number": 700, "Abil-0": 148, "AbilVal-0": 50 } ] """,
            // Item 60 casts buff spell 300 (Abil 43 = CastsSp).
            """ [ { "Number": 60, "Abil-0": 43, "AbilVal-0": 300 } ] """,
            """ [ { "Number": 50, "Action": "checkspell 300" } ] """);

        RoomHazardIndex.RoomHazard? h = idx.HazardForSpell(700);
        Assert.NotNull(h);
        Assert.Contains(60, h!.ProtectingItems);   // carry the buff source
    }

    // A checkspell hazard also records a BuffCounter carrying the buff spell #,
    // its source item(s), and the computed protection window (the buff's Dur in
    // rounds × SpellRoundSeconds) — the timer the walk-time provisioner re-`use`s
    // the source on.
    [Fact]
    public void CheckSpell_BuildsBuffCounter_WithDuration()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            // Room spell 700 is a TextBlock (checkspell gate); buff spell 300 has
            // Dur 600 rounds → 1800s. Item 60 casts buff 300 (Abil 43 = CastsSp).
            """
            [ { "Number": 700, "Abil-0": 148, "AbilVal-0": 50 },
              { "Number": 300, "Dur": 600 } ]
            """,
            """ [ { "Number": 60, "Abil-0": 43, "AbilVal-0": 300 } ] """,
            """ [ { "Number": 50, "Action": "checkspell 300" } ] """);

        RoomHazardIndex.RoomHazard? h = idx.HazardForSpell(700);
        Assert.NotNull(h);
        RoomHazardIndex.BuffCounter counter = Assert.Single(h!.BuffCounters);
        Assert.Equal(300, counter.BuffSpell);
        Assert.Contains(60, counter.SourceItems);
        Assert.Equal(1800, counter.DurationSeconds);   // 600 rounds × 3s
    }

    // The Scorching Desert gates its heat-buff with `failspell` (not `checkspell`):
    // no waterskin buff up → heat damage (user-confirmed). RoomHazardIndex must treat
    // failspell as the same protective-buff gate, or the walk-time provisioner never
    // raises the buff — the "pathed into the desert without using a waterskin" report.
    [Fact]
    public void FailSpell_BuildsBuffCounter_LikeCheckSpell()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            // Room spell 700 → TextBlock; buff 300 (Dur 600 → 1800s); item 60 casts 300.
            """
            [ { "Number": 700, "Abil-0": 148, "AbilVal-0": 50 },
              { "Number": 300, "Dur": 600 } ]
            """,
            """ [ { "Number": 60, "Abil-0": 43, "AbilVal-0": 300 } ] """,
            // The desert shape: `failspell <buff> <block>:random <block>`.
            """ [ { "Number": 50, "Action": "failspell 300 51:random 52" } ] """);

        RoomHazardIndex.RoomHazard? h = idx.HazardForSpell(700);
        Assert.NotNull(h);
        RoomHazardIndex.BuffCounter counter = Assert.Single(h!.BuffCounters);
        Assert.Equal(300, counter.BuffSpell);
        Assert.Contains(60, counter.SourceItems);   // waterskin analogue
        Assert.Equal(1800, counter.DurationSeconds);
    }

    // The sunstone-wristband case: the failspell's random-linked failure branch is
    // guarded by `failitem <wristband>` a couple of `random` hops down — holding it
    // skips the whole desert interaction (damage + teleport), so it's a FULL
    // alternative to the waterskin buff. Both must land in ONE requirement group
    // (carry either clears the route); the buff-refresh provisioner stays tied to the
    // waterskin only.
    [Fact]
    public void FailSpell_FailureBranchFailitem_IsBuffAlternative()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            """
            [ { "Number": 700, "Abil-0": 148, "AbilVal-0": 50 },
              { "Number": 300, "Dur": 600 } ]
            """,
            """ [ { "Number": 60, "Abil-0": 43, "AbilVal-0": 300 } ] """,
            // failspell 300 → random 51 → random 52 → failitem 99 (wristband analogue),
            // mirroring the desert's 2653→2655→2700 nesting.
            """
            [ { "Number": 50, "Action": "failspell 300 49:random 51" },
              { "Number": 51, "Action": "86:cast 713\n100:random 52" },
              { "Number": 52, "Action": "30:failitem 99:cast 743" } ]
            """);

        RoomHazardIndex.RoomHazard? h = idx.HazardForSpell(700);
        Assert.NotNull(h);
        IReadOnlyList<int> group = Assert.Single(h!.RequirementGroups);
        Assert.Contains(60, group);   // waterskin
        Assert.Contains(99, group);   // sunstone wristband analogue
        Assert.True(h.IsSatisfiedBy(item => item == 99));   // wristband alone clears the route
        Assert.True(h.IsSatisfiedBy(item => item == 60));   // waterskin alone clears the route
        // The use-the-item provisioner stays tied to the buff source, not the passive guard.
        RoomHazardIndex.BuffCounter bc = Assert.Single(h.BuffCounters);
        Assert.Equal(new[] { 60 }, bc.SourceItems);
        // ...but the counter also carries the immunity guard, so the provisioner can
        // skip the `use` when the wristband is held (report -112011).
        Assert.Contains(99, bc.ImmunityItems);
    }

    // A checkspell whose buff spell has no Dur in the data → DurationSeconds 0.
    // The provisioner falls back to a periodic refresh rather than once-and-never.
    [Fact]
    public void CheckSpell_UnknownDuration_IsZero()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            """ [ { "Number": 700, "Abil-0": 148, "AbilVal-0": 50 } ] """,
            """ [ { "Number": 60, "Abil-0": 43, "AbilVal-0": 300 } ] """,
            """ [ { "Number": 50, "Action": "checkspell 300" } ] """);

        RoomHazardIndex.RoomHazard? h = idx.HazardForSpell(700);
        Assert.NotNull(h);
        RoomHazardIndex.BuffCounter counter = Assert.Single(h!.BuffCounters);
        Assert.Equal(0, counter.DurationSeconds);
    }

    // A checkspell hazard whose buff-ABSENT branch casts a damage spell records
    // that spell as the BuffCounter's LapseSpell — the desert's "you need water,
    // soon!" (spell 712) the reactive re-raise keys on. The checkspell token's
    // second int is the TB the room jumps to when the buff is absent; that block's
    // `cast` is the lapse-damage spell.
    [Fact]
    public void CheckSpell_DerivesLapseSpell_FromAbsentBranchCast()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            """ [ { "Number": 700, "Abil-0": 148, "AbilVal-0": 50 } ] """,
            """ [ { "Number": 60, "Abil-0": 43, "AbilVal-0": 300 } ] """,
            """
            [ { "Number": 50, "Action": "checkspell 300 51" },
              { "Number": 51, "Action": "cast 712" } ]
            """);

        RoomHazardIndex.RoomHazard? h = idx.HazardForSpell(700);
        Assert.NotNull(h);
        Assert.Equal(712, Assert.Single(h!.BuffCounters).LapseSpell);
    }

    // A bare checkspell with no jump target → no derivable lapse spell (0). The
    // reactive path then stays inert and only the predictive timer holds the buff.
    [Fact]
    public void CheckSpell_NoAbsentBranch_LapseSpellZero()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            """ [ { "Number": 700, "Abil-0": 148, "AbilVal-0": 50 } ] """,
            """ [ { "Number": 60, "Abil-0": 43, "AbilVal-0": 300 } ] """,
            """ [ { "Number": 50, "Action": "checkspell 300" } ] """);

        RoomHazardIndex.RoomHazard? h = idx.HazardForSpell(700);
        Assert.NotNull(h);
        Assert.Equal(0, Assert.Single(h!.BuffCounters).LapseSpell);
    }

    [Fact]
    public void LayeredProtections_RequireOneFromEachGroup()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            // Damage AND a textblock failitem gate → two independent groups.
            """ [ { "Number": 700, "Abil-0": 1, "AbilVal-0": 25, "Abil-1": 148, "AbilVal-1": 50 } ] """,
            """ [ { "Number": 42, "NegateSpell-0": 700 } ] """,
            """ [ { "Number": 50, "Action": "failitem 55" } ] """);

        RoomHazardIndex.RoomHazard? h = idx.HazardForSpell(700);
        Assert.NotNull(h);
        Assert.Equal(2, h!.RequirementGroups.Count);
        Assert.False(h.IsSatisfiedBy(id => id == 42));            // negator only
        Assert.False(h.IsSatisfiedBy(id => id == 55));            // failitem only
        Assert.True(h.IsSatisfiedBy(id => id is 42 or 55));       // one from each
    }

    [Fact]
    public void AttackSpell_NotACandidate()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            // Spell 800 is damaging + negated but is NOT any Room.Spell, so the
            // room-spell candidate filter must keep it out of the index.
            """
            [ { "Number": 700, "Abil-0": 1, "AbilVal-0": 25 },
              { "Number": 800, "Abil-0": 1, "AbilVal-0": 99 } ]
            """,
            """
            [ { "Number": 42, "NegateSpell-0": 700 },
              { "Number": 43, "NegateSpell-0": 800 } ]
            """);

        Assert.NotNull(idx.HazardForSpell(700));
        Assert.Null(idx.HazardForSpell(800));
        Assert.Equal(1, idx.HazardCount);
    }

    // A plain damage hazard (magma-heat shape: a direct Damage ability) is
    // survivable — safe to offer a "cross unprotected — take the damage" choice.
    [Fact]
    public void Damage_IsSurvivable()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            """ [ { "Number": 700, "Abil-0": 1, "AbilVal-0": 25 } ] """,
            """ [ { "Number": 42, "NegateSpell-0": 700 } ] """);

        Assert.True(idx.HazardForSpell(700)!.IsSurvivableDamage);
    }

    // The Silver River shape (spell 753 → TB `failitem <raft>:cast 754`, where 754
    // is a plain Damage spell): the unprotected outcome is survivable damage, so the
    // picker may offer "cross unprotected".
    [Fact]
    public void TextBlockCastDamage_IsSurvivable()
    {
        RoomHazardIndex idx = NewIndex(
            Room(753),
            """
            [ { "Number": 753, "Abil-0": 148, "AbilVal-0": 2750 },
              { "Number": 754, "Abil-0": 1,   "AbilVal-0": 40   } ]
            """,
            itemsJson: null,
            tbInfoJson: """ [ { "Number": 2750, "Action": "failitem 690:cast 754" } ] """);

        RoomHazardIndex.RoomHazard? h = idx.HazardForSpell(753);
        Assert.NotNull(h);
        Assert.Contains(690, h!.ProtectingItems);      // holding a raft aborts it
        Assert.True(h.IsSurvivableDamage);
    }

    // An EndCast death-timer chain is GRAVE — never offer "cross unprotected".
    [Fact]
    public void EndCastChain_IsNotSurvivable()
    {
        RoomHazardIndex idx = NewIndex(
            Room(100),
            """
            [ { "Number": 100, "Abil-0": 151, "AbilVal-0": 101 },
              { "Number": 101, "Abil-0": 1,   "AbilVal-0": 40  } ]
            """,
            """ [ { "Number": 55, "NegateSpell-0": 101 } ] """);

        Assert.False(idx.HazardForSpell(100)!.IsSurvivableDamage);
    }

    // A textblock that forcibly relocates the crosser (teleport) is GRAVE — a
    // counter is the only way past, so "cross unprotected" is never offered.
    [Fact]
    public void TextBlockTeleport_IsNotSurvivable()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            """ [ { "Number": 700, "Abil-0": 148, "AbilVal-0": 50 } ] """,
            itemsJson: null,
            tbInfoJson: """ [ { "Number": 50, "Action": "failitem 55:teleport 12" } ] """);

        Assert.False(idx.HazardForSpell(700)!.IsSurvivableDamage);
    }

    // A checkspell / failspell buff-gate (the desert-heat / drown shape) is treated
    // as GRAVE — conservatively, since its buff-absent branch can kill over time.
    [Fact]
    public void CheckSpell_IsNotSurvivable()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            """ [ { "Number": 700, "Abil-0": 148, "AbilVal-0": 50 } ] """,
            """ [ { "Number": 60, "Abil-0": 43, "AbilVal-0": 300 } ] """,
            """ [ { "Number": 50, "Action": "checkspell 300" } ] """);

        Assert.False(idx.HazardForSpell(700)!.IsSurvivableDamage);
    }

    [Fact]
    public void NoActiveSet_IsEmpty()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            """ [ { "Number": 700, "Abil-0": 1, "AbilVal-0": 25 } ] """,
            """ [ { "Number": 42, "NegateSpell-0": 700 } ] """);
        Assert.Equal(1, idx.HazardCount);

        idx.OnActiveSetChanged(null);
        Assert.Equal(0, idx.HazardCount);
        Assert.Null(idx.HazardForSpell(700));
    }

    // MandatoryItems is the auto-acquire set: single-counter groups only. An
    // any-of group (2+ options) is a manual choice, so it's excluded — feeding
    // it to the each-required demand pipeline would acquire every option.
    [Fact]
    public void MandatoryItems_ExcludesAnyOfGroups()
    {
        var hazard = new RoomHazardIndex.RoomHazard(new IReadOnlyList<int>[]
        {
            new[] { 42 },          // sole counter — mandatory
            new[] { 55, 60 },      // any-of choice — omitted
        });

        Assert.Equal(new[] { 42 }, hazard.MandatoryItems);
        Assert.Equal(new[] { 42, 55, 60 }, hazard.ProtectingItems.OrderBy(i => i).ToArray());
    }

    [Fact]
    public void MandatoryItems_DedupesRepeatedCounter()
    {
        var hazard = new RoomHazardIndex.RoomHazard(new IReadOnlyList<int>[]
        {
            new[] { 42 },
            new[] { 42 },
        });

        Assert.Equal(new[] { 42 }, hazard.MandatoryItems);
    }

    // The trollskin-boots / swamp-boots case (report paradigm-20260829-203409):
    // two items share the same NegateSpell, so they land in the same any-of
    // group. The route picker resolves to sourcing ONE of them (trollskin), but
    // the player instead equips the other (swamp boots) they already owned.
    // GroupSatisfiedByAlternative lets a caller pinned to the originally-chosen
    // id (trollskin) recognize the substitute already covers the hazard.
    [Fact]
    public void GroupSatisfiedByAlternative_OtherGroupMemberCarried_IsTrue()
    {
        RoomHazardIndex idx = NewIndex(
            Room(485),
            """ [ { "Number": 485, "Abil-0": 1, "AbilVal-0": 25 } ] """,
            """
            [ { "Number": 1232, "NegateSpell-0": 485 },
              { "Number": 925,  "NegateSpell-0": 485 } ]
            """);

        Assert.True(idx.GroupSatisfiedByAlternative(1232, id => id == 925));
        Assert.False(idx.GroupSatisfiedByAlternative(1232, _ => false));
    }

    [Fact]
    public void GroupSatisfiedByAlternative_ItemNotInAnyGroup_IsFalse()
    {
        RoomHazardIndex idx = NewIndex(
            Room(485),
            """ [ { "Number": 485, "Abil-0": 1, "AbilVal-0": 25 } ] """,
            """ [ { "Number": 1232, "NegateSpell-0": 485 } ] """);

        Assert.False(idx.GroupSatisfiedByAlternative(999, id => id == 1232));
    }
}
