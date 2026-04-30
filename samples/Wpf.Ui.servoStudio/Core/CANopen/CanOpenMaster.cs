// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Wpf.Ui.servoStudio.Core;

namespace Core.CANopen;

/// <summary>
/// CANopen 主站。基于 <see cref="ICanBus"/> 与从机通讯，向上实现 <see cref="IServoMaster"/>，
/// 与 <c>EtherCATMaster</c>、<c>ModbusRtuMaster</c> 平行。<br/>
/// 已实现：
/// <list type="bullet">
///   <item><description>SDO 加速读 (CS=0x40 → 0x4x)</description></item>
///   <item><description>SDO 加速写 (CS=0x22/0x23/0x27/0x2B/0x2F → 0x60)</description></item>
///   <item><description>NMT 控制 (Start/Stop/PreOp/Reset)</description></item>
///   <item><description>0x080 紧急对象 (Emcy) / 0x700+id 心跳事件</description></item>
/// </list>
/// 暂未实现：分段 SDO（&gt;4 字节）、PDO 映射 —— 标准 CiA402 参数全部 ≤4 字节，已满足。
/// </summary>
public class CanOpenMaster : IDisposable, IServoMaster
{
    private readonly ICanBus _bus;
    private readonly Lock _txLock = new();
    private Thread? _dispatchThread;
    private CancellationTokenSource? _dispatchCts;
    private bool _disposed;

    // 等待 SDO 应答的同步对象（按 nodeId 索引；同一 nodeId 同一时刻只允许一笔 SDO）
    private readonly ConcurrentDictionary<byte, SdoPending> _pending = new();

    /// <summary>SDO 应答超时 (ms)。</summary>
    public int SdoTimeoutMs { get; set; } = 500;

    /// <summary>SDO 失败重试次数（不含首次）。</summary>
    public int RetryCount { get; set; } = 1;

    /// <summary>最近一次操作的失败码。</summary>
    public SdoAbortCode LastAbort { get; private set; } = SdoAbortCode.None;

    /// <summary>底层总线（仅供诊断 / 单元测试使用）。</summary>
    public ICanBus Bus => _bus;

    /// <summary>是否已开始监听总线。</summary>
    public bool IsRunning => _dispatchThread is not null && _bus.IsOpen;

    /// <summary>新心跳事件（0x700+nodeId）：(nodeId, NmtState)。</summary>
    public event Action<byte, NmtState>? Heartbeat;

    /// <summary>紧急对象事件（0x080+nodeId）：(nodeId, errorCode, errorRegister, vendorBytes[5])。</summary>
    public event Action<byte, ushort, byte, byte[]>? Emergency;

    public CanOpenMaster(ICanBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    }

    /// <summary>启动总线监听。<see cref="ICanBus.Open(CanBitrate)"/> 之后调用。</summary>
    public void Start()
    {
        if (IsRunning) return;
        _dispatchCts = new CancellationTokenSource();
        _dispatchThread = new Thread(DispatchLoop)
        {
            IsBackground = true,
            Name = "CanOpenMaster-Dispatch",
        };
        _dispatchThread.Start();
    }

    /// <summary>停止总线监听（不关闭底层 <see cref="ICanBus"/>）。</summary>
    public void Stop()
    {
        try { _dispatchCts?.Cancel(); } catch { /* ignore */ }
        try { _dispatchThread?.Join(200); } catch { /* ignore */ }
        _dispatchThread = null;
        _dispatchCts?.Dispose();
        _dispatchCts = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _bus.Dispose(); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  IServoMaster — SDO 风格 API
    // ════════════════════════════════════════════════════════════════════════

    public bool TryReadSDO<T>(int slaveAddr, ushort index, byte subIndex, out T value) where T : struct
    {
        value = default;
        if (!IsRunning) { LastAbort = SdoAbortCode.LocalBusClosed; return false; }
        byte node = (byte)(slaveAddr & 0x7F);
        if (node is < 1 or > 0x7F) { LastAbort = SdoAbortCode.InvalidValue; return false; }

        int retries = Math.Max(0, RetryCount);
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            if (DoSdoUpload(node, index, subIndex, out byte[] data, out SdoAbortCode err))
            {
                value = SdoCodec.Decode<T>(data);
                LastAbort = SdoAbortCode.None;
                return true;
            }
            LastAbort = err;
            if (IsHardError(err)) return false;
        }
        return false;
    }

