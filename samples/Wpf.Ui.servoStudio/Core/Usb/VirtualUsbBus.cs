// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Core.Usb;

/// <summary>
/// 进程内回环的 <see cref="IUsbBus"/> 实现：发送的 OUT 报文会被自动转换为 IN 方向投递回接收队列，<br/>
/// 用于离线开发 / 单元测试 / UI 联调，无需真实硬件。<br/>
/// 对应 CANopen 子系统中的 <c>VirtualCanBus</c>。
/// </summary>
public sealed class VirtualUsbBus : IUsbBus
{
    private readonly BlockingCollection<byte[]> _rx = new(new ConcurrentQueue<byte[]>());
    private bool _disposed;

    /// <summary>是否启用 OUT→IN 自动回环（默认 true）。关闭后 <see cref="Send"/> 不会回送报文。</summary>
    public bool LoopbackEnabled { get; set; } = true;

    public bool IsOpen { get; private set; }

    public bool Open(ushort vendorId, ushort productId)
    {
        IsOpen = true;
        return true;
    }

    public void Close()
    {
        IsOpen = false;
    }

    public bool Send(UsbPacket packet)
    {
        if (!IsOpen)
        {
            return false;
        }

        if (LoopbackEnabled && packet.Direction == UsbDirection.HostToDevice)
        {
            UsbPacket loopbackPacket = UsbPacket.In(packet.Channel, packet.Sequence, packet.Payload);
            _rx.Add(UsbPacketCodec.Serialize(loopbackPacket));
        }
        return true;
    }

    /// <summary>外部注入一帧（用于测试时模拟从机自发的 IN 报文）。</summary>
    public void InjectIncoming(UsbPacket packet)
    {
        if (!IsOpen)
        {
            return;
        }

        _rx.Add(UsbPacketCodec.Serialize(packet));
    }

    public bool TryReceive(int timeoutMs, out UsbPacket packet)
    {
        packet = default;
        if (!IsOpen)
        {
            return false;
        }

        try
        {
            return _rx.TryTake(out byte[]? rawBytes, timeoutMs, CancellationToken.None)
                && rawBytes is not null
                && UsbPacketCodec.TryDeserialize(rawBytes, UsbDirection.DeviceToHost, out packet);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsOpen = false;
        try { _rx.CompleteAdding(); } catch { /* ignore */ }
        try { _rx.Dispose(); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }
}
