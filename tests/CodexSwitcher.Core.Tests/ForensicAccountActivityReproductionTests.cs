using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Codex.Usage;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Security.Dpapi;
using Xunit;
using Xunit.Abstractions;

namespace CodexSwitcher.Core.Tests;

public sealed class ForensicAccountActivityReproductionTests
{
    private readonly ITestOutputHelper _output;

    public ForensicAccountActivityReproductionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(stream));
    }

    private static string DescribeJsonShape(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(", ", element.EnumerateObject().Select(p => $"{p.Name}: {DescribeJsonShape(p.Value)}")) + "}",
            JsonValueKind.Array => "[" + (element.GetArrayLength() > 0 ? DescribeJsonShape(element[0]) : "empty") + "]",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => element.ValueKind.ToString()
        };
    }

    [Fact]
    public async Task Forensics_Reproduction_AccountActivity_AgainstProductionRuntime()
    {
        var resolver = new CodexRuntimeResolver();
        var runtime = resolver.ResolveCurrentRuntime();

        _output.WriteLine("=== SECTION 5: FORENSIC INVESTIGATION ===");
        _output.WriteLine($"RUNTIME_PATH = {runtime.ExecutablePath}");
        _output.WriteLine($"RUNTIME_VERSION = {runtime.Version}");

        if (string.IsNullOrWhiteSpace(runtime.ExecutablePath) || !File.Exists(runtime.ExecutablePath))
        {
            _output.WriteLine("Production runtime executable not found on host. Aborting forensics.");
            return;
        }

        var runtimeSha256 = ComputeSha256(runtime.ExecutablePath);
        _output.WriteLine($"RUNTIME_SHA256 = {runtimeSha256}");

        var paths = new AppPaths();
        var fs = new PhysicalFileSystem();
        var coordinator = new ProfileOperationCoordinator();
        var vault = new VaultService(new DpapiSecretProtector(), fs, paths.VaultDir, coordinator);
        var store = new ProfileStore(fs, paths.ProfilesPath);

        var profiles = store.LoadAll();
        Assert.NotEmpty(profiles);

        // Pick the first profile with an existing vault blob
        var targetProfile = profiles.FirstOrDefault(p => vault.Exists(p.Id));
        Assert.NotNull(targetProfile);

        _output.WriteLine($"PROFILE_GUID = {targetProfile.Id:D}");
        _output.WriteLine($"PROFILE_NICKNAME = {targetProfile.Nickname}");

        var authBytes = vault.LoadBlob(targetProfile.Id);
        Assert.NotEmpty(authBytes);

        // Create the EXACT profile sandbox used in production quota monitoring
        using var sandbox = ProfileSandbox.Create(paths.TempRoot, targetProfile.Id, authBytes, fs);
        _output.WriteLine($"SANDBOX_DIR = {sandbox.DirectoryPath}");

        var client = new CodexAppServerClient(runtime.ExecutablePath, sandbox.DirectoryPath);
        var sw = Stopwatch.StartNew();

        try
        {
            // 1. initialize handshake
            sw.Restart();
            await client.StartAsync();
            sw.Stop();
            _output.WriteLine($"RPC_1_INITIALIZE_ELAPSED_MS = {sw.ElapsedMilliseconds}");
            _output.WriteLine("RPC_1_INITIALIZE = SUCCESS");

            // 2. account/read
            sw.Restart();
            string? accountType = null;
            string? planType = null;
            bool accountIdPresent = false;
            try
            {
                var accountElem = await client.RequestAsync("account/read", new { refreshToken = false }, TimeSpan.FromSeconds(20));
                sw.Stop();
                _output.WriteLine($"RPC_2_ACCOUNT_READ_ELAPSED_MS = {sw.ElapsedMilliseconds}");
                _output.WriteLine("RPC_2_ACCOUNT_READ = SUCCESS");
                _output.WriteLine($"RPC_2_JSON_SHAPE = {DescribeJsonShape(accountElem)}");

                if (accountElem.TryGetProperty("account", out var acct) && acct.ValueKind == JsonValueKind.Object)
                {
                    if (acct.TryGetProperty("type", out var typeEl)) accountType = typeEl.GetString();
                    if (acct.TryGetProperty("planType", out var planEl)) planType = planEl.GetString();
                    if (acct.TryGetProperty("accountId", out var idEl) && !string.IsNullOrWhiteSpace(idEl.GetString()))
                    {
                        accountIdPresent = true;
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                _output.WriteLine($"RPC_2_ACCOUNT_READ = ERROR ({ex.Message})");
            }

            _output.WriteLine($"ACCOUNT_TYPE = {accountType ?? "null"}");
            _output.WriteLine($"PLAN_TYPE = {planType ?? "null"}");
            _output.WriteLine($"ACCOUNT_ID_PRESENT = {accountIdPresent}");

            // 3. account/rateLimits/read
            sw.Restart();
            try
            {
                var rateLimitsElem = await client.RequestAsync("account/rateLimits/read", new { }, TimeSpan.FromSeconds(20));
                sw.Stop();
                _output.WriteLine($"RPC_3_RATELIMITS_ELAPSED_MS = {sw.ElapsedMilliseconds}");
                _output.WriteLine("RPC_3_RATELIMITS = SUCCESS");
                _output.WriteLine($"RPC_3_JSON_SHAPE = {DescribeJsonShape(rateLimitsElem)}");

                if (rateLimitsElem.TryGetProperty("accountId", out var rlaId) && !string.IsNullOrWhiteSpace(rlaId.GetString()))
                {
                    _output.WriteLine("RPC_3_ACCOUNT_ID_IN_RATELIMITS = PRESENT");
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                _output.WriteLine($"RPC_3_RATELIMITS = ERROR ({ex.Message})");
            }

            // 4. account/usage/read with new { }
            sw.Restart();
            try
            {
                var usageElem1 = await client.RequestAsync("account/usage/read", new { }, TimeSpan.FromSeconds(20));
                sw.Stop();
                _output.WriteLine($"RPC_4_USAGE_EMPTY_OBJ_ELAPSED_MS = {sw.ElapsedMilliseconds}");
                _output.WriteLine("RPC_4_USAGE_EMPTY_OBJ = SUCCESS");
                _output.WriteLine($"RPC_4_USAGE_EMPTY_OBJ_JSON_SHAPE = {DescribeJsonShape(usageElem1)}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                _output.WriteLine($"RPC_4_USAGE_EMPTY_OBJ_ELAPSED_MS = {sw.ElapsedMilliseconds}");
                _output.WriteLine($"RPC_4_USAGE_EMPTY_OBJ = ERROR ({ex.Message})");
            }

            // 4b. account/usage/read with null (omitted params)
            sw.Restart();
            try
            {
                var usageElem2 = await client.RequestAsync("account/usage/read", null, TimeSpan.FromSeconds(20));
                sw.Stop();
                _output.WriteLine($"RPC_4_USAGE_NULL_PARAMS_ELAPSED_MS = {sw.ElapsedMilliseconds}");
                _output.WriteLine("RPC_4_USAGE_NULL_PARAMS = SUCCESS");
                _output.WriteLine($"RPC_4_USAGE_NULL_PARAMS_JSON_SHAPE = {DescribeJsonShape(usageElem2)}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                _output.WriteLine($"RPC_4_USAGE_NULL_PARAMS_ELAPSED_MS = {sw.ElapsedMilliseconds}");
                _output.WriteLine($"RPC_4_USAGE_NULL_PARAMS = ERROR ({ex.Message})");
            }

            // 4c. account/usage/read with { threadId = null }
            sw.Restart();
            try
            {
                var usageElem3 = await client.RequestAsync("account/usage/read", new { threadId = (string?)null }, TimeSpan.FromSeconds(20));
                sw.Stop();
                _output.WriteLine($"RPC_4_USAGE_THREAD_ID_NULL_ELAPSED_MS = {sw.ElapsedMilliseconds}");
                _output.WriteLine("RPC_4_USAGE_THREAD_ID_NULL = SUCCESS");
                _output.WriteLine($"RPC_4_USAGE_THREAD_ID_NULL_JSON_SHAPE = {DescribeJsonShape(usageElem3)}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                _output.WriteLine($"RPC_4_USAGE_THREAD_ID_NULL_ELAPSED_MS = {sw.ElapsedMilliseconds}");
                _output.WriteLine($"RPC_4_USAGE_THREAD_ID_NULL = ERROR ({ex.Message})");
            }
        }
        finally
        {
            await client.StopAsync();
        }
    }
}
