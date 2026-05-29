// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Windows.Threading;
using Core.Net.EtherCAT;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.Services;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels.AppData;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable")]
public sealed partial class DataSaveViewModel : ViewModel, IDisposable
{
    private readonly DeviceAddViewModel _deviceAddViewModel;
    private readonly Lock _sampleLock = new();
    private readonly Queue<DateTime> _sampleMarkers = [];
    private readonly DispatcherTimer _connectionTimer;
    private CancellationTokenSource? _samplingCts;
    private bool _isLoadingSettings;
    private bool _disposed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartSamplingCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopSamplingCommand))]
    private bool _isSampling;

    [ObservableProperty]
    private bool _isStorageEnabled;

    [ObservableProperty]
    private string _connectionInfo = "设备未连接";

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private int _sampleIntervalMs = 100;

    [ObservableProperty]
    private int _maxSampleCount = 5_000;

    [ObservableProperty]
    private int _maxFileSizeMb = 50;

    [ObservableProperty]
    private string _selectedFormat = "CSV";

    [ObservableProperty]
    private string _outputDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ServoStudio",
        "Data");

    [ObservableProperty]
    private string _testDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ServoStudio",
        "TestData");

    [ObservableProperty]
    private bool _isDebugMode;

    [ObservableProperty]
    private string _currentFileName = "未启用";

    [ObservableProperty]
    private int _sampleCount;

    [ObservableProperty]
    private ObservableCollection<DataVariableItem> _variables = [];

    // ===== 调试：测试数据生成 =====
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateTestDataCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelTestDataCommand))]
    private bool _isGeneratingTestData;

    [ObservableProperty]
    private double _testDataProgress;

    [ObservableProperty]
    private string _testDataProgressText = string.Empty;

    private CancellationTokenSource? _testDataCts;

    public IReadOnlyList<string> FormatOptions { get; } = ["CSV", "TSV", "XLS", "JSON"];

    public override void OnNavigatedTo()
    {
        // 进入页面时从磁盘重读一次，保证其他页面（如 UserConfigPage）修改后的值能及时反映。
        LoadSettings();
        RefreshConnectionInfo();
        UpdateLoggerStatusText();
    }

    public DataSaveViewModel(DeviceAddViewModel deviceAddViewModel)
    {
        _deviceAddViewModel = deviceAddViewModel;
        Variables = new ObservableCollection<DataVariableItem>(BuildVariables());

        foreach (DataVariableItem variable in Variables)
        {
            variable.PropertyChanged += OnVariablePropertyChanged;
        }

        DataFrameLogger.Instance.FileRotated += OnFileRotated;

        _connectionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _connectionTimer.Tick += (_, _) => RefreshConnectionInfo();
        _connectionTimer.Start();

        LoadSettings();
        RefreshConnectionInfo();
        RefreshSelectedFilter();
        UpdateLoggerStatusText();

        // 其他页面（如 UserConfigPage）修改同一份 JSON 后，同步到本 VM。
        UserSettingsService.SettingsChanged += OnExternalSettingsChanged;
    }

    private void OnExternalSettingsChanged(object? sender, UserSettings settings)
    {
        // 避免在下一次 SaveSettings 里重入：LoadSettings 本身会押 _isLoadingSettings 标志。
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            _ = dispatcher.BeginInvoke(LoadSettings);
        }
        else
        {
            LoadSettings();
        }
    }

    partial void OnIsStorageEnabledChanged(bool value)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        ApplyStorageState();
        SaveSettings();
    }

    partial void OnSampleIntervalMsChanged(int value)
    {
        if (value < 10)
        {
            SampleIntervalMs = 10;
            return;
        }

        SaveSettings();
    }

    partial void OnMaxSampleCountChanged(int value)
    {
        if (value < 100)
        {
            MaxSampleCount = 100;
            return;
        }

        TrimSampleBuffer();
        SaveSettings();
    }

    partial void OnMaxFileSizeMbChanged(int value)
    {
        if (value < 1)
        {
            MaxFileSizeMb = 1;
            return;
        }

        SaveSettings();

        if (IsStorageEnabled)
        {
            ApplyStorageState();
        }
    }

    partial void OnSelectedFormatChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            SelectedFormat = "CSV";
            return;
        }

        SaveSettings();

        if (IsStorageEnabled)
        {
            ApplyStorageState();
        }
    }

    partial void OnOutputDirectoryChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            OutputDirectory = GetDefaultDirectory();
            return;
        }

        SaveSettings();

        if (IsStorageEnabled)
        {
            ApplyStorageState();
        }
    }

    partial void OnTestDataDirectoryChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            TestDataDirectory = GetDefaultTestDataDirectory();
            return;
        }

        SaveSettings();
    }

    [RelayCommand(CanExecute = nameof(CanStartSampling))]
    private void OnStartSampling()
    {
        if (IsSampling)
        {
            return;
        }

        _samplingCts = new CancellationTokenSource();
        IsSampling = true;
        StatusText = IsStorageEnabled ? "正在采样并写入本地文件" : "正在采样（未写入本地文件）";

        _ = Task.Run(() => SamplingLoopAsync(_samplingCts.Token));
    }

    private bool CanStartSampling() => !IsSampling;

    [RelayCommand(CanExecute = nameof(CanStopSampling))]
    private void OnStopSampling()
    {
        if (!IsSampling)
        {
            return;
        }

        _samplingCts?.Cancel();
        _samplingCts = null;
        IsSampling = false;
        UpdateLoggerStatusText("采样已停止");
    }

    private bool CanStopSampling() => IsSampling;

    [RelayCommand]
    private async Task OnSnapshotOnce()
    {
        int count = await SampleSelectedVariablesAsync(CancellationToken.None);
        if (count > 0)
        {
            StatusText = IsStorageEnabled
                ? $"单次采集完成，已记录 {count} 条并写入本地文件"
                : $"单次采集完成，已记录 {count} 条（未写入本地文件）";
            return;
        }

        if (!Variables.Any(v => v.IsSelected))
        {
            StatusText = "请先选择要采集的变量";
            return;
        }

        StatusText = _deviceAddViewModel.IsAnyConnected
            ? "本次未采集到有效数据"
            : "设备未连接，仅主机变量可采集";
    }

    [RelayCommand]
    private void OnClearSamples()
    {
        lock (_sampleLock)
        {
            _sampleMarkers.Clear();
            SampleCount = 0;
        }

        foreach (DataVariableItem variable in Variables)
        {
            variable.LastValue = string.Empty;
        }

        UpdateLoggerStatusText("采样统计已清空");
    }

    [RelayCommand]
    private void OnSelectAll()
    {
        SetSelection(item => true);
    }

    [RelayCommand]
    private void OnSelectNone()
    {
        SetSelection(item => false);
    }

    [RelayCommand]
    private void OnSelectSlaveOnly()
    {
        SetSelection(item => item.Source == DataVariableSource.Slave);
    }

    [RelayCommand]
    private void OnSelectMasterOnly()
    {
        SetSelection(item => item.Source == DataVariableSource.Master);
    }

    [RelayCommand]
    private void OnBrowseDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择存储目录",
            InitialDirectory = Directory.Exists(OutputDirectory)
                ? OutputDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        bool? result = dialog.ShowDialog();
        if (result == true)
        {
            OutputDirectory = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void OnBrowseTestDataDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择测试数据保存目录",
            InitialDirectory = Directory.Exists(TestDataDirectory)
                ? TestDataDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        bool? result = dialog.ShowDialog();
        if (result == true)
        {
            TestDataDirectory = dialog.FolderName;
        }
    }

    private async Task SamplingLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int sampledCount = await SampleSelectedVariablesAsync(cancellationToken);
                if (sampledCount == 0 && !Variables.Any(v => v.IsSelected))
                {
                    _ = Application.Current.Dispatcher.Invoke(() => StatusText = "请先选择要采集的变量");
                }

                await Task.Delay(SampleIntervalMs, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsSampling = false;
                StatusText = $"采样异常: {ex.Message}";
            });
            AppLogViewModel.Log(AppLogLevel.Warning, AppLogCategory.System, "数据采样异常", ex.Message);
        }
    }

    private async Task<int> SampleSelectedVariablesAsync(CancellationToken cancellationToken)
    {
        DataVariableItem[] selectedVariables = Variables.Where(v => v.IsSelected).ToArray();
        if (selectedVariables.Length == 0)
        {
            return 0;
        }

        int count = 0;
        int currentSampleCount = SampleCount;
        List<(DataVariableItem Variable, string Value)> lastValues = new(selectedVariables.Length);

        await Task.Run(() =>
        {
            foreach (DataVariableItem variable in selectedVariables)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryGetCurrentValue(variable, out string valueText))
                {
                    continue;
                }

                lastValues.Add((variable, valueText));
                currentSampleCount = RegisterSample();
                count++;

                if (IsStorageEnabled)
                {
                    WriteToLocalFile(variable, valueText);
                }
            }
        }, cancellationToken);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            SampleCount = currentSampleCount;

            foreach ((DataVariableItem Variable, string Value) item in lastValues)
            {
                item.Variable.LastValue = item.Value;
            }

            if (count > 0 && IsSampling)
            {
                StatusText = IsStorageEnabled
                    ? $"采样中，累计 {SampleCount} 条，写入文件 {CurrentFileName}"
                    : $"采样中，累计 {SampleCount} 条，当前未启用本地存储";
            }
        });

        return count;
    }

    private bool TryGetCurrentValue(DataVariableItem variable, out string valueText)
    {
        valueText = string.Empty;

        if (variable.Source == DataVariableSource.Master)
        {
            object? raw = variable.ValueProvider?.Invoke();
            valueText = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;
            return true;
        }

        IServoAxis? axis = _deviceAddViewModel.ActiveAxis;
        if (!_deviceAddViewModel.IsAnyConnected || axis == null)
        {
            return false;
        }

        if (!_deviceAddViewModel.ActiveServoMaster.TryReadSDO(axis.SlaveAddr, variable.SdoIndex, variable.SdoSubIndex, out ushort rawValue))
        {
            return false;
        }

        valueText = rawValue.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private void WriteToLocalFile(DataVariableItem variable, string valueText)
    {
        if (variable.Source == DataVariableSource.Master)
        {
            FrameLogBridge.LogMasterFrame(
                variable.Group,
                variable.Name,
                variable.SdoIndex,
                variable.SdoSubIndex,
                variable.DataType,
                valueText,
                variable.Unit);
            return;
        }

        FrameLogBridge.LogSlaveFrame(
            variable.Group,
            variable.Name,
            variable.SdoIndex,
            variable.SdoSubIndex,
            variable.DataType,
            valueText,
            variable.Unit);
    }

    private int RegisterSample()
    {
        lock (_sampleLock)
        {
            _sampleMarkers.Enqueue(DateTime.Now);
            while (_sampleMarkers.Count > MaxSampleCount)
            {
                _ = _sampleMarkers.Dequeue();
            }

            return _sampleMarkers.Count;
        }
    }

    private void TrimSampleBuffer()
    {
        lock (_sampleLock)
        {
            while (_sampleMarkers.Count > MaxSampleCount)
            {
                _ = _sampleMarkers.Dequeue();
            }

            SampleCount = _sampleMarkers.Count;
        }
    }

    private void LoadSettings()
    {
        _isLoadingSettings = true;

        try
        {
            UserSettings settings = UserSettingsService.Load();

            SampleIntervalMs = Math.Max(10, settings.DataSave_PollIntervalMs);
            MaxSampleCount = Math.Max(100, settings.DataSave_MaxSampleCount);
            MaxFileSizeMb = Math.Max(1, settings.DataSave_MaxFileSizeMB);
            SelectedFormat = NormalizeFormat(settings.DataSave_Format);
            OutputDirectory = string.IsNullOrWhiteSpace(settings.DataSave_Directory)
                ? GetDefaultDirectory()
                : settings.DataSave_Directory;
            TestDataDirectory = string.IsNullOrWhiteSpace(settings.DataSave_TestDataDirectory)
                ? GetDefaultTestDataDirectory()
                : settings.DataSave_TestDataDirectory;

            IsDebugMode = settings.IsDebugMode;

            HashSet<string> selectedVariables = new(settings.DataSave_SelectedVariables ?? [], StringComparer.Ordinal);
            foreach (DataVariableItem variable in Variables)
            {
                variable.IsSelected = settings.DataSave_HasVariableSelection
                    ? selectedVariables.Contains(variable.FullName)
                    : variable.Source == DataVariableSource.Slave;
            }

            IsStorageEnabled = settings.DataSave_AutoStart;
        }
        finally
        {
            _isLoadingSettings = false;
        }

        ApplyStorageState();
    }

    private void SaveSettings()
    {
        if (_isLoadingSettings)
        {
            return;
        }

        UserSettings settings = UserSettingsService.Load();
        settings.DataSave_AutoStart = IsStorageEnabled;
        settings.DataSave_Directory = NormalizeDirectory(OutputDirectory);
        settings.DataSave_Format = NormalizeFormat(SelectedFormat);
        settings.DataSave_MaxFileSizeMB = Math.Max(1, MaxFileSizeMb);
        settings.DataSave_PollIntervalMs = Math.Max(10, SampleIntervalMs);
        settings.DataSave_MaxSampleCount = Math.Max(100, MaxSampleCount);
        settings.DataSave_HasVariableSelection = true;
        settings.DataSave_SelectedVariables = Variables
            .Where(v => v.IsSelected)
            .Select(v => v.FullName)
            .ToList();
        settings.DataSave_TestDataDirectory = string.IsNullOrWhiteSpace(TestDataDirectory)
            ? string.Empty
            : TestDataDirectory.Trim();
        UserSettingsService.Save(settings);
    }

    private void ApplyStorageState()
    {
        RefreshSelectedFilter();

        if (!IsStorageEnabled)
        {
            FrameLogBridge.IsEnabled = false;
            DataFrameLogger.Instance.Stop();
            CurrentFileName = "未启用";

            if (!IsSampling)
            {
                StatusText = "本地存储已关闭";
            }

            return;
        }

        DataFrameLogger.Instance.Start(
            NormalizeDirectory(OutputDirectory),
            ParseFormat(SelectedFormat),
            Math.Max(1, MaxFileSizeMb) * 1024L * 1024L);

        FrameLogBridge.IsEnabled = true;
        CurrentFileName = string.IsNullOrWhiteSpace(DataFrameLogger.Instance.CurrentFilePath)
            ? "正在创建文件..."
            : Path.GetFileName(DataFrameLogger.Instance.CurrentFilePath);

        if (!IsSampling)
        {
            StatusText = $"本地存储已启用，当前文件: {CurrentFileName}";
        }
    }

    private void UpdateLoggerStatusText(string? fallback = null)
    {
        if (IsSampling)
        {
            return;
        }

        StatusText = fallback ?? (IsStorageEnabled
            ? $"本地存储已启用，当前文件: {CurrentFileName}"
            : "本地存储已关闭");
    }

    private void RefreshConnectionInfo()
    {
        if (!_deviceAddViewModel.IsAnyConnected || _deviceAddViewModel.ActiveAxis == null)
        {
            ConnectionInfo = "设备未连接";
            return;
        }

        ushort slaveAddr = (ushort)_deviceAddViewModel.ActiveAxis.SlaveAddr;
        string nic = string.IsNullOrWhiteSpace(_deviceAddViewModel.SelectedEthernetName)
            ? "未指定网卡"
            : _deviceAddViewModel.SelectedEthernetName;
        string state = string.IsNullOrWhiteSpace(_deviceAddViewModel.EthernetSlaveStateInfo)
            ? "状态未知"
            : _deviceAddViewModel.EthernetSlaveStateInfo;

        ConnectionInfo = $"已连接 从站 {slaveAddr} | 网卡 {nic} | 状态 {state}";
    }

    private void RefreshSelectedFilter()
    {
        FrameLogBridge.SetFilter(Variables.Where(v => v.IsSelected).Select(v => v.FullName));
    }

    private void SetSelection(Func<DataVariableItem, bool> predicate)
    {
        foreach (DataVariableItem variable in Variables)
        {
            variable.IsSelected = predicate(variable);
        }

        RefreshSelectedFilter();
        SaveSettings();
    }

    private void OnVariablePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DataVariableItem.IsSelected))
        {
            return;
        }

        RefreshSelectedFilter();
        SaveSettings();
    }

    private void OnFileRotated(string filePath)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            CurrentFileName = Path.GetFileName(filePath);
            if (!IsSampling)
            {
                StatusText = $"本地存储已启用，当前文件: {CurrentFileName}";
            }
        });
    }

    private IEnumerable<DataVariableItem> BuildVariables()
    {
        yield return new DataVariableItem
        {
            Group = "主机状态",
            Name = "EtherCAT已连接",
            Source = DataVariableSource.Master,
            DataType = "bool",
            ValueProvider = () => _deviceAddViewModel.IsAnyConnected
        };

        yield return new DataVariableItem
        {
            Group = "主机状态",
            Name = "当前网卡",
            Source = DataVariableSource.Master,
            DataType = "string",
            ValueProvider = () => _deviceAddViewModel.SelectedEthernetName
        };

        yield return new DataVariableItem
        {
            Group = "主机状态",
            Name = "从站状态",
            Source = DataVariableSource.Master,
            DataType = "string",
            ValueProvider = () => _deviceAddViewModel.EthernetSlaveStateInfo
        };

        foreach (HRegisterEntry entry in HVariables.RegisterTable)
        {
            // 在当前活动协议栈下被禁用的寄存器不出现在数据保存清单中
            if (RegisterDisableService.IsDisabledForActive(entry.SdoIndex, entry.SdoSubIndex)) continue;
            yield return new DataVariableItem
            {
                Group = string.IsNullOrWhiteSpace(entry.GroupName) ? "从机参数" : entry.GroupName,
                Name = entry.ParameterName,
                Source = DataVariableSource.Slave,
                SdoIndex = entry.SdoIndex,
                SdoSubIndex = entry.SdoSubIndex,
                DataType = "uint16",
                Unit = entry.Unit ?? string.Empty,
            };
        }
    }

    private static string GetDefaultDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ServoStudio",
            "Data");
    }

    private static string GetDefaultTestDataDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ServoStudio",
            "TestData");
    }

    private static string NormalizeDirectory(string directory)
    {
        return string.IsNullOrWhiteSpace(directory) ? GetDefaultDirectory() : directory.Trim();
    }

    private static string NormalizeFormat(string? format)
    {
        return format?.Trim().ToUpperInvariant() switch
        {
            "TSV" => "TSV",
            "XLS" => "XLS",
            "JSON" or "JSONL" => "JSON",
            _ => "CSV",
        };
    }

    private static DataSaveFormat ParseFormat(string? format)
    {
        return NormalizeFormat(format) switch
        {
            "TSV" => DataSaveFormat.Tsv,
            "XLS" => DataSaveFormat.Xls,
            "JSON" => DataSaveFormat.Json,
            _ => DataSaveFormat.Csv,
        };
    }

    // ============================================================
    // 调试：测试数据生成
    // ============================================================

    private bool CanGenerateTestData() => !IsGeneratingTestData;

    private bool CanCancelTestData() => IsGeneratingTestData;

    [RelayCommand(CanExecute = nameof(CanCancelTestData))]
    private void OnCancelTestData()
    {
        try { _testDataCts?.Cancel(); } catch { }
    }

    /// <summary>
    /// 生成符合 <see cref="DataFrameLogger"/> 写入格式的随机测试用例。
    /// 多组规模 × 全部支持的格式（CSV/TSV/XLS/JSON），同一变量相邻行采用随机游走。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGenerateTestData))]
    private async Task OnGenerateTestDataAsync()
    {
        if (IsGeneratingTestData)
        {
            return;
        }

        // 频道列表：优先使用当前选中的变量；都没勾时回退为全部变量。
        var channels = Variables.Where(v => v.IsSelected).ToList();
        if (channels.Count == 0)
        {
            channels = [.. Variables];
        }

        if (channels.Count == 0)
        {
            StatusText = "无可用变量，无法生成测试数据。";
            return;
        }

        int[] scales = [100, 1_000, 10_000, 100_000];
        DataSaveFormat[] formats =
        [
            DataSaveFormat.Csv,
            DataSaveFormat.Tsv,
            DataSaveFormat.Xls,
            DataSaveFormat.Json,
        ];

        // 输出到 TestDataDirectory 下的带时间戳子目录，与正常采样目录区分。
        string baseDir = string.IsNullOrWhiteSpace(TestDataDirectory) ? GetDefaultTestDataDirectory() : TestDataDirectory;
        string outDir = Path.Combine(baseDir, $"TestData_{DateTime.Now:yyyyMMdd_HHmmss}");
        try { _ = Directory.CreateDirectory(outDir); }
        catch (Exception ex)
        {
            StatusText = $"无法创建测试目录: {ex.Message}";
            return;
        }

        IsGeneratingTestData = true;
        TestDataProgress = 0;
        TestDataProgressText = "准备中…";

        _testDataCts?.Dispose();
        _testDataCts = new CancellationTokenSource();
        var ct = _testDataCts.Token;

        // 总记录数 = Σ(scale) × channels × formats —— 用于精细化进度。
        long totalRows = (long)scales.Sum() * channels.Count * formats.Length;
        long writtenRows = 0;
        int fileIndex = 0;
        int fileTotal = scales.Length * formats.Length;

        try
        {
            await Task.Run(
                () =>
                {
                    var rng = new Random(0xC0FFEE);

                    foreach (int rows in scales)
                    {
                        // 每个 scale 都重新初始化通道当前值，保证相邻连续。
                        double[] state = new double[channels.Count];
                        for (int i = 0; i < channels.Count; i++)
                        {
                            state[i] = (rng.NextDouble() - 0.5) * 100.0;
                        }

                        foreach (DataSaveFormat fmt in formats)
                        {
                            ct.ThrowIfCancellationRequested();
                            fileIndex++;

                            string ext = fmt switch
                            {
                                DataSaveFormat.Csv => "csv",
                                DataSaveFormat.Tsv => "tsv",
                                DataSaveFormat.Xls => "xls",
                                DataSaveFormat.Json => "jsonl",
                                _ => "csv",
                            };

                            string path = Path.Combine(
                                outDir,
                                $"test_{rows:D7}rows_{fmt.ToString().ToLowerInvariant()}.{ext}");

                            ReportProgress(
                                writtenRows,
                                totalRows,
                                $"({fileIndex}/{fileTotal}) {Path.GetFileName(path)}");

                            // 每个文件独立的 state 副本：保证不同格式生成"相同"波形便于对比。
                            double[] localState = (double[])state.Clone();
                            long fileWritten = WriteTestFile(path, fmt, rows, channels, localState, rng, ct,
                                onRowsWritten: delta =>
                                {
                                    long current = Interlocked.Add(ref writtenRows, delta);
                                    // 每 ~1% 或最后一行刷新 UI，避免在百万行场景下抖动。
                                    if (current == totalRows
                                        || (current % Math.Max(1, totalRows / 200)) < delta)
                                    {
                                        ReportProgress(
                                            current,
                                            totalRows,
                                            $"({fileIndex}/{fileTotal}) {Path.GetFileName(path)}");
                                    }
                                });
                            _ = fileWritten;
                        }
                    }
                },
                ct);

            ReportProgress(totalRows, totalRows, $"已生成 {fileTotal} 个文件 → {outDir}");
            StatusText = $"测试数据生成完成：{fileTotal} 个文件位于 {outDir}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "测试数据生成已取消。";
        }
        catch (Exception ex)
        {
            StatusText = $"测试数据生成失败：{ex.Message}";
        }
        finally
        {
            IsGeneratingTestData = false;
        }
    }

    private void ReportProgress(long current, long total, string text)
    {
        double pct = total <= 0 ? 0 : (double)current * 100.0 / total;
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() =>
            {
                TestDataProgress = pct;
                TestDataProgressText = text;
            });
        }
        else
        {
            TestDataProgress = pct;
            TestDataProgressText = text;
        }
    }

    private static long WriteTestFile(
        string path,
        DataSaveFormat fmt,
        int rows,
        IReadOnlyList<DataVariableItem> channels,
        double[] state,
        Random rng,
        CancellationToken ct,
        Action<long> onRowsWritten)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(true)) { AutoFlush = false };

        // ----- Header -----
        // 表头紧随其后是一行 "#TYPE,..." 类型行，标识每列的逻辑数据类型，
        // 与 DataFrameLogger.WriteHeader 输出格式一致；DataViewViewModel 解析时会跳过。
        switch (fmt)
        {
            case DataSaveFormat.Csv:
                writer.WriteLine("Timestamp,Source,Group,Name,Index,SubIndex,DataType,Value,Unit");
                writer.WriteLine("#TYPE,String,String,String,Hex16,UInt8,String,Variant,String");
                break;
            case DataSaveFormat.Tsv:
                writer.WriteLine("Timestamp\tSource\tGroup\tName\tIndex\tSubIndex\tDataType\tValue\tUnit");
                writer.WriteLine("#TYPE\tString\tString\tString\tHex16\tUInt8\tString\tVariant\tString");
                break;
            case DataSaveFormat.Xls:
                writer.WriteLine("<?xml version=\"1.0\"?>");
                writer.WriteLine("<?mso-application progid=\"Excel.Sheet\"?>");
                writer.WriteLine("<Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\" xmlns:ss=\"urn:schemas-microsoft-com:office:spreadsheet\">");
                writer.WriteLine("  <Worksheet ss:Name=\"Frames\"><Table>");
                writer.WriteLine("    <Row><Cell><Data ss:Type=\"String\">Timestamp</Data></Cell><Cell><Data ss:Type=\"String\">Source</Data></Cell><Cell><Data ss:Type=\"String\">Group</Data></Cell><Cell><Data ss:Type=\"String\">Name</Data></Cell><Cell><Data ss:Type=\"String\">Index</Data></Cell><Cell><Data ss:Type=\"String\">SubIndex</Data></Cell><Cell><Data ss:Type=\"String\">DataType</Data></Cell><Cell><Data ss:Type=\"String\">Value</Data></Cell><Cell><Data ss:Type=\"String\">Unit</Data></Cell></Row>");
                writer.WriteLine("    <Row><Cell><Data ss:Type=\"String\">#TYPE</Data></Cell><Cell><Data ss:Type=\"String\">String</Data></Cell><Cell><Data ss:Type=\"String\">String</Data></Cell><Cell><Data ss:Type=\"String\">String</Data></Cell><Cell><Data ss:Type=\"String\">Hex16</Data></Cell><Cell><Data ss:Type=\"String\">UInt8</Data></Cell><Cell><Data ss:Type=\"String\">String</Data></Cell><Cell><Data ss:Type=\"String\">Variant</Data></Cell><Cell><Data ss:Type=\"String\">String</Data></Cell></Row>");
                break;
            case DataSaveFormat.Json:
                break;
        }

        DateTime t0 = DateTime.Now;
        long written = 0;
        const long batchSize = 1000;
        long batchAccum = 0;
        var sb = new StringBuilder(512);

        for (int i = 0; i < rows; i++)
        {
            if ((i & 0xFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            // 每行时间戳：1ms 步长（仅作示例，便于在 ScottPlot 看到顺序）。
            DateTime ts = t0.AddMilliseconds(i);
            string tsText = ts.ToString("yyyy-MM-dd HH:mm:ss.fff");

            for (int c = 0; c < channels.Count; c++)
            {
                DataVariableItem ch = channels[c];
                // 随机游走：相邻行差值在 [-1, +1] 范围内。
                state[c] += (rng.NextDouble() - 0.5) * 2.0;
                string valueText = state[c].ToString("F4", System.Globalization.CultureInfo.InvariantCulture);

                string src = ch.Source == DataVariableSource.Slave ? "Slave" : "Master";
                string addr = ch.Source == DataVariableSource.Slave ? $"0x{ch.SdoIndex:X4}" : string.Empty;
                string sub = ch.Source == DataVariableSource.Slave ? $"0x{ch.SdoSubIndex:X2}" : string.Empty;

                _ = sb.Clear();
                switch (fmt)
                {
                    case DataSaveFormat.Csv:
                        _ = sb.Append(CsvEsc(tsText)).Append(',')
                              .Append(CsvEsc(src)).Append(',')
                              .Append(CsvEsc(ch.Group)).Append(',')
                              .Append(CsvEsc(ch.Name)).Append(',')
                              .Append(CsvEsc(addr)).Append(',')
                              .Append(CsvEsc(sub)).Append(',')
                              .Append(CsvEsc(ch.DataType)).Append(',')
                              .Append(CsvEsc(valueText)).Append(',')
                              .Append(CsvEsc(ch.Unit));
                        break;
                    case DataSaveFormat.Tsv:
                        _ = sb.Append(tsText).Append('\t')
                              .Append(src).Append('\t')
                              .Append(ch.Group).Append('\t')
                              .Append(ch.Name).Append('\t')
                              .Append(addr).Append('\t')
                              .Append(sub).Append('\t')
                              .Append(ch.DataType).Append('\t')
                              .Append(valueText).Append('\t')
                              .Append(ch.Unit);
                        break;
                    case DataSaveFormat.Xls:
                        _ = sb.Append("    <Row>");
                        AppendXlsCell(sb, tsText);
                        AppendXlsCell(sb, src);
                        AppendXlsCell(sb, ch.Group);
                        AppendXlsCell(sb, ch.Name);
                        AppendXlsCell(sb, addr);
                        AppendXlsCell(sb, sub);
                        AppendXlsCell(sb, ch.DataType);
                        AppendXlsCell(sb, valueText);
                        AppendXlsCell(sb, ch.Unit);
                        _ = sb.Append("</Row>");
                        break;
                    case DataSaveFormat.Json:
                        _ = sb.Append('{')
                              .Append("\"ts\":\"").Append(tsText).Append('"')
                              .Append(",\"src\":\"").Append(src).Append('"')
                              .Append(",\"group\":\"").Append(JsonEsc(ch.Group)).Append('"')
                              .Append(",\"name\":\"").Append(JsonEsc(ch.Name)).Append('"');
                        if (ch.Source == DataVariableSource.Slave)
                        {
                            _ = sb.Append(",\"idx\":").Append(ch.SdoIndex)
                                  .Append(",\"sub\":").Append(ch.SdoSubIndex);
                        }
                        _ = sb.Append(",\"type\":\"").Append(JsonEsc(ch.DataType)).Append('"')
                              .Append(",\"val\":\"").Append(valueText).Append('"');
                        if (!string.IsNullOrEmpty(ch.Unit))
                        {
                            _ = sb.Append(",\"unit\":\"").Append(JsonEsc(ch.Unit)).Append('"');
                        }
                        _ = sb.Append('}');
                        break;
                }

                writer.WriteLine(sb);
                written++;
                batchAccum++;
            }

            if (batchAccum >= batchSize)
            {
                onRowsWritten(batchAccum);
                batchAccum = 0;
            }
        }

        if (batchAccum > 0)
        {
            onRowsWritten(batchAccum);
        }

        if (fmt == DataSaveFormat.Xls)
        {
            writer.WriteLine("  </Table></Worksheet>");
            writer.WriteLine("</Workbook>");
        }

        writer.Flush();
        return written;
    }

    private static string CsvEsc(string? s)
    {
        s ??= string.Empty;
        if (s.Length == 0) return string.Empty;
        bool needQuote = s.IndexOfAny([',', '"', '\n', '\r']) >= 0;
        return needQuote ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static void AppendXlsCell(StringBuilder sb, string? s)
    {
        var escaped = System.Security.SecurityElement.Escape(s ?? string.Empty) ?? string.Empty;
        _ = sb.Append("<Cell><Data ss:Type=\"String\">").Append(escaped).Append("</Data></Cell>");
    }

    private static string JsonEsc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length + 4);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': _ = sb.Append("\\\\"); break;
                case '"': _ = sb.Append("\\\""); break;
                case '\n': _ = sb.Append("\\n"); break;
                case '\r': _ = sb.Append("\\r"); break;
                case '\t': _ = sb.Append("\\t"); break;
                default:
                    if (c < 0x20) _ = sb.Append($"\\u{(int)c:X4}");
                    else _ = sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    public new void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed || !disposing)
        {
            return;
        }

        _samplingCts?.Cancel();
        _samplingCts?.Dispose();
        _samplingCts = null;

        _connectionTimer.Stop();
        DataFrameLogger.Instance.FileRotated -= OnFileRotated;
        UserSettingsService.SettingsChanged -= OnExternalSettingsChanged;
        try { _testDataCts?.Cancel(); } catch { }
        _testDataCts?.Dispose();
        _testDataCts = null;
        _disposed = true;
    }
}