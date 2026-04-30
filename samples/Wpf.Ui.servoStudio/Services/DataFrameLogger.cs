// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Wpf.Ui.servoStudio.Models;

namespace Wpf.Ui.servoStudio.Services;

/// <summary>
/// 线程安全、非阻塞的数据帧落盘服务。
///
/// <para>
/// 设计要点：
///   - 生产者（通信线程）仅做无锁 <see cref="ConcurrentQueue{T}.Enqueue"/> 入队 —— 不会阻塞 EtherCAT 通信。
///   - 单一后台消费者线程负责 I/O（打开文件、写入、周期 flush、轮转），与生产者隔离。
///   - 通过 <see cref="SemaphoreSlim"/> 信号量唤醒消费者，空闲时 0 CPU。
/// </para>
/// </summary>
public sealed class DataFrameLogger : IDisposable
{
    public static DataFrameLogger Instance { get; } = new();

    // ===== 配置（运行时可读，启动时固定）=====
    private string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ServoStudio", "Data");
    private DataSaveFormat _format = DataSaveFormat.Csv;
    private long _maxFileSizeBytes = 50L * 1024 * 1024;
    private readonly int _flushIntervalMs = 500;

    // ===== 运行状态 =====
    private readonly ConcurrentQueue<FrameRecord> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _startStopLock = new();
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private StreamWriter? _writer;
    private string _currentFilePath = string.Empty;
    private long _currentFileBytes;
    private bool _headerWritten;
    private int _droppedCount;
    private long _writtenCount;

    /// <summary>每周期最多允许堆积的帧数。超过即丢弃最旧并计数 <see cref="DroppedCount"/>。</summary>
    public int MaxPendingFrames { get; set; } = 200_000;

    public bool IsRunning => _worker is { IsCompleted: false };

    public string CurrentFilePath => _currentFilePath;

    public long WrittenCount => Interlocked.Read(ref _writtenCount);

    public int DroppedCount => _droppedCount;

    /// <summary>当文件发生轮转（新文件开启）时触发。用于 UI 刷新当前文件名。</summary>
    public event Action<string>? FileRotated;

