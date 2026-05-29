// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Core.Modbus;

namespace Wpf.Ui.servoStudio.Models;

public enum FrameDirection
{
    Send,
    Response,
}

public enum FrameProtocolStack
{
    Modbus,
    CANopen,
    USB,
    EtherCAT,
}

public static class FrameRuntimeKeys
{
    public const string ModbusSlaveAddress = "modbus.slaveAddress";
    public const string ModbusFunctionCode = "modbus.functionCode";
    public const string ModbusStartAddress = "modbus.startAddress";
    public const string ModbusReadRegisterCount = "modbus.readRegisterCount";
    public const string ModbusWriteValue = "modbus.writeValue";
    public const string ModbusWriteRegisterCount = "modbus.writeRegisterCount";
    public const string ModbusByteCount = "modbus.byteCount";
    public const string ModbusPayload = "modbus.payload";
    public const string ModbusCrc = "modbus.crc";

    public const string CanopenCobId = "canopen.cobId";
    public const string CanopenDlc = "canopen.dlc";
    public const string CanopenCommand = "canopen.command";
    public const string CanopenIndexLow = "canopen.indexLow";
    public const string CanopenIndexHigh = "canopen.indexHigh";
    public const string CanopenSubIndex = "canopen.subIndex";
    public const string CanopenData0 = "canopen.data0";
    public const string CanopenData1 = "canopen.data1";
    public const string CanopenData2 = "canopen.data2";
    public const string CanopenData3 = "canopen.data3";

    public const string UsbChannel = "usb.channel";
    public const string UsbSequence = "usb.sequence";
    public const string UsbDirection = "usb.direction";
    public const string UsbPayloadLength = "usb.payloadLength";
    public const string UsbPayload = "usb.payload";
}

/// <summary>
/// 帧字段组：代表帧中一个逻辑字段，可跨越一个或多个字节。
/// 多字节字段支持展开为独立字节槽，以及高低字节顺序互换。
/// </summary>
public partial class FrameFieldGroup : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private int _byteCount = 1;

    [ObservableProperty]
    private bool _isReadOnly = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExpandable))]
    private bool _isExpanded = false;

    [ObservableProperty]
    private bool _isByteSwapped = false;

    [ObservableProperty]
    private bool _isUserAdded = false;

    [ObservableProperty]
    private bool _countsTowardPayloadLimit = true;

    [ObservableProperty]
    private bool _isLengthLimitExceeded = false;

    [ObservableProperty]
    private bool _isDragging = false;

    [ObservableProperty]
    private bool _isVariableLength = false;

    [ObservableProperty]
    private string _runtimeKey = string.Empty;

    public bool IsExpandable => ByteCount > 1;

    public ObservableCollection<FrameFieldGroup> SubBytes { get; } = [];

    public FrameFieldGroup() { }

    public FrameFieldGroup(string name, int byteCount, string description, bool isReadOnly = false)
    {
        Name = name;
        ByteCount = byteCount;
        Description = description;
        IsReadOnly = isReadOnly;
    }

    public FrameFieldGroup CloneForInsert()
    {
        var clone = new FrameFieldGroup(Name, Math.Max(1, ByteCount), Description)
        {
            CountsTowardPayloadLimit = CountsTowardPayloadLimit,
            IsVariableLength = IsVariableLength,
            RuntimeKey = RuntimeKey,
            IsUserAdded = true,
        };

        if (IsExpanded)
            clone.Expand();

        return clone;
    }

    public void Expand()
    {
        if (!IsExpandable || IsExpanded) return;
        SubBytes.Clear();
        for (int i = 0; i < ByteCount; i++)
        {
            int byteIndex = IsByteSwapped ? (ByteCount - 1 - i) : i;
            SubBytes.Add(new FrameFieldGroup($"{Name}[{byteIndex}]", 1, $"{Description} 第 {byteIndex} 字节", IsReadOnly)
            {
                CountsTowardPayloadLimit = CountsTowardPayloadLimit,
                IsLengthLimitExceeded = IsLengthLimitExceeded,
                IsUserAdded = IsUserAdded,
                RuntimeKey = RuntimeKey,
            });
        }
        IsExpanded = true;
    }

    public void Collapse()
    {
        if (!IsExpanded) return;
        SubBytes.Clear();
        IsExpanded = false;
    }

    public void SwapBytes()
    {
        if (!IsExpandable) return;
        IsByteSwapped = !IsByteSwapped;
        if (IsExpanded)
        {
            Collapse();
            Expand();
        }
    }

    partial void OnIsLengthLimitExceededChanged(bool value)
    {
        foreach (var subByte in SubBytes)
            subByte.IsLengthLimitExceeded = value;
    }

    partial void OnIsUserAddedChanged(bool value)
    {
        foreach (var subByte in SubBytes)
            subByte.IsUserAdded = value;
    }
}

