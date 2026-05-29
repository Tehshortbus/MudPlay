using System.IO;
using System.Text;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class SpellMessageImporterTests : IDisposable
{
    private readonly string _root;
    private readonly GameDataCache _cache;
    private readonly SpellMessageImporter _importer;

    public SpellMessageImporterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-spellmsg-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _cache = new GameDataCache(_root);
        _importer = new SpellMessageImporter(_cache);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task ParseAsync_ThrowsFileNotFound_ForMissingPath()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            SpellMessageImporter.ParseAsync(Path.Combine(_root, "nope.json")));
    }

    [Fact]
    public async Task ParseStream_DecodesRowsWithStringEnumKind()
    {
        string json =
            "[{\"SpellId\":12,\"Kind\":\"Cast\",\"Pattern\":\"You begin\",\"EffectFlags\":0}," +
             "{\"SpellId\":12,\"Kind\":\"Hit\",\"Pattern\":\"is hit by\",\"EffectFlags\":1}]";

        using MemoryStream ms = new(Encoding.UTF8.GetBytes(json));
        var rows = await SpellMessageImporter.ParseStreamAsync(ms);

        Assert.Equal(2, rows.Count);
        Assert.Equal(SpellMessageKind.Cast, rows[0].Kind);
        Assert.Equal(SpellMessageKind.Hit, rows[1].Kind);
        Assert.Equal("You begin", rows[0].Pattern);
        Assert.Equal(1, rows[1].EffectFlags);
    }

    [Fact]
    public async Task WriteAsync_RequiresActiveSet()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _importer.WriteAsync(Array.Empty<SpellMessage>()));
    }

    [Fact]
    public async Task WriteThenRead_RoundTrips()
    {
        Directory.CreateDirectory(Path.Combine(_root, "v1.11p"));
        _cache.SwitchSet("v1.11p");

        SpellMessage[] rows =
        {
            new(1, SpellMessageKind.Cast,         "You weave"),
            new(1, SpellMessageKind.Hit,          "screams in pain", EffectFlags: 4),
            new(1, SpellMessageKind.TargetEffect, "feels weaker"),
        };

        await _importer.WriteAsync(rows);
        var read = _importer.ReadExisting();

        Assert.Equal(3, read.Count);
        Assert.Equal(SpellMessageKind.Hit, read[1].Kind);
        Assert.Equal(4, read[1].EffectFlags);
    }

    [Fact]
    public void ReadExisting_NoFile_ReturnsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_root, "v1.11p"));
        _cache.SwitchSet("v1.11p");

        Assert.Empty(_importer.ReadExisting());
    }

    [Fact]
    public void ReadExisting_NoActiveSet_ReturnsEmpty()
    {
        Assert.Empty(_importer.ReadExisting());
    }
}
