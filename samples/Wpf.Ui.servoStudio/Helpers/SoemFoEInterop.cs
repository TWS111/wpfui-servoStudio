// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;

namespace Wpf.Ui.servoStudio.Helpers;

/// <summary>
/// 独立 SOEM FoE (File over EtherCAT) 封装。
/// <para>
/// 使用独立编译的 <c>soemfoe.dll</c>（基于开源 SOEM 编译，包含 FoE 模块）。
/// 该 DLL 与 Leal 的 <c>lealsoem.dll</c> 完全独立，拥有自己的 SOEM 上下文。
/// </para>
/// <para>
/// 使用流程: 先暂停 Leal 主站 → 用 soemfoe.dll 独立上下文执行 FoE → 恢复 Leal 主站。
/// 两个库对同一网卡的独占访问通过时序隔离解决。
/// </para>
/// <para>
/// 构建 soemfoe.dll: 运行项目根目录下的 <c>build_soemfoe.ps1</c> 脚本。
/// </para>
/// </summary>
public static class SoemFoEInterop
{
    private const string DllName = "soemfoe";

    /// <summary>FoE 默认超时 (微秒)</summary>
    public const int EC_TIMEOUTFOE = 3_000_000;

    /// <summary>状态检查超时 (微秒)</summary>
    public const int EC_TIMEOUTSTATE = 2_000_000;

    #region P/Invoke — 标准 SOEM 函数

