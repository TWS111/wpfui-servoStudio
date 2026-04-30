// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wpf.Ui.servoStudio.Services;

/// <summary>
/// 厂家固件仓库服务：基于公司网络共享路径的"轻量数据库"。
/// 存储格式：
///   <see cref="DatabaseRoot"/> 目录下保存 firmware-index.json（条目元数据）
///   以及 files/ 子目录（按上传时间命名的实际文件副本）。
/// </summary>
public static class FirmwareRepositoryService
{
    /// <summary>
    /// 公司网络共享数据库根路径。
    /// </summary>
    public const string DatabaseRoot = @"\\国信新能源\国信新能源\数据库";

    /// <summary>
    /// 上传与删除操作共用的验证密码（暂定）。二者始终保持一致。
    /// </summary>
    private const string OperationPassword = "7188";

    /// <summary>
    /// 验证上传/删除操作密码是否正确。
    /// </summary>
    public static bool VerifyPassword(string? password) => password == OperationPassword;

    /// <summary>
    /// 固件状态可选项（顺序与 UI 下拉框保持一致）。
    /// </summary>
    public static readonly IReadOnlyList<string> StatusOptions = new[]
    {
        "未签名",
        "仅签名",
        "未注册",
        "待测试",
        "已知问题",
        "通过测试",
        "发布",
        "归档",
        "测试用例",
        "禁用",
    };

    /// <summary>
    /// 默认状态（用于历史数据兼容或未设置时）。未签名时强制为此状态。
    /// </summary>
    public const string DefaultStatus = "未签名";

    /// <summary>
    /// 签名完成后默认迁转到的状态。
    /// </summary>
    public const string SignedOnlyStatus = "仅签名";

    /// <summary>
    /// 未注册条目（外部添加的文件 / 未带注册标记的旧条目）状态。
    /// 被强制为此值，且禁止除删除外的全部操作。
    /// </summary>
    public const string UnregisteredStatus = "未注册";

    /// <summary>
    /// 孤儿文件（仅出现在 files/ 中、未被索引引用）合成条目的 Id 前缀。
    /// </summary>
    public const string OrphanIdPrefix = "ORPHAN:";

    /// <summary>
    /// pack_ota.py 默认使用的签名校验类型（0-checksum 1-xor 2-CRC32 3-SHA1 4-SHA256 5-SM3）。
    /// 与 FoE 烧录中使用的类型保持一致。
    /// </summary>
    public const int DefaultSignType = 4;

    /// <summary>
    /// 产品类型可选项（暂用 A/B/C 占位，后续可扩展实际产品系列名）。
    /// </summary>
    public static readonly IReadOnlyList<string> ProductTypeOptions = new[]
    {
        "A",
        "B",
        "C",
    };

    /// <summary>
    /// 默认产品类型。
    /// </summary>
    public const string DefaultProductType = "A";

    private const string IndexFileName = "firmware-index.json";
    private const string FilesSubDir = "files";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 检测当前是否已连接到公司网络（即可访问数据库共享路径）。
    /// 只依赖 <see cref="NetworkInterface.GetIsNetworkAvailable"/> 会被 Hyper-V/WSL/VPN 虚拟网卡误导为 true；
    /// <see cref="Directory.Exists"/> 在 SMB 上可能因连接缓存返回陈旧结果。
    /// 这里直接对文件服务器 SMB 端口（445）做一次带超时的 TCP 探测，作为唯一判据。
    /// </summary>
    public static bool IsCompanyNetworkAvailable()
    {
        try
        {
            string? host = ExtractUncHost(DatabaseRoot);
            if (string.IsNullOrEmpty(host))
            {
                return false;
            }

            // 1秒内能建立 TCP 连接才认为公司网络可达。
            using var client = new TcpClient();
            IAsyncResult ar = client.BeginConnect(host, 445, null, null);
            bool connected = ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1), exitContext: false);
            if (!connected)
            {
                return false;
            }

            try
            {
                client.EndConnect(ar);
            }
            catch
            {
                return false;
            }

            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 从 UNC 路径中提取主机名（如 \\服务器\共享 → "服务器"）。
    /// </summary>
    private static string? ExtractUncHost(string uncPath)
    {
        if (string.IsNullOrEmpty(uncPath) || !uncPath.StartsWith(@"\\"))
        {
            return null;
        }

        string trimmed = uncPath.Substring(2);
        int slash = trimmed.IndexOfAny(new[] { '\\', '/' });
        return slash > 0 ? trimmed.Substring(0, slash) : trimmed;
    }

