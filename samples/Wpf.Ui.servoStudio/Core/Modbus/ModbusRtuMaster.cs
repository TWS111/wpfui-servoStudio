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
using Wpf.Ui.servoStudio.Services;

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

    /// <summary>响应等待超时（ms）。默认 100ms：高频轮询场景下足够覆盖单字符串口往返。</summary>
    public int ResponseTimeoutMs { get; set; } = 100;

    /// <summary>
    /// 帧间静默时间下限（ms）。<br/>
    /// 设为 0 时仅遵循 Modbus RTU 协议规定的 3.5 字符间隔（依波特率自动换算，详见 <see cref="ComputeT35Ms"/>）；<br/>
    /// 设为正值时取“协议下限”与该值的较大者。<br/>
    /// 修改：默认值由原 5 ms 调整为 0，以达成 &lt; 5 ms 的轮询周期。
    /// </summary>
    public int InterFrameDelayMs { get; set; }

    /// <summary>失败重试次数（不含首次）。默认 0：高频诊断不重试，由上层统计失败率。</summary>
    public int RetryCount { get; set; }

    /// <summary>最近一次操作的失败原因。</summary>
    public ModbusExceptionCode LastException { get; private set; } = ModbusExceptionCode.None;

    /// <summary>最近一次发送的原始帧字节（含 CRC）。诊断/显示用，非线程安全读取。</summary>
    public byte[] LastTxFrame { get; private set; } = [];

    /// <summary>最近一次成功接收的原始帧字节（含 CRC）。失败时为空数组。</summary>
    public byte[] LastRxFrame { get; private set; } = [];

    /// <summary>
    /// 最近一次成功事务中“主机发送结束 → 从机第一字节到达”的时间间隔（ms）。<br/>
    /// 仅成功事务会更新；失败时保留上一次有效值。用于在总线诊断面板中展示主从应答延时。
    /// </summary>
    public double LastResponseLatencyMs { get; private set; }

    /// <summary>最近一次接收完成的高精度时间戳（用于实现 3.5 字符自适应帧间静默）。</summary>
    private long _lastRxFinishTicks;

    /// <summary>串口是否已打开。</summary>
    public bool IsOpen => _port.IsOpen;

    /// <summary>当前串口名（未打开时返回空串）。</summary>
    public string PortName => _port.PortName ?? string.Empty;

    /// <summary>
    /// 最近一次 <see cref="Open"/> 调用的失败原因（人读文本）。
    /// 打开成功时为 <see langword="null"/>，失败时包含异常消息。
    /// </summary>
    public string? LastOpenError { get; private set; }

    /// <summary>底层串口实例（仅供测试与诊断使用）。</summary>
    internal SerialPortStream UnderlyingPort => _port;

    /// <summary>
    /// 打开串口。重复打开会先 Close 再 Open。<br/>
    /// 失败时返回 <see langword="false"/> 并将原因写入 <see cref="LastOpenError"/>；不向上抛出异常。
    /// </summary>
    public bool Open(string portName, int baudRate, int dataBits, Parity parity, StopBits stopBits)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LastOpenError = null;

        if (_port.IsOpen)
        {
            try { _port.Close(); } catch { /* ignore */ }
        }

        // ── Open 之前仅设置串口"硬件参数"。
        // RJCP.IO.Ports 的 ReadTimeout / WriteTimeout / DtrEnable / RtsEnable / DiscardInBuffer
        // 在端口未打开时访问可能抛 InvalidOperationException；NewLine 也仅在 Open 后保证有效。
        try
        {
            _port.PortName = portName;
            _port.BaudRate = baudRate;
            _port.DataBits = dataBits;
            _port.Parity = parity;
            _port.StopBits = stopBits;
            _port.Handshake = Handshake.None;   // 显式禁用握手，避免默认值在 USB-Serial 上拦截通信
        }
        catch (Exception ex)
        {
            LastOpenError = $"串口参数无效（{portName} @ {baudRate} {dataBits}{parity.ToString()[..1]}{stopBits}）：{ex.Message}";
            LastException = ModbusExceptionCode.PortClosed;
            return false;
        }

        try
        {
            _port.Open();
        }
        catch (UnauthorizedAccessException ex)
        {
            LastOpenError = $"串口 {portName} 已被其他程序占用（{ex.Message}）";
            LastException = ModbusExceptionCode.PortClosed;
            return false;
        }
        catch (System.IO.IOException ex)
        {
            LastOpenError = $"串口 {portName} 不存在或硬件错误（{ex.Message}）";
            LastException = ModbusExceptionCode.PortClosed;
            return false;
        }
        catch (Exception ex)
        {
            // 包含 InvalidOperationException 等未预期类型 —— 把异常类型一并写出便于现场排错
            LastOpenError = $"打开串口 {portName} 失败：{ex.GetType().Name} - {ex.Message}";
            LastException = ModbusExceptionCode.PortClosed;
            return false;
        }

        if (!_port.IsOpen)
        {
            LastOpenError = $"串口 {portName} 打开后状态异常";
            LastException = ModbusExceptionCode.PortClosed;
            return false;
        }

        // ── Open 之后再设置 DTR/RTS、读写超时及缓冲清理。
        // 1) 多数 USB-RS485/RS232 转换器需要 DTR=高才能正常供电（串口助手默认即开 DTR），
        //    若仅置位 RTS 而 DTR 仍为低，部分线缆会出现"打开成功但收发不通"或写超时。
        // 2) 缓冲清理可避免上一次连接遗留的脏数据干扰首次握手。
        try
        {
            _port.ReadTimeout = ResponseTimeoutMs;
            _port.WriteTimeout = ResponseTimeoutMs;
            _port.DtrEnable = true;
            _port.RtsEnable = true;
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();
        }
        catch (Exception ex)
        {
            LastOpenError = $"串口 {portName} 已打开但配置失败：{ex.GetType().Name} - {ex.Message}";
            LastException = ModbusExceptionCode.PortClosed;
            try { _port.Close(); } catch { /* ignore */ }
            return false;
        }

        LastException = ModbusExceptionCode.None;
        return true;
    }

    /// <summary>关闭串口。</summary>
    public void Close()
    {
        StopSilentReceive();
        if (_port.IsOpen)
        {
            _port.Close();
        }

        LastException = ModbusExceptionCode.None;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { Close(); } catch { /* ignore */ }
        _port.Dispose();
        GC.SuppressFinalize(this);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  静默接收模式（Silent / Receive-Only）
    //  Debug 模式下由上位机切换：开启后主站不再发送任何请求帧（Transact 短路返回 false），
    //  由后台线程从串口读取从机主动上报的 ASCII 帧（格式 ">name:val,name:val\r\n"）。
    //  每收到一帧触发 SilentFrameReceived 事件。线程退出后再恢复正常发送。
    // ════════════════════════════════════════════════════════════════════════

    private volatile bool _silentMode;
    private Thread? _silentThread;
    private CancellationTokenSource? _silentCts;

    /// <summary>
    /// 是否处于静默（只接收不发送）模式。<br/>
    /// 开启后 <see cref="Transact"/> 立即返回 false 且置 <see cref="LastException"/>=<see cref="ModbusExceptionCode.PortClosed"/>，
    /// 同时启动后台行读取线程，每收到一个以换行结束的 ASCII 字符串触发 <see cref="SilentFrameReceived"/>。
    /// </summary>
    public bool IsSilentMode => _silentMode;

    /// <summary>静默模式下每收到一行（不含换行符）触发，回调在后台线程上下文。</summary>
    public event Action<string>? SilentFrameReceived;

    /// <summary>进入静默模式。串口未打开时不操作并返回 false。重复调用幂等。</summary>
    public bool StartSilentReceive()
    {
        if (!_port.IsOpen)
        {
            return false;
        }
        if (_silentMode)
        {
            return true;
        }
        _silentCts = new CancellationTokenSource();
        var ct = _silentCts.Token;
        _silentMode = true;
        _silentThread = new Thread(() => SilentReaderLoop(ct))
        {
            IsBackground = true,
            Name = "ModbusSilentReader",
        };
        _silentThread.Start();
        return true;
    }

    /// <summary>退出静默模式。重复调用幂等。</summary>
    public void StopSilentReceive()
    {
        if (!_silentMode)
        {
            return;
        }
        _silentMode = false;
        try { _silentCts?.Cancel(); } catch { /* ignore */ }
        try { _silentThread?.Join(500); } catch { /* ignore */ }
        _silentThread = null;
        try { _silentCts?.Dispose(); } catch { /* ignore */ }
        _silentCts = null;
    }

    private void SilentReaderLoop(CancellationToken ct)
    {
        // 解析以 \r\n 结尾的 ASCII 行。读取时使用较短的 ReadTimeout，便于及时响应取消。
        var buf = new System.Text.StringBuilder(256);
        var byteBuf = new byte[256];
        int prevReadTimeout = -1;
        try
        {
            try { prevReadTimeout = _port.ReadTimeout; } catch { /* ignore */ }
            try { _port.ReadTimeout = 100; } catch { /* ignore */ }
            while (!ct.IsCancellationRequested && _silentMode && _port.IsOpen)
            {
                int n;
                try
                {
                    n = _port.Read(byteBuf, 0, byteBuf.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch
                {
                    break;
                }

                for (int i = 0; i < n; i++)
                {
                    byte b = byteBuf[i];
                    if (b == (byte)'\n')
                    {
                        // 去除末尾 \r
                        string line = buf.ToString();
                        if (line.Length > 0 && line[^1] == '\r')
                        {
                            line = line.Substring(0, line.Length - 1);
                        }
                        buf.Clear();
                        if (line.Length > 0)
                        {
                            try { SilentFrameReceived?.Invoke(line); }
                            catch { /* swallow handler exceptions */ }
                        }
                    }
                    else if (b != 0)
                    {
                        // 简单防爆：单行最长 8KB
                        if (buf.Length < 8192)
                        {
                            buf.Append((char)b);
                        }
                        else
                        {
                            buf.Clear();
                        }
                    }
                }
            }
        }
        finally
        {
            if (prevReadTimeout > 0)
            {
                try { _port.ReadTimeout = prevReadTimeout; } catch { /* ignore */ }
            }
        }
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
        {
            return false;
        }

        value = ConvertFromBigEndian<T>(data);
        return true;
    }

    /// <summary>写入一个 SDO 风格参数。1 寄存器走 0x06，多寄存器走 0x10。</summary>
    public bool TryWriteSDO<T>(int slaveAddr, ushort index, byte subIndex, T value) where T : struct
    {
        // 厂家页禁用记忆：禁用后所有页面写入均拒绝
        if (RegisterDisableService.IsDisabled(ProtocolStack.Modbus, index, subIndex))
        {
            LastException = ModbusExceptionCode.IllegalDataAddress;
            return false;
        }
        byte[] payload = ConvertToBigEndian(value);
        ushort addr = (ushort)MapToModbusAddress(index, subIndex);
        int regs = payload.Length / 2;
        if (regs == 1)
        {
            return WriteSingleRegister((byte)slaveAddr, addr, (ushort)((payload[0] << 8) | payload[1]));
        }

        return WriteMultipleRegisters((byte)slaveAddr, addr, (ushort)regs, payload);
    }

    /// <summary>读取参数；失败抛异常（与 EtherCATMaster.ReadSDO 相同形态）。</summary>
    public T ReadSDO<T>(int slaveAddr, int index, int subIndex) where T : struct
    {
        if (TryReadSDO(slaveAddr, (ushort)index, (byte)subIndex, out T v))
        {
            return v;
        }

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
        byte[] request = BuildRequestFrame(ModbusFunctionCode.ReadHoldingRegisters, slave, startAddr, count, count, payload: null);
        if (!Transact(slave, ModbusFunctionCode.ReadHoldingRegisters, request, count * 2, out _, out FrameRuntimeParseResult? parseResult)
            || parseResult is null)
        {
            return false;
        }

        if (!TryReadSegmentUInt32(parseResult, FrameRuntimeKeys.ModbusByteCount, out uint byteCount)
            || byteCount != (uint)(count * 2))
        {
            LastException = ModbusExceptionCode.InvalidResponse;
            return false;
        }

        if (!TryReadSegmentBytes(parseResult, FrameRuntimeKeys.ModbusPayload, out data)
            || data.Length != count * 2)
        {
            data = [];
            LastException = ModbusExceptionCode.InvalidResponse;
            return false;
        }

        return true;
    }

    /// <summary>0x06 写单个保持寄存器。</summary>
    public bool WriteSingleRegister(byte slave, ushort addr, ushort value)
    {
        byte[] request = BuildRequestFrame(ModbusFunctionCode.WriteSingleRegister, slave, addr, value, 1, payload: null);
        if (!Transact(slave, ModbusFunctionCode.WriteSingleRegister, request, 0, out _, out FrameRuntimeParseResult? parseResult)
            || parseResult is null)
        {
            return false;
        }

        if (!TryReadSegmentUInt32(parseResult, FrameRuntimeKeys.ModbusStartAddress, out uint echoedAddress)
            || echoedAddress != addr
            || !TryReadSegmentUInt32(parseResult, FrameRuntimeKeys.ModbusWriteValue, out uint echoedValue)
            || echoedValue != value)
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

        byte[] request = BuildRequestFrame(ModbusFunctionCode.WriteMultipleRegisters, slave, startAddr, 0, regCount, payload);
        if (!Transact(slave, ModbusFunctionCode.WriteMultipleRegisters, request, 0, out _, out FrameRuntimeParseResult? parseResult)
            || parseResult is null)
        {
            return false;
        }

        if (!TryReadSegmentUInt32(parseResult, FrameRuntimeKeys.ModbusStartAddress, out uint echoedAddress)
            || echoedAddress != startAddr
            || !TryReadSegmentUInt32(parseResult, FrameRuntimeKeys.ModbusWriteRegisterCount, out uint echoedRegisterCount)
            || echoedRegisterCount != regCount)
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
    private bool Transact(
        byte expectedSlave,
        ModbusFunctionCode functionCode,
        byte[] request,
        int expectedPayloadByteCount,
        out byte[]? response,
        out FrameRuntimeParseResult? parseResult)
    {
        response = null;
        parseResult = null;
        if (_silentMode)
        {
            // 静默模式：主机停止下发，所有事务立即视为端口不可用。
            LastException = ModbusExceptionCode.PortClosed;
            return false;
        }
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
                    {
                        _port.DiscardInBuffer();
                    }

                    // —— 自适应帧间静默 ——
                    // 真实 Modbus RTU 设备只要求“相邻两帧之间 ≥ 3.5 字符”静默；
                    // 我们已经在 TryReceive 末尾记录了上次接收完成时刻，
                    // 因此只需补足剩余的静默时间，而非每帧都固定睡眠 5 ms。
                    EnsureInterFrameSilence();

                    FrameFormatRuntimeService.RecordRawFrame(FrameProtocolStack.Modbus, FrameDirection.Send, (byte)functionCode, request);
                    _port.Write(request, 0, request.Length);
                    long txEndTicks = Stopwatch.GetTimestamp();
                    LastTxFrame = (byte[])request.Clone();

                    if (TryReceive(expectedSlave, functionCode, expectedPayloadByteCount, txEndTicks, out response, out parseResult, out ModbusExceptionCode err))
                    {
                        LastRxFrame = response ?? [];
                        LastException = ModbusExceptionCode.None;
                        return true;
                    }

                    LastRxFrame = [];
                    LastException = err;
                    response = null;

                    // 协议级 IllegalDataAddress 等不再重试，节省时间
                    if (err == ModbusExceptionCode.IllegalFunction
                        || err == ModbusExceptionCode.IllegalDataAddress
                        || err == ModbusExceptionCode.IllegalDataValue)
                    {
                        return false;
                    }
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

    /// <summary>读取一帧应答。<paramref name="expectedPayloadByteCount"/> 为正常应答中可变数据区的字节数。
    /// <paramref name="txEndTicks"/> 为 Transact 刚刚完成发送的高精度时间戳，用于计算主机发送结束 → 从机首字节到达的延时。</summary>
    private bool TryReceive(
        byte expectSlave,
        ModbusFunctionCode expectFunc,
        int expectedPayloadByteCount,
        long txEndTicks,
        out byte[]? frame,
        out FrameRuntimeParseResult? parseResult,
        out ModbusExceptionCode err)
    {
        frame = null;
        parseResult = null;
        err = ModbusExceptionCode.None;

        FrameRuntimeFormat responseFormat = FrameFormatRuntimeService.GetModbusFormat(expectFunc, FrameDirection.Response);
        int normalFrameLength = Math.Max(responseFormat.GetByteCount(expectedPayloadByteCount), responseFormat.MinimumByteCount);
        if (normalFrameLength < 5)
        {
            err = ModbusExceptionCode.InvalidResponse;
            return false;
        }

        int prefixLength = TryGetFieldEndOffset(responseFormat, FrameRuntimeKeys.ModbusFunctionCode, expectedPayloadByteCount, out int functionFieldEnd)
            ? functionFieldEnd
            : Math.Min(2, normalFrameLength);
        prefixLength = Math.Clamp(prefixLength, 1, normalFrameLength);

        var buffer = new byte[normalFrameLength];
        if (!ReadFully(buffer, 0, prefixLength, out _, out long firstByteTicks))
        {
            err = ModbusExceptionCode.Timeout;
            return false;
        }

        // 主机发送结束 → 从机首字节到达的间隔（ms）。只要成功读到首字节，该指标即有效。
        long deltaTicks = firstByteTicks - txEndTicks;
        if (deltaTicks < 0)
        {
            deltaTicks = 0;
        }

        LastResponseLatencyMs = deltaTicks * 1000.0 / Stopwatch.Frequency;

        if (!TryReadRawFieldByte(responseFormat, buffer, prefixLength, expectedPayloadByteCount, FrameRuntimeKeys.ModbusFunctionCode, out byte actualFunctionCode))
        {
            err = ModbusExceptionCode.FunctionMismatch;
            return false;
        }

        bool isException = (actualFunctionCode & 0x80) != 0;
        if (isException)
        {
            const int exceptionFrameLength = 5;
            if (prefixLength < exceptionFrameLength
                && !ReadFully(buffer, prefixLength, exceptionFrameLength - prefixLength, out _, out _))
            {
                err = ModbusExceptionCode.Timeout;
                return false;
            }

            byte[] excFrame = buffer[..exceptionFrameLength];
            if (!CheckCrc(excFrame))
            {
                err = ModbusExceptionCode.CrcError;
                return false;
            }

            err = excFrame.Length >= 3 ? (ModbusExceptionCode)excFrame[2] : ModbusExceptionCode.InvalidResponse;
            _lastRxFinishTicks = Stopwatch.GetTimestamp();
            return false;
        }

        if (prefixLength < normalFrameLength
            && !ReadFully(buffer, prefixLength, normalFrameLength - prefixLength, out _, out _))
        {
            err = ModbusExceptionCode.Timeout;
            return false;
        }

        byte[] full = normalFrameLength == buffer.Length ? buffer : buffer[..normalFrameLength];

        if (!CheckCrc(full))
        {
            err = ModbusExceptionCode.CrcError;
            return false;
        }

        parseResult = FrameFormatRuntimeService.RecordRawFrame(FrameProtocolStack.Modbus, FrameDirection.Response, (byte)expectFunc, full);
        if (!TryValidateResponseEnvelope(parseResult, expectSlave, expectFunc, out err))
        {
            parseResult = null;
            return false;
        }

        frame = full;
        // 记录接收完成时刻，供下一帧计算 3.5 字符静默间隔
        _lastRxFinishTicks = Stopwatch.GetTimestamp();
        return true;
    }

    private static byte[] BuildRequestFrame(
        ModbusFunctionCode functionCode,
        byte slave,
        ushort startAddr,
        ushort quantityOrValue,
        ushort regCount,
        byte[]? payload)
    {
        FrameRuntimeFormat format = FrameFormatRuntimeService.GetModbusFormat(functionCode, FrameDirection.Send);
        int variableLength = payload?.Length ?? 0;
        var frameBytes = new List<byte>(Math.Max(format.GetByteCount(variableLength), 8));

        foreach (FrameRuntimeField field in format.Fields)
        {
            frameBytes.AddRange(BuildRequestFieldBytes(field, frameBytes, functionCode, slave, startAddr, quantityOrValue, regCount, payload));
        }

        return [.. frameBytes];
    }

    private static byte[] BuildRequestFieldBytes(
        FrameRuntimeField field,
        List<byte> currentFrame,
        ModbusFunctionCode functionCode,
        byte slave,
        ushort startAddr,
        ushort quantityOrValue,
        ushort regCount,
        byte[]? payload)
    {
        return field.RuntimeKey switch
        {
            FrameRuntimeKeys.ModbusSlaveAddress => FitFieldBytes([slave], field),
            FrameRuntimeKeys.ModbusFunctionCode => FitFieldBytes([(byte)functionCode], field),
            FrameRuntimeKeys.ModbusStartAddress => FitFieldBytes(EncodeBigEndian(startAddr, Math.Max(1, field.ByteCount)), field),
            FrameRuntimeKeys.ModbusReadRegisterCount => FitFieldBytes(EncodeBigEndian(quantityOrValue, Math.Max(1, field.ByteCount)), field),
            FrameRuntimeKeys.ModbusWriteValue => FitFieldBytes(EncodeBigEndian(quantityOrValue, Math.Max(1, field.ByteCount)), field),
            FrameRuntimeKeys.ModbusWriteRegisterCount => FitFieldBytes(EncodeBigEndian(regCount, Math.Max(1, field.ByteCount)), field),
            FrameRuntimeKeys.ModbusByteCount => FitFieldBytes(EncodeBigEndian((uint)(payload?.Length ?? 0), Math.Max(1, field.ByteCount)), field),
            FrameRuntimeKeys.ModbusPayload => payload is not null ? FitVariableBytes(payload, field) : CreatePadding(field, 0),
            FrameRuntimeKeys.ModbusCrc => FitFieldBytes(ComputeCrcBytes([.. currentFrame]), field),
            _ => CreatePadding(field, payload?.Length ?? 0),
        };
    }

    private static byte[] FitFieldBytes(byte[] source, FrameRuntimeField field)
    {
        if (field.IsVariableLength)
        {
            return source;
        }

        int targetLength = Math.Max(1, field.ByteCount);
        if (source.Length == targetLength)
        {
            return source;
        }

        if (source.Length > targetLength)
        {
            return source[^targetLength..];
        }

        var result = new byte[targetLength];
        Buffer.BlockCopy(source, 0, result, targetLength - source.Length, source.Length);
        return result;
    }

    private static byte[] FitVariableBytes(byte[] source, FrameRuntimeField field)
        => field.IsVariableLength ? source : FitFieldBytes(source, field);

    private static byte[] CreatePadding(FrameRuntimeField field, int variableLength)
    {
        int length = field.IsVariableLength ? Math.Max(0, variableLength) : Math.Max(1, field.ByteCount);
        return length == 0 ? [] : new byte[length];
    }

    private static byte[] EncodeBigEndian(uint value, int width)
    {
        int actualWidth = Math.Max(1, width);
        var bytes = new byte[actualWidth];
        for (int i = 0; i < actualWidth; i++)
        {
            int shift = (actualWidth - 1 - i) * 8;
            bytes[i] = (byte)(value >> shift);
        }

        return bytes;
    }

    private static byte[] EncodeBigEndian(ushort value, int width)
        => EncodeBigEndian((uint)value, width);

    private static byte[] ComputeCrcBytes(byte[] frame)
        => CRC16_modbus.CRC16.CRCCalc(frame);

    private static bool TryValidateResponseEnvelope(
        FrameRuntimeParseResult parseResult,
        byte expectedSlave,
        ModbusFunctionCode expectedFunction,
        out ModbusExceptionCode err)
    {
        err = ModbusExceptionCode.None;

        if (!TryReadSegmentUInt32(parseResult, FrameRuntimeKeys.ModbusSlaveAddress, out uint slaveAddress)
            || slaveAddress != expectedSlave)
        {
            err = ModbusExceptionCode.SlaveAddressMismatch;
            return false;
        }

        if (!TryReadSegmentUInt32(parseResult, FrameRuntimeKeys.ModbusFunctionCode, out uint functionCode)
            || (functionCode & 0x7F) != (byte)expectedFunction)
        {
            err = ModbusExceptionCode.FunctionMismatch;
            return false;
        }

        return true;
    }

    private static bool TryReadSegmentBytes(FrameRuntimeParseResult parseResult, string runtimeKey, out byte[] bytes)
    {
        if (parseResult.TryGetSegment(runtimeKey, out FrameRuntimeSegment segment) && segment.IsComplete)
        {
            bytes = segment.Bytes;
            return true;
        }

        bytes = [];
        return false;
    }

    private static bool TryReadSegmentUInt32(FrameRuntimeParseResult parseResult, string runtimeKey, out uint value)
    {
        value = 0;
        if (!TryReadSegmentBytes(parseResult, runtimeKey, out byte[] bytes) || bytes.Length == 0)
        {
            return false;
        }

        foreach (byte current in bytes)
        {
            value = (value << 8) | current;
        }

        return true;
    }

    private static bool TryGetFieldEndOffset(FrameRuntimeFormat format, string runtimeKey, int variableLength, out int endOffset)
    {
        if (format.TryGetFieldOffset(runtimeKey, variableLength, out int offset, out FrameRuntimeField field))
        {
            endOffset = offset + Math.Max(1, field.ByteCount);
            return true;
        }

        endOffset = -1;
        return false;
    }

    private static bool TryReadRawFieldByte(
        FrameRuntimeFormat format,
        byte[] buffer,
        int availableBytes,
        int variableLength,
        string runtimeKey,
        out byte value)
    {
        value = 0;
        if (!format.TryGetFieldOffset(runtimeKey, variableLength, out int offset, out FrameRuntimeField field))
        {
            return false;
        }

        int fieldLength = Math.Max(1, field.ByteCount);
        if (availableBytes < offset + fieldLength)
        {
            return false;
        }

        value = buffer[offset + fieldLength - 1];
        return true;
    }

    /// <summary>带超时的精确读：必须读满 <paramref name="count"/> 字节才返回 true。
    /// <paramref name="firstByteTicks"/> 在首字节返回时记录为 Stopwatch.GetTimestamp()。</summary>
    private bool ReadFully(byte[] buf, int offset, int count, out int read, out long firstByteTicks)
    {
        read = 0;
        firstByteTicks = 0;
        var sw = Stopwatch.StartNew();
        while (read < count)
        {
            int got = _port.Read(buf, offset + read, count - read);
            if (got <= 0)
            {
                if (sw.ElapsedMilliseconds > ResponseTimeoutMs)
                {
                    return false;
                }

                continue;
            }

            if (read == 0)
            {
                firstByteTicks = Stopwatch.GetTimestamp();
            }

            read += got;
        }

        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  帧间静默控制
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>返回 Modbus RTU 3.5 字符间隔对应的毫秒数。按 1 帧 = 11 位计（1 起始 + 8 数据 + 1 奇偶校验 + 1 停止）。</summary>
    private double ComputeT35Ms()
    {
        int baud = _port.BaudRate;
        if (baud <= 0)
        {
            return 2.0;
        }
        // 3.5 字符 = 3.5 * 11 / baud 秒 = 38500 / baud 毫秒
        return 38500.0 / baud;
    }

    /// <summary>保证上一帧接收完成与本帧发送之间至少隔 max(<see cref="InterFrameDelayMs"/>, 3.5字符)。</summary>
    private void EnsureInterFrameSilence()
    {
        double requiredMs = ComputeT35Ms();
        if (InterFrameDelayMs > 0 && InterFrameDelayMs > requiredMs)
        {
            requiredMs = InterFrameDelayMs;
        }

        if (_lastRxFinishTicks == 0)
        {
            // 首帧：只需保证静默下限
            if (requiredMs >= 0.5)
            {
                Thread.Sleep((int)Math.Ceiling(requiredMs));
            }

            return;
        }

        long elapsedTicks = Stopwatch.GetTimestamp() - _lastRxFinishTicks;
        double elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        double remainMs = requiredMs - elapsedMs;
        if (remainMs >= 0.5)
        {
            Thread.Sleep((int)Math.Ceiling(remainMs));
        }
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
        if (frame.Length < 3)
        {
            return false;
        }

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
        {
            return 1;
        }

        if (t == typeof(uint) || t == typeof(int) || t == typeof(float))
        {
            return 2;
        }

        throw new NotSupportedException($"Modbus SDO 暂不支持类型: {t.Name}");
    }

    private static T ConvertFromBigEndian<T>(byte[] data) where T : struct
    {
        Type t = typeof(T);
        if (t == typeof(byte))
        {
            return (T)(object)data[1];
        }

        if (t == typeof(sbyte))
        {
            return (T)(object)unchecked((sbyte)data[1]);
        }

        if (t == typeof(ushort))
        {
            return (T)(object)(ushort)((data[0] << 8) | data[1]);
        }

        if (t == typeof(short))
        {
            return (T)(object)unchecked((short)((data[0] << 8) | data[1]));
        }

        if (t == typeof(uint))
        {
            return (T)(object)(uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
        }

        if (t == typeof(int))
        {
            return (T)(object)unchecked((int)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]));
        }

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