    /// <summary>
    /// 启动后台写入线程。如已运行，仅更新配置并触发轮转。线程安全。
    /// </summary>
    public void Start(string directory, DataSaveFormat format, long maxFileSizeBytes)
    {
        lock (_startStopLock)
        {
            _directory = directory;
            _format = format;
            _maxFileSizeBytes = Math.Max(1L * 1024 * 1024, maxFileSizeBytes);

            if (IsRunning)
            {
                // 运行中 —— 强制开启新文件以应用新配置/新格式
                EnqueueControl(ControlKind.Rotate);
                return;
            }

            _cts = new CancellationTokenSource();
            _droppedCount = 0;
            _writtenCount = 0;
            _worker = Task.Factory.StartNew(
                () => RunWorker(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    /// <summary>停止写入线程并冲刷/关闭文件。线程安全。</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? worker;
        lock (_startStopLock)
        {
            cts = _cts;
            worker = _worker;
            _cts = null;
            _worker = null;
        }

        if (cts == null || worker == null)
            return;

        try
        {
            cts.Cancel();
            _ = _signal.Release();
            _ = worker.Wait(3000);
        }
        catch
        {
            // best-effort
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>
    /// 记录一帧。生产者路径 —— 纯无锁入队，适合在通信线程直接调用。
    /// </summary>
    public void Enqueue(FrameRecord record)
    {
        if (!IsRunning)
            return;

        if (_queue.Count >= MaxPendingFrames)
        {
            // 防止记录队列无限制增长挤爆内存 —— 丢掉最老一条
            _ = _queue.TryDequeue(out _);
            _ = Interlocked.Increment(ref _droppedCount);
        }

        _queue.Enqueue(record);
        try { _ = _signal.Release(); } catch { /* 可忽略 —— 信号量上限在 int.MaxValue */ }
    }

    private void EnqueueControl(ControlKind kind)
    {
        _queue.Enqueue(new FrameRecord { Control = kind });
        try { _ = _signal.Release(); } catch { }
    }

    private void RunWorker(CancellationToken ct)
    {
        try
        {
            OpenNewFile();
            DateTime lastFlushUtc = DateTime.UtcNow;

            while (!ct.IsCancellationRequested)
            {
                // 等待入队信号，但周期性苏醒用于 flush
                _ = _signal.Wait(_flushIntervalMs, ct);

                while (_queue.TryDequeue(out FrameRecord rec))
                {
                    if (ct.IsCancellationRequested)
                        break;

                    if (rec.Control == ControlKind.Rotate)
                    {
                        OpenNewFile();
                        continue;
                    }

                    WriteRecord(rec);
                    _ = Interlocked.Increment(ref _writtenCount);

                    if (_currentFileBytes >= _maxFileSizeBytes)
                        OpenNewFile();
                }

                if ((DateTime.UtcNow - lastFlushUtc).TotalMilliseconds >= _flushIntervalMs)
                {
                    try { _writer?.Flush(); } catch { }

                    lastFlushUtc = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            // 写线程内部异常不能影响通信 —— 吞掉
        }
        finally
        {
            // 收尾：尽可能排空队列
            try
            {
                while (_queue.TryDequeue(out FrameRecord rec))
                {
                    if (rec.Control == ControlKind.Rotate) continue;
                    WriteRecord(rec);
                    _ = Interlocked.Increment(ref _writtenCount);
                }
            }
            catch { }

            CloseFile();
        }
    }

    private void OpenNewFile()
    {
        CloseFile();

        try
        {
            _ = Directory.CreateDirectory(_directory);
        }
        catch
        { /* 可能权限不足，fallback 到 Temp */
            _directory = Path.Combine(Path.GetTempPath(), "ServoStudio", "Data");
            try { _ = Directory.CreateDirectory(_directory); } catch { }
        }

        var ext = _format switch
        {
            DataSaveFormat.Csv => "csv",
            DataSaveFormat.Tsv => "tsv",
            DataSaveFormat.Xls => "xls",
            DataSaveFormat.Json => "jsonl",
            _ => "csv",
        };

        _currentFilePath = Path.Combine(
            _directory,
            $"servo_frames_{DateTime.Now:yyyyMMdd_HHmmss_fff}.{ext}");

        try
        {
            _writer = new StreamWriter(_currentFilePath, append: false, new UTF8Encoding(true))
            {
                AutoFlush = false
            };
            _currentFileBytes = 0;
            _headerWritten = false;

            WriteHeader();
            FileRotated?.Invoke(_currentFilePath);
        }
        catch
        {
            _writer = null;
        }
    }

    private void CloseFile()
    {
        if (_writer == null) return;

        try
        {
            if (_format == DataSaveFormat.Xls && _headerWritten)
            {
                _writer.WriteLine("  </Table></Worksheet>");
                _writer.WriteLine("</Workbook>");
            }

            _writer.Flush();
            _writer.Dispose();
        }
        catch { }
        finally
        {
            _writer = null;
        }
    }

    private void WriteHeader()
    {
        if (_writer == null || _headerWritten) return;

        // 类型行与表头列数一致（9 列）：首列为标识符 "#TYPE"，
        // 同时隐含 Timestamp 列的类型为 DateTime；其余 8 列依次为各列的逻辑类型。
        // 读取端 DataViewViewModel 检测首列 == "#TYPE" 即跳过该行。
        const string TypeMarker = "#TYPE";

        switch (_format)
        {
            case DataSaveFormat.Csv:
                WriteLineTracked("Timestamp,Source,Group,Name,Index,SubIndex,DataType,Value,Unit");
                WriteLineTracked($"{TypeMarker},String,String,String,Hex16,UInt8,String,Variant,String");
                break;
            case DataSaveFormat.Tsv:
                WriteLineTracked("Timestamp\tSource\tGroup\tName\tIndex\tSubIndex\tDataType\tValue\tUnit");
                WriteLineTracked($"{TypeMarker}\tString\tString\tString\tHex16\tUInt8\tString\tVariant\tString");
                break;
            case DataSaveFormat.Xls:
                WriteLineTracked("<?xml version=\"1.0\"?>");
                WriteLineTracked("<?mso-application progid=\"Excel.Sheet\"?>");
                WriteLineTracked("<Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\" xmlns:ss=\"urn:schemas-microsoft-com:office:spreadsheet\">");
                WriteLineTracked("  <Worksheet ss:Name=\"Frames\"><Table>");
                WriteLineTracked("    <Row><Cell><Data ss:Type=\"String\">Timestamp</Data></Cell><Cell><Data ss:Type=\"String\">Source</Data></Cell><Cell><Data ss:Type=\"String\">Group</Data></Cell><Cell><Data ss:Type=\"String\">Name</Data></Cell><Cell><Data ss:Type=\"String\">Index</Data></Cell><Cell><Data ss:Type=\"String\">SubIndex</Data></Cell><Cell><Data ss:Type=\"String\">DataType</Data></Cell><Cell><Data ss:Type=\"String\">Value</Data></Cell><Cell><Data ss:Type=\"String\">Unit</Data></Cell></Row>");
                WriteLineTracked($"    <Row><Cell><Data ss:Type=\"String\">{TypeMarker}</Data></Cell><Cell><Data ss:Type=\"String\">String</Data></Cell><Cell><Data ss:Type=\"String\">String</Data></Cell><Cell><Data ss:Type=\"String\">String</Data></Cell><Cell><Data ss:Type=\"String\">Hex16</Data></Cell><Cell><Data ss:Type=\"String\">UInt8</Data></Cell><Cell><Data ss:Type=\"String\">String</Data></Cell><Cell><Data ss:Type=\"String\">Variant</Data></Cell><Cell><Data ss:Type=\"String\">String</Data></Cell></Row>");
                break;
            case DataSaveFormat.Json:
                // JSONL 无头
                break;
        }

        _headerWritten = true;
    }

    private void WriteRecord(FrameRecord r)
    {
        if (_writer == null) return;

        var ts = r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var src = r.Source == DataVariableSource.Slave ? "Slave" : "Master";
        var addr = r.Source == DataVariableSource.Slave
            ? $"0x{r.SdoIndex:X4}"
            : string.Empty;
        var sub = r.Source == DataVariableSource.Slave
            ? $"0x{r.SdoSubIndex:X2}"
            : string.Empty;

        switch (_format)
        {
            case DataSaveFormat.Csv:
                WriteLineTracked(string.Join(',',
                    CsvEsc(ts), CsvEsc(src), CsvEsc(r.Group), CsvEsc(r.Name),
                    CsvEsc(addr), CsvEsc(sub), CsvEsc(r.DataType),
                    CsvEsc(r.Value), CsvEsc(r.Unit)));
                break;
            case DataSaveFormat.Tsv:
                WriteLineTracked(string.Join('\t', ts, src, r.Group, r.Name, addr, sub, r.DataType, r.Value, r.Unit));
                break;
            case DataSaveFormat.Xls:
                var sb = new StringBuilder(256);
                _ = sb.Append("    <Row>");
                AppendXmlCell(sb, ts);
                AppendXmlCell(sb, src);
                AppendXmlCell(sb, r.Group);
                AppendXmlCell(sb, r.Name);
                AppendXmlCell(sb, addr);
                AppendXmlCell(sb, sub);
                AppendXmlCell(sb, r.DataType);
                AppendXmlCell(sb, r.Value);
                AppendXmlCell(sb, r.Unit);
                _ = sb.Append("</Row>");
                WriteLineTracked(sb.ToString());
                break;
            case DataSaveFormat.Json:
                WriteLineTracked(BuildJson(r, ts, src));
                break;
        }
    }

    private void WriteLineTracked(string s)
    {
        if (_writer == null) return;
        _writer.WriteLine(s);
        // 近似计算字节大小（UTF-8 + 换行）
        _currentFileBytes += Encoding.UTF8.GetByteCount(s) + 2;
    }

    private static string CsvEsc(string? s)
    {
        s ??= string.Empty;
        if (s.Length == 0) return string.Empty;
        bool needQuote = s.IndexOfAny([',', '"', '\n', '\r']) >= 0;
        if (!needQuote) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private static void AppendXmlCell(StringBuilder sb, string? s)
    {
        var escaped = System.Security.SecurityElement.Escape(s ?? string.Empty) ?? string.Empty;
        _ = sb.Append("<Cell><Data ss:Type=\"String\">").Append(escaped).Append("</Data></Cell>");
    }

    private static string BuildJson(FrameRecord r, string ts, string src)
    {
        var sb = new StringBuilder(160);
        _ = sb.Append('{');
        _ = sb.Append("\"ts\":\"").Append(ts).Append('"');
        _ = sb.Append(",\"src\":\"").Append(src).Append('"');
        AppendJson(sb, "group", r.Group);
        AppendJson(sb, "name", r.Name);
        if (r.Source == DataVariableSource.Slave)
        {
            _ = sb.Append(",\"idx\":").Append(r.SdoIndex);
            _ = sb.Append(",\"sub\":").Append(r.SdoSubIndex);
        }

        AppendJson(sb, "type", r.DataType);
        AppendJson(sb, "val", r.Value);
        if (!string.IsNullOrEmpty(r.Unit)) AppendJson(sb, "unit", r.Unit);
        _ = sb.Append('}');
        return sb.ToString();
    }

    private static void AppendJson(StringBuilder sb, string key, string? value)
    {
        _ = sb.Append(",\"").Append(key).Append("\":\"");
        if (!string.IsNullOrEmpty(value))
        {
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': _ = sb.Append("\\\\"); break;
                    case '"': _ = sb.Append("\\\""); break;
                    case '\n': _ = sb.Append("\\n"); break;
                    case '\r': _ = sb.Append("\\r"); break;
                    case '\t': _ = sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) _ = sb.Append($"\\u{(int)c:X4}");
                        else _ = sb.Append(c);
                        break;
                }
            }
        }

        _ = sb.Append('"');
    }

    public void Dispose()
    {
        Stop();
        _signal.Dispose();
    }

    // ===== 内部类型 =====

    internal enum ControlKind { None, Rotate }

    public struct FrameRecord
    {
        public DateTime Timestamp;
        public DataVariableSource Source;
        public string Group;
        public string Name;
        public ushort SdoIndex;
        public byte SdoSubIndex;
        public string DataType;
        public string Value;
        public string Unit;
        internal ControlKind Control;
    }
}