// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Core.Modbus;
using Core.Usb;
using Wpf.Ui.servoStudio.Core.Sync;
using Wpf.Ui.servoStudio.Models;

namespace Wpf.Ui.servoStudio.ViewModels.DeviceSet;

/// <summary>
/// 总线诊断 ViewModel：通过周期性向当前协议栈下发轻量级请求，统计通信成功率、
/// 往返时延、错误分布等指标，以判断总线连接稳定性与通信质量。<br/>
/// 当前支持：
/// <list type="bullet">
///   <item><description>EtherCAT —— 周期性 <c>ReadState(slaveAddr)</c> + 可选 SDO 0x6041 状态字读取</description></item>
///   <item><description>Modbus RTU —— 周期性 <c>TryReadStatusWord</c>，按 <see cref="ModbusExceptionCode"/> 细分错误</description></item>
/// </list>
/// CANopen 暂不参与诊断。
/// </summary>
public partial class BusDiagnosticsViewModel : ViewModel
{
    private readonly DeviceAddViewModel _deviceAdd;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _isInitialized;

    public BusDiagnosticsViewModel(DeviceAddViewModel deviceAdd)
    {
        _deviceAdd = deviceAdd;
        LoadSettings();
    }

    // ===== 自动记忆：IntervalMs / ProbeSdo =====

    private bool _isLoadingSettings;

