// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.Generic;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.Services;

namespace Core.Usb;

internal static class UsbPacketCodec
{
    public static byte[] Serialize(UsbPacket packet)
    {
        FrameDirection direction = packet.Direction == UsbDirection.HostToDevice
            ? FrameDirection.Send
            : FrameDirection.Response;
        FrameRuntimeFormat format = FrameFormatRuntimeService.GetFormat(FrameProtocolStack.USB, direction);
        var bytes = new List<byte>(Math.Max(format.GetByteCount(packet.Payload?.Length ?? 0), 8));

        foreach (FrameRuntimeField field in format.Fields)
        {
            bytes.AddRange(BuildFieldBytes(field, packet));
        }

        return [.. bytes];
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> rawBytes, UsbDirection defaultDirection, out UsbPacket packet)
    {
        FrameDirection frameDirection = defaultDirection == UsbDirection.HostToDevice
            ? FrameDirection.Send
            : FrameDirection.Response;
        FrameRuntimeParseResult parseResult = FrameFormatRuntimeService.ParseRawFrame(FrameProtocolStack.USB, frameDirection, rawBytes);
        if (!parseResult.HasEnoughBytes)
        {
            packet = default;
            return false;
        }

        ushort channel = TryReadUInt16(parseResult, FrameRuntimeKeys.UsbChannel, out ushort parsedChannel)
            ? parsedChannel
            : (ushort)UsbChannel.Control;
        ushort sequence = TryReadUInt16(parseResult, FrameRuntimeKeys.UsbSequence, out ushort parsedSequence)
            ? parsedSequence
            : (ushort)0;
        UsbDirection direction = defaultDirection;
        if (TryReadByte(parseResult, FrameRuntimeKeys.UsbDirection, out byte directionValue)
            && Enum.IsDefined(typeof(UsbDirection), directionValue))
        {
            direction = (UsbDirection)directionValue;
        }

        byte[] payload = TryReadBytes(parseResult, FrameRuntimeKeys.UsbPayload, out byte[] parsedPayload)
            ? parsedPayload
            : [];
        if (TryReadUInt16(parseResult, FrameRuntimeKeys.UsbPayloadLength, out ushort payloadLength)
            && payload.Length > payloadLength)
        {
            payload = payload[..payloadLength];
        }

        packet = new UsbPacket((UsbChannel)channel, sequence, direction, payload);
        return true;
    }

    private static byte[] BuildFieldBytes(FrameRuntimeField field, UsbPacket packet)
    {
        return field.RuntimeKey switch
        {
            FrameRuntimeKeys.UsbChannel => FitFieldBytes(EncodeBigEndian((ushort)packet.Channel, Math.Max(1, field.ByteCount)), field),
            FrameRuntimeKeys.UsbSequence => FitFieldBytes(EncodeBigEndian(packet.Sequence, Math.Max(1, field.ByteCount)), field),
            FrameRuntimeKeys.UsbDirection => FitFieldBytes(EncodeBigEndian((byte)packet.Direction, Math.Max(1, field.ByteCount)), field),
            FrameRuntimeKeys.UsbPayloadLength => FitFieldBytes(EncodeBigEndian((ushort)(packet.Payload?.Length ?? 0), Math.Max(1, field.ByteCount)), field),
            FrameRuntimeKeys.UsbPayload => packet.Payload is not null ? FitVariableBytes(packet.Payload, field) : CreatePadding(field),
            _ => CreatePadding(field),
        };
    }

    private static bool TryReadBytes(FrameRuntimeParseResult parseResult, string runtimeKey, out byte[] bytes)
    {
        if (parseResult.TryGetSegment(runtimeKey, out FrameRuntimeSegment segment) && segment.IsComplete)
        {
            bytes = segment.Bytes;
            return true;
        }

        bytes = [];
        return false;
    }

    private static bool TryReadUInt16(FrameRuntimeParseResult parseResult, string runtimeKey, out ushort value)
    {
        value = 0;
        if (!TryReadBytes(parseResult, runtimeKey, out byte[] bytes) || bytes.Length == 0)
        {
            return false;
        }

        foreach (byte current in bytes)
        {
            value = (ushort)((value << 8) | current);
        }

        return true;
    }

    private static bool TryReadByte(FrameRuntimeParseResult parseResult, string runtimeKey, out byte value)
    {
        value = 0;
        return TryReadBytes(parseResult, runtimeKey, out byte[] bytes)
            && bytes.Length > 0
            && (value = bytes[^1]) >= 0;
    }

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

    private static byte[] FitVariableBytes(byte[] source, FrameRuntimeField field)
        => field.IsVariableLength ? source : FitFieldBytes(source, field);

    private static byte[] CreatePadding(FrameRuntimeField field)
        => field.IsVariableLength ? [] : new byte[Math.Max(1, field.ByteCount)];

    private static byte[] EncodeBigEndian(ushort value, int width)
        => EncodeBigEndian((uint)value, width);

    private static byte[] EncodeBigEndian(byte value, int width)
        => EncodeBigEndian((uint)value, width);

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
}