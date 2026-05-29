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
/// 广成 / 德州 Toomoss USB2XXX (USB2CAN / USB2CANFD) 系列 SDK <b>usb_device.dll</b> 的
/// <see cref="ICanBus"/> 实现。<br/>
/// 兼容 USB-CAN / USB2CAN / USB2CANFD-X1/X2 / USB2XXX-CAN 等设备。<br/>
/// DLL 由 Toomoss 官方提供，需把 <c>usb_device.dll</c>（x64）放到工程
/// <c>native/can/toomoss/</c>。
/// </summary>
/// <remarks>
/// SDK 用法非常简洁：先 <c>USB_ScanDevice</c> 枚举，再 <c>USB_OpenDevice</c>，
/// 然后 <c>CAN_Init/CAN_StartCAN</c> 启动通道，<c>CAN_SendMsg/CAN_GetMsg</c> 收发。
/// </remarks>
public sealed class ToomossCanBus : ICanBus
{
    /// <summary>设备索引（USB 枚举顺序，从 0 开始）。</summary>
    public ushort DeviceIndex { get; set; }

    /// <summary>CAN 通道（0/1）。</summary>
    public byte CanIndex { get; set; }

    public bool IsOpen { get; private set; }

    private bool _disposed;
    private bool _deviceOpened;

    public ToomossCanBus() { }

    public ToomossCanBus(ushort deviceIndex, byte canIndex)
    {
        DeviceIndex = deviceIndex;
        CanIndex = canIndex;
    }

    public bool Open(CanBitrate bitrate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsOpen)
        {
            Close();
        }

