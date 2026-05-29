// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Diagnostics;
using System.Threading;

namespace Wpf.Ui.servoStudio.Core.Sync;

/// <summary>
/// 基于 <see cref="Stopwatch"/> + <see cref="Thread.SpinWait"/> 的高精度周期触发器。<br/>
/// 优点：亚毫秒级抖动；缺点：占用一整个 CPU 核心（busy-wait）。
/// </summary>
public sealed class SpinWaitCyclicTimer : ICyclicTimer
{
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private long _tickCount;
    private long _errorCount;
    private long _lastJitterMicros;
    private bool _disposed;

    public CyclicTimerKind Kind => CyclicTimerKind.SpinWait;
    public bool IsRunning => _thread is { IsAlive: true };
    public long TickCount => Interlocked.Read(ref _tickCount);
    public long ErrorCount => Interlocked.Read(ref _errorCount);
    public long LastJitterMicros => Interlocked.Read(ref _lastJitterMicros);

    public void Start(TimeSpan period, Action tick)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SpinWaitCyclicTimer));
        }

        if (tick is null)
        {
            throw new ArgumentNullException(nameof(tick));
        }

        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        Stop();

        _cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _tickCount, 0);
        Interlocked.Exchange(ref _errorCount, 0);
        Interlocked.Exchange(ref _lastJitterMicros, 0);
        CancellationToken token = _cts.Token;

        long periodSwTicks = (long)(period.TotalSeconds * Stopwatch.Frequency);
        long periodTicks = period.Ticks;

        _thread = new Thread(() =>
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.Highest; } catch { /* ignore */ }
            long next = Stopwatch.GetTimestamp() + periodSwTicks;
            long last = next - periodSwTicks;

            while (!token.IsCancellationRequested)
            {
                // 粗等：剩余 > 2 ms 时让出 CPU
                while (!token.IsCancellationRequested)
                {
                    long remain = next - Stopwatch.GetTimestamp();
                    if (remain <= 0)
                    {
                        break;
                    }

                    if (remain > Stopwatch.Frequency / 500) // 2ms
                    {
                        Thread.Sleep(1);
                    }
                    else if (remain > Stopwatch.Frequency / 5000) // 0.2ms
                    {
                        Thread.SpinWait(64);
                    }
                    else
                    {
                        Thread.SpinWait(8);
                    }
                }
                if (token.IsCancellationRequested)
                {
                    break;
                }

                long now = Stopwatch.GetTimestamp();
                long deltaTicks = (now - last) * TimeSpan.TicksPerSecond / Stopwatch.Frequency;
                last = now;
                long jitter100ns = deltaTicks - periodTicks;
                if (jitter100ns < 0)
                {
                    jitter100ns = -jitter100ns;
                }

                Interlocked.Exchange(ref _lastJitterMicros, jitter100ns / 10);

                try { tick(); }
                catch { Interlocked.Increment(ref _errorCount); }

                Interlocked.Increment(ref _tickCount);

                // 累加期望时刻，防止漂移；若回调耗时过长致超期 > 2 个周期，则重置基准
                next += periodSwTicks;
                long lag = Stopwatch.GetTimestamp() - next;
                if (lag > periodSwTicks * 2)
                {
                    next = Stopwatch.GetTimestamp() + periodSwTicks;
                }
            }
        })
        {
            IsBackground = true,
            Name = "SpinWaitCyclicTimer",
        };
        _thread.Start();
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _thread?.Join(500); } catch { /* ignore */ }
        _thread = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
