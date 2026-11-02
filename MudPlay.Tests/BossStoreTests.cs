using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// BossStore resolution + the realm's delta overlay, mirroring QuestStoreTests: the
// overlay wins over the universal seed, a Removed tombstone hides a seed boss,
// added bosses surface, realm flags gate visibility, and Save keeps the overlay a
// delta. Uses a unique scratch set under AppPaths.DataRoot + a temp seed file.
public sealed class BossStoreTests : IDisposable
{
    private readonly string _scratchSet = "boss-test-" + Path.GetRandomFileName();

    // The boss list is the realm's; a set folder stands in for a realm folder here.
    private string RealmFolder => AppPaths.GameDataSetDir(_scratchSet);
    private readonly string _seedPath = Path.Combine(Path.GetTempPath(), "bossseed-" + Path.GetRandomFileName() + ".json");

    public void Dispose()
    {
        try { string f = AppPaths.GameDataSetDir(_scratchSet); if (Directory.Exists(f)) Directory.Delete(f, true); } catch { }
        try { if (File.Exists(_seedPath)) File.Delete(_seedPath); } catch { }
    }

    private void WriteSeed(params BossDef[] defs) => JsonStore.Save(_seedPath, defs.ToList());

    private static BossDef Boss(string name, bool stock = true, bool para = true, params string[] rooms) => new()
    {
        Name = name, MonsterNumber = 1, Rooms = rooms.ToList(),
        InStock = stock, InParadigm = para, RespawnType = BossRespawnType.Timed,
    };

    [Fact]
    public void Resolve_SeedOnly_ReturnsSeed()
    {
        WriteSeed(Boss("crimson mist", rooms: "3/560"));
        BossStore s = new(seedPath: _seedPath); s.OnRealmChanged(RealmFolder);

        BossDef b = Assert.Single(s.Resolve());
        Assert.Equal("crimson mist", b.Name);
        Assert.Equal(new[] { "3/560" }, b.Rooms);
    }

    [Fact]
    public void Resolve_OverlayWins_OverSeed()
    {
        WriteSeed(Boss("cyclops", rooms: "7/730"));
        BossStore s = new(seedPath: _seedPath); s.OnRealmChanged(RealmFolder);
        s.Save([new BossDef { Name = "cyclops", MonsterNumber = 1, Rooms = new() { "7/730" },
            InStock = true, InParadigm = true, StopBefore = false }]);

        // The seed's Stop before is on by default; the user's explicit off wins.
        BossStore reloaded = new(seedPath: _seedPath); reloaded.OnRealmChanged(RealmFolder);
        Assert.False(Assert.Single(reloaded.Resolve()).StopBefore);
    }

    [Fact]
    public void Resolve_RemovedTombstone_HidesSeedBoss()
    {
        WriteSeed(Boss("cyclops", rooms: "7/730"), Boss("chimera", rooms: "3/583"));
        BossStore s = new(seedPath: _seedPath); s.OnRealmChanged(RealmFolder);

        s.Save([s.Resolve().First(b => b.Name == "chimera")]);   // cyclops omitted → removed

        BossStore reloaded = new(seedPath: _seedPath); reloaded.OnRealmChanged(RealmFolder);
        Assert.Equal(new[] { "chimera" }, reloaded.Resolve().Select(b => b.Name).ToArray());
    }

    [Fact]
    public void Resolve_UserAddedBoss_Surfaces()
    {
        WriteSeed(Boss("cyclops", rooms: "7/730"));
        BossStore s = new(seedPath: _seedPath); s.OnRealmChanged(RealmFolder);

        List<BossDef> list = s.Resolve().ToList();
        list.Add(Boss("my custom boss", rooms: "1/100"));
        s.Save(list);

        BossStore reloaded = new(seedPath: _seedPath); reloaded.OnRealmChanged(RealmFolder);
        Assert.Contains(reloaded.Resolve(), b => b.Name == "my custom boss");
    }

    [Fact]
    public void ResolveForRealm_Stock_HidesParadigmOnly()
    {
        WriteSeed(Boss("shared boss", stock: true, para: true, rooms: "1/1"),
                  Boss("para only boss", stock: false, para: true, rooms: "5/5"));
        BossStore s = new(seedPath: _seedPath); s.OnRealmChanged(RealmFolder);

        Assert.Equal(2, s.ResolveForRealm(RealmType.ParaMud).Count);
        Assert.Equal(new[] { "shared boss" }, s.ResolveForRealm(RealmType.Stock).Select(b => b.Name).ToArray());
    }

