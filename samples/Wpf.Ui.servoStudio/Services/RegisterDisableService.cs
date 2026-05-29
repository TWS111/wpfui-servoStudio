// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Wpf.Ui.servoStudio.Services;

/// <summary>
/// 协议栈枚举，用于在禁用集合中区分三种总线。
/// </summary>
public enum ProtocolStack
{
    EtherCAT,
    CANopen,
    Modbus,
}

/// <summary>
/// 寄存器禁用记忆服务（按协议栈分别保存）。<br/>
/// 一旦某寄存器被禁用：
/// <list type="bullet">
///   <item><description>Modbus 全寄存器轮询会跳过。</description></item>
///   <item><description>所有协议栈的 SDO/Modbus 写入会拒绝（IServoMaster.TryWriteSDO 短路返回 false）。</description></item>
///   <item><description>显示该寄存器的页面（硬件参数等）通过订阅 <see cref="Changed"/> 事件刷新自身集合，从 UI 中隐藏。</description></item>
///   <item><description>状态通过 <see cref="UserSettingsService"/> 持久化，应用重启自动生效。</description></item>
/// </list>
/// </summary>
public static class RegisterDisableService
{
    private static readonly Dictionary<ProtocolStack, HashSet<string>> _disabled = new()
    {
        [ProtocolStack.EtherCAT] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        [ProtocolStack.CANopen] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        [ProtocolStack.Modbus] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    };

    static RegisterDisableService()
    {
        Reload();
    }

    /// <summary>禁用集合发生变化时触发（增/删/批量重载、ActiveStack 变更均会触发一次）。</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// 当前生效的协议栈（由 DeviceAddViewModel 在协议切换时同步）。<br/>
    /// 当各页面（运动限制/PID/硬件等）需要判断"应该按哪个协议栈的禁用列表来隐藏寄存器"时，
    /// 调用 <see cref="IsDisabledForActive"/> 即可。<br/>
    /// 为 null 时表示尚未连接任何协议栈，<see cref="IsDisabledForActive"/> 一律返回 false。
    /// </summary>
    public static ProtocolStack? ActiveStack
    {
        get => _activeStack;
        set
        {
            if (_activeStack == value) return;
            _activeStack = value;
            // 协议栈切换会改变各页面应应用的禁用集合，因此触发一次 Changed 让订阅者刷新。
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
    private static ProtocolStack? _activeStack;

    /// <summary>统一构造寄存器键： "0xIIII/SS"，与协议无关。</summary>
    public static string MakeKey(ushort index, byte subIndex) => $"0x{index:X4}/{subIndex:X2}";

    /// <summary>判断指定协议栈下某寄存器键是否被禁用。</summary>
    public static bool IsDisabled(ProtocolStack stack, string key)
        => _disabled.TryGetValue(stack, out var set) && set.Contains(key);

    /// <summary>判断指定协议栈下 (index, subIndex) 是否被禁用。</summary>
    public static bool IsDisabled(ProtocolStack stack, ushort index, byte subIndex)
        => IsDisabled(stack, MakeKey(index, subIndex));

    /// <summary>判断 (index, subIndex) 在当前 <see cref="ActiveStack"/> 下是否被禁用。<br/>
    /// 当无活动协议栈时返回 false（不隐藏）。</summary>
    public static bool IsDisabledForActive(ushort index, byte subIndex)
        => _activeStack is ProtocolStack s && IsDisabled(s, index, subIndex);

    /// <summary>设置某寄存器在指定协议栈下的禁用状态，并立即持久化。</summary>
    public static void SetDisabled(ProtocolStack stack, string key, bool disabled)
    {
        if (string.IsNullOrEmpty(key)) return;
        var set = _disabled[stack];
        bool changed = disabled ? set.Add(key) : set.Remove(key);
        if (!changed) return;
        Save();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>从用户设置重新载入禁用集合，应用启动 / 设置导入后调用。</summary>
    public static void Reload()
    {
        UserSettings s = UserSettingsService.Load();
        Replace(_disabled[ProtocolStack.EtherCAT], s.DisabledRegisters_EtherCAT);
        Replace(_disabled[ProtocolStack.CANopen], s.DisabledRegisters_CANopen);
        Replace(_disabled[ProtocolStack.Modbus], s.DisabledRegisters_Modbus);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void Replace(HashSet<string> set, IEnumerable<string>? src)
    {
        set.Clear();
        if (src is null) return;
        foreach (string k in src)
        {
            if (!string.IsNullOrEmpty(k))
                _ = set.Add(k);
        }
    }

    private static void Save()
    {
        try
        {
            UserSettings s = UserSettingsService.Load();
            s.DisabledRegisters_EtherCAT = _disabled[ProtocolStack.EtherCAT].ToList();
            s.DisabledRegisters_CANopen = _disabled[ProtocolStack.CANopen].ToList();
            s.DisabledRegisters_Modbus = _disabled[ProtocolStack.Modbus].ToList();
            UserSettingsService.Save(s);
        }
        catch
        {
            // best-effort persistence
        }
    }
}
