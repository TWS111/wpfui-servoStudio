// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Wpf.Ui.servoStudio.Core.Sync;

namespace Core.Usb;

/// <summary>
/// USB 回环测试器 —— 向从机发送已标记的负载，等待从机原样回送后计算往返时延
/// （RTT）、数据完整性及实际吞吐量，用于评估 USB 通信质量。
/// <para>
/// <b>回环帧负载格式（共 14 字节头 + 填充）：</b><br/>
/// [0..3]  魔术字 "LOOP"（0x4C 0x4F 0x4F 0x50）<br/>
/// [4..5]  本类内部回环序号（big-endian uint16）<br/>
/// [6..13] 发送时刻（<see cref="Stopwatch.GetTimestamp"/>，little-endian int64，用于计算 RTT）<br/>
/// [14..]  填充字节：<c>index &amp; 0xFF</c>（用于验证数据完整性）
/// </para>
/// <para>
/// 从机侧须将 <see cref="UsbChannel.Loopback"/> 通道的帧<b>原样回送</b>（Echo）。
/// </para>
/// </summary>
public sealed class UsbLoopbackTester : IDisposable
{
    private const int HeaderSize = 14;

    private readonly UsbMaster _master;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _disposed;

    /// <summary>正在等待回声的帧：回环序号 → 发送时刻（Stopwatch ticks）。</summary>
    private readonly ConcurrentDictionary<ushort, long> _pending = new();

    // ── 原子计数器 ──
    private long _sentCount;
    private long _receivedCount;
    private long _errorCount;
    private long _dropCount;
    private long _totalRttTicks;
    private long _minRttTicks = long.MaxValue;
    private long _maxRttTicks;
    private long _lastRttTicks;
    private long _totalBytesSent;
    private long _totalBytesReceived;
    private long _startTimestamp;

    // ── 带宽采样（500ms 窗口）──
    // 每 500ms 取一次快照，用字节增量 ÷ 实际间隔计算瞬时带宽
    private long _lastTxBytesSnapshot;
    private long _lastRxBytesSnapshot;
    private long _lastSnapshotTimestamp;

    private ushort _loopbackSeq;

    // ────────────────────────────────────────────────────────
    //  公开状态与配置
    // ────────────────────────────────────────────────────────

    /// <summary>是否正在运行。</summary>
    public bool IsRunning => _loopTask != null && !_loopTask.IsCompleted;

    /// <summary>
    /// 每包负载字节数（14 到 512）。默认 64 字节。<br/>
    /// 若设置值小于 <c>14</c>，实际按 14 发送；大于 512 按 512 发送。
    /// </summary>
    public int PacketSize { get; set; } = 64;

    /// <summary>
    /// 相邻两包之间的发送间隔（ms）。默认 10 ms。<br/>
    /// 设为 <c>0</c> 时切换为"背靠背"模式，以最大吞吐量连续发包（带宽测试）。
    /// </summary>
    public int IntervalMs { get; set; } = 10;

    /// <summary>回环响应超时（ms）；超过此时间未收到回声的帧计入丢包。默认 500 ms。</summary>
    public int TimeoutMs { get; set; } = 500;

    // ── 统计属性（可在任意线程安全读取，在 StatsUpdated 事件后刷新到 UI）──
    /// <summary>累计已发送帧数。</summary>
    public long SentCount => Volatile.Read(ref _sentCount);

    /// <summary>累计已成功回声（数据完整）的帧数。</summary>
    public long ReceivedCount => Volatile.Read(ref _receivedCount);

    /// <summary>累计错误数（发送失败或数据不完整）。</summary>
    public long ErrorCount => Volatile.Read(ref _errorCount);

    /// <summary>累计丢包数（超时未回声）。</summary>
    public long DropCount => Volatile.Read(ref _dropCount);

    /// <summary>平均往返时延（ms）。</summary>
    public double AvgRttMs { get; private set; }

    /// <summary>最小往返时延（ms）。</summary>
    public double MinRttMs { get; private set; }

    /// <summary>最大往返时延（ms）。</summary>
    public double MaxRttMs { get; private set; }

    /// <summary>最近一次往返时延（ms）。</summary>
    public double LastRttMs { get; private set; }

    /// <summary>双向合计吞吐量（MB/s，含发送与接收字节数）。</summary>
    public double ThroughputMbps { get; private set; }

