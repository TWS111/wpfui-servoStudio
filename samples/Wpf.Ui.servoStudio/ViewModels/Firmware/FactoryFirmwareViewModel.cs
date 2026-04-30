// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using System.IO;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using Wpf.Ui.servoStudio.Services;

namespace Wpf.Ui.servoStudio.ViewModels.Firmware;

public partial class FactoryFirmwareViewModel : ViewModel
{
    private bool _isInitialized;

    /// <summary>
    /// 是否已订阅系统级网络变化事件。仅在页面驻留期间订阅，离开页面立即解除以避免泄漏。
    /// </summary>
    private bool _isMonitoringNetwork;

    [ObservableProperty]
    private bool _isCompanyNetworkAvailable;

    [ObservableProperty]
    private string _databaseRootPath = FirmwareRepositoryService.DatabaseRoot;

    [ObservableProperty]
    private string _statusText = "就绪";

    // 已选择的本地待上传文件
    [ObservableProperty]
    private string _selectedFilePath = string.Empty;

    [ObservableProperty]
    private string _selectedFileName = string.Empty;

    [ObservableProperty]
    private long _selectedFileSize;

    [ObservableProperty]
    private bool _isFileSelected;

    // 元数据
    [ObservableProperty]
    private string _versionInput = string.Empty;

    [ObservableProperty]
    private string _nameInput = string.Empty;

    [ObservableProperty]
    private string _hardwareVersionInput = string.Empty;

    [ObservableProperty]
    private string _descriptionInput = string.Empty;

    /// <summary>
    /// 上传时选择的固件状态。
    /// </summary>
    [ObservableProperty]
    private string _selectedStatus = FirmwareRepositoryService.DefaultStatus;

    public IReadOnlyList<string> StatusOptions { get; } = FirmwareRepositoryService.StatusOptions;

    /// <summary>
    /// 上传时是否先在本地运行 pack_ota.py 签名后再入库。
    /// 与 <see cref="MarkExternallySignedOnUpload"/> 互斥。
    /// </summary>
    [ObservableProperty]
    private bool _signFirstBeforeUpload;

    /// <summary>
    /// 上传时是否标记为"外部已签名"（原始文件本身即为签名产物）。
    /// 与 <see cref="SignFirstBeforeUpload"/> 互斥。
    /// </summary>
    [ObservableProperty]
    private bool _markExternallySignedOnUpload;

    /// <summary>
    /// 上传时选择的产品类型（A/B/C）。
    /// </summary>
    [ObservableProperty]
    private string _selectedProductType = FirmwareRepositoryService.DefaultProductType;

    public IReadOnlyList<string> ProductTypeOptions { get; } = FirmwareRepositoryService.ProductTypeOptions;

    // 上传状态
    [ObservableProperty]
    private bool _isUploading;

    // 上传/删除操作密码（二者使用同一个输入框，保证一致）
    [ObservableProperty]
    private string _operationPassword = string.Empty;

    /// <summary>
    /// 密码是否已通过"确认"验证。只有验证通过后才能执行上传/删除。
    /// 密码输入发生变化或上传/删除成功后会被重置为 false。
    /// </summary>
    [ObservableProperty]
    private bool _isPasswordConfirmed;

    [ObservableProperty]
    private ObservableCollection<FirmwareEntry> _entries = new();

    /// <summary>
    /// 是否正在加载数据库（检测网络 / 读取索引）。
    /// UI 上表现为蒙版 + 进度环，避免被使用者误以为页面卡死。
    /// </summary>
    [ObservableProperty]
    private bool _isLoading;

    public override async Task OnNavigatedToAsync()
    {
        if (!_isInitialized)
        {
            _isInitialized = true;
        }

        StartNetworkMonitoring();
        await RefreshNetworkStatusAsync();
    }

    /// <summary>
    /// 离开页面时重置密码验证状态，下次进入需重新验证；同时解除网络变化事件订阅。
    /// </summary>
    public override void OnNavigatedFrom()
    {
        StopNetworkMonitoring();
        OperationPassword = string.Empty;
        IsPasswordConfirmed = false;
    }