/// <summary>
/// 协议帧格式定义基类，包含字段组列表和帧描述。
/// </summary>
public partial class FrameFormatBase : ObservableObject
{
    private ObservableCollection<FrameFieldGroup> _fields = [];

    [ObservableProperty]
    private string _frameDescription = string.Empty;

    public FrameDirection Direction { get; init; } = FrameDirection.Send;

    public virtual string EditorTitle => Direction == FrameDirection.Send ? "发送帧" : "应答帧";

    public virtual byte? VariantCode => null;

    public virtual string VariantName => string.Empty;

    public FrameFormatBase()
    {
        _fields.CollectionChanged += OnFieldsCollectionChanged;
    }

    public ObservableCollection<FrameFieldGroup> Fields
    {
        get => _fields;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_fields, value)) return;

            _fields.CollectionChanged -= OnFieldsCollectionChanged;
            SetProperty(ref _fields, value);
            _fields.CollectionChanged += OnFieldsCollectionChanged;
            RefreshLengthLimitState();
        }
    }

    public virtual bool IsReadOnly => false;

    public virtual int? MaxPayloadByteCount => null;

    public virtual string PayloadLimitName => "载荷";

    public int PayloadByteCount => Fields.Where(static field => field.CountsTowardPayloadLimit)
        .Sum(static field => Math.Max(1, field.ByteCount));

    public bool IsPayloadLengthExceeded => MaxPayloadByteCount is int maxPayloadByteCount
        && PayloadByteCount > maxPayloadByteCount;

    public string LengthLimitText => MaxPayloadByteCount is int maxPayloadByteCount
        ? $"{PayloadLimitName} {PayloadByteCount}/{maxPayloadByteCount}B"
        : string.Empty;

    public void InsertByteAt(int index)
    {
        int clampedIndex = Math.Clamp(index, 0, Fields.Count);
        Fields.Insert(clampedIndex, new FrameFieldGroup("新字节", 1, "自定义字节") { IsUserAdded = true });
    }

    public void InsertFieldAt(int index, FrameFieldGroup field)
    {
        ArgumentNullException.ThrowIfNull(field);
        int clampedIndex = Math.Clamp(index, 0, Fields.Count);
        Fields.Insert(clampedIndex, field);
    }

    public void RemoveFieldAt(int index)
    {
        if (index >= 0 && index < Fields.Count)
            Fields.RemoveAt(index);
    }

    public bool MoveFieldTo(FrameFieldGroup field, FrameFormatBase targetFormat, int targetIndex)
    {
        if (IsReadOnly || targetFormat.IsReadOnly) return false;

        int sourceIndex = Fields.IndexOf(field);
        if (sourceIndex < 0) return false;

        if (ReferenceEquals(this, targetFormat))
        {
            int clampedTargetIndex = Math.Clamp(targetIndex, 0, Fields.Count);
            if (sourceIndex < clampedTargetIndex) clampedTargetIndex--;
            if (sourceIndex == clampedTargetIndex) return false;

            Fields.Move(sourceIndex, clampedTargetIndex);
            return true;
        }

        Fields.RemoveAt(sourceIndex);
        int insertIndex = Math.Clamp(targetIndex, 0, targetFormat.Fields.Count);
        targetFormat.Fields.Insert(insertIndex, field);
        return true;
    }

    public void RefreshLengthLimitState()
    {
        bool isPayloadLengthExceeded = IsPayloadLengthExceeded;

        foreach (var field in Fields)
        {
            field.IsLengthLimitExceeded = isPayloadLengthExceeded
                && field.IsUserAdded
                && field.CountsTowardPayloadLimit;
        }

        OnPropertyChanged(nameof(PayloadByteCount));
        OnPropertyChanged(nameof(IsPayloadLengthExceeded));
        OnPropertyChanged(nameof(LengthLimitText));
    }

    private void OnFieldsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RefreshLengthLimitState();

    public FrameRuntimeFormat ToRuntimeFormat(FrameProtocolStack protocol)
        => new(
            protocol,
            Direction,
            EditorTitle,
            FrameDescription,
            Fields.Select(static field => new FrameRuntimeField(
                field.RuntimeKey,
                field.Name,
                field.Description,
                Math.Max(1, field.ByteCount),
                field.IsVariableLength,
                field.CountsTowardPayloadLimit)).ToArray(),
            MaxPayloadByteCount,
            VariantCode,
            VariantName);
}

