// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace Wpf.Ui.servoStudio.Helpers;

/// <summary>
/// 将 EtherCAT ESI (EtherCAT Slave Information) XML 文件转换为
/// SII (Slave Information Interface) 二进制格式，用于写入从站 EEPROM。
/// <para>
/// SII 二进制格式定义参见 ETG.2010 (EtherCAT Slave Information Specification)。
/// </para>
/// </summary>
public static class EsiToSiiConverter
{
    // SII header size: 128 bytes (ECT_SII_START = word 0x0040)
    private const int SII_HEADER_SIZE = 128;

    // SII Category types
    private const ushort CAT_STRINGS = 10;
    private const ushort CAT_GENERAL = 30;
    private const ushort CAT_FMMU = 40;
    private const ushort CAT_SM = 41;
    private const ushort CAT_TXPDO = 50;
    private const ushort CAT_RXPDO = 51;
    private const ushort CAT_END = 0x7FFF;

    /// <summary>
    /// 将 ESI XML 文件转换为 SII 二进制数据。
    /// </summary>
    /// <param name="esiXmlPath">ESI XML 文件路径</param>
    /// <param name="deviceIndex">设备索引 (当 XML 包含多个设备时，默认 0 = 第一个)</param>
    /// <returns>SII 二进制数据 (字节数组)</returns>
    public static byte[] ConvertToSii(string esiXmlPath, int deviceIndex = 0)
    {
        var doc = XDocument.Load(esiXmlPath);
        return ConvertToSii(doc, deviceIndex);
    }

