// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.Generic;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.Services;

namespace Core.CANopen;

internal static class CanOpenFrameCodec
{
    public static CanFrame CreateNmtFrame(NmtCommand command, byte nodeId)
        => BuildFrame(0x000, 2, (byte)command, nodeId, 0x00, 0x00, []);

    public static CanFrame CreateSdoFrame(ushort cobId, byte command, ushort index, byte subIndex, ReadOnlySpan<byte> payload)
    {
        Span<byte> data = stackalloc byte[4];
        int copyLength = Math.Min(4, payload.Length);
        for (int i = 0; i < copyLength; i++)
        {
            data[i] = payload[i];
        }

        return BuildFrame(cobId, 8, command, (byte)(index & 0xFF), (byte)(index >> 8), subIndex, data);
    }

    /// <summary>
    /// 构造一个 CANopen SYNC 帧（COB-ID = 0x080，DLC = 0；可选 1 字节计数器）。<br/>
    /// 由 CANopen 主站作为同步生产者周期性发送，用于驱动从机的同步 PDO / DSP-402 CSP/CSV/CST 模式。
    /// </summary>
    public static CanFrame CreateSyncFrame(byte? counter = null)
    {
        if (counter is byte c)
        {
            var d = new byte[8];
            d[0] = c;
            return new CanFrame(0x080, 1, d);
        }
        return new CanFrame(0x080, 0, new byte[8]);
    }

    /// <summary>
    /// 构造 PDO 帧（不经过 RuntimeFormat，按原始字节序构造）。<br/>
    /// PDO 没有结构化字段（含义由 0x1600/0x1A00 映射决定），所以无需经过 <c>BuildFrame</c>。
    /// </summary>
    public static CanFrame CreatePdoFrame(ushort cobId, ReadOnlySpan<byte> payload)
    {
        var data = new byte[8];
        int len = Math.Min(8, payload.Length);
        for (int i = 0; i < len; i++)
        {
            data[i] = payload[i];
        }

        return new CanFrame(cobId, (byte)len, data);
    }

    public static byte[] ToCanonicalBytes(CanFrame frame)
    {
        var bytes = new byte[3 + frame.Dlc];
        bytes[0] = (byte)(frame.Id >> 8);
        bytes[1] = (byte)(frame.Id & 0xFF);
        bytes[2] = frame.Dlc;
        Buffer.BlockCopy(frame.Data, 0, bytes, 3, frame.Dlc);
        return bytes;
    }

    public static bool TryDecodeSdoResponse(
        FrameRuntimeParseResult parseResult,
        out byte command,
        out ushort index,
        out byte subIndex,
        out byte[] data)
    {
        command = 0;
        index = 0;
        subIndex = 0;
        data = new byte[4];

        if (!TryReadByte(parseResult, FrameRuntimeKeys.CanopenCommand, out command)
            || !TryReadByte(parseResult, FrameRuntimeKeys.CanopenIndexLow, out byte indexLow)
            || !TryReadByte(parseResult, FrameRuntimeKeys.CanopenIndexHigh, out byte indexHigh)
            || !TryReadByte(parseResult, FrameRuntimeKeys.CanopenSubIndex, out subIndex))
        {
            return false;
        }

        index = (ushort)(indexLow | (indexHigh << 8));
        _ = TryReadByte(parseResult, FrameRuntimeKeys.CanopenData0, out data[0]);
        _ = TryReadByte(parseResult, FrameRuntimeKeys.CanopenData1, out data[1]);
        _ = TryReadByte(parseResult, FrameRuntimeKeys.CanopenData2, out data[2]);
        _ = TryReadByte(parseResult, FrameRuntimeKeys.CanopenData3, out data[3]);
        return true;
    }

