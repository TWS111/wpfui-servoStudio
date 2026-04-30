// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using Wpf.Ui.Controls;
using Wpf.Ui.servoStudio.Services;

namespace Wpf.Ui.servoStudio.ViewModels;

public partial class HomeViewModel : ViewModel
{
    private readonly INavigationService _navigationService;
    private readonly PageUsageTracker _usageTracker;

    [ObservableProperty]
    private ObservableCollection<QuickAccessItem> _quickAccessItems = [];

    public HomeViewModel(INavigationService navigationService, PageUsageTracker usageTracker)
    {
        _navigationService = navigationService;
        _usageTracker = usageTracker;

        RefreshQuickAccess();
    }

    /// <summary>
    /// Refreshes the quick access items based on current usage data.
    /// </summary>
    public void RefreshQuickAccess()
    {
        List<QuickAccessItem> items = _usageTracker.GetTopPages(3);
        QuickAccessItems = new ObservableCollection<QuickAccessItem>(items);
    }

    [RelayCommand]
    private void NavigateTo(Type pageType)
    {
        _usageTracker.RecordVisit(pageType);
        _ = _navigationService.Navigate(pageType);
    }
}