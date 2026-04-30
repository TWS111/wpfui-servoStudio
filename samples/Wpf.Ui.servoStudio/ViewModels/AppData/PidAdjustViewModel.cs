// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Windows.Threading;
using Core.Net.EtherCAT;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels.AppData;

public partial class PidAdjustViewModel(DeviceAddViewModel deviceAddViewModel) : ViewModel
{
    private bool _isInitialized = false;
    private DispatcherTimer? _refreshTimer;

    // ===== 连接状态 =====
    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionInfo = "设备未连接";

    [ObservableProperty]
    private string _statusText = "就绪";

    // ===== H08 组 PID 参数 =====

    // H08.00 速度环增益 (Kp)  2008-01h  范围 0~65535  默认 7371
    [ObservableProperty]
    private int _speedKp;

    // H08.01 速度环积分时间常数 (Ki)  2008-02h  范围 0~65535  默认 169
    [ObservableProperty]
    private int _speedKi;

    // H08.02 位置环增益 (Kp)  2008-03h  范围 0~65535  默认 1749
    [ObservableProperty]
    private int _positionKp;

    // H08.03 位置环微分 (Kd)  2008-04h  范围 0~65535  默认 1900
    [ObservableProperty]
    private int _positionKd;

    // H08.04 速度环增量 (ACC)  2008-05h  范围 0~65535  默认 500
    [ObservableProperty]
    private int _speedAcc;

    // H08.05 速度环减量 (DEC)  2008-06h  范围 0~65535  默认 500
    [ObservableProperty]
    private int _speedDec;

    // H08.06 位置环速度正向输出限幅  2008-07h  范围 0~1500  默认 1200
    [ObservableProperty]
    private int _positionSoutMax;

    // H08.07 位置环速度反向输出限幅  2008-08h  范围 0~1500  默认 1200
    [ObservableProperty]
    private int _positionSoutMin;

    // ===== 寄存器元信息 =====
    private static readonly HRegisterEntry[] PidRegisters =
    [
        HVariables.RegisterTable.First(r => r.HIndex == "H08.00"),
        HVariables.RegisterTable.First(r => r.HIndex == "H08.01"),
        HVariables.RegisterTable.First(r => r.HIndex == "H08.02"),
        HVariables.RegisterTable.First(r => r.HIndex == "H08.03"),
        HVariables.RegisterTable.First(r => r.HIndex == "H08.04"),
        HVariables.RegisterTable.First(r => r.HIndex == "H08.05"),
        HVariables.RegisterTable.First(r => r.HIndex == "H08.06"),
        HVariables.RegisterTable.First(r => r.HIndex == "H08.07"),
    ];

