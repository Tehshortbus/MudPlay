using System;
using System.IO;
using System.Linq;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The staged/committed emote library + the shareable package. Each test uses its own
// temp directory so it never touches the live Emotes folder. (Image scaling in Commit
// falls back to a raw copy here since Avalonia's decoder isn't initialised.)
[Collection(EmoteRuntimeCollection.Name)]
public sealed class EmoteStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mudplay-emotes-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (string d in new[] { _dir, _dir + "-tmp" })
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { }
    }

    private string TempImage(string name)
    {
        Directory.CreateDirectory(_dir);
        string p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, new byte[] { 1, 2, 3, 4 });   // stand-in bytes; Commit copies, decode falls back
        return p;
    }

    private static EmoteDraft Emoji(string sc, string emoji) => new() { Shortcode = sc, Emoji = emoji };
    private static EmoteDraft Image(string sc, string path) => new() { Shortcode = sc, ImageSourcePath = path };

    [Fact]
    public void CommitUnicode_PersistsAndReloads()
    {
        new EmoteStore(_dir).Commit(new[] { Emoji("boom", "💥") }, Array.Empty<string>());

        var reloaded = new EmoteStore(_dir);
        var e = Assert.Single(reloaded.Emotes);
        Assert.Equal("boom", e.Shortcode);
        Assert.Equal("💥", e.Emoji);
    }

    [Fact]
    public void CommitImage_CopiesFileIntoStore()
    {
        var store = new EmoteStore(_dir);
        store.Commit(new[] { Image("pic", TempImage("src.png")) }, Array.Empty<string>());

        var e = Assert.Single(store.Emotes);
        Assert.Equal("pic", e.Shortcode);
        Assert.False(string.IsNullOrEmpty(e.Image));
        Assert.True(File.Exists(Path.Combine(_dir, e.Image!)));
    }

    [Fact]
    public void Commit_ReplacingSet_PrunesRemovedImages()
    {
        var store = new EmoteStore(_dir);
        store.Commit(new[] { Image("pic", TempImage("src.png")) }, Array.Empty<string>());
        string img = Path.Combine(_dir, store.Emotes[0].Image!);
        Assert.True(File.Exists(img));

        store.Commit(Array.Empty<EmoteDraft>(), Array.Empty<string>());   // removed
        Assert.Empty(store.Emotes);
        Assert.False(File.Exists(img));
    }

    [Fact]
    public void HiddenDefaults_Persist()
    {
        new EmoteStore(_dir).Commit(Array.Empty<EmoteDraft>(), new[] { "lol", "fire" });

        var reloaded = new EmoteStore(_dir);
        Assert.Contains("lol", reloaded.HiddenDefaults);
        Assert.Contains("fire", reloaded.HiddenDefaults);
    }

    [Fact]
    public void ExportThenReadPackage_RoundTrips()
    {
        var src = new EmoteStore(_dir);
        src.Commit(new[] { Emoji("boom", "💥"), Image("pic", TempImage("s.png")) }, Array.Empty<string>());

        string pkg = Path.Combine(_dir, "pack.mudpack");
        Assert.True(src.Export(pkg));

        var drafts = src.ReadPackageDrafts(pkg, _dir + "-tmp");
        Assert.NotNull(drafts);
        Assert.Contains(drafts!, d => d.Shortcode == "boom" && d.Emoji == "💥");
        Assert.Contains(drafts!, d => d.Shortcode == "pic" && d.ImageSourcePath is not null);
    }

    [Fact]
    public void ReadPackageDrafts_BadFile_ReturnsNull()
    {
        Directory.CreateDirectory(_dir);
        string bad = Path.Combine(_dir, "notapack.txt");
        File.WriteAllText(bad, "nope");
        Assert.Null(new EmoteStore(_dir).ReadPackageDrafts(bad, _dir + "-tmp"));
    }

    // The catalog is keyed by shortcode, built as the store opens at startup: a
    // hand-edited manifest's null shortcode, null emote or null hidden name is left
    // out and the rest of the library loads.
    [Fact]
    public void AManifestWithNulls_LoadsTheRest()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "emotes.json"), """
            { "Emotes": [ { "Shortcode": null, "Emoji": "x" }, { "Shortcode": "boom", "Emoji": "💥" }, null ],
              "HiddenDefaults": [ null, "smile" ] }
            """);

        EmoteStore store = new(_dir);

        Assert.Equal("boom", Assert.Single(store.Emotes).Shortcode);
        Assert.Equal("smile", Assert.Single(store.HiddenDefaults));
    }
}
