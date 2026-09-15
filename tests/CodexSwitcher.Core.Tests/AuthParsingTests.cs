using CodexSwitcher.Core.Tests.TestSupport;

namespace CodexSwitcher.Core.Tests;

/// <summary>
/// Verifica a leitura mínima do auth.json (blob opaco) e a decodificação local do id_token,
/// sem rede e sem depender do schema completo. Ver BUSINESS_RULES.md §2.3, pontos 11 e 12.
/// </summary>
public sealed class AuthParsingTests
{
    [Fact]
    public void AuthJsonReader_ExtractsLightFields()
    {
        var bytes = Sample.AuthJson(accountId: "acct_42", authMode: "chatgpt",
            lastRefresh: "2026-06-25T10:00:00Z");

        var info = AuthJsonReader.TryRead(bytes);

        Assert.NotNull(info);
        Assert.Equal("chatgpt", info!.AuthMode);
        Assert.Equal("acct_42", info.AccountId);
        Assert.NotNull(info.IdToken);
        Assert.Equal(new DateTimeOffset(2026, 6, 25, 10, 0, 0, TimeSpan.Zero), info.LastRefresh);
    }

    [Fact]
    public void AuthJsonReader_InvalidJson_ReturnsNull()
    {
        var bytes = "not json at all"u8.ToArray();
        Assert.Null(AuthJsonReader.TryRead(bytes));
    }

    [Fact]
    public void JwtClaimsReader_DecodesSubAndEmail_Locally()
    {
        var token = Sample.Jwt(sub: "user-xyz", email: "someone@openai-test.com", plan: "plus");

        var claims = JwtClaimsReader.Read(token);

        Assert.Equal("user-xyz", claims.Sub);
        Assert.Equal("someone@openai-test.com", claims.Email);
        Assert.Equal("plus", claims.PlanType);
    }

    [Fact]
    public void JwtClaimsReader_Malformed_ReturnsEmptyClaims_DoesNotThrow()
    {
        var claims = JwtClaimsReader.Read("garbage.token");
        Assert.Null(claims.Sub);
        Assert.Null(claims.Email);
    }

    [Fact]
    public void Identify_CombinesFileAndClaims()
    {
        var token = Sample.Jwt(sub: "sub-1", email: "a@b.com");
        var bytes = Sample.AuthJson(idToken: token, accountId: "acct_1");

        var (file, claims) = AuthJsonReader.Identify(bytes);

        Assert.Equal("acct_1", file!.AccountId);
        Assert.Equal("sub-1", claims.Sub);
        Assert.Equal("a@b.com", claims.Email);
    }
}
