// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Wpf.Ui.Abstractions.Controls;

namespace Wpf.Ui.servoStudio.Views.Pages.AppDataPages;

/// <summary>
/// 配置 JSON 读取 / 导出 页面。
/// </summary>
public partial class UserConfigPage : INavigableView<ViewModels.AppData.UserConfigViewModel>
{
    public ViewModels.AppData.UserConfigViewModel ViewModel { get; }

    public UserConfigPage(ViewModels.AppData.UserConfigViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }
}