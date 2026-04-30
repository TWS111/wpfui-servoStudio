// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using Microsoft.Win32;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.Services;

namespace Wpf.Ui.servoStudio.ViewModels.AppData;

/// <summary>
/// 用户配置页面 ViewModel —— 负责展示 / 修改 / 导入 / 导出 <see cref="UserSettings"/>。
/// 任意字段变更会立即通过 <see cref="UserSettingsService.Save"/> 持久化到本地 JSON。
/// </summary>
public partial class UserConfigViewModel : ViewModel
{
    private bool _isApplying;
    private bool _isInitialized;

    // ===== 外观 =====
    [ObservableProperty] private string _themeMode = "theme_light";

    // ===== 固件 (FoE) =====
    [ObservableProperty] private string _foeTargetFileName = string.Empty;
    [ObservableProperty] private string _foePasswordText = "0x";
    [ObservableProperty] private bool _isPreProcessedFirmware;

    // ===== 数据存储 =====
    [ObservableProperty] private bool _dataSaveAutoStart;
    [ObservableProperty] private string _dataSaveDirectory = string.Empty;
    [ObservableProperty] private string _dataSaveFormat = "CSV";
    [ObservableProperty] private int _dataSaveMaxFileSizeMB = 50;
    [ObservableProperty] private int _dataSavePollIntervalMs = 100;
    [ObservableProperty] private int _dataSaveMaxSampleCount = 5000;

    // ===== 设备连接 =====
    [ObservableProperty] private string _serialPortName = string.Empty;
    [ObservableProperty] private int _baudIndex = -1;
    [ObservableProperty] private int _dataBitIndex = -1;
    [ObservableProperty] private int _checkBitIndex = -1;
    [ObservableProperty] private int _stopBitIndex = -1;
    [ObservableProperty] private string _ethernetDeviceName = string.Empty;

    // ===== 运动配置 =====
    [ObservableProperty] private double _cyclicSendIntervalMs = 20;

    // ===== 应用日志 =====
    [ObservableProperty] private bool _appLogEnabled = true;
    [ObservableProperty] private string _appLogDirectory = string.Empty;
    [ObservableProperty] private string _appLogMinLevel = "Info";
    [ObservableProperty] private int _appLogMaxFileSizeMB = 10;
    [ObservableProperty] private int _appLogRetentionDays = 30;
    [ObservableProperty] private bool _appLogIsAutoScroll = true;

    // ===== 状态 =====
    [ObservableProperty] private string _settingsPath = string.Empty;
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private string _jsonPreview = string.Empty;

    public string[] ThemeOptions { get; } = ["theme_light", "theme_dark", "theme_system"];
    public string[] DataSaveFormatOptions { get; } = ["CSV", "TSV", "XLS", "JSON"];
    public string[] LogLevelOptions { get; } = ["Debug", "Info", "Warning", "Error", "Critical"];

    /// <summary>数据存储页面的变量集合（来自 <see cref="DataSaveViewModel"/>），可直接勾选；
    /// 修改后会由 DataSaveViewModel 自动持久化到 UserSettings.DataSave_SelectedVariables。</summary>
    public System.Collections.ObjectModel.ObservableCollection<DataVariableItem> DataVariables { get; }

    /// <summary>按 Group 分组后的变量视图，用于 XAML 显示组头。</summary>
    public ICollectionView DataVariablesView { get; }

    private readonly DataSaveViewModel _dataSaveViewModel;

