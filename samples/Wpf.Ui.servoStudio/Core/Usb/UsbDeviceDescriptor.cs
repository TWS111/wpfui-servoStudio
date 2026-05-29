// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;

namespace Core.Usb;

/// <summary>
/// 一个被枚举到的 USB 设备的描述。<br/>
/// 与 CANopen 子系统中的 <c>CanAdapterDescriptor</c> 平行：用于绑定到 ComboBox 显示，
/// 并在用户点击"连接"时由 <see cref="UsbMaster"/> 据此打开底层 <see cref="IUsbBus"/>。
/// </summary>
public sealed class UsbDeviceDescriptor
{
    public ushort VendorId { get; init; }

    public ushort ProductId { get; init; }

    /// <summary>设备 Manufacturer 字符串（来自注册表 / 描述符），可能为空。</summary>
    public string Manufacturer { get; init; } = string.Empty;

    /// <summary>设备 Product 字符串（FriendlyName / DeviceDesc），可能为空。</summary>
    public string Product { get; init; } = string.Empty;

    /// <summary>USB 序列号（同一 VID/PID 多设备区分用），可能为空。</summary>
    public string SerialNumber { get; init; } = string.Empty;

    /// <summary>
    /// Windows 设备实例 ID，形如 <c>USB\VID_34B7&amp;PID_A001\6&amp;1234ABCD&amp;0&amp;1</c>。<br/>
    /// 作为持久化记忆的键（"自动记忆上次选择的 USB 设备"）。
    /// </summary>
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>设备类（来自接口或设备描述符）。</summary>
    public UsbDeviceClass DeviceClass { get; init; } = UsbDeviceClass.Unknown;

    /// <summary>
    /// 仅当 <see cref="DeviceClass"/> = Cdc 时填充：宿主侧分配的 COM 端口名（"COM7"）。<br/>
    /// CDC-ACM 工作方式下，<see cref="CdcAcmUsbBus"/> 会以此为参数打开串口。
    /// </summary>
    public string ComPort { get; init; } = string.Empty;

    /// <summary>设备当前是否已被驱动占用 / 处于可用状态。</summary>
    public bool IsAvailable { get; init; } = true;

    /// <summary>UI 显示名（用于 ComboBox.ItemTemplate）。</summary>
    public string DisplayName
    {
        get
        {
            string vidPid = $"{VendorId:X4}:{ProductId:X4}";
            string label = !string.IsNullOrEmpty(Product) ? Product :
                           !string.IsNullOrEmpty(Manufacturer) ? Manufacturer :
                           "USB Device";
            string suffix = DeviceClass switch
            {
                UsbDeviceClass.Cdc when !string.IsNullOrEmpty(ComPort) => $" [{ComPort}]",
                _ => string.Empty,
            };
            return $"{label}  ({vidPid}){suffix}";
        }
    }

    /// <summary>用于排查（设备类、序列号等）。</summary>
    public string Note
    {
        get
        {
            string cls = DeviceClass switch
            {
                UsbDeviceClass.Vendor => "Vendor / Bulk",
                UsbDeviceClass.Cdc => "CDC-ACM",
                UsbDeviceClass.Hid => "HID",
                UsbDeviceClass.Msc => "MSC",
                _ => "Unknown",
            };
            return string.IsNullOrEmpty(SerialNumber) ? cls : $"{cls}, SN={SerialNumber}";
        }
    }

    public override string ToString() => DisplayName;
}
