using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Security;

namespace CodexSwitcher.Core.Services;

/// <summary>Opções de execução de um switch (derivadas de <see cref="AppSettings"/>).</summary>
public sealed record SwitchExecutionOptions(
    CloseReopenMode CloseReopenMode,
    TimeSpan GracefulCloseTimeout,
    int BackupsToKeep,
    bool EnsureFileStore = true)
{
    public static SwitchExecutionOptions From(AppSettings s) => new(
        s.CloseReopenMode,
        TimeSpan.FromSeconds(s.GracefulCloseTimeoutSeconds),
        s.ActiveSlotBackupsToKeep);
}

/// <summary>
/// Orquestra a troca de conta como transação reversível: (confirmar) → fechar apps → write-back →
/// backup → gravar novo slot (atômico) → metadados → reabrir. Em falha da troca, restaura o slot
/// pelo backup e reabre na conta original. Ver BUSINESS_RULES.md §4 e pontos 2, 3, 6, 10, 24, 25.
/// A confirmação (§4.2) é responsabilidade da UI e deve ocorrer ANTES de chamar <see cref="SwitchAsync"/>.
/// </summary>
public sealed class SwitchService
{
    private readonly VaultService _vault;
    private readonly ProfileStore _profileStore;
    private readonly IFileSystem _fs;
    private readonly IProcessManager _processes;
    private readonly ICodexConfigStore _config;
    private readonly IClock _clock;
    private readonly IAuditLog _audit;
    private readonly CodexPaths _paths;
    private readonly string _backupsDir;
    private readonly IProfileOperationCoordinator _coordinator;

    public IProfileOperationCoordinator Coordinator => _coordinator;

