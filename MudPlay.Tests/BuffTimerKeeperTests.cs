using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Buff timers across time out of the game and across a client restart: "buffs
// survive disconnects, relogs", and "we should have buffs that we're tracking
// persist between sessions.. BUT for each buff we're tracking, as soon as we've
// disconnected all timers need to be paused until we're back in the game, then they
// all resume" (user, 2026-10-10).
public sealed class BuffTimerKeeperTests : IDisposable
{
    private readonly string _root;

    public BuffTimerKeeperTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-buff-timer-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const int Waterskin = 283;
    private const int WaterskinBuff = 711;
    private const string SwigLine = "You take a swig of water from your waterskin.";

    private static readonly RoomHazardIndex.BuffCounter Counter =
        new(WaterskinBuff, 712, 1800, new[] { Waterskin }, Array.Empty<int>());

    // One client: the cast pass, the hazard counter and the keeper over a profile,
    // all on one clock.
    private sealed class Client : IDisposable
    {
        public DateTime Now;
        public CharacterProfile Profile;
        public string? ProfileName = "Tester";
        public string? Realm = "Board/Stock";
        public int Saves;
        public LogService Log { get; } = new();
        public List<string> CastsSent { get; } = new();
        public CastCoordinator Cast { get; }
        public CastingDirector Director { get; }
        public AutoHazardCounterProvisioner Hazard { get; }
        public BuffTimerKeeper Keeper { get; }
        private readonly MessageRouter _router = new();

        public Client(DateTime now, CharacterProfile profile)
        {
            Now = now;
            Profile = profile;
            DefaultPatterns.Seed(_router);
            Cast = new CastCoordinator(_router, new LogService());
            Cast.SetWireSender(_ => { });
            Cast.CastSent += CastsSent.Add;
            PlayerState state = new() { MaxHp = 200, Hp = 200, HasPromptData = true };
            Director = new CastingDirector(state, Cast,
                readSpells: () => new SpellsSettings(), readHealth: () => new HealthSettings(),
                isEnabled: () => true, log: new LogService(),
                now: () => Now);
            Hazard = new AutoHazardCounterProvisioner(
                resolveRoom: _ => null,
                hazardForSpell: _ => null,
                carriedCount: id => id == Waterskin ? 1 : 0,
                itemName: id => id == Waterskin ? "waterskin" : null,
                messageMatcherForSpell: spell => spell == WaterskinBuff
                    ? line => line.Contains("swig of water", StringComparison.OrdinalIgnoreCase)
                    : null,
                now: () => new DateTimeOffset(Now, TimeSpan.Zero),
                allCounters: () => new[] { Counter });
            Keeper = new BuffTimerKeeper(
                Director, Hazard,
                profile: () => ProfileName is null ? null : Profile,
                profileName: () => ProfileName,
                realmKey: () => Realm,
                saveProfile: () =>
                {
                    Saves++;
                    Keeper!.WriteTo(Profile);      // what ProfileService.Save raises
                },
                utcNow: () => Now,
                log: Log);
        }

        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);

        // A buff on ourselves with this much left of its length, running.
        public void RaiseBuff(string code, int total, int left)
        {
            Director.RestoreSelfTimers(new[]
            {
                new SavedBuffTimer { Short = code, RemainingSec = left, MarginSec = 15, TotalSec = total },
            });
            Director.ResumeBuffTimers();
        }

        public double LeftOn(string code)
        {
            ActiveBuffTimer timer = Director.SnapshotActiveBuffs().Single(t => t.Short == code);
            return (timer.Until - (Director.PausedAtUtc ?? Now)).TotalSeconds;
        }

        public IReadOnlyList<string> LogLines => Log.Snapshot().Select(e => e.Message).ToList();

