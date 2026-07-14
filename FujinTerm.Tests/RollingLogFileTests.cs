using System.IO;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// Pins the rolling-log primitive that backs the per-character talk.log /
// transactions.log: the on-disk line count never exceeds the cap, content
// persists across a reopen (a restart), and Truncate wipes the file.
public sealed class RollingLogFileTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"fujin-rolllog-{Guid.NewGuid():N}.log");

    [Fact]
    public void Append_KeepsOnlyLastNLines()
    {
        string path = TempPath();
        try
        {
            RollingLogFile log = new();
            log.Open(path, maxLines: 3);
            for (int i = 1; i <= 10; i++) log.Append($"line {i}");

            Assert.Equal(new[] { "line 8", "line 9", "line 10" }, File.ReadAllLines(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reopen_ReloadsTail_AndContinuesAppending()
    {
        string path = TempPath();
        try
        {
            RollingLogFile first = new();
            first.Open(path, maxLines: 5);
            first.Append("a");
            first.Append("b");
            first.Close();

            // Simulate a restart: a fresh instance over the same file continues
            // where the old one left off rather than starting empty.
            RollingLogFile second = new();
            second.Open(path, maxLines: 5);
            second.Append("c");

            Assert.Equal(new[] { "a", "b", "c" }, File.ReadAllLines(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Open_TrimsAnOverlongExistingFileToCap()
    {
        string path = TempPath();
        try
        {
            File.WriteAllLines(path, new[] { "1", "2", "3", "4", "5" });

            RollingLogFile log = new();
            log.Open(path, maxLines: 2);
            log.Append("6");

            Assert.Equal(new[] { "5", "6" }, File.ReadAllLines(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SetMaxLines_ShrinkingCapDropsOldestImmediately()
    {
        string path = TempPath();
        try
        {
            RollingLogFile log = new();
            log.Open(path, maxLines: 5);
            foreach (string s in new[] { "a", "b", "c", "d" }) log.Append(s);

            log.SetMaxLines(2);

            Assert.Equal(new[] { "c", "d" }, File.ReadAllLines(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Truncate_EmptiesTheFile()
    {
        string path = TempPath();
        try
        {
            RollingLogFile log = new();
            log.Open(path, maxLines: 5);
            log.Append("a");
            log.Append("b");

            log.Truncate();

            Assert.Empty(File.ReadAllLines(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Append_AfterClose_IsIgnored()
    {
        string path = TempPath();
        try
        {
            RollingLogFile log = new();
            log.Open(path, maxLines: 5);
            log.Append("a");
            log.Close();
            log.Append("b"); // no open file → dropped

            Assert.Equal(new[] { "a" }, File.ReadAllLines(path));
        }
        finally { File.Delete(path); }
    }
}
