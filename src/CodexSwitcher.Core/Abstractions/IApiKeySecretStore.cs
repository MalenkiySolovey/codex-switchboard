namespace CodexSwitcher.Core.Abstractions;

/// <summary>
/// Secure storage for API provider keys encrypted at rest via DPAPI CurrentUser.
/// Isolates raw provider API keys from provider metadata, settings, and logs.
/// </summary>
public interface IApiKeySecretStore
{
    /// <summary>Checks whether an encrypted API key is stored for the given profile.</summary>
    bool HasApiKey(Guid profileId);

    /// <summary>Encrypts and saves the raw API key atomically to protected storage.</summary>
    void SaveApiKey(Guid profileId, string apiKey);

    /// <summary>
    /// Decrypts the stored API key on demand.
    /// Returns null if not found. Zeroes plaintext buffers immediately after caller use.
    /// </summary>
    string? GetApiKey(Guid profileId);

    /// <summary>Permanently deletes the encrypted API key file for the given profile.</summary>
    bool DeleteApiKey(Guid profileId);
}
