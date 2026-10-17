using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Health;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// MonsterRelationshipWatcher: a monster whose relationship is Hangup, on the room
// roster, is answered with the health settings' hang-up, once per sighting, and
// the watch is off for a minute after a reconnect that follows such a hang-up. One
// whose relationship is Flee is answered with the health settings' flee, once per
// sighting, and a Hangup monster on the same roster outranks it.
public sealed class MonsterRelationshipWatcherTests
{
    private const int Ogre = 7;
    private const int Rat = 8;
    private const int Troll = 9;

    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public LogService Log { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public RoomEntryWatcher Arrivals { get; }
        public MonsterRelationshipWatcher Watcher { get; }

        public Dictionary<int, MonsterRelationship> Relationships { get; } = new();
        public bool OverlayUnreadable { get; set; }
        public bool AtBoardMenu { get; set; }
        public bool HangupsDisabled { get; set; }
        public bool PvpFightActive { get; set; }

        // What HealthManager.HangUpForMonster answers. Jumped by default: an
        // escape that went out and left the session up, so a test of sightings is
        // not also a test of the drop that follows a hang-up.
        public EscapeOutcome Outcome { get; set; } = EscapeOutcome.Jumped;
        public List<string> HangUps { get; } = new();

        // What HealthManager.FleeFromMonster answers, and the question each run was
        // handed to ask when it has to wait for a move to land.
        public FleeOutcome FleeOutcome { get; set; } = FleeOutcome.Started;
        public List<string> Flees { get; } = new();
        public Func<bool>? StillHere { get; private set; }

        // Auto-All has switched every auto off.
        public bool MasterSwitchOff { get; set; }

        // HealthManager.IsFleeInFlight: a run is moving or held for a move to land.
        // True by default, as it is right after a run starts.
        public bool FleeInFlight { get; set; } = true;

        public DateTimeOffset Clock { get; set; } = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        public List<Action> Scheduled { get; } = new();
        public List<string> Notices { get; } = new();
        public int HoldChanges { get; private set; }
        public List<LogEntry> Logged { get; } = new();

        public Harness(
            Func<string, EscapeOutcome>? hangUp = null,
            Func<string, Func<bool>, FleeOutcome>? flee = null,
            Func<bool>? fleeInFlight = null)
        {
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, new PlayerDatabase(new ProfileService(), () => null), Log);
            Watcher = new MonsterRelationshipWatcher(
                Classifier,
                resolveOverlay: ResolveOverlay,
                hangUp: hangUp ?? (reason => { HangUps.Add(reason); return Outcome; }),
                flee: flee ?? ((reason, stillHere) =>
                {
                    Flees.Add(reason);
                    StillHere = stillHere;
                    return FleeOutcome;
                }),
                fleeInFlight: fleeInFlight ?? (() => FleeInFlight),
                masterSwitchOff: () => MasterSwitchOff,
                hangupsDisabled: () => HangupsDisabled,
                pvpHandles: _ => PvpFightActive,
                atBoardMenu: () => AtBoardMenu,
                describeRoom: () => "in Town Square (1/5)",
                schedule: (_, callback) => Scheduled.Add(callback),
                log: Log,
                now: () => Clock);
            Watcher.HoldNotice += Notices.Add;
            Watcher.HoldChanged += () => HoldChanges++;
            Arrivals = new RoomEntryWatcher(Router, Classifier, Log);
            Log.EntryAdded += Logged.Add;

            AddMonster(Ogre, "ogre");
            AddMonster(Rat, "giant rat");
            AddMonster(Troll, "troll");
        }

        private MonsterOverlay ResolveOverlay(int number)
        {
            if (OverlayUnreadable) throw new InvalidOperationException("no active set");
            return Relationships.TryGetValue(number, out MonsterRelationship rel)
                ? new MonsterOverlay { Relationship = rel }
                : new MonsterOverlay();
        }

