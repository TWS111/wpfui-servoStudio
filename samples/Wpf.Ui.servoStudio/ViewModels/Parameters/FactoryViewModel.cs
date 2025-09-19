// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using RJCP.IO.Ports;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Media;
using Windows.Networking;
using Wpf.Ui.servoStudio.Models;

namespace Wpf.Ui.servoStudio.ViewModels.Parameters;

public partial class FactoryViewModel : ViewModel
{
    private bool _isInitialized = false;
    private bool _isVirtualAllSelected = false;
    FactoryParametersFillInfo _factoryParametersFill = new FactoryParametersFillInfo();

    [ObservableProperty]
    private TrulyObservableCollection<FactoryParameters> _factoryParametersCollection = new TrulyObservableCollection<FactoryParameters>();       

    [ObservableProperty]
    private ObservableCollection<FactoryParameters> _originalCollection = new ObservableCollection<FactoryParameters>();

    //[ObservableProperty]
    //[NotifyPropertyChangedFor(nameof(Caption))]   

    [RelayCommand]
    private void OnAutoSendReadAchieve()
    {
        Flags.isAutoSendReadFrameOn = true;
    }

    [RelayCommand]
    private void OnAutoSendReadCancel()
    {
        Flags.isAutoSendReadFrameOn = false;
    }

    [RelayCommand]
    private void OnParametersWrite()
    {
        FactoryParametersCollection.Clear();
    }
  
    private TrulyObservableCollection<FactoryParameters> GenerateProducts()
    {        
        FactoryParametersCollection.CollectionChanged += _info_CollectionChanged;
        
        for (int i = 0; i < FactoryParametersFillInfo.parametersFillinLength; i++)
        {
            _factoryParametersCollection.Add(
                new FactoryParameters
                {
                    Index = _factoryParametersFill.index[i],
                    IndexHex = _factoryParametersFill.indexHex[i],
                    Subscribe = _factoryParametersFill.subscribe[i],
                    ParameterName  = _factoryParametersFill.varibleName[i],
                    TypeUnit = (DataType)_factoryParametersFill.typeUnitIndex[i],
                    MinValue = _factoryParametersFill.minValue[i],
                    MaxValue = _factoryParametersFill.maxValue[i], 
                    DefaultValue = _factoryParametersFill.defaultValue[i],
                }
            );
        }

        return _factoryParametersCollection;
    }
       
    private void _info_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    { 
        if (e.Action == NotifyCollectionChangedAction.Replace)
        {
            var inCollection = sender as TrulyObservableCollection<FactoryParameters>;
            byte nowSlaveAddress = 0;
            int nowSlaveAddressIndex = Array.IndexOf(_factoryParametersFill.index, "H0C_01");
            if (nowSlaveAddressIndex == -1)
            {
                nowSlaveAddress = 0x03;
            }
            else
            {
                nowSlaveAddress = (byte)inCollection[nowSlaveAddressIndex].ActualValue;
            }

            PortTransmitInfo.slaveAddress = nowSlaveAddress;
            //PortTransmitInfo.registerIndex = (byte)e.NewStartingIndex;
            PortTransmitInfo.registerLength = 1;
            PortTransmitInfo.bufferTransmitData = new byte?[10];
            //PortTransmitInfo.bufferTransmitData
        }

        if (e.NewItems != null)
        {
            foreach (FactoryParameters item in e.NewItems)
            {

            }
        }
    }

    [RelayCommand]
    private void OnVirtualAllSelect()
    {        

    }

    //partial void OnFactoryParametersCollectionChanged(ObservableCollection<FactoryParameters> oldValue, ObservableCollection<FactoryParameters> newValue)
    //{ 
    //    // Handle the collection change event here
    //    if (newValue.Count == 0)
    //    {
    //        ;
    //    }        
    //}        

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
        FactoryParametersCollection = GenerateProducts();
        Thread sendComCheckFrame = new Thread(new ThreadStart(CheckingComBuffer));
        sendComCheckFrame.Start();
    }
}
