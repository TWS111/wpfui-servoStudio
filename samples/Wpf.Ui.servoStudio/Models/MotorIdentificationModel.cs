// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Wpf.Ui.servoStudio.Models;

/// <summary>
/// 在线电机参数辨识 - 算法 / 模式枚举。
/// 参考主流伺服厂家（汇川、台达、松下等）的"自学习/参数辨识"功能划分：
/// <list type="bullet">
///   <item><description>StaticInjection: 静止注入辨识，转子不旋转，主要识别 R / Ld / Lq。</description></item>
///   <item><description>RotationSweep: 旋转扫频辨识，需带载/解锁，识别反电势 Ke 与摩擦项。</description></item>
///   <item><description>Comprehensive: 综合辨识 = 静止 + 旋转，得到最完整的电机模型。</description></item>
/// </list>
/// </summary>
public enum MotorIdentificationMode
{
    StaticInjection = 0,
    RotationSweep = 1,
    Comprehensive = 2,
}

/// <summary>
/// 辨识算法当前所处的子阶段，便于 UI 提示进度。
/// </summary>
public enum MotorIdentificationStage
{
    Idle,
    Initializing,
    StatorResistance,
    DAxisInductance,
    QAxisInductance,
    BackEmf,
    Friction,
    Inertia,
    Finalizing,
    Completed,
    Failed,
    Aborted,
}

/// <summary>
/// 单次辨识算法的结果集合。所有字段均带物理单位。
/// </summary>
public class MotorIdentificationResult
{
    /// <summary>辨识完成时间。</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>辨识模式。</summary>
    public MotorIdentificationMode Mode { get; set; }

    /// <summary>定子电阻 Rs (Ω)，每相。</summary>
    public double StatorResistance { get; set; }

    /// <summary>D 轴电感 Ld (mH)。</summary>
    public double DAxisInductance { get; set; }

    /// <summary>Q 轴电感 Lq (mH)。</summary>
    public double QAxisInductance { get; set; }

    /// <summary>反电势常数 Ke (V/krpm)，线-线 RMS。</summary>
    public double BackEmfConstant { get; set; }

    /// <summary>磁链 ψ (Wb)。</summary>
    public double FluxLinkage { get; set; }

    /// <summary>转动惯量 J (kg·cm²)，包含负载折算。</summary>
    public double Inertia { get; set; }

    /// <summary>粘滞摩擦系数 B (N·m·s/rad)。</summary>
    public double ViscousFriction { get; set; }

    /// <summary>库伦摩擦力矩 (N·m)。</summary>
    public double CoulombFriction { get; set; }

    /// <summary>极对数 (估计或读取)。</summary>
    public int PolePairs { get; set; }

    /// <summary>辨识耗时 (秒)。</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>是否通过自检（结果在合理范围）。</summary>
    public bool IsValid { get; set; }

    /// <summary>备注 / 错误信息。</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// 辨识过程产生的日志条目（界面下方实时滚动显示）。
/// </summary>
public class MotorIdentificationLogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public MotorIdentificationStage Stage { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Severity { get; set; } = "Info"; // Info / Warn / Error / Success
    public string Time => Timestamp.ToString("HH:mm:ss.fff");
    public string StageText => Stage.ToString();
}
