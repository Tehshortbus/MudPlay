using System.IO;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// GlobalSettings.StartupProfile — the app-level "auto-load last profile" decision:
// return the last-used profile only when the toggle is on and one exists, else
// null (→ the app opens a blank draft).
public sealed class GlobalSettingsTests
{
    [Fact]
    public void StartupProfile_ToggleOff_ReturnsNull()
    {
        var g = new GlobalSettings
        {
            AutoLoadLastProfile = false,
            LastUsedProfile = new ProfileRef("Paradigm", "MudPlay"),
        };

        Assert.Null(g.StartupProfile());
    }

    [Fact]
    public void StartupProfile_ToggleOn_WithLastUsed_ReturnsIt()
    {
        var last = new ProfileRef("Paradigm", "MudPlay");
        var g = new GlobalSettings { AutoLoadLastProfile = true, LastUsedProfile = last };

        Assert.Equal(last, g.StartupProfile());
    }

    [Fact]
    public void StartupProfile_ToggleOn_FirstRun_ReturnsNull()
    {
        // Toggle on but nothing loaded yet (fresh install) → still opens blank.
        var g = new GlobalSettings { AutoLoadLastProfile = true, LastUsedProfile = null };

        Assert.Null(g.StartupProfile());
    }

    [Fact]
    public void AutoLoadLastProfile_DefaultsOff()
    {
        Assert.False(new GlobalSettings().AutoLoadLastProfile);
    }

    // The renderer is picked from global.json before the settings load, so the
    // launch reads the file itself: what a Settings save writes is what the next
    // launch draws with, and a file that's missing, broken or silent on it draws
    // with the CPU, the default.
    [Fact]
    public void DrawWithGraphicsCard_TheLaunchReadsWhatASaveWrote_AndDefaultsToTheCpu()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-drawing-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "global.json");
            Assert.False(ClientEnvironment.ReadDrawWithGraphicsCard(path));

            JsonStore.Save(path, new GlobalSettings { DrawWithGraphicsCard = true });
            Assert.True(ClientEnvironment.ReadDrawWithGraphicsCard(path));

            JsonStore.Save(path, new GlobalSettings());
            Assert.False(ClientEnvironment.ReadDrawWithGraphicsCard(path));

            File.WriteAllText(path, "{ \"DrawWithGraphicsCard\": tru");
            Assert.False(ClientEnvironment.ReadDrawWithGraphicsCard(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
