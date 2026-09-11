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

    public CodexThreadHandoffService(Func<Task<ICodexAppServerClient>> clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public async Task<IReadOnlyList<CodexThreadSummary>> ListThreadsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var client = await _clientFactory().ConfigureAwait(false);
        JsonElement result;
        try
        {
            // Per official Codex app-server schema, modelProviders: [] explicitly requests sessions across ALL providers
            var allProvidersParams = new
            {
                limit,
                modelProviders = Array.Empty<string>()
            };
            result = await client.RequestAsync("thread/list", allProvidersParams, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Fallback for older Codex app-server versions that may not accept empty modelProviders array
            result = await client.RequestAsync("thread/list", new { limit }, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        }

        var list = new List<CodexThreadSummary>();
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
            }
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

        var forkParams = new
        {
            threadId = sourceThreadId,
            modelProvider = targetModelProvider,
            model = targetModel
        };

        var response = await client.RequestAsync("thread/fork", forkParams, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

        string forkedThreadId = string.Empty;
        if (response.TryGetProperty("thread", out var threadEl) && threadEl.TryGetProperty("id", out var tidEl))
        {
            forkedThreadId = tidEl.GetString() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(forkedThreadId))
        {
            throw new InvalidOperationException("thread/fork did not return a valid thread ID.");
        }

        // Verify returned modelProvider matches explicit target
        string? returnedProvider = null;
        if (response.TryGetProperty("modelProvider", out var rmpEl))
        {
            returnedProvider = rmpEl.GetString();
        }

        if (!string.IsNullOrWhiteSpace(returnedProvider) &&
            !string.Equals(returnedProvider, targetModelProvider, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Target modelProvider mismatch. Requested: {targetModelProvider}, returned: {returnedProvider}");
        }

        // Rename if requested
        if (!string.IsNullOrWhiteSpace(newName))
        {
            try
            {
                await client.RequestAsync("thread/setName", new { threadId = forkedThreadId, name = newName }, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Renaming is best effort
            }
        }

        return new ThreadForkResult(forkedThreadId, sourceThreadId, targetModelProvider, targetModel, newName);
    }
}
