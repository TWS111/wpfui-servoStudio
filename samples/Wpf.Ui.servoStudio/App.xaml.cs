// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.Services;
using Wpf.Ui.DependencyInjection;
using System.Windows.Media;

namespace Wpf.Ui.servoStudio;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    // 单实例互斥量：防止用户同时运行多份程序，避免 ucrtbased.dll 等本地依赖
    // 被先前进程占用导致 EtherCATMaster 等本地组件初始化失败。
    private static System.Threading.Mutex? _singleInstanceMutex;
    private const string SingleInstanceMutexName = "Global\\Wpf.Ui.servoStudio.SingleInstance.{B7A8C3F1-2D04-4F2C-9E62-7C3D7B6A1A11}";

    // The.NET Generic Host provides dependency injection, configuration, logging, and other services.
    // https://docs.microsoft.com/dotnet/core/extensions/generic-host
    // https://docs.microsoft.com/dotnet/core/extensions/dependency-injection
    // https://docs.microsoft.com/dotnet/core/extensions/configuration
    // https://docs.microsoft.com/dotnet/core/extensions/logging

    private static readonly IHost _host = Host.CreateDefaultBuilder()
        .ConfigureAppConfiguration(c =>
        {
            var basePath =
                Path.GetDirectoryName(AppContext.BaseDirectory)
                ?? throw new DirectoryNotFoundException(
                    "Unable to find the base directory of the application."
                );
            _ = c.SetBasePath(basePath);
        })
        .ConfigureServices(
            (context, services) =>
            {
                _ = services.AddNavigationViewPageProvider();

                // App Host
                _ = services.AddHostedService<ApplicationHostService>();

                // Theme manipulation
                _ = services.AddSingleton<IThemeService, ThemeService>();

                // TaskBar manipulation
                _ = services.AddSingleton<ITaskBarService, TaskBarService>();

                // Service containing navigation, same as INavigationWindow... but without window
                _ = services.AddSingleton<INavigationService, NavigationService>();
                _ = services.AddSingleton<IContentDialogService, ContentDialogService>();
                _ = services.AddSingleton<Services.PageUsageTracker>();
                // Main window with navigation
                _ = services.AddSingleton<INavigationWindow, Views.MainWindow>();
                _ = services.AddSingleton<ViewModels.MainWindowViewModel>();

                // Views and ViewModels
                _ = services.AddSingleton<Views.Pages.HomePage>();
                _ = services.AddSingleton<ViewModels.HomeViewModel>();
                _ = services.AddSingleton<Views.Pages.DashboardPage>();
                _ = services.AddSingleton<ViewModels.DashboardViewModel>();
                _ = services.AddSingleton<Views.Pages.DeviceSetPages.StartPage>();
                _ = services.AddSingleton<ViewModels.DeviceSet.StartViewModel>();
                _ = services.AddSingleton<ViewModels.DeviceSet.DeviceAddViewModel>();
                _ = services.AddSingleton<Views.Pages.DeviceSetPages.ListPage>();
                _ = services.AddSingleton<ViewModels.DeviceSet.ListViewModel>();
                _ = services.AddSingleton<Views.Pages.DeviceSetPages.BusDiagnosticsPage>();
                _ = services.AddSingleton<ViewModels.DeviceSet.BusDiagnosticsViewModel>();
                _ = services.AddSingleton<Views.Pages.FactoryPages.FactoryPage>();
                _ = services.AddSingleton<ViewModels.Factory.FactoryViewModel>();
                _ = services.AddSingleton<Views.Pages.MotionPages.MotionTypePage>();
                _ = services.AddSingleton<ViewModels.Motion.MotionTypeViewModel>();
                _ = services.AddSingleton<Views.Pages.MotionPages.MotionLimitPage>();
                _ = services.AddSingleton<ViewModels.Motion.MotionLimitViewModel>();
                _ = services.AddSingleton<Views.Pages.MotionPages.MotionProfilePage>();
                _ = services.AddSingleton<ViewModels.Motion.MotionProfileViewModel>();
                _ = services.AddSingleton<Views.Pages.DataPage>();
                _ = services.AddSingleton<ViewModels.DataViewModel>();
                _ = services.AddSingleton<Views.Pages.ControlPage>();
                _ = services.AddSingleton<ViewModels.ControlViewModel>();
                _ = services.AddSingleton<Views.Pages.QuickControlPage>();
                _ = services.AddSingleton<ViewModels.QuickControlViewModel>();
                _ = services.AddSingleton<Views.Pages.HardwarePage>();
                _ = services.AddSingleton<ViewModels.HardwareViewModel>();
                _ = services.AddSingleton<Views.Pages.FaultInfoPage>();
                _ = services.AddSingleton<ViewModels.FaultInfoViewModel>();
                _ = services.AddSingleton<Views.Pages.SettingsPage>();
                _ = services.AddSingleton<ViewModels.SettingsViewModel>();

                _ = services.AddSingleton<Views.Pages.HardwarePages.ControllerPage>();
                _ = services.AddSingleton<ViewModels.Hardware.ControllerViewModel>();
                _ = services.AddSingleton<Views.Pages.HardwarePages.MotorPage>();
                _ = services.AddSingleton<ViewModels.Hardware.MotorViewModel>();
                _ = services.AddSingleton<Views.Pages.HardwarePages.IOPage>();
                _ = services.AddSingleton<ViewModels.Hardware.IOViewModel>();
                _ = services.AddSingleton<Views.Pages.HardwarePages.StoConfigPage>();
                _ = services.AddSingleton<ViewModels.Hardware.StoConfigViewModel>();
                _ = services.AddSingleton<Views.Pages.TuningPages.MotorIdentificationPage>();
                _ = services.AddSingleton<ViewModels.Tuning.MotorIdentificationViewModel>();
                _ = services.AddSingleton<Views.Pages.TuningPages.AdvancedTuningPage>();
                _ = services.AddSingleton<ViewModels.Tuning.AdvancedTuningViewModel>();
                _ = services.AddSingleton<Views.Pages.MotionPages.MultiStepProfilePage>();
                _ = services.AddSingleton<ViewModels.Motion.MultiStepProfileViewModel>();
                _ = services.AddSingleton<Views.Pages.FirmwarePages.FirmwarePage>();
                _ = services.AddSingleton<ViewModels.Firmware.EcatEepromViewModel>();
                _ = services.AddSingleton<Views.Pages.FirmwarePages.FirmwareProgramPage>();
                _ = services.AddSingleton<ViewModels.Firmware.FirmwareProgramViewModel>();
                _ = services.AddSingleton<Views.Pages.FactoryPages.FactoryFirmwarePage>();
                _ = services.AddSingleton<ViewModels.Factory.FactoryFirmwareViewModel>();
                _ = services.AddSingleton<Views.Pages.FactoryPages.FrameFormatEditorPage>();
                _ = services.AddSingleton<ViewModels.Factory.FrameFormatEditorViewModel>();
                _ = services.AddSingleton<Views.Pages.FactoryPages.AutomationProcessPage>();
                _ = services.AddSingleton<ViewModels.Factory.AutomationProcessViewModel>();
                _ = services.AddSingleton<Views.Pages.AppDataPages.AppLogPage>();
                _ = services.AddSingleton<ViewModels.AppData.AppLogViewModel>();
                _ = services.AddSingleton<Views.Pages.AppDataPages.PidAdjustPage>();
                _ = services.AddSingleton<ViewModels.AppData.PidAdjustViewModel>();
                _ = services.AddSingleton<Views.Pages.AppDataPages.DataSavePage>();
                _ = services.AddSingleton<ViewModels.AppData.DataSaveViewModel>();
                _ = services.AddSingleton<Views.Pages.AppDataPages.DataViewPage>();
                _ = services.AddSingleton<ViewModels.AppData.DataViewViewModel>();
                _ = services.AddSingleton<Views.Pages.AppDataPages.UserConfigPage>();
                _ = services.AddSingleton<ViewModels.AppData.UserConfigViewModel>();
                // Configuration
                _ = services.Configure<AppConfig>(context.Configuration.GetSection(nameof(AppConfig)));
            }
        )
        .Build();

    /// <summary>
    /// Gets services.
    /// </summary>
    public static IServiceProvider Services
    {
        get { return _host.Services; }
    }

    /// <summary>
    /// Occurs when the application is loading.
    /// </summary>
    private async void OnStartup(object sender, StartupEventArgs e)
    {
        // 1) 单实例检查
        try
        {
            _singleInstanceMutex = new System.Threading.Mutex(initiallyOwned: true, name: SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show(
                    "ServoStudio 已经在运行中。\n请关闭已有的程序窗口后再启动新的实例。",
                    "ServoStudio",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown(0);
                return;
            }
        }
        catch
        {
            // 互斥量创建失败时不阻塞启动
        }

        // Initialize theme-sensitive indicator brush before any window shows
        UpdateBitIndicatorOffBrush();
        // 把各厂家 CAN 卡 DLL 子目录加入进程 PATH，便于 P/Invoke 自动定位
        AppendCanNativeDirsToPath();
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += (theme, _) =>
        {
            UpdateBitIndicatorOffBrush();
            ViewModels.AppData.AppLogViewModel.Log(
                Models.AppLogLevel.Info, Models.AppLogCategory.Config,
                "应用主题已切换", $"新主题: {theme}");
        };

        // 启动参数 / 命令行
        try
        {
            ViewModels.AppData.AppLogViewModel.Log(
                Models.AppLogLevel.Debug, Models.AppLogCategory.App,
                "应用启动参数",
                $"args=[{string.Join(", ", e.Args ?? Array.Empty<string>())}] | " +
                $"BaseDirectory={AppContext.BaseDirectory}");
        }
        catch
        {
            // ignore
        }

        await _host.StartAsync();
    }

    private static void UpdateBitIndicatorOffBrush()
    {
        bool isDark = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme()
            == Wpf.Ui.Appearance.ApplicationTheme.Dark;

        Color color = isDark
            ? System.Windows.Media.Color.FromRgb(0x25, 0x25, 0x25)
            : System.Windows.Media.Color.FromRgb(0x9E, 0x9E, 0x9E);

        Application.Current.Resources["BitIndicatorOffBrush"] =
            new System.Windows.Media.SolidColorBrush(color);
    }

    /// <summary>
    /// 将工程附带的 CAN 厂家 DLL 子目录拼到当前进程 PATH 末尾，
    /// 让 P/Invoke 在加载 PCANBasic.dll / ControlCAN.dll / zlgcan.dll / usb_device.dll
    /// 等原生库时能自动找到对应文件（无需用户手动配置环境变量）。
    /// </summary>
    private static void AppendCanNativeDirsToPath()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string canRoot = Path.Combine(baseDir, "native", "can");
            if (!Directory.Exists(canRoot)) return;

            string[] subDirs = ["peak", "controlcan", "zlgcan", "zlgcan/kerneldlls", "toomoss", "itekon"];
            string oldPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var sb = new System.Text.StringBuilder(oldPath);
            foreach (string sub in subDirs)
            {
                string full = Path.Combine(canRoot, sub.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(full)) continue;
                if (oldPath.Contains(full, StringComparison.OrdinalIgnoreCase)) continue;
                sb.Append(Path.PathSeparator).Append(full);
                // 注册到 LOAD_LIBRARY_SEARCH_USER_DIRS — zlgcan.dll 内部加载后端 DLL 时需要
                try { _ = AddDllDirectory(full); } catch { }
            }
            Environment.SetEnvironmentVariable("PATH", sb.ToString());

            // 应用已记忆的 ZLG 驱动目录（user-dirs + PATH），并将缺失的后端 DLL
            // 复制到工程自带 kerneldlls/ 中，确保 zlgcan.dll 无论使用何种搜索方式
            // 都能找到 USBCAN_E_64.dll / USBCANFD.dll 等设备后端。
            try { global::Core.CANopen.Adapters.ZlgKernelDllScanner.ApplyCachedDirs(); } catch { }

            // 注册 ControlCAN.dll → iTEKON DLL 的回退解析器：当工程未携带
            // 原版 ControlCAN.dll 但安装了 iTEKON 的 ECANVCI.dll/ECanVci64.dll 时，
            // 自动重定向以共用同一组 P/Invoke 函数签名。
            try { Wpf.Ui.servoStudio.Services.CanNativeResolver.Register(); } catch { }
        }
        catch
        {
            // 路径配置失败不影响应用启动；适配器在缺失 DLL 时会自动降级
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    /// <summary>
    /// Occurs when the application is closing.
    /// </summary>
    private async void OnExit(object sender, ExitEventArgs e)
    {
        try
        {
            (Services.GetService(typeof(ViewModels.AppData.AppLogViewModel))
                as ViewModels.AppData.AppLogViewModel)?.Shutdown();
        }
        catch
        {
            // ignore
        }

        await _host.StopAsync();

        _host.Dispose();

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Occurs when an exception is thrown by an application but not handled.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 记录未处理异常
        ViewModels.AppData.AppLogViewModel.Log(
            Models.AppLogLevel.Critical, Models.AppLogCategory.System,
            "未处理异常", e.Exception?.ToString() ?? "Unknown exception");

        // 对常见的可恢复异常（如本地 DLL 被另一进程占用）：不让进程崩溃，
        // 仅提示用户后继续运行。
        try
        {
            string? msg = e.Exception?.Message;
            bool isFileLockIssue = e.Exception is System.IO.IOException
                && msg != null
                && msg.IndexOf("being used by another process", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isFileLockIssue)
            {
                MessageBox.Show(
                    "检测到本地依赖文件被另一进程占用：\n" + msg + "\n\n" +
                    "通常是因为已有 ServoStudio 实例尚未完全退出。\n" +
                    "请在任务管理器中结束所有 servoStudio.exe 进程后重试。",
                    "ServoStudio",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                e.Handled = true;
                return;
            }

            // 其他未处理异常也尽量不让宿主线程直接崩溃
            MessageBox.Show(
                "程序发生未处理异常：\n" + (msg ?? "Unknown exception"),
                "ServoStudio",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        }
        catch
        {
            // 兜底：保持原行为
        }
    }
}