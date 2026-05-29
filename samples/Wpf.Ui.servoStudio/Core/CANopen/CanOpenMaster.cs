// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Core.Sync;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.Services;

namespace Core.CANopen;

/// <summary>
/// CANopen 主站。基于 <see cref="ICanBus"/> 与从机通讯，向上实现 <see cref="IServoMaster"/>，
/// 与 <c>EtherCATMaster</c>、<c>ModbusRtuMaster</c> 平行。<br/>
/// 已实现：
/// <list type="bullet">
///   <item><description>SDO 加速读 (CS=0x40 → 0x4x)</description></item>
///   <item><description>SDO 加速写 (CS=0x22/0x23/0x27/0x2B/0x2F → 0x60)</description></item>
///   <item><description>NMT 控制 (Start/Stop/PreOp/Reset)</description></item>
///   <item><description>0x080 SYNC 生产者 / 接收</description></item>
///   <item><description>0x081–0x0FF 紧急对象 (Emcy) / 0x700+id 心跳事件</description></item>
///   <item><description>PDO 映射配置 (0x1400/0x1600/0x1800/0x1A00) + RPDO 发送 + TPDO 接收事件</description></item>
/// </list>
/// 暂未实现：分段 SDO（&gt;4 字节）—— 标准 CiA402 参数全部 ≤4 字节，已满足。
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

    /// <summary>紧急对象事件（0x081..0x0FF）：(nodeId, errorCode, errorRegister, vendorBytes[5])。</summary>
    public event Action<byte, ushort, byte, byte[]>? Emergency;

    /// <summary>SYNC 触发事件（每收到 0x080 帧时回调，可由订阅者用于本地 PDO 同步发送）。</summary>
    public event Action? SyncTick;

    /// <summary>TPDO 接收事件：(nodeId, tpdoIndex 0..3, payload)。<paramref name="tpdoIndex"/> 由 COB-ID 高位推断。</summary>
    public event Action<byte, byte, byte[]>? PdoReceived;

    // ─── SYNC 生产者 ────────────────────────────────────────────────────────
    private ICyclicTimer? _syncTimer;
    private byte _syncCounter;
    private bool _syncIncludeCounter;

    public CanOpenMaster(ICanBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    }

    /// <summary>启动总线监听。<see cref="ICanBus.Open(CanBitrate)"/> 之后调用。</summary>
    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

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
        StopSyncProducer();
        try { _dispatchCts?.Cancel(); } catch { /* ignore */ }
        try { _dispatchThread?.Join(200); } catch { /* ignore */ }
        _dispatchThread = null;
        _dispatchCts?.Dispose();
        _dispatchCts = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

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
        if (!IsRunning)
        {
            LastAbort = SdoAbortCode.LocalBusClosed;
            return false;
        }

        byte node = (byte)(slaveAddr & 0x7F);
        if (node is < 1 or > 0x7F)
        {
            LastAbort = SdoAbortCode.InvalidValue;
            return false;
        }

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
            if (IsHardError(err))
            {
                return false;
            }
        }

        return false;
    }

    public bool TryWriteSDO<T>(int slaveAddr, ushort index, byte subIndex, T value) where T : struct
    {
        // 厂家页禁用记忆：禁用后所有页面写入均拒绝
        if (RegisterDisableService.IsDisabled(ProtocolStack.CANopen, index, subIndex))
        {
            LastAbort = SdoAbortCode.AttemptWriteReadOnly;
            return false;
        }
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
            if (IsHardError(err))
            {
                return false;
            }
        }

        return false;
    }

    public T ReadSDO<T>(int slaveAddr, int index, int subIndex) where T : struct
    {
        if (TryReadSDO(slaveAddr, (ushort)index, (byte)subIndex, out T v))
        {
            return v;
        }

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
        if (!IsRunning)
        {
            return false;
        }

        CanFrame frame = CanOpenFrameCodec.CreateNmtFrame(cmd, nodeId);
        RecordRuntimeFrame(FrameDirection.Send, frame);
        return _bus.Send(frame);
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
            CanFrame req = CanOpenFrameCodec.CreateSdoFrame(tx, 0x40, index, subIndex, []);

            lock (_txLock)
            {
                RecordRuntimeFrame(FrameDirection.Send, req);
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

            ushort tx = (ushort)(0x600 + node);
            CanFrame req = CanOpenFrameCodec.CreateSdoFrame(tx, cs, index, subIndex, payload.AsSpan(0, validBytes));

            lock (_txLock)
            {
                RecordRuntimeFrame(FrameDirection.Send, req);
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
            {
                continue;
            }

            FrameRuntimeParseResult parseResult = RecordRuntimeFrame(FrameDirection.Response, f);
            ushort cob = f.Id;
            ushort fc = (ushort)(cob & 0x780); // function code
            byte node = (byte)(cob & 0x7F);

            switch (fc)
            {
                case 0x580: // SDO Tx (server → client)
                    HandleSdoResponse(node, parseResult);
                    break;

                case 0x080: // SYNC (cob==0x080) 或 EMCY (cob==0x081..0x0FF)
                    if (cob == 0x080)
                    {
                        // SYNC 帧：DLC=0 或 DLC=1（带计数器），不上抛 EMCY
                        try
                        {
                            SyncTick?.Invoke();
                        }
                        catch
                        {
                            /* ignore */
                        }
                    }
                    else if (f.Dlc >= 8)
                    {
                        ushort code = (ushort)(f.Data[0] | (f.Data[1] << 8));
                        byte reg = f.Data[2];
                        var vendor = new byte[5];
                        Buffer.BlockCopy(f.Data, 3, vendor, 0, 5);
                        try
                        {
                            Emergency?.Invoke(node, code, reg, vendor);
                        }
                        catch
                        {
                            /* ignore */
                        }
                    }

                    break;

                case 0x180: // TPDO1
                case 0x280: // TPDO2
                case 0x380: // TPDO3
                case 0x480: // TPDO4
                    DispatchTpdo(fc, node, f);
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
                            _ => NmtState.Unknown,
                        };
                        try
                        {
                            Heartbeat?.Invoke(node, st);
                        }
                        catch
                        {
                            /* ignore */
                        }
                    }

                    break;
            }
        }
    }

    private void HandleSdoResponse(byte node, FrameRuntimeParseResult parseResult)
    {
        if (!_pending.TryGetValue(node, out SdoPending? pend) || pend is null)
        {
            return;
        }

        if (!CanOpenFrameCodec.TryDecodeSdoResponse(parseResult, out byte cs, out ushort idx, out byte sub, out byte[] dataBytes))
        {
            return;
        }

        // 只接收当前期望的 (idx, sub) 应答
        if (idx != pend.Index || sub != pend.SubIndex)
        {
            return;
        }

        if (cs == 0x80)
        {
            // 异常应答
            uint code = (uint)(dataBytes[0] | (dataBytes[1] << 8) | (dataBytes[2] << 16) | (dataBytes[3] << 24));
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
            Buffer.BlockCopy(dataBytes, 0, data, 0, valid);
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

    private static FrameRuntimeParseResult RecordRuntimeFrame(FrameDirection direction, CanFrame frame)
    {
        byte[] bytes = CanOpenFrameCodec.ToCanonicalBytes(frame);
        return FrameFormatRuntimeService.RecordRawFrame(FrameProtocolStack.CANopen, direction, bytes);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  PDO 收发 / SYNC 生产者
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// CANopen 协议预定义 RPDO COB-ID 基址：RPDO i (i=0..3) 默认 = (0x200 + 0x100*i) + nodeId。
    /// </summary>
    private static readonly ushort[] RpdoBase = { 0x200, 0x300, 0x400, 0x500 };

    /// <summary>
    /// CANopen 协议预定义 TPDO COB-ID 基址：TPDO i (i=0..3) 默认 = (0x180 + 0x100*i) + nodeId。
    /// </summary>
    private static readonly ushort[] TpdoBase = { 0x180, 0x280, 0x380, 0x480 };

    /// <summary>
    /// 向 <paramref name="node"/> 的指定 RPDO 发送一帧 PDO 数据。<paramref name="rpdoIndex"/> 取值 0..3。<br/>
    /// 调用此方法前应已调用 <see cref="ConfigureRpdoMapping"/> 完成从机侧映射，并由 NMT 将从机置 Operational。
    /// </summary>
    public bool SendPdo(byte node, byte rpdoIndex, ReadOnlySpan<byte> payload)
    {
        if (!IsRunning)
        {
            return false;
        }

        if (rpdoIndex > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(rpdoIndex));
        }

        if (node is < 1 or > 0x7F)
        {
            throw new ArgumentOutOfRangeException(nameof(node));
        }

        if (payload.Length > 8)
        {
            throw new ArgumentException("PDO payload 最长 8 字节", nameof(payload));
        }

        ushort cob = (ushort)(RpdoBase[rpdoIndex] + node);
        CanFrame frame = CanOpenFrameCodec.CreatePdoFrame(cob, payload);
        RecordRuntimeFrame(FrameDirection.Send, frame);
        lock (_txLock)
        {
            return _bus.Send(frame);
        }
    }

    /// <summary>发送一个 CANopen SYNC 帧（COB-ID = 0x080）。</summary>
    public bool SendSync(byte? counter = null)
    {
        if (!IsRunning)
        {
            return false;
        }

        CanFrame frame = CanOpenFrameCodec.CreateSyncFrame(counter);
        RecordRuntimeFrame(FrameDirection.Send, frame);
        lock (_txLock)
        {
            return _bus.Send(frame);
        }
    }

    /// <summary>
    /// 启动 SYNC 生产者：以 <paramref name="period"/> 周期发送 0x080 帧。<br/>
    /// 重复调用会先停止旧定时器再启动新定时器。
    /// </summary>
    /// <param name="period">SYNC 周期。</param>
    /// <param name="timerKind">底层周期定时器类型。</param>
    /// <param name="includeCounter">是否在 SYNC 帧负载中包含 1 字节计数器（CiA301 可选）。</param>
    public void StartSyncProducer(TimeSpan period, CyclicTimerKind timerKind = CyclicTimerKind.PeriodicTimer, bool includeCounter = false)
    {
        StopSyncProducer();
        _syncIncludeCounter = includeCounter;
        _syncCounter = 0;
        ICyclicTimer t = CyclicTimerFactory.Create(timerKind);
        _syncTimer = t;
        t.Start(period, () =>
        {
            byte? c = null;
            if (_syncIncludeCounter)
            {
                _syncCounter++;
                if (_syncCounter == 0)
                {
                    _syncCounter = 1; // CiA301: counter 1..240
                }

                c = _syncCounter;
            }

            SendSync(c);
        });
    }

    /// <summary>停止 SYNC 生产者。</summary>
    public void StopSyncProducer()
    {
        try
        {
            _syncTimer?.Stop();
        }
        catch
        {
            /* ignore */
        }

        try
        {
            _syncTimer?.Dispose();
        }
        catch
        {
            /* ignore */
        }

        _syncTimer = null;
    }

    /// <summary>当前 SYNC 生产者是否运行中。</summary>
    public bool IsSyncRunning => _syncTimer is { IsRunning: true };

    /// <summary>SYNC 生产者诊断信息（累计触发数 / 抖动 μs）。</summary>
    public (long Ticks, long JitterMicros) SyncDiagnostics
        => _syncTimer is { } t ? (t.TickCount, t.LastJitterMicros) : (0L, 0L);

    /// <summary>
    /// 配置从机的 RPDO 映射（通过 SDO）。<br/>
    /// 标准 CiA301 序列：禁用 → 清空映射 → 写各映射条目 → 写计数 → 设置传输类型 → 启用。
    /// </summary>
    /// <param name="node">从机节点号 (1..127)。</param>
    /// <param name="rpdoIndex">PDO 序号 0..3，对应对象 0x1400+i / 0x1600+i。</param>
    /// <param name="cobIdOverride">可选自定义 COB-ID；为 null 时使用默认 0x200/0x300/0x400/0x500 + node。</param>
    /// <param name="transmissionType">0=同步非循环、1..240=同步循环（每 N 个 SYNC 一次）、254=异步事件驱动、255=异步设备特定。</param>
    /// <param name="mapEntries">映射条目数组（每项由 <c>Cia402Helper.BuildPdoMapping</c> 生成），最多 8 项。</param>
    public bool ConfigureRpdoMapping(byte node, byte rpdoIndex, uint? cobIdOverride, byte transmissionType, uint[] mapEntries)
        => ConfigurePdoMapping(node, rpdoIndex, cobIdOverride, transmissionType, mapEntries, isRx: true, eventTimerMs: 0, inhibitTime100us: 0);

    /// <summary>
    /// 配置从机的 TPDO 映射（通过 SDO）。<br/>
    /// 与 RPDO 一致，额外支持 <paramref name="eventTimerMs"/> (0x1800.5) / <paramref name="inhibitTime100us"/> (0x1800.3)。
    /// </summary>
    public bool ConfigureTpdoMapping(byte node, byte tpdoIndex, uint? cobIdOverride, byte transmissionType, uint[] mapEntries, ushort eventTimerMs = 0, ushort inhibitTime100us = 0)
        => ConfigurePdoMapping(node, tpdoIndex, cobIdOverride, transmissionType, mapEntries, isRx: false, eventTimerMs, inhibitTime100us);

    private bool ConfigurePdoMapping(byte node, byte pdoIndex, uint? cobIdOverride, byte transmissionType, uint[] mapEntries, bool isRx, ushort eventTimerMs, ushort inhibitTime100us)
    {
        if (pdoIndex > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(pdoIndex));
        }

        if (node is < 1 or > 0x7F)
        {
            throw new ArgumentOutOfRangeException(nameof(node));
        }

        if (mapEntries is null || mapEntries.Length > 8)
        {
            throw new ArgumentException("映射条目数必须 0..8", nameof(mapEntries));
        }

        ushort commIdx = (ushort)((isRx ? 0x1400 : 0x1800) + pdoIndex);
        ushort mapIdx = (ushort)((isRx ? 0x1600 : 0x1A00) + pdoIndex);
        ushort baseFc = (isRx ? RpdoBase : TpdoBase)[pdoIndex];
        uint defaultCobId = (uint)(baseFc + node);
        uint cobId = cobIdOverride ?? defaultCobId;

        // 1) 禁用 PDO（COB-ID bit31=1）
        if (!TryWriteSDO<uint>(node, commIdx, 1, cobId | 0x80000000u))
        {
            return false;
        }
        // 2) 清空映射计数
        if (!TryWriteSDO<byte>(node, mapIdx, 0, 0))
        {
            return false;
        }
        // 3) 写映射条目
        for (int i = 0; i < mapEntries.Length; i++)
        {
            if (!TryWriteSDO<uint>(node, mapIdx, (byte)(i + 1), mapEntries[i]))
            {
                return false;
            }
        }
        // 4) 写映射条目数
        if (!TryWriteSDO<byte>(node, mapIdx, 0, (byte)mapEntries.Length))
        {
            return false;
        }
        // 5) 设置传输类型
        if (!TryWriteSDO<byte>(node, commIdx, 2, transmissionType))
        {
            return false;
        }
        // 5.5) TPDO 额外参数
        if (!isRx)
        {
            if (inhibitTime100us > 0)
            {
                _ = TryWriteSDO<ushort>(node, commIdx, 3, inhibitTime100us); // 失败忽略：非必需
            }

            if (eventTimerMs > 0)
            {
                _ = TryWriteSDO<ushort>(node, commIdx, 5, eventTimerMs);
            }
        }
        // 6) 启用 PDO（清除 bit31）
        if (!TryWriteSDO<uint>(node, commIdx, 1, cobId))
        {
            return false;
        }

        return true;
    }

    private void DispatchTpdo(ushort fc, byte node, CanFrame f)
    {
        // fc 已由 (cob & 0x780) 推出：0x180/0x280/0x380/0x480
        byte tpdoIndex = fc switch
        {
            0x180 => 0,
            0x280 => 1,
            0x380 => 2,
            0x480 => 3,
            _ => byte.MaxValue,
        };
        if (tpdoIndex == byte.MaxValue)
        {
            return;
        }

        int len = Math.Min(8, (int)f.Dlc);
        var data = new byte[len];
        if (len > 0)
        {
            Buffer.BlockCopy(f.Data, 0, data, 0, len);
        }

        try
        {
            PdoReceived?.Invoke(node, tpdoIndex, data);
        }
        catch
        {
            /* ignore */
        }
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
        for (int i = 0; i < Math.Min(data.Length, 4); i++)
        {
            b[i] = data[i];
        }

        Type t = typeof(T);
        if (t == typeof(byte))
        {
            return (T)(object)b[0];
        }

        if (t == typeof(sbyte))
        {
            return (T)(object)unchecked((sbyte)b[0]);
        }

        if (t == typeof(ushort))
        {
            return (T)(object)(ushort)(b[0] | (b[1] << 8));
        }

        if (t == typeof(short))
        {
            return (T)(object)unchecked((short)(b[0] | (b[1] << 8)));
        }

        if (t == typeof(uint))
        {
            return (T)(object)(uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
        }

        if (t == typeof(int))
        {
            return (T)(object)unchecked((int)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24)));
        }

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
                buf[0] = by;
                validBytes = 1;
                return buf;

            case sbyte sb:
                buf[0] = unchecked((byte)sb);
                validBytes = 1;
                return buf;

            case ushort us:
                buf[0] = (byte)(us & 0xFF);
                buf[1] = (byte)(us >> 8);
                validBytes = 2;
                return buf;

            case short s:
                ushort us2 = unchecked((ushort)s);
                buf[0] = (byte)(us2 & 0xFF);
                buf[1] = (byte)(us2 >> 8);
                validBytes = 2;
                return buf;

            case uint u:
                buf[0] = (byte)(u & 0xFF);
                buf[1] = (byte)((u >> 8) & 0xFF);
                buf[2] = (byte)((u >> 16) & 0xFF);
                buf[3] = (byte)((u >> 24) & 0xFF);
                validBytes = 4;
                return buf;

            case int i:
                uint u2 = unchecked((uint)i);
                buf[0] = (byte)(u2 & 0xFF);
                buf[1] = (byte)((u2 >> 8) & 0xFF);
                buf[2] = (byte)((u2 >> 16) & 0xFF);
                buf[3] = (byte)((u2 >> 24) & 0xFF);
                validBytes = 4;
                return buf;

            case float f:
                uint u3 = BitConverter.SingleToUInt32Bits(f);
                buf[0] = (byte)(u3 & 0xFF);
                buf[1] = (byte)((u3 >> 8) & 0xFF);
                buf[2] = (byte)((u3 >> 16) & 0xFF);
                buf[3] = (byte)((u3 >> 24) & 0xFF);
                validBytes = 4;
                return buf;

            default:
                throw new NotSupportedException($"CANopen SDO 暂不支持类型: {typeof(T).Name}");
        }
    }
}
