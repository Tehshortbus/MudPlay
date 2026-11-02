using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A number in a settings file nobody would set (hand-edited, a bad import, a
// damaged save) is held to its field's range as the file is read, with a Warn, and
// one bad field costs that field only, never the whole file. Read through the
// stores the client reads them with.
[Collection(GlobalSettingsFileCollection.Name)]
public sealed class SettingsValueHardeningTests : IDisposable
{
    private readonly string _bbs = "test-hardening-" + Guid.NewGuid().ToString("N")[..12];
    private readonly List<string> _warns = new();

    public SettingsValueHardeningTests()
    {
        ScratchRoot.Require();
        SettingsValueGuard.Clamped += OnClamped;
    }

    public void Dispose()
    {
        SettingsValueGuard.Clamped -= OnClamped;
        if (Directory.Exists(AppPaths.BbsFolder(_bbs))) Directory.Delete(AppPaths.BbsFolder(_bbs), true);
    }

    private void OnClamped(string line)
    {
        lock (_warns) _warns.Add(line);
    }

    private List<string> WarnsAbout(string path)
    {
        lock (_warns) return _warns.Where(w => w.Contains(path, StringComparison.Ordinal)).ToList();
    }

    private string WriteBbs(string json)
    {
        string path = AppPaths.BbsProfileFile(_bbs);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    private string WriteProfile(string json)
    {
        new BbsProfileStore().Save(new BbsProfile { Name = _bbs });
        string path = AppPaths.CharacterProfileFile(_bbs, "Hero");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    // The fuzz that found it: 100000 columns × 100000 rows overflowed the grid's
    // cols × rows into an allocation of some 34 GB, and two billion scrollback rows
    // failed the ring's array. The loaded values are the Settings window's limits,
    // and the screen built from them stays small.
    [Fact]
    public void BbsLoad_HugeTerminalGeometry_IsHeldToTheWindowsLimits_AndTheScreenStaysSmall()
    {
        string path = WriteBbs($$"""
            { "Name": "{{_bbs}}", "TerminalCols": 100000, "TerminalRows": 100000, "ScrollbackLines": 2147483647 }
            """);

        BbsProfile bbs = new BbsProfileStore().Get(_bbs)!;

        Assert.Equal(200, bbs.TerminalCols);
        Assert.Equal(100, bbs.TerminalRows);
        Assert.Equal(100_000, bbs.ScrollbackLines);
        List<string> warns = WarnsAbout(path);
        Assert.Contains(warns, w => w.Contains("BBS setting TerminalCols", StringComparison.Ordinal)
                                  && w.Contains("was 100000", StringComparison.Ordinal)
                                  && w.Contains("using 200", StringComparison.Ordinal));
        Assert.Contains(warns, w => w.Contains("ScrollbackLines", StringComparison.Ordinal)
                                  && w.Contains("2147483647", StringComparison.Ordinal));

        long before = GC.GetAllocatedBytesForCurrentThread();
        TerminalEmulator emulator = new(80, 25);
        emulator.Resize(bbs.TerminalCols, bbs.TerminalRows);
        emulator.Screen.Scrollback.SetCapacity(bbs.ScrollbackLines);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 16L * 1024 * 1024);
        Assert.Equal((200, 100), (emulator.Screen.Cols, emulator.Screen.Rows));
    }

    // Whatever slips past the load still can't size the grid or the ring past their
    // own bounds.
    [Fact]
    public void Screen_AndScrollback_AskedForTooMuch_StayWithinTheirBounds()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        TerminalEmulator emulator = new(80, 25);
        emulator.Resize(100_000, 100_000);
        emulator.Screen.Scrollback.SetCapacity(int.MaxValue);

        Assert.Equal((TerminalScreen.MaxCols, TerminalScreen.MaxRows), (emulator.Screen.Cols, emulator.Screen.Rows));
        Assert.Equal(ScrollbackBuffer.MaxCapacity, emulator.Screen.Scrollback.Capacity);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 128L * 1024 * 1024);
    }

    // 5.5 columns came up as a sliver of a terminal with nothing said: a fraction in
    // a whole-number field is rounded, then held to the range, and both are warned.
    [Fact]
    public void FractionalTerminalGeometryAndFontSize_LoadRoundedAndClamped_WithAWarn()
    {
        string profilePath = WriteProfile("""
            { "Name": "Hero", "Settings": { "General": { "TerminalFontSize": 5.5, "NavTooltipFontSize": 99 } } }
            """);
        string bbsPath = WriteBbs($$"""
            { "Name": "{{_bbs}}", "TerminalCols": 80.5, "TerminalRows": 5.5 }
            """);

        BbsProfile bbs = new BbsProfileStore().Get(_bbs)!;
        ProfileService profiles = TestProfiles.Service();
        CharacterProfile hero = profiles.Load(_bbs, "Hero");
        GeneralSettings general = hero.Settings!["General"].Deserialize<GeneralSettings>(JsonStore.Options)!;

        Assert.Equal(81, bbs.TerminalCols);
        Assert.Equal(20, bbs.TerminalRows);
        Assert.Equal(8, general.TerminalFontSize);
        Assert.Equal(32, general.NavTooltipFontSize);
        List<string> bbsWarns = WarnsAbout(bbsPath);
        Assert.Contains(bbsWarns, w => w.Contains("TerminalCols", StringComparison.Ordinal) && w.Contains("not a whole number", StringComparison.Ordinal));
        Assert.Contains(bbsWarns, w => w.Contains("TerminalRows", StringComparison.Ordinal) && w.Contains("using 20", StringComparison.Ordinal));
        Assert.Contains(WarnsAbout(profilePath), w => w.Contains("Character setting Settings.General.TerminalFontSize", StringComparison.Ordinal));
    }

    // A number its field can't hold, or text where a number goes, used to fail the
    // whole profile: the client came up on the default profile with the character's
    // workspace missing. Now that field alone takes the value from the tier below.
    [Fact]
    public void ProfileLoad_OneUnreadableField_KeepsTheRestOfTheCharacter()
    {
        string path = WriteProfile("""
            {
              "Name": "Hero",
              "Favorites": [ { "Label": "Home", "Map": 1, "Room": 2 } ],
              "CorpseHandoffs": [ { "Coins": { "Copper": 1e300, "Gold": 3 } } ],
              "LastKnownStats": { "Strength": -2147483648, "Level": 12 },
              "Settings": { "Health": { "RestMaxHp": "lots", "RunIfBelowHp": 33 } }
            }
            """);

        CharacterProfile hero = TestProfiles.Service().Load(_bbs, "Hero");

        Assert.Equal("Hero", hero.Name);
        Assert.Equal("Home", Assert.Single(hero.Favorites!).Label);
        Assert.Equal(int.MaxValue, hero.CorpseHandoffs![0].Coins.Copper);
        Assert.Equal(3, hero.CorpseHandoffs[0].Coins.Gold);
        Assert.Equal(-1_000_000, hero.LastKnownStats!.Strength);
        Assert.Equal(12, hero.LastKnownStats.Level);
        HealthSettings health = hero.Settings!["Health"].Deserialize<HealthSettings>(JsonStore.Options)!;
        Assert.Equal(new HealthSettings().RestMaxHp, health.RestMaxHp);
        Assert.Equal(33, health.RunIfBelowHp);
        List<string> warns = WarnsAbout(path);
        Assert.Contains(warns, w => w.Contains("CorpseHandoffs[0].Coins.Copper", StringComparison.Ordinal));
        Assert.Contains(warns, w => w.Contains("Settings.Health.RestMaxHp", StringComparison.Ordinal)
                                  && w.Contains("the value from the tier below, or the default", StringComparison.Ordinal));
    }

    // The value used is what the next save writes, beside everything else the
    // character held.
    [Fact]
    public void ProfileSave_AfterAClampedLoad_WritesTheCharacterBackWhole()
    {
        string path = WriteProfile("""
            { "Name": "Hero", "Favorites": [ { "Label": "Home", "Map": 1, "Room": 2 } ], "EventQueueLimit": 100000 }
            """);
        ProfileService profiles = TestProfiles.Service();
        profiles.Load(_bbs, "Hero");

        profiles.Save();

        CharacterProfile onFile = JsonSerializer.Deserialize<CharacterProfile>(File.ReadAllText(path), JsonStore.Options)!;
        Assert.Equal("Hero", onFile.Name);
        Assert.Equal("Home", Assert.Single(onFile.Favorites!).Label);
        Assert.Equal(100, onFile.EventQueueLimit);
    }

    // A file that isn't JSON at all still fails to load, and the client comes up on
    // the default profile. Nothing it saves from there may land on the character's
    // file: the unnamed draft is never written, so the file waits to be repaired.
    [Fact]
    public void ProfileThatWontParse_FallingBackToTheDefault_NeverWritesOverIt()
    {
        string path = WriteProfile("""{ "Name": "Hero", "Favorites": [ { "Label": "Home" """);
        byte[] before = File.ReadAllBytes(path);
        ProfileService profiles = TestProfiles.Service();

        Assert.Throws<InvalidDataException>(() => profiles.Load(_bbs, "Hero"));
        profiles.LoadDefaultProfile(_bbs);
        profiles.Save();

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // A value left out is gone from the file at its next save (a migration's, a
    // rename's), so the file as it was is kept beside it first, and the Warn names it.
    [Fact]
    public void ProfileLoad_LeavingAValueOut_KeepsTheFileAsItWas_BeforeASaveCanLoseIt()
    {
        string path = WriteProfile("""
            { "Name": "Hero", "Favorites": { "Label": "Home" }, "GotoHistory": ["1/2"] }
            """);
        byte[] original = File.ReadAllBytes(path);
        ProfileService profiles = TestProfiles.Service();

        profiles.Load(_bbs, "Hero");
        profiles.Save();

        string backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "profile.json.unreadable-*.bak"));
        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.Contains(WarnsAbout(path), w => w.Contains("Favorites", StringComparison.Ordinal)
                                             && w.Contains(Path.GetFileName(backup), StringComparison.Ordinal));
        Assert.Equal("1/2", Assert.Single(profiles.Current!.GotoHistory!));
    }

    // Reading the same damaged file again makes no new copy, and a file damaged
    // over and over keeps only the newest few: five states inside one second, each
    // read twice, leave the last three, in the order they were made.
    [Fact]
    public void Backups_AreOnePerState_AndTheNewestFewAreKept_EvenWithinOneSecond()
    {
        string path = WriteProfile("""{ "Name": "Hero" }""");
        string folder = Path.GetDirectoryName(path)!;

        DamageFiveTimes(path);

        Assert.Equal(new[] { 3, 4, 5 }, StatesKept(folder));
    }

    // A copy stamped ahead of the clock (the clock set back since it was made) is
    // the newest: the copies after it carry its stamp, counted on from it, so all
    // five share one stamp, as five made inside one millisecond do, and they still
    // sort, and are pruned, in the order they were made.
    [Fact]
    public void Backups_StayInTheOrderMade_WhenTheClockIsBehindTheNewestCopy()
    {
        string path = WriteProfile("""{ "Name": "Hero" }""");
        string folder = Path.GetDirectoryName(path)!;
        File.WriteAllText(Path.Combine(folder, "profile.json.unreadable-99991231-235959999Z-00.bak"), "made before the clock went back");

        DamageFiveTimes(path);

        Assert.Equal(new[] { 3, 4, 5 }, StatesKept(folder));
        Assert.All(Directory.GetFiles(folder, "profile.json.unreadable-*.bak"),
            k => Assert.StartsWith("profile.json.unreadable-99991231-235959999Z-", Path.GetFileName(k), StringComparison.Ordinal));
    }

    // Five states of the profile, each read twice.
    private void DamageFiveTimes(string path)
    {
        for (int state = 1; state <= 5; state++)
        {
            File.WriteAllText(path, $$"""{ "Name": "Hero", "Favorites": {{state}} }""");
            TestProfiles.Service().Load(_bbs, "Hero");
            TestProfiles.Service().Load(_bbs, "Hero");
        }
    }

    // The state each kept copy holds (0 for none of DamageFiveTimes'), in name order.
    private static int[] StatesKept(string folder) =>
        Directory.GetFiles(folder, "profile.json.unreadable-*.bak").Order(StringComparer.Ordinal)
            .Select(File.ReadAllText)
            .Select(t => Enumerable.Range(1, 5).FirstOrDefault(s => t.Contains($"\"Favorites\": {s} ", StringComparison.Ordinal)))
            .ToArray();

    // A copy of the character is the character, not its rescued damaged files.
    [Fact]
    public void CopyProfile_LeavesTheBackupsBehind()
    {
        WriteProfile("""{ "Name": "Hero", "Favorites": 1 }""");
        ProfileService profiles = TestProfiles.Service();
        profiles.Load(_bbs, "Hero");

        profiles.CopyProfile(_bbs, "Hero", "Hero2");

        Assert.Single(Directory.GetFiles(AppPaths.ProfileFolder(_bbs, "Hero"), "*.unreadable-*.bak"));
        Assert.Empty(Directory.GetFiles(AppPaths.ProfileFolder(_bbs, "Hero2"), "*.bak"));
    }

    // A context menu's entries hold entries: the walk goes all the way down, so a bad
    // value two levels in is found in a file that otherwise reads as it stands.
    [Fact]
    public void ProfileLoad_ABadValueDeepInARecursiveSection_IsFound()
    {
        string path = WriteProfile("""
            { "Name": "Hero", "Settings": { "ContextMenu": { "Layout": [
                { "Kind": "Entry", "Id": "a" },
                { "Kind": "Folder", "Label": "More", "Children": [ { "Kind": 99, "Id": "b" } ] } ] } } }
            """);

        CharacterProfile hero = TestProfiles.Service().Load(_bbs, "Hero");

        JsonElement child = hero.Settings!["ContextMenu"].GetProperty("Layout")[1].GetProperty("Children")[0];
        Assert.False(child.TryGetProperty("Kind", out _));
        Assert.Equal("b", child.GetProperty("Id").GetString());
        Assert.Contains(WarnsAbout(path), w => w.Contains("Settings.ContextMenu.Layout[1].Children[0].Kind", StringComparison.Ordinal));
    }

    // Structs are walked too: a player's customisation is one, and its remote
    // controls are flags. An undefined bit goes back to the default; an enum
    // number in a list is left out of it.
    [Fact]
    public void ProfileLoad_UndefinedEnumsInStructsAndLists_AreCaught()
    {
        string path = WriteProfile("""
            { "Name": "Hero",
              "PlayerCustomizations": { "Bob": { "RemoteControls": 1048576, "Notes": "keep" } },
              "CombatProfiles": { "SharedGroups": [ 99 ] } }
            """);

        CharacterProfile hero = TestProfiles.Service().Load(_bbs, "Hero");

        Models.GameData.PlayerCustomization bob = hero.PlayerCustomizations!["Bob"];
        Assert.Equal(Models.GameData.PlayerRemoteControls.None, bob.RemoteControls);
        Assert.Equal("keep", bob.Notes);
        Assert.Empty(hero.CombatProfiles!.SharedGroups!);
        List<string> warns = WarnsAbout(path);
        Assert.Contains(warns, w => w.Contains("PlayerCustomizations[Bob].RemoteControls", StringComparison.Ordinal));
        Assert.Contains(warns, w => w.Contains("CombatProfiles.SharedGroups[0]", StringComparison.Ordinal));
    }

    // A section past saving: left as written for its own reader, said once, and the
    // rest of the profile still loads.
    [Fact]
    public void ProfileLoad_ASectionBeyondRepair_IsLeftAsWritten_AndTheProfileLoads()
    {
        string bad = string.Join(", ", Enumerable.Range(0, 120).Select(i => $"\"x{i}\": 1"));
        string path = WriteProfile($$"""
            { "Name": "Hero", "GotoHistory": ["1/2"],
              "Settings": { "Spells": { "BlessSlots": { {{bad}} } }, "Talk": { "LogMaxLines": 5 } } }
            """);

        CharacterProfile hero = TestProfiles.Service().Load(_bbs, "Hero");

        Assert.Equal("1/2", Assert.Single(hero.GotoHistory!));
        Assert.Contains("x119", hero.Settings!["Spells"].GetRawText(), StringComparison.Ordinal);
        Assert.Equal(100, hero.Settings["Talk"].GetProperty("LogMaxLines").GetInt32());
        Assert.Contains(WarnsAbout(path), w => w.Contains("Settings.Spells", StringComparison.Ordinal)
                                             && w.Contains("the section as written", StringComparison.Ordinal));
    }

    // A key written twice is read as the serializer reads it, the last standing, and
    // the file is still checked.
    [Fact]
    public void ProfileLoad_AKeyWrittenTwice_StillHasItsValuesChecked()
    {
        WriteProfile("""
            { "Name": "Hero", "EventQueueLimit": 5, "EventQueueLimit": 100000, "Favorites": 1 }
            """);

        CharacterProfile hero = TestProfiles.Service().Load(_bbs, "Hero");

        Assert.Equal(100, hero.EventQueueLimit);
        Assert.Null(hero.Favorites);
    }

    // An enum number the enum doesn't have: a section's is left out, so the tier
    // below shows through; a file's own goes back to its default.
    [Fact]
    public void EnumNumbersItDoesntHave_AreLeftOut_WithAWarn()
    {
        string path = WriteProfile("""
            { "Name": "Hero", "NavLairMode": 99, "Settings": { "Health": { "HpThresholdMode": 7, "RunIfBelowHp": 33 } } }
            """);

        CharacterProfile hero = TestProfiles.Service().Load(_bbs, "Hero");

        Assert.Equal(new CharacterProfile().NavLairMode, hero.NavLairMode);
        Assert.False(hero.Settings!["Health"].TryGetProperty("HpThresholdMode", out _));
        Assert.Equal(33, hero.Settings["Health"].GetProperty("RunIfBelowHp").GetInt32());
        List<string> warns = WarnsAbout(path);
        Assert.Contains(warns, w => w.Contains("NavLairMode", StringComparison.Ordinal));
        Assert.Contains(warns, w => w.Contains("Settings.Health.HpThresholdMode", StringComparison.Ordinal));
    }

    // A read says only what the last read of the same file didn't: rereading is
    // quiet, and a value fixed and then broken again is said again.
    [Fact]
    public void Warns_AreSaidOncePerFileState()
    {
        string path = WriteBbs($$"""{ "Name": "{{_bbs}}", "TerminalCols": 5000 }""");
        BbsProfileStore store = new();

        store.Get(_bbs);
        store.Get(_bbs);
        Assert.Single(WarnsAbout(path), w => w.Contains("TerminalCols", StringComparison.Ordinal));

        File.WriteAllText(path, $$"""{ "Name": "{{_bbs}}", "TerminalCols": 80 }""");
        store.Get(_bbs);
        File.WriteAllText(path, $$"""{ "Name": "{{_bbs}}", "TerminalCols": 5000 }""");
        store.Get(_bbs);
        Assert.Equal(2, WarnsAbout(path).Count(w => w.Contains("TerminalCols", StringComparison.Ordinal)));
    }

    // Global tier: two billion days of player cleanup threw "AddDays out of range"
    // from the startup prune, and a section value at that tier reaches every read.
    [Fact]
    public void GlobalLoad_OutOfRangeValues_AreClamped_AndThePruneCopes()
    {
        string file = AppPaths.GlobalSettingsFile;
        string? saved = File.Exists(file) ? File.ReadAllText(file) : null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, """
                { "PlayerCleanupDays": 2147483647, "LocalApiPort": 5, "Settings": { "Talk": { "LogMaxLines": 1e12 } } }
                """);

            SettingsService settings = new();
            ProfileService profiles = TestProfiles.Loaded();
            SettingsResolver resolver = new(settings, new BbsProfileStore(), profiles);

            Assert.Equal(3650, settings.Current.PlayerCleanupDays);
            Assert.Equal(1024, settings.Current.LocalApiPort);
            Assert.Equal(100_000, resolver.Resolve<TalkSettings>("Talk").LogMaxLines);
            Assert.Contains(WarnsAbout(file), w => w.StartsWith("Global setting PlayerCleanupDays", StringComparison.Ordinal));
            PlayerDatabase players = new(profiles, () => null);
            Assert.Equal(0, players.PurgeStale(settings.Current.PlayerCleanupDays, DateTime.UtcNow));
        }
        finally
        {
            if (saved is null) File.Delete(file);
            else File.WriteAllText(file, saved);
        }
    }

    // Whatever window a caller passes, the prune never throws.
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(800_000)]
    public void PurgeStale_AnyWindow_DoesNotThrow(int days)
    {
        PlayerDatabase players = new(TestProfiles.Loaded(), () => null);

        Assert.Equal(0, players.PurgeStale(days, DateTime.UtcNow));
        Assert.Equal(0, players.PurgeStale(days, new DateTime(2, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    // The serializer leaves a null where a file writes one, or where it leaves a
    // record's field out, whatever the field promises, and the code reading it threw
    // far from the file: the trigger list keys on Name. Each such field gets its
    // default, and a null entry in a list or map is left out.
    [Fact]
    public void ProfileLoad_NullsWhereAValueGoes_GetDefaults_AndNullEntriesAreLeftOut()
    {
        string path = WriteProfile("""
            {
              "Name": "Hero",
              "Triggers": [ { "Enabled": true, "Pattern": "A bell rings." }, null ],
              "WindowBounds": { "Settings": null, "Map": { "X": 1, "Y": 2, "Width": 300, "Height": 200 } }
            }
            """);

        CharacterProfile hero = TestProfiles.Service().Load(_bbs, "Hero");

        Models.GameData.Trigger trigger = Assert.Single(hero.Triggers!);
        Assert.Equal(("", "A bell rings.", ""), (trigger.Name, trigger.Pattern, trigger.Response));
        Assert.Equal(Models.GameData.TriggerLevel.Character, TriggerEngine.Nearest(hero.Triggers!)[""]);
        Assert.Equal("Map", Assert.Single(hero.WindowBounds!).Key);
        List<string> warns = WarnsAbout(path);
        Assert.Contains(warns, w => w.Contains("setting Triggers[0].Name in", StringComparison.Ordinal)
                                  && w.Contains("was null, which it can't be; using the default", StringComparison.Ordinal));
        Assert.Contains(warns, w => w.Contains("setting WindowBounds[Settings] in", StringComparison.Ordinal)
                                  && w.Contains("nothing (left out)", StringComparison.Ordinal));
    }

    // A board's Host is matched against the built-in boards at startup, and every
    // BBS has a realm.
    [Fact]
    public void BbsLoad_NullHostAndRealms_LoadAsABlankHostAndAFirstRealm()
    {
        WriteBbs($$"""{ "Name": "{{_bbs}}", "Host": null, "Realms": null }""");

        BbsProfile bbs = new BbsProfileStore().Get(_bbs)!;

        Assert.Equal("", bbs.Host);
        Assert.Single(bbs.Realms);
        Assert.DoesNotContain(BuiltInBbses.All, preset => preset.IsBoardOf(bbs));
    }

    [Fact]
    public void GlobalLoad_NullBoardAndRecentLists_LoadAsLists()
    {
        string file = AppPaths.GlobalSettingsFile;
        string? saved = File.Exists(file) ? File.ReadAllText(file) : null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, """
                { "BuiltInBbsesAdded": null, "RecentProfiles": [ null, { "Bbs": "Board", "Name": "Hero" } ] }
                """);

            SettingsService settings = new();

            Assert.Empty(settings.Current.BuiltInBbsesAdded);
            Assert.Equal("Hero", Assert.Single(settings.Current.RecentProfiles!).Name);
        }
        finally
        {
            if (saved is null) File.Delete(file);
            else File.WriteAllText(file, saved);
        }
    }
}

// Every number a settings section or file holds has a range, and the range never
// takes away a value the Settings window lets the user set.
public sealed class SettingRangeCoverageTests
{
    // The DTOs a user configures: the sections, the three files' own settings and
    // the settings objects they hold. Recorded state (stats, observations, death
    // and quest records) is held only to what its type can hold.
    private static readonly Type[] SettingsTypes = SettingsValueGuard.Sections.Values.Concat(new[]
    {
        typeof(GlobalSettings), typeof(BbsProfile), typeof(RealmProfile), typeof(NavLineStyle), typeof(GuideProgress),
        typeof(EncumbranceHopTimes), typeof(CombatSpellSlot), typeof(PvpSpellSlot), typeof(SoundCueSettings),
        typeof(BuffSlot), typeof(EquipmentSettings), typeof(CombatSpellProfile), typeof(CombatProfileSpells),
        typeof(CombatProfileParty), typeof(MonsterHitColorSettings), typeof(Models.GameData.ScheduledEvent),
        typeof(SessionStatsLayout), typeof(WindowBounds), typeof(CpPlanEntry),
    }).Distinct().ToArray();

    private static bool IsNumber(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t.IsGenericType && t.GetGenericTypeDefinition() is var g && (g == typeof(List<>) || g == typeof(Dictionary<,>)))
            t = t.GetGenericArguments()[^1];
        return t == typeof(int) || t == typeof(long) || t == typeof(double);
    }

    [Fact]
    public void EveryNumberASettingsTypeHolds_HasARange()
    {
        List<string> missing = SettingsTypes
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && IsNumber(p.PropertyType) && p.Name != "SchemaVersion"
                            && p.GetCustomAttribute<SettingRangeAttribute>() is null)
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        Assert.Empty(missing);
    }

    // A default outside its own range would be moved, and warned about, on every load.
    [Fact]
    public void EveryDefault_LiesInItsRange()
    {
        List<string> outside = new();
        foreach (Type type in typeof(SettingRangeAttribute).Assembly.GetTypes())
        {
            PropertyInfo[] ranged = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<SettingRangeAttribute>() is not null).ToArray();
            if (ranged.Length == 0 || type.GetConstructor(Type.EmptyTypes) is null) continue;
            object instance = Activator.CreateInstance(type)!;
            foreach (PropertyInfo p in ranged)
            {
                SettingRangeAttribute range = p.GetCustomAttribute<SettingRangeAttribute>()!;
                Assert.True(range.Min <= range.Max, $"{type.Name}.{p.Name}");
                if (p.GetValue(instance) is not { } value || value is System.Collections.IEnumerable) continue;
                double d = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                if (d < range.Min || d > range.Max) outside.Add($"{type.Name}.{p.Name} = {d}");
            }
        }

        Assert.Empty(outside);
    }

    // Each NumericUpDown in the Settings windows bound to a field of the same name:
    // whatever it lets the user enter, the load keeps.
    [Fact]
    public void EveryRange_HoldsWhatTheSettingsWindowAllows()
    {
        string views = Path.Combine(RepoRoot(), "Views");
        IEnumerable<string> files = Directory.GetFiles(Path.Combine(views, "Settings"), "*.axaml")
            .Append(Path.Combine(views, "AddBuffDialog.axaml"))
            .Append(Path.Combine(views, "CharacterWorkshop", "EquipmentSectionView.axaml"));
        Dictionary<string, List<(string Owner, SettingRangeAttribute Range)>> ranged = new(StringComparer.Ordinal);
        foreach (Type type in typeof(SettingRangeAttribute).Assembly.GetTypes())
            foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (p.GetCustomAttribute<SettingRangeAttribute>() is { } r)
                    (ranged.TryGetValue(p.Name, out var list) ? list : ranged[p.Name] = new()).Add(($"{type.Name}.{p.Name}", r));

        Regex control = new(@"<NumericUpDown\b(?<attrs>[^>]*)>", RegexOptions.Singleline);
        int compared = 0;
        List<string> narrower = new();
        foreach (string file in files)
            foreach (Match m in control.Matches(File.ReadAllText(file)))
            {
                string attrs = m.Groups["attrs"].Value;
                if (Regex.Match(attrs, @"Value=""\{Binding (\w+)") is not { Success: true } bound) continue;
                if (!ranged.TryGetValue(bound.Groups[1].Value, out var owners)) continue;
                double? min = Literal(attrs, "Minimum"), max = Literal(attrs, "Maximum");
                foreach ((string owner, SettingRangeAttribute range) in owners)
                {
                    compared++;
                    if (min is { } lo && lo < range.Min) narrower.Add($"{owner}: window allows {lo}, range starts {range.Min}");
                    if (max is { } hi && hi > range.Max) narrower.Add($"{owner}: window allows {hi}, range ends {range.Max}");
                }
            }

        Assert.Empty(narrower);
        Assert.True(compared > 50, $"only {compared} controls matched a ranged field");
    }

    private static double? Literal(string attrs, string name) =>
        Regex.Match(attrs, name + @"=""(-?[0-9.]+)""") is { Success: true } m
            ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(here)!)!;
}
