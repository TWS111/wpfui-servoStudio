// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Wpf.Ui.servoStudio.Services;

/// <summary>
/// 为 CAN 适配器层注册一组 DLL 名称别名解析规则。<br/>
/// 主要目的是支持 iTEKON 等使用 <c>ECANVCI.dll</c>/<c>ECanVci64.dll</c>
/// （与 ZLG <c>ControlCAN.dll</c> 二进制兼容）的 USBCAN 设备，让现有
/// <see cref="Core.CANopen.Adapters.ControlCanBus"/> 无须改动即可枚举/打开。
/// </summary>
public static class CanNativeResolver
{
    private static bool _registered;

    /// <summary>注册解析器。重复调用是幂等的。</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;

        try
        {
            NativeLibrary.SetDllImportResolver(
                typeof(global::Core.CANopen.Adapters.ControlCanBus).Assembly,
                Resolve);
        }
        catch
        {
            // 旧框架或重复注册时回退到默认行为，不影响 ControlCAN.dll 原有路径加载。
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // 只接管 ControlCAN.dll 这一个名字 — 其他 DLL 走默认解析。
        if (!IsControlCanName(libraryName)) return IntPtr.Zero;

        // 候选 DLL 名称：先用原名，再尝试 iTEKON（及其他兼容厂家）使用的名称。
        string[] candidates =
        [
            libraryName,
            "ControlCAN.dll",
            "ECANVCI.dll",
            "ECanVci64.dll",
            "ECanVci.dll",
            "ECANVci64.dll",
            "iCanX64.dll",
        ];

        // 候选目录：工程 native 子目录 + iTEKON 安装常见位置 + 默认搜索路径。
        string baseDir = AppContext.BaseDirectory;
        string[] dirs =
        [
            Path.Combine(baseDir, "native", "can", "controlcan"),
            Path.Combine(baseDir, "native", "can", "itekon"),
            Path.Combine(baseDir, "native", "can"),
            baseDir,
        ];

        foreach (string name in candidates)
        {
            foreach (string d in dirs)
            {
                try
                {
                    string full = Path.Combine(d, name);
                    if (File.Exists(full) && NativeLibrary.TryLoad(full, out IntPtr h)) return h;
                }
                catch { }
            }
            // 让 Windows 标准搜索（PATH、System32 等）再试一遍
            if (NativeLibrary.TryLoad(name, assembly, searchPath, out IntPtr h2)) return h2;
        }
        return IntPtr.Zero;
    }

    private static bool IsControlCanName(string name)
        => string.Equals(name, "ControlCAN.dll", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "ControlCAN", StringComparison.OrdinalIgnoreCase);
}
