using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Security;

namespace CodexSwitcher.Core.Services;

/// <summary>
/// Armazenamento de credenciais TOTP isolado do cofre principal.
/// Cada chave de 2FA pertence estritamente a um <see cref="ProfileId"/>, sendo cifrada com DPAPI
/// (escopo CurrentUser) e gravada atomicamente em um arquivo separado.
/// Nunca armazena segredos em texto claro nem os mistura com o auth.json.
/// </summary>
public sealed class TotpCredentialStore : ITotpCredentialStore
{
    private readonly ISecretProtector _protector;
    private readonly IFileSystem _fs;
    private readonly string _totpDir;
    private readonly IProfileOperationCoordinator _coordinator;

    public TotpCredentialStore(
        ISecretProtector protector,
        IFileSystem fs,
        string totpDir,
        IProfileOperationCoordinator? coordinator = null)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _totpDir = totpDir ?? throw new ArgumentNullException(nameof(totpDir));
        _coordinator = coordinator ?? new ProfileOperationCoordinator();
    }

    /// <summary>Caminho do arquivo cifrado do segredo TOTP para um perfil específico.</summary>
    public string CredentialPath(Guid profileId) => Path.Combine(_totpDir, $"{profileId:N}.bin");

    /// <inheritdoc/>
    public bool HasCredential(Guid profileId) => _fs.FileExists(CredentialPath(profileId));

    /// <inheritdoc/>
    public void Save(Guid profileId, string provisioning) =>
        Save(profileId, provisioning, DateTimeOffset.UtcNow);

    /// <inheritdoc/>
    public void Save(Guid profileId, string provisioning, DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(provisioning))
            throw new ArgumentException("A chave do 2FA não pode ser vazia.", nameof(provisioning));

        if (!Totp.TryParse(provisioning, out _, out var parseError))
            throw new ArgumentException(parseError ?? "Chave de 2FA inválida.", nameof(provisioning));

        var path = CredentialPath(profileId);

        using (_coordinator.Lock(profileId))
        {
            var record = new ProfileTotpRecord
            {
                SchemaVersion = 1,
                Kind = "profile-totp",
                ProfileId = profileId,
                Provisioning = provisioning.Trim(),
                CreatedAt = createdAt,
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
    public bool TryComputeCode(Guid profileId, DateTimeOffset now, out TotpCode code, out string? error)
    {
        code = default;
        error = null;

        var path = CredentialPath(profileId);

        using (_coordinator.Lock(profileId))
        {
            if (!_fs.FileExists(path))
            {
                error = "2FA não configurado para esta conta.";
                return false;
            }

            byte[] cipher;
            try
            {
                cipher = _fs.ReadAllBytes(path);
            }
            catch (Exception)
            {
                error = "Falha ao ler a credencial 2FA do disco.";
                return false;
            }

            byte[] plaintext;
            try
            {
                plaintext = _protector.Unprotect(cipher);
            }
            catch (SecretDecryptionException)
            {
                error = "A chave de 2FA não pôde ser decifrada neste dispositivo.";
                return false;
            }
            catch (Exception)
            {
                error = "Falha na decifragem da credencial 2FA.";
                return false;
            }

            ProfileTotpRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<ProfileTotpRecord>(plaintext);
            }
            catch
            {
                record = null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            if (record is null || record.SchemaVersion != 1 || record.Kind != "profile-totp" || record.ProfileId != profileId)
            {
                error = "Registro de credencial 2FA inválido ou corrompido.";
                return false;
            }

            if (!Totp.TryParse(record.Provisioning, out var secret, out var parseError))
            {
                error = parseError ?? "Chave de 2FA armazenada inválida.";
                return false;
            }

            code = secret!.Compute(now);
            return true;
        }
    }

    /// <inheritdoc/>
    public bool Delete(Guid profileId)
    {
        var path = CredentialPath(profileId);

        using (_coordinator.Lock(profileId))
        {
            if (_fs.FileExists(path))
            {
                _fs.Delete(path);
                return true;
            }
            return false;
        }
    }

    internal sealed class ProfileTotpRecord
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "profile-totp";

        [JsonPropertyName("profileId")]
        public Guid ProfileId { get; set; }

        [JsonPropertyName("provisioning")]
        public string Provisioning { get; set; } = string.Empty;

        [JsonPropertyName("createdAt")]
        public DateTimeOffset CreatedAt { get; set; }
    }
}
