using System.Security.Cryptography;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;

namespace CodexSwitcher.Core.Services;

/// <summary>
/// Production implementation of <see cref="ICodexTargetSwitchService"/> coordinating
/// ChatGPT credential switches, API provider routing configuration, KeyBroker integrity,
/// process lifecycles, and atomic multi-resource rollbacks.
/// </summary>
public sealed class CodexTargetSwitchService : ICodexTargetSwitchService
{
    private readonly SwitchService _chatGptSwitchService;
    private readonly ICodexRoutingConfigStore _routingConfig;
    private readonly IApiProviderStore _apiProviderStore;
    private readonly IApiKeySecretStore _secretStore;
    private readonly IKeyBrokerInstaller _brokerInstaller;
    private readonly IProcessManager _processes;
    private readonly IFileSystem _fs;
    private readonly IClock _clock;
    private readonly IAuditLog _audit;
    private readonly CodexPaths _paths;

    public CodexTargetSwitchService(
        SwitchService chatGptSwitchService,
        ICodexRoutingConfigStore routingConfig,
        IApiProviderStore apiProviderStore,
        IApiKeySecretStore secretStore,
        IKeyBrokerInstaller brokerInstaller,
        IProcessManager processes,
        IFileSystem fs,
        IClock clock,
        IAuditLog audit,
        CodexPaths paths)
    {
        _chatGptSwitchService = chatGptSwitchService ?? throw new ArgumentNullException(nameof(chatGptSwitchService));
        _routingConfig = routingConfig ?? throw new ArgumentNullException(nameof(routingConfig));
        _apiProviderStore = apiProviderStore ?? throw new ArgumentNullException(nameof(apiProviderStore));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _brokerInstaller = brokerInstaller ?? throw new ArgumentNullException(nameof(brokerInstaller));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<TargetSwitchResult> SwitchToApiProviderAsync(
        Guid apiProfileId,
        SwitchExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 1. Resolve target profile
        var targetProfile = _apiProviderStore.GetById(apiProfileId);
        if (targetProfile is null)
        {
            return Fail(new ActiveTarget.Unknown(), ErrorCategory.Unknown, $"API provider profile {apiProfileId} not found.");
        }

        // 2. Confirm encrypted key exists
        if (!_secretStore.HasApiKey(targetProfile.Id))
        {
            targetProfile.Status = ApiProviderProfileStatus.CredentialMissing;
            _apiProviderStore.Save(targetProfile);
            return Fail(new ActiveTarget.Api(targetProfile), ErrorCategory.DecryptionFailed,
                $"Encrypted API key for '{targetProfile.DisplayName}' is missing. Re-enter the API key.");
        }

        // 3. Ensure KeyBroker is installed and verified
        string brokerPath;
        try
        {
            brokerPath = _brokerInstaller.EnsureInstalled();
        }
        catch (Exception ex)
        {
            return Fail(new ActiveTarget.Api(targetProfile), ErrorCategory.Unknown,
                $"Failed to install or verify KeyBroker executable: {ex.Message}");
        }

        // 4. Capture & close Codex processes
        var closeApps = options.CloseReopenMode == CloseReopenMode.Automatic;
        IReadOnlyList<CodexProcessInfo> captured = [];
        IReadOnlyList<CodexProcessInfo> closed = [];

        if (closeApps)
        {
            captured = _processes.FindRunningCodexProcesses().Where(p => p.IsClosable).ToList();
            closed = await _processes.CloseGracefullyThenKillAsync(captured, options.GracefulCloseTimeout, cancellationToken).ConfigureAwait(false);

            if (_processes.AnyCodexCliRunning())
            {
                ReopenDesktop(captured, out _);
                _audit.Record("api-switch", "aborted", "process remnant");
                return new TargetSwitchResult(TargetSwitchOutcome.AbortedProcessRemnant,
                    "Switch aborted: Codex CLI process is still running. No changes made.",
                    new ActiveTarget.Api(targetProfile),
                    ErrorInfo.Create(ErrorCategory.ProcessRemnant, "Process remnant", _clock.UtcNow),
                    closed);
            }
        }

        // 5. Assert auth.json pre-switch hash
        byte[]? authPreBytes = _fs.FileExists(_paths.ActiveAuthPath)
            ? _fs.ReadAllBytes(_paths.ActiveAuthPath)
            : null;
        byte[]? authPreHash = authPreBytes is not null ? SHA256.HashData(authPreBytes) : null;

        // 6. Read and backup config.toml exact bytes
        byte[]? configOriginalBytes = _fs.FileExists(_paths.ConfigTomlPath)
            ? _fs.ReadAllBytes(_paths.ConfigTomlPath)
            : null;

        try
        {
            var providerBlock = new CodexProviderBlock(
                targetProfile.StableCodexProviderId,
                targetProfile.DisplayName,
                targetProfile.BaseUrl,
                targetProfile.WireApi,
                brokerPath,
                new[] { "--key-id", targetProfile.Id.ToString("D") },
                5000);

            var model = targetProfile.SelectedModel ?? "gpt-5.6-sol";
            _routingConfig.ApplySwitchboardRouting(_paths.ConfigTomlPath, providerBlock, model);

            // 7. Verify auth.json is UNTOUCHED
            if (authPreHash is not null)
            {
                var authPostBytes = _fs.ReadAllBytes(_paths.ActiveAuthPath);
                var authPostHash = SHA256.HashData(authPostBytes);
                if (!CryptographicOperations.FixedTimeEquals(authPreHash, authPostHash))
                {
                    throw new InvalidOperationException("CRITICAL: auth.json bytes modified during API provider switch.");
                }
            }

            // 8. Update profile metadata
            targetProfile.LastSwitchedAt = _clock.UtcNow;
            _apiProviderStore.Save(targetProfile);
            _audit.Record("switch-target", "ok", $"-> API {targetProfile.DisplayName}");

            // 9. Reopen Codex if captured
            var reopenFailures = new List<CodexProcessInfo>();
            if (closeApps)
            {
                ReopenDesktop(captured, out reopenFailures);
            }

            var activeTarget = new ActiveTarget.Api(targetProfile);
            if (reopenFailures.Count > 0)
            {
                return new TargetSwitchResult(TargetSwitchOutcome.SuccessWithReopenWarning,
                    $"Switched routing to {targetProfile.DisplayName}, but some apps failed to reopen.",
                    activeTarget, null, closed, reopenFailures);
            }

            return new TargetSwitchResult(TargetSwitchOutcome.Success,
                $"Active inference route switched to {targetProfile.DisplayName}.",
                activeTarget, null, closed);
        }
        catch (Exception ex)
        {
            // Rollback config.toml to original bytes
            if (configOriginalBytes is not null)
            {
                _routingConfig.RestoreExactBytes(_paths.ConfigTomlPath, configOriginalBytes);
            }

            if (closeApps)
            {
                ReopenDesktop(captured, out _);
            }

            _audit.Record("switch-target", "failed", ex.Message);
            return new TargetSwitchResult(TargetSwitchOutcome.RolledBack,
                $"Failed to switch to API provider. Original configuration was restored: {ex.Message}",
                new ActiveTarget.Api(targetProfile),
                ErrorInfo.Create(ErrorCategory.Unknown, ex.Message, _clock.UtcNow));
        }
    }

    public async Task<TargetSwitchResult> SwitchToChatGptAsync(
        Guid? targetChatGptProfileId,
        List<ProfileMetadata> allChatGptProfiles,
        SwitchExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(allChatGptProfiles);

        var currentActive = allChatGptProfiles.FirstOrDefault(p => p.IsActive);
        var targetProfile = targetChatGptProfileId.HasValue
            ? allChatGptProfiles.FirstOrDefault(p => p.Id == targetChatGptProfileId.Value)
            : currentActive;

        if (targetChatGptProfileId.HasValue && targetProfile is null)
        {
            return Fail(new ActiveTarget.Unknown(), ErrorCategory.Unknown, $"ChatGPT profile {targetChatGptProfileId.Value} not found.");
        }

        var routing = _routingConfig.ReadRoutingState(_paths.ConfigTomlPath);
        var isApiRoutingActive = !string.IsNullOrWhiteSpace(routing.ModelProvider) &&
                                 !string.Equals(routing.ModelProvider, "openai", StringComparison.OrdinalIgnoreCase);

        var isAccountSwitch = targetProfile is not null && (currentActive is null || currentActive.Id != targetProfile.Id);

        // Case 1: Switching to a different ChatGPT account (or initial activation)
        if (isAccountSwitch)
        {
            byte[]? configOriginalBytes = _fs.FileExists(_paths.ConfigTomlPath)
                ? _fs.ReadAllBytes(_paths.ConfigTomlPath)
                : null;

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
                        new ActiveTarget.ChatGpt(targetProfile!.Id, targetProfile.AccountEmail),
                        ErrorInfo.Create(ErrorCategory.Unknown, ex.Message, _clock.UtcNow));
                }
            }

