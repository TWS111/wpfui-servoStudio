// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Wpf.Ui.servoStudio.Models;

/// <summary>
/// 表示一个包含H分组及通信地址等信息的寄存器条目
/// </summary>
public class HRegisterEntry
{
    /// <summary>
    /// H分组及索引，例如 "H00.00"
    /// </summary>
    public string HIndex { get; set; } = string.Empty;

    /// <summary>
    /// 通信地址 (字典对象索引-子索引)，如 "2000-01h"
    /// </summary>
    public string CommAddress { get; set; } = string.Empty;

    /// <summary>
    /// 参数名称，例如 "电机编号"
    /// </summary>
    public string ParameterName { get; set; } = string.Empty;

    /// <summary>
    /// 对应的内部变量名称，例如 "H00_x.MotorNum"
    /// </summary>
    public string VariableName { get; set; } = string.Empty;

    /// <summary>
    /// 最小值
    /// </summary>
    public string MinValue { get; set; } = "-";

    /// <summary>
    /// 最大值
    /// </summary>
    public string MaxValue { get; set; } = "-";

    /// <summary>
    /// 默认值
    /// </summary>
    public string DefaultValue { get; set; } = "-";

    /// <summary>
    /// 是否为只读(监视)参数
    /// </summary>
    public bool IsReadOnly { get; set; } = false;

    /// <summary>
    /// 参数分组名称，例如 "H00 电机参数"
    /// </summary>
    public string GroupName { get; set; } = string.Empty;

    /// <summary>
    /// 参数单位，例如 "rpm"、"A"、"V"、"°C"
    /// </summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>
    /// 从 CommAddress 解析出的 SDO 对象字典索引（如 0x2000）
    /// </summary>
    public ushort SdoIndex
    {
        get
        {
            if (string.IsNullOrEmpty(CommAddress)) return 0;
            // "2000-01h" → 取 "-" 左侧 "2000"
            var parts = CommAddress.Split('-');
            if (parts.Length >= 1 && ushort.TryParse(parts[0], NumberStyles.HexNumber, null, out var idx))
                return idx;
            return 0;
        }
    }

    /// <summary>
    /// 从 CommAddress 解析出的 SDO 子索引（如 0x01）
    /// </summary>
    public byte SdoSubIndex
    {
        get
        {
            if (string.IsNullOrEmpty(CommAddress)) return 0;
            // "2000-01h" → 取 "-" 右侧 "01h" → 去掉 "h"
            var parts = CommAddress.Split('-');
            if (parts.Length >= 2)
            {
                var sub = parts[1].TrimEnd('h', 'H');
                if (byte.TryParse(sub, NumberStyles.HexNumber, null, out var s))
                    return s;
            }

            return 0;
        }
    }
}

