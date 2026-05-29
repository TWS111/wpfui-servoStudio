// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Windows.Threading;
using Core.Net.EtherCAT;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels;

public partial class ControlViewModel(DeviceAddViewModel deviceAddViewModel) : ViewModel
{
    private bool _isInitialized = false;
    private DispatcherTimer? _refreshTimer;

    #region 连接状态

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionInfo = "设备未连接";

    [ObservableProperty]
    private string _parsedStateName = "—";

    #endregion

    #region 状态字 (0x6041) — 只读 16 位

    [ObservableProperty] private ushort _statusWordRaw;
    [ObservableProperty] private string _statusWordHex = "0x0000";

    // 各位状态 (只读显示)
    [ObservableProperty] private bool _swBit0;  // Ready to Switch On
    [ObservableProperty] private bool _swBit1;  // Switched On
    [ObservableProperty] private bool _swBit2;  // Operation Enabled
    [ObservableProperty] private bool _swBit3;  // Fault
    [ObservableProperty] private bool _swBit4;  // Voltage Enabled
    [ObservableProperty] private bool _swBit5;  // Quick Stop
    [ObservableProperty] private bool _swBit6;  // Switch On Disabled
    [ObservableProperty] private bool _swBit7;  // Warning
    [ObservableProperty] private bool _swBit8;  // Manufacturer Specific
    [ObservableProperty] private bool _swBit9;  // Remote
    [ObservableProperty] private bool _swBit10; // Target Reached
    [ObservableProperty] private bool _swBit11; // Internal Limit Active
    [ObservableProperty] private bool _swBit12; // Op Mode Specific 0
    [ObservableProperty] private bool _swBit13; // Op Mode Specific 1
    [ObservableProperty] private bool _swBit14; // Manufacturer Specific
    [ObservableProperty] private bool _swBit15; // Manufacturer Specific

    #endregion

    #region 控制字 (0x6040) — 可读写 16 位

    [ObservableProperty] private ushort _controlWordRaw;
    [ObservableProperty] private string _controlWordHex = "0x0000";

    // 各位状态 (可切换)
    [ObservableProperty] private bool _cwBit0;  // Switch On
    [ObservableProperty] private bool _cwBit1;  // Enable Voltage
    [ObservableProperty] private bool _cwBit2;  // Quick Stop
    [ObservableProperty] private bool _cwBit3;  // Enable Operation
    [ObservableProperty] private bool _cwBit4;  // Op Mode Specific 0
    [ObservableProperty] private bool _cwBit5;  // Op Mode Specific 1
    [ObservableProperty] private bool _cwBit6;  // Op Mode Specific 2
    [ObservableProperty] private bool _cwBit7;  // Fault Reset
    [ObservableProperty] private bool _cwBit8;  // Halt
    [ObservableProperty] private bool _cwBit9;  // Reserved
    [ObservableProperty] private bool _cwBit10; // Reserved
    [ObservableProperty] private bool _cwBit11; // Manufacturer Specific
    [ObservableProperty] private bool _cwBit12; // Manufacturer Specific
    [ObservableProperty] private bool _cwBit13; // Manufacturer Specific
    [ObservableProperty] private bool _cwBit14; // Manufacturer Specific
    [ObservableProperty] private bool _cwBit15; // Manufacturer Specific

    // 防止循环触发的标志
    private bool _suppressControlWordSync = false;

    // 用户正在编辑控制字
    [ObservableProperty]
    private bool _isEditingControlWord;

    // 编辑前的控制字快照（用于放弃编辑时恢复）
    private ushort _controlWordSnapshot;

    #endregion

    #region 快捷命令

    [ObservableProperty]
    private string _operationStatusText = string.Empty;

    [RelayCommand]
    private async Task OnCmdShutdown()
    {
        await WriteControlWordAsync(Cia402ControlCommands.Shutdown, "Shutdown");
    }

    [RelayCommand]
    private async Task OnCmdSwitchOn()
    {
        await WriteControlWordAsync(Cia402ControlCommands.SwitchOn, "Switch On");
    }

    [RelayCommand]
    private async Task OnCmdEnableOperation()
    {
        await WriteControlWordAsync(Cia402ControlCommands.EnableOperation, "Enable Operation");
    }

