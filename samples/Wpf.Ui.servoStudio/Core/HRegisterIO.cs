// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.Generic;
using Wpf.Ui.servoStudio.Models;

namespace Wpf.Ui.servoStudio.Core;

/// <summary>
/// 通用 H 寄存器（厂家自定义 SDO 对象，索引 0x2000~0x2011）读写工具。<br/>
/// 通过 <see cref="HVariables.FindByHIndex(string)"/> 解析 SDO 索引/子索引，
/// 再委托 <see cref="IServoMaster"/> 完成 SDO 读写，避免在各 ViewModel 中重复硬编码。
/// </summary>
public static class HRegisterIO
{
    /// <summary>检查 master/axis 是否已就绪（非 null）。</summary>
    private static bool Ready(IServoMaster? master, IServoAxis? axis)
        => master != null && axis != null;

    /// <summary>
    /// 写 H 寄存器（UINT16），失败将友好名加入 <paramref name="errors"/>。
    /// </summary>
    public static void SafeWriteHReg(
        IServoMaster? master,
        IServoAxis? axis,
        string hIndex,
        ushort value,
        List<string> errors,
        string friendlyName)
    {
        if (!Ready(master, axis))
        {
            errors.Add($"{friendlyName}(设备未连接)");
            return;
        }

        HRegisterEntry? entry = HVariables.FindByHIndex(hIndex);
        if (entry == null || entry.SdoIndex == 0)
        {
            errors.Add($"{friendlyName}(未找到寄存器定义:{hIndex})");
            return;
        }

        try
        {
            if (!master!.TryWriteSDO<ushort>(axis!.SlaveAddr, entry.SdoIndex, entry.SdoSubIndex, value))
            {
                errors.Add(friendlyName);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"{friendlyName}(异常:{ex.Message})");
        }
    }

    /// <summary>
    /// 写 H 寄存器（INT16）。
    /// </summary>
    public static void SafeWriteHRegSigned(
        IServoMaster? master,
        IServoAxis? axis,
        string hIndex,
        short value,
        List<string> errors,
        string friendlyName)
    {
        if (!Ready(master, axis))
        {
            errors.Add($"{friendlyName}(设备未连接)");
            return;
        }

        HRegisterEntry? entry = HVariables.FindByHIndex(hIndex);
        if (entry == null || entry.SdoIndex == 0)
        {
            errors.Add($"{friendlyName}(未找到寄存器定义:{hIndex})");
            return;
        }

        try
        {
            if (!master!.TryWriteSDO<short>(axis!.SlaveAddr, entry.SdoIndex, entry.SdoSubIndex, value))
            {
                errors.Add(friendlyName);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"{friendlyName}(异常:{ex.Message})");
        }
    }

    /// <summary>
    /// 读 H 寄存器（UINT16），成功后回调；失败/未连接静默返回 false。
    /// </summary>
    public static bool ReadHReg(
        IServoMaster? master,
        IServoAxis? axis,
        string hIndex,
        Action<ushort> onSuccess)
    {
        if (!Ready(master, axis))
        {
            return false;
        }

        HRegisterEntry? entry = HVariables.FindByHIndex(hIndex);
        if (entry == null || entry.SdoIndex == 0)
        {
            return false;
        }

        if (master!.TryReadSDO<ushort>(axis!.SlaveAddr, entry.SdoIndex, entry.SdoSubIndex, out ushort v))
        {
            onSuccess(v);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 读 H 寄存器（INT16），成功后回调；失败/未连接静默返回 false。
    /// </summary>
    public static bool ReadHRegSigned(
        IServoMaster? master,
        IServoAxis? axis,
        string hIndex,
        Action<short> onSuccess)
    {
        if (!Ready(master, axis))
        {
            return false;
        }

        HRegisterEntry? entry = HVariables.FindByHIndex(hIndex);
        if (entry == null || entry.SdoIndex == 0)
        {
            return false;
        }

        if (master!.TryReadSDO<short>(axis!.SlaveAddr, entry.SdoIndex, entry.SdoSubIndex, out short v))
        {
            onSuccess(v);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 直接读取 H 寄存器（UINT16），未连接或失败返回 <paramref name="defaultValue"/>。
    /// </summary>
    public static ushort ReadHRegOrDefault(
        IServoMaster? master,
        IServoAxis? axis,
        string hIndex,
        ushort defaultValue = 0)
    {
        if (!Ready(master, axis))
        {
            return defaultValue;
        }

        HRegisterEntry? entry = HVariables.FindByHIndex(hIndex);
        if (entry == null || entry.SdoIndex == 0)
        {
            return defaultValue;
        }

        return master!.TryReadSDO<ushort>(axis!.SlaveAddr, entry.SdoIndex, entry.SdoSubIndex, out ushort v)
            ? v
            : defaultValue;
    }

    /// <summary>
    /// 直接读取 H 寄存器（INT16），未连接或失败返回 <paramref name="defaultValue"/>。
    /// </summary>
    public static short ReadHRegSignedOrDefault(
        IServoMaster? master,
        IServoAxis? axis,
        string hIndex,
        short defaultValue = 0)
    {
        if (!Ready(master, axis))
        {
            return defaultValue;
        }

        HRegisterEntry? entry = HVariables.FindByHIndex(hIndex);
        if (entry == null || entry.SdoIndex == 0)
        {
            return defaultValue;
        }

        return master!.TryReadSDO<short>(axis!.SlaveAddr, entry.SdoIndex, entry.SdoSubIndex, out short v)
            ? v
            : defaultValue;
    }
}
