// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.servoStudio.ViewModels.Hardware;

namespace Wpf.Ui.servoStudio.Views.Pages.HardwarePages;

/// <summary>
/// Interaction logic for ControllerPage.xaml
/// </summary>
public partial class ControllerPage : INavigableView<ControllerViewModel>
{
    public ControllerViewModel ViewModel { get; }

    public ControllerPage(ControllerViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }
}

/// <summary>
/// true → Collapsed, false → Visible
/// </summary>
public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility.Collapsed;
    }
}