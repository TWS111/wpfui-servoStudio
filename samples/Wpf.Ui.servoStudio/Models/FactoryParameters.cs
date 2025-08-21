// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.ComponentModel;

namespace Wpf.Ui.servoStudio.Models;


public class FactoryParameters
{
    public string? Index { get; set; }

    public string? IndexHex { get; set; }

    public string? Subscribe { get; set; }

    public string? ParameterName { get; set; }

    public DataType TypeUnit { get; set; }   

    public int MinValue { get; set; }

    public int MaxValue { get; set; }

    public int ActualValue { get; set; }

    public int? DefaultValue { get; set; }

  
}

public class FactoryParametersFillInfo
{
    public int[] groupInContains = {
        4,  // H00
        15, // H01
        5,  // H02
        7,  // H05
        6,  // H06
        6,  // H07
        8,  // H08
        2,  // H0A
        7,  // H0B
        3,  // H0C
        4,  // H0D
    };

    public string[] index =
    {
        "H00_00",
        "H00_08",
        "H00_15",
        "H00_43",

        "H01_00",
        "H01_02",
        "H01_03",
        "H01_04",
        "H01_05",
        "H01_08",
        "H01_09",
        "H01_10",
        "H01_11",
        "H01_12",
        "H01_13",
        "H01_14",
        "H01_15",
        "H01_16",
        "H01_17",

        "H02_00",
        "H02_02",
        "H02_05",
        "H02_30",
        "H02_31",

        "H05_00",
        "H05_07",
        "H05_15",
        "H05_16",
        "H05_17",
        "H05_21",
        "H05_36",

        "H06_02",
        "H06_03",
        "H06_04",
        "H06_05",
        "H06_06",
        "H06_18",

        "H07_03",
        "H07_09",
        "H07_10",
        "H07_19",
        "H07_20",
        "H07_21",

        "H08_00",
        "H08_01",
        "H08_02",
        "H08_03",
        "H08_04",
        "H08_05",
        "H08_06",
        "H08_07",

        "H0A_04",
        "H0A_10",

        "H0B_00",
        "H0B_11",
        "H0B_15",
        "H0B_17",
        "H0B_24",
        "H0B_26",
        "H0B_27",

        "H0C_00",
        "H0C_02",
        "H0C_13",

        "H0D_00",
        "H0D_01",
        "H0D_02",
        "H0D_05",
    };

    public string[] indexHex =
    {
        "2000-01h",
        "H00_08",
        "2000-10h",
        "2000-2Bh",

        "2001-01h",
        "2001-03h",
        "2001-04h",
        "2001-05h",
        "2001-06h",
        "2001-09h",
        "2001-0Ah",
        "2001-0Bh",
        "2001-0Ch",
        "2001-0Dh",
        "2001-0Eh",
        "2001-0Fh",
        "2001-10h",
        "2001-11h",
        "2001-12h",

        "2002-01h",
        "2002-03h",
        "2002-06h",
        "2002-1Fh",
        "2002-20h",

        "H05_00",
        "H05_07",
        "H05_15",
        "H05_16",
        "H05_17",
        "H05_21",
        "H05_36",

        "H06_02",
        "2006-04h",
        "2006-05h",
        "2006-06h",
        "2006-07h",
        "2006-13h",

        "2007-04h",
        "2007-0Ah",
        "2007-0Bh",
        "2007-14h",
        "2007-15h",
        "2007-16h",

        "H08_00",
        "H08_01",
        "H08_02",
        "H08_03",
        "H08_04",
        "H08_05",
        "H08_06",
        "H08_07",

        "200A-05h",
        "200A-0Bh",

        "200B-01h",
        "200B-0Ch",
        "200B-10h",
        "200B-12h",
        "200B-19h",
        "200B-1Bh",
        "200B-1Ch",

        "H0C_00",
        "H0C_02",
        "H0C_13",

        "200D-01h",
        "200D-02h",
        "200D-05h",
        "200D-06h",
    };

    public string[] subscribe =
    {
        "电机编号",
        "电机编码器调零状态",
        "电机最大转速",
        "电机最大电流",

        "MCU 软件版本号",
        "驱动器编号",
        "驱动器最大输出电流",
        "驱动器额定输出电流",
        "驱动器电流采样电阻",
        "驱动器温度报警阈值",
        "过压保护值",
        "保护恢复值",
        "欠压保护",
        "欠压恢复",
        "硬件DAC过流值",
        "软件过流值",
        "堵转保护最小转速",
        "Mos温度保护值",
        "Mos温度恢复值",

        "控制模式选择",
        "旋转方向选择",
        "使能OFF停机方式选择",
        "用户密码",
        "系统参数初始化",

        "位置指令来源",
        "尺比分子",
        "脉冲指令模式",
        "相对位置圈数增量",
        "相对位置单圈增量",
        "定位完成阈值",
        "机械原点偏移量",

        "速度指令选择",
        "速度值设定",
        "JOG 点动速度设置值",
        "速度指令加速斜坡时间常数",
        "速度指令减速斜坡时间常数",
        "速度达到信号阈值",

        "转矩设置（电流给定）",
        "正内部转矩限制",
        "负内部转矩限制",
        "转矩控制正向速度限制值",
        "转矩控制时负向速度限制值",
        "转矩到达基准值",

        "速度环增益",
        "速度环积分时间常数",
        "位置环增益",
        "位置环微分",
        "速度环增量",
        "速度环减量",
        "位置环速度正向输出限幅",
        "位置环速度反向输出限幅",

        "电机过载保护增益",
        "位置偏差过大故障阈值",

        "电机实际转速",
        "输入位置指令对应速度信息",
        "编码器位置偏差计数器",
        "输入指令脉冲计数器",
        "相电流有效值",
        "母线电压值",
        "Mos温度",

        "从机地址（轴地址）",
        "串口波特率设置",
        "写入是否更新EEPROM",

        "软件复位",
        "故障复位",
        "重置校准指令",
        "紧急停机",
    };
}