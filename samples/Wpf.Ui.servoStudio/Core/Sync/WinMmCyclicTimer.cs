// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Wpf.Ui.servoStudio.Core.Sync;

/// <summary>
/// 基于 Win32 winmm.dll 多媒体定时器（<c>timeSetEvent</c>）的周期触发器。<br/>
/// 优点：1 ms 级稳定抖动；缺点：仅 Windows，需 P/Invoke。
/// </summary>
public sealed class WinMmCyclicTimer : ICyclicTimer
{
    private const int TIME_PERIODIC = 1;
    private const int TIME_CALLBACK_FUNCTION = 0x0000;

    private delegate void TimeCallback(uint uTimerID, uint uMsg, UIntPtr dwUser, UIntPtr dw1, UIntPtr dw2);

    [DllImport("winmm.dll", EntryPoint = "timeSetEvent")]
    private static extern uint TimeSetEvent(uint delay, uint resolution, TimeCallback callback, UIntPtr user, uint eventType);

    [DllImport("winmm.dll", EntryPoint = "timeKillEvent")]
    private static extern uint TimeKillEvent(uint id);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    private uint _timerId;
    // 持有委托引用以防 GC
    private TimeCallback? _callbackKeepAlive;
    private Action? _userTick;
    private long _tickCount;
    private long _errorCount;
    private long _lastJitterMicros;
    private long _lastTicks;
    private long _periodTicks;
    private uint _periodMs;
    private bool _disposed;
    private readonly object _lock = new();

    public CyclicTimerKind Kind => CyclicTimerKind.WinMm;
    public bool IsRunning => _timerId != 0;
    public long TickCount => Interlocked.Read(ref _tickCount);
    public long ErrorCount => Interlocked.Read(ref _errorCount);
    public long LastJitterMicros => Interlocked.Read(ref _lastJitterMicros);

    public void Start(TimeSpan period, Action tick)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WinMmCyclicTimer));
        }

        if (tick is null)
        {
            throw new ArgumentNullException(nameof(tick));
        }

        lock (_lock)
        {
            StopInternal();

            uint ms = (uint)Math.Max(1, (int)Math.Round(period.TotalMilliseconds));
            _periodMs = ms;
            _periodTicks = TimeSpan.FromMilliseconds(ms).Ticks;
            _userTick = tick;
            _callbackKeepAlive = OnCallback;
            Interlocked.Exchange(ref _tickCount, 0);
            Interlocked.Exchange(ref _errorCount, 0);
            Interlocked.Exchange(ref _lastJitterMicros, 0);
            _lastTicks = Stopwatch.GetTimestamp();

            // 请求系统提高定时器分辨率到 1ms
            _ = TimeBeginPeriod(1);

            uint id = TimeSetEvent(ms, 0, _callbackKeepAlive, UIntPtr.Zero, TIME_PERIODIC | TIME_CALLBACK_FUNCTION);
            if (id == 0)
            {
                _ = TimeEndPeriod(1);
                _callbackKeepAlive = null;
                _userTick = null;
                throw new InvalidOperationException("WinMm timeSetEvent 创建失败");
            }
            _timerId = id;
        }
    }

    private void OnCallback(uint uTimerID, uint uMsg, UIntPtr dwUser, UIntPtr dw1, UIntPtr dw2)
    {
        long now = Stopwatch.GetTimestamp();
        long prev = Interlocked.Exchange(ref _lastTicks, now);
        long deltaTicks = (now - prev) * TimeSpan.TicksPerSecond / Stopwatch.Frequency;
        long jitter100ns = deltaTicks - _periodTicks;
        if (jitter100ns < 0)
        {
            jitter100ns = -jitter100ns;
        }

        Interlocked.Exchange(ref _lastJitterMicros, jitter100ns / 10);

        Action? cb = _userTick;
        if (cb is not null)
        {
            try { cb(); }
            catch { Interlocked.Increment(ref _errorCount); }
        }
        Interlocked.Increment(ref _tickCount);
    }

    public void Stop()
    {
        lock (_lock) { StopInternal(); }
    }

    private void StopInternal()
    {
        if (_timerId != 0)
        {
            try { _ = TimeKillEvent(_timerId); } catch { /* ignore */ }
            _timerId = 0;
            try { _ = TimeEndPeriod(1); } catch { /* ignore */ }
        }
        _userTick = null;
        _callbackKeepAlive = null;
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