    /// <summary>
    /// 取得条目实际可烧录的文件在数据库 files/ 中的绝对路径。
    /// 优先返回脚本签名后的产物（<see cref="FirmwareEntry.SignedStoredFileName"/>）；
    /// 不存在时回退到原始上传文件（<see cref="FirmwareEntry.StoredFileName"/>，覆盖外部已签名场景）。
    /// 孤儿条目（Id 以 <see cref="OrphanIdPrefix"/> 开头）也会返回其 files/ 中的实际文件路径。
    /// </summary>
    public static string GetEntryFilePath(FirmwareEntry entry)
    {
        string filesDir = Path.Combine(DatabaseRoot, FilesSubDir);
        string preferred = !string.IsNullOrEmpty(entry.SignedStoredFileName)
            ? entry.SignedStoredFileName
            : entry.StoredFileName;
        return Path.Combine(filesDir, preferred);
    }

    /// <summary>
    /// 确保数据库目录结构已存在，并返回索引文件路径。
    /// </summary>
    private static string EnsureDatabase()
    {
        if (!Directory.Exists(DatabaseRoot))
        {
            _ = Directory.CreateDirectory(DatabaseRoot);
        }

        string filesDir = Path.Combine(DatabaseRoot, FilesSubDir);
        if (!Directory.Exists(filesDir))
        {
            _ = Directory.CreateDirectory(filesDir);
        }

        return Path.Combine(DatabaseRoot, IndexFileName);
    }

