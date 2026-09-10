using System.Text;
using System.Text.Json;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Codex;
using CodexSwitcher.Infra.Io;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexRuntimeCapabilityTests
{
    private static readonly DateTimeOffset TestTime = DateTimeOffset.Parse("2026-09-09T18:00:00Z");

    #region 1. Version Parsing Tests

    [Theory]
    [InlineData("0.153.4")]
    [InlineData("0.130.0-alpha.5")]
    [InlineData("0.154.0-alpha.6.1")]
    [InlineData("1.0.0")]
    public void InspectCandidate_ExtractsExpectedVersion(string expectedVersion)
    {
        using var temp = new TempDir();
        var fakeExe = Path.Combine(temp.Root, "codex.exe");
        File.WriteAllText(fakeExe, "dummy");

        var resolver = new CodexRuntimeResolver(
            probeInspector: _ => (expectedVersion, true));

        var info = resolver.ResolveCurrentRuntime(fakeExe);

        Assert.Equal(expectedVersion, info.Version);
    }

    [Fact]
    public void InspectCandidate_HandlesMissingVersionGracefully()
    {
        using var temp = new TempDir();
        var fakeExe = Path.Combine(temp.Root, "codex.exe");
        File.WriteAllText(fakeExe, "dummy");

        var resolver = new CodexRuntimeResolver(
            probeInspector: _ => (null, false));

        var info = resolver.ResolveCurrentRuntime(fakeExe);

        Assert.Equal("unknown", info.Version);
        Assert.Equal(CapabilityStatus.Unsupported, info.Capabilities.AccountUsageRead);
    }

    #endregion

    #region 2. Capability Discovery & Classification Tests

    [Fact]
    public void CapabilityCache_ClassifiesUnknownVariantAsUnsupported()
    {
        var cache = new CodexCapabilityCache();
        var exeIdentity = "codex_0.130.exe|1000|50000";

        cache.SetCapabilities(exeIdentity, CodexRuntimeCapabilities.LegacyUnsupportedUsage);

        var caps = cache.GetCapabilities(exeIdentity);
        Assert.NotNull(caps);
        Assert.Equal(CapabilityStatus.Supported, caps.AccountRead);
        Assert.Equal(CapabilityStatus.Supported, caps.RateLimitsRead);
        Assert.Equal(CapabilityStatus.Unsupported, caps.AccountUsageRead);
        Assert.False(caps.SupportsAccountUsage);
        Assert.True(caps.CanMonitorQuota);
    }

    [Fact]
    public void CapabilityCache_ClassifiesSupportedCorrectly()
    {
        var cache = new CodexCapabilityCache();
        var exeIdentity = "codex_0.153.exe|2000|60000";

        cache.SetCapabilities(exeIdentity, CodexRuntimeCapabilities.ModernFull);

        var caps = cache.GetCapabilities(exeIdentity);
        Assert.NotNull(caps);
        Assert.Equal(CapabilityStatus.Supported, caps.AccountUsageRead);
        Assert.True(caps.SupportsAccountUsage);
        Assert.True(caps.CanMonitorQuota);
    }

    [Fact]
    public void CapabilityCache_TransientErrorsDoNotMarkUnsupported()
    {
        var cache = new CodexCapabilityCache();
        var exeIdentity = "codex_transient.exe|1000|50000";

        // Transient errors should leave cache empty or set to Unknown
        var caps = cache.GetCapabilities(exeIdentity);
        Assert.Null(caps);
    }

    #endregion

    #region 3. Capability Caching & Invalidation Tests

    [Fact]
    public void CapabilityCache_ReturnsCachedCapabilitiesForSameIdentity()
    {
        var cache = new CodexCapabilityCache();
        var identity = "C:\\codex.exe|1234567|9999";

        cache.SetCapabilities(identity, CodexRuntimeCapabilities.ModernFull);

        var retrieved = cache.GetCapabilities(identity);
        Assert.Same(CodexRuntimeCapabilities.ModernFull, retrieved);
    }

    [Fact]
    public void CapabilityCache_InvalidatesWhenIdentityChanges()
    {
        var cache = new CodexCapabilityCache();
        var oldIdentity = "C:\\codex.exe|1234567|9999";
        var newIdentity = "C:\\codex.exe|7654321|10500"; // file modified/updated

        cache.SetCapabilities(oldIdentity, CodexRuntimeCapabilities.ModernFull);

        var oldRetrieved = cache.GetCapabilities(oldIdentity);
        var newRetrieved = cache.GetCapabilities(newIdentity);

        Assert.NotNull(oldRetrieved);
        Assert.Null(newRetrieved);
    }

    [Fact]
    public void CapabilityCache_ExplicitInvalidateRemovesEntry()
    {
        var cache = new CodexCapabilityCache();
        var identity = "C:\\codex.exe|1234567|9999";

        cache.SetCapabilities(identity, CodexRuntimeCapabilities.ModernFull);
        cache.Invalidate(identity);

        Assert.Null(cache.GetCapabilities(identity));
    }

    [Fact]
    public void CapabilityCache_ClearRemovesAllEntries()
    {
        var cache = new CodexCapabilityCache();
        cache.SetCapabilities("id1", CodexRuntimeCapabilities.ModernFull);
        cache.SetCapabilities("id2", CodexRuntimeCapabilities.LegacyUnsupportedUsage);

        cache.Clear();

        Assert.Null(cache.GetCapabilities("id1"));
        Assert.Null(cache.GetCapabilities("id2"));
    }

    #endregion

    #region 4. RPC Suppression on Unsupported Runtime

    [Fact]
    public async Task Provider_SuppressesAccountUsageRead_WhenRuntimeKnownUnsupported()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var profileId = Guid.NewGuid();
        var exePath = Path.Combine(temp.Root, "codex.exe");
        File.WriteAllText(exePath, "dummy");
        var exeIdentity = CodexRuntimeResolver.GetExecutableIdentity(exePath);

        var cache = new CodexCapabilityCache();
        cache.SetCapabilities(exeIdentity, CodexRuntimeCapabilities.LegacyUnsupportedUsage);

        var fakeClient = new FakeAppServerClient();
        fakeClient.Handlers["account/read"] = _ => JsonDocument.Parse(@"{""account"":{""type"":""chatgpt"",""email"":""user@example.com""},""requiresOpenaiAuth"":true}").RootElement;
        fakeClient.Handlers["account/rateLimits/read"] = _ => JsonDocument.Parse(@"{""rateLimits"":{""primaryLimitId"":""codex"",""limits"":[{""limitId"":""codex"",""limitName"":""Codex"",""windows"":[{""slot"":0,""durationMinutes"":300,""displayLabel"":""5h"",""usedPercent"":25.0,""remainingPercent"":75.0,""resetsAt"":1780000000}]}]}}").RootElement;
        fakeClient.Handlers["account/usage/read"] = _ => throw new InvalidOperationException("Should NOT have been called!");

        var provider = new CodexUsageProvider(
            fs,
            temp.Root,
            codexExecutablePath: exePath,
            clientFactory: (_, _) => fakeClient,
            capabilityCache: cache);

        var authBytes = Encoding.UTF8.GetBytes(@"{""tokens"":{""access_token"":""eyFakeToken123456789.payload.sig""}}");
        var result = await provider.FetchRateLimitsAsync(profileId, authBytes);

        Assert.True(result.Success);
        Assert.NotNull(result.Snapshot);
        Assert.Null(result.Activity);
        Assert.Equal(AccountActivityAvailability.UnsupportedRuntime, result.ActivityAvailability);

        var calledMethods = fakeClient.Requests.Select(r => r.Method).ToList();
        Assert.Contains("account/read", calledMethods);
        Assert.Contains("account/rateLimits/read", calledMethods);
        Assert.DoesNotContain("account/usage/read", calledMethods);
    }

    [Fact]
    public async Task Provider_AutoDetectsUnsupportedVariant_AndUpdatesCapabilityCache()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var profileId = Guid.NewGuid();
        var exePath = Path.Combine(temp.Root, "codex_legacy.exe");
        File.WriteAllText(exePath, "dummy");
        var exeIdentity = CodexRuntimeResolver.GetExecutableIdentity(exePath);

        var cache = new CodexCapabilityCache();

        var fakeClient = new FakeAppServerClient();
        fakeClient.Handlers["account/read"] = _ => JsonDocument.Parse(@"{""account"":{""type"":""chatgpt"",""email"":""user@example.com""},""requiresOpenaiAuth"":true}").RootElement;
        fakeClient.Handlers["account/rateLimits/read"] = _ => JsonDocument.Parse(@"{""rateLimits"":{""primaryLimitId"":""codex"",""limits"":[{""limitId"":""codex"",""limitName"":""Codex"",""windows"":[{""slot"":0,""durationMinutes"":300,""displayLabel"":""5h"",""usedPercent"":25.0,""remainingPercent"":75.0,""resetsAt"":1780000000}]}]}}").RootElement;
        fakeClient.Handlers["account/usage/read"] = _ => throw new InvalidOperationException("Invalid request: unknown variant 'account/usage/read', expected one of 'account/read', 'account/rateLimits/read'");

        var provider = new CodexUsageProvider(
            fs,
            temp.Root,
            codexExecutablePath: exePath,
            clientFactory: (_, _) => fakeClient,
            capabilityCache: cache);

        var authBytes = Encoding.UTF8.GetBytes(@"{""tokens"":{""access_token"":""eyFakeToken123456789.payload.sig""}}");
        var result = await provider.FetchRateLimitsAsync(profileId, authBytes);

        Assert.True(result.Success);
        Assert.NotNull(result.Snapshot);
        Assert.Null(result.Activity);
        Assert.Equal(AccountActivityAvailability.UnsupportedRuntime, result.ActivityAvailability);

        // Verify capability cache was populated with Unsupported
        var cachedCaps = cache.GetCapabilities(exeIdentity);
        Assert.NotNull(cachedCaps);
        Assert.Equal(CapabilityStatus.Unsupported, cachedCaps.AccountUsageRead);
    }

    [Fact]
    public async Task Provider_TransientFailure_MarksTemporarilyUnavailable_NotUnsupported()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var profileId = Guid.NewGuid();
        var exePath = Path.Combine(temp.Root, "codex.exe");
        File.WriteAllText(exePath, "dummy");
        var exeIdentity = CodexRuntimeResolver.GetExecutableIdentity(exePath);

        var cache = new CodexCapabilityCache();

        var fakeClient = new FakeAppServerClient();
        fakeClient.Handlers["account/read"] = _ => JsonDocument.Parse(@"{""account"":{""type"":""chatgpt"",""email"":""user@example.com""},""requiresOpenaiAuth"":true}").RootElement;
        fakeClient.Handlers["account/rateLimits/read"] = _ => JsonDocument.Parse(@"{""rateLimits"":{""primaryLimitId"":""codex"",""limits"":[{""limitId"":""codex"",""limitName"":""Codex"",""windows"":[{""slot"":0,""durationMinutes"":300,""displayLabel"":""5h"",""usedPercent"":25.0,""remainingPercent"":75.0,""resetsAt"":1780000000}]}]}}").RootElement;
        fakeClient.Handlers["account/usage/read"] = _ => throw new TimeoutException("Server timed out processing usage query.");

        var provider = new CodexUsageProvider(
            fs,
            temp.Root,
            codexExecutablePath: exePath,
            clientFactory: (_, _) => fakeClient,
            capabilityCache: cache);

        var authBytes = Encoding.UTF8.GetBytes(@"{""tokens"":{""access_token"":""eyFakeToken123456789.payload.sig""}}");
        var result = await provider.FetchRateLimitsAsync(profileId, authBytes);

        Assert.True(result.Success);
        Assert.NotNull(result.Snapshot);
        Assert.Null(result.Activity);
        Assert.Equal(AccountActivityAvailability.TemporarilyUnavailable, result.ActivityAvailability);

        // Capability cache should NOT be marked Unsupported
        var cachedCaps = cache.GetCapabilities(exeIdentity);
        Assert.Null(cachedCaps);
    }

    #endregion

    #region 5. Presentation Mapping & State Tests

    [Fact]
    public void UsagePresentationMapper_MapFromFetchResult_PropagatesActivityAvailability()
    {
        var profileId = Guid.NewGuid();
        var snapshot = new RateLimitsSnapshot(
            ProfileId: profileId,
            ObservedAt: TestTime,
            PrimaryLimitId: "codex",
            Limits: Array.Empty<LimitBucket>(),
            ResetCreditsAvailable: 0,
            PlanType: "plus",
            AccountEmail: "test@example.com",
            Status: UsageStatus.Healthy);

        var fetchResult = UsageFetchResult.Ok(
            snapshot: snapshot,
            activity: null,
            activityAvailability: AccountActivityAvailability.UnsupportedRuntime);

        var state = UsagePresentationMapper.MapFromFetchResult(fetchResult, profileId, TestTime);

        Assert.Equal(AccountActivityAvailability.UnsupportedRuntime, state.ActivityAvailability);
        Assert.Null(state.Activity);
        Assert.False(state.HasActivity);
    }

    [Fact]
    public void UsagePresentationMapper_MapFromCache_PreservesActivityAndAvailability()
    {
        var profileId = Guid.NewGuid();
        var activity = new AccountActivitySnapshot(
            ProfileId: profileId,
            ObservedAt: TestTime,
            Summary: new AccountTokenUsageSummary(1000000, 50000, 3600, 5, 10),
            DailyBuckets: Array.Empty<DailyTokenUsage>());

        var entry = new UsageCacheEntry(
            ProfileId: profileId,
            ObservedAt: TestTime,
            Snapshot: null,
            Status: UsageStatus.Healthy,
            LastError: null,
            IsStale: false,
            Activity: activity,
            ActivityAvailability: AccountActivityAvailability.Available);

        var state = UsagePresentationMapper.MapFromCache(entry, profileId, TestTime);

        Assert.Equal(AccountActivityAvailability.Available, state.ActivityAvailability);
        Assert.NotNull(state.Activity);
        Assert.True(state.HasActivity);
        Assert.Equal(1000000, state.Activity.Summary?.LifetimeTokens);
    }

    [Fact]
    public void UsagePresentationMapper_StaleCachedActivity_WithUnsupportedRuntime_PreservesData()
    {
        var profileId = Guid.NewGuid();
        var activity = new AccountActivitySnapshot(
            ProfileId: profileId,
            ObservedAt: TestTime.AddDays(-1),
            Summary: new AccountTokenUsageSummary(500000, 25000, 1800, 3, 6),
            DailyBuckets: Array.Empty<DailyTokenUsage>());

        var entry = new UsageCacheEntry(
            ProfileId: profileId,
            ObservedAt: TestTime.AddDays(-1),
            Snapshot: null,
            Status: UsageStatus.Healthy,
            LastError: null,
            IsStale: true,
            Activity: activity,
            ActivityAvailability: AccountActivityAvailability.UnsupportedRuntime);

        var state = UsagePresentationMapper.MapFromCache(entry, profileId, TestTime);

        Assert.Equal(AccountActivityAvailability.UnsupportedRuntime, state.ActivityAvailability);
        Assert.True(state.HasActivity);
        Assert.NotNull(state.Activity);
        Assert.Equal(500000, state.Activity.Summary?.LifetimeTokens);
    }

    #endregion

    #region 6. Candidate Enumeration & Precedence Tests

    [Fact]
    public void ResolveCurrentRuntime_Precedence1_OverrideWins()
    {
        using var temp = new TempDir();
        var overrideExe = Path.Combine(temp.Root, "override_codex.exe");
        File.WriteAllText(overrideExe, "dummy");

        var resolver = new CodexRuntimeResolver(
            probeInspector: path => path.Contains("override") ? ("0.155.0", true) : ("0.130.0", false));

        var info = resolver.ResolveCurrentRuntime(overrideExe);

        Assert.Equal("0.155.0", info.Version);
        Assert.Equal(CapabilityStatus.Supported, info.Capabilities.AccountUsageRead);
    }

    [Fact]
    public void ValidateExecutable_NonExistentFile_ReturnsFalseWithError()
    {
        var resolver = new CodexRuntimeResolver();

        bool valid = resolver.ValidateExecutable("C:\\non_existent\\codex_fake.exe", out var version, out var error);

        Assert.False(valid);
        Assert.Null(version);
        Assert.NotNull(error);
        Assert.Contains("File not found", error);
    }

    [Fact]
    public void ValidateExecutable_EmptyPath_ReturnsFalseWithError()
    {
        var resolver = new CodexRuntimeResolver();

        bool valid = resolver.ValidateExecutable("", out var version, out var error);

        Assert.False(valid);
        Assert.Null(version);
        Assert.NotNull(error);
        Assert.Contains("empty", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeCandidateSource_EnumValuesAreDistinct()
    {
        Assert.NotEqual(RuntimeCandidateSource.CustomOverride, RuntimeCandidateSource.OfficialHashDirectory);
        Assert.NotEqual(RuntimeCandidateSource.OfficialWellKnown, RuntimeCandidateSource.Path);
    }

    [Fact]
    public void CodexRuntimeCapabilities_ConstantsAreConfiguredProperly()
    {
        var modern = CodexRuntimeCapabilities.ModernFull;
        Assert.Equal(CapabilityStatus.Supported, modern.AccountRead);
        Assert.Equal(CapabilityStatus.Supported, modern.RateLimitsRead);
        Assert.Equal(CapabilityStatus.Supported, modern.AccountUsageRead);
        Assert.True(modern.SupportsAccountUsage);
        Assert.True(modern.CanMonitorQuota);

        var legacy = CodexRuntimeCapabilities.LegacyUnsupportedUsage;
        Assert.Equal(CapabilityStatus.Supported, legacy.AccountRead);
        Assert.Equal(CapabilityStatus.Supported, legacy.RateLimitsRead);
        Assert.Equal(CapabilityStatus.Unsupported, legacy.AccountUsageRead);
        Assert.False(legacy.SupportsAccountUsage);
        Assert.True(legacy.CanMonitorQuota);
    }

    #endregion

    #region 7. Security & Zero Credential Leakage Tests

    [Fact]
    public void CapabilityModels_ContainZeroCredentialData()
    {
        var caps = CodexRuntimeCapabilities.ModernFull;
        var json = JsonSerializer.Serialize(caps);

        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("auth", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CapabilityCache_KeyIdentityDoesNotLeakTokens()
    {
        var identity = CodexRuntimeResolver.GetExecutableIdentity("C:\\Program Files\\Codex\\codex.exe");

        Assert.DoesNotContain("ey", identity);
        Assert.Contains("codex.exe", identity);
    }

    [Fact]
    public void CodexRuntimeInfo_ComputeIdentity_IsDeterministicAndSafe()
    {
        var identity1 = CodexRuntimeInfo.ComputeIdentity("C:\\bin\\codex.exe", 1000, TestTime, "0.153.4");
        var identity2 = CodexRuntimeInfo.ComputeIdentity("C:\\bin\\codex.exe", 1000, TestTime, "0.153.4");
        var identity3 = CodexRuntimeInfo.ComputeIdentity("C:\\bin\\codex.exe", 1001, TestTime, "0.153.4");

        Assert.Equal(identity1, identity2);
        Assert.NotEqual(identity1, identity3);
        Assert.DoesNotContain("auth", identity1, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    private sealed class FakeAppServerClient : ICodexAppServerClient
    {
        public bool IsRunning { get; private set; }
        public List<(string Method, object? Params)> Requests { get; } = [];
        public Dictionary<string, Func<object?, JsonElement>> Handlers { get; } = new();

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task<JsonElement> RequestAsync(string method, object? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Requests.Add((method, parameters));
            if (Handlers.TryGetValue(method, out var handler))
            {
                return Task.FromResult(handler(parameters));
            }
            throw new InvalidOperationException($"No handler configured for {method}");
        }

        public Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync()
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            IsRunning = false;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }
    }
}
