using CodexSwitcher.App.Dialogs;
using CodexSwitcher.App.Features.Providers;
using CodexSwitcher.App.Shell.State;
using CodexSwitcher.App.ViewModels;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Security.Dpapi;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.Core.Tests;

public sealed class ApiProvidersViewModelSelectionTests
{
    [Fact]
    public async Task EditingInactiveProviderPersistsAndUpdatesCardWithoutMutatingConfigOrSwitching()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var paths = new AppPaths(temp.Root, Path.Combine(temp.Root, ".codex"));
        paths.EnsureDirectories();
        var config = "model_provider = \"openai\"\nmodel = \"gpt-5.6-sol\"\n";
        fs.WriteAllTextAtomic(paths.Codex.ConfigTomlPath, config);

        var providerStore = new ApiProviderStore(fs, paths.ApiProvidersPath);
        var profile = CreateProfile();
        providerStore.Save(profile);
        var dialog = new FakeProviderDialogService
        {
            EditResult = CreateEditResult(profile, "gpt-6-luna"),
        };
        var switchService = new RecordingTargetSwitchService(providerStore);
        using var viewModel = CreateViewModel(temp.Root, fs, paths, providerStore, dialog, switchService);
        var card = new ApiProviderItemViewModel(profile, descriptor: null, hasSecret: true, isTargetActive: false);
        viewModel.Items.Add(card);

        await viewModel.EditApiProviderAsync(card);