    [Fact]
    public void Save_UnchangedBoss_IsDropped_SoLaterSeedFlowsThrough()
    {
        WriteSeed(Boss("cyclops", rooms: "7/730"));
        BossStore s = new(seedPath: _seedPath); s.OnRealmChanged(RealmFolder);
        s.Save(s.Resolve());   // re-save unchanged

        // A changed seed (same set) now resolves through — proving no frozen overlay.
        WriteSeed(new BossDef { Name = "cyclops", MonsterNumber = 1, Rooms = new() { "7/999" }, InStock = true, InParadigm = true });
        BossStore reloaded = new(seedPath: _seedPath); reloaded.OnRealmChanged(RealmFolder);
        Assert.Equal(new[] { "7/999" }, Assert.Single(reloaded.Resolve()).Rooms);
    }

    [Fact]
    public void Save_NoActiveRealm_IsNoOp()
    {
        BossStore s = new(seedPath: _seedPath);   // never OnRealmChanged
        s.Save([Boss("x", rooms: "1/1")]);          // must not throw
        Assert.Null(s.ActiveRealmFolder);
    }

    // The list used to be kept with the game-data set. A realm with none of its own
    // takes a copy of its set's; one that has its own keeps it.
    [Fact]
    public void RealmWithNoList_TakesACopyOfItsGameDataSets()
    {
        WriteSeed(Boss("cyclops", rooms: "7/730"));
        string legacySet = "boss-legacy-" + Path.GetRandomFileName();
        string realm = Path.Combine(AppPaths.GameDataSetDir(_scratchSet), "realm-a");
        try
        {
            Directory.CreateDirectory(AppPaths.GameDataSetDir(legacySet));
            JsonStore.Save(AppPaths.LegacySetBossesFile(legacySet),
                new List<BossDef> { Boss("added by hand", rooms: "1/1") });

            BossStore s = new(seedPath: _seedPath); s.OnRealmChanged(realm, legacySet);
            Assert.Contains(s.Resolve(), b => b.Name == "added by hand");
            Assert.True(File.Exists(AppPaths.RealmBossesFile(realm)));

            // Its own list now: a change to the set's old file no longer reaches it.
            JsonStore.Save(AppPaths.LegacySetBossesFile(legacySet), new List<BossDef>());
            BossStore again = new(seedPath: _seedPath); again.OnRealmChanged(realm, legacySet);
            Assert.Contains(again.Resolve(), b => b.Name == "added by hand");
        }
        finally
        {
            try { Directory.Delete(AppPaths.GameDataSetDir(legacySet), true); } catch { }
        }
    }

    // Clients on one realm share the file: a save made by another is picked up.
    [Fact]
    public void TakeInOutsideChanges_ReadsWhatAnotherClientSaved()
    {
        WriteSeed(Boss("cyclops", rooms: "7/730"));
        BossStore first = new(seedPath: _seedPath); first.OnRealmChanged(RealmFolder);
        BossStore second = new(seedPath: _seedPath); second.OnRealmChanged(RealmFolder);
        Assert.False(first.TakeInOutsideChanges());

        second.Save(second.Resolve().Append(Boss("added elsewhere", rooms: "2/2")));
        File.SetLastWriteTimeUtc(AppPaths.RealmBossesFile(RealmFolder), DateTime.UtcNow.AddSeconds(5));

        int changed = 0;
        first.Changed += () => changed++;
        Assert.True(first.TakeInOutsideChanges());
        Assert.Equal(1, changed);
        Assert.Contains(first.Resolve(), b => b.Name == "added elsewhere");
    }

