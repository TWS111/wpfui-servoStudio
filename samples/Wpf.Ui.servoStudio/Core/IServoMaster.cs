// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using Core.Net.EtherCAT;

namespace Wpf.Ui.servoStudio.Core;

/// <summary>
/// 统一的伺服主站访问抽象，使页面 ViewModel 与具体协议栈
/// （EtherCAT / Modbus RTU / 后续 CANopen）解耦。<br/>
/// 仅暴露所有协议栈共有的 SDO 风格读写能力（CiA301/CiA402 对象 + HVariables）。
/// </summary>
public interface IServoMaster
{
    /// <summary>读 SDO；失败返回 false。</summary>
    bool TryReadSDO<T>(int slaveAddr, ushort index, byte subIndex, out T value) where T : struct;

    /// <summary>写 SDO；失败返回 false。</summary>
    bool TryWriteSDO<T>(int slaveAddr, ushort index, byte subIndex, T value) where T : struct;

    /// <summary>读 SDO；失败抛出异常（与 EtherCATMaster.ReadSDO 行为一致）。</summary>
    T ReadSDO<T>(int slaveAddr, int index, int subIndex) where T : struct;
}

/// <summary>统一的伺服从机抽象。</summary>
public interface IServoAxis
{
    /// <summary>从机地址。EtherCAT：自动分配；Modbus：1~247。</summary>
    int SlaveAddr { get; }

    /// <summary>从机名称（可空）。</summary>
    string? SlaveName { get; }

    /// <summary>软件版本号（可空）。</summary>
    string? SoftwareVersion { get; }
}

/// <summary>
/// <see cref="EtherCATMaster"/> 的 <see cref="IServoMaster"/> 适配器。<br/>
/// 仅做方法转发，不引入额外状态。
/// </summary>
public sealed class EtherCATServoMasterAdapter(EtherCATMaster master) : IServoMaster
{
    private readonly EtherCATMaster _master = master ?? throw new ArgumentNullException(nameof(master));

    /// <summary>暴露原始 EtherCAT 主站，便于 EtherCAT 专属页面（EEPROM、固件下载）继续使用。</summary>
    public EtherCATMaster Underlying => _master;

    public bool TryReadSDO<T>(int slaveAddr, ushort index, byte subIndex, out T value) where T : struct
        => _master.TryReadSDO(slaveAddr, index, subIndex, out value);

    public bool TryWriteSDO<T>(int slaveAddr, ushort index, byte subIndex, T value) where T : struct
        => _master.TryWriteSDO(slaveAddr, index, subIndex, value);

    public T ReadSDO<T>(int slaveAddr, int index, int subIndex) where T : struct
        => _master.ReadSDO<T>(slaveAddr, index, subIndex);
}

/// <summary>
/// <see cref="EtherCATSlave_CiA402"/> 的 <see cref="IServoAxis"/> 适配器。
/// </summary>
public sealed class EtherCATServoAxisAdapter(EtherCATSlave_CiA402 axis) : IServoAxis
{
    private readonly EtherCATSlave_CiA402 _axis = axis ?? throw new ArgumentNullException(nameof(axis));

    /// <summary>暴露原始 EtherCAT 从机。</summary>
    public EtherCATSlave_CiA402 Underlying => _axis;

    public int SlaveAddr => _axis.SlaveAddr;

    public string? SlaveName => _axis.SlaveName;

    public string? SoftwareVersion => null;
}
