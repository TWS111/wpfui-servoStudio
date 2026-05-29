// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using ScottPlot;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.servoStudio.Services;
using Wpf.Ui.servoStudio.ViewModels;

namespace Wpf.Ui.servoStudio.Views.Pages;

public partial class QuickControlPage : INavigableView<ViewModels.QuickControlViewModel>
{
    private static readonly string CjkFont = RegisterCjkFont();

    private readonly Dictionary<QuickLiveChannel, ScottPlot.Plottables.Signal> _liveSignals = new();
    private readonly Dictionary<QuickLiveChannel, double[]> _liveBoundBuffers = new();

    private float _legendFontSize = 16f;
    private const float LegendFontSizeMin = 8f;
    private const float LegendFontSizeMax = 36f;
    private const float LegendFontStep = 2f;

    private bool _isDarkTheme;
    private bool _isLivePaused;
    private bool _suppressXRangeValueChanged;

    public ViewModels.QuickControlViewModel ViewModel
    {
        get;
    }

    public QuickControlPage(ViewModels.QuickControlViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();

        ScottPlot.Fonts.Default = CjkFont;

        UserSettings settings = UserSettingsService.Load();
        _isDarkTheme = settings.DataView_IsDarkTheme;
        _legendFontSize = Math.Clamp(
            settings.DataView_LegendFontSize > 0 ? settings.DataView_LegendFontSize : 16f,
            LegendFontSizeMin,
            LegendFontSizeMax);

        ConfigurePlot();
        ViewModel.ChannelsChanged += OnChannelsChanged;
        ViewModel.PlotTick += OnPlotTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void ConfigurePlot()
    {
        Plot.Plot.Axes.Title.Label.Text = "在线快速波形";
        Plot.Plot.Axes.Bottom.Label.Text = "样本序号";
        Plot.Plot.Axes.Left.Label.Text = "值";
        Plot.Plot.ShowLegend();
        ApplyCjkFont();
        ApplyTheme();
        Plot.Refresh();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Wpf.Ui.Appearance.ApplicationTheme appTheme = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme();
        if (appTheme != Wpf.Ui.Appearance.ApplicationTheme.Unknown)
        {
            bool wantDark = appTheme == Wpf.Ui.Appearance.ApplicationTheme.Dark;
            if (wantDark != _isDarkTheme)
            {
                _isDarkTheme = wantDark;
                SavePlotSettings();
            }
        }

        RebuildSignals();
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += OnAppThemeChanged;
        Plot.MouseUp += OnPlotInteractionEnded;
        Plot.MouseWheel += OnPlotInteractionEnded;
        Plot.MouseDoubleClick += OnPlotInteractionEnded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= OnAppThemeChanged;
        Plot.MouseUp -= OnPlotInteractionEnded;
        Plot.MouseWheel -= OnPlotInteractionEnded;
        Plot.MouseDoubleClick -= OnPlotInteractionEnded;
    }

    private void OnAppThemeChanged(Wpf.Ui.Appearance.ApplicationTheme theme, System.Windows.Media.Color accentColor)
    {
        _isDarkTheme = theme == Wpf.Ui.Appearance.ApplicationTheme.Dark;
        SavePlotSettings();
        if (Dispatcher.CheckAccess())
        {
            ApplyTheme();
            Plot.Refresh();
        }
        else
        {
            Dispatcher.InvokeAsync(() =>
            {
                ApplyTheme();
                Plot.Refresh();
            });
        }
    }

    private void OnChannelsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(RebuildSignals);
            return;
        }

