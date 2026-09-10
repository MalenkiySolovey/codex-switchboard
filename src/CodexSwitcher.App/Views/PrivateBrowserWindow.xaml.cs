using CodexSwitcher.App.Localization;
using CodexSwitcher.App.Services;
using CodexSwitcher.Infra;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Graphics;

namespace CodexSwitcher.App.Views;

/// <summary>
/// Navegador privado com múltiplas abas, cada uma completamente isolada das demais (ver
/// <see cref="PrivateBrowserTab"/>): nenhum cookie, cache ou histórico é compartilhado entre abas nem
/// persiste após fechar a aba/janela. O cadeado de 2FA fica na barra de cada aba e abre um popup com
/// o <see cref="TotpPanel"/> (instância única da janela, então o segredo/código sobrevivem entre abas
/// e aberturas do popup). Aberto pelo botão "Navegador" em AccountsView, ao lado do "Entrar com OAuth".
/// </summary>
public sealed partial class PrivateBrowserWindow : Window
{
    private readonly AppPaths _paths;
    private readonly Strings _loc = Strings.Current;
    private readonly TotpPanel _totpPanel = new() { Width = 380 };
    private readonly Flyout _totpFlyout;

    public PrivateBrowserWindow(AppPaths paths)
    {
        InitializeComponent();
        _paths = paths;

        Title = _loc.BrowserWindowTitle;
        MissingHintText.Text = _loc.BrowserWebView2MissingHint;
        InstallWebView2Button.Content = _loc.LoginWebView2InstallButton;
        _totpFlyout = new Flyout { Content = _totpPanel, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };

        ConfigureWindow();
        Closed += OnClosed;
        Start();
    }

    private void Start()
    {
        if (!WebView2Bootstrap.IsRuntimeInstalled())
        {
            ShowMissingRuntime();
            return;
        }

        MissingRuntimePanel.Visibility = Visibility.Collapsed;
        Tabs.Visibility = Visibility.Visible;

        if (Tabs.TabItems.Count == 0)
            AddTab();
    }

    private void ShowMissingRuntime()
    {
        MissingTitleText.Text = _loc.LoginWebView2Missing;
        MissingRuntimePanel.Visibility = Visibility.Visible;
        Tabs.Visibility = Visibility.Collapsed;
    }

    private void OnInstallWebView2Click(object sender, RoutedEventArgs e)
    {
        WebView2Bootstrap.OpenOfficialDownloadPage();
    }

    private void OnAddTabButtonClick(TabView sender, object args) => AddTab();

    /// <summary>Aba criada pelo usuário: sessão nova e isolada das demais abas.</summary>
    private void AddTab() => AttachTab(new PrivateBrowserTab(_paths.TempRoot));

    /// <summary>Hospeda uma aba na janela, seja nova (botão "+") ou filha aberta por uma página.</summary>
    private void AttachTab(PrivateBrowserTab tab)
    {
        var item = new TabViewItem
        {
            Header = _loc.BrowserNewTabHeader,
            IconSource = new SymbolIconSource { Symbol = Symbol.Globe },
            Tag = tab,
        };
        tab.TitleChanged += (_, title) => DispatcherQueue.TryEnqueue(() =>
            item.Header = string.IsNullOrWhiteSpace(title) ? _loc.BrowserNewTabHeader : title);
        tab.TwoFactorRequested += (_, button) => _totpFlyout.ShowAt(button);
        tab.NewTabRequested += (_, child) => AttachTab(child);

        TabHost.Children.Add(tab);
        Tabs.TabItems.Add(item);
        Tabs.SelectedItem = item;
    }

    /// <summary>Mostra só a aba selecionada; as demais ficam vivas (Collapsed), sem recarregar.</summary>
    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = (Tabs.SelectedItem as TabViewItem)?.Tag as PrivateBrowserTab;
        foreach (var child in TabHost.Children)
            child.Visibility = ReferenceEquals(child, selected) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        sender.TabItems.Remove(args.Tab);

        if (args.Tab.Tag is PrivateBrowserTab tab)
        {
            TabHost.Children.Remove(tab);
            await tab.CleanupAsync();
        }

        // Fechar a última aba fecha a janela toda (comportamento normal de navegador).
        if (sender.TabItems.Count == 0) Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _totpPanel.StopTimer();
        var tabs = TabHost.Children.OfType<PrivateBrowserTab>().ToList();
        TabHost.Children.Clear();
        _ = CleanupAllAsync(tabs);
    }

    private static async Task CleanupAllAsync(List<PrivateBrowserTab> tabs)
    {
        foreach (var tab in tabs)
            await tab.CleanupAsync();
    }

    private void ConfigureWindow()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(id);

            appWindow.Resize(new SizeInt32(1100, 760));

            var work = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Nearest).WorkArea;
            appWindow.Move(new PointInt32(
                work.X + (work.Width - appWindow.Size.Width) / 2,
                work.Y + (work.Height - appWindow.Size.Height) / 2));

            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            if (File.Exists(iconPath)) appWindow.SetIcon(iconPath);
        }
        catch (Exception)
        {
            // Ajustes de janela são cosméticos; falha não impede o navegador de funcionar.
        }
    }
}
