// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.
using Core.CANOpen.CiA402;

namespace Wpf.Ui.servoStudio.Models;

#region CiA402 状态机

/// <summary>
/// CiA402 驱动器状态机状态定义
/// </summary>
public enum Cia402State
{
    NotReadyToSwitchOn,
    SwitchOnDisabled,
    ReadyToSwitchOn,
    SwitchedOn,
    OperationEnabled,
    QuickStopActive,
    FaultReactionActive,
    Fault,
    Unknown,
}

/// <summary>
/// CiA402 状态机转换命令
/// </summary>
public enum Cia402StateTransition
{
    Shutdown,           // 2,6,8:  任意 → ReadyToSwitchOn
    SwitchOn,           // 3:      ReadyToSwitchOn → SwitchedOn
    EnableOperation,    // 4:      SwitchedOn → OperationEnabled
    DisableOperation,   // 5:      OperationEnabled → SwitchedOn
    DisableVoltage,     // 7,9,10,12: 任意 → SwitchOnDisabled
    QuickStop,          // 11:     OperationEnabled → QuickStopActive
    FaultReset,         // 15:     Fault → SwitchOnDisabled
}

#endregion

#region 控制字 / 状态字

/// <summary>
/// CiA402 控制字 (0x6040) 位定义
/// </summary>
[Flags]
public enum Cia402ControlWord : ushort
{
    None = 0,
    SwitchOn = 1 << 0,   // Bit 0
    EnableVoltage = 1 << 1,   // Bit 1
    QuickStop = 1 << 2,   // Bit 2
    EnableOperation = 1 << 3,   // Bit 3
    OperationModeSpecific0 = 1 << 4,   // Bit 4 (模式相关)
    OperationModeSpecific1 = 1 << 5,   // Bit 5 (模式相关)
    OperationModeSpecific2 = 1 << 6,   // Bit 6 (模式相关)
    FaultReset = 1 << 7,   // Bit 7
    Halt = 1 << 8,   // Bit 8
    // Bit 9:  保留
    // Bit 10: 保留
    // Bit 11-15: 厂商自定义
}

/// <summary>
/// CiA402 状态字 (0x6041) 位定义
/// </summary>
[Flags]
public enum Cia402StatusWord : ushort
{
    None = 0,
    ReadyToSwitchOn = 1 << 0,   // Bit 0
    SwitchedOn = 1 << 1,   // Bit 1
    OperationEnabled = 1 << 2,   // Bit 2
    Fault = 1 << 3,   // Bit 3
    VoltageEnabled = 1 << 4,   // Bit 4
    QuickStop = 1 << 5,   // Bit 5
    SwitchOnDisabled = 1 << 6,   // Bit 6
    Warning = 1 << 7,   // Bit 7
    Remote = 1 << 9,   // Bit 9
    TargetReached = 1 << 10,  // Bit 10
    InternalLimitActive = 1 << 11,  // Bit 11
    OperationModeSpecific0 = 1 << 12, // Bit 12
    OperationModeSpecific1 = 1 << 13, // Bit 13
    // Bit 14-15: 厂商自定义
}

/// <summary>
/// 控制字预定义命令组合值
/// </summary>
public static class Cia402ControlCommands
{
    // 状态机转换命令 (低 4 位有效)
    public const ushort Shutdown = 0x0006; // xxxx.xxxx.0xxx.0110
    public const ushort SwitchOn = 0x0007; // xxxx.xxxx.0xxx.0111
    public const ushort EnableOperation = 0x000F; // xxxx.xxxx.0xxx.1111
    public const ushort DisableVoltage = 0x0000; // xxxx.xxxx.0xxx.xx0x
    public const ushort QuickStop = 0x0002; // xxxx.xxxx.0xxx.x01x
    public const ushort DisableOperation = 0x0007; // xxxx.xxxx.0xxx.0111
    public const ushort FaultReset = 0x0080; // xxxx.xxxx.1xxx.xxxx (上升沿)