        RebuildSignals();
    }

    private void OnPlotTick(object? sender, EventArgs e)
    {
        if (_isLivePaused)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(RefreshSignals);
            return;
        }

        RefreshSignals();
    }

    private void RebuildSignals()
    {
        _liveSignals.Clear();
        _liveBoundBuffers.Clear();
        Plot.Plot.Clear();

        bool any = false;
        foreach (QuickLiveChannel channel in ViewModel.ActiveChannels)
        {
            if (!channel.IsVisible || channel.Buffer.Length == 0)
            {
                continue;
            }

            var source = new ScottPlot.DataSources.SignalSourceDouble(channel.Buffer, 1.0)
            {
                MaximumIndex = Math.Max(0, channel.GetValidCount() - 1),
            };

            var signal = Plot.Plot.Add.Signal(source);
            signal.LegendText = $"{channel.ChannelLabel}  {channel.DisplayLabel}";
            signal.Color = ParseColor(channel.ColorHex);
            signal.LineWidth = 1.5f;
            _liveSignals[channel] = signal;
            _liveBoundBuffers[channel] = channel.Buffer;
            any = true;
        }

        Plot.Plot.Axes.Title.Label.Text = "在线快速波形";
        Plot.Plot.Axes.Bottom.Label.Text = "样本序号";
        Plot.Plot.Axes.Left.Label.Text = "值";
        Plot.Plot.ShowLegend();

        if (any)
        {
            Plot.Plot.Axes.AutoScale();
        }

        ApplyCjkFont();
        ApplyTheme();
        Plot.Refresh();
        UpdateXRangeBoxesFromPlot();
    }

    private void RefreshSignals()
    {
        if (_liveSignals.Count == 0)
        {
            return;
        }

        bool needRebuild = false;
        foreach (QuickLiveChannel channel in ViewModel.ActiveChannels)
        {
            if (!channel.IsVisible)
            {
                continue;
            }

            if (!_liveSignals.ContainsKey(channel)
                || !_liveBoundBuffers.TryGetValue(channel, out double[]? buffer)
                || !ReferenceEquals(buffer, channel.Buffer))
            {
                needRebuild = true;
                break;
            }
        }

        if (needRebuild)
        {
            RebuildSignals();
            return;
        }

        foreach (KeyValuePair<QuickLiveChannel, ScottPlot.Plottables.Signal> pair in _liveSignals)
        {
            if (pair.Value.Data is ScottPlot.DataSources.SignalSourceDouble source)
            {
                source.MaximumIndex = Math.Max(0, pair.Key.GetValidCount() - 1);
            }
        }

        Plot.Plot.Axes.AutoScaleY();
        Plot.Refresh();
    }

    private static string RegisterCjkFont()
    {
        try
        {
            string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
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
            foreach (string candidate in candidates)
            {
                string path = Path.Combine(fontsDir, candidate);
                if (File.Exists(path))
                {
                    ScottPlot.Fonts.AddFontFile(Name, path);
                    return Name;
                }
            }
        }
        catch
        {
            // 回退到 ScottPlot 默认字体。
        }

        return ScottPlot.Fonts.Default;
    }

    private void ApplyCjkFont()
    {
        var plot = Plot.Plot;
        SetLabelCjkFont(plot.Axes.Title.Label);
        SetLabelCjkFont(plot.Axes.Bottom.Label);
        SetLabelCjkFont(plot.Axes.Left.Label);
        SetLabelCjkFont(plot.Axes.Right.Label);
        SetLabelCjkFont(plot.Axes.Top.Label);
        plot.Axes.Bottom.TickLabelStyle.FontName = CjkFont;
        plot.Axes.Left.TickLabelStyle.FontName = CjkFont;
        plot.Axes.Right.TickLabelStyle.FontName = CjkFont;
        plot.Axes.Top.TickLabelStyle.FontName = CjkFont;
        plot.Legend.FontName = CjkFont;
        plot.Legend.FontSize = _legendFontSize;
    }

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

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        _isDarkTheme = !_isDarkTheme;
        SavePlotSettings();
        ApplyTheme();
        Plot.Refresh();
    }

    private void OnParameterEditorGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is QuickMotionParameter parameter)
        {
            parameter.IsLocked = true;
        }
    }

    private void OnParameterEditorLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is QuickMotionParameter parameter)
        {
            parameter.IsLocked = false;
        }
    }

    private void ApplyTheme()
    {
        var plot = Plot.Plot;
        if (_isDarkTheme)
        {
            var figure = ScottPlot.Color.FromHex("#1E1E1E");
            var data = ScottPlot.Color.FromHex("#0A0A0A");
            var foreground = ScottPlot.Color.FromHex("#E6E6E6");
            var grid = ScottPlot.Color.FromHex("#3A3A3A");
            plot.FigureBackground.Color = figure;
            plot.DataBackground.Color = data;
            ApplyAxisColors(plot, foreground, grid);
            plot.Legend.BackgroundColor = ScottPlot.Color.FromHex("#2A2A2A");
            plot.Legend.FontColor = foreground;
            plot.Legend.OutlineColor = foreground;
        }
        else
        {
            var figure = ScottPlot.Color.FromHex("#FFFFFF");
            var data = ScottPlot.Color.FromHex("#FFFFFF");
            var foreground = ScottPlot.Color.FromHex("#000000");
            var grid = ScottPlot.Color.FromHex("#D0D0D0");
            plot.FigureBackground.Color = figure;
            plot.DataBackground.Color = data;
            ApplyAxisColors(plot, foreground, grid);
            plot.Legend.BackgroundColor = ScottPlot.Color.FromHex("#FFFFFF");
            plot.Legend.FontColor = foreground;
            plot.Legend.OutlineColor = foreground;
        }
    }

    private static void ApplyAxisColors(ScottPlot.Plot plot, ScottPlot.Color foreground, ScottPlot.Color grid)
    {
        foreach (var axis in plot.Axes.GetAxes())
        {
            axis.Label.ForeColor = foreground;
            axis.TickLabelStyle.ForeColor = foreground;
            axis.MajorTickStyle.Color = foreground;
            axis.MinorTickStyle.Color = foreground;
            axis.FrameLineStyle.Color = foreground;
        }

        plot.Axes.Title.Label.ForeColor = foreground;
        plot.Grid.XAxisStyle.MajorLineStyle.Color = grid;
        plot.Grid.YAxisStyle.MajorLineStyle.Color = grid;
        plot.Grid.XAxisStyle.MinorLineStyle.Color = grid.WithAlpha(0.4);
        plot.Grid.YAxisStyle.MinorLineStyle.Color = grid.WithAlpha(0.4);
    }

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
        size = Math.Clamp(size, LegendFontSizeMin, LegendFontSizeMax);
        if (Math.Abs(size - _legendFontSize) < 0.01f)
        {
            return;
        }

        _legendFontSize = size;
        SavePlotSettings();
        Plot.Plot.Legend.FontSize = _legendFontSize;
        Plot.Refresh();
    }

    private void SavePlotSettings()
    {
        try
        {
            UserSettings settings = UserSettingsService.Load();
            settings.DataView_IsDarkTheme = _isDarkTheme;
            settings.DataView_LegendFontSize = _legendFontSize;
            UserSettingsService.Save(settings);
        }
        catch
        {
            // 持久化失败不影响运行。
        }
    }

    private void OnPauseLiveRefresh(object sender, RoutedEventArgs e)
    {
        _isLivePaused = true;
        LivePauseButton.Visibility = Visibility.Collapsed;
        LiveResumeButton.Visibility = Visibility.Visible;
    }

    private void OnResumeLiveRefresh(object sender, RoutedEventArgs e)
    {
        _isLivePaused = false;
        LiveResumeButton.Visibility = Visibility.Collapsed;
        LivePauseButton.Visibility = Visibility.Visible;
        RefreshSignals();
    }

    private void OnXRangeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyXRangeFromBoxes();
            e.Handled = true;
        }
    }

    private void OnApplyXRange(object sender, RoutedEventArgs e) => ApplyXRangeFromBoxes();

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

    private void OnPlotInteractionEnded(object sender, RoutedEventArgs e)
    {
        UpdateXRangeBoxesFromPlot();
    }

    private void UpdateXRangeBoxesFromPlot()
    {
        try
        {
            var limits = Plot.Plot.Axes.GetLimits();
            double lo = Math.Floor(limits.Left);
            double hi = Math.Ceiling(limits.Right);
            _suppressXRangeValueChanged = true;
            XMinBox.Value = lo;
            XMaxBox.Value = hi;
        }
        catch
        {
            // 忽略轴范围读取失败。
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
                byte red = Convert.ToByte(s.Substring(0, 2), 16);
                byte green = Convert.ToByte(s.Substring(2, 2), 16);
                byte blue = Convert.ToByte(s.Substring(4, 2), 16);
                return new ScottPlot.Color(red, green, blue);
            }
        }
        catch
        {
            // 使用兜底颜色。
        }

        return ScottPlot.Colors.Yellow;
    }
}