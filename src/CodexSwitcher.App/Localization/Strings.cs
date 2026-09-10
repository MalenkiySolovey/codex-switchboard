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
    public string DetectedAccountTitle => S("Conta atual do Codex detectada", "Current Codex account detected");
    public string DetectedAccountBody => S(
        "Uma sessão ativa do Codex foi encontrada neste computador. Deseja importá-la para o cofre seguro?",
        "An active Codex session was found on this computer. Would you like to import it into the secure vault?");

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
    public string BadgeStaleData => S("DADOS EM CACHE", "STALE DATA");
    public string BadgeRateLimited => S("LIMITE ATINGIDO", "RATE LIMITED");
    public string BadgeNeverLoaded => S("Cota ainda não consultada", "No usage data yet");
    public string LimitReached => S("LIMITE ATINGIDO", "LIMIT REACHED");
    public string QuotaHeader => S("Cota de uso", "Usage quota");
    public string RefreshQuotaTooltip => S("Atualizar cota agora", "Refresh quota now");

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
    public string WarningTitle => S("Aviso", "Warning");
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
        "O WebView2 Runtime não está instalado neste sistema.",
        "The WebView2 Runtime is not installed on this system.");
    public string LoginWebView2MissingHint => S(
        "O WebView2 Runtime é necessário apenas para adicionar contas via navegador. Suas contas existentes, troca de perfil e monitoramento de cota continuam funcionando normalmente.",
        "The WebView2 Runtime is required only for browser-based OAuth account additions. Your existing accounts, switching, and rate-limit monitoring continue to function normally without it.");
    public string LoginWebView2InstallButton => S("Abrir página da Microsoft", "Open Microsoft info page");
    public string LoginWebView2Installing => S(
        "Abrindo a página oficial da Microsoft…", "Opening official Microsoft page…");
    public string LoginWebView2InstallFailed => S(
        "Não foi possível abrir o navegador. Acesse manualmente o site da Microsoft para obter o WebView2 Runtime.",
        "Could not open browser. Please visit Microsoft's site manually to get WebView2 Runtime.");
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
    public string ImportAccountsTooltip => S("Importar contas exportadas (Codex Switchboard / Codex Switcher)", "Import exported accounts (Codex Switchboard / Codex Switcher)");
    public string ExportAll => S("Exportar todas", "Export all");
    public string ExportAllTooltip => S("Exportar todas as contas do cofre", "Export all vault accounts");
    public string Export => S("Exportar", "Export");
    public string HealthSaved => S("Credencial salva no cofre", "Credential saved in vault");
    public string BusyExporting => S("Exportando contas...", "Exporting accounts...");

    // Configurações e Sobre
    public string SettingsButton => S("Configurações", "Settings");
    public string SettingsButtonTooltip => S("Configurações e diagnóstico do sistema", "Settings and system diagnostics");
    public string SettingsTitle => S("Configurações e Sobre", "Settings & About");
    public string AppDescription => S(
        "Aplicativo Windows nativo para gerenciar contas do Codex, alternar credenciais com segurança e monitorar cotas.",
        "Native Windows app to securely manage multiple Codex accounts, switch active credentials, and monitor rate limits.");
    public string IndependenceNotice => S(
        "Projeto independente de código aberto. Não afiliado, endossado ou suportado pela OpenAI.",
        "Independent open-source project. Not affiliated with, endorsed by, or supported by OpenAI.");
    public string CodexRuntimeSection => S("Ambiente de Execução do Codex", "Codex Runtime Environment");
    public string DetectedRuntimeLabel => S("Executável detectado:", "Detected executable:");
    public string RuntimeVersionLabel => S("Versão detectada:", "Detected version:");
    public string RateLimitsCapabilityLabel => S("Monitoramento de cotas:", "Rate limits monitoring:");
    public string AccountActivityCapabilityLabel => S("Atividade histórica da conta:", "Account Activity:");
    public string CapabilitySupported => S("Suportado", "Supported");
    public string CapabilityUnsupported => S("Não suportado", "Unsupported");
    public string CapabilityUnavailable => S("Temporariamente indisponível", "Temporarily unavailable");
    public string CustomRuntimeOverrideLabel => S("Caminho personalizado do executável (opcional):", "Custom executable path override (optional):");
    public string BrowseButton => S("Procurar…", "Browse…");
    public string AutoDetectButton => S("Detecção automática", "Auto-detect");
    public string ApplyButton => S("Aplicar", "Apply");
    public string ExecutableInvalidMsg => S("O executável especificado não foi encontrado ou não é válido.", "The specified executable was not found or is invalid.");
    public string SettingsSavedMsg => S("Configurações salvas e diagnóstico atualizado.", "Settings saved and diagnostics updated.");
    public string LegacyMigrationSection => S("Migração de Dados Legados", "Legacy Data Migration");
    public string LegacyMigrationDetectedMsg => S(
        "Dados do Codex Account Switcher foram detectados no seu computador.",
        "Codex Account Switcher data was detected on this computer.");
    public string LegacyMigrationNoneMsg => S("Nenhum dado legado encontrado para migração.", "No legacy data found to migrate.");
    public string ImportLegacyButton => S("Importar dados do Codex Account Switcher", "Import data from Codex Account Switcher");
    public string MigrationSuccessMsg(int count) => S(
        $"{count} conta(s) migradas com sucesso para o Codex Switchboard. Os arquivos originais foram mantidos intactos.",
        $"Successfully migrated {count} account(s) to Codex Switchboard. Original files remain untouched.");
    public string MigrationFailedMsg(string reason) => S($"Falha na migração: {reason}", $"Migration failed: {reason}");
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

    // Phase 4: Atividade histórica da conta (account/usage/read)
    public string AccountActivityHeader => S("Atividade da conta", "Account activity");
    public string LifetimeTokensLabel => S("Total de tokens", "Lifetime tokens");
    public string PeakDailyTokensLabel => S("Pico diário", "Peak daily tokens");
    public string LongestTurnLabel => S("Maior turno", "Longest turn");
    public string CurrentStreakLabel => S("Sequência atual", "Current streak");
    public string LongestStreakLabel => S("Maior sequência", "Longest streak");
    public string DailyActivityLabel => S("Atividade diária", "Daily activity");
    public string ServerReportedDisclaimer => S("Atividade reportada pelo servidor • Fuso não convertido", "Server-reported activity • Dates not converted");
    public string NoActivityReported => S("Nenhuma atividade registrada", "No activity reported");

    // Phase 5.7: Reset credits & Subscription tracking
    public string ResetCreditsHeader => S("Créditos de reinício de cota", "Rate limit reset credits");
    public string ResetCreditsNone => S("Nenhum detalhe adicional informado pelo runtime", "No additional details reported by runtime");
    public string ResetCreditsUnreported(int count) => S(
        $"+ {count} crédito(s) adicional(is) não detalhado(s) pelo servidor",
        $"+ {count} additional credit(s) not detailed by server");

    public string SubscriptionTrackingLabel => S("Rastreamento de assinatura…", "Subscription tracking…");
    public string SubscriptionTrackingTitle => S("Rastreamento de Assinatura", "Subscription Tracking");
    public string SubscriptionTrackingDisclaimer => S(
        "Rastreamento local fornecido pelo usuário. O Codex não expõe datas de faturamento do ChatGPT. As datas informadas são salvas apenas localmente neste computador.",
        "User-provided local tracking. Codex does not expose ChatGPT billing dates. The entered dates are stored locally on this computer only.");
    public string SubscriptionModeLabel => S("Tipo de evento:", "Date type:");
    public string SubscriptionModeRenewal => S("Renovação", "Renewal");
    public string SubscriptionModeExpiration => S("Expiração", "Expiration");
    public string SubscriptionStartedLabel => S("Data de início (opcional):", "Started date (optional):");
    public string SubscriptionTargetLabel => S("Data de renovação/expiração:", "Renewal/expiration date:");
    public string SubscriptionEstimateButton => S("Estimar próxima renovação mensal", "Estimate next monthly renewal");
    public string SubscriptionManageLink => S("Gerenciar assinatura oficial no ChatGPT", "Manage official subscription on ChatGPT");
    public string SubscriptionClearButton => S("Limpar rastreamento", "Clear tracking");
    public string SubscriptionEstimatedNotice => S("(data estimada mensalmente)", "(monthly estimated date)");

    // Phase 8: Cards de conta compactos e expansíveis
    public string ExpandAccountDetails => S("Expandir detalhes da conta", "Expand account details");
    public string CollapseAccountDetails => S("Recolher detalhes da conta", "Collapse account details");
    public string CompactRemainingFormat(double pct) => S($"{Math.Round(pct)}% disp.", $"{Math.Round(pct)}% rem.");
    public string CompactResetCreditsFormat(int count) => S(
        count == 1 ? "1 reinício" : $"{count} reinícios",
        count == 1 ? "1 reset" : $"{count} resets");
    public string AdditionalQuotaWindowsHeader => S("Janelas de cota adicionais:", "Additional quota windows:");

    // Phase 9: 2FA / TOTP associado ao perfil
    public string AddTotpKey => S("Adicionar chave 2FA…", "Add 2FA key…");
    public string ManageTotpKey => S("Gerenciar 2FA…", "Manage 2FA…");
    public string TotpKeySavedLocally => S("Chave 2FA salva localmente", "2FA key saved locally");
    public string ReplaceTotpKey => S("Substituir chave", "Replace key");
    public string RemoveTotpKey => S("Remover chave salva", "Remove saved key");
    public string RevealTotpCode => S("Revelar código 2FA", "Reveal 2FA code");
    public string HideTotpCode => S("Ocultar código 2FA", "Hide 2FA code");
    public string CopyTotpCode => S("Copiar código 2FA", "Copy 2FA code");
    public string TotpCodeCopied => S("Copiado!", "Copied!");
    public string TotpHiddenPlaceholder => "••• •••";
    public string TotpSecurityWarning => S(
        "Salvar a chave 2FA aqui permite ao Switchboard gerar códigos de autenticação localmente. A chave é protegida para o seu usuário do Windows usando DPAPI. Manter as credenciais da conta e a chave 2FA no mesmo computador traz conveniência, mas reduz a separação física entre os fatores de autenticação.",
        "Saving a 2FA key here lets Switchboard generate authentication codes locally. The key is encrypted for your Windows user using DPAPI. Keeping both account credentials and the 2FA key on the same computer is more convenient, but reduces separation between authentication factors.");
    public string TotpRemoveConfirmationTitle => S("Remover chave 2FA local?", "Remove local 2FA key?");
    public string TotpRemoveConfirmationMessage => S(
        "Isso removerá apenas a cópia salva no Switchboard deste computador. O 2FA NÃO será desativado na sua conta da OpenAI/ChatGPT.",
        "This will remove only Switchboard's locally saved copy on this computer. 2FA will NOT be disabled on your OpenAI/ChatGPT account.");
    public string TotpTimeSyncNotice => S(
        "Se os códigos forem rejeitados pelo serviço, verifique se a data e o horário do Windows estão sincronizados.",
        "If codes are rejected by the service, verify that Windows date and time are synchronized.");
    public string TotpEnterKeyPrompt => S(
        "Cole a chave Base32 ou o link otpauth:// fornecido nas configurações de segurança da sua conta:",
        "Paste the Base32 secret key or otpauth:// link provided in your account security settings:");
    public string TotpSetupTitle => S("Configurar 2FA do Perfil", "Profile 2FA Setup");
    public string TotpManageTitle => S("Gerenciar 2FA do Perfil", "Manage Profile 2FA");
    public string TotpSaveKeyButton => S("Salvar chave", "Save key");
    public string TotpConfirmRemoveButton => S("Remover chave local", "Remove local key");

    // Phase 9.1, 9.2 & 9.3: Portão de verificação de usuário do Windows com suporte a Hello e senha da conta
    public string TotpProtectionHeader => S("Proteção dos códigos 2FA", "2FA code protection");
    public string RequireWindowsVerificationLabel => S("Exigir verificação do Windows antes de exibir códigos 2FA", "Require Windows verification before showing 2FA codes");
    public string WindowsVerificationDescription => S("O Windows Hello/PIN é usado quando disponível. Caso contrário, o Switchboard pode verificar a senha da sua conta atual do Windows.", "Windows Hello/PIN is used when available. Otherwise Switchboard can verify the password of your current Windows account.");
    public string WindowsVerificationFallbackNote => S("Se a infraestrutura de verificação do Windows apresentar falha técnica, seus códigos 2FA continuam acessíveis pela revelação única de emergência.", "If the Windows verification infrastructure experiences a technical failure, your 2FA codes remain accessible through one-time emergency reveal.");
    public string WindowsVerificationDurationLabel => S("Validade da verificação:", "Verification remains valid for:");
    public string WindowsVerificationDurationMinute(int min) => S($"{min} minuto{(min > 1 ? "s" : "")}", $"{min} minute{(min > 1 ? "s" : "")}");
    public string WindowsVerificationPromptMessage => S("Verifique sua identidade do Windows para revelar os códigos 2FA do Codex Switchboard.", "Verify your Windows identity to reveal Codex Switchboard 2FA codes.");
    public string WindowsVerificationTwoTimerNotice => S("Cada código revelado continua sendo ocultado automaticamente após 10 segundos.", "Each revealed code is still automatically hidden after 10 seconds.");
    public string WindowsVerificationHelpTooltip => S("O Windows gerencia a verificação. Dependendo da sua configuração, você pode ser solicitado a usar o Windows Hello, um PIN ou a senha da sua conta do Windows.", "Windows handles the verification. Depending on your sign-in configuration, you may be asked for Windows Hello, a PIN, or your Windows account password.");
    public string WindowsVerificationCanceled => S("A verificação do Windows foi cancelada.", "Windows verification was canceled.");
    public string WindowsVerificationFailed => S("Falha na verificação do Windows.", "Windows verification failed.");
    public string WindowsPasswordIncorrect => S("A senha do Windows não foi aceita.", "The Windows password was not accepted.");
    public string WindowsDifferentUser => S("As credenciais pertencem a outro usuário do Windows.", "The credentials belong to a different Windows user.");
    public string WindowsAccountLocked => S("A conta do Windows está bloqueada.", "The Windows account is locked.");
    public string WindowsPasswordExpired => S("A senha do Windows expirou ou precisa ser alterada.", "The Windows password has expired or must be changed.");
    public string WindowsAccountRestricted => S("Restrições da conta do Windows impedem o logon.", "Windows account restrictions prevent logon.");
    public string WindowsAuthSystemError => S("O Windows não pôde verificar as credenciais devido a uma falha no sistema.", "Windows could not verify these credentials due to a system error.");
    public string WindowsVerificationDegradedNotice => S("A verificação do Windows não pôde ser iniciada devido a uma falha técnica. O código será exibido uma vez usando a proteção padrão de 10 segundos.", "Windows verification could not be started due to a technical failure. The code will be shown once using standard 10-second protection.");
    public string WindowsVerificationDegradedStatusMessage => S("Os códigos permanecem acessíveis usando a proteção de revelação padrão de 10 segundos.", "Codes remain accessible using standard 10-second reveal protection.");
    public string WindowsVerificationTransientTitle => S("Verificação temporariamente indisponível", "Windows verification could not be started");
    public string WindowsVerificationTransientMessage => S("Não foi possível iniciar a verificação do Windows.\n\nSeu código 2FA ainda pode ser exibido uma vez usando a proteção padrão de 10 segundos.\nIsso não desativa a verificação do Windows.", "Windows verification could not be started.\n\nYour 2FA code can still be shown once using the standard 10-second protection.\nThis does not disable Windows verification.");
    public string WindowsVerificationTryAgainButton => S("Tentar novamente", "Try again");
    public string WindowsVerificationShowCodeOnceButton => S("Exibir código uma vez", "Show code once");
    public string CheckAgainButton => S("Verificar novamente", "Check again");
    public string OpenWindowsSignInOptionsButton => S("Abrir opções de entrada do Windows", "Open Windows sign-in options");
    public string WindowsVerificationDisablePasswordPrompt => S("Informe a senha do seu usuário atual do Windows para desativar a proteção dos códigos 2FA.", "Enter your current Windows user password to disable 2FA code protection.");
    public string WindowsVerificationDisableFailed => S("A senha do Windows não pôde ser validada. A proteção de códigos 2FA continua ativada.", "Windows password could not be verified. 2FA code protection remains enabled.");
    public string WindowsVerificationStatusLabel => S("Status da verificação:", "Verification status:");
    public string WindowsHelloMethodLabel => S("Windows Hello / PIN:", "Windows Hello / PIN:");
    public string WindowsPasswordMethodLabel => S("Senha da conta do Windows:", "Windows account password:");
    public string WindowsMethodReady => S("Disponível", "Available");
    public string WindowsHelloNotConfiguredUsingPassword => S("Não configurado — senha do Windows será usada", "Not configured — Windows password will be used");
    public string WindowsHelloNotAvailable => S("Não disponível neste computador", "Not available on this PC");
    public string WindowsHelloAvailable => S("Disponível", "Available");
    public string WindowsVerificationStatusAvailable => S("Disponível", "Available");
    public string WindowsVerificationStatusDeviceNotPresent => S("Windows Hello/PIN não está configurado — senha do Windows será usada", "Windows Hello/PIN is not configured — Windows password will be used");
    public string WindowsVerificationStatusNotConfigured => S("Windows Hello/PIN não está configurado — senha do Windows será usada", "Windows Hello/PIN is not configured — Windows password will be used");
    public string WindowsVerificationStatusDisabledByPolicy => S("Desativada por política do Windows", "Disabled by Windows policy");
    public string WindowsVerificationStatusDeviceBusy => S("Temporariamente ocupado", "Temporarily busy");
    public string WindowsVerificationStatusUnsupported => S("Não compatível nesta versão do Windows", "Not supported on this Windows version");
}
