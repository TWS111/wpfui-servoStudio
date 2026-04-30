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
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.Services;

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

    public DataViewViewModel()
    {
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
            Filter = "数据文件|*.csv;*.tsv;*.jsonl;*.xls|CSV (*.csv)|*.csv|TSV (*.tsv)|*.tsv|JSONL (*.jsonl)|*.jsonl|XLS (*.xls)|*.xls|所有文件|*.*",
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

        await LoadFileAsync(dialog.FileName);
    }

    private bool CanLoadFile() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task OnReload()
    {
        if (!string.IsNullOrEmpty(CurrentFilePath) && File.Exists(CurrentFilePath))
        {
            await LoadFileAsync(CurrentFilePath);
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
        _ = LoadFileAsync(path);
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

    public async Task LoadFileAsync(string path)
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
            Dictionary<string, DataChannel> dict = await Task.Run(
                () => ext switch
                {
                    ".jsonl" => ParseJsonLines(path, progress, ct),
                    ".tsv" => ParseDelimited(path, '\t', progress, ct),
                    ".xls" => ParseXlsXml(path, progress, ct),
                    _ => ParseDelimited(path, ',', progress, ct),
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
    private static Dictionary<string, DataChannel> ParseDelimited(string path, char sep, IProgress<double>? progress, CancellationToken ct)
    {
        var result = new Dictionary<string, DataChannel>(StringComparer.Ordinal);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long total = Math.Max(1, stream.Length);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        string? header = reader.ReadLine();
        if (header == null)
        {
            return result;
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
            throw new InvalidDataException("文件表头不含必要列 (Name/Value)。");
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
    // 由 DataFrameLogger 写入：<Workbook>/<Worksheet>/<Table>/<Row>/<Cell>/<Data>...
    // 第一行 <Row> 为表头列名，后续每行为一条样本记录（与 CSV/TSV 相同的字段顺序）。
    private static Dictionary<string, DataChannel> ParseXlsXml(string path, IProgress<double>? progress, CancellationToken ct)
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
                if (string.Equals(local, "Row", StringComparison.OrdinalIgnoreCase))
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
                    rows.Add(currentRow);
                    inRow = false;
                }
            }
        }

        if (rows.Count == 0)
        {
            return result;
        }

        var headers = rows[0];
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
            throw new InvalidDataException("XLS 文件表头不含必要列 (Name)。");
        }

        // 可选的 "#TYPE" 类型行（首列为哨兵）：有则跳过。
        int dataStartRow = 1;
        if (rows.Count > 1 && rows[1].Count > 0
            && string.Equals(rows[1][0], "#TYPE", StringComparison.OrdinalIgnoreCase))
        {
            dataStartRow = 2;
        }

        for (int r = dataStartRow; r < rows.Count; r++)
        {
            if ((r & 0x3FF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(80.0 + (r * 20.0 / rows.Count));
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

        return result;
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

    public string DisplayLabel =>
        string.IsNullOrEmpty(Unit) ? Name : $"{Name} [{Unit}]";
}
