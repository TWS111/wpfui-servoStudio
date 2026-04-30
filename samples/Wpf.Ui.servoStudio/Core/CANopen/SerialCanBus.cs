// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using RJCP.IO.Ports;

namespace Core.CANopen;

/// <summary>
/// 基于 LAWICEL <b>slcan</b> 协议的 <see cref="ICanBus"/> 实现。<br/>
/// 兼容大多数 USB-CAN 适配器（CANable / OpenCAN-USB / USB-CAN-A 兼容固件等），
/// 与 <see cref="Core.Modbus.ModbusRtuMaster"/> 共用 <see cref="SerialPortStream"/> 串口栈。<br/>
/// 已实现指令子集：<c>S0~S8</c>（比特率）、<c>O</c>（开通道）、<c>C</c>（关通道）、
/// <c>t&lt;id3&gt;&lt;dlc1&gt;&lt;data&gt;</c>（11 位标准帧 Tx/Rx）。<br/>
/// 不支持扩展 ID（CiA301 仅使用 11 位 ID，无需）。
/// </summary>
public sealed class SerialCanBus : ICanBus
{
    private readonly SerialPortStream _port = new();
    private readonly Lock _txLock = new();
    private readonly BlockingCollection<CanFrame> _rxQueue = new(new ConcurrentQueue<CanFrame>());
    private Thread? _rxThread;
    private CancellationTokenSource? _rxCts;
    private bool _disposed;

    /// <summary>串口名（如 COM7）。</summary>
    public string PortName { get; set; } = string.Empty;

    /// <summary>串口波特率（与 CAN 比特率不同！多数 slcan 适配器固定 921600 / 115200）。</summary>
    public int SerialBaudRate { get; set; } = 1_000_000;

    /// <summary>命令应答超时（ms）。</summary>
    public int CommandTimeoutMs { get; set; } = 200;

    public bool IsOpen { get; private set; }

    /// <summary>构造时只设置串口名与串口波特率。</summary>
    public SerialCanBus() { }

    public SerialCanBus(string portName, int serialBaud = 1_000_000)
    {
        PortName = portName;
        SerialBaudRate = serialBaud;
    }

    public bool Open(CanBitrate bitrate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrEmpty(PortName))
            return false;

        try
        {
            if (_port.IsOpen) _port.Close();

            _port.PortName = PortName;
            _port.BaudRate = SerialBaudRate;
            _port.DataBits = 8;
            _port.Parity = Parity.None;
            _port.StopBits = StopBits.One;
            _port.ReadTimeout = CommandTimeoutMs;
            _port.WriteTimeout = CommandTimeoutMs;
            _port.NewLine = "\r";
            _port.Open();
            if (!_port.IsOpen) return false;

            // 关闭可能残留的旧通道
            _ = SendCommand("C");
            // 设置比特率
            string sCmd = bitrate switch
            {
                CanBitrate.Br10k   => "S0",
                CanBitrate.Br20k   => "S1",
                CanBitrate.Br50k   => "S2",
                CanBitrate.Br100k  => "S3",
                CanBitrate.Br125k  => "S4",
                CanBitrate.Br250k  => "S5",
                CanBitrate.Br500k  => "S6",
                CanBitrate.Br800k  => "S7",
                CanBitrate.Br1000k => "S8",
                _ => "S6",
            };
            if (!SendCommand(sCmd))
            {
                _port.Close();
                return false;
            }

            // 打开通道
            if (!SendCommand("O"))
            {
                _port.Close();
                return false;
            }

            _rxCts = new CancellationTokenSource();
            _rxThread = new Thread(RxLoop) { IsBackground = true, Name = "SerialCanBus-Rx" };
            _rxThread.Start();
            IsOpen = true;
            return true;
        }
        catch
        {
            try { _port.Close(); } catch { /* ignore */ }
            return false;
        }
    }

    public void Close()
    {
        if (!IsOpen && !(_port?.IsOpen ?? false)) return;
        IsOpen = false;
        try { _rxCts?.Cancel(); } catch { /* ignore */ }
        try { _rxThread?.Join(200); } catch { /* ignore */ }
        _rxThread = null;

        try { if (_port.IsOpen) { _ = SendCommand("C"); _port.Close(); } } catch { /* ignore */ }
        // 排空
        while (_rxQueue.TryTake(out _)) { }
    }

    public bool Send(CanFrame frame)
    {
        if (!IsOpen) return false;
        // t<id3><dlc1><data hex>\r
        var sb = new StringBuilder(5 + 16);
        sb.Append('t');
        sb.Append(frame.Id.ToString("X3", CultureInfo.InvariantCulture));
        sb.Append(frame.Dlc.ToString("X1", CultureInfo.InvariantCulture));
        for (int i = 0; i < frame.Dlc; i++)
            sb.Append(frame.Data[i].ToString("X2", CultureInfo.InvariantCulture));
        return SendCommand(sb.ToString());
    }

    public bool TryReceive(int timeoutMs, out CanFrame frame)
    {
        frame = default;
        if (!IsOpen) return false;
        try { return _rxQueue.TryTake(out frame, timeoutMs); }
        catch (InvalidOperationException) { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Close(); } catch { /* ignore */ }
        _rxQueue.Dispose();
        _port.Dispose();
    }

    // ─────────────────────── 内部 ───────────────────────

    private bool SendCommand(string cmd)
    {
        lock (_txLock)
        {
            try
            {
                byte[] payload = Encoding.ASCII.GetBytes(cmd + "\r");
                _port.Write(payload, 0, payload.Length);
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 接收循环：按 <c>\r</c> 分帧解析 slcan 应答。识别 <c>t...</c> 数据帧并入队，
    /// 其它行（<c>z</c>/<c>\a</c> 等命令应答）暂时忽略。
    /// </summary>
    private void RxLoop()
    {
        var buf = new StringBuilder(64);
        var chunk = new byte[256];
        var sw = Stopwatch.StartNew();
        CancellationToken token = _rxCts!.Token;

        while (!token.IsCancellationRequested)
        {
            int got;
            try { got = _port.Read(chunk, 0, chunk.Length); }
            catch (TimeoutException) { continue; }
            catch { break; }

            for (int i = 0; i < got; i++)
            {
                char c = (char)chunk[i];
                if (c == '\r' || c == 0x07 /* BEL */)
                {
                    if (buf.Length > 0)
                    {
                        TryParseFrame(buf.ToString());
                        buf.Clear();
                    }
                }
                else
                {
                    buf.Append(c);
                    if (buf.Length > 64) buf.Clear(); // 防止爆缓冲
                }
            }

            // 防止 CPU 100% 空转（_port.Read 为阻塞）
            if (got == 0) Thread.Sleep(1);
            _ = sw;
        }
    }

    private void TryParseFrame(string line)
    {
        // 11 位标准帧：t<3 hex id><1 hex dlc><dlc*2 hex data>
        if (line.Length < 5 || line[0] != 't') return;
        if (!ushort.TryParse(line.AsSpan(1, 3), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort id))
            return;
        if (!byte.TryParse(line.AsSpan(4, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte dlc))
            return;
        if (dlc > 8) return;
        if (line.Length < 5 + dlc * 2) return;

        var data = new byte[8];
        for (int i = 0; i < dlc; i++)
        {
            if (!byte.TryParse(line.AsSpan(5 + i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                return;
            data[i] = b;
        }

        _rxQueue.Add(new CanFrame(id, dlc, data));
    }
}
