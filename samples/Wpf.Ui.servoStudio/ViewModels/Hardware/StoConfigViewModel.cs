// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.Generic;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels.Hardware;

/// <summary>
/// STO 安全配置页 ViewModel — 覆盖双通道 STO (H0F.10 ~ H0F.19)。
/// 寄存器映射：
///   H0F.10  STO 功能使能          (RW, 0/1)
///   H0F.11  STO 故障响应方式       (RW, 0-2)
///   H0F.12  SS1 停机延迟时间       (RW, 0-65535 ms)
///   H0F.13  安全功能状态字         (RO, 16-bit mask)
///   H0F.14  STO_A 通道输入状态     (RO, 0=STO激活, 1=正常)
///   H0F.15  STO_B 通道输入状态     (RO, 0=STO激活, 1=正常)
///   H0F.16  双通道不一致故障延时   (RW, 0-65535 ms, default 100)
///   H0F.17  SS1 制动模式           (RW, 0=自由停车, 1=减速停机)
///   H0F.18  SS1 减速斜率           (RW, 0-65535 rpm/s, default 1000)
///   H0F.19  STO 复位方式           (RW, 0=自动复位, 1=手动)
/// </summary>
public partial class StoConfigViewModel(DeviceAddViewModel deviceAddViewModel) : ViewModel
{
    private IServoMaster? Master => deviceAddViewModel.ActiveServoMaster;
    private IServoAxis? Axis => deviceAddViewModel.ActiveAxis;

    // ── 可写参数 ──────────────────────────────────────────────

    /// <summary>H0F.10 STO 功能全局使能</summary>
    [ObservableProperty]
    private bool _stoEnable;

    /// <summary>H0F.11 STO 故障响应: 0=警告, 1=减速停机, 2=立即报警停机</summary>
    [ObservableProperty]
    private int _stoFaultAction;

    /// <summary>H0F.12 SS1 停机延迟时间 (ms)</summary>
    [ObservableProperty]
    private int _ss1DelayMs = 1000;

    /// <summary>H0F.16 双通道不一致故障延时 (ms)</summary>
    [ObservableProperty]
    private int _stoDiscrepancyMs = 100;

    /// <summary>H0F.17 SS1 制动模式: 0=自由停车, 1=减速停机</summary>
    [ObservableProperty]
    private int _ss1BrakeMode;

    /// <summary>H0F.18 SS1 减速斜率 (rpm/s)</summary>
    [ObservableProperty]
    private int _ss1DecelRate = 1000;

    /// <summary>H0F.19 STO 复位方式: 0=自动复位, 1=手动复位(需外部信号)</summary>
    [ObservableProperty]
    private int _stoResetMode;

    // ── 只读监视 ────────────────────────────────────────────

    /// <summary>H0F.13 安全功能状态字（16 位掩码，自动解析到 Bit 属性）</summary>
    [ObservableProperty]
    private string _safetyStatusWord = "—";

    /// <summary>H0F.13 Bit0: STO_A 是否正常 (true=正常, false=STO激活)</summary>
    [ObservableProperty]
    private bool _stoAOk = true;

    /// <summary>H0F.13 Bit1: STO_B 是否正常</summary>
    [ObservableProperty]
    private bool _stoBOk = true;

    /// <summary>H0F.13 Bit2: STO 功能是否已激活（true=已激活/触发）</summary>
    [ObservableProperty]
    private bool _stoActive;

    /// <summary>H0F.13 Bit3: SS1 序列是否进行中</summary>
    [ObservableProperty]
    private bool _ss1Active;

    /// <summary>H0F.14 STO_A 通道原始输入值（0=STO激活, 1=正常）</summary>
    [ObservableProperty]
    private string _stoAChannelState = "—";

    /// <summary>H0F.15 STO_B 通道原始输入值</summary>
    [ObservableProperty]
    private string _stoBChannelState = "—";

    // ── 状态反馈 ────────────────────────────────────────────

    [ObservableProperty]
    private string _statusText = string.Empty;

    // ── 命令 ────────────────────────────────────────────────

    [RelayCommand]
    private void OnReadAll()
    {
        var master = Master;
        var axis = Axis;
        if (master is null || axis is null) { StatusText = "未连接设备"; return; }

        var errs = new List<string>();

        // 可写参数
        if (!HRegisterIO.ReadHReg(master, axis, "H0F.10", v => StoEnable = v != 0)) errs.Add("H0F.10");
        if (!HRegisterIO.ReadHReg(master, axis, "H0F.11", v => StoFaultAction = v)) errs.Add("H0F.11");
        if (!HRegisterIO.ReadHReg(master, axis, "H0F.12", v => Ss1DelayMs = v)) errs.Add("H0F.12");
        if (!HRegisterIO.ReadHReg(master, axis, "H0F.16", v => StoDiscrepancyMs = v)) errs.Add("H0F.16");
        if (!HRegisterIO.ReadHReg(master, axis, "H0F.17", v => Ss1BrakeMode = v)) errs.Add("H0F.17");
        if (!HRegisterIO.ReadHReg(master, axis, "H0F.18", v => Ss1DecelRate = v)) errs.Add("H0F.18");
        if (!HRegisterIO.ReadHReg(master, axis, "H0F.19", v => StoResetMode = v)) errs.Add("H0F.19");

        // 只读监视
        if (!HRegisterIO.ReadHReg(master, axis, "H0F.13", v =>
        {
            SafetyStatusWord = $"0x{v:X4}";
            StoAOk    = (v & 0x0001) != 0;
            StoBOk    = (v & 0x0002) != 0;
            StoActive = (v & 0x0004) != 0;
            Ss1Active = (v & 0x0008) != 0;
        })) errs.Add("H0F.13");

        if (!HRegisterIO.ReadHReg(master, axis, "H0F.14", v => StoAChannelState = v == 1 ? "正常 (1)" : "STO激活 (0)")) errs.Add("H0F.14");
        if (!HRegisterIO.ReadHReg(master, axis, "H0F.15", v => StoBChannelState = v == 1 ? "正常 (1)" : "STO激活 (0)")) errs.Add("H0F.15");

        StatusText = errs.Count == 0 ? "读取完成" : $"部分失败: {string.Join(';', errs)}";
    }

    [RelayCommand]
    private void OnWriteAll()
    {
        var master = Master;
        var axis = Axis;
        if (master is null || axis is null) { StatusText = "未连接设备"; return; }

        var errs = new List<string>();
        HRegisterIO.SafeWriteHReg(master, axis, "H0F.10", (ushort)(StoEnable ? 1 : 0), errs, "H0F.10");
        HRegisterIO.SafeWriteHReg(master, axis, "H0F.11", (ushort)StoFaultAction,        errs, "H0F.11");
        HRegisterIO.SafeWriteHReg(master, axis, "H0F.12", (ushort)Ss1DelayMs,            errs, "H0F.12");
        // H0F.13/14/15 只读，跳过
        HRegisterIO.SafeWriteHReg(master, axis, "H0F.16", (ushort)StoDiscrepancyMs,     errs, "H0F.16");
        HRegisterIO.SafeWriteHReg(master, axis, "H0F.17", (ushort)Ss1BrakeMode,         errs, "H0F.17");
        HRegisterIO.SafeWriteHReg(master, axis, "H0F.18", (ushort)Ss1DecelRate,         errs, "H0F.18");
        HRegisterIO.SafeWriteHReg(master, axis, "H0F.19", (ushort)StoResetMode,         errs, "H0F.19");
        StatusText = errs.Count == 0 ? "写入完成" : $"部分失败: {string.Join(';', errs)}";
    }
}
