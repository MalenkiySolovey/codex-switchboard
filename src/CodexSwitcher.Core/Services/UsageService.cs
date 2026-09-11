using System.Collections.Concurrent;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Security;

namespace CodexSwitcher.Core.Services;

public sealed record UsageServiceOptions
{
    public int MaxConcurrentUsageProcesses { get; init; } = 1;
}

/// <summary>
/// Service coordinating multi-account rate limit queries and caching.
/// Handles bounded process concurrency, in-flight request coalescing,
/// caller cancellation isolation, and CAS-protected credential rotation writebacks.
/// </summary>
public sealed class UsageService : IUsageService, IDisposable
{
    private readonly ICodexUsageProvider _provider;
    private readonly IUsageCache _cache;
    private readonly VaultService _vault;
    private readonly IProfileOperationCoordinator _coordinator;
    private readonly IClock _clock;
    private readonly UsageServiceOptions _options;
    private readonly SemaphoreSlim _processThrottle;
    private readonly ConcurrentDictionary<Guid, Task<UsageFetchResult>> _inFlight = new();
    private readonly CancellationTokenSource _serviceCts;

    public UsageService(
        ICodexUsageProvider provider,
        IUsageCache cache,
        VaultService vault,
        IProfileOperationCoordinator coordinator,
        IClock clock,
        IAppLifetime? appLifetime)
        : this(provider, cache, vault, coordinator, clock, null, appLifetime)
    {
    }

    public UsageService(
        ICodexUsageProvider provider,
        IUsageCache cache,
        VaultService vault,
        IProfileOperationCoordinator coordinator,
        IClock clock,
        UsageServiceOptions? options = null,
        IAppLifetime? appLifetime = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new UsageServiceOptions();

        _serviceCts = appLifetime is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(appLifetime.ApplicationStopping)
            : new CancellationTokenSource();

        var maxProcesses = Math.Max(1, _options.MaxConcurrentUsageProcesses);
        _processThrottle = new SemaphoreSlim(maxProcesses, maxProcesses);
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
                inFlightTask = ExecuteRefreshAsync(profile);
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var tasks = profiles.Select(async p =>
        {
            try
            {
                var res = await RefreshAsync(p, force: false, cancellationToken).ConfigureAwait(false);
                return (p.Id, Result: res);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var err = ErrorInfo.Create(ErrorCategory.Timeout, "Refresh operation was cancelled by caller.", _clock.UtcNow);
                return (p.Id, Result: UsageFetchResult.Fail(UsageStatus.Error, err));
            }
            catch (Exception ex)
            {
                var err = ErrorInfo.Create(ErrorCategory.Unknown, ex.Message, _clock.UtcNow);
                return (p.Id, Result: UsageFetchResult.Fail(UsageStatus.Error, err));
            }
        }).ToList();

        var completed = await Task.WhenAll(tasks).ConfigureAwait(false);
        return completed.ToDictionary(c => c.Id, c => c.Result);
    }

    private async Task<UsageFetchResult> ExecuteRefreshAsync(ProfileMetadata profile)
    {
        try
        {
            byte[] authBytes;
            string originalFingerprint;

            // Step 1: Read current credentials under coordinator lock
            using (await _coordinator.LockAsync(profile.Id, _serviceCts.Token).ConfigureAwait(false))
            {
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

            // Step 2: Acquire process throttle to bound concurrent child app-server processes
            UsageFetchResult fetchResult;
            await _processThrottle.WaitAsync(_serviceCts.Token).ConfigureAwait(false);
            try
            {
                fetchResult = await _provider.FetchRateLimitsAsync(profile.Id, authBytes, _serviceCts.Token).ConfigureAwait(false);
            }
            finally
            {
                _processThrottle.Release();
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
        catch (OperationCanceledException) when (_serviceCts.IsCancellationRequested)
        {
            var err = ErrorInfo.Create(ErrorCategory.Timeout, "Service shutdown.", _clock.UtcNow);
            return UsageFetchResult.Fail(UsageStatus.ProcessDown, err);
        }
        catch (Exception ex)
        {
            var err = ErrorInfo.Create(ErrorCategory.Unknown, ex.Message, _clock.UtcNow);
            _cache.Set(profile.Id, null, UsageStatus.Error, err);
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
        _processThrottle.Dispose();
    }
}

