// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Core.Net.EtherCAT;
using System.Collections.ObjectModel;
using System.Text;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels.Firmware;

public partial class EcatEepromViewModel(DeviceAddViewModel deviceAddViewModel) : ViewModel
{
    private bool _isInitialized = false;

    [ObservableProperty]
    private string _deviceName = string.Empty;

    [ObservableProperty]
    private string _deviceAddress = string.Empty;

    [ObservableProperty]
    private string _deviceState = string.Empty;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private bool _isReading = false;

    [ObservableProperty]
    private bool _isConnected = false;

    [ObservableProperty]
    private ObservableCollection<ObjectDictionaryEntry> _objectDictionaryEntries = new();

    [ObservableProperty]
    private ObservableCollection<PdoMappingEntry> _rxPdoMappingEntries = new();

    [ObservableProperty]
    private ObservableCollection<PdoMappingEntry> _txPdoMappingEntries = new();

    /// <summary>
    /// CiA 301 / CiA 402 标准对象字典已知条目表
    /// </summary>
    private static readonly (int Index, int SubIndex, string Name, SdoDataType DataType)[] _knownEntries =
    [
        // ──── DS301 通信配置区 ────
        (0x1000, 0, "Device Type", SdoDataType.UInt32),
        (0x1001, 0, "Error Register", SdoDataType.UInt8),
        (0x1008, 0, "Manufacturer Device Name", SdoDataType.VisibleString),
        (0x1009, 0, "Manufacturer Hardware Version", SdoDataType.VisibleString),
        (0x100A, 0, "Manufacturer Software Version", SdoDataType.VisibleString),

        // Identity Object
        (0x1018, 0, "Identity - Number of Entries", SdoDataType.UInt8),
        (0x1018, 1, "Identity - Vendor ID", SdoDataType.UInt32),
        (0x1018, 2, "Identity - Product Code", SdoDataType.UInt32),
        (0x1018, 3, "Identity - Revision Number", SdoDataType.UInt32),
        (0x1018, 4, "Identity - Serial Number", SdoDataType.UInt32),

        // Sync Manager Communication Type
        (0x1C00, 0, "SM Comm Type - Count", SdoDataType.UInt8),
        (0x1C00, 1, "SM0 Communication Type", SdoDataType.UInt8),
        (0x1C00, 2, "SM1 Communication Type", SdoDataType.UInt8),
        (0x1C00, 3, "SM2 Communication Type", SdoDataType.UInt8),
        (0x1C00, 4, "SM3 Communication Type", SdoDataType.UInt8),

        // ──── CiA 402 伺服驱动配置区 ────
        (0x6040, 0, "Controlword (控制字)", SdoDataType.UInt16),
        (0x6041, 0, "Statusword (状态字)", SdoDataType.UInt16),
        (0x6060, 0, "Modes of Operation (运行模式)", SdoDataType.Int8),
        (0x6061, 0, "Modes of Operation Display (运行模式显示)", SdoDataType.Int8),
        (0x6064, 0, "Position Actual Value (实际位置)", SdoDataType.Int32),
        (0x606C, 0, "Velocity Actual Value (实际速度)", SdoDataType.Int32),
        (0x6071, 0, "Target Torque (目标转矩)", SdoDataType.Int16),
        (0x6072, 0, "Max Torque (最大转矩)", SdoDataType.UInt16),
        (0x6073, 0, "Max Current (最大电流)", SdoDataType.UInt16),
        (0x6075, 0, "Motor Rated Current (额定电流)", SdoDataType.UInt32),
        (0x6076, 0, "Motor Rated Torque (额定转矩)", SdoDataType.UInt32),
        (0x6077, 0, "Torque Actual Value (实际转矩)", SdoDataType.Int16),
        (0x607A, 0, "Target Position (目标位置)", SdoDataType.Int32),
        (0x607C, 0, "Home Offset (原点偏移)", SdoDataType.Int32),
        (0x607D, 1, "Min Position Range Limit (最小位置限制)", SdoDataType.Int32),
        (0x607D, 2, "Max Position Range Limit (最大位置限制)", SdoDataType.Int32),
        (0x607E, 0, "Polarity (极性)", SdoDataType.UInt8),
        (0x6081, 0, "Profile Velocity (轮廓速度)", SdoDataType.UInt32),
        (0x6083, 0, "Profile Acceleration (轮廓加速度)", SdoDataType.UInt32),
        (0x6084, 0, "Profile Deceleration (轮廓减速度)", SdoDataType.UInt32),
        (0x6085, 0, "Quick Stop Deceleration (快速停止减速度)", SdoDataType.UInt32),
        (0x6098, 0, "Homing Method (回零方式)", SdoDataType.Int8),
        (0x6099, 1, "Homing Speed - Search Switch (寻找开关速度)", SdoDataType.UInt32),
        (0x6099, 2, "Homing Speed - Search Zero (寻零速度)", SdoDataType.UInt32),
        (0x609A, 0, "Homing Acceleration (回零加速度)", SdoDataType.UInt32),
        (0x60FF, 0, "Target Velocity (目标速度)", SdoDataType.Int32),
        (0x6502, 0, "Supported Drive Modes (支持的运行模式)", SdoDataType.UInt32),
    ];

    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            InitializeViewModel();
        }

        RefreshConnectionStatus();
    }

    private void InitializeViewModel()
    {
        _isInitialized = true;
    }

    private void RefreshConnectionStatus()
    {
        IsConnected = deviceAddViewModel.IsEthernetConnected;
        DeviceName = deviceAddViewModel.EthernetSlaveNameInfo;
        DeviceAddress = deviceAddViewModel.EthernetSlaveAddrInfo;
        DeviceState = deviceAddViewModel.EthernetSlaveStateInfo;
        ReadAllCommand.NotifyCanExecuteChanged();
    }

    private bool CanReadAll() => IsConnected && !IsReading;

    [RelayCommand(CanExecute = nameof(CanReadAll))]
    private async Task OnReadAll()
    {
        IsReading = true;
        StatusText = "正在读取对象字典...";
        ReadAllCommand.NotifyCanExecuteChanged();

        try
        {
            EtherCATSlave_CiA402? axis = deviceAddViewModel.CurrentAxis;
            EtherCATMaster master = deviceAddViewModel.EcatMaster;

            if (axis == null)
            {
                StatusText = "未找到从站设备";
                return;
            }

            int slaveAddr = axis.SlaveAddr;

            // 1. 读取对象字典
            List<ObjectDictionaryEntry> odEntries = await Task.Run(() => ReadObjectDictionary(master, slaveAddr));

            ObjectDictionaryEntries.Clear();
            foreach (ObjectDictionaryEntry? entry in odEntries)
            {
                ObjectDictionaryEntries.Add(entry);
            }

            // 2. 读取 PDO 映射
            StatusText = "正在读取PDO映射...";
            (List<PdoMappingEntry>? rxPdo, List<PdoMappingEntry>? txPdo) = await Task.Run(() => ReadPdoMappings(master, slaveAddr));

            RxPdoMappingEntries.Clear();
            foreach (PdoMappingEntry? entry in rxPdo)
            {
                RxPdoMappingEntries.Add(entry);
            }

            TxPdoMappingEntries.Clear();
            foreach (PdoMappingEntry? entry in txPdo)
            {
                TxPdoMappingEntries.Add(entry);
            }

            StatusText = $"读取完成 — 对象字典: {odEntries.Count} 项, RxPDO: {rxPdo.Count} 项, TxPDO: {txPdo.Count} 项";
        }
        catch (Exception ex)
        {
            StatusText = $"读取失败: {ex.Message}";
            AppData.AppLogViewModel.Log(Models.AppLogLevel.Error, Models.AppLogCategory.Firmware, "EEPROM 读取失败", ex.Message);
        }
        finally
        {
            IsReading = false;
            ReadAllCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void OnRefreshStatus()
    {
        RefreshConnectionStatus();
    }

    // ────────────────── SDO 读取辅助 ──────────────────

    private List<ObjectDictionaryEntry> ReadObjectDictionary(EtherCATMaster master, int slaveAddr)
    {
        var entries = new List<ObjectDictionaryEntry>();

        foreach ((int index, int subIndex, string? name, SdoDataType dataType) in _knownEntries)
        {
            (bool success, string? valueStr, string? hexStr) = TryReadSdo(master, slaveAddr, index, subIndex, dataType);
            if (success)
            {
                entries.Add(new ObjectDictionaryEntry
                {
                    IndexHex = $"0x{index:X4}",
                    SubIndexHex = $"0x{subIndex:X2}",
                    Name = name,
                    DataTypeName = dataType.ToString(),
                    Value = valueStr,
                    HexValue = hexStr
                });
            }
        }

        return entries;
    }

    private static (bool Success, string Value, string Hex) TryReadSdo(
        EtherCATMaster master, int slaveAddr, int index, int subIndex, SdoDataType dataType)
    {
        try
        {
            return dataType switch
            {
                SdoDataType.UInt8 => FormatResult(master.ReadSDO<byte>(slaveAddr, index, subIndex), "X2"),
                SdoDataType.Int8 => FormatSignedResult(master.ReadSDO<sbyte>(slaveAddr, index, subIndex), 1),
                SdoDataType.UInt16 => FormatResult(master.ReadSDO<ushort>(slaveAddr, index, subIndex), "X4"),
                SdoDataType.Int16 => FormatSignedResult(master.ReadSDO<short>(slaveAddr, index, subIndex), 2),
                SdoDataType.UInt32 => FormatResult(master.ReadSDO<uint>(slaveAddr, index, subIndex), "X8"),
                SdoDataType.Int32 => FormatSignedResult(master.ReadSDO<int>(slaveAddr, index, subIndex), 4),
                SdoDataType.VisibleString => ReadVisibleString(master, slaveAddr, index, subIndex),
                _ => ReadRawBytes(master, slaveAddr, index, subIndex),
            };
        }
        catch (Exception ex)
        {
            AppData.AppLogViewModel.Log(Models.AppLogLevel.Warning, Models.AppLogCategory.SDO, "SDO 读取失败", $"Index=0x{index:X4} Sub=0x{subIndex:X2}: {ex.Message}");
            return (false, string.Empty, string.Empty);
        }
    }

    private static (bool, string, string) FormatResult<T>(T value, string hexFormat) where T : struct, IFormattable
    {
        string dec = value.ToString(null, null) ?? string.Empty;
        string hex = $"0x{value.ToString(hexFormat, null)}";
        return (true, dec, hex);
    }

    private static (bool, string, string) FormatSignedResult(long value, int byteCount)
    {
        string dec = value.ToString();
        ulong mask = byteCount switch
        {
            1 => 0xFF,
            2 => 0xFFFF,
            4 => 0xFFFFFFFF,
            _ => (ulong)value
        };
        string hex = $"0x{((ulong)value & mask).ToString($"X{byteCount * 2}")}";
        return (true, dec, hex);
    }

    private static (bool, string, string) ReadVisibleString(
        EtherCATMaster master, int slaveAddr, int index, int subIndex)
    {
        byte[] buf = new byte[128];
        int size = buf.Length;
        int wkc = master.ReadSDO(slaveAddr, index, subIndex, ref size, buf);
        if (wkc > 0 && size > 0)
        {
            string s = Encoding.ASCII.GetString(buf, 0, size).TrimEnd('\0');
            return (true, s, BitConverter.ToString(buf, 0, size));
        }

        return (false, string.Empty, string.Empty);
    }

    private static (bool, string, string) ReadRawBytes(
        EtherCATMaster master, int slaveAddr, int index, int subIndex)
    {
        byte[] buf = new byte[64];
        int size = buf.Length;
        int wkc = master.ReadSDO(slaveAddr, index, subIndex, ref size, buf);
        if (wkc > 0 && size > 0)
        {
            string hex = BitConverter.ToString(buf, 0, size);
            return (true, hex, hex);
        }

        return (false, string.Empty, string.Empty);
    }

    // ────────────────── PDO 映射读取 ──────────────────

    private (List<PdoMappingEntry> RxPdo, List<PdoMappingEntry> TxPdo) ReadPdoMappings(
        EtherCATMaster master, int slaveAddr)
    {
        var rxEntries = new List<PdoMappingEntry>();
        var txEntries = new List<PdoMappingEntry>();

        // SM2 → RxPDO (0x1C12)
        ReadSmPdoAssignment(master, slaveAddr, 0x1C12, "RxPDO", rxEntries);

        // SM3 → TxPDO (0x1C13)
        ReadSmPdoAssignment(master, slaveAddr, 0x1C13, "TxPDO", txEntries);

        return (rxEntries, txEntries);
    }

    private void ReadSmPdoAssignment(
        EtherCATMaster master, int slaveAddr, int smIndex, string direction, List<PdoMappingEntry> entries)
    {
        try
        {
            byte count = master.ReadSDO<byte>(slaveAddr, smIndex, 0);
            if (count == 0) return;

            for (int i = 1; i <= count; i++)
            {
                try
                {
                    ushort pdoIndex = master.ReadSDO<ushort>(slaveAddr, smIndex, i);
                    if (pdoIndex == 0) continue;
                    ReadPdoMappingObject(master, slaveAddr, pdoIndex, direction, entries);
                }
                catch { /* 跳过不可读子索引 */ }
            }
        }
        catch { /* SM 分配对象不可用 */ }
    }

    private void ReadPdoMappingObject(
        EtherCATMaster master, int slaveAddr, int pdoIndex, string direction, List<PdoMappingEntry> entries)
    {
        try
        {
            byte mapCount = master.ReadSDO<byte>(slaveAddr, pdoIndex, 0);
            if (mapCount == 0) return;

            for (int j = 1; j <= mapCount; j++)
            {
                try
                {
                    // 32-bit mapping value: [31:16]=Index, [15:8]=SubIndex, [7:0]=BitLength
                    uint mapping = master.ReadSDO<uint>(slaveAddr, pdoIndex, j);
                    int objIndex = (int)((mapping >> 16) & 0xFFFF);
                    int objSubIndex = (int)((mapping >> 8) & 0xFF);
                    int bitLen = (int)(mapping & 0xFF);

                    entries.Add(new PdoMappingEntry
                    {
                        Direction = direction,
                        PdoIndexHex = $"0x{pdoIndex:X4}",
                        MappedIndexHex = $"0x{objIndex:X4}",
                        MappedSubIndexHex = $"0x{objSubIndex:X2}",
                        BitLength = bitLen.ToString(),
                        ObjectName = GetKnownObjectName(objIndex, objSubIndex)
                    });
                }
                catch { /* 跳过不可读映射条目 */ }
            }
        }
        catch { /* PDO 映射对象不可用 */ }
    }

    private static string GetKnownObjectName(int index, int subIndex)
    {
        foreach ((int idx, int sub, string? name, SdoDataType _) in _knownEntries)
        {
            if (idx == index && sub == subIndex)
                return name;
        }

        return string.Empty;
    }
}