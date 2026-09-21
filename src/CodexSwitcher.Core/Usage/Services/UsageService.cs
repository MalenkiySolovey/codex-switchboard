using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
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
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Models;
using System.Collections.Concurrent;

namespace CodexSwitcher.Core.Usage.Services;

public sealed record UsageServiceOptions
{
    public int MaxConcurrentUsageProcesses { get; init; } = 2;
}

/// <summary>
/// Service coordinating multi-account rate limit queries and caching.
/// Handles bounded process concurrency with priority queueing, in-flight request coalescing,
/// caller cancellation isolation, CAS-protected credential rotation writebacks,
/// safe active-slot vault synchronization, and progressive card result publishing.
/// </summary>
public sealed class UsageService : IUsageService, IDisposable
{
    private readonly ICodexUsageProvider _provider;
    private readonly IUsageCache _cache;
    private readonly IVaultService _vault;
    private readonly IProfileOperationCoordinator _coordinator;
    private readonly IClock _clock;
    private readonly UsageServiceOptions _options;
    private readonly AsyncPriorityThrottle _priorityThrottle;
    private readonly ConcurrentDictionary<Guid, Task<UsageFetchResult>> _inFlight = new();
    private readonly CancellationTokenSource _serviceCts;
    private readonly IFileSystem? _fs;
    private readonly CodexPaths? _codexPaths;
    private readonly IProfileStore? _profileStore;
    private readonly Action? _onProfilesPersistNeeded;

    public UsageService(
        ICodexUsageProvider provider,
        IUsageCache cache,
        IVaultService vault,
        IProfileOperationCoordinator coordinator,
        IClock clock,
        IAppLifetime? appLifetime)
        : this(provider, cache, vault, coordinator, clock, null, appLifetime, null, null, null, null)
    {
    }

    public UsageService(
        ICodexUsageProvider provider,
        IUsageCache cache,
        IVaultService vault,
        IProfileOperationCoordinator coordinator,
        IClock clock,
        UsageServiceOptions? options = null,
        IAppLifetime? appLifetime = null,
        IFileSystem? fs = null,
        CodexPaths? codexPaths = null,
        IProfileStore? profileStore = null,
        Action? onProfilesPersistNeeded = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new UsageServiceOptions();
        _fs = fs;
        _codexPaths = codexPaths;
        _profileStore = profileStore;
        _onProfilesPersistNeeded = onProfilesPersistNeeded;

        _serviceCts = appLifetime is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(appLifetime.ApplicationStopping)
            : new CancellationTokenSource();

        var maxProcesses = Math.Max(1, _options.MaxConcurrentUsageProcesses);
        _priorityThrottle = new AsyncPriorityThrottle(maxProcesses);
    }

    public UsageCacheEntry? GetCached(Guid profileId) => _cache.Get(profileId);

    public Task LoadCacheAsync(CancellationToken cancellationToken = default) =>
        _cache.LoadAsync(cancellationToken);

    public IReadOnlyDictionary<Guid, UsageCacheEntry> GetAllCached() => _cache.GetAll();

    public void Invalidate(Guid profileId) => _cache.Invalidate(profileId);

    public async Task<UsageFetchResult> RefreshAsync(
        ProfileMetadata profile,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // Coalesce duplicate requests for the same profile onto one in-flight Task
        Task<UsageFetchResult> inFlightTask;
        lock (_inFlight)
        {
            if (!_inFlight.TryGetValue(profile.Id, out inFlightTask!))
            {
                inFlightTask = ExecuteRefreshAsync(profile, force);
                _inFlight[profile.Id] = inFlightTask;
            }
        }

        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _serviceCts.Token);
        watchdogCts.CancelAfter(TimeSpan.FromSeconds(75));