        try
        {
            // 1) 扫描 + 打开
            int found = ToomossNative.USB_ScanDevice(IntPtr.Zero);
            if (found <= 0) { Debug.WriteLine("Toomoss: 未扫描到设备"); return false; }
            if (DeviceIndex >= found)
            {
                return false;
            }

            if (ToomossNative.USB_OpenDevice(DeviceIndex) == 0)
            {
                return false;
            }

            _deviceOpened = true;

            // 2) 配置 CAN
            var cfg = new ToomossNative.CAN_INIT_CONFIG
            {
                CAN_Mode = 0,
                CAN_BRP = 0,
                CAN_SJW = 0,
                CAN_BS1 = 0,
                CAN_BS2 = 0,
                CAN_ABOM = 1,
                CAN_NART = 0,
                CAN_RFLM = 0,
                CAN_TXFP = 1,
                CAN_RELAY = 0,
            };
            FillTiming(bitrate, ref cfg);

            if (ToomossNative.CAN_Init(DeviceIndex, CanIndex, ref cfg) == 0)
            {
                return false;
            }

            if (ToomossNative.CAN_StartGetMsg(DeviceIndex, CanIndex) == 0) { /* 部分版本可省略 */ }

            IsOpen = true;
            return true;
        }
        catch (DllNotFoundException ex)
        {
            Debug.WriteLine($"usb_device.dll 未找到: {ex.Message}");
            Close();
            return false;
        }
    }

    public void Close()
    {
        try
        {
            if (IsOpen)
            {
                _ = ToomossNative.CAN_StopGetMsg(DeviceIndex, CanIndex);
            }

            if (_deviceOpened)
            {
                _ = ToomossNative.USB_CloseDevice(DeviceIndex);
            }
        }
        catch { /* ignore */ }
        finally
        {
            IsOpen = false;
            _deviceOpened = false;
        }
    }

    public bool Send(CanFrame frame)
    {
        if (!IsOpen)
        {
            return false;
        }

        var msg = new ToomossNative.CAN_MSG
        {
            ID = frame.Id,
            TimeStamp = 0,
            FrameType = 0, // 数据帧
            DataLen = frame.Dlc,
            Data = new byte[8],
            ExternFlag = 0,
            RemoteFlag = 0,
            BusSatus = 0,
            ErrSatus = 0,
            TECounter = 0,
            RECounter = 0,
        };
        Buffer.BlockCopy(frame.Data, 0, msg.Data, 0, 8);
        try
        {
            int n = ToomossNative.CAN_SendMsg(DeviceIndex, CanIndex, ref msg, 1);
            return n == 1;
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
        var buf = new ToomossNative.CAN_MSG[1];
        while (true)
        {
            try
            {
                int n = ToomossNative.CAN_GetMsg(DeviceIndex, CanIndex, buf);
                if (n > 0)
                {
                    if (buf[0].ExternFlag != 0 || buf[0].RemoteFlag != 0)
                    {
                        continue;
                    }

                    var d = new byte[8];
                    Buffer.BlockCopy(buf[0].Data, 0, d, 0, 8);
                    frame = new CanFrame((ushort)(buf[0].ID & 0x7FF),
                        Math.Min((byte)8, buf[0].DataLen), d);
                    return true;
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

    /// <summary>使用 36 MHz 时钟换算 BRP/BS1/BS2（Toomoss 使用 STM32 bxCAN 风格定时）。</summary>
    private static void FillTiming(CanBitrate br, ref ToomossNative.CAN_INIT_CONFIG cfg)
    {
        // 采样点 ~80%（参照 Toomoss 官方示例）
        (ushort brp, byte bs1, byte bs2, byte sjw) = br switch
        {
            CanBitrate.Br10k   => ((ushort)300, (byte)11, (byte)0, (byte)0),
            CanBitrate.Br20k   => ((ushort)150, (byte)11, (byte)0, (byte)0),
            CanBitrate.Br50k   => ((ushort)60,  (byte)11, (byte)0, (byte)0),
            CanBitrate.Br100k  => ((ushort)30,  (byte)11, (byte)0, (byte)0),
            CanBitrate.Br125k  => ((ushort)24,  (byte)11, (byte)0, (byte)0),
            CanBitrate.Br250k  => ((ushort)12,  (byte)11, (byte)0, (byte)0),
            CanBitrate.Br500k  => ((ushort)6,   (byte)11, (byte)0, (byte)0),
            CanBitrate.Br800k  => ((ushort)3,   (byte)13, (byte)0, (byte)0),
            CanBitrate.Br1000k => ((ushort)3,   (byte)11, (byte)0, (byte)0),
            _ => ((ushort)6, (byte)11, (byte)0, (byte)0),
        };
        cfg.CAN_BRP = brp;
        cfg.CAN_BS1 = bs1;
        cfg.CAN_BS2 = bs2;
        cfg.CAN_SJW = sjw;
    }

    public static int ScanCount()
    {
        try { return Math.Max(0, ToomossNative.USB_ScanDevice(IntPtr.Zero)); }
        catch (DllNotFoundException) { return 0; }
        catch { return 0; }
    }
}

internal static class ToomossNative
{
    public const string Dll = "USB2XXX.dll";

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CAN_INIT_CONFIG
    {
        public uint CAN_Mode;
        public uint CAN_BRP;
        public byte CAN_SJW;
        public byte CAN_BS1;
        public byte CAN_BS2;
        public byte CAN_ABOM;
        public byte CAN_NART;
        public byte CAN_RFLM;
        public byte CAN_TXFP;
        public byte CAN_RELAY;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CAN_MSG
    {
        public uint ID;
        public uint TimeStamp;
        public byte FrameType;
        public byte DataLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] Data;
        public byte ExternFlag;
        public byte RemoteFlag;
        public byte BusSatus;
        public byte ErrSatus;
        public byte TECounter;
        public byte RECounter;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int USB_ScanDevice(IntPtr buffer);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int USB_OpenDevice(ushort deviceIndex);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int USB_CloseDevice(ushort deviceIndex);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int CAN_Init(ushort deviceIndex, byte canIndex, ref CAN_INIT_CONFIG cfg);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int CAN_StartGetMsg(ushort deviceIndex, byte canIndex);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int CAN_StopGetMsg(ushort deviceIndex, byte canIndex);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int CAN_SendMsg(ushort deviceIndex, byte canIndex, ref CAN_MSG msg, int len);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int CAN_GetMsg(ushort deviceIndex, byte canIndex, [Out] CAN_MSG[] buf);
}
