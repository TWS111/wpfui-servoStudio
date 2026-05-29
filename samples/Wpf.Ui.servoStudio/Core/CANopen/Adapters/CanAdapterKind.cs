// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Core.CANopen.Adapters;

/// <summary>
/// 工程支持的 CAN 适配器类别。<br/>
/// 不同类别对应不同厂家 SDK / 原生 DLL，运行时若 DLL 不存在，对应类别会被
/// <see cref="CanAdapterFactory"/> 标记为不可用。
/// </summary>
public enum CanAdapterKind
{
    /// <summary>不指定（由工厂自动选择第一个识别到的设备）。</summary>
    Auto = 0,

    /// <summary>LAWICEL slcan 串口协议（CANable / CHEAP USB-CAN-A 等）。</summary>
    Slcan,

    /// <summary>PEAK PCAN-USB / PCAN-PCI（PCANBasic.dll）。</summary>
    PcanBasic,

    /// <summary>
    /// 周立功 / 创芯 / 致远 等厂家共用的 ControlCAN.dll API（USBCAN-I / USBCAN-II / CANalyst-II /
    /// "CAN II" 兼容卡等）。
    /// </summary>
    ControlCan,

    /// <summary>周立功新版统一 SDK：zlgcan.dll（USBCAN-E-U / USBCANFD-MINI / PCIE-9221 等）。</summary>
    Zlgcan,

    /// <summary>广成 / 德州 Toomoss USB2XXX 系列：usb_device.dll。</summary>
    Toomoss,

    /// <summary>纯软件回环（用于离线测试）。</summary>
    Virtual,
}

/// <summary>
/// 一个已被工厂枚举到的 CAN 适配器条目。<br/>
/// 用于 UI 下拉显示与具体打开。
/// </summary>
public sealed record CanAdapterDescriptor(
    CanAdapterKind Kind,
    string DisplayName,
    string Identifier,
    bool IsAvailable,
    string? Note = null,
    int ChannelCount = 1,
    int DeviceCount = 1)
{
    public override string ToString() =>
        IsAvailable ? DisplayName : $"{DisplayName} (不可用)";
}
