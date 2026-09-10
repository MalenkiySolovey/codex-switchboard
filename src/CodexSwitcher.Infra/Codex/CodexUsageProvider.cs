using System.Text.RegularExpressions;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Infra.Codex;

/// <summary>
/// Queries rate limits for a stored account profile in an isolated sandbox.
/// Ensures that the user's active %USERPROFILE%\.codex\auth.json is NEVER accessed, modified, or synchronized.
/// </summary>
public sealed class CodexUsageProvider : ICodexUsageProvider
{
    private static readonly Regex TokenPattern = new(@"ey[A-Za-z0-9_-]{10,}\.[A-Za-z0-9._-]+", RegexOptions.Compiled);

    private readonly IFileSystem _fs;
    private readonly string _tempRoot;
    private readonly string? _codexExecutablePath;
    private readonly Func<string, string, ICodexAppServerClient> _clientFactory;
    private readonly TimeSpan _timeout;
    private readonly ICodexCapabilityCache _capabilityCache;
    private readonly Func<string?>? _executablePathAccessor;

    public CodexUsageProvider(
        IFileSystem fs,
        string tempRoot,
        string? codexExecutablePath = null,
        Func<string, string, ICodexAppServerClient>? clientFactory = null,
        TimeSpan? timeout = null,
        ICodexCapabilityCache? capabilityCache = null,
        Func<string?>? executablePathAccessor = null)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        ArgumentException.ThrowIfNullOrWhiteSpace(tempRoot);
        _tempRoot = tempRoot;
        _codexExecutablePath = codexExecutablePath ?? CodexCliRunner.ResolveCodexPath();
        _clientFactory = clientFactory ?? ((exe, home) => new CodexAppServerClient(exe, home));
        _timeout = timeout ?? TimeSpan.FromSeconds(25);
        _capabilityCache = capabilityCache ?? new CodexSwitcher.Core.Services.CodexCapabilityCache();
        _executablePathAccessor = executablePathAccessor;
    }

    public async Task<UsageFetchResult> FetchRateLimitsAsync(
        Guid profileId,
        byte[] authJsonBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authJsonBytes);

        if (authJsonBytes.Length == 0)
        {
            var err = ErrorInfo.Create(ErrorCategory.InvalidAuthFile, "auth.json content is empty.", DateTimeOffset.UtcNow);
            return UsageFetchResult.Fail(UsageStatus.Error, err);
        }

        var exePath = _executablePathAccessor?.Invoke() ?? _codexExecutablePath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            exePath = CodexCliRunner.ResolveCodexPath();
        }
        if (string.IsNullOrWhiteSpace(exePath))
        {
            var err = ErrorInfo.Create(ErrorCategory.CodexNotFound, "Codex executable not found on the system.", DateTimeOffset.UtcNow);
            return UsageFetchResult.Fail(UsageStatus.ProcessDown, err);
        }

        ProfileSandbox? sandbox = null;
        ICodexAppServerClient? client = null;

        try
        {
            sandbox = ProfileSandbox.Create(_tempRoot, profileId, authJsonBytes, _fs);
            client = _clientFactory(exePath, sandbox.DirectoryPath);

            await client.StartAsync(cancellationToken).ConfigureAwait(false);

            // Step 1: account/read {"refreshToken": false}
            var accountElement = await client.RequestAsync(
                "account/read",
                new { refreshToken = false },
                _timeout,
                cancellationToken).ConfigureAwait(false);

            var (accountType, email, planType, requiresOpenaiAuth) = CodexUsageResponseParser.ParseAccountInfo(accountElement);

            // Note: requiresOpenaiAuth indicates that the provider requires OpenAI OAuth tokens;
            // it is TRUE for valid ChatGPT accounts and is NOT an error indicator.
            // An unauthenticated state is recognized when the account object itself is missing/null.
            if (string.IsNullOrWhiteSpace(accountType))
            {
                var err = ErrorInfo.Create(ErrorCategory.RefreshTokenExpired, "Account is unauthenticated (account object is null).", DateTimeOffset.UtcNow);
                return UsageFetchResult.Fail(UsageStatus.AuthRequired, err);
            }

            if (!string.Equals(accountType, "chatgpt", StringComparison.OrdinalIgnoreCase))
            {
                var err = ErrorInfo.Create(ErrorCategory.InvalidAuthFile, $"Account type '{accountType}' is not supported for quota monitoring. Only 'chatgpt' accounts are supported.", DateTimeOffset.UtcNow);
                return UsageFetchResult.Fail(UsageStatus.UnsupportedAccountType, err);
            }

            // Step 2: account/rateLimits/read
            RateLimitsSnapshot? snapshot = null;
            ErrorInfo? rateLimitsError = null;
            try
            {
                var rateLimitsElement = await client.RequestAsync(
                    "account/rateLimits/read",
                    new { },
                    _timeout,
                    cancellationToken).ConfigureAwait(false);

                var (primaryLimitId, buckets, resetCredits, resetCreditsDetail) = CodexUsageResponseParser.ParseRateLimitsDetail(rateLimitsElement);

                // Authoritative backend rate limit check (do NOT infer solely from UsedPercent >= 100)
                var isRateLimited = buckets.Any(b => !string.IsNullOrWhiteSpace(b.RateLimitReachedType));
                var status = isRateLimited ? UsageStatus.RateLimited : UsageStatus.Healthy;
                var effectivePlan = planType ?? buckets.FirstOrDefault(b => b.LimitId == (primaryLimitId ?? "codex"))?.PlanType;

                snapshot = new RateLimitsSnapshot(
                    ProfileId: profileId,
                    ObservedAt: DateTimeOffset.UtcNow,
                    PrimaryLimitId: primaryLimitId,
                    Limits: buckets,
                    ResetCreditsAvailable: resetCredits,
                    PlanType: effectivePlan,
                    AccountEmail: email,
                    Status: status,
                    ResetCreditsDetail: resetCreditsDetail);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                rateLimitsError = ErrorInfo.Create(ErrorCategory.Unknown, Sanitize(ex.Message), DateTimeOffset.UtcNow);
            }

            // Step 3: Optional soft account/usage/read RPC (Phase 4 / 4.5)
            AccountActivitySnapshot? activity = null;
            ErrorInfo? activityError = null;
            AccountActivityAvailability activityAvailability = AccountActivityAvailability.Unknown;

            var exeIdentity = CodexRuntimeResolver.GetExecutableIdentity(exePath);
            var cachedCaps = _capabilityCache.GetCapabilities(exeIdentity);

            if (cachedCaps?.AccountUsageRead == CapabilityStatus.Unsupported)
            {
                // Runtime is known not to support account/usage/read; bypass RPC completely
                activityAvailability = AccountActivityAvailability.UnsupportedRuntime;
            }
            else
            {
                try
                {
                    var usageElement = await client.RequestAsync(
                        "account/usage/read",
                        new { },
                        _timeout,
                        cancellationToken).ConfigureAwait(false);

                    activity = CodexUsageResponseParser.ParseAccountActivity(profileId, usageElement);
                    activityAvailability = AccountActivityAvailability.Available;

                    _capabilityCache.SetCapabilities(exeIdentity, CodexRuntimeCapabilities.ModernFull);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    var sanitized = Sanitize(ex.Message);
                    activityError = ErrorInfo.Create(ErrorCategory.Unknown, sanitized, DateTimeOffset.UtcNow);

                    // Check for authoritative unsupported variant indicator (-32601 or unknown variant)
                    if (sanitized.Contains("unknown variant 'account/usage/read'", StringComparison.OrdinalIgnoreCase) ||
                        sanitized.Contains("unknown variant `account/usage/read`", StringComparison.OrdinalIgnoreCase) ||
                        sanitized.Contains("-32601") ||
                        sanitized.Contains("Method not found", StringComparison.OrdinalIgnoreCase))
                    {
                        activityAvailability = AccountActivityAvailability.UnsupportedRuntime;
                        _capabilityCache.SetCapabilities(exeIdentity, CodexRuntimeCapabilities.LegacyUnsupportedUsage);
                    }
                    else
                    {
                        activityAvailability = AccountActivityAvailability.TemporarilyUnavailable;
                    }
                }
            }

            var (mutated, rotatedBytes, _) = sandbox.InspectMutation();

            // Handle partial / full success
            if (snapshot is not null)
            {
                return UsageFetchResult.Ok(
                    snapshot,
                    activity: activity,
                    sandboxAuthMutated: mutated,
                    rotatedAuthJson: mutated ? rotatedBytes : null,
                    activityAvailability: activityAvailability);
            }

            if (activity is not null)
            {
                // Activity succeeded, but rate limits failed
                return UsageFetchResult.Partial(
                    snapshot: null,
                    activity: activity,
                    status: UsageStatus.Healthy,
                    sandboxAuthMutated: mutated,
                    rotatedAuthJson: mutated ? rotatedBytes : null,
                    error: rateLimitsError,
                    activityAvailability: activityAvailability);
            }

            // Both failed
            var primaryError = rateLimitsError ?? activityError ?? ErrorInfo.Create(ErrorCategory.Unknown, "Both rate limits and activity queries failed.", DateTimeOffset.UtcNow);
            return UsageFetchResult.Fail(UsageStatus.Error, primaryError, activityAvailability);
        }
        catch (TimeoutException tex)
        {
            var err = ErrorInfo.Create(ErrorCategory.Timeout, Sanitize(tex.Message), DateTimeOffset.UtcNow);
            return UsageFetchResult.Fail(UsageStatus.ProcessDown, err);
        }
        catch (InvalidOperationException iex)
        {
            var sanitized = Sanitize(iex.Message);
            var isAuthError = sanitized.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
                              sanitized.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                              sanitized.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);

            var category = isAuthError ? ErrorCategory.RefreshTokenExpired : ErrorCategory.Unknown;
            var status = isAuthError ? UsageStatus.AuthRequired : UsageStatus.Error;

            return UsageFetchResult.Fail(status, ErrorInfo.Create(category, sanitized, DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var err = ErrorInfo.Create(ErrorCategory.Unknown, Sanitize(ex.Message), DateTimeOffset.UtcNow);
            return UsageFetchResult.Fail(UsageStatus.Error, err);
        }
        finally
        {
            if (client is not null)
            {
                try
                {
                    await client.StopAsync().ConfigureAwait(false);
                    client.Dispose();
                }
                catch { }
            }

            sandbox?.Dispose();
        }
    }

    private static string Sanitize(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return string.Empty;

        return TokenPattern.Replace(message, "[REDACTED_TOKEN]");
    }
}
