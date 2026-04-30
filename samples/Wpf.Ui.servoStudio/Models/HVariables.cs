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
        new HRegisterEntry { HIndex = "H00.08", CommAddress = "2000-08h", ParameterName = "电机编码器调零状态", VariableName = "H00_x.EncodeState", MinValue = "-", MaxValue = "-", DefaultValue = "1", GroupName = "H00 电机参数" },
        new HRegisterEntry { HIndex = "H00.15", CommAddress = "2000-10h", ParameterName = "电机最大转速", VariableName = "H00_x.MotorMaxSpeed", MinValue = "-", MaxValue = "-", DefaultValue = "1", GroupName = "H00 电机参数", Unit = "rpm" },
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
        new HRegisterEntry { HIndex = "H05.07", CommAddress = "2005-08h", ParameterName = "尺比分子", VariableName = "RA.GearRatios", MinValue = "1", MaxValue = "0xFFFF", DefaultValue = "400", GroupName = "H05 位置控制" },
        new HRegisterEntry { HIndex = "H05.15", CommAddress = "2005-10h", ParameterName = "脉冲指令模式", VariableName = "H05_x.PulseMode", MinValue = "1", MaxValue = "3", DefaultValue = "1", GroupName = "H05 位置控制" },
        new HRegisterEntry { HIndex = "H05.16", CommAddress = "2005-11h", ParameterName = "相对位置圈数增量", VariableName = "mcFocCtrl.TargetAngle_msbX", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "0", GroupName = "H05 位置控制" },
        new HRegisterEntry { HIndex = "H05.17", CommAddress = "2005-12h", ParameterName = "相对位置单圈增量", VariableName = "mcFocCtrl.TargetAngle_lsbX", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "0", GroupName = "H05 位置控制" },
        new HRegisterEntry { HIndex = "H05.21", CommAddress = "2005-16h", ParameterName = "定位完成阈值", VariableName = "H05_x.PositionThreshold", MinValue = "5", MaxValue = "0xFF", DefaultValue = "30", GroupName = "H05 位置控制" },
        new HRegisterEntry { HIndex = "H05.36", CommAddress = "2005-25h", ParameterName = "机械原点偏移量", VariableName = "MR.HallAngleOffset", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H05 位置控制" },

        // H06 组：速度控制参数
        new HRegisterEntry { HIndex = "H06.03", CommAddress = "2006-04h", ParameterName = "速度值设定", VariableName = "RA.LimitSpeedWrite", MinValue = "0", MaxValue = "1500", DefaultValue = "0", GroupName = "H06 速度控制", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H06.18", CommAddress = "2006-13h", ParameterName = "速度达到信号阈值", VariableName = "H06_x.SpeedThreshold", MinValue = "5", MaxValue = "0xFF", DefaultValue = "10", GroupName = "H06 速度控制", Unit = "rpm" },

        // H07 组：转矩控制参数
        new HRegisterEntry { HIndex = "H07.03", CommAddress = "2007-04h", ParameterName = "转矩设置（电流给定）", VariableName = "RA.TCurrentWrite", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "0", GroupName = "H07 转矩控制", Unit = "A" },
        new HRegisterEntry { HIndex = "H07.09", CommAddress = "2007-0Ah", ParameterName = "正内部转矩限制", VariableName = "H07_x.SoutMax_Cur", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "18", GroupName = "H07 转矩控制", Unit = "A" },
        new HRegisterEntry { HIndex = "H07.10", CommAddress = "2007-0Bh", ParameterName = "负内部转矩限制", VariableName = "H07_x.SoutMin_Cur", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "19", GroupName = "H07 转矩控制", Unit = "A" },

        // H08 组：控制环增益
        new HRegisterEntry { HIndex = "H08.00", CommAddress = "2008-01h", ParameterName = "速度环增益", VariableName = "RA.SpeedkpWrite", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "7371", GroupName = "H08 控制环增益" },
        new HRegisterEntry { HIndex = "H08.01", CommAddress = "2008-02h", ParameterName = "速度环积分时间常数", VariableName = "RA.SpeedkiWrite", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "169", GroupName = "H08 控制环增益" },
        new HRegisterEntry { HIndex = "H08.02", CommAddress = "2008-03h", ParameterName = "位置环增益", VariableName = "RA.FlashPKP_Sw", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "1749", GroupName = "H08 控制环增益" },
        new HRegisterEntry { HIndex = "H08.03", CommAddress = "2008-04h", ParameterName = "位置环微分", VariableName = "RA.FlashPKD_Sw", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "1900", GroupName = "H08 控制环增益" },
        new HRegisterEntry { HIndex = "H08.04", CommAddress = "2008-05h", ParameterName = "速度环增量", VariableName = "RA.ACCSpeedWrite", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "500", GroupName = "H08 控制环增益" },
        new HRegisterEntry { HIndex = "H08.05", CommAddress = "2008-06h", ParameterName = "速度环减量", VariableName = "RA.DecValueWrite", MinValue = "0", MaxValue = "0xFFFF", DefaultValue = "500", GroupName = "H08 控制环增益" },
        new HRegisterEntry { HIndex = "H08.06", CommAddress = "2008-07h", ParameterName = "位置环速度正向输出限幅", VariableName = "H08_x.PositionSoutMax", MinValue = "0", MaxValue = "1500", DefaultValue = "1200", GroupName = "H08 控制环增益", Unit = "rpm" },
        new HRegisterEntry { HIndex = "H08.07", CommAddress = "2008-08h", ParameterName = "位置环速度反向输出限幅", VariableName = "H08_x.PositionSoutMin", MinValue = "0", MaxValue = "1500", DefaultValue = "1200", GroupName = "H08 控制环增益", Unit = "rpm" },

        // H0B 组：监视参数
        new HRegisterEntry { HIndex = "H0B.00", CommAddress = "200B-01h", ParameterName = "电机实际转速", VariableName = "H0B_x.ActualSpeed", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true, Unit = "rpm" },
        new HRegisterEntry { HIndex = "H0B.11", CommAddress = "200B-0Ch", ParameterName = "输入位置指令对应速度信息", VariableName = "H0B_x.PulseSpeed", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H0B.15", CommAddress = "200B-10h", ParameterName = "编码器位置偏差计数器", VariableName = "H0B_x.EncodeBias", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H0B.17", CommAddress = "200B-12h", ParameterName = "输入指令脉冲计数器", VariableName = "mcSpeedRamp.PulseShadow", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true },
        new HRegisterEntry { HIndex = "H0B.24", CommAddress = "200B-19h", ParameterName = "相电流有效值", VariableName = "H0B_x.PhaseIValue", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true, Unit = "A" },
        new HRegisterEntry { HIndex = "H0B.26", CommAddress = "200B-1Bh", ParameterName = "母线电压值", VariableName = "H0B_x.BusVoltage", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true, Unit = "V" },
        new HRegisterEntry { HIndex = "H0B.27", CommAddress = "200B-1Ch", ParameterName = "Mos温度", VariableName = "AdcSampleValue.ADCTmosFlt", MinValue = "-", MaxValue = "-", DefaultValue = "-", GroupName = "H0B 监视参数", IsReadOnly = true, Unit = "°C" },

        // H0C 组：通信参数
        new HRegisterEntry { HIndex = "H0C.01", CommAddress = "200C-01h", ParameterName = "从机地址（轴地址）", VariableName = "Device_ID", MinValue = "1", MaxValue = "0xFF", DefaultValue = "3", GroupName = "H0C 通信参数" },
        new HRegisterEntry { HIndex = "H0C.03", CommAddress = "200C-03h", ParameterName = "串口波特率设置", VariableName = "H0C_x.Baurate", MinValue = "1", MaxValue = "4", DefaultValue = "4", GroupName = "H0C 通信参数" },

        // H0D 组：辅助功能
        new HRegisterEntry { HIndex = "H0D.01", CommAddress = "200D-01h", ParameterName = "软件复位", VariableName = "H0D_x.SoftwareReset", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H0D 辅助功能" },
        new HRegisterEntry { HIndex = "H0D.02", CommAddress = "200D-02h", ParameterName = "故障复位", VariableName = "H0D_x.FalutReset", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H0D 辅助功能" },
        new HRegisterEntry { HIndex = "H0D.06", CommAddress = "200D-06h", ParameterName = "紧急停机", VariableName = "H0D_x.EmergencyStop", MinValue = "0", MaxValue = "1", DefaultValue = "0", GroupName = "H0D 辅助功能" },
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