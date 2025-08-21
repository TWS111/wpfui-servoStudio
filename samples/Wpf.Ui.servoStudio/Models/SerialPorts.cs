// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Wpf.Ui.servoStudio.Models;

public class SerialPorts
{
    public string? PortName { get; set; }

    public Baud Baud { get; set; }

    public DataBit Databit { get; set; }

    public CheckBit Checkbit { get; set; }   

    public StopBit Stopbit { get; set; }
}