    [RelayCommand]
    private async Task OnCmdDisableVoltage()
    {
        await WriteControlWordAsync(Cia402ControlCommands.DisableVoltage, "Disable Voltage");
    }

    [RelayCommand]
    private async Task OnCmdDisableOperation()
    {
        await WriteControlWordAsync(Cia402ControlCommands.DisableOperation, "Disable Operation");
    }

    [RelayCommand]
    private async Task OnCmdQuickStop()
    {
        await WriteControlWordAsync(Cia402ControlCommands.QuickStop, "Quick Stop");
    }

    [RelayCommand]
    private async Task OnCmdFaultReset()
    {
        await WriteControlWordAsync(Cia402ControlCommands.FaultReset, "Fault Reset");
    }

    /// <summary>
    /// 一键伺服使能：自动执行 Shutdown → SwitchOn → EnableOperation 完整上电序列。
    /// 若当前处于 Fault 状态则先执行 FaultReset。
    /// </summary>
    [RelayCommand]
    private async Task OnCmdQuickEnable()
    {
        if (!deviceAddViewModel.IsAnyConnected || Axis == null)
        {
            OperationStatusText = "设备未连接";
            return;
        }

        OperationStatusText = "一键使能: 正在执行上电序列…";

        // 读取当前状态字，判断是否需要先故障复位
        ushort sw = 0;
        _ = await Task.Run(() => Master.TryReadSDO(Axis.SlaveAddr, Cia402OdIndex.StatusWord, 0, out sw));
        Cia402State state = Cia402StatusMasks.ParseState(sw);

        if (state == Cia402State.Fault)
        {
            if (!await TryWriteControlWordStepAsync(Cia402ControlCommands.FaultReset, "Fault Reset"))
                return;
            await Task.Delay(50);
        }

        // Shutdown → ReadyToSwitchOn
        if (!await TryWriteControlWordStepAsync(Cia402ControlCommands.Shutdown, "Shutdown"))
            return;
        await Task.Delay(30);

        // SwitchOn → SwitchedOn
        if (!await TryWriteControlWordStepAsync(Cia402ControlCommands.SwitchOn, "Switch On"))
            return;
        await Task.Delay(30);

        // EnableOperation → OperationEnabled
        if (!await TryWriteControlWordStepAsync(Cia402ControlCommands.EnableOperation, "Enable Operation"))
            return;

        await RefreshWordsAsync();
        OperationStatusText = "一键使能: 伺服已进入运行使能状态";
    }

