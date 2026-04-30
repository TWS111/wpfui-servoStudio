// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Core.CANopen;
using Core.CANopen.CiA402;
using Core.Modbus;
using Core.Modbus.CiA402;
using Core.Net.EtherCAT;
using Core.Net.EtherCAT.SeedWork;
using Core.Net.EtherCAT.SeedWork.Interrop;
using RJCP.IO.Ports;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.Services;

namespace Wpf.Ui.servoStudio.ViewModels.DeviceSet;

public partial class DeviceAddViewModel(IContentDialogService contentDialogService, INavigationService navigationService) : ViewModel, IDisposable
{
    private static System.Threading.Timer portSearcher;
    private static System.Threading.Timer timer10ms;
    private string slaveState;
    private string[] _portNamesOld;
    private bool isPortNameChanged = false;
    private bool _isInitialized = false;
    private readonly EtherCATMaster ecatMaster = new EtherCATMaster();
    private EtherCATSlave_CiA402? _axis;

    // ===== Modbus RTU 协议栈（与 EtherCAT 平行、独立串口）=====
    private readonly ModbusRtuMaster _modbusMaster = new();
    private ModbusSlave_CiA402? _modbusAxis;

    // ===== CANopen 协议栈（基于可插拔 ICanBus；默认 slcan over SerialPortStream）=====
    private CanOpenMaster? _canOpenMaster;
    private CanOpenSlave_CiA402? _canOpenAxis;

    // 抽象适配器（缓存）：让所有页面 ViewModel 通过 IServoMaster/IServoAxis 透明访问当前协议栈
    private EtherCATServoMasterAdapter? _ecatServoAdapterCache;
    private EtherCATServoMasterAdapter EcatServoAdapter => _ecatServoAdapterCache ??= new(ecatMaster);

    public EtherCATMaster EcatMaster => ecatMaster;
    public EtherCATSlave_CiA402? CurrentAxis => _axis;
    public ModbusRtuMaster ModbusMaster => _modbusMaster;
    public ModbusSlave_CiA402? CurrentModbusAxis => _modbusAxis;
    public CanOpenMaster? CanOpenMaster => _canOpenMaster;
    public CanOpenSlave_CiA402? CurrentCanOpenAxis => _canOpenAxis;

    /// <summary>当前生效的协议栈主站（基于 <see cref="ActiveProtocol"/>）。EtherCAT/None → ECAT 适配器；Modbus → Modbus 主站；CANopen → CANopen 主站。</summary>
    public IServoMaster ActiveServoMaster => ActiveProtocol switch
    {
        ActiveProtocolStack.Modbus => _modbusMaster,
        ActiveProtocolStack.CANopen when _canOpenMaster is not null => _canOpenMaster,
        _ => EcatServoAdapter,
    };

    /// <summary>当前生效的从机；未连接任何协议时为 null。</summary>
    public IServoAxis? ActiveAxis => ActiveProtocol switch
    {
        ActiveProtocolStack.EtherCAT => _axis is null ? null : new EtherCATServoAxisAdapter(_axis),
        ActiveProtocolStack.Modbus => _modbusAxis,
        ActiveProtocolStack.CANopen => _canOpenAxis,
        _ => null,
    };

    /// <summary>EtherCAT、Modbus 或 CANopen（任一协议栈）的连接状态汇总。</summary>
    public bool IsAnyConnected => IsEthernetConnected || IsModbusConnected || IsCanopenConnected;

    private DispatcherTimer? _slaveStateTimer;

    [ObservableProperty]
    private Visibility _isEthernetLoadingVisible = Visibility.Hidden;

    [ObservableProperty]
    private string _dialogResultText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _comboBoxDataBitFamilies =
     [
        "8",
        "9",
    ];

    [ObservableProperty]
    private ObservableCollection<string> _comboBoxCheckBitFamilies =
     [
        "None",
        "Odd",
        "Even",
    ];

    [ObservableProperty]
    private ObservableCollection<string> _comboBoxStopBitFamilies =
     [
        "1",
        "1.5",
        "2",
    ];

    [ObservableProperty]
    private ObservableCollection<string> _comboBoxBaudFamilies =
     [
        "4800",
        "9600",
        "14400",
        "19200",
        "38400",
        "57600",
        "115200",
        "128000",
        "256000",
        "500000",
        "512000",
        "600000",
        "750000",
        "921600",
    ];

    [ObservableProperty]
    private ObservableCollection<string> _comboBoxPortNameFamilies = new ObservableCollection<string>();

    [ObservableProperty]
    private int _progressbarValue;

    [ObservableProperty]
    private bool _isLinkSucceed = false;

    [ObservableProperty]
    private bool _isLinkFailed = false;

    [ObservableProperty]
    private Visibility _isLinkSucceedVisible = Visibility.Hidden;

    [ObservableProperty]
    private Visibility _isLinkFailedVisible = Visibility.Hidden;

    [ObservableProperty]
    private int _comboBoxPortNameSelect;

    [ObservableProperty]
    private int _comboBoxCheckBitSelect;

    [ObservableProperty]
    private int _comboBoxDataBitSelect;

    [ObservableProperty]
    private int _comboBoxStopBitSelect;

    [ObservableProperty]
    private int _comboBoxBaudSelect;

