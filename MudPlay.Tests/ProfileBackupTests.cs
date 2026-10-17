using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// ProfileService.Save's "Backup profile when making changes" path: with backup on,
// the file as it stood is copied to profile.json.bak before the new one is written.
public sealed class ProfileBackupTests : IDisposable
{
    private readonly string _bbs = "test-backupbbs-" + Guid.NewGuid().ToString("N").Substring(0, 12);

    public void Dispose()
    {
        try
        {
            string folder = AppPaths.BbsFolder(_bbs);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch { /* best-effort */ }
    }

    private (ProfileService Profile, string Path) Loaded(string name)
    {
        ScratchRoot.Require();
        ProfileService profile = new();
        profile.CreateProfile(_bbs, name);
        profile.Load(_bbs, name);
        return (profile, AppPaths.CharacterProfileFile(_bbs, name));
    }

    [Fact]
    public void Save_BackupFalse_NoBakWritten()
    {
        (ProfileService profile, string path) = Loaded("Alice");

        profile.Save(backup: false);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void Save_BackupTrue_WritesBakOfPriorContent()
    {
        (ProfileService profile, string path) = Loaded("Alice");
        string before = File.ReadAllText(path);
        profile.Current!.PendingReconnectLeader = "after the change";

        profile.Save(backup: true);

        Assert.Equal(before, File.ReadAllText(path + ".bak"));
        Assert.Contains("after the change", File.ReadAllText(path));
    }

    [Fact]
    public void Save_BackupTrue_NoPriorFile_NoBakWritten()
    {
        (ProfileService profile, string path) = Loaded("Fresh");
        File.Delete(path);

        profile.Save(backup: true);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".bak"));
    }
}
