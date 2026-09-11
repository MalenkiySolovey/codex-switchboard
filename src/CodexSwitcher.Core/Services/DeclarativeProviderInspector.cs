using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Catalog;
using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Services;

/// <summary>
/// Safe, non-Turing-complete declarative inspection engine for AI model providers.
/// Strictly enforces GET/HEAD-only, exact trusted-host validation, redirect credential stripping,
/// response size capping, and normalized snapshot extraction.
/// </summary>
public sealed class DeclarativeProviderInspector : IDeclarativeProviderInspector
{
    private readonly HttpClient _httpClient;

    public DeclarativeProviderInspector(HttpClient? httpClient = null)
    {
        if (httpClient != null)
        {
            _httpClient = httpClient;
        }
        else
        {
            // Default handler with disabled automatic redirect to enable strict redirect defense
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CodexSwitchboard/0.1.3");
        }
    }

    /// <summary>
    /// Inspects a provider using its catalog descriptor and returns a normalized ApiProviderSnapshot.
    /// </summary>
    public async Task<ApiProviderSnapshot> InspectAsync(
        ProviderDescriptor descriptor,
        string activeBaseUrl,
        string? apiKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(activeBaseUrl))
            throw new ArgumentException("Active base URL cannot be empty.", nameof(activeBaseUrl));

