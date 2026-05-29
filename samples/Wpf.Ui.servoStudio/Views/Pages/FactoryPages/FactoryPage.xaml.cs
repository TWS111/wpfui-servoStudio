// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.servoStudio.ViewModels.Factory;

namespace Wpf.Ui.servoStudio.Views.Pages.FactoryPages;

public partial class FactoryPage : INavigableView<FactoryViewModel>
{
    public FactoryViewModel ViewModel { get; }

    public FactoryPage(FactoryViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();

        _ = new FactoryGateHelper(this, FactoryLockOverlay, "厂家参数页");
    }
}