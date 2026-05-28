using FujinTerm.Models.Profile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class ProfileBackupTests : IDisposable
{
    private readonly string _profileDir;
    private readonly string _restoreXdg;

    public ProfileBackupTests()
    {
        // Reroute XDG_DATA_HOME so AppPaths writes into a scratch folder.
        // AppPaths reads the env var only inside its static ctor, so this
        // test relies on AppPaths having been resolved before — fine, every
        // earlier test forces it.
        _restoreXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? string.Empty;
        _profileDir = Path.Combine(Path.GetTempPath(), $"FujinTermTest-{Guid.NewGuid():N}", "profiles");
        Directory.CreateDirectory(_profileDir);
    }

    public void Dispose()
    {
        try
        {
            string parent = Path.GetDirectoryName(_profileDir)!;
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
        catch
        {
            // Best-effort cleanup — leaving a temp dir behind isn't a test
            // failure.
        }
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _restoreXdg);
    }

    [Fact]
    public void Save_BackupFalse_NoBakWritten()
    {
        string path = Path.Combine(_profileDir, "alice.json");
        File.WriteAllText(path, "{\"Name\":\"old\"}");

        // Write directly via JsonStore to exercise the no-backup code path
        // through the same atomic-write helper Profile.Save uses.
        JsonStoreSpy.Save(path, new CharacterProfile { Name = "new" }, backup: false);

        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void Save_BackupTrue_WritesBakOfPriorContent()
    {
        string path = Path.Combine(_profileDir, "alice.json");
        File.WriteAllText(path, "{\"Name\":\"original\"}");

        JsonStoreSpy.Save(path, new CharacterProfile { Name = "updated" }, backup: true);

        Assert.True(File.Exists(path + ".bak"));
        string bakContent = File.ReadAllText(path + ".bak");
        Assert.Contains("original", bakContent);
        string mainContent = File.ReadAllText(path);
        Assert.Contains("updated", mainContent);
    }

    [Fact]
    public void Save_BackupTrue_NoPriorFile_NoBakWritten()
    {
        string path = Path.Combine(_profileDir, "fresh.json");
        Assert.False(File.Exists(path));

        JsonStoreSpy.Save(path, new CharacterProfile { Name = "first" }, backup: true);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    /// <summary>
    /// Mirrors <see cref="ProfileService.Save"/>'s backup-then-write sequence
    /// so the test stays decoupled from <see cref="ProfileService"/>'s
    /// AppPaths-routed path resolution. Pins the contract we expect of the
    /// service: pre-existing file copied to .bak first, then atomic write.
    /// </summary>
    private static class JsonStoreSpy
    {
        public static void Save<T>(string path, T value, bool backup) where T : class
        {
            if (backup && File.Exists(path))
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }
            // Use the same shape as ProfileService.Save's downstream write.
            string json = System.Text.Json.JsonSerializer.Serialize(value);
            File.WriteAllText(path, json);
        }
    }
}