        Assert.Equal("gpt-6-luna", card.SelectedModel);
        var saved = providerStore.GetById(profile.Id);
        Assert.NotNull(saved);
        Assert.Equal("gpt-6-luna", saved.ModelInventory!.SelectedModel);
        Assert.Equal("gpt-6-luna", saved.SelectedModel);
        Assert.Equal(config, fs.ReadAllText(paths.Codex.ConfigTomlPath));
        Assert.Equal(0, switchService.CallCount);
    }

    [Fact]
    public async Task EditingActiveProviderUsesNormalSwitchWithInventorySelection()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var paths = new AppPaths(temp.Root, Path.Combine(temp.Root, ".codex"));
        paths.EnsureDirectories();
        var providerStore = new ApiProviderStore(fs, paths.ApiProvidersPath);
        var profile = CreateProfile();
        providerStore.Save(profile);
        var dialog = new FakeProviderDialogService
        {
            EditResult = CreateEditResult(profile, "gpt-6-luna"),
        };
        var switchService = new RecordingTargetSwitchService(providerStore);
        using var viewModel = CreateViewModel(temp.Root, fs, paths, providerStore, dialog, switchService);
        var card = new ApiProviderItemViewModel(profile, descriptor: null, hasSecret: true, isTargetActive: true);
        viewModel.Items.Add(card);

        await viewModel.EditApiProviderAsync(card);

        Assert.Equal(1, switchService.CallCount);
        Assert.Equal(profile.Id, switchService.LastProfileId);
        Assert.Equal("gpt-6-luna", switchService.ModelAtSwitch);
        Assert.Equal("gpt-6-luna", card.SelectedModel);
    }

    [Fact]
    public async Task FailedActiveReconciliationRestoresPersistedDefaultAndCard()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var paths = new AppPaths(temp.Root, Path.Combine(temp.Root, ".codex"));
        paths.EnsureDirectories();
        const string config = "model_provider = \"openai\"\nmodel = \"gpt-5.6-sol\"\n";
        fs.WriteAllTextAtomic(paths.Codex.ConfigTomlPath, config);
        var providerStore = new ApiProviderStore(fs, paths.ApiProvidersPath);
        var profile = CreateProfile();
        providerStore.Save(profile);
        var dialog = new FakeProviderDialogService
        {
            EditResult = CreateEditResult(profile, "gpt-6-luna"),
        };
        var switchService = new RecordingTargetSwitchService(providerStore)
        {
            Outcome = TargetSwitchOutcome.Failed,
        };
        using var viewModel = CreateViewModel(temp.Root, fs, paths, providerStore, dialog, switchService);
        var card = new ApiProviderItemViewModel(profile, descriptor: null, hasSecret: true, isTargetActive: true);
        viewModel.Items.Add(card);

        await viewModel.EditApiProviderAsync(card);

        Assert.Equal(1, switchService.CallCount);
        Assert.Equal("gpt-6-luna", switchService.ModelAtSwitch);
        Assert.Equal("gpt-5.6-sol", card.SelectedModel);
        var restored = providerStore.GetById(profile.Id);
        Assert.NotNull(restored);
        Assert.Equal("gpt-5.6-sol", restored.ModelInventory!.SelectedModel);
        Assert.Equal("gpt-5.6-sol", restored.SelectedModel);
        Assert.Equal(config, fs.ReadAllText(paths.Codex.ConfigTomlPath));
    }

    private static ApiProvidersViewModel CreateViewModel(
        string root,
        IFileSystem fs,
        AppPaths paths,
        IApiProviderStore providerStore,
        IProviderDialogService dialog,
        ICodexTargetSwitchService switchService)
    {
        var clock = new FakeClock();
        var apiKeyStore = new FakeApiKeySecretStore();
        var profileStore = new ProfileStore(fs, paths.ProfilesPath);
        var vault = new VaultService(new DpapiSecretProtector(), fs, paths.VaultDir);
        var profileService = new ProfileService(
            vault,
            profileStore,
            new ReconciliationService(fs, paths.Codex),
            fs,
            paths.Codex,
            clock,
            new FakeAudit());

        return new ApiProvidersViewModel(
            providerStore,
            new NoopModelRefreshService(),
            apiKeyStore,
            switchService,
            new NoopThreadHandoffService(),
            new FakeProviderCatalogService(),
            new ProviderModelCache(),
            profileService,
            dialog,
            clock,
            new AppSettings { AlwaysConfirmSwitch = false },
            new AppNotificationService(),
            new AppBusyService(),
            dispatcher: ImmediateUiDispatcher.Instance);
    }

    private static ApiProviderProfile CreateProfile()
    {
        var id = Guid.NewGuid();
        return new ApiProviderProfile
        {
            Id = id,
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(id),
            CatalogProviderId = "router-cheap",
            Nickname = "Router.Cheap",
            BaseUrl = "https://router.cheap/v1",
            SelectedRouteId = "primary",
            SelectedModel = "gpt-5.6-sol",
            ModelInventory = new ApiProviderModelInventory
            {
                SelectedModel = "gpt-5.6-sol",
                Models =
                [
                    new ApiProviderModelItem
                    {
                        Slug = "gpt-5.6-sol",
                        DisplayName = "GPT 5.6 Sol",
                        Enabled = true,
                        DiscoverySource = ModelDiscoverySource.Discovered,
                        Availability = ModelAvailability.Reported,
                    },
                    new ApiProviderModelItem
                    {
                        Slug = "gpt-6-luna",
                        DisplayName = "GPT 6 Luna",
                        Enabled = true,
                        DiscoverySource = ModelDiscoverySource.Discovered,
                        Availability = ModelAvailability.Reported,
                    },
                ],
            },
        };
    }

    private static EditApiProviderResult CreateEditResult(ApiProviderProfile profile, string selectedModel) => new()
    {
        Nickname = profile.Nickname,
        BaseUrl = profile.BaseUrl,
        SelectedRouteId = profile.SelectedRouteId ?? "primary",
        // Deliberately stale compatibility field: inventory selection must win.
        SelectedModel = profile.SelectedModel ?? "gpt-5.6-sol",
        ModelInventory = new ApiProviderModelInventory
        {
            SelectedModel = selectedModel,
            Models = profile.ModelInventory!.Models.Select(model => new ApiProviderModelItem
            {
                Slug = model.Slug,
                DisplayName = model.DisplayName,
                Enabled = model.Enabled,
                DiscoverySource = model.DiscoverySource,
                Availability = model.Availability,
            }).ToList(),
        },
    };

    private sealed class FakeApiKeySecretStore : IApiKeySecretStore
    {
        public bool HasApiKey(Guid profileId) => true;
        public void SaveApiKey(Guid profileId, string apiKey) { }
        public string? GetApiKey(Guid profileId) => null;
        public bool DeleteApiKey(Guid profileId) => false;
        public void CloneApiKey(Guid sourceProfileId, Guid targetProfileId) { }
    }

    private sealed class NoopModelRefreshService : IApiModelInventoryRefreshService
    {
        public event EventHandler<ModelInventoryRefreshResult>? RefreshStateChanged
        {
            add { }
            remove { }
        }
        public ModelInventoryRefreshResult? GetLatestResult(Guid profileId) => null;
        public Task<ModelInventoryRefreshResult> RefreshAsync(Guid profileId, RefreshModelsOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingTargetSwitchService(IApiProviderStore profiles) : ICodexTargetSwitchService
    {
        public int CallCount { get; private set; }
        public Guid? LastProfileId { get; private set; }
        public string? ModelAtSwitch { get; private set; }
        public TargetSwitchOutcome Outcome { get; init; } = TargetSwitchOutcome.Success;

        public Task<TargetSwitchResult> SwitchToApiProviderAsync(Guid apiProfileId, SwitchExecutionOptions options, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastProfileId = apiProfileId;
            ModelAtSwitch = profiles.GetById(apiProfileId)?.GetEffectiveSelectedModel();
            var profile = profiles.GetById(apiProfileId)!;
            return Task.FromResult(new TargetSwitchResult(Outcome, "switch result", new ActiveTarget.Api(profile)));
        }

        public Task<TargetSwitchResult> SwitchToChatGptAsync(Guid? targetChatGptProfileId, List<ProfileMetadata> allChatGptProfiles, SwitchExecutionOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TargetSwitchResult> SwitchApiRouteAsync(Guid apiProfileId, string routeId, string newBaseUrl, SwitchExecutionOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopThreadHandoffService : ICodexThreadHandoffService
    {
        public Task<IReadOnlyList<CodexThreadSummary>> ListThreadsAsync(int limit = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CodexThreadSummary>>([]);

        public Task<ThreadForkResult> ForkThreadAsync(string sourceThreadId, string targetModelProvider, string targetModel, string? newName = null, Guid? targetProfileId = null, string? targetCatalogPath = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ThreadForkResult> StartFreshThreadAsync(string targetModelProvider, string targetModel, string? cwd = null, string? name = null, Guid? targetProfileId = null, string? targetCatalogPath = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ThreadCompatibilityAssessment> AssessThreadCompatibilityAsync(string threadId, EffectiveToolPolicy targetPolicy, CancellationToken cancellationToken = default) =>
            Task.FromResult(ThreadCompatibilityAssessment.Compatible(threadId));
    }

    private sealed class FakeProviderCatalogService : IProviderCatalogService
    {
        public CatalogLoadResult CurrentResult => null!;
        public CatalogLoadResult Reload() => null!;
        public ProviderDescriptor? GetDescriptor(string? providerId) => null;
        public ProviderDescriptor? MatchDescriptor(string? hostOrUrl) => null;
    }

    private sealed class FakeProviderDialogService : IProviderDialogService
    {
        public EditApiProviderResult? EditResult { get; init; }
        public Task<bool> ConfirmAsync(string title, string message, string okText, bool destructive = false) => Task.FromResult(true);
        public Task<string?> PromptTextAsync(string title, string prompt, string initialValue, string okText) => Task.FromResult<string?>(null);
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
        public Task<AddApiProviderResult?> PromptAddApiProviderAsync(IReadOnlyList<ProviderDescriptor> descriptors) => Task.FromResult<AddApiProviderResult?>(null);
        public Task<EditApiProviderResult?> PromptEditApiProviderAsync(ApiProviderProfile profile, ProviderDescriptor? descriptor) => Task.FromResult(EditResult);
        public Task<string?> PromptRotateApiKeyAsync(string providerDisplayName) => Task.FromResult<string?>(null);
        public Task<CodexThreadSummary?> PromptContinueOnThreadAsync(IReadOnlyList<CodexThreadSummary> threads, string targetProviderName, string targetModel) => Task.FromResult<CodexThreadSummary?>(null);
        public Task<bool> PromptFreshThreadChoiceAsync(string threadTitle, IReadOnlyList<string> incompatibleFeatures, string targetProviderName, string targetModel) => Task.FromResult(false);
        public Task<bool> ConfirmSwitchToApiAsync(string providerName, string model, string route) => Task.FromResult(true);
    }
}
