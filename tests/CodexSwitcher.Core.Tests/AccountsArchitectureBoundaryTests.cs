using System.Reflection;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class AccountsArchitectureBoundaryTests
{
    private static Assembly? LoadAppAssembly()
    {
        // Probe known build output paths for CodexSwitchboard.dll
        var baseDir = AppContext.BaseDirectory;
        var probePaths = new[]
        {
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "CodexSwitcher.App", "bin", "x64", "Release", "net10.0-windows10.0.19041.0", "win-x64", "CodexSwitchboard.dll"),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "CodexSwitcher.App", "bin", "Release", "net10.0-windows10.0.19041.0", "win-x64", "CodexSwitchboard.dll"),
            Path.Combine(baseDir, "CodexSwitchboard.dll")
        };

        var existingCandidates = probePaths
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        foreach (var full in existingCandidates)
        {
            try
            {
                return Assembly.LoadFrom(full);
            }
            catch
            {
                // Continue probing
            }
        }
        return null;
    }

    [Fact]
    public void ShellViewModel_MustNotDependOnBackendOrPathServices_AndMainViewModelMustNotExist()
    {
        var appAssembly = LoadAppAssembly();
        if (appAssembly is null)
            return; // Skip if app assembly is not accessible in this test environment

        // Verify MainViewModel has been completely removed (no dead alias)
        var oldMainVmType = appAssembly.GetType("CodexSwitcher.App.ViewModels.MainViewModel");
        Assert.Null(oldMainVmType);

        var shellVmType = appAssembly.GetType("CodexSwitcher.App.Shell.ShellViewModel");
        Assert.NotNull(shellVmType);

        var ctors = shellVmType.GetConstructors();
        Assert.NotEmpty(ctors);

        var forbiddenTypes = new[]
        {
            "ProfileService",
            "SwitchService",
            "IUsageService",
            "UsagePollingCoordinator",
            "ITotpCredentialStore",
            "ITotpRevealAuthorizationService",
            "SettingsStore",
            "ICodexTargetSwitchService",
            "AppPaths",
            "IFileSystem",
            "IApiKeySecretStore",
            "ICodexActiveTargetResolver"
        };

        foreach (var ctor in ctors)
        {
            foreach (var p in ctor.GetParameters())
            {
                foreach (var forbidden in forbiddenTypes)
                {
                    Assert.False(
                        p.ParameterType.Name == forbidden,
                        $"ShellViewModel constructor must not depend on {forbidden}, but found parameter '{p.Name}' of type '{p.ParameterType.Name}'");
                }
            }
        }
    }

    [Fact]
    public void ViewModels_MustNeverExposePersistentTotpSecretSeeds()
    {
        var appAssembly = LoadAppAssembly();
        if (appAssembly is null)
            return;

        Type[] types;
        try
        {
            types = appAssembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).Select(t => t!).ToArray();
        }

        var vmTypes = types.Where(t => t.Name.EndsWith("ViewModel", StringComparison.Ordinal) || t.Name.EndsWith("Coordinator", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(vmTypes);

        var forbiddenPropertySubstrings = new[]
        {
            "Seed",
            "ProvisioningKey",
            "TotpSecret",
            "SecretSeed",
            "PlaintextSeed"
        };

        foreach (var vm in vmTypes)
        {
            foreach (var prop in vm.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var forbidden in forbiddenPropertySubstrings)
                {
                    Assert.False(
                        prop.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                        $"ViewModel '{vm.Name}' must not expose property '{prop.Name}' containing forbidden secret substring '{forbidden}'");
                }
            }
        }
    }

    [Fact]
    public void AccountsSlice_MaintainsDecoupledOwnership()
    {
        var appAssembly = LoadAppAssembly();
        if (appAssembly is null)
            return;

        var accountsVmType = appAssembly.GetType("CodexSwitcher.App.Features.Accounts.AccountsViewModel");
        Assert.NotNull(accountsVmType);

        var totpCoordType = appAssembly.GetType("CodexSwitcher.App.Features.Accounts.TotpPresentationCoordinator");
        Assert.NotNull(totpCoordType);

        var usageCoordType = appAssembly.GetType("CodexSwitcher.App.Features.Accounts.AccountUsageCoordinator");
        Assert.NotNull(usageCoordType);
    }
}
