// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Diagnostics;
using System.Threading;
using RJCP.IO.Ports;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Models;

namespace Core.Modbus;

/// <summary>
/// Modbus 功能码（仅伺服参数读写常用子集）。
/// </summary>
public enum ModbusFunctionCode : byte
{
    /// <summary>0x03 读保持寄存器（最常用：读 H 参数 / CiA402 对象镜像）。</summary>
    ReadHoldingRegisters = 0x03,
    /// <summary>0x06 写单个保持寄存器（16 位参数）。</summary>
    WriteSingleRegister = 0x06,
    /// <summary>0x10 写多个保持寄存器（32 位参数 / 批量写）。</summary>
    WriteMultipleRegisters = 0x10,
}

/// <summary>
/// Modbus 异常码。前 8 项为 Modbus 协议标准；后续为本地通信错误。
/// </summary>
public enum ModbusExceptionCode : byte
{
    None = 0x00,
    IllegalFunction = 0x01,
    IllegalDataAddress = 0x02,
    IllegalDataValue = 0x03,
    SlaveDeviceFailure = 0x04,
    Acknowledge = 0x05,
    SlaveDeviceBusy = 0x06,
    NegativeAcknowledge = 0x07,
    MemoryParityError = 0x08,
    GatewayPathUnavailable = 0x0A,
    GatewayTargetDeviceFailedToRespond = 0x0B,
    // ── 本地（非协议）错误 ──
    CrcError = 0xF0,
    Timeout = 0xF1,
    InvalidResponse = 0xF2,
    PortClosed = 0xF3,
    SlaveAddressMismatch = 0xF4,
    FunctionMismatch = 0xF5,
}

/// <summary>
/// Modbus RTU 主站。串口同步请求-应答 + CRC16 校验。<br/>
/// 公共 API 风格与 <c>EtherCATMaster</c> 平行：<see cref="ReadSDO{T}"/> / <see cref="TryReadSDO{T}"/> / <see cref="TryWriteSDO{T}"/>。<br/>
/// CiA 对象索引 → Modbus 寄存器地址映射默认采用 <b>汇川 IS620N/SV660 风格</b>：<br/>
/// <c>register = ((SdoIndex &amp; 0x00FF) &lt;&lt; 8) | (SubIndex - 1)</c><br/>
/// 与 HVariables 表的 CommAddress（如 "2008-01h"）逐项对齐：H 组号在高字节、参数索引（从 0 开始）在低字节。<br/>
/// 如需对接 CiA402 标准对象（0x6xxx）的厂家专属映射，可子类化并重写 <see cref="MapToModbusAddress"/>。
/// </summary>
public class ModbusRtuMaster : IDisposable, IServoMaster
{
    private readonly SerialPortStream _port = new();
    private readonly Lock _txLock = new();
    private bool _disposed;

    /// <summary>响应等待超时（ms）。</summary>
    public int ResponseTimeoutMs { get; set; } = 500;

    /// <summary>帧间静默时间（ms），符合 RTU 3.5 字符间隔，至少 5 ms。</summary>
    public int InterFrameDelayMs { get; set; } = 5;

    /// <summary>失败重试次数（不含首次）。</summary>
    public int RetryCount { get; set; } = 1;

    /// <summary>最近一次操作的失败原因。</summary>
    public ModbusExceptionCode LastException { get; private set; } = ModbusExceptionCode.None;

    /// <summary>串口是否已打开。</summary>
    public bool IsOpen => _port.IsOpen;

    /// <summary>当前串口名（未打开时返回空串）。</summary>
    public string PortName => _port.PortName ?? string.Empty;

    /// <summary>底层串口实例（仅供测试与诊断使用）。</summary>
    internal SerialPortStream UnderlyingPort => _port;

