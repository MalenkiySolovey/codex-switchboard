using System.Text.Json;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Infra.Codex;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexThreadHandoffServiceTests
{
    private sealed class FakeAppServerClient : ICodexAppServerClient
    {
        public bool IsRunning { get; set; } = true;
        public List<(string Method, object? Parameters)> Requests { get; } = [];

        public Func<string, object?, JsonElement>? ResponseHandler { get; set; }

        public Task<JsonElement> RequestAsync(string method, object? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Requests.Add((method, parameters));
            if (ResponseHandler != null)
            {
                return Task.FromResult(ResponseHandler(method, parameters));
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
    public async Task ListThreadsAsync_ParsesAppServerResponse()
    {
        var fakeClient = new FakeAppServerClient
        {
            ResponseHandler = (method, _) =>
            {
                if (method == "thread/list")
                {
                    var json = @"{
                        ""data"": [
                            {
                                ""id"": ""thread-123"",
                                ""name"": ""Refactoring Session"",
                                ""modelProvider"": ""openai"",
                                ""model"": ""gpt-5.3-codex"",
                                ""createdAt"": 1710000000,
                                ""updatedAt"": 1710000500,
                                ""cwd"": ""C:\\workspace""
                            },
                            {
                                ""id"": ""thread-456"",
                                ""name"": ""Router Test"",
                                ""modelProvider"": ""switchboard_8f31c20a"",
                                ""model"": ""gpt-5.6-sol"",
                                ""createdAt"": 1710001000,
                                ""updatedAt"": 1710001200,
                                ""cwd"": ""C:\\workspace""
                            }
                        ]
                    }";
                    return JsonDocument.Parse(json).RootElement;
                }
                return JsonDocument.Parse("{}").RootElement;
            }
        };

        var service = new CodexThreadHandoffService(() => Task.FromResult<ICodexAppServerClient>(fakeClient));
        var threads = await service.ListThreadsAsync(50);

        Assert.Equal(2, threads.Count);
        Assert.Equal("thread-123", threads[0].Id);
        Assert.Equal("openai", threads[0].ModelProvider);
        Assert.Equal("thread-456", threads[1].Id);
        Assert.Equal("switchboard_8f31c20a", threads[1].ModelProvider);
    }

    [Fact]
    public async Task ForkThreadAsync_PropagatesExplicitModelProviderAndModel()
    {
        var fakeClient = new FakeAppServerClient
        {
            ResponseHandler = (method, _) =>
            {
                if (method == "thread/fork")
                {
                    var json = @"{
                        ""modelProvider"": ""switchboard_8f31c20a"",
                        ""model"": ""gpt-5.6-sol"",
                        ""thread"": {
                            ""id"": ""thread-forked-789""
                        }
                    }";
                    return JsonDocument.Parse(json).RootElement;
                }
                return JsonDocument.Parse("{}").RootElement;
            }
        };

        var service = new CodexThreadHandoffService(() => Task.FromResult<ICodexAppServerClient>(fakeClient));
        var result = await service.ForkThreadAsync(
            "source-thread-123",
            "switchboard_8f31c20a",
            "gpt-5.6-sol",
            "Forked on Router.Cheap");

        Assert.Equal("thread-forked-789", result.ForkedThreadId);
        Assert.Equal("source-thread-123", result.SourceThreadId);
        Assert.Equal("switchboard_8f31c20a", result.TargetModelProvider);
        Assert.Equal("gpt-5.6-sol", result.TargetModel);

        var forkReq = fakeClient.Requests.FirstOrDefault(r => r.Method == "thread/fork");
        Assert.NotNull(forkReq.Parameters);

        var paramJson = JsonSerializer.Serialize(forkReq.Parameters);
        Assert.Contains("switchboard_8f31c20a", paramJson);
        Assert.Contains("gpt-5.6-sol", paramJson);
        Assert.Contains("source-thread-123", paramJson);

        var renameReq = fakeClient.Requests.FirstOrDefault(r => r.Method == "thread/setName");
        Assert.NotNull(renameReq.Parameters);
        var renameJson = JsonSerializer.Serialize(renameReq.Parameters);
        Assert.Contains("thread-forked-789", renameJson);
        Assert.Contains("Forked on Router.Cheap", renameJson);
    }
}