    private void LoadSettings()
    {
        _isLoadingSettings = true;
        try
        {
            Services.UserSettings s = Services.UserSettingsService.Load();
            IntervalMs = Math.Max(1, s.BusDiagnostics_IntervalMs);
            ProbeSdo = s.BusDiagnostics_ProbeSdo;
            UsbHighSpeedMode = s.BusDiagnostics_UsbHighSpeedMode;
        }
        catch
        {
            // 读取失败保留默认值
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void SaveSettings()
    {
        if (_isLoadingSettings) return;
        try
        {
            Services.UserSettings s = Services.UserSettingsService.Load();
            s.BusDiagnostics_IntervalMs = IntervalMs;
            s.BusDiagnostics_ProbeSdo = ProbeSdo;
            s.BusDiagnostics_UsbHighSpeedMode = UsbHighSpeedMode;
            Services.UserSettingsService.Save(s);
        }
        catch
        {
            // 写入失败不影响业务
        }
    }

    partial void OnIntervalMsChanged(int value) => SaveSettings();

    partial void OnProbeSdoChanged(bool value) => SaveSettings();
    /// <summary>原始 DeviceAdd VM，便于 XAML 直接绑定连接概览信息。</summary>
    public DeviceAddViewModel DeviceAdd => _deviceAdd;

    // ───────── 控制类属性 ─────────

    /// <summary>诊断循环是否在运行。</summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>每次诊断请求之间的间隔（毫秒），默认 5 ms。为保证轮询周期 &lt; 5ms。</summary>
    [ObservableProperty]
    private int _intervalMs = 5;

    /// <summary>是否同时探测 SDO（仅 EtherCAT）；为 false 时只调用 ReadState。</summary>
    [ObservableProperty]
    private bool _probeSdo = true;

    /// <summary>状态文本（运行中/已停止/未连接 等）。</summary>
    [ObservableProperty]
    private string _statusText = "未启动";

    /// <summary>当前正在诊断的协议栈名称（EtherCAT / Modbus / 无）。</summary>
    [ObservableProperty]
    private string _activeProtocolText = "无";

    // ───────── 总体统计 ─────────

    [ObservableProperty]
    private long _testCount;

    [ObservableProperty]
    private long _successCount;

    [ObservableProperty]
    private long _failureCount;

    [ObservableProperty]
    private double _successRatePercent;

    [ObservableProperty]
    private double _avgLatencyMs;

    [ObservableProperty]
    private double _minLatencyMs;

    [ObservableProperty]
    private double _maxLatencyMs;

    // —— 从机应答延时（主机发送结束 → 从机首字节到达），仅 Modbus 有效：EtherCAT 由 SOEM 接管不可见。
    [ObservableProperty]
    private double _avgResponseLatencyMs;

    [ObservableProperty]
    private double _minResponseLatencyMs;

    [ObservableProperty]
    private double _maxResponseLatencyMs;

    [ObservableProperty]
    private double _lastResponseLatencyMs;

    /// <summary>只统计成功 Modbus 事务的应答延时样本数，默认 0。</summary>
    private long _responseLatencySampleCount;
    private double _responseLatencySumMs;

    [ObservableProperty]
    private string _lastErrorText = string.Empty;

    /// <summary>累计往返时延（用于在线计算均值）。</summary>
    private double _latencySumMs;

    // ───────── Modbus 全寄存器轮询状态 ─────────

    /// <summary>当前轮询到 HVariables.RegisterTable 的索引（round-robin）。</summary>
    private int _modbusPollIndex;
    /// <summary>本次轮询的 Modbus 寄存器原始地址。</summary>
    private ushort _lastModbusPollAddr;
    /// <summary>本次轮询的寄存器描述（HIndex + 参数名）。</summary>
    private string _lastModbusPollName = string.Empty;
    /// <summary>本次轮询成功读回的原始值（uint16）。</summary>
    private ushort _lastModbusPollValue;

    // ───────── Modbus 错误细分 ─────────

    [ObservableProperty]
    private long _modbusTimeoutCount;

    [ObservableProperty]
    private long _modbusCrcErrorCount;

    [ObservableProperty]
    private long _modbusInvalidResponseCount;

    [ObservableProperty]
    private long _modbusProtocolErrorCount;   // IllegalFunction/Address/Value 等

    [ObservableProperty]
    private long _modbusOtherErrorCount;

    // ───────── EtherCAT 状态信息 ─────────

    /// <summary>最近一次 EtherCAT ReadState 返回的从站状态字符串。</summary>
    [ObservableProperty]
    private string _etherCatLastState = string.Empty;

    /// <summary>EtherCAT 连续掉帧（state=None / 异常）次数。</summary>
    [ObservableProperty]
    private long _etherCatLostCount;

    // ───────── EtherCAT ESC 寄存器诊断（via FPRD）─────────

    /// <summary>最近一次读到的 AL Status 原始值（ESC 寄存器 0x0130）。</summary>
    [ObservableProperty]
    private ushort _ecatAlStatus;

    /// <summary>AL Status 解码后的状态字符串（如 "Op"、"Safe-Op  [!]"）。诊断未启动时为空。</summary>
    [ObservableProperty]
    private string _ecatAlStateText = string.Empty;

    /// <summary>AL Status Bit4 错误指示；true = 当前 AL 状态寄存器中有错误标志位。</summary>
    [ObservableProperty]
    private bool _ecatAlError;

    /// <summary>AL Status Code 原始值（ESC 寄存器 0x0134）。0x0000 = 无错误。</summary>
    [ObservableProperty]
    private ushort _ecatAlStatusCode;

    /// <summary>AL Status Code 的中文说明。</summary>
    [ObservableProperty]
    private string _ecatAlStatusCodeText = string.Empty;

    // ── 端口错误计数（显示自本次诊断 / 上次 Reset 以来的增量）──

    /// <summary>端口 A 帧格式/CRC 错误增量（ESC 0x0300 高字节）。</summary>
    [ObservableProperty]
    private uint _ecatInvFrameA;

    /// <summary>端口 A 物理层信号错误增量（ESC 0x0300 低字节）。</summary>
    [ObservableProperty]
    private uint _ecatRxErrA;

    /// <summary>端口 B 帧格式/CRC 错误增量（ESC 0x0302 高字节）。</summary>
    [ObservableProperty]
    private uint _ecatInvFrameB;

    /// <summary>端口 B 物理层信号错误增量（ESC 0x0302 低字节）。</summary>
    [ObservableProperty]
    private uint _ecatRxErrB;

    /// <summary>ECAT 处理单元错误增量（ESC 0x030C）。</summary>
    [ObservableProperty]
    private uint _ecatProcErr;

    /// <summary>PDI 错误增量（ESC 0x030D）。</summary>
    [ObservableProperty]
    private uint _ecatPdiErr;

    /// <summary>端口 A 链路丢失增量（ESC 0x0310）。</summary>
    [ObservableProperty]
    private uint _ecatLinkLostA;

    /// <summary>端口 B 链路丢失增量（ESC 0x0311）。</summary>
    [ObservableProperty]
    private uint _ecatLinkLostB;

    // ── ESC 基准值（开始诊断或 Reset 时记录，用于计算增量）──
    private byte _baseInvFrameA, _baseRxErrA;
    private byte _baseInvFrameB, _baseRxErrB;
    private byte _baseProcErr, _basePdiErr;
    private byte _baseLinkLostA, _baseLinkLostB;
    private bool _ecatEscBaselineSet;

    // ───────── USB 诊断 ─────────

    [ObservableProperty] private long _usbTxCount;
    [ObservableProperty] private long _usbRxCount;
    [ObservableProperty] private long _usbErrorCount;
    [ObservableProperty] private string _usbLastErrorText = string.Empty;
    [ObservableProperty] private string _usbLastTxFrameHex = string.Empty;
    [ObservableProperty] private string _usbLastTxTimestamp = string.Empty;
    [ObservableProperty] private string _usbLastRxFrameHex = string.Empty;
    [ObservableProperty] private string _usbLastRxTimestamp = string.Empty;

    // ───────── USB 故障分类 ─────────

    /// <summary>发送失败中 Win32=0x79（ERROR_SEM_TIMEOUT / NAK 超时）的累计次数。</summary>
    [ObservableProperty] private long _usbSendFailGle0x79Count;

    /// <summary>发送失败中非 0x79 错误码的累计次数。</summary>
    [ObservableProperty] private long _usbSendFailGleOtherCount;

    /// <summary>推断的故障分类短文本；无故障时为"正常"。</summary>
    [ObservableProperty] private string _usbFaultCategoryText = "正常";

    /// <summary>故障排查提示；无故障时为空字符串。</summary>
    [ObservableProperty] private string _usbFaultHintText = string.Empty;

    /// <summary>当前是否存在 USB 发送故障，驱动页面故障提示区域的可见性。</summary>
    [ObservableProperty] private bool _usbHasFault;

    // ───────── USB 接收故障分类 ─────────

    /// <summary>推断的接收故障分类短文本；无故障时为"正常"。</summary>
    [ObservableProperty] private string _usbRxFaultCategoryText = "正常";

    /// <summary>接收故障排查提示；无故障时为空字符串。</summary>
    [ObservableProperty] private string _usbRxFaultHintText = string.Empty;

    /// <summary>当前是否存在 USB 接收故障（丢包 / 接收错误 / 无回应）。</summary>
    [ObservableProperty] private bool _usbRxHasFault;

    private UsbMaster? _subscribedUsbMaster;

    // 发送 / 接收帧显示各自独立的节流计时，避免高频发送占满节流窗口导致接收帧几乎不刷新。
    private DateTime _usbLastTxDisplayTime = DateTime.MinValue;
    private DateTime _usbLastRxDisplayTime = DateTime.MinValue;

    // 最近一次收到的帧缓存：始终保留，节流仅控制刷新到 UI 的频率，确保不会被清空。
    private string _usbPendingRxFrameHex = string.Empty;
    private string _usbPendingRxTimestamp = string.Empty;
    private UsbChannel _usbPendingRxChannel;
    private ushort _usbPendingRxSequence;
    private bool _usbHasPendingRxFrame;

    // ───────── USB 回环测试 ─────────

    [ObservableProperty] private bool _isUsbLoopbackRunning;
    [ObservableProperty] private int _usbLoopbackPacketSize = 64;
    [ObservableProperty] private int _usbLoopbackIntervalMs = 10;
    [ObservableProperty] private long _usbLoopbackSentCount;
    [ObservableProperty] private long _usbLoopbackReceivedCount;
    [ObservableProperty] private long _usbLoopbackErrorCount;
    [ObservableProperty] private long _usbLoopbackDropCount;
    [ObservableProperty] private double _usbLoopbackAvgRttMs;
    [ObservableProperty] private double _usbLoopbackMinRttMs;
    [ObservableProperty] private double _usbLoopbackMaxRttMs;
    [ObservableProperty] private double _usbLoopbackLastRttMs;
    [ObservableProperty] private double _usbLoopbackThroughputMbps;

    /// <summary>上行吞吐量（MB/s）：从机→上位机（Device→Host），即回声接收方向。</summary>
    [ObservableProperty] private double _usbLoopbackUplinkThroughputMbps;

    /// <summary>下行吞吐量（MB/s）：上位机→从机（Host→Device），即发送方向。</summary>
    [ObservableProperty] private double _usbLoopbackDownlinkThroughputMbps;

    /// <summary>
    /// 带宽测量速率上限模式：false = USB 2.0 全速（默认），true = USB 2.0 高速。
    /// 该选择经 <see cref="Services.UserSettings.BusDiagnostics_UsbHighSpeedMode"/> 持久化。
    /// </summary>
    [ObservableProperty] private bool _usbHighSpeedMode;

    private UsbLoopbackTester? _loopbackTester;

    /// <summary>USB 2.0 全速理论带宽上限（MB/s）：12 Mbps ÷ 8 ≈ 1.5 MB/s。</summary>
    private const double UsbFullSpeedCeilingMBps = 1.5;

    /// <summary>USB 2.0 高速理论带宽上限（MB/s）：480 Mbps ÷ 8 = 60 MB/s。</summary>
    private const double UsbHighSpeedCeilingMBps = 60.0;

    // ───────── 诊断定时器选择 ─────────

    /// <summary>可选定时器类型列表，绑定到 ComboBox。</summary>
    public IReadOnlyList<string> DiagTimerKinds { get; } =
    [
        CyclicTimerFactory.DisplayName(CyclicTimerKind.PeriodicTimer),
        CyclicTimerFactory.DisplayName(CyclicTimerKind.WinMm),
        CyclicTimerFactory.DisplayName(CyclicTimerKind.SpinWait),
    ];

    /// <summary>当前选中的诊断定时器类型索引（0=PeriodicTimer, 1=WinMm, 2=SpinWait）。</summary>
    [ObservableProperty]
    private int _diagTimerKindIndex = 1;

    private CyclicTimerKind DiagTimerKind => (CyclicTimerKind)DiagTimerKindIndex;

    /// <summary>当前生效的带宽上限（MB/s），由 <see cref="UsbHighSpeedMode"/> 决定。</summary>
    public double UsbBandwidthCeilingMBps
        => UsbHighSpeedMode ? UsbHighSpeedCeilingMBps : UsbFullSpeedCeilingMBps;

    /// <summary>带宽上限的说明文本，用于 UI 参考基准提示。</summary>
    public string UsbBandwidthCeilingText
        => UsbHighSpeedMode
            ? "USB 2.0 高速（480 Mbps ≈ 60 MB/s）"
            : "USB 2.0 全速（12 Mbps ≈ 1.5 MB/s）";

    partial void OnUsbHighSpeedModeChanged(bool value)
    {
        SaveSettings();
        OnPropertyChanged(nameof(UsbBandwidthCeilingMBps));
        OnPropertyChanged(nameof(UsbBandwidthCeilingText));
        OnPropertyChanged(nameof(UsbLoopbackThroughputPercent));
        OnPropertyChanged(nameof(UsbLoopbackUplinkThroughputPercent));
        OnPropertyChanged(nameof(UsbLoopbackDownlinkThroughputPercent));
    }

    /// <summary>回环测试成功率（百分比）。</summary>
    public double UsbLoopbackSuccessRatePercent
        => UsbLoopbackSentCount > 0
            ? (double)UsbLoopbackReceivedCount / UsbLoopbackSentCount * 100.0
            : 0.0;

    /// <summary>
    /// 当前双向合计吞吐量占所选带宽上限的百分比。
    /// 仅供参考，因双向合计且包含协议头。
    /// </summary>
    public double UsbLoopbackThroughputPercent
        => Math.Min(100.0, UsbLoopbackThroughputMbps / UsbBandwidthCeilingMBps * 100.0);

    /// <summary>上行吞吐量占所选带宽上限的百分比。</summary>
    public double UsbLoopbackUplinkThroughputPercent
        => Math.Min(100.0, UsbLoopbackUplinkThroughputMbps / UsbBandwidthCeilingMBps * 100.0);

    /// <summary>下行吞吐量占所选带宽上限的百分比。</summary>
    public double UsbLoopbackDownlinkThroughputPercent
        => Math.Min(100.0, UsbLoopbackDownlinkThroughputMbps / UsbBandwidthCeilingMBps * 100.0);

    /// <summary>回环测试未在运行时为 true，用于配置控件的 IsEnabled 绑定。</summary>
    public bool IsUsbLoopbackNotRunning => !IsUsbLoopbackRunning;

    private void SubscribeUsb(UsbMaster? master)
    {
        if (_subscribedUsbMaster is not null)
        {
            _subscribedUsbMaster.PacketReceived -= OnUsbPacketReceived;
            _subscribedUsbMaster.PacketSent -= OnUsbPacketSent;
            _subscribedUsbMaster.ReceiveError -= OnUsbReceiveError;
            _subscribedUsbMaster.SendFailed -= OnUsbSendFailed;
        }
        _subscribedUsbMaster = master;
        if (master is not null)
        {
            master.PacketReceived += OnUsbPacketReceived;
            master.PacketSent += OnUsbPacketSent;
            master.ReceiveError += OnUsbReceiveError;
            master.SendFailed += OnUsbSendFailed;
        }
    }

    private void OnUsbPacketSent(UsbPacket pkt)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            UsbTxCount++;
            DateTime now = DateTime.UtcNow;
            if ((now - _usbLastTxDisplayTime).TotalMilliseconds >= TxFrameDisplayThrottleMs)
            {
                _usbLastTxDisplayTime = now;
                string hex = FormatHexFrame(pkt.Payload ?? []);
                string ts = DateTime.Now.ToString("HH:mm:ss.fff");
                UsbLastTxFrameHex = hex;
                UsbLastTxTimestamp = ts;
                LastTxFrameHex = hex;
                LastTxFrameDesc = $"USB Ch=0x{(ushort)pkt.Channel:X4} Seq={pkt.Sequence}";
                LastFrameTimestamp = ts;
            }
        });
    }

    private void OnUsbPacketReceived(UsbPacket pkt)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            UsbRxCount++;

            // 始终缓存最近一次收到的帧，确保接收帧显示不会被清空。
            _usbPendingRxFrameHex = FormatHexFrame(pkt.Payload ?? []);
            _usbPendingRxTimestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            _usbPendingRxChannel = pkt.Channel;
            _usbPendingRxSequence = pkt.Sequence;
            _usbHasPendingRxFrame = true;

            // 节流仅控制刷新到 UI 的频率：到达节流窗口即刷新一次。
            DateTime now = DateTime.UtcNow;
            if ((now - _usbLastRxDisplayTime).TotalMilliseconds >= RxFrameDisplayThrottleMs)
            {
                FlushPendingUsbRxFrame(now);
            }
        });
    }

    /// <summary>
    /// 将缓存的最近接收帧刷新到绑定属性。无论是否处于节流窗口，缓存的帧都不会丢失；
    /// 周期性统计回调也会调用此方法，确保最后一帧最终一定能显示出来。
    /// </summary>
    private void FlushPendingUsbRxFrame(DateTime nowUtc)
    {
        if (!_usbHasPendingRxFrame)
        {
            return;
        }

        _usbLastRxDisplayTime = nowUtc;
        UsbLastRxFrameHex = _usbPendingRxFrameHex;
        UsbLastRxTimestamp = _usbPendingRxTimestamp;
        LastRxFrameHex = _usbPendingRxFrameHex;
        LastRxFrameDesc = $"USB Ch=0x{(ushort)_usbPendingRxChannel:X4} Seq={_usbPendingRxSequence}";
        if (string.IsNullOrEmpty(LastFrameTimestamp))
        {
            LastFrameTimestamp = _usbPendingRxTimestamp;
        }

        _usbHasPendingRxFrame = false;
    }

    private void OnUsbReceiveError(Exception ex)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            UsbErrorCount++;
            UsbLastErrorText = $"[{DateTime.Now:HH:mm:ss}] {ex.Message}";
            UpdateUsbRxFaultClassification();
        });
    }

    private void OnUsbSendFailed(int gle)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            // 按 Win32 错误码细分：0x79=121 为 ERROR_SEM_TIMEOUT（NAK 超时 / FIFO 满最常见的表现）
            if (gle == 121)
            {
                UsbSendFailGle0x79Count++;
            }
            else
            {
                UsbSendFailGleOtherCount++;
            }

            UsbLastErrorText = gle == 0
                ? $"[{DateTime.Now:HH:mm:ss}] Send 失败（未知错误）"
                : $"[{DateTime.Now:HH:mm:ss}] Send 失败：Win32={gle} (0x{gle:X})";

            UpdateUsbFaultClassification();
        });
    }

    /// <summary>
    /// 根据当前 USB 发送失败计数与回环测试统计，推断故障类别并更新提示文本。<br/>
    /// 特征匹配规则：<br/>
    /// - 存在 0x79 超时 + 回环有发包 + 从未成功回声（ReceivedCount==0）→ 从机 BULK OUT 未服务<br/>
    /// - 存在 0x79 超时但无法确认 FIFO 满模式 → 通用发送超时<br/>
    /// - 仅有非 0x79 失败 → 其他发送错误
    /// </summary>
    private void UpdateUsbFaultClassification()
    {
        bool hasFifoFullPattern =
            UsbSendFailGle0x79Count > 0
            && UsbLoopbackSentCount > 0
            && UsbLoopbackDropCount > 0
            && UsbLoopbackReceivedCount == 0;

        if (hasFifoFullPattern)
        {
            UsbFaultCategoryText = "从机 BULK OUT 未服务（FIFO 满 / NAK 超时）";
            UsbFaultHintText =
                "从机固件未调用首次接收 transfer_request，或读完成回调中未重新 arm。" +
                "主机发出的包已进入从机硬件 FIFO，但固件从未取走，FIFO 满后主机 WritePipe 持续超时。" +
                "参见调试日志 docs/usb-bulk-out-fifo-fault.md。";
            UsbHasFault = true;
        }
        else if (UsbSendFailGle0x79Count > 0)
        {
            UsbFaultCategoryText = "发送超时（Win32=0x79 / ERROR_SEM_TIMEOUT）";
            UsbFaultHintText =
                "发送端点持续 NAK 或 pipe 进入 stall 状态。" +
                "建议检查：从机 BULK OUT 端点是否被 stall、WinUSB 管道策略（AUTO_CLEAR_STALL）是否启用。";
            UsbHasFault = true;
        }
        else if (UsbSendFailGleOtherCount > 0)
        {
            UsbFaultCategoryText = $"其他发送错误（{UsbSendFailGleOtherCount} 次）";
            UsbFaultHintText = "发生非超时类发送失败，查看最近错误文本中的 Win32 错误码。";
            UsbHasFault = true;
        }
        else
        {
            UsbFaultCategoryText = "正常";
            UsbFaultHintText = string.Empty;
            UsbHasFault = false;
        }
    }

    /// <summary>
    /// 根据接收错误数与回环丢包数，推断接收故障类别并更新提示文本。<br/>
    /// - 有接收错误 + 从未成功接收（TxCount>0 且 RxCount=0）→ 从机 BULK IN 未服务<br/>
    /// - 仅有接收错误 → 接收异常（物理层 / pipe stall）<br/>
    /// - 回环丢包 → 从机未在超时期内回送
    /// </summary>
    private void UpdateUsbRxFaultClassification()
    {
        if (UsbErrorCount > 0 && UsbTxCount > 0 && UsbRxCount == 0)
        {
            UsbRxFaultCategoryText = "无回应（接收全错误）";
            UsbRxFaultHintText =
                "主机已发送数据但从未成功接收任何数据，且接收端点持续报错。" +
                "建议检查从机 BULK IN 端点是否正确 arm，以及接收缓冲区大小是否 ≥ wMaxPacketSize（64 B）。";
            UsbRxHasFault = true;
        }
        else if (UsbErrorCount > 0)
        {
            UsbRxFaultCategoryText = $"接收异常（{UsbErrorCount} 次错误）";
            UsbRxFaultHintText =
                "接收端点发生错误，可能为物理层 CRC / 帧格式问题，或 BULK IN pipe 进入 stall 状态。" +
                "建议检查 USB 线缆、接收缓冲区对齐，以及是否启用 AUTO_CLEAR_STALL 管道策略。";
            UsbRxHasFault = true;
        }
        else if (UsbLoopbackDropCount > 0)
        {
            UsbRxFaultCategoryText = $"回环丢包（{UsbLoopbackDropCount} 包）";
            UsbRxFaultHintText =
                "回环测试检测到丢包：从机未在超时期内将数据回送 Loopback 通道。" +
                "建议检查从机 Loopback task 优先级、USB 回调处理时间，以及 IntervalMs 设置是否过小。";
            UsbRxHasFault = true;
        }
        else
        {
            UsbRxFaultCategoryText = "正常";
            UsbRxFaultHintText = string.Empty;
            UsbRxHasFault = false;
        }
    }

    private bool CanStartUsbLoopback() => _deviceAdd.IsUsbConnected && !IsUsbLoopbackRunning;

    private bool CanStopUsbLoopback() => IsUsbLoopbackRunning;

    [RelayCommand(CanExecute = nameof(CanStartUsbLoopback))]
    private void StartUsbLoopback()
    {
        UsbMaster? master = _deviceAdd.UsbMaster;
        if (master is null)
        {
            return;
        }

        _loopbackTester?.Dispose();
        _loopbackTester = new UsbLoopbackTester(master)
        {
            PacketSize = Math.Clamp(UsbLoopbackPacketSize, 14, 512),
            IntervalMs = Math.Max(0, UsbLoopbackIntervalMs),
            TimerKind = DiagTimerKind,
        };
        _loopbackTester.StatsUpdated += OnLoopbackStatsUpdated;
        _loopbackTester.Start();
        IsUsbLoopbackRunning = true;
        OnPropertyChanged(nameof(IsUsbLoopbackNotRunning));
        StartUsbLoopbackCommand.NotifyCanExecuteChanged();
        StopUsbLoopbackCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStopUsbLoopback))]
    private void StopUsbLoopback()
    {
        _loopbackTester?.Stop();
        IsUsbLoopbackRunning = false;
        OnPropertyChanged(nameof(IsUsbLoopbackNotRunning));
        StartUsbLoopbackCommand.NotifyCanExecuteChanged();
        StopUsbLoopbackCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ResetUsbLoopback()
    {
        if (_loopbackTester is not null)
        {
            _loopbackTester.Reset();
        }
        else
        {
            UsbLoopbackSentCount = 0;
            UsbLoopbackReceivedCount = 0;
            UsbLoopbackErrorCount = 0;
            UsbLoopbackDropCount = 0;
            UsbLoopbackAvgRttMs = 0;
            UsbLoopbackMinRttMs = 0;
            UsbLoopbackMaxRttMs = 0;
            UsbLoopbackLastRttMs = 0;
            UsbLoopbackThroughputMbps = 0;
            UsbLoopbackUplinkThroughputMbps = 0;
            UsbLoopbackDownlinkThroughputMbps = 0;
            OnPropertyChanged(nameof(UsbLoopbackSuccessRatePercent));
            OnPropertyChanged(nameof(UsbLoopbackThroughputPercent));
            OnPropertyChanged(nameof(UsbLoopbackUplinkThroughputPercent));
            OnPropertyChanged(nameof(UsbLoopbackDownlinkThroughputPercent));
        }
    }

    private void OnLoopbackStatsUpdated()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_loopbackTester is null)
            {
                return;
            }

            UsbLoopbackSentCount = _loopbackTester.SentCount;
            UsbLoopbackReceivedCount = _loopbackTester.ReceivedCount;
            UsbLoopbackErrorCount = _loopbackTester.ErrorCount;
            UsbLoopbackDropCount = _loopbackTester.DropCount;
            UsbLoopbackAvgRttMs = _loopbackTester.AvgRttMs;
            UsbLoopbackMinRttMs = _loopbackTester.MinRttMs;
            UsbLoopbackMaxRttMs = _loopbackTester.MaxRttMs;
            UsbLoopbackLastRttMs = _loopbackTester.LastRttMs;
            UsbLoopbackThroughputMbps = _loopbackTester.ThroughputMbps;
            UsbLoopbackUplinkThroughputMbps = _loopbackTester.UplinkThroughputMbps;
            UsbLoopbackDownlinkThroughputMbps = _loopbackTester.DownlinkThroughputMbps;
            OnPropertyChanged(nameof(UsbLoopbackSuccessRatePercent));
            OnPropertyChanged(nameof(UsbLoopbackThroughputPercent));
            OnPropertyChanged(nameof(UsbLoopbackUplinkThroughputPercent));
            OnPropertyChanged(nameof(UsbLoopbackDownlinkThroughputPercent));
            UpdateUsbRxFaultClassification();

            // 周期性刷新缓存的最近接收帧，确保流量停止后最后一帧也能显示出来。
            FlushPendingUsbRxFrame(DateTime.UtcNow);

            if (!_loopbackTester.IsRunning && IsUsbLoopbackRunning)
            {
                IsUsbLoopbackRunning = false;
                OnPropertyChanged(nameof(IsUsbLoopbackNotRunning));
                StartUsbLoopbackCommand.NotifyCanExecuteChanged();
                StopUsbLoopbackCommand.NotifyCanExecuteChanged();
            }
        });
    }

    // ───────── 帧内容预览 ─────────
    [ObservableProperty]
    private string _lastTxFrameHex = string.Empty;

    /// <summary>最近一次接收帧的十六进制表示（Modbus）或响应描述（EtherCAT）。</summary>
    [ObservableProperty]
    private string _lastRxFrameHex = string.Empty;

    /// <summary>发送帧解析说明（功能码、地址等）。</summary>
    [ObservableProperty]
    private string _lastTxFrameDesc = string.Empty;

    /// <summary>接收帧解析说明（数据内容等）。</summary>
    [ObservableProperty]
    private string _lastRxFrameDesc = string.Empty;

    /// <summary>帧内容最近一次显示的时刻。</summary>
    [ObservableProperty]
    private string _lastFrameTimestamp = string.Empty;

    /// <summary>发送帧显示最低刷新间隔（ms）。</summary>
    private const int TxFrameDisplayThrottleMs = 500;

    /// <summary>接收帧显示最低刷新间隔（ms），比发送帧慢，避免高频回环时 RX 刷新过快。</summary>
    private const int RxFrameDisplayThrottleMs = 1000;

    private const int FrameDisplayThrottleMs = 500;
    private DateTime _lastFrameDisplayTime = DateTime.MinValue;

    // ───────── Debug 模式门控 ─────────

    /// <summary>是否处于 Debug 模式（由用户配置持久化）；仅在此模式下才采集并显示帧内容。</summary>
    [ObservableProperty]
    private bool _isDebugMode;

    /// <summary>通信质量指标面板是否展开；USB 单独连接时默认折叠，EtherCAT/Modbus 连接时展开。</summary>
    [ObservableProperty]
    private bool _isQualityPanelExpanded = true;

    public bool CanStart => !IsRunning && _deviceAdd.IsAnyConnected
        && (_deviceAdd.ActiveProtocol == ActiveProtocolStack.EtherCAT
            || _deviceAdd.ActiveProtocol == ActiveProtocolStack.Modbus);

    /// <summary>"EtherCAT 同时探测 SDO" 复选框的可交互性：仅当协议栈为 EtherCAT 时启用。</summary>
    public bool IsProbeSDOAvailable => _deviceAdd.ActiveProtocol == ActiveProtocolStack.EtherCAT;

    partial void OnIsRunningChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
    }

    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            _isInitialized = true;
        }

        ActiveProtocolText = _deviceAdd.ActiveProtocol switch
        {
            ActiveProtocolStack.EtherCAT => "EtherCAT",
            ActiveProtocolStack.Modbus => "Modbus RTU",
            ActiveProtocolStack.CANopen => "CANopen（暂不支持诊断）",
            _ => _deviceAdd.IsUsbConnected ? "USB（非控制协议栈）" : "未连接",
        };

        // 每次进页面都同步最新 Debug 开关，保证与用户配置一致
        IsDebugMode = Services.UserSettingsService.Load().IsDebugMode;

        // USB 单独连接（无 EtherCAT/Modbus 轮询）时自动折叠质量指标面板
        IsQualityPanelExpanded = _deviceAdd.IsAnyConnected;

        _deviceAdd.PropertyChanged += OnDeviceAddPropertyChanged;
        SubscribeUsb(_deviceAdd.UsbMaster);

        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(IsProbeSDOAvailable));
        StartUsbLoopbackCommand.NotifyCanExecuteChanged();
        StopUsbLoopbackCommand.NotifyCanExecuteChanged();
    }

    public override void OnNavigatedFrom()
    {
        _deviceAdd.PropertyChanged -= OnDeviceAddPropertyChanged;

        // 离开页面时自动停止诊断，避免后台占用串口/网卡。
        if (IsRunning)
        {
            _ = StopAsync();
        }

        // 停止回环测试并释放资源
        _loopbackTester?.Stop();
        _loopbackTester?.Dispose();
        _loopbackTester = null;
        IsUsbLoopbackRunning = false;
        OnPropertyChanged(nameof(IsUsbLoopbackNotRunning));

        SubscribeUsb(null);
    }

    private void OnDeviceAddPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceAddViewModel.IsUsbConnected))
        {
            StartUsbLoopbackCommand.NotifyCanExecuteChanged();
            StopUsbLoopbackCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartCmd))]
    private async Task OnStart()
    {
        if (IsRunning) return;

        if (!_deviceAdd.IsAnyConnected)
        {
            StatusText = "未检测到已连接设备，无法启动诊断";
            return;
        }

        if (_deviceAdd.ActiveProtocol != ActiveProtocolStack.EtherCAT
            && _deviceAdd.ActiveProtocol != ActiveProtocolStack.Modbus)
        {
            StatusText = $"协议栈 {_deviceAdd.ActiveProtocol} 暂不支持总线诊断";
            return;
        }

        ResetStats();
        _cts = new CancellationTokenSource();
        IsRunning = true;
        ActiveProtocolText = _deviceAdd.ActiveProtocol == ActiveProtocolStack.EtherCAT ? "EtherCAT" : "Modbus RTU";
        StatusText = $"诊断中（{ActiveProtocolText}）...";

        CancellationToken token = _cts.Token;
        _loopTask = Task.Run(() => DiagnosticLoop(token), token);
        await Task.Yield();
    }

    private bool CanStartCmd() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStopCmd))]
    private Task OnStop() => StopAsync();

    private bool CanStopCmd() => IsRunning;

    private async Task StopAsync()
    {
        // 不使用 ConfigureAwait(false)：await 之后必须回到 UI 线程，
        // 因为后续会写入 ObservableProperty / 调用 NotifyCanExecuteChanged，
        // 这些操作要求在创建 DispatcherObject 的线程（UI 线程）上完成，
        // 否则会抛出 “调用线程无法访问此对象” 的跨线程异常。
        try
        {
            _cts?.Cancel();
            if (_loopTask != null)
            {
                try { await _loopTask; }
                catch (OperationCanceledException) { /* expected */ }
                catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is OperationCanceledException))
                {
                    /* expected */
                }
            }
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _loopTask = null;
            IsRunning = false;
            StatusText = "已停止";
        }
    }

    [RelayCommand(CanExecute = nameof(CanResetCmd))]
    private void OnReset() => ResetStats();

    private bool CanResetCmd() => !IsRunning;

    private void ResetStats()
    {
        TestCount = 0;
        SuccessCount = 0;
        FailureCount = 0;
        SuccessRatePercent = 0;
        AvgLatencyMs = 0;
        MinLatencyMs = 0;
        MaxLatencyMs = 0;
        AvgResponseLatencyMs = 0;
        MinResponseLatencyMs = 0;
        MaxResponseLatencyMs = 0;
        LastResponseLatencyMs = 0;
        _responseLatencySampleCount = 0;
        _responseLatencySumMs = 0;
        _latencySumMs = 0;
        LastErrorText = string.Empty;

        ModbusTimeoutCount = 0;
        ModbusCrcErrorCount = 0;
        ModbusInvalidResponseCount = 0;
        ModbusProtocolErrorCount = 0;
        ModbusOtherErrorCount = 0;

        EtherCatLastState = string.Empty;
        EtherCatLostCount = 0;

        EcatAlStatus = 0;
        EcatAlStateText = string.Empty;
        EcatAlError = false;
        EcatAlStatusCode = 0;
        EcatAlStatusCodeText = string.Empty;
        EcatInvFrameA = 0; EcatRxErrA = 0;
        EcatInvFrameB = 0; EcatRxErrB = 0;
        EcatProcErr = 0; EcatPdiErr = 0;
        EcatLinkLostA = 0; EcatLinkLostB = 0;
        _ecatEscBaselineSet = false;

        LastTxFrameHex = string.Empty;
        LastRxFrameHex = string.Empty;
        LastTxFrameDesc = string.Empty;
        LastRxFrameDesc = string.Empty;
        LastFrameTimestamp = string.Empty;
        _lastFrameDisplayTime = DateTime.MinValue;

        _modbusPollIndex = 0;
        _lastModbusPollAddr = 0;
        _lastModbusPollName = string.Empty;
        _lastModbusPollValue = 0;

        UsbTxCount = 0;
        UsbRxCount = 0;
        UsbErrorCount = 0;
        UsbLastErrorText = string.Empty;
        UsbLastTxFrameHex = string.Empty;
        UsbLastTxTimestamp = string.Empty;
        UsbLastRxFrameHex = string.Empty;
        UsbLastRxTimestamp = string.Empty;
        _usbLastTxDisplayTime = DateTime.MinValue;
        _usbLastRxDisplayTime = DateTime.MinValue;
        _usbHasPendingRxFrame = false;
        _usbPendingRxFrameHex = string.Empty;
        _usbPendingRxTimestamp = string.Empty;

        UsbSendFailGle0x79Count = 0;
        UsbSendFailGleOtherCount = 0;
        UsbFaultCategoryText = "正常";
        UsbFaultHintText = string.Empty;
        UsbHasFault = false;

        UsbRxFaultCategoryText = "正常";
        UsbRxFaultHintText = string.Empty;
        UsbRxHasFault = false;

        UsbLoopbackSentCount = 0;
        UsbLoopbackReceivedCount = 0;
        UsbLoopbackErrorCount = 0;
        UsbLoopbackDropCount = 0;
        UsbLoopbackAvgRttMs = 0;
        UsbLoopbackMinRttMs = 0;
        UsbLoopbackMaxRttMs = 0;
        UsbLoopbackLastRttMs = 0;
        UsbLoopbackThroughputMbps = 0;
        OnPropertyChanged(nameof(UsbLoopbackSuccessRatePercent));
        OnPropertyChanged(nameof(UsbLoopbackThroughputPercent));
    }

    // ────────────────────────────────────────────────────────
    //  诊断循环
    // ────────────────────────────────────────────────────────

    private void DiagnosticLoop(CancellationToken token)
    {
        Stopwatch sw = new();
        while (!token.IsCancellationRequested)
        {
            ActiveProtocolStack proto = _deviceAdd.ActiveProtocol;
            (bool ok, double elapsedMs, string? err) result;

            try
            {
                if (proto == ActiveProtocolStack.EtherCAT)
                {
                    result = ProbeEtherCAT(sw);
                    TryUpdateFrameDisplay(() => BuildEcatFrameDisplay(proto, result.ok, result.err));
                }
                else if (proto == ActiveProtocolStack.Modbus)
                {
                    result = ProbeModbus(sw);
                    TryUpdateFrameDisplay(() => BuildModbusFrameDisplay(result.ok));
                }
                else
                {
                    // 协议栈在运行中被切走 / 断开，主动停止
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        StatusText = "协议栈已断开，诊断停止";
                    });
                    break;
                }
            }
            catch (Exception ex)
            {
                result = (false, 0, ex.Message);
            }

            ApplyResult(proto, result.ok, result.elapsedMs, result.err);

            int wait = Math.Max(1, IntervalMs);
            try { Task.Delay(wait, token).Wait(token); }
            catch (OperationCanceledException) { break; }
            catch (AggregateException) { break; }
        }
    }

    /// <summary>
    /// 节流执行帧显示更新：最多每 <see cref="FrameDisplayThrottleMs"/> ms 刷新一次 UI，
    /// 保证高频诊断循环不因 UI 调度而阻塞通信。
    /// </summary>
    private void TryUpdateFrameDisplay(Func<(string txHex, string rxHex, string txDesc, string rxDesc)> buildDisplay)
    {
        // 仅在 Debug 模式下才采集帧内容，避免不必要的数据拷贝与 UI 调度
        if (!IsDebugMode)
            return;

        DateTime now = DateTime.UtcNow;
        if ((now - _lastFrameDisplayTime).TotalMilliseconds < FrameDisplayThrottleMs)
            return;
        _lastFrameDisplayTime = now;

        (string txHex, string rxHex, string txDesc, string rxDesc) = buildDisplay();
        string ts = DateTime.Now.ToString("HH:mm:ss.fff");

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            LastTxFrameHex = txHex;
            LastRxFrameHex = rxHex;
            LastTxFrameDesc = txDesc;
            LastRxFrameDesc = rxDesc;
            LastFrameTimestamp = ts;
        });
    }

    private (string txHex, string rxHex, string txDesc, string rxDesc) BuildModbusFrameDisplay(bool ok)
    {
        byte[] tx = _deviceAdd.ModbusMaster.LastTxFrame;
        byte[] rx = _deviceAdd.ModbusMaster.LastRxFrame;
        string txHex = FormatHexFrame(tx);
        string rxHex = ok ? FormatHexFrame(rx) : "（无有效响应）";
        string pollProgress = HVariables.RegisterTable.Count > 0
            ? $"[{_modbusPollIndex}/{HVariables.RegisterTable.Count}] "
            : string.Empty;
        string txDesc = $"{pollProgress}{DecodeModbusTxFrame(tx)}\n  寄存器 0x{_lastModbusPollAddr:X4} — {_lastModbusPollName}";
        string rxDesc = ok
            ? $"{DecodeModbusRxFrame(rx)}\n  值: 0x{_lastModbusPollValue:X4}  ({_lastModbusPollValue})"
            : $"错误: {_deviceAdd.ModbusMaster.LastException}";
        return (txHex, rxHex, txDesc, rxDesc);
    }

    private (string txHex, string rxHex, string txDesc, string rxDesc) BuildEcatFrameDisplay(
        ActiveProtocolStack proto, bool ok, string? err)
    {
        var axis = _deviceAdd.CurrentAxis;
        string slaveInfo = axis is null ? "?" : axis.SlaveAddr.ToString();
        string txDesc = $"ReadState(slave={slaveInfo})" + (ProbeSdo ? " + SDO 0x6041/0x00" : string.Empty);
        string rxDesc = ok
            ? $"State: {EtherCatLastState}" + (ProbeSdo ? "  StatusWord: OK" : string.Empty)
            : $"失败: {err}";
        // EtherCAT 底层帧由 SOEM 管理，显示操作语义
        string txHex = $"[EtherCAT 帧由 SOEM 管理]\n{txDesc}";
        string rxHex = $"[EtherCAT 帧由 SOEM 管理]\n{rxDesc}";
        return (txHex, rxHex, txDesc, rxDesc);
    }

    private static string FormatHexFrame(byte[] frame) =>
        frame.Length == 0 ? "—" : BitConverter.ToString(frame).Replace('-', ' ');

    private static string DecodeModbusTxFrame(byte[] frame)
    {
        if (frame.Length < 4) return "—";
        byte slave = frame[0];
        byte func = frame[1];
        ushort addr = (ushort)((frame[2] << 8) | frame[3]);
        return func switch
        {
            0x03 => $"从站={slave}  FC03 读保持寄存器  地址=0x{addr:X4}",
            0x06 => $"从站={slave}  FC06 写单寄存器    地址=0x{addr:X4}",
            0x10 => $"从站={slave}  FC10 写多寄存器    地址=0x{addr:X4}",
            _ => $"从站={slave}  FC=0x{func:X2}  地址=0x{addr:X4}",
        };
    }

    private static string DecodeModbusRxFrame(byte[] frame)
    {
        if (frame.Length < 3) return "—";
        byte func = frame[1];
        if ((func & 0x80) != 0)
            return $"异常响应  EC=0x{frame[2]:X2}";
        return func switch
        {
            0x03 when frame.Length >= 5 => $"FC03 读数据  字节数={frame[2]}  数据={FormatRegisterValues(frame, 3, frame[2])}",
            0x06 => $"FC06 写单寄存器确认",
            0x10 => $"FC10 写多寄存器确认",
            _ => $"FC=0x{func:X2}",
        };
    }

    private static string FormatRegisterValues(byte[] frame, int offset, int byteCount)
    {
        if (offset + byteCount > frame.Length) return "?";
        var parts = new System.Text.StringBuilder();
        for (int i = 0; i < byteCount; i += 2)
        {
            if (i > 0) parts.Append(' ');
            if (offset + i + 1 < frame.Length)
                parts.Append($"0x{((frame[offset + i] << 8) | frame[offset + i + 1]):X4}");
        }
        return parts.ToString();
    }

    private (bool ok, double elapsedMs, string? err) ProbeEtherCAT(Stopwatch sw)
    {
        var axis = _deviceAdd.CurrentAxis;
        var master = _deviceAdd.EcatMaster;
        if (axis is null)
        {
            return (false, 0, "未检测到 EtherCAT 从站");
        }

        sw.Restart();
        try
        {
            var state = master.ReadState(axis.SlaveAddr);
            string stateStr = state.ToString();
            sw.Stop();

            // ReadState 返回 None 视为掉线
            bool lost = string.Equals(stateStr, "None", StringComparison.OrdinalIgnoreCase);

            if (ProbeSdo)
            {
                // 轻量 SDO：状态字 0x6041
                if (!master.TryReadSDO(axis.SlaveAddr, 0x6041, 0x00, out ushort _))
                {
                    sw.Stop();
                    Application.Current?.Dispatcher.Invoke(() => EtherCatLastState = stateStr);
                    return (false, sw.Elapsed.TotalMilliseconds, $"SDO 0x6041 读取失败 (state={stateStr})");
                }
            }

            // ── 读取 ESC 诊断寄存器（FPRD），失败不影响主流程 ──
            try
            {
                ushort ectAddr = master.GetSlaveEctAdr(axis.SlaveAddr);
                ushort alSt = 0, alCode = 0, rxErrWordA = 0, rxErrWordB = 0;
                byte procErr = 0, pdiErr = 0, lostLinkA = 0, lostLinkB = 0;
                master.TryFPRD<ushort>(ectAddr, (ushort)0x0130, out alSt);
                master.TryFPRD<ushort>(ectAddr, (ushort)0x0134, out alCode);
                master.TryFPRD<ushort>(ectAddr, (ushort)0x0300, out rxErrWordA);
                master.TryFPRD<ushort>(ectAddr, (ushort)0x0302, out rxErrWordB);
                master.TryFPRD<byte>(ectAddr, (ushort)0x030C, out procErr);
                master.TryFPRD<byte>(ectAddr, (ushort)0x030D, out pdiErr);
                master.TryFPRD<byte>(ectAddr, (ushort)0x0310, out lostLinkA);
                master.TryFPRD<byte>(ectAddr, (ushort)0x0311, out lostLinkB);
                Application.Current?.Dispatcher.Invoke(() =>
                    UpdateEcatEscStats(alSt, alCode, rxErrWordA, rxErrWordB, procErr, pdiErr, lostLinkA, lostLinkB));
            }
            catch { /* ESC 寄存器读取失败为非致命，忽略 */ }

            Application.Current?.Dispatcher.Invoke(() => EtherCatLastState = stateStr);
            if (lost)
            {
                return (false, sw.Elapsed.TotalMilliseconds, "从站状态 None（疑似掉线）");
            }
            return (true, sw.Elapsed.TotalMilliseconds, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, sw.Elapsed.TotalMilliseconds, ex.Message);
        }
    }

    private (bool ok, double elapsedMs, string? err) ProbeModbus(Stopwatch sw)
    {
        var modbus = _deviceAdd.ModbusMaster;
        var axis = _deviceAdd.CurrentModbusAxis;

        if (!modbus.IsOpen)
            return (false, 0, "Modbus 串口已关闭");
        if (axis is null)
            return (false, 0, "未检测到 Modbus 从站");

        // ── 从 HVariables 取本次轮询的寄存器（round-robin） ──
        var table = HVariables.RegisterTable;
        if (table.Count == 0)
            return (false, 0, "HVariables 表为空");

        if (_modbusPollIndex >= table.Count)
            _modbusPollIndex = 0;

        // 厂家页禁用的寄存器跳过；最多扫描一整圈防止死循环
        int scanned = 0;
        while (scanned < table.Count
            && Wpf.Ui.servoStudio.Services.RegisterDisableService.IsDisabledForActive(
                table[_modbusPollIndex].SdoIndex,
                table[_modbusPollIndex].SdoSubIndex))
        {
            _modbusPollIndex = (_modbusPollIndex + 1) % table.Count;
            scanned++;
        }
        if (scanned >= table.Count)
            return (false, 0, "全部寄存器已禁用");

        HRegisterEntry entry = table[_modbusPollIndex];
        _modbusPollIndex = (_modbusPollIndex + 1) % table.Count;

        if (!TryParseCommAddress(entry.CommAddress, out ushort regAddr))
            return (false, 0, $"无法解析 CommAddress: {entry.CommAddress}");

        _lastModbusPollAddr = regAddr;
        _lastModbusPollName = $"{entry.HIndex}  {entry.ParameterName}";

        sw.Restart();
        bool ok = modbus.ReadHoldingRegisters((byte)axis.SlaveAddr, regAddr, 1, out byte[] data);
        sw.Stop();

        if (ok)
        {
            _lastModbusPollValue = data.Length >= 2
                ? (ushort)((data[0] << 8) | data[1])
                : (ushort)0;

            // 记录主从应答间隔（主机发送结束 → 从机首字节到达）
            double respMs = modbus.LastResponseLatencyMs;
            Application.Current?.Dispatcher.Invoke(() => UpdateResponseLatencyStats(respMs));

            return (true, sw.Elapsed.TotalMilliseconds, null);
        }

        ModbusExceptionCode code = modbus.LastException;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            switch (code)
            {
                case ModbusExceptionCode.Timeout:
                    ModbusTimeoutCount++; break;
                case ModbusExceptionCode.CrcError:
                    ModbusCrcErrorCount++; break;
                case ModbusExceptionCode.InvalidResponse:
                case ModbusExceptionCode.SlaveAddressMismatch:
                case ModbusExceptionCode.FunctionMismatch:
                    ModbusInvalidResponseCount++; break;
                case ModbusExceptionCode.IllegalFunction:
                case ModbusExceptionCode.IllegalDataAddress:
                case ModbusExceptionCode.IllegalDataValue:
                case ModbusExceptionCode.SlaveDeviceFailure:
                case ModbusExceptionCode.Acknowledge:
                case ModbusExceptionCode.SlaveDeviceBusy:
                case ModbusExceptionCode.NegativeAcknowledge:
                case ModbusExceptionCode.MemoryParityError:
                case ModbusExceptionCode.GatewayPathUnavailable:
                case ModbusExceptionCode.GatewayTargetDeviceFailedToRespond:
                    ModbusProtocolErrorCount++; break;
                default:
                    ModbusOtherErrorCount++; break;
            }
        });

        return (false, sw.Elapsed.TotalMilliseconds, $"Modbus 错误: {code}");
    }

    /// <summary>
    /// 将 HVariables 的 CommAddress（如 "2000-01h"）解析为 Modbus 保持寄存器地址。<br/>
    /// 映射规则与 <see cref="Core.Modbus.ModbusRtuMaster.MapToModbusAddress"/> 保持一致：
    /// <c>((sdoIndex &amp; 0x00FF) &lt;&lt; 8) | (subIndex - 1)</c>，subIndex=0 时按 0 处理。
    /// </summary>
    private static bool TryParseCommAddress(string commAddr, out ushort regAddr)
    {
        regAddr = 0;
        if (string.IsNullOrEmpty(commAddr)) return false;

        // 去除末尾 'h' / 'H'
        ReadOnlySpan<char> span = commAddr.AsSpan().TrimEnd('h').TrimEnd('H');
        int dash = span.IndexOf('-');
        if (dash < 0) return false;

        if (!ushort.TryParse(span[..dash], NumberStyles.HexNumber, null, out ushort sdoIndex)) return false;
        if (!byte.TryParse(span[(dash + 1)..], NumberStyles.HexNumber, null, out byte subIndex)) return false;

        regAddr = (ushort)(((sdoIndex & 0x00FF) << 8) | (subIndex == 0 ? 0 : (subIndex - 1)));
        return true;
    }

    private void ApplyResult(ActiveProtocolStack proto, bool ok, double elapsedMs, string? err)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            TestCount++;
            if (ok)
            {
                SuccessCount++;
                _latencySumMs += elapsedMs;
                if (SuccessCount == 1)
                {
                    MinLatencyMs = elapsedMs;
                    MaxLatencyMs = elapsedMs;
                }
                else
                {
                    if (elapsedMs < MinLatencyMs) MinLatencyMs = elapsedMs;
                    if (elapsedMs > MaxLatencyMs) MaxLatencyMs = elapsedMs;
                }
                AvgLatencyMs = _latencySumMs / SuccessCount;
            }
            else
            {
                FailureCount++;
                if (!string.IsNullOrEmpty(err))
                {
                    LastErrorText = $"[{DateTime.Now:HH:mm:ss}] {err}";
                }

                if (proto == ActiveProtocolStack.EtherCAT)
                {
                    EtherCatLostCount++;
                }
            }

            SuccessRatePercent = TestCount == 0 ? 0 : (SuccessCount * 100.0 / TestCount);
        });
    }

    /// <summary>仅在 UI 线程上调用：更新主机→从机应答延时（min/avg/max/last）。</summary>
    private void UpdateResponseLatencyStats(double respMs)
    {
        LastResponseLatencyMs = respMs;
        _responseLatencySumMs += respMs;
        _responseLatencySampleCount++;
        if (_responseLatencySampleCount == 1)
        {
            MinResponseLatencyMs = respMs;
            MaxResponseLatencyMs = respMs;
        }
        else
        {
            if (respMs < MinResponseLatencyMs) MinResponseLatencyMs = respMs;
            if (respMs > MaxResponseLatencyMs) MaxResponseLatencyMs = respMs;
        }
        AvgResponseLatencyMs = _responseLatencySumMs / _responseLatencySampleCount;
    }

    // ────────────────────────────────────────────────────────
    //  EtherCAT ESC 诊断寄存器解析
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// 仅在 UI 线程上调用：将 FPRD 读到的原始寄存器值更新到可绑定属性。<br/>
    /// <paramref name="rxErrWordA"/> 对应 ESC 0x0300：低字节=物理层错误，高字节=帧格式/CRC 错误（Port A）。<br/>
    /// <paramref name="rxErrWordB"/> 对应 ESC 0x0302：同上，Port B。
    /// </summary>
    private void UpdateEcatEscStats(
        ushort alSt, ushort alCode,
        ushort rxErrWordA, ushort rxErrWordB,
        byte procErr, byte pdiErr,
        byte lostLinkA, byte lostLinkB)
    {
        EcatAlStatus = alSt;
        EcatAlError = (alSt & 0x0010) != 0;
        EcatAlStateText = DecodeAlState(alSt);
        EcatAlStatusCode = alCode;
        EcatAlStatusCodeText = DecodeAlStatusCode(alCode);

        // 0x0300 低字节 = RX 物理层错误，高字节 = 无效帧（CRC/长度）
        byte invA = (byte)(rxErrWordA >> 8);
        byte rxA  = (byte)(rxErrWordA & 0xFF);
        byte invB = (byte)(rxErrWordB >> 8);
        byte rxB  = (byte)(rxErrWordB & 0xFF);

        if (!_ecatEscBaselineSet)
        {
            _baseInvFrameA = invA; _baseRxErrA = rxA;
            _baseInvFrameB = invB; _baseRxErrB = rxB;
            _baseProcErr = procErr; _basePdiErr = pdiErr;
            _baseLinkLostA = lostLinkA; _baseLinkLostB = lostLinkB;
            _ecatEscBaselineSet = true;
        }

        EcatInvFrameA = Delta8(invA, _baseInvFrameA);
        EcatRxErrA    = Delta8(rxA,  _baseRxErrA);
        EcatInvFrameB = Delta8(invB, _baseInvFrameB);
        EcatRxErrB    = Delta8(rxB,  _baseRxErrB);
        EcatProcErr   = Delta8(procErr,   _baseProcErr);
        EcatPdiErr    = Delta8(pdiErr,    _basePdiErr);
        EcatLinkLostA = Delta8(lostLinkA, _baseLinkLostA);
        EcatLinkLostB = Delta8(lostLinkB, _baseLinkLostB);
    }

    /// <summary>计算 8 位饱和计数器相对基准值的增量，处理 0→255 环绕。</summary>
    private static uint Delta8(byte current, byte baseline) =>
        (uint)((current - baseline + 256) % 256);

    /// <summary>将 AL Status 寄存器（0x0130）解码为人读状态字符串。</summary>
    private static string DecodeAlState(ushort alStatus)
    {
        if (alStatus == 0) return string.Empty;
        bool err = (alStatus & 0x0010) != 0;
        string state = (alStatus & 0x000F) switch
        {
            0x01 => "Init",
            0x02 => "Pre-Op",
            0x03 => "Bootstrap",
            0x04 => "Safe-Op",
            0x08 => "Op",
            _ => $"未知(0x{alStatus & 0xF:X})",
        };
        return err ? $"{state}  [! 错误]" : state;
    }

    /// <summary>将 AL Status Code（0x0134）解码为中文说明（参照 ETG.1000.6 附录）。</summary>
    private static string DecodeAlStatusCode(ushort code) => code switch
    {
        0x0000 => "无错误",
        0x0001 => "未指定错误",
        0x0002 => "无可用内存",
        0x0011 => "无效状态切换请求",
        0x0012 => "未知请求状态",
        0x0013 => "不支持 Bootstrap",
        0x0014 => "无有效固件",
        0x0015 => "无效邮箱配置 (Bootstrap)",
        0x0016 => "无效邮箱配置 (Pre-Op)",
        0x0017 => "无效同步管理器配置",
        0x0018 => "无有效输入",
        0x0019 => "无有效输出",
        0x001A => "不支持同步模式",
        0x001B => "自由运行需三缓冲区",
        0x001C => "看门狗超时",
        0x001D => "无有效输入和输出",
        0x001E => "严重同步错误",
        0x001F => "无同步错误",
        0x0020 => "DC 同步配置无效",
        0x0021 => "DC 锁存配置无效",
        0x0022 => "PLL 错误",
        0x0023 => "DC 同步 IO 错误",
        0x0024 => "DC 同步超时",
        0x0025 => "DC 无效同步周期时间",
        0x002D => "EEPROM 无访问权",
        0x002E => "EEPROM 错误",
        0x002F => "从站应用控制器不可用",
        _ => $"代码 0x{code:X4}",
    };
}
