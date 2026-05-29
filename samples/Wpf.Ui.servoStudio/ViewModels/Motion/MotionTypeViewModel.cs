// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Windows.Threading;
using Core.Net.EtherCAT;
using Core.CANopen;
using Core.CANopen.CiA402;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Core.EtherCAT;
using Wpf.Ui.servoStudio.Core.Sync;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels.Motion;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Timer is lifecycle-managed via OnNavigatedFrom and StopCyclicSend")]
public partial class MotionTypeViewModel(DeviceAddViewModel deviceAddViewModel) : ViewModel
{
    private bool _isInitialized = false;

    /// <summary>
    /// 原点回归方法枚举值数组，与 HmMethodItems 索引一一对应
    /// </summary>
    private static readonly Cia402HomingMethod[] HomingMethodMap =
        (Cia402HomingMethod[])Enum.GetValues(typeof(Cia402HomingMethod));

    /// <summary>
    /// ComboBox index → Cia402OperationMode 映射表
    /// </summary>
    private static readonly Cia402OperationMode[] ModeMap =
    [
        Cia402OperationMode.ProfilePosition,           // 0
        Cia402OperationMode.Velocity,                  // 1
        Cia402OperationMode.ProfileVelocity,           // 2
        Cia402OperationMode.ProfileTorque,             // 3
        Cia402OperationMode.Homing,                    // 4
        Cia402OperationMode.InterpolatedPosition,      // 5
        Cia402OperationMode.CyclicSynchronousPosition, // 6
        Cia402OperationMode.CyclicSynchronousVelocity, // 7
        Cia402OperationMode.CyclicSynchronousTorque,   // 8
    ];

    #region 状态

    [ObservableProperty]
    private string _operationStatusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy = false;

    #endregion

    #region 周期同步发送

