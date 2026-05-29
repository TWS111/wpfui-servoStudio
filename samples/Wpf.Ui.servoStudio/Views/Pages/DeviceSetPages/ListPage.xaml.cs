// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Windows;
using System.Windows.Interop;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.Views.Pages.DeviceSetPages;

/// <summary>
/// 设备列表页：显示已连接设备详情，并提供 EtherCAT / CANopen / Modbus 设备连接入口。
/// </summary>
public partial class ListPage : INavigableView<ListViewModel>
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    private HwndSource? _hwndSource;

    public ListViewModel ViewModel { get; }

    public ListPage(ListViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Window? win = Window.GetWindow(this);
        if (win is null)
        {
            return;
        }

        HwndSource? src = HwndSource.FromHwnd(new WindowInteropHelper(win).Handle);
        if (src is null)
        {
            return;
        }

        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = src;
        _hwndSource.AddHook(WndProc);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE)
        {
            int ev = wParam.ToInt32();
            if (ev == DBT_DEVICEARRIVAL || ev == DBT_DEVICEREMOVECOMPLETE)
            {
                ViewModel.DeviceAdd.RefreshUsbDevices();
            }
        }

        return IntPtr.Zero;
    }

    private void EthernetDevices_DropDownOpened(object? sender, EventArgs e)
    {
        ViewModel.DeviceAdd.RefreshEthernetDevices();
    }
}
