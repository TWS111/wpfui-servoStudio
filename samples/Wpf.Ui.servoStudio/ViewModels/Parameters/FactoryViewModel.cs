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
using System.Windows.Controls;
using System.Windows.Media;
using Windows.Networking;
using Wpf.Ui.servoStudio.Models;

namespace Wpf.Ui.servoStudio.ViewModels.Parameters;

public partial class FactoryViewModel : ViewModel, INotifyPropertyChanged
{
    private bool _isInitialized = false;
    private bool _isVirtualAllSelected = false;

  

    [ObservableProperty]
    private  ObservableCollection<FactoryParameters> _factoryParametersCollection = new ObservableCollection<FactoryParameters>();

    //[ObservableProperty]
    //[NotifyPropertyChangedFor(nameof(Caption))]
    public ObservableCollection<FactoryParameters> _info = new ObservableCollection<FactoryParameters>();

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

    

    private  ObservableCollection<FactoryParameters> GenerateProducts()
    {
        
        _info.CollectionChanged += _info_CollectionChanged;
        FactoryParametersFillInfo _factoryParametersFill = new FactoryParametersFillInfo();
        for (int i = 0; i < 64; i++)
        {
            _factoryParametersCollection.Add(
                new FactoryParameters
                {
                    Index = _factoryParametersFill.index[i],
                    IndexHex = _factoryParametersFill.indexHex[i],
                    Subscribe = _factoryParametersFill.subscribe[i],
                }
            );
        }

        return _factoryParametersCollection;
    }

    private void _info_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if(e.Action == NotifyCollectionChangedAction.Move)
        {
            Console.WriteLine("OHHHH");
        }
    }

    [RelayCommand]
    private void OnVirtualAllSelect()
    {        

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
        FactoryParametersCollection = GenerateProducts();
        Thread sendComCheckFrame = new Thread(new ThreadStart(CheckingComBuffer));
        sendComCheckFrame.Start();
    }
      

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {

        base.OnPropertyChanged(e);

        // 可以获取到是哪个属性改变
        var _proname = e.PropertyName;
    }
}
