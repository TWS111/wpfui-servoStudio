// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.Services;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels.Tuning;

/// <summary>
/// 在线电机参数辨识 ViewModel。
/// 参考主流伺服调试软件（汇川 InoDriverShop / 台达 ASDA-Soft / 松下 PANATERM 等）的
/// "电机自学习/参数辨识"功能：用户在断开负载（或确认安全）的前提下，由驱动器自动注入测试电流，
/// 估计 R / Ld / Lq / Ke / J / B 等关键参数，并可一键写回设备生效。
///
/// 本 ViewModel 当前不直接调用底层算法（伺服固件侧执行），UI 层负责：
/// 1) 参数与安全确认；2) 启动/中止/进度轮询；3) 结果展示、历史归档、CSV 导出；4) 一键写回。
/// 设备端寄存器接入预留挂接点（详见 SubmitToDriveAsync / ReadResultFromDrive）。
/// </summary>
public partial class MotorIdentificationViewModel(DeviceAddViewModel deviceAddViewModel) : ViewModel
{
    private bool _isInitialized;
    private DispatcherTimer? _pollTimer;
    private CancellationTokenSource? _runCts;
    private DateTime _runStartedAt;

    #region 设备连接

    private IServoAxis? Axis => deviceAddViewModel.ActiveAxis;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionInfo = "设备未连接";

    #endregion

    #region 用户设置（与 UserSettings 映射）

    /// <summary>
    /// 总开关：关闭时整页禁用（受 Debug/User 设置项控制）。
    /// </summary>
    [ObservableProperty]
    private bool _isFeatureEnabled;

    /// <summary>0=静止注入 1=旋转扫频 2=综合辨识。</summary>
    [ObservableProperty]
    private int _modeIndex;

    [ObservableProperty]
    private double _injectCurrentPercent = 30.0;

    [ObservableProperty]
    private int _maxTestSpeedRpm = 500;

    [ObservableProperty]
    private int _durationSec = 8;

    [ObservableProperty]
    private bool _autoApplyResult;

    [ObservableProperty]
    private bool _noiseFilterEnabled = true;

    [ObservableProperty]
    private int _historyLimit = 20;

    public IReadOnlyList<string> ModeOptions { get; } = new[]
    {
        "静止辨识 (推荐)",
        "旋转辨识 (需解锁负载)",
        "综合辨识 (静止+旋转)",
    };

    /// <summary>当前所选模式的强类型枚举。</summary>
    public MotorIdentificationMode SelectedMode => (MotorIdentificationMode)ModeIndex;

    #endregion

