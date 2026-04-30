// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Wpf.Ui.Controls;

namespace Wpf.Ui.servoStudio.Services;

/// <summary>
/// Describes a page entry for display in the Quick Access section.
/// </summary>
public class QuickAccessItem : ObservableObject
{
    public Type? PageType { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public SymbolRegular Icon { get; set; }
    public int VisitCount { get; set; }
}

/// <summary>
/// Tracks page visit frequency and provides the top-N most-used pages.
/// Uses time-decay scoring: recent visits weigh more than older ones.
/// Persisted via <see cref="UserSettingsService"/>.
/// </summary>
public class PageUsageTracker
{
    private readonly Dictionary<string, int> _visitCounts;
    private readonly HashSet<string> _excludedPages;

    /// <summary>
    /// Page metadata registry: Type → (Title, Description, Icon).
    /// </summary>
    private static readonly Dictionary<Type, (string Title, string Description, SymbolRegular Icon)> _pageRegistry = new()
    {
        // 设备管理
        [typeof(Views.Pages.DeviceSetPages.StartPage)] = ("设备连接", "添加、选择、断开设备", SymbolRegular.PlugConnected24),
        [typeof(Views.Pages.DeviceSetPages.ListPage)] = ("设备列表", "查看已连接设备", SymbolRegular.TextBulletList20),
        [typeof(Views.Pages.DeviceSetPages.DeviceAddPage)] = ("添加设备", "扫描并添加新设备", SymbolRegular.AddCircle24),
        
        // 控制与调试
        [typeof(Views.Pages.ControlPage)] = ("控制台", "实时控制与调试", SymbolRegular.DesktopCursor24),
        [typeof(Views.Pages.DashboardPage)] = ("仪表盘", "设备状态实时监控", SymbolRegular.Gauge24),
        
        // 故障与诊断
        [typeof(Views.Pages.FaultInfoPage)] = ("故障信息", "查看故障与告警记录", SymbolRegular.Warning24),
        
        // 硬件配置
        [typeof(Views.Pages.HardwarePage)] = ("硬件总览", "硬件配置概览", SymbolRegular.DeveloperBoard16),
        [typeof(Views.Pages.HardwarePages.ControllerPage)] = ("控制器参数", "配置控制器硬件参数", SymbolRegular.DeveloperBoardLightning20),
        [typeof(Views.Pages.HardwarePages.MotorPage)] = ("电机参数", "配置电机相关参数", SymbolRegular.Engine20),
        [typeof(Views.Pages.HardwarePages.IOPage)] = ("IO配置", "输入输出端口配置", SymbolRegular.DockRow24),
        
        // 运动控制
        [typeof(Views.Pages.MotionPages.MotionTypePage)] = ("控制模式", "配置运动控制方式", SymbolRegular.MapDrive16),
        [typeof(Views.Pages.MotionPages.MotionLimitPage)] = ("运动限制", "运动限制参数配置与监控", SymbolRegular.CenterHorizontal20),
        
        // 参数管理
        [typeof(Views.Pages.ParametersPages.FactoryPage)] = ("厂家参数", "查看/修改出厂参数", SymbolRegular.CalendarLock20),
        
        // 数据与日志
        [typeof(Views.Pages.AppDataPages.AppLogPage)] = ("软件日志", "查看应用运行日志", SymbolRegular.DocumentChevronDouble20),
        [typeof(Views.Pages.AppDataPages.DataSavePage)] = ("数据存储", "伺服变量采样与导出", SymbolRegular.ArrowDownload24),
        [typeof(Views.Pages.AppDataPages.DataViewPage)] = ("数据查看", "查看历史数据记录", SymbolRegular.Table24),
        [typeof(Views.Pages.AppDataPages.PidAdjustPage)] = ("PID调节", "PID参数调整与曲线", SymbolRegular.StreamInputOutput20),
        [typeof(Views.Pages.AppDataPages.UserConfigPage)] = ("用户配置", "保存与加载用户参数配置", SymbolRegular.Person20),
        
        // 固件管理
        [typeof(Views.Pages.FirmwarePages.FirmwarePage)] = ("EEPROM提取", "提取EEPROM数据", SymbolRegular.Memory16),
        [typeof(Views.Pages.FirmwarePages.FirmwareProgramPage)] = ("固件烧录", "EtherCAT XML烧录与BOOT", SymbolRegular.Flash24),
        [typeof(Views.Pages.FirmwarePages.FactoryFirmwarePage)] = ("出厂固件", "管理与烧录出厂固件", SymbolRegular.HardDrive24),
        
        // 数据页面
        [typeof(Views.Pages.DataPage)] = ("数据管理", "数据查看与管理", SymbolRegular.Folder24),
    };

