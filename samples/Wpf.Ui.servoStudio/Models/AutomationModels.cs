// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Core.Usb;

namespace Wpf.Ui.servoStudio.Models;

/// <summary>
/// 厂家自动化测试项的结果状态。
/// </summary>
public enum TestResultState
{
    /// <summary>未测：尚未执行。</summary>
    NotRun,

    /// <summary>进行中：命令已下发，等待回传。</summary>
    Running,

    /// <summary>合格：从机回传通过。</summary>
    Pass,

    /// <summary>不合格（故障）：从机回传失败，可结合故障级别定位。</summary>
    Fail,

    /// <summary>超时：在限定时间内未收到从机回传。</summary>
    Timeout,

    /// <summary>不可用：该测试项在当前硬件 / 固件上不支持，或 USB 未连接。</summary>
    Unavailable,
}

/// <summary>
/// 厂家自动化测试分组。
/// </summary>
public enum TestCategory
{
    /// <summary>板级测试（出厂前裸板自检）。</summary>
    Board,

    /// <summary>外设测试（编码器 / IO / ADC / 收发器等）。</summary>
    Peripheral,

    /// <summary>功能测试（电流环 / 保护逻辑 / 输出等）。</summary>
    Function,
}

/// <summary>
/// 单个自动化测试项的可绑定模型：携带测试元数据与执行结果状态。
/// </summary>
public partial class AutomationTestItem : ObservableObject
{
    /// <summary>当前结果状态。</summary>
    [ObservableProperty]
    private TestResultState _state = TestResultState.NotRun;

    /// <summary>结果详情文本（故障原因 / 回传备注 / 超时说明等）。</summary>
    [ObservableProperty]
    private string _detail = string.Empty;

    /// <summary>故障级别 / 阶段码（0 表示无；非 0 指示哪一级失败）。</summary>
    [ObservableProperty]
    private int _faultStage;

    /// <summary>最近一次执行耗时（毫秒）。</summary>
    [ObservableProperty]
    private double _durationMs;

    /// <summary>该项是否正在执行（用于按钮禁用 / 进度指示）。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>测试项显示名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>测试项说明。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>所属分组。</summary>
    public TestCategory Category { get; init; }

    /// <summary>对应的 USB 帧测试命令编号。</summary>
    public FactoryTestId TestId { get; init; }

    /// <summary>单项响应超时（毫秒）。</summary>
    public int TimeoutMs { get; init; } = 2000;

    /// <summary>分组的中文名称，便于 UI 分组标题展示。</summary>
    public string CategoryName => Category switch
    {
        TestCategory.Board => "板级测试",
        TestCategory.Peripheral => "外设测试",
        TestCategory.Function => "功能测试",
        _ => "其他",
    };

    /// <summary>状态的中文短文本，供 UI 直接绑定。</summary>
    public string StateText => State switch
    {
        TestResultState.NotRun => "未测",
        TestResultState.Running => "进行中",
        TestResultState.Pass => "合格",
        TestResultState.Fail => "不合格",
        TestResultState.Timeout => "超时",
        TestResultState.Unavailable => "不可用",
        _ => "未知",
    };

    partial void OnStateChanged(TestResultState value)
    {
        OnPropertyChanged(nameof(StateText));
    }
}
