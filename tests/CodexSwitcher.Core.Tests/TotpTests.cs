using CodexSwitcher.Core.Security;

namespace CodexSwitcher.Core.Tests;

/// <summary>
/// Verifica o gerador TOTP (2FA) contra os vetores oficiais da RFC 6238 (semente ASCII
/// "12345678901234567890" = Base32 "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"), além do parse tolerante da
/// chave colada pelo usuário e de URIs otpauth://.
/// </summary>
public sealed class TotpTests
{
    private const string Rfc6238Base32 = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Compute_MatchesRfc6238_Sha1SixDigits(long unixSeconds, string expected)
    {
        Assert.True(Totp.TryParse(Rfc6238Base32, out var secret, out _));

        var code = secret!.Compute(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        Assert.Equal(expected, code.Code);
    }

    [Fact]
    public void Compute_SecondsRemaining_CountsDownWithinPeriod()
    {
        Totp.TryParse(Rfc6238Base32, out var secret, out _);

        // 10s dentro de uma janela de 30s => faltam 20s.
        var code = secret!.Compute(DateTimeOffset.FromUnixTimeSeconds(10));

        Assert.Equal(30, code.Period);
        Assert.Equal(20, code.SecondsRemaining);
    }

    [Fact]
    public void TryParse_AcceptsSpacesAndLowercase()
    {
        var messy = "gezd gnbv gy3t qojq gezd gnbv gy3t qojq";

        Assert.True(Totp.TryParse(messy, out var secret, out var error));
        Assert.Null(error);
        Assert.Equal("287082", secret!.Compute(DateTimeOffset.FromUnixTimeSeconds(59)).Code);
    }

    [Fact]
    public void TryParse_ParsesOtpAuthUriWithParameters()
    {
        var uri = $"otpauth://totp/ACME:alice@example.com?secret={Rfc6238Base32}&issuer=ACME&digits=6&period=30&algorithm=SHA1";

        Assert.True(Totp.TryParse(uri, out var secret, out _));
        Assert.Equal("ACME", secret!.Issuer);
        Assert.Equal(6, secret.Digits);
        Assert.Equal(30, secret.Period);
        Assert.Equal(TotpAlgorithm.Sha1, secret.Algorithm);
        Assert.Equal("287082", secret.Compute(DateTimeOffset.FromUnixTimeSeconds(59)).Code);
    }

    [Fact]
    public void TryParse_InvalidSecret_Fails()
    {
        Assert.False(Totp.TryParse("not valid base32 !!!", out var secret, out var error));
        Assert.Null(secret);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_Empty_Fails()
    {
        Assert.False(Totp.TryParse("   ", out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Compute_MatchesRfc6238_Sha1EightDigits(long unixSeconds, string expected)
    {
        var uri = $"otpauth://totp/Test?secret={Rfc6238Base32}&digits=8&algorithm=SHA1";
        Assert.True(Totp.TryParse(uri, out var secret, out _));

        var code = secret!.Compute(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        Assert.Equal(expected, code.Code);
    }

    private const string Rfc6238Sha256Base32 = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZA";

    [Theory]
    [InlineData(59L, "46119246")]
    [InlineData(1111111109L, "68084774")]
    [InlineData(1111111111L, "67062674")]
    [InlineData(1234567890L, "91819424")]
    [InlineData(2000000000L, "90698825")]
    [InlineData(20000000000L, "77737706")]
    public void Compute_MatchesRfc6238_Sha256EightDigits(long unixSeconds, string expected)
    {
        var uri = $"otpauth://totp/Test?secret={Rfc6238Sha256Base32}&digits=8&algorithm=SHA256";
        Assert.True(Totp.TryParse(uri, out var secret, out _));

        var code = secret!.Compute(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        Assert.Equal(expected, code.Code);
    }

    private const string Rfc6238Sha512Base32 = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNA";

    [Theory]
    [InlineData(59L, "90693936")]
    [InlineData(1111111109L, "25091201")]
    [InlineData(1111111111L, "99943326")]
    [InlineData(1234567890L, "93441116")]
    [InlineData(2000000000L, "38618901")]
    [InlineData(20000000000L, "47863826")]
    public void Compute_MatchesRfc6238_Sha512EightDigits(long unixSeconds, string expected)
    {
        var uri = $"otpauth://totp/Test?secret={Rfc6238Sha512Base32}&digits=8&algorithm=SHA512";
        Assert.True(Totp.TryParse(uri, out var secret, out _));

        var code = secret!.Compute(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        Assert.Equal(expected, code.Code);
    }

    [Fact]
    public void TryParse_HotpUri_FailsWithUnsupportedError()
    {
        var hotpUri = $"otpauth://hotp/Test?secret={Rfc6238Base32}&counter=1";
        Assert.False(Totp.TryParse(hotpUri, out var secret, out var error));
        Assert.Null(secret);
        Assert.NotNull(error);
        Assert.Contains("TOTP", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Formatted_GroupsEightDigits()
    {
        var code = new TotpCode("94287082", 20, 30);
        Assert.Equal("9428 7082", code.Formatted);
    }

    [Fact]
    public void Formatted_GroupsSixDigits()
    {
        var code = new TotpCode("287082", 20, 30);
        Assert.Equal("287 082", code.Formatted);
    }
}