    // 位置模式专用
    public const ushort NewSetPoint = 0x0010; // Bit 4: 新目标位置
    public const ushort ChangeSetImmediately = 0x0020; // Bit 5: 立即变更
    public const ushort AbsolutePosition = 0x0000; // Bit 6=0: 绝对位置
    public const ushort RelativePosition = 0x0040; // Bit 6=1: 相对位置

    // 原点回归模式专用
    public const ushort HomingStart = 0x0010; // Bit 4: 开始回零

    // 通用
    public const ushort Halt = 0x0100; // Bit 8: 暂停
}

/// <summary>
/// 状态字掩码与匹配值，用于判断当前设备状态
/// </summary>
public static class Cia402StatusMasks
{
    // (掩码, 匹配值)
    public static readonly (ushort Mask, ushort Value) NotReadyToSwitchOn = (0x004F, 0x0000);
    public static readonly (ushort Mask, ushort Value) SwitchOnDisabled = (0x004F, 0x0040);
    public static readonly (ushort Mask, ushort Value) ReadyToSwitchOn = (0x006F, 0x0021);
    public static readonly (ushort Mask, ushort Value) SwitchedOn = (0x006F, 0x0023);
    public static readonly (ushort Mask, ushort Value) OperationEnabled = (0x006F, 0x0027);
    public static readonly (ushort Mask, ushort Value) QuickStopActive = (0x006F, 0x0007);
    public static readonly (ushort Mask, ushort Value) FaultReactionActive = (0x004F, 0x000F);
    public static readonly (ushort Mask, ushort Value) Fault = (0x004F, 0x0008);

    /// <summary>
    /// 根据状态字解析当前 CiA402 状态
    /// </summary>
    public static Cia402State ParseState(ushort statusWord)
    {
        if ((statusWord & NotReadyToSwitchOn.Mask) == NotReadyToSwitchOn.Value) return Cia402State.NotReadyToSwitchOn;
        if ((statusWord & Fault.Mask) == Fault.Value) return Cia402State.Fault;
        if ((statusWord & FaultReactionActive.Mask) == FaultReactionActive.Value) return Cia402State.FaultReactionActive;
        if ((statusWord & SwitchOnDisabled.Mask) == SwitchOnDisabled.Value) return Cia402State.SwitchOnDisabled;
        if ((statusWord & ReadyToSwitchOn.Mask) == ReadyToSwitchOn.Value) return Cia402State.ReadyToSwitchOn;
        if ((statusWord & SwitchedOn.Mask) == SwitchedOn.Value) return Cia402State.SwitchedOn;
        if ((statusWord & OperationEnabled.Mask) == OperationEnabled.Value) return Cia402State.OperationEnabled;
        if ((statusWord & QuickStopActive.Mask) == QuickStopActive.Value) return Cia402State.QuickStopActive;
        return Cia402State.Unknown;
    }
}

#endregion

#region 运行模式

/// <summary>
/// CiA402 运行模式 (0x6060 / 0x6061)
/// </summary>
public enum Cia402OperationMode : sbyte
{
    NoModeAssigned = 0,
    ProfilePosition = 1,  // 轮廓位置模式 (pp)
    Velocity = 2,  // 速度模式 (vl)
    ProfileVelocity = 3,  // 轮廓速度模式 (pv)
    ProfileTorque = 4,  // 轮廓转矩模式 (tq)
    Homing = 6,  // 原点回归模式 (hm)
    InterpolatedPosition = 7,  // 插补位置模式 (ip)
    CyclicSynchronousPosition = 8,  // 周期同步位置模式 (csp)
    CyclicSynchronousVelocity = 9,  // 周期同步速度模式 (csv)
    CyclicSynchronousTorque = 10,  // 周期同步转矩模式 (cst)
}

