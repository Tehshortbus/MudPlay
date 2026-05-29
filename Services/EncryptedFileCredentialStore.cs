using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FujinTerm.Services;

/// <summary>
/// AES-GCM-encrypted JSON file. The encryption key is a 32-byte random
/// generated on first use and persisted under restrictive permissions in
/// the user's data folder. Not as strong as a real OS keychain (the key
/// file is readable by anyone who can read the data folder) but
/// significantly better than plaintext, and the interface stays the same
/// when a follow-up PR swaps in libsecret / DPAPI / Keychain.
/// </summary>
/// <remarks>
/// File layout: <c>Data/credentials.dat</c> — a JSON object mapping each
/// credential id to a base64-encoded blob structured as
/// <c>[12-byte nonce][ciphertext][16-byte tag]</c>. Loaded eagerly on
/// first call and held in memory for the rest of the process; reads are
/// O(1) lookups on a Dictionary.
/// </remarks>
public sealed class EncryptedFileCredentialStore : ICredentialStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly string _keyPath;
    private readonly string _dataPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private byte[]? _key;
    private Dictionary<string, string>? _table;

    public bool IsAvailable { get; private set; } = true;
    public string BackendName => "encrypted file (AES-GCM, per-user key)";

    public EncryptedFileCredentialStore()
        : this(Path.Combine(AppPaths.DataRoot, ".credkey"),
               Path.Combine(AppPaths.DataRoot, "credentials.dat")) { }

    public EncryptedFileCredentialStore(string keyPath, string dataPath)
    {
        _keyPath = keyPath;
        _dataPath = dataPath;
    }

    public async Task<string?> GetAsync(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await EnsureLoadedAsync().ConfigureAwait(false);
        if (_table is null || !_table.TryGetValue(id, out string? blob)) return null;
        return Decrypt(blob);
    }

    public async Task SetAsync(string id, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(secret);
        await EnsureLoadedAsync().ConfigureAwait(false);
        if (_table is null) return;
        _table[id] = Encrypt(secret);
        await PersistAsync().ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await EnsureLoadedAsync().ConfigureAwait(false);
        if (_table is null) return;
        if (_table.Remove(id))
            await PersistAsync().ConfigureAwait(false);
    }

    private async Task EnsureLoadedAsync()
    {
        if (_key is not null && _table is not null) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_key is null) _key = LoadOrCreateKey();
            if (_table is null) _table = LoadTable();
        }
        catch (Exception)
        {
            IsAvailable = false;
            _table = new();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private byte[] LoadOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            byte[] existing = File.ReadAllBytes(_keyPath);
            if (existing.Length == KeySize) return existing;
        }
        byte[] fresh = RandomNumberGenerator.GetBytes(KeySize);
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        File.WriteAllBytes(_keyPath, fresh);
        RestrictPermissions(_keyPath);
        return fresh;
    }

    private Dictionary<string, string> LoadTable()
    {
        if (!File.Exists(_dataPath)) return new();
        string json = File.ReadAllText(_dataPath);
        if (string.IsNullOrWhiteSpace(json)) return new();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
    }

    private async Task PersistAsync()
    {
        if (_table is null) return;
        string json = JsonSerializer.Serialize(_table);
        Directory.CreateDirectory(Path.GetDirectoryName(_dataPath)!);
        string tmp = _dataPath + ".tmp";
        await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
        File.Move(tmp, _dataPath, overwrite: true);
        RestrictPermissions(_dataPath);
    }

    private string Encrypt(string plaintext)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] cipherBytes = new byte[plainBytes.Length];
        byte[] tag = new byte[TagSize];
        using (AesGcm aes = new(_key!, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
        }
        byte[] combined = new byte[NonceSize + cipherBytes.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(cipherBytes, 0, combined, NonceSize, cipherBytes.Length);
        Buffer.BlockCopy(tag, 0, combined, NonceSize + cipherBytes.Length, TagSize);
        return Convert.ToBase64String(combined);
    }

    private string Decrypt(string blob)
    {
        byte[] combined = Convert.FromBase64String(blob);
        if (combined.Length < NonceSize + TagSize)
            throw new InvalidDataException("Credential blob is malformed.");
        byte[] nonce = new byte[NonceSize];
        byte[] tag = new byte[TagSize];
        byte[] cipherBytes = new byte[combined.Length - NonceSize - TagSize];
        Buffer.BlockCopy(combined, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(combined, NonceSize, cipherBytes, 0, cipherBytes.Length);
        Buffer.BlockCopy(combined, NonceSize + cipherBytes.Length, tag, 0, TagSize);

        byte[] plainBytes = new byte[cipherBytes.Length];
        using (AesGcm aes = new(_key!, TagSize))
        {
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        }
        return Encoding.UTF8.GetString(plainBytes);
    }

    /// <summary>chmod 600 on Unix; no-op on Windows (NTFS already inherits user perms).</summary>
    private static void RestrictPermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Older runtimes / non-Unix filesystems — fall through. The file
            // still ends up in the user's data folder which is itself
            // user-private on every supported platform.
        }
    }
}
