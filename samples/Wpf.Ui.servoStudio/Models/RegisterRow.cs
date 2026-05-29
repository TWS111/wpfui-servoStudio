// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Wpf.Ui.servoStudio.Services;

namespace Wpf.Ui.servoStudio.Models;

/// <summary>
/// 厂家参数页中"协议栈寄存器列表"DataGrid 行模型。<br/>
/// 与 <see cref="RegisterDisableService"/> 双向绑定 <see cref="IsDisabled"/>，
/// 读取后写入 <see cref="Value"/> 与 <see cref="Status"/>。
/// </summary>
public partial class RegisterRow : ObservableObject
{
    /// <summary>所属协议栈。</summary>
    public ProtocolStack Stack { get; init; }

    /// <summary>禁用集合中的统一键，由 <see cref="RegisterDisableService.MakeKey"/> 生成。</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>对象字典索引（SDO Index / Modbus 映射前的 CiA 索引）。</summary>
    public ushort Index { get; init; }

    /// <summary>对象字典子索引。</summary>
    public byte SubIndex { get; init; }

    /// <summary>显示用的索引（含 0x 前缀）。</summary>
    public string IndexHex => $"0x{Index:X4}";

    /// <summary>显示用的子索引（含 0x 前缀）。</summary>
    public string SubIndexHex => $"0x{SubIndex:X2}";

    /// <summary>参数名（含中文描述）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>参数所属分组（仅 Modbus / H 参数表使用）。</summary>
    public string Group { get; init; } = string.Empty;

    /// <summary>读写时使用的数据类型。</summary>
    public SdoDataType DataType { get; init; } = SdoDataType.UInt16;

    /// <summary>数据类型显示文本。</summary>
    public string DataTypeText => DataType.ToString();

    [ObservableProperty]
    private string _value = "—";

    [ObservableProperty]
    private string _status = string.Empty;

    private bool _isDisabled;

    /// <summary>是否禁用（写入会持久化到 <see cref="RegisterDisableService"/>）。</summary>
    public bool IsDisabled
    {
        get => _isDisabled;
        set
        {
            if (SetProperty(ref _isDisabled, value))
            {
                RegisterDisableService.SetDisabled(Stack, Key, value);
            }
        }
    }

    /// <summary>不触发持久化、仅同步 UI 用（由服务事件回调时调用）。</summary>
    public void SetIsDisabledFromService(bool value)
    {
        if (_isDisabled == value) return;
        _isDisabled = value;
        OnPropertyChanged(nameof(IsDisabled));
    }
}