            // Execute the full ChatGPT account switch transaction using SwitchService (with full process capture,
            // fail-fast decryption, write-back, backup, atomic auth.json write, and desktop relaunch).
            var chatGptResult = await _chatGptSwitchService.SwitchAsync(
                allChatGptProfiles,
                targetProfile!.Id,
                options,
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
        else if (isApiRoutingActive)
        {
            // Case 2: Same account already active in auth.json, but routing was on an API Provider:
            // Must return model_provider to "openai" and restart Codex desktop so it picks up the route change.
            var closeApps = options.CloseReopenMode == CloseReopenMode.Automatic;
            IReadOnlyList<CodexProcessInfo> captured = [];
            IReadOnlyList<CodexProcessInfo> closed = [];

            if (closeApps)
            {
                captured = _processes.FindRunningCodexProcesses().Where(p => p.IsClosable).ToList();
                closed = await _processes.CloseGracefullyThenKillAsync(captured, options.GracefulCloseTimeout, cancellationToken).ConfigureAwait(false);

                if (_processes.AnyCodexCliRunning())
                {
                    ReopenDesktop(captured, out _);
                    _audit.Record("switch", "aborted", "process remnant");
                    return new TargetSwitchResult(TargetSwitchOutcome.AbortedProcessRemnant,
                        "A troca foi abortada: ainda há um processo do Codex em execução. Nada foi alterado.",
                        new ActiveTarget.ChatGpt(targetProfile?.Id, targetProfile?.AccountEmail),
                        ErrorInfo.Create(ErrorCategory.ProcessRemnant, "Process remnant", _clock.UtcNow),
                        closed);
                }
            }

            byte[]? configOriginalBytes = _fs.FileExists(_paths.ConfigTomlPath)
                ? _fs.ReadAllBytes(_paths.ConfigTomlPath)
                : null;

            try
            {
                _routingConfig.ReturnToOpenAi(_paths.ConfigTomlPath);
            }
            catch (Exception ex)
            {
                if (configOriginalBytes is not null)
                {
                    _routingConfig.RestoreExactBytes(_paths.ConfigTomlPath, configOriginalBytes);
                }
                if (closeApps)
                {
                    ReopenDesktop(captured, out _);
                }

                return new TargetSwitchResult(
                    TargetSwitchOutcome.RolledBack,
                    $"Failed to return model_provider to openai: {ex.Message}",
                    new ActiveTarget.ChatGpt(targetProfile?.Id, targetProfile?.AccountEmail),
                    ErrorInfo.Create(ErrorCategory.Unknown, ex.Message, _clock.UtcNow));
            }

            var reopenFailures = new List<CodexProcessInfo>();
            if (closeApps)
            {
                ReopenDesktop(captured, out reopenFailures);
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
        else
        {
            // Case 3: Same account already active in auth.json AND config.toml is already on OpenAI:
            // Idempotent no-op.
            return new TargetSwitchResult(
                TargetSwitchOutcome.Success,
                $"{targetProfile?.DisplayName ?? "ChatGPT"} já é a conta ativa.",
                new ActiveTarget.ChatGpt(targetProfile?.Id, targetProfile?.AccountEmail));
        }
    }

    public async Task<TargetSwitchResult> SwitchApiRouteAsync(
        Guid apiProfileId,
        string routeId,
        string newBaseUrl,
        SwitchExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newBaseUrl);

        var profile = _apiProviderStore.GetById(apiProfileId);
        if (profile is null)
        {
            return Fail(new ActiveTarget.Unknown(), ErrorCategory.Unknown, $"API provider profile {apiProfileId} not found.");
        }

        profile.SelectedRouteId = routeId;
        profile.BaseUrl = newBaseUrl;
        _apiProviderStore.Save(profile);

        // Update routing config if this provider is currently in config.toml
        var routing = _routingConfig.ReadRoutingState(_paths.ConfigTomlPath);
        if (routing.SwitchboardProviders.ContainsKey(profile.StableCodexProviderId))
        {
            _routingConfig.UpdateProviderRoute(_paths.ConfigTomlPath, profile.StableCodexProviderId, newBaseUrl);
        }

        _audit.Record("route-switch", "ok", $"{profile.DisplayName} -> {routeId} ({newBaseUrl})");
        return new TargetSwitchResult(TargetSwitchOutcome.Success,
            $"Switched {profile.DisplayName} route to {routeId}.",
            new ActiveTarget.Api(profile));
    }

    private void ReopenDesktop(IReadOnlyList<CodexProcessInfo> captured, out List<CodexProcessInfo> failures)
    {
        failures = [];
        var distinct = captured
            .Where(p => p.IsReopenable)
            .GroupBy(p => p.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());

        foreach (var p in distinct)
        {
            try { _processes.Relaunch(p); }
            catch (Exception) { failures.Add(p); }
        }
    }

    private TargetSwitchResult Fail(ActiveTarget target, ErrorCategory category, string message) =>
        new(TargetSwitchOutcome.Failed, message, target, ErrorInfo.Create(category, message, _clock.UtcNow));
}
