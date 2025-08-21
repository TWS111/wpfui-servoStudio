// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Wpf.Ui.servoStudio.Models;

public enum ErrorLevel
{
    Warning1,
    Warning2,
    Warning3,
    Error1,
    Error2,
    Error3,
}
public enum Port
{
    EtherCAT,
    CANopen,
    Modbus,
}

public enum DataType
{
    Bool,
    uint8,
    int8,
    uint16,
    int16,
    uint32,
    int32,    
}

public enum Unit
{
    Thousandth,
    Hundredth,
    Tenth,
    One,
    ThousandthA,
    HundredthA,
    TenthA,
    A,
    ThousandthV,
    HundredthV,
    TenthV,
    V,
    ThousandthW,
    HundredthW,
    TenthW,
    W,
    ThousandthKW,
    HundredthKW,
    TenthKW,
    KW,
    ThousandthNM,
    HundredthNM,
    TenthNM,
    NM,
    Hz,
    TenthS,
    S,
    Rpm,
    Users,
}

public enum Baud
{
    Baud300,
    Baud600,
    Baud1200,
    Baud2400,
    Baud4800,
    Baud9600,
    Baud14400,
    Baud19200,
    Baud38400,
    Baud57600,
    Baud115200,
    Baud128000,
    Baud230400,
    Baud256000,
    Baud460800,
    Baud500000,
    Baud512000,
    Baud600000,
    Baud750000,
    Baud921600,
    Baud1M,
    Baud1M5,
    Baud2M,
}

public enum CheckBit
{
    None,
    One,
}

public enum DataBit
{
    Eight,
    Nine,
}

public enum StopBit
{
    One,
    OnePointFive,
    Two,
}