    [DllImport(DllName, EntryPoint = "ecx_init", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ecx_init(IntPtr context, [MarshalAs(UnmanagedType.LPStr)] string ifname);

    [DllImport(DllName, EntryPoint = "ecx_config_init", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ecx_config_init(IntPtr context, byte usetable);

    [DllImport(DllName, EntryPoint = "ecx_close", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ecx_close(IntPtr context);

    [DllImport(DllName, EntryPoint = "ecx_statecheck", CallingConvention = CallingConvention.Cdecl)]
    private static extern ushort ecx_statecheck(IntPtr context, ushort slave, ushort reqstate, int timeout);

    [DllImport(DllName, EntryPoint = "ecx_writestate", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ecx_writestate(IntPtr context, ushort slave);

    [DllImport(DllName, EntryPoint = "ecx_FOEwrite", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ecx_FOEwrite(IntPtr context, ushort slave, [MarshalAs(UnmanagedType.LPStr)] string filename, uint password, int psize, byte[] p, int timeout);

    [DllImport(DllName, EntryPoint = "ecx_FOEread", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ecx_FOEread(IntPtr context, ushort slave, [MarshalAs(UnmanagedType.LPStr)] string filename, uint password, ref int psize, byte[] p, int timeout);

    // SOEM 上下文分配/释放 — 使用 ecx_context 完整结构
    [DllImport(DllName, EntryPoint = "soemfoe_alloc_context", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr soemfoe_alloc_context();

    [DllImport(DllName, EntryPoint = "soemfoe_free_context", CallingConvention = CallingConvention.Cdecl)]
    private static extern void soemfoe_free_context(IntPtr context);

    // 设置从站状态字段
    [DllImport(DllName, EntryPoint = "soemfoe_set_slave_state", CallingConvention = CallingConvention.Cdecl)]
    private static extern void soemfoe_set_slave_state(IntPtr context, ushort slave, ushort state);

    // 配置 Bootstrap 邮箱 — 读 EEPROM + 更新 SM0/SM1 + 写入硬件
    [DllImport(DllName, EntryPoint = "soemfoe_config_boot_mailbox", CallingConvention = CallingConvention.Cdecl)]
    private static extern int soemfoe_config_boot_mailbox(IntPtr context, ushort slave);

    // 重新写入 SM0/SM1 到从站硬件 (BOOT 确认后调用)
    [DllImport(DllName, EntryPoint = "soemfoe_rewrite_boot_sm", CallingConvention = CallingConvention.Cdecl)]
    private static extern int soemfoe_rewrite_boot_sm(IntPtr context, ushort slave);

    // 刷新邮箱残留数据并重置计数器 (BOOT 确认后、FOEwrite 之前调用)
    [DllImport(DllName, EntryPoint = "soemfoe_flush_mbx", CallingConvention = CallingConvention.Cdecl)]
    private static extern int soemfoe_flush_mbx(IntPtr context, ushort slave);

    // 获取当前邮箱配置 (诊断用)
    [DllImport(DllName, EntryPoint = "soemfoe_get_mbx_info", CallingConvention = CallingConvention.Cdecl)]
    private static extern void soemfoe_get_mbx_info(IntPtr context, ushort slave,
        out ushort mbx_wo, out ushort mbx_l, out ushort mbx_ro, out ushort mbx_rl,
        out ushort sm0_addr, out ushort sm0_len, out ushort sm1_addr, out ushort sm1_len);

    // 写入从站 EEPROM (SII) — FPWR 路径 (依赖配置站地址)
    [DllImport(DllName, EntryPoint = "soemfoe_write_eeprom", CallingConvention = CallingConvention.Cdecl)]
    private static extern int soemfoe_write_eeprom(IntPtr context, ushort slave, ushort start, ushort[] data, int length);

    // 读取从站 EEPROM (SII) — FPRD 路径
    [DllImport(DllName, EntryPoint = "soemfoe_read_eeprom", CallingConvention = CallingConvention.Cdecl)]
    private static extern int soemfoe_read_eeprom(IntPtr context, ushort slave, ushort start, [Out] ushort[] data, int length);

    // 写入从站 EEPROM (SII) — APWR 路径 (自动递增寻址，不依赖站地址；SII 损坏时仍可用)
    [DllImport(DllName, EntryPoint = "soemfoe_write_eeprom_ap", CallingConvention = CallingConvention.Cdecl)]
    private static extern int soemfoe_write_eeprom_ap(IntPtr context, ushort slave, ushort start, ushort[] data, int length);

    // 读取从站 EEPROM (SII) — APRD 路径
    [DllImport(DllName, EntryPoint = "soemfoe_read_eeprom_ap", CallingConvention = CallingConvention.Cdecl)]
    private static extern int soemfoe_read_eeprom_ap(IntPtr context, ushort slave, ushort start, [Out] ushort[] data, int length);

    // 触发从站重新加载 EEPROM (SII 重写后用于让 ESC 重新锁存 Identity/SM/邮箱等)
    [DllImport(DllName, EntryPoint = "soemfoe_reload_eeprom_ap", CallingConvention = CallingConvention.Cdecl)]
    private static extern int soemfoe_reload_eeprom_ap(IntPtr context, ushort slave);

    // 校验 Reload 后 EEPROM 中的 Identity (Vendor/Product) 是否与预期一致 (诊断用)
    [DllImport(DllName, EntryPoint = "soemfoe_verify_identity_reloaded_ap", CallingConvention = CallingConvention.Cdecl)]
    private static extern int soemfoe_verify_identity_reloaded_ap(IntPtr context, ushort slave, uint expectedVendor, uint expectedProduct);

    // 弹出最后一条错误并返回可读字符串
    [DllImport(DllName, EntryPoint = "soemfoe_pop_error_string", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr soemfoe_pop_error_string(IntPtr context);

    // 完整 FoE 写入 — 单次调用，匹配 SOEM firm_update.c 标准流程
    [DllImport(DllName, EntryPoint = "soemfoe_foe_write_full", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    private static extern int soemfoe_foe_write_full(
        [MarshalAs(UnmanagedType.LPStr)] string ifname,
        ushort slave,
        [MarshalAs(UnmanagedType.LPStr)] string filename,
        uint password,
        byte[] data,
        int datasize,
        int timeout,
        byte[] diag_buf,
        int diag_buf_size);

    #endregion

    #region EtherCAT 从站状态常量

    private const ushort EC_STATE_INIT = 0x01;
    private const ushort EC_STATE_PRE_OP = 0x02;
    private const ushort EC_STATE_BOOT = 0x03;

    #endregion

    #region SOEM FoE 错误码常量

    private const int EC_ERR_TYPE_PACKET_ERROR = 3;
    private const int EC_ERR_TYPE_FOE_ERROR = 5;
    private const int EC_ERR_TYPE_FOE_BUF2SMALL = 6;
    private const int EC_ERR_TYPE_FOE_PACKETNUMBER = 7;
    private const int EC_ERR_TYPE_FOE_FILE_NOTFOUND = 10;

    #endregion

    #region 运行时检测

    private static bool? _isFoEAvailable;

    /// <summary>
    /// 检测 soemfoe.dll 是否可用。
    /// </summary>
    public static bool IsFoEAvailable
    {
        get
        {
            if (_isFoEAvailable.HasValue)
                return _isFoEAvailable.Value;

            _isFoEAvailable = CheckFoEAvailable();
            return _isFoEAvailable.Value;
        }
    }

    /// <summary>
    /// 重置可用性缓存，下次访问 IsFoEAvailable 时重新检测。
    /// </summary>
    public static void ResetAvailabilityCache() => _isFoEAvailable = null;

    private static bool CheckFoEAvailable()
    {
        try
        {
            // 检查 soemfoe.dll 是否存在于应用目录
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string dllPath = Path.Combine(appDir, "soemfoe.dll");
            if (!File.Exists(dllPath))
                return false;

            if (!NativeLibrary.TryLoad(dllPath, out IntPtr handle))
                return false;

            bool hasFoE = NativeLibrary.TryGetExport(handle, "ecx_FOEwrite", out _)
                       && NativeLibrary.TryGetExport(handle, "soemfoe_alloc_context", out _);

            NativeLibrary.Free(handle);
            return hasFoE;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region 高级 API

    /// <summary>
    /// FoE 写入结果。
    /// </summary>
    public record FoEResult(bool Success, int WorkCounter, string? ErrorMessage);

    /// <summary>
    /// 使用独立 SOEM 上下文执行 FoE 写入。
    /// <para>
    /// 调用前必须已暂停 Leal 主站（<c>StopActivity()</c>），
    /// 调用后需恢复 Leal 主站（<c>StartActivity()</c>）。
    /// </para>
    /// </summary>
    /// <param name="interfaceName">网卡接口名（与 Leal 使用的相同）</param>
    /// <param name="slaveAddr">从站地址 (1-based)</param>
    /// <param name="filename">FoE 文件名</param>
    /// <param name="data">文件数据</param>
    /// <param name="password">FoE 密码 (默认 0)</param>
    /// <param name="timeout">FoE 超时微秒</param>
    /// <param name="onProgress">进度回调 (已传输字节, 总字节)</param>
    /// <returns>FoE 写入结果</returns>
    public static FoEResult ExecuteFoEWrite(
        string interfaceName,
        int slaveAddr,
        string filename,
        byte[] data,
        uint password = 0,
        int timeout = EC_TIMEOUTFOE,
        Action<string>? onLog = null)
    {
        if (!IsFoEAvailable)
            return new FoEResult(false, 0, "soemfoe.dll 不可用，请先编译并部署该 DLL");

        try
        {
            // 将 Windows 网卡名转换为 pcap 设备名
            string pcapName = ConvertToPcapDeviceName(interfaceName);
            onLog?.Invoke($"网卡映射: {interfaceName} -> {pcapName}");
            onLog?.Invoke($"调用 soemfoe_foe_write_full (匹配 SOEM firm_update.c 标准流程)...");

            // 使用 4KB 诊断缓冲区
            byte[] diagBuf = new byte[4096];

            // 单次 C 调用完成全部 FoE 流程 — 无多次 P/Invoke 往返
            int wkc = soemfoe_foe_write_full(
                pcapName,
                (ushort)slaveAddr,
                filename,
                password,
                data,
                data.Length,
                timeout,
                diagBuf,
                diagBuf.Length);

            // 解析诊断输出
            string diagText = System.Text.Encoding.UTF8.GetString(diagBuf).TrimEnd('\0');
            if (!string.IsNullOrWhiteSpace(diagText))
            {
                foreach (string line in diagText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    onLog?.Invoke($"  [SOEM] {line}");
            }

            if (wkc > 0)
            {
                onLog?.Invoke($"✓ FoE 写入成功 (wkc={wkc})");
                return new FoEResult(true, wkc, null);
            }
            else
            {
                string errorDetail = GetFoEErrorDescription(wkc);
                string fullMsg = $"ecx_FOEwrite 返回 wkc={wkc} ({errorDetail})";
                if (!string.IsNullOrWhiteSpace(diagText))
                    fullMsg += $"\n诊断:\n{diagText}";

                onLog?.Invoke($"✗ FoE 写入失败: wkc={wkc} ({errorDetail})");
                return new FoEResult(false, wkc, fullMsg);
            }
        }
        catch (Exception ex)
        {
            return new FoEResult(false, 0, $"FoE 异常: {ex.Message}");
        }
    }

    /// <summary>
    /// EEPROM 写入结果。
    /// </summary>
    /// <param name="Success">整体是否成功</param>
    /// <param name="WordsWritten">成功写入的 word 数</param>
    /// <param name="ErrorMessage">失败原因 (成功时为 null)</param>
    /// <param name="RolledBack">失败后是否成功回滚到备份</param>
    public record EepromResult(bool Success, int WordsWritten, string? ErrorMessage, bool RolledBack = false);

    /// <summary>
    /// 使用独立 SOEM 上下文将 SII 数据写入从站 EEPROM，采用"备份 + AP 写入 + 回读校验 + 失败回滚"的安全流程。
    /// <para>
    /// ★ 为何采用 AP (自动递增) 寻址而非 FP (配置站地址)：
    /// 如果之前写入过损坏的 SII，从站的 Identity / Station Alias 可能被破坏，ESC 无法分配有效的配置站地址，
    /// 此时 FPWR 路径会失败，必须依赖外部工具 (如 TwinCAT) 才能救回。APWR 只依赖物理拓扑顺序，
    /// 对损坏的 SII 依然可用，保证"只要没有拔线断电，总能自己救回来"。
    /// </para>
    /// <para>
    /// ★ 流程：
    ///  1. 打开独立 SOEM 上下文并扫描从站；
    ///  2. 切换从站至 INIT；
    ///  3. <b>读取现有 EEPROM 作为备份</b>（失败回滚用）；
    ///  4. 分批 APWR 写入新 SII；
    ///  5. 回读所有已写区域并与源数据比对；
    ///  6. 任何步骤失败：用备份数据重写，尽力恢复到初始状态；
    ///  7. 触发 EEPROM 重新加载命令，让 ESC 立即锁存新配置。
    /// </para>
    /// 调用前必须已暂停 Leal 主站，调用后需恢复。
    /// </summary>
    public static EepromResult ExecuteEepromWrite(
        string interfaceName,
        int slaveAddr,
        byte[] siiData,
        Action<string>? onLog = null,
        Action<int, int>? onProgress = null)
    {
        if (!IsFoEAvailable)
            return new EepromResult(false, 0, "soemfoe.dll 不可用");

        if (siiData == null || siiData.Length < 128)
            return new EepromResult(false, 0, "SII 数据长度不足 (< 128 字节)，拒绝写入");

        IntPtr ctx = IntPtr.Zero;

        try
        {
            ctx = soemfoe_alloc_context();
            if (ctx == IntPtr.Zero)
                return new EepromResult(false, 0, "无法分配 SOEM 上下文");

            onLog?.Invoke("正在初始化独立 SOEM 上下文...");
            string pcapName = ConvertToPcapDeviceName(interfaceName);
            onLog?.Invoke($"网卡映射: {interfaceName} -> {pcapName}");
            int initResult = ecx_init(ctx, pcapName);
            if (initResult <= 0)
                return new EepromResult(false, 0, $"ecx_init 失败 (返回 {initResult})");

            onLog?.Invoke("正在扫描从站...");
            int slaveCount = ecx_config_init(ctx, 0);
            if (slaveCount <= 0)
                return new EepromResult(false, 0, $"未检测到从站 (返回 {slaveCount})");

            onLog?.Invoke($"检测到 {slaveCount} 个从站");

            ushort slave = (ushort)slaveAddr;
            if (slave == 0 || slave > slaveCount)
                return new EepromResult(false, 0, $"从站地址 {slave} 超出 1..{slaveCount} 范围");

            // 切换到 INIT — EEPROM 写入的先决条件
            onLog?.Invoke("切换从站至 INIT...");
            soemfoe_set_slave_state(ctx, slave, EC_STATE_INIT);
            _ = ecx_writestate(ctx, slave);
            ushort actualState = ecx_statecheck(ctx, slave, EC_STATE_INIT, EC_TIMEOUTSTATE);
            if ((actualState & 0x0F) != EC_STATE_INIT)
                onLog?.Invoke($"⚠ INIT 状态检查: 0x{actualState:X4} (继续尝试)");

            // 字节 → word (little-endian)
            int wordCount = (siiData.Length + 1) / 2;
            ushort[] words = new ushort[wordCount];
            for (int i = 0; i < wordCount; i++)
            {
                int lo = siiData[i * 2];
                int hi = (i * 2 + 1 < siiData.Length) ? siiData[i * 2 + 1] : 0;
                words[i] = (ushort)(lo | (hi << 8));
            }

            // ── 1) 备份现有 EEPROM (AP) ──
            onLog?.Invoke($"正在备份从站原始 EEPROM ({wordCount} words) ...");
            ushort[] backup = new ushort[wordCount];
            int readCount = soemfoe_read_eeprom_ap(ctx, slave, 0, backup, wordCount);
            bool backupOk = readCount == wordCount;
            if (!backupOk)
            {
                onLog?.Invoke($"⚠ 备份不完整 (读取 {readCount}/{wordCount} words) — 发生写入失败时将无法自动回滚");
            }
            else
            {
                onLog?.Invoke("✓ 原始 EEPROM 备份完成");
            }

            // ── 2) APWR 分批写入 ──
            // 底层 soemfoe_write_eeprom_ap 已升级为"逐字写入 + 等待 EEPROM 物理写周期
            // (~5ms) + 立即单字回读校验 + 自动重试"，所以这里的 batchSize 仅用于进度
            // 报告粒度，不影响数据安全。约 6 ms/word × 338 word ≈ 2 秒。
            onLog?.Invoke($"开始写入 EEPROM: {wordCount} words ({siiData.Length} 字节, APWR 模式, 逐字校验)...");
            const int batchSize = 16;
            int totalWritten = 0;
            string? writeError = null;

            for (int offset = 0; offset < wordCount; offset += batchSize)
            {
                int remaining = Math.Min(batchSize, wordCount - offset);
                ushort[] batch = new ushort[remaining];
                Array.Copy(words, offset, batch, 0, remaining);

                int written = soemfoe_write_eeprom_ap(ctx, slave, (ushort)offset, batch, remaining);
                if (written < remaining)
                {
                    totalWritten += Math.Max(0, written);
                    string errMsg = PopErrorString(ctx);
                    writeError = $"EEPROM 写入在 word 0x{offset + Math.Max(0, written):X4} 处失败";
                    if (!string.IsNullOrEmpty(errMsg))
                        writeError += $" SOEM: {errMsg}";
                    onLog?.Invoke($"✗ {writeError}");
                    break;
                }

                totalWritten += remaining;
                onProgress?.Invoke(totalWritten, wordCount);
            }

            // ── 3) 若写入失败 → 尝试回滚 ──
            if (writeError != null)
            {
                if (backupOk)
                {
                    onLog?.Invoke("正在尝试使用备份回滚 EEPROM...");
                    int restored = soemfoe_write_eeprom_ap(ctx, slave, 0, backup, wordCount);
                    bool rollbackOk = restored == wordCount;
                    if (rollbackOk)
                    {
                        onLog?.Invoke("✓ 回滚成功，从站 EEPROM 已恢复至写入前状态");
                        try { _ = soemfoe_reload_eeprom_ap(ctx, slave); } catch { }

                        return new EepromResult(false, totalWritten,
                            writeError + "\n已回滚到原始 EEPROM，从站应可继续使用。", RolledBack: true);
                    }
                    else
                    {
                        onLog?.Invoke($"✗ 回滚失败 (恢复 {restored}/{wordCount} words)，从站 SII 处于不一致状态");
                        return new EepromResult(false, totalWritten,
                            writeError + $"\n回滚失败 (仅恢复 {restored}/{wordCount} words)，建议立即重试或使用外部工具。",
                            RolledBack: false);
                    }
                }
                else
                {
                    return new EepromResult(false, totalWritten,
                        writeError + "\n由于之前备份不完整，无法自动回滚。", RolledBack: false);
                }
            }

            // ── 4) 最终批量回读校验 ──
            //
            // 底层 soemfoe_write_eeprom_ap 已经对每个 word 完成"写后等待 5ms +
            // 单字回读校验 + 失败重试"，到这里所有 word 在 ESC EEPROM 控制器视角
            // 下已确认写入。本步骤是"第二道防线"——再做一次整体回读对照，并对极
            // 少数仍不一致的 word 进行额外的逐字重写重试。
            //
            // 让 EEPROM 在最后一次写入后完全落盘 (典型 AT24Cxx tWR ≤ 5ms，留余量)。
            Thread.Sleep(100);

            onLog?.Invoke("正在回读校验 EEPROM...");
            ushort[] verify = new ushort[wordCount];
            int verifyRead = soemfoe_read_eeprom_ap(ctx, slave, 0, verify, wordCount);
            if (verifyRead != wordCount)
            {
                onLog?.Invoke($"⚠ 回读不完整 ({verifyRead}/{wordCount})，无法完全校验");
            }
            else
            {
                List<int> mismatches = new();
                for (int i = 0; i < wordCount; i++)
                {
                    if (verify[i] != words[i])
                        mismatches.Add(i);
                }

                const int maxRetryRounds = 3;
                int retryRound = 0;
                while (mismatches.Count > 0 && retryRound < maxRetryRounds)
                {
                    retryRound++;
                    onLog?.Invoke($"⚠ 检测到 {mismatches.Count} 个 word 校验失败，逐字重写 (第 {retryRound}/{maxRetryRounds} 轮)");
                    if (mismatches.Count <= 8)
                    {
                        foreach (int idx in mismatches)
                            onLog?.Invoke($"   word 0x{idx:X4}: 期望 0x{words[idx]:X4} 实际 0x{verify[idx]:X4}");
                    }

                    List<int> stillBad = new();
                    foreach (int idx in mismatches)
                    {
                        ushort[] one = new ushort[] { words[idx] };
                        // 底层已包含 per-word 重试与延时，此处只需再调用一次。
                        int w = soemfoe_write_eeprom_ap(ctx, slave, (ushort)idx, one, 1);
                        if (w != 1)
                        {
                            stillBad.Add(idx);
                            continue;
                        }

                        Thread.Sleep(10);
                        ushort[] back = new ushort[1];
                        int r = soemfoe_read_eeprom_ap(ctx, slave, (ushort)idx, back, 1);
                        if (r != 1 || back[0] != words[idx])
                        {
                            stillBad.Add(idx);
                            verify[idx] = (r == 1) ? back[0] : verify[idx];
                        }
                        else
                        {
                            verify[idx] = back[0];
                        }
                    }

                    mismatches = stillBad;
                }

                if (mismatches.Count > 0)
                {
                    int firstBad = mismatches[0];
                    string verifyErr = $"EEPROM 回读校验失败：word 0x{firstBad:X4} 期望 0x{words[firstBad]:X4} 实际 0x{verify[firstBad]:X4}"
                        + (mismatches.Count > 1 ? $" (共 {mismatches.Count} 字异常，已重试 {maxRetryRounds} 轮)" : $" (已重试 {maxRetryRounds} 轮)");
                    onLog?.Invoke($"✗ {verifyErr}");

                    if (backupOk)
                    {
                        onLog?.Invoke("正在使用备份回滚 EEPROM...");
                        int restored = soemfoe_write_eeprom_ap(ctx, slave, 0, backup, wordCount);
                        if (restored == wordCount)
                        {
                            try { _ = soemfoe_reload_eeprom_ap(ctx, slave); } catch { }

                            onLog?.Invoke("✓ 已回滚到原始 EEPROM");
                            return new EepromResult(false, totalWritten,
                                verifyErr + "\n已回滚到原始 EEPROM。", RolledBack: true);
                        }
                    }

                    return new EepromResult(false, totalWritten, verifyErr, RolledBack: false);
                }

                if (retryRound > 0)
                    onLog?.Invoke($"✓ EEPROM 回读校验通过 (经过 {retryRound} 轮单字重试)");
                else
                    onLog?.Invoke("✓ EEPROM 回读校验通过");
            }

            // ── 5) 触发 ESC 重新加载 SII 配置区 ──
            //
            // ★ 关键步骤：仅把 EEPROM 字节写对是不够的。
            //   ESC 内部的镜像寄存器 (Identity 0x0008..0x000F、Station Alias、
            //   邮箱/SM 默认值等) 只在以下情况会从 EEPROM 重读：
            //     a) 物理上电；
            //     b) 主站显式发送 EEPROM "Reload" 命令到寄存器 0x0502 (bit 2)。
            //   不做 Reload，主站重连时仍读到旧 Identity → 不匹配 → 卡 INIT、
            //   长时间重试，与 TwinCAT 烧录后正常表现差异巨大。
            //   soemfoe_reload_eeprom_ap 已实现为符合 ETG.1000.4 的 Reload 命令。
            //
            // 提取期望 Identity 用于回读校验 (EEPROM word 0x0008..0x000B)
            uint expectedVendor = 0;
            uint expectedProduct = 0;
            if (wordCount >= 12)
            {
                expectedVendor = (uint)words[0x08] | ((uint)words[0x09] << 16);
                expectedProduct = (uint)words[0x0A] | ((uint)words[0x0B] << 16);
            }

            bool reloadOk = false;
            try
            {
                reloadOk = soemfoe_reload_eeprom_ap(ctx, slave) > 0;
                if (reloadOk)
                    onLog?.Invoke("✓ 已发送 EEPROM Reload 命令 (ESC 配置区刷新)");
                else
                    onLog?.Invoke("⚠ EEPROM Reload 命令返回失败 — 从站可能需要重新上电才能生效");
            }
            catch (Exception ex)
            {
                onLog?.Invoke($"⚠ 触发 ESC Reload 时异常: {ex.Message}");
            }

            // 校验 Reload 后从 EEPROM 读出的 Identity 与写入数据一致 (这能确认 EEPROM
            // 现在归 master 控制且数据稳定；ESC 镜像寄存器是否同步刷新需主站重扫描验证)
            if (reloadOk && expectedVendor != 0 && wordCount >= 12)
            {
                try
                {
                    bool idOk = soemfoe_verify_identity_reloaded_ap(ctx, slave, expectedVendor, expectedProduct) > 0;
                    if (idOk)
                        onLog?.Invoke($"✓ Identity 校验通过 (Vendor=0x{expectedVendor:X8} Product=0x{expectedProduct:X8})");
                    else
                        onLog?.Invoke("⚠ Reload 后 Identity 回读不一致 — 可能需要重新上电");
                }
                catch { /* 诊断步骤失败不视为致命 */ }
            }

            onLog?.Invoke($"✓ EEPROM 写入成功 ({totalWritten} words)"
                + (reloadOk ? "" : "\n⚠ 建议手动给从站断电重启以使新 SII 生效"));
            return new EepromResult(true, totalWritten, null);
        }
        catch (Exception ex)
        {
            return new EepromResult(false, 0, $"EEPROM 写入异常: {ex.Message}");
        }
        finally
        {
            if (ctx != IntPtr.Zero)
            {
                try { ecx_close(ctx); } catch { }

                try { soemfoe_free_context(ctx); } catch { }
            }
        }
    }

    /// <summary>
    /// 将 Windows 网卡名称（如 "以太网 2"）转换为 pcap 设备名（如 "\Device\NPF_{GUID}"）。
    /// SOEM 的 ecx_init → pcap_open 需要后者格式，而 Leal 的 StartActivity 内部会自动转换。
    /// </summary>
    private static string ConvertToPcapDeviceName(string windowsAdapterName)
    {
        // 如果已经是 pcap 格式，直接返回
        if (windowsAdapterName.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase)
            || windowsAdapterName.StartsWith("rpcap://", StringComparison.OrdinalIgnoreCase))
        {
            return windowsAdapterName;
        }

        // 通过 .NET NetworkInterface 查找匹配的网卡，获取其 GUID (Id)
        NetworkInterface? nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.Name.Equals(windowsAdapterName, StringComparison.OrdinalIgnoreCase));

        if (nic != null)
        {
            // NetworkInterface.Id 返回形如 "{B1234567-890A-BCDE-F012-34567890ABCD}" 的 GUID
            string id = nic.Id;
            if (!id.StartsWith('{'))
                id = "{" + id + "}";

            return $"\\Device\\NPF_{id}";
        }

        // 回退: 原样返回，让 pcap_open 自行尝试
        return windowsAdapterName;
    }

    /// <summary>
    /// 读取 SOEM 错误栈中的最后一条错误描述。
    /// </summary>
    private static string PopErrorString(IntPtr ctx)
    {
        try
        {
            IntPtr ptr = soemfoe_pop_error_string(ctx);
            if (ptr != IntPtr.Zero)
            {
                string? s = Marshal.PtrToStringAnsi(ptr);
                if (!string.IsNullOrEmpty(s))
                    return s;
            }
        }
        catch { }

        return string.Empty;
    }

    /// <summary>
    /// 将 SOEM ecx_FOEwrite 的负返回值映射为可读描述。
    /// </summary>
    private static string GetFoEErrorDescription(int wkc)
    {
        int code = -wkc; // SOEM 返回负值
        return code switch
        {
            EC_ERR_TYPE_PACKET_ERROR => "非预期邮箱包 (PACKET_ERROR)",
            EC_ERR_TYPE_FOE_ERROR => "从站返回 FoE 错误 (FOE_ERROR)",
            EC_ERR_TYPE_FOE_BUF2SMALL => "缓冲区过小 (FOE_BUF2SMALL)",
            EC_ERR_TYPE_FOE_PACKETNUMBER => "FoE 包序号不匹配 (FOE_PACKETNUMBER)",
            EC_ERR_TYPE_FOE_FILE_NOTFOUND => "从站报告文件未找到 (FOE_FILE_NOTFOUND)",
            _ when wkc == 0 => "邮箱发送失败 (无响应)",
            _ => $"未知错误码 ({wkc})",
        };
    }

    #endregion
}