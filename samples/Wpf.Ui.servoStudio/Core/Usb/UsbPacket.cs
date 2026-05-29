// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;

namespace Core.Usb;

/// <summary>
/// USB 协议栈的应用层报文。<br/>
/// 与 <c>CanFrame</c>（CANopen）平行，但 USB 没有 11 位 ID / 8 字节 DLC 的约束：
/// <list type="bullet">
///   <item><description><see cref="Channel"/> ：逻辑通道，用于分发到不同处理器（曲线拟合 / 自适应参数 / 高带宽遥测）。</description></item>
///   <item><description><see cref="Sequence"/> ：报文序号，用于分片重组 / 丢包检测（具体语义待部署）。</description></item>
///   <item><description><see cref="Payload"/> ：原始字节流，由具体业务码自行解码。</description></item>
/// </list>
/// 该结构为<b>不可变值类型</b>，复制廉价；<see cref="Payload"/> 引用同一字节数组，请勿就地修改。
/// </summary>
public readonly struct UsbPacket : IEquatable<UsbPacket>
{
    /// <summary>逻辑通道。</summary>
    public UsbChannel Channel { get; }

    /// <summary>报文序号（0~65535 循环）。</summary>
    public ushort Sequence { get; }

    /// <summary>报文方向。</summary>
    public UsbDirection Direction { get; }

    /// <summary>原始字节负载。</summary>
    public byte[] Payload { get; }

    public UsbPacket(UsbChannel channel, ushort sequence, UsbDirection direction, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Channel = channel;
        Sequence = sequence;
        Direction = direction;
        Payload = payload;
    }

    /// <summary>构造一个 Host→Device OUT 报文。</summary>
    public static UsbPacket Out(UsbChannel channel, ushort sequence, byte[] payload)
        => new(channel, sequence, UsbDirection.HostToDevice, payload);

    /// <summary>构造一个 Device→Host IN 报文。</summary>
    public static UsbPacket In(UsbChannel channel, ushort sequence, byte[] payload)
        => new(channel, sequence, UsbDirection.DeviceToHost, payload);

    public bool Equals(UsbPacket other)
        => Channel == other.Channel
        && Sequence == other.Sequence
        && Direction == other.Direction
        && ReferenceEquals(Payload, other.Payload);

    public override bool Equals(object? obj) => obj is UsbPacket p && Equals(p);

    public override int GetHashCode() => HashCode.Combine((ushort)Channel, Sequence, (byte)Direction);

    public override string ToString()
        => $"USB {Direction} ch=0x{(ushort)Channel:X4} seq={Sequence} len={Payload?.Length ?? 0}";

    public static bool operator ==(UsbPacket a, UsbPacket b) => a.Equals(b);

    public static bool operator !=(UsbPacket a, UsbPacket b) => !a.Equals(b);
}
