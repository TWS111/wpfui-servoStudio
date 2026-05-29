// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.Concurrent;
using System.Threading;
using RJCP.IO.Ports;

namespace Core.Usb;

/// <summary>
/// CDC-ACM (USB 虚拟串口) 工作方式下的 <see cref="IUsbBus"/> 实现。<br/>
/// 从机 USBX 默认 CDC-ACM demo 在宿主侧会出现一个 COM 端口，本类直接复用现有的
/// <c>RJCP.IO.Ports.SerialPortStream</c>（与 ModbusRtuMaster 相同的依赖）打开它。<br/>
/// <para>
/// <see cref="UsbPacket"/> 在串口介质上的封装由当前 USB 帧格式模板决定：
/// <see cref="Send"/> 使用发送模板拼接原始字节流，<see cref="TryReceive"/> 使用应答模板反解为 <see cref="UsbPacket"/>。
/// 当前仍未补充独立帧定界 / CRC，因此部署时若设备侧存在分片或粘包，仍建议在模板外增加稳定的边界协议。
/// </para>
/// </summary>
public sealed class CdcAcmUsbBus : IUsbBus
{
    private readonly string _portName;
    private readonly int _baud;
    private SerialPortStream? _port;
    private readonly ConcurrentQueue<UsbPacket> _rx = new();
    private readonly Lock _ioLock = new();
    private bool _disposed;

    /// <summary>波特率默认 921600（CDC 通常忽略波特率，但保留以兼容某些桥接芯片）。</summary>
    public CdcAcmUsbBus(string portName, int baud = 921600)
    {
        _portName = portName ?? throw new ArgumentNullException(nameof(portName));
        _baud = baud;
    }

    public bool IsOpen => _port?.IsOpen == true;

    /// <summary>
    /// CDC-ACM 模式不依赖 VID/PID 直接打开（设备已经被系统映射成 COM 口），
    /// 仅在调试日志中保留传入的 VID/PID。<br/>
    /// 真正定位 COM 名由上层 <see cref="UsbDeviceDescriptor.ComPort"/> 提供，
    /// 通过构造函数传入 <see cref="_portName"/>。
    /// </summary>
    public bool Open(ushort vendorId, ushort productId)
    {
        try
        {
            Close();
            _port = new SerialPortStream(_portName, _baud, 8, Parity.None, StopBits.One)
            {
                ReadTimeout = UsbDefaults.DefaultReceiveTimeoutMs,
                WriteTimeout = UsbDefaults.DefaultSendTimeoutMs,
            };
            _port.Open();
            return _port.IsOpen;
        }
        catch
        {
            try { _port?.Dispose(); } catch { /* ignore */ }
            _port = null;
            return false;
        }
    }

    public void Close()
    {
        try { _port?.Close(); } catch { /* ignore */ }
        try { _port?.Dispose(); } catch { /* ignore */ }
        _port = null;
        while (_rx.TryDequeue(out _)) { }
    }

    public bool Send(UsbPacket packet)
    {
        if (!IsOpen)
        {
            return false;
        }

        try
        {
            byte[] rawBytes = UsbPacketCodec.Serialize(packet);
            lock (_ioLock)
            {
                _port!.Write(rawBytes, 0, rawBytes.Length);
                _port.Flush();
            }
            return true;
        }
        catch
        {
            return false;
        }
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
            _port!.ReadTimeout = Math.Max(1, timeoutMs);
            var buf = new byte[UsbDefaults.MaxPacketSize];
            int n = _port.Read(buf, 0, buf.Length);
            if (n <= 0)
            {
                return false;
            }

            var rawBytes = new byte[n];
            Buffer.BlockCopy(buf, 0, rawBytes, 0, n);
            return UsbPacketCodec.TryDeserialize(rawBytes, UsbDirection.DeviceToHost, out packet);
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch
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
        Close();
        GC.SuppressFinalize(this);
    }
}
