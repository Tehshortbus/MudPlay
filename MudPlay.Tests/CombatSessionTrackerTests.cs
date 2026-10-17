using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Spells;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// CombatSessionTracker classification + aggregation.
// Lines route through the real DefaultPatterns seed and a real
// RoundDamageTracker so the new UserMisses / UserDodges
// patterns, the "You"-only offensive filter, the dodge/mob-miss de-dupe, and
// the RoundComplete per-round path are all exercised end-to-end.
public sealed class CombatSessionTrackerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public PlayerState State { get; } = new();
        public RoundDamageTracker Rounds { get; }
        public CombatSessionTracker Tracker { get; }
        public int ChangedCount { get; private set; }

        public Harness(
            IReadOnlyList<(string Name, CasterMessageMatcher Matcher)>? spellMatchers = null,
            IReadOnlyList<SpellLineMatcher>? followUps = null,
            Func<string, bool>? isPartyMember = null)
        {
            DefaultPatterns.Seed(Router);
            Rounds = new RoundDamageTracker(Router, State);
            IReadOnlyList<SpellLineMatcher>? lines = spellMatchers?
                .Select(m => new SpellLineMatcher(m.Name, m.Matcher)).Concat(followUps ?? []).ToList();
            Tracker = new CombatSessionTracker(
                Router, Rounds,
                resolveSpellMatchers: lines is null ? null : () => lines,
                isPartyMember: isPartyMember);
            Rounds.SetOwnSpellLineCheck(Tracker.MatchesOwnSpell);
            Rounds.NoteRoomEntities(new RoomEntitiesObservation(
                "Also here: kobold, giant rat.",
                new[]
                {
                    new RoomEntity("kobold", "kobold", EntityKind.Monster, 1),
                    new RoomEntity("giant rat", "giant rat", EntityKind.Monster, 2),
                },
                DateTimeOffset.UtcNow));
            Tracker.Changed += () => ChangedCount++;
        }

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        public CombatSessionStats Stats => Tracker.Snapshot();

        public void Dispose()
        {
            Tracker.Dispose();
            Rounds.Dispose();
        }
    }

    // ----- fresh state ------------------------------------------------

    [Fact]
    public void Fresh_AllZero()
    {
        using Harness h = new();
        CombatSessionStats s = h.Stats;
        Assert.Equal(0, s.TotalSwings);
        Assert.Equal(0, s.IncomingAttacks);
        Assert.Equal(0, s.RoundsWithDamage);
        Assert.Equal(0, s.PhysicalMaxDamage);
        Assert.Equal(0d, s.HitPercent);
        Assert.Equal(0d, s.DodgePercent);
    }

    // ----- offensive classification -----------------------------------

    [Fact]
    public void PlainHit_CountsAsHit_WithDamageExtent()
    {
        using Harness h = new();
        h.Feed("You slash the kobold for 8 damage!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(1, s.Hits);
        Assert.Equal(0, s.Crits);
        Assert.Equal(0, s.Backstabs);
        Assert.Equal(8, s.HitMinDamage);
        Assert.Equal(8, s.HitMaxDamage);
        Assert.Equal(8d, s.HitAvgDamage);
        Assert.Equal(8, s.PhysicalMinDamage);
        Assert.Equal(8, s.PhysicalMaxDamage);
        Assert.Equal(100d, s.HitPercent);
    }

    [Fact]
    public void CriticallyKeyword_CountsAsCrit()
    {
        using Harness h = new();
        h.Feed("You critically slash the kobold for 24 damage!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(0, s.Hits);
        Assert.Equal(1, s.Crits);
        Assert.Equal(100d, s.CritPercent);
        // Crit damage lands in its own extent (the session panel shows it on the
        // Crit row), and still rolls into the physical max.
        Assert.Equal(24, s.CritMinDamage);
        Assert.Equal(24, s.CritMaxDamage);
        Assert.Equal(24d, s.CritAvgDamage);
        Assert.Equal(0, s.HitMaxDamage); // a crit is not a plain hit
        Assert.Equal(24, s.PhysicalMaxDamage);
    }

    [Fact]
    public void SurpriseKeyword_CountsAsBackstab_AndTracksOwnExtent()
    {
        using Harness h = new();
        h.Feed("You surprise punch kobold thief for 12 damage!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(0, s.Hits);
        Assert.Equal(1, s.Backstabs);
        Assert.Equal(12, s.BackstabMinDamage);
        Assert.Equal(12, s.BackstabMaxDamage);
        Assert.Equal(12d, s.BackstabAvgDamage);
        // A backstab is still a landed physical swing.
        Assert.Equal(1, s.LandedSwings);
        Assert.Equal(12, s.PhysicalMinDamage);
    }

    [Fact]
    public void OtherPlayersHit_IsIgnored()
    {
        using Harness h = new();
        h.Feed("Raijin slashes the kobold for 9 damage!");
        Assert.Equal(0, h.Stats.Hits);
        Assert.Equal(0, h.Stats.LandedSwings);
    }

    [Fact]
    public void Miss_CountsTowardSwingDenominator()
    {
        using Harness h = new();
        h.Feed("You slash the kobold for 8 damage!");
        h.Feed("You swing at the kobold, but miss!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(1, s.Hits);
        Assert.Equal(1, s.Misses);
        Assert.Equal(2, s.TotalSwings);
        Assert.Equal(50d, s.HitPercent);
        Assert.Equal(50d, s.MissPercent);
    }

    [Fact]
    public void RealmWhiff_NoMissWord_CountsWhileEngaged()
    {
        // The live realm prints a whiff as the swing skeleton with no damage
        // tail and no literal "miss" — "You punch acid slime!" — bracketed by
        // combat. It must count toward the miss denominator.
        using Harness h = new();
        h.Feed("*Combat Engaged*");
        h.Feed("You punch acid slime!");
        h.Feed("You punch acid slime for 8 damage!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(1, s.Misses);
        Assert.Equal(1, s.Hits);
        Assert.Equal(2, s.TotalSwings);
    }

    [Fact]
    public void HitLine_IsNeverDoubleCountedAsMiss()
    {
        // The whiff skeleton would match a hit line too, but the "for N damage"
        // look-ahead excludes it — a connecting swing must not also tick misses.
        using Harness h = new();
        h.Feed("*Combat Engaged*");
        h.Feed("You critically punch giant rat for 21 damage!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(0, s.Misses);
        Assert.Equal(1, s.Crits);
    }

    [Fact]
    public void EmoteEndingInBang_OutOfCombat_IsNotCountedAsMiss()
    {
        // "You feel much better!" satisfies the whiff skeleton, so without the
        // combat-engaged gate it would inflate the miss count. Out of combat it
        // must be ignored.
        using Harness h = new();
        h.Feed("You feel much better!");
        Assert.Equal(0, h.Stats.Misses);

        // After combat ends, the gate disarms again.
        h.Feed("*Combat Engaged*");
        h.Feed("*Combat Off*");
        h.Feed("You feel much better!");
        Assert.Equal(0, h.Stats.Misses);
    }

    // A buff of ours that a party member negated or resisted is not a miss (user,
    // 2026-10-17: "Not a miss"); the same line on a monster is one ("Count resists too").
    [Theory]
    [InlineData("You attempt to cast bless at Raijin, but it's negated!", 0)]
    [InlineData("You attempt to cast bless at Raijin, but the spell is resisted.", 0)]
    [InlineData("You attempt to cast disrupt at kobold, but it's negated!", 1)]
    [InlineData("You attempt to cast disrupt at kobold, but the spell is resisted.", 1)]
    public void OwnCastNegatedOrResisted_AMissOnlyOnAMonster(string line, int misses)
    {
        using Harness h = new(isPartyMember: n => string.Equals(n, "Raijin", StringComparison.OrdinalIgnoreCase));
        h.Feed("*Combat Engaged*");
        h.Feed(line);
        Assert.Equal(misses, h.Stats.Misses);
    }

    // Solo, a cast at our own name is never a swing at a monster: the wiring counts
    // our own in-game name alongside the party roster.
    [Theory]
    [InlineData("Fujin", true)]
    [InlineData("fujin", true)]
    [InlineData("Batu", false)]
    [InlineData("kobold", false)]
    public void PartyMemberOrSelf_SoloCountsOurOwnName(string name, bool expected)
        => Assert.Equal(expected, AppServices.IsPartyMemberOrSelfName(name, _ => false, "Fujin Windcaller"));

    [Fact]
    public void PartyMemberOrSelf_NoNameYet_IsNobody()
        => Assert.False(AppServices.IsPartyMemberOrSelfName("Fujin", _ => false, string.Empty));

    [Fact]
    public void OwnCastAtOurselvesResisted_Solo_IsNotAMiss()
    {
        using Harness h = new(isPartyMember: n => AppServices.IsPartyMemberOrSelfName(n, _ => false, "Fujin"));
        h.Feed("*Combat Engaged*");
        h.Feed("You attempt to cast bless at Fujin, but the spell is resisted.");
        h.Feed("You attempt to cast bless at Fujin, but it's negated!");
        Assert.Equal(0, h.Stats.Misses);
    }

    [Fact]
    public void PhysicalExtent_SpansAllLandedCategories()
    {
        using Harness h = new();
        h.Feed("You slash the kobold for 8 damage!");
        h.Feed("You critically slash the kobold for 24 damage!");
        h.Feed("You surprise punch kobold thief for 12 damage!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(3, s.LandedSwings);
        Assert.Equal(8, s.PhysicalMinDamage);
        Assert.Equal(24, s.PhysicalMaxDamage);
        Assert.Equal(44, s.PhysicalTotalDamage);
    }

    // ----- defensive: incoming attacks --------------------------------

    [Fact]
    public void MobHit_Counts()
    {
        using Harness h = new();
        h.Feed("The giant rat bites you for 3 damage!");
        Assert.Equal(1, h.Stats.MobHits);
    }

    [Fact]
    public void HitsTaken_TrackDamageExtent_WhateverTheWording()
    {
        using Harness h = new();
        h.Feed("The giant rat bites you for 3 damage!");
        h.Feed("The kobold claws you with its pincers for 9 damage!");
        h.Feed("Acid sears you for 6 damage!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(3, s.MobHits);
        Assert.Equal(3, s.HitTakenMinDamage);
        Assert.Equal(9, s.HitTakenMaxDamage);
        Assert.Equal(18, s.HitTakenTotalDamage);
        Assert.Equal(6d, s.HitTakenAvgDamage);
    }

    [Fact]
    public void DamageNobodyDealt_IsNotAHitTaken()
    {
        using Harness h = new();
        h.Feed("You are poisoned for 3 damage!");
        Assert.Equal(0, h.Stats.MobHits);
    }

    [Fact]
    public void MobMiss_Counts_AsPlainMiss()
    {
        using Harness h = new();
        h.Feed("The giant rat swings at you");
        CombatSessionStats s = h.Stats;
        Assert.Equal(1, s.MobMisses);
        Assert.Equal(0, s.Dodges);
    }

    [Fact]
    public void Dodge_CountsAsDodge_NotPlainMiss()
    {
        using Harness h = new();
        h.Feed("The kobold thief lunges at you, but you dodge!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(1, s.Dodges);
        Assert.Equal(0, s.MobMisses);   // de-duped: the same line satisfies MobMisses
    }

    [Fact]
    public void DodgePercent_OverAllIncomingAttacks()
    {
        using Harness h = new();
        h.Feed("The giant rat bites you for 3 damage!");          // mob hit
        h.Feed("The giant rat swings at you");                    // plain miss
        h.Feed("The kobold thief lunges at you, but you dodge!"); // dodge

        CombatSessionStats s = h.Stats;
        Assert.Equal(3, s.IncomingAttacks);
        Assert.Equal(1, s.Dodges);
        Assert.Equal(100d / 3d, s.DodgePercent, 3);
    }

    [Fact]
    public void AvoidPercent_FoldsDodgeAndMissTogether()
    {
        using Harness h = new();
        h.Feed("The giant rat bites you for 3 damage!");          // mob hit
        h.Feed("The giant rat swings at you");                    // plain miss
        h.Feed("The kobold thief lunges at you, but you dodge!"); // dodge

        CombatSessionStats s = h.Stats;
        // Two of the three incoming attacks didn't land (one miss + one dodge),
        // so the combined defensive "avoided" rate is 2/3.
        Assert.Equal(2, s.AvoidedAttacks);
        Assert.Equal(200d / 3d, s.AvoidPercent, 3);
    }

    // ----- per-round damage (RoundComplete) ---------------------------

    [Fact]
    public void RoundComplete_FeedsPerRoundDamageExtent()
    {
        using Harness h = new();

        h.Feed("You slash the kobold for 5 damage!");
        h.Feed("*Combat Off*");                       // closes round → DamageDealt 5

        h.Feed("You slash the goblin for 7 damage!");
        h.Feed("*Combat Off*");                       // closes round → DamageDealt 7

        CombatSessionStats s = h.Stats;
        Assert.Equal(2, s.RoundsWithDamage);
        Assert.Equal(5, s.RoundMinDamage);
        Assert.Equal(7, s.RoundMaxDamage);
        Assert.Equal(6d, s.RoundAvgDamage);
    }

    [Fact]
    public void PureDefenceRound_DoesNotDragRoundMinToZero()
    {
        using Harness h = new();
        // A round where we dealt no damage (only took a hit) must not register
        // as a 0-damage round.
        h.Feed("The giant rat bites you for 3 damage!");
        h.Feed("*Combat Off*");
        Assert.Equal(0, h.Stats.RoundsWithDamage);

        h.Feed("You slash the kobold for 5 damage!");
        h.Feed("*Combat Off*");
        Assert.Equal(1, h.Stats.RoundsWithDamage);
        Assert.Equal(5, h.Stats.RoundMinDamage);
    }

    // ----- reset ------------------------------------------------------

    [Fact]
    public void Reset_ZeroesEverything()
    {
        using Harness h = new();
        h.Feed("You slash the kobold for 8 damage!");
        h.Feed("You swing at the kobold, but miss!");
        h.Feed("The giant rat bites you for 3 damage!");
        h.Feed("The kobold thief lunges at you, but you dodge!");

        h.Tracker.Reset();

        CombatSessionStats s = h.Stats;
        Assert.Equal(0, s.TotalSwings);
        Assert.Equal(0, s.IncomingAttacks);
        Assert.Equal(0, s.Hits);
        Assert.Equal(0, s.Dodges);
    }

    // ----- change notification ----------------------------------------

    [Fact]
    public void Changed_FiresOnObservation()
    {
        using Harness h = new();
        Assert.Equal(0, h.ChangedCount);
        h.Feed("You slash the kobold for 8 damage!");
        Assert.True(h.ChangedCount >= 1);
    }

    // ----- our spells and procs, as the round ledger credits them -----

    private static CasterMessageMatcher Matcher(string template) =>
        CasterMessageMatcher.TryCreate(template)
        ?? throw new InvalidOperationException($"template did not compile: {template}");

    [Fact]
    public void ConfiguredSpellCast_TalliesSpellRow_NotASwing()
    {
        // The cast line carries a first-person "You" source the UserHits regex
        // also matches — recognition on the LineDispatched pass must veto the
        // physical-swing classifier so it never becomes a melee hit.
        using Harness h = new(
            spellMatchers: new[] { ("blast", Matcher("You cast {s} at {target} for {damage} damage!")) });

        h.Feed("You cast fireball at the kobold for 12 damage!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(1, s.SpellHits);
        Assert.Equal(12, s.SpellMinDamage);
        Assert.Equal(12, s.SpellMaxDamage);
        Assert.Equal(12, s.SpellTotalDamage);
        // Not a swing: no physical figures move.
        Assert.Equal(0, s.Hits);
        Assert.Equal(0, s.LandedSwings);
        Assert.Equal(0, s.TotalSwings);
        Assert.Equal(0, s.PhysicalMaxDamage);
    }

    [Fact]
    public void WeaponProc_TalliesProcRow_NotASwing()
    {
        using Harness h = new();

        h.Feed("You slash the kobold for 8 damage!");
        h.Feed("Your weapon sears the kobold for 4 damage!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(1, s.Hits);          // the slash, still the only swing
        Assert.Equal(1, s.LandedSwings);
        Assert.Equal(1, s.ProcHits);
        Assert.Equal(4, s.ProcMinDamage);
        Assert.Equal(4, s.ProcMaxDamage);
        Assert.Equal(4, s.ProcTotalDamage);
        // The proc didn't touch the physical extent (8, not 4).
        Assert.Equal(8, s.PhysicalMinDamage);
    }

    [Fact]
    public void VictimOnlyProc_RightAfterOurHit_IsOurs()
    {
        // A proc naming only its victim goes to whoever just hit that monster.
        using Harness h = new();

        h.Feed("You slash the kobold for 8 damage!");
        h.Feed("The kobold takes 3 damage from the cold!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(1, s.Hits);
        Assert.Equal(1, s.ProcHits);
        Assert.Equal(3, s.ProcTotalDamage);
    }

    [Fact]
    public void VictimOnlyProc_WithoutOurHit_IsNotOurs()
    {
        using Harness h = new();

        h.Feed("The kobold takes 3 damage from the cold!");

        Assert.Equal(0, h.Stats.ProcHits);
    }

    [Fact]
    public void VictimOnlyProc_AfterSomeoneElsesHit_IsNotOurs()
    {
        using Harness h = new();

        h.Feed("The giant rat bites the kobold for 5 damage!");
        h.Feed("The kobold takes 3 damage from the cold!");

        Assert.Equal(0, h.Stats.ProcHits);
    }

    [Fact]
    public void ProcAndSpellDamage_CountTowardRoundTotal_NotAsSwings()
    {
        using Harness h = new(
            spellMatchers: new[] { ("blast", Matcher("You cast {s} at {target} for {damage} damage!")) });

        h.Feed("You slash the kobold for 8 damage!");          // swing  → round +8
        h.Feed("Your weapon sears the kobold for 4 damage!");   // proc   → round +4
        h.Feed("You cast fireball at the kobold for 12 damage!"); // spell → round +12
        h.Feed("*Combat Off*");                                  // close round → 24

        CombatSessionStats s = h.Stats;
        // The whole round's damage (swing + proc + spell) lands in the per-round total.
        Assert.Equal(1, s.RoundsWithDamage);
        Assert.Equal(24, s.RoundTotalDamage);
        Assert.Equal(24, s.RoundMinDamage);
        // But only the slash is a swing.
        Assert.Equal(1, s.Hits);
        Assert.Equal(1, s.LandedSwings);
        Assert.Equal(1, s.TotalSwings);
        Assert.Equal(1, s.ProcHits);
        Assert.Equal(1, s.SpellHits);
    }

    [Fact]
    public void Reset_ZeroesProcAndSpellRows()
    {
        using Harness h = new(
            spellMatchers: new[] { ("blast", Matcher("You cast {s} at {target} for {damage} damage!")) });

        h.Feed("You slash the kobold for 8 damage!");
        h.Feed("Your weapon sears the kobold for 4 damage!");
        h.Feed("You cast fireball at the kobold for 12 damage!");

        h.Tracker.Reset();

        CombatSessionStats s = h.Stats;
        Assert.Equal(0, s.ProcHits);
        Assert.Equal(0, s.SpellHits);
        Assert.Equal(0, s.ProcTotalDamage);
        Assert.Equal(0, s.SpellTotalDamage);
        Assert.Empty(s.Spells);
    }

    // ----- spell combat: phantom misses + per-spell breakdown ----------

    [Fact]
    public void SpellCastEmote_FollowedByLandedSpell_RetractsPhantomMiss()
    {
        // A spell-cast emote ("You scatter …!") matches the whiff skeleton and is
        // counted as a miss while engaged — but the spell landing right after means
        // that "miss" was the emote, so it's retracted. Spell-only combat therefore
        // doesn't inflate the miss count.
        using Harness h = new(
            spellMatchers: new[] { ("blast", Matcher("You cast {s} at {target} for {damage} damage!")) });
        h.Feed("*Combat Engaged*");
        h.Feed("You scatter some ashes in a sweeping motion!");   // cast emote
        h.Feed("You cast blast at the kobold for 809 damage!");   // the spell lands

        CombatSessionStats s = h.Stats;
        Assert.Equal(0, s.Misses);          // emote miss retracted
        Assert.Equal(1, s.SpellHits);
        SpellCombatStat blast = Assert.Single(s.Spells);
        Assert.Equal("blast", blast.Name);
        Assert.Equal(1, blast.Landed);
        Assert.Equal(0, blast.Misses);
        Assert.Equal(809, blast.MaxDamage);
        Assert.Equal(100d, blast.AccuracyPercent);
    }

    [Fact]
    public void SpellCast_Resisted_ReattributedAsSpellMiss_NotPhysical()
    {
        // A cast emote with no damage line following (resist / no effect) is not a
        // weapon whiff — at combat-off it's moved to the spell's resist count.
        using Harness h = new(
            spellMatchers: new[] { ("blast", Matcher("You cast {s} at {target} for {damage} damage!")) });
        h.Feed("*Combat Engaged*");
        h.Rounds.NoteOwnCast();
        h.Feed("You scatter some ashes in a sweeping motion!");   // cast emote, resisted
        h.Feed("*Combat Off*");

        CombatSessionStats s = h.Stats;
        Assert.Equal(0, s.Misses);          // not a physical whiff
        SpellCombatStat blast = Assert.Single(s.Spells);
        Assert.Equal(0, blast.Landed);
        Assert.Equal(1, blast.Misses);      // resisted cast
        Assert.Equal(1, blast.Casts);
    }

    // The per-monster record counts the same miss line. It is told only when a
    // spell landing proves the miss was a cast line, never on the guess made when a
    // fight ends: that guess took a weapon user's real whiff off the monster.
    [Fact]
    public void CastLineMissRetracted_FiresOnlyWhenTheSpellLands()
    {
        using Harness h = new(
            spellMatchers: new[] { ("blast", Matcher("You cast {s} at {target} for {damage} damage!")) });
        int retracted = 0;
        h.Tracker.CastLineMissRetracted += () => retracted++;

        h.Feed("*Combat Engaged*");
        h.Feed("You scatter some ashes in a sweeping motion!");
        h.Feed("You cast blast at the kobold for 809 damage!");   // landed: the miss was the cast line
        Assert.Equal(1, retracted);

        h.Rounds.NoteOwnCast();
        h.Feed("You scatter some ashes in a sweeping motion!");   // nothing lands
        h.Feed("*Combat Off*");                                   // the session moves it to the spell's resists
        Assert.Equal(1, h.Stats.Spells.Single().Misses);
        Assert.Equal(1, retracted);                               // and says nothing to the monster's record

        h.Feed("*Combat Engaged*");
        h.Feed("You punch acid slime for 8 damage!");
        h.Feed("You punch acid slime!");                          // a real whiff
        h.Feed("*Combat Off*");
        Assert.Equal(1, retracted);
    }

    // A heal or buff going out between rounds is not an attack cast: the round's
    // only swing, a whiff, stays a weapon miss.
    [Fact]
    public void Whiff_ThenABetweenRoundCast_StaysAPhysicalMiss()
    {
        using Harness h = new(
            spellMatchers: new[] { ("blast", Matcher("You cast {s} at {target} for {damage} damage!")) });
        h.Feed("*Combat Engaged*");
        h.Feed("You punch acid slime!");
        h.Rounds.NoteOwnCast(attack: false);
        h.Feed("*Combat Off*");

        CombatSessionStats s = h.Stats;
        Assert.Equal(1, s.Misses);
        Assert.Empty(s.Spells);
    }

    [Fact]
    public void WeaponWhiff_AfterAHit_StaysPhysical_NotReattributed()
    {
        // A genuine weapon whiff (a swing landed this combat) is NOT reattributed to
        // a spell resist at combat-off.
        using Harness h = new(
            spellMatchers: new[] { ("blast", Matcher("You cast {s} at {target} for {damage} damage!")) });
        h.Feed("*Combat Engaged*");
        h.Feed("You punch acid slime for 8 damage!");   // a real swing lands
        h.Feed("You punch acid slime!");                 // a real whiff
        h.Feed("*Combat Off*");

        CombatSessionStats s = h.Stats;
        Assert.Equal(1, s.Misses);          // kept as a physical miss
        Assert.Empty(s.Spells);
    }

    [Fact]
    public void WhiffOnlyCombat_WithoutACast_StaysPhysical()
    {
        // Knowing spells doesn't make a melee whiff a resisted cast — we never cast.
        using Harness h = new(
            spellMatchers: new[] { ("blast", Matcher("You cast {s} at {target} for {damage} damage!")) });
        h.Feed("*Combat Engaged*");
        h.Feed("You swing at the kobold, but miss!");
        h.Feed("*Combat Off*");

        CombatSessionStats s = h.Stats;
        Assert.Equal(1, s.Misses);
        Assert.Empty(s.Spells);
    }

    [Fact]
    public void CastersEyeSpell_OursOnlyAfterWeCast()
    {
        // "Dark flame sears the kobold" is what everyone in the room sees, so it's our
        // spell only when we cast lately.
        using Harness h = new(
            spellMatchers: new[] { ("dark flame", Matcher("Dark flame sears {target} for {damage} damage!")) });

        h.Feed("Dark flame sears the kobold for 10 damage!");
        Assert.Equal(0, h.Stats.SpellHits);

        h.Rounds.NoteOwnCast();
        h.Feed("Dark flame sears the kobold for 12 damage!");
        CombatSessionStats s = h.Stats;
        Assert.Equal(1, s.SpellHits);
        Assert.Equal(12, s.SpellTotalDamage);
        Assert.Equal(0, s.ProcHits);
        Assert.Equal("dark flame", Assert.Single(s.Spells).Name);
    }

    // ----- backstabs keep their own rate --------------------------------

    [Fact]
    public void Backstab_StaysOutOfTheRegularHitRate()
    {
        using Harness h = new();
        h.Feed("*Combat Engaged*");
        h.Feed("You surprise slash the kobold for 60 damage!");
        h.Feed("You slash the kobold for 8 damage!");
        h.Feed("You swing at the kobold, but miss!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(2, s.TotalSwings);       // the slash and the miss
        Assert.Equal(50d, s.HitPercent);
        Assert.Equal(1, s.Backstabs);
        Assert.Equal(100d, s.BackstabPercent);
    }

    [Fact]
    public void BackstabWhiff_IsTheStabsMiss_NotARegularOne()
    {
        // CombatManager reports the whiff after this tracker counted it.
        using Harness h = new();
        h.Feed("*Combat Engaged*");
        h.Feed("You swing at the kobold, but miss!");
        h.Tracker.OnBackstabResolved("You swing at the kobold, but miss!", landed: false);

        CombatSessionStats s = h.Stats;
        Assert.Equal(0, s.Misses);
        Assert.Equal(1, s.BackstabFails);
        Assert.Equal(0d, s.BackstabPercent);
        Assert.Equal(100d, s.BackstabFailPercent);
    }

    [Fact]
    public void BackstabWhiff_ReportedBeforeTheMissPattern_IsNotCountedTwice()
    {
        using Harness h = new();
        h.Router.LineDispatched += line =>
        {
            if (line.Text.Contains("miss")) h.Tracker.OnBackstabResolved(line.Text, landed: false);
        };
        h.Feed("*Combat Engaged*");
        h.Feed("You swing at the kobold, but miss!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(0, s.Misses);
        Assert.Equal(1, s.BackstabFails);
    }

    [Fact]
    public void BackstabAnsweredByARegularHit_FailedButTheHitCounts()
    {
        // The sneak broke: the round swung as a normal attack.
        using Harness h = new();
        h.Feed("*Combat Engaged*");
        h.Feed("You slash the kobold for 8 damage!");
        h.Tracker.OnBackstabResolved("You slash the kobold for 8 damage!", landed: false);

        CombatSessionStats s = h.Stats;
        Assert.Equal(1, s.Hits);
        Assert.Equal(1, s.BackstabFails);
        Assert.Equal(0, s.Misses);
    }

    [Fact]
    public void LaterIdenticalMiss_AfterAStabWhiff_IsARegularMiss()
    {
        using Harness h = new();
        h.Feed("*Combat Engaged*");
        h.Feed("You swing at the kobold, but miss!");
        h.Tracker.OnBackstabResolved("You swing at the kobold, but miss!", landed: false);
        h.Feed("You swing at the kobold, but miss!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(1, s.Misses);
        Assert.Equal(1, s.BackstabFails);
    }

    // A chained spell's line (necromantic bolt's drain) adds to the cast it follows,
    // not a proc or a cast of its own (report paradigm-20260929-063213).
    [Fact]
    public void FollowUpLine_AddsToTheCastItFollows()
    {
        using Harness h = new(
            spellMatchers: new[] { ("necromantic bolt", Matcher("You fire a necromantic bolt at {target} for {damage} damage!")) },
            followUps: new[] { new SpellLineMatcher("necromantic bolt", Matcher("{target}'s life is drained for {damage} damage!"), FollowUp: true) });

        h.Feed("You fire a necromantic bolt at kobold for 350 damage!");
        h.Feed("kobold's life is drained for 95 damage!");
        h.Feed("You fire a necromantic bolt at kobold for 300 damage!");

        CombatSessionStats s = h.Stats;
        SpellCombatStat bolt = Assert.Single(s.Spells);
        Assert.Equal(2, bolt.Landed);
        Assert.Equal(300, bolt.MinDamage);
        Assert.Equal(445, bolt.MaxDamage);
        Assert.Equal(745, bolt.TotalDamage);
        Assert.Equal(0, s.ProcHits);
    }
}
