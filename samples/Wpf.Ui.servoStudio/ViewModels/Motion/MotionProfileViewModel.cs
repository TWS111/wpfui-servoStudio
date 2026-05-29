// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;

namespace Wpf.Ui.servoStudio.ViewModels.Motion;

/// <summary>
/// 运动曲线页 ViewModel — 支持 T型（梯形加速度）与 S 型（sigmoid 加加速度）运动规划参数配置与实时预览。
/// </summary>
public partial class MotionProfileViewModel : ViewModel
{
    // ───────────────────────────────────────────────
    // 曲线类型选择
    // ───────────────────────────────────────────────

    /// <summary>true = S型，false = T型。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrapezoid))]
    [NotifyPropertyChangedFor(nameof(ProfileTypeName))]
    private bool _isSCurve = false;

    /// <summary>T 型曲线选中状态（与 <see cref="IsSCurve"/> 互斥，可双向绑定到 RadioButton）。</summary>
    public bool IsTrapezoid
    {
        get => !IsSCurve;
        set
        {
            if (value && IsSCurve)
                IsSCurve = false;
            else if (!value && !IsSCurve)
                IsSCurve = true;
        }
    }

    public string ProfileTypeName => IsSCurve ? "S 型曲线（七段式）" : "T 型曲线（梯形加速）";

    // ───────────────────────────────────────────────
    // 公共参数
    // ───────────────────────────────────────────────

    /// <summary>目标位移（用户单位）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPoints))]
    private double _distance = 10000;

    /// <summary>最大速度（用户单位/s）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPoints))]
    private double _maxVelocity = 5000;

    /// <summary>起始速度（用户单位/s）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPoints))]
    private double _startVelocity = 0;

    /// <summary>末端速度（用户单位/s）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPoints))]
    private double _endVelocity = 0;

    // ───────────────────────────────────────────────
    // T 型参数
    // ───────────────────────────────────────────────

    /// <summary>T 型加速度（用户单位/s²）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPoints))]
    private double _acceleration = 10000;

    /// <summary>T 型减速度（用户单位/s²）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPoints))]
    private double _deceleration = 10000;

    // ───────────────────────────────────────────────
    // S 型参数
    // ───────────────────────────────────────────────

    /// <summary>S 型最大加速度（用户单位/s²）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPoints))]
    private double _sMaxAccel = 10000;

    /// <summary>S 型加加速度 Jerk（用户单位/s³）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPoints))]
    private double _jerk = 50000;

    // ───────────────────────────────────────────────
    // 图形预览数据（速度轮廓采样点）
    // ───────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<ProfilePoint> _previewPoints = [];

    // ───────────────────────────────────────────────
    // 结果统计
    // ───────────────────────────────────────────────

    [ObservableProperty]
    private double _totalTime;

    [ObservableProperty]
    private double _peakVelocity;

    [ObservableProperty]
    private double _peakAcceleration;

    // ───────────────────────────────────────────────
    // 状态
    // ───────────────────────────────────────────────

    [ObservableProperty]
    private string _statusMessage = "调整参数后点击「预览曲线」";

    // ───────────────────────────────────────────────
    // 命令
    // ───────────────────────────────────────────────

    [RelayCommand]
    private void Preview()
    {
        if (IsSCurve)
            ComputeSCurve();
        else
            ComputeTCurve();
    }

    partial void OnIsSCurveChanged(bool value)
    {
        Preview();
    }

    partial void OnDistanceChanged(double value) => Preview();
    partial void OnMaxVelocityChanged(double value) => Preview();
    partial void OnStartVelocityChanged(double value) => Preview();
    partial void OnEndVelocityChanged(double value) => Preview();
    partial void OnAccelerationChanged(double value) => Preview();
    partial void OnDecelerationChanged(double value) => Preview();
    partial void OnSMaxAccelChanged(double value) => Preview();
    partial void OnJerkChanged(double value) => Preview();

