// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Threading;
using Core.Net.EtherCAT;
using Wpf.Ui.servoStudio.Core.Sync;
using Wpf.Ui.servoStudio.Models;

namespace Wpf.Ui.servoStudio.Core.EtherCAT;

/// <summary>
/// EtherCAT 周期同步包装服务。基于闭源 <see cref="EtherCATMaster"/> 提供的有限 API：<br/>
/// • <c>TryInOutSync()</c> — 触发一次过程数据交换（DC SYNC0 由 Leal 主站内部驱动）<br/>
/// • <c>WriteSDO/ReadSDO</c> — 通过 SDO 修改 SM 分配 + PDO 映射（0x1C12/0x1C13/0x1600/0x1A00）<br/>
/// 不依赖 <c>AddRxPDOMapping</c> 等 Leal 私有 API，保证对其他 CiA402 从机的兼容性。<br/>
/// 仅由 ViewModel 在 CSP/CSV/CST 模式时按需创建、停止后即释放。
/// </summary>
public sealed class EtherCatCyclicSyncService : IDisposable
{
    private readonly EtherCATMaster _master;
    private ICyclicTimer? _timer;
    private long _tickCount;
    private long _errorCount;
    private bool _disposed;

    /// <summary>每次 InOutSync 触发前调用，调用方可在此写出 Outputs（如 ControlWord、TargetPosition）。</summary>
    public event Action? BeforeSync;

    /// <summary>每次 InOutSync 触发后调用，调用方可在此读入 Inputs（如 StatusWord、PositionActualValue）。</summary>
    public event Action? AfterSync;

    /// <summary>累计 InOutSync 触发次数。</summary>
    public long TickCount => Interlocked.Read(ref _tickCount);

    /// <summary>累计 InOutSync 返回 false（错误）次数。</summary>
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    /// <summary>最近一次实际周期的抖动 μs（仅诊断）。</summary>
    public long LastJitterMicros => _timer?.LastJitterMicros ?? 0;

    /// <summary>当前是否正在运行。</summary>
    public bool IsRunning => _timer is { IsRunning: true };

    /// <summary>底层定时器类型。</summary>
    public CyclicTimerKind? TimerKind => _timer?.Kind;

    /// <summary>底层 EtherCAT 主站（供调用方读写 Inputs/Outputs）。</summary>
    public EtherCATMaster Master => _master;

    public EtherCatCyclicSyncService(EtherCATMaster master)
    {
        _master = master ?? throw new ArgumentNullException(nameof(master));
    }

