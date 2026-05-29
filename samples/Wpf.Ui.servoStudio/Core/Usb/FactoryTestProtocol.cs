// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Text;

namespace Core.Usb;

/// <summary>
/// 厂家自动化测试占位协议 —— 定义上位机下发测试命令、从机回传测试结果所使用的 USB 帧格式。
/// 帧承载于 <see cref="UsbChannel.FactoryTest"/> 通道。<br/>
/// <b>本协议为占位规范，具体字段 / 命令编号待与从机固件对齐后调整。</b>
/// <para>
/// <b>请求帧负载（上位机 → 从机）：</b><br/>
/// [0]     魔术字 0xA5<br/>
/// [1]     协议版本（当前 0x01）<br/>
/// [2]     测试项编号 <see cref="FactoryTestId"/><br/>
/// [3]     子命令 / 保留（默认 0x00）<br/>
/// [4..5]  请求序号（big-endian uint16，用于请求 / 应答配对）<br/>
/// [6..]   可选参数字节<br/>
/// </para>
/// <para>
/// <b>应答帧负载（从机 → 上位机）：</b><br/>
/// [0]     魔术字 0x5A<br/>
/// [1]     协议版本（当前 0x01）<br/>
/// [2]     测试项编号（回显请求值）<br/>
/// [3]     结果码 <see cref="FactoryTestResultCode"/><br/>
/// [4..5]  请求序号（回显，big-endian uint16）<br/>
/// [6]     故障级别 / 阶段码（0 表示无；非 0 指示哪一级失败）<br/>
/// [7]     详情字符串字节数 n<br/>
/// [8..8+n) 详情 ASCII 文本（可选，便于人工排查）<br/>
/// </para>
/// </summary>
public static class FactoryTestProtocol
{
    /// <summary>请求帧魔术字。</summary>
    public const byte RequestMagic = 0xA5;

    /// <summary>应答帧魔术字。</summary>
    public const byte ResponseMagic = 0x5A;

    /// <summary>当前协议版本。</summary>
    public const byte ProtocolVersion = 0x01;

    /// <summary>请求 / 应答帧的最小头部长度（不含可选参数 / 详情）。</summary>
    public const int RequestHeaderSize = 6;

    /// <summary>应答帧最小头部长度（含故障级别与详情长度字段）。</summary>
    public const int ResponseHeaderSize = 8;

    /// <summary>
    /// 构造一条测试请求帧负载。
    /// </summary>
    /// <param name="testId">测试项编号。</param>
    /// <param name="sequence">请求序号，用于与应答配对。</param>
    /// <param name="subCommand">子命令 / 保留字节，默认 0。</param>
    /// <param name="parameters">可选参数字节，默认无。</param>
    public static byte[] BuildRequest(
        FactoryTestId testId,
        ushort sequence,
        byte subCommand = 0x00,
        ReadOnlySpan<byte> parameters = default)
    {
        byte[] payload = new byte[RequestHeaderSize + parameters.Length];

        payload[0] = RequestMagic;
        payload[1] = ProtocolVersion;
        payload[2] = (byte)testId;
        payload[3] = subCommand;
        payload[4] = (byte)(sequence >> 8);
        payload[5] = (byte)(sequence & 0xFF);

        if (parameters.Length > 0)
        {
            parameters.CopyTo(payload.AsSpan(RequestHeaderSize));
        }

        return payload;
    }

    /// <summary>
    /// 尝试将一条应答帧负载解析为 <see cref="FactoryTestResponse"/>。
    /// </summary>
    /// <returns>解析成功返回 true；魔术字 / 长度不匹配返回 false。</returns>
    public static bool TryParseResponse(byte[]? payload, out FactoryTestResponse response)
    {
        response = default;

        if (payload is null || payload.Length < ResponseHeaderSize)
        {
            return false;
        }

        if (payload[0] != ResponseMagic || payload[1] != ProtocolVersion)
        {
            return false;
        }

        byte testId = payload[2];
        byte resultCode = payload[3];
        ushort sequence = (ushort)((payload[4] << 8) | payload[5]);
        byte faultStage = payload[6];
        int detailLen = payload[7];

        string detail = string.Empty;
        if (detailLen > 0 && payload.Length >= ResponseHeaderSize + detailLen)
        {
            detail = Encoding.ASCII.GetString(payload, ResponseHeaderSize, detailLen);
        }

        response = new FactoryTestResponse(
            (FactoryTestId)testId,
            (FactoryTestResultCode)resultCode,
            sequence,
            faultStage,
            detail);

        return true;
    }
}

/// <summary>
/// 厂家测试项编号（占位）。分为板级 / 外设 / 功能三组，编号区间预留扩展空间。
/// 具体项目待与从机固件测试用例对齐。
/// </summary>
public enum FactoryTestId : byte
{
    /// <summary>未指定。</summary>
    None = 0x00,

    // ───── 板级测试（0x10 ~ 0x2F）─────

    /// <summary>板级 - 供电电压检测。</summary>
    BoardPowerRail = 0x10,

    /// <summary>板级 - 主控时钟 / 晶振检测。</summary>
    BoardClock = 0x11,

    /// <summary>板级 - Flash 读写自检。</summary>
    BoardFlash = 0x12,

    /// <summary>板级 - RAM 自检。</summary>
    BoardRam = 0x13,

    // ───── 外设测试（0x30 ~ 0x4F）─────

    /// <summary>外设 - 编码器接口。</summary>
    PeripheralEncoder = 0x30,

    /// <summary>外设 - 数字 IO 回环。</summary>
    PeripheralDigitalIo = 0x31,

    /// <summary>外设 - 模拟量采样。</summary>
    PeripheralAdc = 0x32,

    /// <summary>外设 - 通信收发器（CAN / 485）。</summary>
    PeripheralTransceiver = 0x33,

    // ───── 功能测试（0x50 ~ 0x6F）─────

    /// <summary>功能 - 电流环自检。</summary>
    FunctionCurrentLoop = 0x50,

    /// <summary>功能 - 母线 / 温度保护逻辑。</summary>
    FunctionProtection = 0x51,

    /// <summary>功能 - 使能 / 抱闸输出。</summary>
    FunctionBrakeOutput = 0x52,
}

/// <summary>从机回传的测试结果码（占位）。</summary>
public enum FactoryTestResultCode : byte
{
    /// <summary>合格。</summary>
    Pass = 0x00,

    /// <summary>不合格（故障）。具体级别见 <see cref="FactoryTestResponse.FaultStage"/>。</summary>
    Fail = 0x01,

    /// <summary>该测试项在当前硬件 / 固件上不可用。</summary>
    Unavailable = 0x02,
}

/// <summary>解析后的厂家测试应答。</summary>
public readonly struct FactoryTestResponse
{
    public FactoryTestResponse(
        FactoryTestId testId,
        FactoryTestResultCode resultCode,
        ushort sequence,
        byte faultStage,
        string detail)
    {
        TestId = testId;
        ResultCode = resultCode;
        Sequence = sequence;
        FaultStage = faultStage;
        Detail = detail ?? string.Empty;
    }

    /// <summary>对应的测试项编号。</summary>
    public FactoryTestId TestId { get; }

    /// <summary>结果码。</summary>
    public FactoryTestResultCode ResultCode { get; }

    /// <summary>请求序号（回显）。</summary>
    public ushort Sequence { get; }

    /// <summary>故障级别 / 阶段码：0 表示无；非 0 指示哪一级失败。</summary>
    public byte FaultStage { get; }

    /// <summary>详情文本（可选，便于人工排查）。</summary>
    public string Detail { get; }
}
