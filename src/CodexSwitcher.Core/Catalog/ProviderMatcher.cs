using System.Globalization;

namespace CodexSwitcher.Core.Catalog;

/// <summary>
/// Strict exact-host matching engine for provider detection and security boundaries.
/// NEVER uses substring, regex, or suffix matching to prevent spoofing/exfiltration.
/// </summary>
public static class ProviderMatcher
{
    /// <summary>
    /// Normalizes a host or URL string into a canonical lowercase IDN host.
    /// </summary>
    public static string? NormalizeHost(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        input = input.Trim();

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            return uri.IdnHost.ToLowerInvariant();
        }

        // If input does not have a scheme, try prepending https://
        if (!input.Contains("://") && Uri.TryCreate($"https://{input}", UriKind.Absolute, out var fallbackUri))
        {
            return fallbackUri.IdnHost.ToLowerInvariant();
        }

        return input.ToLowerInvariant();
    }

    /// <summary>
    /// Normalizes a base URL (lowercase scheme and host, normalized path without trailing slash).
    /// </summary>
    public static string? NormalizeBaseUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
            return null;

        var host = uri.IdnHost.ToLowerInvariant();
        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        var path = uri.AbsolutePath.TrimEnd('/');
        return $"{uri.Scheme.ToLowerInvariant()}://{host}{port}{path}";
    }

    /// <summary>
    /// Verifies if a given descriptor matches the specified input URL or hostname.
    /// </summary>
    public static bool Matches(ProviderDescriptor descriptor, string? input)
    {
        if (descriptor == null || string.IsNullOrWhiteSpace(input))
            return false;

        var normalizedInputHost = NormalizeHost(input);
        if (string.IsNullOrEmpty(normalizedInputHost))
            return false;

        if (descriptor.Match?.ExactHosts != null)
        {
            foreach (var rawExactHost in descriptor.Match.ExactHosts)
            {
                var normalizedExact = NormalizeHost(rawExactHost);
                if (string.Equals(normalizedInputHost, normalizedExact, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        var normalizedInputBaseUrl = NormalizeBaseUrl(input);
        if (!string.IsNullOrEmpty(normalizedInputBaseUrl) && descriptor.Match?.ExactBaseUrls != null)
        {
            foreach (var rawBaseUrl in descriptor.Match.ExactBaseUrls)
            {
                var normalizedBase = NormalizeBaseUrl(rawBaseUrl);
                if (string.Equals(normalizedInputBaseUrl, normalizedBase, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the first matching provider descriptor in the catalog for the specified URL or hostname.
    /// </summary>
    public static ProviderDescriptor? FindBestMatch(ProviderCatalog catalog, string? input)
    {
        if (catalog?.Providers == null || string.IsNullOrWhiteSpace(input))
            return null;

        foreach (var provider in catalog.Providers)
        {
            if (Matches(provider, input))
                return provider;
        }

        return null;
    }

    /// <summary>
    /// Checks whether the target URI's host is in the provider's strict trustedHosts list.
    /// </summary>
    public static bool IsHostTrusted(ProviderDescriptor descriptor, Uri targetUri)
    {
        if (descriptor == null || targetUri == null)
            return false;

        var targetHost = targetUri.IdnHost.ToLowerInvariant();

        if (descriptor.TrustedHosts != null)
        {
            foreach (var trusted in descriptor.TrustedHosts)
            {
                var normalizedTrusted = NormalizeHost(trusted);
                if (string.Equals(targetHost, normalizedTrusted, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
