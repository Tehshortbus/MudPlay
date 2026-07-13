using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FujinTerm.Models.Profile;
using FujinTerm.Models.Settings;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Renaming a BBS must move its whole on-disk subtree — bbs.json AND every
/// nested character profile — and re-key each profile's per-BBS credentials
/// (keyed by BBS name). The old Delete+Save rename recursively destroyed the
/// nested profiles and left every reference to the old name dangling, breaking
/// logon-nav / passwords and showing the dead name in the import picker.
/// </summary>
public sealed class BbsRenameCascadeTests : IDisposable
{
    private readonly string _oldBbs;
    private readonly string _newBbs;

    public BbsRenameCascadeTests()
    {
        string tag = Path.GetRandomFileName();
        _oldBbs = "rename-test-old-" + tag;
        _newBbs = "rename-test-new-" + tag;
    }

    public void Dispose()
    {
        foreach (string bbs in new[] { _oldBbs, _newBbs })
        {
            try
            {
                string folder = AppPaths.BbsFolder(bbs);
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    private void SeedBbs(string name) =>
        new BbsProfileStore().Save(new BbsProfile { Name = name, Host = "example.org", Port = 23 });

    private void SeedProfile(string bbs, string character, string credentialKey)
    {
        var profile = new CharacterProfile
        {
            Name = character,
            BbsCredentials = new Dictionary<string, BbsCredentials>(System.StringComparer.OrdinalIgnoreCase)
            {
                [credentialKey] = new BbsCredentials
                {
                    EncryptedUsername = "enc-user",
                    MenuNavSteps = { new MenuStep() },
                },
            },
        };
        string path = AppPaths.CharacterProfileFile(bbs, character);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonStore.Save(path, profile);
    }

    private static CharacterProfile LoadProfile(string bbs, string character) =>
        JsonStore.Load<CharacterProfile>(AppPaths.CharacterProfileFile(bbs, character))!;

    [Fact]
    public void Rename_MovesFolder_PreservesNestedProfiles_AndRewritesName()
    {
        SeedBbs(_oldBbs);
        SeedProfile(_oldBbs, "Fujin", _oldBbs);

        new BbsProfileStore().Rename(_oldBbs, _newBbs);

        // Old folder is gone; new folder carries the moved profile.
        Assert.False(Directory.Exists(AppPaths.BbsFolder(_oldBbs)));
        Assert.True(File.Exists(AppPaths.CharacterProfileFile(_newBbs, "Fujin")));

        // The moved bbs.json now names the new BBS.
        BbsProfile? moved = new BbsProfileStore().Get(_newBbs);
        Assert.NotNull(moved);
        Assert.Equal(_newBbs, moved!.Name);
    }

    [Fact]
    public void Rename_Throws_WhenDestinationExists()
    {
        SeedBbs(_oldBbs);
        SeedBbs(_newBbs);
        Assert.Throws<IOException>(() => new BbsProfileStore().Rename(_oldBbs, _newBbs));
    }

    [Fact]
    public void RenameBbs_RekeysCredentials_OnUnloadedProfiles()
    {
        SeedBbs(_oldBbs);
        SeedProfile(_oldBbs, "Fujin", _oldBbs);
        // Move the subtree first, exactly as the Settings Apply path does.
        new BbsProfileStore().Rename(_oldBbs, _newBbs);

        // No profile loaded → the on-disk file is re-keyed in place.
        new ProfileService().RenameBbs(_oldBbs, _newBbs);

        CharacterProfile after = LoadProfile(_newBbs, "Fujin");
        Assert.NotNull(after.BbsCredentials);
        Assert.False(after.BbsCredentials!.ContainsKey(_oldBbs));
        Assert.True(after.BbsCredentials.ContainsKey(_newBbs));
        Assert.Equal("enc-user", after.BbsCredentials[_newBbs].EncryptedUsername);
    }

    [Fact]
    public void RenameBbs_UpdatesLoadedProfile_CurrentBbsName_AndInMemoryCredentials()
    {
        SeedBbs(_oldBbs);
        SeedProfile(_oldBbs, "Fujin", _oldBbs);

        var service = new ProfileService();
        service.Load(_oldBbs, "Fujin");
        Assert.Equal(_oldBbs, service.CurrentBbsName);

        // Move the folder, then cascade — mirrors RenameSelected's order.
        new BbsProfileStore().Rename(_oldBbs, _newBbs);
        service.RenameBbs(_oldBbs, _newBbs);

        // Live session follows the rename.
        Assert.Equal(_newBbs, service.CurrentBbsName);
        Assert.NotNull(service.Current!.BbsCredentials);
        Assert.False(service.Current.BbsCredentials!.ContainsKey(_oldBbs));
        Assert.True(service.Current.BbsCredentials.ContainsKey(_newBbs));

        // And the change is flushed to the profile's new-location file.
        CharacterProfile onDisk = LoadProfile(_newBbs, "Fujin");
        Assert.True(onDisk.BbsCredentials!.ContainsKey(_newBbs));
        Assert.False(onDisk.BbsCredentials.ContainsKey(_oldBbs));
    }
}
