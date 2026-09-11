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

        // If a specific different account is requested
        if (targetProfile is not null && (currentActive is null || currentActive.Id != targetProfile.Id))
        {
            // Multi-resource transaction:
            // 1. Close Codex
            // 2. Backup config.toml
            byte[]? configOriginalBytes = _fs.FileExists(_paths.ConfigTomlPath)
                ? _fs.ReadAllBytes(_paths.ConfigTomlPath)
                : null;

            // Execute credential slot switch
            var chatGptResult = await _chatGptSwitchService.SwitchAsync(
                allChatGptProfiles,
                targetProfile.Id,
                options with { CloseReopenMode = CloseReopenMode.DoNothing }, // process control coordinated here
                cancellationToken).ConfigureAwait(false);

            if (chatGptResult.Outcome != SwitchOutcome.Success && chatGptResult.Outcome != SwitchOutcome.SuccessWithReopenWarning)
            {
                return new TargetSwitchResult(
                    TargetSwitchOutcome.Failed,
                    chatGptResult.Message,
                    new ActiveTarget.ChatGpt(targetProfile.Id, targetProfile.AccountEmail),
                    chatGptResult.Error,
                    chatGptResult.ClosedProcesses);
            }

            // Set model_provider = "openai"
            try
            {
                _routingConfig.ReturnToOpenAi(_paths.ConfigTomlPath);
            }
            catch (Exception ex)
            {
                // Rollback config
                if (configOriginalBytes is not null)
                {
                    _routingConfig.RestoreExactBytes(_paths.ConfigTomlPath, configOriginalBytes);
                }
                // If previous was active, roll back credential
                if (currentActive is not null)
                {
                    await _chatGptSwitchService.SwitchAsync(
                        allChatGptProfiles,
                        currentActive.Id,
                        options with { CloseReopenMode = CloseReopenMode.DoNothing },
                        cancellationToken).ConfigureAwait(false);
                }

                return new TargetSwitchResult(
                    TargetSwitchOutcome.RolledBack,
                    $"Failed to set model_provider to openai. Rolled back: {ex.Message}",
                    new ActiveTarget.ChatGpt(targetProfile.Id, targetProfile.AccountEmail),
                    ErrorInfo.Create(ErrorCategory.Unknown, ex.Message, _clock.UtcNow));
            }

            var closeApps = options.CloseReopenMode == CloseReopenMode.Automatic;
            var reopenFailures = new List<CodexProcessInfo>();
            if (closeApps && chatGptResult.ClosedProcesses is not null)
            {
                ReopenDesktop(chatGptResult.ClosedProcesses, out reopenFailures);
            }

            return new TargetSwitchResult(
                reopenFailures.Count > 0 ? TargetSwitchOutcome.SuccessWithReopenWarning : TargetSwitchOutcome.Success,
                $"Active inference target switched to ChatGPT account {targetProfile.DisplayName}.",
                new ActiveTarget.ChatGpt(targetProfile.Id, targetProfile.AccountEmail),
                null,
                chatGptResult.ClosedProcesses,
                reopenFailures);
        }
        else
        {
            // Same account already active in auth.json: simply return model_provider to "openai"
            var closeApps = options.CloseReopenMode == CloseReopenMode.Automatic;
            IReadOnlyList<CodexProcessInfo> captured = [];
            IReadOnlyList<CodexProcessInfo> closed = [];

            if (closeApps)
            {
                captured = _processes.FindRunningCodexProcesses().Where(p => p.IsClosable).ToList();
                closed = await _processes.CloseGracefullyThenKillAsync(captured, options.GracefulCloseTimeout, cancellationToken).ConfigureAwait(false);
            }

            _routingConfig.ReturnToOpenAi(_paths.ConfigTomlPath);

            var reopenFailures = new List<CodexProcessInfo>();
            if (closeApps)
            {
                ReopenDesktop(captured, out reopenFailures);
            }

            return new TargetSwitchResult(
                reopenFailures.Count > 0 ? TargetSwitchOutcome.SuccessWithReopenWarning : TargetSwitchOutcome.Success,
                $"Active inference route returned to ChatGPT.",
                new ActiveTarget.ChatGpt(targetProfile?.Id, targetProfile?.AccountEmail),
                null,
                closed,
                reopenFailures);
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