    /// <summary>
    /// 启动周期 InOutSync。<br/>
    /// 每个 tick 顺序：<see cref="BeforeSync"/> → <c>TryInOutSync()</c> → <see cref="AfterSync"/>。
    /// </summary>
    public void Start(TimeSpan period, CyclicTimerKind timerKind)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(EtherCatCyclicSyncService));
        }

        Stop();
        Interlocked.Exchange(ref _tickCount, 0);
        Interlocked.Exchange(ref _errorCount, 0);
        ICyclicTimer t = CyclicTimerFactory.Create(timerKind);
        _timer = t;
        t.Start(period, () =>
        {
            try { BeforeSync?.Invoke(); } catch { /* ignore */ }
            try
            {
                if (!_master.TryInOutSync())
                {
                    Interlocked.Increment(ref _errorCount);
                }
            }
            catch { Interlocked.Increment(ref _errorCount); }
            try { AfterSync?.Invoke(); } catch { /* ignore */ }
            Interlocked.Increment(ref _tickCount);
        });
    }

    /// <summary>停止周期循环。可重新 <see cref="Start"/>。</summary>
    public void Stop()
    {
        try { _timer?.Stop(); } catch { /* ignore */ }
        try { _timer?.Dispose(); } catch { /* ignore */ }
        _timer = null;
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

    // ════════════════════════════════════════════════════════════════════════
    //  PDO 映射配置（通过 SDO 通用方式，与具体 Leal API 解耦）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 配置 EtherCAT 从机的 RxPDO（SM2，主→从过程输出）。<br/>
    /// 前置：从机已处于 PreOp。CiA402 标准过程：<br/>
    /// 1) 写 0x1C12.0=0 解除 SM 分配<br/>
    /// 2) 写 0x1600.0=0 清空映射<br/>
    /// 3) 顺序写 0x1600.1..N 各条目<br/>
    /// 4) 写 0x1600.0=N<br/>
    /// 5) 写 0x1C12.1=0x1600, 0x1C12.0=1
    /// </summary>
    public bool ConfigureRxPdo(int slaveAddr, uint[] mapEntries, ushort pdoIndex = 0x1600)
        => ConfigurePdoCore(slaveAddr, smIndex: 0x1C12, pdoIndex, mapEntries);

    /// <summary>
    /// 配置 EtherCAT 从机的 TxPDO（SM3，从→主过程输入）。流程同 <see cref="ConfigureRxPdo"/>，
    /// 对象索引为 0x1C13 + 0x1A00。
    /// </summary>
    public bool ConfigureTxPdo(int slaveAddr, uint[] mapEntries, ushort pdoIndex = 0x1A00)
        => ConfigurePdoCore(slaveAddr, smIndex: 0x1C13, pdoIndex, mapEntries);

    private bool ConfigurePdoCore(int slaveAddr, ushort smIndex, ushort pdoIndex, uint[] mapEntries)
    {
        if (mapEntries is null || mapEntries.Length > 8)
        {
            throw new ArgumentException("映射条目数必须 0..8", nameof(mapEntries));
        }

        // 1) SM 分配清零
        if (!_master.TryWriteSDO<byte>(slaveAddr, smIndex, 0, 0))
        {
            return false;
        }
        // 2) 映射条目数清零
        if (!_master.TryWriteSDO<byte>(slaveAddr, pdoIndex, 0, 0))
        {
            return false;
        }
        // 3) 写各条目
        for (int i = 0; i < mapEntries.Length; i++)
        {
            if (!_master.TryWriteSDO<uint>(slaveAddr, pdoIndex, (byte)(i + 1), mapEntries[i]))
            {
                return false;
            }
        }
        // 4) 写条目数
        if (!_master.TryWriteSDO<byte>(slaveAddr, pdoIndex, 0, (byte)mapEntries.Length))
        {
            return false;
        }
        // 5) SM 分配
        if (!_master.TryWriteSDO<ushort>(slaveAddr, smIndex, 1, pdoIndex))
        {
            return false;
        }

        if (!_master.TryWriteSDO<byte>(slaveAddr, smIndex, 0, 1))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 配置 DC SyncManager 2/3 同步模式（0x1C32/0x1C33.1=SyncType）。<br/>
    /// SyncType: 0=FreeRun, 1=SM-Synchron, 2=DC-Sync0, 3=DC-Sync1。多数 CiA402 伺服 CSP/CSV/CST 推荐 2 (Sync0)。<br/>
    /// 失败可忽略 —— 部分从机不可写或自动选择同步源。
    /// </summary>
    public void TryConfigureSyncType(int slaveAddr, ushort syncType = 2)
    {
        try { _ = _master.TryWriteSDO<ushort>(slaveAddr, 0x1C32, 1, syncType); } catch { /* ignore */ }
        try { _ = _master.TryWriteSDO<ushort>(slaveAddr, 0x1C33, 1, syncType); } catch { /* ignore */ }
    }

    /// <summary>
    /// 把内置 CiA402 PDO 模板按 RPDO1/TPDO1（即 SM2/SM3 默认对象）下发到 <paramref name="slaveAddr"/>。<br/>
    /// EtherCAT SM 每方向仅一个映射对象（不像 CANopen 4 路 PDO），此处只取模板的 RPDO0/TPDO0。
    /// </summary>
    public bool ApplyTemplate(int slaveAddr, Cia402PdoTemplate template)
    {
        if (template is null)
        {
            throw new ArgumentNullException(nameof(template));
        }

        if (template.Rpdos.Length > 0 && template.Rpdos[0].Enabled
            && !ConfigureRxPdo(slaveAddr, template.Rpdos[0].MapEntries))
        {
            return false;
        }

        if (template.Tpdos.Length > 0 && template.Tpdos[0].Enabled
            && !ConfigureTxPdo(slaveAddr, template.Tpdos[0].MapEntries))
        {
            return false;
        }

        return true;
    }
}
