// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Core.Modbus;
using Wpf.Ui.servoStudio.Models;

namespace Wpf.Ui.servoStudio.Services;

/// <summary>
/// 帧格式运行时注册表。帧格式修改器保存后更新这里，协议栈收发路径从这里读取当前布局。
/// </summary>
public static class FrameFormatRuntimeService
{
    private static readonly object Gate = new();
    private static FrameRuntimeProfile _profile = FrameRuntimeProfile.CreateDefault();
    private static readonly Dictionary<(FrameProtocolStack Protocol, FrameDirection Direction, byte? VariantCode), FrameRuntimeParseResult> LastParsed = [];

    public static event EventHandler? Changed;

    public static FrameRuntimeProfile CurrentProfile
    {
        get
        {
            lock (Gate)
                return _profile;
        }
    }

    public static void Apply(FrameRuntimeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (Gate)
        {
            _profile = profile;
            LastParsed.Clear();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static FrameRuntimeFormat GetFormat(FrameProtocolStack protocol, FrameDirection direction, byte? variantCode = null)
    {
        lock (Gate)
            return _profile.Get(protocol, direction, variantCode);
    }

    public static FrameRuntimeFormat GetModbusFormat(ModbusFunctionCode functionCode, FrameDirection direction)
        => GetFormat(FrameProtocolStack.Modbus, direction, (byte)functionCode);

    public static FrameRuntimeParseResult ParseRawFrame(
        FrameProtocolStack protocol,
        FrameDirection direction,
        ReadOnlySpan<byte> frameBytes,
        byte? variantCode = null)
    {
        FrameRuntimeFormat format;
        lock (Gate)
            format = _profile.Get(protocol, direction, variantCode);

        return Parse(format, frameBytes);
    }

    public static FrameRuntimeParseResult RecordRawFrame(
        FrameProtocolStack protocol,
        FrameDirection direction,
        ReadOnlySpan<byte> frameBytes)
    {
        byte? variantCode = ResolveVariantCode(protocol, frameBytes);
        return RecordRawFrame(protocol, direction, variantCode, frameBytes);
    }

    public static FrameRuntimeParseResult RecordRawFrame(
        FrameProtocolStack protocol,
        FrameDirection direction,
        byte? variantCode,
        ReadOnlySpan<byte> frameBytes)
    {
        FrameRuntimeFormat format;
        lock (Gate)
            format = _profile.Get(protocol, direction, variantCode);

        FrameRuntimeParseResult result = Parse(format, frameBytes);
        lock (Gate)
            LastParsed[(protocol, direction, format.VariantCode)] = result;

        return result;
    }

    public static bool TryGetLastParsed(
        FrameProtocolStack protocol,
        FrameDirection direction,
        byte? variantCode,
        out FrameRuntimeParseResult result)
    {
        lock (Gate)
            return LastParsed.TryGetValue((protocol, direction, variantCode), out result!);
    }

    public static bool TryGetLastParsed(
        FrameProtocolStack protocol,
        FrameDirection direction,
        out FrameRuntimeParseResult result)
        => TryGetLastParsed(protocol, direction, null, out result);

    private static byte? ResolveVariantCode(FrameProtocolStack protocol, ReadOnlySpan<byte> frameBytes)
    {
        if (protocol != FrameProtocolStack.Modbus || frameBytes.Length < 2)
            return null;

        byte functionCode = (byte)(frameBytes[1] & 0x7F);
        return Enum.IsDefined(typeof(ModbusFunctionCode), functionCode) ? functionCode : null;
    }

    private static FrameRuntimeParseResult Parse(FrameRuntimeFormat format, ReadOnlySpan<byte> frameBytes)
    {
        var segments = new List<FrameRuntimeSegment>(format.Fields.Count);
        int offset = 0;
        int fixedAfterVariable = 0;
        int variableIndex = -1;

        for (int i = 0; i < format.Fields.Count; i++)
        {
            if (format.Fields[i].IsVariableLength && variableIndex < 0)
            {
                variableIndex = i;
                continue;
            }

            if (variableIndex >= 0)
                fixedAfterVariable += Math.Max(1, format.Fields[i].ByteCount);
        }

        for (int i = 0; i < format.Fields.Count; i++)
        {
            FrameRuntimeField field = format.Fields[i];
            int byteCount = Math.Max(1, field.ByteCount);
            if (field.IsVariableLength)
                byteCount = Math.Max(byteCount, frameBytes.Length - offset - fixedAfterVariable);

            int available = Math.Clamp(frameBytes.Length - offset, 0, byteCount);
            byte[] bytes = available == 0 ? [] : frameBytes.Slice(offset, available).ToArray();
            segments.Add(new FrameRuntimeSegment(field.RuntimeKey, field.Name, field.Description, offset, bytes, available == byteCount));
            offset += byteCount;
        }

        int payloadBytes = segments
            .Where((segment, index) => format.Fields[index].CountsTowardPayloadLimit)
            .Sum(static segment => segment.Bytes.Length);
        bool payloadExceeded = format.MaxPayloadByteCount is int maxPayload && payloadBytes > maxPayload;

        return new FrameRuntimeParseResult(
            format,
            segments,
            frameBytes.Length,
            frameBytes.Length >= format.MinimumByteCount,
            payloadExceeded);
    }
}