    /// <summary>
    /// 打开串口。重复打开会先 Close 再 Open。
    /// </summary>
    public bool Open(string portName, int baudRate, int dataBits, Parity parity, StopBits stopBits)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_port.IsOpen)
            _port.Close();

        _port.PortName = portName;
        _port.BaudRate = baudRate;
        _port.DataBits = dataBits;
        _port.Parity = parity;
        _port.StopBits = stopBits;
        _port.ReadTimeout = ResponseTimeoutMs;
        _port.WriteTimeout = ResponseTimeoutMs;
        _port.NewLine = "\r\n";

        _port.Open();
        if (_port.IsOpen)
            _port.RtsEnable = true;

        LastException = ModbusExceptionCode.None;
        return _port.IsOpen;
    }

    /// <summary>关闭串口。</summary>
    public void Close()
    {
        if (_port.IsOpen)
            _port.Close();
        LastException = ModbusExceptionCode.None;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Close(); } catch { /* ignore */ }
        _port.Dispose();
        GC.SuppressFinalize(this);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  CiA 索引 → Modbus 地址映射
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 把 CiA 对象索引映射到 Modbus 保持寄存器地址。<br/>
    /// 默认实现：<c>((index &amp; 0x00FF) &lt;&lt; 8) | (subIndex - 1)</c><br/>
    /// 当 <paramref name="subIndex"/> 为 0 时，按 0 处理（兼容 0x6040/0x6041 等无子索引对象）。
    /// </summary>
    public virtual int MapToModbusAddress(ushort sdoIndex, byte subIndex)
        => ((sdoIndex & 0x00FF) << 8) | (subIndex == 0 ? 0 : (subIndex - 1));

    // ════════════════════════════════════════════════════════════════════════
    //  与 EtherCATMaster 平行的 SDO 风格 API
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>读取一个 SDO 风格参数（按对象索引/子索引）。失败返回 false，原因见 <see cref="LastException"/>。</summary>
    public bool TryReadSDO<T>(int slaveAddr, ushort index, byte subIndex, out T value) where T : struct
    {
        value = default;
        int regs = ModbusRegisterCount<T>();
        ushort addr = (ushort)MapToModbusAddress(index, subIndex);
        if (!ReadHoldingRegisters((byte)slaveAddr, addr, (ushort)regs, out byte[] data))
            return false;
        value = ConvertFromBigEndian<T>(data);
        return true;
    }

    /// <summary>写入一个 SDO 风格参数。1 寄存器走 0x06，多寄存器走 0x10。</summary>
    public bool TryWriteSDO<T>(int slaveAddr, ushort index, byte subIndex, T value) where T : struct
    {
        byte[] payload = ConvertToBigEndian(value);
        ushort addr = (ushort)MapToModbusAddress(index, subIndex);
        int regs = payload.Length / 2;
        if (regs == 1)
            return WriteSingleRegister((byte)slaveAddr, addr, (ushort)((payload[0] << 8) | payload[1]));

        return WriteMultipleRegisters((byte)slaveAddr, addr, (ushort)regs, payload);
    }

    /// <summary>读取参数；失败抛异常（与 EtherCATMaster.ReadSDO 相同形态）。</summary>
    public T ReadSDO<T>(int slaveAddr, int index, int subIndex) where T : struct
    {
        if (TryReadSDO(slaveAddr, (ushort)index, (byte)subIndex, out T v))
            return v;
        throw new InvalidOperationException(
            $"Modbus ReadSDO 失败：slave={slaveAddr}, idx=0x{index:X4}/{subIndex}, err={LastException}");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Modbus 功能码原子操作
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>0x03 读保持寄存器。<paramref name="data"/> 为大端字节流，长度 = <paramref name="count"/> × 2。</summary>
    public bool ReadHoldingRegisters(byte slave, ushort startAddr, ushort count, out byte[] data)
    {
        data = [];
        var req = new byte[8];
        req[0] = slave;
        req[1] = (byte)ModbusFunctionCode.ReadHoldingRegisters;
        req[2] = (byte)(startAddr >> 8);
        req[3] = (byte)(startAddr & 0xFF);
        req[4] = (byte)(count >> 8);
        req[5] = (byte)(count & 0xFF);
        AppendCrc(req, 6);

        // 应答：addr(1) + func(1) + byteCount(1) + data(2*count) + crc(2)
        int expectedLen = 5 + 2 * count;
        if (!Transact(req, expectedLen, out byte[]? resp) || resp is null)
            return false;

        if (resp[2] != 2 * count)
        {
            LastException = ModbusExceptionCode.InvalidResponse;
            return false;
        }

        data = new byte[2 * count];
        Buffer.BlockCopy(resp, 3, data, 0, data.Length);
        return true;
    }

    /// <summary>0x06 写单个保持寄存器。</summary>
    public bool WriteSingleRegister(byte slave, ushort addr, ushort value)
    {
        var req = new byte[8];
        req[0] = slave;
        req[1] = (byte)ModbusFunctionCode.WriteSingleRegister;
        req[2] = (byte)(addr >> 8);
        req[3] = (byte)(addr & 0xFF);
        req[4] = (byte)(value >> 8);
        req[5] = (byte)(value & 0xFF);
        AppendCrc(req, 6);

        // 应答：与请求等长 8 字节，且 echo
        if (!Transact(req, 8, out byte[]? resp) || resp is null)
            return false;

        if (resp[2] != req[2] || resp[3] != req[3] || resp[4] != req[4] || resp[5] != req[5])
        {
            LastException = ModbusExceptionCode.InvalidResponse;
            return false;
        }

        return true;
    }

    /// <summary>0x10 写多个保持寄存器。<paramref name="payload"/> 为大端字节流，长度 = <paramref name="regCount"/> × 2。</summary>
    public bool WriteMultipleRegisters(byte slave, ushort startAddr, ushort regCount, byte[] payload)
    {
        if (payload is null || payload.Length != regCount * 2)
        {
            LastException = ModbusExceptionCode.IllegalDataValue;
            return false;
        }

        int byteCount = payload.Length;
        var req = new byte[9 + byteCount];
        req[0] = slave;
        req[1] = (byte)ModbusFunctionCode.WriteMultipleRegisters;
        req[2] = (byte)(startAddr >> 8);
        req[3] = (byte)(startAddr & 0xFF);
        req[4] = (byte)(regCount >> 8);
        req[5] = (byte)(regCount & 0xFF);
        req[6] = (byte)byteCount;
        Buffer.BlockCopy(payload, 0, req, 7, byteCount);
        AppendCrc(req, 7 + byteCount);

        // 应答：addr+func+startAddr+regCount+crc = 8 字节
        if (!Transact(req, 8, out byte[]? resp) || resp is null)
            return false;

        if (resp[2] != req[2] || resp[3] != req[3] || resp[4] != req[4] || resp[5] != req[5])
        {
            LastException = ModbusExceptionCode.InvalidResponse;
            return false;
        }

        return true;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  收发与帧解析
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>同步执行一次请求-应答事务，自动校验 CRC、解析异常码。线程安全。</summary>
    private bool Transact(byte[] request, int expectedResponseLen, out byte[]? response)
    {
        response = null;
        if (!_port.IsOpen)
        {
            LastException = ModbusExceptionCode.PortClosed;
            return false;
        }

        lock (_txLock)
        {
            int retries = Math.Max(0, RetryCount);
            for (int attempt = 0; attempt <= retries; attempt++)
            {
                try
                {
                    // 清干净接收缓冲
                    if (_port.BytesToRead > 0)
                        _port.DiscardInBuffer();

                    if (InterFrameDelayMs > 0)
                        Thread.Sleep(InterFrameDelayMs);

                    _port.Write(request, 0, request.Length);

                    if (TryReceive(request[0], request[1], expectedResponseLen, out response, out ModbusExceptionCode err))
                    {
                        LastException = ModbusExceptionCode.None;
                        return true;
                    }

                    LastException = err;
                    response = null;

                    // 协议级 IllegalDataAddress 等不再重试，节省时间
                    if (err == ModbusExceptionCode.IllegalFunction
                        || err == ModbusExceptionCode.IllegalDataAddress
                        || err == ModbusExceptionCode.IllegalDataValue)
                        return false;
                }
                catch (TimeoutException)
                {
                    LastException = ModbusExceptionCode.Timeout;
                }
                catch (Exception)
                {
                    LastException = ModbusExceptionCode.InvalidResponse;
                }
            }

            return false;
        }
    }

    /// <summary>读取一帧应答。<paramref name="expectedLen"/> 为正常应答总长（含 CRC）。</summary>
    private bool TryReceive(byte expectSlave, byte expectFunc, int expectedLen, out byte[]? frame, out ModbusExceptionCode err)
    {
        frame = null;
        err = ModbusExceptionCode.None;

        // 先收 3 字节头：addr + func + (byteCount 或 异常码)
        var head = new byte[3];
        if (!ReadFully(head, 0, 3, out _))
        {
            err = ModbusExceptionCode.Timeout;
            return false;
        }

        if (head[0] != expectSlave)
        {
            err = ModbusExceptionCode.SlaveAddressMismatch;
            return false;
        }

        bool isException = (head[1] & 0x80) != 0;
        if (isException)
        {
            // 异常应答：addr + func|0x80 + exception_code + crc(2) = 5 字节
            var tail = new byte[2];
            if (!ReadFully(tail, 0, 2, out _))
            {
                err = ModbusExceptionCode.Timeout;
                return false;
            }

            var excFrame = new byte[5];
            Buffer.BlockCopy(head, 0, excFrame, 0, 3);
            Buffer.BlockCopy(tail, 0, excFrame, 3, 2);
            if (!CheckCrc(excFrame))
            {
                err = ModbusExceptionCode.CrcError;
                return false;
            }

            err = (ModbusExceptionCode)head[2];
            return false;
        }

        if ((head[1] & 0x7F) != expectFunc)
        {
            err = ModbusExceptionCode.FunctionMismatch;
            return false;
        }

        int remaining = expectedLen - 3;
        if (remaining <= 0)
        {
            err = ModbusExceptionCode.InvalidResponse;
            return false;
        }

        var rest = new byte[remaining];
        if (!ReadFully(rest, 0, remaining, out _))
        {
            err = ModbusExceptionCode.Timeout;
            return false;
        }

        var full = new byte[expectedLen];
        Buffer.BlockCopy(head, 0, full, 0, 3);
        Buffer.BlockCopy(rest, 0, full, 3, remaining);

        if (!CheckCrc(full))
        {
            err = ModbusExceptionCode.CrcError;
            return false;
        }

        frame = full;
        return true;
    }

    /// <summary>带超时的精确读：必须读满 <paramref name="count"/> 字节才返回 true。</summary>
    private bool ReadFully(byte[] buf, int offset, int count, out int read)
    {
        read = 0;
        var sw = Stopwatch.StartNew();
        while (read < count)
        {
            int got = _port.Read(buf, offset + read, count - read);
            if (got <= 0)
            {
                if (sw.ElapsedMilliseconds > ResponseTimeoutMs) return false;
                continue;
            }

            read += got;
        }

        return true;
    }

    private static void AppendCrc(byte[] frame, int dataLen)
    {
        var slice = new byte[dataLen];
        Buffer.BlockCopy(frame, 0, slice, 0, dataLen);
        byte[] crc = CRC16_modbus.CRC16.CRCCalc(slice);
        // CRC16_modbus 的 CRCCalc 返回 [低字节, 高字节]（注释里反了，按调用现状此约定为：crc16[0]=低,crc16[1]=高）
        frame[dataLen + 0] = crc[0];
        frame[dataLen + 1] = crc[1];
    }

    private static bool CheckCrc(byte[] frame)
    {
        if (frame.Length < 3) return false;
        byte[] crc = CRC16_modbus.CRC16.CRCCalc(frame, 0, frame.Length - 2);
        return frame[^2] == crc[0] && frame[^1] == crc[1];
    }

    // ════════════════════════════════════════════════════════════════════════
    //  数据类型 ↔ 大端字节流（Modbus 寄存器为大端 16 位）
    // ════════════════════════════════════════════════════════════════════════

    private static int ModbusRegisterCount<T>() where T : struct
    {
        Type t = typeof(T);
        if (t == typeof(byte) || t == typeof(sbyte) || t == typeof(ushort) || t == typeof(short))
            return 1;
        if (t == typeof(uint) || t == typeof(int) || t == typeof(float))
            return 2;
        throw new NotSupportedException($"Modbus SDO 暂不支持类型: {t.Name}");
    }

    private static T ConvertFromBigEndian<T>(byte[] data) where T : struct
    {
        Type t = typeof(T);
        if (t == typeof(byte))   return (T)(object)data[1];
        if (t == typeof(sbyte))  return (T)(object)unchecked((sbyte)data[1]);
        if (t == typeof(ushort)) return (T)(object)(ushort)((data[0] << 8) | data[1]);
        if (t == typeof(short))  return (T)(object)unchecked((short)((data[0] << 8) | data[1]));
        if (t == typeof(uint))   return (T)(object)(uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
        if (t == typeof(int))    return (T)(object)unchecked((int)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]));
        if (t == typeof(float))
        {
            uint u = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
            return (T)(object)BitConverter.UInt32BitsToSingle(u);
        }

        throw new NotSupportedException($"Modbus SDO 暂不支持类型: {t.Name}");
    }

    private static byte[] ConvertToBigEndian<T>(T value) where T : struct
    {
        switch (value)
        {
            case byte b:    return [0x00, b];
            case sbyte sb:  return [0x00, unchecked((byte)sb)];
            case ushort us: return [(byte)(us >> 8), (byte)(us & 0xFF)];
            case short s:
                ushort us2 = unchecked((ushort)s);
                return [(byte)(us2 >> 8), (byte)(us2 & 0xFF)];
            case uint u:
                return [(byte)(u >> 24), (byte)(u >> 16), (byte)(u >> 8), (byte)(u & 0xFF)];
            case int i:
                uint u2 = unchecked((uint)i);
                return [(byte)(u2 >> 24), (byte)(u2 >> 16), (byte)(u2 >> 8), (byte)(u2 & 0xFF)];
            case float f:
                uint u3 = BitConverter.SingleToUInt32Bits(f);
                return [(byte)(u3 >> 24), (byte)(u3 >> 16), (byte)(u3 >> 8), (byte)(u3 & 0xFF)];
            default:
                throw new NotSupportedException($"Modbus SDO 暂不支持类型: {typeof(T).Name}");
        }
    }
}
