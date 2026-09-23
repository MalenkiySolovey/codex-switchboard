using System;
using System.Collections.Generic;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexTargetEnvironmentProjectorTests
{
    [Fact]
    public void Project_ChatGpt_EnforcesBuiltInOpenAiCatalogMode_AndRemovesSwitchboardKeys()
    {
        var target = CodexTargetEnvironment.ForChatGpt();
        var rawKeys = new Dictionary<string, string?>
        {
            ["model_provider"] = "\"openai\"",
            ["model"] = "\"deepseek-v4.1-flash\"",
            ["model_reasoning_effort"] = "\"xhigh\"",
            ["model_context_window"] = "500000",
            ["model_catalog_json"] = "\"C:\\fake\\catalogs\\catalog-grok-4.7.json\""
        };
        var ledger = new SwitchboardRoutingBaseline();

        var plan = CodexTargetEnvironmentProjector.Project(target, rawKeys, ledger);

        Assert.Equal("\"openai\"", plan.RootKeysToSet["model_provider"]);
        Assert.Contains("model_catalog_json", plan.RootKeysToRemove);
        Assert.Contains("model_context_window", plan.RootKeysToRemove);
        Assert.Contains("model_reasoning_effort", plan.RootKeysToRemove);
        Assert.Contains("model", plan.RootKeysToRemove);
    }

    [Fact]
    public void Project_ApiProvider_AppliesExactProviderAndModel_AndProfileCatalog()
    {
        var profileId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = profileId,
            Nickname = "Test Router",
            CatalogProviderId = "openrouter",
            StableCodexProviderId = "switchboard_112233445566",
            SelectedModel = "deepseek-v4.1-flash",
            ModelOverrides = new CodexModelOverrides
            {
                ContextWindowTokens = 500000,
                ReasoningEffort = CodexReasoningEffort.High
            }
        };

        var catalogPath = @"C:\Users\User\AppData\Local\CodexSwitchboard\catalogs\test\models.json";

        var target = CodexTargetEnvironment.ForApiProvider(
            profile,
            exactModel: "deepseek-v4.1-flash",
            catalogPath: catalogPath);

        var rawKeys = new Dictionary<string, string?>
        {
            ["model_provider"] = "\"openai\"",
            ["model"] = "\"gpt-5\""
        };
        var ledger = new SwitchboardRoutingBaseline();

        var plan = CodexTargetEnvironmentProjector.Project(target, rawKeys, ledger);

        Assert.Equal("\"switchboard_112233445566\"", plan.RootKeysToSet["model_provider"]);
        Assert.Equal("\"deepseek-v4.1-flash\"", plan.RootKeysToSet["model"]);
        Assert.Equal("500000", plan.RootKeysToSet["model_context_window"]);
        Assert.Equal("\"high\"", plan.RootKeysToSet["model_reasoning_effort"]);
        Assert.Contains("models.json", plan.RootKeysToSet["model_catalog_json"]);
    }

    [Fact]
    public void Project_ChatGpt_PreservesUserBaseline_WhenBaselineHasValue()
    {
        var target = CodexTargetEnvironment.ForChatGpt();
        var rawKeys = new Dictionary<string, string?>
        {
            ["model_provider"] = "\"switchboard_112233445566\"",
            ["model"] = "\"custom-model\"",
            ["model_context_window"] = "256000"
        };
        var ledger = new SwitchboardRoutingBaseline
        {
            BaselineValues = new Dictionary<string, string?>
            {
                ["model"] = "\"gpt-4\"",
                ["model_context_window"] = "128000"
            }
        };

        var plan = CodexTargetEnvironmentProjector.Project(target, rawKeys, ledger);

        Assert.Equal("\"openai\"", plan.RootKeysToSet["model_provider"]);
        // User baseline preserved
        Assert.Equal("\"gpt-4\"", plan.RootKeysToSet["model"]);
        Assert.Equal("128000", plan.RootKeysToSet["model_context_window"]);
    }

    [Fact]
    public void Project_DetectsExternalEditConflicts_WhenConfigModifiedOutsideSwitchboard()
    {
        var target = CodexTargetEnvironment.ForChatGpt();
        var rawKeys = new Dictionary<string, string?>
        {
            ["model"] = "\"gpt-4o-edited-externally\""
        };
        var ledger = new SwitchboardRoutingBaseline
        {
            LastAppliedValues = new Dictionary<string, string?>
            {
                ["model"] = "\"gpt-5\""
            }
        };

        var plan = CodexTargetEnvironmentProjector.Project(target, rawKeys, ledger);

        Assert.NotEmpty(plan.ExternalEditConflicts);
        Assert.Contains(plan.ExternalEditConflicts, c => c.Contains("model"));
    }

    [Fact]
    public void Project_ChatGpt_UserExplicitlyOwnsContext500k_Preserved()
    {
        var target = CodexTargetEnvironment.ForChatGpt();
        var rawKeys = new Dictionary<string, string?>
        {
            ["model_provider"] = "\"switchboard_123\"",
            ["model_context_window"] = "500000"
        };
        var ledger = new SwitchboardRoutingBaseline
        {
            BaselineValues = new Dictionary<string, string?>
            {
                ["model_context_window"] = "500000"
            }
        };

        var plan = CodexTargetEnvironmentProjector.Project(target, rawKeys, ledger);

        Assert.Equal("500000", plan.RootKeysToSet["model_context_window"]);
        Assert.DoesNotContain("model_context_window", plan.RootKeysToRemove);
    }

    [Fact]
    public void Project_ChatGpt_UserExplicitlyOwnsReasoningXHigh_Preserved()
    {
        var target = CodexTargetEnvironment.ForChatGpt();
        var rawKeys = new Dictionary<string, string?>
        {
            ["model_provider"] = "\"switchboard_123\"",
            ["model_reasoning_effort"] = "\"xhigh\""
        };
        var ledger = new SwitchboardRoutingBaseline
        {
            BaselineValues = new Dictionary<string, string?>
            {
                ["model_reasoning_effort"] = "\"xhigh\""
            }
        };

        var plan = CodexTargetEnvironmentProjector.Project(target, rawKeys, ledger);

        Assert.Equal("\"xhigh\"", plan.RootKeysToSet["model_reasoning_effort"]);
        Assert.DoesNotContain("model_reasoning_effort", plan.RootKeysToRemove);
    }

    [Fact]
    public void Project_ChatGpt_ApiProfileWroteContext500k_RemovedOnApiToChatGpt()
    {
        var target = CodexTargetEnvironment.ForChatGpt();
        var rawKeys = new Dictionary<string, string?>
        {
            ["model_provider"] = "\"switchboard_123\"",
            ["model_context_window"] = "500000"
        };
        var ledger = new SwitchboardRoutingBaseline
        {
            ManagedKeys = new Dictionary<string, ManagedKeyProvenance>
            {
                ["model_context_window"] = new ManagedKeyProvenance
                {
                    Key = "model_context_window",
                    BaselineWasPresent = false,
                    BaselineValue = null,
                    LastAppliedWasPresent = true,
                    LastAppliedValue = "500000",
                    OwnerTargetKind = "ApiProvider"
                }
            }
        };

        var plan = CodexTargetEnvironmentProjector.Project(target, rawKeys, ledger);

        Assert.Contains("model_context_window", plan.RootKeysToRemove);
        Assert.False(plan.RootKeysToSet.ContainsKey("model_context_window"));
    }

    [Fact]
    public void Project_ChatGpt_ApiProfileWroteContext256k_RemovedOnApiToChatGpt()
    {
        var target = CodexTargetEnvironment.ForChatGpt();
        var rawKeys = new Dictionary<string, string?>
        {
            ["model_provider"] = "\"switchboard_123\"",
            ["model_context_window"] = "256000"
        };
        var ledger = new SwitchboardRoutingBaseline
        {
            ManagedKeys = new Dictionary<string, ManagedKeyProvenance>
            {
                ["model_context_window"] = new ManagedKeyProvenance
                {
                    Key = "model_context_window",
                    BaselineWasPresent = false,
                    BaselineValue = null,
                    LastAppliedWasPresent = true,
                    LastAppliedValue = "256000",
                    OwnerTargetKind = "ApiProvider"
                }
            }
        };

        var plan = CodexTargetEnvironmentProjector.Project(target, rawKeys, ledger);

        Assert.Contains("model_context_window", plan.RootKeysToRemove);
        Assert.False(plan.RootKeysToSet.ContainsKey("model_context_window"));
    }

    [Fact]
    public void Project_ChatGpt_ApiProfileWroteArbitraryModel_RemovedOnApiToChatGpt()
    {
        var target = CodexTargetEnvironment.ForChatGpt();
        var rawKeys = new Dictionary<string, string?>
        {
            ["model_provider"] = "\"switchboard_123\"",
            ["model"] = "\"company-internal-foo\""
        };
        var ledger = new SwitchboardRoutingBaseline
        {
            ManagedKeys = new Dictionary<string, ManagedKeyProvenance>
            {
                ["model"] = new ManagedKeyProvenance
                {
                    Key = "model",
                    BaselineWasPresent = false,
                    BaselineValue = null,
                    LastAppliedWasPresent = true,
                    LastAppliedValue = "\"company-internal-foo\"",
                    OwnerTargetKind = "ApiProvider"
                }
            }
        };

        var plan = CodexTargetEnvironmentProjector.Project(target, rawKeys, ledger);

        Assert.Contains("model", plan.RootKeysToRemove);
        Assert.False(plan.RootKeysToSet.ContainsKey("model"));
    }

    [Fact]
    public void Project_ExternalUserEdit_AfterSwitchboardWrite_ThrowsConflictOnFailFast()
    {
        var target = CodexTargetEnvironment.ForChatGpt();
        var rawKeys = new Dictionary<string, string?>
        {
            ["model_context_window"] = "999999"
        };
        var ledger = new SwitchboardRoutingBaseline
        {
            ManagedKeys = new Dictionary<string, ManagedKeyProvenance>
            {
                ["model_context_window"] = new ManagedKeyProvenance
                {
                    Key = "model_context_window",
                    LastAppliedWasPresent = true,
                    LastAppliedValue = "500000"
                }
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CodexTargetEnvironmentProjector.Project(target, rawKeys, ledger, failOnExternalEdit: true));
        Assert.Contains("External configuration conflict detected", ex.Message);
        Assert.Contains("model_context_window", ex.Message);
    }

    [Fact]
    public void PurgePoisonedBaselines_WithoutSwitchboardCatalog_NeverPurgesUserValues()
    {
        var baseline = new SwitchboardRoutingBaseline
        {
            BaselineValues = new Dictionary<string, string?>
            {
                ["model_context_window"] = "500000",
                ["model_reasoning_effort"] = "\"xhigh\"",
                ["model"] = "\"deepseek-v4.1-flash\""
            }
        };

        var modified = baseline.PurgePoisonedBaselines();

        Assert.False(modified);
        Assert.Equal("500000", baseline.BaselineValues["model_context_window"]);
        Assert.Equal("\"xhigh\"", baseline.BaselineValues["model_reasoning_effort"]);
        Assert.Equal("\"deepseek-v4.1-flash\"", baseline.BaselineValues["model"]);
    }

    [Fact]
    public void PurgePoisonedBaselines_WithSwitchboardCatalogEvidence_PurgesContaminatedSnapshot()
    {
        var baseline = new SwitchboardRoutingBaseline
        {
            BaselineValues = new Dictionary<string, string?>
            {
                ["model_catalog_json"] = @"C:\Users\User\AppData\Local\CodexSwitchboard\catalogs\catalog-grok-4.7.json",
                ["model_context_window"] = "500000",
                ["model_reasoning_effort"] = "\"xhigh\"",
                ["model"] = "\"deepseek-v4.1-flash\""
            }
        };

        var modified = baseline.PurgePoisonedBaselines();

        Assert.True(modified);
        Assert.False(baseline.BaselineValues.ContainsKey("model_catalog_json"));
        Assert.False(baseline.BaselineValues.ContainsKey("model_context_window"));
        Assert.False(baseline.BaselineValues.ContainsKey("model_reasoning_effort"));
        Assert.False(baseline.BaselineValues.ContainsKey("model"));
    }
}
