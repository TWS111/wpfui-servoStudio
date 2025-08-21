// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Navigation;
using Windows.ApplicationModel.VoiceCommands;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;
using Wpf.Ui.servoStudio.Models;

namespace Wpf.Ui.servoStudio.ViewModels.DeviceSet;

public partial class StartViewModel(
    IContentDialogService contentDialogService, 
    INavigationService navigationService
    ) : ViewModel
{
    private bool _isInitialized = false;

    [ObservableProperty]
    private ObservableCollection<Device> _devicesCollection = GenerateDeviceList();

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
        Port[] portMethod = [Port.EtherCAT, Port.CANopen, Port.Modbus];

        for (int i = 0; i < 5; i++)
        {
            devices.Add(
                new Device
                {
                    Index = i,
                    DeviceName ="COM" + random.Next(0, 20).ToString(),
                    SlaveAddress = random.Next(0, 20),
                    PortMethud = portMethod[random.Next(0, portMethod.Length)],
                }
            );
        }

        return devices;
    }

    [RelayCommand]
    private void OnPageSelect()
    {
        
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

        _ = navigationService.Navigate(type);
    }       

    private void InitializeViewModel()
    {        
        
        _isInitialized = true;
    }
}
