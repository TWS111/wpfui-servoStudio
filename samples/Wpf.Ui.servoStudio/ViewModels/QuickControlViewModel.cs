// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;
using Wpf.Ui.servoStudio.ViewModels.Motion;

namespace Wpf.Ui.servoStudio.ViewModels;

public partial class QuickControlViewModel : ViewModel
{
    private readonly DeviceAddViewModel _deviceVm;
    private readonly ControlViewModel _controlVm;
    private readonly MotionTypeViewModel _motionVm;

    private DispatcherTimer? _timer;
    private bool _isSampling;
    private bool _isAutoReading;
    private int _autoReadCounter;
    private const int AutoReadIntervalTicks = 5; // 200ms * 5 = 1s
    private const int DefaultLiveBufferCapacity = 1024;
    private sbyte? _lastServoModeRaw;
    private Cia402OperationMode? _lastBuiltMode;

    private static readonly string[] _palette =
    {
        "#FFEB3B", "#4FC3F7", "#FF8A80", "#69F0AE",
        "#B388FF", "#FFB74D", "#80DEEA", "#F48FB1",
    };

    public QuickControlViewModel(
        DeviceAddViewModel deviceVm,
        ControlViewModel controlVm,
        MotionTypeViewModel motionVm)
    {
        _deviceVm = deviceVm;
        _controlVm = controlVm;
        _motionVm = motionVm;

        foreach (ServoVariableItem item in ServoVariableCatalog.All)
        {
            FullCatalog.Add(item);
            item.PropertyChanged += OnCatalogItemPropertyChanged;
        }

        ApplyFilter();
        RebuildMotionParameters();
    }

    public ControlViewModel ControlVm => _controlVm;

    public MotionTypeViewModel MotionVm => _motionVm;

    #region 连接 / 当前模式

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionInfo = "设备未连接";

    [ObservableProperty]
    private string _currentModeName = "—";

    [ObservableProperty]
    private string _currentModeDescription = string.Empty;

    private void UpdateConnectionDisplay()
    {
        bool wasConnected = IsConnected;
        IsConnected = _deviceVm.IsAnyConnected && _deviceVm.ActiveAxis != null;
        ConnectionInfo = IsConnected
            ? $"已连接: {_deviceVm.EthernetSlaveNameInfo}"
            : "设备未连接";
        CurrentModeName = EffectiveCurrentMode().ToString();
        CurrentModeDescription = _motionVm.SelectedModeDescription;

        // Bug C 修复：如果之前因未连接而留下的状态文字，连接恢复时清除
        if (!wasConnected && IsConnected
            && MotionParameterStatusText != null
            && MotionParameterStatusText.StartsWith("设备未连接", StringComparison.Ordinal))
        {
            MotionParameterStatusText = $"当前模式 {CurrentModeName} 可快调 {MotionParameters.Count} 个 CiA 参数";
        }
    }

    private Cia402OperationMode EffectiveCurrentMode()
    {
        if (_lastServoModeRaw.HasValue)
        {
            Cia402OperationMode mode = (Cia402OperationMode)_lastServoModeRaw.Value;
            if (Enum.IsDefined(typeof(Cia402OperationMode), mode))
            {
                return mode;
            }
        }

        return _motionVm.SelectedOperationMode;
    }

    #endregion

    #region 回传字段

    [ObservableProperty]
    private string _statusWordHex = "0x0000";

    [ObservableProperty]
    private string _controlWordHex = "0x0000";

    [ObservableProperty]
    private string _parsedStateName = "—";

    [ObservableProperty]
    private string _modeDisplayName = "—";

    [ObservableProperty]
    private int _positionActual;

    [ObservableProperty]
    private int _velocityActual;

    [ObservableProperty]
    private short _torqueActual;

    [ObservableProperty]
    private ushort _errorCode;

    #endregion

    #region 当前模式 CiA 参数快调

    public ObservableCollection<QuickMotionParameter> MotionParameters { get; } = new();

    public int MotionParameterCount => MotionParameters.Count;

    [ObservableProperty]
    private bool _isMotionParameterBusy;

    [ObservableProperty]
    private bool _isAutoReadEnabled = true;

    [ObservableProperty]
    private string _motionParameterStatusText = "等待读取当前模式 CiA 参数";

