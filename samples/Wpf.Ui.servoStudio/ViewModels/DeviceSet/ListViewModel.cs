// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using Windows.ApplicationModel.VoiceCommands;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;
using Wpf.Ui.servoStudio.Models;

namespace Wpf.Ui.servoStudio.ViewModels.DeviceSet;

public partial class ListViewModel : ViewModel
{
    private bool _isInitialized = false;

    [ObservableProperty]
    private ObservableCollection<Device> _devicesCollection = GenerateDeviceList();

    [ObservableProperty]
    private Visibility _isScaningVisible = Visibility.Hidden;

    [ObservableProperty]
    private bool _isTurningDatagirdAviliable = false;
    [ObservableProperty]
    public static bool _isDeviceInfoChecked = false;
    
    [ObservableProperty]
    private TrulyObservableCollection<PortWeight> _weightCollection = new TrulyObservableCollection<PortWeight>();
    private System.Type DialogResult;
    
   
    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            InitializeViewModel();
        }
    }

    private static ObservableCollection<Device> GenerateDeviceList()
    {
        var random = new Random();
        var devices = new ObservableCollection<Device> { }; 

        for (int i = 0; i < 5; i++)
        {
            
        }

        return devices;
    }

    [RelayCommand]
    private async Task OnShowDialog( Type type)
    {
        
        //ContentDialogResult result = await contentDialogService.ShowSimpleDialogAsync(
        //    new SimpleContentDialogCreateOptions()
        //    {
        //        Title = "选择通信方式",
        //        Content = "",
        //        PrimaryButtonText = "EtherCAT",
        //        SecondaryButtonText = "CANopen",
        //        CloseButtonText = "RS485",
        //    }
        //);

        //DialogResultText = result switch
        //{
        //    ContentDialogResult.Primary => "EtherCAT",
        //    ContentDialogResult.Secondary => "CANopen",
        //    _ => "RS485",
        //};
               
    }       

    private void InitializeViewModel()
    {
        _weightCollection.Add(
            new PortWeight
            {
                SoftwareVersion = "1",
                GroupNumber = "1",
                TurningOptions = new List<string> { "0.5", "1", "2", "5", "10", "20", "50", "100" },
                SelectedTurning = "5"
            }
            );
        _weightCollection.Add(
            new PortWeight
            {
                SoftwareVersion = "1",
                GroupNumber = "1",
                TurningOptions = new List<string> { "0.5", "1", "2", "5", "10", "20", "50", "100" },
                SelectedTurning = "1"
            }
            );
        _isInitialized = true;
    }
}
