// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels.Tuning;

/// <summary>
/// 高级整定页 ViewModel —— 覆盖 H09 自调谐 / 陷波滤波器 / 低频抑振寄存器。
/// </summary>
public partial class AdvancedTuningViewModel(DeviceAddViewModel deviceAddViewModel) : ViewModel
{
    private IServoMaster? Master => deviceAddViewModel.ActiveServoMaster;
    private IServoAxis? Axis => deviceAddViewModel.ActiveAxis;

    // ===== H09.00 ~ H09.03 自调谐基本 =====
    [ObservableProperty] private int _autoTuningMode;     // H09.00 0~3
    [ObservableProperty] private int _stiffnessLevel = 12; // H09.01 0~31
    [ObservableProperty] private int _inertiaRatio = 100;  // H09.02 0~65535 (%)
    [ObservableProperty] private bool _inertiaIdentEnabled; // H09.03 0/1
    [ObservableProperty] private int _vibFreqDetect;       // H09.10 RO Hz

    // ===== H09.12 ~ H09.15 陷波滤波器1 =====
    [ObservableProperty] private int _notchFreq1;
    [ObservableProperty] private int _notchDepth1;
    [ObservableProperty] private int _notchBW1 = 2;
    [ObservableProperty] private bool _notchEnabled1;

    // ===== H09.16 ~ H09.19 陷波滤波器2 =====
    [ObservableProperty] private int _notchFreq2;
    [ObservableProperty] private int _notchDepth2;
    [ObservableProperty] private int _notchBW2 = 2;
    [ObservableProperty] private bool _notchEnabled2;

    // ===== H09.30 ~ H09.33 低频抑振 =====
    [ObservableProperty] private int _lowVibFreq1;        // 0.1Hz
    [ObservableProperty] private int _lowVibAmp1;
    [ObservableProperty] private int _lowVibFreq2;
    [ObservableProperty] private int _lowVibAmp2;

    [ObservableProperty] private string _statusText = string.Empty;

    [RelayCommand]
    private void OnReadAll()
    {
        if (Master is null || Axis is null) { StatusText = "未连接设备"; return; }
        var errs = new List<string>();
        bool R(string h, Action<ushort> set) { if (HRegisterIO.ReadHReg(Master, Axis, h, set)) return true; errs.Add(h); return false; }

        R("H09.00", v => AutoTuningMode = v);
        R("H09.01", v => StiffnessLevel = v);
        R("H09.02", v => InertiaRatio = v);
        R("H09.03", v => InertiaIdentEnabled = v != 0);
        R("H09.10", v => VibFreqDetect = v);
        R("H09.12", v => NotchFreq1 = v);
        R("H09.13", v => NotchDepth1 = v);
        R("H09.14", v => NotchBW1 = v);
        R("H09.15", v => NotchEnabled1 = v != 0);
        R("H09.16", v => NotchFreq2 = v);
        R("H09.17", v => NotchDepth2 = v);
        R("H09.18", v => NotchBW2 = v);
        R("H09.19", v => NotchEnabled2 = v != 0);
        R("H09.30", v => LowVibFreq1 = v);
        R("H09.31", v => LowVibAmp1 = v);
        R("H09.32", v => LowVibFreq2 = v);
        R("H09.33", v => LowVibAmp2 = v);

        StatusText = errs.Count == 0 ? "读取完成" : $"部分失败: {string.Join(';', errs)}";
    }

    [RelayCommand]
    private void OnWriteAll()
    {
        if (Master is null || Axis is null) { StatusText = "未连接设备"; return; }
        var errs = new List<string>();
        HRegisterIO.SafeWriteHReg(Master, Axis, "H09.00", (ushort)AutoTuningMode, errs, "H09.00");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H09.01", (ushort)StiffnessLevel, errs, "H09.01");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H09.02", (ushort)InertiaRatio, errs, "H09.02");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H09.03", (ushort)(InertiaIdentEnabled ? 1 : 0), errs, "H09.03");
        // H09.10 只读，跳过
        HRegisterIO.SafeWriteHReg(Master, Axis, "H09.12", (ushort)NotchFreq1, errs, "H09.12");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H09.13", (ushort)NotchDepth1, errs, "H09.13");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H09.14", (ushort)NotchBW1, errs, "H09.14");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H09.15", (ushort)(NotchEnabled1 ? 1 : 0), errs, "H09.15");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H09.16", (ushort)NotchFreq2, errs, "H09.16");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H09.17", (ushort)NotchDepth2, errs, "H09.17");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H09.18", (ushort)NotchBW2, errs, "H09.18");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H09.19", (ushort)(NotchEnabled2 ? 1 : 0), errs, "H09.19");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H09.30", (ushort)LowVibFreq1, errs, "H09.30");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H09.31", (ushort)LowVibAmp1, errs, "H09.31");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H09.32", (ushort)LowVibFreq2, errs, "H09.32");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H09.33", (ushort)LowVibAmp2, errs, "H09.33");

        StatusText = errs.Count == 0 ? "写入完成" : $"部分失败: {string.Join(';', errs)}";
    }
}
