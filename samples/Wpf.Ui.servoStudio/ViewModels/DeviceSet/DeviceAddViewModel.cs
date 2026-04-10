// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

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
using Wpf.Ui.servoStudio.Models;

namespace Wpf.Ui.servoStudio.ViewModels.DeviceSet;

public partial class DeviceAddViewModel(IContentDialogService contentDialogService, INavigationService navigationService) : ViewModel
{
    private static System.Threading.Timer portSearcher;
    private static System.Threading.Timer timer10ms;
    private string slaveState;
    private string[] _portNamesOld;
    private bool isPortNameChanged = false;
    private bool _isInitialized = false;
    private EtherCATMaster ecatMaster = new EtherCATMaster();
    private EtherCATSlave_CiA402? _axis;

    public EtherCATMaster EcatMaster => ecatMaster;
    public EtherCATSlave_CiA402? CurrentAxis => _axis;
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
    }

    partial void OnIsBusyChanged(bool value)
    {
        EthernetConfirmCommand.NotifyCanExecuteChanged();
        EthernetDisconnectCommand.NotifyCanExecuteChanged();
    }

    private bool CanEthernetConfirm() => !IsEthernetConnected && !IsBusy;

    private bool CanEthernetDisconnect() => IsEthernetConnected && !IsBusy;

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

        foreach (var nic in nics)
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
            var result = await Task.Run(() =>
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
        }
        catch (Exception ex)
        {
            EthernetStatusText = $"断开失败: {ex.Message}";
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
            IsEthernetLinkFailedVisible = Visibility.Visible;
        }
        finally
        {

        }
    }

    /// <summary>
    /// 启动从站状态轮询定时器（每 500ms 读取一次从站状态）
    /// </summary>
    private void StartSlaveStatePolling()
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
    private void StopSlaveStatePolling()
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
        catch
        {
            // 轮询读取失败时静默忽略，避免干扰用户操作
        }
    }

    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            InitializeViewModel();           
        }
    }

    [RelayCommand]
    private void OnDeviceConnect(Type type)
    {
        IsTestText = ComboBoxStopBitSelect.ToString();
        timer10ms = new System.Threading.Timer(new TimerCallback(this.timer10ms_Tick), null, 0, 15);
        timer10ms.Change(0, 10);
        if (!vcom.IsOpen)
        {
            vcom.PortName = ComboBoxPortNameFamilies[ComboBoxPortNameSelect];
            vcom.BaudRate = Convert.ToInt32(ComboBoxBaudFamilies[ComboBoxBaudSelect]);
            vcom.DataBits = Convert.ToInt32(ComboBoxDataBitFamilies[ComboBoxDataBitSelect]);
            switch (ComboBoxCheckBitFamilies[ComboBoxCheckBitSelect])
            {
                case "None":
                    vcom.Parity = Parity.None;
                    break;
                case "Odd":
                    vcom.Parity = Parity.Odd;
                    break;
                case "Even":
                    vcom.Parity = Parity.Even;
                    break;
            }

            switch (ComboBoxStopBitFamilies[ComboBoxStopBitSelect])
            {
                case "1":
                    vcom.StopBits = StopBits.One;
                    break;
                case "1.5":
                    vcom.StopBits = StopBits.One5;
                    break;
                case "2":
                    vcom.StopBits = StopBits.Two;
                    break;                
            }

            vcom.ReadTimeout = 500;
            vcom.WriteTimeout = 500;
            try
            {
                vcom.Open();
                if (vcom.IsOpen)
                {
                    vcom.NewLine = "/r/n";
                    vcom.RtsEnable = true;
                }

                IsLinkSucceed = true;
                IsLinkFailed = false;
                IsLinkSucceedVisible = Visibility.Visible;
                IsLinkFailedVisible = Visibility.Hidden;
                
            }
            catch (Exception ex)
            {
                IsLinkFailed = true;
                IsLinkSucceed = false;
                IsLinkFailedVisible = Visibility.Visible;
                IsLinkSucceedVisible = Visibility.Hidden;
                return;
            }

            OnThreadStart();
            _ = navigationService.Navigate(type);
            State = StateEnum.CheckDeviceInfo;
        }
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
        portSearcher.Change(0, 200);
        ComboBoxDataBitSelect = 0;
        ComboBoxStopBitSelect = 0;
        ComboBoxCheckBitSelect = 0;
        ComboBoxBaudSelect = 6;
        ComboBoxPortNameSelect = 0;
    }

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

                ComboBoxPortNameSelect = 0;
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
        if(ProgressbarValue >= 100)
        {
            ProgressbarValue = 100;
            timer10ms.Dispose();
        }
    }
}
