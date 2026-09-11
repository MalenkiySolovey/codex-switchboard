using System.Reflection;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Infra.Providers.Inspection;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void CoreAssembly_MustNotOwnConcreteHttpProviderTransport()
    {
        var coreAssembly = typeof(IDeclarativeProviderInspector).Assembly;

        // Verify DeclarativeProviderInspector is NOT in Core
        var inspectorInCore = coreAssembly.GetType("CodexSwitcher.Core.Services.DeclarativeProviderInspector");
        Assert.Null(inspectorInCore);

        // Verify Core contains no types implementing IDeclarativeProviderInspector
        var coreInspectorImplementations = coreAssembly.GetTypes()
            .Where(t => !t.IsInterface && typeof(IDeclarativeProviderInspector).IsAssignableFrom(t))
            .ToList();

        Assert.Empty(coreInspectorImplementations);

        // Verify DeclarativeProviderInspector is indeed located in Infra
        var infraInspectorType = typeof(DeclarativeProviderInspector);
        Assert.Equal("CodexSwitcher.Infra", infraInspectorType.Assembly.GetName().Name);
        Assert.True(typeof(IDeclarativeProviderInspector).IsAssignableFrom(infraInspectorType));
    }

    [Fact]
    public void CoreAssembly_MustNotReferenceWinUIOrPresentation()
    {
        var coreAssembly = typeof(IDeclarativeProviderInspector).Assembly;
        var referencedAssemblies = coreAssembly.GetReferencedAssemblies();

        var forbiddenNames = new[] { "Microsoft.UI.Xaml", "Microsoft.WindowsAppSDK", "WinUI", "CodexSwitcher.App" };
        foreach (var refAsm in referencedAssemblies)
        {
            foreach (var forbidden in forbiddenNames)
            {
                Assert.DoesNotContain(forbidden, refAsm.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void InfraAssembly_MustNotReferenceWinUIOrPresentation()
    {
        var infraAssembly = typeof(DeclarativeProviderInspector).Assembly;
        var referencedAssemblies = infraAssembly.GetReferencedAssemblies();

        var forbiddenNames = new[] { "Microsoft.UI.Xaml", "Microsoft.WindowsAppSDK", "CodexSwitcher.App" };
        foreach (var refAsm in referencedAssemblies)
        {
            foreach (var forbidden in forbiddenNames)
            {
                Assert.DoesNotContain(forbidden, refAsm.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void ProviderInspectionService_EnforcesStrictSecretEncapsulation()
    {
        var iface = typeof(IProviderInspectionService);
        Assert.True(iface.IsInterface);
        Assert.Equal("CodexSwitcher.Core.Providers.Contracts", iface.Namespace);

        // Verify method signatures: methods should only accept Guid profileId or CancellationToken
        // None of the parameters or return types should be raw string apiKeys
        foreach (var method in iface.GetMethods())
        {
            foreach (var parameter in method.GetParameters())
            {
                Assert.NotEqual("apiKey", parameter.Name, StringComparer.OrdinalIgnoreCase);
                Assert.NotEqual("secret", parameter.Name, StringComparer.OrdinalIgnoreCase);
            }

            Assert.False(method.ReturnType == typeof(string));
        }
    }

    [Fact]
    public void CoreAssembly_MustNotReferenceInfraOrAppAssemblies()
    {
        var coreAssembly = typeof(IProviderInspectionService).Assembly;
        var referencedAssemblies = coreAssembly.GetReferencedAssemblies();

        var forbiddenNames = new[] { "CodexSwitcher.Infra", "CodexSwitcher.App" };
        foreach (var refAsm in referencedAssemblies)
        {
            foreach (var forbidden in forbiddenNames)
            {
                Assert.DoesNotContain(forbidden, refAsm.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void InfraAssembly_MustNotReferenceAppAssembly()
    {
        var infraAssembly = typeof(DeclarativeProviderInspector).Assembly;
        var referencedAssemblies = infraAssembly.GetReferencedAssemblies();

        var forbiddenNames = new[] { "CodexSwitcher.App" };
        foreach (var refAsm in referencedAssemblies)
        {
            foreach (var forbidden in forbiddenNames)
            {
                Assert.DoesNotContain(forbidden, refAsm.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void LegacyNamespaces_MustNotContainAnyTypesInCore()
    {
        var coreAssembly = typeof(IProviderInspectionService).Assembly;
        var legacyNamespaces = new[]
        {
            "CodexSwitcher.Core.Abstractions",
            "CodexSwitcher.Core.Models",
            "CodexSwitcher.Core.Services",
            "CodexSwitcher.Core.Support"
        };

        foreach (var t in coreAssembly.GetTypes())
        {
            foreach (var legacyNs in legacyNamespaces)
            {
                Assert.NotEqual(legacyNs, t.Namespace);
            }
        }
    }

    [Fact]
    public void LegacyNamespaces_MustNotContainAnyTypesInInfra()
    {
        var infraAssembly = typeof(DeclarativeProviderInspector).Assembly;
        var legacyNamespaces = new[]
        {
            "CodexSwitcher.Infra.Io"
        };

        foreach (var t in infraAssembly.GetTypes())
        {
            foreach (var legacyNs in legacyNamespaces)
            {
                Assert.NotEqual(legacyNs, t.Namespace);
            }
        }
    }

    [Fact]
    public void SafeProviderHttpTransport_MustResideInInfraAssembly_AndCoreMustNotReferenceHttpTransport()
    {
        var infraAssembly = typeof(DeclarativeProviderInspector).Assembly;
        var transportType = infraAssembly.GetType("CodexSwitcher.Infra.Providers.Inspection.SafeProviderHttpTransport");
        Assert.NotNull(transportType);
        Assert.Equal("CodexSwitcher.Infra", transportType.Assembly.GetName().Name);

        var coreAssembly = typeof(IDeclarativeProviderInspector).Assembly;
        var coreTransport = coreAssembly.GetType("CodexSwitcher.Core.Providers.Inspection.SafeProviderHttpTransport");
        Assert.Null(coreTransport);

        // Verify Core contains no types with 'HttpTransport' in name
        Assert.DoesNotContain(coreAssembly.GetTypes(), t => t.Name.Contains("HttpTransport", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SwitchPlans_And_ProbePlans_MustNotExposeSecretMaterial()
    {
        var planTypes = new[]
        {
            typeof(CodexSwitchPlan),
            typeof(ApiProviderSwitchPlan),
            typeof(ChatGptAccountSwitchPlan),
            typeof(ChatGptReturnRoutingSwitchPlan),
            typeof(ChatGptNoOpSwitchPlan),
            typeof(ApiRouteSwitchPlan),
            typeof(InvalidSwitchPlan),
            typeof(ProviderProbePlan)
        };

        var sensitiveWords = new[] { "ApiKey", "RawKey", "Secret", "Password", "PrivateToken" };

        foreach (var type in planTypes)
        {
            foreach (var prop in type.GetProperties())
            {
                foreach (var word in sensitiveWords)
                {
                    Assert.False(
                        prop.Name.Contains(word, StringComparison.OrdinalIgnoreCase) && prop.PropertyType == typeof(string),
                        $"Plan type '{type.Name}' exposes sensitive property '{prop.Name}'.");
                }
            }
        }
    }

    [Fact]
    public void TargetSwitchAndProviderInspector_PrimaryConstructors_HaveDecomposedResponsibilities()
    {
        // CodexTargetSwitchService decomposed constructor has exactly 2 parameters
        var switchServiceType = typeof(CodexTargetSwitchService);
        var switchCtors = switchServiceType.GetConstructors();
        var primarySwitchCtor = switchCtors.MinBy(c => c.GetParameters().Length);
        Assert.NotNull(primarySwitchCtor);
        Assert.Equal(2, primarySwitchCtor.GetParameters().Length);
        Assert.Equal(typeof(ISwitchPlanBuilder), primarySwitchCtor.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(ISwitchTransactionExecutor), primarySwitchCtor.GetParameters()[1].ParameterType);

        // DeclarativeProviderInspector decomposed constructor has exactly 3 parameters
        var inspectorType = typeof(DeclarativeProviderInspector);
        var inspectorCtors = inspectorType.GetConstructors();
        var primaryInspectorCtor = inspectorCtors.MaxBy(c => c.GetParameters().Length);
        Assert.NotNull(primaryInspectorCtor);
        Assert.Equal(3, primaryInspectorCtor.GetParameters().Length);
        Assert.Equal(typeof(IProviderProbePlanner), primaryInspectorCtor.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(ISafeProviderHttpTransport), primaryInspectorCtor.GetParameters()[1].ParameterType);
        Assert.Equal(typeof(IProviderProbeResponseMapper), primaryInspectorCtor.GetParameters()[2].ParameterType);
    }

    [Fact]
    public void CoreAssembly_MustNotReferenceHostingFramework()
    {
        var coreAssembly = typeof(ICodexTargetSwitchService).Assembly;
        var referencedAssemblies = coreAssembly.GetReferencedAssemblies();

        foreach (var refAsm in referencedAssemblies)
        {
            Assert.DoesNotContain("Microsoft.Extensions.Hosting", refAsm.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ProductionSource_MustNotExposeStaticServiceProvider()
    {
        var baseDir = AppContext.BaseDirectory;
        var srcDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src"));

        if (!Directory.Exists(srcDir))
            return;

        var csFiles = Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("/obj/"))
            .ToList();

        foreach (var file in csFiles)
        {
            var content = File.ReadAllText(file);
            Assert.False(
                content.Contains("AppHost.Services"),
                $"File '{file}' contains forbidden reference to 'AppHost.Services'.");
            Assert.False(
                content.Contains("static IServiceProvider"),
                $"File '{file}' exposes forbidden 'static IServiceProvider'.");
        }
    }

    [Fact]
    public void ServiceRegistrationModules_MustNotCallBuildServiceProvider()
    {
        var baseDir = AppContext.BaseDirectory;
        var srcDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src"));

        if (!Directory.Exists(srcDir))
            return;

        var registrationFile = Path.Combine(srcDir, "CodexSwitcher.App", "Composition", "ServiceCollectionExtensions.cs");
        if (File.Exists(registrationFile))
        {
            var codeLines = File.ReadAllLines(registrationFile)
                .Select(l => l.Trim())
                .Where(l => !l.StartsWith("//", StringComparison.Ordinal) && !l.StartsWith("///", StringComparison.Ordinal) && !l.StartsWith('*'));

            foreach (var line in codeLines)
            {
                Assert.False(
                    line.Contains("BuildServiceProvider("),
                    $"ServiceCollectionExtensions.cs must not call BuildServiceProvider() in executable code: '{line}'.");
            }
        }
    }

    [Fact]
    public void ViewModels_MustNotDependOnHostOrServiceProvider()
    {
        var baseDir = AppContext.BaseDirectory;
        var srcDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "CodexSwitcher.App"));

        if (!Directory.Exists(srcDir))
            return;

        var vmFiles = Directory.EnumerateFiles(srcDir, "*ViewModel*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("/obj/"))
            .ToList();

        var forbiddenTokens = new[] { "IServiceProvider", "IHost ", "IHost,", "IHost>", "IHostApplicationLifetime" };

        foreach (var file in vmFiles)
        {
            var content = File.ReadAllText(file);
            foreach (var token in forbiddenTokens)
            {
                Assert.False(
                    content.Contains(token),
                    $"ViewModel '{Path.GetFileName(file)}' must not depend on '{token}'.");
            }
        }
    }
}
