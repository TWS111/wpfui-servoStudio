// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wpf.Ui.servoStudio.Models;

/// <summary>
/// 变量来源（CiA 标准对象 / 汇川 H 组寄存器）。
/// </summary>
public enum ServoVariableSource
{
    Cia402,
    HRegister,
}

/// <summary>
/// 变量在 SDO 上的存储类型，用于波形采样时按正确位宽/符号读取。
/// </summary>
public enum ServoVariableType
{
    Int8,
    UInt8,
    Int16,
    UInt16,
    Int32,
    UInt32,
}

/// <summary>
/// 统一表示一个可订阅的伺服变量项。供「快速控制页」波形源下拉多选使用。
/// CiA 项以对象字典索引（如 0x6064）寻址；H 项以 CommAddress 解析出的 SDO Index/SubIndex 寻址。
/// </summary>
public partial class ServoVariableItem : ObservableObject
{
    public ServoVariableSource Source
    {
        get; init;
    }

    /// <summary>显示名，例如 "实际位置 Position Actual"。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>简短地址显示，例如 "0x6064" 或 "H00.05"。</summary>
    public string ShortName { get; init; } = string.Empty;

    public ushort Index
    {
        get; init;
    }
    public byte SubIndex
    {
        get; init;
    }
    public ServoVariableType DataType { get; init; } = ServoVariableType.Int32;
    public string Group { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    public string SourceLabel => Source == ServoVariableSource.Cia402 ? "CiA" : "H";

    public string AddressDisplay => Source == ServoVariableSource.Cia402
        ? $"0x{Index:X4}"
        : ShortName;
}

/// <summary>
/// 汇总 CiA + H 变量的全局目录。CiA 关键运行变量靠前，方便检索。
/// </summary>
public static class ServoVariableCatalog
{
    private static List<ServoVariableItem>? _items;

    public static IReadOnlyList<ServoVariableItem> All => _items ??= Build();

    private static List<ServoVariableItem> Build()
    {
        List<ServoVariableItem> list = new();

        // ── CiA402 关键运行变量（强制排在最前） ─────────────────────────────
        AddCia(list, "状态字 Statusword", 0x6041, ServoVariableType.UInt16, "CiA402 / 控制");
        AddCia(list, "控制字 Controlword", 0x6040, ServoVariableType.UInt16, "CiA402 / 控制");
        AddCia(list, "运行模式 Modes of Operation", 0x6060, ServoVariableType.Int8, "CiA402 / 控制");
        AddCia(list, "运行模式显示 Mode Display", 0x6061, ServoVariableType.Int8, "CiA402 / 控制");
        AddCia(list, "错误代码 Error Code", 0x603F, ServoVariableType.UInt16, "CiA402 / 故障");

        AddCia(list, "实际位置 Position Actual", 0x6064, ServoVariableType.Int32, "CiA402 / 位置", "p");
        AddCia(list, "实际位置(内部) Position Internal", 0x6063, ServoVariableType.Int32, "CiA402 / 位置", "p");
        AddCia(list, "目标位置 Target Position", 0x607A, ServoVariableType.Int32, "CiA402 / 位置", "p");
        AddCia(list, "位置跟随误差 Following Error", 0x60F4, ServoVariableType.Int32, "CiA402 / 位置", "p");

        AddCia(list, "实际速度 Velocity Actual", 0x606C, ServoVariableType.Int32, "CiA402 / 速度", "rpm");
        AddCia(list, "目标速度 Target Velocity", 0x60FF, ServoVariableType.Int32, "CiA402 / 速度", "rpm");

        AddCia(list, "实际转矩 Torque Actual", 0x6077, ServoVariableType.Int16, "CiA402 / 转矩", "‰");
        AddCia(list, "目标转矩 Target Torque", 0x6071, ServoVariableType.Int16, "CiA402 / 转矩", "‰");
        AddCia(list, "实际电流 Current Actual", 0x6078, ServoVariableType.Int16, "CiA402 / 电流");

        // ── H 组寄存器（按表追加在后） ───────────────────────────────────────
        foreach (HRegisterEntry h in HVariables.RegisterTable)
        {
            if (h.SdoIndex == 0)
            {
                continue;
            }

            list.Add(new ServoVariableItem
            {
                Source = ServoVariableSource.HRegister,
                Name = string.IsNullOrEmpty(h.ParameterName)
                    ? h.HIndex
                    : $"{h.ParameterName} ({h.HIndex})",
                ShortName = h.HIndex,
                Index = h.SdoIndex,
                SubIndex = h.SdoSubIndex,
                DataType = ServoVariableType.Int32,
                Group = h.GroupName,
                Unit = h.Unit,
            });
        }

        return list;
    }

    private static void AddCia(
        List<ServoVariableItem> list,
        string name,
        ushort index,
        ServoVariableType type,
        string group,
        string unit = "")
    {
        list.Add(new ServoVariableItem
        {
            Source = ServoVariableSource.Cia402,
            Name = name,
            ShortName = $"0x{index:X4}",
            Index = index,
            SubIndex = 0,
            DataType = type,
            Group = group,
            Unit = unit,
        });
    }
}
