// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Windows;
using System.Collections.ObjectModel;
using Core.Modbus;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.Services;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels.Factory;

/// <summary>
/// 帧格式修改器 ViewModel。
/// 显示四个协议栈的发送/应答帧格式，允许修改 Modbus/CANopen/USB 帧格式。
/// 进入页面时暂停所有主站发送，离开时恢复。
/// </summary>
public partial class FrameFormatEditorViewModel : ViewModel
{
    private readonly DeviceAddViewModel _deviceAddViewModel;
    private bool _isInitialized;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedModbusSendFormat))]
    [NotifyPropertyChangedFor(nameof(SelectedModbusResponseFormat))]
    private ModbusFunctionFrameTemplate _selectedModbusTemplate = null!;

    [ObservableProperty] private CanopenFrameFormat _canopenSendFormat = new(FrameDirection.Send);
    [ObservableProperty] private CanopenFrameFormat _canopenResponseFormat = new(FrameDirection.Response);
    [ObservableProperty] private UsbFrameFormat _usbSendFormat = new(FrameDirection.Send);
    [ObservableProperty] private UsbFrameFormat _usbResponseFormat = new(FrameDirection.Response);
    [ObservableProperty] private EthercatFrameFormat _ethercatFormat = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFactoryUnlocked))]
    private bool _isFactoryLocked = true;

    public bool IsFactoryUnlocked => !IsFactoryLocked;

    [ObservableProperty] private bool _autoSaveEnabled = true;
    [ObservableProperty] private string _statusText = "就绪";

    public ObservableCollection<ModbusFunctionFrameTemplate> ModbusFunctionTemplates { get; } =
    [
        new(ModbusFunctionCode.ReadHoldingRegisters),
        new(ModbusFunctionCode.WriteSingleRegister),
        new(ModbusFunctionCode.WriteMultipleRegisters),
    ];

    public ModbusFrameFormat SelectedModbusSendFormat => SelectedModbusTemplate.SendFormat;

    public ModbusFrameFormat SelectedModbusResponseFormat => SelectedModbusTemplate.ResponseFormat;

    public ObservableCollection<FrameFieldGroup> ExampleFields { get; } =
    [
        new("地址", 1, "站号、节点号或逻辑地址"),
        new("功能码", 1, "命令、服务或功能选择"),
        new("命令字", 1, "请求/应答命令字"),
        new("索引", 2, "对象索引或寄存器地址"),
        new("子索引", 1, "对象子索引"),
        new("长度", 2, "有效数据长度"),
        new FrameFieldGroup("数据区", 1, "可变长度载荷") { IsVariableLength = true },
        new("状态码", 1, "应答状态或异常码"),
        new("序列号", 2, "请求与应答匹配编号"),
        new("CRC16", 2, "低字节在前的 CRC16"),
        new("CRC32", 4, "覆盖帧主体的 CRC32"),
        new("工作计数器", 2, "EtherCAT WKC"),
    ];

    private bool _wasCanOpenRunning;
    private bool _wasUsbRunning;

    public FrameFormatEditorViewModel(DeviceAddViewModel deviceAddViewModel)
    {
        _deviceAddViewModel = deviceAddViewModel;
        SelectedModbusTemplate = ModbusFunctionTemplates[0];
        FactoryAccessService.UnlockStateChanged += OnFactoryAccessChanged;
        UpdateFactoryLockState();
        ApplyRuntimeFormats();
    }

    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            _isInitialized = true;
            StatusText = "已加载默认帧格式";
        }
        PauseAllMasters();
    }

    public override void OnNavigatedFrom() => ResumeAllMasters();

    // ── 字段操作命令 ──────────────────────────────────────────────────────────

    /// <summary>在指定索引后插入一个新字节槽。</summary>
    [RelayCommand]
    private void InsertByte((FrameFormatBase Format, int Index) param)
    {
        if (IsFactoryLocked || param.Format.IsReadOnly) return;
        param.Format.InsertByteAt(param.Index + 1);
        StatusText = param.Format.IsPayloadLengthExceeded
            ? $"{param.Format.LengthLimitText}，新增字段已标记"
            : "已插入 1 个字节字段";
    }

    /// <summary>移除指定字段组。</summary>
    [RelayCommand]
    private void RemoveField((FrameFormatBase Format, FrameFieldGroup Field) param)
    {
        if (IsFactoryLocked || param.Format.IsReadOnly) return;
        int idx = param.Format.Fields.IndexOf(param.Field);
        if (idx >= 0)
        {
            param.Format.RemoveFieldAt(idx);
            StatusText = param.Format.IsPayloadLengthExceeded
                ? $"{param.Format.LengthLimitText}，请调整字段数量"
                : "已删除字段";
        }
    }

    /// <summary>切换多字节字段的展开/收起状态。</summary>
    [RelayCommand]
    private void ToggleExpand(FrameFieldGroup field)
    {
        if (field.IsExpanded) field.Collapse();
        else field.Expand();
    }

    /// <summary>互换字段的高低字节顺序。</summary>
    [RelayCommand]
    private void SwapBytes(FrameFieldGroup field)
    {
        if (IsFactoryLocked) return;
        field.SwapBytes();
    }

    public bool TryMoveField(
        FrameFormatBase sourceFormat,
        FrameFieldGroup field,
        FrameFormatBase targetFormat,
        int targetIndex)
    {
        if (IsFactoryLocked)
        {
            StatusText = "厂家权限已锁定，无法拖动字段";
            return false;
        }

        if (sourceFormat.IsReadOnly || targetFormat.IsReadOnly)
        {
            StatusText = "只读帧格式不支持重排";
            return false;
        }

        if (!ReferenceEquals(sourceFormat, targetFormat) && targetFormat.MaxPayloadByteCount.HasValue)
            field.IsUserAdded = true;

        bool moved = sourceFormat.MoveFieldTo(field, targetFormat, targetIndex);
        if (!moved) return false;

        StatusText = targetFormat.IsPayloadLengthExceeded
            ? $"{targetFormat.LengthLimitText}，拖入字段已标记"
            : "字段位置已更新";

        return true;
    }

    public bool TryCopyFieldTo(FrameFieldGroup templateField, FrameFormatBase targetFormat, int targetIndex)
    {
        if (IsFactoryLocked)
        {
            StatusText = "厂家权限已锁定，无法拖入内容例";
            return false;
        }

        if (targetFormat.IsReadOnly)
        {
            StatusText = "只读帧格式不支持拖入内容例";
            return false;
        }

        FrameFieldGroup clone = templateField.CloneForInsert();
        targetFormat.InsertFieldAt(targetIndex, clone);
        StatusText = targetFormat.IsPayloadLengthExceeded
            ? $"{targetFormat.LengthLimitText}，新增内容例已标记"
            : "已从内容例复制字段";

        return true;
    }

    public IEnumerable<FrameFormatBase> EnumerateFrameFormats()
    {
        foreach (var modbusTemplate in ModbusFunctionTemplates)
        {
            yield return modbusTemplate.SendFormat;
            yield return modbusTemplate.ResponseFormat;
        }

        yield return CanopenSendFormat;
        yield return CanopenResponseFormat;
        yield return UsbSendFormat;
        yield return UsbResponseFormat;
        yield return EthercatFormat;
    }

    // ── 保存 / 重置 ───────────────────────────────────────────────────────────

    [RelayCommand]
    private void SaveFrameFormats()
    {
        if (IsFactoryLocked) { StatusText = "厂家权限已锁定，无法保存"; return; }
        FrameFormatBase? exceededFormat = EnumerateFrameFormats().FirstOrDefault(static format => format.IsPayloadLengthExceeded);
        if (exceededFormat is not null)
        {
            StatusText = $"{exceededFormat.EditorTitle} {exceededFormat.PayloadLimitName}超过上限（{exceededFormat.PayloadByteCount}B），请调整后保存";
            return;
        }

        ApplyRuntimeFormats();
        StatusText = AutoSaveEnabled ? "帧格式已保存并应用（已自动记忆）" : "帧格式已保存并应用";
    }

    [RelayCommand]
    private void ResetToDefault()
    {
        if (IsFactoryLocked) { StatusText = "厂家权限已锁定，无法重置"; return; }
        ResetModbusTemplates();
        CanopenSendFormat = new CanopenFrameFormat(FrameDirection.Send);
        CanopenResponseFormat = new CanopenFrameFormat(FrameDirection.Response);
        UsbSendFormat = new UsbFrameFormat(FrameDirection.Send);
        UsbResponseFormat = new UsbFrameFormat(FrameDirection.Response);
        EthercatFormat = new EthercatFrameFormat();
        ApplyRuntimeFormats();
        StatusText = "已重置为默认帧格式";
    }

    private void ApplyRuntimeFormats()
    {
        FrameRuntimeFormat[] runtimeFormats =
        [
            ..ModbusFunctionTemplates.Select(template => template.SendFormat.ToRuntimeFormat(FrameProtocolStack.Modbus)),
            ..ModbusFunctionTemplates.Select(template => template.ResponseFormat.ToRuntimeFormat(FrameProtocolStack.Modbus)),
            CanopenSendFormat.ToRuntimeFormat(FrameProtocolStack.CANopen),
            CanopenResponseFormat.ToRuntimeFormat(FrameProtocolStack.CANopen),
            UsbSendFormat.ToRuntimeFormat(FrameProtocolStack.USB),
            UsbResponseFormat.ToRuntimeFormat(FrameProtocolStack.USB),
            EthercatFormat.ToRuntimeFormat(FrameProtocolStack.EtherCAT),
        ];

        FrameFormatRuntimeService.Apply(new FrameRuntimeProfile(runtimeFormats));
    }

    private void ResetModbusTemplates()
    {
        SelectedModbusTemplate = null!;
        ModbusFunctionTemplates.Clear();
        ModbusFunctionTemplates.Add(new ModbusFunctionFrameTemplate(ModbusFunctionCode.ReadHoldingRegisters));
        ModbusFunctionTemplates.Add(new ModbusFunctionFrameTemplate(ModbusFunctionCode.WriteSingleRegister));
        ModbusFunctionTemplates.Add(new ModbusFunctionFrameTemplate(ModbusFunctionCode.WriteMultipleRegisters));
        SelectedModbusTemplate = ModbusFunctionTemplates[0];
    }

    // ── 主站暂停 / 恢复 ───────────────────────────────────────────────────────

    private void PauseAllMasters()
    {
        try
        {
            var canOpenMaster = _deviceAddViewModel.CanOpenMaster;
            _wasCanOpenRunning = canOpenMaster?.IsRunning ?? false;
            if (_wasCanOpenRunning) canOpenMaster!.Stop();

            var usbMaster = _deviceAddViewModel.UsbMaster;
            _wasUsbRunning = usbMaster?.IsRunning ?? false;
            if (_wasUsbRunning) usbMaster!.Stop();

            StatusText = "已暂停所有协议栈主站发送";
        }
        catch (Exception ex) { StatusText = $"暂停主站时出错: {ex.Message}"; }
    }

    private void ResumeAllMasters()
    {
        try
        {
            if (_wasCanOpenRunning) _deviceAddViewModel.CanOpenMaster?.Start();
            if (_wasUsbRunning) _deviceAddViewModel.UsbMaster?.Start();
        }
        catch (Exception ex) { StatusText = $"恢复主站时出错: {ex.Message}"; }
    }

    private void OnFactoryAccessChanged(object? sender, EventArgs e)
        => Application.Current.Dispatcher.Invoke(UpdateFactoryLockState);

    private void UpdateFactoryLockState() => IsFactoryLocked = !FactoryAccessService.IsUnlocked;
}