    /// <summary>
    /// 序列步骤写入（不刷新 UI），成功返回 true。
    /// </summary>
    private async Task<bool> TryWriteControlWordStepAsync(ushort value, string stepName)
    {
        try
        {
            bool ok = await Task.Run(() =>
                Master.TryWriteSDO(Axis!.SlaveAddr, Cia402OdIndex.ControlWord, 0, value));
            if (!ok)
            {
                OperationStatusText = $"一键使能: {stepName} 写入失败";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            OperationStatusText = $"一键使能: {stepName} 异常 — {ex.Message}";
            return false;
        }
    }

    [RelayCommand]
    private async Task OnWriteControlWord()
    {
        await WriteControlWordAsync(BuildControlWordFromBits(), "手动写入");
    }

    /// <summary>
    /// 放弃编辑，恢复为设备当前控制字
    /// </summary>
    [RelayCommand]
    private void OnDiscardEdit()
    {
        IsEditingControlWord = false;
        SyncControlWordBitsFromRaw(_controlWordSnapshot);
        OperationStatusText = "已放弃编辑，恢复设备值";
    }

    #endregion

    #region EtherCAT 辅助

    private IServoMaster Master => deviceAddViewModel.ActiveServoMaster;
    private IServoAxis? Axis => deviceAddViewModel.ActiveAxis;

    private async Task WriteControlWordAsync(ushort value, string cmdName)
    {
        if (!deviceAddViewModel.IsAnyConnected || Axis == null)
        {
            OperationStatusText = "设备未连接";
            return;
        }

        try
        {
            bool ok = await Task.Run(() =>
                Master.TryWriteSDO(Axis.SlaveAddr, Cia402OdIndex.ControlWord, 0, value));

            if (ok)
            {
                IsEditingControlWord = false;
                OperationStatusText = $"{cmdName} (0x{value:X4}) 写入成功";
                // 立即刷新一次
                await RefreshWordsAsync();
            }
            else
            {
                OperationStatusText = $"{cmdName} 写入失败";
            }
        }
        catch (Exception ex)
        {
            OperationStatusText = $"{cmdName} 异常: {ex.Message}";
        }
    }

    #endregion

    #region 控制字位 → ushort 组装

    private ushort BuildControlWordFromBits()
    {
        ushort cw = 0;
        if (CwBit0) cw |= 1 << 0;
        if (CwBit1) cw |= 1 << 1;
        if (CwBit2) cw |= 1 << 2;
        if (CwBit3) cw |= 1 << 3;
        if (CwBit4) cw |= 1 << 4;
        if (CwBit5) cw |= 1 << 5;
        if (CwBit6) cw |= 1 << 6;
        if (CwBit7) cw |= 1 << 7;
        if (CwBit8) cw |= 1 << 8;
        if (CwBit9) cw |= 1 << 9;
        if (CwBit10) cw |= 1 << 10;
        if (CwBit11) cw |= 1 << 11;
        if (CwBit12) cw |= 1 << 12;
        if (CwBit13) cw |= 1 << 13;
        if (CwBit14) cw |= 1 << 14;
        if (CwBit15) cw |= unchecked((ushort)(1 << 15));
        return cw;
    }

    private void SyncControlWordBitsFromRaw(ushort cw)
    {
        _suppressControlWordSync = true;
        CwBit0 = (cw & (1 << 0)) != 0;
        CwBit1 = (cw & (1 << 1)) != 0;
        CwBit2 = (cw & (1 << 2)) != 0;
        CwBit3 = (cw & (1 << 3)) != 0;
        CwBit4 = (cw & (1 << 4)) != 0;
        CwBit5 = (cw & (1 << 5)) != 0;
        CwBit6 = (cw & (1 << 6)) != 0;
        CwBit7 = (cw & (1 << 7)) != 0;
        CwBit8 = (cw & (1 << 8)) != 0;
        CwBit9 = (cw & (1 << 9)) != 0;
        CwBit10 = (cw & (1 << 10)) != 0;
        CwBit11 = (cw & (1 << 11)) != 0;
        CwBit12 = (cw & (1 << 12)) != 0;
        CwBit13 = (cw & (1 << 13)) != 0;
        CwBit14 = (cw & (1 << 14)) != 0;
        CwBit15 = (cw & (1 << 15)) != 0;
        ControlWordHex = $"0x{cw:X4}";
        _suppressControlWordSync = false;
    }

    // 任意控制位更改时重新计算 Hex 显示
    partial void OnCwBit0Changed(bool value) => OnControlBitChanged();
    partial void OnCwBit1Changed(bool value) => OnControlBitChanged();
    partial void OnCwBit2Changed(bool value) => OnControlBitChanged();
    partial void OnCwBit3Changed(bool value) => OnControlBitChanged();
    partial void OnCwBit4Changed(bool value) => OnControlBitChanged();
    partial void OnCwBit5Changed(bool value) => OnControlBitChanged();
    partial void OnCwBit6Changed(bool value) => OnControlBitChanged();
    partial void OnCwBit7Changed(bool value) => OnControlBitChanged();
    partial void OnCwBit8Changed(bool value) => OnControlBitChanged();
    partial void OnCwBit9Changed(bool value) => OnControlBitChanged();
    partial void OnCwBit10Changed(bool value) => OnControlBitChanged();
    partial void OnCwBit11Changed(bool value) => OnControlBitChanged();
    partial void OnCwBit12Changed(bool value) => OnControlBitChanged();
    partial void OnCwBit13Changed(bool value) => OnControlBitChanged();
    partial void OnCwBit14Changed(bool value) => OnControlBitChanged();
    partial void OnCwBit15Changed(bool value) => OnControlBitChanged();

    private void OnControlBitChanged()
    {
        if (_suppressControlWordSync) return;

        // 用户首次修改 checkbox → 进入编辑模式，保存快照
        if (!IsEditingControlWord)
        {
            _controlWordSnapshot = ControlWordRaw;
            IsEditingControlWord = true;
        }

        ControlWordRaw = BuildControlWordFromBits();
        ControlWordHex = $"0x{ControlWordRaw:X4}";
    }

    #endregion

    #region 状态字解析

    private void SyncStatusWordBitsFromRaw(ushort sw)
    {
        SwBit0 = (sw & (1 << 0)) != 0;
        SwBit1 = (sw & (1 << 1)) != 0;
        SwBit2 = (sw & (1 << 2)) != 0;
        SwBit3 = (sw & (1 << 3)) != 0;
        SwBit4 = (sw & (1 << 4)) != 0;
        SwBit5 = (sw & (1 << 5)) != 0;
        SwBit6 = (sw & (1 << 6)) != 0;
        SwBit7 = (sw & (1 << 7)) != 0;
        SwBit8 = (sw & (1 << 8)) != 0;
        SwBit9 = (sw & (1 << 9)) != 0;
        SwBit10 = (sw & (1 << 10)) != 0;
        SwBit11 = (sw & (1 << 11)) != 0;
        SwBit12 = (sw & (1 << 12)) != 0;
        SwBit13 = (sw & (1 << 13)) != 0;
        SwBit14 = (sw & (1 << 14)) != 0;
        SwBit15 = (sw & (1 << 15)) != 0;
        StatusWordHex = $"0x{sw:X4}";
        ParsedStateName = Cia402StatusMasks.ParseState(sw).ToString();
    }

    #endregion

    #region 实时刷新

    private async Task RefreshWordsAsync()
    {
        if (!deviceAddViewModel.IsAnyConnected || Axis == null)
        {
            IsConnected = false;
            ConnectionInfo = "设备未连接";
            return;
        }

        IsConnected = true;
        ConnectionInfo = $"已连接: {deviceAddViewModel.EthernetSlaveNameInfo}";

        try
        {
            var (sw, cw) = await Task.Run(() =>
            {
                ushort statusWord = 0;
                ushort controlWord = 0;
                _ = Master.TryReadSDO(Axis.SlaveAddr, Cia402OdIndex.StatusWord, 0, out statusWord);
                _ = Master.TryReadSDO(Axis.SlaveAddr, Cia402OdIndex.ControlWord, 0, out controlWord);
                return (statusWord, controlWord);
            });

            StatusWordRaw = sw;
            SyncStatusWordBitsFromRaw(sw);

            // 用户正在编辑控制字时，不覆盖 checkbox 状态
            if (!IsEditingControlWord)
            {
                ControlWordRaw = cw;
                SyncControlWordBitsFromRaw(cw);
            }
        }
        catch (Exception ex)
        {
            // SDO 通信异常时记录日志，等待下次刷新
            AppData.AppLogViewModel.Log(Models.AppLogLevel.Warning, Models.AppLogCategory.SDO, "SDO 状态字/控制字读取异常", ex.Message);
        }
    }

    private void StartRefreshTimer()
    {
        StopRefreshTimer();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _refreshTimer.Tick += async (_, _) => await RefreshWordsAsync();
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

    #endregion

    #region 生命周期

    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            _isInitialized = true;
            DeviceAddViewModel.CommLost += OnCommLost;
        }

        StartRefreshTimer();
    }

    public override void OnNavigatedFrom()
    {
        StopRefreshTimer();
    }

    /// <summary>
    /// 通信丢失时执行软件急停：写控制字 QuickStop（Bit2=0），并停止刷新定时器。
    /// 在 UI 线程上执行，确保 ObservableProperty 更新安全。
    /// </summary>
    private void OnCommLost(object? sender, CommLostEventArgs e)
    {
        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var master = deviceAddViewModel.ActiveServoMaster;
            var axis = deviceAddViewModel.ActiveAxis;

            ConnectionInfo = $"通信丢失！{e.Protocol} 连续 {e.ConsecutiveFailures} 次未应答，已执行软件急停";
            IsConnected = false;

            if (master is null || axis is null)
            {
                return;
            }

            // 写控制字：清除 Bit2（QuickStop），触发驱动器快速停止
            try
            {
                ushort qsWord = (ushort)(ControlWordRaw & ~(1 << 2));
                master.TryWriteSDO<ushort>(axis.SlaveAddr, 0x6040, 0x00, qsWord);
            }
            catch
            {
                // 通信已丢失，写入失败是预期的，忽略异常
            }
        });
    }

    #endregion
}