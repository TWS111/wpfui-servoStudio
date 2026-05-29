// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Wpf.Ui.servoStudio.Views;

namespace Wpf.Ui.servoStudio.Services;

/// <summary>
/// Managed host of the application.
/// </summary>
public class ApplicationHostService(IServiceProvider serviceProvider) : IHostedService
{
    private INavigationWindow? _navigationWindow;

    /// <summary>
    /// Triggered when the application host is ready to start the service.
    /// </summary>
    /// <param name="cancellationToken">Indicates that the start process has been aborted.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        FactoryAccessService.StartNetworkMonitoring();
        await HandleActivationAsync();
    }

    /// <summary>
    /// Triggered when the application host is performing a graceful shutdown.
    /// </summary>
    /// <param name="cancellationToken">Indicates that the shutdown process should no longer be graceful.</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        FactoryAccessService.StopNetworkMonitoring();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Creates main window during activation.
    /// </summary>
    private async Task HandleActivationAsync()
    {
        if (Application.Current.Windows.OfType<MainWindow>().Any())
        {
            return;
        }

        // 必须在创建/显示 SplashWindow 之前应用主题。
        // 原因：SplashWindow 使用 AllowsTransparency=True + WindowStyle=None，
        // 通过内部 Border 的 CornerRadius 呈现圆角。
        // 一旦 splash.Show() 调用后 splash 会成为 Application.Current.MainWindow，
        // 此时再调用 ApplicationThemeManager.Apply 会触发 WindowBackgroundManager.UpdateBackground，
        // 在 Win11 上为 MainWindow 套上 DWM 系统 backdrop（Mica/Acrylic），
        // 由 DWM 在方形 HWND 区域直接绘制半透明背景，
        // 表现为"启动瞬间是圆角，随后四角出现半透明直角"的现象。
        // 在 Show() 之前应用主题可避免 splash 被识别为 MainWindow，从而不会被套 backdrop。
        UserSettings settings = UserSettingsService.Load();
        switch (settings.ThemeMode)
        {
            case "theme_dark":
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
                break;
            case "theme_system":
                Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
                break;
            default:
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
                break;
        }

        var splash = new SplashWindow();
        splash.Show();

        await splash.ReportAsync(15, "正在初始化...", 250);

        // 预加载所有注册的页面，使其后台逻辑可立即工作；同时把进度反映到启动页
        await PreloadAllPagesAsync(splash, fromPercent: 15, toPercent: 90);

        // 主动执行一次"受信任网络自动解锁厂家权限"检测。
        // 该检测原本只挂在 SettingsViewModel.OnNavigatedTo 上，启动时不会触发；
        // FactoryAccessService.IsUnlocked 是全局静态状态，提前在此处检测可让所有页面立刻看到正确状态。
        await splash.ReportAsync(95, "正在检测网络环境...", 120);
        try
        {
            _ = await FactoryAccessService.TryUnlockByTrustedNetworkAsync();
        }
        catch (Exception ex)
        {
            ViewModels.AppData.AppLogViewModel.Log(
                Models.AppLogLevel.Warning,
                Models.AppLogCategory.App,
                "启动期网络环境检测失败",
                ex.Message);
        }

        await splash.ReportAsync(100, "准备就绪", 200);
        await Task.Delay(150);

        splash.Close();

        _navigationWindow = (serviceProvider.GetService(typeof(INavigationWindow)) as INavigationWindow)!;
        _navigationWindow!.ShowWindow();

        // Watch system theme after window is shown
        if (settings.ThemeMode == "theme_system" && _navigationWindow is Window mainWin)
        {
            Wpf.Ui.Appearance.SystemThemeWatcher.Watch(mainWin);
        }

        // 等待窗口加载完成，确保模板已应用
        if (_navigationWindow is Window window && !window.IsLoaded)
        {
            var tcs = new TaskCompletionSource();
            window.Loaded += (_, _) => tcs.SetResult();
            await tcs.Task;
        }

        _ = _navigationWindow.Navigate(typeof(Views.Pages.HomePage));
    }

    /// <summary>
    /// 在启动页阶段预加载所有已注册的页面（Singleton），并把每个页面的加载映射到进度条。
    /// </summary>
    private async Task PreloadAllPagesAsync(SplashWindow splash, int fromPercent, int toPercent)
    {
        // ⚠ 维护提示：每当 App.xaml.cs 中新增一个 Page Singleton 时，必须同步在此处补齐条目，
        //   否则用户首次进入该页面会在 UI 线程上承担"控件实例化 + 模板展开 + 绑定解析"的全部开销
        //   （通常 1~3s 的卡顿）。新页面若涉及网络/串口枚举等 IO 也建议放进来由启动页统一承担。
        (Type Type, string Label)[] pages =
        [
            (typeof(Views.Pages.HomePage), "主页"),
            (typeof(Views.Pages.DashboardPage), "仪表盘"),
            (typeof(Views.Pages.DeviceSetPages.StartPage), "设备设置 - 起始"),
            (typeof(Views.Pages.DeviceSetPages.ListPage), "设备设置 - 设备列表"),
            (typeof(Views.Pages.DeviceSetPages.BusDiagnosticsPage), "设备设置 - 总线诊断"),
            (typeof(Views.Pages.FactoryPages.FactoryPage), "厂家 - 厂家参数"),
            (typeof(Views.Pages.MotionPages.MotionTypePage), "运动 - 运动类型"),
            (typeof(Views.Pages.MotionPages.MotionLimitPage), "运动 - 运动限制"),
            (typeof(Views.Pages.MotionPages.MotionProfilePage), "运动 - 运动曲线"),
            (typeof(Views.Pages.MotionPages.MultiStepProfilePage), "运动 - 多段曲线"),
            (typeof(Views.Pages.DataPage), "数据"),
            (typeof(Views.Pages.ControlPage), "控制 - 状态机"),
            (typeof(Views.Pages.QuickControlPage), "控制 - 快速控制"),
            (typeof(Views.Pages.HardwarePage), "硬件"),
            (typeof(Views.Pages.HardwarePages.ControllerPage), "硬件 - 控制器"),
            (typeof(Views.Pages.HardwarePages.MotorPage), "硬件 - 电机"),
            (typeof(Views.Pages.HardwarePages.IOPage), "硬件 - IO"),
            (typeof(Views.Pages.HardwarePages.StoConfigPage), "硬件 - STO 配置"),
            (typeof(Views.Pages.TuningPages.MotorIdentificationPage), "调谐 - 电机辨识"),
            (typeof(Views.Pages.TuningPages.AdvancedTuningPage), "调谐 - 高级调谐"),
            (typeof(Views.Pages.FirmwarePages.FirmwarePage), "固件"),
            (typeof(Views.Pages.FirmwarePages.FirmwareProgramPage), "固件 - 烧录"),
            (typeof(Views.Pages.FactoryPages.FactoryFirmwarePage), "厂家 - 厂家固件"),
            (typeof(Views.Pages.FactoryPages.FrameFormatEditorPage), "厂家 - 帧格式修改器"),
            (typeof(Views.Pages.FactoryPages.AutomationProcessPage), "厂家 - 自动化流程"),
            (typeof(Views.Pages.FaultInfoPage), "故障信息"),
            (typeof(Views.Pages.AppDataPages.AppLogPage), "应用数据 - 日志"),
            (typeof(Views.Pages.AppDataPages.PidAdjustPage), "应用数据 - PID 调整"),
            (typeof(Views.Pages.AppDataPages.DataSavePage), "应用数据 - 数据保存"),
            (typeof(Views.Pages.AppDataPages.DataViewPage), "应用数据 - 数据查看"),
            (typeof(Views.Pages.AppDataPages.UserConfigPage), "应用数据 - 用户配置"),
            (typeof(Views.Pages.SettingsPage), "设置"),
        ];

        int span = Math.Max(1, toPercent - fromPercent);

        for (int i = 0; i < pages.Length; i++)
        {
            (Type type, string label) = pages[i];
            int target = fromPercent + (int)Math.Round(span * (i + 1.0) / pages.Length);

            // 先更新文本与进度（短动画，避免页面构造阻塞 UI 时观感停滞）
            await splash.ReportAsync(target, $"正在加载页面：{label}", 60);

            try
            {
                // Singleton 注册：首次 GetService 即触发实例化与构造逻辑
                _ = serviceProvider.GetService(type);
            }
            catch (Exception ex)
            {
                // 单个页面加载失败不应影响整体启动
                ViewModels.AppData.AppLogViewModel.Log(
                    Models.AppLogLevel.Warning,
                    Models.AppLogCategory.App,
                    "启动预加载页面失败",
                    $"{type.FullName}: {ex.Message}");
            }

            // 让 UI 线程有机会刷新进度条
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}