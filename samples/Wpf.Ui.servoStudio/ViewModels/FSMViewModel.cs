// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.Generic;
using System.Windows.Media;

using Wpf.Ui.servoStudio.Models;
namespace Wpf.Ui.servoStudio.ViewModels;

public partial class FSMViewModel : ViewModel
{
    [ObservableProperty]
    private int _counter = 0;

    private bool _isInitialized = false;        

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
