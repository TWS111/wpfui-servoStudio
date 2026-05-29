// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Core.Usb;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels.Factory;

/// <summary>
/// 厂家自动化流程页 ViewModel。<br/>
/// 通过 USB 协议栈（<see cref="UsbChannel.FactoryTest"/> 通道）下发出厂前测试命令，
/// 等待从机回传结果，据此判定每个测试项为合格 / 不合格（故障）/ 超时 / 不可用。<br/>
/// 支持一键全流程串行执行，也支持单项手动执行。
/// </summary>
public partial class AutomationProcessViewModel : ViewModel
{
    private readonly DeviceAddViewModel _deviceAdd;
    private bool _isInitialized;

    private UsbMaster? _subscribedUsbMaster;

    /// <summary>等待回传的请求：序号 → 完成源。</summary>
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<FactoryTestResponse>> _pending = new();

    private ushort _sequence;
    private CancellationTokenSource? _runAllCts;

    public AutomationProcessViewModel(DeviceAddViewModel deviceAdd)
    {
        _deviceAdd = deviceAdd;
        BuildTestItems();
    }

    /// <summary>原始 DeviceAdd VM，便于 XAML 绑定连接概览。</summary>
    public DeviceAddViewModel DeviceAdd => _deviceAdd;

    /// <summary>全部测试项（按分组顺序排列）。</summary>
    public ObservableCollection<AutomationTestItem> Tests { get; } = new();

    /// <summary>板级测试项。</summary>
    public ObservableCollection<AutomationTestItem> BoardTests { get; } = new();

    /// <summary>外设测试项。</summary>
    public ObservableCollection<AutomationTestItem> PeripheralTests { get; } = new();

    /// <summary>功能测试项。</summary>
    public ObservableCollection<AutomationTestItem> FunctionTests { get; } = new();

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private bool _isRunningAll;

    [ObservableProperty]
    private int _passCount;

    [ObservableProperty]
    private int _failCount;

    [ObservableProperty]
    private int _timeoutCount;

    [ObservableProperty]
    private int _unavailableCount;

    /// <summary>测试项总数。</summary>
    public int TotalCount => Tests.Count;

    /// <summary>当前是否可执行测试：USB 已连接且未在全流程运行中。</summary>
    public bool CanRun => _deviceAdd.IsUsbConnected && !IsRunningAll;

    /// <summary>未在全流程运行中（用于配置控件 IsEnabled）。</summary>
    public bool IsNotRunningAll => !IsRunningAll;

    private void BuildTestItems()
    {
        Tests.Clear();
        BoardTests.Clear();
        PeripheralTests.Clear();
        FunctionTests.Clear();

        AddItem(new AutomationTestItem
        {
            Name = "供电电压检测",
            Description = "检测各路供电轨电压是否在允许范围内",
            Category = TestCategory.Board,
            TestId = FactoryTestId.BoardPowerRail,
        });

        AddItem(new AutomationTestItem
        {
            Name = "主控时钟检测",
            Description = "检测主控时钟 / 晶振起振与频率",
            Category = TestCategory.Board,
            TestId = FactoryTestId.BoardClock,
        });

        AddItem(new AutomationTestItem
        {
            Name = "Flash 自检",
            Description = "Flash 读写校验",
            Category = TestCategory.Board,
            TestId = FactoryTestId.BoardFlash,
        });

        AddItem(new AutomationTestItem
        {
            Name = "RAM 自检",
            Description = "RAM 读写校验",
            Category = TestCategory.Board,
            TestId = FactoryTestId.BoardRam,
        });

        AddItem(new AutomationTestItem
        {
            Name = "编码器接口",
            Description = "编码器供电 / 通信 / 计数检测",
            Category = TestCategory.Peripheral,
            TestId = FactoryTestId.PeripheralEncoder,
        });

        AddItem(new AutomationTestItem
        {
            Name = "数字 IO 回环",
            Description = "数字输入 / 输出回环检测",
            Category = TestCategory.Peripheral,
            TestId = FactoryTestId.PeripheralDigitalIo,
        });

        AddItem(new AutomationTestItem
        {
            Name = "模拟量采样",
            Description = "ADC 通道采样与基准检测",
            Category = TestCategory.Peripheral,
            TestId = FactoryTestId.PeripheralAdc,
        });

        AddItem(new AutomationTestItem
        {
            Name = "通信收发器",
            Description = "CAN / 485 收发器自环检测",
            Category = TestCategory.Peripheral,
            TestId = FactoryTestId.PeripheralTransceiver,
        });

        AddItem(new AutomationTestItem
        {
            Name = "电流环自检",
            Description = "电流采样与电流环闭环自检",
            Category = TestCategory.Function,
            TestId = FactoryTestId.FunctionCurrentLoop,
        });

        AddItem(new AutomationTestItem
        {
            Name = "保护逻辑",
            Description = "母线 / 温度保护触发逻辑检测",
            Category = TestCategory.Function,
            TestId = FactoryTestId.FunctionProtection,
        });

        AddItem(new AutomationTestItem
        {
            Name = "使能 / 抱闸输出",
            Description = "使能与抱闸输出通路检测",
            Category = TestCategory.Function,
            TestId = FactoryTestId.FunctionBrakeOutput,
        });

        OnPropertyChanged(nameof(TotalCount));
    }

