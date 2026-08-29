using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace ReminNote.Windows.Features.Today;

public static class TodayTaskDragDropBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(TodayTaskDragDropBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty DragStateProperty =
        DependencyProperty.RegisterAttached(
            "DragState",
            typeof(DragState),
            typeof(TodayTaskDragDropBehavior),
            new PropertyMetadata(null));

    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsEnabledProperty, value);
    }

    public static bool GetIsEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsEnabledProperty);
    }

    private static void OnIsEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not ItemsControl itemsControl)
        {
            return;
        }

        if (args.NewValue is true)
        {
            itemsControl.AllowDrop = true;
            itemsControl.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            itemsControl.PreviewMouseMove += OnPreviewMouseMove;
            itemsControl.DragOver += OnDragOver;
            itemsControl.Drop += OnDrop;
            return;
        }

        itemsControl.AllowDrop = false;
        itemsControl.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        itemsControl.PreviewMouseMove -= OnPreviewMouseMove;
        itemsControl.DragOver -= OnDragOver;
        itemsControl.Drop -= OnDrop;
        itemsControl.ClearValue(DragStateProperty);
    }

    private static void OnPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs args)
    {
        if (sender is not ItemsControl itemsControl ||
            FindTask(args.OriginalSource as DependencyObject) is not { } task)
        {
            return;
        }

        itemsControl.SetValue(
            DragStateProperty,
            new DragState(args.GetPosition(itemsControl), task));
    }

    private static void OnPreviewMouseMove(
        object sender,
        MouseEventArgs args)
    {
        if (sender is not ItemsControl itemsControl ||
            args.LeftButton != MouseButtonState.Pressed ||
            itemsControl.GetValue(DragStateProperty) is not DragState state ||
            state.Source is null)
        {
            return;
        }

        var currentPosition = args.GetPosition(itemsControl);
        var horizontalDistance = Math.Abs(currentPosition.X - state.StartPoint.X);
        var verticalDistance = Math.Abs(currentPosition.Y - state.StartPoint.Y);
        if (horizontalDistance < SystemParameters.MinimumHorizontalDragDistance &&
            verticalDistance < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var source = state.Source;
        state.Source = null;
        DragDrop.DoDragDrop(
            itemsControl,
            source,
            DragDropEffects.Move);
    }

    private static void OnDragOver(object sender, DragEventArgs args)
    {
        if (sender is not ItemsControl ||
            !args.Data.GetDataPresent(typeof(TodayTaskViewModel)))
        {
            return;
        }

        args.Effects = DragDropEffects.Move;
        args.Handled = true;
    }

    private static async void OnDrop(object sender, DragEventArgs args)
    {
        if (sender is not ItemsControl itemsControl ||
            args.Data.GetData(typeof(TodayTaskViewModel)) is not TodayTaskViewModel source ||
            FindTask(args.OriginalSource as DependencyObject) is not { } target ||
            FindPage(itemsControl) is not { } page ||
            ReferenceEquals(source, target))
        {
            return;
        }

        args.Handled = true;
        var targetContainer = itemsControl.ItemContainerGenerator
            .ContainerFromItem(target) as FrameworkElement;
        var insertAfter = targetContainer is not null &&
            args.GetPosition(targetContainer).Y > targetContainer.ActualHeight / 2;

        try
        {
            await page.ReorderTaskByDropAsync(source, target, insertAfter)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // A cancelled UI operation does not need another surface message.
        }
    }

    private static TodayTaskViewModel? FindTask(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement element &&
                element.DataContext is TodayTaskViewModel task)
            {
                return task;
            }

            current = GetParent(current);
        }

        return null;
    }

    private static TodayPageViewModel? FindPage(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement element &&
                element.DataContext is TodayPageViewModel page)
            {
                return page;
            }

            current = GetParent(current);
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject dependencyObject)
    {
        if (dependencyObject is FrameworkContentElement contentElement)
        {
            return contentElement.Parent;
        }

        return dependencyObject is Visual or Visual3D
            ? VisualTreeHelper.GetParent(dependencyObject)
            : LogicalTreeHelper.GetParent(dependencyObject);
    }

    private sealed class DragState(Point startPoint, TodayTaskViewModel source)
    {
        public Point StartPoint { get; } = startPoint;

        public TodayTaskViewModel? Source { get; set; } = source;
    }
}