public sealed record FrameRuntimeField(
    string RuntimeKey,
    string Name,
    string Description,
    int ByteCount,
    bool IsVariableLength,
    bool CountsTowardPayloadLimit);

public sealed record FrameRuntimeFormat(
    FrameProtocolStack Protocol,
    FrameDirection Direction,
    string Title,
    string Description,
    IReadOnlyList<FrameRuntimeField> Fields,
    int? MaxPayloadByteCount,
    byte? VariantCode = null,
    string VariantName = "")
{
    public int MinimumByteCount => Fields.Sum(static field => field.IsVariableLength ? 0 : Math.Max(1, field.ByteCount));

    public int GetByteCount(int variableLength)
        => Fields.Sum(field => field.IsVariableLength ? Math.Max(0, variableLength) : Math.Max(1, field.ByteCount));

    public bool TryGetField(string runtimeKey, out FrameRuntimeField field, out int index)
    {
        for (int i = 0; i < Fields.Count; i++)
        {
            if (string.Equals(Fields[i].RuntimeKey, runtimeKey, StringComparison.Ordinal))
            {
                field = Fields[i];
                index = i;
                return true;
            }
        }

        field = null!;
        index = -1;
        return false;
    }

    public bool TryGetFieldOffset(string runtimeKey, int variableLength, out int offset, out FrameRuntimeField field)
    {
        offset = 0;
        foreach (var candidate in Fields)
        {
            if (string.Equals(candidate.RuntimeKey, runtimeKey, StringComparison.Ordinal))
            {
                field = candidate;
                return true;
            }

            offset += candidate.IsVariableLength ? Math.Max(0, variableLength) : Math.Max(1, candidate.ByteCount);
        }

        field = null!;
        offset = -1;
        return false;
    }
}

public sealed record FrameRuntimeSegment(
    string RuntimeKey,
    string Name,
    string Description,
    int Offset,
    byte[] Bytes,
    bool IsComplete);

public sealed record FrameRuntimeParseResult(
    FrameRuntimeFormat Format,
    IReadOnlyList<FrameRuntimeSegment> Segments,
    int FrameByteCount,
    bool HasEnoughBytes,
    bool IsPayloadLengthExceeded)
{
    public bool TryGetSegment(string runtimeKey, out FrameRuntimeSegment segment)
    {
        foreach (var candidate in Segments)
        {
            if (string.Equals(candidate.RuntimeKey, runtimeKey, StringComparison.Ordinal))
            {
                segment = candidate;
                return true;
            }
        }

        segment = null!;
        return false;
    }
}

public sealed record FrameRuntimeProfile(IReadOnlyList<FrameRuntimeFormat> Formats)
{
    public FrameRuntimeFormat Get(FrameProtocolStack protocol, FrameDirection direction, byte? variantCode = null)
    {
        if (protocol == FrameProtocolStack.EtherCAT)
            return Formats.First(format => format.Protocol == protocol);

        FrameRuntimeFormat? matched = Formats.FirstOrDefault(format =>
            format.Protocol == protocol
            && format.Direction == direction
            && format.VariantCode == variantCode);

        if (matched is not null)
            return matched;

        return Formats.First(format => format.Protocol == protocol && format.Direction == direction);
    }

