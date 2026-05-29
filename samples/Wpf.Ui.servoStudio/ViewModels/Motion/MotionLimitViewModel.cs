// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using Core.Net.EtherCAT;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.Services;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels.Motion;

/// <summary>
/// 运动限制页 ViewModel —— 通过 SDO 读写 CiA402 标准限制对象与 H 变量保护参数。
/// 依赖 <see cref="DeviceAddViewModel"/> 提供的当前网络从站 (CurrentAxis) 与主站 (EcatMaster)。
/// </summary>
public partial class MotionLimitViewModel(DeviceAddViewModel deviceAddViewModel) : ViewModel
{
    private bool _isInitialized;

    #region EtherCAT 访问

    private IServoMaster Master => deviceAddViewModel.ActiveServoMaster;
    private IServoAxis? Axis => deviceAddViewModel.ActiveAxis;

    #endregion

    #region CiA402 限制对象索引常量

    // 极性 (UINT8) — bit7=位置极性, bit6=速度极性 (CiA402-2)
    private const ushort OdPolarity = 0x607E;

    // 转矩 / 电流限制
    private const ushort OdMaxTorque = 0x6072;            // UINT16, ‰ of rated
    private const ushort OdMaxCurrent = 0x6073;           // UINT16, ‰ of rated
    private const ushort OdMotorRatedTorque = 0x6076;     // UINT32, mNm (read-only)
    private const ushort OdPositiveTorqueLimit = 0x60E0;  // UINT16, ‰
    private const ushort OdNegativeTorqueLimit = 0x60E1;  // UINT16, ‰

    // 位置限制
    private const ushort OdSoftPositionLimit = 0x607D;    // INT32, sub1=Min, sub2=Max
    private const ushort OdPositionRangeLimit = 0x607B;   // INT32, sub1=Min, sub2=Max

    // 速度 / 加减速度限制
    private const ushort OdMaxProfileVelocity = 0x607F;   // UINT32
    private const ushort OdMaxMotorSpeed = 0x6080;        // UINT32
    private const ushort OdMaxAcceleration = 0x60C5;      // UINT32
    private const ushort OdMaxDeceleration = 0x60C6;      // UINT32

    // 单位换算（不是“因子”，是减速比与进给常数）
    private const ushort OdGearRatio = 0x6091;            // UINT32, sub1=Motor rev, sub2=Shaft rev
    private const ushort OdFeedConstant = 0x6092;         // UINT32, sub1=Feed,      sub2=Shaft rev

    #endregion

    #region 极性 (0x607E)

    [ObservableProperty]
    private byte _polarityRaw;

    /// <summary>位置极性（bit7）。</summary>
    public bool PolarityPosition
    {
        get => (PolarityRaw & 0x80) != 0;
        set
        {
            byte newValue = value
                ? (byte)(PolarityRaw | 0x80)
                : (byte)(PolarityRaw & ~0x80);
            if (newValue != PolarityRaw)
                PolarityRaw = newValue;
        }
    }

    /// <summary>速度极性（bit6）。</summary>
    public bool PolarityVelocity
    {
        get => (PolarityRaw & 0x40) != 0;
        set
        {
            byte newValue = value
                ? (byte)(PolarityRaw | 0x40)
                : (byte)(PolarityRaw & ~0x40);
            if (newValue != PolarityRaw)
                PolarityRaw = newValue;
        }
    }

    partial void OnPolarityRawChanged(byte value)
    {
        OnPropertyChanged(nameof(PolarityPosition));
        OnPropertyChanged(nameof(PolarityVelocity));
    }

    #endregion

    #region CiA402 限制属性（按对象字典物理类型建模）

    [ObservableProperty] private uint _maxProfileVelocity;
    [ObservableProperty] private uint _maxMotorSpeed;
    [ObservableProperty] private uint _maxAcceleration;
    [ObservableProperty] private uint _maxDeceleration;