/// <summary>
/// 原点回归方法编号 (0x6098)
/// </summary>
public enum Cia402HomingMethod : sbyte
{
    MethodNotDefined = 0,
    NegativeLimitSwitch = 1,
    PositiveLimitSwitch = 2,
    PositiveHomeSwitchNeg = 3,
    PositiveHomeSwitchPos = 4,
    NegativeHomeSwitchNeg = 5,
    NegativeHomeSwitchPos = 6,
    HomeSwitchNegWithIndex = 7,
    HomeSwitchPosWithIndex = 8,
    HomeSwitchNegWithoutIndex = 9,
    HomeSwitchPosWithoutIndex = 10,
    NegLimitWithIndex = 11,
    PosLimitWithIndex = 12,
    IndexNegative = 33,
    IndexPositive = 34,
    CurrentPosition = 35,
}

#endregion

#region 对象字典索引常量

/// <summary>
/// CiA402 标准对象字典索引 (OD Index)
/// </summary>
public static class Cia402OdIndex
{
    // ===== 通信对象 =====
    public const ushort DeviceType = 0x1000;
    public const ushort ErrorRegister = 0x1001;
    public const ushort ManufacturerStatusRegister = 0x1002;
    public const ushort PreDefinedError = 0x1003;
    public const ushort CobIdSync = 0x1005;
    public const ushort CobIdEmcy = 0x1014;
    public const ushort HeartbeatProducerTime = 0x1017;
    public const ushort IdentityObject = 0x1018;

    // ===== RPDO 通信参数 =====
    public const ushort RxPdo1CommParam = 0x1400;
    public const ushort RxPdo2CommParam = 0x1401;
    public const ushort RxPdo3CommParam = 0x1402;
    public const ushort RxPdo4CommParam = 0x1403;

    // ===== RPDO 映射参数 =====
    public const ushort RxPdo1Mapping = 0x1600;
    public const ushort RxPdo2Mapping = 0x1601;
    public const ushort RxPdo3Mapping = 0x1602;
    public const ushort RxPdo4Mapping = 0x1603;

    // ===== TPDO 通信参数 =====
    public const ushort TxPdo1CommParam = 0x1800;
    public const ushort TxPdo2CommParam = 0x1801;
    public const ushort TxPdo3CommParam = 0x1802;
    public const ushort TxPdo4CommParam = 0x1803;

    // ===== TPDO 映射参数 =====
    public const ushort TxPdo1Mapping = 0x1A00;
    public const ushort TxPdo2Mapping = 0x1A01;
    public const ushort TxPdo3Mapping = 0x1A02;
    public const ushort TxPdo4Mapping = 0x1A03;

    // ===== 控制字 / 状态字 =====
    public const ushort ControlWord = 0x6040;
    public const ushort StatusWord = 0x6041;

    // ===== 运行模式 =====
    public const ushort ModesOfOperation = 0x6060;
    public const ushort ModesOfOperationDisplay = 0x6061;

    // ===== 位置相关 =====
    public const ushort PositionActualInternalValue = 0x6063;
    public const ushort PositionActualValue = 0x6064;
    public const ushort PositionWindow = 0x6067;
    public const ushort PositionWindowTime = 0x6068;
    public const ushort TargetPosition = 0x607A;
    public const ushort PositionRangeLimit = 0x607B;
    public const ushort SoftwarePositionLimit = 0x607D;
    public const ushort MaxProfileVelocity = 0x607F;
    public const ushort ProfileVelocity = 0x6081;
    public const ushort ProfileAcceleration = 0x6083;
    public const ushort ProfileDeceleration = 0x6084;
    public const ushort QuickStopDeceleration = 0x6085;
    public const ushort MotionProfileType = 0x6086;
    public const ushort PositionEncoderResolution = 0x608F;
    public const ushort GearRatio = 0x6091;
    public const ushort FeedConstant = 0x6092;

    // ===== 原点回归 =====
    public const ushort HomingMethod = 0x6098;
    public const ushort HomingSpeeds = 0x6099;
    public const ushort HomingAcceleration = 0x609A;

    // ===== 速度相关 =====
    public const ushort VelocitySensorActualValue = 0x6069;
    public const ushort SensorSelectionCode = 0x606A;
    public const ushort TargetVelocity = 0x60FF;
    public const ushort VelocityActualValue = 0x606C;
    public const ushort VelocityWindow = 0x606D;
    public const ushort VelocityWindowTime = 0x606E;
    public const ushort VelocityThreshold = 0x606F;
    public const ushort VelocityThresholdTime = 0x6070;

