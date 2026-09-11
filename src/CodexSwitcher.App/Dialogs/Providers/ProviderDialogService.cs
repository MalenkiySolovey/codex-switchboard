using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodexSwitcher.App.Dialogs.Common;
using CodexSwitcher.App.Dialogs.Shared;
using CodexSwitcher.App.Localization;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Catalog;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Infra;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.App.Dialogs.Providers;

/// <summary>
/// Implementation of dialogs specific to API Provider creation, editing, key rotation, thread handoff, and route switching.
/// </summary>
public sealed class ProviderDialogService : CommonDialogService, IProviderDialogService
{
    private readonly AppPaths _paths;

    public ProviderDialogService(IDialogHost host, AppPaths paths) : base(host)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
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
        panel.Children.Add(errorBar);

        var dialog = new ContentDialog
        {
            Title = _loc.ProviderDialogTitle,
            Content = panel,
            PrimaryButtonText = _loc.SaveAndSwitch,
            SecondaryButtonText = _loc.SaveOnly,
            CloseButtonText = _loc.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        bool isPrimary = false;
        bool confirmed = false;

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
                    SelectedModel = modelBox.Text.Trim(),
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
        panel.Children.Add(errorBar);

        var dialog = new ContentDialog
        {
            Title = _loc.EditProviderDialogTitle,
            Content = panel,
            PrimaryButtonText = _loc.Save,
            CloseButtonText = _loc.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

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
            var dateStr = t.UpdatedAt.ToLocalTime().ToString("g");

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
}
