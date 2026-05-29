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
/// 基于 <b>ControlCAN.dll</b> 的 <see cref="ICanBus"/> 实现。<br/>
/// 这一 API 是国内 CAN 卡事实上的"通用接口"，下列设备均使用同一份 ControlCAN.dll：
/// <list type="bullet">
///   <item>周立功 ZLG <c>USBCAN-I / USBCAN-II / USBCAN-2A / PCI-9810 / PCI-9820</c></item>
///   <item>创芯科技 <c>CANalyst-II</c>（"CAN II" 兼容卡）</item>
///   <item>致远电子 <c>USBCAN-EU/2EU</c></item>
///   <item>大量国产 USB-CAN（USBCAN-OEM 系列）</item>
/// </list>
/// 用户只需把厂家提供的 <c>ControlCAN.dll</c>（x64）放到工程 <c>native/can/controlcan/</c>
/// 或工程输出目录即可，无需修改任何代码。
/// </summary>
public sealed class ControlCanBus : ICanBus
{
    /// <summary>设备类型号（默认 4 = USBCAN2 / CANalyst-II）。</summary>
    public uint DeviceType { get; set; } = ControlCanNative.VCI_USBCAN2;

    /// <summary>设备索引（同一类型设备的第几台，从 0 开始）。</summary>
    public uint DeviceIndex { get; set; }

    /// <summary>CAN 通道索引（0/1，USBCAN-II 双通道时）。</summary>
    public uint CanIndex { get; set; }

    public bool IsOpen { get; private set; }

    private bool _disposed;
    private bool _deviceOpened;

    public ControlCanBus() { }

    public ControlCanBus(uint deviceType, uint deviceIndex, uint canIndex)
    {
        DeviceType = deviceType;
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

        if (!TryGetTiming(bitrate, out byte t0, out byte t1))
        {
            return false;
        }

        try
        {
            uint sts = ControlCanNative.VCI_OpenDevice(DeviceType, DeviceIndex, 0);
            if (sts != ControlCanNative.STATUS_OK)
            {
                Debug.WriteLine($"VCI_OpenDevice failed (type={DeviceType}, idx={DeviceIndex})");
                return false;
            }
            _deviceOpened = true;

            var cfg = new ControlCanNative.VCI_INIT_CONFIG
            {
                AccCode = 0x00000000,
                AccMask = 0xFFFFFFFF,
                Reserved = 0,
                Filter = 1, // 单滤波，全部接收
                Timing0 = t0,
                Timing1 = t1,
                Mode = 0,   // 0 = 正常工作模式
            };

            sts = ControlCanNative.VCI_InitCAN(DeviceType, DeviceIndex, CanIndex, ref cfg);
            if (sts != ControlCanNative.STATUS_OK)
            {
                return false;
            }

            sts = ControlCanNative.VCI_StartCAN(DeviceType, DeviceIndex, CanIndex);
            if (sts != ControlCanNative.STATUS_OK)
            {
                return false;
            }

            IsOpen = true;
            return true;
        }
        catch (DllNotFoundException ex)
        {
            Debug.WriteLine($"ControlCAN.dll 未找到: {ex.Message}");
            CloseInternal();
            return false;
        }
    }

    public void Close()
    {
        if (!_deviceOpened && !IsOpen)
        {
            return;
        }

        CloseInternal();
    }

