// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.servoStudio.Services;

namespace Wpf.Ui.servoStudio.Views.Pages;

public partial class HomePage : INavigableView<ViewModels.HomeViewModel>
{
    public ViewModels.HomeViewModel ViewModel { get; }

    public static readonly DependencyProperty HoverTitleProperty = DependencyProperty.Register(
        nameof(HoverTitle), typeof(string), typeof(HomePage), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HoverDescriptionProperty = DependencyProperty.Register(
        nameof(HoverDescription), typeof(string), typeof(HomePage), new PropertyMetadata(string.Empty));

    public string HoverTitle
    {
        get => (string)GetValue(HoverTitleProperty);
        set => SetValue(HoverTitleProperty, value);
    }

    public string HoverDescription
    {
        get => (string)GetValue(HoverDescriptionProperty);
        set => SetValue(HoverDescriptionProperty, value);
    }

    // -1 = 无固定；0~4 = 对应分类已固定展开
    private int _pinnedIndex = -1;
    private Border[] _subMenus = null!;
    private StackPanel[] _categoryPanels = null!;

    public HomePage(ViewModels.HomeViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ViewModel.RefreshQuickAccess();
            _subMenus = [SubMenu0, SubMenu1, SubMenu2, SubMenu3, SubMenu4];
            _categoryPanels = [CategoryPanel0, CategoryPanel1, CategoryPanel2, CategoryPanel3, CategoryPanel4];
            AttachSubMenuHoverHandlers();
        };
    }

    // 右侧功能说明面板：悬停快速访问卡片时更新
    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { Tag: QuickAccessItem item })
        {
            UpdateHoverDescription(item.Title, item.Description);
            return;
        }

        if (sender is ButtonBase { CommandParameter: Type pageType }
            && PageUsageTracker.TryGetPageMetadata(pageType, out var meta))
        {
            UpdateHoverDescription(meta.Title, meta.Description);
        }
    }

    private void UpdateHoverDescription(string title, string description)
    {
        HoverTitle = title;
        HoverDescription = description;

        if (Resources["DescFadeIn"] is Storyboard sb && DescriptionSection is not null)
            sb.Begin(DescriptionSection, true);
    }

    private void AttachSubMenuHoverHandlers()
    {
        foreach (Border subMenu in _subMenus)
        {
            if (subMenu.Child is not Panel panel)
                continue;

            foreach (UIElement child in panel.Children)
            {
                if (child is ButtonBase button)
                {
                    button.MouseEnter -= Card_MouseEnter;
                    button.MouseEnter += Card_MouseEnter;
                }
            }
        }
    }

    // 鼠标进入分类容器：若未固定则展开悬停预览
    private void CategoryPanel_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!TryGetCategoryIndex(sender, out int index)) return;
        if (_pinnedIndex != index)
            AnimateSubMenu(_subMenus[index], expand: true);
    }

    // 鼠标离开分类容器：若未固定则收起
    private void CategoryPanel_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!TryGetCategoryIndex(sender, out int index)) return;
        if (_pinnedIndex != index)
            AnimateSubMenu(_subMenus[index], expand: false);
    }

    // 点击分类卡片：切换固定状态，不导航
    private void CategoryCard_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCategoryIndex(sender, out int index)) return;

        if (_pinnedIndex == index)
        {
            // 再次点击同一分类：取消固定并收起
            _pinnedIndex = -1;
            AnimateSubMenu(_subMenus[index], expand: false);
        }
        else
        {
            // 收起之前固定的分类
            if (_pinnedIndex >= 0)
                AnimateSubMenu(_subMenus[_pinnedIndex], expand: false);

            _pinnedIndex = index;
            AnimateSubMenu(_subMenus[index], expand: true);
        }

        // 阻止事件冒泡，防止触发 Page_PreviewMouseDown 立即收起
        e.Handled = true;
    }

    // 点击页面空白处或其他控件：收起固定的子菜单
    private void Page_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_pinnedIndex < 0) return;

        // 若点击位置在固定分类的容器内，不收起
        if (e.OriginalSource is DependencyObject source
            && _categoryPanels[_pinnedIndex].IsAncestorOf(source))
            return;

        AnimateSubMenu(_subMenus[_pinnedIndex], expand: false);
        _pinnedIndex = -1;
    }

    private static bool TryGetCategoryIndex(object sender, out int index)
    {
        index = -1;
        return sender is FrameworkElement fe
            && int.TryParse(fe.Tag?.ToString(), out index);
    }

    private void AnimateSubMenu(Border subMenu, bool expand)
    {
        var sb = new Storyboard();

        var maxH = new DoubleAnimation
        {
            To = expand ? 1000 : 0,
            Duration = new Duration(TimeSpan.FromSeconds(expand ? 0.7 : 0.3)),
            EasingFunction = expand
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(maxH, subMenu);
        Storyboard.SetTargetProperty(maxH, new PropertyPath(FrameworkElement.MaxHeightProperty));

        var opacity = new DoubleAnimation
        {
            To = expand ? 1 : 0,
            Duration = new Duration(TimeSpan.FromSeconds(expand ? 0.3 : 0.2))
        };
        Storyboard.SetTarget(opacity, subMenu);
        Storyboard.SetTargetProperty(opacity, new PropertyPath(UIElement.OpacityProperty));

        sb.Children.Add(maxH);
        sb.Children.Add(opacity);
        sb.Begin();
    }
}