    /// <summary>
    /// 读取所有已上传的固件条目。
    /// 同时会扫描 files/ 子目录中未被索引引用的"孤儿文件"，合成只读的"未注册"条目返回。
    /// 索引中存在但 IsRegistered=false 的条目（旧数据或外部手工新增）也会被强制视作"未注册"。
    /// </summary>
    public static List<FirmwareEntry> LoadAll()
    {
        try
        {
            if (!IsCompanyNetworkAvailable())
            {
                return new List<FirmwareEntry>();
            }

            string indexPath = Path.Combine(DatabaseRoot, IndexFileName);
            List<FirmwareEntry> entries;
            if (File.Exists(indexPath))
            {
                string json = File.ReadAllText(indexPath);
                entries = JsonSerializer.Deserialize<List<FirmwareEntry>>(json, _jsonOptions)
                          ?? new List<FirmwareEntry>();
            }
            else
            {
                entries = new List<FirmwareEntry>();
            }

            // 索引中未带注册标记的条目（旧数据或外部手工编辑 JSON 添加的）→ 强制状态为"未注册"。
            foreach (FirmwareEntry e in entries)
            {
                if (!e.IsRegistered)
                {
                    e.Status = UnregisteredStatus;
                }
            }

            // 扫描 files/ 中未被任何索引条目引用的孤儿文件，合成只读"未注册"条目。
            string filesDir = Path.Combine(DatabaseRoot, FilesSubDir);
            if (Directory.Exists(filesDir))
            {
                var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FirmwareEntry e in entries)
                {
                    if (!string.IsNullOrEmpty(e.StoredFileName))
                    {
                        _ = referenced.Add(e.StoredFileName);
                    }
                    if (!string.IsNullOrEmpty(e.SignedStoredFileName))
                    {
                        _ = referenced.Add(e.SignedStoredFileName);
                    }
                }

                foreach (string filePath in Directory.EnumerateFiles(filesDir))
                {
                    string fileName = Path.GetFileName(filePath);
                    if (referenced.Contains(fileName))
                    {
                        continue;
                    }

                    FileInfo fi;
                    try { fi = new FileInfo(filePath); } catch { continue; }

                    entries.Add(new FirmwareEntry
                    {
                        Id = OrphanIdPrefix + fileName,
                        UploadedAt = fi.LastWriteTime,
                        UploadedBy = string.Empty,
                        UploadedFromMachine = string.Empty,
                        OriginalFileName = fileName,
                        StoredFileName = fileName,
                        FileSizeBytes = fi.Length,
                        Version = string.Empty,
                        Name = fileName,
                        ApplicableHardwareVersion = string.Empty,
                        Description = "（外部添加，未通过本程序登记）",
                        Status = UnregisteredStatus,
                        ProductType = string.Empty,
                        IsRegistered = false,
                        IsSigned = false,
                        IsExternallySigned = false,
                        SignedStoredFileName = string.Empty,
                        SignedFileSizeBytes = 0,
                        SignType = -1,
                        SignedAt = null,
                    });
                }
            }

            return entries;
        }
        catch
        {
            return new List<FirmwareEntry>();
        }
    }

    /// <summary>
    /// 把指定本地文件上传到数据库，并写入索引。
    /// </summary>
    /// <param name="signFirst">true 表示上传前先在本地运行 pack_ota.py，将原始 bin 与签名后产物同时入库。</param>
    /// <param name="markExternallySigned">true 表示该文件本身已在外部签名过，不运行脚本，条目直接记为已签名。与 <paramref name="signFirst"/> 互斥。</param>
    /// <returns>成功时返回新建的条目，失败时返回 null。</returns>
    public static async Task<FirmwareEntry?> UploadAsync(
        string sourceFilePath,
        string version,
        string name,
        string applicableHardwareVersion,
        string description,
        string status,
        string productType,
        bool signFirst = false,
        bool markExternallySigned = false)
    {
        if (signFirst && markExternallySigned)
        {
            throw new ArgumentException("signFirst 与 markExternallySigned 不能同时为 true。");
        }

        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException("源文件不存在。", sourceFilePath);
        }

        if (!IsCompanyNetworkAvailable())
        {
            throw new InvalidOperationException("未连接到公司网络，无法访问数据库共享路径。");
        }

        string indexPath = EnsureDatabase();
        string filesDir = Path.Combine(DatabaseRoot, FilesSubDir);

        string id = DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        string originalName = Path.GetFileName(sourceFilePath);
        string ext = Path.GetExtension(sourceFilePath);
        string storedName = $"{id}-{Path.GetFileNameWithoutExtension(originalName)}{ext}";
        string destPath = Path.Combine(filesDir, storedName);

        // 异步复制
        await using (FileStream src = new(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (FileStream dst = new(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await src.CopyToAsync(dst);
        }

        FileInfo fi = new(destPath);

        // 是否在上传前运行脚本签名
        string signedStoredName = string.Empty;
        long signedSize = 0;
        DateTime? signedAt = null;
        int signType = -1;
        bool isSigned = markExternallySigned;

        if (signFirst)
        {
            string signedName = $"{id}-{Path.GetFileNameWithoutExtension(originalName)}.signed.bin";
            string signedDest = Path.Combine(filesDir, signedName);

            try
            {
                await RunPackOtaAsync(destPath, signedDest, DefaultSignType).ConfigureAwait(false);
            }
            catch
            {
                // 签名失败 → 回退之前复制的原始文件，避免遗留垃圾入库
                try { if (File.Exists(destPath)) { File.Delete(destPath); } } catch { }
                try { if (File.Exists(signedDest)) { File.Delete(signedDest); } } catch { }
                throw;
            }

            signedStoredName = signedName;
            signedSize = new FileInfo(signedDest).Length;
            signedAt = DateTime.Now;
            signType = DefaultSignType;
            isSigned = true;
        }

        // 不一致状态保护：未签名时强制状态为“未签名”
        string normalizedStatus = string.IsNullOrWhiteSpace(status) ? DefaultStatus : status.Trim();
        if (!isSigned)
        {
            normalizedStatus = DefaultStatus;
        }
        else if (string.Equals(normalizedStatus, DefaultStatus, StringComparison.Ordinal))
        {
            // 已签名但传入状态仍为未签名，迁转为仅签名
            normalizedStatus = SignedOnlyStatus;
        }

        FirmwareEntry entry = new()
        {
            Id = id,
            UploadedAt = DateTime.Now,
            UploadedBy = Environment.UserName,
            UploadedFromMachine = Environment.MachineName,
            OriginalFileName = originalName,
            StoredFileName = storedName,
            FileSizeBytes = fi.Length,
            Version = version?.Trim() ?? string.Empty,
            Name = name?.Trim() ?? string.Empty,
            ApplicableHardwareVersion = applicableHardwareVersion?.Trim() ?? string.Empty,
            Description = description?.Trim() ?? string.Empty,
            Status = normalizedStatus,
            ProductType = string.IsNullOrWhiteSpace(productType) ? DefaultProductType : productType.Trim(),
            IsRegistered = true, // 通过本程序上传 → 自动打入"已注册"标记
            IsSigned = isSigned,
            IsExternallySigned = markExternallySigned,
            SignedStoredFileName = signedStoredName,
            SignedFileSizeBytes = signedSize,
            SignType = signType,
            SignedAt = signedAt,
        };

        // 仅持久化真实索引条目，孤儿条目（OrphanIdPrefix 开头）不写回 JSON
        List<FirmwareEntry> all = LoadAll()
            .Where(e => !e.Id.StartsWith(OrphanIdPrefix, StringComparison.Ordinal))
            .ToList();
        all.Add(entry);
        await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(all, _jsonOptions));

        return entry;
    }

    /// <summary>
    /// 仅更新指定条目的状态字段（不修改其它字段与实际文件）。
    /// </summary>
    /// <returns>是否成功更新。</returns>
    public static async Task<bool> UpdateStatusAsync(string id, string newStatus)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        if (!IsCompanyNetworkAvailable())
        {
            throw new InvalidOperationException("未连接到公司网络，无法访问数据库共享路径。");
        }

        string indexPath = EnsureDatabase();
        List<FirmwareEntry> all = LoadAll()
            .Where(e => !e.Id.StartsWith(OrphanIdPrefix, StringComparison.Ordinal))
            .ToList();
        FirmwareEntry? target = all.FirstOrDefault(e => e.Id == id);
        if (target is null)
        {
            return false;
        }

        // 未注册条目禁止修改状态
        if (!target.IsRegistered)
        {
            throw new InvalidOperationException("未注册的条目禁止修改状态，请先删除后重新通过本程序上传。");
        }

        target.Status = string.IsNullOrWhiteSpace(newStatus) ? DefaultStatus : newStatus.Trim();
        // 未签名时强制状态为“未签名”
        if (!target.IsSigned)
        {
            target.Status = DefaultStatus;
        }

        await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(all, _jsonOptions));
        return true;
    }

    /// <summary>
    /// 对指定条目运行 pack_ota.py 进行签名，签名后产物与原始文件同时保存在数据库中。
    /// 原状态为“未签名”的条目会被迁转为“仅签名”。已签名条目（包括外部签名）会被拒绝。
    /// </summary>
    /// <returns>是否成功。</returns>
    public static async Task<bool> SignAsync(string id, int signType = DefaultSignType)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        if (!IsCompanyNetworkAvailable())
        {
            throw new InvalidOperationException("未连接到公司网络，无法访问数据库共享路径。");
        }

        string indexPath = EnsureDatabase();
        string filesDir = Path.Combine(DatabaseRoot, FilesSubDir);

        List<FirmwareEntry> all = LoadAll()
            .Where(e => !e.Id.StartsWith(OrphanIdPrefix, StringComparison.Ordinal))
            .ToList();
        FirmwareEntry? target = all.FirstOrDefault(e => e.Id == id);
        if (target is null)
        {
            return false;
        }

        if (!target.IsRegistered)
        {
            throw new InvalidOperationException("未注册的条目禁止签名，请先删除后重新通过本程序上传。");
        }

        if (target.IsSigned)
        {
            throw new InvalidOperationException("该条目已签名，不能重复签名。");
        }

        string srcPath = Path.Combine(filesDir, target.StoredFileName);
        if (!File.Exists(srcPath))
        {
            throw new FileNotFoundException("未找到原始文件，无法签名。", srcPath);
        }

        string signedName = $"{target.Id}-{Path.GetFileNameWithoutExtension(target.OriginalFileName)}.signed.bin";
        string signedDest = Path.Combine(filesDir, signedName);

        await RunPackOtaAsync(srcPath, signedDest, signType).ConfigureAwait(false);

        target.IsSigned = true;
        target.IsExternallySigned = false;
        target.SignedStoredFileName = signedName;
        target.SignedFileSizeBytes = new FileInfo(signedDest).Length;
        target.SignType = signType;
        target.SignedAt = DateTime.Now;
        if (string.Equals(target.Status, DefaultStatus, StringComparison.Ordinal))
        {
            target.Status = SignedOnlyStatus;
        }

        await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(all, _jsonOptions));
        return true;
    }

    /// <summary>
    /// 切换“外部已签名”标记。仅适用于未由脚本签名的条目。
    /// 启用时：认为原始文件本身即为签名产物，在主调用者调用后可进入后续状态。
    /// 禁用时：状态被重置为“未签名”。
    /// </summary>
    public static async Task<bool> MarkExternallySignedAsync(string id, bool externallySigned)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        if (!IsCompanyNetworkAvailable())
        {
            throw new InvalidOperationException("未连接到公司网络，无法访问数据库共享路径。");
        }

        string indexPath = EnsureDatabase();
        List<FirmwareEntry> all = LoadAll()
            .Where(e => !e.Id.StartsWith(OrphanIdPrefix, StringComparison.Ordinal))
            .ToList();
        FirmwareEntry? target = all.FirstOrDefault(e => e.Id == id);
        if (target is null)
        {
            return false;
        }

        if (!target.IsRegistered)
        {
            throw new InvalidOperationException("未注册的条目禁止修改外部签名标记，请先删除后重新通过本程序上传。");
        }

        // 脚本签名过的条目不允许被手动切换为外部签名
        if (!string.IsNullOrEmpty(target.SignedStoredFileName))
        {
            throw new InvalidOperationException("该条目已通过脚本签名，无法手动修改签名来源。");
        }

        if (externallySigned)
        {
            target.IsSigned = true;
            target.IsExternallySigned = true;
            if (string.Equals(target.Status, DefaultStatus, StringComparison.Ordinal))
            {
                target.Status = SignedOnlyStatus;
            }
        }
        else
        {
            target.IsSigned = false;
            target.IsExternallySigned = false;
            target.Status = DefaultStatus;
        }

        await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(all, _jsonOptions));
        return true;
    }

    /// <summary>
    /// 从数据库中删除指定条目及其实际文件。
    /// </summary>
    /// <returns>是否成功删除。</returns>
    public static async Task<bool> DeleteAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        if (!IsCompanyNetworkAvailable())
        {
            throw new InvalidOperationException("未连接到公司网络，无法访问数据库共享路径。");
        }

        string filesDir = Path.Combine(DatabaseRoot, FilesSubDir);

        // 孤儿条目（外部添加但未登记的物理文件）：不在索引中，直接删除磁盘文件即可。
        if (id.StartsWith(OrphanIdPrefix, StringComparison.Ordinal))
        {
            string orphanFileName = id.Substring(OrphanIdPrefix.Length);
            if (string.IsNullOrEmpty(orphanFileName))
            {
                return false;
            }

            string orphanPath = Path.Combine(filesDir, orphanFileName);
            if (!File.Exists(orphanPath))
            {
                return false;
            }

            try
            {
                await Task.Run(() => File.Delete(orphanPath));
                return true;
            }
            catch
            {
                return false;
            }
        }

        string indexPath = EnsureDatabase();
        List<FirmwareEntry> all = LoadAll()
            .Where(e => !e.Id.StartsWith(OrphanIdPrefix, StringComparison.Ordinal))
            .ToList();
        FirmwareEntry? target = all.FirstOrDefault(e => e.Id == id);
        if (target is null)
        {
            return false;
        }

        // 先移除索引条目，再尝试删除文件。即使文件删除失败，索引也已一致。
        _ = all.RemoveAll(e => e.Id == id);
        await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(all, _jsonOptions));

        try
        {
            string filePath = Path.Combine(filesDir, target.StoredFileName);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            if (!string.IsNullOrEmpty(target.SignedStoredFileName))
            {
                string signedPath = Path.Combine(filesDir, target.SignedStoredFileName);
                if (File.Exists(signedPath))
                {
                    File.Delete(signedPath);
                }
            }
        }
        catch
        {
            // 实际文件删除失败不影响索引一致性，后续可手工清理
        }

        return true;
    }

    /// <summary>
    /// 调用输出目录下的 ota_pack_tool/pack_ota.py 对指定输入 bin 进行签名打包，
    /// 产物写入 <paramref name="outputBinPath"/>（可为任意路径，包括网络共享路径）。
    /// 原理与 FoE 烧录中的签名调用一致。
    /// </summary>
    private static async Task RunPackOtaAsync(string inputBinPath, string outputBinPath, int signType)
    {
        if (signType < 0 || signType > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(signType), signType, "签名类型必须为 0-5。");
        }

        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string toolDir = Path.Combine(appDir, "ota_pack_tool");
        string scriptPath = Path.Combine(toolDir, "pack_ota.py");
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("未找到 OTA 打包脚本，请检查输出目录。", scriptPath);
        }

        // 脚本以 cwd 为工作目录定位输入输出文件名，但同时会在 cwd 生成临时 usb_device_update.upd。
        // 为避免与并发调用冲突并保证输入/输出可使用任意路径，
        // 在本地临时目录中作业，完成后再拷贝产物到目标位置。
        string tempDir = Path.Combine(Path.GetTempPath(), "ServoStudio_OtaSign_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(tempDir);

        try
        {
            string tempInputName = "input_" + Path.GetFileName(inputBinPath);
            string tempInput = Path.Combine(tempDir, tempInputName);
            File.Copy(inputBinPath, tempInput, overwrite: true);

            const string tempOutputName = "update_sign.bin";

            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{scriptPath}\" {signType} \"{tempInputName}\" \"{tempOutputName}\"",
                WorkingDirectory = tempDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 Python 进程，请检查是否安装 Python。");

            string stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"pack_ota.py 运行失败 (exit={proc.ExitCode})\nstdout: {stdout}\nstderr: {stderr}");
            }

            string tempOutput = Path.Combine(tempDir, tempOutputName);
            if (!File.Exists(tempOutput))
            {
                throw new InvalidOperationException(
                    $"pack_ota.py 未生成输出文件。stdout: {stdout}\nstderr: {stderr}");
            }

            // 仅保留 .bin 签名产物；usb_device_update.upd 不入库（临时目录会被整体删除）。
            string? destDir = Path.GetDirectoryName(outputBinPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            {
                _ = Directory.CreateDirectory(destDir);
            }

            File.Copy(tempOutput, outputBinPath, overwrite: true);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}