    public bool TryWriteSDO<T>(int slaveAddr, ushort index, byte subIndex, T value) where T : struct
    {
        if (!IsRunning) { LastAbort = SdoAbortCode.LocalBusClosed; return false; }
        byte node = (byte)(slaveAddr & 0x7F);
        if (node is < 1 or > 0x7F) { LastAbort = SdoAbortCode.InvalidValue; return false; }

        byte[] payload = SdoCodec.Encode(value, out int validBytes);
        int retries = Math.Max(0, RetryCount);
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            if (DoSdoDownload(node, index, subIndex, payload, validBytes, out SdoAbortCode err))
            {
                LastAbort = SdoAbortCode.None;
                return true;
            }
            LastAbort = err;
            if (IsHardError(err)) return false;
        }
        return false;
    }

    public T ReadSDO<T>(int slaveAddr, int index, int subIndex) where T : struct
    {
        if (TryReadSDO(slaveAddr, (ushort)index, (byte)subIndex, out T v))
            return v;
        throw new InvalidOperationException(
            $"CANopen ReadSDO 失败：node={slaveAddr}, idx=0x{index:X4}/{subIndex}, abort=0x{(uint)LastAbort:X8}");
    }

    private static bool IsHardError(SdoAbortCode c)
        => c == SdoAbortCode.ObjectDoesNotExist
        || c == SdoAbortCode.SubIndexDoesNotExist
        || c == SdoAbortCode.UnsupportedAccess
        || c == SdoAbortCode.AttemptReadWriteOnly
        || c == SdoAbortCode.AttemptWriteReadOnly;

    // ════════════════════════════════════════════════════════════════════════
    //  NMT
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>发送 NMT 命令。<paramref name="nodeId"/> 为 0 时广播。</summary>
    public bool SendNmt(NmtCommand cmd, byte nodeId = 0)
    {
        if (!IsRunning) return false;
        var payload = new byte[2] { (byte)cmd, nodeId };
        return _bus.Send(new CanFrame(0x000, payload));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  SDO 协议核心
    // ════════════════════════════════════════════════════════════════════════

    private bool DoSdoUpload(byte node, ushort index, byte subIndex, out byte[] data, out SdoAbortCode err)
    {
        data = [];
        err = SdoAbortCode.None;
        var pend = new SdoPending(index, subIndex);
        if (!_pending.TryAdd(node, pend))
        {
            err = SdoAbortCode.GeneralInternalIncompatibility;
            return false;
        }

        try
        {
            ushort tx = (ushort)(0x600 + node);
            CanFrame req = CanFrame.Sdo(tx, 0x40, index, subIndex);

            lock (_txLock)
            {
                if (!_bus.Send(req))
                {
                    err = SdoAbortCode.LocalBusClosed;
                    return false;
                }
            }

            if (!pend.WaitFor(SdoTimeoutMs))
            {
                err = SdoAbortCode.LocalTimeout;
                return false;
            }

            if (pend.AbortCode != SdoAbortCode.None)
            {
                err = pend.AbortCode;
                return false;
            }

            data = pend.Payload ?? [];
            return true;
        }
        finally
        {
            _pending.TryRemove(node, out _);
        }
    }

    private bool DoSdoDownload(byte node, ushort index, byte subIndex, byte[] payload, int validBytes, out SdoAbortCode err)
    {
        err = SdoAbortCode.None;
        if (validBytes is < 1 or > 4)
        {
            err = SdoAbortCode.LocalUnsupportedDataLength;
            return false;
        }

        var pend = new SdoPending(index, subIndex);
        if (!_pending.TryAdd(node, pend))
        {
            err = SdoAbortCode.GeneralInternalIncompatibility;
            return false;
        }

        try
        {
            // CS = 0010 nnes，n=空字节数(=4-validBytes)，e=1 加速，s=1 数据长度有效
            byte cs = (byte)(0x23 | (((4 - validBytes) & 0x03) << 2));

            uint d32 = 0;
            for (int i = 0; i < validBytes; i++)
                d32 |= (uint)payload[i] << (i * 8);

            ushort tx = (ushort)(0x600 + node);
            CanFrame req = CanFrame.Sdo(tx, cs, index, subIndex, d32);

            lock (_txLock)
            {
                if (!_bus.Send(req))
                {
                    err = SdoAbortCode.LocalBusClosed;
                    return false;
                }
            }

            if (!pend.WaitFor(SdoTimeoutMs))
            {
                err = SdoAbortCode.LocalTimeout;
                return false;
            }

            if (pend.AbortCode != SdoAbortCode.None)
            {
                err = pend.AbortCode;
                return false;
            }

            return true;
        }
        finally
        {
            _pending.TryRemove(node, out _);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  接收派发循环
    // ════════════════════════════════════════════════════════════════════════

    private void DispatchLoop()
    {
        CancellationToken token = _dispatchCts!.Token;
        while (!token.IsCancellationRequested)
        {
            if (!_bus.TryReceive(50, out CanFrame f))
                continue;

            ushort cob = f.Id;
            ushort fc = (ushort)(cob & 0x780); // function code
            byte node = (byte)(cob & 0x7F);

            switch (fc)
            {
                case 0x580: // SDO Tx (server → client)
                    HandleSdoResponse(node, f);
                    break;

                case 0x080: // EMCY
                    if (f.Dlc >= 8)
                    {
                        ushort code = (ushort)(f.Data[0] | (f.Data[1] << 8));
                        byte reg = f.Data[2];
                        var vendor = new byte[5];
                        Buffer.BlockCopy(f.Data, 3, vendor, 0, 5);
                        try { Emergency?.Invoke(node, code, reg, vendor); } catch { /* ignore */ }
                    }
                    break;

                case 0x700: // Heartbeat / NMT state
                    if (f.Dlc >= 1)
                    {
                        byte raw = (byte)(f.Data[0] & 0x7F);
                        var st = raw switch
                        {
                            0x00 => NmtState.BootUp,
                            0x04 => NmtState.Stopped,
                            0x05 => NmtState.Operational,
                            0x7F => NmtState.PreOperational,
                            _    => NmtState.Unknown,
                        };
                        try { Heartbeat?.Invoke(node, st); } catch { /* ignore */ }
                    }
                    break;
            }
        }
    }

    private void HandleSdoResponse(byte node, CanFrame f)
    {
        if (!_pending.TryGetValue(node, out SdoPending? pend) || pend is null)
            return;

        if (f.Dlc < 8) return;
        byte cs = f.Data[0];
        ushort idx = (ushort)(f.Data[1] | (f.Data[2] << 8));
        byte sub = f.Data[3];

        // 只接收当前期望的 (idx, sub) 应答
        if (idx != pend.Index || sub != pend.SubIndex) return;

        if (cs == 0x80)
        {
            // 异常应答
            uint code = (uint)(f.Data[4] | (f.Data[5] << 8) | (f.Data[6] << 16) | (f.Data[7] << 24));
            pend.AbortCode = (SdoAbortCode)code;
            pend.Signal();
            return;
        }

        // 上传应答 0x4n（加速、含尺寸）或 0x42（不含尺寸）
        if ((cs & 0xE0) == 0x40)
        {
            int n = (cs & 0x0C) >> 2; // 高 n 字节为空
            int valid = (cs & 0x02) != 0 ? 4 - n : 4;  // size indicator
            if ((cs & 0x01) == 0)
            {
                // e=0：分段传输 —— 暂不支持，按异常返回
                pend.AbortCode = SdoAbortCode.LocalUnsupportedDataLength;
                pend.Signal();
                return;
            }
            valid = Math.Clamp(valid, 1, 4);
            var data = new byte[valid];
            Buffer.BlockCopy(f.Data, 4, data, 0, valid);
            pend.Payload = data;
            pend.Signal();
            return;
        }

        // 下载应答 0x60
        if (cs == 0x60)
        {
            pend.Signal();
            return;
        }

        // 其它命令字（分段、块等）暂不处理
        pend.AbortCode = SdoAbortCode.LocalInvalidResponse;
        pend.Signal();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  辅助类型
    // ════════════════════════════════════════════════════════════════════════

    private sealed class SdoPending(ushort index, byte subIndex)
    {
        private readonly ManualResetEventSlim _evt = new(false);

        public ushort Index { get; } = index;
        public byte SubIndex { get; } = subIndex;
        public byte[]? Payload { get; set; }
        public SdoAbortCode AbortCode { get; set; } = SdoAbortCode.None;

        public bool WaitFor(int timeoutMs) => _evt.Wait(timeoutMs);
        public void Signal() => _evt.Set();
    }
}

/// <summary>
/// 为 SDO 加速传输（≤4 字节）做类型 ↔ 字节流转换。CANopen 数据为小端。
/// </summary>
internal static class SdoCodec
{
    public static T Decode<T>(byte[] data) where T : struct
    {
        Span<byte> b = stackalloc byte[4];
        for (int i = 0; i < Math.Min(data.Length, 4); i++) b[i] = data[i];

        Type t = typeof(T);
        if (t == typeof(byte))   return (T)(object)b[0];
        if (t == typeof(sbyte))  return (T)(object)unchecked((sbyte)b[0]);
        if (t == typeof(ushort)) return (T)(object)(ushort)(b[0] | (b[1] << 8));
        if (t == typeof(short))  return (T)(object)unchecked((short)(b[0] | (b[1] << 8)));
        if (t == typeof(uint))   return (T)(object)(uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
        if (t == typeof(int))    return (T)(object)unchecked((int)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24)));
        if (t == typeof(float))
        {
            uint u = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
            return (T)(object)BitConverter.UInt32BitsToSingle(u);
        }
        throw new NotSupportedException($"CANopen SDO 暂不支持类型: {t.Name}");
    }

    public static byte[] Encode<T>(T value, out int validBytes) where T : struct
    {
        var buf = new byte[4];
        switch (value)
        {
            case byte by:
                buf[0] = by; validBytes = 1; return buf;
            case sbyte sb:
                buf[0] = unchecked((byte)sb); validBytes = 1; return buf;
            case ushort us:
                buf[0] = (byte)(us & 0xFF); buf[1] = (byte)(us >> 8); validBytes = 2; return buf;
            case short s:
                ushort us2 = unchecked((ushort)s);
                buf[0] = (byte)(us2 & 0xFF); buf[1] = (byte)(us2 >> 8); validBytes = 2; return buf;
            case uint u:
                buf[0] = (byte)(u & 0xFF); buf[1] = (byte)((u >> 8) & 0xFF);
                buf[2] = (byte)((u >> 16) & 0xFF); buf[3] = (byte)((u >> 24) & 0xFF);
                validBytes = 4; return buf;
            case int i:
                uint u2 = unchecked((uint)i);
                buf[0] = (byte)(u2 & 0xFF); buf[1] = (byte)((u2 >> 8) & 0xFF);
                buf[2] = (byte)((u2 >> 16) & 0xFF); buf[3] = (byte)((u2 >> 24) & 0xFF);
                validBytes = 4; return buf;
            case float f:
                uint u3 = BitConverter.SingleToUInt32Bits(f);
                buf[0] = (byte)(u3 & 0xFF); buf[1] = (byte)((u3 >> 8) & 0xFF);
                buf[2] = (byte)((u3 >> 16) & 0xFF); buf[3] = (byte)((u3 >> 24) & 0xFF);
                validBytes = 4; return buf;
            default:
                throw new NotSupportedException($"CANopen SDO 暂不支持类型: {typeof(T).Name}");
        }
    }
}