        public void Dispose()
        {
            Director.Dispose();
            Cast.Dispose();
        }
    }

    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // In the game with a buff up and the waterskin drunk.
    private static Client InTheGame(CharacterProfile profile)
    {
        Client c = new(Start, profile);
        c.Keeper.NoteBackInGame();
        c.RaiseBuff("bles", total: 300, left: 300);
        c.Hazard.OnServerLine(SwigLine);
        return c;
    }

    // ----- out of the game ------------------------------------------------

    [Fact]
    public void LeavingTheGame_StopsEveryTimer_AndSavesWhatEachHadLeft()
    {
        using Client c = InTheGame(new CharacterProfile());
        c.Advance(100);

        c.Keeper.NoteLeftGame("the link dropped");

        Assert.Equal(1, c.Saves);
        SavedBuffTimers saved = c.Profile.BuffTimers!;
        Assert.Equal(c.Now, saved.FrozenAtUtc);
        Assert.Equal("Board/Stock", saved.Realm);
        Assert.Equal("Tester", saved.Profile);
        SavedBuffTimer bles = Assert.Single(saved.Buffs);
        Assert.Equal(("bles", 200, 15, 300), (bles.Short, bles.RemainingSec, bles.MarginSec, bles.TotalSec));
        SavedHazardBuff water = Assert.Single(saved.HazardBuffs);
        Assert.Equal((WaterskinBuff, 1700, true), (water.BuffSpell, water.RemainingSec, water.Confirmed));
        Assert.Contains(c.LogLines, l => l.StartsWith("out of the game (the link dropped) — 2 buff timers stand still"));

        // However long it stays out, a later save writes the same time left.
        c.Advance(36_000);
        c.Keeper.WriteTo(c.Profile);
        Assert.Equal(200, c.Profile.BuffTimers!.Buffs[0].RemainingSec);
        Assert.Equal(1700, c.Profile.BuffTimers.HazardBuffs[0].RemainingSec);
        Assert.Equal(Start.AddSeconds(100), c.Profile.BuffTimers.FrozenAtUtc);
    }

    [Fact]
    public void BackInTheGame_EveryTimerRunsOnWithWhatItHadLeft_AndTheSavedCopyIsCleared()
    {
        using Client c = InTheGame(new CharacterProfile());
        c.Advance(100);
        c.Keeper.NoteLeftGame("the link dropped");
        c.Advance(5000);                           // longer than the buff lasts

        c.Keeper.NoteBackInGame();

        Assert.Equal(200, c.LeftOn("bles"));
        Assert.Contains("in 1700 s", c.Hazard.DescribeTracking()[0]);
        Assert.Equal(2, c.Saves);                  // one out, one back in
        Assert.Null(c.Profile.BuffTimers);
    }

    // A save made in the game carries no timers: time only stops while the character
    // is out, so a record taken in the game would be wrong by however long the client
    // ran on. A client that dies in the game leaves none.
    [Fact]
    public void InTheGame_ASaveCarriesNoTimers()
    {
        using Client c = InTheGame(new CharacterProfile());

        c.Keeper.WriteTo(c.Profile);

        Assert.Null(c.Profile.BuffTimers);
    }

    // The game left twice before a prompt (the board's menu, then the link): one
    // stretch, one save, and the time of the first.
    [Fact]
    public void LeftTwiceBeforeAPrompt_IsOneStretchAway()
    {
        using Client c = InTheGame(new CharacterProfile());
        c.Keeper.NoteLeftGame("the game was left");
        c.Advance(60);
        c.Keeper.NoteLeftGame("the link dropped");
        c.Advance(60);

        c.Keeper.NoteBackInGame();

        Assert.Equal(300, c.LeftOn("bles"));
        Assert.Equal(2, c.Saves);
    }

    // The client closes with the character still in the game: the link goes with it,
    // so the timers stop there and the closing save carries them.
    [Fact]
    public void ClientClosingInTheGame_TheClosingSaveCarriesTheTimers()
    {
        using Client c = InTheGame(new CharacterProfile());
        c.Advance(100);

        c.Keeper.NoteClientClosing();
        c.Keeper.WriteTo(c.Profile);               // the window's own save on the way out

        Assert.Equal(200, c.Profile.BuffTimers!.Buffs.Single().RemainingSec);
        Assert.Equal(c.Now, c.Profile.BuffTimers.FrozenAtUtc);
    }

    // The default profile is the template new characters are made from: timers are
    // neither written to it nor read from it.
    [Fact]
    public void TheDefaultProfile_CarriesNoTimers()
    {
        using Client c = InTheGame(new CharacterProfile());
        c.ProfileName = null;

        c.Keeper.NoteLeftGame("the link dropped");
        c.Keeper.WriteTo(c.Profile);

        Assert.Null(c.Profile.BuffTimers);
        Assert.Equal(0, c.Saves);                  // nor is that file rewritten at every disconnect
        Assert.Contains(c.LogLines, l => l.EndsWith("not saved, no character's profile is loaded"));
    }

    // The client began to close and then did not (the game goes on, a prompt shows):
    // the timers run again, and a save made in the game carries none, as ever.
    [Fact]
    public void ClientDidNotCloseAfterAll_TheNextPromptPutsItBackInTheGame()
    {
        using Client c = InTheGame(new CharacterProfile());
        c.Advance(100);
        c.Keeper.NoteClientClosing();

        c.Keeper.NoteBackInGame();                 // PromptScanner.PromptObserved
        c.Advance(250);
        c.Keeper.WriteTo(c.Profile);

        Assert.Null(c.Profile.BuffTimers);
        Assert.Equal(-50, c.LeftOn("bles"));       // running: 300 s, 350 s on
        Assert.Contains(c.Keeper.Describe(), l => l == "running: the character is in the game");
    }

    // ----- between sessions -----------------------------------------------

    // The whole way round: saved on the way out of the game, written to disk, read by
    // a client started later, put back standing still, and run on from that client's
    // first in-game prompt with exactly what was left.
    [Fact]
    public void RestartRoundTrip_ThroughTheProfileFile()
    {
        string file = Path.Combine(_root, "Tester.json");
        using (Client first = InTheGame(new CharacterProfile()))
        {
            first.Advance(100);
            first.Keeper.NoteLeftGame("the link dropped");
            JsonStore.Save(file, first.Profile);
        }

        CharacterProfile loaded = JsonStore.Load<CharacterProfile>(file)!;
        using Client second = new(Start.AddDays(3), loaded);
        second.Keeper.RestoreFrom(loaded);

        Assert.Equal(second.Now, second.Director.PausedAtUtc);
        Assert.Equal(200, second.LeftOn("bles"));
        Assert.Contains(second.LogLines, l =>
            l.StartsWith("restored 2 buff timers from the session that left the game at ")
            && l.Contains("(71 h 58 min ago)") && l.Contains("standing still until the game is entered"));
        Assert.Contains(second.Keeper.Describe(), l => l.StartsWith("2 buff timers put back from the session that left the game at "));
        Assert.Contains(second.Keeper.Describe(), l => l.StartsWith("standing still since "));

        // An hour at the login: a save in that time writes them back as they are, and
        // nothing is cast.
        second.Advance(3600);
        second.Keeper.WriteTo(loaded);
        Assert.Equal(200, loaded.BuffTimers!.Buffs.Single().RemainingSec);
        Assert.Equal(Start.AddSeconds(100), loaded.BuffTimers.FrozenAtUtc);
        second.Director.Evaluate();
        Assert.Empty(second.CastsSent);

        second.Keeper.NoteBackInGame();

        Assert.Equal(200, second.LeftOn("bles"));
        Assert.Contains("in 1700 s", second.Hazard.DescribeTracking()[0]);
        Assert.Null(loaded.BuffTimers);            // spent: cleared by the save back in the game
        Assert.Equal(1, second.Saves);
        Assert.Contains(second.Keeper.Describe(), l => l == "running: the character is in the game");
    }

    // A death found at the login (a hang-up death) wipes what was put back, through
    // the reset the cast pass and the hazard counter already have for a death.
    [Fact]
    public void ADeathFoundAtTheLogin_DropsWhatWasPutBack()
    {
        CharacterProfile profile = SavedOutOfTheGame();
        using Client c = new(Start.AddHours(2), profile);
        c.Keeper.RestoreFrom(profile);

        c.Director.ClearSelfBuffTracking();        // RoomTracker.PlayerDeathInferred
        c.Hazard.Forget("death");
        c.Keeper.WriteTo(profile);

        Assert.Empty(c.Director.SnapshotActiveBuffs());
        Assert.Empty(c.Hazard.DescribeTracking());
        Assert.Null(profile.BuffTimers);
    }

    // A record from another realm, or carried into another profile by a copy, says
    // nothing about the character now loaded.
    [Theory]
    [InlineData("Board/Paradigm", "Tester", "they were saved on Board/Stock, and this is Board/Paradigm")]
    [InlineData("Board/Stock", "Copy of Tester", "they were saved for the profile 'Tester', not this one")]
    public void SavedForAnotherRealmOrProfile_IsDropped(string realm, string name, string why)
    {
        CharacterProfile profile = SavedOutOfTheGame();
        using Client c = new(Start.AddHours(2), profile) { Realm = realm, ProfileName = name };

        c.Keeper.RestoreFrom(profile);

        Assert.Empty(c.Director.SnapshotActiveBuffs());
        Assert.Empty(c.Hazard.DescribeTracking());
        Assert.Null(profile.BuffTimers);
        Assert.Contains(c.LogLines, l => l == $"dropped 2 buff timers saved with the character: {why}");
    }

    // The guard is not only for the load. A realm reassigned, or the profile saved
    // under another name, while the character is out of the game: the timers standing
    // still are the other character's, so the next save drops them. It used to write
    // them out again under the new realm, for a later client to put back as its own.
    [Theory]
    [InlineData("Board/Paradigm", "Tester")]
    [InlineData("Board/Stock", "Tester the Second")]
    public void RealmOrProfileChangedWhileOut_TheTimersAreDropped_NotRelabelled(string realm, string name)
    {
        CharacterProfile profile = SavedOutOfTheGame();
        using Client c = new(Start.AddHours(2), profile);
        c.Keeper.RestoreFrom(profile);

        c.Realm = realm;
        c.ProfileName = name;
        c.Keeper.WriteTo(profile);

        Assert.Null(profile.BuffTimers);
        Assert.Empty(c.Director.SnapshotActiveBuffs());
        Assert.Empty(c.Hazard.DescribeTracking());
        Assert.Contains(c.LogLines, l => l.StartsWith("dropped 2 buff timers: they stood still for 'Tester' on Board/Stock, and the profile is now"));

        using Client next = new(Start.AddHours(3), profile) { Realm = realm, ProfileName = name };
        next.Keeper.RestoreFrom(profile);
        Assert.Empty(next.Director.SnapshotActiveBuffs());
    }

    // While nothing changes, every save out of the game writes the record under the
    // realm and name it was frozen for.
    [Fact]
    public void SavesWhileOut_KeepTheRealmAndProfileTheTimersWereFrozenFor()
    {
        CharacterProfile profile = SavedOutOfTheGame();
        using Client c = new(Start.AddHours(2), profile);
        c.Keeper.RestoreFrom(profile);

        c.Keeper.WriteTo(profile);

        Assert.Equal(("Board/Stock", "Tester"), (profile.BuffTimers!.Realm, profile.BuffTimers.Profile));
    }

    // A death the client did not see and learns of after the login (the verdict
    // waited on a `stat`) wiped what was on when the game was left. Exactly the
    // carried timers go (CastingDirectorTests has a buff raised since staying).
    [Fact]
    public void ADeathFoundAfterTheLogin_DropsTheCarriedTimers()
    {
        CharacterProfile profile = SavedOutOfTheGame();
        using Client c = new(Start.AddHours(2), profile);
        c.Keeper.RestoreFrom(profile);
        c.Keeper.NoteBackInGame();
        c.Advance(30);

        Assert.Equal(1, c.Director.ClearCarriedSelfTimers("a death found late"));   // RoomTracker.UnwitnessedDeathRecorded
        Assert.Equal(1, c.Hazard.ForgetCarried("a death found late"));

        Assert.Empty(c.Director.SnapshotActiveBuffs());
        Assert.Empty(c.Hazard.DescribeTracking());
    }

    // The waterskin drunk again since the login is not a carried buff any more.
    [Fact]
    public void AHazardBuffRaisedAgainSince_IsNotDroppedAsCarried()
    {
        CharacterProfile profile = SavedOutOfTheGame();
        using Client c = new(Start.AddHours(2), profile);
        c.Keeper.RestoreFrom(profile);
        Assert.Contains("(put back from the saved profile)", c.Hazard.DescribeTracking()[0]);
        c.Keeper.NoteBackInGame();
        c.Advance(30);
        c.Hazard.OnServerLine(SwigLine);

        Assert.Equal(0, c.Hazard.ForgetCarried("a death found late"));

        Assert.Single(c.Hazard.DescribeTracking());
    }

    // ----- a profile swap ---------------------------------------------------

    // "if they swap profiles while still connected, toss the timers" (user,
    // 2026-10-10). The calls are the ones ProfileService.Load makes, in its order:
    // the outgoing profile's save, ProfileClosed, then ProfileLoaded for the next.
    [Fact]
    public void ProfileSwappedInTheGame_TossesTheTimers_SavesNone_CarriesNoneIn()
    {
        CharacterProfile outgoing = new();
        using Client c = InTheGame(outgoing);
        c.RaiseBuff("mysh", total: 200, left: 200);
        c.Advance(100);
        CharacterProfile incoming = SavedOutOfTheGame();   // with timers of its own on file

        c.Keeper.WriteTo(outgoing);
        c.Keeper.NoteProfileClosed();
        c.Profile = incoming;
        c.ProfileName = "Other";
        c.Keeper.RestoreFrom(incoming);

        Assert.Null(outgoing.BuffTimers);
        Assert.Empty(c.Director.SnapshotActiveBuffs());
        Assert.Empty(c.Hazard.DescribeTracking());
        Assert.Null(c.Director.PausedAtUtc);       // tossed, not frozen
        Assert.Null(incoming.BuffTimers);
        Assert.Contains(c.LogLines, l => l ==
            "the profile was swapped with the character in the game — 3 buff timers tossed: "
            + "not saved with the outgoing profile, and none carried into the next");
        Assert.Contains(c.LogLines, l => l ==
            "dropped 2 buff timers saved with the character: the profile was loaded with a character already in the game");
        Assert.Contains(c.Keeper.Describe(), l => l.StartsWith("3 buff timers tossed at ")
            && l.EndsWith(": the profile was swapped with the character in the game"));

        // The session goes on in the game: nothing comes back at the next save or
        // when the link drops, and a later load of the outgoing profile finds none.
        c.Advance(60);
        c.Keeper.NoteLeftGame("the link dropped");
        Assert.Null(incoming.BuffTimers);
        using Client later = new(Start.AddDays(1), outgoing);
        later.Keeper.RestoreFrom(outgoing);
        Assert.Empty(later.Director.SnapshotActiveBuffs());
        Assert.Empty(later.Hazard.DescribeTracking());
    }

    // Out of the game it is the ordinary swap: the timers stand still, go to disk
    // with the profile they belong to, and are put back when it is loaded again.
    [Fact]
    public void ProfileSwappedOutOfTheGame_KeepsTheTimersWithTheirProfile()
    {
        CharacterProfile outgoing = new();
        using Client c = InTheGame(outgoing);
        c.Advance(100);
        c.Keeper.NoteLeftGame("the link dropped");

        c.Keeper.WriteTo(outgoing);
        c.Keeper.NoteProfileClosed();

        Assert.Equal(200, outgoing.BuffTimers!.Buffs.Single().RemainingSec);
        Assert.Single(outgoing.BuffTimers.HazardBuffs);
        Assert.DoesNotContain(c.LogLines, l => l.Contains("tossed"));
    }

    // The restart for an update is a restart and no swap: the client drops the link
    // (MainWindowViewModel.StandDownForUpdateAsync), closes, and the one started in
    // its place loads the same profile. The timers stop at the drop, ride the
    // profile, and run on from the new client's first in-game prompt.
    [Fact]
    public void UpdateRestart_IsNoSwap_TheTimersPauseAndPersist()
    {
        string file = Path.Combine(_root, "Tester.json");
        using (Client old = InTheGame(new CharacterProfile()))
        {
            old.Advance(100);
            old.Keeper.NoteLeftGame("the link dropped or the game was left");   // the stand-down's disconnect
            old.Advance(5);
            old.Keeper.NoteClientClosing();
            old.Keeper.WriteTo(old.Profile);       // the window's closing save
            JsonStore.Save(file, old.Profile);
            Assert.DoesNotContain(old.LogLines, l => l.Contains("tossed"));
        }

        CharacterProfile loaded = JsonStore.Load<CharacterProfile>(file)!;
        using Client relaunched = new(Start.AddSeconds(120), loaded);
        relaunched.Keeper.NoteProfileClosed();     // whatever was loaded first at startup going
        relaunched.Keeper.RestoreFrom(loaded);
        relaunched.Advance(20);                    // the logon

        relaunched.Keeper.NoteBackInGame();

        Assert.Equal(200, relaunched.LeftOn("bles"));
        Assert.Contains("in 1700 s", relaunched.Hazard.DescribeTracking()[0]);
        Assert.Null(loaded.BuffTimers);
        Assert.DoesNotContain(relaunched.LogLines, l => l.Contains("tossed"));
    }

    // Restoring is bookkeeping: with nothing saved, nothing is touched and the cast
    // pass is not latched.
    [Fact]
    public void NothingSaved_NothingRestored()
    {
        CharacterProfile profile = new();
        using Client c = new(Start, profile);

        c.Keeper.RestoreFrom(profile);

        Assert.Null(c.Director.PausedAtUtc);
        Assert.Contains(c.Keeper.Describe(), l => l == "saved with the character: none");
    }

    private static CharacterProfile SavedOutOfTheGame()
    {
        CharacterProfile profile = new();
        using Client c = InTheGame(profile);
        c.Advance(100);
        c.Keeper.NoteLeftGame("the link dropped");
        return profile;
    }
}