    public UserConfigViewModel(DataSaveViewModel dataSaveViewModel)
    {
        _dataSaveViewModel = dataSaveViewModel;
        DataVariables = dataSaveViewModel.Variables;

        DataVariablesView = CollectionViewSource.GetDefaultView(DataVariables);
        DataVariablesView.GroupDescriptions.Clear();
        DataVariablesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DataVariableItem.Group)));

        // 变量被勾选后 DataSaveViewModel 会持久化；这里只需刷新 JSON 预览与状态。
        foreach (DataVariableItem v in DataVariables)
        {
            v.PropertyChanged += OnDataVariableChanged;
        }

        SettingsPath = UserSettingsService.GetSettingsPath();
        ReloadFromDisk();
        UserSettingsService.SettingsChanged += OnExternalSettingsChanged;
    }

    private void OnDataVariableChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DataVariableItem.IsSelected))
            return;

        // DataSaveViewModel 已经写盘，这里只刷新右侧 JSON 预览与状态栏
        JsonPreview = UserSettingsService.ToJson(UserSettingsService.Load());
        StatusText = $"已自动保存 · {DateTime.Now:HH:mm:ss}";
    }

    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            _isInitialized = true;
        }

        // 每次进入页面时刷新（其他页面可能已修改配置）
        ReloadFromDisk();
    }

    private void OnExternalSettingsChanged(object? sender, UserSettings settings)
    {
        // 防止在当前页正在触发 Save 时重入
        if (_isApplying)
            return;

        _ = (Application.Current?.Dispatcher?.BeginInvoke(() => ApplySettings(settings)));
    }

    private void ReloadFromDisk()
    {
        ApplySettings(UserSettingsService.Load());
    }

    private void ApplySettings(UserSettings s)
    {
        _isApplying = true;
        try
        {
            ThemeMode = s.ThemeMode;

            FoeTargetFileName = s.FoeTargetFileName;
            FoePasswordText = s.FoePasswordText;
            IsPreProcessedFirmware = s.IsPreProcessedFirmware;

            DataSaveAutoStart = s.DataSave_AutoStart;
            DataSaveDirectory = s.DataSave_Directory;
            DataSaveFormat = s.DataSave_Format;
            DataSaveMaxFileSizeMB = s.DataSave_MaxFileSizeMB;
            DataSavePollIntervalMs = s.DataSave_PollIntervalMs;
            DataSaveMaxSampleCount = s.DataSave_MaxSampleCount;

            SerialPortName = s.DeviceAdd_SerialPortName;
            BaudIndex = s.DeviceAdd_BaudIndex;
            DataBitIndex = s.DeviceAdd_DataBitIndex;
            CheckBitIndex = s.DeviceAdd_CheckBitIndex;
            StopBitIndex = s.DeviceAdd_StopBitIndex;
            EthernetDeviceName = s.DeviceAdd_EthernetDeviceName;

            CyclicSendIntervalMs = s.Motion_CyclicSendIntervalMs;

            AppLogEnabled = s.AppLog_IsEnabled;
            AppLogDirectory = s.AppLog_Directory;
            AppLogMinLevel = s.AppLog_MinLevel;
            AppLogMaxFileSizeMB = s.AppLog_MaxFileSizeMB;
            AppLogRetentionDays = s.AppLog_RetentionDays;
            AppLogIsAutoScroll = s.AppLog_IsAutoScroll;

            JsonPreview = UserSettingsService.ToJson(s);
        }
        finally
        {
            _isApplying = false;
        }
    }

    private void PersistFromViewModel()
    {
        if (_isApplying)
            return;

        UserSettings s = UserSettingsService.Load();

        s.ThemeMode = ThemeMode;

        s.FoeTargetFileName = FoeTargetFileName;
        s.FoePasswordText = FoePasswordText;
        s.IsPreProcessedFirmware = IsPreProcessedFirmware;

        s.DataSave_AutoStart = DataSaveAutoStart;
        s.DataSave_Directory = DataSaveDirectory;
        s.DataSave_Format = DataSaveFormat;
        s.DataSave_MaxFileSizeMB = DataSaveMaxFileSizeMB;
        s.DataSave_PollIntervalMs = DataSavePollIntervalMs;
        s.DataSave_MaxSampleCount = DataSaveMaxSampleCount;

        s.DeviceAdd_SerialPortName = SerialPortName;
        s.DeviceAdd_BaudIndex = BaudIndex;
        s.DeviceAdd_DataBitIndex = DataBitIndex;
        s.DeviceAdd_CheckBitIndex = CheckBitIndex;
        s.DeviceAdd_StopBitIndex = StopBitIndex;
        s.DeviceAdd_EthernetDeviceName = EthernetDeviceName;

        s.Motion_CyclicSendIntervalMs = CyclicSendIntervalMs;

        s.AppLog_IsEnabled = AppLogEnabled;
        s.AppLog_Directory = AppLogDirectory;
        s.AppLog_MinLevel = AppLogMinLevel;
        s.AppLog_MaxFileSizeMB = AppLogMaxFileSizeMB;
        s.AppLog_RetentionDays = AppLogRetentionDays;
        s.AppLog_IsAutoScroll = AppLogIsAutoScroll;

        _isApplying = true;
        try
        {
            UserSettingsService.Save(s);
        }
        finally
        {
            _isApplying = false;
        }

        JsonPreview = UserSettingsService.ToJson(s);
        StatusText = $"已自动保存 · {DateTime.Now:HH:mm:ss}";
    }

    // ===== 每个字段变化后自动写盘 =====
    partial void OnThemeModeChanged(string value)
    {
        PersistFromViewModel();
        ApplyThemeLive(value);
    }

    private void ApplyThemeLive(string mode)
    {
        if (_isApplying)
            return;

        try
        {
            switch (mode)
            {
                case "theme_dark":
                    StopSystemThemeWatch();
                    Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
                    break;
                case "theme_system":
                    if (Application.Current?.MainWindow is { } mainWindow)
                    {
                        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(mainWindow);
                    }

                    Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
                    break;
                default:
                    StopSystemThemeWatch();
                    Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
                    break;
            }
        }
        catch
        {
            // 主题应用失败不影响其他配置保存
        }
    }

    private static void StopSystemThemeWatch()
    {
        try
        {
            if (Application.Current?.MainWindow is { } mainWindow)
            {
                Wpf.Ui.Appearance.SystemThemeWatcher.UnWatch(mainWindow);
            }
        }
        catch
        {
            // ignore
        }
    }

    partial void OnFoeTargetFileNameChanged(string value) => PersistFromViewModel();
    partial void OnFoePasswordTextChanged(string value) => PersistFromViewModel();
    partial void OnIsPreProcessedFirmwareChanged(bool value) => PersistFromViewModel();

    partial void OnDataSaveAutoStartChanged(bool value) => PersistFromViewModel();
    partial void OnDataSaveDirectoryChanged(string value) => PersistFromViewModel();
    partial void OnDataSaveFormatChanged(string value) => PersistFromViewModel();
    partial void OnDataSaveMaxFileSizeMBChanged(int value) => PersistFromViewModel();
    partial void OnDataSavePollIntervalMsChanged(int value) => PersistFromViewModel();
    partial void OnDataSaveMaxSampleCountChanged(int value) => PersistFromViewModel();

    partial void OnSerialPortNameChanged(string value) => PersistFromViewModel();
    partial void OnBaudIndexChanged(int value) => PersistFromViewModel();
    partial void OnDataBitIndexChanged(int value) => PersistFromViewModel();
    partial void OnCheckBitIndexChanged(int value) => PersistFromViewModel();
    partial void OnStopBitIndexChanged(int value) => PersistFromViewModel();
    partial void OnEthernetDeviceNameChanged(string value) => PersistFromViewModel();

    partial void OnCyclicSendIntervalMsChanged(double value) => PersistFromViewModel();

    partial void OnAppLogEnabledChanged(bool value) => PersistFromViewModel();
    partial void OnAppLogDirectoryChanged(string value) => PersistFromViewModel();
    partial void OnAppLogMinLevelChanged(string value) => PersistFromViewModel();
    partial void OnAppLogMaxFileSizeMBChanged(int value) => PersistFromViewModel();
    partial void OnAppLogRetentionDaysChanged(int value) => PersistFromViewModel();
    partial void OnAppLogIsAutoScrollChanged(bool value) => PersistFromViewModel();

    // ===== 命令 =====

    [RelayCommand]
    private void OnReload()
    {
        ReloadFromDisk();
        StatusText = $"已从磁盘重新加载 · {DateTime.Now:HH:mm:ss}";
    }

    [RelayCommand]
    private void OnResetToDefault()
    {
        // 保留不应随"恢复默认"而丢失的数据：
        //   - 数据存储的变量勾选（组 + 变量列表）
        //   - 页面访问计数
        UserSettings current = UserSettingsService.Load();
        var defaults = new UserSettings
        {
            DataSave_HasVariableSelection = current.DataSave_HasVariableSelection,
            DataSave_SelectedVariables = current.DataSave_SelectedVariables ?? new(),
            PageVisitCounts = current.PageVisitCounts ?? new(),
        };

        UserSettingsService.Save(defaults);
        ApplySettings(defaults);
        ApplyThemeLive(defaults.ThemeMode);
        StatusText = "已重置为默认配置（保留已勾选的数据存储变量）";
    }

    [RelayCommand]
    private void OnOpenInExplorer()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{SettingsPath}\""
                });
            }
        }
        catch
        {
            // 忽略
        }
    }

    [RelayCommand]
    private void OnImport()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入用户配置 JSON",
            Filter = "JSON 配置文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
            return;

        if (UserSettingsService.ImportFromFile(dialog.FileName, out UserSettings? imported) && imported != null)
        {
            ApplySettings(imported);
            ApplyThemeLive(imported.ThemeMode);
            StatusText = $"已从 {Path.GetFileName(dialog.FileName)} 导入配置";
        }
        else
        {
            StatusText = "导入失败：文件格式无效或无法读取";
        }
    }

    [RelayCommand]
    private void OnExport()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出用户配置 JSON",
            Filter = "JSON 配置文件 (*.json)|*.json",
            FileName = $"ServoStudio-Settings-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            AddExtension = true,
            DefaultExt = ".json"
        };

        if (dialog.ShowDialog() != true)
            return;

        if (UserSettingsService.ExportToFile(dialog.FileName))
        {
            StatusText = $"已导出至 {dialog.FileName}";
        }
        else
        {
            StatusText = "导出失败";
        }
    }
}