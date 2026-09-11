using System.Globalization;
using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Support;

/// <summary>
/// Resultado da resolução de apresentação de assinatura para exibição em cards e tooltips.
/// </summary>
public sealed record SubscriptionPresentation(
    string? DisplayText,
    string? TooltipText)
{
    public bool HasText => !string.IsNullOrWhiteSpace(DisplayText);
}

/// <summary>
/// Resolve a precedência de exibição entre rastreamento manual do usuário e detecção automática via OAuth.
/// Precedência estrita (BUSINESS_RULES §3.4 / §9):
/// 1. Rastreamento manual do usuário (SubscriptionTracking) com data de destino configurada;
/// 2. Data de término de período detectada automaticamente (ActiveUntil);
/// 3. Estimativa mensal a partir da data de início observada (ActiveStart);
/// 4. Apenas o nome do plano (sem texto de assinatura adicional).
/// </summary>
public static class SubscriptionPresentationResolver
{
    public const string OfficialBillingUrl = "https://chatgpt.com/#settings/Account";

    public static SubscriptionPresentation Resolve(
        SubscriptionTracking? manualTracking,
        DetectedSubscriptionInfo? detected,
        string? planType,
        DateOnly today,
        CultureInfo? culture = null,
        bool pt = false)
    {
        var localCulture = culture ?? (pt ? new CultureInfo("pt-BR") : CultureInfo.InvariantCulture);

        // Precedência 1: Rastreamento manual fornecido pelo usuário
        if (manualTracking is not null && manualTracking.HasTargetDate)
        {
            var target = manualTracking.NextRenewalOrExpiryOn!.Value;
            var isPast = target < today;
            var dateStr = target.ToString("MMM d", localCulture);
            var isEstimated = manualTracking.IsEstimated;
            var est = isEstimated ? (pt ? " (est.)" : " (est.)") : "";

            string display;
            if (pt)
            {
                var action = manualTracking.Mode == SubscriptionTrackingMode.Renewal
                    ? (isPast ? "renovação foi em" : "renova em")
                    : (isPast ? "expirou em" : "expira em");
                display = $"{action} {dateStr}{est}";
            }
            else
            {
                var action = manualTracking.Mode == SubscriptionTrackingMode.Renewal
                    ? (isPast ? "renewal was" : "renews")
                    : (isPast ? "expired" : "expires");
                display = $"{action} {dateStr}{est}";
            }

            var tooltip = SubscriptionFormatter.FormatTooltipText(manualTracking, today, localCulture, pt);
            return new SubscriptionPresentation(display, tooltip);
        }

        // Precedência 2 e 3: Informação detectada automaticamente via token
        if (detected is not null && detected.ActiveUntilUtc.HasValue)
        {
            var untilDate = DateOnly.FromDateTime(detected.ActiveUntilUtc.Value.LocalDateTime);
            var dateStr = untilDate.ToString("MMM d", localCulture);

            string display;
            if (detected.Source == DetectedSubscriptionSource.EstimatedFromStart)
            {
                display = pt ? $"renovação est. {dateStr}" : $"est. renewal {dateStr}";
            }
            else
            {
                display = pt ? $"período até {dateStr}" : $"period until {dateStr}";
            }

            var tooltip = BuildDetectedTooltip(detected, localCulture, pt);
            return new SubscriptionPresentation(display, tooltip);
        }

        // Precedência 4: Sem datas utilizáveis
        return new SubscriptionPresentation(null, null);
    }

    private static string BuildDetectedTooltip(DetectedSubscriptionInfo detected, CultureInfo culture, bool pt)
    {
        var lines = new List<string>();

        if (detected.ActiveStartUtc.HasValue && detected.ActiveUntilUtc.HasValue)
        {
            var startStr = DateOnly.FromDateTime(detected.ActiveStartUtc.Value.LocalDateTime).ToString("MMMM d, yyyy", culture);
            var untilStr = DateOnly.FromDateTime(detected.ActiveUntilUtc.Value.LocalDateTime).ToString("MMMM d, yyyy", culture);
            lines.Add(pt
                ? $"Período de assinatura: {startStr} – {untilStr}"
                : $"Subscription period: {startStr} – {untilStr}");
        }
        else if (detected.ActiveUntilUtc.HasValue)
        {
            var untilStr = DateOnly.FromDateTime(detected.ActiveUntilUtc.Value.LocalDateTime).ToString("MMMM d, yyyy", culture);
            lines.Add(pt
                ? $"Término do período: {untilStr}"
                : $"Period end: {untilStr}");
        }

        if (detected.Source == DetectedSubscriptionSource.EstimatedFromStart)
        {
            var startStr = detected.ActiveStartUtc.HasValue
                ? DateOnly.FromDateTime(detected.ActiveStartUtc.Value.LocalDateTime).ToString("MMMM d, yyyy", culture)
                : "-";
            lines.Add(pt
                ? $"Estimado a partir da data de início ({startStr}). Não constitui informação oficial de faturamento."
                : $"Estimated from detected subscription start date ({startStr}). Not authoritative billing information.");
        }
        else
        {
            var obsStr = DateOnly.FromDateTime(detected.ObservedAtUtc.LocalDateTime).ToString("MMM d, yyyy", culture);
            lines.Add(pt
                ? $"Detectado a partir dos metadados da conta Codex OAuth (observado em {obsStr}). Data de melhor-esforço, não autoritativa de faturamento."
                : $"Detected from Codex OAuth account metadata (observed {obsStr}). Best-effort subscription-period date, not authoritative billing information.");
        }

        if (detected.IsStale)
        {
            lines.Add(pt
                ? "Aviso: a data observada pode estar desatualizada se a assinatura foi renovada recentemente."
                : "Note: Detected subscription date may be stale if recently renewed.");
        }

        lines.Add(pt
            ? "Gerencie sua assinatura oficial em chatgpt.com (Configurações > Assinatura)."
            : "Manage your official subscription at chatgpt.com (Settings > Billing).");

        return string.Join(Environment.NewLine, lines);
    }
}
