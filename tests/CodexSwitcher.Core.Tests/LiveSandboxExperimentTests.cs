using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Security;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Codex;
using CodexSwitcher.Infra.Io;
using Xunit;
using Xunit.Abstractions;

namespace CodexSwitcher.Core.Tests;

[Trait("Category", "LiveIntegration")]
public sealed class LiveSandboxExperimentTests
{
    private readonly ITestOutputHelper _output;

    public LiveSandboxExperimentTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ExecuteLiveIsolatedSandboxExperiment()
    {
        // Opt-in gate: live integration tests only run when explicitly enabled.
        if (Environment.GetEnvironmentVariable("CODEXSWITCHBOARD_RUN_LIVE_TESTS") != "1")
        {
            _output.WriteLine("SKIPPED: Live integration tests are opt-in. Set CODEXSWITCHBOARD_RUN_LIVE_TESTS=1 to run.");
            return;
        }

        var activeCodexDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex");
        var activeAuthPath = Path.Combine(activeCodexDir, "auth.json");

        if (!File.Exists(activeAuthPath))
        {
            _output.WriteLine("SKIPPED: Active auth.json not found at expected location.");
            return;
        }

        // 1. Record pre-experiment active file state
        var preActiveBytes = File.ReadAllBytes(activeAuthPath);
        var preActiveLastWrite = File.GetLastWriteTimeUtc(activeAuthPath);
        var preActiveHash = SHA256.HashData(preActiveBytes);

        // 2. Prepare isolated sandbox
        using var tempRoot = new TempDir();
        var fs = new PhysicalFileSystem();
        var testProfileId = Guid.NewGuid();

        var codexPath = CodexCliRunner.ResolveCodexPath();
        Assert.NotNull(codexPath);
        Assert.True(File.Exists(codexPath));

        var sandbox = ProfileSandbox.Create(tempRoot.Root, testProfileId, preActiveBytes, fs);
        var client = new CodexAppServerClient(codexPath, sandbox.DirectoryPath);

        // Record sandbox auth.json initial state
        var preSandboxBytes = File.ReadAllBytes(sandbox.AuthJsonPath);
        var preSandboxLastWrite = File.GetLastWriteTimeUtc(sandbox.AuthJsonPath);
        var preSandboxHash = SHA256.HashData(preSandboxBytes);

        UsageFetchResult? fetchResult = null;
        byte[]? postSandboxBytes = null;
        DateTimeOffset? postSandboxLastWrite = null;
        byte[]? postSandboxHash = null;

        try
        {
            await client.StartAsync();

            // Step 1: account/read {"refreshToken": false}
            var accountElement = await client.RequestAsync(
                "account/read",
                new { refreshToken = false },
                TimeSpan.FromSeconds(25));

            var (accountType, email, planType, requiresOpenaiAuth) =
                CodexUsageResponseParser.ParseAccountInfo(accountElement);

            if (string.IsNullOrWhiteSpace(accountType))
            {
                var err = ErrorInfo.Create(ErrorCategory.RefreshTokenExpired, "Account is unauthenticated.", DateTimeOffset.UtcNow);
                fetchResult = UsageFetchResult.Fail(UsageStatus.AuthRequired, err);
            }
            else
            {
                // Step 2: account/rateLimits/read
                var rateLimitsElement = await client.RequestAsync(
                    "account/rateLimits/read",
                    new { },
                    TimeSpan.FromSeconds(25));

                var (primaryLimitId, buckets, resetCredits) =
                    CodexUsageResponseParser.ParseRateLimits(rateLimitsElement);

                var isRateLimited = buckets.Any(b =>
                    !string.IsNullOrWhiteSpace(b.RateLimitReachedType) ||
                    b.Windows.Any(w => w.UsedPercent >= 100.0));

                var status = isRateLimited ? UsageStatus.RateLimited : UsageStatus.Healthy;
                var effectivePlan = planType ?? buckets.FirstOrDefault(b => b.LimitId == (primaryLimitId ?? "codex"))?.PlanType;

                var snapshot = new RateLimitsSnapshot(
                    ProfileId: testProfileId,
                    ObservedAt: DateTimeOffset.UtcNow,
                    PrimaryLimitId: primaryLimitId,
                    Limits: buckets,
                    ResetCreditsAvailable: resetCredits,
                    PlanType: effectivePlan,
                    AccountEmail: email,
                    Status: status);

                AccountActivitySnapshot? activity = null;
                try
                {
                    var usageElement = await client.RequestAsync(
                        "account/usage/read",
                        new { },
                        TimeSpan.FromSeconds(25));
                    activity = CodexUsageResponseParser.ParseAccountActivity(testProfileId, usageElement, DateTimeOffset.UtcNow);
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"account/usage/read non-critical failure: {ex.Message}");
                }

                var (mutated, rotBytes, _) = sandbox.InspectMutation();
                fetchResult = UsageFetchResult.Ok(snapshot, sandboxAuthMutated: mutated, rotatedAuthJson: rotBytes, activity: activity);
            }

            // Capture post-query sandbox file state before stopping/disposing
            if (File.Exists(sandbox.AuthJsonPath))
            {
                postSandboxBytes = File.ReadAllBytes(sandbox.AuthJsonPath);
                postSandboxLastWrite = File.GetLastWriteTimeUtc(sandbox.AuthJsonPath);
                postSandboxHash = SHA256.HashData(postSandboxBytes);
            }
        }
        finally
        {
            await client.StopAsync();
            client.Dispose();
            sandbox.Dispose();
        }