    public static FrameRuntimeProfile CreateDefault()
        => new(
        [
            new ModbusFrameFormat(ModbusFunctionCode.ReadHoldingRegisters, FrameDirection.Send).ToRuntimeFormat(FrameProtocolStack.Modbus),
            new ModbusFrameFormat(ModbusFunctionCode.ReadHoldingRegisters, FrameDirection.Response).ToRuntimeFormat(FrameProtocolStack.Modbus),
            new ModbusFrameFormat(ModbusFunctionCode.WriteSingleRegister, FrameDirection.Send).ToRuntimeFormat(FrameProtocolStack.Modbus),
            new ModbusFrameFormat(ModbusFunctionCode.WriteSingleRegister, FrameDirection.Response).ToRuntimeFormat(FrameProtocolStack.Modbus),
            new ModbusFrameFormat(ModbusFunctionCode.WriteMultipleRegisters, FrameDirection.Send).ToRuntimeFormat(FrameProtocolStack.Modbus),
            new ModbusFrameFormat(ModbusFunctionCode.WriteMultipleRegisters, FrameDirection.Response).ToRuntimeFormat(FrameProtocolStack.Modbus),
            new CanopenFrameFormat(FrameDirection.Send).ToRuntimeFormat(FrameProtocolStack.CANopen),
            new CanopenFrameFormat(FrameDirection.Response).ToRuntimeFormat(FrameProtocolStack.CANopen),
            new UsbFrameFormat(FrameDirection.Send).ToRuntimeFormat(FrameProtocolStack.USB),
            new UsbFrameFormat(FrameDirection.Response).ToRuntimeFormat(FrameProtocolStack.USB),
            new EthercatFrameFormat().ToRuntimeFormat(FrameProtocolStack.EtherCAT),
        ]);
}

public partial class ModbusFunctionFrameTemplate : ObservableObject
{
    [ObservableProperty]
    private ModbusFrameFormat _sendFormat;

    [ObservableProperty]
    private ModbusFrameFormat _responseFormat;

    public ModbusFunctionCode FunctionCode { get; }

    public string DisplayName { get; }

    public ModbusFunctionFrameTemplate(ModbusFunctionCode functionCode)
    {
        FunctionCode = functionCode;
        DisplayName = ModbusFrameFormat.GetFunctionCodeDisplayName(functionCode);
        _sendFormat = new ModbusFrameFormat(functionCode, FrameDirection.Send);
        _responseFormat = new ModbusFrameFormat(functionCode, FrameDirection.Response);
    }

    public override string ToString() => $"{DisplayName} (0x{(byte)FunctionCode:X2})";
}

/// <summary>Modbus RTU 帧格式定义。</summary>
public partial class ModbusFrameFormat : FrameFormatBase
{
    public ModbusFunctionCode FunctionCode { get; }

    public override byte? VariantCode => (byte)FunctionCode;

    public override string VariantName => GetFunctionCodeDisplayName(FunctionCode);

    public ModbusFrameFormat(ModbusFunctionCode functionCode, FrameDirection direction = FrameDirection.Send)
    {
        FunctionCode = functionCode;
        Direction = direction;
        FrameDescription = CreateDescription(functionCode, direction);
        Fields = CreateFields(functionCode, direction);
    }

    public static string GetFunctionCodeDisplayName(ModbusFunctionCode functionCode) => functionCode switch
    {
        ModbusFunctionCode.ReadHoldingRegisters => "读保持寄存器",
        ModbusFunctionCode.WriteSingleRegister => "写单个寄存器",
        ModbusFunctionCode.WriteMultipleRegisters => "写多个寄存器",
        _ => $"功能码 0x{(byte)functionCode:X2}",
    };

