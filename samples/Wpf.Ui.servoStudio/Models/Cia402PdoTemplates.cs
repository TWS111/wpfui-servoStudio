// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.Generic;

namespace Wpf.Ui.servoStudio.Models;

/// <summary>
/// 单个 RPDO 或 TPDO 的配置：COB-ID（null 表示用预定义默认值）、传输类型、映射条目。
/// </summary>
public sealed class Cia402PdoChannel
{
    /// <summary>是否启用该通道（未启用时不下发 SDO 配置）。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>自定义 COB-ID；null 时使用 CiA301 预定义值。</summary>
    public uint? CobIdOverride { get; init; }

    /// <summary>
    /// CiA301 PDO 传输类型：<br/>
    /// 0 = 同步非循环（变化时下个 SYNC 发送）<br/>
    /// 1..240 = 同步循环（每 N 个 SYNC 发送一次）<br/>
    /// 254/255 = 异步事件
    /// </summary>
    public byte TransmissionType { get; init; } = 1;

    /// <summary>事件计时器 (ms)，仅 TPDO 使用，0 表示不设。</summary>
    public ushort EventTimerMs { get; init; }

    /// <summary>禁止时间 (100μs)，仅 TPDO 使用，0 表示不设。</summary>
    public ushort InhibitTime100Us { get; init; }

    /// <summary>映射条目（每项 <c>Cia402Helper.BuildPdoMapping</c>），最多 8 项。</summary>
    public uint[] MapEntries { get; init; } = Array.Empty<uint>();
}

/// <summary>
/// 一个完整的 CiA402 PDO 模板：4 路 RPDO + 4 路 TPDO 描述。
/// </summary>
public sealed class Cia402PdoTemplate
{
    public string Name { get; init; } = string.Empty;
    public Cia402PdoChannel[] Rpdos { get; init; } = new Cia402PdoChannel[4];
    public Cia402PdoChannel[] Tpdos { get; init; } = new Cia402PdoChannel[4];
}

/// <summary>
/// 内置的典型 CiA402 PDO 模板集合：CSP / CSV / CST 等。<br/>
/// 在 Operational 之前通过 <c>CanOpenMaster.ConfigureRpdoMapping</c> / <c>ConfigureTpdoMapping</c> 写入从机。
/// </summary>
public static class Cia402PdoTemplates
{
    private static uint Map(ushort index, byte sub, byte bits)
        => Cia402Helper.BuildPdoMapping(index, sub, bits);

    /// <summary>
    /// CSP（周期同步位置）典型模板：<br/>
    /// RPDO1 = ControlWord(16) + ModesOfOperation(8) + padding<br/>
    /// RPDO2 = TargetPosition(32)<br/>
    /// RPDO3 = VelocityOffset(32) + TorqueOffset(16)<br/>
    /// TPDO1 = StatusWord(16) + ModesOfOperationDisplay(8) + padding<br/>
    /// TPDO2 = PositionActualValue(32)<br/>
    /// TPDO3 = VelocityActualValue(32) + TorqueActualValue(16)
    /// </summary>
    public static Cia402PdoTemplate Csp { get; } = new()
    {
        Name = "CSP - 周期同步位置",
        Rpdos = new[]
        {
            new Cia402PdoChannel { TransmissionType = 1, MapEntries = new[]
            {
                Map(Cia402OdIndex.ControlWord, 0, 16),
                Map(Cia402OdIndex.ModesOfOperation, 0, 8),
            }},
            new Cia402PdoChannel { TransmissionType = 1, MapEntries = new[]
            {
                Map(Cia402OdIndex.TargetPosition, 0, 32),
            }},
            new Cia402PdoChannel { TransmissionType = 1, MapEntries = new[]
            {
                Map(Cia402OdIndex.VelocityOffset, 0, 32),
                Map(Cia402OdIndex.TorqueOffset, 0, 16),
            }},
            new Cia402PdoChannel { Enabled = false },
        },
        Tpdos = new[]
        {
            new Cia402PdoChannel { TransmissionType = 1, MapEntries = new[]
            {
                Map(Cia402OdIndex.StatusWord, 0, 16),
                Map(0x6061, 0, 8), // ModesOfOperationDisplay
            }},
            new Cia402PdoChannel { TransmissionType = 1, MapEntries = new[]
            {
                Map(Cia402OdIndex.PositionActualValue, 0, 32),
            }},
            new Cia402PdoChannel { TransmissionType = 1, MapEntries = new[]
            {
                Map(Cia402OdIndex.VelocityActualValue, 0, 32),
                Map(Cia402OdIndex.TorqueActualValue, 0, 16),
            }},
            new Cia402PdoChannel { Enabled = false },
        },
    };

