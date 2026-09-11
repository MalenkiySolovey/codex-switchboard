using System.Net;
using System.Text;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Infra.Providers.Inspection;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class ProviderInspectionDecompositionTests
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
        string host = "api.provider.test")
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
                    Status = CapabilityStatus.Supported,
                    Strategy = "openai-models",
                    Request = new RecipeRequest { Method = "GET", Path = "/models", Auth = "bearer" }
                },
                Balance = new ProviderCapabilityRecipe
                {
                    Status = CapabilityStatus.Supported,
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
    public void ProviderProbePlanner_PlanProbe_CreatesDeterministicPlanWithoutSecrets()
    {
        var planner = new ProviderProbePlanner();
        var descriptor = CreateTestDescriptor();
        var recipe = descriptor.Capabilities!.Models!;

        var (valid, plan, error) = planner.PlanProbe(descriptor, recipe, "https://api.provider.test/v1", "models");

        Assert.True(valid);
        Assert.Null(error);
        Assert.NotNull(plan);
        Assert.Equal("GET", plan.Method);
        Assert.Equal("bearer", plan.AuthScheme);
        Assert.Equal("https://api.provider.test/v1/models", plan.TargetUri.ToString());
        Assert.Contains("api.provider.test", plan.TrustedHosts);

        // Verify plan record has NO properties containing sensitive strings
        var properties = typeof(ProviderProbePlan).GetProperties();
        Assert.DoesNotContain(properties, p => p.Name.Contains("Key", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, p => p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public void ProviderProbePlanner_DisallowedMethods_RejectedWithSecurityViolation(string method)
    {
        var planner = new ProviderProbePlanner();
        var descriptor = CreateTestDescriptor();
        var recipe = new ProviderCapabilityRecipe
        {
            Request = new RecipeRequest { Method = method, Path = "/test" }
        };

        var (valid, plan, error) = planner.PlanProbe(descriptor, recipe, "https://api.provider.test/v1", "models");

        Assert.False(valid);
        Assert.Null(plan);
        Assert.NotNull(error);
        Assert.Contains("Security violation", error);
        Assert.Contains(method, error);
    }

    [Fact]
    public void ProviderProbePlanner_InsecureHttpRemoteHost_RejectedWithSecurityViolation()
    {
        var planner = new ProviderProbePlanner();
        var descriptor = CreateTestDescriptor(host: "remote-insecure.test");
        var recipe = descriptor.Capabilities!.Models!;

        var (valid, plan, error) = planner.PlanProbe(descriptor, recipe, "http://remote-insecure.test/v1", "models");

        Assert.False(valid);
        Assert.Null(plan);
        Assert.NotNull(error);
        Assert.Contains("Insecure HTTP is prohibited", error);
    }

    [Theory]
    [InlineData("http://localhost:8080/v1", "localhost")]
    [InlineData("http://127.0.0.1:8080/v1", "127.0.0.1")]
    public void ProviderProbePlanner_InsecureHttpOnLoopback_PermittedForTesting(string baseUrl, string host)
    {
        var planner = new ProviderProbePlanner();
        var descriptor = CreateTestDescriptor(host: host);
        descriptor.TrustedHosts = [host];
        var recipe = descriptor.Capabilities!.Models!;

        var (valid, plan, error) = planner.PlanProbe(descriptor, recipe, baseUrl, "models");

        Assert.True(valid);
        Assert.Null(error);
        Assert.NotNull(plan);
    }

    [Fact]
    public void ProviderProbePlanner_UntrustedHost_RejectedWithSecurityViolation()
    {
        var planner = new ProviderProbePlanner();
        var descriptor = CreateTestDescriptor(host: "trusted.api.test");
        var recipe = descriptor.Capabilities!.Models!;

        // Probe against foreign host not in trustedHosts
        var (valid, plan, error) = planner.PlanProbe(descriptor, recipe, "https://foreign-host.com/v1", "models");

        Assert.False(valid);
        Assert.Null(plan);
        Assert.NotNull(error);
        Assert.Contains("not in the trustedHosts whitelist", error);
    }

    [Fact]
    public void ProviderProbePlanner_GenericUnknown_OnlyPlansModels()
    {
        var planner = new ProviderProbePlanner();
        var (valid, plan, syntheticDescriptor, error) = planner.PlanGenericUnknownModelsProbe("https://custom.provider.test/v1");

        Assert.True(valid);
        Assert.Null(error);
        Assert.NotNull(plan);
        Assert.NotNull(syntheticDescriptor);
        Assert.Equal("GET", plan.Method);
        Assert.EndsWith("/models", plan.TargetUri.AbsolutePath);
        Assert.Equal(CapabilityStatus.Unknown, syntheticDescriptor.Capabilities?.Balance?.Status);
        Assert.Equal(CapabilityStatus.Unknown, syntheticDescriptor.Capabilities?.Usage?.Status);
    }

    [Fact]
    public async Task SafeProviderHttpTransport_SanitizesException_RedactsRawApiKey()
    {
        var secretKey = "sk-super-secret-production-token";
        var mock = new MockHttpHandler
        {
            ResponseFactory = req => throw new HttpRequestException($"Connection error while accessing host with token {secretKey}")
        };

        using var transport = new SafeProviderHttpTransport(new HttpClient(mock));
        var planner = new ProviderProbePlanner();
        var descriptor = CreateTestDescriptor();
        var (valid, plan, _) = planner.PlanProbe(descriptor, descriptor.Capabilities!.Models!, "https://api.provider.test/v1", "models");

        var response = await transport.SendProbeAsync(plan!, descriptor, secretKey);

        Assert.False(response.Success);
        Assert.NotNull(response.Error);
        // Assert the raw key is NOT present in the error message
        Assert.DoesNotContain(secretKey, response.Error);
        Assert.Contains("[REDACTED_API_KEY]", response.Error);
    }

    [Fact]
    public async Task SafeProviderHttpTransport_RedirectToUntrustedHost_BlocksAndStripsCredentials()
    {
        var mock = new MockHttpHandler
        {
            ResponseFactory = req =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Redirect);
                resp.Headers.Location = new Uri("https://evil-exfiltration.example/capture");
                return resp;
            }
        };

        using var transport = new SafeProviderHttpTransport(new HttpClient(mock));
        var planner = new ProviderProbePlanner();
        var descriptor = CreateTestDescriptor();
        var (valid, plan, _) = planner.PlanProbe(descriptor, descriptor.Capabilities!.Models!, "https://api.provider.test/v1", "models");

        var response = await transport.SendProbeAsync(plan!, descriptor, "sk-secret-key-999");

        Assert.False(response.Success);
        Assert.NotNull(response.Error);
        Assert.Contains("Security violation: Redirect to untrusted host", response.Error);
    }

    [Fact]
    public void ProviderProbeResponseMapper_ExtractsModelsAndBalanceFacts()
    {
        var mapper = new ProviderProbeResponseMapper();

        // 1. Models mapping
        using var modelsDoc = System.Text.Json.JsonDocument.Parse("{ \"data\": [{ \"id\": \"gpt-5.6-sol\" }, { \"id\": \"codex-mini\" }] }");
        var modelsProbeResponse = new ProbeHttpResponse(true, modelsDoc.RootElement.Clone(), 200, null);

        var (success, models, error) = mapper.MapModelsResponse(modelsProbeResponse, "/data");
        Assert.True(success);
        Assert.Null(error);
        Assert.Contains("gpt-5.6-sol", models!);
        Assert.Contains("codex-mini", models!);

        // 2. Balance mapping
        using var balanceDoc = System.Text.Json.JsonDocument.Parse("{ \"balance\": 250.75, \"used\": 49.25, \"limit\": 300.0, \"currency\": \"EUR\" }");
        var balanceProbeResponse = new ProbeHttpResponse(true, balanceDoc.RootElement.Clone(), 200, null);
        var mapping = new RecipeResponseMapping
        {
            Balance = new JsonFieldMapping { Pointer = "/balance", Type = "decimal" },
            Used = new JsonFieldMapping { Pointer = "/used", Type = "decimal" },
            Limit = new JsonFieldMapping { Pointer = "/limit", Type = "decimal" },
            Currency = new CurrencyMapping { Pointer = "/currency" }
        };

        decimal? balance = null;
        decimal? used = null;
        decimal? limit = null;
        decimal? remaining = null;
        string? currency = null;

        mapper.MapBalanceAndUsageResponse(balanceProbeResponse, mapping, ref balance, ref used, ref limit, ref remaining, ref currency);

        Assert.Equal(250.75m, balance);
        Assert.Equal(49.25m, used);
        Assert.Equal(300.0m, limit);
        Assert.Equal("EUR", currency);
    }

    [Fact]
    public async Task DeclarativeProviderInspector_CoordinatesDecomposedComponentsCleanly()
    {
        var mock = new MockHttpHandler
        {
            ResponseFactory = req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/models"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{ \"data\": [{ \"id\": \"model-orchestrated\" }] }", Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{ \"data\": { \"balance\": 77.5, \"used\": 22.5, \"limit\": 100 } }", Encoding.UTF8, "application/json")
                };
            }
        };

        var planner = new ProviderProbePlanner();
        using var transport = new SafeProviderHttpTransport(new HttpClient(mock));
        var mapper = new ProviderProbeResponseMapper();
        using var inspector = new DeclarativeProviderInspector(planner, transport, mapper);

        var descriptor = CreateTestDescriptor();
        var snapshot = await inspector.InspectAsync(descriptor, "https://api.provider.test/v1", "sk-orchestration-key");

        Assert.Equal(HealthStatus.Valid, snapshot.ConnectionStatus);
        Assert.Contains("model-orchestrated", snapshot.Models);
        Assert.Equal(77.5m, snapshot.Balance);
        Assert.Equal(22.5m, snapshot.UsedCredits);
        Assert.Equal(100m, snapshot.CreditLimit);
        Assert.Equal("USD", snapshot.Currency);
    }
}
