// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Core.CANopen.Adapters;

/// <summary>
/// 周立功 ZLG 新版统一 SDK <b>zlgcan.dll</b> 的 <see cref="ICanBus"/> 实现。<br/>
/// 兼容 USBCAN-E-U / USBCAN-2E-U / USBCANFD-MINI / USBCANFD-100U / PCIe-9221 等新一代设备。<br/>
/// DLL 由 ZLG 官方提供（<c>https://manual.zlg.cn/web/#/188</c>），需把 <c>zlgcan.dll</c> 与配套
/// <c>kerneldlls/</c> 目录放到 <c>native/can/zlgcan/</c>，工程会通过 csproj Copy 到输出目录。
/// </summary>
public sealed class ZlgCanBus : ICanBus
{
    /// <summary>设备类型号（默认 USBCAN_E_U）。</summary>
    public uint DeviceType { get; set; } = ZlgcanNative.ZCAN_USBCAN_E_U;

    /// <summary>设备索引（同型号设备多台时，从 0 开始）。</summary>
    public uint DeviceIndex { get; set; }

    /// <summary>通道索引（0/1，多通道时）。</summary>
    public uint Channel { get; set; }

    public bool IsOpen { get; private set; }

    private IntPtr _device = IntPtr.Zero;
    private IntPtr _channel = IntPtr.Zero;
    private bool _disposed;

    public ZlgCanBus() { }

    public ZlgCanBus(uint deviceType, uint deviceIndex, uint channel)
    {
        DeviceType = deviceType;
        DeviceIndex = deviceIndex;
        Channel = channel;
    }

    public bool Open(CanBitrate bitrate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsOpen)
        {
            Close();
        }

        // 见 ProbeDevice 注释：zlgcan 后端按 CWD 相对路径加载，必须切换 CWD。
        string? originalCwd = null;
        string? zlgDir = TryGetZlgcanDir();
        if (zlgDir != null && Directory.Exists(zlgDir))
        {
            try { originalCwd = Directory.GetCurrentDirectory(); Directory.SetCurrentDirectory(zlgDir); }
            catch { originalCwd = null; }
        }