    /// <summary>
    /// CSV（周期同步速度）典型模板：<br/>
    /// RPDO1 = ControlWord(16) + ModesOfOperation(8)<br/>
    /// RPDO2 = TargetVelocity(32) + TorqueOffset(16)<br/>
    /// TPDO1 = StatusWord(16) + ModesOfOperationDisplay(8)<br/>
    /// TPDO2 = VelocityActualValue(32) + PositionActualValue(32)
    /// </summary>
    public static Cia402PdoTemplate Csv { get; } = new()
    {
        Name = "CSV - 周期同步速度",
        Rpdos = new[]
        {
            new Cia402PdoChannel { TransmissionType = 1, MapEntries = new[]
            {
                Map(Cia402OdIndex.ControlWord, 0, 16),
                Map(Cia402OdIndex.ModesOfOperation, 0, 8),
            }},
            new Cia402PdoChannel { TransmissionType = 1, MapEntries = new[]
            {
                Map(Cia402OdIndex.TargetVelocity, 0, 32),
                Map(Cia402OdIndex.TorqueOffset, 0, 16),
            }},
            new Cia402PdoChannel { Enabled = false },
            new Cia402PdoChannel { Enabled = false },
        },
        Tpdos = new[]
        {
            new Cia402PdoChannel { TransmissionType = 1, MapEntries = new[]
            {
                Map(Cia402OdIndex.StatusWord, 0, 16),
                Map(0x6061, 0, 8),
            }},
            new Cia402PdoChannel { TransmissionType = 1, MapEntries = new[]
            {
                Map(Cia402OdIndex.VelocityActualValue, 0, 32),
                Map(Cia402OdIndex.PositionActualValue, 0, 32),
            }},
            new Cia402PdoChannel { Enabled = false },
            new Cia402PdoChannel { Enabled = false },
        },
    };

    /// <summary>
    /// CST（周期同步转矩）典型模板：<br/>
    /// RPDO1 = ControlWord(16) + ModesOfOperation(8)<br/>
    /// RPDO2 = TargetTorque(16) + TorqueOffset(16)<br/>
    /// TPDO1 = StatusWord(16) + ModesOfOperationDisplay(8)<br/>
    /// TPDO2 = TorqueActualValue(16) + VelocityActualValue(32) + PositionActualValue(32) 拆为 TPDO2+TPDO3
    /// </summary>
    public static Cia402PdoTemplate Cst { get; } = new()
    {
        Name = "CST - 周期同步转矩",
        Rpdos = new[]
        {
            new Cia402PdoChannel { TransmissionType = 1, MapEntries = new[]
            {
                Map(Cia402OdIndex.ControlWord, 0, 16),
                Map(Cia402OdIndex.ModesOfOperation, 0, 8),
            }},
            new Cia402PdoChannel { TransmissionType = 1, MapEntries = new[]
            {
                Map(Cia402OdIndex.TargetTorque, 0, 16),
                Map(Cia402OdIndex.TorqueOffset, 0, 16),
            }},
            new Cia402PdoChannel { Enabled = false },
            new Cia402PdoChannel { Enabled = false },
        },
        Tpdos = new[]
        {
            new Cia402PdoChannel { TransmissionType = 1, MapEntries = new[]
            {
                Map(Cia402OdIndex.StatusWord, 0, 16),
                Map(0x6061, 0, 8),
            }},
            new Cia402PdoChannel { TransmissionType = 1, MapEntries = new[]
            {
                Map(Cia402OdIndex.TorqueActualValue, 0, 16),
                Map(Cia402OdIndex.VelocityActualValue, 0, 32),
            }},
            new Cia402PdoChannel { TransmissionType = 1, MapEntries = new[]
            {
                Map(Cia402OdIndex.PositionActualValue, 0, 32),
            }},
            new Cia402PdoChannel { Enabled = false },
        },
    };

    /// <summary>按 CiA402 工作模式返回内置模板，未匹配返回 null。</summary>
    public static Cia402PdoTemplate? ForMode(Cia402OperationMode mode) => mode switch
    {
        Cia402OperationMode.CyclicSynchronousPosition => Csp,
        Cia402OperationMode.CyclicSynchronousVelocity => Csv,
        Cia402OperationMode.CyclicSynchronousTorque => Cst,
        _ => null,
    };

    /// <summary>所有内置模板（用于 UI 列表）。</summary>
    public static IReadOnlyList<Cia402PdoTemplate> All { get; } = new[] { Csp, Csv, Cst };
}