    // ===== 导航 =====
    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            _isInitialized = true;
        }

        StartRefreshTimer();
    }

    public override void OnNavigatedFrom()
    {
        StopRefreshTimer();
    }

    // ===== EtherCAT 辅助 =====
    private IServoMaster Master => deviceAddViewModel.ActiveServoMaster;
    private IServoAxis? Axis => deviceAddViewModel.ActiveAxis;

    /// <summary>
    /// 解析通信地址 "2008-01h" → (index=0x2008, subIndex=0x01)
    /// </summary>
    private static (ushort index, byte subIndex) ParseCommAddress(string commAddress)
    {
        // "2008-01h" → parts[0]="2008", parts[1]="01h"
        var parts = commAddress.Replace("h", "", StringComparison.OrdinalIgnoreCase).Split('-');
        ushort index = Convert.ToUInt16(parts[0], 16);
        byte subIndex = Convert.ToByte(parts[1], 16);
        return (index, subIndex);
    }

    // ===== 读取全部 PID 参数 =====
    [RelayCommand]
    private async Task OnReadAll()
    {
        if (!deviceAddViewModel.IsAnyConnected || Axis == null)
        {
            StatusText = "设备未连接，无法读取";
            return;
        }

        StatusText = "正在读取 PID 参数...";
        try
        {
            var values = await Task.Run(() =>
            {
                var result = new ushort[PidRegisters.Length];
                for (int i = 0; i < PidRegisters.Length; i++)
                {
                    (ushort index, byte sub) = ParseCommAddress(PidRegisters[i].CommAddress);
                    _ = Master.TryReadSDO(Axis.SlaveAddr, index, sub, out result[i]);
                }

                return result;
            });

            SpeedKp = values[0];
            SpeedKi = values[1];
            PositionKp = values[2];
            PositionKd = values[3];
            SpeedAcc = values[4];
            SpeedDec = values[5];
            PositionSoutMax = values[6];
            PositionSoutMin = values[7];

            StatusText = "PID 参数读取完成";
        }
        catch (Exception ex)
        {
            StatusText = $"读取异常: {ex.Message}";
            AppLogViewModel.Log(AppLogLevel.Warning, AppLogCategory.SDO, "PID 参数读取异常", ex.Message);
        }
    }

    // ===== 写入全部 PID 参数 =====
    [RelayCommand]
    private async Task OnWriteAll()
    {
        if (!deviceAddViewModel.IsAnyConnected || Axis == null)
        {
            StatusText = "设备未连接，无法写入";
            return;
        }

        StatusText = "正在写入 PID 参数...";
        try
        {
            var values = new ushort[]
            {
                (ushort)SpeedKp, (ushort)SpeedKi, (ushort)PositionKp, (ushort)PositionKd,
                (ushort)SpeedAcc, (ushort)SpeedDec, (ushort)PositionSoutMax, (ushort)PositionSoutMin
            };

            bool allOk = await Task.Run(() =>
            {
                for (int i = 0; i < PidRegisters.Length; i++)
                {
                    (ushort index, byte sub) = ParseCommAddress(PidRegisters[i].CommAddress);
                    if (!Master.TryWriteSDO(Axis.SlaveAddr, index, sub, values[i]))
                        return false;
                }

                return true;
            });

            StatusText = allOk ? "PID 参数写入成功" : "部分参数写入失败";
        }
        catch (Exception ex)
        {
            StatusText = $"写入异常: {ex.Message}";
            AppLogViewModel.Log(AppLogLevel.Warning, AppLogCategory.SDO, "PID 参数写入异常", ex.Message);
        }
    }

    // ===== 恢复默认值 =====
    [RelayCommand]
    private void OnResetDefaults()
    {
        SpeedKp = 7371;
        SpeedKi = 169;
        PositionKp = 1749;
        PositionKd = 1900;
        SpeedAcc = 500;
        SpeedDec = 500;
        PositionSoutMax = 1200;
        PositionSoutMin = 1200;
        StatusText = "已恢复默认值（尚未写入设备）";
    }

    // ===== 单参数写入（Slider 拖动释放后调用） =====
    [RelayCommand]
    private async Task OnWriteSingle(string hIndex)
    {
        if (!deviceAddViewModel.IsAnyConnected || Axis == null)
        {
            StatusText = "设备未连接";
            return;
        }

        HRegisterEntry? reg = HVariables.FindByHIndex(hIndex);
        if (reg == null) return;

        ushort value = hIndex switch
        {
            "H08.00" => (ushort)SpeedKp,
            "H08.01" => (ushort)SpeedKi,
            "H08.02" => (ushort)PositionKp,
            "H08.03" => (ushort)PositionKd,
            "H08.04" => (ushort)SpeedAcc,
            "H08.05" => (ushort)SpeedDec,
            "H08.06" => (ushort)PositionSoutMax,
            "H08.07" => (ushort)PositionSoutMin,
            _ => 0
        };

        try
        {
            (ushort index, byte sub) = ParseCommAddress(reg.CommAddress);
            bool ok = await Task.Run(() => Master.TryWriteSDO(Axis.SlaveAddr, index, sub, value));
            StatusText = ok
                ? $"{reg.ParameterName} ({reg.HIndex}) 写入成功: {value}"
                : $"{reg.ParameterName} 写入失败";
        }
        catch (Exception ex)
        {
            StatusText = $"{reg.ParameterName} 写入异常: {ex.Message}";
        }
    }

    // ===== 定时刷新 =====
    private async Task RefreshAsync()
    {
        if (!deviceAddViewModel.IsAnyConnected || Axis == null)
        {
            IsConnected = false;
            ConnectionInfo = "设备未连接";
            return;
        }

        IsConnected = true;
        ConnectionInfo = $"已连接: {deviceAddViewModel.EthernetSlaveNameInfo}";
    }

    private void StartRefreshTimer()
    {
        StopRefreshTimer();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();
    }

    private void StopRefreshTimer()
    {
        if (_refreshTimer != null)
        {
            _refreshTimer.Stop();
            _refreshTimer = null;
        }
    }
}