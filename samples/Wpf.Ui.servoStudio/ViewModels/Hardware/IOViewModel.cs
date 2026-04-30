// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using System.Windows.Threading;
using Core.Net.EtherCAT;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels.Hardware;

public partial class IOViewModel(DeviceAddViewModel deviceAddViewModel) : ViewModel
{
    private bool _isInitialized = false;
    private DispatcherTimer? _monitorTimer;

    #region EtherCAT 辅助

    private IServoMaster Master => deviceAddViewModel.ActiveServoMaster;
    private IServoAxis? Axis => deviceAddViewModel.ActiveAxis;

    #endregion

    #region 属性

    [ObservableProperty]
    private ObservableCollection<HardwareController> _hardwareControllerCollection = new();

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionInfo = "设备未连接";

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private int _readProgress;

    [ObservableProperty]
    private int _readTotal;

    [ObservableProperty]
    private bool _isAutoMonitoring;

    #endregion

    #region 命令

    [RelayCommand]
    private async Task OnReadAllParameters()
    {
        if (!deviceAddViewModel.IsAnyConnected || Axis == null)
        {
            StatusText = "设备未连接，无法读取参数";
            return;
        }

        IsBusy = true;
        ReadTotal = HardwareControllerCollection.Count;
        ReadProgress = 0;
        StatusText = "正在读取参数…";
        int successCount = 0;
        int failCount = 0;

        try
        {
            var slaveAddr = Axis.SlaveAddr;

            foreach (HardwareController param in HardwareControllerCollection)
            {
                try
                {
                    ushort readValue = 0;
                    bool ok = await Task.Run(() =>
                        Master.TryReadSDO(slaveAddr, param.SdoIndex, param.SdoSubIndex, out readValue));

                    if (ok)
                    {
                        param.Value = readValue.ToString();
                        param.DeviceValue = readValue.ToString();
                        param.IsReadSuccess = true;
                        param.StatusText = "读取成功";
                        successCount++;
                    }
                    else
                    {
                        param.IsReadSuccess = false;
                        param.StatusText = "读取失败";
                        failCount++;
                    }
                }
                catch (Exception ex)
                {
                    param.IsReadSuccess = false;
                    param.StatusText = $"异常: {ex.Message}";
                    failCount++;
                }

                ReadProgress++;
            }

            StatusText = $"读取完成: 成功 {successCount}, 失败 {failCount}";
            AppData.AppLogViewModel.Log(AppLogLevel.Info, AppLogCategory.SDO, "IO参数读取完成",
                $"成功 {successCount}, 失败 {failCount}");
        }
        catch (Exception ex)
        {
            StatusText = $"读取异常: {ex.Message}";
            AppData.AppLogViewModel.Log(AppLogLevel.Error, AppLogCategory.SDO, "IO参数读取失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OnWriteModifiedParameters()
    {
        if (!deviceAddViewModel.IsAnyConnected || Axis == null)
        {
            StatusText = "设备未连接，无法写入参数";
            return;
        }

        var modifiedParams = HardwareControllerCollection.Where(p => p.IsModified && !p.IsReadOnly).ToList();
        if (modifiedParams.Count == 0)
        {
            StatusText = "没有已修改的参数需要写入";
            return;
        }

        IsBusy = true;
        ReadTotal = modifiedParams.Count;
        ReadProgress = 0;
        StatusText = $"正在写入 {modifiedParams.Count} 个参数…";
        int successCount = 0;
        int failCount = 0;

        try
        {
            var slaveAddr = Axis.SlaveAddr;

            foreach (HardwareController? param in modifiedParams)
            {
                try
                {
                    if (!ushort.TryParse(param.Value, out ushort writeValue))
                    {
                        param.StatusText = "值格式错误";
                        failCount++;
                        ReadProgress++;
                        continue;
                    }

                    bool ok = await Task.Run(() =>
                        Master.TryWriteSDO(slaveAddr, param.SdoIndex, param.SdoSubIndex, writeValue));

                    if (ok)
                    {
                        param.DeviceValue = param.Value;
                        param.StatusText = "写入成功";
                        successCount++;
                    }
                    else
                    {
                        param.StatusText = "写入失败";
                        failCount++;
                    }
                }
                catch (Exception ex)
                {
                    param.StatusText = $"写入异常: {ex.Message}";
                    failCount++;
                }

                ReadProgress++;
            }

            StatusText = $"写入完成: 成功 {successCount}, 失败 {failCount}";
            AppData.AppLogViewModel.Log(AppLogLevel.Info, AppLogCategory.SDO, "IO参数写入完成",
                $"成功 {successCount}, 失败 {failCount}");
        }
        catch (Exception ex)
        {
            StatusText = $"写入异常: {ex.Message}";
            AppData.AppLogViewModel.Log(AppLogLevel.Error, AppLogCategory.SDO, "IO参数写入失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OnRefreshMonitorParameters()
    {
        if (!deviceAddViewModel.IsAnyConnected || Axis == null) return;

        var monitorParams = HardwareControllerCollection.Where(p => p.IsReadOnly).ToList();
        var slaveAddr = Axis.SlaveAddr;

        foreach (HardwareController? param in monitorParams)
        {
            try
            {
                ushort readValue = 0;
                bool ok = await Task.Run(() =>
                    Master.TryReadSDO(slaveAddr, param.SdoIndex, param.SdoSubIndex, out readValue));

                if (ok)
                {
                    param.Value = readValue.ToString();
                    param.DeviceValue = readValue.ToString();
                    param.IsReadSuccess = true;
                }
            }
            catch
            {
            }
        }
    }

    [RelayCommand]
    private void OnToggleAutoMonitor()
    {
        if (IsAutoMonitoring)
        {
            StopMonitorTimer();
            IsAutoMonitoring = false;
            StatusText = "已停止自动监视";
        }
        else
        {
            if (!deviceAddViewModel.IsAnyConnected || Axis == null)
            {
                StatusText = "设备未连接，无法启动自动监视";
                return;
            }

            StartMonitorTimer();
            IsAutoMonitoring = true;
            StatusText = "已开启自动监视 (500ms)";
        }
    }

    [RelayCommand]
    private void OnDiscardAllChanges()
    {
        foreach (HardwareController param in HardwareControllerCollection)
        {
            if (param.IsModified)
            {
                param.Value = param.DeviceValue;
            }
        }

        StatusText = "已放弃所有修改";
    }

    [RelayCommand]
    private async Task OnWriteSingleParameter(HardwareController? param)
    {
        if (param == null || param.IsReadOnly) return;
        if (!deviceAddViewModel.IsAnyConnected || Axis == null)
        {
            StatusText = "设备未连接";
            return;
        }

        if (!ushort.TryParse(param.Value, out ushort writeValue))
        {
            param.StatusText = "值格式错误";
            return;
        }

        try
        {
            var slaveAddr = Axis.SlaveAddr;
            bool ok = await Task.Run(() =>
                Master.TryWriteSDO(slaveAddr, param.SdoIndex, param.SdoSubIndex, writeValue));

            if (ok)
            {
                param.DeviceValue = param.Value;
                param.StatusText = "写入成功";
                StatusText = $"[{param.Index}] {param.Name} 写入成功";
                AppData.AppLogViewModel.Log(AppLogLevel.Info, AppLogCategory.SDO, "参数写入成功",
                    $"{param.Index} = {writeValue}");
            }
            else
            {
                param.StatusText = "写入失败";
                StatusText = $"[{param.Index}] {param.Name} 写入失败";
            }
        }
        catch (Exception ex)
        {
            param.StatusText = $"异常: {ex.Message}";
            StatusText = $"[{param.Index}] 写入异常: {ex.Message}";
        }
    }

    #endregion

    #region 自动监视定时器

    private void StartMonitorTimer()
    {
        StopMonitorTimer();
        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _monitorTimer.Tick += async (_, _) => await OnRefreshMonitorParameters();
        _monitorTimer.Start();
    }

    private void StopMonitorTimer()
    {
        if (_monitorTimer != null)
        {
            _monitorTimer.Stop();
            _monitorTimer = null;
        }
    }

    #endregion

    #region 初始化

    private void InitializeViewModel()
    {
        _isInitialized = true;

        HardwareControllerCollection.Clear();

        foreach (HRegisterEntry entry in HVariables.RegisterTable
            .Where(e => e.HIndex.StartsWith("H04", StringComparison.OrdinalIgnoreCase)))
        {
            HardwareController param = HardwareController.FromRegisterEntry(entry);
            HardwareControllerCollection.Add(param);
        }

        UpdateConnectionState();
    }

    private void UpdateConnectionState()
    {
        IsConnected = deviceAddViewModel.IsAnyConnected && Axis != null;
        ConnectionInfo = IsConnected
            ? $"已连接: {deviceAddViewModel.EthernetSlaveNameInfo}"
            : "设备未连接";
    }

    #endregion

    #region 生命周期

    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            InitializeViewModel();
        }

        UpdateConnectionState();
    }

    public override void OnNavigatedFrom()
    {
        StopMonitorTimer();
        IsAutoMonitoring = false;
    }

    #endregion
}