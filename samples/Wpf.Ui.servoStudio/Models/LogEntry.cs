// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Wpf.Ui.servoStudio.Models;

/// <summary>
/// 应用日志级别
/// </summary>
public enum AppLogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
    Critical = 4
}

/// <summary>
/// 应用日志分类
/// </summary>
public enum AppLogCategory
{
    App,
    EtherCAT,
    SDO,
    Fault,
    Navigation,
    Parameter,
    Firmware,
    Config,
    System,

    /// <summary>
    /// 用户主动触发的操作（按钮点击、配置修改等）
    /// </summary>
    User,

    /// <summary>
    /// 性能 / 计时类记录
    /// </summary>
    Performance,
}

/// <summary>
/// 单条日志记录
/// </summary>
public class LogEntry
{
    public int Index { get; init; }
    public DateTime Timestamp { get; init; }
    public AppLogLevel Level { get; init; }
    public AppLogCategory Category { get; init; }
    public string Message { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;

    public string LevelText => Level switch
    {
        AppLogLevel.Debug => "DEBUG",
        AppLogLevel.Info => "INFO",
        AppLogLevel.Warning => "WARN",
        AppLogLevel.Error => "ERROR",
        AppLogLevel.Critical => "FATAL",
        _ => "?"
    };

    public string CategoryText => Category.ToString();
    public string TimestampText => Timestamp.ToString("HH:mm:ss.fff");
    public string DateText => Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");

    /// <summary>
    /// 序列化为日志行: timestamp|level|category|message|details
    /// </summary>
    public string ToLogLine()
        => $"{Timestamp:yyyy-MM-ddTHH:mm:ss.fff}|{LevelText}|{CategoryText}|{Message}|{Details}";

    /// <summary>
    /// 从日志行解析
    /// </summary>
    public static LogEntry? FromLogLine(string line, int index)
    {
        var parts = line.Split('|', 5);
        if (parts.Length < 4)
            return null;

        if (!DateTime.TryParse(parts[0], out DateTime ts))
            return null;

        AppLogLevel level = parts[1].Trim().ToUpperInvariant() switch
        {
            "DEBUG" => AppLogLevel.Debug,
            "INFO" => AppLogLevel.Info,
            "WARN" or "WARNING" => AppLogLevel.Warning,
            "ERROR" => AppLogLevel.Error,
            "FATAL" or "CRITICAL" => AppLogLevel.Critical,
            _ => AppLogLevel.Info
        };

        _ = Enum.TryParse<AppLogCategory>(parts[2].Trim(), true, out AppLogCategory category);

        return new LogEntry
        {
            Index = index,
            Timestamp = ts,
            Level = level,
            Category = category,
            Message = parts[3],
            Details = parts.Length > 4 ? parts[4] : string.Empty
        };
    }
}

/// <summary>
/// 日志文件信息
/// </summary>
public class LogFileInfo
{
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public DateTime LastModified { get; init; }
    public int EntryCount { get; set; }

    public string FileSizeText => FileSize switch
    {
        < 1024 => $"{FileSize} B",
        < 1048576 => $"{FileSize / 1024.0:F1} KB",
        _ => $"{FileSize / 1048576.0:F2} MB"
    };

    public string LastModifiedText => LastModified.ToString("yyyy-MM-dd HH:mm");
}