using System.Text;
using System.Text.Json;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Support;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Codex;
using CodexSwitcher.Infra.Io;

namespace CodexSwitcher.Core.Tests;

public sealed class ResetCreditsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly PhysicalFileSystem _fs = new();
    private readonly string _cachePath;
    private readonly Guid _profileId = Guid.NewGuid();

    public ResetCreditsTests()
    {
        _cachePath = _dir.Combine("usage-cache.json");
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    [Fact]
    public void Test01_Parser_NumericUnixTimestampSeconds()
    {
        var json = """
        {
            "rateLimitResetCredits": {
                "availableCount": 1,
                "credits": [
                    {
                        "id": "cred-123",
                        "resetType": "codexRateLimits",
                        "status": "available",
                        "grantedAt": 1755536520,
                        "expiresAt": 1755536520,
                        "title": "Monthly bonus",
                        "description": "Bonus reset credit"
                    }
                ]
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var parsed = CodexUsageResponseParser.ParseResetCreditsDetail(doc.RootElement);

        Assert.NotNull(parsed);
        Assert.Equal(1, parsed.AvailableCount);
        Assert.NotNull(parsed.Credits);
        Assert.Single(parsed.Credits);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1755536520), parsed.Credits[0].GrantedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1755536520), parsed.Credits[0].ExpiresAt);
        Assert.Equal("Monthly bonus", parsed.Credits[0].Title);
        Assert.Equal("Bonus reset credit", parsed.Credits[0].Description);
    }

    [Fact]
    public void Test02_Parser_NumericUnixTimestampMilliseconds()
    {
        var json = """
        {
            "rateLimitResetCredits": {
                "availableCount": 1,
                "credits": [
                    {
                        "grantedAt": 1755536520000,
                        "expiresAt": 1755536520000
                    }
                ]
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var parsed = CodexUsageResponseParser.ParseResetCreditsDetail(doc.RootElement);

        Assert.NotNull(parsed);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1755536520000), parsed.Credits![0].ExpiresAt);
    }

    [Fact]
    public void Test03_Parser_Iso8601StringTimestamp()
    {
        var json = """
        {
            "rateLimitResetCredits": {
                "availableCount": 1,
                "credits": [
                    {
                        "grantedAt": "2026-09-15T12:00:00Z",
                        "expiresAt": "2026-09-22T12:00:00Z"
                    }
                ]
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var parsed = CodexUsageResponseParser.ParseResetCreditsDetail(doc.RootElement);

        Assert.NotNull(parsed);
        Assert.Equal(DateTimeOffset.Parse("2026-09-22T12:00:00Z"), parsed.Credits![0].ExpiresAt);
    }

    [Fact]
    public void Test04_Parser_MissingOrNullCreditsArray_RetainsAvailableCount()
    {
        var json = """
        {
            "rateLimitResetCredits": {
                "availableCount": 3
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var parsed = CodexUsageResponseParser.ParseResetCreditsDetail(doc.RootElement);

        Assert.NotNull(parsed);
        Assert.Equal(3, parsed.AvailableCount);
        Assert.Null(parsed.Credits);
        Assert.False(parsed.HasDetailedCredits);
        Assert.Equal(3, parsed.UnreportedCount);
    }

    [Fact]
    public void Test05_Parser_AvailableCountAuthoritative_WhenCreditsEmpty()
    {
        var json = """
        {
            "rateLimitResetCredits": {
                "availableCount": 5,
                "credits": []
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var parsed = CodexUsageResponseParser.ParseResetCreditsDetail(doc.RootElement);

        Assert.NotNull(parsed);
        Assert.Equal(5, parsed.AvailableCount);
        Assert.True(parsed.HasDetailedCredits);
        Assert.Empty(parsed.Credits!);
        Assert.Equal(5, parsed.UnreportedCount);
    }

    [Fact]
    public void Test06_Parser_AvailableCountLessThanCreditsCount()
    {
        var json = """
        {
            "rateLimitResetCredits": {
                "availableCount": 1,
                "credits": [
                    { "title": "Credit 1" },
                    { "title": "Credit 2" }
                ]
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var parsed = CodexUsageResponseParser.ParseResetCreditsDetail(doc.RootElement);

        Assert.NotNull(parsed);
        Assert.Equal(1, parsed.AvailableCount);
        Assert.Equal(2, parsed.Credits!.Count);
        Assert.Equal(0, parsed.UnreportedCount);
    }

    [Fact]
    public void Test07_Parser_AvailableCountGreaterThanCreditsCount()
    {
        var json = """
        {
            "rateLimitResetCredits": {
                "availableCount": 3,
                "credits": [
                    { "title": "Credit 1" }
                ]
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var parsed = CodexUsageResponseParser.ParseResetCreditsDetail(doc.RootElement);

        Assert.NotNull(parsed);
        Assert.Equal(3, parsed.AvailableCount);
        Assert.Single(parsed.Credits!);
        Assert.Equal(2, parsed.UnreportedCount);
    }

    [Fact]
    public void Test08_Parser_StatusValues_NormalizedCorrectly()
    {
        var json = """
        {
            "rateLimitResetCredits": {
                "availableCount": 4,
                "credits": [
                    { "status": "available" },
                    { "status": "redeeming" },
                    { "status": "redeemed" },
                    { "status": "custom_other" }
                ]
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var parsed = CodexUsageResponseParser.ParseResetCreditsDetail(doc.RootElement);

        Assert.NotNull(parsed);
        Assert.Equal(ResetCreditStatus.Available, parsed.Credits![0].NormalizedStatus);
        Assert.Equal(ResetCreditStatus.Redeeming, parsed.Credits[1].NormalizedStatus);
        Assert.Equal(ResetCreditStatus.Redeemed, parsed.Credits[2].NormalizedStatus);
        Assert.Equal(ResetCreditStatus.Unknown, parsed.Credits[3].NormalizedStatus);
    }

    [Fact]
    public void Test09_Parser_ResetType_TolerantParsing()
    {
        var json = """
        {
            "rateLimitResetCredits": {
                "availableCount": 2,
                "credits": [
                    { "resetType": "customType" },
                    { }
                ]
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var parsed = CodexUsageResponseParser.ParseResetCreditsDetail(doc.RootElement);

        Assert.NotNull(parsed);
        Assert.Equal("customType", parsed.Credits![0].ResetType);
        Assert.Equal("codexRateLimits", parsed.Credits[1].ResetType);
    }

    [Fact]
    public void Test10_Parser_IdNeverRetainedOrExposed()
    {
        var json = """
        {
            "rateLimitResetCredits": {
                "availableCount": 1,
                "credits": [
                    { "id": "secret-server-id-99999", "title": "My Credit" }
                ]
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var parsed = CodexUsageResponseParser.ParseResetCreditsDetail(doc.RootElement);

        Assert.NotNull(parsed);
        Assert.Null(parsed.Credits![0].Id);
    }

    [Fact]
    public void Test11_Urgency_GreaterThan7Days_Normal()
    {
        var now = DateTimeOffset.UtcNow;
        var expiry = now.AddDays(8);
        Assert.Equal(CreditExpiryUrgency.Normal, UsageCountdownFormatter.GetExpiryUrgency(expiry, now));
    }

    [Fact]
    public void Test12_Urgency_LessThanOrEqualTo7Days_Warning()
    {
        var now = DateTimeOffset.UtcNow;
        var expiry = now.AddDays(5);
        Assert.Equal(CreditExpiryUrgency.Warning, UsageCountdownFormatter.GetExpiryUrgency(expiry, now));
    }

    [Fact]
    public void Test13_Urgency_LessThanOrEqualTo24Hours_Critical()
    {
        var now = DateTimeOffset.UtcNow;
        var expiry = now.AddHours(18);
        Assert.Equal(CreditExpiryUrgency.Critical, UsageCountdownFormatter.GetExpiryUrgency(expiry, now));
    }

    [Fact]
    public void Test14_Urgency_PassedTimestamp_Expired()
    {
        var now = DateTimeOffset.UtcNow;
        var expiry = now.AddMinutes(-30);
        Assert.Equal(CreditExpiryUrgency.Expired, UsageCountdownFormatter.GetExpiryUrgency(expiry, now));
    }

    [Fact]
    public void Test15_PassedExpiry_DoesNotDecrementAvailableCount()
    {
        var now = DateTimeOffset.UtcNow;
        var expiredCredit = new RateLimitResetCredit(null, "codexRateLimits", "available", now.AddDays(-10), now.AddMinutes(-5), "Credit", null);
        var credits = new RateLimitResetCredits(2, [expiredCredit]);

        // Model must preserve server's authoritative AvailableCount of 2 even if a row has passed expiry
        Assert.Equal(2, credits.AvailableCount);
    }

    [Fact]
    public async Task Test16_CachePersistence_SchemaVersion3_SavesAndRestoresCreditDetails()
    {
        var cache = new UsageCache(_fs, _cachePath);
        var now = DateTimeOffset.UtcNow;
        var credit = new RateLimitResetCredit(null, "codexRateLimits", "available", now.AddDays(-1), now.AddDays(10), "Bonus", "Description");
        var snapshot = new RateLimitsSnapshot(
            _profileId, now, "codex", [], 1, "plus", "test@example.com", UsageStatus.Healthy,
            ResetCreditsDetail: new RateLimitResetCredits(1, [credit]));

        cache.Set(_profileId, snapshot, UsageStatus.Healthy);
        await cache.SaveAsync();

        var loadedCache = new UsageCache(_fs, _cachePath);
        await loadedCache.LoadAsync();

        var entry = loadedCache.Get(_profileId);
        Assert.NotNull(entry);
        Assert.NotNull(entry.Snapshot);
        Assert.NotNull(entry.Snapshot.ResetCreditsDetail);
        Assert.Equal(1, entry.Snapshot.ResetCreditsDetail.AvailableCount);
        var credits = entry.Snapshot.ResetCreditsDetail.Credits!;
        Assert.Single(credits);
        Assert.Equal("Bonus", credits[0].Title);
    }

    [Fact]
    public async Task Test17_CacheMigration_FromSchemaVersion1And2_PreservesCompatibility()
    {
        var v2Json = """
        {
            "schemaVersion": 2,
            "profiles": {
                "77777777-7777-7777-7777-777777777777": {
                    "profileId": "77777777-7777-7777-7777-777777777777",
                    "status": "Healthy",
                    "observedAt": "2026-09-09T20:00:00Z",
                    "isStale": true,
                    "credentialConflict": false,
                    "conflictReason": "None",
                    "snapshot": {
                        "profileId": "77777777-7777-7777-7777-777777777777",
                        "observedAt": "2026-09-09T20:00:00Z",
                        "primaryLimitId": "codex",
                        "limits": [],
                        "resetCreditsAvailable": 2,
                        "planType": "plus",
                        "status": "Healthy"
                    }
                }
            }
        }
        """;
        _fs.WriteAllBytesAtomic(_cachePath, Encoding.UTF8.GetBytes(v2Json));

        var cache = new UsageCache(_fs, _cachePath);
        await cache.LoadAsync();

        var entry = cache.Get(Guid.Parse("77777777-7777-7777-7777-777777777777"));
        Assert.NotNull(entry);
        Assert.Equal(2, entry.Snapshot!.ResetCreditsAvailable);
        Assert.Null(entry.Snapshot.ResetCreditsDetail);

        await cache.SaveAsync();
        var bytes = _fs.ReadAllBytes(_cachePath);
        using var doc = JsonDocument.Parse(bytes);
        Assert.Equal(UsageCache.CurrentSchemaVersion, doc.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void Test18_CorruptedCreditData_ToleratedGracefully()
    {
        var json = """
        {
            "rateLimitResetCredits": "invalid_string_not_object"
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var parsed = CodexUsageResponseParser.ParseResetCreditsDetail(doc.RootElement);
        Assert.Null(parsed);
    }

    [Fact]
    public void Test19_Sorting_NearestExpiryFirst()
    {
        var now = DateTimeOffset.UtcNow;
        var c1 = new RateLimitResetCredit(null, "codex", "available", null, now.AddDays(10), "Far", null);
        var c2 = new RateLimitResetCredit(null, "codex", "available", null, now.AddDays(2), "Near", null);
        var c3 = new RateLimitResetCredit(null, "codex", "available", null, null, "NoExpiry", null);

        var model = new RateLimitResetCredits(3, [c1, c2, c3]);
        var sorted = model.SortedCredits;

        Assert.Equal("Near", sorted[0].Title);
        Assert.Equal("Far", sorted[1].Title);
        Assert.Equal("NoExpiry", sorted[2].Title);
    }

    [Fact]
    public void Test20_Formatting_EnAndPtStrings()
    {
        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var expiry = now.AddDays(3);

        var en = UsageCountdownFormatter.FormatCreditExpiry(expiry, now, pt: false);
        Assert.StartsWith("Expires in 3d (", en);

        var pt = UsageCountdownFormatter.FormatCreditExpiry(expiry, now, pt: true);
        Assert.StartsWith("Expira em 3d (", pt);

        var passedEn = UsageCountdownFormatter.FormatCreditExpiry(now.AddHours(-1), now, pt: false);
        Assert.Equal("Expiry time passed — refresh to verify", passedEn);

        var passedPt = UsageCountdownFormatter.FormatCreditExpiry(now.AddHours(-1), now, pt: true);
        Assert.Equal("Validade expirada — renove para verificar", passedPt);
    }
}
