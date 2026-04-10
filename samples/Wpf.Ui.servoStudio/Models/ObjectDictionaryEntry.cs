// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Wpf.Ui.servoStudio.Models;

public enum SdoDataType
{
    UInt8,
    Int8,
    UInt16,
    Int16,
    UInt32,
    Int32,
    VisibleString,
    Raw
}

public partial class ObjectDictionaryEntry : ObservableObject
{
    [ObservableProperty]
    private string _indexHex = string.Empty;

    [ObservableProperty]
    private string _subIndexHex = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _dataTypeName = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private string _hexValue = string.Empty;
}

public partial class PdoMappingEntry : ObservableObject
{
    [ObservableProperty]
    private string _direction = string.Empty;

    [ObservableProperty]
    private string _pdoIndexHex = string.Empty;

    [ObservableProperty]
    private string _mappedIndexHex = string.Empty;

    [ObservableProperty]
    private string _mappedSubIndexHex = string.Empty;

    [ObservableProperty]
    private string _bitLength = string.Empty;

    [ObservableProperty]
    private string _objectName = string.Empty;
}
