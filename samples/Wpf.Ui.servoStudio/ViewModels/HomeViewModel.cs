// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using Wpf.Ui.servoStudio.Services;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels;

public partial class HomeViewModel : ViewModel
{
    private readonly INavigationService _navigationService;
    private readonly PageUsageTracker _usageTracker;

    public DeviceAddViewModel DeviceAdd { get; }

    [ObservableProperty]
    private ObservableCollection<QuickAccessItem> _quickAccessItems = [];

    public HomeViewModel(INavigationService navigationService, PageUsageTracker usageTracker, DeviceAddViewModel deviceAdd)
    {
        _navigationService = navigationService;
        _usageTracker = usageTracker;
        DeviceAdd = deviceAdd;
        RefreshQuickAccess();
    }

    public void RefreshQuickAccess()
    {
        List<QuickAccessItem> items = _usageTracker.GetTopPages(3);
        QuickAccessItems = new ObservableCollection<QuickAccessItem>(items);
    }

    public override void OnNavigatedTo() => RefreshQuickAccess();

    [RelayCommand]
    private void NavigateTo(Type pageType)
    {
        _ = _navigationService.Navigate(pageType);
    }
}