    [ObservableProperty] private ushort _maxTorque;
    [ObservableProperty] private ushort _maxCurrent;
    [ObservableProperty] private uint _motorRatedTorque;          // 只读
    [ObservableProperty] private ushort _positiveTorqueLimit;
    [ObservableProperty] private ushort _negativeTorqueLimit;

    [ObservableProperty] private int _minSoftwarePosition;
    [ObservableProperty] private int _maxSoftwarePosition;
    [ObservableProperty] private int _positionRangeMin;
    [ObservableProperty] private int _positionRangeMax;

    [ObservableProperty] private uint _gearRatioMotorRev = 1;
    [ObservableProperty] private uint _gearRatioShaftRev = 1;
    [ObservableProperty] private uint _feedConstantFeed = 1;
    [ObservableProperty] private uint _feedConstantShaftRev = 1;

    #endregion

    #region H 变量限制条目

    /// <summary>
    /// 与运动限制相关的 HVariables 条目（电机/驱动器/转矩/位置环输出限幅等保护值）。
    /// </summary>
    public ObservableCollection<HRegisterLimitItem> HLimitItems { get; } = new();

    /// <summary>
    /// HVariables 中归类为 “运动限制 / 保护” 的条目 HIndex 白名单。
    /// </summary>
    private static readonly string[] LimitHIndices =
    {
        // 电机本体限制
        "H00.15", // 电机最大转速
        "H00.43", // 电机最大电流
        // 驱动器/硬件保护
        "H01.03", // 驱动器最大输出电流
        "H01.08", // 驱动器温度报警阈值
        "H01.09", // 过压保护值
        "H01.11", // 欠压保护值
        "H01.13", // 硬件 DAC 过流值
        "H01.14", // 软件过流值
        "H01.15", // 堵转保护最小转速
        // 内部转矩限制
        "H07.09", // 正内部转矩限制
        "H07.10", // 负内部转矩限制
        "H07.11", // 速度模式正向转矩限制
        "H07.12", // 速度模式负向转矩限制
        // 位置环速度输出限幅
        "H08.06", // 位置环速度正向输出限幅
        "H08.07", // 位置环速度反向输出限幅
        // 保护参数（H0A）
        "H0A.00", // 超速保护阈值
        "H0A.04", // 电机过载保护时间
        "H0A.06", // 位置跟踪误差过大保护阈值
        "H0A.10", // 驱动器过热保护温度
        "H0A.11", // 驱动器欠压故障阈值
        "H0A.12", // 驱动器过压故障阈值
        // 软件位置限位 / 超限动作（H0F.00 ~ H0F.06）
        "H0F.00", // 软件正限位使能
        "H0F.01", // 软件正限位（圈数高字）
        "H0F.02", // 软件正限位（单圈脉冲低字）
        "H0F.03", // 软件负限位使能
        "H0F.04", // 软件负限位（圈数高字）
        "H0F.05", // 软件负限位（单圈脉冲低字）
        "H0F.06", // 超限位处理方式
    };

    #endregion

    #region UI 状态

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _connectionInfo = "设备未连接";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _operationStatusText = "就绪";
    [ObservableProperty] private string _lastOperationText = string.Empty;

    #endregion

    #region 生命周期

    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
            Initialize();

