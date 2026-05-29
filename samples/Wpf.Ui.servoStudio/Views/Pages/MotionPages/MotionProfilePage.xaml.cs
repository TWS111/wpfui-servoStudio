// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.IO;
using ScottPlot;
using ScottPlot.WPF;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.servoStudio.Services;
using Wpf.Ui.servoStudio.ViewModels.Motion;

namespace Wpf.Ui.servoStudio.Views.Pages.MotionPages;

/// <summary>
/// 运动曲线页 — T 型 / S 型速度轮廓参数配置与实时 ScottPlot 可视化。
/// 主题切换逻辑与 DataViewPage 一致：
///   • 默认跟随程序主题（订阅 ApplicationThemeManager.Changed）；
///   • 用户可通过单一 “深/浅” 按钮手动覆盖（持久化到 UserSettingsService.MotionProfile_IsDarkTheme）；
///   • 速度图与加速度图共用一个状态。
/// 同时对图表标题 / 轴名 / 刻度 / 图例文字字号统一 +2 pt。
/// </summary>
public partial class MotionProfilePage : INavigableView<MotionProfileViewModel>
{
    public MotionProfileViewModel ViewModel { get; }

    // ── 字体（同 DataViewPage 方式）────────────────────────────────────────
    private static readonly string CjkFont = RegisterCjkFont();

