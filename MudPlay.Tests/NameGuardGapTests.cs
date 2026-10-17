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

// What stands behind the name checks for the names that still got through: the
// folder a refused name is sent to, a profile's set name that can't be a folder, the
// segments of a navigation folder.
[Collection(ScratchRoot.NamesCollection)]
public sealed class NameGuardGapTests : IDisposable
{
    private readonly string _tag = Path.GetRandomFileName().Replace(".", "")[..8];
    private readonly string _jail = Path.Combine(Path.GetTempPath(), "mudplay-nameguard-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _made = new();
    private readonly ProfileService _profiles = new();
    private readonly BbsProfileStore _bbs = new();

    private string Bbs => $"ng{_tag}";
    private string Set => $"ngset{_tag}";

    public NameGuardGapTests()
    {
        Directory.CreateDirectory(_jail);
    }

    public void Dispose()
    {
        try { Directory.Delete(_jail, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        foreach (string path in _made)
        {
            // Only ever what this test made, inside the scratch root.
            if (!ScratchRoot.Holds(path)) continue;
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (IOException) { /* best-effort cleanup of a scratch folder */ }
        }
    }

    private string Made(string path)
    {
        _made.Add(path);
        return path;
    }

    // ----- The folder a refused name is sent to ---------

    [Fact]
    public void TheHoldingFolderForRefusedNames_IsListedAsNoSetBbsOrProfile_WhateverItHolds()
    {
        ScratchRoot.Require();
        string refused = AppPaths.RefusedName;
        _bbs.Save(new BbsProfile { Name = Bbs, Host = "h" });
        _profiles.CreateProfile(Bbs, "kept");
        Made(Path.Combine(AppPaths.BbsDir, Bbs));
        Made(Directory.CreateDirectory(Path.Combine(AppPaths.GameDataRoot, Set)).FullName);

        // Each looks like one of the thing it would be listed as.
        string heldSet = Made(Path.Combine(AppPaths.GameDataRoot, refused));
        Directory.CreateDirectory(heldSet);
        File.WriteAllText(Path.Combine(heldSet, "Rooms.json"), "[]");
        string heldBbs = Made(Path.Combine(AppPaths.BbsDir, refused));
        Directory.CreateDirectory(Path.Combine(heldBbs, "profiles", "stray"));
        File.WriteAllText(AppPaths.BbsProfileFile(refused), "{}");
        File.WriteAllText(AppPaths.CharacterProfileFile(refused, "stray"), "{}");
        string heldProfile = Path.Combine(AppPaths.BbsProfilesDir(Bbs), refused);
        Directory.CreateDirectory(heldProfile);
        File.WriteAllText(AppPaths.CharacterProfileFile(Bbs, refused), "{}");

        Assert.DoesNotContain(refused, new GameDataCache(AppPaths.GameDataRoot).AvailableSets);
        Assert.Contains(Set, new GameDataCache(AppPaths.GameDataRoot).AvailableSets);
        Assert.DoesNotContain(refused, _bbs.ListNames());
        Assert.Contains(Bbs, _bbs.ListNames());
        List<ProfileRef> listed = _profiles.ListAll().ToList();
        Assert.DoesNotContain(listed, r => r.Bbs == refused || r.Name == refused);
        Assert.Contains(new ProfileRef(Bbs, "kept"), listed);
    }

    // ----- A profile's set name that can't be a folder ---------

    [Theory]
    [InlineData("..")]
    [InlineData("../elsewhere")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:x")]
    [InlineData("C:\\x")]
    [InlineData("/abs")]
    [InlineData("nul")]
    [InlineData("trailing.")]
    public void ASetNameThatCannotBeAFolder_IsNoSet_AndTheReasonIsGiven(string name)
    {
        ScratchRoot.Require();
        var cache = new GameDataCache(AppPaths.GameDataRoot);

        string? set = cache.UsableSet(name, out string? whyNot);

        Assert.Null(set);
        Assert.False(string.IsNullOrWhiteSpace(whyNot));
        Assert.Equal(PathGuard.WhyNotPlain(name, "game-data set name") ?? whyNot, whyNot);
    }

    [Fact]
    public void AnOrdinaryOrBlankSetName_IsKeptAsItIs()
    {
        ScratchRoot.Require();
        var cache = new GameDataCache(AppPaths.GameDataRoot);

        foreach (string name in new[] { Set, "data-v1.11p", "Hills - Caves (L40 sim)" })
        {
            Assert.Equal(name, cache.UsableSet(name, out string? whyNot));
            Assert.Null(whyNot);
        }
        foreach (string? blank in new[] { null, "", "  " })
        {
            Assert.Equal(blank, cache.UsableSet(blank, out string? whyNot));
            Assert.Null(whyNot);
        }
    }

    [Fact]
    public void AnOddSetNameAlreadyOnDisk_IsStillASet()
    {
        ScratchRoot.Require();
        // One path segment on this platform, and not a name to give anything.
        if (OperatingSystem.IsWindows()) return;
        string odd = $"odd:{_tag}.";
        Directory.CreateDirectory(Made(Path.Combine(AppPaths.GameDataRoot, odd)));

        Assert.Equal(odd, new GameDataCache(AppPaths.GameDataRoot).UsableSet(odd, out string? whyNot));
        Assert.Null(whyNot);
    }

    // ----- Navigation folder segments ---------

    private static string Old(string root, string folder) =>
        NavFolders.Segments(folder) is { Length: > 0 } segs ? Path.Combine(root, Path.Combine(segs)) : root;

    [Theory]
    [InlineData("Cities")]
    [InlineData("Cities/Silvermere")]
    [InlineData("Cities/Silvermere Keep/Upper Level")]
    [InlineData("  Spaced  /  out  ")]
    [InlineData("a//b/")]
    [InlineData("data-v1.11p (room commands)/Hills - Caves (L40 sim)")]
    [InlineData("")]
    [InlineData(null)]
    public void AnOrdinaryNavFolder_IsTheSameDirectoryAsBefore(string? folder)
    {
        string root = Path.Combine(_jail, "Loops");

        Assert.Equal(Old(root, folder ?? ""), NavFolders.ToDirectory(root, folder));
    }

    [Theory]
    [InlineData("C:")]
    [InlineData("C:/x")]
    [InlineData("x/C:/y")]
    [InlineData("C:\\Windows")]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("x/../../y")]
    [InlineData("/abs/olute")]
    [InlineData("\\\\server\\share")]
    [InlineData("a\\..\\..\\b")]
    public void ANavFolderOfAnyName_NeverLeavesTheRoot(string folder)
    {
        string root = Path.Combine(_jail, "Loops");

        string dir = NavFolders.ToDirectory(root, folder);

        Assert.True(dir == root || PathGuard.IsInside(dir, root), $"{folder} gave {dir}");
    }

    [Theory]
    [InlineData("C:")]
    [InlineData("C:x")]
    [InlineData("c:")]
    [InlineData("Z:")]
    public void ADriveStyleSegment_IsNotAPlainName_SoWindowsHasNoDriveToTakeItFor(string segment)
    {
        // The verdict PathGuard gives on every platform: the colon is the drive's, and
        // Child (which Windows runs the segment through) refuses what leaves the folder.
        Assert.False(PathGuard.IsPlainName(segment));
        Assert.Contains("':'", PathGuard.WhyNotPlain(segment));
        Assert.False(PathGuard.Usable(_jail, segment));
    }

    [Fact]
    public void ASegmentThatIsNoFolderOfItsParent_GivesAPathWhereNothingIs_NotTheRoot()
    {
        string root = Path.Combine(_jail, "Loops");
        string said = "";
        void Hear(string line) { if (line.Contains($"bad{_tag}")) said = line; }
        PathGuard.Refused += Hear;
        try
        {
            string dir = NavFolders.ToDirectory(root, $"ok/bad{_tag}\0x/deeper");

            Assert.Equal(Path.Combine(root, AppPaths.RefusedName), dir);
            Assert.NotEqual(root, dir);
            Assert.False(Directory.Exists(dir));
        }
        finally { PathGuard.Refused -= Hear; }
        Assert.StartsWith("A navigation folder refused: ", said);
    }
}
