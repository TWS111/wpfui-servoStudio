// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using System.Drawing;
using Wpf.Ui.Controls;

namespace Wpf.Ui.servoStudio.ViewModels;

public partial class MainWindowViewModel : ViewModel
{
    private bool _isInitialized = false;

    [ObservableProperty]
    private string _applicationTitle = string.Empty;

    [ObservableProperty]
    private ObservableCollection<object> _navigationItems = [];

    [ObservableProperty]
    private ObservableCollection<object> _navigationFooter = [];

    [ObservableProperty]
    private ObservableCollection<object> _navigationHeader = [];

    [ObservableProperty]
    private ObservableCollection<MenuItem> _trayMenuItems = [];

    [ObservableProperty]
    private Wpf.Ui.Appearance.ApplicationTheme _currentApplicationTheme = Wpf.Ui
        .Appearance
        .ApplicationTheme
        .Unknown;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Style",
        "IDE0060:Remove unused parameter",
        Justification = "Demo"
    )]

    private readonly string[] navigationTitle = new string[10]
    { "设备页", "硬件配置", "运动配置", "参数配置" , "控制台" , "故障信息", "数据", "固件" ,"Settings" ,"FSM" };
    private int errorConst = 1;

    public MainWindowViewModel(INavigationService navigationService)
    {
        if (!_isInitialized)
        {
            InitializeViewModel();
        }
    }

    [RelayCommand]
    private void OnChangeTheme(string parameter)
    {
        switch (parameter)
        {
            case "theme_light":
                if (CurrentApplicationTheme == Wpf.Ui.Appearance.ApplicationTheme.Light)
                {
                    break;
                }

                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
                CurrentApplicationTheme = Wpf.Ui.Appearance.ApplicationTheme.Light;

                break;

            default:
                if (CurrentApplicationTheme == Wpf.Ui.Appearance.ApplicationTheme.Light)
                {
                    break;
                }

                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
                CurrentApplicationTheme = Wpf.Ui.Appearance.ApplicationTheme.Light;

                break;
        }
    }

    private void InitializeViewModel()
    {
        ApplicationTitle = " GX servo Studio    ";

        NavigationItems =
        [
            new NavigationViewItem()
            {
                Content = navigationTitle[0],
                Icon = new SymbolIcon { Symbol = SymbolRegular.Home24 },
                TargetPageType = typeof(Views.Pages.DeviceSetPages.StartPage),
                MenuItemsSource = new object[]
            {
                new NavigationViewItem("设备列表",
                SymbolRegular.TextBulletList20,
                typeof(Views.Pages.DeviceSetPages.ListPage)),
                new NavigationViewItem("添加设备",
                SymbolRegular.Add24,                
                typeof(Views.Pages.DeviceSetPages.DeviceAddPage)),
                new NavigationViewItem("移除设备",
                SymbolRegular.Subtract20,
                typeof(Views.Pages.DashboardPage)),
            },

            },
            new NavigationViewItem()
            {
                Content = navigationTitle[1],
                Icon = new SymbolIcon { Symbol = SymbolRegular.DeveloperBoard16 },
                TargetPageType = typeof(Views.Pages.HardwarePage),
                MenuItemsSource = new object[]
            {
                new NavigationViewItem("控制器参数",
                SymbolRegular.DeveloperBoardLightning20,
                typeof(Views.Pages.HardwarePages.ControllerPage)),
                new NavigationViewItem("电机参数",
                SymbolRegular.ArrowSync20,
                typeof(Views.Pages.DashboardPage)),
                new NavigationViewItem("IO配置",
                SymbolRegular.ArrowSwap20,
                typeof(Views.Pages.DashboardPage)),
                new NavigationViewItem("硬件信息",
                SymbolRegular.Memory16,
                typeof(Views.Pages.DashboardPage)),
            },
            },
            new NavigationViewItem()
            {
                Content = navigationTitle[2],
                Icon = new SymbolIcon { Symbol = SymbolRegular.MapDrive16 },
                TargetPageType = typeof(Views.Pages.DashboardPage),
                MenuItemsSource = new object[]
            {
                new NavigationViewItem("控制模式",
                SymbolRegular.DeveloperBoardLightning20,
                typeof(Views.Pages.DashboardPage)),
                new NavigationViewItem("运动限制",
                SymbolRegular.CenterHorizontal20,
                typeof(Views.Pages.DashboardPage)),
                new NavigationViewItem("振动抑制",
                SymbolRegular.DeviceEq20,
                typeof(Views.Pages.DashboardPage)),
            },
            },
            new NavigationViewItem()
            {
                Content = navigationTitle[3],
                Icon = new SymbolIcon { Symbol = SymbolRegular.Clipboard24 },
                TargetPageType = typeof(Views.Pages.DashboardPage),
                MenuItemsSource = new object[]
            {
                new NavigationViewItem("用户参数",
                SymbolRegular.CalendarChat20,
                typeof(Views.Pages.DashboardPage)),
                new NavigationViewItem("PID调节",
                SymbolRegular.StreamInputOutput20,
                typeof(Views.Pages.DashboardPage)),
                new NavigationViewItem("厂家参数",
                SymbolRegular.CalendarLock20,
                typeof(Views.Pages.ParametersPages.FactoryPage)),
            },
            },
            new NavigationViewItem()
            {
                Content = navigationTitle[4],
                Icon = new SymbolIcon { Symbol = SymbolRegular.DesktopCursor24 },
                TargetPageType = typeof(Views.Pages.ControlPage),
            },
            new NavigationViewItem()
            {
                Content = navigationTitle[5],
                Icon = new SymbolIcon { Symbol = SymbolRegular.Warning24 },
                TargetPageType = typeof(Views.Pages.FaultInfoPage),
                InfoBadge = new InfoBadge
                {
                    Value = errorConst.ToString(),
                    Visibility = errorConst > 0 ? Visibility.Visible : Visibility.Hidden,
                    Severity = InfoBadgeSeverity.Critical,
                },
            },
            new NavigationViewItem()
            {
                Content = navigationTitle[6],
                Icon = new SymbolIcon { Symbol = SymbolRegular.DataUsage20 },
                TargetPageType = typeof(Views.Pages.DashboardPage),
                MenuItemsSource = new object[]
            {
                new NavigationViewItem("数据存储",
                SymbolRegular.ArrowDownload24,
                typeof(Views.Pages.DashboardPage)),
                new NavigationViewItem("数据导入/查看",
                SymbolRegular.Memory16,
                typeof(Views.Pages.DashboardPage)),
                new NavigationViewItem("配置JSON读取/导出",
                SymbolRegular.DocumentChevronDouble24,
                typeof(Views.Pages.DashboardPage)),
                new NavigationViewItem("软件LOG日志",
                SymbolRegular.DocumentChevronDouble20,
                typeof(Views.Pages.DashboardPage)),
            },
            },
            new NavigationViewItem()
            {
                Content = navigationTitle[7],
                Icon = new SymbolIcon { Symbol = SymbolRegular.Apps24 },
                TargetPageType = typeof(Views.Pages.DashboardPage),
                MenuItemsSource = new object[]
            {
                new NavigationViewItem("控制器程序烧写",
                SymbolRegular.ArrowDownload24,
                typeof(Views.Pages.DashboardPage)),
                new NavigationViewItem("EEPROM提取",
                SymbolRegular.Memory16,
                typeof(Views.Pages.DashboardPage)),
            },
            },
        ];
                
        NavigationFooter =
        [
            new NavigationViewItem()
            {
                Content = navigationTitle[^2], //max10 now8-zerobase
                Icon = new SymbolIcon { Symbol = SymbolRegular.Settings24 },
                TargetPageType = typeof(Views.Pages.SettingsPage),
            },
            new NavigationViewItem()
            {
            Content = navigationTitle[^1],//max10 now9-zerobase
                Icon = new SymbolIcon { Symbol = SymbolRegular.Gauge24 },
                TargetPageType = typeof(Views.Pages.DashboardPage),
            },
        ];

        //TrayMenuItems = [new() { Header = "TED", Tag = "tray_home" }];

        _isInitialized = true;
    }
}
