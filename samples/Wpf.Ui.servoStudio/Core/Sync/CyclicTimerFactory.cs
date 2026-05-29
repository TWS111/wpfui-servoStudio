// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;

namespace Wpf.Ui.servoStudio.Core.Sync;

/// <summary>
/// 周期定时器工厂。根据 <see cref="CyclicTimerKind"/> 创建对应实现。<br/>
/// 调用方负责 <see cref="ICyclicTimer.Dispose"/>。
/// </summary>
public static class CyclicTimerFactory
{
    /// <summary>按枚举值创建一个新的周期定时器实例（未启动）。</summary>
    public static ICyclicTimer Create(CyclicTimerKind kind) => kind switch
    {
        CyclicTimerKind.PeriodicTimer => new PeriodicTimerCyclicTimer(),
        CyclicTimerKind.WinMm => new WinMmCyclicTimer(),
        CyclicTimerKind.SpinWait => new SpinWaitCyclicTimer(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的 CyclicTimerKind"),
    };

    /// <summary>用于 UI/绑定的友好名称。</summary>
    public static string DisplayName(CyclicTimerKind kind) => kind switch
    {
        CyclicTimerKind.PeriodicTimer => ".NET PeriodicTimer (托管，毫秒级)",
        CyclicTimerKind.WinMm => "Win32 多媒体定时器 (1ms 级)",
        CyclicTimerKind.SpinWait => "Stopwatch + SpinWait (亚毫秒，CPU 占用高)",
        _ => kind.ToString(),
    };
}
