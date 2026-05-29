// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Core.CANopen;
using Core.CANopen.Adapters;
using Core.CANopen.CiA402;
using Core.Modbus;
using Core.Modbus.CiA402;
using Core.Net.EtherCAT;
using Core.Net.EtherCAT.SeedWork;
using Core.Net.EtherCAT.SeedWork.Interrop;
using Core.Usb;
using RJCP.IO.Ports;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;
using Wpf.Ui.servoStudio.Core;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.Services;

namespace Wpf.Ui.servoStudio.ViewModels.DeviceSet;

/// <summary>通信丢失事件参数。</summary>
public sealed class CommLostEventArgs(string protocol, int consecutiveFailures) : EventArgs
{
    /// <summary>发生通信丢失的协议栈名称（"Modbus" / "CANopen" / "EtherCAT"）。</summary>
    public string Protocol { get; } = protocol;

    /// <summary>连续失败次数。</summary>
    public int ConsecutiveFailures { get; } = consecutiveFailures;
}

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
    private CancellationTokenSource? _modbusSlavePollingCts;
    private int _modbusSlaveProbeActive;
    private int _modbusCommLostCount;

    // ===== CANopen 协议栈（基于可插拔 ICanBus；默认 slcan over SerialPortStream）=====
    private CanOpenMaster? _canOpenMaster;
    private CanOpenSlave_CiA402? _canOpenAxis;
    private CancellationTokenSource? _canOpenSlavePollingCts;
    private int _canOpenSlaveProbeActive;
    private int _canOpenCommLostCount;
    private int _etherCatCommLostCount;

    /// <summary>
    /// 通信丢失事件：连续 <see cref="CommLostThreshold"/> 次探测失败后触发。<br/>
    /// 订阅方（如 ControlViewModel）应在收到此事件后执行软件急停。
    /// </summary>
    public static event EventHandler<CommLostEventArgs>? CommLost;

    /// <summary>连续探测失败多少次后触发 <see cref="CommLost"/> 事件。默认 3 次（约 6 秒）。</summary>
    public static int CommLostThreshold { get; set; } = 3;

    // ===== USB 协议栈（HPM 系列 MCU + ThreadX/USBX，与上述三栈"并行"且承载不同类数据）=====
    // 不参与 ActiveProtocolStack 互斥：USB 用于曲线拟合下发 / 自适应参数 / 高带宽遥测，
    // 与 EtherCAT/CANopen/Modbus 的寄存器访问可同时存在。
    private UsbMaster? _usbMaster;

    // 抽象适配器（缓存）：让所有页面 ViewModel 通过 IServoMaster/IServoAxis 透明访问当前协议栈
    private EtherCATServoMasterAdapter? _ecatServoAdapterCache;
    private EtherCATServoMasterAdapter EcatServoAdapter => _ecatServoAdapterCache ??= new(ecatMaster);

    public EtherCATMaster EcatMaster => ecatMaster;
    public EtherCATSlave_CiA402? CurrentAxis => _axis;
    public ModbusRtuMaster ModbusMaster => _modbusMaster;
    public ModbusSlave_CiA402? CurrentModbusAxis => _modbusAxis;
    public CanOpenMaster? CanOpenMaster => _canOpenMaster;
    public CanOpenSlave_CiA402? CurrentCanOpenAxis => _canOpenAxis;

    /// <summary>当前 USB 协议栈门面（与三大寄存器协议栈<b>并行独立</b>）。未连接时为 null。</summary>
    public UsbMaster? UsbMaster => _usbMaster;

    // ── 在线接收模式桥接 ──────────────────────────────────────────────────
    /// <summary>
    /// 由外部（DataViewViewModel）设置：返回当前在线接收开关是否已开启。<br/>
    /// Modbus 连接时若此委托返回 true，则跳过 ProbeIdentity 仅打开串口。
    /// </summary>
    public Func<bool>? IsLiveReceiveModeActive { get; set; }

    /// <summary>
    /// Modbus 以在线接收模式成功打开串口后触发（在后台线程，订阅者应自行 Dispatch 到 UI）。
    /// </summary>
    public event Action? LiveReceiveConnectionReady;
    // ────────────────────────────────────────────────────────────────────────

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

    /// <summary>任意协议栈（含 USB）连接状态汇总，供 UI 判断"无已连接设备"占位卡可见性。</summary>
    public bool IsAnyDeviceConnected => IsAnyConnected || IsUsbConnected;

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

    // ===== EtherCAT 多从站选择 =====

    /// <summary>连接成功后检测到的从站总数（0 = 未连接）。</summary>
    private int _etherCatSlaveCount;

    private bool _suppressEtherCatSlaveSelectChange;

    /// <summary>已检测到的从站列表项（"从站 1 (地址: 1)"、"从站 2 (地址: 2)" …），用于下拉选择控件。</summary>
    [ObservableProperty]
    private ObservableCollection<string> _etherCatSlaveList = [];

    /// <summary>当前选中的从站在 <see cref="EtherCatSlaveList"/> 中的索引（0-based）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEtherCatMultiSlaveVisible))]
    private int _etherCatSlaveSelect;

    /// <summary>当检测到多个 EtherCAT 从站时显示从站切换控件。</summary>
    public bool IsEtherCatMultiSlaveVisible => _etherCatSlaveCount > 1 && IsEthernetConnected;

    /// <summary>在已连接状态下切换当前操控的从站（重建 <see cref="_axis"/>）。</summary>
    partial void OnEtherCatSlaveSelectChanged(int value)
    {
        if (_suppressEtherCatSlaveSelectChange || !IsEthernetConnected || value < 0 || value >= _etherCatSlaveCount)
        {
            return;
        }

        int newAddr = value + 1;
        int previousSelect = _axis?.SlaveAddr > 0 ? _axis.SlaveAddr - 1 : 0;

        try
        {
            EtherCATSlave_CiA402 nextAxis = new(ecatMaster, newAddr);
            string nextSlaveName = string.Empty;

            try
            {
                nextSlaveName = nextAxis.SlaveName ?? string.Empty;
            }
            catch (Exception ex)
            {
                AppData.AppLogViewModel.Log(
                    Models.AppLogLevel.Warning,
                    Models.AppLogCategory.EtherCAT,
                    "切换从站名称读取失败",
                    ex.Message);
            }

            _axis = nextAxis;
            EthernetSlaveNameInfo = nextSlaveName;
            EthernetSlaveAddrInfo = newAddr.ToString();
            EthernetSlaveStateInfo = string.Empty;

            OnPropertyChanged(nameof(CurrentAxis));
            OnPropertyChanged(nameof(ActiveAxis));
            AppData.AppLogViewModel.Log(
                Models.AppLogLevel.Info,
                Models.AppLogCategory.EtherCAT,
                "EtherCAT 从站切换",
                $"当前控制从站已切换至地址 {newAddr}");
        }
        catch (Exception ex)
        {
            SetEtherCatSlaveSelectSilently(previousSelect, true);
            AppData.AppLogViewModel.Log(
                Models.AppLogLevel.Error,
                Models.AppLogCategory.EtherCAT,
                "EtherCAT 从站切换失败",
                ex.Message);
        }
    }

    private void ClearEtherCatSdoCache()
    {
        ecatMaster.SdoQueue?.SdoModelDic?.Clear();
    }

    private void SetEtherCatSlaveSelectSilently(int value, bool forceNotification = false)
    {
        _suppressEtherCatSlaveSelectChange = true;

        try
        {
            EtherCatSlaveSelect = value;

            if (forceNotification)
            {
                OnPropertyChanged(nameof(EtherCatSlaveSelect));
            }
        }
        finally
        {
            _suppressEtherCatSlaveSelectChange = false;
        }
    }

    partial void OnIsEthernetConnectedChanged(bool value)
    {
        EthernetConfirmCommand.NotifyCanExecuteChanged();
        EthernetDisconnectCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsAnyConnected));
        OnPropertyChanged(nameof(ActiveAxis));
        OnPropertyChanged(nameof(IsEtherCatMultiSlaveVisible));
    }

    partial void OnIsBusyChanged(bool value)
    {
        EthernetConfirmCommand.NotifyCanExecuteChanged();
        EthernetDisconnectCommand.NotifyCanExecuteChanged();
        DeviceConnectCommand.NotifyCanExecuteChanged();
        ModbusDisconnectCommand.NotifyCanExecuteChanged();
        QueryModbusSlaveCommand.NotifyCanExecuteChanged();
        CanOpenConnectCommand.NotifyCanExecuteChanged();
        CanOpenDisconnectCommand.NotifyCanExecuteChanged();
        QueryCanopenSlaveCommand.NotifyCanExecuteChanged();
        UsbConnectCommand.NotifyCanExecuteChanged();
        UsbDisconnectCommand.NotifyCanExecuteChanged();
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

        // 同步至 RegisterDisableService，使所有依赖"当前协议栈禁用集合"的页面（PID/运动限制/硬件等）
        // 在协议切换时自动重建/隐藏对应寄存器项。
        RegisterDisableService.ActiveStack = value switch
        {
            ActiveProtocolStack.EtherCAT => ProtocolStack.EtherCAT,
            ActiveProtocolStack.CANopen => ProtocolStack.CANopen,
            ActiveProtocolStack.Modbus => ProtocolStack.Modbus,
            _ => null,
        };
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

    private bool CanQueryModbusSlave() => IsModbusConnected && !IsBusy;

    /// <summary>CANopen 连接命令的 CanExecute：当前 CANopen 未连接且未被其他栈占用时可用。</summary>
    private bool CanCanOpenConnect() => !IsCanopenConnected && CanActivate(ActiveProtocolStack.CANopen) && !IsBusy;

    private bool CanCanOpenDisconnect() => IsCanopenConnected && !IsBusy;

    private bool CanQueryCanopenSlave() => IsCanopenConnected && !IsBusy;

    // ===== Modbus 状态字段（连接验证后填充）=====

    [ObservableProperty]
    private bool _isModbusConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModbusSlaveOnlineText))]
    private bool _isModbusSlaveOnline;

    public string ModbusSlaveOnlineText => IsModbusSlaveOnline ? "从站在线" : "从站离线";

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
        QueryModbusSlaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsAnyConnected));
        OnPropertyChanged(nameof(ActiveAxis));
    }

    partial void OnIsModbusSlaveOnlineChanged(bool value) => OnPropertyChanged(nameof(ActiveAxis));

    // ===== CANopen 状态字段（连接验证后填充） =====

    [ObservableProperty]
    private bool _isCanopenConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanopenSlaveOnlineText))]
    private bool _isCanopenSlaveOnline;

    public string CanopenSlaveOnlineText => IsCanopenSlaveOnline ? "从站在线" : "从站离线";

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

    /// <summary>CANopen 比特率下拉选项（按数值<b>由大到小</b>排列，匹配工程师从高速到低速的常用筛选习惯）。</summary>
    [ObservableProperty]
    private ObservableCollection<string> _comboBoxCanBitrateFamilies =
    [
        "1000 kbps",
        "800 kbps",
        "500 kbps",
        "250 kbps",
        "125 kbps",
        "100 kbps",
        "50 kbps",
        "20 kbps",
        "10 kbps",
    ];

    /// <summary>CANopen 比特率默认 500 kbps（递减排序下索引 2）。</summary>
    [ObservableProperty]
    private int _comboBoxCanBitrateSelect = 2;

    /// <summary>CAN 适配器列表（由 <see cref="CanAdapterFactory.Enumerate"/> 实时枚举）。</summary>
    [ObservableProperty]
    private ObservableCollection<CanAdapterDescriptor> _canAdapterFamilies = [];

    /// <summary>当前选中的 CAN 适配器索引。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCanSerialPortVisible))]
    [NotifyPropertyChangedFor(nameof(IsCanChannelSelectVisible))]
    [NotifyPropertyChangedFor(nameof(IsCanDeviceIndexSelectVisible))]
    private int _canAdapterSelect = -1;

    /// <summary>仅当 CAN 适配器选择为 SLCAN/串口类型时显示 USB-CAN 串口选择栏。</summary>
    public bool IsCanSerialPortVisible
        => CanAdapterSelect >= 0
        && CanAdapterSelect < CanAdapterFamilies.Count
        && CanAdapterFamilies[CanAdapterSelect].Kind == CanAdapterKind.Slcan;

    [ObservableProperty]
    private ObservableCollection<string> _canChannelFamilies = [];

    [ObservableProperty]
    private int _canChannelSelect;

    [ObservableProperty]
    private ObservableCollection<string> _canDeviceIndexFamilies = [];

    [ObservableProperty]
    private int _canDeviceIndexSelect;

    public bool IsCanChannelSelectVisible => CanChannelFamilies.Count > 1;

    public bool IsCanDeviceIndexSelectVisible => CanDeviceIndexFamilies.Count > 1;

    /// <summary>
    /// CAN 适配器最近一次枚举的诊断行（DLL 是否找到、各 ProbeDevice 的结果等）。<br/>
    /// 仅在 <see cref="IsDebugMode"/> 为 true 时由 UI 显示，便于定位 "未检测到设备" 的根因。
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<string> _canProbeDiagnostics = [];

    /// <summary>
    /// 当前是否处于 Debug 模式（来自 <see cref="UserSettings.IsDebugMode"/>）。<br/>
    /// Debug 模式开启时，设备连接页会显示 CAN 适配器探测诊断面板及"打开日志目录"按钮。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCanProbeDebugVisible))]
    [NotifyPropertyChangedFor(nameof(IsUsbDebugVisible))]
    private bool _isDebugMode;

    /// <summary>添加设备页当前选中的协议栈标签页（0=EtherCAT, 1=CANopen, 2=Modbus），自动记忆。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCanProbeDebugVisible))]
    [NotifyPropertyChangedFor(nameof(IsOnCanOpenTab))]
    private int _deviceAddTabIndex;

    /// <summary>
    /// CAN 适配器诊断面板可见性：仅在 Debug 模式开启 且 当前位于 CANopen 标签页（index=1）时显示。
    /// </summary>
    public bool IsCanProbeDebugVisible => IsDebugMode && DeviceAddTabIndex == 1;

    /// <summary>当前是否在 CANopen 标签页（index=1），控制顶部"刷新 CAN 适配器"按钮的可见性。</summary>
    public bool IsOnCanOpenTab => DeviceAddTabIndex == 1;

    /// <summary>是否正在后台枚举 CAN 适配器（首次进页或手动刷新）。</summary>
    [ObservableProperty]
    private bool _isRefreshingCanAdapters;

    /// <summary>
    /// 刷新 CAN 适配器列表（UI 按钮 / 下拉打开时调用）。<br/>
    /// 统一走异步路径以便整页遮罩（<see cref="IsRefreshingCanAdapters"/>）覆盖加载过程。
    /// </summary>
    [RelayCommand]
    public Task RefreshCanAdapters() => RefreshCanAdaptersAsync();

    /// <summary>
    /// 在后台线程枚举 CAN 适配器，避免首次进页同步 1~2 秒 DLL 搜索 / 设备探测阻塞 UI 线程。<br/>
    /// 枚举期间会推送 <see cref="IsRefreshingCanAdapters"/>=true，UI 可据此显示加载动画。
    /// </summary>
    public async Task RefreshCanAdaptersAsync()
    {
        if (IsRefreshingCanAdapters) return; // 避免重入
        IsRefreshingCanAdapters = true;
        try
        {
            var diag = new List<string>();
            // 后台线程执行 DLL 搜索与探测，不碑阫 UI
            var descriptors = await Task.Run(() =>
                CanAdapterFactory.Enumerate(includeUnavailable: true, diag));
            ApplyAdapterEnumerationResults(descriptors, diag);
        }
        finally
        {
            IsRefreshingCanAdapters = false;
        }
    }

    /// <summary>
    /// 将枚举结果应用到 UI 黑本收藏（CanAdapterFamilies / CanProbeDiagnostics）并同步到 AppLog。<br/>
    /// 调用者需保证该方法在 UI 线程上执行（RefreshCanAdaptersAsync 中 await 后以原上下文返回 UI 线程）。
    /// </summary>
    private void ApplyAdapterEnumerationResults(
        IReadOnlyList<CanAdapterDescriptor> descriptors,
        IList<string> diag)
    {
        var prev = (CanAdapterSelect >= 0 && CanAdapterSelect < CanAdapterFamilies.Count)
            ? CanAdapterFamilies[CanAdapterSelect]
            : null;

        CanAdapterFamilies.Clear();
        foreach (CanAdapterDescriptor d in descriptors)
            CanAdapterFamilies.Add(d);

        // 同步到 UI 面板（仅 Debug 模式可见）
        CanProbeDiagnostics.Clear();
        foreach (string line in diag)
            CanProbeDiagnostics.Add(line);

        // 同步到 AppLog（便于离线复盘 / 用户上传日志排障）
        foreach (string line in diag)
        {
            AppData.AppLogViewModel.Log(
                Models.AppLogLevel.Debug,
                Models.AppLogCategory.System,
                "CAN 适配器枚举",
                line);
        }

        // 优先选回上一次的同 Kind+Identifier
        int idx = -1;
        if (prev != null)
        {
            for (int i = 0; i < CanAdapterFamilies.Count; i++)
            {
                if (CanAdapterFamilies[i].Kind == prev.Kind &&
                    CanAdapterFamilies[i].Identifier == prev.Identifier)
                {
                    idx = i; break;
                }
            }
        }

        // 按优先级选默认适配器：真实硬件 > SLCAN > Virtual
        if (idx < 0)
        {
            // 第一优先：真实硬件（非 Slcan、非 Virtual）
            for (int i = 0; i < CanAdapterFamilies.Count; i++)
            {
                var k = CanAdapterFamilies[i].Kind;
                if (CanAdapterFamilies[i].IsAvailable &&
                    k != CanAdapterKind.Slcan && k != CanAdapterKind.Virtual)
                { idx = i; break; }
            }
        }
        if (idx < 0)
        {
            // 第二优先：SLCAN
            for (int i = 0; i < CanAdapterFamilies.Count; i++)
            {
                if (CanAdapterFamilies[i].IsAvailable &&
                    CanAdapterFamilies[i].Kind == CanAdapterKind.Slcan)
                { idx = i; break; }
            }
        }
        if (idx < 0)
        {
            // 兜底：Virtual 或列表第一项
            for (int i = 0; i < CanAdapterFamilies.Count; i++)
            {
                if (CanAdapterFamilies[i].Kind == CanAdapterKind.Virtual)
                { idx = i; break; }
            }
        }
        if (idx < 0 && CanAdapterFamilies.Count > 0) idx = 0;
        CanAdapterSelect = idx;
        UpdateCanAdapterOptionLists();
        OnPropertyChanged(nameof(IsCanSerialPortVisible));
    }

    private void UpdateCanAdapterOptionLists()
    {
        CanChannelFamilies.Clear();
        CanDeviceIndexFamilies.Clear();

        CanAdapterDescriptor? descriptor = CanAdapterSelect >= 0 && CanAdapterSelect < CanAdapterFamilies.Count
            ? CanAdapterFamilies[CanAdapterSelect]
            : null;
        int channelCount = Math.Max(1, descriptor?.ChannelCount ?? 1);
        int deviceCount = Math.Max(1, descriptor?.DeviceCount ?? 1);

        for (int i = 0; i < channelCount; i++)
            CanChannelFamilies.Add($"CH{i}");
        for (int i = 0; i < deviceCount; i++)
            CanDeviceIndexFamilies.Add($"#{i}");

        if (CanChannelSelect < 0 || CanChannelSelect >= CanChannelFamilies.Count) CanChannelSelect = 0;
        if (CanDeviceIndexSelect < 0 || CanDeviceIndexSelect >= CanDeviceIndexFamilies.Count) CanDeviceIndexSelect = 0;

        OnPropertyChanged(nameof(IsCanChannelSelectVisible));
        OnPropertyChanged(nameof(IsCanDeviceIndexSelectVisible));
    }

    private string BuildCanAdapterIdentifier(CanAdapterDescriptor descriptor)
    {
        if (descriptor.Kind is CanAdapterKind.ControlCan or CanAdapterKind.Zlgcan)
        {
            string[] parts = descriptor.Identifier.Split('/');
            string type = parts.Length > 0 ? parts[0] : "0";
            int deviceIndex = Math.Clamp(CanDeviceIndexSelect, 0, Math.Max(0, descriptor.DeviceCount - 1));
            int channel = Math.Clamp(CanChannelSelect, 0, Math.Max(0, descriptor.ChannelCount - 1));
            return $"{type}/{deviceIndex}/{channel}";
        }

        return descriptor.Identifier;
    }

    // ── ZLG 驱动 DLL 扫描（后台线程，不阻塞 UI）─────────────────────────

    private CancellationTokenSource? _zlgScanCts;

    /// <summary>是否正在执行 ZLG 驱动 DLL 扫描。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZlgScanPanelVisible))]
    private bool _isZlgScanning;

    /// <summary>ZLG 扫描状态文本（显示在进度面板下方）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZlgScanPanelVisible))]
    private string _zlgScanStatus = string.Empty;

    /// <summary>ZLG 扫描进度（0~1）。</summary>
    [ObservableProperty]
    private double _zlgScanFraction;

    /// <summary>是否显示 ZLG 扫描进度面板（扫描中或有末次状态时）。</summary>
    public bool IsZlgScanPanelVisible => IsZlgScanning || !string.IsNullOrEmpty(ZlgScanStatus);

    /// <summary>
    /// 启动 ZLG 驱动 DLL 扫描（常见安装目录，快，通常 &lt; 1 秒）。<br/>
    /// 注：<c>[RelayCommand]</c> 对带可选参数的方法会生成 <c>IAsyncRelayCommand&lt;T&gt;</c>，
    /// 导致 XAML 中未传 CommandParameter 时 CanExecute(null) 返回 false（按钮被禁用）。
    /// 因此这里使用无参方法调用带参核心。
    /// </summary>
    [RelayCommand]
    private Task ScanZlgDriversAsync() => DoScanZlgDriversAsync(fullDriveScan: false);

    /// <summary>全盘扫描（如常见目录未找到）。</summary>
    [RelayCommand]
    private Task ScanZlgDriversFullAsync() => DoScanZlgDriversAsync(fullDriveScan: true);

    /// <summary>取消正在进行的 ZLG 驱动 DLL 扫描。</summary>
    [RelayCommand]
    private void CancelZlgScan() => _zlgScanCts?.Cancel();

    private async Task DoScanZlgDriversAsync(bool fullDriveScan)
    {
        _zlgScanCts?.Cancel();
        _zlgScanCts = new CancellationTokenSource();
        IsZlgScanning = true;
        ZlgScanFraction = 0;
        ZlgScanStatus = fullDriveScan ? "正在进行全盘扫描，请稍候…" : "正在扫描常见驱动安装目录…";

        var progress = new Progress<(string Status, double Fraction)>(report =>
        {
            ZlgScanStatus = report.Status;
            ZlgScanFraction = report.Fraction;
        });

        try
        {
            var found = await ZlgKernelDllScanner.ScanAsync(
                progress, fullDriveScan, _zlgScanCts.Token);

            if (found.Count > 0)
            {
                // 扫描到新目录后重新枚举适配器（后台线程）
                await RefreshCanAdaptersAsync();
            }
        }
        catch (OperationCanceledException)
        {
            ZlgScanStatus = "已取消";
        }
        catch (Exception ex)
        {
            ZlgScanStatus = $"扫描出错: {ex.Message}";
        }
        finally
        {
            IsZlgScanning = false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>在资源管理器中打开当前 AppLog 日志目录（受 Debug 模式管制的诊断入口）。</summary>
    [RelayCommand]
    private void OnOpenLogDirectory()
    {
        AppData.AppLogViewModel.Current?.OpenLogDirectoryCommand.Execute(null);
    }

    private static CanBitrate CanBitrateFromIndex(int idx) => idx switch
    {
        0 => CanBitrate.Br1000k,
        1 => CanBitrate.Br800k,
        2 => CanBitrate.Br500k,
        3 => CanBitrate.Br250k,
        4 => CanBitrate.Br125k,
        5 => CanBitrate.Br100k,
        6 => CanBitrate.Br50k,
        7 => CanBitrate.Br20k,
        8 => CanBitrate.Br10k,
        _ => CanBitrate.Br500k,
    };

    partial void OnIsCanopenConnectedChanged(bool value)
    {
        CanOpenConnectCommand.NotifyCanExecuteChanged();
        CanOpenDisconnectCommand.NotifyCanExecuteChanged();
        QueryCanopenSlaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsAnyConnected));
        OnPropertyChanged(nameof(ActiveServoMaster));
        OnPropertyChanged(nameof(ActiveAxis));
    }

    partial void OnIsCanopenSlaveOnlineChanged(bool value) => OnPropertyChanged(nameof(ActiveAxis));

    // ============================================================
    //  USB（HPM 系列 MCU + ThreadX/USBX 从机协议栈）
    //  与 EtherCAT/CANopen/Modbus 并行独立，不参与 ActiveProtocolStack 互斥。
    // ============================================================

    [ObservableProperty]
    private bool _isUsbConnected;

    [ObservableProperty]
    private string _usbStatusText = string.Empty;

    /// <summary>"Manufacturer / Product (VID:PID)" 等当前已连接设备的人类可读描述。</summary>
    [ObservableProperty]
    private string _usbDeviceInfo = string.Empty;

    /// <summary>当前列表中可见的 USB 设备（已经过过滤）。</summary>
    [ObservableProperty]
    private ObservableCollection<UsbDeviceDescriptor> _usbDevices = [];

    /// <summary>当前选中的 USB 设备索引。</summary>
    [ObservableProperty]
    private int _usbDeviceSelect = -1;

    /// <summary>USB 工作方式下拉选项（顺序与 <see cref="UsbWorkMode"/> 严格一致）。</summary>
    [ObservableProperty]
    private ObservableCollection<string> _usbWorkModes =
    [
        "CDC-ACM (虚拟串口)",
        "WinUSB / 自定义 Bulk-Only",
        "MSC (大容量存储)",
    ];

    /// <summary>当前 USB 工作方式索引（默认 1 = WinUSB Bulk）。</summary>
    [ObservableProperty]
    private int _usbWorkModeSelect = 1;

    /// <summary>VID:PID 白名单文本（每行一项，"34B7:*" / "34B7:A001"）。</summary>
    [ObservableProperty]
    private string _usbVidPidWhitelistText = "34B7:*";

    /// <summary>是否启用 VID/PID 白名单过滤。</summary>
    [ObservableProperty]
    private bool _usbFilterByVidPid = true;

    /// <summary>是否启用设备类（Bulk/CDC/HID/MSC）过滤。</summary>
    [ObservableProperty]
    private bool _usbFilterByClass = true;

    /// <summary>是否启用 Manufacturer/Product 字符串关键词（"HPM/HPMicro/USBX/ThreadX"）过滤。</summary>
    [ObservableProperty]
    private bool _usbFilterByKeyword = true;

    /// <summary>USB 枚举 / 过滤的诊断行（仅在 <see cref="IsDebugMode"/> 时由 UI 展示）。</summary>
    [ObservableProperty]
    private ObservableCollection<string> _usbProbeDiagnostics = [];

    /// <summary>USB 接收内容日志（仅在 Debug 模式时展示，最多保留 200 行）。</summary>
    [ObservableProperty]
    private ObservableCollection<string> _usbRxLog = [];

    /// <summary>USB 接收内容 Debug 面板是否可见（受 <see cref="IsDebugMode"/> 控制）。</summary>
    public bool IsUsbDebugVisible => IsDebugMode;

    partial void OnIsUsbConnectedChanged(bool value)
    {
        UsbConnectCommand.NotifyCanExecuteChanged();
        UsbDisconnectCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsAnyDeviceConnected));
        // USB 与三大栈并行 → 不影响 IsAnyConnected
    }

    partial void OnUsbWorkModeSelectChanged(int value)
    {
        // 工作方式变化 → 立即在协议栈中更新（若已经存在 master，对其 ChangeWorkMode）
        if (_usbMaster is not null)
        {
            try
            {
                bool wasRunning = _usbMaster.IsRunning;
                _usbMaster.ChangeWorkMode(WorkModeFromIndex(value));
                if (wasRunning) _usbMaster.Start();
            }
            catch (Exception ex)
            {
                UsbStatusText = "切换工作方式失败：" + ex.Message;
            }
        }
        PersistSelections();
        RefreshUsbDevices();
    }

    partial void OnUsbFilterByVidPidChanged(bool value) { PersistSelections(); RefreshUsbDevices(); }

    partial void OnUsbFilterByClassChanged(bool value) { PersistSelections(); RefreshUsbDevices(); }

    partial void OnUsbFilterByKeywordChanged(bool value) { PersistSelections(); RefreshUsbDevices(); }

    partial void OnUsbVidPidWhitelistTextChanged(string value) { PersistSelections(); RefreshUsbDevices(); }

    partial void OnUsbDeviceSelectChanged(int value)
    {
        PersistSelections();
        UsbConnectCommand.NotifyCanExecuteChanged();
    }

    private static UsbWorkMode WorkModeFromIndex(int idx) => idx switch
    {
        0 => UsbWorkMode.CdcAcm,
        1 => UsbWorkMode.WinUsbBulk,
        2 => UsbWorkMode.Msc,
        _ => UsbWorkMode.WinUsbBulk,
    };

    private static List<(ushort Vid, ushort Pid)> ParseVidPidList(string text)
    {
        var list = new List<(ushort, ushort)>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (string raw in text.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            int sep = line.IndexOf(':');
            if (sep <= 0) continue;
            string vidStr = line[..sep].Trim().TrimStart('0', 'x', 'X');
            string pidStr = line[(sep + 1)..].Trim().TrimStart('0', 'x', 'X');
            if (!ushort.TryParse(vidStr, System.Globalization.NumberStyles.HexNumber, null, out ushort vid))
                continue;
            ushort pid = 0;
            if (pidStr != "*" && pidStr.Length > 0
                && !ushort.TryParse(pidStr, System.Globalization.NumberStyles.HexNumber, null, out pid))
                continue;
            list.Add((vid, pid));
        }
        if (list.Count == 0) list.Add((UsbDefaults.HpMicroVendorId, UsbDefaults.AnyProductId));
        return list;
    }

    private UsbDeviceFilter BuildUsbFilter() => new()
    {
        UseVidPidWhitelist = UsbFilterByVidPid,
        UseDeviceClass = UsbFilterByClass,
        UseStringKeywords = UsbFilterByKeyword,
        WorkMode = WorkModeFromIndex(UsbWorkModeSelect),
        VidPidWhitelist = ParseVidPidList(UsbVidPidWhitelistText),
    };

    /// <summary>
    /// 重新枚举 USB 设备并按当前过滤条件回填到下拉。<br/>
    /// 优先恢复用户上次选择的 InstanceId；否则选第一个 IsAvailable。
    /// </summary>
    [RelayCommand]
    public void RefreshUsbDevices()
    {
        string? prevId = UsbDeviceSelect >= 0 && UsbDeviceSelect < UsbDevices.Count
            ? UsbDevices[UsbDeviceSelect].InstanceId
            : Services.UserSettingsService.Load().DeviceAdd_UsbInstanceId;

        var diag = new List<string>();
        UsbDevices.Clear();
        if (OperatingSystem.IsWindows())
        {
            foreach (UsbDeviceDescriptor d in UsbDeviceEnumerator.Enumerate(BuildUsbFilter(), onlyPresent: true, diag))
                UsbDevices.Add(d);
        }
        else
        {
            diag.Add("非 Windows 平台，已跳过 USB 设备枚举。");
        }

        UsbProbeDiagnostics.Clear();
        foreach (string line in diag) UsbProbeDiagnostics.Add(line);

        // 选回上次的同 InstanceId
        int idx = -1;
        if (!string.IsNullOrEmpty(prevId))
        {
            for (int i = 0; i < UsbDevices.Count; i++)
            {
                if (string.Equals(UsbDevices[i].InstanceId, prevId, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i; break;
                }
            }
        }
        if (idx < 0 && UsbDevices.Count > 0) idx = 0;
        UsbDeviceSelect = idx;
    }

    private bool CanUsbConnect() => !IsUsbConnected && UsbDeviceSelect >= 0 && UsbDeviceSelect < UsbDevices.Count && !IsBusy;

    private bool CanUsbDisconnect() => IsUsbConnected && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUsbConnect))]
    private void OnUsbConnect()
    {
        try
        {
            UsbDeviceDescriptor d = UsbDevices[UsbDeviceSelect];
            UsbWorkMode mode = WorkModeFromIndex(UsbWorkModeSelect);

            // 释放上一次的 master
            try { _usbMaster?.Dispose(); } catch { /* ignore */ }
            _usbMaster = UsbMaster.Create(mode, d);
            _usbMaster.PacketReceived += OnUsbPacketReceived;

            if (!_usbMaster.Start(d.VendorId, UsbDefaults.AnyProductId))
            {
                UsbStatusText = $"USB 打开失败（{mode}）：{d.DisplayName}";
                _usbMaster.PacketReceived -= OnUsbPacketReceived;
                try { _usbMaster.Dispose(); } catch { /* ignore */ }
                _usbMaster = null;
                return;
            }

            IsUsbConnected = true;
            UsbDeviceInfo = d.DisplayName;
            UsbStatusText = $"已连接 [{mode}] {d.DisplayName}";
            OnPropertyChanged(nameof(UsbMaster));
        }
        catch (Exception ex)
        {
            UsbStatusText = "USB 连接异常：" + ex.Message;
            if (_usbMaster is not null)
            {
                _usbMaster.PacketReceived -= OnUsbPacketReceived;
            }

            try { _usbMaster?.Dispose(); } catch { /* ignore */ }
            _usbMaster = null;
            IsUsbConnected = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUsbDisconnect))]
    private void OnUsbDisconnect()
    {
        if (_usbMaster is not null)
        {
            _usbMaster.PacketReceived -= OnUsbPacketReceived;
        }

        try { _usbMaster?.Stop(); } catch { /* ignore */ }
        try { _usbMaster?.Dispose(); } catch { /* ignore */ }
        _usbMaster = null;
        IsUsbConnected = false;
        UsbStatusText = "已断开";
        UsbDeviceInfo = string.Empty;
        OnPropertyChanged(nameof(UsbMaster));
    }

    [RelayCommand]
    private void OnClearUsbRxLog() => UsbRxLog.Clear();

    private void OnUsbPacketReceived(UsbPacket pkt)
    {
        string channelName = pkt.Channel switch
        {
            UsbChannel.CurveFitting => "曲线拟合",
            UsbChannel.AdaptiveParam => "自适应参数",
            UsbChannel.HighBandwidthTelemetry => "高带宽遥测",
            UsbChannel.VendorPrivate => "厂商私有",
            _ => $"CH:{(ushort)pkt.Channel:X4}",
        };
        string hexPayload = pkt.Payload.Length == 0
            ? "(空)"
            : BitConverter.ToString(pkt.Payload, 0, Math.Min(pkt.Payload.Length, 32))
              + (pkt.Payload.Length > 32 ? $"…(+{pkt.Payload.Length - 32}B)" : string.Empty);
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] Seq={pkt.Sequence} {channelName} {hexPayload}";

        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            if (UsbRxLog.Count >= 200)
            {
                UsbRxLog.RemoveAt(0);
            }

            UsbRxLog.Add(line);
        });
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
            // 排除已知的虚拟/非物理网卡（暂时禁用）
            .Where(nic => !_virtualNicKeywords.Any(keyword =>
               nic.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || nic.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            // 排除子设备（WFP、QoS 等中间层驱动），仅保留母设备（暂时禁用）
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
        string interfaceName = SelectedEthernetName;

        try
        {
            // 异步执行设备连接，避免阻塞 UI 线程
            (int Count, string SlaveName, int SlaveAddr, string SlaveState) result = await Task.Run(() =>
            {
                _axis = null;
                ClearEtherCatSdoCache();

                int c = ecatMaster.StartActivity(interfaceName);
                string name = string.Empty;
                int addr = 0;
                string state = string.Empty;
                if (c > 0)
                {
                    _axis = new EtherCATSlave_CiA402(ecatMaster, 1);
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

            // 填充多从站下拉列表
            _etherCatSlaveCount = result.Count;
            EtherCatSlaveList.Clear();
            for (int i = 1; i <= result.Count; i++)
            {
                EtherCatSlaveList.Add($"从站 {i}（地址: {i}）");
            }

            // 默认选中从站 1（index 0），不触发 OnEtherCatSlaveSelectChanged
            // 因为 _axis 已在 Task.Run 内以地址 1 创建
            SetEtherCatSlaveSelectSilently(0, true);
            OnPropertyChanged(nameof(IsEtherCatMultiSlaveVisible));

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
            ClearEtherCatSdoCache();
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
                ClearEtherCatSdoCache();
            });

            _axis = null;
            _etherCatSlaveCount = 0;
            EtherCatSlaveList.Clear();
            SetEtherCatSlaveSelectSilently(0, true);
            OnPropertyChanged(nameof(IsEtherCatMultiSlaveVisible));
            EthernetStatusText = "设备已断开";
            EthernetSlaveInfo = string.Empty;
            EthernetSlaveNameInfo = string.Empty;
            EthernetSlaveAddrInfo = string.Empty;
            EthernetSlaveStateInfo = string.Empty;
            IsEthernetConnected = false;
            IsEthernetLinkSucceedVisible = Visibility.Hidden;
            if (ActiveProtocol == ActiveProtocolStack.EtherCAT)
            {
                ActiveProtocol = ActiveProtocolStack.None;
            }
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
            _etherCatCommLostCount = 0; // 成功，重置看门狗计数
        }
        catch (Exception ex)
        {
            AppData.AppLogViewModel.Log(Models.AppLogLevel.Warning, Models.AppLogCategory.EtherCAT, "从站状态轮询异常", ex.Message);
            _etherCatCommLostCount++;
            if (_etherCatCommLostCount >= CommLostThreshold)
            {
                CommLost?.Invoke(this, new CommLostEventArgs("EtherCAT", _etherCatCommLostCount));
                _etherCatCommLostCount = 0;
            }
        }
    }

    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            InitializeViewModel();
        }

        // 每次进入页面时刷新 Debug 模式标志，
        // 让设置页里切换 Debug 模式后再返回时能立刻看到（或隐藏）CAN 诊断面板。
        IsDebugMode = Services.UserSettingsService.Load().IsDebugMode;

        // 订阅 USB 热插拔事件（重复订阅会去重）。
        Services.UsbDeviceWatcher.DevicesChanged -= OnUsbDevicesChanged;
        Services.UsbDeviceWatcher.DevicesChanged += OnUsbDevicesChanged;
    }

    public override void OnNavigatedFrom()
    {
        // 离开页面时取消订阅，避免后台仍在持续刷新 CAN 适配器。
        Services.UsbDeviceWatcher.DevicesChanged -= OnUsbDevicesChanged;
    }

    /// <summary>USB 设备热插拔事件回调（已防抖，事件在 UI 线程触发）。</summary>
    private void OnUsbDevicesChanged(object? sender, EventArgs e)
    {
        // 仅当未处于 CAN 连接状态时刷新（避免打断正在使用的设备句柄）。
        if (IsCanopenConnected) return;
        _ = RefreshCanAdaptersAsync();
    }

    private void StartModbusSlavePolling()
    {
        StopModbusSlavePolling();
        var cts = new CancellationTokenSource();
        _modbusSlavePollingCts = cts;
        _ = PollModbusSlaveAsync(cts.Token);
    }

    private void StopModbusSlavePolling()
    {
        var cts = _modbusSlavePollingCts;
        _modbusSlavePollingCts = null;
        try { cts?.Cancel(); } catch { /* ignore */ }
        try { cts?.Dispose(); } catch { /* ignore */ }
    }

    private async Task PollModbusSlaveAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                if (token.IsCancellationRequested || !IsModbusConnected || IsBusy) continue;
                await ProbeModbusSlaveAsync(manual: false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanQueryModbusSlave))]
    private async Task QueryModbusSlave()
    {
        if (!IsModbusConnected) return;
        ModbusStatusText = "正在查询 Modbus 从站在线状态...";
        await ProbeModbusSlaveAsync(manual: true);
    }

    private async Task<bool> ProbeModbusSlaveAsync(bool manual)
    {
        if (!IsModbusConnected || !_modbusMaster.IsOpen)
            return false;
        if (Interlocked.Exchange(ref _modbusSlaveProbeActive, 1) == 1)
            return IsModbusSlaveOnline;

        bool wasOnline = IsModbusSlaveOnline;
        int slaveAddr = Math.Clamp(ModbusSlaveAddress, 1, 247);
        try
        {
            var result = await Task.Run(() =>
            {
                var axis = new ModbusSlave_CiA402(_modbusMaster, slaveAddr);
                bool ok = axis.ProbeIdentity();
                return (Ok: ok, Axis: axis, Error: _modbusMaster.LastException);
            });

            if (result.Ok)
            {
                _modbusAxis = result.Axis;
                IsModbusSlaveOnline = true;
                _modbusCommLostCount = 0; // 探测成功，重置看门狗计数
                ModbusSlaveNameInfo = _modbusAxis.SlaveName ?? $"Modbus Slave #{slaveAddr}";
                ModbusSlaveAddrInfo = _modbusAxis.SlaveAddr.ToString();
                ModbusFirmwareInfo = _modbusAxis.SoftwareVersion ?? string.Empty;
                if (manual || !wasOnline)
                    ModbusStatusText = $"从站 {slaveAddr} 已上线，Modbus 协议栈已启动";
                OnPropertyChanged(nameof(ActiveAxis));
                return true;
            }

            _modbusAxis = null;
            IsModbusSlaveOnline = false;
            ModbusSlaveNameInfo = string.Empty;
            ModbusSlaveAddrInfo = slaveAddr.ToString();
            ModbusFirmwareInfo = string.Empty;
            if (manual || wasOnline)
                ModbusStatusText = $"从站 {slaveAddr} 未应答，链路保持连接，Modbus 核心收发已暂停（{result.Error}）";

            // 看门狗：连续失败计数，达到阈值后触发 CommLost 事件
            if (!manual)
            {
                _modbusCommLostCount++;
                if (_modbusCommLostCount >= CommLostThreshold)
                {
                    CommLost?.Invoke(this, new CommLostEventArgs("Modbus", _modbusCommLostCount));
                    _modbusCommLostCount = 0; // 触发后重置，避免重复触发
                }
            }
            OnPropertyChanged(nameof(ActiveAxis));
            return false;
        }
        catch (Exception ex)
        {
            _modbusAxis = null;
            IsModbusSlaveOnline = false;
            if (manual || wasOnline)
                ModbusStatusText = $"从站在线查询异常：{ex.Message}";
            OnPropertyChanged(nameof(ActiveAxis));
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _modbusSlaveProbeActive, 0);
        }
    }

    private void StartCanOpenSlavePolling()
    {
        StopCanOpenSlavePolling();
        var cts = new CancellationTokenSource();
        _canOpenSlavePollingCts = cts;
        _ = PollCanOpenSlaveAsync(cts.Token);
    }

    private void StopCanOpenSlavePolling()
    {
        var cts = _canOpenSlavePollingCts;
        _canOpenSlavePollingCts = null;
        try { cts?.Cancel(); } catch { /* ignore */ }
        try { cts?.Dispose(); } catch { /* ignore */ }
    }

    private async Task PollCanOpenSlaveAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                if (token.IsCancellationRequested || !IsCanopenConnected || IsBusy) continue;
                await ProbeCanOpenSlaveAsync(manual: false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanQueryCanopenSlave))]
    private async Task QueryCanopenSlave()
    {
        if (!IsCanopenConnected) return;
        CanopenStatusText = "正在查询 CANopen 节点在线状态...";
        await ProbeCanOpenSlaveAsync(manual: true);
    }

    private async Task<bool> ProbeCanOpenSlaveAsync(bool manual)
    {
        var master = _canOpenMaster;
        if (!IsCanopenConnected || master is null)
            return false;
        if (Interlocked.Exchange(ref _canOpenSlaveProbeActive, 1) == 1)
            return IsCanopenSlaveOnline;

        bool wasOnline = IsCanopenSlaveOnline;
        int nodeId = Math.Clamp(CanopenNodeId, 1, 127);
        try
        {
            var result = await Task.Run(() =>
            {
                if (!master.IsRunning)
                    master.Start();

                var axis = new CanOpenSlave_CiA402(master, nodeId);
                bool ok = axis.ProbeIdentity();
                if (!ok)
                    master.Stop();
                return (Ok: ok, Axis: axis, Abort: master.LastAbort);
            });

            if (result.Ok)
            {
                _canOpenAxis = result.Axis;
                IsCanopenSlaveOnline = true;
                _canOpenCommLostCount = 0; // 探测成功，重置看门狗计数
                CanopenSlaveNameInfo = _canOpenAxis.SlaveName ?? $"CANopen Node #{nodeId}";
                CanopenSlaveAddrInfo = _canOpenAxis.SlaveAddr.ToString();
                CanopenFirmwareInfo = _canOpenAxis.SoftwareVersion ?? string.Empty;
                if (manual || !wasOnline)
                    CanopenStatusText = $"节点 {nodeId} 已上线，CANopen 协议栈已启动";
                OnPropertyChanged(nameof(ActiveServoMaster));
                OnPropertyChanged(nameof(ActiveAxis));
                return true;
            }

            _canOpenAxis = null;
            IsCanopenSlaveOnline = false;
            CanopenSlaveNameInfo = string.Empty;
            CanopenSlaveAddrInfo = nodeId.ToString();
            CanopenFirmwareInfo = string.Empty;
            if (manual || wasOnline)
                CanopenStatusText = $"节点 {nodeId} 未应答，链路保持连接，CANopen 核心收发已暂停（{result.Abort}）";

            // 看门狗：连续失败计数，达到阈值后触发 CommLost 事件
            if (!manual)
            {
                _canOpenCommLostCount++;
                if (_canOpenCommLostCount >= CommLostThreshold)
                {
                    CommLost?.Invoke(this, new CommLostEventArgs("CANopen", _canOpenCommLostCount));
                    _canOpenCommLostCount = 0;
                }
            }
            OnPropertyChanged(nameof(ActiveServoMaster));
            OnPropertyChanged(nameof(ActiveAxis));
            return false;
        }
        catch (Exception ex)
        {
            try { master.Stop(); } catch { /* ignore */ }
            _canOpenAxis = null;
            IsCanopenSlaveOnline = false;
            if (manual || wasOnline)
                CanopenStatusText = $"节点在线查询异常：{ex.Message}";
            OnPropertyChanged(nameof(ActiveServoMaster));
            OnPropertyChanged(nameof(ActiveAxis));
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _canOpenSlaveProbeActive, 0);
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
            // ── 在线接收模式：跳过 ProbeIdentity，仅打开串口 ──────────────────
            bool liveMode = IsLiveReceiveModeActive?.Invoke() == true;
            if (liveMode)
            {
                bool portOk = await Task.Run(() => _modbusMaster.Open(portName, baud, dataBits, parity, stop));
                if (!portOk)
                {
                    IsLinkFailed = true;
                    IsLinkSucceed = false;
                    IsLinkFailedVisible = Visibility.Visible;
                    IsLinkSucceedVisible = Visibility.Hidden;
                    ModbusStatusText = string.IsNullOrEmpty(_modbusMaster.LastOpenError)
                        ? $"串口 {portName} 打开失败（在线接收模式）"
                        : _modbusMaster.LastOpenError;
                    AppData.AppLogViewModel.Log(
                        Models.AppLogLevel.Warning,
                        Models.AppLogCategory.System,
                        "Modbus 在线接收模式：串口打开失败",
                        ModbusStatusText);
                    return;
                }

                // 串口已打开，标记连接成功（不校验设备身份）
                IsLinkSucceed = true;
                IsLinkFailed = false;
                IsLinkSucceedVisible = Visibility.Visible;
                IsLinkFailedVisible = Visibility.Hidden;
                IsModbusConnected = true;
                IsModbusSlaveOnline = false;
                ActiveProtocol = ActiveProtocolStack.Modbus;
                ModbusSlaveNameInfo = string.Empty;
                ModbusSlaveAddrInfo = slaveAddr.ToString();
                ModbusFirmwareInfo = string.Empty;
                ModbusStatusText = $"在线接收模式：已连接 {portName} @ {baud}（从机 {slaveAddr}，跳过设备验证）";

                AppData.AppLogViewModel.Log(
                    Models.AppLogLevel.Info,
                    Models.AppLogCategory.System,
                    "Modbus 在线接收模式连接成功",
                    ModbusStatusText);

                // 通知 DataViewViewModel 在 UI 线程启动静默接收
                await Task.Run(() => LiveReceiveConnectionReady?.Invoke());
                return;
            }
            // ────────────────────────────────────────────────────────────────

            // portOpened 在 Task 内赋值，用于在 Close() 之后判断失败原因
            bool portOpened = false;
            ModbusSlave_CiA402? detectedAxis = null;
            ModbusExceptionCode lastException = ModbusExceptionCode.None;
            bool ok = await Task.Run(() =>
            {
                if (!_modbusMaster.Open(portName, baud, dataBits, parity, stop))
                    return false;

                portOpened = true;
                var axis = new ModbusSlave_CiA402(_modbusMaster, slaveAddr);
                bool alive = axis.ProbeIdentity();
                lastException = _modbusMaster.LastException;
                if (alive)
                    detectedAxis = axis;
                return alive;
            });

            if (!ok)
            {
                if (!portOpened)
                {
                    _modbusAxis = null;
                    IsModbusSlaveOnline = false;
                    IsLinkFailed = true;
                    IsLinkSucceed = false;
                    IsLinkFailedVisible = Visibility.Visible;
                    IsLinkSucceedVisible = Visibility.Hidden;
                    // 串口层打开失败
                    ModbusStatusText = string.IsNullOrEmpty(_modbusMaster.LastOpenError)
                        ? $"串口 {portName} 打开失败"
                        : _modbusMaster.LastOpenError;
                    AppData.AppLogViewModel.Log(
                        Models.AppLogLevel.Warning,
                        Models.AppLogCategory.System,
                        "Modbus 连接失败",
                        ModbusStatusText);
                    return;
                }

                _modbusAxis = null;
                IsModbusConnected = true;
                IsModbusSlaveOnline = false;
                ActiveProtocol = ActiveProtocolStack.Modbus;
                IsLinkSucceed = true;
                IsLinkFailed = false;
                IsLinkSucceedVisible = Visibility.Visible;
                IsLinkFailedVisible = Visibility.Hidden;
                ModbusSlaveNameInfo = string.Empty;
                ModbusSlaveAddrInfo = slaveAddr.ToString();
                ModbusFirmwareInfo = string.Empty;
                ModbusStatusText = lastException == ModbusExceptionCode.Timeout
                    ? $"串口 {portName} 已打开；从站 {slaveAddr} 暂无应答（超时），链路保持连接并自动轮询上线"
                    : $"串口 {portName} 已打开；从站 {slaveAddr} 暂不可用（{lastException}），链路保持连接并自动轮询上线";
                AppData.AppLogViewModel.Log(
                    Models.AppLogLevel.Warning,
                    Models.AppLogCategory.System,
                    "Modbus 从站离线连接",
                    ModbusStatusText);
                OnPropertyChanged(nameof(ActiveAxis));
                StartModbusSlavePolling();
                return;
            }

            _modbusAxis = detectedAxis;
            IsLinkSucceed = true;
            IsLinkFailed = false;
            IsLinkSucceedVisible = Visibility.Visible;
            IsLinkFailedVisible = Visibility.Hidden;
            IsModbusConnected = true;
            IsModbusSlaveOnline = true;
            ActiveProtocol = ActiveProtocolStack.Modbus;

            ModbusSlaveNameInfo = _modbusAxis!.SlaveName ?? string.Empty;
            ModbusSlaveAddrInfo = _modbusAxis.SlaveAddr.ToString();
            ModbusFirmwareInfo = _modbusAxis.SoftwareVersion ?? string.Empty;
            ModbusStatusText = $"已连接：{portName} @ {baud}, 8{parity.ToString()[..1]}{(stop == StopBits.One5 ? "1.5" : ((int)stop).ToString())}";
            StartModbusSlavePolling();

        }
        catch (Exception ex)
        {
            StopModbusSlavePolling();
            try { _modbusMaster.Close(); } catch { /* ignore */ }
            _modbusAxis = null;
            IsModbusConnected = false;
            IsModbusSlaveOnline = false;
            IsLinkFailed = true;
            IsLinkSucceed = false;
            IsLinkFailedVisible = Visibility.Visible;
            IsLinkSucceedVisible = Visibility.Hidden;
            ModbusStatusText = $"连接异常：{ex.Message}\n"
                + $"请确认串口参数是否正确，设备是否已上电。";
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
            StopModbusSlavePolling();
            await Task.Run(() => _modbusMaster.Close());
            _modbusAxis = null;
            IsModbusConnected = false;
            IsModbusSlaveOnline = false;
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
    /// 通过用户在 <see cref="CanAdapterFamilies"/> 中选择的 CAN 适配器（PCAN / ControlCAN /
    /// ZLG zlgcan / Toomoss / slcan / Virtual）连接 CANopen 总线，并尝试与指定 nodeId 的
    /// CiA402 从机握手。<br/>
    /// 若用户未选择适配器（例如旧用户首次升级），回退到原行为：使用 <see cref="ComboBoxPortNameSelect"/>
    /// 选定的串口走 slcan 协议。<br/>
    /// 节点地址使用 <see cref="CanopenNodeId"/>，比特率使用 <see cref="ComboBoxCanBitrateSelect"/>。
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

        // 解析待打开的适配器：优先 CanAdapterFamilies；空则回退 slcan + 串口
        CanAdapterDescriptor? descriptor = null;
        if (CanAdapterSelect >= 0 && CanAdapterSelect < CanAdapterFamilies.Count)
            descriptor = CanAdapterFamilies[CanAdapterSelect];

        if (descriptor is null)
        {
            // 回退到原始行为
            if (ComboBoxPortNameFamilies.Count == 0 || ComboBoxPortNameSelect < 0)
            {
                CanopenStatusText = "未检测到可用 CAN 适配器";
                return;
            }
            string portFallback = ComboBoxPortNameFamilies[ComboBoxPortNameSelect];
            descriptor = new CanAdapterDescriptor(
                CanAdapterKind.Slcan, $"SLCAN ({portFallback})", portFallback, true);
        }
        else if (!descriptor.IsAvailable)
        {
            CanopenStatusText = $"适配器不可用：{descriptor.DisplayName} ({descriptor.Note})";
            return;
        }

        CanBitrate bitrate = CanBitrateFromIndex(ComboBoxCanBitrateSelect);
        int nodeId = Math.Clamp(CanopenNodeId, 1, 127);

        string adapterIdentifier = BuildCanAdapterIdentifier(descriptor);
        string adapterDisplayName = descriptor.DisplayName;
        if (descriptor.ChannelCount > 1) adapterDisplayName += $" {CanChannelFamilies[Math.Clamp(CanChannelSelect, 0, CanChannelFamilies.Count - 1)]}";
        if (descriptor.DeviceCount > 1) adapterDisplayName += $" #{Math.Clamp(CanDeviceIndexSelect, 0, descriptor.DeviceCount - 1)}";

        IsBusy = true;
        CanopenStatusText = $"正在打开 {adapterDisplayName} @ {(int)bitrate / 1000} kbps (Node {nodeId})...";

        try
        {
            CanOpenMaster? openedMaster = null;
            CanOpenSlave_CiA402? detectedAxis = null;
            bool slaveAlive = false;
            SdoAbortCode lastAbort = SdoAbortCode.None;
            bool transportOk = await Task.Run(() =>
            {
                ICanBus? bus = CanAdapterFactory.Create(descriptor, adapterIdentifier);
                if (bus is null) return false;
                if (!bus.Open(bitrate))
                {
                    bus.Dispose();
                    return false;
                }

                var master = new CanOpenMaster(bus);
                master.Start();

                var axis = new CanOpenSlave_CiA402(master, nodeId);
                slaveAlive = axis.ProbeIdentity();
                lastAbort = master.LastAbort;
                if (slaveAlive)
                {
                    detectedAxis = axis;
                }
                else
                {
                    master.Stop();
                }

                openedMaster = master;
                return true;
            });

            if (!transportOk || openedMaster is null)
            {
                CanopenStatusText = $"无法打开 {adapterDisplayName}，请检查驱动/DLL、设备占用或比特率设置";
                AppData.AppLogViewModel.Log(
                    Models.AppLogLevel.Warning,
                    Models.AppLogCategory.System,
                    "CANopen 连接失败",
                    $"{adapterDisplayName}: {CanopenStatusText}");
                return;
            }

            _canOpenMaster = openedMaster;
            _canOpenAxis = slaveAlive ? detectedAxis : null;
            IsCanopenConnected = true;
            IsCanopenSlaveOnline = slaveAlive;
            ActiveProtocol = ActiveProtocolStack.CANopen;
            if (slaveAlive && _canOpenAxis is not null)
            {
                CanopenSlaveNameInfo = _canOpenAxis.SlaveName ?? string.Empty;
                CanopenSlaveAddrInfo = _canOpenAxis.SlaveAddr.ToString();
                CanopenFirmwareInfo = _canOpenAxis.SoftwareVersion ?? string.Empty;
                CanopenStatusText = $"已连接：{adapterDisplayName} @ {(int)bitrate / 1000} kbps, Node {nodeId}";
            }
            else
            {
                CanopenSlaveNameInfo = string.Empty;
                CanopenSlaveAddrInfo = nodeId.ToString();
                CanopenFirmwareInfo = string.Empty;
                CanopenStatusText = $"总线已打开：{adapterDisplayName} @ {(int)bitrate / 1000} kbps；节点 {nodeId} 暂无应答，链路保持连接并自动轮询上线（{lastAbort}）";
                AppData.AppLogViewModel.Log(
                    Models.AppLogLevel.Warning,
                    Models.AppLogCategory.System,
                    "CANopen 节点离线连接",
                    CanopenStatusText);
            }
            OnPropertyChanged(nameof(ActiveServoMaster));
            OnPropertyChanged(nameof(ActiveAxis));
            StartCanOpenSlavePolling();
        }
        catch (Exception ex)
        {
            CleanupCanOpen();
            CanopenStatusText = $"连接异常: {ex.Message}";
            AppData.AppLogViewModel.Log(
                Models.AppLogLevel.Error,
                Models.AppLogCategory.System,
                "CANopen 连接异常",
                $"{adapterDisplayName}: {ex.Message}");
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
            StopCanOpenSlavePolling();
            await Task.Run(CleanupCanOpen);
            IsCanopenConnected = false;
            IsCanopenSlaveOnline = false;
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

        // 断开后自动重新枚举（设备 handle 已释放），让用户看到最新的可用列表。
        // 走异步路径，自动触发整页遮罩（IsRefreshingCanAdapters）。
        _ = RefreshCanAdaptersAsync();
    }

    private void CleanupCanOpen()
    {
        StopCanOpenSlavePolling();
        try { _canOpenMaster?.Dispose(); } catch { /* ignore */ }
        // SerialCanBus 由 CanOpenMaster.Dispose 一并释放
        _canOpenMaster = null;
        _canOpenAxis = null;
        IsCanopenSlaveOnline = false;
    }

    private void InitializeViewModel()
    {
        _isInitialized = true;

        // 在设置默认值之前先禁止 PersistSelections，防止默认值把 JSON 中
        // 已保存的标签页索引等字段提前覆盖为 0，导致 LoadPersistedSelections
        // 读回时拿到错误值。LoadPersistedSelections 的 finally 块会重置此标志。
        _isLoadingPersistedSelections = true;

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

        // 初次进入时枚举一遍 CAN 适配器。为避免首次进页 1~2s UI 阻塞（DLL 搜索 + 多设备 ProbeDevice），
        // 以 fire-and-forget 方式启动后台枚举；UI 可绑定 IsRefreshingCanAdapters 显示加载动画。
        _ = RefreshCanAdaptersAsync();

        // LoadPersistedSelections 内部会再次设 _isLoadingPersistedSelections = true，
        // 并在 finally 中将其还原为 false，一并解除上面设置的屏蔽。
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

            // Modbus / CANopen 记忆
            if (s.DeviceAdd_ModbusSlaveAddress >= 1 && s.DeviceAdd_ModbusSlaveAddress <= 247)
                ModbusSlaveAddress = s.DeviceAdd_ModbusSlaveAddress;
            if (s.DeviceAdd_CanopenNodeId >= 1 && s.DeviceAdd_CanopenNodeId <= 127)
                CanopenNodeId = s.DeviceAdd_CanopenNodeId;
            if (s.DeviceAdd_CanopenBitrateIndex >= 0 && s.DeviceAdd_CanopenBitrateIndex < ComboBoxCanBitrateFamilies.Count)
                ComboBoxCanBitrateSelect = s.DeviceAdd_CanopenBitrateIndex;

            // 标签页索引（0~3，超出则保持 0）
            if (s.DeviceAdd_ActiveTabIndex >= 0 && s.DeviceAdd_ActiveTabIndex <= 3)
                DeviceAddTabIndex = s.DeviceAdd_ActiveTabIndex;

            // CAN 适配器：按 Kind + Identifier 匹配（枚举已在 RefreshCanAdapters 中完成）
            if (!string.IsNullOrEmpty(s.DeviceAdd_CanAdapterKind)
                && Enum.TryParse<CanAdapterKind>(s.DeviceAdd_CanAdapterKind, out CanAdapterKind savedKind))
            {
                for (int i = 0; i < CanAdapterFamilies.Count; i++)
                {
                    if (CanAdapterFamilies[i].Kind == savedKind
                        && CanAdapterFamilies[i].Identifier == s.DeviceAdd_CanAdapterIdentifier)
                    {
                        CanAdapterSelect = i;
                        break;
                    }
                }
            }

            // USB 持久化
            if (s.DeviceAdd_UsbWorkModeIndex >= 0 && s.DeviceAdd_UsbWorkModeIndex <= 2)
                UsbWorkModeSelect = s.DeviceAdd_UsbWorkModeIndex;
            if (!string.IsNullOrEmpty(s.DeviceAdd_UsbVidPidWhitelistText))
                UsbVidPidWhitelistText = s.DeviceAdd_UsbVidPidWhitelistText;
            UsbFilterByVidPid = s.DeviceAdd_UsbFilterByVidPid;
            UsbFilterByClass = s.DeviceAdd_UsbFilterByClass;
            UsbFilterByKeyword = s.DeviceAdd_UsbFilterByKeyword;
            // 设备列表枚举（首次加载）。InstanceId 选回逻辑在 RefreshUsbDevices 内完成。
            RefreshUsbDevices();
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

            s.DeviceAdd_ModbusSlaveAddress = Math.Clamp(ModbusSlaveAddress, 1, 247);
            s.DeviceAdd_CanopenNodeId = Math.Clamp(CanopenNodeId, 1, 127);
            s.DeviceAdd_CanopenBitrateIndex = ComboBoxCanBitrateSelect;

            s.DeviceAdd_ActiveTabIndex = Math.Clamp(DeviceAddTabIndex, 0, 3);

            // CAN 适配器 Kind + Identifier
            if (CanAdapterSelect >= 0 && CanAdapterSelect < CanAdapterFamilies.Count)
            {
                s.DeviceAdd_CanAdapterKind = CanAdapterFamilies[CanAdapterSelect].Kind.ToString();
                s.DeviceAdd_CanAdapterIdentifier = CanAdapterFamilies[CanAdapterSelect].Identifier;
            }

            // USB 字段
            if (UsbDeviceSelect >= 0 && UsbDeviceSelect < UsbDevices.Count)
                s.DeviceAdd_UsbInstanceId = UsbDevices[UsbDeviceSelect].InstanceId;
            s.DeviceAdd_UsbWorkModeIndex = Math.Clamp(UsbWorkModeSelect, 0, 2);
            s.DeviceAdd_UsbVidPidWhitelistText = UsbVidPidWhitelistText ?? string.Empty;
            s.DeviceAdd_UsbFilterByVidPid = UsbFilterByVidPid;
            s.DeviceAdd_UsbFilterByClass = UsbFilterByClass;
            s.DeviceAdd_UsbFilterByKeyword = UsbFilterByKeyword;

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
    partial void OnModbusSlaveAddressChanged(int value) => PersistSelections();
    partial void OnCanopenNodeIdChanged(int value) => PersistSelections();
    partial void OnComboBoxCanBitrateSelectChanged(int value) => PersistSelections();
    partial void OnCanAdapterSelectChanged(int value)
    {
        PersistSelections();
        UpdateCanAdapterOptionLists();
        OnPropertyChanged(nameof(IsCanSerialPortVisible));
    }
    partial void OnDeviceAddTabIndexChanged(int value) => PersistSelections();

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
        StopModbusSlavePolling();
        StopCanOpenSlavePolling();
        try { _zlgScanCts?.Cancel(); } catch { /* ignore */ }
        try { _zlgScanCts?.Dispose(); } catch { /* ignore */ }
        try { _slaveStateTimer?.Stop(); } catch { /* ignore */ }
        try { portSearcher?.Dispose(); } catch { /* ignore */ }
        try { timer10ms?.Dispose(); } catch { /* ignore */ }
        try { _modbusMaster.Dispose(); } catch { /* ignore */ }
        try { CleanupCanOpen(); } catch { /* ignore */ }
        try { _usbMaster?.Dispose(); } catch { /* ignore */ }
        try { ecatMaster.Dispose(); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
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