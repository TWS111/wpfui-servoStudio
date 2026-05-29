# EtherCAT 工作原理对话记录

> 本文件由 GitHub Copilot 根据本次会话自动导出，包含三轮 Q&A 的完整原文（中文）。

---

## 第一轮 — 用户提问

> 向我详细解释在程序中的整个EtherCAT的工作原理、执行步骤、数据的具体流向和内容，以及每一步都是在哪个代码位置执行的

## 第一轮 — 助理回答

下面把这个工程里 EtherCAT 的"全链路"按 **执行顺序** 拆开，并指出每一步对应的代码位置。

> 关键背景：本项目自身**不实现 EtherCAT 协议栈**。协议栈来自 NuGet 包 `Leal.Core.Net.EtherCAT`（命名空间 `Core.Net.EtherCAT`，底层封装 `lealsoem.dll` —— SOEM 主站）。仓库里只有两块"自己写"的 EtherCAT 代码：
> 1. 业务编排层（ViewModel 调用 `EtherCATMaster` 的 API）；
> 2. **独立的 FoE/EEPROM 通道**（自编 [native/soemfoe_wrapper.c](samples/Wpf.Ui.servoStudio/native/soemfoe_wrapper.c)，C# 侧 [Helpers/SoemFoEInterop.cs](samples/Wpf.Ui.servoStudio/Helpers/SoemFoEInterop.cs)），固件升级时使用，与 Leal 主站时序互斥。

---

## 一、核心对象 & 数据模型

