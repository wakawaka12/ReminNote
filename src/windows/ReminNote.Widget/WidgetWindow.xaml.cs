using System.Windows;
using System.Windows.Input;
using ReminNote.Widget.ViewModels;

namespace ReminNote.Widget;

public partial class WidgetWindow : Window
{
    public WidgetWindow(WidgetViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.SetViewportWidth(Width);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is WidgetViewModel viewModel)
        {
            viewModel.SetViewportWidth(e.NewSize.Width);
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