    [RelayCommand]
    private async Task ApplySelectedMode()
    {
        await _motionVm.ApplyModeCommand.ExecuteAsync(null);
        MotionParameterStatusText = _motionVm.OperationStatusText;
        UpdateConnectionDisplay();
        RebuildMotionParameters();
        await ReadMotionParameters();
    }

    [RelayCommand]
    private async Task ReadMotionParameters()
    {
        if (!EnsureConnected("读取 CiA 参数"))
        {
            return;
        }

        QuickMotionParameter[] parameters = MotionParameters.ToArray();
        IsMotionParameterBusy = true;
        MotionParameterStatusText = $"正在读取 {CurrentModeName} CiA 参数...";

        try
        {
            IServoMaster master = _deviceVm.ActiveServoMaster;
            int slave = _deviceVm.ActiveAxis!.SlaveAddr;

            List<(QuickMotionParameter Parameter, double? Value, bool Success)> values = await Task.Run(() =>
            {
                List<(QuickMotionParameter Parameter, double? Value, bool Success)> result = [];
                foreach (QuickMotionParameter parameter in parameters)
                {
                    result.Add((parameter, ReadParameterValue(master, slave, parameter, out bool ok), ok));
                }

                return result;
            });

            int okCount = 0;
            foreach ((QuickMotionParameter parameter, double? value, bool ok) in values)
            {
                if (ok)
                {
                    parameter.Value = value;
                    okCount++;
                }
            }

            MotionParameterStatusText = okCount == parameters.Length
                ? $"{CurrentModeName} CiA 参数读取完成"
                : $"CiA 参数读取完成，成功 {okCount}/{parameters.Length}";
        }
        catch (Exception ex)
        {
            MotionParameterStatusText = $"CiA 参数读取异常: {ex.Message}";
        }
        finally
        {
            IsMotionParameterBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyMotionParameters()
    {
        if (!EnsureConnected("下发 CiA 参数"))
        {
            return;
        }

        QuickMotionParameter[] parameters = MotionParameters.ToArray();
        IsMotionParameterBusy = true;
        MotionParameterStatusText = $"正在下发 {CurrentModeName} CiA 参数...";

        try
        {
            IServoMaster master = _deviceVm.ActiveServoMaster;
            int slave = _deviceVm.ActiveAxis!.SlaveAddr;

            List<string> errors = await Task.Run(() =>
            {
                List<string> result = [];
                foreach (QuickMotionParameter parameter in parameters)
                {
                    if (!WriteParameterValue(master, slave, parameter))
                    {
                        result.Add(parameter.AddressDisplay);
                    }
                }

                return result;
            });

            MotionParameterStatusText = errors.Count == 0
                ? $"{CurrentModeName} CiA 参数下发成功"
                : $"部分 CiA 参数写入失败: {string.Join("; ", errors)}";
        }
        catch (Exception ex)
        {
            MotionParameterStatusText = $"CiA 参数下发异常: {ex.Message}";
        }
        finally
        {
            IsMotionParameterBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyMotionParameter(QuickMotionParameter parameter)
    {
        if (!EnsureConnected("下发 CiA 参数"))
        {
            return;
        }

        IsMotionParameterBusy = true;
        MotionParameterStatusText = $"正在下发 {parameter.DisplayName}...";

        try
        {
            IServoMaster master = _deviceVm.ActiveServoMaster;
            int slave = _deviceVm.ActiveAxis!.SlaveAddr;
            bool ok = await Task.Run(() => WriteParameterValue(master, slave, parameter));
            MotionParameterStatusText = ok
                ? $"{parameter.DisplayName} 写入成功"
                : $"{parameter.DisplayName} 写入失败 ({parameter.AddressDisplay})";
        }
        catch (Exception ex)
        {
            MotionParameterStatusText = $"{parameter.DisplayName} 写入异常: {ex.Message}";
        }
        finally
        {
            IsMotionParameterBusy = false;
        }
    }

    private bool EnsureConnected(string actionName)
    {
        UpdateConnectionDisplay();
        if (IsConnected)
        {
            return true;
        }

        MotionParameterStatusText = $"设备未连接，无法{actionName}";
        return false;
    }

    private void RebuildMotionParameters()
    {
        Cia402OperationMode mode = EffectiveCurrentMode();
        if (_lastBuiltMode == mode && MotionParameters.Count > 0)
        {
            return;
        }

        _lastBuiltMode = mode;
        MotionParameters.Clear();
        foreach (QuickMotionParameter parameter in BuildMotionParameterList(mode))
        {
            MotionParameters.Add(parameter);
        }

        OnPropertyChanged(nameof(MotionParameterCount));
        MotionParameterStatusText = IsConnected
            ? $"当前模式 {mode} 可快调 {MotionParameters.Count} 个 CiA 参数"
            : $"当前模式 {mode} 可快调 {MotionParameters.Count} 个 CiA 参数（设备未连接，仅离线预览）";
    }

    private static IEnumerable<QuickMotionParameter> BuildMotionParameterList(Cia402OperationMode mode)
    {
        return mode switch
        {
            Cia402OperationMode.ProfilePosition =>
            [
                P("目标位置", Cia402OdIndex.TargetPosition, 0, ServoVariableType.Int32, "pulse", -2_147_483_648, 2_147_483_647, 1, 1000),
                P("轮廓速度", Cia402OdIndex.ProfileVelocity, 0, ServoVariableType.UInt32, "pulse/s", 0, uint.MaxValue, 1, 1000),
                P("轮廓加速度", Cia402OdIndex.ProfileAcceleration, 0, ServoVariableType.UInt32, "pulse/s^2", 0, uint.MaxValue, 1, 1000),
                P("轮廓减速度", Cia402OdIndex.ProfileDeceleration, 0, ServoVariableType.UInt32, "pulse/s^2", 0, uint.MaxValue, 1, 1000),
                P("急停减速度", Cia402OdIndex.QuickStopDeceleration, 0, ServoVariableType.UInt32, "pulse/s^2", 0, uint.MaxValue, 1, 1000),
            ],
            Cia402OperationMode.Velocity =>
            [
                P("目标速度", Cia402OdIndex.TargetVelocity, 0, ServoVariableType.Int32, "rpm", -2_147_483_648, 2_147_483_647, 1, 100),
            ],
            Cia402OperationMode.ProfileVelocity =>
            [
                P("目标速度", Cia402OdIndex.TargetVelocity, 0, ServoVariableType.Int32, "rpm", -2_147_483_648, 2_147_483_647, 1, 100),
                P("轮廓加速度", Cia402OdIndex.ProfileAcceleration, 0, ServoVariableType.UInt32, "rpm/s", 0, uint.MaxValue, 1, 100),
                P("轮廓减速度", Cia402OdIndex.ProfileDeceleration, 0, ServoVariableType.UInt32, "rpm/s", 0, uint.MaxValue, 1, 100),
                P("急停减速度", Cia402OdIndex.QuickStopDeceleration, 0, ServoVariableType.UInt32, "rpm/s", 0, uint.MaxValue, 1, 100),
            ],
            Cia402OperationMode.ProfileTorque =>
            [
                P("目标转矩", Cia402OdIndex.TargetTorque, 0, ServoVariableType.Int16, "0.1%", short.MinValue, short.MaxValue, 1, 10),
                P("最大转矩", Cia402OdIndex.MaxTorque, 0, ServoVariableType.UInt16, "0.1%", 0, ushort.MaxValue, 1, 10),
                P("转矩斜率", Cia402OdIndex.TorqueSlope, 0, ServoVariableType.UInt32, "0.1%/s", 0, uint.MaxValue, 1, 100),
            ],
            Cia402OperationMode.Homing =>
            [
                P("回零方法", Cia402OdIndex.HomingMethod, 0, ServoVariableType.Int8, "", sbyte.MinValue, sbyte.MaxValue, 1, 1),
                P("搜索开关速度", Cia402OdIndex.HomingSpeeds, 1, ServoVariableType.UInt32, "rpm", 0, uint.MaxValue, 1, 100),
                P("搜索零点速度", Cia402OdIndex.HomingSpeeds, 2, ServoVariableType.UInt32, "rpm", 0, uint.MaxValue, 1, 100),
                P("回零加速度", Cia402OdIndex.HomingAcceleration, 0, ServoVariableType.UInt32, "rpm/s", 0, uint.MaxValue, 1, 100),
            ],
            Cia402OperationMode.InterpolatedPosition =>
            [
                P("插补周期", Cia402OdIndex.InterpolationTimePeriod, 1, ServoVariableType.UInt32, "ms", 0, uint.MaxValue, 1, 10),
            ],
            Cia402OperationMode.CyclicSynchronousPosition =>
            [
                P("目标位置", Cia402OdIndex.TargetPosition, 0, ServoVariableType.Int32, "pulse", -2_147_483_648, 2_147_483_647, 1, 1000),
                P("目标速度", Cia402OdIndex.TargetVelocity, 0, ServoVariableType.Int32, "rpm", -2_147_483_648, 2_147_483_647, 1, 100),
                P("位置前馈", Cia402OdIndex.PositionOffset, 0, ServoVariableType.Int32, "pulse", -2_147_483_648, 2_147_483_647, 1, 1000),
                P("速度前馈", Cia402OdIndex.VelocityOffset, 0, ServoVariableType.Int32, "rpm", -2_147_483_648, 2_147_483_647, 1, 100),
                P("转矩前馈", Cia402OdIndex.TorqueOffset, 0, ServoVariableType.Int16, "0.1%", short.MinValue, short.MaxValue, 1, 10),
                P("插补周期", Cia402OdIndex.InterpolationTimePeriod, 1, ServoVariableType.UInt32, "ms", 0, uint.MaxValue, 1, 10),
            ],
            Cia402OperationMode.CyclicSynchronousVelocity =>
            [
                P("目标速度", Cia402OdIndex.TargetVelocity, 0, ServoVariableType.Int32, "rpm", -2_147_483_648, 2_147_483_647, 1, 100),
                P("速度前馈", Cia402OdIndex.VelocityOffset, 0, ServoVariableType.Int32, "rpm", -2_147_483_648, 2_147_483_647, 1, 100),
                P("转矩前馈", Cia402OdIndex.TorqueOffset, 0, ServoVariableType.Int16, "0.1%", short.MinValue, short.MaxValue, 1, 10),
                P("插补周期", Cia402OdIndex.InterpolationTimePeriod, 1, ServoVariableType.UInt32, "ms", 0, uint.MaxValue, 1, 10),
            ],
            Cia402OperationMode.CyclicSynchronousTorque =>
            [
                P("目标转矩", Cia402OdIndex.TargetTorque, 0, ServoVariableType.Int16, "0.1%", short.MinValue, short.MaxValue, 1, 10),
                P("转矩前馈", Cia402OdIndex.TorqueOffset, 0, ServoVariableType.Int16, "0.1%", short.MinValue, short.MaxValue, 1, 10),
                P("插补周期", Cia402OdIndex.InterpolationTimePeriod, 1, ServoVariableType.UInt32, "ms", 0, uint.MaxValue, 1, 10),
            ],
            _ => [],
        };
    }

    private static QuickMotionParameter P(
        string name,
        ushort index,
        byte subIndex,
        ServoVariableType dataType,
        string unit,
        double min,
        double max,
        double smallChange,
        double largeChange)
    {
        return new QuickMotionParameter
        {
            DisplayName = name,
            Index = index,
            SubIndex = subIndex,
            DataType = dataType,
            Unit = unit,
            Minimum = min,
            Maximum = max,
            SmallChange = smallChange,
            LargeChange = largeChange,
        };
    }

    private static double? ReadParameterValue(IServoMaster master, int slave, QuickMotionParameter parameter, out bool ok)
    {
        try
        {
            ushort index = parameter.Index;
            byte subIndex = parameter.SubIndex;
            switch (parameter.DataType)
            {
                case ServoVariableType.Int8:
                    ok = master.TryReadSDO(slave, index, subIndex, out sbyte i8);
                    return ok ? i8 : null;
                case ServoVariableType.UInt8:
                    ok = master.TryReadSDO(slave, index, subIndex, out byte u8);
                    return ok ? u8 : null;
                case ServoVariableType.Int16:
                    ok = master.TryReadSDO(slave, index, subIndex, out short i16);
                    return ok ? i16 : null;
                case ServoVariableType.UInt16:
                    ok = master.TryReadSDO(slave, index, subIndex, out ushort u16);
                    return ok ? u16 : null;
                case ServoVariableType.Int32:
                    ok = master.TryReadSDO(slave, index, subIndex, out int i32);
                    return ok ? i32 : null;
                case ServoVariableType.UInt32:
                    ok = master.TryReadSDO(slave, index, subIndex, out uint u32);
                    return ok ? u32 : null;
                default:
                    ok = false;
                    return null;
            }
        }
        catch
        {
            ok = false;
            return null;
        }
    }

    private static bool WriteParameterValue(IServoMaster master, int slave, QuickMotionParameter parameter)
    {
        try
        {
            double value = Math.Round(parameter.Value ?? 0);
            value = Math.Clamp(value, parameter.Minimum, parameter.Maximum);
            ushort index = parameter.Index;
            byte subIndex = parameter.SubIndex;

            return parameter.DataType switch
            {
                ServoVariableType.Int8 => master.TryWriteSDO(slave, index, subIndex, (sbyte)value),
                ServoVariableType.UInt8 => master.TryWriteSDO(slave, index, subIndex, (byte)value),
                ServoVariableType.Int16 => master.TryWriteSDO(slave, index, subIndex, (short)value),
                ServoVariableType.UInt16 => master.TryWriteSDO(slave, index, subIndex, (ushort)value),
                ServoVariableType.Int32 => master.TryWriteSDO(slave, index, subIndex, (int)value),
                ServoVariableType.UInt32 => master.TryWriteSDO(slave, index, subIndex, (uint)value),
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region 变量目录 / 搜索 / 多选

    public ObservableCollection<ServoVariableItem> FullCatalog { get; } = new();

    public ObservableCollection<ServoVariableItem> FilteredCatalog { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredCatalog.Clear();
        string q = (SearchText ?? string.Empty).Trim();
        foreach (ServoVariableItem it in FullCatalog)
        {
            if (q.Length == 0
                || it.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || it.ShortName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || it.Group.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                FilteredCatalog.Add(it);
            }
        }
    }

    public ObservableCollection<QuickLiveChannel> ActiveChannels { get; } = new();

    public int TotalChannels => ActiveChannels.Count;

    public int TotalSamples => ActiveChannels.Sum(c => c.GetValidCount());

    [ObservableProperty]
    private int _liveDisplayCapacity = DefaultLiveBufferCapacity;

    [ObservableProperty]
    private int _liveOverflowModeIndex;

    partial void OnLiveDisplayCapacityChanged(int value)
    {
        int clamped = Math.Clamp(value, 10, 100000);
        if (clamped != value)
        {
            LiveDisplayCapacity = clamped;
            return;
        }

        ResetChannelBuffers();
    }

    partial void OnLiveOverflowModeIndexChanged(int value)
    {
        if (value is < 0 or > 1)
        {
            LiveOverflowModeIndex = 0;
            return;
        }

        ResetChannelBuffers();
    }

    public event EventHandler? ChannelsChanged;
    public event EventHandler? PlotTick;

    private QuickWaveOverflowMode OverflowMode => LiveOverflowModeIndex == 1
        ? QuickWaveOverflowMode.Sweep
        : QuickWaveOverflowMode.Rolling;

    private void OnCatalogItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ServoVariableItem.IsSelected))
        {
            return;
        }

        if (sender is not ServoVariableItem item)
        {
            return;
        }

        if (item.IsSelected)
        {
            AddChannel(item);
        }
        else
        {
            RemoveChannel(item);
        }
    }

    private void AddChannel(ServoVariableItem item)
    {
        if (ActiveChannels.Any(c => c.Variable == item))
        {
            return;
        }

        string color = _palette[ActiveChannels.Count % _palette.Length];
        ActiveChannels.Add(new QuickLiveChannel(item, color, LiveDisplayCapacity, OverflowMode));
        RefreshChannelIndexes();
        RaiseChannelSummaryChanged();
        ChannelsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveChannel(ServoVariableItem item)
    {
        QuickLiveChannel? ch = ActiveChannels.FirstOrDefault(c => c.Variable == item);
        if (ch != null && ActiveChannels.Remove(ch))
        {
            RefreshChannelIndexes();
            RaiseChannelSummaryChanged();
            ChannelsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ResetChannelBuffers()
    {
        foreach (QuickLiveChannel channel in ActiveChannels)
        {
            channel.ResetBuffer(LiveDisplayCapacity, OverflowMode);
        }

        RaiseChannelSummaryChanged();
        ChannelsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshChannelIndexes()
    {
        for (int i = 0; i < ActiveChannels.Count; i++)
        {
            ActiveChannels[i].ChannelIndex = i + 1;
        }
    }

    private void RaiseChannelSummaryChanged()
    {
        OnPropertyChanged(nameof(TotalChannels));
        OnPropertyChanged(nameof(TotalSamples));
    }

    [RelayCommand]
    private void ToggleChannel(QuickLiveChannel channel)
    {
        channel.IsVisible = !channel.IsVisible;
        ChannelsChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ShowAllChannels()
    {
        foreach (QuickLiveChannel channel in ActiveChannels)
        {
            channel.IsVisible = true;
        }

        ChannelsChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void HideAllChannels()
    {
        foreach (QuickLiveChannel channel in ActiveChannels)
        {
            channel.IsVisible = false;
        }

        ChannelsChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RemoveChannelByItem(ServoVariableItem item)
    {
        item.IsSelected = false;
    }

    [RelayCommand]
    private void ClearAllChannels()
    {
        foreach (ServoVariableItem it in FullCatalog.Where(i => i.IsSelected).ToList())
        {
            it.IsSelected = false;
        }
    }

    #endregion

    #region 快速控制命令（转发到 ControlViewModel）

    [RelayCommand]
    private Task QuickEnable() => _controlVm.CmdQuickEnableCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task DisableOperation() => _controlVm.CmdDisableOperationCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task QuickStop() => _controlVm.CmdQuickStopCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task DisableVoltage() => _controlVm.CmdDisableVoltageCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task FaultReset() => _controlVm.CmdFaultResetCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task Shutdown() => _controlVm.CmdShutdownCommand.ExecuteAsync(null);

    #endregion

    #region 生命周期与定时采样

    public override void OnNavigatedTo()
    {
        if (_timer != null)
        {
            return;
        }

        UpdateConnectionDisplay();
        RebuildMotionParameters();
        _motionVm.PropertyChanged += OnMotionPropertyChanged;
        _deviceVm.PropertyChanged += OnDevicePropertyChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
    }

    public override void OnNavigatedFrom()
    {
        _motionVm.PropertyChanged -= OnMotionPropertyChanged;
        _deviceVm.PropertyChanged -= OnDevicePropertyChanged;
        _timer?.Stop();
        _timer = null;
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceAddViewModel.IsAnyConnected)
            or nameof(DeviceAddViewModel.IsEthernetConnected)
            or nameof(DeviceAddViewModel.IsModbusConnected)
            or nameof(DeviceAddViewModel.IsCanopenConnected)
            or nameof(DeviceAddViewModel.EthernetSlaveNameInfo))
        {
            UpdateConnectionDisplay();
        }
    }

    private void OnMotionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MotionTypeViewModel.SelectedOperationMode))
        {
            CurrentModeName = EffectiveCurrentMode().ToString();
            RebuildMotionParameters();
        }

        if (e.PropertyName == nameof(MotionTypeViewModel.SelectedModeDescription))
        {
            CurrentModeDescription = _motionVm.SelectedModeDescription;
        }
    }

    private async Task TickAsync()
    {
        if (_isSampling)
        {
            return;
        }

        UpdateConnectionDisplay();
        if (!IsConnected)
        {
            return;
        }

        IServoMaster master = _deviceVm.ActiveServoMaster;
        int slave = _deviceVm.ActiveAxis!.SlaveAddr;
        QuickLiveChannel[] channels = ActiveChannels.ToArray();

        _isSampling = true;

        try
        {
            QuickSampleSnapshot snapshot = await Task.Run(() =>
            {
                QuickSampleSnapshot snap = new();

                if (master.TryReadSDO(slave, Cia402OdIndex.StatusWord, 0, out ushort sw))
                {
                    snap.StatusWord = sw;
                }

                if (master.TryReadSDO(slave, Cia402OdIndex.ControlWord, 0, out ushort cw))
                {
                    snap.ControlWord = cw;
                }

                if (master.TryReadSDO(slave, Cia402OdIndex.ModesOfOperationDisplay, 0, out sbyte md))
                {
                    snap.ModeDisplay = md;
                }

                if (master.TryReadSDO(slave, Cia402OdIndex.PositionActualValue, 0, out int pa))
                {
                    snap.PositionActual = pa;
                }

                if (master.TryReadSDO(slave, Cia402OdIndex.VelocityActualValue, 0, out int va))
                {
                    snap.VelocityActual = va;
                }

                if (master.TryReadSDO(slave, Cia402OdIndex.TorqueActualValue, 0, out short ta))
                {
                    snap.TorqueActual = ta;
                }

                if (master.TryReadSDO(slave, 0x603F, 0, out ushort ec))
                {
                    snap.ErrorCode = ec;
                }

                foreach (QuickLiveChannel ch in channels)
                {
                    ch.Sample(master, slave);
                }

                return snap;
            });

            ApplySnapshot(snapshot);
            foreach (QuickLiveChannel channel in channels)
            {
                channel.NotifySamplesChanged();
            }

            RaiseChannelSummaryChanged();
            PlotTick?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _isSampling = false;
        }

        // Bug B 修复：周期性自动读取快调参数（约 1s/次，并跳过正在被用户编辑的参数）
        if (IsAutoReadEnabled && !IsMotionParameterBusy && !_isAutoReading)
        {
            if (++_autoReadCounter >= AutoReadIntervalTicks)
            {
                _autoReadCounter = 0;
                _ = AutoReadMotionParametersAsync();
            }
        }
    }

    private async Task AutoReadMotionParametersAsync()
    {
        if (!IsConnected || _deviceVm.ActiveServoMaster is null || _deviceVm.ActiveAxis is null)
        {
            return;
        }

        QuickMotionParameter[] parameters = MotionParameters
            .Where(p => !p.IsLocked)
            .ToArray();
        if (parameters.Length == 0)
        {
            return;
        }

        _isAutoReading = true;
        try
        {
            IServoMaster master = _deviceVm.ActiveServoMaster;
            int slave = _deviceVm.ActiveAxis.SlaveAddr;

            List<(QuickMotionParameter Parameter, double? Value, bool Success)> values = await Task.Run(() =>
            {
                List<(QuickMotionParameter Parameter, double? Value, bool Success)> result = [];
                foreach (QuickMotionParameter parameter in parameters)
                {
                    result.Add((parameter, ReadParameterValue(master, slave, parameter, out bool ok), ok));
                }

                return result;
            });

            foreach ((QuickMotionParameter parameter, double? value, bool ok) in values)
            {
                if (ok && !parameter.IsLocked)
                {
                    parameter.Value = value;
                }
            }
        }
        catch
        {
            // 静默失败：下次 tick 重试
        }
        finally
        {
            _isAutoReading = false;
        }
    }

    private void ApplySnapshot(QuickSampleSnapshot snapshot)
    {
        if (snapshot.StatusWord.HasValue)
        {
            ushort sw = snapshot.StatusWord.Value;
            StatusWordHex = $"0x{sw:X4}";
            ParsedStateName = Cia402StatusMasks.ParseState(sw).ToString();
        }

        if (snapshot.ControlWord.HasValue)
        {
            ControlWordHex = $"0x{snapshot.ControlWord.Value:X4}";
        }

        if (snapshot.ModeDisplay.HasValue)
        {
            sbyte raw = snapshot.ModeDisplay.Value;
            ModeDisplayName = ((Cia402OperationMode)raw).ToString();

            // Bug A 修复：使用伺服实际运行模式重建参数列表
            if (_lastServoModeRaw != raw)
            {
                _lastServoModeRaw = raw;
                RebuildMotionParameters();
                CurrentModeName = EffectiveCurrentMode().ToString();
            }
        }

        if (snapshot.PositionActual.HasValue)
        {
            PositionActual = snapshot.PositionActual.Value;
        }

        if (snapshot.VelocityActual.HasValue)
        {
            VelocityActual = snapshot.VelocityActual.Value;
        }

        if (snapshot.TorqueActual.HasValue)
        {
            TorqueActual = snapshot.TorqueActual.Value;
        }

        if (snapshot.ErrorCode.HasValue)
        {
            ErrorCode = snapshot.ErrorCode.Value;
        }
    }

    private sealed class QuickSampleSnapshot
    {
        public ushort? StatusWord
        {
            get; set;
        }

        public ushort? ControlWord
        {
            get; set;
        }

        public sbyte? ModeDisplay
        {
            get; set;
        }

        public int? PositionActual
        {
            get; set;
        }

        public int? VelocityActual
        {
            get; set;
        }

        public short? TorqueActual
        {
            get; set;
        }

        public ushort? ErrorCode
        {
            get; set;
        }
    }

    #endregion
}

public enum QuickWaveOverflowMode
{
    Rolling,
    Sweep,
}

public sealed partial class QuickMotionParameter : ObservableObject
{
    public string DisplayName { get; init; } = string.Empty;

    public ushort Index
    {
        get; init;
    }

    public byte SubIndex
    {
        get; init;
    }

    public ServoVariableType DataType
    {
        get; init;
    }

    public string Unit { get; init; } = string.Empty;

    public double Minimum
    {
        get; init;
    }

    public double Maximum
    {
        get; init;
    }

    public double SmallChange { get; init; } = 1;

    public double LargeChange { get; init; } = 10;

    [ObservableProperty]
    private double? _value;

    /// <summary>
    /// 用户正在编辑该参数时为 true，周期自动读取将跳过。
    /// </summary>
    [ObservableProperty]
    private bool _isLocked;

    public string AddressDisplay => SubIndex == 0
        ? $"0x{Index:X4}"
        : $"0x{Index:X4}:{SubIndex:X2}";

    public string TypeLabel => DataType.ToString();
}

public sealed partial class QuickLiveChannel : ObservableObject
{
    private readonly object _gate = new();
    private int _capacity;
    private int _writeIndex;
    private QuickWaveOverflowMode _overflowMode;

    public ServoVariableItem Variable
    {
        get;
    }

    public string ColorHex
    {
        get;
    }

    public double[] Buffer
    {
        get; private set;
    }

    [ObservableProperty]
    private int _channelIndex;

    [ObservableProperty]
    private bool _isVisible = true;

    public int ValidCount
    {
        get; private set;
    }

    public string ChannelLabel => $"CH{ChannelIndex}";

    public string DisplayLabel => Variable.Name;

    public string Group => Variable.Group;

    public int Count => GetValidCount();

    public QuickLiveChannel(ServoVariableItem variable, string colorHex, int capacity, QuickWaveOverflowMode overflowMode)
    {
        Variable = variable;
        ColorHex = colorHex;
        _capacity = capacity;
        _overflowMode = overflowMode;
        Buffer = new double[capacity];
        Array.Fill(Buffer, double.NaN);
    }

    partial void OnChannelIndexChanged(int value) => OnPropertyChanged(nameof(ChannelLabel));

    public int GetValidCount()
    {
        lock (_gate)
        {
            return ValidCount;
        }
    }

    public void ResetBuffer(int capacity, QuickWaveOverflowMode overflowMode)
    {
        lock (_gate)
        {
            _capacity = capacity;
            _overflowMode = overflowMode;
            _writeIndex = 0;
            ValidCount = 0;
            Buffer = new double[capacity];
            Array.Fill(Buffer, double.NaN);
        }

        NotifySamplesChanged();
    }

    public void NotifySamplesChanged()
    {
        OnPropertyChanged(nameof(Count));
    }

    public void Sample(IServoMaster master, int slave)
    {
        double val = ReadValue(master, slave);
        if (double.IsNaN(val))
        {
            return;
        }

        lock (_gate)
        {
            if (_overflowMode == QuickWaveOverflowMode.Sweep)
            {
                Buffer[_writeIndex] = val;
                _writeIndex = (_writeIndex + 1) % _capacity;
                if (ValidCount < _capacity)
                {
                    ValidCount++;
                }
                else
                {
                    Buffer[_writeIndex] = double.NaN;
                }

                return;
            }

            if (ValidCount < _capacity)
            {
                Buffer[ValidCount++] = val;
            }
            else
            {
                Array.Copy(Buffer, 1, Buffer, 0, _capacity - 1);
                Buffer[_capacity - 1] = val;
            }
        }
    }

    private double ReadValue(IServoMaster master, int slave)
    {
        ushort idx = Variable.Index;
        byte sub = Variable.SubIndex;

        try
        {
            switch (Variable.DataType)
            {
                case ServoVariableType.Int8:
                    return master.TryReadSDO(slave, idx, sub, out sbyte v8) ? v8 : double.NaN;
                case ServoVariableType.UInt8:

                    return master.TryReadSDO(slave, idx, sub, out byte u8) ? u8 : double.NaN;
                case ServoVariableType.Int16:
                    return master.TryReadSDO(slave, idx, sub, out short v16) ? v16 : double.NaN;
                case ServoVariableType.UInt16:
                    return master.TryReadSDO(slave, idx, sub, out ushort u16) ? u16 : double.NaN;
                case ServoVariableType.Int32:
                    return master.TryReadSDO(slave, idx, sub, out int v32) ? v32 : double.NaN;
                case ServoVariableType.UInt32:
                    return master.TryReadSDO(slave, idx, sub, out uint u32) ? u32 : double.NaN;
                default:
                    return double.NaN;
            }
        }
        catch
        {
            return double.NaN;
        }
    }
}