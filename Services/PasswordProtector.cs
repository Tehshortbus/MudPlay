using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FujinTerm.Services;

// AES-GCM encrypt / decrypt for short secrets (BBS passwords). The ciphertext
// is stored inline on the owning record — e.g.
// Models.Profile.BbsCredentials.EncryptedPassword — so a character profile
// JSON is fully self-contained for backup / copy.
//
// Key material lives in a single file at Data/.credkey — 32 random bytes,
// generated on first use, mode 0600 on Unix (NTFS already inherits
// user-private perms on Windows). The key file is the only piece a would-be
// reader needs in addition to the profile JSON to recover the plaintext, so
// backups / shares should exclude it.
//
// Not as strong as a real OS keychain (libsecret / DPAPI / macOS Keychain). A
// real keychain can be swapped in behind this class without touching any
// caller, because the public surface is just Protect / Unprotect.
public sealed class PasswordProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly string _keyPath;
    private readonly object _lock = new();
    private byte[]? _key;

    public PasswordProtector() : this(Path.Combine(AppPaths.DataRoot, ".credkey")) { }

    public PasswordProtector(string keyPath)
    {
        _keyPath = keyPath;
    }

    // Encrypt plaintext with the per-user key and return the base64-encoded
    // blob (nonce ‖ ciphertext ‖ tag) suitable for stuffing into JSON.
    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        byte[] key = LoadOrCreateKey();

        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] cipherBytes = new byte[plainBytes.Length];
        byte[] tag = new byte[TagSize];

        using (AesGcm aes = new(key, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
        }

        byte[] combined = new byte[NonceSize + cipherBytes.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(cipherBytes, 0, combined, NonceSize, cipherBytes.Length);
        Buffer.BlockCopy(tag, 0, combined, NonceSize + cipherBytes.Length, TagSize);
        return Convert.ToBase64String(combined);
    }

    // Decrypt a blob previously produced by Protect. Returns null when the
    // blob is malformed, the tag fails to verify, or the key file has been
    // replaced since the blob was written.
    public string? Unprotect(string protectedBlob)
    {
        if (string.IsNullOrEmpty(protectedBlob)) return null;
        byte[] key = LoadOrCreateKey();

        byte[] combined;
        try { combined = Convert.FromBase64String(protectedBlob); }
        catch (FormatException) { return null; }

        if (combined.Length < NonceSize + TagSize) return null;

        byte[] nonce = new byte[NonceSize];
        byte[] tag = new byte[TagSize];
        byte[] cipherBytes = new byte[combined.Length - NonceSize - TagSize];
        Buffer.BlockCopy(combined, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(combined, NonceSize, cipherBytes, 0, cipherBytes.Length);
        Buffer.BlockCopy(combined, NonceSize + cipherBytes.Length, tag, 0, TagSize);

        byte[] plainBytes = new byte[cipherBytes.Length];
        try
        {
            using AesGcm aes = new(key, TagSize);
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        }
        catch (CryptographicException) { return null; }

        return Encoding.UTF8.GetString(plainBytes);
    }

    private byte[] LoadOrCreateKey()
    {
        lock (_lock)
        {
            if (_key is not null) return _key;
            if (File.Exists(_keyPath))
            {
                byte[] existing = File.ReadAllBytes(_keyPath);
                if (existing.Length == KeySize)
                {
                    _key = existing;
                    return _key;
                }
            }
            byte[] fresh = RandomNumberGenerator.GetBytes(KeySize);
            Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
            File.WriteAllBytes(_keyPath, fresh);
            RestrictPermissions(_keyPath);
            _key = fresh;
            return _key;
        }
    }

    // chmod 600 on Unix; no-op on Windows (NTFS inherits user perms).
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
