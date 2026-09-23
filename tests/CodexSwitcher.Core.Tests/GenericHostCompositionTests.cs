using System.Reflection;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Providers.Inspection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class GenericHostCompositionTests : IDisposable
{
    private readonly string _testDir;
    private readonly AppPaths _testPaths;

    public GenericHostCompositionTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "CodexSwitcher_HostTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        var codexHome = Path.Combine(_testDir, ".codex");
        Directory.CreateDirectory(codexHome);
        _testPaths = new AppPaths(_testDir, codexHome);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }
        catch { }
    }

    private static Assembly? LoadAppAssembly()
    {
        var baseDir = AppContext.BaseDirectory;
        var probePaths = new[]
        {
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "CodexSwitcher.App", "bin", "x64", "Debug", "net10.0-windows10.0.19041.0", "win-x64", "CodexSwitchboard.dll"),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "CodexSwitcher.App", "bin", "Debug", "net10.0-windows10.0.19041.0", "win-x64", "CodexSwitchboard.dll"),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "CodexSwitcher.App", "bin", "x64", "Release", "net10.0-windows10.0.19041.0", "win-x64", "CodexSwitchboard.dll"),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "CodexSwitcher.App", "bin", "Release", "net10.0-windows10.0.19041.0", "win-x64", "CodexSwitchboard.dll"),
            Path.Combine(baseDir, "CodexSwitchboard.dll")
        };

        var candidates = probePaths
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        foreach (var full in candidates)
        {
            try
            {
                return Assembly.LoadFrom(full);
            }
            catch { }
        }

        return null;
    }

    private IHost CreateTestHost(bool validateContainer = true)
    {
        var appAssembly = LoadAppAssembly()
            ?? throw new InvalidOperationException("Could not load CodexSwitchboard.dll for host composition testing.");

        var builderType = appAssembly.GetType("CodexSwitcher.App.Composition.SwitchboardHostBuilder")
            ?? throw new InvalidOperationException("SwitchboardHostBuilder type not found.");

        var createHostValidated = builderType.GetMethod("CreateHost", new[] { typeof(AppPaths), typeof(Action<IServiceCollection>), typeof(bool) });
        if (createHostValidated != null)
        {
            return (IHost)createHostValidated.Invoke(null, new object?[] { _testPaths, null, validateContainer })!;
        }

        var createHostMethod = builderType.GetMethod("CreateHost", new[] { typeof(AppPaths), typeof(Action<IServiceCollection>) })
            ?? throw new InvalidOperationException("CreateHost method not found on SwitchboardHostBuilder.");

        var host = (IHost)createHostMethod.Invoke(null, new object?[] { _testPaths, null })!;
        return host;
    }

    [Fact]
    public void ContainerValidation_WhenEnabledInTest_ValidatesScopesAndDependencies()
    {
        using var host = CreateTestHost(validateContainer: true);
        Assert.NotNull(host);
        using var scope = host.Services.CreateScope();
        Assert.NotNull(scope);
    }

    [Fact]
    public void HostBuild_WithValidateOnBuildAndScopes_ResolvesCoreServices()
    {
        using var host = CreateTestHost();

        var switchService = host.Services.GetRequiredService<ICodexTargetSwitchService>();
        var transport = host.Services.GetRequiredService<ISafeProviderHttpTransport>();
        var catalogService = host.Services.GetRequiredService<IProviderCatalogService>();
        var usageService = host.Services.GetRequiredService<IUsageService>();
        var pollingCoordinator = host.Services.GetRequiredService<UsagePollingCoordinator>();
        var profileCoordinator = host.Services.GetRequiredService<IProfileOperationCoordinator>();
        var lifetime = host.Services.GetRequiredService<IAppLifetime>();

        Assert.NotNull(switchService);
        Assert.NotNull(transport);
        Assert.NotNull(catalogService);
        Assert.NotNull(usageService);
        Assert.NotNull(pollingCoordinator);
        Assert.NotNull(profileCoordinator);
        Assert.NotNull(lifetime);
    }

    [Fact]
    public void SingletonIdentities_MustResolveSameInstance()
    {
        using var host = CreateTestHost();

        var switch1 = host.Services.GetRequiredService<ICodexTargetSwitchService>();
        var switch2 = host.Services.GetRequiredService<ICodexTargetSwitchService>();
        Assert.Same(switch1, switch2);

        var transport1 = host.Services.GetRequiredService<ISafeProviderHttpTransport>();
        var transport2 = host.Services.GetRequiredService<ISafeProviderHttpTransport>();
        Assert.Same(transport1, transport2);

        var catalog1 = host.Services.GetRequiredService<IProviderCatalogService>();
        var catalog2 = host.Services.GetRequiredService<IProviderCatalogService>();
        Assert.Same(catalog1, catalog2);

        var usage1 = host.Services.GetRequiredService<UsagePollingCoordinator>();
        var usage2 = host.Services.GetRequiredService<UsagePollingCoordinator>();
        Assert.Same(usage1, usage2);

        var lifetime1 = host.Services.GetRequiredService<IAppLifetime>();
        var lifetime2 = host.Services.GetRequiredService<IAppLifetime>();
        Assert.Same(lifetime1, lifetime2);
    }

    [Fact]
    public void TransientSettingsViewModel_FactoryProducesDistinctInstances()
    {
        using var host = CreateTestHost();

        var appAssembly = LoadAppAssembly()!;
        var settingsVmType = appAssembly.GetType("CodexSwitcher.App.Features.Settings.SettingsViewModel")!;
        var funcType = typeof(Func<>).MakeGenericType(settingsVmType);

        var factory = host.Services.GetRequiredService(funcType) as Delegate;
        Assert.NotNull(factory);

        var vm1 = factory.DynamicInvoke();
        var vm2 = factory.DynamicInvoke();

        Assert.NotNull(vm1);
        Assert.NotNull(vm2);
        Assert.NotSame(vm1, vm2);
    }

    [Fact]
    public async Task ApplicationLifetime_UnificationWithGenericHost_BridgesStoppingToken()
    {
        using var host = CreateTestHost();

        var lifetime = host.Services.GetRequiredService<IAppLifetime>();
        Assert.False(lifetime.IsStopping);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);

        // Commencing host stop must bridge through HostAppLifetimeAdapter
        await host.StopAsync();

        Assert.True(lifetime.IsStopping);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task HostStopAndDispose_IsIdempotent()
    {
        var host = CreateTestHost();

        // Ensure services are initialized
        _ = host.Services.GetRequiredService<ICodexTargetSwitchService>();
        _ = host.Services.GetRequiredService<ISafeProviderHttpTransport>();
        _ = host.Services.GetRequiredService<UsagePollingCoordinator>();

        // Call StopAsync once
        await host.StopAsync();

        // Call StopAsync again: must be safe and idempotent
        var secondStopEx = await Record.ExceptionAsync(async () => await host.StopAsync());
        Assert.Null(secondStopEx);

        // Dispose once
        host.Dispose();

        // Dispose again: must be idempotent
        var secondDisposeEx = Record.Exception(() => host.Dispose());
        Assert.Null(secondDisposeEx);
    }

    [Fact]
    public void ShellViewModel_ResolvesFromContainerWithDependencies()
    {
        using var host = CreateTestHost();

        var appAssembly = LoadAppAssembly()!;
        var shellVmType = appAssembly.GetType("CodexSwitcher.App.Shell.ShellViewModel")!;

        var shellVm = host.Services.GetRequiredService(shellVmType);
        Assert.NotNull(shellVm);
    }

    [Fact]
    public void AppLifetime_StopApplication_SignalsStopping()
    {
        using var host = CreateTestHost();

        var lifetime = host.Services.GetRequiredService<IAppLifetime>();
        Assert.False(lifetime.IsStopping);

        lifetime.StopApplication();
        Assert.True(lifetime.IsStopping);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public void HostConfiguration_HasNoEnvironmentVariableOrAppSettingsProviders()
    {
        using var host = CreateTestHost();
        var config = host.Services.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
        if (config is Microsoft.Extensions.Configuration.IConfigurationRoot configRoot)
        {
            foreach (var provider in configRoot.Providers)
            {
                var typeName = provider.GetType().Name;
                Assert.False(
                    typeName.Contains("EnvironmentVariables", StringComparison.OrdinalIgnoreCase),
                    $"Host configuration must not load environment variables provider: '{typeName}'.");
                Assert.False(
                    typeName.Contains("JsonConfigurationProvider", StringComparison.OrdinalIgnoreCase),
                    $"Host configuration must not load appsettings.json provider: '{typeName}'.");
            }
        }
    }
}
