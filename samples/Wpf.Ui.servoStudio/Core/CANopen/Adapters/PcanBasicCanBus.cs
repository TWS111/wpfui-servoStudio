// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Core.CANopen.Adapters;

/// <summary>
/// PEAK <b>PCAN-Basic</b> (PCANBasic.dll) 的 <see cref="ICanBus"/> 实现。<br/>
/// 兼容 PCAN-USB / PCAN-USB Pro / PCAN-PCI / PCAN-PCIe 等所有 PEAK 适配器。<br/>
/// DLL 由 PEAK-System 官方提供，需把 <c>PCANBasic.dll</c>（x64）放到工程
/// <c>native/can/peak/</c> 目录或系统 PATH 中（项目通过 csproj 自动 Copy 到输出目录）。
/// </summary>
/// <remarks>
/// 仅实现了经典 CAN（11-bit 标准帧）；CiA301/CANopen 全部使用 11-bit ID，无需 CAN-FD/29-bit。
/// </remarks>
public sealed class PcanBasicCanBus : ICanBus
{
    /// <summary>常用 PCAN-USB 通道句柄（USBBUS1..USBBUS16）。</summary>
    public ushort Channel { get; set; } = PcanNative.PCAN_USBBUS1;

    public bool IsOpen { get; private set; }

    private bool _disposed;

    public PcanBasicCanBus() { }

    public PcanBasicCanBus(ushort channel) { Channel = channel; }

    public bool Open(CanBitrate bitrate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsOpen)
        {
            Close();
        }

        ushort btr0btr1 = bitrate switch
        {
            CanBitrate.Br10k   => PcanNative.PCAN_BAUD_10K,
            CanBitrate.Br20k   => PcanNative.PCAN_BAUD_20K,
            CanBitrate.Br50k   => PcanNative.PCAN_BAUD_50K,
            CanBitrate.Br100k  => PcanNative.PCAN_BAUD_100K,
            CanBitrate.Br125k  => PcanNative.PCAN_BAUD_125K,
            CanBitrate.Br250k  => PcanNative.PCAN_BAUD_250K,
            CanBitrate.Br500k  => PcanNative.PCAN_BAUD_500K,
            CanBitrate.Br800k  => PcanNative.PCAN_BAUD_800K,
            CanBitrate.Br1000k => PcanNative.PCAN_BAUD_1M,
            _ => PcanNative.PCAN_BAUD_500K,
        };

