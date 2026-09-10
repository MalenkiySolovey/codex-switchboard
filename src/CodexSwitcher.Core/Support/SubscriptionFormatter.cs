using System.Globalization;
using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Support;

/// <summary>
/// Formats user-provided subscription tracking dates for display and tooltips.
/// Contains no secrets or sensitive data.
/// </summary>
public static class SubscriptionFormatter
{
    public const string OfficialBillingUrl = "https://chatgpt.com/#settings/Account";

    public static string FormatDisplayText(
        SubscriptionTracking? tracking,
        DateOnly today,
        CultureInfo? culture = null,
        bool pt = false)
    {
        if (tracking is null || !tracking.HasTargetDate)
            return string.Empty;

        var target = tracking.NextRenewalOrExpiryOn!.Value;
        var mode = tracking.Mode;
        var isEstimated = tracking.IsEstimated;
        var isPast = target < today;

        var localCulture = culture ?? (pt ? new CultureInfo("pt-BR") : CultureInfo.InvariantCulture);
        var dateStr = target.ToString("MMM d", localCulture);

        if (pt)
        {
            if (mode == SubscriptionTrackingMode.Renewal)
            {
                var action = isPast ? "renovação foi em" : "renova em";
                var est = isEstimated ? " (est.)" : "";
                return $"• {action} {dateStr}{est}";
            }
            else
            {
                var action = isPast ? "expirou em" : "expira em";
                return $"• {action} {dateStr}";
            }
        }
        else
        {
            if (mode == SubscriptionTrackingMode.Renewal)
            {
                var action = isPast ? "renewal was" : "renews";
                var est = isEstimated ? " (est.)" : "";
                return $"• {action} {dateStr}{est}";
            }
            else
            {
                var action = isPast ? "expired" : "expires";
                return $"• {action} {dateStr}";
            }
        }
    }

    public static string FormatTooltipText(
        SubscriptionTracking? tracking,
        DateOnly today,
        CultureInfo? culture = null,
        bool pt = false)
    {
        if (tracking is null || !tracking.HasTargetDate)
            return string.Empty;

        var target = tracking.NextRenewalOrExpiryOn!.Value;
        var mode = tracking.Mode;
        var isEstimated = tracking.IsEstimated;
        var localCulture = culture ?? (pt ? new CultureInfo("pt-BR") : CultureInfo.InvariantCulture);
        var dateFull = target.ToString("MMMM d, yyyy", localCulture);

        if (pt)
        {
            var modeStr = mode == SubscriptionTrackingMode.Renewal ? "renovação" : "expiração";
            var estStr = isEstimated ? " (estimada)" : "";
            var startedStr = tracking.StartedOn.HasValue
                ? $" Iniciada em: {tracking.StartedOn.Value.ToString("MMMM d, yyyy", localCulture)}."
                : "";
            return $"Rastreamento local fornecido pelo usuário: data de {modeStr}{estStr} em {dateFull}.{startedStr}\nGerencie sua assinatura oficial em chatgpt.com";
        }
        else
        {
            var modeStr = mode == SubscriptionTrackingMode.Renewal ? "renews on" : "expires on";
            var estStr = isEstimated ? " (estimated)" : "";
            var startedStr = tracking.StartedOn.HasValue
                ? $" Started: {tracking.StartedOn.Value.ToString("MMMM d, yyyy", localCulture)}."
                : "";
            return $"User-provided local tracking: {modeStr}{estStr} {dateFull}.{startedStr}\nManage your official subscription at chatgpt.com";
        }
    }
}
