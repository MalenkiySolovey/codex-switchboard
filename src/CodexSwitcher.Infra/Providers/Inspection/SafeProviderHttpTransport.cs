using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Services;

namespace CodexSwitcher.Infra.Providers.Inspection;

/// <summary>
/// Hardened HTTP transport for AI provider capability inspection.
/// Enforces GET/HEAD probes, manual redirect inspection, credential stripping on untrusted redirects,
/// response size capping, and secret redaction.
/// </summary>
public sealed class SafeProviderHttpTransport : ISafeProviderHttpTransport, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public SafeProviderHttpTransport(HttpClient? httpClient = null)
    {
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CodexSwitchboard/0.2.1");
            _ownsHttpClient = true;
        }
    }

    public async Task<ProbeHttpResponse> SendProbeAsync(
        ProviderProbePlan plan,
        ProviderDescriptor descriptor,
        string? apiKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(descriptor);

        var currentUri = plan.TargetUri;
        var redirectsRemaining = 3;
        var shouldAttachCredentials = true;

        while (redirectsRemaining >= 0)
        {
            using var request = new HttpRequestMessage(new HttpMethod(plan.Method), currentUri);

            // Attach configured authentication credentials if permitted
            if (shouldAttachCredentials && !string.IsNullOrWhiteSpace(apiKey))
            {
                if (string.Equals(plan.AuthScheme, "bearer", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                }
                else if (string.Equals(plan.AuthScheme, "x-api-key", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", apiKey.Trim());
                }
            }

            // Attach static headers if defined
            if (plan.Headers != null)
            {
                foreach (var (k, v) in plan.Headers)
                {
                    request.Headers.TryAddWithoutValidation(k, v);
                }
            }

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ProbeHttpResponse(false, null, 0, $"Probe request failed: {SanitizeException(ex, apiKey)}");
            }

            using (response)
            {
                // Handle Redirects (301, 302, 307, 308)
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location;
                    if (location == null)
                    {
                        return new ProbeHttpResponse(false, null, (int)response.StatusCode, "Redirect response missing Location header.");
                    }

                    var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);

                    // Redirect security defense: Verify if redirect destination is trusted
                    if (!ProviderMatcher.IsHostTrusted(descriptor, nextUri))
                    {
                        // CRITICAL: Strip credentials when redirecting outside trusted hosts!
                        shouldAttachCredentials = false;
                        return new ProbeHttpResponse(
                            false,
                            null,
                            (int)response.StatusCode,
                            $"Security violation: Redirect to untrusted host '{nextUri.Host}' blocked to prevent credential exfiltration.");
                    }

                    currentUri = nextUri;
                    redirectsRemaining--;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new ProbeHttpResponse(
                        false,
                        null,
                        (int)response.StatusCode,
                        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                }

                if (string.Equals(plan.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    return new ProbeHttpResponse(true, null, (int)response.StatusCode, null);
                }

                // Enforce response size limit
                byte[] bodyBytes;
                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var ms = new MemoryStream();
                    var buffer = new byte[8192];
                    var totalRead = 0;
                    int read;
                    while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        totalRead += read;
                        if (totalRead > CatalogSecurityConstants.MaxProbeResponseSizeBytes)
                        {
                            return new ProbeHttpResponse(
                                false,
                                null,
                                (int)response.StatusCode,
                                $"Probe response exceeded maximum allowed size of {CatalogSecurityConstants.MaxProbeResponseSizeBytes} bytes.");
                        }
                        ms.Write(buffer, 0, read);
                    }
                    bodyBytes = ms.ToArray();
                }
                catch (Exception ex)
                {
                    return new ProbeHttpResponse(false, null, (int)response.StatusCode, $"Error reading response stream: {SanitizeException(ex, apiKey)}");
                }

                try
                {
                    using var doc = JsonDocument.Parse(bodyBytes);
                    return new ProbeHttpResponse(true, doc.RootElement.Clone(), (int)response.StatusCode, null);
                }
                catch (Exception ex)
                {
                    return new ProbeHttpResponse(false, null, (int)response.StatusCode, $"Malformed JSON response: {SanitizeException(ex, apiKey)}");
                }
            }
        }

        return new ProbeHttpResponse(false, null, 0, "Too many HTTP redirects encountered.");
    }

    private static string SanitizeException(Exception ex, string? apiKey)
    {
        var msg = ex.Message;
        if (!string.IsNullOrWhiteSpace(apiKey) && msg.Contains(apiKey, StringComparison.OrdinalIgnoreCase))
        {
            msg = msg.Replace(apiKey, "[REDACTED_API_KEY]");
        }

        if (msg.Contains("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            msg = "Request failed (credentials redacted).";
        }

        return msg;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
