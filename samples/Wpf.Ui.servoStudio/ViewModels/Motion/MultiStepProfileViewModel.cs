// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels.Motion;

/// <summary>
/// 多段速 / 多段位置曲线页 ViewModel —— 覆盖 H10 寄存器组。
/// </summary>
public partial class MultiStepProfileViewModel(DeviceAddViewModel deviceAddViewModel) : ViewModel
{
    private IServoMaster? Master => deviceAddViewModel.ActiveServoMaster;
    private IServoAxis? Axis => deviceAddViewModel.ActiveAxis;

    [ObservableProperty] private int _multiStepMode; // H10.00 0~3

    // 8 段速度 (signed, rpm) H10.01 ~ H10.08
    [ObservableProperty] private int _speed1;
    [ObservableProperty] private int _speed2;
    [ObservableProperty] private int _speed3;
    [ObservableProperty] private int _speed4;
    [ObservableProperty] private int _speed5;
    [ObservableProperty] private int _speed6;
    [ObservableProperty] private int _speed7;
    [ObservableProperty] private int _speed8;

    // 4 段加减速 (ms) H10.10 ~ H10.17
    [ObservableProperty] private int _accTime1 = 100;
    [ObservableProperty] private int _decTime1 = 100;
    [ObservableProperty] private int _accTime2 = 100;
    [ObservableProperty] private int _decTime2 = 100;
    [ObservableProperty] private int _accTime3 = 100;
    [ObservableProperty] private int _decTime3 = 100;
    [ObservableProperty] private int _accTime4 = 100;
    [ObservableProperty] private int _decTime4 = 100;

    // 4 段位置：圈数 (signed) + 脉冲 (unsigned) H10.20 ~ H10.27
    [ObservableProperty] private int _pos1Revs;
    [ObservableProperty] private int _pos1Pulses;
    [ObservableProperty] private int _pos2Revs;
    [ObservableProperty] private int _pos2Pulses;
    [ObservableProperty] private int _pos3Revs;
    [ObservableProperty] private int _pos3Pulses;
    [ObservableProperty] private int _pos4Revs;
    [ObservableProperty] private int _pos4Pulses;

    [ObservableProperty] private string _statusText = string.Empty;

    [RelayCommand]
    private void OnReadAll()
    {
        if (Master is null || Axis is null) { StatusText = "未连接设备"; return; }
        var errs = new List<string>();
        bool RU(string h, Action<ushort> set) { if (HRegisterIO.ReadHReg(Master, Axis, h, set)) return true; errs.Add(h); return false; }
        bool RS(string h, Action<short> set) { if (HRegisterIO.ReadHRegSigned(Master, Axis, h, set)) return true; errs.Add(h); return false; }

        RU("H10.00", v => MultiStepMode = v);
        RS("H10.01", v => Speed1 = v); RS("H10.02", v => Speed2 = v); RS("H10.03", v => Speed3 = v); RS("H10.04", v => Speed4 = v);
        RS("H10.05", v => Speed5 = v); RS("H10.06", v => Speed6 = v); RS("H10.07", v => Speed7 = v); RS("H10.08", v => Speed8 = v);
        RU("H10.10", v => AccTime1 = v); RU("H10.11", v => DecTime1 = v);
        RU("H10.12", v => AccTime2 = v); RU("H10.13", v => DecTime2 = v);
        RU("H10.14", v => AccTime3 = v); RU("H10.15", v => DecTime3 = v);
        RU("H10.16", v => AccTime4 = v); RU("H10.17", v => DecTime4 = v);
        RS("H10.20", v => Pos1Revs = v); RU("H10.21", v => Pos1Pulses = v);
        RS("H10.22", v => Pos2Revs = v); RU("H10.23", v => Pos2Pulses = v);
        RS("H10.24", v => Pos3Revs = v); RU("H10.25", v => Pos3Pulses = v);
        RS("H10.26", v => Pos4Revs = v); RU("H10.27", v => Pos4Pulses = v);

        StatusText = errs.Count == 0 ? "读取完成" : $"部分失败: {string.Join(';', errs)}";
    }

    [RelayCommand]
    private void OnWriteAll()
    {
        if (Master is null || Axis is null) { StatusText = "未连接设备"; return; }
        var errs = new List<string>();

        HRegisterIO.SafeWriteHReg(Master, Axis, "H10.00", (ushort)MultiStepMode, errs, "H10.00");
        HRegisterIO.SafeWriteHRegSigned(Master, Axis, "H10.01", (short)Speed1, errs, "H10.01");
        HRegisterIO.SafeWriteHRegSigned(Master, Axis, "H10.02", (short)Speed2, errs, "H10.02");
        HRegisterIO.SafeWriteHRegSigned(Master, Axis, "H10.03", (short)Speed3, errs, "H10.03");
        HRegisterIO.SafeWriteHRegSigned(Master, Axis, "H10.04", (short)Speed4, errs, "H10.04");
        HRegisterIO.SafeWriteHRegSigned(Master, Axis, "H10.05", (short)Speed5, errs, "H10.05");
        HRegisterIO.SafeWriteHRegSigned(Master, Axis, "H10.06", (short)Speed6, errs, "H10.06");
        HRegisterIO.SafeWriteHRegSigned(Master, Axis, "H10.07", (short)Speed7, errs, "H10.07");
        HRegisterIO.SafeWriteHRegSigned(Master, Axis, "H10.08", (short)Speed8, errs, "H10.08");

        HRegisterIO.SafeWriteHReg(Master, Axis, "H10.10", (ushort)AccTime1, errs, "H10.10");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H10.11", (ushort)DecTime1, errs, "H10.11");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H10.12", (ushort)AccTime2, errs, "H10.12");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H10.13", (ushort)DecTime2, errs, "H10.13");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H10.14", (ushort)AccTime3, errs, "H10.14");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H10.15", (ushort)DecTime3, errs, "H10.15");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H10.16", (ushort)AccTime4, errs, "H10.16");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H10.17", (ushort)DecTime4, errs, "H10.17");

        HRegisterIO.SafeWriteHRegSigned(Master, Axis, "H10.20", (short)Pos1Revs, errs, "H10.20");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H10.21", (ushort)Pos1Pulses, errs, "H10.21");
        HRegisterIO.SafeWriteHRegSigned(Master, Axis, "H10.22", (short)Pos2Revs, errs, "H10.22");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H10.23", (ushort)Pos2Pulses, errs, "H10.23");
        HRegisterIO.SafeWriteHRegSigned(Master, Axis, "H10.24", (short)Pos3Revs, errs, "H10.24");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H10.25", (ushort)Pos3Pulses, errs, "H10.25");
        HRegisterIO.SafeWriteHRegSigned(Master, Axis, "H10.26", (short)Pos4Revs, errs, "H10.26");
        HRegisterIO.SafeWriteHReg(Master, Axis, "H10.27", (ushort)Pos4Pulses, errs, "H10.27");

        StatusText = errs.Count == 0 ? "写入完成" : $"部分失败: {string.Join(';', errs)}";
    }
}
