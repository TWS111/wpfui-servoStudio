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
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
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
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= OnAppThemeChanged;
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
    /// 用当前 ViewModel 的可见通道重建波形图。
    /// </summary>
    private void RebuildPlot()
    {
        Plot.Plot.Clear();

        bool any = false;
        foreach (var ch in ViewModel.Channels)
        {
            if (!ch.IsVisible || ch.Values.Count == 0)
            {
                continue;
            }

            // X = 0..N-1（按存储顺序），Y = 数据值
            double[] ys = ch.Values.ToArray();
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
