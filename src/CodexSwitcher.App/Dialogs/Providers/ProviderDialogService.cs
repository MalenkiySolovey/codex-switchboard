using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;
using CodexSwitcher.Core.Security.Verification;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Codex.Usage;
using CodexSwitcher.Infra.Common.Logging;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Common.Time;
using CodexSwitcher.Infra.Providers.Inspection;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Scheduling;
using CodexSwitcher.Infra.Security.Dpapi;
using CodexSwitcher.Infra.Security.Hardening;
using CodexSwitcher.Infra.Security.Totp;
using CodexSwitcher.Infra.Settings;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodexSwitcher.App.Dialogs.Common;
using CodexSwitcher.App.Dialogs.Shared;
using CodexSwitcher.App.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.App.Dialogs.Providers;

/// <summary>
/// Implementation of dialogs specific to API Provider creation, editing, key rotation, thread handoff, and route switching.
/// </summary>
public sealed class ProviderDialogService : CommonDialogService, IProviderDialogService
{
    private readonly AppPaths _paths;
    private readonly CodexSwitcher.Core.Providers.Contracts.ICodexModelMetadataResolver _metadataResolver;

    public ProviderDialogService(
        IDialogHost host,
        AppPaths paths,
        CodexSwitcher.Core.Providers.Contracts.ICodexModelMetadataResolver? metadataResolver = null) : base(host)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _metadataResolver = metadataResolver ?? new CodexSwitcher.Core.Providers.Services.CodexModelMetadataResolver();
    }

    public async Task<AddApiProviderResult?> PromptAddApiProviderAsync(IReadOnlyList<ProviderDescriptor> descriptors)
    {
        var panel = new StackPanel { Spacing = 12, Width = 420 };

        var providerCombo = new ComboBox
        {
            Header = _loc.ProviderLabel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var customItem = new ComboBoxItem { Content = "(Custom Provider / OpenAI-compatible)", Tag = "custom" };
        providerCombo.Items.Add(customItem);

        foreach (var desc in descriptors)
        {
            var item = new ComboBoxItem
            {
                Content = $"{desc.DisplayName} ({desc.Id})",
                Tag = desc.Id
            };
            providerCombo.Items.Add(item);
        }

        var nicknameBox = new TextBox
        {
            Header = _loc.NameLabel,
            PlaceholderText = "Router.Cheap",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var passwordBox = new PasswordBox
        {
            Header = _loc.ApiKeyLabel,
            PlaceholderText = "sk-...",
            IsPasswordRevealButtonEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var routeCombo = new ComboBox
        {
            Header = _loc.RouteLabel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };

        var baseUrlBox = new TextBox
        {
            Header = _loc.BaseUrlLabel,
            PlaceholderText = "https://api.example.com/v1",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var modelBox = new TextBox
        {
            Header = _loc.ModelLabel,
            PlaceholderText = "openai/gpt-4o-mini",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var errorBar = new InfoBar
        {
            IsOpen = false,
            Severity = InfoBarSeverity.Error,
            IsClosable = true,
        };

        void UpdateForSelectedDescriptor(ProviderDescriptor? desc)
        {
            routeCombo.Items.Clear();
            if (desc is not null && desc.Routes.Count > 0)
            {
                routeCombo.Visibility = Visibility.Visible;
                foreach (var r in desc.Routes)
                {
                    routeCombo.Items.Add(new ComboBoxItem
                    {
                        Content = !string.IsNullOrWhiteSpace(r.Region) ? $"{r.DisplayName} ({r.Region})" : $"{r.DisplayName} ({r.Id})",
                        Tag = r.Id
                    });
                }
                routeCombo.SelectedIndex = 0;

                var defaultRoute = desc.Routes.FirstOrDefault(r => r.Id.Equals("primary", StringComparison.OrdinalIgnoreCase)) ?? desc.Routes[0];
                baseUrlBox.Text = defaultRoute.BaseUrl;
                nicknameBox.Text = desc.DisplayName;
                modelBox.Text = desc.Codex.DefaultModel;
            }
            else
            {
                routeCombo.Visibility = Visibility.Collapsed;
                if (string.IsNullOrWhiteSpace(nicknameBox.Text) || descriptors.Any(d => d.DisplayName == nicknameBox.Text))
                {
                    nicknameBox.Text = string.Empty;
                }
                baseUrlBox.Text = string.Empty;
                modelBox.Text = string.Empty;
            }
        }

        routeCombo.SelectionChanged += (_, _) =>
        {
            if (providerCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string id)
            {
                var desc = descriptors.FirstOrDefault(d => d.Id == id);
                if (desc is not null && routeCombo.SelectedItem is ComboBoxItem rcbi && rcbi.Tag is string routeId)
                {
                    var route = desc.Routes.FirstOrDefault(r => r.Id == routeId);
                    if (route is not null)
                    {
                        baseUrlBox.Text = route.BaseUrl;
                    }
                }
            }
        };

        providerCombo.SelectionChanged += (_, _) =>
        {
            if (providerCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string id)
            {
                var desc = descriptors.FirstOrDefault(d => d.Id == id);
                UpdateForSelectedDescriptor(desc);
            }
        };

        if (descriptors.Count > 0)
        {
            providerCombo.SelectedIndex = 1;
        }
        else
        {
            providerCombo.SelectedIndex = 0;
        }

        panel.Children.Add(providerCombo);
        panel.Children.Add(nicknameBox);
        panel.Children.Add(passwordBox);
        panel.Children.Add(routeCombo);
        panel.Children.Add(baseUrlBox);
        panel.Children.Add(modelBox);

        var advanced = new ApiProviderAdvancedSettingsControlGroup(null, null, _loc, _metadataResolver);
        advanced.AttachTo(panel);

        modelBox.TextChanged += (_, _) => advanced.UpdateModelContextHint(modelBox.Text?.Trim());
        advanced.UpdateModelContextHint(modelBox.Text?.Trim());

        panel.Children.Add(errorBar);

        var scrollViewer = new ScrollViewer
        {
            Content = panel,
            MaxHeight = 560,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var dialog = new ContentDialog
        {
            Title = _loc.ProviderDialogTitle,
            Content = scrollViewer,
            PrimaryButtonText = _loc.SaveAndSwitch,
            SecondaryButtonText = _loc.SaveOnly,
            CloseButtonText = _loc.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        bool isPrimary = false;
        bool confirmed = false;
        CodexModelOverrides? extractedModel = null;
        ApiProviderTransportOverrides? extractedTransport = null;

        dialog.PrimaryButtonClick += (s, args) =>
        {
            var key = passwordBox.Password?.Trim();
            var name = nicknameBox.Text?.Trim();
            var url = baseUrlBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                errorBar.Title = _loc.ApiKeyRequired;
                errorBar.IsOpen = true;
                args.Cancel = true;
                return;
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                errorBar.Title = "Name is required.";
                errorBar.IsOpen = true;
                args.Cancel = true;
                return;
            }
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                errorBar.Title = "Valid Base URL is required.";
                errorBar.IsOpen = true;
                args.Cancel = true;
                return;
            }

            var (m, t, err) = advanced.ValidateAndExtract(_loc, modelBox.Text?.Trim());
            if (err != null)
            {
                errorBar.Title = err;
                errorBar.IsOpen = true;
                args.Cancel = true;
                return;
            }

            extractedModel = m;
            extractedTransport = t;
            isPrimary = true;
            confirmed = true;
        };

        dialog.SecondaryButtonClick += (s, args) =>
        {
            var key = passwordBox.Password?.Trim();
            var name = nicknameBox.Text?.Trim();
            var url = baseUrlBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                errorBar.Title = _loc.ApiKeyRequired;
                errorBar.IsOpen = true;
                args.Cancel = true;
                return;
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                errorBar.Title = "Name is required.";
                errorBar.IsOpen = true;
                args.Cancel = true;
                return;
            }
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                errorBar.Title = "Valid Base URL is required.";
                errorBar.IsOpen = true;
                args.Cancel = true;
                return;
            }

            var (m, t, err) = advanced.ValidateAndExtract(_loc, modelBox.Text?.Trim());
            if (err != null)
            {
                errorBar.Title = err;
                errorBar.IsOpen = true;
                args.Cancel = true;
                return;
            }

            extractedModel = m;
            extractedTransport = t;
            isPrimary = false;
            confirmed = true;
        };

        try
        {
            var res = await dialog.ShowAsync();
            if (confirmed)
            {
                var key = passwordBox.Password?.Trim() ?? string.Empty;
                var selectedTag = (providerCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "custom";
                var selectedRouteTag = (routeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "primary";

                return new AddApiProviderResult
                {
                    CatalogProviderId = selectedTag,
                    Nickname = nicknameBox.Text.Trim(),
                    ApiKey = key,
                    BaseUrl = baseUrlBox.Text.Trim(),
                    SelectedRouteId = selectedRouteTag,
                    SelectedModel = modelBox.Text?.Trim() ?? string.Empty,
                    ModelOverrides = extractedModel,
                    TransportOverrides = extractedTransport,
                    SaveAndSwitch = isPrimary,
                };
            }
            return null;
        }
        finally
        {
            passwordBox.Password = string.Empty;
        }
    }


    public async Task<EditApiProviderResult?> PromptEditApiProviderAsync(ApiProviderProfile profile, ProviderDescriptor? descriptor)
    {
        var panel = new StackPanel { Spacing = 12, Width = 400 };

        var nicknameBox = new TextBox
        {
            Header = _loc.NameLabel,
            Text = profile.Nickname,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var routeCombo = new ComboBox
        {
            Header = _loc.RouteLabel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };

        var baseUrlBox = new TextBox
        {
            Header = _loc.BaseUrlLabel,
            Text = profile.BaseUrl,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        if (descriptor is not null && descriptor.Routes.Count > 0)
        {
            routeCombo.Visibility = Visibility.Visible;
            foreach (var r in descriptor.Routes)
            {
                routeCombo.Items.Add(new ComboBoxItem
                {
                    Content = !string.IsNullOrWhiteSpace(r.Region) ? $"{r.DisplayName} ({r.Region})" : $"{r.DisplayName} ({r.Id})",
                    Tag = r.Id,
                    IsSelected = r.Id.Equals(profile.SelectedRouteId, StringComparison.OrdinalIgnoreCase)
                });
            }
            if (routeCombo.SelectedIndex < 0) routeCombo.SelectedIndex = 0;

            routeCombo.SelectionChanged += (_, _) =>
            {
                if (routeCombo.SelectedItem is ComboBoxItem rcbi && rcbi.Tag is string routeId)
                {
                    var route = descriptor.Routes.FirstOrDefault(r => r.Id == routeId);
                    if (route is not null)
                    {
                        baseUrlBox.Text = route.BaseUrl;
                    }
                }
            };
        }

        var modelBox = new TextBox
        {
            Header = _loc.ModelLabel,
            Text = profile.SelectedModel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var errorBar = new InfoBar
        {
            IsOpen = false,
            Severity = InfoBarSeverity.Error,
            IsClosable = true,
        };

        panel.Children.Add(nicknameBox);
        panel.Children.Add(routeCombo);
        panel.Children.Add(baseUrlBox);
        panel.Children.Add(modelBox);

        var advanced = new ApiProviderAdvancedSettingsControlGroup(profile.ModelOverrides, profile.TransportOverrides, _loc, _metadataResolver);
        advanced.AttachTo(panel);

        modelBox.TextChanged += (_, _) => advanced.UpdateModelContextHint(modelBox.Text?.Trim());
        advanced.UpdateModelContextHint(modelBox.Text?.Trim());

        panel.Children.Add(errorBar);

        var scrollViewer = new ScrollViewer
        {
            Content = panel,
            MaxHeight = 560,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var dialog = new ContentDialog
        {
            Title = _loc.EditProviderDialogTitle,
            Content = scrollViewer,
            PrimaryButtonText = _loc.Save,
            CloseButtonText = _loc.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        CodexModelOverrides? extractedModel = null;
        ApiProviderTransportOverrides? extractedTransport = null;

        dialog.PrimaryButtonClick += (s, args) =>
        {
            if (string.IsNullOrWhiteSpace(nicknameBox.Text))
            {
                errorBar.Title = "Name is required.";
                errorBar.IsOpen = true;
                args.Cancel = true;
                return;
            }
            if (string.IsNullOrWhiteSpace(baseUrlBox.Text) || !Uri.TryCreate(baseUrlBox.Text.Trim(), UriKind.Absolute, out _))
            {
                errorBar.Title = "Valid Base URL is required.";
                errorBar.IsOpen = true;
                args.Cancel = true;
                return;
            }

            var (m, t, err) = advanced.ValidateAndExtract(_loc, modelBox.Text?.Trim());
            if (err != null)
            {
                errorBar.Title = err;
                errorBar.IsOpen = true;
                args.Cancel = true;
                return;
            }

            extractedModel = m;
            extractedTransport = t;
        };

        var res = await dialog.ShowAsync();
        if (res == ContentDialogResult.Primary)
        {
            var routeTag = (routeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? profile.SelectedRouteId ?? string.Empty;
            return new EditApiProviderResult
            {
                Nickname = nicknameBox.Text.Trim(),
                BaseUrl = baseUrlBox.Text.Trim(),
                SelectedRouteId = routeTag,
                SelectedModel = modelBox.Text?.Trim() ?? string.Empty,
                ModelOverrides = extractedModel,
                TransportOverrides = extractedTransport,
            };
        }
        return null;
    }


    public async Task<string?> PromptRotateApiKeyAsync(string providerDisplayName)
    {
        var panel = new StackPanel { Spacing = 10, Width = 380 };

        panel.Children.Add(new TextBlock
        {
            Text = _loc.RotateKeyDialogTitle + ": " + providerDisplayName,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        });

        var passwordBox = new PasswordBox
        {
            Header = _loc.ApiKeyLabel,
            PlaceholderText = "sk-...",
            IsPasswordRevealButtonEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var errorBar = new InfoBar
        {
            IsOpen = false,
            Severity = InfoBarSeverity.Error,
            IsClosable = true,
        };

        panel.Children.Add(passwordBox);
        panel.Children.Add(errorBar);

        var dialog = new ContentDialog
        {
            Title = _loc.RotateKeyDialogTitle,
            Content = panel,
            PrimaryButtonText = _loc.Save,
            CloseButtonText = _loc.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        dialog.PrimaryButtonClick += (s, args) =>
        {
            if (string.IsNullOrWhiteSpace(passwordBox.Password?.Trim()))
            {
                errorBar.Title = _loc.ApiKeyRequired;
                errorBar.IsOpen = true;
                args.Cancel = true;
            }
        };

        try
        {
            var res = await dialog.ShowAsync();
            if (res == ContentDialogResult.Primary)
            {
                return passwordBox.Password?.Trim();
            }
            return null;
        }
        finally
        {
            passwordBox.Password = string.Empty;
        }
    }


    public async Task<CodexThreadSummary?> PromptContinueOnThreadAsync(IReadOnlyList<CodexThreadSummary> threads, string targetProviderName, string targetModel)
    {
        var panel = new StackPanel { Spacing = 10, Width = 440, MaxHeight = 420 };

        panel.Children.Add(new TextBlock
        {
            Text = $"{_loc.TargetProvider}: {targetProviderName} ({targetModel})",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(new TextBlock
        {
            Text = _loc.ThreadPickerSubtitle,
            Opacity = 0.8,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        var listView = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 280,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        foreach (var t in threads)
        {
            var title = !string.IsNullOrWhiteSpace(t.Name) ? t.Name : t.Id;
            var prov = !string.IsNullOrWhiteSpace(t.ModelProvider) ? t.ModelProvider : "ChatGPT";
            var model = !string.IsNullOrWhiteSpace(t.Model) ? t.Model : "-";
            var dateStr = t.UpdatedAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);

            var itemStack = new StackPanel { Spacing = 2, Padding = new Thickness(0, 4, 0, 4) };
            itemStack.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            });
            itemStack.Children.Add(new TextBlock
            {
                Text = $"{prov} · {model} · {dateStr}",
                FontSize = 11,
                Opacity = 0.7,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            var itemNode = new ListViewItem
            {
                Content = itemStack,
                Tag = t,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            listView.Items.Add(itemNode);
        }

        if (listView.Items.Count > 0)
        {
            listView.SelectedIndex = 0;
        }

        panel.Children.Add(listView);

        var dialog = new ContentDialog
        {
            Title = _loc.ThreadPickerTitle,
            Content = panel,
            PrimaryButtonText = _loc.ForkAndSwitch,
            CloseButtonText = _loc.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        var res = await dialog.ShowAsync();
        if (res == ContentDialogResult.Primary && listView.SelectedItem is ListViewItem selectedItem && selectedItem.Tag is CodexThreadSummary selected)
        {
            return selected;
        }
        return null;
    }


    public async Task<bool> ConfirmSwitchToApiAsync(string providerName, string model, string route)
    {
        var panel = new StackPanel { Spacing = 8, Width = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Switch active Codex routing to {providerName}?",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(new TextBlock
        {
            Text = $"Model: {model}\nRoute: {route}",
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(new TextBlock
        {
            Text = _loc.IdeNote,
            Opacity = 0.7,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            Title = _loc.SwitchToProvider,
            Content = panel,
            PrimaryButtonText = _loc.Confirm,
            CloseButtonText = _loc.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private sealed class ApiProviderAdvancedSettingsControlGroup
    {
        private readonly CodexSwitcher.Core.Providers.Contracts.ICodexModelMetadataResolver _metadataResolver;
        private string? _currentModelSlug;

        public ComboBox ContextPresetCombo { get; }
        public TextBox CustomContextBox { get; }
        public TextBlock UnverifiedContextWarning { get; }
        public TextBox AutoCompactBox { get; }
        public ComboBox CompactScopeCombo { get; }
        public ComboBox ReasoningEffortCombo { get; }
        public ComboBox ReasoningSummaryCombo { get; }
        public ComboBox VerbosityCombo { get; }
        public TextBox ToolOutputLimitBox { get; }
        public TextBox RequestRetriesBox { get; }
        public TextBox StreamRetriesBox { get; }
        public TextBox StreamTimeoutBox { get; }
        public TextBox WsTimeoutBox { get; }
        public CheckBox WebSocketsCheck { get; }
        public CheckBox StandaloneWebSearchCheck { get; }
        public TextBox HeadersBox { get; }
        public TextBox QueryParamsBox { get; }
        public Expander Expander { get; }

        public ApiProviderAdvancedSettingsControlGroup(
            CodexModelOverrides? initialModel,
            ApiProviderTransportOverrides? initialTransport,
            Strings loc,
            CodexSwitcher.Core.Providers.Contracts.ICodexModelMetadataResolver? metadataResolver = null)
        {
            _metadataResolver = metadataResolver ?? new CodexSwitcher.Core.Providers.Services.CodexModelMetadataResolver();

            // Context Window Presets
            ContextPresetCombo = new ComboBox
            {
                Header = loc.ContextWindowLabel,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            ContextPresetCombo.Items.Add(new ComboBoxItem { Content = "Auto (Codex / Catalog default)", Tag = "auto" });
            ContextPresetCombo.Items.Add(new ComboBoxItem { Content = "256k tokens (262,144)", Tag = "262144" });
            ContextPresetCombo.Items.Add(new ComboBoxItem { Content = "500k tokens (500,000)", Tag = "500000" });
            ContextPresetCombo.Items.Add(new ComboBoxItem { Content = "1M tokens (1,000,000)", Tag = "1000000" });
            ContextPresetCombo.Items.Add(new ComboBoxItem { Content = "Custom...", Tag = "custom" });

            CustomContextBox = new TextBox
            {
                Header = "Custom Context Window (tokens)",
                PlaceholderText = "e.g. 500000",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Visibility = Visibility.Collapsed,
            };

            UnverifiedContextWarning = new TextBlock
            {
                Text = "Provider maximum is not verified for this model. Custom context will be applied as an unverified override.",
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 230, 149, 0)),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 2, 0, 4)
            };

            var initialTokens = initialModel?.ContextWindowTokens;
            if (initialTokens is null)
            {
                ContextPresetCombo.SelectedIndex = 0;
            }
            else if (initialTokens == 262144)
            {
                ContextPresetCombo.SelectedIndex = 1;
            }
            else if (initialTokens == 500000)
            {
                ContextPresetCombo.SelectedIndex = 2;
            }
            else if (initialTokens == 1000000)
            {
                ContextPresetCombo.SelectedIndex = 3;
            }
            else
            {
                ContextPresetCombo.SelectedIndex = 4;
                CustomContextBox.Text = initialTokens.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                CustomContextBox.Visibility = Visibility.Visible;
            }

            ContextPresetCombo.SelectionChanged += (_, _) =>
            {
                if (ContextPresetCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                {
                    CustomContextBox.Visibility = tag == "custom" ? Visibility.Visible : Visibility.Collapsed;
                    UpdateWarningVisibility();
                }
            };
            CustomContextBox.TextChanged += (_, _) => UpdateWarningVisibility();

            // Auto-compact Token Limit
            AutoCompactBox = new TextBox
            {
                Header = loc.AutoCompactLabel,
                PlaceholderText = "e.g. 200000 (empty = default)",
                Text = initialModel?.AutoCompactTokenLimit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            CompactScopeCombo = new ComboBox
            {
                Header = "Auto-compact Scope",
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            CompactScopeCombo.Items.Add(new ComboBoxItem { Content = "Default", Tag = CompactLimitScope.Default });
            CompactScopeCombo.Items.Add(new ComboBoxItem { Content = "Total context (total)", Tag = CompactLimitScope.Total });
            CompactScopeCombo.Items.Add(new ComboBoxItem { Content = "Body after prefix (body_after_prefix)", Tag = CompactLimitScope.BodyAfterPrefix });
            CompactScopeCombo.SelectedIndex = initialModel?.AutoCompactTokenLimitScope switch
            {
                CompactLimitScope.Total => 1,
                CompactLimitScope.BodyAfterPrefix => 2,
                _ => 0
            };

            // Reasoning Effort
            ReasoningEffortCombo = new ComboBox
            {
                Header = "Reasoning Effort",
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            ReasoningEffortCombo.Items.Add(new ComboBoxItem { Content = "Default", Tag = CodexReasoningEffort.Default });
            ReasoningEffortCombo.Items.Add(new ComboBoxItem { Content = "None", Tag = CodexReasoningEffort.None });
            ReasoningEffortCombo.Items.Add(new ComboBoxItem { Content = "Minimal", Tag = CodexReasoningEffort.Minimal });
            ReasoningEffortCombo.Items.Add(new ComboBoxItem { Content = "Low", Tag = CodexReasoningEffort.Low });
            ReasoningEffortCombo.Items.Add(new ComboBoxItem { Content = "Medium", Tag = CodexReasoningEffort.Medium });
            ReasoningEffortCombo.Items.Add(new ComboBoxItem { Content = "High", Tag = CodexReasoningEffort.High });
            ReasoningEffortCombo.Items.Add(new ComboBoxItem { Content = "XHigh", Tag = CodexReasoningEffort.XHigh });
            ReasoningEffortCombo.SelectedIndex = (int)(initialModel?.ReasoningEffort ?? CodexReasoningEffort.Default);

            // Reasoning Summary
            ReasoningSummaryCombo = new ComboBox
            {
                Header = "Reasoning Summary",
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            ReasoningSummaryCombo.Items.Add(new ComboBoxItem { Content = "Default", Tag = CodexReasoningSummary.Default });
            ReasoningSummaryCombo.Items.Add(new ComboBoxItem { Content = "Auto", Tag = CodexReasoningSummary.Auto });
            ReasoningSummaryCombo.Items.Add(new ComboBoxItem { Content = "Concise", Tag = CodexReasoningSummary.Concise });
            ReasoningSummaryCombo.Items.Add(new ComboBoxItem { Content = "Detailed", Tag = CodexReasoningSummary.Detailed });
            ReasoningSummaryCombo.Items.Add(new ComboBoxItem { Content = "None", Tag = CodexReasoningSummary.None });
            ReasoningSummaryCombo.SelectedIndex = (int)(initialModel?.ReasoningSummary ?? CodexReasoningSummary.Default);

            // Verbosity
            VerbosityCombo = new ComboBox
            {
                Header = "Output Verbosity",
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            VerbosityCombo.Items.Add(new ComboBoxItem { Content = "Default", Tag = CodexVerbosity.Default });
            VerbosityCombo.Items.Add(new ComboBoxItem { Content = "Low", Tag = CodexVerbosity.Low });
            VerbosityCombo.Items.Add(new ComboBoxItem { Content = "Medium", Tag = CodexVerbosity.Medium });
            VerbosityCombo.Items.Add(new ComboBoxItem { Content = "High", Tag = CodexVerbosity.High });
            VerbosityCombo.SelectedIndex = (int)(initialModel?.Verbosity ?? CodexVerbosity.Default);

            // Tool Output Limit
            ToolOutputLimitBox = new TextBox
            {
                Header = "Tool Output Token Limit",
                PlaceholderText = "e.g. 4000 (empty = default)",
                Text = initialModel?.ToolOutputTokenLimit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            // Retries & Timeouts
            RequestRetriesBox = new TextBox
            {
                Header = "Request Max Retries",
                PlaceholderText = "e.g. 3 (empty = default)",
                Text = initialTransport?.RequestMaxRetries?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            StreamRetriesBox = new TextBox
            {
                Header = "Stream Max Retries",
                PlaceholderText = "e.g. 5 (empty = default)",
                Text = initialTransport?.StreamMaxRetries?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            StreamTimeoutBox = new TextBox
            {
                Header = "Stream Idle Timeout (ms)",
                PlaceholderText = "e.g. 60000 (empty = default)",
                Text = initialTransport?.StreamIdleTimeoutMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            WsTimeoutBox = new TextBox
            {
                Header = "WebSocket Connect Timeout (ms)",
                PlaceholderText = "e.g. 15000 (empty = default)",
                Text = initialTransport?.WebSocketConnectTimeoutMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            // CheckBoxes
            WebSocketsCheck = new CheckBox
            {
                Content = "Supports WebSockets (wire_api = responses)",
                IsThreeState = true,
                IsChecked = initialTransport?.SupportsWebSockets,
            };

            StandaloneWebSearchCheck = new CheckBox
            {
                Content = "Supports Standalone Web Search",
                IsThreeState = true,
                IsChecked = initialTransport?.SupportsStandaloneWebSearch,
            };

            // Custom HTTP Headers
            var initialHeaders = initialTransport?.HttpHeaders != null
                ? string.Join(Environment.NewLine, initialTransport.HttpHeaders.Select(kv => $"{kv.Key}: {kv.Value}"))
                : string.Empty;
            HeadersBox = new TextBox
            {
                Header = "Custom HTTP Headers (Name: Value, one per line)",
                PlaceholderText = "OpenAI-Organization: org-123",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 60,
                Text = initialHeaders,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            // Custom Query Params
            var initialParams = initialTransport?.QueryParams != null
                ? string.Join(Environment.NewLine, initialTransport.QueryParams.Select(kv => $"{kv.Key}={kv.Value}"))
                : string.Empty;
            QueryParamsBox = new TextBox
            {
                Header = "Custom Query Parameters (Key=Value, one per line)",
                PlaceholderText = "api-version=2024-02-01",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 60,
                Text = initialParams,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var expanderPanel = new StackPanel { Spacing = 10, Padding = new Thickness(4) };
            expanderPanel.Children.Add(ReasoningEffortCombo);
            expanderPanel.Children.Add(ReasoningSummaryCombo);
            expanderPanel.Children.Add(VerbosityCombo);
            expanderPanel.Children.Add(ToolOutputLimitBox);
            expanderPanel.Children.Add(RequestRetriesBox);
            expanderPanel.Children.Add(StreamRetriesBox);
            expanderPanel.Children.Add(StreamTimeoutBox);
            expanderPanel.Children.Add(WsTimeoutBox);
            expanderPanel.Children.Add(WebSocketsCheck);
            expanderPanel.Children.Add(StandaloneWebSearchCheck);
            expanderPanel.Children.Add(HeadersBox);
            expanderPanel.Children.Add(QueryParamsBox);

            Expander = new Expander
            {
                Header = loc.AdvancedSettingsLabel,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsExpanded = false,
                Content = expanderPanel,
            };
        }

        public void UpdateModelContextHint(string? modelSlug)
        {
            _currentModelSlug = modelSlug;
            UpdateWarningVisibility();
        }

        private void UpdateWarningVisibility()
        {
            var fact = _metadataResolver.ResolveLimitFact(_currentModelSlug ?? string.Empty);
            if (!fact.IsKnown)
            {
                var isCustom = (ContextPresetCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag && tag != "auto");
                UnverifiedContextWarning.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                UnverifiedContextWarning.Visibility = Visibility.Collapsed;
            }
        }

        public void AttachTo(Panel parent)
        {
            parent.Children.Add(ContextPresetCombo);
            parent.Children.Add(CustomContextBox);
            parent.Children.Add(UnverifiedContextWarning);
            parent.Children.Add(AutoCompactBox);
            parent.Children.Add(CompactScopeCombo);
            parent.Children.Add(Expander);
        }

        public (CodexModelOverrides? Model, ApiProviderTransportOverrides? Transport, string? Error) ValidateAndExtract(Strings loc, string? selectedModel = null)
        {
            long? contextTokens = null;
            if (ContextPresetCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                if (tag == "auto")
                {
                    contextTokens = null;
                }
                else if (long.TryParse(tag, out var presetVal))
                {
                    contextTokens = presetVal;
                }
                else if (tag == "custom")
                {
                    var customText = CustomContextBox.Text?.Trim();
                    if (!string.IsNullOrWhiteSpace(customText))
                    {
                        if (!long.TryParse(customText, out var parsedCustom) || parsedCustom <= 0)
                        {
                            return (null, null, "Custom Context Window must be a positive integer.");
                        }
                        contextTokens = parsedCustom;
                    }
                }
            }

            var effectiveModelSlug = selectedModel ?? _currentModelSlug ?? string.Empty;
            var fact = _metadataResolver.ResolveLimitFact(effectiveModelSlug);
            if (fact.IsKnown && fact.DocumentedMaxContext.HasValue)
            {
                if (contextTokens.HasValue && contextTokens.Value > fact.DocumentedMaxContext.Value)
                {
                    return (null, null, $"Context window ({contextTokens.Value:N0} tokens) exceeds documented maximum of {fact.DocumentedMaxContext.Value:N0} for {fact.ModelSlug} ({fact.SourceDescription}).");
                }
            }

            long? compactTokens = null;
            var compactText = AutoCompactBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(compactText))
            {
                if (!long.TryParse(compactText, out var parsedCompact) || parsedCompact <= 0)
                {
                    return (null, null, "Auto-compact token limit must be a positive integer.");
                }
                if (contextTokens.HasValue && parsedCompact > contextTokens.Value)
                {
                    return (null, null, "Auto-compact token limit cannot exceed context window.");
                }
                if (fact.IsKnown && fact.DocumentedMaxContext.HasValue && parsedCompact > fact.DocumentedMaxContext.Value)
                {
                    return (null, null, $"Auto-compact token limit ({parsedCompact:N0} tokens) exceeds documented maximum of {fact.DocumentedMaxContext.Value:N0} for {fact.ModelSlug}.");
                }
                compactTokens = parsedCompact;
            }

            var compactScope = (CompactLimitScope)(CompactScopeCombo.SelectedIndex >= 0 ? CompactScopeCombo.SelectedIndex : 0);
            var reasoningEffort = (CodexReasoningEffort)(ReasoningEffortCombo.SelectedIndex >= 0 ? ReasoningEffortCombo.SelectedIndex : 0);
            var reasoningSummary = (CodexReasoningSummary)(ReasoningSummaryCombo.SelectedIndex >= 0 ? ReasoningSummaryCombo.SelectedIndex : 0);
            var verbosity = (CodexVerbosity)(VerbosityCombo.SelectedIndex >= 0 ? VerbosityCombo.SelectedIndex : 0);

            int? toolLimit = null;
            var toolLimitText = ToolOutputLimitBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(toolLimitText))
            {
                if (!int.TryParse(toolLimitText, out var parsedTool) || parsedTool <= 0)
                {
                    return (null, null, "Tool Output Token Limit must be a positive integer.");
                }
                toolLimit = parsedTool;
            }

            ulong? reqRetries = null;
            var reqRetriesText = RequestRetriesBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(reqRetriesText))
            {
                if (!ulong.TryParse(reqRetriesText, out var parsedReq))
                {
                    return (null, null, "Request Max Retries must be a non-negative integer.");
                }
                reqRetries = parsedReq;
            }

            ulong? streamRetries = null;
            var streamRetriesText = StreamRetriesBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(streamRetriesText))
            {
                if (!ulong.TryParse(streamRetriesText, out var parsedStream))
                {
                    return (null, null, "Stream Max Retries must be a non-negative integer.");
                }
                streamRetries = parsedStream;
            }

            ulong? streamTimeout = null;
            var streamTimeoutText = StreamTimeoutBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(streamTimeoutText))
            {
                if (!ulong.TryParse(streamTimeoutText, out var parsedTimeout))
                {
                    return (null, null, "Stream Idle Timeout must be a non-negative integer in milliseconds.");
                }
                streamTimeout = parsedTimeout;
            }

            ulong? wsTimeout = null;
            var wsTimeoutText = WsTimeoutBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(wsTimeoutText))
            {
                if (!ulong.TryParse(wsTimeoutText, out var parsedWs))
                {
                    return (null, null, "WebSocket Connect Timeout must be a non-negative integer in milliseconds.");
                }
                wsTimeout = parsedWs;
            }

            Dictionary<string, string>? headers = null;
            var rawHeaders = HeadersBox.Text?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (rawHeaders != null && rawHeaders.Length > 0)
            {
                headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in rawHeaders)
                {
                    var colonIdx = line.IndexOf(':');
                    if (colonIdx <= 0) continue;
                    var hName = line[..colonIdx].Trim();
                    var hVal = line[(colonIdx + 1)..].Trim();
                    if (string.IsNullOrWhiteSpace(hName)) continue;

                    if (ApiProviderTransportOverrides.ForbiddenHeaders.Any(f => f.Equals(hName, StringComparison.OrdinalIgnoreCase)))
                    {
                        return (null, null, loc.ForbiddenHeaderError);
                    }
                    headers[hName] = hVal;
                }
                if (headers.Count == 0) headers = null;
            }

            Dictionary<string, string>? queryParams = null;
            var rawParams = QueryParamsBox.Text?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (rawParams != null && rawParams.Length > 0)
            {
                queryParams = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var line in rawParams)
                {
                    var eqIdx = line.IndexOf('=');
                    if (eqIdx <= 0) continue;
                    var qName = line[..eqIdx].Trim();
                    var qVal = line[(eqIdx + 1)..].Trim();
                    if (string.IsNullOrWhiteSpace(qName)) continue;
                    queryParams[qName] = qVal;
                }
                if (queryParams.Count == 0) queryParams = null;
            }

            CodexModelOverrides? modelOverrides = null;
            if (contextTokens.HasValue ||
                compactTokens.HasValue ||
                compactScope != CompactLimitScope.Default ||
                reasoningEffort != CodexReasoningEffort.Default ||
                reasoningSummary != CodexReasoningSummary.Default ||
                verbosity != CodexVerbosity.Default ||
                toolLimit.HasValue)
            {
                modelOverrides = new CodexModelOverrides
                {
                    ContextWindowTokens = contextTokens,
                    AutoCompactTokenLimit = compactTokens,
                    AutoCompactTokenLimitScope = compactScope,
                    ReasoningEffort = reasoningEffort,
                    ReasoningSummary = reasoningSummary,
                    Verbosity = verbosity,
                    ToolOutputTokenLimit = toolLimit,
                };
            }

            ApiProviderTransportOverrides? transportOverrides = null;
            if (reqRetries.HasValue ||
                streamRetries.HasValue ||
                streamTimeout.HasValue ||
                wsTimeout.HasValue ||
                WebSocketsCheck.IsChecked.HasValue ||
                StandaloneWebSearchCheck.IsChecked.HasValue ||
                headers != null ||
                queryParams != null)
            {
                transportOverrides = new ApiProviderTransportOverrides
                {
                    RequestMaxRetries = reqRetries,
                    StreamMaxRetries = streamRetries,
                    StreamIdleTimeoutMs = streamTimeout,
                    WebSocketConnectTimeoutMs = wsTimeout,
                    SupportsWebSockets = WebSocketsCheck.IsChecked,
                    SupportsStandaloneWebSearch = StandaloneWebSearchCheck.IsChecked,
                    HttpHeaders = headers,
                    QueryParams = queryParams,
                };
            }

            return (modelOverrides, transportOverrides, null);
        }
    }
}
