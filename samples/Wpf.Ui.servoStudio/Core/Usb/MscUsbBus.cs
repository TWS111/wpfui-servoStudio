// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace Core.Usb;

/// <summary>
/// MSC（Mass Storage Class）工作方式下的 <see cref="IUsbBus"/> 实现。<br/>
/// 从机 USBX MSC demo 在宿主侧表现为可移动磁盘；本类通过<b>约定目录的文件读写</b>
/// 实现 <see cref="UsbPacket"/> 收发：<br/>
/// <list type="bullet">
///   <item><description>上位机 → 从机：在 <c>{DriveRoot}/usb_in/</c> 写入 <c>{seq:X4}_{channel:X4}.bin</c>（原始 USB 帧字节流）。</description></item>
///   <item><description>从机 → 上位机：监视 <c>{DriveRoot}/usb_out/</c> 内新出现的 <c>.bin</c> 文件，读取后即时删除。</description></item>
/// </list>
/// <para>
/// 若构造时未提供 <see cref="DriveRoot"/>（或路径不存在），自动回退到
/// <c>%TEMP%/ServoStudioUsbMsc/{vid:X4}_{pid:X4}/</c>，方便联机前的协议自测；
/// 真实部署时由调用方传入盘符（如 <c>"E:\\"</c>）。
/// </para>
/// </summary>
public sealed class MscUsbBus : IUsbBus
{
    private readonly ConcurrentQueue<UsbPacket> _rx = new();
    private readonly Lock _txLock = new();
    private FileSystemWatcher? _watcher;
    private string _inboxPath = string.Empty;
    private string _outboxPath = string.Empty;
    private uint _seqTx;
    private bool _disposed;

    /// <summary>挂载到的盘符或目录（如 "E:\\"）；构造时可不指定，由 <see cref="Open"/> 自动回退。</summary>
    public string DriveRoot { get; set; } = string.Empty;

    /// <summary>HOST → DEVICE 子目录名。</summary>
    public string HostToDeviceFolder { get; set; } = "usb_in";

    /// <summary>DEVICE → HOST 子目录名。</summary>
    public string DeviceToHostFolder { get; set; } = "usb_out";

    public bool IsOpen { get; private set; }

    public MscUsbBus(string driveRoot = "")
    {
        DriveRoot = driveRoot ?? string.Empty;
    }

    public bool Open(ushort vendorId, ushort productId)
    {
        try
        {
            string root = ResolveDriveRoot(vendorId, productId);
            _inboxPath = Path.Combine(root, HostToDeviceFolder);
            _outboxPath = Path.Combine(root, DeviceToHostFolder);
            Directory.CreateDirectory(_inboxPath);
            Directory.CreateDirectory(_outboxPath);

            _watcher = new FileSystemWatcher(_outboxPath, "*.bin")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
                IncludeSubdirectories = false,
            };
            _watcher.Created += OnIncomingFile;

            // 启动时把已经堆积的文件先消费一轮
            foreach (string existing in Directory.EnumerateFiles(_outboxPath, "*.bin"))
            {
                TryConsumeIncomingFile(existing);
            }

            IsOpen = true;
            return true;
        }
        catch
        {
            Close();
            return false;
        }
    }

    public void Close()
    {
        IsOpen = false;
        if (_watcher is not null)
        {
            try { _watcher.EnableRaisingEvents = false; } catch { /* ignore */ }
            try { _watcher.Created -= OnIncomingFile; } catch { /* ignore */ }
            try { _watcher.Dispose(); } catch { /* ignore */ }
            _watcher = null;
        }

        while (_rx.TryDequeue(out _))
        {
        }
    }

    public bool Send(UsbPacket packet)
    {
        if (!IsOpen)
        {
            return false;
        }

        try
        {
            byte[] raw = UsbPacketCodec.Serialize(packet);
            uint seq = Interlocked.Increment(ref _seqTx);
            string name = $"{seq:X8}_{(ushort)packet.Channel:X4}.bin";
            string path = Path.Combine(_inboxPath, name);
            string tmp = path + ".tmp";

            lock (_txLock)
            {
                File.WriteAllBytes(tmp, raw);
                // 原子重命名，避免对端读到半写文件
                File.Move(tmp, path, overwrite: true);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryReceive(int timeoutMs, out UsbPacket packet)
    {
        packet = default;
        if (!IsOpen)
        {
            return false;
        }

        int waited = 0;
        const int pollIntervalMs = 20;
        while (waited <= timeoutMs)
        {
            if (_rx.TryDequeue(out UsbPacket pkt))
            {
                packet = pkt;
                return true;
            }

            Thread.Sleep(pollIntervalMs);
            waited += pollIntervalMs;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
        GC.SuppressFinalize(this);
    }

    private string ResolveDriveRoot(ushort vendorId, ushort productId)
    {
        if (!string.IsNullOrEmpty(DriveRoot) && Directory.Exists(DriveRoot))
        {
            return DriveRoot;
        }

        string fallback = Path.Combine(
            Path.GetTempPath(),
            "ServoStudioUsbMsc",
            $"{vendorId:X4}_{productId:X4}");
        Directory.CreateDirectory(fallback);
        DriveRoot = fallback;
        return fallback;
    }

    private void OnIncomingFile(object sender, FileSystemEventArgs e)
    {
        TryConsumeIncomingFile(e.FullPath);
    }

    private void TryConsumeIncomingFile(string path)
    {
        // 文件可能仍在写入，重试若干次
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                byte[] raw = File.ReadAllBytes(path);
                if (raw.Length > 0
                    && UsbPacketCodec.TryDeserialize(raw, UsbDirection.DeviceToHost, out UsbPacket pkt))
                {
                    _rx.Enqueue(pkt);
                }

                try { File.Delete(path); } catch { /* ignore */ }
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(20);
            }
            catch
            {
                return;
            }
        }
    }
}

