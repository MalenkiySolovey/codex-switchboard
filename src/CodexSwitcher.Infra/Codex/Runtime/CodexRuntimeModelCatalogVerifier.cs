using System.Text.Json;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Common.Paths;

namespace CodexSwitcher.Infra.Codex.Runtime;

/// <summary>
/// Starts a short-lived Switchboard-owned production app-server after routing
/// has been applied and proves that model/list exposes the expected profile
/// inventory. It never mutates Codex state beyond the normal app-server
/// lifecycle and never launches an unversioned fallback binary.
/// </summary>
public sealed class CodexRuntimeModelCatalogVerifier : ICodexRuntimeModelCatalogVerifier
{
    private readonly ICodexRuntimeResolver _runtimeResolver;
    private readonly AppSettings _settings;
    private readonly AppPaths _paths;
    private readonly ISwitchboardCodexProcessRegistry _registry;
    private readonly ICodexRoutingConfigStore _routing;

    public CodexRuntimeModelCatalogVerifier(
        ICodexRuntimeResolver runtimeResolver,
        AppSettings settings,
        AppPaths paths,
        ISwitchboardCodexProcessRegistry registry,
        ICodexRoutingConfigStore routing)
    {
        _runtimeResolver = runtimeResolver ?? throw new ArgumentNullException(nameof(runtimeResolver));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _routing = routing ?? throw new ArgumentNullException(nameof(routing));
    }

    public async Task<CodexRuntimeModelCatalogVerification> VerifyAsync(
        string expectedProvider,
        string expectedSelectedModel,
        IReadOnlyCollection<string> expectedEnabledModels,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSelectedModel);
        ArgumentNullException.ThrowIfNull(expectedEnabledModels);

        var runtime = _runtimeResolver.ResolveCurrentRuntime(_settings.CodexExecutablePathOverride);
        if (string.IsNullOrWhiteSpace(runtime.ExecutablePath) || !File.Exists(runtime.ExecutablePath))
        {
            return new CodexRuntimeModelCatalogVerification(false, [], "Current production Codex runtime could not be resolved.");
        }

        var activeRouting = _routing.ReadRoutingState(_paths.Codex.ConfigTomlPath);
        if (!string.Equals(activeRouting.ModelProvider, expectedProvider, StringComparison.Ordinal) ||
            !string.Equals(activeRouting.Model, expectedSelectedModel, StringComparison.Ordinal))
        {
            return new CodexRuntimeModelCatalogVerification(
                false,
                [],
                "config.toml does not contain the expected provider and selected model for runtime verification.");
        }

        if (string.IsNullOrWhiteSpace(activeRouting.ModelCatalogJson) || !File.Exists(activeRouting.ModelCatalogJson))
        {
            return new CodexRuntimeModelCatalogVerification(false, [], "The active profile catalog is missing from config.toml or disk.");
        }

        await using var client = new CodexAppServerClient(runtime.ExecutablePath, _paths.Codex.CodexHome, _registry);
        try
        {
            await client.StartAsync(cancellationToken).ConfigureAwait(false);
            var response = await client.RequestAsync(
                "model/list",
                new { includeHidden = true },
                TimeSpan.FromSeconds(25),
                cancellationToken).ConfigureAwait(false);

            var observed = ExtractModelSlugs(response);
            var missing = expectedEnabledModels
                .Where(expected => !observed.Contains(expected, StringComparer.Ordinal))
                .ToList();
            if (missing.Count > 0)
            {
                return new CodexRuntimeModelCatalogVerification(
                    false,
                    observed,
                    $"model/list did not expose expected enabled model(s): {string.Join(", ", missing)}.");
            }

            if (!observed.Contains(expectedSelectedModel, StringComparer.Ordinal))
            {
                return new CodexRuntimeModelCatalogVerification(
                    false,
                    observed,
                    $"model/list did not expose selected model '{expectedSelectedModel}'.");
            }

            return new CodexRuntimeModelCatalogVerification(true, observed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CodexRuntimeModelCatalogVerification(false, [], Sanitize(ex.Message));
        }
    }

    private static HashSet<string> ExtractModelSlugs(JsonElement response)
    {
        var models = new HashSet<string>(StringComparer.Ordinal);
        JsonElement modelArray = default;
        if (response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            modelArray = data;
        }
        else if (response.ValueKind == JsonValueKind.Object &&
                 response.TryGetProperty("models", out var catalogModels) && catalogModels.ValueKind == JsonValueKind.Array)
        {
            modelArray = catalogModels;
        }
        else if (response.ValueKind == JsonValueKind.Array)
        {
            modelArray = response;
        }

        if (modelArray.ValueKind != JsonValueKind.Array)
        {
            return models;
        }

        foreach (var model in modelArray.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var identifier = model.TryGetProperty("slug", out var slug) && slug.ValueKind == JsonValueKind.String
                ? slug.GetString()
                : model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : model.TryGetProperty("model", out var modelName) && modelName.ValueKind == JsonValueKind.String
                        ? modelName.GetString()
                        : null;
            if (!string.IsNullOrWhiteSpace(identifier))
            {
                models.Add(identifier);
            }
        }

        return models;
    }

    private static string Sanitize(string message) =>
        message.Contains("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? "Runtime model/list verification failed (credentials redacted)."
            : message;
}