    [ObservableProperty]
    private string? _isTestText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _ethernetDeviceNames = new ObservableCollection<string>();

    [ObservableProperty]
    private int _ethernetDeviceSelect;

    [ObservableProperty]
    private Visibility _isEthernetLinkSucceedVisible = Visibility.Hidden;

    [ObservableProperty]
    private Visibility _isEthernetLinkFailedVisible = Visibility.Hidden;

    [ObservableProperty]
    private string _ethernetStatusText = string.Empty;

    [ObservableProperty]
    private string _ethernetSlaveInfo = string.Empty;

    [ObservableProperty]
    private string _ethernetSlaveNameInfo = string.Empty;

    [ObservableProperty]
    private string _ethernetSlaveAddrInfo = string.Empty;

    [ObservableProperty]
    private string _ethernetSlaveStateInfo = string.Empty;

    [ObservableProperty]
    private string _selectedEthernetName = string.Empty;

    [ObservableProperty]
    private bool _isEthernetConnected = false;

    [ObservableProperty]
    private bool _isBusy = false;

    // 网络适配器描述 → 以太网名称的映射
    private readonly Dictionary<string, string> _adapterDescToName = new();

    // 虚拟/非物理网卡的常见关键词（不区分大小写匹配）
    private static readonly string[] _virtualNicKeywords =
    [
        // 虚拟化 / 容器
        "hyper-v", "virtual", "vmware", "vmnet", "vethernet",
        "docker", "wsl", "vEthernet",
        // VPN / 隧道
        "vpn", "tap-windows", "tunneling", "teredo", "isatap",
        // 蓝牙 / 无线
        "bluetooth", "bt network",
        // 抓包 / 调试
        "loopback", "pseudo", "npcap", "wireshark",
        // 内核调试
        "kdnic",
    ];

    // 子设备 / 中间层驱动关键词（过滤 WFP、QoS 等非母设备）
    private static readonly string[] _childDeviceKeywords =
    [
        "wfp", "qos", "filter", "lightweight", "light weight",
        "miniport", "multiplexor", "bridge", "packet scheduler",
        "native mac", "im platform",
    ];

