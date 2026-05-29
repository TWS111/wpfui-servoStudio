// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Wpf.Ui.servoStudio.Core.Sync;

/// <summary>
/// 基于 .NET <see cref="PeriodicTimer"/> 的周期触发器。<br/>
/// 优点：纯托管，无 P/Invoke；缺点：抖动随系统负载变化，毫秒级以下不稳定。
/// </summary>
public sealed class PeriodicTimerCyclicTimer : ICyclicTimer
{
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _tickCount;
    private long _errorCount;
    private long _lastJitterMicros;
    private bool _disposed;

    public CyclicTimerKind Kind => CyclicTimerKind.PeriodicTimer;
    public bool IsRunning => _loop is { IsCompleted: false };
    public long TickCount => Interlocked.Read(ref _tickCount);
    public long ErrorCount => Interlocked.Read(ref _errorCount);
    public long LastJitterMicros => Interlocked.Read(ref _lastJitterMicros);

    public void Start(TimeSpan period, Action tick)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PeriodicTimerCyclicTimer));
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

        _timer = new PeriodicTimer(period);
        _cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _tickCount, 0);
        Interlocked.Exchange(ref _errorCount, 0);
        Interlocked.Exchange(ref _lastJitterMicros, 0);

        long periodTicks = period.Ticks; // 100ns
        CancellationToken token = _cts.Token;
        PeriodicTimer timer = _timer;

        _loop = Task.Run(async () =>
        {
            // 提高线程优先级以减少抖动
            try { Thread.CurrentThread.Priority = ThreadPriority.Highest; } catch { /* ignore */ }
            var sw = Stopwatch.StartNew();
            long last = sw.ElapsedTicks;
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    long now = sw.ElapsedTicks;
                    long deltaTicks = now - last;
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
                }
            }
            catch (OperationCanceledException) { /* normal */ }
        }, token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _timer?.Dispose(); } catch { /* ignore */ }
        try { _loop?.Wait(200); } catch { /* ignore */ }
        _loop = null;
        _timer = null;
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
