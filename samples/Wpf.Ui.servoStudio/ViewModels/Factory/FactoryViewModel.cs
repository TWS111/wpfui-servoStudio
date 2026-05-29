// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.Services;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels.Factory;

/// <summary>
/// 厂家参数页 ViewModel。<br/>
/// 仅展示三种协议栈（EtherCAT / CANopen / Modbus）中代码已配置的所有寄存器，
/// 支持单个读、全部读，以及按寄存器禁用（持久化）。
/// 厂家密码验证仍由 <c>FactoryPage.xaml.cs</c> 的遮罩 + <see cref="FactoryAccessService"/> 提供。
/// </summary>
public partial class FactoryViewModel : ViewModel
{
    private readonly DeviceAddViewModel _deviceAdd;
    private bool _isInitialized;

    [ObservableProperty]
    private ObservableCollection<RegisterRow> _etherCatRegisters = new();

    [ObservableProperty]
    private ObservableCollection<RegisterRow> _canOpenRegisters = new();

    [ObservableProperty]
    private ObservableCollection<RegisterRow> _modbusRegisters = new();

    [ObservableProperty]
    private string _ethercatStatus = "未连接";

    [ObservableProperty]
    private string _canopenStatus = "未连接";

    [ObservableProperty]
    private string _modbusStatus = "未连接";

    [ObservableProperty]
    private bool _isReadingAll;

