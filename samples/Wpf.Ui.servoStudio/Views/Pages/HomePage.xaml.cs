// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.servoStudio.Services;

namespace Wpf.Ui.servoStudio.Views.Pages;

public partial class HomePage : INavigableView<ViewModels.HomeViewModel>
{
    public ViewModels.HomeViewModel ViewModel { get; }

    public static readonly DependencyProperty HoverTitleProperty = DependencyProperty.Register(
        nameof(HoverTitle), typeof(string), typeof(HomePage), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HoverDescriptionProperty = DependencyProperty.Register(
        nameof(HoverDescription), typeof(string), typeof(HomePage), new PropertyMetadata(string.Empty));

    public string HoverTitle
    {
        get => (string)GetValue(HoverTitleProperty);
        set => SetValue(HoverTitleProperty, value);
    }

    public string HoverDescription
    {
        get => (string)GetValue(HoverDescriptionProperty);
        set => SetValue(HoverDescriptionProperty, value);
    }

    /// <summary>
    /// 去抖定时器：在卡片之间快速切换时，旧卡片的 MouseLeave 不会立即触发淡出；
    /// 若在 <see cref="LeaveDebounceMs"/> 内有新的 MouseEnter 到来则取消淡出。
    /// </summary>
    private readonly DispatcherTimer _fadeOutTimer;
    private const int LeaveDebounceMs = 120;

    public HomePage(ViewModels.HomeViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();

        _fadeOutTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(LeaveDebounceMs),
        };
        _fadeOutTimer.Tick += OnFadeOutTimerTick;

        Loaded += (_, _) => ViewModel.RefreshQuickAccess();
    }

    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not QuickAccessItem item)
        {
            return;
        }

        // 取消任何待执行的淡出，避免新卡片刚显示又被旧卡片的 Leave 淡出
        _fadeOutTimer.Stop();

        HoverTitle = item.Title;
        HoverDescription = item.Description;

        if (DescriptionPanel is null)
        {
            return;
        }

        // 释放上一次 Storyboard.Begin(target, true) 持有的时钟，
        // 然后启动新的淡入动画。
        DescriptionPanel.BeginAnimation(UIElement.OpacityProperty, null);

        if (Resources["DescFadeIn"] is Storyboard sb)
        {
            sb.Begin(DescriptionPanel, true);
        }
        else
        {
            DescriptionPanel.Opacity = 1.0;
        }
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        // 启动去抖定时器；若用户进入相邻卡片，会在 Tick 之前被 MouseEnter 取消。
        _fadeOutTimer.Stop();
        _fadeOutTimer.Start();
    }

    private void OnFadeOutTimerTick(object? sender, EventArgs e)
    {
        _fadeOutTimer.Stop();

        if (DescriptionPanel is null)
        {
            return;
        }

        DescriptionPanel.BeginAnimation(UIElement.OpacityProperty, null);

        if (Resources["DescFadeOut"] is Storyboard sb)
        {
            sb.Begin(DescriptionPanel, true);
        }
        else
        {
            DescriptionPanel.Opacity = 0.0;
        }
    }
}
