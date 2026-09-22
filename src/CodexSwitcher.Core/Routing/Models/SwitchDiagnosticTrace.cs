using System;
using System.Globalization;
using System.Text;

namespace CodexSwitcher.Core.Routing.Models;

/// <summary>
/// Sanitized diagnostic trace for a switch transaction.
/// Strictly excludes API keys, authorization tokens, and credentials.
/// </summary>
public sealed class SwitchDiagnosticTrace
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public Guid? RequestedEndpointId { get; set; }
    public Guid? RequestedModelConfigId { get; set; }
    public string? RequestedModel { get; set; }

    public string? PreviousTargetSummary { get; set; }
    public Guid? SecretOwnerResolved { get; set; }
    public string? SwitchPlan { get; set; }

    public bool ConfigChanged { get; set; }
    public bool CatalogChanged { get; set; }
    public bool RuntimeRestartRequired { get; set; }
    public bool RuntimeRestartCompleted { get; set; }

    public string? EffectiveModelProvider { get; set; }
    public string? EffectiveModel { get; set; }
    public string? EffectiveModelCatalogJson { get; set; }

    public bool RoutingStateCommitted { get; set; }
    public bool FinalTargetMatchesRequested { get; set; }
    public string? PostconditionFailureReason { get; set; }

    public string ToSanitizedSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"RequestedTarget: EndpointId={RequestedEndpointId}, ModelConfigId={RequestedModelConfigId}, Model={RequestedModel}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"PreviousTarget: {PreviousTargetSummary}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"SecretOwnerResolved: {SecretOwnerResolved}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"SwitchPlan: {SwitchPlan}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"ConfigChanged: {ConfigChanged}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"CatalogChanged: {CatalogChanged}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"RuntimeRestartRequired: {RuntimeRestartRequired}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"RuntimeRestartCompleted: {RuntimeRestartCompleted}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"EffectiveModelProvider: {EffectiveModelProvider}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"EffectiveModel: {EffectiveModel}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"RoutingStateCommitted: {RoutingStateCommitted}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"FinalTargetMatchesRequested: {FinalTargetMatchesRequested}");
        if (!string.IsNullOrWhiteSpace(PostconditionFailureReason))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"PostconditionFailureReason: {PostconditionFailureReason}");
        }
        return sb.ToString().TrimEnd();
    }
}
