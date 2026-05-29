// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Wpf.Ui.servoStudio.Services;
using Wpf.Ui.servoStudio.ViewModels.DeviceSet;

namespace Wpf.Ui.servoStudio.ViewModels;

public partial class SettingsViewModel : ViewModel
{

    public string Test
    {
        get => "test";
        set
        {
            // This is just a placeholder to demonstrate the property structure.
            // In a real application, you would implement logic here.
        }
    }

    private bool _isInitialized = false;

    public SettingsViewModel()
    {
        // 订阅全局解锁状态变化（如网络变化导致的自动解锁/撤销解锁）
        Services.FactoryAccessService.UnlockStateChanged += OnFactoryUnlockStateChanged;
    }

    private void OnFactoryUnlockStateChanged(object? sender, EventArgs e)
    {
        // 该事件可能从非 UI 线程（NetworkChange 监听）触发，必须切回 UI 线程
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            SyncFactoryStateFromService();
        }
        else
        {
            _ = dispatcher.InvokeAsync(SyncFactoryStateFromService);
        }
    }

    /// <summary>
    /// 把 <see cref="Services.FactoryAccessService"/> 的最新状态同步到 ViewModel，并刷新提示文本。
    /// </summary>
    private void SyncFactoryStateFromService()
    {
        bool wasUnlocked = IsFactoryUnlocked;

        IsFactoryUnlocked = Services.FactoryAccessService.IsUnlocked;
        IsFactoryLockedOut = Services.FactoryAccessService.IsLockedOut;
        IsUnlockedByTrustedNetwork = Services.FactoryAccessService.IsUnlockedByTrustedNetwork;

        if (IsFactoryUnlocked)
        {
            IsFactoryUnlockMessageError = false;
            FactoryUnlockMessage = IsUnlockedByTrustedNetwork
                ? "厂家权限已解锁（公司网络）"
                : "厂家权限已解锁";
        }
        else if (IsFactoryLockedOut)
        {
            IsFactoryUnlockMessageError = true;
            FactoryUnlockMessage = "本次运行已锁定厂家模式，请重启软件后再试";
        }
        else if (wasUnlocked)
        {
            // 由 unlocked → locked，且未被 LockedOut，说明是网络撤销（或外部 Lock）
            IsFactoryUnlockMessageError = false;
            FactoryUnlockMessage = "已离开公司网络，厂家权限已撤销";
        }
        else
        {
            IsFactoryUnlockMessageError = false;
            FactoryUnlockMessage = string.Empty;
        }
    }

    [ObservableProperty]
    private string _appVersion = string.Empty;

    [ObservableProperty]
    private string appName = string.Empty;

    [ObservableProperty]
    private Wpf.Ui.Appearance.ApplicationTheme _currentApplicationTheme = Wpf.Ui
        .Appearance
        .ApplicationTheme
        .Unknown;

    [ObservableProperty]
    private bool _isFollowingSystemTheme;

    [ObservableProperty]
    private bool _isDebugMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommLostThresholdDisplay))]
    private int _commLostThreshold = DeviceAddViewModel.CommLostThreshold;

    /// <summary>看门狗阈值的显示文本，用于 UI 提示。</summary>
    public string CommLostThresholdDisplay => $"连续 {CommLostThreshold} 次失败后触发急停（约 {CommLostThreshold * 2} 秒）";

    partial void OnCommLostThresholdChanged(int value)
    {
        DeviceAddViewModel.CommLostThreshold = System.Math.Max(1, value);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInputFactoryPassword))]
    [NotifyPropertyChangedFor(nameof(FactoryStatusText))]
    [NotifyPropertyChangedFor(nameof(FactoryStatusKind))]
    private bool _isFactoryUnlocked;

    [ObservableProperty]
    private string _factoryUnlockMessage = string.Empty;

    [ObservableProperty]
    private bool _isFactoryUnlockMessageError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInputFactoryPassword))]
    [NotifyPropertyChangedFor(nameof(FactoryStatusText))]
    [NotifyPropertyChangedFor(nameof(FactoryStatusKind))]
    private bool _isFactoryLockedOut;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FactoryStatusText))]
    [NotifyPropertyChangedFor(nameof(FactoryStatusKind))]
    private bool _isUnlockedByTrustedNetwork;

    /// <summary>
    /// 密码输入框与“确认”按钮是否可用：仅在未解锁且未被锁定时可用。
    /// </summary>
    public bool CanInputFactoryPassword => !IsFactoryUnlocked && !IsFactoryLockedOut;

    /// <summary>
    /// 当前厂家权限状态的简要文本，用于顶部状态标签。
    /// </summary>
    public string FactoryStatusText
    {
        get
        {
            if (IsFactoryLockedOut)
            {
                return "已锁定";
            }

            if (IsFactoryUnlocked)
            {
                return IsUnlockedByTrustedNetwork ? "已解锁（公司网络）" : "已解锁";
            }

            return "未解锁";
        }
    }

    /// <summary>
    /// 状态标签的调色种类： Locked / Unlocked / LockedOut。
    /// </summary>
    public string FactoryStatusKind
    {
        get
        {
            if (IsFactoryLockedOut)
            {
                return "LockedOut";
            }
            return IsFactoryUnlocked ? "Unlocked" : "Locked";
        }
    }

    public override void OnNavigatedTo()
    {
        if (!_isInitialized)
        {
            InitializeViewModel();
        }

        // 每次进入设置页同步一次当前解锁状态
        SyncFactoryStateFromService();

        // 若尚未解锁也未被锁定，检测是否处于受信任的公司网络，如果是则自动解锁
        if (!IsFactoryUnlocked && !IsFactoryLockedOut)
        {
            _ = Services.FactoryAccessService.TryUnlockByTrustedNetworkAsync();
            // 解锁成功会通过 UnlockStateChanged 事件回流到本 ViewModel
        }
    }

    private void InitializeViewModel()
    {
        CurrentApplicationTheme = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme();
        IsFollowingSystemTheme = Services.UserSettingsService.Load().ThemeMode == "theme_system";
        IsDebugMode = Services.UserSettingsService.Load().IsDebugMode;
        AppVersion = $"Wpf.Ui.servoStudio - {GetAssemblyVersion()}";

        // 加载运动周期同步设置
        UserSettings s = Services.UserSettingsService.Load();
        _suppressMotionSyncSave = true;
        MotionCyclicTimerKindIndex = System.Math.Clamp(s.Motion_CyclicTimerKind, 0, 2);
        MotionPreferPdoForCyclicSync = s.Motion_PreferPdoForCyclicSync;
        _suppressMotionSyncSave = false;

        _isInitialized = true;
    }

    /// <summary>
    /// 尝试使用输入的密码解锁厂家权限。密码不做任何持久化。
    /// 一旦输入过错误密码，本会话将被锁定，无法再次以密码或公司网络解锁。
    /// </summary>
    public void TryUnlockFactory(string? password)
    {
        if (Services.FactoryAccessService.TryUnlock(password))
        {
            IsFactoryUnlocked = true;
            IsUnlockedByTrustedNetwork = false;
            IsFactoryLockedOut = false;
            IsFactoryUnlockMessageError = false;
            FactoryUnlockMessage = "厂家权限已解锁";
            return;
        }

        IsFactoryUnlocked = Services.FactoryAccessService.IsUnlocked;
        IsUnlockedByTrustedNetwork = Services.FactoryAccessService.IsUnlockedByTrustedNetwork;
        IsFactoryLockedOut = Services.FactoryAccessService.IsLockedOut;
        IsFactoryUnlockMessageError = true;
        FactoryUnlockMessage = IsFactoryLockedOut
            ? "厂家密码错误，本次运行已锁定厂家模式，请重启软件后再试"
            : "厂家密码错误";
    }

    [RelayCommand]
    private void OnLockFactory()
    {
        Services.FactoryAccessService.Lock();
        IsFactoryUnlocked = false;
        IsUnlockedByTrustedNetwork = false;
        IsFactoryUnlockMessageError = false;
        FactoryUnlockMessage = "已锁定厂家权限";
    }

    private static string GetAssemblyVersion()
    {
        return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? string.Empty;
    }

    [RelayCommand]
    private void OnChangeTheme(string parameter)
    {
        switch (parameter)
        {
            case "theme_system":
                IsFollowingSystemTheme = true;
                if (Application.Current.MainWindow is { } window)
                {
                    Wpf.Ui.Appearance.SystemThemeWatcher.Watch(window);
                }

                Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
                CurrentApplicationTheme = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme();
                break;

            case "theme_dark":
                StopSystemThemeWatch();
                if (CurrentApplicationTheme == Wpf.Ui.Appearance.ApplicationTheme.Dark)
                {
                    break;
                }

                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
                CurrentApplicationTheme = Wpf.Ui.Appearance.ApplicationTheme.Dark;

                break;

            default:
                StopSystemThemeWatch();
                if (CurrentApplicationTheme == Wpf.Ui.Appearance.ApplicationTheme.Light)
                {
                    break;
                }

                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
                CurrentApplicationTheme = Wpf.Ui.Appearance.ApplicationTheme.Light;

                break;
        }

        // Save the theme choice to local storage
        UserSettings settings = Services.UserSettingsService.Load();
        settings.ThemeMode = parameter;
        Services.UserSettingsService.Save(settings);
    }

    private void StopSystemThemeWatch()
    {
        if (!IsFollowingSystemTheme)
        {
            return;
        }

        IsFollowingSystemTheme = false;

        if (Application.Current.MainWindow is { } window)
        {
            Wpf.Ui.Appearance.SystemThemeWatcher.UnWatch(window);
        }
    }

    partial void OnIsDebugModeChanged(bool value)
    {
        UserSettings settings = Services.UserSettingsService.Load();
        settings.IsDebugMode = value;
        Services.UserSettingsService.Save(settings);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  运动周期同步（CSP/CSV/CST）— 底层周期定时器与 PDO 路径开关
    // ════════════════════════════════════════════════════════════════════════

    private bool _suppressMotionSyncSave;

    /// <summary>
    /// 周期同步定时器候选列表（与 <see cref="Core.Sync.CyclicTimerKind"/> 索引对齐）。<br/>
    /// 0=PeriodicTimer, 1=WinMm, 2=SpinWait。绑定到 SettingsPage 的 ComboBox。
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<string> MotionCyclicTimerKinds { get; }
        = new(new[]
        {
            Core.Sync.CyclicTimerFactory.DisplayName(Core.Sync.CyclicTimerKind.PeriodicTimer),
            Core.Sync.CyclicTimerFactory.DisplayName(Core.Sync.CyclicTimerKind.WinMm),
            Core.Sync.CyclicTimerFactory.DisplayName(Core.Sync.CyclicTimerKind.SpinWait),
        });

    [ObservableProperty]
    private int _motionCyclicTimerKindIndex;

    [ObservableProperty]
    private bool _motionPreferPdoForCyclicSync = true;

    partial void OnMotionCyclicTimerKindIndexChanged(int value)
    {
        if (_suppressMotionSyncSave) return;
        try
        {
            UserSettings s = Services.UserSettingsService.Load();
            s.Motion_CyclicTimerKind = System.Math.Clamp(value, 0, 2);
            Services.UserSettingsService.Save(s);
        }
        catch { /* ignore */ }
    }

    partial void OnMotionPreferPdoForCyclicSyncChanged(bool value)
    {
        if (_suppressMotionSyncSave) return;
        try
        {
            UserSettings s = Services.UserSettingsService.Load();
            s.Motion_PreferPdoForCyclicSync = value;
            Services.UserSettingsService.Save(s);
        }
        catch { /* ignore */ }
    }
}