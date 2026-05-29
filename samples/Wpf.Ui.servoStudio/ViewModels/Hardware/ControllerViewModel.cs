// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using System.Windows.Threading;
using Core.Net.EtherCAT;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.Services;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels.Hardware;

public partial class ControllerViewModel(DeviceAddViewModel deviceAddViewModel) : ViewModel
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

    /// <summary>
    /// 一键读取所有参数（从已连接的从站通过 SDO 逐一读取）
    /// </summary>
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
            AppData.AppLogViewModel.Log(AppLogLevel.Info, AppLogCategory.SDO, "控制器参数读取完成",
                $"成功 {successCount}, 失败 {failCount}");
        }
        catch (Exception ex)
        {
            StatusText = $"读取异常: {ex.Message}";
            AppData.AppLogViewModel.Log(AppLogLevel.Error, AppLogCategory.SDO, "控制器参数读取失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 写入所有已修改的参数到设备
    /// </summary>
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
            AppData.AppLogViewModel.Log(AppLogLevel.Info, AppLogCategory.SDO, "控制器参数写入完成",
                $"成功 {successCount}, 失败 {failCount}");
        }
        catch (Exception ex)
        {
            StatusText = $"写入异常: {ex.Message}";
            AppData.AppLogViewModel.Log(AppLogLevel.Error, AppLogCategory.SDO, "控制器参数写入失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 刷新监视参数（H0B 组只读参数）
    /// </summary>
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
                // 监视参数刷新异常时静默跳过
            }
        }
    }

    /// <summary>
    /// 开启/关闭监视参数自动刷新
    /// </summary>
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

    /// <summary>
    /// 放弃用户所有修改，恢复为设备值
    /// </summary>
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

    /// <summary>
    /// 写入单个参数
    /// </summary>
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
            .Where(e => IsControllerEntry(e))
            .Where(e => !RegisterDisableService.IsDisabledForActive(e.SdoIndex, e.SdoSubIndex)))
        {
            HardwareController param = HardwareController.FromRegisterEntry(entry);
            HardwareControllerCollection.Add(param);
        }

        RegisterDisableService.Changed -= OnDisabledChanged;
        RegisterDisableService.Changed += OnDisabledChanged;

        UpdateConnectionState();
    }

    private void OnDisabledChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            HardwareControllerCollection.Clear();
            foreach (HRegisterEntry entry in HVariables.RegisterTable
                .Where(x => IsControllerEntry(x))
                .Where(x => !RegisterDisableService.IsDisabledForActive(x.SdoIndex, x.SdoSubIndex)))
            {
                HardwareControllerCollection.Add(HardwareController.FromRegisterEntry(entry));
            }
        });
    }

    /// <summary>
    /// 本页展示的寄存器范围：
    /// · H01 组：驱动器硬件参数
    /// · H0D.09 ~ H0D.12：制动电阻选择 / 阻值 / 功率 / 制动单元动作电压
    /// </summary>
    private static bool IsControllerEntry(HRegisterEntry e)
    {
        if (e.HIndex.StartsWith("H01", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(e.HIndex, "H0D.09", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.HIndex, "H0D.10", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.HIndex, "H0D.11", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.HIndex, "H0D.12", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
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

    #region H0D 辅助命令

    [ObservableProperty]
    private string _h0DStatus = string.Empty;

    private void TriggerH0DCmd(string hIndex, string label)
    {
        var master = Master;
        var axis = Axis;
        if (master is null || axis is null) { H0DStatus = "未连接设备"; return; }
        var errs = new List<string>();
        HRegisterIO.SafeWriteHReg(master, axis, hIndex, 1, errs, hIndex);
        H0DStatus = errs.Count == 0 ? $"{label} 已下发" : $"{label} 失败: {string.Join(';', errs)}";
    }

    [RelayCommand] private void OnSoftReset()     => TriggerH0DCmd("H0D.01", "软件复位");
    [RelayCommand] private void OnFaultReset()    => TriggerH0DCmd("H0D.02", "故障复位");
    [RelayCommand] private void OnParamSave()     => TriggerH0DCmd("H0D.03", "参数存储");
    [RelayCommand] private void OnHomingTrigger() => TriggerH0DCmd("H0D.05", "主动回零");
    [RelayCommand] private void OnEmergencyStop() => TriggerH0DCmd("H0D.06", "紧急停机");

    #endregion
}