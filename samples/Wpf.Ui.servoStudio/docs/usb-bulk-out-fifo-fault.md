# USB BULK OUT FIFO 满 / 未服务故障 — 从机端调试指南

**记录日期：** 2026-05-27  
**涉及版本：** HighendServo 固件（调试阶段）  
**主机侧现象来源：** GX Servo Studio 总线诊断页 → USB 回环测试

---

## 一、复现现象（主机侧观测）

| 阶段 | 统计指标 | 说明 |
|------|---------|------|
| 阶段一：初始 N 包 | 已发包 ≤ ~16，丢包同步增加 | 数据进入从机硬件 FIFO，USB SIE 自动 ACK；**固件从未取走** |
| 阶段二：FIFO 满后 | `WinUsb_WritePipe` 返回失败，GLE = 121（0x79 = `ERROR_SEM_TIMEOUT`） | 设备 FIFO 满 → 对后续 BULK OUT token 持续 NAK → 主机等到 PipeTimeout 用尽 |
| 阶段三：持续 | 所有发包均立即失败，数据错误值累增 | WinUSB 默认 `AUTO_CLEAR_STALL=FALSE`，pipe 进入半卡死状态 |

数字 "8" 对应 HPM6E 的 USBD 控制器单端点 BD/QH 深度（4～8 个 Transfer Buffer），**主机成功数量由硬件 FIFO 吃掉多少决定，完全绕过固件参与**。

---

## 二、根因分析

### 根因：USBX 设备端 BULK OUT 端点 Transfer Request 未被调用（或仅调用一次后未重新 arm）

USBX 是**异步非阻塞模型**，端点接收必须由固件主动调用 `ux_device_stack_transfer_request`（或封装 API）。  
若缺失以下任一步骤，主机数据只在硬件 FIFO 里积压，最终导致 NAK 超时：

1. **首次 arm** — `SET_CONFIGURATION` 完成、class activation 回调执行后，必须显式 post 一次 transfer request
2. **回调内重新 arm** — 每次读完成回调执行后，必须立即 post 下一次 transfer request（否则仅能接收一个事务）

---

## 三、排查清单

### 3.1 检查 USBX class activation 回调

```c
/* 伪代码：自定义 class _activate 或 _change 回调 */
UINT my_class_activate(UX_SLAVE_CLASS_COMMAND *command)
{
    MY_CLASS *cls = (MY_CLASS *)command->ux_slave_class_command_class->ux_slave_class_instance;

    /* !! 必须在此处 post 首次 BULK OUT transfer request !! */
    ux_device_stack_transfer_request(
        cls->bulk_out_endpoint->ux_slave_endpoint_transfer_request,
        /* requested_length = wMaxPacketSize 或更大 */
        512,
        512);

    return UX_SUCCESS;
}
```

### 3.2 检查读完成回调内重新 arm

```c
/* 读完成回调 */
VOID my_bulk_out_complete(UX_SLAVE_TRANSFER *transfer)
{
    ULONG actual_len = transfer->ux_slave_transfer_request_actual_length;

    /* 处理数据 */
    process_received_data(transfer->ux_slave_transfer_request_data_pointer, actual_len);

    /* !! 必须重新 arm，否则后续包无法接收 !! */
    ux_device_stack_transfer_request(
        transfer,
        512,
        512);
}
```

### 3.3 检查 BULK OUT 端点描述符 wMaxPacketSize

```c
/* USB 2.0 High-Speed 下 Bulk 端点 wMaxPacketSize 必须为 512 */
/* 若写成 64（FS 默认）而主机以 512 发送，会触发 babble error → 端点 stall */
static const UCHAR my_interface_descriptor[] = {
    /* ... */
    0x07,           /* bLength */
    0x05,           /* bDescriptorType = Endpoint */
    0x01,           /* bEndpointAddress = OUT, EP1 */
    0x02,           /* bmAttributes = Bulk */
    0x00, 0x02,     /* wMaxPacketSize = 512  ← 必须为此值！*/
    0x00,           /* bInterval */
};
```

