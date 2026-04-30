// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Diagnostics;
using System.IO;
using Core.Net.EtherCAT;
using Core.Net.EtherCAT.SeedWork;
using Core.Net.EtherCAT.SeedWork.Interrop;
using Microsoft.Win32;
using Wpf.Ui.servoStudio.Helpers;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.Services;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels.Firmware;

public partial class FirmwareProgramViewModel(DeviceAddViewModel deviceAddViewModel) : ViewModel
{
    private bool _isInitialized = false;

    #region EtherCAT 辅助

    private EtherCATMaster Master => deviceAddViewModel.EcatMaster;
    private EtherCATSlave_CiA402? Axis => deviceAddViewModel.CurrentAxis;

    #endregion

    #region 属性

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionInfo = "设备未连接";

    [ObservableProperty]
    private string _deviceState = string.Empty;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private int _progressValue;

    [ObservableProperty]
    private int _progressMax = 100;

    // ── XML 文件信息 ──

    [ObservableProperty]
    private string _selectedFilePath = string.Empty;

    [ObservableProperty]
    private string _selectedFileName = string.Empty;

    [ObservableProperty]
    private long _selectedFileSize;

    [ObservableProperty]
    private bool _isFileSelected;

    // ── FoE 密码 ──

    [ObservableProperty]
    private string _foePasswordText = "0x";

    // ── FoE 目标文件名 ──

    [ObservableProperty]
    private string _foeTargetFileName = string.Empty;

    // ── 已签名固件 (跳过 OTA 打包) ──

    [ObservableProperty]
    private bool _isPreProcessedFirmware;

    // ── 烧录日志 ──

    [ObservableProperty]
    private string _burnLog = string.Empty;

    // ── ESI XML EEPROM 烧录 ──

    [ObservableProperty]
    private string _esiFilePath = string.Empty;

    [ObservableProperty]
    private string _esiFileName = string.Empty;

    [ObservableProperty]
    private long _esiFileSize;

    [ObservableProperty]
    private bool _isEsiFileSelected;

    [ObservableProperty]
    private string _esiBurnLog = string.Empty;

    [ObservableProperty]
    private string _esiStatusText = "就绪";

    [ObservableProperty]
    private bool _isEsiBusy;

    [ObservableProperty]
    private int _esiProgressValue;

    [ObservableProperty]
    private int _esiProgressMax = 100;

    #endregion

    #region 命令

