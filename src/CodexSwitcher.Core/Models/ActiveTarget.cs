namespace CodexSwitcher.Core.Models;

public enum ActiveTargetKind
{
    ChatGpt = 1,
    ApiProvider = 2,
    ExternalProvider = 3,
    Unknown = 4
}

/// <summary>
/// Discriminated union representing the active inference target configured in Codex.
/// Separates the inference routing fact from the credential slot fact in auth.json.
/// </summary>
public abstract record ActiveTarget(ActiveTargetKind Kind)
{
    public sealed record ChatGpt(Guid? ProfileId, string? AccountEmail = null) : ActiveTarget(ActiveTargetKind.ChatGpt);
    public sealed record Api(ApiProviderProfile Profile) : ActiveTarget(ActiveTargetKind.ApiProvider);
    public sealed record External(string ProviderId) : ActiveTarget(ActiveTargetKind.ExternalProvider);
    public sealed record Unknown(string? RawProvider = null) : ActiveTarget(ActiveTargetKind.Unknown);
}
