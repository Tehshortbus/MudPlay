using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class PasswordProtectorTests : IDisposable
{
    private readonly string _scratchDir;
    private readonly PasswordProtector _protector;

    public PasswordProtectorTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), $"FT-pwprot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
        _protector = new PasswordProtector(Path.Combine(_scratchDir, ".key"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void RoundTrip_RecoversPlaintext()
    {
        string blob = _protector.Protect("hunter2");
        Assert.NotEqual("hunter2", blob);                  // ciphertext, not plaintext
        Assert.Equal("hunter2", _protector.Unprotect(blob));
    }

    [Fact]
    public void Protect_TwoCallsProduceDifferentCiphertext()
    {
        string a = _protector.Protect("hunter2");
        string b = _protector.Protect("hunter2");
        Assert.NotEqual(a, b);                              // fresh nonce per call
        Assert.Equal("hunter2", _protector.Unprotect(a));
        Assert.Equal("hunter2", _protector.Unprotect(b));
    }

    [Fact]
    public void Unprotect_GarbageReturnsNull()
    {
        Assert.Null(_protector.Unprotect("not-base64-!!!"));
        Assert.Null(_protector.Unprotect("dGlueQ=="));      // too short to be a blob
    }

    [Fact]
    public void Unprotect_WithDifferentKey_ReturnsNull()
    {
        string blob = _protector.Protect("hunter2");
        // Different key file — the GCM tag verification fails and we hand
        // back null rather than throwing.
        PasswordProtector other = new(Path.Combine(_scratchDir, ".other-key"));
        Assert.Null(other.Unprotect(blob));
    }

    [Fact]
    public void NewInstance_SameKeyFile_DecryptsExisting()
    {
        string keyPath = Path.Combine(_scratchDir, ".shared-key");
        PasswordProtector first = new(keyPath);
        string blob = first.Protect("shared-secret");

        PasswordProtector second = new(keyPath);
        Assert.Equal("shared-secret", second.Unprotect(blob));
    }
}
