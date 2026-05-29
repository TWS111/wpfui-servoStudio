// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Wpf.Ui.servoStudio.ViewModels.DeviceSet;

/// <summary>
/// 设备列表页 ViewModel：
/// 该页用于展示当前已连接设备的详细信息（EtherCAT / CANopen / Modbus），
/// 并提供设备连接入口。数据与命令均直接转发到 <see cref="DeviceAddViewModel"/>。
/// </summary>
public partial class ListViewModel : ViewModel
{
    private bool _isInitialized;

    /// <summary>真正持有协议栈与从站对象的 ViewModel。</summary>
    public DeviceAddViewModel DeviceAdd { get; }

    public ListViewModel(DeviceAddViewModel deviceAdd)
    {
        DeviceAdd = deviceAdd;
    }

    public override void OnNavigatedTo()
    {
        DeviceAdd.OnNavigatedTo();

        if (!_isInitialized)
        {
            _isInitialized = true;
        }
    }
}
