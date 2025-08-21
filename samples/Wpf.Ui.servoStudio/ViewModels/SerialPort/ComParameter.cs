// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Wpf.Ui.servoStudio.Models;

namespace Wpf.Ui.servoStudio.ViewModels.SerialPort
{
    public partial class ComParameter : ViewModel
    {
        private bool _isInitialized = false;

        [ObservableProperty]
        private ObservableCollection<SerialPorts> _portsCollection;

        [RelayCommand]
        private void OnProductsAchieve()
        {
            PortsCollection = GeneratePorts();
        }

        private static ObservableCollection<SerialPorts> GeneratePorts()
        {
            var serialPorts = new ObservableCollection<SerialPorts> { };

            return serialPorts;
        }
    }
}