    // Stop before ships off for the bosses that won't attack on sight — the Neutral
    // ones, sheriff lionheart, justicar halford and mayor godfrey — plus the lord of
    // the hunt, and the gigantic black ooze,
    // which is hostile but can't be avoided in the labyrinth (user, 2026-10-01).
    // Every other seed boss leaves it on.
    [Fact]
    public void ShippedSeed_StopBeforeIsOffOnlyForTheListedBosses()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-boss-seed-" + Path.GetRandomFileName());
        try
        {
            AppPaths.ExtractEmbeddedSeeds(dir);
            List<BossDef> seed = JsonStore.Load<List<BossDef>>(Path.Combine(dir, "BossDefs.seed.json"))!;
            // The live flag and its reset default ship in step.
            Assert.All(seed, b => Assert.Equal(b.StopBefore, b.ResetStopBefore));
            Assert.All(seed, b => Assert.False(b.ResetGrabAll));
            string[] off = seed.Where(b => !b.StopBefore).Select(b => b.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[]
            {
                "animated juggernaut",
                "aquilas",
                "argak the grey",
                "catoblepas",
                "choira pyromancer",
                "cocoon",
                "darem tidegrasp",
                "enigma lord",
                "fair maiden",
                "fallen champion",
                "giant pulsating cocoon",
                "giant river turtle",
                "giant roc",
                "gigantic black ooze",
                "grakh bonegrinder",
                "grand master",
                "hanging cocoon",
                "justicar halford",
                "kai master",
                "lallim whitemane",
                "lord chisholm (malformation)",
                "lord of the hunt",
                "massive cocoon",
                "mayor godfrey",
                "mayor of arlysia (arachnigoth)",
                "remik of the ebon blade",
                "sharh'kur",
                "sheriff lionheart",
                "storm giant commander",
                "storm giant king",
                "volodar",
                "wandering cleric",
                "woodelf lord",
            }, off);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* temp cleanup */ }
        }
    }

    // Each boss carries its own reset defaults; an edit to one is a delta the overlay keeps.
    [Fact]
    public void ResetDefaults_ArePerBoss_AndSurviveASave()
    {
        Assert.True(new BossDef().ResetStopBefore);
        Assert.False(new BossDef().ResetGrabAll);

        WriteSeed(Boss("cyclops", rooms: "7/730"));
        BossStore s = new(seedPath: _seedPath); s.OnRealmChanged(RealmFolder);
        BossDef edited = Assert.Single(s.Resolve());
        edited.DefaultStopBefore = false;
        edited.DefaultGrabAll = true;
        s.Save([edited]);

        BossStore reloaded = new(seedPath: _seedPath); reloaded.OnRealmChanged(RealmFolder);
        BossDef back = Assert.Single(reloaded.Resolve());
        Assert.False(back.ResetStopBefore);
        Assert.True(back.ResetGrabAll);
        Assert.True(back.StopBefore);   // the live flag is untouched by a default edit
    }

    // An overlay entry saved before the reset defaults existed doesn't state them, so
    // it resets the way the seed boss does.
    [Fact]
    public void OverlayEntryWithoutResetDefaults_TakesTheSeedBosss()
    {
        BossDef quiet = Boss("mayor godfrey", rooms: "1/1");
        quiet.StopBefore = false; quiet.DefaultStopBefore = false;
        WriteSeed(quiet);
        BossStore s = new(seedPath: _seedPath); s.OnRealmChanged(RealmFolder);
        s.Save([new BossDef { Name = "mayor godfrey", MonsterNumber = 1, Rooms = new() { "1/2" },
            InStock = true, InParadigm = true, StopBefore = false }]);

        BossStore reloaded = new(seedPath: _seedPath); reloaded.OnRealmChanged(RealmFolder);
        BossDef back = Assert.Single(reloaded.Resolve());
        Assert.Equal(new[] { "1/2" }, back.Rooms);
        Assert.False(back.ResetStopBefore);
    }

    // A boss stops the walk one room short unless the user turns that off.
    [Fact]
    public void StopBefore_DefaultsOn()
    {
        Assert.True(new BossDef().StopBefore);
        WriteSeed(Boss("cyclops", rooms: "7/730"));
        BossStore s = new(seedPath: _seedPath); s.OnRealmChanged(RealmFolder);
        Assert.True(Assert.Single(s.Resolve()).StopBefore);
    }

    [Fact]
    public void MatchesSeed_DetectsRoomAndFlagEdits()
    {
        BossDef seed = Boss("x", rooms: "1/1");
        Assert.True(seed.Clone().MatchesSeed(seed));
        BossDef edited = seed.Clone(); edited.StopBefore = !seed.StopBefore;
        Assert.False(edited.MatchesSeed(seed));
        BossDef reroom = seed.Clone(); reroom.Rooms = new() { "1/2" };
        Assert.False(reroom.MatchesSeed(seed));
    }

    // Timer-sync adoption relies on recovering a boss the user REMOVED from their list
    // (the case they care about) — by MDB number or name — even though Resolve() hides
    // it. A genuine non-match returns null.
    [Fact]
    public void FindInCatalog_RecoversRemovedSeedBoss_ByNumberOrName()
    {
        WriteSeed(new BossDef { Name = "shadow lord", MonsterNumber = 412, InStock = true, InParadigm = true });
        BossStore s = new(seedPath: _seedPath); s.OnRealmChanged(RealmFolder);
        s.Save(Array.Empty<BossDef>());          // remove it (Removed tombstone)
        Assert.Empty(s.Resolve());               // hidden from the normal list

        Assert.Equal("shadow lord", s.FindInCatalog(412, null)?.Name);
        Assert.Equal("shadow lord", s.FindInCatalog(null, "shadow lord")?.Name);
        Assert.Null(s.FindInCatalog(999, "nobody"));
    }

    // A saved copy of a boss holds the monster number it had when the user edited it.
    // Nothing in the client edits that number, so when the seed's is corrected the
    // copy must follow, or it goes on reading the wrong record's timer.
    [Fact]
    public void Resolve_SavedCopyTakesTheSeedsMonsterNumberAndGameName()
    {
        WriteSeed(new BossDef { Name = "great green dragon", MonsterNumber = 1038, Rooms = new() { "1/3111" },
            InStock = true, InParadigm = true });
        BossStore s = new(seedPath: _seedPath); s.OnRealmChanged(RealmFolder);
        BossDef edited = s.Resolve().Single();
        edited.GrabAll = true;
        s.Save([edited]);

        WriteSeed(new BossDef { Name = "great green dragon", MonsterNumber = 1058, Rooms = new() { "1/3111" },
            InStock = true, InParadigm = true });
        BossStore reloaded = new(seedPath: _seedPath); reloaded.OnRealmChanged(RealmFolder);

        BossDef b = Assert.Single(reloaded.Resolve());
        Assert.Equal(1058, b.MonsterNumber);
        Assert.True(b.GrabAll);   // the user's edit is kept
    }

    // The shipped list: bosses the game gives one name each have their own entry, so
    // neither replaces the other (the list holds one boss per Name).
    [Fact]
    public void ShippedSeed_KeepsSameNamedBossesApart()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-boss-" + Path.GetRandomFileName());
        try
        {
            AppPaths.ExtractEmbeddedSeeds(dir);
            BossStore s = new(seedPath: Path.Combine(dir, "BossDefs.seed.json"));
            IReadOnlyList<BossDef> all = s.Resolve();

            foreach (string gameName in new[] { "nahr", "master assassin" })
            {
                List<BossDef> pair = all.Where(b => b.GameName == gameName).ToList();
                Assert.Equal(2, pair.Count);
                Assert.Equal(2, pair.Select(b => b.MonsterNumber).Distinct().Count());
                Assert.Empty(pair[0].Rooms.Intersect(pair[1].Rooms));
                Assert.DoesNotContain(all, b => b.Name == gameName);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    // ----- Stop before / Grab All are each character's own --------------------

    // A character's profile, as far as the store sees it: whether one is loaded and
    // the choices it holds.
    private sealed class Character
    {
        public Dictionary<string, BossFlagChoice>? Flags;
        public int Saves;
        public void Wire(BossStore store) => store.SetCharacterFlags(
            hasCharacter: () => true, read: () => Flags, write: f => { Flags = f; Saves++; });
    }

    [Fact]
    public void CharacterFlags_OverlayTheRealmsList_AndASaveKeepsThemOutOfIt()
    {
        WriteSeed(Boss("cyclops", rooms: "7/730"), Boss("chimera", rooms: "3/583"));
        BossStore store = new(seedPath: _seedPath); store.OnRealmChanged(RealmFolder);
        Character fujin = new() { Flags = new() };
        fujin.Wire(store);

        List<BossDef> rows = store.Resolve().ToList();
        Assert.All(rows, b => Assert.True(b.StopBefore));          // the bosses' own default
        rows.First(b => b.Name == "cyclops").StopBefore = false;
        rows.First(b => b.Name == "chimera").GrabAll = true;
        store.Save(rows);

        Assert.False(fujin.Flags!["cyclops"].StopBefore);
        Assert.True(fujin.Flags["chimera"].GrabAll);
        Assert.False(store.Resolve().First(b => b.Name == "cyclops").StopBefore);

        // Another character on the same realm sees none of it.
        BossStore other = new(seedPath: _seedPath); other.OnRealmChanged(RealmFolder);
        Character raijin = new() { Flags = new() };
        raijin.Wire(other);
        Assert.True(other.Resolve().First(b => b.Name == "cyclops").StopBefore);
        Assert.False(other.Resolve().First(b => b.Name == "chimera").GrabAll);
        // ...and the realm's file holds no entry for a boss only ticked, so a later
        // seed change still flows through.
        Assert.False(File.Exists(AppPaths.RealmBossesFile(RealmFolder))
            && File.ReadAllText(AppPaths.RealmBossesFile(RealmFolder)).Contains("cyclops"));
    }

    // The realm's list used to carry the two ticks. A character that has never had
    // its own takes those once, and they stay in the file for the next character.
    [Fact]
    public void CharacterWithNoChoicesYet_TakesTheRealmsOldOnes_Once()
    {
        WriteSeed(Boss("cyclops", rooms: "7/730"), Boss("chimera", rooms: "3/583"));
        BossStore before = new(seedPath: _seedPath); before.OnRealmChanged(RealmFolder);
        List<BossDef> old = before.Resolve().ToList();
        old.First(b => b.Name == "cyclops").StopBefore = false;     // saved realm-wide, the old way
        before.Save(old);

        BossStore store = new(seedPath: _seedPath); store.OnRealmChanged(RealmFolder);
        Character fujin = new();                                    // Flags null: never set
        fujin.Wire(store);

        Assert.NotNull(fujin.Flags);
        Assert.False(fujin.Flags!["cyclops"].StopBefore);
        Assert.Equal(1, fujin.Saves);
        Assert.False(store.Resolve().First(b => b.Name == "cyclops").StopBefore);

        // Ticking it back on is this character's business only...
        List<BossDef> rows = store.Resolve().ToList();
        rows.First(b => b.Name == "cyclops").StopBefore = true;
        store.Save(rows);
        Assert.Empty(fujin.Flags!);
        store.OnRealmChanged(RealmFolder);                          // a reload doesn't take them again
        Assert.True(store.Resolve().First(b => b.Name == "cyclops").StopBefore);

        // ...and a character loading later still starts from what the realm had.
        BossStore later = new(seedPath: _seedPath); later.OnRealmChanged(RealmFolder);
        Character raijin = new();
        raijin.Wire(later);
        Assert.False(raijin.Flags!["cyclops"].StopBefore);
    }

    // A boss whose own default is off (a Neutral one) needs no stored choice to stay off.
    [Fact]
    public void CharacterFlags_AreStoredOnlyWhereTheyDifferFromTheBosssDefault()
    {
        BossDef cocoon = Boss("cocoon", rooms: "9/1");
        cocoon.DefaultStopBefore = false;
        WriteSeed(cocoon);
        BossStore store = new(seedPath: _seedPath); store.OnRealmChanged(RealmFolder);
        Character fujin = new() { Flags = new() };
        fujin.Wire(store);

        Assert.False(Assert.Single(store.Resolve()).StopBefore);
        store.Save(store.Resolve());
        Assert.Empty(fujin.Flags!);

        List<BossDef> rows = store.Resolve().ToList();
        rows[0].StopBefore = true;
        store.Save(rows);
        Assert.True(fujin.Flags!["cocoon"].StopBefore);
    }

    // The list is keyed by Name, read at startup (the seed) and on realm load: a
    // hand-edited file's null boss or null name is dropped and the rest loads.
    [Fact]
    public void ABossWithNoName_IsDropped_FromTheSeedAndTheRealmsList()
    {
        File.WriteAllText(_seedPath, """[ { "Name": null }, { "Name": "cyclops", "Rooms": [ "7/730" ] }, null ]""");
        Directory.CreateDirectory(RealmFolder);
        File.WriteAllText(AppPaths.RealmBossesFile(RealmFolder), """[ null, { "Name": null }, { "Name": "lich", "Rooms": [ "8/1" ] } ]""");

        BossStore s = new(seedPath: _seedPath);
        s.OnRealmChanged(RealmFolder);

        Assert.Equal(new[] { "cyclops", "lich" }, s.Resolve().Select(b => b.Name).Order());
    }
}
