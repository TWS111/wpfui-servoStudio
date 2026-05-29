// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;

namespace Core.Usb;

/// <summary>
/// 可插拔的 USB 传输层。<br/>
/// 把宿主侧具体实现（WinUSB / LibUsb / Cypress CyUSB / etc.）封装在此接口背后，
/// 使上层 <see cref="UsbMaster"/> 与具体驱动解耦，对应 CANopen 子系统中的 <c>ICanBus</c>。<br/>
/// 已知派生：
/// <list type="bullet">
///   <item><description><see cref="VirtualUsbBus"/>：内存回环，用于离线 / 单元测试。</description></item>
///   <item><description><see cref="UsbBulkBus"/>：真实 USB Bulk 端点实现（骨架，待对接 P/Invoke）。</description></item>
/// </list>
/// </summary>
public interface IUsbBus : IDisposable
{
    /// <summary>总线是否已打开（已枚举到设备并准备好端点）。</summary>
    bool IsOpen { get; }

    /// <summary>
    /// 枚举并打开匹配 (<paramref name="vendorId"/>, <paramref name="productId"/>) 的设备。<br/>
    /// 已打开时实现应先 <see cref="Close"/> 再重新枚举。
    /// </summary>
    bool Open(ushort vendorId, ushort productId);

    /// <summary>关闭设备句柄 / 释放端点。</summary>
    void Close();

    /// <summary>
    /// 在 OUT 端点上发送一个 USB 报文。<br/>
    /// 内部由实现负责分片（若负载超过 <see cref="UsbDefaults.MaxPacketSize"/>），调用方传入完整应用层报文即可。<br/>
    /// 失败返回 false；可结合 <see cref="UsbMaster"/> 上层做超时/重试。
    /// </summary>
    bool Send(UsbPacket packet);

    /// <summary>
    /// 阻塞接收一个 USB 报文（含超时）。超时返回 false。<br/>
    /// 由实现完成端点轮询 / 异步 IO 等待 / 分片重组 → 还原成应用层 <see cref="UsbPacket"/>。
    /// </summary>
    bool TryReceive(int timeoutMs, out UsbPacket packet);
}
