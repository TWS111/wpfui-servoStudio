// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.servoStudio.ViewModels.Hardware;

namespace Wpf.Ui.servoStudio.Views.Pages.HardwarePages;

/// <summary>
/// Interaction logic for MotorPage.xaml
/// </summary>
public partial class MotorPage : INavigableView<MotorViewModel>
{
    public MotorViewModel ViewModel { get; }

    public MotorPage(MotorViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }
}