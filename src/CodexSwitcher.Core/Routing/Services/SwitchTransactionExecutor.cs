using System.Security.Cryptography;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Core.Routing.Services;

/// <summary>
/// Production implementation of <see cref="ISwitchTransactionExecutor"/> coordinating
/// atomic resource mutations, process lifecycles, auth.json integrity verification,
/// and reverse-order multi-resource rollbacks.
/// JOURNAL_READY_BOUNDARY = YES.
/// </summary>
public sealed class SwitchTransactionExecutor : ISwitchTransactionExecutor
{
    private readonly SwitchService _chatGptSwitchService;
    private readonly ICodexRoutingConfigStore _routingConfig;
    private readonly IApiProviderStore _apiProviderStore;
    private readonly IProcessManager _processes;
    private readonly IFileSystem _fs;
    private readonly IClock _clock;
    private readonly IAuditLog _audit;
    private readonly CodexPaths _paths;
    private readonly Func<ISwitchCompensationCoordinator>? _coordinatorFactory;
    private readonly ICodexModelCatalogService? _modelCatalogService;

    public SwitchTransactionExecutor(
        SwitchService chatGptSwitchService,
        ICodexRoutingConfigStore routingConfig,
        IApiProviderStore apiProviderStore,
        IProcessManager processes,
        IFileSystem fs,
        IClock clock,
        IAuditLog audit,
        CodexPaths paths,
        Func<ISwitchCompensationCoordinator>? coordinatorFactory = null,
        ICodexModelCatalogService? modelCatalogService = null)
    {
        _chatGptSwitchService = chatGptSwitchService ?? throw new ArgumentNullException(nameof(chatGptSwitchService));
        _routingConfig = routingConfig ?? throw new ArgumentNullException(nameof(routingConfig));
        _apiProviderStore = apiProviderStore ?? throw new ArgumentNullException(nameof(apiProviderStore));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _coordinatorFactory = coordinatorFactory;
        _modelCatalogService = modelCatalogService;
    }

    private ISwitchCompensationCoordinator CreateCompensationCoordinator() =>
        _coordinatorFactory != null
            ? _coordinatorFactory()
            : new SwitchCompensationCoordinator(_routingConfig, _processes, _audit, _paths);

