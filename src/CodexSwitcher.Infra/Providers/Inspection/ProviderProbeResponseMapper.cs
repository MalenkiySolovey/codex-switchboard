using System.Text.Json;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Services;

namespace CodexSwitcher.Infra.Providers.Inspection;

/// <summary>
/// Production implementation of <see cref="IProviderProbeResponseMapper"/> extracting
/// normalized models and credit balances using <see cref="JsonPointerExtractor"/>.
/// </summary>
public sealed class ProviderProbeResponseMapper : IProviderProbeResponseMapper
{
    public (bool Success, List<string>? Models, string? Error) MapModelsResponse(
        ProbeHttpResponse response,
        string? pointer)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!response.Success || response.Json == null)
        {
            return (false, null, response.Error);
        }

        var root = response.Json.Value;
        var effectivePointer = pointer ?? "/data";

        if (JsonPointerExtractor.TryExtractStringArray(root, effectivePointer, out var models))
        {
            return (true, models, null);
        }

        return (true, [], null);
    }

    public void MapBalanceAndUsageResponse(
        ProbeHttpResponse response,
        RecipeResponseMapping? mapping,
        ref decimal? balance,
        ref decimal? usedCredits,
        ref decimal? creditLimit,
        ref decimal? remainingCredits,
        ref string? currency)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!response.Success || response.Json == null || mapping == null)
        {
            return;
        }

        var root = response.Json.Value;

        if (mapping.Balance != null && JsonPointerExtractor.TryExtractDecimal(root, mapping.Balance, out var b))
        {
            balance = b;
        }

        if (mapping.Used != null && JsonPointerExtractor.TryExtractDecimal(root, mapping.Used, out var u))
        {
            usedCredits = u;
        }

        if (mapping.Limit != null && JsonPointerExtractor.TryExtractDecimal(root, mapping.Limit, out var l))
        {
            creditLimit = l;
        }

        if (mapping.Remaining != null && JsonPointerExtractor.TryExtractDecimal(root, mapping.Remaining, out var r))
        {
            remainingCredits = r;
        }

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
}
