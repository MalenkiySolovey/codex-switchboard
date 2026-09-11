using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
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
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Core.Usage.Models;
using System.Globalization;

namespace CodexSwitcher.Core.Usage.Formatting;

/// <summary>
/// Formats rate-limit reset countdowns deterministically without network or disk operations.
/// </summary>
public static class UsageCountdownFormatter
{
    public static string FormatCountdown(DateTimeOffset? resetsAt, DateTimeOffset now, bool pt = false)
    {
        if (resetsAt is null)
            return "-";

        if (resetsAt.Value <= now)
            return pt ? "Reset pendente — renove para atualizar" : "Reset due — refresh to verify";

        var diff = resetsAt.Value - now;
        if (diff.TotalDays >= 1)
        {
            var days = (int)diff.TotalDays;
            var hours = diff.Hours;
            return pt ? $"Reinicia em {days}d {hours}h" : $"Resets in {days}d {hours}h";
        }

        if (diff.TotalHours >= 1)
        {
            var hours = (int)diff.TotalHours;
            var minutes = diff.Minutes;
            return pt ? $"Reinicia em {hours}h {minutes}m" : $"Resets in {hours}h {minutes}m";
        }

        if (diff.TotalMinutes >= 1)
        {
            var minutes = (int)diff.TotalMinutes;
            return pt ? $"Reinicia em {minutes}m" : $"Resets in {minutes}m";
        }

        var seconds = Math.Max(1, (int)diff.TotalSeconds);
        return pt ? $"Reinicia em {seconds}s" : $"Resets in {seconds}s";
    }

    public static string FormatCompactCountdown(DateTimeOffset? resetsAt, DateTimeOffset now, bool pt = false)
    {
        if (resetsAt is null)
            return "-";

        if (resetsAt.Value <= now)
            return pt ? "Reset pendente" : "Reset due";

        var diff = resetsAt.Value - now;
        if (diff.TotalDays >= 1)
        {
            var days = (int)diff.TotalDays;
            var hours = diff.Hours;
            return $"{days}d {hours}h";
        }

        if (diff.TotalHours >= 1)
        {
            var hours = (int)diff.TotalHours;
            var minutes = diff.Minutes;
            return $"{hours}h {minutes}m";
        }

        if (diff.TotalMinutes >= 1)
        {
            var minutes = (int)diff.TotalMinutes;
            return $"{minutes}m";
        }

        var seconds = Math.Max(1, (int)diff.TotalSeconds);
        return $"{seconds}s";
    }

    public static string FormatCountdownWithAbsolute(
        DateTimeOffset? resetsAt,
        DateTimeOffset now,
        CultureInfo? culture = null,
        bool pt = false)
    {
        if (resetsAt is null)
            return "-";

        var relative = FormatCountdown(resetsAt, now, pt);
        var local = resetsAt.Value.ToLocalTime();
        var effectiveCulture = culture ?? (pt ? new CultureInfo("pt-BR") : CultureInfo.InvariantCulture);
        var concise = local.ToString("MMM d, HH:mm", effectiveCulture);
        return $"{relative} ({concise})";
    }

    public static string FormatAbsoluteReset(DateTimeOffset? resetsAt, bool pt = false)
    {
        if (resetsAt is null)
            return pt ? "Horário de reinício: desconhecido" : "Reset time: unknown";

        var local = resetsAt.Value.ToLocalTime();
        var datePart = local.ToString("dd/MM/yyyy HH:mm");
        return pt ? $"Reinício programado: {datePart}" : $"Scheduled reset: {datePart}";
    }

    public static string FormatFullResetTooltip(
        DateTimeOffset? resetsAt,
        CultureInfo? culture = null,
        bool pt = false)
    {
        if (resetsAt is null)
            return pt ? "Horário de reinício: desconhecido" : "Reset time: unknown";

        var local = resetsAt.Value.ToLocalTime();
        var effectiveCulture = culture ?? (pt ? new CultureInfo("pt-BR") : CultureInfo.InvariantCulture);
        var timeStr = local.ToString("HH:mm:ss");

        if (pt)
        {
            var dateStr = local.ToString("d 'de' MMMM 'de' yyyy", effectiveCulture);
            return $"{dateStr} às {timeStr} (horário local)";
        }
        else
        {
            var dateStr = local.ToString("MMMM d, yyyy", effectiveCulture);
            return $"{dateStr} at {timeStr} (local time)";
        }
    }

    public static CreditExpiryUrgency GetExpiryUrgency(DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        if (!expiresAt.HasValue)
            return CreditExpiryUrgency.Normal;

        if (expiresAt.Value <= now)
            return CreditExpiryUrgency.Expired;

        var diff = expiresAt.Value - now;
        if (diff.TotalHours <= 24)
            return CreditExpiryUrgency.Critical;

        if (diff.TotalDays <= 7)
            return CreditExpiryUrgency.Warning;

        return CreditExpiryUrgency.Normal;
    }

    public static string FormatCreditExpiry(
        DateTimeOffset? expiresAt,
        DateTimeOffset now,
        CultureInfo? culture = null,
        bool pt = false)
    {
        if (!expiresAt.HasValue)
            return pt ? "Sem data de expiração" : "No expiration date";

        if (expiresAt.Value <= now)
            return pt ? "Validade expirada — renove para verificar" : "Expiry time passed — refresh to verify";

        var effectiveCulture = culture ?? (pt ? new CultureInfo("pt-BR") : CultureInfo.InvariantCulture);
        var local = expiresAt.Value.ToLocalTime();
        var dateStr = local.ToString("MMM d, HH:mm", effectiveCulture);
        var diff = expiresAt.Value - now;

        if (diff.TotalDays >= 1)
        {
            var days = (int)diff.TotalDays;
            return pt ? $"Expira em {days}d ({dateStr})" : $"Expires in {days}d ({dateStr})";
        }

        if (diff.TotalHours >= 1)
        {
            var hours = (int)diff.TotalHours;
            var minutes = diff.Minutes;
            return pt ? $"Expira em {hours}h {minutes}m ({dateStr})" : $"Expires in {hours}h {minutes}m ({dateStr})";
        }

        var mins = Math.Max(1, (int)diff.TotalMinutes);
        return pt ? $"Expira em {mins}m ({dateStr})" : $"Expires in {mins}m ({dateStr})";
    }
}
