// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wpf.Ui.servoStudio.Services;

/// <summary>
/// Provides load/save for user settings persisted as a JSON file in AppData.
/// </summary>
public static class UserSettingsService
{
    private static readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ServoStudio",
        "settings.json");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Loads settings from disk. Returns defaults if file does not exist or is invalid.
    /// </summary>
    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<UserSettings>(json, _jsonOptions) ?? new UserSettings();
            }
        }
        catch
        {
            // Corrupted file — return defaults
        }

        return new UserSettings();
    }

    /// <summary>
    /// Saves settings to disk.
    /// </summary>
    public static void Save(UserSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath)!;
            if (!Directory.Exists(dir))
            {
                _ = Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            File.WriteAllText(_settingsPath, json);

            SettingsChanged?.Invoke(null, settings);
        }
        catch
        {
            // Best-effort save — don't crash the app
        }
    }

    /// <summary>Fires after <see cref="Save"/> persists the file. Used by pages (e.g. UserConfigPage) to refresh UI.</summary>
    public static event EventHandler<UserSettings>? SettingsChanged;

    /// <summary>Gets the absolute path of the JSON settings file.</summary>
    public static string GetSettingsPath() => _settingsPath;

    /// <summary>Serializes a <see cref="UserSettings"/> instance to a JSON string.</summary>
    public static string ToJson(UserSettings settings)
        => JsonSerializer.Serialize(settings, _jsonOptions);

    /// <summary>
    /// 用于 UI 实时预览：可选择性脱敏掉厂家专属字段（厂家密码未解锁时不显示明文）。
    /// </summary>
    public static string ToJsonForDisplay(UserSettings settings, bool includeFactoryFields)
    {
        if (includeFactoryFields)
            return ToJson(settings);

        JsonNode? node = JsonNode.Parse(JsonSerializer.Serialize(settings, _jsonOptions));
        if (node is not JsonObject obj)
            return ToJson(settings);

        foreach (string field in _factoryFieldNames)
        {
            if (obj.ContainsKey(field))
                obj[field] = "<lock>需厂家权限解锁后查看</lock>";
        }

        return obj.ToJsonString(_jsonOptions);
    }

    /// <summary>
    /// 厂家专属字段集合：导出 JSON 时会在此集合中的字段上做 AES 加密；UI 锁定时会脱敏显示。
    /// </summary>
    private static readonly string[] _factoryFieldNames =
    [
        nameof(UserSettings.FoeTargetFileName),
        nameof(UserSettings.FoePasswordText),
        nameof(UserSettings.IsPreProcessedFirmware),
        nameof(UserSettings.DisabledRegisters_EtherCAT),
        nameof(UserSettings.DisabledRegisters_CANopen),
        nameof(UserSettings.DisabledRegisters_Modbus),
    ];

    /// <summary>导出/导入加密所用的固定密钥派生串。变化将导致旧导出文件无法识别。</summary>
    private const string _factoryEncryptionSeed = "ServoStudio-Factory-Encryption-Key-v1";

    /// <summary>
    /// 在 JsonObject 中将厂家专属字段替换为加密信封；非字段或不存在时跳过。
    /// 加密信封形如：{ "$enc": "AES-256-CBC", "iv": "base64", "data": "base64" }
    /// </summary>
    private static void EncryptFactoryFields(JsonObject obj)
    {
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(_factoryEncryptionSeed));
        foreach (string field in _factoryFieldNames)
        {
            if (!obj.ContainsKey(field) || obj[field] is null)
                continue;

            // 把原节点序列化为 UTF-8 字节再加密
            string raw = obj[field]!.ToJsonString();
            byte[] plain = Encoding.UTF8.GetBytes(raw);

            using var aes = Aes.Create();
            aes.Key = key;
            aes.GenerateIV();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            byte[] cipher = aes.EncryptCbc(plain, aes.IV);

            obj[field] = new JsonObject
            {
                ["$enc"] = "AES-256-CBC",
                ["iv"] = Convert.ToBase64String(aes.IV),
                ["data"] = Convert.ToBase64String(cipher),
            };
        }

        obj["_factoryEncrypted"] = true;
    }

    /// <summary>
    /// 与 <see cref="EncryptFactoryFields"/> 相反：把加密信封还原为原始 JsonNode。
    /// 解密失败时该字段保留为信封原样，调用方反序列化时 UserSettings 默认值生效。
    /// </summary>
    private static void DecryptFactoryFields(JsonObject obj)
    {
        if (obj["_factoryEncrypted"] is not JsonValue v || !v.TryGetValue<bool>(out bool flag) || !flag)
            return;

        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(_factoryEncryptionSeed));
        foreach (string field in _factoryFieldNames)
        {
            if (obj[field] is not JsonObject envelope) continue;
            if (envelope["$enc"]?.GetValue<string>() != "AES-256-CBC") continue;

            try
            {
                byte[] iv = Convert.FromBase64String(envelope["iv"]!.GetValue<string>());
                byte[] cipher = Convert.FromBase64String(envelope["data"]!.GetValue<string>());

                using var aes = Aes.Create();
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                byte[] plain = aes.DecryptCbc(cipher, iv);
                string raw = Encoding.UTF8.GetString(plain);
                obj[field] = JsonNode.Parse(raw);
            }
            catch
            {
                // 解密失败：保留原信封节点，反序列化时该字段会回落到默认值。
                // 静默处理避免抛断导入流程。
            }
        }

        _ = obj.Remove("_factoryEncrypted");
    }

    /// <summary>Imports settings from the given JSON file and persists them as current. Returns true on success.</summary>
    public static bool ImportFromFile(string path, out UserSettings? imported)
    {
        imported = null;
        try
        {
            if (!File.Exists(path))
                return false;

            var json = File.ReadAllText(path);
            // 先试图以 JsonObject 打开：如果是加密过的导出文件，需先解密。
            JsonNode? node = JsonNode.Parse(json);
            if (node is JsonObject obj)
            {
                DecryptFactoryFields(obj);
                json = obj.ToJsonString(_jsonOptions);
            }

            UserSettings? parsed = JsonSerializer.Deserialize<UserSettings>(json, _jsonOptions);
            if (parsed == null)
                return false;

            imported = parsed;
            Save(parsed);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Exports current settings to the given file path. Returns true on success.
    /// 对于厂家专属字段最终会被 AES-256-CBC 加密为信封对象，并在顶层添加 "_factoryEncrypted": true 标记。</summary>
    public static bool ExportToFile(string path, UserSettings? settings = null)
    {
        try
        {
            settings ??= Load();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                _ = Directory.CreateDirectory(dir);
            }

            // 先序列化为 JsonObject，再对厂家字段执行加密。
            string json = JsonSerializer.Serialize(settings, _jsonOptions);
            JsonNode? node = JsonNode.Parse(json);
            if (node is JsonObject obj)
            {
                EncryptFactoryFields(obj);
                json = obj.ToJsonString(_jsonOptions);
            }

            File.WriteAllText(path, json);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// User-persisted settings.
/// </summary>
public class UserSettings
{
    /// <summary>
    /// "theme_light", "theme_dark", or "theme_system".
    /// </summary>
    public string ThemeMode { get; set; } = "theme_light";

    /// <summary>
    /// Page visit counts keyed by full type name.
    /// </summary>
    public Dictionary<string, int> PageVisitCounts { get; set; } = new();

    /// <summary>
    /// FoE target filename remembered across sessions.
    /// </summary>
    public string FoeTargetFileName { get; set; } = string.Empty;

    /// <summary>
    /// FoE password text remembered across sessions.
    /// </summary>
    public string FoePasswordText { get; set; } = "0x";

    /// <summary>
    /// Whether the pre-processed (signed) firmware checkbox was checked.
    /// </summary>
    public bool IsPreProcessedFirmware { get; set; }

    // ===== DataSavePage 配置（数据存储页） =====

    /// <summary>是否在应用启动/页面打开时自动启用数据帧落盘。</summary>
    public bool DataSave_AutoStart { get; set; }

    /// <summary>数据帧落盘目录。空串表示使用默认 %LocalAppData%\ServoStudio\Data。</summary>
    public string DataSave_Directory { get; set; } = string.Empty;

    /// <summary>数据帧落盘格式：CSV / TSV / XLS / JSON。</summary>
    public string DataSave_Format { get; set; } = "CSV";

    /// <summary>单文件最大 MB，超过则自动轮转。</summary>
    public int DataSave_MaxFileSizeMB { get; set; } = 50;

    /// <summary>从机变量采样间隔 (ms)，用于"每读一帧即落盘一条"的轮询周期。</summary>
    public int DataSave_PollIntervalMs { get; set; } = 100;

    /// <summary>页面内保留的最大采样条数，用于实时显示与统计。</summary>
    public int DataSave_MaxSampleCount { get; set; } = 5000;

    /// <summary>是否已经由用户显式保存过变量勾选状态。</summary>
    public bool DataSave_HasVariableSelection { get; set; }

    /// <summary>被勾选参与落盘的变量 FullName 列表（Group.Name 或 Name）。</summary>
    public List<string> DataSave_SelectedVariables { get; set; } = new();

    /// <summary>调试 - 测试数据生成器的输出根目录。空串表示使用默认 (OutputDirectory\TestData)。</summary>
    public string DataSave_TestDataDirectory { get; set; } = string.Empty;

    // ===== 设备列表页串口/以太网连接配置 =====

    /// <summary>记忆的串口名称（如 COM3）。</summary>
    public string DeviceAdd_SerialPortName { get; set; } = string.Empty;

    /// <summary>串口波特率下拉索引。</summary>
    public int DeviceAdd_BaudIndex { get; set; } = -1;

    /// <summary>串口数据位下拉索引。</summary>
    public int DeviceAdd_DataBitIndex { get; set; } = -1;

    /// <summary>串口校验位下拉索引。</summary>
    public int DeviceAdd_CheckBitIndex { get; set; } = -1;

    /// <summary>串口停止位下拉索引。</summary>
    public int DeviceAdd_StopBitIndex { get; set; } = -1;

    /// <summary>记忆的以太网适配器描述。</summary>
    public string DeviceAdd_EthernetDeviceName { get; set; } = string.Empty;

    /// <summary>记忆的 Modbus 从机地址（1~247）。</summary>
    public int DeviceAdd_ModbusSlaveAddress { get; set; } = 1;

    /// <summary>记忆的 CANopen 节点 ID（1~127）。</summary>
    public int DeviceAdd_CanopenNodeId { get; set; } = 1;

    /// <summary>记忆的 CANopen 比特率下拉索引（默认 500 kbps = 6）。</summary>
    public int DeviceAdd_CanopenBitrateIndex { get; set; } = 6;

    /// <summary>添加设备页上次激活的协议栈标签页索引（0=EtherCAT, 1=CANopen, 2=Modbus, 3=USB）。</summary>
    public int DeviceAdd_ActiveTabIndex { get; set; } = 0;

    /// <summary>记忆的 CAN 适配器类别名称（对应 <c>CanAdapterKind</c> 枚举字符串）。</summary>
    public string DeviceAdd_CanAdapterKind { get; set; } = string.Empty;

    // ===== USB（HPM 系列 MCU + ThreadX/USBX 从机协议栈） =====

    /// <summary>记忆的 USB 设备 InstanceId（用于下次启动自动定位上次连接的设备）。</summary>
    public string DeviceAdd_UsbInstanceId { get; set; } = string.Empty;

    /// <summary>记忆的 USB 工作方式索引（0=CDC-ACM, 1=WinUSB Bulk, 2=MSC），默认 1。</summary>
    public int DeviceAdd_UsbWorkModeIndex { get; set; } = 1;

    /// <summary>
    /// 记忆的 USB VID/PID 白名单文本（每行一项，形如 "34B7:*" / "34B7:A001"）。<br/>
    /// 缺省含 HPMicro VID 通配。
    /// </summary>
    public string DeviceAdd_UsbVidPidWhitelistText { get; set; } = "34B7:*";

    /// <summary>是否启用 VID/PID 白名单过滤。</summary>
    public bool DeviceAdd_UsbFilterByVidPid { get; set; } = true;

    /// <summary>是否启用设备类（Bulk/CDC/HID/MSC）过滤。</summary>
    public bool DeviceAdd_UsbFilterByClass { get; set; } = true;

    /// <summary>是否启用 Manufacturer/Product 字符串关键词（"HPM/HPMicro/USBX/ThreadX"）过滤。</summary>
    public bool DeviceAdd_UsbFilterByKeyword { get; set; } = true;

    // ===== 厂家参数页 / 寄存器禁用持久化（按协议栈分别保存禁用 Key 列表） =====

    /// <summary>
    /// 被禁用的 EtherCAT 寄存器键集合。Key 形如 "0x6040/00"，由 <see cref="RegisterDisableService.MakeKey"/> 生成。
    /// </summary>
    public List<string> DisabledRegisters_EtherCAT { get; set; } = new();

    /// <summary>
    /// 被禁用的 CANopen 寄存器键集合（同上格式）。
    /// </summary>
    public List<string> DisabledRegisters_CANopen { get; set; } = new();

    /// <summary>
    /// 被禁用的 Modbus 寄存器键集合（同上格式，Key 由 SDO Index/SubIndex 派生）。
    /// </summary>
    public List<string> DisabledRegisters_Modbus { get; set; } = new();

    /// <summary>记忆的 CAN 适配器标识符（设备路径 / 序列号等）。</summary>
    public string DeviceAdd_CanAdapterIdentifier { get; set; } = string.Empty;

    /// <summary>
    /// ZLG 设备后端 DLL（如 USBCAN_E_64.dll）所在目录列表，由 <c>ZlgKernelDllScanner</c> 扫描后自动记忆。<br/>
    /// 每次枚举 CAN 适配器时读取并加入进程 PATH，使 <c>ZCAN_OpenDevice</c> 能找到对应后端驱动。
    /// </summary>
    public List<string> ZlgKernelDllDirs { get; set; } = [];

    /// <summary>
    /// ZLG 设备最近一次成功探测的 (deviceType, deviceIndex) 组合列表。<br/>
    /// 格式：每项为 "type:index"（如 "33:0"）。下次扫描时优先尝试这些组合命中即可跳过其他 8 种设备类型的盲探，
    /// 把 32 次 ZCAN_OpenDevice/ZCAN_CloseDevice 的串行调用降到设备数量级。
    /// </summary>
    public List<string> ZlgProbeHistory { get; set; } = [];

    /// <summary>
    /// ControlCAN 设备最近一次成功探测的 (deviceType, deviceIndex) 组合列表。<br/>
    /// 格式同 <see cref="ZlgProbeHistory"/>。把 5×4=20 次 VCI_OpenDevice/VCI_CloseDevice 降到设备数量级。
    /// </summary>
    public List<string> ControlCanProbeHistory { get; set; } = [];

    // ===== BusDiagnosticsPage 总线诊断配置 =====

    /// <summary>总线诊断采样间隔 (ms)，默认 100。</summary>
    public int BusDiagnostics_IntervalMs { get; set; } = 100;

    /// <summary>总线诊断是否同时探测 SDO 0x6041（仅 EtherCAT）。</summary>
    public bool BusDiagnostics_ProbeSdo { get; set; } = true;

    /// <summary>
    /// USB 带宽测量的速率上限模式：<c>false</c> = USB 2.0 全速（12 Mbps ≈ 1.5 MB/s，默认）；
    /// <c>true</c> = USB 2.0 高速（480 Mbps ≈ 60 MB/s）。仅影响带宽百分比的分母基准。
    /// </summary>
    public bool BusDiagnostics_UsbHighSpeedMode { get; set; }

    // ===== MotionTypePage 运动配置 =====

    /// <summary>CSP/CSV/CST 周期同步发送间隔 (ms)。</summary>
    public double Motion_CyclicSendIntervalMs { get; set; } = 20;

    /// <summary>
    /// CSP/CSV/CST 周期同步使用的底层定时器类型。<br/>
    /// 0 = .NET PeriodicTimer (默认), 1 = Win32 多媒体定时器, 2 = Stopwatch+SpinWait。<br/>
    /// 对应 <c>Wpf.Ui.servoStudio.Core.Sync.CyclicTimerKind</c>。
    /// </summary>
    public int Motion_CyclicTimerKind { get; set; } = 0;

    /// <summary>
    /// 进入 CSP/CSV/CST 模式时是否优先使用 PDO + SYNC（CANopen）或 PDO + InOutSync（EtherCAT）路径。<br/>
    /// false = 退回到 SDO 周期写（旧行为，用于兼容 / 调试）。
    /// </summary>
    public bool Motion_PreferPdoForCyclicSync { get; set; } = true;

    // ===== AppLogPage 日志配置 =====

    /// <summary>是否启用应用日志写入。</summary>
    public bool AppLog_IsEnabled { get; set; } = true;

    /// <summary>应用日志目录。空表示使用默认 %LocalAppData%\ServoStudio\Logs。</summary>
    public string AppLog_Directory { get; set; } = string.Empty;

    /// <summary>最低日志级别。</summary>
    public string AppLog_MinLevel { get; set; } = "Info";

    /// <summary>单日志文件最大 MB。</summary>
    public int AppLog_MaxFileSizeMB { get; set; } = 10;

    /// <summary>日志保留天数。</summary>
    public int AppLog_RetentionDays { get; set; } = 30;

    /// <summary>是否自动滚动日志列表。</summary>
    public bool AppLog_IsAutoScroll { get; set; } = true;

    // ===== DataViewPage 波形窗配置 =====

    /// <summary>波形窗是否使用深色主题。true = 深色；false = 浅色。</summary>
    public bool DataView_IsDarkTheme { get; set; }

    /// <summary>波形窗图例字号（pt）。默认 16，范围 8–36。</summary>
    public float DataView_LegendFontSize { get; set; } = 16f;

    // ===== PidAdjustPage 波形窗配置 =====

    /// <summary>PID调节页波形窗是否使用深色主题。true = 深色；false = 浅色。</summary>
    public bool PidAdjust_IsDarkTheme { get; set; }

    /// <summary>PID调节页波形窗图例字号（pt）。默认 16，范围 8–36。</summary>
    public float PidAdjust_LegendFontSize { get; set; } = 16f;

    // ===== MotionProfilePage 波形窗主题（与 DataView 行为一致：跟随主题，可手动覆盖） =====

    /// <summary>运动曲线页（速度/加速度）波形窗是否使用深色主题。</summary>
    public bool MotionProfile_IsDarkTheme { get; set; }

    /// <summary>
    /// 数据导入是否强制校验必要列（Name 等）。仅在 Debug 模式下可在 UI 切换。
    /// 关闭后将以"第一非空行"为列标题，缺失必要列时按通用列方向解释（每列一通道）。
    /// </summary>
    public bool DataView_EnforceRequiredColumns { get; set; } = true;

    // ===== Debug 模式 =====

    /// <summary>是否启用 Debug 模式（显示测试数据生成等调试控件）。</summary>
    public bool IsDebugMode { get; set; }

    // ===== DataViewPage 波形窗导入配置 =====

    /// <summary>波形窗上次导入文件所在的目录，用于下次打开对话框时定位。空串表示使用默认目录。</summary>
    public string DataView_LastDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 波形窗按文件记忆的可见通道签名列表。
    /// 键 = 文件绝对路径（小写）；值 = 该文件中曾被勾选展示的通道签名（Source|Group|Name|SdoIndex|SdoSubIndex|DataType|Unit）列表。
    /// </summary>
    public Dictionary<string, List<string>> DataView_VisibleChannelsByFile { get; set; } = new();

    // ===== 在线电机参数辨识页 =====

    /// <summary>是否启用在线电机参数辨识总开关（Debug 模式调控）。关闭时该页一切操作禁用。</summary>
    public bool MotorIdent_IsEnabled { get; set; }

    /// <summary>辨识方式：0=静止辨识 1=旋转辨识 2=综合辨识。</summary>
    public int MotorIdent_Mode { get; set; } = 0;

    /// <summary>注入电流幅值百分比（占额定电流），范围 5%~100%，默认 30%。</summary>
    public double MotorIdent_InjectCurrentPercent { get; set; } = 30.0;

    /// <summary>旋转辨识最大测试转速 rpm，默认 500。</summary>
    public int MotorIdent_MaxTestSpeedRpm { get; set; } = 500;

    /// <summary>单次辨识时长 s，默认 8。</summary>
    public int MotorIdent_DurationSec { get; set; } = 8;

    /// <summary>是否在辨识完成后自动写入电机参数寄存器，默认 false。</summary>
    public bool MotorIdent_AutoApplyResult { get; set; }

    /// <summary>是否启用估计算法的自适应抗噪滤波。</summary>
    public bool MotorIdent_NoiseFilterEnabled { get; set; } = true;

    /// <summary>历史记录条数上限。</summary>
    public int MotorIdent_HistoryLimit { get; set; } = 20;
}