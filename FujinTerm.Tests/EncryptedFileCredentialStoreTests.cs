using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class EncryptedFileCredentialStoreTests : IDisposable
{
    private readonly string _scratchDir;
    private readonly EncryptedFileCredentialStore _store;

    public EncryptedFileCredentialStoreTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), $"FujinTerm-cred-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
        _store = new EncryptedFileCredentialStore(
            Path.Combine(_scratchDir, ".credkey"),
            Path.Combine(_scratchDir, "credentials.dat"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task SetThenGet_RoundTrips()
    {
        await _store.SetAsync("bbs:playpen:raijin:password", "hunter2");
        Assert.Equal("hunter2", await _store.GetAsync("bbs:playpen:raijin:password"));
    }

    [Fact]
    public async Task UnsetId_ReturnsNull()
    {
        Assert.Null(await _store.GetAsync("never-set"));
    }

    [Fact]
    public async Task Set_Overwrite_ReplacesOldValue()
    {
        await _store.SetAsync("k", "first");
        await _store.SetAsync("k", "second");
        Assert.Equal("second", await _store.GetAsync("k"));
    }

    [Fact]
    public async Task Delete_DropsValue()
    {
        await _store.SetAsync("k", "v");
        await _store.DeleteAsync("k");
        Assert.Null(await _store.GetAsync("k"));
    }

    [Fact]
    public async Task DataFile_DoesNotContainPlaintext()
    {
        await _store.SetAsync("k", "ThisIsTheSecret123");
        string dataFile = Path.Combine(_scratchDir, "credentials.dat");
        string contents = await File.ReadAllTextAsync(dataFile);
        Assert.DoesNotContain("ThisIsTheSecret123", contents);
    }

    [Fact]
    public async Task NewInstance_WithSameKey_DecryptsExisting()
    {
        await _store.SetAsync("k", "shared-secret");

        EncryptedFileCredentialStore other = new(
            Path.Combine(_scratchDir, ".credkey"),
            Path.Combine(_scratchDir, "credentials.dat"));
        Assert.Equal("shared-secret", await other.GetAsync("k"));
    }

    [Fact]
    public void BackendName_IsHumanReadable()
    {
        Assert.False(string.IsNullOrWhiteSpace(_store.BackendName));
    }
}
