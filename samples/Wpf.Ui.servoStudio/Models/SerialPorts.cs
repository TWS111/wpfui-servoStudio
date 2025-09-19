// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;

namespace Wpf.Ui.servoStudio.Models;

public class SerialPorts
{
    public string? PortName { get; set; }

    public Baud Baud { get; set; }

    public DataBit Databit { get; set; }

    public CheckBit Checkbit { get; set; }

    public StopBit Stopbit { get; set; }
}

public class PortWeight : ObservableObject
{
    public string? SoftwareVersion { get; set; }
    public string? GroupNumber { get; set; }

    public IReadOnlyList<string> TurningOptions { get; init; } = Array.Empty<string>();

    private string _selectedTurning = "";
    public string SelectedTurning
    {
        get => _selectedTurning;
        set => SetProperty(ref _selectedTurning, value);
    }
}

public enum SerialPortTransmitFault
{
    InvalidFunction,
    InvalidSlaveAddress,
    WrongCrcValue,
    WrongRegisterNumber,
    OtherTransmitFault,
    EOE
}

public class SerialPortFaultInfo
{
    public SerialPortTransmitFault faultName;
    public int[] faultCount = new int[(int)SerialPortTransmitFault.EOE];    
}

public class SerialPortFrameInfo
{
    public int slaveAddress;
    public int bufferRemain;
    public int readyToReadLength;
    public bool isNotReadyToWaitEnoughLength;
}

public class SerialPortTransmitInfo
{
    public byte slaveAddress;
    public byte controlWord;
    public byte[] registerIndex = new byte[2];
    public byte registerLength;
    public byte?[] bufferTransmitData;
    public string? faultDescription;
}

public class SerialPortReceiveInfo
{
    public byte[] headReceiveData;
    public byte[] bufferReceiveData;
    public string? faultDescription;
    public bool isDataAvailable;
}

public class QueueInsertInfo
{
    public bool isInsertToSendQueue;
    public byte slaveAddress;
    public byte insertGroupNumber;
    public byte insertCommand;
    public byte insertRegisterIndex;
    public byte insertRegisterLength;
    public byte[] bufferTransmitData = new byte[4];
}

public class QueueInfo()
{
    public List<QueueInsertInfo> bufferTransmitDataList = new List<QueueInsertInfo>();
    public int[] groupWeightSet = new int[0xF]
    {
        0,0,0,0,0,0,0,0,0,0,10,0,0,0,0
    };
    public int[] groupWeightActual = new int[0xF];

}