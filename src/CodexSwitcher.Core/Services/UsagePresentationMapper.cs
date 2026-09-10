using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Services;

/// <summary>
/// Maps raw rate-limit models and cache entries to normalized presentation models.
/// Orders windows by duration ascending (shortest first) and resolves the 9 presentation states.
/// </summary>
public static class UsagePresentationMapper
{
    public static AccountUsageState MapFromCache(
        UsageCacheEntry? entry,
        Guid profileId,
        DateTimeOffset now,
        bool pt = false)
    {
        if (entry is null || (entry.Snapshot is null && entry.Status == UsageStatus.Unknown && entry.LastError is null && entry.Activity is null))
            return AccountUsageState.NeverLoadedState(profileId);

        return MapInternal(
            profileId: profileId,
            snapshot: entry.Snapshot,
            status: entry.Status,
            lastError: entry.LastError,
            isStale: entry.IsStale,
            credentialConflict: entry.CredentialConflict,
            conflictReason: entry.EffectiveConflictReason,
            activity: entry.Activity,
            activityAvailability: entry.ActivityAvailability,
            observedAt: entry.ObservedAt,
            pt: pt);
    }

    public static AccountUsageState MapFromFetchResult(
        UsageFetchResult result,
        Guid profileId,
        DateTimeOffset now,
        bool pt = false)
    {
        if (result.Snapshot is null && result.Activity is null)
        {
            return MapInternal(
                profileId: profileId,
                snapshot: null,
                status: result.Status,
                lastError: result.Error,
                isStale: false,
                credentialConflict: false,
                conflictReason: CredentialConflictReason.None,
                activity: null,
                activityAvailability: result.ActivityAvailability,
                observedAt: now,
                pt: pt);
        }

        var status = result.Snapshot?.Status ?? result.Status;
        if (status == UsageStatus.Unknown && result.Snapshot is not null)
            status = UsageStatus.Healthy;

        return MapInternal(
            profileId: profileId,
            snapshot: result.Snapshot,
            status: status,
            lastError: result.Snapshot?.LastError ?? result.Error,
            isStale: false,
            credentialConflict: false,
            conflictReason: CredentialConflictReason.None,
            activity: result.Activity,
            activityAvailability: result.ActivityAvailability,
            observedAt: result.Snapshot?.ObservedAt ?? result.Activity?.ObservedAt ?? now,
            pt: pt);
    }

    private static AccountUsageState MapInternal(
        Guid profileId,
        RateLimitsSnapshot? snapshot,
        UsageStatus status,
        ErrorInfo? lastError,
        bool isStale,
        bool credentialConflict,
        CredentialConflictReason conflictReason,
        AccountActivitySnapshot? activity,
        AccountActivityAvailability activityAvailability,
        DateTimeOffset? observedAt,
        bool pt)
    {
        var rawWindows = ExtractWindows(snapshot);
        var windows = MapWindows(rawWindows);

        var (visualState, noticeMessage) = ResolveVisualStateAndNotice(
            status: status,
            snapshot: snapshot,
            windows: windows,
            isStale: isStale,
            credentialConflict: credentialConflict,
            conflictReason: conflictReason,
            lastError: lastError,
            pt: pt);

        return new AccountUsageState(
            ProfileId: profileId,
            Status: status,
            VisualState: visualState,
            IsStale: isStale,
            IsRefreshing: false,
            PlanType: snapshot?.PlanType,
            ResetCreditsAvailable: snapshot?.ResetCreditsAvailable,
            ObservedAt: observedAt,
            Windows: windows,
            NoticeMessage: noticeMessage,
            LastError: lastError,
            CredentialConflict: credentialConflict,
            ConflictReason: conflictReason,
            Activity: activity,
            ActivityAvailability: activityAvailability,
            ResetCreditsDetail: snapshot?.ResetCreditsDetail);
    }

    private static IReadOnlyList<UsageWindow> ExtractWindows(RateLimitsSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Limits.Count == 0)
            return Array.Empty<UsageWindow>();

        var primary = snapshot.Limits.FirstOrDefault(b => b.LimitId == (snapshot.PrimaryLimitId ?? "codex"))
                      ?? snapshot.Limits.FirstOrDefault();

        if (primary is not null && primary.Windows.Count > 0)
            return primary.Windows;

