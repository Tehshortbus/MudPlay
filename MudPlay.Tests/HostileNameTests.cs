using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A BBS, realm, profile or game-data set name becomes a folder name, and several of
// the operations that take one delete or move that folder with everything in it. A
// name is typed by the user, read from global.json or a bbs.json, or passed on the
// command line, so it is not to be trusted: ".." as a BBS name pointed the BBS
// store's recursive delete at the data root.
//
// Every operation that builds a path from such a name is run here with each kind of
// hostile name, and whatever the operation says of it (some throw, some return),
// nothing on disk may have changed: not where the name points, not in the folders
// around it, and no folder of that odd name made where a plain one would go.
//
// These run against the data root the test run was given and nothing else: the
// constructor refuses any root that did not come from MUDPLAY_DATA_ROOT. No name
// here reaches further up than that root, so a build without the guard damages only
// the scratch folder.
[Collection(ScratchRoot.NamesCollection)]
public sealed class HostileNameTests : IDisposable
{
    private readonly string _tag = Path.GetRandomFileName().Replace(".", "")[..8];
    private readonly List<string> _made = new();

    private readonly ProfileService _profiles = TestProfiles.Service();
    private readonly BbsProfileStore _bbs = new();

    private string Bbs => $"hn{_tag}";
    private string Sibling => $"hn{_tag}-sibling";
    private string Set => $"hnset{_tag}";
    private string Set2 => $"hnset{_tag}-two";
    private const string Kept = "kept";
    private const string Mover = "mover";
    private const string Realm = "home";

    private string ProfilesDir => Path.Combine(AppPaths.BbsDir, Bbs, "profiles");
    private string RealmsDir => Path.Combine(AppPaths.BbsDir, Bbs, "Realms");

    public HostileNameTests()
    {
        ScratchRoot.Require();

        _bbs.Save(new BbsProfile { Name = Bbs, Host = "h", Realms = { new RealmProfile { Name = Realm } } });
        _bbs.Save(new BbsProfile { Name = Sibling, Host = "h" });
        _profiles.CreateProfile(Bbs, Kept);
        _profiles.CreateProfile(Bbs, Mover);
        _profiles.CreateProfile(Sibling, Kept);
        Directory.CreateDirectory(Path.Combine(RealmsDir, Realm));
        foreach (string set in new[] { Set, Set2 })
        {
            Directory.CreateDirectory(Path.Combine(AppPaths.GameDataRoot, set, "Loops"));
            File.WriteAllText(Path.Combine(AppPaths.GameDataRoot, set, "Rooms.json"), "[]");
            File.WriteAllText(Path.Combine(AppPaths.GameDataRoot, set, "Loops", "a.loop"), "{}");
        }
        foreach (string canary in Canaries) File.WriteAllText(canary, "canary " + _tag);
        _made.AddRange(Fixtures);
        _made.AddRange(Canaries.Take(3));
    }

    public void Dispose()
    {
        foreach (string path in _made)
        {
            // Only ever something of this test's, by its tag, inside the scratch root.
            if (!path.Contains(_tag, StringComparison.Ordinal) || !ScratchRoot.Holds(path)) continue;
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { /* best-effort cleanup of a scratch folder */ }
        }
    }

    // What this test owns outright, each a folder nothing else in the run touches.
    private string[] Fixtures => new[]
    {
        Path.Combine(AppPaths.BbsDir, Bbs),
        Path.Combine(AppPaths.BbsDir, Sibling),
        Path.Combine(AppPaths.GameDataRoot, Set),
        Path.Combine(AppPaths.GameDataRoot, Set2),
    };

    // A file beside or above every folder an operation here is meant to work in. The
    // first three sit in folders the whole test run shares.
    private string[] Canaries => new[]
    {
        Path.Combine(AppPaths.DataRoot, $"hn-canary-{_tag}.txt"),
        Path.Combine(AppPaths.BbsDir, $"hn-canary-{_tag}.txt"),
        Path.Combine(AppPaths.GameDataRoot, $"hn-canary-{_tag}.txt"),
        Path.Combine(AppPaths.BbsDir, Bbs, "canary.txt"),
        Path.Combine(ProfilesDir, "canary.txt"),
        Path.Combine(ProfilesDir, Kept, "canary.txt"),
        Path.Combine(RealmsDir, "canary.txt"),
        Path.Combine(RealmsDir, Realm, "canary.txt"),
        Path.Combine(AppPaths.BbsDir, Sibling, "canary.txt"),
        Path.Combine(AppPaths.GameDataRoot, Set, "canary.txt"),
        Path.Combine(AppPaths.GameDataRoot, Set2, "canary.txt"),
    };