        try
        {
            uint sts = PcanNative.CAN_Initialize(Channel, btr0btr1, 0, 0, 0);
            if (sts != PcanNative.PCAN_ERROR_OK)
            {
                Debug.WriteLine($"PCAN_Initialize failed: 0x{sts:X8}");
                return false;
            }
            IsOpen = true;
            return true;
        }
        catch (DllNotFoundException ex)
        {
            Debug.WriteLine($"PCANBasic.dll 未找到: {ex.Message}");
            return false;
        }
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        try { _ = PcanNative.CAN_Uninitialize(Channel); } catch { /* ignore */ }
        IsOpen = false;
    }

    public bool Send(CanFrame frame)
    {
        if (!IsOpen)
        {
            return false;
        }

        var msg = new PcanNative.TPCANMsg
        {
            ID = frame.Id,
            MSGTYPE = PcanNative.PCAN_MESSAGE_STANDARD,
            LEN = frame.Dlc,
            DATA = new byte[8],
        };
        Buffer.BlockCopy(frame.Data, 0, msg.DATA, 0, 8);
        try
        {
            uint sts = PcanNative.CAN_Write(Channel, ref msg);
            return sts == PcanNative.PCAN_ERROR_OK;
        }
        catch (DllNotFoundException) { return false; }
    }

    public bool TryReceive(int timeoutMs, out CanFrame frame)
    {
        frame = default;
        if (!IsOpen)
        {
            return false;
        }

        long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (true)
        {
            try
            {
                uint sts = PcanNative.CAN_Read(Channel, out PcanNative.TPCANMsg msg, out _);
                if (sts == PcanNative.PCAN_ERROR_OK)
                {
                    // 仅接受标准 11-bit 数据帧
                    if ((msg.MSGTYPE & PcanNative.PCAN_MESSAGE_EXTENDED) != 0)
                    {
                        continue;
                    }

                    if ((msg.MSGTYPE & PcanNative.PCAN_MESSAGE_RTR) != 0)
                    {
                        continue;
                    }

                    var d = new byte[8];
                    Buffer.BlockCopy(msg.DATA, 0, d, 0, 8);
                    frame = new CanFrame((ushort)(msg.ID & 0x7FF), msg.LEN, d);
                    return true;
                }
                if (sts != PcanNative.PCAN_ERROR_QRCVEMPTY)
                {
                    return false;
                }
            }
            catch (DllNotFoundException) { return false; }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 探测一个 PCAN 通道是否物理连接（不修改通道波特率，仅查询设备状态）。
    /// </summary>
    public static bool ProbeChannel(ushort channel)
    {
        try
        {
            // 通过 GetValue(CHANNEL_CONDITION) 判断设备是否插入
            var buf = new byte[4];
            uint sts = PcanNative.CAN_GetValue(channel,
                PcanNative.PCAN_CHANNEL_CONDITION, buf, 4);
            if (sts != PcanNative.PCAN_ERROR_OK)
            {
                return false;
            }

            uint cond = BitConverter.ToUInt32(buf, 0);
            // PCAN_CHANNEL_AVAILABLE = 0x01, OCCUPIED = 0x02, PCAN_BUS_PNP = 0x04
            return (cond & 0x07) != 0;
        }
        catch (DllNotFoundException) { return false; }
        catch { return false; }
    }
}

/// <summary>
/// PCAN-Basic 原生 P/Invoke。完全与 PEAK 官方 <c>PCANBasic.cs</c> 对位的最小子集。
/// </summary>
internal static class PcanNative
{
    public const string Dll = "PCANBasic.dll";

    // PCAN handles (USB)
    public const ushort PCAN_USBBUS1  = 0x51;
    public const ushort PCAN_USBBUS2  = 0x52;
    public const ushort PCAN_USBBUS3  = 0x53;
    public const ushort PCAN_USBBUS4  = 0x54;
    public const ushort PCAN_USBBUS5  = 0x55;
    public const ushort PCAN_USBBUS6  = 0x56;
    public const ushort PCAN_USBBUS7  = 0x57;
    public const ushort PCAN_USBBUS8  = 0x58;
    public const ushort PCAN_USBBUS9  = 0x509;
    public const ushort PCAN_USBBUS10 = 0x50A;
    public const ushort PCAN_USBBUS11 = 0x50B;
    public const ushort PCAN_USBBUS12 = 0x50C;
    public const ushort PCAN_USBBUS13 = 0x50D;
    public const ushort PCAN_USBBUS14 = 0x50E;
    public const ushort PCAN_USBBUS15 = 0x50F;
    public const ushort PCAN_USBBUS16 = 0x510;

    // Baud rates (BTR0/BTR1)
    public const ushort PCAN_BAUD_1M   = 0x0014;
    public const ushort PCAN_BAUD_800K = 0x0016;
    public const ushort PCAN_BAUD_500K = 0x001C;
    public const ushort PCAN_BAUD_250K = 0x011C;
    public const ushort PCAN_BAUD_125K = 0x031C;
    public const ushort PCAN_BAUD_100K = 0x432F;
    public const ushort PCAN_BAUD_50K  = 0x472F;
    public const ushort PCAN_BAUD_20K  = 0x532F;
    public const ushort PCAN_BAUD_10K  = 0x672F;

    // Message types
    public const byte PCAN_MESSAGE_STANDARD = 0x00;
    public const byte PCAN_MESSAGE_RTR      = 0x01;
    public const byte PCAN_MESSAGE_EXTENDED = 0x02;

    // Status
    public const uint PCAN_ERROR_OK         = 0x00000;
    public const uint PCAN_ERROR_QRCVEMPTY  = 0x00020;

    // Parameters
    public const byte PCAN_CHANNEL_CONDITION = 0x05;

    [StructLayout(LayoutKind.Sequential)]
    public struct TPCANMsg
    {
        public uint ID;
        public byte MSGTYPE;
        public byte LEN;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] DATA;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TPCANTimestamp
    {
        public uint millis;
        public ushort millis_overflow;
        public ushort micros;
    }

    [DllImport(Dll, EntryPoint = "CAN_Initialize")]
    public static extern uint CAN_Initialize(ushort channel, ushort btr0btr1, byte hwType, uint ioPort, ushort interrupt);

    [DllImport(Dll, EntryPoint = "CAN_Uninitialize")]
    public static extern uint CAN_Uninitialize(ushort channel);

    [DllImport(Dll, EntryPoint = "CAN_Write")]
    public static extern uint CAN_Write(ushort channel, ref TPCANMsg msg);

    [DllImport(Dll, EntryPoint = "CAN_Read")]
    public static extern uint CAN_Read(ushort channel, out TPCANMsg msg, out TPCANTimestamp ts);

    [DllImport(Dll, EntryPoint = "CAN_GetValue")]
    public static extern uint CAN_GetValue(ushort channel, byte parameter, [Out] byte[] buffer, uint bufferLen);
}
