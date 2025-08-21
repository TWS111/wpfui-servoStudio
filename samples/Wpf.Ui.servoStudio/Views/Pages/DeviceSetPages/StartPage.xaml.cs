// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Drawing.Printing;
using System.Windows.Controls;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.Views.Pages.DeviceSetPages;

/// <summary>
/// Interaction logic for DataView.xaml
/// </summary>
public partial class StartPage : INavigableView<StartViewModel>
{   
    public StartViewModel ViewModel { get; }

    public StartPage(StartViewModel viewModel,
        IContentDialogService contentDialogService)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
        
    }
}
