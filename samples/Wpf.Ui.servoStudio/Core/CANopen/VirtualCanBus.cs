// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Core.CANopen;

/// <summary>
/// 基于内存回环队列的 <see cref="ICanBus"/> 实现。<br/>
/// 用于离线开发与单元测试：不需要任何 CAN 硬件即可让 <see cref="CanOpenMaster"/> 走完
/// SDO 收发的代码路径；上层调用方可注入一个虚拟从机响应函数模拟应答。
/// </summary>
public sealed class VirtualCanBus : ICanBus
{
    private readonly BlockingCollection<CanFrame> _rxQueue = new(new ConcurrentQueue<CanFrame>());
    private bool _disposed;

    /// <summary>
    /// 当上层 <see cref="Send"/> 一帧时被调用：实现可在此组合应答帧并通过
    /// <see cref="Inject"/> 推入接收队列，从而模拟一个 CANopen 从机。
    /// </summary>
    public Action<VirtualCanBus, CanFrame>? OnFrameSent { get; set; }

    public bool IsOpen { get; private set; }

    public bool Open(CanBitrate bitrate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsOpen = true;
        return true;
    }

    public void Close()
    {
        IsOpen = false;
        // 排空接收队列，避免污染下一次会话
        while (_rxQueue.TryTake(out _)) { }
    }

    public bool Send(CanFrame frame)
    {
        if (!IsOpen)
        {
            return false;
        }

        OnFrameSent?.Invoke(this, frame);
        return true;
    }

    public bool TryReceive(int timeoutMs, out CanFrame frame)
    {
        frame = default;
        if (!IsOpen)
        {
            return false;
        }

        try
        {
            if (_rxQueue.TryTake(out frame, timeoutMs))
            {
                return true;
            }
        }
        catch (InvalidOperationException) { /* CompleteAdding 后正常终止 */ }
        return false;
    }

    /// <summary>由模拟从机调用：把一帧应答放入接收队列。</summary>
    public void Inject(CanFrame frame)
    {
        if (IsOpen && !_disposed)
        {
            _rxQueue.Add(frame);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { Close(); } catch { /* ignore */ }
        _rxQueue.Dispose();
    }
}