        UpdateConnectionState();
    }

    public override void OnNavigatedFrom() { }

    private void Initialize()
    {
        _isInitialized = true;
        PopulateHLimitItems();
        // 厂家页禁用变更 / 协议栈切换时联动刷新本页 H 寄存器列表
        RegisterDisableService.Changed -= OnDisabledChanged;
        RegisterDisableService.Changed += OnDisabledChanged;
    }

    private void OnDisabledChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() => PopulateHLimitItems());
    }

    private void PopulateHLimitItems()
    {
        HLimitItems.Clear();
        foreach (string hIdx in LimitHIndices)
        {
            HRegisterEntry? entry = HVariables.FindByHIndex(hIdx);
            if (entry is null) continue;
            // 当前活动协议栈下被禁用的 H 寄存器一律不在本页显示
            if (RegisterDisableService.IsDisabledForActive(entry.SdoIndex, entry.SdoSubIndex)) continue;
            HLimitItems.Add(HRegisterLimitItem.FromEntry(entry));
        }
    }

    private void UpdateConnectionState()
    {
        IsConnected = deviceAddViewModel.IsAnyConnected && Axis != null;
        ConnectionInfo = IsConnected
            ? $"已连接：{deviceAddViewModel.EthernetSlaveNameInfo}（从站 {Axis!.SlaveAddr}）"
            : "设备未连接";
    }

    #endregion

    #region 命令 — 批量

    [RelayCommand]
    private async Task ReadAllParameters()
    {
        UpdateConnectionState();
        if (!IsConnected)
        {
            OperationStatusText = "设备未连接，请先在 “设备添加” 中连接 EtherCAT 从站";
            return;
        }

        IsBusy = true;
        OperationStatusText = "正在读取所有限制参数 ...";
        var errors = new List<string>();
        try
        {
            ushort addr = (ushort)Axis!.SlaveAddr;
            await Task.Run(() =>
            {
                ReadAllCiA402(addr, errors);
                ReadAllHLimits(addr, errors);
            });

            OperationStatusText = errors.Count == 0
                ? "全部参数读取成功"
                : $"读取完成，{errors.Count} 项失败";
            LastOperationText = errors.Count == 0
                ? $"{DateTime.Now:HH:mm:ss} 读取完成"
                : $"{DateTime.Now:HH:mm:ss} 失败项:\n  " + string.Join("\n  ", errors);

            AppData.AppLogViewModel.Log(
                AppLogLevel.Info,
                AppLogCategory.SDO,
                "运动限制参数读取",
                OperationStatusText);
        }
        catch (Exception ex)
        {
            OperationStatusText = $"读取异常: {ex.Message}";
            AppData.AppLogViewModel.Log(
                AppLogLevel.Error,
                AppLogCategory.SDO,
                "运动限制参数读取异常",
                ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyAllParameters()
    {
        UpdateConnectionState();
        if (!IsConnected)
        {
            OperationStatusText = "设备未连接，请先在 “设备添加” 中连接 EtherCAT 从站";
            return;
        }

        IsBusy = true;
        OperationStatusText = "正在下发所有限制参数 ...";
        var errors = new List<string>();
        try
        {
            ushort addr = (ushort)Axis!.SlaveAddr;
            await Task.Run(() =>
            {
                WriteAllCiA402(addr, errors);
                WriteAllHLimits(addr, errors);
            });

            OperationStatusText = errors.Count == 0
                ? "全部参数下发成功"
                : $"下发完成，{errors.Count} 项失败";
            LastOperationText = errors.Count == 0
                ? $"{DateTime.Now:HH:mm:ss} 下发完成"
                : $"{DateTime.Now:HH:mm:ss} 失败项:\n  " + string.Join("\n  ", errors);

            AppData.AppLogViewModel.Log(
                AppLogLevel.Info,
                AppLogCategory.SDO,
                "运动限制参数下发",
                OperationStatusText);
        }
        catch (Exception ex)
        {
            OperationStatusText = $"下发异常: {ex.Message}";
            AppData.AppLogViewModel.Log(
                AppLogLevel.Error,
                AppLogCategory.SDO,
                "运动限制参数下发异常",
                ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ResetToDefault()
    {
        // 仅恢复本地显示，不下发；用户确认后再点击 “下发全部”
        PolarityRaw = 0;
        MaxProfileVelocity = 3000;
        MaxMotorSpeed = 3000;
        MaxAcceleration = 100000;
        MaxDeceleration = 100000;
        MaxTorque = 1000;
        MaxCurrent = 1000;
        PositiveTorqueLimit = 1000;
        NegativeTorqueLimit = 1000;
        MinSoftwarePosition = int.MinValue;
        MaxSoftwarePosition = int.MaxValue;
        PositionRangeMin = int.MinValue;
        PositionRangeMax = int.MaxValue;
        GearRatioMotorRev = 1;
        GearRatioShaftRev = 1;
        FeedConstantFeed = 1;
        FeedConstantShaftRev = 1;

        foreach (HRegisterLimitItem item in HLimitItems)
        {
            item.CurrentValue = HRegisterLimitItem.ParseNumeric(item.DefaultValue, item.CurrentValue);
            item.StatusText = string.Empty;
        }

        OperationStatusText = "已恢复默认值（本地预览，未下发到驱动器）";
    }

    #endregion

    #region 命令 — 单条 H 项

    [RelayCommand]
    private async Task ReadSingleHItem(HRegisterLimitItem? item)
    {
        if (item == null)
            return;

        UpdateConnectionState();
        if (!IsConnected)
        {
            item.StatusText = "未连接";
            return;
        }

        try
        {
            ushort addr = (ushort)Axis!.SlaveAddr;
            ushort sdoIdx = item.SdoIndex;
            byte sdoSub = item.SdoSubIndex;
            ushort raw = 0;
            bool ok = await Task.Run(() => Master.TryReadSDO(addr, sdoIdx, sdoSub, out raw));
            if (ok)
            {
                item.CurrentValue = raw;
                item.StatusText = "读取成功";
            }
            else
            {
                item.StatusText = "读取失败";
            }
        }
        catch (Exception ex)
        {
            item.StatusText = $"异常: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task WriteSingleHItem(HRegisterLimitItem? item)
    {
        if (item == null || item.IsReadOnly)
            return;

        UpdateConnectionState();
        if (!IsConnected)
        {
            item.StatusText = "未连接";
            return;
        }

        try
        {
            ushort raw = ClampToUInt16(item.CurrentValue);
            ushort addr = (ushort)Axis!.SlaveAddr;
            ushort sdoIdx = item.SdoIndex;
            byte sdoSub = item.SdoSubIndex;
            bool ok = await Task.Run(() => Master.TryWriteSDO(addr, sdoIdx, sdoSub, raw));
            item.StatusText = ok ? "写入成功" : "写入失败";
        }
        catch (Exception ex)
        {
            item.StatusText = $"异常: {ex.Message}";
        }
    }

    #endregion

    #region SDO 批量读写实现

    private void ReadAllCiA402(ushort addr, List<string> errors)
    {
        if (TryRead(addr, OdPolarity, 0, out byte pol, errors, "0x607E 极性"))
            PolarityRaw = pol;

        if (TryRead(addr, OdMaxProfileVelocity, 0, out uint mpv, errors, "0x607F 最大轮廓速度"))
            MaxProfileVelocity = mpv;
        if (TryRead(addr, OdMaxMotorSpeed, 0, out uint mms, errors, "0x6080 最大电机速度"))
            MaxMotorSpeed = mms;
        if (TryRead(addr, OdMaxAcceleration, 0, out uint ma, errors, "0x60C5 最大加速度"))
            MaxAcceleration = ma;
        if (TryRead(addr, OdMaxDeceleration, 0, out uint md, errors, "0x60C6 最大减速度"))
            MaxDeceleration = md;

        if (TryRead(addr, OdMaxTorque, 0, out ushort mt, errors, "0x6072 最大转矩"))
            MaxTorque = mt;
        if (TryRead(addr, OdMaxCurrent, 0, out ushort mc, errors, "0x6073 最大电流"))
            MaxCurrent = mc;
        if (TryRead(addr, OdMotorRatedTorque, 0, out uint mrt, errors, "0x6076 电机额定转矩"))
            MotorRatedTorque = mrt;
        if (TryRead(addr, OdPositiveTorqueLimit, 0, out ushort ptl, errors, "0x60E0 正转矩限制"))
            PositiveTorqueLimit = ptl;
        if (TryRead(addr, OdNegativeTorqueLimit, 0, out ushort ntl, errors, "0x60E1 负转矩限制"))
            NegativeTorqueLimit = ntl;

        if (TryRead(addr, OdSoftPositionLimit, 1, out int spMin, errors, "0x607D-1 最小软件位置"))
            MinSoftwarePosition = spMin;
        if (TryRead(addr, OdSoftPositionLimit, 2, out int spMax, errors, "0x607D-2 最大软件位置"))
            MaxSoftwarePosition = spMax;
        if (TryRead(addr, OdPositionRangeLimit, 1, out int prMin, errors, "0x607B-1 位置范围下限"))
            PositionRangeMin = prMin;
        if (TryRead(addr, OdPositionRangeLimit, 2, out int prMax, errors, "0x607B-2 位置范围上限"))
            PositionRangeMax = prMax;

        if (TryRead(addr, OdGearRatio, 1, out uint grM, errors, "0x6091-1 减速比电机圈数"))
            GearRatioMotorRev = grM;
        if (TryRead(addr, OdGearRatio, 2, out uint grS, errors, "0x6091-2 减速比轴圈数"))
            GearRatioShaftRev = grS;
        if (TryRead(addr, OdFeedConstant, 1, out uint fcF, errors, "0x6092-1 进给常数 feed"))
            FeedConstantFeed = fcF;
        if (TryRead(addr, OdFeedConstant, 2, out uint fcS, errors, "0x6092-2 进给常数轴圈数"))
            FeedConstantShaftRev = fcS;
    }

    private void WriteAllCiA402(ushort addr, List<string> errors)
    {
        TryWrite(addr, OdPolarity, 0, PolarityRaw, errors, "0x607E 极性");
        TryWrite(addr, OdMaxProfileVelocity, 0, MaxProfileVelocity, errors, "0x607F 最大轮廓速度");
        TryWrite(addr, OdMaxMotorSpeed, 0, MaxMotorSpeed, errors, "0x6080 最大电机速度");
        TryWrite(addr, OdMaxAcceleration, 0, MaxAcceleration, errors, "0x60C5 最大加速度");
        TryWrite(addr, OdMaxDeceleration, 0, MaxDeceleration, errors, "0x60C6 最大减速度");

        TryWrite(addr, OdMaxTorque, 0, MaxTorque, errors, "0x6072 最大转矩");
        TryWrite(addr, OdMaxCurrent, 0, MaxCurrent, errors, "0x6073 最大电流");
        // 0x6076 电机额定转矩为只读，不下发
        TryWrite(addr, OdPositiveTorqueLimit, 0, PositiveTorqueLimit, errors, "0x60E0 正转矩限制");
        TryWrite(addr, OdNegativeTorqueLimit, 0, NegativeTorqueLimit, errors, "0x60E1 负转矩限制");

        TryWrite(addr, OdSoftPositionLimit, 1, MinSoftwarePosition, errors, "0x607D-1 最小软件位置");
        TryWrite(addr, OdSoftPositionLimit, 2, MaxSoftwarePosition, errors, "0x607D-2 最大软件位置");
        TryWrite(addr, OdPositionRangeLimit, 1, PositionRangeMin, errors, "0x607B-1 位置范围下限");
        TryWrite(addr, OdPositionRangeLimit, 2, PositionRangeMax, errors, "0x607B-2 位置范围上限");

        TryWrite(addr, OdGearRatio, 1, GearRatioMotorRev, errors, "0x6091-1 减速比电机圈数");
        TryWrite(addr, OdGearRatio, 2, GearRatioShaftRev, errors, "0x6091-2 减速比轴圈数");
        TryWrite(addr, OdFeedConstant, 1, FeedConstantFeed, errors, "0x6092-1 进给常数 feed");
        TryWrite(addr, OdFeedConstant, 2, FeedConstantShaftRev, errors, "0x6092-2 进给常数轴圈数");
    }

    private void ReadAllHLimits(ushort addr, List<string> errors)
    {
        foreach (HRegisterLimitItem item in HLimitItems)
        {
            try
            {
                ushort raw = 0;
                if (Master.TryReadSDO(addr, item.SdoIndex, item.SdoSubIndex, out raw))
                {
                    item.CurrentValue = raw;
                    item.StatusText = "已读取";
                }
                else
                {
                    item.StatusText = "读取失败";
                    errors.Add($"{item.HIndex} {item.ParameterName}");
                }
            }
            catch (Exception ex)
            {
                item.StatusText = $"异常: {ex.Message}";
                errors.Add($"{item.HIndex} {item.ParameterName}(异常)");
            }
        }
    }

    private void WriteAllHLimits(ushort addr, List<string> errors)
    {
        foreach (HRegisterLimitItem item in HLimitItems)
        {
            if (item.IsReadOnly)
                continue;

            try
            {
                ushort raw = ClampToUInt16(item.CurrentValue);
                if (Master.TryWriteSDO(addr, item.SdoIndex, item.SdoSubIndex, raw))
                {
                    item.StatusText = "已写入";
                }
                else
                {
                    item.StatusText = "写入失败";
                    errors.Add($"{item.HIndex} {item.ParameterName}");
                }
            }
            catch (Exception ex)
            {
                item.StatusText = $"异常: {ex.Message}";
                errors.Add($"{item.HIndex} {item.ParameterName}(异常)");
            }
        }
    }

    #endregion

    #region SDO 通用辅助

    private bool TryRead<T>(ushort addr, ushort idx, byte sub, out T value, List<string> errors, string name)
        where T : struct
    {
        try
        {
            if (Master.TryReadSDO(addr, idx, sub, out value))
                return true;

            errors.Add(name);
            return false;
        }
        catch (Exception ex)
        {
            value = default;
            errors.Add($"{name}(异常:{ex.Message})");
            return false;
        }
    }

    private void TryWrite<T>(ushort addr, ushort idx, byte sub, T value, List<string> errors, string name)
        where T : struct
    {
        try
        {
            if (!Master.TryWriteSDO(addr, idx, sub, value))
                errors.Add(name);
        }
        catch (Exception ex)
        {
            errors.Add($"{name}(异常:{ex.Message})");
        }
    }

    private static ushort ClampToUInt16(double value)
    {
        long rounded = (long)Math.Round(value);
        return (ushort)Math.Clamp(rounded, 0L, ushort.MaxValue);
    }

    #endregion
}

/// <summary>
/// H 变量运动限制条目（UI 绑定模型）。
/// </summary>
public partial class HRegisterLimitItem : ObservableObject
{
    public string HIndex { get; set; } = string.Empty;
    public string ParameterName { get; set; } = string.Empty;
    public string CommAddress { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public bool IsReadOnly { get; set; }
    public bool IsEditable => !IsReadOnly;
    public ushort SdoIndex { get; set; }
    public byte SdoSubIndex { get; set; }
    public string MinValue { get; set; } = "0";
    public string MaxValue { get; set; } = "65535";
    public string DefaultValue { get; set; } = "0";

    [ObservableProperty]
    private double _currentValue;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public double MinValueNumeric => ParseNumeric(MinValue, 0);
    public double MaxValueNumeric => ParseNumeric(MaxValue, ushort.MaxValue);

    public static HRegisterLimitItem FromEntry(HRegisterEntry entry) => new()
    {
        HIndex = entry.HIndex,
        ParameterName = entry.ParameterName,
        CommAddress = entry.CommAddress,
        GroupName = entry.GroupName,
        Unit = entry.Unit,
        IsReadOnly = entry.IsReadOnly,
        SdoIndex = entry.SdoIndex,
        SdoSubIndex = entry.SdoSubIndex,
        MinValue = entry.MinValue,
        MaxValue = entry.MaxValue,
        DefaultValue = entry.DefaultValue,
        CurrentValue = ParseNumeric(entry.DefaultValue, 0),
    };

    /// <summary>
    /// 兼容十进制 / 十六进制（"0x..."）/ "-" 占位符。
    /// </summary>
    public static double ParseNumeric(string str, double fallback)
    {
        if (string.IsNullOrWhiteSpace(str) || str == "-")
            return fallback;

        string s = str.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(
                s.Substring(2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out long hv)
                ? hv
                : fallback;
        }

        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double v)
            ? v
            : fallback;
    }
}
