using System.Text.Json;
using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Infra.Codex;

/// <summary>
/// Tolerant parser for Codex app-server account and rate-limit JSON responses.
/// Decoupled from upstream schema changes: ignores unknown properties and tolerates missing or null fields.
/// </summary>
public static class CodexUsageResponseParser
{
    private static readonly string[] KnownWindowSlots = ["primary", "secondary"];

    /// <summary>
    /// Parses an <c>account/read</c> response payload.
    /// <para>
    /// NOTE on <c>requiresOpenaiAuth</c>: This flag indicates whether the configured provider requires
    /// OpenAI authentication (as opposed to API-key or anonymous access). A valid, fully authenticated
    /// ChatGPT profile returns <c>account != null</c> (with <c>type = "chatgpt"</c>) and <c>requiresOpenaiAuth = true</c>.
    /// It must NOT be interpreted as an authentication failure, a re-login requirement, or a rate-limit indicator.
    /// </para>
    /// </summary>
    public static (string? AccountType, string? Email, string? PlanType, bool RequiresOpenaiAuth) ParseAccountInfo(JsonElement root)
    {
        string? accountType = null;
        string? email = null;
        string? planType = null;
        var requiresOpenaiAuth = false;

        if (root.ValueKind != JsonValueKind.Object)
            return (accountType, email, planType, false);

        if (root.TryGetProperty("account", out var accountEl) && accountEl.ValueKind == JsonValueKind.Object)
        {
            if (accountEl.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
                accountType = typeEl.GetString();

            if (accountEl.TryGetProperty("email", out var emailEl) && emailEl.ValueKind == JsonValueKind.String)
                email = emailEl.GetString();

            if (accountEl.TryGetProperty("planType", out var planEl) && planEl.ValueKind == JsonValueKind.String)
                planType = planEl.GetString();
        }

        if (root.TryGetProperty("requiresOpenaiAuth", out var reqAuthEl) && reqAuthEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            requiresOpenaiAuth = reqAuthEl.GetBoolean();
        }

        return (accountType, email, planType, requiresOpenaiAuth);
    }

    /// <summary>
    /// Normalizes an <c>account/rateLimits/read</c> JSON response into a list of <see cref="LimitBucket"/>,
    /// primary limit ID, and available reset credits.
    /// Backward-compatible overload.
    /// </summary>
    public static (string? PrimaryLimitId, IReadOnlyList<LimitBucket> Limits, int? ResetCreditsAvailable) ParseRateLimits(JsonElement root)
    {
        var (primaryId, limits, credits, _) = ParseRateLimitsDetail(root);
        return (primaryId, limits, credits);
    }

    /// <summary>
    /// Normalizes an <c>account/rateLimits/read</c> JSON response into a list of <see cref="LimitBucket"/>,
    /// primary limit ID, available reset credits count, and detailed reset credits breakdown.
    /// </summary>
    public static (string? PrimaryLimitId, IReadOnlyList<LimitBucket> Limits, int? ResetCreditsAvailable, RateLimitResetCredits? ResetCreditsDetail) ParseRateLimitsDetail(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return (null, [], null, null);

        var buckets = new Dictionary<string, LimitBucket>(StringComparer.OrdinalIgnoreCase);

        // 1. Preferred: rateLimitsByLimitId
        if (root.TryGetProperty("rateLimitsByLimitId", out var multiBucketEl) && multiBucketEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in multiBucketEl.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var bucket = ParseBucket(prop.Value, fallbackLimitId: prop.Name);
                if (bucket is not null)
                    buckets[bucket.LimitId] = bucket;
            }
        }

        string? primaryLimitId = buckets.ContainsKey("codex") ? "codex" : null;

        // 2. Legacy fallback: rateLimits
        if (primaryLimitId is null && root.TryGetProperty("rateLimits", out var legacyEl) && legacyEl.ValueKind == JsonValueKind.Object)
        {
            var fallback = ParseBucket(legacyEl, fallbackLimitId: "codex");
            if (fallback is not null)
            {
                buckets[fallback.LimitId] = fallback;
                primaryLimitId = fallback.LimitId;
            }
        }

        if (primaryLimitId is null && buckets.Count > 0)
        {
            primaryLimitId = buckets.Keys.First();
        }

        // 3. Reset credits detail and available count
        var resetCreditsDetail = ParseResetCreditsDetail(root);
        int? resetCreditsAvailable = resetCreditsDetail?.AvailableCount;

        if (resetCreditsAvailable is null)
        {
            JsonElement? credEl = null;
            if (root.TryGetProperty("rateLimitsByLimitId", out var rlbli) && rlbli.ValueKind == JsonValueKind.Object &&
                rlbli.TryGetProperty("codex", out var cdxEl) && cdxEl.ValueKind == JsonValueKind.Object &&
                cdxEl.TryGetProperty("credits", out var c1))
            {
                credEl = c1;
            }
            else if (root.TryGetProperty("rateLimits", out var rl) && rl.ValueKind == JsonValueKind.Object &&
                     rl.TryGetProperty("credits", out var c2))
            {
                credEl = c2;
            }

            if (credEl.HasValue && credEl.Value.ValueKind == JsonValueKind.Object)
            {
                if (credEl.Value.TryGetProperty("balance", out var balEl))
                {
                    if (balEl.ValueKind == JsonValueKind.Number && balEl.TryGetInt32(out var balInt))
                        resetCreditsAvailable = balInt;
                    else if (balEl.ValueKind == JsonValueKind.String && int.TryParse(balEl.GetString(), out var balParsed))
                        resetCreditsAvailable = balParsed;
                }
            }
        }

        return (primaryLimitId, buckets.Values.ToList(), resetCreditsAvailable, resetCreditsDetail);
    }

