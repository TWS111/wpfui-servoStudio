// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.servoStudio.Services;

namespace Wpf.Ui.servoStudio.Views.Pages.FactoryPages;

/// <summary>
/// 厂家页面权限门控辅助类。封装遮罩显隐、未授权弹窗和导航回退逻辑，
/// 三个厂家页面（FactoryPage / FactoryFirmwarePage / FrameFormatEditorPage）
/// 在构造函数中各自创建一个实例即可复用全部门控行为。
/// </summary>
internal sealed class FactoryGateHelper
{
    private readonly Page _page;
    private readonly Border _overlay;
    private readonly string _pageName;
    private bool _redirecting;

    public FactoryGateHelper(Page page, Border overlay, string pageName)
    {
        _page = page;
        _overlay = overlay;
        _pageName = pageName;

        UpdateLockOverlay();

        _page.IsVisibleChanged += OnIsVisibleChanged;
        FactoryAccessService.UnlockStateChanged += OnUnlockStateChanged;
        _page.Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        FactoryAccessService.UnlockStateChanged -= OnUnlockStateChanged;
    }

    private void OnUnlockStateChanged(object? sender, EventArgs e)
    {
        _ = _page.Dispatcher.BeginInvoke(new Action(UpdateLockOverlay));
    }

    private void UpdateLockOverlay()
    {
        _overlay.Visibility = FactoryAccessService.IsUnlocked
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateLockOverlay();

        if (!_page.IsVisible || _redirecting)
        {
            return;
        }

        if (FactoryAccessService.IsUnlocked)
        {
            return;
        }

        _redirecting = true;

        _ = _page.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(async () => await ShowNoPermissionDialogAsync()));
    }

    private async System.Threading.Tasks.Task ShowNoPermissionDialogAsync()
    {
        try
        {
            var messageBox = new Wpf.Ui.Controls.MessageBox
            {
                Title = "无权限",
                Content = $"无权限访问{_pageName}。\r\n请先到“设置”页输入正确的厂家密码解锁。",
                CloseButtonText = "确定",
                CloseButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary,
                Owner = Window.GetWindow(_page),
            };

            _ = await messageBox.ShowDialogAsync();

            if (App.Services.GetService(typeof(Wpf.Ui.INavigationService))
                is Wpf.Ui.INavigationService navigation)
            {
                if (!navigation.GoBack())
                {
                    _ = navigation.Navigate(typeof(Views.Pages.HomePage));
                }
            }
        }
        finally
        {
            _redirecting = false;
        }
    }
}
