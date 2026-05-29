// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Wpf.Ui.servoStudio.Services;

/// <summary>
/// 监听 Windows USB 设备热插拔事件（<c>WM_DEVICECHANGE</c>），<br/>
/// 通过一个隐藏的消息窗口接收系统广播，使 ViewModel 无需访问主窗口即可订阅设备变化。<br/>
/// <para>
/// 使用方式：<br/>
/// 1. 在应用启动早期（如 <c>App.OnStartup</c> 或 MainWindow 构造时）调用 <see cref="Start"/>。<br/>
/// 2. 在任意 ViewModel 中订阅 <see cref="DevicesChanged"/> 事件。<br/>
/// </para>
/// <para>
/// 事件经过 300 ms 防抖处理：Windows 在插拔单个设备时会连发多条
/// DBT_DEVNODES_CHANGED / DEVICEARRIVAL / DEVICEREMOVECOMPLETE，
/// 防抖可避免触发数次不必要的设备枚举。
/// </para>
/// </summary>
public static class UsbDeviceWatcher
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVNODES_CHANGED = 0x0007;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    private const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
    private const int DEVICE_NOTIFY_ALL_INTERFACE_CLASSES = 0x00000004;

    private static HwndSource? _hwndSource;
    private static DispatcherTimer? _debounceTimer;
    private static IntPtr _notifyHandle = IntPtr.Zero;
    private static bool _started;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string dbcc_name;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, IntPtr notificationFilter, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    /// <summary>USB / 即插即用设备发生变化（已防抖）。在 UI 线程触发。</summary>
    public static event EventHandler? DevicesChanged;

    /// <summary>启动监听。重复调用是幂等的。</summary>
    public static void Start()
    {
        if (_started) return;
        _started = true;

        // 必须在 UI 线程上创建 HwndSource（消息窗口）。
        var app = Application.Current;
        if (app?.Dispatcher == null) return;

        if (app.Dispatcher.CheckAccess())
            CreateMessageWindow();
        else
            app.Dispatcher.BeginInvoke(new Action(CreateMessageWindow));
    }

    private static void CreateMessageWindow()
    {
        if (_hwndSource != null) return;

        var parameters = new HwndSourceParameters("ServoStudio.UsbDeviceWatcher")
        {
            // 消息窗口：HWND_MESSAGE 父句柄，不在桌面渲染
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0,
        };
        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        // HWND_MESSAGE（消息窗口）默认不接收 WM_DEVICECHANGE 这类系统广播消息。
        // 必须通过 RegisterDeviceNotification 显式订阅，加 DEVICE_NOTIFY_ALL_INTERFACE_CLASSES
        // 后可在 GUID 为 0 时接收所有设备接口类的到达/移除通知。
        var filter = new DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
            dbcc_reserved = 0,
            dbcc_classguid = Guid.Empty,
            dbcc_name = string.Empty,
        };
        IntPtr buf = Marshal.AllocHGlobal(filter.dbcc_size);
        try
        {
            Marshal.StructureToPtr(filter, buf, false);
            _notifyHandle = RegisterDeviceNotification(
                _hwndSource.Handle,
                buf,
                DEVICE_NOTIFY_WINDOW_HANDLE | DEVICE_NOTIFY_ALL_INTERFACE_CLASSES);
        }
        finally { Marshal.FreeHGlobal(buf); }

        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer?.Stop();
            try { DevicesChanged?.Invoke(null, EventArgs.Empty); }
            catch { /* 订阅方异常不影响 watcher 自身 */ }
        };
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_DEVICECHANGE) return IntPtr.Zero;

        int evt = wParam.ToInt32();
        if (evt == DBT_DEVNODES_CHANGED
            || evt == DBT_DEVICEARRIVAL
            || evt == DBT_DEVICEREMOVECOMPLETE)
        {
            // 防抖：在 300 ms 静默期后才触发，期间任何新事件都会重置计时器
            _debounceTimer?.Stop();
            _debounceTimer?.Start();
        }
        return IntPtr.Zero;
    }
}