| 角色 | 类型 | 出处 |
|---|---|---|
| EtherCAT 主站（持有 SOEM 上下文） | `EtherCATMaster` | NuGet `Core.Net.EtherCAT` |
| CiA402 从机（伺服轴） | `EtherCATSlave_CiA402` | NuGet `Core.Net.EtherCAT` |
| 协议无关抽象 | `IServoMaster` / `IServoAxis` | [Core/IServoMaster.cs](samples/Wpf.Ui.servoStudio/Core/IServoMaster.cs) |
| ECAT 适配到统一抽象 | `EtherCATServoMasterAdapter` / `EtherCATServoAxisAdapter` | [Core/IServoMaster.cs#L45](samples/Wpf.Ui.servoStudio/Core/IServoMaster.cs#L45) |
| EtherCAT 全局编排 | `DeviceAddViewModel` | [ViewModels/DeviceSet/DeviceAddViewModel.cs](samples/Wpf.Ui.servoStudio/ViewModels/DeviceSet/DeviceAddViewModel.cs) |
| 独立 FoE 上下文（旁路） | `SoemFoEInterop` + `soemfoe.dll` | [Helpers/SoemFoEInterop.cs](samples/Wpf.Ui.servoStudio/Helpers/SoemFoEInterop.cs) / [native/soemfoe_wrapper.c](samples/Wpf.Ui.servoStudio/native/soemfoe_wrapper.c) |

`DeviceAddViewModel` 在构造时即 new 出主站对象（一次创建，整个进程复用）：

[ViewModels/DeviceSet/DeviceAddViewModel.cs#L38](samples/Wpf.Ui.servoStudio/ViewModels/DeviceSet/DeviceAddViewModel.cs#L38)
```csharp
private readonly EtherCATMaster ecatMaster = new EtherCATMaster();
private EtherCATSlave_CiA402? _axis;
```

---

## 二、执行步骤（按时间顺序）

### 步骤 0 —— 用户在 UI 选网卡

`DeviceAddPage.xaml` 里的下拉框枚举本机网卡，绑定到 `EthernetDeviceNames`，选中后 `SelectedEthernetName` 就是 NPF 网卡名（如 `\Device\NPF_{GUID}`）。映射逻辑见 [DeviceAddViewModel.cs#L430](samples/Wpf.Ui.servoStudio/ViewModels/DeviceSet/DeviceAddViewModel.cs#L430) 的 `UpdateSelectedEthernetName`。

### 步骤 1 —— 启动主站 + 扫描总线（INIT → PRE-OP）

入口：`OnEthernetConfirm` 命令（点击"连接"按钮）。

[DeviceAddViewModel.cs#L444](samples/Wpf.Ui.servoStudio/ViewModels/DeviceSet/DeviceAddViewModel.cs#L444)
```csharp
[RelayCommand(CanExecute = nameof(CanEthernetConfirm))]
private async Task OnEthernetConfirm()
{
    ...
    _axis = new EtherCATSlave_CiA402(ecatMaster, 1);     // 绑定 1 号从站
    int c = ecatMaster.StartActivity(SelectedEthernetName); // ★关键：打开网卡 + 扫描
    if (c > 0)
    {
        name  = _axis.SlaveName;
        addr  = _axis.SlaveAddr;
        state = ecatMaster.ReadState(addr).ToString();   // 读 AL Status
    }
    ...
    StartSlaveStatePolling();   // 开 500ms DispatcherTimer 轮询从站状态
}
```

`ecatMaster.StartActivity(ifname)` 一步做了 SOEM 标准启动序列（在 NuGet 黑盒里）：
- `ecx_init(ifname)` —— 用 npcap 打开网卡，拿到原始 EtherCAT 帧通道；
- `ecx_config_init(false)` —— 广播 `BRD AL Status`、自动分配站点地址（FixedAddress = 1, 2, …）、读取每个从站 EEPROM 中的 SII（Identity / 邮箱 / SM / FMMU）；
- `ecx_config_map_group()` —— 解析 PDO 配置生成过程数据 IO 映射；
- `ecx_statecheck(0, EC_STATE_SAFE_OP, …)` —— 把整个网络推到 SAFE-OP；
- 维护一个内部循环线程做 `ecx_send_processdata` / `ecx_receive_processdata`。

返回值 `c` 是检测到的从站个数，>0 即"链路已起来"。

### 步骤 2 —— NMT/AL 状态机切换

UI 上的 4 个按钮分别绑定到这 4 个 RelayCommand，用法完全一样：写状态请求 → 立刻读回当前状态。

[DeviceAddViewModel.cs#L575-L668](samples/Wpf.Ui.servoStudio/ViewModels/DeviceSet/DeviceAddViewModel.cs#L575-L668)
```csharp
ecatMaster.WriteState(_axis.SlaveAddr, SlaveState.Init);          // WrCmd → AL Control
ecatMaster.WriteState(_axis.SlaveAddr, SlaveState.PreOperational);
ecatMaster.WriteState(_axis.SlaveAddr, SlaveState.SafeOperational);
ecatMaster.WriteState(_axis.SlaveAddr, SlaveState.Operational);
EthernetSlaveStateInfo = ecatMaster.ReadState(_axis.SlaveAddr).ToString(); // 读 AL Status
```

底层数据流：
- `WriteState` → SOEM 内部用 `FPWR(0x0120, state)` 写 AL Control 寄存器；
- `ReadState`  → `FPRD(0x0130)` 读 AL Status；
- 状态切换的成功 / 失败由从站本身回写 AL Status 与 AL Status Code 体现。

### 步骤 3 —— 周期性状态监控

`StartSlaveStatePolling()` ([DeviceAddViewModel.cs#L685](samples/Wpf.Ui.servoStudio/ViewModels/DeviceSet/DeviceAddViewModel.cs#L685)) 起一个 500 ms 的 `DispatcherTimer`：

[DeviceAddViewModel.cs#L706](samples/Wpf.Ui.servoStudio/ViewModels/DeviceSet/DeviceAddViewModel.cs#L706)
```csharp
private async void SlaveStateTimer_Tick(...)
{
    var state = await Task.Run(() => ecatMaster.ReadState(addr).ToString());
    EthernetSlaveStateInfo = state;
}
```

> 注意：真正的过程数据（PDO）循环交换在 `Leal.Core.Net.EtherCAT` 的内部线程里跑，UI 只通过 `ReadState` / `ReadSDO` / `WriteSDO` 抓快照。

### 步骤 4 —— SDO 读写（参数访问）

所有 ViewModel 都通过 `IServoMaster Master => deviceAddViewModel.ActiveServoMaster;`，对 EtherCAT 来说该属性返回的是 `EtherCATServoMasterAdapter`：

[Core/IServoMaster.cs#L52](samples/Wpf.Ui.servoStudio/Core/IServoMaster.cs#L52)
```csharp
public bool TryReadSDO<T>(int slaveAddr, ushort index, byte subIndex, out T value)
    => _master.TryReadSDO(slaveAddr, index, subIndex, out value);
```

数据流：上层 ViewModel → `IServoMaster.TryReadSDO/TryWriteSDO/ReadSDO<T>` → `EtherCATMaster`（NuGet）→ SOEM `ecx_SDOread/ecx_SDOwrite` → CoE 邮箱（Sm0/Sm1）→ 从机对象字典（如 `0x6041` 状态字、`0x6040` 控制字、`0x6060` 操作模式）。

典型使用点（搜 `IServoMaster Master =>` 可以全部找到）：
- [ViewModels/Motion/MotionTypeViewModel.cs#L352](samples/Wpf.Ui.servoStudio/ViewModels/Motion/MotionTypeViewModel.cs#L352)
- [ViewModels/Hardware/MotorViewModel.cs#L22](samples/Wpf.Ui.servoStudio/ViewModels/Hardware/MotorViewModel.cs#L22)
- [ViewModels/ControlViewModel.cs#L229](samples/Wpf.Ui.servoStudio/ViewModels/ControlViewModel.cs#L229)
- [ViewModels/FaultInfoViewModel.cs#L60](samples/Wpf.Ui.servoStudio/ViewModels/FaultInfoViewModel.cs#L60)

主站还维护一个 `SdoQueue.SdoModelDic` 缓存（断开时清空，见步骤 5）。

### 步骤 5 —— 断开

[DeviceAddViewModel.cs#L523](samples/Wpf.Ui.servoStudio/ViewModels/DeviceSet/DeviceAddViewModel.cs#L523) 的 `OnEthernetDisconnect`：
```csharp
StopSlaveStatePolling();                                 // 停 UI 轮询
ecatMaster.WriteState(_axis.SlaveAddr, SlaveState.Init); // 把从站打回 INIT
ecatMaster.StopActivity();                               // 关 npcap、停内部循环线程
ecatMaster.SdoQueue.SdoModelDic.Clear();                 // 清 SDO 缓存
```

---

## 三、FoE / EEPROM 旁路（固件烧录、SII 烧录）

固件升级要进 BOOT 状态，且需要重新配置 bootstrap 邮箱。Leal 库不直接暴露这些底层操作，所以工程**自己编了一份 SOEM** 出 `soemfoe.dll`，与 Leal 完全隔离的第二个 SOEM 上下文。整个流程是**时序互斥**：先停 Leal、用 soemfoe 干完、再起 Leal。

入口：[ViewModels/Firmware/FirmwareProgramViewModel.cs#L233](samples/Wpf.Ui.servoStudio/ViewModels/Firmware/FirmwareProgramViewModel.cs#L233)
```csharp
if (SoemFoEInterop.IsFoEAvailable)
{
    Master.StopActivity();                          // ① 停 Leal 主站，释放网卡
    ...
    var foeResult = await Task.Run(() =>
        SoemFoEInterop.ExecuteFoEWrite(             // ② 独立 SOEM 完成 FoE
            interfaceName, slaveAddr, filename, data, ...));
    ...
    int count = Master.StartActivity(interfaceName); // ③ 重启 Leal 主站
}
```

`ExecuteFoEWrite` ([Helpers/SoemFoEInterop.cs#L210](samples/Wpf.Ui.servoStudio/Helpers/SoemFoEInterop.cs#L210)) 把网卡名转成 pcap 名，调一个**单次 P/Invoke 完成全流程**的 native 函数 `soemfoe_foe_write_full`，返回 wkc 与诊断字符串。

native 端做的事（[native/soemfoe_wrapper.c](samples/Wpf.Ui.servoStudio/native/soemfoe_wrapper.c)）：
1. `soemfoe_alloc_context` —— 分配独立 `ecx_contextt`；
2. `ecx_init` + `ecx_config_init` —— 在隔离上下文里再扫一次网；
3. `ecx_writestate(BOOT)` + `ecx_statecheck(BOOT)` —— 把目标从站推到 BOOT；
4. `soemfoe_config_boot_mailbox` —— **关键步骤**：从 EEPROM word 0x0014 / 0x0016 读 bootstrap mailbox 配置，覆盖 `slavelist[i].SM[0/1]`、`mbx_wo/ro/l/rl`，再 `FPWR` 写回 SM0/SM1（标准 `ecx_config_init` 不会读 BOOT 邮箱，所以必须自己干）；
5. `soemfoe_flush_mbx` —— 清掉残留邮箱内容、重置 mbx 计数；
6. `ecx_FOEwrite` —— 真正按 FoE 协议分块发文件；
7. 期间错误用 `soemfoe_pop_error_string` 写进 diag_buf 给 C# 显示。

EEPROM (SII) 烧录走平行的一组函数：`soemfoe_write_eeprom` (FPWR)、`soemfoe_write_eeprom_ap` (APWR，SII 损坏时仍可用)、`soemfoe_reload_eeprom_ap` 触发 ESC 重新锁存身份。入口：[FirmwareProgramViewModel.cs#L480](samples/Wpf.Ui.servoStudio/ViewModels/Firmware/FirmwareProgramViewModel.cs#L480) 的 `ExecuteEepromWrite`，前置 ESI→SII 转换在 [Helpers/EsiToSiiConverter.cs](samples/Wpf.Ui.servoStudio/Helpers/EsiToSiiConverter.cs)。

---

## 四、整体时序图（文字版）

```
UI 选网卡
   │
   ▼
DeviceAddViewModel.OnEthernetConfirm
   │   ecatMaster.StartActivity(ifname)         ← 进入 NuGet/SOEM
   │      ├─ ecx_init  (npcap 打开网卡)
   │      ├─ ecx_config_init  (BRD/APRD 扫描+读 SII)
   │      ├─ ecx_config_map_group (PDO 映射)
   │      └─ statecheck → SAFE-OP，并起内部循环线程
   │         循环：ecx_send_processdata / ecx_receive_processdata
   ▼
StartSlaveStatePolling (DispatcherTimer 500ms)  ← UI 显示 AL 状态
   │
   ├──[用户点 NMT 按钮]── ecatMaster.WriteState(...) / ReadState(...)
   │
   ├──[各页面参数读写]── IServoMaster.Try{Read,Write}SDO  → CoE 邮箱
   │
   ├──[固件升级页面]── 暂停 Leal → SoemFoEInterop.ExecuteFoEWrite (独立 ctx)
   │                       └─ BOOT → 配 boot mailbox → FoEwrite → 退出
   │                  恢复 Leal (StartActivity)
   │
   ▼
OnEthernetDisconnect  → WriteState(Init) → StopActivity → 清 SDO 缓存
```

---

## 五、想看哪一层就跳哪里

- **何时建 / 销毁主站、调谁的 API、UI 怎么联动**：[DeviceAddViewModel.cs](samples/Wpf.Ui.servoStudio/ViewModels/DeviceSet/DeviceAddViewModel.cs)（连接 [#L444](samples/Wpf.Ui.servoStudio/ViewModels/DeviceSet/DeviceAddViewModel.cs#L444)、状态切换 [#L575-L668](samples/Wpf.Ui.servoStudio/ViewModels/DeviceSet/DeviceAddViewModel.cs#L575-L668)、轮询 [#L685](samples/Wpf.Ui.servoStudio/ViewModels/DeviceSet/DeviceAddViewModel.cs#L685)、断开 [#L523](samples/Wpf.Ui.servoStudio/ViewModels/DeviceSet/DeviceAddViewModel.cs#L523)）
- **页面如何透明访问 SDO**：[Core/IServoMaster.cs](samples/Wpf.Ui.servoStudio/Core/IServoMaster.cs)
- **协议栈实现**：NuGet `Leal.Core.Net.EtherCAT`（黑盒，对应 `lealsoem.dll`）— 见 [servoStudio.csproj#L52](samples/Wpf.Ui.servoStudio/servoStudio.csproj#L52)
- **FoE / EEPROM 旁路**：C# 侧 [Helpers/SoemFoEInterop.cs](samples/Wpf.Ui.servoStudio/Helpers/SoemFoEInterop.cs)，native 侧 [native/soemfoe_wrapper.c](samples/Wpf.Ui.servoStudio/native/soemfoe_wrapper.c)，编译脚本 [native/build_soemfoe.ps1](samples/Wpf.Ui.servoStudio/build_soemfoe.ps1)
- **ESI XML → SII 二进制**：[Helpers/EsiToSiiConverter.cs](samples/Wpf.Ui.servoStudio/Helpers/EsiToSiiConverter.cs)
- **FoE / EEPROM 调用入口**：[ViewModels/Firmware/FirmwareProgramViewModel.cs](samples/Wpf.Ui.servoStudio/ViewModels/Firmware/FirmwareProgramViewModel.cs)、[ViewModels/Firmware/EcatEepromViewModel.cs](samples/Wpf.Ui.servoStudio/ViewModels/Firmware/EcatEepromViewModel.cs)

需要我进一步展开哪一块（例如 PDO 映射 / 具体 SDO 调用栈 / FoE 数据帧细节），告诉我即可。

---

## 第二轮 — 用户提问

> 展开PDO映射和SDO调用栈的讲解

## 第二轮 — 助理回答

下面把 **PDO 映射** 与 **SDO 调用栈** 两条线分别拆开讲。所有"在线"的 PDO/SDO 真正的网络帧都在 NuGet `Leal.Core.Net.EtherCAT`（黑盒）里发，工程自身的代码只到 `EtherCATMaster.ReadSDO/WriteSDO/...` 这一层；想看更底层就要看 `lealsoem.dll`/SOEM。

---

## 一、PDO 映射

### 1.1 概念地图（CiA402 + ETG 1000.6）

PDO 配置在伺服**对象字典**里有 4 张相关的表，本工程在 [Models/Cia402Varibles.cs#L211-L233](samples/Wpf.Ui.servoStudio/Models/Cia402Varibles.cs#L211-L233) 集中定义：

| 索引段 | 含义 | 例子 |
|---|---|---|
| `0x1400-0x1403` | RxPDO **通信参数**（COB-ID / 传输类型） | `RxPdo1CommParam=0x1400` |
| `0x1600-0x1603` | RxPDO **映射**（主→从过程数据） | `RxPdo1Mapping=0x1600` |
| `0x1800-0x1803` | TxPDO **通信参数** | `TxPdo1CommParam=0x1800` |
| `0x1A00-0x1A03` | TxPDO **映射**（从→主过程数据） | `TxPdo1Mapping=0x1A00` |
| `0x1C12` | **SM2 分配表** = 当前启用了哪几个 RxPDO | SDO 0x1C12 子 0..N |
| `0x1C13` | **SM3 分配表** = 当前启用了哪几个 TxPDO | SDO 0x1C13 子 0..N |

每个 0x1600 / 0x1A00 子项是一个 32-bit 字段：

```
[31:16] = 对象索引       [15:8] = 子索引       [7:0] = 位长
```

工程提供了打包用的小工具：

[Models/Cia402Varibles.cs#L488](samples/Wpf.Ui.servoStudio/Models/Cia402Varibles.cs#L488)
```csharp
public static uint BuildPdoMapping(ushort index, byte subIndex, byte bitLength)
    => (uint)(index << 16 | subIndex << 8 | bitLength);
```

> 注意：本工程**不主动改写** PDO 映射（不调 0x1600/0x1A00 写 SDO），它把映射当作"由 ESI/EEPROM 决定"的固定结构，然后**只读出来展示**。真正的"映射重配置"业务，工程是通过 EEPROM/SII 烧录 + 重启来落地的（FoE/EEPROM 旁路那条线）。

### 1.2 离线生成（ESI XML → SII 二进制）

烧到从机 EEPROM 的 SII 二进制里就预置了 PDO 描述：

[Helpers/EsiToSiiConverter.cs#L30-L31](samples/Wpf.Ui.servoStudio/Helpers/EsiToSiiConverter.cs#L30-L31)
```csharp
private const ushort CAT_TXPDO = 50;   // SII Category 50 = TxPDO
private const ushort CAT_RXPDO = 51;   // SII Category 51 = RxPDO
```

[Helpers/EsiToSiiConverter.cs#L134-L136](samples/Wpf.Ui.servoStudio/Helpers/EsiToSiiConverter.cs#L134-L136)
```csharp
// ── 解析 PDO 定义 ──
var rxPdos = device.Elements("RxPdo").ToList();
var txPdos = device.Elements("TxPdo").ToList();
```

转换器把 ESI 里的 `<RxPdo>/<TxPdo>` 节点翻译成 ETG.2010 规定的 SII Category 50/51 块写进二进制；之后通过 `soemfoe_write_eeprom_ap` 把 SII 整块烧进 EEPROM，再 `soemfoe_reload_eeprom_ap` 让 ESC 重新锁存（流程见上一轮答复中 FoE 旁路那一节）。

### 1.3 上线扫描（SOEM 内部）

`ecatMaster.StartActivity(ifname)` 内部会调 SOEM 的 `ecx_config_init` + `ecx_config_map_group`，它的 PDO 行为是：
1. 读每个从机 EEPROM 的 SII（包含 PDO Category 与 SyncManager Category）；
2. CoE 通过 SDO 读 `0x1C12`/`0x1C13` 拿到当前生效的 PDO 列表；
3. 对每个生效的 0x1600/0x1A00，再用 SDO 读出每个子项 32-bit 映射；
4. 把所有 RxPDO 字节累加成一段连续内存映射给 SM2，把 TxPDO 累加给 SM3，并安装相应的 FMMU；
5. 起循环线程后，主站每周期**用 LRW（或 LRD/LWR）一帧把整段过程数据扫一遍** —— 这就是"PDO 数据流"，**完全不再走 SDO**。

> 也就是说：上电以后，你在 UI 看到的"控制字 0x6040、状态字 0x6041、目标位置 0x607A、实际位置 0x6064"等被映射进 PDO 的对象，**真正的高频读写是在 PDO 里**，进程上层调用的 SDO API 只有在变量**未被映射**时才走邮箱。

### 1.4 应用层"读回 + 展示"PDO 映射

业务页面在 EtherCAT EEPROM 页 ([ViewModels/Firmware/EcatEepromViewModel.cs#L155-L170](samples/Wpf.Ui.servoStudio/ViewModels/Firmware/EcatEepromViewModel.cs#L155-L170)) 用一组**纯 SDO 读取**复现整个 PDO 映射：

[EcatEepromViewModel.cs#L296-L307](samples/Wpf.Ui.servoStudio/ViewModels/Firmware/EcatEepromViewModel.cs#L296-L307)
```csharp
private (List<PdoMappingEntry> RxPdo, List<PdoMappingEntry> TxPdo) ReadPdoMappings(
    EtherCATMaster master, int slaveAddr)
{
    ReadSmPdoAssignment(master, slaveAddr, 0x1C12, "RxPDO", rxEntries); // SM2
    ReadSmPdoAssignment(master, slaveAddr, 0x1C13, "TxPDO", txEntries); // SM3
    return (rxEntries, txEntries);
}
```

[EcatEepromViewModel.cs#L309-L329](samples/Wpf.Ui.servoStudio/ViewModels/Firmware/EcatEepromViewModel.cs#L309-L329)
```csharp
byte count = master.ReadSDO<byte>(slaveAddr, smIndex, 0);          // SM 分配表条目数
for (int i = 1; i <= count; i++)
{
    ushort pdoIndex = master.ReadSDO<ushort>(slaveAddr, smIndex, i); // 例如 0x1600
    ReadPdoMappingObject(master, slaveAddr, pdoIndex, ...);
}
```

[EcatEepromViewModel.cs#L334-L360](samples/Wpf.Ui.servoStudio/ViewModels/Firmware/EcatEepromViewModel.cs#L334-L360)
```csharp
byte mapCount = master.ReadSDO<byte>(slaveAddr, pdoIndex, 0);       // 映射条目数
for (int j = 1; j <= mapCount; j++)
{
    uint mapping = master.ReadSDO<uint>(slaveAddr, pdoIndex, j);    // 32-bit
    int objIndex    = (int)((mapping >> 16) & 0xFFFF);
    int objSubIndex = (int)((mapping >> 8)  & 0xFF);
    int bitLen      = (int)(mapping & 0xFF);
    ...
}
```

收集完后填进 `RxPdoMappingEntries` / `TxPdoMappingEntries`，绑定到 [Views/Pages/FirmwarePages/FirmwarePage.xaml](samples/Wpf.Ui.servoStudio/Views/Pages/FirmwarePages/FirmwarePage.xaml) 的两个 TabItem。

PDO 数据流总结：

```
离线:  ESI XML ──[EsiToSiiConverter]──> SII 二进制
                  │
                  └─ Category 50 / 51 (PDO 描述)
                  └─ SII 写 EEPROM (FoE 旁路)
                                  │
                                  ▼
上线:  StartActivity → SOEM 读 EEPROM/SDO 1C12 1C13 1600 1A00
                    → 生成 SM2/SM3 映射、装 FMMU
                    → 周期 LRW: [TxPDO 字段] [RxPDO 字段]   ← 真正的过程数据
                                  │
                                  ▼
读回展示:  EcatEepromViewModel ── SDO 0x1C12/0x1C13/0x1600/0x1A00 ── UI 表格
```

---

## 二、SDO 调用栈

工程里有**三种** SDO 调用形态，从抽象到具体如下：

```
        UI 命令 / 数据存储 / 数据导入
                  │
   ┌──────────────┼──────────────┐
   ▼              ▼              ▼
(具体类型)  (统一抽象)  (诊断/批量读)
TryWriteSdo   IServoMaster.    EtherCATMaster.
TryReadSdo    Try{Read,Write}  ReadSDO<T> /
(各 ViewModel)SDO<T>(...)      ReadSDO(ref size, buf)
   │              │              │
   └──────────────┴──────┬───────┘
                         ▼
        EtherCATServoMasterAdapter (Core/IServoMaster.cs)
                         │
                         ▼
            EtherCATMaster  (NuGet Leal.Core.Net.EtherCAT)
                         │  ── 内部维护 SdoQueue.SdoModelDic 缓存
                         ▼
                     SOEM (lealsoem.dll)
                ecx_SDOread / ecx_SDOwrite
                         │
                         ▼
      CoE 邮箱 (SM0=主→从, SM1=从→主)
                         │
                         ▼
                从机对象字典 (0x6040/0x6041/0x607A/...)
```

### 2.1 顶层入口 —— 强类型业务包装

每个页面 ViewModel 自己包一层，把"取 Master/Axis、判断连接、异常处理"集中：

[ViewModels/Motion/MotionTypeViewModel.cs#L359-L376](samples/Wpf.Ui.servoStudio/ViewModels/Motion/MotionTypeViewModel.cs#L359-L376)
```csharp
private bool TryWriteSdo<T>(ushort index, byte subIndex, T value) where T : struct
{
    if (!IsConnected) return false;
    return Master.TryWriteSDO(Axis!.SlaveAddr, index, subIndex, value);
}

private bool TryReadSdo<T>(ushort index, byte subIndex, out T value) where T : struct
{
    value = default;
    if (!IsConnected) return false;
    return Master.TryReadSDO(Axis!.SlaveAddr, index, subIndex, out value);
}
```

带异常聚合的版本（参数页保存按钮一次写一堆）：

[MotionTypeViewModel.cs#L384-L394](samples/Wpf.Ui.servoStudio/ViewModels/Motion/MotionTypeViewModel.cs#L384-L394)
```csharp
private void SafeWriteSdo<T>(ushort index, byte subIndex, T value, List<string> errors, string name)
    where T : struct
{
    if (!TryWriteSdo<T>(index, subIndex, value))
        errors.Add($"{name}(0x{index:X4}:{subIndex})");
}
```

典型批量调用（CSP 模式参数）：

[MotionTypeViewModel.cs#L600-L604](samples/Wpf.Ui.servoStudio/ViewModels/Motion/MotionTypeViewModel.cs#L600-L604)
```csharp
SafeWriteSdo<int>  (Cia402OdIndex.TargetPosition,   0, ..., errors, "目标位置 (0x607A)");
SafeWriteSdo<int>  (Cia402OdIndex.PositionOffset,   0, ..., errors, "位置前馈 (0x60B0)");
SafeWriteSdo<int>  (Cia402OdIndex.VelocityOffset,   0, ..., errors, "速度前馈 (0x60B1)");
SafeWriteSdo<short>(Cia402OdIndex.TorqueOffset,     0, ..., errors, "转矩前馈 (0x60B2)");
SafeWriteSdo<uint> (Cia402OdIndex.InterpolationTimePeriod, 1, ..., errors, "插补周期 (0x60C2)");
```

> 这里**仍然是 SDO**，不是 PDO；用户每按一次"应用"才走一次邮箱，符合 CiA402"周期参数走 PDO、配置参数走 SDO"的规范。

### 2.2 协议无关抽象 —— `IServoMaster`

为了让同一个 ViewModel 同时支持 EtherCAT/Modbus/CANopen，所有调用最终先落到 `IServoMaster`：

[Core/IServoMaster.cs#L19-L25](samples/Wpf.Ui.servoStudio/Core/IServoMaster.cs#L19-L25)
```csharp
bool TryReadSDO<T>(int slaveAddr, ushort index, byte subIndex, out T value) where T : struct;
bool TryWriteSDO<T>(int slaveAddr, ushort index, byte subIndex, T value)    where T : struct;
T    ReadSDO<T>   (int slaveAddr, int index,    int subIndex)               where T : struct;
```

EtherCAT 的实现就是个空壳转发：

[Core/IServoMaster.cs#L52-L59](samples/Wpf.Ui.servoStudio/Core/IServoMaster.cs#L52-L59)
```csharp
public bool TryReadSDO<T>(int slaveAddr, ushort index, byte subIndex, out T value)
    => _master.TryReadSDO(slaveAddr, index, subIndex, out value);
public bool TryWriteSDO<T>(int slaveAddr, ushort index, byte subIndex, T value)
    => _master.TryWriteSDO(slaveAddr, index, subIndex, value);
public T ReadSDO<T>(int slaveAddr, int index, int subIndex)
    => _master.ReadSDO<T>(slaveAddr, index, subIndex);
```

`DeviceAddViewModel.ActiveServoMaster` 决定当前转发到 EtherCAT/Modbus/CANopen 哪个适配器（[DeviceAddViewModel.cs#L62](samples/Wpf.Ui.servoStudio/ViewModels/DeviceSet/DeviceAddViewModel.cs#L62)）。

### 2.3 NuGet 层 —— `EtherCATMaster`

来自 `Leal.Core.Net.EtherCAT`，本仓库不可见源码。能观察到的对外 API：

| 调用形态 | 用法 | 出现位置 |
|---|---|---|
| `TryReadSDO<T>(slave, idx, sub, out v)` | 强类型成功/失败 | 页面 ViewModel |
| `TryWriteSDO<T>(slave, idx, sub, v)` | 同上 | 页面 ViewModel |
| `ReadSDO<T>(slave, idx, sub)` | 强类型抛异常 | EEPROM 字典扫描，[EcatEepromViewModel.cs#L227](samples/Wpf.Ui.servoStudio/ViewModels/Firmware/EcatEepromViewModel.cs#L227) |
| `ReadSDO(slave, idx, sub, ref size, buf[])` | 任意长度（VisibleString / 大对象） | [EcatEepromViewModel.cs#L268, L283](samples/Wpf.Ui.servoStudio/ViewModels/Firmware/EcatEepromViewModel.cs#L268) |
| `WriteState / ReadState` | AL Status 状态机 | [DeviceAddViewModel.cs#L575-L668](samples/Wpf.Ui.servoStudio/ViewModels/DeviceSet/DeviceAddViewModel.cs#L575-L668) |
| `SdoQueue.SdoModelDic` | 库内置 SDO 模型缓存（断开时清） | [DeviceAddViewModel.cs#L513, L544](samples/Wpf.Ui.servoStudio/ViewModels/DeviceSet/DeviceAddViewModel.cs#L513) |

`SdoQueue` 是 Leal 内部的 SDO 请求模型字典：上层第一次按 `(idx, sub, T)` 访问时它会缓存解析好的对象描述符，避免每次 P/Invoke 重新组帧；断开时由业务层显式 `Clear()`。

### 2.4 协议栈层 —— SOEM (lealsoem.dll)

NuGet 内部对应的 SOEM 函数（你在自编的 [native/SOEM/include/soem/](samples/Wpf.Ui.servoStudio/native/SOEM) 也能看到同样符号）：

- `ecx_SDOread (ctx, slave, index, subindex, complete, &size, buf, timeout)`
- `ecx_SDOwrite(ctx, slave, index, subindex, complete,  size, buf, timeout)`

它们做的事：
1. 把请求按 CoE/SDO 协议打成邮箱帧（Initiate Upload/Download，必要时切到 Segmented/Expedited）；
2. 用 `FPWR` 写从机的 SM0 邮箱区（地址来自 EEPROM 0x0018），并把邮箱忙位置 1；
3. 周期内主站再用 `FPRD` 轮询从机的 SM1（地址来自 0x001A）拿应答；超时返回 wkc=0；
4. 失败时透传 SDO Abort Code（如 `0x06090011` 子索引不存在等）。

> 这一层需要 EtherCAT 至少在 **PRE-OP** 状态（邮箱可用）；**INIT** 不能 SDO，只能 ALControl/ALStatus。

### 2.5 数据流向 + 落盘

`DataSaveViewModel` 周期采样时调用同一个 `IServoMaster.TryReadSDO`：

[ViewModels/AppData/DataSaveViewModel.cs#L470-L475](samples/Wpf.Ui.servoStudio/ViewModels/AppData/DataSaveViewModel.cs#L470-L475)
```csharp
if (!_deviceAddViewModel.ActiveServoMaster.TryReadSDO(
        axis.SlaveAddr, variable.SdoIndex, variable.SdoSubIndex, out ushort rawValue))
    return false;
```

读到值之后通过 `FrameLogBridge` → `DataFrameLogger` 异步入队落盘：

[ViewModels/AppData/DataSaveViewModel.cs#L483-L502](samples/Wpf.Ui.servoStudio/ViewModels/AppData/DataSaveViewModel.cs#L483-L502)
```csharp
FrameLogBridge.LogMasterFrame(group, name, idx, sub, type, value, unit);
FrameLogBridge.LogSlaveFrame (group, name, idx, sub, type, value, unit);
```

`FrameLogBridge` 是无锁直通：

[Services/FrameLogBridge.cs#L48-L58](samples/Wpf.Ui.servoStudio/Services/FrameLogBridge.cs#L48-L58)
```csharp
DataFrameLogger.Instance.Enqueue(new DataFrameLogger.FrameRecord
{
    Timestamp = DateTime.Now,
    Source    = DataVariableSource.Master,
    SdoIndex  = index,
    SdoSubIndex = subIndex,
    DataType  = dataType,
    Value     = value?.ToString(),
    Unit      = unit,
});
```

后台线程从 `ConcurrentQueue` 出队写文件，**保证生产者（通信线程）零阻塞** —— 这正是 [Services/DataFrameLogger.cs#L18](samples/Wpf.Ui.servoStudio/Services/DataFrameLogger.cs#L18) 注释里强调的"不会阻塞 EtherCAT 通信"。

### 2.6 完整调用栈一例：写"目标位置 0x607A"

```
[UI 按钮: 应用 CSP 参数]
   │
ControlViewModel.OnApplyCsp / MotionTypeViewModel.OnApply
   │   SafeWriteSdo<int>(0x607A, 0, target, errors, "目标位置")
   ▼
MotionTypeViewModel.TryWriteSdo<int>(0x607A, 0, target)
   │   IsConnected? Axis?
   ▼
IServoMaster.TryWriteSDO(slaveAddr, 0x607A, 0, target)        ← 协议无关
   ▼
EtherCATServoMasterAdapter.TryWriteSDO(...)                    ← 转发
   ▼
EtherCATMaster.TryWriteSDO<int>(slaveAddr, 0x607A, 0, target)  ← NuGet
   │   SdoQueue.SdoModelDic[ (0x607A,0,int) ] 命中 / 新建
   ▼
P/Invoke → lealsoem.dll → ecx_SDOwrite(ctx, slave, 0x607A, 0, FALSE, 4, &target, EC_TIMEOUTRXM)
   │
   ├─ FPWR(SM0_addr) 把 CoE Download Initiate 帧塞进从机 mailbox-in
   └─ FPRD(SM1_addr) 轮询 mailbox-out 拿 Download Response
   ▼
返回 wkc → bool
```

读取（如 0x6041 状态字）方向反过来：CoE Upload Initiate → 应答里取 4 字节 → 反序列化为 `T`。

---

## 三、PDO vs SDO 在本工程里的角色总结

| 维度 | PDO | SDO |
|---|---|---|
| 周期性 | 主站循环线程每周期一次（μs 级） | 业务事件触发，毫秒级 |
| 数据通道 | EtherCAT LRW + SM2/SM3 + FMMU（过程数据帧） | CoE 邮箱（SM0/SM1） |
| 工程对其的控制粒度 | 通过 ESI/EEPROM 烧录间接控制（`EsiToSiiConverter` + FoE 旁路） | 业务直接调 `IServoMaster.Try{Read,Write}SDO` |
| 上线"读回"方式 | 通过 SDO 0x1C12/0x1C13/0x1600/0x1A00 重建（[EcatEepromViewModel](samples/Wpf.Ui.servoStudio/ViewModels/Firmware/EcatEepromViewModel.cs)） | 直接读 |
| 工程入口典型代码 | [EcatEepromViewModel.cs#L296](samples/Wpf.Ui.servoStudio/ViewModels/Firmware/EcatEepromViewModel.cs#L296)、[Cia402Varibles.cs#L488](samples/Wpf.Ui.servoStudio/Models/Cia402Varibles.cs#L488)、[EsiToSiiConverter.cs#L134](samples/Wpf.Ui.servoStudio/Helpers/EsiToSiiConverter.cs#L134) | [MotionTypeViewModel.cs#L359](samples/Wpf.Ui.servoStudio/ViewModels/Motion/MotionTypeViewModel.cs#L359)、[IServoMaster.cs#L19](samples/Wpf.Ui.servoStudio/Core/IServoMaster.cs#L19)、[DataSaveViewModel.cs#L470](samples/Wpf.Ui.servoStudio/ViewModels/AppData/DataSaveViewModel.cs#L470) |

要再下钻，可以告诉我具体一类（例如：**SDO Abort Code 怎么传到 UI**、**字符串/大对象 SDO 分段读**、或者 **PDO 字段如何在 Leal 库中映射到 C# 缓冲区**）。

---

## 第三轮 — 用户提问

> 介绍SDO分段读和PDO如何在库中映射的。在此之后，将这段对话中你和我的所有内容输出到一个本地markdown文件中

## 第三轮 — 助理回答

已收集到全部所需材料。下面是第三轮回答，随后将整段对话写入本地 markdown 文件。

---

## 第三轮回答

### 8.1 SDO 分段读（Segmented Upload）

**前提：mailbox 容量限制**

SDO 走 CoE 邮箱（SM0=Mailbox Out / SM1=Mailbox In）。邮箱容量在 ESI 中给出，常见 `MailboxSize = 128/256` 字节，扣掉 SDO 头后单帧能搬 ≤ `mbxlength - 10` B 数据。SOEM 据此把上传分三种：

| 形态 | 触发条件 | SDO 命令字节 |
|---|---|---|
| Expedited | 数据 ≤ 4 B | `cmd & 0x02` 置位（含数据长度） |
| Normal | 4 B < data ≤ mailbox-10 B | 单帧带 `SDOlen` |
| Segmented | data > mailbox-10 B | 多帧循环，toggle 位翻转 |

**入口（C# 侧）**

`master.ReadSDO` 是 Leal 暴露的同名包装；典型调用见 [samples/Wpf.Ui.servoStudio/ViewModels/Firmware/EcatEepromViewModel.cs](samples/Wpf.Ui.servoStudio/ViewModels/Firmware/EcatEepromViewModel.cs#L263)：

```csharp
// VisibleString，最大 128 B
int size = 128;
byte[] buf = new byte[size];
master.ReadSDO(slaveAddr, index, subIndex, ref size, buf);
```

`buf` 被 pin 后以 `void*` 传入底层 `ecx_SDOread`，对应 SOEM 原型 `ecx_SDOread(ctx, slave, index, sub, CA, *psize, *p, timeout)`，其中 `p` 即托管 `buf` 的固定地址，`psize` in/out。

**SOEM 内部状态机**（[samples/Wpf.Ui.servoStudio/native/SOEM/src/ec_coe.c](samples/Wpf.Ui.servoStudio/native/SOEM/src/ec_coe.c#L117)）：

1. 发出 `ECT_SDO_UP_REQ`（或 CA 形式 `ECT_SDO_UP_REQ_CA`）。
2. 收到首响应：
   - `cmd & 0x02` ⇒ Expedited，长度从 `cmd` 高位解出，直接 `memcpy` 4 B 内出参。
   - 否则读 `SDOlen`，若 ≤ Framedatasize（mbx-10），单帧搞定。
   - 若 `SDOlen` > 单帧能力，进入 segmented 循环：
     - 先把首帧已带的数据拷走、`hp += Framedatasize`、`SDOlen -= Framedatasize`。
3. Segmented 循环（约 L230–L295）：
   ```c
   SDOp->Command = ECT_SDO_SEG_UP_REQ + toggle;   // 0x60 / 0x70 交替
   /* 发 + 收 */
   if ((aSDOp->Command & 0x01) > 0) {              // 末段标志
      if (Framedatasize == 7)
         Framedatasize -= ((aSDOp->Command & 0x0e) >> 1);  // 末段 padding
      NotLast = FALSE;
   }
   memcpy(hp, &(aSDOp->Index), Framedatasize);     // 段载荷紧跟在 Index 字段位置
   hp += Framedatasize;
   *psize += Framedatasize;
   toggle ^= 0x10;                                 // 切换 toggle
   ```
   `toggle ^= 0x10` 是从机识别"这是新一段，不是重传"的唯一依据；丢一帧就死锁，超时由 `EC_TIMEOUTRXM` 兜底。
4. 任何一段返回 `cmd == ECT_SDO_ABORT(0x80)`，立刻调 `ecx_SDOerror` 把 4 B abort code（如 `0x06090011` "subindex 不存在"）入错误队列，C# 端 `ReadSDO` 返回失败。

**文本对象的真实路径**

`ReadVisibleString(0x1008 制造商名称)` 多半是 normal 单帧；若读 `0x1018:04 序列号扩展` 或某些大型 `Identity Object` 的字符串字段，长度 >118 B 即触发 segmented，循环可能跑 2–3 次，每次推进 ≤7 B（标准段大小）。整个过程对上层透明——C# 只看到 `size` 出参变成实际字节数。

---

### 8.2 PDO 在库内是怎么"映射"成内存的

PDO 在线工作不是 C# 侧驱动，而是 Leal 内置 SOEM cycle thread 调度。从 `ecx_config_init()` → `ecx_config_map_group(pIOmap, group)` 一路推下去：

**步骤 A：读 PDO 装配 → 计算位宽**

在 PreOP 下，SOEM 对每个 slave 顺序 SDO 读：
- `0x1C12:0` → 拿到 RxPDO 个数 → 逐个读 `0x1C12:1..n` 拿到 PDO 索引（如 0x1600）
- `0x1600:0` → 条目数 → `0x1600:1..k` 拿到 32-bit 映射字（Index/Sub/BitLen）
- `0x1A00` 同理处理 TxPDO

把 BitLen 累加得 `Ibits`、`Obits`，转字节：

```c
context->slavelist[slave].Ibytes = (Ibits + 7) / 8;
context->slavelist[slave].Obytes = (Obits + 7) / 8;
```

（[samples/Wpf.Ui.servoStudio/native/SOEM/src/ec_config.c](samples/Wpf.Ui.servoStudio/native/SOEM/src/ec_config.c#L655)）

**步骤 B：分配 SyncManager**

`SM2 = RxPDO 区` `SM3 = TxPDO 区`；SOEM 计算每个 SM 的 PhysStart/Length，FPWR 写入 ESC 寄存器 `0x0810/0x0818/...`。

**步骤 C：建 FMMU + 切片到 IOmap**

核心：用户给 SOEM 一段 C 缓冲 `pIOmap`（Leal 内部分配），SOEM 把每个 slave 的输入/输出在这段缓冲里"切位置"，然后用 FMMU 把"逻辑地址段 ↔ 物理 SM 段"绑死。

[samples/Wpf.Ui.servoStudio/native/SOEM/src/ec_config.c](samples/Wpf.Ui.servoStudio/native/SOEM/src/ec_config.c#L862)（输入侧节选）：

```c
context->slavelist[slave].FMMU[FMMUc].LogStart = htoel(*LogAddr);
...
ecx_FPWR(&context->port, configadr,
         ECT_REG_FMMU0 + sizeof(ec_fmmut) * FMMUc,
         sizeof(ec_fmmut),
         &(context->slavelist[slave].FMMU[FMMUc]),
         EC_TIMEOUTRET3);

context->slavelist[slave].inputs =
    (uint8*)pIOmap + etohl(context->slavelist[slave].FMMU[FMMUc].LogStart)
                   - context->grouplist[group].logstartaddr;
```

含义：
- `LogAddr` 是单调递增的"全网逻辑地址游标"，每分配一个 slave 就推进 `Ibytes`/`Obytes`。
- FMMU 寄存器告诉 ESC："收到的 LRW 帧里 `[LogStart, LogStart+LogLength)` 这一段，请映射到我本地 SM3（输入）的 PhysStart"。
- C 侧再把 `slavelist[i].inputs` 设为 `pIOmap + LogStart - groupBase`，于是 **`pIOmap` 这个大字节数组刚好整段就是 LRW 帧的 payload**——Leal 在 cycle thread 每轮调 `ecx_send_processdata()`/`ecx_receive_processdata()`，发送时 `pIOmap` 输出区被打包进 LRW，接收时输入区由 ESC 自动写回。

**步骤 D：C# 侧看到什么**

Leal 把 `slavelist[i].inputs` 这个 `byte*` 用反射/特性绑到 `EtherCATSlave_CiA402` 的强类型属性（`StatusWord`/`PositionActualValue` 等）。所以 `axis.PositionActualValue` 取值时只是访问 `*(int*)(inputs + offset)`——零 SDO、零锁、纯内存读，offset 在 PreOP 阶段已由上面的 PDO 解析固化。

**整体数据通路图**

```
  ESI XML (.xml)
       │  EsiToSiiConverter.GenerateSii()
       ▼
  SII binary  ──FoE──►  Slave EEPROM (0x1600/0x1A00 默认值)
                                  │  Init→PreOP 时 ESC 自加载
                                  ▼
                       OD 0x1600 / 0x1A00 / 0x1C12 / 0x1C13
                                  │  ecx_config_init 读 SDO
                                  ▼
              累加位宽 → Ibytes / Obytes
                                  │
                                  ▼
              ecx_config_map_group(pIOmap)
                ├─ 计算 LogStart / LogLength
                ├─ FPWR 写入 FMMU[0..n]
                └─ slavelist[i].inputs/outputs = pIOmap + offset
                                  │
                                  ▼
              cycle thread:  LRW(LogAddr, len=ΣIO) ⇄ pIOmap
                                  │
                                  ▼
        Leal 强类型属性 EtherCATSlave_CiA402.StatusWord etc.
                                  │
                                  ▼
            EtherCATServoAxisAdapter → IServoAxis → ViewModel
```

要点回顾：
- **0x1600 是"配方"**：每个 32-bit 字告诉从机"PDO 里第 N 字节是 OD 哪个项"。
- **SM2/SM3 是"信箱口"**：物理上从机如何看 PDO。
- **FMMU 是"路由表"**：把全局 LRW 的某段字节路由到本地某 SM。
- **`pIOmap` 是"邮局总分拣台"**：所有从机的 PDO 在主机内存里拼成一整块，LRW 一帧搞定。

---

下面把三轮对话写入本地文件。