// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Microsoft.Extensions.Primitives;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Wpf.Ui.servoStudio.Models;

public class FactoryParameters : ObservableObject, INotifyPropertyChanged
{
    public string? _index;
    public string? Index
    {
        get => _index;
        set
        {
            if (_index == null)
            {
                _index = value;
            }
        }
    }

    public string? _indexHex;
    public string? IndexHex
    {
        get => _indexHex;
        set
        {
            if (_indexHex == null)
            {
                _indexHex = value;
            }
        }
    }

    public string? _subscribe;
    public string? Subscribe
    {
        get => _subscribe;
        set
        {
            if (_subscribe == null)
            {
                _subscribe = value;
            }
        }
    }

    public string? _parameterName;
    public string? ParameterName
    {
        get => _parameterName;
        set
        {
            if (_parameterName == null)
            {
                _parameterName = value;
            }
        }
    }

    public DataType TypeUnit { get; set; }

    public int _actualValue;
    public int ActualValue
    {
        get => _actualValue;
        set
        {
            if(_minValue != "-")
            {
                if(value > int.Parse(_maxValue) || value < int.Parse(_minValue))
                {
                    return;
                }
            }
            SetProperty(ref _actualValue, value);
            SetField(ref _actualValue, value);
            OnPropertyChanged(nameof(ActualValueChange));
        }
    }

    public string? _minValue;
    public string? MinValue
    {
        get => _minValue;
        set
        {
            if (_minValue == null)
            {
                _minValue = value;
            }
        }
    }

    public string? _maxValue;
    public string? MaxValue
    {
        get => _maxValue;
        set
        {
            if (_maxValue == null)
            {
                _maxValue = value;
            }
        }
    }

    public string? _defaultValue;
    public string? DefaultValue
    {
        get => _defaultValue;
        set
        {
            if (_defaultValue == null)
            {
                _defaultValue = value;
            }
        }
    }

    private int ActualValueChange
    {
        get; set;
    }        
    
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        OnPropertyChanged(propertyName);
        return true;
    }
}

public class FactoryMessage
{
    public FactoryMessage(string? message)
    {
        Message = message;
    }

    public string? Message { get; }
}

public class DateChangedEventArgs : EventArgs

{
    public DateTime OldValue { get; set; }

    public DateTime NewValue { get; set; }
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

    public const int parametersFillinLength = 51;

    public int[] typeUnitIndex =
    {
        3,
        3,
        3,
        3,

        3,
        3,
        3,
        3,
        3,
        4,
        4,
        4,
        3,
        3,
        4,

        3,
        3,
        3,
        5,
        3,

        3,
        3,
        6,
        4,
        3,
        3,

        4,
        4,

        4,
        4,
        4,

        4,
        4,
        3,
        3,
        3,
        3,
        3,
        3,

        4,
        3,
        3,
        6,
        3,
        3,
        3,

        3,
        3,

        3,
        3,
        3,
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
        "H01_11",
        "H01_13",
        "H01_14",
        "H01_15",

        "H02_00",
        "H02_02",
        "H02_05",
        "H02_30",
        "H02_31",

        "H05_07",
        "H05_15",
        "H05_16",
        "H05_17",
        "H05_21",
        "H05_36",

        "H06_03",
        "H06_18",

        "H07_03",
        "H07_09",
        "H07_10",

        "H08_00",
        "H08_01",
        "H08_02",
        "H08_03",
        "H08_04",
        "H08_05",
        "H08_06",
        "H08_07",

        "H0B_00",
        "H0B_11",
        "H0B_15",
        "H0B_17",
        "H0B_24",
        "H0B_26",
        "H0B_27",

        "H0C_01",
        "H0C_03",

        "H0D_01",
        "H0D_02",
        "H0D_06",
    };

    public string[] indexHex =
    {
        "2000-01h",
        "2000-08h",
        "2000-10h",
        "2000-2Bh",

        "2001-01h",
        "2001-03h",
        "2001-04h",
        "2001-05h",
        "2001-06h",
        "2001-09h",
        "2001-0Ah",        
        "2001-0Ch",
        "2001-0Dh",
        "2001-0Eh",
        "2001-0Fh",
        "2001-10h",

        "2002-01h",
        "2002-03h",
        "2002-06h",
        "2002-1Fh",
        "2002-20h",

        "2005-08h",
        "2005-10h",
        "2005-11h",
        "2005-12h",
        "2005-16h",
        "2005-25h",

        "2006-04h",
        "2006-13h",

        "2007-04h",
        "2007-0Ah",
        "2007-0Bh",

        "2008-01h",
        "2008-02h",
        "2008-03h",
        "2008-04h",
        "2008-05h",
        "2008-06h",
        "2008-07h",
        "2008-08h",

        "200B-01h",
        "200B-0Ch",
        "200B-10h",
        "200B-12h",
        "200B-19h",
        "200B-1Bh",
        "200B-1Ch",

        "200C-01h",
        "200C-03h",

        "200D-01h",
        "200D-02h",
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
        "欠压保护值",
        "硬件DAC过流值",
        "软件过流值",
        "堵转保护最小转速",

        "控制模式选择",
        "旋转方向选择",
        "使能OFF停机方式选择",
        "用户密码",
        "系统参数初始化",
                
        "尺比分子",
        "脉冲指令模式",
        "相对位置圈数增量",
        "相对位置单圈增量",
        "定位完成阈值",
        "机械原点偏移量",

        "速度值设定",
        "速度达到信号阈值",

        "转矩设置（电流给定）",
        "正内部转矩限制",
        "负内部转矩限制",

        "速度环增益",
        "速度环积分时间常数",
        "位置环增益",
        "位置环微分",
        "速度环增量",
        "速度环减量",
        "位置环速度正向输出限幅",
        "位置环速度反向输出限幅",

        "电机实际转速",
        "输入位置指令对应速度信息",
        "编码器位置偏差计数器",
        "输入指令脉冲计数器",
        "相电流有效值",
        "母线电压值",
        "Mos温度",

        "从机地址（轴地址）",
        "串口波特率设置",

        "软件复位",
        "故障复位",
        "紧急停机",
    };

