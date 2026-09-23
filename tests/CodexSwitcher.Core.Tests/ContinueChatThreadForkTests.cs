using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Infra.Codex.Threads;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class ContinueChatThreadForkTests
{
    private sealed class MockAppServerClient : ICodexAppServerClient
    {
        public bool IsRunning { get; set; } = true;
        public List<(string Method, object? Parameters)> Requests { get; } = [];
        public Func<string, object?, JsonElement>? Handler { get; set; }

        public Task<JsonElement> RequestAsync(string method, object? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Requests.Add((method, parameters));
            if (Handler != null)
            {
                return Task.FromResult(Handler(method, parameters));
            }

            var defaultDoc = JsonDocument.Parse("{}");
            return Task.FromResult(defaultDoc.RootElement);
        }

        public Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ExactExample_DeepSeekFreeToNonFree_PropagatesExactTargetModel()
    {
        // Section O regression test:
        // SOURCE: provider = switchboard_provider_a, model = deepseek-v4.1-flash:free, thread = thread-source-1
        // TARGET: provider = switchboard_provider_b, model = deepseek-v4.1-flash
        var client = new MockAppServerClient
        {
            Handler = (method, _) =>
            {
                if (method == "thread/fork")
                {
                    return JsonDocument.Parse(@"{
                        ""modelProvider"": ""switchboard_provider_b"",
                        ""model"": ""deepseek-v4.1-flash"",
                        ""thread"": { ""id"": ""thread-forked-new-2"" }
                    }").RootElement;
                }
                if (method == "thread/read")
                {
                    return JsonDocument.Parse(@"{
                        ""thread"": { ""id"": ""thread-forked-new-2"" }
                    }").RootElement;
                }
                return JsonDocument.Parse("{}").RootElement;
            }
        };

        var service = new CodexThreadHandoffService(() => Task.FromResult<ICodexAppServerClient>(client));
        var result = await service.ForkThreadAsync(
            "thread-source-1",
            "switchboard_provider_b",
            "deepseek-v4.1-flash");

        Assert.Equal("thread-forked-new-2", result.ForkedThreadId);
        Assert.Equal("thread-source-1", result.SourceThreadId);
        Assert.Equal("switchboard_provider_b", result.TargetModelProvider);
        Assert.Equal("deepseek-v4.1-flash", result.TargetModel);

        var forkReq = client.Requests.First(r => r.Method == "thread/fork");
        var json = JsonSerializer.Serialize(forkReq.Parameters);
        Assert.Contains("thread-source-1", json);
        Assert.Contains("switchboard_provider_b", json);
        Assert.Contains("deepseek-v4.1-flash", json);
        Assert.DoesNotContain("deepseek-v4.1-flash:free", json);
    }

    [Fact]
    public async Task ReverseExample_DeepSeekNonFreeToFree_PropagatesExactTargetModel()
    {
        // Section P reverse test:
        // SOURCE: deepseek-v4.1-flash
        // TARGET: deepseek-v4.1-flash:free
        var client = new MockAppServerClient
        {
            Handler = (method, _) =>
            {
                if (method == "thread/fork")
                {
                    return JsonDocument.Parse(@"{
                        ""modelProvider"": ""switchboard_provider_free"",
                        ""model"": ""deepseek-v4.1-flash:free"",
                        ""thread"": { ""id"": ""thread-forked-free-3"" }
                    }").RootElement;
                }
                if (method == "thread/read")
                {
                    return JsonDocument.Parse(@"{
                        ""thread"": { ""id"": ""thread-forked-free-3"" }
                    }").RootElement;
                }
                return JsonDocument.Parse("{}").RootElement;
            }
        };

        var service = new CodexThreadHandoffService(() => Task.FromResult<ICodexAppServerClient>(client));
        var result = await service.ForkThreadAsync(
            "thread-source-paid",
            "switchboard_provider_free",
            "deepseek-v4.1-flash:free");

        Assert.Equal("thread-forked-free-3", result.ForkedThreadId);
        Assert.Equal("switchboard_provider_free", result.TargetModelProvider);
        Assert.Equal("deepseek-v4.1-flash:free", result.TargetModel);

        var forkReq = client.Requests.First(r => r.Method == "thread/fork");
        var json = JsonSerializer.Serialize(forkReq.Parameters);
        Assert.Contains("deepseek-v4.1-flash:free", json);
    }

    [Fact]
    public async Task SameSlugDifferentProvider_PropagatesTargetProvider()
    {
        // Section Q test:
        // Provider A: deepseek-v4.1-flash
        // Provider B: deepseek-v4.1-flash
        var client = new MockAppServerClient
        {
            Handler = (method, _) =>
            {
                if (method == "thread/fork")
                {
                    return JsonDocument.Parse(@"{
                        ""modelProvider"": ""switchboard_provider_b"",
                        ""model"": ""deepseek-v4.1-flash"",
                        ""thread"": { ""id"": ""thread-forked-4"" }
                    }").RootElement;
                }
                if (method == "thread/read")
                {
                    return JsonDocument.Parse(@"{
                        ""thread"": { ""id"": ""thread-forked-4"" }
                    }").RootElement;
                }
                return JsonDocument.Parse("{}").RootElement;
            }
        };

        var service = new CodexThreadHandoffService(() => Task.FromResult<ICodexAppServerClient>(client));
        var result = await service.ForkThreadAsync(
            "thread-source-a",
            "switchboard_provider_b",
            "deepseek-v4.1-flash");

        Assert.Equal("switchboard_provider_b", result.TargetModelProvider);
        Assert.Equal("deepseek-v4.1-flash", result.TargetModel);

        var forkReq = client.Requests.First(r => r.Method == "thread/fork");
        var json = JsonSerializer.Serialize(forkReq.Parameters);
        Assert.Contains("switchboard_provider_b", json);
    }

    [Fact]
    public async Task SameProviderDifferentModel_PropagatesTargetModel()
    {
        // Section R test:
        // same provider id, source model-a -> target model-b
        var client = new MockAppServerClient
        {
            Handler = (method, _) =>
            {
                if (method == "thread/fork")
                {
                    return JsonDocument.Parse(@"{
                        ""modelProvider"": ""switchboard_provider_a"",
                        ""model"": ""model-b"",
                        ""thread"": { ""id"": ""thread-forked-5"" }
                    }").RootElement;
                }
                if (method == "thread/read")
                {
                    return JsonDocument.Parse(@"{
                        ""thread"": { ""id"": ""thread-forked-5"" }
                    }").RootElement;
                }
                return JsonDocument.Parse("{}").RootElement;
            }
        };

        var service = new CodexThreadHandoffService(() => Task.FromResult<ICodexAppServerClient>(client));
        var result = await service.ForkThreadAsync(
            "thread-source-model-a",
            "switchboard_provider_a",
            "model-b");

        Assert.Equal("switchboard_provider_a", result.TargetModelProvider);
        Assert.Equal("model-b", result.TargetModel);
    }

    [Fact]
    public async Task ReturnedProviderMismatch_ThrowsInvalidOperationException()
    {
        var client = new MockAppServerClient
        {
            Handler = (method, _) =>
            {
                if (method == "thread/fork")
                {
                    return JsonDocument.Parse(@"{
                        ""modelProvider"": ""wrong_provider"",
                        ""model"": ""gpt-5.6-sol"",
                        ""thread"": { ""id"": ""thread-forked-6"" }
                    }").RootElement;
                }
                return JsonDocument.Parse("{}").RootElement;
            }
        };

        var service = new CodexThreadHandoffService(() => Task.FromResult<ICodexAppServerClient>(client));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ForkThreadAsync("src-1", "expected_provider", "gpt-5.6-sol"));
    }

    [Fact]
    public async Task ReturnedModelMismatch_ThrowsInvalidOperationException()
    {
        var client = new MockAppServerClient
        {
            Handler = (method, _) =>
            {
                if (method == "thread/fork")
                {
                    return JsonDocument.Parse(@"{
                        ""modelProvider"": ""prov-1"",
                        ""model"": ""deepseek-v4.1-flash:free"",
                        ""thread"": { ""id"": ""thread-forked-7"" }
                    }").RootElement;
                }
                return JsonDocument.Parse("{}").RootElement;
            }
        };

        var service = new CodexThreadHandoffService(() => Task.FromResult<ICodexAppServerClient>(client));
        // Requested non-free, but server returned :free
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ForkThreadAsync("src-1", "prov-1", "deepseek-v4.1-flash"));
    }

    [Fact]
    public async Task ReturnedIdEqualsSourceId_ThrowsInvalidOperationException()
    {
        var client = new MockAppServerClient
        {
            Handler = (method, _) =>
            {
                if (method == "thread/fork")
                {
                    return JsonDocument.Parse(@"{
                        ""modelProvider"": ""prov-1"",
                        ""model"": ""model-1"",
                        ""thread"": { ""id"": ""same-source-id"" }
                    }").RootElement;
                }
                return JsonDocument.Parse("{}").RootElement;
            }
        };

        var service = new CodexThreadHandoffService(() => Task.FromResult<ICodexAppServerClient>(client));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ForkThreadAsync("same-source-id", "prov-1", "model-1"));
    }

    [Fact]
    public async Task EmptyReturnedId_ThrowsInvalidOperationException()
    {
        var client = new MockAppServerClient
        {
            Handler = (method, _) =>
            {
                if (method == "thread/fork")
                {
                    return JsonDocument.Parse(@"{
                        ""modelProvider"": ""prov-1"",
                        ""model"": ""model-1"",
                        ""thread"": { ""id"": """" }
                    }").RootElement;
                }
                return JsonDocument.Parse("{}").RootElement;
            }
        };

        var service = new CodexThreadHandoffService(() => Task.FromResult<ICodexAppServerClient>(client));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ForkThreadAsync("source-1", "prov-1", "model-1"));
    }

    [Fact]
    public async Task ReadBackVerification_FallsBackToThreadList_WhenReadFails()
    {
        var client = new MockAppServerClient
        {
            Handler = (method, _) =>
            {
                if (method == "thread/fork")
                {
                    return JsonDocument.Parse(@"{
                        ""modelProvider"": ""prov-1"",
                        ""model"": ""model-1"",
                        ""thread"": { ""id"": ""forked-verified-list"" }
                    }").RootElement;
                }
                if (method == "thread/read")
                {
                    throw new InvalidOperationException("thread/read not supported on mock");
                }
                if (method == "thread/list")
                {
                    return JsonDocument.Parse(@"{
                        ""data"": [
                            { ""id"": ""forked-verified-list"", ""modelProvider"": ""prov-1"", ""model"": ""model-1"" }
                        ]
                    }").RootElement;
                }
                return JsonDocument.Parse("{}").RootElement;
            }
        };

        var service = new CodexThreadHandoffService(() => Task.FromResult<ICodexAppServerClient>(client));
        var result = await service.ForkThreadAsync("source-1", "prov-1", "model-1");

        Assert.Equal("forked-verified-list", result.ForkedThreadId);
        Assert.Contains(client.Requests, r => r.Method == "thread/list");
    }

    [Fact]
    public async Task RenameThread_UsesThreadNameSet_NotThreadSetName()
    {
        var client = new MockAppServerClient
        {
            Handler = (method, _) =>
            {
                if (method == "thread/fork")
                {
                    return JsonDocument.Parse(@"{
                        ""modelProvider"": ""prov-1"",
                        ""model"": ""model-1"",
                        ""thread"": { ""id"": ""forked-rename-1"" }
                    }").RootElement;
                }
                if (method == "thread/read")
                {
                    return JsonDocument.Parse(@"{
                        ""thread"": { ""id"": ""forked-rename-1"" }
                    }").RootElement;
                }
                return JsonDocument.Parse("{}").RootElement;
            }
        };

        var service = new CodexThreadHandoffService(() => Task.FromResult<ICodexAppServerClient>(client));
        await service.ForkThreadAsync("source-1", "prov-1", "model-1", "My Continued Chat");

        Assert.Contains(client.Requests, r => r.Method == "thread/name/set");
        Assert.DoesNotContain(client.Requests, r => r.Method == "thread/setName");
    }

    [Fact]
    public async Task RenameThread_FailureDoesNotInvalidateFork()
    {
        var client = new MockAppServerClient
        {
            Handler = (method, _) =>
            {
                if (method == "thread/fork")
                {
                    return JsonDocument.Parse(@"{
                        ""modelProvider"": ""prov-1"",
                        ""model"": ""model-1"",
                        ""thread"": { ""id"": ""forked-rename-fail"" }
                    }").RootElement;
                }
                if (method == "thread/read")
                {
                    return JsonDocument.Parse(@"{
                        ""thread"": { ""id"": ""forked-rename-fail"" }
                    }").RootElement;
                }
                if (method == "thread/name/set")
                {
                    throw new InvalidOperationException("Simulated rename error: no rollout found");
                }
                return JsonDocument.Parse("{}").RootElement;
            }
        };

        var service = new CodexThreadHandoffService(() => Task.FromResult<ICodexAppServerClient>(client));
        var result = await service.ForkThreadAsync("source-1", "prov-1", "model-1", "My Continued Chat");

        // Fork succeeds despite rename exception
        Assert.Equal("forked-rename-fail", result.ForkedThreadId);
    }

    [Fact]
    public async Task ListThreadsAsync_SendsModelProvidersEmptyArray_AndPaginates()
    {
        int callCount = 0;
        var client = new MockAppServerClient
        {
            Handler = (method, _) =>
            {
                if (method == "thread/list")
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        return JsonDocument.Parse(@"{
                            ""data"": [
                                { ""id"": ""thread-p1-1"", ""modelProvider"": ""prov-a"" },
                                { ""id"": ""thread-p1-2"", ""modelProvider"": ""prov-b"" }
                            ],
                            ""nextCursor"": ""cursor-page-2""
                        }").RootElement;
                    }
                    else
                    {
                        return JsonDocument.Parse(@"{
                            ""data"": [
                                { ""id"": ""thread-p2-1"", ""modelProvider"": ""prov-c"" }
                            ]
                        }").RootElement;
                    }
                }
                return JsonDocument.Parse("{}").RootElement;
            }
        };

        var service = new CodexThreadHandoffService(() => Task.FromResult<ICodexAppServerClient>(client));
        var threads = await service.ListThreadsAsync(limit: 3);

        Assert.Equal(3, threads.Count);
        Assert.Equal(2, callCount);

        // Every thread/list call sent modelProviders = []
        var listRequests = client.Requests.Where(r => r.Method == "thread/list").ToList();
        Assert.Equal(2, listRequests.Count);
        foreach (var req in listRequests)
        {
            var json = JsonSerializer.Serialize(req.Parameters);
            Assert.Contains("modelProviders", json);
        }
    }

    [Fact]
    public void RequiresFreshThread_DetectsProprietaryToolHistory()
    {
        var targetPolicy = new EffectiveToolPolicy(
            AllowStandardFunctionTools: true,
            AllowCustomFreeformApplyPatch: false,
            AllowToolSearch: false,
            AllowHostedWebSearch: false,
            AllowStandaloneWebSearch: false,
            AllowNamespaceTools: false,
            AllowMultiAgent: false);

        var historyItems = new[] { "user_message", "web_search_call", "assistant_message" };
        var assessment = ThreadCompatibilityAssessment.EvaluateHistoryItems("thread-web-1", historyItems, targetPolicy);

        Assert.True(assessment.RequiresFreshThread);
        Assert.Contains(assessment.IncompatibleFeatures, f => f.Contains("Hosted web_search"));
    }

    [Fact]
    public void RequiresFreshThread_CompatibleWhenPolicyPermitsAllHistory()
    {
        var targetPolicy = EffectiveToolPolicy.ForOpenAiNative();
        var historyItems = new[] { "user_message", "web_search_call", "apply_patch", "assistant_message" };
        var assessment = ThreadCompatibilityAssessment.EvaluateHistoryItems("thread-compat-1", historyItems, targetPolicy);

        Assert.False(assessment.RequiresFreshThread);
        Assert.Empty(assessment.IncompatibleFeatures);
    }
}
