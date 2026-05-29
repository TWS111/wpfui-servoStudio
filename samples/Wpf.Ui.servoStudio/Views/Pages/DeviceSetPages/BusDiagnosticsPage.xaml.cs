// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.Views.Pages.DeviceSetPages;

/// <summary>
/// 总线诊断页：通过周期性发送轻量请求，统计 EtherCAT / Modbus 的成功率、时延与错误分布。
/// </summary>
public partial class BusDiagnosticsPage : INavigableView<BusDiagnosticsViewModel>
{
    public BusDiagnosticsViewModel ViewModel { get; }

    public BusDiagnosticsPage(BusDiagnosticsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }
}
