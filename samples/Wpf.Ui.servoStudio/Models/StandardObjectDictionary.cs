// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Wpf.Ui.servoStudio.Models;

/// <summary>
/// 厂家参数页中"协议栈寄存器列表"使用的标准对象字典清单。<br/>
/// 仅放置 CiA301/CiA402 标准对象（以及 EtherCAT 通信对象），便于 EtherCAT 与 CANopen 标签共享。
/// </summary>
public static class StandardObjectDictionary
{
    /// <summary>
    /// EtherCAT 标签使用的对象字典：DS301 通信对象 + EtherCAT SyncManager + CiA402 伺服对象。
    /// </summary>
    public static readonly (ushort Index, byte SubIndex, string Name, SdoDataType DataType)[] EtherCAT =
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

    /// <summary>
    /// CANopen 标签使用的对象字典：DS301 通信对象 + Identity + RPDO/TPDO 通信参数 + CiA402 伺服对象。
    /// 不含 EtherCAT 专属的 SyncManager（0x1C00）。
    /// </summary>
    public static readonly (ushort Index, byte SubIndex, string Name, SdoDataType DataType)[] CANopen =
    [
        // DS301 通信配置区
        (0x1000, 0, "Device Type", SdoDataType.UInt32),
        (0x1001, 0, "Error Register", SdoDataType.UInt8),
        (0x1005, 0, "COB-ID SYNC", SdoDataType.UInt32),
        (0x1008, 0, "Manufacturer Device Name", SdoDataType.VisibleString),
        (0x1009, 0, "Manufacturer Hardware Version", SdoDataType.VisibleString),
        (0x100A, 0, "Manufacturer Software Version", SdoDataType.VisibleString),
        (0x1014, 0, "COB-ID EMCY", SdoDataType.UInt32),
        (0x1017, 0, "Producer Heartbeat Time", SdoDataType.UInt16),

        // Identity Object
        (0x1018, 1, "Identity - Vendor ID", SdoDataType.UInt32),
        (0x1018, 2, "Identity - Product Code", SdoDataType.UInt32),
        (0x1018, 3, "Identity - Revision Number", SdoDataType.UInt32),
        (0x1018, 4, "Identity - Serial Number", SdoDataType.UInt32),

        // RPDO 通信参数 0x1400–0x1403
        (0x1400, 1, "RxPdo1 COB-ID", SdoDataType.UInt32),
        (0x1401, 1, "RxPdo2 COB-ID", SdoDataType.UInt32),
        (0x1402, 1, "RxPdo3 COB-ID", SdoDataType.UInt32),
        (0x1403, 1, "RxPdo4 COB-ID", SdoDataType.UInt32),
        // TPDO 通信参数 0x1800–0x1803
        (0x1800, 1, "TxPdo1 COB-ID", SdoDataType.UInt32),
        (0x1801, 1, "TxPdo2 COB-ID", SdoDataType.UInt32),
        (0x1802, 1, "TxPdo3 COB-ID", SdoDataType.UInt32),
        (0x1803, 1, "TxPdo4 COB-ID", SdoDataType.UInt32),

        // CiA 402 伺服驱动配置区
        (0x6040, 0, "Controlword (控制字)", SdoDataType.UInt16),
        (0x6041, 0, "Statusword (状态字)", SdoDataType.UInt16),
        (0x6060, 0, "Modes of Operation (运行模式)", SdoDataType.Int8),
        (0x6061, 0, "Modes of Operation Display (运行模式显示)", SdoDataType.Int8),
        (0x6064, 0, "Position Actual Value (实际位置)", SdoDataType.Int32),
        (0x606C, 0, "Velocity Actual Value (实际速度)", SdoDataType.Int32),
        (0x6071, 0, "Target Torque (目标转矩)", SdoDataType.Int16),
        (0x6072, 0, "Max Torque (最大转矩)", SdoDataType.UInt16),
        (0x6077, 0, "Torque Actual Value (实际转矩)", SdoDataType.Int16),
        (0x607A, 0, "Target Position (目标位置)", SdoDataType.Int32),
        (0x6081, 0, "Profile Velocity (轮廓速度)", SdoDataType.UInt32),
        (0x6083, 0, "Profile Acceleration (轮廓加速度)", SdoDataType.UInt32),
        (0x6084, 0, "Profile Deceleration (轮廓减速度)", SdoDataType.UInt32),
        (0x6085, 0, "Quick Stop Deceleration (快速停止减速度)", SdoDataType.UInt32),
        (0x6098, 0, "Homing Method (回零方式)", SdoDataType.Int8),
        (0x609A, 0, "Homing Acceleration (回零加速度)", SdoDataType.UInt32),
        (0x60FF, 0, "Target Velocity (目标速度)", SdoDataType.Int32),
        (0x6502, 0, "Supported Drive Modes (支持的运行模式)", SdoDataType.UInt32),
    ];
}
