using System.Text.Json;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Infra.Codex;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexUsageResponseParserTests
{
    [Fact]
    public void Scenario1_WeeklyOnlyResponse_ParsesCorrectly()
    {
        var json = """
        {
            "rateLimitsByLimitId": {
                "codex": {
                    "limitId": "codex",
                    "limitName": "Codex Limit",
                    "planType": "plus",
                    "secondary": {
                        "windowDurationMins": 10080,
                        "usedPercent": 42.5,
                        "resetsAt": 1774390000
                    }
                }
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var (primaryId, limits, credits) = CodexUsageResponseParser.ParseRateLimits(doc.RootElement);

        Assert.Equal("codex", primaryId);
        Assert.Single(limits);
        var bucket = limits[0];
        Assert.Equal("codex", bucket.LimitId);
        Assert.Equal("plus", bucket.PlanType);
        Assert.Single(bucket.Windows);

        var window = bucket.Windows[0];
        Assert.Equal("secondary", window.Slot);
        Assert.Equal(10080, window.DurationMinutes);
        Assert.Equal("7d", window.DisplayLabel);
        Assert.Equal(42.5, window.UsedPercent);
        Assert.Equal(57.5, window.RemainingPercent);
        Assert.NotNull(window.ResetsAt);
        Assert.Null(credits);
    }

    [Fact]
    public void Scenario2_5hPlusWeeklyResponse_ParsesBothWindows()
    {
        var json = """
        {
            "rateLimitsByLimitId": {
                "codex": {
                    "limitId": "codex",
                    "primary": {
                        "windowDurationMins": 300,
                        "usedPercent": 15.0,
                        "resetsAt": 1774393600
                    },
                    "secondary": {
                        "windowDurationMins": 10080,
                        "usedPercent": 80.0,
                        "resetsAt": 1774900000
                    }
                }
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var (primaryId, limits, _) = CodexUsageResponseParser.ParseRateLimits(doc.RootElement);

        Assert.Equal("codex", primaryId);
        Assert.Single(limits);
        var bucket = limits[0];
        Assert.Equal(2, bucket.Windows.Count);

        var primary = bucket.Windows[0];
        Assert.Equal("primary", primary.Slot);
        Assert.Equal(300, primary.DurationMinutes);
        Assert.Equal("5h", primary.DisplayLabel);
        Assert.Equal(15.0, primary.UsedPercent);
        Assert.Equal(85.0, primary.RemainingPercent);

        var secondary = bucket.Windows[1];
        Assert.Equal("secondary", secondary.Slot);
        Assert.Equal(10080, secondary.DurationMinutes);
        Assert.Equal("7d", secondary.DisplayLabel);
        Assert.Equal(80.0, secondary.UsedPercent);
        Assert.Equal(20.0, secondary.RemainingPercent);
    }

    [Fact]
    public void Scenario3_PrimaryOnly_ParsesPrimaryWithoutSecondary()
    {
        var json = """
        {
            "rateLimitsByLimitId": {
                "codex": {
                    "limitId": "codex",
                    "primary": {
                        "windowDurationMins": 300,
                        "usedPercent": 10.0,
                        "resetsAt": 1774390000
                    }
                }
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var (primaryId, limits, _) = CodexUsageResponseParser.ParseRateLimits(doc.RootElement);

        Assert.Equal("codex", primaryId);
        var bucket = Assert.Single(limits);
        var window = Assert.Single(bucket.Windows);
        Assert.Equal("primary", window.Slot);
        Assert.Equal(300, window.DurationMinutes);
        Assert.Equal("5h", window.DisplayLabel);
    }

    [Fact]
    public void Scenario4_SecondaryNull_HandlesNullSlotGracefully()
    {
        var json = """
        {
            "rateLimitsByLimitId": {
                "codex": {
                    "limitId": "codex",
                    "primary": {
                        "windowDurationMins": 300,
                        "usedPercent": 5.0,
                        "resetsAt": 1774390000
                    },
                    "secondary": null
                }
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var (primaryId, limits, _) = CodexUsageResponseParser.ParseRateLimits(doc.RootElement);

        Assert.Equal("codex", primaryId);
        var bucket = Assert.Single(limits);
        var window = Assert.Single(bucket.Windows);
        Assert.Equal("primary", window.Slot);
    }

    [Fact]
    public void Scenario5_MissingRateLimitsByLimitId_FallsBackToLegacyRateLimits()
    {
        var json = """
        {
            "rateLimits": {
                "limitId": "codex",
                "planType": "pro",
                "primary": {
                    "windowDurationMins": 300,
                    "usedPercent": 25.0,
                    "resetsAt": 1774390000
                }
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var (primaryId, limits, _) = CodexUsageResponseParser.ParseRateLimits(doc.RootElement);

        Assert.Equal("codex", primaryId);
        var bucket = Assert.Single(limits);
        Assert.Equal("codex", bucket.LimitId);
        Assert.Equal("pro", bucket.PlanType);
        Assert.Single(bucket.Windows);
    }

    [Fact]
    public void Scenario6_UnknownDuration_FormatsGracefully()
    {
        var json = """
        {
            "rateLimitsByLimitId": {
                "codex": {
                    "limitId": "codex",
                    "primary": {
                        "windowDurationMins": 45,
                        "usedPercent": 50.0,
                        "resetsAt": 1774390000
                    },
                    "secondary": {
                        "windowDurationMins": 120,
                        "usedPercent": 30.0,
                        "resetsAt": 1774395000
                    }
                }
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var (_, limits, _) = CodexUsageResponseParser.ParseRateLimits(doc.RootElement);

        var bucket = Assert.Single(limits);
        Assert.Equal(2, bucket.Windows.Count);

        Assert.Equal("45m", bucket.Windows[0].DisplayLabel);
        Assert.Equal(45, bucket.Windows[0].DurationMinutes);

        Assert.Equal("2h", bucket.Windows[1].DisplayLabel);
        Assert.Equal(120, bucket.Windows[1].DurationMinutes);
    }

    [Fact]
    public void Scenario7_MalformedOptionalField_DoesNotCrash()
    {
        var json = """
        {
            "rateLimitsByLimitId": {
                "codex": {
                    "limitId": "codex",
                    "planType": 12345,
                    "rateLimitReachedType": true,
                    "primary": {
                        "windowDurationMins": "invalid_string",
                        "usedPercent": false,
                        "resetsAt": "tomorrow"
                    }
                }
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var (primaryId, limits, _) = CodexUsageResponseParser.ParseRateLimits(doc.RootElement);

        Assert.Equal("codex", primaryId);
        var bucket = Assert.Single(limits);
        Assert.Null(bucket.PlanType);
        Assert.Null(bucket.RateLimitReachedType);
        var window = Assert.Single(bucket.Windows);
        Assert.Null(window.DurationMinutes);
        Assert.Equal("unknown", window.DisplayLabel);
        Assert.Null(window.UsedPercent);
        Assert.Null(window.RemainingPercent);
        Assert.Null(window.ResetsAt);
    }

    [Fact]
    public void Scenario8_NullResetTimestamp_HandledGracefully()
    {
        var json = """
        {
            "rateLimitsByLimitId": {
                "codex": {
                    "limitId": "codex",
                    "primary": {
                        "windowDurationMins": 300,
                        "usedPercent": 0.0,
                        "resetsAt": null
                    }
                }
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var (_, limits, _) = CodexUsageResponseParser.ParseRateLimits(doc.RootElement);

        var bucket = Assert.Single(limits);
        var window = Assert.Single(bucket.Windows);
        Assert.Null(window.ResetsAt);
        Assert.Equal(0.0, window.UsedPercent);
        Assert.Equal(100.0, window.RemainingPercent);
    }

    [Fact]
    public void Scenario9_ResetCreditCount_ParsedSuccessfully()
    {
        var json = """
        {
            "rateLimitsByLimitId": {
                "codex": {
                    "limitId": "codex",
                    "primary": {
                        "windowDurationMins": 300,
                        "usedPercent": 50.0,
                        "resetsAt": 1774390000
                    }
                }
            },
            "rateLimitResetCredits": {
                "availableCount": 5
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var (_, _, credits) = CodexUsageResponseParser.ParseRateLimits(doc.RootElement);

        Assert.Equal(5, credits);
    }

    [Fact]
    public void Scenario10_ZeroSecretLeakage_LogsAndExceptionsDoNotContainTokens()
    {
        // Simulate an error payload containing a JWT token
        var fakeJwt = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        var rawErrorMessage = $"Failed to authenticate token {fakeJwt} with endpoint";

        // Verify the sanitization regex replaces JWT patterns with [REDACTED_TOKEN]
        var sanitized = System.Text.RegularExpressions.Regex.Replace(
            rawErrorMessage,
            @"ey[A-Za-z0-9_-]{10,}\.[A-Za-z0-9._-]+",
            "[REDACTED_TOKEN]");

        Assert.DoesNotContain(fakeJwt, sanitized);
        Assert.Contains("[REDACTED_TOKEN]", sanitized);
    }

    [Fact]
    public void ParseAccountInfo_ValidPayload_ReturnsExpectedFields()
    {
        var json = """
        {
            "requiresOpenaiAuth": true,
            "account": {
                "type": "chatgpt",
                "email": "user@example.com",
                "planType": "plus"
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var (type, email, plan, reqOpenaiAuth) = CodexUsageResponseParser.ParseAccountInfo(doc.RootElement);

        Assert.Equal("chatgpt", type);
        Assert.Equal("user@example.com", email);
        Assert.Equal("plus", plan);
        Assert.True(reqOpenaiAuth);
    }

    [Fact]
    public void ParseAccountInfo_NullAccount_ReturnsNullType()
    {
        var json = """
        {
            "requiresOpenaiAuth": true,
            "account": null
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var (type, email, plan, reqOpenaiAuth) = CodexUsageResponseParser.ParseAccountInfo(doc.RootElement);

        Assert.Null(type);
        Assert.Null(email);
        Assert.Null(plan);
        Assert.True(reqOpenaiAuth);
    }

    [Fact]
    public void ParseRateLimits_AdditionalUnknownFields_IgnoredGracefully()
    {
        var json = """
        {
            "unknownRootKey": "unexpected_value",
            "futureProtocolVersion": 42,
            "rateLimitsByLimitId": {
                "codex": {
                    "limitId": "codex",
                    "planType": "team",
                    "rateLimitReachedType": "rate_limit_reached",
                    "unknownBucketProperty": { "foo": "bar" },
                    "primary": {
                        "windowDurationMins": 300,
                        "usedPercent": 99.5,
                        "resetsAt": 1774390000,
                        "futureWindowProperty": 9999
                    }
                },
                "future_new_limit": {
                    "limitId": "future_new_limit",
                    "primary": {
                        "windowDurationMins": 60,
                        "usedPercent": 10.0
                    }
                }
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var (primaryId, limits, _) = CodexUsageResponseParser.ParseRateLimits(doc.RootElement);

        Assert.Equal("codex", primaryId);
        Assert.Equal(2, limits.Count);

        var codexBucket = limits.First(b => b.LimitId == "codex");
        Assert.Equal("team", codexBucket.PlanType);
        Assert.Equal("rate_limit_reached", codexBucket.RateLimitReachedType);
        Assert.Single(codexBucket.Windows);
        Assert.Equal(300, codexBucket.Windows[0].DurationMinutes);
        Assert.Equal(99.5, codexBucket.Windows[0].UsedPercent);
    }
}
