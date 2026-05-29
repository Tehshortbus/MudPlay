namespace FujinTerm.Services;

/// <summary>
/// Cross-platform secret storage. Each call is keyed by an opaque
/// <paramref name="id"/> — for example <c>bbs:{bbs-name}:{char-name}:password</c> —
/// and only the ID is persisted in profile JSON; the secret itself
/// never appears in any user-readable file.
/// </summary>
/// <remarks>
/// Phase 4 ships a single implementation: <see cref="EncryptedFileCredentialStore"/>
/// — AES-GCM with a per-user random key kept under restrictive permissions
/// in the user's data folder. Future PRs can swap in real OS-keychain
/// integrations (libsecret on Linux, DPAPI on Windows, Keychain on macOS)
/// behind the same interface.
/// </remarks>
public interface ICredentialStore
{
    /// <summary>True when the store can read + write. False = setup error / permission issue.</summary>
    bool IsAvailable { get; }

    /// <summary>Human-readable summary of the backing implementation — surfaced in the Log pane.</summary>
    string BackendName { get; }

    /// <summary>Retrieve a previously-stored secret. Returns <c>null</c> when no secret exists for the id.</summary>
    Task<string?> GetAsync(string id);

    /// <summary>Persist a secret under the id, overwriting any previous value.</summary>
    Task SetAsync(string id, string secret);

    /// <summary>Drop the secret. No-op if it was never set.</summary>
    Task DeleteAsync(string id);
}