/// <summary>
/// 厂家固件数据库条目。
/// </summary>
public sealed class FirmwareEntry
{
    public string Id { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
    public string UploadedFromMachine { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string Version { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ApplicableHardwareVersion { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 固件状态：未签名/仅签名/未注册/待测试/已知问题/通过测试/发布/归档/测试用例/禁用。
    /// </summary>
    public string Status { get; set; } = FirmwareRepositoryService.DefaultStatus;

    /// <summary>
    /// 产品类型（暂用 A/B/C，后续可替换为真实产品系列名）。
    /// </summary>
    public string ProductType { get; set; } = FirmwareRepositoryService.DefaultProductType;

    /// <summary>    /// 是否为通过本程序合法上传并登记的条目。
    /// 仅当通过 <see cref="FirmwareRepositoryService.UploadAsync"/> 入库时被设为 true；
    /// 外部直接编辑 JSON 添加的条目以及 files/ 中的孤儿文件均为 false（视作"未注册"，
    /// 状态被强制为 <see cref="FirmwareRepositoryService.UnregisteredStatus"/>，
    /// 除删除外的全部操作被禁止）。
    /// </summary>
    public bool IsRegistered { get; set; }

    /// <summary>    /// 是否已签名。true 表示本条目拥有有效的签名产物（脚本生成或用户标记为外部已签名）。
    /// IsSigned 为 false 时，Status 始终被强制为“未签名”。
    /// </summary>
    public bool IsSigned { get; set; }

    /// <summary>
    /// 是否为用户手动标记“外部已签名”的文件。此时认为原始文件本身即为签名产物，不会在数据库中存储额外的签名文件。
    /// </summary>
    public bool IsExternallySigned { get; set; }

    /// <summary>
    /// 脚本签名后生成的签名产物在 files/ 中的存储名。仅脚本签名时存在。外部已签名或未签名时为空。
    /// </summary>
    public string SignedStoredFileName { get; set; } = string.Empty;

    /// <summary>
    /// 脚本签名产物的字节数。外部已签名或未签名时为 0。
    /// </summary>
    public long SignedFileSizeBytes { get; set; }

    /// <summary>
    /// 脚本签名时采用的校验类型（0-5），-1 表示未脚本签名。
    /// </summary>
    public int SignType { get; set; } = -1;

    /// <summary>
    /// 脚本签名完成时间。未脚本签名时为 null。
    /// </summary>
    public DateTime? SignedAt { get; set; }

    /// <summary>
    /// 亲和 UI 绑定用：签名来源文本。
    /// </summary>
    [JsonIgnore]
    public string SignSourceText =>
        IsExternallySigned ? "已签名（外部）"
            : IsSigned ? "已签名（脚本）"
            : "未签名";
}