    private readonly DispatcherTimer _cyclicSendTimer = new();
    private int _cyclicSendCycleCount = 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCyclicSendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCyclicSendCommand))]
    private bool _isCyclicSendRunning = false;

    /// <summary>周期发送间隔 (ms)，最小 1 ms</summary>
    [ObservableProperty] private double _cyclicSendIntervalMs = 20;

    [ObservableProperty] private string _cyclicSendLastError = string.Empty;
    [ObservableProperty] private int _cyclicSendCycleCountDisplay = 0;

    private bool _isLoadingMotionSettings;
    partial void OnCyclicSendIntervalMsChanged(double value)
    {
        if (_isLoadingMotionSettings) return;
        try
        {
            var s = Services.UserSettingsService.Load();
            s.Motion_CyclicSendIntervalMs = value;
            Services.UserSettingsService.Save(s);
        }
        catch { }
    }

    private bool IsCsMode => SelectedOperationMode is
        Cia402OperationMode.CyclicSynchronousPosition or
        Cia402OperationMode.CyclicSynchronousVelocity or
        Cia402OperationMode.CyclicSynchronousTorque;

    private bool CanStartCyclicSend() => IsConnected && IsCsMode && !IsCyclicSendRunning;
    private bool CanStopCyclicSend() => IsCyclicSendRunning;

    [RelayCommand(CanExecute = nameof(CanStartCyclicSend))]
    private void OnStartCyclicSend()
    {
        if (IsCyclicSendRunning)
        {
            return;
        }

        _cyclicSendCycleCount = 0;
        CyclicSendCycleCountDisplay = 0;
        CyclicSendLastError = string.Empty;

        int intervalMs = Math.Max(1, (int)CyclicSendIntervalMs);

        // 优先尝试 PDO + SYNC（CANopen）或 PDO + InOutSync（EtherCAT）。失败回退到 DispatcherTimer + SDO。
        Services.UserSettings settings = Services.UserSettingsService.Load();
        bool preferPdo = settings.Motion_PreferPdoForCyclicSync;
        CyclicTimerKind timerKind = (CyclicTimerKind)System.Math.Clamp(settings.Motion_CyclicTimerKind, 0, 2);

        if (preferPdo && TryStartPdoPath(intervalMs, timerKind, out string pdoStatus))
        {
            IsCyclicSendRunning = true;
            OperationStatusText = pdoStatus;
            return;
        }

        _cyclicSendTimer.Stop();
        _cyclicSendTimer.Tick -= CyclicSendTimerTick;
        _cyclicSendTimer.Tick += CyclicSendTimerTick;
        _cyclicSendTimer.Interval = TimeSpan.FromMilliseconds(intervalMs);
        _cyclicSendTimer.Start();
        IsCyclicSendRunning = true;
        OperationStatusText = $"周期发送已启动 (SDO 路径) — 间隔 {intervalMs} ms";
    }

    [RelayCommand(CanExecute = nameof(CanStopCyclicSend))]
    private void OnStopCyclicSend()
    {
        StopCyclicSend();
    }

    private void StopCyclicSend()
    {
        _cyclicSendTimer.Stop();
        _cyclicSendTimer.Tick -= CyclicSendTimerTick;
        StopPdoPath();
        if (IsCyclicSendRunning)
        {
            IsCyclicSendRunning = false;
            OperationStatusText = $"周期发送已停止，共发送 {_cyclicSendCycleCount} 帧";
        }
    }

    private void CyclicSendTimerTick(object? sender, EventArgs e)
    {
        if (!IsConnected || !IsCsMode)
        {
            StopCyclicSend();
            return;
        }

        var errors = new List<string>();
        switch (SelectedOperationMode)
        {
            case Cia402OperationMode.CyclicSynchronousPosition:
                // 测试模式：在本次发送前按步长累加目标位置
                if (CspTestModeEnabled)
                {
                    double step = CspTestPositionStep ?? 0;
                    double next = (CspTargetPosition ?? 0) + step;
                    // 钳位到 INT32 范围，防止溢出
                    if (next > int.MaxValue) next = int.MaxValue;
                    else if (next < int.MinValue) next = int.MinValue;
                    CspTargetPosition = next;
                }

                SafeWriteSdo<int>(Cia402OdIndex.TargetPosition, 0, (int)(CspTargetPosition ?? 0), errors, "目标位置");
                SafeWriteSdo<int>(Cia402OdIndex.TargetVelocity, 0, (int)(CspTargetVelocity ?? 0), errors, "目标速度");
                SafeWriteSdo<int>(Cia402OdIndex.VelocityOffset, 0, (int)(CspVelocityOffset ?? 0), errors, "速度前馈");
                SafeWriteSdo<short>(Cia402OdIndex.TorqueOffset, 0, (short)(CspTorqueOffset ?? 0), errors, "转矩前馈");
                break;
            case Cia402OperationMode.CyclicSynchronousVelocity:
                SafeWriteSdo<int>(Cia402OdIndex.TargetVelocity, 0, (int)(CsvTargetVelocity ?? 0), errors, "目标速度");
                SafeWriteSdo<int>(Cia402OdIndex.VelocityOffset, 0, (int)(CsvVelocityOffset ?? 0), errors, "速度前馈");
                SafeWriteSdo<short>(Cia402OdIndex.TorqueOffset, 0, (short)(CsvTorqueOffset ?? 0), errors, "转矩前馈");
                break;
            case Cia402OperationMode.CyclicSynchronousTorque:
                SafeWriteSdo<short>(Cia402OdIndex.TargetTorque, 0, (short)(CstTargetTorque ?? 0), errors, "目标转矩");
                SafeWriteSdo<short>(Cia402OdIndex.TorqueOffset, 0, (short)(CstTorqueOffset ?? 0), errors, "转矩前馈");
                break;
            default:
                StopCyclicSend();
                return;
        }

        _cyclicSendCycleCount++;
        int count = _cyclicSendCycleCount;
        CyclicSendCycleCountDisplay = count;
        if (errors.Count > 0)
            CyclicSendLastError = $"帧#{count} 失败: {string.Join("; ", errors)}";
        if (count % 100 == 0)
            OperationStatusText = errors.Count > 0
                ? $"周期发送中 — 第 {count} 帧，本帧失败: {string.Join("; ", errors)}"
                : $"周期发送中 — 已发送 {count} 帧";
    }

    // ────────────────────────────────────────────────────────────────────
    //  CSP/CSV/CST 高实时 PDO + SYNC 路径
    //  - CANopen：ConfigurePdoMapping → NMT Start → StartSyncProducer(ICyclicTimer) → 每个 SyncTick 用 SendPdo 下发 RPDO
    //  - EtherCAT：ApplyTemplate → 用 EtherCatCyclicSyncService 周期 InOutSync；
    //              过程数据写入由 Leal 闭源主站内部缓冲完成（公开 API 中 Outputs 字节布局不可控），
    //              故本路径主要保证 DC-Sync0 + InOutSync 持续运行，命令值仍以 SDO 旁路写入（在 BeforeSync 回调中）。
    // ────────────────────────────────────────────────────────────────────

    private EtherCatCyclicSyncService? _ecatSyncService;
    private CanOpenMaster? _activeCanMaster;
    private byte _activeCanNode;
    private Cia402PdoTemplate? _activePdoTemplate;
    private long _pdoCycleCount;

    /// <summary>
    /// 尝试启动 PDO + SYNC 路径。仅在 CANopen 或 EtherCAT 连接并且 CSP/CSV/CST 模式有对应模板时返回 true。
    /// </summary>
    private bool TryStartPdoPath(int intervalMs, CyclicTimerKind timerKind, out string status)
    {
        status = string.Empty;
        Cia402PdoTemplate? template = Cia402PdoTemplates.ForMode(SelectedOperationMode);
        if (template is null)
        {
            return false;
        }

        ActiveProtocolStack proto = deviceAddViewModel.ActiveProtocol;
        TimeSpan period = TimeSpan.FromMilliseconds(intervalMs);

        if (proto == ActiveProtocolStack.CANopen)
        {
            CanOpenMaster? can = deviceAddViewModel.CanOpenMaster;
            CanOpenSlave_CiA402? slave = deviceAddViewModel.CurrentCanOpenAxis;
            if (can is null || slave is null)
            {
                return false;
            }
            return TryStartCanopenPdoPath(can, (byte)slave.SlaveAddr, template, period, timerKind, out status);
        }

        if (proto == ActiveProtocolStack.EtherCAT)
        {
            EtherCATMaster ecat = deviceAddViewModel.EcatMaster;
            EtherCATSlave_CiA402? ax = deviceAddViewModel.CurrentAxis;
            if (ax is null)
            {
                return false;
            }
            return TryStartEcatPdoPath(ecat, ax.SlaveAddr, template, period, timerKind, out status);
        }

        return false;
    }

    private bool TryStartCanopenPdoPath(
        CanOpenMaster can,
        byte node,
        Cia402PdoTemplate template,
        TimeSpan period,
        CyclicTimerKind timerKind,
        out string status)
    {
        try
        {
            // 进入 Pre-Operational 才能修改 PDO 通信/映射参数
            can.SendNmt(NmtCommand.EnterPreOperational, node);
            System.Threading.Thread.Sleep(20);

            for (byte i = 0; i < template.Rpdos.Length; i++)
            {
                Cia402PdoChannel ch = template.Rpdos[i];
                if (!ch.Enabled || ch.MapEntries.Length == 0)
                {
                    continue;
                }
                if (!can.ConfigureRpdoMapping(node, i, ch.CobIdOverride, ch.TransmissionType, ch.MapEntries))
                {
                    status = $"CANopen RPDO{i + 1} 映射写入失败 — 已回退到 SDO 路径";
                    return false;
                }
            }
            for (byte i = 0; i < template.Tpdos.Length; i++)
            {
                Cia402PdoChannel ch = template.Tpdos[i];
                if (!ch.Enabled || ch.MapEntries.Length == 0)
                {
                    continue;
                }
                if (!can.ConfigureTpdoMapping(node, i, ch.CobIdOverride, ch.TransmissionType, ch.MapEntries, ch.EventTimerMs, ch.InhibitTime100Us))
                {
                    status = $"CANopen TPDO{i + 1} 映射写入失败 — 已回退到 SDO 路径";
                    return false;
                }
            }

            // 进入 Operational
            can.SendNmt(NmtCommand.Start, node);
            System.Threading.Thread.Sleep(20);

            _activeCanMaster = can;
            _activeCanNode = node;
            _activePdoTemplate = template;
            _pdoCycleCount = 0;
            can.SyncTick += OnCanopenSyncTick;
            can.StartSyncProducer(period, timerKind, includeCounter: false);

            status = $"周期同步已启动 (CANopen PDO+SYNC, {CyclicTimerFactory.DisplayName(timerKind)}, {period.TotalMilliseconds:F1} ms)";
            return true;
        }
        catch (Exception ex)
        {
            status = $"CANopen PDO 路径启动异常：{ex.Message} — 已回退到 SDO 路径";
            return false;
        }
    }

    private bool TryStartEcatPdoPath(
        EtherCATMaster ecat,
        int slaveAddr,
        Cia402PdoTemplate template,
        TimeSpan period,
        CyclicTimerKind timerKind,
        out string status)
    {
        try
        {
            EtherCatCyclicSyncService svc = new(ecat);
            // 注意：EtherCAT 每个 SyncManager 仅一个 PDO 映射对象；模板的 RPDO0/TPDO0 即整段过程数据。
            if (!svc.ApplyTemplate(slaveAddr, template))
            {
                svc.Dispose();
                status = "EtherCAT PDO 映射写入失败 — 已回退到 SDO 路径";
                return false;
            }
            svc.TryConfigureSyncType(slaveAddr, syncType: 2);
            svc.BeforeSync += OnEcatBeforeSync;
            svc.Start(period, timerKind);
            _ecatSyncService = svc;
            _pdoCycleCount = 0;

            status = $"周期同步已启动 (EtherCAT InOutSync, {CyclicTimerFactory.DisplayName(timerKind)}, {period.TotalMilliseconds:F1} ms)";
            return true;
        }
        catch (Exception ex)
        {
            status = $"EtherCAT PDO 路径启动异常：{ex.Message} — 已回退到 SDO 路径";
            return false;
        }
    }

    /// <summary>停止 PDO 路径（与 SDO 路径互斥，可重复调用）。</summary>
    private void StopPdoPath()
    {
        if (_activeCanMaster is not null)
        {
            try
            {
                _activeCanMaster.SyncTick -= OnCanopenSyncTick;
                _activeCanMaster.StopSyncProducer();
            }
            catch
            {
                // ignore
            }
            _activeCanMaster = null;
            _activePdoTemplate = null;
            _activeCanNode = 0;
        }

        if (_ecatSyncService is not null)
        {
            try
            {
                _ecatSyncService.BeforeSync -= OnEcatBeforeSync;
                _ecatSyncService.Stop();
                _ecatSyncService.Dispose();
            }
            catch
            {
                // ignore
            }
            _ecatSyncService = null;
        }
    }

    /// <summary>CANopen SYNC 触发回调：按当前模板的 RPDO 映射构建并下发当前命令值。</summary>
    private void OnCanopenSyncTick()
    {
        CanOpenMaster? can = _activeCanMaster;
        Cia402PdoTemplate? tpl = _activePdoTemplate;
        if (can is null || tpl is null)
        {
            return;
        }

        for (byte i = 0; i < tpl.Rpdos.Length; i++)
        {
            Cia402PdoChannel ch = tpl.Rpdos[i];
            if (!ch.Enabled || ch.MapEntries.Length == 0)
            {
                continue;
            }
            byte[] payload = BuildRpdoPayload(ch);
            if (payload.Length == 0)
            {
                continue;
            }
            can.SendPdo(_activeCanNode, i, payload);
        }

        long n = System.Threading.Interlocked.Increment(ref _pdoCycleCount);
        if ((n % 100) == 0)
        {
            // 跨线程更新 UI 显示
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp is not null)
            {
                disp.BeginInvoke(new Action(() =>
                {
                    _cyclicSendCycleCount = (int)n;
                    CyclicSendCycleCountDisplay = (int)n;
                }));
            }
        }
    }

    /// <summary>
    /// EtherCAT InOutSync 前回调：当前 Leal 闭源 API 不暴露稳定的 Outputs 字节布局，
    /// 故此处仅以 SDO 旁路下发本帧命令值；下一阶段若获取到 Leal 私有 PDO 缓冲访问，再切换至零拷贝写入。
    /// </summary>
    private void OnEcatBeforeSync()
    {
        // 在后台 tick 线程上以 SDO 旁路写入；EtherCAT 的 SDO 写入由 Leal 主站序列化处理。
        try
        {
            switch (SelectedOperationMode)
            {
                case Cia402OperationMode.CyclicSynchronousPosition:
                    _ = TryWriteSdo<int>(Cia402OdIndex.TargetPosition, 0, (int)(CspTargetPosition ?? 0));
                    break;
                case Cia402OperationMode.CyclicSynchronousVelocity:
                    _ = TryWriteSdo<int>(Cia402OdIndex.TargetVelocity, 0, (int)(CsvTargetVelocity ?? 0));
                    break;
                case Cia402OperationMode.CyclicSynchronousTorque:
                    _ = TryWriteSdo<short>(Cia402OdIndex.TargetTorque, 0, (short)(CstTargetTorque ?? 0));
                    break;
            }
        }
        catch
        {
            // ignore
        }

        long n = System.Threading.Interlocked.Increment(ref _pdoCycleCount);
        if ((n % 100) == 0)
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp is not null)
            {
                disp.BeginInvoke(new Action(() =>
                {
                    _cyclicSendCycleCount = (int)n;
                    CyclicSendCycleCountDisplay = (int)n;
                }));
            }
        }
    }

    /// <summary>按 CiA-402 PDO 映射条目（高 16 = index，bits[15:8] = sub，bits[7:0] = 位宽）从当前命令属性构建 RPDO 字节流。</summary>
    private byte[] BuildRpdoPayload(Cia402PdoChannel ch)
    {
        var bytes = new List<byte>(8);
        foreach (uint entry in ch.MapEntries)
        {
            ushort idx = (ushort)(entry >> 16);
            byte bits = (byte)entry;

            switch (idx)
            {
                case Cia402OdIndex.ControlWord:
                    // 控制字由别处管理；周期下发占位为 0（部分驱动允许保持上一次值，安全策略）。
                    bytes.AddRange(BitConverter.GetBytes((ushort)0));
                    break;

                case Cia402OdIndex.ModesOfOperation:
                    bytes.Add((byte)(sbyte)SelectedOperationMode);
                    break;

                case Cia402OdIndex.TargetPosition:
                    bytes.AddRange(BitConverter.GetBytes((int)(CspTargetPosition ?? 0)));
                    break;

                case Cia402OdIndex.TargetVelocity:
                {
                    int tv = SelectedOperationMode == Cia402OperationMode.CyclicSynchronousVelocity
                        ? (int)(CsvTargetVelocity ?? 0)
                        : (int)(CspTargetVelocity ?? 0);
                    bytes.AddRange(BitConverter.GetBytes(tv));
                    break;
                }

                case Cia402OdIndex.TargetTorque:
                    bytes.AddRange(BitConverter.GetBytes((short)(CstTargetTorque ?? 0)));
                    break;

                case Cia402OdIndex.VelocityOffset:
                {
                    int vo = SelectedOperationMode == Cia402OperationMode.CyclicSynchronousVelocity
                        ? (int)(CsvVelocityOffset ?? 0)
                        : (int)(CspVelocityOffset ?? 0);
                    bytes.AddRange(BitConverter.GetBytes(vo));
                    break;
                }

                case Cia402OdIndex.TorqueOffset:
                {
                    short to = SelectedOperationMode switch
                    {
                        Cia402OperationMode.CyclicSynchronousVelocity => (short)(CsvTorqueOffset ?? 0),
                        Cia402OperationMode.CyclicSynchronousTorque => (short)(CstTorqueOffset ?? 0),
                        _ => (short)(CspTorqueOffset ?? 0),
                    };
                    bytes.AddRange(BitConverter.GetBytes(to));
                    break;
                }

                default:
                {
                    // 未知/未实现的映射字段：按位宽补 0 占位，避免错位
                    int byteLen = (bits + 7) / 8;
                    for (int k = 0; k < byteLen; k++)
                    {
                        bytes.Add(0);
                    }
                    break;
                }
            }
        }
        return bytes.ToArray();
    }

    #endregion

    #region 模式选择

    [ObservableProperty]
    private ObservableCollection<string> _comboBoxMotionMode = new(
    [
        "轮廓位置模式 (PP)",
        "速度模式 (VL)",
        "轮廓速度模式 (PV)",
        "轮廓转矩模式 (TQ)",
        "原点回归模式 (HM)",
        "插补位置模式 (IP)",
        "周期同步位置 (CSP)",
        "周期同步速度 (CSV)",
        "周期同步转矩 (CST)",
    ]);

    [ObservableProperty]
    private int _motionModeIndex;

    [ObservableProperty]
    private Cia402OperationMode _selectedOperationMode = Cia402OperationMode.ProfilePosition;

    [ObservableProperty]
    private string _selectedModeDescription = string.Empty;

    #endregion

    #region 面板可见性

    [ObservableProperty] private bool _isProfilePositionVisible = true;
    [ObservableProperty] private bool _isVelocityVisible;
    [ObservableProperty] private bool _isProfileVelocityVisible;
    [ObservableProperty] private bool _isProfileTorqueVisible;
    [ObservableProperty] private bool _isHomingVisible;
    [ObservableProperty] private bool _isInterpolatedPositionVisible;
    [ObservableProperty] private bool _isCspVisible;
    [ObservableProperty] private bool _isCsvVisible;
    [ObservableProperty] private bool _isCstVisible;

    #endregion

    #region 轮廓位置模式 (PP) — 0x607A / 0x6081 / 0x6083 / 0x6084 / 0x6085

    [ObservableProperty] private double? _ppTargetPosition = 0;
    [ObservableProperty] private double? _ppProfileVelocity = 0;
    [ObservableProperty] private double? _ppProfileAcceleration = 0;
    [ObservableProperty] private double? _ppProfileDeceleration = 0;
    [ObservableProperty] private double? _ppQuickStopDeceleration = 0;
    [ObservableProperty] private bool _ppIsRelative = false;
    [ObservableProperty] private bool _ppChangeImmediately = false;

    #endregion

    #region 速度模式 (VL) — 0x60FF

    [ObservableProperty] private double? _vlTargetVelocity = 0;

    #endregion

    #region 轮廓速度模式 (PV) — 0x60FF / 0x6083 / 0x6084 / 0x6085

    [ObservableProperty] private double? _pvTargetVelocity = 0;
    [ObservableProperty] private double? _pvProfileAcceleration = 0;
    [ObservableProperty] private double? _pvProfileDeceleration = 0;
    [ObservableProperty] private double? _pvQuickStopDeceleration = 0;

    #endregion

    #region 轮廓转矩模式 (TQ) — 0x6071 / 0x6072 / 0x6087

    [ObservableProperty] private double? _tqTargetTorque = 0;
    [ObservableProperty] private double? _tqMaxTorque = 0;
    [ObservableProperty] private double? _tqTorqueSlope = 0;

    #endregion

    #region 原点回归模式 (HM) — 0x6098 / 0x6099 / 0x609A

    [ObservableProperty] private ObservableCollection<string> _hmMethodItems = new();
    [ObservableProperty] private int _hmMethodIndex = 0;
    [ObservableProperty] private double? _hmSpeedDuringSearch = 0;
    [ObservableProperty] private double? _hmSpeedDuringZero = 0;
    [ObservableProperty] private double? _hmHomingAcceleration = 0;

    #endregion

    #region 插补位置模式 (IP) — 0x60C0 / 0x60C2

    [ObservableProperty] private double? _ipInterpolationTimePeriod = 0;

    #endregion

    #region 周期同步位置 (CSP) — 0x607A / 0x60FF / 0x60B0 / 0x60B1 / 0x60B2 / 0x60C2

    [ObservableProperty] private double? _cspTargetPosition = 0;
    [ObservableProperty] private double? _cspTargetVelocity = 0;
    [ObservableProperty] private double? _cspPositionOffset = 0;
    [ObservableProperty] private double? _cspVelocityOffset = 0;
    [ObservableProperty] private double? _cspTorqueOffset = 0;
    [ObservableProperty] private double? _cspInterpolationTimePeriod = 0;

    /// <summary>CSP 测试模式使能：启用后，每个周期向目标位置累加固定增量。</summary>
    [ObservableProperty] private bool _cspTestModeEnabled = false;

    /// <summary>CSP 测试模式——每个发送周期的目标位置增量 (counts)。可为负值表示反向。</summary>
    [ObservableProperty] private double? _cspTestPositionStep = 1000;

    #endregion

    #region 周期同步速度 (CSV) — 0x60FF / 0x60B1 / 0x60B2 / 0x60C2

    [ObservableProperty] private double? _csvTargetVelocity = 0;
    [ObservableProperty] private double? _csvVelocityOffset = 0;
    [ObservableProperty] private double? _csvTorqueOffset = 0;
    [ObservableProperty] private double? _csvInterpolationTimePeriod = 0;

    #endregion

    #region 周期同步转矩 (CST) — 0x6071 / 0x60B2 / 0x60C2

    [ObservableProperty] private double? _cstTargetTorque = 0;
    [ObservableProperty] private double? _cstTorqueOffset = 0;
    [ObservableProperty] private double? _cstInterpolationTimePeriod = 0;

    #endregion

    #region PP 模式 H05 扩展参数（厂商自定义寄存器）

    [ObservableProperty] private double? _ppH05PosCmdSource = 0;
    [ObservableProperty] private double? _ppH05GearNum1 = 1;
    [ObservableProperty] private double? _ppH05GearDen1 = 1;
    [ObservableProperty] private double? _ppH05PositionNearThreshold = 65535;
    [ObservableProperty] private double? _ppH05PositionThreshold = 30;
    [ObservableProperty] private double? _ppH05PositionLpfTimeMs = 0;
    [ObservableProperty] private double? _ppH05PositionSCurveMs = 0;

    #endregion

    #region VL 模式 H06 扩展参数（厂商自定义寄存器）

    [ObservableProperty] private double? _vlH06SpeedCmdSource = 0;
    [ObservableProperty] private double? _vlH06AccTimeMs = 100;
    [ObservableProperty] private double? _vlH06DecTimeMs = 100;
    [ObservableProperty] private double? _vlH06SCurveTimeMs = 0;
    [ObservableProperty] private double? _vlH06ZeroClampEn = 0;
    [ObservableProperty] private double? _vlH06ZeroClampThreshold = 10;

    #endregion

    #region PV 模式 H06 扩展参数（厂商自定义寄存器）

    [ObservableProperty] private double? _pvH06AccTimeMs = 100;
    [ObservableProperty] private double? _pvH06DecTimeMs = 100;
    [ObservableProperty] private double? _pvH06SCurveTimeMs = 0;
    [ObservableProperty] private double? _pvH06ZeroClampEn = 0;
    [ObservableProperty] private double? _pvH06ZeroClampThreshold = 10;

    #endregion

    #region TQ 模式 H07+H0F 扩展参数（厂商自定义寄存器）

    [ObservableProperty] private double? _tqH07TorqueCmdSource = 0;
    [ObservableProperty] private double? _tqH07TorqueLpfTimeMs = 0;
    [ObservableProperty] private double? _tqH07TorqueLimitPos = 18;
    [ObservableProperty] private double? _tqH07TorqueLimitNeg = 19;
    [ObservableProperty] private double? _tqH07SpeedModeTorqueLimitPos = 18;
    [ObservableProperty] private double? _tqH07SpeedModeTorqueLimitNeg = 18;
    [ObservableProperty] private double? _tqH0fSpeedLimitPos = 3000;
    [ObservableProperty] private double? _tqH0fSpeedLimitNeg = 3000;
    [ObservableProperty] private double? _tqH0fSpeedLimitEn = 0;

    #endregion

    #region HM 模式 H05+H0D 扩展参数（厂商自定义寄存器）

    [ObservableProperty] private double? _hmH05HomingTriggerMode = 0;
    [ObservableProperty] private double? _hmH05HomingHighSpeed = 100;
    [ObservableProperty] private double? _hmH05HomingLowSpeed = 10;
    [ObservableProperty] private double? _hmH05HomingAccDecMs = 1000;
    [ObservableProperty] private double? _hmH05HomeOffset = 0;
    [ObservableProperty] private double? _hmH05HomingTimeoutMs = 50000;

    #endregion

    #region IP 模式 H05 扩展参数（厂商自定义寄存器）

    [ObservableProperty] private double? _ipH05PositionThreshold = 30;
    [ObservableProperty] private double? _ipH05PositionLpfTimeMs = 0;

    #endregion

    #region CSP 模式 H08 扩展参数（厂商自定义寄存器）

    [ObservableProperty] private double? _cspH08SpeedKp1 = 7371;
    [ObservableProperty] private double? _cspH08SpeedKi1 = 169;
    [ObservableProperty] private double? _cspH08PositionKp1 = 1749;
    [ObservableProperty] private double? _cspH08PositionKd1 = 1900;
    [ObservableProperty] private double? _cspH08SpeedFeedForward = 0;
    [ObservableProperty] private double? _cspH08TorqueFeedForward = 0;
    [ObservableProperty] private double? _cspH08FeedForwardLpfMs = 0;
    [ObservableProperty] private double? _cspH05PositionThreshold = 30;

    #endregion

    #region CSV 模式 H08+H06 扩展参数（厂商自定义寄存器）

    [ObservableProperty] private double? _csvH08SpeedKp1 = 7371;
    [ObservableProperty] private double? _csvH08SpeedKi1 = 169;
    [ObservableProperty] private double? _csvH06AccTimeMs = 100;
    [ObservableProperty] private double? _csvH06DecTimeMs = 100;

    #endregion

    #region CST 模式 H07+H0F 扩展参数（厂商自定义寄存器）

    [ObservableProperty] private double? _cstH07TorqueLimitPos = 18;
    [ObservableProperty] private double? _cstH07TorqueLimitNeg = 19;
    [ObservableProperty] private double? _cstH0fSpeedLimitPos = 3000;
    [ObservableProperty] private double? _cstH0fSpeedLimitNeg = 3000;
    [ObservableProperty] private double? _cstH0fSpeedLimitEn = 0;

    #endregion

    #region 模式切换逻辑

    partial void OnMotionModeIndexChanged(int value)
    {
        if (value >= 0 && value < ModeMap.Length)
        {
            SelectedOperationMode = ModeMap[value];
            UpdateModeVisibility();
            UpdateModeDescription();
        }
    }

    private void UpdateModeVisibility()
    {
        IsProfilePositionVisible = SelectedOperationMode == Cia402OperationMode.ProfilePosition;
        IsVelocityVisible = SelectedOperationMode == Cia402OperationMode.Velocity;
        IsProfileVelocityVisible = SelectedOperationMode == Cia402OperationMode.ProfileVelocity;
        IsProfileTorqueVisible = SelectedOperationMode == Cia402OperationMode.ProfileTorque;
        IsHomingVisible = SelectedOperationMode == Cia402OperationMode.Homing;
        IsInterpolatedPositionVisible = SelectedOperationMode == Cia402OperationMode.InterpolatedPosition;
        IsCspVisible = SelectedOperationMode == Cia402OperationMode.CyclicSynchronousPosition;
        IsCsvVisible = SelectedOperationMode == Cia402OperationMode.CyclicSynchronousVelocity;
        IsCstVisible = SelectedOperationMode == Cia402OperationMode.CyclicSynchronousTorque;
    }

    private void UpdateModeDescription()
    {
        SelectedModeDescription = SelectedOperationMode switch
        {
            Cia402OperationMode.ProfilePosition => $"轮廓位置模式 — 写入运行模式 0x{Cia402OdIndex.ModesOfOperation:X4} = {(sbyte)Cia402OperationMode.ProfilePosition}，设置目标位置、速度与加减速参数，驱动器按梯形/S 形曲线运动到目标位置。",
            Cia402OperationMode.Velocity => $"速度模式 — 写入运行模式 0x{Cia402OdIndex.ModesOfOperation:X4} = {(sbyte)Cia402OperationMode.Velocity}，直接设置目标速度，驱动器以指定速度连续运行。",
            Cia402OperationMode.ProfileVelocity => $"轮廓速度模式 — 写入运行模式 0x{Cia402OdIndex.ModesOfOperation:X4} = {(sbyte)Cia402OperationMode.ProfileVelocity}，设置目标速度与加减速参数，驱动器按轮廓曲线达到目标速度。",
            Cia402OperationMode.ProfileTorque => $"轮廓转矩模式 — 写入运行模式 0x{Cia402OdIndex.ModesOfOperation:X4} = {(sbyte)Cia402OperationMode.ProfileTorque}，设置目标转矩与最大转矩，驱动器按转矩斜率输出到目标。",
            Cia402OperationMode.Homing => $"原点回归模式 — 写入运行模式 0x{Cia402OdIndex.ModesOfOperation:X4} = {(sbyte)Cia402OperationMode.Homing}，选择回零方式与速度，驱动器自动搜索机械原点。",
            Cia402OperationMode.InterpolatedPosition => $"插补位置模式 — 写入运行模式 0x{Cia402OdIndex.ModesOfOperation:X4} = {(sbyte)Cia402OperationMode.InterpolatedPosition}，主站按固定周期下发位置插补点，驱动器按时间同步执行。",
            Cia402OperationMode.CyclicSynchronousPosition => $"周期同步位置模式 — 写入运行模式 0x{Cia402OdIndex.ModesOfOperation:X4} = {(sbyte)Cia402OperationMode.CyclicSynchronousPosition}，主站每周期下发目标位置，驱动器实时跟踪。",
            Cia402OperationMode.CyclicSynchronousVelocity => $"周期同步速度模式 — 写入运行模式 0x{Cia402OdIndex.ModesOfOperation:X4} = {(sbyte)Cia402OperationMode.CyclicSynchronousVelocity}，主站每周期下发目标速度，驱动器实时跟踪。",
            Cia402OperationMode.CyclicSynchronousTorque => $"周期同步转矩模式 — 写入运行模式 0x{Cia402OdIndex.ModesOfOperation:X4} = {(sbyte)Cia402OperationMode.CyclicSynchronousTorque}，主站每周期下发目标转矩，驱动器实时跟踪。",
            _ => string.Empty,
        };
    }

    #endregion

    #region EtherCAT 辅助

    private IServoMaster Master => deviceAddViewModel.ActiveServoMaster;
    private IServoAxis? Axis => deviceAddViewModel.ActiveAxis;
    private bool IsConnected => deviceAddViewModel.IsAnyConnected && Axis != null;

    /// <summary>
    /// 通过 SDO 将值写入从站对象字典
    /// </summary>
    private bool TryWriteSdo<T>(ushort index, byte subIndex, T value) where T : struct
    {
        if (!IsConnected)
            return false;

        return Master.TryWriteSDO(Axis!.SlaveAddr, index, subIndex, value);
    }

    /// <summary>
    /// 通过 SDO 从从站对象字典读取值
    /// </summary>
    private bool TryReadSdo<T>(ushort index, byte subIndex, out T value) where T : struct
    {
        value = default;
        if (!IsConnected)
            return false;

        return Master.TryReadSDO(Axis!.SlaveAddr, index, subIndex, out value);
    }

    #endregion

    /// <summary>
    /// 带异常隔离的 SDO 写入 — 单个参数失败不影响批次中其他参数
    /// </summary>
    private void SafeWriteSdo<T>(ushort index, byte subIndex, T value, List<string> errors, string name) where T : struct
    {
        try
        {
            if (!TryWriteSdo<T>(index, subIndex, value))
            {
                errors.Add(name);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"{name}(异常:{ex.Message})");
        }
    }

    #region H 寄存器 SDO 辅助方法

    /// <summary>
    /// 通过 SDO 将无符号十六位值写入 H 寄存器（厂商自定义 SDO 对象）
    /// </summary>
    private void SafeWriteHReg(string hIndex, ushort value, List<string> errors, string friendlyName)
    {
        HRegisterEntry? entry = HVariables.FindByHIndex(hIndex);
        if (entry == null)
        {
            errors.Add($"{friendlyName}(未找到寄存器定义)");
            return;
        }

        try
        {
            if (!TryWriteSdo<ushort>(entry.SdoIndex, entry.SdoSubIndex, value))
            {
                errors.Add(friendlyName);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"{friendlyName}(异常:{ex.Message})");
        }
    }

    /// <summary>
    /// 通过 SDO 将有符号十六位值写入 H 寄存器
    /// </summary>
    private void SafeWriteHRegSigned(string hIndex, short value, List<string> errors, string friendlyName)
    {
        HRegisterEntry? entry = HVariables.FindByHIndex(hIndex);
        if (entry == null)
        {
            errors.Add($"{friendlyName}(未找到寄存器定义)");
            return;
        }

        try
        {
            if (!TryWriteSdo<short>(entry.SdoIndex, entry.SdoSubIndex, value))
            {
                errors.Add(friendlyName);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"{friendlyName}(异常:{ex.Message})");
        }
    }

    /// <summary>
    /// 通过 SDO 回读 H 寄存器 UINT16 值，成功则执行回调
    /// </summary>
    private void ReadHReg(string hIndex, Action<ushort> onSuccess)
    {
        HRegisterEntry? entry = HVariables.FindByHIndex(hIndex);
        if (entry == null)
        {
            return;
        }

        if (TryReadSdo<ushort>(entry.SdoIndex, entry.SdoSubIndex, out ushort v))
        {
            onSuccess(v);
        }
    }

    /// <summary>
    /// 通过 SDO 回读 H 寄存器 INT16 值
    /// </summary>
    private void ReadHRegSigned(string hIndex, Action<short> onSuccess)
    {
        HRegisterEntry? entry = HVariables.FindByHIndex(hIndex);
        if (entry == null)
        {
            return;
        }

        if (TryReadSdo<short>(entry.SdoIndex, entry.SdoSubIndex, out short v))
        {
            onSuccess(v);
        }
    }

    #endregion

    #region 命令

    /// <summary>
    /// 应用运行模式：将 SelectedOperationMode 写入 0x6060，然后回读 0x6061 验证
    /// </summary>
    [RelayCommand]
    private async Task OnApplyMode()
    {
        if (!IsConnected)
        {
            OperationStatusText = "设备未连接，请先在设备管理中连接 EtherCAT 从站";
            return;
        }

        IsBusy = true;
        OperationStatusText = $"正在切换运行模式至 {ComboBoxMotionMode[MotionModeIndex]}...";

        try
        {
            var modeValue = (sbyte)SelectedOperationMode;

            bool ok = await Task.Run(() =>
                TryWriteSdo<sbyte>(Cia402OdIndex.ModesOfOperation, 0, modeValue));

            if (!ok)
            {
                OperationStatusText = "写入运行模式 (0x6060) 失败";
                return;
            }

            // 回读 0x6061 验证
            sbyte displayMode = 0;
            bool readOk = await Task.Run(() =>
                TryReadSdo<sbyte>(Cia402OdIndex.ModesOfOperationDisplay, 0, out displayMode));

            if (readOk && displayMode == modeValue)
            {
                OperationStatusText = $"运行模式已切换为 {ComboBoxMotionMode[MotionModeIndex]} (0x6061 = {displayMode})";
                // 切换模式后读取当前模式的参数
                await ReadCurrentModeParametersAsync();
            }
            else if (readOk)
            {
                OperationStatusText = $"运行模式写入成功，但回读值不一致 (期望: {modeValue}, 实际: {displayMode})，驱动器可能不支持该模式";
            }
            else
            {
                OperationStatusText = "运行模式写入成功，但回读 0x6061 失败";
            }
        }
        catch (Exception ex)
        {
            OperationStatusText = $"切换运行模式异常: {ex.Message}";
            AppData.AppLogViewModel.Log(Models.AppLogLevel.Error, Models.AppLogCategory.EtherCAT, "切换运行模式异常", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 下发参数配置：根据当前选中的模式，将对应参数通过 SDO 写入从站对象字典
    /// </summary>
    [RelayCommand]
    private async Task OnApplyParameters()
    {
        if (!IsConnected)
        {
            OperationStatusText = "设备未连接，请先在设备管理中连接 EtherCAT 从站";
            return;
        }

        IsBusy = true;
        OperationStatusText = $"正在下发 {ComboBoxMotionMode[MotionModeIndex]} 参数...";

        try
        {
            var errors = new List<string>();

            await Task.Run(() =>
            {
                switch (SelectedOperationMode)
                {
                    case Cia402OperationMode.ProfilePosition:
                        WritePpParameters(errors);
                        break;
                    case Cia402OperationMode.Velocity:
                        WriteVlParameters(errors);
                        break;
                    case Cia402OperationMode.ProfileVelocity:
                        WritePvParameters(errors);
                        break;
                    case Cia402OperationMode.ProfileTorque:
                        WriteTqParameters(errors);
                        break;
                    case Cia402OperationMode.Homing:
                        WriteHmParameters(errors);
                        break;
                    case Cia402OperationMode.InterpolatedPosition:
                        WriteIpParameters(errors);
                        break;
                    case Cia402OperationMode.CyclicSynchronousPosition:
                        WriteCspParameters(errors);
                        break;
                    case Cia402OperationMode.CyclicSynchronousVelocity:
                        WriteCsvParameters(errors);
                        break;
                    case Cia402OperationMode.CyclicSynchronousTorque:
                        WriteCstParameters(errors);
                        break;
                }
            });

            if (errors.Count == 0)
            {
                OperationStatusText = $"{ComboBoxMotionMode[MotionModeIndex]} 参数下发成功";
            }
            else
            {
                OperationStatusText = $"部分参数写入失败: {string.Join("; ", errors)}";
            }
        }
        catch (Exception ex)
        {
            OperationStatusText = $"参数下发异常: {ex.Message}";
            AppData.AppLogViewModel.Log(Models.AppLogLevel.Error, Models.AppLogCategory.Parameter, "参数下发异常", ex.Message);
        }

        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 向 H0D.05 写 1 以主动触发原点回归
    /// </summary>
    [RelayCommand]
    private void OnTriggerHoming()
    {
        if (!IsConnected)
        {
            OperationStatusText = "设备未连接，无法触发回零";
            return;
        }

        List<string> errors = [];
        SafeWriteHReg("H0D.05", 1, errors, "H0D.05 主动回零触发");
        OperationStatusText = errors.Count == 0 ? "已触发主动回零 (H0D.05 = 1)" : $"触发失败: {string.Join("; ", errors)}";
    }

    #endregion

    #region 参数写入

    private void WritePpParameters(List<string> errors)
    {
        if (!TryWriteSdo<int>(Cia402OdIndex.TargetPosition, 0, (int)(PpTargetPosition ?? 0)))
            errors.Add("目标位置 (0x607A)");
        if (!TryWriteSdo<uint>(Cia402OdIndex.ProfileVelocity, 0, (uint)(PpProfileVelocity ?? 0)))
            errors.Add("轮廓速度 (0x6081)");
        if (!TryWriteSdo<uint>(Cia402OdIndex.ProfileAcceleration, 0, (uint)(PpProfileAcceleration ?? 0)))
            errors.Add("轮廓加速度 (0x6083)");
        if (!TryWriteSdo<uint>(Cia402OdIndex.ProfileDeceleration, 0, (uint)(PpProfileDeceleration ?? 0)))
            errors.Add("轮廓减速度 (0x6084)");
        if (!TryWriteSdo<uint>(Cia402OdIndex.QuickStopDeceleration, 0, (uint)(PpQuickStopDeceleration ?? 0)))
            errors.Add("急停减速度 (0x6085)");

        WritePpHParameters(errors);
    }

    private void WriteVlParameters(List<string> errors)
    {
        if (!TryWriteSdo<int>(Cia402OdIndex.TargetVelocity, 0, (int)(VlTargetVelocity ?? 0)))
            errors.Add("目标速度 (0x60FF)");

        WriteVlHParameters(errors);
    }

    private void WritePvParameters(List<string> errors)
    {
        if (!TryWriteSdo<int>(Cia402OdIndex.TargetVelocity, 0, (int)(PvTargetVelocity ?? 0)))
            errors.Add("目标速度 (0x60FF)");
        if (!TryWriteSdo<uint>(Cia402OdIndex.ProfileAcceleration, 0, (uint)(PvProfileAcceleration ?? 0)))
            errors.Add("轮廓加速度 (0x6083)");
        if (!TryWriteSdo<uint>(Cia402OdIndex.ProfileDeceleration, 0, (uint)(PvProfileDeceleration ?? 0)))
            errors.Add("轮廓减速度 (0x6084)");
        if (!TryWriteSdo<uint>(Cia402OdIndex.QuickStopDeceleration, 0, (uint)(PvQuickStopDeceleration ?? 0)))
            errors.Add("急停减速度 (0x6085)");

        WritePvHParameters(errors);
    }

    private void WriteTqParameters(List<string> errors)
    {
        if (!TryWriteSdo<short>(Cia402OdIndex.TargetTorque, 0, (short)(TqTargetTorque ?? 0)))
            errors.Add("目标转矩 (0x6071)");
        if (!TryWriteSdo<ushort>(Cia402OdIndex.MaxTorque, 0, (ushort)(TqMaxTorque ?? 0)))
            errors.Add("最大转矩 (0x6072)");
        if (!TryWriteSdo<uint>(Cia402OdIndex.TorqueSlope, 0, (uint)(TqTorqueSlope ?? 0)))
            errors.Add("转矩斜坡 (0x6087)");

        WriteTqHParameters(errors);
    }

    private void WriteHmParameters(List<string> errors)
    {
        // 获取当前选中的回零方法枚举值
        sbyte hmMethod = HmMethodIndex >= 0 && HmMethodIndex < HomingMethodMap.Length
            ? (sbyte)HomingMethodMap[HmMethodIndex]
            : (sbyte)0;

        if (!TryWriteSdo<sbyte>(Cia402OdIndex.HomingMethod, 0, hmMethod))
            errors.Add("回零方法 (0x6098)");
        if (!TryWriteSdo<uint>(Cia402OdIndex.HomingSpeeds, 1, (uint)(HmSpeedDuringSearch ?? 0)))
            errors.Add("寻找开关速度 (0x6099-1)");
        if (!TryWriteSdo<uint>(Cia402OdIndex.HomingSpeeds, 2, (uint)(HmSpeedDuringZero ?? 0)))
            errors.Add("寻找零点速度 (0x6099-2)");
        if (!TryWriteSdo<uint>(Cia402OdIndex.HomingAcceleration, 0, (uint)(HmHomingAcceleration ?? 0)))
            errors.Add("回零加速度 (0x609A)");

        WriteHmHParameters(errors);
    }

    private void WriteIpParameters(List<string> errors)
    {
        if (!TryWriteSdo<uint>(Cia402OdIndex.InterpolationTimePeriod, 1, (uint)(IpInterpolationTimePeriod ?? 0)))
            errors.Add("插补周期 (0x60C2)");

        WriteIpHParameters(errors);
    }

    private void WriteCspParameters(List<string> errors)
    {
        SafeWriteSdo<int>(Cia402OdIndex.TargetPosition, 0, (int)(CspTargetPosition ?? 0), errors, "目标位置 (0x607A)");
        SafeWriteSdo<int>(Cia402OdIndex.TargetVelocity, 0, (int)(CspTargetVelocity ?? 0), errors, "目标速度 (0x60FF)");
        SafeWriteSdo<int>(Cia402OdIndex.PositionOffset, 0, (int)(CspPositionOffset ?? 0), errors, "位置前馈 (0x60B0)");
        SafeWriteSdo<int>(Cia402OdIndex.VelocityOffset, 0, (int)(CspVelocityOffset ?? 0), errors, "速度前馈 (0x60B1)");
        SafeWriteSdo<short>(Cia402OdIndex.TorqueOffset, 0, (short)(CspTorqueOffset ?? 0), errors, "转矩前馈 (0x60B2)");
        SafeWriteSdo<uint>(Cia402OdIndex.InterpolationTimePeriod, 1, (uint)(CspInterpolationTimePeriod ?? 0), errors, "插补周期 (0x60C2)");

        WriteCspHParameters(errors);
    }

    private void WriteCsvParameters(List<string> errors)
    {
        SafeWriteSdo<int>(Cia402OdIndex.TargetVelocity, 0, (int)(CsvTargetVelocity ?? 0), errors, "目标速度 (0x60FF)");
        SafeWriteSdo<int>(Cia402OdIndex.VelocityOffset, 0, (int)(CsvVelocityOffset ?? 0), errors, "速度前馈 (0x60B1)");
        SafeWriteSdo<short>(Cia402OdIndex.TorqueOffset, 0, (short)(CsvTorqueOffset ?? 0), errors, "转矩前馈 (0x60B2)");
        SafeWriteSdo<uint>(Cia402OdIndex.InterpolationTimePeriod, 1, (uint)(CsvInterpolationTimePeriod ?? 0), errors, "插补周期 (0x60C2)");

        WriteCsvHParameters(errors);
    }

    private void WriteCstParameters(List<string> errors)
    {
        SafeWriteSdo<short>(Cia402OdIndex.TargetTorque, 0, (short)(CstTargetTorque ?? 0), errors, "目标转矩 (0x6071)");
        SafeWriteSdo<short>(Cia402OdIndex.TorqueOffset, 0, (short)(CstTorqueOffset ?? 0), errors, "转矩前馈 (0x60B2)");
        SafeWriteSdo<uint>(Cia402OdIndex.InterpolationTimePeriod, 1, (uint)(CstInterpolationTimePeriod ?? 0), errors, "插补周期 (0x60C2)");

        WriteCstHParameters(errors);
    }

    private void WritePpHParameters(List<string> errors)
    {
        SafeWriteHReg("H05.00", (ushort)(PpH05PosCmdSource ?? 0), errors, "H05.00 位置指令来源");
        SafeWriteHReg("H05.02", (ushort)(PpH05GearNum1 ?? 1), errors, "H05.02 电子齿轮分子1");
        SafeWriteHReg("H05.04", (ushort)(PpH05GearDen1 ?? 1), errors, "H05.04 电子齿轮分母1");
        SafeWriteHReg("H05.20", (ushort)(PpH05PositionNearThreshold ?? 65535), errors, "H05.20 定位接近阈值");
        SafeWriteHReg("H05.21", (ushort)(PpH05PositionThreshold ?? 30), errors, "H05.21 定位完成阈值");
        SafeWriteHReg("H05.30", (ushort)(PpH05PositionLpfTimeMs ?? 0), errors, "H05.30 位置低通滤波");
        SafeWriteHReg("H05.31", (ushort)(PpH05PositionSCurveMs ?? 0), errors, "H05.31 位置S曲线滤波");
    }

    private void WriteVlHParameters(List<string> errors)
    {
        SafeWriteHReg("H06.00", (ushort)(VlH06SpeedCmdSource ?? 0), errors, "H06.00 速度指令来源");
        SafeWriteHReg("H06.05", (ushort)(VlH06AccTimeMs ?? 100), errors, "H06.05 加速时间");
        SafeWriteHReg("H06.06", (ushort)(VlH06DecTimeMs ?? 100), errors, "H06.06 减速时间");
        SafeWriteHReg("H06.07", (ushort)(VlH06SCurveTimeMs ?? 0), errors, "H06.07 S曲线时间");
        SafeWriteHReg("H06.10", (ushort)(VlH06ZeroClampEn ?? 0), errors, "H06.10 零速钳位使能");
        SafeWriteHReg("H06.11", (ushort)(VlH06ZeroClampThreshold ?? 10), errors, "H06.11 零速钳位阈值");
    }

    private void WritePvHParameters(List<string> errors)
    {
        SafeWriteHReg("H06.05", (ushort)(PvH06AccTimeMs ?? 100), errors, "H06.05 加速时间");
        SafeWriteHReg("H06.06", (ushort)(PvH06DecTimeMs ?? 100), errors, "H06.06 减速时间");
        SafeWriteHReg("H06.07", (ushort)(PvH06SCurveTimeMs ?? 0), errors, "H06.07 S曲线时间");
        SafeWriteHReg("H06.10", (ushort)(PvH06ZeroClampEn ?? 0), errors, "H06.10 零速钳位使能");
        SafeWriteHReg("H06.11", (ushort)(PvH06ZeroClampThreshold ?? 10), errors, "H06.11 零速钳位阈值");
    }

    private void WriteTqHParameters(List<string> errors)
    {
        SafeWriteHReg("H07.00", (ushort)(TqH07TorqueCmdSource ?? 0), errors, "H07.00 转矩指令来源");
        SafeWriteHReg("H07.05", (ushort)(TqH07TorqueLpfTimeMs ?? 0), errors, "H07.05 转矩滤波");
        SafeWriteHReg("H07.09", (ushort)(TqH07TorqueLimitPos ?? 18), errors, "H07.09 正内部转矩限制");
        SafeWriteHReg("H07.10", (ushort)(TqH07TorqueLimitNeg ?? 19), errors, "H07.10 负内部转矩限制");
        SafeWriteHReg("H07.11", (ushort)(TqH07SpeedModeTorqueLimitPos ?? 18), errors, "H07.11 速度模式正转矩限制");
        SafeWriteHReg("H07.12", (ushort)(TqH07SpeedModeTorqueLimitNeg ?? 18), errors, "H07.12 速度模式负转矩限制");
        SafeWriteHReg("H0F.20", (ushort)(TqH0fSpeedLimitPos ?? 3000), errors, "H0F.20 正向速度限制");
        SafeWriteHReg("H0F.21", (ushort)(TqH0fSpeedLimitNeg ?? 3000), errors, "H0F.21 负向速度限制");
        SafeWriteHReg("H0F.22", (ushort)(TqH0fSpeedLimitEn ?? 0), errors, "H0F.22 速度限制使能");
    }

    private void WriteHmHParameters(List<string> errors)
    {
        SafeWriteHReg("H05.32", (ushort)(HmH05HomingTriggerMode ?? 0), errors, "H05.32 原点回归触发方式");
        SafeWriteHReg("H05.33", (ushort)(HmH05HomingHighSpeed ?? 100), errors, "H05.33 回零高速搜索速度");
        SafeWriteHReg("H05.34", (ushort)(HmH05HomingLowSpeed ?? 10), errors, "H05.34 回零低速找零速度");
        SafeWriteHReg("H05.35", (ushort)(HmH05HomingAccDecMs ?? 1000), errors, "H05.35 回零加减速时间");
        SafeWriteHRegSigned("H05.36", (short)(HmH05HomeOffset ?? 0), errors, "H05.36 机械原点偏移量");
        SafeWriteHReg("H05.37", (ushort)(HmH05HomingTimeoutMs ?? 50000), errors, "H05.37 回零限位时间");
    }

    private void WriteIpHParameters(List<string> errors)
    {
        SafeWriteHReg("H05.21", (ushort)(IpH05PositionThreshold ?? 30), errors, "H05.21 定位完成阈值");
        SafeWriteHReg("H05.30", (ushort)(IpH05PositionLpfTimeMs ?? 0), errors, "H05.30 位置低通滤波");
    }

    private void WriteCspHParameters(List<string> errors)
    {
        SafeWriteHReg("H08.00", (ushort)(CspH08SpeedKp1 ?? 7371), errors, "H08.00 速度环增益1");
        SafeWriteHReg("H08.01", (ushort)(CspH08SpeedKi1 ?? 169), errors, "H08.01 速度环积分1");
        SafeWriteHReg("H08.02", (ushort)(CspH08PositionKp1 ?? 1749), errors, "H08.02 位置环增益1");
        SafeWriteHReg("H08.03", (ushort)(CspH08PositionKd1 ?? 1900), errors, "H08.03 位置环微分1");
        SafeWriteHReg("H08.21", (ushort)(CspH08SpeedFeedForward ?? 0), errors, "H08.21 速度前馈增益");
        SafeWriteHReg("H08.22", (ushort)(CspH08TorqueFeedForward ?? 0), errors, "H08.22 转矩前馈增益");
        SafeWriteHReg("H08.23", (ushort)(CspH08FeedForwardLpfMs ?? 0), errors, "H08.23 前馈滤波");
        SafeWriteHReg("H05.21", (ushort)(CspH05PositionThreshold ?? 30), errors, "H05.21 定位完成阈值");
    }

    private void WriteCsvHParameters(List<string> errors)
    {
        SafeWriteHReg("H08.00", (ushort)(CsvH08SpeedKp1 ?? 7371), errors, "H08.00 速度环增益1");
        SafeWriteHReg("H08.01", (ushort)(CsvH08SpeedKi1 ?? 169), errors, "H08.01 速度环积分1");
        SafeWriteHReg("H06.05", (ushort)(CsvH06AccTimeMs ?? 100), errors, "H06.05 加速时间");
        SafeWriteHReg("H06.06", (ushort)(CsvH06DecTimeMs ?? 100), errors, "H06.06 减速时间");
    }

    private void WriteCstHParameters(List<string> errors)
    {
        SafeWriteHReg("H07.09", (ushort)(CstH07TorqueLimitPos ?? 18), errors, "H07.09 正内部转矩限制");
        SafeWriteHReg("H07.10", (ushort)(CstH07TorqueLimitNeg ?? 19), errors, "H07.10 负内部转矩限制");
        SafeWriteHReg("H0F.20", (ushort)(CstH0fSpeedLimitPos ?? 3000), errors, "H0F.20 正向速度限制");
        SafeWriteHReg("H0F.21", (ushort)(CstH0fSpeedLimitNeg ?? 3000), errors, "H0F.21 负向速度限制");
        SafeWriteHReg("H0F.22", (ushort)(CstH0fSpeedLimitEn ?? 0), errors, "H0F.22 速度限制使能");
    }

    #endregion

    #region 参数回读

    /// <summary>
    /// 根据当前运行模式，从从站回读对应的参数值到 UI
    /// </summary>
    private async Task ReadCurrentModeParametersAsync()
    {
        if (!IsConnected)
            return;

        await Task.Run(() =>
        {
            switch (SelectedOperationMode)
            {
                case Cia402OperationMode.ProfilePosition:
                    ReadPpParameters();
                    break;
                case Cia402OperationMode.Velocity:
                    ReadVlParameters();
                    break;
                case Cia402OperationMode.ProfileVelocity:
                    ReadPvParameters();
                    break;
                case Cia402OperationMode.ProfileTorque:
                    ReadTqParameters();
                    break;
                case Cia402OperationMode.Homing:
                    ReadHmParameters();
                    break;
                case Cia402OperationMode.InterpolatedPosition:
                    ReadIpParameters();
                    break;
                case Cia402OperationMode.CyclicSynchronousPosition:
                    ReadCspParameters();
                    break;
                case Cia402OperationMode.CyclicSynchronousVelocity:
                    ReadCsvParameters();
                    break;
                case Cia402OperationMode.CyclicSynchronousTorque:
                    ReadCstParameters();
                    break;
            }
        });
    }

    private void ReadPpParameters()
    {
        if (TryReadSdo<int>(Cia402OdIndex.TargetPosition, 0, out var pos))
            PpTargetPosition = pos;
        if (TryReadSdo<uint>(Cia402OdIndex.ProfileVelocity, 0, out var vel))
            PpProfileVelocity = vel;
        if (TryReadSdo<uint>(Cia402OdIndex.ProfileAcceleration, 0, out var acc))
            PpProfileAcceleration = acc;
        if (TryReadSdo<uint>(Cia402OdIndex.ProfileDeceleration, 0, out var dec))
            PpProfileDeceleration = dec;
        if (TryReadSdo<uint>(Cia402OdIndex.QuickStopDeceleration, 0, out var qs))
            PpQuickStopDeceleration = qs;

        ReadPpHParameters();
    }

    private void ReadVlParameters()
    {
        if (TryReadSdo<int>(Cia402OdIndex.TargetVelocity, 0, out var vel))
            VlTargetVelocity = vel;

        ReadVlHParameters();
    }

    private void ReadPvParameters()
    {
        if (TryReadSdo<int>(Cia402OdIndex.TargetVelocity, 0, out var vel))
            PvTargetVelocity = vel;
        if (TryReadSdo<uint>(Cia402OdIndex.ProfileAcceleration, 0, out var acc))
            PvProfileAcceleration = acc;
        if (TryReadSdo<uint>(Cia402OdIndex.ProfileDeceleration, 0, out var dec))
            PvProfileDeceleration = dec;
        if (TryReadSdo<uint>(Cia402OdIndex.QuickStopDeceleration, 0, out var qs))
            PvQuickStopDeceleration = qs;

        ReadPvHParameters();
    }

    private void ReadTqParameters()
    {
        if (TryReadSdo<short>(Cia402OdIndex.TargetTorque, 0, out var tq))
            TqTargetTorque = tq;
        if (TryReadSdo<ushort>(Cia402OdIndex.MaxTorque, 0, out var max))
            TqMaxTorque = max;
        if (TryReadSdo<uint>(Cia402OdIndex.TorqueSlope, 0, out var slope))
            TqTorqueSlope = slope;

        ReadTqHParameters();
    }

    private void ReadHmParameters()
    {
        if (TryReadSdo<sbyte>(Cia402OdIndex.HomingMethod, 0, out var method))
        {
            // 查找枚举值对应的索引
            int idx = Array.IndexOf(HomingMethodMap, (Cia402HomingMethod)method);
            if (idx >= 0)
                Application.Current.Dispatcher.Invoke(() => SetHmMethodIndex(idx));
        }

        if (TryReadSdo<uint>(Cia402OdIndex.HomingSpeeds, 1, out var searchSpd))
            HmSpeedDuringSearch = searchSpd;
        if (TryReadSdo<uint>(Cia402OdIndex.HomingSpeeds, 2, out var zeroSpd))
            HmSpeedDuringZero = zeroSpd;
        if (TryReadSdo<uint>(Cia402OdIndex.HomingAcceleration, 0, out var acc))
            HmHomingAcceleration = acc;

        ReadHmHParameters();
    }

    private void SetHmMethodIndex(int idx)
    {
        HmMethodIndex = idx;
    }

    private void ReadIpParameters()
    {
        if (TryReadSdo<uint>(Cia402OdIndex.InterpolationTimePeriod, 1, out var period))
            IpInterpolationTimePeriod = period;

        ReadIpHParameters();
    }

    private void ReadCspParameters()
    {
        if (TryReadSdo<int>(Cia402OdIndex.TargetPosition, 0, out var pos))
            CspTargetPosition = pos;
        if (TryReadSdo<int>(Cia402OdIndex.TargetVelocity, 0, out var vel))
            CspTargetVelocity = vel;
        if (TryReadSdo<int>(Cia402OdIndex.PositionOffset, 0, out var posOff))
            CspPositionOffset = posOff;
        if (TryReadSdo<int>(Cia402OdIndex.VelocityOffset, 0, out var velOff))
            CspVelocityOffset = velOff;
        if (TryReadSdo<short>(Cia402OdIndex.TorqueOffset, 0, out var tqOff))
            CspTorqueOffset = tqOff;
        if (TryReadSdo<uint>(Cia402OdIndex.InterpolationTimePeriod, 1, out var period))
            CspInterpolationTimePeriod = period;

        ReadCspHParameters();
    }

    private void ReadCsvParameters()
    {
        if (TryReadSdo<int>(Cia402OdIndex.TargetVelocity, 0, out var vel))
            CsvTargetVelocity = vel;
        if (TryReadSdo<int>(Cia402OdIndex.VelocityOffset, 0, out var velOff))
            CsvVelocityOffset = velOff;
        if (TryReadSdo<short>(Cia402OdIndex.TorqueOffset, 0, out var tqOff))
            CsvTorqueOffset = tqOff;
        if (TryReadSdo<uint>(Cia402OdIndex.InterpolationTimePeriod, 1, out var period))
            CsvInterpolationTimePeriod = period;

        ReadCsvHParameters();
    }

    private void ReadCstParameters()
    {
        if (TryReadSdo<short>(Cia402OdIndex.TargetTorque, 0, out var tq))
            CstTargetTorque = tq;
        if (TryReadSdo<short>(Cia402OdIndex.TorqueOffset, 0, out var tqOff))
            CstTorqueOffset = tqOff;
        if (TryReadSdo<uint>(Cia402OdIndex.InterpolationTimePeriod, 1, out var period))
            CstInterpolationTimePeriod = period;

        ReadCstHParameters();
    }

    private void ReadPpHParameters()
    {
        ReadHReg("H05.00", v => PpH05PosCmdSource = v);
        ReadHReg("H05.02", v => PpH05GearNum1 = v);
        ReadHReg("H05.04", v => PpH05GearDen1 = v);
        ReadHReg("H05.20", v => PpH05PositionNearThreshold = v);
        ReadHReg("H05.21", v => PpH05PositionThreshold = v);
        ReadHReg("H05.30", v => PpH05PositionLpfTimeMs = v);
        ReadHReg("H05.31", v => PpH05PositionSCurveMs = v);
    }

    private void ReadVlHParameters()
    {
        ReadHReg("H06.00", v => VlH06SpeedCmdSource = v);
        ReadHReg("H06.05", v => VlH06AccTimeMs = v);
        ReadHReg("H06.06", v => VlH06DecTimeMs = v);
        ReadHReg("H06.07", v => VlH06SCurveTimeMs = v);
        ReadHReg("H06.10", v => VlH06ZeroClampEn = v);
        ReadHReg("H06.11", v => VlH06ZeroClampThreshold = v);
    }

    private void ReadPvHParameters()
    {
        ReadHReg("H06.05", v => PvH06AccTimeMs = v);
        ReadHReg("H06.06", v => PvH06DecTimeMs = v);
        ReadHReg("H06.07", v => PvH06SCurveTimeMs = v);
        ReadHReg("H06.10", v => PvH06ZeroClampEn = v);
        ReadHReg("H06.11", v => PvH06ZeroClampThreshold = v);
    }

    private void ReadTqHParameters()
    {
        ReadHReg("H07.00", v => TqH07TorqueCmdSource = v);
        ReadHReg("H07.05", v => TqH07TorqueLpfTimeMs = v);
        ReadHReg("H07.09", v => TqH07TorqueLimitPos = v);
        ReadHReg("H07.10", v => TqH07TorqueLimitNeg = v);
        ReadHReg("H07.11", v => TqH07SpeedModeTorqueLimitPos = v);
        ReadHReg("H07.12", v => TqH07SpeedModeTorqueLimitNeg = v);
        ReadHReg("H0F.20", v => TqH0fSpeedLimitPos = v);
        ReadHReg("H0F.21", v => TqH0fSpeedLimitNeg = v);
        ReadHReg("H0F.22", v => TqH0fSpeedLimitEn = v);
    }

    private void ReadHmHParameters()
    {
        ReadHReg("H05.32", v => HmH05HomingTriggerMode = v);
        ReadHReg("H05.33", v => HmH05HomingHighSpeed = v);
        ReadHReg("H05.34", v => HmH05HomingLowSpeed = v);
        ReadHReg("H05.35", v => HmH05HomingAccDecMs = v);
        ReadHRegSigned("H05.36", v => HmH05HomeOffset = v);
        ReadHReg("H05.37", v => HmH05HomingTimeoutMs = v);
    }

    private void ReadIpHParameters()
    {
        ReadHReg("H05.21", v => IpH05PositionThreshold = v);
        ReadHReg("H05.30", v => IpH05PositionLpfTimeMs = v);
    }

    private void ReadCspHParameters()
    {
        ReadHReg("H08.00", v => CspH08SpeedKp1 = v);
        ReadHReg("H08.01", v => CspH08SpeedKi1 = v);
        ReadHReg("H08.02", v => CspH08PositionKp1 = v);
        ReadHReg("H08.03", v => CspH08PositionKd1 = v);
        ReadHReg("H08.21", v => CspH08SpeedFeedForward = v);
        ReadHReg("H08.22", v => CspH08TorqueFeedForward = v);
        ReadHReg("H08.23", v => CspH08FeedForwardLpfMs = v);
        ReadHReg("H05.21", v => CspH05PositionThreshold = v);
    }

    private void ReadCsvHParameters()
    {
        ReadHReg("H08.00", v => CsvH08SpeedKp1 = v);
        ReadHReg("H08.01", v => CsvH08SpeedKi1 = v);
        ReadHReg("H06.05", v => CsvH06AccTimeMs = v);
        ReadHReg("H06.06", v => CsvH06DecTimeMs = v);
    }

    private void ReadCstHParameters()
    {
        ReadHReg("H07.09", v => CstH07TorqueLimitPos = v);
        ReadHReg("H07.10", v => CstH07TorqueLimitNeg = v);
        ReadHReg("H0F.20", v => CstH0fSpeedLimitPos = v);
        ReadHReg("H0F.21", v => CstH0fSpeedLimitNeg = v);
        ReadHReg("H0F.22", v => CstH0fSpeedLimitEn = v);
    }

    #endregion

    #region 生命周期

    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            InitializeViewModel();
        }

        // 每次导航到页面时，检查连接状态并尝试回读当前模式
        _ = RefreshConnectionAndReadModeAsync();
    }

    public override void OnNavigatedFrom()
    {
        StopCyclicSend();
    }

    private void InitializeViewModel()
    {
        _isInitialized = true;

        // 加载用户保存的周期发送间隔
        try
        {
            var savedInterval = Services.UserSettingsService.Load().Motion_CyclicSendIntervalMs;
            if (savedInterval >= 1)
            {
                _isLoadingMotionSettings = true;
                try { CyclicSendIntervalMs = savedInterval; }
                finally { _isLoadingMotionSettings = false; }
            }
        }
        catch
        {
            // ignore
        }

        // 填充原点回归方法列表
        HmMethodItems.Clear();
        foreach (Cia402HomingMethod method in Enum.GetValues(typeof(Cia402HomingMethod)))
        {
            string name = method switch
            {
                Cia402HomingMethod.MethodNotDefined => "未定义 (0)",
                Cia402HomingMethod.NegativeLimitSwitch => "负限位开关 (1)",
                Cia402HomingMethod.PositiveLimitSwitch => "正限位开关 (2)",
                Cia402HomingMethod.PositiveHomeSwitchNeg => "正原点开关-负向 (3)",
                Cia402HomingMethod.PositiveHomeSwitchPos => "正原点开关-正向 (4)",
                Cia402HomingMethod.NegativeHomeSwitchNeg => "负原点开关-负向 (5)",
                Cia402HomingMethod.NegativeHomeSwitchPos => "负原点开关-正向 (6)",
                Cia402HomingMethod.HomeSwitchNegWithIndex => "原点开关-负向+索引 (7)",
                Cia402HomingMethod.HomeSwitchPosWithIndex => "原点开关-正向+索引 (8)",
                Cia402HomingMethod.HomeSwitchNegWithoutIndex => "原点开关-负向 (9)",
                Cia402HomingMethod.HomeSwitchPosWithoutIndex => "原点开关-正向 (10)",
                Cia402HomingMethod.NegLimitWithIndex => "负限位+索引 (11)",
                Cia402HomingMethod.PosLimitWithIndex => "正限位+索引 (12)",
                Cia402HomingMethod.IndexNegative => "索引脉冲-负向 (33)",
                Cia402HomingMethod.IndexPositive => "索引脉冲-正向 (34)",
                Cia402HomingMethod.CurrentPosition => "当前位置 (35)",
                _ => method.ToString(),
            };
            HmMethodItems.Add(name);
        }

        UpdateModeDescription();
    }

    /// <summary>
    /// 导航到页面时，回读当前驱动器运行模式并同步 UI，然后回读该模式参数
    /// </summary>
    private async Task RefreshConnectionAndReadModeAsync()
    {
        if (!IsConnected)
        {
            OperationStatusText = "设备未连接";
            return;
        }

        try
        {
            // 读取从站当前运行模式 (0x6061)
            sbyte displayMode = 0;
            bool ok = await Task.Run(() =>
                TryReadSdo<sbyte>(Cia402OdIndex.ModesOfOperationDisplay, 0, out displayMode));

            if (ok && displayMode != 0)
            {
                var readMode = (Cia402OperationMode)displayMode;
                int idx = Array.IndexOf(ModeMap, readMode);
                if (idx >= 0)
                {
                    MotionModeIndex = idx;
                    OperationStatusText = $"当前有效模式: {ComboBoxMotionMode[idx]}";
                }
                else
                {
                    OperationStatusText = $"从站返回模式值 {displayMode}，暂不支持";
                }
            }
            else
            {
                OperationStatusText = "已连接，运行模式未设置";
            }

            await ReadCurrentModeParametersAsync();
        }
        catch (Exception ex)
        {
            OperationStatusText = $"读取运行模式失败: {ex.Message}";
            AppData.AppLogViewModel.Log(Models.AppLogLevel.Error, Models.AppLogCategory.EtherCAT, "读取运行模式失败", ex.Message);
        }
    }

    #endregion

    #region H02 基本控制

    [ObservableProperty]
    private int _ctrlMode; // H02.00 0~4

    [ObservableProperty]
    private bool _reverseDirection; // H02.02 0/1

    [ObservableProperty]
    private int _offStopMode; // H02.05 0~1

    [ObservableProperty]
    private int _faultStopMode; // H02.06 0~3

    [ObservableProperty]
    private int _quickStopMode; // H02.07 0~3

    [ObservableProperty]
    private int _powerLossMode; // H02.08 0~2

    [ObservableProperty]
    private string _h02Status = string.Empty;

    [RelayCommand]
    private void OnReadH02()
    {
        if (!IsConnected || Axis is null) { H02Status = "未连接设备"; return; }
        var errs = new List<string>();
        if (!HRegisterIO.ReadHReg(Master, Axis, "H02.00", v => CtrlMode = v)) errs.Add("H02.00");
        if (!HRegisterIO.ReadHReg(Master, Axis, "H02.02", v => ReverseDirection = v != 0)) errs.Add("H02.02");
        if (!HRegisterIO.ReadHReg(Master, Axis, "H02.05", v => OffStopMode = v)) errs.Add("H02.05");
        if (!HRegisterIO.ReadHReg(Master, Axis, "H02.06", v => FaultStopMode = v)) errs.Add("H02.06");
        if (!HRegisterIO.ReadHReg(Master, Axis, "H02.07", v => QuickStopMode = v)) errs.Add("H02.07");
        if (!HRegisterIO.ReadHReg(Master, Axis, "H02.08", v => PowerLossMode = v)) errs.Add("H02.08");
        H02Status = errs.Count == 0 ? "读取完成" : $"部分失败: {string.Join(';', errs)}";
    }

    [RelayCommand]
    private void OnWriteH02()
    {
        if (!IsConnected || Axis is null) { H02Status = "未连接设备"; return; }
        var errs = new List<string>();
        HRegisterIO.SafeWriteHReg(Master, Axis, "H02.00", (ushort)CtrlMode, errs, "H02.00");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H02.02", (ushort)(ReverseDirection ? 1 : 0), errs, "H02.02");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H02.05", (ushort)OffStopMode, errs, "H02.05");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H02.06", (ushort)FaultStopMode, errs, "H02.06");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H02.07", (ushort)QuickStopMode, errs, "H02.07");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H02.08", (ushort)PowerLossMode, errs, "H02.08");
        H02Status = errs.Count == 0 ? "写入完成" : $"部分失败: {string.Join(';', errs)}";
    }

    #endregion
}