    // ===== 转矩相关 =====
    public const ushort TargetTorque = 0x6071;
    public const ushort MaxTorque = 0x6072;
    public const ushort TorqueActualValue = 0x6077;
    public const ushort CurrentActualValue = 0x6078;
    public const ushort TorqueSlope = 0x6087;
    public const ushort TorqueProfileType = 0x6088;

    // ===== 前馈偏移量 (CSP/CSV/CST) =====
    public const ushort PositionOffset = 0x60B0;
    public const ushort VelocityOffset = 0x60B1;
    public const ushort TorqueOffset = 0x60B2;

    // ===== 插补位置模式 =====
    public const ushort InterpolationSubModeSelect = 0x60C0;
    public const ushort InterpolationDataRecord = 0x60C1;
    public const ushort InterpolationTimePeriod = 0x60C2;

    // ===== 数字输入/输出 =====
    public const ushort DigitalInputs = 0x60FD;
    public const ushort DigitalOutputs = 0x60FE;

    // ===== 探针功能 (Touch Probe) =====
    public const ushort TouchProbeFunction = 0x60B8;
    public const ushort TouchProbeStatus = 0x60B9;
    public const ushort TouchProbePos1PosValue = 0x60BA;
    public const ushort TouchProbePos1NegValue = 0x60BB;

    // ===== 额外信息 =====
    public const ushort SupportedDriveModes = 0x6502;
    public const ushort DriveData = 0x6510;
}

#endregion

#region 运动参数结构体

/// <summary>
/// 轮廓位置模式参数
/// </summary>
public struct ProfilePositionParameters
{
    public int TargetPosition { get; set; }
    public uint ProfileVelocity { get; set; }
    public uint ProfileAcceleration { get; set; }
    public uint ProfileDeceleration { get; set; }
    public bool IsRelative { get; set; }
    public bool ChangeImmediately { get; set; }
}

/// <summary>
/// 轮廓速度模式参数
/// </summary>
public struct ProfileVelocityParameters
{
    public int TargetVelocity { get; set; }
    public uint ProfileAcceleration { get; set; }
    public uint ProfileDeceleration { get; set; }
    public uint QuickStopDeceleration { get; set; }
}

/// <summary>
/// 轮廓转矩模式参数
/// </summary>
public struct ProfileTorqueParameters
{
    public short TargetTorque { get; set; }
    public ushort MaxTorque { get; set; }
    public uint TorqueSlope { get; set; }
}

/// <summary>
/// 原点回归模式参数
/// </summary>
public struct HomingParameters
{
    public Cia402HomingMethod Method { get; set; }
    public uint SpeedDuringSearch { get; set; }
    public uint SpeedDuringZero { get; set; }
    public uint HomingAcceleration { get; set; }
}

/// <summary>
/// 周期同步位置模式参数 (CSP)
/// </summary>
public struct CyclicSyncPositionParameters
{
    public int TargetPosition { get; set; }
    public int PositionOffset { get; set; }
    public int VelocityOffset { get; set; }
    public short TorqueOffset { get; set; }
    public uint InterpolationTimePeriodUs { get; set; }
}

/// <summary>
/// 周期同步速度模式参数 (CSV)
/// </summary>
public struct CyclicSyncVelocityParameters
{
    public int TargetVelocity { get; set; }
    public int VelocityOffset { get; set; }
    public short TorqueOffset { get; set; }
    public uint InterpolationTimePeriodUs { get; set; }
}

/// <summary>
/// 周期同步转矩模式参数 (CST)
/// </summary>
public struct CyclicSyncTorqueParameters
{
    public short TargetTorque { get; set; }
    public short TorqueOffset { get; set; }
    public uint InterpolationTimePeriodUs { get; set; }
}

#endregion

#region 错误码

