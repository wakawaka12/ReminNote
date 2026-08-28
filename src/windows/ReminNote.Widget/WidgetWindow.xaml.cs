using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ReminNote.Widget.ViewModels;

namespace ReminNote.Widget;

public partial class WidgetWindow : Window
{
    public WidgetWindow(WidgetViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.SetViewportWidth(Width);
        UpdateWindowClip();
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is WidgetViewModel viewModel)
        {
            viewModel.SetViewportWidth(e.NewSize.Width);
        }

        UpdateWindowClip();
    }

    private void UpdateWindowClip()
    {
        if (WindowSurface.ActualWidth <= 0 || WindowSurface.ActualHeight <= 0)
        {
            return;
        }

        if (TryFindResource("WidgetWindowCornerRadius") is CornerRadius radius)
        {
            WindowSurface.Clip = new RectangleGeometry(
                new Rect(0, 0, WindowSurface.ActualWidth, WindowSurface.ActualHeight),
                radius.TopLeft,
                radius.TopLeft);
        }
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
