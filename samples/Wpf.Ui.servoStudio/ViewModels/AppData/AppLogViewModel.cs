// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Microsoft.Win32;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.Services;

namespace Wpf.Ui.servoStudio.ViewModels.AppData;

public partial class AppLogViewModel : ViewModel, IDisposable
{
    /// <summary>
    /// 全局静态实例，供其它 ViewModel 调用 Log()
    /// </summary>
    public static AppLogViewModel? Current { get; private set; }

    private readonly object _writeLock = new();
    private readonly List<LogEntry> _sessionEntries = [];
    private StreamWriter? _writer;
    private string _currentLogFilePath = string.Empty;
    private int _logIndex;

    // ===== 配置 =====
    [ObservableProperty]
    private bool _isLoggingEnabled = true;

    [ObservableProperty]
    private string _logDirectory = string.Empty;

    [ObservableProperty]
    private string _selectedMinLevel = "Info";

    [ObservableProperty]
    private int _maxFileSizeMB = 10;

    [ObservableProperty]
    private int _retentionDays = 30;

    // ===== 日志数据 =====
    [ObservableProperty]
    private ObservableCollection<LogEntry> _filteredEntries = [];

    // ===== 筛选 =====
    [ObservableProperty]
    private string _selectedLevelFilter = "全部";

    [ObservableProperty]
    private string _selectedCategoryFilter = "全部";

    [ObservableProperty]
    private string _searchText = string.Empty;

    // ===== 状态 =====
    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private string _currentFileName = string.Empty;

    [ObservableProperty]
    private int _totalEntries;

    [ObservableProperty]
    private bool _isAutoScroll = true;

    [ObservableProperty]
    private string _loadedSource = "当前会话";

    // ===== 统计（按级别，会话维度） =====
    [ObservableProperty]
    private int _debugCount;

    [ObservableProperty]
    private int _infoCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private int _criticalCount;

    [ObservableProperty]
    private DateTime _sessionStartTime = DateTime.Now;

    /// <summary>
    /// 上一次导航到的页面类型（用于记录 from → to）。
    /// </summary>
    private Type? _lastNavigatedPage;

    // ===== 历史文件列表 =====
    [ObservableProperty]
    private ObservableCollection<LogFileInfo> _logFiles = [];

    // ===== 下拉选项 =====
    public string[] MinLevelOptions => ["Debug", "Info", "Warning", "Error", "Critical"];
    public string[] LevelFilterOptions => ["全部", "Debug", "Info", "Warning", "Error", "Critical"];
    public string[] CategoryFilterOptions =>
        ["全部", "App", "EtherCAT", "SDO", "Fault", "Navigation", "Parameter", "Firmware", "Config", "System", "User", "Performance"];

    public AppLogViewModel()
    {
        Current = this;

        // 优先加载用户保存的日志配置
        _isLoadingSettings = true;
        try
        {
            UserSettings s = Services.UserSettingsService.Load();
            IsLoggingEnabled = s.AppLog_IsEnabled;
            LogDirectory = string.IsNullOrWhiteSpace(s.AppLog_Directory)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ServoStudio", "Logs")
                : s.AppLog_Directory;
            SelectedMinLevel = string.IsNullOrEmpty(s.AppLog_MinLevel) ? "Info" : s.AppLog_MinLevel;
            MaxFileSizeMB = Math.Max(1, s.AppLog_MaxFileSizeMB);
            RetentionDays = Math.Max(1, s.AppLog_RetentionDays);
            IsAutoScroll = s.AppLog_IsAutoScroll;
        }
        finally
        {
            _isLoadingSettings = false;
        }

        _ = Directory.CreateDirectory(LogDirectory);

        OpenLogFile();

        // ===== 会话启动信息（更详尽） =====
        try
        {
            var asm = System.Reflection.Assembly.GetEntryAssembly();
            var version = asm?.GetName().Version?.ToString() ?? "unknown";
            var asmName = asm?.GetName().Name ?? "ServoStudio";
            WriteLog(AppLogLevel.Info, AppLogCategory.App, "会话开始",
                $"程序: {asmName} v{version} | 目录: {LogDirectory}");

            WriteLog(AppLogLevel.Info, AppLogCategory.System, "运行环境",
                $"OS: {Environment.OSVersion} | x64: {Environment.Is64BitOperatingSystem}/{Environment.Is64BitProcess} | " +
                $".NET: {Environment.Version} | CPU核心: {Environment.ProcessorCount} | " +
                $"用户: {Environment.UserName}@{Environment.MachineName} | " +
                $"工作目录: {Environment.CurrentDirectory}");

            WriteLog(AppLogLevel.Debug, AppLogCategory.App, "日志配置",
                $"启用={IsLoggingEnabled} | 最低级别={SelectedMinLevel} | 单文件上限={MaxFileSizeMB}MB | 保留={RetentionDays}天 | 自动滚动={IsAutoScroll}");
        }
        catch (Exception ex)
        {
            WriteLog(AppLogLevel.Warning, AppLogCategory.System, "采集启动信息失败", ex.Message);
        }
    }

