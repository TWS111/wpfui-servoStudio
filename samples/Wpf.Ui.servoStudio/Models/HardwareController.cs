// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Wpf.Ui.servoStudio.Models;

public class HardwareController : INotifyPropertyChanged
{
    /// <summary>H 分组索引，如 "H00.00"</summary>
    public string Index { get; set; } = string.Empty;

    /// <summary>CiA402 通信地址，如 "2000-01h"</summary>
    public string CommAddress { get; set; } = string.Empty;

    /// <summary>参数名称（中文）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>内部变量名</summary>
    public string VariableName { get; set; } = string.Empty;

    /// <summary>参数单位</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>参数分组名称</summary>
    public string GroupName { get; set; } = string.Empty;

    /// <summary>最小值</summary>
    public string Min { get; set; } = "-";

    /// <summary>最大值</summary>
    public string Max { get; set; } = "-";

    /// <summary>默认值</summary>
    public string DefaultValue { get; set; } = "-";

    /// <summary>是否为只读参数</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>SDO 对象字典索引</summary>
    public ushort SdoIndex { get; set; }

    /// <summary>SDO 子索引</summary>
    public byte SdoSubIndex { get; set; }

    private string _value = string.Empty;
    /// <summary>当前值（从设备读取或用户编辑）</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (_value != value)
            {
                _value = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsModified));
            }
        }
    }

    private string _deviceValue = string.Empty;
    /// <summary>设备端当前值（上次读取的值）</summary>
    public string DeviceValue
    {
        get => _deviceValue;
        set
        {
            if (_deviceValue != value)
            {
                _deviceValue = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsModified));
            }
        }
    }

    /// <summary>是否已被用户修改（Value != DeviceValue）</summary>
    public bool IsModified => !IsReadOnly && Value != DeviceValue;

    private bool _isReadSuccess;
    /// <summary>最近一次读取是否成功</summary>
    public bool IsReadSuccess
    {
        get => _isReadSuccess;
        set { _isReadSuccess = value; OnPropertyChanged(); }
    }

    private string _statusText = string.Empty;
    /// <summary>状态描述文本</summary>
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 从 HRegisterEntry 构造 HardwareController
    /// </summary>
    public static HardwareController FromRegisterEntry(HRegisterEntry entry)
    {
        return new HardwareController
        {
            Index = entry.HIndex,
            CommAddress = entry.CommAddress,
            Name = entry.ParameterName,
            VariableName = entry.VariableName,
            Unit = entry.Unit,
            GroupName = entry.GroupName,
            Min = entry.MinValue,
            Max = entry.MaxValue,
            DefaultValue = entry.DefaultValue,
            IsReadOnly = entry.IsReadOnly,
            SdoIndex = entry.SdoIndex,
            SdoSubIndex = entry.SdoSubIndex,
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}