// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Wpf.Ui.servoStudio.Models;

/// <summary>
/// 数据存储 — 支持的文件格式。
/// </summary>
public enum DataSaveFormat
{
    /// <summary>逗号分隔值</summary>
    Csv,
    /// <summary>Tab 分隔值</summary>
    Tsv,
    /// <summary>Excel 兼容 XML (SpreadsheetML 2003)</summary>
    Xls,
    /// <summary>行分隔 JSON (JSONL)</summary>
    Json,
}

/// <summary>
/// 变量来源 — 区分从机读回值与主机写入（或主机端缓存）值。
/// </summary>
public enum DataVariableSource
{
    /// <summary>从机（通过 SDO 读取）</summary>
    Slave,
    /// <summary>主机（主机端发送/设定值）</summary>
    Master,
}

/// <summary>
/// 一个可被数据记录器采样的变量描述。
/// </summary>
public partial class DataVariableItem : ObservableObject
{
    /// <summary>是否被选中参与采样</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>最近一次采集到的值（UI 显示用）</summary>
    [ObservableProperty]
    private string _lastValue = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Group { get; init; } = string.Empty;

    public DataVariableSource Source { get; init; }

    /// <summary>对象字典索引（Slave 时有值）</summary>
    public ushort SdoIndex { get; init; }

    /// <summary>对象字典子索引</summary>
    public byte SdoSubIndex { get; init; }

    /// <summary>类型标识（用于解析，CiA402 通常 uint16/int32/int16 等）</summary>
    public string DataType { get; init; } = "uint16";

    public string Unit { get; init; } = string.Empty;

    /// <summary>主机端变量 —— 提供值的委托；从机变量忽略此字段</summary>
    public Func<object?>? ValueProvider { get; init; }

    public string FullName => string.IsNullOrEmpty(Group) ? Name : $"{Group}.{Name}";

    public string SourceText => Source == DataVariableSource.Slave ? "从机" : "主机";

    public string AddressText => Source == DataVariableSource.Slave
        ? $"0x{SdoIndex:X4}-{SdoSubIndex:X2}h"
        : "—";
}