    // ===== 用户配置自动持久化 =====

    private readonly bool _isLoadingSettings;

    private void SaveUserSettings()
    {
        if (_isLoadingSettings)
            return;

        try
        {
            UserSettings s = Services.UserSettingsService.Load();
            s.AppLog_IsEnabled = IsLoggingEnabled;
            s.AppLog_Directory = LogDirectory ?? string.Empty;
            s.AppLog_MinLevel = SelectedMinLevel ?? "Info";
            s.AppLog_MaxFileSizeMB = Math.Max(1, MaxFileSizeMB);
            s.AppLog_RetentionDays = Math.Max(1, RetentionDays);
            s.AppLog_IsAutoScroll = IsAutoScroll;
            Services.UserSettingsService.Save(s);
        }
        catch
        {
            // ignore
        }
    }

    partial void OnIsLoggingEnabledChanged(bool value) => SaveUserSettings();
    partial void OnLogDirectoryChanged(string value) => SaveUserSettings();
    partial void OnSelectedMinLevelChanged(string value) => SaveUserSettings();
    partial void OnMaxFileSizeMBChanged(int value) => SaveUserSettings();
    partial void OnRetentionDaysChanged(int value) => SaveUserSettings();
    partial void OnIsAutoScrollChanged(bool value) => SaveUserSettings();

    public override void OnNavigatedTo()
    {
        if (LoadedSource == "当前会话")
            ApplyFilter();

        RefreshLogFiles();
        StatusText = $"共 {FilteredEntries.Count} 条记录";
    }

    // ========== 静态便捷方法 ==========

    /// <summary>
    /// 全局静态日志写入，供所有 ViewModel 调用
    /// </summary>
    public static void Log(AppLogLevel level, AppLogCategory category, string message, string details = "")
    {
        Current?.WriteLog(level, category, message, details);
    }

    /// <summary>
    /// 记录一次页面导航（在 NavigationView.Navigated 中调用）。
    /// </summary>
    public static void LogNavigation(Type? pageType, string pageTitle = "", string? from = null)
    {
        if (Current is null || pageType is null)
            return;

        var title = string.IsNullOrEmpty(pageTitle)
            ? Services.PageUsageTracker.GetPageTitle(pageType)
            : pageTitle;

        var fromTitle = from ?? (Current._lastNavigatedPage is Type lp
            ? Services.PageUsageTracker.GetPageTitle(lp)
            : "(初始)");

        Current.WriteLog(AppLogLevel.Info, AppLogCategory.Navigation,
            $"打开页面: {title}",
            $"{fromTitle} → {title}  [{pageType.FullName}]");

        Current._lastNavigatedPage = pageType;
    }

    /// <summary>
    /// 记录一次用户主动操作（按钮点击、菜单等）。
    /// </summary>
    public static void LogUserAction(string action, string details = "")
        => Current?.WriteLog(AppLogLevel.Info, AppLogCategory.User, action, details);

    /// <summary>
    /// 记录一段耗时操作（毫秒）。
    /// </summary>
    public static void LogPerformance(string operation, long elapsedMs, string details = "")
        => Current?.WriteLog(AppLogLevel.Debug, AppLogCategory.Performance,
            $"{operation} 耗时 {elapsedMs} ms", details);