    private static string CreateDescription(ModbusFunctionCode functionCode, FrameDirection direction)
        => (functionCode, direction) switch
        {
            (ModbusFunctionCode.ReadHoldingRegisters, FrameDirection.Send) => "0x03 请求：从站地址、功能码、起始地址、寄存器数量、CRC",
            (ModbusFunctionCode.ReadHoldingRegisters, FrameDirection.Response) => "0x03 应答：从站地址、功能码、字节数、数据区、CRC",
            (ModbusFunctionCode.WriteSingleRegister, FrameDirection.Send) => "0x06 请求：从站地址、功能码、寄存器地址、写入值、CRC",
            (ModbusFunctionCode.WriteSingleRegister, FrameDirection.Response) => "0x06 应答：从站地址、功能码、寄存器地址、回显值、CRC",
            (ModbusFunctionCode.WriteMultipleRegisters, FrameDirection.Send) => "0x10 请求：从站地址、功能码、起始地址、寄存器数量、字节数、数据区、CRC",
            (ModbusFunctionCode.WriteMultipleRegisters, FrameDirection.Response) => "0x10 应答：从站地址、功能码、起始地址、寄存器数量、CRC",
            _ => "Modbus RTU 帧格式",
        };

    private static ObservableCollection<FrameFieldGroup> CreateFields(ModbusFunctionCode functionCode, FrameDirection direction)
        => (functionCode, direction) switch
        {
            (ModbusFunctionCode.ReadHoldingRegisters, FrameDirection.Send) =>
            [
                CreateField("从站地址", 1, "目标从站地址 (1~247)", FrameRuntimeKeys.ModbusSlaveAddress),
                CreateField("功能码", 1, "0x03 读保持寄存器", FrameRuntimeKeys.ModbusFunctionCode),
                CreateField("起始地址", 2, "寄存器起始地址 (高字节在前)", FrameRuntimeKeys.ModbusStartAddress),
                CreateField("寄存器数量", 2, "读取寄存器数量 (高字节在前)", FrameRuntimeKeys.ModbusReadRegisterCount),
                CreateField("CRC16", 2, "CRC16 校验 (低字节在前)", FrameRuntimeKeys.ModbusCrc, isReadOnly: true),
            ],
            (ModbusFunctionCode.ReadHoldingRegisters, FrameDirection.Response) =>
            [
                CreateField("从站地址", 1, "响应从站地址", FrameRuntimeKeys.ModbusSlaveAddress),
                CreateField("功能码", 1, "正常 0x03，异常时为 0x83", FrameRuntimeKeys.ModbusFunctionCode),
                CreateField("字节数", 1, "有效数据字节数", FrameRuntimeKeys.ModbusByteCount),
                CreateVariableField("数据区", "寄存器数据区", FrameRuntimeKeys.ModbusPayload),
                CreateField("CRC16", 2, "CRC16 校验 (低字节在前)", FrameRuntimeKeys.ModbusCrc, isReadOnly: true),
            ],
            (ModbusFunctionCode.WriteSingleRegister, FrameDirection.Send) =>
            [
                CreateField("从站地址", 1, "目标从站地址 (1~247)", FrameRuntimeKeys.ModbusSlaveAddress),
                CreateField("功能码", 1, "0x06 写单个寄存器", FrameRuntimeKeys.ModbusFunctionCode),
                CreateField("寄存器地址", 2, "目标寄存器地址 (高字节在前)", FrameRuntimeKeys.ModbusStartAddress),
                CreateField("写入值", 2, "写入寄存器值 (高字节在前)", FrameRuntimeKeys.ModbusWriteValue),
                CreateField("CRC16", 2, "CRC16 校验 (低字节在前)", FrameRuntimeKeys.ModbusCrc, isReadOnly: true),
            ],
            (ModbusFunctionCode.WriteSingleRegister, FrameDirection.Response) =>
            [
                CreateField("从站地址", 1, "响应从站地址", FrameRuntimeKeys.ModbusSlaveAddress),
                CreateField("功能码", 1, "正常 0x06，异常时为 0x86", FrameRuntimeKeys.ModbusFunctionCode),
                CreateField("寄存器地址", 2, "回显寄存器地址", FrameRuntimeKeys.ModbusStartAddress),
                CreateField("回显值", 2, "回显写入值", FrameRuntimeKeys.ModbusWriteValue),
                CreateField("CRC16", 2, "CRC16 校验 (低字节在前)", FrameRuntimeKeys.ModbusCrc, isReadOnly: true),
            ],
            (ModbusFunctionCode.WriteMultipleRegisters, FrameDirection.Send) =>
            [
                CreateField("从站地址", 1, "目标从站地址 (1~247)", FrameRuntimeKeys.ModbusSlaveAddress),
                CreateField("功能码", 1, "0x10 写多个寄存器", FrameRuntimeKeys.ModbusFunctionCode),
                CreateField("起始地址", 2, "起始寄存器地址 (高字节在前)", FrameRuntimeKeys.ModbusStartAddress),
                CreateField("寄存器数量", 2, "写入寄存器数量 (高字节在前)", FrameRuntimeKeys.ModbusWriteRegisterCount),
                CreateField("字节数", 1, "后续数据区字节数", FrameRuntimeKeys.ModbusByteCount),
                CreateVariableField("数据区", "写入数据区", FrameRuntimeKeys.ModbusPayload),
                CreateField("CRC16", 2, "CRC16 校验 (低字节在前)", FrameRuntimeKeys.ModbusCrc, isReadOnly: true),
            ],
            (ModbusFunctionCode.WriteMultipleRegisters, FrameDirection.Response) =>
            [
                CreateField("从站地址", 1, "响应从站地址", FrameRuntimeKeys.ModbusSlaveAddress),
                CreateField("功能码", 1, "正常 0x10，异常时为 0x90", FrameRuntimeKeys.ModbusFunctionCode),
                CreateField("起始地址", 2, "回显起始寄存器地址", FrameRuntimeKeys.ModbusStartAddress),
                CreateField("寄存器数量", 2, "回显写入寄存器数量", FrameRuntimeKeys.ModbusWriteRegisterCount),
                CreateField("CRC16", 2, "CRC16 校验 (低字节在前)", FrameRuntimeKeys.ModbusCrc, isReadOnly: true),
            ],
            _ =>
            [
                CreateField("从站地址", 1, "目标从站地址", FrameRuntimeKeys.ModbusSlaveAddress),
                CreateField("功能码", 1, "Modbus 功能码", FrameRuntimeKeys.ModbusFunctionCode),
                CreateField("CRC16", 2, "CRC16 校验 (低字节在前)", FrameRuntimeKeys.ModbusCrc, isReadOnly: true),
            ],
        };

