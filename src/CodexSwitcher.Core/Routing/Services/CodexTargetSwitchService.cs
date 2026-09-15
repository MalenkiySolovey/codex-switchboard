using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Security.Secrets;

namespace CodexSwitcher.Core.Routing.Services;

/// <summary>
/// Facade implementation of <see cref="ICodexTargetSwitchService"/> coordinating
/// switch plan construction and atomic execution.
/// Acts as the single concurrency lock owner for all target switch operations.
/// </summary>
public sealed class CodexTargetSwitchService : ICodexTargetSwitchService, IDisposable
{
    private readonly ISwitchPlanBuilder _planBuilder;
    private readonly ISwitchTransactionExecutor _executor;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Decomposed constructor injecting dedicated plan builder and transaction executor.
    /// </summary>
    public CodexTargetSwitchService(
        ISwitchPlanBuilder planBuilder,
        ISwitchTransactionExecutor executor)
    {
        _planBuilder = planBuilder ?? throw new ArgumentNullException(nameof(planBuilder));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <summary>
    /// Backward-compatible constructor for existing composition roots and tests.
    /// Assembles decomposed components internally.
    /// </summary>
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
        : this(
            new SwitchPlanBuilder(apiProviderStore, secretStore, brokerInstaller, routingConfig, paths),
            new SwitchTransactionExecutor(chatGptSwitchService, routingConfig, apiProviderStore, processes, fs, clock, audit, paths))
    {
    }

    public async Task<TargetSwitchResult> SwitchToApiProviderAsync(
        Guid apiProfileId,
        SwitchExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plan = _planBuilder.BuildApiProviderPlan(apiProfileId, options);
            return await _executor.ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
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

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plan = _planBuilder.BuildChatGptPlan(targetChatGptProfileId, allChatGptProfiles, options);
            return await _executor.ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
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
        ArgumentNullException.ThrowIfNull(options);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plan = _planBuilder.BuildApiRoutePlan(apiProfileId, routeId, newBaseUrl, options);
            return await _executor.ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
    }
}
