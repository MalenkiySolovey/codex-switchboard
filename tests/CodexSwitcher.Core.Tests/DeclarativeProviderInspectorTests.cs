using System.Net;
using System.Text;
using CodexSwitcher.Core.Catalog;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class DeclarativeProviderInspectorTests
{
    private sealed class MockHttpHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public List<HttpRequestMessage> RequestHistory { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            RequestHistory.Add(request);

            if (ResponseFactory != null)
                return Task.FromResult(ResponseFactory(request));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{ \"data\": [] }", Encoding.UTF8, "application/json")
            });
        }
    }

    private static ProviderDescriptor CreateTestDescriptor(
        string id = "test-provider",
        string host = "api.provider.test",
        CapabilityStatus modelsStatus = CapabilityStatus.Supported,
        CapabilityStatus balanceStatus = CapabilityStatus.Supported)
    {
        return new ProviderDescriptor
        {
            Id = id,
            DisplayName = "Test Provider",
            Match = new ProviderMatchCriteria { ExactHosts = new List<string> { host } },
            TrustedHosts = new List<string> { host },
            Routes = new List<ProviderRoute>
            {
                new() { Id = "primary", BaseUrl = $"https://{host}/v1", DisplayName = "Primary" }
            },
            Capabilities = new ProviderCapabilities
            {
                Models = new ProviderCapabilityRecipe
                {
                    Status = modelsStatus,
                    Strategy = "openai-models",
                    Request = new RecipeRequest { Method = "GET", Path = "/models", Auth = "bearer" }
                },
                Balance = new ProviderCapabilityRecipe
                {
                    Status = balanceStatus,
                    Strategy = "json-http",
                    Request = new RecipeRequest { Method = "GET", Path = "/balance", Auth = "bearer" },
                    Response = new RecipeResponseMapping
                    {
                        Balance = new JsonFieldMapping { Pointer = "/data/balance", Type = "decimal" },
                        Used = new JsonFieldMapping { Pointer = "/data/used", Type = "decimal" },
                        Limit = new JsonFieldMapping { Pointer = "/data/limit", Type = "decimal" },
                        Currency = new CurrencyMapping { Literal = "USD" }
                    }
                },
                Usage = new ProviderCapabilityRecipe { Status = CapabilityStatus.Unknown, Strategy = "unknown" }
            }
        };
    }

    [Fact]
    public async Task InspectAsync_BearerAuth_AttachesAuthorizationHeader()
    {
        var mock = new MockHttpHandler
        {
            ResponseFactory = req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/models"))
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{ \"data\": [{ \"id\": \"gpt-5.6-sol\" }] }", Encoding.UTF8, "application/json")
                    };

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{ \"data\": { \"balance\": 100.5, \"used\": 12.3, \"limit\": 200 } }", Encoding.UTF8, "application/json")
                };
            }
        };

        var inspector = new DeclarativeProviderInspector(new HttpClient(mock));
        var descriptor = CreateTestDescriptor();

        var snapshot = await inspector.InspectAsync(descriptor, "https://api.provider.test/v1", "sk-secret-12345");

        Assert.Equal(HealthStatus.Valid, snapshot.ConnectionStatus);
        Assert.Contains("gpt-5.6-sol", snapshot.Models);
        Assert.Equal(100.5m, snapshot.Balance);
        Assert.Equal(12.3m, snapshot.UsedCredits);
        Assert.Equal(200m, snapshot.CreditLimit);
        Assert.Equal("USD", snapshot.Currency);

        // Verify Bearer header was sent
        Assert.NotNull(mock.LastRequest?.Headers.Authorization);
        Assert.Equal("Bearer", mock.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("sk-secret-12345", mock.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task InspectAsync_ApiKeyHeader_AttachesCustomHeader()
    {
        var mock = new MockHttpHandler
        {
            ResponseFactory = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{ \"data\": { \"balance\": 50.0 } }", Encoding.UTF8, "application/json")
            }
        };

        var inspector = new DeclarativeProviderInspector(new HttpClient(mock));
        var descriptor = CreateTestDescriptor(modelsStatus: CapabilityStatus.Unknown);
        descriptor.Capabilities.Balance.Request!.Auth = "x-api-key";

        var snapshot = await inspector.InspectAsync(descriptor, "https://api.provider.test/v1", "my-x-key");

        Assert.NotNull(mock.LastRequest);
        Assert.True(mock.LastRequest.Headers.Contains("x-api-key"));
        Assert.Equal("my-x-key", mock.LastRequest.Headers.GetValues("x-api-key").First());
    }

    [Fact]
    public async Task InspectAsync_PostMethod_IsRejectedWithSecurityViolation()
    {
        var mock = new MockHttpHandler();
        var inspector = new DeclarativeProviderInspector(new HttpClient(mock));
        var descriptor = CreateTestDescriptor(modelsStatus: CapabilityStatus.Unknown);
        descriptor.Capabilities.Balance.Request!.Method = "POST";

        var snapshot = await inspector.InspectAsync(descriptor, "https://api.provider.test/v1", "test-key");

        Assert.NotNull(snapshot.Error);
        Assert.Contains("Security violation", snapshot.Error);
        Assert.Contains("POST", snapshot.Error);
        // Verify no request was actually sent
        Assert.Null(mock.LastRequest);
    }

    [Fact]
    public async Task InspectAsync_TargetHostOutsideTrustedHosts_IsBlocked()
    {
        var mock = new MockHttpHandler();
        var inspector = new DeclarativeProviderInspector(new HttpClient(mock));
        var descriptor = CreateTestDescriptor(host: "api.provider.test");
        // Untrusted foreign host in route
        var snapshot = await inspector.InspectAsync(descriptor, "https://attacker.com/v1", "test-key");

        Assert.NotNull(snapshot.Error);
        Assert.Contains("trustedHosts", snapshot.Error);
        Assert.Null(mock.LastRequest);
    }

    [Fact]
    public async Task InspectAsync_RedirectToUntrustedHost_StripsCredentialsAndBlocks()
    {
        var mock = new MockHttpHandler
        {
            ResponseFactory = req =>
            {
                // First request redirects to an untrusted host
                var resp = new HttpResponseMessage(HttpStatusCode.Redirect);
                resp.Headers.Location = new Uri("https://evil-exfiltration.example/capture");
                return resp;
            }
        };

        var inspector = new DeclarativeProviderInspector(new HttpClient(mock));
        var descriptor = CreateTestDescriptor(modelsStatus: CapabilityStatus.Unknown);

        var snapshot = await inspector.InspectAsync(descriptor, "https://api.provider.test/v1", "sk-sensitive-token");

        Assert.NotNull(snapshot.Error);
        Assert.Contains("Redirect to untrusted host", snapshot.Error);
        Assert.Contains("blocked", snapshot.Error);
    }

    [Fact]
    public async Task InspectAsync_RedirectToTrustedHost_Succeeds()
    {
        var mock = new MockHttpHandler
        {
            ResponseFactory = req =>
            {
                if (req.RequestUri!.AbsolutePath == "/v1/balance")
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                    redirect.Headers.Location = new Uri("https://api.provider.test/v2/balance");
                    return redirect;
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{ \"data\": { \"balance\": 99.0 } }", Encoding.UTF8, "application/json")
                };
            }
        };

        var inspector = new DeclarativeProviderInspector(new HttpClient(mock));
        var descriptor = CreateTestDescriptor(modelsStatus: CapabilityStatus.Unknown);

        var snapshot = await inspector.InspectAsync(descriptor, "https://api.provider.test/v1", "test-token");

        Assert.Equal(HealthStatus.Valid, snapshot.ConnectionStatus);
        Assert.Equal(99.0m, snapshot.Balance);
    }

    [Fact]
    public async Task InspectAsync_ResponseSizeLimitExceeded_RejectsGracefully()
    {
        var largeString = new string('A', CatalogSecurityConstants.MaxProbeResponseSizeBytes + 1024);
        var mock = new MockHttpHandler
        {
            ResponseFactory = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(largeString, Encoding.UTF8, "application/json")
            }
        };

        var inspector = new DeclarativeProviderInspector(new HttpClient(mock));
        var descriptor = CreateTestDescriptor(modelsStatus: CapabilityStatus.Unknown);

        var snapshot = await inspector.InspectAsync(descriptor, "https://api.provider.test/v1", "test-token");

        Assert.NotNull(snapshot.Error);
        Assert.Contains("exceeded maximum allowed size", snapshot.Error);
    }

    [Fact]
    public async Task InspectAsync_HttpErrorCodes_HandledGracefully()
    {
        foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, (HttpStatusCode)429, HttpStatusCode.InternalServerError })
        {
            var mock = new MockHttpHandler
            {
                ResponseFactory = req => new HttpResponseMessage(code)
            };

            var inspector = new DeclarativeProviderInspector(new HttpClient(mock));
            var descriptor = CreateTestDescriptor(modelsStatus: CapabilityStatus.Unknown);

            var snapshot = await inspector.InspectAsync(descriptor, "https://api.provider.test/v1", "test-token");

            Assert.NotNull(snapshot.Error);
            Assert.Contains(((int)code).ToString(), snapshot.Error);
        }
    }

    [Fact]
    public async Task InspectAsync_MalformedJson_ReturnsErrorGracefully()
    {
        var mock = new MockHttpHandler
        {
            ResponseFactory = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not-valid-json<<<", Encoding.UTF8, "application/json")
            }
        };

        var inspector = new DeclarativeProviderInspector(new HttpClient(mock));
        var descriptor = CreateTestDescriptor(modelsStatus: CapabilityStatus.Unknown);

        var snapshot = await inspector.InspectAsync(descriptor, "https://api.provider.test/v1", "test-token");

        Assert.NotNull(snapshot.Error);
        Assert.Contains("Malformed JSON", snapshot.Error);
    }

    [Fact]
    public async Task InspectGenericUnknown_ProbesOnlyModels_DoesNotGuessBalance()
    {
        var mock = new MockHttpHandler
        {
            ResponseFactory = req =>
            {
                Assert.EndsWith("/models", req.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{ \"data\": [{ \"id\": \"unknown-custom-model\" }] }", Encoding.UTF8, "application/json")
                };
            }
        };

        var inspector = new DeclarativeProviderInspector(new HttpClient(mock));
        var snapshot = await inspector.InspectGenericUnknownAsync("https://custom-proxy.internal/v1", "sk-custom-key");

        Assert.Equal(HealthStatus.Valid, snapshot.ConnectionStatus);
        Assert.Contains("unknown-custom-model", snapshot.Models);
        Assert.Null(snapshot.Balance);
        Assert.Equal(CapabilityStatus.Unknown, snapshot.Capabilities["balance"]);
        Assert.Equal(CapabilityStatus.Unknown, snapshot.Capabilities["usage"]);
    }
}
