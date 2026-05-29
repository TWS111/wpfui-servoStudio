// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Threading;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.Services;

namespace Core.Usb;

/// <summary>
/// USB 协议栈的高层门面，与 <c>CanOpenMaster</c> / <c>ModbusRtuMaster</c> / <c>EtherCATMaster</c>
/// <b>平行但独立</b>：本栈承载的数据与现有三栈不同，专用于：
/// <list type="bullet">
///   <item><description>上位机负载计算的<b>曲线拟合</b>数据下发（<see cref="UsbChannel.CurveFitting"/>）。</description></item>
///   <item><description>电机部分<b>自适应参数</b>的调节计算（<see cref="UsbChannel.AdaptiveParam"/>）。</description></item>
///   <item><description>从机向上报送的<b>高带宽数据</b>（<see cref="UsbChannel.HighBandwidthTelemetry"/>）。</description></item>
/// </list>
/// 从机侧基于 ThreadX 中间件 <c>USBX</c>（默认配置）实现 Bulk 端点。<br/>
/// <para>
/// 本类负责生命周期管理 + 后台接收派发，<b>不</b>实现 <c>IServoMaster</c>，
/// 因此不会侵入现有寄存器禁用 / SDO 路径。
/// </para>
/// <para>
/// 具体业务编解码（曲线拟合包格式 / 自适应参数协议字段 / 高带宽数据帧头等）由后续部署补全：
/// 在 <see cref="OnPacketReceived"/> 路径中按 <see cref="UsbPacket.Channel"/> 分发到各自处理器即可。
/// </para>
/// </summary>
public sealed class UsbMaster : IDisposable
{
    private IUsbBus _bus;
    private readonly Lock _txLock = new();
    private Thread? _rxThread;
    private CancellationTokenSource? _rxCts;
    private int _seqOut;
    private bool _disposed;

    /// <summary>底层总线（诊断 / 单元测试用途）。</summary>
    public IUsbBus Bus => _bus;

    /// <summary>
    /// 当前 USB 工作方式 —— 由构造时注入，决定底层 <see cref="IUsbBus"/> 实现：
    /// CDC-ACM → <see cref="CdcAcmUsbBus"/>；WinUSB Bulk → <see cref="UsbBulkBus"/>；MSC → <see cref="MscUsbBus"/>。<br/>
    /// UI 切换工作方式后须调用 <see cref="ChangeWorkMode"/>，以保证协议栈内部状态与从机 USBX 配置一致
    /// （即"在协议栈中更新他们"）。
    /// </summary>
    public UsbWorkMode WorkMode { get; private set; }

    /// <summary>当前绑定的设备描述（仅由工厂构造路径填充，便于上层 UI 显示当前连接对象）。</summary>
    public UsbDeviceDescriptor? BoundDevice { get; private set; }

    /// <summary>是否已开始监听总线。</summary>
    public bool IsRunning => _rxThread is not null && _bus.IsOpen;

    /// <summary>接收循环单次阻塞读超时 (ms)。</summary>
    public int ReceivePollTimeoutMs { get; set; } = UsbDefaults.DefaultReceiveTimeoutMs;

    /// <summary>
    /// 从机 → 上位机方向新报文事件。<br/>
    /// 由后台调度线程触发，订阅者请注意线程安全（如需 UI 更新，请 Dispatcher.Invoke）。
    /// </summary>
    public event Action<UsbPacket>? PacketReceived;

    /// <summary>接收回路出现异常时触发（不会终止接收线程，下一次循环会重试）。</summary>
    public event Action<Exception>? ReceiveError;

    /// <summary>成功发送一个报文后触发（在调用方线程上同步触发）。</summary>
    public event Action<UsbPacket>? PacketSent;

    /// <summary>
    /// 发送失败时触发，参数为底层 Win32 错误码（0 = 未知 / 非 WinUSB 路径）。<br/>
    /// 可据此区分"端点号错"（GLE = 2/1168）、"超时"（GLE = 1460/995）等情形。
    /// </summary>
    public event Action<int>? SendFailed;

