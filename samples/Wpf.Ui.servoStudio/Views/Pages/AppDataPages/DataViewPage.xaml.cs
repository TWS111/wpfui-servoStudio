// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.ComponentModel;
using System.IO;
using ScottPlot;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.servoStudio.Services;
using Wpf.Ui.servoStudio.ViewModels.AppData;

namespace Wpf.Ui.servoStudio.Views.Pages.AppDataPages;

public partial class DataViewPage : INavigableView<DataViewViewModel>
{
    /// <summary>已订阅 PropertyChanged 的通道集合，用于在卸载/替换时取消订阅。</summary>
    private readonly HashSet<DataChannel> _subscribedChannels = [];

    /// <summary>ScottPlot 中实际可用的中文字体名（通过 Fonts.AddFontFile 注册后返回）。</summary>
    private static readonly string CjkFont = RegisterCjkFont();

    public DataViewViewModel ViewModel { get; }

    public DataViewPage(DataViewViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();

        // ScottPlot 5 的 SkiaSharp 渲染默认字体不包含 CJK 字形，未注册前会显示为方框。
        // CjkFont 在静态构造时已通过 Fonts.AddFontFile 注册了 Windows 自带的字体文件；
        // 但 LabelStyle (Title/XLabel/YLabel) 在 ScottPlot 5 中走 SKTypeface.FromFamilyName
        // 解析路径，自定义注册名无法命中，会回退到不含 CJK 的默认字体。
        // 因此对带中文的标题/轴名/刻度/图例，统一用 Fonts.Detect(text) 让 ScottPlot 在
        // 系统字体里挑一个能渲染对应字符的家族名。
        ScottPlot.Fonts.Default = CjkFont;

        // 从磁盘恢复波形窗深/浅主题与图例字号
        var s0 = UserSettingsService.Load();
        _isDarkTheme = s0.DataView_IsDarkTheme;
        _legendFontSize = Math.Clamp(
            s0.DataView_LegendFontSize > 0 ? s0.DataView_LegendFontSize : 16f,
            LegendFontSizeMin, LegendFontSizeMax);

        Plot.Plot.Title("数据导入查看");
        Plot.Plot.XLabel("样本序号");
        Plot.Plot.YLabel("值");
        ApplyCjkFont();
        Plot.Plot.ShowLegend();
        ApplyTheme();
        Plot.Refresh();

        ViewModel.ChannelsReplaced += OnChannelsReplaced;
        ViewModel.LiveFrameReceived += OnLiveFrameReceived;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 上一次 <see cref="OnCompositionRendering"/> 实际执行刷新的时间戳（毫秒）。<br/>
    /// CompositionTarget.Rendering 会以显示器刷新率（~60Hz）触发，过高帧率下 ScottPlot
    /// 重绘与数据更新周期并不匹配，节流至 ~20fps 既平滑又节省 GPU/CPU。
    /// </summary>
    private long _liveLastRenderMs;

    /// <summary>是否已订阅 CompositionTarget.Rendering（防重复订阅）。</summary>
    private bool _liveRenderingHooked;

    /// <summary>最小重绘间隔，50ms = 20fps。</summary>
    private const int LiveMinRefreshIntervalMs = 50;

    private readonly System.Diagnostics.Stopwatch _liveClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>静默模式下后台线程置位，UI tick 时若被置位则刷新波形。</summary>
    private volatile bool _liveDirty;

    /// <summary>是否已手动暂停波形刷新（按下“暂停刷新”按钮时置 true）。
    /// 暂停期间后台 Modbus 线程仍继续接收数据，只是不刷新画面。</summary>
    private bool _isLivePaused;

    /// <summary>
    /// 在线接收模式下按通道名缓存的持久 Signal 对象。<br/>
    /// 每帧只更新 <c>Signal.Data.Ys</c>，跳过 Clear/Add/AutoScale/ApplyCjkFont/ApplyTheme，
    /// 将 UI 线程每帧负荷从 O(字体匹配 × 帧率) 降至 O(渲染)，消除周期性卡顿。
    /// </summary>
    private Dictionary<string, ScottPlot.Plottables.Signal>? _liveSignals;

    /// <summary>
    /// 与 <see cref="_liveSignals"/> 同步：每个 Signal 当前绑定的 <see cref="DataChannel.LiveBuffer"/>
    /// 引用。容量变化时通道会重建 LiveBuffer，此处用引用相等检测来触发 Signal 重建。
    /// </summary>
    private Dictionary<string, double[]>? _liveBoundBuffers;

    private void OnLiveFrameReceived()
    {
        _liveDirty = true;
        Services.LiveDiag.Tick("frame.recv");
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DataViewViewModel.IsLiveReceiveEnabled))
        {
            if (ViewModel.IsLiveReceiveEnabled)
            {
                Services.LiveDiag.Enable();
                _liveSignals = null;  // 强制下次 tick 全量重建
                _liveBoundBuffers = null;
                _liveDirty = true;
                HookLiveRendering();
            }
            else
            {
                UnhookLiveRendering();
                _liveSignals = null;
                _liveBoundBuffers = null;
                // 恢复暂停状态，重置按钮外观
                _isLivePaused = false;
                LivePauseButton.Visibility = System.Windows.Visibility.Visible;
                LiveResumeButton.Visibility = System.Windows.Visibility.Collapsed;
                // 停止后做一次全量重建（含 AutoScale / 字体 / 主题）
                RebuildPlot();
                Services.LiveDiag.Flush();
                Services.LiveDiag.Disable();
            }
        }
    }

    private void HookLiveRendering()
    {
        if (_liveRenderingHooked)
        {
            return;
        }
        System.Windows.Media.CompositionTarget.Rendering += OnCompositionRendering;
        _liveRenderingHooked = true;
    }

    private void UnhookLiveRendering()
    {
        if (!_liveRenderingHooked)
        {
            return;
        }
        System.Windows.Media.CompositionTarget.Rendering -= OnCompositionRendering;
        _liveRenderingHooked = false;
    }

    /// <summary>
    /// 代替 <see cref="DispatcherTimer"/> 的高优先级帧同步回调。CompositionTarget.Rendering 在 WPF
    /// 合成线程准备帧时触发，不会被本身的渲染任务饣死，与 Render 优先级同阶。
    /// 按 <see cref="LiveMinRefreshIntervalMs"/> 内部节流到 20fps。
    /// </summary>
    private void OnPauseLiveRefresh(object sender, System.Windows.RoutedEventArgs e)
    {
        _isLivePaused = true;
        LivePauseButton.Visibility = System.Windows.Visibility.Collapsed;
        LiveResumeButton.Visibility = System.Windows.Visibility.Visible;
    }

    private void OnResumeLiveRefresh(object sender, System.Windows.RoutedEventArgs e)
    {
        _isLivePaused = false;
        _liveDirty = true;  // 立即触发一帧刷新
        LiveResumeButton.Visibility = System.Windows.Visibility.Collapsed;
        LivePauseButton.Visibility = System.Windows.Visibility.Visible;
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        if (_isLivePaused)
        {
            return;
        }
        if (!_liveDirty)
        {
            Services.LiveDiag.Tick("tick.idle");
            return;
        }
        long now = _liveClock.ElapsedMilliseconds;
        if (now - _liveLastRenderMs < LiveMinRefreshIntervalMs)
        {
            Services.LiveDiag.Tick("tick.throttle");
            return;
        }
        _liveLastRenderMs = now;
        _liveDirty = false;
        using (Services.LiveDiag.Scoped("tick.total"))
        {
            UpdateLivePlot();
        }
    }

    /// <summary>
    /// 在线接收专用轻量帧刷新。<br/>
    /// <b>动态流模式</b>（每帧执行）：仅 <c>SignalSourceDouble.MaximumIndex</c> 调整 +
    /// <c>AutoScaleY</c> + <c>Refresh()</c>。零 GC 分配、无字体匹配/主题/AutoScaleX。<br/>
    /// <b>全量重建</b>（首次 / 新增通道 / 容量变化）：复用 ViewModel 预分配的 <see cref="DataChannel.LiveBuffer"/>
    /// 创建 <c>Signal</c>，绑定一次后由动态路径直接使用。
    /// </summary>
    private void UpdateLivePlot()
    {
        var channels = ViewModel.Channels;

        // ── 判断是否需要全量重建：缓存为空 / 通道集合变化 / buffer 引用变化 ───────
        bool needFullRebuild = _liveSignals is null || _liveSignals.Count == 0;
        if (!needFullRebuild && _liveSignals is not null && _liveBoundBuffers is not null)
        {
            foreach (var ch in channels)
            {
                if (!ch.IsVisible || ch.LiveBuffer is null)
                {
                    continue;
                }
                if (!_liveSignals.ContainsKey(ch.Name))
                {
                    needFullRebuild = true;
                    break;
                }
                // 容量变化时 buffer 引用会被替换 → 需重建 SignalSourceDouble
                if (!_liveBoundBuffers.TryGetValue(ch.Name, out var boundBuf)
                    || !ReferenceEquals(boundBuf, ch.LiveBuffer))
                {
                    needFullRebuild = true;
                    break;
                }
            }
        }

        if (needFullRebuild)
        {
            using (Services.LiveDiag.Scoped("build.signals"))
            {
                BuildLiveSignals();
            }
            return;
        }

        // ── 动态流路径：更新 MaximumIndex + Y 自动缩放 + 刷新 ───────────────
        foreach (var ch in channels)
        {
            if (!ch.IsVisible || ch.LiveBuffer is null)
            {
                continue;
            }
            if (!_liveSignals!.TryGetValue(ch.Name, out var sig))
            {
                continue;
            }
            if (sig.Data is ScottPlot.DataSources.SignalSourceDouble src)
            {
                int maxIdx = Math.Max(0, ch.LiveValidCount - 1);
                if (src.MaximumIndex != maxIdx)
                {
                    src.MaximumIndex = maxIdx;
                }
            }
        }

        // 高速流场景下 Y 范围必须每帧重算才能跟随波形（这是用户卡顿原因之一：之前根本没调用）。
        // AutoScaleY 内部仅扫描各 Signal 的可见区间，开销 ~O(可见样本数)，远低于全量 RebuildPlot。
        using (Services.LiveDiag.Scoped("tick.autoscaleY"))
        {
            Plot.Plot.Axes.AutoScaleY();
        }
        using (Services.LiveDiag.Scoped("tick.refresh"))
        {
            Plot.Refresh();
        }
    }

    /// <summary>
    /// 用 <see cref="DataChannel.LiveBuffer"/> 重建所有在线 <c>Signal</c> 并应用字体/主题/AutoScale。
    /// 一次性绑定后由 <see cref="UpdateLivePlot"/> 的动态路径反复刷新而无需再分配。
    /// </summary>
    private void BuildLiveSignals()
    {
        _liveSignals = new Dictionary<string, ScottPlot.Plottables.Signal>(ViewModel.Channels.Count);
        _liveBoundBuffers = new Dictionary<string, double[]>(ViewModel.Channels.Count);
        Plot.Plot.Clear();
        bool any = false;
        foreach (var ch in ViewModel.Channels)
        {
            if (!ch.IsVisible || ch.LiveBuffer is null || ch.LiveBuffer.Length == 0)
            {
                continue;
            }
            // 直接绑定到通道预分配 buffer，不复制；后续每帧零分配。
            var src = new ScottPlot.DataSources.SignalSourceDouble(ch.LiveBuffer, 1.0)
            {
                MaximumIndex = Math.Max(0, ch.LiveValidCount - 1),
            };
            var sig = Plot.Plot.Add.Signal(src);
            sig.LegendText = $"{ch.ChannelLabel}  {ch.DisplayLabel}";
            sig.Color = ParseColor(ch.ColorHex);
            sig.LineWidth = 1.5f;
            _liveSignals[ch.Name] = sig;
            _liveBoundBuffers[ch.Name] = ch.LiveBuffer;
            any = true;
        }
        if (any)
        {
            Plot.Plot.Axes.AutoScale();
        }
        ApplyCjkFont();
        ApplyTheme();
        Plot.Refresh();
        UpdateXRangeBoxesFromPlot();
    }    /// <summary>
    /// 通过 <see cref="ScottPlot.Fonts.AddFontFile"/> 把 Windows 系统字体文件
    /// （Microsoft YaHei / SimSun 任何一个能找到的）注册给 SkiaSharp，
    /// 返回注册后的字体名。如果都不存在则回退为 ScottPlot 默认字体。
    /// </summary>
    private static string RegisterCjkFont()
    {
        try
        {
            string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            // 候选：雅黑(Win10/11) -> 雅黑UI -> 黑体 -> 宋体 -> 等线
            string[] candidates =
            {
                "msyh.ttc",
                "msyhl.ttc",
                "msyhbd.ttc",
                "Deng.ttf",
                "simhei.ttf",
                "simsun.ttc",
            };
            const string Name = "Wpf.Ui.servoStudio.CjkFont";
            foreach (var f in candidates)
            {
                string path = Path.Combine(fontsDir, f);
                if (File.Exists(path))
                {
                    ScottPlot.Fonts.AddFontFile(Name, path);
                    return Name;
                }
            }
        }
        catch
        {
            // 忽略；下一行兜底返回默认字体
        }

        return ScottPlot.Fonts.Default;
    }

    private void ApplyCjkFont()
    {
        var p = Plot.Plot;

        // 标题与轴名按"实际文本"探测系统中能渲染该文本的字体家族。
        // 这是 ScottPlot 官方推荐用于解决 CJK 方框的 API（v5.0.21+）。
        SetLabelCjkFont(p.Axes.Title.Label);
        SetLabelCjkFont(p.Axes.Bottom.Label);
        SetLabelCjkFont(p.Axes.Left.Label);
        SetLabelCjkFont(p.Axes.Right.Label);
        SetLabelCjkFont(p.Axes.Top.Label);

        // 刻度文本目前都是数字，但保险起见也用 CJK 字体（避免负号/科学计数符号在某些主题缺字形）。
        p.Axes.Bottom.TickLabelStyle.FontName = CjkFont;
        p.Axes.Left.TickLabelStyle.FontName = CjkFont;
        p.Axes.Right.TickLabelStyle.FontName = CjkFont;
        p.Axes.Top.TickLabelStyle.FontName = CjkFont;

        // 图例继续走自定义注册的字体（已验证可显示中文），字号可动态增减。
        p.Legend.FontName = CjkFont;
        p.Legend.FontSize = _legendFontSize;
    }

    /// <summary>ScottPlot 5 默认 13，初始赋值 16（需求 +3）；可通过按钮动态增减。</summary>
    private float _legendFontSize = 16f;
    private const float LegendFontSizeMin = 8f;
    private const float LegendFontSizeMax = 36f;
    private const float LegendFontStep = 2f;

    /// <summary>当前是否为深色主题。</summary>
    private bool _isDarkTheme;

    /// <summary>
    /// 用 <see cref="ScottPlot.Fonts.Detect"/> 为给定 <see cref="ScottPlot.LabelStyle"/>
    /// 选择一个能渲染其当前 Text 的系统字体家族名。空文本时回退到 CjkFont。
    /// </summary>
    private static void SetLabelCjkFont(ScottPlot.LabelStyle label)
    {
        try
        {
            string text = label.Text ?? string.Empty;
            string detected = string.IsNullOrEmpty(text)
                ? CjkFont
                : ScottPlot.Fonts.Detect(text);
            label.FontName = string.IsNullOrEmpty(detected) ? CjkFont : detected;
        }
        catch
        {
            label.FontName = CjkFont;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 导航到本页时先同步一次当前 App 主题：
        // 若用户在本页不可见期间切换了 App 主题，Unloaded 已取消订阅导致事件未收到，
        // 在此直接读取最新主题值确保波形窗与 App 主题一致。
        var appTheme = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme();
        if (appTheme != Wpf.Ui.Appearance.ApplicationTheme.Unknown)
        {
            bool wantDark = appTheme == Wpf.Ui.Appearance.ApplicationTheme.Dark;
            if (wantDark != _isDarkTheme)
            {
                _isDarkTheme = wantDark;
                // 延迟保存：只在实际发生偏差时才写盘，避免在 App 主题 == 用户上次手动选择时重复写盘
                SavePlotSettings();
            }
        }

        SubscribeChannels();
        RebuildPlot();   // 内部已调用 ApplyTheme() + Plot.Refresh()
        // 订阅后续 App 主题变化
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += OnAppThemeChanged;

        // 鼠标拖动平移 / 滚轮缩放 / 双击复位 后把最新轴范围同步回 NumberBox。
        // ScottPlot.WPF.WpfPlot 直接暴露标准 WPF 输入事件；交互完成后再读取范围。
        Plot.MouseUp += OnPlotInteractionEnded;
        Plot.MouseWheel += OnPlotInteractionEnded;
        Plot.MouseDoubleClick += OnPlotInteractionEnded;

        // 如果导航返回时在线接收仍开启则恢复刷新帧同步回调
        if (ViewModel.IsLiveReceiveEnabled)
        {
            _liveDirty = true;
            HookLiveRendering();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= OnAppThemeChanged;
        Plot.MouseUp -= OnPlotInteractionEnded;
        Plot.MouseWheel -= OnPlotInteractionEnded;
        Plot.MouseDoubleClick -= OnPlotInteractionEnded;
        UnhookLiveRendering();
        UnsubscribeChannels();
    }

    /// <summary>
    /// 程序主题切换时自动同步波形窗主题（用户仍可通过"深/浅"按钮手动覆盖）。
    /// </summary>
    private void OnAppThemeChanged(Wpf.Ui.Appearance.ApplicationTheme theme, System.Windows.Media.Color _)
    {
        _isDarkTheme = theme == Wpf.Ui.Appearance.ApplicationTheme.Dark;
        SavePlotSettings();
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyTheme();
            Plot.Refresh();
        }
        else
        {
            dispatcher.InvokeAsync(() => { ApplyTheme(); Plot.Refresh(); });
        }
    }

    private void OnChannelsReplaced()
    {
        _liveSignals = null;  // 通道集合整体替换，强制下次 live tick 全量重建
        _liveBoundBuffers = null;
        UnsubscribeChannels();
        SubscribeChannels();
        RebuildPlot();
    }

    private void SubscribeChannels()
    {
        foreach (var ch in ViewModel.Channels)
        {
            if (_subscribedChannels.Add(ch))
            {
                ch.PropertyChanged += OnChannelPropertyChanged;
            }
        }
    }

    private void UnsubscribeChannels()
    {
        foreach (var ch in _subscribedChannels)
        {
            ch.PropertyChanged -= OnChannelPropertyChanged;
        }

        _subscribedChannels.Clear();
    }

    private void OnChannelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DataChannel.IsVisible))
        {
            RebuildPlot();
        }
    }

    /// <summary>
    /// 用当前 ViewModel 的可见通道重建波形图（全量路径，含字体/主题/AutoScale）。<br/>
    /// 在线接收期间的帧刷新应使用 <see cref="UpdateLivePlot"/>，而非此方法。
    /// </summary>
    private void RebuildPlot()
    {
        _liveSignals = null;  // 重建后缓存失效，下次 live tick 会重新填充
        _liveBoundBuffers = null;
        Plot.Plot.Clear();

        bool any = false;

        // 通过 SnapshotChannels() 在 _liveLock 保护下一次性取得各通道数据副本，
        // 避免后台在线接收线程并发修改 List<double> 时产生数据竞争（会引发偶发零值）。
        var snapshots = ViewModel.SnapshotChannels();

        foreach (var (ch, ys) in snapshots)
        {
            if (!ch.IsVisible || ys.Length == 0)
            {
                continue;
            }

            // X = 0..N-1（按存储顺序），Y = 数据值
            var sig = Plot.Plot.Add.Signal(ys);
            sig.LegendText = $"{ch.ChannelLabel}  {ch.DisplayLabel}";
            sig.Color = ParseColor(ch.ColorHex);
            sig.LineWidth = 1.5f;
            any = true;
        }

        if (any)
        {
            Plot.Plot.Axes.AutoScale();
        }

        // 重建后重新套用中文字体，避免 ScottPlot 内部某些样式被默认值覆盖。
        ApplyCjkFont();
        ApplyTheme();
        Plot.Refresh();
        UpdateXRangeBoxesFromPlot();
    }

    // ===== 主题切换（深色/浅色背景） =====
    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        _isDarkTheme = !_isDarkTheme;
        SavePlotSettings();
        ApplyTheme();
        Plot.Refresh();
    }

    /// <summary>
    /// 把当前主题（深/浅）应用到 ScottPlot 5：图形/数据区背景、坐标轴线、刻度文本、网格、图例。
    /// </summary>
    private void ApplyTheme()
    {
        var p = Plot.Plot;
        if (_isDarkTheme)
        {
            // 深色：经典示波器配色
            var fig = ScottPlot.Color.FromHex("#1E1E1E");
            var data = ScottPlot.Color.FromHex("#0A0A0A");
            var fg = ScottPlot.Color.FromHex("#E6E6E6");
            var grid = ScottPlot.Color.FromHex("#3A3A3A");
            p.FigureBackground.Color = fig;
            p.DataBackground.Color = data;
            ApplyAxisColors(p, fg, grid);
            p.Legend.BackgroundColor = ScottPlot.Color.FromHex("#2A2A2A");
            p.Legend.FontColor = fg;
            p.Legend.OutlineColor = fg;
        }
        else
        {
            var fig = ScottPlot.Color.FromHex("#FFFFFF");
            var data = ScottPlot.Color.FromHex("#FFFFFF");
            var fg = ScottPlot.Color.FromHex("#000000");
            var grid = ScottPlot.Color.FromHex("#D0D0D0");
            p.FigureBackground.Color = fig;
            p.DataBackground.Color = data;
            ApplyAxisColors(p, fg, grid);
            p.Legend.BackgroundColor = ScottPlot.Color.FromHex("#FFFFFF");
            p.Legend.FontColor = fg;
            p.Legend.OutlineColor = fg;
        }
    }

    private static void ApplyAxisColors(ScottPlot.Plot p, ScottPlot.Color fg, ScottPlot.Color grid)
    {
        foreach (var axis in p.Axes.GetAxes())
        {
            axis.Label.ForeColor = fg;
            axis.TickLabelStyle.ForeColor = fg;
            axis.MajorTickStyle.Color = fg;
            axis.MinorTickStyle.Color = fg;
            axis.FrameLineStyle.Color = fg;
        }
        p.Axes.Title.Label.ForeColor = fg;
        // ScottPlot 5: DefaultGrid 暴露 XAxisStyle / YAxisStyle，分别含主/次网格线样式。
        p.Grid.XAxisStyle.MajorLineStyle.Color = grid;
        p.Grid.YAxisStyle.MajorLineStyle.Color = grid;
        p.Grid.XAxisStyle.MinorLineStyle.Color = grid.WithAlpha(0.4);
        p.Grid.YAxisStyle.MinorLineStyle.Color = grid.WithAlpha(0.4);
    }

    // ===== 图例字号 +/- =====
    private void OnLegendFontInc(object sender, RoutedEventArgs e)
    {
        SetLegendFontSize(_legendFontSize + LegendFontStep);
    }

    private void OnLegendFontDec(object sender, RoutedEventArgs e)
    {
        SetLegendFontSize(_legendFontSize - LegendFontStep);
    }

    private void SetLegendFontSize(float size)
    {
        size = Math.Max(LegendFontSizeMin, Math.Min(LegendFontSizeMax, size));
        if (Math.Abs(size - _legendFontSize) < 0.01f)
        {
            return;
        }

        _legendFontSize = size;
        SavePlotSettings();
        Plot.Plot.Legend.FontSize = _legendFontSize;
        Plot.Refresh();
    }

    /// <summary>把当前波形窗深/浅主题和图例字号持久化到用户设置。</summary>
    private void SavePlotSettings()
    {
        try
        {
            var s = UserSettingsService.Load();
            s.DataView_IsDarkTheme = _isDarkTheme;
            s.DataView_LegendFontSize = _legendFontSize;
            UserSettingsService.Save(s);
        }
        catch
        {
            // 持久化失败不影响运行
        }
    }

    // ===== 横轴范围 / 自动缩放 =====

    /// <summary>当 NumberBox.Value 由 <see cref="UpdateXRangeBoxesFromPlot"/> 程序赋值时
    /// 抑制 <see cref="OnXRangeValueChanged"/> 反向回写 plot，避免循环触发。</summary>
    private bool _suppressXRangeValueChanged;

    private void OnXRangeKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            ApplyXRangeFromBoxes();
            e.Handled = true;
        }
    }

    private void OnApplyXRange(object sender, RoutedEventArgs e) => ApplyXRangeFromBoxes();

    /// <summary>NumberBox 的 ValueChanged：用户点击右侧 ▲▼ 微调按钮、或拼写后失焦时触发 → 立即应用到 X 轴。</summary>
    private void OnXRangeValueChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressXRangeValueChanged)
        {
            return;
        }

        ApplyXRangeFromBoxes();
    }

    private void ApplyXRangeFromBoxes()
    {
        double? lo = XMinBox.Value;
        double? hi = XMaxBox.Value;
        if (lo is null || hi is null || hi <= lo)
        {
            UpdateXRangeBoxesFromPlot();
            return;
        }

        Plot.Plot.Axes.SetLimitsX(lo.Value, hi.Value);
        Plot.Refresh();
    }

    private void OnAutoX(object sender, RoutedEventArgs e)
    {
        Plot.Plot.Axes.AutoScaleX();
        Plot.Refresh();
        UpdateXRangeBoxesFromPlot();
    }

    private void OnAutoY(object sender, RoutedEventArgs e)
    {
        Plot.Plot.Axes.AutoScaleY();
        Plot.Refresh();
    }

    private void OnAutoAll(object sender, RoutedEventArgs e)
    {
        Plot.Plot.Axes.AutoScale();
        Plot.Refresh();
        UpdateXRangeBoxesFromPlot();
    }

    /// <summary>
    /// 鼠标拖动 / 滚轮缩放后 ScottPlot 已重绘，此处把最新轴范围回填到 NumberBox。
    /// 订阅在 OnLoaded() 中完成。MouseUp / MouseWheel / MouseDoubleClick 三种委托
    /// 的 EventArgs 都派生自 RoutedEventArgs，用基类签名即可统一接管。
    /// </summary>
    private void OnPlotInteractionEnded(object sender, RoutedEventArgs e)
    {
        UpdateXRangeBoxesFromPlot();
    }

    /// <summary>把当前 Plot 的 X 范围回填到两个 NumberBox（用于自动缩放或重建后展示）。
    /// 取整后回写以避免 NumberBox 在 MaxDecimalPlaces=0 下做尾数显示截断。</summary>
    private void UpdateXRangeBoxesFromPlot()
    {
        try
        {
            var lim = Plot.Plot.Axes.GetLimits();
            double lo = Math.Floor(lim.Left);
            double hi = Math.Ceiling(lim.Right);
            _suppressXRangeValueChanged = true;
            XMinBox.Value = lo;
            XMaxBox.Value = hi;
        }
        catch
        {
            // ignore
        }
        finally
        {
            _suppressXRangeValueChanged = false;
        }
    }

    private static ScottPlot.Color ParseColor(string hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return ScottPlot.Colors.Yellow;
        }

        try
        {
            string s = hex.TrimStart('#');
            if (s.Length == 6)
            {
                byte r = Convert.ToByte(s.Substring(0, 2), 16);
                byte g = Convert.ToByte(s.Substring(2, 2), 16);
                byte b = Convert.ToByte(s.Substring(4, 2), 16);
                return new ScottPlot.Color(r, g, b);
            }
        }
        catch
        {
            // fall through
        }

        return ScottPlot.Colors.Yellow;
    }
}
