// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using System.Windows.Threading;
using Core.Net.EtherCAT;
using Wpf.Ui.Controls;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels;

public partial class FaultInfoViewModel(
    DeviceAddViewModel deviceAddViewModel,
    MainWindowViewModel mainWindowViewModel) : ViewModel
{
    private bool _isInitialized = false;
    private DispatcherTimer? _refreshTimer;
    private int _recordIndex = 0;

    #region 属性

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionInfo = "设备未连接 Device Not Connected";

    [ObservableProperty]
    private string _currentStateName = "—";

    [ObservableProperty]
    private string _statusWordHex = "0x0000";

    [ObservableProperty]
    private string _errorRegisterHex = "0x00";

    [ObservableProperty]
    private bool _hasFault;

    [ObservableProperty]
    private bool _hasWarning;

    [ObservableProperty]
    private int _activeFaultCount;

    [ObservableProperty]
    private string _operationStatusText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<FaultRecord> _faultRecords = [];

    #endregion

    #region EtherCAT 辅助

    private IServoMaster Master => deviceAddViewModel.ActiveServoMaster;
    private IServoAxis? Axis => deviceAddViewModel.ActiveAxis;

    #endregion

    #region 实时刷新

    private async Task RefreshStatusAsync()
    {
        if (!deviceAddViewModel.IsAnyConnected || Axis == null)
        {
            IsConnected = false;
            ConnectionInfo = "设备未连接 Device Not Connected";
            HasFault = false;
            HasWarning = false;
            ActiveFaultCount = 0;
            UpdateBadge();
            return;
        }

        IsConnected = true;
        ConnectionInfo = $"已连接 Connected: {deviceAddViewModel.EthernetSlaveNameInfo}";

        try
        {
            var (sw, errReg) = await Task.Run(() =>
            {
                _ = Master.TryReadSDO(Axis.SlaveAddr, Cia402OdIndex.StatusWord, 0, out ushort statusWord);
                _ = Master.TryReadSDO(Axis.SlaveAddr, Cia402OdIndex.ErrorRegister, 0, out byte errorRegister);
                return (statusWord, errorRegister);
            });

            StatusWordHex = $"0x{sw:X4}";
            ErrorRegisterHex = $"0x{errReg:X2}";

            Cia402State state = Cia402StatusMasks.ParseState(sw);
            CurrentStateName = state.ToString();

            bool prevFault = HasFault;
            bool prevWarning = HasWarning;

            HasFault = Cia402Helper.IsFault(sw) || state == Cia402State.FaultReactionActive;
            HasWarning = (sw & (ushort)Cia402StatusWord.Warning) != 0;

            // 新产生故障时自动记录
            if (HasFault && !prevFault)
            {
                AddRecord(FaultSeverity.Fault, "StatusWord", sw,
                    $"状态机进入故障: {state}", $"State machine fault: {state}");

                // 读取 Error Register 详情
                if (errReg != 0)
                {
                    foreach ((byte mask, string? cn, string? en) in Cia301ErrorRegister.GetActiveBits(errReg))
                    {
                        AddRecord(FaultSeverity.Fault, "ErrorRegister (0x1001)", mask, cn, en);
                    }
                }
            }

            if (HasWarning && !prevWarning)
            {
                AddRecord(FaultSeverity.Warning, "StatusWord Bit7", 0x0080,
                    "状态字警告位置位", "StatusWord warning bit set");
            }

            ActiveFaultCount = HasFault ? 1 : 0;
            UpdateBadge();
        }
        catch (Exception ex)
        {
            // SDO 通信异常，记录日志，等待下次刷新
            AppData.AppLogViewModel.Log(Models.AppLogLevel.Warning, Models.AppLogCategory.Fault, "SDO 故障状态读取异常", ex.Message);
        }
    }

    private void StartRefreshTimer()
    {
        StopRefreshTimer();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
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

    #region 导航徽标

    private void UpdateBadge()
    {
        mainWindowViewModel.UpdateFaultBadge(HasFault, HasWarning, FaultRecords.Count);
    }

    #endregion

    #region 命令

    /// <summary>
    /// 读取历史缓存故障 — CiA301 Pre-defined Error Field (0x1003)
    /// </summary>
    [RelayCommand]
    private async Task OnReadHistory()
    {
        if (!deviceAddViewModel.IsAnyConnected || Axis == null)
        {
            OperationStatusText = "设备未连接，无法读取 Device not connected";
            return;
        }

        try
        {
            List<(byte sub, uint code)> records = await Task.Run(() =>
            {
                var list = new List<(byte sub, uint code)>();
                // 子索引 0 = 记录数量
                if (!Master.TryReadSDO(Axis.SlaveAddr, Cia402OdIndex.PreDefinedError, 0, out byte count))
                    return list;

                for (byte i = 1; i <= count && i <= 10; i++)
                {
                    if (Master.TryReadSDO(Axis.SlaveAddr, Cia402OdIndex.PreDefinedError, i, out uint errCode))
                    {
                        list.Add((i, errCode));
                    }
                }

                return list;
            });

            if (records.Count == 0)
            {
                OperationStatusText = "无历史故障记录 No history fault records";
                return;
            }

            foreach ((byte sub, uint code) in records)
            {
                ushort emergencyCode = (ushort)(code & 0xFFFF);
                AddRecord(FaultSeverity.Fault, $"PreDefinedError (0x1003) Sub{sub}", emergencyCode,
                    Cia402EmergencyCode.GetDescription(emergencyCode),
                    Cia402EmergencyCodeEn.GetDescription(emergencyCode));
            }

            OperationStatusText = $"读取到 {records.Count} 条历史故障 Read {records.Count} history record(s)";
        }
        catch (Exception ex)
        {
            OperationStatusText = $"读取失败 Read failed: {ex.Message}";
            AppData.AppLogViewModel.Log(Models.AppLogLevel.Error, Models.AppLogCategory.Fault, "读取历史故障失败", ex.Message);
        }
    }

    /// <summary>
    /// 故障复位 — 写入控制字 Bit7 上升沿
    /// </summary>
    [RelayCommand]
    private async Task OnFaultReset()
    {
        if (!deviceAddViewModel.IsAnyConnected || Axis == null)
        {
            OperationStatusText = "设备未连接 Device not connected";
            return;
        }

        try
        {
            bool ok = await Task.Run(() =>
                Master.TryWriteSDO(Axis.SlaveAddr, Cia402OdIndex.ControlWord, 0, Cia402ControlCommands.FaultReset));

            if (ok)
            {
                OperationStatusText = "故障复位已发送 Fault reset sent (0x0080)";
                AddRecord(FaultSeverity.Info, "ControlWord", Cia402ControlCommands.FaultReset,
                    "已发送故障复位命令", "Fault reset command sent");
            }
            else
            {
                OperationStatusText = "故障复位写入失败 Fault reset write failed";
            }
        }
        catch (Exception ex)
        {
            OperationStatusText = $"故障复位异常 Fault reset exception: {ex.Message}";
            AppData.AppLogViewModel.Log(Models.AppLogLevel.Error, Models.AppLogCategory.Fault, "故障复位异常", ex.Message);
        }
    }

    /// <summary>
    /// 清除 Pre-defined Error Field (写入子索引0=0)
    /// </summary>
    [RelayCommand]
    private async Task OnClearDeviceErrors()
    {
        if (!deviceAddViewModel.IsAnyConnected || Axis == null)
        {
            OperationStatusText = "设备未连接 Device not connected";
            return;
        }

        try
        {
            bool ok = await Task.Run(() =>
                Master.TryWriteSDO<byte>(Axis.SlaveAddr, Cia402OdIndex.PreDefinedError, 0, 0));

            OperationStatusText = ok
                ? "已清除设备历史故障 Device error history cleared"
                : "清除失败 Clear failed";
        }
        catch (Exception ex)
        {
            OperationStatusText = $"清除异常 Clear exception: {ex.Message}";
            AppData.AppLogViewModel.Log(Models.AppLogLevel.Error, Models.AppLogCategory.Fault, "清除设备故障异常", ex.Message);
        }
    }

    /// <summary>
    /// 清除选中的本地记录
    /// </summary>
    [RelayCommand]
    private void OnClearSelected()
    {
        var selected = FaultRecords.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            OperationStatusText = "未选中任何记录 No records selected";
            return;
        }

        foreach (FaultRecord? r in selected)
            _ = FaultRecords.Remove(r);

        OperationStatusText = $"已清除 {selected.Count} 条记录 Cleared {selected.Count} record(s)";
        UpdateBadge();
    }

    /// <summary>
    /// 清除所有本地记录
    /// </summary>
    [RelayCommand]
    private void OnClearAll()
    {
        int count = FaultRecords.Count;
        FaultRecords.Clear();
        _recordIndex = 0;
        OperationStatusText = $"已清除全部 {count} 条记录 Cleared all {count} record(s)";
        UpdateBadge();
    }

    /// <summary>
    /// 全选/取消全选
    /// </summary>
    [RelayCommand]
    private void OnToggleSelectAll()
    {
        bool anyUnselected = FaultRecords.Any(r => !r.IsSelected);
        foreach (FaultRecord r in FaultRecords)
            r.IsSelected = anyUnselected;
    }

    #endregion

    #region 辅助

    private void AddRecord(FaultSeverity severity, string source, ushort code, string descCn, string descEn)
    {
        _recordIndex++;
        FaultRecords.Insert(0, new FaultRecord
        {
            Index = _recordIndex,
            Timestamp = DateTime.Now,
            Severity = severity,
            Source = source,
            RawCode = code,
            DescriptionCn = descCn,
            DescriptionEn = descEn,
        });
    }

    #endregion

    #region 生命周期

    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
            _isInitialized = true;

        StartRefreshTimer();
    }

    public override void OnNavigatedFrom()
    {
        StopRefreshTimer();
    }

    #endregion

    #region H11 故障诊断

    [ObservableProperty]
    private int _powerOnCount;          // H11.00 上电次数

    [ObservableProperty]
    private int _totalRunTimeH;         // H11.01 累计运行时间 (h)

    [ObservableProperty]
    private int _faultSpeed;            // H11.02 最近故障转速 (rpm, 有符号)

    [ObservableProperty]
    private int _faultCurrent;          // H11.03 最近故障电流 (A)

    [ObservableProperty]
    private int _faultBusVoltage;       // H11.04 最近故障母线电压 (V)

    [ObservableProperty]
    private int _faultTemp;             // H11.05 最近故障温度 (°C)

    [ObservableProperty]
    private int _faultCtrlMode;         // H11.06 最近故障控制模式

    [ObservableProperty]
    private string _faultHistory3 = "—"; // H11.10

    [ObservableProperty]
    private string _faultHistory4 = "—"; // H11.11

    [ObservableProperty]
    private string _faultHistory5 = "—"; // H11.12

    [ObservableProperty]
    private string _h11Status = string.Empty;

    [RelayCommand]
    private void OnReadH11()
    {
        if (!deviceAddViewModel.IsAnyConnected || Axis is null)
        {
            H11Status = "未连接设备 Device not connected";
            return;
        }

        var errs = new List<string>();
        if (!HRegisterIO.ReadHReg(Master, Axis, "H11.00", v => PowerOnCount = v)) errs.Add("H11.00");
        if (!HRegisterIO.ReadHReg(Master, Axis, "H11.01", v => TotalRunTimeH = v)) errs.Add("H11.01");
        if (!HRegisterIO.ReadHRegSigned(Master, Axis, "H11.02", v => FaultSpeed = v)) errs.Add("H11.02");
        if (!HRegisterIO.ReadHReg(Master, Axis, "H11.03", v => FaultCurrent = v)) errs.Add("H11.03");
        if (!HRegisterIO.ReadHReg(Master, Axis, "H11.04", v => FaultBusVoltage = v)) errs.Add("H11.04");
        if (!HRegisterIO.ReadHRegSigned(Master, Axis, "H11.05", v => FaultTemp = v)) errs.Add("H11.05");
        if (!HRegisterIO.ReadHReg(Master, Axis, "H11.06", v => FaultCtrlMode = v)) errs.Add("H11.06");
        if (!HRegisterIO.ReadHReg(Master, Axis, "H11.10", v => FaultHistory3 = v == 0 ? "—" : $"0x{v:X4}")) errs.Add("H11.10");
        if (!HRegisterIO.ReadHReg(Master, Axis, "H11.11", v => FaultHistory4 = v == 0 ? "—" : $"0x{v:X4}")) errs.Add("H11.11");
        if (!HRegisterIO.ReadHReg(Master, Axis, "H11.12", v => FaultHistory5 = v == 0 ? "—" : $"0x{v:X4}")) errs.Add("H11.12");
        H11Status = errs.Count == 0 ? "读取完成 Read OK" : $"部分失败: {string.Join(';', errs)}";
    }

    [RelayCommand]
    private void OnClearFaultHistory()
    {
        if (!deviceAddViewModel.IsAnyConnected || Axis is null)
        {
            H11Status = "未连接设备 Device not connected";
            return;
        }
        var errs = new List<string>();
        HRegisterIO.SafeWriteHReg(Master, Axis, "H11.20", 1, errs, "H11.20 故障记录清除");
        H11Status = errs.Count == 0 ? "故障记录已清除 Cleared" : $"清除失败: {string.Join(';', errs)}";
    }

    #endregion
}