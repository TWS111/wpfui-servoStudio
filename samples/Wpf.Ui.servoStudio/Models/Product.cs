// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Wpf.Ui.servoStudio.Models;

public class Product
{
    public int Time { get; set; }

    public int ErrorCode { get; set; }

    public string? ErrorName { get; set; }

    public Unit TypeUnit { get; set; }

    public bool IsVirtual { get; set; }
}

/// <summary>
/// CiA301/CiA402 故障/警告记录
/// </summary>
public class FaultRecord : ObservableObject
{
    private bool _isSelected;

    /// <summary>记录序号</summary>
    public int Index { get; set; }

    /// <summary>发生时间</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>严重级别: Fault / Warning / Info</summary>
    public FaultSeverity Severity { get; set; }

    /// <summary>OD 来源 (StatusWord / ErrorRegister / PreDefinedError)</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>原始错误码 (Hex)</summary>
    public ushort RawCode { get; set; }

    /// <summary>错误码十六进制显示</summary>
    public string CodeHex => $"0x{RawCode:X4}";

    /// <summary>中文描述</summary>
    public string DescriptionCn { get; set; } = string.Empty;

    /// <summary>英文描述</summary>
    public string DescriptionEn { get; set; } = string.Empty;

    /// <summary>是否选中 (用于批量操作)</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// 故障严重级别
/// </summary>
public enum FaultSeverity
{
    Info,
    Warning,
    Fault,
}

/// <summary>
/// CiA301 Error Register (0x1001) 位定义
/// </summary>
public static class Cia301ErrorRegister
{
    public const byte Generic = 0x01; // Bit 0
    public const byte Current = 0x02; // Bit 1
    public const byte Voltage = 0x04; // Bit 2
    public const byte Temperature = 0x08; // Bit 3
    public const byte Communication = 0x10; // Bit 4
    public const byte DeviceProfile = 0x20; // Bit 5
    public const byte Reserved = 0x40; // Bit 6
    public const byte ManufacturerSpecific = 0x80; // Bit 7

    public static IEnumerable<(byte Mask, string NameCn, string NameEn)> GetActiveBits(byte reg)
    {
        if ((reg & Generic) != 0) yield return (Generic, "通用错误", "Generic Error");
        if ((reg & Current) != 0) yield return (Current, "电流异常", "Current Error");
        if ((reg & Voltage) != 0) yield return (Voltage, "电压异常", "Voltage Error");
        if ((reg & Temperature) != 0) yield return (Temperature, "温度异常", "Temperature Error");
        if ((reg & Communication) != 0) yield return (Communication, "通信错误", "Communication Error");
        if ((reg & DeviceProfile) != 0) yield return (DeviceProfile, "设备配置错误", "Device Profile Error");
        if ((reg & ManufacturerSpecific) != 0) yield return (ManufacturerSpecific, "厂商自定义错误", "Manufacturer Specific");
    }
}

/// <summary>
/// CiA402 Emergency Error Code 英文描述扩展
/// </summary>
public static class Cia402EmergencyCodeEn
{
    public static string GetDescription(ushort code) => code switch
    {
        0x0000 => "No Error",
        0x1000 => "Generic Error",
        0x2310 => "Over Current",
        0x2320 => "Short Circuit",
        0x3210 => "Over Voltage",
        0x3220 => "Under Voltage",
        0x4210 => "Drive Over Temperature",
        0x4310 => "Motor Over Temperature",
        0x5110 => "Supply Voltage Error",
        0x6010 => "Internal Software Error",
        0x8611 => "Following / Position Limit Error",
        0x8100 => "Communication Error",
        0x8110 => "CAN Overrun",
        0x8120 => "CAN Passive Mode",
        0x8130 => "Heartbeat Error",
        0x8140 => "Bus Off",
        0x9000 => "External Error",
        _ => $"Unknown (0x{code:X4})",
    };
}