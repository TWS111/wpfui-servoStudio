# CAN 适配器原生 DLL 部署目录

工程对常见 CAN 卡的连接适配通过 P/Invoke 加载各厂家官方 DLL 实现。请按下表把对应 **64-bit** DLL
放入下列子目录后重新构建工程，构建脚本会通过 `servoStudio.csproj` 的
`<None Include="native\can\**" CopyToOutputDirectory="PreserveNewest"/>` 规则
将其连同子目录一起拷贝到运行时输出目录（`bin\<Configuration>\net9.0-windows*\native\can\`）。

由于授权与版权原因，**仓库中不附带厂家二进制文件**，请按下方链接从厂家官网获取：

| 子目录 | 必需 DLL | 适用设备 | 官方下载页 |
|---|---|---|---|
| `peak\` | `PCANBasic.dll` (x64) | PEAK PCAN-USB / PCI / PCIe | <https://www.peak-system.com/Drivers.523.0.html?L=1> → "PCAN-Basic API" |
| `controlcan\` | `ControlCAN.dll` (x64) + `kerneldlls\*` | 周立功 USBCAN-I/II、创芯科技 CANalyst-II（"CAN II"）、致远 USBCAN-EU、其他 ControlCAN 兼容卡 | <https://www.zlg.cn/can/down/down/id/22.html> 或创芯科技 CANalyst-II 资料盘 |
| `zlgcan\` | `zlgcan.dll` (x64) + 配套 `kerneldlls\` | 周立功新一代统一 SDK：USBCAN-E-U / 2E-U、USBCANFD-MINI/100U/200U、PCIe-9221 等 | <https://manual.zlg.cn/web/#/188> → "ZLGCAN 系列接口卡 Windows 驱动 + 库" |
| `toomoss\` | `usb_device.dll` (x64) | 广成 / 德州 Toomoss USB2CAN / USB2CANFD-X1/X2 / USB2XXX | <https://www.toomoss.com/?cn/Download/Index/cid/19> |

> **拷贝目标位置选择**  
> 上面默认放在 `native\can\<vendor>\` 下；如果厂家 DLL 依赖兄弟 DLL（如 `zlgcan.dll` 旁边
> 必须有 `kerneldlls\` 目录），保持目录结构原样拷贝即可，工程不会修改文件名。  
> 程序启动时由 `Core/CANopen/Adapters/CanAdapterFactory.cs` 在
> `AppDomain.BaseDirectory`、`bin\...\` 与 `PATH` 三处搜索 DLL；建议把 vendor 目录加入 PATH，
> 或在 `App.xaml.cs` 启动时 `SetDllDirectory("native\\can\\peak")`。

## 适配器矩阵

| 适配器实现 | `ICanBus` 类 | 类别枚举 | 设备识别函数 |
|---|---|---|---|
| LAWICEL slcan 串口 | `Core.CANopen.SerialCanBus` | `Slcan` | `SerialPort.GetPortNames()` |
| PEAK PCAN-Basic | `Core.CANopen.Adapters.PcanBasicCanBus` | `PcanBasic` | `CAN_GetValue(PCAN_CHANNEL_CONDITION)` |
| ControlCAN（ZLG/创芯/CANII） | `Core.CANopen.Adapters.ControlCanBus` | `ControlCan` | `VCI_OpenDevice/CloseDevice` |
| ZLG zlgcan | `Core.CANopen.Adapters.ZlgCanBus` | `Zlgcan` | `ZCAN_OpenDevice/CloseDevice` |
| Toomoss USB2XXX | `Core.CANopen.Adapters.ToomossCanBus` | `Toomoss` | `USB_ScanDevice` |
| 虚拟回环 | `Core.CANopen.VirtualCanBus` | `Virtual` | （总是可用） |

各类别的"已部署 + 已插入设备"由 `CanAdapterFactory.Enumerate()` 实时检查，UI 在 *设备添加* 页 →
*CANopen* 标签下的"CAN 适配器"下拉中显示。

## DLL 缺失/未插入设备的行为

- **DLL 缺失**：对应类别在下拉中以"(不可用)"形式显示，连接按钮无效；其他类别正常使用。
- **DLL 已就绪、设备未插入**：同上，下拉条目标记 `IsAvailable=false`，提示"未检测到设备"。
- **DLL 由 .NET P/Invoke 延迟绑定加载**：首次调用厂家函数时才会实际 LoadLibrary；
  捕获 `DllNotFoundException` 后回退失败，不影响应用启动。

## 如何新增厂家适配器

1. 在 `Core/CANopen/Adapters/` 新建 `<Vendor>CanBus.cs`，实现 `ICanBus` 即可（参照 `PcanBasicCanBus`）。
2. 在 `CanAdapterKind.cs` 增加枚举值。
3. 在 `CanAdapterFactory.cs` 的 `Enumerate()` / `Create()` 各加一个分支。
4. 在本 README 表格补一行。
