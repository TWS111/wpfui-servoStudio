// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Windows;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.Controls;
using Wpf.Ui.servoStudio.Services;
using Wpf.Ui.servoStudio.ViewModels.Firmware;

namespace Wpf.Ui.servoStudio.Views.Pages.FirmwarePages;

/// <summary>
/// Interaction logic for FactoryFirmwarePage.xaml
/// </summary>
public partial class FactoryFirmwarePage : INavigableView<FactoryFirmwareViewModel>
{
    public FactoryFirmwareViewModel ViewModel { get; }

    public FactoryFirmwarePage(FactoryFirmwareViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();

        // 页面构造即根据当前解锁状态更新遮罩，避免内容闪现
        UpdateLockOverlay();

        IsVisibleChanged += FactoryFirmwarePage_IsVisibleChanged;
        FactoryAccessService.UnlockStateChanged += OnUnlockStateChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Unloaded += (_, _) =>
        {
            FactoryAccessService.UnlockStateChanged -= OnUnlockStateChanged;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        };
    }

    /// <summary>
    /// ViewModel 把 OperationPassword 清空时（如上传/删除成功后），同步清空 PasswordBox 显示。
    /// </summary>
    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FactoryFirmwareViewModel.OperationPassword)
            && string.IsNullOrEmpty(ViewModel.OperationPassword)
            && OperationPasswordBox is not null
            && OperationPasswordBox.Password.Length > 0)
        {
            OperationPasswordBox.Password = string.Empty;
        }
    }

    private bool _redirecting;

    private void OnUnlockStateChanged(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(new Action(UpdateLockOverlay));
    }

    /// <summary>
    /// PasswordBox.Password 不支持数据绑定，这里手动同步到 ViewModel。
    /// </summary>
    private void OperationPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox box)
        {
            ViewModel.OperationPassword = box.Password;
        }
    }

    private void UpdateLockOverlay()
    {
        FactoryLockOverlay.Visibility = FactoryAccessService.IsUnlocked
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void FactoryFirmwarePage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // 先根据最新状态刷新遮罩，确保不会透出厂家内容
        UpdateLockOverlay();

        if (!IsVisible || _redirecting)
        {
            return;
        }

        if (FactoryAccessService.IsUnlocked)
        {
            return;
        }

        _redirecting = true;

        // 延迟到当前导航 / 布局流程结束后再弹窗。
        // 直接在 IsVisibleChanged 中调用 ShowDialog 会触发
        // “暂停调度程序处理时，无法执行此操作。”异常。
        _ = Dispatcher.BeginInvoke(
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
                Content = "无权限访问厂家固件页。\r\n请先到“设置”页输入正确的厂家密码解锁。",
                CloseButtonText = "确定",
                CloseButtonAppearance = ControlAppearance.Primary,
                Owner = Window.GetWindow(this),
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
