// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Windows;
using System.Windows.Threading;
using Core.Net.EtherCAT;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.Services;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels.AppData;

/// <summary>
/// PID 调节页波形通道：保存名称、颜色及实时样本环形缓冲。
/// </summary>
public sealed class PidWaveChannel
{
    private readonly Queue<double> _queue;
    private readonly int _maxSize;

    public string Name { get; }
    public string ColorHex { get; }
    public bool IsVisible { get; set; }
    public int Count => _queue.Count;

    public PidWaveChannel(string name, string colorHex, int maxSize = 1000, bool defaultVisible = false)
    {
        Name = name;
        ColorHex = colorHex;
        _maxSize = maxSize;
        _queue = new Queue<double>(maxSize + 1);
        IsVisible = defaultVisible;
    }

    public void Append(double value)
    {
        if (_queue.Count >= _maxSize) _queue.Dequeue();
        _queue.Enqueue(value);
    }

    public void Clear() => _queue.Clear();
    public double[] ToArray() => _queue.ToArray();
}

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

    // ===== 波形通道 (位置 / 速度 / 转矩，各含目标+实际) =====

    /// <summary>波形环形缓冲最大样本数（约 100 s @ 10 Hz）。</summary>
    public const int WaveformBufferSize = 1000;

    public readonly PidWaveChannel ChTargetPos = new("目标位置", "#4FC3F7", WaveformBufferSize, defaultVisible: false);
    public readonly PidWaveChannel ChActualPos = new("实际位置", "#0288D1", WaveformBufferSize, defaultVisible: false);
    public readonly PidWaveChannel ChTargetVel = new("目标速度", "#81C784", WaveformBufferSize, defaultVisible: true);
    public readonly PidWaveChannel ChActualVel = new("实际速度", "#E57373", WaveformBufferSize, defaultVisible: true);
    public readonly PidWaveChannel ChTargetTrq = new("目标转矩", "#FFD54F", WaveformBufferSize, defaultVisible: false);
    public readonly PidWaveChannel ChActualTrq = new("实际转矩", "#FF8A65", WaveformBufferSize, defaultVisible: false);

    /// <summary>定时器每次追加新样本后触发，通知波形窗刷新（已在 Dispatcher 上下文内触发）。</summary>
    public event Action? WaveformUpdated;

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

    // —— 第二增益组 (H08.08 ~ H08.11) ——
    [ObservableProperty] private int _speedKp2;            // H08.08
    [ObservableProperty] private int _speedKi2;            // H08.09
    [ObservableProperty] private int _positionKp2;         // H08.10
    [ObservableProperty] private int _positionKd2;         // H08.11

    // —— 增益切换 (H08.12 ~ H08.15) ——
    [ObservableProperty] private int _gainSwitchMode;              // H08.12 0~4
    [ObservableProperty] private int _gainSwitchDelayMs;           // H08.13 ms
    [ObservableProperty] private int _gainSwitchSpeedThreshold;    // H08.14 rpm
    [ObservableProperty] private int _gainSwitchPosErrThreshold;   // H08.15

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
        HVariables.RegisterTable.First(r => r.HIndex == "H08.08"),
        HVariables.RegisterTable.First(r => r.HIndex == "H08.09"),
        HVariables.RegisterTable.First(r => r.HIndex == "H08.10"),
        HVariables.RegisterTable.First(r => r.HIndex == "H08.11"),
        HVariables.RegisterTable.First(r => r.HIndex == "H08.12"),
        HVariables.RegisterTable.First(r => r.HIndex == "H08.13"),
        HVariables.RegisterTable.First(r => r.HIndex == "H08.14"),
        HVariables.RegisterTable.First(r => r.HIndex == "H08.15"),
    ];

    // ===== 可见性（与厂家页禁用联动）=====
    [ObservableProperty] private Visibility _h0800Visibility = Visibility.Visible;
    [ObservableProperty] private Visibility _h0801Visibility = Visibility.Visible;
    [ObservableProperty] private Visibility _h0802Visibility = Visibility.Visible;
    [ObservableProperty] private Visibility _h0803Visibility = Visibility.Visible;
    [ObservableProperty] private Visibility _h0804Visibility = Visibility.Visible;
    [ObservableProperty] private Visibility _h0805Visibility = Visibility.Visible;
    [ObservableProperty] private Visibility _h0806Visibility = Visibility.Visible;
    [ObservableProperty] private Visibility _h0807Visibility = Visibility.Visible;
    [ObservableProperty] private Visibility _h0808Visibility = Visibility.Visible;
    [ObservableProperty] private Visibility _h0809Visibility = Visibility.Visible;
    [ObservableProperty] private Visibility _h0810Visibility = Visibility.Visible;
    [ObservableProperty] private Visibility _h0811Visibility = Visibility.Visible;
    [ObservableProperty] private Visibility _h0812Visibility = Visibility.Visible;
    [ObservableProperty] private Visibility _h0813Visibility = Visibility.Visible;
    [ObservableProperty] private Visibility _h0814Visibility = Visibility.Visible;
    [ObservableProperty] private Visibility _h0815Visibility = Visibility.Visible;

    private void RefreshVisibilities()
    {
        H0800Visibility = VisFor(PidRegisters[0]);
        H0801Visibility = VisFor(PidRegisters[1]);
        H0802Visibility = VisFor(PidRegisters[2]);
        H0803Visibility = VisFor(PidRegisters[3]);
        H0804Visibility = VisFor(PidRegisters[4]);
        H0805Visibility = VisFor(PidRegisters[5]);
        H0806Visibility = VisFor(PidRegisters[6]);
        H0807Visibility = VisFor(PidRegisters[7]);
        H0808Visibility = VisFor(PidRegisters[8]);
        H0809Visibility = VisFor(PidRegisters[9]);
        H0810Visibility = VisFor(PidRegisters[10]);
        H0811Visibility = VisFor(PidRegisters[11]);
        H0812Visibility = VisFor(PidRegisters[12]);
        H0813Visibility = VisFor(PidRegisters[13]);
        H0814Visibility = VisFor(PidRegisters[14]);
        H0815Visibility = VisFor(PidRegisters[15]);

        static Visibility VisFor(HRegisterEntry e)
            => RegisterDisableService.IsDisabledForActive(e.SdoIndex, e.SdoSubIndex)
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    // ===== 导航 =====
    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            _isInitialized = true;
            RegisterDisableService.Changed -= OnDisabledChanged;
            RegisterDisableService.Changed += OnDisabledChanged;
            DeviceAddViewModel.CommLost += OnCommLost;
        }

        RefreshVisibilities();
        StartRefreshTimer();
    }

    private void OnDisabledChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(RefreshVisibilities);
    }

    private void OnCommLost(object? sender, CommLostEventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            StopRefreshTimer();
            IsConnected = false;
            ConnectionInfo = $"通信丢失！{e.Protocol} 连续 {e.ConsecutiveFailures} 次未应答，波形采集已停止";
        });
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
                    HRegisterEntry r = PidRegisters[i];
                    if (RegisterDisableService.IsDisabledForActive(r.SdoIndex, r.SdoSubIndex))
                        continue; // 被禁用的寄存器跳过读取
                    (ushort index, byte sub) = ParseCommAddress(r.CommAddress);
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
            SpeedKp2 = values[8];
            SpeedKi2 = values[9];
            PositionKp2 = values[10];
            PositionKd2 = values[11];
            GainSwitchMode = values[12];
            GainSwitchDelayMs = values[13];
            GainSwitchSpeedThreshold = values[14];
            GainSwitchPosErrThreshold = values[15];

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
                (ushort)SpeedAcc, (ushort)SpeedDec, (ushort)PositionSoutMax, (ushort)PositionSoutMin,
                (ushort)SpeedKp2, (ushort)SpeedKi2, (ushort)PositionKp2, (ushort)PositionKd2,
                (ushort)GainSwitchMode, (ushort)GainSwitchDelayMs,
                (ushort)GainSwitchSpeedThreshold, (ushort)GainSwitchPosErrThreshold,
            };

            bool allOk = await Task.Run(() =>
            {
                for (int i = 0; i < PidRegisters.Length; i++)
                {
                    HRegisterEntry r = PidRegisters[i];
                    if (RegisterDisableService.IsDisabledForActive(r.SdoIndex, r.SdoSubIndex))
                        continue; // 被禁用的寄存器跳过写入
                    (ushort index, byte sub) = ParseCommAddress(r.CommAddress);
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
        SpeedKp2 = 7371;
        SpeedKi2 = 169;
        PositionKp2 = 1749;
        PositionKd2 = 1900;
        GainSwitchMode = 0;
        GainSwitchDelayMs = 0;
        GainSwitchSpeedThreshold = 200;
        GainSwitchPosErrThreshold = 1000;
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
            "H08.08" => (ushort)SpeedKp2,
            "H08.09" => (ushort)SpeedKi2,
            "H08.10" => (ushort)PositionKp2,
            "H08.11" => (ushort)PositionKd2,
            "H08.12" => (ushort)GainSwitchMode,
            "H08.13" => (ushort)GainSwitchDelayMs,
            "H08.14" => (ushort)GainSwitchSpeedThreshold,
            "H08.15" => (ushort)GainSwitchPosErrThreshold,
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

    // ===== 清除波形 =====
    [RelayCommand]
    private void OnClearWaveform()
    {
        ChTargetPos.Clear();
        ChActualPos.Clear();
        ChTargetVel.Clear();
        ChActualVel.Clear();
        ChTargetTrq.Clear();
        ChActualTrq.Clear();
        WaveformUpdated?.Invoke();
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

        // 读取实时 PDO 值并追加到可见波形通道
        bool anyVisible = ChTargetPos.IsVisible || ChActualPos.IsVisible
                       || ChTargetVel.IsVisible || ChActualVel.IsVisible
                       || ChTargetTrq.IsVisible || ChActualTrq.IsVisible;

        if (anyVisible)
        {
            await Task.Run(() =>
            {
                int targetPos = 0, actualPos = 0, targetVel = 0, actualVel = 0;
                short targetTrq = 0, actualTrq = 0;

                if (ChTargetPos.IsVisible)
                    Master.TryReadSDO<int>(Axis.SlaveAddr, Cia402OdIndex.TargetPosition, 0, out targetPos);
                if (ChActualPos.IsVisible)
                    Master.TryReadSDO<int>(Axis.SlaveAddr, Cia402OdIndex.PositionActualValue, 0, out actualPos);
                if (ChTargetVel.IsVisible)
                    Master.TryReadSDO<int>(Axis.SlaveAddr, Cia402OdIndex.TargetVelocity, 0, out targetVel);
                if (ChActualVel.IsVisible)
                    Master.TryReadSDO<int>(Axis.SlaveAddr, Cia402OdIndex.VelocityActualValue, 0, out actualVel);
                if (ChTargetTrq.IsVisible)
                    Master.TryReadSDO<short>(Axis.SlaveAddr, Cia402OdIndex.TargetTorque, 0, out targetTrq);
                if (ChActualTrq.IsVisible)
                    Master.TryReadSDO<short>(Axis.SlaveAddr, Cia402OdIndex.TorqueActualValue, 0, out actualTrq);

                // 回到 Dispatcher 线程追加并通知
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (ChTargetPos.IsVisible) ChTargetPos.Append(targetPos);
                    if (ChActualPos.IsVisible) ChActualPos.Append(actualPos);
                    if (ChTargetVel.IsVisible) ChTargetVel.Append(targetVel);
                    if (ChActualVel.IsVisible) ChActualVel.Append(actualVel);
                    if (ChTargetTrq.IsVisible) ChTargetTrq.Append(targetTrq);
                    if (ChActualTrq.IsVisible) ChActualTrq.Append(actualTrq);
                    WaveformUpdated?.Invoke();
                });
            });
        }
    }

    private void StartRefreshTimer()
    {
        StopRefreshTimer();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
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