    private static string RegisterCjkFont()
    {
        try
        {
            string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            string[] candidates = ["msyh.ttc", "msyhl.ttc", "msyhbd.ttc", "Deng.ttf", "simhei.ttf", "simsun.ttc"];
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
        catch { /* 忽略，回退默认 */ }
        return ScottPlot.Fonts.Default;
    }

    // ── 主题状态（两图共用）────────────────────────────────────────────────
    private bool _isDarkTheme;

    // ── 文字字号统一 +2 pt（ScottPlot 5 默认：标题 16 / 轴名 13 / 刻度 12 / 图例 13） ──
    private const float TitleFontSize     = 18f;   // 16 + 2
    private const float AxisLabelFontSize = 15f;   // 13 + 2
    private const float TickLabelFontSize = 14f;   // 12 + 2
    private const float LegendFontSize    = 15f;   // 13 + 2

    // 线条颜色
    private static readonly ScottPlot.Color VelLineColor = ScottPlot.Color.FromHex("#2196F3");
    private static readonly ScottPlot.Color AccLineColor = ScottPlot.Color.FromHex("#FF5722");

    // ── 构造函数 ────────────────────────────────────────────────────────────
    public MotionProfilePage(MotionProfileViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();

        ScottPlot.Fonts.Default = CjkFont;

        // 从磁盘恢复深 / 浅主题（默认 false=浅）
        try { _isDarkTheme = UserSettingsService.Load().MotionProfile_IsDarkTheme; }
        catch { _isDarkTheme = false; }

        InitPlot(VelocityPlot, "速度轮廓",   "时间 (s)", "速度 (u/s)");
        InitPlot(AccelPlot,    "加速度轮廓", "时间 (s)", "加速度 (u/s²)");

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.PreviewPoints))
                Dispatcher.Invoke(RefreshPlots);
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        RefreshPlots();
    }

    // ── 加载 / 卸载：跟随 App 主题 ─────────────────────────────────────────
    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // 与 DataViewPage 一致：导航回本页时同步一次当前 App 主题
        var appTheme = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme();
        if (appTheme != Wpf.Ui.Appearance.ApplicationTheme.Unknown)
        {
            bool wantDark = appTheme == Wpf.Ui.Appearance.ApplicationTheme.Dark;
            if (wantDark != _isDarkTheme)
            {
                _isDarkTheme = wantDark;
                SaveThemeSetting();
                ApplyThemeToAll();
            }
        }
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += OnAppThemeChanged;
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= OnAppThemeChanged;
    }

    private void OnAppThemeChanged(Wpf.Ui.Appearance.ApplicationTheme theme, System.Windows.Media.Color _)
    {
        _isDarkTheme = theme == Wpf.Ui.Appearance.ApplicationTheme.Dark;
        SaveThemeSetting();
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyThemeToAll();
        }
        else
        {
            dispatcher.InvokeAsync(ApplyThemeToAll);
        }
    }

    // ── 主题按钮（单一切换） ────────────────────────────────────────────────
    private void OnToggleTheme(object sender, System.Windows.RoutedEventArgs e)
    {
        _isDarkTheme = !_isDarkTheme;
        SaveThemeSetting();
        ApplyThemeToAll();
    }

    private void ApplyThemeToAll()
    {
        ApplyTheme(VelocityPlot, _isDarkTheme);
        ApplyTheme(AccelPlot,    _isDarkTheme);
        VelocityPlot.Refresh();
        AccelPlot.Refresh();
    }

    private void SaveThemeSetting()
    {
        try
        {
            var s = UserSettingsService.Load();
            s.MotionProfile_IsDarkTheme = _isDarkTheme;
            UserSettingsService.Save(s);
        }
        catch { /* 持久化失败不影响运行 */ }
    }

    // ── ScottPlot 初始化 ────────────────────────────────────────────────────
    private void InitPlot(WpfPlot plot, string title, string xLabel, string yLabel)
    {
        var plt = plot.Plot;
        plt.Title(title);
        plt.XLabel(xLabel);
        plt.YLabel(yLabel);

        ApplyFontSizes(plt);
        ApplyCjkFont(plt);
        ApplyTheme(plot, _isDarkTheme);
        plot.Refresh();
    }

    private static void ApplyFontSizes(ScottPlot.Plot plt)
    {
        plt.Axes.Title.Label.FontSize = TitleFontSize;
        plt.Axes.Bottom.Label.FontSize = AxisLabelFontSize;
        plt.Axes.Left.Label.FontSize   = AxisLabelFontSize;
        plt.Axes.Right.Label.FontSize  = AxisLabelFontSize;
        plt.Axes.Top.Label.FontSize    = AxisLabelFontSize;

        plt.Axes.Bottom.TickLabelStyle.FontSize = TickLabelFontSize;
        plt.Axes.Left.TickLabelStyle.FontSize   = TickLabelFontSize;
        plt.Axes.Right.TickLabelStyle.FontSize  = TickLabelFontSize;
        plt.Axes.Top.TickLabelStyle.FontSize    = TickLabelFontSize;

        plt.Legend.FontSize = LegendFontSize;
    }

    private static void ApplyCjkFont(ScottPlot.Plot plt)
    {
        SetLabelCjkFont(plt.Axes.Title.Label);
        SetLabelCjkFont(plt.Axes.Bottom.Label);
        SetLabelCjkFont(plt.Axes.Left.Label);
        SetLabelCjkFont(plt.Axes.Right.Label);
        SetLabelCjkFont(plt.Axes.Top.Label);
        plt.Axes.Bottom.TickLabelStyle.FontName = CjkFont;
        plt.Axes.Left.TickLabelStyle.FontName   = CjkFont;
        plt.Axes.Right.TickLabelStyle.FontName  = CjkFont;
        plt.Axes.Top.TickLabelStyle.FontName    = CjkFont;
        plt.Legend.FontName = CjkFont;
    }

    private static void SetLabelCjkFont(ScottPlot.LabelStyle label)
    {
        try
        {
            string text = label.Text ?? string.Empty;
            string detected = string.IsNullOrEmpty(text) ? CjkFont : ScottPlot.Fonts.Detect(text);
            label.FontName = string.IsNullOrEmpty(detected) ? CjkFont : detected;
        }
        catch { label.FontName = CjkFont; }
    }

    // ── 主题应用（深 / 浅） ─────────────────────────────────────────────────
    private static void ApplyTheme(WpfPlot plot, bool dark)
    {
        var plt = plot.Plot;
        if (dark)
        {
            plt.FigureBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
            plt.DataBackground.Color   = ScottPlot.Color.FromHex("#0A0A0A");
            ApplyAxisColors(plt,
                fg:   ScottPlot.Color.FromHex("#E6E6E6"),
                grid: ScottPlot.Color.FromHex("#3A3A3A"));
        }
        else
        {
            plt.FigureBackground.Color = ScottPlot.Color.FromHex("#FFFFFF");
            plt.DataBackground.Color   = ScottPlot.Color.FromHex("#FFFFFF");
            ApplyAxisColors(plt,
                fg:   ScottPlot.Color.FromHex("#222222"),
                grid: ScottPlot.Color.FromHex("#D0D0D0"));
        }
    }

    private static void ApplyAxisColors(ScottPlot.Plot plt, ScottPlot.Color fg, ScottPlot.Color grid)
    {
        foreach (var axis in plt.Axes.GetAxes())
        {
            axis.Label.ForeColor          = fg;
            axis.TickLabelStyle.ForeColor = fg;
            axis.MajorTickStyle.Color     = fg;
            axis.MinorTickStyle.Color     = fg;
            axis.FrameLineStyle.Color     = fg;
        }
        plt.Axes.Title.Label.ForeColor = fg;
        plt.Grid.XAxisStyle.MajorLineStyle.Color = grid;
        plt.Grid.YAxisStyle.MajorLineStyle.Color = grid;
        plt.Grid.XAxisStyle.MinorLineStyle.Color = grid.WithAlpha(0.4);
        plt.Grid.YAxisStyle.MinorLineStyle.Color = grid.WithAlpha(0.4);
    }

    // ── 刷新数据 ─────────────────────────────────────────────────────────────
    private void RefreshPlots()
    {
        var pts = ViewModel.PreviewPoints;
        if (pts == null || pts.Count == 0) return;

        double[] time = pts.Select(p => p.Time).ToArray();
        double[] vel  = pts.Select(p => p.Velocity).ToArray();
        double[] acc  = pts.Select(p => p.Acceleration).ToArray();

        RefreshOnePlot(VelocityPlot, VelLineColor, time, vel);
        RefreshOnePlot(AccelPlot,    AccLineColor,  time, acc);
    }

    private void RefreshOnePlot(WpfPlot plot, ScottPlot.Color lineColor, double[]? xs = null, double[]? ys = null)
    {
        var plt = plot.Plot;
        plt.Clear();

        if (xs == null || ys == null)
        {
            var pts = ViewModel.PreviewPoints;
            if (pts == null || pts.Count == 0)
            {
                ApplyTheme(plot, _isDarkTheme);
                plot.Refresh();
                return;
            }
            xs = pts.Select(p => p.Time).ToArray();
            ys = plot == VelocityPlot
                ? pts.Select(p => p.Velocity).ToArray()
                : pts.Select(p => p.Acceleration).ToArray();
        }

        var sig = plt.Add.ScatterLine(xs, ys);
        sig.Color     = lineColor;
        sig.LineWidth = 2;

        var hline = plt.Add.HorizontalLine(0);
        hline.Color       = ScottPlot.Color.FromHex("#888888");
        hline.LineWidth   = 1;
        hline.LinePattern = LinePattern.Dashed;

        ApplyTheme(plot, _isDarkTheme);
        ApplyFontSizes(plt);
        ApplyCjkFont(plt);
        plt.Axes.AutoScale();
        plot.Refresh();
    }
}