    private static FrameFieldGroup CreateField(string name, int byteCount, string description, string runtimeKey, bool isReadOnly = false)
        => new(name, byteCount, description, isReadOnly)
        {
            RuntimeKey = runtimeKey,
        };

    private static FrameFieldGroup CreateVariableField(string name, string description, string runtimeKey)
        => new(name, 1, description)
        {
            IsVariableLength = true,
            RuntimeKey = runtimeKey,
        };
}

/// <summary>CANopen 帧格式定义（基于 CAN 2.0A 标准帧）。</summary>
public partial class CanopenFrameFormat : FrameFormatBase
{
    public CanopenFrameFormat(FrameDirection direction = FrameDirection.Send)
    {
        Direction = direction;
        FrameDescription = direction == FrameDirection.Send
            ? "主站请求：0x600+Node SDO/NMT 命令与 8B 数据区"
            : "从站应答：0x580+Node SDO 应答、Abort 或心跳/EMCY 数据";
        Fields =
        [
            new FrameFieldGroup(direction == FrameDirection.Send ? "COB-ID Tx" : "COB-ID Rx", 2, "通信对象标识符 (11-bit, 高字节在前)") { CountsTowardPayloadLimit = false, RuntimeKey = FrameRuntimeKeys.CanopenCobId },
            new FrameFieldGroup("DLC", 1, "数据长度码 (0~8)") { CountsTowardPayloadLimit = false, RuntimeKey = FrameRuntimeKeys.CanopenDlc },
            new("命令字", 1, direction == FrameDirection.Send ? "客户端命令字 CS / NMT 命令" : "服务器应答 CS / Abort") { RuntimeKey = FrameRuntimeKeys.CanopenCommand },
            new("Index L", 1, "SDO 对象索引低字节 / NMT 节点号") { RuntimeKey = FrameRuntimeKeys.CanopenIndexLow },
            new("Index H", 1, "SDO 对象索引高字节 / NMT 保留") { RuntimeKey = FrameRuntimeKeys.CanopenIndexHigh },
            new("SubIndex", 1, "对象子索引 / NMT 保留") { RuntimeKey = FrameRuntimeKeys.CanopenSubIndex },
            new("数据 0", 1, direction == FrameDirection.Send ? "写入数据低字节或 0" : "返回数据低字节或 AbortCode[0]") { RuntimeKey = FrameRuntimeKeys.CanopenData0 },
            new("数据 1", 1, direction == FrameDirection.Send ? "写入数据字节 1" : "返回数据字节 1 或 AbortCode[1]") { RuntimeKey = FrameRuntimeKeys.CanopenData1 },
            new("数据 2", 1, direction == FrameDirection.Send ? "写入数据字节 2" : "返回数据字节 2 或 AbortCode[2]") { RuntimeKey = FrameRuntimeKeys.CanopenData2 },
            new("数据 3", 1, direction == FrameDirection.Send ? "写入数据高字节" : "返回数据高字节或 AbortCode[3]") { RuntimeKey = FrameRuntimeKeys.CanopenData3 },
        ];
    }