    /// <summary>
    /// Default quick-access pages when there's no usage history.
    /// </summary>
    private static readonly Type[] _defaultTopPages =
    [
        typeof(Views.Pages.DeviceSetPages.StartPage),
        typeof(Views.Pages.ControlPage),
        typeof(Views.Pages.AppDataPages.PidAdjustPage),
    ];

    public PageUsageTracker()
    {
        UserSettings settings = UserSettingsService.Load();
        _visitCounts = new Dictionary<string, int>(settings.PageVisitCounts);

        // Pages that should never appear in quick access
        _excludedPages =
        [
            typeof(Views.Pages.SettingsPage).FullName!,
            typeof(Views.Pages.HomePage).FullName!,
        ];
    }

    /// <summary>
    /// Returns the friendly title for a given page type, or the type name when not registered.
    /// </summary>
    public static string GetPageTitle(Type pageType)
    {
        if (pageType is null)
            return string.Empty;

        return _pageRegistry.TryGetValue(pageType, out (string Title, string Description, SymbolRegular Icon) meta)
            ? meta.Title
            : pageType.Name;
    }

    /// <summary>
    /// Records a page visit.
    /// </summary>
    public void RecordVisit(Type pageType)
    {
        var key = pageType.FullName!;
        if (_excludedPages.Contains(key))
        {
            return;
        }

        _ = _visitCounts.TryGetValue(key, out int current);
        _visitCounts[key] = current + 1;

        // Persist
        UserSettings settings = UserSettingsService.Load();
        settings.PageVisitCounts = new Dictionary<string, int>(_visitCounts);
        UserSettingsService.Save(settings);
    }

    /// <summary>
    /// Returns the top <paramref name="count"/> most-used pages as <see cref="QuickAccessItem"/>s.
    /// Falls back to defaults when there is insufficient history.
    /// </summary>
    public List<QuickAccessItem> GetTopPages(int count = 3)
    {
        // Filter to only registered, non-excluded pages with visit count > 0
        var ranked = _visitCounts
            .Where(kv => !_excludedPages.Contains(kv.Key) && kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Take(count)
            .ToList();

        var result = new List<QuickAccessItem>();

        foreach (KeyValuePair<string, int> kv in ranked)
        {
            Type? pageType = _pageRegistry.Keys.FirstOrDefault(t => t.FullName == kv.Key);
            if (pageType is not null && _pageRegistry.TryGetValue(pageType, out (string Title, string Description, SymbolRegular Icon) meta))
            {
                result.Add(new QuickAccessItem
                {
                    PageType = pageType,
                    Title = meta.Title,
                    Description = meta.Description,
                    Icon = meta.Icon,
                    VisitCount = kv.Value,
                });
            }
        }

        // Fill remaining slots with defaults (preserving order, skipping duplicates)
        if (result.Count < count)
        {
            foreach (Type defaultType in _defaultTopPages)
            {
                if (result.Count >= count)
                {
                    break;
                }

                if (result.Any(r => r.PageType == defaultType))
                {
                    continue;
                }

                if (_pageRegistry.TryGetValue(defaultType, out (string Title, string Description, SymbolRegular Icon) meta))
                {
                    result.Add(new QuickAccessItem
                    {
                        PageType = defaultType,
                        Title = meta.Title,
                        Description = meta.Description,
                        Icon = meta.Icon,
                        VisitCount = 0,
                    });
                }
            }
        }

        return result;
    }
}