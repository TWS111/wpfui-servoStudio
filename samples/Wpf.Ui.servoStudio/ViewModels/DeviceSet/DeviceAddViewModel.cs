// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using RJCP.IO.Ports;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;
using Wpf.Ui.servoStudio.Models;

namespace Wpf.Ui.servoStudio.ViewModels.DeviceSet;

public partial class DeviceAddViewModel(IContentDialogService contentDialogService) : ViewModel
{
    private static System.Threading.Timer portSearcher;
    private static System.Threading.Timer timer10ms;
    
    private string[] _portNamesOld;
    private bool isPortNameChanged = false;
    private bool _isInitialized = false;    

    [ObservableProperty]
    private string _dialogResultText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _comboBoxDataBitFamilies =
     [
        "8",
        "9",
    ];

    [ObservableProperty]
    private ObservableCollection<string> _comboBoxCheckBitFamilies =
     [
        "None",
        "Odd",
        "Even",
    ];

    [ObservableProperty]
    private ObservableCollection<string> _comboBoxStopBitFamilies =
     [
        "1",
        "1.5",
        "2",   
    ];

    [ObservableProperty]
    private ObservableCollection<string> _comboBoxBaudFamilies =
     [        
        "4800",
        "9600",
        "14400",
        "19200",
        "38400",
        "57600",
        "115200",     
        "128000",
        "256000",
        "500000",
        "512000",
        "600000",
        "750000",
        "921600",
    ];

    [ObservableProperty]
    private ObservableCollection<string> _comboBoxPortNameFamilies = new ObservableCollection<string>();

    [ObservableProperty]
    private int _progressbarValue;

    [ObservableProperty]
    private bool _isLinkSucceed = false;

    [ObservableProperty]
    private bool _isLinkFailed = false;

    [ObservableProperty]
    private int _comboBoxPortNameSelect;

    [ObservableProperty]
    private int _comboBoxCheckBitSelect;

    [ObservableProperty]
    private int _comboBoxDataBitSelect;

    [ObservableProperty]
    private int _comboBoxStopBitSelect;

    [ObservableProperty]
    private int _comboBoxBaudSelect;       

    [ObservableProperty]
    private string? _isTestText = string.Empty;

    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            InitializeViewModel();           
        }
    }

    [RelayCommand]
    private void OnDeviceConnect()
    {
        IsTestText = ComboBoxStopBitSelect.ToString();
        timer10ms = new System.Threading.Timer(new TimerCallback(this.timer10ms_Tick), null, 0, 15);
        timer10ms.Change(0, 10);
        if (!vcom.IsOpen)
        {
            vcom.PortName = ComboBoxPortNameFamilies[ComboBoxPortNameSelect];
            vcom.BaudRate = Convert.ToInt32(ComboBoxBaudFamilies[ComboBoxBaudSelect]);
            vcom.DataBits = Convert.ToInt32(ComboBoxDataBitFamilies[ComboBoxDataBitSelect]);
            switch (ComboBoxCheckBitFamilies[ComboBoxCheckBitSelect])
            {
                case "None":
                    vcom.Parity = Parity.None;
                    break;
                case "Odd":
                    vcom.Parity = Parity.Odd;
                    break;
                case "Even":
                    vcom.Parity = Parity.Even;
                    break;
            }

            switch (ComboBoxStopBitFamilies[ComboBoxStopBitSelect])
            {
                case "1":
                    vcom.StopBits = StopBits.One;
                    break;
                case "1.5":
                    vcom.StopBits = StopBits.One5;
                    break;
                case "2":
                    vcom.StopBits = StopBits.Two;
                    break;                
            }

            vcom.ReadTimeout = 500;
            vcom.WriteTimeout = 500;
            try
            {
                vcom.Open();
                if (vcom.IsOpen)
                {
                    vcom.NewLine = "/r/n";
                    vcom.RtsEnable = true;
                }
                IsLinkSucceed = true;
                IsLinkFailed = false;
                OnThreadStart();
            }
            catch (Exception ex)
            {
                IsLinkFailed = true;
                IsLinkSucceed = false;
            }
        }
    }

    private void InitializeViewModel()
    {                
        _isInitialized = true;
        _portNamesOld = vcom.GetPortNames();
        ComboBoxPortNameFamilies.Clear();
        for (int i = 0; i < _portNamesOld.Length; i++)
        {
            ComboBoxPortNameFamilies.Add(_portNamesOld[i]);
        }
        ComboBoxPortNameSelect = 0;
        portSearcher = new System.Threading.Timer(new TimerCallback(this.timerAutoSearch_Tick), null, 0, 15);
        portSearcher.Change(0, 200);
        ComboBoxDataBitSelect = 0;
        ComboBoxStopBitSelect = 0;
        ComboBoxCheckBitSelect = 0;
        ComboBoxBaudSelect = 6;
        ComboBoxPortNameSelect = 0;
    }

    private void timerAutoSearch_Tick(object state)
    {
        System.Windows.Application.Current.Dispatcher.Invoke((Action)(() =>
        {
            
            string[] _portNames = vcom.GetPortNames();
            string[] _portNameTemporary = _portNames.Distinct().ToArray(); 
            if (_portNameTemporary.Length > 0 && _portNameTemporary.Length != _portNamesOld.Length)
            {
                ComboBoxPortNameFamilies.Clear();
                for (int i = 0; i < _portNameTemporary.Length; i++)
                {
                    ComboBoxPortNameFamilies.Add(_portNameTemporary[i]);
                }
                ComboBoxPortNameSelect = 0;
            }
            else if (_portNameTemporary.Length > 0 && _portNameTemporary.Length != _portNamesOld.Length)
            {
                for (int i = 0; i < _portNameTemporary.Length; i++)
                {
                    if (_portNameTemporary[i] != _portNamesOld[i])
                    {
                        isPortNameChanged = true;
                    }
                }
                if (isPortNameChanged)
                {
                    ComboBoxPortNameFamilies.Clear();
                    for (int i = 0; i < _portNameTemporary.Length; i++)
                    {
                        ComboBoxPortNameFamilies.Add(_portNameTemporary[i]);
                    }
                    ComboBoxPortNameSelect = 0;
                    isPortNameChanged = false;
                }
            }

                _portNamesOld = vcom.GetPortNames();
        }));        
        
    }
    private void timer10ms_Tick(object state)
    {
        ProgressbarValue++;
        if(ProgressbarValue >= 100)
        {
            ProgressbarValue = 100;
            timer10ms.Dispose();
        }
    }
}
