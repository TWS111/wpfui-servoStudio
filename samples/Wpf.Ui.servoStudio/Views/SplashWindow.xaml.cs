// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Wpf.Ui.servoStudio.Services;

namespace Wpf.Ui.servoStudio.Views;

/// <summary>
/// Splash screen window shown during application startup.
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{GetAssemblyVersion()}";
        ApplyThemeColors();
    }

    private void ApplyThemeColors()
    {
        UserSettings settings = Services.UserSettingsService.Load();
        bool isDark = settings.ThemeMode switch
        {
            "theme_light" => false,
            "theme_dark" => true,
            // "theme_system" — detect current system theme
            _ => Wpf.Ui.Appearance.ApplicationThemeManager.GetSystemTheme()
                     is Wpf.Ui.Appearance.SystemTheme.Dark
                     or Wpf.Ui.Appearance.SystemTheme.CapturedMotion
                     or Wpf.Ui.Appearance.SystemTheme.Glow
        };

        if (isDark)
        {
            RootBorder.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1A1A2E"));
            TitleText.Foreground = Brush("#E0E0E0");
            SubtitleText.Foreground = Brush("#7A7A9A");
            Divider.Fill = Brush("#2A2A4A");
            StatusText.Foreground = Brush("#9A9AB0");
            LoadProgress.Background = Brush("#2A2A4A");
            VersionText.Foreground = Brush("#5A5A7A");
            CopyrightText.Foreground = Brush("#4A4A6A");
        }
        else
        {
            RootBorder.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F5F5FA"));
            TitleText.Foreground = Brush("#1A1A2E");
            SubtitleText.Foreground = Brush("#6A6A8A");
            Divider.Fill = Brush("#D8D8E8");
            StatusText.Foreground = Brush("#6A6A8A");
            LoadProgress.Background = Brush("#D8D8E8");
            VersionText.Foreground = Brush("#8A8AA0");
            CopyrightText.Foreground = Brush("#9A9AB0");
        }
    }

    private static System.Windows.Media.SolidColorBrush Brush(string hex) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    /// <summary>
    /// 平滑地把进度条推进到 <paramref name="targetPercent"/> 并更新状态文本。
    /// 调用方负责安排各阶段的进度区间。
    /// </summary>
    /// <param name="targetPercent">目标百分比 (0~100)。</param>
    /// <param name="status">状态文本。</param>
    /// <param name="durationMs">动画总时长（毫秒），最低保证每步 1ms。</param>
    public async Task ReportAsync(int targetPercent, string status, int durationMs = 200)
    {
        StatusText.Text = status;

        targetPercent = Math.Clamp(targetPercent, 0, 100);
        int from = (int)Math.Round(LoadProgress.Value);

        if (targetPercent == from)
        {
            // 让 UI 线程有机会刷新文本
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            return;
        }

        int direction = targetPercent > from ? 1 : -1;
        int steps = Math.Abs(targetPercent - from);
        int delay = Math.Max(1, durationMs / steps);

        for (int v = from + direction; direction > 0 ? v <= targetPercent : v >= targetPercent; v += direction)
        {
            LoadProgress.Value = v;
            await Task.Delay(delay);
        }
    }

    private static string GetAssemblyVersion()
    {
        return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "1.0.0";
    }
}