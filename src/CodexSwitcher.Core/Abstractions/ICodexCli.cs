namespace CodexSwitcher.Core.Abstractions;

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
