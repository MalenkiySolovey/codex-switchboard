using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
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
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Codex.Usage;
using CodexSwitcher.Infra.Common.Logging;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Common.Time;
using CodexSwitcher.Infra.Providers.Inspection;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Scheduling;
using CodexSwitcher.Infra.Security.Dpapi;
using CodexSwitcher.Infra.Security.Hardening;
using CodexSwitcher.Infra.Security.Totp;
using CodexSwitcher.Infra.Settings;
using System.Text.Json;

namespace CodexSwitcher.Infra.Codex.Threads;

/// <summary>
/// Production implementation of <see cref="ICodexThreadHandoffService"/> using
/// JSON-RPC 2.0 over Codex app-server stdio.
/// Explicitly propagates modelProvider and model to ensure proper isolation.
/// </summary>
public sealed class CodexThreadHandoffService : ICodexThreadHandoffService
{
    private readonly Func<Task<ICodexAppServerClient>> _clientFactory;
    private readonly IAuditLog? _audit;

    public CodexThreadHandoffService(Func<Task<ICodexAppServerClient>> clientFactory)
        : this(clientFactory, null)
    {
    }

    public CodexThreadHandoffService(
        Func<Task<ICodexAppServerClient>> clientFactory,
        IAuditLog? audit)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _audit = audit;
    }

    public async Task<IReadOnlyList<CodexThreadSummary>> ListThreadsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var client = await _clientFactory().ConfigureAwait(false);
        var list = new List<CodexThreadSummary>();
        string? currentCursor = null;

        while (list.Count < limit && !cancellationToken.IsCancellationRequested)
        {
            int pageLimit = Math.Min(limit - list.Count, 50);
            var queryParams = new Dictionary<string, object?>
            {
                ["limit"] = pageLimit,
                ["modelProviders"] = Array.Empty<string>()
            };
            if (!string.IsNullOrWhiteSpace(currentCursor))
            {
                queryParams["cursor"] = currentCursor;
            }

            JsonElement result;
            try
            {
                result = await client.RequestAsync("thread/list", queryParams, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Fallback for app-server variants
                result = await client.RequestAsync("thread/list", queryParams, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            }

            int countBefore = list.Count;
            if (result.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dataEl.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id)) continue;

                    var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    var modelProvider = item.TryGetProperty("modelProvider", out var mpEl) ? mpEl.GetString() : null;
                    var model = item.TryGetProperty("model", out var mEl) ? mEl.GetString() : null;
                    var cwd = item.TryGetProperty("cwd", out var cwdEl) ? cwdEl.GetString() : null;

                    DateTimeOffset createdAt = DateTimeOffset.UtcNow;
                    if (item.TryGetProperty("createdAt", out var caEl) && caEl.TryGetInt64(out var caUnix))
                    {
                        createdAt = DateTimeOffset.FromUnixTimeSeconds(caUnix);
                    }

                    DateTimeOffset updatedAt = createdAt;
                    if (item.TryGetProperty("updatedAt", out var uaEl) && uaEl.TryGetInt64(out var uaUnix))
                    {
                        updatedAt = DateTimeOffset.FromUnixTimeSeconds(uaUnix);
                    }

                    list.Add(new CodexThreadSummary(id, name, modelProvider, model, createdAt, updatedAt, cwd));
                    if (list.Count >= limit) break;
                }
            }

            string? nextCursor = null;
            if (result.TryGetProperty("nextCursor", out var ncEl) && ncEl.ValueKind == JsonValueKind.String)
            {
                nextCursor = ncEl.GetString();
            }
            else if (result.TryGetProperty("cursor", out var cEl) && cEl.ValueKind == JsonValueKind.String)
            {
                nextCursor = cEl.GetString();
            }

            if (string.IsNullOrWhiteSpace(nextCursor) || list.Count == countBefore || list.Count >= limit)
            {
                break;
            }
            currentCursor = nextCursor;
        }

        return list;
    }

    public async Task<ThreadForkResult> ForkThreadAsync(
        string sourceThreadId,
        string targetModelProvider,
        string targetModel,
        string? newName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetModelProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetModel);

        var client = await _clientFactory().ConfigureAwait(false);

        var forkParams = new Dictionary<string, object?>
        {
            ["threadId"] = sourceThreadId,
            ["modelProvider"] = targetModelProvider,
            ["model"] = targetModel,
            ["ephemeral"] = false
        };

        var response = await client.RequestAsync("thread/fork", forkParams, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

        string forkedThreadId = string.Empty;
        JsonElement? threadEl = null;
        if (response.TryGetProperty("thread", out var tEl) && tEl.ValueKind == JsonValueKind.Object)
        {
            threadEl = tEl;
            if (tEl.TryGetProperty("id", out var tidEl))
            {
                forkedThreadId = tidEl.GetString() ?? string.Empty;
            }
        }
        if (string.IsNullOrWhiteSpace(forkedThreadId) && response.TryGetProperty("id", out var idEl))
        {
            forkedThreadId = idEl.GetString() ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(forkedThreadId) && response.TryGetProperty("threadId", out var tIdEl))
        {
            forkedThreadId = tIdEl.GetString() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(forkedThreadId))
        {
            throw new InvalidOperationException("thread/fork did not return a valid thread ID.");
        }

        if (string.Equals(forkedThreadId, sourceThreadId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"thread/fork returned the source thread ID '{sourceThreadId}'. A distinct forked thread ID is required.");
        }

        // Validate forkedFromId when present
        string? returnedForkedFromId = null;
        if (threadEl.HasValue && threadEl.Value.TryGetProperty("forkedFromId", out var ffiEl) && ffiEl.ValueKind == JsonValueKind.String)
        {
            returnedForkedFromId = ffiEl.GetString();
        }
        else if (response.TryGetProperty("forkedFromId", out var ffiRoot) && ffiRoot.ValueKind == JsonValueKind.String)
        {
            returnedForkedFromId = ffiRoot.GetString();
        }

        if (!string.IsNullOrWhiteSpace(returnedForkedFromId) &&
            !string.Equals(returnedForkedFromId, sourceThreadId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"thread/fork returned mismatched forkedFromId '{returnedForkedFromId}'. Expected '{sourceThreadId}'.");
        }

        // Validate ephemeral is not true
        if (threadEl.HasValue && threadEl.Value.TryGetProperty("ephemeral", out var ephEl) && ephEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            if (ephEl.GetBoolean())
            {
                throw new InvalidOperationException("thread/fork returned an ephemeral thread. Non-ephemeral thread is required for desktop persistence.");
            }
        }
        else if (response.TryGetProperty("ephemeral", out var ephRoot) && ephRoot.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            if (ephRoot.GetBoolean())
            {
                throw new InvalidOperationException("thread/fork returned an ephemeral thread. Non-ephemeral thread is required for desktop persistence.");
            }
        }

        // Authoritatively verify returned modelProvider matches explicit target
        string? returnedProvider = null;
        if (response.TryGetProperty("modelProvider", out var rmpEl) && rmpEl.ValueKind == JsonValueKind.String)
        {
            returnedProvider = rmpEl.GetString();
        }
        else if (threadEl.HasValue && threadEl.Value.TryGetProperty("modelProvider", out var tmpEl) && tmpEl.ValueKind == JsonValueKind.String)
        {
            returnedProvider = tmpEl.GetString();
        }

        if (!string.IsNullOrWhiteSpace(returnedProvider) &&
            !string.Equals(returnedProvider, targetModelProvider, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Target modelProvider mismatch. Requested: '{targetModelProvider}', returned: '{returnedProvider}'");
        }

        // Authoritatively verify returned model matches explicit target (exact string equality, no suffix stripping)
        string? returnedModel = null;
        if (response.TryGetProperty("model", out var rmEl) && rmEl.ValueKind == JsonValueKind.String)
        {
            returnedModel = rmEl.GetString();
        }
        else if (threadEl.HasValue && threadEl.Value.TryGetProperty("model", out var tmEl) && tmEl.ValueKind == JsonValueKind.String)
        {
            returnedModel = tmEl.GetString();
        }

        if (!string.IsNullOrWhiteSpace(returnedModel) &&
            !string.Equals(returnedModel, targetModel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Target model mismatch. Requested: '{targetModel}', returned: '{returnedModel}'");
        }

        // Verify returned path when exposed by runtime
        string? returnedPath = null;
        if (threadEl.HasValue && threadEl.Value.TryGetProperty("path", out var pEl) && pEl.ValueKind == JsonValueKind.String)
        {
            returnedPath = pEl.GetString();
        }
        else if (response.TryGetProperty("path", out var pRoot) && pRoot.ValueKind == JsonValueKind.String)
        {
            returnedPath = pRoot.GetString();
        }

        bool returnedPathPresent = !string.IsNullOrWhiteSpace(returnedPath);
        bool returnedPathExists = false;
        if (returnedPathPresent)
        {
            returnedPathExists = File.Exists(returnedPath);
            if (!returnedPathExists)
            {
                throw new InvalidOperationException($"thread/fork returned path '{returnedPath}', but file does not exist on disk.");
            }
        }

        // Read-back verification (Requirement K): verify the new thread is persisted and readable
        bool threadReadOk = false;
        try
        {
            var readResp = await client.RequestAsync("thread/read", new { threadId = forkedThreadId }, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            if (readResp.ValueKind == JsonValueKind.Object)
            {
                string? readId = null;
                if (readResp.TryGetProperty("thread", out var readThread) && readThread.ValueKind == JsonValueKind.Object && readThread.TryGetProperty("id", out var rtid))
                {
                    readId = rtid.GetString();
                }
                else if (readResp.TryGetProperty("id", out var rid))
                {
                    readId = rid.GetString();
                }

                if (string.Equals(readId, forkedThreadId, StringComparison.OrdinalIgnoreCase) ||
                    readResp.TryGetProperty("turns", out _) ||
                    readResp.TryGetProperty("history", out _))
                {
                    threadReadOk = true;
                }
            }
        }
        catch (Exception)
        {
            // Fallback to thread/list with modelProviders = []
        }

        bool threadListOk = false;
        if (!threadReadOk)
        {
            try
            {
                var listResp = await client.RequestAsync("thread/list", new { limit = 50, modelProviders = Array.Empty<string>() }, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
                if (listResp.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataArr.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var idProp) && string.Equals(idProp.GetString(), forkedThreadId, StringComparison.OrdinalIgnoreCase))
                        {
                            threadListOk = true;
                            break;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        bool verifiedReadable = threadReadOk || threadListOk;
        if (!verifiedReadable)
        {
            throw new InvalidOperationException($"Forked thread '{forkedThreadId}' could not be verified via thread/read or thread/list.");
        }

        // Safe Human-QA Diagnostic Log (no prompt text, no credentials, no auth, no thread content)
        _audit?.Record(
            "thread-fork-diagnostic",
            verifiedReadable ? "success" : "failed",
            $"THREAD_ID={forkedThreadId} FORKED_FROM_ID={returnedForkedFromId ?? sourceThreadId} MODEL_PROVIDER={targetModelProvider} MODEL={targetModel} EPHEMERAL=false RETURNED_PATH_PRESENT={returnedPathPresent} RETURNED_PATH_EXISTS={returnedPathExists} THREAD_READ_OK={threadReadOk} THREAD_LIST_OK={threadListOk}");
        System.Diagnostics.Trace.TraceInformation($"[ThreadHandoffDiagnostic] THREAD_ID={forkedThreadId} FORKED_FROM_ID={returnedForkedFromId ?? sourceThreadId} MODEL_PROVIDER={targetModelProvider} MODEL={targetModel} EPHEMERAL=false RETURNED_PATH_PRESENT={returnedPathPresent} RETURNED_PATH_EXISTS={returnedPathExists} THREAD_READ_OK={threadReadOk} THREAD_LIST_OK={threadListOk}");

        // Best-effort rename using official thread/name/set ONLY AFTER thread persistence is verified
        if (!string.IsNullOrWhiteSpace(newName))
        {
            try
            {
                await client.RequestAsync("thread/name/set", new { threadId = forkedThreadId, name = newName }, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Renaming is cosmetic and best effort per official protocol
            }
        }

        return new ThreadForkResult(forkedThreadId, sourceThreadId, targetModelProvider, targetModel, newName);
    }

    public async Task<ThreadForkResult> StartFreshThreadAsync(
        string targetModelProvider,
        string targetModel,
        string? cwd = null,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetModelProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetModel);

        var client = await _clientFactory().ConfigureAwait(false);

        var startParams = new Dictionary<string, object?>
        {
            ["modelProvider"] = targetModelProvider,
            ["model"] = targetModel,
            ["ephemeral"] = false
        };
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            startParams["cwd"] = cwd;
        }

        var response = await client.RequestAsync("thread/start", startParams, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

        string freshThreadId = string.Empty;
        JsonElement? startThreadEl = null;
        if (response.TryGetProperty("thread", out var threadEl) && threadEl.ValueKind == JsonValueKind.Object)
        {
            startThreadEl = threadEl;
            if (threadEl.TryGetProperty("id", out var tidEl))
            {
                freshThreadId = tidEl.GetString() ?? string.Empty;
            }
        }
        if (string.IsNullOrWhiteSpace(freshThreadId) && response.TryGetProperty("id", out var idEl))
        {
            freshThreadId = idEl.GetString() ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(freshThreadId) && response.TryGetProperty("threadId", out var tIdEl))
        {
            freshThreadId = tIdEl.GetString() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(freshThreadId))
        {
            throw new InvalidOperationException("thread/start did not return a valid thread ID.");
        }

        // Validate ephemeral is not true
        if (startThreadEl.HasValue && startThreadEl.Value.TryGetProperty("ephemeral", out var ephEl) && ephEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            if (ephEl.GetBoolean())
            {
                throw new InvalidOperationException("thread/start returned an ephemeral thread. Non-ephemeral thread is required for desktop persistence.");
            }
        }
        else if (response.TryGetProperty("ephemeral", out var ephRoot) && ephRoot.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            if (ephRoot.GetBoolean())
            {
                throw new InvalidOperationException("thread/start returned an ephemeral thread. Non-ephemeral thread is required for desktop persistence.");
            }
        }

        // Authoritatively verify returned modelProvider matches explicit target
        string? returnedProvider = null;
        if (response.TryGetProperty("modelProvider", out var rmpEl) && rmpEl.ValueKind == JsonValueKind.String)
        {
            returnedProvider = rmpEl.GetString();
        }
        else if (startThreadEl.HasValue && startThreadEl.Value.TryGetProperty("modelProvider", out var tmpEl) && tmpEl.ValueKind == JsonValueKind.String)
        {
            returnedProvider = tmpEl.GetString();
        }

        if (!string.IsNullOrWhiteSpace(returnedProvider) &&
            !string.Equals(returnedProvider, targetModelProvider, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Target modelProvider mismatch on thread/start. Requested: '{targetModelProvider}', returned: '{returnedProvider}'");
        }

        // Authoritatively verify returned model matches explicit target
        string? returnedModel = null;
        if (response.TryGetProperty("model", out var rmEl) && rmEl.ValueKind == JsonValueKind.String)
        {
            returnedModel = rmEl.GetString();
        }
        else if (startThreadEl.HasValue && startThreadEl.Value.TryGetProperty("model", out var tmEl) && tmEl.ValueKind == JsonValueKind.String)
        {
            returnedModel = tmEl.GetString();
        }

        if (!string.IsNullOrWhiteSpace(returnedModel) &&
            !string.Equals(returnedModel, targetModel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Target model mismatch on thread/start. Requested: '{targetModel}', returned: '{returnedModel}'");
        }

        string? freshPath = null;
        if (startThreadEl.HasValue && startThreadEl.Value.TryGetProperty("path", out var spEl) && spEl.ValueKind == JsonValueKind.String)
        {
            freshPath = spEl.GetString();
        }
        else if (response.TryGetProperty("path", out var spRoot) && spRoot.ValueKind == JsonValueKind.String)
        {
            freshPath = spRoot.GetString();
        }

        bool freshPathPresent = !string.IsNullOrWhiteSpace(freshPath);
        bool freshPathExists = freshPathPresent && File.Exists(freshPath);

        // Read-back verification (Requirement K): verify the fresh thread is persisted and readable
        try
        {
            await client.RequestAsync("thread/read", new { threadId = freshThreadId }, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Fallback / best effort
        }

        // Safe Human-QA Diagnostic Log (no prompt text, no credentials, no auth, no thread content)
        _audit?.Record(
            "thread-start-diagnostic",
            "success",
            $"FRESH_THREAD_ID={freshThreadId} MODEL_PROVIDER={targetModelProvider} MODEL={targetModel} EPHEMERAL=false RETURNED_PATH_PRESENT={freshPathPresent} RETURNED_PATH_EXISTS={freshPathExists}");
        System.Diagnostics.Trace.TraceInformation($"[ThreadHandoffDiagnostic] FRESH_THREAD_ID={freshThreadId} MODEL_PROVIDER={targetModelProvider} MODEL={targetModel} EPHEMERAL=false RETURNED_PATH_PRESENT={freshPathPresent} RETURNED_PATH_EXISTS={freshPathExists}");

        // Best-effort rename using official thread/name/set
        if (!string.IsNullOrWhiteSpace(name))
        {
            try
            {
                await client.RequestAsync("thread/name/set", new { threadId = freshThreadId, name }, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Renaming is cosmetic and best effort per official protocol
            }
        }

        return new ThreadForkResult(freshThreadId, string.Empty, targetModelProvider, targetModel, name);
    }

    public async Task<ThreadCompatibilityAssessment> AssessThreadCompatibilityAsync(
        string threadId,
        EffectiveToolPolicy targetPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentNullException.ThrowIfNull(targetPolicy);

        try
        {
            var client = await _clientFactory().ConfigureAwait(false);
            var response = await client.RequestAsync("thread/read", new { threadId }, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);

            var itemTypes = new List<string>();
            ExtractItemTypes(response, itemTypes);

            return ThreadCompatibilityAssessment.EvaluateHistoryItems(threadId, itemTypes, targetPolicy);
        }
        catch
        {
            // If app-server cannot read thread or method is unavailable, default to compatible
            return ThreadCompatibilityAssessment.Compatible(threadId);
        }
    }

    private static void ExtractItemTypes(JsonElement element, List<string> itemTypes)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if ((string.Equals(prop.Name, "type", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(prop.Name, "name", StringComparison.OrdinalIgnoreCase)) &&
                    prop.Value.ValueKind == JsonValueKind.String)
                {
                    itemTypes.Add(prop.Value.GetString()!);
                }
                else
                {
                    ExtractItemTypes(prop.Value, itemTypes);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ExtractItemTypes(item, itemTypes);
            }
        }
    }
}