    public string[] varibleName =
    {
        "H00_x.MotorNum",
        "H00_x.EncodeState",
        "H00_x.MotorMaxSpeed",
        "H00_x.MotorMaxIvalue",

        "H01_x.SoftwareVersion",
        "H01_x.ControlNum",
        "H01_x.ControllerMaxIVuale",
        "H01_x.ControllerTypeIVuale",
        "H01_x.AdcSampleRValue",
        "RA.TempWrite",
        "RA.OverVoltage",
        "RA.UnderVoltage",
        "OverHardcurrentValue",
        "RA.CurrentProtectWrite",
        "H01_x.STALMinSpeed",

        "CtrlMode",
        "mcFocCtrl.PosiResponeFlag",
        "H02_x.OffMode",
        "H02_x.UserKey",
        "RA.ResetFlag",

        "RA.GearRatios",
        "H05_x.PulseMode",
        "mcFocCtrl.TargetAngle_msbX",
        "mcFocCtrl.TargetAngle_lsbX",
        "H05_x.PositionThreshold",
        "MR.HallAngleOffset",

        "RA.LimitSpeedWrite",
        "H06_x.SpeedThreshold",

        "RA.TCurrentWrite",
        "H07_x.SoutMax_Cur",
        "H07_x.SoutMin_Cur",

        "RA.SpeedkpWrite",
        "RA.SpeedkiWrite",
        "RA.FlashPKP_Sw",
        "RA.FlashPKD_Sw",
        "RA.ACCSpeedWrite",
        "RA.DecValueWrite",
        "H08_x.PositionSoutMax",
        "H08_x.PositionSoutMin",

        "H0B_x.ActualSpeed",
        "H0B_x.PulseSpeed",
        "H0B_x.EncodeBias",
        "mcSpeedRamp.PulseShadow ",
        "H0B_x.PhaseIValue",
        "H0B_x.BusVoltage",
        "AdcSampleValue.ADCTmosFlt",

        "Device_ID",
        "H0C_x.Baurate",

        "H0D_x.SoftwareReset",
        "H0D_x.FalutReset",
        "H0D_x.EmergencyStop",
    };

    public string?[] minValue =
    {
        "-",
        "-",
        "-",
        "-",

        "-",
        "-",
        "-",
        "-",
        "-",
        "-",
        "-",
        "-",
        "-",
        "-",
        "-",

        "0",
        "0",
        "0",
        "-",
        "-",

        "1",
        "1",
        "0",
        "0",
        "5",
        "-",

        "0",
        "5",

        "0",
        "0",
        "0",

        "0",
        "0",
        "0",
        "0",
        "0",
        "0",
        "0",
        "0",

        "-",
        "-",
        "-",
        "-",
        "-",
        "-",
        "-",

        "1",
        "1",

        "0",
        "0",
        "0",
    };

    public string?[] maxValue =
    {
        "-",
        "-",
        "-",
        "-",

        "-",
        "-",
        "-",
        "-",
        "-",
        "-",
        "-",
        "-",
        "-",
        "-",
        "-",

        "4",
        "1",
        "1",
        "-",
        "-",

        "0xFFFF",
        "3",
        "0xFFFF",
        "0xFFFF",
        "0xFF",
        "-",

        "1500",
        "0xFF",

        "0xFFFF",
        "0xFFFF",
        "0xFFFF",

        "0xFFFF",
        "0xFFFF",
        "0xFFFF",
        "0xFFFF",
        "0xFFFF",
        "0xFFFF",
        "1500",
        "1500",

        "-",
        "-",
        "-",
        "-",
        "-",
        "-",
        "-",

        "0xFF",
        "4",

        "1",
        "1",
        "1",
    };

    public string?[] defaultValue =
    {
        "1",
        "1",
        "1",
        "1",

        "23",
        "1",
        "-",
        "-",
        "2",
        "-",
        "38",
        "-",
        "-",
        "-",
        "50",
        
        "3",
        "0",
        "0",
        "-",        
        "0",
        
        "400",
        "1",
        "0",
        "0",
        "30",
        "-",
        
        "0",
        "10",
        
        "0",
        "18",
        "19",
        
        "7371",
        "169",
        "1749",
        "1900",
        "500",
        "500",
        "1200",
        "1200",
        
        "-",
        "-",
        "-",
        "-",
        "-",
        "-",
        "-",
        
        "3",
        "4",
        
        "0",
        "0",
        "0",

    };
}