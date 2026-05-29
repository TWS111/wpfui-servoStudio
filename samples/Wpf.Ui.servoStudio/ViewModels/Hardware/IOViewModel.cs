// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using Core.Net.EtherCAT;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.Services;
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

    #region 友好配置 (DI/DO 功能选择 + 极性)

    /// <summary>
    /// DI/DO 常用功能码项，参考汇川 SV680 H04 组手册。<br/>
    /// 自定义功能码可在下方 DataGrid 直接填写 H04.00~05 的数字。
    /// </summary>
    public sealed record IoFunctionItem(int Code, string DisplayName)
    {
        public override string ToString() => $"[{Code:D2}] {DisplayName}";
    }

    /// <summary>DI 可选功能码列表（通用项）。</summary>
    public IReadOnlyList<IoFunctionItem> DiFunctionItems { get; } = new List<IoFunctionItem>
    {
        new(0, "未使用"),
        new(1, "伺服使能 (S-ON)"),
        new(2, "故障复位"),
        new(3, "增益切换"),
        new(4, "正向限位 (P-OT)"),
        new(5, "反向限位 (N-OT)"),
        new(6, "原点开关"),
        new(7, "急停 (E-Stop)"),
        new(8, "JOG 正向"),
        new(9, "JOG 反向"),
        new(10, "电机零位锁定"),
        new(11, "外部触发回零"),
    };

    /// <summary>DO 可选功能码列表（通用项）。</summary>
    public IReadOnlyList<IoFunctionItem> DoFunctionItems { get; } = new List<IoFunctionItem>
    {
        new(0, "未使用"),
        new(1, "伺服就绪"),
        new(2, "电机旋转"),
        new(3, "零速钳位中"),
        new(4, "速度到达"),
        new(5, "定位完成"),
        new(6, "定位接近"),
        new(7, "故障输出"),
        new(8, "报警输出"),
        new(9, "原点回归完成"),
        new(10, "转矩到达"),
        new(11, "制动器输出"),
    };

    [ObservableProperty] private int _di1Func;
    [ObservableProperty] private int _di2Func;
    [ObservableProperty] private int _di3Func;
    [ObservableProperty] private int _di4Func;
    [ObservableProperty] private int _do1Func;
    [ObservableProperty] private int _do2Func;

    [ObservableProperty] private bool _di1PolarityHigh = true;
    [ObservableProperty] private bool _di2PolarityHigh = true;
    [ObservableProperty] private bool _di3PolarityHigh = true;
    [ObservableProperty] private bool _di4PolarityHigh = true;
    [ObservableProperty] private bool _do1PolarityHigh = true;
    [ObservableProperty] private bool _do2PolarityHigh = true;

    /// <summary>true 时 setter 不再回写到 controller，避免循环触发。</summary>
    private bool _suppressFriendlySync;

    partial void OnDi1FuncChanged(int value) => WriteFuncToCtl("H04.00", value);
    partial void OnDi2FuncChanged(int value) => WriteFuncToCtl("H04.01", value);
    partial void OnDi3FuncChanged(int value) => WriteFuncToCtl("H04.02", value);
    partial void OnDi4FuncChanged(int value) => WriteFuncToCtl("H04.03", value);
    partial void OnDo1FuncChanged(int value) => WriteFuncToCtl("H04.04", value);
    partial void OnDo2FuncChanged(int value) => WriteFuncToCtl("H04.05", value);

    partial void OnDi1PolarityHighChanged(bool value) => WritePolarityBit("H04.07", 0, value);
    partial void OnDi2PolarityHighChanged(bool value) => WritePolarityBit("H04.07", 1, value);
    partial void OnDi3PolarityHighChanged(bool value) => WritePolarityBit("H04.07", 2, value);
    partial void OnDi4PolarityHighChanged(bool value) => WritePolarityBit("H04.07", 3, value);
    partial void OnDo1PolarityHighChanged(bool value) => WritePolarityBit("H04.08", 0, value);
    partial void OnDo2PolarityHighChanged(bool value) => WritePolarityBit("H04.08", 1, value);

    private HardwareController? FindCtl(string hIndex)
        => HardwareControllerCollection.FirstOrDefault(c => c.Index == hIndex);

    private void WriteFuncToCtl(string hIndex, int value)
    {
        if (_suppressFriendlySync) return;
        HardwareController? ctl = FindCtl(hIndex);
        if (ctl == null) return;
        string newText = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (ctl.Value != newText) ctl.Value = newText;
    }

    private void WritePolarityBit(string hIndex, int bit, bool high)
    {
        if (_suppressFriendlySync) return;
        HardwareController? ctl = FindCtl(hIndex);
        if (ctl == null) return;

        ushort current = 0;
        _ = ushort.TryParse(ctl.Value, out current);
        ushort mask = (ushort)(1 << bit);
        ushort updated = high
            ? (ushort)(current | mask)
            : (ushort)(current & ~mask);
        if (current != updated)
            ctl.Value = updated.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 根据 HardwareControllerCollection 当前值刷新 6 个功能码 + 6 个极性属性。
    /// </summary>
    private void SyncFriendlyFromCtls()
    {
        _suppressFriendlySync = true;
        try
        {
            Di1Func = ParseFuncCode("H04.00");
            Di2Func = ParseFuncCode("H04.01");
            Di3Func = ParseFuncCode("H04.02");
            Di4Func = ParseFuncCode("H04.03");
            Do1Func = ParseFuncCode("H04.04");
            Do2Func = ParseFuncCode("H04.05");

            ushort diMask = ParseMask("H04.07");
            Di1PolarityHigh = (diMask & 0x01) != 0;
            Di2PolarityHigh = (diMask & 0x02) != 0;
            Di3PolarityHigh = (diMask & 0x04) != 0;
            Di4PolarityHigh = (diMask & 0x08) != 0;
            ushort doMask = ParseMask("H04.08");
            Do1PolarityHigh = (doMask & 0x01) != 0;
            Do2PolarityHigh = (doMask & 0x02) != 0;
        }
        finally
        {
            _suppressFriendlySync = false;
        }
    }

    private int ParseFuncCode(string hIndex)
    {
        HardwareController? ctl = FindCtl(hIndex);
        if (ctl == null) return 0;
        if (int.TryParse(ctl.Value, out int v)) return v;
        return 0;
    }

    private ushort ParseMask(string hIndex)
    {
        HardwareController? ctl = FindCtl(hIndex);
        if (ctl == null) return 0;
        return ushort.TryParse(ctl.Value, out ushort v) ? v : (ushort)0;
    }

    private void HookCtlValueChanged()
    {
        foreach (HardwareController c in HardwareControllerCollection)
        {
            c.PropertyChanged -= OnCtlPropertyChanged;
            c.PropertyChanged += OnCtlPropertyChanged;
        }
    }

    private void OnCtlPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HardwareController.Value)) return;
        if (sender is not HardwareController ctl) return;
        if (!ctl.Index.StartsWith("H04.", StringComparison.OrdinalIgnoreCase)) return;
        // 若是用户在 DataGrid 里改的，反向刷新友好属性
        SyncFriendlyFromCtls();
    }

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
            SyncFriendlyFromCtls();
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
            .Where(e => e.HIndex.StartsWith("H04", StringComparison.OrdinalIgnoreCase))
            .Where(e => !RegisterDisableService.IsDisabledForActive(e.SdoIndex, e.SdoSubIndex)))
        {
            HardwareController param = HardwareController.FromRegisterEntry(entry);
            HardwareControllerCollection.Add(param);
        }

        RegisterDisableService.Changed -= OnDisabledChanged;
        RegisterDisableService.Changed += OnDisabledChanged;

        HookCtlValueChanged();
        SyncFriendlyFromCtls();

        UpdateConnectionState();
    }

    private void OnDisabledChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            HardwareControllerCollection.Clear();
            foreach (HRegisterEntry entry in HVariables.RegisterTable
                .Where(x => x.HIndex.StartsWith("H04", StringComparison.OrdinalIgnoreCase))
                .Where(x => !RegisterDisableService.IsDisabledForActive(x.SdoIndex, x.SdoSubIndex)))
            {
                HardwareControllerCollection.Add(HardwareController.FromRegisterEntry(entry));
            }
            HookCtlValueChanged();
            SyncFriendlyFromCtls();
        });
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