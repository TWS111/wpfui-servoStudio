// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using System.Diagnostics.Metrics;
using System.Windows.Media;
using Wpf.Ui.servoStudio.Models;

namespace Wpf.Ui.servoStudio.ViewModels;

public partial class FaultInfoViewModel : ViewModel
{
    private bool _isInitialized = false;
    private bool _isVirtualAllSelected = false;

    [ObservableProperty]
    private ObservableCollection<Product> _productsCollection;

    [RelayCommand]
    private void OnProductsAchieve()
    {        
        ProductsCollection = GenerateProducts();
    }

    [RelayCommand]
    private void OnProductsClear()
    {
        ProductsCollection.Clear();
    }

    private static ObservableCollection<Product> GenerateProducts()
    {
        var random = new Random();
        var products = new ObservableCollection<Product> { };

        var adjectives = new[] { "Red", "Blueberry" };
        var names = new[] { "Marmalade", "Dumplings", "Soup" };
        ErrorLevel[] units = [ErrorLevel.Warning1, ErrorLevel.Warning3, ErrorLevel.Error1, ErrorLevel.Error2];

        for (int i = 0; i < 15; i++)
        {
            products.Add(
                new Product
                {
                    Time = 0,
                    ErrorCode = i,
                    ErrorName =
                        adjectives[random.Next(0, adjectives.Length)]
                        + " "
                        + names[random.Next(0, names.Length)],
                    
                    IsVirtual = random.Next(0, 2) == 1,
                }
            );
        }

        return products;
    }

    [RelayCommand]
    private void OnVirtualAllSelect()
    {
        for (int i = 0; i < ProductsCollection.Count; i++)
        {
            if(_isVirtualAllSelected is not true)
            {
                ProductsCollection[i].IsVirtual = true;
            }
            else
            {
                ProductsCollection[i].IsVirtual = false;
            }
        }

        _isVirtualAllSelected = !_isVirtualAllSelected;
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
