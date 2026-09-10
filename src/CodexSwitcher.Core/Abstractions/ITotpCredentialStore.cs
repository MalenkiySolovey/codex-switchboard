using CodexSwitcher.Core.Security;

namespace CodexSwitcher.Core.Abstractions;

/// <summary>
/// Gerenciador de credenciais TOTP (2FA) por perfil, cifradas em repouso com DPAPI.
/// Isola o segredo de 2FA do auth.json do Codex e do ProfileMetadata.
/// </summary>
public interface ITotpCredentialStore
{
    /// <summary>Verifica se há segredo TOTP configurado para o perfil especificado.</summary>
    bool HasCredential(Guid profileId);

    /// <summary>
    /// Cifra e grava o segredo TOTP de forma atômica no armazenamento protegido.
    /// Valida o segredo antes da persistência.
    /// </summary>
    void Save(Guid profileId, string provisioning, DateTimeOffset createdAt);

    /// <summary>
    /// Cifra e grava o segredo TOTP de forma atômica no armazenamento protegido.
    /// Valida o segredo antes da persistência. Usa o relógio padrão para a data de criação.
    /// </summary>
    void Save(Guid profileId, string provisioning);

    /// <summary>
    /// Decifra o segredo sob demanda, gera o código TOTP atual e descarta buffers intermediários.
    /// Nunca expõe a semente de 2FA à camada de apresentação.
    /// </summary>
    bool TryComputeCode(Guid profileId, DateTimeOffset now, out TotpCode code, out string? error);

    /// <summary>Exclui o arquivo cifrado do segredo TOTP do perfil.</summary>
    bool Delete(Guid profileId);
}
