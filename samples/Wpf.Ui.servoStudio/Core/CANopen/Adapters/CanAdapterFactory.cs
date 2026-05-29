// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Wpf.Ui.servoStudio.Services;

namespace Core.CANopen.Adapters;

/// <summary>
/// 统一的 CAN 适配器枚举与构造工厂。<br/>
/// 调用 <see cref="Enumerate"/> 一次返回当前机器上"已识别 + DLL 已就绪"的所有 CAN 卡条目，
/// 调用 <see cref="Create(CanAdapterDescriptor, string?)"/> 按条目构造对应的 <see cref="ICanBus"/>。<br/>
/// 设计要点：所有厂家 SDK 用延迟绑定，缺失时不会抛 <c>DllNotFoundException</c> 出本类边界，
/// 只是把对应类别标记为不可用（<see cref="CanAdapterDescriptor.IsAvailable"/>=false）。
/// </summary>
public static class CanAdapterFactory
{
    /// <summary>枚举当前机器上可用的 CAN 适配器条目。</summary>
    /// <param name="includeUnavailable">是否包含 DLL 未部署或未插入设备的占位条目（用于 UI 提示）。</param>
    /// <param name="diag">
    /// 可选诊断日志输出。若非 <c>null</c>，将逐步把 DLL 查找、加载、设备探测每一步的结果
    /// 追加到该列表中，便于在 Debug 模式 UI 或 AppLog 中定位 "未检测到设备" 的具体原因。
    /// </param>
    public static IReadOnlyList<CanAdapterDescriptor> Enumerate(bool includeUnavailable = true, IList<string>? diag = null)
    {
        var list = new List<CanAdapterDescriptor>();
        Log(diag, $"=== CAN 适配器枚举开始 @ {DateTime.Now:HH:mm:ss.fff} ===");

        // 1) Slcan：基于已枚举的串口（与 Modbus 共用 SerialPortStream）
        string[] ports = SafeGetSerialPorts();
        Log(diag, $"[SLCAN] 串口数={ports.Length}: {string.Join(",", ports)}");
        foreach (string port in ports)
        {
            list.Add(new CanAdapterDescriptor(
                CanAdapterKind.Slcan,
                $"SLCAN ({port})",
                Identifier: port,
                IsAvailable: true,
                Note: "LAWICEL/CANable 兼容串口"));
        }

        // 2) PCAN-Basic：探测 USBBUS1..USBBUS4
        bool pcanDllOk = IsDllAvailableDetailed(PcanNative.Dll, out string pcanDllStatus);
        Log(diag, $"[PCAN] DLL: {pcanDllStatus}");
        ushort[] pcanCh = [PcanNative.PCAN_USBBUS1, PcanNative.PCAN_USBBUS2,
                           PcanNative.PCAN_USBBUS3, PcanNative.PCAN_USBBUS4];
        bool anyPcan = false;
        if (pcanDllOk)
        {
            for (int i = 0; i < pcanCh.Length; i++)
            {
                bool ok = PcanBasicCanBus.ProbeChannel(pcanCh[i]);
                Log(diag, $"[PCAN] Probe USBBUS{i + 1} (0x{pcanCh[i]:X4}): {(ok ? "OK" : "无设备")}");
                if (ok)
                {
                    list.Add(new CanAdapterDescriptor(
                        CanAdapterKind.PcanBasic,
                        $"PCAN-USB Bus{i + 1}",
                        Identifier: pcanCh[i].ToString("X4"),
                        IsAvailable: true,
                        Note: "PEAK PCANBasic"));
                    anyPcan = true;
                }
            }
        }
        if (!anyPcan && includeUnavailable)
        {
            list.Add(new CanAdapterDescriptor(
                CanAdapterKind.PcanBasic,
                "PCAN-USB",
                Identifier: pcanCh[0].ToString("X4"),
                IsAvailable: false,
                Note: pcanDllOk ? "未检测到 PEAK 设备" : "缺少 PCANBasic.dll"));
        }

        // 3) ControlCAN.dll（老版 ZLG SDK / 创芯 CANalyst-II 兼容 USBCAN-I/II）。
        //    注：USBCAN-E-U(20) / USBCAN-2E-U(21) 使用新版 zlgcan.dll 路径，此处不重复探测，
        //    避免升驱动后 ControlCAN.dll 对同一物理设备返回多个伪类型句柄。
        bool ccDllOk = IsDllAvailableDetailed(ControlCanNative.Dll, out string ccDllStatus);
        Log(diag, $"[ControlCAN] DLL: {ccDllStatus}");

        // iTEKON 兼容厂家：可能只装了 ECANVCI.dll/ECanVci64.dll 而无 ControlCAN.dll。
        // 通过 CanNativeResolver 已让 P/Invoke 自动重定向，此处仅诊断显示。
        bool iTekonDllSeen = false;
        if (!ccDllOk)
        {
            foreach (string alias in new[] { "ECANVCI.dll", "ECanVci64.dll", "ECanVci.dll" })
            {
                if (IsDllAvailableDetailed(alias, out string aliasStatus))
                {
                    Log(diag, $"[ControlCAN] iTEKON 兼容 DLL: {alias} -> {aliasStatus}");
                    iTekonDllSeen = true;
                    ccDllOk = true;
                    break;
                }
            }
        }
        bool anyCc = false;
        if (ccDllOk)
        {
            var ccGroups = new Dictionary<uint, (string Name, int Count)>();
            (uint type, string name)[] knownTypes =
            [
                (ControlCanNative.VCI_USBCAN2,    "USBCAN-II / CANalyst-II"),
                (ControlCanNative.VCI_USBCAN1,    "USBCAN-I"),
            ];

            // 历史缓存：上次扫描成功的 (type, idx) 命中后跳过同 idx 下其余 type 的盲探。
            HashSet<(uint Type, uint Index)> ccHistory = LoadControlCanProbeHistory();
            var ccNewHistory = new List<(uint Type, uint Index)>();
            Log(diag, $"[ControlCAN] Probe loop start: historyCount={ccHistory.Count}");

            // 同一 idx 如果多个 type 都 Probe 成功（这在 ControlCAN 驱动中是常见伪阳性，
            // 因为 VCI_OpenDevice 不严格校验 type），只保留第一个。
            for (uint idx = 0; idx < 2; idx++)
            {
                bool addedThisIdx = false;

                // 优先尝试历史记录里这个 idx 命中过的 type
                var ccStaleHistory = new List<(uint Type, uint Index)>();
                foreach ((uint htype, uint hidx) in ccHistory)
                {
                    if (hidx != idx)
                    {
                        continue;
                    }

                    var matched = knownTypes.FirstOrDefault(t => t.type == htype);
                    if (matched.type == 0 && matched.name is null)
                    {
                        continue;
                    }

                    bool ok = ControlCanBus.ProbeDevice(htype, idx);
                    Log(diag, $"[ControlCAN] History-hit Probe type={htype} ({matched.name}) idx={idx}: {(ok ? "OK" : "失败")}");
                    if (ok)
                    {
                        string brand = iTekonDllSeen ? "iTEKON " : "";
                        if (!ccGroups.TryGetValue(htype, out var group))
                        {
                            ccGroups[htype] = ($"{brand}{matched.name}", 1);
                        }
                        else
                        {
                            ccGroups[htype] = (group.Name, group.Count + 1);
                        }

                        anyCc = true;
                        addedThisIdx = true;
                        ccNewHistory.Add((htype, idx));
                        break;
                    }
                    else
                    {
                        // 缓存命中失败：标记为陈旧，下面统一移除
                        ccStaleHistory.Add((htype, hidx));
                    }
                }
                foreach (var stale in ccStaleHistory)
                {
                    ccHistory.Remove(stale);
                }

                // 历史未命中：回退全量盲探
                if (!addedThisIdx)
                {
                    foreach (var (type, name) in knownTypes)
                    {
                        bool ok = ControlCanBus.ProbeDevice(type, idx);
                        Log(diag, $"[ControlCAN] Probe type={type} ({name}) idx={idx}: {(ok ? "OK" : "无设备")}");
                        if (ok && !addedThisIdx)
                        {
                            string brand = iTekonDllSeen ? "iTEKON " : "";
                            if (!ccGroups.TryGetValue(type, out var group))
                            {
                                ccGroups[type] = ($"{brand}{name}", 1);
                            }
                            else
                            {
                                ccGroups[type] = (group.Name, group.Count + 1);
                            }

                            anyCc = true;
                            addedThisIdx = true; // 同一物理 idx 只记一个明确成功的 type
                            ccNewHistory.Add((type, idx));
                        }
                    }
                }
            }

            // 持久化新的命中记录（仅在扫描到至少一个设备时更新，避免误清空）
            if (ccNewHistory.Count > 0)
            {
                SaveControlCanProbeHistory(ccNewHistory);
            }

            foreach (var (type, group) in ccGroups)
            {
                string note = iTekonDllSeen ? "ECANVCI.dll (iTEKON 兼容)" : "ControlCAN.dll";
                int channelCount = GetControlCanChannelCount(type);
                list.Add(new CanAdapterDescriptor(
                    CanAdapterKind.ControlCan,
                    group.Name,
                    Identifier: $"{type}/0/0",
                    IsAvailable: true,
                    Note: $"{note}; ch={channelCount}; devices={group.Count}",
                    ChannelCount: channelCount,
                    DeviceCount: group.Count));
            }
        }
        if (!anyCc && includeUnavailable)
        {
            list.Add(new CanAdapterDescriptor(
                CanAdapterKind.ControlCan,
                "ZLG/创芯/iTEKON ControlCAN",
                Identifier: $"{ControlCanNative.VCI_USBCAN2}/0/0",
                IsAvailable: false,
                Note: ccDllOk ? "未检测到设备" : "缺少 ControlCAN.dll / ECANVCI.dll"));
        }

        // 让 USB 协议栈在两套 ZLG SDK（ControlCAN ↔ zlgcan）之间释放设备句柄。
        // 实测：两者对同一硬件（如 USBCAN-2E-U）的探测若紧贴，后者 OpenDevice 可能返回 0。
        Thread.Sleep(50);

        // 4) ZLG 新版 zlgcan.dll —— USBCAN-2E-U 等新一代设备实际依赖此 SDK
        bool zlgDllOk = IsDllAvailableDetailed(ZlgcanNative.Dll, out string zlgDllStatus);
        Log(diag, $"[zlgcan] DLL: {zlgDllStatus}");

        // 诊断：列出 zlgcan/kerneldlls 中实际存在的关键 backend DLL 与是否能独立加载。
        if (zlgDllOk)
        {
            string? zdir = ZlgCanBus.TryGetZlgcanDir();
            string kdir = zdir != null ? Path.Combine(zdir, "kerneldlls") : "";
            Log(diag, $"[zlgcan] kerneldlls 路径={kdir}, 存在={Directory.Exists(kdir)}");
            if (Directory.Exists(kdir))
            {
                string[] critical = ["dll_cfg.ini", "USBCAN.xml", "VCI_USBCAN2.xml", "USBCAN_E_64.dll", "USBCAN_4E_U_X64.dll", "USBCAN_8E_U_x64.dll", "USBCANFD.dll", "USBCANFD800U.dll"];
                foreach (string n in critical)
                {
                    string full = Path.Combine(kdir, n);
                    bool exists = File.Exists(full);
                    string loadInfo = "未尝试";
                    if (exists && n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        if (NativeLibrary.TryLoad(full, out IntPtr h))
                        {
                            NativeLibrary.Free(h);
                            loadInfo = "可独立加载";
                        }
                        else
                        {
                            loadInfo = $"加载失败 GetLastError={Marshal.GetLastWin32Error()}";
                        }
                    }
                    else if (exists)
                    {
                        loadInfo = "配置文件存在";
                    }
                    Log(diag, $"[zlgcan] backend {n}: 存在={exists}, {loadInfo}");
                }
                string devicesProperty = Path.Combine(kdir, "devices_property");
                Log(diag, $"[zlgcan] backend devices_property: 存在={Directory.Exists(devicesProperty)}");
            }
        }

        bool anyZlg = false;
        if (zlgDllOk)
        {
            (uint type, string name)[] knownTypes =
            [
                (ZlgcanNative.ZCAN_USBCANFD_200U, "USBCANFD-200U"),
                (ZlgcanNative.ZCAN_USBCANFD_100U, "USBCANFD-100U"),
                (ZlgcanNative.ZCAN_USBCANFD_MINI, "USBCANFD-MINI"),
                (ZlgcanNative.ZCAN_USBCANFD_800U, "USBCANFD-800U"),
                (ZlgcanNative.ZCAN_USBCAN_2E_U,   "USBCAN-2E-U"),
                (ZlgcanNative.ZCAN_USBCAN_E_U,    "USBCAN-E-U"),
                (ZlgcanNative.ZCAN_USBCAN_4E_U,   "USBCAN-4E-U"),
                (ZlgcanNative.ZCAN_USBCAN_8E_U,   "USBCAN-8E-U"),
            ];

            // 历史缓存：上次扫描成功的 (type, deviceIndex) 组合优先重试。
            // 命中后跳过该 deviceIndex 下其他 type 的盲探，把 32 次串行 Open/Close 降到设备数量级。
            HashSet<(uint Type, uint Index)> history = LoadZlgProbeHistory();

            var zlgGroups = new Dictionary<(uint Type, string DisplayName), (ZlgProbeResult Probe, int Count)>();
            var newHistory = new List<(uint Type, uint Index)>();

            // CWD 切换提前到整段循环外：原实现每次 Probe 都 SetCurrentDirectory，32 次重复切换；
            // 这里一次性切换、整段使用、最后恢复，避免重复的进程级状态写入。
            string? originalCwd = null;
            string? zlgDir = ZlgCanBus.TryGetZlgcanDir();
            try
            {
                if (zlgDir != null && Directory.Exists(zlgDir))
                {
                    originalCwd = Directory.GetCurrentDirectory();
                    try { Directory.SetCurrentDirectory(zlgDir); } catch { originalCwd = null; }
                }
                Log(diag, $"[zlgcan] Probe loop start: cwd={Directory.GetCurrentDirectory()}, historyCount={history.Count}");

                for (uint deviceIndex = 0; deviceIndex < 4; deviceIndex++)
                {
                    var successfulZlgProbes = new List<(uint Type, string Name, ZlgProbeResult Probe)>();

                    // 优先尝试历史记录里这个 index 命中过的 type，命中后跳过其余 type
                    bool historyHit = false;
                    var staleHistoryEntries = new List<(uint Type, uint Index)>();
                    foreach ((uint htype, uint hidx) in history)
                    {
                        if (hidx != deviceIndex)
                        {
                            continue;
                        }

                        var matched = knownTypes.FirstOrDefault(t => t.type == htype);
                        if (matched.type == 0 && matched.name is null)
                        {
                            continue;
                        }

                        ZlgProbeResult probe = ZlgCanBus.ProbeDeviceDetailed(htype, deviceIndex);
                        Log(diag, $"[zlgcan] History-hit Probe type={htype} ({matched.name}) idx={deviceIndex}: {(probe.IsSuccess ? "OK" : "失败")}");
                        if (probe.IsSuccess)
                        {
                            successfulZlgProbes.Add((htype, matched.name, probe));
                            historyHit = true;
                            break;
                        }
                        else
                        {
                            // 缓存命中失败：标记移除，避免每次扫描都先尝试已失效的旧 type
                            staleHistoryEntries.Add((htype, hidx));
                        }
                    }

                    // 把失败的历史项从工作集中剔除（防止同 index 还有其他历史 type 时继续尝试）
                    foreach (var stale in staleHistoryEntries)
                    {
                        history.Remove(stale);
                    }

                    // 历史未命中或缓存为空：回退到全量盲探
                    if (!historyHit)
                    {
                        foreach (var (type, name) in knownTypes)
                        {
                            ZlgProbeResult probe = ZlgCanBus.ProbeDeviceDetailed(type, deviceIndex);
                            int attempts = 1;
                            Log(diag, probe.ToNativeCallLog(attempts, name));
                            if (!probe.IsSuccess)
                            {
                                Thread.Sleep(30);
                                probe = ZlgCanBus.ProbeDeviceDetailed(type, deviceIndex);
                                attempts = 2;
                                Log(diag, probe.ToNativeCallLog(attempts, name));
                            }
                            bool ok = probe.IsSuccess;
                            Log(diag, $"[zlgcan] Probe type={type} ({name}) idx={deviceIndex}: {(ok ? "OK" : "无设备")} (attempts={attempts})");
                            if (ok)
                            {
                                successfulZlgProbes.Add((type, name, probe));
                            }
                        }
                    }

                    if (successfulZlgProbes.Count == 0)
                    {
                        if (deviceIndex > 0)
                        {
                            break;
                        }

                        continue;
                    }

                    var best = successfulZlgProbes
                        .OrderByDescending(item => ZlgProbeScore(item.Type, item.Name, item.Probe))
                        .First();
                    string displayName = NormalizeZlgDisplayName(best.Name, best.Probe);
                    Log(diag, $"[zlgcan] Select best idx={deviceIndex}: type={best.Type} ({displayName}), hwType='{best.Probe.HardwareType}', serial='{best.Probe.SerialNumber}', candidates={successfulZlgProbes.Count}");
                    var key = (best.Type, displayName);
                    if (!zlgGroups.TryGetValue(key, out var group))
                    {
                        zlgGroups[key] = (best.Probe, 1);
                    }
                    else
                    {
                        zlgGroups[key] = (group.Probe, group.Count + 1);
                    }

                    // 记录命中条目供下次扫描复用
                    newHistory.Add((best.Type, deviceIndex));
                }
            }
            finally
            {
                if (originalCwd != null)
                {
                    try { Directory.SetCurrentDirectory(originalCwd); } catch { /* ignore */ }
                }
            }

            // 持久化新的命中记录（仅在扫描到至少一个设备时更新，避免误清空）
            if (newHistory.Count > 0)
            {
                SaveZlgProbeHistory(newHistory);
            }

            foreach (var (key, group) in zlgGroups)
            {
                int channelCount = GetZlgChannelCount(key.Type, group.Probe);
                list.Add(new CanAdapterDescriptor(
                    CanAdapterKind.Zlgcan,
                    key.DisplayName,
                    Identifier: $"{key.Type}/0/0",
                    IsAvailable: true,
                    Note: BuildZlgProbeNote(key.Type, group.Probe, group.Count, channelCount),
                    ChannelCount: channelCount,
                    DeviceCount: group.Count));
                anyZlg = true;
            }
        }
        if (!anyZlg && includeUnavailable)
        {
            list.Add(new CanAdapterDescriptor(
                CanAdapterKind.Zlgcan,
                "ZLG zlgcan",
                Identifier: $"{ZlgcanNative.ZCAN_USBCAN_E_U}/0/0",
                IsAvailable: false,
                Note: zlgDllOk ? "未检测到设备（请确认驱动已安装、ZCANPRO 已完全退出、设备已插好）" : "缺少 zlgcan.dll"));
        }

        // 4.5) iTEKON：与 ControlCAN.dll 共用 P/Invoke 函数签名，但 DLL 名为 ECANVCI.dll。
        // 上方第 3) 段中已在探测成功时把 brand 改写为 "iTEKON ..."；这里另外提供
        // 显式占位项与"自动选择型号"入口，让用户在 UI 中明确看到 iTEKON 选项。
        if (!anyCc)
        {
            // 上方第 3) 段没有产生任何 ControlCAN/iTEKON 条目时，单独显示一个 iTEKON 占位项，
            // 方便用户安装驱动后定向排查（在 includeUnavailable=false 时不显示）。
            if (includeUnavailable)
            {
                bool itekonDllSeen = iTekonDllSeen
                    || IsDllAvailableDetailed("ECANVCI.dll", out _)
                    || IsDllAvailableDetailed("ECanVci64.dll", out _);
                list.Add(new CanAdapterDescriptor(
                    CanAdapterKind.ControlCan,
                    "iTEKON USBCAN (兼容 ControlCAN)",
                    Identifier: $"{ControlCanNative.VCI_USBCAN2}/0/0",
                    IsAvailable: false,
                    Note: itekonDllSeen
                        ? "检测到 ECANVCI.dll 但未发现设备，请确认 iTEKON 驱动已安装并已插好设备"
                        : "缺少 ECANVCI.dll / ECanVci64.dll（请安装 iTEKON CAN 驱动）"));
            }
        }

        // 5) Toomoss / 德州 USB2XXX
        bool tmDllOk = IsDllAvailableDetailed(ToomossNative.Dll, out string tmDllStatus);
        Log(diag, $"[Toomoss] DLL: {tmDllStatus}");
        int tmCount = tmDllOk ? ToomossCanBus.ScanCount() : 0;
        Log(diag, $"[Toomoss] ScanCount={tmCount}");
        for (ushort i = 0; i < tmCount; i++)
        {
            list.Add(new CanAdapterDescriptor(
                CanAdapterKind.Toomoss,
                $"Toomoss USB2XXX #{i} CH0",
                Identifier: $"{i}/0",
                IsAvailable: true,
                Note: ToomossNative.Dll));
        }
        if (tmCount == 0 && includeUnavailable)
        {
            list.Add(new CanAdapterDescriptor(
                CanAdapterKind.Toomoss,
                "Toomoss / 广成 USB2CAN",
                Identifier: "0/0",
                IsAvailable: false,
                Note: tmDllOk ? "未检测到设备" : $"缺少 {ToomossNative.Dll}"));
        }

        // 6) 总是提供 Virtual（用于离线测试）
        list.Add(new CanAdapterDescriptor(
            CanAdapterKind.Virtual,
            "虚拟回环（离线测试）",
            Identifier: "loopback",
            IsAvailable: true,
            Note: "VirtualCanBus"));

        int avail = list.Count(d => d.IsAvailable);
        Log(diag, $"=== 枚举结束：共 {list.Count} 项，其中 {avail} 项可用 ===");
        return list;
    }