    /// <summary>
    /// 上行吞吐量（MB/s）——从机→上位机（Device→Host）方向，即回声接收字节数。
    /// </summary>
    public double UplinkThroughputMbps { get; private set; }

    /// <summary>
    /// 下行吞吐量（MB/s）——上位机→从机（Host→Device）方向，即发送字节数。
    /// </summary>
    public double DownlinkThroughputMbps { get; private set; }

    /// <summary>统计数据更新事件，由接收 / 超时扫描线程触发，订阅者须切换到 UI 线程后再读属性。</summary>
    public event Action? StatsUpdated;

    /// <summary>
    /// 发送循环使用的底层定时器类型。默认 <see cref="CyclicTimerKind.WinMm"/>（1ms 级稳定）。<br/>
    /// 必须在 <see cref="Start"/> 之前设置，运行中修改无效。
    /// </summary>
    public CyclicTimerKind TimerKind { get; set; } = CyclicTimerKind.WinMm;

    // ────────────────────────────────────────────────────────
    //  构造与生命周期
    // ────────────────────────────────────────────────────────

    /// <param name="master">已运行的 USB 主站实例，不得为 null。</param>
    public UsbLoopbackTester(UsbMaster master)
    {
        _master = master ?? throw new ArgumentNullException(nameof(master));
        _master.PacketReceived += OnPacketReceived;
    }

