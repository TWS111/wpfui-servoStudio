// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.IO;
using System.Text.Json;

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

    /// <summary>Imports settings from the given JSON file and persists them as current. Returns true on success.</summary>
    public static bool ImportFromFile(string path, out UserSettings? imported)
    {
        imported = null;
        try
        {
            if (!File.Exists(path))
                return false;

            var json = File.ReadAllText(path);
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

    /// <summary>Exports current settings to the given file path. Returns true on success.</summary>
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

            File.WriteAllText(path, JsonSerializer.Serialize(settings, _jsonOptions));
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

    // ===== DeviceAddPage 串口/以太网连接配置 =====

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

    // ===== MotionTypePage 运动配置 =====

    /// <summary>CSP/CSV/CST 周期同步发送间隔 (ms)。</summary>
    public double Motion_CyclicSendIntervalMs { get; set; } = 20;

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
}