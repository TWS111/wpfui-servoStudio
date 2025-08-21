// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Windows.Media;
using Wpf.Ui.servoStudio.Models;

namespace Wpf.Ui.servoStudio.ViewModels;

public partial class DashboardViewModel : ViewModel
{
    private bool _isInitialized = false;

    [ObservableProperty]
    private int _counter = 0;

    [ObservableProperty]
    private List<object> _devices = [];

    [RelayCommand]
    private void OnCounterIncrement()
    {
        Counter++;
    }

    [RelayCommand]
    private void OnCounterClear()
    {
        Counter = 0;
    }

    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            InitializeViewModel();
        }
    }
    private void InitializeViewModel()
    {        
        Devices.Clear();
        _isInitialized = true;
    }
}