    // ----- The names ---------

    public static readonly string[] Kinds =
    {
        "dotdot", "dot", "up-and-over", "nested", "backslash", "absolute", "drive", "trailing-dot",
        "trailing-space", "empty", "spaces", "control", "device",
    };

    private string Hostile(string kind) => kind switch
    {
        "dotdot" => "..",
        "dot" => ".",
        "up-and-over" => $"../esc{_tag}",
        "nested" => $"n{_tag}/inner",
        "backslash" => $"b{_tag}\\inner",
        // Absolute, and still inside the scratch root: a build without the guard
        // must have nothing to reach but that.
        "absolute" => Path.Combine(Path.GetFullPath(AppPaths.DataRoot), $"abs{_tag}"),
        "drive" => $"C:d{_tag}",
        "trailing-dot" => $"t{_tag}.",
        "trailing-space" => $"s{_tag} ",
        "empty" => "",
        "spaces" => "   ",
        "control" => $"c{_tag}\u0001x",
        "device" => $"LPT1.{_tag}",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // ----- The operations ---------

    // Which folder the name is meant to name a child of.
    private enum Under { Profiles, BbsDir, Realms, Sets }

    private sealed record Op(string Id, Under Parent, Action<HostileNameTests, string> Run);

    private static readonly Op[] Ops =
    {
        // A profile's name.
        new("Profile.Load(bbs, H)", Under.Profiles, (t, h) => t._profiles.Load(t.Bbs, h)),
        new("Profile.SaveAs(bbs, H)", Under.Profiles, (t, h) => { t._profiles.LoadDefaultProfile(); t._profiles.SaveAs(t.Bbs, h); }),
        new("Profile.CreateProfile(bbs, H)", Under.Profiles, (t, h) => t._profiles.CreateProfile(t.Bbs, h)),
        new("Profile.CopyProfile(bbs, kept, H)", Under.Profiles, (t, h) => t._profiles.CopyProfile(t.Bbs, Kept, h)),
        new("Profile.CopyProfile(bbs, H, new)", Under.Profiles, (t, h) => t._profiles.CopyProfile(t.Bbs, h, "copied")),
        new("Profile.MoveProfile(bbs, mover, bbs, H)", Under.Profiles, (t, h) => t._profiles.MoveProfile(t.Bbs, Mover, t.Bbs, h)),
        new("Profile.MoveProfile(bbs, H, bbs, new)", Under.Profiles, (t, h) => t._profiles.MoveProfile(t.Bbs, h, t.Bbs, "moved")),
        new("Profile.ReHome(bbs, H) of the loaded profile", Under.Profiles, (t, h) => t._profiles.ReHome(t.Bbs, h)),
        new("Profile.DeleteProfile(bbs, H)", Under.Profiles, (t, h) => t._profiles.DeleteProfile(t.Bbs, h)),
        new("Profile.Exists(bbs, H)", Under.Profiles, (t, h) => t._profiles.Exists(t.Bbs, h)),
        new("Profile.RealmOf(bbs, H)", Under.Profiles, (t, h) => t._profiles.RealmOf(new ProfileRef(t.Bbs, h))),
        new("Profile.AssignRealm(bbs, H)", Under.Profiles, (t, h) => t._profiles.AssignRealm(new ProfileRef(t.Bbs, h), Realm)),

        // A BBS's name, to the profile store.
        new("Profile.Load(H, name)", Under.BbsDir, (t, h) => t._profiles.Load(h, Kept)),
        new("Profile.SaveAs(H, name)", Under.BbsDir, (t, h) => { t._profiles.LoadDefaultProfile(); t._profiles.SaveAs(h, $"p{t._tag}"); }),
        new("Profile.CreateProfile(H, name)", Under.BbsDir, (t, h) => t._profiles.CreateProfile(h, $"p{t._tag}")),
        new("Profile.CopyProfile(H, kept, new)", Under.BbsDir, (t, h) => t._profiles.CopyProfile(h, Kept, $"p{t._tag}")),
        new("Profile.MoveProfile(bbs, mover, H, name)", Under.BbsDir, (t, h) => t._profiles.MoveProfile(t.Bbs, Mover, h, $"p{t._tag}")),
        new("Profile.MoveProfile(H, name, bbs, new)", Under.BbsDir, (t, h) => t._profiles.MoveProfile(h, Kept, t.Bbs, "moved")),
        new("Profile.ReHome(H) of the loaded profile", Under.BbsDir, (t, h) => t._profiles.ReHome(h)),
        new("Profile.DeleteProfile(H, name)", Under.BbsDir, (t, h) => t._profiles.DeleteProfile(h, Kept)),
        new("Profile.Exists(H, name)", Under.BbsDir, (t, h) => t._profiles.Exists(h, Kept)),
        new("Profile.AssignRealm(H, name)", Under.BbsDir, (t, h) => t._profiles.AssignRealm(new ProfileRef(h, Kept), Realm)),
        new("Profile.RenameBbs(bbs, H)", Under.BbsDir, (t, h) => t._profiles.RenameBbs(t.Bbs, h)),
        new("Profile.RenameRealm(H, old, new)", Under.BbsDir, (t, h) => t._profiles.RenameRealm(h, Realm, "other")),

        // A BBS's name, to the BBS store.
        new("Bbs.Get(H)", Under.BbsDir, (t, h) => t._bbs.Get(h)),
        new("Bbs.Save(H)", Under.BbsDir, (t, h) => t._bbs.Save(new BbsProfile { Name = h, Host = "h" })),
        new("Bbs.Exists(H)", Under.BbsDir, (t, h) => t._bbs.Exists(h)),
        new("Bbs.Delete(H)", Under.BbsDir, (t, h) => t._bbs.Delete(h)),
        new("Bbs.Rename(sibling, H)", Under.BbsDir, (t, h) => t._bbs.Rename(t.Sibling, h)),
        new("Bbs.Rename(H, new)", Under.BbsDir, (t, h) => t._bbs.Rename(h, $"renamed{t._tag}")),

        // A realm's name.
        new("Realm.MoveData(bbs, home, H)", Under.Realms, (t, h) => t.Realms().MoveData(t.Bbs, Realm, h)),
        new("Realm.MoveData(bbs, H, new)", Under.Realms, (t, h) => t.Realms().MoveData(t.Bbs, h, "moved")),
        new("Realm.DeleteContents(bbs, H)", Under.Realms, (t, h) => t.Realms().DeleteContents(t.Bbs, h, Array.Empty<ProfileRef>())),
        new("Realm.Rename(bbs, home, H)", Under.Realms, (t, h) => t.Realms().Rename(t.Bbs, Realm, h)),
        new("Resolver: a realm-tier override with realm H of the loaded profile", Under.Realms, (t, h) => t.WriteRealmOverride(h)),

        // A game-data set's name.
        new("Sets.DeleteSet(H)", Under.Sets, (t, h) => t.Sets().DeleteSet(h)),
        new("Sets.Copy(H, set2, everything)", Under.Sets, (t, h) => t.Sets().Copy(h, t.Set2, Everything)),
        new("Sets.Copy(set, H, everything)", Under.Sets, (t, h) => t.Sets().Copy(t.Set, h, Everything)),
        new("Sets.Move(H, set2, everything)", Under.Sets, (t, h) => t.Sets().Move(h, t.Set2, Everything)),
        new("Sets.Move(set, H, everything)", Under.Sets, (t, h) => t.Sets().Move(t.Set, h, Everything)),
        new("Sets.FileCount(H)", Under.Sets, (t, h) => t.Sets().FileCount(h, GameDataSetPart.Loops)),
        new("Importer.ImportAsync(file, H)", Under.Sets, (t, h) => t.Import(h)),
        new("Cache.SwitchSet(H) and a table read", Under.Sets, (t, h) => { var cache = new GameDataCache(AppPaths.GameDataRoot); cache.SwitchSet(h); cache.GetTable("Rooms"); }),
        new("Loops.LoadAll(H) and a loop saved", Under.Sets, (t, h) => t.SaveALoopOn(h)),
    };

    private const GameDataSetPart Everything =
        GameDataSetPart.Loops | GameDataSetPart.Messages | GameDataSetPart.RecordOverrides;

    private RealmCatalog Realms() => new(_bbs, _profiles);

    private GameDataSetManager Sets() => new(new GameDataCache(AppPaths.GameDataRoot), _ => { }, _ => { });

    private void Import(string target)
    {
        // The importer makes the target folder before it reads the file, so a file
        // that is no database is enough to see where it would have written.
        string file = Path.Combine(AppPaths.GameDataRoot, Set, "not-a-database.mdb");
        File.WriteAllText(file, "x");
        try { new MdbImporter().ImportAsync(file, target).GetAwaiter().GetResult(); }
        finally { File.Delete(file); }
    }

    // A bbs.json can name a realm anything. The BBS is read with realm H in it and
    // its file put back as it was before anything is compared; then a game-data
    // override is written "only for this realm", and read back.
    private void WriteRealmOverride(string realm)
    {
        string file = Path.Combine(AppPaths.BbsDir, Bbs, "bbs.json");
        byte[] was = File.ReadAllBytes(file);
        SettingsResolver resolver;
        try
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(was)!.AsObject();
            json["Realms"]![0]!["Name"] = realm;
            File.WriteAllText(file, json.ToJsonString());
            _profiles.Current!.Realm = realm;
            resolver = new SettingsResolver(new SettingsService(), new BbsProfileStore(), _profiles, () => Set);
        }
        finally { File.WriteAllBytes(file, was); }
        // A one-segment name on this platform is a realm folder of that name, kept
        // working like any realm made before the rule; only the others are tested.
        if (PathGuard.Child(RealmsDir, realm) is not null) return;
        resolver.ResolveGameData("Monsters", "1", new Models.GameData.MonsterOverlay());
        resolver.WriteGameDataAt(SettingsTier.Bbs, "Monsters", "1", new Models.GameData.MonsterOverlay { Name = "x" });
    }