    /// <summary>
    /// Parses detailed reset credits from <c>rateLimitResetCredits</c>.
    /// Tolerates missing fields, string or numeric timestamps (seconds or milliseconds),
    /// strips IDs for privacy, and respects availableCount as authoritative.
    /// </summary>
    public static RateLimitResetCredits? ParseResetCreditsDetail(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (!root.TryGetProperty("rateLimitResetCredits", out var creditsEl) || creditsEl.ValueKind != JsonValueKind.Object)
            return null;

        int? availableCount = null;
        if (creditsEl.TryGetProperty("availableCount", out var countEl) &&
            countEl.ValueKind == JsonValueKind.Number &&
            countEl.TryGetInt32(out var count) &&
            count >= 0)
        {
            availableCount = count;
        }

        if (!availableCount.HasValue)
            return null;

        List<RateLimitResetCredit>? creditsList = null;
        if (creditsEl.TryGetProperty("credits", out var listEl) && listEl.ValueKind == JsonValueKind.Array)
        {
            creditsList = new List<RateLimitResetCredit>();
            foreach (var item in listEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                string resetType = item.TryGetProperty("resetType", out var rt) && rt.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(rt.GetString())
                    ? rt.GetString()!
                    : "codexRateLimits";

                string status = item.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(st.GetString())
                    ? st.GetString()!
                    : "available";

                DateTimeOffset? grantedAt = ParseTimestamp(item, "grantedAt");
                DateTimeOffset? expiresAt = ParseTimestamp(item, "expiresAt");

                string? title = item.TryGetProperty("title", out var ttl) && ttl.ValueKind == JsonValueKind.String
                    ? ttl.GetString()
                    : null;

                string? description = item.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String
                    ? desc.GetString()
                    : null;

                creditsList.Add(new RateLimitResetCredit(
                    Id: null,
                    ResetType: resetType,
                    Status: status,
                    GrantedAt: grantedAt,
                    ExpiresAt: expiresAt,
                    Title: title,
                    Description: description));
            }
        }

        return new RateLimitResetCredits(availableCount.Value, creditsList);
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var num))
        {
            if (num <= 0) return null;
            try
            {
                return num > 100_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(num)
                    : DateTimeOffset.FromUnixTimeSeconds(num);
            }
            catch
            {
                return null;
            }
        }

        if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString()))
        {
            var str = el.GetString()!;
            if (long.TryParse(str, out var parsedLong) && parsedLong > 0)
            {
                try
                {
                    return parsedLong > 100_000_000_000L
                        ? DateTimeOffset.FromUnixTimeMilliseconds(parsedLong)
                        : DateTimeOffset.FromUnixTimeSeconds(parsedLong);
                }
                catch { }
            }

            if (DateTimeOffset.TryParse(str, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedDto))
            {
                return parsedDto;
            }
        }

        return null;
    }

    private static LimitBucket? ParseBucket(JsonElement bucketEl, string fallbackLimitId)
    {
        string limitId = fallbackLimitId;
        if (bucketEl.TryGetProperty("limitId", out var idEl) && idEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(idEl.GetString()))
        {
            limitId = idEl.GetString()!;
        }

        if (string.IsNullOrWhiteSpace(limitId))
            return null;

        string? limitName = bucketEl.TryGetProperty("limitName", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString()
            : null;

        string? planType = bucketEl.TryGetProperty("planType", out var planEl) && planEl.ValueKind == JsonValueKind.String
            ? planEl.GetString()
            : null;

        string? reachedType = bucketEl.TryGetProperty("rateLimitReachedType", out var reachEl) && reachEl.ValueKind == JsonValueKind.String
            ? reachEl.GetString()
            : null;

        var windows = new List<UsageWindow>();
        foreach (var slot in KnownWindowSlots)
        {
            if (bucketEl.TryGetProperty(slot, out var windowEl) && windowEl.ValueKind == JsonValueKind.Object)
            {
                windows.Add(ParseWindow(slot, windowEl));
            }
        }

        return new LimitBucket(limitId, limitName, windows, reachedType, planType);
    }

    private static UsageWindow ParseWindow(string slot, JsonElement windowEl)
    {
        int? durationMinutes = null;
        if (windowEl.TryGetProperty("windowDurationMins", out var durEl) && durEl.ValueKind == JsonValueKind.Number && durEl.TryGetInt32(out var d) && d > 0)
        {
            durationMinutes = d;
        }

        double? usedPercent = null;
        if (windowEl.TryGetProperty("usedPercent", out var usedEl) && usedEl.ValueKind == JsonValueKind.Number && usedEl.TryGetDouble(out var u) && !double.IsNaN(u) && !double.IsInfinity(u))
        {
            usedPercent = u;
        }

        DateTimeOffset? resetsAt = null;
        if (windowEl.TryGetProperty("resetsAt", out var resetEl) && resetEl.ValueKind == JsonValueKind.Number && resetEl.TryGetInt64(out var r) && r > 0)
        {
            try
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(r);
            }
            catch (ArgumentOutOfRangeException)
            {
                resetsAt = null;
            }
        }

        double? remainingPercent = usedPercent.HasValue ? 100.0 - usedPercent.Value : null;
        string displayLabel = FormatDuration(durationMinutes);

        return new UsageWindow(
            Slot: slot,
            DurationMinutes: durationMinutes,
            DisplayLabel: displayLabel,
            UsedPercent: usedPercent,
            RemainingPercent: remainingPercent,
            ResetsAt: resetsAt);
    }

    /// <summary>
    /// Formats window duration in minutes to human-readable label (e.g. 300 -> "5h", 10080 -> "7d").
    /// </summary>
    public static string FormatDuration(int? durationMinutes)
    {
        if (!durationMinutes.HasValue || durationMinutes.Value <= 0)
            return "unknown";

        var m = durationMinutes.Value;
        if (m == 300) return "5h";
        if (m == 10080) return "7d";
        if (m % 1440 == 0) return $"{m / 1440}d";
        if (m % 60 == 0) return $"{m / 60}h";
        return $"{m}m";
    }

    /// <summary>
    /// Parses an <c>account/usage/read</c> response payload into an <see cref="AccountActivitySnapshot"/>.
    /// Decoupled from upstream schema changes: tolerates missing/null fields, unknown properties,
    /// large Int64 numbers, out-of-order dates, and unrecognized date formats without failing.
    /// </summary>
    public static AccountActivitySnapshot ParseAccountActivity(
        Guid profileId,
        JsonElement root,
        DateTimeOffset? observedAt = null)
    {
        var timestamp = observedAt ?? DateTimeOffset.UtcNow;
        if (root.ValueKind != JsonValueKind.Object)
            return new AccountActivitySnapshot(profileId, timestamp, null, Array.Empty<DailyTokenUsage>());

        AccountTokenUsageSummary? summary = null;
        if (root.TryGetProperty("summary", out var summaryEl) && summaryEl.ValueKind == JsonValueKind.Object)
        {
            var lifetime = GetInt64OrNull(summaryEl, "lifetimeTokens");
            var peakDaily = GetInt64OrNull(summaryEl, "peakDailyTokens");
            var longestTurn = GetInt64OrNull(summaryEl, "longestRunningTurnSec");
            var currentStreak = GetInt64OrNull(summaryEl, "currentStreakDays");
            var longestStreak = GetInt64OrNull(summaryEl, "longestStreakDays");

            summary = new AccountTokenUsageSummary(
                LifetimeTokens: lifetime,
                PeakDailyTokens: peakDaily,
                LongestRunningTurnSeconds: longestTurn,
                CurrentStreakDays: currentStreak,
                LongestStreakDays: longestStreak);
        }

        var buckets = new List<DailyTokenUsage>();
        if (root.TryGetProperty("dailyUsageBuckets", out var bucketsEl) && bucketsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in bucketsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                string? startDateRaw = null;
                if (item.TryGetProperty("startDate", out var dateEl) && dateEl.ValueKind == JsonValueKind.String)
                {
                    startDateRaw = dateEl.GetString();
                }

                if (string.IsNullOrWhiteSpace(startDateRaw))
                    continue;

                var tokens = GetInt64OrNull(item, "tokens") ?? 0;
                if (tokens < 0)
                    tokens = 0;

                DateOnly? parsedDate = null;
                if (DateOnly.TryParseExact(startDateRaw, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
                {
                    parsedDate = d;
                }

                buckets.Add(new DailyTokenUsage(
                    StartDateRaw: startDateRaw,
                    ParsedDate: parsedDate,
                    Tokens: tokens));
            }
        }

        // Deterministic date ordering (chronological ascending)
        var sortedBuckets = buckets
            .OrderBy(b => b.ParsedDate ?? DateOnly.MinValue)
            .ThenBy(b => b.StartDateRaw, StringComparer.Ordinal)
            .ToList();

        return new AccountActivitySnapshot(
            ProfileId: profileId,
            ObservedAt: timestamp,
            Summary: summary,
            DailyBuckets: sortedBuckets);
    }

    private static long? GetInt64OrNull(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var val))
            return val;

        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var parsedVal))
            return parsedVal;

        return null;
    }
}
