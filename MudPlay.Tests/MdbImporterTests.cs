using System.IO;
using System.Linq;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Surface tests for MdbImporter — covers folder
// bookkeeping, filename sanitization, and the missing-file early
// exit. The Jackcess read path runs against real .mdb / .accdb
// fixtures and is exercised manually via the Game Data → Import
// .mdb… flow.
public sealed class MdbImporterTests
{
    [Fact]
    public async Task ImportAsync_MissingFile_ReturnsFailureWithNotFoundMessage()
    {
        MdbImporter importer = new();
        string bogus = Path.Combine(Path.GetTempPath(), "definitely-not-a-real-file-12345.mdb");

        MdbImportResult result = await importer.ImportAsync(bogus);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, result.FolderName);
        Assert.Equal(0, result.TablesFound);
        Assert.Equal(0, result.TablesImported);
        Assert.Equal(0, result.TablesSkipped);
        Assert.Equal(0, result.RowsImported);
    }

    [Theory]
    [InlineData("Monsters",           "Monsters")]
    [InlineData("Spell Messages",     "Spell Messages")]
    [InlineData("Path/With/Slashes",  "Path_With_Slashes")]
    [InlineData("",                   "_unnamed")]
    [InlineData("   ",                "_unnamed")]
    public void MakeFilesystemSafe_StripsInvalidChars(string input, string expected)
    {
        // The colon edge-case differs between Linux and Windows
        // (Path.GetInvalidFileNameChars varies by platform), so we
        // assert the cross-platform-safe cases only.
        Assert.Equal(expected, MdbImporter.MakeFilesystemSafe(input));
    }

    // A zero-table import removes the freshly-created (empty) set folder so a broken
    // MDB never leaves an empty set to switch to.
    [Fact]
    public void RemoveDirectoryIfEmpty_DeletesAnEmptyFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-empty-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        MdbImporter.RemoveDirectoryIfEmpty(dir);

        Assert.False(Directory.Exists(dir));
    }

    // The post-write round-trip check that gates the per-table import retry: a
    // truncated / malformed file is rejected so the importer retries (and, if it
    // stays bad, reports the table skipped instead of shipping a crasher).
    [Fact]
    public void TableJsonParses_TrueForValid_FalseForMalformedOrMissing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-tjp-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            string good = Path.Combine(dir, "good.json");
            File.WriteAllText(good, "[{\"Name\":\"Goblin\"}]");
            Assert.True(MdbImporter.TableJsonParses(good));

            string bad = Path.Combine(dir, "bad.json");
            // A raw 0x1E mid-string — the exact corruption the crash report carried.
            File.WriteAllText(bad, "[{\"Name\":\"Go" + (char)0x1E + "blin\"}]");
            Assert.False(MdbImporter.TableJsonParses(bad));

            string truncated = Path.Combine(dir, "truncated.json");
            File.WriteAllText(truncated, "[{\"Name\":\"Gob");   // interrupted write
            Assert.False(MdbImporter.TableJsonParses(truncated));

            Assert.False(MdbImporter.TableJsonParses(Path.Combine(dir, "missing.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // An export may or may not carry the Lairs table, and may or may not carry
    // room-command item sources: four kinds, each named for what it has.
    [Theory]
    [InlineData(9, false, 0, "9 tables, 1,234 entries — lairs table: no, room commands: no]")]
    [InlineData(10, true, 0, "10 tables, 1,234 entries — lairs table: yes, room commands: no]")]
    [InlineData(9, false, 187, "9 tables, 1,234 entries — lairs table: no, room commands: yes (187 items)]")]
    [InlineData(10, true, 187, "10 tables, 1,234 entries — lairs table: yes, room commands: yes (187 items)]")]
    public void CompleteStatus_NamesWhatTheExportCarries(int tables, bool lairs, int roomCommandItems, string tail)
    {
        MdbImportResult r = new(true, "", "set", tables, tables, 0, 1234, lairs, roomCommandItems);

        Assert.Equal("[MDB IMPORT COMPLETE: set — " + tail,
            MudPlay.ViewModels.MainWindowViewModel.BuildMdbCompleteStatus(r));
    }

    [Fact]
    public void CompleteStatus_TooFewTables_SaysSoAndPointsAtTheLog()
    {
        MdbImportResult r = new(true, "", "set", 4, 4, 0, 50, false, 0);

        Assert.EndsWith("UNEXPECTED TABLE COUNT — see Program Log]",
            MudPlay.ViewModels.MainWindowViewModel.BuildMdbCompleteStatus(r));
    }

    // The safety guard: a failed re-import over an EXISTING populated set must never
    // delete that set's files.
    [Fact]
    public void RemoveDirectoryIfEmpty_KeepsAPopulatedFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-populated-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string keep = Path.Combine(dir, "Rooms.json");
        File.WriteAllText(keep, "[]");
        try
        {
            MdbImporter.RemoveDirectoryIfEmpty(dir);

            Assert.True(Directory.Exists(dir), "an existing populated set must survive a failed re-import");
            Assert.True(File.Exists(keep));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