    public async Task<TargetSwitchResult> ExecuteAsync(CodexSwitchPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return plan switch
        {
            InvalidSwitchPlan invalidPlan => ExecuteInvalidPlan(invalidPlan),
            ChatGptNoOpSwitchPlan noOpPlan => ExecuteChatGptNoOp(noOpPlan),
            ApiRouteSwitchPlan routePlan => ExecuteApiRouteSwitch(routePlan),
            ApiProviderSwitchPlan apiPlan => await ExecuteApiProviderSwitchAsync(apiPlan, cancellationToken).ConfigureAwait(false),
            ChatGptAccountSwitchPlan accountPlan => await ExecuteChatGptAccountSwitchAsync(accountPlan, cancellationToken).ConfigureAwait(false),
            ChatGptReturnRoutingSwitchPlan returnPlan => await ExecuteChatGptReturnRoutingAsync(returnPlan, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported switch plan type '{plan.GetType().Name}'.")
        };
    }

    private TargetSwitchResult ExecuteInvalidPlan(InvalidSwitchPlan invalidPlan)
    {
        return new TargetSwitchResult(
            TargetSwitchOutcome.Failed,
            invalidPlan.ErrorMessage,
            invalidPlan.Target,
            ErrorInfo.Create(invalidPlan.Category, invalidPlan.ErrorMessage, _clock.UtcNow));
    }

    private static TargetSwitchResult ExecuteChatGptNoOp(ChatGptNoOpSwitchPlan noOpPlan)
    {
        return new TargetSwitchResult(
            TargetSwitchOutcome.Success,
            $"{noOpPlan.ActiveProfile?.DisplayName ?? "ChatGPT"} já é a conta ativa.",
            new ActiveTarget.ChatGpt(noOpPlan.ActiveProfile?.Id, noOpPlan.ActiveProfile?.AccountEmail));
    }

    private TargetSwitchResult ExecuteApiRouteSwitch(ApiRouteSwitchPlan routePlan)
    {
        var profile = routePlan.Profile;
        profile.SelectedRouteId = routePlan.RouteId;
        profile.BaseUrl = routePlan.NewBaseUrl;
        _apiProviderStore.Save(profile);

        if (routePlan.IsCurrentlyActiveInToml)
        {
            _routingConfig.UpdateProviderRoute(_paths.ConfigTomlPath, profile.StableCodexProviderId, routePlan.NewBaseUrl);
        }

        _audit.Record("route-switch", "ok", $"{profile.DisplayName} -> {routePlan.RouteId} ({routePlan.NewBaseUrl})");
        return new TargetSwitchResult(
            TargetSwitchOutcome.Success,
            $"Switched {profile.DisplayName} route to {routePlan.RouteId}.",
            new ActiveTarget.Api(profile));
    }

    private async Task<TargetSwitchResult> ExecuteApiProviderSwitchAsync(
        ApiProviderSwitchPlan apiPlan,
        CancellationToken cancellationToken)
    {
        var targetProfile = apiPlan.TargetProfile;
        var options = apiPlan.Options;
        var model = targetProfile.SelectedModel ?? "gpt-5.6-sol";
        var modelCatalogJson = _modelCatalogService?.EnsureModelCatalog(model, targetProfile.ModelOverrides?.ContextWindowTokens, targetProfile.ModelOverrides);

        var currentRouting = _routingConfig.ReadRoutingState(_paths.ConfigTomlPath);
        var oldCatalogHash = GetFileSha256(currentRouting.ModelCatalogJson);
        var newCatalogHash = GetFileSha256(modelCatalogJson);
        var catalogChanged = !string.Equals(oldCatalogHash, newCatalogHash, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(currentRouting.ModelCatalogJson) != !string.IsNullOrWhiteSpace(modelCatalogJson));
        var closeApps = options.CloseReopenMode == CloseReopenMode.Automatic || catalogChanged;

        var compensator = CreateCompensationCoordinator();
        IReadOnlyList<CodexProcessInfo> captured = [];
        IReadOnlyList<CodexProcessInfo> closed = [];

        // 1. Capture & close Codex processes
        if (closeApps)
        {
            captured = _processes.FindRunningCodexProcesses().Where(p => p.IsClosable).ToList();
            closed = await _processes.CloseGracefullyThenKillAsync(captured, options.GracefulCloseTimeout, cancellationToken).ConfigureAwait(false);
            compensator.RegisterCapturedProcesses(captured);

            if (_processes.AnyCodexCliRunning())
            {
                compensator.ReopenDesktop(captured, out _);
                _audit.Record("api-switch", "aborted", "process remnant");
                return new TargetSwitchResult(
                    TargetSwitchOutcome.AbortedProcessRemnant,
                    "Switch aborted: Codex CLI process is still running. No changes made.",
                    new ActiveTarget.Api(targetProfile),
                    ErrorInfo.Create(ErrorCategory.ProcessRemnant, "Process remnant", _clock.UtcNow),
                    closed);
            }
        }

        // 2. Assert auth.json pre-switch hash
        byte[]? authPreBytes = _fs.FileExists(_paths.ActiveAuthPath)
            ? _fs.ReadAllBytes(_paths.ActiveAuthPath)
            : null;
        byte[]? authPreHash = authPreBytes is not null ? SHA256.HashData(authPreBytes) : null;

        // 3. Read and backup config.toml exact bytes
        byte[]? configOriginalBytes = _fs.FileExists(_paths.ConfigTomlPath)
            ? _fs.ReadAllBytes(_paths.ConfigTomlPath)
            : null;

        if (configOriginalBytes is not null)
        {
            compensator.RegisterConfigBackup(configOriginalBytes);
        }

        try
        {
            var transport = targetProfile.TransportOverrides;
            var providerBlock = new CodexProviderBlock(
                targetProfile.StableCodexProviderId,
                targetProfile.DisplayName,
                targetProfile.BaseUrl,
                targetProfile.WireApi,
                apiPlan.BrokerPath,
                new[] { "--key-id", targetProfile.Id.ToString("D") },
                5000,
                transport?.RequestMaxRetries,
                transport?.StreamMaxRetries,
                transport?.StreamIdleTimeoutMs,
                transport?.WebSocketConnectTimeoutMs,
                transport?.SupportsWebSockets,
                transport?.SupportsStandaloneWebSearch,
                transport?.QueryParams,
                transport?.HttpHeaders,
                transport?.EnvHttpHeaders,
                transport?.ResponsesPolicy ?? ResponsesCompatibilityPolicy.Auto);

            _routingConfig.ApplySwitchboardRouting(_paths.ConfigTomlPath, providerBlock, model, targetProfile.ModelOverrides, modelCatalogJson);

            // 4. Verify auth.json is UNTOUCHED
            if (authPreHash is not null)
            {
                var authPostBytes = _fs.ReadAllBytes(_paths.ActiveAuthPath);
                var authPostHash = SHA256.HashData(authPostBytes);
                if (!CryptographicOperations.FixedTimeEquals(authPreHash, authPostHash))
                {
                    throw new InvalidOperationException("CRITICAL: auth.json bytes modified during API provider switch.");
                }
            }

            // 5. Update profile metadata
            targetProfile.LastSwitchedAt = _clock.UtcNow;
            _apiProviderStore.Save(targetProfile);
            _audit.Record("switch-target", "ok", $"-> API {targetProfile.DisplayName}");

            // 6. Reopen Codex if captured
            var reopenFailures = new List<CodexProcessInfo>();
            if (closeApps)
            {
                compensator.ReopenDesktop(captured, out reopenFailures);
            }

            var activeTarget = new ActiveTarget.Api(targetProfile);
            if (reopenFailures.Count > 0)
            {
                return new TargetSwitchResult(
                    TargetSwitchOutcome.SuccessWithReopenWarning,
                    $"Switched routing to {targetProfile.DisplayName}, but some apps failed to reopen.",
                    activeTarget, null, closed, reopenFailures);
            }

            return new TargetSwitchResult(
                TargetSwitchOutcome.Success,
                $"Active inference route switched to {targetProfile.DisplayName}.",
                activeTarget, null, closed);
        }
        catch (Exception ex)
        {
            await compensator.CompensateAsync("switch-target", ex.Message, cancellationToken).ConfigureAwait(false);
            return new TargetSwitchResult(
                TargetSwitchOutcome.RolledBack,
                $"Failed to switch to API provider. Original configuration was restored: {ex.Message}",
                new ActiveTarget.Api(targetProfile),
                ErrorInfo.Create(ErrorCategory.Unknown, ex.Message, _clock.UtcNow));
        }
    }

    private async Task<TargetSwitchResult> ExecuteChatGptAccountSwitchAsync(
        ChatGptAccountSwitchPlan accountPlan,
        CancellationToken cancellationToken)
    {
        var targetProfile = accountPlan.TargetProfile;
        var options = accountPlan.Options;
        var isApiRoutingActive = accountPlan.IsApiRoutingActive;

        var compensator = CreateCompensationCoordinator();
        byte[]? configOriginalBytes = _fs.FileExists(_paths.ConfigTomlPath)
            ? _fs.ReadAllBytes(_paths.ConfigTomlPath)
            : null;

        if (configOriginalBytes is not null && isApiRoutingActive)
        {
            compensator.RegisterConfigBackup(configOriginalBytes);
        }

        var initialRouting = _routingConfig.ReadRoutingState(_paths.ConfigTomlPath);
        var catalogChanged = isApiRoutingActive && !string.IsNullOrWhiteSpace(initialRouting.ModelCatalogJson);

        // If API provider routing was active, reset config.toml to openai BEFORE SwitchService switches credentials
        // so that when SwitchService relaunches desktop apps, config.toml already points to openai.
        if (isApiRoutingActive)
        {
            try
            {
                _routingConfig.ReturnToOpenAi(_paths.ConfigTomlPath);
            }
            catch (Exception ex)
            {
                return new TargetSwitchResult(
                    TargetSwitchOutcome.Failed,
                    $"Failed to reset model_provider to openai: {ex.Message}",
                    new ActiveTarget.ChatGpt(targetProfile.Id, targetProfile.AccountEmail),
                    ErrorInfo.Create(ErrorCategory.Unknown, ex.Message, _clock.UtcNow));
            }
        }

        // Execute the full ChatGPT account switch transaction using SwitchService
        var switchOptions = (options.CloseReopenMode != CloseReopenMode.Automatic && catalogChanged)
            ? options with { CloseReopenMode = CloseReopenMode.Automatic }
            : options;

        var chatGptResult = await _chatGptSwitchService.SwitchAsync(
            accountPlan.AllChatGptProfiles,
            targetProfile.Id,
            switchOptions,
            cancellationToken).ConfigureAwait(false);

        if (chatGptResult.Outcome != SwitchOutcome.Success && chatGptResult.Outcome != SwitchOutcome.SuccessWithReopenWarning)
        {
            // If switch failed or rolled back, restore config.toml if it was previously in API mode
            if (isApiRoutingActive && configOriginalBytes is not null)
            {
                _routingConfig.RestoreExactBytes(_paths.ConfigTomlPath, configOriginalBytes);
            }

            var targetOutcome = chatGptResult.Outcome switch
            {
                SwitchOutcome.RolledBack => TargetSwitchOutcome.RolledBack,
                SwitchOutcome.AbortedProcessRemnant => TargetSwitchOutcome.AbortedProcessRemnant,
                _ => TargetSwitchOutcome.Failed,
            };

            return new TargetSwitchResult(
                targetOutcome,
                chatGptResult.Message,
                new ActiveTarget.ChatGpt(targetProfile.Id, targetProfile.AccountEmail),
                chatGptResult.Error,
                chatGptResult.ClosedProcesses,
                chatGptResult.ReopenFailures);
        }

        // Guarantee config.toml has model_provider = openai (idempotent, ensures consistency)
        try
        {
            _routingConfig.ReturnToOpenAi(_paths.ConfigTomlPath);
        }
        catch
        {
            // Non-fatal if config is already openai
        }

        _audit.Record("switch-target", "ok", $"-> ChatGPT {targetProfile.DisplayName}");

        return new TargetSwitchResult(
            chatGptResult.Outcome == SwitchOutcome.SuccessWithReopenWarning
                ? TargetSwitchOutcome.SuccessWithReopenWarning
                : TargetSwitchOutcome.Success,
            $"Active inference target switched to ChatGPT account {targetProfile.DisplayName}.",
            new ActiveTarget.ChatGpt(targetProfile.Id, targetProfile.AccountEmail),
            null,
            chatGptResult.ClosedProcesses,
            chatGptResult.ReopenFailures);
    }

    private async Task<TargetSwitchResult> ExecuteChatGptReturnRoutingAsync(
        ChatGptReturnRoutingSwitchPlan returnPlan,
        CancellationToken cancellationToken)
    {
        var targetProfile = returnPlan.ActiveProfile;
        var options = returnPlan.Options;
        var currentRouting = _routingConfig.ReadRoutingState(_paths.ConfigTomlPath);
        var catalogChanged = !string.IsNullOrWhiteSpace(currentRouting.ModelCatalogJson);
        var closeApps = options.CloseReopenMode == CloseReopenMode.Automatic || catalogChanged;

        var compensator = CreateCompensationCoordinator();
        IReadOnlyList<CodexProcessInfo> captured = [];
        IReadOnlyList<CodexProcessInfo> closed = [];

        if (closeApps)
        {
            captured = _processes.FindRunningCodexProcesses().Where(p => p.IsClosable).ToList();
            closed = await _processes.CloseGracefullyThenKillAsync(captured, options.GracefulCloseTimeout, cancellationToken).ConfigureAwait(false);
            compensator.RegisterCapturedProcesses(captured);

            if (_processes.AnyCodexCliRunning())
            {
                compensator.ReopenDesktop(captured, out _);
                _audit.Record("switch", "aborted", "process remnant");
                return new TargetSwitchResult(
                    TargetSwitchOutcome.AbortedProcessRemnant,
                    "A troca foi abortada: ainda há um processo do Codex em execução. Nada foi alterado.",
                    new ActiveTarget.ChatGpt(targetProfile?.Id, targetProfile?.AccountEmail),
                    ErrorInfo.Create(ErrorCategory.ProcessRemnant, "Process remnant", _clock.UtcNow),
                    closed);
            }
        }

        byte[]? configOriginalBytes = _fs.FileExists(_paths.ConfigTomlPath)
            ? _fs.ReadAllBytes(_paths.ConfigTomlPath)
            : null;

        if (configOriginalBytes is not null)
        {
            compensator.RegisterConfigBackup(configOriginalBytes);
        }

        try
        {
            _routingConfig.ReturnToOpenAi(_paths.ConfigTomlPath);
        }
        catch (Exception ex)
        {
            await compensator.CompensateAsync("switch-target", ex.Message, cancellationToken).ConfigureAwait(false);
            return new TargetSwitchResult(
                TargetSwitchOutcome.RolledBack,
                $"Failed to return model_provider to openai: {ex.Message}",
                new ActiveTarget.ChatGpt(targetProfile?.Id, targetProfile?.AccountEmail),
                ErrorInfo.Create(ErrorCategory.Unknown, ex.Message, _clock.UtcNow));
        }

        var reopenFailures = new List<CodexProcessInfo>();
        if (closeApps)
        {
            compensator.ReopenDesktop(captured, out reopenFailures);
        }

        _audit.Record("switch-target", "ok", "-> ChatGPT (routing returned)");

        return new TargetSwitchResult(
            reopenFailures.Count > 0 ? TargetSwitchOutcome.SuccessWithReopenWarning : TargetSwitchOutcome.Success,
            "Active inference route returned to ChatGPT.",
            new ActiveTarget.ChatGpt(targetProfile?.Id, targetProfile?.AccountEmail),
            null,
            closed,
            reopenFailures);
    }

    private string? GetFileSha256(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !_fs.FileExists(path)) return null;
        try
        {
            return Convert.ToHexString(SHA256.HashData(_fs.ReadAllBytes(path)));
        }
        catch
        {
            return null;
        }
    }
}
