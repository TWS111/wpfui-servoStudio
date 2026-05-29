// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Core.Usb;

/// <summary>
/// 基于 Windows 原生 WinUSB（<c>setupapi.dll</c> + <c>winusb.dll</c>）的 <see cref="IUsbBus"/> 实现。<br/>
/// 与从机侧 ThreadX/USBX Bulk-In / Bulk-Out 端点对接，承载曲线拟合下发、自适应参数交互、
/// 高带宽数据上报等大块数据。<br/>
/// <para>
/// 部署前提：从机固件需通过 WCID / MS OS 描述符（或安装匹配的 INF）使 Windows
/// 自动为该设备绑定 <c>WinUSB.sys</c>，否则 <see cref="CreateFile"/> 会失败。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UsbBulkBus : IUsbBus
{
    private SafeFileHandle? _deviceHandle;
    private IntPtr _winUsbHandle = IntPtr.Zero;
    private bool _disposed;

    /// <summary>当前总线是否已打开（设备已枚举且 WinUSB 句柄就绪）。</summary>
    public bool IsOpen => _winUsbHandle != IntPtr.Zero && _deviceHandle is { IsInvalid: false };

    /// <summary>最近一次 WritePipe 失败时的 Win32 错误码，0 表示无错误。</summary>
    public int LastSendError { get; private set; }

    /// <summary>
    /// 由上层（设备列表选择）传入的目标 Windows 设备实例 ID，
    /// 形如 <c>USB\VID_34B7&amp;PID_6002\SN12345</c>。<br/>
    /// 用于在 <see cref="Open"/> 内精确匹配同 VID/PID 多设备场景；为空时取首个匹配。
    /// </summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    /// WinUSB 设备接口 GUID，默认使用 <see cref="UsbDefaults.WinUsbDeviceInterfaceGuid"/>。<br/>
    /// 若从机 INF 自定义了 DeviceInterfaceGUID，可在外部覆盖。
    /// </summary>
    public Guid InterfaceGuid { get; set; } = UsbDefaults.WinUsbDeviceInterfaceGuid;

    /// <summary>Bulk OUT 端点地址。</summary>
    public byte BulkOutEndpoint { get; set; } = UsbDefaults.BulkOutEndpoint;

    /// <summary>Bulk IN 端点地址。</summary>
    public byte BulkInEndpoint { get; set; } = UsbDefaults.BulkInEndpoint;

    /// <summary>发送超时 (ms)。</summary>
    public int SendTimeoutMs { get; set; } = UsbDefaults.DefaultSendTimeoutMs;

    /// <summary>接收超时 (ms)。</summary>
    public int ReceiveTimeoutMs { get; set; } = UsbDefaults.DefaultReceiveTimeoutMs;

    /// <inheritdoc/>
    public bool Open(ushort vendorId, ushort productId)
    {
        Close();

        string? devicePath = FindDevicePath(vendorId, productId, InstanceId, InterfaceGuid);
        System.Diagnostics.Debug.WriteLine($"[UsbBulkBus] FindDevicePath VID={vendorId:X4} PID={productId:X4} GUID={InterfaceGuid} → {devicePath ?? "null"}");
        if (devicePath is null)
        {
            System.Diagnostics.Debug.WriteLine($"[UsbBulkBus] 未找到设备路径，Open 失败");
            return false;
        }

        SafeFileHandle handle = NativeMethods.CreateFile(
            devicePath,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_ATTRIBUTE_NORMAL | NativeMethods.FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            System.Diagnostics.Debug.WriteLine($"[UsbBulkBus] CreateFile 失败，Win32Error={err} (0x{err:X})");
            handle.Dispose();
            return false;
        }

        if (!NativeMethods.WinUsb_Initialize(handle, out IntPtr winUsb))
        {
            int err = Marshal.GetLastWin32Error();
            System.Diagnostics.Debug.WriteLine($"[UsbBulkBus] WinUsb_Initialize 失败，Win32Error={err} (0x{err:X})");
            handle.Dispose();
            return false;
        }

        System.Diagnostics.Debug.WriteLine($"[UsbBulkBus] Open 成功，path={devicePath}");
        _deviceHandle = handle;
        _winUsbHandle = winUsb;

        DiscoverBulkEndpoints();
        ApplyPipeTimeouts();
        return true;
    }

    /// <inheritdoc/>
    public void Close()
    {
        if (_winUsbHandle != IntPtr.Zero)
        {
            try 
            { 
                NativeMethods.WinUsb_Free(_winUsbHandle); 
            }
            catch
            {
                /* ignore */
            }

            _winUsbHandle = IntPtr.Zero;
        }

        if (_deviceHandle is not null)
        {
            try
            {
                _deviceHandle.Dispose(); 
            }
            catch
            {
                /* ignore */
            }
            
            _deviceHandle = null;
        }
    }

    /// <inheritdoc/>
    public bool Send(UsbPacket packet)
    {
        if (!IsOpen)
        {
            return false;
        }

        byte[] payload = UsbPacketCodec.Serialize(packet);
        int offset = 0;
        while (offset < payload.Length)
        {
            int chunk = Math.Min(UsbDefaults.MaxPacketSize, payload.Length - offset);
            if (!WritePipeChunk(payload, offset, chunk))
            {
                return false;
            }

            offset += chunk;
        }

        return true;
    }

    /// <inheritdoc/>
    public bool TryReceive(int timeoutMs, out UsbPacket packet)
    {
        packet = default;
        if (!IsOpen)
        {
            return false;
        }

        SetPipeTimeout(BulkInEndpoint, Math.Max(1, timeoutMs));

        byte[] buffer = new byte[UsbDefaults.MaxPacketSize];
        bool ok = NativeMethods.WinUsb_ReadPipe(
            _winUsbHandle,
            BulkInEndpoint,
            buffer,
            (uint)buffer.Length,
            out uint transferred,
            IntPtr.Zero);

        if (!ok || transferred == 0)
        {
            return false;
        }

        byte[] raw = new byte[transferred];
        Buffer.BlockCopy(buffer, 0, raw, 0, (int)transferred);
        return UsbPacketCodec.TryDeserialize(raw, UsbDirection.DeviceToHost, out packet);
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

    private bool WritePipeChunk(byte[] buffer, int offset, int count)
    {
        SetPipeTimeout(BulkOutEndpoint, SendTimeoutMs);

        byte[] chunk;
        if (offset == 0 && count == buffer.Length)
        {
            chunk = buffer;
        }
        else
        {
            chunk = new byte[count];
            Buffer.BlockCopy(buffer, offset, chunk, 0, count);
        }

        bool ok = NativeMethods.WinUsb_WritePipe(
            _winUsbHandle,
            BulkOutEndpoint,
            chunk,
            (uint)count,
            out uint transferred,
            IntPtr.Zero);

        if (!ok || transferred != (uint)count)
        {
            LastSendError = Marshal.GetLastWin32Error();
            System.Diagnostics.Debug.WriteLine(
                $"[UsbBulkBus] WritePipe 失败：pipe=0x{BulkOutEndpoint:X2}, transferred={transferred}/{count}, GLE={LastSendError} (0x{LastSendError:X})");
            return false;
        }

        LastSendError = 0;
        return true;
    }

    private void ApplyPipeTimeouts()
    {
        SetPipeTimeout(BulkOutEndpoint, SendTimeoutMs);
        SetPipeTimeout(BulkInEndpoint, ReceiveTimeoutMs);

        // 非 512B 倍数的 OUT 包需要 ZLP 终止，否则 EHCI 会一直等 short packet
        uint one = 1;
        try
        {
            NativeMethods.WinUsb_SetPipePolicy(
                _winUsbHandle,
                BulkOutEndpoint,
                NativeMethods.SHORT_PACKET_TERMINATE,
                sizeof(uint),
                ref one);
        }
        catch
        {
            // 部分驱动不支持，忽略
        }
    }

    private void SetPipeTimeout(byte pipeId, int timeoutMs)
    {
        if (_winUsbHandle == IntPtr.Zero)
        {
            return;
        }

        uint value = (uint)Math.Max(1, timeoutMs);
        try
        {
            NativeMethods.WinUsb_SetPipePolicy(
                _winUsbHandle,
                pipeId,
                NativeMethods.PIPE_TRANSFER_TIMEOUT,
                sizeof(uint),
                ref value);
        }
        catch
        {
            // 部分驱动不支持设置超时策略，忽略即可。
        }
    }

    /// <summary>
    /// WinUsb_Initialize 后立即调用，通过 QueryInterfaceSettings + QueryPipe 自动发现
    /// 第一个 Bulk OUT 和 Bulk IN 端点地址，覆盖硬编码的默认值（0x01/0x81）。
    /// </summary>
    private void DiscoverBulkEndpoints()
    {
        try
        {
            NativeMethods.USB_INTERFACE_DESCRIPTOR ifaceDesc = default;
            if (!NativeMethods.WinUsb_QueryInterfaceSettings(_winUsbHandle, 0, ref ifaceDesc))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[UsbBulkBus] WinUsb_QueryInterfaceSettings 失败，GLE={Marshal.GetLastWin32Error()}");
                return;
            }

            byte? foundOut = null;
            byte? foundIn = null;

            for (byte i = 0; i < ifaceDesc.bNumEndpoints; i++)
            {
                NativeMethods.WINUSB_PIPE_INFORMATION pipe = default;
                if (!NativeMethods.WinUsb_QueryPipe(_winUsbHandle, 0, i, ref pipe))
                {
                    continue;
                }

                if (pipe.PipeType != NativeMethods.USBD_PIPE_TYPE.UsbdPipeTypeBulk)
                {
                    continue;
                }

                // bit7 = 1 → IN，bit7 = 0 → OUT
                if ((pipe.PipeId & 0x80) != 0)
                {
                    foundIn ??= pipe.PipeId;
                }
                else
                {
                    foundOut ??= pipe.PipeId;
                }
            }

            if (foundOut.HasValue)
            {
                BulkOutEndpoint = foundOut.Value;
            }

            if (foundIn.HasValue)
            {
                BulkInEndpoint = foundIn.Value;
            }

            System.Diagnostics.Debug.WriteLine(
                $"[UsbBulkBus] 端点发现完成：OUT=0x{BulkOutEndpoint:X2}  IN=0x{BulkInEndpoint:X2}  接口端点数={ifaceDesc.bNumEndpoints}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UsbBulkBus] DiscoverBulkEndpoints 异常：{ex.Message}");
        }
    }

    private static string? FindDevicePath(ushort vendorId, ushort productId, string instanceFilter, Guid interfaceGuid)
    {
        IntPtr devInfo = NativeMethods.SetupDiGetClassDevs(
            ref interfaceGuid,
            null,
            IntPtr.Zero,
            NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);

        if (devInfo == NativeMethods.INVALID_HANDLE_VALUE)
        {
            return null;
        }

        string vidPidLower = $"vid_{vendorId:x4}&pid_{productId:x4}";
        string? instanceTail = ExtractInstanceTail(instanceFilter);
        string? matched = null;

        try
        {
            var ifaceData = new NativeMethods.SP_DEVICE_INTERFACE_DATA();
            ifaceData.cbSize = (uint)Marshal.SizeOf(ifaceData);

            uint index = 0;
            while (NativeMethods.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref interfaceGuid, index, ref ifaceData))
            {
                index++;
                string? path = GetDevicePath(devInfo, ref ifaceData);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                string pathLower = path.ToLowerInvariant();
                if (productId != UsbDefaults.AnyProductId)
                {
                    if (!pathLower.Contains(vidPidLower, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }
                else if (!pathLower.Contains($"vid_{vendorId:x4}", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(instanceTail))
                {
                    if (!pathLower.Contains(instanceTail!.ToLowerInvariant(), StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                matched = path;
                break;
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(devInfo);
        }

        return matched;
    }

    private static string? ExtractInstanceTail(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId))
        {
            return null;
        }

        // InstanceId 形如 "USB\VID_34B7&PID_6002\SN12345"，取末段
        int last = instanceId.LastIndexOf('\\');
        return last >= 0 && last + 1 < instanceId.Length ? instanceId[(last + 1)..] : instanceId;
    }

    private static string? GetDevicePath(IntPtr devInfo, ref NativeMethods.SP_DEVICE_INTERFACE_DATA ifaceData)
    {
        // 第一次调用：拿到所需缓冲区大小
        NativeMethods.SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
        if (required == 0)
        {
            return null;
        }

        IntPtr detailBuffer = Marshal.AllocHGlobal((int)required);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W 的 cbSize：32 位为 6，64 位为 8（包含 1 字符 padding）
            int cbSize = IntPtr.Size == 8 ? 8 : 6;
            Marshal.WriteInt32(detailBuffer, cbSize);

            if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData, detailBuffer, required, out _, IntPtr.Zero))
            {
                return null;
            }

            // DevicePath 紧跟 cbSize 之后（4 字节偏移）
            IntPtr pathPtr = IntPtr.Add(detailBuffer, 4);
            return Marshal.PtrToStringUni(pathPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(detailBuffer);
        }
    }

    [SupportedOSPlatform("windows")]
    private static class NativeMethods
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        public const uint DIGCF_PRESENT = 0x00000002;
        public const uint DIGCF_DEVICEINTERFACE = 0x00000010;

        public const uint PIPE_TRANSFER_TIMEOUT = 0x03;
        public const uint SHORT_PACKET_TERMINATE = 0x01;

        public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid,
            string? enumerator,
            IntPtr hwndParent,
            uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet,
            IntPtr deviceInfoData,
            ref Guid interfaceClassGuid,
            uint memberIndex,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
        public static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            IntPtr deviceInterfaceDetailData,
            uint deviceInterfaceDetailDataSize,
            out uint requiredSize,
            IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_Initialize(SafeFileHandle deviceHandle, out IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_Free(IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_WritePipe(
            IntPtr interfaceHandle,
            byte pipeId,
            byte[] buffer,
            uint bufferLength,
            out uint lengthTransferred,
            IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_ReadPipe(
            IntPtr interfaceHandle,
            byte pipeId,
            byte[] buffer,
            uint bufferLength,
            out uint lengthTransferred,
            IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_SetPipePolicy(
            IntPtr interfaceHandle,
            byte pipeId,
            uint policyType,
            uint valueLength,
            ref uint value);

        // ── 端点查询 ─────────────────────────────────────────────────────────────
        public enum USBD_PIPE_TYPE : int
        {
            UsbdPipeTypeControl = 0,
            UsbdPipeTypeIsochronous = 1,
            UsbdPipeTypeBulk = 2,
            UsbdPipeTypeInterrupt = 3,
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct USB_INTERFACE_DESCRIPTOR
        {
            public byte bLength;
            public byte bDescriptorType;
            public byte bInterfaceNumber;
            public byte bAlternateSetting;
            public byte bNumEndpoints;
            public byte bInterfaceClass;
            public byte bInterfaceSubClass;
            public byte bInterfaceProtocol;
            public byte iInterface;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINUSB_PIPE_INFORMATION
        {
            public USBD_PIPE_TYPE PipeType;
            public byte PipeId;
            public ushort MaximumPacketSize;
            public byte Interval;
        }

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_QueryInterfaceSettings(
            IntPtr interfaceHandle,
            byte alternateSettingNumber,
            ref USB_INTERFACE_DESCRIPTOR usbAltInterfaceDescriptor);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_QueryPipe(
            IntPtr interfaceHandle,
            byte alternateInterfaceNumber,
            byte pipeIndex,
            ref WINUSB_PIPE_INFORMATION pipeInformation);
    }
}

