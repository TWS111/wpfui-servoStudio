// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Wpf.Ui.servoStudio.Services;

/// <summary>
/// 在线接收波形排查专用轻量日志器。<br/>
/// 用法：<see cref="Mark(string, double)"/> 记录一次事件的耗时（毫秒）；
/// <see cref="Tick(string)"/> 仅计数；<see cref="Flush"/> 在每秒（或显式调用）把当前
/// 累计统计写入 <c>%TEMP%\servoStudio_live_diag.log</c>，每行 JSON 风格便于分析。<br/>
/// 设计原则：调用点零分配、线程安全、无锁热路径；后台 1Hz 翻页输出。
/// </summary>
internal static class LiveDiag
{
    private static readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private static readonly string _logPath =
        Path.Combine(Path.GetTempPath(), "servoStudio_live_diag.log");
    private static readonly Stopwatch _wallClock = Stopwatch.StartNew();
    private static long _nextFlushMs;
    private static int _enabled;

    /// <summary>开启日志。重复调用幂等。第一次调用会清空既有文件。</summary>
    public static void Enable()
    {
        if (Interlocked.Exchange(ref _enabled, 1) == 1)
        {
            return;
        }
        try
        {
            File.WriteAllText(
                _logPath,
                $"# LiveDiag opened at {DateTime.Now:HH:mm:ss.fff}\r\n",
                Encoding.UTF8);
        }
        catch
        {
            /* ignore */
        }
        _nextFlushMs = _wallClock.ElapsedMilliseconds + 1000;
    }

    /// <summary>关闭日志。</summary>
    public static void Disable() => Interlocked.Exchange(ref _enabled, 0);

    public static bool IsEnabled => Volatile.Read(ref _enabled) == 1;

    public static string LogPath => _logPath;

    /// <summary>累加一次"耗时事件"。<paramref name="ms"/> 不会被裁剪，max/min 同时统计。</summary>
    public static void Mark(string key, double ms)
    {
        if (!IsEnabled)
        {
            return;
        }
        var b = _buckets.GetOrAdd(key, static _ => new Bucket());
        b.Add(ms);
        MaybeFlush();
    }

    /// <summary>累加一次"计数事件"（不带耗时）。</summary>
    public static void Tick(string key)
    {
        if (!IsEnabled)
        {
            return;
        }
        var b = _buckets.GetOrAdd(key, static _ => new Bucket());
        b.AddCountOnly();
        MaybeFlush();
    }

    /// <summary>用 <see cref="Stopwatch"/> 测量一段代码，自动 Mark。</summary>
    public static Scope Scoped(string key) => new(key);

    private static void MaybeFlush()
    {
        long now = _wallClock.ElapsedMilliseconds;
        if (now < Volatile.Read(ref _nextFlushMs))
        {
            return;
        }
        // 抢占 flush（线程安全，错过一秒可接受）
        long expected = Volatile.Read(ref _nextFlushMs);
        if (Interlocked.CompareExchange(ref _nextFlushMs, now + 1000, expected) != expected)
        {
            return;
        }
        FlushCore(now);
    }

    /// <summary>强制立即刷新一次。</summary>
    public static void Flush() => FlushCore(_wallClock.ElapsedMilliseconds);

    private static void FlushCore(long nowMs)
    {
        if (_buckets.IsEmpty)
        {
            return;
        }
        var sb = new StringBuilder(512);
        sb.Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("] ");
        bool first = true;
        foreach (var kv in _buckets)
        {
            var snap = kv.Value.SnapshotAndReset();
            if (snap.Count == 0)
            {
                continue;
            }
            if (!first)
            {
                sb.Append(' ');
            }
            first = false;
            sb.Append(kv.Key)
              .Append("={n=").Append(snap.Count);
            if (snap.HasTiming)
            {
                sb.Append(",avg=")
                  .Append((snap.SumMs / snap.Count).ToString("F2"))
                  .Append(",max=")
                  .Append(snap.MaxMs.ToString("F2"));
            }
            sb.Append('}');
        }
        if (first)
        {
            return;
        }
        sb.Append("\r\n");
        try
        {
            File.AppendAllText(_logPath, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            /* ignore */
        }
    }

    public readonly struct Scope : IDisposable
    {
        private readonly string _key;
        private readonly long _startTicks;
        public Scope(string key)
        {
            _key = key;
            _startTicks = IsEnabled ? Stopwatch.GetTimestamp() : 0;
        }
        public void Dispose()
        {
            if (_startTicks == 0)
            {
                return;
            }
            double ms = (Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency;
            Mark(_key, ms);
        }
    }

    private sealed class Bucket
    {
        private long _count;
        private double _sumMs;
        private double _maxMs;
        private readonly Lock _lock = new();

        public void Add(double ms)
        {
            lock (_lock)
            {
                _count++;
                _sumMs += ms;
                if (ms > _maxMs)
                {
                    _maxMs = ms;
                }
            }
        }

        public void AddCountOnly()
        {
            lock (_lock)
            {
                _count++;
            }
        }

        public Snapshot SnapshotAndReset()
        {
            lock (_lock)
            {
                var s = new Snapshot(_count, _sumMs, _maxMs, _sumMs > 0 || _maxMs > 0);
                _count = 0;
                _sumMs = 0;
                _maxMs = 0;
                return s;
            }
        }
    }

    public readonly record struct Snapshot(long Count, double SumMs, double MaxMs, bool HasTiming);
}
