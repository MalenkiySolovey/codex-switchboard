using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra;
using CodexSwitcher.Infra.Codex;
using CodexSwitcher.Infra.Io;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexActiveTargetResolverTests
{
    private readonly PhysicalFileSystem _fs = new();

    [Fact]
    public void ResolveActiveTarget_WhenOpenAi_ReturnsChatGptTarget()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var configPath = Path.Combine(temp.Root, "config.toml");
        File.WriteAllText(configPath, "model_provider = \"openai\"\nmodel = \"gpt-5.3-codex\"");

        var routingStore = new CodexRoutingConfigStore(_fs, paths);
        var providerStore = new ApiProviderStore(_fs, paths.ApiProvidersPath);
        var resolver = new CodexActiveTargetResolver(routingStore, providerStore);

        var accountId = Guid.NewGuid();
        var target = resolver.ResolveActiveTarget(configPath, accountId, "user@example.com");

        Assert.IsType<ActiveTarget.ChatGpt>(target);
        var chatGpt = (ActiveTarget.ChatGpt)target;
        Assert.Equal(accountId, chatGpt.ProfileId);
        Assert.Equal("user@example.com", chatGpt.AccountEmail);
    }

    [Fact]
    public void ResolveActiveTarget_WhenSwitchboardProvider_ReturnsApiTarget()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var configPath = Path.Combine(temp.Root, "config.toml");
        var stableId = "switchboard_12345678abcd";
        File.WriteAllText(configPath, $"model_provider = \"{stableId}\"\n");

        var routingStore = new CodexRoutingConfigStore(_fs, paths);
        var providerStore = new ApiProviderStore(_fs, paths.ApiProvidersPath);

        var apiProfile = new ApiProviderProfile
        {
            Id = Guid.NewGuid(),
            StableCodexProviderId = stableId,
            Nickname = "Router Test"
        };
        providerStore.Save(apiProfile);

        var resolver = new CodexActiveTargetResolver(routingStore, providerStore);
        var target = resolver.ResolveActiveTarget(configPath, Guid.NewGuid(), "chatgpt@example.com");

        Assert.IsType<ActiveTarget.Api>(target);
        var api = (ActiveTarget.Api)target;
        Assert.Equal(stableId, api.Profile.StableCodexProviderId);
        Assert.Equal("Router Test", api.Profile.DisplayName);
    }

    [Fact]
    public void ResolveActiveTarget_WhenExternalProvider_ReturnsExternalTarget()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var configPath = Path.Combine(temp.Root, "config.toml");
        File.WriteAllText(configPath, "model_provider = \"company_internal\"\n");

        var routingStore = new CodexRoutingConfigStore(_fs, paths);
        var providerStore = new ApiProviderStore(_fs, paths.ApiProvidersPath);
        var resolver = new CodexActiveTargetResolver(routingStore, providerStore);

        var target = resolver.ResolveActiveTarget(configPath);
        Assert.IsType<ActiveTarget.External>(target);
        var external = (ActiveTarget.External)target;
        Assert.Equal("company_internal", external.ProviderId);
    }
}
