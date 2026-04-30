// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Wpf.Ui.servoStudio.Services;

/// <summary>
/// 厂家权限访问服务。
/// 仅在内存中保存解锁状态，不做任何持久化，应用重启后自动失效。
/// </summary>
public static class FactoryAccessService
{
    /// <summary>
    /// 厂家密码（暂定）。
    /// </summary>
    private const string FactoryPassword = "1421";

    /// <summary>
    /// 受信任的公司网络路由器 MAC 地址，连接到此网络时无需密码自动解锁。
    /// </summary>
    private const string TrustedGatewayMac = "14-d8-64-8c-a6-bc";

    /// <summary>
    /// 当前会话内是否已解锁厂家权限。
    /// </summary>
    public static bool IsUnlocked { get; private set; }

    /// <summary>
    /// 标记当前解锁是否由受信任网络自动触发（区别于手动密码解锁）。
    /// 网络监听只会撤销由网络自动产生的解锁，不影响密码解锁。
    /// </summary>
    private static bool _unlockedByTrustedNetwork;

    /// <summary>
    /// 当前解锁是否由受信任网络（公司网络）自动产生。仅在 <see cref="IsUnlocked"/> 为 true 时有意义。
    /// </summary>
    public static bool IsUnlockedByTrustedNetwork => IsUnlocked && _unlockedByTrustedNetwork;

    /// <summary>
    /// 一旦输入过错误的厂家密码，本会话内将锁定厂家模式：
    /// 既不能再以密码解锁，也不会被受信任网络自动解锁。重启应用后重置。
    /// </summary>
    public static bool IsLockedOut { get; private set; }

    /// <summary>
    /// 解锁状态发生变化时触发。
    /// </summary>
    public static event EventHandler? UnlockStateChanged;

    /// <summary>
    /// 尝试使用给定密码解锁厂家权限。
    /// 一旦输入过错误密码，本会话中将被锁定，后续任何密码（包含正确密码）都不会解锁。
    /// </summary>
    /// <returns>密码正确且未被锁定返回 true，其他情况返回 false。</returns>
    public static bool TryUnlock(string? password)
    {
        if (IsLockedOut)
        {
            return false;
        }

        if (password == FactoryPassword)
        {
            if (!IsUnlocked)
            {
                IsUnlocked = true;
                _unlockedByTrustedNetwork = false; // 密码解锁，不受网络监听管控
                UnlockStateChanged?.Invoke(null, EventArgs.Empty);
            }

            return true;
        }

        // 错误密码触发会话级锁定，同时撤销已有的解锁状态（包含由公司网络产生的解锁）。
        IsLockedOut = true;
        if (IsUnlocked)
        {
            IsUnlocked = false;
            _unlockedByTrustedNetwork = false;
            UnlockStateChanged?.Invoke(null, EventArgs.Empty);
        }

        return false;
    }

    /// <summary>
    /// 清除已解锁状态。
    /// </summary>
    public static void Lock()
    {
        if (IsUnlocked)
        {
            IsUnlocked = false;
            _unlockedByTrustedNetwork = false;
            UnlockStateChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 检测当前默认网关的 MAC 地址是否为受信任的公司网络路由器。
    /// 如果是，则无需密码自动解锁厂家权限。
    /// 如果当前会话已被锁定（输入过错误密码），则不会自动解锁。
    /// </summary>
    /// <returns>成功检测到受信任网络并完成解锁返回 true，否则返回 false。</returns>
    public static async Task<bool> TryUnlockByTrustedNetworkAsync()
    {
        if (IsLockedOut)
        {
            return false;
        }

        try
        {
            var gatewayMac = await GetDefaultGatewayMacAsync();
            if (string.Equals(gatewayMac, TrustedGatewayMac, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsUnlocked)
                {
                    IsUnlocked = true;
                    _unlockedByTrustedNetwork = true;
                    UnlockStateChanged?.Invoke(null, EventArgs.Empty);
                }
                return true;
            }
        }
        catch
        {
            // 网络检测失败不影响正常使用
        }
        return false;
    }

    /// <summary>
    /// 获取当前活动网卡的默认网关 MAC 地址（通过 ARP 缓存表）。
    /// </summary>
    private static async Task<string?> GetDefaultGatewayMacAsync()
    {
        // 收集所有活动网卡的默认网关 IP（IPv4）
        var gatewayIps = new HashSet<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            foreach (var gw in nic.GetIPProperties().GatewayAddresses)
            {
                if (gw.Address.AddressFamily == AddressFamily.InterNetwork)
                    gatewayIps.Add(gw.Address.ToString());
            }
        }

        if (gatewayIps.Count == 0)
            return null;

        // 读取系统 ARP 缓存表
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("arp", "-a")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        // 解析 ARP 表，Windows 格式示例：
        //   10.0.0.1          14-d8-64-8c-a6-bc     动态
        var macPattern = new Regex(
            @"([0-9a-f]{2}-[0-9a-f]{2}-[0-9a-f]{2}-[0-9a-f]{2}-[0-9a-f]{2}-[0-9a-f]{2})",
            RegexOptions.IgnoreCase);

        foreach (var line in output.Split('\n'))
        {
            foreach (var gwIp in gatewayIps)
            {
                if (!line.Contains(gwIp))
                    continue;
                var match = macPattern.Match(line);
                if (match.Success)
                    return match.Groups[1].Value.ToLowerInvariant();
            }
        }

        return null;
    }

    #region 网络变化监听

    private static bool _isMonitoring;

    /// <summary>
    /// 启动网络变化监听。当网络连接状态改变时自动重新检测受信任网络并相应地解锁或锁定。
    /// 仅影响由受信任网络自动产生的解锁，密码解锁不受此监听管控。
    /// 应在应用启动时调用一次。
    /// </summary>
    public static void StartNetworkMonitoring()
    {
        if (_isMonitoring)
            return;
        _isMonitoring = true;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    /// <summary>
    /// 停止网络变化监听。应在应用退出时调用。
    /// </summary>
    public static void StopNetworkMonitoring()
    {
        if (!_isMonitoring)
            return;
        _isMonitoring = false;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
    }

    /// <summary>
    /// 处理网络整体可用性变化（如网线拔出、所有网络断开）。
    /// 若已无任何网络连接，撤销由受信任网络产生的自动解锁。
    /// </summary>
    private static void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable && _unlockedByTrustedNetwork)
        {
            Lock();
        }
        else if (e.IsAvailable)
        {
            _ = TryUnlockByTrustedNetworkAsync();
        }
    }

    /// <summary>
    /// 处理网络地址变化（如切换 Wi-Fi、DHCP 重新分配、接入不同路由器）。
    /// 重新检测受信任网络；若不再匹配且当前为网络自动解锁，则撤销解锁。
    /// </summary>
    private static async void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        // 等待系统完成网络配置（ARP 表刷新需要短暂延迟）
        await Task.Delay(1500);

        var trusted = await TryUnlockByTrustedNetworkAsync();
        if (!trusted && _unlockedByTrustedNetwork)
        {
            Lock();
        }
    }

    #endregion
}