        return snapshot.Limits.SelectMany(b => b.Windows).ToList();
    }

    public static IReadOnlyList<UsageWindowModel> MapWindows(IReadOnlyList<UsageWindow> rawWindows)
    {
        if (rawWindows.Count == 0)
            return Array.Empty<UsageWindowModel>();

        // Sort by duration ascending: shortest duration first (e.g. 45m < 300m < 10080m); null durations at the end.
        var sorted = rawWindows
            .OrderBy(w => w.DurationMinutes ?? int.MaxValue)
            .ThenBy(w => w.Slot);

        var result = new List<UsageWindowModel>();
        foreach (var w in sorted)
        {
            double? used = w.UsedPercent is { } u ? Math.Clamp(u, 0.0, 100.0) : null;
            double? remaining = w.RemainingPercent is { } r ? Math.Clamp(r, 0.0, 100.0) : null;

            bool isExhausted = (remaining is { } rem && rem <= 0.0) || (used is { } us && us >= 100.0);
            bool isLowQuota = !isExhausted && remaining is { } rVal && rVal < 20.0;

            var label = !string.IsNullOrWhiteSpace(w.DisplayLabel)
                ? w.DisplayLabel
                : (w.DurationMinutes is { } mins ? $"{mins}m" : "Window");

            result.Add(new UsageWindowModel(
                Slot: w.Slot,
                DurationMinutes: w.DurationMinutes,
                DisplayLabel: label,
                UsedPercent: used,
                RemainingPercent: remaining,
                ResetsAt: w.ResetsAt,
                IsExhausted: isExhausted,
                IsLowQuota: isLowQuota));
        }

        return result;
    }

    /// <summary>
    /// Selects up to two representative windows for compact card presentation using the Phase 8 deterministic priority:
    /// 1. Exhausted / rate-critical windows (IsExhausted)
    /// 2. Low-quota / actionable windows (IsLowQuota)
    /// 3. Shorter duration first (DurationMinutes)
    /// 4. Original stable list order as tie-breaker.
    /// The selected windows are returned ordered by duration ascending for natural display.
    /// </summary>
    public static IReadOnlyList<UsageWindowModel> SelectCompactWindows(
        IReadOnlyList<UsageWindowModel> windows,
        out int additionalCount)
    {
        if (windows.Count == 0)
        {
            additionalCount = 0;
            return Array.Empty<UsageWindowModel>();
        }

        if (windows.Count <= 2)
        {
            additionalCount = 0;
            return windows;
        }

        var indexed = windows.Select((w, idx) => (Window: w, Index: idx)).ToList();
        var top2 = indexed
            .OrderByDescending(x => x.Window.IsExhausted)
            .ThenByDescending(x => x.Window.IsLowQuota)
            .ThenBy(x => x.Window.DurationMinutes ?? int.MaxValue)
            .ThenBy(x => x.Index)
            .Take(2)
            .Select(x => x.Window)
            .OrderBy(w => w.DurationMinutes ?? int.MaxValue)
            .ToList();

        additionalCount = windows.Count - top2.Count;
        return top2;
    }

    private static (UsageVisualState State, string? Notice) ResolveVisualStateAndNotice(
        UsageStatus status,
        RateLimitsSnapshot? snapshot,
        IReadOnlyList<UsageWindowModel> windows,
        bool isStale,
        bool credentialConflict,
        CredentialConflictReason conflictReason,
        ErrorInfo? lastError,
        bool pt)
    {
        if (credentialConflict || conflictReason != CredentialConflictReason.None)
        {
            string notice = conflictReason switch
            {
                CredentialConflictReason.ActiveCredentialMutation =>
                    pt ? "Conflito de credencial: a conta ativa foi rotacionada no sandbox; gravação suprimida."
                       : "Credential conflict: active profile credentials rotated in sandbox; active slot write suppressed.",
                CredentialConflictReason.InactiveGenerationChanged =>
                    pt ? "Conflito de credencial: o perfil foi modificado concorrentemente no cofre durante a rotação."
                       : "Credential conflict: profile was modified concurrently in vault during rotation.",
                _ =>
                    pt ? "Conflito de credencial detectado para este perfil."
                       : "Credential conflict detected for this profile."
            };

            return (UsageVisualState.CredentialConflict, notice);
        }

        if (status == UsageStatus.AuthRequired)
        {
            return (
                UsageVisualState.AuthRequired,
                pt ? "Autenticação necessária. Conecte-se novamente para renovar a cota."
                   : "Authentication required. Sign in again to refresh quota.");
        }

        if (status == UsageStatus.UnsupportedAccountType)
        {
            return (
                UsageVisualState.UnsupportedAccountType,
                pt ? "Monitoramento de cota não é suportado para este tipo de credencial."
                   : "Usage monitoring is not supported for this credential type.");
        }

        if (status is UsageStatus.ProcessDown or UsageStatus.BackingOff)
        {
            return (
                UsageVisualState.ProcessDown,
                pt ? "Processo do Codex indisponível. Tentativa automática em andamento."
                   : "Codex app-server process unavailable. Automatic retry scheduled.");
        }

        if (status == UsageStatus.Error)
        {
            var err = lastError?.Message;
            var msg = !string.IsNullOrWhiteSpace(err)
                ? (pt ? $"Erro: {err}" : $"Error: {err}")
                : (pt ? "Falha ao obter dados de uso." : "Failed to retrieve usage data.");
            return (UsageVisualState.ProcessDown, msg);
        }

        // Authoritative backend rate limit check (do NOT infer solely from window.UsedPercent >= 100)
        bool hasBucketRateLimit = snapshot?.Limits.Any(b => !string.IsNullOrWhiteSpace(b.RateLimitReachedType)) == true;

        if (status == UsageStatus.RateLimited || hasBucketRateLimit)
        {
            return (UsageVisualState.RateLimited, null);
        }

        if (isStale)
        {
            return (UsageVisualState.StaleCache, null);
        }

        if (status == UsageStatus.Healthy || (snapshot is not null && status == UsageStatus.Unknown))
        {
            return (UsageVisualState.Fresh, null);
        }

        return (UsageVisualState.NeverLoaded, null);
    }
}