### 3.4 确认接收 buffer 足够大

USBX transfer request 的 buffer 长度必须 **≥ wMaxPacketSize**，否则收到大包会直接 STALL：

```c
/* 静态分配接收 buffer，至少 512 字节 */
static UCHAR bulk_out_buffer[512];

/* Post transfer request 时指定此 buffer */
transfer_request->ux_slave_transfer_request_data_pointer = bulk_out_buffer;
transfer_request->ux_slave_transfer_request_requested_length = sizeof(bulk_out_buffer);
```

### 3.5 查看 USBX 内部计数器

在固件中加临时打印（或通过 SEGGER RTT 输出）验证：

```c
/* 确认 activation 进了几次 */
UX_TRACE_IN_LINE_INSERT(UX_TRACE_DEVICE_CLASS_xxx_ACTIVATE, ...);

/* 确认 transfer_request 调用次数 */
static volatile uint32_t g_bulk_out_arm_count = 0;
g_bulk_out_arm_count++;  /* 在每次 ux_device_stack_transfer_request 调用前 */

/* 确认回调进了几次、actual_length 多少 */
static volatile uint32_t g_bulk_out_cb_count = 0;
g_bulk_out_cb_count++;
```

---

## 四、二次验证方法（Loopback 测试）

在固件中实现最小回环：把 BULK OUT 收到的字节原样发到 BULK IN，能秒级验证收发双向：

```c
VOID bulk_loopback_test(UX_SLAVE_TRANSFER *out_transfer)
{
    ULONG len = out_transfer->ux_slave_transfer_request_actual_length;
    UX_SLAVE_TRANSFER *in_transfer = get_bulk_in_transfer();

    /* 回写到 BULK IN */
    ux_utility_memory_copy(
        in_transfer->ux_slave_transfer_request_data_pointer,
        out_transfer->ux_slave_transfer_request_data_pointer,
        len);
    in_transfer->ux_slave_transfer_request_requested_length = len;
    ux_device_stack_transfer_request(in_transfer, len, len);

    /* 重新 arm BULK OUT */
    ux_device_stack_transfer_request(out_transfer, 512, 512);
}
```

主机侧打开总线诊断页 → USB → 回环测试，包大小 64 B，间隔 10 ms，观察：
- 若 "已发包" 持续增加且 "回声成功 ≈ 已发包" → 收发双向正常
- 若 "丢包" 累增 → arm 缺失或回调未重 post

---

## 五、USBD 端点状态寄存器确认 STALL

通过 SEGGER J-Link 内存读取确认端点是否被 stall（HPM6E Chip Reference 中找 USB_ENDPTSTAT / ENDPTFLUSH）：

```
Monitor Expression: USB0->ENDPTSTAT  /* 各 bit 对应端点方向 stall 状态 */
```

若 EP1 OUT bit 置位，需执行 `USB0->ENDPTFLUSH = (1 << 1)` 后重新初始化。

---

## 六、主机侧配合改进（已计划）

| 改进项 | 说明 | 状态 |
|--------|------|------|
| AUTO_CLEAR_STALL | WinUSB 检测到 pipe stall 后自动发 `ClearFeature(ENDPOINT_HALT)` | 待实施 |
| WinUsb_ResetPipe | 发送失败后主动调用，使 pipe 可恢复重试 | 待实施 |

---

## 七、相关代码文件

| 文件 | 说明 |
|------|------|
| `samples/Wpf.Ui.servoStudio/Core/Usb/UsbBulkBus.cs` | 主机侧 WinUSB 收发实现 |
| `samples/Wpf.Ui.servoStudio/Core/Usb/UsbDefs.cs` | 设备接口 GUID、端点默认值、超时配置 |
| `src/Application/source/UsbXTest.c`（固件） | MS OS 2.0 描述符注册、BULK OUT endpoint 初始化 |
