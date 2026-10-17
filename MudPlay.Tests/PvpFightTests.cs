using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Game.Pvp;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// PvpFight: attacking a player, the PvP spells, the evil-warning opt-in, what ends
// a fight, and the chase.
public sealed class PvpFightTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public RoomEntityClassifier Classifier { get; }
        public RoomDepartureWatcher Departure { get; }
        public PvpFight Fight { get; }

        public bool PvpEnabled { get; set; } = true;
        public PvpSettings Settings { get; set; } = new();
        public HashSet<string> Party { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset Clock { get; set; } = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        public bool CastWorks { get; set; } = true;
        public bool StepWorks { get; set; } = true;

        public List<string> Sent { get; } = new();
        public List<(string Code, string Target)> Casts { get; } = new();
        public List<Direction> Steps { get; } = new();
        public List<PvpChaseExit> Exits { get; } = new();
        public bool BackOnTask { get; set; } = true;
        public bool LeadingParty { get; set; }
        public Dictionary<string, PvpSpellInfo> Spells { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Mana { get; set; } = 100;

        public void Open(params Direction[] ways) => Exits.AddRange(ways.Select(w => new PvpChaseExit(w, Door: false)));
        public void Door(Direction way) => Exits.Add(new PvpChaseExit(way, Door: true));
        public List<string> Reports { get; } = new();
        public int Suspended { get; private set; }
        public int Resumed { get; private set; }
        public List<(TimeSpan Delay, Action Action)> Scheduled { get; } = new();

        // What is scheduled apart from the once-a-round tick.
        public List<(TimeSpan Delay, Action Action)> Waits =>
            Scheduled.Where(s => s.Delay != TimeSpan.FromSeconds(5)).ToList();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, new MonsterMessageStore(), Players, new LogService());
            Departure = new RoomDepartureWatcher(Router, Classifier);
            Fight = new PvpFight(
                Router, Classifier,
                pvpEnabled: () => PvpEnabled,
                inParty: Party.Contains,
                readSettings: () => Settings,
                attackCommandFor: given => $"a {given}",
                send: Sent.Add,
                cast: (code, target) => { if (CastWorks) Casts.Add((code, target)); return CastWorks; },
                spellInfo: code => Spells.TryGetValue(code, out PvpSpellInfo info) ? info : null,
                manaMeets: floor => Mana >= floor,
                stepToward: way => { if (StepWorks) Steps.Add(way); return StepWorks; },
                exitsHere: () => Exits,
                suspendEngines: _ => { Suspended++; return () => Resumed++; },
                backOnTask: () => BackOnTask,
                leadingParty: () => LeadingParty,
                schedule: (delay, action) => Scheduled.Add((delay, action)),
                now: () => Clock);
            Fight.Reported += Reports.Add;
            Departure.PlayerDeparted += Fight.NotePlayerDeparted;

            Players.RecordObservation("Bob", "Mage", null, null, null, null, null, DateTime.UtcNow);
            Players.RecordObservation("Ann", "Warrior", null, null, null, null, null, DateTime.UtcNow);
        }

        // Run what was scheduled so far, oldest first, advancing the clock by each delay.
        public void RunScheduled()
        {
            List<(TimeSpan Delay, Action Action)> due = new(Scheduled);
            Scheduled.Clear();
            foreach ((TimeSpan delay, Action action) in due)
            {
                Clock += delay;
                action();
            }
        }

        public void Feed(string line) => Router.Dispatch(new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        public void Dispose()
        {
            Fight.Dispose();
            Departure.Dispose();
            Classifier.Dispose();
        }
    }

    // ----- starting ------------------------------------------------------

    [Fact]
    public void Engage_StopsTheRunningWalk_AndAttacks()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");

        Assert.True(h.Fight.Engage("Bob", chase: false, "Bob is here"));

        Assert.Equal("a Bob", Assert.Single(h.Sent));
        Assert.Equal(1, h.Suspended);
        Assert.True(h.Fight.IsActive);
        Assert.Contains("attacking Bob", Assert.Single(h.Reports));
    }

    // The combat engine re-decides on these: stand down for the fight, pick the
    // room back up after it.
    [Fact]
    public void ActiveChanged_IsRaisedWhenAFightStarts_AndWhenItEnds()
    {
        using Harness h = new();
        int changes = 0;
        h.Fight.ActiveChanged += () => changes++;
        h.Feed("Also here: Bob.");

        h.Fight.Engage("Bob", false, "x");
        Assert.Equal(1, changes);
        h.Fight.Engage("Bob", false, "x");      // the same fight
        Assert.Equal(1, changes);

        h.Fight.Stop("done");
        Assert.Equal(2, changes);
        h.Fight.Stop("done");
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Engage_Refused_WhenPvpIsOff_OrTheyAreInOurParty_OrAnotherFightIsOn()
    {
        using Harness off = new() { PvpEnabled = false };
        Assert.False(off.Fight.Engage("Bob", false, "x"));

        using Harness h = new();
        h.Party.Add("Bob");
        h.Feed("Also here: Bob, Ann.");
        Assert.False(h.Fight.Engage("Bob", false, "x"));

        Assert.True(h.Fight.Engage("Ann", false, "x"));
        h.Party.Clear();
        Assert.False(h.Fight.Engage("Bob", false, "x"));
        Assert.Equal("a Ann", Assert.Single(h.Sent));
    }

    [Fact]
    public void EngagingTheSamePlayerAgain_SendsNothingMore_ButCanTurnOnTheChase()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: false, "x");

        Assert.True(h.Fight.Engage("Bob", chase: true, "x"));
        Assert.Single(h.Sent);

        h.Feed("Bob just left to the north.");
        Assert.Equal(Direction.N, Assert.Single(h.Steps));
    }

    [Fact]
    public void AtKill_OnlyTakesAPlayerWhoIsHere()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");

        Assert.False(h.Fight.EngageOnOrder("giant rat"));
        Assert.False(h.Fight.EngageOnOrder("Ann"));
        Assert.True(h.Fight.EngageOnOrder("Bob"));
        Assert.Equal("a Bob", Assert.Single(h.Sent));
    }

    // A whole @kill: a player we may fight is fought, and any name that is no
    // player in the room is handed to the combat engine as a monster's.
    [Fact]
    public void AtKill_Taken_FightsThePlayer_AndAMonsterGoesToTheCombatEngine()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        List<string> monsters = new();

        Assert.Null(h.Fight.TakeKillOrder("Bob", monsters.Add));
        Assert.Equal("a Bob", Assert.Single(h.Sent));
        Assert.Empty(monsters);

        Assert.Null(h.Fight.TakeKillOrder("giant rat", monsters.Add));
        Assert.Equal(new[] { "giant rat" }, monsters);
    }

    // A monster whose name starts with the given name of a player standing here is
    // still a monster: the order goes to the combat engine and is not refused for
    // the player's sake.
    [Fact]
    public void AtKill_NamingAMonsterThatSharesAPlayersFirstWord_WithPvpOff_GoesToTheCombatEngine()
    {
        using Harness h = new() { PvpEnabled = false };
        h.Feed("Also here: Bob.");
        List<string> monsters = new();

        Assert.Null(h.Fight.TakeKillOrder("bob cat", monsters.Add));

        Assert.Equal(new[] { "bob cat" }, monsters);
        Assert.Empty(h.Sent);
    }

    // A player in the room we won't fight is NOT handed to the combat engine,
    // which would attack by that name all the same. The sender is told why.
    [Fact]
    public void AtKill_NamingAPlayer_WithPvpOff_IsRefused_NotRetargeted()
    {
        using Harness h = new() { PvpEnabled = false };
        h.Feed("Also here: Bob.");
        List<string> monsters = new();

        Assert.Equal("not attacking Bob: PvP is off", h.Fight.TakeKillOrder("Bob", monsters.Add));

        Assert.Empty(monsters);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void AtKill_NamingAPartyMember_IsRefused_NotRetargeted()
    {
        using Harness h = new();
        h.Party.Add("Bob");
        h.Feed("Also here: Bob.");
        List<string> monsters = new();

        Assert.Equal("not attacking Bob: they are in my party", h.Fight.TakeKillOrder("Bob", monsters.Add));

        Assert.Empty(monsters);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void AtKill_NamingAPlayer_WithAnotherFightUnderWay_IsRefused_NotRetargeted()
    {
        using Harness h = new();
        h.Feed("Also here: Bob, Ann.");
        h.Fight.Engage("Ann", chase: false, "x");
        int sentBefore = h.Sent.Count;
        List<string> monsters = new();

        Assert.Equal("not attacking Bob: I'm fighting Ann", h.Fight.TakeKillOrder("Bob", monsters.Add));

        Assert.Empty(monsters);
        Assert.Equal(sentBefore, h.Sent.Count);
    }

    // ----- the PvP spells --------------------------------------------------

    private static readonly PvpSpellInfo Slow = new(BetweenRound: true, Duration: TimeSpan.FromSeconds(15), MonsterOnly: false);
    private static readonly PvpSpellInfo Dispel = new(BetweenRound: true, Duration: TimeSpan.Zero, MonsterOnly: false);
    private static readonly PvpSpellInfo Bolt = new(BetweenRound: false, Duration: TimeSpan.Zero, MonsterOnly: false);
    private static readonly PvpSpellInfo Charm = new(BetweenRound: false, Duration: TimeSpan.Zero, MonsterOnly: true);

    private static PvpSpellSlot Slot(string code, int? maxCasts = null, int minMana = 0) =>
        new() { SpellName = code, MaxCasts = maxCasts, MinManaPerCast = minMana };

    // A between-round spell goes out with the attack at the start and again each
    // time its duration has run out; the attack follows every cast, which broke it.
    [Fact]
    public void BetweenRoundSpell_IsCastAtTheStart_AndAgainWhenItsDurationRunsOut()
    {
        using Harness h = new() { Settings = new PvpSettings { Spell1 = Slot("slow") } };
        h.Spells["slow"] = Slow;
        h.Feed("Also here: Bob.");

        h.Fight.Engage("Bob", false, "x");
        Assert.Equal(("slow", "Bob"), Assert.Single(h.Casts));
        Assert.Equal(new[] { "a Bob" }, h.Sent);

        h.RunScheduled();                       // 5 s
        h.RunScheduled();                       // 10 s
        Assert.Single(h.Casts);
        Assert.Single(h.Sent);

        h.RunScheduled();                       // 15 s: the 15 s spell has run out
        Assert.Equal(2, h.Casts.Count);
        Assert.Equal(new[] { "a Bob", "a Bob" }, h.Sent);
    }

    [Fact]
    public void BetweenRoundSpellWithNoDuration_OrOneWeDontKnow_IsCastOnce()
    {
        using Harness h = new() { Settings = new PvpSettings { Spell1 = Slot("disp"), Spell2 = Slot("zzzz") } };
        h.Spells["disp"] = Dispel;
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");

        for (int i = 0; i < 6; i++) h.RunScheduled();

        Assert.Equal(new[] { ("disp", "Bob"), ("zzzz", "Bob") }, h.Casts);
    }

    [Fact]
    public void BetweenRoundSpell_ThatCouldNotBeCast_IsTriedAgainNextRound()
    {
        using Harness h = new() { CastWorks = false, Settings = new PvpSettings { Spell1 = Slot("slow") } };
        h.Spells["slow"] = Slow;
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");
        Assert.Empty(h.Casts);

        h.CastWorks = true;
        h.RunScheduled();
        Assert.Equal(("slow", "Bob"), Assert.Single(h.Casts));
    }

    // A combat spell is the attack itself, sent once and repeated by the game.
    [Fact]
    public void CombatSpell_IsTheAttack_InPlaceOfTheProfiles()
    {
        using Harness h = new() { Settings = new PvpSettings { Spell1 = Slot("bolt") } };
        h.Spells["bolt"] = Bolt;
        h.Feed("Also here: Bob.");

        h.Fight.Engage("Bob", false, "x");
        h.RunScheduled();
        h.RunScheduled();

        Assert.Equal(new[] { "bolt Bob" }, h.Sent);
        Assert.Empty(h.Casts);
    }

    [Fact]
    public void CombatSpell_FallsBackToTheProfileAttack_WhenManaDropsUnderItsFloor_AndComesBack()
    {
        using Harness h = new() { Settings = new PvpSettings { Spell1 = Slot("bolt", minMana: 40) } };
        h.Spells["bolt"] = Bolt;
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");

        h.Mana = 30;
        h.RunScheduled();
        Assert.Equal("a Bob", h.Sent[^1]);

        h.Mana = 80;
        h.RunScheduled();
        Assert.Equal(new[] { "bolt Bob", "a Bob", "bolt Bob" }, h.Sent);
    }

    [Fact]
    public void CombatSpell_StopsAfterItsMaxCasts_CountedInRounds()
    {
        using Harness h = new() { Settings = new PvpSettings { Spell1 = Slot("bolt", maxCasts: 2) } };
        h.Spells["bolt"] = Bolt;
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");

        h.RunScheduled();                       // one round on it
        Assert.Equal(new[] { "bolt Bob" }, h.Sent);
        h.RunScheduled();                       // two: spent
        Assert.Equal(new[] { "bolt Bob", "a Bob" }, h.Sent);
    }

    [Fact]
    public void SecondCombatSpell_TakesOverWhenTheFirstCantBeUsed()
    {
        using Harness h = new() { Mana = 10, Settings = new PvpSettings { Spell1 = Slot("bolt", minMana: 50), Spell2 = Slot("zap") } };
        h.Spells["bolt"] = Bolt;
        h.Spells["zap"] = Bolt;
        h.Feed("Also here: Bob.");

        h.Fight.Engage("Bob", false, "x");

        Assert.Equal(new[] { "zap Bob" }, h.Sent);
    }

    [Fact]
    public void MonsterOnlySpell_IsNotUsed()
    {
        using Harness h = new() { Settings = new PvpSettings { Spell1 = Slot("chrm") } };
        h.Spells["chrm"] = Charm;
        h.Feed("Also here: Bob.");

        h.Fight.Engage("Bob", false, "x");
        h.RunScheduled();

        Assert.Equal(new[] { "a Bob" }, h.Sent);
        Assert.Empty(h.Casts);
    }

    // A cast of ours breaks combat: the Off that follows is answered with the attack.
    [Fact]
    public void CombatOff_WithThemStillHere_IsAnsweredWithTheAttack()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");

        h.Clock += TimeSpan.FromSeconds(3);
        h.Feed("*Combat Off*");

        Assert.Equal(new[] { "a Bob", "a Bob" }, h.Sent);
    }

    // Re-issuing an attack prints Off then Engaged. That Off is not the fight ending.
    [Fact]
    public void OffAnsweredByEngaged_RightAfterOurAttack_IsNotAnsweredWithAnother()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");

        h.Feed("*Combat Off*");
        h.Feed("*Combat Engaged*");
        h.Scheduled.Single(x => x.Delay < TimeSpan.FromSeconds(5)).Action();

        Assert.Single(h.Sent);
    }

    [Fact]
    public void OffWithNoEngaged_RightAfterOurAttack_IsLookedAtAgain()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");

        h.Feed("*Combat Off*");
        h.Scheduled.Single(x => x.Delay < TimeSpan.FromSeconds(5)).Action();
        Assert.Equal(2, h.Sent.Count);

        h.Feed("You do not see Bob here!");
        Assert.False(h.Fight.IsActive);
    }

    // ----- evil warnings ----------------------------------------------------

    [Fact]
    public void WarningsRefusal_WithoutTheOptIn_EndsTheFight_AndTouchesNothing()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");

        h.Feed("To do this action, you must turn off your evil warnings.");

        Assert.False(h.Fight.IsActive);
        Assert.Equal(new[] { "a Bob" }, h.Sent);
        Assert.Contains("evil warnings are on", h.Reports[^1]);
        Assert.Equal(1, h.Resumed);
    }

    // The warnings stay off past the end of the fight: they go back on once what
    // the fight interrupted is running again and a minute has gone by quietly.
    [Fact]
    public void WarningsRefusal_WithTheOptIn_SwitchesThemOff_AndPutsThemBackOnceBackOnTask()
    {
        using Harness h = new() { BackOnTask = false, Settings = new PvpSettings { TurnOffEvilWarningsToAttack = true } };
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");

        h.Feed("To do this action, you must turn off your evil warnings.");
        Assert.Equal(new[] { "a Bob", "set warning off", "a Bob" }, h.Sent);
        Assert.True(h.Fight.IsActive);

        h.Fight.Stop("done");
        Assert.DoesNotContain("set warning on", h.Sent);

        for (int i = 0; i < 8; i++) h.RunScheduled();       // two minutes, still not back at it
        Assert.DoesNotContain("set warning on", h.Sent);

        h.BackOnTask = true;
        h.RunScheduled();
        Assert.Equal("set warning on", h.Sent[^1]);
        Assert.Contains("back on task", h.Reports[^1]);

        h.RunScheduled();
        Assert.Single(h.Sent, s => s == "set warning on");
    }

    [Fact]
    public void WarningsStayOff_ForASecondFightBeforeTheyWentBackOn_WithoutBeingSentAgain()
    {
        using Harness h = new() { Settings = new PvpSettings { TurnOffEvilWarningsToAttack = true } };
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");
        h.Feed("To do this action, you must turn off your evil warnings.");
        h.Fight.Stop("done");

        h.RunScheduled();                                   // 15 s on: too soon
        Assert.True(h.Fight.Engage("Bob", false, "x"));
        for (int i = 0; i < 8; i++) h.RunScheduled();       // back on task, but a fight is on
        Assert.Single(h.Sent, s => s == "set warning off");
        Assert.DoesNotContain("set warning on", h.Sent);

        h.Fight.Stop("done");
        for (int i = 0; i < 6; i++) h.RunScheduled();
        Assert.Single(h.Sent, s => s == "set warning on");
    }

    [Fact]
    public void FightEndingOnADisconnect_CantPutTheWarningsBack_AndSaysSo()
    {
        using Harness h = new() { Settings = new PvpSettings { TurnOffEvilWarningsToAttack = true } };
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");
        h.Feed("To do this action, you must turn off your evil warnings.");
        int sent = h.Sent.Count;

        h.Fight.Stop("disconnected", resume: false, connected: false);

        Assert.Equal(sent, h.Sent.Count);
        Assert.Contains("set warning on", h.Reports[^1]);
        Assert.Equal(0, h.Resumed);
    }

    [Theory]
    [InlineData("You have progressed too far to the evil side to do this action.")]
    [InlineData("You have chosen a way of life which does not allow this action.")]
    [InlineData("Such an attack would result in a very unbalanced combat round.")]
    public void OtherRefusals_EndTheFight(string line)
    {
        using Harness h = new() { Settings = new PvpSettings { TurnOffEvilWarningsToAttack = true } };
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");

        h.Feed(line);

        Assert.False(h.Fight.IsActive);
        Assert.DoesNotContain("set warning off", h.Sent);
    }

    // The same lines answer other things (a Good monster); long after our attack
    // they aren't about this fight.
    [Fact]
    public void RefusalLongAfterOurAttack_IsNotOurs()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");

        h.Clock += TimeSpan.FromSeconds(10);
        h.Feed("To do this action, you must turn off your evil warnings.");

        Assert.True(h.Fight.IsActive);
    }

    // ----- ending -------------------------------------------------------------

    [Fact]
    public void WithoutChase_TheFightEndsWhenTheyLeave_AndTheWalkIsPickedUp()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: false, "x");

        h.Feed("Bob just left to the north.");

        Assert.False(h.Fight.IsActive);
        Assert.Empty(h.Steps);
        Assert.Equal(1, h.Resumed);
    }

    [Fact]
    public void AnAttackerWeCantSee_IsStillAttacked_AndTheGamesAnswerEndsIt()
    {
        using Harness h = new();
        h.Feed("Also here: Ann.");

        Assert.True(h.Fight.Engage("Bob", chase: false, "Bob attacked us"));
        Assert.Equal("a Bob", Assert.Single(h.Sent));

        h.Feed("You do not see Bob here!");
        Assert.False(h.Fight.IsActive);
    }

    [Fact]
    public void Dying_EndsTheFight_AndResumesNothing()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");

        h.Fight.Stop("we died", resume: false);

        Assert.False(h.Fight.IsActive);
        Assert.Equal(0, h.Resumed);
    }

    // ----- the chase ------------------------------------------------------------

    [Fact]
    public void Chase_StepsTheWayTheyLeftAtOnce_AndAttacksWhenTheyAreSeenAgain()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");

        h.Feed("Bob just left upwards.");
        Assert.Equal(Direction.U, Assert.Single(h.Steps));
        Assert.True(h.Fight.IsActive);

        h.Feed("Also here: Bob.");              // the new room, before the step is confirmed
        Assert.Single(h.Sent);
        h.Fight.NoteStepLanded();
        Assert.Equal(new[] { "a Bob", "a Bob" }, h.Sent);
    }

    // Followed them and they aren't there: carry on the way they were heading.
    [Fact]
    public void Chase_OutOfSight_KeepsToTheirHeading()
    {
        using Harness h = new();
        h.Open(Direction.N, Direction.S, Direction.E);
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");
        h.Feed("Bob just left to the north.");

        h.Feed("Also here: Ann.");
        h.Fight.NoteStepLanded();

        Assert.Equal(new[] { Direction.N, Direction.N }, h.Steps);
    }

    // The heading runs out: with one other way on, that is the guess; never back.
    [Fact]
    public void Chase_HeadingBlocked_TakesTheOnlyOtherWayOut()
    {
        using Harness h = new();
        h.Open(Direction.S, Direction.E);
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");
        h.Feed("Bob just left to the north.");

        h.Fight.NoteStepLanded();

        Assert.Equal(new[] { Direction.N, Direction.E }, h.Steps);
    }

    [Fact]
    public void Chase_NoGuessToMake_WaitsTheSetTime_ThenGivesUp()
    {
        using Harness h = new() { Settings = new PvpSettings { ChaseWaitSeconds = 25 } };
        h.Open(Direction.S, Direction.E, Direction.W);
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");
        h.Feed("Bob just left to the north.");

        h.Fight.NoteStepLanded();
        Assert.Single(h.Steps);
        Assert.True(h.Fight.IsActive);
        Assert.Equal(TimeSpan.FromSeconds(25), Assert.Single(h.Waits).Delay);

        h.RunScheduled();
        Assert.False(h.Fight.IsActive);
        Assert.Contains("lost Bob", h.Reports[^1]);
        Assert.Equal(1, h.Resumed);
    }

    [Fact]
    public void Chase_SeenAgainWhileWaiting_CarriesOn()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");
        h.Feed("Bob just left to the north.");
        h.Fight.NoteStepLanded();               // no exits known: waiting

        h.Feed("Also here: Bob.");
        h.RunScheduled();

        Assert.True(h.Fight.IsActive);
        Assert.Equal("a Bob", h.Sent[^1]);
    }

    [Fact]
    public void Chase_GuessingSwitchedOff_WaitsInsteadOfGuessing()
    {
        using Harness h = new() { Settings = new PvpSettings { ChaseGuessDirection = false } };
        h.Open(Direction.N, Direction.S);
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");
        h.Feed("Bob just left to the north.");

        h.Fight.NoteStepLanded();

        Assert.Single(h.Steps);
        Assert.Single(h.Waits);
    }

    [Fact]
    public void Chase_GivesUpAfterTheSetNumberOfRoomsWithoutSight()
    {
        using Harness h = new() { Settings = new PvpSettings { ChaseRoomsUnseen = 3 } };
        h.Open(Direction.N, Direction.S);
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");
        h.Feed("Bob just left to the north.");

        h.Fight.NoteStepLanded();
        h.Fight.NoteStepLanded();
        Assert.Equal(3, h.Steps.Count);
        Assert.True(h.Fight.IsActive);

        h.Fight.NoteStepLanded();
        Assert.Equal(3, h.Steps.Count);
        Assert.False(h.Fight.IsActive);
        Assert.Contains("after 3 room(s)", h.Reports[^1]);
        Assert.Equal(1, h.Resumed);
    }

    [Fact]
    public void Chase_SeeingThemResetsTheRoomCount()
    {
        using Harness h = new() { Settings = new PvpSettings { ChaseRoomsUnseen = 2 } };
        h.Open(Direction.N, Direction.S);
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");

        for (int i = 0; i < 4; i++)
        {
            h.Feed("Bob just left to the north.");
            h.Feed("Also here: Bob.");
            h.Fight.NoteStepLanded();
        }

        Assert.Equal(4, h.Steps.Count);
        Assert.True(h.Fight.IsActive);
    }

    // They vanished with no departure line: the chase still looks for a way.
    [Fact]
    public void Chase_MissedTheirLeaving_GuessesFromTheRoom()
    {
        using Harness h = new();
        h.Open(Direction.E);
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");

        h.Feed("Also here: Ann.");

        Assert.Equal(Direction.E, Assert.Single(h.Steps));
    }

    [Fact]
    public void Chase_StepThatCantBeMade_Waits()
    {
        using Harness h = new() { StepWorks = false };
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");

        h.Feed("Bob just left to the north.");

        Assert.Empty(h.Steps);
        Assert.Single(h.Waits);
        Assert.True(h.Fight.IsActive);
    }

    [Fact]
    public void Chase_WithTracking_TracksOnArrival_AndFollowsTheAnswerOverAGuess()
    {
        using Harness h = new() { Settings = new PvpSettings { TrackEnemies = true } };
        h.Open(Direction.N, Direction.S, Direction.E);
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");
        h.Feed("Bob just left to the north.");

        h.Fight.NoteStepLanded();
        Assert.Equal("track Bob", h.Sent[^1]);
        Assert.Single(h.Steps);                 // no guess while the answer is awaited

        h.Feed("Ann went west from here.");     // someone else's trail
        Assert.Single(h.Steps);

        h.Feed("Bob went east from here.");
        Assert.Equal(Direction.E, h.Steps[^1]);

        h.RunScheduled();                       // the answer already came: no guess on top
        Assert.Equal(2, h.Steps.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Chase_TrackThatFailsOrGoesUnanswered_FallsBackToTheGuess(bool failureLine)
    {
        using Harness h = new() { Settings = new PvpSettings { TrackEnemies = true } };
        h.Open(Direction.N, Direction.S);
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");
        h.Feed("Bob just left to the north.");
        h.Fight.NoteStepLanded();

        if (failureLine) h.Feed("Your tracking skills fail you this time.");
        else h.RunScheduled();

        Assert.Equal(new[] { Direction.N, Direction.N }, h.Steps);
    }

    [Fact]
    public void Chase_WaitingWithTrackingOn_TracksAgainOnTheSetInterval()
    {
        using Harness h = new()
        {
            Settings = new PvpSettings { TrackEnemies = true, TrackEnemiesEverySeconds = 10, ChaseWaitSeconds = 60, ChaseGuessDirection = false },
        };
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");
        h.Feed("Bob just left to the north.");
        h.Fight.NoteStepLanded();               // tracks
        h.Feed("Your tracking skills fail you this time.");   // no guessing: waits

        int tracksBefore = h.Sent.Count(s => s == "track Bob");
        (TimeSpan delay, Action retrack) = h.Scheduled.Single(s => s.Delay == TimeSpan.FromSeconds(10));
        retrack();
        Assert.Equal(tracksBefore + 1, h.Sent.Count(s => s == "track Bob"));

        h.Feed("Bob went south from here.");
        Assert.Equal(Direction.S, h.Steps[^1]);
    }

    // ----- a door at a crossroads -------------------------------------------------

    // Behind the door first, a few rooms in; nothing there, so back to the junction
    // and on the way they were heading. The walk back isn't counted against the
    // rooms-without-sight limit.
    [Fact]
    public void Chase_CrossroadsWithADoor_LooksBehindItFirst_ThenComesBackForTheOtherWay()
    {
        using Harness h = new() { Settings = new PvpSettings { ChaseDoorRooms = 2, ChaseRoomsUnseen = 4 } };
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");
        h.Feed("Bob just left to the north.");               // step 1: north, into the crossroads

        h.Open(Direction.N, Direction.S);
        h.Door(Direction.E);
        h.Fight.NoteStepLanded();                             // the door first
        Assert.Equal(Direction.E, h.Steps[^1]);

        h.Exits.Clear();
        h.Open(Direction.E, Direction.W);
        h.Fight.NoteStepLanded();                             // one room in: carry on east
        Assert.Equal(new[] { Direction.N, Direction.E, Direction.E }, h.Steps);

        h.Fight.NoteStepLanded();                             // two rooms in, nothing: turn back
        h.Fight.NoteStepLanded();
        Assert.Equal(new[] { Direction.N, Direction.E, Direction.E, Direction.W, Direction.W }, h.Steps);

        h.Exits.Clear();
        h.Open(Direction.N, Direction.S);
        h.Door(Direction.E);
        h.Fight.NoteStepLanded();                             // back at the crossroads: north now
        Assert.Equal(Direction.N, h.Steps[^1]);
        Assert.True(h.Fight.IsActive);
    }

    [Fact]
    public void Chase_SeenBehindTheDoor_AttacksThere()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");
        h.Feed("Bob just left to the north.");
        h.Open(Direction.N, Direction.S);
        h.Door(Direction.E);
        h.Fight.NoteStepLanded();

        h.Feed("Also here: Bob.");
        h.Fight.NoteStepLanded();

        Assert.Equal("a Bob", h.Sent[^1]);
        Assert.Equal(2, h.Steps.Count);
    }

    // ----- @kill ------------------------------------------------------------------

    [Fact]
    public void AtKill_WithItsSettingOn_SwitchesWarningsOffBeforeAttacking()
    {
        using Harness h = new() { Settings = new PvpSettings { KillOrderTurnsOffEvilWarnings = true } };
        h.Feed("Also here: Bob.");

        Assert.True(h.Fight.EngageOnOrder("Bob"));

        Assert.Equal(new[] { "set warning off", "a Bob" }, h.Sent);
    }

    [Fact]
    public void AtKill_WithItsSettingOff_JustAttacks()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");

        h.Fight.EngageOnOrder("Bob");

        Assert.Equal(new[] { "a Bob" }, h.Sent);
    }

    // Leading a party, our own fight is passed on to it; an order we were given isn't.
    [Fact]
    public void LeadingAParty_StartingAFight_SendsKillToTheParty()
    {
        using Harness h = new() { LeadingParty = true };
        h.Feed("Also here: Bob.");

        h.Fight.Engage("Bob", false, "Bob is here");

        Assert.Equal(new[] { "a Bob", ".@kill Bob" }, h.Sent);
    }

    [Fact]
    public void KillOrder_IsNotSent_WhenNotLeading_WhenSwitchedOff_OrForAFightWeWereOrderedInto()
    {
        using Harness following = new();
        following.Feed("Also here: Bob.");
        following.Fight.Engage("Bob", false, "x");
        Assert.Equal(new[] { "a Bob" }, following.Sent);

        using Harness off = new() { LeadingParty = true, Settings = new PvpSettings { LeaderSendsKillOrder = false } };
        off.Feed("Also here: Bob.");
        off.Fight.Engage("Bob", false, "x");
        Assert.Equal(new[] { "a Bob" }, off.Sent);

        using Harness ordered = new() { LeadingParty = true };
        ordered.Feed("Also here: Bob.");
        ordered.Fight.EngageOnOrder("Bob");
        Assert.Equal(new[] { "a Bob" }, ordered.Sent);
    }

    [Fact]
    public void Started_NamesThePlayer_OncePerFight()
    {
        using Harness h = new();
        List<string> started = new();
        h.Fight.Started += started.Add;
        h.Feed("Also here: Bob.");

        h.Fight.Engage("Bob", false, "x");
        h.Fight.Engage("Bob", true, "x");

        Assert.Equal("Bob", Assert.Single(started));
    }

    [Fact]
    public void WarningsBack_WaitsTheSetTime()
    {
        using Harness h = new()
        {
            Settings = new PvpSettings { TurnOffEvilWarningsToAttack = true, WarningsBackAfterSeconds = 200 },
        };
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");
        h.Feed("To do this action, you must turn off your evil warnings.");
        h.Fight.Stop("done");

        for (int i = 0; i < 12; i++) h.RunScheduled();        // three minutes
        Assert.DoesNotContain("set warning on", h.Sent);

        h.RunScheduled();
        h.RunScheduled();
        Assert.Equal("set warning on", h.Sent[^1]);
    }
}
