using System.Globalization;

namespace CodexSwitcher.App.Localization;

/// <summary>
/// Localização simples pt/en decidida uma vez na inicialização pela cultura do sistema:
/// português apenas se o idioma for "pt" ou a região for Brasil; caso contrário, inglês.
/// Sem travessões (—) nos textos, por preferência do usuário.
/// </summary>
public sealed class Strings
{
    public static Strings Current { get; } = new();

    public bool Pt { get; }

    private Strings()
    {
        // Override manual opcional (CODEXSWITCHER_LANG=pt|en); caso contrário, detecção automática.
        var forced = Environment.GetEnvironmentVariable("CODEXSWITCHER_LANG");
        if (string.Equals(forced, "pt", StringComparison.OrdinalIgnoreCase)) { Pt = true; return; }
        if (string.Equals(forced, "en", StringComparison.OrdinalIgnoreCase)) { Pt = false; return; }

        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var isBrazil = false;
        try { isBrazil = RegionInfo.CurrentRegion.TwoLetterISORegionName.Equals("BR", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { /* região indisponível */ }
        Pt = lang.Equals("pt", StringComparison.OrdinalIgnoreCase) || isBrazil;
    }

    private string S(string pt, string en) => Pt ? pt : en;

    // Cabeçalho / barra de ferramentas
    public string AccountsHeader => S("Suas contas", "Your accounts");
    public string AccountsSubtitle => S(
        "Troque de conta do Codex com um clique, sem refazer o login.",
        "Switch Codex accounts in one click, no re-login.");
    public string SearchPlaceholder => S("Buscar…", "Search…");
    public string RefreshAll => S("Renovar todas", "Refresh all");
    public string RefreshAllTooltip => S("Renovar todas agora", "Refresh all now");
    public string SignInOAuth => S("Entrar com OAuth", "Sign in with OAuth");
    public string DetectAccount => S("Detectar conta", "Detect account");
    public string DetectAccountTooltip => S(
        "Procurar a conta logada no Codex agora e importar se for nova",
        "Look for the account logged into Codex now and import it if it is new");
    public string TwoFactorButton => S("2FA", "2FA");
    public string TwoFactorTooltip => S("Gerador de código 2FA", "2FA code generator");
    public string BrowserButton => S("Navegador", "Browser");
    public string BrowserTooltip => S(
        "Abrir navegador privado temporário", "Open temporary private browser");

    // Navegador privado (abas isoladas)
    public string BrowserWindowTitle => S("Navegador privado", "Private browser");
    public string BrowserNewTabHeader => S("Nova aba", "New tab");
    public string BrowserBackTooltip => S("Voltar", "Back");
    public string BrowserForwardTooltip => S("Avançar", "Forward");
    public string BrowserReloadTooltip => S("Recarregar", "Reload");
    public string BrowserAddressPlaceholder => S("Digite uma URL ou pesquise…", "Type a URL or search…");
    public string BrowserWebView2MissingHint => S(
        "Necessário para o navegador funcionar (o Windows 10 não vem com ele por padrão).",
        "Required for the browser to work (Windows 10 does not include it by default).");

    // Gerador 2FA (TOTP)
    public string TotpTitle => S("Gerador de código 2FA", "2FA code generator");
    public string TotpSubtitle => S(
        "Cole a chave e receba um código que renova sozinho.",
        "Paste your key and get a code that refreshes itself.");
    public string TotpSecretLabel => S("Chave secreta (2FA)", "2FA secret key");
    public string TotpSecretPlaceholder => S(
        "Cole a chave secreta ou um link otpauth://",
        "Paste the secret key or an otpauth:// link");
    public string TotpPaste => S("Colar da área de transferência", "Paste from clipboard");
    public string TotpCopy => S("Copiar código", "Copy code");
    public string TotpClickToCopy => S("Clique para copiar o código", "Click to copy the code");
    public string TotpCopied => S("Código copiado!", "Code copied!");
    public string TotpExpiresIn(int seconds) => S($"expira em {seconds}s", $"expires in {seconds}s");
    public string TotpInvalid => S(
        "Chave inválida. Confira se copiou a chave completa (Base32).",
        "Invalid key. Make sure you copied the full key (Base32).");
    public string TotpEmptyHint => S(
        "Cole a chave secreta do 2FA acima para gerar o código que renova a cada 30 segundos.",
        "Paste your 2FA secret key above to generate a code that refreshes every 30 seconds.");

    // Estado vazio
    public string EmptyTitle => S("Nenhuma conta ainda", "No accounts yet");
    public string EmptyBody => S(
        "Adicione sua primeira conta do Codex fazendo login numa sessão limpa, ou importe a conta que já está logada no seu computador.",
        "Add your first Codex account by signing in with a clean session, or import the account already logged in on this computer.");
    public string ImportCurrent => S("Importar conta atual", "Import current account");

    // Adoção
    public string AdoptTitle => S("Conta detectada no Codex", "Account detected in Codex");
    public string AdoptMessage => S(
        "Há uma conta logada no seu Codex que ainda não está no cofre.",
        "There is an account logged into Codex that is not in the vault yet.");
    public string Import => S("Importar", "Import");

    // Card
    public string Switch => S("Trocar", "Switch");
    public string InUse => S("Em uso", "In use");
    public string RefreshNow => S("Renovar agora", "Refresh now");
    public string Rename => S("Renomear", "Rename");
    public string MarkNeedsReLogin => S("Marcar: precisa re-login", "Mark: needs re-login");
    public string MarkUsed => S("Marcar como usado", "Mark as used");
    public string UnmarkUsed => S("Desmarcar como usado", "Unmark as used");
    public string Remove => S("Remover", "Remove");
    public string MoreActions => S("Mais ações", "More actions");

    // Selos
    public string BadgeActiveNow => S("Ativa agora", "Active now");
    public string BadgeHealthy => S("Pronta", "Ready");
    public string BadgeRenewSoon => S("Renovar em breve", "Renew soon");
    public string BadgeRefreshing => S("Renovando…", "Refreshing…");
    public string BadgeNeedsReLogin => S("Precisa re-login", "Needs re-login");
    public string BadgeError => S("Erro", "Error");
    public string BadgeUnavailable => S("Indisponível", "Unavailable");

    // Subtítulo / saúde do item
    public string CodexAccount => S("conta Codex", "Codex account");
    public string ModeFormat(string mode) => S($"modo {mode}", $"{mode} mode");
    public string NeverUsedHere => S("nunca usada aqui", "never used here");
    public string SwitchedFormat(string rel) => S($"trocou {rel}", $"switched {rel}");
    public string HealthNeedsReLogin => S("Sessão expirada, precisa entrar de novo", "Session expired, sign in again");
    public string HealthError => S("Erro ao processar esta conta", "Error processing this account");
    public string HealthRefreshing => S("Renovando…", "Refreshing…");
    public string HealthUnavailable => S("Indisponível neste usuário/máquina", "Unavailable on this user/machine");
    public string HealthNeverRefreshed => S("Ainda não renovada", "Not renewed yet");
    public string RefreshedFormat(string rel) => S($"Renovada {rel}", $"Renewed {rel}");
    public string SuffixCanExpire => S(" · pode expirar!", " · may expire!");
    public string SuffixRenewSoon => S(" · renove em breve", " · renew soon");

    // Ocupado
    public string BusyLoading => S("Carregando contas…", "Loading accounts…");
    public string BusySwitching(string name) => S($"Trocando para {name}…", $"Switching to {name}…");
    public string BusyRefreshingOne(string name) => S($"Renovando {name}…", $"Refreshing {name}…");
    public string BusyRefreshingAll => S("Renovando todas as contas…", "Refreshing all accounts…");
    public string BusyWaitingLogin => S("Aguardando login…", "Waiting for login…");
    public string BusyImporting => S("Importando conta atual…", "Importing current account…");
    public string BusyDetecting => S("Procurando conta logada no Codex…", "Looking for the account logged into Codex…");

    // InfoBar / resultados
    public string ErrorTitle => S("Erro", "Error");
    public string SwitchedTitle => S("Conta trocada", "Account switched");
    public string SwitchedMsg(string name) => S($"Conta trocada para {name}.", $"Switched to {name}.");
    public string SwitchReopenWarnTitle => S("Conta trocada (com aviso)", "Switched (with warning)");
    public string SwitchReopenWarnMsg => S(
        "Conta trocada, mas alguns apps não puderam ser reabertos automaticamente.",
        "Account switched, but some apps could not be reopened automatically.");
    public string SwitchRolledBackTitle => S("Troca revertida com segurança", "Switch safely rolled back");
    public string SwitchRolledBackMsg => S(
        "A troca falhou. A conta original foi restaurada e os apps reabertos. Nenhuma credencial foi perdida.",
        "The switch failed. The original account was restored and apps reopened. No credential was lost.");
    public string SwitchAbortedTitle => S("Troca abortada", "Switch aborted");
    public string SwitchAbortedMsg => S(
        "A troca foi abortada: ainda há um processo do Codex em execução. Nada foi alterado.",
        "The switch was aborted: a Codex process is still running. Nothing was changed.");
    public string SwitchFailedTitle => S("Falha na troca", "Switch failed");
    public string SwitchFailedMsg => S(
        "Não foi possível concluir a troca. Nada foi alterado.",
        "The switch could not be completed. Nothing was changed.");

    public string RenewedTitle => S("Renovado", "Renewed");
    public string NotRenewedTitle => S("Não renovado", "Not renewed");
    public string RefreshSuccessMsg(string name) => S($"{name} renovado.", $"{name} renewed.");
    public string RefreshNeedsReLoginMsg(string name) => S($"{name} precisa de novo login.", $"{name} needs a new login.");
    public string RefreshTransientMsg => S(
        "Falha temporária ao renovar. Tente novamente.", "Temporary refresh failure. Try again.");
    public string RefreshAllDoneTitle => S("Concluído", "Done");
    public string RefreshAllDoneMsg => S(
        "Renovação de todas as contas finalizada.", "Finished refreshing all accounts.");

    public string LoginCanceledTitle => S("Login cancelado", "Login canceled");
    public string LoginCanceledMsg => S("Nenhuma conta foi adicionada.", "No account was added.");
    public string AddedTitle => S("Conta adicionada", "Account added");
    public string AddedMsg(string name) => S($"{name} foi adicionada ao cofre.", $"{name} was added to the vault.");
    public string NicknameTitle => S("Dar um apelido", "Set a nickname");
    public string NicknamePrompt => S("Como quer chamar esta conta?", "What do you want to call this account?");
    public string Save => S("Salvar", "Save");
    public string ImportedTitle => S("Conta importada", "Account imported");
    public string ImportedMsg(string name) => S(
        $"{name} (já logada no Codex) foi adicionada.", $"{name} (already logged into Codex) was added.");
    public string DetectNoneTitle => S("Nenhuma conta logada", "No account logged in");
    public string DetectNoneMsg => S(
        "Não há nenhuma conta logada no Codex neste computador agora.",
        "There is no account logged into Codex on this computer right now.");
    public string DetectKnownTitle => S("Nada novo para importar", "Nothing new to import");
    public string DetectKnownMsg(string name) => S(
        $"A conta logada no Codex ({name}) já está no cofre.",
        $"The account logged into Codex ({name}) is already in the vault.");

    public string RenameTitle => S("Renomear conta", "Rename account");
    public string RenamePrompt => S("Novo apelido:", "New nickname:");
    public string RemoveTitle => S("Remover conta", "Remove account");
    public string RemoveConfirm(string name) => S(
        $"Remover \"{name}\" do cofre? O login cifrado desta conta será apagado.",
        $"Remove \"{name}\" from the vault? This account's encrypted login will be deleted.");
    public string RemovedTitle => S("Conta removida", "Account removed");
    public string RemovedMsg(string name) => S($"{name} foi removida do cofre.", $"{name} was removed from the vault.");

    // Diálogo de confirmação do switch
    public string Confirm => S("Confirmar", "Confirm");
    public string Cancel => S("Cancelar", "Cancel");
    public string ConfirmSwitchTitle(string name) => S($"Trocar para {name}?", $"Switch to {name}?");
    public string CurrentAccount(string name) => S($"Conta atual: {name}", $"Current account: {name}");
    public string NoManaged => S("nenhuma gerenciada", "none managed");
    public string WillCloseReopen => S("Serão fechados e reabertos automaticamente:", "Will be closed and reopened automatically:");
    public string WillCloseCli => S("Serão fechados (CLI não é reaberta):", "Will be closed (CLI is not reopened):");
    public string NoAppsRunning => S("Nenhum app do Codex em execução foi detectado.", "No running Codex app was detected.");
    public string CliActiveTitle => S("Sessão de CLI ativa", "Active CLI session");
    public string CliActiveMsg => S(
        "Trabalho não salvo na CLI do Codex pode se perder ao fechar.",
        "Unsaved work in the Codex CLI may be lost when closing.");
    public string IdeNote => S(
        "A extensão de IDE não será fechada. Recarregue a janela do editor após a troca, se usar a extensão.",
        "The IDE extension will not be closed. Reload your editor window after switching if you use the extension.");
    public string AppLabel(string names) => S($"App {names}", $"App {names}");

    // Janela de login
    public string LoginTitle => S("Entrar em uma conta Codex", "Sign in to a Codex account");
    public string LoginPreparing => S("Preparando sessão de login limpa…", "Preparing a clean login session…");
    public string LoginCleanNote => S(
        "Sessão anônima e descartável, sem cookies nem histórico.",
        "Anonymous, disposable session, no cookies or history.");
    public string LoginWaitingPage => S("Aguardando a página de login…", "Waiting for the login page…");
    public string LoginCodexNotFound => S("O binário do Codex não foi encontrado no PATH.", "The Codex binary was not found on PATH.");
    public string LoginInstallCodex => S(
        "Instale o Codex CLI ou configure o caminho nas preferências.",
        "Install the Codex CLI or set its path in preferences.");
    public string LoginWebView2Failed => S("Não foi possível iniciar o WebView2.", "Could not start WebView2.");
    public string LoginWebView2Hint => S(
        "Verifique se o WebView2 Runtime (Evergreen) está instalado. ",
        "Make sure the WebView2 Runtime (Evergreen) is installed. ");
    public string LoginWebView2Missing => S(
        "O WebView2 Runtime não está instalado neste Windows.",
        "The WebView2 Runtime is not installed on this Windows.");
    public string LoginWebView2MissingHint => S(
        "Necessário para abrir a página de login (o Windows 10 não vem com ele por padrão).",
        "Required to open the sign-in page (Windows 10 does not include it by default).");
    public string LoginWebView2InstallButton => S("Baixar e instalar", "Download and install");
    public string LoginWebView2Installing => S(
        "Baixando e instalando o WebView2 Runtime…", "Downloading and installing the WebView2 Runtime…");
    public string LoginWebView2InstallFailed => S(
        "Não foi possível instalar automaticamente. Tente novamente ou instale manualmente pelo site da Microsoft.",
        "Could not install it automatically. Try again or install it manually from Microsoft's site.");
    public string LoginCleanOpened => S(
        "Sessão limpa aberta. Entre na sua conta do ChatGPT para continuar.",
        "Clean session open. Sign in to your ChatGPT account to continue.");
    public string LoginCleanHint => S(
        "Sem cookies ou histórico anteriores. Ao concluir, esta janela fecha sozinha.",
        "No prior cookies or history. When done, this window closes itself.");
    public string LoginCompleting => S("Concluindo login com segurança…", "Completing login securely…");
    public string LoginFailed => S("Não foi possível concluir o login.", "Could not complete the login.");
    public string LoginFailedHint => S(
        "Feche esta janela e tente novamente. Sua sessão continua limpa e nada foi alterado.",
        "Close this window and try again. Your session stays clean and nothing was changed.");
    public string Close => S("Fechar", "Close");
    public string ImportAccounts => S("Importar", "Import");
    public string ImportAccountsTooltip => S("Importar contas exportadas do Codex Switcher", "Import accounts exported by Codex Switcher");
    public string ExportAll => S("Exportar todas", "Export all");
    public string ExportAllTooltip => S("Exportar todas as contas do cofre", "Export all vault accounts");
    public string Export => S("Exportar", "Export");
    public string HealthSaved => S("Credencial salva no cofre", "Credential saved in vault");
    public string BusyExporting => S("Exportando contas...", "Exporting accounts...");
    public string ImportedAccountsMsg(int count) => S(
        $"{count} conta(s) foram importadas para o cofre.", $"{count} account(s) were imported into the vault.");
    public string ExportTitle => S("Exportar credenciais", "Export credentials");
    public string ExportWarning => S(
        "O arquivo exportado contém tokens de acesso em texto recuperável. Guarde-o como uma senha e não o envie para outras pessoas.",
        "The exported file contains recoverable access tokens. Store it like a password and do not send it to others.");
    public string ExportOneWarning(string name) => S($"Exportar {name}? {ExportWarning}", $"Export {name}? {ExportWarning}");
    public string ExportedTitle => S("Contas exportadas", "Accounts exported");
    public string ExportedAllMsg(int count) => S($"{count} conta(s) foram exportadas.", $"{count} account(s) were exported.");
    public string ExportedOneMsg(string name) => S($"{name} foi exportada.", $"{name} was exported.");
    public string Cancelar => Cancel;
}