        // Caller cancellation isolation: caller cancellation token only cancels caller await,
        // leaving the shared operation to run to completion.
        return await inFlightTask.WaitAsync(watchdogCts.Token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<Guid, UsageFetchResult>> RefreshAllAsync(
        IReadOnlyList<ProfileMetadata> profiles,
        CancellationToken cancellationToken = default) =>
        await RefreshAllAsync(profiles, onAccountCompleted: null, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<Guid, UsageFetchResult>> RefreshAllAsync(
        IReadOnlyList<ProfileMetadata> profiles,
        Action<Guid, UsageFetchResult>? onAccountCompleted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var tasks = profiles.Select(async p =>
        {
            try
            {
                var res = await RefreshAsync(p, force: false, cancellationToken).ConfigureAwait(false);
                onAccountCompleted?.Invoke(p.Id, res);
                return (p.Id, Result: res);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var err = ErrorInfo.Create(ErrorCategory.Timeout, "Refresh operation was cancelled by caller.", _clock.UtcNow);
                var fail = UsageFetchResult.Fail(UsageStatus.Error, err);
                onAccountCompleted?.Invoke(p.Id, fail);
                return (p.Id, Result: fail);
            }
            catch (Exception ex)
            {
                var err = ErrorInfo.Create(ErrorCategory.Unknown, ex.Message, _clock.UtcNow);
                var fail = UsageFetchResult.Fail(UsageStatus.Error, err);
                onAccountCompleted?.Invoke(p.Id, fail);
                return (p.Id, Result: fail);
            }
        }).ToList();

        var completed = await Task.WhenAll(tasks).ConfigureAwait(false);
        return completed.ToDictionary(c => c.Id, c => c.Result);
    }

    private async Task<UsageFetchResult> ExecuteRefreshAsync(ProfileMetadata profile, bool force)
    {
        try
        {
            byte[] authBytes;
            string originalFingerprint;

            // Step 1: Read current credentials under coordinator lock
            using (await _coordinator.LockAsync(profile.Id, _serviceCts.Token).ConfigureAwait(false))
            {
                // Safe active profile vault resync from active auth.json upon external token drift
                if (profile.IsActive && _fs != null && _codexPaths != null && _fs.FileExists(_codexPaths.ActiveAuthPath))
                {
                    try
                    {
                        var activeBytes = _fs.ReadAllBytes(_codexPaths.ActiveAuthPath);
                        var activeFp = Fingerprint.Compute(activeBytes);
                        if (_vault.Exists(profile.Id))
                        {
                            var currentVaultBytes = _vault.LoadBlob(profile.Id);
                            var currentVaultFp = Fingerprint.Compute(currentVaultBytes);
                            if (activeFp != currentVaultFp)
                            {
                                var (_, activeClaims) = AuthJsonReader.Identify(activeBytes);
                                if (string.IsNullOrEmpty(profile.AccountSub) || activeClaims.Sub == profile.AccountSub)
                                {
                                    _vault.SaveBlob(profile.Id, activeBytes);
                                    profile.BlobFingerprint = activeFp;
                                    profile.LastRefreshedAt = _clock.UtcNow;
                                    var sub = SubscriptionJwtClaimExtractor.Extract(activeBytes, _clock.UtcNow);
                                    if (sub is not null) profile.DetectedSubscription = sub;
                                    _onProfilesPersistNeeded?.Invoke();
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (!_vault.Exists(profile.Id))
                {
                    var err = ErrorInfo.Create(ErrorCategory.InvalidAuthFile, "Profile credentials not found in vault.", _clock.UtcNow);
                    _cache.Set(profile.Id, null, UsageStatus.AuthRequired, err);
                    await _cache.SaveAsync(_serviceCts.Token).ConfigureAwait(false);
                    return UsageFetchResult.Fail(UsageStatus.AuthRequired, err);
                }

                try
                {
                    authBytes = _vault.LoadBlob(profile.Id);
                    originalFingerprint = Fingerprint.Compute(authBytes);
                }
                catch (SecretDecryptionException)
                {
                    var err = ErrorInfo.Create(ErrorCategory.DecryptionFailed, "Profile credentials could not be decrypted.", _clock.UtcNow);
                    _cache.Set(profile.Id, null, UsageStatus.AuthRequired, err);
                    await _cache.SaveAsync(_serviceCts.Token).ConfigureAwait(false);
                    return UsageFetchResult.Fail(UsageStatus.AuthRequired, err);
                }
            }

            // Step 2: Acquire priority throttle to bound concurrent child app-server processes
            UsageFetchResult fetchResult;
            var priority = force ? UsagePriority.Interactive : UsagePriority.Background;
            using (await _priorityThrottle.AcquireAsync(priority, _serviceCts.Token).ConfigureAwait(false))
            {
                var fetchOpts = new UsageFetchOptions(
                    ExcludeResetCreditDetails: !force,
                    IncludeActivity: force);

                fetchResult = await _provider.FetchRateLimitsAsync(profile.Id, authBytes, fetchOpts, _serviceCts.Token).ConfigureAwait(false);
            }

            // Sync live plan changes into ProfileMetadata if returned from account/read
            var livePlan = fetchResult.Snapshot?.PlanType;
            if (!string.IsNullOrWhiteSpace(livePlan) && !string.Equals(profile.PlanType, livePlan, StringComparison.OrdinalIgnoreCase))
            {
                profile.PlanType = livePlan;
                if (string.Equals(livePlan, "free", StringComparison.OrdinalIgnoreCase))
                {
                    if (profile.DetectedSubscription is not null)
                    {
                        profile.DetectedSubscription = profile.DetectedSubscription with { IsStale = true };
                    }
                }
                _onProfilesPersistNeeded?.Invoke();
            }

            // Step 3: Handle rotation and update cache
            if (fetchResult.SandboxAuthMutated && fetchResult.RotatedAuthJson is not null)
            {
                if (profile.IsActive)
                {
                    // CASE C: Active profile credential rotation detected in sandbox.
                    // DO NOT write %USERPROFILE%\.codex\auth.json.
                    // DO NOT blindly update vault. External processes own active credentials.
                    var conflictErr = ErrorInfo.Create(
                        ErrorCategory.Unknown,
                        "Active profile credential rotation detected in sandbox; active slot write suppressed.",
                        _clock.UtcNow);

                    _cache.Set(
                        profile.Id,
                        fetchResult.Snapshot,
                        fetchResult.Status,
                        conflictErr,
                        isStale: false,
                        credentialConflict: true,
                        conflictReason: CredentialConflictReason.ActiveCredentialMutation,
                        activity: fetchResult.Activity,
                        activityAvailability: fetchResult.ActivityAvailability);

                    await _cache.SaveAsync(_serviceCts.Token).ConfigureAwait(false);
                    return fetchResult;
                }
                else
                {
                    // CASE B: Inactive profile credential rotation.
                    // Perform CAS writeback using original fingerprint.
                    var (casSuccess, _) = await _vault.SaveBlobIfUnchangedAsync(
                        profile.Id,
                        originalFingerprint,
                        fetchResult.RotatedAuthJson,
                        _serviceCts.Token).ConfigureAwait(false);

                    if (casSuccess)
                    {
                        var newSub = SubscriptionJwtClaimExtractor.Extract(fetchResult.RotatedAuthJson, _clock.UtcNow);
                        if (newSub is not null)
                            profile.DetectedSubscription = newSub;

                        _cache.Set(
                            profile.Id,
                            fetchResult.Snapshot,
                            fetchResult.Status,
                            fetchResult.Error,
                            isStale: false,
                            credentialConflict: false,
                            conflictReason: CredentialConflictReason.None,
                            activity: fetchResult.Activity,
                            activityAvailability: fetchResult.ActivityAvailability);

                        await _cache.SaveAsync(_serviceCts.Token).ConfigureAwait(false);
                        return fetchResult;
                    }
                    else
                    {
                        // CAS conflict: vault was modified concurrently; discard rotated bytes
                        var casConflictErr = ErrorInfo.Create(
                            ErrorCategory.Unknown,
                            "Credential generation conflict during rotation writeback.",
                            _clock.UtcNow);

                        _cache.Set(
                            profile.Id,
                            fetchResult.Snapshot,
                            UsageStatus.Error,
                            casConflictErr,
                            isStale: false,
                            credentialConflict: true,
                            conflictReason: CredentialConflictReason.InactiveGenerationChanged,
                            activity: fetchResult.Activity,
                            activityAvailability: fetchResult.ActivityAvailability);

                        await _cache.SaveAsync(_serviceCts.Token).ConfigureAwait(false);
                        return UsageFetchResult.Fail(UsageStatus.Error, casConflictErr);
                    }
                }
            }

            // CASE A: Normal success or unchanged credentials
            var previousEntry = _cache.Get(profile.Id);

            // Retain last-known-good quota snapshot marked stale on transient errors
            var effectiveSnapshot = fetchResult.Snapshot;
            var isStale = false;
            if (effectiveSnapshot is null && previousEntry?.Snapshot is not null &&
                fetchResult.Status is UsageStatus.ProcessDown or UsageStatus.BackingOff or UsageStatus.Error)
            {
                effectiveSnapshot = previousEntry.Snapshot;
                isStale = true;
            }

            // Preserve cached activity if current fetch skipped activity
            var effectiveActivity = fetchResult.Activity ?? previousEntry?.Activity;
            var effectiveActivityAvailability = fetchResult.Activity is not null
                ? fetchResult.ActivityAvailability
                : (previousEntry?.ActivityAvailability ?? fetchResult.ActivityAvailability);

            _cache.Set(
                profile.Id,
                effectiveSnapshot,
                fetchResult.Status,
                fetchResult.Error,
                isStale: isStale,
                credentialConflict: false,
                conflictReason: CredentialConflictReason.None,
                activity: effectiveActivity,
                activityAvailability: effectiveActivityAvailability);

            await _cache.SaveAsync(_serviceCts.Token).ConfigureAwait(false);
            return fetchResult;
        }
        catch (OperationCanceledException) when (_serviceCts.IsCancellationRequested)
        {
            var err = ErrorInfo.Create(ErrorCategory.Timeout, "Service shutdown.", _clock.UtcNow);
            return UsageFetchResult.Fail(UsageStatus.ProcessDown, err);
        }
        catch (Exception ex)
        {
            var err = ErrorInfo.Create(ErrorCategory.Unknown, ex.Message, _clock.UtcNow);
            var prev = _cache.Get(profile.Id);
            if (prev?.Snapshot is not null)
            {
                _cache.Set(
                    profile.Id,
                    prev.Snapshot,
                    UsageStatus.Error,
                    err,
                    isStale: true,
                    credentialConflict: false,
                    conflictReason: CredentialConflictReason.None,
                    activity: prev.Activity,
                    activityAvailability: prev.ActivityAvailability);
            }
            else
            {
                _cache.Set(profile.Id, null, UsageStatus.Error, err);
            }
            try { await _cache.SaveAsync(_serviceCts.Token).ConfigureAwait(false); } catch { }
            return UsageFetchResult.Fail(UsageStatus.Error, err);
        }
        finally
        {
            lock (_inFlight)
            {
                _inFlight.TryRemove(profile.Id, out _);
            }
        }
    }

    public void Dispose()
    {
        try { _serviceCts.Cancel(); } catch { }
        _serviceCts.Dispose();
        _priorityThrottle.Dispose();
    }
}
