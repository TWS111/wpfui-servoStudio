// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.servoStudio.Models;
using Wpf.Ui.servoStudio.ViewModels.Factory;

namespace Wpf.Ui.servoStudio.Views.Pages.FactoryPages;

/// <summary>
/// 将字节数转换为字节槽宽度（每字节 132px，最小 132px）。
/// </summary>
public sealed class ByteCountToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n ? Math.Max(1, n) * 132.0 : 132.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 将 bool 取反后转换为 Visibility。
/// </summary>
public sealed class InverseBoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public partial class FrameFormatEditorPage : INavigableView<FrameFormatEditorViewModel>
{
    private const string FieldDragDataFormat = "ServoStudio.FrameFormatEditor.Field";

    private Point _dragStartPoint;
    private FrameFieldGroup? _dragCandidate;
    private Border? _activeDropSurface;
    private Popup? _dragPreviewPopup;

    public FrameFormatEditorViewModel ViewModel { get; }

    public FrameFormatEditorPage(FrameFormatEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();

        _ = new FactoryGateHelper(this, FactoryLockOverlay, "帧格式修改器页");
    }

    private void OnToggleExpandClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: FrameFieldGroup field })
            ViewModel.ToggleExpandCommand.Execute(field);
    }

    private void OnSwapBytesClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: FrameFieldGroup field })
            ViewModel.SwapBytesCommand.Execute(field);
    }

    private void OnInsertByteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: FrameFieldGroup field }) return;
        var format = GetFormatForField(field);
        if (format is null) return;
        int idx = format.Fields.IndexOf(field);
        ViewModel.InsertByteCommand.Execute((format, idx));
    }

    private void OnRemoveFieldClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: FrameFieldGroup field }) return;
        var format = GetFormatForField(field);
        if (format is null) return;
        ViewModel.RemoveFieldCommand.Execute((format, field));
    }

    private void OnDragHandlePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: FrameFieldGroup field } handle) return;

        var format = GetFormatForField(field);
        if (!CanStartDrag(format) && !CanStartCopyDrag(field)) return;

        _dragCandidate = field;
        _dragStartPoint = e.GetPosition(this);
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void OnDragHandlePreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border handle)
            handle.ReleaseMouseCapture();

        _dragCandidate = null;
    }

    private void OnDragHandlePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not Border handle) return;

        Point currentPoint = e.GetPosition(this);
        Vector delta = currentPoint - _dragStartPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var field = _dragCandidate;
        var sourceFormat = GetFormatForField(field);
        bool isCopy = sourceFormat is null && CanStartCopyDrag(field);
        if (!isCopy && !CanStartDrag(sourceFormat)) return;

        _dragCandidate = null;
        handle.ReleaseMouseCapture();
        StartFieldDrag(handle, sourceFormat, field, isCopy);
        e.Handled = true;
    }

    private void OnDragHandleGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        UpdateDragPreviewPosition();
        e.UseDefaultCursors = true;
    }

    private void OnFrameDragOver(object sender, DragEventArgs e)
    {
        if (sender is not Border dropSurface
            || dropSurface.Tag is not FrameFormatBase targetFormat
            || !TryGetDragPayload(e.Data, out var payload)
            || !CanDrop(payload, targetFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        SetActiveDropSurface(dropSurface);
        UpdateDragPreviewPosition();
        e.Effects = payload.IsCopy ? DragDropEffects.Copy : DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnFrameDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border surface && ReferenceEquals(surface, _activeDropSurface))
            ClearActiveDropSurface();
    }

    private void OnFrameDrop(object sender, DragEventArgs e)
    {
        if (sender is not Border dropSurface
            || dropSurface.Tag is not FrameFormatBase targetFormat
            || !TryGetDragPayload(e.Data, out var payload)
            || !CanDrop(payload, targetFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            ClearActiveDropSurface();
            return;
        }

        var itemsControl = FindDescendant<ItemsControl>(dropSurface);
        int targetIndex = itemsControl is null
            ? targetFormat.Fields.Count
            : GetDropIndex(itemsControl, e.GetPosition(itemsControl));

        bool accepted = payload.IsCopy
            ? ViewModel.TryCopyFieldTo(payload.Field, targetFormat, targetIndex)
            : payload.SourceFormat is not null && ViewModel.TryMoveField(payload.SourceFormat, payload.Field, targetFormat, targetIndex);

        e.Effects = accepted
            ? payload.IsCopy ? DragDropEffects.Copy : DragDropEffects.Move
            : DragDropEffects.None;

        e.Handled = true;
        ClearActiveDropSurface();
    }

    private void StartFieldDrag(UIElement dragSource, FrameFormatBase? sourceFormat, FrameFieldGroup field, bool isCopy)
    {
        try
        {
            if (!isCopy)
                field.IsDragging = true;
            OpenDragPreview(field);

            var data = new DataObject(FieldDragDataFormat, new FieldDragPayload(sourceFormat, field, isCopy));
            DragDrop.DoDragDrop(dragSource, data, isCopy ? DragDropEffects.Copy : DragDropEffects.Move);
        }
        finally
        {
            if (!isCopy)
                field.IsDragging = false;
            CloseDragPreview();
            ClearActiveDropSurface();
        }
    }

    private bool CanStartDrag(FrameFormatBase? format)
        => format is not null && !ViewModel.IsFactoryLocked && !format.IsReadOnly;

    private bool CanStartCopyDrag(FrameFieldGroup field)
        => !ViewModel.IsFactoryLocked && ViewModel.ExampleFields.Contains(field);

    private bool CanDrop(FieldDragPayload payload, FrameFormatBase targetFormat)
    {
        if (ViewModel.IsFactoryLocked || targetFormat.IsReadOnly)
            return false;

        if (payload.IsCopy)
            return ViewModel.ExampleFields.Contains(payload.Field);

        return payload.SourceFormat is not null
               && !payload.SourceFormat.IsReadOnly
               && payload.SourceFormat.Fields.Contains(payload.Field);
    }

    private static bool TryGetDragPayload(IDataObject data, out FieldDragPayload payload)
    {
        if (data.GetDataPresent(FieldDragDataFormat)
            && data.GetData(FieldDragDataFormat) is FieldDragPayload dragPayload)
        {
            payload = dragPayload;
            return true;
        }

        payload = default!;
        return false;
    }

    private static int GetDropIndex(ItemsControl itemsControl, Point dropPoint)
    {
        int itemCount = itemsControl.Items.Count;
        int fallbackIndex = itemCount;

        for (int index = 0; index < itemCount; index++)
        {
            if (itemsControl.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
                continue;

            Point topLeft = container.TranslatePoint(new Point(0, 0), itemsControl);
            var bounds = new Rect(topLeft, container.RenderSize);

            if (dropPoint.Y < bounds.Top)
                return index;

            if (dropPoint.Y <= bounds.Bottom)
            {
                if (dropPoint.X < bounds.Left + bounds.Width / 2)
                    return index;

                fallbackIndex = index + 1;
            }
        }

        return Math.Clamp(fallbackIndex, 0, itemCount);
    }

    private void SetActiveDropSurface(Border dropSurface)
    {
        if (ReferenceEquals(_activeDropSurface, dropSurface)) return;

        ClearActiveDropSurface();
        _activeDropSurface = dropSurface;
        dropSurface.BorderThickness = new Thickness(2);
        dropSurface.BorderBrush = ResolveBrush("AccentFillColorDefaultBrush", Brushes.DodgerBlue);
        dropSurface.Background = ResolveBrush("ControlFillColorSecondaryBrush", Brushes.Transparent);
    }

    private void ClearActiveDropSurface()
    {
        if (_activeDropSurface is null) return;

        _activeDropSurface.ClearValue(Border.BorderThicknessProperty);
        _activeDropSurface.ClearValue(Border.BorderBrushProperty);
        _activeDropSurface.ClearValue(Border.BackgroundProperty);
        _activeDropSurface = null;
    }

    private void OpenDragPreview(FrameFieldGroup field)
    {
        CloseDragPreview();

        double previewWidth = Math.Min(Math.Max(field.ByteCount * 132.0, 132.0), 380.0);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = field.Name,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResolveBrush("TextFillColorPrimaryBrush", Brushes.Black),
        });
        panel.Children.Add(new TextBlock
        {
            Text = field.Description,
            FontSize = 13,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResolveBrush("TextFillColorSecondaryBrush", Brushes.DimGray),
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{field.ByteCount}B",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = field.IsLengthLimitExceeded
                ? ResolveBrush("SystemFillColorCriticalBrush", Brushes.IndianRed)
                : ResolveBrush("AccentTextFillColorPrimaryBrush", Brushes.DodgerBlue),
        });

        _dragPreviewPopup = new Popup
        {
            AllowsTransparency = true,
            IsHitTestVisible = false,
            Placement = PlacementMode.AbsolutePoint,
            Child = new Border
            {
                Width = previewWidth,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Opacity = 0.92,
                Background = field.IsLengthLimitExceeded
                    ? ResolveBrush("SystemFillColorCautionBackgroundBrush", Brushes.LemonChiffon)
                    : ResolveBrush("ControlFillColorDefaultBrush", Brushes.White),
                BorderBrush = field.IsLengthLimitExceeded
                    ? ResolveBrush("SystemFillColorCriticalBrush", Brushes.IndianRed)
                    : ResolveBrush("AccentFillColorDefaultBrush", Brushes.DodgerBlue),
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 2,
                    Opacity = 0.28,
                    Color = Colors.Black,
                },
                Child = panel,
            },
        };

        UpdateDragPreviewPosition();
        _dragPreviewPopup.IsOpen = true;
    }

    private void CloseDragPreview()
    {
        if (_dragPreviewPopup is null) return;

        _dragPreviewPopup.IsOpen = false;
        _dragPreviewPopup = null;
    }

    private void UpdateDragPreviewPosition()
    {
        if (_dragPreviewPopup is not { IsOpen: true }) return;
        if (!GetCursorPos(out var cursorPoint)) return;

        var screenPoint = new Point(cursorPoint.X, cursorPoint.Y);
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } compositionTarget)
            screenPoint = compositionTarget.TransformFromDevice.Transform(screenPoint);

        _dragPreviewPopup.HorizontalOffset = screenPoint.X + 16;
        _dragPreviewPopup.VerticalOffset = screenPoint.Y + 16;
    }

    private Brush ResolveBrush(string resourceKey, Brush fallback)
        => TryFindResource(resourceKey) as Brush ?? fallback;

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childCount; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T typedChild)
                return typedChild;

            T? nestedChild = FindDescendant<T>(child);
            if (nestedChild is not null)
                return nestedChild;
        }

        return null;
    }

    /// <summary>
    /// 根据字段组找到其所属的帧格式对象。
    /// </summary>
    private FrameFormatBase? GetFormatForField(FrameFieldGroup field)
    {
        foreach (var fmt in ViewModel.EnumerateFrameFormats())
        {
            if (fmt.Fields.Contains(field)) return fmt;
        }
        return null;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private sealed record FieldDragPayload(FrameFormatBase? SourceFormat, FrameFieldGroup Field, bool IsCopy);
}
