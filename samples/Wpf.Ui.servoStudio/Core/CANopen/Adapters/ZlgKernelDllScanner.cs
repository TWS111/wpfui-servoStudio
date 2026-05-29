// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Wpf.Ui.servoStudio.Services;

namespace Core.CANopen.Adapters;

/// <summary>
/// 扫描 ZLG USBCAN 系列驱动安装目录，发现并记忆包含设备后端 DLL（如 USBCAN_E_64.dll）的目录，<br/>
/// 并将其加入进程 PATH，使 <c>zlgcan.dll</c> 在调用 <c>ZCAN_OpenDevice</c> 时能通过
/// <c>dll_cfg.ini</c> 找到对应后端驱动。<br/>
/// <para>
/// 背景：这些 DLL 由 ZLG 驱动安装包写入系统，不随 ZCANPRO / 本工程附带。<br/>
/// 典型安装路径：<c>%ProgramFiles(x86)%\ZHIYUAN USBCAN_(2)E_U Driver\</c><br/>
///               <c>%ProgramFiles(x86)%\ZHIYUAN USBCANFD Driver\</c>
/// </para>
/// </summary>
public static class ZlgKernelDllScanner
{
    /// <summary>
    /// <c>zlgcan.dll</c> 通过 <c>dll_cfg.ini</c> 按设备类型延迟加载的后端 DLL（x64 命名）。<br/>
    /// 这些文件由驱动安装包安装，不在本工程的 <c>kerneldlls/</c> 中。
    /// </summary>
    public static readonly string[] DeviceDlls =
    [
        "USBCAN_E_64.dll",   // type 20 USBCAN-E-U / type 21 USBCAN-2E-U
        "USBCANFD.dll",      // type 41/42/43/76~81 USBCANFD 系列
        "USBCANFD800U.dll",  // type 59 USBCANFD-800U
        "USBCAN.dll",        // type 3/4 classic USBCAN-I/II
    ];

    private static readonly object _lock = new();
    private static readonly List<string> _runtimeDirs = [];

    // ── 公共接口 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 快速同步方法：将设置中已记忆的驱动目录添加到进程 PATH。<br/>
    /// 应在 <c>ZCAN_OpenDevice</c> 首次调用前执行，以确保后端 DLL 可被加载器找到。
    /// </summary>
    public static void ApplyCachedDirs()
    {
        try
        {
            var settings = UserSettingsService.Load();
            foreach (string d in settings.ZlgKernelDllDirs)
            {
                TryRegisterDir(d);
            }
        }
        catch { /* settings 读取失败不影响功能 */ }

        lock (_lock)
        {
            foreach (string d in _runtimeDirs)
            {
                PrependDirToPath(d);
            }
        }

        // 兜底：把扫描到的目录中的后端 DLL 复制到工程 native/can/zlgcan/kerneldlls/，
        // zlgcan.dll 大多通过相对路径 "kerneldlls\xxx.dll" 加载后端，此举不受
        // PATH/AddDllDirectory 是否生效影响，是最可靠的方案。
        try { MirrorBackendDllsToAppKerneldlls(); } catch { /* 复制失败不阻断流程 */ }
    }

    /// <summary>
    /// 把已知后端 DLL 从扫描到的目录复制到工程自带 kerneldlls/ 中（如果目标不存在）。<br/>
    /// 这是 zlgcan.dll 无法定位 backend 时最稳的兜底机制。
    /// </summary>
    private static void MirrorBackendDllsToAppKerneldlls()
    {
        string baseDir = AppContext.BaseDirectory;
        string target = Path.Combine(baseDir, "native", "can", "zlgcan", "kerneldlls");
        if (!Directory.Exists(target))
        {
            try { Directory.CreateDirectory(target); } catch { return; }
        }

        // 工程已自带 USBCANFD.dll/USBCAN.dll/USBCANFD800U.dll/devices_property 等基础文件；
        // 真正常缺的是各型号专用后端：USBCAN_E_64.dll / USBCAN_4E_U_X64.dll / USBCAN_8E_U_x64.dll 等。
        string[] candidateNames =
        [
            "USBCAN_E_64.dll",
            "USBCAN_4E_U_X64.dll",
            "USBCAN_8E_U_x64.dll",
            "USBCANFD.dll",
            "USBCANFD800U.dll",
            "USBCAN.dll",
        ];

        string[] sourceDirs;
        lock (_lock)
        {
            sourceDirs = [.. _runtimeDirs];
        }

        foreach (string name in candidateNames)
        {
            string dst = Path.Combine(target, name);
            if (File.Exists(dst))
            {
                continue; // 已经有了不覆盖（避免破坏工程自带版本）
            }

            foreach (string src in sourceDirs)
            {
                string candidate = Path.Combine(src, name);
                if (!File.Exists(candidate))
                {
                    continue;
                }

                try { File.Copy(candidate, dst, overwrite: false); break; }
                catch { /* 单个失败继续下一个 */ }
            }
        }
    }

