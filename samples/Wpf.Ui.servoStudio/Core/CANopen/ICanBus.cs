// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;

namespace Core.CANopen;

/// <summary>
/// CANopen 标准比特率（CiA301）。
/// </summary>
public enum CanBitrate
{
    Br10k = 10_000,
    Br20k = 20_000,
    Br50k = 50_000,
    Br100k = 100_000,
    Br125k = 125_000,
    Br250k = 250_000,
    Br500k = 500_000,
    Br800k = 800_000,
    Br1000k = 1_000_000,
}

/// <summary>
/// 可插拔的 CAN 总线传输层。<br/>
/// 把硬件相关性集中在此接口背后，使 <see cref="CanOpenMaster"/> 与具体 USB-CAN 适配器解耦：
/// <list type="bullet">
///   <item><description><see cref="VirtualCanBus"/>: 内存回环，便于离线/单元测试。</description></item>
///   <item><description><see cref="SerialCanBus"/>: 通用 slcan (LAWICEL) 串口协议，兼容 CANable / USB-CAN-A 等常见适配器。</description></item>
///   <item><description>未来可继续派生：PCAN-Basic、Kvaser、ZLG 等。</description></item>
/// </list>
/// </summary>
public interface ICanBus : IDisposable
{
    /// <summary>总线是否处于已打开状态。</summary>
    bool IsOpen { get; }

    /// <summary>打开总线并设置比特率。已打开时会先 Close 再 Open。</summary>
    bool Open(CanBitrate bitrate);

    /// <summary>关闭总线。</summary>
    void Close();

    /// <summary>发送一帧 CAN 数据。失败返回 false。</summary>
    bool Send(CanFrame frame);

    /// <summary>阻塞接收一帧（含超时）。超时返回 false。</summary>
    bool TryReceive(int timeoutMs, out CanFrame frame);
}