        try
        {
            _device = ZlgcanNative.ZCAN_OpenDevice(DeviceType, DeviceIndex, 0);
            if (_device == IntPtr.Zero)
            {
                return false;
            }

            if (!TryGetTiming(bitrate, out byte timing0, out byte timing1))
            {
                return false;
            }

            // 通过属性接口设置波特率（适用于 USBCAN-E-U/2E-U、USBCANFD 等新固件）。
            // 某些 zlgcan.dll 版本导出 ZCAN_SetValue 而非 SetValue；若属性接口不可用，
            // 下方 Timing0/Timing1 仍作为兜底配置，避免连接阶段直接抛异常。
            try
            {
                IntPtr prop = ZlgcanNative.GetIProperty(_device);
                if (prop != IntPtr.Zero)
                {
                    string baudPath = $"{Channel}/baud_rate";
                    string baudVal = ((int)bitrate).ToString();
                    _ = ZlgcanNative.SetValue(prop, baudPath, baudVal);
                    _ = ZlgcanNative.ReleaseIProperty(prop);
                }
            }
            catch (EntryPointNotFoundException ex)
            {
                Debug.WriteLine($"zlgcan 属性接口不可用，改用 Timing0/Timing1: {ex.Message}");
            }

            var cfg = new ZlgcanNative.ZCAN_CHANNEL_INIT_CONFIG
            {
                CanType = 0, // 经典 CAN
                Cfg = new ZlgcanNative.ZCAN_CHANNEL_INIT_CONFIG_UNION
                {
                    Can = new ZlgcanNative.ZCAN_CHANNEL_CAN_INIT_CONFIG
                    {
                        AccCode = 0,
                        AccMask = 0xFFFFFFFF,
                        Reserved = 0,
                        Filter = 1,
                        Timing0 = timing0,
                        Timing1 = timing1,
                        Mode = 0,
                    },
                },
            };

            _channel = ZlgcanNative.ZCAN_InitCAN(_device, Channel, ref cfg);
            if (_channel == IntPtr.Zero) { Close(); return false; }

            if (ZlgcanNative.ZCAN_StartCAN(_channel) != 1) { Close(); return false; }

            IsOpen = true;
            return true;
        }
        catch (DllNotFoundException ex)
        {
            Debug.WriteLine($"zlgcan.dll 未找到: {ex.Message}");
            Close();
            return false;
        }
        finally
        {
            if (originalCwd != null)
            {
                try { Directory.SetCurrentDirectory(originalCwd); } catch { }
            }
        }
    }

    public void Close()
    {
        try
        {
            if (_channel != IntPtr.Zero)
            {
                _ = ZlgcanNative.ZCAN_ResetCAN(_channel);
            }

            if (_device != IntPtr.Zero)
            {
                _ = ZlgcanNative.ZCAN_CloseDevice(_device);
            }
        }
        catch { /* ignore */ }
        finally
        {
            _channel = IntPtr.Zero;
            _device = IntPtr.Zero;
            IsOpen = false;
        }
    }

    public bool Send(CanFrame frame)
    {
        if (!IsOpen)
        {
            return false;
        }

        var msg = new ZlgcanNative.ZCAN_Transmit_Data
        {
            Frame = new ZlgcanNative.can_frame
            {
                CanId = frame.Id,
                CanDlc = frame.Dlc,
                __pad = 0, __res0 = 0, __res1 = 0,
                Data = new byte[8],
            },
            TransmitType = 0,
        };
        Buffer.BlockCopy(frame.Data, 0, msg.Frame.Data, 0, 8);
        try
        {
            uint n = ZlgcanNative.ZCAN_Transmit(_channel, ref msg, 1);
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
        var rx = new ZlgcanNative.ZCAN_Receive_Data[1];
        while (true)
        {
            try
            {
                int waitMs = (int)Math.Max(0, deadline - Environment.TickCount64);
                uint n = ZlgcanNative.ZCAN_Receive(_channel, rx, 1, waitMs);
                if (n > 0)
                {
                    uint id = rx[0].Frame.CanId;
                    if ((id & 0x80000000u) != 0)
                    {
                        continue; // 扩展帧
                    }

                    if ((id & 0x40000000u) != 0)
                    {
                        continue; // RTR
                    }

                    var d = new byte[8];
                    Buffer.BlockCopy(rx[0].Frame.Data, 0, d, 0, 8);
                    frame = new CanFrame((ushort)(id & 0x7FF), Math.Min((byte)8, rx[0].Frame.CanDlc), d);
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

    /// <summary>SJA1000 经典 BTR0/BTR1 寄存器（16 MHz 晶振）。属性接口不可用时作为兜底。</summary>
    private static bool TryGetTiming(CanBitrate bitrate, out byte timing0, out byte timing1)
    {
        (timing0, timing1) = bitrate switch
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

    /// <summary>探测设备插入（不打开通道）。</summary>
    public static bool ProbeDevice(uint deviceType, uint deviceIndex)
        => ProbeDeviceDetailed(deviceType, deviceIndex).IsSuccess;

    /// <summary>探测设备插入，并返回 ZLG 原生调用的详细结果。</summary>
    internal static ZlgProbeResult ProbeDeviceDetailed(uint deviceType, uint deviceIndex)
    {
        // zlgcan.dll 内部通过相对路径 "kerneldlls\xxx.dll" 加载后端，
        // 该路径以当前进程工作目录（CWD）为基准。WPF 应用启动后 CWD 不一定是
        // zlgcan.dll 所在目录，因此 LoadLibrary 找不到后端 → ZCAN_OpenDevice 返回 0。
        // 这里临时切换 CWD 到 zlgcan.dll 所在目录，调用结束后立即恢复。
        string? originalCwd = null;
        string? zlgDir = TryGetZlgcanDir();
        string? loadedDllPath = TryGetLoadedZlgcanPath();
        string? effectiveCwd = null;
        try
        {
            if (zlgDir != null && Directory.Exists(zlgDir))
            {
                originalCwd = Directory.GetCurrentDirectory();
                try { Directory.SetCurrentDirectory(zlgDir); } catch { originalCwd = null; }
            }
            effectiveCwd = Directory.GetCurrentDirectory();

            IntPtr h = ZlgcanNative.ZCAN_OpenDevice(deviceType, deviceIndex, 0);
            int openLastError = Marshal.GetLastWin32Error();
            if (h == IntPtr.Zero)
            {
                return new ZlgProbeResult(
                    deviceType,
                    deviceIndex,
                    zlgDir,
                    loadedDllPath,
                    originalCwd,
                    effectiveCwd,
                    h,
                    openLastError,
                        null,
                        null,
                        null,
                        null,
                        null,
                    null,
                    null,
                    null,
                    null);
            }

                    var deviceInfo = new ZlgcanNative.ZCAN_DEVICE_INFO
                    {
                    Reserved = new ushort[4],
                    };
                    uint infoResult = ZlgcanNative.ZCAN_GetDeviceInf(h, ref deviceInfo);
                    int infoLastError = Marshal.GetLastWin32Error();

            uint closeResult = ZlgcanNative.ZCAN_CloseDevice(h);
            int closeLastError = Marshal.GetLastWin32Error();
            return new ZlgProbeResult(
                deviceType,
                deviceIndex,
                zlgDir,
                loadedDllPath,
                originalCwd,
                effectiveCwd,
                h,
                openLastError,
                infoResult,
                infoLastError,
                deviceInfo.CanNum,
                TrimNativeString(deviceInfo.SerialNum),
                TrimNativeString(deviceInfo.HwType),
                closeResult,
                closeLastError,
                null,
                null);
        }
        catch (Exception ex)
        {
            return new ZlgProbeResult(
                deviceType,
                deviceIndex,
                zlgDir,
                loadedDllPath,
                originalCwd,
                effectiveCwd,
                IntPtr.Zero,
                Marshal.GetLastWin32Error(),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                ex.GetType().Name,
                ex.Message);
        }
        finally
        {
            if (originalCwd != null)
            {
                try { Directory.SetCurrentDirectory(originalCwd); } catch { }
            }
        }
    }

    private static string TrimNativeString(string? value)
        => (value ?? string.Empty).TrimEnd('\0', ' ', '\r', '\n', '\t');

    private static string? TryGetLoadedZlgcanPath()
    {
        try
        {
            foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
            {
                if (string.Equals(m.ModuleName, ZlgcanNative.Dll, StringComparison.OrdinalIgnoreCase))
                {
                    return m.FileName;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>定位已加载的 zlgcan.dll 所在目录。</summary>
    internal static string? TryGetZlgcanDir()
    {
        try
        {
            foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
            {
                if (string.Equals(m.ModuleName, ZlgcanNative.Dll, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetDirectoryName(m.FileName);
                }
            }
        }
        catch { }
        // 回退：工程自带位置
        return Path.Combine(AppContext.BaseDirectory, "native", "can", "zlgcan");
    }
}

internal readonly record struct ZlgProbeResult(
    uint DeviceType,
    uint DeviceIndex,
    string? ZlgDirectory,
    string? LoadedDllPath,
    string? OriginalCwd,
    string? EffectiveCwd,
    IntPtr OpenHandle,
    int OpenLastWin32Error,
    uint? GetDeviceInfResult,
    int? GetDeviceInfLastWin32Error,
    byte? CanNum,
    string? SerialNumber,
    string? HardwareType,
    uint? CloseResult,
    int? CloseLastWin32Error,
    string? ExceptionType,
    string? ExceptionMessage)
{
    public bool IsSuccess => OpenHandle != IntPtr.Zero && ExceptionType == null;

    public string ToNativeCallLog(int attempt, string deviceName)
    {
        string handle = $"0x{OpenHandle.ToInt64():X16}";
        string close = CloseResult.HasValue
            ? $"; ZCAN_CloseDevice(handle={handle}) => ret={CloseResult.Value}, lastError={CloseLastWin32Error}"
            : "";
        string info = GetDeviceInfResult.HasValue
            ? $"; ZCAN_GetDeviceInf(handle={handle}) => ret={GetDeviceInfResult.Value}, lastError={GetDeviceInfLastWin32Error}, canNum={CanNum}, serial='{SerialNumber ?? string.Empty}', hwType='{HardwareType ?? string.Empty}'"
            : "";
        string exception = ExceptionType != null
            ? $"; exception={ExceptionType}: {ExceptionMessage}"
            : "";
        return $"[zlgcan] call#{attempt} {deviceName}: "
            + $"ZCAN_OpenDevice(type={DeviceType}, idx={DeviceIndex}, reserved=0) => handle={handle}, lastError={OpenLastWin32Error}"
            + info
            + close
            + exception
            + $"; cwdBefore={OriginalCwd ?? "<null>"}; cwdDuring={EffectiveCwd ?? "<null>"}; zlgDir={ZlgDirectory ?? "<null>"}; dll={LoadedDllPath ?? "<not-loaded>"}";
    }
}

internal static class ZlgcanNative
{
    public const string Dll = "zlgcan.dll";

    // 注意：以下设备类型号必须与 zlgcan SDK 的 kerneldlls/dll_cfg.ini 中的映射一致。
    // 之前的常量 38/39/41 是错误的（38/39 对应 zpcfd.dll 即 PCI-CANFD 板卡，41 才是 USBCANFD 系列）。
    // 当前值已对齐到 zlgcan SDK 官方 dll_cfg.ini：
    //   USBCAN_E_64.dll → 20, 21
    //   USBCAN_4E_U_X64.dll → 31
    //   USBCAN_8E_U_x64.dll → 34
    //   USBCANFD.dll → 41, 42, 43, 76, 77, 80, 81, 85, 86, 87
    //   USBCANFD800U.dll → 59
    public const uint ZCAN_USBCAN_E_U      = 20;
    public const uint ZCAN_USBCAN_2E_U     = 21;
    public const uint ZCAN_USBCAN_4E_U     = 31;
    public const uint ZCAN_USBCAN_8E_U     = 34;
    public const uint ZCAN_USBCANFD_200U   = 41;
    public const uint ZCAN_USBCANFD_100U   = 42;
    public const uint ZCAN_USBCANFD_MINI   = 43;
    public const uint ZCAN_USBCANFD_800U   = 59;
    public const uint ZCAN_PCIE_CANFD_100U = 37;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct can_frame
    {
        public uint CanId;
        public byte CanDlc;
        public byte __pad;
        public byte __res0;
        public byte __res1;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] Data;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ZCAN_Transmit_Data
    {
        public can_frame Frame;
        public uint TransmitType;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ZCAN_Receive_Data
    {
        public can_frame Frame;
        public ulong TimeStamp;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ZCAN_CHANNEL_CAN_INIT_CONFIG
    {
        public uint AccCode;
        public uint AccMask;
        public uint Reserved;
        public byte Filter;
        public byte Timing0;
        public byte Timing1;
        public byte Mode;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct ZCAN_CHANNEL_INIT_CONFIG_UNION
    {
        [FieldOffset(0)] public ZCAN_CHANNEL_CAN_INIT_CONFIG Can;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ZCAN_CHANNEL_INIT_CONFIG
    {
        public uint CanType;
        public ZCAN_CHANNEL_INIT_CONFIG_UNION Cfg;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct ZCAN_DEVICE_INFO
    {
        public ushort HwVersion;
        public ushort FwVersion;
        public ushort DrVersion;
        public ushort InVersion;
        public ushort IrqNum;
        public byte CanNum;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 20)]
        public string SerialNum;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 40)]
        public string HwType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public ushort[] Reserved;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern IntPtr ZCAN_OpenDevice(uint deviceType, uint deviceIndex, uint reserved);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern uint ZCAN_CloseDevice(IntPtr deviceHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern IntPtr ZCAN_InitCAN(IntPtr deviceHandle, uint canIndex, ref ZCAN_CHANNEL_INIT_CONFIG cfg);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern uint ZCAN_GetDeviceInf(IntPtr deviceHandle, ref ZCAN_DEVICE_INFO deviceInfo);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern uint ZCAN_StartCAN(IntPtr channelHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern uint ZCAN_ResetCAN(IntPtr channelHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern uint ZCAN_Transmit(IntPtr channelHandle, ref ZCAN_Transmit_Data data, uint length);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern uint ZCAN_Receive(IntPtr channelHandle, [Out] ZCAN_Receive_Data[] data, uint length, int waitTimeMs);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern IntPtr GetIProperty(IntPtr deviceHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern uint ReleaseIProperty(IntPtr property);

    [DllImport(Dll, EntryPoint = "ZCAN_SetValue", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, SetLastError = true)]
    public static extern uint SetValue(IntPtr property,
        [MarshalAs(UnmanagedType.LPStr)] string path,
        [MarshalAs(UnmanagedType.LPStr)] string value);
}
