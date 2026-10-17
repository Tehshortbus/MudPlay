using System;
using System.IO;
using Xunit;

namespace MudPlay.Tests;

// Test infrastructure: the guard ScratchRoot.Require puts in front of every test that
// deletes folders through the app's stores. A scratch root must not be, hold, or lie
// inside the installed data folder, however symbolic links dress either path up, or
// a test run would delete the user's live data.
public sealed class ScratchRootTests : IDisposable
{
    private readonly string _jail = Path.Combine(Path.GetTempPath(), "mudplay-scratchroot-" + Guid.NewGuid().ToString("N"));

    public ScratchRootTests() => Directory.CreateDirectory(_jail);

    public void Dispose()
    {
        try { Directory.Delete(_jail, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void AScratchRootThatIsASymbolicLinkToTheLiveFolder_IsRefused_AsIsOneInsideIt()
    {
        if (OperatingSystem.IsWindows()) return;
        string live = Directory.CreateDirectory(Path.Combine(_jail, "aa-live")).FullName;
        Directory.CreateDirectory(Path.Combine(live, "game data"));
        string link = Path.Combine(_jail, "zz-link");
        Directory.CreateSymbolicLink(link, live);
        string other = Directory.CreateDirectory(Path.Combine(_jail, "mm-other")).FullName;

        // Lexically the link is nowhere near the live folder.
        Assert.False(Path.GetFullPath(link).StartsWith(live, StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(ScratchRoot.WhyNotScratch(link, live));
        Assert.NotNull(ScratchRoot.WhyNotScratch(Path.Combine(link, "game data"), live));
        Assert.NotNull(ScratchRoot.WhyNotScratch(Path.Combine(link, "not", "there", "yet"), live));
        Assert.NotNull(ScratchRoot.WhyNotScratch(live, live));
        Assert.Null(ScratchRoot.WhyNotScratch(other, live));
    }

    [Fact]
    public void AScratchRootBesideOrAboveTheLiveFolderBehindALink_IsRefused_AndAnUnrelatedOneIsNot()
    {
        if (OperatingSystem.IsWindows()) return;
        string live = Directory.CreateDirectory(Path.Combine(_jail, "aa-live")).FullName;
        string liveLink = Path.Combine(_jail, "bb-livelink");
        Directory.CreateSymbolicLink(liveLink, live);
        string aboveLink = Path.Combine(_jail, "cc-abovelink");
        Directory.CreateSymbolicLink(aboveLink, _jail);
        string other = Directory.CreateDirectory(Path.Combine(_jail, "mm-other")).FullName;

        // The live folder given as a link, and a root that is a link to the folder around it.
        Assert.NotNull(ScratchRoot.WhyNotScratch(live, liveLink));
        Assert.NotNull(ScratchRoot.WhyNotScratch(aboveLink, live));
        Assert.Null(ScratchRoot.WhyNotScratch(other, liveLink));
    }
}
