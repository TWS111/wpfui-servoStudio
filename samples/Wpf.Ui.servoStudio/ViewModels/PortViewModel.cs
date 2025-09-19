// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Wpf.Ui.servoStudio.ViewModels;

public partial class PortViewModel : ViewModel
{
    
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
    
    private void OnPortReceive()
    {
        // Handle port receive logic here
        // This method can be called when data is received from the port
    }
}
