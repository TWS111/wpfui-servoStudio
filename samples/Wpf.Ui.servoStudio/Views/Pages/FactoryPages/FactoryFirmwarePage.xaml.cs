// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Windows;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.servoStudio.ViewModels.Factory;

namespace Wpf.Ui.servoStudio.Views.Pages.FactoryPages;

public partial class FactoryFirmwarePage : INavigableView<FactoryFirmwareViewModel>
{
    public FactoryFirmwareViewModel ViewModel { get; }

    public FactoryFirmwarePage(FactoryFirmwareViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();

        _ = new FactoryGateHelper(this, FactoryLockOverlay, "厂家固件页");

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

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

    private void OperationPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox box)
        {
            ViewModel.OperationPassword = box.Password;
        }
    }
}