    /// <summary>
    /// 应用退出时调用，写入会话结束信息并关闭文件。
    /// </summary>
    public void Shutdown()
    {
        try
        {
            TimeSpan duration = DateTime.Now - SessionStartTime;
            WriteLog(AppLogLevel.Info, AppLogCategory.App, "会话结束",
                $"持续时间: {duration:hh\\:mm\\:ss} | 共 {_logIndex} 条 | " +
                $"Debug={DebugCount}, Info={InfoCount}, Warn={WarningCount}, Error={ErrorCount}, Critical={CriticalCount}");
        }
        catch
        {
            // ignore
        }

        lock (_writeLock)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
            }
            catch
            {
                // ignore
            }
        }
    }

    // ========== 核心写入 ==========

    public void WriteLog(AppLogLevel level, AppLogCategory category, string message, string details = "")
    {
        if (!IsLoggingEnabled)
            return;
        if (level < GetMinLevel())
            return;

        var entry = new LogEntry
        {
            Index = Interlocked.Increment(ref _logIndex),
            Timestamp = DateTime.Now,
            Level = level,
            Category = category,
            Message = message,
            Details = details
        };

        // 写入文件（线程安全）
        lock (_writeLock)
        {
            try
            {
                CheckRotation();
                _writer?.WriteLine(entry.ToLogLine());
                _writer?.Flush();
            }
            catch
            {
                // 日志系统自身不应抛出异常
            }
        }

        // 始终保留会话缓冲
        lock (_sessionEntries)
        {
            _sessionEntries.Add(entry);
            while (_sessionEntries.Count > 50000)
                _sessionEntries.RemoveAt(0);
        }

        // 更新会话统计
        IncrementStat(entry.Level);

        // 若正在查看当前会话，实时更新 UI
        if (LoadedSource == "当前会话")
        {
            _ = (Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                if (MatchesFilter(entry))
                {
                    FilteredEntries.Add(entry);
                    TotalEntries = FilteredEntries.Count;
                }
            }));
        }
    }

    // ========== 文件管理 ==========

    private void OpenLogFile()
    {
        lock (_writeLock)
        {
            _writer?.Dispose();

            var baseName = $"servoStudio_{DateTime.Now:yyyyMMdd}";
            _currentLogFilePath = Path.Combine(LogDirectory, $"{baseName}.log");
            CurrentFileName = $"{baseName}.log";

            // 超过最大大小时创建轮转文件
            if (File.Exists(_currentLogFilePath))
            {
                var fi = new FileInfo(_currentLogFilePath);
                if (fi.Length > (long)MaxFileSizeMB * 1024 * 1024)
                {
                    var suffix = 1;
                    string rotatedPath;
                    do
                    {
                        rotatedPath = Path.Combine(LogDirectory, $"{baseName}_{suffix:D3}.log");
                        suffix++;
                    } while (File.Exists(rotatedPath));

                    _currentLogFilePath = rotatedPath;
                    CurrentFileName = Path.GetFileName(rotatedPath);
                }
            }

            _writer = new StreamWriter(_currentLogFilePath, append: true, Encoding.UTF8)
            {
                AutoFlush = false
            };
        }
    }

    private void CheckRotation()
    {
        if (string.IsNullOrEmpty(_currentLogFilePath) || !File.Exists(_currentLogFilePath))
            return;

        var fi = new FileInfo(_currentLogFilePath);
        if (fi.Length > (long)MaxFileSizeMB * 1024 * 1024)
            OpenLogFile();
    }

    // ========== 筛选 ==========

    partial void OnSelectedLevelFilterChanged(string value) => ApplyFilter();
    partial void OnSelectedCategoryFilterChanged(string value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<LogEntry> source = LoadedSource == "当前会话"
            ? _sessionEntries
            : FilteredEntries.ToList(); // loaded file data captured before re-filter

        // 当加载文件时，从_loadedFileEntries重新筛选
        if (LoadedSource != "当前会话" && _loadedFileEntries != null)
            source = _loadedFileEntries;

        IEnumerable<LogEntry> query = source;

        if (SelectedLevelFilter != "全部" && TryParseLevel(SelectedLevelFilter, out AppLogLevel level))
            query = query.Where(e => e.Level == level);

        if (SelectedCategoryFilter != "全部" && Enum.TryParse<AppLogCategory>(SelectedCategoryFilter, true, out AppLogCategory cat))
            query = query.Where(e => e.Category == cat);

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var keyword = SearchText;
            query = query.Where(e =>
                e.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                e.Details.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        FilteredEntries = new ObservableCollection<LogEntry>(query);
        TotalEntries = FilteredEntries.Count;

        // 加载历史文件时按文件内容统计；当前会话时统计仍由 WriteLog 累加，无需重算。
        if (LoadedSource != "当前会话" && _loadedFileEntries != null)
            RecomputeStats(_loadedFileEntries);
    }

    private List<LogEntry>? _loadedFileEntries;

    private bool MatchesFilter(LogEntry entry)
    {
        if (SelectedLevelFilter != "全部" && TryParseLevel(SelectedLevelFilter, out AppLogLevel level) && entry.Level != level)
            return false;
        if (SelectedCategoryFilter != "全部" && Enum.TryParse<AppLogCategory>(SelectedCategoryFilter, true, out AppLogCategory cat) && entry.Category != cat)
            return false;
        if (!string.IsNullOrWhiteSpace(SearchText) &&
            !entry.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase) &&
            !entry.Details.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    // ========== 历史文件列表 ==========

    private void RefreshLogFiles()
    {
        try
        {
            var files = Directory.GetFiles(LogDirectory, "*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(50)
                .Select(f => new LogFileInfo
                {
                    FileName = f.Name,
                    FilePath = f.FullName,
                    FileSize = f.Length,
                    LastModified = f.LastWriteTime
                })
                .ToList();

            LogFiles = new ObservableCollection<LogFileInfo>(files);
        }
        catch (Exception ex)
        {
            // 目录不可访问时直接写文件日志（避免递归调用 WriteLog 触发 UI 更新）
            System.Diagnostics.Debug.WriteLine($"[AppLog] 刷新日志文件列表失败: {ex.Message}");
        }
    }

    // ========== 命令 ==========

    [RelayCommand]
    private void OnBrowseDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择日志存储目录",
            InitialDirectory = LogDirectory
        };

        if (dialog.ShowDialog() == true)
        {
            LogDirectory = dialog.FolderName;
            _ = Directory.CreateDirectory(LogDirectory);
            OpenLogFile();
            WriteLog(AppLogLevel.Info, AppLogCategory.Config, "日志目录已更改", LogDirectory);
            RefreshLogFiles();
        }
    }

    [RelayCommand]
    private void OnOpenLogDirectory()
    {
        if (Directory.Exists(LogDirectory))
            _ = System.Diagnostics.Process.Start("explorer.exe", LogDirectory);
    }

    [RelayCommand]
    private void OnRefreshLogs()
    {
        if (LoadedSource != "当前会话")
            OnLoadCurrentSession();
        else
            ApplyFilter();

        RefreshLogFiles();
        StatusText = $"已刷新 - 共 {FilteredEntries.Count} 条记录";
    }

    [RelayCommand]
    private void OnLoadLogFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择日志文件",
            InitialDirectory = LogDirectory,
            Filter = "日志文件 (*.log)|*.log|所有文件|*.*",
            DefaultExt = ".log"
        };

        if (dialog.ShowDialog() == true)
            LoadFromFile(dialog.FileName);
    }

    [RelayCommand]
    private void OnLoadHistoryFile(LogFileInfo? fileInfo)
    {
        if (fileInfo?.FilePath is { Length: > 0 } path)
            LoadFromFile(path);
    }

    [RelayCommand]
    private void OnLoadCurrentSession()
    {
        _loadedFileEntries = null;
        LoadedSource = "当前会话";
        ApplyFilter();
        lock (_sessionEntries)
        {
            RecomputeStats(_sessionEntries);
        }

        StatusText = $"当前会话 - 共 {FilteredEntries.Count} 条记录";
    }

    [RelayCommand]
    private void OnExportLogs()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出日志",
            InitialDirectory = LogDirectory,
            Filter = "日志文件 (*.log)|*.log|CSV (*.csv)|*.csv",
            DefaultExt = ".log",
            FileName = $"export_{DateTime.Now:yyyyMMdd_HHmmss}"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            using var sw = new StreamWriter(dialog.FileName, false, Encoding.UTF8);

            if (ext == ".csv")
            {
                sw.WriteLine("序号,时间,级别,分类,消息,详情");
                foreach (LogEntry e in FilteredEntries)
                    sw.WriteLine($"{e.Index},\"{e.DateText}\",{e.LevelText},{e.CategoryText},\"{CsvEscape(e.Message)}\",\"{CsvEscape(e.Details)}\"");
            }
            else
            {
                foreach (LogEntry e in FilteredEntries)
                    sw.WriteLine(e.ToLogLine());
            }

            StatusText = $"已导出 {FilteredEntries.Count} 条到 {Path.GetFileName(dialog.FileName)}";
            WriteLog(AppLogLevel.Info, AppLogCategory.App, "日志已导出", dialog.FileName);
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败: {ex.Message}";
            WriteLog(AppLogLevel.Error, AppLogCategory.App, "日志导出失败", ex.Message);
        }
    }

    [RelayCommand]
    private void OnClearCurrentLogs()
    {
        lock (_sessionEntries)
        {
            _sessionEntries.Clear();
        }

        _loadedFileEntries = null;
        FilteredEntries.Clear();
        TotalEntries = 0;
        _logIndex = 0;
        DebugCount = InfoCount = WarningCount = ErrorCount = CriticalCount = 0;
        SessionStartTime = DateTime.Now;
        LoadedSource = "当前会话";
        StatusText = "已清空当前日志";
        WriteLog(AppLogLevel.Info, AppLogCategory.App, "会话日志已清空");
    }

    [RelayCommand]
    private void OnCleanOldFiles()
    {
        try
        {
            DateTime cutoff = DateTime.Now.AddDays(-RetentionDays);
            FileInfo[] files = Directory.GetFiles(LogDirectory, "*.log")
                .Select(f => new FileInfo(f))
                .Where(f => f.LastWriteTime < cutoff && f.FullName != _currentLogFilePath)
                .ToArray();

            foreach (FileInfo? f in files)
                f.Delete();

            RefreshLogFiles();
            StatusText = $"已清理 {files.Length} 个过期日志文件";
            WriteLog(AppLogLevel.Info, AppLogCategory.App, $"已清理 {files.Length} 个过期文件",
                $"保留天数: {RetentionDays}");
        }
        catch (Exception ex)
        {
            StatusText = $"清理失败: {ex.Message}";
            WriteLog(AppLogLevel.Error, AppLogCategory.App, "清理过期日志失败", ex.Message);
        }
    }

    // ========== 辅助 ==========

    private void LoadFromFile(string filePath)
    {
        try
        {
            var lines = File.ReadAllLines(filePath, Encoding.UTF8);
            var entries = new List<LogEntry>();
            int idx = 0;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var entry = LogEntry.FromLogLine(line, ++idx);
                if (entry != null)
                    entries.Add(entry);
            }

            _loadedFileEntries = entries;
            LoadedSource = Path.GetFileName(filePath);
            ApplyFilter();
            StatusText = $"已加载 {LoadedSource} - 共 {entries.Count} 条";
            WriteLog(AppLogLevel.Info, AppLogCategory.App, "加载历史日志文件", filePath);
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败: {ex.Message}";
            WriteLog(AppLogLevel.Error, AppLogCategory.App, "加载日志文件失败", ex.Message);
        }
    }

    private AppLogLevel GetMinLevel() => SelectedMinLevel switch
    {
        "Debug" => AppLogLevel.Debug,
        "Info" => AppLogLevel.Info,
        "Warning" => AppLogLevel.Warning,
        "Error" => AppLogLevel.Error,
        "Critical" => AppLogLevel.Critical,
        _ => AppLogLevel.Info
    };

    private static bool TryParseLevel(string s, out AppLogLevel level)
    {
        level = s switch
        {
            "Debug" => AppLogLevel.Debug,
            "Info" => AppLogLevel.Info,
            "Warning" => AppLogLevel.Warning,
            "Error" => AppLogLevel.Error,
            "Critical" => AppLogLevel.Critical,
            _ => AppLogLevel.Info
        };
        return s is "Debug" or "Info" or "Warning" or "Error" or "Critical";
    }

    private static string CsvEscape(string s) => s.Replace("\"", "\"\"");

    // ========== 统计 ==========

    private void IncrementStat(AppLogLevel level)
    {
        _ = (Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            switch (level)
            {
                case AppLogLevel.Debug: DebugCount++; break;
                case AppLogLevel.Info: InfoCount++; break;
                case AppLogLevel.Warning: WarningCount++; break;
                case AppLogLevel.Error: ErrorCount++; break;
                case AppLogLevel.Critical: CriticalCount++; break;
            }
        }));
    }

    private void RecomputeStats(IEnumerable<LogEntry> source)
    {
        int d = 0, i = 0, w = 0, e = 0, c = 0;
        foreach (LogEntry entry in source)
        {
            switch (entry.Level)
            {
                case AppLogLevel.Debug: d++; break;
                case AppLogLevel.Info: i++; break;
                case AppLogLevel.Warning: w++; break;
                case AppLogLevel.Error: e++; break;
                case AppLogLevel.Critical: c++; break;
            }
        }

        DebugCount = d;
        InfoCount = i;
        WarningCount = w;
        ErrorCount = e;
        CriticalCount = c;
    }

    public new void Dispose()
    {
        throw new NotImplementedException();
    }
}