using System.Windows;
using ReminNote.Windows.ViewModels;

namespace ReminNote.Windows;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
