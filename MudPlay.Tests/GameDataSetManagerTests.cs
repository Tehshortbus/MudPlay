using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// GameDataSetManager copy / move / delete
// operations over the shared per-set loop library. Isolated via unique
// GUID-suffixed set names under AppPaths.GameDataRoot
// (AppPaths caches its root at static-init, so we can't sandbox it) —
// Dispose deletes them so nothing leaks into the user's real Data/ tree.
public sealed class GameDataSetManagerTests : IDisposable
{
    private readonly List<string> _createdSets = new();

    public void Dispose()
    {
        foreach (string set in _createdSets)
        {
            try
            {
                string dir = Path.Combine(AppPaths.GameDataRoot, set);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { /* best-effort */ }
        }
        foreach (string file in _createdFiles)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch { /* best-effort */ }
        }
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(AppPaths.BbsDir, "test-bbs-*"))
                Directory.Delete(dir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ----- fixture ---------------------------------------------------

    private string NewSetName()
    {
        string name = "test-gdset-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        _createdSets.Add(name);
        return name;
    }

    // Create the set directory so it counts as "exists".
    private string CreateSet()
    {
        string name = NewSetName();
        Directory.CreateDirectory(AppPaths.GameDataSetDir(name));
        return name;
    }

    // Drop a file into the set's shared Loops/ folder.
    private static void SeedLoop(string setName, string fileName, string content = "x")
    {
        string loops = AppPaths.GameDataSetLoopsFolder(setName);
        Directory.CreateDirectory(loops);
        File.WriteAllText(Path.Combine(loops, fileName), content);
    }

    private static GameDataSetManager NewManager(
        GameDataCache cache, Action? reload = null, Action<string>? onDeleted = null,
        Action<string>? onReplaced = null) =>
        new(cache, _ => reload?.Invoke(), onDeleted ?? (_ => { }), onSetDataReplaced: onReplaced);

    // ----- Copy ------------------------------------------------------

    [Fact]
    public void CopyLoops_CopiesFilesAndLeavesSourceIntact()
    {
        GameDataCache cache = new();
        string src = CreateSet();
        string dst = CreateSet();
        SeedLoop(src, "a.loop");
        SeedLoop(src, "b.lair");

        GameDataSetManager.OpResult result = NewManager(cache).Copy(src, dst, GameDataSetPart.Loops);

        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Combine(AppPaths.GameDataSetLoopsFolder(dst), "a.loop")));
        Assert.True(File.Exists(Path.Combine(AppPaths.GameDataSetLoopsFolder(dst), "b.lair")));
        // Source untouched on a copy.
        Assert.True(File.Exists(Path.Combine(AppPaths.GameDataSetLoopsFolder(src), "a.loop")));
    }

    [Fact]
    public void CopyLoops_PreservesNestedSubdirectories()
    {
        GameDataCache cache = new();
        string src = CreateSet();
        string dst = CreateSet();
        string nested = Path.Combine(AppPaths.GameDataSetLoopsFolder(src), "Town", "Inner");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "deep.loop"), "x");

        GameDataSetManager.OpResult result = NewManager(cache).Copy(src, dst, GameDataSetPart.Loops);

        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Combine(
            AppPaths.GameDataSetLoopsFolder(dst), "Town", "Inner", "deep.loop")));
    }

    // ----- Move ------------------------------------------------------

    [Fact]
    public void MoveLoops_MovesFilesAndRemovesSourceLibrary()
    {
        GameDataCache cache = new();
        string src = CreateSet();
        string dst = CreateSet();
        SeedLoop(src, "a.loop");

        GameDataSetManager.OpResult result = NewManager(cache).Move(src, dst, GameDataSetPart.Loops);

        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Combine(AppPaths.GameDataSetLoopsFolder(dst), "a.loop")));
        // Source loop library removed entirely on a move.
        Assert.False(Directory.Exists(AppPaths.GameDataSetLoopsFolder(src)));
    }

    // ----- validation guards ----------------------------------------

    [Fact]
    public void CopyLoops_SameSet_Fails()
    {
        GameDataCache cache = new();
        string s = CreateSet();
        SeedLoop(s, "a.loop");

        GameDataSetManager.OpResult result = NewManager(cache).Copy(s, s, GameDataSetPart.Loops);

        Assert.False(result.Ok);
    }

    [Fact]
    public void CopyLoops_EmptySource_Fails()
    {
        GameDataCache cache = new();
        string dst = CreateSet();

        GameDataSetManager.OpResult result = NewManager(cache).Copy("", dst, GameDataSetPart.Loops);

        Assert.False(result.Ok);
    }

    [Fact]
    public void CopyLoops_MissingDestination_Fails()
    {
        GameDataCache cache = new();
        string src = CreateSet();
        SeedLoop(src, "a.loop");
        string ghost = NewSetName(); // never created on disk

        GameDataSetManager.OpResult result = NewManager(cache).Copy(src, ghost, GameDataSetPart.Loops);

        Assert.False(result.Ok);
    }

    [Fact]
    public void CopyLoops_SourceHasNoLoops_Fails()
    {
        GameDataCache cache = new();
        string src = CreateSet(); // exists but empty Loops/
        string dst = CreateSet();

        GameDataSetManager.OpResult result = NewManager(cache).Copy(src, dst, GameDataSetPart.Loops);

        Assert.False(result.Ok);
    }

    // ----- active-set reload ----------------------------------------

    [Fact]
    public void CopyLoops_IntoActiveSet_FiresReload()
    {
        GameDataCache cache = new();
        string src = CreateSet();
        string dst = CreateSet();
        SeedLoop(src, "a.loop");
        cache.SwitchSet(dst);

        int reloads = 0;
        NewManager(cache, reload: () => reloads++).Copy(src, dst, GameDataSetPart.Loops);

        Assert.Equal(1, reloads);
    }

    [Fact]
    public void CopyLoops_UnrelatedSets_DoesNotReload()
    {
        GameDataCache cache = new();
        string src = CreateSet();
        string dst = CreateSet();
        string other = CreateSet();
        SeedLoop(src, "a.loop");
        cache.SwitchSet(other);

        int reloads = 0;
        NewManager(cache, reload: () => reloads++).Copy(src, dst, GameDataSetPart.Loops);

        Assert.Equal(0, reloads);
    }

    // ----- Picking what goes across ----------------------------------

    private readonly List<string> _createdFiles = new();

    // An override side-file for a set, beside a tier: Global, or a folder under BBS/.
    private string SeedOverride(string folder, string table, string setName, string content = "{}")
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"{table}_overrides.{setName}.json");
        File.WriteAllText(path, content);
        _createdFiles.Add(path);
        return path;
    }

    private static string GlobalFolder => Path.Combine(AppPaths.DataRoot, "Global");

    [Fact]
    public void Copy_OnlyTheTickedParts_GoAcross_AndReplaceTheDestinations()
    {
        GameDataCache cache = new();
        string src = CreateSet();
        string dst = CreateSet();
        SeedLoop(src, "a.loop");
        File.WriteAllText(AppPaths.MessagesFile(src), "old messages");
        File.WriteAllText(AppPaths.FlavorPrefixesFile(src), "old prefixes");
        File.WriteAllText(AppPaths.MessagesFile(dst), "the new set's messages");

        GameDataSetManager.OpResult result = NewManager(cache)
            .Copy(src, dst, GameDataSetPart.Messages | GameDataSetPart.RecordOverrides);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("old messages", File.ReadAllText(AppPaths.MessagesFile(dst)));
        Assert.Equal("old prefixes", File.ReadAllText(AppPaths.FlavorPrefixesFile(dst)));
        Assert.False(Directory.Exists(AppPaths.GameDataSetLoopsFolder(dst)));   // loops weren't ticked
        Assert.True(File.Exists(AppPaths.MessagesFile(src)));                   // a copy keeps the source
    }

    [Fact]
    public void Move_Messages_TakesAllThreeFilesAndRemovesThemFromTheSource()
    {
        GameDataCache cache = new();
        string src = CreateSet();
        string dst = CreateSet();
        File.WriteAllText(AppPaths.MessagesFile(src), "m");
        File.WriteAllText(AppPaths.MonsterMessagesFile(src), "mm");
        File.WriteAllText(AppPaths.FlavorPrefixesFile(src), "fp");

        GameDataSetManager.OpResult result = NewManager(cache).Move(src, dst, GameDataSetPart.Messages);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("m", File.ReadAllText(AppPaths.MessagesFile(dst)));
        Assert.Equal("mm", File.ReadAllText(AppPaths.MonsterMessagesFile(dst)));
        Assert.Equal("fp", File.ReadAllText(AppPaths.FlavorPrefixesFile(dst)));
        Assert.False(File.Exists(AppPaths.MessagesFile(src)));
        Assert.False(File.Exists(AppPaths.MonsterMessagesFile(src)));
        Assert.False(File.Exists(AppPaths.FlavorPrefixesFile(src)));
    }

    // Record overrides sit beside each tier, named for the set. A set whose name
    // only begins with the source's ("x" and "x (room commands)") is another set.
    [Fact]
    public void Copy_RecordOverrides_RenamesEveryTiersFileForTheDestination()
    {
        GameDataCache cache = new();
        string src = CreateSet();
        string dst = CreateSet();
        string realmFolder = Path.Combine(AppPaths.BbsDir, "test-bbs-" + src, "realm");
        string profileFolder = Path.Combine(realmFolder, "profiles", "char");
        SeedOverride(GlobalFolder, "items", src, "global items");
        SeedOverride(realmFolder, "monsters", src, "realm monsters");
        SeedOverride(profileFolder, "items", src, "char items");
        string other = SeedOverride(GlobalFolder, "items", src + " (room commands)", "another set");

        GameDataSetManager manager = NewManager(cache);
        Assert.Equal(3, manager.FileCount(src, GameDataSetPart.RecordOverrides));
        GameDataSetManager.OpResult result = manager.Copy(src, dst, GameDataSetPart.RecordOverrides);

        Assert.True(result.Ok, result.Message);
        string global = Path.Combine(GlobalFolder, $"items_overrides.{dst}.json");
        string realm = Path.Combine(realmFolder, $"monsters_overrides.{dst}.json");
        string character = Path.Combine(profileFolder, $"items_overrides.{dst}.json");
        _createdFiles.AddRange(new[] { global, realm, character });
        Assert.Equal("global items", File.ReadAllText(global));
        Assert.Equal("realm monsters", File.ReadAllText(realm));
        Assert.Equal("char items", File.ReadAllText(character));
        Assert.Equal("another set", File.ReadAllText(other));
        Assert.False(File.Exists(Path.Combine(GlobalFolder, $"items_overrides.{dst} (room commands).json")));
    }

    [Fact]
    public void Copy_NothingTicked_Fails()
    {
        GameDataCache cache = new();
        string src = CreateSet();
        string dst = CreateSet();
        SeedLoop(src, "a.loop");

        Assert.False(NewManager(cache).Copy(src, dst, GameDataSetPart.None).Ok);
    }

    // The reload is told what changed, and a ticked part the source has none of is
    // named in the result rather than failing the rest.
    [Fact]
    public void Copy_IntoActiveSet_ReloadsWhatChanged_AndNamesWhatWasMissing()
    {
        GameDataCache cache = new();
        string src = CreateSet();
        string dst = CreateSet();
        File.WriteAllText(AppPaths.MessagesFile(src), "m");
        cache.SwitchSet(dst);

        GameDataSetPart changed = GameDataSetPart.None;
        GameDataSetManager manager = new(cache, c => changed = c, _ => { });
        GameDataSetManager.OpResult result =
            manager.Copy(src, dst, GameDataSetPart.Messages | GameDataSetPart.RecordOverrides);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(GameDataSetPart.Messages, changed);
        Assert.Contains("Nothing there for: Game Data Browser edits", result.Message);
    }

    // ----- Delete ----------------------------------------------------

    [Fact]
    public void DeleteSet_RemovesFolderFromDisk()
    {
        GameDataCache cache = new();
        string set = CreateSet();
        SeedLoop(set, "a.loop");

        GameDataSetManager.OpResult result = NewManager(cache).DeleteSet(set);

        Assert.True(result.Ok);
        Assert.False(Directory.Exists(AppPaths.GameDataSetDir(set)));
    }

    [Fact]
    public void DeleteSet_FiresOnSetDeletedCallback()
    {
        GameDataCache cache = new();
        string set = CreateSet();

        string? deleted = null;
        NewManager(cache, onDeleted: s => deleted = s).DeleteSet(set);

        Assert.Equal(set, deleted);
    }

    [Fact]
    public void DeleteSet_OfActiveSet_SwitchesCacheToNull()
    {
        GameDataCache cache = new();
        string set = CreateSet();
        cache.SwitchSet(set);
        Assert.Equal(set, cache.ActiveSet);

        NewManager(cache).DeleteSet(set);

        Assert.Null(cache.ActiveSet);
    }

    [Fact]
    public void DeleteSet_NonActiveSet_LeavesActiveUnchanged()
    {
        GameDataCache cache = new();
        string active = CreateSet();
        string doomed = CreateSet();
        cache.SwitchSet(active);

        NewManager(cache).DeleteSet(doomed);

        Assert.Equal(active, cache.ActiveSet);
    }

    [Fact]
    public void DeleteSet_MissingSet_FailsAndDoesNotFireCallback()
    {
        GameDataCache cache = new();
        string ghost = NewSetName(); // never created

        bool fired = false;
        GameDataSetManager.OpResult result =
            NewManager(cache, onDeleted: _ => fired = true).DeleteSet(ghost);

        Assert.False(result.Ok);
        Assert.False(fired);
    }

    // ----- Naming the sets whose data an op replaced ------------------

    [Fact]
    public void Copy_OfEdits_NamesTheDestinationAsReplaced_ActiveOrNot()
    {
        GameDataCache cache = new();
        string src = CreateSet();
        string dst = CreateSet();
        File.WriteAllText(AppPaths.MessagesFile(src), "m");
        SeedOverride(GlobalFolder, "Monsters", src);
        _createdFiles.Add(Path.Combine(GlobalFolder, $"Monsters_overrides.{dst}.json"));
        var replaced = new List<string>();

        NewManager(cache, onReplaced: replaced.Add)
            .Copy(src, dst, GameDataSetPart.Messages | GameDataSetPart.RecordOverrides);

        Assert.Equal(new[] { dst }, replaced);
    }

    [Fact]
    public void Move_OfEdits_NamesBothSetsAsReplaced()
    {
        GameDataCache cache = new();
        string src = CreateSet();
        string dst = CreateSet();
        File.WriteAllText(AppPaths.MessagesFile(src), "m");
        var replaced = new List<string>();

        NewManager(cache, onReplaced: replaced.Add).Move(src, dst, GameDataSetPart.Messages);

        Assert.Equal(new[] { dst, src }, replaced);
    }

    [Fact]
    public void CopyOrMove_OfTheLoopLibraryAlone_NamesNoSetAsReplaced()
    {
        GameDataCache cache = new();
        string src = CreateSet();
        string dst = CreateSet();
        SeedLoop(src, "a.loop");
        var replaced = new List<string>();
        GameDataSetManager manager = NewManager(cache, onReplaced: replaced.Add);

        Assert.True(manager.Copy(src, dst, GameDataSetPart.Loops).Ok);
        Assert.True(manager.Move(src, dst, GameDataSetPart.Loops).Ok);

        Assert.Empty(replaced);
    }

    [Fact]
    public void DeleteSet_NamesItAsReplaced_SoALaterSetOfThatNameStartsClean()
    {
        GameDataCache cache = new();
        string kept = CreateSet();
        string doomed = CreateSet();
        cache.SwitchSet(kept);
        var replaced = new List<string>();

        NewManager(cache, onReplaced: replaced.Add).DeleteSet(doomed);

        Assert.Equal(new[] { doomed }, replaced);
    }
}