    private void AddItem(AutomationTestItem item)
    {
        Tests.Add(item);

        switch (item.Category)
        {
            case TestCategory.Board:
                BoardTests.Add(item);
                break;

            case TestCategory.Peripheral:
                PeripheralTests.Add(item);
                break;

            case TestCategory.Function:
                FunctionTests.Add(item);
                break;
        }
    }

    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            _isInitialized = true;
        }

        _deviceAdd.PropertyChanged += OnDeviceAddPropertyChanged;
        SubscribeUsb(_deviceAdd.UsbMaster);

        UpdateStatus();
        OnPropertyChanged(nameof(CanRun));
        RunAllCommand.NotifyCanExecuteChanged();
        RunItemCommand.NotifyCanExecuteChanged();
    }

    public override void OnNavigatedFrom()
    {
        _deviceAdd.PropertyChanged -= OnDeviceAddPropertyChanged;
        SubscribeUsb(null);

        // 离开页面时停止仍在进行的全流程，避免后台占用 USB。
        try
        {
            _runAllCts?.Cancel();
        }
        catch
        {
            // 忽略取消异常
        }
    }

    private void OnDeviceAddPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceAddViewModel.IsUsbConnected))
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                // USB 主站实例可能在连接 / 断开时变更，重新订阅。
                SubscribeUsb(_deviceAdd.UsbMaster);
                UpdateStatus();
                OnPropertyChanged(nameof(CanRun));
                RunAllCommand.NotifyCanExecuteChanged();
                RunItemCommand.NotifyCanExecuteChanged();
            });
        }
    }

    private void SubscribeUsb(UsbMaster? master)
    {
        if (ReferenceEquals(_subscribedUsbMaster, master))
        {
            return;
        }

        if (_subscribedUsbMaster is not null)
        {
            _subscribedUsbMaster.PacketReceived -= OnUsbPacketReceived;
        }

        _subscribedUsbMaster = master;

        if (master is not null)
        {
            master.PacketReceived += OnUsbPacketReceived;
        }
    }

    private void OnUsbPacketReceived(UsbPacket pkt)
    {
        if (pkt.Channel != UsbChannel.FactoryTest)
        {
            return;
        }

        if (!FactoryTestProtocol.TryParseResponse(pkt.Payload, out FactoryTestResponse response))
        {
            return;
        }

        if (_pending.TryRemove(response.Sequence, out TaskCompletionSource<FactoryTestResponse>? tcs))
        {
            tcs.TrySetResult(response);
        }
    }

    private bool CanRunGuard() => CanRun;

    [RelayCommand(CanExecute = nameof(CanRunGuard))]
    private async Task RunAll()
    {
        if (IsRunningAll)
        {
            return;
        }

        if (!_deviceAdd.IsUsbConnected)
        {
            StatusText = "USB 未连接，无法执行自动化流程";
            return;
        }

        IsRunningAll = true;
        OnPropertyChanged(nameof(IsNotRunningAll));
        OnPropertyChanged(nameof(CanRun));
        RunAllCommand.NotifyCanExecuteChanged();
        RunItemCommand.NotifyCanExecuteChanged();

        _runAllCts = new CancellationTokenSource();
        CancellationToken ct = _runAllCts.Token;

        ResetResults();

        try
        {
            int index = 0;
            foreach (AutomationTestItem item in Tests)
            {
                if (ct.IsCancellationRequested)
                {
                    StatusText = "已停止";
                    break;
                }

                index++;
                StatusText = $"正在执行（{index}/{Tests.Count}）：{item.CategoryName} - {item.Name}";

                await RunSingleAsync(item, ct);
            }

            if (!ct.IsCancellationRequested)
            {
                UpdateStatus();
            }
        }
        finally
        {
            IsRunningAll = false;
            OnPropertyChanged(nameof(IsNotRunningAll));
            OnPropertyChanged(nameof(CanRun));
            RunAllCommand.NotifyCanExecuteChanged();
            RunItemCommand.NotifyCanExecuteChanged();

            _runAllCts?.Dispose();
            _runAllCts = null;
        }
    }

    private bool CanRunItem(AutomationTestItem? item) => item is not null && _deviceAdd.IsUsbConnected && !IsRunningAll && !item.IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunItem))]
    private async Task RunItem(AutomationTestItem? item)
    {
        if (item is null)
        {
            return;
        }

        await RunSingleAsync(item, CancellationToken.None);
        UpdateStatus();
    }

    /// <summary>
    /// 执行单个测试项：下发测试命令并等待回传，按结果更新测试项状态。
    /// </summary>
    private async Task RunSingleAsync(AutomationTestItem item, CancellationToken ct)
    {
        UsbMaster? master = _deviceAdd.UsbMaster;

        item.IsBusy = true;
        item.State = TestResultState.Running;
        item.Detail = string.Empty;
        item.FaultStage = 0;
        RunItemCommand.NotifyCanExecuteChanged();

        if (master is null || !master.IsRunning || !_deviceAdd.IsUsbConnected)
        {
            item.State = TestResultState.Unavailable;
            item.Detail = "USB 未连接或主站未运行";
            item.DurationMs = 0;
            item.IsBusy = false;
            RunItemCommand.NotifyCanExecuteChanged();
            return;
        }

        ushort seq = NextSequence();
        var tcs = new TaskCompletionSource<FactoryTestResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = tcs;

        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            byte[] request = FactoryTestProtocol.BuildRequest(item.TestId, seq);
            bool sent = master.Send(UsbChannel.FactoryTest, request);

            if (!sent)
            {
                item.State = TestResultState.Unavailable;
                item.Detail = "测试命令发送失败";
                return;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(item.TimeoutMs);

            using (timeoutCts.Token.Register(() => tcs.TrySetCanceled()))
            {
                FactoryTestResponse response = await tcs.Task.ConfigureAwait(true);
                ApplyResponse(item, response);
            }
        }
        catch (TaskCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                item.State = TestResultState.NotRun;
                item.Detail = "流程已停止";
            }
            else
            {
                item.State = TestResultState.Timeout;
                item.Detail = $"等待回传超时（>{item.TimeoutMs} ms）";
            }
        }
        catch (System.Exception ex)
        {
            item.State = TestResultState.Unavailable;
            item.Detail = $"执行异常：{ex.Message}";
        }
        finally
        {
            sw.Stop();
            item.DurationMs = sw.Elapsed.TotalMilliseconds;
            _pending.TryRemove(seq, out _);
            item.IsBusy = false;
            RunItemCommand.NotifyCanExecuteChanged();
        }
    }

    private static void ApplyResponse(AutomationTestItem item, FactoryTestResponse response)
    {
        item.FaultStage = response.FaultStage;

        switch (response.ResultCode)
        {
            case FactoryTestResultCode.Pass:
                item.State = TestResultState.Pass;
                item.Detail = string.IsNullOrEmpty(response.Detail) ? "合格" : response.Detail;
                break;

            case FactoryTestResultCode.Fail:
                item.State = TestResultState.Fail;
                item.Detail = string.IsNullOrEmpty(response.Detail)
                    ? $"不合格（故障级别 {response.FaultStage}）"
                    : $"{response.Detail}（故障级别 {response.FaultStage}）";
                break;

            case FactoryTestResultCode.Unavailable:
                item.State = TestResultState.Unavailable;
                item.Detail = string.IsNullOrEmpty(response.Detail) ? "该测试项不可用" : response.Detail;
                break;

            default:
                item.State = TestResultState.Unavailable;
                item.Detail = $"未知结果码 0x{(byte)response.ResultCode:X2}";
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(IsNotRunningAll))]
    private void Stop()
    {
        try
        {
            _runAllCts?.Cancel();
        }
        catch
        {
            // 忽略
        }
    }

    [RelayCommand(CanExecute = nameof(IsNotRunningAll))]
    private void Reset()
    {
        ResetResults();
        StatusText = "就绪";
    }

    private void ResetResults()
    {
        foreach (AutomationTestItem item in Tests)
        {
            item.State = TestResultState.NotRun;
            item.Detail = string.Empty;
            item.FaultStage = 0;
            item.DurationMs = 0;
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        int pass = 0;
        int fail = 0;
        int timeout = 0;
        int unavailable = 0;

        foreach (AutomationTestItem item in Tests)
        {
            switch (item.State)
            {
                case TestResultState.Pass:
                    pass++;
                    break;

                case TestResultState.Fail:
                    fail++;
                    break;

                case TestResultState.Timeout:
                    timeout++;
                    break;

                case TestResultState.Unavailable:
                    unavailable++;
                    break;
            }
        }

        PassCount = pass;
        FailCount = fail;
        TimeoutCount = timeout;
        UnavailableCount = unavailable;

        if (!_deviceAdd.IsUsbConnected)
        {
            StatusText = "USB 未连接";
            return;
        }

        if (pass + fail + timeout + unavailable == 0)
        {
            StatusText = "就绪";
            return;
        }

        StatusText = fail + timeout > 0
            ? $"完成：合格 {pass}，不合格 {fail}，超时 {timeout}，不可用 {unavailable}"
            : $"全部合格：{pass}/{Tests.Count}";
    }

    private ushort NextSequence()
    {
        _sequence = unchecked((ushort)(_sequence + 1));
        if (_sequence == 0)
        {
            _sequence = 1;
        }

        return _sequence;
    }

    partial void OnIsRunningAllChanged(bool value)
    {
        StopCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
    }
}
