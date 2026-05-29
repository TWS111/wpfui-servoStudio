// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml;
using Microsoft.Win32;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.Services;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels.AppData;

/// <summary>
/// 数据导入/查看 — 加载 <see cref="Services.DataFrameLogger"/> 写入的数据文件，
/// 按变量分通道（每变量按存储顺序作为 X 轴，数据值作为 Y 轴）供波形窗显示。
/// </summary>
public partial class DataViewViewModel : ViewModel
{
    private static readonly string DefaultDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ServoStudio", "Data");

    [ObservableProperty]
    private string _currentFilePath = string.Empty;

    [ObservableProperty]
    private string _currentFileName = "未加载";

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private int _totalSamples;

    [ObservableProperty]
    private int _totalChannels;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelLoadCommand))]
    private bool _isLoading;

    /// <summary>0–100 文件解析进度。</summary>
    [ObservableProperty]
    private double _loadProgress;

    private CancellationTokenSource? _loadCts;

    /// <summary>
    /// 通道列表 —— 每个 DataChannel 即一个变量，按存储顺序聚合 Y 值。
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<DataChannel> _channels = [];

    /// <summary>是否处于 Debug 模式（控制必要列开关的可见性）。从用户设置同步。</summary>
    [ObservableProperty]
    private bool _isDebugMode;

    /// <summary>
    /// 是否强制校验必要列（Name/Value）。
    /// ON：当文件缺失必要列时报错（与原有行为一致）。
    /// OFF：以第一非空行为列标题；若不含 Name 列，则按"每列一通道"展开（列名作为通道名，列内单元格作为采样值）。
    /// 仅 Debug 模式下可见，并自动持久化到用户设置。
    /// </summary>
    [ObservableProperty]
    private bool _enforceRequiredColumns = true;

    // ===== 设备虚拟示波器配置 (H0D.20 ~ H0D.23) =====
    [ObservableProperty] private int _oscCh1Signal;        // H0D.20 通道1信号选择 (0~31)
    [ObservableProperty] private int _oscCh2Signal;        // H0D.21 通道2信号选择 (0~31)
    [ObservableProperty] private int _oscTriggerMode;      // H0D.22 触发模式 (0~3)
    [ObservableProperty] private int _oscSamplePeriodMs = 1; // H0D.23 采样周期 (1~1000 ms)
    [ObservableProperty] private string _oscConfigStatus = string.Empty;

    // ===== 在线接收（静默模式） =====
    /// <summary>
    /// 在 Debug 模式下且当前协议栈为 Modbus 时可用：开启后主机停止下发，转为静默接收从机周期上报的
    /// ASCII 帧（格式 ">name1:val1,name2:val2,...\r\n"），实时刷新到波形窗。
    /// </summary>
    [ObservableProperty] private bool _isLiveReceiveSupported;

    [ObservableProperty] private bool _isLiveReceiveEnabled;

    /// <summary>波形窗最大显示样本数（每通道）。Roll 模式下超过即滚动；Sweep 模式下覆盖最旧位置。</summary>
    [ObservableProperty] private int _liveDisplayCapacity = 1000;

    /// <summary>超容显示策略。</summary>
    [ObservableProperty] private WaveOverflowMode _liveOverflowMode = WaveOverflowMode.Roll;

    /// <summary>给 ComboBox.SelectedIndex 使用的整数封装（0=Roll, 1=Sweep）。</summary>
    public int LiveOverflowModeIndex
    {
        get => (int)LiveOverflowMode;
        set
        {
            var m = value == 1 ? WaveOverflowMode.Sweep : WaveOverflowMode.Roll;
            if (m != LiveOverflowMode)
            {
                LiveOverflowMode = m;
            }
        }
    }

    /// <summary>静默模式累计帧序号（仅 Sweep 模式按容量取模作为写指针）。</summary>
    private int _liveSampleIndex;

    /// <summary>静默模式数据更新锁。</summary>
    private readonly Lock _liveLock = new();

    /// <summary>在线接收每收到并解析一帧后触发（可能在后台线程；View 可在 Dispatcher 中节流刷新）。</summary>
    public event Action? LiveFrameReceived;

    /// <summary>Sweep 模式下的当前写指针（供 View 显示用，0 表示尚未开始扫写）。</summary>
    public int LiveSweepWritePosition
        => LiveOverflowMode == WaveOverflowMode.Sweep && LiveDisplayCapacity > 0
            ? _liveSampleIndex % Math.Max(1, LiveDisplayCapacity)
            : 0;

    private readonly DeviceAddViewModel? _deviceAddViewModel;

    public DataViewViewModel(DeviceAddViewModel deviceAddViewModel) : this()
    {
        _deviceAddViewModel = deviceAddViewModel;
        // 将"在线接收是否激活"查询委托注入 DeviceAddViewModel，
        // 使 Modbus 连接时可绕过 ProbeIdentity。
        _deviceAddViewModel.IsLiveReceiveModeActive = () => IsLiveReceiveEnabled;
        // 当 Modbus 以在线接收模式连通串口后，由 DeviceAddViewModel 触发该事件，
        // 在 UI 线程上启动静默接收。
        _deviceAddViewModel.LiveReceiveConnectionReady += OnLiveReceiveConnectionReady;
        _deviceAddViewModel.PropertyChanged += OnDeviceAddPropertyChanged;
        UpdateLiveReceiveSupported();
    }

    public DataViewViewModel()
    {
        var s = UserSettingsService.Load();
        _isDebugMode = s.IsDebugMode;
        _enforceRequiredColumns = s.DataView_EnforceRequiredColumns;
        UserSettingsService.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, UserSettings s)
    {
        IsDebugMode = s.IsDebugMode;
        UpdateLiveReceiveSupported();
    }

    /// <summary>
    /// Modbus 以在线接收模式连通串口后，DeviceAddViewModel 在后台线程触发此事件；
    /// 派发到 UI 线程后调用 StartLiveReceive。
    /// </summary>
    private void OnLiveReceiveConnectionReady()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.InvokeAsync(StartLiveReceive);
        else
            StartLiveReceive();
    }

    private void OnDeviceAddPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 仅在 Modbus 断开时同步停止在线接收
        if (e.PropertyName == nameof(DeviceAddViewModel.IsModbusConnected)
            && _deviceAddViewModel?.IsModbusConnected == false
            && IsLiveReceiveEnabled)
        {
            IsLiveReceiveEnabled = false;
        }
    }

    /// <summary>在线接收开关可见性：仅由 Debug 模式决定，与连接状态无关。</summary>
    private void UpdateLiveReceiveSupported()
    {
        IsLiveReceiveSupported = IsDebugMode;
        // Debug 模式关闭时强制停止在线接收
        if (!IsDebugMode && IsLiveReceiveEnabled)
        {
            IsLiveReceiveEnabled = false;
        }
    }

    partial void OnIsDebugModeChanged(bool value) => UpdateLiveReceiveSupported();

    partial void OnEnforceRequiredColumnsChanged(bool value)
    {
        try
        {
            var s = UserSettingsService.Load();
            s.DataView_EnforceRequiredColumns = value;
            UserSettingsService.Save(s);
        }
        catch { }
    }

    // ===== 设备虚拟示波器 (H0D.20 ~ H0D.23) =====
    [RelayCommand]
    private async Task OnReadOscConfig()
    {
        if (_deviceAddViewModel is null || !_deviceAddViewModel.IsAnyConnected || _deviceAddViewModel.ActiveAxis is null)
        {
            OscConfigStatus = "设备未连接";
            return;
        }

        OscConfigStatus = "读取中...";
        await Task.Run(() =>
        {
            var master = _deviceAddViewModel.ActiveServoMaster;
            var axis = _deviceAddViewModel.ActiveAxis;
            HRegisterIO.ReadHReg(master, axis, "H0D.20", v => OscCh1Signal = v);
            HRegisterIO.ReadHReg(master, axis, "H0D.21", v => OscCh2Signal = v);
            HRegisterIO.ReadHReg(master, axis, "H0D.22", v => OscTriggerMode = v);
            HRegisterIO.ReadHReg(master, axis, "H0D.23", v => OscSamplePeriodMs = v);
        });
        OscConfigStatus = "读取完成";
    }

    [RelayCommand]
    private async Task OnWriteOscConfig()
    {
        if (_deviceAddViewModel is null || !_deviceAddViewModel.IsAnyConnected || _deviceAddViewModel.ActiveAxis is null)
        {
            OscConfigStatus = "设备未连接";
            return;
        }

        OscConfigStatus = "写入中...";
        var errors = new List<string>();
        await Task.Run(() =>
        {
            var master = _deviceAddViewModel.ActiveServoMaster;
            var axis = _deviceAddViewModel.ActiveAxis;
            HRegisterIO.SafeWriteHReg(master, axis, "H0D.20", (ushort)OscCh1Signal, errors, "通道1信号");
            HRegisterIO.SafeWriteHReg(master, axis, "H0D.21", (ushort)OscCh2Signal, errors, "通道2信号");
            HRegisterIO.SafeWriteHReg(master, axis, "H0D.22", (ushort)OscTriggerMode, errors, "触发模式");
            HRegisterIO.SafeWriteHReg(master, axis, "H0D.23", (ushort)OscSamplePeriodMs, errors, "采样周期");
        });
        OscConfigStatus = errors.Count == 0 ? "写入成功" : $"写入失败: {string.Join(", ", errors)}";
    }

    [RelayCommand(CanExecute = nameof(CanLoadFile))]
    private async Task OnLoadFile()
    {
        // 优先使用上次记忆的目录，其次默认存储目录，最后 LocalAppData
        var savedDir = UserSettingsService.Load().DataView_LastDirectory;
        string initialDir = Directory.Exists(savedDir)
            ? savedDir
            : Directory.Exists(DefaultDirectory)
                ? DefaultDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var dialog = new OpenFileDialog
        {
            Title = "选择要导入的数据文件",
            Filter = "数据文件|*.csv;*.tsv;*.jsonl;*.xls;*.xlsx|CSV (*.csv)|*.csv|TSV (*.tsv)|*.tsv|JSONL (*.jsonl)|*.jsonl|XLS (*.xls)|*.xls|XLSX (*.xlsx)|*.xlsx|所有文件|*.*",
            InitialDirectory = initialDir,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // 记忆所选文件所在目录
        var chosenDir = Path.GetDirectoryName(dialog.FileName);
        if (!string.IsNullOrEmpty(chosenDir) && Directory.Exists(chosenDir))
        {
            try
            {
                var s = UserSettingsService.Load();
                s.DataView_LastDirectory = chosenDir;
                UserSettingsService.Save(s);
            }
            catch { }
        }

        // 含多工作表的格式（xls / xlsx）：在解析前列出工作表名，>1 时弹窗让用户选择
        string? sheet = null;
        var ext0 = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        if (ext0 == ".xls" || ext0 == ".xlsx")
        {
            List<string> sheets = ext0 == ".xlsx"
                ? ListXlsxSheetNames(dialog.FileName)
                : ListXlsXmlSheetNames(dialog.FileName);
            if (sheets.Count > 1)
            {
                sheet = Views.Dialogs.SheetPickerWindow.Pick(
                    Path.GetFileName(dialog.FileName), sheets);
                if (sheet is null)
                {
                    // 用户取消
                    return;
                }
            }
            else if (sheets.Count == 1)
            {
                sheet = sheets[0];
            }
        }

        await LoadFileAsync(dialog.FileName, sheet);
    }

    private bool CanLoadFile() => !IsLoading;

    /// <summary>当前文件最近一次解析时所选的工作表名（仅 xls/xlsx 有意义）。</summary>
    private string? _currentSheetName;

    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task OnReload()
    {
        if (!string.IsNullOrEmpty(CurrentFilePath) && File.Exists(CurrentFilePath))
        {
            await LoadFileAsync(CurrentFilePath, _currentSheetName);
        }
    }

    private bool CanReload() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanCancelLoad))]
    private void OnCancelLoad()
    {
        try { _loadCts?.Cancel(); } catch { }
    }

    private bool CanCancelLoad() => IsLoading;

    [RelayCommand]
    private void OnClear()
    {
        Channels = [];
        CurrentFilePath = string.Empty;
        CurrentFileName = "未加载";
        TotalSamples = 0;
        TotalChannels = 0;
        StatusText = "已清空";
        ChannelsReplaced?.Invoke();
    }

    [RelayCommand]
    private void OnSelectAll()
    {
        foreach (var ch in Channels)
        {
            ch.IsVisible = true;
        }
    }

    [RelayCommand]
    private void OnUnselectAll()
    {
        foreach (var ch in Channels)
        {
            ch.IsVisible = false;
        }
    }

    /// <summary>切换单个通道的显示/关闭状态（绑定到通道按钮）。</summary>
    [RelayCommand]
    private static void OnToggleChannel(DataChannel? channel)
    {
        if (channel is null)
        {
            return;
        }

        // 非可绘制类型（如 string）禁止点亮，避免误入波形。
        if (!channel.IsPlottable)
        {
            return;
        }

        channel.IsVisible = !channel.IsVisible;
    }

    /// <summary>当通道集合被整体替换时触发，View 据此重建波形。</summary>
    public event Action? ChannelsReplaced;

    /// <summary>
    /// 按色环顺序生成通道颜色（HSV）：
    /// CH1 = 0°（红）；最后一个通道 = 接近 360° 的色环末端；中间均匀过渡，不重复循环。
    /// 当 totalCount &lt;= 1 时单个通道仍为红色。
    /// </summary>
    public static string GetChannelColorHex(int index, int totalCount)
    {
        if (totalCount <= 1)
        {
            return HsvToRgbHex(0.0, 0.85, 1.0);
        }

        // 把 [0°, 360°) 均匀分成 totalCount 份；
        // i=0 → 0°（红），i=totalCount-1 → (totalCount-1)/totalCount * 360°（色环最末端）。
        double h = (index % totalCount) * 360.0 / totalCount;
        const double s = 0.85;
        const double v = 1.00;
        return HsvToRgbHex(h, s, v);
    }

    private static string HsvToRgbHex(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs(((h / 60.0) % 2) - 1));
        double m = v - c;
        double r1, g1, b1;
        if (h < 60) { r1 = c; g1 = x; b1 = 0; }
        else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
        else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
        else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
        else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }
        int r = (int)Math.Round((r1 + m) * 255);
        int g = (int)Math.Round((g1 + m) * 255);
        int b = (int)Math.Round((b1 + m) * 255);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    public void LoadFile(string path)
    {
        // 兼容旧的同步入口：转为后台异步加载，UI 不再阻塞。
        _ = LoadFileAsync(path, null);
    }

    // === 可见通道按文件持久化 ===
    private static string NormalizeFileKey(string path)
    {
        try { return Path.GetFullPath(path).ToLowerInvariant(); }
        catch { return (path ?? string.Empty).ToLowerInvariant(); }
    }

    /// <summary>通道内容签名：决定"是什么通道"，与 ChannelIndex 无关。</summary>
    public static string GetChannelSignature(DataChannel ch) =>
        $"{ch.Source}|{ch.Group}|{ch.Name}|{ch.SdoIndex}|{ch.SdoSubIndex}|{ch.DataType}|{ch.Unit}";

    /// <summary>读取磁盘记忆中该文件的可见通道签名集合；若该文件未记忆则返回 null。</summary>
    private static HashSet<string>? TryGetRememberedVisibleSignatures(string filePath)
    {
        try
        {
            var s = Services.UserSettingsService.Load();
            if (s.DataView_VisibleChannelsByFile is null) return null;
            string key = NormalizeFileKey(filePath);
            if (s.DataView_VisibleChannelsByFile.TryGetValue(key, out var list))
            {
                return new HashSet<string>(list ?? new List<string>(), StringComparer.Ordinal);
            }
        }
        catch { }
        return null;
    }

    private void SubscribeChannelVisibility(IEnumerable<DataChannel> channels)
    {
        foreach (var ch in channels)
        {
            ch.PropertyChanged += OnChannelVisibilityChanged;
        }
    }

    private void UnsubscribeChannelVisibility(IEnumerable<DataChannel>? channels)
    {
        if (channels is null) return;
        foreach (var ch in channels)
        {
            ch.PropertyChanged -= OnChannelVisibilityChanged;
        }
    }

    private void OnChannelVisibilityChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DataChannel.IsVisible)) return;
        SaveVisibleChannelsForCurrentFile();
    }

    /// <summary>把当前文件中所有 IsVisible 的通道签名写回 UserSettings。</summary>
    private void SaveVisibleChannelsForCurrentFile()
    {
        if (string.IsNullOrEmpty(CurrentFilePath)) return;
        try
        {
            var s = Services.UserSettingsService.Load();
            s.DataView_VisibleChannelsByFile ??= new Dictionary<string, List<string>>();
            string key = NormalizeFileKey(CurrentFilePath);
            var sigs = new List<string>();
            foreach (var ch in Channels)
            {
                if (ch.IsVisible)
                {
                    sigs.Add(GetChannelSignature(ch));
                }
            }
            s.DataView_VisibleChannelsByFile[key] = sigs;
            Services.UserSettingsService.Save(s);
        }
        catch { }
    }

    public async Task LoadFileAsync(string path, string? sheetName = null)
    {
        if (IsLoading)
        {
            return;
        }

        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        IsLoading = true;
        LoadProgress = 0;
        StatusText = "正在解析...";
        CurrentFileName = Path.GetFileName(path);
        _currentSheetName = sheetName;

        // 进度节流：每 ~1% 或最少 50ms 才推送一次到 UI 线程。
        double lastReported = -1;
        DateTime lastTime = DateTime.MinValue;
        var progress = new Progress<double>(p =>
        {
            if (p < 100 && p - lastReported < 1.0
                && (DateTime.UtcNow - lastTime).TotalMilliseconds < 50)
            {
                return;
            }
            lastReported = p;
            lastTime = DateTime.UtcNow;
            LoadProgress = p;
            StatusText = p >= 100 ? "正在生成通道..." : $"正在解析... {p:F1}%";
        });

        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            bool enforceRequired = EnforceRequiredColumns;
            Dictionary<string, DataChannel> dict = await Task.Run(
                () => ext switch
                {
                    ".jsonl" => ParseJsonLines(path, progress, ct),
                    ".tsv" => ParseDelimited(path, '\t', enforceRequired, progress, ct),
                    ".xls" => ParseXlsXml(path, sheetName, enforceRequired, progress, ct),
                    ".xlsx" => ParseXlsx(path, sheetName, enforceRequired, progress, ct),
                    _ => ParseDelimited(path, ',', enforceRequired, progress, ct),
                }, ct);

            ct.ThrowIfCancellationRequested();

            // 按变量名 + 来源/索引区分通道，按出现顺序构建
            var newChannels = new ObservableCollection<DataChannel>();
            int totalSamples = 0;

            // 取出该文件已记忆的"可见通道签名"集合（按内容匹配，不仅靠通道号）
            var rememberedSignatures = TryGetRememberedVisibleSignatures(path);
            bool hasRemembered = rememberedSignatures is not null;

            foreach (var kv in dict)
            {
                var ch = kv.Value;
                int idx = newChannels.Count;
                ch.ChannelIndex = idx;
                ch.ChannelLabel = $"CH{idx + 1}";

                bool defaultVisible = ch.IsPlottable && ch.Values.Count > 0 && idx < 8;
                if (hasRemembered)
                {
                    // 仅当 (a) 该文件存在记忆 且 (b) 当前通道签名在记忆集合中 且 (c) 通道可绘制且有数据 时才点亮。
                    // 若该文件存在记忆但本通道不在内 → 关闭（即"无已记忆通道则不激活"语义对单通道生效）。
                    ch.IsVisible = ch.IsPlottable
                                   && ch.Values.Count > 0
                                   && rememberedSignatures!.Contains(GetChannelSignature(ch));
                }
                else
                {
                    // 起始可见性：仅数值型且含样本的前 8 个默认勾选；非可绘制始终为关闭。
                    ch.IsVisible = defaultVisible;
                }
                newChannels.Add(ch);
                totalSamples += ch.Values.Count;
            }

            // 颜色按色环均匀分布：CH1 = 0°（红），最后一个 = (n-1)/n * 360°，中间均匀过渡，不循环。
            int total = newChannels.Count;
            for (int i = 0; i < total; i++)
            {
                newChannels[i].ColorHex = GetChannelColorHex(i, total);
            }

            // 取消上一文件的可见性订阅，再为新通道订阅 IsVisible 变化以持久化勾选。
            UnsubscribeChannelVisibility(Channels);
            Channels = newChannels;
            SubscribeChannelVisibility(newChannels);
            CurrentFilePath = path;
            CurrentFileName = Path.GetFileName(path);
            TotalChannels = newChannels.Count;
            TotalSamples = totalSamples;
            int emptyChannels = 0;
            foreach (var c in newChannels)
            {
                if (c.Values.Count == 0)
                {
                    emptyChannels++;
                }
            }

            LoadProgress = 100;
            StatusText = emptyChannels == 0
                ? $"已加载 {newChannels.Count} 个通道，共 {totalSamples} 个样本"
                : $"已加载 {newChannels.Count} 个通道（含 {emptyChannels} 个空通道），共 {totalSamples} 个样本";
            ChannelsReplaced?.Invoke();

            AppLogViewModel.Log(
                AppLogLevel.Info,
                AppLogCategory.System,
                "数据文件已加载",
                $"{CurrentFileName} | 通道:{newChannels.Count} 样本:{totalSamples}");
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消加载";
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败: {ex.Message}";
            AppLogViewModel.Log(
                AppLogLevel.Error,
                AppLogCategory.System,
                "数据文件加载失败",
                $"{path} | {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ===== 解析: CSV/TSV =====
    // 表头: Timestamp,Source,Group,Name,Index,SubIndex,DataType,Value,Unit
    private static Dictionary<string, DataChannel> ParseDelimited(string path, char sep, bool enforceRequired, IProgress<double>? progress, CancellationToken ct)
    {
        var result = new Dictionary<string, DataChannel>(StringComparer.Ordinal);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long total = Math.Max(1, stream.Length);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        // 读取首个非空行作为表头
        string? header;
        while (true)
        {
            header = reader.ReadLine();
            if (header is null)
            {
                return result;
            }
            if (header.Length > 0 && !string.IsNullOrWhiteSpace(header))
            {
                break;
            }
        }

        // 列索引
        var headers = SplitDelimited(header, sep);
        int idxName = IndexOf(headers, "Name");
        int idxValue = IndexOf(headers, "Value");
        int idxSource = IndexOf(headers, "Source");
        int idxGroup = IndexOf(headers, "Group");
        int idxIndex = IndexOf(headers, "Index");
        int idxSubIndex = IndexOf(headers, "SubIndex");
        int idxUnit = IndexOf(headers, "Unit");
        int idxDataType = IndexOf(headers, "DataType");
        int idxTimestamp = IndexOf(headers, "Timestamp");

        if (idxName < 0 || idxValue < 0)
        {
            if (enforceRequired)
            {
                throw new InvalidDataException("文件表头不含必要列 (Name/Value)。");
            }

            // permissive 模式：缓存剩余行后按"每列一通道"装填
            var rows = new List<List<string>> { headers };
            string? l;
            long lc = 0;
            while ((l = reader.ReadLine()) != null)
            {
                if ((++lc & 0x3FF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(stream.Position * 80.0 / total);
                }
                if (l.Length == 0)
                {
                    continue;
                }
                rows.Add(SplitDelimited(l, sep));
            }
            BuildChannelsColumnWise(headers, rows, dataStartRow: 1, result, progress, ct, rowsPhaseStart: 80.0, rowsPhaseEnd: 100.0);
            return result;
        }

        string? line;
        long lineCount = 0;
        bool typeRowChecked = false;
        while ((line = reader.ReadLine()) != null)
        {
            if ((++lineCount & 0x3FF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(stream.Position * 100.0 / total);
            }

            if (line.Length == 0)
            {
                continue;
            }

            // 表头之后可能跟随一行 "#TYPE,..." 的类型行，跳过。
            if (!typeRowChecked)
            {
                typeRowChecked = true;
                if (line.StartsWith("#TYPE", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            var cols = SplitDelimited(line, sep);

            // 只要 Name 列存在，就建立通道（即使 Value 列缺失或不可解析）。
            if (cols.Count <= idxName)
            {
                continue;
            }

            string name = Get(cols, idxName);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            string source = Get(cols, idxSource);
            string sdoIdx = Get(cols, idxIndex);
            string sdoSub = Get(cols, idxSubIndex);
            string key = $"{source}|{sdoIdx}|{sdoSub}|{name}";

            if (!result.TryGetValue(key, out var ch))
            {
                ch = new DataChannel
                {
                    Name = name,
                    Source = source,
                    Group = Get(cols, idxGroup),
                    SdoIndex = sdoIdx,
                    SdoSubIndex = sdoSub,
                    Unit = Get(cols, idxUnit),
                    DataType = Get(cols, idxDataType),
                };
                result[key] = ch;
            }

            // Value 列可能不存在/不可解析——不影响通道本身的创建，仅不追加 Y 值。
            string rawValue = Get(cols, idxValue);
            if (TryParseNumber(rawValue, out double d))
            {
                ch.Values.Add(d);

                if (idxTimestamp >= 0)
                {
                    if (DateTime.TryParse(Get(cols, idxTimestamp), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                    {
                        ch.Timestamps.Add(ts);
                    }
                }
            }
        }

        return result;
    }

    // ===== 解析: JSONL =====
    private static Dictionary<string, DataChannel> ParseJsonLines(string path, IProgress<double>? progress, CancellationToken ct)
    {
        var result = new Dictionary<string, DataChannel>(StringComparer.Ordinal);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long total = Math.Max(1, stream.Length);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        string? line;
        long lineCount = 0;
        while ((line = reader.ReadLine()) != null)
        {
            if ((++lineCount & 0x3FF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(stream.Position * 100.0 / total);
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                string source = root.TryGetProperty("src", out var s) ? s.GetString() ?? string.Empty : string.Empty;
                string sdoIdx = root.TryGetProperty("idx", out var i) ? i.ToString() : string.Empty;
                string sdoSub = root.TryGetProperty("sub", out var sb) ? sb.ToString() : string.Empty;
                string key = $"{source}|{sdoIdx}|{sdoSub}|{name}";

                if (!result.TryGetValue(key, out var ch))
                {
                    ch = new DataChannel
                    {
                        Name = name,
                        Source = source,
                        Group = root.TryGetProperty("group", out var g) ? g.GetString() ?? string.Empty : string.Empty,
                        SdoIndex = sdoIdx,
                        SdoSubIndex = sdoSub,
                        Unit = root.TryGetProperty("unit", out var u) ? u.GetString() ?? string.Empty : string.Empty,
                        DataType = root.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty,
                    };
                    result[key] = ch;
                }

                if (root.TryGetProperty("val", out var v))
                {
                    var raw = v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.ToString();
                    if (TryParseNumber(raw, out double d))
                    {
                        ch.Values.Add(d);
                        if (root.TryGetProperty("ts", out var ts) && DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                        {
                            ch.Timestamps.Add(dt);
                        }
                    }
                }
            }
            catch
            {
                // 跳过格式异常行
            }
        }

        return result;
    }

    // ===== 解析: XLS (XML Spreadsheet 2003) =====
    // 由 DataFrameLogger 写入：<Workbook>/<Worksheet ss:Name="...">/<Table>/<Row>/<Cell>/<Data>...
    // sheetName 为 null/空 时取第一个 Worksheet；否则匹配 ss:Name。
    private static Dictionary<string, DataChannel> ParseXlsXml(string path, string? sheetName, bool enforceRequired, IProgress<double>? progress, CancellationToken ct)
    {
        var result = new Dictionary<string, DataChannel>(StringComparer.Ordinal);

        var settings = new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
            DtdProcessing = DtdProcessing.Ignore,
        };

        using var stream = File.OpenRead(path);
        long total = Math.Max(1, stream.Length);
        using var reader = XmlReader.Create(stream, settings);

        var rows = new List<List<string>>();
        var currentRow = new List<string>();
        bool inRow = false;
        bool inCell = false;
        // 当前是否处于"目标 Worksheet"内：true 才采集 Row。null 模式 = 接受首个 Worksheet。
        bool collecting = false;
        bool firstWorksheetSeen = false;
        long readCounter = 0;

        while (reader.Read())
        {
            if ((++readCounter & 0xFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                // XML 阶段占总进度的 0–80%（Row 处理占余下 20%）。
                progress?.Report(stream.Position * 80.0 / total);
            }

            if (reader.NodeType == XmlNodeType.Element)
            {
                string local = reader.LocalName;
                if (string.Equals(local, "Worksheet", StringComparison.OrdinalIgnoreCase))
                {
                    string name = reader.GetAttribute("Name", "urn:schemas-microsoft-com:office:spreadsheet")
                        ?? reader.GetAttribute("ss:Name")
                        ?? reader.GetAttribute("Name")
                        ?? string.Empty;
                    if (string.IsNullOrEmpty(sheetName))
                    {
                        collecting = !firstWorksheetSeen;
                    }
                    else
                    {
                        collecting = string.Equals(name, sheetName, StringComparison.Ordinal);
                    }
                    firstWorksheetSeen = true;
                }
                else if (collecting && string.Equals(local, "Row", StringComparison.OrdinalIgnoreCase))
                {
                    inRow = true;
                    currentRow = [];
                }
                else if (inRow && string.Equals(local, "Cell", StringComparison.OrdinalIgnoreCase))
                {
                    inCell = true;
                }
                else if (inCell && string.Equals(local, "Data", StringComparison.OrdinalIgnoreCase))
                {
                    string text = reader.IsEmptyElement ? string.Empty : reader.ReadElementContentAsString();
                    currentRow.Add(text);
                    inCell = false;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                string local = reader.LocalName;
                if (string.Equals(local, "Cell", StringComparison.OrdinalIgnoreCase))
                {
                    inCell = false;
                }
                else if (string.Equals(local, "Row", StringComparison.OrdinalIgnoreCase))
                {
                    if (collecting)
                    {
                        rows.Add(currentRow);
                    }
                    inRow = false;
                }
                else if (string.Equals(local, "Worksheet", StringComparison.OrdinalIgnoreCase))
                {
                    collecting = false;
                }
            }
        }

        if (rows.Count == 0)
        {
            return result;
        }

        BuildChannelsFromRows(rows, result, progress, ct, rowsPhaseStart: 80.0, rowsPhaseEnd: 100.0, enforceRequired: enforceRequired);
        return result;
    }

    /// <summary>
    /// 公共"行数组 → 通道字典"装填逻辑。
    /// 优先取 rows 中第一个非空行作为表头；可选随后的 "#TYPE" 哨兵行会被跳过；进度按 rowsPhaseStart..rowsPhaseEnd 上报。
    /// enforceRequired=true：要求表头含 Name 列，否则抛出 InvalidDataException（与原行为一致）。
    /// enforceRequired=false：缺失 Name 列时按"每列一通道"展开（列名做通道名，单元格做采样值）。
    /// </summary>
    private static void BuildChannelsFromRows(
        List<List<string>> rows,
        Dictionary<string, DataChannel> result,
        IProgress<double>? progress,
        CancellationToken ct,
        double rowsPhaseStart,
        double rowsPhaseEnd,
        bool enforceRequired = true)
    {
        // 找到第一行非空行作为表头（permissive 模式下可能跳过若干空行/注释）
        int headerRow = 0;
        while (headerRow < rows.Count && IsRowEmpty(rows[headerRow]))
        {
            headerRow++;
        }
        if (headerRow >= rows.Count)
        {
            return;
        }

        var headers = rows[headerRow];
        int idxName = IndexOf(headers, "Name");
        int idxValue = IndexOf(headers, "Value");
        int idxSource = IndexOf(headers, "Source");
        int idxGroup = IndexOf(headers, "Group");
        int idxIndex = IndexOf(headers, "Index");
        int idxSubIndex = IndexOf(headers, "SubIndex");
        int idxUnit = IndexOf(headers, "Unit");
        int idxDataType = IndexOf(headers, "DataType");
        int idxTimestamp = IndexOf(headers, "Timestamp");

        if (idxName < 0)
        {
            if (enforceRequired)
            {
                throw new InvalidDataException("文件表头不含必要列 (Name)。");
            }

            // 通用列方向解释：每列 = 一个通道
            BuildChannelsColumnWise(headers, rows, headerRow + 1, result, progress, ct, rowsPhaseStart, rowsPhaseEnd);
            return;
        }

        int dataStartRow = headerRow + 1;
        if (dataStartRow < rows.Count && rows[dataStartRow].Count > 0
            && string.Equals(rows[dataStartRow][0], "#TYPE", StringComparison.OrdinalIgnoreCase))
        {
            dataStartRow++;
        }

        double phaseSpan = Math.Max(0.0, rowsPhaseEnd - rowsPhaseStart);
        for (int r = dataStartRow; r < rows.Count; r++)
        {
            if ((r & 0x3FF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(rowsPhaseStart + (r * phaseSpan / Math.Max(1, rows.Count)));
            }

            var cols = rows[r];
            if (cols.Count <= idxName)
            {
                continue;
            }

            string name = Get(cols, idxName);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            string source = Get(cols, idxSource);
            string sdoIdx = Get(cols, idxIndex);
            string sdoSub = Get(cols, idxSubIndex);
            string key = $"{source}|{sdoIdx}|{sdoSub}|{name}";

            if (!result.TryGetValue(key, out var ch))
            {
                ch = new DataChannel
                {
                    Name = name,
                    Source = source,
                    Group = Get(cols, idxGroup),
                    SdoIndex = sdoIdx,
                    SdoSubIndex = sdoSub,
                    Unit = Get(cols, idxUnit),
                    DataType = Get(cols, idxDataType),
                };
                result[key] = ch;
            }

            string rawValue = idxValue >= 0 ? Get(cols, idxValue) : string.Empty;
            if (TryParseNumber(rawValue, out double d))
            {
                ch.Values.Add(d);

                if (idxTimestamp >= 0
                    && DateTime.TryParse(Get(cols, idxTimestamp), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                {
                    ch.Timestamps.Add(ts);
                }
            }
        }
    }

    /// <summary>permissive 模式下：每个表头列 = 一个通道；遍历每条数据行，把数值单元格 push 到对应通道。</summary>
    private static void BuildChannelsColumnWise(
        List<string> headers,
        List<List<string>> rows,
        int dataStartRow,
        Dictionary<string, DataChannel> result,
        IProgress<double>? progress,
        CancellationToken ct,
        double rowsPhaseStart,
        double rowsPhaseEnd)
    {
        // 为每列预创建通道；空列名用 "Col{N}" 占位
        var channels = new DataChannel?[headers.Count];
        for (int c = 0; c < headers.Count; c++)
        {
            string name = string.IsNullOrWhiteSpace(headers[c]) ? $"Col{c + 1}" : headers[c].Trim();
            string key = $"||col{c}|{name}";
            if (!result.TryGetValue(key, out var ch))
            {
                ch = new DataChannel { Name = name };
                result[key] = ch;
            }
            channels[c] = ch;
        }

        double phaseSpan = Math.Max(0.0, rowsPhaseEnd - rowsPhaseStart);
        for (int r = dataStartRow; r < rows.Count; r++)
        {
            if ((r & 0x3FF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(rowsPhaseStart + (r * phaseSpan / Math.Max(1, rows.Count)));
            }

            var cols = rows[r];
            if (IsRowEmpty(cols))
            {
                continue;
            }

            int n = Math.Min(cols.Count, channels.Length);
            for (int c = 0; c < n; c++)
            {
                if (TryParseNumber(cols[c], out double d))
                {
                    channels[c]!.Values.Add(d);
                }
            }
        }
    }

    private static bool IsRowEmpty(List<string> row)
    {
        for (int i = 0; i < row.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(row[i]))
            {
                return false;
            }
        }
        return true;
    }

    // ===== 解析: XLSX (Office Open XML / SpreadsheetML 2007+) =====
    // xlsx 即一个 zip：
    //   xl/sharedStrings.xml       —— 共享字符串表（可能不存在）
    //   xl/workbook.xml             —— 工作表清单（<sheets><sheet name="..." r:id="rIdN"/></sheets>）
    //   xl/_rels/workbook.xml.rels  —— rId → 部件相对路径
    //   xl/worksheets/sheetN.xml    —— 实际表格
    // sheetName 为 null/空 时取第一张表。
    private static Dictionary<string, DataChannel> ParseXlsx(string path, string? sheetName, bool enforceRequired, IProgress<double>? progress, CancellationToken ct)
    {
        var result = new Dictionary<string, DataChannel>(StringComparer.Ordinal);

        using var archive = System.IO.Compression.ZipFile.OpenRead(path);

        // 共享字符串
        var sharedStrings = ReadSharedStrings(archive, ct);
        progress?.Report(20);

        // 解析 workbook.xml + rels，建立 sheet name → 部件路径 映射
        var sheetMap = ReadXlsxSheetMap(archive);

        System.IO.Compression.ZipArchiveEntry? sheetEntry = null;
        if (sheetMap.Count > 0)
        {
            string? targetPart = null;
            if (string.IsNullOrEmpty(sheetName))
            {
                targetPart = sheetMap[0].Part;
            }
            else
            {
                foreach (var (n, p) in sheetMap)
                {
                    if (string.Equals(n, sheetName, StringComparison.Ordinal))
                    {
                        targetPart = p;
                        break;
                    }
                }
            }

            if (targetPart is not null)
            {
                sheetEntry = archive.GetEntry(targetPart);
            }
        }

        // workbook 解析失败时回退：按文件名字典序找第一个 sheetN.xml
        if (sheetEntry is null)
        {
            foreach (var e in archive.Entries)
            {
                string name = e.FullName.Replace('\\', '/');
                if (name.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    if (sheetEntry is null
                        || string.Compare(name, sheetEntry.FullName, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        sheetEntry = e;
                    }
                }
            }
        }

        if (sheetEntry is null)
        {
            throw new InvalidDataException("XLSX 中未找到工作表 (xl/worksheets/sheet*.xml)。");
        }

        var rows = ReadSheetRows(sheetEntry, sharedStrings, progress, ct);
        if (rows.Count == 0)
        {
            return result;
        }

        BuildChannelsFromRows(rows, result, progress, ct, rowsPhaseStart: 80.0, rowsPhaseEnd: 100.0, enforceRequired: enforceRequired);
        return result;
    }

    /// <summary>列出 xlsx 中所有工作表名称（按 workbook.xml 顺序）。失败返回空列表。</summary>
    public static List<string> ListXlsxSheetNames(string path)
    {
        var names = new List<string>();
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(path);
            foreach (var (n, _) in ReadXlsxSheetMap(archive))
            {
                names.Add(n);
            }
        }
        catch
        {
            // ignore — 调用方按空列表处理
        }
        return names;
    }

    /// <summary>列出 XLS (XML Spreadsheet 2003) 中所有 Worksheet 名称（按出现顺序）。失败返回空列表。</summary>
    public static List<string> ListXlsXmlSheetNames(string path)
    {
        var names = new List<string>();
        try
        {
            var settings = new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreWhitespace = true,
                IgnoreProcessingInstructions = true,
                DtdProcessing = DtdProcessing.Ignore,
            };
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element
                    && string.Equals(reader.LocalName, "Worksheet", StringComparison.OrdinalIgnoreCase))
                {
                    string name = reader.GetAttribute("Name", "urn:schemas-microsoft-com:office:spreadsheet")
                        ?? reader.GetAttribute("ss:Name")
                        ?? reader.GetAttribute("Name")
                        ?? string.Empty;
                    if (!string.IsNullOrEmpty(name))
                    {
                        names.Add(name);
                    }
                }
            }
        }
        catch
        {
            // ignore
        }
        return names;
    }

    /// <summary>读取 xlsx 的 sheet 列表（顺序保留），返回 (Name, PartPath) 列表，PartPath 形如 "xl/worksheets/sheet1.xml"。</summary>
    private static List<(string Name, string Part)> ReadXlsxSheetMap(System.IO.Compression.ZipArchive archive)
    {
        var result = new List<(string Name, string Part)>();
        var workbook = archive.GetEntry("xl/workbook.xml");
        var rels = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (workbook is null || rels is null)
        {
            return result;
        }

        // rels: <Relationship Id="rId1" Target="worksheets/sheet1.xml" .../>
        var relMap = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
            using var s = rels.Open();
            using var r = XmlReader.Create(s, settings);
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "Relationship")
                {
                    string id = r.GetAttribute("Id") ?? string.Empty;
                    string tgt = r.GetAttribute("Target") ?? string.Empty;
                    if (id.Length > 0 && tgt.Length > 0)
                    {
                        relMap[id] = tgt;
                    }
                }
            }
        }
        catch
        {
            return result;
        }

        // workbook: <sheets><sheet name="..." sheetId="1" r:id="rId1"/></sheets>
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
            using var s = workbook.Open();
            using var r = XmlReader.Create(s, settings);
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "sheet")
                {
                    string name = r.GetAttribute("name") ?? string.Empty;
                    string rid = r.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships")
                        ?? r.GetAttribute("r:id")
                        ?? string.Empty;
                    if (rid.Length > 0 && relMap.TryGetValue(rid, out var target))
                    {
                        // target 通常相对于 xl/，例如 "worksheets/sheet1.xml"；也可能 "/xl/worksheets/sheet1.xml"
                        string part = target.StartsWith("/", StringComparison.Ordinal)
                            ? target.TrimStart('/')
                            : "xl/" + target;
                        part = part.Replace('\\', '/');
                        result.Add((name, part));
                    }
                }
            }
        }
        catch
        {
            // 部分失败时返回已收集到的
        }

        return result;
    }

    private static List<string> ReadSharedStrings(System.IO.Compression.ZipArchive archive, CancellationToken ct)
    {
        var list = new List<string>();
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return list;
        }

        var settings = new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = false,
            IgnoreProcessingInstructions = true,
            DtdProcessing = DtdProcessing.Ignore,
        };

        using var s = entry.Open();
        using var reader = XmlReader.Create(s, settings);
        var sb = new StringBuilder();
        bool inSi = false;
        bool inT = false;
        bool inRPh = false; // <rPh> = phonetic guide (CJK 拼音/かな)，需排除以免污染主文本
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.Element)
            {
                string local = reader.LocalName;
                if (local == "si")
                {
                    inSi = true;
                    inRPh = false;
                    sb.Clear();
                    if (reader.IsEmptyElement)
                    {
                        list.Add(string.Empty);
                        inSi = false;
                    }
                }
                else if (inSi && local == "rPh")
                {
                    if (!reader.IsEmptyElement)
                    {
                        inRPh = true;
                    }
                }
                else if (inSi && !inRPh && local == "t")
                {
                    if (!reader.IsEmptyElement)
                    {
                        inT = true;
                    }
                }
            }
            else if ((reader.NodeType == XmlNodeType.Text
                      || reader.NodeType == XmlNodeType.CDATA
                      || reader.NodeType == XmlNodeType.Whitespace
                      || reader.NodeType == XmlNodeType.SignificantWhitespace)
                     && inT)
            {
                _ = sb.Append(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                string local = reader.LocalName;
                if (local == "t")
                {
                    inT = false;
                }
                else if (local == "rPh")
                {
                    inRPh = false;
                }
                else if (local == "si")
                {
                    list.Add(sb.ToString());
                    inSi = false;
                }
            }
        }
        return list;
    }

    private static List<List<string>> ReadSheetRows(
        System.IO.Compression.ZipArchiveEntry sheetEntry,
        List<string> sharedStrings,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var rows = new List<List<string>>();
        var settings = new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
            DtdProcessing = DtdProcessing.Ignore,
        };

        long total = Math.Max(1, sheetEntry.Length);
        using var s = sheetEntry.Open();
        // ZipArchiveEntry.Open 返回 DeflateStream，无 Position；改用计数读取以估算进度。
        using var counting = new CountingStream(s);
        using var reader = XmlReader.Create(counting, settings);

        var current = new List<string>();
        string? cellType = null;
        bool inV = false;
        bool inIsT = false; // <is><t>
        var cellText = new StringBuilder();
        long readCounter = 0;

        while (reader.Read())
        {
            if ((++readCounter & 0xFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(20.0 + counting.BytesRead * 60.0 / total);
            }

            if (reader.NodeType == XmlNodeType.Element)
            {
                string local = reader.LocalName;
                if (local == "row")
                {
                    current = [];
                }
                else if (local == "c")
                {
                    cellType = reader.GetAttribute("t");
                    cellText.Clear();
                }
                else if (local == "v")
                {
                    if (!reader.IsEmptyElement)
                    {
                        inV = true;
                    }
                }
                else if (local == "t")
                {
                    // <is><t>...</t></is> for inline strings
                    if (!reader.IsEmptyElement)
                    {
                        inIsT = true;
                    }
                }
            }
            else if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.Whitespace
                     || reader.NodeType == XmlNodeType.SignificantWhitespace || reader.NodeType == XmlNodeType.CDATA)
            {
                if (inV || inIsT)
                {
                    cellText.Append(reader.Value);
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                string local = reader.LocalName;
                if (local == "v")
                {
                    inV = false;
                }
                else if (local == "t")
                {
                    inIsT = false;
                }
                else if (local == "c")
                {
                    string raw = cellText.ToString();
                    string text = cellType switch
                    {
                        "s" when int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)
                            && idx >= 0 && idx < sharedStrings.Count => sharedStrings[idx],
                        "str" or "inlineStr" => raw,
                        "b" => raw == "1" ? "true" : "false",
                        _ => raw,
                    };
                    current.Add(text);
                }
                else if (local == "row")
                {
                    rows.Add(current);
                }
            }
        }

        return rows;
    }

    /// <summary>包装一个只前进的流，记录已读字节数用于进度估算（适用于 DeflateStream 这种无 Position 的流）。</summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        public long BytesRead { get; private set; }
        public CountingStream(Stream inner) { _inner = inner; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _inner.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private static bool TryParseNumber(string raw, out double value)
    {
        if (string.IsNullOrEmpty(raw))
        {
            value = 0;
            return false;
        }

        // 支持: 普通十进制 / 0xHEX / 布尔
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("0X", StringComparison.Ordinal))
        {
            if (long.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long h))
            {
                value = h;
                return true;
            }
        }

        if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase))
        {
            value = 1;
            return true;
        }

        if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
        {
            value = 0;
            return true;
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static List<string> SplitDelimited(string line, char sep)
    {
        var list = new List<string>(16);
        if (sep == '\t')
        {
            list.AddRange(line.Split('\t'));
            return list;
        }

        // CSV: 处理双引号转义
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        _ = sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    _ = sb.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == sep)
                {
                    list.Add(sb.ToString());
                    _ = sb.Clear();
                }
                else
                {
                    _ = sb.Append(c);
                }
            }
        }

        list.Add(sb.ToString());
        return list;
    }

    private static int IndexOf(List<string> headers, string name)
    {
        for (int i = 0; i < headers.Count; i++)
        {
            if (string.Equals(headers[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Get(List<string> cols, int idx)
        => idx >= 0 && idx < cols.Count ? cols[idx] : string.Empty;

    // ════════════════════════════════════════════════════════════════════════
    //  线程安全快照（供 RebuildPlot 使用）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 在 <c>_liveLock</c> 保护下对所有通道做数据快照，返回 (通道元信息, 值数组副本) 列表。<br/>
    /// View 层的 RebuildPlot 应调用此方法而非直接读取 <see cref="DataChannel.Values"/>，
    /// 以避免后台在线接收线程并发修改时产生数据竞争（导致偶发零值等错误）。
    /// </summary>
    public (DataChannel Channel, double[] Values)[] SnapshotChannels()
    {
        lock (_liveLock)
        {
            var channels = Channels;
            var result = new (DataChannel, double[])[channels.Count];
            for (int i = 0; i < channels.Count; i++)
            {
                result[i] = (channels[i], channels[i].Values.ToArray());
            }
            return result;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  在线接收（静默模式）—— 解析 ">name:val,name:val\r\n" 并实时刷新通道
    // ════════════════════════════════════════════════════════════════════════

    partial void OnIsLiveReceiveEnabledChanged(bool value)
    {
        if (value)
        {
            StartLiveReceive();
        }
        else
        {
            StopLiveReceive();
        }
    }

    partial void OnLiveDisplayCapacityChanged(int value)
    {
        if (value < 10)
        {
            // 静默纠回（OnPropertyChanged 已经触发，再次赋值不会无限递归因为值相等）
            LiveDisplayCapacity = 10;
            return;
        }
        if (value > 1_000_000)
        {
            LiveDisplayCapacity = 1_000_000;
            return;
        }
        ResizeLiveBuffers(value);
    }

    partial void OnLiveOverflowModeChanged(WaveOverflowMode value)
    {
        OnPropertyChanged(nameof(LiveOverflowModeIndex));
        // 切换模式：重置写指针，并按新模式调整缓冲。
        lock (_liveLock)
        {
            _liveSampleIndex = 0;
            int cap = Math.Max(1, LiveDisplayCapacity);
            foreach (var ch in Channels)
            {
                ResetLiveChannelBuffer(ch, cap, value);
            }
        }
        LiveFrameReceived?.Invoke();
    }

    private void StartLiveReceive()
    {
        if (_deviceAddViewModel is null)
        {
            IsLiveReceiveEnabled = false;
            return;
        }

        var modbus = _deviceAddViewModel.ModbusMaster;
        if (!modbus.IsOpen)
        {
            // 串口尚未打开：开关已开但用户还未连接，等到连接时由 OnLiveReceiveConnectionReady 触发
            StatusText = "在线接收：等待 Modbus 串口连接";
            return;
        }

        // 清空旧通道，重新开始
        Channels = [];
        TotalChannels = 0;
        TotalSamples = 0;
        _liveSampleIndex = 0;
        ChannelsReplaced?.Invoke();

        modbus.SilentFrameReceived += OnModbusSilentFrame;
        if (!modbus.StartSilentReceive())
        {
            modbus.SilentFrameReceived -= OnModbusSilentFrame;
            StatusText = "在线接收启动失败（串口未打开）";
            IsLiveReceiveEnabled = false;
            return;
        }
        StatusText = "在线接收：已进入静默模式";
    }

    private void StopLiveReceive()
    {
        if (_deviceAddViewModel is null)
        {
            return;
        }
        var modbus = _deviceAddViewModel.ModbusMaster;
        modbus.SilentFrameReceived -= OnModbusSilentFrame;
        try { modbus.StopSilentReceive(); } catch { /* ignore */ }
        StatusText = "在线接收：已停止";
    }

    private void OnModbusSilentFrame(string line)
    {
        using var __diag = Services.LiveDiag.Scoped("vm.frame");
        // 期望帧：">name:val,name:val,..."
        if (string.IsNullOrEmpty(line))
        {
            return;
        }
        // 容忍非帧前缀（找到第一个 '>' 即起点）
        int startIdx = line.IndexOf('>');
        if (startIdx < 0)
        {
            return;
        }
        string body = line.Substring(startIdx + 1);
        if (body.Length == 0)
        {
            return;
        }

        var pairs = body.Split(',');
        // 解析并去重
        var parsed = new List<(string name, double val)>(pairs.Length);
        foreach (var raw in pairs)
        {
            int colon = raw.IndexOf(':');
            if (colon <= 0 || colon >= raw.Length - 1)
            {
                continue;
            }
            string name = raw.Substring(0, colon).Trim();
            string vstr = raw.Substring(colon + 1).Trim();
            if (name.Length == 0 || vstr.Length == 0)
            {
                continue;
            }
            if (!double.TryParse(vstr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v))
            {
                continue;
            }
            parsed.Add((name, v));
        }
        if (parsed.Count == 0)
        {
            return;
        }

        // 新通道需要在 UI 线程下加入 ObservableCollection
        List<DataChannel>? toAddOnUi = null;
        int cap;
        WaveOverflowMode mode;
        lock (_liveLock)
        {
            cap = Math.Max(1, LiveDisplayCapacity);
            mode = LiveOverflowMode;

            foreach (var (name, _) in parsed)
            {
                if (FindLiveChannel(name) is null)
                {
                    var ch = CreateLiveChannel(name, cap, mode);
                    (toAddOnUi ??= []).Add(ch);
                }
            }
        }

        if (toAddOnUi is { Count: > 0 })
        {
            Services.LiveDiag.Tick("vm.addChannel");
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(() => AddLiveChannelsOnUi(toAddOnUi));
            }
            else
            {
                AddLiveChannelsOnUi(toAddOnUi);
            }
        }

        // 写入数据
        lock (_liveLock)
        {
            cap = Math.Max(1, LiveDisplayCapacity);
            mode = LiveOverflowMode;
            if (mode == WaveOverflowMode.Sweep)
            {
                int writePos = _liveSampleIndex % cap;
                foreach (var (name, val) in parsed)
                {
                    var ch = FindLiveChannel(name);
                    if (ch is null)
                    {
                        continue;
                    }
                    EnsureLiveBuffer(ch, cap, mode);
                    ch.LiveBuffer![writePos] = val;
                    // Sweep 模式整段缓冲始终参与显示（首圈未覆盖处为 NaN，会被绘制跳过）
                    if (ch.LiveValidCount < cap)
                    {
                        ch.LiveValidCount = cap;
                    }
                }
                _liveSampleIndex++;
            }
            else // Roll
            {
                foreach (var (name, val) in parsed)
                {
                    var ch = FindLiveChannel(name);
                    if (ch is null)
                    {
                        continue;
                    }
                    EnsureLiveBuffer(ch, cap, mode);
                    var buf = ch.LiveBuffer!;
                    int count = ch.LiveValidCount;
                    if (count < cap)
                    {
                        // 缓冲未满：直接追加
                        buf[count] = val;
                        ch.LiveValidCount = count + 1;
                    }
                    else
                    {
                        // 缓冲已满：整体左移一格再写入末尾。
                        // cap=1000 时一次 8KB memcpy，远比每帧重新分配新数组+SignalSourceDouble 廉价。
                        Array.Copy(buf, 1, buf, 0, cap - 1);
                        buf[cap - 1] = val;
                    }
                }
                _liveSampleIndex++;
            }
        }

        // 通知 View 节流刷新
        LiveFrameReceived?.Invoke();
    }

    private DataChannel? FindLiveChannel(string name)
    {
        foreach (var ch in Channels)
        {
            if (string.Equals(ch.Name, name, StringComparison.Ordinal))
            {
                return ch;
            }
        }
        return null;
    }

    private static DataChannel CreateLiveChannel(string name, int capacity, WaveOverflowMode mode)
    {
        var ch = new DataChannel
        {
            Name = name,
            Group = "在线",
            Source = "Live",
            DataType = "REAL",
            IsVisible = true,
        };
        InitLiveBuffer(ch, capacity, mode);
        return ch;
    }

    /// <summary>
    /// 为通道分配/重设 <see cref="DataChannel.LiveBuffer"/>。Sweep 模式初始全 NaN（整段始终参与绘制）；
    /// Roll 模式初始空、<c>LiveValidCount=0</c>，由写入逐步填满。
    /// </summary>
    private static void InitLiveBuffer(DataChannel ch, int capacity, WaveOverflowMode mode)
    {
        ch.LiveBuffer = new double[capacity];
        if (mode == WaveOverflowMode.Sweep)
        {
            Array.Fill(ch.LiveBuffer, double.NaN);
            ch.LiveValidCount = capacity;
        }
        else
        {
            // Roll 模式：从 0 开始填充，未填充处的 NaN 标记让 ScottPlot 跳过
            Array.Fill(ch.LiveBuffer, double.NaN);
            ch.LiveValidCount = 0;
        }
    }

    /// <summary>
    /// 确保通道的 LiveBuffer 已按当前 <paramref name="capacity"/> 分配。容量变化时重建并复制旧数据。
    /// </summary>
    private static void EnsureLiveBuffer(DataChannel ch, int capacity, WaveOverflowMode mode)
    {
        if (ch.LiveBuffer is null || ch.LiveBuffer.Length != capacity)
        {
            var oldBuf = ch.LiveBuffer;
            int oldValid = ch.LiveValidCount;
            ch.LiveBuffer = new double[capacity];
            Array.Fill(ch.LiveBuffer, double.NaN);
            if (oldBuf is not null && oldValid > 0)
            {
                // 保留尾部数据（Roll 语义：保留最近 capacity 个样本）
                int srcStart = Math.Max(0, oldValid - capacity);
                int copyCount = Math.Min(oldValid - srcStart, capacity);
                if (copyCount > 0)
                {
                    Array.Copy(oldBuf, srcStart, ch.LiveBuffer, 0, copyCount);
                }
                ch.LiveValidCount = mode == WaveOverflowMode.Sweep ? capacity : copyCount;
            }
            else
            {
                ch.LiveValidCount = mode == WaveOverflowMode.Sweep ? capacity : 0;
            }
        }
    }

    private void AddLiveChannelsOnUi(List<DataChannel> toAdd)
    {
        // 按当前通道数为新增项分配颜色与编号
        foreach (var ch in toAdd)
        {
            Channels.Add(ch);
        }
        // 重新分配颜色（按色环均匀分布）
        int total = Channels.Count;
        for (int i = 0; i < total; i++)
        {
            Channels[i].ChannelIndex = i + 1;
            Channels[i].ChannelLabel = $"CH{i + 1}";
            Channels[i].ColorHex = GetChannelColorHex(i, total);
        }
        TotalChannels = total;
        ChannelsReplaced?.Invoke();
    }

    private static void EnsureSweepBufferCapacity(DataChannel ch, int capacity)
    {
        // 旧 List<double> 路径已废弃；保留空壳避免外部潜在调用。改走 EnsureLiveBuffer。
        EnsureLiveBuffer(ch, capacity, WaveOverflowMode.Sweep);
    }

    private void ResizeLiveBuffers(int newCapacity)
    {
        lock (_liveLock)
        {
            int cap = Math.Max(1, newCapacity);
            foreach (var ch in Channels)
            {
                EnsureLiveBuffer(ch, cap, LiveOverflowMode);
            }
            // Sweep 写指针不超过容量
            if (LiveOverflowMode == WaveOverflowMode.Sweep)
            {
                _liveSampleIndex = _liveSampleIndex % cap;
            }
        }
        // 通知 View 触发一次全量重建（绑定新数组）
        ChannelsReplaced?.Invoke();
        LiveFrameReceived?.Invoke();
    }

    private static void ResetLiveChannelBuffer(DataChannel ch, int capacity, WaveOverflowMode mode)
    {
        InitLiveBuffer(ch, capacity, mode);
    }
}

/// <summary>波形窗超过显示容量后的策略。</summary>
public enum WaveOverflowMode
{
    /// <summary>滚动：新样本追加，丢弃最旧。</summary>
    Roll = 0,
    /// <summary>扫屏：写指针在固定缓冲内循环覆盖（示波器扫屏式）。</summary>
    Sweep = 1,
}

/// <summary>
/// 一个变量通道 — X 轴: 0..N-1 (按存储顺序)，Y 轴: <see cref="Values"/>。
/// </summary>
public partial class DataChannel : ObservableObject
{
    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private int _channelIndex;

    [ObservableProperty]
    private string _channelLabel = string.Empty;

    /// <summary>通道颜色（#RRGGBB 字符串），用于波形与按钮指示。</summary>
    [ObservableProperty]
    private string _colorHex = "#FFEB3B";

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _group = string.Empty;

    [ObservableProperty]
    private string _source = string.Empty;

    [ObservableProperty]
    private string _sdoIndex = string.Empty;

    [ObservableProperty]
    private string _sdoSubIndex = string.Empty;

    [ObservableProperty]
    private string _dataType = string.Empty;

    [ObservableProperty]
    private string _unit = string.Empty;

    /// <summary>是否可绘制为波形（数值型）。字符串/八位组等非数值类型为 false，UI 上禁用切换。</summary>
    [ObservableProperty]
    private bool _isPlottable = true;

    partial void OnDataTypeChanged(string value)
    {
        IsPlottable = IsPlottableType(value);
    }

    /// <summary>
    /// 根据 DataType 字符串（如 "string"、"UINT32"、"HEX16"、"REAL"…）判断是否可参与波形绘制。
    /// 凡是非数值/位串/字符串/八位组等不可数值化的类型一律返回 false。
    /// </summary>
    public static bool IsPlottableType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            return true; // 未声明时默认允许（仍受 Values.Count > 0 影响）
        }

        string t = dataType.Trim().ToUpperInvariant();
        // 字符串/字节数组/可见字符串/Unicode串：不可绘制
        if (t.Contains("STRING") || t.Contains("OCTET") || t.Contains("UNICODE") || t.Contains("CHAR"))
        {
            return false;
        }
        // 已知数值/布尔/枚举/位串/HEX/REAL/INT/UINT/FLOAT/DOUBLE/DECIMAL/VARIANT 视为可绘制
        if (t.Contains("BOOL") || t.Contains("BIT") || t.Contains("BYTE")
            || t.Contains("INT") || t.Contains("UINT")
            || t.Contains("HEX") || t.Contains("REAL") || t.Contains("FLOAT")
            || t.Contains("DOUBLE") || t.Contains("DECIMAL")
            || t.Contains("ENUM") || t.Contains("NUM")
            || t == "VARIANT")
        {
            return true;
        }
        // 其它未知类型：保守允许，由 TryParseNumber 决定是否真的入图
        return true;
    }

    /// <summary>Y 值序列（按存储顺序）。</summary>
    public List<double> Values { get; } = new(1024);

    /// <summary>对应时间戳（如可解析），与 <see cref="Values"/> 等长或缺省。</summary>
    public List<DateTime> Timestamps { get; } = new(1024);

    public int Count => Values.Count;

    // ── 在线接收（高速流）专用预分配缓冲 ──────────────────────────────
    /// <summary>
    /// 在线接收模式专用的预分配 Y 缓冲（长度 = 显示容量）。<br/>
    /// 后台线程直接按下标写入单个 <c>double</c>，64-bit 平台单点写原子，无需 lock。
    /// View 把 ScottPlot <c>Signal</c> 的 <c>SignalSourceDouble</c> 绑定到此数组后，
    /// 每帧零分配只刷新即可。<br/>
    /// 静态文件加载场景下保持为 <c>null</c>，继续使用 <see cref="Values"/>。
    /// </summary>
    public double[]? LiveBuffer { get; set; }

    /// <summary>
    /// Roll 模式下已写入的有效样本数（≤ <see cref="LiveBuffer"/>.Length）。<br/>
    /// 用作 ScottPlot <c>SignalSourceDouble.MaximumIndex</c> 上限，避免显示尾部 NaN。
    /// </summary>
    public int LiveValidCount { get; set; }
    // ────────────────────────────────────────────────────────────────────

    public string DisplayLabel =>
        string.IsNullOrEmpty(Unit) ? Name : $"{Name} [{Unit}]";
}