    /// <summary>当前进程内存中已生效的目录快照（线程安全）。</summary>
    public static IReadOnlyList<string> RuntimeDirs
    {
        get { lock (_lock)
            {
                return [.. _runtimeDirs];
            }
        }
    }

    /// <summary>
    /// 异步扫描含 ZLG 设备 DLL 的目录。<br/>
    /// 扫描顺序：① 已记忆路径验证 → ② 已知驱动安装路径（ZHIYUAN/ZLG 等） →
    /// ③ 所有 Program Files 厂商子目录 → ④（仅 <paramref name="fullDriveScan"/>=true）
    /// 全固定驱动器 BFS（深度 ≤4，跳过系统目录）。<br/>
    /// 每发现新目录立即持久化，无需等待全部扫描完成。
    /// </summary>
    /// <param name="progress">
    /// 进度回调：<c>(状态文本, 进度 0~1)</c>。<br/>
    /// 值为 -1 表示进度不定（仅全盘扫描中使用）。
    /// </param>
    /// <param name="fullDriveScan">是否扫描所有固定驱动器（较慢，建议仅在用户手动触发时使用）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>本次新发现的目录列表（已添加到运行时缓存并持久化）。</returns>
    public static Task<IReadOnlyList<string>> ScanAsync(
        IProgress<(string Status, double Fraction)>? progress = null,
        bool fullDriveScan = false,
        CancellationToken ct = default) =>
        Task.Run(() => ScanCore(progress, fullDriveScan, ct), ct);

