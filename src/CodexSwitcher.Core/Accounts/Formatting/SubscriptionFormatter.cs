using System.Globalization;
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;
using CodexSwitcher.Core.Security.Verification;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Core.Accounts.Models;

namespace CodexSwitcher.Core.Accounts.Formatting;

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
