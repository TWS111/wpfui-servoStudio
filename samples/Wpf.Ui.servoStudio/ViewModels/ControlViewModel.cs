// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using ScottPlot.WPF;
using System.Windows.Media;
using Wpf.Ui.servoStudio.Models;

namespace Wpf.Ui.servoStudio.ViewModels;

public partial class ControlViewModel : ViewModel
{
    private bool _isInitialized = false;
    public bool _isCurrentSelected = false;
    private bool _isCCWSelected = false;
    private bool _isCWSelected = true;

    //private List<double> degreeDataX = new List<double>();
    //private List<double> degreeDataY = new List<double>();

    
    [ObservableProperty]
    private Visibility _isCCWVisible = Visibility.Hidden;
    [ObservableProperty]
    private Visibility _isCWVisible = Visibility.Hidden;

    [ObservableProperty]
    private bool _isMotorEnabled;

    [ObservableProperty]
    public bool _isPluseNotSelected;

    [ObservableProperty]
    public bool _isPositionSelected;

    [ObservableProperty]
    private Visibility _isCurrentUnitVisible = Visibility.Hidden;
    [ObservableProperty]
    private Visibility _isVelocityUnitVisible = Visibility.Hidden;
    [ObservableProperty]
    private Visibility _isPositionUnitVisible = Visibility.Hidden;

    [ObservableProperty]
    private WpfPlot _signalImage = new WpfPlot();

    
    [RelayCommand]
    public void OnSelectEnabled(object sender)
    {       
        IsMotorEnabled = !IsMotorEnabled;
        if (IsMotorEnabled)
        {
            IsCCWVisible = _isCCWSelected ? Visibility.Visible : Visibility.Hidden;
            IsCWVisible = _isCWSelected ? Visibility.Visible : Visibility.Hidden;
        }
        else
        {
            IsCCWVisible = Visibility.Hidden;
            IsCWVisible = Visibility.Hidden;
        }
    }

    [RelayCommand]
    public void OnPluseSelected(object sender)
    {
        IsPluseNotSelected = false;
        _isCurrentSelected = false;
        IsPositionSelected = true;
        IsCurrentUnitVisible = Visibility.Hidden;
        IsVelocityUnitVisible = Visibility.Hidden;
        IsPositionUnitVisible = Visibility.Hidden;
    }

    [RelayCommand]
    public void OnDirectionSelected(object sender)
    {
        _isCCWSelected = !_isCCWSelected;
        _isCWSelected = !_isCWSelected;
        if (IsMotorEnabled)
        {
            IsCCWVisible = _isCCWSelected ? Visibility.Visible : Visibility.Hidden;
            IsCWVisible = _isCWSelected ? Visibility.Visible : Visibility.Hidden;
        }
        else
        {
            IsCCWVisible = Visibility.Hidden;
            IsCWVisible = Visibility.Hidden;
        }
    }

    [RelayCommand]
    public void OnCurrentSelected(object sender)
    {
        IsPluseNotSelected = true;
        _isCurrentSelected = true;
        IsPositionSelected = false;
        IsCurrentUnitVisible = Visibility.Visible;
        IsVelocityUnitVisible = Visibility.Hidden;
        IsPositionUnitVisible = Visibility.Hidden;
    }

    [RelayCommand]
    public void OnVelocitySelected(object sender)
    {
        IsPluseNotSelected = true;
        _isCurrentSelected = true;
        IsPositionSelected = false;
        IsCurrentUnitVisible = Visibility.Hidden;
        IsVelocityUnitVisible = Visibility.Visible;
        IsPositionUnitVisible = Visibility.Hidden;
    }

    [RelayCommand]
    public void OnPositionSelected(object sender)
    {
        IsPluseNotSelected = true;
        IsPositionSelected = true;
        _isCurrentSelected = true;
        IsCurrentUnitVisible = Visibility.Hidden;
        IsVelocityUnitVisible = Visibility.Hidden;
        IsPositionUnitVisible = Visibility.Visible;
    }

    [RelayCommand]
    private void OnGiven()
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
    }
}