    private static CanFrame BuildFrame(
        ushort cobId,
        byte dlc,
        byte command,
        byte indexLow,
        byte indexHigh,
        byte subIndex,
        ReadOnlySpan<byte> payload)
    {
        FrameRuntimeFormat format = FrameFormatRuntimeService.GetFormat(FrameProtocolStack.CANopen, FrameDirection.Send);
        var dataBytes = new List<byte>(8);

        foreach (FrameRuntimeField field in format.Fields)
        {
            byte[] fieldBytes = BuildFieldBytes(field, cobId, dlc, command, indexLow, indexHigh, subIndex, payload);
            if (!IsMetadataField(field.RuntimeKey))
            {
                dataBytes.AddRange(fieldBytes);
            }
        }

        byte actualDlc = Math.Min((byte)8, dlc);
        while (dataBytes.Count < actualDlc)
        {
            dataBytes.Add(0);
        }

        byte[] packedData = new byte[8];
        if (dataBytes.Count > 0)
        {
            byte[] payloadBytes = dataBytes.ToArray();
            Buffer.BlockCopy(payloadBytes, 0, packedData, 0, Math.Min(8, payloadBytes.Length));
        }

        return new CanFrame(cobId, actualDlc, packedData);
    }

    private static byte[] BuildFieldBytes(
        FrameRuntimeField field,
        ushort cobId,
        byte dlc,
        byte command,
        byte indexLow,
        byte indexHigh,
        byte subIndex,
        ReadOnlySpan<byte> payload)
    {
        return field.RuntimeKey switch
        {
            FrameRuntimeKeys.CanopenCobId => FitFieldBytes(EncodeBigEndian(cobId, Math.Max(1, field.ByteCount)), field),
            FrameRuntimeKeys.CanopenDlc => FitFieldBytes(EncodeBigEndian(dlc, Math.Max(1, field.ByteCount)), field),
            FrameRuntimeKeys.CanopenCommand => FitFieldBytes([command], field),
            FrameRuntimeKeys.CanopenIndexLow => FitFieldBytes([indexLow], field),
            FrameRuntimeKeys.CanopenIndexHigh => FitFieldBytes([indexHigh], field),
            FrameRuntimeKeys.CanopenSubIndex => FitFieldBytes([subIndex], field),
            FrameRuntimeKeys.CanopenData0 => FitFieldBytes([GetPayloadByte(payload, 0)], field),
            FrameRuntimeKeys.CanopenData1 => FitFieldBytes([GetPayloadByte(payload, 1)], field),
            FrameRuntimeKeys.CanopenData2 => FitFieldBytes([GetPayloadByte(payload, 2)], field),
            FrameRuntimeKeys.CanopenData3 => FitFieldBytes([GetPayloadByte(payload, 3)], field),
            _ => CreatePadding(field),
        };
    }

    private static bool TryReadByte(FrameRuntimeParseResult parseResult, string runtimeKey, out byte value)
    {
        value = 0;
        if (!parseResult.TryGetSegment(runtimeKey, out FrameRuntimeSegment segment)
            || !segment.IsComplete
            || segment.Bytes.Length == 0)
        {
            return false;
        }

        value = segment.Bytes[^1];
        return true;
    }

    private static byte GetPayloadByte(ReadOnlySpan<byte> payload, int index)
        => index >= 0 && index < payload.Length ? payload[index] : (byte)0;

    private static byte[] FitFieldBytes(byte[] source, FrameRuntimeField field)
    {
        if (field.IsVariableLength)
        {
            return source;
        }

        int targetLength = Math.Max(1, field.ByteCount);
        if (source.Length == targetLength)
        {
            return source;
        }

        if (source.Length > targetLength)
        {
            return source[^targetLength..];
        }

        var result = new byte[targetLength];
        Buffer.BlockCopy(source, 0, result, targetLength - source.Length, source.Length);
        return result;
    }

    private static byte[] CreatePadding(FrameRuntimeField field)
        => field.IsVariableLength ? [] : new byte[Math.Max(1, field.ByteCount)];

    private static byte[] EncodeBigEndian(uint value, int width)
    {
        int actualWidth = Math.Max(1, width);
        var bytes = new byte[actualWidth];
        for (int i = 0; i < actualWidth; i++)
        {
            int shift = (actualWidth - 1 - i) * 8;
            bytes[i] = (byte)(value >> shift);
        }

        return bytes;
    }

    private static byte[] EncodeBigEndian(ushort value, int width)
        => EncodeBigEndian((uint)value, width);

    private static byte[] EncodeBigEndian(byte value, int width)
        => EncodeBigEndian((uint)value, width);

    private static bool IsMetadataField(string runtimeKey)
        => string.Equals(runtimeKey, FrameRuntimeKeys.CanopenCobId, StringComparison.Ordinal)
            || string.Equals(runtimeKey, FrameRuntimeKeys.CanopenDlc, StringComparison.Ordinal);
}