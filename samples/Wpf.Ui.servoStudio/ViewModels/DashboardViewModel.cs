// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Windows.Threading;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels;

/// <summary>
/// 仪表盘：周期性读取 H0B 监视参数（电机转速 / 转矩 / 母线电压 / 温度 / 相电流 / 当前故障码 等）。
/// </summary>
public partial class DashboardViewModel(DeviceAddViewModel deviceAddViewModel) : ViewModel
{
    private bool _isInitialized = false;
    private DispatcherTimer? _timer;

    private Core.IServoMaster? Master => deviceAddViewModel.ActiveServoMaster;
    private Core.IServoAxis? Axis => deviceAddViewModel.ActiveAxis;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionInfo = "设备未连接 Device Not Connected";

    [ObservableProperty]
    private bool _isAutoRefresh = true;

    [ObservableProperty]
    private int _refreshIntervalMs = 500;

    [ObservableProperty]
    private string _h0BStatus = string.Empty;

    // ===== H0B 监视参数 =====
    [ObservableProperty] private int _actualSpeed;          // H0B.00 rpm (signed)
    [ObservableProperty] private int _speedCmdInput;        // H0B.01 rpm (signed)
    [ObservableProperty] private int _internalTorqueCmd;    // H0B.02 % (signed)
    [ObservableProperty] private int _posCmdPulseSpeed;     // H0B.03 rpm (signed)
    [ObservableProperty] private int _motorTemp;            // H0B.07 °C
    [ObservableProperty] private int _busVoltageMonitor;    // H0B.13 V
    [ObservableProperty] private int _phaseCurrent;         // H0B.24 A (×0.01)
    [ObservableProperty] private int _busVoltage;           // H0B.26 V
    [ObservableProperty] private int _mosTemp;              // H0B.27 °C
    [ObservableProperty] private string _faultCodeHex = "—";         // H0B.30 原始十六进制
    [ObservableProperty] private string _faultName = "—";            // 故障名称（查表）
    [ObservableProperty] private string _faultDetail = string.Empty; // 故障详细描述（查表）
    [ObservableProperty] private bool _hasFault;                     // 是否有故障（用于 UI 高亮）
    [ObservableProperty] private int _encoderBias;                   // H0B.15

    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            _isInitialized = true;
            DeviceAddViewModel.CommLost += OnCommLost;
        }

        if (IsAutoRefresh)
            StartTimer();
        ReadOnce();
    }

    public override void OnNavigatedFrom()
    {
        StopTimer();
    }

    private void OnCommLost(object? sender, CommLostEventArgs e)
    {
        _ = System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            StopTimer();
            IsConnected = false;
            ConnectionInfo = $"通信丢失！{e.Protocol} 连续 {e.ConsecutiveFailures} 次未应答，刷新已停止";
        });
    }

    partial void OnIsAutoRefreshChanged(bool value)
    {
        if (value) StartTimer(); else StopTimer();
    }

    partial void OnRefreshIntervalMsChanged(int value)
    {
        if (_timer != null)
            _timer.Interval = TimeSpan.FromMilliseconds(System.Math.Max(50, value));
    }

    private void StartTimer()
    {
        StopTimer();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(System.Math.Max(50, RefreshIntervalMs)) };
        _timer.Tick += (_, _) => ReadOnce();
        _timer.Start();
    }

    private void StopTimer()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer = null;
        }
    }

    [RelayCommand]
    private void OnReadOnce() => ReadOnce();

    [RelayCommand]
    private void OnToggleAutoRefresh() => IsAutoRefresh = !IsAutoRefresh;

    private void ReadOnce()
    {
        var master = Master;
        var axis = Axis;
        if (master is null || axis is null)
        {
            IsConnected = false;
            ConnectionInfo = "设备未连接 Device Not Connected";
            return;
        }

        IsConnected = true;
        ConnectionInfo = $"已连接 Connected: {deviceAddViewModel.EthernetSlaveNameInfo}";

        var errs = new System.Collections.Generic.List<string>();
        if (!HRegisterIO.ReadHRegSigned(master, axis, "H0B.00", v => ActualSpeed = v)) errs.Add("H0B.00");
        if (!HRegisterIO.ReadHRegSigned(master, axis, "H0B.01", v => SpeedCmdInput = v)) errs.Add("H0B.01");
        if (!HRegisterIO.ReadHRegSigned(master, axis, "H0B.02", v => InternalTorqueCmd = v)) errs.Add("H0B.02");
        if (!HRegisterIO.ReadHRegSigned(master, axis, "H0B.03", v => PosCmdPulseSpeed = v)) errs.Add("H0B.03");
        if (!HRegisterIO.ReadHRegSigned(master, axis, "H0B.07", v => MotorTemp = v)) errs.Add("H0B.07");
        if (!HRegisterIO.ReadHReg(master, axis, "H0B.13", v => BusVoltageMonitor = v)) errs.Add("H0B.13");
        if (!HRegisterIO.ReadHRegSigned(master, axis, "H0B.15", v => EncoderBias = v)) errs.Add("H0B.15");
        if (!HRegisterIO.ReadHReg(master, axis, "H0B.24", v => PhaseCurrent = v)) errs.Add("H0B.24");
        if (!HRegisterIO.ReadHReg(master, axis, "H0B.26", v => BusVoltage = v)) errs.Add("H0B.26");
        if (!HRegisterIO.ReadHRegSigned(master, axis, "H0B.27", v => MosTemp = v)) errs.Add("H0B.27");
        if (!HRegisterIO.ReadHReg(master, axis, "H0B.30", v =>
        {
            ushort code = (ushort)v;
            FaultCodeHex = code == 0 ? "—" : $"0x{code:X4}";
            FaultName = FaultCodeTable.GetName(code);
            FaultDetail = code == 0 ? string.Empty : FaultCodeTable.GetDetail(code);
            HasFault = code != 0;
        })) errs.Add("H0B.30");

        H0BStatus = errs.Count == 0
            ? $"刷新 OK @ {System.DateTime.Now:HH:mm:ss}"
            : $"部分失败: {string.Join(';', errs)}";
    }
}
