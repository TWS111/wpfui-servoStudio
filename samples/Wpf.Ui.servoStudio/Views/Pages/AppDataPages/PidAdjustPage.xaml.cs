// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.IO;
using ScottPlot;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.servoStudio.Services;
using Wpf.Ui.servoStudio.ViewModels.AppData;

namespace Wpf.Ui.servoStudio.Views.Pages.AppDataPages;

public partial class PidAdjustPage : INavigableView<PidAdjustViewModel>
{
    // ===== CJK 字体（与 DataViewPage 相同逻辑）=====
    private static readonly string CjkFont = RegisterCjkFont();

    private static string RegisterCjkFont()
    {
        try
        {
            string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            string[] candidates = { "msyh.ttc", "msyhl.ttc", "msyhbd.ttc", "Deng.ttf", "simhei.ttf", "simsun.ttc" };
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
        catch { }
        return ScottPlot.Fonts.Default;
    }

    // ===== 波形外观状态 =====
    private bool _isDarkTheme;
    private float _legendFontSize = 16f;
    private const float LegendFontSizeMin = 8f;
    private const float LegendFontSizeMax = 36f;
    private const float LegendFontStep = 2f;

    public PidAdjustViewModel ViewModel { get; }

    public PidAdjustPage(PidAdjustViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();

        ScottPlot.Fonts.Default = CjkFont;

        var s = UserSettingsService.Load();
        _isDarkTheme = s.PidAdjust_IsDarkTheme;
        _legendFontSize = Math.Clamp(
            s.PidAdjust_LegendFontSize > 0 ? s.PidAdjust_LegendFontSize : 16f,
            LegendFontSizeMin, LegendFontSizeMax);

        WavePlot.Plot.Title("PID 调节波形");
        WavePlot.Plot.XLabel("样本序号");
        WavePlot.Plot.YLabel("值");
        ApplyWaveCjkFont();
        WavePlot.Plot.ShowLegend();
        ApplyWaveTheme();
        WavePlot.Refresh();

        ViewModel.WaveformUpdated += OnWaveformUpdated;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ===== 生命周期 =====
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var appTheme = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme();
        if (appTheme != Wpf.Ui.Appearance.ApplicationTheme.Unknown)
        {
            bool wantDark = appTheme == Wpf.Ui.Appearance.ApplicationTheme.Dark;
            if (wantDark != _isDarkTheme)
            {
                _isDarkTheme = wantDark;
                SaveWaveSettings();
            }
        }

        RebuildWavePlot();
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += OnAppThemeChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= OnAppThemeChanged;
    }

    private void OnAppThemeChanged(Wpf.Ui.Appearance.ApplicationTheme theme, System.Windows.Media.Color _)
    {
        _isDarkTheme = theme == Wpf.Ui.Appearance.ApplicationTheme.Dark;
        SaveWaveSettings();
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        { ApplyWaveTheme(); WavePlot.Refresh(); }
        else
            dispatcher.InvokeAsync(() => { ApplyWaveTheme(); WavePlot.Refresh(); });
    }

    // ===== 波形重建 =====
    private void OnWaveformUpdated() => RebuildWavePlot();

    private void RebuildWavePlot()
    {
        WavePlot.Plot.Clear();

        bool any = false;
        foreach (var ch in GetChannels())
        {
            if (!ch.IsVisible || ch.Count == 0) continue;
            double[] ys = ch.ToArray();
            var sig = WavePlot.Plot.Add.Signal(ys);
            sig.LegendText = ch.Name;
            sig.Color = ParseColor(ch.ColorHex);
            sig.LineWidth = 1.5f;
            any = true;
        }

        if (any) WavePlot.Plot.Axes.AutoScale();
        ApplyWaveCjkFont();
        ApplyWaveTheme();
        WavePlot.Refresh();
    }

    private IEnumerable<PidWaveChannel> GetChannels()
    {
        yield return ViewModel.ChTargetPos;
        yield return ViewModel.ChActualPos;
        yield return ViewModel.ChTargetVel;
        yield return ViewModel.ChActualVel;
        yield return ViewModel.ChTargetTrq;
        yield return ViewModel.ChActualTrq;
    }

    // ===== 通道选择复选框 =====
    private void OnChannelChecked(object sender, RoutedEventArgs e)
    {
        // 防止 XAML 初始化阶段控件尚未完全加载时触发
        if (ChkTargetPos == null || ChkActualPos == null ||
            ChkTargetVel == null || ChkActualVel == null ||
            ChkTargetTrq == null || ChkActualTrq == null)
            return;

        ViewModel.ChTargetPos.IsVisible = ChkTargetPos.IsChecked == true;
        ViewModel.ChActualPos.IsVisible = ChkActualPos.IsChecked == true;
        ViewModel.ChTargetVel.IsVisible = ChkTargetVel.IsChecked == true;
        ViewModel.ChActualVel.IsVisible = ChkActualVel.IsChecked == true;
        ViewModel.ChTargetTrq.IsVisible = ChkTargetTrq.IsChecked == true;
        ViewModel.ChActualTrq.IsVisible = ChkActualTrq.IsChecked == true;

        // 清除已关闭通道的缓冲，避免下次重新打开时显示旧数据
        if (!ViewModel.ChTargetPos.IsVisible) ViewModel.ChTargetPos.Clear();
        if (!ViewModel.ChActualPos.IsVisible) ViewModel.ChActualPos.Clear();
        if (!ViewModel.ChTargetVel.IsVisible) ViewModel.ChTargetVel.Clear();
        if (!ViewModel.ChActualVel.IsVisible) ViewModel.ChActualVel.Clear();
        if (!ViewModel.ChTargetTrq.IsVisible) ViewModel.ChTargetTrq.Clear();
        if (!ViewModel.ChActualTrq.IsVisible) ViewModel.ChActualTrq.Clear();

        RebuildWavePlot();
    }

    // ===== 工具栏事件 =====
    private void OnToggleWaveTheme(object sender, RoutedEventArgs e)
    {
        _isDarkTheme = !_isDarkTheme;
        SaveWaveSettings();
        ApplyWaveTheme();
        WavePlot.Refresh();
    }

    private void OnWaveLegendFontInc(object sender, RoutedEventArgs e)
        => SetWaveLegendFontSize(_legendFontSize + LegendFontStep);

    private void OnWaveLegendFontDec(object sender, RoutedEventArgs e)
        => SetWaveLegendFontSize(_legendFontSize - LegendFontStep);

    private void SetWaveLegendFontSize(float size)
    {
        size = Math.Max(LegendFontSizeMin, Math.Min(LegendFontSizeMax, size));
        if (Math.Abs(size - _legendFontSize) < 0.01f) return;
        _legendFontSize = size;
        SaveWaveSettings();
        WavePlot.Plot.Legend.FontSize = _legendFontSize;
        WavePlot.Refresh();
    }

    private void OnWaveAutoAll(object sender, RoutedEventArgs e)
    {
        WavePlot.Plot.Axes.AutoScale();
        WavePlot.Refresh();
    }

    private void OnWaveClear(object sender, RoutedEventArgs e)
        => ViewModel.ClearWaveformCommand.Execute(null);

    // ===== 持久化 =====
    private void SaveWaveSettings()
    {
        try
        {
            var s = UserSettingsService.Load();
            s.PidAdjust_IsDarkTheme = _isDarkTheme;
            s.PidAdjust_LegendFontSize = _legendFontSize;
            UserSettingsService.Save(s);
        }
        catch { }
    }

    // ===== 主题应用（与 DataViewPage 完全一致）=====
    private void ApplyWaveTheme()
    {
        var p = WavePlot.Plot;
        if (_isDarkTheme)
        {
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
        p.Grid.XAxisStyle.MajorLineStyle.Color = grid;
        p.Grid.YAxisStyle.MajorLineStyle.Color = grid;
        p.Grid.XAxisStyle.MinorLineStyle.Color = grid.WithAlpha(0.4);
        p.Grid.YAxisStyle.MinorLineStyle.Color = grid.WithAlpha(0.4);
    }

    // ===== CJK 字体应用（与 DataViewPage 完全一致）=====
    private void ApplyWaveCjkFont()
    {
        var p = WavePlot.Plot;
        SetLabelCjkFont(p.Axes.Title.Label);
        SetLabelCjkFont(p.Axes.Bottom.Label);
        SetLabelCjkFont(p.Axes.Left.Label);
        SetLabelCjkFont(p.Axes.Right.Label);
        SetLabelCjkFont(p.Axes.Top.Label);
        p.Axes.Bottom.TickLabelStyle.FontName = CjkFont;
        p.Axes.Left.TickLabelStyle.FontName = CjkFont;
        p.Axes.Right.TickLabelStyle.FontName = CjkFont;
        p.Axes.Top.TickLabelStyle.FontName = CjkFont;
        p.Legend.FontName = CjkFont;
        p.Legend.FontSize = _legendFontSize;
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

    // ===== 颜色解析 =====
    private static ScottPlot.Color ParseColor(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return ScottPlot.Colors.Yellow;
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
        catch { }
        return ScottPlot.Colors.Yellow;
    }
}
