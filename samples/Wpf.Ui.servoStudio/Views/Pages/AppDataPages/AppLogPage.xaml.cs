// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.ViewModels.AppData;

namespace Wpf.Ui.servoStudio.Views.Pages.AppDataPages;

public partial class AppLogPage : INavigableView<AppLogViewModel>
{
    /// <summary>
    /// 当前打开的悬停 Popup（确保同一时刻只有一个）。
    /// </summary>
    private Popup? _activeHoverPopup;

    /// <summary>
    /// 用于在鼠标短暂离开 单元格/Popup 时延迟关闭，给用户从单元格移动到 Popup 内的时间。
    /// </summary>
    private readonly DispatcherTimer _hoverCloseTimer;

    public AppLogViewModel ViewModel { get; }

    public AppLogPage(AppLogViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();

        _hoverCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _hoverCloseTimer.Tick += (_, _) =>
        {
            _hoverCloseTimer.Stop();
            CloseActiveHoverPopup();
        };

        // 离开页面时确保关闭
        Unloaded += (_, _) => CloseActiveHoverPopup();
    }

    // ========== 悬停 Popup 控制 ==========

    private void HoverCell_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe)
            return;

        // 取消可能正在等待的关闭
        _hoverCloseTimer.Stop();

        var text = fe.Tag as string ?? string.Empty;
        if (string.IsNullOrEmpty(text))
            return;

        Popup? popup = FindHoverPopup(fe);
        if (popup is null)
            return;

        if (!ReferenceEquals(_activeHoverPopup, popup))
        {
            CloseActiveHoverPopup();
            _activeHoverPopup = popup;
        }

        if (!popup.IsOpen)
            popup.IsOpen = true;
    }

    private void HoverCell_MouseLeave(object sender, MouseEventArgs e)
    {
        // 留点时间让鼠标移动到 Popup 内容上
        _hoverCloseTimer.Stop();
        _hoverCloseTimer.Start();
    }

    private void HoverPopupContent_MouseEnter(object sender, MouseEventArgs e)
    {
        _hoverCloseTimer.Stop();
    }

    private void HoverPopupContent_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverCloseTimer.Stop();
        _hoverCloseTimer.Start();
    }

    private void CloseActiveHoverPopup()
    {
        if (_activeHoverPopup is { IsOpen: true } p)
            p.IsOpen = false;
        _activeHoverPopup = null;
    }

    private static Popup? FindHoverPopup(DependencyObject root)
    {
        // 视觉树查找
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is Popup vp && vp.Name == "HoverPopup")
                return vp;

            Popup? nested = FindHoverPopup(child);
            if (nested is not null)
                return nested;
        }

        // Popup 通常不在视觉树中，再查逻辑树
        if (root is FrameworkElement fe)
        {
            foreach (var logical in System.Windows.LogicalTreeHelper.GetChildren(fe))
            {
                if (logical is Popup lp && lp.Name == "HoverPopup")
                    return lp;
            }
        }

        return null;
    }

    // ========== 复制 ==========

    private void CopyHoverContent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe)
            return;

        var text = fe.Tag as string ?? string.Empty;
        if (string.IsNullOrEmpty(text))
            return;

        try
        {
            Clipboard.SetText(text);
            ViewModel.StatusText = $"已复制 {text.Length} 个字符到剪贴板";
            AppLogViewModel.LogUserAction("复制日志内容", $"长度: {text.Length}");
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"复制失败: {ex.Message}";
            AppLogViewModel.Log(AppLogLevel.Warning, AppLogCategory.User, "复制日志内容失败", ex.Message);
        }
    }
}