/// <summary>
/// CiA402 标准紧急错误码 (Emergency Error Code)
/// </summary>
public static class Cia402EmergencyCode
{
    public const ushort NoError = 0x0000;
    public const ushort GenericError = 0x1000;
    public const ushort OverCurrent = 0x2310;
    public const ushort ShortCircuit = 0x2320;
    public const ushort OverVoltage = 0x3210;
    public const ushort UnderVoltage = 0x3220;
    public const ushort OverTemperatureDrive = 0x4210;
    public const ushort OverTemperatureMotor = 0x4310;
    public const ushort SupplyVoltageError = 0x5110;
    public const ushort InternalSoftware = 0x6010;
    public const ushort FollowingError = 0x8611;
    public const ushort CommunicationError = 0x8100;
    public const ushort CanOverrun = 0x8110;
    public const ushort CanPassiveMode = 0x8120;
    public const ushort HeartbeatError = 0x8130;
    public const ushort BusOff = 0x8140;
    public const ushort PositionLimitExceeded = 0x8611;
    public const ushort ExternalError = 0x9000;

    /// <summary>
    /// 获取错误码描述
    /// </summary>
    public static string GetDescription(ushort code) => code switch
    {
        NoError => "无错误",
        GenericError => "通用错误",
        OverCurrent => "过电流",
        ShortCircuit => "短路",
        OverVoltage => "过压",
        UnderVoltage => "欠压",
        OverTemperatureDrive => "驱动器过温",
        OverTemperatureMotor => "电机过温",
        SupplyVoltageError => "供电电压异常",
        InternalSoftware => "内部软件错误",
        FollowingError => "跟随误差超限",
        CommunicationError => "通信错误",
        CanOverrun => "CAN 过载",
        CanPassiveMode => "CAN 被动模式",
        HeartbeatError => "心跳超时",
        BusOff => "总线关闭",
        ExternalError => "外部错误",
        _ => $"未知错误 (0x{code:X4})",
    };
}

#endregion

#region 辅助工具

/// <summary>
/// CiA402 辅助方法
/// </summary>
public static class Cia402Helper
{
    /// <summary>
    /// 根据目标状态生成控制字
    /// </summary>
    public static ushort BuildControlWordForTransition(Cia402State currentState, Cia402State targetState)
    {
        return (currentState, targetState) switch
        {
            (Cia402State.SwitchOnDisabled, Cia402State.ReadyToSwitchOn) => Cia402ControlCommands.Shutdown,
            (Cia402State.ReadyToSwitchOn, Cia402State.SwitchedOn) => Cia402ControlCommands.SwitchOn,
            (Cia402State.SwitchedOn, Cia402State.OperationEnabled) => Cia402ControlCommands.EnableOperation,
            (Cia402State.OperationEnabled, Cia402State.SwitchedOn) => Cia402ControlCommands.DisableOperation,
            (Cia402State.OperationEnabled, Cia402State.QuickStopActive) => Cia402ControlCommands.QuickStop,
            (Cia402State.Fault, Cia402State.SwitchOnDisabled) => Cia402ControlCommands.FaultReset,
            (_, Cia402State.SwitchOnDisabled) => Cia402ControlCommands.DisableVoltage,
            _ => throw new InvalidOperationException(
                     $"不支持的状态转换: {currentState} → {targetState}"),
        };
    }

    /// <summary>
    /// 判断状态字中 TargetReached 标志是否置位
    /// </summary>
    public static bool IsTargetReached(ushort statusWord)
        => (statusWord & (ushort)Cia402StatusWord.TargetReached) != 0;

    /// <summary>
    /// 判断是否处于故障状态
    /// </summary>
    public static bool IsFault(ushort statusWord)
        => (statusWord & (ushort)Cia402StatusWord.Fault) != 0;

    /// <summary>
    /// 将 OD 索引和子索引组合为 PDO 映射值 (Index:SubIndex:BitLength)
    /// </summary>
    public static uint BuildPdoMapping(ushort index, byte subIndex, byte bitLength)
        => (uint)(index << 16 | subIndex << 8 | bitLength);
}

#endregion