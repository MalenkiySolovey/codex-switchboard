namespace CodexSwitcher.Core.Abstractions;

/// <summary>
/// Resultado da operação de verificação da senha da conta do Windows do usuário atual.
/// </summary>
public enum WindowsPasswordVerificationResult
{
    /// <summary>A senha do usuário atual do Windows foi validada com sucesso (SID coincide).</summary>
    VerifiedCurrentUser,

    /// <summary>O usuário cancelou explicitamente o diálogo de credenciais do Windows.</summary>
    Canceled,

    /// <summary>Credenciais inválidas fornecidas (senha incorreta ou falha de logon).</summary>
    InvalidCredentials,

    /// <summary>Credenciais válidas foram fornecidas, mas pertencem a outro usuário do Windows (SID diferente).</summary>
    DifferentUser,

    /// <summary>A conta do usuário do Windows está bloqueada.</summary>
    AccountLocked,

    /// <summary>A senha do usuário do Windows expirou ou precisa ser alterada.</summary>
    PasswordExpired,

    /// <summary>A conta do usuário do Windows possui restrições ativas (ex.: desativada, horário ou estação restritos).</summary>
    AccountRestricted,

    /// <summary>Nenhum servidor de logon disponível para autenticar a credencial.</summary>
    NoLogonServers,

    /// <summary>O subsistema CredUI ou provedor de credenciais não está disponível.</summary>
    CredentialProviderUnavailable,

    /// <summary>Tipo de credencial não suportado pelo provedor de autenticação.</summary>
    UnsupportedCredentialType,

    /// <summary>Pacote de autenticação não suportado pelo sistema ou pela sessão.</summary>
    UnsupportedAuthenticationPackage,

    /// <summary>Falha ao mapear a identidade do usuário para o formato de autenticação.</summary>
    IdentityMappingFailed,

    /// <summary>Erro do sistema operacional durante a tentativa de verificação.</summary>
    SystemError
}

/// <summary>
/// Abstração para validação da senha da conta Windows do usuário logado atualmente.
/// As credenciais existem apenas transitoriamente durante a operação de verificação e são limpas imediatamente.
/// </summary>
public interface IWindowsPasswordVerificationService
{
    /// <summary>
    /// Indica se o subsistema de verificação por senha do Windows é suportado na plataforma atual.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Solicita e valida a senha da conta do Windows do usuário atual.
    /// </summary>
    Task<WindowsPasswordVerificationResult> VerifyCurrentUserPasswordAsync(string? message = null, string? caption = null);
}
