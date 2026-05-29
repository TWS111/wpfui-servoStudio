// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;

namespace Core.Usb;

/// <summary>
/// USB 协议栈的逻辑通道。<br/>
/// 从机使用 ThreadX 中间件 <c>USBX</c>（默认配置），HPM 系列 MCU 上运行从机协议栈，
/// 与现有 EtherCAT / CANopen / Modbus 三栈承载的数据并非同一类：USB 通道专门用于
/// <b>大范围数据传输</b>与<b>高带宽数据回传</b>，具体载荷格式 / 协议字段由后续部署确定，
/// 这里仅预留通道分类。
/// </summary>
public enum UsbChannel : ushort
{
    /// <summary>未指定 / 控制类报文（保留）。</summary>
    Control = 0x0000,

    /// <summary>上位机 → 从机：负载计算所得的曲线拟合数据下发。</summary>
    CurveFitting = 0x0010,

    /// <summary>上位机 ↔ 从机：电机自适应参数的调节计算。</summary>
    AdaptiveParam = 0x0020,

    /// <summary>从机 → 上位机：高带宽数据上报（采样波形 / 状态流等）。</summary>
    HighBandwidthTelemetry = 0x0030,

    /// <summary>
    /// 厂家自动化测试通道 —— 上位机下发测试命令、从机回传测试结果（合格 / 故障级别 / 不可用）。<br/>
    /// 用于出厂前板级 / 外设 / 功能测试。具体命令与结果字段见 <see cref="FactoryTestProtocol"/>。
    /// </summary>
    FactoryTest = 0x00E0,

    /// <summary>厂商私有 / 调试通道（保留）。</summary>
    VendorPrivate = 0x00F0,

    /// <summary>
    /// 回环测试通道 —— 主机发出的帧由从机原样转发回来，用于延时 / 带宽 / 数据完整性测量。<br/>
    /// 从机须在 USBX 协议栈中将收到的该通道帧无修改地回送（Echo）。
    /// </summary>
    Loopback = 0x00FF,
}

/// <summary>USB 传输方向（相对于上位机视角）。</summary>
public enum UsbDirection : byte
{
    /// <summary>上位机 → 从机（OUT）。</summary>
    HostToDevice = 0,

    /// <summary>从机 → 上位机（IN）。</summary>
    DeviceToHost = 1,
}

/// <summary>
/// USB 工作方式 —— 即从机 USBX 配置的 USB 类组合，决定宿主侧应使用何种 <see cref="IUsbBus"/> 实现。
/// </summary>
public enum UsbWorkMode
{
    /// <summary>
    /// CDC-ACM 虚拟串口。<br/>
    /// USBX 内置 demo 之一，宿主免驱（系统自带 usbser.sys），设备会被枚举为一个 COM 端口；
    /// 速率受限（≈ 1 MB/s 量级），但部署最快、与现有串口工具链兼容。
    /// </summary>
    CdcAcm = 0,

    /// <summary>
    /// WinUSB / 自定义 Bulk-Only。<br/>
    /// 高带宽方案（曲线拟合下发 + 高带宽遥测），需要从机端实现 WCID/MS OS 描述符或安装 INF；
    /// 宿主侧通过 WinUSB API 直接读写 Bulk 端点。
    /// </summary>
    WinUsbBulk = 1,

    /// <summary>
    /// MSC（Mass Storage Class）—— 大容量存储。<br/>
    /// 设备被枚举为可移动磁盘，曲线 / 参数文件以文件形式交换；
    /// 适合"一次性大块下载"场景，不适合实时高带宽遥测。
    /// </summary>
    Msc = 2,
}

/// <summary>USB 设备类（Device Class / Interface Class），用于设备列表过滤。</summary>
public enum UsbDeviceClass
{
    /// <summary>未识别。</summary>
    Unknown = 0,

    /// <summary>厂商自定义（包含 WinUSB Bulk-Only），class code 0xFF。</summary>
    Vendor = 0xFF,

    /// <summary>CDC（含 CDC-ACM 虚拟串口），class code 0x02 / 0x0A。</summary>
    Cdc = 0x02,

    /// <summary>HID。</summary>
    Hid = 0x03,

    /// <summary>大容量存储 MSC，class code 0x08。</summary>
    Msc = 0x08,

    /// <summary>USB 复合设备父节点（usbccgp 驱动），不应出现在设备选择列表中。</summary>
    Composite = 0xEF,
}

/// <summary>
/// USB 主机端默认参数 / 占位常量。<br/>
/// 具体数值待后续与从机 USBX 描述符对齐后调整 —— HPM 官方 demo 默认 VID 为 HPMicro <c>0x34B7</c>，
/// PID 视具体 demo 而定，建议在 UI 设置页内由用户追加自定义白名单。
/// </summary>
public static class UsbDefaults
{
    /// <summary>HPMicro 公开 VID（HPM 系列 MCU + USBX 默认 demo）。</summary>
    public const ushort HpMicroVendorId = 0x34B7;

    /// <summary>白名单为空 / 通配 PID 时使用。</summary>
    public const ushort AnyProductId = 0x0000;

    /// <summary>CDC-ACM 工作方式下从机固件使用的 PID（与从机 USBX descriptor 对齐）。</summary>
    public const ushort CdcAcmProductId = 0x6001;

    /// <summary>WinUSB / Bulk-Only 工作方式下从机固件使用的 PID（与从机 USBX descriptor 对齐）。</summary>
    public const ushort WinUsbBulkProductId = 0x6002;

    /// <summary>
    /// WinUSB 设备接口 GUID。<br/>
    /// 从机固件在 MS OS 2.0 Registry Property（<c>DeviceInterfaceGUIDs</c>）中注册的自定义 GUID，
    /// Windows 据此将接口驱动绑定为 WinUSB 并以此 GUID 注册设备接口路径。<br/>
    /// 宿主侧须使用相同 GUID 调用 <c>SetupDiGetClassDevs</c>，否则无法获取 WinUSB 接口路径。<br/>
    /// 与从机 <c>UsbXTest.c</c> 中 MS OS 2.0 描述符保持同步。
    /// </summary>
    public static readonly Guid WinUsbDeviceInterfaceGuid = new("FE16556F-B828-4689-9E11-36139EE9EBDC");

    /// <summary>默认 Bulk OUT 端点（待与 USBX 配置同步）。</summary>
    public const byte BulkOutEndpoint = 0x01;

    /// <summary>默认 Bulk IN 端点（待与 USBX 配置同步）。</summary>
    public const byte BulkInEndpoint = 0x81;

    /// <summary>单次 Bulk 传输最大长度（默认 USB 2.0 高速 512 B；具体由从机 wMaxPacketSize 确定）。</summary>
    public const int MaxPacketSize = 512;

    /// <summary>默认接收超时 (ms)。</summary>
    public const int DefaultReceiveTimeoutMs = 200;

    /// <summary>默认发送超时 (ms)。</summary>
    public const int DefaultSendTimeoutMs = 200;

    /// <summary>设备 Manufacturer/Product 字符串过滤的内置关键词（不区分大小写）。</summary>
    public static readonly string[] HpmKeywordHints =
    [
        "HPM",
        "HPMicro",
        "USBX",
        "ThreadX",
    ];
}