    #region 运行状态

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StageText))]
    private MotorIdentificationStage _stage = MotorIdentificationStage.Idle;

    public string StageText => Stage switch
    {
        MotorIdentificationStage.Idle => "空闲",
        MotorIdentificationStage.Initializing => "初始化",
        MotorIdentificationStage.StatorResistance => "辨识 Rs",
        MotorIdentificationStage.DAxisInductance => "辨识 Ld",
        MotorIdentificationStage.QAxisInductance => "辨识 Lq",
        MotorIdentificationStage.BackEmf => "辨识 Ke",
        MotorIdentificationStage.Friction => "辨识摩擦",
        MotorIdentificationStage.Inertia => "辨识惯量",
        MotorIdentificationStage.Finalizing => "结果确认",
        MotorIdentificationStage.Completed => "已完成",
        MotorIdentificationStage.Failed => "失败",
        MotorIdentificationStage.Aborted => "已中止",
        _ => Stage.ToString(),
    };

    /// <summary>0~100。</summary>
    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private string _elapsedText = "00:00";

    /// <summary>用户已确认安全条件（电机空载/已断负载/紧停可达）。</summary>
    [ObservableProperty]
    private bool _safetyConfirmed;

    public bool CanStart => IsFeatureEnabled && IsConnected && SafetyConfirmed && !IsRunning;
    public bool CanAbort => IsRunning;

    partial void OnIsFeatureEnabledChanged(bool value) => RaiseCanExecChanged();
    partial void OnIsConnectedChanged(bool value) => RaiseCanExecChanged();
    partial void OnSafetyConfirmedChanged(bool value) => RaiseCanExecChanged();
    partial void OnIsRunningChanged(bool value) => RaiseCanExecChanged();

    private void RaiseCanExecChanged()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanAbort));
        StartCommand.NotifyCanExecuteChanged();
        AbortCommand.NotifyCanExecuteChanged();
    }

    #endregion

    #region 结果与历史

    [ObservableProperty]
    private MotorIdentificationResult? _latestResult;

    public ObservableCollection<MotorIdentificationResult> History { get; } = new();

    public ObservableCollection<MotorIdentificationLogEntry> Logs { get; } = new();

    #endregion

    #region 导航

    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            _isInitialized = true;
            LoadFromUserSettings();
            UserSettingsService.SettingsChanged += OnUserSettingsChanged;
        }

        UpdateConnectionState();
        AppendLog(MotorIdentificationStage.Idle, "进入在线电机参数辨识页面。", "Info");
    }

    public override void OnNavigatedFrom()
    {
        // 用户离开页面时确保停止后台轮询，防止异常状态延续。
        StopPollTimer();
        if (IsRunning)
        {
            // 不强制写零给驱动 (避免误中断中的辨识)，仅取消 UI 端协程。
            _runCts?.Cancel();
        }

        SaveToUserSettings();
    }

    private void OnUserSettingsChanged(object? sender, UserSettings e)
    {
        _ = Application.Current?.Dispatcher.BeginInvoke(LoadFromUserSettings);
    }

    private void LoadFromUserSettings()
    {
        UserSettings s = UserSettingsService.Load();
        IsFeatureEnabled = s.MotorIdent_IsEnabled;
        ModeIndex = Math.Clamp(s.MotorIdent_Mode, 0, 2);
        InjectCurrentPercent = Math.Clamp(s.MotorIdent_InjectCurrentPercent, 5.0, 100.0);
        MaxTestSpeedRpm = Math.Max(0, s.MotorIdent_MaxTestSpeedRpm);
        DurationSec = Math.Max(1, s.MotorIdent_DurationSec);
        AutoApplyResult = s.MotorIdent_AutoApplyResult;
        NoiseFilterEnabled = s.MotorIdent_NoiseFilterEnabled;
        HistoryLimit = Math.Max(1, s.MotorIdent_HistoryLimit);
    }

    private void SaveToUserSettings()
    {
        UserSettings s = UserSettingsService.Load();
        s.MotorIdent_IsEnabled = IsFeatureEnabled;
        s.MotorIdent_Mode = ModeIndex;
        s.MotorIdent_InjectCurrentPercent = InjectCurrentPercent;
        s.MotorIdent_MaxTestSpeedRpm = MaxTestSpeedRpm;
        s.MotorIdent_DurationSec = DurationSec;
        s.MotorIdent_AutoApplyResult = AutoApplyResult;
        s.MotorIdent_NoiseFilterEnabled = NoiseFilterEnabled;
        s.MotorIdent_HistoryLimit = HistoryLimit;
        UserSettingsService.Save(s);
    }

    private void UpdateConnectionState()
    {
        IsConnected = deviceAddViewModel.IsAnyConnected && Axis != null;
        ConnectionInfo = IsConnected
            ? $"已连接：从站 {Axis!.SlaveAddr}"
            : "设备未连接";
    }

    #endregion

    #region 命令

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task OnStart()
    {
        UpdateConnectionState();
        if (!CanStart)
        {
            StatusText = "无法启动：请检查总开关 / 设备连接 / 安全确认。";
            return;
        }

        // 输入合法性校验（基本范围）
        if (InjectCurrentPercent is < 5 or > 100)
        {
            StatusText = "注入电流百分比超出 5~100% 范围。";
            return;
        }

        if (SelectedMode != MotorIdentificationMode.StaticInjection && MaxTestSpeedRpm <= 0)
        {
            StatusText = "旋转辨识需要设置一个正向最大测试转速。";
            return;
        }

        SaveToUserSettings();

        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        CancellationToken ct = _runCts.Token;

        IsRunning = true;
        Progress = 0;
        Stage = MotorIdentificationStage.Initializing;
        StatusText = $"开始 {ModeOptions[ModeIndex]} ...";
        _runStartedAt = DateTime.Now;
        ElapsedText = "00:00";
        Logs.Clear();
        AppendLog(Stage, $"启动辨识：模式={SelectedMode}, I={InjectCurrentPercent:F1}%, "
            + $"nMax={MaxTestSpeedRpm}rpm, T={DurationSec}s, 滤波={NoiseFilterEnabled}", "Info");

        try
        {
            // 1) 下发参数 / 启动指令到驱动器（实际 SDO 写入留作后续接入点）
            await SubmitToDriveAsync(ct).ConfigureAwait(true);

            // 2) 启动 UI 端轮询定时器（200 ms）
            StartPollTimer();

            // 3) 等待整个流程在轮询中走完 / 被取消
            await Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && IsRunning)
                {
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Stage = MotorIdentificationStage.Aborted;
            StatusText = "已中止。";
            AppendLog(Stage, "用户中止辨识。", "Warn");
        }
        catch (Exception ex)
        {
            Stage = MotorIdentificationStage.Failed;
            StatusText = $"辨识失败：{ex.Message}";
            AppendLog(Stage, ex.Message, "Error");
        }
        finally
        {
            StopPollTimer();
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAbort))]
    private void OnAbort()
    {
        if (_runCts is { IsCancellationRequested: false })
        {
            _runCts.Cancel();
            StatusText = "正在中止...";
            AppendLog(Stage, "请求中止辨识。", "Warn");
        }
    }

    [RelayCommand]
    private async Task OnApplyToDrive()
    {
        if (LatestResult == null || !LatestResult.IsValid)
        {
            StatusText = "没有可写入的有效结果。";
            return;
        }

        if (!deviceAddViewModel.IsAnyConnected || Axis == null)
        {
            StatusText = "设备未连接，无法写入。";
            return;
        }

        StatusText = "正在写入电机参数到驱动器…";
        AppendLog(MotorIdentificationStage.Finalizing, "写入辨识结果到电机参数寄存器。", "Info");
        try
        {
            await WriteResultToDriveAsync(LatestResult).ConfigureAwait(true);
            StatusText = "结果已写入驱动器。";
            AppendLog(MotorIdentificationStage.Completed, "写入完成。", "Success");
        }
        catch (Exception ex)
        {
            StatusText = $"写入失败：{ex.Message}";
            AppendLog(MotorIdentificationStage.Failed, ex.Message, "Error");
        }
    }

    [RelayCommand]
    private void OnClearLogs() => Logs.Clear();

    [RelayCommand]
    private void OnClearHistory() => History.Clear();

    [RelayCommand]
    private void OnExportCsv()
    {
        if (History.Count == 0)
        {
            StatusText = "暂无可导出的历史记录。";
            return;
        }

        try
        {
            Microsoft.Win32.SaveFileDialog dlg = new()
            {
                FileName = $"MotorIdent_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                Filter = "CSV 文件|*.csv|所有文件|*.*",
            };
            if (dlg.ShowDialog() != true)
            {
                return;
            }

            List<string> lines = new()
            {
                "Timestamp,Mode,Rs(Ohm),Ld(mH),Lq(mH),Ke(V/krpm),Flux(Wb),"
                    + "J(kg.cm^2),B(N.m.s/rad),Coulomb(N.m),PolePairs,Elapsed(s),Valid,Note",
            };

            foreach (MotorIdentificationResult r in History)
            {
                string[] fields =
                [
                    r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    r.Mode.ToString(),
                    r.StatorResistance.ToString("F4", CultureInfo.InvariantCulture),
                    r.DAxisInductance.ToString("F3", CultureInfo.InvariantCulture),
                    r.QAxisInductance.ToString("F3", CultureInfo.InvariantCulture),
                    r.BackEmfConstant.ToString("F3", CultureInfo.InvariantCulture),
                    r.FluxLinkage.ToString("F5", CultureInfo.InvariantCulture),
                    r.Inertia.ToString("F3", CultureInfo.InvariantCulture),
                    r.ViscousFriction.ToString("F5", CultureInfo.InvariantCulture),
                    r.CoulombFriction.ToString("F4", CultureInfo.InvariantCulture),
                    r.PolePairs.ToString(CultureInfo.InvariantCulture),
                    r.ElapsedSeconds.ToString("F1", CultureInfo.InvariantCulture),
                    r.IsValid.ToString(CultureInfo.InvariantCulture),
                    (r.Note ?? string.Empty).Replace(',', ';'),
                ];

                lines.Add(string.Join(',', fields));
            }

            System.IO.File.WriteAllLines(dlg.FileName, lines, System.Text.Encoding.UTF8);
            StatusText = $"已导出 {History.Count} 条历史记录。";
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
        }
    }

    #endregion

    #region 进度轮询 / 模拟流程

    private void StartPollTimer()
    {
        StopPollTimer();
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();
    }

    private void StopPollTimer()
    {
        if (_pollTimer != null)
        {
            _pollTimer.Stop();
            _pollTimer.Tick -= OnPollTick;
            _pollTimer = null;
        }
    }

    private void OnPollTick(object? sender, EventArgs e)
    {
        TimeSpan elapsed = DateTime.Now - _runStartedAt;
        ElapsedText = elapsed.ToString(@"mm\:ss");

        // 后续：替换为从驱动器读取实际进度/阶段。当前以时间分段模拟。
        int total = Math.Max(1, DurationSec);
        double p = Math.Min(1.0, elapsed.TotalSeconds / total);
        Progress = p * 100.0;

        MotorIdentificationStage newStage = MapStageByProgress(p, SelectedMode);
        if (newStage != Stage)
        {
            Stage = newStage;
            AppendLog(Stage, "进入阶段：" + StageText, "Info");
        }

        if (p >= 1.0)
        {
            FinalizeRun();
        }
    }

    private static MotorIdentificationStage MapStageByProgress(double p, MotorIdentificationMode mode)
    {
        // 简化的阶段切换：覆盖主流厂家产品的 UI 进度提示风格。
        return mode switch
        {
            MotorIdentificationMode.StaticInjection => p switch
            {
                < 0.10 => MotorIdentificationStage.Initializing,
                < 0.45 => MotorIdentificationStage.StatorResistance,
                < 0.75 => MotorIdentificationStage.DAxisInductance,
                < 0.95 => MotorIdentificationStage.QAxisInductance,
                _ => MotorIdentificationStage.Finalizing,
            },
            MotorIdentificationMode.RotationSweep => p switch
            {
                < 0.10 => MotorIdentificationStage.Initializing,
                < 0.55 => MotorIdentificationStage.BackEmf,
                < 0.85 => MotorIdentificationStage.Friction,
                < 0.95 => MotorIdentificationStage.Inertia,
                _ => MotorIdentificationStage.Finalizing,
            },
            _ => p switch
            {
                < 0.05 => MotorIdentificationStage.Initializing,
                < 0.25 => MotorIdentificationStage.StatorResistance,
                < 0.45 => MotorIdentificationStage.DAxisInductance,
                < 0.60 => MotorIdentificationStage.QAxisInductance,
                < 0.75 => MotorIdentificationStage.BackEmf,
                < 0.88 => MotorIdentificationStage.Friction,
                < 0.96 => MotorIdentificationStage.Inertia,
                _ => MotorIdentificationStage.Finalizing,
            },
        };
    }

    private void FinalizeRun()
    {
        StopPollTimer();
        Progress = 100;
        Stage = MotorIdentificationStage.Completed;

        MotorIdentificationResult result = ReadResultFromDrive();
        result.Mode = SelectedMode;
        result.ElapsedSeconds = (DateTime.Now - _runStartedAt).TotalSeconds;
        string note;
        result.IsValid = ValidateResult(result, out note);
        result.Note = note;

        LatestResult = result;
        History.Insert(0, result);
        while (History.Count > Math.Max(1, HistoryLimit))
        {
            History.RemoveAt(History.Count - 1);
        }

        StatusText = result.IsValid ? "辨识完成。" : "辨识完成（结果存疑，请检查）。";
        AppendLog(Stage, StatusText, result.IsValid ? "Success" : "Warn");

        if (result.IsValid && AutoApplyResult)
        {
            _ = OnApplyToDrive();
        }

        // 让外层 await 退出
        IsRunning = false;
    }

    #endregion

    #region 设备 I/O 接入点（占位实现，等待后续对接驱动协议）

    /// <summary>
    /// 把当前界面参数与启动命令下发给驱动器。
    /// TODO: 后续接入实际 SDO/PDO（例如 0x6098 起的 OD 项或厂家自定义寄存器）。
    /// </summary>
    private Task SubmitToDriveAsync(CancellationToken ct)
    {
        AppendLog(MotorIdentificationStage.Initializing,
            "[占位] 将参数下发到驱动器并发出启动命令。", "Info");
        return Task.CompletedTask;
    }

    /// <summary>从驱动器读取最终辨识结果。当前用占位估计值。</summary>
    private MotorIdentificationResult ReadResultFromDrive()
    {
        // 占位：基于模式生成合理范围的"演示值"。真实实现应从 OD 读取。
        Random rnd = new();
        return new MotorIdentificationResult
        {
            StatorResistance = Math.Round(0.5 + rnd.NextDouble() * 1.5, 4),
            DAxisInductance = Math.Round(2.0 + rnd.NextDouble() * 5.0, 3),
            QAxisInductance = Math.Round(2.5 + rnd.NextDouble() * 5.0, 3),
            BackEmfConstant = Math.Round(20.0 + rnd.NextDouble() * 30.0, 2),
            FluxLinkage = Math.Round(0.05 + rnd.NextDouble() * 0.15, 5),
            Inertia = Math.Round(0.3 + rnd.NextDouble() * 1.0, 3),
            ViscousFriction = Math.Round(rnd.NextDouble() * 1e-3, 5),
            CoulombFriction = Math.Round(rnd.NextDouble() * 0.05, 4),
            PolePairs = 5,
        };
    }

    /// <summary>把结果写回驱动器（电机参数寄存器）。</summary>
    private Task WriteResultToDriveAsync(MotorIdentificationResult result)
    {
        // TODO: 调用 ActiveServoMaster / Axis 上的 SDO 写入接口写入 Rs/Ld/Lq/Ke/J 等。
        // 留作后续接入点：保持 UI 完整流程可用。
        return Task.CompletedTask;
    }

    private static bool ValidateResult(MotorIdentificationResult r, out string note)
    {
        if (r.StatorResistance is <= 0 or > 100)
        {
            note = "Rs 超出合理范围 (0,100] Ω。";
            return false;
        }

        if (r.DAxisInductance < 0 || r.QAxisInductance < 0)
        {
            note = "Ld/Lq 不能为负。";
            return false;
        }

        if (r.PolePairs is <= 0 or > 50)
        {
            note = "极对数估算异常。";
            return false;
        }

        note = "OK";
        return true;
    }

    #endregion

    #region 日志辅助

    private void AppendLog(MotorIdentificationStage stage, string message, string severity)
    {
        MotorIdentificationLogEntry entry = new()
        {
            Stage = stage,
            Message = message,
            Severity = severity,
        };

        if (Application.Current?.Dispatcher.CheckAccess() == false)
        {
            _ = Application.Current.Dispatcher.BeginInvoke(() => Logs.Insert(0, entry));
        }
        else
        {
            Logs.Insert(0, entry);
        }

        // 控制条目上限，避免长跑时无限增长
        while (Logs.Count > 500)
        {
            Logs.RemoveAt(Logs.Count - 1);
        }
    }

    #endregion
}
