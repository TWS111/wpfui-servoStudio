// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

// 数据导入：当文件包含多个工作表（xls / xlsx）时弹出此简单选择窗。
// 程序化构建，避免新增 XAML 资源。

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Wpf.Ui.servoStudio.Views.Dialogs;

internal static class SheetPickerWindow
{
    /// <summary>
    /// 弹出工作表选择对话框；用户取消返回 null，确定返回所选名称。
    /// 在 STA UI 线程上调用。
    /// </summary>
    public static string? Pick(string fileName, IList<string> sheetNames, string? preselect = null)
    {
        if (sheetNames is null || sheetNames.Count == 0)
        {
            return null;
        }

        var win = new Window
        {
            Title = "选择工作表",
            Width = 380,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.CanResize,
        };

        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = $"文件 \"{fileName}\" 包含多个工作表，请选择要导入的一项：",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var list = new ListBox
        {
            SelectionMode = SelectionMode.Single,
        };
        foreach (var n in sheetNames)
        {
            list.Items.Add(n);
        }

        if (!string.IsNullOrEmpty(preselect) && sheetNames.Contains(preselect))
        {
            list.SelectedItem = preselect;
        }
        else
        {
            list.SelectedIndex = 0;
        }

        list.MouseDoubleClick += (_, __) =>
        {
            if (list.SelectedItem is not null)
            {
                win.DialogResult = true;
                win.Close();
            }
        };
        Grid.SetRow(list, 1);
        grid.Children.Add(list);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var ok = new Button
        {
            Content = "确定",
            Width = 80,
            IsDefault = true,
            Margin = new Thickness(0, 0, 8, 0),
        };
        ok.Click += (_, __) =>
        {
            if (list.SelectedItem is not null)
            {
                win.DialogResult = true;
                win.Close();
            }
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 80,
            IsCancel = true,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        win.Content = grid;

        bool? r = win.ShowDialog();
        if (r == true && list.SelectedItem is string s)
        {
            return s;
        }
        return null;
    }
}