        // 3. Capture post-experiment active file state
        var postActiveBytes = File.ReadAllBytes(activeAuthPath);
        var postActiveLastWrite = File.GetLastWriteTimeUtc(activeAuthPath);
        var postActiveHash = SHA256.HashData(postActiveBytes);

        var activeFileUntouched = preActiveHash.SequenceEqual(postActiveHash) &&
                                  preActiveLastWrite == postActiveLastWrite &&
                                  preActiveBytes.Length == postActiveBytes.Length;

        _output.WriteLine("=== ACTIVE FILE ZERO-TOUCH VERIFICATION ===");
        _output.WriteLine($"Active auth changed: {(activeFileUntouched ? "NO" : "YES")}");
        _output.WriteLine($"Active auth fingerprint unchanged: {(activeFileUntouched ? "YES" : "NO")}");
        Assert.True(activeFileUntouched, "CRITICAL ERROR: Real active auth.json was modified!");

        // 4. Capture sandbox mutation analysis
        Assert.NotNull(postSandboxBytes);
        Assert.NotNull(postSandboxHash);

        var sandboxHashMatches = preSandboxHash.SequenceEqual(postSandboxHash);
        var sandboxLastWriteMatches = postSandboxLastWrite.HasValue && preSandboxLastWrite == postSandboxLastWrite.Value;
        var sandboxMutated = fetchResult?.SandboxAuthMutated ?? false;

        _output.WriteLine("=== SANDBOX MUTATION EXPERIMENT ===");
        _output.WriteLine($"sandbox beforeHash == afterHash: {sandboxHashMatches.ToString().ToLowerInvariant()}");
        _output.WriteLine($"sandbox LastWriteUtc before == after: {sandboxLastWriteMatches.ToString().ToLowerInvariant()}");
        _output.WriteLine($"SandboxAuthMutated boolean: {sandboxMutated.ToString().ToLowerInvariant()}");

        if (sandboxHashMatches)
        {
            _output.WriteLine("Empirical Sandbox Result: A. sandbox auth unchanged");
        }
        else
        {
            _output.WriteLine("Empirical Sandbox Result: B. sandbox auth changed");
            AnalyzeJsonDifferences(preSandboxBytes, postSandboxBytes);
        }

        // 5. Rate limit output
        Assert.NotNull(fetchResult);
        Assert.True(fetchResult.Success);
        Assert.NotNull(fetchResult.Snapshot);

        var snap = fetchResult.Snapshot;
        _output.WriteLine("=== NORMALIZED RATE LIMIT SNAPSHOT ===");
        _output.WriteLine($"Status: {snap.Status}");
        _output.WriteLine($"PlanType: {snap.PlanType ?? "null"}");
        _output.WriteLine($"PrimaryLimitId: {snap.PrimaryLimitId ?? "null"}");
        _output.WriteLine($"ResetCreditsAvailable: {snap.ResetCreditsAvailable?.ToString() ?? "null"}");
        _output.WriteLine($"Buckets count: {snap.Limits.Count}");

