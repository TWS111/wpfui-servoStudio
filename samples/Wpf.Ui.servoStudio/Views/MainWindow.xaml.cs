// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Windows.Threading;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;
using Wpf.Ui.servoStudio.Services;
using Wpf.Ui.servoStudio.ViewModels;
using Wpf.Ui.servoStudio.ViewModels.AppData;

namespace Wpf.Ui.servoStudio.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : INavigationWindow
{
    private readonly PageUsageTracker? _usageTracker;
    private readonly HomeViewModel? _homeViewModel;
    private bool _firstHomeNavHandled = false;

    public ViewModels.MainWindowViewModel ViewModel { get; }

    public MainWindow(ViewModels.MainWindowViewModel viewModel,
        INavigationService navigationService,
        IContentDialogService contentDialogService
        )
    {
        ViewModel = viewModel;
        DataContext = this;

        //Appearance.SystemThemeWatcher.Watch(this);
        //获取当前系统主题

        InitializeComponent();

        navigationService.SetNavigationControl(RootNavigation);
        contentDialogService.SetDialogHost(RootContentDialog);

        // 解析 PageUsageTracker（用于在每次导航时记录访问次数）
        try
        {
            _usageTracker = App.Services.GetService(typeof(PageUsageTracker)) as PageUsageTracker;
            _homeViewModel = App.Services.GetService(typeof(HomeViewModel)) as HomeViewModel;
        }
        catch
        {
            _usageTracker = null;
            _homeViewModel = null;
        }

        // 订阅导航事件 → 写入应用日志（包含每个被打开的页面）
        RootNavigation.Navigated += OnRootNavigated;

        // 启动时默认最大化：在 SourceInitialized 后设置，避免 FluentWindow
        // + ExtendsContentIntoTitleBar 在 XAML 阶段设置 WindowState=Maximized 导致
        // 窗口顶部越过屏幕工作区的问题。
        SourceInitialized += (_, _) =>
        {
            WindowState = WindowState.Maximized;
            // 注册全局 USB 设备热插拔监听（设备添加/连接页等订阅 DevicesChanged 事件自动刷新）。
            UsbDeviceWatcher.Start();
        };
    }

    private void OnRootNavigated(NavigationView sender, NavigatedEventArgs args)
    {
        try
        {
            Type? pageType = args.Page?.GetType();
            if (pageType is null)
                return;

            AppLogViewModel.LogNavigation(pageType);
            _usageTracker?.RecordVisit(pageType);

            // 每次导航后刷新首页快速入口，确保访问记录即时反映
            _homeViewModel?.RefreshQuickAccess();

            // 首次进入首页：延迟 0.5s 展开所有一级菜单，0.8s 后自动收起
            if (!_firstHomeNavHandled && pageType == typeof(Pages.HomePage))
            {
                _firstHomeNavHandled = true;
                var delayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                delayTimer.Tick += (s, e) =>
                {
                    delayTimer.Stop();
                    FlashExpandTopLevelMenus();
                };
                delayTimer.Start();
            }
        }
        catch
        {
            // 日志/统计失败不应影响导航
        }
    }

    private void FlashExpandTopLevelMenus()
    {
        var expanded = new System.Collections.Generic.List<NavigationViewItem>();
        foreach (var obj in ViewModel.NavigationItems)
        {
            if (obj is NavigationViewItem nvi
                && (nvi.MenuItemsSource != null || nvi.MenuItems.Count > 0))
            {
                nvi.IsExpanded = true;
                expanded.Add(nvi);
            }
        }

        if (expanded.Count == 0)
            return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            foreach (var nvi in expanded)
                nvi.IsExpanded = false;
        };
        timer.Start();
    }

    public INavigationView GetNavigation() => RootNavigation;

    public bool Navigate(Type pageType) => RootNavigation.Navigate(pageType);

    public void SetPageService(INavigationViewPageProvider navigationViewPageProvider) =>
        RootNavigation.SetPageProviderService(navigationViewPageProvider);

    public void ShowWindow() => Show();

    public void CloseWindow() => Close();

    /// <summary>
    /// Raises the closed event.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // Make sure that closing this window will begin the process of closing the application.
        Application.Current.Shutdown();
    }

    INavigationView INavigationWindow.GetNavigation()
    {
        throw new NotImplementedException();
    }

    public void SetServiceProvider(IServiceProvider serviceProvider)
    {
        throw new NotImplementedException();
    }
}