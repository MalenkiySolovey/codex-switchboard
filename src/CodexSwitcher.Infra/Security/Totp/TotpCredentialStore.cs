using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;
using CodexSwitcher.Core.Security.Verification;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Codex.Usage;
using CodexSwitcher.Infra.Common.Logging;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Common.Time;
using CodexSwitcher.Infra.Providers.Inspection;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Scheduling;
using CodexSwitcher.Infra.Security.Dpapi;
using CodexSwitcher.Infra.Security.Hardening;
using CodexSwitcher.Infra.Security.Totp;
using CodexSwitcher.Infra.Settings;
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Services;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TotpGenerator = CodexSwitcher.Core.Security.Totp.Totp;

namespace CodexSwitcher.Infra.Security.Totp;

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

        if (!TotpGenerator.TryParse(provisioning, out _, out var parseError))
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
    public bool TryComputeCode(Guid profileId, DateTimeOffset now, out TotpCode code, out string? errorMessage)
    {
        code = default;
        errorMessage = null;

        var path = CredentialPath(profileId);

        using (_coordinator.Lock(profileId))
        {
            if (!_fs.FileExists(path))
            {
                errorMessage = "2FA não configurado para esta conta.";
                return false;
            }

            byte[] cipher;
            try
            {
                cipher = _fs.ReadAllBytes(path);
            }
            catch (Exception)
            {
                errorMessage = "Falha ao ler a credencial 2FA do disco.";
                return false;
            }

            byte[] plaintext;
            try
            {
                plaintext = _protector.Unprotect(cipher);
            }
            catch (SecretDecryptionException)
            {
                errorMessage = "A chave de 2FA não pôde ser decifrada neste dispositivo.";
                return false;
            }
            catch (Exception)
            {
                errorMessage = "Falha na decifragem da credencial 2FA.";
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
                errorMessage = "Registro de credencial 2FA inválido ou corrompido.";
                return false;
            }

            if (!TotpGenerator.TryParse(record.Provisioning, out var secret, out var parseError))
            {
                errorMessage = parseError ?? "Chave de 2FA armazenada inválida.";
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
