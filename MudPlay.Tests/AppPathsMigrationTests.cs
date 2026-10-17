using System;
using System.IO;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The one-time legacy "FujinTerm" → MudPlay data migration copies only files the
// destination LACKS and never overwrites — so updating from a 2.x build backfills
// a user's profiles / BBS folders / settings without clobbering anything the new
// install already created. Pins that non-destructive contract.
public sealed class AppPathsMigrationTests : IDisposable
{
    private readonly string _root;

    public AppPathsMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-migrate-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
    }

    private string Src => Path.Combine(_root, "src");
    private string Dst => Path.Combine(_root, "dst");

    [Fact]
    public void CopyMissing_CopiesFilesAndNestedDirs_TheDestinationLacks()
    {
        Directory.CreateDirectory(Path.Combine(Src, "BBS", "Paradigm"));
        File.WriteAllText(Path.Combine(Src, "global.json"), "old-global");
        File.WriteAllText(Path.Combine(Src, "BBS", "Paradigm", "conn.json"), "bbs");

        int copied = AppPaths.CopyMissing(Src, Dst);

        Assert.Equal(2, copied);
        Assert.Equal("old-global", File.ReadAllText(Path.Combine(Dst, "global.json")));
        Assert.Equal("bbs", File.ReadAllText(Path.Combine(Dst, "BBS", "Paradigm", "conn.json")));
    }

    [Fact]
    public void CopyMissing_NeverOverwritesExistingFiles()
    {
        Directory.CreateDirectory(Src);
        Directory.CreateDirectory(Dst);
        File.WriteAllText(Path.Combine(Src, "global.json"), "old");
        File.WriteAllText(Path.Combine(Dst, "global.json"), "new");     // dst already has it
        File.WriteAllText(Path.Combine(Src, "profile.json"), "carry");   // dst lacks it

        int copied = AppPaths.CopyMissing(Src, Dst);

        Assert.Equal(1, copied);                                          // only the missing one
        Assert.Equal("new", File.ReadAllText(Path.Combine(Dst, "global.json")));   // untouched
        Assert.Equal("carry", File.ReadAllText(Path.Combine(Dst, "profile.json")));
    }

    // ----- Data/ subfolder flattening (3.0–3.2 → app-folder root) --------

    // The data root is the app folder itself; a "Data" subfolder sits inside it.
    private string DataRoot => Path.Combine(_root, "MudPlay");
    private string DataSub => Path.Combine(DataRoot, "Data");

    [Fact]
    public void Flatten_LiftsSubfolderContentsUp_AndRemovesData()
    {
        Directory.CreateDirectory(Path.Combine(DataSub, "BBS", "Paradigm"));
        Directory.CreateDirectory(Path.Combine(DataSub, "Global"));
        File.WriteAllText(Path.Combine(DataSub, "Global", "global.json"), "g");
        File.WriteAllText(Path.Combine(DataSub, "BBS", "Paradigm", "bbs.json"), "b");
        File.WriteAllText(Path.Combine(DataSub, ".migrated-from-fujinterm"), "marker");

        AppPaths.FlattenDataSubfolder(DataSub, DataRoot);

        // Contents lifted a level up; the Data/ subfolder is gone.
        Assert.False(Directory.Exists(DataSub));
        Assert.Equal("g", File.ReadAllText(Path.Combine(DataRoot, "Global", "global.json")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(DataRoot, "BBS", "Paradigm", "bbs.json")));
        // Loose files (e.g. the migration marker) come up too.
        Assert.True(File.Exists(Path.Combine(DataRoot, ".migrated-from-fujinterm")));
    }

    [Fact]
    public void Flatten_NoDataSubfolder_IsNoOp()
    {
        Directory.CreateDirectory(DataRoot);
        File.WriteAllText(Path.Combine(DataRoot, "global.json"), "already-flat");

        AppPaths.FlattenDataSubfolder(DataSub, DataRoot);

        // Nothing to lift — the already-flat data is untouched.
        Assert.Equal("already-flat", File.ReadAllText(Path.Combine(DataRoot, "global.json")));
    }

    [Fact]
    public void Flatten_DestinationDirExists_MergesWithoutClobberingNewer()
    {
        // A prior partial run (or the ctor's pre-created empty dirs) left a
        // destination folder in place. The merge must keep the newer flat copy and
        // only backfill what's missing — then still remove the Data/ subfolder.
        Directory.CreateDirectory(Path.Combine(DataSub, "Global"));
        File.WriteAllText(Path.Combine(DataSub, "Global", "global.json"), "old");   // in Data/
        File.WriteAllText(Path.Combine(DataSub, "Global", "extra.json"), "carry");   // only in Data/
        Directory.CreateDirectory(Path.Combine(DataRoot, "Global"));
        File.WriteAllText(Path.Combine(DataRoot, "Global", "global.json"), "new");   // already flat

        AppPaths.FlattenDataSubfolder(DataSub, DataRoot);

        Assert.False(Directory.Exists(DataSub));
        Assert.Equal("new", File.ReadAllText(Path.Combine(DataRoot, "Global", "global.json")));   // newer kept
        Assert.Equal("carry", File.ReadAllText(Path.Combine(DataRoot, "Global", "extra.json")));  // backfilled
    }
}
