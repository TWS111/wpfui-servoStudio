# 主站核心原理与执行流程文档 ✨

> 喵～ 这是 GX Servo Studio 主站核心的完整技术解析文档，包含原理、调用链、执行流程和伺服控制细节～

---

## 目录

1. [整体架构概览](#1-整体架构概览)
2. [核心接口定义 — IServoMaster / IServoAxis](#2-核心接口定义)
3. [协议栈实现详解](#3-协议栈实现详解)
   - 3.1 [CANopen Master](#31-canopen-master)
   - 3.2 [Modbus RTU Master](#32-modbus-rtu-master)
   - 3.3 [CiA402 从站包装层](#33-cia402-从站包装层)
4. [H寄存器对象字典 — HVariables](#4-h寄存器对象字典--hvariables)
5. [ViewModel 层与伺服控制](#5-viewmodel-层与伺服控制)
   - 5.1 [ControlViewModel — 状态机控制](#51-controlviewmodel--状态机控制)
   - 5.2 [DashboardViewModel — 监控数据读取](#52-dashboardviewmodel--监控数据读取)
   - 5.3 [QuickControlViewModel — 模式参数快调与波形采样](#53-quickcontrolviewmodel--模式参数快调与波形采样)
6. [EtherCAT CoE SDO 与 PDO 详解](#6-ethercat-coe-sdo-与-pdo-详解)
7. [通信丢失看门狗](#7-通信丢失看门狗)
8. [完整伺服控制执行流程](#8-完整伺服控制执行流程)
9. [关键文件位置速查](#9-关键文件位置速查)

---

## 1. 整体架构概览

```text
┌─────────────────────────────────────────────────────────────┐
│                     ViewModel 层（UI逻辑）                   │
│  ControlViewModel  DashboardViewModel  QuickControlViewModel │
└──────────────────────────┬──────────────────────────────────┘
                           │ IServoMaster / IServoAxis
┌──────────────────────────▼──────────────────────────────────┐
│                    协议适配抽象层                             │
│   EtherCATServoMasterAdapter   ModbusRtuMaster   CanOpenMaster│
└─────┬──────────────────────┬─────────────────────┬──────────┘
      │ SOEM DLL             │ SerialPortStream     │ USB2XXX / ZLG
┌─────▼──────┐    ┌──────────▼──────────┐   ┌──────▼──────────┐
│  EtherCAT  │    │    Modbus RTU        │   │   CANopen/CAN   │
│  物理链路   │    │   RS-485 物理链路    │   │  CAN 物理链路   │
└────────────┘    └────────────────────┘    └────────────────┘
```

整个主站核心分三层喵：

- **接口层**：`IServoMaster` / `IServoAxis`，让上层 ViewModel 完全不感知底层协议差异
- **协议实现层**：三套独立实现，分别包装 SOEM (EtherCAT)、SerialPortStream (Modbus) 和 USB2XXX SDK (CANopen)
- **ViewModel 层**：通过统一接口执行伺服控制，定时读取状态，响应异常事件

---

## 2. 核心接口定义

**文件：** `Core/IServoMaster.cs`

### IServoMaster

主站操作接口，提供协议无关的 SDO 读写能力。

```csharp
public interface IServoMaster
{
    // 不抛异常的安全读取，成功返回 true，失败返回 false + out default(T)
    bool TryReadSDO<T>(int slaveAddr, ushort index, byte subIndex, out T value);

    // 不抛异常的安全写入
    bool TryWriteSDO<T>(int slaveAddr, ushort index, byte subIndex, T value);

    // 抛异常版本，失败时 throw InvalidOperationException
    T ReadSDO<T>(int slaveAddr, int index, int subIndex);
}
```

**泛型类型支持：** `byte`, `sbyte`, `ushort`, `short`, `uint`, `int`, `float`（内部按协议规范做端序转换）

### IServoAxis

从站/轴的抽象，代表一个物理伺服驱动器节点。

```csharp
public interface IServoAxis
{
    int    SlaveAddr;       // EtherCAT: 自动编号(1-N); Modbus: 1-247; CANopen: 1-127
    string? SlaveName;      // 设备名称（EtherCAT从ESI读取，Modbus/CANopen由探针填入）
    string? SoftwareVersion;// 固件版本号
}
```

### 协议地址映射规则

| 协议     | 地址含义           | 范围      |
|----------|--------------------|-----------|
| EtherCAT | 拓扑顺序从站编号   | 1 ~ N     |
| Modbus   | RTU 从站地址       | 1 ~ 247   |
| CANopen  | Node-ID            | 1 ~ 127   |

---

## 3. 协议栈实现详解

### 3.1 CANopen Master

**文件：** `Core/CANopen/CanOpenMaster.cs`

#### 核心字段与线程模型

```text
CanOpenMaster
├── _bus: ICanBus               ← 物理层（USB2XXX / ZLG / VirtualBus）
├── _pendingSdoMap: Dictionary<byte, SdoPendingEntry>   ← 每 nodeId 一个 SDO 等待槽
├── _dispatchThread: Thread     ← 后台帧分发线程（独立于主线程）
└── _rxQueue: BlockingCollection<CanFrame>              ← 接收帧队列
```

**帧分发线程 (`DispatchLoop`)：**

```text
while (running)
{
    frame = _rxQueue.Take()       // 阻塞等待新帧
    switch (frame.CobId >> 7)
    {
        case 0x580 >> 7:  → ProcessSdoResponse(frame)   // SDO 服务器响应
        case 0x700 >> 7:  → ProcessHeartbeat(frame)     // 心跳 / NMT 状态
        case 0x080 >> 7:  → ProcessEmcy(frame)           // 紧急对象 EMCY
        default:          → ProcessPdo(frame)            // PDO（TPDO → 主站接收）
    }
}
```

#### SDO 协议基础：为什么"读取"也要先发送？

SDO（Service Data Object）是 CANopen 的**请求-响应协议**，主站永远是发起方。"读取从站对象"在协议层分两步：

```text
主站（CanOpenMaster）                    从站（伺服驱动器）
        │                                        │
        │── 上传请求帧 (Upload Request) ────────►  │
        │   CobId = 0x600 + nodeId               │  ← 主站主动发出，触发从站返回数据
        │   CS    = 0x40                         │
        │   data  = [index_lo, index_hi, sub, 0] │
        │                                        │
        │◄── 上传响应帧 (Upload Response) ────────│
        │   CobId = 0x580 + nodeId               │  ← 从站携带实际数据回复
        │   CS    = 0x4F(1B)/0x4B(2B)/0x43(4B)  │
        │   data  = [index_lo, index_hi, sub, value...]
```

因此 `TryReadSDO` 中的 `_bus.Send()` 发出的是**上传请求**，不是在写数据到从站。
方法名"Read"是应用层语义（"我要读一个值"），协议层必须先发请求才能收到数据。

对比写入（下载）：主站发的是含实际数据的下载请求帧，从站回复空的确认帧（CS=0x60）。
两者都需要 `_bus.Send()`，区别仅在于请求帧是否携带数据。

#### SDO 传输流程（加速传输，≤4字节）

**读取（上传，Upload）**

```text
TryReadSDO<T>(slaveAddr, index, subIndex)
    │
    ├─1. 构造上传请求帧:
    │       CobId = 0x600 + nodeId
    │       data  = [0x40, index_lo, index_hi, subIndex, 0, 0, 0, 0]
    │       （CS=0x40 表示 Upload Request，数据字段全零，由从站填充）
    │
    ├─2. _bus.Send(frame)
    │       ← 发出"请求"，触发从站把对象值打包回传
    │
    ├─3. _pendingSdoMap[nodeId] = new SdoPendingEntry
    │       { Index=index, SubIndex=subIndex, Event=new ManualResetEvent(false) }
    │
    ├─4. entry.Event.WaitOne(timeout=500ms)
    │       ← 当前线程阻塞，等待 DispatchLoop 收到响应后唤醒
    │
    └─5. DispatchLoop → ProcessSdoResponse(frame):
             frame.CobId == 0x580 + nodeId → 匹配等待槽
             CS 解析:
               0x4F → 1字节有效 (data[4])
               0x4B → 2字节有效 (data[4..5])
               0x47 → 3字节有效 (data[4..6])
               0x43 → 4字节有效 (data[4..7])
               0x80 → 中止传输，data[4..7] 为 AbortCode（解析为错误描述）
             → entry.Value = 小端序解析后的原始字节
             → entry.Success = (CS != 0x80)
             → entry.Event.Set()   ← 唤醒步骤4的阻塞

    返回 entry.Success，out value = 泛型类型转换(entry.Value)
```

**写入（下载，Download）**

```text
TryWriteSDO<T>(slaveAddr, index, subIndex, value)
    │
    ├─1. 将 value 序列化为 byte[4]（小端序）
    │
    ├─2. 根据类型字节长度选择 CS（命令说明符）:
    │       sizeof(T)==1 → CS=0x2F  (1字节有效)
    │       sizeof(T)==2 → CS=0x2B  (2字节有效)
    │       sizeof(T)==3 → CS=0x27  (3字节有效)
    │       sizeof(T)==4 → CS=0x23  (4字节有效)
    │       变长         → CS=0x22  (段传输，不含数据长度)
    │
    ├─3. 构造下载请求帧:
    │       CobId = 0x600 + nodeId
    │       data  = [CS, index_lo, index_hi, subIndex, val0, val1, val2, val3]
    │       ← 数据已包含在请求帧中，从站直接写入对象字典
    │
    ├─4. _bus.Send(frame)
    │
    └─5. 等待下载响应帧 (CobId=0x580+nodeId):
             CS=0x60 → 下载成功（确认帧，数据字段全零）
             CS=0x80 → 中止传输（AbortCode 指示原因）
```

**COB-ID 分配规则（CANopen 标准）**

| 方向         | COB-ID 范围         | 说明                     |
|--------------|---------------------|--------------------------|
| 主站→从站请求 | 0x600 + nodeId      | SDO Client → Server      |
| 从站→主站响应 | 0x580 + nodeId      | SDO Server → Client      |
| 从站心跳      | 0x700 + nodeId      | Heartbeat Producer       |
| 从站紧急      | 0x080 + nodeId      | Emergency Object (EMCY)  |
| RPDO1（主→从）| 0x200 + nodeId      | Receive PDO 1            |
| TPDO1（从→主）| 0x180 + nodeId      | Transmit PDO 1           |

#### DispatchLoop 与 SDO 等待槽的协作

```text
主线程（ViewModel 调用）          后台 DispatchLoop 线程
        │                                  │
TryReadSDO()                               │
  _bus.Send(request)                       │
  _pendingSdoMap[nodeId]=entry             │
  entry.Event.WaitOne() ──阻塞─────────────┤
                                           │ 物理层收到 CAN 帧
                                           │ _rxQueue.Add(frame)
                                           │ ↓
                                           │ frame = _rxQueue.Take()
                                           │ CobId==0x580+nodeId
                                           │ ProcessSdoResponse()
                                           │   entry.Value = 解析结果
                                           │   entry.Event.Set() ──唤醒──►
                                           │                              │
                                           │                        entry.Success?
                                           │                        out value = entry.Value
                                           │                        return true/false
```

每个 nodeId 同一时刻只允许一个 SDO 事务（`_pendingSdoMap` 每 nodeId 一个槽），
并发调用同一节点的 SDO 会串行化（通过 `lock` 或 `SemaphoreSlim` 保护）。

#### PDO 配置流程

```text
ConfigureRpdoMapping(node, rpdoIndex, cobIdOverride, transmissionType, mapEntries[])
    │
    ├─1. 写 0x1400+rpdoIndex, sub1 = cobId | 0x80000000  ← 先禁用
    ├─2. 写 0x1600+rpdoIndex, sub0 = 0                   ← 清空映射数量
    ├─3. 逐一写 0x1600+rpdoIndex, sub1..N = mapEntries[i] ← 写入映射对象
    ├─4. 写 0x1600+rpdoIndex, sub0 = mapEntries.Length    ← 恢复映射数量
    ├─5. 写 0x1400+rpdoIndex, sub2 = transmissionType
    └─6. 写 0x1400+rpdoIndex, sub1 = cobId & ~0x80000000  ← 启用 RPDO
```

#### PDO 实时数据通路

PDO（Process Data Object）是 CANopen 的**无确认广播机制**，不走 SDO 的请求-响应流程，帧开销极小，适合周期性过程数据交换喵～

**COB-ID 默认分配（CiA301 标准）**

| PDO 类型 | 索引 | COB-ID 基址 | 方向 |
|----------|------|-------------|------|
| RPDO1    | 0    | 0x200 + nodeId | 主站 → 从站 |
| RPDO2    | 1    | 0x300 + nodeId | 主站 → 从站 |
| RPDO3    | 2    | 0x400 + nodeId | 主站 → 从站 |
| RPDO4    | 3    | 0x500 + nodeId | 主站 → 从站 |
| TPDO1    | 0    | 0x180 + nodeId | 从站 → 主站 |
| TPDO2    | 1    | 0x280 + nodeId | 从站 → 主站 |
| TPDO3    | 2    | 0x380 + nodeId | 从站 → 主站 |
| TPDO4    | 3    | 0x480 + nodeId | 从站 → 主站 |

**RPDO 发送（主站 → 从站）**

```text
SendPdo(node, rpdoIndex, payload)
    │
    ├─1. 验证 node ∈ [1..127]，payload.Length ≤ 8
    ├─2. cobId = RpdoBase[rpdoIndex] + node
    │       RpdoBase = { 0x200, 0x300, 0x400, 0x500 }
    ├─3. 构造 CanFrame(cobId, payload)
    ├─4. lock(_txLock) → _bus.Send(frame)
    └─5. RecordRuntimeFrame(frame)   ← 帧记录（调试/诊断用）
```

**TPDO 接收（从站 → 主站）**

```text
DispatchLoop 收到帧
    │
    └─ CobId 范围判断:
         0x181..0x1FF → TPDO1 (tpdoIndex=0)
         0x281..0x2FF → TPDO2 (tpdoIndex=1)
         0x381..0x3FF → TPDO3 (tpdoIndex=2)
         0x481..0x4FF → TPDO4 (tpdoIndex=3)
         → node = CobId & 0x7F
         → payload = frame.Data[0..DLC-1]
         → PdoReceived?.Invoke(node, tpdoIndex, payload)
```

**SYNC 生产者（主站驱动周期同步）**

```text
StartSyncProducer(period, timerKind, includeCounter)
    │
    ├─ 创建周期定时器（PeriodicTimer / HighResolutionTimer / ThreadPoolTimer）
    └─ 每拍执行 SendSync(counter?):
             CanFrame(cobId=0x080, DLC=0)          ← 无计数器
             CanFrame(cobId=0x080, DLC=1, [c])     ← 有计数器（1..240 循环）
             → _bus.Send(frame)
             → SyncTick?.Invoke()                  ← 通知订阅者发送 RPDO
```

**完整 PDO 周期执行链（以 CSP 模式为例）**

```text
MotionTypeViewModel 初始化:
    1. SendNmt(EnterPreOperational, node)
    2. ConfigureRpdoMapping(node, 0, null, 1, [0x60400010, 0x60600008])
       ← RPDO1: ControlWord(16bit) + ModesOfOperation(8bit)
    3. ConfigureRpdoMapping(node, 1, null, 1, [0x607A0020])
       ← RPDO2: TargetPosition(32bit)
    4. ConfigureTpdoMapping(node, 0, null, 1, [0x60410010, 0x60610008], 0, 0)
       ← TPDO1: StatusWord(16bit) + ModesOfOperationDisplay(8bit)
    5. ConfigureTpdoMapping(node, 1, null, 1, [0x60640020], 0, 0)
       ← TPDO2: PositionActualValue(32bit)
    6. SendNmt(StartRemoteNode, node)
    7. StartSyncProducer(period=1ms, timerKind, includeCounter=false)
    8. SyncTick += OnSyncTick

OnSyncTick():
    → SendPdo(node, 0, [controlWord_lo, controlWord_hi, mode])
    → SendPdo(node, 1, [pos_b0, pos_b1, pos_b2, pos_b3])

PdoReceived(node, tpdoIndex=0, payload):
    → statusWord = payload[0] | (payload[1]<<8)
    → modeDisplay = payload[2]

PdoReceived(node, tpdoIndex=1, payload):
    → actualPosition = BitConverter.ToInt32(payload, 0)
```

**映射条目格式（mapEntries 数组元素）**

```text
uint mapEntry = (index << 16) | (subIndex << 8) | bitLength
示例：
  0x60400010 → index=0x6040, sub=0x00, bits=16  (ControlWord, 2字节)
  0x607A0020 → index=0x607A, sub=0x00, bits=32  (TargetPosition, 4字节)
  0x60600008 → index=0x6060, sub=0x00, bits=8   (ModesOfOperation, 1字节)
```

**传输类型（TransmissionType）**

| 值 | 含义 |
|----|------|
| 0  | 同步非周期（SYNC触发，但只在数据变化时发送） |
| 1..240 | 同步周期（每N个SYNC发送一次） |
| 254 | 异步（事件驱动，从站自主发送） |
| 255 | 异步（事件驱动，含 inhibit time 限速） |

#### NMT 控制

| 命令                  | CobId | Data[0] | 说明               |
|-----------------------|-------|---------|-------------------|
| `StartRemoteNode`     | 0x000 | 0x01    | 进入 Operational  |
| `StopRemoteNode`      | 0x000 | 0x02    | 进入 Stopped       |
| `EnterPreOperational` | 0x000 | 0x80    | 进入 Pre-Op        |
| `ResetNode`           | 0x000 | 0x81    | 完全复位           |
| `ResetCommunication`  | 0x000 | 0x82    | 通信层复位         |

---

### 3.2 Modbus RTU Master

**文件：** `Core/Modbus/ModbusRtuMaster.cs`

#### 帧格式（RTU 模式）

```text
[SlaveAddr:1] [FuncCode:1] [Data:N] [CRC16:2]
```

CRC16 使用 Modbus 标准多项式 0xA001（LSB-first 反转多项式）。

#### 功能码实现

| 功能码 | 方法                      | 说明                     |
|--------|---------------------------|--------------------------|
| 0x03   | `ReadHoldingRegisters()`  | 读保持寄存器（大端序）   |
| 0x06   | `WriteSingleRegister()`   | 写单个寄存器             |
| 0x10   | `WriteMultipleRegisters()`| 写多个寄存器（批量下发） |

#### CiA 对象字典 → Modbus 地址映射

```text
Modbus地址 = ((index & 0x00FF) << 8) | (subIndex - 1)
```

示例：
- 控制字 0x6040, sub0x00 → `(0x40 << 8) | (0x00) = 0x4000`
- 状态字 0x6041, sub0x00 → `(0x41 << 8) | (0x00) = 0x4100`

**H 寄存器地址映射（汇川 IS620N/SV660/SV680）：**

| H 寄存器 | 通信地址  | 说明         |
|----------|-----------|--------------|
| H03.50   | 2003-33h  | 控制字（写） |
| H0B.30   | 200B-1Fh  | 状态字（读） |
| H02.00   | 2002-01h  | 操作模式     |
| H05.36   | 2005-25h  | 归零偏移     |

#### TryReadSDO 内部执行流程

```text
TryReadSDO<ushort>(slaveAddr=1, index=0x6041, subIndex=0x00)
    │
    ├─1. 计算 Modbus 地址: addr = (0x41 << 8) | 0x00 = 0x4100
    ├─2. ReadHoldingRegisters(slave=1, start=0x4100, count=1)
    │       → 发送帧: [01][03][41 00][00 01][CRC]
    │       → 等待响应: [01][03][02][HH LL][CRC]
    ├─3. 大端序解析: value = (data[0]<<8) | data[1]
    └─4. 泛型类型转换后返回 (true, value)
```

#### 静默接收模式（调试专用）

```text
StartSilentReceive()
    → 开启后台线程读取串口原始数据
    → 以 ASCII 行为边界解析调试帧
    → 数据写入 SilentReceiveBuffer（供 AppLogPage 显示）
```

---

### 3.3 CiA402 从站包装层

#### CanOpenSlave_CiA402

**文件：** `Core/CANopen/CanOpenSlave_CiA402.cs`

封装 `CanOpenMaster`，提供 CiA402 语义接口：

```csharp
// 标准 CiA 对象索引
const ushort ObjControlWord      = 0x6040;
const ushort ObjStatusWord       = 0x6041;
const ushort ObjModesOfOperation = 0x6060;
const ushort ObjModesOfOpDisplay = 0x6061;

bool TryReadStatusWord(out ushort sw)
    → _master.TryReadSDO<ushort>(SlaveAddr, 0x6041, 0x00, out sw)

bool TryWriteControlWord(ushort cw)
    → _master.TryWriteSDO<ushort>(SlaveAddr, 0x6040, 0x00, cw)

bool TryReadByHIndex(string hIndex, out ushort value)
    → HVariables.FindByHIndex(hIndex) → 获取 SdoIndex/SdoSubIndex
    → _master.TryReadSDO<ushort>(SlaveAddr, sdoIndex, sdoSub, out value)

bool TryWriteByHIndex(string hIndex, ushort value)
    → HVariables.FindByHIndex(hIndex) → 获取 SdoIndex/SdoSubIndex
    → _master.TryWriteSDO<ushort>(SlaveAddr, sdoIndex, sdoSub, value)
```

#### ModbusSlave_CiA402

**文件：** `Core/Modbus/ModbusSlave_CiA402.cs`

封装 `ModbusRtuMaster`，同样提供 CiA402 语义，但地址来自可配置的 H 寄存器元组：

```csharp
(string HIndex, string CommAddress) ControlWordAddress   = ("H03.50", "2003-33h");
(string HIndex, string CommAddress) StatusWordAddress    = ("H0B.30", "200B-1Fh");
(string HIndex, string CommAddress) OperationModeAddress = ("H02.00", "2002-01h");
(string HIndex, string CommAddress) HomeOffsetAddress    = ("H05.36", "2005-25h");
```

---

## 4. H寄存器对象字典 — HVariables

**文件：** `Models/HVariables.cs`  
**规模：** 400+ 条目，覆盖汇川 SV680 / IS620N / SV660 系列驱动器全部参数

### 数据结构

```csharp
public class HRegisterEntry
{
    public string HIndex;           // "H00.00"
    public string CommAddress;      // "2000-01h"
    public ushort SdoIndex;         // 0x2000
    public byte   SdoSubIndex;      // 0x01
    public string ParameterName;    // "额定功率"
    public string Unit;             // "kW"
    public double Min, Max, Default;
    public bool   IsReadOnly;
    public string GroupName;        // "H00 - 电机参数"
}
```

### 分组结构

| 组     | 内容                                      | 关键参数                                   |
|--------|-------------------------------------------|--------------------------------------------|
| **H00** | 电机参数                                 | 额定功率/转速/转矩/电流/编码器类型/PPR     |
| **H01** | 驱动器参数                               | 软件版本号、驱动器编号、电流限值、温度阈值 |
| **H02** | 基础控制                                 | 控制模式、旋转方向、停机方式、用户密码     |
| **H03** | 速度环参数                               | Kp/Ki、增益切换、零速钳位、控制字          |
| **H04** | IO参数                                   | DI/DO 功能选择、极性、滤波时间             |
| **H05** | 位置控制                                 | 电子齿轮、归零配置、S曲线滤波、软限位     |
| **H06** | 速度控制                                 | 速度给定源、T曲线加减速、零速钳位         |
| **H07** | 转矩控制                                 | 转矩给定源、电流限值、前馈                 |
| **H08** | 控制环增益                               | 速度/位置 Kp/Ki/Kd、增益切换、前馈         |
| **H0B** | 监控参数（只读）                         | 实际转速/转矩/位置/温度/母线电压/故障码   |
| **H0C** | 通信参数                                 | 从站地址、波特率                           |
| **H0D** | 辅助功能                                 | 软件复位、故障复位、参数保存、点动、归零触发、紧停 |
| **H0E** | 模拟量IO                                 | AI1/AI2/AO1/AO2 功能、缩放、偏移、滤波   |
| **H0F** | 安全与软限位                             | STO使能、软位置限位、速度限制             |
| **H10** | 多段速度/位置                            | 8段速度段、4段位置段（含加减速时间）      |
| **H11** | 故障诊断                                 | 上电次数、运行时长、故障历史、故障上下文  |

### 查找方法

```csharp
// 通过 H 索引查找（如 "H0B.30"）
HRegisterEntry? entry = HVariables.FindByHIndex("H0B.30");

// 通过通信地址查找（如 "200B-1Fh"）
HRegisterEntry? entry = HVariables.FindByCommAddress("200B-1Fh");

// 典型使用示例：
if (HVariables.FindByHIndex("H0B.30") is { } entry)
{
    master.TryReadSDO<ushort>(axis.SlaveAddr, entry.SdoIndex, entry.SdoSubIndex, out ushort sw);
}
```

---

## 5. ViewModel 层与伺服控制

### 5.1 ControlViewModel — 状态机控制

**文件：** `ViewModels/ControlViewModel.cs`

这是整个软件中最核心的伺服控制入口，直接操作 CiA402 状态机喵～

#### CiA402 状态机

```text
上电
  │
  ▼
Not Ready to Switch On  (SW bits 6,5,4,3,2,1,0 = 0b0000000)
  │ 自动（驱动器内部）
  ▼
Switch On Disabled       (SW bit 6=1)
  │ CW: ShutDown (0x0006)
  ▼
Ready to Switch On       (SW bits 6,5,0 = 1,0,1)
  │ CW: SwitchOn (0x0007)
  ▼
Switched On              (SW bits 6,5,1,0 = 1,0,1,1)
  │ CW: EnableOperation (0x000F)
  ▼
Operation Enabled        (SW bits 6,5,2,1,0 = 1,0,1,1,1)  ← 正常运行
  │
  ├─ CW: QuickStop (Bit2=0) → Quick Stop Active
  ├─ CW: DisableOperation  → Switched On
  ├─ CW: DisableVoltage    → Switch On Disabled
  └─ 故障发生             → Fault
         │ CW: FaultReset (Bit7=1→0)
         └─────────────────────────────┘
```

#### 定时刷新循环（200ms）

```text
_refreshTimer.Tick (每 200ms)
    │
    ├─1. IsBusy 检查：串行操作中跳过
    ├─2. master.TryReadSDO<ushort>(axis.SlaveAddr, 0x6041, 0, out ushort sw)
    │        → 更新 StatusWordRaw, StatusWordBits[16]
    │        → 解析 CiA402 状态名称 (StateText)
    ├─3. 若用户未在编辑控制字：
    │       master.TryReadSDO<ushort>(axis.SlaveAddr, 0x6040, 0, out ushort cw)
    │           → 更新 ControlWordRaw, ControlWordBits[16]
    └─4. 失败计数 → 触发 CommLost 逻辑（见第7节）
```

#### 状态机命令执行

| 命令方法               | 写入 CW 值 | 说明                         |
|------------------------|-----------|------------------------------|
| `OnCmdShutdown()`      | `0x0006`  | 关断使能，回到 Ready         |
| `OnCmdSwitchOn()`      | `0x0007`  | 上电开关接通                 |
| `OnCmdEnableOperation()`| `0x000F` | 伺服使能（进入运行状态）     |
| `OnCmdDisableVoltage()`| `0x0000`  | 禁止电压，回到 Disabled      |
| `OnCmdDisableOperation()`| `0x0007`| 禁止运行，保持 SwitchedOn   |
| `OnCmdQuickStop()`     | CW & ~0x04| 清位2，触发快速停车          |
| `OnCmdFaultReset()`    | CW \| 0x80, 延迟, CW & ~0x80 | 故障复位（上升沿触发） |

#### 一键使能 (`OnCmdQuickEnable`)

```csharp
OnCmdQuickEnable()
    │
    ├─1. 读取状态字 → 判断当前 CiA402 状态
    ├─2. 若在 Fault → 执行 FaultReset 序列（Bit7: 0→1→0，间隔50ms）
    ├─3. 读取状态字 → 等待退出 Fault（最多3次重试）
    ├─4. 写 ShutDown (0x0006) → 等待 ReadyToSwitchOn（等待50ms）
    ├─5. 写 SwitchOn (0x0007) → 等待 SwitchedOn
    └─6. 写 EnableOp (0x000F) → 等待 OperationEnabled
```

#### CommLost 紧急处理

```csharp
private void OnCommLost(object? sender, CommLostEventArgs e)
{
    // 通信丢失时执行软件紧急停车：清除 Bit2 (QuickStop)
    ushort qsWord = (ushort)(ControlWordRaw & ~(1 << 2));
    _master?.TryWriteSDO<ushort>(_axis.SlaveAddr, 0x6040, 0x00, qsWord);
}
```

---

### 5.2 DashboardViewModel — 监控数据读取

**文件：** `ViewModels/DashboardViewModel.cs`

#### 监控参数刷新（500ms 间隔）

```text
_monitorTimer.Tick (每 500ms)
    │
    ├─ ReadHReg("H0B.00") → ActualSpeed      (r/min)
    ├─ ReadHReg("H0B.02") → ActualTorque     (%)
    ├─ ReadHReg("H0B.04") → ActualPosition   (pulse)
    ├─ ReadHReg("H0B.06") → MotorTemperature (°C)
    ├─ ReadHReg("H0B.08") → BusVoltage       (V)
    ├─ ReadHReg("H0B.10") → PhaseCurrent     (A)
    ├─ ReadHReg("H0B.12") → MosTemperature   (°C)
    └─ ReadHReg("H0B.30") → FaultCode
            → FaultCodeTable.GetName(faultCode)  → FaultName
            → FaultCodeTable.GetDetail(faultCode) → FaultDetail
            → HasFault = (faultCode != 0)
```

#### HRegisterIO 辅助工具

**文件：** `Core/HRegisterIO.cs`

```csharp
// 读取（含 null 保护、H 索引查找、类型转换）
public static bool ReadHReg(IServoMaster? master, IServoAxis? axis,
    string hIndex, Action<ushort> onSuccess)
{
    if (master is null || axis is null) return false;
    if (HVariables.FindByHIndex(hIndex) is not { } entry) return false;
    if (!master.TryReadSDO<ushort>(axis.SlaveAddr, entry.SdoIndex, entry.SdoSubIndex, out ushort v))
        return false;
    onSuccess(v);
    return true;
}

// 写入（带错误收集列表）
public static void SafeWriteHReg(IServoMaster? master, IServoAxis? axis,
    string hIndex, ushort value, List<string> errors, string friendlyName)
{
    if (!ReadHReg(master, axis, hIndex, _ => { })) return; // 先确认可达
    if (HVariables.FindByHIndex(hIndex) is not { } entry)
    {
        errors.Add($"{friendlyName}: H索引不存在 ({hIndex})");
        return;
    }
    if (!master!.TryWriteSDO<ushort>(axis!.SlaveAddr, entry.SdoIndex, entry.SdoSubIndex, value))
        errors.Add($"{friendlyName}: 写入失败");
}
```

---

### 5.3 QuickControlViewModel — 模式参数快调与波形采样

**文件：** `ViewModels/QuickControlViewModel.cs`（1186 行）

#### 模式参数映射（CiA402 对象索引）

| 运动模式 (modes_of_operation) | 对象索引 | 参数名 |
|-------------------------------|----------|--------|
| ProfilePosition (PP, 1)       | 0x607A   | TargetPosition |
| ProfilePosition               | 0x6081   | ProfileVelocity |
| ProfilePosition               | 0x6083   | ProfileAcceleration |
| ProfilePosition               | 0x6084   | ProfileDeceleration |
| ProfilePosition               | 0x6085   | QuickStopDeceleration |
| Velocity (VL, 2)              | 0x6042   | TargetVelocity |
| ProfileVelocity (PV, 3)       | 0x60FF   | TargetVelocity |
| ProfileVelocity               | 0x6083   | ProfileAcceleration |
| ProfileVelocity               | 0x6084   | ProfileDeceleration |
| ProfileTorque (PT, 4)         | 0x6071   | TargetTorque |
| ProfileTorque                 | 0x6072   | MaxTorque |
| ProfileTorque                 | 0x6087   | TorqueSlope |
| Homing (HM, 6)                | 0x6098   | HomingMethod |
| Homing                        | 0x6099:1 | HomingSpeedSearch |
| Homing                        | 0x6099:2 | HomingSpeedZero |
| Homing                        | 0x609A   | HomingAcceleration |
| InterpolatedPosition (IP, 7)  | 0x60C2:1 | InterpolationTimePeriod |
| CyclicSynchronousPosition (CSP, 8) | 0x607A | TargetPosition |
| CyclicSynchronousPosition     | 0x60FF   | TargetVelocity |
| CyclicSynchronousPosition     | 0x60B0   | PositionOffset |
| CyclicSynchronousPosition     | 0x60B1   | VelocityOffset |
| CyclicSynchronousPosition     | 0x60B2   | TorqueOffset |
| CyclicSynchronousVelocity (CSV, 9) | 0x60FF | TargetVelocity |
| CyclicSynchronousTorque (CST, 10) | 0x6071 | TargetTorque |

#### 自动读取循环（1s 间隔）

```text
_autoReadTimer.Tick (每 1000ms)
    │
    ├─1. 读取当前操作模式: TryReadSDO(0x6061, 0) → _currentMode
    ├─2. 根据模式过滤参数列表
    └─3. 逐一读取参数（跳过用户正在编辑的参数）:
             foreach (param in _activeParams)
                 if (!param.IsBeingEdited)
                     master.TryReadSDO(axis, param.Index, param.SubIndex, out val)
                     → param.ValueDisplay = FormatValue(val)
```

#### 实时波形采样

每个采样通道（`LiveChannel`）独立追踪一个 SDO 对象：

```csharp
public class LiveChannel
{
    public ushort SdoIndex;    // 被采样的 CiA 对象索引
    public byte   SdoSubIndex;
    public string Label;       // 通道名称（显示用）
    public double ScaleFactor; // 单位缩放
    public double[] Buffer;    // 环形缓冲（10k ~ 100k 采样点）
    public int     Head;       // 写指针
    public int     Count;      // 有效样本数
    private object _gate = new(); // 线程锁

    public void Sample(IServoMaster master, int slaveAddr)
    {
        double val;
        if (!master.TryReadSDO<short>(slaveAddr, SdoIndex, SdoSubIndex, out short raw))
            return;
        val = raw * ScaleFactor;

        lock (_gate)
        {
            Buffer[Head] = val;
            Head = (Head + 1) % Buffer.Length;
            if (Count < Buffer.Length) Count++;
        }
    }
}
```

波形采样定时器间隔由用户配置（最小 10ms），通过后台线程并行采集所有通道：

```text
_sampleTimer.Tick
    │
    └─ Task.Run(() =>
            foreach (ch in ActiveChannels)
                ch.Sample(_master, _axis.SlaveAddr))
    → 采样完毕通知 UI 刷新 OxyPlot 图表
```

---

## 6. EtherCAT CoE SDO 与 PDO 详解

**文件：** `Core/EtherCAT/EtherCatCyclicSyncService.cs`，`Models/Cia402PdoTemplates.cs`

### 6.1 两条通道的根本区别

EtherCAT 的 CoE（CAN application protocol over EtherCAT）把 CANopen 的两类服务映射到两条完全独立的通道上：

| 维度 | CoE SDO（邮箱通道） | PDO（过程数据通道） |
|------|--------------------|--------------------|
| 物理路径 | Mailbox SM0/SM1（非周期，带确认） | SM2(RxPDO) / SM3(TxPDO)（周期，无确认） |
| 触发方式 | 主站主动读写，从站回复 | DC SYNC0 信号驱动，每拍原子交换 |
| 延迟特性 | 毫秒级（邮箱轮询） | 微秒级（实时周期） |
| 典型用途 | 参数配置、状态查询、PDO 映射配置 | ControlWord/TargetPos/StatusWord 等实时数据 |
| C# 入口 | `_master.TryReadSDO<T>()` / `TryWriteSDO<T>()` | `_master.TryInOutSync()` 内部自动完成 |

### 6.2 PDO 映射配置（CoE SDO 写入阶段）

**文件：** `EtherCatCyclicSyncService.ConfigurePdoCore()` — Lines 132-174

EtherCAT 的 PDO 映射配置和 CANopen 基本相同，但多了一个 **SyncManager 分配步骤**（0x1C12/0x1C13）：

```text
ConfigureRxPdo(slaveAddr, mapEntries[], pdoIndex=0x1600)
    → ConfigurePdoCore(slaveAddr, smIndex=0x1C12, pdoIndex, mapEntries)
        │
        ├─1. TryWriteSDO(0x1C12, sub0, 0)           ← 清空 SM2 分配（禁用 RxPDO）
        ├─2. TryWriteSDO(0x1600, sub0, 0)            ← 清空映射条目数
        ├─3. TryWriteSDO(0x1600, sub1..N, mapEntries[i]) ← 写映射条目
        ├─4. TryWriteSDO(0x1600, sub0, N)            ← 恢复条目数
        ├─5. TryWriteSDO(0x1C12, sub1, 0x1600)       ← 将 PDO 对象分配给 SM2
        └─6. TryWriteSDO(0x1C12, sub0, 1)            ← 启用 SM2 分配（1个PDO）

ConfigureTxPdo(slaveAddr, mapEntries[], pdoIndex=0x1A00)
    → ConfigurePdoCore(slaveAddr, smIndex=0x1C13, pdoIndex, mapEntries)
        （步骤同上，SM3 分配给 TxPDO）
```

和 CANopen 对比：EtherCAT 没有 COB-ID 和传输类型的概念，用 SM 分配号（0x1C12/0x1C13）替代了 CANopen 的 0x1400/0x1800 通信参数对象。

### 6.3 同步类型配置

```text
TryConfigureSyncType(slaveAddr, syncType=2)
    │
    ├─ TryWriteSDO(0x1C32, sub1, syncType)  ← RxPDO 同步类型
    └─ TryWriteSDO(0x1C33, sub1, syncType)  ← TxPDO 同步类型
```

| syncType 值 | 含义 |
|-------------|------|
| 0 | FreeRun（从站自由运行，不受主站 SYNC 控制） |
| 1 | SM-Synchron（由 SM2 写入触发同步） |
| 2 | DC-Sync0（由 DC SYNC0 信号触发，推荐，实时性最好） |
| 3 | DC-Sync1（由 DC SYNC1 信号触发） |

### 6.4 周期同步服务结构

```text
EtherCatCyclicSyncService
├── _master: EtherCATMaster         ← Leal 封装的 SOEM，持有所有从站 PDO 缓冲
├── _cyclicTimer                    ← PeriodicTimer / HighResolutionTimer / ThreadPoolTimer
├── BeforeSync: event Action        ← 每拍前触发，外部向 PDO 输出缓冲写数据
├── AfterSync:  event Action        ← 每拍后触发，外部从 PDO 输入缓冲读数据
├── TickCount:  long (Interlocked)  ← 累计成功 InOutSync 次数
├── ErrorCount: long (Interlocked)  ← 累计失败次数
└── LastJitterMicros: double        ← 上拍周期抖动 (μs)
```

### 6.5 每拍执行链（周期运行阶段）

```text
定时器 Tick（每 period，例如 1ms）
    │
    ├─1. BeforeSync?.Invoke()
    │       ← 外部代码（如 MotionTypeViewModel）把 TargetPosition 等写入
    │         _master 内部的 PDO 输出缓冲（通过 _master.SetRxPdoValue<T>() 等方法）
    │
    ├─2. bool ok = _master.TryInOutSync()
    │       ← SOEM 核心操作：
    │         a. 把所有从站的 RxPDO 输出缓冲打包为一帧 EtherCAT Process Data
    │         b. 通过 NIC 发出，沿环网逐站写入各从站 SM2
    │         c. 同帧回程时各从站将 SM3 数据附在帧内返回主站
    │         d. SOEM 解包，填充各从站的 TxPDO 输入缓冲
    │         整个过程在 DC SYNC0 时间窗口内原子完成
    │
    ├─3. Interlocked.Increment(ok ? _tickCount : _errorCount)
    │
    └─4. AfterSync?.Invoke()
            ← 外部代码读取 _master 内部 TxPDO 输入缓冲中的 StatusWord、ActualPosition 等
```

### 6.6 PDO 模板（`Cia402PdoTemplates.cs`）

模板定义了 RPDO/TPDO 的映射条目，映射条目格式同 CANopen：`(index<<16)|(sub<<8)|bits`

**CSP 模板（Cyclic Synchronous Position）**

| PDO | 映射对象 | 对象索引 | 位宽 |
|-----|---------|---------|------|
| RPDO1 | ControlWord | 0x6040:00 | 16 |
| RPDO1 | ModesOfOperation | 0x6060:00 | 8 |
| RPDO2 | TargetPosition | 0x607A:00 | 32 |
| RPDO3 | VelocityOffset | 0x60B1:00 | 32 |
| RPDO3 | TorqueOffset | 0x60B2:00 | 16 |
| TPDO1 | StatusWord | 0x6041:00 | 16 |
| TPDO1 | ModesOfOperationDisplay | 0x6061:00 | 8 |
| TPDO2 | PositionActualValue | 0x6064:00 | 32 |
| TPDO3 | VelocityActualValue | 0x606C:00 | 32 |
| TPDO3 | TorqueActualValue | 0x6077:00 | 16 |

**CSV 模板（Cyclic Synchronous Velocity）**

| PDO | 映射对象 | 对象索引 | 位宽 |
|-----|---------|---------|------|
| RPDO1 | ControlWord | 0x6040:00 | 16 |
| RPDO1 | ModesOfOperation | 0x6060:00 | 8 |
| RPDO2 | TargetVelocity | 0x60FF:00 | 32 |
| RPDO2 | TorqueOffset | 0x60B2:00 | 16 |
| TPDO1 | StatusWord | 0x6041:00 | 16 |
| TPDO1 | ModesOfOperationDisplay | 0x6061:00 | 8 |
| TPDO2 | VelocityActualValue | 0x606C:00 | 32 |
| TPDO2 | PositionActualValue | 0x6064:00 | 32 |

**CST 模板（Cyclic Synchronous Torque）**

| PDO | 映射对象 | 对象索引 | 位宽 |
|-----|---------|---------|------|
| RPDO1 | ControlWord | 0x6040:00 | 16 |
| RPDO1 | ModesOfOperation | 0x6060:00 | 8 |
| RPDO2 | TargetTorque | 0x6071:00 | 16 |
| RPDO2 | TorqueOffset | 0x60B2:00 | 16 |
| TPDO1 | StatusWord | 0x6041:00 | 16 |
| TPDO1 | ModesOfOperationDisplay | 0x6061:00 | 8 |
| TPDO2 | TorqueActualValue | 0x6077:00 | 16 |
| TPDO2 | VelocityActualValue | 0x606C:00 | 32 |
| TPDO3 | PositionActualValue | 0x6064:00 | 32 |

### 6.7 CANopen PDO vs EtherCAT PDO 完整对比

| 维度 | CANopen | EtherCAT (CoE) |
|------|---------|----------------|
| 发送触发 | `SendSync(0x080)` → 从站响应 RPDO | DC SYNC0 硬件信号 → `TryInOutSync()` 原子交换 |
| 接收方式 | `DispatchLoop` 收 TPDO 帧 → `PdoReceived` 事件 | `TryInOutSync()` 后读取缓冲 |
| PDO 数量 | 最多 4 RPDO + 4 TPDO | 1 RxPDO（SM2）+ 1 TxPDO（SM3） |
| 每帧数据量 | 每 PDO 最多 8 字节 | 全部从站合并为一帧，无 8 字节限制 |
| 配置对象 | 0x1400/0x1600（RPDO）+ 0x1800/0x1A00（TPDO） | 同上 + 0x1C12（SM2分配）+ 0x1C13（SM3分配） |
| SYNC 来源 | 主站软件 `StartSyncProducer()` 生成 0x080 帧 | ESC 硬件 DC 时钟，`_master` 内部处理 |
| 抖动来源 | 软件定时器 + CAN 总线仲裁延迟 | 硬件 DC 分布时钟，纳秒级同步 |
| C# 代码路径 | `SyncTick += () => SendPdo(...)` | `BeforeSync += () => SetRxPdo(...)`，`AfterSync += () => GetTxPdo(...)` |

---

## 7. 通信丢失看门狗

**定义位置：** `ViewModels/DeviceSet/DeviceAddViewModel.cs`

### 事件定义

```csharp
public sealed class CommLostEventArgs(string protocol, int consecutiveFailures) : EventArgs
{
    public string Protocol             { get; } // "Modbus" / "CANopen" / "EtherCAT"
    public int    ConsecutiveFailures  { get; }
}

public static event EventHandler<CommLostEventArgs>? CommLost;
public static int CommLostThreshold { get; set; } = 3; // 默认3次连续失败
```

### 各协议的失败检测点

| 协议      | 检测位置                         | 触发条件                       |
|-----------|----------------------------------|-------------------------------|
| Modbus    | `ProbeModbusSlaveAsync()` 定时轮询 | `++_modbusCommLostCount >= CommLostThreshold` |
| CANopen   | `ProbeCanOpenSlaveAsync()` 定时轮询 | `++_canOpenCommLostCount >= CommLostThreshold` |
| EtherCAT  | `SlaveStateTimer_Tick`           | 从站状态不为 Operational × N次 |

### 事件订阅者

| 订阅者                   | 处理动作                                  |
|--------------------------|-------------------------------------------|
| `ControlViewModel`       | 软件紧急停车（清除 CW Bit2）              |
| `DashboardViewModel`     | 停止监控定时器                            |
| `QuickControlViewModel`  | 停止实时波形采样，更新连接信息提示        |
| `PidAdjustViewModel`     | 停止刷新定时器，更新连接状态文字          |

### 阈值配置

- 在 `Views/Pages/SettingsPage.xaml` → "通信看门狗" 区域
- 绑定至 `SettingsViewModel.CommLostThreshold`（范围 1~30）
- `partial void OnCommLostThresholdChanged(int v)` → 同步写入 `DeviceAddViewModel.CommLostThreshold`

---

## 8. 完整伺服控制执行流程

以下以"连接 Modbus 从站 → 使能伺服 → 速度模式运行 → 断开"为完整示例喵：

```text
┌──────────────────────────────────────────────────────────────┐
│ Step 1: 设备连接（ListPage / DeviceAddViewModel）              │
│                                                              │
│  用户选择端口 + 波特率 → "连接" 按钮                         	  │
│      → new ModbusRtuMaster() → Open(portName, baudRate)      │
│      → new ModbusSlave_CiA402(master, slaveAddr)             │
│      → 写入全局静态: DeviceAddViewModel.CurrentMaster = master │
│      → 写入全局静态: DeviceAddViewModel.CurrentAxis  = axis    │
│      → 触发 ProbeModbusSlaveAsync 周期性探针                    │
└──────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────┐
│ Step 2: 导航至控制页（ControlPage / ControlViewModel）       	  │
│                                                              │
│  OnNavigatedTo():                                            │
│      → _master = DeviceAddViewModel.CurrentMaster            │
│      → _axis   = DeviceAddViewModel.CurrentAxis              │
│      → DeviceAddViewModel.CommLost += OnCommLost             │
│      → 启动 _refreshTimer (200ms)                             │
└──────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────┐
│ Step 3: 一键使能（OnCmdQuickEnable）                           │
│                                                              │
│  1. TryReadSDO(0x6041) → 检查是否在 Fault 状态                 │
│  2. [如有故障] TryWriteSDO(0x6040, cw|0x80) → 延迟50ms         │
│               TryWriteSDO(0x6040, cw&~0x80) ← FaultReset     │
│  3. TryWriteSDO(0x6040, 0x0006) ← Shutdown                   │
│     等待 TryReadSDO(0x6041) & 0x006F == 0x0021 (ReadyToSwitchOn)│
│  4. TryWriteSDO(0x6040, 0x0007) ← SwitchOn                   │
│     等待 StatusWord & 0x006F == 0x0023 (SwitchedOn)           │
│  5. TryWriteSDO(0x6040, 0x000F) ← EnableOperation            │
│     等待 StatusWord & 0x006F == 0x0027 (OperationEnabled)     │
└──────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────┐
│ Step 4: 切换到速度模式并设定速度（QuickControlPage）         	  │
│                                                              │
│  1. TryWriteSDO(0x6060, 0x00, 3) ← ModesOfOperation = PV     │
│  2. 等待 TryReadSDO(0x6061) == 3 (ProfileVelocity 已激活)   	│
│  3. TryWriteSDO(0x60FF, 0x00, targetRpm) ← TargetVelocity    │
│  4. TryWriteSDO(0x6083, 0x00, accel)                         │
│  5. TryWriteSDO(0x6084, 0x00, decel)                         │
│  6. 写入 ControlWord 0x000F → 确保 OperationEnabled         	│
└──────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────┐
│ Step 5: 实时监控（DashboardPage + 波形采样）                  	│
│                                                              │
│  DashboardViewModel (500ms):                                 │
│      ReadHReg("H0B.00") → 显示实际转速                         │
│      ReadHReg("H0B.02") → 显示实际转矩                         │
│      ReadHReg("H0B.30") → 故障码 → FaultCodeTable 查名称       │
│                                                              │
│  LiveChannel 采样 (10~100ms):                                 │
│      TryReadSDO(0x606C, 0) → ActualVelocity → Buffer[Head]   │
│      TryReadSDO(0x6064, 0) → ActualPosition → Buffer[Head]   │
│      → OxyPlot 更新波形图                                    	│
└──────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────┐
│ Step 6: 通信异常处理（看门狗）                               	│
│                                                              │
│  ProbeModbusSlaveAsync (200ms) 连续3次失败:                    │
│      DeviceAddViewModel.CommLost?.Invoke(..., CommLostEventArgs)│
│          │                                                   │
│          ├─ ControlViewModel.OnCommLost():                   │
│          │       TryWriteSDO(0x6040, cw & ~0x04)  ← 紧停      │
│          ├─ DashboardViewModel.OnCommLost():                 │
│          │       _monitorTimer.Stop()                        │
│          └─ QuickControlViewModel.OnCommLost():              │
│                  _sampleTimer.Stop()                         │
│                  ConnectionInfo = "通信已断开"                 │
└──────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────┐
│ Step 7: 断开连接                                              │
│                                                              │
│  用户点击"断开"按钮:                                            │
│      → ControlViewModel.OnNavigatedFrom():                   │
│              DeviceAddViewModel.CommLost -= OnCommLost       │
│              _refreshTimer.Stop()                            │
│      → DeviceAddViewModel.Disconnect():                      │
│              _master.Close()                                 │
│              CurrentMaster = null                            │
│              CurrentAxis   = null                            │
└──────────────────────────────────────────────────────────────┘
```

---

## 9. 关键文件位置速查

| 文件路径（相对 `samples/Wpf.Ui.servoStudio/`） | 作用 |
|------------------------------------------------|------|
| `Core/IServoMaster.cs`                         | 协议无关接口定义 |
| `Core/HRegisterIO.cs`                          | H 寄存器读写辅助工具 |
| `Core/CANopen/CanOpenMaster.cs`                | CANopen SDO/PDO/NMT 主站 (758行) |
| `Core/CANopen/CanOpenSlave_CiA402.cs`          | CANopen CiA402 从站封装 (177行) |
| `Core/CANopen/CanFrame.cs`                     | CAN 帧结构与帧类型枚举 |
| `Core/CANopen/SerialCanBus.cs`                 | USB2XXX / ZLG 物理层适配 |
| `Core/CANopen/VirtualCanBus.cs`                | 虚拟 CAN 总线（测试用） |
| `Core/Modbus/ModbusRtuMaster.cs`               | Modbus RTU 主站 (1054行) |
| `Core/Modbus/ModbusSlave_CiA402.cs`            | Modbus CiA402 从站封装 (183行) |
| `Core/EtherCAT/EtherCatCyclicSyncService.cs`  | EtherCAT 周期同步服务 (212行) |
| `Models/HVariables.cs`                         | H 寄存器对象字典 (400+ 条目) |
| `Models/FaultCodeTable.cs`                     | 汇川 SV680 故障码查找表 (40+ 条目) |
| `ViewModels/ControlViewModel.cs`               | CiA402 状态机控制 (485行) |
| `ViewModels/DashboardViewModel.cs`             | 实时监控参数 (155行) |
| `ViewModels/QuickControlViewModel.cs`          | 模式参数快调 + 波形采样 (1186行) |
| `ViewModels/DeviceSet/DeviceAddViewModel.cs`   | 设备探针 + CommLost 看门狗 |
| `Services/PageUsageTracker.cs`                 | 页面访问频率 + 快速访问 |
| `Services/ApplicationHostService.cs`           | 启动流程 + 页面预加载 |

---

> 文档生成于 2026-05-22，基于当前代码库状态整理 ✨
> 如代码发生重大更改，请同步更新本文档喵～