    /// <summary>
    /// 订阅系统网络变化事件，使页面在公司网络断开 / 切换时能及时刷新状态，
    /// 与设置页中"厂家密码受信任网络锁定/解锁"的实现思路一致
    /// （参见 <see cref="FactoryAccessService.StartNetworkMonitoring"/>）。
    /// </summary>
    private void StartNetworkMonitoring()
    {
        if (_isMonitoringNetwork)
        {
            return;
        }

        _isMonitoringNetwork = true;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    private void StopNetworkMonitoring()
    {
        if (!_isMonitoringNetwork)
        {
            return;
        }

        _isMonitoringNetwork = false;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
    }

    /// <summary>
    /// 网络整体可用性变化（如网线拔出、所有网络断开）时触发。
    /// 立刻把页面状态置为"未连接"，并异步重新检测以反映真实状态（如果还能恢复）。
    /// </summary>
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable)
        {
            // 立即同步置为不可用，避免界面继续显示陈旧的"可用"状态
            DispatchToUi(() =>
            {
                IsCompanyNetworkAvailable = false;
                Entries.Clear();
                StatusText = "网络已断开，无法访问厂家固件数据库。";
            });
        }

        // 不论变为可用或不可用，都做一次完整的 SMB 共享检测，更新到真实状态
        DispatchToUi(async () => await RefreshNetworkStatusAsync().ConfigureAwait(false));
    }

    /// <summary>
    /// 网络地址变化（如切换 Wi-Fi、DHCP 重新分配、接入不同路由器）时触发。
    /// 等待短暂延迟以让系统完成网络配置，再重新检测公司共享是否可达。
    /// </summary>
    private async void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        // 等待系统完成网络配置（与 FactoryAccessService 中保持一致）
        await Task.Delay(1500).ConfigureAwait(false);

        DispatchToUi(async () => await RefreshNetworkStatusAsync().ConfigureAwait(false));
    }

    /// <summary>
    /// 把动作切回 UI 线程执行。NetworkChange 事件回调发生在线程池线程，
    /// 而 <see cref="Entries"/> 等可观察集合的更改必须在 UI 线程进行。
    /// </summary>
    private static void DispatchToUi(Action action)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is null || disp.CheckAccess())
        {
            action();
        }
        else
        {
            _ = disp.InvokeAsync(action);
        }
    }

    [RelayCommand]
    private Task OnRefreshNetwork() => RefreshNetworkStatusAsync();

    /// <summary>
    /// 异步检测公司网络连接状态并加载条目。
    /// <see cref="FirmwareRepositoryService.IsCompanyNetworkAvailable"/> 与 <see cref="FirmwareRepositoryService.LoadAll"/>
    /// 均涉及 SMB 共享访问，不可走在 UI 线程，否则网络超时会造成页面卡死。
    /// </summary>
    private async Task RefreshNetworkStatusAsync()
    {
        if (IsLoading)
        {
            return;
        }

        // 无条件先清掉上一次的"已连接"缓存状态，避免在重新检测期间界面仍显示旧的"已连接公司网络"。
        // 这一步必须在任何 await 之前同步完成，确保下一帧 UI 立即反映"正在检测"。
        IsCompanyNetworkAvailable = false;
        Entries.Clear();

        IsLoading = true;
        StatusText = "正在检测公司网络与加载数据库...";

        try
        {
            Task<(bool available, List<FirmwareEntry> entries)> probeTask = Task.Run(static () =>
            {
                bool ok = FirmwareRepositoryService.IsCompanyNetworkAvailable();
                List<FirmwareEntry> list = ok
                    ? FirmwareRepositoryService.LoadAll()
                                               .OrderByDescending(x => x.UploadedAt)
                                               .ToList()
                    : new List<FirmwareEntry>();
                return (ok, list);
            });

            // SMB 探测整体超时兜底（IsCompanyNetworkAvailable 内部已对 TCP 连接设了 1s 超时；
            // 这里再加一层 6s 整体超时覆盖 LoadAll 阶段，避免极端情况下 UI 长时间停在加载蒙版）。
            Task winner = await Task.WhenAny(probeTask, Task.Delay(TimeSpan.FromSeconds(6)));

            (bool available, List<FirmwareEntry> entries) result =
                winner == probeTask ? await probeTask : (false, new List<FirmwareEntry>());

            IsCompanyNetworkAvailable = result.available;
            Entries.Clear();
            if (result.available)
            {
                foreach (FirmwareEntry e in result.entries)
                {
                    Entries.Add(e);
                }

                StatusText = $"已连接公司网络 · 数据库: {DatabaseRootPath}";
            }
            else
            {
                StatusText = "未检测到公司网络，无法访问厂家固件数据库。";
            }
        }
        catch (Exception ex)
        {
            IsCompanyNetworkAvailable = false;
            Entries.Clear();
            StatusText = $"加载数据库失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 重载条目（仅用于上传/删除/签名/状态修改后的刷新。在已连接状态下调用）。
    /// 同样放到后台线程避免读取共享时阻塞 UI。
    /// </summary>
    private async Task ReloadEntriesAsync()
    {
        if (!IsCompanyNetworkAvailable)
        {
            return;
        }

        IsLoading = true;
        try
        {
            List<FirmwareEntry> list = await Task.Run(static () =>
                FirmwareRepositoryService.LoadAll()
                                         .OrderByDescending(x => x.UploadedAt)
                                         .ToList());

            Entries.Clear();
            foreach (FirmwareEntry e in list)
            {
                Entries.Add(e);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OnBrowseFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要上传的固件文件",
            Filter = "固件文件 (*.bin;*.hex;*.efw;*.fw)|*.bin;*.hex;*.efw;*.fw|所有文件 (*.*)|*.*",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SelectedFilePath = dialog.FileName;
        SelectedFileName = Path.GetFileName(dialog.FileName);
        SelectedFileSize = new FileInfo(dialog.FileName).Length;
        IsFileSelected = true;

        if (string.IsNullOrWhiteSpace(NameInput))
        {
            NameInput = Path.GetFileNameWithoutExtension(dialog.FileName);
        }

        StatusText = $"已选择: {SelectedFileName} ({SelectedFileSize:N0} 字节)";
    }

    [RelayCommand]
    private void OnClearFile()
    {
        SelectedFilePath = string.Empty;
        SelectedFileName = string.Empty;
        SelectedFileSize = 0;
        IsFileSelected = false;
        StatusText = "已清除文件选择。";
    }

    private bool CanUpload() =>
        IsCompanyNetworkAvailable
        && IsFileSelected
        && !IsUploading
        && IsPasswordConfirmed
        && !string.IsNullOrWhiteSpace(VersionInput)
        && !string.IsNullOrWhiteSpace(NameInput);

    private bool CanConfirmPassword() => !string.IsNullOrEmpty(OperationPassword) && !IsPasswordConfirmed;

    [RelayCommand(CanExecute = nameof(CanConfirmPassword))]
    private void OnConfirmPassword()
    {
        if (FirmwareRepositoryService.VerifyPassword(OperationPassword))
        {
            IsPasswordConfirmed = true;
            StatusText = "密码验证通过，现可执行上传/删除。";
        }
        else
        {
            IsPasswordConfirmed = false;
            OperationPassword = string.Empty;
            ShowPasswordError();
        }
    }

    [RelayCommand]
    private void OnResetPassword()
    {
        OperationPassword = string.Empty;
        IsPasswordConfirmed = false;
        StatusText = "密码验证状态已重置。";
    }

    [RelayCommand(CanExecute = nameof(CanUpload))]
    private async Task OnUpload()
    {
        if (!CanUpload())
        {
            return;
        }

        IsUploading = true;
        StatusText = "正在上传...";
        UploadCommand.NotifyCanExecuteChanged();

        try
        {
            FirmwareEntry? entry = await FirmwareRepositoryService.UploadAsync(
                SelectedFilePath,
                VersionInput,
                NameInput,
                HardwareVersionInput,
                DescriptionInput,
                SelectedStatus,
                SelectedProductType,
                signFirst: SignFirstBeforeUpload,
                markExternallySigned: MarkExternallySignedOnUpload);

            if (entry is not null)
            {
                StatusText = $"上传成功 · {entry.OriginalFileName} ({entry.FileSizeBytes:N0} 字节)";
                OnClearFile();
                VersionInput = string.Empty;
                NameInput = string.Empty;
                HardwareVersionInput = string.Empty;
                DescriptionInput = string.Empty;
                SelectedStatus = FirmwareRepositoryService.DefaultStatus;
                SelectedProductType = FirmwareRepositoryService.DefaultProductType;
                SignFirstBeforeUpload = false;
                MarkExternallySignedOnUpload = false;
                // 密码验证状态在本页保持，直到离开页面才重新锁定
                await ReloadEntriesAsync();
            }
            else
            {
                StatusText = "上传失败：未知错误。";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"上传失败: {ex.Message}";
        }
        finally
        {
            IsUploading = false;
            UploadCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanDelete(FirmwareEntry? entry) =>
        IsCompanyNetworkAvailable && !IsUploading && IsPasswordConfirmed && entry is not null;

    private bool CanEditStatus(FirmwareEntry? entry) =>
        IsCompanyNetworkAvailable && !IsUploading && IsPasswordConfirmed
        && entry is not null && entry.IsRegistered && entry.IsSigned;

    private bool CanSign(FirmwareEntry? entry) =>
        IsCompanyNetworkAvailable && !IsUploading && IsPasswordConfirmed
        && entry is not null && entry.IsRegistered && !entry.IsSigned;

    private bool CanToggleExternalSigned(FirmwareEntry? entry) =>
        IsCompanyNetworkAvailable && !IsUploading && IsPasswordConfirmed
        && entry is not null && entry.IsRegistered
        && string.IsNullOrEmpty(entry.SignedStoredFileName); // 脚本签名过的不允许切换

    /// <summary>
    /// 烧录按键可用条件：条目已注册且已签名（即"仅签名"及以上状态），
    /// 不要求密码已验证（烧录不修改数据库）；公司网络断开时自然不可用（条目集合会被清空）。
    /// </summary>
    private bool CanBurn(FirmwareEntry? entry) =>
        IsCompanyNetworkAvailable && entry is not null
        && entry.IsRegistered && entry.IsSigned;

    /// <summary>
    /// 跳转到"FoE 固件烧录"页并自动加载该条目对应的已签名固件。
    /// 仅 .bin 文件支持烧录；非 .bin 弹窗提示并中止。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanBurn))]
    private async Task OnBurn(FirmwareEntry? entry)
    {
        if (entry is null || !entry.IsRegistered || !entry.IsSigned)
        {
            return;
        }

        string filePath = FirmwareRepositoryService.GetEntryFilePath(entry);

        if (!System.IO.File.Exists(filePath))
        {
            _ = await new Wpf.Ui.Controls.MessageBox
            {
                Title = "无法烧录",
                Content = $"未在数据库中找到该条目对应的实际文件：\r\n{filePath}",
                CloseButtonText = "确定",
                CloseButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary,
                Owner = Application.Current?.MainWindow,
            }.ShowDialogAsync();
            return;
        }

        // 仅支持 .bin 文件烧录
        if (!string.Equals(System.IO.Path.GetExtension(filePath), ".bin", StringComparison.OrdinalIgnoreCase))
        {
            _ = await new Wpf.Ui.Controls.MessageBox
            {
                Title = "不支持的文件类型",
                Content = $"FoE 固件烧录仅支持 .bin 文件，当前条目实际文件为：\r\n{System.IO.Path.GetFileName(filePath)}",
                CloseButtonText = "确定",
                CloseButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary,
                Owner = Application.Current?.MainWindow,
            }.ShowDialogAsync();
            return;
        }

        // 通过 DI 取出 FirmwareProgramViewModel 单例并预填文件信息（厂家库中的固件均为已签名状态，
        // 因此自动勾选"已签名固件 (跳过 OTA 打包)"避免再次走 pack_ota.py 流程）。
        if (App.Services.GetService(typeof(FirmwareProgramViewModel))
            is not FirmwareProgramViewModel programVm)
        {
            StatusText = "无法获取 FoE 固件烧录 ViewModel，跳转失败。";
            return;
        }

        try
        {
            var fi = new System.IO.FileInfo(filePath);
            programVm.SelectedFilePath = filePath;
            programVm.SelectedFileName = fi.Name;
            programVm.SelectedFileSize = fi.Length;
            programVm.IsFileSelected = true;
            programVm.IsPreProcessedFirmware = true;
            programVm.StatusText = $"已从厂家库载入: {fi.Name}";
        }
        catch (Exception ex)
        {
            StatusText = $"载入文件失败: {ex.Message}";
            return;
        }

        if (App.Services.GetService(typeof(Wpf.Ui.INavigationService))
            is Wpf.Ui.INavigationService navigation)
        {
            _ = navigation.Navigate(typeof(Views.Pages.FirmwarePages.FirmwareProgramPage));
        }
        else
        {
            StatusText = "导航服务不可用，无法跳转到固件烧录页。";
        }
    }

    /// <summary>
    /// 对已上传条目仅修改状态。使用嵌入 ComboBox 的对话框选择新状态。
    /// 未签名条目的状态被强制为"未签名"，不允许修改；已签名条目不允许选择"未签名"。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditStatus))]
    private async Task OnEditStatus(FirmwareEntry? entry)
    {
        if (entry is null || !IsCompanyNetworkAvailable || !IsPasswordConfirmed || !entry.IsRegistered || !entry.IsSigned)
        {
            return;
        }

        // 已签名条目可选项中去掉"未签名"
        var availableOptions = StatusOptions
            .Where(s => !string.Equals(s, FirmwareRepositoryService.DefaultStatus, StringComparison.Ordinal))
            .ToList();

        var combo = new System.Windows.Controls.ComboBox
        {
            ItemsSource = availableOptions,
            SelectedItem = availableOptions.Contains(entry.Status) ? entry.Status : FirmwareRepositoryService.SignedOnlyStatus,
            MinWidth = 220,
            Margin = new System.Windows.Thickness(0, 8, 0, 0),
        };
        var hint = new System.Windows.Controls.TextBlock
        {
            Text = $"修改\"{entry.Name}\" (v{entry.Version}) 的状态：\r\n当前状态: {entry.Status}",
            TextWrapping = System.Windows.TextWrapping.Wrap,
        };
        var panel = new System.Windows.Controls.StackPanel { MinWidth = 280 };
        panel.Children.Add(hint);
        panel.Children.Add(combo);

        var dlg = new Wpf.Ui.Controls.MessageBox
        {
            Title = "修改状态",
            Content = panel,
            PrimaryButtonText = "保存",
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            CloseButtonText = "取消",
            Owner = Application.Current?.MainWindow,
        };

        Wpf.Ui.Controls.MessageBoxResult res = await dlg.ShowDialogAsync();
        if (res != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            return;
        }

        string newStatus = combo.SelectedItem as string ?? FirmwareRepositoryService.DefaultStatus;
        if (string.Equals(newStatus, entry.Status, StringComparison.Ordinal))
        {
            StatusText = "状态未发生变化。";
            return;
        }

        try
        {
            bool ok = await FirmwareRepositoryService.UpdateStatusAsync(entry.Id, newStatus);
            if (ok)
            {
                StatusText = $"已更新状态 · {entry.Name}: {entry.Status} → {newStatus}";
                await ReloadEntriesAsync();
            }
            else
            {
                StatusText = "状态更新失败：未找到对应条目。";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"状态更新失败: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task OnDelete(FirmwareEntry? entry)
    {
        if (entry is null || !IsCompanyNetworkAvailable || !IsPasswordConfirmed)
        {
            return;
        }

        // 二次确认
        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Title = "确认删除",
            Content = $"确定要从数据库中删除此条目吗？\r\n\r\n名称: {entry.Name}\r\n版本: {entry.Version}\r\n文件: {entry.OriginalFileName}\r\n\r\n该操作不可撤销。",
            PrimaryButtonText = "删除",
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger,
            CloseButtonText = "取消",
            Owner = Application.Current?.MainWindow,
        };

        Wpf.Ui.Controls.MessageBoxResult result = await confirm.ShowDialogAsync();
        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            return;
        }

        try
        {
            StatusText = "正在删除...";
            bool ok = await FirmwareRepositoryService.DeleteAsync(entry.Id);
            if (ok)
            {
                StatusText = $"已删除 · {entry.OriginalFileName}";
                // 密码验证状态在本页保持，直到离开页面才重新锁定
                await ReloadEntriesAsync();
            }
            else
            {
                StatusText = "删除失败：未找到对应条目。";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"删除失败: {ex.Message}";
        }
    }

    private static void ShowPasswordError()
    {
        _ = new Wpf.Ui.Controls.MessageBox
        {
            Title = "密码错误",
            Content = "操作密码不正确，请重试。",
            CloseButtonText = "确定",
            CloseButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            Owner = Application.Current?.MainWindow,
        }.ShowDialogAsync();
    }

    /// <summary>
    /// 对已入库且未签名的条目运行 pack_ota.py 签名。原始与签名后产物同时存于数据库。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSign))]
    private async Task OnSign(FirmwareEntry? entry)
    {
        if (entry is null || !IsCompanyNetworkAvailable || !IsPasswordConfirmed || !entry.IsRegistered || entry.IsSigned)
        {
            return;
        }

        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Title = "确认签名",
            Content = $"将对以下条目运行本地 pack_ota.py 进行签名：\r\n\r\n名称: {entry.Name}\r\n版本: {entry.Version}\r\n原文件: {entry.OriginalFileName}\r\n校验类型: SHA256\r\n\r\n签名产物会与原始文件同时保存在数据库中，状态会迁转为\"{FirmwareRepositoryService.SignedOnlyStatus}\"。",
            PrimaryButtonText = "签名",
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            CloseButtonText = "取消",
            Owner = Application.Current?.MainWindow,
        };

        if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            return;
        }

        try
        {
            IsUploading = true;
            StatusText = $"正在签名 {entry.OriginalFileName}...";
            bool ok = await FirmwareRepositoryService.SignAsync(entry.Id);
            StatusText = ok
                ? $"签名成功 · {entry.OriginalFileName}"
                : "签名失败：未找到对应条目。";
            await ReloadEntriesAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"签名失败: {ex.Message}";
        }
        finally
        {
            IsUploading = false;
        }
    }

    /// <summary>
    /// 切换"外部已签名"标记。仅适用于未由脚本签名的条目。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleExternalSigned))]
    private async Task OnToggleExternalSigned(FirmwareEntry? entry)
    {
        if (entry is null || !IsCompanyNetworkAvailable || !IsPasswordConfirmed || !entry.IsRegistered)
        {
            return;
        }

        if (!string.IsNullOrEmpty(entry.SignedStoredFileName))
        {
            StatusText = "该条目已通过脚本签名，无法修改\"外部签名\"标记。";
            return;
        }

        bool target = !entry.IsExternallySigned;
        string actionText = target ? "标记为外部已签名" : "取消外部已签名标记";

        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Title = $"确认{actionText}",
            Content = target
                ? $"将该条目标记为\"外部已签名\"：\r\n\r\n名称: {entry.Name}\r\n原文件: {entry.OriginalFileName}\r\n\r\n标记后，原始文件将被视为已签名产物，状态可进一步修改。\r\n请确认该文件确实在外部经过了等价的签名处理。"
                : $"将取消该条目的\"外部已签名\"标记：\r\n\r\n名称: {entry.Name}\r\n原文件: {entry.OriginalFileName}\r\n\r\n取消后状态会被重置为\"未签名\"。",
            PrimaryButtonText = target ? "标记" : "取消标记",
            PrimaryButtonAppearance = target ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Caution,
            CloseButtonText = "取消",
            Owner = Application.Current?.MainWindow,
        };

        if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            return;
        }

        try
        {
            bool ok = await FirmwareRepositoryService.MarkExternallySignedAsync(entry.Id, target);
            StatusText = ok
                ? $"{actionText}成功 · {entry.OriginalFileName}"
                : "操作失败：未找到对应条目。";
            await ReloadEntriesAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"{actionText}失败: {ex.Message}";
        }
    }

    partial void OnIsCompanyNetworkAvailableChanged(bool value)
    {
        UploadCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        EditStatusCommand.NotifyCanExecuteChanged();
        SignCommand.NotifyCanExecuteChanged();
        ToggleExternalSignedCommand.NotifyCanExecuteChanged();
        BurnCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsFileSelectedChanged(bool value) => UploadCommand.NotifyCanExecuteChanged();

    partial void OnIsUploadingChanged(bool value)
    {
        UploadCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        EditStatusCommand.NotifyCanExecuteChanged();
        SignCommand.NotifyCanExecuteChanged();
        ToggleExternalSignedCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPasswordConfirmedChanged(bool value)
    {
        UploadCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        EditStatusCommand.NotifyCanExecuteChanged();
        SignCommand.NotifyCanExecuteChanged();
        ToggleExternalSignedCommand.NotifyCanExecuteChanged();
        ConfirmPasswordCommand.NotifyCanExecuteChanged();
    }

    partial void OnOperationPasswordChanged(string value)
    {
        // 密码输入变化后，原有"已验证"状态应作废
        if (IsPasswordConfirmed)
        {
            IsPasswordConfirmed = false;
        }

        ConfirmPasswordCommand.NotifyCanExecuteChanged();
    }

    partial void OnVersionInputChanged(string value) => UploadCommand.NotifyCanExecuteChanged();
    partial void OnNameInputChanged(string value) => UploadCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// 上传选项互斥：不能同时勾选"先签名再上传"与"已外部签名"。
    /// </summary>
    partial void OnSignFirstBeforeUploadChanged(bool value)
    {
        if (value && MarkExternallySignedOnUpload)
        {
            MarkExternallySignedOnUpload = false;
        }
    }

    partial void OnMarkExternallySignedOnUploadChanged(bool value)
    {
        if (value && SignFirstBeforeUpload)
        {
            SignFirstBeforeUpload = false;
        }
    }
}