    // ── 内部实现 ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> ScanCore(
        IProgress<(string, double)>? progress,
        bool fullDriveScan,
        CancellationToken ct)
    {
        var newFound = new List<string>();

        // Phase 1：验证已记忆路径（~2 ms）
        Report(progress, "正在验证已记忆路径…", 0.0);
        try
        {
            var settings = UserSettingsService.Load();
            foreach (string d in settings.ZlgKernelDllDirs)
            {
                ct.ThrowIfCancellationRequested();
                if (TryRegisterDir(d))
                {
                    newFound.Add(d);
                    PrependDirToPath(d);
                    PersistDirs();
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 忽略读取错误 */ }

        // Phase 2：已知驱动安装目录 + Program Files 厂商扫描（< 500 ms）
        Report(progress, "正在扫描驱动安装目录…", 0.05);
        var phase2Dirs = GetKnownDriverDirs().ToList();
        int ph2Total = Math.Max(1, phase2Dirs.Count);
        int ph2Done = 0;
        foreach (string dir in phase2Dirs)
        {
            ct.ThrowIfCancellationRequested();
            ph2Done++;
            if (TryRegisterDir(dir))
            {
                newFound.Add(dir);
                PrependDirToPath(dir);
                PersistDirs();
                Report(progress, $"已找到: {dir}", 0.05 + 0.35 * ph2Done / ph2Total);
            }
        }

        if (!fullDriveScan)
        {
            Report(progress,
                newFound.Count > 0
                    ? $"扫描完成，已发现 {newFound.Count} 个驱动目录"
                    : "常见安装位置未发现驱动 DLL，可尝试全盘扫描",
                1.0);
            try { MirrorBackendDllsToAppKerneldlls(); } catch { }
            return newFound;
        }

        // Phase 3：全盘 BFS（可能较慢，深度 ≤ 4）
        Report(progress, "正在进行全盘扫描，请稍候…", 0.4);
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .ToArray();

        for (int i = 0; i < drives.Length; i++)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            double driveBase = 0.4 + 0.6 * i / Math.Max(1, drives.Length);
            double driveSpan = 0.6 / Math.Max(1, drives.Length);
            ScanDrive(drives[i].RootDirectory.FullName, driveBase, driveSpan,
                      newFound, progress, ct);
        }

        Report(progress,
            newFound.Count > 0
                ? $"全盘扫描完成，共发现 {newFound.Count} 个驱动目录"
                : "全盘扫描完成，未发现驱动 DLL（请确认驱动已安装）",
            1.0);
        try { MirrorBackendDllsToAppKerneldlls(); } catch { }
        return newFound;
    }

    private static void ScanDrive(
        string root, double baseFrac, double spanFrac,
        List<string> newFound, IProgress<(string, double)>? progress, CancellationToken ct)
    {
        string[] topDirs = SafeGetDirectories(root);
        int total = Math.Max(1, topDirs.Length);
        int done = 0;
        foreach (string top in topDirs)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            done++;
            if (IsSkippableDir(top))
            {
                continue;
            }

            double frac = baseFrac + spanFrac * done / total;
            Report(progress, $"扫描: {Path.GetFileName(top)}", frac);
            BfsScan(top, 4, newFound, ct);
        }
    }

    private static void BfsScan(string dir, int depthLeft, List<string> newFound, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        if (TryRegisterDir(dir))
        {
            newFound.Add(dir);
            PrependDirToPath(dir);
            PersistDirs();
        }
        if (depthLeft <= 0)
        {
            return;
        }

        foreach (string sub in SafeGetDirectories(dir))
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (!IsSkippableDir(sub))
            {
                BfsScan(sub, depthLeft - 1, newFound, ct);
            }
        }
    }

    /// <summary>枚举高优先级的已知驱动目录（无需全盘扫描即可覆盖绝大多数用户的安装场景）。</summary>
    private static IEnumerable<string> GetKnownDriverDirs()
    {
        string[] pfRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];

        // 明确已知的 ZHIYUAN 驱动安装包目录名
        string[] knownExact =
        [
            "ZHIYUAN USBCAN_(2)E_U Driver",
            "ZHIYUAN USBCANFD Driver",
            "ZHIYUAN USBCAN 8EU Driver",
            "ZHIYUAN USBCAN 4EU Driver",
            "ZHIYUAN Electronics",
        ];

        // 关键字匹配（覆盖 ZCANPRO 及自定义路径）
        string[] vendorKeywords = ["ZHIYUAN", "ZLG", "ZCANPRO", "USBCAN", "USBCANFD", "ControlCAN"];

        foreach (string pf in pfRoots)
        {
            if (!Directory.Exists(pf))
            {
                continue;
            }

            // 精确名称优先（最快找到标准安装位置）
            foreach (string exact in knownExact)
            {
                string full = Path.Combine(pf, exact);
                if (!Directory.Exists(full))
                {
                    continue;
                }

                yield return full;
                foreach (string sub in SafeGetDirectories(full))
                {
                    yield return sub;
                }
            }

            // 关键字匹配（展开三层，覆盖 ZCANPRO 子目录等）
            foreach (string sub1 in SafeGetDirectories(pf))
            {
                string n = Path.GetFileName(sub1);
                if (!vendorKeywords.Any(kw => n.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                yield return sub1;
                foreach (string sub2 in SafeGetDirectories(sub1))
                {
                    yield return sub2;
                    foreach (string sub3 in SafeGetDirectories(sub2))
                    {
                        yield return sub3;
                    }
                }
            }
        }
    }

    private static bool ContainsAnyDeviceDll(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return false;
        }

        try
        {
            foreach (string dll in DeviceDlls)
            {
                if (File.Exists(Path.Combine(dir, dll)))
                {
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>尝试将目录加入运行时缓存。若目录含目标 DLL 且不重复则返回 true。</summary>
    private static bool TryRegisterDir(string dir)
    {
        if (string.IsNullOrEmpty(dir))
        {
            return false;
        }

        dir = dir.TrimEnd('\\', '/');
        if (!ContainsAnyDeviceDll(dir))
        {
            return false;
        }

        lock (_lock)
        {
            if (_runtimeDirs.Any(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            _runtimeDirs.Add(dir);
            return true;
        }
    }

    private static void PersistDirs()
    {
        try
        {
            var settings = UserSettingsService.Load();
            lock (_lock)
            {
                settings.ZlgKernelDllDirs.Clear();
                settings.ZlgKernelDllDirs.AddRange(_runtimeDirs);
            }
            UserSettingsService.Save(settings);
        }
        catch { }
    }

    private static void PrependDirToPath(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return;
        }

        string current = Environment.GetEnvironmentVariable("PATH") ?? "";
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

        // zlgcan.dll 内部使用 LoadLibraryEx(LOAD_LIBRARY_SEARCH_USER_DIRS) 加载
        // USBCAN_E_64.dll / USBCANFD.dll 等后端 DLL，该搜索集忽略 PATH —— 必须
        // 通过 AddDllDirectory 显式注册目录才能被搜索到。
        try { _ = AddDllDirectory(dir); } catch { /* 旧系统不支持时忽略 */ }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    private static bool IsSkippableDir(string path)
    {
        string name = Path.GetFileName(path);
        return name.Equals("Windows", StringComparison.OrdinalIgnoreCase)
            || name.Equals("System32", StringComparison.OrdinalIgnoreCase)
            || name.Equals("SysWOW64", StringComparison.OrdinalIgnoreCase)
            || name.Equals("WinSxS", StringComparison.OrdinalIgnoreCase)
            || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("$", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] SafeGetDirectories(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch { return []; }
    }

    private static void Report(IProgress<(string, double)>? p, string status, double fraction)
        => p?.Report((status, fraction));
}
