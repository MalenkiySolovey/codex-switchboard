using System.Reflection;
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
}
