// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using System.Windows.Media;
using Wpf.Ui.servoStudio.Models;

namespace Wpf.Ui.servoStudio.ViewModels.Hardware;

public partial class ControllerViewModel : ViewModel
{
    private bool _isInitialized = false;
    private bool _isVirtualAllSelected = false;

    [ObservableProperty]
    private ObservableCollection<HardwareController> _hardwareControllerCollection;

    [RelayCommand]
    private void OnProductsAchieve()
    {
        
    }

    [RelayCommand]
    private void OnHardwareControllerClear()
    {
        HardwareControllerCollection.Clear();
    }

   

    [RelayCommand]
    private void OnVirtualAllSelect()
    {
        for (int i = 0; i < HardwareControllerCollection.Count; i++)
        {
            
        }

        _isVirtualAllSelected = !_isVirtualAllSelected;
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
        _isInitialized = true;
    }
}