        var capabilities = new Dictionary<string, CapabilityStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["models"] = descriptor.Capabilities?.Models?.Status ?? CapabilityStatus.Unknown,
            ["balance"] = descriptor.Capabilities?.Balance?.Status ?? CapabilityStatus.Unknown,
            ["usage"] = descriptor.Capabilities?.Usage?.Status ?? CapabilityStatus.Unknown,
        };

        var modelsList = new List<string>();
        decimal? balance = null;
        string? currency = null;
        decimal? usedCredits = null;
        decimal? creditLimit = null;
        decimal? remainingCredits = null;
        string? overallError = null;
        var connectionStatus = HealthStatus.Unknown;

        // 1. Inspect Models if supported
        var modelsRecipe = descriptor.Capabilities?.Models;
        if (modelsRecipe != null && modelsRecipe.Status == CapabilityStatus.Supported)
        {
            var modelsResult = await ExecuteModelsProbeAsync(descriptor, modelsRecipe, activeBaseUrl, apiKey, ct);
            if (modelsResult.Success)
            {
                connectionStatus = HealthStatus.Valid;
                if (modelsResult.Models != null)
                    modelsList.AddRange(modelsResult.Models);
            }
            else
            {
                connectionStatus = HealthStatus.Error;
                overallError = modelsResult.Error;
            }
        }

        // 2. Inspect Balance if supported
        var balanceRecipe = descriptor.Capabilities?.Balance;
        if (balanceRecipe != null && balanceRecipe.Status == CapabilityStatus.Supported)
        {
            var balanceResult = await ExecuteJsonProbeAsync(descriptor, balanceRecipe, activeBaseUrl, apiKey, ct);
            if (balanceResult.Success && balanceResult.Json != null)
            {
                if (connectionStatus == HealthStatus.Unknown)
                    connectionStatus = HealthStatus.Valid;

                ExtractFields(balanceResult.Json.Value, balanceRecipe.Response, ref balance, ref usedCredits, ref creditLimit, ref remainingCredits, ref currency);
            }
            else if (balanceResult.Error != null)
            {
                overallError ??= balanceResult.Error;
            }
        }

        // 3. Inspect Usage if supported and separate from balance
        var usageRecipe = descriptor.Capabilities?.Usage;
        if (usageRecipe != null && usageRecipe.Status == CapabilityStatus.Supported && usageRecipe != balanceRecipe)
        {
            var usageResult = await ExecuteJsonProbeAsync(descriptor, usageRecipe, activeBaseUrl, apiKey, ct);
            if (usageResult.Success && usageResult.Json != null)
            {
                ExtractFields(usageResult.Json.Value, usageRecipe.Response, ref balance, ref usedCredits, ref creditLimit, ref remainingCredits, ref currency);
            }
        }

        if (connectionStatus == HealthStatus.Unknown)
        {
            connectionStatus = overallError == null ? HealthStatus.Valid : HealthStatus.Unknown;
        }

        return new ApiProviderSnapshot
        {
            ConnectionStatus = connectionStatus,
            Models = modelsList,
            Balance = balance,
            Currency = currency,
            UsedCredits = usedCredits,
            RemainingCredits = remainingCredits,
            CreditLimit = creditLimit,
            Usage = usedCredits,
            Capabilities = capabilities,
            LastCheckedAt = DateTimeOffset.UtcNow,
            Error = overallError,
        };
    }

    /// <summary>
    /// Inspects an unknown generic OpenAI-compatible provider using safe default endpoints.
    /// strictly never guesses balance/credits paths.
    /// </summary>
    public async Task<ApiProviderSnapshot> InspectGenericUnknownAsync(
        string rawBaseUrl,
        string? apiKey,
        CancellationToken ct = default)
    {
        var normalizedBase = ProviderMatcher.NormalizeBaseUrl(rawBaseUrl);
        if (string.IsNullOrEmpty(normalizedBase))
        {
            return new ApiProviderSnapshot
            {
                ConnectionStatus = HealthStatus.Unknown,
                Error = "Invalid base URL format.",
                LastCheckedAt = DateTimeOffset.UtcNow,
            };
        }

        var baseUri = new Uri(normalizedBase);
        var host = baseUri.IdnHost.ToLowerInvariant();

        // Synthesize a generic ad-hoc descriptor with strict trustedHosts limited to the target host
        var syntheticDescriptor = new ProviderDescriptor
        {
            Id = "generic-unknown",
            DisplayName = host,
            TrustedHosts = new List<string> { host },
            Capabilities = new ProviderCapabilities
            {
                Models = new ProviderCapabilityRecipe
                {
                    Status = CapabilityStatus.Supported,
                    Strategy = "openai-models",
                    Request = new RecipeRequest { Method = "GET", Path = "/models", Auth = "bearer" }
                },
                Balance = new ProviderCapabilityRecipe { Status = CapabilityStatus.Unknown, Strategy = "unknown" },
                Usage = new ProviderCapabilityRecipe { Status = CapabilityStatus.Unknown, Strategy = "unknown" },
            }
        };

        var modelsRecipe = syntheticDescriptor.Capabilities.Models;
        var modelsResult = await ExecuteModelsProbeAsync(syntheticDescriptor, modelsRecipe, normalizedBase, apiKey, ct);

        var capabilities = new Dictionary<string, CapabilityStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["models"] = modelsResult.Success ? CapabilityStatus.Supported : CapabilityStatus.Unknown,
            ["balance"] = CapabilityStatus.Unknown,
            ["usage"] = CapabilityStatus.Unknown,
        };

        return new ApiProviderSnapshot
        {
            ConnectionStatus = modelsResult.Success ? HealthStatus.Valid : HealthStatus.Unknown,
            Models = (IReadOnlyList<string>?)modelsResult.Models ?? Array.Empty<string>(),
            Balance = null,
            Currency = null,
            Capabilities = capabilities,
            LastCheckedAt = DateTimeOffset.UtcNow,
            Error = modelsResult.Error,
        };
    }

    private async Task<(bool Success, List<string>? Models, string? Error)> ExecuteModelsProbeAsync(
        ProviderDescriptor descriptor,
        ProviderCapabilityRecipe recipe,
        string baseUrl,
        string? apiKey,
        CancellationToken ct)
    {
        var jsonResult = await ExecuteJsonProbeAsync(descriptor, recipe, baseUrl, apiKey, ct);
        if (!jsonResult.Success || jsonResult.Json == null)
            return (false, null, jsonResult.Error);

        var root = jsonResult.Json.Value;
        var pointer = recipe.Response?.Models?.Pointer ?? "/data";

        if (JsonPointerExtractor.TryExtractStringArray(root, pointer, out var models))
        {
            return (true, models, null);
        }

        return (true, new List<string>(), null);
    }

    private async Task<(bool Success, JsonElement? Json, string? Error)> ExecuteJsonProbeAsync(
        ProviderDescriptor descriptor,
        ProviderCapabilityRecipe recipe,
        string baseUrl,
        string? apiKey,
        CancellationToken ct)
    {
        var reqConfig = recipe.Request ?? new RecipeRequest { Method = "GET", Path = "/models" };

        // Hard requirement: Only GET and HEAD are permitted for automatic inspection
        var method = reqConfig.Method?.Trim().ToUpperInvariant() ?? "GET";
        if (method != "GET" && method != "HEAD")
        {
            return (false, null, $"Security violation: Method '{method}' is not allowed for automatic inspection. Only GET and HEAD are permitted.");
        }

        // Build target URI
        string targetUrl;
        if (!string.IsNullOrWhiteSpace(reqConfig.Url))
        {
            targetUrl = reqConfig.Url;
        }
        else
        {
            var trimmedBase = baseUrl.TrimEnd('/');
            var path = reqConfig.Path ?? "";
            if (!path.StartsWith('/'))
                path = "/" + path;
            targetUrl = trimmedBase + path;
        }

        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri))
        {
            return (false, null, "Invalid target probe URL.");
        }

        // Security boundary: Scheme check (HTTPS required, except for loopback in test environments)
        if (!targetUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && !IsLoopback(targetUri))
        {
            return (false, null, $"Security violation: Insecure HTTP is prohibited for credential probes: {targetUri.Host}");
        }

        // Security boundary: Trusted host check
        if (!ProviderMatcher.IsHostTrusted(descriptor, targetUri))
        {
            return (false, null, $"Security violation: Target host '{targetUri.Host}' is not in the trustedHosts whitelist.");
        }

        // Execute request with manual redirect handling to defend credentials
        return await ExecuteWithRedirectDefenseAsync(targetUri, method, reqConfig, descriptor, apiKey, ct);
    }

    private async Task<(bool Success, JsonElement? Json, string? Error)> ExecuteWithRedirectDefenseAsync(
        Uri targetUri,
        string method,
        RecipeRequest reqConfig,
        ProviderDescriptor descriptor,
        string? apiKey,
        CancellationToken ct)
    {
        var currentUri = targetUri;
        var redirectsRemaining = 3;
        var shouldAttachCredentials = true;

        while (redirectsRemaining >= 0)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), currentUri);

            // Attach configured authentication credentials if permitted
            if (shouldAttachCredentials && !string.IsNullOrWhiteSpace(apiKey))
            {
                var authScheme = reqConfig.Auth?.Trim().ToLowerInvariant() ?? "none";
                if (authScheme == "bearer")
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                }
                else if (authScheme == "x-api-key")
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", apiKey.Trim());
                }
            }

            // Attach static headers if defined
            if (reqConfig.Headers != null)
            {
                foreach (var (k, v) in reqConfig.Headers)
                {
                    request.Headers.TryAddWithoutValidation(k, v);
                }
            }

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (false, null, $"Probe request failed: {SanitizeException(ex)}");
            }

            using (response)
            {
                // Handle Redirects (301, 302, 307, 308)
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location;
                    if (location == null)
                        return (false, null, "Redirect response missing Location header.");

                    var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);

                    // Redirect security defense: Verify if redirect destination is trusted
                    if (!ProviderMatcher.IsHostTrusted(descriptor, nextUri))
                    {
                        // CRITICAL: Strip credentials when redirecting outside trusted hosts!
                        shouldAttachCredentials = false;
                        return (false, null, $"Security violation: Redirect to untrusted host '{nextUri.Host}' blocked to prevent credential exfiltration.");
                    }

                    currentUri = nextUri;
                    redirectsRemaining--;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return (false, null, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                }

                if (method == "HEAD")
                {
                    return (true, null, null);
                }

                // Enforce response size limit
                byte[] bodyBytes;
                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    using var ms = new MemoryStream();
                    var buffer = new byte[8192];
                    var totalRead = 0;
                    int read;
                    while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                    {
                        totalRead += read;
                        if (totalRead > CatalogSecurityConstants.MaxProbeResponseSizeBytes)
                        {
                            return (false, null, $"Probe response exceeded maximum allowed size of {CatalogSecurityConstants.MaxProbeResponseSizeBytes} bytes.");
                        }
                        ms.Write(buffer, 0, read);
                    }
                    bodyBytes = ms.ToArray();
                }
                catch (Exception ex)
                {
                    return (false, null, $"Error reading response stream: {SanitizeException(ex)}");
                }

                try
                {
                    using var doc = JsonDocument.Parse(bodyBytes);
                    return (true, doc.RootElement.Clone(), null);
                }
                catch (Exception ex)
                {
                    return (false, null, $"Malformed JSON response: {SanitizeException(ex)}");
                }
            }
        }

        return (false, null, "Too many HTTP redirects encountered.");
    }

    private static void ExtractFields(
        JsonElement root,
        RecipeResponseMapping? mapping,
        ref decimal? balance,
        ref decimal? usedCredits,
        ref decimal? creditLimit,
        ref decimal? remainingCredits,
        ref string? currency)
    {
        if (mapping == null)
            return;

        if (mapping.Balance != null && JsonPointerExtractor.TryExtractDecimal(root, mapping.Balance, out var b))
            balance = b;

        if (mapping.Used != null && JsonPointerExtractor.TryExtractDecimal(root, mapping.Used, out var u))
            usedCredits = u;

        if (mapping.Limit != null && JsonPointerExtractor.TryExtractDecimal(root, mapping.Limit, out var l))
            creditLimit = l;

        if (mapping.Remaining != null && JsonPointerExtractor.TryExtractDecimal(root, mapping.Remaining, out var r))
            remainingCredits = r;

        if (mapping.Currency != null)
        {
            if (!string.IsNullOrWhiteSpace(mapping.Currency.Literal))
            {
                currency = mapping.Currency.Literal;
            }
            else if (!string.IsNullOrWhiteSpace(mapping.Currency.Pointer) &&
                     JsonPointerExtractor.TryExtractString(root, mapping.Currency.Pointer, out var c))
            {
                currency = c;
            }
        }
    }

    private static bool IsLoopback(Uri uri)
    {
        return uri.IsLoopback ||
               string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeException(Exception ex)
    {
        // Redact any possible URL parameters or credential tokens in error messages
        var msg = ex.Message;
        if (msg.Contains("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            msg = "Request failed (credentials redacted).";
        }
        return msg;
    }
}
