using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexSwitcher.Core.Abstractions;

namespace CodexSwitcher.Core.Services;

/// <summary>
/// Secure storage for API provider keys, encrypted at rest via DPAPI CurrentUser.
/// Each key belongs strictly to a profile ID and is stored in an isolated binary file.
/// Plaintext keys are never stored in profiles.json, settings.json, or audit logs.
/// </summary>
public sealed class ApiKeySecretStore : IApiKeySecretStore
{
    private readonly ISecretProtector _protector;
    private readonly IFileSystem _fs;
    private readonly string _apiKeysDir;
    private readonly IProfileOperationCoordinator _coordinator;

    public ApiKeySecretStore(
        ISecretProtector protector,
        IFileSystem fs,
        string apiKeysDir,
        IProfileOperationCoordinator? coordinator = null)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _apiKeysDir = apiKeysDir ?? throw new ArgumentNullException(nameof(apiKeysDir));
        _coordinator = coordinator ?? new ProfileOperationCoordinator();
    }

    /// <summary>Resolves the file path of the encrypted secret blob for a given profile.</summary>
    public string KeyPath(Guid profileId) => Path.Combine(_apiKeysDir, $"{profileId:N}.bin");

    /// <inheritdoc/>
    public bool HasApiKey(Guid profileId) => _fs.FileExists(KeyPath(profileId));

    /// <inheritdoc/>
    public void SaveApiKey(Guid profileId, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key cannot be empty or whitespace.", nameof(apiKey));

        var path = KeyPath(profileId);

        using (_coordinator.Lock(profileId))
        {
            var record = new ApiKeyRecord
            {
                SchemaVersion = 1,
                Kind = "api-key",
                ProfileId = profileId,
                ApiKey = apiKey.Trim(),
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var plaintextBytes = JsonSerializer.SerializeToUtf8Bytes(record);
            try
            {
                var cipher = _protector.Protect(plaintextBytes);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    _fs.CreateDirectory(dir);

                _fs.WriteAllBytesAtomic(path, cipher);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }
        }
    }

    /// <inheritdoc/>
    public string? GetApiKey(Guid profileId)
    {
        var path = KeyPath(profileId);

        using (_coordinator.Lock(profileId))
        {
            if (!_fs.FileExists(path))
                return null;

            byte[] cipher;
            try
            {
                cipher = _fs.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            byte[] plaintext;
            try
            {
                plaintext = _protector.Unprotect(cipher);
            }
            catch
            {
                return null;
            }

            try
            {
                var record = JsonSerializer.Deserialize<ApiKeyRecord>(plaintext);
                return record?.ApiKey;
            }
            catch
            {
                return null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    /// <inheritdoc/>
    public bool DeleteApiKey(Guid profileId)
    {
        var path = KeyPath(profileId);
        using (_coordinator.Lock(profileId))
        {
            if (!_fs.FileExists(path))
                return false;

            try
            {
                _fs.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private sealed class ApiKeyRecord
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "api-key";

        [JsonPropertyName("profileId")]
        public Guid ProfileId { get; set; }

        [JsonPropertyName("apiKey")]
        public string ApiKey { get; set; } = string.Empty;

        [JsonPropertyName("createdAt")]
        public DateTimeOffset CreatedAt { get; set; }
    }
}
