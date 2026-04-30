// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Models;

namespace Core.Modbus.CiA402;

/// <summary>
/// 通过 Modbus RTU 访问的 CiA402 伺服从机。<br/>
/// 与 <c>EtherCATSlave_CiA402</c> 接口对位：暴露 <see cref="SlaveAddr"/>、<see cref="SlaveName"/>、状态字/控制字/操作模式等高层访问。<br/>
/// 默认采用 HVariables（H 参数表）的 CommAddress 进行寻址：
/// <list type="bullet">
///   <item><description>控制字   = H03.50（CommAddress 2003-33h）— 与典型汇川约定一致</description></item>
///   <item><description>状态字   = H0B.30（CommAddress 200B-1Fh）— 镜像 0x6041</description></item>
///   <item><description>操作模式 = H02.00（CommAddress 2002-01h）</description></item>
/// </list>
/// 厂家差异较大，可通过设置 <see cref="ControlWordAddress"/> / <see cref="StatusWordAddress"/> / <see cref="OperationModeAddress"/>
/// 重写为厂家手册标注的 H 寄存器（CiA 索引/子索引形式）。
/// </summary>
public class ModbusSlave_CiA402 : IServoAxis
{
    private readonly ModbusRtuMaster _master;

    public ModbusSlave_CiA402(ModbusRtuMaster master, int slaveAddr)
    {
        _master = master ?? throw new ArgumentNullException(nameof(master));
        if (slaveAddr is < 1 or > 247)
            throw new ArgumentOutOfRangeException(nameof(slaveAddr), "Modbus 从机地址应在 1~247 之间");

        SlaveAddr = slaveAddr;

        // 默认 CiA402 控制字/状态字/操作模式映射（典型汇川 H 镜像）。
        // 调用方可在连接成功后根据厂家手册覆盖这三个映射。
        ControlWordAddress = (0x2003, 0x33);
        StatusWordAddress = (0x200B, 0x1F);
        OperationModeAddress = (0x2002, 0x01);
    }

    /// <summary>Modbus 从机地址（轴地址）。</summary>
    public int SlaveAddr { get; }

    /// <summary>从机名称（连接后由 <see cref="ProbeIdentity"/> 填充）。</summary>
    public string? SlaveName { get; private set; }

    /// <summary>软件版本号（来自 H01.00 = 2001-01h）。</summary>
    public string? SoftwareVersion { get; private set; }

    string? IServoAxis.SlaveName => SlaveName;
    string? IServoAxis.SoftwareVersion => SoftwareVersion;

    /// <summary>驱动器编号（来自 H01.02 = 2001-03h）。</summary>
    public ushort DriveNumber { get; private set; }

    /// <summary>电机编号（来自 H00.00 = 2000-01h）。</summary>
    public ushort MotorNumber { get; private set; }

    /// <summary>控制字 (CiA 0x6040) 的厂家镜像地址，默认 H03.50。</summary>
    public (ushort Index, byte SubIndex) ControlWordAddress { get; set; }

    /// <summary>状态字 (CiA 0x6041) 的厂家镜像地址，默认 H0B.30。</summary>
    public (ushort Index, byte SubIndex) StatusWordAddress { get; set; }

    /// <summary>操作模式 (CiA 0x6060) 的厂家镜像地址，默认 H02.00。</summary>
    public (ushort Index, byte SubIndex) OperationModeAddress { get; set; }

    /// <summary>
    /// 探测从机身份：读取 H01.00 软件版本、H01.02 驱动器编号、H00.00 电机编号。<br/>
    /// 任意一项读取成功即视为从机存活，<see cref="SlaveName"/> 与 <see cref="SoftwareVersion"/> 会被更新。
    /// </summary>
    public bool ProbeIdentity()
    {
        bool any = false;

        if (_master.TryReadSDO(SlaveAddr, 0x2001, 0x01, out ushort swVer))
        {
            SoftwareVersion = $"v{(swVer >> 8) & 0xFF:D2}.{swVer & 0xFF:D2}";
            any = true;
        }

        if (_master.TryReadSDO(SlaveAddr, 0x2001, 0x03, out ushort drvNum))
        {
            DriveNumber = drvNum;
            any = true;
        }

        if (_master.TryReadSDO(SlaveAddr, 0x2000, 0x01, out ushort motorNum))
        {
            MotorNumber = motorNum;
        }

        if (any)
        {
            SlaveName = string.IsNullOrEmpty(SoftwareVersion)
                ? $"Servo Drive #{DriveNumber}"
                : $"Servo Drive #{DriveNumber} (FW {SoftwareVersion})";
        }

        return any;
    }

    // ────────────────── CiA402 控制字 / 状态字 ──────────────────

    /// <summary>读取状态字（CiA402 0x6041 镜像）。</summary>
    public bool TryReadStatusWord(out ushort value)
        => _master.TryReadSDO(SlaveAddr, StatusWordAddress.Index, StatusWordAddress.SubIndex, out value);

    /// <summary>写入控制字（CiA402 0x6040 镜像）。</summary>
    public bool TryWriteControlWord(ushort value)
        => _master.TryWriteSDO(SlaveAddr, ControlWordAddress.Index, ControlWordAddress.SubIndex, value);

    /// <summary>读取当前操作模式（CiA402 0x6061/0x6060 镜像）。返回 sbyte（CiA402 标准）。</summary>
    public bool TryReadOperationMode(out sbyte mode)
    {
        bool ok = _master.TryReadSDO(SlaveAddr, OperationModeAddress.Index, OperationModeAddress.SubIndex, out ushort raw);
        mode = unchecked((sbyte)(raw & 0xFF));
        return ok;
    }

    /// <summary>设置目标操作模式（CiA402 0x6060 镜像）。</summary>
    public bool TryWriteOperationMode(sbyte mode)
    {
        ushort raw = (ushort)((byte)mode);
        return _master.TryWriteSDO(SlaveAddr, OperationModeAddress.Index, OperationModeAddress.SubIndex, raw);
    }

    // ────────────────── 通过 H 索引快捷读写 ──────────────────

    /// <summary>按 HVariables 中的 HIndex（如 "H08.00"）读取 16 位寄存器值。</summary>
    public bool TryReadByHIndex(string hIndex, out ushort value)
    {
        value = 0;
        HRegisterEntry? entry = HVariables.FindByHIndex(hIndex);
        if (entry == null) return false;
        return _master.TryReadSDO(SlaveAddr, entry.SdoIndex, entry.SdoSubIndex, out value);
    }

    /// <summary>按 HVariables 中的 HIndex 写 16 位寄存器值。</summary>
    public bool TryWriteByHIndex(string hIndex, ushort value)
    {
        HRegisterEntry? entry = HVariables.FindByHIndex(hIndex);
        if (entry == null) return false;
        if (entry.IsReadOnly) return false;
        return _master.TryWriteSDO(SlaveAddr, entry.SdoIndex, entry.SdoSubIndex, value);
    }
}
