// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;

namespace Core.CANopen;

/// <summary>
/// 一个 CAN 数据帧。<br/>
/// 仅支持经典 CAN（11 位标准 ID + 最多 8 字节数据），不涉及 CAN-FD。
/// </summary>
public readonly struct CanFrame : IEquatable<CanFrame>
{
    /// <summary>11 位标准 ID (COB-ID)。</summary>
    public ushort Id { get; }

    /// <summary>有效数据长度 0~8。</summary>
    public byte Dlc { get; }

    /// <summary>数据负载（始终 8 字节，超出 Dlc 的部分为 0）。</summary>
    public byte[] Data { get; }

    public CanFrame(ushort id, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "经典 CAN 帧最多 8 字节数据");
        }

        Id = (ushort)(id & 0x07FF);
        Dlc = (byte)data.Length;
        Data = new byte[8];
        Buffer.BlockCopy(data, 0, Data, 0, data.Length);
    }

    public CanFrame(ushort id, byte dlc, byte[] data8)
    {
        ArgumentNullException.ThrowIfNull(data8);
        if (data8.Length != 8)
        {
            throw new ArgumentException("data8 必须为 8 字节缓冲", nameof(data8));
        }

        Id = (ushort)(id & 0x07FF);
        Dlc = Math.Min((byte)8, dlc);
        Data = new byte[8];
        Buffer.BlockCopy(data8, 0, Data, 0, 8);
    }

    /// <summary>构造一个 8 字节固定长度的 SDO 帧。</summary>
    public static CanFrame Sdo(ushort cobId, byte cs, ushort index, byte subIndex, uint data32 = 0)
    {
        var d = new byte[8];
        d[0] = cs;
        d[1] = (byte)(index & 0xFF);
        d[2] = (byte)((index >> 8) & 0xFF);
        d[3] = subIndex;
        d[4] = (byte)(data32 & 0xFF);
        d[5] = (byte)((data32 >> 8) & 0xFF);
        d[6] = (byte)((data32 >> 16) & 0xFF);
        d[7] = (byte)((data32 >> 24) & 0xFF);
        return new CanFrame(cobId, 8, d);
    }

    public bool Equals(CanFrame other) =>
        Id == other.Id && Dlc == other.Dlc &&
        Data.AsSpan(0, Dlc).SequenceEqual(other.Data.AsSpan(0, other.Dlc));

    public override bool Equals(object? obj) => obj is CanFrame f && Equals(f);

    public override int GetHashCode() => HashCode.Combine(Id, Dlc);

    public override string ToString()
    {
        var hex = Convert.ToHexString(Data, 0, Dlc);
        return $"0x{Id:X3} [{Dlc}] {hex}";
    }

    public static bool operator ==(CanFrame a, CanFrame b) => a.Equals(b);
    public static bool operator !=(CanFrame a, CanFrame b) => !a.Equals(b);
}