    private void CloseInternal()
    {
        try
        {
            if (IsOpen)
            {
                _ = ControlCanNative.VCI_ResetCAN(DeviceType, DeviceIndex, CanIndex);
            }

            if (_deviceOpened)
            {
                _ = ControlCanNative.VCI_CloseDevice(DeviceType, DeviceIndex);
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

        var obj = new ControlCanNative.VCI_CAN_OBJ
        {
            ID = frame.Id,
            TimeStamp = 0,
            TimeFlag = 0,
            SendType = 0,         // 0=正常发送
            RemoteFlag = 0,
            ExternFlag = 0,
            DataLen = frame.Dlc,
            Data = new byte[8],
            Reserved = new byte[3],
        };
        Buffer.BlockCopy(frame.Data, 0, obj.Data, 0, 8);

        try
        {
            uint n = ControlCanNative.VCI_Transmit(DeviceType, DeviceIndex, CanIndex, ref obj, 1);
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
        var rx = new ControlCanNative.VCI_CAN_OBJ[1];
        while (true)
        {
            try
            {
                uint waitMs = (uint)Math.Min(int.MaxValue, Math.Max(0, deadline - Environment.TickCount64));
                uint n = ControlCanNative.VCI_Receive(DeviceType, DeviceIndex, CanIndex, rx, 1, waitMs);
                if (n is > 0 and not 0xFFFFFFFF)
                {
                    if (rx[0].ExternFlag != 0 || rx[0].RemoteFlag != 0)
                    {
                        continue; // 跳过扩展帧/远程帧
                    }

                    var d = new byte[8];
                    Buffer.BlockCopy(rx[0].Data, 0, d, 0, 8);
                    frame = new CanFrame((ushort)(rx[0].ID & 0x7FF), (byte)Math.Min(8, (int)rx[0].DataLen), d);
                    return true;
                }
                if (n == 0xFFFFFFFF)
                {
                    return false; // 内部错误
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

    /// <summary>SJA1000 经典 BTR0/BTR1 寄存器（16 MHz 晶振、ControlCAN 默认）。</summary>
    private static bool TryGetTiming(CanBitrate br, out byte t0, out byte t1)
    {
        (t0, t1) = br switch
        {
            CanBitrate.Br10k   => ((byte)0x31, (byte)0x1C),
            CanBitrate.Br20k   => ((byte)0x18, (byte)0x1C),
            CanBitrate.Br50k   => ((byte)0x09, (byte)0x1C),
            CanBitrate.Br100k  => ((byte)0x04, (byte)0x1C),
            CanBitrate.Br125k  => ((byte)0x03, (byte)0x1C),
            CanBitrate.Br250k  => ((byte)0x01, (byte)0x1C),
            CanBitrate.Br500k  => ((byte)0x00, (byte)0x1C),
            CanBitrate.Br800k  => ((byte)0x00, (byte)0x16),
            CanBitrate.Br1000k => ((byte)0x00, (byte)0x14),
            _ => ((byte)0x00, (byte)0x1C),
        };
        return true;
    }

    /// <summary>探测特定 ControlCAN 设备是否插入（不打开 CAN 通道）。</summary>
    public static bool ProbeDevice(uint deviceType, uint deviceIndex)
    {
        try
        {
            uint sts = ControlCanNative.VCI_OpenDevice(deviceType, deviceIndex, 0);
            if (sts != ControlCanNative.STATUS_OK)
            {
                return false;
            }

            _ = ControlCanNative.VCI_CloseDevice(deviceType, deviceIndex);
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch { return false; }
    }
}

/// <summary>ControlCAN.dll 原生 P/Invoke。</summary>
internal static class ControlCanNative
{
    public const string Dll = "ControlCAN.dll";

    public const uint STATUS_OK = 1;

    // 设备类型号（与 ZLG/创芯/致远手册一致）
    public const uint VCI_PCI5121     = 1;
    public const uint VCI_PCI9810     = 2;
    public const uint VCI_USBCAN1     = 3;
    public const uint VCI_USBCAN2     = 4;   // USBCAN-II / CANalyst-II / "CAN II"
    public const uint VCI_USBCAN_E_U  = 20;
    public const uint VCI_USBCAN_2E_U = 21;

    [StructLayout(LayoutKind.Sequential)]
    public struct VCI_INIT_CONFIG
    {
        public uint AccCode;
        public uint AccMask;
        public uint Reserved;
        public byte Filter;
        public byte Timing0;
        public byte Timing1;
        public byte Mode;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct VCI_CAN_OBJ
    {
        public uint ID;
        public uint TimeStamp;
        public byte TimeFlag;
        public byte SendType;
        public byte RemoteFlag;
        public byte ExternFlag;
        public byte DataLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] Data;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public byte[] Reserved;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VCI_OpenDevice(uint deviceType, uint deviceIndex, uint reserved);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VCI_CloseDevice(uint deviceType, uint deviceIndex);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VCI_InitCAN(uint deviceType, uint deviceIndex, uint canIndex, ref VCI_INIT_CONFIG cfg);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VCI_StartCAN(uint deviceType, uint deviceIndex, uint canIndex);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VCI_ResetCAN(uint deviceType, uint deviceIndex, uint canIndex);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VCI_Transmit(uint deviceType, uint deviceIndex, uint canIndex,
        ref VCI_CAN_OBJ frameSingle, uint length);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VCI_Receive(uint deviceType, uint deviceIndex, uint canIndex,
        [Out] VCI_CAN_OBJ[] frames, uint length, uint waitTimeMs);
}