        public void AddMonster(int number, string name) =>
            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}", Name: name, Links: new[] { new GameDataLink("Monsters", number) }));

        public void Feed(string line) => Router.Dispatch(new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        // One second passes and the countdown's timer fires.
        public void Tick(int seconds = 1)
        {
            for (int i = 0; i < seconds; i++)
            {
                Clock += TimeSpan.FromSeconds(1);
                List<Action> due = new(Scheduled);
                Scheduled.Clear();
                foreach (Action callback in due) callback();
            }
        }

        // The watcher hangs up for the ogre, and the line drops for it.
        public void HangUpAndDrop()
        {
            Outcome = EscapeOutcome.HungUp;
            Relationships[Ogre] = MonsterRelationship.Hangup;
            Feed("Also here: ogre.");
            Classifier.NoteGameLeft();
            Watcher.NoteDisconnected();
        }

        public IEnumerable<string> InfoLines =>
            Logged.Where(e => e.Source == MonsterRelationshipWatcher.HangupLogCategory && e.Severity == LogSeverity.Info)
                  .Select(e => e.Message);

        public IEnumerable<string> WarnLines =>
            Logged.Where(e => e.Source == MonsterRelationshipWatcher.HangupLogCategory && e.Severity == LogSeverity.Warn)
                  .Select(e => e.Message);

        public IEnumerable<string> FleeLines =>
            Logged.Where(e => e.Source == MonsterRelationshipWatcher.FleeLogCategory && e.Severity == LogSeverity.Info)
                  .Select(e => e.Message);

        public void Dispose()
        {
            Arrivals.Dispose();
            Watcher.Dispose();
            Classifier.Dispose();
        }
    }

    // ----- what is seen --------------------------------------------------

    [Fact]
    public void HangupMonsterOnTheRoomDisplay_HangsUp_AndSaysWhichAndWhere()
    {
        using Harness h = new() { Outcome = EscapeOutcome.HungUp };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: giant rat, ogre.");

        Assert.Equal("ogre (#7) is here, relationship Hangup", Assert.Single(h.HangUps));
        string line = Assert.Single(h.InfoLines);
        Assert.Contains("ogre (#7) seen in Town Square (1/5)", line);
        Assert.Contains("hung up", line);
        Assert.Contains("ogre (#7) seen in Town Square (1/5)", h.Watcher.LastHangupSighting);
    }

    [Fact]
    public void ItIsTheSight_NotAnAttack()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("The ogre swings at you!");
        Assert.Empty(h.HangUps);

        h.Feed("Also here: ogre.");
        Assert.Single(h.HangUps);
    }

    [Fact]
    public void AnArrivalLineThatNamesIt_HangsUp()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: giant rat.");
        Assert.Empty(h.HangUps);

        h.Feed("An ogre stomps into the room from southeast.");

        Assert.Single(h.HangUps);
    }

    [Fact]
    public void AFlavorPrefixedName_ReadsTheSameRecord()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: large ogre.");

        Assert.Equal("large ogre (#7) is here, relationship Hangup", Assert.Single(h.HangUps));
    }

    [Theory]
    [InlineData(MonsterRelationship.Enemy)]
    [InlineData(MonsterRelationship.Neutral)]
    [InlineData(MonsterRelationship.Friend)]
    [InlineData(MonsterRelationship.Flee)]
    public void EveryOtherRelationship_DrawsNoHangUp(MonsterRelationship relationship)
    {
        using Harness h = new();
        h.Relationships[Ogre] = relationship;

        h.Feed("Also here: ogre, giant rat.");

        Assert.Empty(h.HangUps);
        Assert.Empty(h.InfoLines);
        Assert.Equal("(none this session)", h.Watcher.LastHangupSighting);
    }

    // The relationship belongs to a monster record. A name on the roster that
    // resolves to none has no relationship to read.
    [Fact]
    public void ANameThatResolvesToNoRecord_DrawsNothing()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: gorgon.");

        Assert.Empty(h.HangUps);
    }

    [Fact]
    public void AnOverlayThatCannotBeRead_DrawsNothing_AndIsSaidOnce()
    {
        using Harness h = new() { OverlayUnreadable = true };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: ogre.");
        h.Feed("Also here: ogre, giant rat.");

        Assert.Empty(h.HangUps);
        Assert.Contains("could not be read", Assert.Single(h.WarnLines));
    }

    // ----- once per sighting --------------------------------------------

    [Fact]
    public void OneAnswerPerRoomDisplay_WhateverReissuesItsRoster()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: giant rat, ogre.");
        h.Classifier.ReemitCurrent();
        h.Feed("A giant rat scurries into the room from north.");
        h.Classifier.RemoveDeadEntity("giant rat");
        h.Classifier.ReemitCurrent();

        Assert.Single(h.HangUps);
        Assert.Single(h.InfoLines);
    }

    // The next room's roster replaces this one's with no empty roster between
    // (its "Also here:" line is read before the move is confirmed), and an escape
    // that left the session up has to be asked for again there.
    [Fact]
    public void TheNextRoomsDisplay_IsANewSighting_WithNoEmptyRosterBetween()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");

        h.Feed("Also here: ogre.");
        h.Classifier.ReemitCurrent();

        Assert.Equal(2, h.HangUps.Count);
    }

    [Fact]
    public void TwoHangupMonsters_OneDying_IsStillTheSameSighting()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Relationships[Troll] = MonsterRelationship.Hangup;

        h.Feed("Also here: ogre, troll.");
        h.Classifier.RemoveDeadEntity("ogre");
        h.Classifier.ReemitCurrent();

        Assert.Equal("ogre (#7) is here, relationship Hangup", Assert.Single(h.HangUps));
    }

    [Fact]
    public void AHangupMonsterThatWasNotThereWhenAnswered_IsAnswered()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Relationships[Troll] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");

        h.Feed("A troll lumbers into the room from north.");

        Assert.Equal(2, h.HangUps.Count);
        Assert.StartsWith("troll (#9)", h.HangUps[1]);
    }

    // A name the roster could not place is looked at again once `who` supplies a
    // record. That is the same room display with one more monster known on it:
    // the one already answered is not answered again, the new one is.
    [Fact]
    public void AMonsterPlacedLater_InsideTheSameDisplay_IsAnsweredOnce()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre, gorgon.");
        Assert.Single(h.HangUps);

        h.AddMonster(10, "gorgon");
        h.Relationships[10] = MonsterRelationship.Hangup;
        h.Classifier.ReclassifyUnknown();
        h.Classifier.ReclassifyUnknown();
        h.Classifier.ReemitCurrent();

        Assert.Equal(2, h.HangUps.Count);
        Assert.StartsWith("gorgon (#10)", h.HangUps[1]);
    }

    [Fact]
    public void ARosterWithoutIt_EndsTheSighting()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: giant rat, ogre.");

        h.Classifier.RemoveDepartedEntity("ogre");
        h.Feed("An ogre stomps into the room from southeast.");

        Assert.Equal(2, h.HangUps.Count);
    }

    [Fact]
    public void AHangUpThatDidNotGoOut_IsSaid_AndNotRetriedThisSighting()
    {
        using Harness h = new() { Outcome = EscapeOutcome.NotSent };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: ogre.");
        h.Classifier.ReemitCurrent();

        Assert.Single(h.HangUps);
        Assert.Contains("did not go out", h.Watcher.LastHangupSighting);
        Assert.Single(h.WarnLines);
    }

    // ----- while the line is dropping -----------------------------------

    [Fact]
    public void WhileTheLineIsDropping_NothingMoreIsSent()
    {
        using Harness h = new() { Outcome = EscapeOutcome.HungUp };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Relationships[Troll] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");

        h.Feed("A troll lumbers into the room from north.");
        h.Feed("Also here: ogre, troll.");

        Assert.Single(h.HangUps);
    }

    // The client closes the line itself, so this is the hang-up that did not
    // take: the watch cannot stay off for good on the strength of it.
    [Fact]
    public void AHangUpWithNoDropAfterIt_PutsTheWatchBackOn()
    {
        using Harness h = new() { Outcome = EscapeOutcome.HungUp };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");

        h.Clock += TimeSpan.FromSeconds(11);
        h.Feed("Also here: ogre.");

        Assert.Equal(2, h.HangUps.Count);
        Assert.Contains("did not drop", Assert.Single(h.WarnLines));
    }

    // ----- a dropped connection -----------------------------------------

    // The roster used to outlive the link, and anything that re-issued it while
    // offline or at the login (the Auto-Combat toggle, a PvP fight ending) was
    // read as the monster seen again.
    [Fact]
    public void AfterOurHangUpDropsTheLine_AReissuedRosterSendsNothing()
    {
        using Harness h = new() { Outcome = EscapeOutcome.HungUp };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");

        h.Watcher.NoteDisconnected();
        h.Classifier.ReemitCurrent();

        Assert.Single(h.HangUps);
    }

    [Fact]
    public void NothingIsSeenWhileDisconnected()
    {
        using Harness h = new() { Outcome = EscapeOutcome.HangupsDisabled };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");
        h.HangUps.Clear();

        h.Classifier.NoteGameLeft();
        h.Watcher.NoteDisconnected();
        h.Outcome = EscapeOutcome.HungUp;
        h.Classifier.ReemitCurrent();

        Assert.Null(h.Classifier.Current);
        Assert.Empty(h.HangUps);
    }

    // The monster left while we were away and the room is empty, so no "Also
    // here:" line rebuilds the roster: the first arrival must not be added to it.
    [Fact]
    public void AnArrivalAfterTheReconnect_IsNotAddedToTheOldRoster()
    {
        using Harness h = new() { Outcome = EscapeOutcome.HangupsDisabled };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");
        h.HangUps.Clear();

        h.Classifier.NoteGameLeft();
        h.Watcher.NoteDisconnected();
        h.Outcome = EscapeOutcome.HungUp;
        h.Feed("A giant rat scurries into the room from north.");

        Assert.Equal("giant rat", Assert.Single(h.Classifier.Current!.Value.Entities).ResolvedName);
        Assert.Empty(h.HangUps);
    }

    // ----- the minute after a reconnect ---------------------------------

    [Fact]
    public void AfterReconnecting_TheWatchIsOffForAMinute_FromTheFirstGamePrompt()
    {
        using Harness h = new();
        h.HangUpAndDrop();
        Assert.Equal("watch off: the minute starts at the first game prompt after the reconnect", h.Watcher.DescribeHold());
        Assert.Null(h.Watcher.HoldText);

        // Back in the same room, the monster still there: the room is displayed
        // ahead of the first prompt.
        h.Feed("Also here: ogre.");
        h.Classifier.ReemitCurrent();
        Assert.Single(h.HangUps);
        Assert.Single(h.InfoLines, line => line.Contains("the watch is off after the reconnect"));

        h.Clock += TimeSpan.FromMinutes(5);   // however long the login took
        h.Watcher.NoteInGamePrompt();
        Assert.Equal("Hangup watch off 1:00", h.Watcher.HoldText);
        Assert.Equal(60, h.Watcher.HoldSecondsLeft);
        Assert.Contains("Hangup watch off for 60 seconds", Assert.Single(h.Notices));
        Assert.Equal(1, h.HoldChanges);

        h.Tick();
        Assert.Equal("Hangup watch off 0:59", h.Watcher.HoldText);
        Assert.Equal("watch off, 59s left", h.Watcher.DescribeHold());
        Assert.Equal(2, h.HoldChanges);

        h.Feed("Also here: ogre.");
        h.Tick(58);
        Assert.Equal("Hangup watch off 0:01", h.Watcher.HoldText);
        Assert.Single(h.HangUps);
    }

    [Fact]
    public void WhenTheMinuteEnds_AMonsterStillThereIsAnswered()
    {
        using Harness h = new();
        h.HangUpAndDrop();
        h.Feed("Also here: ogre.");
        h.Watcher.NoteInGamePrompt();

        h.Tick(60);

        Assert.Null(h.Watcher.HoldText);
        Assert.Equal("none", h.Watcher.DescribeHold());
        Assert.Equal(new[] { "Hangup watch back on" }, h.Notices.Skip(1));
        Assert.Equal(2, h.HangUps.Count);
        Assert.Empty(h.Scheduled);
    }

    [Fact]
    public void WhenTheMinuteEnds_AnEmptyRoomSendsNothing()
    {
        using Harness h = new();
        h.HangUpAndDrop();
        h.Watcher.NoteInGamePrompt();

        h.Tick(60);

        Assert.Null(h.Watcher.HoldText);
        Assert.Single(h.HangUps);
    }

    // A second hang-up and reconnect starts a minute of its own.
    [Fact]
    public void EachHangUpAndReconnect_StartsItsOwnMinute()
    {
        using Harness h = new();
        h.HangUpAndDrop();
        h.Feed("Also here: ogre.");
        h.Watcher.NoteInGamePrompt();
        h.Tick(60);
        Assert.Equal(2, h.HangUps.Count);

        h.Classifier.NoteGameLeft();
        h.Watcher.NoteDisconnected();
        h.Feed("Also here: ogre.");
        h.Watcher.NoteInGamePrompt();

        Assert.Equal("Hangup watch off 1:00", h.Watcher.HoldText);
        Assert.Equal(2, h.HangUps.Count);
    }

    // A prompt between the exit command and the drop is not the reconnect.
    [Fact]
    public void APromptBeforeTheLineDrops_DoesNotStartTheMinute()
    {
        using Harness h = new() { Outcome = EscapeOutcome.HungUp };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");

        h.Watcher.NoteInGamePrompt();

        Assert.Null(h.Watcher.HoldText);
        Assert.Empty(h.Notices);
    }

    // Another path's escape had just gone out when the monster was seen: the
    // sighting is answered and nothing more is asked for while its roster stands.
    [Fact]
    public void AnEscapeAlreadyUnderWay_AnswersTheSighting()
    {
        using Harness h = new() { Outcome = EscapeOutcome.AlreadyEscaping };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: ogre.");
        h.Outcome = EscapeOutcome.HungUp;
        h.Classifier.ReemitCurrent();

        Assert.Single(h.HangUps);
        Assert.Contains("another escape had just gone out", Assert.Single(h.InfoLines));
        Assert.Empty(h.WarnLines);
    }

    // A low-HP or PvP hang-up had gone out a moment before the monster was seen,
    // and the line drops for it: the monster was in sight, so the reconnect gets
    // its minute all the same.
    [Fact]
    public void ADropRightAfterSeeingItDuringAnotherEscape_TurnsTheWatchOff()
    {
        using Harness h = new() { Outcome = EscapeOutcome.AlreadyEscaping };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");

        h.Classifier.NoteGameLeft();
        h.Watcher.NoteDisconnected();
        h.Outcome = EscapeOutcome.HungUp;
        h.Feed("Also here: ogre.");
        h.Watcher.NoteInGamePrompt();

        Assert.Equal("Hangup watch off 1:00", h.Watcher.HoldText);
        Assert.Single(h.HangUps);
    }

    // The other escape may have been a wimpy jump, which drops no line: nothing is
    // waited on, nothing is warned about, and a drop long after is not ours.
    [Fact]
    public void SeeingItDuringAnotherEscape_OwesNoDrop()
    {
        using Harness h = new() { Outcome = EscapeOutcome.AlreadyEscaping };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");

        h.Clock += TimeSpan.FromSeconds(11);
        h.Outcome = EscapeOutcome.Jumped;
        h.Feed("Also here: ogre.");
        Assert.Equal(2, h.HangUps.Count);
        Assert.Empty(h.WarnLines);

        h.Classifier.NoteGameLeft();
        h.Watcher.NoteDisconnected();
        Assert.Equal("none", h.Watcher.DescribeHold());
    }

    // The minute is for a reconnect the user makes. The PvP response's own
    // dial-back enters the game unattended, so it gets none.
    [Fact]
    public void AnAutomaticReconnect_GetsNoMinute()
    {
        using Harness h = new();
        h.HangUpAndDrop();

        h.Watcher.CancelHold();
        h.Watcher.CancelHold();
        Assert.Equal("none", h.Watcher.DescribeHold());
        Assert.Single(h.InfoLines, line => line.Contains("the reconnect is automatic"));

        h.Watcher.NoteInGamePrompt();
        h.Feed("Also here: ogre.");

        Assert.Null(h.Watcher.HoldText);
        Assert.Empty(h.Notices);
        Assert.Equal(2, h.HangUps.Count);
    }

    // Out at the board's menu when the minute ends, nothing is sent there; the
    // monster is answered on the way back into the game.
    [Fact]
    public void WhenTheMinuteEndsAtTheBoardsMenu_TheAnswerWaitsForTheGame()
    {
        using Harness h = new();
        h.HangUpAndDrop();
        h.Watcher.NoteInGamePrompt();
        h.Feed("Also here: ogre.");

        h.AtBoardMenu = true;
        h.Tick(60);
        Assert.Null(h.Watcher.HoldText);
        Assert.Single(h.HangUps);
        Assert.Single(h.InfoLines, line => line.Contains("board's menu"));

        h.AtBoardMenu = false;
        h.Watcher.NoteBackInGame();
        Assert.Equal(2, h.HangUps.Count);
    }

    // A carrier loss inside the minute neither restarts nor ends it: it runs out
    // on the clock, offline or not, and the next room display is answered.
    [Fact]
    public void ASecondDropDuringTheMinute_LetsItRunOut()
    {
        using Harness h = new();
        h.HangUpAndDrop();
        h.Watcher.NoteInGamePrompt();
        h.Feed("Also here: ogre.");
        h.Tick(10);

        h.Classifier.NoteGameLeft();
        h.Watcher.NoteDisconnected();
        Assert.Equal("Hangup watch off 0:50", h.Watcher.HoldText);

        h.Tick(50);
        Assert.Null(h.Watcher.HoldText);
        Assert.Equal("none", h.Watcher.DescribeHold());
        Assert.Equal("Hangup watch back on", h.Notices.Last());
        Assert.Single(h.HangUps);

        h.Watcher.NoteInGamePrompt();
        h.Feed("Also here: ogre.");
        Assert.Null(h.Watcher.HoldText);
        Assert.Equal(2, h.HangUps.Count);
    }

    [Fact]
    public void ADisposedWatcher_CountsNothingDown()
    {
        Harness h = new();
        h.HangUpAndDrop();
        h.Watcher.NoteInGamePrompt();
        int changes = h.HoldChanges;

        h.Dispose();
        h.Tick(60);

        Assert.Equal(changes, h.HoldChanges);
        Assert.Single(h.Notices);
    }

    [Theory]
    [InlineData(EscapeOutcome.Jumped)]
    [InlineData(EscapeOutcome.NotSent)]
    [InlineData(EscapeOutcome.HangupsDisabled)]
    public void ADropThatWasNotOurHangUp_TurnsNothingOff(EscapeOutcome outcome)
    {
        using Harness h = new() { Outcome = outcome };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");
        h.HangUps.Clear();

        h.Classifier.NoteGameLeft();
        h.Watcher.NoteDisconnected();
        h.Watcher.NoteInGamePrompt();
        h.Outcome = EscapeOutcome.HungUp;
        h.Feed("Also here: ogre.");

        Assert.Null(h.Watcher.HoldText);
        Assert.Equal("none", h.Watcher.DescribeHold());
        Assert.Single(h.HangUps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnotherCharacter_HasNoHold_AndNoLastSighting(bool minuteRunning)
    {
        using Harness h = new();
        h.HangUpAndDrop();
        if (minuteRunning) h.Watcher.NoteInGamePrompt();
        int changes = h.HoldChanges;

        h.Watcher.Reset();

        Assert.Null(h.Watcher.HoldText);
        Assert.Equal("none", h.Watcher.DescribeHold());
        Assert.Equal("(none this session)", h.Watcher.LastHangupSighting);
        Assert.Equal(minuteRunning ? changes + 1 : changes, h.HoldChanges);

        // The old minute's timer has nothing left to do, and the next prompt
        // starts none.
        h.Tick(60);
        h.Watcher.NoteInGamePrompt();
        Assert.Null(h.Watcher.HoldText);
        Assert.Equal(minuteRunning ? 1 : 0, h.Notices.Count);

        h.Feed("Also here: ogre.");
        Assert.Equal(2, h.HangUps.Count);
    }

    // ----- what stops it -------------------------------------------------

    [Theory]
    [InlineData(EscapeOutcome.HangupsDisabled, "Disable Hangups is on")]
    [InlineData(EscapeOutcome.AllOff, "Allow hangup in all-off mode is not ticked")]
    public void HeldByASwitch_SaidOncePerSighting_AndAnsweredOnceItIsLifted(EscapeOutcome held, string why)
    {
        using Harness h = new() { Outcome = held };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: ogre.");
        h.Classifier.ReemitCurrent();
        h.Feed("A giant rat scurries into the room from north.");

        string line = Assert.Single(h.InfoLines);
        Assert.Contains("ogre (#7) seen in Town Square (1/5)", line);
        Assert.Contains(why, line);
        Assert.Contains(why, h.Watcher.LastHangupSighting);

        h.HangUps.Clear();
        h.Outcome = EscapeOutcome.HungUp;
        h.Classifier.ReemitCurrent();

        Assert.Single(h.HangUps);
        Assert.Contains("hung up", h.InfoLines.Last());
    }

    // At the board's menu the exit command would be a menu selection.
    [Fact]
    public void AtTheBoardsMenu_NothingIsAskedFor()
    {
        using Harness h = new() { Outcome = EscapeOutcome.HangupsDisabled };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Feed("Also here: ogre.");
        h.HangUps.Clear();

        h.AtBoardMenu = true;
        h.Outcome = EscapeOutcome.HungUp;
        h.Classifier.ReemitCurrent();
        h.Classifier.ReemitCurrent();

        Assert.Empty(h.HangUps);
        Assert.Single(h.InfoLines, line => line.Contains("board's menu"));
    }

    // The PvP actions win (user, 2026-10-09): during a fight with a player nothing
    // is asked for, and the roster re-issued at its end is answered.
    [Fact]
    public void DuringAFightWithAPlayer_NothingIsAskedFor_UntilItEnds()
    {
        using Harness h = new() { Outcome = EscapeOutcome.HungUp, PvpFightActive = true };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: ogre.");
        h.Classifier.ReemitCurrent();
        h.Feed("A giant rat scurries into the room from north.");

        Assert.Empty(h.HangUps);
        Assert.Contains("the PvP actions come first", Assert.Single(h.InfoLines));
        Assert.False(h.Watcher.HangupWatchIsOff);

        h.PvpFightActive = false;
        h.Classifier.ReemitCurrent();

        Assert.Single(h.HangUps);
    }

    // What self-defence reads: true only while no hang-up will come by the user's
    // choice or for the minute after a reconnect.
    [Fact]
    public void WatchIsOff_WithDisableHangups_AndForTheMinuteAfterAReconnect()
    {
        using Harness h = new();
        Assert.False(h.Watcher.HangupWatchIsOff);

        h.HangupsDisabled = true;
        Assert.True(h.Watcher.HangupWatchIsOff);
        h.HangupsDisabled = false;

        h.HangUpAndDrop();
        Assert.True(h.Watcher.HangupWatchIsOff);      // from the drop, through the login
        h.Watcher.NoteInGamePrompt();
        h.Tick(59);
        Assert.True(h.Watcher.HangupWatchIsOff);
        h.Tick();
        Assert.False(h.Watcher.HangupWatchIsOff);
    }

    // Nothing automatic responds in all-off mode, and the menu is not the game:
    // a hang-up held for those does not turn self-defence on.
    [Theory]
    [InlineData(EscapeOutcome.AllOff, false)]
    [InlineData(EscapeOutcome.HungUp, true)]
    public void WatchIsOff_IsNotSetByTheOtherHolds(EscapeOutcome outcome, bool atBoardMenu)
    {
        using Harness h = new() { Outcome = outcome, AtBoardMenu = atBoardMenu };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: ogre.");

        Assert.Single(h.InfoLines);
        Assert.False(h.Watcher.HangupWatchIsOff);
    }

    // The whole path through HealthManager: the exit command, the carrier drop and
    // the penalty line after it, and the two switches that stop it: Disable
    // Hangups, and the master switch being off without the all-off opt-in.
    [Theory]
    [InlineData(true, false, false, 1)]    // master switch on
    [InlineData(true, true, false, 0)]     // Disable Hangups
    [InlineData(false, false, false, 0)]   // master switch off
    [InlineData(false, false, true, 1)]    // master switch off, Allow hangup in all-off mode
    [InlineData(false, true, true, 0)]     // Disable Hangups outranks it
    public void SightGoesThroughTheHealthHangUp(bool masterSwitchOn, bool hangupsDisabled, bool allowInAllOff, int expected)
    {
        GeneralSettings general = new() { DisableHangups = hangupsDisabled, AllowHangupInAllOffMode = allowInAllOff };
        LogService log = new();
        List<string> wire = new();
        List<bool> penaltyAsked = new();
        int carrierDrops = 0;
        HangupSignal signal = new();
        using HealthManager health = new(
            new PlayerState(), new MovementCoordinator(log),
            readSettings: () => new HealthSettings(),
            isEnabled: () => masterSwitchOn,
            readHangupCommand: () => "=x",
            getActiveMovementEngine: null,
            getLastSentDirection: null,
            readCombatSettings: null,
            readGeneralSettings: () => general,
            hasEngageableHostiles: null,
            log: log,
            hangupSignal: signal);
        health.SetWireSender(bytes => wire.Add(System.Text.Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        health.SetHangupDisconnect(() => carrierDrops++);
        health.SetMasterSwitch(() => !masterSwitchOn, _ => { });
        health.SetHangupPenaltyLog(pvp =>
        {
            Assert.Contains("=x", wire);
            penaltyAsked.Add(pvp);
        });
        using Harness h = new(hangUp: health.HangUpForMonster);
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: ogre.");
        h.Classifier.ReemitCurrent();

        Assert.Equal(expected, wire.Count(w => w == "=x"));
        Assert.Equal(expected, carrierDrops);
        Assert.Equal(expected, penaltyAsked.Count);
        Assert.DoesNotContain(true, penaltyAsked);
        Assert.Equal(expected == 1, signal.ConsumeDisconnectIntent());
    }

    // ----- a Flee monster: what is seen ----------------------------------

    [Fact]
    public void FleeMonsterOnTheRoomDisplay_StartsARun_AndSaysWhichAndWhere()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Flee;

        h.Feed("Also here: giant rat, ogre.");

        Assert.Equal("ogre (#7) is here, relationship Flee", Assert.Single(h.Flees));
        string line = Assert.Single(h.FleeLines);
        Assert.Contains("ogre (#7) seen in Town Square (1/5)", line);
        Assert.Contains("running", line);
        Assert.Contains("ogre (#7) seen in Town Square (1/5)", h.Watcher.LastFleeSighting);
        // It is not a hang-up, and is not logged or reported as one.
        Assert.Empty(h.HangUps);
        Assert.Empty(h.InfoLines);
        Assert.Equal("(none this session)", h.Watcher.LastHangupSighting);
    }

    [Fact]
    public void FleeMonster_AnArrivalLineThatNamesIt_StartsARun()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Flee;
        h.Feed("Also here: giant rat.");
        Assert.Empty(h.Flees);

        h.Feed("An ogre stomps into the room from southeast.");

        Assert.Single(h.Flees);
    }

    [Theory]
    [InlineData(MonsterRelationship.Enemy)]
    [InlineData(MonsterRelationship.Neutral)]
    [InlineData(MonsterRelationship.Friend)]
    public void EnemyNeutralAndFriend_StartNoRun(MonsterRelationship relationship)
    {
        using Harness h = new();
        h.Relationships[Ogre] = relationship;

        h.Feed("Also here: ogre, giant rat.");

        Assert.Empty(h.Flees);
        Assert.Empty(h.FleeLines);
        Assert.Equal("(none this session)", h.Watcher.LastFleeSighting);
    }

    // ----- a Flee monster: once per sighting -----------------------------

    [Fact]
    public void OneRunPerRoomDisplay_WhateverReissuesItsRoster()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Flee;

        h.Feed("Also here: giant rat, ogre.");
        h.Classifier.ReemitCurrent();
        h.Feed("A giant rat scurries into the room from north.");
        h.Classifier.RemoveDeadEntity("giant rat");
        h.Classifier.ReemitCurrent();

        Assert.Single(h.Flees);
        Assert.Single(h.FleeLines);

        // The same room displayed again, as it is when a walk comes back to it.
        h.Feed("Also here: ogre.");
        Assert.Equal(2, h.Flees.Count);
    }

    [Fact]
    public void AFleeMonsterThatWasNotThereWhenAnswered_IsRunFrom()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Flee;
        h.Relationships[Troll] = MonsterRelationship.Flee;
        h.Feed("Also here: ogre.");

        h.Feed("A troll lumbers into the room from north.");

        Assert.Equal(2, h.Flees.Count);
        Assert.StartsWith("troll (#9)", h.Flees[1]);
    }

    // ----- a Flee monster: Hangup outranks it ----------------------------

    [Fact]
    public void WithAHangupMonsterOnTheSameRoster_TheHangUpIsTheAnswer_AndNoRunStarts()
    {
        using Harness h = new() { Outcome = EscapeOutcome.HungUp };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Relationships[Troll] = MonsterRelationship.Flee;

        h.Feed("Also here: troll, ogre.");
        h.Classifier.ReemitCurrent();

        Assert.Single(h.HangUps);
        Assert.Empty(h.Flees);
        string line = Assert.Single(h.FleeLines);
        Assert.Contains("troll (#9) seen in Town Square (1/5)", line);
        Assert.Contains("a Hangup monster is here as well", line);
    }

    // No hang-up is coming (Disable Hangups on): the Flee monster beside the
    // Hangup one is still there to be run from.
    [Fact]
    public void WithNoHangUpComing_TheFleeMonsterIsRunFrom()
    {
        using Harness h = new() { Outcome = EscapeOutcome.HangupsDisabled, HangupsDisabled = true };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Relationships[Troll] = MonsterRelationship.Flee;

        h.Feed("Also here: troll, ogre.");

        Assert.Equal("troll (#9) is here, relationship Flee", Assert.Single(h.Flees));
    }

    // ----- a Flee monster: what stops the run ----------------------------

    // All autos off is the master switch off, and with it off nothing automatic
    // fires at all (user, 2026-10-09). The flee is not even asked for: an auto
    // turned back on by hand would otherwise let it through. The sighting stays
    // open, so the first roster after the switch is back on is answered.
    [Fact]
    public void MasterSwitchOff_NoRunIsAskedFor_UntilItIsBackOn()
    {
        using Harness h = new() { MasterSwitchOff = true };
        h.Relationships[Ogre] = MonsterRelationship.Flee;

        h.Feed("Also here: ogre.");
        h.Classifier.ReemitCurrent();
        h.Feed("A giant rat scurries into the room from north.");

        Assert.Empty(h.Flees);
        string line = Assert.Single(h.FleeLines);
        Assert.Contains("Auto-All is off", line);
        Assert.Contains("Auto-All is off", h.Watcher.LastFleeSighting);

        h.MasterSwitchOff = false;
        h.Classifier.ReemitCurrent();

        Assert.Single(h.Flees);
        Assert.Contains("running", h.FleeLines.Last());
    }

    // The master switch is asked for the run only: a Hangup monster's answer is the
    // health settings' hang-up, with its own all-off rule.
    [Fact]
    public void MasterSwitchOff_DoesNotHoldAHangupMonstersHangUp()
    {
        using Harness h = new() { MasterSwitchOff = true, Outcome = EscapeOutcome.HungUp };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;

        h.Feed("Also here: ogre.");

        Assert.Single(h.HangUps);
    }

    // The run-if-below flee's own gate: with Auto-Heal and Auto-Rest both off the
    // health engine runs no flee. The sighting stays open, so the first roster
    // after one is turned on is answered.
    [Fact]
    public void HealthEngineOff_SaidOncePerSighting_AndRunFromOnceAnAutoIsOn()
    {
        using Harness h = new() { FleeOutcome = FleeOutcome.EngineOff };
        h.Relationships[Ogre] = MonsterRelationship.Flee;

        h.Feed("Also here: ogre.");
        h.Classifier.ReemitCurrent();
        h.Feed("A giant rat scurries into the room from north.");

        string line = Assert.Single(h.FleeLines);
        Assert.Contains("Auto-Heal and Auto-Rest are both off", line);
        Assert.Contains("Auto-Heal and Auto-Rest are both off", h.Watcher.LastFleeSighting);

        h.FleeOutcome = FleeOutcome.Started;
        h.Classifier.ReemitCurrent();

        Assert.Contains("running", h.FleeLines.Last());
        h.Flees.Clear();
        h.Classifier.ReemitCurrent();
        Assert.Empty(h.Flees);
    }

    // No run could start. It is said, and not asked for again inside the sighting:
    // a walk started beside the monster is not turned round by the next roster.
    [Theory]
    [InlineData(FleeOutcome.NoEngine, "no walk or loop is running")]
    [InlineData(FleeOutcome.Paused, "the walk or loop is paused, which counts as idle")]
    [InlineData(FleeOutcome.NoRoute, "no way out of the room could be found")]
    [InlineData(FleeOutcome.Follower, "a follower does not run off alone")]
    public void ARunThatCouldNotStart_IsSaid_AndNotTriedAgainThisSighting(FleeOutcome outcome, string why)
    {
        using Harness h = new() { FleeOutcome = outcome };
        h.Relationships[Ogre] = MonsterRelationship.Flee;

        h.Feed("Also here: ogre.");
        h.FleeOutcome = FleeOutcome.Started;
        h.Classifier.ReemitCurrent();
        h.Feed("A giant rat scurries into the room from north.");

        Assert.Single(h.Flees);
        string line = Assert.Single(h.FleeLines);
        Assert.Contains("ogre (#7) seen in Town Square (1/5)", line);
        Assert.Contains(why, line);
        // Nothing was sent in the run's place.
        Assert.Empty(h.HangUps);

        h.Feed("Also here: ogre.");
        Assert.Equal(2, h.Flees.Count);
    }

    [Theory]
    [InlineData(FleeOutcome.AlreadyRunning, "a flee was already under way")]
    [InlineData(FleeOutcome.Escaping, "a hang-up or wimpy jump had just gone out")]
    public void AnEscapeAlreadyUnderWay_AnswersTheFleeSighting(FleeOutcome outcome, string why)
    {
        using Harness h = new() { FleeOutcome = outcome };
        h.Relationships[Ogre] = MonsterRelationship.Flee;

        h.Feed("Also here: ogre.");
        h.Classifier.ReemitCurrent();

        Assert.Single(h.Flees);
        Assert.Contains(why, Assert.Single(h.FleeLines));
    }

    [Fact]
    public void AtTheBoardsMenu_NoRunIsAskedFor_UntilBackInTheGame()
    {
        using Harness h = new() { AtBoardMenu = true };
        h.Relationships[Ogre] = MonsterRelationship.Flee;

        h.Feed("Also here: ogre.");
        h.Classifier.ReemitCurrent();

        Assert.Empty(h.Flees);
        Assert.Contains("board's menu", Assert.Single(h.FleeLines));

        h.AtBoardMenu = false;
        h.Watcher.NoteBackInGame();

        Assert.Single(h.Flees);
    }

    // The PvP actions win (user, 2026-10-09), for a Flee monster as for a Hangup one.
    [Fact]
    public void DuringAFightWithAPlayer_NoRunIsAskedFor_UntilItEnds()
    {
        using Harness h = new() { PvpFightActive = true };
        h.Relationships[Ogre] = MonsterRelationship.Flee;

        h.Feed("Also here: ogre.");
        h.Classifier.ReemitCurrent();

        Assert.Empty(h.Flees);
        Assert.Contains("the PvP actions come first", Assert.Single(h.FleeLines));

        h.PvpFightActive = false;
        h.Classifier.ReemitCurrent();

        Assert.Single(h.Flees);
    }

    // A run that waits for a move to land asks, when it has, whether a Flee monster
    // is on the roster of the room it landed in.
    [Fact]
    public void TheRunIsHandedAWayToAskWhetherAFleeMonsterIsStillThere()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Flee;
        h.Feed("Also here: ogre, giant rat.");
        Assert.True(h.StillHere!());

        h.Feed("Also here: giant rat.");
        Assert.False(h.StillHere!());

        h.Feed("Also here: ogre.");
        h.Classifier.NoteGameLeft();
        Assert.False(h.StillHere!());
    }

    // ----- a Flee monster: fought back when no run is coming --------------
    //
    // "fight back" (user, 2026-10-09). Self-defence asks NoAnswerComing of the two
    // relationships it leaves out of the fight: for Flee it is true exactly where
    // the client itself is not going to run.

    [Theory]
    [InlineData(FleeOutcome.NoEngine, true)]         // nothing to run along
    [InlineData(FleeOutcome.Paused, true)]           // a paused walk or loop is idle
    [InlineData(FleeOutcome.NoRoute, true)]          // no way out
    [InlineData(FleeOutcome.Follower, true)]         // stays with the party
    [InlineData(FleeOutcome.EngineOff, true)]        // Auto-Heal and Auto-Rest both off
    [InlineData(FleeOutcome.Started, false)]         // a run is under way
    [InlineData(FleeOutcome.AlreadyRunning, false)]  // so is another
    [InlineData(FleeOutcome.Escaping, false)]        // a hang-up or jump is the answer
    [InlineData(FleeOutcome.Down, false)]            // cannot fight either
    public void NoRunComing_IsSaidToSelfDefence_ByWhatCameOfTheRun(FleeOutcome outcome, bool fightBack)
    {
        using Harness h = new() { FleeOutcome = outcome };
        h.Relationships[Ogre] = MonsterRelationship.Flee;
        Assert.False(h.Watcher.NoAnswerComing(MonsterRelationship.Flee));   // none in sight

        h.Feed("Also here: ogre.");
        h.Classifier.ReemitCurrent();

        Assert.Equal(fightBack, h.Watcher.NoAnswerComing(MonsterRelationship.Flee));
        Assert.Equal(fightBack, Assert.Single(h.FleeLines).Contains("it is fought back if it attacks"));
        // A Flee monster's state says nothing about a Hangup one.
        Assert.False(h.Watcher.NoAnswerComing(MonsterRelationship.Hangup));
    }

    // A run that is over with a Flee monster still on the roster: its first move
    // was refused, or a second Flee monster stood where it landed (seen while the
    // run was still in flight). No other run is coming in this sighting, so the
    // monster is fought back. An escape is not asked after: it ends the stay.
    [Theory]
    [InlineData(FleeOutcome.Started, true)]
    [InlineData(FleeOutcome.AlreadyRunning, true)]
    [InlineData(FleeOutcome.Escaping, false)]
    public void OnceTheRunIsOver_AFleeMonsterStillHere_IsFoughtBack(FleeOutcome outcome, bool foughtBack)
    {
        using Harness h = new() { FleeOutcome = outcome };
        h.Relationships[Ogre] = MonsterRelationship.Flee;
        h.Feed("Also here: ogre.");
        Assert.False(h.Watcher.NoAnswerComing(MonsterRelationship.Flee));   // in flight

        h.FleeInFlight = false;                                             // landed, or refused

        Assert.Equal(foughtBack, h.Watcher.NoAnswerComing(MonsterRelationship.Flee));
        // No second run is chained from here.
        h.Classifier.ReemitCurrent();
        Assert.Single(h.Flees);

        // Away from the monster there is nothing to fight.
        h.Feed("Also here: giant rat.");
        Assert.False(h.Watcher.NoAnswerComing(MonsterRelationship.Flee));
    }

    // Down, nothing can be done. Back on its feet the character is asked again at
    // the next roster, and runs or defends itself.
    [Fact]
    public void WhileDown_NothingIsSaidToBeComing_AndTheSightingStaysOpen()
    {
        using Harness h = new() { FleeOutcome = FleeOutcome.Down };
        h.Relationships[Ogre] = MonsterRelationship.Flee;

        h.Feed("Also here: ogre.");
        h.Classifier.ReemitCurrent();

        Assert.Contains("the character is down", Assert.Single(h.FleeLines));
        Assert.False(h.Watcher.NoAnswerComing(MonsterRelationship.Flee));

        h.FleeOutcome = FleeOutcome.NoEngine;
        h.Classifier.ReemitCurrent();

        Assert.True(h.Watcher.NoAnswerComing(MonsterRelationship.Flee));
    }

    [Fact]
    public void ADroppedConnection_EndsWhatWasSaidOfTheFleeMonster()
    {
        using Harness h = new() { FleeOutcome = FleeOutcome.NoEngine };
        h.Relationships[Ogre] = MonsterRelationship.Flee;
        h.Feed("Also here: ogre.");
        Assert.True(h.Watcher.NoAnswerComing(MonsterRelationship.Flee));

        h.Classifier.NoteGameLeft();
        h.Watcher.NoteDisconnected();

        Assert.False(h.Watcher.NoAnswerComing(MonsterRelationship.Flee));
    }

    // A Hangup monster beside the Flee one, and the hang-up neither went out nor is
    // owed: the all-off rule held it, or no exit command is set. The Flee monster is
    // answered as if alone, and is not said to have been left to the hang-up.
    [Theory]
    [InlineData(EscapeOutcome.AllOff, FleeOutcome.Started)]
    [InlineData(EscapeOutcome.NotSent, FleeOutcome.Started)]
    [InlineData(EscapeOutcome.AllOff, FleeOutcome.NoEngine)]
    [InlineData(EscapeOutcome.NotSent, FleeOutcome.NoEngine)]
    public void HangupAndFleeMonster_WhenNoHangUpWentOut_TheFleeMonsterIsAnsweredAsIfAlone(
        EscapeOutcome hangUp, FleeOutcome flee)
    {
        using Harness h = new() { Outcome = hangUp, FleeOutcome = flee };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Relationships[Troll] = MonsterRelationship.Flee;

        h.Feed("Also here: troll, ogre.");
        h.Classifier.ReemitCurrent();

        Assert.Equal("troll (#9) is here, relationship Flee", Assert.Single(h.Flees));
        Assert.DoesNotContain(h.FleeLines, line => line.Contains("the hang-up is the answer"));
        Assert.Equal(flee == FleeOutcome.NoEngine, h.Watcher.NoAnswerComing(MonsterRelationship.Flee));
    }

    // A hang-up owed (held for the board's menu or the PvP side) is still this
    // room's answer: the Flee monster is not run from ahead of it.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void HangupAndFleeMonster_WhileTheHangUpIsOwed_NoRunStarts(bool atBoardMenu, bool pvp)
    {
        using Harness h = new() { Outcome = EscapeOutcome.HungUp, AtBoardMenu = atBoardMenu, PvpFightActive = pvp };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Relationships[Troll] = MonsterRelationship.Flee;

        h.Feed("Also here: troll, ogre.");

        Assert.Empty(h.Flees);
        Assert.Contains("the hang-up is the answer", Assert.Single(h.FleeLines));
    }

    // The minute after a reconnect held back hang-ups only. When it ends the roster
    // is read again for the Hangup monster; a Flee monster answered in that display
    // is not run from a second time.
    [Fact]
    public void WhenTheMinuteEnds_AnAnsweredFleeSightingIsNotRunAgain()
    {
        using Harness h = new();
        h.Relationships[Troll] = MonsterRelationship.Flee;
        h.HangUpAndDrop();
        h.Watcher.NoteInGamePrompt();
        h.HangUps.Clear();

        h.Feed("Also here: ogre, troll.");
        Assert.Single(h.Flees);                                    // no hang-up is coming: run
        Assert.Empty(h.HangUps);

        h.Tick(60);

        Assert.Single(h.HangUps);                                  // the Hangup monster, now
        Assert.Single(h.Flees);
    }

    // The holds that are someone else's to answer do not turn self-defence on: the
    // PvP actions' roster, the board's menu, and a room the hang-up answers.
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void HeldForSomeoneElsesAnswer_TheFleeMonsterIsNotFoughtBack(bool pvp, bool atBoardMenu, bool hangupMonster)
    {
        using Harness h = new() { PvpFightActive = pvp, AtBoardMenu = atBoardMenu };
        h.Relationships[Ogre] = MonsterRelationship.Flee;
        if (hangupMonster) h.Relationships[Troll] = MonsterRelationship.Hangup;

        h.Feed("Also here: ogre, troll.");

        Assert.Empty(h.Flees);
        Assert.False(h.Watcher.NoAnswerComing(MonsterRelationship.Flee));
    }

    // With the master switch off no run is coming. Whether anything is fought then
    // is self-defence's own gate, as for a Neutral monster: Auto-Combat is among
    // the autos the switch turned off.
    [Fact]
    public void MasterSwitchOff_NoRunIsComing()
    {
        using Harness h = new() { MasterSwitchOff = true };
        h.Relationships[Ogre] = MonsterRelationship.Flee;

        h.Feed("Also here: ogre.");

        Assert.True(h.Watcher.NoAnswerComing(MonsterRelationship.Flee));
    }

    // What was true of a monster that has gone is not true of the next one.
    [Fact]
    public void NoRunComing_EndsWithTheSighting_AndWhenARunStarts()
    {
        using Harness h = new() { FleeOutcome = FleeOutcome.EngineOff };
        h.Relationships[Ogre] = MonsterRelationship.Flee;
        h.Feed("Also here: ogre.");
        Assert.True(h.Watcher.NoAnswerComing(MonsterRelationship.Flee));

        // An auto goes back on: the open sighting is answered with a run.
        h.FleeOutcome = FleeOutcome.Started;
        h.Classifier.ReemitCurrent();
        Assert.False(h.Watcher.NoAnswerComing(MonsterRelationship.Flee));

        h.FleeOutcome = FleeOutcome.NoEngine;
        h.Feed("Also here: ogre.");
        Assert.True(h.Watcher.NoAnswerComing(MonsterRelationship.Flee));

        h.Feed("Also here: giant rat.");
        Assert.False(h.Watcher.NoAnswerComing(MonsterRelationship.Flee));
    }

    // A Hangup and a Flee monster in the room with Disable Hangups on (user,
    // 2026-10-09): "it would only run from the flee monster if a navigation engine
    // was running ... otherwise it'd fight, and if it was idle, and disable hangups
    // was on, it would fight both".
    [Theory]
    [InlineData(FleeOutcome.Started, false)]    // a walk or loop is running: run
    [InlineData(FleeOutcome.NoEngine, true)]    // idle: neither answered on sight, both fought back
    public void HangupAndFleeMonster_WithDisableHangupsOn(FleeOutcome outcome, bool fleeFoughtBack)
    {
        using Harness h = new() { Outcome = EscapeOutcome.HangupsDisabled, HangupsDisabled = true, FleeOutcome = outcome };
        h.Relationships[Ogre] = MonsterRelationship.Hangup;
        h.Relationships[Troll] = MonsterRelationship.Flee;

        h.Feed("Also here: troll, ogre.");

        Assert.Equal("troll (#9) is here, relationship Flee", Assert.Single(h.Flees));
        Assert.True(h.Watcher.NoAnswerComing(MonsterRelationship.Hangup));
        Assert.Equal(fleeFoughtBack, h.Watcher.NoAnswerComing(MonsterRelationship.Flee));
    }

    [Fact]
    public void AnotherCharacter_HasNoLastFleeSighting_AndIsAnsweredAfresh()
    {
        using Harness h = new();
        h.Relationships[Ogre] = MonsterRelationship.Flee;
        h.Feed("Also here: ogre.");

        h.Watcher.Reset();

        Assert.Equal("(none this session)", h.Watcher.LastFleeSighting);
        h.Classifier.ReemitCurrent();
        Assert.Equal(2, h.Flees.Count);
    }

    // ----- a Flee monster: the whole path through HealthManager ----------

    private sealed class RecordingEngine : IRecoverableEngine
    {
        public string Name => "FakeLoop";
        public RoomKey? JourneyOrigin { get; set; }
        public List<Direction> Moves { get; } = new();
        public string? PausedFor { get; private set; }
        public RoomKey? ResumedAt { get; private set; }

        public Direction? PeekNextPlannedDirection() => null;
        public IReadOnlyList<Direction> PeekPlannedDirections(int count) => Array.Empty<Direction>();
        public void SendBacktrackMove(Direction direction) => Moves.Add(direction);
        public void PauseForRecovery(string reason) => PausedFor = reason;
        public void ResumeAfterRecovery(RoomKey recoveredAnchor) => ResumedAt = recoveredAnchor;
        public void AbortFromRecoveryFailure(string detail) { }
    }

    // The game refuses the run's first move, so the character is still beside the
    // monster with no run in flight: self-defence is told to fight it back.
    [Fact]
    public void ARefusedFirstMove_LeavesTheMonsterToBeFoughtBack()
    {
        LogService log = new();
        RecordingEngine loop = new() { JourneyOrigin = new RoomKey(1, 1) };
        using HealthManager health = new(
            new PlayerState(), new MovementCoordinator(log),
            readSettings: () => new HealthSettings(),
            isEnabled: () => true,
            readHangupCommand: null,
            getActiveMovementEngine: () => loop,
            getLastSentDirection: () => Direction.N,
            readCombatSettings: () => new CombatSettings { RunDistance = 1, BreakBeforeFleeing = false },
            readGeneralSettings: null,
            hasEngageableHostiles: null,
            log: log,
            findReversePath: (_, _) => new[] { Direction.S });
        health.NoteRoomChanged(new RoomKey(1, 5));
        using Harness h = new(flee: health.FleeFromMonster, fleeInFlight: () => health.IsFleeInFlight);
        h.Relationships[Ogre] = MonsterRelationship.Flee;

        h.Feed("Also here: ogre.");
        Assert.Equal(new[] { Direction.S }, loop.Moves);
        Assert.False(h.Watcher.NoAnswerComing(MonsterRelationship.Flee));   // running

        health.NoteMoveBlocked();                                            // "There is no exit in that direction!"

        Assert.True(h.Watcher.NoAnswerComing(MonsterRelationship.Flee));
        h.Classifier.ReemitCurrent();
        Assert.Single(loop.Moves);                                           // no second run from the same sighting
    }

    // A run cut short by a death, a drop of the line or a profile load ends in
    // HealthManager (EndFlee). The watch needs nothing of its own for it: what the
    // app already tells it on each of the three clears the sighting, and the next
    // one is answered afresh, fight-back included.
    [Theory]
    [InlineData("died")]
    [InlineData("disconnected")]
    [InlineData("profile")]
    public void AFleeCutShort_LeavesNothingStandingInTheWatch(string cut)
    {
        LogService log = new();
        RecordingEngine loop = new() { JourneyOrigin = new RoomKey(1, 1) };
        using HealthManager health = new(
            new PlayerState(), new MovementCoordinator(log),
            readSettings: () => new HealthSettings(),
            isEnabled: () => true,
            readHangupCommand: null,
            getActiveMovementEngine: () => loop,
            getLastSentDirection: () => Direction.N,
            readCombatSettings: () => new CombatSettings { RunDistance = 3, BreakBeforeFleeing = false },
            readGeneralSettings: null,
            hasEngageableHostiles: null,
            log: log,
            findReversePath: (_, _) => new[] { Direction.S, Direction.W, Direction.U });
        health.NoteRoomChanged(new RoomKey(1, 5));
        using Harness h = new(flee: health.FleeFromMonster, fleeInFlight: () => health.IsFleeInFlight);
        h.Relationships[Ogre] = MonsterRelationship.Flee;
        h.Feed("Also here: ogre.");
        Assert.Single(loop.Moves);                                           // two rooms still queued

        switch (cut)
        {
            case "died":
                health.EndFlee("died");
                h.Classifier.NoteRoomChanged();
                break;
            case "disconnected":
                health.EndFlee("disconnected", handBackLiveEngine: true);
                h.Classifier.NoteGameLeft();
                h.Watcher.NoteDisconnected();
                break;
            default:
                health.EndFlee("another profile was loaded");
                h.Watcher.Reset();
                break;
        }

        Assert.False(health.IsFleeInFlight);
        Assert.False(h.Watcher.NoAnswerComing(MonsterRelationship.Flee));
        health.NoteRoomChanged(new RoomKey(1, 9));
        Assert.Single(loop.Moves);                                           // no queued step went out

        h.Feed("Also here: ogre.");                                          // the next sighting
        Assert.Equal(2, loop.Moves.Count);
        health.NoteMoveBlocked();
        Assert.True(h.Watcher.NoAnswerComing(MonsterRelationship.Flee));
    }

    // The sight sends what the health settings' own flee sends: `break` when a fight
    // is on, then the Combat tab's run distance back along the loop, one move per
    // room, and the loop picked up again where the run lands. No exit command.
    [Theory]
    [InlineData(true, false, false, null)]                               // an Auto on, a loop running
    [InlineData(false, false, false, "Auto-Heal and Auto-Rest are both off")]
    [InlineData(true, true, false, "a follower does not run off alone")]
    [InlineData(true, false, true, "Auto-All is off")]                   // an auto turned back on by hand
    public void SightGoesThroughTheHealthFlee(bool healthEngineOn, bool follower, bool masterSwitchOff, string? whyNot)
    {
        LogService log = new();
        List<string> wire = new();
        RecordingEngine loop = new() { JourneyOrigin = new RoomKey(1, 1) };
        CombatSettings combat = new() { RunDistance = 2, RunDirection = RunDirection.Backward, BreakBeforeFleeing = true };
        PlayerState state = new();
        using HealthManager health = new(
            state, new MovementCoordinator(log),
            readSettings: () => new HealthSettings(),
            isEnabled: () => healthEngineOn,
            readHangupCommand: () => "=x",
            getActiveMovementEngine: () => loop,
            getLastSentDirection: () => Direction.N,
            readCombatSettings: () => combat,
            readGeneralSettings: () => new GeneralSettings(),
            hasEngageableHostiles: () => false,
            log: log,
            hasHostileInRoom: () => false,
            findReversePath: (_, _) => new[] { Direction.S, Direction.W, Direction.U });
        health.SetWireSender(bytes => wire.Add(System.Text.Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        health.IsServerEngaged = () => true;
        health.SetPartyRoleSync(isPartyFollower: () => follower, requestPartyWait: () => { }, requestPartyOk: () => { });
        state.MaxHp = 200;
        state.Hp = 200;
        state.HasPromptData = true;
        health.NoteRoomChanged(new RoomKey(1, 5));
        using Harness h = new(flee: health.FleeFromMonster) { MasterSwitchOff = masterSwitchOff };
        h.Relationships[Ogre] = MonsterRelationship.Flee;

        h.Feed("Also here: ogre.");
        h.Classifier.ReemitCurrent();

        if (whyNot is not null)
        {
            Assert.Empty(loop.Moves);
            Assert.Null(loop.PausedFor);
            Assert.Empty(wire);
            Assert.Contains(whyNot, Assert.Single(h.FleeLines));
            return;
        }

        Assert.Equal(new[] { "break" }, wire);
        Assert.Equal(new[] { Direction.S }, loop.Moves);
        Assert.Contains("ogre (#7) is here, relationship Flee", loop.PausedFor);
        Assert.Contains("running", Assert.Single(h.FleeLines));

        health.NoteRoomChanged(new RoomKey(1, 4));
        Assert.Equal(new[] { Direction.S, Direction.W }, loop.Moves);
        Assert.Null(loop.ResumedAt);

        health.NoteRoomChanged(new RoomKey(1, 3));
        Assert.Equal(2, loop.Moves.Count);
        Assert.Equal(new RoomKey(1, 3), loop.ResumedAt);
        Assert.DoesNotContain("=x", wire);
    }
}
