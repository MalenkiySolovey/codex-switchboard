namespace CodexSwitcher.Core.Models;

/// <summary>
/// Result data captured from the Add API Provider dialog.
/// ApiKey is ephemeral in-memory string passed to the caller for DPAPI encryption.
/// </summary>
public sealed class AddApiProviderResult
{
    public string CatalogProviderId { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string SelectedRouteId { get; set; } = "primary";
    public string SelectedModel { get; set; } = string.Empty;
    public bool SaveAndSwitch { get; set; }
}

/// <summary>
/// Result data captured from the Edit API Provider dialog.
/// </summary>
public sealed class EditApiProviderResult
{
    public string Nickname { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string SelectedRouteId { get; set; } = string.Empty;
    public string SelectedModel { get; set; } = string.Empty;
}