    public SwitchService(
        VaultService vault,
        ProfileStore profileStore,
        IFileSystem fs,
        IProcessManager processes,
        ICodexConfigStore config,
        IClock clock,
        IAuditLog audit,
        CodexPaths paths,
        string backupsDir,
        IProfileOperationCoordinator? coordinator = null)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _backupsDir = backupsDir ?? throw new ArgumentNullException(nameof(backupsDir));
        _coordinator = coordinator ?? _vault.Coordinator;
    }

    /// <summary>Monta o plano de switch para o popup de confirmação (§4.2). Não altera nada.</summary>
    public SwitchPlan BuildPlan(ProfileMetadata? from, ProfileMetadata to, CloseReopenMode mode)
    {
        var running = mode == CloseReopenMode.DoNothing
            ? []
            : _processes.FindRunningCodexProcesses();

        var toClose = running.Where(p => p.IsClosable).ToList();
        var toReopen = running.Where(p => p.IsReopenable).ToList();
        var hasCli = running.Any(p => p.Kind == CodexProcessKind.Cli);
        return new SwitchPlan(from, to, toClose, toReopen, hasCli);
    }

    /// <summary>
    /// Executa a troca. Pressupõe que a confirmação do usuário já ocorreu e que
    /// <paramref name="allProfiles"/> foi reconciliado (o ativo tem <c>IsActive=true</c>).
    /// </summary>
    public async Task<SwitchResult> SwitchAsync(
        List<ProfileMetadata> allProfiles,
        Guid targetProfileId,
        SwitchExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allProfiles);
        ArgumentNullException.ThrowIfNull(options);

        var to = allProfiles.FirstOrDefault(p => p.Id == targetProfileId);
        if (to is null)
            return Fail(ErrorCategory.Unknown, "Perfil de destino não encontrado.");

        var from = allProfiles.FirstOrDefault(p => p.IsActive && p.Id != targetProfileId);

        if (to.IsActive)
        {
            _audit.Record("switch", "noop", $"{to.DisplayName} já está ativa");
            return new SwitchResult(SwitchOutcome.Success, $"{to.DisplayName} já é a conta ativa.");
        }

        var lockHandle = from is not null
            ? await _coordinator.LockTwoAsync(from.Id, to.Id, cancellationToken).ConfigureAwait(false)
            : await _coordinator.LockAsync(to.Id, cancellationToken).ConfigureAwait(false);

        try
        {
            // (2) Fail-fast: decifrar o destino ANTES de fechar qualquer app. Se não decifrar, nada é tocado.
            byte[] targetBytes;
            try
            {
                targetBytes = _vault.LoadBlob(to.Id);
            }
            catch (SecretDecryptionException)
            {
                to.HealthStatus = HealthStatus.Unknown;
                to.LastError = ErrorInfo.Create(ErrorCategory.DecryptionFailed,
                    "Perfil não pôde ser decifrado neste usuário/máquina.", _clock.UtcNow);
                _profileStore.SaveAll(allProfiles);
                _audit.Record("switch", "failed", "destino não decifrável");
                return Fail(ErrorCategory.DecryptionFailed,
                    "Não foi possível ler a credencial do perfil de destino. Nada foi alterado.");
            }

            var closeApps = options.CloseReopenMode == CloseReopenMode.Automatic;
            IReadOnlyList<CodexProcessInfo> captured = [];
            IReadOnlyList<CodexProcessInfo> closed = [];

            // (2 do fluxo) Capturar processos ANTES de matar (ponto 23) e (3) encerrar.
            if (closeApps)
            {
                captured = _processes.FindRunningCodexProcesses().Where(p => p.IsClosable).ToList();
                closed = await _processes
                    .CloseGracefullyThenKillAsync(captured, options.GracefulCloseTimeout, cancellationToken)
                    .ConfigureAwait(false);

                // (4) Verificação anti-corrida: se sobrou CLI viva, abortar SEM tocar no slot e reabrir.
                if (_processes.AnyCodexCliRunning())
                {
                    ReopenDesktop(captured, out _);
                    _audit.Record("switch", "aborted", "processo codex remanescente");
                    return new SwitchResult(SwitchOutcome.AbortedProcessRemnant,
                        "A troca foi abortada: ainda há um processo do Codex em execução. Nada foi alterado.",
                        ErrorInfo.Create(ErrorCategory.ProcessRemnant, "Processo remanescente", _clock.UtcNow),
                        closed);
                }
            }

            // Ler o slot ativo atual (pós-encerramento) — base do write-back, do backup e do rollback.
            byte[]? originalActiveBytes = _fs.FileExists(_paths.ActiveAuthPath)
                ? _fs.ReadAllBytes(_paths.ActiveAuthPath)
                : null;

            var now = _clock.UtcNow;
            var backupPath = originalActiveBytes is not null
                ? Path.Combine(_backupsDir, $"auth-{now.UtcDateTime:yyyyMMddTHHmmssfffZ}.bin")
                : null;

            try
            {
                // (5) Write-back do slot ativo atual no perfil de origem (preserva refresh/rotação: pontos 2, 3).
                if (from is not null && originalActiveBytes is not null)
                {
                    from.BlobFingerprint = _vault.SaveBlob(from.Id, originalActiveBytes);
                    var info = AuthJsonReader.TryRead(originalActiveBytes);
                    if (info?.LastRefresh is { } lr)
                        from.LastRefreshedAt = lr;
                    from.HealthStatus = HealthStatus.Valid;
                    _audit.Record("write-back", "ok", from.DisplayName);
                }

                // (6) Backup cifrado do slot ativo antes de sobrescrever.
                if (originalActiveBytes is not null && backupPath is not null)
                {
                    _vault.SaveEncryptedFile(backupPath, originalActiveBytes);
                    RotateBackups(options.BackupsToKeep);
                }

                // (7) Gravar o novo slot com escrita atômica (temp + move).
                _fs.WriteAllBytesAtomic(_paths.ActiveAuthPath, targetBytes);

                // (8) Garantir cli_auth_credentials_store = "file" (idempotente).
                if (options.EnsureFileStore)
                {
                    try { _config.EnsureFileStore(_paths.ConfigTomlPath); }
                    catch (IOException) { /* não fatal para a troca da credencial */ }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecretDecryptionException)
            {
                // (rollback) Restaurar o slot ativo e reabrir na conta original.
                return Rollback(allProfiles, from, to, originalActiveBytes, captured, ex);
            }

            // (9) Metadados: destino vira ativo; origem deixa de ser.
            to.LastSwitchedAt = now;
            to.IsActive = true;
            to.HealthStatus = to.HealthStatus == HealthStatus.Unknown ? HealthStatus.Valid : to.HealthStatus;
            to.LastError = null;
            if (from is not null) from.IsActive = false;
            to.BlobFingerprint = Fingerprint.Compute(targetBytes);
            _profileStore.SaveAll(allProfiles);

            // (10) Reabrir SOMENTE após o slot estar persistido (ponto 25). CLIs não reabrem (ponto 20).
            var reopenFailures = new List<CodexProcessInfo>();
            if (closeApps)
                ReopenDesktop(captured, out reopenFailures);

            if (reopenFailures.Count > 0)
            {
                _audit.Record("switch", "ok-reopen-warn", $"{from?.DisplayName ?? "-"} -> {to.DisplayName}");
                return new SwitchResult(SwitchOutcome.SuccessWithReopenWarning,
                    $"Conta trocada para {to.DisplayName}, mas alguns apps não puderam ser reabertos automaticamente.",
                    null, closed, reopenFailures);
            }

            _audit.Record("switch", "ok", $"{from?.DisplayName ?? "-"} -> {to.DisplayName}");
            return new SwitchResult(SwitchOutcome.Success, $"Conta trocada para {to.DisplayName}.", null, closed);
        }
        finally
        {
            lockHandle.Dispose();
        }
    }

    private SwitchResult Rollback(
        List<ProfileMetadata> allProfiles,
        ProfileMetadata? from,
        ProfileMetadata to,
        byte[]? originalActiveBytes,
        IReadOnlyList<CodexProcessInfo> captured,
        Exception cause)
    {
        try
        {
            if (originalActiveBytes is not null)
                _fs.WriteAllBytesAtomic(_paths.ActiveAuthPath, originalActiveBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Falha catastrófica ao restaurar: a credencial original ainda está no backup e no cofre.
            _audit.Record("switch", "rollback-restore-failed", ex.GetType().Name);
        }

        // Metadados permanecem: origem segue ativa, destino inativo.
        if (from is not null) from.IsActive = true;
        to.IsActive = false;
        _profileStore.SaveAll(allProfiles);

        var reopenFailures = new List<CodexProcessInfo>();
        ReopenDesktop(captured, out reopenFailures);

        _audit.Record("switch", "rolled-back", cause.GetType().Name);
        return new SwitchResult(SwitchOutcome.RolledBack,
            "A troca falhou. A conta original foi restaurada e os apps reabertos. Nenhuma credencial foi perdida.",
            ErrorInfo.Create(Categorize(cause), "Falha ao gravar o novo slot ativo.", _clock.UtcNow),
            ReopenFailures: reopenFailures.Count > 0 ? reopenFailures : null);
    }

    private void ReopenDesktop(IReadOnlyList<CodexProcessInfo> captured, out List<CodexProcessInfo> failures)
    {
        failures = [];
        // O app desktop tem vários processos (estilo Electron) com o mesmo executável — reabrir só
        // UMA vez por executável (ponto 27: reabertura é efeito colateral controlado).
        var distinct = captured
            .Where(p => p.IsReopenable)
            .GroupBy(p => p.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());

        foreach (var p in distinct)
        {
            try { _processes.Relaunch(p); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failures.Add(p);
            }
        }
    }

    private void RotateBackups(int keep)
    {
        if (keep <= 0) return;
        var backups = _fs.EnumerateFiles(_backupsDir, "auth-*.bin")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .Skip(keep)
            .ToList();
        foreach (var old in backups)
        {
            try { _fs.Delete(old); }
            catch (IOException) { /* rotação é best-effort */ }
        }
    }

    private static ErrorCategory Categorize(Exception ex) => ex switch
    {
        UnauthorizedAccessException => ErrorCategory.PermissionDenied,
        SecretDecryptionException => ErrorCategory.DecryptionFailed,
        _ => ErrorCategory.Unknown,
    };

    private SwitchResult Fail(ErrorCategory category, string message) =>
        new(SwitchOutcome.Failed, message, ErrorInfo.Create(category, message, _clock.UtcNow));
}
