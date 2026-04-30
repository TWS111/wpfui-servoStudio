// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Windows.Threading;
using Core.Net.EtherCAT;
using Wpf.Ui.servoStudio.Core;
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
            return;

        _cyclicSendTimer.Stop();
        _cyclicSendTimer.Tick -= CyclicSendTimerTick;
        _cyclicSendTimer.Tick += CyclicSendTimerTick;

        _cyclicSendCycleCount = 0;
        CyclicSendCycleCountDisplay = 0;
        CyclicSendLastError = string.Empty;

        int intervalMs = Math.Max(1, (int)CyclicSendIntervalMs);
        _cyclicSendTimer.Interval = TimeSpan.FromMilliseconds(intervalMs);
        _cyclicSendTimer.Start();
        IsCyclicSendRunning = true;
        OperationStatusText = $"周期发送已启动 — 间隔 {intervalMs} ms";
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

    #region 轮廓位置模式 (PP) — 0x607A / 0x6081 / 0x6083 / 0x6084

    [ObservableProperty] private double? _ppTargetPosition = 0;
    [ObservableProperty] private double? _ppProfileVelocity = 0;
    [ObservableProperty] private double? _ppProfileAcceleration = 0;
    [ObservableProperty] private double? _ppProfileDeceleration = 0;
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

    #region 周期同步位置 (CSP) — 0x607A / 0x60B0 / 0x60B1 / 0x60B2 / 0x60C2

    [ObservableProperty] private double? _cspTargetPosition = 0;
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
                errors.Add(name);
        }
        catch (Exception ex)
        {
            errors.Add($"{name}(异常:{ex.Message})");
        }
    }

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
    }

    private void WriteVlParameters(List<string> errors)
    {
        if (!TryWriteSdo<int>(Cia402OdIndex.TargetVelocity, 0, (int)(VlTargetVelocity ?? 0)))
            errors.Add("目标速度 (0x60FF)");
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
    }

    private void WriteTqParameters(List<string> errors)
    {
        if (!TryWriteSdo<short>(Cia402OdIndex.TargetTorque, 0, (short)(TqTargetTorque ?? 0)))
            errors.Add("目标转矩 (0x6071)");
        if (!TryWriteSdo<ushort>(Cia402OdIndex.MaxTorque, 0, (ushort)(TqMaxTorque ?? 0)))
            errors.Add("最大转矩 (0x6072)");
        if (!TryWriteSdo<uint>(Cia402OdIndex.TorqueSlope, 0, (uint)(TqTorqueSlope ?? 0)))
            errors.Add("转矩斜坡 (0x6087)");
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
    }

    private void WriteIpParameters(List<string> errors)
    {
        if (!TryWriteSdo<uint>(Cia402OdIndex.InterpolationTimePeriod, 1, (uint)(IpInterpolationTimePeriod ?? 0)))
            errors.Add("插补周期 (0x60C2)");
    }

    private void WriteCspParameters(List<string> errors)
    {
        SafeWriteSdo<int>(Cia402OdIndex.TargetPosition, 0, (int)(CspTargetPosition ?? 0), errors, "目标位置 (0x607A)");
        SafeWriteSdo<int>(Cia402OdIndex.PositionOffset, 0, (int)(CspPositionOffset ?? 0), errors, "位置前馈 (0x60B0)");
        SafeWriteSdo<int>(Cia402OdIndex.VelocityOffset, 0, (int)(CspVelocityOffset ?? 0), errors, "速度前馈 (0x60B1)");
        SafeWriteSdo<short>(Cia402OdIndex.TorqueOffset, 0, (short)(CspTorqueOffset ?? 0), errors, "转矩前馈 (0x60B2)");
        SafeWriteSdo<uint>(Cia402OdIndex.InterpolationTimePeriod, 1, (uint)(CspInterpolationTimePeriod ?? 0), errors, "插补周期 (0x60C2)");
    }

    private void WriteCsvParameters(List<string> errors)
    {
        SafeWriteSdo<int>(Cia402OdIndex.TargetVelocity, 0, (int)(CsvTargetVelocity ?? 0), errors, "目标速度 (0x60FF)");
        SafeWriteSdo<int>(Cia402OdIndex.VelocityOffset, 0, (int)(CsvVelocityOffset ?? 0), errors, "速度前馈 (0x60B1)");
        SafeWriteSdo<short>(Cia402OdIndex.TorqueOffset, 0, (short)(CsvTorqueOffset ?? 0), errors, "转矩前馈 (0x60B2)");
        SafeWriteSdo<uint>(Cia402OdIndex.InterpolationTimePeriod, 1, (uint)(CsvInterpolationTimePeriod ?? 0), errors, "插补周期 (0x60C2)");
    }

    private void WriteCstParameters(List<string> errors)
    {
        SafeWriteSdo<short>(Cia402OdIndex.TargetTorque, 0, (short)(CstTargetTorque ?? 0), errors, "目标转矩 (0x6071)");
        SafeWriteSdo<short>(Cia402OdIndex.TorqueOffset, 0, (short)(CstTorqueOffset ?? 0), errors, "转矩前馈 (0x60B2)");
        SafeWriteSdo<uint>(Cia402OdIndex.InterpolationTimePeriod, 1, (uint)(CstInterpolationTimePeriod ?? 0), errors, "插补周期 (0x60C2)");
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
    }

    private void ReadVlParameters()
    {
        if (TryReadSdo<int>(Cia402OdIndex.TargetVelocity, 0, out var vel))
            VlTargetVelocity = vel;
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
    }

    private void ReadTqParameters()
    {
        if (TryReadSdo<short>(Cia402OdIndex.TargetTorque, 0, out var tq))
            TqTargetTorque = tq;
        if (TryReadSdo<ushort>(Cia402OdIndex.MaxTorque, 0, out var max))
            TqMaxTorque = max;
        if (TryReadSdo<uint>(Cia402OdIndex.TorqueSlope, 0, out var slope))
            TqTorqueSlope = slope;
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
    }

    private void SetHmMethodIndex(int idx)
    {
        HmMethodIndex = idx;
    }

    private void ReadIpParameters()
    {
        if (TryReadSdo<uint>(Cia402OdIndex.InterpolationTimePeriod, 1, out var period))
            IpInterpolationTimePeriod = period;
    }

    private void ReadCspParameters()
    {
        if (TryReadSdo<int>(Cia402OdIndex.TargetPosition, 0, out var pos))
            CspTargetPosition = pos;
        if (TryReadSdo<int>(Cia402OdIndex.PositionOffset, 0, out var posOff))
            CspPositionOffset = posOff;
        if (TryReadSdo<int>(Cia402OdIndex.VelocityOffset, 0, out var velOff))
            CspVelocityOffset = velOff;
        if (TryReadSdo<short>(Cia402OdIndex.TorqueOffset, 0, out var tqOff))
            CspTorqueOffset = tqOff;
        if (TryReadSdo<uint>(Cia402OdIndex.InterpolationTimePeriod, 1, out var period))
            CspInterpolationTimePeriod = period;
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
    }

    private void ReadCstParameters()
    {
        if (TryReadSdo<short>(Cia402OdIndex.TargetTorque, 0, out var tq))
            CstTargetTorque = tq;
        if (TryReadSdo<short>(Cia402OdIndex.TorqueOffset, 0, out var tqOff))
            CstTorqueOffset = tqOff;
        if (TryReadSdo<uint>(Cia402OdIndex.InterpolationTimePeriod, 1, out var period))
            CstInterpolationTimePeriod = period;
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
}