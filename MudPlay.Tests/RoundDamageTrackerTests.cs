using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// RoundDamageTracker: round boundaries, the per-combatant damage ledger, the ring
// buffer, and the RoundComplete emit contract. The damage lines are the engine's own
// wordings (Stock 1.11p message table and its hardcoded melee forms).
public sealed class RoundDamageTrackerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public PlayerState State { get; } = new();
        public LogService Log { get; } = new();
        public RoundDamageTracker Tracker { get; }
        public List<RoundSummary> Completed { get; } = new();
        public DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public bool OwnSpell;
        // The settle timer the tracker armed, when the harness gives it a scheduler.
        public Action? Settle;

        public Harness(Func<bool>? shouldWriteTrace = null, bool withScheduler = false)
        {
            DefaultPatterns.Seed(Router);
            Tracker = new RoundDamageTracker(Router, State, Log, shouldWriteTrace, clock: () => Now,
                scheduleDelay: withScheduler
                    ? (_, callback) => { Settle = callback; return new Handle(() => Settle = null); }
                    : null);
            Tracker.RoundComplete += Completed.Add;
            Tracker.SetNameSources(
                partyNames: () => new[] { "Fujin Blade", "Bob Smith" },
                selfName: () => "Fujin");
            Tracker.SetOwnSpellLineCheck(line => OwnSpell && line.StartsWith("Dark flame sears", StringComparison.Ordinal));
            Tracker.NoteRoomEntities(new RoomEntitiesObservation(
                "Also here: large giant rat, goblin, Bob.",
                new[]
                {
                    new RoomEntity("large giant rat", "giant rat", EntityKind.Monster, 1),
                    new RoomEntity("goblin", "goblin", EntityKind.Monster, 2),
                    new RoomEntity("Bob", "Bob", EntityKind.Player, null),
                },
                Now));
        }

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        // The 5 s combat tick closes the round.
        public RoundSummary CloseRound()
        {
            Now = Now.AddSeconds(5);
            Tracker.OnCombatTick();
            return Completed[^1];
        }

        public void Dispose() => Tracker.Dispose();

        private sealed class Handle(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }

    private static CombatantDamage Row(RoundSummary r, string name)
        => r.Combatants.Single(c => c.Name == name);

    // ----- attribution ------------------------------------------------

    [Fact]
    public void OwnSwing_IsDealtByYou_TakenByTheMonster()
    {
        using Harness h = new();
        h.Feed("You slash the large giant rat for 12 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(12, r.DamageDealt);
        Assert.Equal(12, Row(r, "large giant rat").Taken);
    }

    [Fact]
    public void MonsterBaseName_CountsUnderTheRoomDisplayName()
    {
        using Harness h = new();
        h.Feed("You slash the giant rat for 7 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(7, Row(r, "large giant rat").Taken);
    }

    [Fact]
    public void PartyMembersSwing_IsTheirs_NotYours()
    {
        using Harness h = new();
        h.Feed("Bob slashes large giant rat for 9 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(0, r.DamageDealt);
        Assert.Equal(9, Row(r, "Bob").Dealt);
        Assert.Equal(9, Row(r, "large giant rat").Taken);
    }

    [Theory]
    [InlineData("The large giant rat bites you for 3 damage!")]
    // A monster attack's own verb phrase: several words around "you".
    [InlineData("The large giant rat claws you with its pincers for 3 damage!")]
    [InlineData("The large giant rat critically chops you for 3 damage!")]
    // A monster's spell on you names the caster first.
    [InlineData("The large giant rat casts acid bolt on you for 3 damage!")]
    public void MonsterHitsYou_IsTakenByYou_DealtByTheMonster(string line)
    {
        using Harness h = new();
        h.Feed(line);
        RoundSummary r = h.CloseRound();

        Assert.Equal(3, r.DamageTaken);
        Assert.Equal(0, r.DamageDealt);
        Assert.Equal(3, Row(r, "large giant rat").Dealt);
    }

    [Fact]
    public void MonsterHitsPartyMember()
    {
        using Harness h = new();
        h.Feed("The goblin bites Bob for 2 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(2, Row(r, "goblin").Dealt);
        Assert.Equal(2, Row(r, "Bob").Taken);
    }

    [Fact]
    public void PartyMembersSpell_NamesTheCaster()
    {
        using Harness h = new();
        h.Feed("Bob casts fireball on goblin for 15 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(15, Row(r, "Bob").Dealt);
        Assert.Equal(15, Row(r, "goblin").Taken);
    }

    [Fact]
    public void SpellOnYouWithNoCaster_IsTakenByYou_FromUnknown()
    {
        using Harness h = new();
        h.State.InCombat = true;
        h.Feed("Acid sears you for 6 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(6, r.DamageTaken);
        Assert.Equal(6, r.UnknownDealt);
    }

    [Fact]
    public void AreaEffect_IsUnknownBothWays()
    {
        using Harness h = new();
        h.State.InCombat = true;
        h.Feed("An earthquake rocks the room for 20 damage!");
        RoundSummary r = h.CloseRound();

        Assert.All(r.Combatants, c => Assert.Equal((0, 0), (c.Dealt, c.Taken)));
        Assert.Equal(20, r.UnknownDealt);
        Assert.Equal(20, r.UnknownTaken);
    }

    // Our room spell names no victim: every monster in the room takes the amount
    // (report paradigm-20260929-063213).
    [Fact]
    public void OurRoomSpell_HitsEveryMonsterInTheRoom()
    {
        using Harness h = new();
        List<string> tags = new();
        h.Tracker.LineAttributed += (_, tag) => tags.Add(tag);
        h.Feed("A hellish storm of fire and brimstone scorches your foes for 50 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(100, Row(r, DamageLineAttributor.Self).Dealt);
        Assert.Equal(50, Row(r, "large giant rat").Taken);
        Assert.Equal(50, Row(r, "goblin").Taken);
        Assert.Equal(0, Row(r, "Bob").Taken);          // a player, not a foe
        Assert.Equal(0, r.UnknownTaken);
        Assert.Equal("You → 2 foes 50 each", Assert.Single(tags));
    }

    // Spirit horde is scoped to the room in the game data but its line says "your
    // foe": the scope decides, and every monster takes the amount (report
    // paradigm-20261006-051627, where it all went to nobody).
    [Fact]
    public void OurRoomSpell_WordedForOneFoe_StillHitsEveryMonster()
    {
        using Harness h = new();
        h.Tracker.SetOwnSpellLineCheck(
            line => line.StartsWith("A horde of shrieking spirits", StringComparison.Ordinal),
            line => line.StartsWith("A horde of shrieking spirits", StringComparison.Ordinal));
        h.Tracker.NoteOwnCast();
        h.Feed("A horde of shrieking spirits ravages your foe for 167 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(334, Row(r, DamageLineAttributor.Self).Dealt);
        Assert.Equal(167, Row(r, "large giant rat").Taken);
        Assert.Equal(167, Row(r, "goblin").Taken);
        Assert.Equal(0, r.UnknownTaken);
    }

    // The same wording from a spell that isn't scoped to the room is not spread.
    [Fact]
    public void OurSpell_WordedForOneFoe_NotRoomScoped_IsNotSpread()
    {
        using Harness h = new();
        h.Tracker.SetOwnSpellLineCheck(
            line => line.StartsWith("A horde of shrieking spirits", StringComparison.Ordinal),
            _ => false);
        h.Tracker.NoteOwnCast();
        h.Feed("A horde of shrieking spirits ravages your foe for 167 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(167, Row(r, DamageLineAttributor.Self).Dealt);
        Assert.Equal(0, Row(r, "goblin").Taken);
        Assert.Equal(167, r.UnknownTaken);
    }

    // The server repeats an announced spell every round with no new cast from us, so
    // a later round lands long after the cast was sent. It is still ours while the
    // engine says the spell is repeating, and nobody's once it isn't.
    [Fact]
    public void RepeatedSpellRound_LongAfterTheCast_IsStillOurs_WhileItRepeats()
    {
        using Harness h = new() { OwnSpell = true };
        h.State.InCombat = true;
        bool repeating = true;
        h.Tracker.SetOwnSpellRepeating(() => repeating);
        h.Tracker.NoteOwnCast();
        h.Now = h.Now.AddSeconds(12);

        h.Feed("Dark flame sears goblin for 20 damage!");
        RoundSummary r = h.CloseRound();
        Assert.Equal(20, Row(r, DamageLineAttributor.Self).Dealt);

        repeating = false;
        h.Now = h.Now.AddSeconds(12);
        h.Feed("Dark flame sears goblin for 20 damage!");
        r = h.CloseRound();
        Assert.Equal(20, r.UnknownDealt);
    }

    // A party member's room spell names nobody; the caster's announce right before it
    // says whose it is, and every monster in the room takes the amount (report
    // paradigm-20260929-161536).
    [Fact]
    public void PartyMembersRoomSpell_IsTheirs_OnEveryMonster()
    {
        using Harness h = new();
        List<string> tags = new();
        h.Tracker.LineAttributed += (_, tag) => tags.Add(tag);
        h.State.InCombat = true;
        h.Feed("Bob summons a powerful tempest into the room!");
        h.Feed("An acidic tempest blows through the room for 431 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(862, Row(r, "Bob").Dealt);
        Assert.Equal(431, Row(r, "large giant rat").Taken);
        Assert.Equal(431, Row(r, "goblin").Taken);
        Assert.Equal(0, r.UnknownDealt);
        Assert.Equal(0, r.UnknownTaken);
        Assert.Equal("Bob → 2 foes 431 each", Assert.Single(tags));
    }

    // Only the line right before counts, and a monster's announce is never a caster
    // whose spell hits the monsters.
    [Theory]
    [InlineData("goblin summons a powerful tempest into the room!")]
    [InlineData("Bob moves to attack goblin.")]
    public void RoomDamage_WithoutAPlayersAnnounce_StaysUnknown(string previous)
    {
        using Harness h = new();
        h.State.InCombat = true;
        h.Feed(previous);
        h.Feed("An acidic tempest blows through the room for 431 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(431, r.UnknownDealt);
        Assert.Equal(431, r.UnknownTaken);
    }

    // ----- same-named monsters and the HP cap -------------------------------

    // Three 540-HP muckworms and two 400-HP drowned dead, with the HP estimates wired in
    // as the app does.
    private static (Harness H, MonsterHpTracker Hp) MuckRoom(bool capAtHp = true)
    {
        Harness h = new();
        MonsterHpTracker hp = new(n => n == 1 ? 540 : 400, _ => 0);
        h.Tracker.SetMonsterHp(hp.TargetOf, hp.RoomMonsters, () => capAtHp);
        h.Tracker.Attributed += line =>
        {
            if (line.Foes > 0) hp.NoteAreaDamage(line.Sides.Amount);
            else hp.NoteDamage(line.Sides);
        };
        RoomEntitiesObservation room = new("Also here: muckworm, muckworm, muckworm, drowned dead, drowned dead.",
            new[]
            {
                new RoomEntity("muckworm", "muckworm", EntityKind.Monster, 1),
                new RoomEntity("muckworm", "muckworm", EntityKind.Monster, 1),
                new RoomEntity("muckworm", "muckworm", EntityKind.Monster, 1),
                new RoomEntity("drowned dead", "drowned dead", EntityKind.Monster, 2),
                new RoomEntity("drowned dead", "drowned dead", EntityKind.Monster, 2),
            }, DateTimeOffset.UtcNow);
        hp.NoteRoomEntities(room);
        h.Tracker.NoteRoomEntities(room);
        h.State.InCombat = true;
        return (h, hp);
    }

    // A monster can't take more than it has left: an 812 room spell on 540-HP
    // muckworms counts 540 each, and we dealt what they took (user, 2026-09-29).
    [Fact]
    public void RoomSpell_PastAMonstersHp_CountsOnlyWhatKilledIt()
    {
        (Harness h, _) = MuckRoom();
        using (h)
        {
            h.Feed("A hellish storm of fire and brimstone scorches your foes for 812 damage!");
            RoundSummary r = h.CloseRound();

            Assert.Equal(3 * 540, Row(r, "muckworm").Taken);
            Assert.Equal(2 * 400, Row(r, "drowned dead").Taken);
            Assert.Equal(3 * 540 + 2 * 400, Row(r, DamageLineAttributor.Self).Dealt);
        }
    }

    // With Cap at monster HP off, every line counts the number the game printed.
    [Fact]
    public void CapOff_KillingBlowCountsInFull()
    {
        (Harness h, _) = MuckRoom(capAtHp: false);
        using (h)
        {
            h.Feed("You slash muckworm for 500 damage!");
            h.Feed("You slash muckworm for 80 damage!");
            h.Feed("A hellish storm of fire and brimstone scorches your foes for 812 damage!");
            RoundSummary r = h.CloseRound();

            Assert.Equal(580 + 3 * 812, Row(r, "muckworm").Taken);
            Assert.Equal(2 * 812, Row(r, "drowned dead").Taken);
            Assert.Equal(580 + 5 * 812, Row(r, DamageLineAttributor.Self).Dealt);
        }
    }

    // Each round keeps its tallies both ways, so a reader with its own cap choice (the
    // Round Totals window) can show either without changing what the terminal prints.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Round_CarriesBothCapChoices_WhicheverTheSettingIs(bool capAtHp)
    {
        (Harness h, _) = MuckRoom(capAtHp);
        using (h)
        {
            h.Feed("A hellish storm of fire and brimstone scorches your foes for 812 damage!");
            RoundSummary r = h.CloseRound();
            Assert.Equal(capAtHp, r.Capped);

            static int Taken(IReadOnlyList<RoundTotalsRow> rows, string name) => rows.First(x => x.Name.StartsWith(name)).Taken;
            static int Dealt(IReadOnlyList<RoundTotalsRow> rows, string name) => rows.First(x => x.Name.StartsWith(name)).Dealt;

            IReadOnlyList<RoundTotalsRow> capped = RoundTotalsFormatter.Rows(r, Everyone, capAtHp: true);
            Assert.Equal(3 * 540, Taken(capped, "muckworm"));
            Assert.Equal(3 * 540 + 2 * 400, Dealt(capped, DamageLineAttributor.Self));

            IReadOnlyList<RoundTotalsRow> full = RoundTotalsFormatter.Rows(r, Everyone, capAtHp: false);
            Assert.Equal(3 * 812, Taken(full, "muckworm"));
            Assert.Equal(5 * 812, Dealt(full, DamageLineAttributor.Self));

            // Each monster's own row follows the same choice.
            IReadOnlyList<RoundTotalsRow> eachFull = RoundTotalsFormatter.Rows(r, Everyone, eachMonster: true, capAtHp: false);
            Assert.Equal(812, Taken(eachFull, "muckworm #1"));
            IReadOnlyList<RoundTotalsRow> eachCapped = RoundTotalsFormatter.Rows(r, Everyone, eachMonster: true, capAtHp: true);
            Assert.Equal(540, Taken(eachCapped, "muckworm #1"));

            // Asked for nothing in particular, the rows are as the setting counted them.
            Assert.Equal(capAtHp ? 3 * 540 : 3 * 812, Taken(RoundTotalsFormatter.Rows(r, Everyone), "muckworm"));
        }
    }

    // Stacked (the default): a shared row says how many there were.
    [Fact]
    public void Table_Stacked_LabelsSharedRowsWithTheCount()
    {
        (Harness h, _) = MuckRoom();
        using (h)
        {
            h.Feed("A hellish storm of fire and brimstone scorches your foes for 812 damage!");
            IReadOnlyList<string> table = RoundTotalsFormatter.Table(h.CloseRound(), Everyone);

            Assert.Contains(table, l => l.Contains("muckworm x3"));
            Assert.Contains(table, l => l.Contains("drowned dead x2"));
        }
    }

    // One row per monster: a hit on a shared name lands on the first listed, a room
    // spell on each.
    [Fact]
    public void Table_EachMonster_SplitsSharedNames()
    {
        (Harness h, _) = MuckRoom();
        using (h)
        {
            h.Feed("You slash muckworm for 100 damage!");
            h.Feed("A hellish storm of fire and brimstone scorches your foes for 300 damage!");
            RoundSummary r = h.CloseRound();

            Dictionary<string, int> taken = r.EachMonster!.ToDictionary(m => m.Name, m => m.Taken);
            Assert.Equal(400, taken["muckworm #1"]);
            Assert.Equal(300, taken["muckworm #2"]);
            Assert.Equal(300, taken["muckworm #3"]);
            Assert.Equal(300, taken["drowned dead #1"]);

            IReadOnlyList<string> table = RoundTotalsFormatter.Table(r, Everyone, eachMonster: true);
            Assert.Contains(table, l => l.Contains("muckworm #3"));
            Assert.DoesNotContain(table, l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\[ muckworm\s+\d"));
        }
    }

    // An estimate already at 0 that still takes a hit was wrong: that hit counts in full.
    [Fact]
    public void HitOnAMonsterEstimatedDead_CountsInFull()
    {
        (Harness h, _) = MuckRoom();
        using (h)
        {
            h.Feed("You slash muckworm for 600 damage!");
            h.Feed("You slash muckworm for 50 damage!");
            RoundSummary r = h.CloseRound();

            Assert.Equal(540 + 50, Row(r, "muckworm").Taken);
        }
    }

    // A damage shield's line names only whom it struck back at; the hit just before
    // says whose shield it was.
    [Theory]
    [InlineData("large giant rat is scorched for 4 damage!")]
    [InlineData("The shield spikes stab large giant rat for 4 damage!")]
    [InlineData("A counterstrike at large giant rat does 4 damage!")]
    public void ShieldReflect_OnAMonsterThatHitUs_IsOurs(string reflect)
    {
        using Harness h = new();
        h.State.InCombat = true;
        h.Feed("The large giant rat bites you for 29 damage!");
        h.Feed(reflect);
        RoundSummary r = h.CloseRound();

        Assert.Equal(4, Row(r, DamageLineAttributor.Self).Dealt);
        Assert.Equal(4, Row(r, "large giant rat").Taken);
        Assert.Equal(0, r.UnknownDealt);
    }

    [Fact]
    public void ShieldReflect_OnAMonsterThatHitAPartyMember_IsTheirs()
    {
        using Harness h = new();
        h.State.InCombat = true;
        h.Feed("The goblin bites Bob for 2 damage!");
        h.Feed("goblin is shocked for 3 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(3, Row(r, "Bob").Dealt);
        Assert.Equal(3, Row(r, "goblin").Taken);
    }

    [Fact]
    public void ShieldReflect_OnUs_IsTheMonstersWeHit()
    {
        using Harness h = new();
        h.State.InCombat = true;
        h.Feed("You slash goblin for 11 damage!");
        h.Feed("You are scorched for 5 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(5, Row(r, "goblin").Dealt);
        Assert.Equal(5, Row(r, DamageLineAttributor.Self).Taken);
        Assert.Equal(0, r.UnknownDealt);
    }

    // The razor boar's spines (report paradigm-20261009-023430): the party member's
    // three points of it sat in the round's "unknown" row, and ours had no attacker.
    [Fact]
    public void SpinesOnUs_AreTheMonstersWeHit()
    {
        using Harness h = new();
        h.State.InCombat = true;
        h.Feed("You slash goblin for 11 damage!");
        h.Feed("You are jabbed by spines for 1 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(1, Row(r, "goblin").Dealt);
        Assert.Equal(1, Row(r, DamageLineAttributor.Self).Taken);
        Assert.Equal(0, r.UnknownDealt);
    }

    [Fact]
    public void SpinesOnAPartyMember_AreTheMonstersTheyHit()
    {
        using Harness h = new();
        h.State.InCombat = true;
        h.Feed("Bob jumpkicks goblin for 94 damage!");
        h.Feed("Bob is jabbed by spines for 1 damage!");
        h.Feed("Bob jumpkicks goblin for 74 damage!");
        h.Feed("Bob is jabbed by spines for 2 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(3, Row(r, "goblin").Dealt);
        Assert.Equal(3, Row(r, "Bob").Taken);
        Assert.Equal(0, r.UnknownDealt);
    }

    // A shield whose wording the client has never seen: the game data says the monster
    // just hit carries one, and the line directly follows that hit.
    [Theory]
    [InlineData("You slash goblin for 11 damage!", "You are lashed by thorns for 3 damage!", DamageLineAttributor.Self)]
    [InlineData("Bob jumpkicks goblin for 94 damage!", "Bob is lashed by thorns for 3 damage!", "Bob")]
    public void AnUnknownShieldWording_OnAMonsterTheDataGivesAShield_IsThatMonsters(
        string hit, string struckBack, string victim)
    {
        using Harness h = new();
        h.Tracker.SetDamageShieldCheck(number => number == 2);   // the goblin
        h.State.InCombat = true;
        h.Feed(hit);
        h.Feed(struckBack);
        RoundSummary r = h.CloseRound();

        Assert.Equal(3, Row(r, "goblin").Dealt);
        Assert.Equal(3, Row(r, victim).Taken);
        Assert.Equal(0, r.UnknownDealt);
    }

    [Fact]
    public void AnUnknownWording_OnAMonsterWithNoShield_IsNotItsDoing()
    {
        using Harness h = new();
        h.Tracker.SetDamageShieldCheck(number => number == 2);   // the goblin, not the rat
        h.State.InCombat = true;
        h.Feed("You slash large giant rat for 11 damage!");
        h.Feed("You are poisoned for 5 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(0, Row(r, "large giant rat").Dealt);
        Assert.Equal(5, Row(r, DamageLineAttributor.Self).Taken);
    }

    [Fact]
    public void AnUnknownWording_NotDirectlyAfterTheHit_IsNotTheShields()
    {
        using Harness h = new();
        h.Tracker.SetDamageShieldCheck(number => number == 2);
        h.State.InCombat = true;
        h.Feed("You slash goblin for 11 damage!");
        h.Feed("The goblin lunges at you, but you dodge out of the way!");
        h.Feed("You are poisoned for 5 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(0, Row(r, "goblin").Dealt);
        Assert.Equal(5, Row(r, DamageLineAttributor.Self).Taken);
    }

    // Only a shield's own wording counts — someone's unnamed spell right after the
    // monster's hit isn't ours.
    [Fact]
    public void UnnamedSpell_AfterAMonsterHit_StaysUnknown()
    {
        using Harness h = new();
        h.State.InCombat = true;
        h.Feed("The goblin bites you for 2 damage!");
        h.Feed("A brilliant beam zaps goblin for 10 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(10, r.UnknownDealt);
    }

    // Damage nobody dealt — a condition or effect — is yours to take and no one's to
    // deal, so it doesn't land in unknown (report paradigm-20260929-003750).
    [Theory]
    [InlineData("You are poisoned for 5 damage!")]
    [InlineData("Your blood is drained for 5 damage!")]
    public void DamageToYouWithNoSource_CountsInCombat(string line)
    {
        using Harness h = new();
        h.State.InCombat = true;
        h.Feed(line);
        RoundSummary r = h.CloseRound();

        Assert.Equal(5, r.DamageTaken);
        Assert.Equal(0, r.UnknownDealt);
    }

    // Report paradigm-20261009-120757: the room's heat, two seconds after the round,
    // showed in Round Totals as a round of its own ("Round 2 taken: You 46").
    [Fact]
    public void RoomSpellDamage_MidFight_OpensNoRound_AndIsTakenInOneAlreadyOpen()
    {
        using Harness h = new();
        h.Tracker.SetOffRoundDamageCheck(OffRoundDamageLinesTests.ProbeIn(OffRoundDamageLinesTests.MagmaHeat));
        h.State.InCombat = true;

        h.Feed("You are seared by the flames for 46 damage!");
        h.Tracker.OnCombatTick();
        Assert.Empty(h.Completed);

        h.Feed("The goblin bites you for 2 damage!");
        h.Feed("You are seared by the flames for 28 damage!");
        RoundSummary r = h.CloseRound();
        Assert.Single(h.Completed);
        Assert.Equal(30, r.DamageTaken);
    }

    [Fact]
    public void TheSameTextFromAMonsterElsewhere_OpensARound()
    {
        using Harness h = new();
        h.Tracker.SetOffRoundDamageCheck(OffRoundDamageLinesTests.ProbeIn(roomSpell: 0));
        h.State.InCombat = true;

        h.Feed("A chaotic storm assaults you for 30 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(30, r.DamageTaken);
    }

    [Fact]
    public void DamageToYouWithNoSource_BetweenFights_OpensNoRound()
    {
        using Harness h = new();
        h.Feed("You are poisoned for 5 damage!");
        h.Tracker.OnCombatTick();

        Assert.Empty(h.Completed);
    }

    [Fact]
    public void YourProcLine_IsYours()
    {
        using Harness h = new();
        h.Feed("Your streaking arrow strikes goblin for 7 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(7, r.DamageDealt);
        Assert.Equal(7, Row(r, "goblin").Taken);
    }

    // "Dark flame sears goblin" is what the caster AND the room see, so it's ours only
    // when it's our attack spell and we cast lately.
    [Fact]
    public void CasterEyeSpellLine_IsYours_WhenYouCastIt()
    {
        using Harness h = new() { OwnSpell = true };
        h.Tracker.NoteOwnCast();
        h.Feed("Dark flame sears goblin for 12 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(12, r.DamageDealt);
        Assert.Equal(12, Row(r, "goblin").Taken);
    }

    [Fact]
    public void CasterEyeSpellLine_IsUnknown_WhenYouDidntCast()
    {
        using Harness h = new() { OwnSpell = true };
        h.State.InCombat = true;
        h.Feed("Dark flame sears goblin for 12 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(0, r.DamageDealt);
        Assert.Equal(12, r.UnknownDealt);
    }

    [Fact]
    public void CasterEyeSpellLine_IsUnknown_WhenYourCastIsStale()
    {
        using Harness h = new() { OwnSpell = true };
        h.State.InCombat = true;
        h.Tracker.NoteOwnCast();
        h.Now = h.Now.AddSeconds(10);
        h.Feed("Dark flame sears goblin for 12 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(12, r.UnknownDealt);
    }

    // A weapon proc names only its victim; it lands right after the swing that set it
    // off, so it's the hitter's (report paradigm-20260929-043055).
    [Fact]
    public void WeaponProc_GoesToWhoeverJustHitThatMonster()
    {
        using Harness h = new();
        h.Feed("You cut goblin for 11 damage!");
        h.Feed("goblin takes 3 damage from the cold!");
        h.Feed("Flames burn goblin for 15 damage!");
        h.Feed("Bob shatters large giant rat for 47 damage!");
        h.Feed("large giant rat's life is drained for 12 damage!");
        h.Feed("A magic missile streaks out at large giant rat for 19 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(29, r.DamageDealt);
        Assert.Equal(78, Row(r, "Bob").Dealt);
        Assert.Equal(0, r.UnknownDealt);
    }

    // A caster-less hit on a monster nobody just hit stays unknown.
    [Fact]
    public void CasterlessHit_WithNoHitBeforeIt_StaysUnknown()
    {
        using Harness h = new();
        h.Feed("You cut goblin for 11 damage!");
        h.Feed("Flames burn large giant rat for 15 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(15, r.UnknownDealt);
    }

    [Fact]
    public void NonDamageLine_IsIgnored()
    {
        using Harness h = new();
        h.Feed("You take 5 damage for bashing the door!");
        h.Feed("Bob gossips: I hit it hard");
        h.Tracker.OnCombatTick();

        Assert.Empty(h.Completed);
    }

    // ----- round boundaries ------------------------------------------

    [Fact]
    public void Fresh_NoCompletedRounds()
    {
        using Harness h = new();
        Assert.Empty(h.Completed);
        Assert.Empty(h.Tracker.Recent);
    }

    [Fact]
    public void OwnSwing_StartsRound_NotYetClosed()
    {
        using Harness h = new();
        h.Feed("You hit goblin for 5 damage!");
        Assert.Empty(h.Completed);
    }

    [Fact]
    public void CombatOff_ClosesRound_EmitsSummary()
    {
        using Harness h = new();
        h.State.Hp = 100;
        h.State.Ma = 50;
        h.Feed("You hit large giant rat for 5 damage!");
        h.Feed("The large giant rat bites you for 3 damage!");
        h.State.Hp = 97;        // post-damage snapshot
        h.Feed("*Combat Off*");

        Assert.Single(h.Completed);
        RoundSummary s = h.Completed[0];
        Assert.Equal(1, s.RoundNumber);
        Assert.Equal(1, s.FightRound);
        Assert.Equal(5, s.DamageDealt);
        Assert.Equal(3, s.DamageTaken);
        Assert.Equal(100, s.HpBefore);
        Assert.Equal(97,  s.HpAfter);
    }

    [Fact]
    public void MissOnly_StillOpensARound()
    {
        using Harness h = new();
        h.Feed("The goblin swings at you");
        RoundSummary r = h.CloseRound();

        Assert.All(r.Combatants, c => Assert.Equal((0, 0), (c.Dealt, c.Taken)));
        Assert.Equal(0, r.UnknownDealt);
    }

    [Fact]
    public void CombatTick_NoOpenRound_ClosesNothing()
    {
        // Idle ticks between fights must not fabricate empty rounds.
        using Harness h = new();
        h.Tracker.OnCombatTick();
        h.Tracker.OnCombatTick();
        Assert.Empty(h.Completed);
    }

    [Fact]
    public void BurstOfSwings_OneTick_CollapsesToSingleRound()
    {
        // A whole 5-second window's worth of swings belongs to ONE round;
        // the boundary is the tick, not the damage lines.
        using Harness h = new();
        h.Feed("You hit goblin for 4 damage!");
        h.Feed("You hit goblin for 6 damage!");
        h.Feed("You hit goblin for 7 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Single(h.Completed);
        Assert.Equal(17, r.DamageDealt);
    }

    [Fact]
    public void BackToBackRounds_ShareAFight_AGapStartsANewOne()
    {
        using Harness h = new();
        h.Feed("You hit goblin for 5 damage!");
        h.CloseRound();
        h.Now = h.Now.AddSeconds(5);
        h.Feed("You hit goblin for 8 damage!");
        h.CloseRound();
        h.Now = h.Now.AddSeconds(30);
        h.Feed("You hit large giant rat for 2 damage!");
        h.CloseRound();

        Assert.Equal(new[] { 1, 2, 3 }, h.Completed.Select(r => r.RoundNumber));
        Assert.Equal(new[] { 1, 2, 1 }, h.Completed.Select(r => r.FightRound));
        Assert.Equal(new[] { 5, 8, 2 }, h.Completed.Select(r => r.DamageDealt));
    }

    // Monsters that walk in right after a kill are a new fight once the room was
    // clear, however quickly they follow (report paradigm-20260928-231225).
    [Fact]
    public void RoomClearedOfHostiles_RestartsTheRoundCount()
    {
        using Harness h = new();
        h.State.InCombat = true;
        h.Feed("You hit goblin for 5 damage!");
        h.CloseRound();
        h.Feed("You hit goblin for 8 damage!");
        h.State.InCombat = false;   // the goblin died; the room is clear
        h.CloseRound();
        h.State.InCombat = true;    // a new monster walks in straight away
        h.Feed("You hit large giant rat for 2 damage!");
        h.CloseRound();

        Assert.Equal(new[] { 1, 2, 1 }, h.Completed.Select(r => r.FightRound));
    }

    // The kill that clears the room drops InCombat before the round closes; the round
    // was still one we fought in. A member's fight we only stood by for isn't.
    [Fact]
    public void Engaged_StaysSetWhenTheKillEndsCombat_AndIsOffForAFightWeStoodByFor()
    {
        using Harness h = new();
        h.State.InCombat = true;
        h.Feed("You hit goblin for 8 damage!");
        h.State.InCombat = false;   // the goblin died; the round closes on that
        h.Feed("Bob hits large giant rat for 4 damage!");
        h.CloseRound();

        Assert.Equal(new[] { true, false }, h.Completed.Select(r => r.Engaged));
    }

    // ----- Reset path -------------------------------------------------

    [Fact]
    public void Reset_ClearsRing_ResetsCounter()
    {
        using Harness h = new();
        h.Feed("You hit goblin for 5 damage!");
        h.Feed("*Combat Off*");
        Assert.Single(h.Tracker.Recent);

        h.Tracker.Reset();
        Assert.Empty(h.Tracker.Recent);

        h.Feed("You hit goblin for 8 damage!");
        h.Feed("*Combat Off*");
        Assert.Equal(2, h.Completed.Count);
        Assert.Equal(1, h.Completed[1].RoundNumber);
        Assert.Equal(1, h.Completed[1].FightRound);
    }

    [Fact]
    public void Reset_MidRound_AbandonsAccumulation()
    {
        using Harness h = new();
        h.Feed("You hit goblin for 5 damage!");
        h.Tracker.Reset();
        h.Feed("*Combat Off*");
        Assert.Empty(h.Completed);
    }

    // ----- Recent ring ------------------------------------------------

    [Fact]
    public void Recent_TrimmedToRingCapacity_OldestFirst()
    {
        using Harness h = new();
        for (int i = 0; i < RoundDamageTracker.RingCapacity + 10; i++)
        {
            h.Feed($"You hit goblin for {i + 1} damage!");
            h.Feed("*Combat Off*");
        }

        Assert.Equal(RoundDamageTracker.RingCapacity, h.Tracker.Recent.Count);
        Assert.Equal(RoundDamageTracker.RingCapacity + 10, h.Tracker.Recent[^1].DamageDealt);
        Assert.Equal(11, h.Tracker.Recent[0].DamageDealt);
    }

    // ----- printed lines ---------------------------------------------

    [Fact]
    public void Formatter_BiggestFirst_UnknownLast_ZerosLeftOut()
    {
        using Harness h = new();
        h.State.InCombat = true;
        h.Feed("You slash large giant rat for 45 damage!");
        h.Feed("Bob slashes large giant rat for 30 damage!");
        h.Feed("The large giant rat bites you for 12 damage!");
        h.Feed("An earthquake rocks the room for 8 damage!");
        RoundSummary r = h.CloseRound();

        (string dealt, string taken) = RoundTotalsFormatter.Format(r);
        Assert.Equal("[Round 1 dealt: You 45, Bob 30, large giant rat 12, goblin 0, unknown 8]", dealt);
        Assert.Equal("[Round 1 taken: large giant rat 75, You 12, goblin 0, Bob 0, unknown 8]", taken);
    }

    // Everyone in the room is listed every round, even with nothing against them.
    [Fact]
    public void Formatter_ListsEveryCombatant_EvenAtZero()
    {
        using Harness h = new();
        h.Feed("You skewer red slime for 2 damage!");
        RoundSummary r = h.CloseRound();

        (string dealt, string taken) = RoundTotalsFormatter.Format(r);
        Assert.Equal("[Round 1 dealt: You 2, large giant rat 0, goblin 0, Bob 0]", dealt);
        Assert.Equal("[Round 1 taken: You 0, large giant rat 0, goblin 0, Bob 0, unknown 2]", taken);
    }

    // The terminal's table: one row per combatant in the room, biggest dealer first,
    // unknown last, every row its own "[ … ]" notice line.
    [Fact]
    public void Table_OneRowPerCombatant()
    {
        using Harness h = new();
        h.State.InCombat = true;
        h.Feed("You slash large giant rat for 45 damage!");
        h.Feed("Bob slashes large giant rat for 30 damage!");
        h.Feed("The large giant rat bites you for 12 damage!");
        h.Feed("An earthquake rocks the room for 8 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(new[]
        {
            "[Round 1 -----------------------]",
            "[ Combatant        Dealt  Taken ]",
            "[ You                 45     12 ]",
            "[ Bob                 30      0 ]",
            "[ large giant rat     12     75 ]",
            "[ goblin               0      0 ]",
            "[ unknown              8      8 ]",
        }, RoundTotalsFormatter.Table(r, Everyone));
        Assert.All(RoundTotalsFormatter.Table(r, Everyone), line => Assert.True(ClientNotice.IsNotice(line)));
    }

    // Us first, then the party, then monsters — whoever dealt more within each.
    [Fact]
    public void Table_OrdersSelfThenPartyThenMonsters()
    {
        using Harness h = new();
        h.State.InCombat = true;
        h.Feed("The large giant rat bites you for 50 damage!");
        h.Feed("The goblin bites Bob for 20 damage!");
        h.Feed("Bob slashes goblin for 5 damage!");
        RoundSummary r = h.CloseRound();

        Assert.Equal(new[] { "You", "Bob", "large giant rat", "goblin" },
            RoundTotalsFormatter.Table(r, Everyone).Skip(2).Select(line => line[2..].Split("  ")[0].Trim()));
    }

    private static readonly CombatantKind[] Everyone =
        { CombatantKind.Self, CombatantKind.Party, CombatantKind.Player, CombatantKind.Monster };

    private static RoundSummary MixedRound()
    {
        using Harness h = new();
        h.State.InCombat = true;
        h.Feed("You slash large giant rat for 45 damage!");
        h.Feed("Bob slashes large giant rat for 30 damage!");
        h.Feed("The large giant rat bites you for 12 damage!");
        h.Feed("An earthquake rocks the room for 8 damage!");
        return h.CloseRound();
    }

    private static string[] RowNames(IReadOnlyList<string> table)
        => table.Skip(2).Select(line => line[2..].Split("  ")[0].Trim()).ToArray();

    // Settings → Combat's Me / Party / Other players / Monsters boxes pick the rows;
    // the unknown row prints whenever the table does.
    [Fact]
    public void Table_JustMe_KeepsOurRowAndUnknown()
    {
        IReadOnlyList<string> table = RoundTotalsFormatter.Table(MixedRound(), new[] { CombatantKind.Self });
        Assert.StartsWith("[Round 1 ", table[0]);
        Assert.Contains("Combatant", table[1]);
        Assert.Matches(@"^\[ You\s+45\s+12 \]$", table[2]);
        Assert.Equal(new[] { "You", "unknown" }, RowNames(table));
        Assert.All(table, line => Assert.True(ClientNotice.IsNotice(line)));
    }

    [Fact]
    public void Table_PartyAndMonsters_LeaveUsOut()
        => Assert.Equal(new[] { "Bob", "large giant rat", "goblin", "unknown" },
            RowNames(RoundTotalsFormatter.Table(MixedRound(), new[] { CombatantKind.Party, CombatantKind.Monster })));

    [Fact]
    public void Table_NothingTicked_PrintsNothing()
        => Assert.Empty(RoundTotalsFormatter.Table(MixedRound(), Array.Empty<CombatantKind>()));

    [Fact]
    public void Formatter_NoCombatants_SaysNone()
    {
        RoundSummary r = new(1, 2, default, default,
            Array.Empty<CombatantDamage>(), 0, 0, 0, 0, 0, 0);
        (string dealt, string taken) = RoundTotalsFormatter.Format(r);
        Assert.Equal("[Round 2 dealt: none]", dealt);
        Assert.Equal("[Round 2 taken: none]", taken);
    }

    // ----- when a round closes ---------------------------------------

    // A round ends once its lines go quiet, so its totals follow its own lines instead
    // of waiting for the next round to start.
    [Fact]
    public void QuietAfterTheBurst_ClosesTheRound()
    {
        using Harness h = new(withScheduler: true);
        h.Feed("You skewer goblin for 2 damage!");
        h.Now = h.Now.AddMilliseconds(100);
        h.Feed("You impale goblin for 12 damage!");
        Assert.NotNull(h.Settle);

        // The timer armed on the first line fires 250 ms after it: only 150 ms of quiet,
        // so it waits out the rest.
        h.Now = h.Now.AddMilliseconds(150);
        h.Settle!();
        Assert.Empty(h.Completed);

        h.Now = h.Now.AddMilliseconds(100);
        h.Settle!();
        RoundSummary r = Assert.Single(h.Completed);
        Assert.Equal(14, r.DamageDealt);
    }

    // On a kill, *Combat Off* lands mid-burst and the other monsters' swings of the
    // same round follow it; with the settle timer they stay in that round.
    [Fact]
    public void CombatOff_WithSettleTimer_LeavesTheRoundOpenForTheRestOfTheBurst()
    {
        using Harness h = new(withScheduler: true);
        h.Feed("You skewer goblin for 8 damage!");
        h.Feed("*Combat Off*");
        h.Feed("The large giant rat bites you for 3 damage!");
        Assert.Empty(h.Completed);

        h.Now = h.Now.AddMilliseconds(600);
        h.Settle!();
        RoundSummary r = Assert.Single(h.Completed);
        Assert.Equal(8, r.DamageDealt);
        Assert.Equal(3, r.DamageTaken);
    }

    // The room clearing ends the round there and then: the walker moves on in the same
    // instant, so waiting for quiet printed the totals after our next move
    // (report paradigm-20260928-232024).
    [Fact]
    public void RoomClearing_ClosesTheRoundAtOnce()
    {
        using Harness h = new(withScheduler: true);
        h.State.InCombat = true;
        h.Feed("You surprise skewer goblin for 61 damage!");
        Assert.Empty(h.Completed);

        h.State.InCombat = false;
        RoundSummary r = Assert.Single(h.Completed);
        Assert.Equal(61, r.DamageDealt);
        Assert.Null(h.Settle);
    }

    // The next round's first line fires the combat tick after that line opened its
    // round; the tick must not close it with just that one line.
    [Fact]
    public void CombatTick_DoesNotCloseARoundThatJustOpened()
    {
        using Harness h = new();
        h.Feed("The goblin flails at you!");
        h.Feed("You skewer goblin for 2 damage!");
        h.Tracker.OnCombatTick();
        Assert.Empty(h.Completed);

        h.Feed("You impale goblin for 12 damage!");
        RoundSummary r = h.CloseRound();
        Assert.Equal(14, r.DamageDealt);
    }
}