    /// <summary>安全追加一行诊断信息（diag 为 null 时空操作）。</summary>
    private static void Log(IList<string>? diag, string line) => diag?.Add(line);

    /// <summary>从 UserSettings 读取上次扫描成功的 ZLG (type, deviceIndex) 组合。</summary>
    private static HashSet<(uint Type, uint Index)> LoadZlgProbeHistory()
    {
        var set = new HashSet<(uint, uint)>();
        try
        {
            UserSettings settings = UserSettingsService.Load();
            foreach (string entry in settings.ZlgProbeHistory ?? [])
            {
                string[] parts = entry.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2
                    && uint.TryParse(parts[0], out uint type)
                    && uint.TryParse(parts[1], out uint index))
                {
                    set.Add((type, index));
                }
            }
        }
        catch { /* 读取失败不影响枚举主流程 */ }
        return set;
    }

    /// <summary>把本次扫描成功的 (type, deviceIndex) 组合写入 UserSettings 供下次复用。</summary>
    private static void SaveZlgProbeHistory(IEnumerable<(uint Type, uint Index)> hits)
    {
        try
        {
            UserSettings settings = UserSettingsService.Load();
            settings.ZlgProbeHistory = hits.Select(h => $"{h.Type}:{h.Index}").Distinct().ToList();
            UserSettingsService.Save(settings);
        }
        catch { /* 写入失败不影响枚举主流程 */ }
    }

    /// <summary>从 UserSettings 读取上次扫描成功的 ControlCAN (type, deviceIndex) 组合。</summary>
    private static HashSet<(uint Type, uint Index)> LoadControlCanProbeHistory()
    {
        var set = new HashSet<(uint, uint)>();
        try
        {
            UserSettings settings = UserSettingsService.Load();
            foreach (string entry in settings.ControlCanProbeHistory ?? [])
            {
                string[] parts = entry.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2
                    && uint.TryParse(parts[0], out uint type)
                    && uint.TryParse(parts[1], out uint index))
                {
                    set.Add((type, index));
                }
            }
        }
        catch { /* 读取失败不影响枚举主流程 */ }
        return set;
    }

    /// <summary>把本次扫描成功的 ControlCAN (type, deviceIndex) 组合写入 UserSettings 供下次复用。</summary>
    private static void SaveControlCanProbeHistory(IEnumerable<(uint Type, uint Index)> hits)
    {
        try
        {
            UserSettings settings = UserSettingsService.Load();
            settings.ControlCanProbeHistory = hits.Select(h => $"{h.Type}:{h.Index}").Distinct().ToList();
            UserSettingsService.Save(settings);
        }
        catch { /* 写入失败不影响枚举主流程 */ }
    }

    private static int ZlgProbeScore(uint type, string configuredName, ZlgProbeResult probe)
    {
        string hardwareType = (probe.HardwareType ?? string.Empty).Replace("_", "-", StringComparison.OrdinalIgnoreCase);
        if (hardwareType.Contains("USBCANFD-200U", StringComparison.OrdinalIgnoreCase))
        {
            return 1000 + (type == ZlgcanNative.ZCAN_USBCANFD_200U ? 80 : IsZlgFdType(type) ? 40 : 0);
        }

        if (hardwareType.Contains("USBCANFD-100U", StringComparison.OrdinalIgnoreCase))
        {
            return 980 + (type == ZlgcanNative.ZCAN_USBCANFD_100U ? 80 : IsZlgFdType(type) ? 40 : 0);
        }

        if (hardwareType.Contains("USBCANFD-MINI", StringComparison.OrdinalIgnoreCase))
        {
            return 960 + (type == ZlgcanNative.ZCAN_USBCANFD_MINI ? 80 : IsZlgFdType(type) ? 40 : 0);
        }

        if (hardwareType.Contains("USBCANFD-800U", StringComparison.OrdinalIgnoreCase))
        {
            return 940 + (type == ZlgcanNative.ZCAN_USBCANFD_800U ? 80 : IsZlgFdType(type) ? 40 : 0);
        }

        if (hardwareType.Contains("USBCAN-2E-U", StringComparison.OrdinalIgnoreCase)
            || hardwareType.Contains("USBCAN2E", StringComparison.OrdinalIgnoreCase))
        {
            return 900 + (type == ZlgcanNative.ZCAN_USBCAN_2E_U ? 80 : 0);
        }

        if (hardwareType.Contains("USBCAN-E-U", StringComparison.OrdinalIgnoreCase)
            || hardwareType.Contains("USBCANE", StringComparison.OrdinalIgnoreCase))
        {
            return 880 + (type == ZlgcanNative.ZCAN_USBCAN_E_U ? 80 : 0);
        }

        return type switch
        {
            ZlgcanNative.ZCAN_USBCANFD_200U => 800,
            ZlgcanNative.ZCAN_USBCANFD_100U => 790,
            ZlgcanNative.ZCAN_USBCANFD_MINI => 780,
            ZlgcanNative.ZCAN_USBCANFD_800U => 770,
            ZlgcanNative.ZCAN_USBCAN_2E_U => 700,
            ZlgcanNative.ZCAN_USBCAN_E_U => 690,
            ZlgcanNative.ZCAN_USBCAN_4E_U => 680,
            ZlgcanNative.ZCAN_USBCAN_8E_U => 670,
            _ => 0,
        };
    }

    private static bool IsZlgFdType(uint type)
        => type == ZlgcanNative.ZCAN_USBCANFD_200U
        || type == ZlgcanNative.ZCAN_USBCANFD_100U
        || type == ZlgcanNative.ZCAN_USBCANFD_MINI
        || type == ZlgcanNative.ZCAN_USBCANFD_800U;

    private static int GetControlCanChannelCount(uint type)
        => type == ControlCanNative.VCI_USBCAN2 ? 2 : 1;

    private static int GetZlgChannelCount(uint type, ZlgProbeResult probe)
    {
        if (probe.CanNum is > 0)
        {
            return Math.Clamp((int)probe.CanNum.Value, 1, 16);
        }

        return type switch
        {
            ZlgcanNative.ZCAN_USBCAN_2E_U => 2,
            ZlgcanNative.ZCAN_USBCAN_4E_U => 4,
            ZlgcanNative.ZCAN_USBCAN_8E_U => 8,
            ZlgcanNative.ZCAN_USBCANFD_200U => 2,
            ZlgcanNative.ZCAN_USBCANFD_100U => 1,
            ZlgcanNative.ZCAN_USBCANFD_MINI => 1,
            ZlgcanNative.ZCAN_USBCANFD_800U => 8,
            _ => 1,
        };
    }

    private static string NormalizeZlgDisplayName(string configuredName, ZlgProbeResult probe)
    {
        string hardwareType = (probe.HardwareType ?? string.Empty).Replace("_", "-", StringComparison.OrdinalIgnoreCase).Trim();
        string[] knownNames =
        [
            "USBCANFD-200U",
            "USBCANFD-100U",
            "USBCANFD-MINI",
            "USBCANFD-800U",
            "USBCAN-2E-U",
            "USBCAN-E-U",
            "USBCAN-4E-U",
            "USBCAN-8E-U",
        ];
        foreach (string knownName in knownNames)
        {
            if (hardwareType.Contains(knownName, StringComparison.OrdinalIgnoreCase))
            {
                return knownName;
            }
        }

        return configuredName;
    }

    private static string BuildZlgProbeNote(uint openedType, ZlgProbeResult probe, int deviceCount, int channelCount)
    {
        var parts = new List<string> { $"zlgcan.dll type={openedType}" };
        if (!string.IsNullOrWhiteSpace(probe.HardwareType))
        {
            parts.Add($"hw={probe.HardwareType}");
        }

        if (!string.IsNullOrWhiteSpace(probe.SerialNumber))
        {
            parts.Add($"sn={probe.SerialNumber}");
        }

        parts.Add($"ch={channelCount}");
        parts.Add($"devices={deviceCount}");
        return string.Join("; ", parts);
    }

    /// <summary>按描述符构造对应 <see cref="ICanBus"/>。失败时返回 <c>null</c>。</summary>
    public static ICanBus? Create(CanAdapterDescriptor descriptor, string? overrideIdentifier = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        string id = overrideIdentifier ?? descriptor.Identifier;

        try
        {
            return descriptor.Kind switch
            {
                CanAdapterKind.Slcan      => new SerialCanBus(id),
                CanAdapterKind.Virtual    => new VirtualCanBus(),
                CanAdapterKind.PcanBasic  => CreatePcan(id),
                CanAdapterKind.ControlCan => CreateControlCan(id),
                CanAdapterKind.Zlgcan     => CreateZlg(id),
                CanAdapterKind.Toomoss    => CreateToomoss(id),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static PcanBasicCanBus CreatePcan(string id)
    {
        ushort ch = ushort.TryParse(id, System.Globalization.NumberStyles.HexNumber, null, out ushort h) ? h : PcanNative.PCAN_USBBUS1;
        return new PcanBasicCanBus(ch);
    }

    private static ControlCanBus CreateControlCan(string id)
    {
        // 格式 type/devIndex/canIndex
        string[] parts = id.Split('/');
        uint type = parts.Length > 0 && uint.TryParse(parts[0], out uint t) ? t : ControlCanNative.VCI_USBCAN2;
        uint dev = parts.Length > 1 && uint.TryParse(parts[1], out uint d) ? d : 0;
        uint can = parts.Length > 2 && uint.TryParse(parts[2], out uint c) ? c : 0;
        return new ControlCanBus(type, dev, can);
    }

    private static ZlgCanBus CreateZlg(string id)
    {
        string[] parts = id.Split('/');
        uint type = parts.Length > 0 && uint.TryParse(parts[0], out uint t) ? t : ZlgcanNative.ZCAN_USBCAN_E_U;
        uint dev = parts.Length > 1 && uint.TryParse(parts[1], out uint d) ? d : 0;
        uint ch = parts.Length > 2 && uint.TryParse(parts[2], out uint c) ? c : 0;
        return new ZlgCanBus(type, dev, ch);
    }

    private static ToomossCanBus CreateToomoss(string id)
    {
        string[] parts = id.Split('/');
        ushort dev = parts.Length > 0 && ushort.TryParse(parts[0], out ushort d) ? d : (ushort)0;
        byte ch = parts.Length > 1 && byte.TryParse(parts[1], out byte c) ? c : (byte)0;
        return new ToomossCanBus(dev, ch);
    }

    /// <summary>
    /// 检查 DLL 是否可加载，并通过 <paramref name="status"/> 输出查找/加载过程的人类可读结果。<br/>
    /// 搜索顺序：进程已加载模块 → 工程 native 目录 → System32 → PATH →
    /// Program Files 厂商目录（含 ZCANPRO、PEAK 等安装路径） → Windows 加载器标准搜索。<br/>
    /// 找到文件后立即将其所在目录（及 <c>kerneldlls/</c> 子目录）写入进程 PATH，
    /// 再通过 <see cref="NativeLibrary.TryLoad"/> 验证 DLL 及其依赖均可真正加载。
    /// </summary>
    private static bool IsDllAvailableDetailed(string dllName, out string status)
    {
        try
        {
            // 1) 进程已加载的模块
            foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
            {
                if (string.Equals(m.ModuleName, dllName, StringComparison.OrdinalIgnoreCase))
                {
                    // 即使 DLL 已加载，也必须确保后端 kerneldlls 目录已注册到 LOAD_LIBRARY_SEARCH_USER_DIRS，
                    // 否则 zlgcan.dll 内部按设备类型 LoadLibraryEx("USBCANFD.dll" 等) 仍会失败。
                    bool isZlgSdkLoaded = string.Equals(dllName, ZlgcanNative.Dll, StringComparison.OrdinalIgnoreCase);
                    string extraHint = "";
                    if (isZlgSdkLoaded)
                    {
                        string loadedDir = Path.GetDirectoryName(m.FileName) ?? "";
                        if (!string.IsNullOrEmpty(loadedDir))
                        {
                            PrependDirToPath(loadedDir);
                            string ksub = Path.Combine(loadedDir, "kerneldlls");
                            if (Directory.Exists(ksub))
                            {
                                PrependDirToPath(ksub);
                            }
                        }
                        ZlgKernelDllScanner.ApplyCachedDirs();
                        extraHint = $", kerneldlls user-dirs 已注册";
                    }
                    status = $"已在进程中加载 ({m.FileName}{extraHint})";
                    return true;
                }
            }

            // 2) 全量搜索：找到文件后更新 PATH，再让加载器用全路径/名称加载
            string? dllPath = FindDllPath(dllName);
            if (dllPath != null)
            {
                string dir = Path.GetDirectoryName(dllPath)!;
                PrependDirToPath(dir);
                // zlgcan.dll 等 ZLG SDK 将 USB 协议栈依赖放在 kerneldlls/ 子目录
                string kernelSub = Path.Combine(dir, "kerneldlls");
                bool hasKernel = Directory.Exists(kernelSub);
                if (hasKernel)
                {
                    PrependDirToPath(kernelSub);
                }

                // 补充驱动安装包目录（如 ZHIYUAN USBCAN_(2)E_U Driver\），
                // 确保 ZCAN_OpenDevice 调用时能找到 USBCAN_E_64.dll 等设备后端 DLL。
                bool isZlgSdk = string.Equals(dllName, ZlgcanNative.Dll, StringComparison.OrdinalIgnoreCase);
                string missingDevDllHint = "";
                if (isZlgSdk)
                {
                    ZlgKernelDllScanner.ApplyCachedDirs();
                    // 检测关键设备 DLL 是否可被加载（仅为诊断，不影响返回值）
                    bool usbcanEOk = NativeLibrary.TryLoad("USBCAN_E_64.dll", out IntPtr hE);
                    if (usbcanEOk)
                    {
                        NativeLibrary.Free(hE);
                    }
                    else
                    {
                        missingDevDllHint = "; USBCAN_E_64.dll 未找到（USBCAN-E-U/2E-U 将无法枚举，请安装 ZHIYUAN 驱动包或运行驱动 DLL 扫描）";
                    }
                }

                try
                {
                    if (NativeLibrary.TryLoad(dllPath, out IntPtr h1))
                    {
                        NativeLibrary.Free(h1);
                        status = $"已加载 (路径={dllPath}{(hasKernel ? ", kerneldlls=有" : ", kerneldlls=无")}{missingDevDllHint})";
                        return true;
                    }
                    status = $"找到文件但 NativeLibrary.TryLoad 失败 (路径={dllPath}{(hasKernel ? "" : "; 注意：未发现 kerneldlls 子目录")})";
                }
                catch (Exception loadEx)
                {
                    status = $"找到文件但加载抛异常 (路径={dllPath}): {loadEx.GetType().Name}: {loadEx.Message}";
                    return false;
                }
            }
            else
            {
                status = $"未在 native/、AppDir、System32、PATH 或 Program Files 厂商目录中找到 {dllName}";
            }

            // 3) 兜底：让 Windows 加载器走标准搜索顺序（System32、WinSxS、KnownDlls…）
            if (NativeLibrary.TryLoad(dllName, out IntPtr h2))
            {
                NativeLibrary.Free(h2);
                status = $"由 Windows 标准搜索加载 (DLL={dllName})";
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            status = $"检查 DLL 时异常: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 在所有候选目录中搜索 <paramref name="dllName"/>，返回第一个匹配的完整路径，
    /// 或 <c>null</c>（未找到）。搜索顺序：工程 native → 应用目录 → System32 →
    /// PATH → Program Files 厂商子目录。
    /// </summary>
    private static string? FindDllPath(string dllName)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string canRoot = Path.Combine(baseDir, "native", "can");
        string sys32   = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string sysWow  = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);

        var dirs = new List<string>
        {
            baseDir,
            Path.Combine(baseDir, "x64"),
            canRoot,
            Path.Combine(canRoot, "peak"),
            Path.Combine(canRoot, "controlcan"),
            Path.Combine(canRoot, "zlgcan"),
            Path.Combine(canRoot, "toomoss"),
            sys32,
            sysWow,
        };

        // PATH 环境变量
        string? envPath = Environment.GetEnvironmentVariable("PATH");
        if (envPath != null)
        {
            foreach (string p in envPath.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(p))
                {
                    dirs.Add(p);
                }
            }
        }

        // Program Files：扫描厂商关键字目录（最多展开两层，避免过慢）
        string[] pfRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];
        // 匹配常见厂商目录名（ZLG/ZHIYUAN 系列、PCAN、Kvaser、Toomoss 等）
        string[] vendorKeywords = ["ZHIYUAN", "ZLG", "ZCAN", "USBCAN", "PEAK", "PCAN", "Kvaser", "Toomoss", "ControlCAN", "iTEKON", "ITEKON", "ECANVCI"];
        foreach (string pf in pfRoots)
        {
            if (!Directory.Exists(pf))
            {
                continue;
            }

            try
            {
                foreach (string sub1 in Directory.GetDirectories(pf))
                {
                    string n1 = Path.GetFileName(sub1);
                    bool match = vendorKeywords.Any(kw =>
                        n1.Contains(kw, StringComparison.OrdinalIgnoreCase));
                    if (!match)
                    {
                        continue;
                    }

                    dirs.Add(sub1);
                    try
                    {
                        foreach (string sub2 in Directory.GetDirectories(sub1))
                        {
                            dirs.Add(sub2);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        foreach (string dir in dirs)
        {
            if (string.IsNullOrEmpty(dir))
            {
                continue;
            }

            try
            {
                string full = Path.Combine(dir, dllName);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch { }
        }

        // iTEKON / 其他 ControlCAN 兼容厂家：ECANVCI.dll、ECanVci64.dll 等。
        // 当请求的是 ControlCAN.dll 时，再次按这些等价名查一遍 — 一旦找到，
        // CanNativeResolver 会在实际 P/Invoke 时把 "ControlCAN.dll" 重定向到此文件。
        if (string.Equals(dllName, ControlCanNative.Dll, StringComparison.OrdinalIgnoreCase))
        {
            string[] aliases = ["ECANVCI.dll", "ECanVci64.dll", "ECanVci.dll", "ECANVci64.dll", "iCanX64.dll"];
            foreach (string alias in aliases)
            {
                foreach (string dir in dirs)
                {
                    if (string.IsNullOrEmpty(dir))
                    {
                        continue;
                    }

                    try
                    {
                        string full = Path.Combine(dir, alias);
                        if (File.Exists(full))
                        {
                            return full;
                        }
                    }
                    catch { }
                }
            }
        }
        return null;
    }

    /// <summary>将目录前置插入进程 PATH（已存在则跳过），同时通过
    /// <c>AddDllDirectory</c> 注册到 <c>LOAD_LIBRARY_SEARCH_USER_DIRS</c> 集合 ——
    /// zlgcan.dll 等 SDK 内部加载后端 DLL 时会使用该集合而非 PATH。</summary>
    private static void PrependDirToPath(string dir)
    {
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        string? current = Environment.GetEnvironmentVariable("PATH") ?? "";
        bool inPath = false;
        foreach (string p in current.Split(Path.PathSeparator))
        {
            if (string.Equals(p.TrimEnd('\\', '/'), dir.TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase)) { inPath = true; break; }
        }

        if (!inPath)
        {
            Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + current);
        }

        try { _ = AddDllDirectory(dir); } catch { /* 旧系统不支持时忽略 */ }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    private static string[] SafeGetSerialPorts()
    {
        try { return SerialPort.GetPortNames(); }
        catch { return []; }
    }
}