/// <summary>
/// 集中管理的H分组寄存器表，结合了CiA301通讯地址
/// </summary>
public static class HVariables
{
    public static readonly List<HRegisterEntry> RegisterTable = new()
    {
        // H00 组：电机参数
        new HRegisterEntry { HIndex = "H00.00", CommAddress = "2000-01h", ParameterName = "电机编号", VariableName = "H00_x.MotorNum", MinValue = "-", MaxValue = "-", DefaultValue = "1", GroupName = "H00 电机参数" },
        new HRegisterEntry { HIndex = "H00.01", CommAddress = "2000-02h", ParameterName = "电机额定功率", VariableName = "H00_x.MotorRatedPower", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H00 电机参数", Unit = "W" },
        new HRegisterEntry { HIndex = "H00.02", CommAddress = "2000-03h", ParameterName = "电机额定转速", VariableName = "H00_x.MotorRatedSpeed", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H00 电机参数", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H00.03", CommAddress = "2000-04h", ParameterName = "电机额定转矩", VariableName = "H00_x.MotorRatedTorque", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H00 电机参数", Unit = "N·m" },
        new HRegisterEntry { HIndex = "H00.04", CommAddress = "2000-05h", ParameterName = "电机额定电流", VariableName = "H00_x.MotorRatedCurrent", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H00 电机参数", Unit = "A" },
        new HRegisterEntry { HIndex = "H00.05", CommAddress = "2000-06h", ParameterName = "电机额定电压", VariableName = "H00_x.MotorRatedVoltage", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H00 电机参数", Unit = "V" },
        new HRegisterEntry { HIndex = "H00.06", CommAddress = "2000-07h", ParameterName = "电机相电阻", VariableName = "H00_x.MotorPhaseResistance", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H00 电机参数", Unit = "mΩ" },
        new HRegisterEntry { HIndex = "H00.07", CommAddress = "2000-09h", ParameterName = "电机相电感", VariableName = "H00_x.MotorPhaseInductance", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H00 电机参数", Unit = "μH" },
        new HRegisterEntry { HIndex = "H00.09", CommAddress = "2000-0Ah", ParameterName = "电机反电动势系数", VariableName = "H00_x.MotorBackEmf", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H00 电机参数", Unit = "mV/rpm" },
        new HRegisterEntry { HIndex = "H00.08", CommAddress = "2000-08h", ParameterName = "电机编码器调零状态", VariableName = "H00_x.EncodeState", MinValue = "-", MaxValue = "-", DefaultValue = "1", GroupName = "H00 电机参数" },
        new HRegisterEntry { HIndex = "H00.15", CommAddress = "2000-10h", ParameterName = "电机最大转速", VariableName = "H00_x.MotorMaxSpeed", MinValue = "-", MaxValue = "-", DefaultValue = "1", GroupName = "H00 电机参数", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H00.18", CommAddress = "2000-13h", ParameterName = "电机极对数", VariableName = "H00_x.MotorPolePairs", MinValue = "1", MaxValue = "64", DefaultValue = "4", GroupName = "H00 电机参数" },
        new HRegisterEntry { HIndex = "H00.20", CommAddress = "2000-15h", ParameterName = "编码器类型", VariableName = "H00_x.EncoderType", MinValue = "0", MaxValue = "4", DefaultValue = "0", GroupName = "H00 电机参数" },
        new HRegisterEntry { HIndex = "H00.21", CommAddress = "2000-16h", ParameterName = "编码器分辨率(PPR)", VariableName = "H00_x.EncoderPPR", MinValue = "1", MaxValue = "1048576", DefaultValue = "10000", GroupName = "H00 电机参数" },
        new HRegisterEntry { HIndex = "H00.22", CommAddress = "2000-17h", ParameterName = "多圈编码器使能", VariableName = "H00_x.MultiTurnEn", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H00 电机参数" },
        new HRegisterEntry { HIndex = "H00.43", CommAddress = "2000-2Bh", ParameterName = "电机最大电流", VariableName = "H00_x.MotorMaxIvalue", MinValue = "-", MaxValue = "-", DefaultValue = "1", GroupName = "H00 电机参数", Unit = "A" },

        // H01 组：驱动器及硬件参数
        new HRegisterEntry { HIndex = "H01.00", CommAddress = "2001-01h", ParameterName = "MCU 软件版本号", VariableName = "H01_x.SoftwareVersion", MinValue = "-", MaxValue = "-", DefaultValue = "23", GroupName = "H01 驱动器参数", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H01.02", CommAddress = "2001-03h", ParameterName = "驱动器编号", VariableName = "H01_x.ControlNum", MinValue = "-", MaxValue = "-", DefaultValue = "1", GroupName = "H01 驱动器参数" },
        new HRegisterEntry { HIndex = "H01.03", CommAddress = "2001-04h", ParameterName = "驱动器最大输出电流", VariableName = "H01_x.ControllerMaxIVuale", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H01 驱动器参数", Unit = "A" },
        new HRegisterEntry { HIndex = "H01.04", CommAddress = "2001-05h", ParameterName = "驱动器额定输出电流", VariableName = "H01_x.ControllerTypeIVuale", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H01 驱动器参数", Unit = "A" },
        new HRegisterEntry { HIndex = "H01.05", CommAddress = "2001-06h", ParameterName = "驱动器电流采样电阻", VariableName = "H01_x.AdcSampleRValue", MinValue = "-", MaxValue = "-", DefaultValue = "2", GroupName = "H01 驱动器参数" },
        new HRegisterEntry { HIndex = "H01.08", CommAddress = "2001-09h", ParameterName = "驱动器温度报警阈值", VariableName = "RA.TempWrite", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H01 驱动器参数", Unit = "°C" },
        new HRegisterEntry { HIndex = "H01.09", CommAddress = "2001-0Ah", ParameterName = "过压保护值", VariableName = "RA.OverVoltage", MinValue = "-", MaxValue = "-", DefaultValue = "38", GroupName = "H01 驱动器参数", Unit = "V" },
        new HRegisterEntry { HIndex = "H01.11", CommAddress = "2001-0Ch", ParameterName = "欠压保护值", VariableName = "RA.UnderVoltage", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H01 驱动器参数", Unit = "V" },
        new HRegisterEntry { HIndex = "H01.13", CommAddress = "2001-0Dh", ParameterName = "硬件DAC过流值", VariableName = "OverHardcurrentValue", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H01 驱动器参数", Unit = "A" },
        new HRegisterEntry { HIndex = "H01.14", CommAddress = "2001-0Eh", ParameterName = "软件过流值", VariableName = "RA.CurrentProtectWrite", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H01 驱动器参数", Unit = "A" },
        new HRegisterEntry { HIndex = "H01.15", CommAddress = "2001-0Fh", ParameterName = "堵转保护最小转速", VariableName = "H01_x.STALMinSpeed", MinValue = "-", MaxValue = "-", DefaultValue = "50", GroupName = "H01 驱动器参数", Unit = "rpm" },

        // H02 组：基本控制参数
        new HRegisterEntry { HIndex = "H02.00", CommAddress = "2002-01h", ParameterName = "控制模式选择", VariableName = "CtrlMode", MinValue = "0", MaxValue = "4", DefaultValue = "3", GroupName = "H02 基本控制" },
        new HRegisterEntry { HIndex = "H02.02", CommAddress = "2002-03h", ParameterName = "旋转方向选择", VariableName = "mcFocCtrl.PosiResponeFlag", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H02 基本控制" },
        new HRegisterEntry { HIndex = "H02.05", CommAddress = "2002-06h", ParameterName = "使能OFF停机方式选择", VariableName = "H02_x.OffMode", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H02 基本控制" },
        // —— 参考汇川 SV680 H02.06/07/08 ——
        new HRegisterEntry { HIndex = "H02.06", CommAddress = "2002-07h", ParameterName = "故障停机方式选择", VariableName = "H02_x.FaultStopMode", MinValue = "0", MaxValue = "3", DefaultValue = "0", GroupName = "H02 基本控制" },
        new HRegisterEntry { HIndex = "H02.07", CommAddress = "2002-08h", ParameterName = "急停停机方式选择", VariableName = "H02_x.QuickStopMode", MinValue = "0", MaxValue = "3", DefaultValue = "1", GroupName = "H02 基本控制" },
        new HRegisterEntry { HIndex = "H02.08", CommAddress = "2002-09h", ParameterName = "主电源缺相处理方式", VariableName = "H02_x.PowerLossMode", MinValue = "0", MaxValue = "2", DefaultValue = "0", GroupName = "H02 基本控制" },
        new HRegisterEntry { HIndex = "H02.30", CommAddress = "2002-1Fh", ParameterName = "用户密码", VariableName = "H02_x.UserKey", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H02 基本控制" },
        new HRegisterEntry { HIndex = "H02.31", CommAddress = "2002-20h", ParameterName = "系统参数初始化", VariableName = "RA.ResetFlag", MinValue = "-", MaxValue = "-", DefaultValue = "0", GroupName = "H02 基本控制" },

        // H04 组：IO参数
        new HRegisterEntry { HIndex = "H04.00", CommAddress = "2004-01h", ParameterName = "DI1 功能选择", VariableName = "H04_x.DI1_Func", MinValue = "0", MaxValue = "0xFF", DefaultValue = "0", GroupName = "H04 IO参数" },
        new HRegisterEntry { HIndex = "H04.01", CommAddress = "2004-02h", ParameterName = "DI2 功能选择", VariableName = "H04_x.DI2_Func", MinValue = "0", MaxValue = "0xFF", DefaultValue = "0", GroupName = "H04 IO参数" },
        new HRegisterEntry { HIndex = "H04.02", CommAddress = "2004-03h", ParameterName = "DI3 功能选择", VariableName = "H04_x.DI3_Func", MinValue = "0", MaxValue = "0xFF", DefaultValue = "0", GroupName = "H04 IO参数" },
        new HRegisterEntry { HIndex = "H04.03", CommAddress = "2004-04h", ParameterName = "DI4 功能选择", VariableName = "H04_x.DI4_Func", MinValue = "0", MaxValue = "0xFF", DefaultValue = "0", GroupName = "H04 IO参数" },
        new HRegisterEntry { HIndex = "H04.04", CommAddress = "2004-05h", ParameterName = "DO1 功能选择", VariableName = "H04_x.DO1_Func", MinValue = "0", MaxValue = "0xFF", DefaultValue = "0", GroupName = "H04 IO参数" },
        new HRegisterEntry { HIndex = "H04.05", CommAddress = "2004-06h", ParameterName = "DO2 功能选择", VariableName = "H04_x.DO2_Func", MinValue = "0", MaxValue = "0xFF", DefaultValue = "0", GroupName = "H04 IO参数" },
        new HRegisterEntry { HIndex = "H04.06", CommAddress = "2004-07h", ParameterName = "DI 滤波时间", VariableName = "H04_x.DI_FilterTime", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "10", GroupName = "H04 IO参数", Unit = "ms" },
        new HRegisterEntry { HIndex = "H04.07", CommAddress = "2004-08h", ParameterName = "DI 极性配置", VariableName = "H04_x.DI_Polarity", MinValue = "0", MaxValue = "0xFF", DefaultValue = "0", GroupName = "H04 IO参数" },
        new HRegisterEntry { HIndex = "H04.08", CommAddress = "2004-09h", ParameterName = "DO 极性配置", VariableName = "H04_x.DO_Polarity", MinValue = "0", MaxValue = "0xFF", DefaultValue = "0", GroupName = "H04 IO参数" },
        new HRegisterEntry { HIndex = "H04.10", CommAddress = "2004-0Bh", ParameterName = "DI 输入状态", VariableName = "H04_x.DI_Status", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H04 IO参数", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H04.11", CommAddress = "2004-0Ch", ParameterName = "DO 输出状态", VariableName = "H04_x.DO_Status", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H04 IO参数", IsReadOnly = true },

        // H05 组：位置控制参数
        // —— 电子齿轮（参考汇川 SV680 H05.00~H05.06）——
        new HRegisterEntry { HIndex = "H05.00", CommAddress = "2005-01h", ParameterName = "位置指令来源", VariableName = "H05_x.PosCmdSource", MinValue = "0", MaxValue = "2", DefaultValue = "0", GroupName = "H05 位置控制" },
        new HRegisterEntry { HIndex = "H05.02", CommAddress = "2005-03h", ParameterName = "电子齿轮分子1", VariableName = "H05_x.GearNum1", MinValue = "1", MaxValue = "1073741824", DefaultValue = "1", GroupName = "H05 位置控制" },
        new HRegisterEntry { HIndex = "H05.04", CommAddress = "2005-05h", ParameterName = "电子齿轮分母1", VariableName = "H05_x.GearDen1", MinValue = "1", MaxValue = "1073741824", DefaultValue = "1", GroupName = "H05 位置控制" },
        new HRegisterEntry { HIndex = "H05.06", CommAddress = "2005-07h", ParameterName = "电子齿轮分子2", VariableName = "H05_x.GearNum2", MinValue = "1", MaxValue = "1073741824", DefaultValue = "1", GroupName = "H05 位置控制" },
        new HRegisterEntry { HIndex = "H05.07", CommAddress = "2005-08h", ParameterName = "尺比分子", VariableName = "RA.GearRatios", MinValue = "1", MaxValue = "0xFFFF", DefaultValue = "400", GroupName = "H05 位置控制" },
        new HRegisterEntry { HIndex = "H05.08", CommAddress = "2005-09h", ParameterName = "电子齿轮分母2", VariableName = "H05_x.GearDen2", MinValue = "1", MaxValue = "1073741824", DefaultValue = "1", GroupName = "H05 位置控制" },
        new HRegisterEntry { HIndex = "H05.15", CommAddress = "2005-10h", ParameterName = "脉冲指令模式", VariableName = "H05_x.PulseMode", MinValue = "1", MaxValue = "3", DefaultValue = "1", GroupName = "H05 位置控制" },
        new HRegisterEntry { HIndex = "H05.16", CommAddress = "2005-11h", ParameterName = "相对位置圈数增量", VariableName = "mcFocCtrl.TargetAngle_msbX", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "0", GroupName = "H05 位置控制" },
        new HRegisterEntry { HIndex = "H05.17", CommAddress = "2005-12h", ParameterName = "相对位置单圈增量", VariableName = "mcFocCtrl.TargetAngle_lsbX", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "0", GroupName = "H05 位置控制" },
        new HRegisterEntry { HIndex = "H05.20", CommAddress = "2005-15h", ParameterName = "定位接近信号阈值", VariableName = "H05_x.PositionNearThreshold", MinValue = "1", MaxValue = "65535", DefaultValue = "65535", GroupName = "H05 位置控制" },
        new HRegisterEntry { HIndex = "H05.21", CommAddress = "2005-16h", ParameterName = "定位完成阈值", VariableName = "H05_x.PositionThreshold", MinValue = "5", MaxValue = "0xFF", DefaultValue = "30", GroupName = "H05 位置控制" },
        // —— S 型曲线 / 平滑滤波（参考汇川 SV680 H05.30/31）——
        new HRegisterEntry { HIndex = "H05.30", CommAddress = "2005-1Fh", ParameterName = "位置指令一阶低通滤波时间常数", VariableName = "H05_x.PositionLpfTimeMs", MinValue = "0", MaxValue = "1000", DefaultValue = "0", GroupName = "H05 位置控制", Unit = "ms" },
        new HRegisterEntry { HIndex = "H05.31", CommAddress = "2005-20h", ParameterName = "位置指令S曲线滤波时间", VariableName = "H05_x.PositionSCurveFilterMs", MinValue = "0", MaxValue = "1000", DefaultValue = "0", GroupName = "H05 位置控制", Unit = "ms" },
        // —— 原点回归（回零，参考汇川 SV680 H05.32~H05.37）——
        new HRegisterEntry { HIndex = "H05.32", CommAddress = "2005-21h", ParameterName = "原点回归触发方式", VariableName = "H05_x.HomingTrigger", MinValue = "0", MaxValue = "5", DefaultValue = "0", GroupName = "H05 位置控制" },
        new HRegisterEntry { HIndex = "H05.33", CommAddress = "2005-22h", ParameterName = "原点回归高速搜索速度", VariableName = "H05_x.HomingHighSpeed", MinValue = "0", MaxValue = "3000", DefaultValue = "100", GroupName = "H05 位置控制", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H05.34", CommAddress = "2005-23h", ParameterName = "原点回归低速找零速度", VariableName = "H05_x.HomingLowSpeed", MinValue = "0", MaxValue = "1000", DefaultValue = "10", GroupName = "H05 位置控制", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H05.35", CommAddress = "2005-24h", ParameterName = "原点回归加减速时间", VariableName = "H05_x.HomingAccDecMs", MinValue = "0", MaxValue = "65535", DefaultValue = "1000", GroupName = "H05 位置控制", Unit = "ms" },
        new HRegisterEntry { HIndex = "H05.36", CommAddress = "2005-25h", ParameterName = "机械原点偏移量", VariableName = "MR.HallAngleOffset", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H05 位置控制" },
        new HRegisterEntry { HIndex = "H05.37", CommAddress = "2005-26h", ParameterName = "原点回归限位时间", VariableName = "H05_x.HomingTimeoutMs", MinValue = "0", MaxValue = "65535", DefaultValue = "50000", GroupName = "H05 位置控制", Unit = "ms" },

        // H06 组：速度控制参数
        new HRegisterEntry { HIndex = "H06.00", CommAddress = "2006-01h", ParameterName = "速度指令来源", VariableName = "H06_x.SpeedCmdSource", MinValue = "0", MaxValue = "5", DefaultValue = "0", GroupName = "H06 速度控制" },
        new HRegisterEntry { HIndex = "H06.01", CommAddress = "2006-02h", ParameterName = "模拟量速度指令设定", VariableName = "H06_x.AnalogSpeedRef", MinValue = "-3000", MaxValue = "3000", DefaultValue = "0", GroupName = "H06 速度控制", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H06.03", CommAddress = "2006-04h", ParameterName = "速度值设定", VariableName = "RA.LimitSpeedWrite", MinValue = "0", MaxValue = "1500", DefaultValue = "0", GroupName = "H06 速度控制", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H06.04", CommAddress = "2006-05h", ParameterName = "点动运行速度", VariableName = "H06_x.JogSpeed", MinValue = "0", MaxValue = "3000", DefaultValue = "100", GroupName = "H06 速度控制", Unit = "rpm" },
        // —— T 型曲线（梯形加减速）参数（参考汇川 SV680 H06.05/06）——
        new HRegisterEntry { HIndex = "H06.05", CommAddress = "2006-06h", ParameterName = "加速时间常数（T 曲线）", VariableName = "H06_x.AccTimeMs", MinValue = "0", MaxValue = "65535", DefaultValue = "100", GroupName = "H06 速度控制", Unit = "ms" },
        new HRegisterEntry { HIndex = "H06.06", CommAddress = "2006-07h", ParameterName = "减速时间常数（T 曲线）", VariableName = "H06_x.DecTimeMs", MinValue = "0", MaxValue = "65535", DefaultValue = "100", GroupName = "H06 速度控制", Unit = "ms" },
        // —— S 型曲线 Jerk 段时间（参考汇川 SV680 H06.07）——
        new HRegisterEntry { HIndex = "H06.07", CommAddress = "2006-08h", ParameterName = "S 曲线时间（Jerk 段）", VariableName = "H06_x.SCurveTimeMs", MinValue = "0", MaxValue = "1000", DefaultValue = "0", GroupName = "H06 速度控制", Unit = "ms" },
        new HRegisterEntry { HIndex = "H06.10", CommAddress = "2006-0Bh", ParameterName = "零速钳位功能使能", VariableName = "H06_x.ZeroClampEn", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H06 速度控制" },
        new HRegisterEntry { HIndex = "H06.11", CommAddress = "2006-0Ch", ParameterName = "零速钳位阈值", VariableName = "H06_x.ZeroClampThreshold", MinValue = "0", MaxValue = "200", DefaultValue = "10", GroupName = "H06 速度控制", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H06.18", CommAddress = "2006-13h", ParameterName = "速度达到信号阈值", VariableName = "H06_x.SpeedThreshold", MinValue = "5", MaxValue = "0xFF", DefaultValue = "10", GroupName = "H06 速度控制", Unit = "rpm" },

        // H07 组：转矩控制参数
        new HRegisterEntry { HIndex = "H07.00", CommAddress = "2007-01h", ParameterName = "转矩指令来源", VariableName = "H07_x.TorqueCmdSource", MinValue = "0", MaxValue = "5", DefaultValue = "0", GroupName = "H07 转矩控制" },
        new HRegisterEntry { HIndex = "H07.03", CommAddress = "2007-04h", ParameterName = "转矩设置（电流给定）", VariableName = "RA.TCurrentWrite", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "0", GroupName = "H07 转矩控制", Unit = "A" },
        new HRegisterEntry { HIndex = "H07.05", CommAddress = "2007-06h", ParameterName = "转矩指令低通滤波时间常数", VariableName = "H07_x.TorqueLpfTimeMs", MinValue = "0", MaxValue = "1000", DefaultValue = "0", GroupName = "H07 转矩控制", Unit = "ms" },
        new HRegisterEntry { HIndex = "H07.09", CommAddress = "2007-0Ah", ParameterName = "正内部转矩限制", VariableName = "H07_x.SoutMax_Cur", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "18", GroupName = "H07 转矩控制", Unit = "A" },
        new HRegisterEntry { HIndex = "H07.10", CommAddress = "2007-0Bh", ParameterName = "负内部转矩限制", VariableName = "H07_x.SoutMin_Cur", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "19", GroupName = "H07 转矩控制", Unit = "A" },
        new HRegisterEntry { HIndex = "H07.11", CommAddress = "2007-0Ch", ParameterName = "速度控制模式正向转矩限制", VariableName = "H07_x.SpeedModeTorqueLimitPos", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "18", GroupName = "H07 转矩控制", Unit = "A" },
        new HRegisterEntry { HIndex = "H07.12", CommAddress = "2007-0Dh", ParameterName = "速度控制模式负向转矩限制", VariableName = "H07_x.SpeedModeTorqueLimitNeg", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "18", GroupName = "H07 转矩控制", Unit = "A" },

        // H08 组：控制环增益
        new HRegisterEntry { HIndex = "H08.00", CommAddress = "2008-01h", ParameterName = "速度环增益（第一增益）", VariableName = "RA.SpeedkpWrite", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "7371", GroupName = "H08 控制环增益" },
        new HRegisterEntry { HIndex = "H08.01", CommAddress = "2008-02h", ParameterName = "速度环积分时间常数（第一）", VariableName = "RA.SpeedkiWrite", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "169", GroupName = "H08 控制环增益" },
        new HRegisterEntry { HIndex = "H08.02", CommAddress = "2008-03h", ParameterName = "位置环增益（第一增益）", VariableName = "RA.FlashPKP_Sw", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "1749", GroupName = "H08 控制环增益" },
        new HRegisterEntry { HIndex = "H08.03", CommAddress = "2008-04h", ParameterName = "位置环微分（第一）", VariableName = "RA.FlashPKD_Sw", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "1900", GroupName = "H08 控制环增益" },
        new HRegisterEntry { HIndex = "H08.04", CommAddress = "2008-05h", ParameterName = "速度环增量", VariableName = "RA.ACCSpeedWrite", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "500", GroupName = "H08 控制环增益" },
        new HRegisterEntry { HIndex = "H08.05", CommAddress = "2008-06h", ParameterName = "速度环减量", VariableName = "RA.DecValueWrite", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "500", GroupName = "H08 控制环增益" },
        new HRegisterEntry { HIndex = "H08.06", CommAddress = "2008-07h", ParameterName = "位置环速度正向输出限幅", VariableName = "H08_x.PositionSoutMax", MinValue = "0", MaxValue = "1500", DefaultValue = "1200", GroupName = "H08 控制环增益", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H08.07", CommAddress = "2008-08h", ParameterName = "位置环速度反向输出限幅", VariableName = "H08_x.PositionSoutMin", MinValue = "0", MaxValue = "1500", DefaultValue = "1200", GroupName = "H08 控制环增益", Unit = "rpm" },
        // —— 第二增益组（参考汇川 SV680 H08.08~H08.12）——
        new HRegisterEntry { HIndex = "H08.08", CommAddress = "2008-09h", ParameterName = "速度环增益（第二增益）", VariableName = "H08_x.SpeedKp2", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "7371", GroupName = "H08 控制环增益" },
        new HRegisterEntry { HIndex = "H08.09", CommAddress = "2008-0Ah", ParameterName = "速度环积分时间常数（第二）", VariableName = "H08_x.SpeedKi2", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "169", GroupName = "H08 控制环增益" },
        new HRegisterEntry { HIndex = "H08.10", CommAddress = "2008-0Bh", ParameterName = "位置环增益（第二增益）", VariableName = "H08_x.PositionKp2", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "1749", GroupName = "H08 控制环增益" },
        new HRegisterEntry { HIndex = "H08.11", CommAddress = "2008-0Ch", ParameterName = "位置环微分（第二）", VariableName = "H08_x.PositionKd2", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "1900", GroupName = "H08 控制环增益" },
        new HRegisterEntry { HIndex = "H08.12", CommAddress = "2008-0Dh", ParameterName = "增益切换模式", VariableName = "H08_x.GainSwitchMode", MinValue = "0", MaxValue = "4", DefaultValue = "0", GroupName = "H08 控制环增益" },
        new HRegisterEntry { HIndex = "H08.13", CommAddress = "2008-0Eh", ParameterName = "增益切换延迟时间", VariableName = "H08_x.GainSwitchDelayMs", MinValue = "0", MaxValue = "1000", DefaultValue = "0", GroupName = "H08 控制环增益", Unit = "ms" },
        new HRegisterEntry { HIndex = "H08.14", CommAddress = "2008-0Fh", ParameterName = "增益切换速度阈值", VariableName = "H08_x.GainSwitchSpeedThreshold", MinValue = "0", MaxValue = "3000", DefaultValue = "200", GroupName = "H08 控制环增益", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H08.15", CommAddress = "2008-10h", ParameterName = "增益切换位置误差阈值", VariableName = "H08_x.GainSwitchPosErrThreshold", MinValue = "0", MaxValue = "65535", DefaultValue = "1000", GroupName = "H08 控制环增益" },
        // —— 前馈与模型跟踪（参考汇川 SV680 H08.20~H08.23）——
        new HRegisterEntry { HIndex = "H08.20", CommAddress = "2008-15h", ParameterName = "模型跟踪控制增益", VariableName = "H08_x.ModelTrackingGain", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "0", GroupName = "H08 控制环增益" },
        new HRegisterEntry { HIndex = "H08.21", CommAddress = "2008-16h", ParameterName = "速度前馈增益", VariableName = "H08_x.SpeedFeedForward", MinValue = "0", MaxValue = "1000", DefaultValue = "0", GroupName = "H08 控制环增益" },
        new HRegisterEntry { HIndex = "H08.22", CommAddress = "2008-17h", ParameterName = "转矩前馈增益", VariableName = "H08_x.TorqueFeedForward", MinValue = "0", MaxValue = "1000", DefaultValue = "0", GroupName = "H08 控制环增益" },
        new HRegisterEntry { HIndex = "H08.23", CommAddress = "2008-18h", ParameterName = "前馈低通滤波时间常数", VariableName = "H08_x.FeedForwardLpfTimeMs", MinValue = "0", MaxValue = "1000", DefaultValue = "0", GroupName = "H08 控制环增益", Unit = "ms" },

        // H09 组：自调谐 / 陷波滤波 / 振动抑制（参考汇川 SV680 H09）
        new HRegisterEntry { HIndex = "H09.00", CommAddress = "2009-01h", ParameterName = "自动调谐模式", VariableName = "H09_x.AutoTuningMode", MinValue = "0", MaxValue = "3", DefaultValue = "0", GroupName = "H09 自调谐" },
        new HRegisterEntry { HIndex = "H09.01", CommAddress = "2009-02h", ParameterName = "机械刚性等级", VariableName = "H09_x.StiffnessLevel", MinValue = "0", MaxValue = "31", DefaultValue = "12", GroupName = "H09 自调谐" },
        new HRegisterEntry { HIndex = "H09.02", CommAddress = "2009-03h", ParameterName = "负载惯量比", VariableName = "H09_x.InertiaRatio", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "100", GroupName = "H09 自调谐" },
        new HRegisterEntry { HIndex = "H09.03", CommAddress = "2009-04h", ParameterName = "惯量辨识使能", VariableName = "H09_x.InertiaIdentEn", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H09 自调谐" },
        new HRegisterEntry { HIndex = "H09.10", CommAddress = "2009-0Bh", ParameterName = "机械振动频率检测", VariableName = "H09_x.VibFreqDetect", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "0", GroupName = "H09 自调谐", Unit = "Hz", IsReadOnly = true },
        // 陷波滤波器1
        new HRegisterEntry { HIndex = "H09.12", CommAddress = "2009-0Dh", ParameterName = "陷波滤波器1陷波频率", VariableName = "H09_x.NotchFreq1", MinValue = "0", MaxValue = "5000", DefaultValue = "0", GroupName = "H09 自调谐", Unit = "Hz" },
        new HRegisterEntry { HIndex = "H09.13", CommAddress = "2009-0Eh", ParameterName = "陷波滤波器1陷波深度", VariableName = "H09_x.NotchDepth1", MinValue = "0", MaxValue = "99", DefaultValue = "0", GroupName = "H09 自调谐" },
        new HRegisterEntry { HIndex = "H09.14", CommAddress = "2009-0Fh", ParameterName = "陷波滤波器1陷波带宽", VariableName = "H09_x.NotchBW1", MinValue = "0", MaxValue = "10", DefaultValue = "2", GroupName = "H09 自调谐" },
        new HRegisterEntry { HIndex = "H09.15", CommAddress = "2009-10h", ParameterName = "陷波滤波器1使能", VariableName = "H09_x.NotchEn1", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H09 自调谐" },
        // 陷波滤波器2
        new HRegisterEntry { HIndex = "H09.16", CommAddress = "2009-11h", ParameterName = "陷波滤波器2陷波频率", VariableName = "H09_x.NotchFreq2", MinValue = "0", MaxValue = "5000", DefaultValue = "0", GroupName = "H09 自调谐", Unit = "Hz" },
        new HRegisterEntry { HIndex = "H09.17", CommAddress = "2009-12h", ParameterName = "陷波滤波器2陷波深度", VariableName = "H09_x.NotchDepth2", MinValue = "0", MaxValue = "99", DefaultValue = "0", GroupName = "H09 自调谐" },
        new HRegisterEntry { HIndex = "H09.18", CommAddress = "2009-13h", ParameterName = "陷波滤波器2陷波带宽", VariableName = "H09_x.NotchBW2", MinValue = "0", MaxValue = "10", DefaultValue = "2", GroupName = "H09 自调谐" },
        new HRegisterEntry { HIndex = "H09.19", CommAddress = "2009-14h", ParameterName = "陷波滤波器2使能", VariableName = "H09_x.NotchEn2", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H09 自调谐" },
        // 低频振动抑制
        new HRegisterEntry { HIndex = "H09.30", CommAddress = "2009-1Fh", ParameterName = "低频振动抑制频率1", VariableName = "H09_x.LowVibFreq1", MinValue = "0", MaxValue = "500", DefaultValue = "0", GroupName = "H09 自调谐", Unit = "0.1Hz" },
        new HRegisterEntry { HIndex = "H09.31", CommAddress = "2009-20h", ParameterName = "低频振动抑制幅值1", VariableName = "H09_x.LowVibAmp1", MinValue = "0", MaxValue = "100", DefaultValue = "0", GroupName = "H09 自调谐" },
        new HRegisterEntry { HIndex = "H09.32", CommAddress = "2009-21h", ParameterName = "低频振动抑制频率2", VariableName = "H09_x.LowVibFreq2", MinValue = "0", MaxValue = "500", DefaultValue = "0", GroupName = "H09 自调谐", Unit = "0.1Hz" },
        new HRegisterEntry { HIndex = "H09.33", CommAddress = "2009-22h", ParameterName = "低频振动抑制幅值2", VariableName = "H09_x.LowVibAmp2", MinValue = "0", MaxValue = "100", DefaultValue = "0", GroupName = "H09 自调谐" },

        // H0A 组：保护参数（参考汇川 SV680 H0A）
        new HRegisterEntry { HIndex = "H0A.00", CommAddress = "200A-01h", ParameterName = "超速保护阈值", VariableName = "H0A_x.OverSpeedThreshold", MinValue = "0", MaxValue = "10000", DefaultValue = "6000", GroupName = "H0A 保护参数", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H0A.01", CommAddress = "200A-02h", ParameterName = "超速保护使能", VariableName = "H0A_x.OverSpeedEn", MinValue = "0", MaxValue = "1", DefaultValue = "1", GroupName = "H0A 保护参数" },
        new HRegisterEntry { HIndex = "H0A.04", CommAddress = "200A-05h", ParameterName = "电机过载保护时间", VariableName = "H0A_x.MotorOverloadTimeS", MinValue = "0", MaxValue = "65535", DefaultValue = "3600", GroupName = "H0A 保护参数", Unit = "s" },
        new HRegisterEntry { HIndex = "H0A.05", CommAddress = "200A-06h", ParameterName = "电机过载保护使能", VariableName = "H0A_x.MotorOverloadEn", MinValue = "0", MaxValue = "1", DefaultValue = "1", GroupName = "H0A 保护参数" },
        new HRegisterEntry { HIndex = "H0A.06", CommAddress = "200A-07h", ParameterName = "位置跟踪误差过大保护阈值", VariableName = "H0A_x.PosTrackErrThreshold", MinValue = "0", MaxValue = "65535", DefaultValue = "32767", GroupName = "H0A 保护参数" },
        new HRegisterEntry { HIndex = "H0A.07", CommAddress = "200A-08h", ParameterName = "位置跟踪误差过大保护使能", VariableName = "H0A_x.PosTrackErrEn", MinValue = "0", MaxValue = "1", DefaultValue = "1", GroupName = "H0A 保护参数" },
        new HRegisterEntry { HIndex = "H0A.10", CommAddress = "200A-0Bh", ParameterName = "驱动器过热保护温度", VariableName = "H0A_x.DriveOverTempC", MinValue = "0", MaxValue = "150", DefaultValue = "85", GroupName = "H0A 保护参数", Unit = "°C" },
        new HRegisterEntry { HIndex = "H0A.11", CommAddress = "200A-0Ch", ParameterName = "驱动器欠压故障阈值", VariableName = "H0A_x.UnderVoltageThreshold", MinValue = "0", MaxValue = "1000", DefaultValue = "200", GroupName = "H0A 保护参数", Unit = "V" },
        new HRegisterEntry { HIndex = "H0A.12", CommAddress = "200A-0Dh", ParameterName = "驱动器过压故障阈值", VariableName = "H0A_x.OverVoltageThreshold", MinValue = "0", MaxValue = "1000", DefaultValue = "400", GroupName = "H0A 保护参数", Unit = "V" },

        // H0B 组：监视参数（全部只读）
        new HRegisterEntry { HIndex = "H0B.00", CommAddress = "200B-01h", ParameterName = "电机实际转速", VariableName = "H0B_x.ActualSpeed", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true, Unit = "rpm" },
        new HRegisterEntry { HIndex = "H0B.01", CommAddress = "200B-02h", ParameterName = "速度指令（输入端）", VariableName = "H0B_x.SpeedCmdInput", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true, Unit = "rpm" },
        new HRegisterEntry { HIndex = "H0B.02", CommAddress = "200B-03h", ParameterName = "内部转矩指令", VariableName = "H0B_x.InternalTorqueCmd", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true, Unit = "%" },
        new HRegisterEntry { HIndex = "H0B.03", CommAddress = "200B-04h", ParameterName = "位置指令脉冲速度", VariableName = "H0B_x.PosCmdPulseSpeed", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true, Unit = "rpm" },
        new HRegisterEntry { HIndex = "H0B.04", CommAddress = "200B-05h", ParameterName = "电机实际位置（圈数）", VariableName = "H0B_x.ActualPosRevs", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H0B.05", CommAddress = "200B-06h", ParameterName = "电机实际位置（单圈脉冲）", VariableName = "H0B_x.ActualPosPulses", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H0B.07", CommAddress = "200B-08h", ParameterName = "电机温度", VariableName = "H0B_x.MotorTemp", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true, Unit = "°C" },
        new HRegisterEntry { HIndex = "H0B.11", CommAddress = "200B-0Ch", ParameterName = "输入位置指令对应速度信息", VariableName = "H0B_x.PulseSpeed", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H0B.13", CommAddress = "200B-0Eh", ParameterName = "直流母线电压", VariableName = "H0B_x.BusVoltageMonitor", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true, Unit = "V" },
        new HRegisterEntry { HIndex = "H0B.15", CommAddress = "200B-10h", ParameterName = "编码器位置偏差计数器", VariableName = "H0B_x.EncodeBias", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H0B.17", CommAddress = "200B-12h", ParameterName = "输入指令脉冲计数器", VariableName = "mcSpeedRamp.PulseShadow", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H0B.18", CommAddress = "200B-13h", ParameterName = "编码器反馈脉冲计数器", VariableName = "H0B_x.EncoderFeedbackPulse", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H0B.24", CommAddress = "200B-19h", ParameterName = "相电流有效值", VariableName = "H0B_x.PhaseIValue", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true, Unit = "A" },
        new HRegisterEntry { HIndex = "H0B.26", CommAddress = "200B-1Bh", ParameterName = "母线电压值", VariableName = "H0B_x.BusVoltage", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true, Unit = "V" },
        new HRegisterEntry { HIndex = "H0B.27", CommAddress = "200B-1Ch", ParameterName = "Mos温度", VariableName = "AdcSampleValue.ADCTmosFlt", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true, Unit = "°C" },
        new HRegisterEntry { HIndex = "H0B.30", CommAddress = "200B-1Fh", ParameterName = "当前故障代码", VariableName = "H0B_x.FaultCode", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H0B.31", CommAddress = "200B-20h", ParameterName = "历史故障1（最近）", VariableName = "H0B_x.FaultHistory1", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H0B.32", CommAddress = "200B-21h", ParameterName = "历史故障2", VariableName = "H0B_x.FaultHistory2", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true },

        // H0C 组：通信参数
        new HRegisterEntry { HIndex = "H0C.01", CommAddress = "200C-01h", ParameterName = "从机地址（轴地址）", VariableName = "Device_ID", MinValue = "1", MaxValue = "0xFF", DefaultValue = "3", GroupName = "H0C 通信参数" },
        new HRegisterEntry { HIndex = "H0C.03", CommAddress = "200C-03h", ParameterName = "串口波特率设置", VariableName = "H0C_x.Baurate", MinValue = "1", MaxValue = "4", DefaultValue = "4", GroupName = "H0C 通信参数" },

        // H0D 组：辅助功能
        new HRegisterEntry { HIndex = "H0D.01", CommAddress = "200D-01h", ParameterName = "软件复位", VariableName = "H0D_x.SoftwareReset", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H0D 辅助功能" },
        new HRegisterEntry { HIndex = "H0D.02", CommAddress = "200D-02h", ParameterName = "故障复位", VariableName = "H0D_x.FalutReset", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H0D 辅助功能" },
        new HRegisterEntry { HIndex = "H0D.03", CommAddress = "200D-03h", ParameterName = "参数存储", VariableName = "H0D_x.ParamSave", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H0D 辅助功能" },
        new HRegisterEntry { HIndex = "H0D.04", CommAddress = "200D-04h", ParameterName = "点动运行使能", VariableName = "H0D_x.JogEnable", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H0D 辅助功能" },
        new HRegisterEntry { HIndex = "H0D.05", CommAddress = "200D-05h", ParameterName = "主动回零触发", VariableName = "H0D_x.HomingTrigger", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H0D 辅助功能" },
        new HRegisterEntry { HIndex = "H0D.06", CommAddress = "200D-06h", ParameterName = "紧急停机", VariableName = "H0D_x.EmergencyStop", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H0D 辅助功能" },
        new HRegisterEntry { HIndex = "H0D.07", CommAddress = "200D-07h", ParameterName = "伺服使能来源", VariableName = "H0D_x.EnableSource", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H0D 辅助功能" },
        new HRegisterEntry { HIndex = "H0D.08", CommAddress = "200D-08h", ParameterName = "软件伺服使能", VariableName = "H0D_x.SoftEnable", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H0D 辅助功能" },
        new HRegisterEntry { HIndex = "H0D.09", CommAddress = "200D-09h", ParameterName = "制动电阻选择", VariableName = "H0D_x.BrakeResistorSelect", MinValue = "0", MaxValue = "2", DefaultValue = "0", GroupName = "H0D 辅助功能" },
        new HRegisterEntry { HIndex = "H0D.10", CommAddress = "200D-0Ah", ParameterName = "制动电阻阻值", VariableName = "H0D_x.BrakeResistorOhm", MinValue = "10", MaxValue = "1000", DefaultValue = "100", GroupName = "H0D 辅助功能", Unit = "Ω" },
        new HRegisterEntry { HIndex = "H0D.11", CommAddress = "200D-0Bh", ParameterName = "制动电阻功率", VariableName = "H0D_x.BrakeResistorPower", MinValue = "10", MaxValue = "10000", DefaultValue = "100", GroupName = "H0D 辅助功能", Unit = "W" },
        new HRegisterEntry { HIndex = "H0D.12", CommAddress = "200D-0Ch", ParameterName = "制动单元动作电压", VariableName = "H0D_x.BrakeUnitVoltage", MinValue = "0", MaxValue = "1000", DefaultValue = "380", GroupName = "H0D 辅助功能", Unit = "V" },
        new HRegisterEntry { HIndex = "H0D.20", CommAddress = "200D-14h", ParameterName = "虚拟示波器通道1信号选择", VariableName = "H0D_x.OscCh1Signal", MinValue = "0", MaxValue = "31", DefaultValue = "0", GroupName = "H0D 辅助功能" },
        new HRegisterEntry { HIndex = "H0D.21", CommAddress = "200D-15h", ParameterName = "虚拟示波器通道2信号选择", VariableName = "H0D_x.OscCh2Signal", MinValue = "0", MaxValue = "31", DefaultValue = "0", GroupName = "H0D 辅助功能" },
        new HRegisterEntry { HIndex = "H0D.22", CommAddress = "200D-16h", ParameterName = "虚拟示波器触发模式", VariableName = "H0D_x.OscTriggerMode", MinValue = "0", MaxValue = "3", DefaultValue = "0", GroupName = "H0D 辅助功能" },
        new HRegisterEntry { HIndex = "H0D.23", CommAddress = "200D-17h", ParameterName = "虚拟示波器采样周期", VariableName = "H0D_x.OscSamplePeriodMs", MinValue = "1", MaxValue = "1000", DefaultValue = "1", GroupName = "H0D 辅助功能", Unit = "ms" },

        // H0E 组：模拟量 I/O 参数（参考汇川 SV680 H0E）
        new HRegisterEntry { HIndex = "H0E.00", CommAddress = "200E-01h", ParameterName = "AI1 功能选择", VariableName = "H0E_x.AI1_Func", MinValue = "0", MaxValue = "7", DefaultValue = "1", GroupName = "H0E 模拟量IO" },
        new HRegisterEntry { HIndex = "H0E.01", CommAddress = "200E-02h", ParameterName = "AI1 满量程对应物理量", VariableName = "H0E_x.AI1_MaxRef", MinValue = "-30000", MaxValue = "30000", DefaultValue = "3000", GroupName = "H0E 模拟量IO", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H0E.02", CommAddress = "200E-03h", ParameterName = "AI1 偏置电压", VariableName = "H0E_x.AI1_Offset_mV", MinValue = "-10000", MaxValue = "10000", DefaultValue = "0", GroupName = "H0E 模拟量IO", Unit = "mV" },
        new HRegisterEntry { HIndex = "H0E.03", CommAddress = "200E-04h", ParameterName = "AI1 死区宽度", VariableName = "H0E_x.AI1_DeadBand_mV", MinValue = "0", MaxValue = "500", DefaultValue = "0", GroupName = "H0E 模拟量IO", Unit = "mV" },
        new HRegisterEntry { HIndex = "H0E.04", CommAddress = "200E-05h", ParameterName = "AI1 低通滤波时间常数", VariableName = "H0E_x.AI1_LpfTimeMs", MinValue = "0", MaxValue = "1000", DefaultValue = "5", GroupName = "H0E 模拟量IO", Unit = "ms" },
        new HRegisterEntry { HIndex = "H0E.05", CommAddress = "200E-06h", ParameterName = "AI1 实际电压值（监视）", VariableName = "H0E_x.AI1_ActualMv", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0E 模拟量IO", IsReadOnly = true, Unit = "mV" },
        new HRegisterEntry { HIndex = "H0E.10", CommAddress = "200E-0Bh", ParameterName = "AI2 功能选择", VariableName = "H0E_x.AI2_Func", MinValue = "0", MaxValue = "7", DefaultValue = "2", GroupName = "H0E 模拟量IO" },
        new HRegisterEntry { HIndex = "H0E.11", CommAddress = "200E-0Ch", ParameterName = "AI2 满量程对应物理量", VariableName = "H0E_x.AI2_MaxRef", MinValue = "-30000", MaxValue = "30000", DefaultValue = "3000", GroupName = "H0E 模拟量IO", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H0E.12", CommAddress = "200E-0Dh", ParameterName = "AI2 偏置电压", VariableName = "H0E_x.AI2_Offset_mV", MinValue = "-10000", MaxValue = "10000", DefaultValue = "0", GroupName = "H0E 模拟量IO", Unit = "mV" },
        new HRegisterEntry { HIndex = "H0E.13", CommAddress = "200E-0Eh", ParameterName = "AI2 死区宽度", VariableName = "H0E_x.AI2_DeadBand_mV", MinValue = "0", MaxValue = "500", DefaultValue = "0", GroupName = "H0E 模拟量IO", Unit = "mV" },
        new HRegisterEntry { HIndex = "H0E.14", CommAddress = "200E-0Fh", ParameterName = "AI2 低通滤波时间常数", VariableName = "H0E_x.AI2_LpfTimeMs", MinValue = "0", MaxValue = "1000", DefaultValue = "5", GroupName = "H0E 模拟量IO", Unit = "ms" },
        new HRegisterEntry { HIndex = "H0E.15", CommAddress = "200E-10h", ParameterName = "AI2 实际电压值（监视）", VariableName = "H0E_x.AI2_ActualMv", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0E 模拟量IO", IsReadOnly = true, Unit = "mV" },
        new HRegisterEntry { HIndex = "H0E.20", CommAddress = "200E-15h", ParameterName = "AO1 功能选择", VariableName = "H0E_x.AO1_Func", MinValue = "0", MaxValue = "7", DefaultValue = "1", GroupName = "H0E 模拟量IO" },
        new HRegisterEntry { HIndex = "H0E.21", CommAddress = "200E-16h", ParameterName = "AO1 满量程对应物理量", VariableName = "H0E_x.AO1_FullScaleRef", MinValue = "0", MaxValue = "30000", DefaultValue = "3000", GroupName = "H0E 模拟量IO", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H0E.22", CommAddress = "200E-17h", ParameterName = "AO1 偏置", VariableName = "H0E_x.AO1_Offset_mV", MinValue = "-10000", MaxValue = "10000", DefaultValue = "0", GroupName = "H0E 模拟量IO", Unit = "mV" },
        new HRegisterEntry { HIndex = "H0E.23", CommAddress = "200E-18h", ParameterName = "AO1 增益", VariableName = "H0E_x.AO1_Gain", MinValue = "500", MaxValue = "2000", DefaultValue = "1000", GroupName = "H0E 模拟量IO" },
        new HRegisterEntry { HIndex = "H0E.30", CommAddress = "200E-1Fh", ParameterName = "AO2 功能选择", VariableName = "H0E_x.AO2_Func", MinValue = "0", MaxValue = "7", DefaultValue = "2", GroupName = "H0E 模拟量IO" },
        new HRegisterEntry { HIndex = "H0E.31", CommAddress = "200E-20h", ParameterName = "AO2 满量程对应物理量", VariableName = "H0E_x.AO2_FullScaleRef", MinValue = "0", MaxValue = "30000", DefaultValue = "3000", GroupName = "H0E 模拟量IO", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H0E.32", CommAddress = "200E-21h", ParameterName = "AO2 偏置", VariableName = "H0E_x.AO2_Offset_mV", MinValue = "-10000", MaxValue = "10000", DefaultValue = "0", GroupName = "H0E 模拟量IO", Unit = "mV" },
        new HRegisterEntry { HIndex = "H0E.33", CommAddress = "200E-22h", ParameterName = "AO2 增益", VariableName = "H0E_x.AO2_Gain", MinValue = "500", MaxValue = "2000", DefaultValue = "1000", GroupName = "H0E 模拟量IO" },

        // H0F 组：安全功能与软件限位（参考汇川 SV680 H0F）
        new HRegisterEntry { HIndex = "H0F.00", CommAddress = "200F-01h", ParameterName = "软件正限位使能", VariableName = "H0F_x.SoftPosLimitEn", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H0F 安全与限位" },
        new HRegisterEntry { HIndex = "H0F.01", CommAddress = "200F-02h", ParameterName = "软件正限位（圈数高字）", VariableName = "H0F_x.SoftPosLimitHigh", MinValue = "-32768", MaxValue = "32767", DefaultValue = "1000", GroupName = "H0F 安全与限位" },
        new HRegisterEntry { HIndex = "H0F.02", CommAddress = "200F-03h", ParameterName = "软件正限位（单圈脉冲低字）", VariableName = "H0F_x.SoftPosLimitLow", MinValue = "0", MaxValue = "65535", DefaultValue = "0", GroupName = "H0F 安全与限位" },
        new HRegisterEntry { HIndex = "H0F.03", CommAddress = "200F-04h", ParameterName = "软件负限位使能", VariableName = "H0F_x.SoftNegLimitEn", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H0F 安全与限位" },
        new HRegisterEntry { HIndex = "H0F.04", CommAddress = "200F-05h", ParameterName = "软件负限位（圈数高字）", VariableName = "H0F_x.SoftNegLimitHigh", MinValue = "-32768", MaxValue = "32767", DefaultValue = "-1000", GroupName = "H0F 安全与限位" },
        new HRegisterEntry { HIndex = "H0F.05", CommAddress = "200F-06h", ParameterName = "软件负限位（单圈脉冲低字）", VariableName = "H0F_x.SoftNegLimitLow", MinValue = "0", MaxValue = "65535", DefaultValue = "0", GroupName = "H0F 安全与限位" },
        new HRegisterEntry { HIndex = "H0F.06", CommAddress = "200F-07h", ParameterName = "超限位处理方式", VariableName = "H0F_x.LimitOverAction", MinValue = "0", MaxValue = "2", DefaultValue = "0", GroupName = "H0F 安全与限位" },
        new HRegisterEntry { HIndex = "H0F.10", CommAddress = "200F-0Bh", ParameterName = "STO 功能使能", VariableName = "H0F_x.STO_Enable", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H0F 安全与限位" },
        new HRegisterEntry { HIndex = "H0F.11", CommAddress = "200F-0Ch", ParameterName = "STO 故障响应方式", VariableName = "H0F_x.STO_FaultAction", MinValue = "0", MaxValue = "2", DefaultValue = "0", GroupName = "H0F 安全与限位" },
        new HRegisterEntry { HIndex = "H0F.12", CommAddress = "200F-0Dh", ParameterName = "SS1 停机延迟时间", VariableName = "H0F_x.SS1_DelayMs", MinValue = "0", MaxValue = "65535", DefaultValue = "1000", GroupName = "H0F 安全与限位", Unit = "ms" },
        new HRegisterEntry { HIndex = "H0F.13", CommAddress = "200F-0Eh", ParameterName = "安全功能状态字（监视）", VariableName = "H0F_x.SafetyStatus", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0F 安全与限位", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H0F.14", CommAddress = "200F-0Fh", ParameterName = "STO_A 通道输入状态（监视）", VariableName = "H0F_x.STO_A_State", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0F 安全与限位", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H0F.15", CommAddress = "200F-10h", ParameterName = "STO_B 通道输入状态（监视）", VariableName = "H0F_x.STO_B_State", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0F 安全与限位", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H0F.16", CommAddress = "200F-11h", ParameterName = "双通道不一致故障延时", VariableName = "H0F_x.STO_DiscrepancyMs", MinValue = "0", MaxValue = "65535", DefaultValue = "100", GroupName = "H0F 安全与限位", Unit = "ms" },
        new HRegisterEntry { HIndex = "H0F.17", CommAddress = "200F-12h", ParameterName = "SS1 制动模式", VariableName = "H0F_x.SS1_BrakeMode", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H0F 安全与限位" },
        new HRegisterEntry { HIndex = "H0F.18", CommAddress = "200F-13h", ParameterName = "SS1 减速斜率", VariableName = "H0F_x.SS1_DecelRate", MinValue = "0", MaxValue = "65535", DefaultValue = "1000", GroupName = "H0F 安全与限位", Unit = "rpm/s" },
        new HRegisterEntry { HIndex = "H0F.19", CommAddress = "200F-14h", ParameterName = "STO 复位方式", VariableName = "H0F_x.STO_ResetMode", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H0F 安全与限位" },
        new HRegisterEntry { HIndex = "H0F.20", CommAddress = "200F-15h", ParameterName = "正向速度限制值", VariableName = "H0F_x.PosSpeedLimit", MinValue = "0", MaxValue = "10000", DefaultValue = "3000", GroupName = "H0F 安全与限位", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H0F.21", CommAddress = "200F-16h", ParameterName = "负向速度限制值", VariableName = "H0F_x.NegSpeedLimit", MinValue = "0", MaxValue = "10000", DefaultValue = "3000", GroupName = "H0F 安全与限位", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H0F.22", CommAddress = "200F-17h", ParameterName = "速度限制功能使能", VariableName = "H0F_x.SpeedLimitEn", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H0F 安全与限位" },

        // H10 组：内部多段速 / 多段位置（参考汇川 SV680 H10）
        new HRegisterEntry { HIndex = "H10.00", CommAddress = "2010-01h", ParameterName = "多段速/位置触发模式", VariableName = "H10_x.MultiStepMode", MinValue = "0", MaxValue = "3", DefaultValue = "0", GroupName = "H10 多段速位置" },
        new HRegisterEntry { HIndex = "H10.01", CommAddress = "2010-02h", ParameterName = "多段速第1段速度", VariableName = "H10_x.Speed1", MinValue = "-6000", MaxValue = "6000", DefaultValue = "100", GroupName = "H10 多段速位置", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H10.02", CommAddress = "2010-03h", ParameterName = "多段速第2段速度", VariableName = "H10_x.Speed2", MinValue = "-6000", MaxValue = "6000", DefaultValue = "200", GroupName = "H10 多段速位置", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H10.03", CommAddress = "2010-04h", ParameterName = "多段速第3段速度", VariableName = "H10_x.Speed3", MinValue = "-6000", MaxValue = "6000", DefaultValue = "300", GroupName = "H10 多段速位置", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H10.04", CommAddress = "2010-05h", ParameterName = "多段速第4段速度", VariableName = "H10_x.Speed4", MinValue = "-6000", MaxValue = "6000", DefaultValue = "400", GroupName = "H10 多段速位置", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H10.05", CommAddress = "2010-06h", ParameterName = "多段速第5段速度", VariableName = "H10_x.Speed5", MinValue = "-6000", MaxValue = "6000", DefaultValue = "500", GroupName = "H10 多段速位置", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H10.06", CommAddress = "2010-07h", ParameterName = "多段速第6段速度", VariableName = "H10_x.Speed6", MinValue = "-6000", MaxValue = "6000", DefaultValue = "600", GroupName = "H10 多段速位置", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H10.07", CommAddress = "2010-08h", ParameterName = "多段速第7段速度", VariableName = "H10_x.Speed7", MinValue = "-6000", MaxValue = "6000", DefaultValue = "700", GroupName = "H10 多段速位置", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H10.08", CommAddress = "2010-09h", ParameterName = "多段速第8段速度", VariableName = "H10_x.Speed8", MinValue = "-6000", MaxValue = "6000", DefaultValue = "800", GroupName = "H10 多段速位置", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H10.10", CommAddress = "2010-0Bh", ParameterName = "多段速第1段加速时间", VariableName = "H10_x.AccTime1", MinValue = "0", MaxValue = "65535", DefaultValue = "100", GroupName = "H10 多段速位置", Unit = "ms" },
        new HRegisterEntry { HIndex = "H10.11", CommAddress = "2010-0Ch", ParameterName = "多段速第1段减速时间", VariableName = "H10_x.DecTime1", MinValue = "0", MaxValue = "65535", DefaultValue = "100", GroupName = "H10 多段速位置", Unit = "ms" },
        new HRegisterEntry { HIndex = "H10.12", CommAddress = "2010-0Dh", ParameterName = "多段速第2段加速时间", VariableName = "H10_x.AccTime2", MinValue = "0", MaxValue = "65535", DefaultValue = "100", GroupName = "H10 多段速位置", Unit = "ms" },
        new HRegisterEntry { HIndex = "H10.13", CommAddress = "2010-0Eh", ParameterName = "多段速第2段减速时间", VariableName = "H10_x.DecTime2", MinValue = "0", MaxValue = "65535", DefaultValue = "100", GroupName = "H10 多段速位置", Unit = "ms" },
        new HRegisterEntry { HIndex = "H10.14", CommAddress = "2010-0Fh", ParameterName = "多段速第3段加速时间", VariableName = "H10_x.AccTime3", MinValue = "0", MaxValue = "65535", DefaultValue = "100", GroupName = "H10 多段速位置", Unit = "ms" },
        new HRegisterEntry { HIndex = "H10.15", CommAddress = "2010-10h", ParameterName = "多段速第3段减速时间", VariableName = "H10_x.DecTime3", MinValue = "0", MaxValue = "65535", DefaultValue = "100", GroupName = "H10 多段速位置", Unit = "ms" },
        new HRegisterEntry { HIndex = "H10.16", CommAddress = "2010-11h", ParameterName = "多段速第4段加速时间", VariableName = "H10_x.AccTime4", MinValue = "0", MaxValue = "65535", DefaultValue = "100", GroupName = "H10 多段速位置", Unit = "ms" },
        new HRegisterEntry { HIndex = "H10.17", CommAddress = "2010-12h", ParameterName = "多段速第4段减速时间", VariableName = "H10_x.DecTime4", MinValue = "0", MaxValue = "65535", DefaultValue = "100", GroupName = "H10 多段速位置", Unit = "ms" },
        new HRegisterEntry { HIndex = "H10.20", CommAddress = "2010-15h", ParameterName = "多段位置第1段位移（圈数）", VariableName = "H10_x.Pos1_Revs", MinValue = "-32768", MaxValue = "32767", DefaultValue = "0", GroupName = "H10 多段速位置" },
        new HRegisterEntry { HIndex = "H10.21", CommAddress = "2010-16h", ParameterName = "多段位置第1段位移（脉冲）", VariableName = "H10_x.Pos1_Pulses", MinValue = "0", MaxValue = "65535", DefaultValue = "0", GroupName = "H10 多段速位置" },
        new HRegisterEntry { HIndex = "H10.22", CommAddress = "2010-17h", ParameterName = "多段位置第2段位移（圈数）", VariableName = "H10_x.Pos2_Revs", MinValue = "-32768", MaxValue = "32767", DefaultValue = "0", GroupName = "H10 多段速位置" },
        new HRegisterEntry { HIndex = "H10.23", CommAddress = "2010-18h", ParameterName = "多段位置第2段位移（脉冲）", VariableName = "H10_x.Pos2_Pulses", MinValue = "0", MaxValue = "65535", DefaultValue = "0", GroupName = "H10 多段速位置" },
        new HRegisterEntry { HIndex = "H10.24", CommAddress = "2010-19h", ParameterName = "多段位置第3段位移（圈数）", VariableName = "H10_x.Pos3_Revs", MinValue = "-32768", MaxValue = "32767", DefaultValue = "0", GroupName = "H10 多段速位置" },
        new HRegisterEntry { HIndex = "H10.25", CommAddress = "2010-1Ah", ParameterName = "多段位置第3段位移（脉冲）", VariableName = "H10_x.Pos3_Pulses", MinValue = "0", MaxValue = "65535", DefaultValue = "0", GroupName = "H10 多段速位置" },
        new HRegisterEntry { HIndex = "H10.26", CommAddress = "2010-1Bh", ParameterName = "多段位置第4段位移（圈数）", VariableName = "H10_x.Pos4_Revs", MinValue = "-32768", MaxValue = "32767", DefaultValue = "0", GroupName = "H10 多段速位置" },
        new HRegisterEntry { HIndex = "H10.27", CommAddress = "2010-1Ch", ParameterName = "多段位置第4段位移（脉冲）", VariableName = "H10_x.Pos4_Pulses", MinValue = "0", MaxValue = "65535", DefaultValue = "0", GroupName = "H10 多段速位置" },

        // H11 组：故障诊断与运行记录（参考汇川 SV680 H11）
        new HRegisterEntry { HIndex = "H11.00", CommAddress = "2011-01h", ParameterName = "上电次数", VariableName = "H11_x.PowerOnCount", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H11 故障诊断", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H11.01", CommAddress = "2011-02h", ParameterName = "累计运行时间", VariableName = "H11_x.TotalRunTimeH", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H11 故障诊断", IsReadOnly = true, Unit = "h" },
        new HRegisterEntry { HIndex = "H11.02", CommAddress = "2011-03h", ParameterName = "最近故障发生时转速", VariableName = "H11_x.FaultSpeed", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H11 故障诊断", IsReadOnly = true, Unit = "rpm" },
        new HRegisterEntry { HIndex = "H11.03", CommAddress = "2011-04h", ParameterName = "最近故障发生时电流", VariableName = "H11_x.FaultCurrent", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H11 故障诊断", IsReadOnly = true, Unit = "A" },
        new HRegisterEntry { HIndex = "H11.04", CommAddress = "2011-05h", ParameterName = "最近故障发生时母线电压", VariableName = "H11_x.FaultBusVoltage", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H11 故障诊断", IsReadOnly = true, Unit = "V" },
        new HRegisterEntry { HIndex = "H11.05", CommAddress = "2011-06h", ParameterName = "最近故障发生时温度", VariableName = "H11_x.FaultTemp", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H11 故障诊断", IsReadOnly = true, Unit = "°C" },
        new HRegisterEntry { HIndex = "H11.06", CommAddress = "2011-07h", ParameterName = "最近故障发生时控制模式", VariableName = "H11_x.FaultCtrlMode", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H11 故障诊断", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H11.10", CommAddress = "2011-0Bh", ParameterName = "历史故障3", VariableName = "H11_x.FaultHistory3", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H11 故障诊断", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H11.11", CommAddress = "2011-0Ch", ParameterName = "历史故障4", VariableName = "H11_x.FaultHistory4", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H11 故障诊断", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H11.12", CommAddress = "2011-0Dh", ParameterName = "历史故障5", VariableName = "H11_x.FaultHistory5", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H11 故障诊断", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H11.20", CommAddress = "2011-15h", ParameterName = "故障记录全部清除", VariableName = "H11_x.FaultClearAll", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H11 故障诊断" },
    };

    /// <summary>
    /// 根据 H分组及索引查找寄存器
    /// </summary>
    public static HRegisterEntry? FindByHIndex(string hIndex)
    {
        return RegisterTable.FirstOrDefault(r => r.HIndex == hIndex || r.HIndex.Replace(".", "_") == hIndex);
    }

    /// <summary>
    /// 根据 通信地址查找寄存器
    /// </summary>
    public static HRegisterEntry? FindByCommAddress(string commAddress)
    {
        return RegisterTable.FirstOrDefault(r => string.Equals(r.CommAddress, commAddress, StringComparison.OrdinalIgnoreCase));
    }
}