    /// <summary>
    /// 将 ESI XML 文档转换为 SII 二进制数据。
    /// </summary>
    public static byte[] ConvertToSii(XDocument doc, int deviceIndex = 0)
    {
        XElement root = doc.Root ?? throw new InvalidDataException("ESI XML 根元素为空");

        // ── 解析 Vendor 信息 ──
        XElement? vendorElement = root.Element("Vendor");
        uint vendorId = ParseEsiHex32(vendorElement?.Element("Id")?.Value ?? "0");
        string vendorName = vendorElement?.Element("Name")?.Value ?? "";

        // ── 解析 Group 信息 ──
        XElement? groupElement = root.Element("Descriptions")?.Element("Groups")?.Element("Group");
        string groupName = groupElement?.Element("Name")?.Value ?? "";

        // ── 解析 Device 信息 ──
        XElement? devicesElement = root.Element("Descriptions")?.Element("Devices");
        List<XElement> devices = devicesElement?.Elements("Device").ToList() ?? [];
        if (devices.Count == 0)
            throw new InvalidDataException("ESI XML 中未找到 Device 元素");
        if (deviceIndex >= devices.Count)
            throw new ArgumentOutOfRangeException(nameof(deviceIndex),
                $"设备索引 {deviceIndex} 超出范围 (共 {devices.Count} 个设备)");

        XElement device = devices[deviceIndex];
        XElement? typeElement = device.Element("Type");
        uint productCode = ParseEsiHex32(typeElement?.Attribute("ProductCode")?.Value ?? "0");
        uint revisionNo = ParseEsiHex32(typeElement?.Attribute("RevisionNo")?.Value ?? "0");
        uint serialNo = ParseEsiHex32(typeElement?.Attribute("SerialNo")?.Value ?? "0");
        string typeName = typeElement?.Value ?? "";
        string deviceName = device.Element("Name")?.Value ?? typeName;
        string groupType = device.Element("GroupType")?.Value ?? "";

        // ── 解析 Eeprom 配置数据 ──
        XElement? eepromElement = device.Element("Eeprom");
        string configDataHex = eepromElement?.Element("ConfigData")?.Value ?? "";
        string bootStrapHex = eepromElement?.Element("BootStrap")?.Value ?? "";
        int eepromByteSize = int.Parse(eepromElement?.Element("ByteSize")?.Value ?? "2048",
            CultureInfo.InvariantCulture);

        // ── 解析 SyncManager 定义 ──
        var smElements = device.Elements("Sm").ToList();

        // ── 解析 FMMU 定义 ──
        var fmmuElements = device.Elements("Fmmu").ToList();

        // ── 解析 Mailbox 协议 (Device 直接子元素的 Mailbox，非 Info 中的) ──
        // mailbox 有两处：Info > Mailbox (timeout) 和 Device > Mailbox (protocol)
        XElement? mailboxElement = null;
        foreach (XElement mbx in device.Elements("Mailbox"))
        {
            // 带有协议子元素（CoE, FoE 等）的是 protocol mailbox
            if (mbx.Element("CoE") != null || mbx.Element("FoE") != null ||
                mbx.Element("EoE") != null || mbx.Element("SoE") != null ||
                mbx.Element("VoE") != null || mbx.Attribute("DataLinkLayer") != null)
            {
                mailboxElement = mbx;
                break;
            }
        }

        ushort mbxProtocol = 0;
        byte coeDetails = 0;
        byte foeDetails = 0;
        byte eoeDetails = 0;
        if (mailboxElement != null)
        {
            if (mailboxElement.Element("AoE") != null) mbxProtocol |= 0x0001;
            if (mailboxElement.Element("EoE") != null) { mbxProtocol |= 0x0002; eoeDetails = 0x01; }

            if (mailboxElement.Element("CoE") != null)
            {
                mbxProtocol |= 0x0004;
                XElement coe = mailboxElement.Element("CoE")!;
                if (coe.Attribute("SdoInfo")?.Value == "1") coeDetails |= 0x01;
                if (coe.Attribute("PdoAssign")?.Value == "1") coeDetails |= 0x02;
                if (coe.Attribute("PdoConfig")?.Value == "1") coeDetails |= 0x04;
                if (coe.Attribute("PdoUpload")?.Value == "1") coeDetails |= 0x08;
                if (coe.Attribute("CompleteAccess")?.Value == "1") coeDetails |= 0x10;
            }

            if (mailboxElement.Element("FoE") != null) { mbxProtocol |= 0x0008; foeDetails = 0x01; }

            if (mailboxElement.Element("SoE") != null) mbxProtocol |= 0x0010;
            if (mailboxElement.Element("VoE") != null) mbxProtocol |= 0x0020;
        }

        // ── 解析 PDO 定义 ──
        var rxPdos = device.Elements("RxPdo").ToList();
        var txPdos = device.Elements("TxPdo").ToList();

        // ── 构建字符串表 ──
        // SII 字符串索引从 1 开始
        var stringTable = new List<string>();
        // Index 1: Group type name
        stringTable.Add(groupType.Length > 0 ? groupType : groupName);
        // Index 2: Image (empty placeholder)
        stringTable.Add("");
        // Index 3: Device order number / type name
        stringTable.Add(typeName);
        // Index 4: Device display name
        stringTable.Add(deviceName);

        // 预收集所有 PDO 名称和条目名称到字符串表
        // (必须在写入 STRINGS Category 之前完成，否则 PDO 引用的字符串索引无效)
        foreach (XElement? pdo in rxPdos.Concat(txPdos))
        {
            string pdoName = pdo.Element("Name")?.Value ?? "";
            _ = AddOrFindString(stringTable, pdoName);
            foreach (XElement entry in pdo.Elements("Entry"))
            {
                string entryName = entry.Element("Name")?.Value ?? "";
                _ = AddOrFindString(stringTable, entryName);
            }
        }

        // ── 解析标准邮箱地址 ──
        ushort stdMbxRxOffset = 0, stdMbxRxSize = 0;
        ushort stdMbxTxOffset = 0, stdMbxTxSize = 0;
        foreach (XElement? sm in smElements)
        {
            string smName = sm.Value.Trim();
            if (smName == "MBoxOut" || (smName.Contains("MBox") && smName.Contains("Out")))
            {
                stdMbxRxOffset = ParseEsiHex16(sm.Attribute("StartAddress")?.Value ?? "0");
                stdMbxRxSize = ushort.Parse(sm.Attribute("DefaultSize")?.Value ?? "0",
                    CultureInfo.InvariantCulture);
            }
            else if (smName == "MBoxIn" || (smName.Contains("MBox") && smName.Contains("In")))
            {
                stdMbxTxOffset = ParseEsiHex16(sm.Attribute("StartAddress")?.Value ?? "0");
                stdMbxTxSize = ushort.Parse(sm.Attribute("DefaultSize")?.Value ?? "0",
                    CultureInfo.InvariantCulture);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // 构建 SII 二进制数据 — 严格按照 ETG.2010 / SOEM ec_type.h 地址布局
        //
        // SII EEPROM word address map (SOEM 定义):
        //   Word 0x0000-0x0007  (byte 0-15):   Config Area + CRC
        //   Word 0x0008-0x000F  (byte 16-31):  Identity (Vendor/Product/Revision/Serial)
        //   Word 0x0010-0x0013  (byte 32-39):  Reserved
        //   Word 0x0014-0x0015  (byte 40-43):  Bootstrap RX Mailbox (ECT_SII_BOOTRXMBX)
        //   Word 0x0016-0x0017  (byte 44-47):  Bootstrap TX Mailbox (ECT_SII_BOOTTXMBX)
        //   Word 0x0018-0x0019  (byte 48-51):  Standard RX Mailbox  (ECT_SII_RXMBXADR)
        //   Word 0x001A-0x001B  (byte 52-55):  Standard TX Mailbox  (ECT_SII_TXMBXADR)
        //   Word 0x001C-0x001D  (byte 56-59):  Mailbox Protocol     (ECT_SII_MBXPROTO)
        //   Word 0x001E-0x001F  (byte 60-63):  Reserved + EEPROM Size
        //   Word 0x0020-0x003F  (byte 64-127): Reserved
        //   Word 0x0040+        (byte 128+):   Categories           (ECT_SII_START)
        // ═══════════════════════════════════════════════════════════════

        // 固定 128 字节 header (64 words)，初始化为 0
        byte[] header = new byte[SII_HEADER_SIZE];

        // ── Words 0-6 (bytes 0-13): Config Data ──
        byte[] configBytes = HexStringToBytes(configDataHex);
        Array.Copy(configBytes, 0, header, 0, Math.Min(configBytes.Length, 14));

        // ── Word 7 (bytes 14-15): CRC-8 + padding ──
        header[14] = ComputeSiiCrc(header, 0, 14);
        header[15] = 0xFF;

        // ── Words 8-9 (bytes 16-19): Vendor ID ──
        WriteUInt32LE(header, 16, vendorId);
        // ── Words 10-11 (bytes 20-23): Product Code ──
        WriteUInt32LE(header, 20, productCode);
        // ── Words 12-13 (bytes 24-27): Revision ──
        WriteUInt32LE(header, 24, revisionNo);
        // ── Words 14-15 (bytes 28-31): Serial Number ──
        WriteUInt32LE(header, 28, serialNo);

        // ── Words 16-19 (bytes 32-39): Reserved — 已经是 0 ──

        // ── Words 20-21 (bytes 40-43): Bootstrap RX Mailbox (ECT_SII_BOOTRXMBX = 0x0014) ──
        // ── Words 22-23 (bytes 44-47): Bootstrap TX Mailbox (ECT_SII_BOOTTXMBX = 0x0016) ──
        byte[] bootBytes = HexStringToBytes(bootStrapHex);
        Array.Copy(bootBytes, 0, header, 40, Math.Min(bootBytes.Length, 8));

        // ── Words 24-25 (bytes 48-51): Standard RX Mailbox (ECT_SII_RXMBXADR = 0x0018) ──
        WriteUInt16LE(header, 48, stdMbxRxOffset);
        WriteUInt16LE(header, 50, stdMbxRxSize);

        // ── Words 26-27 (bytes 52-55): Standard TX Mailbox (ECT_SII_TXMBXADR = 0x001A) ──
        WriteUInt16LE(header, 52, stdMbxTxOffset);
        WriteUInt16LE(header, 54, stdMbxTxSize);

        // ── Words 28-29 (bytes 56-59): Mailbox Protocol (ECT_SII_MBXPROTO = 0x001C) ──
        WriteUInt16LE(header, 56, mbxProtocol);
        // byte 58-59: reserved (0)

        // ── Word 31 (bytes 62-63): EEPROM Size ──
        // ETG.2010 §6.2: value = (Size in KBit) − 1.
        // e.g. 16 KBit (=2 KByte) → 0x000F, 32 KBit → 0x001F.
        // Writing the raw KBit value (a common bug) causes the ESC to reject
        // the SII on next EEPROM load → slave becomes unconnectable.
        int eepromKBit = Math.Max(1, eepromByteSize * 8 / 1024);
        WriteUInt16LE(header, 62, (ushort)(eepromKBit - 1));

        // ── Words 32-63 (bytes 64-127): Reserved — 已经是 0 ──

        // ═══ 构建 Category 数据 (从 byte 128 / word 0x0040 开始) ═══
        using var catMs = new MemoryStream();
        using var catBw = new BinaryWriter(catMs);

        // Category: STRINGS (type 10)
        WriteCategory(catBw, CAT_STRINGS, BuildStringsCategory(stringTable));

        // Category: GENERAL (type 30)
        WriteCategory(catBw, CAT_GENERAL, BuildGeneralCategory(
            coeDetails, foeDetails, eoeDetails, fmmuElements));

        // Category: FMMU (type 40)
        byte[] fmmuData = BuildFmmuCategory(fmmuElements);
        if (fmmuData.Length > 0)
            WriteCategory(catBw, CAT_FMMU, fmmuData);

        // Category: SM (type 41)
        byte[] smData = BuildSmCategory(smElements);
        if (smData.Length > 0)
            WriteCategory(catBw, CAT_SM, smData);

        // Category: TXPDO (type 50)
        foreach (XElement? txPdo in txPdos)
        {
            byte[] pdoData = BuildPdoCategory(txPdo, stringTable);
            WriteCategory(catBw, CAT_TXPDO, pdoData);
        }

        // Category: RXPDO (type 51)
        foreach (XElement? rxPdo in rxPdos)
        {
            byte[] pdoData = BuildPdoCategory(rxPdo, stringTable);
            WriteCategory(catBw, CAT_RXPDO, pdoData);
        }

        // End marker
        catBw.Write(CAT_END);
        catBw.Write((ushort)0);

        catBw.Flush();
        byte[] categoryData = catMs.ToArray();

        // ═══ 组合: 128 字节 header + category 数据 (仅写有效部分) ═══
        // 不再填充到 eepromByteSize：ESC 读到 0x7FFF end-marker 便停止，
        // end-marker 之后的 EEPROM 内容对协议栈无影响，强制覆写只会增加
        // 写入失败面积与耗时。
        int totalDataLen = SII_HEADER_SIZE + categoryData.Length;
        // 对齐到 word 边界 (应当已经对齐，但稳妥起见)
        if (totalDataLen % 2 != 0) totalDataLen++;
        // 如用户提供的 ByteSize 更小则以其为上限 (防越界)
        if (eepromByteSize > 0 && totalDataLen > eepromByteSize)
            throw new InvalidDataException(
                $"生成的 SII 数据 ({totalDataLen} 字节) 超过 EEPROM 容量 ({eepromByteSize} 字节)。请检查 ESI XML 或 EEPROM/ByteSize 字段。");

        byte[] result = new byte[totalDataLen];
        Array.Copy(header, 0, result, 0, SII_HEADER_SIZE);
        Array.Copy(categoryData, 0, result, SII_HEADER_SIZE, categoryData.Length);

        // 最终一致性校验 — 宁可抛异常阻止烧录，也不要把坏 SII 写进芯片
        ValidateGeneratedSii(result, vendorId, productCode, mbxProtocol);

        return result;
    }

    /// <summary>
    /// 对已生成的 SII 二进制执行一致性校验。任何明显错误都抛出 <see cref="InvalidDataException"/>，
    /// 阻止写入芯片 — 这是防止把从站写砖的最后一道防线。
    /// </summary>
    private static void ValidateGeneratedSii(byte[] sii, uint vendorId, uint productCode, ushort mbxProtocol)
    {
        if (sii.Length < SII_HEADER_SIZE + 4)
            throw new InvalidDataException($"生成的 SII 长度异常: {sii.Length} 字节");

        // Identity 必须非零 (否则主站永远无法识别该从站)
        if (vendorId == 0)
            throw new InvalidDataException("ESI XML 缺少有效的 Vendor Id (<Vendor><Id>...)，拒绝烧录以避免从站 Identity 被清零。");
        if (productCode == 0)
            throw new InvalidDataException("ESI XML 缺少有效的 ProductCode (<Type ProductCode=\"...\">)，拒绝烧录以避免从站 Identity 被清零。");

        // Config area 前 14 字节 (PDI Control / ESC Config / ...) 不应全 0
        bool allZero = true;
        for (int i = 0; i < 14; i++)
            if (sii[i] != 0) { allZero = false; break; }

        if (allZero)
            throw new InvalidDataException(
                "ESI XML 中 <Eeprom><ConfigData> 缺失或全零：PDI Control / ESC Configuration 将为 0，从站烧录后会失去 PDI 访问能力（即 MCU 端再也无法读 ESC）。请在 ESI XML 中补齐 ConfigData 后再烧录。");

        // CRC 自校验
        byte expectedCrc = ComputeSiiCrc(sii, 0, 14);
        if (sii[14] != expectedCrc)
            throw new InvalidDataException(
                $"SII Config Area CRC 自校验失败 (期望 0x{expectedCrc:X2}, 实际 0x{sii[14]:X2})。这是内部错误，请报告。");

        // 若声明了邮箱协议 (FoE/CoE/...)，则标准邮箱地址必须非零
        if (mbxProtocol != 0)
        {
            ushort rxAdr = (ushort)(sii[48] | (sii[49] << 8));
            ushort txAdr = (ushort)(sii[52] | (sii[53] << 8));
            if (rxAdr == 0 || txAdr == 0)
                throw new InvalidDataException(
                    $"ESI XML 声明了邮箱协议 (0x{mbxProtocol:X4}) 但未提供有效的 <Sm> MBoxIn/MBoxOut 地址 (RxMbx=0x{rxAdr:X4}, TxMbx=0x{txAdr:X4})。烧录后邮箱将无法工作，导致 FoE/CoE 全部失败。");
        }
    }

    #region SII Category Builders

    private static byte[] BuildStringsCategory(List<string> strings)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)strings.Count);
        foreach (string s in strings)
        {
            byte[] encoded = Encoding.GetEncoding(28591).GetBytes(s); // ISO-8859-1
            ms.WriteByte((byte)Math.Min(encoded.Length, 255));
            ms.Write(encoded, 0, Math.Min(encoded.Length, 255));
        }
        // Pad to word boundary
        byte[] data = ms.ToArray();
        if (data.Length % 2 != 0)
        {
            byte[] padded = new byte[data.Length + 1];
            Array.Copy(data, padded, data.Length);
            return padded;
        }

