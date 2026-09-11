using CodexSwitcher.Core.Providers.Catalog;

namespace CodexSwitcher.Infra.Providers.Inspection;

/// <summary>
/// Maps raw HTTP probe responses into normalized domain facts.
/// Uses safe JSON pointer evaluation without runtime reflection.
/// </summary>
public interface IProviderProbeResponseMapper
{
    /// <summary>
    /// Maps a JSON probe response into a list of model IDs.
    /// </summary>
    (bool Success, List<string>? Models, string? Error) MapModelsResponse(
        ProbeHttpResponse response,
        string? pointer);

    /// <summary>
    /// Maps a JSON probe response into balance, usage, limit, and currency facts.
    /// </summary>
    void MapBalanceAndUsageResponse(
        ProbeHttpResponse response,
        RecipeResponseMapping? mapping,
        ref decimal? balance,
        ref decimal? usedCredits,
        ref decimal? creditLimit,
        ref decimal? remainingCredits,
        ref string? currency);
}