    public FactoryViewModel(DeviceAddViewModel deviceAdd)
    {
        _deviceAdd = deviceAdd;
        RegisterDisableService.Changed += OnDisabledChanged;
    }

    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            _isInitialized = true;
            BuildEtherCAT();
            BuildCANopen();
            BuildModbus();
        }
        UpdateConnectionStatus();
    }

    private void OnDisabledChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            SyncDisabled(EtherCatRegisters);
            SyncDisabled(CanOpenRegisters);
            SyncDisabled(ModbusRegisters);
        });
    }

    private static void SyncDisabled(ObservableCollection<RegisterRow> rows)
    {
        foreach (RegisterRow r in rows)
        {
            r.SetIsDisabledFromService(RegisterDisableService.IsDisabled(r.Stack, r.Key));
        }
    }

    private void BuildEtherCAT()
    {
        EtherCatRegisters.Clear();
        foreach ((ushort idx, byte sub, string name, SdoDataType dt) in StandardObjectDictionary.EtherCAT)
        {
            EtherCatRegisters.Add(new RegisterRow
            {
                Stack = ProtocolStack.EtherCAT,
                Key = RegisterDisableService.MakeKey(idx, sub),
                Index = idx,
                SubIndex = sub,
                Name = name,
                Group = "标准对象字典",
                DataType = dt,
            });
        }
        // H 寄存器在 EtherCAT 协议栈下亦可访问；其禁用集合与 Modbus/CANopen 完全独立。
        AppendHRegisters(EtherCatRegisters, ProtocolStack.EtherCAT);
        SyncDisabled(EtherCatRegisters);
    }

    private void BuildCANopen()
    {
        CanOpenRegisters.Clear();
        foreach ((ushort idx, byte sub, string name, SdoDataType dt) in StandardObjectDictionary.CANopen)
        {
            CanOpenRegisters.Add(new RegisterRow
            {
                Stack = ProtocolStack.CANopen,
                Key = RegisterDisableService.MakeKey(idx, sub),
                Index = idx,
                SubIndex = sub,
                Name = name,
                Group = "标准对象字典",
                DataType = dt,
            });
        }
        // H 寄存器在 CANopen 协议栈下亦可访问；其禁用集合独立。
        AppendHRegisters(CanOpenRegisters, ProtocolStack.CANopen);
        SyncDisabled(CanOpenRegisters);
    }

    private static void AppendHRegisters(ObservableCollection<RegisterRow> rows, ProtocolStack stack)
    {
        foreach (HRegisterEntry e in HVariables.RegisterTable)
        {
            ushort idx = e.SdoIndex;
            byte sub = e.SdoSubIndex;
            rows.Add(new RegisterRow
            {
                Stack = stack,
                Key = RegisterDisableService.MakeKey(idx, sub),
                Index = idx,
                SubIndex = sub,
                Name = $"{e.HIndex}  {e.ParameterName}",
                Group = e.GroupName,
                DataType = SdoDataType.UInt16,
            });
        }
    }

    private void BuildModbus()
    {
        ModbusRegisters.Clear();
        foreach (HRegisterEntry e in HVariables.RegisterTable)
        {
            ushort idx = e.SdoIndex;
            byte sub = e.SdoSubIndex;
            ModbusRegisters.Add(new RegisterRow
            {
                Stack = ProtocolStack.Modbus,
                Key = RegisterDisableService.MakeKey(idx, sub),
                Index = idx,
                SubIndex = sub,
                Name = $"{e.HIndex}  {e.ParameterName}",
                Group = e.GroupName,
                DataType = SdoDataType.UInt16,
            });
        }
        SyncDisabled(ModbusRegisters);
    }

    private void UpdateConnectionStatus()
    {
        EthercatStatus = _deviceAdd.IsEthernetConnected
            ? $"已连接（从站 {_deviceAdd.CurrentAxis?.SlaveAddr}）"
            : "未连接";
        CanopenStatus = _deviceAdd.IsCanopenConnected
            ? $"已连接（节点 {_deviceAdd.CurrentCanOpenAxis?.SlaveAddr}）"
            : "未连接";
        ModbusStatus = _deviceAdd.IsModbusConnected
            ? $"已连接（从机 {_deviceAdd.CurrentModbusAxis?.SlaveAddr}）"
            : "未连接";
    }

    [RelayCommand]
    private void OnReadOne(RegisterRow? row)
    {
        if (row is null) return;
        if (row.IsDisabled)
        {
            row.Value = "—";
            row.Status = "已禁用";
            return;
        }
        UpdateConnectionStatus();
        ReadSingle(row);
    }

    [RelayCommand]
    private Task OnReadAllEcatAsync() => ReadAllAsync(EtherCatRegisters);

    [RelayCommand]
    private Task OnReadAllCanopenAsync() => ReadAllAsync(CanOpenRegisters);

    [RelayCommand]
    private Task OnReadAllModbusAsync() => ReadAllAsync(ModbusRegisters);

    private async Task ReadAllAsync(ObservableCollection<RegisterRow> rows)
    {
        if (IsReadingAll) return;
        IsReadingAll = true;
        UpdateConnectionStatus();
        try
        {
            await Task.Run(() =>
            {
                foreach (RegisterRow r in rows)
                {
                    if (r.IsDisabled)
                    {
                        SetRowStatus(r, "—", "已禁用 / 跳过");
                        continue;
                    }
                    ReadSingle(r);
                }
            });
        }
        finally
        {
            IsReadingAll = false;
        }
    }

    private void ReadSingle(RegisterRow row)
    {
        IServoMaster? master;
        int slaveAddr;
        switch (row.Stack)
        {
            case ProtocolStack.EtherCAT:
                if (!_deviceAdd.IsEthernetConnected || _deviceAdd.CurrentAxis is null)
                {
                    SetRowStatus(row, "—", "EtherCAT 未连接");
                    return;
                }
                master = new EtherCATServoMasterAdapter(_deviceAdd.EcatMaster);
                slaveAddr = _deviceAdd.CurrentAxis.SlaveAddr;
                break;
            case ProtocolStack.CANopen:
                if (!_deviceAdd.IsCanopenConnected || _deviceAdd.CanOpenMaster is null || _deviceAdd.CurrentCanOpenAxis is null)
                {
                    SetRowStatus(row, "—", "CANopen 未连接");
                    return;
                }
                master = _deviceAdd.CanOpenMaster;
                slaveAddr = _deviceAdd.CurrentCanOpenAxis.SlaveAddr;
                break;
            case ProtocolStack.Modbus:
                if (!_deviceAdd.IsModbusConnected || _deviceAdd.CurrentModbusAxis is null)
                {
                    SetRowStatus(row, "—", "Modbus 未连接");
                    return;
                }
                master = _deviceAdd.ModbusMaster;
                slaveAddr = _deviceAdd.CurrentModbusAxis.SlaveAddr;
                break;
            default:
                return;
        }

        try
        {
            (bool ok, string text) = TryRead(master, slaveAddr, row.Index, row.SubIndex, row.DataType);
            SetRowStatus(row, ok ? text : "—", ok ? "OK" : "读取失败");
        }
        catch (Exception ex)
        {
            SetRowStatus(row, "—", ex.Message);
        }
    }

    private static (bool ok, string text) TryRead(IServoMaster master, int slave, ushort idx, byte sub, SdoDataType type)
    {
        switch (type)
        {
            case SdoDataType.UInt8:
                return master.TryReadSDO(slave, idx, sub, out byte u8) ? (true, $"0x{u8:X2} ({u8})") : (false, string.Empty);
            case SdoDataType.Int8:
                return master.TryReadSDO(slave, idx, sub, out sbyte i8) ? (true, $"{i8}") : (false, string.Empty);
            case SdoDataType.UInt16:
                return master.TryReadSDO(slave, idx, sub, out ushort u16) ? (true, $"0x{u16:X4} ({u16})") : (false, string.Empty);
            case SdoDataType.Int16:
                return master.TryReadSDO(slave, idx, sub, out short i16) ? (true, $"{i16}") : (false, string.Empty);
            case SdoDataType.UInt32:
                return master.TryReadSDO(slave, idx, sub, out uint u32) ? (true, $"0x{u32:X8} ({u32})") : (false, string.Empty);
            case SdoDataType.Int32:
                return master.TryReadSDO(slave, idx, sub, out int i32) ? (true, $"{i32}") : (false, string.Empty);
            case SdoDataType.VisibleString:
            default:
                return master.TryReadSDO(slave, idx, sub, out uint raw)
                    ? (true, $"0x{raw:X8}")
                    : (false, string.Empty);
        }
    }

    private static void SetRowStatus(RegisterRow row, string value, string status)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            row.Value = value;
            row.Status = status;
        });
    }
}