    public override int? MaxPayloadByteCount => 8;

    public override string PayloadLimitName => "CANopen 数据区";
}

/// <summary>USB 自定义帧格式定义（HPM 系列 MCU + ThreadX/USBX Bulk 传输）。</summary>
public partial class UsbFrameFormat : FrameFormatBase
{
    public UsbFrameFormat(FrameDirection direction = FrameDirection.Send)
    {
        Direction = direction;
        FrameDescription = direction == FrameDirection.Send
            ? "Host → Device：通道、序列、方向、长度与负载"
            : "Device → Host：通道、序列、方向、长度与负载/状态";
        Fields =
        [
            new("通道", 2, "USB 应用通道 (高字节在前)") { RuntimeKey = FrameRuntimeKeys.UsbChannel },
            new("序列号", 2, "主站递增序列号，用于应答匹配") { RuntimeKey = FrameRuntimeKeys.UsbSequence },
            new("方向", 1, direction == FrameDirection.Send ? "HostToDevice" : "DeviceToHost") { RuntimeKey = FrameRuntimeKeys.UsbDirection },
            new("数据长度", 2, "有效负载字节数 (高字节在前)") { RuntimeKey = FrameRuntimeKeys.UsbPayloadLength },
            new FrameFieldGroup(direction == FrameDirection.Send ? "负载" : "负载/状态", 1, "可变长度数据区") { IsVariableLength = true, RuntimeKey = FrameRuntimeKeys.UsbPayload },
        ];
    }
}

/// <summary>EtherCAT 帧格式定义（只读显示）。</summary>
public partial class EthercatFrameFormat : FrameFormatBase
{
    public override bool IsReadOnly => true;

    public override string EditorTitle => "收发共用";

    public EthercatFrameFormat()
    {
        FrameDescription = "EtherCAT 数据报模板（主站发送与从站应答共用）";
        Fields =
        [
            new("以太网目的 MAC", 6, "目的 MAC 地址 (6 字节)", isReadOnly: true),
            new("以太网源 MAC", 6, "源 MAC 地址 (6 字节)", isReadOnly: true),
            new("EtherType", 2, "以太网类型字段 (0x88A4 = EtherCAT)", isReadOnly: true),
            new("帧头 Length/Type", 2, "EtherCAT 帧头：bit0-10=长度, bit12-15=类型", isReadOnly: true),
            new("命令 CMD", 1, "数据报命令 (APRD/FPRD/BRD/BWR 等)", isReadOnly: true),
            new("索引 IDX", 1, "数据报索引", isReadOnly: true),
            new("地址 Address", 4, "物理地址或逻辑地址 (4 字节)", isReadOnly: true),
            new("长度/标志 Len/Flags", 2, "bit0-10=数据长度, bit14=循环, bit15=下一帧", isReadOnly: true),
            new("IRQ", 2, "中断请求字段", isReadOnly: true),
            new FrameFieldGroup("数据区 Data", 1, "数据区 (可变长度，此处为首字节示意)", isReadOnly: true) { IsVariableLength = true },
            new("工作计数器 WKC", 2, "工作计数器，从站递增", isReadOnly: true),
        ];
    }
}