    // ───────────────────────────────────────────────
    // T 型曲线计算（梯形速度轮廓）
    // ───────────────────────────────────────────────

    private void ComputeTCurve()
    {
        double v0 = Math.Max(0, StartVelocity);
        double vt = Math.Max(0, EndVelocity);
        double vm = Math.Max(v0, Math.Max(vt, MaxVelocity));
        double acc = Math.Max(1, Acceleration);
        double dec = Math.Max(1, Deceleration);
        double dist = Math.Max(0, Distance);

        // 加速段时间与位移
        double tAcc = (vm - v0) / acc;
        double dAcc = (v0 + vm) / 2.0 * tAcc;

        // 减速段时间与位移
        double tDec = (vm - vt) / dec;
        double dDec = (vm + vt) / 2.0 * tDec;

        // 匀速段
        double dConst = dist - dAcc - dDec;
        double tConst = 0;

        if (dConst < 0)
        {
            // 三角轮廓（速度达不到 vm）
            // vm' = sqrt((2*acc*dec*dist + acc*vt²+ dec*v0²) / (acc+dec))
            double vmPrime = Math.Sqrt(
                Math.Max(0, (2.0 * acc * dec * dist + dec * v0 * v0 + acc * vt * vt) / (acc + dec)));
            tAcc = (vmPrime - v0) / acc;
            tDec = (vmPrime - vt) / dec;
            vm = vmPrime;
            dConst = 0;
        }
        else
        {
            tConst = dConst / vm;
        }

        double totalT = tAcc + tConst + tDec;
        TotalTime = Math.Round(totalT, 4);
        PeakVelocity = Math.Round(vm, 2);
        PeakAcceleration = Math.Round(acc, 2);

        const int samples = 300;
        var pts = new ObservableCollection<ProfilePoint>();

        for (int i = 0; i <= samples; i++)
        {
            double t = totalT * i / samples;
            double v;
            double a;

            if (t <= tAcc)
            {
                v = v0 + acc * t;
                a = acc;
            }
            else if (t <= tAcc + tConst)
            {
                v = vm;
                a = 0;
            }
            else
            {
                double td = t - tAcc - tConst;
                v = vm - dec * td;
                a = -dec;
            }

            pts.Add(new ProfilePoint(t, Math.Max(0, v), a));
        }

        PreviewPoints = pts;
        StatusMessage = $"T型曲线  总时间 {TotalTime:F3} s  峰值速度 {PeakVelocity:F0} u/s";
    }

    // ───────────────────────────────────────────────
    // S 型曲线计算（七段式）
    // ───────────────────────────────────────────────

    private void ComputeSCurve()
    {
        double v0 = Math.Max(0, StartVelocity);
        double vt = Math.Max(0, EndVelocity);
        double vm = Math.Max(v0, Math.Max(vt, MaxVelocity));
        double amax = Math.Max(1, SMaxAccel);
        double jerk = Math.Max(1, Jerk);

        // 时间计算：加速侧（7段分别为 Jerk+, 匀加速, Jerk-, 匀速, Jerk-, 匀减速, Jerk+）
        // 加速侧
        double tjAcc = amax / jerk;                      // Jerk 段持续时间
        double dvJerk = amax * tjAcc;                    // Jerk 段速度增量
        double dvAcc = vm - v0;

        double taConst = 0;
        if (dvAcc > dvJerk)
            taConst = (dvAcc - dvJerk) / amax;          // 匀加速时间

        double tAccTotal = 2 * tjAcc + taConst;

        // 减速侧
        double tjDec = amax / jerk;
        double dvDec = vm - vt;
        double tdConst = 0;
        if (dvDec > dvJerk)
            tdConst = (dvDec - dvJerk) / amax;

        double tDecTotal = 2 * tjDec + tdConst;

        // 位移计算（加速段 & 减速段）
        double dAccel = TrapezoidArea(v0, vm, tAccTotal);
        double dDecel = TrapezoidArea(vm, vt, tDecTotal);
        double dist = Math.Max(0, Distance);
        double dConst = dist - dAccel - dDecel;
        double tConst = dConst > 0 ? dConst / vm : 0;

        double totalT = tAccTotal + tConst + tDecTotal;
        TotalTime = Math.Round(totalT, 4);
        PeakVelocity = Math.Round(vm, 2);
        PeakAcceleration = Math.Round(amax, 2);

        const int samples = 400;
        var pts = new ObservableCollection<ProfilePoint>();

        for (int i = 0; i <= samples; i++)
        {
            double t = totalT * i / samples;
            (double v, double a) = SampleSCurve(t,
                v0, vm, vt,
                tjAcc, taConst,
                tjDec, tdConst,
                tAccTotal, tConst, tDecTotal,
                amax, jerk);

            pts.Add(new ProfilePoint(t, Math.Max(0, v), a));
        }

        PreviewPoints = pts;
        StatusMessage = $"S型曲线  总时间 {TotalTime:F3} s  峰值速度 {PeakVelocity:F0} u/s  Jerk {jerk:F0} u/s³";
    }