    /// <summary>开始回环测试；若已在运行则忽略。</summary>
    public void Start()
    {
        if (IsRunning || _disposed)
        {
            return;
        }

        ResetCounters();
        _startTimestamp = Stopwatch.GetTimestamp();
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => SendLoop(_cts.Token));
    }

    /// <summary>停止回环测试（异步，不等待任务结束）。</summary>
    public void Stop()
    {
        try { _cts?.Cancel(); }
        catch { /* ignore */ }
    }

    /// <summary>重置所有统计计数器；若正在运行则先停止。</summary>
    public void Reset()
    {
        if (IsRunning)
        {
            Stop();
        }

        ResetCounters();
        StatsUpdated?.Invoke();
    }

    private void ResetCounters()
    {
        _pending.Clear();
        Volatile.Write(ref _lastSnapshotTimestamp, 0);
        Volatile.Write(ref _lastTxBytesSnapshot, 0);
        Volatile.Write(ref _lastRxBytesSnapshot, 0);
        Interlocked.Exchange(ref _sentCount, 0);
        Interlocked.Exchange(ref _receivedCount, 0);
        Interlocked.Exchange(ref _errorCount, 0);
        Interlocked.Exchange(ref _dropCount, 0);
        Interlocked.Exchange(ref _totalRttTicks, 0);
        Volatile.Write(ref _minRttTicks, long.MaxValue);
        Interlocked.Exchange(ref _maxRttTicks, 0);
        Interlocked.Exchange(ref _lastRttTicks, 0);
        Interlocked.Exchange(ref _totalBytesSent, 0);
        Interlocked.Exchange(ref _totalBytesReceived, 0);
        _startTimestamp = Stopwatch.GetTimestamp();
        AvgRttMs = 0;
        MinRttMs = 0;
        MaxRttMs = 0;
        LastRttMs = 0;
        ThroughputMbps = 0;
        UplinkThroughputMbps = 0;
        DownlinkThroughputMbps = 0;
    }

    // ────────────────────────────────────────────────────────
    //  发送循环
    // ────────────────────────────────────────────────────────

    private async Task SendLoop(CancellationToken ct)
    {
        Task timeoutTask = Task.Run(() => TimeoutLoop(ct), CancellationToken.None);

        int intervalMs = Math.Max(1, IntervalMs);

        // IntervalMs == 0 → 背靠背模式，不用高精度定时器，直接 Task.Yield
        if (IntervalMs <= 0)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    SendOneTick();
                    await Task.Yield();
                }
                catch (OperationCanceledException) { break; }
                catch { Interlocked.Increment(ref _errorCount); }
            }
        }
        else
        {
            // 用 ICyclicTimer 驱动发包，精度由 TimerKind 决定
            using ICyclicTimer timer = CyclicTimerFactory.Create(TimerKind);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetResult(true));

            timer.Start(TimeSpan.FromMilliseconds(intervalMs), () =>
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                SendOneTick();
            });

            await tcs.Task.ConfigureAwait(false);
            timer.Stop();
        }

        try { await timeoutTask.ConfigureAwait(false); }
        catch { /* ignore */ }
    }

    private void SendOneTick()
    {
        int payloadSize = Math.Clamp(PacketSize, HeaderSize, UsbDefaults.MaxPacketSize - 7);
        byte[] payload = BuildPayload(payloadSize, _loopbackSeq);

        _pending[_loopbackSeq] = Stopwatch.GetTimestamp();

        bool sent = _master.Send(UsbChannel.Loopback, payload);
        if (sent)
        {
            Interlocked.Increment(ref _sentCount);
            // 7 = UsbPacketCodec 序列化加的帧头（Channel 2B + Seq 2B + Dir 1B + PayloadLen 2B）
            int wireBytes = payloadSize + 7;
            Interlocked.Add(ref _totalBytesSent, wireBytes);
        }
        else
        {
            _pending.TryRemove(_loopbackSeq, out _);
            Interlocked.Increment(ref _errorCount);
        }

        _loopbackSeq = unchecked((ushort)(_loopbackSeq + 1));
    }

    // ────────────────────────────────────────────────────────
    //  超时扫描（定期清除已超时的待回声帧并触发统计更新）
    // ────────────────────────────────────────────────────────

    private async Task TimeoutLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                int delay = Math.Clamp(TimeoutMs / 2, 20, 250);
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            long cutoffTicks = Stopwatch.GetTimestamp()
                - (long)((double)TimeoutMs / 1000.0 * Stopwatch.Frequency);

            foreach (KeyValuePair<ushort, long> kv in _pending)
            {
                if (kv.Value < cutoffTicks && _pending.TryRemove(kv.Key, out _))
                {
                    Interlocked.Increment(ref _dropCount);
                }
            }

            ComputeDerivedStats();
            StatsUpdated?.Invoke();
        }
    }

    // ────────────────────────────────────────────────────────
    //  负载构造
    // ────────────────────────────────────────────────────────

    private static byte[] BuildPayload(int size, ushort seq)
    {
        byte[] payload = new byte[size];

        // 魔术字 "LOOP"
        payload[0] = 0x4C;
        payload[1] = 0x4F;
        payload[2] = 0x4F;
        payload[3] = 0x50;

        // 回环序号（big-endian）
        payload[4] = (byte)(seq >> 8);
        payload[5] = (byte)(seq & 0xFF);

        // 发送时刻（little-endian Int64），在此处写入以保证 RTT 精度
        long ts = Stopwatch.GetTimestamp();
        byte[] tsBytes = BitConverter.GetBytes(ts);
        tsBytes.CopyTo(payload, 6);

        // 填充：index & 0xFF，接收端用于完整性校验
        for (int i = HeaderSize; i < size; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        return payload;
    }

    // ────────────────────────────────────────────────────────
    //  接收处理
    // ────────────────────────────────────────────────────────

    private void OnPacketReceived(UsbPacket pkt)
    {
        if (pkt.Channel != UsbChannel.Loopback)
        {
            return;
        }

        byte[]? payload = pkt.Payload;
        if (payload is null || payload.Length < HeaderSize)
        {
            return;
        }

        // 验证魔术字
        if (payload[0] != 0x4C || payload[1] != 0x4F || payload[2] != 0x4F || payload[3] != 0x50)
        {
            return;
        }

        // 解析回环序号
        ushort seq = (ushort)((payload[4] << 8) | payload[5]);

        // 从待回声表中移除；若已被超时清除则忽略（视作丢包已计入）
        if (!_pending.TryRemove(seq, out _))
        {
            return;
        }

        // 从负载中读取发送时刻并计算 RTT
        long sendTicks = BitConverter.ToInt64(payload, 6);
        long rttTicks = Stopwatch.GetTimestamp() - sendTicks;
        if (rttTicks < 0)
        {
            rttTicks = 0;
        }

        // 填充完整性校验
        bool intact = true;
        for (int i = HeaderSize; i < payload.Length; i++)
        {
            if (payload[i] != (byte)(i & 0xFF))
            {
                intact = false;
                break;
            }
        }

        if (!intact)
        {
            Interlocked.Increment(ref _errorCount);
            return;
        }

        Interlocked.Increment(ref _receivedCount);
        // 7 = UsbPacketCodec 序列化加的帧头（Channel 2B + Seq 2B + Dir 1B + PayloadLen 2B）
        int rxWireBytes = payload.Length + 7;
        Interlocked.Add(ref _totalBytesReceived, rxWireBytes);
        Interlocked.Exchange(ref _lastRttTicks, rttTicks);
        Interlocked.Add(ref _totalRttTicks, rttTicks);

        // 更新最小 RTT（CAS 循环）
        long prevMin = Volatile.Read(ref _minRttTicks);
        while (rttTicks < prevMin)
        {
            long updated = Interlocked.CompareExchange(ref _minRttTicks, rttTicks, prevMin);
            if (updated == prevMin)
            {
                break;
            }

            prevMin = updated;
        }

        // 更新最大 RTT（CAS 循环）
        long prevMax = Volatile.Read(ref _maxRttTicks);
        while (rttTicks > prevMax)
        {
            long updated = Interlocked.CompareExchange(ref _maxRttTicks, rttTicks, prevMax);
            if (updated == prevMax)
            {
                break;
            }

            prevMax = updated;
        }

        ComputeDerivedStats();
        StatsUpdated?.Invoke();
    }

    // ────────────────────────────────────────────────────────
    //  派生统计计算（在非 UI 线程调用，读取后由 StatsUpdated 通知 UI）
    // ────────────────────────────────────────────────────────

    private void ComputeDerivedStats()
    {
        long rcvd = Volatile.Read(ref _receivedCount);
        long totalRtt = Volatile.Read(ref _totalRttTicks);
        long minRtt = Volatile.Read(ref _minRttTicks);
        long maxRtt = Volatile.Read(ref _maxRttTicks);
        long lastRtt = Volatile.Read(ref _lastRttTicks);

        AvgRttMs = rcvd > 0 ? TicksToMs(totalRtt / rcvd) : 0;
        MinRttMs = minRtt == long.MaxValue ? 0 : TicksToMs(minRtt);
        MaxRttMs = TicksToMs(maxRtt);
        LastRttMs = TicksToMs(lastRtt);

        // 带宽每 500ms 采样一次：字节增量 ÷ 实际间隔
        long nowTs = Stopwatch.GetTimestamp();
        long lastTs = Volatile.Read(ref _lastSnapshotTimestamp);
        long sampleTicks = Stopwatch.Frequency / 2; // 500ms

        if (lastTs > 0 && (nowTs - lastTs) >= sampleTicks)
        {
            double intervalSec = (double)(nowTs - lastTs) / Stopwatch.Frequency;
            long txNow = Volatile.Read(ref _totalBytesSent);
            long rxNow = Volatile.Read(ref _totalBytesReceived);
            long txDelta = txNow - Volatile.Read(ref _lastTxBytesSnapshot);
            long rxDelta = rxNow - Volatile.Read(ref _lastRxBytesSnapshot);
            double divisor = intervalSec * 1024.0 * 1024.0;

            ThroughputMbps = (txDelta + rxDelta) / divisor;
            DownlinkThroughputMbps = txDelta / divisor;
            UplinkThroughputMbps = rxDelta / divisor;

            Volatile.Write(ref _lastSnapshotTimestamp, nowTs);
            Volatile.Write(ref _lastTxBytesSnapshot, txNow);
            Volatile.Write(ref _lastRxBytesSnapshot, rxNow);
        }
        else if (lastTs == 0)
        {
            // 首次调用，只记录快照，不输出带宽
            Volatile.Write(ref _lastSnapshotTimestamp, nowTs);
            Volatile.Write(ref _lastTxBytesSnapshot, Volatile.Read(ref _totalBytesSent));
            Volatile.Write(ref _lastRxBytesSnapshot, Volatile.Read(ref _totalBytesReceived));
        }
    }

    private static double TicksToMs(long ticks)
        => (double)ticks / Stopwatch.Frequency * 1000.0;

    // ────────────────────────────────────────────────────────
    //  IDisposable
    // ────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _master.PacketReceived -= OnPacketReceived;

        try { _cts?.Cancel(); }
        catch { /* ignore */ }

        _cts?.Dispose();
    }
}
