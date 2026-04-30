// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Wpf.Ui.servoStudio.Models;

namespace Wpf.Ui.servoStudio.Services;

/// <summary>
/// 全局数据落盘桥接器。
///
/// <para>
/// 业务代码可在任意位置调用 <see cref="LogMasterFrame"/> / <see cref="LogSlaveFrame"/> 记录一帧，
/// 实际入队与写文件由 <see cref="DataFrameLogger"/> 在后台线程完成 —— 生产者路径无锁、非阻塞。
/// </para>
///
/// <para>
/// 只有当用户在"数据存储"页开启存储，且该变量被勾选（或未设置过滤 => 全部记录）时，帧才会落盘。
/// </para>
/// </summary>
public static class FrameLogBridge
{
    /// <summary>是否启用记录；由 DataSaveViewModel 启停控制。</summary>
    public static volatile bool IsEnabled;

    /// <summary>
    /// 可选的变量白名单（以 FullName 区分，FullName = Group.Name 或 Name）。
    /// null 或空集合 => 全部记录。
    /// </summary>
    private static volatile HashSet<string>? _filter;

    public static void SetFilter(IEnumerable<string>? names)
    {
        if (names == null) { _filter = null; return; }

        var set = new HashSet<string>(names, StringComparer.Ordinal);
        _filter = set.Count == 0 ? null : set;
    }

    public static void LogMasterFrame(string group, string name, ushort index, byte subIndex, string dataType, object? value, string unit = "")
    {
        if (!IsEnabled) return;
        var full = string.IsNullOrEmpty(group) ? name : $"{group}.{name}";
        HashSet<string>? filter = _filter;
        if (filter != null && !filter.Contains(full)) return;

        DataFrameLogger.Instance.Enqueue(new DataFrameLogger.FrameRecord
        {
            Timestamp = DateTime.Now,
            Source = DataVariableSource.Master,
            Group = group ?? string.Empty,
            Name = name ?? string.Empty,
            SdoIndex = index,
            SdoSubIndex = subIndex,
            DataType = dataType ?? string.Empty,
            Value = value?.ToString() ?? string.Empty,
            Unit = unit ?? string.Empty,
        });
    }

    public static void LogSlaveFrame(string group, string name, ushort index, byte subIndex, string dataType, object? value, string unit = "")
    {
        if (!IsEnabled) return;
        var full = string.IsNullOrEmpty(group) ? name : $"{group}.{name}";
        HashSet<string>? filter = _filter;
        if (filter != null && !filter.Contains(full)) return;

        DataFrameLogger.Instance.Enqueue(new DataFrameLogger.FrameRecord
        {
            Timestamp = DateTime.Now,
            Source = DataVariableSource.Slave,
            Group = group ?? string.Empty,
            Name = name ?? string.Empty,
            SdoIndex = index,
            SdoSubIndex = subIndex,
            DataType = dataType ?? string.Empty,
            Value = value?.ToString() ?? string.Empty,
            Unit = unit ?? string.Empty,
        });
    }
}