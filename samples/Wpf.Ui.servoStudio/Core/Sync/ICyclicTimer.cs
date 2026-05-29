// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;

namespace Wpf.Ui.servoStudio.Core.Sync;

/// <summary>
/// 高精度周期触发器抽象。<br/>
/// 用于驱动 CANopen SYNC 生产者与 EtherCAT InOutSync 周期循环。<br/>
/// 实现方按 <see cref="Start"/> 中的 <c>period</c> 周期调用 <c>tick</c>。<br/>
/// <c>tick</c> 在实现内部的工作线程上调用，不在 UI 线程。
/// </summary>
public interface ICyclicTimer : IDisposable
{
    /// <summary>启动周期触发。重复调用会先停止再启动。</summary>
    /// <param name="period">周期间隔。</param>
    /// <param name="tick">每次触发回调；回调中抛出的异常会被吞掉并累计到 <see cref="ErrorCount"/>。</param>
    void Start(TimeSpan period, Action tick);

    /// <summary>停止周期触发。</summary>
    void Stop();

    /// <summary>当前是否处于运行状态。</summary>
    bool IsRunning { get; }

    /// <summary>累计触发次数（自启动以来）。</summary>
    long TickCount { get; }

    /// <summary>累计 tick 回调抛出异常的次数。</summary>
    long ErrorCount { get; }

    /// <summary>最近一次周期的实际间隔与目标 period 的偏差（μs，仅诊断）。</summary>
    long LastJitterMicros { get; }

    /// <summary>定时器类型，便于诊断显示。</summary>
    CyclicTimerKind Kind { get; }
}

/// <summary>周期定时器实现类型。</summary>
public enum CyclicTimerKind
{
    /// <summary>.NET PeriodicTimer + 后台线程（高优先级）。毫秒级，依赖系统调度。</summary>
    PeriodicTimer = 0,

    /// <summary>Win32 timeSetEvent (winmm.dll)。多媒体定时器，1ms 级别更稳定。</summary>
    WinMm = 1,

    /// <summary>Stopwatch + SpinWait。CPU 占用高但抖动最小，亚毫秒精度。</summary>
    SpinWait = 2,
}
