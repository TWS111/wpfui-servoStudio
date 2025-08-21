// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.Views.Pages.DeviceSetPages;

/// <summary>
/// Interaction logic for DataView.xaml
/// </summary>
public partial class DeviceAddPage : INavigableView<DeviceAddViewModel>
{
    
    public DeviceAddViewModel ViewModel { get; }

    public DeviceAddPage(DeviceAddViewModel viewModel,
        IContentDialogService contentDialogService)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
        
    }
}
