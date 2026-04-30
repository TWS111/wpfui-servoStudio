// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Wpf.Ui.Abstractions.Controls;

namespace Wpf.Ui.servoStudio.Views.Pages;

/// <summary>
/// Interaction logic for SettingsPage.xaml
/// </summary>
public partial class SettingsPage : INavigableView<ViewModels.SettingsViewModel>
{
    public ViewModels.SettingsViewModel ViewModel { get; }

    public SettingsPage(ViewModels.SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }

    private void FactoryUnlockButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        // 密码只在内存中使用，输入后立即清空，不做任何持久化
        var password = FactoryPasswordBox.Password;
        ViewModel.TryUnlockFactory(password);
        FactoryPasswordBox.Clear();
    }
}