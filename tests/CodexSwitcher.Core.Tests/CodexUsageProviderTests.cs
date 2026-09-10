using System.Text;
using System.Text.Json;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Security;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Codex;
using CodexSwitcher.Infra.Io;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexUsageProviderTests
{
    private sealed class FakeAppServerClient : ICodexAppServerClient
    {
        public bool IsRunning { get; private set; }
        public bool StartCalled { get; private set; }
        public bool StopCalled { get; private set; }
        public List<(string Method, object? Params)> Requests { get; } = [];
        public Dictionary<string, Func<object?, JsonElement>> Handlers { get; } = new();
        public Action? OnRateLimitsRequested { get; set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalled = true;
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task<JsonElement> RequestAsync(string method, object? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Requests.Add((method, parameters));

            if (method == "account/rateLimits/read")
            {
                OnRateLimitsRequested?.Invoke();
            }

            if (Handlers.TryGetValue(method, out var handler))
            {
                return Task.FromResult(handler(parameters));
            }
            throw new InvalidOperationException($"No handler configured for {method}");
        }

        public Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            StopCalled = true;
            IsRunning = false;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            IsRunning = false;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task FetchRateLimitsAsync_SuccessfulFlow_ReturnsSnapshotWithWindows()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var profileId = Guid.NewGuid();
        var authBytes = Encoding.UTF8.GetBytes("{\"tokens\":{\"access_token\":\"dummy\"}}");

        var fakeClient = new FakeAppServerClient();
        fakeClient.Handlers["account/read"] = _ => ParseJson("""
        {
            "requiresOpenaiAuth": false,
            "account": {
                "type": "chatgpt",
                "email": "user@example.com",
                "planType": "plus"
            }
        }
        """);

        fakeClient.Handlers["account/rateLimits/read"] = _ => ParseJson("""
        {
            "rateLimitsByLimitId": {
                "codex": {
                    "limitId": "codex",
                    "planType": "plus",
                    "primary": {
                        "windowDurationMins": 300,
                        "usedPercent": 20.0,
                        "resetsAt": 1774390000
                    },
                    "secondary": {
                        "windowDurationMins": 10080,
                        "usedPercent": 60.0,
                        "resetsAt": 1774900000
                    }
                }
            },
            "rateLimitResetCredits": {
                "availableCount": 2
            }
        }
        """);

        var provider = new CodexUsageProvider(
            fs: fs,
            tempRoot: temp.Root,
            codexExecutablePath: "dummy.exe",
            clientFactory: (exe, home) => fakeClient);

        var result = await provider.FetchRateLimitsAsync(profileId, authBytes);

        Assert.True(result.Success);
        Assert.Equal(UsageStatus.Healthy, result.Status);
        Assert.NotNull(result.Snapshot);

        var snapshot = result.Snapshot!;
        Assert.Equal(profileId, snapshot.ProfileId);
        Assert.Equal("codex", snapshot.PrimaryLimitId);
        Assert.Equal("plus", snapshot.PlanType);
        Assert.Equal("user@example.com", snapshot.AccountEmail);
        Assert.Equal(2, snapshot.ResetCreditsAvailable);

        Assert.NotNull(snapshot.PrimaryWindow);
        Assert.Equal(300, snapshot.PrimaryWindow!.DurationMinutes);
        Assert.Equal("5h", snapshot.PrimaryWindow!.DisplayLabel);
        Assert.Equal(20.0, snapshot.PrimaryWindow!.UsedPercent);
        Assert.Equal(80.0, snapshot.PrimaryWindow!.RemainingPercent);

        Assert.NotNull(snapshot.SecondaryWindow);
        Assert.Equal(10080, snapshot.SecondaryWindow!.DurationMinutes);
        Assert.Equal("7d", snapshot.SecondaryWindow!.DisplayLabel);

        Assert.False(result.SandboxAuthMutated);
        Assert.Null(result.RotatedAuthJson);
        Assert.True(fakeClient.StartCalled);
        Assert.True(fakeClient.StopCalled);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_WhenRequiresAuth_ReturnsAuthRequired()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var profileId = Guid.NewGuid();
        var authBytes = Encoding.UTF8.GetBytes("{\"tokens\":{\"access_token\":\"dummy\"}}");

        var fakeClient = new FakeAppServerClient();
        fakeClient.Handlers["account/read"] = _ => ParseJson("""
        {
            "requiresOpenaiAuth": true,
            "account": null
        }
        """);

        var provider = new CodexUsageProvider(
            fs: fs,
            tempRoot: temp.Root,
            codexExecutablePath: "dummy.exe",
            clientFactory: (exe, home) => fakeClient);

        var result = await provider.FetchRateLimitsAsync(profileId, authBytes);

        Assert.False(result.Success);
        Assert.Equal(UsageStatus.AuthRequired, result.Status);
        Assert.NotNull(result.Error);
        Assert.Equal(ErrorCategory.RefreshTokenExpired, result.Error!.Category);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_RateLimitReached_ReturnsRateLimitedStatus()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var profileId = Guid.NewGuid();
        var authBytes = Encoding.UTF8.GetBytes("{\"tokens\":{\"access_token\":\"dummy\"}}");

        var fakeClient = new FakeAppServerClient();
        fakeClient.Handlers["account/read"] = _ => ParseJson("""
        {
            "requiresOpenaiAuth": false,
            "account": { "type": "chatgpt", "email": "user@example.com" }
        }
        """);

        fakeClient.Handlers["account/rateLimits/read"] = _ => ParseJson("""
        {
            "rateLimitsByLimitId": {
                "codex": {
                    "limitId": "codex",
                    "rateLimitReachedType": "usage_limit_reached",
                    "primary": {
                        "windowDurationMins": 300,
                        "usedPercent": 100.0,
                        "resetsAt": 1774390000
                    }
                }
            }
        }
        """);

        var provider = new CodexUsageProvider(
            fs: fs,
            tempRoot: temp.Root,
            codexExecutablePath: "dummy.exe",
            clientFactory: (exe, home) => fakeClient);

        var result = await provider.FetchRateLimitsAsync(profileId, authBytes);

        Assert.True(result.Success);
        Assert.Equal(UsageStatus.RateLimited, result.Status);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(UsageStatus.RateLimited, result.Snapshot!.Status);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_WhenProcessTimesOut_ReturnsProcessDown()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var profileId = Guid.NewGuid();
        var authBytes = Encoding.UTF8.GetBytes("{\"tokens\":{\"access_token\":\"dummy\"}}");

        var fakeClient = new FakeAppServerClient();
        fakeClient.Handlers["account/read"] = _ => throw new TimeoutException("RPC request timed out waiting for 'account/read'.");

        var provider = new CodexUsageProvider(
            fs: fs,
            tempRoot: temp.Root,
            codexExecutablePath: "dummy.exe",
            clientFactory: (exe, home) => fakeClient);

        var result = await provider.FetchRateLimitsAsync(profileId, authBytes);

        Assert.False(result.Success);
        Assert.Equal(UsageStatus.ProcessDown, result.Status);
        Assert.NotNull(result.Error);
        Assert.Equal(ErrorCategory.Timeout, result.Error!.Category);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_WhenAuthJsonEmpty_ReturnsInvalidAuthFile()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var profileId = Guid.NewGuid();

        var provider = new CodexUsageProvider(
            fs: fs,
            tempRoot: temp.Root,
            codexExecutablePath: "dummy.exe");

        var result = await provider.FetchRateLimitsAsync(profileId, Array.Empty<byte>());

        Assert.False(result.Success);
        Assert.Equal(UsageStatus.Error, result.Status);
        Assert.NotNull(result.Error);
        Assert.Equal(ErrorCategory.InvalidAuthFile, result.Error!.Category);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_WhenSandboxMutates_CapturesRotatedCredentials()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var profileId = Guid.NewGuid();
        var initialAuth = Encoding.UTF8.GetBytes("{\"access_token\":\"initial_token\"}");
        var rotatedAuth = Encoding.UTF8.GetBytes("{\"access_token\":\"rotated_token_xyz\"}");

        string? capturedSandboxHome = null;

        var fakeClient = new FakeAppServerClient();
        fakeClient.Handlers["account/read"] = _ => ParseJson("{\"account\":{\"type\":\"chatgpt\",\"email\":\"user@example.com\"},\"requiresOpenaiAuth\": false}");
        fakeClient.Handlers["account/rateLimits/read"] = _ => ParseJson("""
        {
            "rateLimitsByLimitId": {
                "codex": {
                    "limitId": "codex",
                    "primary": { "windowDurationMins": 300, "usedPercent": 5.0 }
                }
            }
        }
        """);

        fakeClient.OnRateLimitsRequested = () =>
        {
            if (capturedSandboxHome is not null)
            {
                var authPath = Path.Combine(capturedSandboxHome, "auth.json");
                File.WriteAllBytes(authPath, rotatedAuth);
            }
        };

        var provider = new CodexUsageProvider(
            fs: fs,
            tempRoot: temp.Root,
            codexExecutablePath: "dummy.exe",
            clientFactory: (exe, home) =>
            {
                capturedSandboxHome = home;
                return fakeClient;
            });

        var result = await provider.FetchRateLimitsAsync(profileId, initialAuth);

        Assert.True(result.Success);
        Assert.True(result.SandboxAuthMutated);
        Assert.NotNull(result.RotatedAuthJson);
        Assert.Equal(rotatedAuth, result.RotatedAuthJson);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_ActiveSlotSafety_ActiveCodexHomeNeverTouched()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();

        // Simulate active slot in a mock active home
        var mockActiveHome = Path.Combine(temp.Root, "real-active-codex-home");
        Directory.CreateDirectory(mockActiveHome);
        var activeAuthPath = Path.Combine(mockActiveHome, "auth.json");
        var originalActiveBytes = Encoding.UTF8.GetBytes("{\"active_user\":\"REAL_ACTIVE_USER_DO_NOT_TOUCH\"}");
        File.WriteAllBytes(activeAuthPath, originalActiveBytes);
        var originalWriteTime = File.GetLastWriteTimeUtc(activeAuthPath);
        var originalFp = Fingerprint.Compute(originalActiveBytes);

        var sandboxTempRoot = Path.Combine(temp.Root, "isolated-sandboxes");

        var profileId = Guid.NewGuid();
        var queryProfileBytes = Encoding.UTF8.GetBytes("{\"target_profile\":\"PROFILE_BEING_QUERIED\"}");

        var fakeClient = new FakeAppServerClient();
        fakeClient.Handlers["account/read"] = _ => ParseJson("{\"account\":{\"type\":\"chatgpt\",\"email\":\"user@example.com\"},\"requiresOpenaiAuth\": false}");
        fakeClient.Handlers["account/rateLimits/read"] = _ => ParseJson("""
        {
            "rateLimitsByLimitId": {
                "codex": {
                    "limitId": "codex",
                    "primary": { "windowDurationMins": 300, "usedPercent": 10.0 }
                }
            }
        }
        """);

        var provider = new CodexUsageProvider(
            fs: fs,
            tempRoot: sandboxTempRoot,
            codexExecutablePath: "dummy.exe",
            clientFactory: (exe, home) => fakeClient);

        var result = await provider.FetchRateLimitsAsync(profileId, queryProfileBytes);

        Assert.True(result.Success);

        // Verify active slot was NEVER modified:
        var currentActiveBytes = File.ReadAllBytes(activeAuthPath);
        var currentFp = Fingerprint.Compute(currentActiveBytes);
        var currentWriteTime = File.GetLastWriteTimeUtc(activeAuthPath);

        Assert.Equal(originalActiveBytes, currentActiveBytes);
        Assert.Equal(originalFp, currentFp);
        Assert.Equal(originalWriteTime, currentWriteTime);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_ZeroTokenLeakage_SanitizesErrors()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var profileId = Guid.NewGuid();
        var authBytes = Encoding.UTF8.GetBytes("{\"tokens\":{\"access_token\":\"dummy\"}}");

        var fakeJwt = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJleHAiOjE5OTk5OTk5OTl9.signature_part_here_12345";

        var fakeClient = new FakeAppServerClient();
        fakeClient.Handlers["account/read"] = _ => throw new InvalidOperationException($"Failed request with secret token {fakeJwt} returned by server");

        var provider = new CodexUsageProvider(
            fs: fs,
            tempRoot: temp.Root,
            codexExecutablePath: "dummy.exe",
            clientFactory: (exe, home) => fakeClient);

        var result = await provider.FetchRateLimitsAsync(profileId, authBytes);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain(fakeJwt, result.Error!.Message);
        Assert.Contains("[REDACTED_TOKEN]", result.Error!.Message);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_WhenRequiresOpenaiAuthTrueWithValidAccount_SucceedsWithoutAuthRequired()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var profileId = Guid.NewGuid();
        var authBytes = Encoding.UTF8.GetBytes("{\"tokens\":{\"access_token\":\"dummy\"}}");

        var fakeClient = new FakeAppServerClient();
        // Valid ChatGPT account, with requiresOpenaiAuth = true (normal provider configuration)
        fakeClient.Handlers["account/read"] = _ => ParseJson("""
        {
            "requiresOpenaiAuth": true,
            "account": {
                "type": "chatgpt",
                "email": "user@example.com",
                "planType": "plus"
            }
        }
        """);

        fakeClient.Handlers["account/rateLimits/read"] = _ => ParseJson("""
        {
            "rateLimitsByLimitId": {
                "codex": {
                    "limitId": "codex",
                    "planType": "plus",
                    "primary": {
                        "windowDurationMins": 300,
                        "usedPercent": 10.0
                    }
                }
            }
        }
        """);

        var provider = new CodexUsageProvider(
            fs: fs,
            tempRoot: temp.Root,
            codexExecutablePath: "dummy.exe",
            clientFactory: (exe, home) => fakeClient);

        var result = await provider.FetchRateLimitsAsync(profileId, authBytes);

        // Crucial invariant: requiresOpenaiAuth == true does NOT trigger AuthRequired when account is valid
        Assert.True(result.Success);
        Assert.Equal(UsageStatus.Healthy, result.Status);
        Assert.NotNull(result.Snapshot);
        Assert.Equal("user@example.com", result.Snapshot!.AccountEmail);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_WeeklyOnlyWindow_ParsesSuccessfully()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var profileId = Guid.NewGuid();
        var authBytes = Encoding.UTF8.GetBytes("{\"tokens\":{\"access_token\":\"dummy\"}}");

        var fakeClient = new FakeAppServerClient();
        fakeClient.Handlers["account/read"] = _ => ParseJson("""
        {
            "account": { "type": "chatgpt", "email": "user@example.com" }
        }
        """);

        fakeClient.Handlers["account/rateLimits/read"] = _ => ParseJson("""
        {
            "rateLimitsByLimitId": {
                "codex": {
                    "limitId": "codex",
                    "secondary": {
                        "windowDurationMins": 10080,
                        "usedPercent": 55.0
                    }
                }
            }
        }
        """);

        var provider = new CodexUsageProvider(
            fs: fs,
            tempRoot: temp.Root,
            codexExecutablePath: "dummy.exe",
            clientFactory: (exe, home) => fakeClient);

        var result = await provider.FetchRateLimitsAsync(profileId, authBytes);

        Assert.True(result.Success);
        Assert.NotNull(result.Snapshot);
        var bucket = Assert.Single(result.Snapshot!.Limits);
        var window = Assert.Single(bucket.Windows);
        Assert.Equal(10080, window.DurationMinutes);
        Assert.Equal("7d", window.DisplayLabel);
        Assert.Equal(55.0, window.UsedPercent);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_5hOnlyWindow_ParsesSuccessfully()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var profileId = Guid.NewGuid();
        var authBytes = Encoding.UTF8.GetBytes("{\"tokens\":{\"access_token\":\"dummy\"}}");

        var fakeClient = new FakeAppServerClient();
        fakeClient.Handlers["account/read"] = _ => ParseJson("""
        {
            "account": { "type": "chatgpt", "email": "user@example.com" }
        }
        """);

        fakeClient.Handlers["account/rateLimits/read"] = _ => ParseJson("""
        {
            "rateLimitsByLimitId": {
                "codex": {
                    "limitId": "codex",
                    "primary": {
                        "windowDurationMins": 300,
                        "usedPercent": 15.0
                    }
                }
            }
        }
        """);

        var provider = new CodexUsageProvider(
            fs: fs,
            tempRoot: temp.Root,
            codexExecutablePath: "dummy.exe",
            clientFactory: (exe, home) => fakeClient);

        var result = await provider.FetchRateLimitsAsync(profileId, authBytes);

        Assert.True(result.Success);
        Assert.NotNull(result.Snapshot);
        var bucket = Assert.Single(result.Snapshot!.Limits);
        var window = Assert.Single(bucket.Windows);
        Assert.Equal(300, window.DurationMinutes);
        Assert.Equal("5h", window.DisplayLabel);
        Assert.Equal(15.0, window.UsedPercent);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_ArbitraryDurationWindow_ParsesSuccessfully()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var profileId = Guid.NewGuid();
        var authBytes = Encoding.UTF8.GetBytes("{\"tokens\":{\"access_token\":\"dummy\"}}");

        var fakeClient = new FakeAppServerClient();
        fakeClient.Handlers["account/read"] = _ => ParseJson("""
        {
            "account": { "type": "chatgpt", "email": "user@example.com" }
        }
        """);

        fakeClient.Handlers["account/rateLimits/read"] = _ => ParseJson("""
        {
            "rateLimitsByLimitId": {
                "codex": {
                    "limitId": "codex",
                    "primary": {
                        "windowDurationMins": 45,
                        "usedPercent": 33.0
                    }
                }
            }
        }
        """);

        var provider = new CodexUsageProvider(
            fs: fs,
            tempRoot: temp.Root,
            codexExecutablePath: "dummy.exe",
            clientFactory: (exe, home) => fakeClient);

        var result = await provider.FetchRateLimitsAsync(profileId, authBytes);

        Assert.True(result.Success);
        Assert.NotNull(result.Snapshot);
        var bucket = Assert.Single(result.Snapshot!.Limits);
        var window = Assert.Single(bucket.Windows);
        Assert.Equal(45, window.DurationMinutes);
        Assert.Equal("45m", window.DisplayLabel);
        Assert.Equal(33.0, window.UsedPercent);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_WhenAccountTypeIsNotChatGpt_ReturnsUnsupportedAccountType()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var profileId = Guid.NewGuid();
        var authBytes = Encoding.UTF8.GetBytes("{\"tokens\":{\"access_token\":\"dummy\"}}");

        var fakeClient = new FakeAppServerClient();
        fakeClient.Handlers["account/read"] = _ => ParseJson("""
        {
            "account": { "type": "azure", "email": "corp@azure.com" }
        }
        """);

        var provider = new CodexUsageProvider(
            fs: fs,
            tempRoot: temp.Root,
            codexExecutablePath: "dummy.exe",
            clientFactory: (exe, home) => fakeClient);

        var result = await provider.FetchRateLimitsAsync(profileId, authBytes);

        Assert.False(result.Success);
        Assert.Equal(UsageStatus.UnsupportedAccountType, result.Status);
        Assert.NotNull(result.Error);
        Assert.Contains("azure", result.Error.Message);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_WhenAccountIsNull_ReturnsAuthRequired()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var profileId = Guid.NewGuid();
        var authBytes = Encoding.UTF8.GetBytes("{\"tokens\":{\"access_token\":\"dummy\"}}");

        var fakeClient = new FakeAppServerClient();
        fakeClient.Handlers["account/read"] = _ => ParseJson("""
        {
            "account": null
        }
        """);

        var provider = new CodexUsageProvider(
            fs: fs,
            tempRoot: temp.Root,
            codexExecutablePath: "dummy.exe",
            clientFactory: (exe, home) => fakeClient);

        var result = await provider.FetchRateLimitsAsync(profileId, authBytes);

        Assert.False(result.Success);
        Assert.Equal(UsageStatus.AuthRequired, result.Status);
        Assert.NotNull(result.Error);
        Assert.Equal(ErrorCategory.RefreshTokenExpired, result.Error.Category);
    }
}
