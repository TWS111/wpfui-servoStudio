// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Core.Usb;

/// <summary>
/// 已组装的 USB 枚举过滤规则。<br/>
/// 与<see cref="UsbDeviceEnumerator"/> 配合：枚举出所有设备 → 应用以下任一/组合条件后返回最终列表。<br/>
/// 与 CANopen 子系统中"按 CanAdapterKind 收窄"思路对位。
/// </summary>
public sealed class UsbDeviceFilter
{
    /// <summary>是否启用 VID/PID 白名单过滤。<see cref="VidPidWhitelist"/> 为空时此开关无效。</summary>
    public bool UseVidPidWhitelist { get; set; } = true;

    /// <summary>是否启用 Manufacturer/Product 字符串关键词过滤。</summary>
    public bool UseStringKeywords { get; set; } = true;

    /// <summary>是否启用设备类过滤（与 <see cref="WorkMode"/> 协同收窄）。</summary>
    public bool UseDeviceClass { get; set; } = true;

    /// <summary>当前希望使用的工作方式 —— 决定 <see cref="UseDeviceClass"/> 应放行哪些 <see cref="UsbDeviceClass"/>。</summary>
    public UsbWorkMode WorkMode { get; set; } = UsbWorkMode.CdcAcm;

    /// <summary>VID/PID 白名单 (PID = 0 → 该 VID 下任意 PID)。</summary>
    public IReadOnlyList<(ushort Vid, ushort Pid)> VidPidWhitelist { get; set; } = new List<(ushort, ushort)>
    {
        (UsbDefaults.HpMicroVendorId, UsbDefaults.AnyProductId),
    };

    /// <summary>Manufacturer/Product 字符串关键词（不区分大小写包含匹配）。默认 = HpmKeywordHints。</summary>
    public IReadOnlyList<string> StringKeywords { get; set; } = UsbDefaults.HpmKeywordHints;