    private void SaveALoopOn(string set)
    {
        var cache = new GameDataCache(AppPaths.GameDataRoot);
        var graph = new RoomGraphManager(cache);
        var loops = new LoopManager(new BfsMapper(graph), graph);
        loops.LoadAll(set);
        loops.Save(new Loop($"loop{_tag}", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
    }

    // Every operation with every kind of name, but one: a realm is renamed by what
    // was typed with the spaces around it taken off, so a name that ends in a space
    // is, to that operation, the plain name before the space.
    public static IEnumerable<object[]> Cases() =>
        from op in Ops from kind in Kinds
        where !(op.Id.StartsWith("Realm.Rename", StringComparison.Ordinal) && kind == "trailing-space")
        select new object[] { op.Id, kind };

    // ----- What may not change ---------

    private string ParentOf(Under parent) => parent switch
    {
        Under.Profiles => ProfilesDir,
        Under.BbsDir => AppPaths.BbsDir,
        Under.Realms => RealmsDir,
        _ => AppPaths.GameDataRoot,
    };

    // Folders of the whole run: too busy to list, watched by their canaries and by
    // the places a name could have made something in them.
    private static bool IsShared(string full)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.DataRoot));
        string path = Path.TrimEndingDirectorySeparator(full);
        return root.StartsWith(path, StringComparison.Ordinal)   // the root or above it
            || path == Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.BbsDir))
            || path == Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.GameDataRoot));
    }

    // Where a build that joins the name on unchecked would have gone, and what it
    // would have made there.
    private IEnumerable<string> Reach(Under parent, string hostile)
    {
        string target = Path.GetFullPath(Path.Combine(ParentOf(parent), hostile));
        yield return target;
        foreach (string child in new[] { "profiles", "bbs.json", "profile.json", "Realms", "Loops", "Rooms.json", "Emotes" })
            yield return Path.Combine(target, child);
        yield return Path.Combine(target, "profiles", $"p{_tag}");
        // Where AppPaths sends a name it refuses: a read may look there, and
        // nothing may ever be made there.
        yield return Path.Combine(ParentOf(parent), ".refused-name");
        yield return Path.Combine(ParentOf(parent), "copied");
        yield return Path.Combine(ParentOf(parent), "moved");
        yield return Path.Combine(AppPaths.BbsDir, $"renamed{_tag}");
    }

    private SortedDictionary<string, string> Snapshot(IEnumerable<string> reach)
    {
        var state = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Note(string path)
        {
            if (File.Exists(path)) state[path] = "file " + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))[..12];
            else if (Directory.Exists(path)) state[path] = "folder";
            else state[path] = "absent";
        }
        void Tree(string path)
        {
            Note(path);
            if (!Directory.Exists(path) || IsShared(Path.GetFullPath(path))) return;
            foreach (string entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)) Note(entry);
        }
        foreach (string fixture in Fixtures) Tree(fixture);
        foreach (string canary in Canaries) Note(canary);
        foreach (string path in reach) Tree(path);
        return state;
    }

    private static List<string> Differences(SortedDictionary<string, string> before, SortedDictionary<string, string> after) =>
        before.Keys.Union(after.Keys).Order(StringComparer.Ordinal)
            .Where(p => before.GetValueOrDefault(p, "absent") != after.GetValueOrDefault(p, "absent"))
            .Select(p => $"{p}: {before.GetValueOrDefault(p, "absent")} -> {after.GetValueOrDefault(p, "absent")}")
            .ToList();

    [Theory]
    [MemberData(nameof(Cases))]
    public void AHostileNameChangesNothingOnDisk(string opId, string kind)
    {
        Op op = Ops.Single(o => o.Id == opId);
        string hostile = Hostile(kind);
        // The operations on the loaded profile need one loaded; that is not the name
        // under test, and it is in place before anything is compared.
        if (opId.Contains("of the loaded profile")) _profiles.Load(Bbs, Mover);
        string[] reach = Reach(op.Parent, hostile).ToArray();
        _made.AddRange(reach);
        SortedDictionary<string, string> before = Snapshot(reach);

        Record.Exception(() => op.Run(this, hostile));

        List<string> changed = Differences(before, Snapshot(reach));
        // Two ways to fail, told apart for whoever reads the failure. A name that is
        // one path segment on this platform (a backslash or a colon in it on Linux)
        // makes a folder of that odd name where a plain one would go, and an
        // operation that moves something there empties where it came from: wrong,
        // and inside the folder it was meant for. Any other change went somewhere
        // the name had no business pointing.
        string asNamed = Path.Combine(ParentOf(op.Parent), hostile);
        bool oneSegment = hostile.Length > 0 && hostile is not ("." or "..")
            && !hostile.Contains('/') && !Path.IsPathRooted(hostile);
        string[] itsOwn =
        {
            asNamed, Path.Combine(ProfilesDir, Mover), Path.Combine(AppPaths.BbsDir, Sibling),
            Path.Combine(RealmsDir, Realm), Path.Combine(AppPaths.BbsDir, Bbs, "bbs.json"),
        };
        List<string> elsewhere = changed
            .Where(c => !(oneSegment && itsOwn.Any(own => c.StartsWith(own, StringComparison.Ordinal)))).ToList();
        Assert.True(elsewhere.Count == 0,
            $"ESCAPED: {opId} with {kind} name {Show(hostile)} changed what it should not reach:\n  " + string.Join("\n  ", elsewhere));
        Assert.True(changed.Count == 0,
            $"TOOK THE NAME: {opId} with {kind} name {Show(hostile)} made or changed:\n  " + string.Join("\n  ", changed));
    }

    private static string Show(string name) =>
        "\"" + string.Concat(name.Select(c => char.IsControl(c) ? $"\\u{(int)c:x4}" : c.ToString())) + "\"";

    // ----- Names that never become the loaded profile ---------

    [Theory]
    [InlineData("dotdot")]
    [InlineData("up-and-over")]
    [InlineData("nested")]
    public void AProfileFilePlantedWhereAHostileNamePoints_IsNotLoaded(string kind)
    {
        string hostile = Hostile(kind);
        // A profile.json where the unchecked join lands: without the guard it loads,
        // and from then on every save of the session goes there.
        string planted = Path.GetFullPath(Path.Combine(ProfilesDir, hostile, "profile.json"));
        _made.Add(Path.GetDirectoryName(planted)!);
        Directory.CreateDirectory(Path.GetDirectoryName(planted)!);
        File.Copy(Path.Combine(ProfilesDir, Kept, "profile.json"), planted, overwrite: true);

        Record.Exception(() => _profiles.Load(Bbs, hostile));

        Assert.NotEqual(hostile, _profiles.CurrentProfileName);
    }

    [Theory]
    [MemberData(nameof(KindRows))]
    public void TheDefaultProfileIsNotStartedOnAHostileBbs(string kind)
    {
        string hostile = Hostile(kind);

        _profiles.LoadDefaultProfile(onBbs: hostile);

        Assert.Null(_profiles.CurrentBbsName);
        Assert.NotNull(_profiles.Current);
    }

    public static IEnumerable<object[]> KindRows() => Kinds.Select(k => new object[] { k });
}