    private static double TrapezoidArea(double v1, double v2, double t) => (v1 + v2) / 2.0 * t;

    private static (double Vel, double Acc) SampleSCurve(
        double t,
        double v0, double vm, double vt,
        double tj1, double ta,
        double tj2, double td,
        double tAcc, double tConst, double tDec,
        double amax, double jerk)
    {
        // Phase boundaries
        double p1 = tj1;
        double p2 = tj1 + ta;
        double p3 = tAcc;          // = 2*tj1 + ta
        double p4 = tAcc + tConst;
        double p5 = p4 + tj2;
        double p6 = p4 + tj2 + td;
        // p7 = p4 + 2*tj2 + td = totalT

        if (t <= p1)
        {
            // Jerk+
            double dt = t;
            double a = jerk * dt;
            double v = v0 + 0.5 * jerk * dt * dt;
            return (v, a);
        }
        else if (t <= p2)
        {
            // Constant acc = amax
            double dt = t - p1;
            double vAtP1 = v0 + 0.5 * jerk * tj1 * tj1;
            double v = vAtP1 + amax * dt;
            return (v, amax);
        }
        else if (t <= p3)
        {
            // Jerk-
            double dt = t - p2;
            double vAtP2 = v0 + 0.5 * jerk * tj1 * tj1 + amax * ta;
            double a = amax - jerk * dt;
            double v = vAtP2 + amax * dt - 0.5 * jerk * dt * dt;
            return (v, a);
        }
        else if (t <= p4)
        {
            // Constant velocity = vm
            return (vm, 0);
        }
        else if (t <= p5)
        {
            // Jerk- (decel start)
            double dt = t - p4;
            double a = -jerk * dt;
            double v = vm - 0.5 * jerk * dt * dt;
            return (v, a);
        }
        else if (t <= p6)
        {
            // Constant decel = -amax
            double dt = t - p5;
            double vAtP5 = vm - 0.5 * jerk * tj2 * tj2;
            double v = vAtP5 - amax * dt;
            return (v, -amax);
        }
        else
        {
            // Jerk+ (decel end)
            double dt = t - p6;
            double vAtP6 = vm - 0.5 * jerk * tj2 * tj2 - amax * td;
            double a = -amax + jerk * dt;
            double v = vAtP6 - amax * dt + 0.5 * jerk * dt * dt;
            return (v, a);
        }
    }

    // ───────────────────────────────────────────────
    // INavigationAware
    // ───────────────────────────────────────────────

    public override void OnNavigatedTo()
    {
        Preview();
    }

    public override void OnNavigatedFrom() { }
}

/// <summary>速度轮廓采样点（时间、速度、加速度）。</summary>
public sealed class ProfilePoint(double time, double velocity, double acceleration)
{
    public double Time { get; } = time;
    public double Velocity { get; } = velocity;
    public double Acceleration { get; } = acceleration;
}