    /// <summary>对单个设备做过滤判断。</summary>
    public bool Accept(UsbDeviceDescriptor d)
    {
        if (UseVidPidWhitelist && VidPidWhitelist.Count > 0)
        {
            bool vidPidOk = VidPidWhitelist.Any(p =>
                p.Vid == d.VendorId && (p.Pid == UsbDefaults.AnyProductId || p.Pid == d.ProductId));
            if (!vidPidOk)
            {
                return false;
            }
        }

        if (UseDeviceClass)
        {
            // Composite 父节点无论何种工作方式都排除
            if (d.DeviceClass == UsbDeviceClass.Composite)
            {
                return false;
            }

            UsbDeviceClass[] expected = WorkMode switch
            {
                UsbWorkMode.CdcAcm => [UsbDeviceClass.Cdc],
                UsbWorkMode.WinUsbBulk => [UsbDeviceClass.Vendor, UsbDeviceClass.Unknown],
                UsbWorkMode.Msc => [UsbDeviceClass.Msc],
                _ => [UsbDeviceClass.Unknown],
            };
            // Unknown 视为放行（很多 USBX demo 在 Device Descriptor 处不设 class）
            if (d.DeviceClass != UsbDeviceClass.Unknown && !expected.Contains(d.DeviceClass))
            {
                return false;
            }
        }

        if (UseStringKeywords && StringKeywords.Count > 0)
        {
            string blob = $"{d.Manufacturer}\n{d.Product}";
            bool kwOk = StringKeywords.Any(kw =>
                blob.Contains(kw, StringComparison.OrdinalIgnoreCase));
            // 字符串过滤是"软命中"：如果命中加分；如果未命中但 VID/PID 已命中 HPMicro，仍放行（避免漏掉无字符串描述符的固件）
            if (!kwOk && !(UseVidPidWhitelist && VidPidWhitelist.Any(p => p.Vid == d.VendorId)))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// USB 设备枚举器：基于 Windows 注册表 <c>HKLM\SYSTEM\CurrentControlSet\Enum\USB</c>，
/// 不依赖额外 NuGet 包 / P/Invoke，对运行于 net9.0-windows 的 WPF 工程开箱可用。<br/>
/// <para>
/// 比 <c>SetupDiGetClassDevs</c> 路径更简单：注册表里已经包含了 Windows 解析好的
/// VID/PID/Mfg/DeviceDesc/FriendlyName/HardwareID 等字段，且能列出"曾经接入过"的设备
/// （由 <see cref="OnlyPresent"/> 控制是否仅返回当前在线的）。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class UsbDeviceEnumerator
{
    private const string EnumUsbRoot = @"SYSTEM\CurrentControlSet\Enum\USB";

    /// <summary>
    /// 枚举注册表中所有 USB 设备并按 <paramref name="filter"/> 过滤。<br/>
    /// 失败 / 非 Windows 平台静默返回空列表。
    /// </summary>
    /// <param name="filter">过滤规则；为 null 时返回全部。</param>
    /// <param name="onlyPresent">仅返回 ConfigFlags 标记为"在线"的设备（默认 true）。</param>
    /// <param name="diagnostics">可选：写入诊断行（"扫描到 N 项"，被某条规则拒绝的原因等）。</param>
    public static IReadOnlyList<UsbDeviceDescriptor> Enumerate(
        UsbDeviceFilter? filter = null,
        bool onlyPresent = true,
        IList<string>? diagnostics = null)
    {
        var result = new List<UsbDeviceDescriptor>();
        if (!OperatingSystem.IsWindows())
        {
            diagnostics?.Add("USB 枚举不支持当前平台（仅 Windows）");
            return result;
        }

        try
        {
            using RegistryKey? root = Registry.LocalMachine.OpenSubKey(EnumUsbRoot);
            if (root is null)
            {
                diagnostics?.Add($"未找到注册表项 HKLM\\{EnumUsbRoot}");
                return result;
            }

            int total = 0, kept = 0;
            foreach (string vidPidName in root.GetSubKeyNames())
            {
                // 形如 "VID_34B7&PID_A001"
                if (!TryParseVidPid(vidPidName, out ushort vid, out ushort pid))
                {
                    continue;
                }

                using RegistryKey? vidPidKey = root.OpenSubKey(vidPidName);
                if (vidPidKey is null)
                {
                    continue;
                }

                foreach (string instanceName in vidPidKey.GetSubKeyNames())
                {
                    using RegistryKey? instanceKey = vidPidKey.OpenSubKey(instanceName);
                    if (instanceKey is null)
                    {
                        continue;
                    }

                    total++;
                    UsbDeviceDescriptor desc = ReadDescriptor(vid, pid, vidPidName, instanceName, instanceKey);

                    if (onlyPresent && !desc.IsAvailable)
                    {
                        continue;
                    }

                    if (filter is null || filter.Accept(desc))
                    {
                        result.Add(desc);
                        kept++;
                    }
                }
            }
            diagnostics?.Add($"USB 枚举完成：扫描 {total} 项，过滤后保留 {kept} 项");
        }
        catch (Exception ex)
        {
            diagnostics?.Add($"USB 枚举异常：{ex.Message}");
        }

        return result;
    }

    [SupportedOSPlatform("windows")]
    private static UsbDeviceDescriptor ReadDescriptor(
        ushort vid, ushort pid, string vidPidName, string instanceName, RegistryKey instanceKey)
    {
        string mfg = instanceKey.GetValue("Mfg") as string ?? string.Empty;
        string desc = instanceKey.GetValue("DeviceDesc") as string ?? string.Empty;
        string friendly = instanceKey.GetValue("FriendlyName") as string ?? string.Empty;
        string service = instanceKey.GetValue("Service") as string ?? string.Empty;
        string @class = instanceKey.GetValue("Class") as string ?? string.Empty;
        int configFlags = (instanceKey.GetValue("ConfigFlags") as int?) ?? 0;
        // 注册表多值常以 "驱动名;Hardware Name" 格式存储，截取分号后人类可读部分
        static string Tail(string s)
        {
            int i = s.LastIndexOf(';');
            return i >= 0 && i + 1 < s.Length ? s[(i + 1)..] : s;
        }

        UsbDeviceClass cls = ClassifyByService(service, @class);
        string com = TryReadComPort(instanceKey);

        // ConfigFlags bit 0x20 (CONFIGFLAG_REINSTALL) / 0x40 (CONFIGFLAG_FAILEDINSTALL) 视为不可用
        bool available = (configFlags & 0x60) == 0;

        // WinUSB 设备：拔出后注册表项仍保留（幽灵条目）。
        // Control 子键是否存在并不可靠（Windows 不保证一定创建），
        // 改用 CM_Locate_DevNodeW 向 PnP 管理器查询节点是否当前在线：
        // CM_LOCATE_DEVNODE_NORMAL(0) 仅定位实际存在的节点，
        // 幽灵节点（设备已拔出但注册表项未清理）会返回非零错误码。
        if (available && string.Equals(service, "WinUSB", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string fullInstanceId = $"USB\\{vidPidName}\\{instanceName}";
                int cr = NativeMethods.CM_Locate_DevNodeW(out _, fullInstanceId, NativeMethods.CM_LOCATE_DEVNODE_NORMAL);
                if (cr != NativeMethods.CR_SUCCESS)
                {
                    available = false;
                }
            }
            catch
            {
                // CM API 不可用（极少见），保留 ConfigFlags 判断结果
            }
        }

        string product = !string.IsNullOrEmpty(friendly) ? friendly : Tail(desc);

        return new UsbDeviceDescriptor
        {
            VendorId = vid,
            ProductId = pid,
            Manufacturer = Tail(mfg),
            Product = product,
            SerialNumber = ExtractSerialFromInstance(instanceName),
            InstanceId = $"USB\\{vidPidName}\\{instanceName}",
            DeviceClass = cls,
            ComPort = com,
            IsAvailable = available,
        };
    }

    private static UsbDeviceClass ClassifyByService(string service, string @class)
    {
        if (string.Equals(service, "usbccgp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(@class, "USB", StringComparison.OrdinalIgnoreCase))
        {
            return UsbDeviceClass.Composite;
        }

        if (string.Equals(service, "usbser", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(@class, "Ports", StringComparison.OrdinalIgnoreCase))
        {
            return UsbDeviceClass.Cdc;
        }

        if (string.Equals(service, "HidUsb", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(@class, "HIDClass", StringComparison.OrdinalIgnoreCase))
        {
            return UsbDeviceClass.Hid;
        }

        if (string.Equals(service, "USBSTOR", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(@class, "USB", StringComparison.OrdinalIgnoreCase) && service.Contains("STOR", StringComparison.OrdinalIgnoreCase))
        {
            return UsbDeviceClass.Msc;
        }

        if (string.Equals(service, "WinUSB", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(@class, "USBDevice", StringComparison.OrdinalIgnoreCase))
        {
            return UsbDeviceClass.Vendor;
        }

        return UsbDeviceClass.Unknown;
    }

    [SupportedOSPlatform("windows")]
    private static string TryReadComPort(RegistryKey instanceKey)
    {
        try
        {
            using RegistryKey? deviceParameters = instanceKey.OpenSubKey("Device Parameters");
            return deviceParameters?.GetValue("PortName") as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryParseVidPid(string s, out ushort vid, out ushort pid)
    {
        vid = pid = 0;
        // 形如 "VID_34B7&PID_6002" 或复合设备子接口 "VID_34B7&PID_6002&MI_00"
        // &MI_xx 段必须跳过，否则 PID_ 后 4 位会取到 "FFFF" 等错误值
        int vIdx = s.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        int pIdx = s.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (vIdx < 0 || pIdx < 0 || vIdx + 8 > s.Length || pIdx + 8 > s.Length)
        {
            return false;
        }

        if (!ushort.TryParse(s.AsSpan(vIdx + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out vid))
        {
            return false;
        }

        // PID 后紧跟 '&' 或字符串结束才是合法的 4 位 PID，排除 &MI_xx 干扰
        ReadOnlySpan<char> pidSpan = s.AsSpan(pIdx + 4, 4);
        if (!ushort.TryParse(pidSpan, System.Globalization.NumberStyles.HexNumber, null, out pid))
        {
            return false;
        }

        // 若 PID 后紧跟 "&MI" 说明这是复合设备子接口键，跳过（由父设备键统一处理）
        int afterPid = pIdx + 8;
        if (afterPid < s.Length && s[afterPid] == '&' &&
            s.IndexOf("&MI_", afterPid, StringComparison.OrdinalIgnoreCase) == afterPid)
        {
            return false;
        }

        return true;
    }

    private static string ExtractSerialFromInstance(string instanceName)
    {
        // 实例名形如 "0123456789ABCDEF" (真实 USB 序列号) 或 "6&abcdef&0&1" (Windows 合成 ID)
        // 含 '&' 视为合成 ID，无序列号
        return instanceName.Contains('&') ? string.Empty : instanceName;
    }

    [SupportedOSPlatform("windows")]
    private static class NativeMethods
    {
        /// <summary>CM_Locate_DevNodeW 成功返回值。</summary>
        public const int CR_SUCCESS = 0x00000000;

        /// <summary>只定位实际存在的节点（幽灵节点返回 CR_NO_SUCH_DEVINST）。</summary>
        public const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;

        /// <summary>
        /// 向配置管理器查询指定设备实例 ID 对应的节点句柄。<br/>
        /// 使用 <see cref="CM_LOCATE_DEVNODE_NORMAL"/> 时只定位当前存在的节点，
        /// 未插入设备返回非零错误码（如 CR_NO_SUCH_DEVINST = 0x00000013）。
        /// </summary>
        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode, SetLastError = false, ExactSpelling = true)]
        public static extern int CM_Locate_DevNodeW(
            out uint pdnDevInst,
            string pDeviceID,
            uint ulFlags);
    }
}