    /// <summary>
    /// 选择本地 XML 文件
    /// </summary>
    [RelayCommand]
    private void OnSelectFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择固件文件",
            Filter = "BIN 文件 (*.bin)|*.bin|所有文件 (*.*)|*.*",
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
        {
            SelectedFilePath = dialog.FileName;
            SelectedFileName = Path.GetFileName(dialog.FileName);

            var fileInfo = new FileInfo(dialog.FileName);
            SelectedFileSize = fileInfo.Length;
            IsFileSelected = true;

            AppendLog($"已选择文件: {SelectedFileName} ({SelectedFileSize:N0} 字节)");
            StatusText = $"已选择: {SelectedFileName}";
        }
    }

    /// <summary>
    /// 清除已选择的文件
    /// </summary>
    [RelayCommand]
    private void OnClearFile()
    {
        SelectedFilePath = string.Empty;
        SelectedFileName = string.Empty;
        SelectedFileSize = 0;
        IsFileSelected = false;
        StatusText = "已清除文件选择";
    }

    /// <summary>
    /// 通过 FoE 将 XML 文件烧录到从站设备。
    /// FoE 流程: 暂停 Leal 主站 → 独立 SOEM 上下文执行 FoE → 恢复 Leal 主站。
    /// 若 FoE 不可用则回退到 SDO 分段传输。
    /// </summary>
    [RelayCommand]
    private async Task OnBurnXml()
    {
        if (!IsFileSelected || string.IsNullOrEmpty(SelectedFilePath))
        {
            StatusText = "请先选择要烧录的 XML 文件";
            return;
        }

        if (!deviceAddViewModel.IsEthernetConnected || Axis == null)
        {
            StatusText = "设备未连接，无法烧录";
            return;
        }

        IsBusy = true;
        ProgressValue = 0;

        // 停止从站状态轮询，避免与烧录操作并发访问本机 EtherCAT 资源
        deviceAddViewModel.StopSlaveStatePolling();

        try
        {
            byte[] fileData;

            if (IsPreProcessedFirmware)
            {
                // ── 已签名固件: 直接读取，跳过 OTA 打包 ──
                AppendLog("已选择预处理固件，跳过 OTA 打包...");
                fileData = await Task.Run(() => File.ReadAllBytes(SelectedFilePath));
                ProgressMax = fileData.Length;
                AppendLog($"文件大小: {fileData.Length:N0} 字节");
            }
            else
            {
                // ── 原始固件: 先进行 OTA 打包签名处理 ──
                AppendLog("正在进行 OTA 打包签名处理...");
                StatusText = "OTA 打包签名...";
                string otaSignedPath = await Task.Run(() => RunOtaPackTool(SelectedFilePath));
                if (string.IsNullOrEmpty(otaSignedPath))
                {
                    StatusText = "OTA 打包失败";
                    return;
                }

                AppendLog($"OTA 打包完成: {Path.GetFileName(otaSignedPath)}");

                AppendLog("正在读取签名后的固件文件...");
                fileData = await Task.Run(() => File.ReadAllBytes(otaSignedPath));
                ProgressMax = fileData.Length;
                AppendLog($"签名后文件大小: {fileData.Length:N0} 字节");
            }

            int slaveAddr = Axis.SlaveAddr;
            string foeFileName = string.IsNullOrWhiteSpace(FoeTargetFileName)
                ? SelectedFileName
                : FoeTargetFileName.Trim();
            string interfaceName = deviceAddViewModel.SelectedEthernetName;
            bool transferSuccess = false;

            // ── 方案 A: FoE 传输 (独立 SOEM 上下文) ──
            if (SoemFoEInterop.IsFoEAvailable)
            {
                AppendLog("检测到 soemfoe.dll，使用 FoE 协议传输");
                StatusText = "准备 FoE 传输...";

                // A1. 暂停 Leal 主站 (释放网卡独占)
                AppendLog("暂停 Leal 主站以释放网卡...");
                await Task.Run(() =>
                {
                    Master.WriteState(slaveAddr, SlaveState.Init);
                    Master.StopActivity();
                });
                AppendLog("Leal 主站已暂停");

                // A2. 使用独立 SOEM 上下文执行 FoE
                StatusText = "正在通过 FoE 烧录...";
                SoemFoEInterop.FoEResult foeResult = await Task.Run(() =>
                    SoemFoEInterop.ExecuteFoEWrite(
                        interfaceName, slaveAddr, foeFileName, fileData,
                        password: ParseFoePassword(FoePasswordText),
                        onLog: msg => System.Windows.Application.Current.Dispatcher.Invoke(
                            () => AppendLog($"  [FoE] {msg}"))));

                transferSuccess = foeResult.Success;
                if (transferSuccess)
                {
                    ProgressValue = ProgressMax;
                    AppendLog($"✓ FoE 传输成功 (wkc={foeResult.WorkCounter})");
                }
                else
                {
                    AppendLog($"✗ FoE 传输失败: {foeResult.ErrorMessage}");
                }

                // A3. 恢复 Leal 主站连接
                AppendLog("正在恢复 Leal 主站连接...");
                StatusText = "恢复设备连接...";
                await ReconnectLealMaster(interfaceName, slaveAddr);
            }
            else
            {
                AppendLog("⚠ soemfoe.dll 不可用，使用 SDO 分段传输");
            }

            // ── 方案 B: 回退到 SDO 分段传输 ──
            if (!transferSuccess)
            {
                AppendLog("正在切换从站至 INIT...");
                StatusText = "切换从站至 INIT 状态...";
                await Task.Run(() => Master.WriteState(slaveAddr, SlaveState.Init));

                AppendLog("开始 SDO 分段传输 (0xF010)...");
                StatusText = "正在通过 SDO 烧录...";
                transferSuccess = await Task.Run(() => WriteFirmwareViaSDO(slaveAddr, fileData));

                if (transferSuccess)
                {
                    ProgressValue = ProgressMax;
                    AppendLog("✓ SDO 分段传输完成");
                }
                else
                {
                    AppendLog("✗ SDO 分段传输失败");
                }

                // 恢复从站状态
                AppendLog("正在恢复从站状态至 INIT...");
                try
                {
                    await Task.Run(() => Master.WriteState(slaveAddr, SlaveState.Init));
                    AppendLog("从站已恢复 INIT 状态");
                }
                catch (Exception ex)
                {
                    AppendLog($"⚠ 恢复状态异常: {ex.Message}");
                }
            }

            StatusText = transferSuccess ? "烧录成功" : "烧录失败";

            // Persist firmware settings for next session
            UserSettings settings = Services.UserSettingsService.Load();
            settings.FoeTargetFileName = FoeTargetFileName;
            settings.FoePasswordText = FoePasswordText;
            settings.IsPreProcessedFirmware = IsPreProcessedFirmware;
            Services.UserSettingsService.Save(settings);

            // 记录日志
            AppData.AppLogViewModel.Log(
                transferSuccess ? AppLogLevel.Info : AppLogLevel.Warning,
                AppLogCategory.Firmware,
                transferSuccess ? "固件烧录完成" : "固件烧录失败",
                $"文件: {SelectedFileName}, 大小: {fileData.Length:N0} 字节");
        }
        catch (Exception ex)
        {
            AppendLog($"✗ 烧录异常: {ex.Message}");
            StatusText = $"烧录失败: {ex.Message}";
            AppData.AppLogViewModel.Log(AppLogLevel.Error, AppLogCategory.Firmware,
                "固件烧录失败", ex.Message);
        }
        finally
        {
            IsBusy = false;

            // 恢复从站状态轮询
            deviceAddViewModel.StartSlaveStatePolling();
        }
    }

    /// <summary>
    /// FoE 操作完成后恢复 Leal 主站连接。
    /// </summary>
    private async Task ReconnectLealMaster(string interfaceName, int slaveAddr)
    {
        try
        {
            (int Count, string State) result = await Task.Run(() =>
            {
                int count = Master.StartActivity(interfaceName);
                string state = string.Empty;
                if (count > 0)
                {
                    state = Master.ReadState(slaveAddr).ToString();
                }

                return (Count: count, State: state);
            });

            if (result.Count > 0)
            {
                AppendLog($"✓ Leal 主站已恢复 (从站数: {result.Count}, 状态: {result.State})");
                deviceAddViewModel.EthernetSlaveStateInfo = result.State;
            }
            else
            {
                AppendLog("⚠ Leal 主站恢复后未检测到从站，可能需要手动重连");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"⚠ Leal 主站恢复失败: {ex.Message}");
            AppendLog("请在设备管理页面手动断开并重新连接");
        }
    }

    /// <summary>
    /// 选择 ESI XML 文件
    /// </summary>
    [RelayCommand]
    private void OnSelectEsiFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 EtherCAT ESI XML 或 SII 二进制文件",
            Filter = "ESI/SII 文件 (*.xml;*.bin;*.sii)|*.xml;*.bin;*.sii|XML 文件 (*.xml)|*.xml|二进制文件 (*.bin;*.sii)|*.bin;*.sii|所有文件 (*.*)|*.*",
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
        {
            EsiFilePath = dialog.FileName;
            EsiFileName = Path.GetFileName(dialog.FileName);
            EsiFileSize = new FileInfo(dialog.FileName).Length;
            IsEsiFileSelected = true;

            AppendEsiLog($"已选择文件: {EsiFileName} ({EsiFileSize:N0} 字节)");
            EsiStatusText = $"已选择: {EsiFileName}";
        }
    }

    /// <summary>
    /// 清除已选择的 ESI 文件
    /// </summary>
    [RelayCommand]
    private void OnClearEsiFile()
    {
        EsiFilePath = string.Empty;
        EsiFileName = string.Empty;
        EsiFileSize = 0;
        IsEsiFileSelected = false;
        EsiStatusText = "已清除文件选择";
    }

    /// <summary>
    /// 将 ESI XML 写入从站 EEPROM (SII)。
    /// 流程: 暂停 Leal 主站 → 用独立 SOEM 上下文写入 EEPROM → 恢复 Leal 主站。
    /// </summary>
    [RelayCommand]
    private async Task OnBurnEsi()
    {
        if (!IsEsiFileSelected || string.IsNullOrEmpty(EsiFilePath))
        {
            EsiStatusText = "请先选择要烧录的 ESI XML 文件";
            return;
        }

        if (!deviceAddViewModel.IsEthernetConnected || Axis == null)
        {
            EsiStatusText = "设备未连接，无法烧录";
            return;
        }

        if (!SoemFoEInterop.IsFoEAvailable)
        {
            EsiStatusText = "soemfoe.dll 不可用";
            return;
        }

        IsEsiBusy = true;
        EsiProgressValue = 0;
        deviceAddViewModel.StopSlaveStatePolling();

        try
        {
            byte[] siiData;
            string ext = Path.GetExtension(EsiFilePath).ToLowerInvariant();

            if (ext == ".xml")
            {
                // ESI XML → SII 二进制转换
                AppendEsiLog("正在解析 ESI XML 并转换为 SII 二进制格式...");
                siiData = await Task.Run(() => EsiToSiiConverter.ConvertToSii(EsiFilePath));
                AppendEsiLog($"SII 数据生成完成: {siiData.Length:N0} 字节 ({siiData.Length / 2} words)");
            }
            else
            {
                // 直接使用 BIN/SII 二进制文件
                AppendEsiLog("正在读取 SII 二进制文件...");
                siiData = await Task.Run(() => File.ReadAllBytes(EsiFilePath));
                AppendEsiLog($"文件大小: {siiData.Length:N0} 字节");
            }

            int slaveAddr = Axis.SlaveAddr;
            string interfaceName = deviceAddViewModel.SelectedEthernetName;

            // 暂停 Leal 主站
            AppendEsiLog("暂停 Leal 主站以释放网卡...");
            await Task.Run(() =>
            {
                Master.WriteState(slaveAddr, SlaveState.Init);
                Master.StopActivity();
            });
            AppendEsiLog("Leal 主站已暂停");

            // 使用独立 SOEM 上下文写入 EEPROM
            EsiStatusText = "正在写入 EEPROM...";
            SoemFoEInterop.EepromResult result = await Task.Run(() =>
                SoemFoEInterop.ExecuteEepromWrite(
                    interfaceName, slaveAddr, siiData,
                    onLog: msg => System.Windows.Application.Current.Dispatcher.Invoke(
                        () => AppendEsiLog($"  [EEPROM] {msg}")),
                    onProgress: (written, total) => System.Windows.Application.Current.Dispatcher.Invoke(
                        () =>
                        {
                            EsiProgressMax = total;
                            EsiProgressValue = written;
                        })));

            if (result.Success)
            {
                EsiProgressValue = EsiProgressMax;
                AppendEsiLog($"✓ EEPROM 写入成功 ({result.WordsWritten} words)");
                EsiStatusText = "EEPROM 写入成功";
            }
            else
            {
                AppendEsiLog($"✗ EEPROM 写入失败: {result.ErrorMessage}");
                if (result.RolledBack)
                {
                    AppendEsiLog("ℹ 已自动回滚到烧录前的原始 EEPROM，从站应可继续使用");
                    EsiStatusText = "EEPROM 写入失败（已回滚）";
                }
                else
                {
                    AppendEsiLog("⚠ 未能回滚；如下次上电无法连接，请使用 TwinCAT 等工具通过 AP 重写有效 SII");
                    EsiStatusText = "EEPROM 写入失败";
                }
            }

            // 恢复 Leal 主站
            AppendEsiLog("正在恢复 Leal 主站连接...");
            EsiStatusText = "恢复设备连接...";
            await ReconnectLealMasterEsi(interfaceName, slaveAddr);

            AppData.AppLogViewModel.Log(
                result.Success ? AppLogLevel.Info : AppLogLevel.Warning,
                AppLogCategory.Firmware,
                result.Success ? "EEPROM 烧录完成" : "EEPROM 烧录失败",
                $"文件: {EsiFileName}, 大小: {siiData.Length:N0} 字节");
        }
        catch (Exception ex)
        {
            AppendEsiLog($"✗ 烧录异常: {ex.Message}");
            EsiStatusText = $"烧录失败: {ex.Message}";
            AppData.AppLogViewModel.Log(AppLogLevel.Error, AppLogCategory.Firmware,
                "EEPROM 烧录失败", ex.Message);
        }
        finally
        {
            IsEsiBusy = false;
            deviceAddViewModel.StartSlaveStatePolling();
        }
    }

    /// <summary>
    /// ESI EEPROM 操作完成后恢复 Leal 主站连接。
    /// </summary>
    private async Task ReconnectLealMasterEsi(string interfaceName, int slaveAddr)
    {
        try
        {
            (int Count, string State) result = await Task.Run(() =>
            {
                int count = Master.StartActivity(interfaceName);
                string state = string.Empty;
                if (count > 0)
                    state = Master.ReadState(slaveAddr).ToString();
                return (Count: count, State: state);
            });

            if (result.Count > 0)
            {
                AppendEsiLog($"✓ Leal 主站已恢复 (从站数: {result.Count}, 状态: {result.State})");
                deviceAddViewModel.EthernetSlaveStateInfo = result.State;
            }
            else
            {
                AppendEsiLog("⚠ Leal 主站恢复后未检测到从站，可能需要手动重连");
            }
        }
        catch (Exception ex)
        {
            AppendEsiLog($"⚠ Leal 主站恢复失败: {ex.Message}");
            AppendEsiLog("请在设备管理页面手动断开并重新连接");
        }
    }

    #endregion

    #region SDO 分段传输

    /// <summary>
    /// 通过 SDO 分段传输将固件数据写入从站
    /// 使用标准 ESI 更新对象 (0xF010 子索引序列)
    /// </summary>
    private bool WriteFirmwareViaSDO(int slaveAddr, byte[] data)
    {
        const int segmentSize = 480; // SDO 分段大小
        int totalSegments = (data.Length + segmentSize - 1) / segmentSize;

        for (int i = 0; i < totalSegments; i++)
        {
            int offset = i * segmentSize;
            int length = Math.Min(segmentSize, data.Length - offset);
            byte[] segment = new byte[length];
            Array.Copy(data, offset, segment, 0, length);

            try
            {
                // 使用 SDO 写入数据段
                // 对象 0xF010: ESI 数据存储
                int wkc = Master.WriteSDO(slaveAddr, 0xF010, i + 1, segment.Length, segment);
                if (wkc <= 0)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        AppendLog($"✗ 段 {i + 1}/{totalSegments} 写入失败 (wkc={wkc})");
                    });
                    return false;
                }
            }
            catch (Exception ex)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    AppendLog($"✗ 段 {i + 1}/{totalSegments} 异常: {ex.Message}");
                });
                return false;
            }

            // 更新进度
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ProgressValue = offset + length;
                AppendLog($"  段 {i + 1}/{totalSegments} 已写入 ({length} 字节)");
            });
        }

        return true;
    }

    #endregion

    #region 辅助

    private void AppendLog(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        BurnLog += $"[{timestamp}] {message}\n";
    }

    private void AppendEsiLog(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        EsiBurnLog += $"[{timestamp}] {message}\n";
    }

    private void UpdateConnectionState()
    {
        IsConnected = deviceAddViewModel.IsEthernetConnected && Axis != null;
        ConnectionInfo = IsConnected
            ? $"已连接: {deviceAddViewModel.EthernetSlaveNameInfo}"
            : "设备未连接";
        DeviceState = IsConnected
            ? deviceAddViewModel.EthernetSlaveStateInfo
            : string.Empty;
    }

    /// <summary>
    /// 调用 pack_ota.py 对原始 bin 文件进行 OTA 签名打包。
    /// 返回签名后的文件路径，失败返回 null。
    /// </summary>
    private string RunOtaPackTool(string originalBinPath)
    {
        // 使用输出目录下由 MSBuild 复制的 ota_pack_tool
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string toolDir = Path.Combine(appDir, "ota_pack_tool");

        string scriptPath = Path.Combine(toolDir, "pack_ota.py");
        if (!File.Exists(scriptPath))
        {
            System.Windows.Application.Current.Dispatcher.Invoke(
                () => AppendLog($"✗ 找不到 OTA 打包脚本: {scriptPath}"));
            return null!;
        }

        // 将原始 bin 复制到工具目录
        string srcFileName = Path.GetFileName(originalBinPath);
        string copiedBin = Path.Combine(toolDir, srcFileName);
        File.Copy(originalBinPath, copiedBin, overwrite: true);

        string outputName = "update_sign.bin";
        string outputPath = Path.Combine(toolDir, outputName);

        // 如果存在旧的输出文件先删除
        if (File.Exists(outputPath))
            File.Delete(outputPath);

        // 执行: python pack_ota.py 4 <input.bin> update_sign.bin
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{scriptPath}\" 4 \"{srcFileName}\" \"{outputName}\"",
            WorkingDirectory = toolDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(
                () => AppendLog("✗ 无法启动 Python 进程"));
            return null!;
        }

        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            System.Windows.Application.Current.Dispatcher.Invoke(
                () => AppendLog($"  [OTA] {stdout.TrimEnd()}"));
        }

        if (proc.ExitCode != 0)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                AppendLog($"✗ OTA 打包脚本返回错误码: {proc.ExitCode}");
                if (!string.IsNullOrWhiteSpace(stderr))
                    AppendLog($"  [OTA stderr] {stderr.TrimEnd()}");
            });
            return null!;
        }

        if (!File.Exists(outputPath))
        {
            System.Windows.Application.Current.Dispatcher.Invoke(
                () => AppendLog($"✗ OTA 打包后未生成输出文件: {outputPath}"));
            return null!;
        }

        return outputPath;
    }

    private static uint ParseFoePassword(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        text = text.Trim();

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
        {
            if (uint.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint hex))
                return hex;
        }

        if (uint.TryParse(text, out uint dec))
            return dec;

        return 0;
    }

    #endregion

    #region 生命周期

    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            _isInitialized = true;

            // Restore remembered firmware settings
            UserSettings settings = Services.UserSettingsService.Load();
            if (!string.IsNullOrEmpty(settings.FoeTargetFileName))
                FoeTargetFileName = settings.FoeTargetFileName;
            if (!string.IsNullOrEmpty(settings.FoePasswordText))
                FoePasswordText = settings.FoePasswordText;
            IsPreProcessedFirmware = settings.IsPreProcessedFirmware;
        }

        UpdateConnectionState();
    }

    #endregion
}