// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Models;

namespace Core.CANopen.CiA402;

/// <summary>
/// 通过 CANopen SDO 访问的 CiA402 伺服从机。<br/>
/// 与 <c>EtherCATSlave_CiA402</c> / <c>ModbusSlave_CiA402</c> 接口对位：
/// 暴露 <see cref="SlaveAddr"/>（即 nodeId 1~127）、<see cref="SlaveName"/>、控制字/状态字/操作模式等高层访问。<br/>
/// 默认沿用 CiA402 标准对象索引（不需要厂家映射）：<see cref="Cia402OdIndex.Controlword"/> / <see cref="Cia402OdIndex.Statusword"/> /
/// <see cref="Cia402OdIndex.ModesOfOperation"/>。
/// </summary>
public class CanOpenSlave_CiA402 : IServoAxis
{
    private readonly CanOpenMaster _master;

    public CanOpenSlave_CiA402(CanOpenMaster master, int nodeId)
    {
        _master = master ?? throw new ArgumentNullException(nameof(master));
        if (nodeId is < 1 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeId), "CANopen nodeId 应在 1~127 之间");
        }

        SlaveAddr = nodeId;

        // CANopen 标准 CiA402 对象（无须厂家镜像）
        ControlWordIndex = (Cia402OdIndex.ControlWord, 0x00);
        StatusWordIndex = (Cia402OdIndex.StatusWord, 0x00);
        OperationModeIndex = (Cia402OdIndex.ModesOfOperation, 0x00);
    }

    /// <summary>CANopen nodeId（1~127）。</summary>
    public int SlaveAddr { get; }

    /// <summary>从机名称（来自 0x1008 ManufacturerDeviceName）。</summary>
    public string? SlaveName { get; private set; }

    /// <summary>软件版本（来自 0x100A ManufacturerSoftwareVersion）。</summary>
    public string? SoftwareVersion { get; private set; }

    /// <summary>厂商 ID（来自 0x1018 sub1）。</summary>
    public uint VendorId { get; private set; }

    /// <summary>产品代码（来自 0x1018 sub2）。</summary>
    public uint ProductCode { get; private set; }

    string? IServoAxis.SlaveName => SlaveName;
    string? IServoAxis.SoftwareVersion => SoftwareVersion;

    /// <summary>控制字 0x6040 的对象映射（默认 CiA402 标准）。</summary>
    public (ushort Index, byte SubIndex) ControlWordIndex { get; set; }

    /// <summary>状态字 0x6041 的对象映射（默认 CiA402 标准）。</summary>
    public (ushort Index, byte SubIndex) StatusWordIndex { get; set; }

    /// <summary>操作模式 0x6060/0x6061 的对象映射（默认 CiA402 标准）。</summary>
    public (ushort Index, byte SubIndex) OperationModeIndex { get; set; }

    /// <summary>
    /// 探测从机身份：通过 SDO 读取 0x1018（Identity）、0x1008（DeviceName）、0x100A（SwVersion）。<br/>
    /// 任意一项读取成功即认为从机存活，<see cref="SlaveName"/> / <see cref="SoftwareVersion"/> 会被填充。
    /// </summary>
    public bool ProbeIdentity()
    {
        bool any = false;

        // 0x1018 Identity Object: sub1=VendorId, sub2=ProductCode, sub3=Revision, sub4=SerialNumber
        if (_master.TryReadSDO(SlaveAddr, 0x1018, 0x01, out uint vid))
        {
            VendorId = vid; any = true;
        }
        if (_master.TryReadSDO(SlaveAddr, 0x1018, 0x02, out uint pc))
        {
            ProductCode = pc; any = true;
        }

        // 0x100A 软件版本（VISIBLE_STRING，加速 SDO 仅读到前 4 字节，作为简易标记）
        if (_master.TryReadSDO(SlaveAddr, 0x100A, 0x00, out uint swRaw))
        {
            SoftwareVersion = DecodeAsciiTrim(swRaw);
            any = true;
        }

        // 0x1008 设备名称（VISIBLE_STRING 同样仅取前 4 字节）
        if (_master.TryReadSDO(SlaveAddr, 0x1008, 0x00, out uint nameRaw))
        {
            string raw = DecodeAsciiTrim(nameRaw);
            SlaveName = string.IsNullOrEmpty(raw)
                ? $"CANopen Node #{SlaveAddr}"
                : raw;
            any = true;
        }
        else if (any)
        {
            SlaveName = $"CANopen Node #{SlaveAddr} (PC=0x{ProductCode:X8})";
        }

        return any;
    }

    private static string DecodeAsciiTrim(uint raw)
    {
        Span<byte> b = stackalloc byte[4];
        b[0] = (byte)(raw & 0xFF);
        b[1] = (byte)((raw >> 8) & 0xFF);
        b[2] = (byte)((raw >> 16) & 0xFF);
        b[3] = (byte)((raw >> 24) & 0xFF);
        int len = 0;
        while (len < 4 && b[len] >= 0x20 && b[len] < 0x7F)
        {
            len++;
        }

        return len == 0 ? string.Empty : System.Text.Encoding.ASCII.GetString(b[..len]);
    }

    // ────────────────── CiA402 控制字 / 状态字 ──────────────────

    /// <summary>读取状态字 (0x6041)。</summary>
    public bool TryReadStatusWord(out ushort value)
        => _master.TryReadSDO(SlaveAddr, StatusWordIndex.Index, StatusWordIndex.SubIndex, out value);

    /// <summary>写入控制字 (0x6040)。</summary>
    public bool TryWriteControlWord(ushort value)
        => _master.TryWriteSDO(SlaveAddr, ControlWordIndex.Index, ControlWordIndex.SubIndex, value);

    /// <summary>读取当前操作模式（CiA402 标准 sbyte）。</summary>
    public bool TryReadOperationMode(out sbyte mode)
    {
        bool ok = _master.TryReadSDO(SlaveAddr, OperationModeIndex.Index, OperationModeIndex.SubIndex, out sbyte v);
        mode = ok ? v : (sbyte)0;
        return ok;
    }

    /// <summary>设置目标操作模式（CiA402 标准 sbyte）。</summary>
    public bool TryWriteOperationMode(sbyte mode)
        => _master.TryWriteSDO(SlaveAddr, OperationModeIndex.Index, OperationModeIndex.SubIndex, mode);

    // ────────────────── 通过 H 索引快捷读写（与 Modbus 从机一致的语义） ──────────────────

    /// <summary>按 HVariables 中的 HIndex（如 "H08.00"）读取 16 位寄存器值。</summary>
    public bool TryReadByHIndex(string hIndex, out ushort value)
    {
        value = 0;
        HRegisterEntry? entry = HVariables.FindByHIndex(hIndex);
        if (entry == null)
        {
            return false;
        }

        return _master.TryReadSDO(SlaveAddr, entry.SdoIndex, entry.SdoSubIndex, out value);
    }

    /// <summary>按 HVariables 中的 HIndex 写 16 位寄存器值。</summary>
    public bool TryWriteByHIndex(string hIndex, ushort value)
    {
        HRegisterEntry? entry = HVariables.FindByHIndex(hIndex);
        if (entry == null)
        {
            return false;
        }

        if (entry.IsReadOnly)
        {
            return false;
        }

        return _master.TryWriteSDO(SlaveAddr, entry.SdoIndex, entry.SdoSubIndex, value);
    }
}
