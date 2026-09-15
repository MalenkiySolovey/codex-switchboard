using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
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
namespace CodexSwitcher.Core.Routing.Contracts;

/// <summary>Resultado bruto de uma invocação do binário codex.</summary>
public sealed record CodexCliResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Desfecho de um login OAuth conduzido pelo app-server do codex. Ver §5.</summary>
public sealed record CodexLoginResult(bool Success, string? Error = null);

/// <summary>
/// Sessão de login OAuth (ChatGPT) conduzida pelo <c>codex app-server</c>: o codex devolve a
/// <see cref="AuthUrl"/> para o cliente abrir no WebView2 limpo (nunca abre o navegador do sistema),
/// mantém o servidor de callback local e escreve o <c>auth.json</c> no CODEX_HOME isolado ao concluir.
/// Diferente do <c>login --device-auth</c>, não exige a configuração de segurança do ChatGPT.
/// Ver BUSINESS_RULES.md §5 e a memória [[login-clean-guest-session]].
/// </summary>
public interface ICodexLoginSession : IAsyncDisposable
{
    /// <summary>URL de autorização OAuth a ser aberta no WebView2 efêmero (sessão visitante).</summary>
    string AuthUrl { get; }

    /// <summary>Identificador do login em andamento (usado para cancelar).</summary>
    string LoginId { get; }

    /// <summary>
    /// Completa quando o codex sinaliza o fim do login (<c>account/login/completed</c>). Em caso de
    /// sucesso, o <c>auth.json</c> já está escrito no CODEX_HOME da sessão.
    /// </summary>
    Task<CodexLoginResult> Completion { get; }
}

/// <summary>
/// Executa o binário <c>codex</c>. Permite definir <c>CODEX_HOME</c> <b>apenas</b> no processo
/// filho — nunca globalmente — para isolar login/refresh do slot ativo real.
/// Ver BUSINESS_RULES.md §5, §6 e ponto 7.
/// </summary>
public interface ICodexCli
{
    /// <summary>O binário codex foi localizado (PATH ou override configurado)?</summary>
    bool IsAvailable { get; }

    /// <summary>Caminho resolvido do binário, ou null se não encontrado.</summary>
    string? ResolvedPath { get; }

    /// <summary>
    /// Inicia um login OAuth (ChatGPT) via <c>codex app-server</c> com CODEX_HOME isolado. Faz o
    /// handshake (initialize + account/login/start) e retorna a sessão já com a URL de autorização,
    /// que o cliente deve abrir no WebView2 efêmero. O codex NÃO abre o navegador do sistema (evita
    /// contaminar o login com a sessão/cookies do navegador padrão). Ver §5.2.
    /// </summary>
    Task<ICodexLoginSession> StartChatGptLoginAsync(
        string codexHome,
        CancellationToken cancellationToken = default);
}
