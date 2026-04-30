// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Core.CANopen;

/// <summary>
/// CANopen NMT 命令（CiA301 §7.2.8）。<br/>
/// 主站通过 COB-ID = 0x000 发布 2 字节命令：[cs, nodeId]，nodeId=0 表示广播。
/// </summary>
public enum NmtCommand : byte
{
    /// <summary>启动远程节点（进入 Operational）。</summary>
    Start = 0x01,
    /// <summary>停止远程节点。</summary>
    Stop = 0x02,
    /// <summary>进入预运行状态。</summary>
    EnterPreOperational = 0x80,
    /// <summary>复位节点（包含应用程序复位）。</summary>
    ResetNode = 0x81,
    /// <summary>仅复位通信。</summary>
    ResetCommunication = 0x82,
}

/// <summary>
/// CANopen NMT 状态（来自 0x700+nodeId 心跳的 byte0，bit7 = toggle 已掩去）。
/// </summary>
public enum NmtState : byte
{
    BootUp = 0x00,
    Stopped = 0x04,
    Operational = 0x05,
    PreOperational = 0x7F,
    Unknown = 0xFF,
}

/// <summary>
/// SDO Abort Codes（CiA301 §7.2.4.3.17）—— 仅含常用项。
/// </summary>
public enum SdoAbortCode : uint
{
    None = 0x0000_0000,
    ToggleBitNotAlternated = 0x0503_0000,
    SdoProtocolTimedOut = 0x0504_0000,
    CommandSpecifierNotValid = 0x0504_0001,
    InvalidBlockSize = 0x0504_0002,
    InvalidSequenceNumber = 0x0504_0003,
    CrcError = 0x0504_0004,
    OutOfMemory = 0x0504_0005,
    UnsupportedAccess = 0x0601_0000,
    AttemptReadWriteOnly = 0x0601_0001,
    AttemptWriteReadOnly = 0x0601_0002,
    ObjectDoesNotExist = 0x0602_0000,
    ObjectCannotBeMappedToPdo = 0x0604_0041,
    PdoMappingExceedsLength = 0x0604_0042,
    GeneralParameterIncompatibility = 0x0604_0043,
    GeneralInternalIncompatibility = 0x0604_0047,
    AccessFailedHardware = 0x0606_0000,
    DataTypeMismatchLength = 0x0607_0010,
    DataTypeMismatchHigh = 0x0607_0012,
    DataTypeMismatchLow = 0x0607_0013,
    SubIndexDoesNotExist = 0x0609_0011,
    InvalidValue = 0x0609_0030,
    ValueTooHigh = 0x0609_0031,
    ValueTooLow = 0x0609_0032,
    GeneralError = 0x0800_0000,
    DataCannotTransferred = 0x0800_0020,
    DataCannotTransferredLocalControl = 0x0800_0021,
    DataCannotTransferredDeviceState = 0x0800_0022,
    NoObjectDictionary = 0x0800_0023,

    // 本地（非协议）错误
    LocalTimeout = 0xFFFF_0001,
    LocalBusClosed = 0xFFFF_0002,
    LocalUnsupportedDataLength = 0xFFFF_0003,
    LocalInvalidResponse = 0xFFFF_0004,
}