    /// <summary>低层注入构造（单元测试 / 自带 IUsbBus 实现时使用）。</summary>
    public UsbMaster(IUsbBus bus, UsbWorkMode workMode = UsbWorkMode.WinUsbBulk)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        WorkMode = workMode;
    }

    /// <summary>
    /// 工厂构造：根据 <paramref name="workMode"/> 自动选择底层 <see cref="IUsbBus"/> 实现，
    /// 并按 <paramref name="descriptor"/> 中的 COM 号 / 实例 ID 等定位真实设备。<br/>
    /// 当前阶段 WinUSB / MSC 仍为骨架（部分操作抛 <see cref="NotImplementedException"/>）；
    /// CDC-ACM 已可基于宿主 COM 端口正常收发字节流。
    /// </summary>
    public static UsbMaster Create(UsbWorkMode workMode, UsbDeviceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        IUsbBus bus = workMode switch
        {
            UsbWorkMode.CdcAcm => new CdcAcmUsbBus(string.IsNullOrEmpty(descriptor.ComPort) ? "COM1" : descriptor.ComPort),
            UsbWorkMode.WinUsbBulk => new UsbBulkBus { InstanceId = descriptor.InstanceId },
            UsbWorkMode.Msc => new MscUsbBus(descriptor.ComPort),
            _ => new VirtualUsbBus(),
        };
        return new UsbMaster(bus, workMode) { BoundDevice = descriptor };
    }

    /// <summary>
    /// 切换 USB 工作方式：先 <see cref="Stop"/> 当前总线 → 释放 → 按新方式重建底层 <see cref="IUsbBus"/>。<br/>
    /// 若当前正在运行，调用方需自行决定是否随后再次 <see cref="Start"/>。<br/>
    /// 这是"在协议栈中更新工作方式"的运行期入口。
    /// </summary>
    public void ChangeWorkMode(UsbWorkMode newMode, UsbDeviceDescriptor? descriptor = null)
    {
        Stop();
        try { _bus.Dispose(); } catch { /* ignore */ }
        UsbDeviceDescriptor d = descriptor ?? BoundDevice ?? new UsbDeviceDescriptor();
        _bus = newMode switch
        {
            UsbWorkMode.CdcAcm => new CdcAcmUsbBus(string.IsNullOrEmpty(d.ComPort) ? "COM1" : d.ComPort),
            UsbWorkMode.WinUsbBulk => new UsbBulkBus { InstanceId = d.InstanceId },
            UsbWorkMode.Msc => new MscUsbBus(d.ComPort),
            _ => new VirtualUsbBus(),
        };
        WorkMode = newMode;
        BoundDevice = d;
    }

    /// <summary>
    /// 打开总线并启动后台接收。<br/>
    /// VID/PID 缺省使用 <see cref="UsbDefaults.HpMicroVendorId"/> / <see cref="UsbDefaults.AnyProductId"/>，
    /// 可在与从机 USBX 描述符对齐后传入实际值。
    /// </summary>
    public bool Start(ushort vendorId = UsbDefaults.HpMicroVendorId, ushort productId = UsbDefaults.AnyProductId)
    {
        if (IsRunning)
        {
            return true;
        }

        if (!_bus.Open(vendorId, productId))
        {
            return false;
        }

        _rxCts = new CancellationTokenSource();
        _rxThread = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = "UsbMaster-Receive",
        };
        _rxThread.Start();
        return true;
    }

    /// <summary>停止后台接收并关闭底层总线。</summary>
    public void Stop()
    {
        try { _rxCts?.Cancel(); } catch { /* ignore */ }
        try { _rxThread?.Join(200); } catch { /* ignore */ }
        _rxThread = null;
        _rxCts?.Dispose();
        _rxCts = null;
        try { _bus.Close(); } catch { /* ignore */ }
    }

    /// <summary>
    /// 在指定通道上发送一段原始负载（Host → Device）。<br/>
    /// 内部自动分配递增序号；失败返回 false。<br/>
    /// 具体应用层编解码（曲线拟合 / 自适应参数 / 私有协议）由调用方在 <paramref name="payload"/> 中完成。
    /// </summary>
    public bool Send(UsbChannel channel, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!IsRunning)
        {
            return false;
        }

        ushort seq = unchecked((ushort)Interlocked.Increment(ref _seqOut));
        var pkt = UsbPacket.Out(channel, seq, payload);
        lock (_txLock)
        {
            RecordRuntimePacket(pkt);
            bool sent = _bus.Send(pkt);
            if (sent)
            {
                try { PacketSent?.Invoke(pkt); } catch { /* swallow */ }
            }
            else
            {
                int gle = (_bus as UsbBulkBus)?.LastSendError ?? 0;
                try { SendFailed?.Invoke(gle); } catch { /* swallow */ }
            }

            return sent;
        }
    }

    /// <summary>发送一个已构造好的报文（高级用法：调用方自管序号 / 方向）。</summary>
    public bool SendRaw(UsbPacket packet)
    {
        if (!IsRunning)
        {
            return false;
        }

        lock (_txLock)
        {
            RecordRuntimePacket(packet);
            return _bus.Send(packet);
        }
    }

    private void ReceiveLoop()
    {
        var cts = _rxCts;
        if (cts is null)
        {
            return;
        }

        while (!cts.IsCancellationRequested)
        {
            try
            {
                if (_bus.TryReceive(ReceivePollTimeoutMs, out UsbPacket pkt))
                {
                    OnPacketReceived(pkt);
                }
            }
            catch (Exception ex) when (!cts.IsCancellationRequested)
            {
                try { ReceiveError?.Invoke(ex); } catch { /* swallow */ }
            }
        }
    }

    private void OnPacketReceived(UsbPacket packet)
    {
        // TODO: 后续部署时，可在此按 packet.Channel 做分发：
        //   - UsbChannel.CurveFitting          → 曲线拟合下行应答 / 进度
        //   - UsbChannel.AdaptiveParam         → 自适应参数计算结果
        //   - UsbChannel.HighBandwidthTelemetry→ 高带宽采样 / 状态流
        // 当前仅向外抛事件，保持框架与具体业务解耦。
        RecordRuntimePacket(packet);
        try { PacketReceived?.Invoke(packet); } catch { /* swallow，避免污染接收线程 */ }
    }

    private static void RecordRuntimePacket(UsbPacket packet)
    {
        byte[] bytes = UsbPacketCodec.Serialize(packet);

        FrameDirection direction = packet.Direction == UsbDirection.HostToDevice
            ? FrameDirection.Send
            : FrameDirection.Response;
        FrameFormatRuntimeService.RecordRawFrame(FrameProtocolStack.USB, direction, bytes);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        try { _bus.Dispose(); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }
}