    partial void OnIsEthernetConnectedChanged(bool value)
    {
        EthernetConfirmCommand.NotifyCanExecuteChanged();
        EthernetDisconnectCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsAnyConnected));
        OnPropertyChanged(nameof(ActiveAxis));
    }

    partial void OnIsBusyChanged(bool value)
    {
        EthernetConfirmCommand.NotifyCanExecuteChanged();
        EthernetDisconnectCommand.NotifyCanExecuteChanged();
    }

    // ───────────────── 协议栈互斥（EtherCAT / CANopen / Modbus）─────────────────

    /// <summary>
    /// 当前已连接的协议栈类型。三个协议栈同一时刻只允许一个处于连接态：
    /// 当某一栈连接成功后，其他栈的连接入口将被禁用，必须先断开当前栈才能切换。
    /// </summary>
    [ObservableProperty]
    private ActiveProtocolStack _activeProtocol = ActiveProtocolStack.None;

    partial void OnActiveProtocolChanged(ActiveProtocolStack value)
    {
        // 通知所有连接命令重新评估 CanExecute
        EthernetConfirmCommand.NotifyCanExecuteChanged();
        EthernetDisconnectCommand.NotifyCanExecuteChanged();
        DeviceConnectCommand.NotifyCanExecuteChanged();
        ModbusDisconnectCommand.NotifyCanExecuteChanged();
        CanOpenConnectCommand.NotifyCanExecuteChanged();
        CanOpenDisconnectCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ActiveServoMaster));
        OnPropertyChanged(nameof(ActiveAxis));
    }

    /// <summary>
    /// 当前协议栈是否允许新建“某一类型”的连接：仅当尚未连接，或当前连接的就是该类型时返回 true。
    /// </summary>
    private bool CanActivate(ActiveProtocolStack target)
        => ActiveProtocol == ActiveProtocolStack.None || ActiveProtocol == target;

    private static string ProtocolDisplayName(ActiveProtocolStack p) => p switch
    {
        ActiveProtocolStack.EtherCAT => "EtherCAT",
        ActiveProtocolStack.CANopen => "CANopen",
        ActiveProtocolStack.Modbus => "Modbus",
        _ => "无",
    };

    private bool CanEthernetConfirm() => !IsEthernetConnected && !IsBusy && CanActivate(ActiveProtocolStack.EtherCAT);

    private bool CanEthernetDisconnect() => IsEthernetConnected && !IsBusy;

    /// <summary>Modbus 串口连接命令的 CanExecute：当前未打开串口且当前激活协议非其他栈时可用。</summary>
    private bool CanDeviceConnect(Type type) => !_modbusMaster.IsOpen && CanActivate(ActiveProtocolStack.Modbus) && !IsBusy;

    private bool CanModbusDisconnect() => _modbusMaster.IsOpen && !IsBusy;

    /// <summary>CANopen 连接命令的 CanExecute：当前 CANopen 未连接且未被其他栈占用时可用。</summary>
    private bool CanCanOpenConnect() => !IsCanopenConnected && CanActivate(ActiveProtocolStack.CANopen) && !IsBusy;

    private bool CanCanOpenDisconnect() => IsCanopenConnected && !IsBusy;

    // ===== Modbus 状态字段（连接验证后填充）=====

    [ObservableProperty]
    private bool _isModbusConnected;

    [ObservableProperty]
    private string _modbusStatusText = string.Empty;

    [ObservableProperty]
    private string _modbusSlaveNameInfo = string.Empty;

    [ObservableProperty]
    private string _modbusSlaveAddrInfo = string.Empty;

    [ObservableProperty]
    private string _modbusFirmwareInfo = string.Empty;

    /// <summary>要连接的 Modbus 从机地址 (1~247)，默认 1。</summary>
    [ObservableProperty]
    private int _modbusSlaveAddress = 1;

    partial void OnIsModbusConnectedChanged(bool value)
    {
        DeviceConnectCommand.NotifyCanExecuteChanged();
        ModbusDisconnectCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsAnyConnected));
        OnPropertyChanged(nameof(ActiveAxis));
    }

    // ===== CANopen 状态字段（连接验证后填充） =====

    [ObservableProperty]
    private bool _isCanopenConnected;

    [ObservableProperty]
    private string _canopenStatusText = string.Empty;

    [ObservableProperty]
    private string _canopenSlaveNameInfo = string.Empty;

    [ObservableProperty]
    private string _canopenSlaveAddrInfo = string.Empty;

    [ObservableProperty]
    private string _canopenFirmwareInfo = string.Empty;

    /// <summary>要连接的 CANopen 节点地址 (1~127)，默认 1。</summary>
    [ObservableProperty]
    private int _canopenNodeId = 1;

    /// <summary>CANopen 比特率下拉选项。</summary>
    [ObservableProperty]
    private ObservableCollection<string> _comboBoxCanBitrateFamilies =
    [
        "10 kbps",
        "20 kbps",
        "50 kbps",
        "100 kbps",
        "125 kbps",
        "250 kbps",
        "500 kbps",
        "800 kbps",
        "1000 kbps",
    ];

    /// <summary>CANopen 比特率默认 500 kbps（索引 6）。</summary>
    [ObservableProperty]
    private int _comboBoxCanBitrateSelect = 6;

    private static CanBitrate CanBitrateFromIndex(int idx) => idx switch
    {
        0 => CanBitrate.Br10k,
        1 => CanBitrate.Br20k,
        2 => CanBitrate.Br50k,
        3 => CanBitrate.Br100k,
        4 => CanBitrate.Br125k,
        5 => CanBitrate.Br250k,
        6 => CanBitrate.Br500k,
        7 => CanBitrate.Br800k,
        8 => CanBitrate.Br1000k,
        _ => CanBitrate.Br500k,
    };

    partial void OnIsCanopenConnectedChanged(bool value)
    {
        CanOpenConnectCommand.NotifyCanExecuteChanged();
        CanOpenDisconnectCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsAnyConnected));
        OnPropertyChanged(nameof(ActiveServoMaster));
        OnPropertyChanged(nameof(ActiveAxis));
    }

    /// <summary>
    /// 刷新以太网设备列表（ComboBox 下拉时调用）<br/>
    /// ComboBox 显示网络适配器描述（Description），内部映射到以太网名称（Name）<br/>
    /// 仅保留物理以太网设备（RJ45 / USB 扩展坞），排除虚拟网卡
    /// </summary>
    public void RefreshEthernetDevices()
    {
        var nics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                       || nic.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet)
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
            // 排除已知的虚拟/非物理网卡
            .Where(nic => !_virtualNicKeywords.Any(keyword =>
                nic.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || nic.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            // 排除子设备（WFP、QoS 等中间层驱动），仅保留母设备
            .Where(nic => !_childDeviceKeywords.Any(keyword =>
                nic.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        EthernetDeviceNames.Clear();
        _adapterDescToName.Clear();

        foreach (NetworkInterface? nic in nics)
        {
            EthernetDeviceNames.Add(nic.Description);
            _adapterDescToName[nic.Description] = nic.Name;
        }

        if (EthernetDeviceNames.Count > 0)
        {
            EthernetDeviceSelect = 0;
        }

        UpdateSelectedEthernetName();
    }

    /// <summary>
    /// 当 ComboBox 选中项变化时，更新对应的以太网名称
    /// </summary>
    partial void OnEthernetDeviceSelectChanged(int value)
    {
        UpdateSelectedEthernetName();
    }

    private void UpdateSelectedEthernetName()
    {
        if (EthernetDeviceSelect >= 0 && EthernetDeviceSelect < EthernetDeviceNames.Count)
        {
            var desc = EthernetDeviceNames[EthernetDeviceSelect];
            SelectedEthernetName = _adapterDescToName.TryGetValue(desc, out var name) ? name : string.Empty;
        }
        else
        {
            SelectedEthernetName = string.Empty;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEthernetConfirm))]
    private async Task OnEthernetConfirm()
    {
        if (string.IsNullOrEmpty(SelectedEthernetName))
        {
            EthernetStatusText = "未找到可用的以太网设备";
            IsEthernetLinkFailedVisible = Visibility.Visible;
            IsEthernetLinkSucceedVisible = Visibility.Hidden;
            IsEthernetLoadingVisible = Visibility.Hidden;
            return;
        }

        // 在执行设备连接前，标记忙碌状态，显示加载动画，隐藏成功/失败标志
        IsBusy = true;
        IsEthernetLoadingVisible = Visibility.Visible;
        IsEthernetLinkSucceedVisible = Visibility.Hidden;
        IsEthernetLinkFailedVisible = Visibility.Hidden;
        EthernetStatusText = "正在连接设备...";
        EthernetSlaveInfo = string.Empty;
        EthernetSlaveNameInfo = string.Empty;
        EthernetSlaveAddrInfo = string.Empty;
        EthernetSlaveStateInfo = string.Empty;

        try
        {
            // 异步执行设备连接，避免阻塞 UI 线程
            (int Count, string SlaveName, int SlaveAddr, string SlaveState) result = await Task.Run(() =>
            {
                _axis = new EtherCATSlave_CiA402(ecatMaster, 1);
                int c = ecatMaster.StartActivity(SelectedEthernetName);
                string name = string.Empty;
                int addr = 0;
                string state = string.Empty;
                if (c > 0)
                {
                    name = _axis.SlaveName ?? string.Empty;
                    addr = _axis.SlaveAddr;
                    state = ecatMaster.ReadState(addr).ToString();
                }

                return (Count: c, SlaveName: name, SlaveAddr: addr, SlaveState: state);
            });

            slaveState = result.SlaveState;
            EthernetStatusText = $"已连接到 \"{SelectedEthernetName}\"";
            EthernetSlaveInfo = result.Count > 0 ? $"检测到 {result.Count} 个从站" : "未检测到任何从站";
            IsEthernetLinkSucceedVisible = Visibility.Visible;
            IsEthernetLinkFailedVisible = Visibility.Hidden;
            IsEthernetConnected = true;
            ActiveProtocol = ActiveProtocolStack.EtherCAT;

            if (result.Count > 0)
            {
                EthernetSlaveNameInfo = result.SlaveName;
                EthernetSlaveAddrInfo = result.SlaveAddr.ToString();
                EthernetSlaveStateInfo = result.SlaveState;
            }

            StartSlaveStatePolling();
        }
        catch (Exception ex)
        {
            EthernetStatusText = $"连接失败: {ex.Message}";
            AppData.AppLogViewModel.Log(Models.AppLogLevel.Error, Models.AppLogCategory.EtherCAT, "EtherCAT 连接失败", ex.Message);
            IsEthernetLinkFailedVisible = Visibility.Visible;
            IsEthernetLinkSucceedVisible = Visibility.Hidden;
            ecatMaster.StopActivity();
            ecatMaster.SdoQueue.SdoModelDic.Clear();
        }
        finally
        {
            // 连接完成后，隐藏加载动画，恢复忙碌状态
            IsEthernetLoadingVisible = Visibility.Hidden;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEthernetDisconnect))]
    private async Task OnEthernetDisconnect()
    {
        StopSlaveStatePolling();

        IsBusy = true;
        IsEthernetLoadingVisible = Visibility.Visible;
        IsEthernetLinkSucceedVisible = Visibility.Hidden;
        IsEthernetLinkFailedVisible = Visibility.Hidden;
        EthernetStatusText = "正在断开设备...";

        try
        {
            await Task.Run(() =>
            {
                if (_axis != null)
                {
                    ecatMaster.WriteState(_axis.SlaveAddr, SlaveState.Init);
                }

                ecatMaster.StopActivity();
                ecatMaster.SdoQueue.SdoModelDic.Clear();
            });

            _axis = null;
            EthernetStatusText = "设备已断开";
            EthernetSlaveInfo = string.Empty;
            EthernetSlaveNameInfo = string.Empty;
            EthernetSlaveAddrInfo = string.Empty;
            EthernetSlaveStateInfo = string.Empty;
            IsEthernetConnected = false;
            IsEthernetLinkSucceedVisible = Visibility.Hidden;
            if (ActiveProtocol == ActiveProtocolStack.EtherCAT)
                ActiveProtocol = ActiveProtocolStack.None;
        }
        catch (Exception ex)
        {
            EthernetStatusText = $"断开失败: {ex.Message}";
            AppData.AppLogViewModel.Log(Models.AppLogLevel.Error, Models.AppLogCategory.EtherCAT, "EtherCAT 断开失败", ex.Message);
            IsEthernetLinkFailedVisible = Visibility.Visible;
        }
        finally
        {
            IsEthernetLoadingVisible = Visibility.Hidden;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OnNmtSwitchToInit()
    {
        try
        {
            await Task.Run(() =>
            {
                if (_axis != null)
                {
                    ecatMaster.WriteState(_axis.SlaveAddr, SlaveState.Init);
                    EthernetSlaveStateInfo = ecatMaster.ReadState(_axis.SlaveAddr).ToString();
                }
            });
        }
        catch (Exception ex)
        {
            EthernetStatusText = $"切换状态失败: {ex.Message}";
            AppData.AppLogViewModel.Log(Models.AppLogLevel.Error, Models.AppLogCategory.EtherCAT, "NMT 切换到 Init 失败", ex.Message);
            IsEthernetLinkFailedVisible = Visibility.Visible;
        }
        finally
        {

        }
    }

    [RelayCommand]
    private async Task OnNmtSwitchToPreOP()
    {
        try
        {
            await Task.Run(() =>
            {
                if (_axis != null)
                {
                    ecatMaster.WriteState(_axis.SlaveAddr, SlaveState.PreOperational);
                    EthernetSlaveStateInfo = ecatMaster.ReadState(_axis.SlaveAddr).ToString();
                }
            });
        }
        catch (Exception ex)
        {
            EthernetStatusText = $"切换状态失败: {ex.Message}";
            AppData.AppLogViewModel.Log(Models.AppLogLevel.Error, Models.AppLogCategory.EtherCAT, "NMT 切换到 PreOP 失败", ex.Message);
            IsEthernetLinkFailedVisible = Visibility.Visible;
        }
        finally
        {

        }
    }

    [RelayCommand]
    private async Task OnNmtSwitchToOP()
    {
        try
        {
            await Task.Run(() =>
            {
                if (_axis != null)
                {
                    ecatMaster.WriteState(_axis.SlaveAddr, SlaveState.Operational);
                    EthernetSlaveStateInfo = ecatMaster.ReadState(_axis.SlaveAddr).ToString();
                }
            });
        }
        catch (Exception ex)
        {
            EthernetStatusText = $"切换状态失败: {ex.Message}";
            AppData.AppLogViewModel.Log(Models.AppLogLevel.Error, Models.AppLogCategory.EtherCAT, "NMT 切换到 OP 失败", ex.Message);
            IsEthernetLinkFailedVisible = Visibility.Visible;
        }
        finally
        {

        }
    }

    [RelayCommand]
    private async Task OnNmtSwitchToSafeOP()
    {
        try
        {
            await Task.Run(() =>
            {
                if (_axis != null)
                {
                    ecatMaster.WriteState(_axis.SlaveAddr, SlaveState.SafeOperational);
                    EthernetSlaveStateInfo = ecatMaster.ReadState(_axis.SlaveAddr).ToString();
                }
            });
        }
        catch (Exception ex)
        {
            EthernetStatusText = $"切换状态失败: {ex.Message}";
            AppData.AppLogViewModel.Log(Models.AppLogLevel.Error, Models.AppLogCategory.EtherCAT, "NMT 切换到 SafeOP 失败", ex.Message);
            IsEthernetLinkFailedVisible = Visibility.Visible;
        }
        finally
        {

        }
    }

    /// <summary>
    /// 启动从站状态轮询定时器（每 500ms 读取一次从站状态）
    /// </summary>
    internal void StartSlaveStatePolling()
    {
        StopSlaveStatePolling();

        _slaveStateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _slaveStateTimer.Tick += SlaveStateTimer_Tick;
        _slaveStateTimer.Start();
    }

    /// <summary>
    /// 停止从站状态轮询定时器
    /// </summary>
    internal void StopSlaveStatePolling()
    {
        if (_slaveStateTimer != null)
        {
            _slaveStateTimer.Stop();
            _slaveStateTimer.Tick -= SlaveStateTimer_Tick;
            _slaveStateTimer = null;
        }
    }

    /// <summary>
    /// 定时器回调：异步读取从站状态并刷新 UI
    /// </summary>
    private async void SlaveStateTimer_Tick(object? sender, EventArgs e)
    {
        if (_axis == null || !IsEthernetConnected || IsBusy)
        {
            return;
        }

        try
        {
            var addr = _axis.SlaveAddr;
            var state = await Task.Run(() => ecatMaster.ReadState(addr).ToString());
            EthernetSlaveStateInfo = state;
        }
        catch (Exception ex)
        {
            // 轮询读取失败时记录日志
            AppData.AppLogViewModel.Log(Models.AppLogLevel.Warning, Models.AppLogCategory.EtherCAT, "从站状态轮询异常", ex.Message);
        }
    }

    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            InitializeViewModel();
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeviceConnect))]
    private async Task OnDeviceConnect(Type type)
    {
        // 协议栈互斥：若其他栈（EtherCAT / CANopen）已处于连接状态，拒绝开启串口
        if (!CanActivate(ActiveProtocolStack.Modbus))
        {
            IsLinkFailed = true;
            IsLinkSucceed = false;
            IsLinkFailedVisible = Visibility.Visible;
            IsLinkSucceedVisible = Visibility.Hidden;
            ModbusStatusText = $"已被 {ProtocolDisplayName(ActiveProtocol)} 占用，请先断开";
            AppData.AppLogViewModel.Log(
                Models.AppLogLevel.Warning,
                Models.AppLogCategory.System,
                "Modbus 连接被阻止",
                $"其他协议栈 ({ProtocolDisplayName(ActiveProtocol)}) 已处于连接状态，请先断开后再试。");
            return;
        }

        if (ComboBoxPortNameFamilies.Count == 0 || ComboBoxPortNameSelect < 0)
        {
            IsLinkFailed = true;
            IsLinkSucceed = false;
            IsLinkFailedVisible = Visibility.Visible;
            IsLinkSucceedVisible = Visibility.Hidden;
            ModbusStatusText = "未检测到可用串口";
            return;
        }

        // 解析 UI 串口参数
        string portName = ComboBoxPortNameFamilies[ComboBoxPortNameSelect];
        int baud = Convert.ToInt32(ComboBoxBaudFamilies[ComboBoxBaudSelect]);
        int dataBits = Convert.ToInt32(ComboBoxDataBitFamilies[ComboBoxDataBitSelect]);
        Parity parity = ComboBoxCheckBitFamilies[ComboBoxCheckBitSelect] switch
        {
            "Odd" => Parity.Odd,
            "Even" => Parity.Even,
            _ => Parity.None,
        };
        StopBits stop = ComboBoxStopBitFamilies[ComboBoxStopBitSelect] switch
        {
            "1.5" => StopBits.One5,
            "2" => StopBits.Two,
            _ => StopBits.One,
        };
        int slaveAddr = Math.Clamp(ModbusSlaveAddress, 1, 247);

        IsBusy = true;
        ModbusStatusText = $"正在连接 {portName} @ {baud} (Slave {slaveAddr})...";
        IsLinkSucceedVisible = Visibility.Hidden;
        IsLinkFailedVisible = Visibility.Hidden;

        try
        {
            bool ok = await Task.Run(() =>
            {
                if (!_modbusMaster.Open(portName, baud, dataBits, parity, stop))
                    return false;

                _modbusAxis = new ModbusSlave_CiA402(_modbusMaster, slaveAddr);
                return _modbusAxis.ProbeIdentity();
            });

            if (!ok)
            {
                _modbusMaster.Close();
                _modbusAxis = null;
                IsLinkFailed = true;
                IsLinkSucceed = false;
                IsLinkFailedVisible = Visibility.Visible;
                IsLinkSucceedVisible = Visibility.Hidden;
                ModbusStatusText = _modbusMaster.IsOpen
                    ? $"未收到从机 {slaveAddr} 应答 ({_modbusMaster.LastException})"
                    : $"串口 {portName} 打开失败";
                AppData.AppLogViewModel.Log(
                    Models.AppLogLevel.Warning,
                    Models.AppLogCategory.System,
                    "Modbus 连接失败",
                    ModbusStatusText);
                return;
            }

            IsLinkSucceed = true;
            IsLinkFailed = false;
            IsLinkSucceedVisible = Visibility.Visible;
            IsLinkFailedVisible = Visibility.Hidden;
            IsModbusConnected = true;
            ActiveProtocol = ActiveProtocolStack.Modbus;

            ModbusSlaveNameInfo = _modbusAxis!.SlaveName ?? string.Empty;
            ModbusSlaveAddrInfo = _modbusAxis.SlaveAddr.ToString();
            ModbusFirmwareInfo = _modbusAxis.SoftwareVersion ?? string.Empty;
            ModbusStatusText = $"已连接：{portName} @ {baud}, 8{parity.ToString()[..1]}{(stop == StopBits.One5 ? "1.5" : ((int)stop).ToString())}";

            // 仅在导航类型有效时跳转，避免在新连接路径中误导航。
            if (type != null)
                _ = navigationService.Navigate(type);
        }
        catch (Exception ex)
        {
            try { _modbusMaster.Close(); } catch { /* ignore */ }
            _modbusAxis = null;
            IsLinkFailed = true;
            IsLinkSucceed = false;
            IsLinkFailedVisible = Visibility.Visible;
            IsLinkSucceedVisible = Visibility.Hidden;
            ModbusStatusText = $"连接异常: {ex.Message}";
            AppData.AppLogViewModel.Log(
                Models.AppLogLevel.Error,
                Models.AppLogCategory.System,
                "Modbus 连接异常",
                $"{portName}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanModbusDisconnect))]
    private async Task OnModbusDisconnect()
    {
        IsBusy = true;
        ModbusStatusText = "正在断开 Modbus...";
        try
        {
            await Task.Run(() => _modbusMaster.Close());
            _modbusAxis = null;
            IsModbusConnected = false;
            IsLinkSucceedVisible = Visibility.Hidden;
            IsLinkFailedVisible = Visibility.Hidden;
            ModbusSlaveNameInfo = string.Empty;
            ModbusSlaveAddrInfo = string.Empty;
            ModbusFirmwareInfo = string.Empty;
            ModbusStatusText = "Modbus 已断开";
            if (ActiveProtocol == ActiveProtocolStack.Modbus)
                ActiveProtocol = ActiveProtocolStack.None;
        }
        catch (Exception ex)
        {
            ModbusStatusText = $"断开失败: {ex.Message}";
            AppData.AppLogViewModel.Log(
                Models.AppLogLevel.Error,
                Models.AppLogCategory.System,
                "Modbus 断开异常",
                ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ═════════════════════════════ CANopen 连接命令 ═════════════════════════════

    /// <summary>
    /// 通过 slcan 串口适配器连接 CANopen 总线，并尝试与指定 nodeId 的 CiA402 从机握手。<br/>
    /// 串口选择沿用 Modbus 同款下拉（<see cref="ComboBoxPortNameFamilies"/>），节点地址使用
    /// <see cref="CanopenNodeId"/>，比特率使用 <see cref="ComboBoxCanBitrateSelect"/>。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCanOpenConnect))]
    private async Task OnCanOpenConnect()
    {
        if (!CanActivate(ActiveProtocolStack.CANopen))
        {
            CanopenStatusText = $"已被 {ProtocolDisplayName(ActiveProtocol)} 占用，请先断开";
            AppData.AppLogViewModel.Log(
                Models.AppLogLevel.Warning,
                Models.AppLogCategory.System,
                "CANopen 连接被阻止",
                $"其他协议栈 ({ProtocolDisplayName(ActiveProtocol)}) 已处于连接状态。");
            return;
        }

        if (ComboBoxPortNameFamilies.Count == 0 || ComboBoxPortNameSelect < 0)
        {
            CanopenStatusText = "未检测到可用串口（CAN-USB 适配器）";
            return;
        }

        string portName = ComboBoxPortNameFamilies[ComboBoxPortNameSelect];
        CanBitrate bitrate = CanBitrateFromIndex(ComboBoxCanBitrateSelect);
        int nodeId = Math.Clamp(CanopenNodeId, 1, 127);

        IsBusy = true;
        CanopenStatusText = $"正在打开 {portName} @ {(int)bitrate / 1000} kbps (Node {nodeId})...";

        try
        {
            bool ok = await Task.Run(() =>
            {
                var bus = new SerialCanBus(portName);
                if (!bus.Open(bitrate))
                {
                    bus.Dispose();
                    return false;
                }

                var master = new CanOpenMaster(bus);
                master.Start();

                var axis = new CanOpenSlave_CiA402(master, nodeId);
                bool alive = axis.ProbeIdentity();
                if (!alive)
                {
                    master.Dispose(); // 也会 Dispose bus
                    return false;
                }

                _canOpenMaster = master;
                _canOpenAxis = axis;
                return true;
            });

            if (!ok)
            {
                CanopenStatusText = $"未收到节点 {nodeId} 应答，请检查接线/比特率/nodeId";
                AppData.AppLogViewModel.Log(
                    Models.AppLogLevel.Warning,
                    Models.AppLogCategory.System,
                    "CANopen 连接失败",
                    CanopenStatusText);
                return;
            }

            IsCanopenConnected = true;
            ActiveProtocol = ActiveProtocolStack.CANopen;
            CanopenSlaveNameInfo = _canOpenAxis!.SlaveName ?? string.Empty;
            CanopenSlaveAddrInfo = _canOpenAxis.SlaveAddr.ToString();
            CanopenFirmwareInfo = _canOpenAxis.SoftwareVersion ?? string.Empty;
            CanopenStatusText = $"已连接：{portName} @ {(int)bitrate / 1000} kbps, Node {nodeId}";
        }
        catch (Exception ex)
        {
            CleanupCanOpen();
            CanopenStatusText = $"连接异常: {ex.Message}";
            AppData.AppLogViewModel.Log(
                Models.AppLogLevel.Error,
                Models.AppLogCategory.System,
                "CANopen 连接异常",
                $"{portName}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCanOpenDisconnect))]
    private async Task OnCanOpenDisconnect()
    {
        IsBusy = true;
        CanopenStatusText = "正在断开 CANopen...";
        try
        {
            await Task.Run(CleanupCanOpen);
            IsCanopenConnected = false;
            CanopenSlaveNameInfo = string.Empty;
            CanopenSlaveAddrInfo = string.Empty;
            CanopenFirmwareInfo = string.Empty;
            CanopenStatusText = "CANopen 已断开";
            if (ActiveProtocol == ActiveProtocolStack.CANopen)
                ActiveProtocol = ActiveProtocolStack.None;
        }
        catch (Exception ex)
        {
            CanopenStatusText = $"断开失败: {ex.Message}";
            AppData.AppLogViewModel.Log(
                Models.AppLogLevel.Error,
                Models.AppLogCategory.System,
                "CANopen 断开异常",
                ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CleanupCanOpen()
    {
        try { _canOpenMaster?.Dispose(); } catch { /* ignore */ }
        // SerialCanBus 由 CanOpenMaster.Dispose 一并释放
        _canOpenMaster = null;
        _canOpenAxis = null;
    }

    private void InitializeViewModel()
    {
        _isInitialized = true;
        _portNamesOld = vcom.GetPortNames();
        ComboBoxPortNameFamilies.Clear();
        for (int i = 0; i < _portNamesOld.Length; i++)
        {
            ComboBoxPortNameFamilies.Add(_portNamesOld[i]);
        }

        ComboBoxPortNameSelect = 0;
        portSearcher = new System.Threading.Timer(new TimerCallback(this.timerAutoSearch_Tick), null, 0, 15);
        _ = portSearcher.Change(0, 200);
        ComboBoxDataBitSelect = 0;
        ComboBoxStopBitSelect = 0;
        ComboBoxCheckBitSelect = 0;
        ComboBoxBaudSelect = 6;
        ComboBoxPortNameSelect = 0;

        LoadPersistedSelections();
    }

    // ===== 用户选择持久化（自动保存 / 加载到 UserSettings JSON） =====

    private bool _isLoadingPersistedSelections;

    private void LoadPersistedSelections()
    {
        _isLoadingPersistedSelections = true;
        try
        {
            UserSettings s = Services.UserSettingsService.Load();

            // 串口名：优先按名称匹配当前可用端口
            if (!string.IsNullOrEmpty(s.DeviceAdd_SerialPortName))
            {
                int idx = ComboBoxPortNameFamilies.IndexOf(s.DeviceAdd_SerialPortName);
                if (idx >= 0)
                    ComboBoxPortNameSelect = idx;
            }

            if (s.DeviceAdd_BaudIndex >= 0 && s.DeviceAdd_BaudIndex < ComboBoxBaudFamilies.Count)
                ComboBoxBaudSelect = s.DeviceAdd_BaudIndex;
            if (s.DeviceAdd_DataBitIndex >= 0 && s.DeviceAdd_DataBitIndex < ComboBoxDataBitFamilies.Count)
                ComboBoxDataBitSelect = s.DeviceAdd_DataBitIndex;
            if (s.DeviceAdd_CheckBitIndex >= 0 && s.DeviceAdd_CheckBitIndex < ComboBoxCheckBitFamilies.Count)
                ComboBoxCheckBitSelect = s.DeviceAdd_CheckBitIndex;
            if (s.DeviceAdd_StopBitIndex >= 0 && s.DeviceAdd_StopBitIndex < ComboBoxStopBitFamilies.Count)
                ComboBoxStopBitSelect = s.DeviceAdd_StopBitIndex;

            if (!string.IsNullOrEmpty(s.DeviceAdd_EthernetDeviceName))
                SelectedEthernetName = s.DeviceAdd_EthernetDeviceName;
        }
        finally
        {
            _isLoadingPersistedSelections = false;
        }
    }

    private void PersistSelections()
    {
        if (_isLoadingPersistedSelections)
            return;

        try
        {
            UserSettings s = Services.UserSettingsService.Load();

            // 保存串口名字符串而非索引（端口枚举顺序会变）
            if (ComboBoxPortNameFamilies.Count > 0
                && ComboBoxPortNameSelect >= 0
                && ComboBoxPortNameSelect < ComboBoxPortNameFamilies.Count)
            {
                s.DeviceAdd_SerialPortName = ComboBoxPortNameFamilies[ComboBoxPortNameSelect];
            }

            s.DeviceAdd_BaudIndex = ComboBoxBaudSelect;
            s.DeviceAdd_DataBitIndex = ComboBoxDataBitSelect;
            s.DeviceAdd_CheckBitIndex = ComboBoxCheckBitSelect;
            s.DeviceAdd_StopBitIndex = ComboBoxStopBitSelect;
            s.DeviceAdd_EthernetDeviceName = SelectedEthernetName ?? string.Empty;

            Services.UserSettingsService.Save(s);
        }
        catch
        {
            // 写入失败不影响业务
        }
    }

    partial void OnComboBoxPortNameSelectChanged(int value) => PersistSelections();
    partial void OnComboBoxBaudSelectChanged(int value) => PersistSelections();
    partial void OnComboBoxDataBitSelectChanged(int value) => PersistSelections();
    partial void OnComboBoxCheckBitSelectChanged(int value) => PersistSelections();
    partial void OnComboBoxStopBitSelectChanged(int value) => PersistSelections();
    partial void OnSelectedEthernetNameChanged(string value) => PersistSelections();

    private void timerAutoSearch_Tick(object state)
    {
        System.Windows.Application.Current.Dispatcher.Invoke((Action)(() =>
        {

            string[] _portNames = vcom.GetPortNames();
            string[] _portNameTemporary = _portNames.Distinct().ToArray();
            if (_portNameTemporary.Length > 0 && _portNameTemporary.Length != _portNamesOld.Length)
            {
                ComboBoxPortNameFamilies.Clear();
                for (int i = 0; i < _portNameTemporary.Length; i++)
                {
                    ComboBoxPortNameFamilies.Add(_portNameTemporary[i]);
                }

                // 优先恢复用户上次选择的端口
                var saved = Services.UserSettingsService.Load().DeviceAdd_SerialPortName;
                int idx = !string.IsNullOrEmpty(saved) ? ComboBoxPortNameFamilies.IndexOf(saved) : -1;
                ComboBoxPortNameSelect = idx >= 0 ? idx : 0;
            }
            else if (_portNameTemporary.Length > 0 && _portNameTemporary.Length != _portNamesOld.Length)
            {
                for (int i = 0; i < _portNameTemporary.Length; i++)
                {
                    if (_portNameTemporary[i] != _portNamesOld[i])
                    {
                        isPortNameChanged = true;
                    }
                }

                if (isPortNameChanged)
                {
                    ComboBoxPortNameFamilies.Clear();
                    for (int i = 0; i < _portNameTemporary.Length; i++)
                    {
                        ComboBoxPortNameFamilies.Add(_portNameTemporary[i]);
                    }

                    ComboBoxPortNameSelect = 0;
                    isPortNameChanged = false;
                }
            }

            _portNamesOld = vcom.GetPortNames();
        }));

    }
    private void timer10ms_Tick(object state)
    {
        ProgressbarValue++;
        if (ProgressbarValue >= 100)
        {
            ProgressbarValue = 100;
            timer10ms.Dispose();
        }
    }

    public new void Dispose()
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 当前激活的协议栈类型。三协议栈（EtherCAT / CANopen / Modbus）同时只允许一个处于连接状态。
/// </summary>
public enum ActiveProtocolStack
{
    None,
    EtherCAT,
    CANopen,
    Modbus,
}