        return data;
    }

    private static byte[] BuildGeneralCategory(
        byte coeDetails, byte foeDetails, byte eoeDetails,
        List<XElement> fmmuElements)
    {
        // General category: 32 bytes (16 words)
        byte[] data = new byte[32];
        data[0] = 1;  // GroupIdx (string index 1)
        data[1] = 2;  // ImageIdx (string index 2)
        data[2] = 3;  // OrderIdx (string index 3)
        data[3] = 4;  // NameIdx  (string index 4)
        data[4] = 0;  // reserved
        data[5] = coeDetails;
        data[6] = foeDetails;
        data[7] = eoeDetails;
        // data[8..10] reserved
        // data[11] Flags: bit0=SafeOp support, bit1=not LRW
        data[11] = 0x00;
        // data[12..13] CurrentOnEBus (0)
        // data[14..15] reserved / pad
        // data[16..27] Ports (physical layer)
        // Port 0: MII (0x01), Port 1-3: not implemented (0x00)
        // Each port uses 4 bits: low nibble of data[16]
        data[16] = 0x01; // Port0 = MII
        return data;
    }

    private static byte[] BuildFmmuCategory(List<XElement> fmmuElements)
    {
        if (fmmuElements.Count == 0)
            return [];

        int len = fmmuElements.Count;
        // Pad to word boundary
        if (len % 2 != 0) len++;
        byte[] data = new byte[len];

        for (int i = 0; i < fmmuElements.Count; i++)
        {
            string usage = fmmuElements[i].Value.Trim().ToLowerInvariant();
            data[i] = usage switch
            {
                "outputs" => 0x01,
                "inputs" => 0x02,
                "mboxstate" => 0x03,
                _ => 0xFF // unused
            };
        }

        return data;
    }

    private static byte[] BuildSmCategory(List<XElement> smElements)
    {
        if (smElements.Count == 0)
            return [];

        // 8 bytes per SM
        byte[] data = new byte[smElements.Count * 8];
        for (int i = 0; i < smElements.Count; i++)
        {
            XElement sm = smElements[i];
            int offset = i * 8;
            ushort startAddr = ParseEsiHex16(sm.Attribute("StartAddress")?.Value ?? "0");
            ushort defaultSize = ushort.Parse(sm.Attribute("DefaultSize")?.Value ?? "0",
                CultureInfo.InvariantCulture);
            byte controlByte = (byte)ParseEsiHex16(sm.Attribute("ControlByte")?.Value ?? "0");
            byte enable = (byte)(sm.Attribute("Enable")?.Value == "1" ? 1 : 0);

            string smName = sm.Value.Trim();
            byte smType = smName.ToLowerInvariant() switch
            {
                "mboxout" => 1,
                "mboxin" => 2,
                "outputs" => 3,
                "inputs" => 4,
                _ => 0
            };

            // Write SM entry: StartAddr(2), Length(2), CtrlReg(1), Status(1), Enable(1), Type(1)
            data[offset + 0] = (byte)(startAddr & 0xFF);
            data[offset + 1] = (byte)(startAddr >> 8);
            data[offset + 2] = (byte)(defaultSize & 0xFF);
            data[offset + 3] = (byte)(defaultSize >> 8);
            data[offset + 4] = controlByte;
            data[offset + 5] = 0; // status (not used)
            data[offset + 6] = enable;
            data[offset + 7] = smType;
        }

        return data;
    }

    private static byte[] BuildPdoCategory(XElement pdoElement, List<string> stringTable)
    {
        // PDO header: 8 bytes
        // Entries: 8 bytes each
        var entries = pdoElement.Elements("Entry").ToList();

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // PDO Index
        ushort pdoIndex = ParseEsiHex16(pdoElement.Element("Index")?.Value ?? "0");
        bw.Write(pdoIndex);

        // Number of entries
        bw.Write((byte)entries.Count);

        // SM assignment
        byte smAssign = byte.Parse(pdoElement.Attribute("Sm")?.Value ?? "0",
            CultureInfo.InvariantCulture);
        bw.Write(smAssign);

        // DC Sync
        bw.Write((byte)0);

        // PDO name string index (add to string table if needed)
        string pdoName = pdoElement.Element("Name")?.Value ?? "";
        byte pdoNameIdx = AddOrFindString(stringTable, pdoName);
        bw.Write(pdoNameIdx);

        // Flags (2 bytes)
        bw.Write((ushort)0);

        // Entries
        foreach (XElement? entry in entries)
        {
            ushort entryIndex = ParseEsiHex16(entry.Element("Index")?.Value ?? "0");
            byte subIndex = byte.Parse(entry.Element("SubIndex")?.Value ?? "0",
                CultureInfo.InvariantCulture);
            string entryName = entry.Element("Name")?.Value ?? "";
            byte entryNameIdx = AddOrFindString(stringTable, entryName);
            byte bitLen = byte.Parse(entry.Element("BitLen")?.Value ?? "0",
                CultureInfo.InvariantCulture);

            // Data type mapping (ETG basic types)
            string dtName = entry.Element("DataType")?.Value ?? "";
            byte dataType = MapEtgDataType(dtName);

            bw.Write(entryIndex);      // 2 bytes: Index
            bw.Write(subIndex);        // 1 byte: SubIndex
            bw.Write(entryNameIdx);    // 1 byte: Name string index
            bw.Write(dataType);        // 1 byte: Data type
            bw.Write(bitLen);          // 1 byte: Bit length
            bw.Write((ushort)0);       // 2 bytes: Flags
        }

        bw.Flush();
        return ms.ToArray();
    }

    #endregion

    #region Helper Methods

    private static void WriteCategory(BinaryWriter bw, ushort type, byte[] data)
    {
        // Category header: Type (word) + Size in words (word)
        ushort sizeInWords = (ushort)((data.Length + 1) / 2);
        bw.Write(type);
        bw.Write(sizeInWords);
        bw.Write(data);
        // Pad to word boundary
        if (data.Length % 2 != 0)
            bw.Write((byte)0);
    }

    /// <summary>
    /// CRC-8 校验 (ETG.2010: polynomial 0x07, init 0xFF)
    /// </summary>
    private static byte ComputeSiiCrc(byte[] data, int offset, int length)
    {
        byte crc = 0xFF;
        for (int i = offset; i < offset + length; i++)
        {
            crc ^= data[i];
            for (int bit = 0; bit < 8; bit++)
            {
                if ((crc & 0x80) != 0)
                    crc = (byte)((crc << 1) ^ 0x07);
                else
                    crc = (byte)(crc << 1);
            }
        }

        return crc;
    }

    private static byte[] HexStringToBytes(string hex)
    {
        if (string.IsNullOrEmpty(hex))
            return [];

        hex = hex.Trim();
        if (hex.Length % 2 != 0)
            hex = "0" + hex;

        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture);
        return bytes;
    }

    private static uint ParseEsiHex32(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        value = value.Trim();
        if (value.StartsWith("#x", StringComparison.OrdinalIgnoreCase))
            return uint.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return uint.Parse(value, CultureInfo.InvariantCulture);
    }

    private static ushort ParseEsiHex16(string value)
    {
        return (ushort)(ParseEsiHex32(value) & 0xFFFF);
    }

    private static byte AddOrFindString(List<string> stringTable, string value)
    {
        if (string.IsNullOrEmpty(value))
            return 0; // index 0 = no string

        int idx = stringTable.IndexOf(value);
        if (idx >= 0)
            return (byte)(idx + 1); // 1-based

        if (stringTable.Count >= 255)
            return 0; // overflow protection

        stringTable.Add(value);
        return (byte)stringTable.Count; // 1-based
    }

    private static byte MapEtgDataType(string typeName)
    {
        // ETG.1000 data type indices
        return typeName.ToUpperInvariant() switch
        {
            "BOOL" => 0x01,
            "BYTE" => 0x1E,
            "SINT" => 0x02,
            "INT" => 0x03,
            "DINT" => 0x04,
            "LINT" => 0x14,
            "USINT" => 0x05,
            "UINT" => 0x06,
            "UDINT" => 0x07,
            "ULINT" => 0x15,
            "REAL" => 0x08,
            "LREAL" => 0x11,
            "STRING" => 0x09,
            "BIT2" => 0x31,
            "BIT3" => 0x32,
            "BIT4" => 0x33,
            _ when typeName.StartsWith("STRING(", StringComparison.OrdinalIgnoreCase) => 0x09,
            _ => 0x00
        };
    }

    private static void WriteUInt16LE(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteUInt32LE(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    #endregion
}