        foreach (var b in snap.Limits)
        {
            _output.WriteLine($"Bucket '{b.LimitId}' (ReachedType: {b.RateLimitReachedType ?? "none"}):");
            foreach (var w in b.Windows)
            {
                _output.WriteLine($"  Window [{w.Slot}] DurationMinutes={w.DurationMinutes} ({w.DisplayLabel}) Used={w.UsedPercent:F1}% Remaining={w.RemainingPercent:F1}% ResetsAt={w.ResetsAt:O}");
            }
        }

        if (fetchResult.Activity is { } act)
        {
            _output.WriteLine("=== NORMALIZED ACCOUNT ACTIVITY SNAPSHOT ===");
            _output.WriteLine($"LifetimeTokens: {act.Summary?.LifetimeTokens?.ToString() ?? "null"}");
            _output.WriteLine($"PeakDailyTokens: {act.Summary?.PeakDailyTokens?.ToString() ?? "null"}");
            _output.WriteLine($"LongestTurnSec: {act.Summary?.LongestRunningTurnSeconds?.ToString() ?? "null"}");
            _output.WriteLine($"CurrentStreakDays: {act.Summary?.CurrentStreakDays?.ToString() ?? "null"}");
            _output.WriteLine($"LongestStreakDays: {act.Summary?.LongestStreakDays?.ToString() ?? "null"}");
            _output.WriteLine($"DailyBuckets Count: {act.DailyBuckets.Count}");
            foreach (var bucket in act.DailyBuckets.Take(5))
            {
                _output.WriteLine($"  Bucket Date={bucket.StartDateRaw} Tokens={bucket.Tokens}");
            }
        }
    }

    private void AnalyzeJsonDifferences(byte[] beforeBytes, byte[] afterBytes)
    {
        try
        {
            using var beforeDoc = JsonDocument.Parse(beforeBytes);
            using var afterDoc = JsonDocument.Parse(afterBytes);

            var beforeRoot = beforeDoc.RootElement;
            var afterRoot = afterDoc.RootElement;

            var changedTopLevel = new List<string>();
            foreach (var prop in afterRoot.EnumerateObject())
            {
                if (!beforeRoot.TryGetProperty(prop.Name, out var beforeProp) ||
                    beforeProp.GetRawText() != prop.Value.GetRawText())
                {
                    changedTopLevel.Add(prop.Name);
                }
            }
            _output.WriteLine($"Changed top-level fields: [{string.Join(", ", changedTopLevel)}]");

            var lastRefreshChanged = false;
            if (beforeRoot.TryGetProperty("last_refresh", out var bLr) &&
                afterRoot.TryGetProperty("last_refresh", out var aLr))
            {
                lastRefreshChanged = bLr.GetString() != aLr.GetString();
            }
            _output.WriteLine($"last_refresh changed: {lastRefreshChanged.ToString().ToLowerInvariant()}");

            if (beforeRoot.TryGetProperty("tokens", out var bTok) &&
                afterRoot.TryGetProperty("tokens", out var aTok))
            {
                var changedTokenFields = new List<string>();
                var accessTokenChanged = false;
                var refreshTokenChanged = false;
                var idTokenChanged = false;

                foreach (var prop in aTok.EnumerateObject())
                {
                    if (!bTok.TryGetProperty(prop.Name, out var bProp) ||
                        bProp.GetRawText() != prop.Value.GetRawText())
                    {
                        changedTokenFields.Add(prop.Name);
                        if (prop.Name == "access_token") accessTokenChanged = true;
                        if (prop.Name == "refresh_token") refreshTokenChanged = true;
                        if (prop.Name == "id_token") idTokenChanged = true;
                    }
                }

                _output.WriteLine($"Changed fields below tokens: [{string.Join(", ", changedTokenFields)}]");
                _output.WriteLine($"access-token field changed: {accessTokenChanged.ToString().ToLowerInvariant()}");
                _output.WriteLine($"refresh-token field changed: {refreshTokenChanged.ToString().ToLowerInvariant()}");
                _output.WriteLine($"id-token field changed: {idTokenChanged.ToString().ToLowerInvariant()}");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Error analyzing JSON differences: {ex.Message}");
        }
    }

    [Fact]
    public async Task ProbeResetCreditDetailsOnCurrentRuntime()
    {
        if (Environment.GetEnvironmentVariable("CODEXSWITCHBOARD_RUN_LIVE_TESTS") != "1")
        {
            _output.WriteLine("SKIPPED: Live integration tests are opt-in. Set CODEXSWITCHBOARD_RUN_LIVE_TESTS=1 to run.");
            return;
        }

        var activeCodexDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex");
        var activeAuthPath = Path.Combine(activeCodexDir, "auth.json");

        if (!File.Exists(activeAuthPath))
        {
            _output.WriteLine("SKIPPED: Active auth.json not found at expected location.");
            return;
        }

        var activeBytes = File.ReadAllBytes(activeAuthPath);
        using var tempRoot = new TempDir();
        var fs = new PhysicalFileSystem();
        var testProfileId = Guid.NewGuid();

        var codexPath = CodexCliRunner.ResolveCodexPath();
        Assert.NotNull(codexPath);
        Assert.True(File.Exists(codexPath));

        var sandbox = ProfileSandbox.Create(tempRoot.Root, testProfileId, activeBytes, fs);
        var client = new CodexAppServerClient(codexPath, sandbox.DirectoryPath);

        try
        {
            await client.StartAsync();

            var rateLimitsElement = await client.RequestAsync(
                "account/rateLimits/read",
                new { },
                TimeSpan.FromSeconds(25));

            _output.WriteLine("=== CONTROLLED LIVE PROBE: rateLimitResetCredits ===");

            if (rateLimitsElement.TryGetProperty("rateLimitResetCredits", out var resetCreditsEl))
            {
                if (resetCreditsEl.ValueKind == JsonValueKind.Object)
                {
                    int? availableCount = null;
                    if (resetCreditsEl.TryGetProperty("availableCount", out var countEl) && countEl.ValueKind == JsonValueKind.Number)
                    {
                        availableCount = countEl.GetInt32();
                    }
                    _output.WriteLine($"availableCount: {(availableCount.HasValue ? availableCount.Value.ToString() : "absent/null")}");

                    if (resetCreditsEl.TryGetProperty("credits", out var creditsEl))
                    {
                        if (creditsEl.ValueKind == JsonValueKind.Null)
                        {
                            _output.WriteLine("credits field: null");
                        }
                        else if (creditsEl.ValueKind == JsonValueKind.Array)
                        {
                            var count = creditsEl.GetArrayLength();
                            _output.WriteLine("credits field: array");
                            _output.WriteLine($"credits array length: {count}");

                            int rowIndex = 0;
                            foreach (var row in creditsEl.EnumerateArray())
                            {
                                var status = row.TryGetProperty("status", out var s) ? s.GetString() : "absent";
                                var resetType = row.TryGetProperty("resetType", out var rt) ? rt.GetString() : "absent";
                                var gaKind = row.TryGetProperty("grantedAt", out var ga) ? ga.ValueKind.ToString() : "absent";
                                var eaKind = row.TryGetProperty("expiresAt", out var ea) ? ea.ValueKind.ToString() : "absent";
                                var hasTitle = row.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString());
                                var hasDescription = row.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(d.GetString());

                                _output.WriteLine($"  [Row {rowIndex}] status={status}, resetType={resetType}, grantedAtKind={gaKind}, expiresAtKind={eaKind}, title={(hasTitle ? "present" : "absent")}, description={(hasDescription ? "present" : "absent")}");
                                rowIndex++;
                            }
                        }
                        else
                        {
                            _output.WriteLine($"credits field: {creditsEl.ValueKind}");
                        }
                    }
                    else
                    {
                        _output.WriteLine("credits field: absent");
                    }
                }
                else
                {
                    _output.WriteLine($"rateLimitResetCredits field kind: {resetCreditsEl.ValueKind}");
                }
            }
            else
            {
                _output.WriteLine("rateLimitResetCredits field: absent");
            }
        }
        finally
        {
            await client.StopAsync();
            client.Dispose();
            sandbox.Dispose();
        }
    }
}
